using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Telemetry;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

const string issuer = "https://identity.invalid/application/o/lucia/";
const string publicOrigin = "https://localhost:8443";
const string clientId = "lucia-auth-checks";
const string ownerKey = "opaque.owner.key.with.dots.and.at.least.32.characters";
const string inferenceKey = "opaque.inference.key.with.dots.and.32.characters";
var rootDirectory = Path.Combine(Directory.GetCurrentDirectory(), $".lucia-auth-checks-{Guid.NewGuid():N}");
Directory.CreateDirectory(Path.Combine(rootDirectory, "wwwroot"));
await File.WriteAllTextAsync(Path.Combine(rootDirectory, "wwwroot", "index.html"), "<!doctype html><title>Auth checks SPA</title>");
await File.WriteAllTextAsync(Path.Combine(rootDirectory, "secret"), "synthetic-test-secret-not-a-live-credential\n");
using var authorityKey = RSA.Create(2048);
using var authority = CreateAuthority(authorityKey);
await File.WriteAllTextAsync(Path.Combine(rootDirectory, "ca.pem"), authority.ExportCertificatePem());
using var serverKey = RSA.Create(2048);
using var serverCertificate = CreateServerCertificate(authority, serverKey);
using var signingKey = RSA.Create(2048);
var jwtKey = new RsaSecurityKey(signingKey) { KeyId = "synthetic-test-key" };
var publicJwtKey = new RsaSecurityKey(signingKey.ExportParameters(false)) { KeyId = jwtKey.KeyId };
var checks = 0;

void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
    checks++;
}

void Rejects(Action action, string message)
{
    try { action(); }
    catch (OptionsValidationException) { checks++; return; }
    throw new InvalidOperationException(message);
}

HostAuthenticationOptions Settings() => new()
{
    Enabled = true,
    Authority = issuer,
    ClientId = clientId,
    ClientSecretFile = Path.Combine(rootDirectory, "secret"),
    PublicOrigin = publicOrigin,
    CaCertificatePath = Path.Combine(rootDirectory, "ca.pem"),
    DataProtectionKeysDirectory = Path.Combine(rootDirectory, "keys"),
    TrustedProxyNetworks = ["172.31.240.0/24"]
};

WebApplicationBuilder Builder(string environment = "Production")
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        EnvironmentName = environment,
        ContentRootPath = rootDirectory,
        WebRootPath = Path.Combine(rootDirectory, "wwwroot")
    });
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(serverCertificate));
        options.Listen(IPAddress.Loopback, 0);
    });
    builder.Services.Configure<HostPlatformOptions>(options =>
    {
        options.ApiKey = ownerKey;
        options.InferenceApiKey = inferenceKey;
    });
    return builder;
}

string Jwt(string? role = "Owner", string? scope = HostAuthentication.ApiScope, string? tokenIssuer = null,
    string? audience = null, bool expired = false, bool idToken = false, SecurityKey? key = null, bool signed = true,
    bool accessNonce = false, bool expiration = true, bool future = false, string? authorizedParty = null)
{
    var claims = new List<Claim> { new("sub", "synthetic-subject"), new("preferred_username", "owner"), new("name", "Test Owner") };
    if (role is not null) claims.Add(new(HostAuthentication.RoleClaim, role));
    if (scope is not null) claims.Add(new("scope", scope));
    if (idToken || accessNonce) claims.Add(new("nonce", "synthetic-nonce"));
    if (!idToken)
    {
        claims.Add(new("uid", "synthetic-access-token-id"));
        claims.Add(new("azp", authorizedParty ?? clientId));
    }
    return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
    {
        Subject = new ClaimsIdentity(claims),
        Issuer = tokenIssuer ?? issuer,
        Audience = audience ?? clientId,
        IssuedAt = DateTime.UtcNow.AddMinutes(-20),
        NotBefore = DateTime.UtcNow.AddMinutes(future ? 5 : -20),
        Expires = expiration ? DateTime.UtcNow.AddMinutes(expired ? -10 : 10) : null,
        SigningCredentials = signed ? new SigningCredentials(key ?? jwtKey, SecurityAlgorithms.RsaSha256) : null
    });
}

HttpClient Client(WebApplication app, X509Certificate2? ca = null, bool secure = true)
{
    var handler = HostAuthenticationOptions.CreateBackchannelHandler(ca ?? authority);
    handler.UseCookies = false;
    var client = new HttpClient(handler)
    {
        BaseAddress = new Uri(app.Urls.Single(url => url.StartsWith(secure ? "https:" : "http:", StringComparison.Ordinal))
            .Replace("127.0.0.1", "localhost", StringComparison.Ordinal)),
        Timeout = TimeSpan.FromSeconds(15)
    };
    client.DefaultRequestHeaders.Host = "localhost:8443";
    return client;
}

static async Task<HttpResponseMessage> Send(HttpClient client, string path, string method = "GET", string? authorization = null,
    string? cookie = null, string? csrf = null, HttpContent? content = null, Dictionary<string, string>? headers = null)
{
    using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = content };
    if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
    if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", cookie);
    if (csrf is not null) request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
    if (headers is not null)
        foreach (var header in headers)
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
    return await client.SendAsync(request);
}

