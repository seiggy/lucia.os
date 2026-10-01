using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Web;
using GitHub.Copilot;
using Lucia.Homelab.Server.Assistant;
using Lucia.Homelab.Server.Stacks;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.DataProtection;
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

    // GitHub sign-in against a scripted GitHub: synthetic tokens and no network. Checks for the code fire at once; the 10-minute linger never does.
    var directory = Path.Combine(storage, "assistant");
    var options = Options.Create(new AssistantOptions { Directory = directory });
    var github = new FakeGitHub();
    var clock = new InstantTime();
    using var signIn = new GitHubSignIn(options, new EphemeralDataProtectionProvider(), NullLogger<GitHubSignIn>.Instance, new HttpClient(github), clock);
    async Task<GitHubStatus> Settle(string who, string state)
    {
        for (var i = 0; i < 500 && signIn.Status(who).State != state; i++) await Task.Delay(10);
        var status = signIn.Status(who);
        Check(status.State == state, $"The sign-in reaches {state} (it is {status.State}).");
        return status;
    }
    static object Issued(string access, string refresh, int expires = 28_800) => new
    {
        access_token = access, token_type = "bearer", scope = "", expires_in = expires, refresh_token = refresh, refresh_token_expires_in = 15_897_600
    };

    // Bring-your-own models against scripted LiteLLM and Local AI apps: no network.
    var openAi = new FakeOpenAi();
    IReadOnlyList<AppEndpoint> apps =
    [
        new("litellm", "llm", "lucialab01", new Uri("http://10.0.0.5:4000/"), "sk-master", null),
        new("local-ai", "ai", "lucialab02", new Uri("http://10.0.0.6:8000/"), "local-key", null),
        new("local-ai", "ai-old", "lucialab03", new Uri("http://10.0.0.7:8000/"), "local-key", null),
        new("local-ai", "ai-off", "lucialab04", null, null, "Local AI on lucialab04 is stopped. Start it in Apps.")
    ];
    var spark = new SparkInference(new Uri("http://127.0.0.1:8080/v1"), "inference-key", "qwen3-spark", 32_768, 2_048, null);
    var providers = new AssistantProviders(options, new EphemeralDataProtectionProvider(), NullLogger<AssistantProviders>.Instance,
        new HttpClient(openAi), _ => Task.FromResult(apps), () => spark);

    // Runs and chat files. Shutdown is already underway, so each turn stops before the Copilot runtime starts:
    // turns run end to end without a CLI, a real token or network.
    await using var runtime = new AssistantRuntime(options, signIn, providers, NullLoggerFactory.Instance);
    await using var runs = new AssistantRuns(runtime, options, new StoppedLifetime(), NullLogger<AssistantRuns>.Instance);
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
    await Reject(() => runs.StartAsync(owner, Ask("Hi"), default), 503, "assistant_not_connected", "Until the owner signs in with GitHub, the assistant asks them to.");
    Check(!Directory.Exists(Path.Combine(directory, "users")), "Rejected requests write nothing.");
    var listed = Node(await runtime.ModelsAsync(owner, default));
    var sources = listed["sources"]!.AsArray();
    Check(signIn.Status(owner) == new GitHubStatus("disconnected") && !(bool)listed["connected"]! && (string)sources[0]!["id"]! == "github"
        && sources[0]!["models"]!.AsArray().Count == 0 && (string)sources[0]!["reason"]! == "Sign in with GitHub to use Copilot's models.",
        "Before signing in, the owner is disconnected and Copilot's models ask them to sign in.");
    Check(string.Join(",", listed["models"]!.AsArray().Select(item => (string)item!["id"]!)) == "litellm:gpt-4o,local:qwen3-spark,local:ai/qwen3"
        && (string)listed["models"]![2]!["name"]! == "qwen3 · lucialab02",
        "LiteLLM's and Local AI's chat models list without GitHub; embedding models and unsafe ids are left out.");
    Check((string)sources[2]!["reason"]! == "Local AI on lucialab03 isn't answering. Local AI on lucialab04 is stopped. Start it in Apps.",
        "Local AI apps that can't serve say why, and the others still list.");
    var keyFile = Path.Combine(directory, "litellm", owner + ".json");
    Check(openAi.Minted == 1 && File.Exists(keyFile) && !File.ReadAllText(keyFile).Contains("sk-virtual")
        && openAi.Calls.Any(call => call.Contains("/key/generate") && call.Contains($"\"key_alias\":\"lucia-assistant-{owner}\"")),
        "LiteLLM gets the owner a virtual key, named for them and saved encrypted.");
    await runtime.ModelsAsync(owner, default);
    Check(openAi.Minted == 1, "The saved key is reused.");
    openAi.Revoked.Add("sk-virtual-1");
    await runtime.ModelsAsync(owner, default);
    Check(openAi.Minted == 2 && openAi.Calls.Count(call => call.Contains("/key/delete")) == 2, "A key LiteLLM refuses is replaced, deleting the old one first.");

    var (provider, served) = await providers.ResolveAsync(owner, "litellm:gpt-4o", default);
    Check(provider is { Type: "openai", WireApi: "completions", BaseUrl: "http://10.0.0.5:4000/v1", ApiKey: "sk-virtual-2", MaxOutputTokens: null }
        && served == "gpt-4o", "A LiteLLM model runs on LiteLLM's OpenAI API with the owner's key.");
    (provider, served) = await providers.ResolveAsync(owner, "local:qwen3-spark", default);
    Check(provider is { BaseUrl: "http://127.0.0.1:8080/v1", ApiKey: "inference-key", MaxOutputTokens: 2_048, MaxPromptTokens: 30_720 }
        && served == "qwen3-spark", "The Spark's model runs over loopback with the inference key, within the Spark's limits.");
    (provider, served) = await providers.ResolveAsync(owner, "local:ai/qwen3", default);
    Check(provider is { BaseUrl: "http://10.0.0.6:8000/v1", ApiKey: "local-key", MaxOutputTokens: 8_192, MaxPromptTokens: 24_576 }
        && served == "qwen3", "A node's model runs on its Local AI app, leaving a quarter of the context for the answer.");
    Check(await providers.ResolveAsync(owner, "gpt-5.1", default) == (null, "gpt-5.1") && AssistantProviders.IsGitHub(null)
        && AssistantProviders.IsGitHub("other:model") && !AssistantProviders.IsGitHub("litellm:openai/gpt-4o") && !AssistantProviders.IsGitHub("local:ai/qwen3"),
        "Other model ids are Copilot's.");
    await Reject(() => providers.ResolveAsync(owner, "litellm:gone", default), 503, "assistant_source_unavailable", "A model LiteLLM dropped asks for another.");
    await Reject(() => providers.ResolveAsync(owner, "local:llama", default), 503, "assistant_source_unavailable", "A model the Spark unloaded asks for another.");
    await Reject(() => providers.ResolveAsync(owner, "local:removed/qwen3", default), 503, "assistant_source_unavailable", "A removed Local AI app asks for another model.");
    await Reject(() => providers.ResolveAsync(owner, "local:ai-off/qwen3", default), 503, "assistant_source_unavailable", "A stopped Local AI app says so.");

    spark = spark with { Model = null, Problem = "No chat model is loaded on the Spark. Load one on the AI page." };
    apps = [apps[1]];
    sources = Node(await runtime.ModelsAsync(owner, default))["sources"]!.AsArray();
    Check((string)sources[1]!["reason"]! == "Install the LiteLLM app to use its models." && sources[2]!["models"]!.AsArray().Count == 1
        && (string)sources[2]!["reason"]! == "No chat model is loaded on the Spark. Load one on the AI page.",
        "Without LiteLLM or a Spark model, the picker says what to do and lists what's left.");
    await Reject(() => providers.ResolveAsync(owner, "local:qwen3-spark", default), 503, "assistant_source_unavailable", "An unloaded Spark model says why.");
    var local = await runs.StartAsync("00000000000000aa", Ask("Hi", model: "local:ai/qwen3", session: Guid.NewGuid().ToString("N")), default);
    await local.Completion;
    Check(local.Done, "Local models answer without a GitHub sign-in.");

    var pending = await signIn.StartAsync(owner, default);
    Check(pending is { State: "pending", UserCode: "CODE-0001", VerificationUri: "https://github.com/login/device", Interval: 5 }
        && signIn.Status(owner) == pending, "Signing in shows a code to enter on GitHub.");
    Check(await signIn.StartAsync(owner, default) == pending && github.Codes == 1, "Starting again, from another tab or a double click, keeps the same code.");
    github.Reply("CODE-0001", new { error = "authorization_pending" }, new { error = "slow_down", interval = 10 }, Issued("ghu_synthetic", "ghr_synthetic"));
    Check((await Settle(owner, "connected")).Login == "octocat", "Once the code is entered on GitHub, the owner is signed in as their login.");
    Check(clock.Waits.Contains(TimeSpan.FromSeconds(10)), "When GitHub asks Lucia to slow down, it waits longer between checks.");
    var saved = Path.Combine(directory, "github", owner + ".json");
    var envelope = File.ReadAllText(saved);
    Check(!envelope.Contains("synthetic"), "Tokens are saved encrypted, never in plain text.");
    Check(await signIn.TokenAsync(owner, default) == "ghu_synthetic" && github.Refreshes == 0, "A token with hours left is used as is.");

    var copied = Path.Combine(directory, "github", stranger + ".json");
    File.Copy(saved, copied);
    Check(signIn.Status(stranger).State == "disconnected" && await signIn.TokenAsync(stranger, default) is null,
        "One owner's saved sign-in does not decrypt for another owner.");
    await signIn.DisconnectAsync(stranger);
    Check(!File.Exists(copied) && File.Exists(saved), "Disconnecting deletes only that owner's saved sign-in.");

    github.Reply((await signIn.StartAsync(stranger, default)).UserCode!, new { error = "access_denied" });
    Check((await Settle(stranger, "denied")).Message == "Access was declined on GitHub.", "Declining on GitHub ends the sign-in and says so.");
    github.Reply((await signIn.StartAsync(stranger, default)).UserCode!, new { error = "expired_token" });
    await Settle(stranger, "expired");
    var abandoned = await signIn.StartAsync(stranger, default);
    await signIn.CancelAsync(stranger);
    Check(signIn.Status(stranger).State == "disconnected" && (await signIn.StartAsync(stranger, default)).UserCode != abandoned.UserCode,
        "Cancelling forgets the code, and signing in again gets a new one.");
    await signIn.CancelAsync(stranger);

    github.Reply((await signIn.StartAsync(stranger, default)).UserCode!, Issued("ghu_old", "ghr_old", expires: 1_800));
    await Settle(stranger, "connected");
    github.Reply("refresh:ghr_old", Issued("ghu_new", "ghr_new"));
    Check(await signIn.TokenAsync(stranger, default) == "ghu_new" && await signIn.TokenAsync(stranger, default) == "ghu_new"
        && github.Refreshes == 1 && signIn.Status(stranger).Login == "octocat", "A token with under an hour left is renewed once and keeps the login.");
    github.Reply((await signIn.StartAsync(stranger, default)).UserCode!, Issued("ghu_old", "ghr_spent", expires: 1_800));
    await Settle(stranger, "connected");
    github.Reply("refresh:ghr_spent", new { error = "bad_refresh_token" });
    Check(await signIn.TokenAsync(stranger, default) is null && signIn.Status(stranger).State == "disconnected" && !File.Exists(copied),
        "When GitHub will not renew a sign-in, the owner is signed out.");

    var refused = new IOException("Communication error.", new InvalidOperationException("Failed to fetch Copilot user info: 401 Unauthorized: {\"message\":\"Bad credentials\"}"));
    Check(await signIn.ExplainAsync(owner, new IOException("Pipe closed.")) is null, "Other runtime failures are not blamed on the sign-in.");
    Check(await signIn.ExplainAsync(stranger, refused) is { Code: "assistant_not_connected" }, "Copilot refusing a signed-out owner asks them to sign in.");
    Check(await signIn.ExplainAsync(owner, refused) is { StatusCode: 503, Code: "copilot_unavailable" } unavailable && unavailable.Message.Contains("@octocat")
        && File.Exists(saved), "Copilot refusing a working GitHub sign-in names the account to check.");

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

    github.UserStatus = 401;
    Check(await signIn.ExplainAsync(owner, refused) is { Code: "github_signed_out" } && signIn.Status(owner).State == "disconnected" && !File.Exists(saved),
        "When GitHub no longer accepts the sign-in, the owner is signed out.");

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

