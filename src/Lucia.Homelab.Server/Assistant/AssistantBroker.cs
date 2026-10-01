using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentGovernance;
using AgentGovernance.Policy;
using GitHub.Copilot;
using Lucia.Homelab.Server.Domains;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using PermissionDecision = GitHub.Copilot.Rpc.PermissionDecision;

namespace Lucia.Homelab.Server.Assistant;

/// <summary>
/// How much a tool can do. The tier, the chat's mode and the owner's settings decide whether a call runs, asks or is refused.
/// A secret tool asks the owner to type a value into an app: its card in the chat is the consent, and Plan mode refuses it.
/// </summary>
public static class ToolTier
{
    public const string Read = "read", Web = "web", Change = "change", Destructive = "destructive", Secret = "secret";
}

/// <summary>The owner's standing choices: change tools that run without asking, and sites the assistant may read without asking.</summary>
public sealed record AssistantSettings(string[] AutoTools, string[] Hosts)
{
    public static readonly AssistantSettings Default = new([], ["docs.docker.com", "hub.docker.com", "github.com", "raw.githubusercontent.com"]);
}

/// <summary>One turn's tools and the permission handler that governs them.</summary>
#pragma warning disable GHCP001 // The SDK marks its permission decision as evaluation-only, but it is the handler's required result.
public sealed record AssistantKit(IReadOnlyList<AIFunction> Tools, Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> Permission);
#pragma warning restore GHCP001

/// <summary>
/// Decides every tool call the assistant makes. Lucia's policy, evaluated by the Agent Governance Toolkit, sorts a call into
/// run, ask or refuse; asking shows the owner an approval in the chat and holds the call until they answer. "Allow for this
/// chat" grants live in memory, so they end with the chat or a restart. Decisions are logged, and counted on <see cref="GovernanceMeter"/>.
/// </summary>
public sealed class AssistantBroker(IOptions<AssistantOptions> options, ILogger<AssistantBroker> logger) : IDisposable
{
    public const string GovernanceMeter = "AgentGovernance";
    public const int MaxCallsPerTurn = 50, MaxHosts = 50;
    private static readonly TimeSpan AnswerWait = TimeSpan.FromMinutes(30);

    // First match by priority; anything no rule covers is refused.
    private const string Policy = """
        apiVersion: governance.toolkit/v1
        version: "1.0"
        name: lucia-assistant
        default_action: deny
        rules:
          - name: plan-mode-changes-nothing
            condition: "mode == 'plan' and tier == 'change' or mode == 'plan' and tier == 'destructive' or mode == 'plan' and tier == 'secret'"
            action: deny
            priority: 100
          - name: destructive-always-asks
            condition: "tier == 'destructive'"
            action: require_approval
            priority: 90
          - name: owner-types-secrets
            condition: "tier == 'secret'"
            action: allow
            priority: 85
          - name: reads-run
            condition: "tier == 'read'"
            action: allow
            priority: 80
          - name: web-allowed-host
            condition: "tier == 'web' and host_allowed"
            action: allow
            priority: 70
          - name: web-other-host
            condition: "tier == 'web'"
            action: require_approval
            priority: 60
          - name: change-automatic
            condition: "tier == 'change' and autonomy == 'auto'"
            action: allow
            priority: 50
          - name: change-asks
            condition: "tier == 'change'"
            action: require_approval
            priority: 40
        """;

    private readonly GovernanceKernel _kernel = Kernel();
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _grants = new();
    private readonly ConcurrentDictionary<string, Question> _questions = new();

    private sealed record Pending(string SessionKey, string? Grant, TaskCompletionSource<(bool Approved, string? Reason)> Answer);

    private sealed record Question(string Kind, TaskCompletionSource<string?> Answer);

    private enum Verdict { Allow, Ask, Deny }

