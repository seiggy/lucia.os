using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lucia.Homelab.Server.Domains;

/// <summary>Stages only domain.yml; publication is not verified activation and never replaces the private gateway CA.</summary>
public static class DomainIngressConfiguration
{
    public const string FileName = "domain.yml";
    /// <summary>The gateway's entrypoint for traffic from the internet, which only public routes join.</summary>
    public const string PublicEntryPoint = "public";
    /// <summary>
    /// Cloudflare's proxy addresses (https://www.cloudflare.com/ips-v4). Public records are proxied, so the public entrypoint
    /// takes connections only from these. The gateway's static configuration also trusts their X-Forwarded-For.
    /// </summary>
    public static readonly string[] CloudflareRanges =
    [
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18", "108.162.192.0/18",
        "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
        "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
    ];

    /// <summary>
    /// Every entrypoint runs the always-present <c>forwarded-strip@file</c> middleware (routes.yml), and Traefik already
    /// replaces an untrusted client's X-Forwarded-*. The public entrypoint trusts Cloudflare, so only Cloudflare's own
    /// X-Forwarded-For and X-Forwarded-Proto survive there: these headers pass through Cloudflare from the visitor and are
    /// removed, and the route pins the real host and port.
    /// </summary>
    internal static readonly string[] VisitorHeaders =
    [
        "Forwarded", "X-Forwarded-Host", "X-Forwarded-Port", "X-Forwarded-Server", "X-Forwarded-Prefix", "X-Forwarded-Uri",
        "X-Forwarded-Method", "X-Forwarded-Ssl", "X-Forwarded-Scheme", "X-Forwarded-Tls-Client-Cert", "X-Forwarded-Tls-Client-Cert-Info",
        "X-Real-Ip", "X-Url-Scheme", "X-Original-Url", "X-Rewrite-Url",
    ];

    /// <summary>A public route's header middleware: visitor-supplied proxy headers removed, the real host and port set.</summary>
    internal static object PublicHeaders(string host)
    {
        var headers = VisitorHeaders.ToDictionary(name => name, _ => "");
        headers["X-Forwarded-Host"] = host;
        headers["X-Forwarded-Port"] = "443";
        return new { headers = new { customRequestHeaders = headers } };
    }

    /// <summary>The middleware name for a public route's headers.</summary>
    internal static string PublicHeadersName(string router) => router + "-forwarded";

    public static string Build(DomainNamingPlan plan, string publishedCertificatesRoot, string certificatePath, string privateKeyPath,
        bool publicAccess = false)
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
        var middlewares = new Dictionary<string, object>
        {
            ["domain-spark-redirect"] = new { redirectRegex = new
            {
                regex = "^https://" + Regex.Escape(spark) + "(:443)?(/.*)?$",
                replacement = "https://" + lucia + "/", permanent = false
            } }
        };
        if (publicAccess)
        {
            middlewares["public-sources"] = new { ipAllowList = new { sourceRange = CloudflareRanges } };
            middlewares[PublicHeadersName("domain-authentik")] = PublicHeaders(authentik);
        }
        return JsonSerializer.Serialize(new
        {
            http = new
            {
                routers = new Dictionary<string, object>
                {
                    ["domain-lucia"] = new { entryPoints = new[] { "host" }, rule = $"Host(`{lucia}`)", service = "domain-lucia", tls = new { } },
                    ["domain-authentik"] = publicAccess
                        ? new { entryPoints = new[] { "host", PublicEntryPoint }, rule = $"Host(`{authentik}`)", service = "domain-authentik",
                            middlewares = new[] { PublicHeadersName("domain-authentik") }, tls = new { } }
                        : (object)new { entryPoints = new[] { "host" }, rule = $"Host(`{authentik}`)", service = "domain-authentik", tls = new { } },
                    ["domain-spark"] = new { entryPoints = new[] { "host" }, rule = $"Host(`{spark}`)", service = "noop@internal",
                        middlewares = new[] { "domain-spark-redirect" }, tls = new { } }
                },
                services = new Dictionary<string, object>
                {
                    ["domain-lucia"] = new { loadBalancer = new { servers = new[] { new { url = "http://lucia-host:8080" } } } },
                    ["domain-authentik"] = new { loadBalancer = new { servers = new[] { new { url = "http://identity-server:9000" } } } }
                },
                middlewares
            },
            tls = new { certificates = new[] { new { certFile = certificate, keyFile = key } } }
        });
    }

    public static void Publish(string gatewayDirectory, DomainNamingPlan plan, string publishedCertificatesRoot,
        string certificatePath, string privateKeyPath, bool publicAccess = false)
    {
        var json = Build(plan, publishedCertificatesRoot, certificatePath, privateKeyPath, publicAccess);
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
            NotifyGateway(gatewayDirectory);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    /// <summary>
    /// The gateway watches only its top-level configuration directory, which holds this one. Touching this directory is an
    /// event in the watched one, so the gateway reloads the files inside it.
    /// </summary>
    internal static void NotifyGateway(string gatewayDirectory) => Directory.SetLastWriteTimeUtc(gatewayDirectory, DateTime.UtcNow);

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
