using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Lucia.Homelab.Server.Host;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Trace;

// No model runtime, GPU, live AdGuard, account, or DNS mutations are used by these checks.
var storage = Path.GetFullPath(Path.Combine("tests", "Lucia.Homelab.AdGuardChecks", ".checks-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(storage);
const string password = " Synthetic:password-é-not-live ";
const string replacement = "Replacement-not-live";
const string serverStatus = """
    {"dns_addresses":["192.168.5.8"],"dns_port":53,"http_port":3000,"protection_enabled":true,
     "protection_disabled_duration":0,"running":true,"version":"v0.107.79","language":"en"}
    """;
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var checks = 0;
var skipped = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
}
void NoSecret(string text) =>
    Check(!text.Contains(password, StringComparison.Ordinal) && !text.Contains(replacement, StringComparison.Ordinal)
        && !text.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("owner-é:" + password)), StringComparison.Ordinal),
        "A credential leaked.");
async Task Reject(Func<Task> action, string code, int status)
{
    try { await action(); }
    catch (AdGuardManagementException e)
    {
        Check(e.Code == code && e.StatusCode == status, $"Expected {code}/{status}, got {e.Code}/{e.StatusCode}.");
        NoSecret(e.ToString());
        Check(e.InnerException is null, "Error retained a raw inner exception.");
        return;
    }
    throw new InvalidOperationException("Expected " + code);
}
AdGuardConnectionRequest Request(string url = "https://adguard.home:8443", string secret = password, bool http = false,
    string username = "owner-é") => new() { BaseUrl = url, Username = username, Password = secret, AllowInsecureHttp = http };
