using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lucia.Homelab.Server.Assistant.Skills;

// Edits to an existing owner or assistant skill, which the assistant makes only after the owner approves a diff in the chat.
public sealed partial class SkillStore
{
    private const int MaxDiffFile = 32 * 1024, MaxDiff = 192 * 1024, MaxApproved = 200;
    private static readonly TimeSpan ApprovalLife = TimeSpan.FromHours(2);
    private readonly ConcurrentDictionary<string, (EditPlan Plan, DateTimeOffset At)> _approved = new();

    /// <summary>What an approved edit will write, computed once when the owner is asked and applied as it is.</summary>
    private sealed record EditPlan(string Name, string BaseHash, bool Enabled, string BeforeManifest, string AfterManifest, SkillSettings Settings,
        IReadOnlyList<(string Path, byte[] Data)> Writes, IReadOnlyList<string> Removes, string[] Warnings);

    private static readonly string[] EditFields = ["name", "rationale", "description", "execution", "tier", "body", "files", "removeFiles"];

    /// <summary>
    /// Reads an edit_skill call's arguments once, exactly: the property names are case-sensitive, none may repeat or be unknown, and
    /// each value has its type. The card the owner approves and the write both come from this one reading.
    /// </summary>
    public static SkillEditRequest ParseEdit(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw Invalid("Name the skill to edit.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? name = null, description = null, execution = null, tier = null, body = null, rationale = null;
        SkillDraftFile[]? files = null;
        string[]? removes = null;
        foreach (var property in arguments.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw Invalid($"{Shown(property.Name)} is given twice.");
            if (!EditFields.Contains(property.Name, StringComparer.Ordinal)) throw Invalid($"edit_skill has no argument called {Shown(property.Name)}.");
            var value = property.Value;
            switch (property.Name)
            {
                case "name": name = Str(value, property.Name); break;
                case "rationale": rationale = Str(value, property.Name); break;
                case "description": description = Str(value, property.Name); break;
                case "execution": execution = Str(value, property.Name); break;
                case "tier": tier = Str(value, property.Name); break;
                case "body": body = Str(value, property.Name); break;
                case "removeFiles":
                    removes = value.ValueKind == JsonValueKind.Null ? null : value.ValueKind == JsonValueKind.Array
                        ? [.. value.EnumerateArray().Select(item => Str(item, "removeFiles") ?? throw Invalid("removeFiles lists paths."))]
                        : throw Invalid("removeFiles is a list of paths.");
                    break;
                case "files":
                    files = value.ValueKind == JsonValueKind.Null ? null : value.ValueKind == JsonValueKind.Array
                        ? [.. value.EnumerateArray().Select(ParseFile)] : throw Invalid("files is a list of {path, content}.");
                    break;
            }
        }
        return new(name, description, execution, tier, body, files, removes, rationale);
    }

    private static SkillDraftFile ParseFile(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) throw Invalid("Each file is {path, content}.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? path = null, content = null;
        foreach (var property in item.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw Invalid($"{Shown(property.Name)} is given twice in a file.");
            switch (property.Name)
            {
                case "path": path = Str(property.Value, "path"); break;
                case "content": content = Str(property.Value, "content"); break;
                default: throw Invalid($"A file has no property called {Shown(property.Name)}.");
            }
        }
        return new(path, content);
    }

    private static string? Str(JsonElement value, string field) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        _ => throw Invalid($"{field} must be text."),
    };

