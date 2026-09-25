using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Http.Features;

namespace Lucia.Homelab.Server.Host;

public sealed class InferenceKeyOptions
{
    public string Directory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lucia", "inference-keys");
}

public sealed record InferenceKeyInfo(Guid Id, string Name, string Hint, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);
public sealed record CreateInferenceKeyRequest(string Name, DateTimeOffset? ExpiresAt = null);
public sealed record CreatedInferenceKey(InferenceKeyInfo Key, string Secret);

public sealed class InferenceApiKeyStore(IOptions<InferenceKeyOptions> options, TimeProvider clock,
    ILogger<InferenceApiKeyStore> logger) : IHostedService, IDisposable
{
    public const string Prefix = "lucia_inf_";
    private const int MaximumKeys = 1000;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false
    };
    private sealed record StoredKey(InferenceKeyInfo Info, string Hash, string CreatedBy);
    private sealed record Document(int Version, StoredKey[] Keys);
    private readonly SemaphoreSlim _changes = new(1, 1);
    private StoredKey[] _keys = [];
    private FileStream? _lease;
    private volatile bool _ready;
    private volatile bool _faulted;
    private string Root => Path.GetFullPath(options.Value.Directory);
    private string FilePath => Path.Combine(Root, "keys.json");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(options.Value.Directory) || Root == Path.GetPathRoot(Root))
            throw new InvalidOperationException("InferenceKeys:Directory must be an absolute dedicated private directory.");
        RejectLinks(Root);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Root);
        else
        {
            Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            RequirePrivate(Root);
        }
        var leasePath = Path.Combine(Root, ".keys.lease");
        RejectLinks(leasePath);
        var fileOptions = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        _lease = new FileStream(leasePath, fileOptions);
        try
        {
            try
            {
                using var probe = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                throw new InvalidOperationException("Inference-key storage does not provide exclusive file locking.");
            }
            catch (IOException) { }
            RequirePrivate(leasePath);
            if (_lease.Length != 0)
            {
                if (_lease.Length != "Lucia.InferenceKeys.v1\n"u8.Length)
                    throw new InvalidDataException("The inference-key store ownership marker is invalid.");
                var marker = new byte["Lucia.InferenceKeys.v1\n"u8.Length];
                _lease.ReadExactly(marker);
                if (!marker.AsSpan().SequenceEqual("Lucia.InferenceKeys.v1\n"u8))
                    throw new InvalidDataException("The inference-key store ownership marker is invalid.");
            }
            RejectLinks(FilePath);
            if (File.Exists(FilePath))
            {
                if (_lease.Length == 0) throw new InvalidDataException("Inference-key data exists without its ownership marker.");
                RequirePrivate(FilePath);
                if (new FileInfo(FilePath).Length > 1024 * 1024) throw new InvalidDataException("The inference-key store is oversized.");
                var document = JsonSerializer.Deserialize<Document>(await File.ReadAllBytesAsync(FilePath, cancellationToken), Json)
                    ?? throw new InvalidDataException("The inference-key store is empty.");
                if (document.Version != 1 || document.Keys.Length > MaximumKeys
                    || document.Keys.Select(item => item.Info.Id).Distinct().Count() != document.Keys.Length
                    || document.Keys.Select(item => item.Hash).Distinct().Count() != document.Keys.Length)
                    throw new InvalidDataException("The inference-key store is invalid.");
                foreach (var item in document.Keys)
                {
                    ValidateName(item.Info.Name);
                    if (item.Info.Id == Guid.Empty || item.Hash.Length != 64 || !item.Hash.All(char.IsAsciiHexDigit)
                        || !item.Info.Hint.StartsWith(Prefix, StringComparison.Ordinal) || item.Info.Hint.Length > 40
                        || string.IsNullOrWhiteSpace(item.CreatedBy) || item.CreatedBy.Length > 512
                        || item.Info.CreatedAt > clock.GetUtcNow().AddMinutes(1)
                        || item.Info.ExpiresAt <= item.Info.CreatedAt)
                        throw new InvalidDataException("The inference-key store contains an invalid record.");
                }
                _keys = document.Keys;
            }
            else
            {
                if (_lease.Length != 0) throw new InvalidDataException("The initialized inference-key store is missing. Restore it instead of resetting keys.");
                _lease.Write("Lucia.InferenceKeys.v1\n"u8);
                _lease.Flush(flushToDisk: true);
                await Save([], cancellationToken);
            }
            _ready = true;
        }
        catch
        {
            _lease.Dispose();
            _lease = null;
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public InferenceKeyInfo[] List()
    {
        RequireReady();
        return Volatile.Read(ref _keys).Select(item => item.Info).OrderByDescending(item => item.CreatedAt).ToArray();
    }

    public bool Authenticates(string token)
        => Match(token) is not null;

    public Guid? Match(string token)
    {
        RequireReady();
        if (token.Length != Prefix.Length + 43 || !token.StartsWith(Prefix, StringComparison.Ordinal)
            || token[Prefix.Length..].Any(value => !char.IsAsciiLetterOrDigit(value) && value is not '-' and not '_'))
            return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var now = clock.GetUtcNow();
        return Volatile.Read(ref _keys).FirstOrDefault(item =>
            (item.Info.ExpiresAt is null || item.Info.ExpiresAt > now)
            && CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(item.Hash)))?.Info.Id;
    }

    public async Task<CreatedInferenceKey> Create(CreateInferenceKeyRequest request, string actor, CancellationToken cancellationToken)
    {
        ValidateName(request.Name);
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 512) throw new ArgumentException("A stable owner identity is required.");
        if (request.ExpiresAt <= clock.GetUtcNow()) throw new ArgumentException("Choose a future expiry date or leave it unset.");
        await _changes.WaitAsync(cancellationToken);
        try
        {
            RequireReady();
            if (_keys.Length >= MaximumKeys) throw new InvalidOperationException("Revoke unused keys before creating another.");
            var createdAt = clock.GetUtcNow();
            if (request.ExpiresAt <= createdAt) throw new ArgumentException("Choose a future expiry date or leave it unset.");
            var secret = Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var info = new InferenceKeyInfo(Guid.NewGuid(), request.Name.Trim(),
                secret[..(Prefix.Length + 6)] + "..." + secret[^4..], createdAt, request.ExpiresAt?.ToUniversalTime());
            var next = _keys.Append(new StoredKey(info, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))), actor)).ToArray();
            await Save(next, cancellationToken);
            Volatile.Write(ref _keys, next);
            logger.LogInformation("Created inference-only API key {KeyId}.", info.Id);
            return new(info, secret);
        }
        finally { _changes.Release(); }
    }

    public async Task Revoke(Guid id, CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken);
        try
        {
            RequireReady();
            if (!_keys.Any(item => item.Info.Id == id)) throw new KeyNotFoundException("API key not found.");
            var next = _keys.Where(item => item.Info.Id != id).ToArray();
            await Save(next, cancellationToken);
            Volatile.Write(ref _keys, next);
            logger.LogInformation("Revoked inference API key {KeyId}.", id);
        }
        finally { _changes.Release(); }
    }

    private async Task Save(StoredKey[] keys, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(Root, $".keys-{Guid.NewGuid():N}.tmp");
        try
        {
            RejectLinks(Root);
            RejectLinks(FilePath);
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, fileOptions))
            {
                await JsonSerializer.SerializeAsync(file, new Document(1, keys), Json, cancellationToken);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _faulted = true;
            logger.LogError("Inference-key persistence failed ({ErrorType}); managed-key access is closed until recovery.", error.GetType().Name);
            throw;
        }
        finally { File.Delete(temporary); }
    }

    private void RequireReady()
    {
        if (!_ready || _faulted) throw new InvalidOperationException("The inference-key store is unavailable. Inspect host logs before retrying.");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl))
            throw new ArgumentException("Give the key a name of 1 to 80 characters.");
    }

    private static void RequirePrivate(string path)
    {
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) &
            (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
             UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException("Inference-key storage must be private to the host account.");
    }

    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if (new FileInfo(current).LinkTarget is not null
                || ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)))
                throw new IOException("Inference-key storage must not use symbolic links or junctions.");
    }

    public void Dispose()
    {
        _ready = false;
        _lease?.Dispose();
        _changes.Dispose();
    }
}

