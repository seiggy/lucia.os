using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Lucia.Homelab.Server.Onboarding;

namespace Lucia.Homelab.Server.Stacks;

public sealed record NodeRequest(Guid RequestId, string Kind, string Container, int Tail);
public sealed record NodeRequestResult(Guid RequestId, bool Success, string? Output, string? Message);

/// <summary>
/// Requests for a node, delivered over the node's long-poll so answers come back in about a second. The agent accepts
/// only the kinds it knows with validated arguments: container logs, and the owner's machine actions (check or install
/// Debian updates, restart, update the agent). Nothing here carries a command.
/// </summary>
public sealed partial class NodeRequests
{
    public const int MaxTail = 5000, MaxOutputChars = 128 * 1024;
    public static readonly string[] Actions = ["check-updates", "install-updates", "restart", "update-agent"];
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(20), PollHold = TimeSpan.FromSeconds(25);
    private readonly ConcurrentDictionary<Guid, Channel<NodeRequest>> _queues = new();
    private readonly ConcurrentDictionary<Guid, (Guid Node, TaskCompletionSource<NodeRequestResult> Answer)> _pending = new();

    public Task<NodeRequestResult> Logs(Guid node, string container, int tail, CancellationToken ct)
    {
        if (!ContainerPattern().IsMatch(container) || tail is < 1 or > MaxTail)
            throw new HardwareOnboardingException(400, "invalid_logs_request", $"Name a container and ask for 1–{MaxTail} lines.");
        return Ask(node, new(Guid.NewGuid(), "logs", container, tail), ct);
    }

    /// <summary>Starts one of <see cref="Actions"/>; the node answers once it has started, and reports progress in its heartbeat.</summary>
    public async Task<object> Act(Guid node, string action, CancellationToken ct)
    {
        if (!Actions.Contains(action)) throw new HardwareOnboardingException(404, "unknown_action", "Servers don't have that action.");
        var result = await Ask(node, new(Guid.NewGuid(), action, "", 0), ct);
        return result.Success ? new { message = result.Message }
            : throw new HardwareOnboardingException(409, "action_refused", result.Message ?? "The server refused that action.");
    }

    private async Task<NodeRequestResult> Ask(Guid node, NodeRequest request, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<NodeRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.RequestId] = (node, answer);
        try
        {
            if (!Queue(node).Writer.TryWrite(request))
                throw new HardwareOnboardingException(503, "node_busy", "This node has too many pending requests. Try again shortly.");
            return await answer.Task.WaitAsync(AnswerTimeout, ct);
        }
        catch (TimeoutException)
        {
            throw new HardwareOnboardingException(504, "node_no_answer",
                "The node didn't answer. It may be offline or running an older agent.");
        }
        finally { _pending.TryRemove(request.RequestId, out _); }
    }

    /// <summary>Holds a node's poll until it has something to answer, or returns empty after the hold.</summary>
    public async Task<NodeRequest[]> Wait(Guid node, CancellationToken ct)
    {
        var reader = Queue(node).Reader;
        using var hold = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hold.CancelAfter(PollHold);
        var requests = new List<NodeRequest>();
        try
        {
            while (requests.Count == 0)
            {
                var first = await reader.ReadAsync(hold.Token);
                if (_pending.ContainsKey(first.RequestId)) requests.Add(first);
            }
            while (requests.Count < 8 && reader.TryRead(out var more))
                if (_pending.ContainsKey(more.RequestId)) requests.Add(more);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return [.. requests];
    }

    public void Complete(Guid node, NodeRequestResult result)
    {
        HardwareInventoryValidation.Require(result.Output is null || result.Output.Length <= MaxOutputChars, "The node answer is oversized.");
        HardwareInventoryValidation.Text(result.Message, 1024);
        if (_pending.TryGetValue(result.RequestId, out var pending) && pending.Node == node)
            pending.Answer.TrySetResult(result);
    }

    private Channel<NodeRequest> Queue(Guid node) => _queues.GetOrAdd(node, _ =>
        Channel.CreateBounded<NodeRequest>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false }));

    /// <summary>A Docker container name or ID.</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z")]
    internal static partial Regex ContainerPattern();
}
