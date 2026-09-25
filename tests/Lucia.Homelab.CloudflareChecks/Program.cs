using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.AccessControl;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Lucia.Homelab.Server.Domains;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;

const string account = "0123456789abcdef0123456789abcdef";
const string otherAccount = "fedcba9876543210fedcba9876543210";
const string zoneId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const string oldToken = "opaque.Synthetic-Account+Token/NoFixedPrefix=__NOT_LIVE";
const string newToken = "cfat_SyntheticReplacement__NOT_LIVE";
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var checks = 0;
var storage = Path.GetFullPath(Path.Combine("tests", "Lucia.Homelab.CloudflareChecks", ".checks-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(storage);

void Check(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
    checks++;
}

void NoSecrets(string text) =>
    Check(!text.Contains(oldToken, StringComparison.Ordinal) && !text.Contains(newToken, StringComparison.Ordinal),
        "A secret escaped encryption, serialization, logs or safe errors.");

async Task<CloudflareDomainException> Reject(Func<Task> action, string code, int status)
{
    try { await action(); }
    catch (CloudflareDomainException e)
    {
        Check(e.Code == code && e.StatusCode == status, $"Expected {code}/{status}; got {e.Code}/{e.StatusCode}.");
        NoSecrets(e.ToString());
        return e;
    }
    throw new InvalidOperationException($"Expected {code}.");
}

object Zone(string id = zoneId, string name = "Example.COM", string owner = account, string status = "active",
    string type = "full", object? nameServers = null) =>
    new { id, name, status, type, account = new { id = owner, name = "Private account name never in public DTO" },
        name_servers = nameServers ?? new[] { "ALICE.NS.CLOUDFLARE.COM", "bob.ns.cloudflare.com" } };
HttpResponseMessage Envelope(object result) => FakeHandler.Json(JsonSerializer.Serialize(new { success = true, result }));
HttpResponseMessage Verification(string status = "active", string? notBefore = null, string? expires = null) =>
    Envelope(new { id = "provider-token-id-not-returned", status, not_before = notBefore, expires_on = expires });
HttpResponseMessage Page(object[] result, int total = 1, int page = 1, int? pages = null, int? count = null) =>
    FakeHandler.Json(JsonSerializer.Serialize(new { success = true, result,
        result_info = new { page, per_page = 50, total_pages = pages ?? Math.Max(1, (total + 49) / 50), total_count = total,
            count = count ?? result.Length } }));

var settings = Options.Create(new CloudflareDomainOptions { CredentialsDirectory = Path.Combine(storage, "credentials") });
var keys = Path.Combine(storage, "keys");
Directory.CreateDirectory(keys);
var protection = DataProtectionProvider.Create(new DirectoryInfo(keys), b => b.SetApplicationName("CloudflareChecks"));
using var handler = new FakeHandler();
using var client = new HttpClient(handler);
var service = new CloudflareDomainService(settings, protection, client);
var recordPath = Path.Combine(settings.Value.CredentialsDirectory, "cloudflare-domains.json");

HttpResponseMessage Success(HttpRequestMessage request)
{
    if (request.RequestUri!.AbsolutePath == $"/client/v4/accounts/{account}/tokens/verify") return Verification();
    if (request.RequestUri.AbsolutePath == "/client/v4/zones") return Page([Zone()]);
    if (request.RequestUri.AbsolutePath == $"/client/v4/zones/{zoneId}") return Envelope(Zone());
    throw new InvalidOperationException("Unexpected fake provider path.");
}
handler.Respond = Success;
try
{
    var status = await service.GetStatusAsync();
    Check(status == new CloudflareCredentialStatus(false, null, null, null, 0), "Fresh storage did not start disconnected.");
    Check((await service.GetTokenAsync()).Token is null, "Implicit credentials were used.");
    await Reject(() => service.ListZonesAsync(), "cloudflare_not_connected", 409);
    Check(handler.Requests.Count == 0, "Disconnected status performed network access.");

    status = await service.SaveAsync(new(account.ToUpperInvariant(), oldToken));
    Check(status is { Configured: true, AccountId: account, VerifiedAt: not null, ExpiresAt: null, ZoneCount: 1 },
        "Saved status is incorrect.");
    Check(handler.Requests.Count == 2, "Save did not verify and discover before persistence.");
    Check(handler.Requests[0].Uri.AbsoluteUri == $"https://api.cloudflare.com/client/v4/accounts/{account}/tokens/verify",
        "Used user-token verification instead of account-token verification.");
    var query = QueryHelpers.ParseQuery(handler.Requests[1].Uri.Query);
    Check(query["account.id"] == account && query["status"] == "active" && query["type"] == "full"
        && query["match"] == "all" && query["page"] == "1" && query["per_page"] == "50", "Zone discovery filters are missing.");
    Check(handler.Requests.All(r => r.Method == HttpMethod.Get && r.Token == oldToken && r.Suppressed), "Transport leaked instrumentation or issued a write.");
    var secret = await service.GetTokenAsync();
    Check(secret.Token == oldToken && secret.AccountId == account, "Server token unavailable.");
    NoSecrets(JsonSerializer.Serialize(status, json) + JsonSerializer.Serialize(secret, json) + secret
        + JsonSerializer.Serialize(new CloudflareCredentialRequest(account, oldToken), json) + new CloudflareCredentialRequest(account, oldToken));
    var saved = await File.ReadAllTextAsync(recordPath);
    NoSecrets(saved);
    Check(saved.Contains("protectedData") && !saved.Contains(account), "Record is not protected in full.");
    Check(Directory.GetFiles(keys, "*.xml").Length > 0, "Checks did not use real persisted Data Protection.");
    Check(Directory.GetFiles(settings.Value.CredentialsDirectory).Length == 1, "More than one credential record persisted.");
    if (OperatingSystem.IsWindows())
    {
        var user = WindowsIdentity.GetCurrent().User!;
        foreach (var acl in new FileSystemSecurity[] { new FileInfo(recordPath).GetAccessControl(), new DirectoryInfo(settings.Value.CredentialsDirectory).GetAccessControl() })
        {
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            Check(acl.AreAccessRulesProtected && rules.Length == 1 && rules[0].IdentityReference == user
                && rules[0].AccessControlType == AccessControlType.Allow && rules[0].FileSystemRights.HasFlag(FileSystemRights.FullControl),
                "Credentials are not restricted to the current Windows user.");
        }
    }
    else
    {
        Check(File.GetUnixFileMode(recordPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Record mode is not 0600.");
        Check(File.GetUnixFileMode(settings.Value.CredentialsDirectory) ==
            (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "Directory mode is not 0700.");
    }

    var zones = await service.ListZonesAsync();
    Check(zones.Count == 1 && zones[0].Name == "example.com" && zones[0].NameServers[0] == "alice.ns.cloudflare.com", "Zone canonicalization failed.");
    Check(!JsonSerializer.Serialize(zones, json).Contains("Private account"), "Private account data escaped.");
    var zone = await service.GetZoneAsync(zoneId);
    Check(zone.Id == zoneId && handler.Requests.Last().Uri.AbsolutePath == $"/client/v4/zones/{zoneId}", "Selected zone was not revalidated.");
    handler.Respond = _ => Envelope(Zone(name: "BÜCHER.Example"));
    Check((await service.GetZoneAsync(zoneId)).Name == "xn--bcher-kva.example", "IDNA zone normalization failed.");
    handler.Respond = _ => Envelope(Zone(name: "BÜCHER\u3002Example"));
    Check((await service.GetZoneAsync(zoneId)).Name == "xn--bcher-kva.example", "IDNA dot normalization failed.");
    handler.Respond = _ => Envelope(Zone(owner: otherAccount));
    await Reject(() => service.GetZoneAsync(zoneId), "zone_account_mismatch", 422);
    foreach (var (state, type) in new[] { ("pending", "full"), ("active", "partial"), ("active", "secondary") })
    {
        handler.Respond = _ => Envelope(Zone(status: state, type: type));
        await Reject(() => service.GetZoneAsync(zoneId), "zone_not_authoritative", 422);
    }
    foreach (var name in new[] { "bad..example.com", "-bad.example", "bad-.example", "https://example.com", "exam ple.com", "*.example.com", "_x.example.com", "127.0.0.1" })
    {
        handler.Respond = _ => Envelope(Zone(name: name));
        await Reject(() => service.GetZoneAsync(zoneId), "invalid_provider_response", 502);
    }
    foreach (var servers in new object[] { "alice.ns.cloudflare.com", Array.Empty<string>(), new[] { "https://evil.invalid" }, new[] { "ns.example", "NS.EXAMPLE" }, new object[] { 4 } })
    {
        handler.Respond = _ => Envelope(Zone(nameServers: servers));
        await Reject(() => service.GetZoneAsync(zoneId), "invalid_provider_response", 502);
    }

    handler.Respond = request =>
    {
        var p = int.Parse(QueryHelpers.ParseQuery(request.RequestUri!.Query)["page"].ToString());
        return Page(Enumerable.Range(p == 1 ? 1 : 51, p == 1 ? 50 : 1)
            .Select(i => Zone(i.ToString("x32"), $"zone{i}.example")).ToArray(), 51, p);
    };
    var before = handler.Requests.Count;
    zones = await service.ListZonesAsync();
    Check(zones.Count == 51 && handler.Requests.Count == before + 2, "Pagination truncated an accessible zone.");
    Check(handler.Requests.Skip(before).All(r => QueryHelpers.ParseQuery(r.Uri.Query)["account.id"] == account), "Account filter vanished on page 2.");
    foreach (var (response, expectedCode, expectedStatus) in new (Func<HttpResponseMessage>, string, int)[] {
        (() => Page([Zone()], 1001, pages: 21), "incomplete_provider_listing", 502),
        (() => Page([Zone()], 2), "incomplete_provider_listing", 502),
        (() => Page([Zone()], page: 2), "incomplete_provider_listing", 502),
        (() => Page([Zone()], count: 0), "incomplete_provider_listing", 502),
        (() => Page([Zone(), Zone()], 2), "incomplete_provider_listing", 502),
        (() => Page([Zone(owner: otherAccount)]), "zone_account_mismatch", 422),
        (() => FakeHandler.Json("""{"success":true,"result":[]}"""), "incomplete_provider_listing", 502)
    })
    {
        handler.Respond = _ => response();
        await Reject(() => service.ListZonesAsync(), expectedCode, expectedStatus);
    }
    handler.Respond = request =>
    {
        var p = int.Parse(QueryHelpers.ParseQuery(request.RequestUri!.Query)["page"].ToString());
        return Page(Enumerable.Range(1, p == 1 ? 50 : 1).Select(i => Zone(i.ToString("x32"), $"z{i}.example")).ToArray(), p == 1 ? 51 : 52, p);
    };
    await Reject(() => service.ListZonesAsync(), "incomplete_provider_listing", 502);
    handler.Respond = _ => Page([], 0, pages: 0);
    Check((await service.ListZonesAsync()).Count == 0, "Empty listings are not supported.");

    foreach (var httpStatus in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
    {
        handler.Respond = _ => FakeHandler.Json(newToken, httpStatus);
        await Reject(() => service.SaveAsync(new(account, newToken)), "provider_token_rejected", 422);
        Check(await File.ReadAllTextAsync(recordPath) == saved && (await service.GetTokenAsync()).Token == oldToken, "Rejected replacement changed credentials.");
    }
    handler.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/verify", StringComparison.Ordinal)
        ? Verification() : FakeHandler.Json(newToken, HttpStatusCode.Forbidden);
    await Reject(() => service.SaveAsync(new(account, newToken)), "provider_token_rejected", 422);
    Check(await File.ReadAllTextAsync(recordPath) == saved, "Discovery failure replaced verified credentials.");
    handler.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/verify", StringComparison.Ordinal) ? Verification() : Page([], 0);
    await Reject(() => service.SaveAsync(new(account, newToken)), "no_accessible_zones", 422);
    handler.Respond = _ => Verification("disabled");
    await Reject(() => service.SaveAsync(new(account, newToken)), "provider_token_inactive", 422);
    handler.Respond = _ => Verification(notBefore: DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
    await Reject(() => service.SaveAsync(new(account, newToken)), "provider_token_not_yet_valid", 422);
    handler.Respond = _ => Verification(expires: DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O"));
    await Reject(() => service.SaveAsync(new(account, newToken)), "provider_token_expired", 422);
    handler.Respond = _ => Verification(expires: "invalid " + newToken);
    await Reject(() => service.SaveAsync(new(account, newToken)), "invalid_provider_response", 502);
    Check(await File.ReadAllTextAsync(recordPath) == saved, "Expiry check changed existing credentials.");

    handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://redirect.invalid/" + newToken) } };
    before = handler.Requests.Count;
    await Reject(() => service.SaveAsync(new(account, newToken)), "provider_redirect_rejected", 502);
    Check(handler.Requests.Count == before + 1, "Followed a provider redirect.");
    using (var native = CloudflareHttp.CreateHandler())
    {
        Check(!native.AllowAutoRedirect && !native.UseCookies && !native.UseProxy && native.Credentials is null
            && native.ActivityHeadersPropagator is null && native.SslOptions.RemoteCertificateValidationCallback is null,
            "Production transport permits credential forwarding, proxying, ambient credentials or custom trust.");
    }
    handler.Respond = _ => throw new HttpRequestException(newToken);
    await Reject(() => service.SaveAsync(new(account, newToken)), "provider_unavailable", 502);
    handler.Respond = _ => FakeHandler.Json("""{"success":false,"errors":[{"message":"cfat_SyntheticReplacement__NOT_LIVE"}]}""");
    await Reject(() => service.SaveAsync(new(account, newToken)), "provider_token_rejected", 422);
    handler.Respond = _ => FakeHandler.Json("bad JSON " + newToken);
    await Reject(() => service.SaveAsync(new(account, newToken)), "invalid_provider_response", 502);
    handler.Respond = _ => Envelope(new { status = "active", private_echo = newToken });
    await Reject(() => service.SaveAsync(new(account, newToken)), "invalid_provider_response", 502);
    handler.Respond = _ => FakeHandler.Json(new string('x', 1_000_001));
    await Reject(() => service.SaveAsync(new(account, newToken)), "provider_response_too_large", 502);
    handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(new byte[1_000_001])) };
    await Reject(() => service.SaveAsync(new(account, newToken)), "provider_response_too_large", 502);
    handler.Respond = _ =>
    {
        var response = FakeHandler.Json(newToken, HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(3));
        return response;
    };
    Check((await Reject(() => service.SaveAsync(new(account, newToken)), "provider_rate_limited", 429)).RetryAfterSeconds == 3600,
        "Retry-After was not bounded.");
    using (var slow = new FakeHandler { AsyncRespond = async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Verification(); } })
    using (var impatient = new HttpClient(slow) { Timeout = TimeSpan.FromMilliseconds(40) })
    {
        await Reject(() => new CloudflareDomainService(settings, protection, impatient).SaveAsync(new(account, newToken)), "provider_timeout", 504);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await service.GetStatusAsync(cancelled.Token); throw new InvalidOperationException("Ignored caller cancellation."); }
        catch (OperationCanceledException) { checks++; }
    }
    handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) };
    await Reject(() => service.SaveAsync(new(account, newToken)), "provider_timeout", 504);

    before = handler.Requests.Count;
    foreach (var bad in new[] { "", " ", "abc\nxyz", "abc\u007f", "á", new string('x', 4097) })
        await Reject(() => service.SaveAsync(new(account, bad)), "invalid_token_format", 400);
    foreach (var bad in new[] { "", account + "f", new string('g', 32), "../" + account, account + "\n" })
        await Reject(() => service.SaveAsync(new(bad, oldToken)), "invalid_account_id", 400);
    await Reject(() => service.GetZoneAsync("../secrets"), "invalid_zone_id", 400);
    Check(handler.Requests.Count == before, "Invalid input reached the provider.");
    handler.Respond = Success;
    await service.SaveAsync(new(account, new string('Z', 4096)));
    Check((await service.GetTokenAsync()).Token!.Length == 4096, "Opaque maximum-length tokens were rejected.");
    await service.SaveAsync(new(account, "eyJhbGci.synthetic.payload-signature"));
    Check((await service.GetTokenAsync()).Token!.StartsWith("eyJ"), "Token parser incorrectly enforces a prefix or JWT structure.");
    await service.SaveAsync(new(account, oldToken));
    await new CloudflareCredentialStore(settings.Value.CredentialsDirectory, protection).WriteAsync(
        new CloudflareStoredCredential(1, "managed", account, oldToken,
            DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1), 1), CancellationToken.None);
    Check((await service.GetStatusAsync()).Configured, "Expired stored credentials lost their visible status.");
    before = handler.Requests.Count;
    await Reject(() => service.GetTokenAsync(), "provider_token_expired", 422);
    await Reject(() => service.ListZonesAsync(), "provider_token_expired", 422);
    Check(handler.Requests.Count == before, "Expired stored credentials reached the provider.");
    await service.SaveAsync(new(account, oldToken));
    var expiration = DateTimeOffset.UtcNow.AddHours(1);
    handler.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/verify", StringComparison.Ordinal)
        ? Verification(notBefore: DateTimeOffset.UtcNow.AddHours(-1).ToString("O"), expires: expiration.ToString("O")) : Success(request);
    Check((await service.SaveAsync(new(account, oldToken))).ExpiresAt == expiration, "Token expiry was not retained.");
    handler.Respond = Success;
    await service.SaveAsync(new(account, oldToken));

    handler.Respond = request =>
    {
        if (!request.RequestUri!.AbsolutePath.EndsWith("/dns_records", StringComparison.Ordinal)) return Success(request);
        var name = QueryHelpers.ParseQuery(request.RequestUri.Query)["name"].ToString();
        return Page([new { id = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", name, type = "CNAME", content = "delegate.example.net" }]);
    };
    before = handler.Requests.Count;
    var dns = await service.ListChallengeRecordsAsync(zoneId, ["_acme-challenge.Lucia.Example.COM"]);
    Check(dns.Single().Name == "_acme-challenge.lucia.example.com" && dns.Single().Type == "CNAME", "Challenge CNAME preflight failed.");
    Check(handler.Requests.Count == before + 2
        && QueryHelpers.ParseQuery(handler.Requests.Last().Uri.Query)["name"] == "_acme-challenge.lucia.example.com", "DNS preflight lacked an exact-name filter or zone revalidation.");
    await Reject(() => service.ListChallengeRecordsAsync(zoneId, ["_acme-challenge.notexample.com"]), "invalid_challenge_names", 400);
    await Reject(() => service.ListChallengeRecordsAsync(zoneId, ["_acme-challenge.example.com.evil.invalid"]), "invalid_challenge_names", 400);
    handler.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/dns_records", StringComparison.Ordinal)
        ? Page([new { id = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", name = "unrequested.example.com", type = "TXT", content = "private" }]) : Success(request);
    await Reject(() => service.ListChallengeRecordsAsync(zoneId, ["_acme-challenge.example.com"]), "invalid_provider_response", 502);

    status = await service.DisconnectAsync();
    Check(!status.Configured && status.ZoneCount == 0 && status.AccountId is null, "Disconnect status incorrect.");
    var restarted = new CloudflareDomainService(settings,
        DataProtectionProvider.Create(new DirectoryInfo(keys), b => b.SetApplicationName("CloudflareChecks")), client);
    Check((await restarted.GetTokenAsync()).Token is null, "Restart lost explicit disconnect.");
    var plaintext = protection.CreateProtector("Lucia.Homelab.CloudflareDomainCredentials.v1")
        .Unprotect(JsonDocument.Parse(await File.ReadAllTextAsync(recordPath)).RootElement.GetProperty("protectedData").GetString()!);
    Check(plaintext.Contains("disconnected") && !plaintext.Contains(oldToken), "Disconnect marker missing.");
    handler.Respond = Success;
    await restarted.SaveAsync(new(account, newToken));
    Check((await service.GetTokenAsync()).Token == newToken, "Reconnect or disk refresh failed.");
    await File.WriteAllTextAsync(recordPath, "{corrupt " + newToken);
    await Reject(() => service.GetStatusAsync(), "credential_storage_unavailable", 503);
    before = handler.Requests.Count;
    await Reject(() => service.SaveAsync(new(account, oldToken)), "credential_storage_unavailable", 503);
    Check(handler.Requests.Count == before, "Corrupt state contacted provider.");
    await service.DisconnectAsync();
    await File.WriteAllTextAsync(recordPath, new string('x', 65537));
    await Reject(() => service.GetTokenAsync(), "credential_storage_unavailable", 503);
    await service.DisconnectAsync();
    var otherKeys = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(storage, "other-keys")));
    await Reject(() => new CloudflareDomainService(settings, otherKeys, client).GetStatusAsync(), "credential_storage_unavailable", 503);
    await service.DisconnectAsync();
    await new CloudflareCredentialStore(settings.Value.CredentialsDirectory, protection).WriteAsync(
        new CloudflareStoredCredential(1, "disconnected", account, oldToken, null, null, 0), CancellationToken.None);
    await Reject(() => service.GetStatusAsync(), "credential_storage_unavailable", 503);
    await service.DisconnectAsync();

    var target = Path.Combine(storage, "untouched");
    await File.WriteAllTextAsync(target, "untouched");
    File.Delete(recordPath);
    var linked = false;
    try { File.CreateSymbolicLink(recordPath, target); linked = true; }
    catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { }
    catch (IOException) when (OperatingSystem.IsWindows()) { }
    if (linked)
    {
        await Reject(() => service.GetStatusAsync(), "credential_storage_unavailable", 503);
        await Reject(() => service.DisconnectAsync(), "credential_storage_unavailable", 503);
        Check(await File.ReadAllTextAsync(target) == "untouched", "A credential operation followed a symlink.");
        File.Delete(recordPath);
    }
    await service.DisconnectAsync();

    await CheckEndpoints();
    Check(handler.Requests.All(r => r.Method == HttpMethod.Get && r.Uri.Scheme == "https" && r.Uri.Host == "api.cloudflare.com"
        && !r.Uri.AbsoluteUri.Contains(oldToken) && !r.Uri.AbsoluteUri.Contains(newToken)), "A request wrote DNS or leaked a token in a URL.");
    Console.WriteLine($"PASS: {checks} Cloudflare checks. Fake provider only; no DNS writes, Certbot, live tokens or GPU dependencies.");
}
finally
{
    if (Directory.Exists(storage)) Directory.Delete(storage, recursive: true);
}

async Task CheckEndpoints()
{
    var logs = new CaptureLogger();
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    builder.Logging.AddProvider(logs);
    builder.Services.AddAuthentication("check").AddScheme<AuthenticationSchemeOptions, CheckAuthentication>("check", _ => { });
    builder.Services.AddAuthorization(o => o.AddPolicy("HostOwner", p => p.RequireAuthenticatedUser().RequireRole("Owner")));
    builder.Services.AddHttpLogging(o => o.LoggingFields = HttpLoggingFields.All);
    builder.AddCloudflareDomains();
    builder.Services.AddSingleton(service);
    await using var app = builder.Build();
    app.UseHttpLogging();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapCloudflareDomains();
    var traceOptions = app.Services.GetRequiredService<IOptions<AspNetCoreTraceInstrumentationOptions>>().Value;
    var context = new DefaultHttpContext();
    context.Request.Path = "/api/host/domains/cloudflare/credentials";
    Check(!traceOptions.Filter!(context), "Inbound Cloudflare requests are still traced.");
    await app.StartAsync();
    try
    {
        using var browser = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var response = await browser.GetAsync("/api/host/domains/cloudflare/credentials");
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "Anonymous credentials endpoint was allowed.");
        browser.DefaultRequestHeaders.Add("X-Check-Role", "Inference");
        response = await browser.GetAsync("/api/host/domains/cloudflare/zones");
        Check(response.StatusCode == HttpStatusCode.Forbidden, "Inference token was allowed owner zone discovery.");
        browser.DefaultRequestHeaders.Remove("X-Check-Role");
        browser.DefaultRequestHeaders.Add("X-Check-Role", "Owner");
        handler.Respond = Success;
        response = await browser.PutAsJsonAsync("/api/host/domains/cloudflare/credentials", new { accountId = account, token = oldToken });
        Check(response.IsSuccessStatusCode, "Owner could not save credentials.");
        NoSecrets(await response.Content.ReadAsStringAsync());
        Check(response.Headers.CacheControl?.NoStore == true, "Credential response can be cached.");
        response = await browser.GetAsync("/api/host/domains/cloudflare/zones");
        Check(response.IsSuccessStatusCode, "Owner could not list zones.");
        NoSecrets(await response.Content.ReadAsStringAsync());
        response = await browser.GetAsync("/api/host/domains/cloudflare/credentials?unexpected=1");
        Check(response.StatusCode == HttpStatusCode.BadRequest, "A credential query parameter was accepted.");
        var before = handler.Requests.Count;
        foreach (var body in new[] { "{}", "{\"accountId\":\"" + account + "\"}", "{\"token\":\"" + oldToken + "\"}",
            "{\"accountId\":\"" + account + "\",\"token\":\"" + oldToken + "\",\"extra\":true}",
            "{\"accountId\":\"" + account + "\",\"token\":\"" + oldToken + "\",\"token\":\"duplicate\"}",
            "{invalid " + oldToken })
        {
            response = await browser.PutAsync("/api/host/domains/cloudflare/credentials", new StringContent(body, Encoding.UTF8, "application/json"));
            Check(response.StatusCode == HttpStatusCode.BadRequest, "Malformed credential input was accepted.");
            NoSecrets(await response.Content.ReadAsStringAsync());
        }
        Check(handler.Requests.Count == before, "Malformed requests contacted Cloudflare.");
        response = await browser.PutAsync("/api/host/domains/cloudflare/credentials", new StringContent(new string('x', 8193), Encoding.UTF8, "application/json"));
        Check(response.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Oversized request accepted.");
        response = await browser.PutAsync("/api/host/domains/cloudflare/credentials",
            new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(new string('x', 8193)))) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } });
        Check(response.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Chunked oversized request accepted.");
        response = await browser.PutAsync("/api/host/domains/cloudflare/credentials", new StringContent(oldToken));
        Check(response.StatusCode == HttpStatusCode.UnsupportedMediaType, "Non-JSON credentials accepted.");
        handler.Respond = _ => FakeHandler.Json(newToken, HttpStatusCode.Forbidden);
        response = await browser.PutAsJsonAsync("/api/host/domains/cloudflare/credentials", new { accountId = account, token = newToken });
        Check((int)response.StatusCode == 422, "Provider auth failure became Lucia 401/403.");
        NoSecrets(await response.Content.ReadAsStringAsync());
        handler.Respond = _ => throw new InvalidOperationException(newToken);
        response = await browser.GetAsync("/api/host/domains/cloudflare/zones");
        Check(response.StatusCode == HttpStatusCode.ServiceUnavailable, "Unexpected failure was not safely contained.");
        NoSecrets(await response.Content.ReadAsStringAsync());
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(1)) } };
        response = await browser.GetAsync("/api/host/domains/cloudflare/zones");
        Check((int)response.StatusCode == 429 && response.Headers.RetryAfter?.Delta == TimeSpan.FromHours(1), "Endpoint rate-limit mapping incorrect.");
        response = await browser.DeleteAsync("/api/host/domains/cloudflare/credentials");
        Check(response.IsSuccessStatusCode && !(await service.GetStatusAsync()).Configured, "Endpoint disconnect failed.");
        response = await browser.GetAsync("/api/host/domains/cloudflare/dns_records");
        Check(response.StatusCode == HttpStatusCode.NotFound, "Private DNS records gained a browser endpoint.");
        NoSecrets(string.Join('\n', logs.Lines));
    }
    finally { await app.StopAsync(); }
}

internal sealed class FakeHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage>? Respond { get; set; }
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? AsyncRespond { get; set; }
    public List<(HttpMethod Method, Uri Uri, string? Token, bool Suppressed)> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.Parameter, Sdk.SuppressInstrumentation));
        return AsyncRespond?.Invoke(request, ct) ?? Task.FromResult(Respond!(request));
    }
    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

internal sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
{
    public override bool CanSeek => false;
}

internal sealed class StalledStream : MemoryStream
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
}

internal sealed class CheckAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers["X-Check-Role"].ToString();
        if (role is not ("Owner" or "Inference")) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test-owner"), new Claim(ClaimTypes.Role, role)], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

internal sealed class CaptureLogger : ILoggerProvider
{
    public List<string> Lines { get; } = [];
    public ILogger CreateLogger(string categoryName) => new Capture(this);
    public void Dispose() { }
    private sealed class Capture(CaptureLogger owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { lock (owner.Lines) owner.Lines.Add(formatter(state, exception) + exception); }
    }
}
