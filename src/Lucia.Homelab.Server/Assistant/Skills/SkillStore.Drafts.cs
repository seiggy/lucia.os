using System.Text;
using System.Text.Json;
using Lucia.Homelab.Server.Domains;

namespace Lucia.Homelab.Server.Assistant.Skills;

// Skills the assistant drafts for the owner to review. A draft is always disabled; the assistant may rewrite its own draft until the
// owner edits or enables it, and never changes any other skill.
public sealed partial class SkillStore
{
    private const int MaxDraftFiles = 40, MaxSourceChats = 10, MaxRationale = 1000;
    private static readonly IReadOnlyDictionary<string, string?> OwnerCopy = new Dictionary<string, string?>
    {
        [SkillAuthors.AuthorKey] = null, [SkillAuthors.SourceChatsKey] = null, [SkillAuthors.RationaleKey] = null,
        [SkillAuthors.WarningsKey] = null, [SkillAuthors.LastEditKey] = null,
    };

    /// <summary>Whether the skill is enabled, built-ins always. Read live, so a skill disabled mid-turn stops at once.</summary>
    public bool IsEnabled(string name) => ValidName(name) && Directory.Exists(Dir(name)) && EntryFor(name).Enabled;

    /// <summary>The assistant wrote this skill (it was drafted, not imported or created by the owner).</summary>
    public bool AssistantAuthored(string name) => ValidName(name) && !IsBuiltIn(name) && Directory.Exists(Dir(name)) && EntryFor(name).Source == SkillAuthors.Assistant;

    /// <summary>
    /// Whether the assistant may no longer change this skill: it exists and is enabled, built in, not its own draft, or the owner has
    /// edited it. False for a name nothing uses.
    /// </summary>
    public bool DraftLocked(string name)
    {
        if (!ValidName(name) || !Directory.Exists(Dir(name))) return false;
        if (IsBuiltIn(name) || !AssistantAuthored(name) || EntryFor(name).Enabled) return true;
        return ReadMarkdown(name, out _)?.Metadata().GetValueOrDefault(SkillAuthors.LastEditKey)?.ToString() == SkillAuthors.Owner;
    }

    private IReadOnlyDictionary<string, string?>? OwnerEdit(string name) =>
        AssistantAuthored(name) ? new Dictionary<string, string?> { [SkillAuthors.LastEditKey] = SkillAuthors.Owner } : null;

    /// <summary>Records that the owner changed an assistant draft, which locks it against the assistant. Callers hold the gate.</summary>
    private void MarkOwnerEdit(string name)
    {
        if (!AssistantAuthored(name) || ReadMarkdown(name, out _) is not { } markdown
            || markdown.Metadata().GetValueOrDefault(SkillAuthors.LastEditKey)?.ToString() == SkillAuthors.Owner) return;
        WriteText(Path.Combine(Dir(name), Manifest), SkillMarkdown.Write(name, markdown.Text("description") ?? "", markdown.Settings(), markdown.Body, markdown, OwnerEdit(name)));
    }

