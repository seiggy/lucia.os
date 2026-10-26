using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Security;
using Lucia.Homelab.Server.Stacks;

/// <summary>The gateway's CrowdSec middlewares, the safe harbor's validation, and the pins shared with provisioning.</summary>
internal static class SecurityChecks
{
    internal static Task Run(Action<bool, string> check)
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

        var rendered = JsonNode.Parse(GatewaySecurity.Render(harbor, cloudflare))!["http"]!["middlewares"]!;
        var decisions = rendered[GatewaySecurity.Middleware]!["plugin"]![GatewaySecurity.PluginName]!;
        var appsec = rendered[GatewaySecurity.AppSecMiddleware]!["plugin"]![GatewaySecurity.PluginName]!;
        string[] Strings(JsonNode node) => [.. node.AsArray().Select(item => item!.GetValue<string>())];
        check(rendered.AsObject().Count == 2 && decisions["crowdsecMode"]!.GetValue<string>() == "stream"
            && decisions["updateMaxFailure"]!.GetValue<int>() == -1 && !decisions["streamStartupBlock"]!.GetValue<bool>()
            && decisions["crowdsecAppsecEnabled"] is null,
            "The ban check must use the decision stream and keep serving when the engine is unreachable.");
        check(appsec["crowdsecMode"]!.GetValue<string>() == "appsec" && appsec["crowdsecAppsecEnabled"]!.GetValue<bool>()
            && !appsec["crowdsecAppsecFailureBlock"]!.GetValue<bool>() && !appsec["crowdsecAppsecUnreachableBlock"]!.GetValue<bool>()
            && !appsec["crowdsecAppsecUnreadableBodyBlock"]!.GetValue<bool>() && appsec["httpTimeoutSeconds"]!.GetValue<int>() == 2,
            "AppSec must let requests through when the engine fails, can't be reached, or can't read a streamed body.");
        foreach (var middleware in new[] { decisions, appsec })
            check(Strings(middleware["clientTrustedIps"]!).SequenceEqual(harbor)
                && Strings(middleware["forwardedHeadersTrustedIps"]!).SequenceEqual(cloudflare)
                && middleware["crowdsecLapiKey"] is null && middleware["crowdsecLapiKeyFile"]!.GetValue<string>() == GatewaySecurity.KeyFile
                && middleware["crowdsecLapiHost"]!.GetValue<string>() == "identity-crowdsec:8080",
                "Both middlewares skip the harbor, read visitors only behind Cloudflare and keep the key in its file.");

        var folder = Path.Combine(Path.GetTempPath(), "lucia-security-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var security = Path.Combine(folder, "security");
            check(GatewaySecurity.Publish(security, harbor, cloudflare) && !GatewaySecurity.Publish(security, harbor, cloudflare)
                && File.ReadAllText(Path.Combine(security, GatewaySecurity.HarborFileName)) == string.Concat(harbor.Select(item => item + "\n"))
                && File.ReadAllText(Path.Combine(security, GatewaySecurity.FileName)) == GatewaySecurity.Render(harbor, cloudflare)
                && Directory.GetFiles(security).Length == 2,
                "Publishing writes the middlewares and the engine's list once, and leaves unchanged files alone.");
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

        var root = Root();
        var profiles = File.ReadAllText(Path.Combine(root, "deployment", "identity", "crowdsec", "profiles.yaml"));
        check(cloudflare.All(range => profiles.Contains($"'{range}'", StringComparison.Ordinal)),
            "profiles.yaml must never ban any of Cloudflare's ranges.");
        check(profiles.Contains("'720h') < 1 ? '4h'", StringComparison.Ordinal) && profiles.Contains("'24h' : '168h'", StringComparison.Ordinal),
            "Public addresses are banned for 4 hours, then a day, then a week.");
        var provision = File.ReadAllText(Path.Combine(root, "tools", "identity", "provision.py"));
        check(provision.Contains($"PLUGIN_TREE_SHA256 = \"{GatewaySecurity.PluginTreeSha256}\"", StringComparison.Ordinal)
            && provision.Contains($"PLUGIN_VERSION = \"{GatewaySecurity.PluginVersion}\"", StringComparison.Ordinal)
            && provision.Contains($"PLUGIN_MODULE = \"{GatewaySecurity.PluginModule}\"", StringComparison.Ordinal),
            "provision.py and GatewaySecurity.cs must pin the same plugin release and tree.");

        var relay = Lucia.Homelab.Server.Telemetry.TelemetryRelay.Config("\"spark\"", false, [new("crowdsec", "127.0.0.1:19060", Authenticated: false)], "debug: {}");
        var job = relay[relay.IndexOf("job_name: crowdsec", StringComparison.Ordinal)..];
        check(job.Contains("127.0.0.1:19060", StringComparison.Ordinal) && !job[..job.IndexOf("relabel_configs", StringComparison.Ordinal)].Contains("authorization", StringComparison.Ordinal),
            "The relay scrapes CrowdSec's metrics on loopback without an app key.");
        return Task.CompletedTask;
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "deployment", "identity", "crowdsec", "profiles.yaml")))
                return directory.FullName;
        throw new InvalidOperationException("The checkout root was not found.");
    }
}
