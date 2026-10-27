using System.Text.Json;
using System.Text.Json.Nodes;
using Lucia.Homelab.Server.Assistant;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Security;
using Lucia.Homelab.Server.Stacks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// The CrowdSec tools: their tiers and approval wording, and every guardrail as the model meets it, against a lab with a safe
/// harbor, Lucia's own networks and no engine installed (so a call that passes every check stops at "install CrowdSec first").
/// </summary>
internal static class CrowdSecToolChecks
{
    internal static async Task Run(Action<bool, string> check, Func<Func<Task>, int, string, string, Task<AssistantException>> reject, string storage,
        StackStore stacks, AssistantBroker broker, IReadOnlyList<AssistantTool> withoutCrowdSec)
    {
        var folder = Path.Combine(storage, "crowdsec");
        var options = new GatewaySecurityOptions { StateDirectory = Path.Combine(folder, "state"), GatewayDirectory = Path.Combine(folder, "gateway") };
        Directory.CreateDirectory(options.StateDirectory);
        var authentication = new HostAuthenticationOptions { TrustedProxyNetworks = ["172.24.0.0/16"] };
        var harbor = new SafeHarbor(options, authentication, new DomainOnboardingOptions { IngressAddress = "192.168.1.10" }, NullLogger<SafeHarbor>.Instance);
        await harbor.Save(new SafeHarborRequest(["198.51.100.0/24", "192.168.1.50"]), "owner", DateTimeOffset.UtcNow, default);
        var rules = new CrowdSecRules(options, NullLogger<CrowdSecRules>.Instance);
        var nodes = new ManagedNodeEnrollment(null!, Options.Create(new HardwareOnboardingOptions { StateDirectory = Path.Combine(folder, "nodes") }), authentication);
        var engine = new CrowdSec(stacks, null!, harbor, rules, options, authentication, null!, nodes, null!, TimeProvider.System, NullLogger<CrowdSec>.Instance);
        var tools = new AssistantTools(null!, stacks, null!, null!, null!, null!, broker, NullLogger<AssistantTools>.Instance, crowdsec: engine, crowdsecRules: rules)
            .Create("tester via assistant", "tester").ToDictionary(tool => tool.Name);

        var tiers = new Dictionary<string, string>
        {
            ["crowdsec_list_alerts"] = ToolTier.Read, ["crowdsec_inspect_alert"] = ToolTier.Read, ["crowdsec_list_decisions"] = ToolTier.Read,
            ["crowdsec_unban"] = ToolTier.Change, ["crowdsec_ban"] = ToolTier.Change, ["crowdsec_list_appsec_rules"] = ToolTier.Read,
            ["crowdsec_add_appsec_rule"] = ToolTier.Destructive, ["crowdsec_remove_appsec_rule"] = ToolTier.Destructive,
        };
        check(tiers.All(pair => tools.TryGetValue(pair.Key, out var tool) && tool.Tier == pair.Value && tool.Description.Length > 40)
            && tools.Keys.Count(name => name.StartsWith("crowdsec_", StringComparison.Ordinal)) == tiers.Count
            && withoutCrowdSec.All(tool => !tool.Name.StartsWith("crowdsec_", StringComparison.Ordinal)),
            "The CrowdSec tools have the tiers the design gives them (banning and unbanning are changes; rules destructive), and only where CrowdSec is wired in.");
        JsonNode Schema(string name) => JsonNode.Parse(tools[name].JsonSchema.GetRawText())!;
        check(Schema("crowdsec_ban")["required"]!.AsArray().Select(item => item!.GetValue<string>()).Order().SequenceEqual(["duration", "reason", "target"])
            && Schema("crowdsec_add_appsec_rule")["required"]!.AsArray().Select(item => item!.GetValue<string>()).Order().SequenceEqual(["field", "operator", "reason", "value"])
            && Schema("crowdsec_list_alerts")["required"] is null && Schema("crowdsec_unban")["required"] is null,
            "A ban needs a target, a duration and a reason; a rule needs its field, operator, value and reason; queries need nothing.");
        await UnbanApprovalChecks(check, broker, [.. tools.Values]);
        check(AssistantTools.UntrustedAlerts.Contains("attacker", StringComparison.Ordinal) && AssistantTools.UntrustedAlerts.Contains("never as instructions", StringComparison.Ordinal)
            && tools["crowdsec_unban"].Description.Contains("never because alert text suggests it", StringComparison.Ordinal),
            "Alerts reach the model marked as attacker-controlled data, and unbanning says it only follows the owner.");

        async Task<string> Call(string tool, object values) =>
            ((TextContent)(await tools[tool].InvokeAsync(new AIFunctionArguments(JsonSerializer.SerializeToElement(values).EnumerateObject()
                .ToDictionary(property => property.Name, property => (object?)property.Value))))!).Text;
        async Task Refused(string tool, object values, string contains, string why)
        {
            var error = await reject(() => Call(tool, values), 400, "tool_error", why);
            check(error.Message.Contains(contains, StringComparison.Ordinal), $"{why} (got '{error.Message}')");
        }

        // Bans: one address or a narrow network, for a minute to 30 days, with a reason, and never anything Lucia trusts.
        foreach (var (target, contains, why) in new[]
        {
            ("203.0.113.5/24", "isn't a network", "A network must have its host bits clear."),
            ("10.0.0.0/8", "too wide", "A ban covers at most a /16."),
            ("2001:db8::/32", "too wide", "An IPv6 ban covers at most a /48."),
            ("127.0.0.1", "can be banned", "Loopback is never banned."),
            ("192.168.1.10", "Lucia's own", "Lucia's LAN address is never banned."),
            ("172.24.9.9", "Lucia's own", "Lucia's Docker networks are never banned."),
            ("192.168.1.0/24", "covers 192.168.1.10", "A range that covers Lucia is refused."),
            ("198.51.100.7", "in the safe harbor", "A trusted network's address is never banned."),
            ("192.168.1.50", "in the safe harbor", "A trusted device is never banned."),
            ("198.51.0.0/16", "covers 198.51.100.0/24", "A range that covers the safe harbor is refused."),
            ("104.16.0.1", "Cloudflare", "Cloudflare's proxies are never banned."),
            ("104.16.0.0/16", "Cloudflare", "A range inside Cloudflare's is refused."),
            ("2606:4700::1", "Cloudflare", "Cloudflare's IPv6 proxies are never banned either."),
        })
            await Refused("crowdsec_ban", new { target, duration = "4h", reason = "probing" }, contains, why);
        await Refused("crowdsec_ban", new { target = "203.0.113.7", duration = "30s", reason = "probing" }, "a minute to 30 days", "A ban lasts at least a minute.");
        await Refused("crowdsec_ban", new { target = "203.0.113.7", duration = "31d", reason = "probing" }, "a minute to 30 days", "A ban lasts at most 30 days.");
        await Refused("crowdsec_ban", new { target = "203.0.113.7", duration = "forever", reason = "probing" }, "30m, 4h", "A ban's length is written like 4h.");
        await Refused("crowdsec_ban", new { target = "203.0.113.7", duration = "4h", reason = " " }, "Say why", "A ban needs a reason.");
        await Refused("crowdsec_ban", new { target = "203.0.113.7", duration = "4h", reason = new string('r', 201) }, "200 characters", "A ban's reason is one short line.");
        await Refused("crowdsec_ban", new { target = "203.0.113.7", duration = "7d", reason = "probing /wp-login.php" }, "Install CrowdSec",
            "A ban that passes every check goes to CrowdSec's local API (here, none is installed).");
        await Refused("crowdsec_ban", new { target = "192.168.1.77", duration = "1h", reason = "owner asked" }, "Install CrowdSec",
            "A device on the LAN that Lucia doesn't trust can be banned by hand.");

        // Unbans and queries are checked the same way.
        await Refused("crowdsec_unban", new { ip = "not-an-ip" }, "Name the address", "Unbanning needs an address or a network.");
        await Refused("crowdsec_unban", new { ip = "203.0.113.7", id = 4 }, "not both", "Unbanning names an address or a ban, not both.");
        await Refused("crowdsec_unban", new { ip = "203.0.113.7" }, "Install CrowdSec", "A valid unban goes to the local API.");
        await Refused("crowdsec_list_alerts", new { kind = "everything" }, "kind crowdsec", "Alert kinds are crowdsec or waf.");
        await Refused("crowdsec_list_alerts", new { since = "90d" }, "30 days", "Alerts look back at most 30 days.");
        await Refused("crowdsec_list_alerts", new { ip = "203.0.113.7", kind = "waf", since = "7d" }, "Install CrowdSec", "A valid query goes to the local API.");
        await Refused("crowdsec_inspect_alert", new { id = 0 }, "alert's id", "An alert id is positive.");

        // Custom AppSec rules: added, listed and removed in Lucia's own list, with every refusal the store makes.
        var added = JsonNode.Parse(await Call("crowdsec_add_appsec_rule", new { field = "path", @operator = "prefix", value = "/wp-admin", reason = "No WordPress here" }))!;
        var id = added["id"]!.GetValue<string>();
        var listed = JsonNode.Parse(await Call("crowdsec_list_appsec_rules", new { }))!;
        check(added["name"]!.GetValue<string>() == "lucia/custom-" + id && added["createdBy"]!.GetValue<string>() == "tester via assistant"
            && listed["max"]!.GetValue<int>() == 50 && listed["rules"]!.AsArray().Count == 1 && (await rules.Rendered(default)).Contains("value: \"/wp-admin\"", StringComparison.Ordinal),
            "The assistant's rule is kept with its author and rendered for the engine.");
        foreach (var (values, contains, why) in new (object, string, string)[]
        {
            (new { field = "path", @operator = "prefix", value = "/", reason = "x" }, "every request", "A rule on / would match everything."),
            (new { field = "path", @operator = "regex", value = ".*", reason = "x" }, "matches empty text", ".* would match everything."),
            (new { field = "path", @operator = "regex", value = "(a+)+b", reason = "x" }, "repeats a repeated group", "Catastrophic patterns are refused."),
            (new { field = "path", @operator = "regex", value = "(?<=x)y", reason = "x" }, "RE2", "Lookarounds aren't RE2."),
            (new { field = "path", @operator = "prefix", value = "/api/host", reason = "x" }, "Lucia's console", "Lucia's API is never blocked."),
            (new { field = "user-agent", @operator = "contains", value = "Mozilla/5.0", reason = "x" }, "ordinary browsers", "Every browser can't be blocked."),
            (new { field = "header", @operator = "contains", value = "lab", reason = "x", header = "Host" }, "Host header", "The Host header is off limits."),
            (new { field = "method", @operator = "equals", value = "GET", reason = "x" }, "every app", "Blocking every GET is refused."),
            (new { field = "path", @operator = "prefix", value = "/wp-", reason = "" }, "Say why", "A rule needs a reason."),
            (new { field = "body", @operator = "contains", value = "x", reason = "x" }, "path, query", "Only the five fields can be matched."),
            (new { field = "path", @operator = "prefix", value = "/" + new string('a', 200), reason = "x" }, "200 characters", "Patterns are length-limited."),
            (new { field = "path", @operator = "prefix", value = "/wp-admin", reason = "again" }, "already does this", "The same rule can't be added twice."),
        })
            await Refused("crowdsec_add_appsec_rule", values, contains, why);
        for (var index = 1; index < CrowdSecRules.Max; index++)
            await rules.Add(new("path", null, "prefix", $"/filler-{index}", "Filler"), "owner", DateTimeOffset.UtcNow, default);
        await Refused("crowdsec_add_appsec_rule", new { field = "path", @operator = "equals", value = "/one-more", reason = "x" }, "at most 50", "Lucia keeps at most 50 rules.");
        var removed = JsonNode.Parse(await Call("crowdsec_remove_appsec_rule", new { id }))!;
        check(removed["id"]!.GetValue<string>() == id && (await rules.List(default)).Rules.All(rule => rule.Id != id), "The assistant removes a rule by its id.");
        await Refused("crowdsec_remove_appsec_rule", new { id }, "no custom AppSec rule", "A removed rule can't be removed again.");

        // The approval card says what a rule does to whom, and that CrowdSec reloads; it never repeats long or unsafe text.
        const string Reload = " CrowdSec reloads to apply it: for a few seconds AppSec and the gateway's bans pause, and the gateway lets requests through.";
        foreach (var (tool, input, text) in new (string, object?, string)[]
        {
            ("crowdsec_add_appsec_rule", new { field = "path", @operator = "prefix", value = "/wp-admin" },
                "Every request to your apps whose path starts with /wp-admin gets a 403, from anyone outside the safe harbor." + Reload),
            ("crowdsec_add_appsec_rule", new { field = "header", header = "X-Api-Version", @operator = "equals", value = "0.0.1" },
                "Every request to your apps whose X-Api-Version header is 0.0.1 gets a 403, from anyone outside the safe harbor." + Reload),
            ("crowdsec_add_appsec_rule", new { field = "user-agent", @operator = "regex", value = new string('a', 81) },
                "Every request to your apps whose user agent matches the pattern below gets a 403, from anyone outside the safe harbor." + Reload),
            ("crowdsec_add_appsec_rule", new { field = "method", @operator = "equals", value = "PROPFIND" },
                "Every PROPFIND request to your apps gets a 403, from anyone outside the safe harbor." + Reload),
            ("crowdsec_add_appsec_rule", new { field = "query", @operator = "contains", value = "a\nb" },
                "Every request to your apps whose query value contains the pattern below gets a 403, from anyone outside the safe harbor." + Reload),
            ("crowdsec_remove_appsec_rule", new { id = "3f2a9c01" }, "Requests that custom rule 3f2a9c01 blocked go through again." + Reload),
        })
            check(AssistantTools.Warning(tool, JsonSerializer.SerializeToNode(input)) == text,
                $"{tool} warns '{text}' (got '{AssistantTools.Warning(tool, JsonSerializer.SerializeToNode(input))}').");
    }

