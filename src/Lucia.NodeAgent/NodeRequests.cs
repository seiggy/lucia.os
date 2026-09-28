using System.Text;
using System.Text.RegularExpressions;

namespace Lucia.NodeAgent;

internal sealed record NodeRequest(Guid RequestId, string Kind, string Container, int Tail);
internal sealed record NodeRequestResult(Guid RequestId, bool Success, string? Output, string? Message);

/// <summary>
/// Answers read-only questions from Lucia over a long-poll. Only allowlisted kinds with validated arguments run, as
/// fixed argument arrays; the server can't send commands.
/// </summary>
internal static partial class NodeRequests
{
    internal const int MaxOutputChars = 96 * 1024;

    internal static async Task RunAsync(DiscoveryClient client, Guid node, Func<string> certificate, System.Security.Cryptography.ECDsa key,
        CancellationToken token)
    {
        string? lastError = null;
        while (!token.IsCancellationRequested)
        {
            if (NodeRuntime.Current.State != "Ready")
            {
                await Task.Delay(TimeSpan.FromSeconds(15), token);
                continue;
            }
            try
            {
                foreach (var request in await client.RequestsAsync(node, certificate(), key, token))
                    _ = Task.Run(async () =>
                    {
                        try { await client.AnswerAsync(node, certificate(), await AnswerAsync(request, token), key, token); }
                        catch (Exception ex) when (ex is NodeAgentException or OperationCanceledException)
                        { Console.Error.WriteLine("A node request could not be answered. " + ex.Message); }
                    }, token);
                lastError = null;
            }
            catch (Exception ex) when (ex is NodeAgentException || ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                // Older servers don't have the channel; don't hammer them.
                if (ex.Message != lastError) Console.Error.WriteLine("Node request channel unavailable; retrying in a minute. " + ex.Message);
                lastError = ex.Message;
                await Task.Delay(TimeSpan.FromMinutes(1), token);
            }
        }
    }

    internal static async Task<NodeRequestResult> AnswerAsync(NodeRequest request, CancellationToken token)
    {
        if (request.Kind != "logs" || request.Container is null || !ContainerPattern().IsMatch(request.Container) || request.Tail is < 1 or > 5000)
            return new(request.RequestId, false, null, "This node doesn't accept that request.");
        try
        {
            var (exit, stdout, stderr) = await Commands.CaptureAsync("/usr/bin/docker",
                ["logs", "--tail", request.Tail.ToString(System.Globalization.CultureInfo.InvariantCulture), "--timestamps", "--", request.Container],
                TimeSpan.FromSeconds(15), token, 1024 * 1024, failOnError: false);
            if (exit != 0)
                return new(request.RequestId, false, null, StackRunner.Bounded(stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(), 500)
                    ?? "docker logs failed.");
            return new(request.RequestId, true, MergeLogs(stdout, stderr), null);
        }
        catch (NodeAgentException ex) { return new(request.RequestId, false, null, ex.Message); }
    }

    /// <summary>Interleaves a container's stdout and stderr by Docker's timestamp prefix and keeps the newest text.</summary>
    internal static string MergeLogs(string stdout, string stderr)
    {
        var lines = stdout.Split('\n').Concat(stderr.Split('\n')).Where(line => line.Length > 0)
            .Select((line, index) => (Key: SortKey(line), Index: index, Line: Clean(line)))
            .OrderBy(item => item.Key, StringComparer.Ordinal).ThenBy(item => item.Index).Select(item => item.Line);
        var text = string.Join('\n', lines);
        if (text.Length <= MaxOutputChars) return text;
        var tail = text[^MaxOutputChars..];
        var start = tail.IndexOf('\n');
        return start >= 0 ? tail[(start + 1)..] : tail;
    }

    // Docker prints RFC 3339 with trailing zeros trimmed, so pad the fraction for a sortable key.
    private static string SortKey(string line)
    {
        var match = TimestampPattern().Match(line);
        return match.Success ? match.Groups[1].Value + "." + match.Groups[2].Value.PadRight(9, '0') : "";
    }

    private static string Clean(string line)
    {
        var text = AnsiPattern().Replace(line, "");
        var clean = new StringBuilder(text.Length);
        foreach (var c in text) if (c == '\t' || !char.IsControl(c)) clean.Append(c);
        return clean.ToString();
    }

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z")]
    private static partial Regex ContainerPattern();
    [GeneratedRegex(@"\A(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d{1,9}))?Z")]
    private static partial Regex TimestampPattern();
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex AnsiPattern();
}
