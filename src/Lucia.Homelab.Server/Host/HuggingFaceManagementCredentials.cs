using System.Net;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Host;

public sealed class HuggingFaceManagementOptions
{
    public string CredentialsDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lucia", "provider-credentials");
}

public sealed record HuggingFaceCredentialStatus(
    bool Configured, string? AccountName, string Source, DateTimeOffset? ValidatedAt);

// Intentionally not a record: neither serialization nor ToString may reveal the token.
public sealed class HuggingFaceDownloadCredentials
{
    internal HuggingFaceDownloadCredentials(string? token) => Token = token;
    [JsonIgnore] public string? Token { get; }
    public bool DisableImplicitCredentials => Token is null;
    public override string ToString() => nameof(HuggingFaceDownloadCredentials);
}

public sealed class HuggingFaceManagementException(int statusCode, string code, string message, int? retryAfterSeconds = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

public sealed class HuggingFaceCredentialService
{
    private const int MaximumRecordBytes = 16 * 1024;
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileModeBits = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _directory;
    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly HostPlatformOptions _legacy;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HuggingFaceCredentialService(IOptions<HuggingFaceManagementOptions> options,
        IOptions<HostPlatformOptions> legacy, IDataProtectionProvider protection, HttpClient http)
    {
        _directory = Path.GetFullPath(options.Value.CredentialsDirectory);
        _path = Path.Combine(_directory, "huggingface.json");
        _protector = protection.CreateProtector("Lucia.Homelab.HuggingFaceCredentials.v1");
        _legacy = legacy.Value;
        _http = http;
    }

    public async Task<HuggingFaceCredentialStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var record = await ReadAsync(cancellationToken);
            if (record is not null) return Status(record);
            return new(LegacyToken() is not null, null, LegacyToken() is null ? "none" : "legacy", null);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// For the download subprocess only. Set HF_TOKEN from Token; otherwise remove HF_TOKEN and
    /// HUGGING_FACE_HUB_TOKEN. Always remove the latter alias. Set HF_HUB_DISABLE_IMPLICIT_TOKEN
    /// to "1" when DisableImplicitCredentials is true, otherwise "0". Never pass Token as an argument.
    /// </summary>
    public async Task<HuggingFaceDownloadCredentials> GetDownloadCredentialsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var record = await ReadAsync(cancellationToken);
            return new(record is null ? LegacyToken() : record.Token);
        }
        finally { _gate.Release(); }
    }

    public async Task<HuggingFaceCredentialStatus> SaveAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (!ValidToken(token))
            throw new HuggingFaceManagementException(400, "invalid_token_format", "Supply a Hugging Face access token (hf_ followed by letters and digits, at most 512 characters).");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Check storage before contacting the provider; corruption must never enable fallback.
            await ReadAsync(cancellationToken);
            using var response = await HuggingFaceManagementHttp.GetJsonAsync(
                _http, "/api/whoami-v2", token, 128 * 1024, cancellationToken, validatingToken: true);
            var root = response.RootElement;
            var name = HuggingFaceManagementHttp.String(root, "name");
            if (HuggingFaceManagementHttp.String(root, "type") != "user" || name is null
                || !Regex.IsMatch(name, @"\A[A-Za-z0-9][A-Za-z0-9-]{0,95}\z", RegexOptions.CultureInvariant))
                throw HuggingFaceManagementHttp.InvalidResponse();
            var record = new CredentialRecord(1, "managed", token, name, DateTimeOffset.UtcNow);
            await WriteAsync(record, cancellationToken);
            return Status(record);
        }
        finally { _gate.Release(); }
    }

    public async Task<HuggingFaceCredentialStatus> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Disconnect may replace a corrupt regular file, but never follow a link.
            var record = new CredentialRecord(1, "disconnected", null, null, null);
            await WriteAsync(record, cancellationToken);
            return Status(record);
        }
        finally { _gate.Release(); }
    }

    private string? LegacyToken()
    {
        if (string.IsNullOrWhiteSpace(_legacy.HuggingFaceToken)) return null;
        if (!ValidToken(_legacy.HuggingFaceToken)) throw StorageError();
        return _legacy.HuggingFaceToken;
    }

    private static bool ValidToken(string? token) => token is { Length: >= 4 and <= 512 }
        && Regex.IsMatch(token, @"\Ahf_[A-Za-z0-9]+\z", RegexOptions.CultureInvariant);

    private static HuggingFaceCredentialStatus Status(CredentialRecord record) =>
        new(record.State == "managed", record.AccountName, record.State, record.ValidatedAt);

    private async Task<CredentialRecord?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            EnsureDirectory();
            RejectLinks(_path);
            FileStream stream;
            try
            {
                stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (FileNotFoundException) { return null; }
            await using (stream)
            {
                RejectLinks(_path);
                MakePrivate(_path, directory: false);
                var bytes = await HuggingFaceManagementHttp.ReadBoundedAsync(stream, MaximumRecordBytes, cancellationToken);
                var envelope = JsonSerializer.Deserialize<ProtectedRecord>(bytes, Json);
                if (envelope is not { Version: 1, ProtectedData.Length: > 0 }) throw StorageError();
                var plaintext = _protector.Unprotect(envelope.ProtectedData);
                if (plaintext.Length > 4096) throw StorageError();
                var record = JsonSerializer.Deserialize<CredentialRecord>(plaintext, Json);
                if (record is null || record.Version != 1) throw StorageError();
                if (record.State == "disconnected")
                {
                    if (record.Token is not null || record.AccountName is not null || record.ValidatedAt is not null)
                        throw StorageError();
                }
                else if (record.State != "managed" || !ValidToken(record.Token) || record.AccountName is null
                    || !Regex.IsMatch(record.AccountName, @"\A[A-Za-z0-9][A-Za-z0-9-]{0,95}\z", RegexOptions.CultureInvariant)
                    || record.ValidatedAt is null || record.ValidatedAt > DateTimeOffset.UtcNow.AddMinutes(5))
                    throw StorageError();
                return record;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or CryptographicException or JsonException or ArgumentException or HuggingFaceManagementException)
        {
            throw StorageError();
        }
    }

    private async Task WriteAsync(CredentialRecord record, CancellationToken cancellationToken)
    {
        string? staging = null;
        try
        {
            EnsureDirectory();
            RejectLinks(_path);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new ProtectedRecord(1, _protector.Protect(JsonSerializer.Serialize(record, Json))), Json);
            if (bytes.Length > MaximumRecordBytes) throw StorageError();
            staging = Path.Combine(_directory, $".huggingface-{Guid.NewGuid():N}.new");
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileModeBits;
            await using (var stream = new FileStream(staging, options))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            MakePrivate(staging, directory: false);
            RejectLinks(_path);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, _path, overwrite: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or CryptographicException or ArgumentException)
        {
            throw StorageError();
        }
        finally
        {
            if (staging is not null)
            {
                try { File.Delete(staging); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private void EnsureDirectory()
    {
        RejectLinks(_path);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
        else Directory.CreateDirectory(_directory, DirectoryMode);
        RejectLinks(_path);
        MakePrivate(_directory, directory: true);
    }

    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            var info = new FileInfo(current);
            if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
                throw StorageError();
            var directory = new DirectoryInfo(current);
            if (directory.LinkTarget is not null || (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0))
                throw StorageError();
        }
    }

    private static void MakePrivate(string path, bool directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, directory ? DirectoryMode : FileModeBits);
            return;
        }
        var user = WindowsIdentity.GetCurrent().User ?? throw StorageError();
        if (directory)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
    }

    private static HuggingFaceManagementException StorageError() =>
        new(503, "credential_storage_unavailable", "Hugging Face credential storage is unreadable or unsafe. Check private storage permissions and the existing Data Protection keys, or disconnect to replace a damaged record. No fallback credential was used.");

    private sealed record ProtectedRecord(int Version, string ProtectedData);
    private sealed record CredentialRecord(int Version, string State, string? Token, string? AccountName, DateTimeOffset? ValidatedAt);
}

