using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lucia.Homelab.Server.Stacks;

namespace Lucia.Homelab.Server.Assistant.Skills;

/// <summary>
/// Runs a Python or JavaScript skill script in a Hyperlight micro-VM on a server with KVM. The server's agent keeps the skill and the
/// sandbox runtime (Lucia.SkillSandbox, built on the Agent Framework's Hyperlight package) in its skill cache; one command copies the
/// skill into a private directory and starts the runtime in a locked-down Docker container, handing it the request on stdin. The
/// container is read-only, has no capabilities and, unless the skill lists allowed domains, no network. Nothing the model supplies
/// reaches a command line: it only ever travels inside the request JSON.
/// </summary>
public sealed class SandboxSkillRunner(NodeExec exec, SkillStore store, SkillSandboxRuntime runtime)
{
    public const int Seconds = 300, ScriptSeconds = 200;
    public const string NoServer = "No server can run sandboxed skills: Hyperlight needs an x86_64 server with KVM.";
    /// <summary>The .NET runtime-deps image the sandbox runs in, pinned by its linux/amd64 digest.</summary>
    public const string Image = "mcr.microsoft.com/dotnet/runtime-deps:10.0@sha256:62028293921f5bd6db3d9f9119cd29cd926dfd3c2336da23b09f425e40c3c832";
    private static readonly TimeSpan SyncWait = TimeSpan.FromSeconds(60), SyncPoll = TimeSpan.FromSeconds(4);

    /// <summary>The language the sandbox runs a script as, or null when it only runs Python and JavaScript.</summary>
    public static string? Language(string script) => Path.GetExtension(script).ToLowerInvariant() switch
    {
        ".py" => "python",
        ".js" or ".mjs" => "javascript",
        _ => null,
    };

    /// <summary>The server to run on: the one named when it has KVM, otherwise the first with KVM, or null when none has.</summary>
    public static (Guid Id, string Hostname)? Choose(string? named, IReadOnlyList<(Guid Id, string Hostname)> kvm)
    {
        if (kvm.Count == 0) return null;
        var wanted = (named ?? "").Trim().TrimEnd('.');
        foreach (var item in kvm)
            if (wanted.Length > 0 && (Guid.TryParse(wanted, out var id) && id == item.Id
                || string.Equals(item.Hostname, wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Hostname, wanted.Split('.')[0], StringComparison.OrdinalIgnoreCase))) return item;
        return kvm[0];
    }

    /// <summary>The request the runtime reads on stdin.</summary>
    public static string Request(string script, string language, JsonObject arguments, string[] allowedDomains) =>
        new JsonObject
        {
            ["script"] = script, ["language"] = language, ["arguments"] = arguments.DeepClone(),
            ["allowedDomains"] = new JsonArray([.. allowedDomains.Select(domain => (JsonNode?)JsonValue.Create(domain))]),
            ["timeoutSeconds"] = ScriptSeconds,
        }.ToJsonString();

    /// <summary>The shell that runs the sandbox. Null when the skill name or a hash is not valid, so no model text reaches a path.</summary>
    public static string? Command(string skill, string skillHash, string sandboxHash, string requestJson, bool network)
    {
        if (!SkillStore.ValidName(skill) || !SkillNodeEndpoints.HashPattern().IsMatch(skillHash) || !SkillNodeEndpoints.HashPattern().IsMatch(sandboxHash)) return null;
        var text = new StringBuilder();
        text.Append("set -u\numask 077\n");
        text.Append("app=").Append(Quote($"{NodeSkillRunner.CacheRoot}/_sandbox/{sandboxHash}")).Append('\n');
        text.Append("src=").Append(Quote($"{NodeSkillRunner.CacheRoot}/{skill}/{skillHash}")).Append('\n');
        text.Append("[ -d \"$app\" ] && [ -d \"$src\" ] || { echo ").Append(Quote(NodeSkillRunner.NotSyncedText)).Append(" >&2; exit ")
            .Append(NodeSkillRunner.NotSyncedExit).Append("; }\n");
        text.Append("command -v docker >/dev/null 2>&1 || { echo \"Lucia: Docker isn't installed on this server, so the sandbox can't run here.\" >&2; exit 127; }\n");
        text.Append("[ -c /dev/kvm ] || { echo \"Lucia: this server has no /dev/kvm, so the sandbox can't run here.\" >&2; exit 127; }\n");
        text.Append("gid=$(stat -c %g /dev/kvm) || { echo \"Lucia: could not read the KVM device's group.\" >&2; exit 125; }\n");
        text.Append("d=$(mktemp -d) || { echo \"Lucia: could not create a temporary directory on this server.\" >&2; exit 125; }\n");
        text.Append("trap 'rm -rf \"$d\"' EXIT\n");
        text.Append("cp -R \"$src/.\" \"$d/\" || { echo \"Lucia: could not copy the skill into a work directory.\" >&2; exit 125; }\n");
        // The container's user is nobody, so the private copy is made readable (and never writable) for it.
        text.Append("chmod -R u=rwX,go=rX \"$d\" || exit 125\n");
        text.Append("docker image inspect ").Append(Quote(Image)).Append(" >/dev/null 2>&1 || docker pull -q ").Append(Quote(Image))
            .Append(" >/dev/null || { echo \"Lucia: could not download the sandbox's base image.\" >&2; exit 125; }\n");
        text.Append("printf '%s' ").Append(Quote(Convert.ToBase64String(Encoding.UTF8.GetBytes(requestJson)))).Append(" | base64 -d | docker run --rm -i")
            .Append(" --device /dev/kvm --network ").Append(network ? "bridge" : "none")
            .Append(" --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges --pids-limit 256 --memory 1g --cpus 2")
            .Append(" --group-add \"$gid\" --user 65534:65534 -e HOME=/tmp -e DOTNET_EnableDiagnostics=0")
            .Append(" -v \"$app\":/app:ro -v \"$d\":/skill:ro ").Append(Quote(Image)).Append(" /app/Lucia.SkillSandbox\n");
        text.Append("exit $?\n");
        return text.ToString();
    }

