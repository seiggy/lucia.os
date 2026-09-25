using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using TensorSharp.Runtime;
using Lucia.Homelab.Server.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.FirstOrDefault() == "download")
{
    var directory = args[Array.IndexOf(args, "--local-dir") + 1];
    if (args[1] == "checks/failure")
    {
        Console.Error.WriteLine("Denied hf-test-secret https://example.invalid/download?token=secret");
        return 42;
    }
    if (args[1] == "checks/slow")
        await Task.Delay(TimeSpan.FromMinutes(1));
    foreach (var file in args.Skip(2).TakeWhile(arg => arg != "--revision"))
    {
        var destination = Path.Combine(directory, file);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (args[1] == "checks/invalid")
            await File.WriteAllTextAsync(destination, "not a GGUF");
        else
            WriteGguf(destination);
    }
    if (args[1] == "checks/persistence-error")
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(directory)!, "model.json.tmp"));
    if (args.Any(arg => arg.Contains("hf-test-secret", StringComparison.Ordinal)))
        return 43;
    if (Environment.GetEnvironmentVariable("HF_TOKEN") != "hf-test-secret")
        return 44;
    if (Environment.GetEnvironmentVariable("HF_ENDPOINT") != "https://huggingface.co")
        return 45;
    if (Environment.GetEnvironmentVariable("HF_HUB_DISABLE_IMPLICIT_TOKEN") != "0")
        return 46;
    return 0;
}

static void WriteGguf(string path)
{
    using var writer = new BinaryWriter(File.Create(path));
    void Text(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }
    void StringValue(string key, string value) { Text(key); writer.Write(8u); Text(value); }
    void Number(string key, uint value) { Text(key); writer.Write(4u); writer.Write(value); }
    writer.Write(0x46554747u);
    writer.Write(3u);
    writer.Write(1ul);
    writer.Write(10ul);
    StringValue("general.architecture", "qwen35moe");
    StringValue("tokenizer.ggml.model", "gpt2");
    Text("tokenizer.ggml.tokens"); writer.Write(9u); writer.Write(8u); writer.Write(2ul); Text("a"); Text("b");
    Number("qwen35moe.context_length", 32768);
    Number("qwen35moe.block_count", 5);
    Number("qwen35moe.nextn_predict_layers", 1);
    Number("qwen35moe.attention.head_count", 8);
    Number("qwen35moe.attention.head_count_kv", 2);
    Number("qwen35moe.embedding_length", 64);
    Number("qwen35moe.full_attention_interval", 4);
    Text("token_embd.weight"); writer.Write(2u); writer.Write(64ul); writer.Write(2ul); writer.Write(0u); writer.Write(0ul);
    while (writer.BaseStream.Position % 32 != 0) writer.Write((byte)0);
    writer.Write(new byte[64 * 2 * sizeof(float)]);
}

static void Check([DoesNotReturnIf(false)] bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static async Task<LocalModel> WaitFor(ModelCatalog catalog, Guid id, params ModelDownloadState[] states)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while (true)
    {
        var model = catalog.Find(id)!;
        if (states.Contains(model.State))
            return model;
        await Task.Delay(25, timeout.Token);
    }
}

var valid = new ModelDownloadRequest("huggingface", "checks/model", "weights/model.gguf", ModelKind.Chat, Pro: true);
Check(ModelCatalog.Validate(valid) is null, "Valid Hugging Face GGUF request rejected.");
foreach (var file in new[] { "../outside.gguf", "/absolute.gguf", @"C:\outside.gguf", "--token.gguf", "folder//model.gguf", @"folder\model.gguf", "model.safetensors" })
    Check(ModelCatalog.Validate(valid with { File = file }) is not null, $"Unsafe or unsupported path accepted: {file}");
Check(ModelCatalog.Validate(valid with { Provider = "unknown" }) is not null, "Unknown provider accepted.");
Check(ModelCatalog.Validate(valid with { Repository = "--token" }) is not null, "CLI option accepted as repository.");
Check(ModelCatalog.Validate(valid with { Revision = "--token" }) is not null, "CLI option accepted as revision.");
Check(ModelCatalog.Validate(valid with { Kind = (ModelKind)999 }) is not null, "Invalid model kind accepted.");
Check(ModelCatalog.ModelFiles("weights/model-00001-of-00002.gguf").SequenceEqual(
    ["weights/model-00001-of-00002.gguf", "weights/model-00002-of-00002.gguf"]), "Split GGUF files were not expanded.");
Check(ModelCatalog.Validate(valid with { File = "model-00002-of-00002.gguf" }) is not null, "Non-first GGUF shard accepted.");

