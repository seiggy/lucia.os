using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

namespace Lucia.NodeAgent;

/// <summary>
/// Runs commands the owner approved in Lucia's assistant, as root, each in its own transient systemd unit
/// (<c>lucia-exec-&lt;job&gt;</c>) with a hard time limit. Output goes to the journal and the exit code stays in memory, so a
/// command outlives an agent restart but its exit code doesn't. Stdin is empty, and whatever it leaves in the background
/// stops with it.
/// </summary>
internal static partial class NodeExec
{
    internal const int MaxScript = 16 * 1024, MaxSeconds = 1800, MaxOutput = 32 * 1024, MaxRunning = 4;
    /// <summary>Tells Lucia, in the heartbeat, that this agent runs commands.</summary>
    internal static readonly string[] Features = ["exec"];
    private const string ScriptDirectory = "/var/lib/lucia-agent/exec";
    // Lucia waits 20 seconds for an answer.
    private static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(10);
    private static readonly Dictionary<string, (string Executable, string Extension)> Interpreters = new(StringComparer.Ordinal)
    {
        ["bash"] = ("/bin/bash", ".sh"),
        ["sh"] = ("/bin/sh", ".sh"),
        ["python3"] = ("/usr/bin/python3", ".py"),
    };
    private static readonly Dictionary<Guid, Job> Jobs = [];

    private sealed record Job(DateTimeOffset Started, Task<(int? Exit, string? Message)> Run);

    /// <summary>
    /// <c>exec</c> starts a command under its request id and answers like <c>exec-status</c>: within about ten seconds, with
    /// the output so far and the exit code once it has finished. <c>exec-stop</c> stops one.
    /// </summary>
    internal static async Task<NodeRequestResult> AnswerAsync(NodeRequest request, CancellationToken token)
    {
        if (Refusal(request) is { } refusal) return new(request.RequestId, false, null, refusal);
        try
        {
            if (request.Kind == "exec-stop")
            {
                var (exit, _, _) = await Commands.CaptureAsync("/usr/bin/systemctl", ["stop", "--no-block", Unit(request.Job!.Value) + ".service"],
                    TimeSpan.FromSeconds(30), token, 4096, failOnError: false);
                return new(request.RequestId, true, null, exit == 0 ? "Stopping it." : "It isn't running.");
            }
            if (request.Kind == "exec" && Start(request, token) is { } failure) return new(request.RequestId, false, null, failure);
            return await StatusAsync(request.RequestId, request.Job ?? request.RequestId, token);
        }
        catch (Exception ex) when (ex is NodeAgentException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new(request.RequestId, false, null, StackRunner.Bounded(ex.Message, 500));
        }
    }

    /// <summary>Why this node won't take <paramref name="request"/>, or null when it will.</summary>
    internal static string? Refusal(NodeRequest request) => request.Kind switch
    {
        "exec" when request.Command is not { Length: > 0 and <= MaxScript } || request.Command.Contains('\0') =>
            $"Commands are 1 to {MaxScript} characters.",
        "exec" when request.Interpreter is null || !Interpreters.ContainsKey(request.Interpreter) => "Run commands with bash, sh or python3.",
        "exec" when request.Timeout is not (>= 1 and <= MaxSeconds) => $"Give a command 1 to {MaxSeconds} seconds.",
        "exec" => null,
        "exec-status" or "exec-stop" => request.Job is null ? "Say which command." : null,
        _ => "This node doesn't accept that request.",
    };

    /// <summary>Starts a command in the background, or says why it can't. A repeated request is already running.</summary>
    private static string? Start(NodeRequest request, CancellationToken token)
    {
        var job = request.RequestId;
        var (executable, extension) = Interpreters[request.Interpreter!];
        if (!File.Exists(executable)) return $"{request.Interpreter} isn't installed on this server.";
        lock (Jobs)
        {
            if (Jobs.ContainsKey(job)) return null;
            foreach (var old in Jobs.Where(item => item.Value.Run.IsCompleted && item.Value.Started < DateTimeOffset.UtcNow.AddHours(-1)).ToArray())
                Jobs.Remove(old.Key);
            if (Jobs.Values.Count(item => !item.Run.IsCompleted) >= MaxRunning)
                return $"This server is already running {MaxRunning} of the assistant's commands. Wait for one to finish.";
            SecureStateDirectory.MakePrivateDirectory(ScriptDirectory);
            var script = $"{ScriptDirectory}/{job:N}{extension}";
            SecureStateDirectory.WriteSystemFile(script, Encoding.UTF8.GetBytes(request.Command!));
            Console.WriteLine($"Running a command the owner approved in Lucia's assistant as {Unit(job)} "
                + $"({request.Interpreter}, {request.Timeout}-second limit).");
            var seconds = request.Timeout!.Value;
            Jobs[job] = new(DateTimeOffset.UtcNow, Task.Run(() => RunAsync(Unit(job), executable, script, seconds, token), CancellationToken.None));
        }
        return null;
    }