    /// <summary>What the runtime answered, taken from the last JSON line of the command's output.</summary>
    public static (int ExitCode, string Output, bool Truncated, string? Error)? Parse(string? output)
    {
        foreach (var line in (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("{\"exitCode\"", StringComparison.Ordinal)) continue;
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;
                var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                return (root.GetProperty("exitCode").GetInt32(),
                    root.TryGetProperty("output", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : "",
                    root.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True, error);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
        }
        return null;
    }

    /// <param name="script">The script's path inside the skill, such as scripts/check.py.</param>
    /// <param name="arguments">What the model passed: an optional <c>node</c> names the server, the rest goes to the script.</param>
    /// <returns>The result as text for the model; Lucia's own refusals read "HOST: ..." so the model can tell them from the script's output.</returns>
    public async Task<string> RunAsync(string skill, string script, JsonElement? arguments, CancellationToken ct)
    {
        if (Language(script) is not { } language) return $"HOST: {script} can't run in the sandbox, which runs JavaScript and Python only.";
        var (passed, error, _) = SkillArguments.Normalize(arguments, null, SkillExecution.Sandbox);
        if (error is not null) return error;
        var named = SkillArguments.NodeOf(passed!);
        passed!.Remove("node");
        var hash = store.HashOf(skill);
        if (hash is null) return "HOST: that skill no longer exists.";
        if (runtime.Hash is not { } sandboxHash) return "HOST: this controller has no sandbox runtime build, so skills can't run in the sandbox.";
        var domains = store.SettingsOf(skill).AllowedDomains;
        var command = Command(skill, hash, sandboxHash, Request(script, language, passed, domains), network: domains.Length > 0);
        if (command is null) return "HOST: that skill can't be run in the sandbox.";
        try
        {
            if (named is { Length: > 0 })
                try { named = (await exec.Node(named, ct)).Id.ToString(); }
                catch (AssistantException e) when (e.Code == "unknown_node") { named = null; }
            if (Choose(named, await exec.KvmNodes(ct)) is not { } node) return "HOST: " + NoServer;
            var (id, hostname) = node;
            if (!await exec.SyncsSkills(id, ct))
                return $"HOST: {hostname}'s agent can't download skills yet. Update it with node_action update-agent, then try again.";
            var result = await exec.Exec(id, hostname, command, "bash", Seconds, ct);
            if (NodeSkillRunner.NotSynced(result))
            {
                try { await exec.SyncSkills(id, ct); }
                catch (Exception e) when (e is Onboarding.HardwareOnboardingException or AssistantException) { }
                var until = DateTimeOffset.UtcNow + SyncWait;
                while (NodeSkillRunner.NotSynced(result) && DateTimeOffset.UtcNow < until)
                {
                    await Task.Delay(SyncPoll, ct);
                    result = await exec.Exec(id, hostname, command, "bash", Seconds, ct);
                }
                if (NodeSkillRunner.NotSynced(result))
                    return $"HOST: {hostname} hasn't downloaded this skill and the sandbox runtime yet; try again in a few minutes.";
            }
            var where = $"in the Hyperlight sandbox on {hostname}";
            if (Parse(result.Output) is not { } run)
                return $"HOST: the sandbox on {hostname} gave no answer (exit code {(result.ExitCode is { } c ? c.ToString() : "unknown")}).\n"
                    + (string.IsNullOrWhiteSpace(result.Output) ? "(no output)" : result.Output);
            if (run.Error is { Length: > 0 }) return $"HOST: {script} didn't run {where}: {run.Error}";
            var note = run.Truncated ? "\n(Output was cut to its last 48 KiB.)" : "";
            return $"Ran {script} {where}. Exit code {run.ExitCode}.\n" + (run.Output.Length == 0 ? "(no output)" : run.Output) + note;
        }
        catch (AssistantException e) when (e.Code is "unknown_node" or "exec_unsupported" or "command_refused") { return "HOST: " + e.Message; }
        catch (Onboarding.HardwareOnboardingException e) { return "HOST: " + e.Message; }
    }

    private static string Quote(string text) => "'" + text.Replace("'", "'\\''") + "'";
}
