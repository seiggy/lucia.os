using System.Text.Json;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Host;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

internal static class RecoveryChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "lucia-domain-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DomainOnboardingOptions { StateDirectory = Path.Combine(root, "domains"), GatewayDirectory = Path.Combine(root, "gateway") };
            Directory.CreateDirectory(options.GatewayDirectory);
            using var store = new DomainOnboardingStore(options);
            var now = DateTimeOffset.UtcNow;
            var naming = DomainNames.Plan(new("example.com", "lab", "atlas"), "example.com");
            var plan = new DomainSetupPlan(Guid.NewGuid(), "", now, now.AddMinutes(30), new string('a', 32), new string('b', 32),
                naming, "192.168.0.222", "owner@example.com", 60, "https://letsencrypt.org/documents/example.pdf",
                "https://adguard.example.com", "owner", [new(naming.LocalHostnames[0], "192.168.0.222", false)], [], []);
            plan = plan with { ReviewHash = DomainOnboardingStore.Hash(plan) };
            var job = new DomainSetupJob(Guid.NewGuid(), "Running", "LocalDns", "Synthetic interrupted DNS mutation", "owner", now, now,
                plan, [], [naming.LocalHostnames[0]], PendingRewrites: [naming.LocalHostnames[1]],
                IngressBefore: "{\"prior\":true}", IngressPublished: "{\"pending\":true}");
            var gateway = Path.Combine(options.GatewayDirectory, DomainIngressConfiguration.FileName);
            await File.WriteAllTextAsync(gateway, job.IngressPublished);
            await store.Update(current => current with { Job = job });
            var dns = new FakeDns
            {
                Entries = [new(naming.LocalHostnames[0], plan.IngressAddress), new(naming.LocalHostnames[1], plan.IngressAddress),
                    new("unrelated.example.com", "192.168.0.99")]
            };
            using var client = new HttpClient(new NoNetwork());
            var cloudflare = new CloudflareDomainService(Options.Create(new CloudflareDomainOptions
            { CredentialsDirectory = Path.Combine(root, "credentials") }), DataProtectionProvider.Create(Path.Combine(root, "keys")), client);
            var auth = new HostAuthenticationOptions { PublicOrigin = "https://legacy.invalid", Authority = "https://legacy.invalid:9443/application/o/lucia/" };
            using var gate = new DomainConnectionGate();
            var preflight = new DomainDnsPreflight(client);
            var service = new DomainOnboardingService(options, store, cloudflare, dns, preflight, client, auth, gate);
            var heartbeat = Path.Combine(store.Root, "activation-worker.json");
            await File.WriteAllTextAsync(heartbeat, JsonSerializer.Serialize(new { schemaVersion = 1, ready = true, checkedAt = now }));
            check(!service.WorkerReady(), "An old worker without gateway reload support was accepted.");
            await File.WriteAllTextAsync(heartbeat, JsonSerializer.Serialize(new { schemaVersion = 1, ready = true, ingressReloadVersion = 1, checkedAt = now }));
            check(service.WorkerReady(), "The upgraded gateway reload worker was not accepted.");
            await File.WriteAllTextAsync(heartbeat, JsonSerializer.Serialize(new { schemaVersion = 1, ready = true, ingressReloadVersion = 1, checkedAt = now.AddMinutes(-1) }));
            check(!service.WorkerReady(), "A stale worker heartbeat was accepted.");
            var certbot = new CertbotCertificateService(new(options.StateDirectory, Path.Combine(options.StateDirectory, "certificates")));
            using var worker = new DomainOnboardingWorker(store, options, service, cloudflare, dns, preflight, certbot, auth,
                new FakeLifetime(), gate, NullLogger<DomainOnboardingWorker>.Instance);
            await worker.StartAsync(default);
            var recovered = await Wait(store, "Failed");
            check(recovered.RecoveryRequired && recovered.PendingRewrites!.SequenceEqual([naming.LocalHostnames[1]]),
                "Indeterminate additions lost their unresolved journal.");
            check(!dns.Entries.Any(item => item.Domain == naming.LocalHostnames[0]) && dns.Entries.Any(item => item.Domain == "unrelated.example.com"),
                "Recovery did not confine deletion to confirmed-owned rewrites.");
            check(dns.Entries.Any(item => item.Domain == naming.LocalHostnames[1]), "An uncertain rewrite was silently adopted or deleted.");
            check(await File.ReadAllTextAsync(gateway) == job.IngressBefore, "Restart did not restore the journaled prior ingress.");
            dns.Entries.RemoveAll(item => item.Domain == naming.LocalHostnames[1]);
            await service.Recover(job.Id, default);
            recovered = await Wait(store, "Failed");
            check(!recovered.RecoveryRequired && recovered.PendingRewrites!.Length == 0 && recovered.CreatedRewrites.Length == 0,
                "Explicit recovery did not reconcile a manually reviewed, now-absent addition.");
            await worker.StopAsync(default);

            var committed = job with { State = "Running", Certificate = new("lucia-" + job.Id.ToString("N"), now, now.AddDays(80),
                naming.CertificateNames, new string('c', 64), "/not-read/fullchain.pem", "/not-read/key.pem", false),
                CreatedRewrites = [], PendingRewrites = [], IngressBefore = null, IngressPublished = null,
                NextRenewalAt = now.AddHours(12) };
            await store.Update(current => current with { Job = committed });
            var profile = new DomainRuntimeProfile(1, naming.Domain, naming.ServiceUrls.Lucia, naming.ServiceUrls.Authentik,
                naming.ServiceUrls.Spark, auth.PublicOrigin, auth.Authority, job.Id, now);
            var active = Path.Combine(store.Root, "active.json");
            await File.WriteAllTextAsync(active, JsonSerializer.Serialize(profile));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(active, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using var restarted = new DomainOnboardingWorker(store, options, service, cloudflare, dns, preflight, certbot, auth,
                new FakeLifetime(), gate, NullLogger<DomainOnboardingWorker>.Instance);
            await restarted.StartAsync(default);
            var retryable = await Wait(store, "Activating");
            check(retryable.NextRenewalAt == committed.NextRenewalAt && retryable.NextActivationCheckAt is not null,
                "A committed profile lost its activation retry or certificate renewal schedule.");
            check(File.Exists(active), "Committed profile was rolled back as a pre-activation interruption.");
            await restarted.StopAsync(default);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<DomainSetupJob> Wait(DomainOnboardingStore store, string expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var job = (await store.Read(timeout.Token)).Job!;
            if (job.State == expected) return job;
            await Task.Delay(50, timeout.Token);
        }
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("Recovery checks must not contact a network.");
    }
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }
    private sealed class FakeDns : ILocalDnsProvider
    {
        public List<AdGuardRewrite> Entries { get; set; } = [];
        public Task<AdGuardConnectionStatus> GetConnectionAsync(CancellationToken ct = default) =>
            Task.FromResult(new AdGuardConnectionStatus(true, "https://adguard.example.com", "owner", false, "fixture", DateTimeOffset.UtcNow));
        public Task<AdGuardHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new AdGuardHealth(true, true, true));
        public Task<IReadOnlyList<AdGuardRewrite>> ListRewritesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AdGuardRewrite>>(Entries.ToArray());
        public Task AddRewriteAsync(AdGuardRewrite entry, CancellationToken ct = default) =>
            throw new InvalidOperationException("Recovery must not add DNS records.");
        public Task DeleteRewriteAsync(AdGuardRewrite entry, CancellationToken ct = default)
        { Entries.Remove(entry); return Task.CompletedTask; }
    }
}