var keys = Path.Combine(storage, "keys");
var protection = DataProtectionProvider.Create(new DirectoryInfo(keys), b => b.SetApplicationName("AdGuardChecks"));
var options = Options.Create(new AdGuardManagementOptions { CredentialsDirectory = Path.Combine(storage, "credentials") });
var fake = new FakeState();
string Profile(HttpRequestMessage request)
{
    var basic = Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization!.Parameter!));
    return JsonSerializer.Serialize(new { name = basic[..basic.IndexOf(':')], language = "en", theme = "auto" });
}
void Healthy() => fake.Response = request => Task.FromResult(FakeState.Json(request.RequestUri!.AbsolutePath switch
{
    "/control/profile" => Profile(request),
    "/control/status" => serverStatus,
    "/control/rewrite/settings" => """{"enabled":true}""",
    "/control/rewrite/list" => "[]",
    _ => ""
}));
Healthy();
var resolved = 0;
var transport = new AdGuardTransport((host, ct) =>
{
    resolved++;
    return Task.FromResult(new[] { IPAddress.Parse("192.168.5.8") });
}, (origin, addresses) => new FakeHandler(fake));
var service = new AdGuardConnectionService(options, protection, transport);
var path = Path.Combine(options.Value.CredentialsDirectory, "adguard.json");
try
{
    Check(await service.GetStatusAsync() is { Configured: false, BaseUrl: null, Username: null, LastVerifiedAt: null }, "Initial state is not unconfigured.");
    Check(fake.Requests.Count == 0, "Status performed remote access.");
    await Reject(() => service.VerifyAsync(), "adguard_not_configured", 409);
    var status = await service.SaveAsync(Request());
    Check(status is { Configured: true, Version: "v0.107.79", AllowInsecureHttp: false, LastVerifiedAt: not null }, "Saved status invalid.");
    Check(resolved == 1 && fake.Requests.Count == 4 && fake.Requests.All(r => r.Method == "GET"), "Save was not exactly four read-only requests.");
    Check(fake.Requests.Select(r => r.Uri.AbsolutePath).SequenceEqual(
        new[] { "/control/profile", "/control/status", "/control/rewrite/list", "/control/rewrite/settings" }), "Wrong validation endpoints.");
    Check(fake.Requests.All(r => r.Uri.UserInfo == "" && !r.Uri.AbsoluteUri.Contains("owner")
        && r.Auth == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("owner-é:" + password))), "Basic encoding/URL boundary invalid.");
    var publicJson = JsonSerializer.Serialize(status, json);
    NoSecret(publicJson + Request() + JsonSerializer.Serialize(Request(), json));
    using (var doc = JsonDocument.Parse(publicJson))
        Check(doc.RootElement.EnumerateObject().Select(p => p.Name).SequenceEqual(
            new[] { "configured", "baseUrl", "username", "allowInsecureHttp", "version", "lastVerifiedAt" }), "Public status shape changed.");
    var saved = await File.ReadAllTextAsync(path);
    var tls = """{"enabled":false,"server_name":"","force_https":false,"port_https":443,"port_dns_over_tls":853,"certificate_chain":"","private_key":"","private_key_saved":false,"certificate_path":"/etc/old.pem","private_key_path":"/etc/old.key","valid_cert":false}""";
    fake.Response = request => Task.FromResult(FakeState.Json(request.RequestUri!.AbsolutePath switch
        { "/control/tls/status" => tls, "/control/profile" => Profile(request), _ => "{}" }));
    Check(await service.CertificateNameAsync() == "adguard.home", "Certificate name is not the connection host.");
    fake.Requests.Clear();
    Check(await service.PushCertificateAsync(["adguard.home"], "CHAIN", "KEY"), "First push did not configure.");
    Check(fake.Requests.Select(r => r.Method + " " + r.Uri.AbsolutePath).SequenceEqual(new[] { "GET /control/profile", "GET /control/tls/status", "POST /control/tls/configure" }), "Wrong TLS requests.");
    using (var sent = JsonDocument.Parse(fake.Requests[2].Body!))
    {
        var root = sent.RootElement;
        Check(root.GetProperty("enabled").GetBoolean() && root.GetProperty("server_name").GetString() == "adguard.home"
            && root.GetProperty("port_https").GetInt32() == 443 && root.GetProperty("certificate_path").GetString() == ""
            && Encoding.UTF8.GetString(Convert.FromBase64String(root.GetProperty("certificate_chain").GetString()!)) == "CHAIN"
            && Encoding.UTF8.GetString(Convert.FromBase64String(root.GetProperty("private_key").GetString()!)) == "KEY", "TLS configure body invalid.");
        tls = root.ToString();
    }
    fake.Requests.Clear();
    Check(!await service.PushCertificateAsync(["adguard.home"], "CHAIN", "KEY") && fake.Requests.Count == 2, "Unchanged certificate was pushed again.");
    await Reject(() => service.PushCertificateAsync(["other.home"], "CHAIN", "KEY"), "adguard_connection_changed", 409);
    fake.Requests.Clear();
    Check(await service.PushCertificateAsync(["new.home", "adguard.home"], "CHAIN", "KEY") && JsonDocument.Parse(fake.Requests[2].Body!).RootElement.GetProperty("server_name").GetString() == "new.home", "Move to a new name did not serve it.");
    Healthy();
    fake.Requests.Clear();
    NoSecret(saved);
    Check(!saved.Contains("owner") && !saved.Contains("adguard.home") && saved.Contains("protectedData"), "Record was not fully protected.");
    Check(Directory.GetFiles(keys, "*.xml").Length > 0, "Real Data Protection keys absent.");
    if (OperatingSystem.IsWindows())
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var acl = new FileInfo(path).GetAccessControl();
        Check(acl.AreAccessRulesProtected, "Record ACL inherits permissions.");
        foreach (FileSystemAccessRule access in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            Check(access.IdentityReference.Equals(user), "Record ACL is not current-user only.");
        var directoryAcl = new DirectoryInfo(options.Value.CredentialsDirectory).GetAccessControl();
        Check(directoryAcl.AreAccessRulesProtected, "Directory ACL inherits permissions.");
        foreach (FileSystemAccessRule access in directoryAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            Check(access.IdentityReference.Equals(user), "Directory ACL is not private.");
    }
    else
    {
        Check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Record not 0600.");
        Check(File.GetUnixFileMode(options.Value.CredentialsDirectory) ==
            (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "Directory not 0700.");
    }
    foreach (var (httpStatus, code, expected) in new[]
    {
        (401, "adguard_access_denied", 403), (403, "adguard_access_denied", 403),
        (429, "adguard_rate_limited", 429), (500, "adguard_unavailable", 502),
        (404, "adguard_api_unavailable", 502), (302, "adguard_redirect_rejected", 502)
    })
    {
        var before = fake.Requests.Count;
        fake.Response = _ => Task.FromResult(FakeState.Json(replacement, (HttpStatusCode)httpStatus));
        await Reject(() => service.SaveAsync(Request(secret: replacement)), code, expected);
        Check(fake.Requests.Count == before + 1, "Failed validation retried or followed redirects.");
        Check(await File.ReadAllTextAsync(path) == saved, "Failed replacement changed prior good config.");
    }
    fake.Response = _ => throw new HttpRequestException(replacement);
    await Reject(() => service.SaveAsync(Request(secret: replacement)), "adguard_unavailable", 502);
    fake.Response = _ => Task.FromResult(FakeState.Json("not JSON " + replacement));
    await Reject(() => service.SaveAsync(Request(secret: replacement)), "invalid_adguard_response", 502);
    fake.Response = _ => Task.FromResult(FakeState.Json(new string('x', 1024 * 1024 + 1)));
    await Reject(() => service.SaveAsync(Request(secret: replacement)), "adguard_response_too_large", 502);
    fake.Response = _ =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new NonSeekableStream(new string('x', 1024 * 1024 + 1))) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return Task.FromResult(response);
    };
    await Reject(() => service.SaveAsync(Request(secret: replacement)), "adguard_response_too_large", 502);
    fake.Response = _ => Task.FromResult(FakeState.Json("{}"));
    await Reject(() => service.SaveAsync(Request(secret: replacement)), "invalid_adguard_response", 502);
    void WithStatus(string body) => fake.Response = request => Task.FromResult(FakeState.Json(request.RequestUri!.AbsolutePath switch
    {
        "/control/profile" => Profile(request), "/control/status" => body,
        "/control/rewrite/settings" => """{"enabled":true}""", _ => "[]"
    }));
    WithStatus(serverStatus.Replace("\"protection_disabled_duration\":0", "\"protection_disabled_duration\":\"bad\""));
    await Reject(() => service.SaveAsync(Request(secret: replacement)), "invalid_adguard_response", 502);
    WithStatus(serverStatus.Replace("v0.107.79", "v0.107.79-" + replacement));
    await Reject(() => service.SaveAsync(Request(secret: replacement)), "invalid_adguard_response", 502);
    fake.Response = request => Task.FromResult(FakeState.Json(request.RequestUri!.AbsolutePath switch
    {
        "/control/profile" => Profile(request), "/control/status" => serverStatus, _ => "{}"
    }));
    await Reject(() => service.SaveAsync(Request()), "invalid_adguard_response", 502);
    Check(await File.ReadAllTextAsync(path) == saved, "Schema failure lost prior good config.");
    foreach (var name in new[] { "", " \t " })
    {
        fake.Response = request => Task.FromResult(FakeState.Json(request.RequestUri!.AbsolutePath switch
        {
            "/control/profile" => JsonSerializer.Serialize(new { name, language = "en", theme = "auto" }),
            "/control/status" => serverStatus, "/control/rewrite/settings" => """{"enabled":true}""", _ => "[]"
        }));
        var before = fake.Requests.Count;
        await Reject(() => service.SaveAsync(Request(secret: replacement)), "adguard_authentication_required", 403);
        await Reject(() => service.VerifyAsync(), "adguard_authentication_required", 403);
        await Reject(() => service.AddRewriteAsync(new("service.home", "192.168.5.10")), "adguard_authentication_required", 403);
        Check(fake.Requests.Skip(before).All(r => r.Method == "GET" && r.Uri.AbsolutePath == "/control/profile"),
            "Userless AdGuard was trusted or mutated despite an empty profile.");
        Check(await File.ReadAllTextAsync(path) == saved, "Userless server discarded good credentials.");
    }
    fake.Response = _ => Task.FromResult(FakeState.Json("""{"name":"another-user"}"""));
    await Reject(() => service.SaveAsync(Request()), "adguard_identity_mismatch", 403);
    WithStatus(serverStatus.Replace("\"running\":true", "\"running\":false"));
    await Reject(() => service.SaveAsync(Request()), "adguard_dns_not_running", 409);
    await Reject(() => service.VerifyAsync(), "adguard_dns_not_running", 409);
    Check(await File.ReadAllTextAsync(path) == saved, "Stopped DNS modified the verified configuration.");
    fake.Response = request => Task.FromResult(FakeState.Json(request.RequestUri!.AbsolutePath switch
    {
        "/control/profile" => Profile(request), "/control/status" => serverStatus, "/control/rewrite/list" => "[]", _ => "{}"
    }));
    await Reject(() => service.SaveAsync(Request()), "invalid_adguard_response", 502);
    Check(await File.ReadAllTextAsync(path) == saved, "Invalid rewrite settings modified saved configuration.");
    Healthy();
    var beforeVerify = fake.Requests.Count;
    await service.VerifyAsync();
    Check(fake.Requests.Skip(beforeVerify).All(r => r.Method == "GET") && fake.Requests.Count == beforeVerify + 4, "Verify mutated the provider.");
    Check((await service.GetConnectionAsync()).Configured, "Interface connection alias invalid.");
    var health = await service.GetHealthAsync();
    Check(health is { Running: true, ProtectionEnabled: true, RewritesEnabled: true, Version: "v0.107.79", DnsPort: 53 }
        && health.DnsAddresses.SequenceEqual(new[] { "192.168.5.8" }), "Read-only health status is incomplete.");
    NoSecret(JsonSerializer.Serialize(health, json));

    string[] advertisedAddresses = [
        "192.168.5.8", "https://adguard.home/dns-query", "tls://adguard.home:853", "quic://adguard.home:853",
        "192.168.5.8:5353", "[fd00::8]:5353", "fd00::8"
    ];
    WithStatus(serverStatus.Replace("""["192.168.5.8"]""", JsonSerializer.Serialize(advertisedAddresses)));
    var beforeEncryptedDns = fake.Requests.Count;
    Check((await service.SaveAsync(Request())).Configured, "Valid encrypted-DNS advertisements prevented connection setup.");
    Check((await service.GetHealthAsync()).DnsAddresses.SequenceEqual(advertisedAddresses),
        "Advertised DNS endpoints were dropped or rewritten.");
    await service.VerifyAsync();
    Check(fake.Requests.Skip(beforeEncryptedDns).All(r => r.Method == "GET" && r.Uri.Host == "adguard.home"
        && r.Uri.Port == 8443 && r.Uri.AbsolutePath.StartsWith("/control/", StringComparison.Ordinal)),
        "DNS advertisements were treated as connection destinations.");
    var savedWithEncryptedDns = await File.ReadAllTextAsync(path);
    foreach (var invalidAddresses in new[] { "null", "{}", "[null]", "[42]", "[true]", """[""]""",
        JsonSerializer.Serialize(new[] { "dns\naddress" }), JsonSerializer.Serialize(new[] { new string('x', 2049) }),
        JsonSerializer.Serialize(Enumerable.Repeat("192.168.5.8", 65)), JsonSerializer.Serialize(new[] { replacement }) })
    {
        WithStatus(serverStatus.Replace("""["192.168.5.8"]""", invalidAddresses));
        await Reject(() => service.SaveAsync(Request(secret: replacement)), "invalid_adguard_response", 502);
        Check(await File.ReadAllTextAsync(path) == savedWithEncryptedDns, "Malformed metadata replaced the saved connection.");
    }
    Healthy();

    foreach (var bad in new[] { "http://192.168.1.2", "https://user:secret@192.168.1.2", "https://192.168.1.2/a/..",
        "https://192.168.1.2/%2e", "https://192.168.1.2/?q=a", "https://192.168.1.2/#a",
        "https://192.168.1.2\\", "ftp://192.168.1.2", " https://192.168.1.2", "https://[fd00::1%25eth0]" })
    {
        var before = fake.Requests.Count;
        await Reject(() => service.SaveAsync(Request(bad)), bad.StartsWith("http:") ? "adguard_http_consent_required" : "invalid_adguard_url", 400);
        Check(fake.Requests.Count == before, "Invalid URL sent credentials.");
    }
    foreach (var bad in new[] { Request(username: "bad:name"), Request(username: "bad\nname"), Request(username: new string('a', 129)),
        Request(secret: "bad\rsecret"), Request(secret: "bad\nsecret"), Request(secret: new string('a', 1025)), Request(secret: ""),
        Request(secret: "\ud800") })
        await Reject(() => service.SaveAsync(bad), "invalid_adguard_credentials", 400);
    Check((await service.SaveAsync(Request(secret: "a"))).Configured, "A short password collided with ordinary schema property names.");
    Check((await service.SaveAsync(Request(secret: " \t "))).Configured, "Password whitespace was not preserved.");
    Check((await service.SaveAsync(Request(secret: new string('a', 1024), username: new string('u', 128)))).Configured, "Maximum credential lengths failed.");
    status = await service.SaveAsync(Request("http://192.168.5.8:3000/", http: true));
    Check(status.AllowInsecureHttp && status.BaseUrl == "http://192.168.5.8:3000", "HTTP consent not public or origin not canonical.");
    foreach (var address in new[] { "127.0.0.1", "::1", "169.254.169.254", "169.254.1.1", "100.100.100.200",
        "100.64.0.1", "8.8.8.8", "0.0.0.0", "::", "fe80::1", "ff02::1", "224.0.0.1", "2001:4860:4860::8888",
        "172.15.255.255", "172.32.0.1", "192.169.0.1", "::ffff:192.168.5.8" })
    {
        Check(!AdGuardTransport.IsPrivate(IPAddress.Parse(address)), "Unsafe address classified as LAN.");
        var url = IPAddress.Parse(address).AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"http://[{address}]:3000" : $"http://{address}:3000";
        await Reject(() => service.SaveAsync(Request(url, http: true)), "adguard_private_network_required", 400);
    }
    foreach (var address in new[] { "10.0.0.0", "10.255.255.255", "172.16.0.0", "172.31.255.255", "192.168.0.0", "192.168.255.255", "fc00::1", "fdff::1" })
        Check(AdGuardTransport.IsPrivate(IPAddress.Parse(address)), "Private address incorrectly rejected.");
    var mixed = new AdGuardConnectionService(options, protection, new AdGuardTransport(
        (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.5.8"), IPAddress.Loopback }),
        (_, _) => throw new InvalidOperationException("Mixed DNS reached transport")));
    await Reject(() => mixed.SaveAsync(Request()), "adguard_private_network_required", 400);
    var external = new AdGuardConnectionService(options, protection, new AdGuardTransport(
        (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
        (_, _) => throw new InvalidOperationException("Public DNS reached transport")));
    await Reject(() => external.SaveAsync(Request("http://adguard.home", http: true)), "adguard_private_network_required", 400);
    var deadlineFake = new FakeState
    {
        ResponseWithCancellation = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return FakeState.Json(""); }
    };
    var timed = new AdGuardConnectionService(options, protection, new AdGuardTransport(
        (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.5.8") }), (_, _) => new FakeHandler(deadlineFake), TimeSpan.FromMilliseconds(50)));
    await Reject(() => timed.SaveAsync(Request()), "adguard_timeout", 504);
    Check(deadlineFake.Requests.Count == 1, "Timeout retried authentication.");

    // Exercise the actual SocketsHttpHandler ConnectCallback with scripted streams, not real sockets.
    var dnsCalls = 0;
    var sockets = new List<IPEndPoint>();
    var rawRequests = new List<string>();
    var httpSpans = new CaptureHttpSpans();
    using var tracing = Sdk.CreateTracerProviderBuilder().AddHttpClientInstrumentation().AddProcessor(httpSpans).Build();
    using (var instrumentedHandler = AdGuardTransport.CreateHandler(new Uri("http://adguard.home:3000"),
        [IPAddress.Parse("192.168.5.8")], (_, _) => ValueTask.FromResult<Stream>(new ScriptedHttpStream("[]", []))))
    {
        instrumentedHandler.ActivityHeadersPropagator = DistributedContextPropagator.Current;
        using var instrumented = new HttpClient(instrumentedHandler);
        await instrumented.GetStringAsync("http://adguard.home:3000/control/rewrite/list");
    }
    Check(httpSpans.Count > 0, "Trace suppression test did not enable real HTTP instrumentation.");
    var spansBeforeCredentials = httpSpans.Count;
    var pinnedTransport = new AdGuardTransport((_, _) =>
    {
        dnsCalls++;
        return Task.FromResult(new[] { IPAddress.Parse(dnsCalls == 1 ? "192.168.5.9" : "8.8.8.8") });
    }, (origin, addresses) => AdGuardTransport.CreateHandler(origin, addresses, (endpoint, _) =>
    {
        sockets.Add(endpoint);
        Stream stream = new ScriptedHttpStream(sockets.Count switch
        {
            1 => """{"name":"owner-é","language":"en","theme":"auto"}""",
            2 => serverStatus, 3 => "[]", _ => """{"enabled":true}"""
        }, rawRequests);
        return ValueTask.FromResult(stream);
    }));
    var pinned = new AdGuardConnectionService(options, protection, pinnedTransport);
    await pinned.SaveAsync(Request("http://adguard.home:3000", http: true));
    Check(dnsCalls == 1 && sockets.Count == 4 && sockets.All(e => e.Address.ToString() == "192.168.5.9" && e.Port == 3000),
        "Actual sockets were not pinned across reconnection.");
    Check(rawRequests.All(r => !r.Contains("traceparent", StringComparison.OrdinalIgnoreCase)
        && r.Contains("Host: adguard.home:3000")), "Tracing propagated or origin hostname lost.");
    Check(httpSpans.Count == spansBeforeCredentials, "Credential requests escaped HTTP instrumentation suppression.");
    await Reject(() => pinned.VerifyAsync(), "adguard_private_network_required", 400);
    using (var handler = AdGuardTransport.CreateHandler(new Uri("https://adguard.home:8443"), [IPAddress.Parse("192.168.5.8")]))
        Check(!handler.AllowAutoRedirect && !handler.UseProxy && !handler.UseCookies && handler.Credentials is null
            && handler.SslOptions.RemoteCertificateValidationCallback is null && handler.ActivityHeadersPropagator is null,
            "Transport weakened TLS, proxy, cookies or redirect defaults.");

    Healthy();
    await service.SaveAsync(Request());
    var rule = new AdGuardRewrite("service.home", "192.168.5.10");
    var beforeAdd = fake.Requests.Count;
    await service.AddRewriteAsync(rule);
    var mutation = fake.Requests.Last();
    Check(mutation.Method == "POST" && mutation.Uri.AbsolutePath == "/control/rewrite/add"
        && mutation.Body == """{"domain":"service.home","answer":"192.168.5.10"}""", "Add was not an exact two-field API request.");
    Check(fake.Requests.Skip(beforeAdd).Select(r => r.Uri.AbsolutePath).SequenceEqual(
        new[] { "/control/profile", "/control/status", "/control/rewrite/settings", "/control/rewrite/list", "/control/rewrite/add" }), "Unexpected rewrite preflight/mutation.");
    void WithList(string list) => fake.Response = request => Task.FromResult(FakeState.Json(request.RequestUri!.AbsolutePath switch
    {
        "/control/profile" => Profile(request), "/control/status" => serverStatus, "/control/rewrite/settings" => """{"enabled":true}""",
        "/control/rewrite/list" => list, _ => ""
    }));
    WithList("""[{"domain":"service.home","answer":"192.168.5.10"},{"domain":"elsewhere.home","answer":"alias.example"},{"domain":"public.home","answer":"8.8.8.8","enabled":false}]""");
    var listed = await service.ListRewritesAsync();
    Check(listed.Count == 3 && !listed[2].Enabled && listed[1].Answer == "alias.example", "Existing non-private/CNAME/disabled entries lost.");
    await Reject(() => service.AddRewriteAsync(rule), "adguard_rewrite_conflict", 409);
    await service.DeleteRewriteAsync(rule);
    Check(fake.Requests.Last().Uri.AbsolutePath == "/control/rewrite/delete" && fake.Requests.Last().Body == mutation.Body, "Delete was not exact pair deletion.");
    await Reject(() => service.DeleteRewriteAsync(rule with { Answer = "192.168.5.11" }), "adguard_rewrite_not_found", 409);
    WithList("""[{"domain":"SERVICE.HOME.","answer":"192.168.5.99","enabled":false}]""");
    await Reject(() => service.AddRewriteAsync(rule), "adguard_rewrite_conflict", 409);
    foreach (var bad in new[] { new AdGuardRewrite("a;rm -rf", "192.168.1.2"), new("https://site", "192.168.1.2"),
        new("a.*.home", "192.168.1.2"), new("-a.home", "192.168.1.2"), new("home", "https://evil"),
        new("home", "alias.home"), new("home", "8.8.8.8"), new("home", "::1"), new("home", "192.168.001.2"),
        new("home", "fe80::1"), rule with { Enabled = false } })
        await Reject(() => service.AddRewriteAsync(bad), "invalid_adguard_rewrite", 400);
    WithList("""[{"domain":"service.home","answer":"192.168.5.10","enabled":false}]""");
    await service.DeleteRewriteAsync(rule with { Enabled = false });
    Check(fake.Requests.Last().Uri.AbsolutePath == "/control/rewrite/delete", "Exact disabled-rule cleanup was blocked.");
    var remoteRules = new List<AdGuardRewrite>
    {
        rule, rule with { Enabled = false }, rule with { Answer = "192.168.5.11" },
        new("other.home", rule.Answer)
    };
    fake.Response = request =>
    {
        if (request.RequestUri!.AbsolutePath == "/control/rewrite/delete")
        {
            remoteRules.RemoveAll(item => item.Domain == rule.Domain && item.Answer == rule.Answer);
            return Task.FromResult(FakeState.Json(""));
        }
        return Task.FromResult(FakeState.Json(request.RequestUri.AbsolutePath switch
        {
            "/control/profile" => Profile(request), "/control/status" => serverStatus,
            "/control/rewrite/settings" => """{"enabled":true}""",
            _ => JsonSerializer.Serialize(remoteRules, json)
        }));
    };
    var duplicateEntries = await service.ListRewritesAsync();
    Check(duplicateEntries.Count == 4 && duplicateEntries[0].Enabled && !duplicateEntries[1].Enabled,
        "List lost duplicate pair enabled states.");
    await Reject(() => service.AddRewriteAsync(rule), "adguard_rewrite_conflict", 409);
    await service.DeleteRewriteAsync(rule with { Enabled = false });
    Check(remoteRules.Count == 2 && remoteRules.All(item => item.Domain != rule.Domain || item.Answer != rule.Answer),
        "Exact delete semantics failed to remove all matching pairs regardless of enabled.");
    Healthy();
    await service.AddRewriteAsync(new("*.service.home", "fd00::1"));
    Check(fake.Requests.Last().Body == """{"domain":"*.service.home","answer":"fd00::1"}""", "Valid wildcard/ULA rewrite changed.");
    foreach (var mode in new[] { "protection", "running", "rewrite" })
    {
        fake.Response = request => Task.FromResult(FakeState.Json(request.RequestUri!.AbsolutePath switch
        {
            "/control/profile" => Profile(request),
            "/control/status" => serverStatus.Replace(mode == "running" ? "\"running\":true" : "\"protection_enabled\":true",
                mode == "running" ? "\"running\":false" : mode == "protection" ? "\"protection_enabled\":false" : "\"protection_enabled\":true"),
            "/control/rewrite/settings" => mode == "rewrite" ? """{"enabled":false}""" : """{"enabled":true}""",
            _ => "[]"
        }));
        var before = fake.Requests.Count;
        await Reject(() => service.AddRewriteAsync(rule), "adguard_rewrites_disabled", 409);
        await Reject(() => service.DeleteRewriteAsync(rule), "adguard_rewrites_disabled", 409);
        Check(fake.Requests.Skip(before).All(r => r.Method == "GET"), "Disabled rewrite guard mutated settings.");
        var disabledHealth = await service.GetHealthAsync();
        Check(mode switch
        {
            "running" => !disabledHealth.Running,
            "protection" => !disabledHealth.ProtectionEnabled,
            _ => !disabledHealth.RewritesEnabled
        }, "Disabled state not exposed by read-only health.");
        if (mode == "rewrite")
            Check((await service.SaveAsync(Request())).Configured, "Disabled global rewrites prevented saving an otherwise verified connection.");
    }
    Healthy();
    fake.Response = request =>
    {
        if (request.Method == HttpMethod.Post) throw new HttpRequestException(password);
        return Task.FromResult(FakeState.Json(request.RequestUri!.AbsolutePath switch
        {
            "/control/profile" => Profile(request), "/control/status" => serverStatus,
            "/control/rewrite/settings" => """{"enabled":true}""", _ => "[]"
        }));
    };
    await Reject(() => service.AddRewriteAsync(rule), "adguard_mutation_indeterminate", 502);
    fake.ResponseWithCancellation = async (request, ct) =>
    {
        if (request.Method == HttpMethod.Post) await Task.Delay(Timeout.Infinite, ct);
        return FakeState.Json(request.RequestUri!.AbsolutePath switch
        {
            "/control/profile" => Profile(request), "/control/status" => serverStatus,
            "/control/rewrite/settings" => """{"enabled":true}""", _ => "[]"
        });
    };
    var timedMutation = new AdGuardConnectionService(options, protection, new AdGuardTransport(
        (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.5.8") }), (_, _) => new FakeHandler(fake), TimeSpan.FromMilliseconds(50)));
    await Reject(() => timedMutation.AddRewriteAsync(rule), "adguard_mutation_indeterminate", 502);
    Healthy();
    status = await service.DisconnectAsync();
    Check(!status.Configured && status.BaseUrl is null && status.Username is null, "Disconnect leaked configuration.");
    var restarted = new AdGuardConnectionService(options,
        DataProtectionProvider.Create(new DirectoryInfo(keys), b => b.SetApplicationName("AdGuardChecks")), transport);
    Check(!(await restarted.GetStatusAsync()).Configured, "Disconnect did not persist.");
    foreach (var badRecord in new[] { "{broken " + password, new string('x', 32769), """{"version":2,"protectedData":"bad"}""",
        """{"version":1,"version":1,"protectedData":"bad"}""" })
    {
        await File.WriteAllTextAsync(path, badRecord);
        var before = fake.Requests.Count;
        await Reject(() => service.GetStatusAsync(), "adguard_storage_unavailable", 503);
        await Reject(() => service.SaveAsync(Request()), "adguard_storage_unavailable", 503);
        Check(fake.Requests.Count == before, "Corrupt storage contacted the remote.");
        Check(!(await service.DisconnectAsync()).Configured, "Explicit disconnect failed corrupt-record recovery.");
    }
    await service.SaveAsync(Request());
    var otherKeys = new AdGuardConnectionService(options,
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(storage, "other-keys"))), transport);
    await Reject(() => otherKeys.GetStatusAsync(), "adguard_storage_unavailable", 503);
    var recordProtector = protection.CreateProtector("Lucia.Homelab.AdGuardCredentials.v1");
    foreach (var plaintext in new[] { """{"state":"disconnected"}""", """{"version":1,"Version":1,"state":"disconnected"}""",
        """{"version":1,"state":"disconnected","password":"unexpected"}""" })
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            version = 1, protectedData = Convert.ToBase64String(recordProtector.Protect(Encoding.UTF8.GetBytes(plaintext)))
        }));
        await Reject(() => service.GetStatusAsync(), "adguard_storage_unavailable", 503);
        await service.DisconnectAsync();
    }
    var target = Path.Combine(storage, "untouched");
    await File.WriteAllTextAsync(target, "untouched");
    File.Delete(path);
    var linked = false;
    try { File.CreateSymbolicLink(path, target); linked = true; }
    catch (Exception e) when (OperatingSystem.IsWindows() && e is UnauthorizedAccessException or IOException) { skipped++; }
    if (linked)
    {
        await Reject(() => service.GetStatusAsync(), "adguard_storage_unavailable", 503);
        await Reject(() => service.DisconnectAsync(), "adguard_storage_unavailable", 503);
        Check(await File.ReadAllTextAsync(target) == "untouched", "Credential symlink followed.");
        File.Delete(path);
    }
    var linkDirectory = Path.Combine(storage, "linked");
    linked = false;
    try { Directory.CreateSymbolicLink(linkDirectory, options.Value.CredentialsDirectory); linked = true; }
    catch (Exception e) when (OperatingSystem.IsWindows() && e is UnauthorizedAccessException or IOException)
    {
        // Junctions need no symlink privilege and exercise Windows reparse-component rejection.
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{linkDirectory.Replace("'", "''")}' -Target '{options.Value.CredentialsDirectory.Replace("'", "''")}' -ErrorAction Stop | Out-Null");
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Check(process.ExitCode == 0, "Could not create a local test junction.");
        linked = true;
    }
    if (linked)
    {
        var unsafeService = new AdGuardConnectionService(Options.Create(new AdGuardManagementOptions
            { CredentialsDirectory = Path.Combine(linkDirectory, "child") }), protection, transport);
        await Reject(() => unsafeService.DisconnectAsync(), "adguard_storage_unavailable", 503);
        Directory.Delete(linkDirectory);
    }
    await service.DisconnectAsync();

    // Real loopback Kestrel exercises only Lucia's management routes, never a live AdGuard.
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    builder.AddAdGuardManagement();
    builder.Services.Configure<AdGuardManagementOptions>(o => o.CredentialsDirectory = options.Value.CredentialsDirectory);
    builder.Services.AddSingleton(service);
    builder.Services.AddSingleton(new HostAuthenticationOptions { Enabled = true });
    builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.SecurePolicy = CookieSecurePolicy.None; });
    builder.Services.AddAuthentication(HostAuthentication.Scheme)
        .AddScheme<AuthenticationSchemeOptions, CheckAuthentication>(HostAuthentication.Scheme, _ => { });
    builder.Services.AddAuthorizationBuilder().AddPolicy("HostOwner", p => p.RequireAuthenticatedUser().RequireRole("Owner"));
    await using var app = builder.Build();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseHostCsrf();
    app.MapAdGuardManagement();
    app.MapGet("/checks/csrf", (IAntiforgery antiforgery, HttpContext context) =>
        Results.Json(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }));
    await app.StartAsync();
    using var api = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
    Check(ReferenceEquals(app.Services.GetRequiredService<ILocalDnsProvider>(), service), "DI interface not shared with service.");
    var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
        .Where(e => e.RoutePattern.RawText!.StartsWith("/api/host/connections/adguard")).ToArray();
    Check(routes.Length == 8 && routes.All(e => e.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
        .Any(a => a.Policy == "HostOwner")), "Routes not Owner-only or unexpected CRUD routes exposed.");
    using (var response = await api.GetAsync("/api/host/connections/adguard"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "Anonymous owner route allowed.");
    api.DefaultRequestHeaders.Add("X-Check-Role", "Inference");
    using (var response = await api.GetAsync("/api/host/connections/adguard"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "Non-owner allowed.");
    api.DefaultRequestHeaders.Remove("X-Check-Role");
    api.DefaultRequestHeaders.Add("X-Check-Role", "Owner");
    async Task<HttpResponseMessage> Put(string body, string media = "application/json") =>
        await api.PutAsync("/api/host/connections/adguard", new StringContent(body, Encoding.UTF8, media));
    using (var response = await Put(JsonSerializer.Serialize(new { baseUrl = "https://adguard.home:8443", username = "owner-é", password, allowInsecureHttp = false })))
    {
        Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true, "Owner save or no-store failed.");
        NoSecret(await response.Content.ReadAsStringAsync());
    }
    foreach (var body in new[] { "{bad " + password,
        """{"baseUrl":"https://adguard.home","username":"owner","password":"secret","allowInsecureHttp":false,"extra":"secret"}""",
        """{"baseUrl":"https://adguard.home","username":"owner","password":"secret","password":"other"}""" })
    {
        using var response = await Put(body);
        Check(response.StatusCode == HttpStatusCode.BadRequest, "Malformed/duplicate request not rejected.");
        NoSecret(await response.Content.ReadAsStringAsync());
    }
    using (var response = await Put(new string('x', 16385)))
        Check(response.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Request limit not enforced.");
    using (var content = new StreamContent(new NonSeekableStream(new string('x', 16385))))
    {
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await api.PutAsync("/api/host/connections/adguard", content);
        Check(response.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Chunked request limit not enforced.");
    }
    using (var response = await Put("{}", "text/plain"))
        Check(response.StatusCode == HttpStatusCode.UnsupportedMediaType, "Non-JSON accepted.");
    using (var response = await api.PostAsync("/api/host/connections/adguard/verify", null))
        Check(response.StatusCode == HttpStatusCode.OK, "Verify route missing.");
    api.DefaultRequestHeaders.Add("X-Check-Cookie", "true");
    foreach (var method in new[] { HttpMethod.Put, HttpMethod.Post, HttpMethod.Delete })
    {
        using var request = new HttpRequestMessage(method, "/api/host/connections/adguard" + (method == HttpMethod.Post ? "/verify" : ""));
        using var response = await api.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.Forbidden && (await response.Content.ReadAsStringAsync()).Contains("invalid_csrf_token"),
            "Global CSRF did not guard cookie mutation.");
    }
    using (var response = await api.GetAsync("/checks/csrf"))
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        api.DefaultRequestHeaders.Add("X-CSRF-TOKEN", doc.RootElement.GetProperty("token").GetString());
    }
    using (var response = await api.DeleteAsync("/api/host/connections/adguard"))
        Check(response.StatusCode == HttpStatusCode.OK, "Valid-CSRF disconnect failed.");
    await app.StopAsync();
    Console.WriteLine($"PASS: {checks} assertions; {skipped} platform symlink checks skipped. No live AdGuard/GPU accessed.");
}
finally { Directory.Delete(storage, recursive: true); }

