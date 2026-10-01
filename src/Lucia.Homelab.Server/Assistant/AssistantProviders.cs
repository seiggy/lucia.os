using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitHub.Copilot;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Stacks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Assistant;

public sealed record AssistantModel(string Id, string Name);

/// <summary>One group in the model picker. <c>Reason</c> says why it has no models, or which of its apps can't serve right now.</summary>
public sealed record AssistantSource(string Id, string Name, AssistantModel[] Models, string? Reason = null);

/// <summary>The Spark's own OpenAI API, reached over loopback, or why it can't serve the assistant.</summary>
public sealed record SparkInference(Uri? BaseUri, string? Key, string? Model, int ContextTokens, int MaxOutputTokens, string? Problem);

/// <summary>
/// Models the owner brings: LiteLLM's, the Spark's and each Local AI app's. They run in the same Copilot runtime as
/// bring-your-own-key providers, so they need no GitHub sign-in. Model ids carry where they run: <c>litellm:&lt;model&gt;</c>,
/// <c>local:&lt;model&gt;</c> on the Spark and <c>local:&lt;stack&gt;/&lt;model&gt;</c> on a node; bare ids are Copilot's.
/// LiteLLM gets a virtual key per owner, minted with the app's master key and kept encrypted on the host.
/// </summary>
public sealed partial class AssistantProviders(IOptions<AssistantOptions> options, IDataProtectionProvider protection,
    ILogger<AssistantProviders> logger, HttpClient http, Func<CancellationToken, Task<IReadOnlyList<AppEndpoint>>> apps,
    Func<SparkInference> spark)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public static HttpClient CreateClient() => new(CloudflareHttp.CreateHandler())
    {
        Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1024 * 1024
    };

    public static bool IsGitHub(string? id) => id is null || Parse(id).Source == "github";

    public async Task<AssistantSource[]> ListAsync(string owner, CancellationToken ct)
    {
        var endpoints = await apps(ct);
        var liteLlm = LiteLlmSourceAsync(owner, endpoints, ct);
        var local = LocalSourceAsync(endpoints, ct);
        return [await liteLlm, await local];
    }

    /// <summary>The provider and model name for a turn. No provider means GitHub Copilot.</summary>
    public async Task<(ProviderConfig? Provider, string? Model)> ResolveAsync(string owner, string? id, CancellationToken ct)
    {
        if (id is null) return (null, null);
        var (source, stack, model) = Parse(id);
        if (source == "github") return (null, id);
        if (model.Length == 0) throw Unavailable("That model isn't available. Pick another model.");
        var endpoints = await apps(ct);
        if (source == "litellm")
        {
            var (app, problem) = LiteLlm(endpoints);
            if (app is null) throw Unavailable(problem!);
            (string Key, ServedModel[] Models) served;
            try { served = await LiteLlmModelsAsync(owner, app, ct); }
            catch (Exception e) when (Unreachable(e, ct)) { throw Unavailable(Failure(app, "LiteLLM", e)); }
            if (served.Models.All(item => item.Id != model)) throw Unavailable($"{model} isn't on LiteLLM anymore. Pick another model.");
            return (Provider(new Uri(app.BaseUri!, "v1"), served.Key, null, null), model);
        }
        if (stack is null)
        {
            var host = spark();
            if (host.Problem is { } problem) throw Unavailable(problem);
            if (host.Model != model) throw Unavailable($"{model} isn't loaded on the Spark anymore. Pick another model.");
            return (Provider(host.BaseUri!, host.Key, host.MaxOutputTokens, Math.Max(1, host.ContextTokens - host.MaxOutputTokens)), model);
        }
        var node = endpoints.FirstOrDefault(item => item.Template == "local-ai" && item.Stack == stack)
            ?? throw Unavailable("That Local AI app isn't installed anymore. Pick another model.");
        var (models, reason) = await ServedAsync(node, ct);
        var found = models.FirstOrDefault(item => item.Id == model)
            ?? throw Unavailable(reason ?? $"{model} isn't on Local AI on {node.Node} anymore. Pick another model.");
        // Engines that don't report an output limit get a quarter of the context, so prompt and answer always fit.
        var output = found.Output ?? found.Context / 4;
        var prompt = found.Context - output;
        return (Provider(new Uri(node.BaseUri!, "v1"), node.Key, output > 0 ? output : null, prompt > 0 ? prompt : null), model);
    }

    private static (string Source, string? Stack, string Model) Parse(string id)
    {
        var colon = id.IndexOf(':');
        var rest = colon < 0 ? id : id[(colon + 1)..];
        switch (colon < 0 ? "" : id[..colon])
        {
            case "litellm": return ("litellm", null, rest);
            case "local":
                var slash = rest.IndexOf('/');
                return slash < 0 ? ("local", null, rest) : ("local", rest[..slash], rest[(slash + 1)..]);
            default: return ("github", null, id);
        }
    }

    private static ProviderConfig Provider(Uri baseUrl, string? key, int? output, int? prompt) => new()
    {
        Type = "openai", WireApi = "completions", BaseUrl = baseUrl.ToString().TrimEnd('/'), ApiKey = key,
        MaxOutputTokens = output, MaxPromptTokens = prompt
    };

    private static (AppEndpoint? App, string? Problem) LiteLlm(IReadOnlyList<AppEndpoint> endpoints)
    {
        var all = endpoints.Where(item => item.Template == "litellm").ToArray();
        return all.FirstOrDefault(item => item.Problem is null) is { } app ? (app, null)
            : (null, all.Length == 0 ? "Install the LiteLLM app to use its models." : all[0].Problem);
    }

    private async Task<AssistantSource> LiteLlmSourceAsync(string owner, IReadOnlyList<AppEndpoint> endpoints, CancellationToken ct)
    {
        var (app, problem) = LiteLlm(endpoints);
        if (app is null) return new("litellm", "LiteLLM", [], problem);
        try
        {
            var models = (await LiteLlmModelsAsync(owner, app, ct)).Models.Select(item => new AssistantModel("litellm:" + item.Id, item.Id)).ToArray();
            return new("litellm", "LiteLLM", models, models.Length == 0 ? "LiteLLM has no models yet. Add one on its admin page." : null);
        }
        catch (Exception e) when (Unreachable(e, ct)) { return new("litellm", "LiteLLM", [], Failure(app, "LiteLLM", e)); }
    }

    private async Task<(string Key, ServedModel[] Models)> LiteLlmModelsAsync(string owner, AppEndpoint app, CancellationToken ct)
    {
        var key = await KeyAsync(owner, app, null, ct);
        JsonElement list;
        try { list = await SendAsync(HttpMethod.Get, app.BaseUri!, "v1/models", key, null, ct); }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            key = await KeyAsync(owner, app, key, ct);
            list = await SendAsync(HttpMethod.Get, app.BaseUri!, "v1/models", key, null, ct);
        }
        return (key, Models(list, "litellm:"));
    }

    private async Task<AssistantSource> LocalSourceAsync(IReadOnlyList<AppEndpoint> endpoints, CancellationToken ct)
    {
        var models = new List<AssistantModel>();
        var problems = new List<string>();
        var host = spark();
        if (host.Problem is { } problem) problems.Add(problem);
        else if (host.Model is { } model && ModelPattern().IsMatch("local:" + model)) models.Add(new("local:" + model, $"{model} · Spark"));
        var nodes = endpoints.Where(item => item.Template == "local-ai").ToArray();
        var served = await Task.WhenAll(nodes.Select(node => ServedAsync(node, ct)));
        for (var index = 0; index < nodes.Length; index++)
        {
            models.AddRange(served[index].Models.Select(item => new AssistantModel($"local:{nodes[index].Stack}/{item.Id}", $"{item.Id} · {nodes[index].Node}")));
            if (served[index].Problem is { } reason) problems.Add(reason);
        }
        return new("local", "Local AI", [.. models], problems.Count == 0 ? null : string.Join(" ", problems));
    }

    private async Task<(ServedModel[] Models, string? Problem)> ServedAsync(AppEndpoint node, CancellationToken ct)
    {
        if (node.Problem is { } problem) return ([], problem);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var models = Models(await SendAsync(HttpMethod.Get, node.BaseUri!, "v1/models", node.Key, null, timeout.Token), $"local:{node.Stack}/");
            return (models, models.Length == 0 ? $"Local AI on {node.Node} has no chat model loaded." : null);
        }
        catch (Exception e) when (Unreachable(e, ct)) { return ([], Failure(node, "Local AI", e)); }
    }

    /// <summary>The owner's saved LiteLLM key, or a new one when there is none or LiteLLM refused <paramref name="rejected"/>.</summary>
    private async Task<string> KeyAsync(string owner, AppEndpoint app, string? rejected, CancellationToken ct)
    {
        if (rejected is null && Read(owner) is { } saved) return saved;
        var gate = _gates.GetOrAdd(Check(owner), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (Read(owner) is { } current && current != rejected) return current;
            var alias = "lucia-assistant-" + owner;
            // LiteLLM keeps aliases unique, so drop the old key first. An error status means there was none.
            try { await SendAsync(HttpMethod.Post, app.BaseUri!, "key/delete", app.Key, new { key_aliases = new[] { alias } }, ct); }
            catch (HttpRequestException e) when (e.StatusCode is not null) { }
            var reply = await SendAsync(HttpMethod.Post, app.BaseUri!, "key/generate", app.Key,
                new { key_alias = alias, metadata = new { lucia = "assistant", owner } }, ct);
            var key = reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("key", out var value) && value.GetString() is { Length: > 0 and <= 4096 } minted
                ? minted : throw new InvalidDataException("LiteLLM did not return a key.");
            await DomainOnboardingStore.WriteJson(Saved(owner), new Envelope(1, Protector(owner).Protect(key)), json: Json);
            return key;
        }
        finally { gate.Release(); }
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, Uri baseUri, string path, string? key, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct), new JsonDocumentOptions { MaxDepth = 16 });
        return document.RootElement.Clone();
    }

    /// <summary>Chat models from an OpenAI <c>/v1/models</c> list. Lucia's engine marks capabilities; vLLM reports its context.</summary>
    private static ServedModel[] Models(JsonElement list, string prefix) =>
        list.ValueKind == JsonValueKind.Object && list.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object && (!item.TryGetProperty("capabilities", out var capabilities)
                    || capabilities.ValueKind != JsonValueKind.Array || capabilities.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "chat")))
                .Select(item => new ServedModel(item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString()! : "",
                    Number(item, "context_length") ?? Number(item, "max_model_len"), Number(item, "max_output_tokens")))
                .Where(item => ModelPattern().IsMatch(prefix + item.Id)).DistinctBy(item => item.Id).Take(200).ToArray()
            : [];

    private static int? Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0 ? number : null;

    private static bool Unreachable(Exception e, CancellationToken ct) =>
        e is HttpRequestException or JsonException or InvalidDataException || e is OperationCanceledException && !ct.IsCancellationRequested;

    private string Failure(AppEndpoint app, string what, Exception e)
    {
        logger.LogWarning("{App} on {Node} could not list its models ({ErrorType}).", what, app.Node, e.GetType().Name);
        return e is HttpRequestException { StatusCode: { } status }
            ? $"{what} on {app.Node} answered with HTTP {(int)status}. Check its logs in Apps."
            : $"{what} on {app.Node} isn't answering.";
    }

    private static AssistantException Unavailable(string message) => new(503, "assistant_source_unavailable", message);

    private string Saved(string owner) => Path.Combine(Path.GetFullPath(options.Value.Directory), "litellm", Check(owner) + ".json");

    // The owner is part of the purpose, so one owner's file copied over another's does not decrypt.
    private IDataProtector Protector(string owner) => protection.CreateProtector("Lucia.Homelab.AssistantLiteLlm.v1", owner);

    private string? Read(string owner)
    {
        var path = Saved(owner);
        try
        {
            if (!File.Exists(path)) return null;
            DomainOnboardingStore.RejectLinks(path);
            if (new FileInfo(path).Length > 65_536) throw new InvalidDataException("The saved key is too large.");
            return JsonSerializer.Deserialize<Envelope>(File.ReadAllBytes(path), Json) is { Version: 1, Data.Length: > 0 } envelope
                ? Protector(owner).Unprotect(envelope.Data) : throw new InvalidDataException("The saved key has an unknown format.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidDataException)
        {
            logger.LogWarning("A saved LiteLLM key could not be read ({ErrorType}); Lucia will make a new one.", e.GetType().Name);
            return null;
        }
    }

    private static string Check(string owner) =>
        OwnerPattern().IsMatch(owner) ? owner : throw new ArgumentException("The owner key is invalid.", nameof(owner));

    private sealed record Envelope(int Version, string Data);

    private sealed record ServedModel(string Id, int? Context, int? Output);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex OwnerPattern();

    [GeneratedRegex("^[A-Za-z0-9._:/-]{1,100}$")]
    private static partial Regex ModelPattern();
}
