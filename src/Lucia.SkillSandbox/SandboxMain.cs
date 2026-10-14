using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI.Hyperlight;
using Microsoft.Extensions.AI;

namespace Lucia.SkillSandbox;

/// <summary>What the guest reported: its exit code and the text it wrote.</summary>
internal sealed record GuestRun(int ExitCode, string Output);

/// <summary>
/// Runs one skill script in a Hyperlight micro-VM. The Agent Framework's <see cref="HyperlightExecuteCodeFunction"/> does the work:
/// the skill folder is exposed read-only as /input, outbound requests are limited to the skill's allowed domains and the script
/// source goes in as the <c>code</c> argument. Reads one request from stdin and writes one JSON result to stdout.
/// </summary>
internal static partial class SandboxMain
{
    public const string SkillRootVariable = "LUCIA_SKILL_ROOT", DefaultSkillRoot = "/skill";
    public const int MaxOutputBytes = 48 * 1024;
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Handles the request on <paramref name="input"/>, writes the JSON result and returns the process exit code.</summary>
    public static async Task<int> RunAsync(TextReader input, TextWriter output)
    {
        var root = Environment.GetEnvironmentVariable(SkillRootVariable) is { Length: > 0 } custom ? custom : DefaultSkillRoot;
        var result = await HandleAsync(await ReadBounded(input), root, ExecuteAsync);
        output.WriteLine(JsonSerializer.Serialize(result, Json));
        output.Flush();
        return result.ExitCode == 0 ? 0 : 1;
    }

    internal sealed record SandboxResult(int ExitCode, string Output, bool Truncated, string? Error);

    internal delegate Task<GuestRun> Executor(SandboxRequest request, string root, string code, CancellationToken token);

