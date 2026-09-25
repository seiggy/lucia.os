using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Lucia.Homelab.Server.Host;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Disposable check data stays within the project, never in an OS temporary directory.
var storage = Path.GetFullPath(Path.Combine("tests", "Lucia.Homelab.HuggingFaceChecks", ".checks-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(storage);
const string oldToken = "hf_SyntheticOldCredentialNotLive";
const string newToken = "hf_SyntheticNewCredentialNotLive";
const string legacyToken = "hf_SyntheticLegacyCredentialNotLive";
const string sha = "1234567890abcdef1234567890abcdef12345678";
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var checks = 0;

void Check(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
    checks++;
}

void NoSecrets(string text)
{
    Check(!new[] { oldToken, newToken, legacyToken }.Any(token => text.Contains(token, StringComparison.Ordinal)), "A secret escaped a result or error.");
}

async Task<HuggingFaceManagementException> Reject(Func<Task> action, string code, int status)
{
    try { await action(); }
    catch (HuggingFaceManagementException exception)
    {
        Check(exception.Code == code && exception.StatusCode == status, $"Expected {code}/{status}, got {exception.Code}/{exception.StatusCode}.");
        NoSecrets(exception.ToString());
        return exception;
    }
    throw new InvalidOperationException($"Expected {code}.");
}

var keys = Path.Combine(storage, "keys");
Directory.CreateDirectory(keys);
var protection = DataProtectionProvider.Create(new DirectoryInfo(keys), options => options.SetApplicationName("Lucia.HuggingFaceChecks"));
var legacy = Options.Create(new HostPlatformOptions { HuggingFaceToken = legacyToken });
var settings = Options.Create(new HuggingFaceManagementOptions { CredentialsDirectory = Path.Combine(storage, "credentials") });
using var handler = new FakeHandler();
using var client = new HttpClient(handler);
var credentials = new HuggingFaceCredentialService(settings, legacy, protection, client);
var browser = new HuggingFaceBrowserService(credentials, client);

string Repository(params object[] files) => JsonSerializer.Serialize(new
{
    id = "owner/model", sha, gated = false, @private = false,
    gguf = new { architecture = "llama", context_length = 32768, chat_template = "Do not expose templates or arbitrary metadata." },
    siblings = files
});
object FileEntry(string file, long? size = 100) => new { rfilename = file, size };

try
{
    var status = await credentials.GetStatusAsync();
    Check(status is { Configured: true, Source: "legacy", AccountName: null, ValidatedAt: null }, "Legacy fallback status is wrong.");
    var active = await credentials.GetDownloadCredentialsAsync();
    Check(active.Token == legacyToken && !active.DisableImplicitCredentials, "Legacy token was not explicit.");
    NoSecrets(JsonSerializer.Serialize(active, json) + active);

    handler.Respond = _ => FakeHandler.Json("""{"type":"user","name":"test-owner","auth":{"type":"access_token"},"email":"not-returned@example.invalid"}""");
    status = await credentials.SaveAsync(oldToken);
    Check(status is { Configured: true, Source: "managed", AccountName: "test-owner", ValidatedAt: not null }, "Managed status is wrong.");
    NoSecrets(JsonSerializer.Serialize(status, json));
    Check(handler.Requests.Last() is { Uri.AbsoluteUri: "https://huggingface.co/api/whoami-v2", Token: oldToken }, "Token validation left the fixed whoami endpoint.");
    var recordPath = Path.Combine(settings.Value.CredentialsDirectory, "huggingface.json");
    var saved = await System.IO.File.ReadAllTextAsync(recordPath);
    NoSecrets(saved);
    Check(saved.Contains("protectedData") && !saved.Contains("test-owner"), "Credential payload was not fully protected.");
    Check(Directory.GetFiles(keys, "*.xml").Length > 0, "Real ephemeral Data Protection keys were not created.");
    if (!OperatingSystem.IsWindows())
    {
        Check(System.IO.File.GetUnixFileMode(settings.Value.CredentialsDirectory) ==
            (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "Credential directory is not 0700.");
        Check(System.IO.File.GetUnixFileMode(recordPath) ==
            (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Credential record is not 0600.");
    }

    handler.Respond = _ => FakeHandler.Json(newToken, HttpStatusCode.Unauthorized);
    await Reject(() => credentials.SaveAsync(newToken), "provider_token_rejected", 422);
    Check(await System.IO.File.ReadAllTextAsync(recordPath) == saved, "Failed replacement modified the previous record.");
    Check((await credentials.GetDownloadCredentialsAsync()).Token == oldToken, "Failed replacement lost the previous token.");
    handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri("https://elsewhere.invalid/" + newToken) }
    };
    await Reject(() => credentials.SaveAsync(newToken), "provider_redirect_rejected", 502);
    Check((await credentials.GetDownloadCredentialsAsync()).Token == oldToken, "Redirect changed a credential.");
    handler.Respond = _ => throw new HttpRequestException(newToken);
    await Reject(() => credentials.SaveAsync(newToken), "provider_unavailable", 502);
    handler.Respond = _ => FakeHandler.Json("""{"type":"user","name":"test-owner","extra":"hf_SyntheticNewCredentialNotLive"}""");
    await Reject(() => credentials.SaveAsync(newToken), "invalid_provider_response", 502);
    handler.Respond = _ => FakeHandler.Json("""{"type":"user","name":"test-owner","extra":"hf_\u0053yntheticNewCredentialNotLive"}""");
    await Reject(() => credentials.SaveAsync(newToken), "invalid_provider_response", 502);
    handler.Respond = _ => FakeHandler.Json(new string('x', 128 * 1024 + 1));
    await Reject(() => credentials.SaveAsync(newToken), "provider_response_too_large", 502);
    handler.Respond = _ => FakeHandler.Json("not JSON " + newToken);
    await Reject(() => credentials.SaveAsync(newToken), "invalid_provider_response", 502);
    var before = handler.Requests.Count;
    await Reject(() => credentials.SaveAsync("bad-token\r\nInjected: value"), "invalid_token_format", 400);
    await Reject(() => credentials.SaveAsync("hf_" + new string('a', 510)), "invalid_token_format", 400);
    Check(handler.Requests.Count == before, "Invalid token syntax caused network traffic.");

    status = await credentials.DisconnectAsync();
    Check(status is { Configured: false, Source: "disconnected", AccountName: null, ValidatedAt: null }, "Disconnect did not persist explicit state.");
    var restarted = new HuggingFaceCredentialService(settings, legacy,
        DataProtectionProvider.Create(new DirectoryInfo(keys), options => options.SetApplicationName("Lucia.HuggingFaceChecks")), client);
    active = await restarted.GetDownloadCredentialsAsync();
    Check(active.Token is null && active.DisableImplicitCredentials, "Disconnect reactivated the legacy token on restart.");
    handler.Respond = _ => FakeHandler.Json("""{"type":"user","name":"second-owner"}""");
    status = await restarted.SaveAsync(newToken);
    Check(status.AccountName == "second-owner" && (await credentials.GetDownloadCredentialsAsync()).Token == newToken, "Reconnect did not replace the marker.");

    await System.IO.File.WriteAllTextAsync(recordPath, "{broken " + oldToken);
    await Reject(() => credentials.GetStatusAsync(), "credential_storage_unavailable", 503);
    await Reject(() => credentials.GetDownloadCredentialsAsync(), "credential_storage_unavailable", 503);
    before = handler.Requests.Count;
    await Reject(() => credentials.SaveAsync(newToken), "credential_storage_unavailable", 503);
    Check(handler.Requests.Count == before, "Corrupt storage caused provider traffic.");
    await credentials.DisconnectAsync();
    Check((await credentials.GetDownloadCredentialsAsync()).Token is null, "Disconnect did not recover corrupt regular storage.");
    await System.IO.File.WriteAllTextAsync(recordPath, new string('x', 16385));
    await Reject(() => credentials.GetStatusAsync(), "credential_storage_unavailable", 503);
    await credentials.DisconnectAsync();
    var badKeyService = new HuggingFaceCredentialService(settings, legacy,
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(storage, "other-keys"))), client);
    await Reject(() => badKeyService.GetStatusAsync(), "credential_storage_unavailable", 503);
    await System.IO.File.WriteAllTextAsync(recordPath, """{"version":2,"protectedData":"unrecognized"}""");
    await Reject(() => credentials.GetStatusAsync(), "credential_storage_unavailable", 503);
    await credentials.DisconnectAsync();
    await credentials.SaveAsync(oldToken);

    // Symlink creation on Windows may require Developer Mode; Linux always exercises these checks.
    var target = Path.Combine(storage, "untouched.txt");
    await System.IO.File.WriteAllTextAsync(target, "untouched");
    System.IO.File.Delete(recordPath);
    var linked = false;
    try
    {
        System.IO.File.CreateSymbolicLink(recordPath, target);
        linked = true;
    }
    catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { }
    catch (IOException) when (OperatingSystem.IsWindows()) { }
    if (linked)
    {
        await Reject(() => credentials.GetStatusAsync(), "credential_storage_unavailable", 503);
        await Reject(() => credentials.DisconnectAsync(), "credential_storage_unavailable", 503);
        Check(await System.IO.File.ReadAllTextAsync(target) == "untouched", "Credential operation followed a symbolic link.");
        System.IO.File.Delete(recordPath);
    }
    await credentials.DisconnectAsync();

    handler.Respond = _ => FakeHandler.Json("""
        [{"id":"owner/model","pipeline_tag":"text-generation","downloads":42,"likes":5,"gated":"manual","private":true},
         {"id":"../invalid","downloads":1},{"id":"owner/model","downloads":100}]
        """);
    var search = await browser.SearchAsync("  small model  ", ModelKind.Chat);
    Check(search.Query == "small model" && search.Items.Count == 1 && search.Items[0] is { Gated: true, Private: true, Downloads: 42 },
        "Search fields, deduplication or identifiers are wrong.");
    Check(search.Compatibility == "unverified", "Search implied compatibility.");
    var request = handler.Requests.Last();
    Check(request.Uri.Query.Contains("filter=gguf") && request.Uri.Query.Contains("pipeline_tag=text-generation")
        && request.Uri.Query.Contains("limit=30") && request.Token is null, "Search was not bounded or used an implicit credential.");
    await browser.SearchAsync("embedding", ModelKind.Embedding);
    Check(handler.Requests.Last().Uri.Query.Contains("pipeline_tag=feature-extraction"), "Embedding intent was lost.");
    before = handler.Requests.Count;
    foreach (var query in new[] { "", " ", "\nmodel", new string('x', 101), oldToken })
        await Reject(() => browser.SearchAsync(query, ModelKind.Chat), "invalid_query", 400);
    await Reject(() => browser.SearchAsync("model", (ModelKind)99), "invalid_kind", 400);
    Check(handler.Requests.Count == before, "Invalid search caused network requests.");
    handler.Respond = _ => FakeHandler.Json(JsonSerializer.Serialize(
        Enumerable.Range(0, 70).Select(i => new { id = "owner/model" + i })));
    search = await browser.SearchAsync("model", ModelKind.Chat);
    Check(search.Items.Count == 30 && search.LimitReached, "Search result count was not bounded.");
    before = handler.Requests.Count;
    handler.Respond = _ =>
    {
        var response = FakeHandler.Json("[]");
        response.Headers.TryAddWithoutValidation("Link", "<https://elsewhere.invalid/private>; rel=\"next\"");
        return response;
    };
    await browser.SearchAsync("model", ModelKind.Chat);
    Check(handler.Requests.Count == before + 1, "Browser followed an untrusted pagination link.");

    handler.Respond = _ => FakeHandler.Json(Repository(
        FileEntry("model-Q4_K_M.gguf", 1000),
        FileEntry("quant/model-Q8_0-00003-of-00003.gguf", 30),
        FileEntry("quant/model-Q8_0-00001-of-00003.gguf", 10),
        FileEntry("quant/model-Q8_0-00002-of-00003.gguf", 20),
        FileEntry("incomplete-Q5_K_M-00001-of-00002.gguf"),
        FileEntry("missing-first-Q5_K_M-00002-of-00002.gguf"),
        FileEntry("oversize-Q5_K_M-00001-of-00129.gguf"),
        FileEntry("broken-Q5_K_M-1-of-2.gguf"),
        FileEntry("mmproj-F16.gguf"), FileEntry("adapter.gguf"),
        FileEntry("lora-Q8_0.gguf"), FileEntry("nested/projector.gguf"),
        FileEntry("../escape.gguf"), FileEntry("/absolute.gguf"), FileEntry("-option.gguf"),
        FileEntry("missing-size.gguf", null), FileEntry("README.md"),
        new { rfilename = "model-F16.gguf", lfs = new { size = 2000 } }));
    var repository = await browser.GetRepositoryAsync("owner/model", "feature/quant", ModelKind.Embedding);
    Check(repository.Revision == sha && repository.RequestedRevision == "feature/quant" && repository.Choices.Count == 3,
        "Revision or standalone choice filtering is wrong.");
    Check(handler.Requests.Last().Uri.AbsoluteUri.Contains("/revision/feature%2Fquant?blobs=true"), "Revision was not safely encoded.");
    var split = repository.Choices.Single(choice => choice.Files.Count == 3);
    Check(split.File.EndsWith("00001-of-00003.gguf") && split.TotalSizeBytes == 60
        && split.Files.Select(part => part.SizeBytes).SequenceEqual(new long[] { 10, 20, 30 }), "Split shard grouping or total size is wrong.");
    Check(split.Quantization == "Q8_0" && split.LabelSource == "filename_inferred" && split.Compatibility == "unverified", "Quantization inference was not labeled.");
    Check(repository.Choices.All(choice => choice.Download is
        { Provider: "huggingface", Repository: "owner/model", Kind: ModelKind.Embedding, Revision: sha, Pro: false }
        && ModelCatalog.Validate(choice.Download) is null), "Download request shape is not catalog-compatible.");
    Check(repository.GgufMetadata is { Architecture: "llama", ContextLength: 32768, Source: "huggingface_api", Scope: "repository" },
        "Optional official GGUF metadata was not exposed.");
    Check(repository.Warnings.Any(warning => warning.Contains("Incomplete")) && repository.Warnings.Any(warning => warning.Contains("Projector")),
        "Excluded choices were not explained.");
    NoSecrets(JsonSerializer.Serialize(repository, json));
    Check(!JsonSerializer.Serialize(repository, json).Contains("chat_template"), "Arbitrary provider fields escaped.");

    handler.Respond = _ => FakeHandler.Json(Repository(FileEntry("weights.safetensors"), FileEntry("mmproj.gguf")));
    repository = await browser.GetRepositoryAsync("owner/model");
    Check(repository.Availability == "no_standalone_gguf" && repository.Choices.Count == 0, "Unsupported repository was presented as loadable.");
    handler.Respond = _ => FakeHandler.Json(Repository(Enumerable.Range(1, 128)
        .Select(i => FileEntry($"model-Q4_K_M-{i:D5}-of-00128.gguf", i)).ToArray()));
    repository = await browser.GetRepositoryAsync("owner/model");
    Check(repository.Choices.Single().Files.Count == 128 && repository.Choices.Single().TotalSizeBytes == 8256, "128-shard boundary failed.");
    handler.Respond = _ => FakeHandler.Json(Repository(FileEntry("model-Q4_K_M-00001-of-00002.gguf", long.MaxValue),
        FileEntry("model-Q4_K_M-00002-of-00002.gguf", 1)));
    await Reject(() => browser.GetRepositoryAsync("owner/model"), "invalid_provider_response", 502);
    handler.Respond = _ => FakeHandler.Json(Repository(FileEntry("dup.gguf"), FileEntry("dup.gguf")));
    await Reject(() => browser.GetRepositoryAsync("owner/model"), "invalid_provider_response", 502);
    handler.Respond = _ => FakeHandler.Json(Repository(Enumerable.Range(0, 4097).Select(i => FileEntry($"f{i}.txt")).ToArray()));
    await Reject(() => browser.GetRepositoryAsync("owner/model"), "repository_too_large", 422);
    handler.Respond = _ => FakeHandler.Json(Repository(Enumerable.Range(0, 257).Select(i => FileEntry($"f{i}.gguf")).ToArray()));
    await Reject(() => browser.GetRepositoryAsync("owner/model"), "too_many_model_choices", 422);
    handler.Respond = _ => FakeHandler.Json(Repository(FileEntry("model.gguf")).Replace(sha, "main"));
    await Reject(() => browser.GetRepositoryAsync("owner/model"), "invalid_provider_response", 502);
    handler.Respond = _ => FakeHandler.Json(Repository(FileEntry("model.gguf")));
    await Reject(() => browser.GetRepositoryAsync("owner/model", new string('a', 40)), "invalid_provider_response", 502);
    before = handler.Requests.Count;
    foreach (var name in new[] { "https://other.invalid/model", "owner/model/extra", "owner/../model", "owner/-option", "owner/model?token=x" })
        await Reject(() => browser.GetRepositoryAsync(name), "invalid_repository", 400);
    foreach (var revision in new[] { "", "../main", "-option", "main?token=x", "main#fragment", "feature//x", "main\\file", "refs/../main" })
        await Reject(() => browser.GetRepositoryAsync("owner/model", revision), "invalid_revision", 400);
    Check(handler.Requests.Count == before, "Invalid repository or revision caused traffic.");

    handler.Respond = _ => FakeHandler.Json(oldToken, HttpStatusCode.Forbidden);
    await Reject(() => browser.GetRepositoryAsync("owner/model"), "provider_access_denied", 403);
    handler.Respond = _ => FakeHandler.Json(oldToken, HttpStatusCode.Unauthorized);
    await Reject(() => browser.SearchAsync("model", ModelKind.Chat), "provider_access_denied", 403);
    handler.Respond = _ => FakeHandler.Json(oldToken, HttpStatusCode.NotFound);
    await Reject(() => browser.GetRepositoryAsync("owner/model"), "provider_not_found", 404);
    handler.Respond = _ =>
    {
        var response = FakeHandler.Json(oldToken, HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
        return response;
    };
    var limited = await Reject(() => browser.SearchAsync("model", ModelKind.Chat), "provider_rate_limited", 429);
    Check(limited.RetryAfterSeconds == 90, "Retry-After was not preserved.");
    handler.Respond = _ => FakeHandler.Json(oldToken, HttpStatusCode.ServiceUnavailable);
    await Reject(() => browser.GetRepositoryAsync("owner/model"), "provider_unavailable", 502);
    handler.Respond = _ => throw new OperationCanceledException(newToken);
    await Reject(() => browser.SearchAsync("model", ModelKind.Chat), "provider_timeout", 504);
    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        try { await browser.SearchAsync("model", ModelKind.Chat, canceled.Token); throw new InvalidOperationException("Cancellation was ignored."); }
        catch (OperationCanceledException) { checks++; }
    }
    handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 256 * 1024 + 1))))
    };
    await Reject(() => browser.SearchAsync("model", ModelKind.Chat), "provider_response_too_large", 502);
    Check(handler.Requests.All(item => item.Uri.Scheme == "https" && item.Uri.Host == "huggingface.co"
        && !new[] { oldToken, newToken, legacyToken }.Any(secret => item.Uri.AbsoluteUri.Contains(secret))), "A token left the fixed HTTPS origin/header boundary.");

    // Real local endpoints exercise owner authorization, no-store, safe body parsing, and the endpoint contract.
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
    builder.Services.Configure<HuggingFaceManagementOptions>(options => options.CredentialsDirectory = settings.Value.CredentialsDirectory);
    builder.Services.AddSingleton<IOptions<HostPlatformOptions>>(legacy);
    builder.Services.AddSingleton<IDataProtectionProvider>(protection);
    builder.AddHuggingFaceManagement();
    using (var registered = builder.Services.BuildServiceProvider())
    {
        var registeredService = registered.GetRequiredService<HuggingFaceCredentialService>();
        Check((await registeredService.GetStatusAsync()).Source == "disconnected", "Production service registration failed.");
        Check(registered.GetRequiredService<HuggingFaceBrowserService>() is not null, "Browser registration failed.");
        Check(registered.GetRequiredService<HuggingFacePreviewService>() is not null, "Preview registration failed.");
    }
    builder.Services.AddSingleton(credentials);
    builder.Services.AddSingleton(browser);
    builder.Services.AddAuthentication("Check").AddScheme<AuthenticationSchemeOptions, CheckAuthentication>("Check", _ => { });
    builder.Services.AddAuthorizationBuilder().AddPolicy("HostOwner", policy => policy.RequireAuthenticatedUser().RequireRole("Owner"));
    await using var app = builder.Build();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapHuggingFaceManagement();
    await app.StartAsync();
    try
    {
        using var local = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using (var response = await local.GetAsync("/api/host/huggingface/credentials"))
            Check(response.StatusCode == HttpStatusCode.Unauthorized, "Anonymous credentials request was accepted.");
        local.DefaultRequestHeaders.Add("X-Check-Role", "Inference");
        using (var response = await local.GetAsync("/api/host/huggingface/credentials"))
            Check(response.StatusCode == HttpStatusCode.Forbidden, "Inference caller accessed owner credentials.");
        local.DefaultRequestHeaders.Remove("X-Check-Role");
        local.DefaultRequestHeaders.Add("X-Check-Role", "Owner");
        handler.Respond = _ => FakeHandler.Json("""{"type":"user","name":"endpoint-owner"}""");
        using (var response = await local.PutAsJsonAsync("/api/host/huggingface/credentials", new { token = oldToken }))
        {
            Check(response.IsSuccessStatusCode && response.Headers.CacheControl?.NoStore == true, "Credential save endpoint failed/no-store missing.");
            var body = await response.Content.ReadAsStringAsync();
            NoSecrets(body);
            using var result = JsonDocument.Parse(body);
            Check(result.RootElement.GetProperty("accountName").GetString() == "endpoint-owner"
                && result.RootElement.EnumerateObject().Count() == 4, "Credential status shape changed.");
        }
        handler.Respond = _ => FakeHandler.Json(newToken, HttpStatusCode.Unauthorized);
        using (var response = await local.PutAsJsonAsync("/api/host/huggingface/credentials", new { token = newToken }))
        {
            Check((int)response.StatusCode == 422 && response.Headers.CacheControl?.NoStore == true, "Provider rejection became a Lucia session 401.");
            NoSecrets(await response.Content.ReadAsStringAsync());
        }
        using (var response = await local.PutAsync("/api/host/huggingface/credentials", new StringContent("{ " + newToken, Encoding.UTF8, "application/json")))
        {
            Check(response.StatusCode == HttpStatusCode.BadRequest, "Malformed JSON was accepted.");
            NoSecrets(await response.Content.ReadAsStringAsync());
        }
        using (var response = await local.PutAsync("/api/host/huggingface/credentials", new StringContent(new string('x', 4097), Encoding.UTF8, "application/json")))
            Check((int)response.StatusCode == 413, "Credential request body was not bounded.");
        using (var response = await local.GetAsync("/api/host/huggingface/search?query=model&kind=0"))
            Check(response.StatusCode == HttpStatusCode.BadRequest, "Numeric enum input was accepted.");
        handler.Respond = _ => FakeHandler.Json(Repository(FileEntry("model-Q4_K_M.gguf")));
        using (var response = await local.GetAsync("/api/host/huggingface/repository?repository=owner%2Fmodel&kind=Embedding"))
        {
            Check(response.IsSuccessStatusCode && response.Headers.CacheControl?.NoStore == true, "Repository endpoint failed.");
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var download = result.RootElement.GetProperty("choices")[0].GetProperty("download");
            Check(download.GetProperty("kind").GetString() == "Embedding" && download.GetProperty("revision").GetString() == sha
                && !download.GetProperty("pro").GetBoolean(), "Download endpoint JSON shape changed.");
        }
        using (var response = await local.DeleteAsync("/api/host/huggingface/credentials"))
            Check(response.IsSuccessStatusCode && (await credentials.GetDownloadCredentialsAsync()).DisableImplicitCredentials, "Disconnect endpoint failed.");
        var endpointData = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToArray();
        Check(endpointData.Length == 6 && endpointData.All(endpoint => endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
            .Any(data => data.Policy == "HostOwner")), "An endpoint is missing HostOwner.");
    }
    finally { await app.StopAsync(); }
    checks += await PreviewChecks.RunAsync(storage);
    Console.WriteLine($"Hugging Face management: {checks} checks passed. No live tokens, model downloads, or GPU use.");
}
finally
{
    Directory.Delete(storage, recursive: true);
}

sealed class FakeHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Json("[]");
    public List<(Uri Uri, string? Token)> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add((request.RequestUri!, request.Headers.Authorization?.Parameter));
        return Task.FromResult(Respond(request));
    }
    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
}

sealed class CheckAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers["X-Check-Role"].ToString();
        return Task.FromResult(role.Length == 0 ? AuthenticateResult.NoResult()
            : AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, "checks"), new Claim(ClaimTypes.Role, role)], Scheme.Name)), Scheme.Name)));
    }
}