    private EditPlan PlanEdit(SkillEditRequest request)
    {
        var name = request.Name?.Trim() ?? "";
        if (!ValidName(name)) throw Invalid("Name the skill to edit, from list_skills.");
        RequireOwned(name);
        var previous = ReadMarkdown(name, out var raw)
            ?? throw new AssistantException(409, "skill_invalid", "That skill's SKILL.md can't be read, so it can't be edited.");
        var current = previous.Settings();
        var settings = Settings(request.Execution ?? current.Execution, request.Tier ?? current.Tier, current.AllowedDomains, current.Network);
        var description = Description(request.Description ?? previous.Text("description"));
        var body = Limit(request.Body ?? previous.Body);
        var rationale = request.Rationale?.Trim() ?? "";
        if (rationale.Length is 0 or > MaxRationale) throw Invalid($"Give a rationale of 1 to {MaxRationale} characters: what changes and why.");
        var writes = NormalizeFiles(request.Files, MaxDraftFiles);
        var removes = (request.RemoveFiles ?? []).Select(Normalize).Distinct().ToArray();
        if (removes.Length > MaxDraftFiles) throw new AssistantException(409, "too_many_files", $"Remove at most {MaxDraftFiles} files per call.");
        var inspection = Inspect(name);
        var existing = inspection.Files;
        if (removes.FirstOrDefault(path => existing.All(file => file.Path != path)) is { } missing) throw new AssistantException(404, "file_not_found", $"{missing} isn't in the skill.");
        if (removes.FirstOrDefault(path => writes.Any(file => file.Path == path)) is { } both) throw Invalid($"{both} is both written and removed.");
        var paths = existing.Select(file => file.Path).Except(removes).Union(writes.Select(file => file.Path)).ToArray();
        if (settings.Execution == SkillExecution.Sandbox) RequireSandboxLanguages(paths);
        if (paths.Length > MaxFiles) throw new AssistantException(409, "too_many_files", $"A skill holds at most {MaxFiles} files.");
        var size = existing.Where(file => !removes.Contains(file.Path) && writes.All(item => item.Path != file.Path)).Sum(file => file.Size) + writes.Sum(file => (long)file.Data.Length);
        if (size > MaxSkillBytes) throw new AssistantException(413, "skill_too_large", "A skill is at most 5 MiB.");
        var scripts = new List<(string Path, string Content)>();
        foreach (var path in paths.Where(path => path.StartsWith("scripts/", StringComparison.Ordinal) && SkillLanguages.IsScript(path)))
            if (writes.FirstOrDefault(file => file.Path == path) is { Data: not null } written) scripts.Add((path, Encoding.UTF8.GetString(written.Data)));
            else if (existing.First(file => file.Path == path).Size <= MaxEditableBytes) scripts.Add((path, File.ReadAllText(Resolve(name, path), Encoding.UTF8)));
        var warnings = SkillReviewLint.Check(settings.Execution, settings.Tier, scripts);
        // A draft the owner has already edited or enabled stays locked against the assistant: its marker is never moved back.
        var reviewed = AssistantAuthored(name) && previous.Metadata().GetValueOrDefault(SkillAuthors.LastEditKey)?.ToString() == SkillAuthors.Owner;
        var extra = new Dictionary<string, string?> { [SkillAuthors.LastEditKey] = reviewed ? SkillAuthors.Owner : SkillAuthors.Assistant };
        if (AssistantAuthored(name)) extra[SkillAuthors.WarningsKey] = warnings.Length > 0 ? JsonSerializer.Serialize(warnings) : null;
        return new(name, inspection.Hash, EntryFor(name).Enabled, raw ?? "", SkillMarkdown.Write(name, description, settings, body, previous, extra), settings, writes, removes, warnings);
    }

