using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Lucia.Homelab.Server.Domains;

public sealed record DomainServiceUrls(string Lucia, string Authentik, string Spark);
public sealed record DomainNamingRequest(string Domain, string Subdomain, string SparkName, DomainServiceUrls? ServiceUrls = null);
public sealed record DomainNamingPlan(
    string Domain, string Namespace, string SparkName, DomainServiceUrls ServiceUrls,
    string[] CertificateNames, string[] LocalHostnames);

public static class DomainNames
{
    public static DomainNamingPlan Plan(DomainNamingRequest request, string verifiedZoneName)
    {
        var zone = Hostname(verifiedZoneName);
        var domain = Hostname(request.Domain);
        if (domain != zone) throw new ArgumentException("Select the domain from a verified Cloudflare zone.");
        var subdomain = Hostname(request.Subdomain);
        var ns = Hostname(subdomain + "." + domain);
        var sparkName = Label(request.SparkName);
        var urls = request.ServiceUrls ?? new(
            $"https://lucia.{ns}", $"https://auth.{ns}", $"https://{sparkName}.{ns}");
        var hosts = new[] { ServiceHost(urls.Lucia, zone), ServiceHost(urls.Authentik, zone), ServiceHost(urls.Spark, zone) };
        if (hosts.Distinct(StringComparer.Ordinal).Count() != hosts.Length)
            throw new ArgumentException("Lucia, Authentik, and the Spark must have distinct hostnames.");
        var normalized = new DomainServiceUrls("https://" + hosts[0], "https://" + hosts[1], "https://" + hosts[2]);
        var certificateNames = new List<string> { ns, "*." + ns };
        foreach (var host in hosts)
        {
            var prefix = host.EndsWith("." + ns, StringComparison.Ordinal) ? host[..^(ns.Length + 1)] : null;
            if (host != ns && (prefix is null || prefix.Contains('.')))
                certificateNames.Add(host);
        }
        return new(domain, ns, sparkName, normalized, certificateNames.Distinct(StringComparer.Ordinal).ToArray(), hosts);
    }

    /// <summary>
    /// The naming with public access on or off. Public access moves Authentik to <c>auth.&lt;zone&gt;</c>, so sign-in works
    /// from anywhere, and adds <c>*.&lt;zone&gt;</c> to the certificate for public app names. Off puts it back under the namespace.
    /// </summary>
    public static DomainNamingPlan WithPublic(DomainNamingPlan naming, bool enabled)
    {
        var zone = naming.Domain;
        var plan = Plan(new(zone, naming.Namespace[..^(zone.Length + 1)], naming.SparkName,
            naming.ServiceUrls with { Authentik = enabled ? $"https://auth.{zone}" : $"https://auth.{naming.Namespace}" }), zone);
        if (!enabled) return plan;
        bool covered(string name) => !name.StartsWith('*') && name.EndsWith("." + zone, StringComparison.Ordinal)
            && !name[..^(zone.Length + 1)].Contains('.');
        return plan with { CertificateNames = [.. plan.CertificateNames.Where(name => !covered(name)), "*." + zone] };
    }

    public static string ServiceHost(string url, string zone)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString()
            || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Service URLs must be HTTPS origins without paths, credentials, or custom ports.");
        var host = Hostname(uri.IdnHost);
        if (host != zone && !host.EndsWith("." + zone, StringComparison.Ordinal))
            throw new ArgumentException("Each service hostname must belong to the selected Cloudflare zone.");
        return host;
    }

    public static string Hostname(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 253 || value != value.Trim()
            || value.Contains('*') || value.EndsWith('.') || value.Any(char.IsControl))
            throw new ArgumentException("Enter a DNS name without wildcard characters, a URL scheme, or a trailing dot.");
        string host;
        try { host = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(value).ToLowerInvariant(); }
        catch (ArgumentException) { throw new ArgumentException("The DNS name is invalid."); }
        if (host.Length > 253 || IPAddress.TryParse(host, out _) || host.Split('.').Any(label => !ValidLabel(label)))
            throw new ArgumentException("The DNS name contains an invalid label.");
        return host;
    }

    public static string Label(string value)
    {
        var label = Hostname(value);
        if (label.Contains('.')) throw new ArgumentException("Use a single DNS label for the Spark's name.");
        return label;
    }

    private static bool ValidLabel(string value) =>
        Regex.IsMatch(value, @"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z", RegexOptions.CultureInvariant);
}
