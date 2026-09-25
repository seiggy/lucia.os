using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace Lucia.Homelab.Server.Domains;

internal static class CertbotFiles
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static bool IsWithin(string root, string path) =>
        string.Equals(Path.GetFullPath(root), Path.GetFullPath(path), PathComparison)
        || Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
            + Path.DirectorySeparatorChar, PathComparison);

    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            var file = new FileInfo(current);
            var directory = new DirectoryInfo(current);
            if (file.LinkTarget is not null || directory.LinkTarget is not null
                || (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0)
                || (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0))
                throw CertbotCertificateService.StorageError();
        }
    }

    internal static void EnsureDirectory(string path)
    {
        RejectLinks(path);
        if (!Directory.Exists(path))
        {
            var parent = Path.GetDirectoryName(path);
            if (parent is not null && !Directory.Exists(parent)) EnsureDirectory(parent);
            if (OperatingSystem.IsWindows()) new DirectoryInfo(path).Create(DirectoryAcl());
            else Directory.CreateDirectory(path, DirectoryMode);
        }
        MakePrivate(path, directory: true);
    }

    internal static void PrepareCoreDirectories(string root, string config, string work)
    {
        // Certbot's strict check requires exactly 0755 here. The enclosing 0700 root
        // keeps accounts, keys and working files inaccessible to other users.
        EnsureDirectory(root);
        foreach (var path in new[] { config, work, Path.Combine(config, "renewal-hooks"),
            Path.Combine(config, "renewal-hooks", "pre"), Path.Combine(config, "renewal-hooks", "deploy"),
            Path.Combine(config, "renewal-hooks", "post") })
        {
            EnsureDirectory(path);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, DirectoryMode | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    internal static FileStream OpenPrivate(string path, FileMode mode)
    {
        RejectLinks(path);
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateMode;
        var file = new FileStream(path, options);
        try { MakePrivate(path, directory: false); return file; }
        catch { file.Dispose(); throw; }
    }

    internal static void WritePrivate(string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            using var file = OpenPrivate(path, FileMode.CreateNew);
            file.Write(bytes);
            file.Flush(true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static string ReadBounded(string path, int limit)
    {
        RejectLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > limit) throw CertbotCertificateService.StorageError();
        var bytes = new byte[limit + 1];
        try
        {
            var count = file.ReadAtLeast(bytes, limit + 1, throwOnEndOfStream: false);
            if (count > limit) throw CertbotCertificateService.StorageError();
            return new UTF8Encoding(false, true).GetString(bytes, 0, count);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static (string Pem, string Generation) ReadLive(string config, string lineage, string stem)
    {
        var path = Path.Combine(config, "live", lineage, stem + ".pem");
        var resolved = ResolveLive(config, path);
        return (ReadBounded(resolved.Path, stem == "privkey" ? 32 * 1024 : 128 * 1024), resolved.Generation);
    }

    private static (string Path, string Generation) ResolveLive(string config, string path)
    {
        RejectLinks(Path.GetDirectoryName(path)!);
        var relative = Path.GetRelativePath(Path.Combine(config, "live"), path).Split(Path.DirectorySeparatorChar);
        if (relative.Length != 2 || !CertbotCertificateService.IsLineage(relative[0])
            || !Regex.IsMatch(relative[1], @"\A(cert|chain|fullchain|privkey)\.pem\z"))
            throw CertbotCertificateService.StorageError();
        var target = new FileInfo(path).LinkTarget;
        if (target is null) throw CertbotCertificateService.StorageError();
        var resolved = Path.GetFullPath(target, Path.GetDirectoryName(path)!);
        // Do not normalize away components that Certbot/the OS could traverse through a link.
        var canonicalTarget = Path.IsPathFullyQualified(target) ? resolved
            : Path.GetRelativePath(Path.GetDirectoryName(path)!, resolved);
        if (!string.Equals(target, canonicalTarget, PathComparison)) throw CertbotCertificateService.StorageError();
        var archive = Path.Combine(config, "archive", relative[0]);
        if (!string.Equals(Path.GetDirectoryName(resolved), archive, PathComparison))
            throw CertbotCertificateService.StorageError();
        var stem = Path.GetFileNameWithoutExtension(path);
        var match = Regex.Match(Path.GetFileName(resolved), @"\A" + stem + @"([1-9][0-9]*)\.pem\z");
        if (!match.Success) throw CertbotCertificateService.StorageError();
        RejectLinks(resolved);
        if (!File.Exists(resolved)) throw CertbotCertificateService.StorageError();
        return (resolved, match.Groups[1].Value);
    }

    internal static void CheckTree(string root, string? config = null)
    {
        RejectLinks(root);
        MakePrivate(root, directory: true);
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).LinkTarget is not null)
            {
                if (config is null) throw CertbotCertificateService.StorageError();
                _ = ResolveLive(config, path);
            }
            else if ((attributes & FileAttributes.Directory) != 0) CheckTree(path, config);
            else MakePrivate(path, directory: false);
        }
    }

    internal static void SanitizeLogs(string directory, string token)
    {
        CheckTree(directory);
        var needle = Encoding.UTF8.GetBytes(token);
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var staging = path + ".redacted-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var output = OpenPrivate(staging, FileMode.CreateNew))
                    {
                        var buffer = new byte[8192 + needle.Length];
                        var carry = 0;
                        int count;
                        while ((count = input.Read(buffer, carry, 8192)) != 0)
                        {
                            var length = carry + count;
                            var scan = buffer.AsSpan(0, length);
                            int offset;
                            while ((offset = scan.IndexOf(needle)) >= 0)
                            {
                                scan.Slice(offset, needle.Length).Fill((byte)'*');
                                scan = scan[(offset + needle.Length)..];
                            }
                            var write = Math.Max(0, length - needle.Length + 1);
                            output.Write(buffer, 0, write);
                            carry = length - write;
                            buffer.AsSpan(write, carry).CopyTo(buffer);
                        }
                        output.Write(buffer, 0, carry);
                        output.Flush(true);
                        CryptographicOperations.ZeroMemory(buffer);
                    }
                    RejectLinks(path);
                    File.Move(staging, path, overwrite: true);
                }
                finally { if (File.Exists(staging)) File.Delete(staging); }
            }
        }
        finally { CryptographicOperations.ZeroMemory(needle); }
    }

    private static void MakePrivate(string path, bool directory)
    {
        RejectLinks(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, directory ? DirectoryMode : PrivateMode);
        else if (directory) new DirectoryInfo(path).SetAccessControl(DirectoryAcl());
        else
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static DirectorySecurity DirectoryAcl()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