    /// <summary>
    /// The per-file diff of an edit for the owner's approval card: SKILL.md first, then each added, changed or removed file. A file is
    /// changed when its bytes differ; only the text shown is cut, at 32 KiB a side and 192 KiB in all, with <c>truncated</c> set where
    /// text was left out. Throws when the edit is invalid. With an <paramref name="approvalKey"/> the plan is kept for
    /// <see cref="EditApprovedAsync"/>, together with the skill's hash now.
    /// </summary>
    public JsonObject EditDiff(SkillEditRequest request, string? approvalKey = null)
    {
        var plan = PlanEdit(request);
        var files = new JsonArray();
        var total = 0;
        var truncated = false;
        void Add(string path, string change, string? before, string? after)
        {
            var entry = new JsonObject { ["path"] = path, ["change"] = change };
            var cut = false;
            string? Fit(string? text)
            {
                if (text is null) return null;
                if (text.Length > MaxDiffFile) { text = text[..MaxDiffFile]; cut = true; }
                if (total + text.Length > MaxDiff) { cut = true; return null; }
                total += text.Length;
                return text;
            }
            if (Fit(before) is { } b) entry["before"] = b;
            if (Fit(after) is { } a) entry["after"] = a;
            if (cut) { entry["truncated"] = true; truncated = true; }
            files.Add(entry);
        }
        if (plan.BeforeManifest != plan.AfterManifest) Add(Manifest, "changed", plan.BeforeManifest, plan.AfterManifest);
        var existing = Inspect(plan.Name).Files;
        foreach (var (path, data) in plan.Writes)
        {
            if (existing.Any(file => file.Path == path))
            {
                if (!File.ReadAllBytes(Resolve(plan.Name, path)).AsSpan().SequenceEqual(data)) Add(path, "changed", ReadText(plan.Name, path), Encoding.UTF8.GetString(data));
            }
            else Add(path, "added", null, Encoding.UTF8.GetString(data));
        }
        foreach (var path in plan.Removes) Add(path, "removed", ReadText(plan.Name, path), null);
        if (approvalKey is not null) Keep(approvalKey, plan);
        var diff = new JsonObject { ["enabled"] = plan.Enabled, ["files"] = files };
        if (truncated) diff["truncated"] = true;
        return diff;
    }

    private void Keep(string key, EditPlan plan)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var old in _approved.Where(item => now - item.Value.At > ApprovalLife).Select(item => item.Key).ToArray()) _approved.TryRemove(old, out _);
        foreach (var old in _approved.OrderBy(item => item.Value.At).Take(Math.Max(0, _approved.Count - MaxApproved)).Select(item => item.Key).ToArray()) _approved.TryRemove(old, out _);
        _approved[key] = (plan, now);
    }

    private string ReadText(string name, string path)
    {
        var file = new FileInfo(Resolve(name, path));
        using var stream = file.OpenRead();
        var buffer = new byte[Math.Min(file.Length, MaxDiffFile + 4)];
        var read = stream.Read(buffer, 0, buffer.Length);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    /// <summary>
    /// Writes the edit the owner approved under <paramref name="approvalKey"/>, exactly as shown, once. The skill keeps its enabled state
    /// and its hash changes, so grants tied to the old hash stop applying. Refused when the skill changed since the owner was asked.
    /// </summary>
    public async Task<SkillEditResult> EditApprovedAsync(string approvalKey, CancellationToken ct)
    {
        if (!_approved.TryRemove(approvalKey, out var kept))
            throw new AssistantException(409, "edit_not_approved", "This edit wasn't approved by the owner, so it didn't run.");
        var plan = kept.Plan;
        await Locked(async () =>
        {
            if (!Directory.Exists(Dir(plan.Name)) || Inspect(plan.Name).Hash != plan.BaseHash)
                throw new AssistantException(409, "skill_changed", "The skill changed after you approved; ask again.");
            foreach (var (path, data) in plan.Writes) WriteBytes(Resolve(plan.Name, path), data);
            foreach (var path in plan.Removes) File.Delete(Resolve(plan.Name, path));
            WriteText(Path.Combine(Dir(plan.Name), Manifest), plan.AfterManifest);
            await Touch(plan.Name, entry => entry, ct);
        }, ct);
        return new(plan.Name, plan.Enabled, HashOf(plan.Name) ?? "", plan.Warnings, [Manifest, .. plan.Writes.Select(file => file.Path), .. plan.Removes]);
    }
}
