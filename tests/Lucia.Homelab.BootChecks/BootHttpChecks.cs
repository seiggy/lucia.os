using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Lucia.Homelab.Server.Boot;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Host;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

internal static class BootHttpChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "lucia-boot-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HardwareOnboarding:StateDirectory"] = Path.Combine(directory, "state"),
                ["HardwareOnboarding:DiscoveryNetworkCidr"] = "192.168.0.0/23",
                ["HardwareOnboarding:BootBaseUrl"] = "https://spark.test",
                ["HardwareOnboarding:DiscoveryAdapterQualified"] = "true",
                ["Boot:Enabled"] = "true",
                ["Boot:ControlDirectory"] = Path.Combine(directory, "control"),
                ["Boot:AllowedNetworks:0"] = "192.168.0.0/23"
            });
            builder.Services.AddAuthentication("fixture")
                .AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("fixture", null);
            builder.Services.AddAuthorizationBuilder().AddPolicy("HostOwner", policy => policy.RequireRole("Owner"));
            builder.AddHardwareOnboarding();
            builder.AddHardwareBoot();
            builder.Services.AddSingleton(new HostAuthenticationOptions());
            builder.Services.AddSingleton<ManagedNodeEnrollment>();
            builder.Services.AddSingleton<OwnerSshKeys>();
            builder.Services.AddSingleton(new Lucia.Homelab.Server.Domains.DomainOnboardingOptions { StateDirectory = Path.Combine(directory, "domains") });
            builder.Services.AddSingleton<Lucia.Homelab.Server.Domains.DomainOnboardingStore>();
            await using var app = builder.Build();
            app.Use((context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers.ContainsKey("X-Outside-Fixture")
                    ? "192.168.2.1" : "192.168.1.42");
                return next(context);
            });
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapHardwareOnboarding();
            app.MapHardwareBoot();
            app.MapManagedInstallation();
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            async Task<HttpResponseMessage> Send(string path, HttpMethod? method = null, object? body = null,
                string? token = null, bool outside = false)
            {
                using var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
                if (body is not null) request.Content = JsonContent.Create(body);
                if (token is not null) request.Headers.Authorization = new("Bearer", token);
                if (outside) request.Headers.Add("X-Outside-Fixture", "1");
                return await client.SendAsync(request);
            }
            using (var closed = await Send("/api/boot/challenge"))
                check(closed.StatusCode == HttpStatusCode.Forbidden, "Discovery was open by default.");
            using (var anonymous = await Send("/api/host/onboarding/window", HttpMethod.Post, new { minutes = 30 }))
                check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "Anonymous caller opened onboarding.");
            using (var member = await Send("/api/host/onboarding/window", HttpMethod.Post, new { minutes = 30 }, "fixture-member"))
                check(member.StatusCode == HttpStatusCode.Forbidden, "Inference-only caller opened onboarding.");
            using (var opened = await Send("/api/host/onboarding/window", HttpMethod.Post, new { minutes = 30 }, "fixture-owner"))
                check(opened.StatusCode == HttpStatusCode.OK, "Owner could not open qualified discovery.");
            using (var outside = await Send("/api/boot/challenge", outside: true))
                check(outside.StatusCode == HttpStatusCode.Forbidden, "Discovery accepted an unconfigured subnet.");
            using (var configuration = await Send("/api/boot/discovery.cfg"))
            {
                var body = await configuration.Content.ReadAsStringAsync();
                check(configuration.StatusCode == HttpStatusCode.OK && body.Contains("discover-and-wait", StringComparison.Ordinal)
                    && !body.Contains("partman-auto", StringComparison.Ordinal)
                    && configuration.Headers.CacheControl?.NoStore == true, "Discovery preseed contains installation authority or permits caching.");
            }

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var inventory = new HardwareReport("x86_64", "uefi", false, null, "Synthetic server", null,
                "11111111-1111-1111-1111-111111111111", "Synthetic CPU", 4, 16L * 1024 * 1024 * 1024,
                [new("eth0", "02:00:00:00:00:01", ["192.168.1.42"])],
                [new("/dev/disk/by-id/ata-lucia-fixture", "/dev/sda", "Synthetic disk", "fixture-disk",
                    64L * 1024 * 1024 * 1024, false, false)]);
            var report = JsonSerializer.Serialize(inventory, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            async Task<SignedDiscovery> Proof()
            {
                var challenge = await client.GetFromJsonAsync<DiscoveryChallenge>("/api/boot/challenge")
                    ?? throw new InvalidOperationException("Challenge response missing.");
                return new(challenge.ChallengeId, key.ExportSubjectPublicKeyInfoPem(), report,
                    Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(
                        $"lucia-discovery-v1\n{challenge.ChallengeId}\n{challenge.Nonce}\n{report}"),
                        HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
            }
            var proof = await Proof();
            using (var bad = await Send("/api/boot/discover", HttpMethod.Post, proof with { ReportJson = report + " " }))
                check(bad.StatusCode == HttpStatusCode.Unauthorized, "Tampered hardware report registered.");
            Guid deviceId;
            string token;
            string code;
            using (var registered = await Send("/api/boot/discover", HttpMethod.Post, proof))
            {
                check(registered.StatusCode == HttpStatusCode.OK, "Signed inventory failed registration.");
                using var body = JsonDocument.Parse(await registered.Content.ReadAsStringAsync());
                deviceId = body.RootElement.GetProperty("deviceId").GetGuid();
                token = body.RootElement.GetProperty("token").GetString()!;
                code = body.RootElement.GetProperty("verificationCode").GetString()!;
                check(body.RootElement.EnumerateObject().Count() == 4 && code.Length == 14
                    && body.RootElement.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow,
                    "Registration contract differs from the native discovery client.");
            }
            using (var replay = await Send("/api/boot/discover", HttpMethod.Post, proof))
                check(replay.StatusCode == HttpStatusCode.Unauthorized, "Discovery proof replay registered another device.");
            using (var duplicate = await Send("/api/boot/discover", HttpMethod.Post, await Proof()))
                check(duplicate.StatusCode == HttpStatusCode.Conflict, "Repeated registration leaked or renewed a capability.");
            using (var read = await Send($"/api/boot/devices/{deviceId}", token: token))
            {
                check(read.StatusCode == HttpStatusCode.OK, "Device-specific capability could not read its own status.");
                using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
                check(body.RootElement.GetProperty("phase").GetString() == "Discovered"
                    && body.RootElement.GetProperty("verificationCode").GetString() == code
                    && body.RootElement.GetProperty("heartbeatFreshness").GetString() == "Unknown",
                    "Discovery was mistaken for managed health or lost its physical matching code.");
            }
            foreach (var path in new[] { $"/api/boot/devices/{deviceId}", $"/api/boot/devices/{Guid.NewGuid()}" })
            {
                using var absent = await Send(path);
                check(absent.StatusCode == HttpStatusCode.Forbidden, "An anonymous device status read succeeded.");
            }
            using (var other = await Send($"/api/boot/devices/{Guid.NewGuid()}", token: token))
                check(other.StatusCode == HttpStatusCode.Forbidden, "Capability crossed the device boundary.");
            using (var corrupted = await Send($"/api/boot/devices/{deviceId}", token: token[..^1] + (token[^1] == '0' ? "1" : "0")))
                check(corrupted.StatusCode == HttpStatusCode.Forbidden, "A corrupted capability authenticated.");
            using (var owner = await Send("/api/host/onboarding", token: token))
                check(owner.StatusCode == HttpStatusCode.Unauthorized, "Device capability acquired owner access.");
            using (var disabled = await Send($"/api/host/devices/{deviceId}/approve-install", HttpMethod.Post,
                new { hostname = "fixture", diskId = inventory.Disks[0].Id, confirmation = "ERASE" }, "fixture-owner"))
                check(disabled.StatusCode == HttpStatusCode.ServiceUnavailable, "Unqualified installation could be approved.");
            using (var configuration = await Send($"/api/boot/devices/{deviceId}/installation", token: token))
            {
                using var body = JsonDocument.Parse(await configuration.Content.ReadAsStringAsync());
                check(configuration.StatusCode == HttpStatusCode.OK && !body.RootElement.GetProperty("canRequestInstallationGrant").GetBoolean()
                    && body.RootElement.GetProperty("taskId").ValueKind == JsonValueKind.Null,
                    "Discovery was given installation authority without approval.");
            }
            foreach (var suffix in new[] { "installation", "challenge" })
            {
                using var denied = await Send($"/api/boot/devices/{deviceId}/{suffix}");
                check(denied.StatusCode == HttpStatusCode.Forbidden, "Installation transport accepted an anonymous session.");
                using var outside = await Send($"/api/boot/devices/{deviceId}/{suffix}", token: token, outside: true);
                check(outside.StatusCode == HttpStatusCode.Forbidden, "Installation transport accepted an unconfigured network.");
            }
            using (var denied = await Send($"/api/boot/devices/{deviceId}/progress", HttpMethod.Post,
                new { phase = "Installing", message = "Synthetic forbidden transition" }, token))
                check(denied.StatusCode == HttpStatusCode.Conflict, "An unapproved device reported installation progress.");
            using (var denied = await Send("/api/host/nodes", token: token))
                check(denied.StatusCode == HttpStatusCode.Unauthorized, "A discovery capability read the Owner managed-node registry.");
            using (var closed = await Send("/api/host/onboarding/window", HttpMethod.Delete, token: "fixture-owner"))
                check(closed.StatusCode == HttpStatusCode.OK, "Owner could not stop accepting new hardware.");
            using (var closedChallenge = await Send("/api/boot/challenge"))
                check(closedChallenge.StatusCode == HttpStatusCode.Forbidden, "Closing admission still allowed new challenges.");
            using (var status = await Send($"/api/boot/devices/{deviceId}", token: token))
                check(status.StatusCode == HttpStatusCode.OK, "Closing admission stranded an admitted device.");
            using (var challenge = await Send($"/api/boot/devices/{deviceId}/challenge", token: token))
                check(challenge.StatusCode == HttpStatusCode.OK, "Closing admission stranded an existing authenticated installation session.");
            await app.StopAsync();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers.Authorization.ToString() switch
            {
                "Bearer fixture-owner" => "Owner",
                "Bearer fixture-member" => "Inference",
                _ => null
            };
            if (role is null) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim("sub", "synthetic-subject"), new Claim(ClaimTypes.Role, role)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
