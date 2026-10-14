using System.ComponentModel;
using System.Text.Json;
using Lucia.Homelab.Server.Assistant.Skills;
using Microsoft.Extensions.AI;

namespace Lucia.Homelab.Server.Assistant;

/// <summary>
/// The tools behind <c>/create-skill</c> and review jobs: read the owner's chats, look at every skill without running it, and draft a
/// new skill, always disabled, for the owner to review and enable. Every chat read is scoped to the calling owner.
/// </summary>
public sealed class SkillAuthoring(AssistantChatLog chats, SkillStore store, ILogger<SkillAuthoring> logger, AssistantPush? push = null)
{
    private const int MaxChats = 50, MaxMarked = 50;

    /// <summary>The tools as tier, name, description and body; <see cref="AssistantTools"/> wraps them so results are redacted and capped.</summary>
    internal (string Tier, string Name, string Description, Delegate Body)[] Specs() =>
    [
        (ToolTier.Read, "list_chats",
            "Lists the owner's chats and job runs, newest first, with how many tool calls in each were declined, left unanswered, failed or hit "
            + "a time limit, and when each was last reviewed for skills. Start here to find calls worth turning into a skill.",
            ListChats),
        (ToolTier.Read, "read_chat_transcript",
            "Reads one of the owner's chats, condensed: user messages, trimmed assistant text and every tool call with its input and outcome "
            + "(ran, declined with the owner's reason, expired, failed, timedOut). Use onlyIssues to see just the problem calls.",
            ReadTranscript),
        (ToolTier.Draft, "mark_chats_reviewed",
            "Records that chats were looked at for skills, so the next review skips them. Do it for every chat you read, even when you drafted nothing.",
            MarkReviewed),
        (ToolTier.Read, "list_skills",
            "Lists the enabled and built-in skills with status, author, where each runs, its tier, and for drafts the chats and rationale behind them. "
            + "Pass includeDisabled to also see disabled skills and drafts: do that before drafting, so you don't repeat a skill. It never runs anything.",
            ListSkills),
        (ToolTier.Read, "get_skill",
            "Reads one enabled or built-in skill's SKILL.md and file list, or one file's text with path. Pass includeDisabled to read a disabled skill or draft. It never runs anything.",
            GetSkill),
        (ToolTier.SkillEdit, "edit_skill",
            "Edits an existing skill of the owner's or one you drafted, even an enabled one. The owner sees a diff and must approve every edit; it is never "
            + "automatic, never allowed in Plan mode and never done in a scheduled job. Leave out what stays the same. Use it only when the owner asks "
            + "for the change; to propose a new skill use draft_skill. It keeps the skill's enabled state. Built-in skills can't be edited.",
            EditSkill),
        (ToolTier.Draft, "draft_skill",
            "Creates a skill for the owner to review, or with update rewrites your own earlier draft. It is always saved disabled: the owner reads "
            + "and enables it on the Skills page, and nothing runs until then. Load the skill-authoring skill first and follow it.",
            DraftSkill),
    ];

    /// <summary>The owner behind a chat's session key: the owner id, a dash, then the chat id.</summary>
    internal static string OwnerOf(string session) =>
        session.Length == 49 && session[16] == '-' ? session[..16] : throw new AssistantException(400, "chat_only", "This tool only works in a chat.");

    private object? ListChats(AIFunctionArguments args,
        [Description("Only chats with declined, expired, failed or timed-out calls. Default true.")] bool onlyWithIssues = true,
        [Description("Only chats updated since this ISO 8601 time.")] string? since = null,
        [Description("How many chats, 1 to 50. Default 20.")] int limit = 20)
    {
        var owner = OwnerOf(AssistantTools.Call(args).SessionKey);
        DateTimeOffset? from = null;
        if (!string.IsNullOrWhiteSpace(since))
            from = DateTimeOffset.TryParse(since, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed
                : throw new AssistantException(400, "invalid_since", "Give since as an ISO 8601 time, such as 2026-05-01T00:00:00Z.");
        return chats.List(owner, onlyWithIssues, from, Math.Clamp(limit, 1, MaxChats)).Select(chat => new
        {
            chat.Id, chat.Title, chat.Updated, Job = chat.Job, chat.Issues, chat.ReviewedAt,
        });
    }

    private async Task<object?> ReadTranscript(AIFunctionArguments args, [Description("The chat's id, from list_chats.")] string chatId,
        [Description("Only the problem calls, each with the message before it. Default false.")] bool onlyIssues = false, CancellationToken ct = default)
    {
        var owner = OwnerOf(AssistantTools.Call(args).SessionKey);
        if (await chats.ReadAsync(owner, chatId, ct) is not { } chat)
            throw new AssistantException(404, "chat_not_found", "That chat doesn't exist. Use list_chats to find chat ids.");
        var result = ChatTranscripts.Condense(chat.Messages, onlyIssues);
        var summary = new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = chatId, ["title"] = chat.Info.Title, ["updated"] = chat.Info.Updated,
            ["job"] = chat.Info.JobId is null ? null : chats.JobName(owner, chat.Info.JobId) ?? "job",
            ["issues"] = System.Text.Json.JsonSerializer.SerializeToNode(ChatTranscripts.Count(chat.Messages), AssistantStream.Json),
            ["reviewedAt"] = chats.Reviews(owner).TryGetValue(chatId, out var review) ? review.At : null,
        };
        foreach (var (key, value) in result.ToArray())
        {
            result.Remove(key);
            summary[key] = value;
        }
        return summary;
    }

