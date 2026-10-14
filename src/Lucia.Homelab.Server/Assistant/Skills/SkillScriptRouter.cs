#pragma warning disable MAAI001 // The Agent Framework marks its skills API evaluation-only; it is the API this feature builds on.
using System.Text.Json;
using Microsoft.Agents.AI;

namespace Lucia.Homelab.Server.Assistant.Skills;

/// <summary>
/// The Agent Framework's script runner for Lucia: reads the skill's settings, checks the script against its execution mode and
/// hands it to the runner for that mode: the node runner, the Spark runner or the Hyperlight sandbox runner.
/// </summary>
public sealed class SkillScriptRouter(SkillStore store, NodeSkillRunner node, SparkSkillRunner spark, SandboxSkillRunner sandbox)
{
    public AgentFileSkillScriptRunner Runner => RunAsync;

    /// <summary>Why a script can't run in a mode, as text for the model; null when it can go to that mode's runner.</summary>
    public static string? Refuse(string execution, string script)
    {
        if (!SkillLanguages.IsScript(script)) return $"HOST: {script} isn't a script Lucia runs. Scripts end in .sh, .py, .js, .mjs, .ts, .mts or .cs.";
        return execution switch
        {
            SkillExecution.Node => null,
            SkillExecution.Sandbox when !SkillLanguages.SandboxCanRun(script) => $"HOST: {script} can't run in the sandbox, which runs JavaScript and Python only.",
            SkillExecution.Sandbox => null,
            SkillExecution.Spark => null,
            _ => $"HOST: unknown execution mode {execution}.",
        };
    }

    private async Task<object?> RunAsync(AgentFileSkill skill, AgentFileSkillScript script, JsonElement? arguments, IServiceProvider? services,
        CancellationToken ct)
    {
        var name = skill.Frontmatter.Name;
        var relative = Path.GetRelativePath(skill.Path, script.FullPath).Replace('\\', '/');
        if (!SkillStore.ValidName(name) || relative.StartsWith("../", StringComparison.Ordinal)) return "HOST: that script isn't part of the skill.";
        var settings = store.SettingsOf(name);
        if (Refuse(settings.Execution, relative) is { } refusal) return refusal;
        return settings.Execution switch
        {
            SkillExecution.Spark => await spark.RunAsync(name, relative, arguments, ct),
            SkillExecution.Sandbox => await sandbox.RunAsync(name, relative, arguments, ct),
            _ => await node.RunAsync(name, relative, arguments, ct),
        };
    }
}
