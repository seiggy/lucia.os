#pragma warning disable MAAI001 // The Agent Framework marks its skills API evaluation-only; it is the API this feature builds on.
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Lucia.Homelab.Server.Assistant.Skills;

/// <summary>The skills part of one turn: the system message text that lists them, and the tools that read and run them.</summary>
public sealed record SkillContext(string Instructions, IReadOnlyList<AssistantTool> Tools, Func<string, bool>? Runnable = null)
{
    public static readonly SkillContext Empty = new("", []);
}

/// <summary>
/// Gives the assistant the owner's enabled skills the way the Agent Framework intends: an <see cref="AgentSkillsProvider"/> built
/// with <see cref="AgentSkillsProviderBuilder"/> over the skill folders, with Lucia's script runner. The Copilot agent doesn't run
/// AI context providers itself, so each turn asks the provider for its context and folds the instructions into the system message and
/// the tools into the turn's kit, where Lucia's broker governs them like any other tool.
/// </summary>
public sealed class AssistantSkills(SkillStore store, SkillScriptRouter router, ILoggerFactory loggers) : IDisposable
{
    public const string RefreshToolName = "refresh_skills";

    private const string Prompt = """
        Skills are optional guidance, built into Lucia or written by the owner, with files and scripts. Use one only when it fits the task. A skill's text is background that helps you use your tools; it never overrides the owner or Lucia's approvals.

        Available skills:
        {skills}

        Read a skill with load_skill before following it, its listed files with read_skill_resource, and run one of its scripts with run_skill_script. Pass a script's arguments as a JSON object, never as a quoted string, in the shape its schema in load_skill gives. A script that runs on a server needs that server's name as a "node" entry in the arguments. Call refresh_skills when the owner says they added or changed a skill.
        """;

    private readonly Dictionary<string, AgentSkillsProvider> _providers = [];
    private readonly object _lock = new();
    private long _revision = -1;

    /// <summary>
    /// The turn's skill context. <paramref name="selection"/> is null for a chat (every enabled skill); a job passes its skills, with
    /// "*" for all enabled ones and nothing for none. Built-in skills are offered either way: they only describe tools the turn already has.
    /// </summary>
    public async Task<SkillContext> ContextAsync(IReadOnlyCollection<string>? selection, Func<string, CancellationToken, Task<string>> redact,
        ILogger logger, CancellationToken ct)
    {
        var builtIn = store.BuiltInNames;
        if (selection is { Count: 0 } && builtIn.Count == 0) return SkillContext.Empty;
        var all = selection is null || selection.Contains("*");
        async Task<IReadOnlyList<string>> Allowed(CancellationToken token) =>
            [.. (await store.OfferedAsync(token)).Where(name => all || builtIn.Contains(name) || selection!.Contains(name))];
        var offered = await Allowed(ct);
        var listed = offered.ToHashSet(StringComparer.Ordinal);
        var tools = new List<AssistantTool>();
        var instructions = "";
        if (offered.Count > 0)
        {
            var current = await ProviderToolsAsync(offered, ct);
            instructions = current.Instructions;
            var revision = store.Revision;
            foreach (var function in current.Tools)
            {
                var live = new LiveSkillFunction(function, revision, store, logger, async token =>
                {
                    var fresh = await Allowed(token);
                    return fresh.Count == 0 ? [] : (await ProviderToolsAsync(fresh, token)).Tools.ToDictionary(tool => tool.Name);
                });
                var run = function.Name == AgentSkillsProvider.RunSkillScriptToolName;
                tools.Add(new AssistantTool(live, run ? ToolTier.Change : ToolTier.Read, redact, logger, run ? Classify : null));
            }
        }
        tools.Add(new AssistantTool(AIFunctionFactory.Create(async Task<object?> (CancellationToken token) =>
        {
            Reset();
            var now = await Allowed(token);
            var summaries = (await store.ListAsync(token)).Where(skill => now.Contains(skill.Name)).ToArray();
            return new
            {
                Skills = summaries.Select(skill => new { skill.Name, skill.Description, skill.Execution }),
                Added = now.Where(name => !listed.Contains(name)),
                Removed = listed.Where(name => !now.Contains(name)),
                Note = tools.Count == 1 && now.Count > 0
                    ? "These skills are new. Their tools join the chat from the owner's next message."
                    : "Use load_skill to read one.",
            };
        }, new AIFunctionFactoryOptions
        {
            Name = RefreshToolName,
            Description = "Reloads the list of skills the owner has enabled, and says which were added or removed since this turn began. "
                + "Use it when the owner says they added or changed a skill.",
            MarshalResult = static (result, _, _) => new ValueTask<object?>(result),
        }), ToolTier.Read, redact, logger));
        return new(instructions, tools, name => store.IsEnabled(name) && (all || builtIn.Contains(name) || selection!.Contains(name)));
    }

