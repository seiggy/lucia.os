using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lucia.Desktop.Core;

internal static class BootstrapPayload
{
    internal const int MaximumFileBytes = 2 * 1024 * 1024;
    internal const int MaximumTotalBytes = 16 * 1024 * 1024;
    internal static readonly string[] Files =
    [
        "tools/desktop/bootstrap.py",
        "tools/identity/provision.py",
        "tools/identity/owner.py",
        "tools/identity/application.py",
        "tools/host/provision_host.py",
        "tools/host/package.py",
        "tools/boot/prepare.py",
        "tools/boot/provision_boot.py",
        "tools/boot/Dockerfile",
        "tools/domains/activation_worker.py",
        "tools/nodes/enrollment_worker.py",
        "tools/nodes/prepare_directory.py",
        "tools/packages/package_worker.py",
        "deployment/host/Dockerfile",
        "deployment/boot/Dockerfile",
        "deployment/boot/serve.py",
        "deployment/boot/discover-and-wait",
        "deployment/boot/partitioner-guard",
        "deployment/boot/grub.cfg.in",
        "deployment/boot/finish-install",
        "deployment/identity/start-ca.sh",
        "deployment/identity/renew-certificate.sh",
        "deployment/identity/install-node-trust.sh",
        "deployment/identity/init_org_tree.ldif",
        "deployment/identity/init_org_entries.ldif",
        "src/Lucia.Homelab.Identity.AppHost/AppHost.cs",
        "src/Lucia.Homelab.Identity.AppHost/Lucia.Homelab.Identity.AppHost.csproj",
        "src/Lucia.Homelab.Identity.AppHost/aspire.config.json"
    ];

    internal static string Script(string directory)
    {
        var source = Read(directory, Files[0]);
        if (source.Length > 100 * 1024)
            throw new InvalidDataException("The bootstrap entry point exceeds the safe SSH command limit.");
        try { return new UTF8Encoding(false, true).GetString(source).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw new InvalidDataException("The bootstrap entry point is not UTF-8."); }
    }

    internal static (byte[] Archive, string Sha256) Build(string directory, CancellationToken cancellationToken)
    {
        using var archive = new MemoryStream();
        var manifest = new List<object>();
        long total = 0;
        using (var gzip = new GZipStream(archive, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var path in Files.Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = Read(directory, path);
                total += bytes.Length;
                if (total > MaximumTotalBytes) throw new InvalidDataException("The bootstrap payload is too large.");
                manifest.Add(new { path, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) });
                Add(tar, path, bytes);
            }
            Add(tar, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(new { schema_version = 1, version = "0.1.0", files = manifest }));
        }
        var result = archive.ToArray();
        if (result.Length > MaximumTotalBytes) throw new InvalidDataException("The bootstrap archive is too large.");
        return (result, Convert.ToHexStringLower(SHA256.HashData(result)));
    }

    private static byte[] Read(string directory, string relativePath)
    {
        if (!Files.Contains(relativePath, StringComparer.Ordinal))
            throw new InvalidDataException("An unapproved payload path was requested.");
        var path = Path.GetFullPath(directory);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The bootstrap payload directory must not be a symbolic link.");
        foreach (var segment in relativePath.Split('/'))
        {
            path = Path.Combine(path, segment);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Symbolic links are not allowed in the bootstrap payload.");
        }
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaximumFileBytes)
            throw new InvalidDataException("A bootstrap payload file exceeds the size limit.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException("A bootstrap payload file changed while being read.");
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Contains("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal) ||
            text.Contains("-----BEGIN RSA PRIVATE KEY-----", StringComparison.Ordinal) ||
            text.Contains("-----BEGIN EC PRIVATE KEY-----", StringComparison.Ordinal) ||
            text.Contains("-----BEGIN OPENSSH PRIVATE KEY-----", StringComparison.Ordinal) ||
            text.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal))
            throw new InvalidDataException("Private keys are not allowed in the bootstrap payload.");
        return bytes;
    }

    private static void Add(TarWriter writer, string name, byte[] bytes)
    {
        using var data = new MemoryStream(bytes, writable: false);
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = data,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead |
                (name.EndsWith(".sh", StringComparison.Ordinal)
                    ? UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute : 0),
            ModificationTime = DateTimeOffset.UnixEpoch,
            Uid = 0,
            Gid = 0
        });
    }
}
