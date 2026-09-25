using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lucia.Homelab.Server.Domains;

/// <summary>Stages only domain.yml; publication is not verified activation and never replaces the private gateway CA.</summary>
public static class DomainIngressConfiguration
{
    public const string FileName = "domain.yml";
    public static string Build(DomainNamingPlan plan, string publishedCertificatesRoot, string certificatePath, string privateKeyPath)
    {
        var zone = DomainNames.Hostname(plan.Domain);
        var lucia = DomainNames.ServiceHost(plan.ServiceUrls.Lucia, zone);
        var authentik = DomainNames.ServiceHost(plan.ServiceUrls.Authentik, zone);
        var spark = DomainNames.ServiceHost(plan.ServiceUrls.Spark, zone);
        if (new[] { lucia, authentik, spark }.Distinct(StringComparer.Ordinal).Count() != 3)
            throw new ArgumentException("Domain ingress requires three distinct service hostnames.");
        var certificate = GatewayCertificatePath(publishedCertificatesRoot, certificatePath);
        var key = GatewayCertificatePath(publishedCertificatesRoot, privateKeyPath);
        if (certificate == key) throw new ArgumentException("Certificate and private key paths must differ.");
        return JsonSerializer.Serialize(new
        {
            http = new
            {
                routers = new Dictionary<string, object>
                {
                    ["domain-lucia"] = new { entryPoints = new[] { "host" }, rule = $"Host(`{lucia}`)", service = "domain-lucia", tls = new { } },
                    ["domain-authentik"] = new { entryPoints = new[] { "host" }, rule = $"Host(`{authentik}`)", service = "domain-authentik", tls = new { } },
                    ["domain-spark"] = new { entryPoints = new[] { "host" }, rule = $"Host(`{spark}`)", service = "noop@internal",
                        middlewares = new[] { "domain-spark-redirect" }, tls = new { } }
                },
                services = new Dictionary<string, object>
                {
                    ["domain-lucia"] = new { loadBalancer = new { servers = new[] { new { url = "http://lucia-host:8080" } } } },
                    ["domain-authentik"] = new { loadBalancer = new { servers = new[] { new { url = "http://identity-server:9000" } } } }
                },
                middlewares = new Dictionary<string, object>
                {
                    ["domain-spark-redirect"] = new { redirectRegex = new
                    {
                        regex = "^https://" + Regex.Escape(spark) + "(:443)?(/.*)?$",
                        replacement = "https://" + lucia + "/", permanent = false
                    } }
                }
            },
            tls = new { certificates = new[] { new { certFile = certificate, keyFile = key } } }
        });
    }

    public static void Publish(string gatewayDirectory, DomainNamingPlan plan, string publishedCertificatesRoot,
        string certificatePath, string privateKeyPath)
    {
        var json = Build(plan, publishedCertificatesRoot, certificatePath, privateKeyPath);
        if (!Path.IsPathFullyQualified(gatewayDirectory))
            throw new ArgumentException("The mounted domain gateway directory must be absolute.");
        DomainActivationConfiguration.RejectLinks(gatewayDirectory);
        Directory.CreateDirectory(gatewayDirectory);
        var target = Path.Combine(gatewayDirectory, FileName);
        DomainActivationConfiguration.RejectLinks(target);
        var pending = Path.Combine(gatewayDirectory, $".domain-{Guid.NewGuid():N}.pending");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(pending, options))
            {
                stream.Write(System.Text.Encoding.UTF8.GetBytes(json));
                stream.Flush(flushToDisk: true);
            }
            DomainActivationConfiguration.RejectLinks(target);
            File.Move(pending, target, overwrite: true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    private static string GatewayCertificatePath(string root, string path)
    {
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Published certificate paths must be absolute.");
        DomainActivationConfiguration.RejectLinks(root);
        DomainActivationConfiguration.RejectLinks(path);
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        // Fixed portable path grammar also excludes traversal, ADS, URI syntax and
        // accidental references to Certbot's live symlinks instead of copied versions.
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => !Regex.IsMatch(part, @"\A[a-zA-Z0-9_-][a-zA-Z0-9_.-]*\z"))
            || !File.Exists(path))
            throw new ArgumentException("Certificates must be existing regular files beneath the published certificate root.");
        return "/domain-certificates/" + relative.Replace('\\', '/');
    }
}
