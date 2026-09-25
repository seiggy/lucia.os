using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;

namespace Lucia.Homelab.Server.Domains;

internal sealed record CloudflareStoredCredential(int Version, string State, string? AccountId, string? Token,
    DateTimeOffset? VerifiedAt, DateTimeOffset? ExpiresAt, int ZoneCount)
{
    public override string ToString() => nameof(CloudflareStoredCredential);
}

internal sealed class CloudflareCredentialStore
{
    private const int MaximumRecordBytes = 65536;
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileModeBits = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _directory;
    private readonly string _path;
    private readonly IDataProtector _protector;

    internal CloudflareCredentialStore(string directory, IDataProtectionProvider protection)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) throw StorageError();
        _directory = Path.GetFullPath(directory);
        _path = Path.Combine(_directory, "cloudflare-domains.json");
        _protector = protection.CreateProtector("Lucia.Homelab.CloudflareDomainCredentials.v1");
    }

    internal async Task<CloudflareStoredCredential?> ReadAsync(CancellationToken ct)
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
                var bytes = await CloudflareHttp.ReadBoundedAsync(stream, MaximumRecordBytes, ct);
                var envelope = JsonSerializer.Deserialize<ProtectedRecord>(bytes, Json);
                if (envelope is not { Version: 1, ProtectedData.Length: > 0 }) throw StorageError();
                var plaintext = _protector.Unprotect(envelope.ProtectedData);
                if (plaintext.Length > 32768) throw StorageError();
                var record = JsonSerializer.Deserialize<CloudflareStoredCredential>(plaintext, Json);
                if (record is null || record.Version != 1) throw StorageError();
                if (record.State == "disconnected")
                {
                    if (record.AccountId is not null || record.Token is not null || record.VerifiedAt is not null
                        || record.ExpiresAt is not null || record.ZoneCount != 0) throw StorageError();
                }
                else if (record.State != "managed" || !CloudflareValidation.Id(record.AccountId)
                    || !CloudflareValidation.Token(record.Token) || record.VerifiedAt is null
                    || record.VerifiedAt > DateTimeOffset.UtcNow.AddMinutes(5) || record.ExpiresAt <= record.VerifiedAt
                    || record.ZoneCount is < 1 or > 1000) throw StorageError();
                return record;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException
            or JsonException or ArgumentException or CloudflareDomainException or System.Security.SecurityException)
        { throw StorageError(); }
    }

    internal async Task WriteAsync(CloudflareStoredCredential record, CancellationToken ct)
    {
        string? staging = null;
        try
        {
            EnsureDirectory();
            RejectLinks(_path);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new ProtectedRecord(1, _protector.Protect(JsonSerializer.Serialize(record, Json))), Json);
            if (bytes.Length > MaximumRecordBytes) throw StorageError();
            staging = Path.Combine(_directory, $".cloudflare-{Guid.NewGuid():N}.new");
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileModeBits;
            await using (var stream = new FileStream(staging, options))
            {
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            MakePrivate(staging, directory: false);
            RejectLinks(_path);
            ct.ThrowIfCancellationRequested();
            File.Move(staging, _path, overwrite: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException
            or ArgumentException or System.Security.SecurityException)
        { throw StorageError(); }
        finally
        {
            if (staging is not null)
            {
                try { File.Delete(staging); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
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
            var file = new FileInfo(current);
            var directory = new DirectoryInfo(current);
            if (file.LinkTarget is not null || (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0)
                || directory.LinkTarget is not null || (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0))
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

    private static CloudflareDomainException StorageError() =>
        new(503, "credential_storage_unavailable", "Cloudflare credential storage is unreadable or unsafe. Check private permissions and the existing Data Protection keys, or disconnect to replace a damaged regular file. No environment credential was used.");

    private sealed record ProtectedRecord(int Version, string ProtectedData);
}