    private async Task<object?> MarkReviewed(AIFunctionArguments args, [Description("The chat ids you looked at.")] string[] chatIds,
        [Description("A short note, such as what you found or why nothing was drafted.")] string? note = null, CancellationToken ct = default)
    {
        var owner = OwnerOf(AssistantTools.Call(args).SessionKey);
        var ids = (chatIds ?? []).Select(id => id?.Trim() ?? "").Distinct().ToArray();
        if (ids.Length is 0 or > MaxMarked) throw new AssistantException(400, "invalid_chats", $"Give 1 to {MaxMarked} chat ids.");
        if (note is { Length: > 500 }) throw new AssistantException(400, "invalid_note", "Keep the note under 500 characters.");
        RequireOwned(owner, ids);
        await chats.MarkReviewedAsync(owner, ids, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), ct);
        return new { Marked = ids.Length };
    }

    private void RequireOwned(string owner, IEnumerable<string> ids)
    {
        var unknown = ids.Where(id => !AssistantChatLog.IsChatId(id) || chats.ReadInfo(chats.Folder(owner, id)) is null).ToArray();
        if (unknown.Length > 0)
            throw new AssistantException(404, "chat_not_found", "These aren't chats you can use: " + string.Join(", ", unknown.Take(5).Select(id => id.Length <= 40 ? id : id[..40] + "…")) + ". Use list_chats for ids.");
    }

    private async Task<object?> ListSkills([Description("Also list disabled skills and drafts. Pass it when checking for an existing skill before you draft one.")] bool includeDisabled = false,
        CancellationToken ct = default) =>
        (await store.ListAsync(ct)).Where(skill => includeDisabled || skill.Enabled).Select(skill => new
        {
            skill.Name, skill.Description, Status = Status(skill), skill.Author, skill.Execution, skill.Tier, skill.Updated, skill.SourceChats, skill.Rationale,
        });

    private static string Status(SkillSummary skill) => skill.BuiltIn ? "built-in" : skill.Enabled ? "enabled" : "disabled";

    private async Task<object?> GetSkill([Description("The skill's name, from list_skills.")] string name,
        [Description("A file in the skill, such as scripts/check.sh. Leave out to read SKILL.md and the file list.")] string? path = null,
        [Description("Also read a disabled skill or draft.")] bool includeDisabled = false, CancellationToken ct = default)
    {
        var detail = await store.GetAsync(name ?? "", ct);
        if (!detail.Enabled && !includeDisabled)
            throw new AssistantException(404, "skill_disabled", "That skill is disabled. Pass includeDisabled to read it.");
        if (path is not null)
        {
            var file = store.ReadFile(detail.Name, path);
            return file.Editable ? new { detail.Name, file.Path, file.Language, file.Size, file.Content }
                : new { detail.Name, file.Path, file.Language, file.Size, Note = "This file is too large to show." };
        }
        return new
        {
            detail.Name, detail.Description, Status = detail.BuiltIn ? "built-in" : detail.Enabled ? "enabled" : "disabled", detail.Author, detail.Execution,
            detail.Tier, detail.NeedsReview, detail.Rationale, detail.SourceChats, detail.Warnings, detail.Problem, detail.Body,
            Files = detail.Files.Select(file => new { file.Path, file.Kind, file.Size }), detail.ParametersSchema,
        };
    }

    private async Task<object?> DraftSkill(AIFunctionArguments args,
        [Description("The skill's name: lowercase letters, digits and single hyphens, such as check-zfs-health.")] string name,
        [Description("What it does and when to use it, in one or two sentences (up to 1,024 characters).")] string description,
        [Description("Where scripts run: node (the server you name), spark (the Spark, for Lucia and LAN APIs) or sandbox (Python or JavaScript, no host access).")] string execution,
        [Description("read only if it changes nothing; otherwise change or destructive.")] string tier,
        [Description("The SKILL.md body in Markdown: when to use it, the arguments, the steps and what the output means.")] string body,
        [Description("Why this narrow skill, and which declined, expired or timed-out calls it replaces.")] string rationale,
        [Description("Scripts under scripts/, notes under references/ and data under assets/. A file you send replaces the one at that path.")] SkillDraftFile[]? files = null,
        [Description("Ids of the chats this came from, from list_chats.")] string[]? sourceChats = null,
        [Description("Rewrite your own earlier disabled draft of this name instead of refusing the name.")] bool update = false,
        [Description("The JSON Schema of the arguments object the scripts take, as an object (JSON text also works): {\"type\":\"object\",\"properties\":{...},\"required\":[...]}. A node skill's schema gets the \"node\" property added if you leave it out. Always send it.")] JsonElement? parametersSchema = null,
        CancellationToken ct = default)
    {
        var owner = OwnerOf(AssistantTools.Call(args).SessionKey);
        var sources = (sourceChats ?? []).Select(id => id?.Trim() ?? "").Where(id => id.Length > 0).Distinct().ToArray();
        RequireOwned(owner, sources);
        var result = await store.DraftAsync(new(name, description, execution, tier, body, files, sources, rationale, update, SchemaText(parametersSchema)), ct);
        await Notify(owner, (result.Updated ? "Skill draft updated: " : "New skill to review: ") + result.Name,
            "The assistant drafted a skill. Review it on the Skills page and enable it if it looks right.", result.Name, ct);
        return new
        {
            result.Name, result.Status, result.Hash, result.Warnings, result.Review,
            Note = "Saved disabled. The owner reviews it and enables it on the Skills page; it can't run until then.",
        };
    }

    // The owner approved this exact edit in the chat; the broker built the approval's diff from the same arguments.
    private async Task<object?> EditSkill(AIFunctionArguments args,
        [Description("The skill to edit, from list_skills. Built-in skills can't be edited.")] string name,
        [Description("What changes and why, in a sentence or two. The owner reads it with the diff.")] string rationale,
        [Description("The new description, or leave out to keep it.")] string? description = null,
        [Description("node, spark or sandbox, or leave out to keep it.")] string? execution = null,
        [Description("read, change or destructive, or leave out to keep it.")] string? tier = null,
        [Description("The whole new SKILL.md body, or leave out to keep it.")] string? body = null,
        [Description("Files to add or replace under scripts/, references/ or assets/.")] SkillDraftFile[]? files = null,
        [Description("Paths of files to delete from the skill.")] string[]? removeFiles = null,
        [Description("The new JSON Schema of the arguments object the scripts take, as an object (JSON text also works), or leave out to keep it.")] JsonElement? parametersSchema = null, CancellationToken ct = default)
    {
        var owner = OwnerOf(AssistantTools.Call(args).SessionKey);
        // The arguments were read once, when the owner was asked; what runs is that approved plan.
        var result = await store.EditApprovedAsync(AssistantTools.Call(args) is var (session, call) ? session + "|" + call : "", ct);
        await Notify(owner, "Skill edited: " + result.Name, "The assistant edited this skill with your approval.", result.Name, ct);
        return new { result.Name, Status = result.Enabled ? "enabled" : "disabled", result.Hash, result.Warnings, result.Files, Note = "Saved. The skill keeps its enabled state; its content changed, so earlier 'always allow' choices for its scripts no longer apply." };
    }

    // A schema may come as an object or as JSON text; SkillSchemas validates either form once it's stored as text.
    private static string? SchemaText(JsonElement? schema) => schema switch
    {
        null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        { ValueKind: JsonValueKind.Object } value => value.GetRawText(),
        { ValueKind: JsonValueKind.String } value => value.GetString(),
        _ => throw new AssistantException(400, "invalid_schema", "HOST: parametersSchema must be a JSON Schema object."),
    };

    // The draft is saved; telling the owner is best effort.
    private async Task Notify(string owner, string title, string text, string skill, CancellationToken ct)
    {
        if (push is null) return;
        try
        {
            await push.SendAsync(owner, title, text, "/#/settings/assistant/skills?skill=" + skill, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("The new-skill notification could not be sent ({ErrorType}).", e.GetType().Name);
        }
    }
}
