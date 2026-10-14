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
using Lucia.Homelab.Server.Assistant.Skills;
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
    var filling = new AssistantStream("a1c", "gpt-x");
    var fillChunks = new List<JsonNode>();
    SessionUsageInfoEvent Fill(long used) => new() { Data = new() { CurrentTokens = used, MessagesLength = 3, TokenLimit = 128_000, SystemTokens = 900, ConversationTokens = 3_000, ToolDefinitionsTokens = 1_100 } };
    Feed(filling, fillChunks, Fill(5_000));
    Feed(filling, fillChunks, Fill(5_000));
    Feed(filling, fillChunks, Fill(6_500));
    Check(Types(fillChunks) == "message-metadata,message-metadata" && (long)fillChunks[0]["messageMetadata"]!["context"]!["used"]! == 5_000
        && (long)fillChunks[1]["messageMetadata"]!["context"]!["used"]! == 6_500 && (long)fillChunks[1]["messageMetadata"]!["context"]!["limit"]! == 128_000
        && (long)fillChunks[1]["messageMetadata"]!["context"]!["system"]! == 900 && (long)fillChunks[1]["messageMetadata"]!["context"]!["tools"]! == 1_100,
        "The runtime's context fill streams as metadata, once per change, with its limit and where the tokens go.");
    var lastInput = new AssistantStream("a1d", null);
    Feed(lastInput, [], new AssistantUsageEvent { Data = new() { Model = "m", InputTokens = 7_000, OutputTokens = 100, MaxPromptTokens = 64_000 } });
    Check((long)Node(lastInput.Finish()[^1])["messageMetadata"]!["context"]!["used"]! == 7_000
        && (long)lastInput.Message.Metadata!["context"]!["limit"]! == 64_000,
        "Without a context report from the runtime, the last call's prompt size shows how full the context is.");

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
        && string.Join(",", toolSet.CountBy(tool => tool.Tier).OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}:{pair.Value}")) == "change:9,destructive:6,read:16,registry:1,secret:1,web:1"
        && toolSet.All(tool => !tool.JsonSchema.GetRawText().Contains("\"ct\"") && !tool.JsonSchema.GetRawText().Contains("CancellationToken")
            && !tool.JsonSchema.GetRawText().Contains("\"args\"")),
        "The assistant has 16 read, 1 web, 1 registry, 1 secret, 9 change and 6 destructive tools, each described, with no cancellation token or call context in their schemas.");
    var toolRegistry = ToolSchema("query_registry");
    // Reasoning for models the owner brought: written into the request for the engine, only for chat requests, and only for allowed efforts.
    string RewriteOf(string json, string effort) => System.Text.Encoding.UTF8.GetString(AssistantEngineProxy.Rewrite(System.Text.Encoding.UTF8.GetBytes(json), effort));
    var disabled = JsonNode.Parse(RewriteOf("""{"model":"q","messages":[],"reasoning_effort":"high","chat_template_kwargs":{"x":1}}""", "none"))!;
    var lowered = JsonNode.Parse(RewriteOf("""{"model":"q","messages":[]}""", "low"))!;
    Check(disabled["reasoning_effort"] is null && !(bool)disabled["chat_template_kwargs"]!["enable_thinking"]! && (int)disabled["chat_template_kwargs"]!["x"]! == 1
        && (string)lowered["reasoning_effort"]! == "low" && lowered["chat_template_kwargs"] is null
        && RewriteOf("""{"input":"x"}""", "low") == """{"input":"x"}""" && RewriteOf("not json", "low") == "not json",
        "Disabled turns thinking off with enable_thinking:false, other efforts send reasoning_effort, and only chat requests change.");
    Check(AssistantEngineProxy.Target(new Uri("http://10.0.0.6:8000/v1/"), "/assistant-engine/v1/chat/completions", "?a=1").ToString() == "http://10.0.0.6:8000/v1/chat/completions?a=1"
        && AssistantEngineProxy.Target(new Uri("http://10.0.0.6:8000/v1/"), "/assistant-engine/v1/models", "").ToString() == "http://10.0.0.6:8000/v1/models",
        "The shim sends a request on to the same path under the engine's own base.");
    var shim = new AssistantEngineProxy(() => 8080);
    var opened = shim.Open(new GitHub.Copilot.ProviderConfig { Type = "openai", WireApi = "completions", BaseUrl = "http://10.0.0.6:8000/v1", ApiKey = "real-key", MaxOutputTokens = 100, MaxPromptTokens = 900 }, "low");
    Check(opened is { Provider: { BaseUrl: "http://127.0.0.1:8080/assistant-engine/v1" } routedProvider } && routedProvider.ApiKey != "real-key" && routedProvider.MaxPromptTokens == 900
        && new AssistantEngineProxy(() => null).Open(new GitHub.Copilot.ProviderConfig { BaseUrl = "http://10.0.0.6:8000/v1" }, "low") is null,
        "A turn's provider points at the shim with its own key, never the engine's, and needs the host to listen on HTTP.");
    opened?.Lease.Dispose();
    var imageRows = AssistantTools.ParseImages("""
        {"Repository":"ghcr.io/home-operations/jackett","Tag":"0.24.2668","Digest":"sha256:abc","ID":"sha256:111","CreatedAt":"2026-10-01 10:00:00 +0000 UTC"}
        {"Repository":"local/app","Tag":"dev","Digest":"<none>","ID":"sha256:222","CreatedAt":"x"}
        not json
        {"Repository":"<none>","Tag":"<none>","Digest":"<none>","ID":"sha256:333"}
        """, null);
    Check(Required(ToolSchema("list_node_images")).SequenceEqual(["node"]) && imageRows.Length == 2
        && AssistantTools.ParseImages("{\"Repository\":\"a/b\",\"Tag\":\"1\",\"Digest\":\"sha256:z\"}\n{\"Repository\":\"c/d\",\"Tag\":\"2\"}", "A/B").Length == 1
        && JsonSerializer.Serialize(imageRows[1]).Contains("\"Digest\":null", StringComparison.Ordinal),
        "list_node_images needs a node, reads Docker's image lines, skips junk and untagged images, filters by name, and gives locally built images no digest.");
    Check(Required(toolRegistry).SequenceEqual(["image"])
        && AssistantTools.RegistryImage("nginx") is ("docker.io", "registry-1.docker.io", "library/nginx")
        && AssistantTools.RegistryImage("ghcr.io/home-operations/jackett") is ("ghcr.io", "ghcr.io", "home-operations/jackett")
        && AssistantTools.RegistryImage("index.docker.io/grafana/grafana") is ("docker.io", "registry-1.docker.io", "grafana/grafana"),
        "query_registry needs an image, and finds Docker Hub's and other registries' hosts and paths.");
    foreach (var bad in new[] { "nginx:1.25", "ghcr.io/a/b@sha256:0", "../etc", "" })
        try { AssistantTools.RegistryImage(bad); Check(false, $"query_registry must refuse the image {bad}."); }
        catch (AssistantException) { }
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
    var efforts = await broker.SaveSettingsAsync(owner, new AssistantSettings([], [], new() { ["local:qwen3"] = ["xhigh", "none", "low", "low"] }), [], default);
    Check(efforts.ModelEfforts!["local:qwen3"].SequenceEqual(["none", "low", "xhigh"]) && (await broker.SettingsAsync(owner, default)).ModelEfforts!["local:qwen3"].Length == 3,
        "The efforts an owner allows for a model are kept once each, cheapest first.");
    foreach (var bad in new Dictionary<string, string[]>[] { new() { ["gpt-5"] = ["low"] }, new() { ["local:q"] = ["turbo"] }, new() { ["litellm:bad id"] = ["low"] } })
        await Reject(() => broker.SaveSettingsAsync(owner, new AssistantSettings([], [], bad), [], default), 400, "invalid_efforts", "Only the owner's own models get efforts, from the known list.");
    var overlaid = Node(await runtime.ModelsAsync(owner, new Dictionary<string, string[]> { ["local:qwen3-spark"] = ["none", "medium"] }, default));
    var qwen = overlaid["models"]!.AsArray().First(model => (string)model!["id"]! == "local:qwen3-spark")!;
    Check(qwen["reasoning"]!.AsArray().Select(item => (string)item!).SequenceEqual(["none", "medium"]) && qwen["defaultReasoning"] is null,
        "An owner's allowed efforts show on their model's card.");
    var gpt = overlaid["models"]!.AsArray().First(model => (string)model!["id"]! == "litellm:gpt-4o")!;
    Check(gpt["reasoning"]!.AsArray().Select(item => (string)item!).SequenceEqual(["low", "medium", "high"]) && (int)gpt["context"]! == 128_000,
        "A LiteLLM model it says can reason gets the standard efforts, and its input limit.");
    await broker.SaveSettingsAsync(owner, new AssistantSettings([], [], new()), [], default);
    Directory.Delete(Path.Combine(directory, "users"), recursive: true);
    var chat = Guid.NewGuid().ToString("N");
    AssistantChatRequest Ask(string text, string messageId = "m1", string mode = "execute", string? model = null, string? session = null) =>
        new(session ?? chat, messageId, text, mode, model, "/apps");

    foreach (var bad in new[] { "", "abc", Guid.NewGuid().ToString("D"), "0123456789ABCDEF0123456789ABCDEF", "..\\" + chat })
        await Reject(() => runs.StartAsync(owner, Ask("Hi", session: bad), "tester via assistant", null, default), 400, "invalid_session", $"Chat id '{bad}' is rejected.");
    await Reject(() => runs.StartAsync(owner, Ask("Hi", mode: "auto"), "tester via assistant", null, default), 400, "invalid_request", "Unknown modes are rejected.");
    await Reject(() => runs.StartAsync(owner, Ask("Hi", messageId: "bad id"), "tester via assistant", null, default), 400, "invalid_request", "Message ids are validated.");
    await Reject(() => runs.StartAsync(owner, Ask("Hi", messageId: null!), "tester via assistant", null, default), 400, "invalid_request", "A missing message id is rejected.");
    await Reject(() => runs.StartAsync(owner, Ask("Hi", model: "gpt 5; rm"), "tester via assistant", null, default), 400, "invalid_request", "Model ids are validated.");
    foreach (var (effort, tier) in new (string?, string?)[] { ("High; rm", null), ("", null), (null, "huge") })
        await Reject(() => runs.StartAsync(owner, Ask("Hi") with { Reasoning = effort, ContextTier = tier }, "tester via assistant", null, default), 400, "invalid_request",
            $"Reasoning effort '{effort}' and context window '{tier}' are validated.");
    await Reject(() => runs.StartAsync(owner, Ask(" \n "), "tester via assistant", null, default), 400, "invalid_message", "Blank messages are rejected.");
    await Reject(() => runs.StartAsync(owner, Ask(new string('x', 32_769)), "tester via assistant", null, default), 400, "invalid_message", "Messages over 32,768 characters are rejected.");
    await Reject(() => runs.StartAsync(owner, Ask("Hi"), "tester via assistant", null, default), 503, "assistant_not_connected", "Until the owner signs in with GitHub, the assistant asks them to.");
    Check(!Directory.Exists(Path.Combine(directory, "users")), "Rejected requests write nothing.");
    var listed = Node(await runtime.ModelsAsync(owner, null, default));
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
    await runtime.ModelsAsync(owner, null, default);
    Check(openAi.Minted == 1, "The saved key is reused.");
    openAi.Revoked.Add("sk-virtual-1");
    await runtime.ModelsAsync(owner, null, default);
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
    sources = Node(await runtime.ModelsAsync(owner, null, default))["sources"]!.AsArray();
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

    // A job's chat runs what the job may do, destructive tools included, and "always" saves the grant to the job.
    List<string> jobSaved = [], jobWaited = [];
    var jobWaits = new AssistantJobWaits();
    var jobGrants = new AssistantJobGrants("Nightly", ["tool:wipe"], grant => { jobSaved.Add(grant); return Task.CompletedTask; },
        tool => { jobWaited.Add(tool); return Task.CompletedTask; }, jobWaits);
    var jobRun = new AssistantRun("m-job", default);
    var jobKit = broker.Kit(new AssistantTurn(owner, govKey, "Hi", null, "execute"), govSettings, govTools, new AssistantStream("m-job", null), jobRun, jobGrants);
    Check(Verdict(await GovCall(jobKit, "wipe", new { app = "grafana" })) == "approve"
        && (string)GovNew(jobRun)[2]["reason"]! == "This job may do this without asking.", "A job runs the destructive tools it was granted.");
    var jobAsk = GovCall(jobKit, "run_command", new { node = "lucialab02", command = "id" });
    govChunks = GovNew(jobRun);
    Check(!jobAsk.IsCompleted && Types(govChunks) == "tool-input-available,tool-approval-request" && jobWaited.SequenceEqual(["run_command"])
        && jobWaits.Open.Count == 1, "Anything else waits for the owner, who is told about it.");
    Check(runs.Respond(owner, chat, (string)govChunks[1]["approvalId"]!, true, null, always: true) && Verdict(await jobAsk) == "approve"
        && jobSaved.SequenceEqual(["tool:run_command"]) && jobGrants.Has("tool:run_command") && jobWaits.Open.IsEmpty && jobWaits.Expired == 0,
        "'Always' in a job's chat saves to the job, destructive tools too.");
    Check(Verdict(await GovCall(jobKit, "run_command", new { node = "lucialab02", command = "id" })) == "approve", "The job then runs it without asking.");
    var (_, plainRun, plainKit) = GovTurn();
    var plainWipe = GovCall(plainKit, "wipe", new { app = "grafana" });
    Check(!plainWipe.IsCompleted, "A job's grants don't reach other chats.");
    plainRun.Stop.Cancel();
    await plainWipe;

    // Web Push: RFC 8291's own example encrypts to the same bytes.
    static byte[] B64(string text) => Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(text);
    var pushPublic = B64("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8");
    using (var pushServer = System.Security.Cryptography.ECDiffieHellman.Create(new System.Security.Cryptography.ECParameters
    {
        Curve = System.Security.Cryptography.ECCurve.NamedCurves.nistP256, D = B64("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw"),
        Q = new() { X = pushPublic[1..33], Y = pushPublic[33..65] },
    }))
        Check(AssistantPush.Encrypt(B64("V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24"),
            B64("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4"), B64("BTBZMqHH6r4Tts7J_aSIgg"), pushServer,
            B64("DGv6ra1nlYgDCS1FRnbzlw")).SequenceEqual(B64("DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8")
                .Concat(B64("8pfeW0KbunFT06SuDKoJH9Ql87S1QUrdirN6GcG7sFz1y1sqLgVi1VhjVkHsUoEsbI_0LpXMuGvnzQ"))), "Push messages match RFC 8291's example.");
    var push = new AssistantPush(options, new EphemeralDataProtectionProvider(), NullLogger<AssistantPush>.Instance, new HttpClient());
    Check(B64(push.PublicKey()) is { Length: 65 } && push.PublicKey() == push.PublicKey(), "The VAPID key is a P-256 public key, kept.");
    var pushKeys = new PushKeys("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4", "BTBZMqHH6r4Tts7J_aSIgg");
    await Reject(() => push.AddAsync(owner, new("Laptop", "https://169.254.169.254/push", pushKeys), "https://lucia.example", default), 400, "invalid_device",
        "Only known push services are notified, so a device can't aim Lucia's requests elsewhere.");
    await Reject(() => push.AddAsync(owner, new("Laptop", "http://fcm.googleapis.com/fcm/send/x", pushKeys), "https://lucia.example", default), 400,
        "invalid_device", "Push services are reached over HTTPS.");
    await Reject(() => push.AddAsync(owner, new("Laptop", "https://fcm.googleapis.com/fcm/send/x", new PushKeys("AAAA", "BTBZMqHH6r4Tts7J_aSIgg")),
        "https://lucia.example", default), 400, "invalid_device", "Bad push keys are refused.");
    var device = await push.AddAsync(owner, new("Laptop", "https://fcm.googleapis.com/fcm/send/x", pushKeys), "https://lucia.example", default);
    var again = await push.AddAsync(owner, new("Phone", "https://fcm.googleapis.com/fcm/send/x", pushKeys), "https://lucia.example", default);
    Check(again.Id == device.Id && (await push.DevicesAsync(owner, default)) is [{ Name: "Phone" }] && (await push.DevicesAsync(stranger, default)).Count == 0,
        "Registering a browser again updates it, and devices are the owner's own.");
    await push.RemoveAsync(owner, device.Id, default);
    Check((await push.DevicesAsync(owner, default)).Count == 0, "A device can be removed.");

    // Jobs: saved per owner, validated, and scheduled by cron in the job's time zone.
    var jobs = new AssistantJobs(options, runs, new StoppedLifetime(), NullLogger<AssistantJobs>.Instance);
    AssistantJobRequest JobRequest(string cron = "0 7 * * *", string[]? tools = null, string zone = "America/Chicago") =>
        new("Nightly triage", ["Check the apps.", "Fix what you can."], cron, zone, null, true, tools, ["docs.docker.com"]);
    await Reject(() => jobs.SaveAsync(owner, null, JobRequest("every day"), "tester via assistant", "tester", default), 400, "invalid_job", "Schedules are cron.");
    await Reject(() => jobs.SaveAsync(owner, null, JobRequest(zone: "Mars/Olympus"), "tester via assistant", "tester", default), 400, "invalid_job",
        "Time zones are real.");
    await Reject(() => jobs.SaveAsync(owner, null, JobRequest(tools: ["shell"]), "tester via assistant", "tester", default), 400, "invalid_job",
        "Jobs may only be granted the assistant's own change tools.");
    await Reject(() => jobs.SaveAsync(owner, null, JobRequest() with { Prompts = [] }, "tester via assistant", "tester", default), 400, "invalid_job",
        "A job needs a prompt.");
    foreach (var (effort, tier) in new (string?, string?)[] { ("", null), ("High; rm", null), (null, "huge"), (null, "") })
        await Reject(() => jobs.SaveAsync(owner, null, JobRequest() with { Reasoning = effort, ContextTier = tier }, "tester via assistant", "tester", default),
            400, "invalid_job", $"A job's reasoning effort '{effort}' and context window '{tier}' must be well formed.");
    var tuned = JsonSerializer.SerializeToNode(await jobs.SaveAsync(owner, null, JobRequest() with { Reasoning = "low", ContextTier = "long_context" },
        "tester via assistant", "tester", default), AssistantStream.Json)!;
    Check((string)tuned["reasoning"]! == "low" && (string)tuned["contextTier"]! == "long_context",
        "A job keeps the reasoning effort and context window it was saved with.");
    await jobs.DeleteAsync(owner, (string)tuned["id"]!, default);
    var savedJob = JsonSerializer.SerializeToNode(await jobs.SaveAsync(owner, null, JobRequest(), "tester via assistant", "tester", default), AssistantStream.Json)!;
    var jobId = (string)savedJob["id"]!;
    Check(savedJob["next"] is not null && !(bool)savedJob["running"]! && (string)savedJob["timeZone"]! == "America/Chicago"
        && JsonSerializer.SerializeToNode(await jobs.ListAsync(stranger, default))!.AsArray().Count == 0, "A saved job shows its next run, and only to its owner.");
    var nextRuns = AssistantJobs.Next(new("0 7 * * *", "America/Chicago"), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    Check(nextRuns.Count == 3 && nextRuns[0] == new DateTimeOffset(2026, 1, 1, 13, 0, 0, TimeSpan.Zero), "7am in Chicago is 13:00 UTC in winter.");
    await jobs.TickAsync(new DateTimeOffset(2026, 1, 1, 13, 0, 30, TimeSpan.Zero), new DateTimeOffset(2026, 1, 1, 13, 1, 0, TimeSpan.Zero), default);
    Check((await jobs.RunsAsync(owner, jobId, default)).Count == 0, "A job that isn't due doesn't run.");
    File.WriteAllText(Path.Combine(directory, "users", owner, "jobs", jobId + ".runs.json"), JsonSerializer.Serialize(
        new[] { new AssistantJobRun("r1", chat, "schedule", DateTimeOffset.UtcNow, null, "running", null) }, AssistantStream.Json));
    Check((await jobs.RunsAsync(owner, jobId, default)) is [{ Status: "failed", Error: "Lucia restarted during this run." }],
        "A run Lucia lost track of shows as failed.");
    await Reject(() => jobs.GetAsync(owner, "../../etc", default), 404, "job_not_found", "Job ids are only ever job ids.");
    await jobs.DeleteAsync(owner, jobId, default);
    Check(JsonSerializer.SerializeToNode(await jobs.ListAsync(owner, default))!.AsArray().Count == 0, "A job can be deleted.");

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
    // The Spark isn't a managed node, so its health and updates have tools of their own.
    var sparkBuffer = new Lucia.Homelab.Server.Telemetry.SparkTelemetryBuffer(TimeProvider.System);
    sparkBuffer.Add(new(DateTimeOffset.UtcNow, 42.26, 20, 3.456, 128L << 30, 100L << 30, 4000L << 30, 3000L << 30, "enP7s7", 1_250_000, 125_000,
        "NVIDIA GB10", 12.5, 51.2, 33.3, true, 7200, []));
    var sparkTools = new AssistantTools(null!, labStacks, null!, null!, null!, null!, broker, NullLogger<AssistantTools>.Instance, null, null, sparkBuffer,
        new Lucia.Homelab.Server.Telemetry.SparkTelemetryOptions { Enabled = true }).Create("tester via assistant", "tester");
    var sparkHealth = JsonNode.Parse(((TextContent)(await sparkTools.Single(tool => tool.Name == "get_spark_health").InvokeAsync(new AIFunctionArguments()))!).Text)!;
    Check((string)sparkHealth["state"]! == "Healthy" && (double)sparkHealth["cpu"]!["percent"]! == 42.3 && (double)sparkHealth["memoryGiB"]!["available"]! == 100
        && (string)sparkHealth["gpu"]!["name"]! == "NVIDIA GB10" && (double)sparkHealth["network"]!["receiveMbps"]! == 10 && (double)sparkHealth["uptimeHours"]! == 2
        && (int)sparkHealth["lastHour"]!["samples"]! == 1 && ToolSchema("get_spark_health")["required"] is null && ToolSchema("get_spark_updates")["required"] is null
        && toolSet.Single(tool => tool.Name == "get_spark_updates").Tier == ToolTier.Read && toolSet.Single(tool => tool.Name == "check_spark_updates").Tier == ToolTier.Change
        && toolSet.Single(tool => tool.Name == "list_nodes").Description.Contains("get_spark_health"),
        "The Spark's health reads as rounded host readings, its updates are a read tool and checking them a change, and list_nodes points to them.");
    var sparkMissing = await Reject(() => sparkTools.Single(tool => tool.Name == "get_spark_updates").InvokeAsync(new AIFunctionArguments()).AsTask(), 400, "tool_error",
        "Spark updates say so when the update service isn't there.");
    Check(sparkMissing.Message == "Spark updates aren't available.", "The assistant is told plainly that Spark updates aren't available.");
    Check(AssistantTools.Unknown("spark").Message.Contains("install_spark_updates") && AssistantTools.Unknown("lucialab9").Message.Contains("list_nodes")
        && toolSet.Single(tool => tool.Name == "install_spark_updates").Tier == ToolTier.Destructive,
        "Asking a node tool for the Spark points to the Spark's own tools, and installing its updates is a risky tool.");
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

    // Skills: the store keeps packages safely, the Agent Framework loads them, and each turn folds the enabled ones into the kit.
    var skillOptions = Options.Create(new AssistantOptions { Directory = Path.Combine(storage, "skill-data") });
    var skillStore = new SkillStore(skillOptions, NullLogger<SkillStore>.Instance, null, Path.Combine(storage, "no-built-in-skills"));
    var skillRouter = new SkillScriptRouter(skillStore, new NodeSkillRunner(new NodeExec(null!, null!), skillStore), new SparkSkillRunner(skillOptions, skillStore),
    new SandboxSkillRunner(new NodeExec(null!, null!), skillStore, new SkillSandboxRuntime()));
    using var skillProvider = new AssistantSkills(skillStore, skillRouter, NullLoggerFactory.Instance);
    foreach (var bad in new[] { "../x.py", "/etc/passwd", "scripts\\x.py", "other/x.py", "scripts/x.exe", "scripts/../x.py", "SKILL.md", "references/run.sh",
        "scripts", "scripts/.hidden.py", "scripts//x.py", "C:/x.py", "scripts/a b.py", "" })
        await Reject(() => Task.FromResult(SkillStore.Normalize(bad)), 400, "invalid_path", $"'{bad}' isn't a path a skill accepts.");
    Check(SkillStore.Normalize("scripts/check.py") == "scripts/check.py" && SkillStore.Normalize("references/notes.md") == "references/notes.md"
        && SkillStore.Normalize("assets/data/table.csv") == "assets/data/table.csv", "Scripts, references and assets are accepted.");
    Check(!SkillStore.ValidName("Bad_Name") && !SkillStore.ValidName("-a") && !SkillStore.ValidName("a--b") && !SkillStore.ValidName(new string('a', 65))
        && SkillStore.ValidName("check-zfs-health"), "Skill names are agentskills.io names.");

    var alpha = await skillStore.CreateAsync(new("alpha", "Checks the alpha service.", "node", "read", null, null), default);
    Check(alpha is { Enabled: false, Execution: "node", Tier: "read", Source: "created", Problem: null, Scripts: 0 } && alpha.Hash.Length == 64
        && alpha.Body.Contains("# alpha"), "A new skill starts disabled and loads through the Agent Framework.");
    await Reject(() => skillStore.CreateAsync(new("alpha", "Again.", "node", "read", null, null), default), 409, "skill_exists", "A skill name is unique.");
    await Reject(() => skillStore.CreateAsync(new("beta", "Bad tier.", "node", "root", null, null), default), 400, "invalid_skill", "A tier is read, change or destructive.");
    await Reject(() => skillStore.CreateAsync(new("beta", "Bad mode.", "cloud", "read", null, null), default), 400, "invalid_skill", "An execution mode is node, spark or sandbox.");
    await Reject(() => skillStore.CreateAsync(new("beta", "Bad domain.", "sandbox", "read", ["not a domain"], null), default), 400, "invalid_domain", "Allowed domains are host names.");
    var markdownPath = Path.Combine(skillStore.Root, "alpha", "SKILL.md");
    var markdownText = await File.ReadAllTextAsync(markdownPath);
    Check(markdownText.Contains("lucia.execution: node") && markdownText.Contains("lucia.tier: read") && markdownText.StartsWith("---"),
        "Lucia's settings are written in the frontmatter metadata map.");
    var sandboxed = await skillStore.CreateAsync(new("boxed", "Runs in the sandbox.", "sandbox", "destructive", ["API.Example.com", "*.example.org"], "# Boxed\n"), default);
    var boxedSettings = skillStore.SettingsOf("boxed");
    Check(sandboxed.Execution == "sandbox" && sandboxed.Tier == "destructive" && sandboxed.AllowedDomains.SequenceEqual(["api.example.com", "*.example.org"])
        && boxedSettings.Execution == "sandbox" && boxedSettings.Tier == "destructive" && boxedSettings.AllowedDomains.SequenceEqual(["api.example.com", "*.example.org"]),
        "Execution, tier and allowed domains round-trip through the frontmatter.");
    Check(SkillSettings.From(new Dictionary<string, object?> { ["lucia.execution"] = "bogus", ["lucia.tier"] = "bogus" }) is { Execution: "node", Tier: "change", AllowedDomains: [] }
        && SkillSettings.From(null) is { Execution: "node", Tier: "change" }, "Unknown or missing settings fall back to node and change.");

    var hashBefore = alpha.Hash;
    var script = await skillStore.WriteFileAsync("alpha", "scripts/check.py", "print('hi')\n", default);
    var hashAfter = (await skillStore.GetAsync("alpha", default)).Hash;
    Check(script is { Kind: "script", Language: "python", Editable: true } && hashAfter != hashBefore && hashAfter.Length == 64, "Writing a script re-hashes the skill.");
    await skillStore.WriteFileAsync("alpha", "scripts/check.py", "print('hello')\n", default);
    Check((await skillStore.GetAsync("alpha", default)).Hash != hashAfter && skillStore.ReadFile("alpha", "scripts/check.py").Content == "print('hello')\n",
        "Editing a script changes the hash and the content is read back.");
    var updated = await skillStore.UpdateAsync("alpha", new("Checks alpha.", "node", "change", [], "# New body\n"), default);
    Check(updated.Hash != hashAfter && updated.Tier == "change" && updated.Description == "Checks alpha." && updated.Body.StartsWith("# New body"), "Saving the skill rewrites its frontmatter and body.");

    await skillStore.WriteFileAsync("alpha", "scripts/run.sh", "echo hi\n", default);
    await Reject(() => skillStore.UpdateAsync("alpha", new("Checks alpha.", "sandbox", "change", [], "# Body\n"), default), 409, "sandbox_languages",
        "A skill with a shell script can't move to the sandbox.");
    await skillStore.WriteFileAsync("boxed", "scripts/ok.js", "console.log(1)\n", default);
    await skillStore.WriteFileAsync("boxed", "scripts/ok.py", "print(1)\n", default);
    foreach (var offending in new[] { "scripts/x.sh", "scripts/x.ts", "scripts/x.cs" })
        await Reject(() => skillStore.WriteFileAsync("boxed", offending, "x", default), 409, "sandbox_languages", $"A sandboxed skill refuses {offending}.");
    await Reject(() => skillStore.RenameFileAsync("boxed", "scripts/ok.py", "scripts/ok.sh", default), 409, "sandbox_languages", "Renaming can't dodge the language lock.");
    await skillStore.WriteFileAsync("boxed", "references/guide.md", "# Guide\n", default);
    await skillStore.DeleteFileAsync("alpha", "scripts/run.sh", default);

    await Reject(() => skillStore.WriteFileAsync("alpha", "scripts/big.py", new string('x', 1024 * 1024 + 1), default), 413, "file_too_large", "Files are at most 1 MiB.");
    await Reject(() => skillStore.WriteFileAsync("alpha", "scripts/../../evil.py", "x", default), 400, "invalid_path", "A write can't climb out of the skill.");
    await Reject(() => skillStore.WriteFileAsync("alpha", "SKILL.md", "x", default), 400, "invalid_path", "SKILL.md is written through the skill, not as a file.");
    await skillStore.CreateAsync(new("many", "Many files.", "node", "read", null, null), default);
    for (var i = 0; i < 200; i++) await skillStore.WriteFileAsync("many", $"references/f{i}.md", "x", default);
    await Reject(() => skillStore.WriteFileAsync("many", "references/one-more.md", "x", default), 409, "too_many_files", "A skill holds at most 200 files.");
    await skillStore.CreateAsync(new("heavy", "Heavy files.", "node", "read", null, null), default);
    for (var i = 0; i < 4; i++) await skillStore.WriteFileAsync("heavy", $"references/b{i}.md", new string('x', 1024 * 1024), default);
    await Reject(() => skillStore.WriteFileAsync("heavy", "references/b4.md", new string('x', 1024 * 1024), default), 413, "skill_too_large", "A skill is at most 5 MiB.");
    await skillStore.DeleteAsync("many", default);
    await skillStore.DeleteAsync("heavy", default);

    try
    {
        var outside = Path.Combine(storage, "skill-outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(skillStore.Root, "alpha", "references", "link"), outside);
        var linked = await skillStore.GetAsync("alpha", default);
        Check(linked.Problem is { } linkProblem && linkProblem.Contains("link") && (await skillStore.OfferedAsync(default)).Count == 0,
            "A skill containing a link is never offered.");
        Directory.Delete(Path.Combine(skillStore.Root, "alpha", "references", "link"));
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }

    // Zip import: zip-slip is refused, and a good package is unpacked as a disabled skill.
    static byte[] Zip(params (string Path, string Text)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, text) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write(text);
            }
        return stream.ToArray();
    }
    const string zipSkill = "---\nname: from-zip\ndescription: Imported from a zip.\nmetadata:\n  lucia.execution: sandbox\n---\n\n# Zip\n";
    foreach (var evil in new[] { "../evil.py", "from-zip/../../evil.py", "/abs/evil.py", "from-zip\\evil.py", "C:/evil.py" })
        await Reject(() => skillStore.ImportZipAsync(new MemoryStream(Zip(("from-zip/SKILL.md", zipSkill), (evil, "x"))), default), 400, "invalid_path",
            $"A zip entry '{evil}' can't write outside the skill.");
    await Reject(() => skillStore.ImportZipAsync(new MemoryStream([1, 2, 3]), default), 400, "invalid_zip", "A non-zip is refused.");
    await Reject(() => skillStore.ImportZipAsync(new MemoryStream(Zip(("from-zip/README.md", "x"))), default), 400, "invalid_skill", "A package needs a SKILL.md.");
    await Reject(() => skillStore.ImportZipAsync(new MemoryStream(Zip(("from-zip/SKILL.md", zipSkill), ("from-zip/scripts/a.sh", "echo"))), default), 409, "sandbox_languages",
        "A sandboxed package can't carry a shell script.");
    var imported = await skillStore.ImportZipAsync(new MemoryStream(Zip(("from-zip/SKILL.md", zipSkill), ("from-zip/scripts/a.py", "print(1)"),
        ("from-zip/references/r.md", "# R"), ("from-zip/LICENSE", "MIT"), ("from-zip/docs/x.bin", "x"))), default);
    Check(imported is { Name: "from-zip", Enabled: false, Source: "zip", Problem: null, Scripts: 1 } && imported.Files.Length == 2 && !File.Exists(Path.Combine(skillStore.Root, "evil.py")),
        "A good zip is unpacked disabled, skipping files Lucia doesn't keep.");
    await Reject(() => skillStore.ImportZipAsync(new MemoryStream(Zip(("SKILL.md", zipSkill))), default), 409, "skill_exists", "An import can't replace a skill.");
    using (var export = new MemoryStream())
    {
        await skillStore.ExportAsync("from-zip", export, default);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(export.ToArray()));
        Check(archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).SequenceEqual(["from-zip/SKILL.md", "from-zip/references/r.md", "from-zip/scripts/a.py"]), "An export holds the skill under its name.");
    }
    var rootZip = await skillStore.ImportZipAsync(new MemoryStream(Zip(("SKILL.md", zipSkill.Replace("from-zip", "root-zip").Replace("sandbox", "node")))), default);
    Check(rootZip.Name == "root-zip", "A zip may hold the skill at its top.");
    await Reject(() => skillStore.ImportZipAsync(new MemoryStream(new byte[6 * 1024 * 1024]), default), 413, "zip_too_large", "Zips are at most 5 MiB.");
    await skillStore.DeleteAsync("root-zip", default);
    await skillStore.DeleteAsync("from-zip", default);

    // A broken SKILL.md is reported as a problem and never offered.
    Directory.CreateDirectory(Path.Combine(skillStore.Root, "broken"));
    await File.WriteAllTextAsync(Path.Combine(skillStore.Root, "broken", "SKILL.md"), "no frontmatter here");
    var brokenSummary = (await skillStore.ListAsync(default)).Single(skill => skill.Name == "broken");
    Check(brokenSummary.Problem is not null && brokenSummary.Description == "", "The Agent Framework's refusal shows as the skill's problem.");
    await Reject(() => skillStore.SetEnabledAsync("broken", true, default), 409, "skill_invalid", "A skill with a problem can't be enabled.");
    Directory.Delete(Path.Combine(skillStore.Root, "broken"), recursive: true);

    // Context folding: only enabled skills reach the turn, as the provider's instructions and tools.
    Task<string> NoRedact(string text, CancellationToken _) => Task.FromResult(text);
    var emptyContext = await skillProvider.ContextAsync(null, NoRedact, NullLogger.Instance, default);
    Check(emptyContext.Instructions == "" && emptyContext.Tools.Select(tool => tool.Name).SequenceEqual(["refresh_skills"]),
        "With nothing enabled a turn gets only refresh_skills.");
    await skillStore.SetEnabledAsync("alpha", true, default);
    await skillStore.WriteFileAsync("boxed", "references/more.md", "x", default);
    var context = await skillProvider.ContextAsync(null, NoRedact, NullLogger.Instance, default);
    Check(context.Instructions.Contains("alpha") && context.Instructions.Contains("Checks alpha.") && !context.Instructions.Contains("boxed")
        && context.Tools.Select(tool => tool.Name).Order().SequenceEqual(["load_skill", "read_skill_resource", "refresh_skills", "run_skill_script"]),
        "An enabled skill is listed with its description, with the framework's three tools and refresh_skills; a disabled one isn't there.");
    Check(context.Instructions.Contains("\"node\"") && context.Tools.All(tool => tool.Tier == (tool.Name == "run_skill_script" ? ToolTier.Change : ToolTier.Read)),
        "The prompt says node-mode scripts need a node argument, and only the script tool is above read.");

    await skillStore.SetEnabledAsync("boxed", true, default);
    var everything = await skillProvider.ContextAsync(["*"], NoRedact, NullLogger.Instance, default);
    var selected = await skillProvider.ContextAsync(["boxed", "missing"], NoRedact, NullLogger.Instance, default);
    var nothing = await skillProvider.ContextAsync([], NoRedact, NullLogger.Instance, default);
    Check(everything.Instructions.Contains("alpha") && everything.Instructions.Contains("boxed") && selected.Instructions.Contains("boxed") && !selected.Instructions.Contains("alpha")
        && nothing.Tools.Count == 0 && nothing.Instructions == "", "A job's skills filter what a turn is offered: all enabled, the named ones, or none.");
    await skillStore.SetEnabledAsync("boxed", false, default);
    Check(!(await skillProvider.ContextAsync(["*"], NoRedact, NullLogger.Instance, default)).Instructions.Contains("boxed"), "A job is never offered a disabled skill, even by name.");

    // The tools run through the framework with Lucia's runner: a Spark skill goes to the Spark runner, a sandbox skill goes to the sandbox runner.
    var sparky = await skillStore.CreateAsync(new("sparky", "Pinned to the Spark.", "spark", "read", null, null), default);
    await skillStore.WriteFileAsync("sparky", "scripts/go.py", "print(1)\n", default);
    await skillStore.SetEnabledAsync("sparky", true, default);
    var sparkContext = await skillProvider.ContextAsync(null, NoRedact, NullLogger.Instance, default);
    var runTool = sparkContext.Tools.First(tool => tool.Name == "run_skill_script");
    var sparkRun = (await runTool.InvokeAsync(new AIFunctionArguments { ["skillName"] = "sparky", ["scriptName"] = "scripts/go.py" }, default))?.ToString();
    Check(sparkRun is not null && sparkRun.Contains("skill runner service isn't running"), "A Spark-mode script says so when the Spark's runner service isn't answering: " + sparkRun);
    var loaded = (await sparkContext.Tools.First(tool => tool.Name == "load_skill").InvokeAsync(new AIFunctionArguments { ["skillName"] = "sparky" }, default))?.ToString();
    Check(loaded is not null && loaded.Contains("Pinned to the Spark."), "load_skill reads the skill through the framework.");
    // A skill added during the turn can be loaded by the live tool without waiting for the next message.
    await skillStore.CreateAsync(new("late", "Added mid-turn.", "node", "read", null, null), default);
    await skillStore.SetEnabledAsync("late", true, default);
    var lateLoaded = (await sparkContext.Tools.First(tool => tool.Name == "load_skill").InvokeAsync(new AIFunctionArguments { ["skillName"] = "late" }, default))?.ToString();
    Check(lateLoaded is not null && lateLoaded.Contains("Added mid-turn."), "load_skill sees a skill enabled after the turn began.");
    var refreshed = JsonSerializer.Serialize(await sparkContext.Tools.First(tool => tool.Name == "refresh_skills").InvokeAsync(new AIFunctionArguments(), default), AssistantStream.Json);
    Check(refreshed.Contains("late") && refreshed.Contains("added", StringComparison.OrdinalIgnoreCase), "refresh_skills reports what was added: " + refreshed);

    // Built-in skills ship with Lucia: always offered to chats and jobs, read-only, with their names reserved.
    var builtInRoot = Path.Combine(AppContext.BaseDirectory, "Assistant", "Skills", "BuiltIn");
    var sourceRoot = AppContext.BaseDirectory;
    while (!File.Exists(Path.Combine(sourceRoot, "src", "Lucia.Homelab.Server", "Lucia.Homelab.Server.csproj")) && Path.GetDirectoryName(sourceRoot) is { } up) sourceRoot = up;
    var serverProject = Path.Combine(sourceRoot, "src", "Lucia.Homelab.Server");
    if (!Directory.Exists(builtInRoot)) builtInRoot = Path.Combine(serverProject, "Assistant", "Skills", "BuiltIn");
    var builtOptions = Options.Create(new AssistantOptions { Directory = Path.Combine(storage, "builtin-data") });
    var builtStore = new SkillStore(builtOptions, NullLogger<SkillStore>.Instance, null, builtInRoot);
    var builtRouter = new SkillScriptRouter(builtStore, new NodeSkillRunner(new NodeExec(null!, null!), builtStore), new SparkSkillRunner(builtOptions, builtStore),
        new SandboxSkillRunner(new NodeExec(null!, null!), builtStore, new SkillSandboxRuntime()));
    using var builtProvider = new AssistantSkills(builtStore, builtRouter, NullLoggerFactory.Instance);
    string[] builtNames = ["app-troubleshooting", "container-image-updates", "custom-app-authoring", "os-security-patching"];
    var builtList = await builtStore.ListAsync(default);
    Check(builtList.Select(skill => skill.Name).SequenceEqual(builtNames) && builtList.All(skill => skill is { BuiltIn: true, Enabled: true, Execution: "node", Tier: "read", Source: "built-in", Problem: null, Scripts: 0 }
        && skill.Hash.Length == 64 && skill.Description.Length > 0), "Built-in skills are listed, enabled and read-only, with their source and tier.");
    Check(builtList.All(skill => builtStore.HashOf(skill.Name) == skill.Hash) && builtStore.IsBuiltIn("app-troubleshooting") && !builtStore.IsBuiltIn("alpha"),
        "A built-in skill's hash is computed like any other.");
    foreach (var (label, selection) in new (string, string[]?)[] { ("A chat", null), ("A job with all skills", ["*"]), ("A job with no skills", []), ("A job with others", ["unrelated"]) })
    {
        var builtContext = await builtProvider.ContextAsync(selection, NoRedact, NullLogger.Instance, default);
        Check(builtNames.All(name => builtContext.Instructions.Contains(name)) && builtContext.Tools.Any(tool => tool.Name == "load_skill"), $"{label} is offered the built-in skills.");
    }
    var builtTools = (await builtProvider.ContextAsync(null, NoRedact, NullLogger.Instance, default)).Tools;
    var imageGuide = (await builtTools.First(tool => tool.Name == "load_skill").InvokeAsync(new AIFunctionArguments { ["skillName"] = "container-image-updates" }, default))?.ToString();
    Check(imageGuide is not null && imageGuide.Contains("query_registry") && imageGuide.Contains("list_node_images"), "load_skill reads a built-in skill through the framework.");
    var identity = AssistantRuntime.Identity;
    string[] movedPhrases = ["Registry lookups", "get_spark_updates"];
    // These stay in Identity on purpose: they guard against regressions even when no skill is loaded.
    string[] keptPhrases = ["query_registry", "list_node_images", "generate"];
    var guides = string.Join("\n", await Task.WhenAll(builtNames.Select(async name => (await builtStore.GetAsync(name, default)).Body)));
    Check(movedPhrases.All(phrase => !identity.Contains(phrase)) && movedPhrases.All(phrase => guides.Contains(phrase))
        && keptPhrases.All(phrase => identity.Contains(phrase) && guides.Contains(phrase)) && identity.Length < 2200,
        $"Tool guidance moved from the system prompt into built-in skills, keeping the registry and generate rules (Identity is {identity.Length} characters).");
    Check(identity.Contains("never instructions") && identity.Contains("ask_owner") && identity.Contains("Plan mode") && identity.Contains("request_secret") && identity.Contains("approve"),
        "The system prompt keeps its safety rules.");

    foreach (var reserved in builtNames)
        await Reject(() => builtStore.CreateAsync(new(reserved, "Mine.", "node", "read", null, null), default), 409, "skill_reserved", $"{reserved} can't be created by the owner.");
    await Reject(() => builtStore.ImportZipAsync(new MemoryStream(Zip(("SKILL.md", "---\nname: os-security-patching\ndescription: Mine.\n---\n\n# Mine\n"))), default), 409, "skill_reserved",
        "An import can't take a built-in name.");
    await Reject(() => builtStore.UpdateAsync("app-troubleshooting", new("Mine.", "node", "read", [], "# Mine\n"), default), 409, "skill_builtin", "A built-in skill can't be edited.");
    await Reject(() => builtStore.SetEnabledAsync("app-troubleshooting", false, default), 409, "skill_builtin", "A built-in skill can't be disabled.");
    await Reject(() => builtStore.DeleteAsync("app-troubleshooting", default), 409, "skill_builtin", "A built-in skill can't be deleted.");
    await Reject(() => builtStore.WriteFileAsync("app-troubleshooting", "references/x.md", "x", default), 409, "skill_builtin", "A built-in skill takes no new files.");
    await Reject(() => builtStore.DeleteFileAsync("app-troubleshooting", "references/x.md", default), 409, "skill_builtin", "A built-in skill's files can't be deleted.");
    await Reject(() => builtStore.RenameFileAsync("app-troubleshooting", "references/x.md", "references/y.md", default), 409, "skill_builtin", "A built-in skill's files can't be renamed.");
    var builtDetail = await builtStore.GetAsync("app-troubleshooting", default);
    Check(builtDetail is { BuiltIn: true, Enabled: true } && builtDetail.Body.Contains("read_logs"), "A built-in skill's detail is readable.");
    using (var builtExport = new MemoryStream())
    {
        await builtStore.ExportAsync("app-troubleshooting", builtExport, default);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(builtExport.ToArray()));
        Check(archive.Entries.Any(entry => entry.FullName == "app-troubleshooting/SKILL.md"), "A built-in skill can be exported.");
    }

    var copy = await builtStore.DuplicateAsync("os-security-patching", "my-patching", default);
    Check(copy is { Name: "my-patching", BuiltIn: false, Enabled: false, Execution: "node", Tier: "read", Problem: null } && copy.Source == "copy of os-security-patching"
        && copy.Body == (await builtStore.GetAsync("os-security-patching", default)).Body && copy.Description == builtList.Single(skill => skill.Name == "os-security-patching").Description
        && File.Exists(Path.Combine(builtStore.Root, "my-patching", "SKILL.md")) && !(await builtStore.OfferedAsync(default)).Contains("my-patching"),
        "Duplicating a built-in skill makes a disabled owner copy.");
    await builtStore.UpdateAsync("my-patching", new("Mine.", "node", "read", [], "# Mine\n"), default);
    await builtStore.SetEnabledAsync("my-patching", true, default);
    var copyOfCopy = await builtStore.DuplicateAsync("my-patching", "my-patching-two", default);
    Check(copyOfCopy is { Enabled: false, BuiltIn: false, Body: "# Mine\n" } && (await builtStore.ListAsync(default)).Select(skill => skill.Name).Take(4).SequenceEqual(builtNames),
        "The owner's copy is editable and can be duplicated too, and the list keeps built-ins first.");
    await Reject(() => builtStore.DuplicateAsync("os-security-patching", "app-troubleshooting", default), 409, "skill_reserved", "A copy can't take a built-in name.");
    await Reject(() => builtStore.DuplicateAsync("os-security-patching", "my-patching", default), 409, "skill_exists", "A copy can't replace a skill.");
    await Reject(() => builtStore.DuplicateAsync("os-security-patching", "Bad Name", default), 400, "invalid_skill", "A copy needs a valid name.");
    await Reject(() => builtStore.DuplicateAsync("missing-skill", "copy-of-missing", default), 404, "skill_not_found", "A missing skill can't be duplicated.");
    var builtManifest = await new SkillPackages(builtStore).ManifestAsync(default);
    Check(builtManifest.Skills.Select(entry => entry.Name).SequenceEqual(["my-patching"]), "Nodes are sent the owner's skills, not the built-in ones.");
    Directory.CreateDirectory(Path.Combine(builtStore.Root, "app-troubleshooting"));
    await File.WriteAllTextAsync(Path.Combine(builtStore.Root, "app-troubleshooting", "SKILL.md"), "---\nname: app-troubleshooting\ndescription: Impostor.\n---\n\n# Impostor\n");
    Check((await builtStore.ListAsync(default)).Count(skill => skill.Name == "app-troubleshooting") == 1 && (await builtStore.GetAsync("app-troubleshooting", default)).Body.Contains("read_logs"),
        "A folder in the owner's store can't shadow a built-in skill.");
    Directory.Delete(Path.Combine(builtStore.Root, "app-troubleshooting"), recursive: true);
    Check(File.ReadAllText(Path.Combine(serverProject, "Lucia.Homelab.Server.csproj")).Contains(@"Assistant\Skills\BuiltIn\**\*.md")
        && File.ReadAllText(Path.Combine(serverProject, "Lucia.Homelab.Server.csproj")).Contains("CopyToPublishDirectory"), "The built-in skills are copied to the publish output.");

    // Router: the mode and the extension decide where a script may go.
    Check(SkillScriptRouter.Refuse("node", "scripts/a.py") is null && SkillScriptRouter.Refuse("node", "scripts/a.cs") is null
        && SkillScriptRouter.Refuse("node", "references/a.md")!.Contains("isn't a script")
        && SkillScriptRouter.Refuse("sandbox", "scripts/a.sh")!.Contains("JavaScript and Python only")
        && SkillScriptRouter.Refuse("sandbox", "scripts/a.py") is null && SkillScriptRouter.Refuse("sandbox", "scripts/a.mjs") is null
        && SkillScriptRouter.Refuse("spark", "scripts/a.py") is null && SkillScriptRouter.Refuse("spark", "scripts/a.exe")!.Contains("isn't a script")
        && SkillScriptRouter.Refuse("nowhere", "scripts/a.py")!.Contains("unknown execution mode"), "The router checks the mode and extension of every script.");
    // Spark mode: a request names a skill, its hash and a script, never a path, flag, image or command. A stand-in for the Spark's
    // worker answers, so the file protocol is exercised end to end.
    var sparkFolder = Path.Combine(storage, "skill-data", "skill-runner");
    var sparkRunner = new SparkSkillRunner(skillOptions, skillStore);
    Directory.CreateDirectory(Path.Combine(sparkFolder, "requests"));
    Directory.CreateDirectory(Path.Combine(sparkFolder, "responses"));
    void Heartbeat(string image, bool ready = true, DateTimeOffset? at = null, string? note = null) => File.WriteAllText(Path.Combine(sparkFolder, "worker.json"),
        JsonSerializer.Serialize(new { schemaVersion = 1, ready, image, message = note, busy = false, checkedAt = at ?? DateTimeOffset.UtcNow }));
    Check((await sparkRunner.RunAsync("sparky", "scripts/go.py", null, default)).Contains("service isn't running"), "No heartbeat means the runner isn't running.");
    Heartbeat("ready", at: DateTimeOffset.UtcNow.AddMinutes(-2));
    Check((await sparkRunner.RunAsync("sparky", "scripts/go.py", null, default)).Contains("service isn't running"), "A stale heartbeat means the runner isn't running.");
    Heartbeat("building");
    Check((await sparkRunner.RunAsync("sparky", "scripts/go.py", null, default)).Contains("still building"), "A first-time image build is reported, not waited on.");
    Heartbeat("failed", note: "pull denied");
    Check((await sparkRunner.RunAsync("sparky", "scripts/go.py", null, default)).Contains("couldn't build") && (await sparkRunner.RunAsync("sparky", "scripts/go.py", null, default)).Contains("pull denied"),
        "A failed image build is reported with its reason.");
    Check(Directory.GetFiles(Path.Combine(sparkFolder, "requests")).Length == 0, "Nothing is queued while the runner is unavailable.");
    Heartbeat("ready");

    var seenRequests = new List<JsonObject>();
    var tamper = false;
    var paused = false;
    using var workerStop = new CancellationTokenSource();
    var standIn = Task.Run(async () =>
    {
        while (!workerStop.IsCancellationRequested)
        {
            foreach (var file in paused ? [] : Directory.GetFiles(Path.Combine(sparkFolder, "requests"), "*.json"))
            {
                byte[] bytes;
                try { bytes = File.ReadAllBytes(file); File.Delete(file); }
                catch (IOException) { continue; }
                var request = JsonNode.Parse(bytes)!.AsObject();
                lock (seenRequests) seenRequests.Add(request);
                var id = request["id"]!.GetValue<string>();
                var isCheck = request["kind"]!.GetValue<string>() == "check";
                var isCs = request["script"]!.GetValue<string>().EndsWith(".cs", StringComparison.Ordinal);
                var response = new
                {
                    schemaVersion = 1, id, requestHash = tamper ? new string('0', 64) : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
                    status = "ok", exitCode = isCheck ? 1 : 0, timedOut = false, truncated = false,
                    output = isCheck ? (isCs ? "check.cs(2,5): error CS1002: ; expected" : "  File \"check.py\", line 3\nSyntaxError: invalid syntax") : "ran " + request["arguments"]!.ToJsonString(),
                    diagnostics = isCs ? new[] { new { line = 2, column = 5, severity = "error", message = "CS1002: ; expected" } } : [], message = (string?)null,
                    durationMs = 5, finishedAt = DateTimeOffset.UtcNow,
                };
                var target = Path.Combine(sparkFolder, "responses", id + ".json");
                File.WriteAllText(target + ".tmp", JsonSerializer.Serialize(response));
                File.Move(target + ".tmp", target, overwrite: true);
            }
            await Task.Delay(20);
        }
    });
    JsonObject LastRequest() { lock (seenRequests) return seenRequests[^1]; }
    int RequestCount() { lock (seenRequests) return seenRequests.Count; }

    var hostileArgs = JsonSerializer.SerializeToElement(new { network = "host", image = "evil:latest", path = "/etc", command = "rm -rf /; docker run --privileged x", volume = "/:/host" });
    var sparkResult = await sparkRunner.RunAsync("sparky", "scripts/go.py", hostileArgs, default);
    var sparkRequest = LastRequest();
    Check(sparkResult.StartsWith("Ran scripts/go.py on the Spark") && sparkResult.Contains("Exit code 0") && sparkResult.Contains("without network access") && sparkResult.Contains("ran {"),
        "A Spark run reports the exit code and output: " + sparkResult);
    Check(sparkRequest.Select(pair => pair.Key).Order().SequenceEqual(["arguments", "content", "expiresAt", "hash", "id", "kind", "language", "network", "schemaVersion",
            "script", "skill", "timeoutSeconds"]) && sparkRequest["kind"]!.GetValue<string>() == "run" && sparkRequest["skill"]!.GetValue<string>() == "sparky"
        && sparkRequest["hash"]!.GetValue<string>() == skillStore.HashOf("sparky") && sparkRequest["script"]!.GetValue<string>() == "scripts/go.py"
        && sparkRequest["language"]!.GetValue<string>() == "python" && sparkRequest["network"]!.GetValue<string>() == "none"
        && sparkRequest["timeoutSeconds"]!.GetValue<int>() == 300 && sparkRequest["content"] is null && Guid.TryParse(sparkRequest["id"]!.GetValue<string>(), out _)
        && DateTimeOffset.Parse(sparkRequest["expiresAt"]!.GetValue<string>()) > DateTimeOffset.UtcNow,
        "A request carries only the skill, hash, script, language, arguments, limits and network: " + sparkRequest.ToJsonString());
    Check(sparkRequest["arguments"]!["network"]!.GetValue<string>() == "host" && sparkRequest["arguments"]!["image"]!.GetValue<string>() == "evil:latest",
        "Whatever the model passes stays inside the arguments; it can't set the network, image or any flag.");
    Check(sparkRequest["id"]!.GetValue<string>() == sparkRequest["id"]!.GetValue<string>().ToLowerInvariant()
        && !Directory.GetFiles(Path.Combine(sparkFolder, "requests")).Any() && !Directory.GetFiles(Path.Combine(sparkFolder, "responses")).Any(),
        "Request and response files are removed once the answer is read.");
    Check((await sparkRunner.RunAsync("sparky", "scripts/go.exe", null, default)).Contains("isn't a script that runs on the Spark")
        && (await sparkRunner.RunAsync("sparky", "scripts/go.py", JsonSerializer.SerializeToElement(new[] { 1 }), default)).Contains("object")
        && (await sparkRunner.RunAsync("ghost", "scripts/go.py", null, default)).Contains("no longer exists")
        && (await sparkRunner.RunAsync("sparky", "scripts/go.py", JsonSerializer.SerializeToElement(new { big = new string('x', 17 * 1024) }), default)).Contains("too large"),
        "A bad script, argument shape, missing skill or oversized arguments never reach the queue.");

    // The tool the model calls goes through the router to the Spark runner for a Spark skill only.
    var before = RequestCount();
    var viaTool = (await runTool.InvokeAsync(new AIFunctionArguments { ["skillName"] = "sparky", ["scriptName"] = "scripts/go.py" }, default))?.ToString();
    Check(viaTool is not null && viaTool.StartsWith("Ran scripts/go.py on the Spark") && RequestCount() == before + 1, "run_skill_script sends a Spark skill to the Spark runner: " + viaTool);
    var nodeTool = (await runTool.InvokeAsync(new AIFunctionArguments { ["skillName"] = "alpha", ["scriptName"] = "scripts/check.py" }, default))?.ToString();
    Check(RequestCount() == before + 1 && nodeTool is not null && !nodeTool.Contains("on the Spark"), "A node skill never goes to the Spark runner.");

    // The skill's network choice: none unless a Spark skill asks for the bridge, and the frontmatter keeps it.
    var bridged = await skillStore.UpdateAsync("sparky", new(sparky.Description, "spark", "read", null, sparky.Body, "bridge"), default);
    Check(bridged.Network == "bridge" && skillStore.SettingsOf("sparky").Network == "bridge" && File.ReadAllText(Path.Combine(storage, "skill-data", "skills", "sparky", "SKILL.md")).Contains("lucia.network: bridge"),
        "A Spark skill can allow the bridge network, kept in its frontmatter.");
    await sparkRunner.RunAsync("sparky", "scripts/go.py", null, default);
    Check(LastRequest()["network"]!.GetValue<string>() == "bridge", "The request carries the skill's network.");
    var unchanged = await skillStore.UpdateAsync("sparky", new(sparky.Description, "spark", "read", null, sparky.Body), default);
    Check(unchanged.Network == "bridge", "An update that doesn't mention the network keeps it.");
    var dropped = await skillStore.UpdateAsync("sparky", new(sparky.Description, "node", "read", null, sparky.Body, "bridge"), default);
    Check(dropped.Network == "none" && SkillSettings.From(new Dictionary<string, object?> { ["lucia.execution"] = "node", ["lucia.network"] = "bridge" }).Network == "bridge"
        && SkillSettings.From(new Dictionary<string, object?> { ["lucia.network"] = "host" }).Network == "none" && SkillSettings.From(null).Network == "none",
        "Only a Spark skill keeps the bridge, and unknown values fall back to none.");
    await Reject(() => skillStore.UpdateAsync("sparky", new(sparky.Description, "spark", "read", null, sparky.Body, "host"), default), 400, "invalid_skill", "A skill's network is none or bridge.");
    await skillStore.UpdateAsync("sparky", new(sparky.Description, "spark", "read", null, sparky.Body), default);

    // Checks: C# and Spark-mode skills go to the Spark's container; the editor's content travels, nothing from the skill folder.
    var sparkChecker = new SkillChecker(new NodeExec(null!, null!), sparkRunner, skillStore);
    var csCheck = await sparkChecker.CheckAsync("scripts/app.cs", "int x = ;", null, default, "sparky");
    var csRequest = LastRequest();
    Check(!csCheck.Ok && csCheck.CheckedOn == "the Spark" && csCheck.Diagnostics is [{ Line: 2, Column: 5, Severity: "error" }] && csRequest["kind"]!.GetValue<string>() == "check"
        && csRequest["language"]!.GetValue<string>() == "csharp" && csRequest["content"]!.GetValue<string>() == "int x = ;" && csRequest["hash"] is null && csRequest["arguments"] is null
        && csRequest["timeoutSeconds"]!.GetValue<int>() == 180, "A C# check goes to the Spark with its compiler diagnostics: " + csRequest.ToJsonString());
    var pyCheck = await sparkChecker.CheckAsync("scripts/go.py", "print(", null, default, "sparky");
    Check(!pyCheck.Ok && pyCheck.Diagnostics is [{ Line: 3 }] && LastRequest()["language"]!.GetValue<string>() == "python" && LastRequest()["timeoutSeconds"]!.GetValue<int>() == 60,
        "Every check of a Spark-mode skill goes to the Spark; its output becomes markers.");
    var csOnNode = await sparkChecker.CheckAsync("scripts/app.cs", "int x = ;", null, default, "alpha");
    Check(csOnNode.CheckedOn == "the Spark" && (await sparkChecker.CheckAsync("scripts/app.cs", "", null, default)).Problem == "Name the skill to check this file.",
        "A C# file is checked on the Spark whatever its skill's mode.");
    Check((await sparkRunner.CheckAsync("sparky", "scripts/a.md", "x", default)).Problem!.Contains("scripts folder")
        && (await sparkRunner.CheckAsync("sparky", "references/a.py", "x", default)).Problem!.Contains("scripts folder")
        && (await sparkRunner.CheckAsync("sparky", "scripts/a.py", new string('x', 257 * 1024), default)).Problem!.Contains("too big"), "Only scripts of a bounded size are checked.");

    // An answer for another request, or nobody answering, is never taken as the result.
    tamper = true;
    Check((await sparkRunner.RunAsync("sparky", "scripts/go.py", null, default)).Contains("isn't this request's result"), "A response with another request's hash is refused.");
    tamper = false;
    paused = true;
    var cancelled = false;
    using (var impatient = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
    {
        try { await sparkRunner.RunAsync("sparky", "scripts/go.py", null, impatient.Token); }
        catch (OperationCanceledException) { cancelled = true; }
    }
    Check(cancelled, "A cancelled wait stops.");
    Check(!Directory.GetFiles(Path.Combine(sparkFolder, "requests")).Any(), "A request nobody picked up is withdrawn from the queue.");
    paused = false;
    Heartbeat("ready", ready: false);
    Check((await sparkChecker.CheckAsync("scripts/go.py", "print(1)", null, default, "sparky")).Problem!.Contains("service isn't running"), "A check says so when the runner is down.");
    workerStop.Cancel();
    await standIn;

    // SkillStore's content hash and the worker's are the same algorithm; tools/skills/check_skill_worker.py asserts this vector too.
    var vector = new SkillStore(Options.Create(new AssistantOptions { Directory = Path.Combine(storage, "hash-vector") }), NullLogger<SkillStore>.Instance);
    foreach (var (path, text) in new[] { ("SKILL.md", "---\nname: demo\ndescription: A demo.\n---\n\n# Demo\n"), ("scripts/run.py", "print('hi')\n"),
        ("scripts/deep/er/tool.sh", "echo hi\n"), ("references/notes.md", "Notes \u00e9\n"), ("assets/data.json", "{}") })
    {
        var full = Path.Combine(storage, "hash-vector", "skills", "demo", path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new System.Text.UTF8Encoding(false).GetBytes(text));
    }
    Check(vector.HashOf("demo") == "d4724d5896c46c242c69ddabe78ffcc81821a4e065c33f4a26e9cf67aa9eeca7", "The content hash matches the Spark worker's: " + vector.HashOf("demo"));

    // The node runner runs the agent's synced copy in a private work directory, by language, and always cleans up.
    var nodeHash = skillStore.HashOf("alpha")!;
    var shell = NodeSkillRunner.Command("alpha", nodeHash, "{\"a\":1}", "scripts/check.py")!;
    Check(shell.Contains("mktemp -d") && shell.Contains("umask 077") && shell.Contains("trap 'rm -rf \"$d\"' EXIT") && shell.Contains("LUCIA_SKILL_ARGS")
        && shell.Contains("python3 'scripts/check.py'") && shell.Contains("command -v python3") && shell.Contains($"src='/var/lib/lucia-agent/skills/alpha/{nodeHash}'")
        && shell.Contains("cp -R \"$src/.\" \"$d/\"") && shell.Contains("exit 66") && !shell.Contains("base64 -d <<") && !shell.Contains("LUCIA_BUNDLE") && shell.Length < 2048,
        "A node command runs the cached copy from a private temp directory, passes the arguments, runs python3 and deletes the directory.");
    var hostile = NodeSkillRunner.Command("alpha", nodeHash, "{\"x\":\"'; rm -rf / #\\\"$(reboot)\"}", "scripts/a'b.py")!;
    Check(!hostile.Contains("reboot") && !hostile.Contains("rm -rf / #") && hostile.Contains("python3 'scripts/a'\\''b.py'"),
        "Arguments travel base64-encoded and script paths are quoted, so neither can inject shell.");
    Check(NodeSkillRunner.Command("alpha; reboot", nodeHash, "{}", "scripts/a.py") is null && NodeSkillRunner.Command("alpha", "../../x", "{}", "scripts/a.py") is null
        && NodeSkillRunner.Command("alpha", nodeHash[..40], "{}", "scripts/a.py") is null, "Only a valid skill name and hash reach the cache path.");
    Check(NodeSkillRunner.Command("alpha", nodeHash, "{}", "scripts/a.sh")!.Contains("bash 'scripts/a.sh'")
        && NodeSkillRunner.Command("alpha", nodeHash, "{}", "scripts/a.mjs")!.Contains("node 'scripts/a.mjs'")
        && NodeSkillRunner.Command("alpha", nodeHash, "{}", "scripts/a.ts")!.Contains("node --experimental-strip-types 'scripts/a.ts'")
        && NodeSkillRunner.Command("alpha", nodeHash, "{}", "scripts/a.cs")!.Contains("dotnet run 'scripts/a.cs'")
        && NodeSkillRunner.Command("alpha", nodeHash, "{}", "scripts/a.exe") is null, "Each language runs with its own runtime.");

    // Packages for nodes: only enabled node and sandbox skills, each pinned to its content hash and checksum.
    var packages = new SkillPackages(skillStore);
    var manifest = await packages.ManifestAsync(default);
    var wantedNames = (await skillStore.ListAsync(default)).Where(skill => skill.Enabled && skill.Problem is null && skill.Execution is "node" or "sandbox")
        .Select(skill => skill.Name).Order(StringComparer.Ordinal).ToArray();
    Check(manifest.Sandbox is null && manifest.Skills.Select(entry => entry.Name).SequenceEqual(wantedNames) && wantedNames.Contains("alpha") && wantedNames.Contains("late")
        && !wantedNames.Contains("sparky") && !wantedNames.Contains("boxed"), "The manifest lists only enabled node and sandbox skills: " + string.Join(",", wantedNames));
    var alphaEntry = manifest.Skills.Single(entry => entry.Name == "alpha");
    var alphaPackage = (await packages.GetAsync("alpha", alphaEntry.Hash, default))!.Value;
    Check(alphaEntry.Hash == nodeHash && alphaEntry.Size == alphaPackage.Bytes.Length
        && alphaEntry.Sha256 == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(alphaPackage.Bytes)) && alphaPackage.Sha256 == alphaEntry.Sha256,
        "A manifest entry carries the hash, size and SHA-256 of the package that is served.");
    using (var unpacked = new System.Formats.Tar.TarReader(new System.IO.Compression.GZipStream(new MemoryStream(alphaPackage.Bytes), System.IO.Compression.CompressionMode.Decompress)))
    {
        var entries = new List<string>();
        while (unpacked.GetNextEntry() is { } tarEntry) entries.Add(tarEntry.Name + ":" + tarEntry.EntryType);
        Check(entries.Contains("SKILL.md:RegularFile") && entries.All(item => item.EndsWith(":RegularFile")), "A package holds regular files only.");
    }
    Check(await packages.GetAsync("alpha", new string('0', 64), default) is null && await packages.GetAsync("alpha", "short", default) is null
        && await packages.GetAsync("sparky", skillStore.HashOf("sparky")!, default) is null && await packages.GetAsync("boxed", skillStore.HashOf("boxed")!, default) is null
        && await packages.GetAsync("../alpha", nodeHash, default) is null && await packages.GetAsync("missing", nodeHash, default) is null,
        "A download is refused unless the hash is the current one of an enabled node skill.");
    await skillStore.WriteFileAsync("late", "scripts/changed.py", "print('changed')", default);
    var repacked = await packages.ManifestAsync(default);
    Check(repacked.Skills.Single(entry => entry.Name == "late").Hash != manifest.Skills.Single(entry => entry.Name == "late").Hash
        && await packages.GetAsync("late", manifest.Skills.Single(entry => entry.Name == "late").Hash, default) is null
        && await packages.GetAsync("late", repacked.Skills.Single(entry => entry.Name == "late").Hash, default) is not null,
        "Editing a skill replaces its package; the old hash can no longer be downloaded.");
    Check((await new NodeSkillRunner(new NodeExec(null!, null!), skillStore).RunAsync("alpha", "scripts/check.py", null, default)).StartsWith("Error: this skill runs on a server")
        && (await new NodeSkillRunner(new NodeExec(null!, null!), skillStore).RunAsync("alpha", "scripts/check.py", JsonSerializer.SerializeToElement(new { x = 1 }), default)).Contains("\"node\""),
        "A node-mode script without a node argument says what to pass.");

    // Sandbox mode: the server is the named one with KVM or else the first with KVM, the command is fixed, and the language lock holds at run time.
    var kvmA = (Id: Guid.NewGuid(), Hostname: "alpha-node");
    var kvmB = (Id: Guid.NewGuid(), Hostname: "beta-node");
    Check(SandboxSkillRunner.Choose("beta-node", [kvmA, kvmB]) == kvmB && SandboxSkillRunner.Choose("BETA-NODE.lan.example", [kvmA, kvmB]) == kvmB
        && SandboxSkillRunner.Choose(kvmB.Id.ToString(), [kvmA, kvmB]) == kvmB, "A named server that has KVM runs the sandbox.");
    Check(SandboxSkillRunner.Choose("gamma", [kvmA, kvmB]) == kvmA && SandboxSkillRunner.Choose(null, [kvmA, kvmB]) == kvmA && SandboxSkillRunner.Choose("  ", [kvmB]) == kvmB,
        "Any other name falls back to the first server with KVM.");
    Check(SandboxSkillRunner.Choose("alpha-node", []) is null && SandboxSkillRunner.NoServer == "No server can run sandboxed skills: Hyperlight needs an x86_64 server with KVM.",
        "With no KVM server there is nothing to run on, and the error says why.");
    var sbxHash = new string('b', 64);
    var sbxRequest = SandboxSkillRunner.Request("scripts/go.py", "python", new System.Text.Json.Nodes.JsonObject { ["x"] = "'; reboot #$(id)" }, ["api.example.com"]);
    using (var sbxDoc = JsonDocument.Parse(sbxRequest))
        Check(sbxDoc.RootElement.GetProperty("script").GetString() == "scripts/go.py" && sbxDoc.RootElement.GetProperty("language").GetString() == "python"
            && sbxDoc.RootElement.GetProperty("arguments").GetProperty("x").GetString() == "'; reboot #$(id)"
            && sbxDoc.RootElement.GetProperty("allowedDomains")[0].GetString() == "api.example.com" && sbxDoc.RootElement.GetProperty("timeoutSeconds").GetInt32() == SandboxSkillRunner.ScriptSeconds,
            "The sandbox request names the script, language, arguments, domains and time limit.");
    var sbxCommand = SandboxSkillRunner.Command("alpha", nodeHash, sbxHash, sbxRequest, network: false)!;
    Check(sbxCommand.Contains("docker run --rm -i --device /dev/kvm --network none --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges --pids-limit 256 --memory 1g --cpus 2 --group-add \"$gid\" --user 65534:65534")
        && sbxCommand.Contains($"-v \"$app\":/app:ro -v \"$d\":/skill:ro '{SandboxSkillRunner.Image}' /app/Lucia.SkillSandbox")
        && sbxCommand.Contains("stat -c %g /dev/kvm") && sbxCommand.Contains("trap 'rm -rf \"$d\"' EXIT") && sbxCommand.Contains($"/_sandbox/{sbxHash}'")
        && sbxCommand.Contains($"/alpha/{nodeHash}'") && sbxCommand.Contains("exit " + NodeSkillRunner.NotSyncedExit),
        "The sandbox command is fixed: a hardened read-only container with KVM, no network, private copies and cleanup.");
    Check(!sbxCommand.Contains("reboot") && !sbxCommand.Contains("go.py") && !sbxCommand.Contains("api.example.com") && !sbxCommand.Contains("bridge"),
        "Nothing the model or the author supplies reaches the command line; the request travels as base64 on stdin.");
    Check(SandboxSkillRunner.Command("alpha", nodeHash, sbxHash, sbxRequest, network: true)!.Contains("--network bridge")
        && SandboxSkillRunner.Command("alpha; reboot", nodeHash, sbxHash, "{}", false) is null && SandboxSkillRunner.Command("alpha", "../x", sbxHash, "{}", false) is null
        && SandboxSkillRunner.Command("alpha", nodeHash, "$(id)", "{}", false) is null, "The network is opened only when asked, and only valid names and hashes reach a path.");
    Check(System.Text.RegularExpressions.Regex.IsMatch(SandboxSkillRunner.Image, @"^mcr\.microsoft\.com/dotnet/runtime-deps:10\.0@sha256:[0-9a-f]{64}$"), "The runtime image is pinned by digest.");
    Check(SandboxSkillRunner.Parse("Unable to find image\n{\"exitCode\":3,\"output\":\"hi\\n\",\"truncated\":true,\"error\":null}\n") is (3, "hi\n", true, null)
        && SandboxSkillRunner.Parse("{\"exitCode\":1,\"output\":\"\",\"truncated\":false,\"error\":\"nope\"}") is (1, "", false, "nope")
        && SandboxSkillRunner.Parse("docker: error\n") is null, "The runner reads the sandbox's JSON answer from the last line it finds.");
    Check(SandboxSkillRunner.Language("scripts/a.py") == "python" && SandboxSkillRunner.Language("scripts/a.mjs") == "javascript" && SandboxSkillRunner.Language("scripts/a.js") == "javascript"
        && SandboxSkillRunner.Language("scripts/a.sh") is null && SandboxSkillRunner.Language("scripts/a.ts") is null && SandboxSkillRunner.Language("scripts/a.cs") is null, "The sandbox runs .py, .js and .mjs only.");
    var sbxRuntimeDir = Path.Combine(storage, "sbx-runtime");
    var sbxMissing = new SandboxSkillRunner(new NodeExec(null!, null!), skillStore, new SkillSandboxRuntime(sbxRuntimeDir));
    foreach (var sbxScript in new[] { "scripts/a.sh", "scripts/a.ts", "scripts/a.cs" })
        Check((await sbxMissing.RunAsync("boxed", sbxScript, null, default)).Contains("JavaScript and Python only"), $"A sandbox skill's {sbxScript} is refused at run time too.");
    Check((await sbxMissing.RunAsync("boxed", "scripts/ok.py", null, default)).Contains("no sandbox runtime build"), "A controller without the runtime build says so.");

    // The runtime package: only advertised to a node with KVM, and only while a sandbox skill is enabled.
    Directory.CreateDirectory(sbxRuntimeDir);
    foreach (var (file, text) in new[] { ("Lucia.SkillSandbox", "launcher"), ("libhyperlight.so", "native"), ("python-sandbox.aot", "wasm"), ("Lucia.SkillSandbox.dll", "dll"), ("Lucia.SkillSandbox.pdb", "symbols") })
        File.WriteAllText(Path.Combine(sbxRuntimeDir, file), text);
    var sbxRuntime = new SkillSandboxRuntime(sbxRuntimeDir);
    var sbxPackages = new SkillPackages(skillStore, sbxRuntime);
    Check((await sbxPackages.ManifestAsync(true, default)).Sandbox is null, "No sandbox skill is enabled, so no node is offered the runtime.");
    await skillStore.SetEnabledAsync("boxed", true, default);
    var kvmManifest = await sbxPackages.ManifestAsync(true, default);
    Check((await sbxPackages.ManifestAsync(false, default)).Sandbox is null && (await new SkillPackages(skillStore).ManifestAsync(true, default)).Sandbox is null
        && (await new SkillPackages(skillStore, new SkillSandboxRuntime(Path.Combine(storage, "no-runtime"))).ManifestAsync(true, default)).Sandbox is null
        && kvmManifest.Sandbox is { } sbxEntry && sbxEntry.Hash == sbxRuntime.Hash && kvmManifest.Skills.Any(entry => entry.Name == "boxed"),
        "Only a node that reports KVM is offered the sandbox runtime, and only when this controller ships it.");
    var sbxPackage = (await sbxPackages.GetSandboxAsync(kvmManifest.Sandbox!.Hash, default))!.Value;
    Check(sbxPackage.Sha256 == kvmManifest.Sandbox.Sha256 && sbxPackage.Bytes.Length == kvmManifest.Sandbox.Size && await sbxPackages.GetSandboxAsync(new string('0', 64), default) is null
        && await new SkillPackages(skillStore).GetSandboxAsync(kvmManifest.Sandbox.Hash, default) is null, "The sandbox package matches its manifest entry and is served only at that hash.");
    using (var sbxTar = new System.Formats.Tar.TarReader(new System.IO.Compression.GZipStream(new MemoryStream(sbxPackage.Bytes), System.IO.Compression.CompressionMode.Decompress)))
    {
        var sbxEntries = new Dictionary<string, System.Formats.Tar.TarEntry>();
        while (sbxTar.GetNextEntry() is { } tarEntry) sbxEntries[tarEntry.Name] = tarEntry;
        Check(sbxEntries.Keys.Order().SequenceEqual(new[] { "Lucia.SkillSandbox", "Lucia.SkillSandbox.dll", "libhyperlight.so", "python-sandbox.aot" }.Order())
            && sbxEntries.Values.All(item => item.EntryType == System.Formats.Tar.TarEntryType.RegularFile && !item.Name.StartsWith("./") && !item.Name.StartsWith('/'))
            && (sbxEntries["Lucia.SkillSandbox"].Mode & UnixFileMode.UserExecute) != 0 && (sbxEntries["libhyperlight.so"].Mode & UnixFileMode.UserExecute) != 0
            && (sbxEntries["python-sandbox.aot"].Mode & UnixFileMode.UserExecute) == 0, "The runtime tar has flat names without ./, keeps execute bits on the launcher and libraries and leaves out symbols.");
    }
    await skillStore.SetEnabledAsync("boxed", false, default);
    Check((await sbxPackages.ManifestAsync(true, default)).Sandbox is null, "The runtime is withdrawn when the last sandbox skill is disabled.");    var syntax = SkillChecker.Parse(".py", "  File \"check.py\", line 3\n    x =\n       ^\nSyntaxError: invalid syntax\n");
    Check(syntax is [{ Line: 3, Severity: "error" }] && syntax[0].Message.Contains("invalid syntax")
        && SkillChecker.Parse(".sh", "check.sh: line 4: syntax error near unexpected token `fi'")[0].Line == 4
        && SkillChecker.Parse(".js", "/tmp/x/check.js:7\n  x = ;\n      ^\nSyntaxError: Unexpected token ';'\n")[0].Line == 7
        && SkillChecker.Command(".cs", "") is null && SkillChecker.Command(".ts", "AAAA")!.Contains("--experimental-strip-types"),
        "Syntax check output becomes editor markers.");

    // Grants: "always allow" for a script is per skill and content hash, and the tier is the skill's own.
    var alphaHash = skillStore.HashOf("alpha")!;
    Check(AssistantSkills.Grant("alpha", alphaHash) == "skill:alpha@" + alphaHash[..8] && Regex.IsMatch(AssistantSkills.Grant("alpha", alphaHash), @"^skill:[a-z0-9-]+@[0-9a-f]{8}$"),
        "A skill grant is skill:<name>@<first 8 hex of the hash>.");
    var skillRun = new AssistantRun("m-skill", default);
    var skillKit = broker.Kit(new AssistantTurn(owner, govKey, "Hi", null, "execute"), new AssistantSettings([], []), [.. context.Tools], new AssistantStream("m-skill", null), skillRun);
    Check(Verdict(await GovCall(skillKit, "load_skill", new { skillName = "alpha" })) == "approve", "Loading a skill is a read.");
    Check(Verdict(await GovCall(skillKit, "refresh_skills")) == "approve", "Refreshing the skill list is a read.");
    Check(Verdict(await GovCall(skillKit, "run_skill_script", new { skillName = "sparky", scriptName = "scripts/go.py" })) == "approve", "A read-tier skill's script runs without asking.");
    var skillAsk = GovCall(skillKit, "run_skill_script", new { skillName = "alpha", scriptName = "scripts/check.py", arguments = new { node = "lab" } });
    var skillChunks = GovNew(skillRun);
    Check(!skillAsk.IsCompleted && Types(skillChunks) == "tool-input-available,tool-approval-request", "A change-tier skill's script waits for the owner.");
    Check(runs.Respond(owner, chat, (string)skillChunks[1]["approvalId"]!, true, null, always: true) && Verdict(await skillAsk) == "approve", "The owner can approve a script.");
    Check(Verdict(await GovCall(skillKit, "run_skill_script", new { skillName = "alpha", scriptName = "scripts/other.py" })) == "approve"
        && (string)GovNew(skillRun)[^1]["reason"]! == "You allowed this for this chat.", "'Always' allows that skill's scripts for the chat.");
    await skillStore.WriteFileAsync("alpha", "scripts/check.py", "print('changed after approval')\n", default);
    var regrant = GovCall(skillKit, "run_skill_script", new { skillName = "alpha", scriptName = "scripts/check.py" });
    Check(!regrant.IsCompleted, "Editing a skill ends its earlier 'always allow'.");
    skillRun.Stop.Cancel();
    await regrant;
    var skillPlanRun = new AssistantRun("m-skill-plan", default);
    var skillPlanKit = broker.Kit(new AssistantTurn(owner, govKey + "-plan", "Hi", null, "plan"), new AssistantSettings([], []), [.. context.Tools], new AssistantStream("m-skill-plan", null), skillPlanRun);
    Check(Verdict(await GovCall(skillPlanKit, "run_skill_script", new { skillName = "alpha", scriptName = "scripts/check.py" })) == govPlan, "Plan mode refuses a change-tier script.");
    Check(Verdict(await GovCall(skillPlanKit, "run_skill_script", new { skillName = "sparky", scriptName = "scripts/go.py" })) == "approve", "Plan mode still runs a read-tier script.");
    Check(Verdict(await GovCall(skillPlanKit, "run_skill_script", new { skillName = "nonexistent", scriptName = "scripts/go.py" })) == govPlan, "A script of an unknown skill counts as a change.");
    var boxedGrant = AssistantSkills.Grant("boxed", skillStore.HashOf("boxed")!);
    List<string> skillSaved = [];
    var skillJob = new AssistantJobGrants("Skills", [boxedGrant], grant => { skillSaved.Add(grant); return Task.CompletedTask; }, _ => Task.CompletedTask);
    var skillJobKit = broker.Kit(new AssistantTurn(owner, govKey + "-job", "Hi", null, "execute"), new AssistantSettings([], []), [.. context.Tools],
        new AssistantStream("m-skill-job", null), new AssistantRun("m-skill-job", default), skillJob);
    Check(Verdict(await GovCall(skillJobKit, "run_skill_script", new { skillName = "boxed", scriptName = "scripts/ok.py" })) == "approve",
        "A job runs a destructive skill it holds the grant for.");
    var jobSkillAsk = GovCall(skillJobKit, "run_skill_script", new { skillName = "alpha", scriptName = "scripts/check.py" });
    Check(!jobSkillAsk.IsCompleted, "A job asks for a skill it has no grant for.");

    // Jobs pick their skills: none by default, "*" for all, or names; skill grants are saved in the job's permissions.
    var skillJobs = new AssistantJobs(options, runs, new StoppedLifetime(), NullLogger<AssistantJobs>.Instance);
    AssistantJobRequest SkillJob(string[]? skills, string[]? tools = null) =>
        new("Skilled", ["Do it."], "0 7 * * *", "UTC", null, true, tools, [], Skills: skills);
    await Reject(() => skillJobs.SaveAsync(owner, null, SkillJob(["Not A Name"]), "tester via assistant", "tester", default), 400, "invalid_job", "Job skills are names or *.");
    var skilled = JsonSerializer.SerializeToNode(await skillJobs.SaveAsync(owner, null, SkillJob(["alpha", "*", "alpha"], [boxedGrant]), "tester via assistant", "tester", default), AssistantStream.Json)!;
    Check(skilled["skills"]!.AsArray().Select(item => (string)item!).SequenceEqual(["*"]) && skilled["tools"]!.AsArray().Select(item => (string)item!).SequenceEqual([boxedGrant]),
        "A job keeps '*' for every enabled skill, and a saved skill grant.");
    var named = JsonSerializer.SerializeToNode(await skillJobs.SaveAsync(owner, null, SkillJob(["alpha", "boxed", "alpha"]), "tester via assistant", "tester", default), AssistantStream.Json)!;
    var plainJob = JsonSerializer.SerializeToNode(await skillJobs.SaveAsync(owner, null, SkillJob(null), "tester via assistant", "tester", default), AssistantStream.Json)!;
    Check(named["skills"]!.AsArray().Count == 2 && plainJob["skills"]!.AsArray().Count == 0
        && (await skillJobs.SkillsAsync(owner, (string)named["id"]!, default)).SequenceEqual(["alpha", "boxed"])
        && (await skillJobs.SkillsAsync(owner, (string)plainJob["id"]!, default)).Length == 0, "A job with no skills selected gets none; named skills are kept once each.");
    foreach (var made in new[] { skilled, named, plainJob }) await skillJobs.DeleteAsync(owner, (string)made["id"]!, default);

    // Review fixes: the inspection cache trusts only its own revision, a disabled skill stops mid-turn, and rewrites keep unknown frontmatter.
    var cache = (System.Collections.IDictionary)typeof(SkillStore).GetField("_inspections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(skillStore)!;
    var realHash = skillStore.HashOf("alpha")!;
    var entry = cache["alpha"]!;
    entry.GetType().GetProperty("Hash")!.SetValue(entry, new string('f', 64));
    entry.GetType().GetProperty("Revision")!.SetValue(entry, skillStore.Revision - 1);
    Check(skillStore.HashOf("alpha") == realHash, "An inspection built at an older revision is never served, so a racing reader can't leave a stale hash.");

    await skillStore.SetEnabledAsync("alpha", true, default);
    foreach (var gone in new[] { "sparky", "late" }) await skillStore.SetEnabledAsync(gone, false, default);
    var stillLoads = (await sparkContext.Tools.First(tool => tool.Name == "load_skill").InvokeAsync(new AIFunctionArguments { ["skillName"] = "sparky" }, default))?.ToString();
    Check(stillLoads is not null && !stillLoads.Contains("Pinned to the Spark."), "A skill disabled mid-turn can't be loaded while others stay enabled.");
    var stillRuns = (await sparkContext.Tools.First(tool => tool.Name == "run_skill_script").InvokeAsync(new AIFunctionArguments { ["skillName"] = "sparky", ["scriptName"] = "scripts/go.py" }, default))?.ToString();
    Check(stillRuns is not null && !stillRuns.Contains("isn't available yet"), "A skill disabled mid-turn can't run its scripts: " + stillRuns);
    await skillStore.SetEnabledAsync("alpha", false, default);
    var lastGone = (await sparkContext.Tools.First(tool => tool.Name == "run_skill_script").InvokeAsync(new AIFunctionArguments { ["skillName"] = "alpha", ["scriptName"] = "scripts/check.py" }, default))?.ToString();
    Check(lastGone == "Error: that skill is no longer enabled.", "Disabling the last enabled skill stops an in-flight turn from running it.");

    await skillStore.CreateAsync(new("rich", "Has extra frontmatter.", "node", "read", null, null), default);
    var richPath = Path.Combine(skillStore.Root, "rich", "SKILL.md");
    await File.WriteAllTextAsync(richPath, "---\nname: rich\ndescription: Has extra frontmatter.\nlicense: MIT\nversion: 1.2\nauthor: someone\nallowed-tools:\n  - Bash\n  - Read\nmetadata:\n  team: ops\n  tags:\n    - a\n    - b\n  lucia.execution: node\n  lucia.tier: read\n---\n\n# Rich\n");
    await skillStore.UpdateAsync("rich", new("Still rich.", "node", "change", [], "# Rich again\n"), default);
    var rewritten = SkillMarkdown.Parse(await File.ReadAllTextAsync(richPath))!;
    Check(rewritten.Text("description") == "Still rich." && rewritten.Text("license") == "MIT" && rewritten.Text("version") == "1.2" && rewritten.Text("author") == "someone"
        && rewritten.Map["allowed-tools"] is IList<object> { Count: 2 } && rewritten.Map["metadata"] is IDictionary<object, object> { } nested
        && nested["tags"] is IList<object> { Count: 2 } && (string)nested["team"] == "ops" && (string)nested["lucia.tier"] == "change" && rewritten.Body.StartsWith("# Rich again"),
        "Saving a skill keeps unknown frontmatter keys, list values and nested metadata, changing only name, description and lucia.* settings.");
    await skillStore.DeleteAsync("rich", default);

    // Every skill service the app registers must have its Skills-namespace constructor dependencies registered too,
    // so a missing AddSingleton can't break the container (and with it chats and the node routes) at runtime.
    var skillServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
    skillServices.AddSkills();
    var registered = skillServices.Select(service => service.ServiceType).ToHashSet();
    var skillNamespace = typeof(SkillStore).Namespace;
    var missing = registered.Where(type => type.Namespace == skillNamespace)
        .SelectMany(type => type.GetConstructors().SelectMany(ctor => ctor.GetParameters()))
        .Select(parameter => parameter.ParameterType)
        .Where(type => type.Namespace == skillNamespace && !registered.Contains(type)).Distinct().ToArray();
    Check(missing.Length == 0, "Skill services depend on unregistered types: " + string.Join(", ", missing.Select(type => type.Name)));
    var routeServices = new[] { typeof(SkillPackages), typeof(SandboxSkillRunner), typeof(SparkSkillRunner), typeof(NodeSkillRunner), typeof(AssistantSkills) };
    Check(routeServices.All(registered.Contains), "AddSkills registers every service the skill routes and runners take.");
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
            ("GET", "http://10.0.0.5:4000/model/info") when key == "sk-master" => Json(new
            {
                data = new object[] { new { model_name = "gpt-4o", model_info = new { supports_reasoning = true, max_input_tokens = 128_000 } } }
            }),
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
