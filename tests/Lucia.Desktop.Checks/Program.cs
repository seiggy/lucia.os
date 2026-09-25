using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lucia.Desktop.Core;
using Renci.SshNet.Sftp;

if (args is ["--upload-probe", var probeHost, var probeUsername, var probeKey, var probePayload])
{
    var probeConnection = new ConnectionOptions { Host = probeHost, Username = probeUsername, PrivateKeyPath = probeKey };
    BootstrapProtocol.Connection(probeConnection);
    var payload = BootstrapPayload.Build(probePayload, CancellationToken.None);
    var client = new BootstrapClient();
    var uploaded = await client.UploadAsync(probeConnection, payload.Archive, CancellationToken.None);
    Console.WriteLine("UPLOAD_PROBE=" + uploaded);
    Console.WriteLine("UPLOAD_SHA256=" + payload.Sha256);
    var nonce = Guid.NewGuid().ToString("N") + "'\"\n\u2603";
    var body = JsonSerializer.SerializeToUtf8Bytes(new { probe = nonce });
    var echoed = await client.ExecuteAsync(probeConnection, BootstrapClient.ScriptCommand(
        "import hashlib,json,sys; value=json.load(sys.stdin)['probe']; print(json.dumps({'sha256':hashlib.sha256(value.encode()).hexdigest()}))"),
        body, CancellationToken.None);
    using var result = JsonDocument.Parse(echoed);
    if (result.RootElement.GetProperty("sha256").GetString() != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(nonce))))
        throw new InvalidOperationException("The SSH stdin JSON handoff did not preserve the probe exactly.");
    Console.WriteLine("SSH_STDIN_VERIFIED=True");
    Console.WriteLine($"Uploaded the real {payload.Archive.Length}-byte payload with verified 0700 directory and 0600 file permissions. No installer was started.");
    return;
}

var assertions = 0;
void Check(bool condition, string name)
{
    assertions++;
    if (!condition) throw new InvalidOperationException("CHECK FAILED: " + name);
}
void Reject(Action action, string name)
{
    try { action(); }
    catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or IOException)
    {
        assertions++;
        return;
    }
    throw new InvalidOperationException("CHECK FAILED (accepted invalid input): " + name);
}
async Task RejectAsync(Func<Task> action, string name)
{
    try { await action(); }
    catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or IOException)
    {
        assertions++;
        return;
    }
    throw new InvalidOperationException("CHECK FAILED (accepted invalid input): " + name);
}
async Task OperationalError(Func<Task> action, string name)
{
    try { await action(); }
    catch (InvalidOperationException exception) when (exception.GetType() == typeof(InvalidOperationException) && exception.InnerException is null)
    {
        assertions++;
        return;
    }
    throw new InvalidOperationException("CHECK FAILED (unexpected public operational exception): " + name);
}

var bundledPayload = args switch
{
    [] => null,
    ["--payload", var directory] => Path.GetFullPath(directory),
    _ => throw new ArgumentException("Usage: Lucia.Desktop.Checks [--payload <source-or-BootstrapPayload-directory>] or --upload-probe <host> <SSH-user> <key-file> <payload-directory>. Only the explicit upload probe connects, requires an already-approved host pin, and uploads a payload without starting an installer.")
};

