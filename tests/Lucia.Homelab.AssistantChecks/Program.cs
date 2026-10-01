// The SDK marks permission decisions as evaluation-only; the broker checks inspect them.
#pragma warning disable GHCP001
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Web;
using GitHub.Copilot;
using Lucia.Homelab.Server.Assistant;
using Lucia.Homelab.Server.Stacks;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ApproveOnce = GitHub.Copilot.Rpc.PermissionDecisionApproveOnce;
using PermissionDecision = GitHub.Copilot.Rpc.PermissionDecision;
using Rejected = GitHub.Copilot.Rpc.PermissionDecisionReject;

// Disposable check data stays within the project, never in an OS temporary directory.
var storage = Path.GetFullPath(Path.Combine("tests", "Lucia.Homelab.AssistantChecks", ".checks-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(storage);
var checks = 0;

void Check(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
    checks++;
}

async Task<AssistantException> Reject(Func<Task> action, int status, string code, string reason)
{
    try { await action(); }
    catch (AssistantException e)
    {
        Check(e.StatusCode == status && e.Code == code, $"{reason} (got {e.StatusCode} {e.Code})");
        return e;
    }
    throw new InvalidOperationException(reason + " (it was accepted)");
}

static Func<Task> Throws(Action action) => () =>
{
    action();
    return Task.CompletedTask;
};

static string Verdict(PermissionDecision decision) => decision switch
{
    ApproveOnce => "approve",
    Rejected rejected => "reject: " + rejected.Feedback,
    _ => decision.GetType().Name,
};

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

    // Redaction: secret-looking text, every app's secret values, and tool arguments as the owner sees them.
    var redJwt = string.Join('.', "eyJhbGciOiJIUzI1NiJ9", "eyJzdWIiOiJsdWNpYSJ9", "c2lnbmF0dXJlLXZhbHVl");
    foreach (var (redIn, redOut) in new (string In, string? Out)[]
    {
        ("DB_PASSWORD=hunter2hunter2", "DB_PASSWORD=[redacted]"),
        ("""{"password": "correct horse battery"}""", """{"password": "[redacted]"}"""),
        ("Authorization: Basic dXNlcjpwYXNz", "Authorization: [redacted]"),
        ("postgres://lucia:pa55word@db:5432/app", "postgres://lucia:[redacted]@db:5432/app"),
        ("jwt " + redJwt, "jwt [redacted]"),
        // References, flags, numbers, paths, URLs and structure are left alone.
        ("DB_PASSWORD=${DB_PASSWORD}", null),
        ("require_password: false", null),
        ("token_ttl: 3600", null),
        ("secret_file: /run/secrets/db", null),
        ("api_key_url = https://example.com/key", null),
        ("""ports: ["8080:80"]""", null),
    })
    {
        var redGot = AssistantTools.Patterns(redIn);
        Check(redGot == (redOut ?? redIn), $"'{redIn}' becomes '{redOut ?? redIn}' (got '{redGot}').");
    }
    var redGitHub = "gh" + "p_" + "A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8";
    Check(!AssistantTools.Patterns($"found {redGitHub} in the log").Contains(redGitHub), "AGT's credential redactor runs too, catching provider tokens.");

    var redGuid = Guid.NewGuid().ToString("N");
    var redToken = string.Concat(Enumerable.Repeat("aB3_", 10)) + "zZ";
    Dictionary<string, string> redEnv = new()
    {
        ["DB_PASSWORD"] = "\"s3cret-Value!\"", ["APP_SECRET"] = @"back\slash-Secret9", ["SHORT_SECRET"] = "abc123",
        ["PORT"] = "8080", ["INSTANCE_ID"] = redGuid, ["UPSTREAM"] = redToken,
    };
    Check(AssistantTools.Scrub($$"""login s3cret-Value! raw back\slash-Secret9 json "{{JsonEncodedText.Encode(@"back\slash-Secret9")}}" {{redToken}}""", redEnv)
        == """login [redacted] raw [redacted] json "[redacted]" [redacted]""", "Apps' secret values are hidden wherever they appear, raw or JSON-escaped.");
    Check(AssistantTools.Scrub($"abc123 on 8080 as {redGuid}", redEnv) == $"abc123 on 8080 as {redGuid}", "Short values, settings and ids stay readable.");

    var redArgs = JsonNode.Parse("""
        {"name":"db","env":{"DB_PASSWORD":"hunter2hunter2","PORT":"5432"},"secrets":["a","b"],"token":"","apiKey":{"value":"xyz","n":3,"on":true},
         "compose":"environment:\n  POSTGRES_PASSWORD: s3cretpass99\n"}
        """);
    Check(JsonNode.DeepEquals(AssistantTools.Mask(redArgs), JsonNode.Parse("""
        {"name":"db","env":{"DB_PASSWORD":"••••••","PORT":"5432"},"secrets":["••••••","••••••"],"token":"","apiKey":{"value":"••••••","n":3,"on":true},
         "compose":"environment:\n  POSTGRES_PASSWORD: [redacted]\n"}
        """)) && (string)redArgs!["env"]!["DB_PASSWORD"]! == "hunter2hunter2" && AssistantTools.Mask(null) is null,
        "The owner sees a copy of the tool's arguments with secret fields masked and secret-looking text redacted.");

    // Environment edits for custom apps: comments and order survive, and secrets are generated, never written by the model.
    var (toolEnv, toolGenerated) = AssistantTools.EditEnv("# Database\r\nDB_HOST=db\nDB_PASSWORD=old\nDB_HOST=dup\n\nPORT=80\n",
        new Dictionary<string, string> { ["DB_HOST"] = "postgres", ["NEW_KEY"] = "a b" }, ["PORT"], ["DB_PASSWORD", "JWT_SECRET"]);
    var toolMatch = Regex.Match(toolEnv, @"\A# Database\nDB_HOST=postgres\nDB_PASSWORD=([A-Za-z0-9_-]{43})\n\nNEW_KEY=a b\nJWT_SECRET=([A-Za-z0-9_-]{43})\n\z");
    Check(toolMatch.Success && toolMatch.Groups[1].Value != toolMatch.Groups[2].Value && toolGenerated.SequenceEqual(["DB_PASSWORD", "JWT_SECRET"]),
        $"Edits keep comments and order, drop duplicates, append new names and replace weak secrets (got '{toolEnv}').");
    var toolKept = new string('k', 40);
    Check(AssistantTools.EditEnv($"JWT_SECRET={toolKept}\n", null, null, ["JWT_SECRET"]).Env == $"JWT_SECRET={toolKept}\n"
        && AssistantTools.EditEnv("A=1\r\n", null, [], null) is ("A=1\r\n", []), "A strong saved secret is kept, and no edits leave the file as it was.");
    await Reject(Throws(() => AssistantTools.EditEnv("", new Dictionary<string, string> { ["1BAD"] = "x" }, null, null)), 400, "invalid_env_name",
        "Variable names are checked.");
    await Reject(Throws(() => AssistantTools.EditEnv("", new Dictionary<string, string> { ["A"] = "x\ny" }, null, null)), 400, "invalid_env_value",
        "A value cannot add lines.");
    await Reject(Throws(() => AssistantTools.EditEnv("", null, ["A"], ["A"])), 400, "env_conflict", "A name is set, removed or generated, not two of those.");

    // Every tool answers in redacted text capped at 48 KB, and only Lucia's own errors reach the model.
    AssistantTool ToolOf(Func<object?> body, string tier = ToolTier.Read) => new(AIFunctionFactory.Create(body, new AIFunctionFactoryOptions
    {
        Name = "probe", MarshalResult = static (result, _, _) => new ValueTask<object?>(result),
    }), tier, (text, _) => Task.FromResult(AssistantTools.Patterns(text)), NullLogger.Instance);
    async Task<string> ToolText(AssistantTool tool) => ((TextContent)(await tool.InvokeAsync(new AIFunctionArguments()))!).Text;
    Check(await ToolText(ToolOf(() => null)) == "Done."
        && await ToolText(ToolOf(() => new { Url = "postgres://lucia:pa55word@db/app", Count = 2 })) == """{"url":"postgres://lucia:[redacted]@db/app","count":2}"""
        && (await ToolText(ToolOf(() => new string('x', 60_000)))).StartsWith(new string('x', AssistantTool.MaxOutput) + "\n[Cut at 48 KB.")
        && ToolOf(() => null, ToolTier.Change).Tier == ToolTier.Change, "Tool results are JSON text, redacted and cut at 48 KB.");
    var toolError = await Reject(() => ToolText(ToolOf(() => throw new ArgumentException("Bad value DB_PASSWORD=hunter2hunter2"))), 400, "tool_error",
        "Lucia's own errors reach the model.");
    var toolCrash = await Reject(() => ToolText(ToolOf(() => throw new InvalidOperationException("db at 10.0.0.5 said pa55word"))), 500, "tool_failed",
        "Unexpected errors don't.");
    Check(toolError.Message == "Bad value DB_PASSWORD=[redacted]" && toolCrash.Message == "The tool failed unexpectedly.",
        $"Error text is redacted, and an unexpected error says nothing more (got '{toolError.Message}').");
    var toolCancelled = false;
    try { await ToolText(ToolOf(() => throw new OperationCanceledException())); }
    catch (OperationCanceledException) { toolCancelled = true; }
    Check(toolCancelled, "Cancellation reaches the runtime instead of becoming a tool error.");

    var toolSet = new AssistantTools(null!, null!, null!, null!, null!, null!, null!, NullLogger<AssistantTools>.Instance).Create("tester via assistant", "tester");
    JsonNode ToolSchema(string name) => JsonNode.Parse(toolSet.Single(tool => tool.Name == name).JsonSchema.GetRawText())!;
    static string[] Required(JsonNode schema) => [.. schema["required"]!.AsArray().Select(item => (string)item!)];
    Check(toolSet.Select(tool => tool.Name).Distinct().Count() == toolSet.Count && toolSet.All(tool => !string.IsNullOrWhiteSpace(tool.Description))
        && string.Join(",", toolSet.CountBy(tool => tool.Tier).Select(pair => $"{pair.Key}:{pair.Value}")) == "read:13,web:1,secret:1,change:7,destructive:5"
        && toolSet.All(tool => !tool.JsonSchema.GetRawText().Contains("\"ct\"") && !tool.JsonSchema.GetRawText().Contains("CancellationToken")
            && !tool.JsonSchema.GetRawText().Contains("\"args\"")),
        "The assistant has 13 read, 1 web, 1 secret, 7 change and 5 destructive tools, each described, with no cancellation token or call context in their schemas.");
    var toolWeb = ToolSchema("read_web_page");
    Check(Required(toolWeb).SequenceEqual(["url"]) && (int)toolWeb["properties"]!["start"]!["default"]! == 0,
        $"read_web_page needs a URL, and starts at the top of the page (got {toolWeb.ToJsonString()}).");
    var toolRun = ToolSchema("run_command");
    Check(Required(toolRun).SequenceEqual(["node", "command"])
        && (string)toolRun["properties"]!["interpreter"]!["default"]! == "bash" && (int)toolRun["properties"]!["timeoutSeconds"]!["default"]! == 120,
        $"run_command needs a node and a command, and defaults to bash for two minutes (got {toolRun.ToJsonString()}).");
    var toolAsk = ToolSchema("ask_owner");
    Check(Required(toolAsk).SequenceEqual(["question"]) && (bool)toolAsk["properties"]!["allowFreeform"]!["default"]!
        && Required(ToolSchema("request_secret")).SequenceEqual(["app", "name", "description"]),
        $"ask_owner needs only a question, and request_secret an app, a variable name and a description (got {toolAsk.ToJsonString()}).");
    Check(StackCatalog.Unquoted("ghp_A1b2.C3~d4+E5/f6=g-7") && !StackCatalog.Unquoted("pa$$word") && !StackCatalog.Unquoted("abc #def")
        && !StackCatalog.Unquoted("\"quoted\"") && !StackCatalog.Unquoted("two words"),
        "Secrets are saved unquoted, so only characters compose reads literally are accepted: it would expand $, cut at #, and strip quotes.");
    var toolLogs = ToolSchema("read_logs");
    var toolSave = ToolSchema("save_custom_app")["properties"]!;
    Check(toolLogs["required"]!.AsArray().Select(item => (string)item!).SequenceEqual(["node", "container"])
        && toolSave["edits"]!["items"]!["properties"]!.AsObject().Select(pair => pair.Key).SequenceEqual(["find", "replace"])
        && toolSave["routes"]!["items"]!["properties"]!.AsObject().Select(pair => pair.Key).SequenceEqual(["host", "port", "grpcPort"]),
        $"Tool schemas use the names their descriptions mention (got {toolLogs.ToJsonString()} and {toolSave.ToJsonString()}).");

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
    using var broker = new AssistantBroker(options, NullLogger<AssistantBroker>.Instance);
    await using var runs = new AssistantRuns(runtime, options, new StoppedLifetime(), NullLogger<AssistantRuns>.Instance, broker);
    const string owner = "0123456789abcdef", stranger = "fedcba9876543210";
    var chat = Guid.NewGuid().ToString("N");
    AssistantChatRequest Ask(string text, string messageId = "m1", string mode = "execute", string? model = null, string? session = null) =>
        new(session ?? chat, messageId, text, mode, model, "/apps");

    foreach (var bad in new[] { "", "abc", Guid.NewGuid().ToString("D"), "0123456789ABCDEF0123456789ABCDEF", "..\\" + chat })
        await Reject(() => runs.StartAsync(owner, Ask("Hi", session: bad), "tester via assistant", null, default), 400, "invalid_session", $"Chat id '{bad}' is rejected.");
    await Reject(() => runs.StartAsync(owner, Ask("Hi", mode: "auto"), "tester via assistant", null, default), 400, "invalid_request", "Unknown modes are rejected.");
    await Reject(() => runs.StartAsync(owner, Ask("Hi", messageId: "bad id"), "tester via assistant", null, default), 400, "invalid_request", "Message ids are validated.");
    await Reject(() => runs.StartAsync(owner, Ask("Hi", messageId: null!), "tester via assistant", null, default), 400, "invalid_request", "A missing message id is rejected.");
    await Reject(() => runs.StartAsync(owner, Ask("Hi", model: "gpt 5; rm"), "tester via assistant", null, default), 400, "invalid_request", "Model ids are validated.");
    await Reject(() => runs.StartAsync(owner, Ask(" \n "), "tester via assistant", null, default), 400, "invalid_message", "Blank messages are rejected.");
    await Reject(() => runs.StartAsync(owner, Ask(new string('x', 32_769)), "tester via assistant", null, default), 400, "invalid_message", "Messages over 32,768 characters are rejected.");
    await Reject(() => runs.StartAsync(owner, Ask("Hi"), "tester via assistant", null, default), 503, "assistant_not_connected", "Until the owner signs in with GitHub, the assistant asks them to.");
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
    var local = await runs.StartAsync("00000000000000aa", Ask("Hi", model: "local:ai/qwen3", session: Guid.NewGuid().ToString("N")), "tester via assistant", null, default);
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
    var first = await runs.StartAsync(owner, Ask(question), "tester via assistant", null, default);
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

    var retry = await runs.StartAsync(owner, Ask(question), "tester via assistant", null, default);
    await retry.Completion;
    messages = Node(await runs.GetAsync(owner, chat, default))["messages"]!.AsArray();
    Check(messages.Count == 2 && (string)messages[0]!["id"]! == "m1" && (string)messages[1]!["id"]! == retry.MessageId && retry.MessageId != first.MessageId,
        "Trying again keeps one copy of the question and replaces the old answer.");

    var next = await runs.StartAsync(owner, Ask("Restart it.", messageId: "m2", mode: "plan", model: "gpt-5.1"), "tester via assistant", null, default);
    await next.Completion;
    view = Node(await runs.GetAsync(owner, chat, default));
    messages = view["messages"]!.AsArray();
    Check(messages.Count == 4 && (string)messages[2]!["id"]! == "m2" && (string)messages[3]!["id"]! == next.MessageId
        && (string)messages[3]!["metadata"]!["model"]! == "gpt-5.1" && (string)view["title"]! == "Why is grafana down?",
        "A new message is appended and the chat keeps its title.");

    var longChat = Guid.NewGuid().ToString("N");
    await (await runs.StartAsync(owner, Ask("\r\n " + new string('a', 100) + "\r\nsecond line", session: longChat), "tester via assistant", null, default)).Completion;
    var shortChat = Guid.NewGuid().ToString("N");
    await (await runs.StartAsync(owner, Ask("Short title\r\nMore detail", session: shortChat), "tester via assistant", null, default)).Completion;
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
    await Reject(() => runs.StartAsync(owner, Ask("Hi", session: huge), "tester via assistant", null, default), 409, "session_too_large", "Oversized chats cannot take new messages.");

    github.UserStatus = 401;
    Check(await signIn.ExplainAsync(owner, refused) is { Code: "github_signed_out" } && signIn.Status(owner).State == "disconnected" && !File.Exists(saved),
        "When GitHub no longer accepts the sign-in, the owner is signed out.");

    // The approval broker: Lucia's policy runs reads, asks before changes and refuses what Plan mode can't do. An asked call
    // waits for the owner's answer in the chat, and "always" lasts for the chat.
    AssistantTool GovTool(string name, string tier) => new(AIFunctionFactory.Create((Func<string>)(() => "ok"), new AIFunctionFactoryOptions { Name = name }),
        tier, (value, _) => Task.FromResult(value), NullLogger.Instance);
    AssistantTool[] govTools = [GovTool("peek", ToolTier.Read), GovTool("tweak", ToolTier.Change), GovTool("nudge", ToolTier.Change),
        GovTool("wipe", ToolTier.Destructive), GovTool("fetch", ToolTier.Web), GovTool("vault", ToolTier.Secret), GovTool("run_command", ToolTier.Destructive)];
    var govSettings = new AssistantSettings(["nudge"], ["docs.docker.com"]);
    var govKey = owner + "-" + chat;
    var govCalls = 0;
    var govSeen = new Dictionary<AssistantRun, int>();
    (AssistantStream Stream, AssistantRun Run, AssistantKit Kit) GovTurn(string turnMode = "execute")
    {
        var turnStream = new AssistantStream("m-gov", null);
        var turnRun = new AssistantRun("m-gov", default);
        return (turnStream, turnRun, broker.Kit(new AssistantTurn(owner, govKey, "Hi", null, turnMode), govSettings, govTools, turnStream, turnRun));
    }
    Task<PermissionDecision> GovCall(AssistantKit kit, string tool, object? callArgs = null) => kit.Permission(new PermissionRequestCustomTool
    {
        ToolName = tool, ToolDescription = "", ToolCallId = $"call-{++govCalls}",
        Args = callArgs is null ? null : JsonSerializer.SerializeToElement(callArgs),
    }, null!);
    List<JsonNode> GovNew(AssistantRun target)
    {
        var (fresh, _, _) = target.Read(govSeen.GetValueOrDefault(target));
        govSeen[target] = govSeen.GetValueOrDefault(target) + fresh.Count;
        return [.. fresh.Select(chunk => JsonNode.Parse(chunk)!)];
    }
    const string govOwn = "reject: Lucia's assistant can only use its own tools.";
    const string govPlan = "reject: Refused: the chat is in Plan mode, which changes nothing. Put this step in your plan; the owner can switch to Execute to run it.";

    var (govStream, govRun, govKit) = GovTurn();
    Check(Verdict(await GovCall(govKit, "peek")) == "approve" && GovNew(govRun).Count == 0, "Reads run without asking, and add nothing to the chat.");
    Check(Verdict(await GovCall(govKit, "vault", new { app = "grafana", name = "GRAFANA_TOKEN" })) == "approve" && GovNew(govRun).Count == 0,
        "Asking the owner for a secret needs no approval: the secret card is their consent.");
    Check(Verdict(await GovCall(govKit, "shell")) == govOwn && Verdict(await govKit.Permission(new PermissionRequest { Kind = "shell" }, null!)) == govOwn
        && GovNew(govRun).Count == 0, "The runtime's own shell, file and URL tools, and unknown tools, are refused.");

    Check(Verdict(await GovCall(govKit, "nudge", new { app = "grafana" })) == "approve", "A change the owner set to run automatically runs.");
    var govChunks = GovNew(govRun);
    Check(Types(govChunks) == "tool-input-available,tool-approval-request,tool-approval-response"
        && (string)govChunks[0]["toolName"]! == "nudge" && (string)govChunks[0]["input"]!["app"]! == "grafana"
        && (string)govChunks[1]["toolCallId"]! == (string)govChunks[0]["toolCallId"]! && (bool)govChunks[1]["isAutomatic"]! && govChunks[1]["reason"] is null
        && (bool)govChunks[2]["approved"]! && (string)govChunks[2]["reason"]! == "Runs automatically in your assistant settings.",
        "The chat shows that it ran on its own, and why.");

    var govAsk = GovCall(govKit, "tweak", new { app = "grafana", setEnv = new Dictionary<string, string> { ["DB_PASSWORD"] = "hunter2hunter2" } });
    govChunks = GovNew(govRun);
    var govApproval = (string)govChunks[1]["approvalId"]!;
    Check(!govAsk.IsCompleted && Types(govChunks) == "tool-input-available,tool-approval-request"
        && (string)govChunks[0]["input"]!["setEnv"]!["DB_PASSWORD"]! == "••••••" && (string)govChunks[1]["reason"]! == "This changes your lab."
        && govChunks[1]["isAutomatic"] is null, "Other changes wait for the owner, who sees their arguments with secrets masked.");
    Check(!runs.Respond(stranger, chat, govApproval, true, null, true) && !govAsk.IsCompleted, "Another owner cannot answer it.");
    Check(runs.Respond(owner, chat, govApproval, true, null, always: true) && Verdict(await govAsk) == "approve"
        && !runs.Respond(owner, chat, govApproval, false, null, false), "The owner approves it, once.");
    govChunks = GovNew(govRun);
    Check(Types(govChunks) == "tool-approval-response" && (bool)govChunks[0]["approved"]! && (string)govChunks[0]["approvalId"]! == govApproval,
        "The chat records the answer.");
    Check(Verdict(await GovCall(govKit, "tweak", new { app = "grafana" })) == "approve", "After 'always', the tool runs without asking in this chat.");
    govChunks = GovNew(govRun);
    Check(Types(govChunks) == "tool-input-available,tool-approval-request,tool-approval-response" && (bool)govChunks[1]["isAutomatic"]!
        && (string)govChunks[2]["reason"]! == "You allowed this for this chat.", "The chat says the owner allowed it.");

    var govWipe = GovCall(govKit, "wipe", new { app = "grafana" });
    govChunks = GovNew(govRun);
    Check(Types(govChunks) == "tool-input-available,tool-approval-request" && (string)govChunks[1]["reason"]! == "This can remove data or interrupt your lab."
        && runs.Respond(owner, chat, (string)govChunks[1]["approvalId"]!, false, "not today", false)
        && Verdict(await govWipe) == "reject: The owner declined this tool call: not today",
        "Destructive calls ask, and a refusal reaches the model with the owner's reason.");
    govChunks = GovNew(govRun);
    Check(Types(govChunks) == "tool-approval-response,tool-output-denied" && !(bool)govChunks[0]["approved"]! && (string)govChunks[0]["reason"]! == "not today",
        "A refused call ends as denied.");
    govWipe = GovCall(govKit, "wipe", new { app = "grafana" });
    Check(runs.Respond(owner, chat, (string)GovNew(govRun)[1]["approvalId"]!, true, null, always: true) && Verdict(await govWipe) == "approve"
        && Types(GovNew(govRun)) == "tool-approval-response", "The owner can approve a destructive call…");
    govWipe = GovCall(govKit, "wipe", new { app = "grafana" });
    govChunks = GovNew(govRun);
    Check(!govWipe.IsCompleted && Types(govChunks) == "tool-input-available,tool-approval-request", "…but 'always' never covers one: the next asks again.");
    govRun.Stop.Cancel();
    Check(Verdict(await govWipe) == "reject: The owner stopped this turn." && GovNew(govRun).Count == 0
        && !runs.Respond(owner, chat, (string)govChunks[1]["approvalId"]!, true, null, false),
        "Stopping the turn refuses the waiting call, which can no longer be answered.");
    var govEnd = govStream.Abort().Select(Node).ToList();
    Check(Types(govEnd) == "tool-approval-response,tool-output-denied,tool-output-error,tool-output-error,tool-output-error,tool-output-error,message-metadata,abort"
        && (string)govEnd[0]["reason"]! == "The run stopped before you answered." && govStream.Message.Parts[^1].State == "output-denied"
        && govStream.Message.Parts.Count(part => part.State == "output-error") == 4,
        "When the turn ends, its unanswered approval is refused and approved calls that never finished are marked unfinished.");

    // Each destructive approval says what it puts at risk. Names come from the model, so only short plain ones are repeated.
    var (_, warnRun, warnKit) = GovTurn();
    var warnAsk = GovCall(warnKit, "run_command", new { node = "lucialab02", command = "nvidia-smi" });
    govChunks = GovNew(warnRun);
    Check(Types(govChunks) == "tool-input-available,tool-approval-request"
        && (string)govChunks[1]["reason"]! == "This runs the command below as root on lucialab02. It can change anything there."
        && runs.Respond(owner, chat, (string)govChunks[1]["approvalId"]!, false, null, false) && Verdict(await warnAsk) == "reject: The owner declined this tool call.",
        "A destructive approval names what it risks.");
    foreach (var (warnTool, warnInput, warnText) in new (string, object?, string)[]
    {
        ("delete_app", new { app = "whoami" }, "Lucia stops whoami and removes it from its server. Its data directory stays there."),
        ("restore_backup", new { app = "grafana", snapshot = "a1b2c3" }, "The backup replaces grafana's data. Anything changed since it was taken is lost."),
        ("set_public_route", new { app = "whoami", host = "whoami.lab.example", publicName = "whoami.example.com" },
            "Anyone on the internet will be able to reach whoami.example.com."),
        ("set_public_route", new { app = "whoami", host = "whoami.lab.example" }, "Anyone using whoami.lab.example from outside your network loses access."),
        ("set_public_route", new { app = "whoami", host = "whoami.lab.example", publicName = "" },
            "Anyone using whoami.lab.example from outside your network loses access."),
        ("node_action", new { node = "lucialab02", action = "install-updates" }, "Apps on lucialab02 may restart briefly while the updates install."),
        ("node_action", new { node = "lucialab02", action = "restart" }, "Every app on lucialab02 is offline for a few minutes while it restarts."),
        ("node_action", new { node = "lucialab02", action = "update-agent" }, "Lucia loses touch with lucialab02 for about a minute while its agent updates."),
        ("node_action", new { node = "lucialab02", action = "check-updates" }, "This can remove data or interrupt your lab."),
        ("run_command", new { node = "lab02; reboot", command = "id" }, "This runs the command below as root on this server. It can change anything there."),
        ("delete_app", new { app = new string('a', 101) }, "Lucia stops this app and removes it from its server. Its data directory stays there."),
        ("delete_app", new { app = 5 }, "Lucia stops this app and removes it from its server. Its data directory stays there."),
        ("delete_app", new[] { "whoami" }, "Lucia stops this app and removes it from its server. Its data directory stays there."),
        ("restore_backup", null, "The backup replaces this app's data. Anything changed since it was taken is lost."),
    })
        Check(AssistantTools.Warning(warnTool, JsonSerializer.SerializeToNode(warnInput)) == warnText,
            $"{warnTool} {JsonSerializer.Serialize(warnInput)} warns '{warnText}' (got '{AssistantTools.Warning(warnTool, JsonSerializer.SerializeToNode(warnInput))}').");

    var (_, webRun, webKit) = GovTurn();
    Check(Verdict(await GovCall(webKit, "fetch", new { url = "https://docs.docker.com/compose/" })) == "approve", "Reading an allowed site runs.");
    govChunks = GovNew(webRun);
    Check(Types(govChunks) == "tool-input-available,tool-approval-request,tool-approval-response"
        && (string)govChunks[2]["reason"]! == "docs.docker.com is on the assistant's allowed sites.", "The chat says the site is allowed.");
    foreach (var webBad in new object?[] { new { url = "ftp://docs.docker.com/" }, new { url = "/compose" }, null, new { url = 5 } })
        Check(Verdict(await GovCall(webKit, "fetch", webBad)) == "reject: Give an absolute http or https URL.",
            $"Web reads need an absolute http or https URL ({JsonSerializer.Serialize(webBad)}).");
    Check(GovNew(webRun).Count == 0, "Malformed web reads add nothing to the chat.");
    var webAsk = GovCall(webKit, "fetch", new { url = "https://example.com/" });
    govChunks = GovNew(webRun);
    Check(Types(govChunks) == "tool-input-available,tool-approval-request" && (string)govChunks[1]["reason"]! == "example.com isn't on the assistant's allowed sites."
        && runs.Respond(owner, chat, (string)govChunks[1]["approvalId"]!, true, null, always: true) && Verdict(await webAsk) == "approve",
        "Other sites ask first.");
    GovNew(webRun);
    Check(Verdict(await GovCall(webKit, "fetch", new { url = "https://EXAMPLE.com./x" })) == "approve"
        && (string)GovNew(webRun)[2]["reason"]! == "You allowed this for this chat.", "Allowing a site for the chat covers any spelling of its name.");

    var (_, planRun, planKit) = GovTurn("plan");
    Check(Verdict(await GovCall(planKit, "peek")) == "approve" && GovNew(planRun).Count == 0, "Plan mode still reads.");
    foreach (var planTool in new[] { "nudge", "tweak", "wipe", "vault" })
    {
        var planVerdict = Verdict(await GovCall(planKit, planTool, new { app = "grafana" }));
        var planChunks = GovNew(planRun);
        Check(planVerdict == govPlan && Types(planChunks) == "tool-input-available,tool-approval-request,tool-approval-response,tool-output-denied"
            && (string)planChunks[2]["reason"]! == "Plan mode doesn't change anything. Switch to Execute to run it.",
            $"Plan mode refuses {planTool}, even when it would run on its own, and says so in the chat (got '{planVerdict}').");
    }
    Check(Verdict(await GovCall(planKit, "fetch", new { url = "https://docs.docker.com/" })) == "approve", "Plan mode still reads allowed sites.");

    var (_, _, limitKit) = GovTurn();
    var limitVerdicts = new List<string>();
    for (var limitCall = 0; limitCall <= AssistantBroker.MaxCallsPerTurn; limitCall++) limitVerdicts.Add(Verdict(await GovCall(limitKit, "peek")));
    Check(limitVerdicts.Count(verdict => verdict == "approve") == AssistantBroker.MaxCallsPerTurn
        && limitVerdicts[^1] == "reject: This turn has used its 50 tool calls. Summarize what you found and ask the owner how to go on.",
        $"A turn makes up to 50 tool calls (got {string.Join(" | ", limitVerdicts.Distinct())}).");

    broker.Forget(govKey);
    var (_, forgotRun, forgotKit) = GovTurn();
    var forgotCall = GovCall(forgotKit, "tweak", new { app = "grafana" });
    var forgotChunks = GovNew(forgotRun);
    Check(Types(forgotChunks) == "tool-input-available,tool-approval-request" && runs.Respond(owner, chat, (string)forgotChunks[1]["approvalId"]!, false, null, false)
        && Verdict(await forgotCall) == "reject: The owner declined this tool call.", "Deleting the chat forgets what the owner allowed in it.");
    Check(!runs.Respond(owner, chat, "approval-x", true, null, false), "Unknown approvals cannot be answered.");
    await Reject(Throws(() => runs.Respond(owner, "abc", "approval-x", true, null, false)), 400, "invalid_session", "Answers check the chat id.");

    // Questions: a tool call waits for the owner's answer, or a secret they type, on its card in the chat. Only the chat's owner
    // answers, a secret never answers a question whose answer the model reads, and stopping the turn ends the wait.
    var pendingAnswer = broker.AskAsync(govKey, "call-q1", "answer", default);
    Check(!pendingAnswer.IsCompleted && !runs.AnswerQuestion(stranger, chat, "call-q1", "answer", "yes")
        && !runs.AnswerQuestion(owner, chat, "call-q1", "secret", "hunter2hunter2") && !pendingAnswer.IsCompleted,
        "Only the chat's owner answers a question, and never with a secret.");
    Check(runs.AnswerQuestion(owner, chat, "call-q1", "answer", "Port 8080") && await pendingAnswer == "Port 8080"
        && !runs.AnswerQuestion(owner, chat, "call-q1", "answer", "again"), "The owner answers a question once.");
    var pendingSecret = broker.AskAsync(govKey, "call-q2", "secret", default);
    await Reject(() => broker.AskAsync(govKey, "call-q2", "secret", default), 409, "question_pending", "A tool call asks once.");
    Check(!runs.AnswerQuestion(owner, chat, "call-q2", "answer", "hunter2hunter2") && runs.AnswerQuestion(owner, chat, "call-q2", null, null)
        && await pendingSecret is null, "A secret card takes only a secret, and the owner can decline it.");
    using var pendingStop = new CancellationTokenSource();
    var pendingStopped = broker.AskAsync(govKey, "call-q3", "answer", pendingStop.Token);
    await pendingStop.CancelAsync();
    var pendingCancelled = false;
    try { await pendingStopped; }
    catch (OperationCanceledException) { pendingCancelled = true; }
    Check(pendingCancelled && !runs.AnswerQuestion(owner, chat, "call-q3", "answer", "late"),
        "Stopping the turn ends the wait, and the question can no longer be answered.");

    // The question tools over a lab with one custom app and one catalog app: what the model gets back, and what it can't ask for.
    var labProtection = new EphemeralDataProtectionProvider();
    var labEnv = labProtection.CreateProtector("Lucia.Homelab.StackEnvironment.v1").Protect("GF_ADMIN_PASSWORD=Xk3vN9qL2mP7wR4t\n");
    StoredStack LabApp(string name, StackTemplate? template) => new(name, "services: {}\n", labEnv,
        new StackManifest(1, new StackPlacement("lucialab01"), template), "Running", 1, 0, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "tester");
    Directory.CreateDirectory(Path.Combine(storage, "lab", "stacks"));
    File.WriteAllText(Path.Combine(storage, "lab", "stacks", "stacks.json"), JsonSerializer.Serialize(
        new StackFile(1, [LabApp("grafana", null), LabApp("jellyfin", new StackTemplate("jellyfin", 1))]), Lucia.Homelab.Server.Domains.DomainOnboardingStore.Json));
    var labStacks = new StackStore(Options.Create(new Lucia.Homelab.Server.Onboarding.HardwareOnboardingOptions { StateDirectory = Path.Combine(storage, "lab", "state") }),
        labProtection, null!, null!, null!, null!, TimeProvider.System, null!);
    var labTools = new AssistantTools(null!, labStacks, null!, null!, null!, null!, broker, NullLogger<AssistantTools>.Instance)
        .Create("tester via assistant", "tester");
    AIFunctionArguments InChat(string callId, Dictionary<string, object?> values) => new(values)
    {
        Context = new Dictionary<object, object?> { [typeof(ToolInvocation)] = new ToolInvocation { SessionId = govKey, ToolCallId = callId, ToolName = "tool" } },
    };
    async Task<string> LabCall(string tool, AIFunctionArguments callArgs) =>
        ((TextContent)(await labTools.Single(item => item.Name == tool).InvokeAsync(callArgs))!).Text;
    async Task<string> Answered(string tool, string callId, Dictionary<string, object?> values, string? kind, string? value)
    {
        var call = LabCall(tool, InChat(callId, values));
        for (var i = 0; i < 500 && !call.IsCompleted && !runs.AnswerQuestion(owner, chat, callId, kind, value); i++) await Task.Delay(10);
        return await call;
    }
    async Task LabRefuses(string tool, AIFunctionArguments callArgs, string message)
    {
        var refusal = await Reject(() => LabCall(tool, callArgs), 400, "tool_error", $"{tool} refuses: {message}");
        Check(refusal.Message == message, $"{tool} explains its refusal (got '{refusal.Message}').");
    }
    Dictionary<string, object?> SecretAsk(string name, string app = "grafana", string description = "The admin token, from Grafana's settings.") =>
        new() { ["app"] = app, ["name"] = name, ["description"] = description };

    Check(await Answered("ask_owner", "call-q4", new() { ["question"] = "Which port should Grafana use?", ["choices"] = new[] { "3000", "8080" } },
        "answer", "8080") == """{"answer":"8080"}""", "ask_owner hands the model the owner's answer.");
    Check((await Answered("ask_owner", "call-q5", new() { ["question"] = "Restart Grafana now?" }, null, null)).StartsWith("The owner chose not to answer."),
        "…or tells it they chose not to.");
    await LabRefuses("ask_owner", new AIFunctionArguments(new Dictionary<string, object?> { ["question"] = "Restart Grafana now?" }), "This tool only works in a chat.");
    await LabRefuses("ask_owner", InChat("call-q6", new() { ["question"] = "Restart Grafana now?", ["allowFreeform"] = false }),
        "Offer choices, or let the owner type an answer.");
    await LabRefuses("ask_owner", InChat("call-q6", new() { ["question"] = "Which port?", ["choices"] = Enumerable.Range(1, 9).Select(n => $"Port {n}").ToArray() }),
        "Offer up to 8 choices of up to 200 characters each.");

    var secretCall = LabCall("request_secret", InChat("call-s1", SecretAsk("GRAFANA_TOKEN")));
    var secretAsAnswer = runs.AnswerQuestion(owner, chat, "call-s1", "answer", "Xk3vN9qL2mP7wR4t");
    for (var i = 0; i < 500 && !secretCall.IsCompleted && !runs.AnswerQuestion(owner, chat, "call-s1", null, null); i++) await Task.Delay(10);
    Check(!secretAsAnswer && await secretCall == "The owner chose not to give GRAFANA_TOKEN. Nothing was saved.",
        "A secret card can't be answered as a question, and declining it saves nothing.");
    await LabRefuses("request_secret", InChat("call-s2", SecretAsk("1TOKEN")), "\"1TOKEN\" isn't an environment variable name.");
    await LabRefuses("request_secret", InChat("call-s2", SecretAsk("GRAFANA_URL")),
        "Lucia only hides variables named as secrets. Save it as GRAFANA_URL_TOKEN or GRAFANA_URL_PASSWORD, and pass that to the container in the compose.");
    await LabRefuses("request_secret", InChat("call-s2", SecretAsk("GRAFANA_TOKEN", description: " ")), "Describe the value in up to 500 characters.");
    await LabRefuses("request_secret", InChat("call-s2", SecretAsk("GRAFANA_TOKEN", "loki")), "There's no app named loki. Create it with save_custom_app first.");
    await LabRefuses("request_secret", InChat("call-s2", SecretAsk("JELLYFIN_API_KEY", "jellyfin")),
        "jellyfin is the catalog app jellyfin: the owner types its secrets on the Apps page.");
    await LabRefuses("request_secret", new AIFunctionArguments(SecretAsk("GRAFANA_TOKEN")), "This tool only works in a chat.");

    // The owner's assistant settings: change tools that run on their own, and sites it reads without asking.
    Check(ReferenceEquals(await broker.SettingsAsync(owner, default), AssistantSettings.Default), "Without saved settings, the defaults apply.");
    string[] govChange = ["tweak", "nudge"];
    var govSaved = await broker.SaveSettingsAsync(owner, new AssistantSettings(["nudge"], ["Docs.Docker.com.", "bücher.example", "docs.docker.com"]), govChange, default);
    var govLoaded = await broker.SettingsAsync(owner, default);
    Check(govSaved.Hosts.SequenceEqual(["docs.docker.com", "xn--bcher-kva.example"]) && govLoaded.AutoTools.SequenceEqual(["nudge"])
        && govLoaded.Hosts.SequenceEqual(govSaved.Hosts), $"Sites are saved once each, as lower-case punycode names (got {string.Join(",", govSaved.Hosts)}).");
    const string govSiteHelp = " Add sites by name, like docs.docker.com.";
    foreach (var (govBadTools, govBadHosts, govCode, govMessage) in new (string[], string[], string, string)[]
    {
        (["peek"], [], "invalid_settings", "Automatic tools must be the assistant's change tools, each listed once."),
        (["nudge", "nudge"], [], "invalid_settings", "Automatic tools must be the assistant's change tools, each listed once."),
        ([], ["docs.docker.com", "10.0.0.5."], "invalid_sites", "10.0.0.5. is an address, not a site name." + govSiteHelp),
        ([], ["[fd00::1]"], "invalid_sites", "[fd00::1] is an address, not a site name." + govSiteHelp),
        ([], ["https://docs.docker.com/"], "invalid_sites", "https://docs.docker.com/ isn't a site name." + govSiteHelp),
        ([], [.. Enumerable.Range(0, 51).Select(number => $"site{number}.example")], "invalid_sites", "Add at most 50 sites."),
        ([], ["exa mple.com"], "invalid_sites", "exa mple.com isn't a site name." + govSiteHelp),
        ([], [new string('a', 70) + " b"], "invalid_sites", new string('a', 59) + "… isn't a site name." + govSiteHelp),
        ([], [""], "invalid_sites", "Each site needs a name, like docs.docker.com."),
    })
    {
        var govError = await Reject(() => broker.SaveSettingsAsync(owner, new AssistantSettings(govBadTools, govBadHosts), govChange, default), 400, govCode,
            $"Settings with tools [{string.Join(",", govBadTools)}] and {govBadHosts.Length} sites like '{govBadHosts.LastOrDefault()}' are refused.");
        Check(govError.Message == govMessage, $"The refusal names the problem: '{govMessage}' (got '{govError.Message}').");
    }
    File.WriteAllText(Path.Combine(directory, "users", owner, "settings.json"), "{ not json");
    Check(ReferenceEquals(await broker.SettingsAsync(owner, default), AssistantSettings.Default), "Unreadable settings fall back to the defaults.");

    // read_web_page reads public sites only. The guard checks every connection after DNS, so names that resolve into the lab are refused too.
    foreach (var (webAddress, webPublic) in new (string, bool)[]
    {
        ("1.1.1.1", true), ("8.8.8.8", true), ("2606:4700::1111", true), ("::ffff:8.8.8.8", true), ("10.1.2.3", false), ("127.0.0.1", false),
        ("169.254.169.254", false), ("172.16.5.4", false), ("192.168.0.222", false), ("100.100.100.100", false), ("0.0.0.0", false),
        ("224.0.0.1", false), ("255.255.255.255", false), ("::1", false), ("::", false), ("fe80::1", false), ("fd00::1", false),
        ("::ffff:192.168.0.1", false), ("2001:db8::1", false), ("2002:c0a8:1::1", false), ("64:ff9b::808:808", false),
    })
        Check(AssistantWeb.Public(IPAddress.Parse(webAddress)) == webPublic, $"{webAddress} is {(webPublic ? "" : "not ")}a public address.");
    var webDoc = new Uri("https://docs.example.com/guide/start");
    var webText = AssistantWeb.Text("""
        <!doctype html><html><head><title>Getting &amp; started</title><style>p { color: red }</style></head>
        <body><nav><a href="/">Home</a></nav>
        <main><h1>Install</h1><p>Run   <code>lucia up</code> and
        read <a href="../faq#ports">the FAQ</a>, or <a href="#top">go back</a>.<a href="https://github.com/x"><img src="logo.png"></a></p>
        <script>alert("x")</script>
        <ul><li>One</li><li>Two</li></ul>
        <pre><code>services:
          app:
            command: echo "a &lt; b"
        </code></pre>
        <p>Done.</p></main><footer>&copy; Example</footer></body></html>
        """, webDoc);
    Check(webText == """
        # Getting & started

        # Install

        Run `lucia up` and read [the FAQ](https://docs.example.com/faq#ports), or go back.

        - One
        - Two

        ```
        services:
          app:
            command: echo "a < b"
        ```

        Done.
        """.ReplaceLineEndings("\n"), $"A page becomes Markdown-like text without its scripts, menus and footer (got '{webText}').");
    Check(AssistantWeb.Text("<p>a &#xE000;9&#xE001; b</p>", webDoc) == "a  b", "A page can't spell Lucia's code-block marker.");

    // A loopback site stands in for the web. The guard is checked first: once it is swapped out, pooled connections would skip it.
    var webDigits = string.Concat(Enumerable.Repeat("0123456789", 5_000));
    var webEmoji = new string('a', 19_999) + "\U0001F600b";
    var webBuilder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
    webBuilder.Logging.ClearProviders();
    await using var webSite = webBuilder.Build();
    webSite.Urls.Add("http://127.0.0.1:0");
    var webHits = 0;
    webSite.Use(_ => async context =>
    {
        Interlocked.Increment(ref webHits);
        (int Status, string? Type, string Body, string? Location) reply = context.Request.Path.Value switch
        {
            "/page" => (200, "text/html; charset=utf-8",
                "<html><head><title>Lab notes</title></head><body><main><h2>Ports</h2><p>See <a href=\"/docs\">the docs</a>.</p></main></body></html>", null),
            "/move" => (302, null, "", "/page"),
            "/away" => (302, null, "", "http://example.com/x"),
            "/loop" => (302, null, "", "/loop"),
            "/image" => (200, "image/png", "PNG", null),
            "/long" => (200, "text/plain", webDigits, null),
            "/emoji" => (200, "text/plain; charset=utf-8", webEmoji, null),
            _ => (404, "text/plain", "Not here.", null),
        };
        context.Response.StatusCode = reply.Status;
        if (reply.Type is not null) context.Response.ContentType = reply.Type;
        if (reply.Location is not null) context.Response.Headers.Location = reply.Location;
        await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(reply.Body));
    });
    await webSite.StartAsync();
    var webBase = webSite.Urls.Single().TrimEnd('/');
    try
    {
        const string webPrivate = " is on a private or reserved network. read_web_page reads public sites only; use the lab tools for the owner's network.";
        var webRefused = await Reject(() => AssistantWeb.Read(webBase + "/page", 0, default), 403, "private_address", "A loopback address is refused.");
        var webNamed = await Reject(() => AssistantWeb.Read(webBase.Replace("127.0.0.1", "localhost") + "/page", 0, default), 403, "private_address",
            "A name that resolves to loopback is refused.");
        Check(webRefused.Message == "127.0.0.1" + webPrivate && webNamed.Message == "localhost" + webPrivate,
            $"The refusal names the host and points to the lab tools (got '{webRefused.Message}').");
        await LabRefuses("read_web_page", new AIFunctionArguments(new Dictionary<string, object?> { ["url"] = webBase + "/page" }), "127.0.0.1" + webPrivate);
        Check(webHits == 0, $"The guard refuses before a request is sent (the site saw {webHits}).");
        foreach (var webBad in new[] { "ftp://example.com/", "/relative/page", "not a url", "https://example.com/" + new string('a', AssistantWeb.MaxUrl) })
            await Reject(() => AssistantWeb.Read(webBad, 0, default), 400, "invalid_url", $"'{webBad[..Math.Min(webBad.Length, 30)]}' isn't read.");
        await Reject(() => AssistantWeb.Read("https://example.com/", -1, default), 400, "invalid_start", "A negative start is refused.");

        AssistantWeb.Reachable = _ => true;
        var webPage = await AssistantWeb.Read(webBase + "/page", 0, default);
        Check(webPage == $"URL: {webBase}/page\n\n# Lab notes\n\n## Ports\n\nSee [the docs]({webBase}/docs).", $"A page reads as text (got '{webPage}').");
        Check(await AssistantWeb.Read(webBase + "/move", 0, default) == webPage, "A redirect within the site is followed, and the text names where it landed.");
        Check(await AssistantWeb.Read(webBase + "/away", 0, default)
            == $"{webBase}/away moved to http://example.com/x, which is another site. Read it with read_web_page if you still need it.",
            "A redirect to another site stops, so the owner's allowed sites decide on that site too.");
        await Reject(() => AssistantWeb.Read(webBase + "/loop", 0, default), 502, "too_many_redirects", "A redirect loop stops.");
        var webImage = await Reject(() => AssistantWeb.Read(webBase + "/image", 0, default), 415, "not_text", "An image isn't read.");
        var webMissing = await Reject(() => AssistantWeb.Read(webBase + "/missing", 0, default), 502, "page_error", "An error page is reported.");
        Check(webImage.Message == $"{webBase}/image is image/png, not a page or a text file." && webMissing.Message == $"{webBase}/missing answered HTTP 404.",
            $"Refusals say what the site sent (got '{webImage.Message}' and '{webMissing.Message}').");
        Check(await AssistantWeb.Read(webBase + "/long", 0, default)
                == $"URL: {webBase}/long\n\n{webDigits[..20_000]}\n\n[Characters 0 to 20000 of 50000. Read on with start 20000.]"
            && await AssistantWeb.Read(webBase + "/long", 40_000, default) == $"URL: {webBase}/long\n\n{webDigits[40_000..]}",
            "A long page is read 20,000 characters at a time, and the note says where to go on.");
        await Reject(() => AssistantWeb.Read(webBase + "/long", 50_000, default), 400, "past_end", "Starting past the end is refused.");
        Check(await AssistantWeb.Read(webBase + "/emoji", 0, default)
                == $"URL: {webBase}/emoji\n\n{webEmoji[..19_999]}\n\n[Characters 0 to 19999 of 20002. Read on with start 19999.]"
            && await AssistantWeb.Read(webBase + "/emoji", 20_000, default) == $"URL: {webBase}/emoji\n\n\U0001F600b",
            "A read never splits an emoji in two.");
        Check(webHits == 17, $"Each read is one request per hop (the site saw {webHits}).");
    }
    finally
    {
        AssistantWeb.Reachable = AssistantWeb.Public;
        await webSite.StopAsync();
    }

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