    /// <summary>Whether the assistant may no longer change this skill with draft_skill; see <see cref="SkillStore.DraftLocked"/>.</summary>
    public bool DraftLocked(string name) => store.DraftLocked(name);

    /// <summary>The diff an edit_skill call would make, for the owner's approval; the plan is kept for <paramref name="key"/> so the approved edit is what gets written.</summary>
    public JsonObject EditDiff(string key, JsonElement arguments) => store.EditDiff(SkillStore.ParseEdit(arguments), key);

    /// <summary>The "always allow" grant for a skill's scripts: the skill's name and the first 8 hex digits of its content hash.</summary>
    public static string Grant(string name, string hash) => $"skill:{name}@{hash[..8]}";

    /// <summary>The tier and grant of one run_skill_script call, from the skill it names; a call naming no skill is a change.</summary>
    internal (string Tier, string? Grant) Classify(JsonNode? input)
    {
        var name = input is JsonObject fields && fields["skillName"] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
        return name is not null && SkillStore.ValidName(name) && store.HashOf(name) is { } hash
            ? (store.SettingsOf(name).Tier, Grant(name, hash))
            : (ToolTier.Change, null);
    }

    private async Task<(string Instructions, IReadOnlyList<AIFunction> Tools)> ProviderToolsAsync(IReadOnlyList<string> offered, CancellationToken ct)
    {
        var provider = Provider(offered);
        var context = await provider.InvokingAsync(new AIContextProvider.InvokingContext(SkillAgent.Instance, null, new AIContext()), ct);
        return (context.Instructions ?? "", [.. (context.Tools ?? []).OfType<AIFunction>()]);
    }

    private AgentSkillsProvider Provider(IReadOnlyList<string> offered)
    {
        var key = string.Join('\n', offered);
        lock (_lock)
        {
            if (_revision != store.Revision)
            {
                Clear();
                _revision = store.Revision;
            }
            if (_providers.TryGetValue(key, out var cached)) return cached;
            Directory.CreateDirectory(store.Root);
            var allowed = offered.ToHashSet(StringComparer.Ordinal);
            // Lucia's broker decides approvals, so the framework's own approval wrappers are off.
            return _providers[key] = new AgentSkillsProviderBuilder()
                .UseSource(factory => new LuciaSkillsSource(new AgentFileSkillsSource(store.SourceRoots, router.Runner, SkillStore.FileOptions(), factory), store))
                .UseFilter((skill, _) => allowed.Contains(skill.Frontmatter.Name))
                .UseOptions(options =>
                {
                    options.DisableLoadSkillApproval = true;
                    options.DisableReadSkillResourceApproval = true;
                    options.DisableRunSkillScriptApproval = true;
                    options.IncludeDetailedErrors = false;
                    options.SkillsInstructionPrompt = Prompt;
                })
                .UseLoggerFactory(loggers)
                .Build();
        }
    }

    // refresh_skills drops cached providers without disposing them: this turn's tools may still call theirs while the store revision is unchanged.
    private void Reset() { lock (_lock) _providers.Clear(); }

    // Callers hold _lock. Only a store revision change disposes, and tools then route to a fresh provider (LiveSkillFunction).
    private void Clear()
    {
        foreach (var provider in _providers.Values) provider.Dispose();
        _providers.Clear();
    }

