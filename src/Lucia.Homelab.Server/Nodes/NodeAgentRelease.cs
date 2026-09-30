using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Lucia.Homelab.Server.Nodes;

/// <summary>
/// The node agent this controller ships under <c>boot/node-agent-linux-x64</c>. Its release ID is a SHA-256 over each
/// file's name and SHA-256, sorted by name; the agent computes the same over its installed directory, so a node is up
/// to date exactly when the IDs match.
/// </summary>
public static partial class NodeAgentRelease
{
    public const int MaxFiles = 512;
    public const long MaxBytes = 512L * 1024 * 1024;
    private static readonly string Shipped = Path.Combine(AppContext.BaseDirectory, "boot", "node-agent-linux-x64");
    private static readonly Lazy<string?> LatestId = new(() => Compute(Shipped));

    /// <summary>The shipped agent's release ID, or null when this controller has no agent build.</summary>
    public static string? Latest => LatestId.Value;

    public static bool IsId(string value) => ReleasePattern().IsMatch(value);

    public static string? Compute(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        var files = Directory.GetFiles(directory).Order(StringComparer.Ordinal).ToArray();
        if (files.Length is 0 or > MaxFiles) return null;
        var manifest = new StringBuilder();
        foreach (var file in files)
        {
            using var stream = File.OpenRead(file);
            manifest.Append(Path.GetFileName(file)).Append('\0').Append(Convert.ToHexStringLower(SHA256.HashData(stream))).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString())));
    }

    /// <summary>Streams the shipped agent as a flat, uncompressed tar.</summary>
    public static async Task WriteTar(Stream output, CancellationToken ct)
    {
        await using var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var file in Directory.GetFiles(Shipped).Order(StringComparer.Ordinal))
            await writer.WriteEntryAsync(file, Path.GetFileName(file), ct);
    }

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex ReleasePattern();
}
