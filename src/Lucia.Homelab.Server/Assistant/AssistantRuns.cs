using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Assistant;

public sealed record AssistantChatRequest(string SessionId, string MessageId, string Text, string Mode, string? Model, string? Route);
public sealed record AssistantSessionInfo(string Title, DateTimeOffset Updated, string? Model);
public sealed record AssistantTranscript(List<UiMessage> Messages);

/// <summary>Runs chat turns in the background so they survive disconnects, and keeps each owner's chats as JSON files.</summary>
public sealed partial class AssistantRuns(AssistantRuntime runtime, IOptions<AssistantOptions> options,
    IHostApplicationLifetime lifetime, ILogger<AssistantRuns> logger) : IAsyncDisposable
{
    private const long TranscriptLimit = 16 * 1024 * 1024;
    private readonly ConcurrentDictionary<string, AssistantRun> _runs = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string Root => Path.GetFullPath(options.Value.Directory);

    public AssistantRun? Find(string owner, string id) => _runs.GetValueOrDefault(Key(owner, SessionId(id)));

    public void Stop(string owner, string id)
    {
        if (Find(owner, id) is { Done: false } run) run.Stop.Cancel();
    }

    public async Task<AssistantRun> StartAsync(string owner, AssistantChatRequest request, CancellationToken ct)
    {
        var id = SessionId(request.SessionId);
        if (request.MessageId is null || !MessageIdPattern().IsMatch(request.MessageId) || request.Mode is not ("plan" or "execute")
            || request.Model is { } model && !ModelPattern().IsMatch(model))
            throw new AssistantException(400, "invalid_request", "The request body is invalid.");
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 32_768)
            throw new AssistantException(400, "invalid_message", "Messages must be between 1 and 32,768 characters.");
        if (!await runtime.ConnectedAsync(owner, ct)) throw GitHubSignIn.NotConnected();
        var key = Key(owner, id);
        var folder = Path.Combine(Root, "users", owner, id);
        await _gate.WaitAsync(ct);
        try
        {
            if (_runs.TryGetValue(key, out var active) && !active.Done)
                throw new AssistantException(409, "run_active", "This chat is still answering. Stop it or wait for it to finish.");
            var transcript = await LoadAsync(folder, ct) ?? new([]);
            var info = (ReadInfo(folder) ?? new(Title(request.Text), default, null)) with
            {
                Updated = DateTimeOffset.UtcNow, Model = request.Model
            };
            // "Try again" resends the last message: keep one copy of it and drop the failed or stopped answer it replaces.
            var retried = transcript.Messages.FindLastIndex(message => message.Role == "user");
            if (retried >= 0 && transcript.Messages[retried].Id == request.MessageId)
                transcript.Messages.RemoveRange(retried + 1, transcript.Messages.Count - retried - 1);
            else
                transcript.Messages.Add(new(request.MessageId, "user", [new UiPart("text", request.Text)]));
            await SaveAsync(folder, transcript, info);
            var run = new AssistantRun(Guid.NewGuid().ToString("N"), lifetime.ApplicationStopping);
            _runs[key] = run;
            var route = request.Route is { } page && RoutePattern().IsMatch(page) ? page : "unknown";
            var prompt = $"<lucia-context>\nMode: {(request.Mode == "plan" ? "Plan" : "Execute")}\nPage: {route}\n</lucia-context>\n\n{request.Text}";
            run.Completion = Task.Run(() => ExecuteAsync(key, folder, run, new(owner, key, prompt, request.Model), transcript, info));
            return run;
        }
        finally { _gate.Release(); }
    }

    public object List(string owner)
    {
        var root = Path.Combine(Root, "users", owner);
        var sessions = Directory.Exists(root)
            ? Directory.EnumerateDirectories(root)
                .Select(folder => (Id: Path.GetFileName(folder), Info: ReadInfo(folder)))
                .Where(session => session.Info is not null && IsSessionId(session.Id))
                .OrderByDescending(session => session.Info!.Updated).Take(200)
                .Select(session => new
                {
                    id = session.Id, title = session.Info!.Title, updated = session.Info.Updated, model = session.Info.Model,
                    running = _runs.GetValueOrDefault(Key(owner, session.Id)) is { Done: false }
                }).ToArray()
            : [];
        return new { sessions };
    }

    public async Task<object> GetAsync(string owner, string id, CancellationToken ct)
    {
        var run = Find(owner, id);
        var folder = Path.Combine(Root, "users", owner, id);
        var transcript = await LoadAsync(folder, ct)
            ?? throw new AssistantException(404, "session_not_found", "This chat no longer exists.");
        // A turn that finishes while this loads is replayed by reattaching, so leave its answer out.
        var messages = run is { Done: false } ? transcript.Messages.Where(message => message.Id != run.MessageId) : transcript.Messages;
        return new { id, title = ReadInfo(folder)?.Title ?? "Chat", messages, running = run is { Done: false } };
    }

    public async Task DeleteAsync(string owner, string id, CancellationToken ct)
    {
        var key = Key(owner, SessionId(id));
        await _gate.WaitAsync(ct);
        try
        {
            if (_runs.TryGetValue(key, out var run) && !run.Done)
                throw new AssistantException(409, "run_active", "Stop this chat before deleting it.");
            _runs.TryRemove(key, out _);
            var folder = Path.Combine(Root, "users", owner, id);
            if (Directory.Exists(folder))
            {
                DomainOnboardingStore.RejectLinks(folder);
                Directory.Delete(folder, recursive: true);
            }
        }
        finally { _gate.Release(); }
        await runtime.DeleteAsync(key, ct);
    }

    private async Task ExecuteAsync(string key, string folder, AssistantRun run, AssistantTurn turn,
        AssistantTranscript transcript, AssistantSessionInfo info)
    {
        var stream = new AssistantStream(run.MessageId, turn.Model ?? runtime.DefaultModel);
        run.Publish(stream.Start());
        try
        {
            await foreach (var update in runtime.RunAsync(turn, run.Stop.Token))
                run.Publish(stream.Map(update));
            run.Publish(stream.Finish());
        }
        catch (OperationCanceledException) when (run.Stop.IsCancellationRequested)
        {
            run.Publish(stream.Abort());
            if (!lifetime.ApplicationStopping.IsCancellationRequested) await runtime.AbortAsync(turn.Owner, key, turn.Model);
        }
        catch (Exception e)
        {
            logger.LogWarning("An assistant turn failed ({ErrorType}).", e.GetType().Name);
            logger.LogDebug(e, "Assistant turn failure.");
            var explained = e as AssistantException ?? await runtime.ExplainAsync(turn.Owner, e);
            run.Publish(stream.Fail(explained?.Message));
        }
        try
        {
            var message = stream.Message;
            if (message.Parts.Count > 0 || stream.Interrupted) transcript.Messages.Add(message);
            await SaveAsync(folder, transcript, info with { Updated = DateTimeOffset.UtcNow });
        }
        catch (Exception e) { logger.LogError("Saving an assistant chat failed ({ErrorType}).", e.GetType().Name); }
        run.Complete();
        // Keep the finished turn briefly so a chat bar that reconnects mid-turn can still replay its end.
        try { await Task.Delay(TimeSpan.FromSeconds(30), lifetime.ApplicationStopping); }
        catch (OperationCanceledException) { }
        _runs.TryRemove(new KeyValuePair<string, AssistantRun>(key, run));
        run.Stop.Dispose();
    }

    private static async Task<AssistantTranscript?> LoadAsync(string folder, CancellationToken ct)
    {
        var path = Path.Combine(folder, "transcript.json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > TranscriptLimit)
            throw new AssistantException(409, "session_too_large", "This chat is too long to open. Start a new chat.");
        await using var file = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AssistantTranscript>(file, AssistantStream.Json, ct);
    }

    private AssistantSessionInfo? ReadInfo(string folder)
    {
        var path = Path.Combine(folder, "session.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AssistantSessionInfo>(File.ReadAllBytes(path), AssistantStream.Json) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("An assistant chat could not be read ({ErrorType}).", e.GetType().Name);
            return null;
        }
    }

    private static async Task SaveAsync(string folder, AssistantTranscript transcript, AssistantSessionInfo info)
    {
        await DomainOnboardingStore.WriteJson(Path.Combine(folder, "transcript.json"), transcript, json: AssistantStream.Json);
        await DomainOnboardingStore.WriteJson(Path.Combine(folder, "session.json"), info, json: AssistantStream.Json);
    }

    private static string Key(string owner, string id) => owner + "-" + id;

    private static bool IsSessionId(string? id) => Guid.TryParseExact(id, "N", out var guid) && guid.ToString("N") == id;

    private static string SessionId(string? id) =>
        IsSessionId(id) ? id! : throw new AssistantException(400, "invalid_session", "The chat id is invalid.");

    private static string Title(string text)
    {
        var line = text.AsSpan().Trim();
        if (line.IndexOfAny('\r', '\n') is var end and >= 0) line = line[..end];
        return line.Length > 80 ? string.Concat(line[..79].TrimEnd(), "…") : line.ToString();
    }

    /// <summary>ApplicationStopping has already cancelled every turn; give them a moment to save before the runtime goes.</summary>
    public async ValueTask DisposeAsync()
    {
        try { await Task.WhenAll(_runs.Values.Select(run => run.Completion)).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception e) { logger.LogWarning("Assistant turns did not stop cleanly ({ErrorType}).", e.GetType().Name); }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex MessageIdPattern();

    [GeneratedRegex("^[A-Za-z0-9._:/-]{1,100}$")]
    private static partial Regex ModelPattern();

    [GeneratedRegex("^/[A-Za-z0-9/_.-]{0,200}$")]
    private static partial Regex RoutePattern();
}
