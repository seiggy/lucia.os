using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lucia.Desktop.Core;

internal sealed record HostArtifact(string ArchivePath, string ManifestPath, string Sha256, long Size)
{
    internal static HostArtifact Load(string directory)
    {
        var archive = Path.GetFullPath(Path.Combine(directory, "host-linux-arm64.tar.gz"));
        var manifest = Path.GetFullPath(Path.Combine(directory, "manifest.json"));
        foreach (var path in new[] { archive, manifest })
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The host package must not contain symbolic links.");
        if (new FileInfo(manifest).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("The host manifest exceeds its size limit.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifest));
        var root = document.RootElement;
        if (root.GetProperty("schema_version").GetInt32() != 1 || root.GetProperty("rid").GetString() != "linux-arm64" ||
            root.GetProperty("version").GetString() != "0.1.0" ||
            root.GetProperty("entrypoint").GetString() != "Lucia.Homelab.Server.dll")
            throw new InvalidDataException("This desktop needs a Linux ARM64 Lucia host package.");
        var sha256 = root.GetProperty("sha256").GetString();
        var size = root.GetProperty("size").GetInt64();
        if (sha256 is null || sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)) ||
            size is < 1 or > 4L * 1024 * 1024 * 1024 || new FileInfo(archive).Length != size)
            throw new InvalidDataException("The host archive size or fingerprint is invalid.");
        var files = root.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() is < 2 or > 20000)
            throw new InvalidDataException("The host manifest file list is invalid.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in files.EnumerateArray())
        {
            var name = file.GetProperty("path").GetString();
            var length = file.GetProperty("size").GetInt64();
            var hash = file.GetProperty("sha256").GetString();
            if (name is null || !Regex.IsMatch(name, @"\A[A-Za-z0-9_./@+-]{1,240}\z") ||
                name.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.')) ||
                !names.Add(name) || length is < 0 or > 1024L * 1024 * 1024 ||
                hash is null || !Regex.IsMatch(hash, @"\A[a-f0-9]{64}\z"))
                throw new InvalidDataException("The host manifest contains unsafe paths or invalid file metadata.");
            total += length;
        }
        if (total > 4L * 1024 * 1024 * 1024 || !names.Contains("Lucia.Homelab.Server.dll") ||
            !names.Contains("wwwroot/index.html"))
            throw new InvalidDataException("The host manifest is oversized or missing its server/dashboard entrypoint.");
        return new HostArtifact(archive, manifest, sha256.ToLowerInvariant(), size);
    }

    internal async Task VerifyAsync(CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(ArchivePath);
        var actual = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
        if (Convert.ToHexStringLower(actual) != Sha256)
            throw new InvalidDataException("The bundled host archive failed its SHA-256 check. Rebuild or reinstall the complete desktop package.");
    }
}