var known = new ModelInspection("qwen35moe", ModelKind.Chat, ModelPresets.Bundled.SizeBytes,
    ModelPresets.Bundled.SizeBytes, 262144, 10, 81920, ["Q6_K"]);
var plan = ModelInspector.Calculate(known, new HostPlatformOptions(), 128 * ModelInspector.GiB);
Check(plan.VoiceReserveBytes == 8 * ModelInspector.GiB && plan.OsReserveBytes == 8 * ModelInspector.GiB, "Voice and OS reserves were not separate.");
Check(plan.EffectiveContextTokens == 32768 && plan.MemoryLimitedContextTokens == 262144, "Context calculation exceeded its cap or ignored metadata.");
var noRoom = ModelInspector.Calculate(known, new HostPlatformOptions(), 32 * ModelInspector.GiB);
Check(noRoom.EffectiveContextTokens == 0, "An over-budget model was admitted.");
var declarations = TensorSharpChatClient.ConvertTools([AIFunctionFactory.Create((string path) => path, "probe")]);
Check(declarations[0].Required.Contains("path") && declarations[0].ParametersSchemaJson?.Contains("properties") == true, "Function schema was lost.");
var history = TensorSharpChatClient.ConvertMessages([
    new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call1", "probe", new Dictionary<string, object?> { ["path"] = "sample" })]),
    new Microsoft.Extensions.AI.ChatMessage(ChatRole.Tool, [new FunctionResultContent("call1", "result")])
]);
Check(history[0].ToolCalls![0].Id == "call1" && history[1].ToolCallId == "call1", "Tool call IDs did not survive the round trip.");
var parser = OutputParserFactory.Create("qwen35moe");
parser.Init(false, declarations);
var parsed = parser.Add("<tool_call>\n<function=probe>\n<parameter=path>sample</parameter>\n</function>\n</tool_call>", true);
Check(parsed.ToolCalls is { Count: 1 } && parsed.ToolCalls[0].Name == "probe", "Qwen function-call syntax was not parsed.");
foreach (var reason in new[] { "max_tokens", "thinking_budget", "repetition", "MAX_TOKENS" })
{
    var completion = TensorSharpChatClient.CompleteGeneration(
        new TensorSharp.Server.ChatStreamUpdate("", true, 4, 5, 0, 0, 0, 0, reason),
        [new FunctionCallContent("call1", "probe")]);
    Check(completion.FinishReason == ChatFinishReason.Length && !completion.Contents.OfType<FunctionCallContent>().Any(),
        "Truncated generation released a tool call.");
}
var finished = TensorSharpChatClient.CompleteGeneration(
    new TensorSharp.Server.ChatStreamUpdate("", true, 4, 5, 0, 0, 0, 0, "eos"),
    [new FunctionCallContent("call1", "probe")]);
Check(finished.FinishReason == ChatFinishReason.ToolCalls && finished.Contents.OfType<FunctionCallContent>().Count() == 1,
    "A completed generation lost its tool call.");