var workspace = Path.Combine(Directory.GetCurrentDirectory(), ".desktop-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspace);
try
{
    SftpFileAttributes Attributes(string permissions, int owner = 1000) =>
        (SftpFileAttributes)Activator.CreateInstance(typeof(SftpFileAttributes), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [DateTime.UtcNow, DateTime.UtcNow, 0L, owner, owner, Convert.ToUInt32(permissions, 8), null],
            culture: null)!;
    var directoryMode = Attributes("040775");
    Reject(() => directoryMode.SetPermissions(448), "SSH.NET rejects raw 0700 bitmask instead of chmod digits");
    directoryMode.SetPermissions(BootstrapClient.PrivateDirectoryMode);
    Check(BootstrapClient.HasPrivatePermissions(directoryMode, 1000, directory: true), "actual SSH.NET setter produces exactly private 0700 directories");
    var fileMode = Attributes("100664");
    Reject(() => fileMode.SetPermissions(384), "SSH.NET rejects raw 0600 bitmask instead of chmod digits");
    fileMode.SetPermissions(BootstrapClient.PrivateFileMode);
    Check(BootstrapClient.HasPrivatePermissions(fileMode, 1000, directory: false), "actual SSH.NET setter produces exactly private 0600 files");
    BootstrapClient.ValidateDirectory(Attributes("040775"), 1000, applicationCache: true);
    assertions++;
    Reject(() => BootstrapClient.ValidateDirectory(Attributes("040775"), 1000, applicationCache: false), "shared writable cache parent is not silently chmodded");
    Reject(() => BootstrapClient.ValidateDirectory(Attributes("040775", owner: 1001), 1000, applicationCache: true), "another account's staging directory cannot be adopted");
    Reject(() => BootstrapClient.ValidateDirectory(Attributes("120777"), 1000, applicationCache: true), "staging symlink remains rejected");
    Check(!BootstrapClient.HasPrivatePermissions(Attributes("040500"), 1000, directory: true), "missing owner write permission is rejected");
    Check(!BootstrapClient.HasPrivatePermissions(Attributes("104600"), 1000, directory: false), "unexpected special permission bits are rejected");
    var connection = new ConnectionOptions { Host = "Spark.local", Username = "spark", Password = "ssh-Sensitive-'🔒\nvalue" };
    var options = new SetupOptions
    {
        PublicHost = "192.168.0.222", OwnerUsername = "owner", OwnerPassword = "owner-Sensitive-67",
        SudoPassword = "sudo-Sensitive-23", InstallPrerequisites = true, ConfigureHost = false
    };
    var redactor = new Redactor(connection, options);
    var freshOptions = new SetupOptions
    {
        PublicHost = options.PublicHost, OwnerUsername = options.OwnerUsername,
        OwnerPassword = "desktop supplied ' quoted \" password 🔒", InstallPrerequisites = true, VerifyOnly = false
    };
    var requestBytes = BootstrapProtocol.Request(freshOptions);
    using (var request = JsonDocument.Parse(requestBytes))
    {
        var root = request.RootElement;
        Check(root.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
            .SetEquals(["schema_version", "public_host", "owner_username", "owner_password", "sudo_password", "install_prerequisites", "verify_only",
                "configure_host", "model_directory", "host_package"]), "fresh request has additive schema-1 host fields");
        Check(root.GetProperty("configure_host").GetBoolean(), "new desktop requests configure managed host");
        Check(root.GetProperty("host_package").ValueKind == JsonValueKind.Null, "installed artifacts need not be reuploaded");
        Check(root.GetProperty("owner_password").GetString() == freshOptions.OwnerPassword, "desktop owner password preserved exactly through JSON stdin framing");
        Check(root.GetProperty("sudo_password").ValueKind == JsonValueKind.Null, "fresh request has no sudo password by default");
        Check(root.GetProperty("install_prerequisites").GetBoolean() && !root.GetProperty("verify_only").GetBoolean(), "fresh install approval and mode serialized");
    }
    CryptographicOperations.ZeroMemory(requestBytes);
    BootstrapProtocol.Connection(connection);
    BootstrapProtocol.Setup(options, resume: false);
    var legacyOptions = new SetupOptions
    {
        PublicHost = options.PublicHost, OwnerUsername = options.OwnerUsername,
        OwnerPassword = " old pw ", VerifyOnly = true
    };
    BootstrapProtocol.Setup(legacyOptions, resume: false);
    assertions++;
    using (var legacyRequest = JsonDocument.Parse(BootstrapProtocol.Request(legacyOptions)))
        Check(legacyRequest.RootElement.GetProperty("owner_password").GetString() == " old pw ",
            "short legacy verification password passes unchanged without trimming");
    BootstrapProtocol.Setup(new SetupOptions
    {
        PublicHost = options.PublicHost, OwnerUsername = options.OwnerUsername, OwnerPassword = "old"
    }, resume: false, existingOwner: true);
    assertions++;
    foreach (var password in new[] { "short", "0123456789012\r4", "0123456789012\n4", "0123456789012\04", new string('x', 1025) })
        Reject(() => BootstrapProtocol.Setup(new SetupOptions
        {
            PublicHost = options.PublicHost, OwnerUsername = options.OwnerUsername, OwnerPassword = password
        }, resume: false), "new owner password policy enforced");
    BootstrapProtocol.Setup(new SetupOptions
    {
        PublicHost = options.PublicHost, OwnerUsername = options.OwnerUsername, OwnerPassword = new string('x', 1024)
    }, resume: false);
    assertions++;
    Reject(() => BootstrapProtocol.Setup(new SetupOptions
    {
        PublicHost = options.PublicHost, OwnerUsername = options.OwnerUsername, OwnerPassword = "", VerifyOnly = true
    }, resume: false), "existing owner verification still requires a nonempty password");
    var sshSettings = BootstrapClient.ConnectionInfo(connection, null);
    Check(sshSettings.RetryAttempts > 0 && sshSettings.Timeout <= TimeSpan.FromSeconds(30), "bounded SSH channel opens remain enabled");
    Check(sshSettings.AuthenticationMethods.Count == 1 && sshSettings.LoggerFactory is null, "one authentication method without credential logging");
    Check(BootstrapProtocol.Host("SPARK.local") == "spark.local", "host normalization");
    Check(BootstrapProtocol.Host("2001:db8::1") == "2001:db8::1", "IPv6 accepted");
    foreach (var host in new[] { "", " spark", "https://spark", "spark:22", "spark/x", "user@spark", "-spark", "a..b", "x\nx", "spark;touch", "[::1]", "fe80::1%eth0" })
        Reject(() => BootstrapProtocol.Host(host), "invalid hostname");
    Reject(() => BootstrapProtocol.Connection(new ConnectionOptions { Host = "spark", Port = 0, Username = "u", Password = "pw" }), "invalid port");
    Reject(() => BootstrapProtocol.Connection(new ConnectionOptions { Host = "spark", Username = "u\nbad", Password = "pw" }), "invalid username");
    Reject(() => BootstrapProtocol.Connection(new ConnectionOptions { Host = "spark", Username = "u" }), "authentication required");
    Reject(() => BootstrapProtocol.Connection(new ConnectionOptions { Host = "spark", Username = "u", Password = "pw", PrivateKeyPath = "key" }), "ambiguous authentication");
    BootstrapProtocol.Setup(new SetupOptions { PublicHost = "spark", OwnerUsername = "owner" }, resume: true);
    foreach (var path in new[] { "models", "/", "/data/../models", "/data\nmodels", @"C:\models", "/data\\models" })
        Reject(() => BootstrapProtocol.Setup(new SetupOptions { PublicHost = "spark", OwnerUsername = "owner", ModelDirectory = path },
            resume: true), "model import requires an absolute safe Linux path");
    Reject(() => BootstrapProtocol.Setup(new SetupOptions { PublicHost = "spark", OwnerUsername = "owner" }, resume: false), "password required only for new jobs");
    Reject(() => BootstrapProtocol.Setup(new SetupOptions { PublicHost = "spark", OwnerUsername = "-bad", OwnerPassword = "pw" }, resume: false), "invalid owner");

    var hostile = "a'\";$HOME\n$(touch injected)";
    Check(BootstrapProtocol.Quote("") == "''", "empty shell argument");
    Check(BootstrapProtocol.Quote(hostile) == "'a'\"'\"'\";$HOME\n$(touch injected)'", "POSIX single-quote escaping");
    Reject(() => BootstrapProtocol.Quote("bad\0arg"), "NUL rejected");
    var command = BootstrapClient.ScriptCommand("print('hello')\n", "start", "--archive", hostile, "--sha256", "abc");
    Check(command.StartsWith("python3 -c 'print('\"'\"'hello'\"'\"')", StringComparison.Ordinal), "source shell quoted");
    Check(!command.Contains(connection.Password!) && !command.Contains(options.OwnerPassword!), "command contains no credentials");
    Check(!connection.ToString().Contains(connection.Password!) && !options.ToString().Contains(options.OwnerPassword!), "models do not print secrets");
    var secrets = new[] { connection.Password!, options.OwnerPassword!, options.SudoPassword! };
    foreach (var secret in secrets)
    {
        var cleaned = redactor.Clean("failed: " + secret + " encoded " + JsonEncodedText.Encode(secret) + " url " +
            Uri.EscapeDataString(secret) + " b64 " + Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)));
        Check(!cleaned.Contains(secret) && !cleaned.Contains(Uri.EscapeDataString(secret)) &&
            !cleaned.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret))), "plain and encoded secret redaction");
    }
    Check(!redactor.Clean("password=unknown-secret").Contains("unknown-secret"), "unexpected password diagnostic withheld");
    Check(!redactor.Clean("-----BEGIN OPENSSH PRIVATE KEY-----\nsensitive").Contains("sensitive"), "unexpected private key withheld");
    Check(!redactor.Clean("a\u001b\u202eb").Any(c => char.IsControl(c) || c == '\u202e'), "control characters stripped");
    Check(redactor.Clean(new string('x', 1000)).Length <= 401, "bounded diagnostics");

    var pinsPath = Path.Combine(workspace, "pins");
    var pins = new HostPins(pinsPath);
    var hostKey = new HostKeyInfo("Spark.local", 22, "ssh-ed25519", "SHA256:" + Convert.ToBase64String(SHA256.HashData("host-one"u8)).TrimEnd('='));
    try { pins.Check(hostKey); throw new InvalidOperationException("Unknown host was accepted."); }
    catch (HostKeyConfirmationRequiredException exception) { Check(exception.Key == hostKey, "unknown fingerprint surfaced"); }
    Check(!Directory.Exists(pinsPath), "unapproved host check writes no local preferences");
    pins.Trust(hostKey);
    new HostPins(pinsPath).Check(hostKey with { Host = "spark.local" });
    assertions++;
    var changedKey = hostKey with { Fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData("host-two"u8)).TrimEnd('=') };
    Reject(() => pins.Check(changedKey), "changed key blocked");
    await OperationalError(() => { pins.Check(changedKey); return Task.CompletedTask; }, "host mismatch uses UI-handled operational exception");
    Reject(() => pins.Trust(changedKey), "changed key cannot be silently approved");
    Reject(() => pins.Check(hostKey with { Algorithm = "ssh-rsa" }), "algorithm mismatch blocked");
    pins.Check(hostKey);
    assertions++;
    var savedPin = Directory.GetFiles(pinsPath).Single();
    Check(!File.ReadAllText(savedPin).Contains("Sensitive"), "preferences contain no credentials");
    Reject(() => pins.Trust(hostKey with { Fingerprint = "SHA256:invalid" }), "invalid fingerprint rejected");
    File.WriteAllText(savedPin, "{broken");
    Reject(() => pins.Check(hostKey), "corrupt pin fails closed");

    using var key = RSA.Create(2048);
    InstallationResult Certificate(bool ca = true, bool expired = false, bool future = false)
    {
        var request = new CertificateRequest("CN=Lucia offline test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(ca ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(future ? 1 : -10),
            DateTimeOffset.UtcNow.AddDays(expired ? -1 : 10));
        return new InstallationResult("https://192.168.0.222:9443", "ldaps://192.168.0.222:636", "owner",
            certificate.ExportCertificatePem(), Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)), true);
    }
    var installation = Certificate();
    CertificateTrust.Validate(installation);
    assertions++;
    Reject(() => CertificateTrust.Validate(Certificate(ca: false)), "non-CA rejected");
    Reject(() => CertificateTrust.Validate(Certificate(expired: true)), "expired CA rejected");
    Reject(() => CertificateTrust.Validate(Certificate(future: true)), "future CA rejected");
    Reject(() => CertificateTrust.Validate(installation with { RootFingerprint = new string('0', 64) }), "fingerprint mismatch rejected");
    await OperationalError(() =>
    {
        CertificateTrust.Validate(installation with { RootFingerprint = new string('0', 64) });
        return Task.CompletedTask;
    }, "certificate validation uses UI-handled operational exception");
    Reject(() => CertificateTrust.Validate(installation with { RootCertificatePem = installation.RootCertificatePem + installation.RootCertificatePem }), "multiple CA certs rejected");
    Reject(() => CertificateTrust.Validate(installation with { RootCertificatePem = installation.RootCertificatePem + key.ExportPkcs8PrivateKeyPem() }), "certificate plus private key rejected");
    Reject(() => CertificateTrust.Validate(installation with { RootCertificatePem = key.ExportPkcs8PrivateKeyPem() }), "private key rejected");
    Reject(() => CertificateTrust.Validate(installation with { RootCertificatePem = "note\n" + installation.RootCertificatePem }), "unexpected PEM text rejected");
    var exported = Path.Combine(workspace, "public.crt");
    await CertificateTrust.ExportAsync(installation, exported);
    await CertificateTrust.ExportAsync(installation, exported);
    Check(File.ReadAllText(exported).Contains("BEGIN CERTIFICATE") && !File.ReadAllText(exported).Contains("PRIVATE KEY"), "public-only export");
    var differentCertificate = Certificate();
    await RejectAsync(() => CertificateTrust.ExportAsync(differentCertificate, exported), "refuse mismatched export overwrite");
    CertificateTrust.Validate(installation with { RootCertificatePem = File.ReadAllText(exported) });
    assertions++;
    Check(CertificateTrust.ScopeDescription.Contains(OperatingSystem.IsWindows() ? "current-user" :
        OperatingSystem.IsMacOS() ? "login keychain" : OperatingSystem.IsLinux() ? "all users" : "unavailable"), "accurate platform trust scope");

    var inspect = new JsonObject
    {
        ["schema_version"] = 1, ["hostname"] = "spark", ["architecture"] = "aarch64", ["operating_system"] = "Ubuntu",
        ["state_directory"] = "/home/u/.local/share/lucia/identity", ["is_installed"] = false, ["owner_ready"] = false,
        ["owner_username"] = null, ["public_host"] = null, ["can_install"] = true, ["requires_sudo"] = false,
        ["active_job_id"] = null, ["checks"] = new JsonArray(new JsonObject { ["name"] = "Docker", ["status"] = "ready", ["message"] = "Ready." }),
        ["planned_changes"] = new JsonArray("Install services."), ["authentik_url"] = null
    };
    var inspected = BootstrapProtocol.Inspection(inspect.ToJsonString(), connection.Host, redactor);
    Check(inspected.CanInstall && !inspected.IsInstalled && inspected.Checks.Count == 1, "complete inspect parses");
    Check(!inspected.HostReady && !inspected.ApplicationReady && inspected.HostPackageRequired, "legacy identity inspection never claims host readiness");
    void BadInspection(string property, JsonNode? value, string description)
    {
        var copy = (JsonObject)inspect.DeepClone();
        copy[property] = value;
        Reject(() => BootstrapProtocol.Inspection(copy.ToJsonString(), connection.Host, redactor), description);
    }
    BadInspection("schema_version", 2, "future protocol rejected");
    BadInspection("schema_version", "1", "string protocol rejected");
    BadInspection("is_installed", "false", "string boolean rejected");
    BadInspection("active_job_id", "../job", "traversal job rejected");
    BadInspection("checks", null, "missing checks rejected");
    BadInspection("authentik_url", "file:///C:/Windows", "file URL rejected");
    var missing = (JsonObject)inspect.DeepClone();
    missing.Remove("requires_sudo");
    Reject(() => BootstrapProtocol.Inspection(missing.ToJsonString(), connection.Host, redactor), "missing required field rejected");
    Reject(() => BootstrapProtocol.Document("{\"schema_version\":1,\"schema_version\":1}"), "duplicate fields rejected");
    Reject(() => BootstrapProtocol.Document("{}{}"), "multiple JSON objects rejected");
    await OperationalError(() => { BootstrapProtocol.Document("{}{}"); return Task.CompletedTask; }, "protocol validation uses UI-handled operational exception");
    Reject(() => BootstrapProtocol.Document("{\"schema_version\":1} diagnostic"), "trailing logs rejected");
    Reject(() => BootstrapProtocol.Document(new string(' ', BootstrapProtocol.MaximumResponseBytes + 1)), "oversized JSON rejected");
    var job = new string('a', 32);
    Check(BootstrapProtocol.Started($"{{\"schema_version\":1,\"status\":\"running\",\"job_id\":\"{job}\"}}") == job, "start result parsed");
    Reject(() => BootstrapProtocol.Started($"{{\"schema_version\":1,\"status\":\"succeeded\",\"job_id\":\"{job}\"}}"), "premature start success rejected");
    var status = new JsonObject
    {
        ["schema_version"] = 1, ["job_id"] = job, ["status"] = "succeeded", ["error"] = null,
        ["events"] = new JsonArray(new JsonObject { ["phase"] = "owner", ["level"] = "info", ["message"] = "Verified " + options.OwnerPassword }),
        ["result"] = new JsonObject
        {
            ["authentik_url"] = installation.AuthentikUrl, ["ldap_url"] = installation.LdapUrl,
            ["owner_username"] = installation.OwnerUsername, ["root_certificate_pem"] = installation.RootCertificatePem,
            ["root_fingerprint"] = installation.RootFingerprint, ["owner_login_verified"] = true
        }
    };
    var parsed = BootstrapProtocol.Status(status.ToJsonString(), job, options, redactor);
    Check(parsed.Result == installation && !parsed.Events[0].Message.Contains(options.OwnerPassword!), "real success validated and progress redacted");
    Reject(() => BootstrapProtocol.Status(status.ToJsonString(), job, freshOptions, redactor), "managed host flow rejects legacy identity-only success");
    var hostStatus = (JsonObject)status.DeepClone();
    hostStatus["result"]!["host_url"] = "https://192.168.0.222";
    hostStatus["result"]!["host_ready"] = true;
    hostStatus["result"]!["application_ready"] = true;
    Check(BootstrapProtocol.Status(hostStatus.ToJsonString(), job, freshOptions, redactor).Result!.HostReady,
        "host and application readiness allow completion");
    hostStatus["result"]!["application_ready"] = false;
    Reject(() => BootstrapProtocol.Status(hostStatus.ToJsonString(), job, freshOptions, redactor), "missing application prevents completion despite owner/host readiness");
    hostStatus["result"]!["application_ready"] = true;
    hostStatus["result"]!["host_url"] = "https://evil.test";
    Reject(() => BootstrapProtocol.Status(hostStatus.ToJsonString(), job, freshOptions, redactor), "host launch URL cannot leave reviewed host");
    BootstrapProtocol.LaunchUrl("https://[2001:db8::1]", "2001:db8::1");
    assertions++;
    var existingInspectionJson = (JsonObject)inspect.DeepClone();
    existingInspectionJson["is_installed"] = true;
    existingInspectionJson["public_host"] = options.PublicHost;
    existingInspectionJson["authentik_url"] = "https://192.168.0.222:10443";
    var existingInspection = BootstrapProtocol.Inspection(existingInspectionJson.ToJsonString(), connection.Host, redactor);
    var existingStatus = (JsonObject)status.DeepClone();
    existingStatus["result"]!["authentik_url"] = existingInspection.AuthentikUrl;
    existingStatus["result"]!["ldap_url"] = "ldaps://192.168.0.222:1636";
    Check(BootstrapProtocol.Status(existingStatus.ToJsonString(), job, options, redactor, existingInspection).Result!.LdapUrl
        == "ldaps://192.168.0.222:1636", "existing reviewed HTTPS port and same-host explicit LDAP port preserved");
    Reject(() => BootstrapProtocol.Status(existingStatus.ToJsonString(), job, options, redactor), "fresh install cannot silently change default ports");
    existingStatus["result"]!["authentik_url"] = "https://192.168.0.222:11443";
    Reject(() => BootstrapProtocol.Status(existingStatus.ToJsonString(), job, options, redactor, existingInspection), "existing HTTPS port cannot differ from inspection");
    existingStatus["result"]!["authentik_url"] = existingInspection.AuthentikUrl;
    existingStatus["result"]!["ldap_url"] = "ldaps://evil.test:1636";
    Reject(() => BootstrapProtocol.Status(existingStatus.ToJsonString(), job, options, redactor, existingInspection), "existing LDAP must retain intended host");
    existingStatus["result"]!["ldap_url"] = "ldaps://192.168.0.222";
    Reject(() => BootstrapProtocol.Status(existingStatus.ToJsonString(), job, options, redactor, existingInspection), "existing LDAP port must be explicit");
    void BadStatus(string property, JsonNode? value, string description, bool inResult = false)
    {
        var copy = (JsonObject)status.DeepClone();
        (inResult ? copy["result"]!.AsObject() : copy)[property] = value;
        Reject(() => BootstrapProtocol.Status(copy.ToJsonString(), job, options, redactor), description);
    }
    BadStatus("result", null, "success needs real result");
    BadStatus("owner_login_verified", false, "unverified owner rejected", inResult: true);
    BadStatus("owner_username", "different-owner", "different owner rejected", inResult: true);
    BadStatus("job_id", new string('b', 32), "wrong job rejected");
    BadStatus("status", "ready", "unknown status rejected");
    BadStatus("error", "actually failed", "success with error rejected");
    foreach (var url in new[] { "http://192.168.0.222:9443", "https://evil.test:9443", "https://192.168.0.222:443",
        "https://u@192.168.0.222:9443", "https://192.168.0.222:9443/?redirect=evil", "https://192.168.0.222:9443/#bad",
        "javascript:alert(1)", "file:///a" })
        BadStatus("authentik_url", url, "unsafe service URL rejected", inResult: true);
    BootstrapProtocol.ServiceUrl("https://[2001:db8::1]:9443", "2001:db8::1", "https", 9443);
    assertions++;
    var running = (JsonObject)status.DeepClone();
    running["status"] = "running";
    running["result"] = null;
    Check(BootstrapProtocol.Status(running.ToJsonString(), job, options, redactor).Result is null, "running job does not fabricate result");
    running["status"] = "failed";
    running["error"] = "Failed with " + options.SudoPassword;
    Check(!BootstrapProtocol.Status(running.ToJsonString(), job, options, redactor).Error!.Contains(options.SudoPassword!), "failure redaction");

    var hostPayloadRoot = Path.Combine(workspace, "HostPayload");
    Reject(() => HostArtifact.Load(hostPayloadRoot), "missing host bundle blocks, never fabricates an app shortcut");
    Directory.CreateDirectory(hostPayloadRoot);
    var hostArchive = Path.Combine(hostPayloadRoot, "host-linux-arm64.tar.gz");
    // DummyFixture: transport bytes, not a deployable Server archive.
    using (var file = File.Create(hostArchive))
        file.SetLength(17 * 1024 * 1024);
    string hostHash;
    using (var file = File.OpenRead(hostArchive))
        hostHash = Convert.ToHexStringLower(SHA256.HashData(file));
    var hostManifest = new JsonObject
    {
        ["schema_version"] = 1, ["version"] = "0.1.0", ["rid"] = "linux-arm64",
        ["entrypoint"] = "Lucia.Homelab.Server.dll",
        ["sha256"] = hostHash, ["size"] = new FileInfo(hostArchive).Length,
        ["files"] = new JsonArray(
            new JsonObject { ["path"] = "Lucia.Homelab.Server.dll", ["size"] = 1, ["sha256"] = hostHash },
            new JsonObject { ["path"] = "wwwroot/index.html", ["size"] = 1, ["sha256"] = hostHash })
    };
    void SaveHostManifest() => File.WriteAllText(Path.Combine(hostPayloadRoot, "manifest.json"), hostManifest.ToJsonString());
    SaveHostManifest();
    var artifact = HostArtifact.Load(hostPayloadRoot);
    await artifact.VerifyAsync(CancellationToken.None);
    Check(artifact.Size > BootstrapPayload.MaximumTotalBytes, "host archives exceed source payload bound without loading into memory");
    using (var hostRequest = JsonDocument.Parse(BootstrapProtocol.Request(freshOptions,
        new HostPackageUpload("/home/u/.cache/lucia-desktop/stage/host.tar.gz",
            "/home/u/.cache/lucia-desktop/stage/host-manifest.json", artifact.Sha256, artifact.Size))))
        Check(hostRequest.RootElement.GetProperty("host_package").GetProperty("sha256").GetString() == hostHash,
            "private SFTP paths and artifact hash serialized separately from credentials");
    hostManifest["files"]![0]!["path"] = "../escape.dll";
    SaveHostManifest();
    Reject(() => HostArtifact.Load(hostPayloadRoot), "host manifest traversal rejected");
    hostManifest["files"]![0]!["path"] = "Lucia.Homelab.Server.dll";
    hostManifest["size"] = 4L * 1024 * 1024 * 1024 + 1;
    SaveHostManifest();
    Reject(() => HostArtifact.Load(hostPayloadRoot), "archive larger than 4 GiB rejected");
    hostManifest["size"] = artifact.Size;
    var originalHostFiles = hostManifest["files"]!.DeepClone();
    var boundaryFiles = hostManifest["files"]!.AsArray();
    foreach (var file in boundaryFiles)
        file!["size"] = 1024L * 1024 * 1024;
    foreach (var name in new[] { "first.dll", "second.dll" })
        boundaryFiles.Add(new JsonObject { ["path"] = name, ["size"] = 1024L * 1024 * 1024, ["sha256"] = hostHash });
    SaveHostManifest();
    Check(HostArtifact.Load(hostPayloadRoot).Size == artifact.Size, "manifest expanded size accepts exactly 4 GiB without allocating its contents");
    boundaryFiles.Add(new JsonObject { ["path"] = "overflow.dll", ["size"] = 1, ["sha256"] = hostHash });
    SaveHostManifest();
    Reject(() => HostArtifact.Load(hostPayloadRoot), "manifest expanded size rejects 4 GiB plus one byte");
    hostManifest["files"] = originalHostFiles;
    SaveHostManifest();
    using (var file = File.OpenWrite(hostArchive))
        file.WriteByte(1);
    await RejectAsync(() => HostArtifact.Load(hostPayloadRoot).VerifyAsync(CancellationToken.None), "streamed host digest rejects tampering");

    var payloadRoot = Path.Combine(workspace, "BootstrapPayload");
    foreach (var file in BootstrapPayload.Files)
    {
        var path = Path.Combine(payloadRoot, file.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# public source\n");
    }
    File.WriteAllText(Path.Combine(payloadRoot, ".env"), "password=must-never-package");
    Directory.CreateDirectory(Path.Combine(payloadRoot, "bin"));
    File.WriteAllText(Path.Combine(payloadRoot, "bin", "secret.key"), "private");
    File.WriteAllText(Path.Combine(payloadRoot, "deployment", "identity", "unapproved.sh"), "must-not-package");
    await OperationalError(() => new BootstrapClient(payloadRoot, Path.Combine(workspace, "state"))
        .RunAsync(connection, inspected with { CanInstall = false, IsInstalled = false }, freshOptions, new Progress<BootstrapEvent>()),
        "blocked fresh inspection fails before authentication or upload");
    await OperationalError(() => new BootstrapClient(Path.Combine(workspace, "missing-payload"), Path.Combine(workspace, "state"))
        .InspectAsync(connection), "missing payload is actionable before any connection");
    var invalidKey = Path.Combine(workspace, "invalid-ssh.key");
    File.WriteAllText(invalidKey, "not a private key; never expose this content");
    await OperationalError(() => new BootstrapClient(payloadRoot, Path.Combine(workspace, "state"))
        .InspectAsync(new ConnectionOptions { Host = "spark", Username = "spark", PrivateKeyPath = invalidKey }),
        "SSH key-loading failure is normalized before any network access");
    var payload = BootstrapPayload.Build(payloadRoot, CancellationToken.None);
    Check(payload.Sha256 == Convert.ToHexStringLower(SHA256.HashData(payload.Archive)), "archive hash verified");
    var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    using (var data = new MemoryStream(payload.Archive))
    using (var gzip = new GZipStream(data, CompressionMode.Decompress))
    using (var tar = new TarReader(gzip))
    {
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            Check(entry.EntryType == TarEntryType.RegularFile, "only regular archive files");
            Check(!entry.Name.Contains("..") && !entry.Name.StartsWith('/'), "relative safe archive paths");
            using var content = new MemoryStream();
            entry.DataStream!.CopyTo(content);
            entries.Add(entry.Name, content.ToArray());
        }
    }
    Check(entries.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(BootstrapPayload.Files.Append("manifest.json")), "exact source allowlist, no secrets or build output");
    using (var manifest = JsonDocument.Parse(entries["manifest.json"]))
    {
        Check(manifest.RootElement.GetProperty("schema_version").GetInt32() == 1 &&
            manifest.RootElement.GetProperty("version").GetString() == "0.1.0", "manifest version");
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
            Check(file.GetProperty("sha256").GetString() == Convert.ToHexStringLower(SHA256.HashData(entries[file.GetProperty("path").GetString()!])), "manifest file hash");
    }
    var scriptPath = Path.Combine(payloadRoot, "tools", "desktop", "bootstrap.py");
    File.WriteAllText(scriptPath, new string('x', 101 * 1024));
    Reject(() => BootstrapPayload.Script(payloadRoot), "single SSH argument size bounded");
    File.WriteAllText(scriptPath, new string('x', BootstrapPayload.MaximumFileBytes + 1));
    Reject(() => BootstrapPayload.Build(payloadRoot, CancellationToken.None), "individual payload size bounded");
    File.WriteAllText(scriptPath, key.ExportPkcs8PrivateKeyPem());
    Reject(() => BootstrapPayload.Build(payloadRoot, CancellationToken.None), "private key cannot enter approved payload file");
    File.Delete(scriptPath);
    try
    {
        File.CreateSymbolicLink(scriptPath, exported);
        Reject(() => BootstrapPayload.Build(payloadRoot, CancellationToken.None), "approved symlink rejected");
        File.Delete(scriptPath);
    }
    catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
    {
        Console.WriteLine("SKIP symlink fixture: local OS does not permit creating a symbolic link.");
    }
    File.WriteAllText(scriptPath, "print('inspect')");
    var totalFile = new string('x', BootstrapPayload.MaximumFileBytes);
    foreach (var file in BootstrapPayload.Files)
        File.WriteAllText(Path.Combine(payloadRoot, file.Replace('/', Path.DirectorySeparatorChar)), totalFile);
    Reject(() => BootstrapPayload.Build(payloadRoot, CancellationToken.None), "total payload size bounded");

    var cancelled = new CancellationToken(canceled: true);
    try { BootstrapPayload.Build(payloadRoot, cancelled); throw new InvalidOperationException("Cancellation ignored."); }
    catch (OperationCanceledException) { assertions++; }
    try { await new BootstrapClient(payloadRoot, Path.Combine(workspace, "state")).InspectAsync(connection, cancelled); throw new InvalidOperationException("Cancellation ignored."); }
    catch (OperationCanceledException) { assertions++; }
    try
    {
        await new BootstrapClient(payloadRoot, Path.Combine(workspace, "state")).RunAsync(connection,
            inspected with { ActiveJobId = job, CanInstall = false },
            new SetupOptions { PublicHost = options.PublicHost, OwnerUsername = options.OwnerUsername },
            new Progress<BootstrapEvent>(), cancelled);
        throw new InvalidOperationException("Cancellation ignored.");
    }
    catch (OperationCanceledException) { assertions++; }
    Check(!Directory.Exists(Path.Combine(workspace, "state")), "cancelled inspection has no preferences or upload side effects");
    try { await CertificateTrust.ExportAsync(installation, Path.Combine(workspace, "cancelled.crt"), cancelled); throw new InvalidOperationException("Cancellation ignored."); }
    catch (OperationCanceledException) { assertions++; }
    Check(!File.Exists(Path.Combine(workspace, "cancelled.crt")), "cancelled export writes nothing");

    if (bundledPayload is not null)
    {
        var bundledScript = BootstrapPayload.Script(bundledPayload);
        var actualPayload = BootstrapPayload.Build(bundledPayload, CancellationToken.None);
        Check(!string.IsNullOrWhiteSpace(bundledScript), "actual bundled entry point is usable");
        Check(actualPayload.Sha256 == Convert.ToHexStringLower(SHA256.HashData(actualPayload.Archive)), "actual payload hash verified");
        Console.WriteLine($"PASS: actual allowlisted payload packaged ({actualPayload.Archive.Length:N0} compressed bytes).");
    }

    Console.WriteLine($"PASS: {assertions} offline checks. No network connections or OS trust changes.");
}
finally
{
    Directory.Delete(workspace, recursive: true);
}
