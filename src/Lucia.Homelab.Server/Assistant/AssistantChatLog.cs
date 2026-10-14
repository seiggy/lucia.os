using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Assistant;

/// <summary>How many tool calls in a chat the owner declined, left unanswered, that failed or that hit a time limit.</summary>
public sealed record ChatIssues(int Declined, int Expired, int Failed, int TimedOut)
{
    public static readonly ChatIssues None = new(0, 0, 0, 0);

    public int Total => Declined + Expired + Failed + TimedOut;
}

public sealed record ChatReview(DateTimeOffset At, string? Note);

/// <summary>One chat as the skill-authoring tools list it.</summary>
public sealed record ChatListing(string Id, string Title, DateTimeOffset Updated, string? Job, ChatIssues Issues, DateTimeOffset? ReviewedAt);

/// <summary>
/// Reads an owner's saved chats for the skill-authoring tools and the sessions list: which calls went wrong, and which chats were
/// already looked at for skills. Everything is scoped to the owner's own folder.
/// </summary>
public sealed class AssistantChatLog(IOptions<AssistantOptions> options, ILogger<AssistantChatLog> logger)
{
    private const long TranscriptLimit = 16 * 1024 * 1024;
    private const int MaxReviewed = 2000;
    private readonly ConcurrentDictionary<string, (DateTime Written, long Length, ChatIssues Issues)> _issues = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string Root => Path.GetFullPath(options.Value.Directory);

    private string ReviewPath => Path.Combine(Root, "skill-reviews.json");

    public static bool IsChatId(string? id) => Guid.TryParseExact(id, "N", out var guid) && guid.ToString("N") == id;

    public string Folder(string owner, string id) => Path.Combine(Root, "users", owner, id);

    /// <summary>The issues in the chat saved in <paramref name="folder"/>, cached until its transcript changes.</summary>
    public ChatIssues IssuesIn(string folder)
    {
        var path = Path.Combine(folder, "transcript.json");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > TranscriptLimit) return ChatIssues.None;
            if (_issues.TryGetValue(path, out var cached) && cached.Written == info.LastWriteTimeUtc && cached.Length == info.Length) return cached.Issues;
            using var file = File.OpenRead(path);
            var transcript = JsonSerializer.Deserialize<AssistantTranscript>(file, AssistantStream.Json);
            var issues = transcript is null ? ChatIssues.None : ChatTranscripts.Count(transcript.Messages);
            _issues[path] = (info.LastWriteTimeUtc, info.Length, issues);
            return issues;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("A chat's issues could not be counted ({ErrorType}).", e.GetType().Name);
            return ChatIssues.None;
        }
    }

    public AssistantSessionInfo? ReadInfo(string folder)
    {
        var path = Path.Combine(folder, "session.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AssistantSessionInfo>(File.ReadAllBytes(path), AssistantStream.Json) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("An assistant chat could not be read ({ErrorType}).", e.GetType().Name);
            return null;
        }
    }

    /// <summary>The owner's chat, or null when it doesn't exist or is too large to read.</summary>
    public async Task<(AssistantSessionInfo Info, List<UiMessage> Messages)?> ReadAsync(string owner, string id, CancellationToken ct)
    {
        if (!IsChatId(id)) return null;
        var folder = Folder(owner, id);
        var path = Path.Combine(folder, "transcript.json");
        try
        {
            if (ReadInfo(folder) is not { } info || !File.Exists(path) || new FileInfo(path).Length > TranscriptLimit) return null;
            await using var file = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<AssistantTranscript>(file, AssistantStream.Json, ct) is { } transcript ? (info, transcript.Messages) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("An assistant chat could not be read ({ErrorType}).", e.GetType().Name);
            return null;
        }
    }

    /// <summary>The name of the owner's job with this id, for labelling the chats it ran.</summary>
    public string? JobName(string owner, string? jobId)
    {
        if (!IsChatId(jobId)) return null;
        try
        {
            var path = Path.Combine(Root, "users", owner, "jobs", jobId + ".json");
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The owner's chats and job runs, newest first, with their issue counts and when each was last reviewed for skills.</summary>
    public IReadOnlyList<ChatListing> List(string owner, bool onlyWithIssues, DateTimeOffset? since, int limit)
    {
        var root = Path.Combine(Root, "users", owner);
        if (!Directory.Exists(root)) return [];
        var reviews = Reviews(owner);
        var listed = new List<ChatListing>();
        foreach (var chat in Directory.EnumerateDirectories(root)
            .Select(folder => (Id: Path.GetFileName(folder), Folder: folder)).Where(chat => IsChatId(chat.Id))
            .Select(chat => (chat.Id, chat.Folder, Info: ReadInfo(chat.Folder))).Where(chat => chat.Info is not null)
            .OrderByDescending(chat => chat.Info!.Updated))
        {
            if (since is { } from && chat.Info!.Updated < from) continue;
            var issues = IssuesIn(chat.Folder);
            if (onlyWithIssues && issues.Total == 0) continue;
            listed.Add(new(chat.Id, chat.Info!.Title, chat.Info.Updated, chat.Info.JobId is null ? null : JobName(owner, chat.Info.JobId) ?? "job",
                issues, reviews.TryGetValue(chat.Id, out var review) ? review.At : null));
            if (listed.Count >= limit) break;
        }
        return listed;
    }

    /// <summary>When each of the owner's chats was last reviewed for skills.</summary>
    public IReadOnlyDictionary<string, ChatReview> Reviews(string owner)
    {
        var all = ReadReviews();
        return all.TryGetValue(owner, out var mine) ? mine : new Dictionary<string, ChatReview>();
    }

    /// <summary>Records that the owner's chats were looked at for skills. The ids must be chats the owner has.</summary>
    public async Task MarkReviewedAsync(string owner, IReadOnlyCollection<string> ids, string? note, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var all = ReadReviews();
            if (!all.TryGetValue(owner, out var mine)) all[owner] = mine = [];
            var now = DateTimeOffset.UtcNow;
            foreach (var id in ids) mine[id] = new(now, note);
            // Keep the file bounded: the oldest reviews go first.
            foreach (var old in mine.OrderBy(pair => pair.Value.At).Take(Math.Max(0, mine.Count - MaxReviewed)).Select(pair => pair.Key).ToArray()) mine.Remove(old);
            await DomainOnboardingStore.WriteJson(ReviewPath, all, ct, AssistantStream.Json);
        }
        finally { _gate.Release(); }
    }

    private Dictionary<string, Dictionary<string, ChatReview>> ReadReviews()
    {
        try
        {
            if (!File.Exists(ReviewPath) || new FileInfo(ReviewPath).Length > 4 * 1024 * 1024) return [];
            DomainOnboardingStore.RejectLinks(ReviewPath);
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, ChatReview>>>(File.ReadAllBytes(ReviewPath), AssistantStream.Json) ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("The skill reviews could not be read ({ErrorType}).", e.GetType().Name);
            return [];
        }
    }
}