var root = Path.Combine(Path.GetTempPath(), $"lucia-model-checks-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    var interruptedId = Guid.NewGuid();
    var interruptedDirectory = Path.Combine(root, interruptedId.ToString("N"));
    Directory.CreateDirectory(interruptedDirectory);
    await File.WriteAllTextAsync(Path.Combine(interruptedDirectory, "model.json"),
        JsonSerializer.Serialize(new LocalModel(interruptedId, valid, ModelDownloadState.Downloading,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.SetMinimumLevel(LogLevel.Critical);
    builder.Services.Configure<HostPlatformOptions>(options =>
    {
        options.ApiKey = new string('x', 32);
        options.ModelDirectory = root + Path.DirectorySeparatorChar;
        options.HuggingFaceExecutable = Environment.ProcessPath!;
        options.HuggingFaceToken = "hf-test-secret";
    });
    builder.Services.AddSingleton<ModelCatalog>();
    builder.Services.AddHostedService(services => services.GetRequiredService<ModelCatalog>());
    using var host = builder.Build();
    await host.StartAsync();
    var catalog = host.Services.GetRequiredService<ModelCatalog>();
    Check(catalog.Find(interruptedId)?.State == ModelDownloadState.Interrupted, "A restart incorrectly completed an unfinished download.");
    await catalog.RetryAsync(interruptedId, CancellationToken.None);
    await WaitFor(catalog, interruptedId, ModelDownloadState.Ready);
    var model = await catalog.DownloadAsync(valid, CancellationToken.None);
    await WaitFor(catalog, model.Id, ModelDownloadState.Ready);
    Check(catalog.Find(model.Id)!.Inspection?.AttentionLayers == 1, "MTP/recurrent layers were incorrectly counted as full attention.");
    Check(File.Exists(catalog.ModelPath(model.Id)), "Completed model was not saved.");
    var duplicate = await catalog.DownloadAsync(valid, CancellationToken.None);
    Check(duplicate.Id == model.Id, "Repeated download created a duplicate model.");
    Check(await catalog.ReadSelectionAsync(CancellationToken.None) is null, "An untouched catalog fabricated a startup selection.");
    var selectionEmbedding = Guid.NewGuid();
    await catalog.SaveSelectionAsync(ModelKind.Chat, model.Id, 8192);
    await catalog.SaveSelectionAsync(ModelKind.Embedding, selectionEmbedding, 512);
    var selection = await catalog.ReadSelectionAsync(CancellationToken.None);
    Check(selection?.ChatId == model.Id && selection.ChatContext == 8192
        && selection.EmbeddingId == selectionEmbedding && selection.EmbeddingContext == 512, "The two startup slots overwrote each other.");
    await catalog.SaveSelectionAsync(ModelKind.Chat, null, null);
    selection = await catalog.ReadSelectionAsync(CancellationToken.None);
    Check(selection?.ChatId is null && selection?.EmbeddingId == selectionEmbedding, "Unloading chat cleared the embedding startup selection.");
    await catalog.SaveSelectionAsync(ModelKind.Embedding, null, null);
    await catalog.SaveSelectionAsync(ModelKind.Chat, model.Id, 8192);

    var failed = await catalog.DownloadAsync(valid with { Repository = "checks/failure" }, CancellationToken.None);
    var failure = await WaitFor(catalog, failed.Id, ModelDownloadState.Failed);
    Check(failure.Error is not null && failure.Error.Contains("42", StringComparison.Ordinal), "CLI failure was not surfaced.");
    Check(!failure.Error.Contains("hf-test-secret", StringComparison.Ordinal), "Token leaked into failure response.");
    Check(!failure.Error.Contains("token=secret", StringComparison.Ordinal), "Signed URL leaked into failure response.");
    var invalid = await catalog.DownloadAsync(valid with { Repository = "checks/invalid" }, CancellationToken.None);
    await WaitFor(catalog, invalid.Id, ModelDownloadState.Failed);
    var unsaved = await catalog.DownloadAsync(valid with { Repository = "checks/persistence-error" }, CancellationToken.None);
    var persistenceFailure = await WaitFor(catalog, unsaved.Id, ModelDownloadState.Failed);
    Check(persistenceFailure.PersistenceError is not null, "A persistence failure was hidden.");
    var recovery = await catalog.DownloadAsync(valid with { Repository = "checks/recovery" }, CancellationToken.None);
    await WaitFor(catalog, recovery.Id, ModelDownloadState.Ready);
    await catalog.DeleteAsync(unsaved.Id, CancellationToken.None);

    var slow = await catalog.DownloadAsync(valid with { Repository = "checks/slow" }, CancellationToken.None);
    await WaitFor(catalog, slow.Id, ModelDownloadState.Downloading);
    var queued = await catalog.DownloadAsync(valid with { Repository = "checks/queued" }, CancellationToken.None);
    await catalog.CancelAsync(queued.Id, CancellationToken.None);
    Check(catalog.Find(queued.Id)?.State == ModelDownloadState.Canceled, "Queued cancellation waited for the active download.");
    await catalog.DeleteAsync(queued.Id, CancellationToken.None);
    await catalog.CancelAsync(slow.Id, CancellationToken.None);
    await WaitFor(catalog, slow.Id, ModelDownloadState.Canceled);
    await host.StopAsync();

    var saved = JsonSerializer.Deserialize<LocalModel>(
        await File.ReadAllTextAsync(Path.Combine(root, model.Id.ToString("N"), "model.json")),
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    Check(saved?.State == ModelDownloadState.Ready, "Model readiness was not persisted.");
    await catalog.DeleteAsync(model.Id, CancellationToken.None);
    Check((await catalog.ReadSelectionAsync(CancellationToken.None))?.ChatId is null, "Deleting a model retained its startup selection.");
    Check(catalog.Find(model.Id) is null, "Deleted model remains in the catalog.");
    Check(!Directory.Exists(Path.Combine(root, model.Id.ToString("N"))), "Deleted model files remain.");
    var web = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        EnvironmentName = Environments.Development,
        ApplicationName = typeof(ApiDocumentation).Assembly.GetName().Name
    });
    web.Logging.SetMinimumLevel(LogLevel.Critical);
    web.WebHost.UseUrls("http://127.0.0.1:0");
    web.Configuration["HostPlatform:ApiKey"] = new string('o', 32);
    web.Configuration["HostPlatform:InferenceApiKey"] = new string('i', 32);
    web.Configuration["HostPlatform:ModelDirectory"] = Path.Combine(root, "api-models");
    web.Configuration["HostPlatform:Backend"] = "cpu";
    web.AddHostPlatform();
    web.Services.AddApiDocumentation();
    await using var app = web.Build();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapHostPlatform();
    app.MapDevelopmentApiDocumentation();
    await app.StartAsync();
    using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
    Check((await client.GetAsync("/v1/models")).StatusCode == HttpStatusCode.Unauthorized, "Unauthenticated inference was permitted.");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('i', 32));
    Check((await client.GetAsync("/v1/models")).StatusCode == HttpStatusCode.OK, "Inference key could not list models.");
    Check((await client.GetAsync("/api/host/models")).StatusCode == HttpStatusCode.Forbidden, "Inference key granted model administration.");
    Check((await client.PostAsync("/v1/chat/completions", new StringContent("{\"model\":\"missing\",\"messages\":[]}", Encoding.UTF8, "application/json")))
        .StatusCode == HttpStatusCode.ServiceUnavailable, "An unloaded model did not return an explicit unavailable response.");
    Check((await client.PostAsync("/v1/embeddings", new StringContent("{\"model\":\"missing\",\"input\":\"sample\"}", Encoding.UTF8, "application/json")))
        .StatusCode == HttpStatusCode.ServiceUnavailable, "An unloaded embedding model was not explicitly unavailable.");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('o', 32));
    Check((await client.GetAsync("/api/host/status")).StatusCode == HttpStatusCode.OK, "Owner could not read host state.");
    Check((await client.GetAsync("/api/host/model-catalog")).StatusCode == HttpStatusCode.OK, "Catalog unavailable.");
    Check((await client.PostAsync("/api/host/models/download",
        new StringContent(JsonSerializer.Serialize(valid with { Pro = false }), Encoding.UTF8, "application/json")))
        .StatusCode == HttpStatusCode.BadRequest, "An unvalidated model bypassed Pro opt-in.");
    Check((await client.PostAsync("/v1/videos/generations", new StringContent("{}", Encoding.UTF8, "application/json")))
        .StatusCode == HttpStatusCode.NotFound, "Unrequested TensorSharp routes were exposed.");
    client.DefaultRequestHeaders.Authorization = null;
    var scalar = await client.GetStringAsync("/scalar/v1");
    Check(scalar.Contains("Lucia Host API", StringComparison.Ordinal), "Scalar explorer is unavailable.");
    Check(!scalar.Contains(new string('o', 32), StringComparison.Ordinal), "Owner key leaked into the API explorer.");
    using var openapi = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
    var spec = openapi.RootElement;
    Check(spec.GetProperty("components").GetProperty("securitySchemes").GetProperty("LuciaBearer")
        .GetProperty("scheme").GetString() == "bearer", "Bearer authentication is not documented.");
    Check(spec.GetProperty("paths").GetProperty("/api/host/models").GetProperty("get")
        .GetProperty("security")[0].TryGetProperty("LuciaBearer", out _), "Owner API authentication is absent from OpenAPI.");
    Check(spec.GetProperty("paths").GetProperty("/v1/chat/completions").GetProperty("post")
        .GetProperty("requestBody").GetProperty("content").GetProperty("application/json")
        .GetProperty("example").GetProperty("messages").GetArrayLength() == 1, "Raw inference request example is missing.");
    await app.StopAsync();
    var production = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
    production.Logging.SetMinimumLevel(LogLevel.Critical);
    production.WebHost.UseUrls("http://127.0.0.1:0");
    production.Services.AddApiDocumentation();
    await using var productionApp = production.Build();
    productionApp.MapDevelopmentApiDocumentation();
    await productionApp.StartAsync();
    using var productionClient = new HttpClient { BaseAddress = new Uri(productionApp.Urls.Single()) };
    Check((await productionClient.GetAsync("/scalar/v1")).StatusCode == HttpStatusCode.NotFound, "Scalar was exposed outside development.");
    Check((await productionClient.GetAsync("/openapi/v1.json")).StatusCode == HttpStatusCode.NotFound, "OpenAPI was exposed outside development.");
    await productionApp.StopAsync();
    Console.WriteLine("Host checks passed: format, memory budget, tool round trip, downloads, cancellation, persistence, deletion, API auth and unavailable inference.");
}
finally
{
    Directory.Delete(root, recursive: true);
}
return 0;