/// <summary>Timers up to a minute fire at once, so checks for a code run instantly; longer ones (the linger) never fire.</summary>
sealed class InstantTime : TimeProvider
{
    public ConcurrentQueue<TimeSpan> Waits { get; } = new();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Waits.Enqueue(dueTime);
        return base.CreateTimer(callback, state, dueTime <= TimeSpan.FromMinutes(1) ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, period);
    }
}

/// <summary>GitHub's device-flow endpoints, scripted per code. A check with no reply queued waits for one, so a code stays pending without spinning.</summary>
sealed class FakeGitHub : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Channel<object>> _replies = new();
    private int _codes, _refreshes;

    public int Codes => _codes;
    public int Refreshes => _refreshes;
    public int UserStatus { get; set; } = 200;

    /// <summary>Queues token replies for a user code, or for "refresh:" and a refresh token.</summary>
    public void Reply(string key, params object[] bodies)
    {
        foreach (var body in bodies) Queue(key).Writer.TryWrite(body);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        switch (request.RequestUri!.AbsoluteUri)
        {
            case "https://github.com/login/device/code":
                var code = $"CODE-{Interlocked.Increment(ref _codes):D4}";
                return Json(new { device_code = "device:" + code, user_code = code, verification_uri = "https://github.com/login/device", expires_in = 899, interval = 5 });
            case "https://github.com/login/oauth/access_token":
                var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
                var refresh = form["grant_type"] == "refresh_token";
                if (refresh) Interlocked.Increment(ref _refreshes);
                return Json(await Queue(refresh ? "refresh:" + form["refresh_token"] : form["device_code"]!["device:".Length..]).Reader.ReadAsync(ct));
            case "https://api.github.com/user" when request.Headers.Authorization?.Scheme == "Bearer":
                return new HttpResponseMessage((HttpStatusCode)UserStatus) { Content = JsonContent.Create(new { login = "octocat" }) };
            default:
                throw new InvalidOperationException("Unexpected request to " + request.RequestUri);
        }
    }

    private Channel<object> Queue(string key) => _replies.GetOrAdd(key, _ => Channel.CreateUnbounded<object>());

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
}

