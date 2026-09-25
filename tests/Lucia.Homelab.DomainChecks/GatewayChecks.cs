using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal static class GatewayChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "lucia-domain-tls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var rootKey = RSA.Create(2048);
            var rootRequest = new CertificateRequest("CN=Domain checks root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(2));
            using var leafKey = RSA.Create(2048);
            var leafRequest = new CertificateRequest("CN=localhost", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            leafRequest.CertificateExtensions.Add(names.Build());
            using var unsignedKey = leafRequest.Create(root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
            using var combined = unsignedKey.CopyWithPrivateKey(leafKey);
            using var leaf = X509CertificateLoader.LoadPkcs12(combined.Export(X509ContentType.Pfx), null);
            var ca = Path.Combine(folder, "ca.pem");
            await File.WriteAllTextAsync(ca, root.ExportCertificatePem());
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(leaf)));
            await using var app = builder.Build();
            app.MapGet("/health/live", () => Results.Json(new { status = "ok" }));
            await app.StartAsync();
            var origin = app.Urls.Single().Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
            var trust = new HostAuthenticationOptions { CaCertificatePath = ca };
            var fingerprint = leaf.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
            await DomainConnectivity.VerifyGateway(origin, "127.0.0.1", "/health/live", trust, false,
                value => value.GetProperty("status").GetString() == "ok", default, fingerprint);
            check(true, "Correct served certificate was not accepted.");
            try
            {
                await DomainConnectivity.VerifyGateway(origin, "127.0.0.1", "/health/live", trust, false,
                    _ => true, default, new string('0', 64));
                throw new InvalidOperationException("A valid but different served certificate was accepted as deployed.");
            }
            catch (DomainProbeException error) when (error.Code == "gateway_certificate_not_served" && error.Target == origin)
            { check(true, "Old certificate was rejected."); }
            try
            {
                await DomainConnectivity.VerifyGateway(origin.Replace("localhost", "wrong-name.invalid", StringComparison.Ordinal),
                    "127.0.0.1", "/health/live", trust, false, _ => true, default, fingerprint);
                throw new InvalidOperationException("Fingerprint pinning bypassed hostname validation.");
            }
            catch (DomainProbeException error) when (error.Code == "gateway_certificate_name")
            { check(true, "Hostname validation remains enabled."); }
            try
            {
                await DomainConnectivity.VerifyGateway(origin, "127.0.0.1", "/missing", trust, false, _ => true, default, fingerprint);
                throw new InvalidOperationException("A missing route was accepted.");
            }
            catch (DomainProbeException error) when (error.Code == "gateway_http_error" && error.StatusCode == 404)
            { check(true, "HTTP status was retained."); }
            try
            {
                await DomainConnectivity.VerifyGateway(origin, "127.0.0.1", "/health/live", trust, false, _ => false, default, fingerprint);
                throw new InvalidOperationException("A wrong service identity was accepted.");
            }
            catch (DomainProbeException error) when (error.Code == "gateway_response_invalid")
            { check(true, "Service identity failure was retained."); }
            await app.StopAsync();
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
