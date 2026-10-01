using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

internal static class NodeEnrollmentChecks
{
    internal static Task Run(Action<bool, string> check)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var id = Guid.NewGuid();
        var task = Guid.NewGuid();
        var request = new CertificateRequest("CN=" + id.ToString("D"), key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("synthetic-node");
        request.CertificateExtensions.Add(san.Build());
        var csr = request.CreateSigningRequestPem();
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
        ManagedNodeEnrollment.ValidateCsr(new(id, task, csr), "synthetic-node", fingerprint);
        check(true, "A signed P-256 enrollment CSR was rejected.");
        void Reject(Action action)
        {
            try { action(); throw new InvalidOperationException("Expected identity denial."); }
            catch (HardwareOnboardingException error) when (error.StatusCode == 403) { check(true, "Enrollment rejected."); }
        }
        Reject(() => ManagedNodeEnrollment.ValidateCsr(new(id, task, csr), "another-node", fingerprint));
        Reject(() => ManagedNodeEnrollment.ValidateCsr(new(Guid.NewGuid(), task, csr), "synthetic-node", fingerprint));
        Reject(() => ManagedNodeEnrollment.ValidateCsr(new(id, task, csr), "synthetic-node", new string('0', 64)));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        Reject(() => ManagedNodeEnrollment.ValidateCsr(new(id, task, request.CreateSigningRequestPem()), "synthetic-node", fingerprint));
        var metrics = new NodeHeartbeat(id, "synthetic-node", "Debian GNU/Linux 13 (trixie)", 60, 0.2, 8192, 4096, 102400, 51200);
        ManagedNodeEnrollment.ValidateHeartbeat(id, metrics);
        check(true, "Valid managed metrics were rejected.");
        try { ManagedNodeEnrollment.ValidateHeartbeat(id, metrics with { UptimeSeconds = double.NaN }); throw new InvalidOperationException("NaN accepted"); }
        catch (HardwareOnboardingException error) when (error.StatusCode == 400) { check(true, "Invalid metrics rejected."); }
        var runtime = new NodeRuntime("Ready", "26.1.5", "2.26.1", true, [new("nvidia", "NVIDIA CMP 170HX", 64L << 30, "8.0")]);
        ManagedNodeEnrollment.ValidateHeartbeat(id, metrics with { Runtime = runtime });
        check(true, "Valid container runtime was rejected.");
        foreach (var bad in new[] { runtime with { State = "Maybe" }, runtime with { Gpus = [new("amd", "x", null, null)] },
            runtime with { Message = "line\nbreak" }, runtime with { Gpus = [.. Enumerable.Repeat(runtime.Gpus[0], 17)] } })
        {
            try { ManagedNodeEnrollment.ValidateHeartbeat(id, metrics with { Runtime = bad }); throw new InvalidOperationException("Bad runtime accepted"); }
            catch (HardwareOnboardingException error) when (error.StatusCode == 400) { check(true, "Invalid runtime rejected."); }
        }
        var updates = new NodeUpdateStatus("Idle", DateTimeOffset.UtcNow, 1, 1, [new("libc6", "2.41-12", "2.41-12+deb13u1", true)], false, "1 update available.");
        ManagedNodeEnrollment.ValidateHeartbeat(id, metrics with { Updates = updates, AgentRelease = new string('a', 64) });
        check(true, "Valid update status was rejected.");
        foreach (var bad in new[]
        {
            metrics with { Updates = updates with { State = "Maybe" } },
            metrics with { Updates = updates with { Packages = [.. Enumerable.Repeat(updates.Packages[0], 51)] } },
            metrics with { Updates = updates with { Count = -1 } },
            metrics with { Updates = updates with { Message = "line\nbreak" } },
            metrics with { AgentRelease = "not-a-release" },
            metrics with { CpuPercent = 101 },
            metrics with { GpuPercent = double.NaN },
            metrics with { CpuTemperatureCelsius = 400 },
            metrics with { Features = ["Exec"] },
            metrics with { Features = ["exec", null!] },
            metrics with { Features = [.. Enumerable.Repeat("exec", 17)] },
        })
        {
            try { ManagedNodeEnrollment.ValidateHeartbeat(id, bad); throw new InvalidOperationException("Bad update status accepted"); }
            catch (HardwareOnboardingException error) when (error.StatusCode == 400) { check(true, "Invalid update status rejected."); }
        }
        ManagedNodeEnrollment.ValidateHeartbeat(id, metrics with { CpuPercent = 12.5, CpuTemperatureCelsius = 54, GpuPercent = 0, GpuTemperatureCelsius = 38 });
        check(true, "Valid utilization was rejected.");
        ManagedNodeEnrollment.ValidateHeartbeat(id, metrics with { Features = ["exec", "future-thing"] });
        check(true, "Valid agent features were rejected.");
        var releaseFolder = Path.Combine(Path.GetTempPath(), "lucia-release-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(releaseFolder);
        try
        {
            File.WriteAllText(Path.Combine(releaseFolder, "a"), "1");
            File.WriteAllText(Path.Combine(releaseFolder, "B"), "2");
            // The agent computes the same ID over its installed files; see Lucia.NodeAgent.Checks.
            check(NodeAgentRelease.Compute(releaseFolder) == "72a3aae02a41db9bf5c8a41a53348020cdeba3d09ff4d7a89a87f27d86705f9e",
                "Agent release ID changed.");
        }
        finally { Directory.Delete(releaseFolder, recursive: true); }
        var folder = Path.Combine(Path.GetTempPath(), "lucia-node-cert-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var rootRequest = new CertificateRequest("CN=Disposable node checks", rootKey, HashAlgorithmName.SHA256);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(3));
            var caPath = Path.Combine(folder, "ca.pem");
            File.WriteAllText(caPath, root.ExportCertificatePem());
            var leafRequest = new CertificateRequest("CN=" + id.ToString("D"), key, HashAlgorithmName.SHA256);
            leafRequest.CertificateExtensions.Add(san.Build());
            leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
            using var expired = leafRequest.Create(root, DateTimeOffset.UtcNow.AddHours(-25), DateTimeOffset.UtcNow.AddHours(-1), RandomNumberGenerator.GetBytes(16));
            var nodeOptions = Options.Create(new HardwareOnboardingOptions { StateDirectory = Path.Combine(folder, "onboarding") });
            using var store = new HardwareOnboardingStore(nodeOptions, TimeProvider.System, NullLogger<HardwareOnboardingStore>.Instance);
            var service = new ManagedNodeEnrollment(store, nodeOptions, new HostAuthenticationOptions { CaCertificatePath = caPath });
            Reject(() => service.ValidateCertificate(id, "synthetic-node", fingerprint, expired.ExportCertificatePem()));
            service.ValidateCertificate(id, "synthetic-node", fingerprint, expired.ExportCertificatePem(), renewal: true);
            check(true, "A known key's expired certificate cannot be checked for renewal recovery.");
            Reject(() => service.ValidateCertificate(Guid.NewGuid(), "synthetic-node", fingerprint, expired.ExportCertificatePem(), renewal: true));

            var sshKeys = new OwnerSshKeys(nodeOptions);
            var sshKey = "ssh-ed25519 " + Convert.ToBase64String([0, 0, 0, 11, .. "ssh-ed25519"u8, 0, 0, 0, 32, .. RandomNumberGenerator.GetBytes(32)]);
            var added = sshKeys.Add("zack", new(sshKey + " zack@laptop", null), DateTimeOffset.UtcNow, default).GetAwaiter().GetResult();
            check(added.Keys is [{ Label: "zack@laptop" }] && added.Keys[0].PublicKey == sshKey, "An owner SSH key was not stored normalized with its label.");
            check(sshKeys.Authorized(default).GetAwaiter().GetResult() is { Count: 1 } authorized && authorized["zack"].SequenceEqual([sshKey]),
                "Heartbeat key list did not match the stored keys.");
            try { sshKeys.Add("zack", new(sshKey, "again"), DateTimeOffset.UtcNow, default).GetAwaiter().GetResult(); check(false, "Duplicate key accepted."); }
            catch (HardwareOnboardingException error) when (error.StatusCode == 409) { check(true, "Duplicate rejected."); }
            foreach (var name in new[] { "root", "lucia-recovery", "Zack", "../x", "" })
            {
                try { OwnerSshKeys.Username(name); check(false, "Unsafe SSH username accepted."); }
                catch (HardwareOnboardingException error) when (error.StatusCode == 400) { check(true, "Unsafe username rejected."); }
            }
            var removed = sshKeys.Remove("zack", added.Keys[0].Id, default).GetAwaiter().GetResult();
            check(removed.Keys.Length == 0 && sshKeys.Authorized(default).GetAwaiter().GetResult().Count == 0, "A removed key was still sent to nodes.");
        }
        finally { Directory.Delete(folder, recursive: true); }
        return Task.CompletedTask;
    }
}
