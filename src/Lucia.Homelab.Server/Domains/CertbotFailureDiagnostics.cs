using System.Globalization;
using System.Text.RegularExpressions;

namespace Lucia.Homelab.Server.Domains;

internal static class CertbotFailureDiagnostics
{
    internal static string Read(string logs, string lineage, DateTimeOffset? since = null, DateTimeOffset? until = null)
    {
        var path = Path.Combine(logs, "letsencrypt.log");
        CertbotFiles.RejectLinks(path);
        if (!File.Exists(path)) return "certbot_no_diagnostic";
        var text = CertbotFiles.ReadBounded(path, 1024 * 1024);
        if (!CertbotCertificateService.IsLineage(lineage) || !text.Contains(lineage, StringComparison.Ordinal))
            return "certbot_no_diagnostic";
        if (since is not null || until is not null)
        {
            var match = Regex.Match(text, @"(?m)^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2},\d{3})");
            if (!match.Success || !DateTimeOffset.TryParseExact(match.Groups[1].Value,
                    "yyyy-MM-dd HH:mm:ss,fff", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var timestamp)
                || (since is { } earliest && timestamp < earliest.AddSeconds(-5))
                || (until is { } latest && timestamp > latest.AddSeconds(5)))
                return "certbot_no_diagnostic";
        }
        return Classify(text);
    }

    // Only fixed identifiers leave this boundary, never raw log text or provider messages.
    internal static string Classify(string text)
    {
        if (text.Contains("exists, but it should be owned by current user with permissions 0o755", StringComparison.Ordinal))
            return "certbot_directory_mode";
        if (text.Contains("No space left on device", StringComparison.OrdinalIgnoreCase)) return "certbot_disk_full";
        if (text.Contains("urn:ietf:params:acme:error:rateLimited", StringComparison.OrdinalIgnoreCase))
            return "acme_rate_limit";
        if (text.Contains("Invalid API Token", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Authentication error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Invalid request headers", StringComparison.OrdinalIgnoreCase))
            return "cloudflare_access_denied";
        if (text.Contains("Incorrect TXT record", StringComparison.OrdinalIgnoreCase)
            || text.Contains("NXDOMAIN looking up TXT", StringComparison.OrdinalIgnoreCase)
            || text.Contains("No TXT record found", StringComparison.OrdinalIgnoreCase))
            return "dns_challenge_not_visible";
        if (text.Contains("The requested dns-cloudflare plugin does not appear to be installed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("No module named 'certbot_dns_cloudflare'", StringComparison.Ordinal))
            return "certbot_plugin_missing";
        if (text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) return "certbot_storage_denied";
        return "certbot_unknown";
    }
}
