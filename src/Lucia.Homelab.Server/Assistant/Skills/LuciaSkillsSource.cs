#pragma warning disable MAAI001 // The Agent Framework marks its skills API evaluation-only; it is the API this feature builds on.
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;

namespace Lucia.Homelab.Server.Assistant.Skills;

/// <summary>
/// The Agent Framework's file source with each skill's scripts told apart by their real argument schema. The framework describes every
/// file script as taking an array of strings, so a model sends its arguments as JSON text; here a script advertises the schema the skill
/// declares (or its execution mode's default), in load_skill's "available scripts" block and nowhere else. Discovery, the resource
/// and script tools and the runner stay the framework's.
/// </summary>
public sealed partial class LuciaSkillsSource(AgentSkillsSource inner, SkillStore store) : AgentSkillsSource
{
    public override async Task<IList<AgentSkill>> GetSkillsAsync(AgentSkillsSourceContext context, CancellationToken cancellationToken = default)
    {
        var skills = await inner.GetSkillsAsync(context, cancellationToken);
        return [.. skills.Select(skill => skill is AgentFileSkill file ? new LuciaSkill(file, store) : skill)];
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>A file skill whose scripts carry Lucia's schemas.</summary>
    internal sealed class LuciaSkill(AgentFileSkill inner, SkillStore store) : AgentSkill
    {
        private string? _content;

        public override AgentSkillFrontmatter Frontmatter => inner.Frontmatter;

        public override async ValueTask<string> GetContentAsync(CancellationToken cancellationToken = default)
        {
            if (_content is not null) return _content;
            var original = await inner.GetContentAsync(cancellationToken);
            var at = original.LastIndexOf(ScriptsMarker, StringComparison.Ordinal);
            if (at < 0) return _content = original;
            var names = ScriptName().Matches(original[at..]).Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).ToArray();
            var scripts = new List<AgentSkillScript>();
            foreach (var name in names)
                if (await GetScriptAsync(name, cancellationToken) is { } script) scripts.Add(script);
            return _content = original[..at] + ScriptsBlock(scripts);
        }

        public override ValueTask<AgentSkillResource?> GetResourceAsync(string name, CancellationToken cancellationToken = default) =>
            inner.GetResourceAsync(name, cancellationToken);

        public override async ValueTask<AgentSkillScript?> GetScriptAsync(string name, CancellationToken cancellationToken = default) =>
            await inner.GetScriptAsync(name, cancellationToken) is { } script
                ? new LuciaSkillScript(script, inner, store.SchemaOf(inner.Frontmatter.Name, name))
                : null;
    }

    private const string ScriptsMarker = "\n<available_scripts";

    [GeneratedRegex(@"<script name=""([^""]*)""")]
    private static partial Regex ScriptName();

    /// <summary>The framework's available_scripts block (same format), with each script's schema as Lucia gives it.</summary>
    internal static string ScriptsBlock(IReadOnlyList<AgentSkillScript> scripts)
    {
        if (scripts.Count == 0) return "\n<available_scripts />";
        var text = new StringBuilder("\n<available_scripts>\n");
        foreach (var script in scripts)
        {
            text.Append("  <script name=\"").Append(Escape(script.Name, quotes: true)).Append('"');
            if (!string.IsNullOrWhiteSpace(script.Description)) text.Append(" description=\"").Append(Escape(script.Description, quotes: true)).Append('"');
            if (script.ParametersSchema is { } schema)
                text.Append(">\n    <parameters_schema>").Append(Escape(schema.GetRawText(), quotes: false)).Append("</parameters_schema>\n  </script>\n");
            else text.Append("/>\n");
        }
        return text.Append("</available_scripts>").ToString();
    }

    private static string Escape(string value, bool quotes)
    {
        var result = value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        return quotes ? result.Replace("\"", "&quot;").Replace("'", "&apos;") : result;
    }
}

/// <summary>A skill script that runs as the framework's file script does, and says its <c>arguments</c> are an object of the skill's declared shape.</summary>
internal sealed class LuciaSkillScript : AgentSkillScript
{
    private readonly AgentSkillScript _inner;
    private readonly AgentSkill _skill;
    private readonly JsonElement _schema;

    public LuciaSkillScript(AgentSkillScript inner, AgentSkill skill, System.Text.Json.Nodes.JsonObject schema) : base(inner.Name, inner.Description)
    {
        _inner = inner;
        _skill = skill;
        _schema = JsonSerializer.SerializeToElement(schema);
    }

    public override JsonElement? ParametersSchema => _schema;

    public override Task<object?> RunAsync(AgentSkill skill, JsonElement? arguments, IServiceProvider? serviceProvider, CancellationToken cancellationToken = default) =>
        _inner.RunAsync(_skill, arguments, serviceProvider, cancellationToken);
}
