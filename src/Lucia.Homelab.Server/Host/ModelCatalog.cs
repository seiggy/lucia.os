using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Host;

[JsonConverter(typeof(JsonStringEnumConverter<ModelKind>))]
public enum ModelKind { Chat, Embedding }

[JsonConverter(typeof(JsonStringEnumConverter<ModelDownloadState>))]
public enum ModelDownloadState { Queued, Downloading, Ready, Failed, Canceled, Interrupted }

/// <summary><c>Gguf</c> runs on TensorSharp. <c>Safetensors</c> is a whole Hugging Face repository for vLLM.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelFormat>))]
public enum ModelFormat { Gguf, Safetensors }

/// <param name="File">The GGUF (first shard) to download; <c>config.json</c> for a safetensors repository.</param>
public sealed record ModelDownloadRequest(
    string Provider,
    string Repository,
    string File,
    ModelKind Kind,
    string Revision = "main",
    bool Pro = false,
    long? SizeBytes = null,
    ModelFormat Format = ModelFormat.Gguf);

public sealed record LocalModel(
    Guid Id,
    ModelDownloadRequest Source,
    ModelDownloadState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Error = null,
    ModelInspection? Inspection = null,
    string? PersistenceError = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? DownloadedBytes = null);

public sealed record ModelSelection(int Version = 1, Guid? ChatId = null, int? ChatContext = null,
    Guid? EmbeddingId = null, int? EmbeddingContext = null);