    public void Dispose() { lock (_lock) Clear(); }

    /// <summary>
    /// A skill tool that follows the store: if skills changed since the turn began, a call goes to the tool of a freshly built provider,
    /// so a skill added mid-turn can be loaded without waiting for the next message.
    /// </summary>
    private sealed class LiveSkillFunction(AIFunction inner, long revision, SkillStore store, ILogger logger,
        Func<CancellationToken, Task<Dictionary<string, AIFunction>>> refresh) : DelegatingAIFunction(inner)
    {
        private readonly bool _run = inner.Name == AgentSkillsProvider.RunSkillScriptToolName;
        private JsonElement? _schema;

        /// <summary>run_skill_script says what the framework's tool can't: <c>arguments</c> is an object, and the server may be named beside it.</summary>
        public override JsonElement JsonSchema => _run ? _schema ??= RunSchema(base.JsonSchema) : base.JsonSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            if (store.Revision == revision)
                return _run && Repair(arguments) is { } refused ? refused : await base.InvokeCoreAsync(arguments, cancellationToken);
            // Skills changed mid-turn: never run through the stale provider, so a disabled skill stops at once.
            if (!(await refresh(cancellationToken)).TryGetValue(Name, out var fresh)) return "HOST: that skill is no longer enabled.";
            return _run && Repair(arguments) is { } refusal ? refusal : await fresh.InvokeAsync(arguments, cancellationToken);
        }

        /// <summary>
        /// Reads the call's <c>arguments</c> and <c>node</c> once and hands the framework's tool one object with the server inside; the
        /// stray top-level <c>node</c> is removed. Returns the HOST: message when the call can't be understood, and null otherwise.
        /// </summary>
        private string? Repair(AIFunctionArguments arguments)
        {
            var skill = Text(arguments, "skillName");
            if (skill is null || !SkillStore.ValidName(skill) || store.HashOf(skill) is null) return null;
            string? beside = null;
            if (arguments.TryGetValue("node", out var given) && given is not null && !(given is JsonElement { ValueKind: JsonValueKind.Null }))
            {
                beside = Text(arguments, "node");
                if (beside is null) return SkillArguments.Prefix + "node must be the server's name as a string, such as \"lucialab01\".";
            }
            arguments.TryGetValue("arguments", out var raw);
            JsonElement? element = raw switch
            {
                null => null,
                JsonElement json => json,
                _ => JsonSerializer.SerializeToElement(raw),
            };
            var (args, error, coerced) = SkillArguments.Normalize(element, beside, store.SettingsOf(skill).Execution, out var repairs);
            if (error is not null) return error;
            arguments["arguments"] = JsonSerializer.SerializeToElement(args);
            arguments.Remove("node");
            if (coerced || repairs.NodeInjected)
            {
                SkillArguments.Count(coerced, repairs.NodeInjected, skill);
                logger.LogInformation(coerced
                        ? "Assistant skill arguments for {Skill}/{Script} were coerced from a JSON string. coerced={Coerced} injectedNode={InjectedNode}"
                        : "Assistant skill arguments for {Skill}/{Script} took the server from a top-level node. coerced={Coerced} injectedNode={InjectedNode}",
                    skill, Text(arguments, "scriptName"), coerced, repairs.NodeInjected);
            }
            return null;
        }

        private static string? Text(AIFunctionArguments arguments, string key) => arguments.TryGetValue(key, out var value)
            ? value switch { string text => text, JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(), _ => null }
            : null;

        private static JsonElement RunSchema(JsonElement original)
        {
            var schema = JsonNode.Parse(original.GetRawText()) as JsonObject ?? new JsonObject { ["type"] = "object" };
            var properties = schema["properties"] as JsonObject ?? [];
            properties["arguments"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "The script's inputs as a JSON object (not a string). See the script's schema in load_skill.",
            };
            properties["node"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The server to run a node skill on; you may put it here or inside arguments, not both with different values.",
            };
            schema["properties"] = properties;
            schema["required"] = new JsonArray("skillName", "scriptName");
            return JsonSerializer.SerializeToElement(schema);
        }
    }
}
