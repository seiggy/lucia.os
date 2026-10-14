using System.Text.RegularExpressions;

namespace Lucia.Homelab.Server.Assistant.Skills;

/// <summary>
/// Heuristic checks on a skill the assistant drafted, so the owner sees what to look at before enabling it. A warning never blocks a
/// draft and a clean result proves nothing: the owner still reads the scripts.
/// </summary>
public static partial class SkillReviewLint
{
    /// <summary>The warnings for a skill's scripts, each phrased for the owner. Paths are skill-relative, such as scripts/check.sh.</summary>
    public static string[] Check(string execution, string tier, IEnumerable<(string Path, string Content)> scripts)
    {
        var warnings = new List<string>();
        foreach (var (path, content) in scripts.Where(script => SkillLanguages.IsScript(script.Path)))
        {
            var language = SkillLanguages.Language(path);
            void Add(bool found, string message)
            {
                if (found && !warnings.Contains(message)) warnings.Add(message);
            }
            Add(Passthrough(language, content), $"{path} may run text it is given as a command (eval, shell -c, shell=True, exec or similar). A narrow skill should run fixed commands with validated parameters.");
            if (tier == ToolTier.Read)
                Add(Changes(content), $"{path} looks like it changes the system, but the skill's tier is read. Check the tier.");
            if (UsesArguments(content))
                Add(!Validates(content), $"{path} reads its arguments without an obvious allow-list or pattern check.");
            if (execution == SkillExecution.Node)
                Add(Network(content), $"{path} reaches the network from the server (curl, wget, requests or fetch).");
        }
        return [.. warnings];
    }

    private static bool Passthrough(string? language, string text)
    {
        if (Any(Shell(), text) || Any(PowerShell(), text)) return true;
        return language switch
        {
            SkillLanguages.Shell => Any(ShellEval(), text),
            SkillLanguages.Python => Any(PythonExec(), text),
            SkillLanguages.Javascript or SkillLanguages.Typescript => Any(JsExec(), text) || text.Contains("child_process", StringComparison.Ordinal) && Any(BareExec(), text),
            SkillLanguages.Csharp => Any(CsharpProcess(), text),
            _ => false,
        };
    }

    private static bool Changes(string text) => Any(ChangeCommands(), text);

    private static bool UsesArguments(string text) => text.Contains("LUCIA_SKILL_ARGS", StringComparison.Ordinal);

    private static bool Validates(string text) => Any(ValidationHints(), text);

    private static bool Network(string text) => Any(NetworkCalls(), text);

    private static bool Any(Regex pattern, string text)
    {
        try { return pattern.IsMatch(text); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    [GeneratedRegex(@"(?:^|[\s;&|(`])(?:bash|sh|zsh|dash)\s+-c\s+[""']?[^\n]*\$", RegexOptions.Multiline, 250)]
    private static partial Regex Shell();

    [GeneratedRegex(@"\bInvoke-Expression\b", RegexOptions.IgnoreCase, 250)]
    private static partial Regex PowerShell();

    [GeneratedRegex(@"(?:^|[\s;&|(`])eval\s", RegexOptions.Multiline, 250)]
    private static partial Regex ShellEval();

    [GeneratedRegex(@"\beval\s*\(|\bexec\s*\(|shell\s*=\s*True|\bos\.system\s*\(|\bos\.popen\s*\(", RegexOptions.None, 250)]
    private static partial Regex PythonExec();

    [GeneratedRegex(@"\beval\s*\(|\bnew\s+Function\s*\(|\bchild_process\s*\.\s*exec(?:Sync)?\s*\(|(?<![\w.])execSync\s*\(", RegexOptions.None, 250)]
    private static partial Regex JsExec();

    [GeneratedRegex(@"(?<![\w.])exec\s*\(", RegexOptions.None, 250)]
    private static partial Regex BareExec();

    [GeneratedRegex(@"Process\.Start\s*\(\s*(?:\$""|[A-Za-z_]+\s*\+|[a-z][A-Za-z0-9_]*\s*\))|FileName\s*=\s*(?:\$""|\w+\s*\+)|Arguments\s*=\s*(?:\$""|\w+\s*\+)|""(?:/bin/)?(?:bash|sh)""[^\n]*""-c""", RegexOptions.None, 250)]
    private static partial Regex CsharpProcess();

    [GeneratedRegex(@"(?:^|[\s;&|(`])(?:sudo\s+)?rm\s|\bsystemctl\s+(?:restart|stop|start|disable|enable|reload|kill|mask)\b|\bdocker\s+(?:rm|stop|kill|restart|rmi|prune|volume\s+rm|system\s+prune)\b|\bapt(?:-get)?\s+(?:install|remove|purge|upgrade|dist-upgrade|autoremove)\b|\breboot\b|\bshutdown\b|>>?\s*/etc/", RegexOptions.Multiline, 250)]
    private static partial Regex ChangeCommands();

    [GeneratedRegex(@"\bfullmatch\b|\bre\.(?:match|search|fullmatch)\b|\bRegex\b|\.test\s*\(|\.match\s*\(|=~|\bcase\s+[""$]|\ballow(?:ed|list)?\b|\bALLOW|\bwhitelist\b|\b(?:in|includes|has)\s*\(?\s*(?:ALLOWED|allowed)|\bisdigit\s*\(|\bisalnum\s*\(|\bparseInt\b|\bint\s*\(", RegexOptions.IgnoreCase, 250)]
    private static partial Regex ValidationHints();

    [GeneratedRegex(@"(?:^|[\s;&|(`])(?:curl|wget)\s|\brequests\.(?:get|post|put|delete|request)\b|\burllib\.request\b|\bfetch\s*\(|\bHttpClient\b", RegexOptions.Multiline, 250)]
    private static partial Regex NetworkCalls();
}