    public AssistantKit Kit(AssistantTurn turn, AssistantSettings settings, IReadOnlyList<AssistantTool> tools, AssistantStream stream, AssistantRun run)
    {
        var tiers = tools.ToDictionary(tool => tool.Name, tool => tool.Tier);
        var calls = 0;
        return new(tools, async (request, invocation) =>
        {
            if (request is not PermissionRequestCustomTool call || !tiers.TryGetValue(call.ToolName, out var tier))
                return PermissionDecision.Reject("Lucia's assistant can only use its own tools.");
            if (Interlocked.Increment(ref calls) > MaxCallsPerTurn)
                return PermissionDecision.Reject($"This turn has used its {MaxCallsPerTurn} tool calls. Summarize what you found and ask the owner how to go on.");
            var input = call.Args is { ValueKind: not JsonValueKind.Undefined } args ? JsonNode.Parse(args.GetRawText()) : null;
            string? host = null;
            if (tier == ToolTier.Web)
            {
                if (input is not JsonObject fields || fields["url"] is not JsonValue value || !value.TryGetValue(out string? url)
                    || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    return PermissionDecision.Reject("Give an absolute http or https URL.");
                host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
            }
            var grant = tier switch { ToolTier.Change => "tool:" + call.ToolName, ToolTier.Web => "host:" + host, _ => null };
            var granted = grant is not null && _grants.TryGetValue(turn.SessionKey, out var grants) && grants.ContainsKey(grant);
            var automatic = settings.AutoTools.Contains(call.ToolName);
            var listed = host is not null && settings.Hosts.Contains(host);
            var (verdict, rule) = Decide(turn, call.ToolName, tier, autonomy: automatic || granted ? "auto" : "ask", hostAllowed: listed || granted);
            logger.LogInformation("Assistant tool call {Tool} ({Tier}, {Mode} mode): {Verdict} by {Rule}.",
                call.ToolName, tier, turn.Mode, verdict, rule ?? "default");

            var approvalId = "approval-" + Guid.NewGuid().ToString("N");
            var toolCallId = call.ToolCallId is { Length: > 0 } id ? id : approvalId;
            if (verdict == Verdict.Allow && tier is ToolTier.Read or ToolTier.Secret) return PermissionDecision.ApproveOnce();
            if (verdict != Verdict.Ask)
            {
                var allowed = verdict == Verdict.Allow;
                var why = !allowed ? rule == "plan-mode-changes-nothing" ? "Plan mode doesn't change anything. Switch to Execute to run it." : "Lucia's policy doesn't allow this."
                    : granted ? "You allowed this for this chat."
                    : host is not null ? $"{host} is on the assistant's allowed sites."
                    : "Runs automatically in your assistant settings.";
                lock (stream)
                {
                    run.Publish(stream.Ask(toolCallId, call.ToolName, input, approvalId, null, automatic: true));
                    run.Publish(stream.Answer(approvalId, allowed, why));
                }
                return allowed ? PermissionDecision.ApproveOnce()
                    : PermissionDecision.Reject(rule == "plan-mode-changes-nothing"
                        ? "Refused: the chat is in Plan mode, which changes nothing. Put this step in your plan; the owner can switch to Execute to run it."
                        : "Refused by Lucia's policy.");
            }

            var reason = tier switch
            {
                ToolTier.Destructive => AssistantTools.Warning(call.ToolName, input),
                ToolTier.Web => $"{host} isn't on the assistant's allowed sites.",
                _ => "This changes your lab.",
            };
            var pending = new Pending(turn.SessionKey, grant, new(TaskCreationOptions.RunContinuationsAsynchronously));
            _pending[approvalId] = pending;
            try
            {
                lock (stream) run.Publish(stream.Ask(toolCallId, call.ToolName, input, approvalId, reason, automatic: false));
                var (approved, answer) = await pending.Answer.Task.WaitAsync(AnswerWait, run.Stop.Token);
                lock (stream) run.Publish(stream.Answer(approvalId, approved, answer));
                return approved ? PermissionDecision.ApproveOnce()
                    : PermissionDecision.Reject(answer is null ? "The owner declined this tool call." : $"The owner declined this tool call: {answer}");
            }
            catch (TimeoutException)
            {
                lock (stream) run.Publish(stream.Answer(approvalId, false, "No answer within 30 minutes."));
                return PermissionDecision.Reject("The owner didn't answer within 30 minutes, so the call didn't run.");
            }
            catch (OperationCanceledException) { return PermissionDecision.Reject("The owner stopped this turn."); }
            finally { _pending.TryRemove(approvalId, out _); }
        });
    }

    /// <summary>Answers a waiting approval. False when it was already answered, has expired, or belongs to another chat.</summary>
    public bool Respond(string sessionKey, string approvalId, bool approved, string? reason, bool always)
    {
        if (!_pending.TryGetValue(approvalId, out var pending) || pending.SessionKey != sessionKey
            || !pending.Answer.TrySetResult((approved, reason))) return false;
        if (approved && always && pending.Grant is { } grant) _grants.GetOrAdd(sessionKey, _ => new()).TryAdd(grant, 0);
        return true;
    }

    public void Forget(string sessionKey) => _grants.TryRemove(sessionKey, out _);

    /// <summary>
    /// Holds a question tool's call until the owner answers its card in the chat: <paramref name="kind"/> is "answer" or
    /// "secret". Null when they decline. Stopping the turn cancels the call, which ends the wait.
    /// </summary>
    public async Task<string?> AskAsync(string sessionKey, string toolCallId, string kind, CancellationToken ct)
    {
        var key = sessionKey + "/" + toolCallId;
        var question = new Question(kind, new(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_questions.TryAdd(key, question)) throw new AssistantException(409, "question_pending", "This question is already waiting for the owner.");
        try { return await question.Answer.Task.WaitAsync(AnswerWait, ct); }
        catch (TimeoutException) { throw new AssistantException(408, "no_answer", "The owner didn't answer within 30 minutes."); }
        finally { _questions.TryRemove(key, out _); }
    }

    /// <summary>
    /// Answers a waiting question, or declines it when <paramref name="kind"/> is null. False when nothing in this chat waits
    /// on it, or it waits for the other kind: a secret never reaches a question whose answer the model reads.
    /// </summary>
    public bool Answer(string sessionKey, string toolCallId, string? kind, string? value) =>
        _questions.TryGetValue(sessionKey + "/" + toolCallId, out var question) && (kind is null || question.Kind == kind)
        && question.Answer.TrySetResult(kind is null ? null : value);

    public async Task<AssistantSettings> SettingsAsync(string owner, CancellationToken ct)
    {
        var path = SettingsPath(owner);
        try
        {
            if (!File.Exists(path)) return AssistantSettings.Default;
            DomainOnboardingStore.RejectLinks(path);
            await using var file = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<AssistantSettings>(file, AssistantStream.Json, ct);
            return settings is { AutoTools: not null, Hosts: not null } ? settings : AssistantSettings.Default;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("The assistant settings could not be read ({ErrorType}); using the defaults.", e.GetType().Name);
            return AssistantSettings.Default;
        }
    }

    /// <summary>Saves the owner's settings: <paramref name="changeTools"/> are the tools that may run automatically.</summary>
    public async Task<AssistantSettings> SaveSettingsAsync(string owner, AssistantSettings settings, IReadOnlyCollection<string> changeTools, CancellationToken ct)
    {
        var auto = settings.AutoTools ?? [];
        if (auto.Distinct().Count() != auto.Length || auto.Any(name => !changeTools.Contains(name)))
            throw new AssistantException(400, "invalid_settings", "Automatic tools must be the assistant's change tools, each listed once.");
        var hosts = settings.Hosts ?? [];
        if (hosts.Length > MaxHosts) throw new AssistantException(400, "invalid_sites", $"Add at most {MaxHosts} sites.");
        var names = hosts.Select(Host).ToArray();
        if (Array.FindIndex(names, name => name is null) is var bad and >= 0)
            throw new AssistantException(400, "invalid_sites", SiteProblem(hosts[bad]));
        var saved = new AssistantSettings(auto, [.. names.OfType<string>().Distinct()]);
        await DomainOnboardingStore.WriteJson(SettingsPath(owner), saved, ct, AssistantStream.Json);
        return saved;
    }

    public void Dispose() => _kernel.Dispose();

    private (Verdict Verdict, string? Rule) Decide(AssistantTurn turn, string tool, string tier, string autonomy, bool hostAllowed)
    {
        Dictionary<string, object> context = new() { ["tier"] = tier, ["mode"] = turn.Mode, ["autonomy"] = autonomy, ["host_allowed"] = hostAllowed };
        var result = _kernel.EvaluateToolCall("did:mesh:lucia-" + turn.Owner, tool, context);
        var verdict = $"{result.PolicyDecision?.Action}".Replace("_", "").ToLowerInvariant() switch
        {
            "allow" or "warn" or "log" => Verdict.Allow,
            "requireapproval" => Verdict.Ask,
            "" => result.Allowed ? Verdict.Allow : Verdict.Deny,
            _ => Verdict.Deny,
        };
        return (verdict, result.PolicyDecision?.MatchedRule);
    }

    /// <summary>A host name in the form the broker compares: punycode, lower case, no trailing dot. Null for IPs and anything else.</summary>
    internal static string? Host(string? text)
    {
        var name = text?.Trim().TrimEnd('.');
        return Uri.CheckHostName(name) == UriHostNameType.Dns && Uri.TryCreate("http://" + name + "/", UriKind.Absolute, out var uri)
            ? uri.IdnHost.ToLowerInvariant() : null;
    }

    /// <summary>Why an allowed site was refused, naming it.</summary>
    private static string SiteProblem(string? text)
    {
        var entry = text?.Trim() ?? "";
        if (entry.Length == 0) return "Each site needs a name, like docs.docker.com.";
        if (entry.Length > 60) entry = entry[..59].TrimEnd() + "…";
        return Uri.CheckHostName(entry.TrimEnd('.')) is UriHostNameType.IPv4 or UriHostNameType.IPv6
            ? $"{entry} is an address, not a site name. Add sites by name, like docs.docker.com."
            : $"{entry} isn't a site name. Add sites by name, like docs.docker.com.";
    }

    private string SettingsPath(string owner) => Path.Combine(Path.GetFullPath(options.Value.Directory), "users", owner, "settings.json");

    private static GovernanceKernel Kernel()
    {
        // AGT's audit trail is in-memory; decisions are logged instead, which reaches the observability stack.
        var kernel = new GovernanceKernel(new GovernanceOptions
        {
            ConflictStrategy = ConflictResolutionStrategy.PriorityFirstMatch, EnableAudit = false, EnableMetrics = true,
            EnablePromptInjectionDetection = false,
        });
        kernel.LoadPolicyFromYaml(Policy);
        return kernel;
    }
}