/// <summary>Turns a saved chat into the compact account a model reads when it looks for calls worth a skill.</summary>
public static partial class ChatTranscripts
{
    public const string Declined = "declined", Expired = "expired", Failed = "failed", TimedOut = "timedOut", Ran = "ran", Refused = "refused";
    private const int BudgetBytes = 44 * 1024, UserText = 1500, AssistantText = 800, InputText = 500, ResultText = 240, ErrorText = 400;

    /// <summary>The outcome of one tool part, and the owner's reason, the error or the note that explains it.</summary>
    public static (string Outcome, string? Detail) Outcome(UiPart part)
    {
        var approval = part.Approval;
        var automatic = approval?["isAutomatic"]?.GetValue<bool>() == true;
        var reason = approval?["reason"] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
        switch (part.State)
        {
            case "output-denied":
                if (automatic) return (Refused, reason);
                return reason is not null && (reason.StartsWith("No answer within", StringComparison.Ordinal)
                    || reason.StartsWith("The run stopped before you answered", StringComparison.Ordinal)) ? (Expired, reason) : (Declined, reason);
            case "approval-requested":
                return (Expired, "The owner never answered.");
            case "output-error":
                var error = part.ErrorText ?? "The tool failed.";
                return TimeoutWords().IsMatch(error) ? (TimedOut, error) : (Failed, error);
            case "output-available":
                var (timedOut, note) = TimedOutResult(part.Output);
                return timedOut ? (TimedOut, note) : (Ran, null);
            default:
                return (Failed, "The call never finished.");
        }
    }

    /// <summary>
    /// A command that hit its time limit: run_command reports exit code 124 or Running, with a "stopped answering" note when the
    /// server went quiet; a skill's script says "Exit code 124" in its text.
    /// </summary>
    private static (bool TimedOut, string? Note) TimedOutResult(JsonNode? output)
    {
        if (output is JsonObject fields)
        {
            var code = fields["exitCode"] is JsonValue exit && exit.TryGetValue(out int number) ? number : (int?)null;
            var note = fields["note"] is JsonValue noteValue && noteValue.TryGetValue(out string? noteText) ? noteText : null;
            var running = fields["running"] is JsonValue running_ && running_.TryGetValue(out bool isRunning) && isRunning;
            if (code == 124 || running || note is not null && StoppedAnswering().IsMatch(note))
                return (true, note ?? (code == 124 ? "Exit code 124: the command hit its time limit." : null));
            return (false, null);
        }
        if (output is JsonValue text && text.TryGetValue(out string? plain) && TimedOutText().IsMatch(plain))
            return (true, Clip(plain, ErrorText));
        return (false, null);
    }

    public static ChatIssues Count(IEnumerable<UiMessage> messages)
    {
        int declined = 0, expired = 0, failed = 0, timedOut = 0;
        foreach (var part in messages.Where(message => message.Role == "assistant").SelectMany(message => message.Parts).Where(part => part.Type == "dynamic-tool"))
            switch (Outcome(part).Outcome)
            {
                case Declined: declined++; break;
                case Expired: expired++; break;
                case Failed: failed++; break;
                case TimedOut: timedOut++; break;
            }
        return new(declined, expired, failed, timedOut);
    }

