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
        }
        finally { Directory.Delete(folder, recursive: true); }
        return Task.CompletedTask;
    }
}