try
{
    Settings().Validate(development: false);
    new HostAuthenticationOptions().Validate(development: true);
    Rejects(() => new HostAuthenticationOptions().Validate(development: false), "Production allowed disabled SSO.");
    foreach (var invalid in new Action<HostAuthenticationOptions>[]
    {
        settings => settings.Authority = "",
        settings => settings.Authority = "http://identity.invalid/",
        settings => settings.Authority = "https://user@identity.invalid/",
        settings => settings.Authority = issuer + "?query=true",
        settings => settings.Authority = @"https://identity.invalid\issuer",
        settings => settings.PublicOrigin = "https://localhost/path",
        settings => settings.PublicOrigin = "https://localhost/#fragment",
        settings => settings.PublicOrigin = "http://localhost",
        settings => settings.AdditionalPublicOrigins = ["https://*.example.com"],
        settings => settings.AdditionalPublicOrigins = ["http://legacy.example.com"],
        settings => settings.AdditionalPublicOrigins = ["https://legacy.example.com/path"],
        settings => settings.AdditionalPublicOrigins = ["https://legacy.example.com/.."],
        settings => settings.AdditionalPublicOrigins = ["https://legacy.example.com:"],
        settings => settings.AdditionalPublicOrigins = ["https://legacy.example.com?"],
        settings => settings.AdditionalPublicOrigins = ["https://legacy.example.com:0"],
        settings => settings.AdditionalPublicOrigins = ["https://legacy.example.com:65536"],
        settings => settings.AdditionalPublicOrigins = ["https://user@legacy.example.com"],
        settings => settings.AdditionalPublicOrigins = Enumerable.Repeat("https://legacy.example.com", 5).ToArray(),
        settings => settings.ClientId = "",
        settings => settings.ClientSecretFile = "relative",
        settings => settings.CaCertificatePath = "relative",
        settings => settings.DataProtectionKeysDirectory = "",
        settings => settings.TrustedProxyNetworks = [],
        settings => settings.TrustedProxyNetworks = ["not-a-cidr"],
        settings => settings.TrustedProxyNetworks = ["0.0.0.0/0"],
        settings => settings.TrustedProxyNetworks = ["::/0"],
        settings => settings.TrustedProxyNetworks = ["8.8.8.0/24"],
        settings => settings.TrustedProxyNetworks = ["172.0.0.0/8"],
        settings => settings.TrustedProxyNetworks = ["127.0.0.0/8"]
    })
    {
        var settings = Settings();
        invalid(settings);
        Rejects(() => settings.Validate(development: false), "Invalid SSO settings passed validation.");
    }
    var missing = Settings();
    missing.ClientSecretFile = Path.Combine(rootDirectory, "missing-secret");
    Rejects(() => missing.ReadClientSecret(), "Missing secret was accepted.");
    await File.WriteAllTextAsync(Path.Combine(rootDirectory, "empty-secret"), "\n");
    missing.ClientSecretFile = Path.Combine(rootDirectory, "empty-secret");
    Rejects(() => missing.ReadClientSecret(), "Empty secret was accepted.");
    missing = Settings();
    missing.CaCertificatePath = Path.Combine(rootDirectory, "secret");
    Rejects(() => missing.ReadCertificateAuthority(), "Invalid CA was accepted.");
    await File.WriteAllTextAsync(Path.Combine(rootDirectory, "leaf.pem"), serverCertificate.ExportCertificatePem());
    missing.CaCertificatePath = Path.Combine(rootDirectory, "leaf.pem");
    Rejects(() => missing.ReadCertificateAuthority(), "Leaf certificate was accepted as CA.");
    await File.WriteAllTextAsync(Path.Combine(rootDirectory, "private.pem"),
        authority.ExportCertificatePem() + authorityKey.ExportPkcs8PrivateKeyPem());
    missing.CaCertificatePath = Path.Combine(rootDirectory, "private.pem");
    Rejects(() => missing.ReadCertificateAuthority(), "A CA file containing a private key was accepted.");
    using (var loaded = Settings().ReadCertificateAuthority())
        Check(loaded.Thumbprint == authority.Thumbprint, "Public CA import failed.");
    Check(!Settings().AuthorityUsesSystemTrust && Settings().AdditionalPublicOrigins.Length == 0,
        "Legacy authentication defaults changed.");
    using (var systemTrust = HostAuthenticationOptions.CreateBackchannelHandler(authority, authorityUsesSystemTrust: true))
        Check(!systemTrust.AllowAutoRedirect && systemTrust.SslOptions.RemoteCertificateValidationCallback is null
            && systemTrust.SslOptions.CertificateChainPolicy is null,
            "Public OIDC trust replaced or bypassed native system validation.");
    using (var customTrust = HostAuthenticationOptions.CreateBackchannelHandler(authority))
        Check(customTrust.SslOptions.CertificateChainPolicy?.TrustMode == X509ChainTrustMode.CustomRootTrust
            && customTrust.SslOptions.CertificateChainPolicy.CustomTrustStore.Count == 1
            && customTrust.SslOptions.RemoteCertificateValidationCallback is null,
            "Private recovery authority stopped using explicit private CA trust.");

    var domainDirectory = Path.Combine(rootDirectory, "domains");
    Directory.CreateDirectory(domainDirectory);
    var profile = new DomainRuntimeProfile(1, "example.com", "https://lucia.homelab.example.com",
        "https://auth.homelab.example.com", "https://atlas.homelab.example.com",
        publicOrigin, issuer, Guid.NewGuid(), DateTimeOffset.UtcNow);
    var profileJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    void WriteProfile(string json)
    {
        File.WriteAllText(Path.Combine(domainDirectory, "active.json"), json);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path.Combine(domainDirectory, "active.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    void RejectDomain(Action action, string message)
    {
        try { action(); }
        catch (Exception exception) when (exception is ArgumentException or IOException or JsonException)
        {
            checks++;
            return;
        }
        throw new InvalidOperationException(message);
    }
    var noProfile = Builder();
    noProfile.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["DomainOnboarding:StateDirectory"] = domainDirectory,
        ["HostAuthentication:PublicOrigin"] = publicOrigin,
        ["HostAuthentication:Authority"] = issuer
    });
    Check(DomainActivationConfiguration.Apply(noProfile) is null
        && noProfile.Configuration["HostAuthentication:PublicOrigin"] == publicOrigin
        && noProfile.Configuration["HostAuthentication:AuthorityUsesSystemTrust"] is null,
        "Missing profile altered legacy authentication.");
    foreach (var invalid in new[]
    {
        profile with { SchemaVersion = 2 }, profile with { ProfileId = Guid.Empty },
        profile with { ActivatedAt = DateTimeOffset.UtcNow.AddDays(1) },
        profile with { VerifiedZone = "*.example.com" },
        profile with { CanonicalLuciaOrigin = "https://lucia.outside.invalid" },
        profile with { CanonicalLuciaOrigin = "https://lucia.homelab.example.com:443" },
        profile with { CanonicalLuciaOrigin = "https://lucia.homelab.example.com:8443" },
        profile with { CanonicalLuciaOrigin = "https://lucia.homelab.example.com/path" },
        profile with { CanonicalLuciaOrigin = "https://user@lucia.homelab.example.com" },
        profile with { SparkOrigin = profile.CanonicalLuciaOrigin },
        profile with { LegacyLuciaOrigin = "https://other.invalid" },
        profile with { LegacyAuthority = "https://other.invalid/application/o/lucia/" }
    })
    {
        WriteProfile(JsonSerializer.Serialize(invalid, profileJson));
        RejectDomain(() => DomainActivationConfiguration.Apply(noProfile), "Unsafe domain runtime profile was accepted.");
    }
    var fqdnRecovery = profile with
    {
        LegacyLuciaOrigin = "https://recovery-api.homelab.example.com:8443",
        LegacyAuthority = "https://recovery-auth.homelab.example.com:9443/application/o/lucia/"
    };
    fqdnRecovery.Validate(fqdnRecovery.LegacyLuciaOrigin, fqdnRecovery.LegacyAuthority);
    foreach (var service in new[] { profile.CanonicalLuciaOrigin, profile.CanonicalAuthentikOrigin, profile.SparkOrigin })
    foreach (var suffix in new[] { "", "." })
    foreach (var luciaRecovery in new[] { true, false })
    {
        var host = new Uri(service).IdnHost;
        var collision = luciaRecovery
            ? fqdnRecovery with { LegacyLuciaOrigin = "https://" + host.ToUpperInvariant() + suffix + ":8443" }
            : fqdnRecovery with { LegacyAuthority = "https://" + host + suffix + ":9443/application/o/lucia/" };
        try
        {
            collision.Validate(collision.LegacyLuciaOrigin, collision.LegacyAuthority);
            throw new InvalidOperationException("A public service hostname could replace a private recovery route or SNI certificate.");
        }
        catch (ArgumentException exception)
        {
            Check(exception.Message == "Use distinct public hostnames to preserve private recovery addresses.",
                "A recovery hostname collision did not produce the actionable validation error.");
        }
    }
    foreach (var json in new[]
    {
        JsonSerializer.Serialize(profile, profileJson).Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal),
        JsonSerializer.Serialize(profile, profileJson).Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"ClientSecretFile\":\"evil\"", StringComparison.Ordinal),
        "{}", new string(' ', DomainActivationConfiguration.MaximumProfileBytes + 1)
    })
    {
        WriteProfile(json);
        RejectDomain(() => DomainActivationConfiguration.Read(domainDirectory), "Malformed/unbounded domain profile was accepted.");
    }
    WriteProfile(JsonSerializer.Serialize(profile, profileJson));
    if (!OperatingSystem.IsWindows())
    {
        var path = Path.Combine(domainDirectory, "active.json");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        RejectDomain(() => DomainActivationConfiguration.Read(domainDirectory), "Nonprivate profile was accepted.");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var linked = Path.Combine(rootDirectory, "linked-domains");
        Directory.CreateSymbolicLink(linked, domainDirectory);
        RejectDomain(() => DomainActivationConfiguration.Read(linked), "Linked profile directory was accepted.");
        Directory.Delete(linked);
    }
    var certificateRoot = Path.Combine(domainDirectory, "certificates");
    var version = Path.Combine(certificateRoot, "version-1");
    Directory.CreateDirectory(version);
    var publicCertificate = Path.Combine(version, "fullchain.pem");
    var publicKey = Path.Combine(version, "privkey.pem");
    File.WriteAllText(publicCertificate, "synthetic-prevalidated-certificate");
    File.WriteAllText(publicKey, "synthetic-not-a-private-key");
    var plan = DomainNames.Plan(new("example.com", "homelab", "atlas"), "example.com");
    var gatewayDirectory = Path.Combine(rootDirectory, "domain-gateway");
    Directory.CreateDirectory(gatewayDirectory);
    var legacyGateway = Path.Combine(gatewayDirectory, "tls.json");
    File.WriteAllText(legacyGateway, "leave-private-ca-config-alone");
    DomainIngressConfiguration.Publish(gatewayDirectory, plan, certificateRoot, publicCertificate, publicKey);
    DomainIngressConfiguration.Publish(gatewayDirectory, plan, certificateRoot, publicCertificate, publicKey);
    using (var ingress = JsonDocument.Parse(File.ReadAllText(Path.Combine(gatewayDirectory, DomainIngressConfiguration.FileName))))
    {
        var routers = ingress.RootElement.GetProperty("http").GetProperty("routers");
        Check(routers.GetProperty("domain-lucia").GetProperty("rule").GetString() == "Host(`lucia.homelab.example.com`)"
            && routers.GetProperty("domain-authentik").GetProperty("entryPoints")[0].GetString() == "host"
            && ingress.RootElement.GetProperty("tls").GetProperty("certificates")[0].GetProperty("keyFile").GetString()
                == "/domain-certificates/version-1/privkey.pem", "Ingress routing or mapped certificate paths are incorrect.");
        Check(File.ReadAllText(legacyGateway) == "leave-private-ca-config-alone"
            && Directory.GetFiles(gatewayDirectory).Length == 2, "Ingress touched legacy gateway configuration or left staging files.");
    }
    RejectDomain(() => DomainIngressConfiguration.Build(plan, certificateRoot, Settings().CaCertificatePath, publicKey),
        "Ingress accepted a certificate outside the published root.");
    RejectDomain(() => DomainIngressConfiguration.Build(plan, certificateRoot, publicCertificate, publicCertificate),
        "Ingress accepted the certificate as its private key.");

    foreach (var value in new[] { "/", "/models", "/settings?tab=access#section", "/space%20name", "/nested/path?value=https://elsewhere.invalid" })
        Check(HostAuthentication.IsLocalReturnUrl(value), $"Local return URL rejected: {value}");
    foreach (var value in new[] { "", "//evil.invalid", @"\/evil.invalid", @"/\evil.invalid", "https://evil.invalid", "javascript:alert(1)",
        "/%2fevil.invalid", "/%5cevil.invalid", "/%252fevil.invalid", "/%0d%0aLocation:evil", "/\r\n", "~/path" })
        Check(!HostAuthentication.IsLocalReturnUrl(value), "Unsafe return URL was accepted.");
    Check(HostAuthentication.IsLocalReturnUrl(null), "Default return URL was rejected.");

    var platform = new HostPlatformOptions { ApiKey = ownerKey, InferenceApiKey = inferenceKey };
    foreach (var (header, expected) in new (string?, string)[]
    {
        (null, HostAuthentication.CookieScheme),
        ("", HostApiKeyAuthentication.SchemeName),
        ("Basic invalid", HostApiKeyAuthentication.SchemeName),
        ("Bearer " + ownerKey, HostApiKeyAuthentication.SchemeName),
        ("bearer " + inferenceKey, HostApiKeyAuthentication.SchemeName),
        ("Bearer " + Jwt(), HostAuthentication.JwtScheme),
        ("Bearer invalid", HostAuthentication.JwtScheme)
    })
    {
        var context = new DefaultHttpContext();
        if (header is not null) context.Request.Headers.Authorization = header;
        Check(HostAuthentication.SelectScheme(context.Request, true, platform) == expected, "Wrong API authentication scheme.");
        Check(HostAuthentication.SelectScheme(context.Request, false, platform) == HostApiKeyAuthentication.SchemeName,
            "Development unexpectedly selected SSO.");
    }
    var multiple = new DefaultHttpContext();
    multiple.Request.Headers.Authorization = new Microsoft.Extensions.Primitives.StringValues(["Bearer " + ownerKey, "Bearer invalid"]);
    Check(HostAuthentication.SelectScheme(multiple.Request, true, platform) == HostApiKeyAuthentication.SchemeName,
        "Multiple authorization headers selected cookie authentication.");
    Check(!HostApiKeyAuthentication.Matches(new string('x', 4097), new string('x', 4097)), "Oversized API key accepted.");
    Check(!HostApiKeyAuthentication.Matches("short", "short"), "Short API key accepted.");

    var builder = Builder();
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["HostAuthentication:Enabled"] = "true",
        ["HostAuthentication:Authority"] = issuer,
        ["HostAuthentication:ClientId"] = clientId,
        ["HostAuthentication:ClientSecretFile"] = Settings().ClientSecretFile,
        ["HostAuthentication:PublicOrigin"] = publicOrigin,
        ["HostAuthentication:CaCertificatePath"] = Settings().CaCertificatePath,
        ["HostAuthentication:DataProtectionKeysDirectory"] = Settings().DataProtectionKeysDirectory,
        ["HostAuthentication:TrustedProxyNetworks:0"] = "172.31.240.0/24",
        ["InferenceKeys:Directory"] = Path.Combine(rootDirectory, "inference-keys"),
        ["AdGuardManagement:CredentialsDirectory"] = Path.Combine(rootDirectory, "network-credentials"),
        ["CloudflareDomains:CredentialsDirectory"] = Path.Combine(rootDirectory, "network-credentials"),
        ["DomainOnboarding:StateDirectory"] = Path.Combine(rootDirectory, "domains")
    });
    builder.AddHostAuthentication();
    builder.AddHostOutputCache();
    builder.AddSparkTelemetry();
    builder.AddInferenceKeyManagement();
    builder.AddAdGuardManagement();
    builder.AddCloudflareDomains();
    builder.AddDomainOnboarding();
    var provider = new OpenIdConnectConfiguration
    {
        Issuer = issuer,
        AuthorizationEndpoint = "https://identity.invalid/authorize",
        TokenEndpoint = "https://identity.invalid/token",
        EndSessionEndpoint = "https://identity.invalid/logout"
    };
    provider.SigningKeys.Add(publicJwtKey);
    builder.Services.Configure<JwtBearerOptions>(HostAuthentication.JwtScheme,
        options => options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(provider));
    builder.Services.Configure<OpenIdConnectOptions>(HostAuthentication.OidcScheme,
        options => options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(provider));
    await using var app = builder.Build();
    var executions = 0;
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/checks/proxy/trusted"))
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("172.31.240.2");
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("internal:8080");
        }
        await next(context);
    });
    app.UseHostProxy();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseHostCsrf();
    app.UseDomainConnectionGuard();
    app.UseOutputCache();
    app.MapHostAuthentication();
    app.MapSparkTelemetry();
    app.MapInferenceKeyManagement();
    app.MapAdGuardManagement();
    app.MapCloudflareDomains();
    app.MapDomainOnboarding();
    app.MapGet("/api/boot/identity", (HttpContext context) =>
        Results.Json(new { authenticated = context.User.Identity?.IsAuthenticated == true })).AllowAnonymous();
    app.MapGet("/api/host/status", () => Results.Json(new { startupError = "Synthetic model startup failure." })).RequireAuthorization("HostOwner");
    app.MapPost("/api/host/models", () => { executions++; return Results.Ok(); }).RequireAuthorization("HostOwner");
    app.MapGet("/v1/models", () => Results.Json(new { data = Array.Empty<object>() })).RequireAuthorization("HostInference");
    app.MapPost("/v1/chat/completions", async (HttpContext context) =>
    {
        executions++;
        context.Response.ContentType = "text/event-stream";
        await context.Response.WriteAsync("data: [DONE]\n\n");
    }).RequireAuthorization("HostInference");
    app.MapMethods("/api/host/mutate", ["POST", "PUT", "PATCH", "DELETE"], () => { executions++; return Results.Ok(); })
        .RequireAuthorization("HostOwner");
    app.MapGet("/checks/proxy/{kind}", (HttpContext context) => Results.Json(new
    {
        scheme = context.Request.Scheme, host = context.Request.Host.Value, remoteIp = context.Connection.RemoteIpAddress?.ToString()
    }));
    var cachedExecutions = 0;
    app.MapGet("/checks/cache", () => ++cachedExecutions).CacheOutput();
    app.MapHostWeb();
    await app.StartAsync();
    using var client = Client(app);

    var cookieOptions = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(HostAuthentication.CookieScheme);
    var oidcOptions = app.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(HostAuthentication.OidcScheme);
    string Cookie(string? role = "Owner", bool expired = false)
    {
        var claims = new List<Claim> { new("sub", "test-subject"), new("preferred_username", "owner"), new("name", "Test Owner") };
        if (role is not null) claims.Add(new(HostAuthentication.RoleClaim, role));
        var identity = new ClaimsIdentity(claims, HostAuthentication.CookieScheme, "preferred_username", HostAuthentication.RoleClaim);
        var properties = new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow.AddHours(-1),
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(expired ? -10 : 60)
        };
        properties.StoreTokens([new AuthenticationToken { Name = "id_token", Value = "synthetic-private-id-token-hint" }]);
        return cookieOptions.Cookie.Name + "=" + cookieOptions.TicketDataFormat.Protect(
            new AuthenticationTicket(new ClaimsPrincipal(identity), properties, HostAuthentication.CookieScheme));
    }
    var ownerCookie = Cookie();
    foreach (var path in new[] { "/api/host/connections/adguard", "/api/host/domains/cloudflare/credentials", "/api/host/domains/status" })
    {
        using var anonymous = await Send(client, path);
        using var inferenceOnly = await Send(client, path, authorization: "Bearer " + inferenceKey);
        using var owner = await Send(client, path, authorization: "Bearer " + ownerKey);
        Check(anonymous.StatusCode == HttpStatusCode.Unauthorized && inferenceOnly.StatusCode == HttpStatusCode.Forbidden
            && owner.StatusCode == HttpStatusCode.OK, "DNS/provider settings did not enforce Owner access.");
        Check(owner.Headers.CacheControl?.NoStore == true, "DNS configuration can be cached.");
    }
    using (var response = await Send(client, "/api/host/domains/start", "POST", authorization: "Bearer " + ownerKey,
        content: new StringContent("""{"planId":"11111111-1111-4111-8111-111111111111","reviewHash":"invalid","acceptTerms":false,"acceptDnsChanges":false}""", Encoding.UTF8, "application/json")))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "DNS setup started without explicit consent.");
    using (var response = await Send(client, "/api/host/connections/adguard", "DELETE", cookie: ownerCookie))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "Disconnecting AdGuard did not require cookie CSRF.");
    const string diagnoseRoute = "/api/host/domains/jobs/11111111-1111-4111-8111-111111111111/diagnose";
    using (var anonymous = await Send(client, "/api/host/domains/overview"))
    using (var inferenceOnly = await Send(client, "/api/host/domains/overview", authorization: "Bearer " + inferenceKey))
    using (var owner = await Send(client, "/api/host/domains/overview", authorization: "Bearer " + ownerKey))
        Check(anonymous.StatusCode == HttpStatusCode.Unauthorized && inferenceOnly.StatusCode == HttpStatusCode.Forbidden
            && owner.StatusCode == HttpStatusCode.Conflict, "Managed domain overview did not enforce Owner access and activation.");
    using (var anonymous = await Send(client, diagnoseRoute, "POST"))
    using (var inferenceOnly = await Send(client, diagnoseRoute, "POST", authorization: "Bearer " + inferenceKey))
    using (var noCsrf = await Send(client, diagnoseRoute, "POST", cookie: ownerCookie))
    using (var owner = await Send(client, diagnoseRoute, "POST", authorization: "Bearer " + ownerKey))
    {
        Check(anonymous.StatusCode == HttpStatusCode.Unauthorized && inferenceOnly.StatusCode == HttpStatusCode.Forbidden,
            "DNS diagnosis did not enforce Owner access.");
        Check(noCsrf.StatusCode == HttpStatusCode.Forbidden, "DNS diagnosis did not require cookie CSRF.");
        Check(owner.StatusCode == HttpStatusCode.Conflict, "DNS diagnosis accepted a nonexistent failed job.");
    }
    string managedKey;
    Guid managedKeyId;
    using (var response = await Send(client, "/api/host/inference-keys", "POST", authorization: "Bearer " + ownerKey,
        content: new StringContent("""{"name":"Synthetic external client","expiresAt":null}""", Encoding.UTF8, "application/json")))
    {
        Check(response.StatusCode == HttpStatusCode.OK, "Owner could not create an inference key.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        managedKey = json.RootElement.GetProperty("secret").GetString()!;
        managedKeyId = json.RootElement.GetProperty("key").GetProperty("id").GetGuid();
        Check(response.Headers.CacheControl?.NoStore == true, "One-time API secret response can be cached.");
    }
    using (var response = await Send(client, "/v1/models", authorization: "Bearer " + managedKey))
        Check(response.StatusCode == HttpStatusCode.OK, "Managed inference key could not use OpenAI endpoints.");
    using (var response = await Send(client, "/api/host/status", authorization: "Bearer " + managedKey, cookie: ownerCookie))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "Managed inference key inherited Owner access.");
    using (var response = await Send(client, "/api/host/inference-keys", authorization: "Bearer " + managedKey))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "Inference client could manage API keys.");
    using (var response = await Send(client, "/api/host/inference-keys", authorization: "Bearer " + ownerKey))
    {
        var body = await response.Content.ReadAsStringAsync();
        Check(!body.Contains(managedKey, StringComparison.Ordinal) && !body.Contains("\"hash\"", StringComparison.Ordinal),
            "API-key listing exposed a secret or its hash.");
    }
    using (var response = await Send(client, "/api/host/inference-keys/" + managedKeyId, "DELETE", authorization: "Bearer " + ownerKey))
        Check(response.StatusCode == HttpStatusCode.NoContent, "Owner could not revoke an inference key.");
    using (var response = await Send(client, "/v1/models", authorization: "Bearer " + managedKey, cookie: ownerCookie))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "Revoked key remained valid or fell back to a browser cookie.");
    foreach (var (authorization, expected) in new (string?, HttpStatusCode)[]
    {
        (null, HttpStatusCode.Unauthorized), ("Bearer " + inferenceKey, HttpStatusCode.Forbidden),
        ("Bearer " + ownerKey, HttpStatusCode.OK)
    })
    {
        using var response = await Send(client, "/api/host/telemetry", authorization: authorization);
        Check(response.StatusCode == expected, "Spark metrics escaped the Owner boundary.");
        if (expected == HttpStatusCode.OK)
            Check(response.Headers.CacheControl?.NoStore == true, "Private Spark metric responses can be cached by intermediaries.");
    }
    foreach (var (authorization, cookie) in new (string?, string?)[]
    {
        (null, ownerCookie), ("Bearer " + ownerKey, null), ("Bearer d1.synthetic-discovery-capability", ownerCookie)
    })
    {
        using var response = await Send(client, "/api/boot/identity", authorization: authorization, cookie: cookie);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(response.StatusCode == HttpStatusCode.OK && !json.RootElement.GetProperty("authenticated").GetBoolean(),
            "The discovery adapter inherited human or machine-key authority.");
    }
    Check(cookieOptions.Cookie.Name!.StartsWith("__Host-", StringComparison.Ordinal)
        && cookieOptions.Cookie.HttpOnly && cookieOptions.Cookie.SecurePolicy == CookieSecurePolicy.Always
        && cookieOptions.Cookie.Path == "/" && cookieOptions.Cookie.Domain is null && cookieOptions.Cookie.SameSite == SameSiteMode.Lax,
        "Session cookie security attributes are incorrect.");
    Check(!cookieOptions.SlidingExpiration && cookieOptions.ExpireTimeSpan == TimeSpan.FromHours(8), "Session lifetime is not bounded.");
    Check(oidcOptions.ResponseType == "code" && oidcOptions.UsePkce && !oidcOptions.SaveTokens
        && oidcOptions.Scope.Contains("lucia_api") && oidcOptions.RequireHttpsMetadata, "OIDC code/PKCE settings are incorrect.");
    var forwarded = app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
    Check(forwarded.ForwardLimit == 1 && forwarded.KnownProxies.Count == 0 && forwarded.KnownIPNetworks.Count == 1
        && forwarded.AllowedHosts.SequenceEqual(["localhost"]), "Proxy trust is wider than configured.");

    using (var anonymous = await Send(client, "/api/auth/session"))
    {
        Check(anonymous.StatusCode == HttpStatusCode.OK, "Anonymous session did not return 200.");
        using var json = JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync());
        Check(json.RootElement.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(
            new[] { "enabled", "authenticated", "username", "displayName", "isOwner", "canAccess", "csrfToken" }.Order()),
            "Session JSON shape differs from the agreed contract.");
        Check(json.RootElement.GetProperty("enabled").GetBoolean() && !json.RootElement.GetProperty("authenticated").GetBoolean()
            && json.RootElement.GetProperty("username").ValueKind == JsonValueKind.Null
            && json.RootElement.GetProperty("displayName").ValueKind == JsonValueKind.Null
            && !json.RootElement.GetProperty("isOwner").GetBoolean() && !json.RootElement.GetProperty("canAccess").GetBoolean()
            && json.RootElement.GetProperty("csrfToken").ValueKind == JsonValueKind.Null, "Anonymous session claims leaked.");
        Check(anonymous.Headers.CacheControl?.NoStore == true, "Session response can be cached.");
    }
    string csrfToken;
    string csrfCookie;
    using (var session = await Send(client, "/api/auth/session", cookie: ownerCookie))
    {
        var body = await session.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Check(json.RootElement.GetProperty("username").GetString() == "owner"
            && json.RootElement.GetProperty("displayName").GetString() == "Test Owner"
            && json.RootElement.GetProperty("isOwner").GetBoolean()
            && json.RootElement.GetProperty("canAccess").GetBoolean(), "Owner browser session did not expose the expected profile.");
        csrfToken = json.RootElement.GetProperty("csrfToken").GetString()!;
        var setCookie = session.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("__Host-Lucia.Csrf=", StringComparison.Ordinal));
        csrfCookie = setCookie.Split(';')[0];
        Check(setCookie.Contains("secure", StringComparison.OrdinalIgnoreCase) && setCookie.Contains("httponly", StringComparison.OrdinalIgnoreCase)
            && setCookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase), "Antiforgery cookie is insecure.");
        Check(!body.Contains("synthetic-private-id-token-hint", StringComparison.Ordinal)
            && !body.Contains(ownerKey, StringComparison.Ordinal) && !body.Contains("access_token", StringComparison.Ordinal), "Session leaked credentials.");
    }
    var ownerWithCsrf = ownerCookie + "; " + csrfCookie;
    foreach (var auth in new string?[] { null, "", "Basic invalid", "Bearer invalid", "Bearer " + ownerKey + "x" })
    {
        using var response = await Send(client, "/api/host/status", authorization: auth);
        Check(response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.Location is null
            && response.Content.Headers.ContentType?.MediaType == "application/json", "API challenge was not JSON 401.");
    }
    foreach (var auth in new[] { "", "Basic invalid", "Bearer invalid", "Bearer " + ownerKey + "x" })
    {
        using var response = await Send(client, "/api/host/status", authorization: auth, cookie: ownerCookie);
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "Invalid Authorization fell back to a valid cookie.");
        using var session = await Send(client, "/api/auth/session", authorization: auth, cookie: ownerCookie);
        using var json = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        Check(!json.RootElement.GetProperty("authenticated").GetBoolean(), "Session fell back to a cookie after invalid Authorization.");
    }
    using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/host/status"))
    {
        request.Headers.TryAddWithoutValidation("Authorization", ["Bearer " + ownerKey, "Bearer invalid"]);
        request.Headers.TryAddWithoutValidation("Cookie", ownerCookie);
        using var response = await client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "Multiple Authorization values bypassed authentication.");
    }

    foreach (var (role, adminStatus, inferenceStatus) in new[]
    {
        ("Owner", HttpStatusCode.OK, HttpStatusCode.OK),
        ("Inference", HttpStatusCode.Forbidden, HttpStatusCode.OK),
        ("Unassigned", HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)
    })
    {
        foreach (var cookieAuthentication in new[] { false, true })
        {
            var cookie = cookieAuthentication ? Cookie(role) : null;
            var auth = cookieAuthentication ? null : "Bearer " + Jwt(role);
            using var admin = await Send(client, "/api/host/status", authorization: auth, cookie: cookie);
            using var inference = await Send(client, "/v1/models", authorization: auth, cookie: cookie);
            Check(admin.StatusCode == adminStatus && inference.StatusCode == inferenceStatus, "Owner/inference role boundary failed.");
            if (adminStatus == HttpStatusCode.Forbidden)
                Check(admin.Content.Headers.ContentType?.MediaType == "application/json" && admin.Headers.Location is null, "Forbidden API redirected.");
            using var session = await Send(client, "/api/auth/session", authorization: auth, cookie: cookie);
            using var json = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
            Check(json.RootElement.GetProperty("isOwner").GetBoolean() == (role == "Owner")
                && json.RootElement.GetProperty("canAccess").GetBoolean() == (role != "Unassigned")
                && (json.RootElement.GetProperty("csrfToken").ValueKind == JsonValueKind.String) == cookieAuthentication,
                "Session role or CSRF classification is incorrect.");
        }
    }
    using var wrongSigningKey = RSA.Create(2048);
    foreach (var token in new[]
    {
        Jwt(scope: null), Jwt(scope: "openid profile"), Jwt(scope: "not_lucia_api"),
        Jwt(tokenIssuer: "https://other.invalid/"), Jwt(audience: "other-client"), Jwt(expired: true),
        Jwt(scope: null, idToken: true), Jwt(idToken: true), Jwt(key: new RsaSecurityKey(wrongSigningKey) { KeyId = jwtKey.KeyId }),
        Jwt(signed: false), Jwt(expiration: false), Jwt(future: true), Jwt(authorizedParty: "other-client")
    })
    {
        using var response = await Send(client, "/v1/models", authorization: "Bearer " + token, cookie: ownerCookie);
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "Invalid or ID token was accepted as an API token.");
    }
    using (var response = await Send(client, "/v1/models", authorization: "Bearer " + Jwt(scope: "openid lucia_api profile")))
        Check(response.StatusCode == HttpStatusCode.OK, "Space-separated API scope was rejected.");
    using (var response = await Send(client, "/v1/models", authorization: "Bearer " + Jwt(accessNonce: true)))
        Check(response.StatusCode == HttpStatusCode.OK, "An Authentik access token carrying a nonce was mistaken for an ID token.");
    using (var response = await Send(client, "/api/host/status", cookie: Cookie(expired: true)))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "Expired browser session was accepted.");
    using (var response = await Send(client, "/api/host/status", authorization: "Bearer " + ownerKey))
        Check(response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadAsStringAsync()).Contains("startupError", StringComparison.Ordinal),
            "Opaque owner key or authenticated startup diagnostics failed.");
    using (var response = await Send(client, "/api/host/status", authorization: "Bearer " + inferenceKey, cookie: ownerCookie))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "Inference key inherited owner cookie privileges.");

    var before = executions;
    foreach (var method in new[] { "POST", "PUT", "PATCH", "DELETE" })
    {
        using var response = await Send(client, "/api/host/mutate", method, cookie: ownerWithCsrf);
        Check(response.StatusCode == HttpStatusCode.Forbidden, "Unsafe cookie API did not require CSRF.");
    }
    using (var response = await Send(client, "/v1/chat/completions", "POST", cookie: ownerWithCsrf, csrf: "incorrect"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "Streaming inference accepted invalid CSRF.");
    Check(executions == before, "Rejected CSRF requests executed application work.");
    using (var response = await Send(client, "/v1/chat/completions", "POST", cookie: ownerWithCsrf, csrf: csrfToken))
        Check(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "text/event-stream",
            "Valid cookie CSRF did not reach streaming inference.");
    using (var response = await Send(client, "/api/host/models", "POST", cookie: ownerWithCsrf, csrf: csrfToken))
        Check(response.StatusCode == HttpStatusCode.OK, "Valid owner CSRF failed.");
    foreach (var bearer in new[] { ownerKey, inferenceKey, Jwt("Inference") })
    {
        using var response = await Send(client, "/v1/chat/completions", "POST", authorization: "Bearer " + bearer,
            cookie: ownerWithCsrf, csrf: "irrelevant");
        Check(response.StatusCode == HttpStatusCode.OK, "Bearer client incorrectly required CSRF.");
    }
    Check(executions == before + 5, "Request counter did not match authorized operations.");

    using (var response = await Send(client, "/auth/login?returnUrl=%2Fmodels%3Ftab%3Dchat"))
    {
        Check(response.StatusCode == HttpStatusCode.Redirect, "Login did not challenge OIDC.");
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Check(query["redirect_uri"] == publicOrigin + "/signin-oidc" && query["response_type"] == "code"
            && query["code_challenge_method"] == "S256" && query["code_challenge"].ToString().Length > 30,
            "Login redirect URI or PKCE challenge is wrong.");
        var properties = oidcOptions.StateDataFormat.Unprotect(query["state"].ToString());
        Check(properties?.RedirectUri == publicOrigin + "/models?tab=chat", "Post-login return path escaped PublicOrigin.");
        Check(response.Headers.GetValues("Set-Cookie").All(value => value.Contains("secure", StringComparison.OrdinalIgnoreCase)
            && value.Contains("httponly", StringComparison.OrdinalIgnoreCase) && value.Contains("samesite=none", StringComparison.OrdinalIgnoreCase)),
            "OIDC correlation/nonce cookies cannot safely support form_post.");
    }
    using (var response = await Send(client, "/auth/login?returnUrl=%2F%2Fevil.invalid"))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "Login endpoint allowed an open redirect.");
    using (var response = await Send(client, "/auth/logout", "POST", cookie: ownerWithCsrf))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "Logout did not require CSRF.");
    foreach (var form in new[] { false, true })
    {
        using var response = await Send(client, "/auth/logout", "POST", cookie: ownerWithCsrf, csrf: form ? null : csrfToken,
            content: form ? new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = csrfToken }) : null);
        Check(response.StatusCode == HttpStatusCode.Redirect, "Logout did not redirect to OIDC end-session.");
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Check(query["post_logout_redirect_uri"] == publicOrigin + "/signout-callback-oidc"
            && query["id_token_hint"] == "synthetic-private-id-token-hint", "Logout callback or normal ID-token hint is missing.");
        Check(response.Headers.GetValues("Set-Cookie").Any(value => value.StartsWith("__Host-Lucia.Session=;", StringComparison.Ordinal)),
            "Logout did not clear the session cookie.");
        var properties = oidcOptions.StateDataFormat.Unprotect(query["state"].ToString());
        Check(properties?.RedirectUri == publicOrigin + "/", "Logout return URL is not fixed to PublicOrigin.");
    }
    using (var response = await Send(client, "/auth/logout", "POST", authorization: "Bearer " + ownerKey))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "Machine key could initiate browser logout.");
    using (var response = await Send(client, "/auth/logout"))
        Check(response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound
            && response.Content.Headers.ContentType?.MediaType != "text/html", "GET could initiate logout or returned SPA HTML.");

    var validated = new Microsoft.AspNetCore.Authentication.OpenIdConnect.TokenValidatedContext(new DefaultHttpContext(), new AuthenticationScheme(HostAuthentication.OidcScheme, null,
        typeof(OpenIdConnectHandler)), oidcOptions, new ClaimsPrincipal(new ClaimsIdentity()), new AuthenticationProperties())
    {
        TokenEndpointResponse = new OpenIdConnectMessage { IdToken = "id-only", AccessToken = "never-save", RefreshToken = "never-save-refresh" }
    };
    await oidcOptions.Events.TokenValidated(validated);
    var validatedProperties = validated.Properties ?? throw new InvalidOperationException("OIDC properties were lost.");
    Check(validatedProperties.GetTokens().Single().Name == "id_token" && validatedProperties.AllowRefresh == false
        && validatedProperties.IsPersistent
        && validatedProperties.ExpiresUtc - validatedProperties.IssuedUtc <= HostAuthentication.SessionLifetime + TimeSpan.FromSeconds(1),
        "OIDC saved unnecessary tokens, a session-only cookie, or unbounded session properties.");
    string persistentCookie;
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Scheme = "https";
        var identity = new ClaimsIdentity(
            [new Claim("sub", "test-subject"), new Claim("preferred_username", "owner"), new Claim(HostAuthentication.RoleClaim, "Owner")],
            HostAuthentication.CookieScheme, "preferred_username", HostAuthentication.RoleClaim);
        await context.SignInAsync(HostAuthentication.CookieScheme, new ClaimsPrincipal(identity), validatedProperties);
        var header = context.Response.Headers.SetCookie.Single(value =>
            value?.StartsWith("__Host-Lucia.Session=", StringComparison.Ordinal) == true)!;
        var cookie = Microsoft.Net.Http.Headers.SetCookieHeaderValue.Parse(header);
        Check(cookie.Expires > DateTimeOffset.UtcNow.AddHours(7)
            && cookie.Expires <= DateTimeOffset.UtcNow + HostAuthentication.SessionLifetime,
            "Browser session cookie has no bounded persistent expiry.");
        persistentCookie = header.Split(';')[0];
    }
    for (var refresh = 0; refresh < 2; refresh++)
    {
        using var response = await Send(client, "/api/auth/session", cookie: persistentCookie);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(json.RootElement.GetProperty("authenticated").GetBoolean()
            && json.RootElement.GetProperty("canAccess").GetBoolean(), "Persisted cookie did not survive a page/session refresh.");
    }
    Check(Directory.EnumerateFiles(Settings().DataProtectionKeysDirectory, "*.xml").Any(), "Data Protection keys were not persisted.");

    using (var response = await Send(client, "/checks/proxy/untrusted", headers: new()
    {
        ["X-Forwarded-Proto"] = "http", ["X-Forwarded-Host"] = "evil.invalid", ["X-Forwarded-For"] = "192.0.2.1"
    }))
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(response.StatusCode == HttpStatusCode.OK && json.RootElement.GetProperty("scheme").GetString() == "https"
            && json.RootElement.GetProperty("host").GetString() == "localhost:8443"
            && json.RootElement.GetProperty("remoteIp").GetString() != "192.0.2.1", "Untrusted forwarding headers were processed.");
    }
    using (var response = await Send(client, "/checks/proxy/trusted", headers: new()
    {
        ["X-Forwarded-Proto"] = "https", ["X-Forwarded-Host"] = "localhost:8443", ["X-Forwarded-For"] = "192.0.2.1"
    }))
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(response.StatusCode == HttpStatusCode.OK && json.RootElement.GetProperty("remoteIp").GetString() == "192.0.2.1",
            "Configured proxy network was not honored.");
    }
    using (var response = await Send(client, "/checks/proxy/trusted", headers: new()
    {
        ["X-Forwarded-Proto"] = "http, https", ["X-Forwarded-Host"] = "evil.invalid, localhost:8443",
        ["X-Forwarded-For"] = "192.0.2.1, 172.31.240.9"
    }))
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(response.StatusCode == HttpStatusCode.OK && json.RootElement.GetProperty("remoteIp").GetString() == "172.31.240.9",
            "Forwarded headers exceeded one hop.");
    }
    using (var response = await Send(client, "/checks/proxy/trusted", headers: new()
    {
        ["X-Forwarded-Proto"] = "https", ["X-Forwarded-Host"] = "evil.invalid", ["X-Forwarded-For"] = "192.0.2.1"
    }))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "Proxy could supply an unapproved Host.");

    using var httpClient = Client(app, secure: false);
    httpClient.DefaultRequestHeaders.Host = "internal:8080";
    using (var response = await Send(httpClient, "/health/live"))
        Check(response.StatusCode == HttpStatusCode.OK && await response.Content.ReadAsStringAsync() == "{\"status\":\"ok\"}",
            "Container liveness requires SSO, HTTPS, model startup, or exposes details.");
    using (var response = await Send(httpClient, "/api/auth/session"))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "Insecure direct access bypassed origin enforcement.");

    foreach (var path in new[] { "/", "/dashboard/models" })
    {
        using var response = await Send(client, path);
        Check(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "text/html"
            && (await response.Content.ReadAsStringAsync()).Contains("Auth checks SPA", StringComparison.Ordinal), "SPA navigation fallback failed.");
    }
    foreach (var path in new[] { "/api/missing", "/api/missing.json", "/v1/missing", "/auth/missing", "/health/missing", "/signin-oidc/missing" })
    {
        using var response = await Send(client, path);
        Check(response.StatusCode == HttpStatusCode.NotFound && response.Content.Headers.ContentType?.MediaType != "text/html",
            "Unknown API/auth endpoint returned SPA HTML.");
    }
    Check(await client.GetStringAsync("/checks/cache") == await client.GetStringAsync("/checks/cache") && cachedExecutions == 1,
        "In-memory OutputCache fallback failed.");

    using var untrustedAuthorityKey = RSA.Create(2048);
    using var untrustedAuthority = CreateAuthority(untrustedAuthorityKey);
    using (var wrongCaClient = Client(app, untrustedAuthority))
    {
        try
        {
            using var response = await wrongCaClient.GetAsync("/health/live");
            throw new InvalidOperationException("TLS accepted a server signed by a different CA.");
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.SecureConnectionError) { checks++; }
    }
    using (var nativeClient = new HttpClient(HostAuthenticationOptions.CreateBackchannelHandler(authority, true))
    {
        Timeout = TimeSpan.FromSeconds(15)
    })
    {
        try
        {
            using var response = await nativeClient.GetAsync(client.BaseAddress + "health/live");
            throw new InvalidOperationException("Public-authority system trust accepted the untrusted private LDAP CA.");
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.SecureConnectionError) { checks++; }
    }
    using (var wrongNameClient = Client(app))
    {
        wrongNameClient.DefaultRequestHeaders.Host = "wrong-name.invalid";
        try
        {
            using var response = await wrongNameClient.GetAsync("/health/live");
            throw new InvalidOperationException("TLS accepted a certificate with a mismatched hostname.");
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.SecureConnectionError) { checks++; }
    }
    await app.StopAsync();

    var restarted = Builder();
    restarted.Configuration.AddConfiguration(builder.Configuration);
    restarted.AddHostAuthentication();
    await using (var restartedApp = restarted.Build())
    {
        restartedApp.UseHostProxy();
        restartedApp.UseAuthentication();
        restartedApp.UseAuthorization();
        restartedApp.MapHostAuthentication();
        await restartedApp.StartAsync();
        using var restartedClient = Client(restartedApp);
        using var response = await Send(restartedClient, "/api/auth/session", cookie: persistentCookie);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(json.RootElement.GetProperty("authenticated").GetBoolean()
            && json.RootElement.GetProperty("isOwner").GetBoolean(), "Host restart lost the persisted browser session.");
        await restartedApp.StopAsync();
    }

    var migrated = Builder();
    migrated.Configuration.AddConfiguration(builder.Configuration);
    migrated.Configuration["DomainOnboarding:StateDirectory"] = domainDirectory;
    Check(DomainActivationConfiguration.Apply(migrated) == profile, "The reviewed domain profile was not applied.");
    migrated.AddHostAuthentication();
    var newIssuer = profile.CanonicalAuthentikOrigin + "/application/o/lucia/";
    var migratedProvider = new OpenIdConnectConfiguration
    {
        Issuer = newIssuer, AuthorizationEndpoint = profile.CanonicalAuthentikOrigin + "/authorize",
        TokenEndpoint = profile.CanonicalAuthentikOrigin + "/token", EndSessionEndpoint = profile.CanonicalAuthentikOrigin + "/logout"
    };
    migratedProvider.SigningKeys.Add(publicJwtKey);
    migrated.Services.Configure<JwtBearerOptions>(HostAuthentication.JwtScheme,
        options => options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(migratedProvider));
    migrated.Services.Configure<OpenIdConnectOptions>(HostAuthentication.OidcScheme,
        options => options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(migratedProvider));
    await using (var migratedApp = migrated.Build())
    {
        var settings = migratedApp.Services.GetRequiredService<HostAuthenticationOptions>();
        Check(settings.PublicOrigin == profile.CanonicalLuciaOrigin && settings.Authority == newIssuer
            && settings.AuthorityUsesSystemTrust && settings.AdditionalPublicOrigins.SequenceEqual([publicOrigin])
            && settings.CaCertificatePath == Settings().CaCertificatePath
            && settings.ClientSecretFile == Settings().ClientSecretFile && settings.ClientId == clientId
            && settings.DataProtectionKeysDirectory == Settings().DataProtectionKeysDirectory
            && settings.TrustedProxyNetworks.SequenceEqual(Settings().TrustedProxyNetworks),
            "Activation altered legacy credentials, proxy networks, private CA, or Data Protection storage.");
        Check(migratedApp.Services.GetRequiredService<X509Certificate2>().Thumbprint == authority.Thumbprint,
            "Migration failed to load the private CA for legacy services.");
        var migratedOidc = migratedApp.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(HostAuthentication.OidcScheme);
        var migratedJwt = migratedApp.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(HostAuthentication.JwtScheme);
        foreach (var handler in new[] { migratedOidc.BackchannelHttpHandler, migratedJwt.BackchannelHttpHandler })
            Check(handler is SocketsHttpHandler sockets && sockets.SslOptions.CertificateChainPolicy is null
                && sockets.SslOptions.RemoteCertificateValidationCallback is null && !sockets.AllowAutoRedirect,
                "Migrated OIDC/JWT does not use the safe native system trust backchannel.");
        var migratedForwarded = migratedApp.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        Check(migratedForwarded.AllowedHosts.Order().SequenceEqual(new[] { "lucia.homelab.example.com", "localhost" }.Order()),
            "Forwarding did not allow exactly the canonical and legacy hosts.");
        migratedApp.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("172.31.240.2");
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("internal:8080");
            await next(context);
        });
        migratedApp.UseHostProxy();
        migratedApp.UseAuthentication();
        migratedApp.UseAuthorization();
        migratedApp.UseHostCsrf();
        migratedApp.MapHostAuthentication();
        migratedApp.MapGet("/v1/models", () => Results.Ok()).RequireAuthorization("HostInference");
        await migratedApp.StartAsync();
        using var migratedClient = Client(migratedApp, secure: false);
        Dictionary<string, string> ProxyHeaders(string host, string scheme = "https") => new()
        {
            ["X-Forwarded-Proto"] = scheme, ["X-Forwarded-Host"] = host, ["X-Forwarded-For"] = "192.0.2.9"
        };
        foreach (var host in new[] { "lucia.homelab.example.com", "localhost:8443" })
        {
            using var api = await Send(migratedClient, "/v1/models", authorization: "Bearer " + inferenceKey, headers: ProxyHeaders(host));
            Check(api.StatusCode == HttpStatusCode.OK, "Canonical or legacy-origin API keys stopped working.");
            using var previousJwt = await Send(migratedClient, "/v1/models", authorization: "Bearer " + Jwt(), headers: ProxyHeaders(host));
            Check(previousJwt.StatusCode == HttpStatusCode.Unauthorized, "Migration accepted an old-issuer JWT.");
            using var currentJwt = await Send(migratedClient, "/v1/models", authorization: "Bearer " + Jwt(tokenIssuer: newIssuer),
                headers: ProxyHeaders(host));
            Check(currentJwt.StatusCode == HttpStatusCode.OK, "Migration rejected a new-issuer JWT.");
        }
        foreach (var host in new[] { "localhost", "localhost:443", "localhost:9443", "lucia.homelab.example.com:8443",
            "lucia.homelab.example.com.evil.invalid", "auth.homelab.example.com" })
        {
            using var response = await Send(migratedClient, "/v1/models", authorization: "Bearer " + inferenceKey, headers: ProxyHeaders(host));
            Check(response.StatusCode == HttpStatusCode.BadRequest, "An alias hostname/port mismatch bypassed origin enforcement.");
        }
        using (var response = await Send(migratedClient, "/v1/models", authorization: "Bearer " + inferenceKey,
            headers: ProxyHeaders("localhost:8443", "http")))
            Check(response.StatusCode == HttpStatusCode.BadRequest, "Legacy alias accepted insecure forwarding.");
        using (var response = await Send(migratedClient, "/auth/login?returnUrl=%2Fmodels", headers: ProxyHeaders("localhost:8443")))
            Check(response.StatusCode == HttpStatusCode.Redirect
                && response.Headers.Location!.AbsoluteUri == profile.CanonicalLuciaOrigin + "/auth/login?returnUrl=%2Fmodels"
                && !response.Headers.Contains("Set-Cookie"), "Alias login issued unusable cookies instead of moving to the canonical origin.");
        using (var response = await Send(migratedClient, "/auth/login?returnUrl=%2Fmodels", headers: ProxyHeaders("lucia.homelab.example.com")))
        {
            var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
            Check(query["redirect_uri"] == profile.CanonicalLuciaOrigin + "/signin-oidc"
                && migratedOidc.StateDataFormat.Unprotect(query["state"].ToString())?.RedirectUri == profile.CanonicalLuciaOrigin + "/models",
                "New login callback or return URL is not canonical.");
        }
        using (var response = await Send(migratedClient, "/api/auth/session", headers: ProxyHeaders("lucia.homelab.example.com")))
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Check(!json.RootElement.GetProperty("authenticated").GetBoolean(), "New origin did not require fresh browser SSO.");
        }
        Check(migratedOidc.SignedOutRedirectUri == profile.CanonicalLuciaOrigin + "/",
            "Migrated logout return URL is not canonical.");
        await migratedApp.StopAsync();
    }

    var development = Builder(Environments.Development);
    development.AddHostAuthentication();
    await using var developmentApp = development.Build();
    developmentApp.UseAuthentication();
    developmentApp.UseAuthorization();
    developmentApp.UseHostCsrf();
    developmentApp.MapHostAuthentication();
    developmentApp.MapPost("/v1/chat/completions", () => Results.Ok()).RequireAuthorization("HostInference");
    await developmentApp.StartAsync();
    using var developmentClient = Client(developmentApp, secure: false);
    using (var response = await Send(developmentClient, "/api/auth/session"))
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(response.StatusCode == HttpStatusCode.OK && !json.RootElement.GetProperty("enabled").GetBoolean(),
            "Development session contract changed.");
    }
    using (var response = await Send(developmentClient, "/v1/chat/completions", "POST", authorization: "Bearer " + inferenceKey))
        Check(response.StatusCode == HttpStatusCode.OK, "Development static-key inference broke.");
    using (var response = await Send(developmentClient, "/auth/login"))
        Check(response.StatusCode == HttpStatusCode.NotFound, "Disabled SSO challenged a provider.");
    await developmentApp.StopAsync();
    Console.WriteLine($"Host authentication checks passed ({checks} assertions). Synthetic JWT/OIDC configuration, real cookies/CSRF/TLS/middleware; no live OIDC callback or model loading.");
}
finally
{
    Directory.Delete(rootDirectory, recursive: true);
}

static X509Certificate2 CreateAuthority(RSA key)
{
    var request = new CertificateRequest("CN=Lucia synthetic checks CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
    request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
    return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
}

static X509Certificate2 CreateServerCertificate(X509Certificate2 authority, RSA key)
{
    var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
    var names = new SubjectAlternativeNameBuilder();
    names.AddDnsName("localhost");
    names.AddIpAddress(IPAddress.Loopback);
    request.CertificateExtensions.Add(names.Build());
    using var certificate = request.Create(authority, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(12),
        RandomNumberGenerator.GetBytes(16));
    using var withKey = certificate.CopyWithPrivateKey(key);
    // Windows Schannel needs a PKCS#12-imported key rather than CopyWithPrivateKey's ephemeral key.
    return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
}