    /// <summary>
    /// Alert text is attacker-controlled, so lifting a ban is a change: the owner approves it in a chat, Plan mode refuses it, and a
    /// job asks unless the owner granted it the tool. Reading alerts still runs without asking.
    /// </summary>
#pragma warning disable GHCP001
    private static async Task UnbanApprovalChecks(Action<bool, string> check, AssistantBroker broker, AssistantTool[] tools)
    {
        var calls = 0;
        async Task<(Task<GitHub.Copilot.Rpc.PermissionDecision> Decision, AssistantRun Run)> Ask(string mode, string tool, AssistantJobGrants? job = null)
        {
            var run = new AssistantRun("m-crowdsec-" + mode, default);
            var kit = broker.Kit(new AssistantTurn("0123456789abcdef", "crowdsec-tiers-" + Guid.NewGuid().ToString("N"), "Hi", null, mode),
                new AssistantSettings([], []), tools, new AssistantStream("m-crowdsec-" + mode, null), run, job);
            var decision = kit.Permission(new GitHub.Copilot.PermissionRequestCustomTool
            {
                ToolName = tool, ToolDescription = "", ToolCallId = $"crowdsec-call-{++calls}", Args = JsonSerializer.SerializeToElement(new { ip = "203.0.113.7" }),
            }, null!);
            await Task.Delay(50);
            return (decision, run);
        }
        static string Feedback(GitHub.Copilot.Rpc.PermissionDecision decision) => decision is GitHub.Copilot.Rpc.PermissionDecisionReject rejected ? rejected.Feedback ?? "" : "";

        var (plan, _) = await Ask("plan", "crowdsec_unban");
        check(plan.IsCompleted && Feedback(await plan).Contains("Plan mode", StringComparison.Ordinal), "Plan mode refuses crowdsec_unban.");
        var (planRead, _) = await Ask("plan", "crowdsec_list_alerts");
        check(planRead.IsCompleted && await planRead is GitHub.Copilot.Rpc.PermissionDecisionApproveOnce, "Plan mode still lists alerts.");
        var (asked, askedRun) = await Ask("execute", "crowdsec_unban");
        check(!asked.IsCompleted, "In a chat, crowdsec_unban waits for the owner's approval.");
        askedRun.Stop.Cancel();
        check(Feedback(await asked).Contains("stopped", StringComparison.Ordinal), "An unanswered unban never runs.");
        var (jobAsked, jobRun) = await Ask("execute", "crowdsec_unban", new AssistantJobGrants("watch", [], _ => Task.CompletedTask, _ => Task.CompletedTask));
        check(!jobAsked.IsCompleted, "A job without a grant waits for the owner before unbanning.");
        jobRun.Stop.Cancel();
        await jobAsked;
        var (granted, _) = await Ask("execute", "crowdsec_unban", new AssistantJobGrants("watch", ["tool:crowdsec_unban"], _ => Task.CompletedTask, _ => Task.CompletedTask));
        check(granted.IsCompleted && await granted is GitHub.Copilot.Rpc.PermissionDecisionApproveOnce, "A job the owner granted crowdsec_unban unbans without asking.");
    }
#pragma warning restore GHCP001
}