internal static class HuggingFaceManagementHttp
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // No HttpClientFactory logging middleware, ambient credentials, redirects, or custom certificate trust.
    internal static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null,
        ConnectTimeout = TimeSpan.FromSeconds(5), MaxResponseHeadersLength = 32,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    }) { Timeout = Timeout };

    internal static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path, string? token, int maximumBytes,
        CancellationToken cancellationToken, bool validatingToken = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        try
        {
            var uri = new Uri("https://huggingface.co" + path);
            if (!path.StartsWith("/api/", StringComparison.Ordinal) || uri.Scheme != "https"
                || uri.Host != "huggingface.co" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
                || (token is not null && uri.AbsoluteUri.Contains(token, StringComparison.Ordinal)))
                throw new HuggingFaceManagementException(400, "invalid_provider_path", "The Hugging Face API path is invalid.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) throw ProviderError(response, validatingToken);
            if (response.Content.Headers.ContentLength > maximumBytes)
                throw TooLarge();
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = await ReadBoundedAsync(stream, maximumBytes, deadline.Token);
            var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
            try
            {
                if (token is not null && ContainsToken(document.RootElement, token)) throw InvalidResponse();
                return document;
            }
            catch { document.Dispose(); throw; }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            throw new HuggingFaceManagementException(504, "provider_timeout", "Hugging Face did not respond in time. Try again.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new HuggingFaceManagementException(502, "provider_unavailable", "Cannot securely reach Hugging Face. Check network connectivity and try again.");
        }
        catch (JsonException) { throw InvalidResponse(); }
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximumBytes + 1 - (int)output.Length)), cancellationToken);
            if (read == 0) return output.ToArray();
            output.Write(buffer, 0, read);
            if (output.Length > maximumBytes) throw TooLarge();
        }
    }

    internal static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static HuggingFaceManagementException InvalidResponse() =>
        new(502, "invalid_provider_response", "Hugging Face returned unexpected metadata. No credential or model was changed.");

    private static HuggingFaceManagementException TooLarge() =>
        new(502, "provider_response_too_large", "Hugging Face returned more metadata than this bounded browser supports. Try a smaller repository or a narrower search.");

    private static bool ContainsToken(JsonElement value, string token) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Contains(token, StringComparison.Ordinal),
        JsonValueKind.Array => value.EnumerateArray().Any(item => ContainsToken(item, token)),
        JsonValueKind.Object => value.EnumerateObject().Any(item =>
            item.Name.Contains(token, StringComparison.Ordinal) || ContainsToken(item.Value, token)),
        _ => false
    };

    private static HuggingFaceManagementException ProviderError(HttpResponseMessage response, bool validatingToken)
    {
        var status = (int)response.StatusCode;
        if (status == 429)
        {
            var retry = response.Headers.RetryAfter;
            var seconds = retry?.Delta?.TotalSeconds ?? (retry?.Date - DateTimeOffset.UtcNow)?.TotalSeconds;
            return new(429, "provider_rate_limited", "Hugging Face rate-limited this request. Wait before retrying; a valid access token may provide higher limits.",
                seconds.HasValue ? (int)Math.Clamp(Math.Ceiling(seconds.Value), 1, 3600) : null);
        }
        if (status is 401 or 403)
            return validatingToken
                ? new(422, "provider_token_rejected", "Hugging Face rejected this token. Check that it is valid and has read access. The previous credential was preserved.")
                : new(403, "provider_access_denied", "Hugging Face denied access. Connect a valid read token and, for gated models, accept the repository terms and obtain approval on Hugging Face.");
        if (status == 404)
            return new(404, "provider_not_found", "The repository or revision was not found, or it is private and your Hugging Face token cannot access it.");
        if (status is >= 300 and < 400)
            return new(502, "provider_redirect_rejected", "Hugging Face requested a redirect. For credential safety this browser does not follow redirects; use the repository's current owner/name.");
        return new(502, "provider_unavailable", "Hugging Face could not complete this request. Try again later.");
    }
}
