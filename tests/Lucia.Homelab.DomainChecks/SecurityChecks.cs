using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Security;
using Lucia.Homelab.Server.Stacks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

/// <summary>
/// The gateway's CrowdSec middlewares, the safe harbor's validation, the CrowdSec catalog app, its certificates from Lucia's
/// authority, the collector link, the page's parsers, and the pins shared with provisioning.
/// </summary>
internal static class SecurityChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var cloudflare = DomainIngressConfiguration.CloudflareRanges;
        foreach (var (input, expected) in new[]
        {
            ("192.168.1.50", "192.168.1.50"), (" 192.168.1.50 ", "192.168.1.50"), ("192.168.1.50/32", "192.168.1.50"),
            ("192.168.1.0/24", "192.168.1.0/24"), ("10.0.0.0/8", "10.0.0.0/8"), ("fd00::/8", "fd00::/8"),
            ("203.0.113.0/24", "203.0.113.0/24"), ("2001:db8::1", "2001:db8::1"), ("2001:db8:1::/48", "2001:db8:1::/48"),
        })
            check(GatewaySecurity.Canonical(input, cloudflare, out var error) == expected && error is null,
                $"The safe harbor must accept {input} as {expected}.");
        foreach (var input in new[]
        {
            "", "not-an-ip", "0.0.0.0/0", "::/0", "0.0.0.0", "::", "8.0.0.0/8", "203.0.113.0/23", "2001:db8::/32", "127.0.0.1",
            "::1", "224.0.0.1", "255.255.255.255", "ff02::1", "192.168.1.5/24", "104.16.0.1", "104.0.0.0/8", "172.64.0.0/13",
            "fe80::1%eth0", "::ffff:192.168.1.1", "10.0.0.0/7", "192.168.1.0/33",
        })
            check(GatewaySecurity.Canonical(input, cloudflare, out var error) is null && !string.IsNullOrEmpty(error),
                $"The safe harbor must refuse {input}: nothing may trust the internet, Cloudflare or loopback.");

        var harbor = GatewaySecurity.Harbor(["172.24.0.0/16", "192.168.1.10"], ["192.168.1.10/32", "192.168.1.50"]);
        check(harbor.SequenceEqual(["127.0.0.0/8", "::1", "172.24.0.0/16", "192.168.1.10", "192.168.1.50"]),
            "The harbor must list loopback, then Lucia's own addresses, then the owner's, each once.");

        // Without an engine both middlewares are empty chains: the entrypoints keep their names and nothing waits on CrowdSec.
        string[] Strings(JsonNode node) => [.. node.AsArray().Select(item => item!.GetValue<string>())];
        var idle = JsonNode.Parse(GatewaySecurity.Render(harbor, cloudflare))!["http"]!["middlewares"]!;
        check(idle.AsObject().Count == 2 && new[] { GatewaySecurity.Middleware, GatewaySecurity.AppSecMiddleware }.All(name =>
                idle[name]!.AsObject().Count == 1 && idle[name]!["chain"]!["middlewares"]!.AsArray().Count == 0),
            "Without a healthy engine the gateway's CrowdSec middlewares must be empty chains.");

        var link = new CrowdSecLink("192.168.1.20", 8088, 7422, "bouncer-key-0123456789abcdef0123456789abcdef", "-----BEGIN CERTIFICATE-----\nAA==\n-----END CERTIFICATE-----\n");
        var rendered = JsonNode.Parse(GatewaySecurity.Render(harbor, cloudflare, link))!["http"]!["middlewares"]!;
        var decisions = rendered[GatewaySecurity.Middleware]!["plugin"]![GatewaySecurity.PluginName]!;
        var appsec = rendered[GatewaySecurity.AppSecMiddleware]!["plugin"]![GatewaySecurity.PluginName]!;
        check(rendered.AsObject().Count == 2 && decisions["crowdsecMode"]!.GetValue<string>() == "stream"
            && decisions["updateMaxFailure"]!.GetValue<int>() == -1 && !decisions["streamStartupBlock"]!.GetValue<bool>()
            && decisions["crowdsecAppsecEnabled"] is null,
            "The ban check must use the decision stream and keep serving when the engine is unreachable.");
        check(appsec["crowdsecMode"]!.GetValue<string>() == "appsec" && appsec["crowdsecAppsecEnabled"]!.GetValue<bool>()
            && !appsec["crowdsecAppsecFailureBlock"]!.GetValue<bool>() && !appsec["crowdsecAppsecUnreachableBlock"]!.GetValue<bool>()
            && !appsec["crowdsecAppsecUnreadableBodyBlock"]!.GetValue<bool>()
            && appsec["crowdsecAppsecScheme"]!.GetValue<string>() == "https" && appsec["crowdsecAppsecHost"]!.GetValue<string>() == "192.168.1.20:7422"
            && appsec["crowdsecAppsecTLSCertificateAuthority"]!.GetValue<string>() == link.CaPem,
            "AppSec must go over TLS to the engine's server and let requests through when the engine fails, can't be reached, or can't read a body.");
        foreach (var middleware in new[] { decisions, appsec })
            check(Strings(middleware["clientTrustedIps"]!).SequenceEqual(harbor)
                && Strings(middleware["forwardedHeadersTrustedIps"]!).SequenceEqual(cloudflare)
                && middleware["crowdsecLapiScheme"]!.GetValue<string>() == "https"
                && middleware["crowdsecLapiHost"]!.GetValue<string>() == "192.168.1.20:8088"
                && middleware["crowdsecLapiKey"]!.GetValue<string>() == link.Key
                && middleware["crowdsecLapiTLSCertificateAuthority"]!.GetValue<string>() == link.CaPem
                && middleware["crowdsecLapiTLSInsecureVerify"] is null && middleware["httpTimeoutSeconds"]!.GetValue<int>() == 2,
                "Both middlewares skip the harbor, read visitors only behind Cloudflare, and reach the engine over TLS that trusts only Lucia's authority, within two seconds.");
        check(new CrowdSecLink("fd00::20", 8088, 7422, "k", "c").Authority(8088) == "[fd00::20]:8088" && !link.ToString().Contains(link.Key, StringComparison.Ordinal),
            "IPv6 engine addresses are bracketed and the bouncer key never shows in logs.");

        var folder = Path.Combine(Path.GetTempPath(), "lucia-security-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var security = Path.Combine(folder, "security");
            Directory.CreateDirectory(security);
            File.WriteAllText(Path.Combine(security, "safe-harbor.txt"), "127.0.0.0/8\n");
            check(GatewaySecurity.Publish(security, harbor, cloudflare) && !GatewaySecurity.Publish(security, harbor, cloudflare)
                && File.ReadAllText(Path.Combine(security, GatewaySecurity.FileName)) == GatewaySecurity.Render(harbor, cloudflare)
                && Directory.GetFiles(security).Length == 1,
                "Publishing writes the middlewares once, leaves an unchanged file alone, and removes the engine's old harbor file.");
            check(GatewaySecurity.Publish(security, harbor, cloudflare, link) && !GatewaySecurity.Publish(security, harbor, cloudflare, link)
                && GatewaySecurity.Publish(security, harbor, cloudflare),
                "Switching between the engine and empty chains rewrites the middlewares each way.");
            var owner = Path.Combine(folder, GatewaySecurity.StateFileName);
            check(GatewaySecurity.ReadOwner(owner).Length == 0, "No saved list means no owner entries.");
            File.WriteAllText(owner, """{"schemaVersion":1,"addresses":["192.168.1.50"],"updatedAt":"2026-10-26T00:00:00Z","updatedBy":"owner"}""");
            check(GatewaySecurity.ReadOwner(owner).SequenceEqual(["192.168.1.50"]), "The saved list must read back.");
            foreach (var bad in new[] { """{"schemaVersion":2,"addresses":[]}""", """{"addresses":[]}""", """{"schemaVersion":1,"addresses":[1]}""",
                         """{"schemaVersion":1,"addresses":[""" + string.Join(",", Enumerable.Range(1, 33).Select(i => $"\"10.0.0.{i}\"")) + "]}" })
            {
                File.WriteAllText(owner, bad);
                try
                {
                    GatewaySecurity.ReadOwner(owner);
                    check(false, "An invalid saved safe-harbor list was accepted: " + bad);
                }
                catch (InvalidDataException) { check(true, ""); }
            }
            var tree = Path.Combine(folder, "tree");
            Directory.CreateDirectory(Path.Combine(tree, "b"));
            File.WriteAllText(Path.Combine(tree, "b", "c.go"), "package b\n");
            File.WriteAllText(Path.Combine(tree, "a.txt"), "a");
            static string Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            check(GatewaySecurity.TreeDigest(tree) == Hex($"{Hex("a")}  a.txt\n{Hex("package b\n")}  b/c.go\n"),
                "The plugin tree digest must match provision.py's tree_digest listing.");

            await CertificateChecks(check, folder);
        }
        finally { Directory.Delete(folder, recursive: true); }

        check(SafeHarbor.Validate(["192.168.1.50", "192.168.1.50/32", " 192.168.2.0/24"]).SequenceEqual(["192.168.1.50", "192.168.2.0/24"]),
            "Saving the list must store each entry once, in canonical form.");
        foreach (var bad in new string[]?[] { null, [.. Enumerable.Range(1, 33).Select(i => $"10.0.0.{i}")], ["192.168.1.50", "0.0.0.0/0"] })
        {
            try
            {
                SafeHarbor.Validate(bad);
                check(false, "An invalid safe-harbor request was accepted.");
            }
            catch (HardwareOnboardingException error) when (error.StatusCode == 400) { check(true, ""); }
        }

        ActiveRoute[] routes = [new("immich", new("photos", 2283, null, "images"), "192.168.1.20"), new("obs", new("otlp", 4318, 4317), "192.168.1.9")];
        var apps = JsonNode.Parse(AppGateway.Build(routes, "homelab.example.com", "example.com", appSec: true)!)!["http"]!;
        var plain = JsonNode.Parse(AppGateway.Build(routes, "homelab.example.com", "example.com")!)!["http"]!;
        check(new[] { "app-immich-photos", "app-obs-otlp", "app-obs-otlp-grpc" }.All(id =>
                Strings(apps["routers"]![id]!["middlewares"]!).SequenceEqual(["crowdsec-appsec@file"]))
            && Strings(apps["routers"]!["app-immich-photos-public"]!["middlewares"]!).SequenceEqual(["app-immich-photos-public-forwarded"])
            && plain["routers"]!["app-immich-photos"]!["middlewares"] is null && plain["routers"]!["app-obs-otlp-grpc"]!["middlewares"] is null,
            "App routes on the LAN pass AppSec; public routers rely on the public entrypoint's, and nothing changes without CrowdSec.");
        check(apps["middlewares"]?[GatewaySecurity.Middleware] is null && apps["middlewares"]?[GatewaySecurity.AppSecMiddleware] is null,
            "Only crowdsec.yml may define the CrowdSec middlewares.");

        CatalogChecks(check, cloudflare);
        ReportChecks(check);

        var root = Root();
        var provision = File.ReadAllText(Path.Combine(root, "tools", "identity", "provision.py"));
        check(provision.Contains($"PLUGIN_TREE_SHA256 = \"{GatewaySecurity.PluginTreeSha256}\"", StringComparison.Ordinal)
            && provision.Contains($"PLUGIN_VERSION = \"{GatewaySecurity.PluginVersion}\"", StringComparison.Ordinal)
            && provision.Contains($"PLUGIN_MODULE = \"{GatewaySecurity.PluginModule}\"", StringComparison.Ordinal),
            "provision.py and GatewaySecurity.cs must pin the same plugin release and tree.");
        check(!provision.Contains("crowdsec-bouncer-key", StringComparison.Ordinal) && !provision.Contains("lucia-identity-crowdsec", StringComparison.Ordinal)
            && !Directory.Exists(Path.Combine(root, "deployment", "identity", "crowdsec")),
            "The Spark no longer runs a CrowdSec engine: provisioning makes no engine key, files or container.");
        var worker = File.ReadAllText(Path.Combine(root, "tools", "nodes", "enrollment_worker.py"));
        check(worker.Contains($"APP_TEMPLATES = frozenset({{\"{CrowdSecApp.AppId}\"}})", StringComparison.Ordinal) && !worker.Contains(".invalid", StringComparison.Ordinal)
            && worker.Contains("names = manifest.get(\"names\")", StringComparison.Ordinal) && worker.Contains("\"-verify_hostname\", name", StringComparison.Ordinal)
            && worker.Contains($"\"{AppCertificates.Requests}\"", StringComparison.Ordinal) && worker.Contains($"\"{AppCertificates.Responses}\"", StringComparison.Ordinal)
            && worker.Contains("\"lucia-app-\" + value[\"stack\"]", StringComparison.Ordinal) && AppCertificates.CommonName("crowdsec") == "lucia-app-crowdsec",
            "The Spark's certificate worker signs the CrowdSec app's requests from the queues the host writes, for the stack's own name under the active domain.");

        var relay = Lucia.Homelab.Server.Telemetry.TelemetryRelay.Config("\"lucialab01\"", false, [new("crowdsec", "127.0.0.1:19060", Authenticated: false)], "debug: {}");
        var job = relay[relay.IndexOf("job_name: crowdsec", StringComparison.Ordinal)..];
        check(job.Contains("127.0.0.1:19060", StringComparison.Ordinal) && !job[..job.IndexOf("relabel_configs", StringComparison.Ordinal)].Contains("authorization", StringComparison.Ordinal),
            "A server's relay scrapes CrowdSec's metrics on loopback without an app key.");
        var spark = Path.Combine(Path.GetTempPath(), "lucia-relay-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            _ = new Lucia.Homelab.Server.Telemetry.ControllerRelay(spark, "https://lucia.example.com");
            check(!File.ReadAllText(Path.Combine(spark, "relay.yaml")).Contains("crowdsec", StringComparison.Ordinal),
                "The Spark's own relay no longer scrapes a CrowdSec engine.");
        }
        finally { Directory.Delete(spark, recursive: true); }
    }

    private static void CatalogChecks(Action<bool, string> check, string[] cloudflare)
    {
        var app = StackCatalog.Find(CrowdSecApp.AppId);
        var settings = StackCatalog.Settings(app, null);
        var node = new Lucia.Homelab.Server.Nodes.ManagedNodeFacts(Guid.NewGuid(), "lucialab01", true, null);
        var output = app.Render(settings, node, new Dictionary<string, string>());
        var env = StackCatalog.ReadEnv(output.Env);
        var compose = output.Compose;
        check(app.RequiresApps.SequenceEqual([ObservabilityApp.AppId]) && app.Single && app.BackupMode == "stop"
            && app.BackupExclude.SequenceEqual(["volumes/state"]) && !app.ServerBound && !app.UsesAddress,
            "CrowdSec needs Observability, runs once, and is backed up stopped (its SQLite database) without its in-memory state.");
        var naming = Lucia.Homelab.Server.Domains.DomainNames.Plan(new("example.com", "lab", "spark"), "example.com");
        var refusal = "";
        try { StackStore.RequireDomain(app, null, required: true); }
        catch (Lucia.Homelab.Server.Onboarding.HardwareOnboardingException error) when (error.Code == "domain_required") { refusal = error.Message; }
        StackStore.RequireDomain(app, naming, required: true);
        StackStore.RequireDomain(app, null, required: false);
        check(refusal.Contains("Domains page", StringComparison.Ordinal) && CrowdSecApp.FullName(settings, naming.Namespace) == "crowdsec.lab.example.com"
            && CrowdSecApp.FullName(StackCatalog.Settings(app, new() { ["crowdsec-host"] = "Guard" }), naming.Namespace) == "guard.lab.example.com",
            "CrowdSec installs only under an active domain, as crowdsec-host under its namespace; an installed one can still be saved without one.");
        var badName = false;
        try { app.Render(StackCatalog.Settings(app, new() { ["crowdsec-host"] = "bad_name" }), new(Guid.NewGuid(), "lucialab01", true, null), new Dictionary<string, string>()); }
        catch (Lucia.Homelab.Server.Onboarding.HardwareOnboardingException error) when (error.Code == "invalid_setting") { badName = true; }
        check(badName, "CrowdSec's name must be one DNS label.");
        check(compose.Contains($"    image: {CrowdSecApp.Image}\n", StringComparison.Ordinal)
            && CrowdSecApp.Image.EndsWith("@sha256:0f2523fa61ef507f15d953045cface490cc880670c62f2755ced17524107f71a", StringComparison.Ordinal)
            && compose.Contains($"    image: {ObservabilityApp.Alpine}\n", StringComparison.Ordinal),
            "The engine and its sidecar are pinned by digest.");
        check(compose.Contains("      - \"8088:8080\"\n", StringComparison.Ordinal) && compose.Contains("      - \"7422:7422\"\n", StringComparison.Ordinal)
            && compose.Contains("      - \"7423:7423\"\n", StringComparison.Ordinal) && compose.Contains("      - \"127.0.0.1:19060:6060\"\n", StringComparison.Ordinal)
            && compose.Split("ports:").Length == 2,
            "Only the local API, AppSec and the log intake face the LAN; metrics stay on loopback.");
        var engine = compose[compose.IndexOf("  crowdsec:\n", StringComparison.Ordinal)..compose.IndexOf("\nconfigs:\n", StringComparison.Ordinal)];
        var sidecar = compose[compose.IndexOf("  lucia:\n", StringComparison.Ordinal)..compose.IndexOf("  crowdsec:\n", StringComparison.Ordinal)];
        foreach (var (service, label) in new[] { (engine, "engine"), (sidecar, "sidecar") })
            check((service == engine || service.Contains("    read_only: true\n", StringComparison.Ordinal)) && service.Contains("    cap_drop: [ALL]\n", StringComparison.Ordinal)
                && service.Contains("    security_opt: [\"no-new-privileges:true\"]\n", StringComparison.Ordinal)
                && service.Contains("    pids_limit: ", StringComparison.Ordinal) && service.Contains("    mem_limit: ", StringComparison.Ordinal),
                $"CrowdSec's {label} runs locked down: no capabilities, no privilege gain, bounded processes and memory (and the sidecar read-only).");
        check(sidecar.Contains("    network_mode: none\n", StringComparison.Ordinal) && !sidecar.Contains("GATEWAY_KEY", StringComparison.Ordinal)
            && !sidecar.Contains("configs:", StringComparison.Ordinal) && sidecar.Contains("        put tls.sha256 ", StringComparison.Ordinal)
            && sidecar.Contains("TLS_CERT: ${LUCIA_CROWDSEC_TLS_CERT:-}", StringComparison.Ordinal)
            && !engine.Replace("${LUCIA_CROWDSEC_NAME:-localhost}", "", StringComparison.Ordinal).Contains("LUCIA_CROWDSEC_", StringComparison.Ordinal)
            && engine.Contains("      - state:/lucia/state:ro\n", StringComparison.Ordinal)
            && compose.Contains("  state:\n    driver_opts:\n      type: tmpfs\n", StringComparison.Ordinal),
            "Lucia's certificate, harbor and enroll key reach only the offline sidecar, so a change never restarts the engine; the engine reads them from memory.");
        check(env.Keys.Order().SequenceEqual([CrowdSecApp.GatewayKey, CrowdSecApp.IntakeToken, CrowdSecApp.MachinePassword])
            && env.Values.All(value => value.Length >= 32)
            && app.Render(settings, node, env).Env == output.Env && !compose.Contains(env[CrowdSecApp.GatewayKey], StringComparison.Ordinal),
            "The bouncer key, the machine password and the intake token are generated once, kept on re-render, and never written into the compose.");
        check(engine.Contains("      AGENT_USERNAME: lucia-host\n", StringComparison.Ordinal) && engine.Contains("      AGENT_PASSWORD: ${LUCIA_MACHINE_PASSWORD}\n", StringComparison.Ordinal)
            && engine.Contains("      LOCAL_API_URL: https://${LUCIA_CROWDSEC_NAME:-localhost}:8080\n", StringComparison.Ordinal)
            && engine.Contains("      - \"${LUCIA_CROWDSEC_NAME:-localhost}:127.0.0.1\"\n", StringComparison.Ordinal) && !compose.Contains(".invalid", StringComparison.Ordinal)
            && output.Names!.SequenceEqual(["crowdsec"]) && app.Fields.Any(field => field.Id == "crowdsec-host" && field.Default == "crowdsec")
            && engine.Contains("      CACERT_FILE: /lucia/state/ca.crt\n", StringComparison.Ordinal)
            && engine.Contains("      DISABLE_PARSERS: crowdsecurity/whitelists\n", StringComparison.Ordinal)
            && engine.Contains("crowdsecurity/traefik ", StringComparison.Ordinal) && engine.Contains("crowdsecurity/whitelist-good-actors", StringComparison.Ordinal),
            "The engine registers Lucia's machine, reaches its own local API on loopback under its name under the domain with verified TLS, records LAN alerts and installs the gateway's collections.");
        check(CrowdSecApp.Settings.Contains("      cert_file: /lucia/state/tls.crt", StringComparison.Ordinal)
            && !CrowdSecApp.Settings.Contains("insecure_skip_verify", StringComparison.Ordinal) && CrowdSecApp.Settings.Contains("      ca_cert_path: \"\"", StringComparison.Ordinal)
            && CrowdSecApp.Settings.Contains("    max_age: 30d", StringComparison.Ordinal)
            && CrowdSecApp.Acquisition.Contains("  X-Lucia-Intake: ${LUCIA_INTAKE_TOKEN}", StringComparison.Ordinal)
            && CrowdSecApp.Acquisition.Contains("  server_cert: /lucia/state/tls.crt", StringComparison.Ordinal)
            && CrowdSecApp.Acquisition.Contains("cert_file: /lucia/state/tls.crt", StringComparison.Ordinal)
            && compose.Contains("$${LUCIA_INTAKE_TOKEN}", StringComparison.Ordinal),
            "The local API, AppSec and the log intake all serve Lucia's certificate, and the intake checks its token.");
        check(CrowdSecApp.Acquisition.Contains("  evt.Line.Labels.type != \"traefik\" ? nil : map(", StringComparison.Ordinal)
            && CrowdSecApp.Acquisition.Contains("unique_id: lucia-appsec", StringComparison.Ordinal),
            "The access-log split never runs on AppSec's events (CrowdSec 1.8.1 applies the http transform to them too), so AppSec blocks stay alerts.");
        using (var waf = System.Text.Json.JsonDocument.Parse("""
            [{"id":5,"kind":"waf","scenario":"crowdsecurity/vpatch-env-access","source":{"scope":"Ip","value":"203.0.113.77","cn":"NL"},
              "created_at":"2026-10-26T20:30:20Z","events_count":1,"decisions":null,"simulated":false,
              "events":[{"meta":[{"key":"rule_name","value":"crowdsecurity/vpatch-env-access"},{"key":"uri","value":"/.env"}]}],
              "meta":[{"key":"target_uri","value":"[\"/.env\"]"}]},
             {"id":6,"kind":"waf","scenario":"crowdsecurity/vpatch-git-config","source":{"scope":"Ip","value":"192.168.0.184"},
              "events_count":1,"meta":[{"key":"target_uri","value":"[\"/.git/config\"]"}]},
             {"id":7,"kind":"crowdsec","scenario":"crowdsecurity/appsec-vpatch","source":{"scope":"Ip","value":"203.0.113.77"},
              "events_count":2,"decisions":[{"id":1}]},
             {"id":8,"scenario":"crowdsecurity/http-probing","source":{"scope":"Ip","value":"203.0.113.50"}}]
            """))
        {
            var parsed = CrowdSecLapi.ParseAlerts(waf.RootElement);
            check(parsed.Length == 4 && parsed[0].Kind == "waf" && parsed[0].Path == "/.env" && parsed[0].Country == "NL"
                && parsed[1].Path == "/.git/config" && CrowdSecLapi.IsAppSec(parsed[1]) && parsed[1].Decisions == 0
                && parsed[2].Kind == "crowdsec" && parsed[2].Path is null && !CrowdSecLapi.IsAppSec(parsed[2]) && parsed[2].Decisions == 1
                && parsed[3].Kind == "crowdsec" && !CrowdSecLapi.IsAppSec(parsed[3]),
                "AppSec blocks come back as waf alerts with the rule and the blocked path; scenario alerts don't.");
        }
        check(CrowdSecApp.Start.Contains("kill -HUP \"$main\"", StringComparison.Ordinal) && CrowdSecApp.Start.Contains("lucia-status", StringComparison.Ordinal)
            && CrowdSecApp.Start.Contains("output=${output//\"$key\"/<key>}", StringComparison.Ordinal)
            && CrowdSecApp.Start.Contains("unset LUCIA_GATEWAY_KEY", StringComparison.Ordinal),
            "The engine reloads for a renewed certificate, reports its status, never logs the enroll key and keeps the bouncer key out of its children.");
        var profiles = CrowdSecApp.Profiles(cloudflare);
        check(cloudflare.All(range => profiles.Contains($"'{range}'", StringComparison.Ordinal))
            && profiles.IndexOf("lucia_cloudflare_never_banned", StringComparison.Ordinal) < profiles.IndexOf("lucia_private_alert_only", StringComparison.Ordinal)
            && profiles.IndexOf("lucia_private_alert_only", StringComparison.Ordinal) < profiles.IndexOf("lucia_public_escalating_ban", StringComparison.Ordinal)
            && compose.Contains("'192.168.0.0/16'", StringComparison.Ordinal),
            "CrowdSec never bans Cloudflare and only records private addresses.");
        check(profiles.Contains("'720h') < 1 ? '4h'", StringComparison.Ordinal) && profiles.Contains("'24h' : '168h'", StringComparison.Ordinal),
            "Public addresses are banned for 4 hours, then a day, then a week.");
        try
        {
            app.Render(StackCatalog.Settings(app, new() { ["appsec-port"] = "8088" }), node, env);
            check(false, "CrowdSec accepted two listeners on one port.");
        }
        catch (HardwareOnboardingException error) when (error.StatusCode == 400) { check(true, ""); }
        StoredStack Stored(string name, string template, int? version = null) => new(name, "", "",
            new StackManifest(1, new(), new(template, version ?? StackCatalog.Find(template).Version)), "Running", 1, 0, 0,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "owner", "lucialab01");
        string? Refusal(params StoredStack[] stacks)
        {
            try { StackStore.RequireCompanions(app, stacks); return null; }
            catch (HardwareOnboardingException error) when (error.StatusCode == 409) { return error.Code + ": " + error.Message; }
        }
        check(Refusal() == "requires_app: Install Observability first. CrowdSec reads the gateway's access log through Observability's collector."
            && Refusal(Stored("media", "plex")) is { } missing && missing.StartsWith("requires_app", StringComparison.Ordinal)
            && Refusal(Stored("observability", ObservabilityApp.AppId)) is null
            && Refusal(Stored("observability", ObservabilityApp.AppId), Stored("guard", CrowdSecApp.AppId)) == "already_installed: Lucia runs one CrowdSec, and it's already installed as guard."
            && StackCatalog.Apps.Where(item => item.Id != CrowdSecApp.AppId).All(item => item.RequiresApps.Length == 0 && !item.Single),
            "Installing CrowdSec needs Observability first, and Lucia runs only one CrowdSec.");
        check(Refusal(Stored("observability", ObservabilityApp.AppId, 11)) is { } old && old.StartsWith("requires_update: Update Observability first", StringComparison.Ordinal)
            && old.Contains("version 12 or later", StringComparison.Ordinal) && old.Contains("runs version 11", StringComparison.Ordinal)
            && Refusal(Stored("observability", ObservabilityApp.AppId, 11), Stored("metrics", ObservabilityApp.AppId, 12)) is null
            && app.RequiresVersions[ObservabilityApp.AppId] == ObservabilityApp.ForwardsToCrowdSec && ObservabilityApp.ForwardsToCrowdSec <= StackCatalog.Find(ObservabilityApp.AppId).Version,
            "An Observability too old to forward the gateway's access log must be updated before CrowdSec installs.");
        var secretEnv = "GATEWAY_KEY=gk-0123456789abcdef0123456789abcdef\nLUCIA_MACHINE_PASSWORD=mp-0123456789abcdef0123456789abcdef\n"
            + "INTAKE_TOKEN=it-0123456789abcdef0123456789abcdef\nLUCIA_CROWDSEC_TLS_KEY=a2V5\nLUCIA_CROWDSEC_TLS_CERT=Y2VydA==\n"
            + "LUCIA_CROWDSEC_ENROLL_KEY=cmh5r7ygd0001jw08wdqjt4sq\nLUCIA_CROWDSEC_HARBOR=127.0.0.0/8\nMY_SETTING=mine\n";
        var guard = Stored("guard", CrowdSecApp.AppId);
        var managed = StackStore.ManagedSecrets(guard, secretEnv);
        var shown = StackStore.Mask(secretEnv, managed);
        var converted = Stored("guard", CrowdSecApp.AppId) with { Compose = $"services:\n  crowdsec:\n    image: {CrowdSecApp.Image}\n", Manifest = new StackManifest(1, new()) };
        var custom = Stored("tool", "plex") with { Manifest = new StackManifest(1, new()) };
        check(managed.SetEquals(["GATEWAY_KEY", "LUCIA_MACHINE_PASSWORD", "INTAKE_TOKEN", "LUCIA_CROWDSEC_TLS_KEY", "LUCIA_CROWDSEC_ENROLL_KEY"])
            && !shown.Contains("gk-0", StringComparison.Ordinal) && !shown.Contains("mp-0", StringComparison.Ordinal) && !shown.Contains("it-0", StringComparison.Ordinal)
            && !shown.Contains("a2V5", StringComparison.Ordinal) && !shown.Contains("cmh5r7ygd", StringComparison.Ordinal)
            && shown.Contains("GATEWAY_KEY=" + StackStore.KeptByLucia + "\n", StringComparison.Ordinal) && shown.Contains("MY_SETTING=mine\n", StringComparison.Ordinal)
            && shown.Contains("LUCIA_CROWDSEC_TLS_CERT=Y2VydA==\n", StringComparison.Ordinal)
            && StackStore.ManagedSecrets(converted, secretEnv).SetEquals(managed)
            && StackStore.ManagedSecrets(custom, "GATEWAY_KEY=theirs\nLUCIA_CROWDSEC_TOKEN=t\n").SetEquals(["LUCIA_CROWDSEC_TOKEN"]),
            "The stack API shows Lucia's CrowdSec credentials by name only, in the app or a copy converted to custom, and leaves the owner's own values alone.");
        var edited = StackStore.KeepManaged(shown.Replace("MY_SETTING=mine", "MY_SETTING=changed", StringComparison.Ordinal)
            .Replace("GATEWAY_KEY=" + StackStore.KeptByLucia + "\n", "", StringComparison.Ordinal)
            .Replace("INTAKE_TOKEN=" + StackStore.KeptByLucia, "INTAKE_TOKEN=forged", StringComparison.Ordinal), secretEnv, managed);
        var saved = StackCatalog.ReadEnv(edited);
        var before = StackCatalog.ReadEnv(secretEnv);
        check(saved["MY_SETTING"] == "changed" && managed.All(key => saved[key] == before[key]) && !edited.Contains(StackStore.KeptByLucia, StringComparison.Ordinal)
            && saved["LUCIA_CROWDSEC_HARBOR"] == "127.0.0.0/8" && StackStore.KeepManaged("A=1", "", new HashSet<string>()) == "A=1",
            "Saving the app puts Lucia's values back, whether the mask was sent, the line dropped or a new value typed; the owner's edits stand.");
        var start = DateTimeOffset.UnixEpoch;
        var seen = CrowdSec.Track(null, 10, start);
        var quiet = CrowdSec.Track(seen, 10, start + CrowdSec.QuietAfter);
        var moving = CrowdSec.Track(quiet, 11, start + CrowdSec.QuietAfter);
        var restarted = CrowdSec.Track(moving, 0, start + CrowdSec.QuietAfter * 2);
        check(seen == (10, start) && quiet.Since == start && moving == (11, start + CrowdSec.QuietAfter) && restarted == (0, start + CrowdSec.QuietAfter * 2),
            "The page times how long the engine's access-log count has stood still; any change, a restart's reset included, starts it over.");

        var observability = StackCatalog.Find(ObservabilityApp.AppId);
        var collector = observability.Render(StackCatalog.Settings(observability, null), node, new Dictionary<string, string>()).Compose;
        check(observability.Version == 12 && collector.Contains("\"--config=env:LUCIA_CROWDSEC_COLLECTOR\"]", StringComparison.Ordinal)
            && collector.Contains("      LUCIA_CROWDSEC_COLLECTOR: ${LUCIA_CROWDSEC_COLLECTOR:-}\n", StringComparison.Ordinal)
            && collector.Contains("      LUCIA_CROWDSEC_TOKEN: ${LUCIA_CROWDSEC_TOKEN:-}\n", StringComparison.Ordinal)
            && collector.Split("--config=env:LUCIA_CROWDSEC_COLLECTOR").Length == 3,
            "Observability's collector loads (and its health check validates) the CrowdSec forwarding config Lucia sets.");
        var lines = CrowdSecApp.CollectorLines("https://192.168.1.20:7423/v1/logs", "-----BEGIN CERTIFICATE-----\r\nAAAA\r\n-----END CERTIFICATE-----\r\n",
            "token-0123456789abcdef0123456789abcdef");
        var config = lines.Split('\n')[0];
        check(config.StartsWith("LUCIA_CROWDSEC_COLLECTOR='{", StringComparison.Ordinal) && config.EndsWith("}'", StringComparison.Ordinal)
            && config.Count(c => c == '\'') == 2 && !config.Contains('\r') && !config.Contains("token-0123", StringComparison.Ordinal)
            && config.Contains("ca_pem: \"-----BEGIN CERTIFICATE-----\\nAAAA\\n-----END CERTIFICATE-----\\n\"", StringComparison.Ordinal)
            && config.Contains("X-Lucia-Intake: \"${env:LUCIA_CROWDSEC_TOKEN}\"", StringComparison.Ordinal)
            && config.Contains("logs_endpoint: \"https://192.168.1.20:7423/v1/logs\", encoding: json", StringComparison.Ordinal)
            && config.Contains("retry_on_failure: {enabled: false}", StringComparison.Ordinal)
            && config.Contains("resource.attributes[\\\"service.name\\\"] != \\\"lucia-gateway\\\"", StringComparison.Ordinal)
            && config.Contains("receivers: [otlp], processors: [memory_limiter, filter/crowdsec, transform/crowdsec, batch], exporters: [otlp_http/crowdsec]", StringComparison.Ordinal) && config.Contains("set(log.attributes[\\\"time\\\"]", StringComparison.Ordinal)
            && lines.EndsWith("\nLUCIA_CROWDSEC_TOKEN=token-0123456789abcdef0123456789abcdef\n", StringComparison.Ordinal),
            "The collector forwards only the gateway's access log, over TLS trusting Lucia's authority, with the token beside the config and failed sends dropped.");
        check(StackStore.LuciaLines("A=1\nLUCIA_CROWDSEC_HARBOR=127.0.0.0/8\nLUCIA_CROWDSEC_TLS_KEY=x\n") == "LUCIA_CROWDSEC_HARBOR=127.0.0.0/8\nLUCIA_CROWDSEC_TLS_KEY=x\n",
            "Lucia's CrowdSec variables survive the app re-rendering.");
    }

    private static void ReportChecks(Action<bool, string> check)
    {
        var line = """2026-10-26T19:00:00Z lucia-status {"v":1,"at":"2026-10-26T19:00:00Z","version":"v1.8.1-909b5157","bouncers":[{"name":"lucia_gateway_0123456789ab","lastPull":"2026-10-26T18:59:50Z","revoked":false}],"metrics":{"linesRead":120,"linesParsed":118,"linesUnparsed":2,"appsecProcessed":40,"appsecBlocked":3},"capi":{"registered":true,"sharing":true,"blocklist":true},"console":{"authenticated":true,"enrolled":false,"plan":"","registered":true},"enroll":{"state":"failed","message":"the attachment key provided is not valid","at":"2026-10-26T18:58:00Z"},"harbor":5}""";
        var report = CrowdSec.ParseReport("noise\n" + line + "\ntime=\"x\" level=info msg=\"GET /v1/decisions/stream\"\n");
        check(report is { Version: "v1.8.1-909b5157", Bouncer.Name: "lucia_gateway_0123456789ab", Metrics.AppSecBlocked: 3, Metrics.LinesParsed: 118,
                Capi.Registered: true, Capi.Authenticated: true, Capi.Sharing: true, Enrolled: false, EnrollState: "failed", Harbor: 5 }
            && report.EnrollMessage == "the attachment key provided is not valid" && report.Bouncer.LastPull == DateTimeOffset.Parse("2026-10-26T18:59:50Z"),
            "The page reads the engine's latest status line.");
        check(CrowdSec.ParseReport("lucia-status {\"v\":2,\"at\":\"2026-10-26T19:00:00Z\"}\nlucia-status not json\n") is null
            && CrowdSec.ParseReport(line.Replace("\"state\":\"failed\"", "\"state\":\"hacked\"", StringComparison.Ordinal))?.EnrollState == "none",
            "Unknown status versions and states are ignored.");
        check(CrowdSec.ValidEnrollKey("cmh5r7ygd0001jw08wdqjt4sq") && !CrowdSec.ValidEnrollKey("short") && !CrowdSec.ValidEnrollKey("has space in it")
            && !CrowdSec.ValidEnrollKey("key\nLUCIA_CROWDSEC_TLS_KEY=x") && !CrowdSec.ValidEnrollKey(new string('a', 129)),
            "Enroll keys are one plain token, so they can't add lines to the app's environment.");
        check(CrowdSec.Duration("3h59m30.5s") == new TimeSpan(0, 3, 59, 30, 500) && CrowdSec.Duration("-1m") == TimeSpan.FromMinutes(-1)
            && CrowdSec.Duration("167h") == TimeSpan.FromHours(167), "Decision durations parse as CrowdSec writes them.");
        var lines = CrowdSec.Lines(new AppCertificate("CHAIN\n", "KEY\n", DateTimeOffset.UnixEpoch), "ROOT\r\n", ["127.0.0.0/8", "192.168.1.50"], "cmh5r7ygd0001jw08wdqjt4sq", "0123456789abcdef",
            "crowdsec.lab.example.com");
        var parsed = StackCatalog.ReadEnv(lines);
        check(parsed["LUCIA_CROWDSEC_HARBOR"] == "127.0.0.0/8,192.168.1.50" && parsed[CrowdSecApp.NameVariable] == "crowdsec.lab.example.com" && parsed["LUCIA_CROWDSEC_ENROLL_KEY"] == "cmh5r7ygd0001jw08wdqjt4sq"
            && Encoding.ASCII.GetString(Convert.FromBase64String(parsed["LUCIA_CROWDSEC_TLS_KEY"])) == "KEY\n"
            && Encoding.ASCII.GetString(Convert.FromBase64String(parsed["LUCIA_CROWDSEC_CA"])) == "ROOT\n"
            && lines.Split('\n').All(item => item.Length == 0 || item.StartsWith(StackStore.CrowdSecPrefix, StringComparison.Ordinal))
            && !CrowdSec.Lines(null, "ROOT", [], "bad key", "0123456789abcdef", null).Contains("ENROLL", StringComparison.Ordinal)
            && CrowdSec.Lines(null, "ROOT", [], null, null, "crowdsec.lab.example.com") is var waiting
            && !waiting.Contains("TLS_", StringComparison.Ordinal) && !waiting.Contains("NAME", StringComparison.Ordinal)
            && !CrowdSec.Lines(new AppCertificate("CHAIN\n", "KEY\n", DateTimeOffset.UnixEpoch), "ROOT", [], null, null, null).Contains("TLS_", StringComparison.Ordinal),
            "The engine's variables carry its name with its certificate and Lucia's root on one line each (never a name without a certificate), the harbor and only a valid enroll key.");
    }

    /// <summary>The host's side of app certificates, against an offline authority standing in for the Spark's worker.</summary>
    private static async Task CertificateChecks(Action<bool, string> check, string folder)
    {
        var now = DateTimeOffset.UtcNow;
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=Offline root", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var root = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
        using var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var intermediateRequest = new CertificateRequest("CN=Offline intermediate", intermediateKey, HashAlgorithmName.SHA256);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var intermediatePublic = intermediateRequest.Create(root, now.AddDays(-1), now.AddMonths(6), [1]);
        using var intermediate = intermediatePublic.CopyWithPrivateKey(intermediateKey);
        string Sign(string csrPem, string name, TimeSpan lifetime, X509Certificate2? by = null)
        {
            var request = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
            var leaf = new CertificateRequest(request.SubjectName, request.PublicKey, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(name);
            leaf.CertificateExtensions.Add(names.Build());
            leaf.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            leaf.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false));
            using var issued = leaf.Create(by ?? intermediate, now.AddMinutes(-1), now + lifetime, RandomNumberGenerator.GetBytes(8));
            return issued.ExportCertificatePem() + "\n" + intermediate.ExportCertificatePem() + "\n";
        }

        var options = Options.Create(new HardwareOnboardingOptions { StateDirectory = Path.Combine(folder, "data", "onboarding") });
        var certificates = new AppCertificates(options, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var nodeId = Guid.NewGuid();
        var requests = Path.Combine(folder, "data", "nodes", AppCertificates.Requests);
        var responses = Path.Combine(folder, "data", "nodes", AppCertificates.Responses);
        var request = Path.Combine(requests, "crowdsec.json");
        var ready = true;
        async Task Answer(Func<JsonNode, (bool Success, string? Chain)> worker)
        {
            var bytes = await File.ReadAllBytesAsync(request);
            var (success, chain) = worker(JsonNode.Parse(bytes)!);
            Directory.CreateDirectory(responses);
            await File.WriteAllTextAsync(Path.Combine(responses, "crowdsec.json"), new JsonObject
            {
                ["schemaVersion"] = 1, ["stack"] = "crowdsec", ["requestHash"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["success"] = success,
                ["message"] = success ? "App certificate signed." : "Lucia's authority refused this app certificate request.", ["checkedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["certificatePem"] = chain,
            }.ToJsonString());
        }

        const string Name = "crowdsec.lab.example.com", Renamed = "guard.lab.example.com";
        var (none, waiting) = await certificates.Ensure("crowdsec", nodeId, "192.168.1.20", Name, null, root, () => ready, CancellationToken.None);
        var asked = JsonNode.Parse(await File.ReadAllTextAsync(request))!;
        var csr = CertificateRequest.LoadSigningRequestPem(asked["csrPem"]!.GetValue<string>(), HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        check(none is null && waiting is null && asked["stack"]!.GetValue<string>() == "crowdsec" && asked["address"]!.GetValue<string>() == "192.168.1.20"
            && asked["nodeId"]!.GetValue<Guid>() == nodeId && asked["schemaVersion"]!.GetValue<int>() == 1
            && csr.SubjectName.Name == "CN=lucia-app-crowdsec" && csr.CertificateExtensions.Count == 1
            && csr.CertificateExtensions[0] is X509SubjectAlternativeNameExtension san && !san.EnumerateIPAddresses().Any()
            && san.EnumerateDnsNames().SequenceEqual([Name]) && !File.ReadAllText(request).Contains("PRIVATE KEY", StringComparison.Ordinal)
            && Directory.GetFiles(Path.Combine(folder, "data", "security", "app-certificates")).All(path => !File.ReadAllText(path).Contains("PRIVATE KEY", StringComparison.Ordinal)),
            "The host asks for the app's name under the domain, from its current server, under the stack's name, and keeps the key encrypted and to itself.");
        await Answer(_ => (false, null));
        var (stillNone, refused) = await certificates.Ensure("crowdsec", nodeId, "192.168.1.20", Name, null, root, () => ready, CancellationToken.None);
        check(stillNone is null && refused == "Lucia's authority refused this app certificate request.", "A refusal is shown as the worker wrote it.");
        await Answer(value => (true, Sign(value["csrPem"]!.GetValue<string>(), Name, TimeSpan.FromDays(7))));
        var (issued, problem) = await certificates.Ensure("crowdsec", nodeId, "192.168.1.20", Name, null, root, () => ready, CancellationToken.None);
        check(issued is not null && problem is null && issued.NotAfter > now.AddDays(6) && !File.Exists(request)
            && issued.KeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal) && AppCertificates.Valid(issued, Name, root, DateTimeOffset.UtcNow)
            && !AppCertificates.Valid(issued, Renamed, root, DateTimeOffset.UtcNow) && !issued.ToString().Contains("PRIVATE", StringComparison.Ordinal),
            "The signed chain is accepted only with the host's key, for the name it asked for.");
        var (kept, quiet) = await certificates.Ensure("crowdsec", nodeId, "192.168.1.20", Name, issued, root, () => ready, CancellationToken.None);
        check(kept == issued && quiet is null && !File.Exists(request), "A certificate with time to spare is kept, and nothing is asked.");

        // The certificate names the app, not its server: a move keeps it, a new name replaces it at once.
        var (moved, _) = await certificates.Ensure("crowdsec", nodeId, "192.168.1.30", Name, issued, root, () => ready, CancellationToken.None);
        check(moved == issued && !File.Exists(request), "A move to another server keeps the certificate, since local DNS follows the app.");
        var (renamed, _) = await certificates.Ensure("crowdsec", nodeId, "192.168.1.30", Renamed, issued, root, () => ready, CancellationToken.None);
        check(renamed is null && JsonNode.Parse(await File.ReadAllTextAsync(request))!["address"]!.GetValue<string>() == "192.168.1.30",
            "A new name needs a new certificate, asked for from the server the app runs on now.");
        await Answer(value => (true, Sign(value["csrPem"]!.GetValue<string>(), Name, TimeSpan.FromDays(7))));
        var (mismatch, warning) = await certificates.Ensure("crowdsec", nodeId, "192.168.1.30", Renamed, null, root, () => ready, CancellationToken.None);
        check(mismatch is null && warning is not null && !File.Exists(request), "A chain for another name is refused, and the request starts over.");
        await certificates.Ensure("crowdsec", nodeId, "192.168.1.30", Renamed, null, root, () => ready, CancellationToken.None);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var otherRequest = new CertificateRequest("CN=Other root", otherKey, HashAlgorithmName.SHA256);
        otherRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var otherRoot = otherRequest.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
        await Answer(value => (true, Sign(value["csrPem"]!.GetValue<string>(), Renamed, TimeSpan.FromDays(7), otherRoot)));
        var (foreign, _) = await certificates.Ensure("crowdsec", nodeId, "192.168.1.30", Renamed, null, root, () => ready, CancellationToken.None);
        check(foreign is null, "A chain that doesn't lead to Lucia's root is refused.");

        // Renewal: fewer than four days left asks again, and the current certificate keeps serving meanwhile.
        await certificates.Ensure("crowdsec", nodeId, "192.168.1.30", Renamed, null, root, () => ready, CancellationToken.None);
        await Answer(value => (true, Sign(value["csrPem"]!.GetValue<string>(), Renamed, TimeSpan.FromDays(3))));
        var (shortLived, _) = await certificates.Ensure("crowdsec", nodeId, "192.168.1.30", Renamed, null, root, () => ready, CancellationToken.None);
        ready = false;
        var (stillServing, down) = await certificates.Ensure("crowdsec", nodeId, "192.168.1.30", Renamed, shortLived, root, () => ready, CancellationToken.None);
        check(shortLived is not null && stillServing == shortLived && File.Exists(request) && down is not null && down.Contains("isn't running", StringComparison.Ordinal),
            "A certificate near its end keeps serving while the renewal waits, and a stopped worker is named.");
        await certificates.Forget("crowdsec", CancellationToken.None);
        check(!File.Exists(request) && Directory.GetFiles(Path.Combine(folder, "data", "security", "app-certificates")).Length == 0,
            "Removing the app drops its request and waiting key.");

        var env = new Dictionary<string, string>
        {
            ["LUCIA_CROWDSEC_TLS_CERT"] = Convert.ToBase64String(Encoding.ASCII.GetBytes(issued!.ChainPem)),
            ["LUCIA_CROWDSEC_TLS_KEY"] = Convert.ToBase64String(Encoding.ASCII.GetBytes(issued.KeyPem)),
        };
        check(CrowdSec.Certificate(env) is { } read && read.ChainPem == issued.ChainPem && read.KeyPem == issued.KeyPem
            && CrowdSec.Certificate(new Dictionary<string, string> { ["LUCIA_CROWDSEC_TLS_CERT"] = "not base64", ["LUCIA_CROWDSEC_TLS_KEY"] = "x" }) is null,
            "The engine's certificate reads back from its environment, and garbage reads as none.");
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "tools", "identity", "provision.py")))
                return directory.FullName;
        throw new InvalidOperationException("The checkout root was not found.");
    }
}
