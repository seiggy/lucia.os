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

public sealed record ModelDownloadRequest(
    string Provider,
    string Repository,
    string File,
    ModelKind Kind,
    string Revision = "main",
    bool Pro = false);

public sealed record LocalModel(
    Guid Id,
    ModelDownloadRequest Source,
    ModelDownloadState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Error = null,
    ModelInspection? Inspection = null,
    string? PersistenceError = null);

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

    private string Root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(_options.ModelDirectory, environment.ContentRootPath));
    private string DirectoryFor(Guid id) => Path.Combine(Root, id.ToString("N"));
    private string FilesFor(Guid id) => Path.Combine(DirectoryFor(id), "files");
    private string ManifestFor(Guid id) => Path.Combine(DirectoryFor(id), "model.json");

    public IReadOnlyList<LocalModel> List() => _models.Values.OrderByDescending(m => m.CreatedAt).ToArray();
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
                    var inspection = ModelInspector.Inspect(ExistingFile(id, model.Source.File), model.Source.Kind);
                    model = model with { Inspection = inspection };
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
        await base.StartAsync(cancellationToken);
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
        await _changes.WaitAsync(cancellationToken);
        try
        {
            var existing = _models.Values.FirstOrDefault(m => m.Source == request
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
            logger.LogInformation("Deleted local model {ModelId}", id);
        }
        finally { _changes.Release(); }
    }

    public string ModelPath(Guid id)
    {
        var model = Require(id);
        if (model.State != ModelDownloadState.Ready)
            throw new InvalidOperationException("The model download is not ready.");
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
            var inspection = ModelInspector.Inspect(path, Require(id).Source.Kind);
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
        foreach (var argument in new[] { "download", model.Source.Repository }.Concat(ModelFiles(model.Source.File)))
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