public sealed class ModelCatalog(
    IOptions<HostPlatformOptions> options,
    IHostEnvironment environment,
    ILogger<ModelCatalog> logger,
    HuggingFaceCredentialService? huggingFaceCredentials = null) : BackgroundService
{
    internal const string LeaseFileName = ".lucia-model-catalog.lease";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private FileStream? _lease;
    private readonly HostPlatformOptions _options = options.Value;
    private readonly ConcurrentDictionary<Guid, LocalModel> _models = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _downloads = new();
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly LinkedList<Guid> _pending = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Channel<bool> _mirror = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });

    private string Root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(_options.ModelDirectory, environment.ContentRootPath));
    private string? Cache => string.IsNullOrWhiteSpace(_options.LlamaCache) ? null
        : Path.TrimEndingDirectorySeparator(Path.GetFullPath(_options.LlamaCache, environment.ContentRootPath));
    private string DirectoryFor(Guid id) => Path.Combine(Root, id.ToString("N"));
    private string FilesFor(Guid id) => Path.Combine(DirectoryFor(id), "files");
    private string ManifestFor(Guid id) => Path.Combine(DirectoryFor(id), "model.json");

    public IReadOnlyList<LocalModel> List() => _models.Values.OrderByDescending(m => m.CreatedAt)
        .Select(m => m.State == ModelDownloadState.Downloading ? m with { DownloadedBytes = OnDisk(m.Id) } : m).ToArray();

    // The Hugging Face CLI writes partial files under files/.cache, so everything under files/ is what has arrived so far.
    private long? OnDisk(Guid id)
    {
        try { return new DirectoryInfo(FilesFor(id)).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }
    public LocalModel? Find(Guid id) => _models.GetValueOrDefault(id);

    public async Task<ModelSelection?> ReadSelectionAsync(CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken);
        try { RequireLease(); return await ReadSelectionFileAsync(cancellationToken); }
        finally { _changes.Release(); }
    }

    private async Task<ModelSelection?> ReadSelectionFileAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(Root, "active-models.json");
        if (!File.Exists(path)) return null;
        RejectLink(path);
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Saved model selection is oversized.");
        var selection = JsonSerializer.Deserialize<ModelSelection>(await File.ReadAllTextAsync(path, cancellationToken), Json)
            ?? throw new InvalidDataException("Saved model selection is empty.");
        if (selection.Version != 1 || selection.ChatId == Guid.Empty || selection.EmbeddingId == Guid.Empty
            || (selection.ChatId is null) != (selection.ChatContext is null)
            || (selection.EmbeddingId is null) != (selection.EmbeddingContext is null)
            || selection.ChatContext is < 256 or > 1048576 || selection.EmbeddingContext is < 1 or > 1048576)
            throw new InvalidDataException("Saved model selection is invalid.");
        return selection;
    }

    public async Task SaveSelectionAsync(ModelKind kind, Guid? id, int? contextTokens)
    {
        if (!Enum.IsDefined(kind) || id == Guid.Empty || (id is null) != (contextTokens is null)
            || (contextTokens is { } count && (count < (kind == ModelKind.Chat ? 256 : 1) || count > 1048576)))
            throw new ArgumentException("The model startup selection is invalid.");
        await _changes.WaitAsync();
        try
        {
            RequireLease();
            var selection = await ReadSelectionFileAsync(CancellationToken.None) ?? new ModelSelection();
            selection = kind switch
            {
                ModelKind.Chat => selection with { ChatId = id, ChatContext = contextTokens },
                ModelKind.Embedding => selection with { EmbeddingId = id, EmbeddingContext = contextTokens },
                _ => throw new ArgumentException("Unknown model kind.")
            };
            await WriteSelectionFileAsync(selection);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogError(error, "The loaded-model startup selection could not be saved.");
            throw new InvalidOperationException("The runtime changed, but its startup selection could not be saved. Check host storage before restarting.", error);
        }
        finally { _changes.Release(); }
    }

    private async Task WriteSelectionFileAsync(ModelSelection selection)
    {
        var path = Path.Combine(Root, "active-models.json");
        var temporary = Path.Combine(Root, ".active-models-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (File.Exists(path)) RejectLink(path);
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, fileOptions))
            {
                await JsonSerializer.SerializeAsync(stream, selection, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public static string? Validate(ModelDownloadRequest request)
    {
        if (request.Provider != "huggingface")
            return "Only the 'huggingface' model provider is currently supported.";
        if (string.IsNullOrWhiteSpace(request.Repository) || request.Repository.Length > 200
            || !Regex.IsMatch(request.Repository, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]*/[a-zA-Z0-9][a-zA-Z0-9_.-]*\z"))
            return "Repository must be a Hugging Face owner/model identifier.";
        if (string.IsNullOrWhiteSpace(request.Revision) || request.Revision.Length > 200
            || request.Revision.StartsWith('-') || request.Revision.Any(char.IsControl))
            return "Revision must be a nonempty branch, tag, or commit.";
        if (!Enum.IsDefined(request.Kind))
            return "Kind must be Chat or Embedding.";
        if (request.SizeBytes is < 1 or > 1L << 40)
            return "SizeBytes must be the download's size, up to 1 TiB.";
        if (request.Format == ModelFormat.Safetensors)
            return request.File == "config.json" && request.Kind == ModelKind.Chat ? null
                : "A safetensors download is a whole LLM repository: set file to config.json and kind to Chat.";
        if (!Enum.IsDefined(request.Format))
            return "Format must be Gguf or Safetensors.";
        if (!IsModelFile(request.File))
            return "Model files must be repository-relative .gguf paths without traversal or option prefixes.";
        var shard = Regex.Match(request.File, @"-(\d{5})-of-(\d{5})\.gguf$", RegexOptions.IgnoreCase);
        if (shard.Success && (shard.Groups[1].Value != "00001" || !int.TryParse(shard.Groups[2].Value, out var count) || count is < 1 or > 128))
            return "Select the first shard of a split GGUF (up to 128 shards).";
        return null;
    }

    private static bool IsModelFile(string? file) =>
        !string.IsNullOrWhiteSpace(file) && file.Length <= 400
        && file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
        && !file.Any(c => char.IsControl(c) || "<>:\"\\|?*".Contains(c))
        && file.Split('/').All(part => part.Length > 0 && part is not "." and not ".."
            && !part.StartsWith('-') && !part.EndsWith('.') && !part.EndsWith(' '));

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_lease is not null)
            throw new InvalidOperationException("This model catalog already holds its directory lease.");
        Directory.CreateDirectory(Root);
        RejectLink(Root);
        _lease = AcquireLease();
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id))
                continue;
            RejectLink(directory);
            var manifest = ManifestFor(id);
            if (!File.Exists(manifest))
                throw new InvalidDataException($"Model directory {id} has no manifest; preserve its files and repair the catalog.");
            var model = JsonSerializer.Deserialize<LocalModel>(await File.ReadAllTextAsync(manifest, cancellationToken), Json)
                ?? throw new InvalidDataException($"Model manifest {id} is empty.");
            if (model.Id != id || model.Source is null || Validate(model.Source) is not null || !Enum.IsDefined(model.State))
                throw new InvalidDataException($"Model manifest {id} is invalid.");
            if (model.State is ModelDownloadState.Queued or ModelDownloadState.Downloading)
            {
                model = model with { State = ModelDownloadState.Interrupted, Error = "The host stopped during this download. Retry to resume.", UpdatedAt = DateTimeOffset.UtcNow };
                model = await SaveRecoveryAsync(model, cancellationToken);
                logger.LogWarning("Model download {ModelId} was interrupted; partial files were retained", id);
            }
            else if (model.State == ModelDownloadState.Ready)
            {
                try
                {
                    var anchor = ExistingFile(id, model.Source.File);
                    model = model with { Inspection = model.Source.Format == ModelFormat.Gguf ? ModelInspector.Inspect(anchor, model.Source.Kind, options.Value.RequireTensorSharp) : null };
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
                {
                    logger.LogError(exception, "Installed model {ModelId} failed format validation", id);
                    model = model with { State = ModelDownloadState.Failed, Error = exception.Message, Inspection = null };
                }
                model = await SaveRecoveryAsync(model, cancellationToken);
            }
            _models[id] = model;
        }
        MirrorLlamaCache();
        await base.StartAsync(cancellationToken);
    }

    internal const string LlamaPresetsFileName = "llama-models.ini";
    private const string MirroredMarker = ".mirrored";

    private static bool Llama(LocalModel model) => model is { State: ModelDownloadState.Ready, Source.Format: ModelFormat.Gguf };

    // llama.cpp embeds at most one batch per input, so embedding models get a batch as long as their context.
    private const int LlamaEmbeddingContext = 8192;

    /// <summary>
    /// The llama.cpp router's preset file: every Ready GGUF, named <c>owner/repo:QUANT</c> as llama.cpp names them, with
    /// embedding models served as embeddings and the named ones loaded when the router starts.
    /// </summary>
    internal static string LlamaPresets(string root, IEnumerable<LocalModel> models, IReadOnlySet<string>? loadOnStartup = null)
    {
        var ini = new StringBuilder("version = 1\n");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models.Where(Llama).OrderBy(m => m.CreatedAt))
        {
            // INI has no quoting, so only plain paths are listed.
            if (!Regex.IsMatch(model.Source.File, @"\A[A-Za-z0-9._/-]+\z")) continue;
            var name = $"{model.Source.Repository}:{LlamaTag(model.Source.File)}";
            if (!names.Add(name)) continue;
            ini.Append($"\n[{name}]\nmodel = {root}/{model.Id:N}/files/{model.Source.File}\n");
            if (model.Source.Kind == ModelKind.Embedding || model.Inspection?.Kind == ModelKind.Embedding)
            {
                var context = Math.Min(LlamaEmbeddingContext, model.Inspection?.NativeContextTokens is > 0 and var native ? native : LlamaEmbeddingContext);
                ini.Append($"embeddings = true\nc = {context}\nbatch-size = {context}\nubatch-size = {context}\n");
            }
            if (loadOnStartup?.Contains(name) == true) ini.Append("load-on-startup = true\n");
        }
        return ini.ToString();
    }

    private static readonly Regex Quantization = new(@"(?:\A|[._/-])((?:IQ|Q|TQ)[1-8](?:_[A-Z0-9]+)*|BF16|F16|F32)(?=[._/-]|\z)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string? QuantizationOf(string file) =>
        Quantization.Match(file) is { Success: true } match ? match.Groups[1].Value.ToUpperInvariant() : null;

    private static string LlamaTag(string file) => QuantizationOf(file)
        ?? Regex.Replace(Path.GetFileNameWithoutExtension(file), @"-\d{5}-of-\d{5}\z", "");

    private void MirrorLlamaCache()
    {
        if (Cache is not null) _mirror.Writer.TryWrite(true);
    }

    /// <summary>
    /// Keeps the cache equal to the library's Ready GGUFs: copies new ones, drops removed ones, and lists a model in
    /// the preset file only once every file of it is on local disk.
    /// </summary>
    private async Task MirrorAsync(string cache, CancellationToken stoppingToken)
    {
        await foreach (var signal in _mirror.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                Directory.CreateDirectory(cache);
                var wanted = _models.Values.Where(Llama).ToDictionary(m => m.Id.ToString("N"));
                foreach (var directory in Directory.EnumerateDirectories(cache))
                    if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _) && !wanted.ContainsKey(Path.GetFileName(directory)))
                        Directory.Delete(directory, recursive: true);
                WriteLlamaPresets(cache);
                foreach (var model in wanted.Values.OrderBy(m => m.CreatedAt))
                {
                    var target = Path.Combine(cache, model.Id.ToString("N"));
                    if (File.Exists(Path.Combine(target, MirroredMarker))) continue;
                    logger.LogInformation("Copying model {ModelId} to local disk for llama.cpp", model.Id);
                    foreach (var file in ModelFiles(model.Source.File))
                    {
                        var source = Path.Combine(DirectoryFor(model.Id), "files", file);
                        var destination = Path.Combine(target, "files", file);
                        if (File.Exists(destination) && new FileInfo(destination).Length == new FileInfo(source).Length) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                        await using (var output = new FileStream(destination + ".part", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                            await input.CopyToAsync(output, 16 << 20, stoppingToken);
                        File.Move(destination + ".part", destination, overwrite: true);
                    }
                    await File.WriteAllTextAsync(Path.Combine(target, MirroredMarker), "", stoppingToken);
                    logger.LogInformation("Model {ModelId} is on local disk", model.Id);
                    WriteLlamaPresets(cache);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A full disk or a model deleted mid-copy; the next library change retries.
                logger.LogError(exception, "Could not copy the model library to local disk for llama.cpp");
            }
        }
    }

    private void WriteLlamaPresets(string cache)
    {
        var path = Path.Combine(cache, LlamaPresetsFileName);
        File.WriteAllText(path + ".tmp", LlamaPresets(cache,
            _models.Values.Where(m => File.Exists(Path.Combine(cache, m.Id.ToString("N"), MirroredMarker))),
            (_options.LlamaLoadOnStartup ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private FileStream AcquireLease()
    {
        var path = Path.Combine(Root, LeaseFileName);
        try
        {
            RejectLeaseLink(path);
            var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                RejectLeaseLink(path);
                // Unix FileShare.None uses advisory flock; .NET can silently ignore unsupported/disabled locking.
                try
                {
                    using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException exception) when (IsLeaseContention(exception))
                {
                    return lease;
                }
                throw new InvalidOperationException(
                    $"Model catalog lease '{path}' is not exclusive. Use a filesystem supporting file locks and ensure System.IO.DisableFileLocking / DOTNET_SYSTEM_IO_DISABLEFILELOCKING is not enabled.");
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        catch (IOException exception) when (IsLeaseContention(exception))
        {
            throw new IOException(
                $"Another host holds the model catalog lease '{path}'. Stop and dispose that host before starting this instance. Do not delete or replace the lease file.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"Cannot acquire model catalog lease '{path}'. Check directory permissions and filesystem locking support; do not delete model data or an active lease file.", exception);
        }
    }

    private static bool IsLeaseContention(IOException exception) =>
        OperatingSystem.IsWindows() ? (exception.HResult & 0xffff) == 32
            : exception.HResult == (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? 35 : 11);

    private static void RejectLeaseLink(string path)
    {
        var file = new FileInfo(path);
        if (file.LinkTarget is not null || (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException($"Model catalog lease '{path}' must not be a symbolic link or reparse point. Stop the host and repair this path before retrying.");
    }

    private void RequireLease()
    {
        if (_lease is null)
            throw new InvalidOperationException("Start the model catalog and acquire its directory lease before accessing model files.");
    }

    public override void Dispose()
    {
        base.Dispose();
        _lease?.Dispose();
        _lease = null;
    }

    public async Task<LocalModel> DownloadAsync(ModelDownloadRequest request, CancellationToken cancellationToken)
    {
        var error = Validate(request);
        if (error is not null)
            throw new ArgumentException(error, nameof(request));
        if (!request.Pro && ModelPresets.Match(request) is null)
            throw new ArgumentException("Choose a catalog model, or explicitly set pro=true for an unvalidated Hugging Face model.", nameof(request));
        // The size is only for showing progress; it doesn't make a download different.
        request = request with { SizeBytes = ModelPresets.Match(request)?.SizeBytes ?? request.SizeBytes };
        await _changes.WaitAsync(cancellationToken);
        try
        {
            var existing = _models.Values.FirstOrDefault(m => m.Source with { SizeBytes = null } == request with { SizeBytes = null }
                && m.State is ModelDownloadState.Queued or ModelDownloadState.Downloading or ModelDownloadState.Ready);
            if (existing is not null)
                return existing;
            var now = DateTimeOffset.UtcNow;
            var model = new LocalModel(Guid.NewGuid(), request, ModelDownloadState.Queued, now, now);
            await EnqueueAsync(model, cancellationToken);
            return model;
        }
        finally { _changes.Release(); }
    }

    public async Task<LocalModel> RetryAsync(Guid id, CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken);
        try
        {
            var model = Require(id);
            if (_downloads.ContainsKey(id))
                throw new InvalidOperationException("Wait for the previous download to finish stopping before retrying.");
            if (model.State is not (ModelDownloadState.Failed or ModelDownloadState.Canceled or ModelDownloadState.Interrupted))
                throw new InvalidOperationException("Only failed, canceled, or interrupted downloads can be retried.");
            model = model with { State = ModelDownloadState.Queued, Error = null, Inspection = null, UpdatedAt = DateTimeOffset.UtcNow };
            await EnqueueAsync(model, cancellationToken);
            return model;
        }
        finally { _changes.Release(); }
    }

    private async Task EnqueueAsync(LocalModel model, CancellationToken cancellationToken)
    {
        if (_pending.Count >= 16)
            throw new InvalidOperationException("The download queue is full. Wait for an existing download to finish.");
        await SaveAsync(model, cancellationToken);
        _models[model.Id] = model;
        var source = new CancellationTokenSource();
        _downloads[model.Id] = source;
        _pending.AddLast(model.Id);
        _wake.Writer.TryWrite(true);
    }

    public async Task CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken);
        try
        {
            var model = Require(id);
            if (!_downloads.TryGetValue(id, out var source))
                throw new InvalidOperationException("This model has no pending download.");
            await source.CancelAsync();
            if (model.State == ModelDownloadState.Queued)
            {
                _pending.Remove(id);
                _downloads.TryRemove(id, out _);
                source.Dispose();
                await SetStateAsync(id, ModelDownloadState.Canceled, "Queued download canceled.");
            }
        }
        finally { _changes.Release(); }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        RequireLease();
        await _changes.WaitAsync(cancellationToken);
        try
        {
            Require(id);
            if (_downloads.ContainsKey(id))
                throw new InvalidOperationException("Cancel the download and wait for it to stop before deleting the model.");
            if (await ReadSelectionFileAsync(cancellationToken) is { } selection
                && (selection.ChatId == id || selection.EmbeddingId == id))
                await WriteSelectionFileAsync(selection with
                {
                    ChatId = selection.ChatId == id ? null : selection.ChatId,
                    ChatContext = selection.ChatId == id ? null : selection.ChatContext,
                    EmbeddingId = selection.EmbeddingId == id ? null : selection.EmbeddingId,
                    EmbeddingContext = selection.EmbeddingId == id ? null : selection.EmbeddingContext
                });
            RejectLink(Root);
            var directory = DirectoryFor(id);
            RejectLink(directory);
            Directory.Delete(directory, recursive: true);
            _models.TryRemove(id, out _);
            MirrorLlamaCache();
            logger.LogInformation("Deleted local model {ModelId}", id);
        }
        finally { _changes.Release(); }
    }

    public string ModelPath(Guid id)
    {
        var model = Require(id);
        if (model.State != ModelDownloadState.Ready)
            throw new InvalidOperationException("The model download is not ready.");
        if (model.Source.Format != ModelFormat.Gguf)
            throw new InvalidOperationException("This model is a safetensors repository. Serve it with vLLM instead.");
        return ExistingFile(id, model.Source.File);
    }

    private string ExistingFile(Guid id, string file)
    {
        RequireLease();
        RejectLink(Root);
        var root = FilesFor(id);
        var path = Path.GetFullPath(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Model path is outside its managed directory.");
        for (var current = path; current != Root; current = Path.GetDirectoryName(current)
            ?? throw new InvalidDataException("Invalid model directory."))
            RejectLink(current);
        if (new FileInfo(path).Length == 0)
            throw new InvalidDataException("The downloaded model file is empty.");
        return path;
    }

    private LocalModel Require(Guid id) => Find(id) ?? throw new KeyNotFoundException("Model not found.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mirror = Cache is { } cache ? Task.Run(() => MirrorAsync(cache, stoppingToken), stoppingToken) : Task.CompletedTask;
        // ponytail: one model download at a time; add parallel workers when download throughput requires it.
        await foreach (var signal in _wake.Reader.ReadAllAsync(stoppingToken))
        {
            while (true)
            {
                Guid id;
                CancellationTokenSource source;
                await _changes.WaitAsync(stoppingToken);
                try
                {
                    if (_pending.First is null)
                        break;
                    id = _pending.First.Value;
                    _pending.RemoveFirst();
                    source = _downloads[id];
                    if (!await SetStateAsync(id, ModelDownloadState.Downloading))
                    {
                        _downloads.TryRemove(id, out _);
                        source.Dispose();
                        continue;
                    }
                }
                finally { _changes.Release(); }
                await ProcessDownloadAsync(id, source, stoppingToken);
            }
        }
    }

    private async Task ProcessDownloadAsync(Guid id, CancellationTokenSource source, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(source.Token, stoppingToken);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            await RunDownloadAsync(Require(id), linked.Token);
            if (Require(id).Source.Format == ModelFormat.Safetensors)
            {
                ExistingFile(id, "config.json");
                if (!Directory.EnumerateFiles(FilesFor(id), "*.safetensors", SearchOption.AllDirectories).Any())
                    throw new InvalidDataException("The repository has no .safetensors weights at this revision.");
                if (await SetStateAsync(id, ModelDownloadState.Ready))
                    logger.LogInformation("Model download {ModelId} is ready", id);
                return;
            }
            foreach (var file in ModelFiles(Require(id).Source.File))
                ExistingFile(id, file);
            var path = ExistingFile(id, Require(id).Source.File);
            if (ModelPresets.Match(Require(id).Source) is { } preset)
            {
                await using var file = File.OpenRead(path);
                if (file.Length != preset.SizeBytes
                    || !Convert.ToHexString(await SHA256.HashDataAsync(file, linked.Token)).Equals(preset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded model does not match the catalog size and SHA-256.");
            }
            var inspection = ModelInspector.Inspect(path, Require(id).Source.Kind, options.Value.RequireTensorSharp);
            if (await SetStateAsync(id, ModelDownloadState.Ready, inspection: inspection))
                logger.LogInformation("Model download {ModelId} is ready", id);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            await SetStateAsync(id, stoppingToken.IsCancellationRequested ? ModelDownloadState.Interrupted : ModelDownloadState.Canceled,
                "Download stopped. Partial files were retained for retry.");
            logger.LogInformation("Model download {ModelId} stopped", id);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception
            or InvalidOperationException or NotSupportedException or ArgumentException or OverflowException)
        {
            logger.LogError(exception, "Model download {ModelId} failed", id);
            await SetStateAsync(id, ModelDownloadState.Failed, exception.Message);
        }
        finally
        {
            await _changes.WaitAsync(CancellationToken.None);
            try
            {
                _downloads.TryRemove(id, out _);
                source.Dispose();
            }
            finally { _changes.Release(); }
        }
    }

    private async Task RunDownloadAsync(LocalModel model, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(FilesFor(model.Id));
        var start = new ProcessStartInfo(_options.HuggingFaceExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var files = model.Source.Format == ModelFormat.Safetensors
            ? SnapshotFiles.SelectMany(pattern => new[] { "--include", pattern }) : ModelFiles(model.Source.File);
        foreach (var argument in new[] { "download", model.Source.Repository }.Concat(files))
            start.ArgumentList.Add(argument);
        foreach (var argument in new[] { "--revision", model.Source.Revision, "--local-dir", FilesFor(model.Id), "--quiet" })
            start.ArgumentList.Add(argument);
        start.Environment["HF_HOME"] = _options.HuggingFaceHomeDirectory is { Length: > 0 } home
            ? Path.GetFullPath(home, environment.ContentRootPath) : Path.Combine(Root, ".huggingface");
        start.Environment["HF_ENDPOINT"] = "https://huggingface.co";
        start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        start.Environment["HF_HUB_DISABLE_UPDATE_CHECK"] = "1";
        start.Environment["HF_HUB_DISABLE_PROGRESS_BARS"] = "1";
        start.Environment["HF_HUB_VERBOSITY"] = "error";
        start.Environment["HF_DEBUG"] = "0";
        var access = huggingFaceCredentials is null
            ? new HuggingFaceDownloadCredentials(string.IsNullOrWhiteSpace(_options.HuggingFaceToken) ? null : _options.HuggingFaceToken)
            : await huggingFaceCredentials.GetDownloadCredentialsAsync(cancellationToken);
        start.Environment.Remove("HF_TOKEN");
        start.Environment.Remove("HUGGING_FACE_HUB_TOKEN");
        start.Environment["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = access.DisableImplicitCredentials ? "1" : "0";
        if (access.Token is { } activeToken)
            start.Environment["HF_TOKEN"] = activeToken;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Hugging Face CLI.");
        var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
        start.Environment.TryGetValue("HF_TOKEN", out var token);
        var errors = ReadErrorTailAsync(process.StandardError, token);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            await Task.WhenAll(output, errors);
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Hugging Face download exited with code {process.ExitCode}. Check repository, revision, filenames, token access, and free disk space. {await errors}");
    }

    private static async Task<string> ReadErrorTailAsync(StreamReader reader, string? token)
    {
        var tail = new StringBuilder();
        var buffer = new char[512];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > 4096)
                tail.Remove(0, tail.Length - 4096);
        }
        var text = tail.ToString();
        if (!string.IsNullOrEmpty(token))
            text = text.Replace(token, "[redacted]", StringComparison.Ordinal);
        text = Regex.Replace(text, @"hf_[A-Za-z0-9_-]+", "[redacted]");
        text = Regex.Replace(text, @"https?://\S+", "[remote URL]");
        return Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", "").Trim();
    }

    /// <summary>
    /// What vLLM needs from a repository: weights, configs and tokenizer files. Code (<c>*.py</c>) is left out because
    /// Lucia never runs vLLM with trust_remote_code, and other weight formats would only double the download.
    /// </summary>
    public static readonly string[] SnapshotFiles = ["*.safetensors", "*.json", "*.model", "*.txt", "*.jinja", "*.tiktoken"];

    public static IReadOnlyList<string> ModelFiles(string file)
    {
        var shard = Regex.Match(file, @"-00001-of-(\d{5})\.gguf$", RegexOptions.IgnoreCase);
        if (!shard.Success)
            return [file];
        var count = int.Parse(shard.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (count is < 1 or > 128)
            throw new ArgumentException("Invalid GGUF shard count.", nameof(file));
        return Enumerable.Range(1, count).Select(index => file[..shard.Index] + $"-{index:D5}-of-{count:D5}.gguf").ToArray();
    }

    private async Task<bool> SetStateAsync(Guid id, ModelDownloadState state, string? error = null, ModelInspection? inspection = null)
    {
        var model = Require(id) with
        {
            State = state,
            Error = error,
            Inspection = inspection,
            PersistenceError = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        model = await SaveRecoveryAsync(model, CancellationToken.None);
        _models[id] = model.PersistenceError is null || state is ModelDownloadState.Canceled or ModelDownloadState.Interrupted
            ? model : model with { State = ModelDownloadState.Failed, Error = error ?? "Model state could not be saved. Free disk space or repair storage permissions, then retry." };
        MirrorLlamaCache();
        return model.PersistenceError is null;
    }

    private async Task<LocalModel> SaveRecoveryAsync(LocalModel model, CancellationToken cancellationToken)
    {
        try
        {
            model = model with { PersistenceError = null };
            await SaveAsync(model, cancellationToken);
            return model;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Model {ModelId} has an unpersisted state; keeping the owner API available for storage recovery", model.Id);
            return model with { PersistenceError = exception.Message };
        }
    }

    private async Task SaveAsync(LocalModel model, CancellationToken cancellationToken)
    {
        RequireLease();
        Directory.CreateDirectory(DirectoryFor(model.Id));
        var path = ManifestFor(model.Id);
        try
        {
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(model, Json), cancellationToken);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            logger.LogError(exception, "Could not persist model manifest {ModelId}", model.Id);
            File.Delete(path + ".tmp");
            if (!File.Exists(path) && !Directory.EnumerateFileSystemEntries(DirectoryFor(model.Id)).Any())
                Directory.Delete(DirectoryFor(model.Id));
            throw;
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Symbolic links are not allowed in the managed model directory.");
    }
}
