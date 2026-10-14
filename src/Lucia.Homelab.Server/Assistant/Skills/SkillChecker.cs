using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Stacks;

namespace Lucia.Homelab.Server.Assistant.Skills;

/// <summary>
/// A syntax-only check of a script, run on a managed server through its agent: the editor shows the result as markers. The file
/// travels gzipped inside the command and is deleted afterwards; nothing in it runs. C# files and Spark-mode skills are checked in
/// the Spark skill runner's container instead.
/// </summary>
public sealed partial class SkillChecker(NodeExec exec, SparkSkillRunner spark, SkillStore store)
{
    public const int Seconds = 60;

    /// <summary>The shell that unpacks <paramref name="gzipBase64"/> and checks it; null for a language that has no server-side check yet.</summary>
    public static string? Command(string extension, string gzipBase64)
    {
        var (runtime, check) = extension.ToLowerInvariant() switch
        {
            ".py" => ("python3", "python3 -m py_compile check.py"),
            ".sh" => ("bash", "bash -n check.sh"),
            ".js" or ".mjs" => ("node", $"node --check check{extension.ToLowerInvariant()}"),
            ".ts" or ".mts" => ("node", $"node --check --experimental-strip-types check{extension.ToLowerInvariant()}"),
            _ => ("", ""),
        };
        if (runtime.Length == 0) return null;
        var file = "check" + extension.ToLowerInvariant();
        return "set -u\numask 077\n"
            + "d=$(mktemp -d) || { echo \"Lucia: could not create a temporary directory on this server.\" >&2; exit 125; }\n"
            + "trap 'rm -rf \"$d\"' EXIT\ncd \"$d\" || exit 125\n"
            + $"command -v {runtime} >/dev/null 2>&1 || {{ echo \"Lucia: {runtime} isn't installed on this server.\" >&2; exit 127; }}\n"
            + $"base64 -d <<'LUCIA_FILE' | gunzip > {file} || {{ echo \"Lucia: could not unpack the file.\" >&2; exit 125; }}\n{gzipBase64}\nLUCIA_FILE\n"
            + $"{check} 2>&1\nexit $?\n";
    }

    public async Task<SkillCheckResult> CheckAsync(string path, string content, string? node, CancellationToken ct, string? skill = null)
    {
        var extension = Path.GetExtension(path);
        // C# has no runtime on a node, and a Spark-mode skill is checked where it runs: both go to the Spark's container.
        if (SkillLanguages.IsScript(path) && (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || (skill is not null && store.SettingsOf(skill).Execution == SkillExecution.Spark)))
            return skill is null ? new(false, [], null, "Name the skill to check this file.") : await spark.CheckAsync(skill, path, content, ct);
        if (SkillLanguages.Language(path) is null or "markdown" or "json" or "yaml" or "text" || !SkillLanguages.IsScript(path))
            return new(false, [], null, "Only scripts have a syntax check.");
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(Encoding.UTF8.GetBytes(content));
        var command = Command(extension, Convert.ToBase64String(packed.ToArray()))!;
        if (command.Length > NodeRequests.MaxCommandChars)
            return new(false, [], null, $"This file is too big to check on a server (about {packed.Length / 1024.0:0.#} KiB packed; the limit is roughly 11 KiB).");
        try
        {
            (Guid Id, string Hostname) target;
            if (node is { Length: > 0 }) target = await exec.Node(node, ct);
            else target = await exec.FirstWithExec(ct)
                ?? throw new AssistantException(409, "no_exec_node", "No managed server can run commands yet, so there is nowhere to check the script.");
            var result = await exec.Exec(target.Id, target.Hostname, command, "bash", Seconds, ct);
            var output = result.Output ?? "";
            if (result.ExitCode == 0) return new(true, [], target.Hostname, null);
            if (result.ExitCode == 127 && output.Contains("isn't installed", StringComparison.Ordinal))
                return new(false, [], target.Hostname, output.Replace("Lucia: ", "").Trim());
            if (result.ExitCode is null) return new(false, [], target.Hostname, "The check didn't finish.");
            return new(false, Parse(extension, output), target.Hostname, null);
        }
        catch (Exception e) when (e is AssistantException or HardwareOnboardingException)
        {
            return new(false, [], null, e.Message);
        }
    }

    /// <summary>Reads a compiler's complaint into markers: the line it names and its message, when it names one.</summary>
    public static SkillDiagnostic[] Parse(string extension, string output)
    {
        var lines = output.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var diagnostics = new List<SkillDiagnostic>();
        switch (extension.ToLowerInvariant())
        {
            case ".py":
                var at = PythonLine().Match(output);
                var error = lines.LastOrDefault(line => line.Contains("Error", StringComparison.Ordinal)) ?? "Syntax error.";
                if (at.Success) diagnostics.Add(Marker(int.Parse(at.Groups[1].Value), error));
                break;
            case ".sh":
                foreach (var line in lines)
                    if (BashLine().Match(line) is { Success: true } match) diagnostics.Add(Marker(int.Parse(match.Groups[1].Value), match.Groups[2].Value));
                break;
            default:
                var node = NodeLine().Match(output);
                var complaint = lines.FirstOrDefault(line => line.Contains("Error:", StringComparison.Ordinal)) ?? "Syntax error.";
                if (node.Success) diagnostics.Add(Marker(int.Parse(node.Groups[1].Value), complaint));
                break;
        }
        if (diagnostics.Count == 0)
            diagnostics.Add(Marker(1, lines.Length > 0 ? lines[^1] : "The syntax check failed."));
        return [.. diagnostics.Take(20)];
    }

    private static SkillDiagnostic Marker(int line, string message) =>
        new(Math.Max(1, line), 1, null, null, "error", message.Length <= 300 ? message.Trim() : message[..300].Trim());

    [GeneratedRegex(@"File "".*?"", line (\d+)", RegexOptions.None, 1000)]
    private static partial Regex PythonLine();

    [GeneratedRegex(@"line (\d+): (.+)", RegexOptions.None, 1000)]
    private static partial Regex BashLine();

    [GeneratedRegex(@"^\S*check\.\w+:(\d+)\s*$", RegexOptions.Multiline, 1000)]
    private static partial Regex NodeLine();
}