    private static bool IsProblem(string outcome) => outcome is Declined or Expired or Failed or TimedOut;

    /// <summary>
    /// The chat as JSON the model reads: user messages, trimmed assistant text and every tool call with its masked input and outcome.
    /// <paramref name="onlyIssues"/> keeps just the problem calls, each with the user message and assistant text before it. The whole
    /// is kept under about 44 KB by dropping the oldest turns that have no problem first.
    /// </summary>
    public static JsonObject Condense(IReadOnlyList<UiMessage> messages, bool onlyIssues)
    {
        var turns = new List<(JsonObject Entry, bool Problem)>();
        UiMessage? lastUser = null;
        var userAdded = false;
        foreach (var message in messages)
        {
            if (message.Role == "user")
            {
                lastUser = message;
                userAdded = false;
                if (!onlyIssues) turns.Add((UserEntry(message), false));
                continue;
            }
            if (message.Role != "assistant") continue;
            var calls = new JsonArray();
            var problems = 0;
            foreach (var part in message.Parts.Where(part => part.Type == "dynamic-tool"))
            {
                var (outcome, detail) = Outcome(part);
                if (IsProblem(outcome)) problems++;
                else if (onlyIssues) continue;
                calls.Add(Call(part, outcome, detail));
            }
            if (onlyIssues && problems == 0) continue;
            if (onlyIssues && lastUser is not null && !userAdded)
            {
                turns.Add((UserEntry(lastUser), true));
                userAdded = true;
            }
            var assistant = new JsonObject { ["role"] = "assistant" };
            var words = string.Join("\n", message.Parts.Where(part => part.Type == "text" && !string.IsNullOrWhiteSpace(part.Text)).Select(part => part.Text!.Trim()));
            if (words.Length > 0) assistant["text"] = Clip(words, AssistantText);
            if (calls.Count > 0) assistant["calls"] = calls;
            if (assistant.Count > 1) turns.Add((assistant, problems > 0));
        }
        var omitted = 0;
        while (turns.Sum(turn => turn.Entry.ToJsonString().Length) > BudgetBytes && turns.Count > 1)
        {
            var drop = turns.FindIndex(turn => !turn.Problem);
            turns.RemoveAt(drop < 0 ? 0 : drop);
            omitted++;
        }
        var result = new JsonObject { ["messages"] = new JsonArray(turns.Select(turn => (JsonNode)turn.Entry).ToArray()) };
        if (omitted > 0) result["omitted"] = omitted;
        return result;
    }

    private static JsonObject UserEntry(UiMessage message) => new()
    {
        ["role"] = "user",
        ["text"] = Clip(string.Join("\n", message.Parts.Where(part => part.Type == "text").Select(part => part.Text)), UserText),
    };

    private static JsonObject Call(UiPart part, string outcome, string? detail)
    {
        var call = new JsonObject { ["tool"] = part.ToolName, ["outcome"] = outcome };
        if (part.Input is not null) call["input"] = Shorten(AssistantTools.Mask(part.Input));
        if (outcome is Declined or Refused or Expired) { if (detail is not null) call["reason"] = Clip(detail, ErrorText); }
        else if (outcome is Failed) call["error"] = Clip(detail ?? "", ErrorText);
        else
        {
            if (outcome == TimedOut && detail is not null) call["note"] = Clip(detail, ErrorText);
            if (part.Output is not null)
            {
                if (part.Output is JsonObject fields && fields["exitCode"] is { } code) call["exitCode"] = code.DeepClone();
                var text = part.Output is JsonObject output && output["output"] is JsonValue shown && shown.TryGetValue(out string? body) ? body : part.Output.ToJsonString();
                call["result"] = Clip(text, ResultText);
            }
        }
        return call;
    }

    /// <summary>Copies a call's input with every long string cut, so one huge script can't use the budget.</summary>
    private static JsonNode? Shorten(JsonNode? node) => node switch
    {
        JsonObject fields => new JsonObject(fields.Select(pair => KeyValuePair.Create(pair.Key, Shorten(pair.Value)))),
        JsonArray list => new JsonArray(list.Take(20).Select(Shorten).ToArray()),
        JsonValue value when value.TryGetValue(out string? text) => Clip(text, InputText),
        _ => node?.DeepClone(),
    };

    private static string Clip(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";

    [GeneratedRegex(@"stopped answering", RegexOptions.IgnoreCase, 250)]
    private static partial Regex StoppedAnswering();

    [GeneratedRegex(@"timed out|time limit|timeout|stopped answering|exit code 124\b", RegexOptions.IgnoreCase, 250)]
    private static partial Regex TimeoutWords();

    [GeneratedRegex(@"Exit code 124\b|stopped answering", RegexOptions.None, 250)]
    private static partial Regex TimedOutText();
}