    /// <summary>Validates the request and the script, runs it with <paramref name="executor"/> and shapes the result. Never throws.</summary>
    internal static async Task<SandboxResult> HandleAsync(string? requestJson, string root, Executor executor)
    {
        if (requestJson is null) return Failure($"The sandbox request is larger than {SandboxRequest.MaxRequestBytes / 1024} KiB.");
        if (SandboxRequest.Parse(requestJson, out var problem) is not { } request) return Failure(problem);
        var code = LoadScript(root, request, out problem);
        if (code is null) return Failure(problem);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds));
        try
        {
            // The guest runs inside a blocking native call that cancellation can't interrupt, so it gets its own thread and a deadline.
            var source = Preamble(request) + code;
            var run = await Task.Run(() => executor(request, root, source, cancel.Token)).WaitAsync(
                TimeSpan.FromSeconds(request.TimeoutSeconds) + Grace);
            var (text, truncated) = Bound(run.Output);
            return new(run.ExitCode, text, truncated, null);
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException)
        {
            return Failure($"The script ran past its {request.TimeoutSeconds}-second limit and was stopped.", 124);
        }
        catch (Exception e)
        {
            return Failure(Describe(e));
        }
    }

    private static SandboxResult Failure(string error, int exitCode = 1) => new(exitCode, "", false, error);

    /// <summary>Reads up to the request limit from the reader; null when there is more.</summary>
    internal static async Task<string?> ReadBounded(TextReader input)
    {
        var buffer = new char[SandboxRequest.MaxRequestBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await input.ReadAsync(buffer.AsMemory(total))) > 0) total += read;
        return total > SandboxRequest.MaxRequestBytes ? null : new string(buffer, 0, total);
    }

    /// <summary>The script's source, or null with the reason. The file must be a regular file inside the skill folder, not a link.</summary>
    internal static string? LoadScript(string root, SandboxRequest request, out string error)
    {
        error = "";
        try
        {
            var skill = Path.GetFullPath(root).TrimEnd('/', '\\');
            var path = Path.GetFullPath(Path.Combine(skill, request.Script));
            if (!path.StartsWith(skill + Path.DirectorySeparatorChar, StringComparison.Ordinal)) { error = "The script is outside the skill."; return null; }
            var info = new FileInfo(path);
            if (!info.Exists) { error = $"The skill has no {request.Script}."; return null; }
            for (var folder = info.Directory; folder is not null && folder.FullName.TrimEnd('/', '\\') != skill; folder = folder.Parent)
                if (folder.LinkTarget is not null) { error = "The script is behind a link, which the sandbox won't follow."; return null; }
            if (info.LinkTarget is not null) { error = "The script is a link, which the sandbox won't follow."; return null; }
            if (info.Length > SandboxRequest.MaxScriptBytes) { error = "The script is larger than the sandbox accepts."; return null; }
            return File.ReadAllText(path, new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = "The script could not be read.";
            return null;
        }
    }

    /// <summary>
    /// Defines <c>LUCIA_SKILL_ARGS</c>, the arguments object, in the script's first line. It adds no line break, so every later line
    /// keeps its number in error messages. A Python script that opens with <c>from __future__</c> must not use arguments.
    /// </summary>
    internal static string Preamble(SandboxRequest request) => request.Language == SandboxRequest.Python
        ? $"import json as _lucia_json; LUCIA_SKILL_ARGS = _lucia_json.loads(r'{request.ArgumentsJson}'); "
        : $"const LUCIA_SKILL_ARGS = {request.ArgumentsJson}; ";

    /// <summary>The last <see cref="MaxOutputBytes"/> bytes of <paramref name="text"/>, cut on a character boundary.</summary>
    internal static (string Text, bool Truncated) Bound(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= MaxOutputBytes) return (text, false);
        var start = bytes.Length - MaxOutputBytes;
        while (start < bytes.Length && (bytes[start] & 0xC0) == 0x80) start++;
        return (Encoding.UTF8.GetString(bytes, start, bytes.Length - start), true);
    }

    /// <summary>One line about a failure, without host paths or stack frames.</summary>
    internal static string Describe(Exception e)
    {
        var line = (e.Message ?? "").Split('\n')[0];
        line = PathPattern().Replace(line, "<path>");
        line = line.Length > 300 ? line[..300] : line;
        return line.Length == 0 ? "The sandbox failed to run the script." : "The sandbox failed: " + line;
    }

    [GeneratedRegex(@"(?:[A-Za-z]:)?(?:[/\\][\w.\-]+){2,}")]
    private static partial Regex PathPattern();

    private static async Task<GuestRun> ExecuteAsync(SandboxRequest request, string root, string code, CancellationToken token)
    {
        var options = request.Language == SandboxRequest.Python
            ? HyperlightCodeActProviderOptions.CreateForWasm(PythonGuest())
            : HyperlightCodeActProviderOptions.CreateForJavaScript();
        // The skill folder is the sandbox's /input, which the guest can read but not change.
        options.HostInputDirectory = root;
        options.AllowedDomains = [.. request.AllowedDomains.Select(domain => new AllowedDomain(domain))];
        options.ApprovalMode = CodeActApprovalMode.NeverRequire;
        if (request.HeapSize is not null) options.HeapSize = request.HeapSize;
        using var function = new HyperlightExecuteCodeFunction(options);
        var result = await function.InvokeAsync(new AIFunctionArguments { ["code"] = code }, token);
        return Parse(result?.ToString());
    }

    private static string PythonGuest()
    {
        foreach (var path in new[] { Path.Combine(AppContext.BaseDirectory, "python-sandbox.aot"),
            Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", "python-sandbox.aot") })
            if (File.Exists(path)) return path;
        throw new FileNotFoundException("The Python guest module is missing from this sandbox build.");
    }

    /// <summary>The function answers {"stdout","stderr","exit_code","success"}; stderr follows stdout.</summary>
    internal static GuestRun Parse(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return new(0, "");
        try
        {
            using var document = JsonDocument.Parse(answer);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(0, answer);
            var stdout = root.TryGetProperty("stdout", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : "";
            var stderr = root.TryGetProperty("stderr", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
            var success = !root.TryGetProperty("success", out var s) || s.ValueKind != JsonValueKind.False;
            var code = root.TryGetProperty("exit_code", out var c) && c.TryGetInt32(out var number) ? number : success ? 0 : 1;
            if (!success && code == 0) code = 1;
            var text = stderr.Length == 0 ? stdout : stdout.Length == 0 ? stderr : stdout.TrimEnd('\n') + "\n" + stderr;
            return new(code, text);
        }
        catch (JsonException) { return new(0, answer); }
    }
}