sealed class FakeState
{
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> ResponseWithCancellation { get; set; } = (_, _) => Task.FromResult(Json("{}"));
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Response
    {
        set => ResponseWithCancellation = (request, _) => value(request);
    }
    public List<(string Method, Uri Uri, string? Auth, string? Body)> Requests { get; } = [];
    public static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };
}
sealed class FakeHandler(FakeState state) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        state.Requests.Add((request.Method.Method, request.RequestUri!, request.Headers.Authorization?.ToString(),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
        return await state.ResponseWithCancellation(request, ct);
    }
}
sealed class ScriptedHttpStream(string body, List<string> requests) : Stream
{
    private readonly MemoryStream _response = new(Encoding.UTF8.GetBytes(
        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}"));
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _response.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _response.ReadAsync(buffer, cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => requests.Add(Encoding.UTF8.GetString(buffer, offset, count));
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { requests.Add(Encoding.UTF8.GetString(buffer.Span)); return ValueTask.CompletedTask; }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _response.Dispose(); base.Dispose(disposing); }
}
sealed class CheckAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers["X-Check-Role"].ToString();
        return Task.FromResult(role.Length == 0 ? AuthenticateResult.NoResult() :
            AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "synthetic"), new Claim(ClaimTypes.Role, role)], Scheme.Name)),
                Request.Headers["X-Check-Cookie"] == "true" ? HostAuthentication.CookieScheme : Scheme.Name)));
    }
}
sealed class CaptureHttpSpans : BaseProcessor<Activity>
{
    public int Count { get; private set; }
    public override void OnEnd(Activity activity) => Count++;
}
sealed class NonSeekableStream(string text) : MemoryStream(Encoding.UTF8.GetBytes(text))
{
    public override bool CanSeek => false;
}
