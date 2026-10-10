using System.Text.Json;
using Lucia.Homelab.Server.Domains;

if (args is ["--export-ingress-fixture", var fixture])
{
    fixture = Path.GetFullPath(fixture);
    var certificates = Path.Combine(fixture, "certificates");
    Directory.CreateDirectory(certificates);
    File.WriteAllText(Path.Combine(certificates, "fullchain.pem"), "Replaced with a disposable test certificate by check_ingress.py.");
    File.WriteAllText(Path.Combine(certificates, "privkey.pem"), "Replaced with a disposable test key by check_ingress.py.");
    DomainIngressConfiguration.Publish(Path.Combine(fixture, "gateway"), DomainNames.Plan(new("example.test", "lab", "spark"), "example.test"),
        certificates, Path.Combine(certificates, "fullchain.pem"), Path.Combine(certificates, "privkey.pem"));
    Console.WriteLine(Path.Combine(fixture, "gateway", DomainIngressConfiguration.FileName));
    return;
}

var count = 0;
void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    count++;
}
void Reject(Action action)
{
    try { action(); }
    catch (ArgumentException) { count++; return; }
    throw new InvalidOperationException("Invalid domain input was accepted.");
}
var standard = DomainNames.Plan(new("example.com", "homelab", "atlas"), "example.com");
Check(standard.ServiceUrls.Lucia == "https://lucia.homelab.example.com", "Default Lucia URL is incorrect.");
Check(standard.ServiceUrls.Authentik == "https://auth.homelab.example.com", "Default Authentik URL is incorrect.");
Check(standard.CertificateNames.SequenceEqual(["homelab.example.com", "*.homelab.example.com"]), "Base/wildcard certificate coverage differs.");
var publicNaming = DomainNames.WithPublic(standard, true);
Check(publicNaming.ServiceUrls.Authentik == "https://auth.example.com" && publicNaming.ServiceUrls.Lucia == standard.ServiceUrls.Lucia
    && publicNaming.CertificateNames.SequenceEqual(["*.homelab.example.com", "*.example.com"])
    && publicNaming.LocalHostnames.Contains("auth.example.com") && !publicNaming.LocalHostnames.Contains("auth.homelab.example.com"),
    "Public access must move Authentik to auth.<zone> and cover public names with *.<zone>.");