    private static string? Clip(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Length <= MaxRationale ? text : text[..MaxRationale];

    private static string[] Split(string? text) =>
        [.. (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MaxSourceChats)];

    private static string[] Warnings(string? json)
    {
        try { return json is { Length: > 0 } && JsonSerializer.Deserialize<string[]>(json) is { } list ? [.. list.Take(50)] : []; }
        catch (JsonException) { return []; }
    }

    /// <summary>Checks the files an assistant sends and encodes them: safe skill-relative paths, no repeats, each within the file limit.</summary>
    private static List<(string Path, byte[] Data)> NormalizeFiles(SkillDraftFile[]? files, int max)
    {
        files ??= [];
        if (files.Length > max) throw new AssistantException(409, "too_many_files", $"Send at most {max} files per call.");
        var normalized = new List<(string Path, byte[] Data)>();
        foreach (var file in files)
        {
            var relative = Normalize(file?.Path);
            if (normalized.Any(item => item.Path == relative)) throw Invalid($"{relative} is listed twice.");
            var data = new UTF8Encoding(false).GetBytes(file!.Content ?? throw Invalid($"Send the content of {relative}."));
            if (data.Length > MaxFileBytes) throw new AssistantException(413, "file_too_large", "Skill files are at most 1 MiB.");
            normalized.Add((relative, data));
        }
        return normalized;
    }
    /// <summary>
    /// Creates a disabled skill drafted by the assistant, or with <see cref="SkillDraftRequest.Update"/> rewrites its own disabled
    /// draft. Uses the same validation as the owner's writes; a name that is taken and isn't the assistant's own untouched draft is refused.
    /// </summary>
    public async Task<SkillDraftResult> DraftAsync(SkillDraftRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (!ValidName(name)) throw Invalid("Name a skill with lowercase letters, digits and single hyphens, up to 64 characters.");
        var settings = Settings(request.Execution, request.Tier, null);
        var description = Description(request.Description);
        var body = request.Body is { Length: > 0 } text ? Limit(text) : $"# {name}\n\nSay when to use this skill and what to do, step by step.\n";
        var rationale = request.Rationale?.Trim() ?? "";
        if (rationale.Length is 0 or > MaxRationale) throw Invalid($"Give a rationale of 1 to {MaxRationale} characters: why this narrow skill, and which calls it replaces.");
        var sources = (request.SourceChats ?? []).Select(id => id?.Trim() ?? "").Where(id => id.Length > 0).Distinct().ToArray();
        if (sources.Length > MaxSourceChats || sources.Any(id => id.Length != 32 || !id.All(char.IsAsciiHexDigitLower)))
            throw Invalid($"List up to {MaxSourceChats} source chats by their ids.");
        var normalized = NormalizeFiles(request.Files, MaxDraftFiles);
        string? schemaText = null;
        if (request.ParametersSchema is { Length: > 0 } declared && SkillSchemas.Check(declared, out schemaText) is { } schemaProblem) throw Invalid(schemaProblem);
        IReadOnlyDictionary<string, string?> Provenance(string[]? found)
        {
            var extra = new Dictionary<string, string?>
            {
                [SkillAuthors.AuthorKey] = SkillAuthors.Assistant,
                [SkillAuthors.SourceChatsKey] = sources.Length > 0 ? string.Join(',', sources) : null,
                [SkillAuthors.RationaleKey] = rationale,
                [SkillAuthors.WarningsKey] = found is { Length: > 0 } ? JsonSerializer.Serialize(found) : null,
                [SkillAuthors.LastEditKey] = SkillAuthors.Assistant,
            };
            if (schemaText is not null) extra[SkillSchemas.Key] = schemaText;
            return extra;
        }
        var updated = false;
        string[] warnings = [];
        await Locked(async () =>
        {
            var exists = Directory.Exists(Dir(name));
            if (exists && !request.Update)
                throw new AssistantException(409, "skill_exists", $"There is already a skill named {name}. get_skill it and update your own draft (update: true), or pick another name.");
            if (exists && DraftLocked(name))
                throw new AssistantException(409, "draft_locked", "Only your own disabled drafts can be updated. get_skill the existing skill, or pick another name for a new draft.");
            if (!exists) RequireRoom();
            updated = exists;
            var previous = exists ? ReadMarkdown(name, out _) : null;
            if (exists && previous is null) throw new AssistantException(409, "draft_locked", "That draft's SKILL.md can't be read, so it can't be updated.");
            var current = exists ? Inspect(name).Files : [];
            var paths = current.Select(file => file.Path).Union(normalized.Select(file => file.Path)).ToArray();
            if (settings.Execution == SkillExecution.Sandbox) RequireSandboxLanguages(paths);
            if (paths.Length > MaxFiles) throw new AssistantException(409, "too_many_files", $"A skill holds at most {MaxFiles} files.");
            var size = current.Where(file => normalized.All(item => item.Path != file.Path)).Sum(file => file.Size) + normalized.Sum(file => (long)file.Data.Length);
            if (size > MaxSkillBytes) throw new AssistantException(413, "skill_too_large", "A skill is at most 5 MiB.");
            DomainOnboardingStore.EnsureDirectory(Root);
            try
            {
                DomainOnboardingStore.EnsureDirectory(Dir(name));
                // The manifest goes first: a skill folder without one reads as broken, and the lint reads the folder.
                WriteText(Path.Combine(Dir(name), Manifest), SkillMarkdown.Write(name, description, settings, body, previous, Provenance(null)));
                foreach (var (path, data) in normalized) WriteBytes(Resolve(name, path), data);
                var scripts = new List<(string Path, string Content)>();
                foreach (var file in Inspect(name).Files.Where(file => file.Kind == "script" && file.Size <= MaxEditableBytes))
                    scripts.Add((file.Path, File.ReadAllText(Resolve(name, file.Path), Encoding.UTF8)));                warnings = SkillReviewLint.Check(settings.Execution, settings.Tier, scripts);
                WriteText(Path.Combine(Dir(name), Manifest), SkillMarkdown.Write(name, description, settings, body, previous, Provenance(warnings)));
                await Touch(name, entry => new(false, SkillAuthors.Assistant, DateTimeOffset.UtcNow, ""), ct);
            }
            catch when (!exists)
            {
                if (Directory.Exists(Dir(name))) Directory.Delete(Dir(name), recursive: true);
                throw;
            }
        }, ct);
        return new(name, "disabled", HashOf(name) ?? "", warnings, "#/settings/assistant/skills?skill=" + name, updated);
    }
}
