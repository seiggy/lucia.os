using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lucia.Homelab.Server.Stacks;

namespace Lucia.Homelab.Server.Assistant.Skills;

/// <summary>
/// Runs a skill's script on a managed server over the node agent's command channel. The agent keeps a synced copy of every enabled
/// skill under /var/lib/lucia-agent/skills/&lt;name&gt;/&lt;hash&gt;; the command copies it into a private temporary directory (so a script
/// can't change the cache), runs it with the runtime its language needs, and always deletes the directory. A runtime the server lacks
/// is reported, never installed.
/// </summary>
public sealed class NodeSkillRunner(NodeExec exec, SkillStore store)
{
    public const int Seconds = 300;
    /// <summary>The exit code and text the command reports when the node doesn't hold the skill yet.</summary>
    public const int NotSyncedExit = 66;
    public const string NotSyncedText = "Lucia: this skill hasn't been downloaded to this server yet.";
    public const string CacheRoot = "/var/lib/lucia-agent/skills";
    private static readonly TimeSpan SyncWait = TimeSpan.FromSeconds(30), SyncPoll = TimeSpan.FromSeconds(4);

    /// <summary>The shell that runs the synced copy of a skill. Null when the script's extension has no runtime on a node.</summary>
    public static string? Command(string skill, string hash, string argumentsJson, string script)
    {
        var (runtime, run) = Path.GetExtension(script).ToLowerInvariant() switch
        {
            ".sh" => ("bash", $"bash {Quote(script)}"),
            ".py" => ("python3", $"python3 {Quote(script)}"),
            ".js" or ".mjs" => ("node", $"node {Quote(script)}"),
            ".ts" or ".mts" => ("node", $"node --experimental-strip-types {Quote(script)}"),
            ".cs" => ("dotnet", $"dotnet run {Quote(script)}"),
            _ => ("", ""),
        };
        if (runtime.Length == 0 || !SkillStore.ValidName(skill) || !SkillNodeEndpoints.HashPattern().IsMatch(hash)) return null;
        var missing = runtime == "dotnet" ? ".NET 10 SDK (dotnet)" : runtime;
        var text = new StringBuilder();
        text.Append("set -u\numask 077\n");
        text.Append("src=").Append(Quote($"{CacheRoot}/{skill}/{hash}")).Append('\n');
        text.Append("[ -d \"$src\" ] || { echo ").Append(Quote(NotSyncedText)).Append(" >&2; exit ").Append(NotSyncedExit).Append("; }\n");
        text.Append("d=$(mktemp -d) || { echo \"Lucia: could not create a temporary directory on this server.\" >&2; exit 125; }\n");
        text.Append("trap 'rm -rf \"$d\"' EXIT\ncd \"$d\" || exit 125\n");
        text.Append("command -v ").Append(runtime).Append(" >/dev/null 2>&1 || { echo \"Lucia: ").Append(missing)
            .Append(" isn't installed on this server, so this script can't run here.\" >&2; exit 127; }\n");
        text.Append("cp -R \"$src/.\" \"$d/\" || { echo \"Lucia: could not copy the skill into a work directory.\" >&2; exit 125; }\n");
        text.Append("LUCIA_SKILL_ARGS=$(printf '%s' ").Append(Quote(Convert.ToBase64String(Encoding.UTF8.GetBytes(argumentsJson)))).Append(" | base64 -d)\n");
        text.Append("export LUCIA_SKILL_ARGS\n");
        if (runtime == "dotnet")
            text.Append("export HOME=\"${HOME:-$d}\" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1\n");
        text.Append("printf '%s' \"$LUCIA_SKILL_ARGS\" | ").Append(run).Append("\nexit $?\n");
        return text.ToString();
    }

    internal static bool NotSynced(NodeRequestResult result) =>
        result.ExitCode == NotSyncedExit && (result.Output ?? "").Contains(NotSyncedText, StringComparison.Ordinal);

    /// <param name="script">The script's path inside the skill, such as scripts/check.py.</param>
    /// <param name="arguments">What the model passed: <c>node</c> names the server, the rest goes to the script.</param>
    /// <returns>The result as text for the model; expected failures read "Error: ..." so the model sees why.</returns>
    public async Task<string> RunAsync(string skill, string script, JsonElement? arguments, CancellationToken ct)
    {
        var passed = arguments is { ValueKind: JsonValueKind.Object } given ? JsonNode.Parse(given.GetRawText()) as JsonObject ?? [] : [];
        if (arguments is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) })
            return "Error: pass the arguments as an object, with a \"node\" entry naming the server to run on.";
        var node = passed["node"] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
        if (string.IsNullOrWhiteSpace(node))
            return "Error: this skill runs on a server. Pass its name in the arguments object as \"node\", for example the hostname from list_nodes.";
        passed.Remove("node");
        var hash = store.HashOf(skill);
        if (hash is null) return "Error: that skill no longer exists.";
        var command = Command(skill, hash, passed.ToJsonString(), script);
        if (command is null) return $"Error: {script} has an extension that doesn't run on a server.";
        try
        {
            var (id, hostname) = await exec.Node(node, ct);
            if (!await exec.SyncsSkills(id, ct))
                return $"Error: {hostname}'s agent can't download skills yet. Update it with node_action update-agent, then try again.";
            var result = await exec.Exec(id, hostname, command, "bash", Seconds, ct);
            if (NotSynced(result))
            {
                try { await exec.SyncSkills(id, ct); }
                catch (Exception e) when (e is Onboarding.HardwareOnboardingException or AssistantException) { }
                var until = DateTimeOffset.UtcNow + SyncWait;
                while (NotSynced(result) && DateTimeOffset.UtcNow < until)
                {
                    await Task.Delay(SyncPoll, ct);
                    result = await exec.Exec(id, hostname, command, "bash", Seconds, ct);
                }
                if (NotSynced(result)) return $"Error: {hostname} hasn't downloaded this skill yet; try again in a minute.";
            }
            var note = result.Running ? $"\n{hostname} stopped answering, so Lucia stopped waiting. The script stops by itself at its time limit." : "";
            return $"Ran {script} on {hostname}. Exit code {(result.ExitCode is { } code ? code.ToString() : "unknown")}.{note}\n"
                + (string.IsNullOrEmpty(result.Output) ? "(no output)" : result.Output);
        }
        catch (AssistantException e) when (e.Code is "unknown_node" or "exec_unsupported" or "command_refused") { return "Error: " + e.Message; }
        catch (Onboarding.HardwareOnboardingException e) { return "Error: " + e.Message; }
    }
    private static string Quote(string text) => "'" + text.Replace("'", "'\\''") + "'";
}
