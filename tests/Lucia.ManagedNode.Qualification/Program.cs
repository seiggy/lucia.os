using System.Net;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using Lucia.Homelab.Server.Boot;
using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

// Opt-in controller for a disposable QEMU guest. Never run against live state.
if (args is not ["--fixture", var root] || !Path.IsPathFullyQualified(root)
    || !File.Exists(Path.Combine(root, ".lucia-disposable-install-fixture")))
    throw new InvalidOperationException("An explicitly marked disposable fixture directory is required.");
var marker = File.ReadAllText(Path.Combine(root, ".lucia-disposable-install-fixture"));
if (marker.Trim() != "no-live-disks-or-identities-v1") throw new InvalidOperationException("Invalid fixture marker.");
var key = File.ReadAllText(Path.Combine(root, "owner-key")).Trim();
if (key.Length < 32) throw new InvalidOperationException("A private fixture owner key is required.");
using var certificate = X509Certificate2.CreateFromPemFile(Path.Combine(root, "server.crt"), Path.Combine(root, "server.key"));
var chain = new X509Certificate2Collection();
chain.ImportFromPem(File.ReadAllText(Path.Combine(root, "server.crt")));
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole();
builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 19443, endpoint => endpoint.UseHttps(tls =>
{
    tls.ServerCertificate = certificate;
    tls.ServerCertificateChain = chain;
})));
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["HardwareOnboarding:StateDirectory"] = Path.Combine(root, "data", "onboarding"),
    ["HardwareOnboarding:DiscoveryNetworkCidr"] = "192.168.0.0/23",
    ["HardwareOnboarding:BootBaseUrl"] = "https://192.168.0.222:19443",
    ["HardwareOnboarding:DiscoveryAdapterQualified"] = "true",
    ["HardwareOnboarding:BootArtifactsQualified"] = "true",
    ["HardwareOnboarding:EnrollmentQualified"] = "true",
    ["HardwareOnboarding:InstallationEnabled"] = "true",
    ["Boot:Enabled"] = "true",
    ["Boot:ControlDirectory"] = Path.Combine(root, "data", "boot-control"),
    ["Boot:AllowedNetworks:0"] = "192.168.0.0/23"
});
builder.Services.AddSingleton(new FixtureOwnerKey(key));
builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureOwnerAuthentication>("fixture", null);
builder.Services.AddAuthorizationBuilder().AddPolicy("HostOwner", policy => policy.RequireAuthenticatedUser().RequireRole("Owner"));
builder.Services.AddSingleton(new HostAuthenticationOptions
{
    PublicOrigin = "https://192.168.0.222:19443", CaCertificatePath = Path.Combine(root, "root.crt")
});
builder.AddHardwareOnboarding();
builder.AddHardwareBoot();
builder.Services.AddSingleton<ManagedNodeEnrollment>();
builder.Services.AddSingleton<OwnerSshKeys>();
var app = builder.Build();
app.Use((context, next) =>
{
    // The QEMU user-network gateway reaches this loopback-only fixture listener.
    context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.0.15");
    return next(context);
});
app.UseAuthentication();
app.UseAuthorization();
app.MapHardwareOnboarding();
app.MapHardwareBoot();
app.MapManagedInstallation();
app.MapOwnerSshKeys();
app.MapGet("/health/live", () => Results.Json(new { status = "ok", fixture = true }));
await app.RunAsync();

internal sealed record FixtureOwnerKey(string Value);
internal sealed class FixtureOwnerAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, FixtureOwnerKey key) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.Authorization.ToString() != "Bearer " + key.Value)
            return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "disposable-vm-owner"), new(ClaimTypes.Role, "Owner"),
            new("preferred_username", "qualification-owner")], "fixture");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "fixture")));
    }
}
