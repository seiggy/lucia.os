using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Nodes;

namespace Lucia.Homelab.Server.Assistant.Skills;

/// <summary>
/// The Hyperlight sandbox runtime this controller ships under <c>boot/skill-sandbox-linux-x64</c> (the self-contained
/// Lucia.SkillSandbox build for x86_64 Linux) and the tar.gz nodes with KVM download. Its hash is the same file-name-and-SHA-256
/// digest the node agent release uses. The package is built once, on first use, and kept in memory.
/// </summary>
public sealed partial class SkillSandboxRuntime(string? directory = null)
{
    public const string Folder = "skill-sandbox-linux-x64";
    private readonly string _directory = directory ?? Path.Combine(AppContext.BaseDirectory, "boot", Folder);
    private readonly SemaphoreSlim _build = new(1, 1);
    private (string Hash, byte[] Bytes, string Sha256)? _package;
    private string? _hash;

    /// <summary>The shipped runtime's hash, or null when this controller has no sandbox build.</summary>
    public string? Hash => _hash ??= NodeAgentRelease.Compute(_directory);

    public async Task<SkillSandboxEntry?> EntryAsync(CancellationToken ct) =>
        await BuildAsync(ct) is { } package ? new(package.Hash, package.Bytes.Length, package.Sha256) : null;

    public async Task<(byte[] Bytes, string Sha256)?> GetAsync(string hash, CancellationToken ct) =>
        await BuildAsync(ct) is { } package && string.Equals(package.Hash, hash, StringComparison.Ordinal) ? (package.Bytes, package.Sha256) : null;

    private async Task<(string Hash, byte[] Bytes, string Sha256)?> BuildAsync(CancellationToken ct)
    {
        if (_package is { } built) return built;
        await _build.WaitAsync(ct);
        try
        {
            if (_package is { } again) return again;
            _package = await Task.Run(Pack, ct);
            return _package;
        }
        finally { _build.Release(); }
    }

    private (string Hash, byte[] Bytes, string Sha256)? Pack()
    {
        try
        {
            if (Hash is not { } hash) return null;
            using var packed = new MemoryStream();
            using (var gzip = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: true))
                foreach (var file in Directory.GetFiles(_directory).Order(StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(file);
                    if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                    using var data = File.OpenRead(file);
                    tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name)
                    {
                        DataStream = data, ModificationTime = DateTimeOffset.UnixEpoch, Mode = Executable(name) ? ExecutableMode : PlainMode,
                    });
                }
            var bytes = packed.ToArray();
            return (hash, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private const UnixFileMode PlainMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode ExecutableMode = PlainMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>The launcher, createdump and the native libraries keep their execute bit; the node unpacks it and the container runs them.</summary>
    internal static bool Executable(string name) => name is "Lucia.SkillSandbox" or "createdump" || NativeLibrary().IsMatch(name);

    [GeneratedRegex(@"\Alib[A-Za-z0-9_+.-]+\.so(?:\.[0-9]+)*\z")]
    private static partial Regex NativeLibrary();
}
