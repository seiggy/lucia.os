using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lucia.Homelab.Server.Boot;
using Microsoft.Extensions.Options;

var checks = 0;
var clock = new ManualClock();
var challenges = new DiscoveryChallenges(clock);
var address = IPAddress.Parse("192.0.2.10");
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
const string report = """{"architecture":"x86_64","disks":[]}""";
SignedDiscovery Sign(DiscoveryChallenge challenge, ECDsa signingKey, string inventory = report) =>
    new(challenge.ChallengeId, signingKey.ExportSubjectPublicKeyInfoPem(), inventory,
        Convert.ToBase64String(signingKey.SignData(
            Encoding.UTF8.GetBytes($"lucia-discovery-v1\n{challenge.ChallengeId}\n{challenge.Nonce}\n{inventory}"),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Reject(Action action, int expected)
{
    try { action(); }
    catch (DiscoveryProtocolException error)
    {
        Check(error.StatusCode == expected, "Unexpected protocol error status.");
        return;
    }
    throw new InvalidOperationException("Invalid discovery proof was accepted.");
}

var challenge = challenges.Issue(address);
var signed = Sign(challenge, key);
Reject(() => challenges.Verify(signed with { ReportJson = report + " " }, address), 401);
Reject(() => challenges.Verify(signed with { Signature = new string('A', 88) }, address), 401);
Reject(() => challenges.Verify(signed, IPAddress.Parse("192.0.2.11")), 401);
Reject(() => challenges.Verify(signed with { PublicKeyPem = key.ExportPkcs8PrivateKeyPem() }, address), 401);
var verified = challenges.Verify(signed, address);
Check(verified.ReportJson == report && verified.PublicKeyFingerprint ==
    Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo())), "Verified inventory or identity changed.");
Reject(() => challenges.Verify(signed, address), 401);
Reject(() => challenges.Verify(signed with { ReportJson = new string('x', 131073) }, address), 400);
Reject(() => challenges.Verify(signed with { ChallengeId = "" }, address), 400);

var expired = Sign(challenges.Issue(address), key);
clock.Now += TimeSpan.FromMinutes(2);
Reject(() => challenges.Verify(expired, address), 401);
using var wrongCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
Reject(() => challenges.Verify(Sign(challenges.Issue(address), wrongCurve), address), 400);

var racing = Sign(challenges.Issue(address), key);
var successes = 0;
Parallel.For(0, 16, _ =>
{
    try { challenges.Verify(racing, address); Interlocked.Increment(ref successes); }
    catch (DiscoveryProtocolException) { }
});
Check(successes == 1, "Concurrent proof replay was accepted.");
var limited = new DiscoveryChallenges(clock);
for (var i = 0; i < 128; i++) limited.Issue(address);
Reject(() => limited.Issue(address), 429);
clock.Now += TimeSpan.FromMinutes(2);
Check(limited.Issue(address).ExpiresAt > clock.Now, "Expired challenges were not reclaimed.");
new BootOptions().Validate();
Check(!new BootOptions().Allows(address), "Discovery accepted traffic while disabled.");
var boot = new BootOptions { Enabled = true, ControlDirectory = Path.Combine(Path.GetTempPath(), "lucia-boot-checks"), AllowedNetworks = ["192.168.0.0/23"] };
boot.Validate();
Check(boot.Allows(IPAddress.Parse("192.168.1.42")) && boot.Allows(IPAddress.Parse("::ffff:192.168.0.42"))
    && !boot.Allows(IPAddress.Parse("192.168.2.1")) && !boot.Allows(null), "Provisioning subnet boundary is incorrect.");
foreach (var network in new[] { "0.0.0.0/0", "127.0.0.0/8", "8.8.8.0/24", "::/0", "invalid" })
{
    try
    {
        new BootOptions { Enabled = true, ControlDirectory = boot.ControlDirectory, AllowedNetworks = [network] }.Validate();
        throw new InvalidOperationException("Unbounded provisioning network was accepted.");
    }
    catch (OptionsValidationException) { checks++; }
}
var control = Path.Combine(Path.GetTempPath(), "lucia-boot-lease-" + Guid.NewGuid().ToString("N"));
var leasePath = Path.Combine(control, "admission.json");
try
{
    using (var lease = new BootAdmissionLease(control, clock))
    {
        Check(!File.Exists(leasePath), "Starting the boot controller opened admission.");
        lease.Update(clock.Now.AddMinutes(30));
        using (var document = JsonDocument.Parse(File.ReadAllBytes(leasePath)))
        {
            var value = document.RootElement;
            Check(value.EnumerateObject().Count() == 4 && value.GetProperty("schemaVersion").GetInt32() == 1
                && value.GetProperty("expiresAt").GetDateTimeOffset() == clock.Now.AddSeconds(10),
                "Boot heartbeat schema or lifetime differs from the file-service contract.");
        }
        lease.Update(clock.Now.AddSeconds(2));
        using (var document = JsonDocument.Parse(File.ReadAllBytes(leasePath)))
            Check(document.RootElement.GetProperty("expiresAt").GetDateTimeOffset() == clock.Now.AddSeconds(2),
                "Boot heartbeat outlived the admission window.");
        lease.Update(null);
        Check(!File.Exists(leasePath), "Closing admission retained a boot lease.");
        lease.Update(clock.Now.AddMinutes(30));
    }
    Check(!File.Exists(leasePath), "Stopping the controller retained an active lease.");
    File.WriteAllText(leasePath, "{}");
    using (var restarted = new BootAdmissionLease(control, clock))
        Check(!File.Exists(leasePath), "Restart restored a previous boot admission window.");
}
finally
{
    Directory.Delete(control, recursive: true);
}
await BootHttpChecks.Run(Check);
Console.WriteLine($"Discovery proof and HTTP checks passed ({checks} assertions). Synthetic loopback fixtures; no physical devices were changed.");

sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}
