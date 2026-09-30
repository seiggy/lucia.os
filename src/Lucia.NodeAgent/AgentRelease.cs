using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Lucia.NodeAgent;

/// <summary>
/// The installed agent's release ID and its self-update. The ID is a SHA-256 over each file's name and SHA-256, sorted
/// by name, the same as Lucia computes over the agent it ships. An update extracts Lucia's agent beside the live one,
/// checks it matches the release Lucia named, swaps the directories (keeping the old one as <c>agent.prev</c>) and
/// restarts the service. To roll back by hand: stop the service, move <c>agent.prev</c> back to <c>agent</c>, start it.
/// </summary>
internal static partial class AgentRelease
{
    internal const int MaxFiles = 512;
    internal const long MaxBytes = 512L * 1024 * 1024;
    internal const string Live = "/usr/lib/lucia/agent";
    private static readonly string[] Required = ["lucia-node-agent", "lucia-node-agent.dll", "lucia-node-agent.deps.json",
        "lucia-node-agent.runtimeconfig.json", "libcoreclr.so", "libhostfxr.so"];
    private static readonly Lazy<string?> Installed = new(() =>
    {
        try { return Compute(AppContext.BaseDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    });

    internal static string? Current => Installed.Value;

    internal static bool IsId(string value) => ReleasePattern().IsMatch(value);

    internal static string Compute(string directory)
    {
        var manifest = new StringBuilder();
        foreach (var file in Directory.GetFiles(directory).Order(StringComparer.Ordinal))
        {
            using var stream = File.OpenRead(file);
            manifest.Append(Path.GetFileName(file)).Append('\0').Append(Convert.ToHexStringLower(SHA256.HashData(stream))).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString())));
    }

    internal static async Task<string> UpdateAsync(DiscoveryClient client, Guid node, string certificate, ECDsa key, CancellationToken token)
    {
        var next = Live + ".next";
        var previous = Live + ".prev";
        if (Directory.Exists(next)) Directory.Delete(next, recursive: true);
        if (OperatingSystem.IsWindows()) throw new NodeAgentException("The agent updates itself only on its Debian server.");
        Directory.CreateDirectory(next, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string? release = null;
        await client.DownloadAgentAsync(node, certificate, key, async (stream, named, ct) =>
        {
            release = named;
            await ExtractAsync(stream, next, ct);
        }, token);
        if (Compute(next) != release)
        {
            Directory.Delete(next, recursive: true);
            throw new NodeAgentException("The downloaded agent doesn't match the release Lucia named. Nothing was changed.");
        }
        if (release == Current)
        {
            Directory.Delete(next, recursive: true);
            return "The agent is already up to date.";
        }
        if (Directory.Exists(previous)) Directory.Delete(previous, recursive: true);
        Directory.Move(Live, previous);
        try { Directory.Move(next, Live); }
        catch
        {
            Directory.Move(previous, Live);
            throw;
        }
        Console.Error.WriteLine("Agent updated; restarting into it. The previous agent is kept at " + previous + ".");
        // A transient timer restarts the service from outside it, so the restart doesn't kill its own caller.
        await NodeRuntime.RunAsync("/usr/bin/systemd-run", ["--on-active=3", "--unit=lucia-agent-restart", "--collect", "--quiet",
            "/usr/bin/systemctl", "restart", "lucia-node-agent.service"], TimeSpan.FromMinutes(1), token);
        return "Agent updated.";
    }

    /// <summary>Extracts a flat tar of regular files with plain names into an empty directory, within the file and size limits.</summary>
    internal static async Task ExtractAsync(Stream stream, string directory, CancellationToken token)
    {
        await using var reader = new TarReader(stream);
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        while (await reader.GetNextEntryAsync(copyData: false, token) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || !NamePattern().IsMatch(entry.Name) || entry.Name is "." or ".." || !names.Add(entry.Name) || names.Count > MaxFiles
                || (total += entry.Length) > MaxBytes)
                throw new NodeAgentException("The agent download holds something other than a flat set of files. Nothing was changed.");
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                | (entry.Name is "lucia-node-agent" or "createdump" ? UnixFileMode.UserExecute : 0);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = mode;
            await using var output = new FileStream(Path.Combine(directory, entry.Name), options);
            if (entry.DataStream is { } data) await data.CopyToAsync(output, token);
            if (output.Length != entry.Length) throw new NodeAgentException("The agent download was cut short. Nothing was changed.");
        }
        if (Required.Any(name => !names.Contains(name)))
            throw new NodeAgentException("The agent download is missing parts of the agent. Nothing was changed.");
    }

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex ReleasePattern();
    [GeneratedRegex(@"\A[A-Za-z0-9_.+-]{1,128}\z")]
    private static partial Regex NamePattern();
}