public static class InferenceKeyManagement
{
    public static void AddInferenceKeyManagement(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<InferenceKeyOptions>().BindConfiguration("InferenceKeys");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<InferenceApiKeyStore>();
        builder.Services.AddHostedService(services => services.GetRequiredService<InferenceApiKeyStore>());
    }

    public static void MapInferenceKeyManagement(this WebApplication app)
    {
        var keys = app.MapGroup("/api/host/inference-keys").RequireAuthorization("HostOwner")
            .WithTags("Inference API keys").AddEndpointFilter<HostErrorFilter>()
            .AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                return await next(context);
            });
        keys.MapGet("/", (InferenceApiKeyStore store) => store.List());
        keys.MapPost("/", async (HttpContext context, InferenceApiKeyStore store, CancellationToken ct) =>
        {
            if (!context.Request.HasJsonContentType() || context.Request.ContentLength > 4096)
                throw new ArgumentException("Supply a JSON key name and optional expiry, up to 4096 bytes.");
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
                feature.MaxRequestBodySize = 4096;
            else throw new InvalidOperationException("Request size limits are unavailable.");
            var request = await context.Request.ReadFromJsonAsync<CreateInferenceKeyRequest>(InferenceApiKeyStore.Json, ct)
                ?? throw new ArgumentException("A key name and optional expiry are required.");
            var actor = context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
            return Results.Json(await store.Create(request, actor, ct));
        });
        keys.MapDelete("/{id:guid}", async (Guid id, InferenceApiKeyStore store, CancellationToken ct) =>
        {
            await store.Revoke(id, ct);
            return Results.NoContent();
        });
    }
}
