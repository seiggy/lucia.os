using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Lucia.NodeAgent;

/// <summary>What bounds a downloaded package: how many files, how big, and whether it may hold folders and executable files.</summary>
internal sealed record SkillLimits(int Files, long FileBytes, long TotalBytes, long DownloadBytes, bool Directories, bool KeepExecutable, bool RequireSkillFile);

/// <summary>
/// The node's copy of Lucia's skill packages under <c>/var/lib/lucia-agent/skills/&lt;name&gt;/&lt;hash&gt;</c>. A package is checked
/// against the SHA-256 Lucia declared, unpacked into a fresh directory with strict rules (regular files only, plain relative
/// names, bounded sizes, root-owned 0700 folders and 0600 files, no execute bits; the sandbox runtime alone is world-readable and keeps
/// its execute bits, because a container's unprivileged user runs it) and moved into place with one rename, so a reader sees a whole package or none. Packages Lucia no longer lists are pruned.
/// </summary>
internal static partial class SkillCache
{
    internal const string Root = "/var/lib/lucia-agent/skills";
    /// <summary>The folder name of the sandbox runtime; it can't be a skill name, which never starts with an underscore.</summary>
    internal const string SandboxName = "_sandbox";
    internal static readonly SkillLimits Skill = new(256, 1024 * 1024, 6L * 1024 * 1024, 8L * 1024 * 1024, false, false, true);
    internal static readonly SkillLimits Sandbox = new(16384, 512L * 1024 * 1024, 1536L * 1024 * 1024, 1024L * 1024 * 1024, true, true, false);
    private const string Staging = ".tmp-";
    private const int MaxDepth = 12, MaxPath = 240;

    internal static bool ValidName(string name) => name == SandboxName || name.Length <= 64 && NamePattern().IsMatch(name);
    internal static bool ValidHash(string hash) => HashPattern().IsMatch(hash);

