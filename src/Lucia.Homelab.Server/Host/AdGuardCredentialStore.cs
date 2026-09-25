using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;

namespace Lucia.Homelab.Server.Host;

internal sealed class AdGuardStoredConnection
{
    [JsonRequired] public int Version { get; init; } = 1;
    [JsonRequired] public string State { get; init; } = "";
    public string? BaseUrl { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public bool AllowInsecureHttp { get; init; }
    public string? ServerVersion { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public override string ToString() => nameof(AdGuardStoredConnection);
}

internal sealed class AdGuardCredentialStore
{
    internal const int MaximumRecordBytes = 32 * 1024;
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileModeBits = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 4, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _directory;
    private readonly string _path;
    private readonly IDataProtector _protector;

    internal AdGuardCredentialStore(string directory, IDataProtectionProvider protection)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) throw StorageError();
        _directory = Path.GetFullPath(directory);
        _path = Path.Combine(_directory, "adguard.json");
        _protector = protection.CreateProtector("Lucia.Homelab.AdGuardCredentials.v1");
    }

    internal async Task<AdGuardStoredConnection?> ReadAsync(CancellationToken ct)
    {
        try
        {
            EnsureDirectory();
            FileStream stream;
            try { stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous); }
            catch (FileNotFoundException) { return null; }
            await using (stream)
            {
                RejectLinks(_path);
                MakePrivate(_path, directory: false);
                var bytes = await AdGuardTransport.ReadBoundedAsync(stream, MaximumRecordBytes, ct);
                RejectDuplicateProperties(bytes);
                var envelope = JsonSerializer.Deserialize<ProtectedRecord>(bytes, Json);
                if (envelope is not { Version: 1, ProtectedData.Length: > 0 }) throw StorageError();
                var plaintext = _protector.Unprotect(Convert.FromBase64String(envelope.ProtectedData));
                try
                {
                    if (plaintext.Length > 16 * 1024) throw StorageError();
                    RejectDuplicateProperties(plaintext);
                    var record = JsonSerializer.Deserialize<AdGuardStoredConnection>(plaintext, Json);
                    if (record is null || record.Version != 1) throw StorageError();
                    if (record.State == "disconnected")
                    {
                        if (record.BaseUrl is not null || record.Username is not null || record.Password is not null
                            || record.ServerVersion is not null || record.LastVerifiedAt is not null || record.AllowInsecureHttp)
                            throw StorageError();
                    }
                    else
                    {
                        if (record.State != "configured" || !AdGuardValidation.Version(record.ServerVersion)
                            || record.LastVerifiedAt is null || record.LastVerifiedAt > DateTimeOffset.UtcNow.AddMinutes(5)
                            || record.LastVerifiedAt < DateTimeOffset.UnixEpoch) throw StorageError();
                        var canonical = AdGuardValidation.Connection(new()
                        {
                            BaseUrl = record.BaseUrl!, Username = record.Username!,
                            Password = record.Password!, AllowInsecureHttp = record.AllowInsecureHttp
                        });
                        if (canonical != record.BaseUrl
                            || AdGuardValidation.SensitiveMetadata(record.ServerVersion!, record.Username!, record.Password!))
                            throw StorageError();
                    }
                    return record;
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException
            or JsonException or ArgumentException or FormatException or AdGuardManagementException)
        { throw StorageError(); }
    }

    internal async Task WriteAsync(AdGuardStoredConnection record, CancellationToken ct)
    {
        string? staging = null;
        try
        {
            EnsureDirectory();
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(record, Json);
            byte[] bytes;
            try
            {
                if (plaintext.Length > 16 * 1024) throw StorageError();
                bytes = JsonSerializer.SerializeToUtf8Bytes(
                    new ProtectedRecord(1, Convert.ToBase64String(_protector.Protect(plaintext))), Json);
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            if (bytes.Length > MaximumRecordBytes) throw StorageError();
            staging = Path.Combine(_directory, $".adguard-{Guid.NewGuid():N}.new");
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileModeBits;
            await using (var stream = new FileStream(staging, options))
            {
                MakePrivate(staging, directory: false);
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            RejectLinks(_path);
            RejectLinks(staging);
            ct.ThrowIfCancellationRequested();
            File.Move(staging, _path, overwrite: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        { throw StorageError(); }
        finally
        {
            if (staging is not null)
            {
                try { RejectLinks(staging); File.Delete(staging); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or AdGuardManagementException) { }
            }
        }
    }

    private void EnsureDirectory()
    {
        RejectLinks(_path);
        if (OperatingSystem.IsWindows())
        {
            // Apply the private ACL at creation, not after inherited readers can enter the directory.
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User ?? throw StorageError(),
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(_directory).Create(security);
        }
        else Directory.CreateDirectory(_directory, DirectoryMode);
        RejectLinks(_path);
        MakePrivate(_directory, directory: true);
        if (Directory.Exists(_path)) throw StorageError();
    }

    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            var file = new FileInfo(current);
            var dir = new DirectoryInfo(current);
            if (file.LinkTarget is not null || dir.LinkTarget is not null
                || (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0)
                || (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0))
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
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
    }

    private static void RejectDuplicateProperties(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw StorageError();
        var fields = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        if (fields.Distinct(StringComparer.Ordinal).Count() != fields.Length) throw StorageError();
    }

    internal static AdGuardManagementException StorageError() => new(503, "adguard_storage_unavailable",
        "AdGuard credential storage is unreadable or unsafe. Check private storage permissions and Data Protection keys. Disconnect can replace a damaged regular record, but not an unsafe path.");
    private sealed record ProtectedRecord(int Version, string ProtectedData);
}