/// <summary>LiteLLM (10.0.0.5) and Local AI (10.0.0.6, unreachable 10.0.0.7) OpenAI APIs. LiteLLM takes only keys it minted and hasn't revoked.</summary>
sealed class FakeOpenAi : HttpMessageHandler
{
    public ConcurrentQueue<string> Calls { get; } = new();
    public HashSet<string> Revoked { get; } = [];
    public int Minted { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var key = request.Headers.Authorization?.Parameter;
        Calls.Enqueue($"{request.Method} {request.RequestUri} {(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct))}");
        return (request.Method.Method, request.RequestUri!.AbsoluteUri) switch
        {
            ("POST", "http://10.0.0.5:4000/key/delete") when key == "sk-master" => Minted == 0 ? new(HttpStatusCode.NotFound) : Json(new { deleted_keys = new[] { "old" } }),
            ("POST", "http://10.0.0.5:4000/key/generate") when key == "sk-master" => Json(new { key = $"sk-virtual-{++Minted}" }),
            ("GET", "http://10.0.0.5:4000/v1/models") when key is not null && key.StartsWith("sk-virtual-") && !Revoked.Contains(key) =>
                Json(new { data = new object[] { new { id = "gpt-4o" }, new { id = "bad id" } } }),
            ("GET", "http://10.0.0.5:4000/v1/models") => new(HttpStatusCode.Unauthorized),
            ("GET", "http://10.0.0.6:8000/v1/models") when key == "local-key" => Json(new
            {
                data = new object[] { new { id = "qwen3", max_model_len = 32_768 }, new { id = "embed", capabilities = new[] { "embedding" } } }
            }),
            ("GET", "http://10.0.0.7:8000/v1/models") => throw new HttpRequestException("No route to host."),
            _ => throw new InvalidOperationException("Unexpected request to " + request.RequestUri)
        };
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
}