    /// <summary>Installs a verified package; returns false when this hash was already there.</summary>
    internal static bool Install(string root, string name, string hash, byte[] gzip, string expectedSha256, SkillLimits limits)
    {
        if (!ValidName(name) || !ValidHash(hash) || !HashPattern().IsMatch(expectedSha256))
            throw new NodeAgentException("The skill name or hash is not valid.");
        if (gzip.Length > limits.DownloadBytes) throw new NodeAgentException("The skill download is larger than allowed.");
        if (!Convert.ToHexStringLower(SHA256.HashData(gzip)).Equals(expectedSha256, StringComparison.Ordinal))
            throw new NodeAgentException("The downloaded skill doesn't match the checksum Lucia declared. It was discarded.");
        var target = Path.Combine(root, name, hash);
        if (Directory.Exists(target)) return false;
        PrivateDirectory(root);
        var staging = Path.Combine(root, Staging + Guid.NewGuid().ToString("N"));
        try
        {
            Unpack(gzip, staging, limits);
            PrivateDirectory(Path.Combine(root, name));
            if (Directory.Exists(target)) return false;
            Directory.Move(staging, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NodeAgentException("The skill could not be installed: " + ex.Message);
        }
        finally { Remove(staging); }
    }

    /// <summary>Unpacks <paramref name="gzip"/> into the new directory <paramref name="directory"/>, or throws having written nothing the caller must keep.</summary>
    internal static void Unpack(byte[] gzip, string directory, SkillLimits limits)
    {
        try
        {
            PrivateDirectory(directory, limits.KeepExecutable);
            using var packed = new GZipStream(new MemoryStream(gzip, writable: false), CompressionMode.Decompress);
            // The tar's own sizes are checked per entry; this stops a gzip bomb that hides data outside them.
            using var capped = new CappedStream(packed, limits.TotalBytes + limits.Files * 2048L + 64 * 1024);
            using var reader = new TarReader(capped);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            while (reader.GetNextEntry(copyData: false) is { } entry)
            {
                var directoryEntry = entry.EntryType == TarEntryType.Directory;
                if (!(entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile || directoryEntry && limits.Directories))
                    throw new NodeAgentException($"The skill holds a {entry.EntryType} entry; only regular files are accepted.");
                var relative = Relative(entry.Name, directoryEntry);
                if (!seen.Add(relative) || seen.Count > limits.Files) throw new NodeAgentException("The skill has duplicate names or too many files.");
                var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
                if (directoryEntry)
                {
                    PrivateDirectory(path, limits.KeepExecutable);
                    continue;
                }
                if (entry.Length > limits.FileBytes || (total += entry.Length) > limits.TotalBytes)
                    throw new NodeAgentException("The skill is larger than allowed.");
                if (Path.GetDirectoryName(path) is { } parent && parent != directory) PrivateDirectory(parent, limits.KeepExecutable);
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                        | (limits.KeepExecutable && (entry.Mode & UnixFileMode.UserExecute) != 0 ? UnixFileMode.UserExecute : 0);
                using var output = new FileStream(path, options);
                if (entry.DataStream is { } data) data.CopyTo(output);
                if (output.Length != entry.Length) throw new NodeAgentException("The skill download was cut short.");
                if (limits.KeepExecutable && !OperatingSystem.IsWindows()) OpenFile(path, (entry.Mode & UnixFileMode.UserExecute) != 0);
            }
            if (limits.RequireSkillFile && !File.Exists(Path.Combine(directory, "SKILL.md")))
                throw new NodeAgentException("The skill has no SKILL.md.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new NodeAgentException("The skill package is damaged or unsafe: " + ex.Message);
        }
    }

    /// <summary>A plain relative path with forward slashes: no absolute path, drive, backslash, control character, "." or ".." part.</summary>
    internal static string Relative(string name, bool directory)
    {
        var trimmed = directory ? name.TrimEnd('/') : name;
        var parts = trimmed.Split('/');
        if (trimmed.Length is 0 or > MaxPath || parts.Length > MaxDepth || name.StartsWith('/') || trimmed.Contains('\\') || trimmed.Contains(':')
            || trimmed.Any(char.IsControl) || parts.Any(part => part is "" or "." or ".." || part.Length > 128 || part.EndsWith(' ') || part.EndsWith('.')))
            throw new NodeAgentException("The skill holds a file name that isn't a plain relative path.");
        return trimmed;
    }

    /// <summary>The complete packages on disk: skill name to its newest installed hash (the sandbox included as <see cref="SandboxName"/>).</summary>
    internal static Dictionary<string, string> Installed(string root)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!Directory.Exists(root)) return found;
            foreach (var skill in new DirectoryInfo(root).EnumerateDirectories().Where(item => item.LinkTarget is null && ValidName(item.Name)))
            {
                var newest = skill.EnumerateDirectories().Where(item => item.LinkTarget is null && ValidHash(item.Name))
                    .OrderByDescending(item => item.CreationTimeUtc).FirstOrDefault();
                if (newest is not null) found[skill.Name] = newest.Name;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return found;
    }

    /// <summary>Deletes every skill and hash that isn't <paramref name="keep"/> (name to hash), and any leftover staging folder. Returns what it removed.</summary>
    internal static string[] Prune(string root, IReadOnlyDictionary<string, string> keep)
    {
        var removed = new List<string>();
        if (!Directory.Exists(root)) return [];
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos().ToArray())
        {
            if (entry.Name.StartsWith(Staging, StringComparison.Ordinal)) { Remove(entry.FullName); continue; }
            if (!(entry is DirectoryInfo skill && skill.LinkTarget is null && ValidName(entry.Name)) || !keep.TryGetValue(entry.Name, out var hash))
            {
                Remove(entry.FullName);
                removed.Add(entry.Name);
                continue;
            }
            foreach (var version in skill.EnumerateFileSystemInfos().ToArray())
                if (version.Name != hash || version is not DirectoryInfo { LinkTarget: null })
                {
                    Remove(version.FullName);
                    removed.Add(entry.Name + "/" + version.Name);
                }
        }
        return [.. removed];
    }

    private static void Remove(string path)
    {
        try
        {
            if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null) Directory.Delete(path, recursive: true);
            else if (File.Exists(path) || Directory.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Creates a root-owned directory: private, or for the sandbox runtime (public code the container's unprivileged user must read) world-readable.</summary>
    private static void PrivateDirectory(string path, bool open = false)
    {
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(path); return; }
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (open) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static void OpenFile(string path, bool executable)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
            | (executable ? UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute : 0));
    }

    private sealed class CappedStream(Stream inner, long limit) : Stream
    {
        private long read;
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        private int Count(int n) => (read += n) > limit ? throw new InvalidDataException("The package expands to more than allowed.") : n;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [GeneratedRegex(@"\A[a-z0-9]+(?:-[a-z0-9]+)*\z")]
    private static partial Regex NamePattern();
    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex HashPattern();
}