    private static async Task<(int? Exit, string? Message)> RunAsync(string unit, string executable, string script, int seconds,
        CancellationToken token)
    {
        try
        {
            var (exit, stdout, stderr) = await Commands.CaptureAsync("/usr/bin/systemd-run",
                [$"--unit={unit}", "--collect", "--wait", $"--property=RuntimeMaxSec={seconds}", "--property=TimeoutStopSec=15",
                    "--setenv=HOME=/root", "--setenv=DEBIAN_FRONTEND=noninteractive", "--", executable, script],
                TimeSpan.FromSeconds(seconds + 120), token, 16 * 1024, failOnError: false);
            return Outcome(exit, stdout + "\n" + stderr, seconds);
        }
        catch (Exception ex) when (ex is NodeAgentException or IOException or Win32Exception) { return (null, ex.Message); }
        finally
        {
            try { SecureStateDirectory.DeleteSystemFile(script); }
            catch (Exception ex) when (ex is NodeAgentException or IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Reads <c>systemd-run --wait</c>'s report: the exit code when the command ended by itself, otherwise why it didn't. A
    /// stopped command reports success with exit 0, so a kill counts first.
    /// </summary>
    internal static (int? Exit, string? Message) Outcome(int exit, string report, int seconds)
    {
        var result = ResultPattern().Match(report);
        if (!result.Success)
            return (null, StackRunner.Bounded(report.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(), 500)
                ?? $"systemd-run exited with code {exit}.");
        var main = MainPattern().Match(report);
        return (result.Groups[1].Value, main.Groups[1].Value) switch
        {
            ("timeout", _) => (null, $"It hit its {seconds}-second limit and was stopped."),
            ("oom-kill", _) => (null, "It ran out of memory and was killed."),
            (_, "killed" or "dumped") => (null, $"It was killed by SIG{main.Groups[2].Value}."),
            ("success" or "exit-code", _) => (exit, null),
            (var other, _) => (null, $"systemd ended it with result {other}."),
        };
    }

    private static async Task<NodeRequestResult> StatusAsync(Guid requestId, Guid job, CancellationToken token)
    {
        var unit = Unit(job);
        Job? known;
        lock (Jobs) known = Jobs.GetValueOrDefault(job);
        bool running;
        (int? Exit, string? Message) outcome = default;
        if (known is not null)
        {
            await Task.WhenAny(known.Run, Task.Delay(AnswerWait, token));
            running = !known.Run.IsCompleted;
            if (!running)
                outcome = known.Run.IsCompletedSuccessfully ? known.Run.Result : (null, "Lucia's agent stopped while it ran, so its exit code is unknown.");
        }
        else
        {
            var until = DateTimeOffset.UtcNow + AnswerWait;
            while ((running = await ActiveAsync(unit, token)) && DateTimeOffset.UtcNow < until) await Task.Delay(TimeSpan.FromSeconds(2), token);
            if (!running) outcome = (null, "Lucia's agent restarted while it ran, or never ran it, so its exit code is unknown.");
        }
        return new(requestId, true, await OutputAsync(unit, token), outcome.Message, outcome.Exit, running);
    }

    private static async Task<bool> ActiveAsync(string unit, CancellationToken token) =>
        (await Commands.CaptureAsync("/usr/bin/systemctl", ["is-active", "--quiet", unit + ".service"],
            TimeSpan.FromSeconds(15), token, 1024, failOnError: false)).Exit == 0;

    private static async Task<string> OutputAsync(string unit, CancellationToken token)
    {
        var (_, stdout, _) = await Commands.CaptureAsync("/usr/bin/journalctl",
            [$"_SYSTEMD_UNIT={unit}.service", "--output=cat", "--no-pager", "--quiet", "--lines=2000"],
            TimeSpan.FromSeconds(15), token, 8 * MaxOutput, failOnError: false);
        return Tail(stdout);
    }

    /// <summary>The newest output without terminal escapes, cut at a line start to about <see cref="MaxOutput"/> characters.</summary>
    internal static string Tail(string journal)
    {
        var text = string.Join('\n', journal.Split('\n').Select(NodeRequests.Clean)).TrimEnd();
        if (text.Length <= MaxOutput) return text;
        var tail = text[^MaxOutput..];
        var start = tail.IndexOf('\n');
        return "[Earlier output cut.]\n" + (start >= 0 ? tail[(start + 1)..] : tail);
    }

    private static string Unit(Guid job) => "lucia-exec-" + job.ToString("N");

    [GeneratedRegex(@"^Finished with result: (\S+)", RegexOptions.Multiline)]
    private static partial Regex ResultPattern();
    [GeneratedRegex(@"^Main processes terminated with: code=(\w+)/status=(\w+)", RegexOptions.Multiline)]
    private static partial Regex MainPattern();
}
