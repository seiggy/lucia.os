using System.Formats.Tar;
using System.IO.Compression;

namespace Lucia.Homelab.Server.Assistant.Skills;

/// <summary>A skill folder packed as a tar.gz, small enough to travel inside one command to a node.</summary>
public static class SkillBundle
{
    /// <summary>Packs <paramref name="paths"/> (relative, forward slashes) from <paramref name="folder"/>. The same files always give the same bytes.</summary>
    public static byte[] Pack(string folder, IEnumerable<string> paths)
    {
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: true))
            foreach (var path in paths.Order(StringComparer.Ordinal))
            {
                using var data = File.OpenRead(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar)));
                tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = data, ModificationTime = DateTimeOffset.UnixEpoch,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                });
            }
        return packed.ToArray();
    }

    public static string Encode(byte[] bundle) => Convert.ToBase64String(bundle);
}
