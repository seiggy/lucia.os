using System.Text.Json;
using System.Text.Json.Nodes;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Host;
using Microsoft.Extensions.Logging.Abstractions;

internal static class OperationsChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "lucia-domain-operations-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new DomainOnboardingOptions { StateDirectory = Path.Combine(root, "state"), GatewayDirectory = Path.Combine(root, "gateway") };
            using var store = new DomainOnboardingStore(options);
            var naming = DomainNames.Plan(new("example.com", "lab", "atlas"), "example.com");
            var now = DateTimeOffset.UtcNow;
            var plan = new DomainSetupPlan(Guid.NewGuid(), "", now, now.AddMinutes(30), new string('a', 32), new string('b', 32),
                naming, "192.168.0.222", "owner@example.com", 60, "https://letsencrypt.org/documents/example.pdf",
                "https://adguard.example.com", "owner", [], [], []);
            plan = plan with { ReviewHash = DomainOnboardingStore.Hash(plan) };
            var certificates = Path.Combine(store.Root, "certificates");
            Directory.CreateDirectory(certificates);
            var cert = Path.Combine(certificates, "fixture.pem");
            var key = Path.Combine(certificates, "fixture.key");
            await File.WriteAllTextAsync(cert, "Synthetic file for configuration inspection only.");
            await File.WriteAllTextAsync(key, "Synthetic file, not a private key.");
            var job = new DomainSetupJob(Guid.NewGuid(), "Active", "Active", "Synthetic active configuration", "owner", now, now, plan,
                [], [naming.LocalHostnames[0]], new("lucia-" + Guid.NewGuid().ToString("N"), now, now.AddDays(60), naming.CertificateNames,
                    new string('c', 64), cert, key, true), NextRenewalAt: now.AddHours(12));
            await store.Update(current => current with { Job = job });
            var profile = new DomainRuntimeProfile(1, naming.Domain, naming.ServiceUrls.Lucia, naming.ServiceUrls.Authentik,
                naming.ServiceUrls.Spark, "https://legacy.invalid", "https://legacy.invalid:9443/application/o/lucia/", job.Id, now);
            await DomainOnboardingStore.WriteJson(Path.Combine(store.Root, "active.json"), profile);
            DomainIngressConfiguration.Publish(options.GatewayDirectory, naming, certificates, cert, key);
            var provider = new ReadOnlyDns { Entries = naming.LocalHostnames.Select(name => new AdGuardRewrite(name, plan.IngressAddress))
                .Append(new("unrelated.example.com", "192.168.0.99")).ToArray() };
            var service = new DomainOperationsService(store, options, provider, NullLogger<DomainOperationsService>.Instance);
            var before = await File.ReadAllTextAsync(Path.Combine(store.Root, "workflow.json"));
            var snapshot = await service.Read(default);
            check(snapshot.Routes.Length == 3 && snapshot.Routes.All(r => r.Configuration == "Published"), "Published routes were not identified.");
            check(snapshot.Routes.Single(r => r.Kind == "Redirect").Target == naming.ServiceUrls.Lucia + "/", "Spark redirect was not distinguished from a proxy.");
            check(snapshot.DnsRecords.All(r => r.State == "Matches") && snapshot.DnsRecords.Sum(r => r.Records.Length) == 3,
                "Managed records were missing or unrelated AdGuard records were exposed.");
            check(snapshot.DnsRecords[0].Ownership == "Created by Lucia during setup" && snapshot.DnsRecords[1].Ownership == "Pre-existing record",
                "Existing DNS rules were falsely adopted.");
            check(provider.Reads == 2 && before == await File.ReadAllTextAsync(Path.Combine(store.Root, "workflow.json")),
                "Reading the operations view changed workflow or invoked unexpected DNS operations.");
            var path = Path.Combine(options.GatewayDirectory, DomainIngressConfiguration.FileName);
            var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            document["http"]!["services"]!["domain-lucia"]!["loadBalancer"]!["servers"]![0]!["url"] = "http://different-service:8080";
            await File.WriteAllTextAsync(path, document.ToJsonString());
            snapshot = await service.Read(default);
            check(snapshot.Routes[0] is { Configuration: "Changed", Target: "http://different-service:8080" },
                "Changed gateway destination was shown as the original configuration.");
            document["http"]!["routers"]!.AsObject().Remove("domain-authentik");
            await File.WriteAllTextAsync(path, document.ToJsonString());
            check((await service.Read(default)).Routes[1].Configuration == "Missing", "Missing route was reported as published.");
            await File.WriteAllTextAsync(path, "invalid JSON");
            provider.Fail = true;
            snapshot = await service.Read(default);
            check(snapshot.GatewayError is not null && snapshot.DnsError is not null
                && snapshot.Routes.All(r => r.Configuration == "Unavailable") && snapshot.DnsRecords.All(r => r.State == "Unavailable"),
                "Unavailable sources were presented as empty or healthy.");
            check(!JsonSerializer.Serialize(snapshot).Contains("PRIVATE_PROVIDER_DETAIL"), "A raw provider error escaped.");
            provider.Fail = false;
            var duplicate = provider.Entries.Append(provider.Entries[0]).ToArray();
            check(DomainOperationsService.Records(job, duplicate, provider.Health)[0].State == "Conflict", "Duplicate DNS rules were not flagged.");
            check(DomainOperationsService.Records(job, [], provider.Health).All(r => r.State == "Missing"), "Absent records were not reported.");
            check(DomainOperationsService.Records(job, provider.Entries, new(true, true, false)).All(r => r.State == "Disabled"),
                "Globally disabled DNS rewrites were shown as healthy.");
            check(DomainOperationsService.Records(job, [new("*.lab.example.com", "192.168.0.99")], provider.Health).All(r => r.State == "Conflict"),
                "Overlapping wildcard drift was hidden.");
            check(DomainOperationsService.Records(job, [new(naming.LocalHostnames[0], plan.IngressAddress) { Enabled = false }], provider.Health)[0].State == "Disabled",
                "Disabled exact record was not identified.");

            var app = new Lucia.Homelab.Server.Stacks.ActiveRoute("musicbrainz", new("musicbrainz", 5000), "192.168.1.172");
            var appJson = JsonNode.Parse(Lucia.Homelab.Server.Stacks.AppGateway.Build([app], naming.Namespace)!)!;
            var appRoute = DomainOperationsService.AppRoutes([app], naming.Namespace, appJson);
            appJson["http"]!["services"]!["app-musicbrainz-musicbrainz"]!["loadBalancer"]!["servers"]![0]!["url"] = "http://192.168.1.9:5000";
            check(appRoute is [{ Name: "MusicBrainz", Configuration: "Published", Target: "http://192.168.1.172:5000" }]
                && appRoute[0].Origin == $"https://musicbrainz.{naming.Namespace}"
                && DomainOperationsService.AppRoutes([app], naming.Namespace, appJson)[0].Configuration == "Changed"
                && DomainOperationsService.AppRoutes([app], naming.Namespace, new JsonObject())[0].Configuration == "Missing",
                "App routes weren't reported against the published gateway configuration.");
            var wanted = Lucia.Homelab.Server.Nodes.ManagedNodeDns.Wanted(naming,
                [new("node1", "192.168.1.172"), new("atlas", "192.168.1.5"), new("public", "8.8.8.8"), new("taken", "192.168.1.9")]);
            check(wanted.Length == 2 && wanted[0] == new AdGuardRewrite("node1.lab.example.com", "192.168.1.172"),
                "Node DNS accepted a public address or a Lucia service name.");
            var routed = Lucia.Homelab.Server.Nodes.ManagedNodeDns.Wanted(naming, [new("node1", "192.168.1.172")], plan.IngressAddress,
                [new Lucia.Homelab.Server.Stacks.ActiveRoute("obs", new("grafana", 3030), "192.168.1.172")]);
            check(routed.Contains(new AdGuardRewrite("grafana.lab.example.com", plan.IngressAddress)) && routed.Length == 2,
                "An app's web address must point at Lucia's gateway, not at the app's server.");
            var state = Path.Combine(root, "nodes", "dns-records.json");
            var dns = new WritableDns { Entries = [new("taken.lab.example.com", "192.168.1.50")] };
            await Lucia.Homelab.Server.Nodes.ManagedNodeDns.Apply(dns, state, wanted, default);
            check(dns.Entries.Contains(wanted[0]) && dns.Entries.Contains(new("taken.lab.example.com", "192.168.1.50")) && dns.Entries.Length == 2,
                "Node record was not published, or a record Lucia did not create was replaced.");
            await Lucia.Homelab.Server.Nodes.ManagedNodeDns.Apply(dns, state, [wanted[0] with { Answer = "192.168.1.173" }], default);
            check(dns.Entries.Contains(new("node1.lab.example.com", "192.168.1.173")) && !dns.Entries.Contains(wanted[0]),
                "A changed node address did not replace Lucia's own record.");
            await Lucia.Homelab.Server.Nodes.ManagedNodeDns.Apply(dns, state, [], default);
            check(dns.Entries is [{ Domain: "taken.lab.example.com" }], "A removed node's record was kept, or another record was deleted.");
            var primary = new AdGuardRewrite("taken.lab.example.com", "192.168.1.231");
            await Lucia.Homelab.Server.Nodes.ManagedNodeDns.Apply(dns, state, [primary], default, new HashSet<string> { primary.Domain });
            check(dns.Entries is [var moved] && moved == primary, "AdGuard's name didn't follow the primary over a record made by hand.");

            var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            Lucia.Homelab.Server.Stacks.AppInstance Instance(string name) => new(name, name, "192.168.1.230", "Ready", at);
            var entries = new Dictionary<string, AdGuardInstanceState>
            {
                ["down"] = new(HealthyAt: at.AddMinutes(-5), SyncedAt: at),
                ["stale"] = new(HealthyAt: at, SyncedAt: at.AddMinutes(-11)),
                ["older"] = new(HealthyAt: at, SyncedAt: at.AddMinutes(-2)),
                ["newer"] = new(HealthyAt: at, SyncedAt: at.AddMinutes(-1)),
                ["new"] = new(Setup: true, HealthyAt: at, SyncedAt: at),
            };
            check(AdGuardFleet.Successor(entries.Keys.Select(Instance), entries, at) == "newer"
                && AdGuardFleet.Successor(new[] { "down", "stale", "new" }.Select(Instance), entries, at) is null,
                "Failover must pick the healthy, set-up instance that synced most recently within ten minutes.");
            check(AdGuardFleet.NextAddress(["192.168.1.230", null], new HashSet<string> { "192.168.1.231" }) == "192.168.1.232"
                && AdGuardFleet.NextAddress(["192.168.1.254"], new HashSet<string>()) is null && AdGuardFleet.NextAddress([], new HashSet<string>()) is null,
                "The next AdGuard address must be the first free one after the highest, within the /24.");
            check(AdGuardFleet.SyncError("""{"level":"info","msg":"Sync done"}""", 0, "pw") is null
                && AdGuardFleet.SyncError("""{"level":"ERROR","msg":"Error syncing","error":": 401 Unauthorized"}""", 0, "pw") == "Error syncing: 401 Unauthorized"
                && AdGuardFleet.SyncError("""{"level":"error","msg":"login pw failed"}""", 0, "pw") == "The sync failed."
                && AdGuardFleet.SyncError("panic", 2, "pw") == "The sync exited with code 2.",
                "Sync errors must come from error-level log lines, without the password.");
            var environment = AdGuardFleet.SyncEnvironment("a.lab", "b.lab", "u", "p");
            check(environment["ORIGIN_URL"] == "https://a.lab" && environment["REPLICA1_URL"] == "https://b.lab" && environment["FEATURES_TLS_CONFIG"] == "false"
                && environment["FEATURES_DHCP_SERVER_CONFIG"] == "false" && environment["REPLICA1_AUTO_SETUP"] == "false",
                "Sync must leave each instance's certificate and DHCP alone.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class WritableDns : ILocalDnsProvider
    {
        internal AdGuardRewrite[] Entries = [];
        public Task<AdGuardConnectionStatus> GetConnectionAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<AdGuardHealth> GetHealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AdGuardHealth(true, true, true));
        public Task<IReadOnlyList<AdGuardRewrite>> ListRewritesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AdGuardRewrite>>(Entries);
        public Task AddRewriteAsync(AdGuardRewrite rewrite, CancellationToken cancellationToken = default)
        {
            if (Entries.Any(entry => entry.Domain == rewrite.Domain)) throw new InvalidOperationException("Name already exists.");
            Entries = [.. Entries, rewrite];
            return Task.CompletedTask;
        }
        public Task DeleteRewriteAsync(AdGuardRewrite rewrite, CancellationToken cancellationToken = default)
        {
            Entries = Entries.Where(entry => entry != rewrite).ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class ReadOnlyDns : ILocalDnsProvider
    {
        internal AdGuardRewrite[] Entries = [];
        internal readonly AdGuardHealth Health = new(true, true, true);
        internal bool Fail;
        internal int Reads;
        public Task<AdGuardConnectionStatus> GetConnectionAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected connection read.");
        public Task<AdGuardHealth> GetHealthAsync(CancellationToken cancellationToken = default)
        { Reads++; if (Fail) throw new IOException("PRIVATE_PROVIDER_DETAIL"); return Task.FromResult(Health); }
        public Task<IReadOnlyList<AdGuardRewrite>> ListRewritesAsync(CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult<IReadOnlyList<AdGuardRewrite>>(Entries); }
        public Task AddRewriteAsync(AdGuardRewrite rewrite, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Overview must not add DNS records.");
        public Task DeleteRewriteAsync(AdGuardRewrite rewrite, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Overview must not delete DNS records.");
    }
}
