using System.Text.Json;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using Lucia.Homelab.Server.Assistant;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// Disposable check data stays within the project, never in an OS temporary directory.
var storage = Path.GetFullPath(Path.Combine("tests", "Lucia.Homelab.AssistantChecks", ".checks-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(storage);
var checks = 0;

void Check(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
    checks++;
}

async Task Reject(Func<Task> action, int status, string code, string reason)
{
    try { await action(); }
    catch (AssistantException e)
    {
        Check(e.StatusCode == status && e.Code == code, $"{reason} (got {e.StatusCode} {e.Code})");
        return;
    }
    throw new InvalidOperationException(reason + " (it was accepted)");
}

static JsonNode Node(object value) => JsonSerializer.SerializeToNode(value, AssistantStream.Json)!;

static string Types(IEnumerable<JsonNode> chunks) => string.Join(",", chunks.Select(chunk => (string)chunk["type"]!));

static void Feed(AssistantStream stream, List<JsonNode> into, object raw, AIContent? content = null, bool wrapped = false)
{
    content ??= new AIContent();
    content.RawRepresentation = raw;
    var update = new AgentResponseUpdate { Contents = [content] };
    into.AddRange(stream.Map(wrapped ? new AgentResponseUpdate { RawRepresentation = update } : update).Select(Node));
}

static AssistantMessageDeltaEvent TextDelta(string id, string delta) => new() { Data = new() { MessageId = id, DeltaContent = delta } };
static AssistantMessageEvent Text(string id, string content) => new() { Data = new() { MessageId = id, Content = content } };
static AssistantReasoningDeltaEvent ReasoningDelta(string id, string delta) => new() { Data = new() { ReasoningId = id, DeltaContent = delta } };
static AssistantReasoningEvent Reasoning(string id, string content) => new() { Data = new() { ReasoningId = id, Content = content } };
static ToolExecutionStartEvent ToolStart(string id, string name) => new() { Data = new() { ToolCallId = id, ToolName = name } };
static ToolExecutionCompleteEvent ToolEnd(string id, bool success) => new() { Data = new() { ToolCallId = id, Success = success } };
static AssistantUsageEvent Usage(string model, int input, int output) => new() { Data = new() { Model = model, InputTokens = input, OutputTokens = output } };
static SessionErrorEvent SessionError(string message) => new() { Data = new() { ErrorType = "synthetic", Message = message } };

try
{
    // A full turn: reasoning, streamed text, tools, a text-only final message and usage.
    var stream = new AssistantStream("a1", "default-model");
    var start = Node(stream.Start());
    Check((string)start["type"]! == "start" && (string)start["messageId"]! == "a1", "A turn starts with its message id.");
    var mapped = new List<JsonNode>();
    Feed(stream, mapped, ReasoningDelta("r-1", "Check"));
    Feed(stream, mapped, ReasoningDelta("r-1", "ing"));
    Feed(stream, mapped, Reasoning("r-1", "Checking"));
    Feed(stream, mapped, TextDelta("m-1", "Hel"));
    Feed(stream, mapped, TextDelta("m-1", "lo"), wrapped: true);
    Feed(stream, mapped, TextDelta("m-1", ""));
    Feed(stream, mapped, Text("m-1", "Hello"));
    Feed(stream, mapped, ToolStart("c1", "read_logs"), new FunctionCallContent("c1", "read_logs", new Dictionary<string, object?> { ["app"] = "grafana" }));
    Feed(stream, mapped, ToolEnd("c1", true), new FunctionResultContent("c1", "ok"));
    Feed(stream, mapped, ToolStart("c2", "restart_app"), new FunctionCallContent("c2", "restart_app"));
    Feed(stream, mapped, ToolEnd("c2", false), new FunctionResultContent("c2", "Denied by policy."));
    Feed(stream, mapped, ToolEnd("c9", true), new FunctionResultContent("c9", "stray"));
    Feed(stream, mapped, Text("m-2", "Done."));
    Feed(stream, mapped, Usage("gpt-x", 10, 5));
    Feed(stream, mapped, Usage("gpt-x", 3, 2));
    mapped.AddRange(stream.Finish().Select(Node));
    Check(Types(mapped) == "reasoning-start,reasoning-delta,reasoning-delta,reasoning-end,text-start,text-delta,text-delta,text-end,"
        + "tool-input-available,tool-output-available,tool-input-available,tool-output-error,text-start,text-delta,text-end,finish",
        "Events map to UI chunks in order, skipping empty deltas, already-streamed messages and unknown tool results.");
    Check((string)mapped[0]["id"]! == "r0" && (string)mapped[3]["id"]! == "r0" && (string)mapped[4]["id"]! == "t1"
        && (string)mapped[7]["id"]! == "t1" && (string)mapped[12]["id"]! == "t2" && (string)mapped[14]["id"]! == "t2",
        "Each text or reasoning part gets its own id from start to end.");
    Check((string)mapped[6]["delta"]! == "lo", "Updates wrapped by the telemetry agent are unwrapped.");
    Check((string)mapped[8]["toolCallId"]! == "c1" && (string)mapped[8]["toolName"]! == "read_logs"
        && (string)mapped[8]["input"]!["app"]! == "grafana" && (bool)mapped[8]["dynamic"]!
        && mapped[10]["input"] is JsonObject { Count: 0 }, "Tool calls stream their name and input, empty input as {}.");
    Check((string)mapped[9]["output"]! == "ok" && (string)mapped[11]["errorText"]! == "Denied by policy.",
        "Tool results stream as output or as an error.");
    var finish = mapped[^1];
    Check((string)finish["finishReason"]! == "stop" && (string)finish["messageMetadata"]!["model"]! == "gpt-x"
        && (long)finish["messageMetadata"]!["usage"]!["inputTokens"]! == 13 && (long)finish["messageMetadata"]!["usage"]!["outputTokens"]! == 7,
        "The finish chunk reports the model and summed token usage.");
    var message = stream.Message;
    Check(message is { Id: "a1", Role: "assistant" } && !stream.Interrupted
        && string.Join("|", message.Parts.Select(part => $"{part.Type}:{part.State}:{part.Text ?? part.ToolCallId}"))
            == "reasoning:done:Checking|text:done:Hello|dynamic-tool:output-available:c1|dynamic-tool:output-error:c2|text:done:Done.",
        "The saved message keeps every part in order.");
    Check((string?)message.Parts[2].Input?["app"] == "grafana" && message.Parts[2].Output?.GetValue<string>() == "ok"
        && message.Parts[3].ErrorText == "Denied by policy." && (string?)message.Metadata?["model"] == "gpt-x",
        "The saved message keeps tool input, output, errors and the model.");

    // Failures prefer the runtime's own session error and keep the partial answer.
    var failing = new AssistantStream("a2", null);
    var failed = new List<JsonNode>();
    Feed(failing, failed, TextDelta("m-1", "Partial"));
    Feed(failing, failed, SessionError(new string('e', 400)));
    failed.AddRange(failing.Fail("The HTTP request failed.").Select(Node));
    var capped = new string('e', 300) + "…";
    Check(Types(failed) == "text-start,text-delta,text-end,message-metadata,error", "A failure closes the open part, then sends metadata and the error.");
    Check((string)failed[^1]["errorText"]! == capped && (string)failed[^2]["messageMetadata"]!["error"]! == capped,
        "The session error wins over the exception and is capped at 300 characters.");
    Check(failing.Interrupted && failing.Message.Parts is [{ Type: "text", Text: "Partial" }] && (string?)failing.Message.Metadata?["error"] == capped,
        "A failed turn is kept with its partial answer and error.");
    Check((string)Node(new AssistantStream("a3", null).Fail(null)[^1])["errorText"]! == "The assistant stopped unexpectedly."
        && (string)Node(new AssistantStream("a3", null).Fail("Connect GitHub.")[^1])["errorText"]! == "Connect GitHub.",
        "Without a session error, the exception's message or a generic one is used.");

    var stopping = new AssistantStream("a4", "gpt-x");
    var stopped = new List<JsonNode>();
    Feed(stopping, stopped, ReasoningDelta("r-1", "Looking"));
    stopped.AddRange(stopping.Abort().Select(Node));
    Check(Types(stopped) == "reasoning-start,reasoning-delta,reasoning-end,message-metadata,abort"
        && (bool)stopped[^2]["messageMetadata"]!["stopped"]! && stopping.Interrupted && (bool?)stopping.Message.Metadata?["stopped"] == true,
        "A stopped turn closes its part, is marked stopped and is kept.");
    var idle = new AssistantStream("a5", null);
    Check(idle.Message.Parts.Count == 0 && idle.Message.Metadata is null && !idle.Interrupted, "An untouched turn has no parts or metadata and is not kept.");

    // The replay buffer lets a reopened chat bar catch up and follow a turn.
    using (var appStopping = new CancellationTokenSource())
    {
        var run = new AssistantRun("a1", appStopping.Token);
        var (empty, emptyDone, changed) = run.Read(0);
        Check(empty.Count == 0 && !emptyDone && !changed.IsCompleted && run.MessageId == "a1", "A new turn has nothing to replay.");
        run.Publish(new { type = "start" });
        Check(changed.IsCompletedSuccessfully, "Publishing wakes readers.");
        run.Publish(new List<object> { new { type = "text-delta", delta = "Hi" }, new { type = "finish" } });
        var tail = run.Read(1);
        Check(tail.Chunks.SequenceEqual(["{\"type\":\"text-delta\",\"delta\":\"Hi\"}", "{\"type\":\"finish\"}"]) && !tail.Done,
            "Readers replay from any offset.");
        run.Complete();
        run.Publish(new { type = "late" });
        run.Complete();
        var replayed = run.Read(0);
        Check(tail.Changed.IsCompletedSuccessfully && run.Done && replayed.Done && replayed.Chunks.Count == 4 && replayed.Chunks[^1] == "[DONE]",
            "Completing appends [DONE] once, and nothing is added after it.");
        appStopping.Cancel();
        Check(run.Stop.IsCancellationRequested, "Application shutdown stops the turn.");
        run.Stop.Dispose();
    }

    // Runs and chat files. Shutdown is already underway, so each turn stops before the Copilot runtime starts:
    // turns run end to end without a CLI, a real token or network.
    var directory = Path.Combine(storage, "assistant");
    var connected = Options.Create(new AssistantOptions { Directory = directory, GitHubToken = "synthetic-not-a-token" });
    await using var runtime = new AssistantRuntime(connected, NullLoggerFactory.Instance);
    await using var runs = new AssistantRuns(runtime, connected, new StoppedLifetime(), NullLogger<AssistantRuns>.Instance);
    const string owner = "0123456789abcdef", stranger = "fedcba9876543210";
    var chat = Guid.NewGuid().ToString("N");
    AssistantChatRequest Ask(string text, string messageId = "m1", string mode = "execute", string? model = null, string? session = null) =>
        new(session ?? chat, messageId, text, mode, model, "/apps");

    foreach (var bad in new[] { "", "abc", Guid.NewGuid().ToString("D"), "0123456789ABCDEF0123456789ABCDEF", "..\\" + chat })
        await Reject(() => runs.StartAsync(owner, Ask("Hi", session: bad), default), 400, "invalid_session", $"Chat id '{bad}' is rejected.");
    await Reject(() => runs.StartAsync(owner, Ask("Hi", mode: "auto"), default), 400, "invalid_request", "Unknown modes are rejected.");
    await Reject(() => runs.StartAsync(owner, Ask("Hi", messageId: "bad id"), default), 400, "invalid_request", "Message ids are validated.");
    await Reject(() => runs.StartAsync(owner, Ask("Hi", messageId: null!), default), 400, "invalid_request", "A missing message id is rejected.");
    await Reject(() => runs.StartAsync(owner, Ask("Hi", model: "gpt 5; rm"), default), 400, "invalid_request", "Model ids are validated.");
    await Reject(() => runs.StartAsync(owner, Ask(" \n "), default), 400, "invalid_message", "Blank messages are rejected.");
    await Reject(() => runs.StartAsync(owner, Ask(new string('x', 32_769)), default), 400, "invalid_message", "Messages over 32,768 characters are rejected.");
    var offline = Options.Create(new AssistantOptions { Directory = directory, GitHubToken = " " });
    await using (var offlineRuntime = new AssistantRuntime(offline, NullLoggerFactory.Instance))
    await using (var offlineRuns = new AssistantRuns(offlineRuntime, offline, new StoppedLifetime(), NullLogger<AssistantRuns>.Instance))
        await Reject(() => offlineRuns.StartAsync(owner, Ask("Hi"), default), 503, "assistant_not_connected", "Without a GitHub token the assistant asks to connect.");
    Check(!Directory.Exists(Path.Combine(directory, "users")), "Rejected requests write nothing.");

    const string question = "  Why is grafana down?\nIt was fine yesterday.";
    var first = await runs.StartAsync(owner, Ask(question), default);
    await first.Completion;
    var replay = first.Read(0);
    Check(replay.Done && replay.Chunks.SequenceEqual([$"{{\"type\":\"start\",\"messageId\":\"{first.MessageId}\"}}",
            "{\"type\":\"message-metadata\",\"messageMetadata\":{\"stopped\":true}}", "{\"type\":\"abort\"}", "[DONE]"]),
        "A turn cut short by shutdown replays as stopped.");
    Check(runs.Find(owner, chat) is null, "A finished turn is released once shutdown ends its linger.");
    var folder = Path.Combine(directory, "users", owner, chat);
    Check(File.Exists(Path.Combine(folder, "transcript.json")) && File.Exists(Path.Combine(folder, "session.json")),
        "Each chat is saved as JSON in its owner's folder.");
    var view = Node(await runs.GetAsync(owner, chat, default));
    var messages = view["messages"]!.AsArray();
    Check((string)view["title"]! == "Why is grafana down?" && !(bool)view["running"]!, "The title is the first line of the first message.");
    Check(messages.Count == 2 && (string)messages[0]!["id"]! == "m1" && (string)messages[0]!["role"]! == "user"
        && (string)messages[0]!["parts"]![0]!["text"]! == question && (string)messages[1]!["id"]! == first.MessageId
        && messages[1]!["parts"]!.AsArray().Count == 0 && (bool)messages[1]!["metadata"]!["stopped"]!,
        "The question and the stopped answer are saved.");

    var retry = await runs.StartAsync(owner, Ask(question), default);
    await retry.Completion;
    messages = Node(await runs.GetAsync(owner, chat, default))["messages"]!.AsArray();
    Check(messages.Count == 2 && (string)messages[0]!["id"]! == "m1" && (string)messages[1]!["id"]! == retry.MessageId && retry.MessageId != first.MessageId,
        "Trying again keeps one copy of the question and replaces the old answer.");

    var next = await runs.StartAsync(owner, Ask("Restart it.", messageId: "m2", mode: "plan", model: "gpt-5.1"), default);
    await next.Completion;
    view = Node(await runs.GetAsync(owner, chat, default));
    messages = view["messages"]!.AsArray();
    Check(messages.Count == 4 && (string)messages[2]!["id"]! == "m2" && (string)messages[3]!["id"]! == next.MessageId
        && (string)messages[3]!["metadata"]!["model"]! == "gpt-5.1" && (string)view["title"]! == "Why is grafana down?",
        "A new message is appended and the chat keeps its title.");

    var longChat = Guid.NewGuid().ToString("N");
    await (await runs.StartAsync(owner, Ask("\r\n " + new string('a', 100) + "\r\nsecond line", session: longChat), default)).Completion;
    var shortChat = Guid.NewGuid().ToString("N");
    await (await runs.StartAsync(owner, Ask("Short title\r\nMore detail", session: shortChat), default)).Completion;
    var sessions = Node(runs.List(owner))["sessions"]!.AsArray();
    Check(sessions.Count == 3 && (string)sessions[0]!["id"]! == shortChat && (string)sessions[0]!["title"]! == "Short title"
        && (string)sessions[1]!["title"]! == new string('a', 79) + "…" && (string)sessions[2]!["id"]! == chat
        && (string)sessions[2]!["model"]! == "gpt-5.1" && sessions.All(session => !(bool)session!["running"]!),
        "Chats list newest first, with titles cut to 80 characters and the last model used.");

    Check(Node(runs.List(stranger))["sessions"]!.AsArray().Count == 0 && runs.Find(stranger, chat) is null, "Another owner sees no chats.");
    await Reject(() => runs.GetAsync(stranger, chat, default), 404, "session_not_found", "Another owner cannot open a chat.");
    await Reject(() => runs.GetAsync(owner, "..\\" + chat, default), 400, "invalid_session", "Chat ids cannot leave the owner's folder.");
    await Reject(() => runs.DeleteAsync(owner, "abc", default), 400, "invalid_session", "Deleting checks the chat id first.");

    var owned = Path.Combine(directory, "users", owner);
    var corrupt = Directory.CreateDirectory(Path.Combine(owned, Guid.NewGuid().ToString("N"))).FullName;
    File.WriteAllText(Path.Combine(corrupt, "session.json"), "{ not json");
    Directory.CreateDirectory(Path.Combine(owned, "notes"));
    File.Copy(Path.Combine(folder, "session.json"), Path.Combine(owned, "notes", "session.json"));
    Check(Node(runs.List(owner))["sessions"]!.AsArray().Count == 3, "Unreadable chats and stray folders are left out of the list.");

    var huge = Guid.NewGuid().ToString("N");
    Directory.CreateDirectory(Path.Combine(owned, huge));
    using (var file = File.Create(Path.Combine(owned, huge, "transcript.json"))) file.SetLength(16 * 1024 * 1024 + 1);
    await Reject(() => runs.GetAsync(owner, huge, default), 409, "session_too_large", "Oversized chats are refused, not loaded.");
    await Reject(() => runs.StartAsync(owner, Ask("Hi", session: huge), default), 409, "session_too_large", "Oversized chats cannot take new messages.");

    Console.WriteLine($"Assistant checks passed ({checks} assertions). No Copilot runtime, GitHub token or network used.");
}
finally
{
    if (Directory.Exists(storage)) Directory.Delete(storage, recursive: true);
}

/// <summary>Shutdown already underway: every turn stops at its first await, before the Copilot runtime is started.</summary>
sealed class StoppedLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping { get; } = new(canceled: true);
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
}