Check(DomainNames.WithPublic(publicNaming, false).CertificateNames.SequenceEqual(standard.CertificateNames)
    && DomainNames.WithPublic(publicNaming, false).ServiceUrls == standard.ServiceUrls, "Turning public access off must restore the original names.");
{
    var certs = Path.Combine(Path.GetTempPath(), "lucia-public-check-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(certs);
    File.WriteAllText(Path.Combine(certs, "a.pem"), "");
    File.WriteAllText(Path.Combine(certs, "k.pem"), "");
    var ingress = System.Text.Json.Nodes.JsonNode.Parse(DomainIngressConfiguration.Build(publicNaming, certs, Path.Combine(certs, "a.pem"), Path.Combine(certs, "k.pem"), true))!;
    var closed = System.Text.Json.Nodes.JsonNode.Parse(DomainIngressConfiguration.Build(standard, certs, Path.Combine(certs, "a.pem"), Path.Combine(certs, "k.pem")))!;
    Directory.Delete(certs, recursive: true);
    Check(ingress["http"]!["routers"]!["domain-authentik"]!["entryPoints"]!.AsArray().Select(item => item!.GetValue<string>()).SequenceEqual(["host", "public"])
        && ingress["http"]!["routers"]!["domain-lucia"]!["entryPoints"]!.AsArray().Count == 1
        && ingress["http"]!["middlewares"]!["public-sources"]!["ipAllowList"]!["sourceRange"]!.AsArray().Count == DomainIngressConfiguration.CloudflareRanges.Length
        && closed["http"]!["middlewares"]?["public-sources"] is null
        && closed["http"]!["routers"]!["domain-authentik"]!["entryPoints"]!.AsArray().Count == 1,
        "Only Authentik may answer on the public entrypoint, and only from Cloudflare.");
}
var custom = DomainNames.Plan(new("example.com", "lab", "atlas",
    new("https://dashboard.example.com", "https://login.nested.lab.example.com", "https://atlas.lab.example.com")), "example.com");
Check(custom.ServiceUrls.Lucia == "https://dashboard.example.com", "Custom service URL was overwritten.");
Check(custom.CertificateNames.Contains("dashboard.example.com") && custom.CertificateNames.Contains("login.nested.lab.example.com"),
    "Names outside single-level wildcard coverage are missing exact SANs.");
Check(!custom.CertificateNames.Contains("atlas.lab.example.com"), "Covered one-level name was unnecessarily duplicated.");
var apex = DomainNames.Plan(new("example.com", "lab", "atlas",
    new("https://lab.example.com", "https://auth.lab.example.com", "https://atlas.lab.example.com")), "example.com");
Check(apex.CertificateNames.Contains("lab.example.com"), "Namespace apex is not separately covered.");
Check(DomainNames.Hostname("BÜCHER.example") == "xn--bcher-kva.example", "IDNA normalization failed.");
foreach (var bad in new[] { "", "*", "*.lab", "bad..name", "-bad", "a.", "https://lab", "192.168.0.1", "a b", new string('a', 64) })
    Reject(() => DomainNames.Hostname(bad));
foreach (var url in new[] { "http://lucia.example.com", "https://lucia.example.com/path", "https://lucia.example.com:8443",
    "https://user:secret@lucia.example.com", "https://lucia.example.com?query=1", "https://example.com.evil.invalid",
    "https://lucia.example.com/#fragment" })
    Reject(() => DomainNames.ServiceHost(url, "example.com"));
Reject(() => DomainNames.Plan(new("different.com", "lab", "atlas"), "example.com"));
Reject(() => DomainNames.Plan(new("example.com", "lab", "atlas",
    new("https://same.example.com", "https://same.example.com", "https://atlas.example.com")), "example.com"));

var root = Path.Combine(Path.GetTempPath(), "lucia-domains-check-" + Guid.NewGuid().ToString("N"));
try
{
    var options = new DomainOnboardingOptions { StateDirectory = root };
    var now = DateTimeOffset.UtcNow;
    var plan = new DomainSetupPlan(Guid.NewGuid(), "", now, now.AddMinutes(30), new string('a', 32), new string('b', 32),
        standard, "192.168.0.222", "owner@example.com", 60, "https://letsencrypt.org/documents/example.pdf",
        "https://adguard.example.com", "owner", [new("lucia.homelab.example.com", "192.168.0.222", false)], [], []);
    plan = plan with { ReviewHash = DomainOnboardingStore.Hash(plan) };
    using (var store = new DomainOnboardingStore(options))
    {
        Check((await store.Read()).Job is null, "A fresh domain store created a job.");
        await store.Update(state => state with { Plan = plan });
        Check((await store.Read()).Plan?.ReviewHash == plan.ReviewHash, "Domain review did not persist.");
        try
        {
            await store.AcceptReview(new(plan.Id, plan.ReviewHash, false, true), "owner", plan.TermsUrl);
            throw new InvalidOperationException("Terms were not required.");
        }
        catch (ArgumentException) { count++; }
        var accepted = await store.AcceptReview(new(plan.Id, plan.ReviewHash, true, true), "owner", plan.TermsUrl);
        Check((await store.Read()).Plan is null, "An accepted review was not consumed.");
        await store.Update(current => current with { Job = accepted with { State = "Active", NextRenewalAt = now.AddHours(12) } });
        var repeated = await store.AcceptReview(new(plan.Id, plan.ReviewHash, true, true), "owner", plan.TermsUrl);
        Check(repeated.Id == accepted.Id && repeated.State == "Active" && repeated.NextRenewalAt == now.AddHours(12),
            "Replaying Start replaced the active renewal job.");
        try
        {
            await store.AcceptReview(new(Guid.NewGuid(), plan.ReviewHash, true, true), "owner", plan.TermsUrl);
            throw new InvalidOperationException("A different review replaced an active job.");
        }
        catch (InvalidOperationException error) when (error.Message == "An existing DNS setup cannot be replaced.") { count++; }
        try
        {
            using var other = new DomainOnboardingStore(options);
            throw new InvalidOperationException("The workflow lease allowed two writers.");
        }
        catch (IOException) { count++; }
    }
    using (var restarted = new DomainOnboardingStore(options))
        Check((await restarted.Read()).Job?.Plan.ReviewHash == plan.ReviewHash, "Restart lost the reviewed configuration.");
    var path = Path.Combine(root, "workflow.json");
    var original = await File.ReadAllTextAsync(path);
    await File.WriteAllTextAsync(path, original.Replace("owner@example.com", "changed@example.com", StringComparison.Ordinal));
    using (var damaged = new DomainOnboardingStore(options))
    {
        try
        {
            await damaged.Read();
            throw new InvalidOperationException("Changed review data was accepted.");
        }
        catch (InvalidDataException) { count++; }
    }
    File.Delete(path);
    using (var missing = new DomainOnboardingStore(options))
    {
        try
        {
            await missing.Read();
            throw new InvalidOperationException("Missing initialized workflow was silently reset.");
        }
        catch (InvalidDataException) { count++; }
    }
}
finally { Directory.Delete(root, recursive: true); }
await RecoveryChecks.Run(Check);
await GatewayChecks.Run(Check);
await DnsChecks.Run(Check);
await SupportChecks.Run(Check);
await OperationsChecks.Run(Check);
await NodeEnrollmentChecks.Run(Check);
await DhcpChecks.Run(Check);
await StackChecks.Run(Check);
await LabMapChecks.Run(Check);
await PeopleChecks.Run(Check);
Console.WriteLine($"Domain naming, review, and recovery checks passed ({count} assertions). No live DNS or certificates changed.");
