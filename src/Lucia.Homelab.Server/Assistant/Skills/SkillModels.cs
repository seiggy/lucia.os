namespace Lucia.Homelab.Server.Assistant.Skills;

/// <summary>Where a skill's scripts run: on the server the model names, pinned to the Spark, or in a Hyperlight sandbox.</summary>
public static class SkillExecution
{
    public const string Node = "node", Spark = "spark", Sandbox = "sandbox";

    public static bool Valid(string? value) => value is Node or Spark or Sandbox;
}

/// <summary>Whether a Spark-mode script's container may reach the network: none (the default) or the Docker bridge for outbound access.</summary>
public static class SkillNetwork
{
    public const string None = "none", Bridge = "bridge";
}

/// <summary>A skill's Lucia settings, kept in its SKILL.md frontmatter <c>metadata</c> map so the package stays portable.</summary>
public sealed record SkillSettings(string Execution, string Tier, string[] AllowedDomains, string Network = SkillNetwork.None)
{
    public const string ExecutionKey = "lucia.execution", TierKey = "lucia.tier", DomainsKey = "lucia.allowed-domains", NetworkKey = "lucia.network";

    public static readonly SkillSettings Default = new(SkillExecution.Node, ToolTier.Change, []);

    /// <summary>Reads the settings from a metadata map, falling back to the defaults for anything missing or unknown.</summary>
    public static SkillSettings From(IEnumerable<KeyValuePair<string, object?>>? metadata)
    {
        var map = metadata?.ToDictionary(pair => pair.Key, pair => pair.Value?.ToString(), StringComparer.Ordinal);
        string? Value(string key) => map is not null && map.TryGetValue(key, out var text) ? text?.Trim() : null;
        var execution = Value(ExecutionKey) is { } mode && SkillExecution.Valid(mode) ? mode : SkillExecution.Node;
        var tier = Value(TierKey) is ToolTier.Read or ToolTier.Change or ToolTier.Destructive ? Value(TierKey)! : ToolTier.Change;
        var domains = (Value(DomainsKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new(execution, tier, domains, Value(NetworkKey) == SkillNetwork.Bridge ? SkillNetwork.Bridge : SkillNetwork.None);
    }
}

public sealed record SkillSummary(string Name, string Description, string Execution, string Tier, bool Enabled, string Hash, DateTimeOffset Updated,
    string? Source, int Scripts, string? Problem, bool BuiltIn = false, string Author = SkillAuthors.Owner, bool NeedsReview = false,
    string? Rationale = null, string[]? SourceChats = null, string[]? Warnings = null)
{
    public string[] SourceChats { get; init; } = SourceChats ?? [];
    public string[] Warnings { get; init; } = Warnings ?? [];
}

/// <summary>Who wrote a skill: the owner, the assistant (a draft for the owner to review) or Lucia (built in), and the metadata keys that record it.</summary>
public static class SkillAuthors
{
    public const string Owner = "owner", Assistant = "assistant", Lucia = "lucia";
    public const string AuthorKey = "lucia.author", SourceChatsKey = "lucia.source-chats", RationaleKey = "lucia.rationale",
        WarningsKey = "lucia.review-warnings", LastEditKey = "lucia.last-edit";
}

/// <param name="Update">Replace the assistant's own disabled draft of that name instead of refusing a name that is taken.</param>
public sealed record SkillDraftRequest(string? Name, string? Description, string? Execution, string? Tier, string? Body, SkillDraftFile[]? Files,
    string[]? SourceChats, string? Rationale, bool Update = false);

public sealed record SkillDraftFile(string? Path, string? Content);

public sealed record SkillDraftResult(string Name, string Status, string Hash, string[] Warnings, string Review, bool Updated);

/// <summary>An edit to an existing skill; anything left out stays as it is. <paramref name="RemoveFiles"/> deletes files from the skill.</summary>
public sealed record SkillEditRequest(string? Name, string? Description, string? Execution, string? Tier, string? Body, SkillDraftFile[]? Files,
    string[]? RemoveFiles, string? Rationale);

public sealed record SkillEditResult(string Name, bool Enabled, string Hash, string[] Warnings, string[] Files);

public sealed record SkillFile(string Path, string Kind, long Size, string? Language, bool Editable);

public sealed record SkillNodeState(string Node, bool Synced);

public sealed record SkillDetail(string Name, string Description, string Execution, string Tier, bool Enabled, string Hash, DateTimeOffset Updated,
    string? Source, int Scripts, string? Problem, string[] AllowedDomains, string? License, string? Compatibility, string Body, SkillFile[] Files,
    SkillNodeState[]? Nodes = null, string Network = SkillNetwork.None, bool BuiltIn = false)
{
    public SkillDetail(SkillSummary summary, string[] allowedDomains, string? license, string? compatibility, string body, SkillFile[] files)
        : this(summary.Name, summary.Description, summary.Execution, summary.Tier, summary.Enabled, summary.Hash, summary.Updated, summary.Source,
            summary.Scripts, summary.Problem, allowedDomains, license, compatibility, body, files, null, SkillNetwork.None, summary.BuiltIn)
    {
        Author = summary.Author;
        NeedsReview = summary.NeedsReview;
        Rationale = summary.Rationale;
        SourceChats = summary.SourceChats;
        Warnings = summary.Warnings;
    }

    public string Author { get; init; } = SkillAuthors.Owner;
    public bool NeedsReview { get; init; }
    public string? Rationale { get; init; }
    public string[] SourceChats { get; init; } = [];
    public string[] Warnings { get; init; } = [];
}

public sealed record SkillCreateRequest(string? Name, string? Description, string? Execution, string? Tier, string[]? AllowedDomains, string? Body,
    string? Network = null);

public sealed record SkillUpdateRequest(string? Description, string? Execution, string? Tier, string[]? AllowedDomains, string? Body,
    string? Network = null);

/// <param name="Hash">The hash of the version the owner reviewed. Required to enable an assistant-authored skill.</param>
public sealed record SkillEnabledRequest(bool Enabled, string? Hash = null);

public sealed record SkillDuplicateRequest(string? Name);

public sealed record SkillFileRequest(string? Content);

public sealed record SkillRenameRequest(string? From, string? To);

public sealed record SkillCheckRequest(string? Path, string? Content, string? Node);

public sealed record SkillGitHubRequest(string? Url);

public sealed record SkillFileContent(string Path, string Content, string? Language, long Size, bool Editable);

public sealed record SkillDiagnostic(int Line, int Column, int? EndLine, int? EndColumn, string Severity, string Message);

public sealed record SkillCheckResult(bool Ok, SkillDiagnostic[] Diagnostics, string? CheckedOn, string? Problem);

/// <summary>What <c>skills/index.json</c> keeps about a skill beyond its package: the owner's choices, not part of the folder.</summary>
public sealed record SkillIndexEntry(bool Enabled, string? Source, DateTimeOffset Updated, string Hash);

public sealed record SkillIndex(Dictionary<string, SkillIndexEntry> Skills);

/// <summary>The extensions Lucia accepts in a skill, and the language each belongs to.</summary>
public static class SkillLanguages
{
    public const string Javascript = "javascript", Typescript = "typescript", Python = "python", Shell = "shell", Csharp = "csharp";

    public static readonly string[] ScriptExtensions = [".sh", ".py", ".js", ".mjs", ".ts", ".mts", ".cs"];

    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        [".js"] = Javascript, [".mjs"] = Javascript, [".ts"] = Typescript, [".mts"] = Typescript, [".py"] = Python, [".sh"] = Shell,
        [".cs"] = Csharp, [".md"] = "markdown", [".json"] = "json", [".yaml"] = "yaml", [".yml"] = "yaml", [".txt"] = "text", [".csv"] = "text",
        [".tmpl"] = "text", [".template"] = "text", [".conf"] = "text", [".ini"] = "text", [".toml"] = "text",
    };

    public static string? Language(string path) => Languages.GetValueOrDefault(Path.GetExtension(path));

    public static bool IsScript(string path) => ScriptExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether Hyperlight's guests can run the file: JavaScript and Python only.</summary>
    public static bool SandboxCanRun(string path) => Path.GetExtension(path).ToLowerInvariant() is ".js" or ".mjs" or ".py";
}
