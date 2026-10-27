using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Security;
using Lucia.Homelab.Server.Stacks;
using Lucia.Homelab.Server.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// CrowdSec's alerts in Observability (the engine's notifications, Loki's labels and the dashboards) and the response the
/// owner and the assistant share: bans, alert queries and custom AppSec rules, with every guardrail.
/// </summary>
internal static class CrowdSecResponseChecks
{
    internal static void Run(Action<bool, string> check, string[] cloudflare)
    {
        NotificationChecks(check, cloudflare);
        BanGuardChecks(check, cloudflare);
        RuleChecks(check);
        DashboardChecks(check);
    }

    private static void NotificationChecks(Action<bool, string> check, string[] cloudflare)
    {
        var app = StackCatalog.Find(CrowdSecApp.AppId);
        var node = new Lucia.Homelab.Server.Nodes.ManagedNodeFacts(Guid.NewGuid(), "lucialab01", true, null);
        var compose = app.Render(StackCatalog.Settings(app, null), node, new Dictionary<string, string>()).Compose;
        var sidecar = compose[compose.IndexOf("  lucia:\n", StringComparison.Ordinal)..compose.IndexOf("  crowdsec:\n", StringComparison.Ordinal)];
        var engine = compose[compose.IndexOf("  crowdsec:\n", StringComparison.Ordinal)..compose.IndexOf("\nconfigs:\n", StringComparison.Ordinal)];
        check(app.Version == 3 && !app.Telemetry, "CrowdSec is catalog version 3; its alerts go to Observability's TLS alert intake on the LAN, not through the gateway's telemetry address.");
        check(sidecar.Contains("      APPSEC_RULES: ${LUCIA_CROWDSEC_APPSEC_RULES:-}\n", StringComparison.Ordinal)
            && sidecar.Contains("      OTLP_ENDPOINT: ${LUCIA_CROWDSEC_OTLP_ENDPOINT:-}\n", StringComparison.Ordinal)
            && sidecar.Contains("      OTLP_AUTHORIZATION: ${LUCIA_CROWDSEC_OTLP_AUTHORIZATION:-}\n", StringComparison.Ordinal)
            && !engine.Contains("OTLP", StringComparison.Ordinal) && !engine.Contains("APPSEC_RULES", StringComparison.Ordinal),
            "The custom rules and the collector's address reach only the sidecar, so changing them never recreates the engine.");
        check(N(CrowdSecApp.Sidecar).IndexOf("appsec-rules.yaml", StringComparison.Ordinal) < N(CrowdSecApp.Sidecar).IndexOf("put tls.sha256", StringComparison.Ordinal)
            && N(CrowdSecApp.Sidecar).IndexOf("put otlp-authorization", StringComparison.Ordinal) < N(CrowdSecApp.Sidecar).IndexOf("put tls.sha256", StringComparison.Ordinal)
            && N(CrowdSecApp.Sidecar).Contains("printf '%s' \"$APPSEC_RULES\" | base64 -d", StringComparison.Ordinal),
            "The sidecar writes the rules and the collector before the certificate's marker, so the engine starts with them.");
        check(N(CrowdSecApp.Settings).Contains("  notification_dir: /tmp/lucia/notifications/", StringComparison.Ordinal)
            && N(CrowdSecApp.Settings).Contains("    profiles_path: /tmp/lucia/profiles.yaml", StringComparison.Ordinal)
            && N(CrowdSecApp.Settings).Contains("plugin_config:\n  user: nobody\n  group: nobody", StringComparison.Ordinal)
            && engine.Contains("    cap_drop: [ALL]\n", StringComparison.Ordinal) && engine.Contains("    cap_add: [SETUID, SETGID]\n", StringComparison.Ordinal)
            && engine.Contains("    group_add: [\"65534\"]\n", StringComparison.Ordinal) && engine.Contains("      PLUGIN_UNIX_SOCKET_GROUP: nobody\n", StringComparison.Ordinal)
            && engine.Contains("    security_opt: [\"no-new-privileges:true\"]\n", StringComparison.Ordinal)
            && N(CrowdSecApp.Settings).Contains("  level: full", StringComparison.Ordinal) && !N(CrowdSecApp.Settings).Contains("aggregated", StringComparison.Ordinal),
            "Notifications live in memory and the plugin runs as nobody (the engine keeps only SETUID and SETGID to start it); full metrics add active decisions and alerts.");
        check(N(CrowdSecApp.Start).Contains("install -m 0644 /lucia/config/profiles.yaml \"$run/.profiles.yaml\"", StringComparison.Ordinal)
            && N(CrowdSecApp.Start).Contains("install -m 0644 /lucia/config/profiles-quiet.yaml \"$run/.profiles.yaml\"", StringComparison.Ordinal)
            && N(CrowdSecApp.Start).Contains("yq '.url = strenv(URL) | .headers.Authorization = strenv(AUTH)'", StringComparison.Ordinal)
            && N(CrowdSecApp.Start).Contains("grep -Eqx 'Basic [A-Za-z0-9+/=]+'", StringComparison.Ordinal)
            && N(CrowdSecApp.Start).Contains("grep -Eqx 'https://[A-Za-z0-9.-]+(:[0-9]{1,5})?'", StringComparison.Ordinal)
            && N(CrowdSecApp.Start).Contains("install -m 0644 \"$state/ca.crt\" /tmp/lucia-ca/.ca.crt", StringComparison.Ordinal)
            && CrowdSecApp.Notifications.Split("ca_cert_path: /tmp/lucia-ca/ca.crt\n").Length == CrowdSecApp.AlertClasses.Length + 1
            && N(CrowdSecApp.Start).Contains("if [ \"$reload\" = 1 ]; then kill -HUP \"$main\"; fi", StringComparison.Ordinal)
            && N(CrowdSecApp.Start).Split("kill -HUP \"$main\"").Length == 3,
            "The engine picks the profiles with or without notifications, fills in the collector only after checking it, and reloads once for any change.");
        check(N(CrowdSecApp.Start).Contains("grep -Eqx 'lucia/custom-[0-9a-f]{8}'", StringComparison.Ordinal)
            && N(CrowdSecApp.Start).Contains("rm -f \"$dir\"/lucia-custom-*.yaml", StringComparison.Ordinal)
            && N(CrowdSecApp.Start).Contains("/lucia/config/lucia-placeholder.yaml /etc/crowdsec/appsec-rules/lucia-placeholder.yaml", StringComparison.Ordinal)
            && N(CrowdSecApp.Start).Contains("/lucia/config/lucia-appsec.yaml /etc/crowdsec/appsec-configs/lucia-custom.yaml", StringComparison.Ordinal)
            && N(CrowdSecApp.Acquisition).Contains("  - crowdsecurity/appsec-default\n  - lucia/custom\n", StringComparison.Ordinal)
            && N(CrowdSecApp.AppSecConfig).Contains("  - lucia/custom-*", StringComparison.Ordinal)
            && N(CrowdSecApp.AppSecPlaceholder).Contains("name: lucia/custom-none", StringComparison.Ordinal)
            && N(CrowdSecApp.AppSecPlaceholder).Contains("value: \"lucia:never-matches\"", StringComparison.Ordinal),
            "Custom rules load as lucia/custom-<id> files beside an always-present rule that matches nothing (CrowdSec refuses an empty pattern), and only Lucia's names are written.");
        var loud = N(CrowdSecApp.Profiles(cloudflare, notify: true));
        var quiet = N(CrowdSecApp.Profiles(cloudflare, notify: false));
        check(CrowdSecApp.AlertClasses.All(type => loud.Contains($"notifications:\n  - lucia_alerts_{type}\non_success: break", StringComparison.Ordinal))
            && !quiet.Contains("notifications", StringComparison.Ordinal) && !quiet.Contains("lucia_public_recorded", StringComparison.Ordinal)
            && loud.IndexOf("lucia_public_escalating_ban", StringComparison.Ordinal) < loud.IndexOf("lucia_public_recorded", StringComparison.Ordinal)
            && loud.IndexOf("lucia_public_recorded", StringComparison.Ordinal) < loud.IndexOf("lucia_other_recorded", StringComparison.Ordinal)
            && loud.Split("decisions:").Length == 2 && quiet.Split("decisions:").Length == 2
            && loud.Contains("name: lucia_cloudflare_never_banned", StringComparison.Ordinal),
            "Every profile sends its alerts to Observability by class, two last profiles catch alerts nothing else matched (an AppSec block from the internet), and only the public profile bans.");
        var notifications = CrowdSecApp.Notifications;
        var documents = notifications.Split("---\n");
        check(documents.Length == CrowdSecApp.AlertClasses.Length
            && CrowdSecApp.AlertClasses.All(type => notifications.Contains($"name: lucia_alerts_{type}\n", StringComparison.Ordinal)
                && notifications.Contains($"{{\"key\":\"crowdsec.source_class\",\"value\":{{\"stringValue\":\"{type}\"}}}}", StringComparison.Ordinal))
            && documents.All(document => document.Contains("type: http\n", StringComparison.Ordinal) && document.Contains("url: \"\"\n", StringComparison.Ordinal)
                && document.Contains("  Authorization: \"\"\n", StringComparison.Ordinal) && document.Contains("max_retry: 1\n", StringComparison.Ordinal)
                && document.Contains("timeout: 10s\n", StringComparison.Ordinal) && document.Contains("format: |\n  {\"resourceLogs\":[", StringComparison.Ordinal))
            && !notifications.Contains("LUCIA_CLASS", StringComparison.Ordinal),
            "One http notification per class posts OTLP/JSON with an empty address and credentials the engine fills in, one retry and a short timeout.");
        var template = N(CrowdSecApp.AlertTemplate);
        check(template.Contains("{\"key\":\"service.name\",\"value\":{\"stringValue\":\"crowdsec\"}},{\"key\":\"crowdsec.kind\"", StringComparison.Ordinal)
            && new[] { "source.address", "crowdsec.scenario", "crowdsec.rule", "crowdsec.decision", "crowdsec.duration", "crowdsec.origin", "url.path",
                "http.request.method", "user_agent.original", "server.address", "source.geo.country_iso_code", "source.as.organization.name" }
                .All(key => template.Contains($"{{\"key\":\"{key}\",\"value\":", StringComparison.Ordinal))
            && template.Contains("(toDate \"2006-01-02T15:04:05Z07:00\" $stop).UnixNano", StringComparison.Ordinal)
            && template.Contains("kindIs \"slice\" $list", StringComparison.Ordinal),
            "Each alert becomes one log record: service and kind on the resource (Loki's only labels), the rest as attributes, timed when the alert ended.");
        var lan = new TelemetryDestination(new Uri("https://otlp-alerts.lab.example.com:4319"), "Basic bHVjaWE6cGFzc3dvcmQ=");
        var lines = StackCatalog.ReadEnv(CrowdSec.Lines(null, "ROOT", [], null, null, null, "name: lucia/custom-0011aabb\n", lan));
        check(lines["LUCIA_CROWDSEC_OTLP_ENDPOINT"] == "https://otlp-alerts.lab.example.com:4319" && lines["LUCIA_CROWDSEC_OTLP_AUTHORIZATION"] == lan.Authorization
            && Encoding.UTF8.GetString(Convert.FromBase64String(lines["LUCIA_CROWDSEC_APPSEC_RULES"])) == "name: lucia/custom-0011aabb\n"
            && StackCatalog.ReadEnv(CrowdSec.Lines(null, "ROOT", [], null, null, null)) is var none && none["LUCIA_CROWDSEC_APPSEC_RULES"] == ""
            && !none.ContainsKey("LUCIA_CROWDSEC_OTLP_ENDPOINT")
            && !CrowdSec.Lines(null, "ROOT", [], null, null, null, "", lan with { Authorization = "Basic x\nEVIL=1" }).Contains("EVIL", StringComparison.Ordinal),
            "The engine's variables carry the rules (one base64 line) and the collector only with plain credentials; without Observability there's none.");
        check(StackStore.ManagedSecrets(new StoredStack("guard", "", "", new StackManifest(1, new()), "Running", 1, 0, 0, DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch, "owner", "lucialab01"), "LUCIA_CROWDSEC_OTLP_AUTHORIZATION=Basic x\nLUCIA_CROWDSEC_APPSEC_RULES=\nLUCIA_CROWDSEC_INTAKE='{tls: {key_pem: \"k\"}}'\n")
                .SetEquals(["LUCIA_CROWDSEC_OTLP_AUTHORIZATION", "LUCIA_CROWDSEC_INTAKE"]),
            "The stack API shows the collector's credentials and the alert intake's key by name only.");
        IntakeChecks(check);
        check(CrowdSecApp.ConsoleLine("https://lucia.lab.example.com") == "LUCIA_CROWDSEC_CONSOLE=https://lucia.lab.example.com\n"
            && CrowdSecApp.ConsoleLine("https://lucia.lab.example.com/") == "LUCIA_CROWDSEC_CONSOLE=https://lucia.lab.example.com\n"
            && new[] { null, "http://lucia.lab.example.com", "https://lucia.lab.example.com/x", "https://a b", "https://x\nEVIL=1", "https://${HOME}" }
                .All(origin => CrowdSecApp.ConsoleLine(origin) == ""),
            "Grafana's link to Gateway protection is only ever a plain https origin.");
        var status = """lucia-status {"v":1,"at":"2026-10-27T16:00:00Z","version":"v1.8.1","harbor":3,"rules":2}""";
        check(CrowdSec.ParseReport(status) is { Harbor: 3, Rules: 2 } && CrowdSec.ParseReport(status.Replace("\"rules\":2", "\"rules\":-1", StringComparison.Ordinal)) is { Rules: null }
            && CrowdSec.ParseReport(status.Replace(",\"rules\":2", "", StringComparison.Ordinal)) is { Rules: null }
            && N(CrowdSecApp.Start).Contains("\"rules\": (strenv(RULES) | to_number)", StringComparison.Ordinal),
            "The engine reports how many custom rules it loaded; an older engine reports none.");
    }

    private static void BanGuardChecks(Action<bool, string> check, string[] cloudflare)
    {
        foreach (var (input, value, scope) in new[]
        {
            ("203.0.113.7", "203.0.113.7", "Ip"), (" 203.0.113.7 ", "203.0.113.7", "Ip"), ("203.0.113.7/32", "203.0.113.7", "Ip"),
            ("203.0.113.0/24", "203.0.113.0/24", "Range"), ("198.51.0.0/16", "198.51.0.0/16", "Range"), ("2001:db8:1::/48", "2001:db8:1::/48", "Range"),
            ("2001:db8::7", "2001:db8::7", "Ip"), ("192.168.1.77", "192.168.1.77", "Ip"),
        })
            check(CrowdSecGuard.Target(input) is var target && target.Value == value && target.Scope == scope, $"A ban must accept {input} as {scope} {value}.");
        foreach (var input in new[]
        {
            "", "not-an-ip", "203.0.113.5/24", "203.0.0.0/15", "0.0.0.0/0", "::/0", "2001:db8::/47", "127.0.0.1", "::1", "0.0.0.0", "224.0.0.1",
            "255.255.255.255", "ff02::1", "::ffff:203.0.113.7", "fe80::1%eth0", new string('1', 65),
        })
            check(Throws(() => CrowdSecGuard.Target(input), 400), $"A ban must refuse {input}: not one address or a /16 (/48) at most.");
        foreach (var (input, minutes) in new[] { ("1m", 1), ("90m", 90), ("4h", 240), ("1h30m", 90), ("7d", 10080), ("30d", 43200), ("2D", 2880) })
            check(CrowdSecGuard.Duration(input) == TimeSpan.FromMinutes(minutes) && CrowdSecGuard.Format(TimeSpan.FromMinutes(minutes)) == $"{minutes}m",
                $"A ban of {input} lasts {minutes} minutes.");
        foreach (var input in new[] { "", "0m", "30s", "31d", "720h1m", "4", "4x", "-1h", "1h 30m", "99999999999d", null })
            check(Throws(() => CrowdSecGuard.Duration(input), 400), $"A ban of '{input}' must be refused: from a minute to 30 days.");
        check(CrowdSecGuard.Reason("  probing  ") == "probing" && Throws(() => CrowdSecGuard.Reason(""), 400) && Throws(() => CrowdSecGuard.Reason("   "), 400)
            && Throws(() => CrowdSecGuard.Reason(new string('a', 201)), 400) && Throws(() => CrowdSecGuard.Reason("line\nINJECTED"), 400)
            && CrowdSecGuard.Reason(new string('a', 200)).Length == 200,
            "Every ban needs a reason of one line, up to 200 characters.");

        (IPNetwork, string)[] protectedRanges =
        [
            (IPNetwork.Parse("127.0.0.0/8"), "Lucia's own"), (IPNetwork.Parse("172.24.0.0/16"), "Lucia's own"), (IPNetwork.Parse("192.168.1.10/32"), "Lucia's own"),
            (IPNetwork.Parse("192.168.1.50/32"), "in the safe harbor"), (IPNetwork.Parse("198.51.100.0/24"), "in the safe harbor"),
            .. cloudflare.Concat(CrowdSecGuard.CloudflareV6).Select(range => (IPNetwork.Parse(range), "Cloudflare's")),
        ];
        foreach (var input in new[] { "192.168.1.10", "192.168.1.50", "192.168.1.0/24", "198.51.100.7", "198.51.0.0/16", "172.24.3.4", "104.16.0.1", "104.16.0.0/16",
                     "173.245.48.0/20", "2400:cb00::1", "2400:cb00::/48", "172.24.0.0/16" })
        {
            var (_, _, network) = CrowdSecGuard.Target(input);
            check(Throws(() => CrowdSecGuard.RefuseProtected(network, protectedRanges), 409),
                $"A ban on {input} must be refused: it is, contains or sits inside the safe harbor, Lucia's own addresses or Cloudflare.");
        }
        foreach (var input in new[] { "203.0.113.7", "192.168.1.77", "192.168.2.0/24", "198.51.101.0/24", "2001:db8::7" })
        {
            var (_, _, network) = CrowdSecGuard.Target(input);
            check(!Throws(() => CrowdSecGuard.RefuseProtected(network, protectedRanges), 409), $"A ban on {input} touches nothing Lucia trusts.");
        }
        var message = "";
        try { CrowdSecGuard.RefuseProtected(CrowdSecGuard.Target("192.168.1.0/24").Network, protectedRanges); }
        catch (HardwareOnboardingException error) { message = error.Message; }
        check(message == "Lucia never bans 192.168.1.0/24: it covers 192.168.1.10, which is Lucia's own.", "A refusal names what the ban would cover.");
    }

    private static void RuleChecks(Action<bool, string> check)
    {
        foreach (var (request, field, op, value) in new (CrowdSecRuleRequest, string, string, string)[]
        {
            (new("path", null, "prefix", "/wp-admin", "No WordPress"), "path", "prefix", "/wp-admin"),
            (new("PATH", null, "startsWith", "/cgi-bin/", "Old CGI scans"), "path", "prefix", "/cgi-bin/"),
            (new("path", null, "equals", "/.env", "Env files"), "path", "equals", "/.env"),
            (new("path", null, "contains", "/phpmyadmin", "Scanners"), "path", "contains", "/phpmyadmin"),
            (new("path", null, "regex", @"^/cgi-bin/.*\.pl$", "Perl CGI"), "path", "regex", @"^/cgi-bin/.*\.pl$"),
            (new("query", null, "contains", "UNION SELECT", "SQL injection"), "query", "contains", "UNION SELECT"),
            (new("ua", null, "contains", "sqlmap", "Scanner"), "user-agent", "contains", "sqlmap"),
            (new("user-agent", null, "regex", "^(nikto|masscan)/", "Scanners"), "user-agent", "regex", "^(nikto|masscan)/"),
            (new("header", "X-Api-Version", "equals", "0.0.1-evil", "Exploit header"), "header", "equals", "0.0.1-evil"),
            (new("header", "user-agent", "contains", "BadBot", "Bot"), "user-agent", "contains", "BadBot"),
            (new("method", null, "equals", "propfind", "No WebDAV"), "method", "equals", "PROPFIND"),
            (new("path", null, "regex", @"^/wp-(?:admin|login)\.php$", "WordPress"), "path", "regex", @"^/wp-(?:admin|login)\.php$"),
            (new("path", null, "regex", @"[A-Za-z0-9_\-]{8,}\.bak$", "Backups"), "path", "regex", @"[A-Za-z0-9_\-]{8,}\.bak$"),
            (new("path", null, "regex", @"^/cgi-bin/.*?\.(pl|sh)$", "CGI"), "path", "regex", @"^/cgi-bin/.*?\.(pl|sh)$"),
            (new("path", null, "regex", @"^/[^/]+/\.git/[\w.-]+", "Git"), "path", "regex", @"^/[^/]+/\.git/[\w.-]+"),
            (new("query", null, "regex", @"(?:union|UNION)\s+(?:all\s+)?select", "SQLi"), "query", "regex", @"(?:union|UNION)\s+(?:all\s+)?select"),
            (new("header", "X-Api-Version", "regex", @"^0\.0\.[0-9]{1,2}-evil$", "Exploit"), "header", "regex", @"^0\.0\.[0-9]{1,2}-evil$"),
            (new("path", null, "prefix", "/wp-", "Café scanner — blocked"), "path", "prefix", "/wp-"),
        })
        {
            var result = CrowdSecRules.Validate(request);
            check(result.Field == field && result.Operator == op && result.Value == value && result.Reason.Length > 0,
                $"A custom rule on {request.Field} {request.Operator} {request.Value} must be accepted as {field} {op} {value}.");
        }
        foreach (var (request, why) in new (CrowdSecRuleRequest, string)[]
        {
            (new("path", null, "prefix", "/", "x"), "/ matches every path"),
            (new("path", null, "contains", "/", "x"), "/ matches every path"),
            (new("path", null, "equals", "/", "x"), "/ is every app's front page"),
            (new("path", null, "regex", ".*", "x"), ".* matches everything"),
            (new("path", null, "regex", "^", "x"), "^ matches everything"),
            (new("path", null, "regex", "a*", "x"), "a* matches empty text"),
            (new("path", null, "regex", "(a+)+$", "x"), "a nested repeat"),
            (new("path", null, "regex", "(?=admin)", "x"), "a lookahead isn't RE2"),
            (new("path", null, "regex", @"(a)\1", "x"), "a backreference isn't RE2"),
            (new("path", null, "regex", "a{1000}", "x"), "a huge counted repeat"),
            (new("path", null, "regex", "[", "x"), "an invalid regex"),
            (new("path", null, "prefix", "/api", "x"), "Lucia's API"),
            (new("path", null, "prefix", "/api/host/", "x"), "Lucia's API"),
            (new("path", null, "contains", "index", "x"), "Lucia's console"),
            (new("path", null, "regex", "^/v1/", "x"), "OTLP and the access log's own path"),
            (new("path", null, "prefix", "wp-admin", "x"), "a path without /"),
            (new("path", null, "equals", "", "x"), "an empty pattern"),
            (new("path", null, "equals", "   ", "x"), "a blank pattern"),
            (new("path", null, "contains", "/a\"b", "x"), "a quote"),
            (new("path", null, "contains", "/%{REQUEST_URI}", "x"), "a macro"),
            (new("path", null, "contains", @"/a\b", "x"), "a backslash outside a regex"),
            (new("path", null, "contains", "/a\nb", "x"), "a control character"),
            (new("path", null, "contains", "/" + new string('a', 200), "x"), "a pattern over 200 characters"),
            (new("user-agent", null, "contains", "Mozilla", "x"), "every browser"),
            (new("user-agent", null, "contains", "OpenTelemetry", "x"), "Lucia's telemetry"),
            (new("user-agent", null, "prefix", "lucia-", "x"), "Lucia itself"),
            (new("user-agent", null, "regex", "Chrome|Firefox", "x"), "browsers"),
            (new("query", null, "regex", ".", "x"), "any query"),
            (new("header", "Host", "contains", "lab", "x"), "the Host header"),
            (new("header", null, "contains", "x", "x"), "a header rule without a name"),
            (new("header", "Bad Header", "contains", "x", "x"), "a header name with a space"),
            (new("path", "X-Thing", "prefix", "/wp-", "x"), "a header name on a path rule"),
            (new("method", null, "equals", "GET", "x"), "every GET"),
            (new("method", null, "equals", "POST", "x"), "every POST"),
            (new("method", null, "contains", "PROP", "x"), "a method rule that isn't equals"),
            (new("method", null, "equals", "GET /", "x"), "not a method"),
            (new("body", null, "contains", "x", "x"), "an unknown field"),
            (new("path", null, "like", "/wp-", "x"), "an unknown operator"),
            (new("path", null, "prefix", "/wp-", ""), "no reason"),
            (new("path", null, "prefix", "/wp-", new string('r', 201)), "a reason over 200 characters"),
            (new("path", null, "prefix", "/wp-", "two\nlines"), "a reason over two lines"),
            (new(null, null, null, null, null), "nothing"),
            // RE2 and .NET disagree here: .NET reads [[:print:]] as a class of [:print plus a literal ], RE2 as every printable character.
            (new("path", null, "regex", "[[:print:]]+", "x"), "a POSIX class, which matches everything in RE2"),
            (new("user-agent", null, "regex", "[[:alpha:]]", "x"), "a POSIX class on the user agent"),
            (new("path", null, "regex", "/[a-z-[aeiou]]+", "x"), ".NET class subtraction"),
            (new("path", null, "regex", "(?i)/ADMIN", "x"), "a flag group"),
            (new("path", null, "regex", "(?P<x>/a)", "x"), "a named group"),
            (new("path", null, "regex", "(?<x>/a)", "x"), "a .NET named group"),
            (new("path", null, "regex", @"/\p{L}+", "x"), "a Unicode class"),
            (new("path", null, "regex", @"/\x41", "x"), "a hex escape"),
            (new("path", null, "regex", @"/\Qa.b\E", "x"), "a quoted span"),
            (new("path", null, "regex", @"\A/a", "x"), "\\A"),
            (new("path", null, "regex", @"/a\z", "x"), "\\z"),
            (new("path", null, "regex", "/a{,5}", "x"), "a { that RE2 reads as text and .NET differently"),
            (new("path", null, "regex", "/a{2", "x"), "an unclosed counted repeat"),
            (new("path", null, "regex", "/a]", "x"), "a bare ]"),
            (new("path", null, "regex", "/a}", "x"), "a bare }"),
            (new("path", null, "regex", "/[]a]", "x"), "a class that starts with ]"),
            (new("path", null, "regex", "/[a-c-e]", "x"), "an ambiguous - in a class"),
            (new("path", null, "regex", "/[z-a]", "x"), "a backwards range"),
            (new("path", null, "regex", @"/[\d-z]", "x"), "a range from a class escape"),
            (new("path", null, "regex", "/a**", "x"), "a repeated repeat"),
            (new("path", null, "regex", "/a{2}{3}", "x"), "a repeated counted repeat"),
            (new("path", null, "regex", "^*/a", "x"), "a repeat of an anchor"),
            (new("path", null, "regex", "/(a", "x"), "an unclosed group"),
            (new("path", null, "regex", "/a)", "x"), "an unopened group"),
            (new("path", null, "regex", "/a{101}", "x"), "a counted repeat over 100"),
            (new("path", null, "regex", "/a{5,2}", "x"), "a counted repeat that runs backwards"),
            // Headers every request carries, and header rules that match ordinary values.
            (new("header", "Accept", "contains", "text/html", "x"), "Accept"),
            (new("header", "accept-language", "contains", "en", "x"), "Accept-Language"),
            (new("header", "Cookie", "contains", "session", "x"), "Cookie"),
            (new("header", "CF-Connecting-IP", "equals", "203.0.113.7", "x"), "Cloudflare's headers"),
            (new("header", "X-Forwarded-For", "contains", "203.0.113.7", "x"), "the gateway's forwarding headers"),
            (new("header", "X-Real-Ip", "equals", "203.0.113.7", "x"), "X-Real-Ip"),
            (new("header", "Connection", "equals", "close", "x"), "Connection"),
            (new("header", "Content-Type", "contains", "json", "x"), "Content-Type"),
            (new("header", "Sec-Fetch-Mode", "equals", "navigate", "x"), "the browser's Sec- headers"),
            (new("header", "Authorization", "prefix", "Basic", "x"), "Authorization, which Lucia's own clients send"),
            (new("header", "X-Custom", "regex", ".", "x"), "any header value"),
            (new("header", "X-Custom", "contains", "=", "x"), "a header value like a=b"),
            (new("header", "X-Custom", "regex", "^[0-9a-f-]+$", "x"), "a UUID"),
            (new("header", "X-Custom", "prefix", "gzip", "x"), "an encoding list"),
            (new("header", "X-Custom", "regex", @"^\d+\.\d+\.\d+\.\d+$", "x"), "an IP address"),
            (new("header", "X-Custom", "contains", "*/*", "x"), "an Accept value"),
            // Only printable ASCII patterns; reasons go to CrowdSec's YAML, which can't hold emoji.
            (new("path", null, "contains", "/\U0001F916", "x"), "an emoji in the pattern"),
            (new("path", null, "contains", "/café", "x"), "an accent in the pattern"),
            (new("path", null, "regex", "/caf[é]", "x"), "an accent in a regex class"),
            (new("path", null, "prefix", "/wp-", "Bots \U0001F916"), "an emoji in the reason"),
            (new("path", null, "prefix", "/wp-", "x\uFFFE"), "a non-character in the reason"),
        })
            check(Throws(() => CrowdSecRules.Validate(request), 400), $"A custom rule must be refused for {why}.");

        var rendered = CrowdSecRules.Render(
        [
            new("0011aabb", "lucia/custom-0011aabb", "path", null, "prefix", "/wp-admin", "No \"WordPress\" here", DateTimeOffset.UnixEpoch, "owner"),
            new("2233ccdd", "lucia/custom-2233ccdd", "header", "X-Api-Version", "regex", @"^0\.0\.", "Exploit", DateTimeOffset.UnixEpoch, "owner"),
            new("4455eeff", "lucia/custom-4455eeff", "user-agent", null, "contains", "sqlmap", "Scanner", DateTimeOffset.UnixEpoch, "owner"),
            new("66778899", "lucia/custom-66778899", "query", null, "equals", "drop-table", "SQLi", DateTimeOffset.UnixEpoch, "owner"),
            new("aabbccdd", "lucia/custom-aabbccdd", "method", null, "equals", "PROPFIND", "WebDAV", DateTimeOffset.UnixEpoch, "owner"),
        ]);
        var docs = rendered.Split("---\n");
        check(docs.Length == 5 && docs[0].StartsWith("name: lucia/custom-0011aabb\n", StringComparison.Ordinal)
            && docs[0].Contains("description: \"Lucia custom rule: No \\u0022WordPress\\u0022 here\"\n", StringComparison.Ordinal)
            && docs[0].Contains("  - zones:\n      - URI\n    match:\n      type: startsWith\n      value: \"/wp-admin\"\n", StringComparison.Ordinal)
            && docs[1].Contains("      - HEADERS\n    variables:\n      - \"X-Api-Version\"\n    match:\n      type: regex\n      value: \"^0\\\\.0\\\\.\"\n", StringComparison.Ordinal)
            && docs[2].Contains("      - HEADERS\n    variables:\n      - \"User-Agent\"\n    match:\n      type: contains\n", StringComparison.Ordinal)
            && docs[3].Contains("      - ARGS\n    match:\n      type: equals\n", StringComparison.Ordinal)
            && docs[4].Contains("      - METHOD\n    match:\n      type: equals\n      value: \"PROPFIND\"\n", StringComparison.Ordinal)
            && docs.All(doc => doc.Contains("labels:\n  type: exploit\n", StringComparison.Ordinal))
            && CrowdSecRules.Render([]) == "",
            "Rules render as CrowdSec appsec-rules, one document each, every string JSON-quoted; none render as nothing.");
        var accented = CrowdSecRules.Render([new("0011aabb", "lucia/custom-0011aabb", "path", null, "prefix", "/wp-admin", "Café scanner — blocked", DateTimeOffset.UnixEpoch, "owner")]);
        check(accented.Contains("description: \"Lucia custom rule: Caf\\u00E9 scanner \\u2014 blocked\"\n", StringComparison.Ordinal) && accented.All(ch => ch < 128),
            "A reason in any language renders as ASCII with single \\u escapes, which CrowdSec's YAML reader accepts.");
        check(CrowdSecRules.RegexProblem("[[:print:]]+") is { } posix && posix.Contains("[[:alpha:]]", StringComparison.Ordinal)
            && CrowdSecRules.RegexProblem(@"^/a(?:b|c)*?[x-z\]\-]{1,3}\.d$") is null,
            "The regex check names the construct it refuses and passes the RE2 syntax .NET reads the same way.");
        Re2Corpus();
    }

    /// <summary>The store behind the page and the assistant: the 50-rule limit, duplicates, removal and a tampered file.</summary>
    internal static async Task RulesStore(Action<bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "lucia-rules-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new CrowdSecRules(new GatewaySecurityOptions { StateDirectory = folder }, NullLogger<CrowdSecRules>.Instance);
            var changed = 0;
            store.Changed += () => changed++;
            check((await store.List(default)).Rules.Length == 0 && await store.Rendered(default) == "", "No saved rules means none.");
            var first = await store.Add(new("path", null, "prefix", "/wp-admin", "No WordPress"), "zack via assistant", DateTimeOffset.UnixEpoch, default);
            check(Regex.IsMatch(first.Id, "^[0-9a-f]{8}$") && first.Name == "lucia/custom-" + first.Id && first.CreatedBy == "zack via assistant" && changed == 1
                && (await store.Rendered(default)).StartsWith($"name: lucia/custom-{first.Id}\n", StringComparison.Ordinal),
                "A new rule gets an id, a CrowdSec name and its author, and the engine's copy follows.");
            check(await ThrowsAsync(() => store.Add(new("path", null, "prefix", "/wp-admin", "Again"), "owner", DateTimeOffset.UnixEpoch, default), 409),
                "The same rule can't be added twice.");
            for (var index = 1; index < CrowdSecRules.Max; index++)
                await store.Add(new("path", null, "prefix", $"/blocked-{index}", "Filler"), "owner", DateTimeOffset.UnixEpoch, default);
            check((await store.List(default)).Rules.Length == CrowdSecRules.Max
                && await ThrowsAsync(() => store.Add(new("path", null, "prefix", "/one-more", "Over"), "owner", DateTimeOffset.UnixEpoch, default), 409),
                "Lucia keeps at most 50 custom rules.");
            var removed = await store.Remove("lucia/custom-" + first.Id, "owner", default);
            check(removed.Id == first.Id && (await store.List(default)).Rules.All(rule => rule.Id != first.Id)
                && !(await store.Rendered(default)).Contains(first.Id, StringComparison.Ordinal),
                "A rule is removed by its id or its CrowdSec name.");
            check(await ThrowsAsync(() => store.Remove(first.Id, "owner", default), 404) && await ThrowsAsync(() => store.Remove("../../x", "owner", default), 400),
                "Removing an unknown or malformed id is refused.");
            var path = Path.Combine(folder, CrowdSecRules.StateFileName);
            var tampered = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            tampered["rules"]![0]!["value"] = "/";
            await File.WriteAllTextAsync(path, tampered.ToJsonString());
            check(await store.Rendered(default) == "" && await ThrowsAsync(() => store.List(default), null),
                "A saved rule that fails today's checks (edited by hand) gives the engine no rules and the page an error.");
            var unconfigured = new CrowdSecRules(new GatewaySecurityOptions(), NullLogger<CrowdSecRules>.Instance);
            check(await ThrowsAsync(() => unconfigured.Add(new("path", null, "prefix", "/wp-admin", "x"), "owner", DateTimeOffset.UnixEpoch, default), 409),
                "Without gateway security there's nowhere to keep rules.");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    internal static void Alerts(Action<bool, string> check, string[] cloudflare)
    {
        var (path, kind, range, take) = CrowdSecGuard.AlertsPath(new());
        check(path == "/v1/alerts?include_capi=false&limit=50" && kind is null && range is null && take == 50, "By default the newest 50 alerts, community blocklist excluded.");
        (path, kind, range, take) = CrowdSecGuard.AlertsPath(new("203.0.113.7", null, "crowdsecurity/http-probing", "waf", "6h", 500));
        check(path == "/v1/alerts?include_capi=false&limit=500&value=203.0.113.7&scenario=crowdsecurity%2Fhttp-probing&since=360m" && kind == "waf" && take == 200,
            "An address is matched by value (CrowdSec's ip filter misses AppSec alerts), kind is picked from the latest 500 and at most 200 come back.");
        (path, kind, range, _) = CrowdSecGuard.AlertsPath(new(null, "203.0.113.0/24", null, "scenario", null, 10));
        check(path == "/v1/alerts?include_capi=false&limit=500" && kind == "crowdsec" && range is { } network && network.ToString() == "203.0.113.0/24",
            "A network is matched here from the latest 500; the page's kind=scenario still means crowdsec.");
        check(CrowdSecGuard.AlertsPath(new(null, null, null, "appsec", "30d", 0)) is (_, "waf", null, 1) && CrowdSecGuard.AlertsPath(new(Since: "7d")).Path.EndsWith("&since=10080m", StringComparison.Ordinal),
            "kind=appsec means waf, and the limit is at least one.");
        foreach (var bad in new CrowdSecAlertQuery[]
        {
            new(Ip: "not-an-ip"), new(Ip: "fe80::1%eth0"), new(Range: "203.0.113.5/24"), new(Range: "203.0.113.5"), new(Scenario: "x y"), new(Scenario: "a&b=c"),
            new(Kind: "everything"), new(Since: "31d"), new(Since: "0m"), new(Since: "1w"), new(Since: "1h30m"),
        })
            check(Throws(() => CrowdSecGuard.AlertsPath(bad), 400), $"An alert query must be refused: {bad}.");

        var now = DateTimeOffset.Parse("2026-10-27T16:00:00Z");
        var body = JsonNode.Parse(CrowdSecLapi.BanAlert("203.0.113.0/24", "Range", TimeSpan.FromHours(4), "probing \"photos\"", "zack via assistant", CrowdSec.AssistantOrigin, now))!;
        var alert = body[0]!;
        var decision = alert["decisions"]![0]!;
        check(body.AsArray().Count == 1 && alert["scenario"]!.GetValue<string>() == "lucia/manual-ban" && alert["remediation"]!.GetValue<bool>()
            && alert["message"]!.GetValue<string>() == "probing \"photos\" (by zack via assistant)" && alert["start_at"]!.GetValue<string>() == "2026-10-27T16:00:00Z"
            && alert["source"]!["scope"]!.GetValue<string>() == "Range" && alert["source"]!["value"]!.GetValue<string>() == "203.0.113.0/24"
            && decision["type"]!.GetValue<string>() == "ban" && decision["duration"]!.GetValue<string>() == "240m" && decision["origin"]!.GetValue<string>() == "lucia-assistant"
            && decision["scope"]!.GetValue<string>() == "Range" && decision["value"]!.GetValue<string>() == "203.0.113.0/24"
            && alert["meta"]!.AsArray().Any(item => item!["key"]!.GetValue<string>() == "lucia_reason" && JsonNode.Parse(item["value"]!.GetValue<string>())![0]!.GetValue<string>() == "probing \"photos\""),
            "A ban is the alert cscli posts, with one ban decision, Lucia's scenario, the assistant's origin, the reason and who asked.");
        using (var detail = JsonDocument.Parse("""
            {"id":12,"kind":"crowdsec","scenario":"crowdsecurity/http-probing","message":"Ip 203.0.113.10 performed 'crowdsecurity/http-probing'","machine_id":"localhost",
             "source":{"scope":"Ip","value":"203.0.113.10","as_number":"64500","cn":"NL"},"events_count":11,"created_at":"2026-10-27T15:00:00Z",
             "meta":[{"key":"target_uri","value":"[\"/nope-0\",\"/nope-1\"]"},{"key":"user_agent","value":"[\"Mozilla/5.0 probe\"]"},{"key":"broken","value":"not json"}],
             "events":[{"timestamp":"2026-10-27T14:59:00Z","meta":[{"key":"http_path","value":"/nope-0"},{"key":"http_verb","value":"GET"}]}],
             "decisions":[{"id":4,"value":"203.0.113.10","scope":"Ip","type":"ban","origin":"crowdsec","duration":"3h59m","scenario":"crowdsecurity/http-probing"}]}
            """))
        {
            var parsed = CrowdSecLapi.ParseAlert(detail.RootElement, now);
            check(parsed is { Alert.Id: 12, Alert.Country: "NL", AsNumber: "64500", MachineId: "localhost", Events.Length: 1, Decisions.Length: 1 }
                && parsed.Context["target_uri"].SequenceEqual(["/nope-0", "/nope-1"]) && parsed.Context["broken"].SequenceEqual(["not json"])
                && parsed.Events[0].Meta["http_verb"] == "GET" && parsed.Decisions[0].Origin == "crowdsec"
                && CrowdSecLapi.ParseAlert(JsonDocument.Parse("[]").RootElement, now) is null,
                "Inspecting an alert gives its context lists, events and decision.");
        }
        check(CrowdSecLapi.UnbanQuery("203.0.113.7") == "ip=203.0.113.7" && CrowdSecLapi.UnbanQuery("203.0.113.0/24") == "range=203.0.113.0%2F24"
            && CrowdSecLapi.UnbanQuery("203.0.113.5/24") is null, "Unbanning names one address or one network.");
    }

    private static void DashboardChecks(Action<bool, string> check)
    {
        var observability = StackCatalog.Find(ObservabilityApp.AppId);
        var compose = observability.Render(StackCatalog.Settings(observability, null), new(Guid.NewGuid(), "lucialab01", true, null), new Dictionary<string, string>()).Compose;
        check(compose.Contains("        otlp_config:\n          resource_attributes:\n            attributes_config:\n              - action: index_label\n                attributes: [crowdsec.kind]\n",
                StringComparison.Ordinal),
            "Loki indexes CrowdSec's alerts by kind beside the service; everything else stays structured metadata.");
        var files = new[] { "crowdsec" }.Concat(CrowdSecDashboard.Community.Select(file => "crowdsec-" + Path.GetFileNameWithoutExtension(file))).ToArray();
        check(files.All(name => compose.Contains($"      - source: {name}\n        target: /etc/grafana/dashboards/lucia/{name}.json\n", StringComparison.Ordinal)
                && compose.Contains($"\n  {name}:\n    content: |\n", StringComparison.Ordinal)),
            "Grafana gets the Lucia CrowdSec dashboard and CrowdSec's four community dashboards.");
        var lucia = compose.Split('\n').Single(line => line.TrimStart().StartsWith("{\"uid\":\"lucia-crowdsec\"", StringComparison.Ordinal)).Trim();
        check(lucia.Contains("\"url\":\"${LUCIA_CROWDSEC_CONSOLE:-}/#/security\"", StringComparison.Ordinal)
            && !Regex.IsMatch(lucia.Replace("$$", "", StringComparison.Ordinal).Replace("${LUCIA_CROWDSEC_CONSOLE:-}", "", StringComparison.Ordinal), @"\$")
            && !lucia.Contains(CrowdSecDashboard.ConsolePlaceholder, StringComparison.Ordinal),
            "The Lucia CrowdSec dashboard links to Gateway protection at the address Lucia sets, and every Grafana variable is escaped for compose.");
        using var json = JsonDocument.Parse(CrowdSecDashboard.Json());
        var panels = json.RootElement.GetProperty("panels").EnumerateArray().ToArray();
        string[] Exprs(JsonElement panel) => panel.TryGetProperty("targets", out var targets)
            ? [.. targets.EnumerateArray().Select(target => target.GetProperty("expr").GetString()!)] : [];
        var offenders = panels.Single(panel => panel.GetProperty("title").GetString() == "Offenders" && panel.GetProperty("type").GetString() == "table");
        var renamed = offenders.GetProperty("transformations").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "organize")
            .GetProperty("options").GetProperty("renameByName");
        check(new[] { "Address", "Scenario", "Path", "User agent", "Country", "Alerts", "Last seen", "Decision", "For the assistant" }
                .All(name => renamed.EnumerateObject().Any(item => item.Value.GetString() == name))
            && offenders.GetProperty("fieldConfig").GetProperty("defaults").GetProperty("custom").GetProperty("inspect").GetBoolean()
            && Exprs(offenders)[0].Contains("line_format \"CrowdSec alert: {{.source_address}}", StringComparison.Ordinal)
            && offenders.GetRawText().Contains("/#/security", StringComparison.Ordinal),
            "The offenders table has a row per address and scenario with its count and last sighting, copyable cells, a line for the assistant and a link to Gateway protection.");
        var allExprs = panels.SelectMany(Exprs).ToArray();
        check(allExprs.Where(expr => expr.Contains("service_name", StringComparison.Ordinal)).All(expr => expr.Contains("{service_name=\"crowdsec\"", StringComparison.Ordinal))
            && allExprs.Any(expr => expr.Contains("crowdsec_kind=\"waf\"", StringComparison.Ordinal))
            && allExprs.Any(expr => expr.Contains("cs_active_decisions{origin!~\"CAPI|lists\"}", StringComparison.Ordinal))
            && panels.Any(panel => panel.GetProperty("title").GetString() == "Top offenders")
            && panels.Any(panel => panel.GetProperty("title").GetString() == "Alerts by scenario")
            && panels.Any(panel => panel.GetProperty("title").GetString() == "AppSec blocks" && panel.GetProperty("type").GetString() == "logs"),
            "The dashboard charts alerts by scenario, top offenders, recent AppSec blocks and active bans.");
        check(CrowdSecDashboard.Counters.All(metric => MetricTiers.Counters.Any(item => item.Metric == metric))
            && CrowdSecDashboard.Gauges.All(metric => MetricTiers.Gauges.Any(item => item.Metric == metric))
            && compose.Contains("record: cs_appsec_reqs_total:rate", StringComparison.Ordinal),
            "CrowdSec's AppSec counters and active bans are kept past the hour of raw metrics.");
        var dead = new Regex(@"cs_(filesource|journalctlsource|cloudwatch_stream|syslogsource)_hits_total|cs_bucket_created_total");
        foreach (var file in CrowdSecDashboard.Community)
        {
            var text = CrowdSecDashboard.CommunityJson(file);
            using var community = JsonDocument.Parse(text);
            var root = community.RootElement;
            var exprs = new List<string>();
            void Walk(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Name == "expr" && property.Value.ValueKind == JsonValueKind.String) exprs.Add(property.Value.GetString()!);
                        Walk(property.Value);
                    }
                }
                else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Walk(item);
            }
            Walk(root);
            check(root.GetProperty("uid").GetString()!.StartsWith("crowdsec-", StringComparison.Ordinal) && !text.Contains("DS_PROMETHEUS", StringComparison.Ordinal)
                && !text.Contains("IH0jqv6nz", StringComparison.Ordinal) && !text.Contains("\"graph\"", StringComparison.Ordinal) && !text.Contains("machine=", StringComparison.Ordinal)
                && exprs.Count > 0 && exprs.All(expr => !dead.IsMatch(expr))
                && exprs.Where(expr => expr.StartsWith("process_", StringComparison.Ordinal) || expr.Contains("(process_", StringComparison.Ordinal))
                    .All(expr => expr.Contains("job=\"crowdsec\"", StringComparison.Ordinal))
                && root.GetProperty("description").GetString()!.Contains("MIT", StringComparison.Ordinal),
                $"CrowdSec's community dashboard {file} uses Lucia's Prometheus, the node's crowdsec job and CrowdSec 1.8.1's metric names, and keeps its attribution.");
        }
        var license = Path.Combine(Root(), "src", "Lucia.Homelab.Server", "Telemetry", "CrowdSec", "LICENSE");
        check(File.ReadAllText(license).Contains("Copyright (c) 2020 crowdsecurity", StringComparison.Ordinal),
            "The community dashboards' MIT license ships beside them.");
    }

    private static void IntakeChecks(Action<bool, string> check)
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        using var self = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=lucia-app-observability", key, System.Security.Cryptography.HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        var intake = new AppCertificate(self.ExportCertificatePem() + "\n", key.ExportPkcs8PrivateKeyPem() + "\n", self.NotAfter.ToUniversalTime());
        var forward = ("https://crowdsec.lab.example.com:7423/v1/logs", "-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n", "token-0123456789abcdef0123456789abcdef");
        var only = CrowdSecApp.CollectorLines(null, intake);
        var onlyEnv = StackCatalog.ReadEnv(only);
        var config = onlyEnv["LUCIA_CROWDSEC_INTAKE"];
        check(CrowdSecApp.CollectorLines(null, null) == "" && CrowdSecApp.CollectorLines(forward, null) == CrowdSecApp.CollectorLines(forward.Item1, forward.Item2, forward.Item3)
            && !CrowdSecApp.CollectorLines(forward, null).Contains("INTAKE", StringComparison.Ordinal),
            "Without the intake's certificate the collector gets only the access-log forwarding, and without CrowdSec nothing.");
        check(config.StartsWith("'{receivers: {otlp/crowdsec_alerts: {protocols: {http: {endpoint: \"0.0.0.0:4319\", auth: {authenticator: basicauth/server}, "
                + "tls: {cert_pem: \"-----BEGIN CERTIFICATE-----\\n", StringComparison.Ordinal)
            && config.Contains("\\n-----END CERTIFICATE-----\\n\", key_pem: \"-----BEGIN PRIVATE KEY-----\\n", StringComparison.Ordinal)
            && config.Contains("\\n-----END PRIVATE KEY-----\\n\", min_version: \"1.2\"}}}}}", StringComparison.Ordinal)
            && config.Contains("logs/crowdsec_alerts: {receivers: [otlp/crowdsec_alerts], processors: [memory_limiter, batch], exporters: [otlphttp/loki]}", StringComparison.Ordinal)
            && !config.Contains("otlp_http/crowdsec", StringComparison.Ordinal) && !config.Contains("${env:", StringComparison.Ordinal)
            && onlyEnv.Keys.SequenceEqual(["LUCIA_CROWDSEC_INTAKE"])
            && only.Split('\n').All(line => !line.Contains('\r')) && only.Split('\n').Count(line => line.Length > 0) == 1,
            "The alert intake is its own collector config: an OTLP/HTTP receiver over TLS 1.2 or later behind the collector's own password, sending to Loki only, with the certificate and key inline.");
        var bothEnv = StackCatalog.ReadEnv(CrowdSecApp.CollectorLines(forward, intake));
        check(bothEnv["LUCIA_CROWDSEC_COLLECTOR"].Contains("pipelines: {logs/crowdsec: {receivers: [otlp], ", StringComparison.Ordinal)
            && !bothEnv["LUCIA_CROWDSEC_COLLECTOR"].Contains("crowdsec_alerts", StringComparison.Ordinal) && bothEnv["LUCIA_CROWDSEC_INTAKE"] == config,
            "With both, the access log still goes to CrowdSec from the plain receiver and the alerts come back to Loki from the TLS one, in separate configs.");
        var read = CrowdSecApp.IntakeCertificate(onlyEnv);
        check(read is not null && read.ChainPem.Trim() == intake.ChainPem.Trim() && read.KeyPem.Trim() == intake.KeyPem.Trim()
            && Math.Abs((read.NotAfter - intake.NotAfter).TotalSeconds) < 1
            && CrowdSecApp.IntakeCertificate(new Dictionary<string, string> { ["LUCIA_CROWDSEC_INTAKE"] = "'{tls: {cert_pem: \"garbage\", key_pem: \"x\"}}'" }) is null
            && CrowdSecApp.IntakeCertificate(new Dictionary<string, string>()) is null,
            "Lucia reads the intake's certificate back from the Observability app's environment, and garbage reads as none.");

        var observability = StackCatalog.Find(ObservabilityApp.AppId);
        var settings = StackCatalog.Settings(observability, null);
        var node = new Lucia.Homelab.Server.Nodes.ManagedNodeFacts(Guid.NewGuid(), "lucialab01", true, null);
        var output = observability.Render(settings, node, new Dictionary<string, string>());
        check(output.Names!.SequenceEqual(["otlp-alerts"]) && !observability.NamesNeedDomain && StackCatalog.Find(CrowdSecApp.AppId).NamesNeedDomain
            && ObservabilityApp.AlertsName(settings, "lab.example.com") == "otlp-alerts.lab.example.com"
            && output.Compose.Contains("      - \"4319:4319\"\n", StringComparison.Ordinal)
            && output.Compose.Contains("      LUCIA_CROWDSEC_INTAKE: ${LUCIA_CROWDSEC_INTAKE:-}\n", StringComparison.Ordinal)
            && output.Compose.Split("--config=env:LUCIA_CROWDSEC_INTAKE").Length == 3
            && output.Compose.Contains("      - \"4318:4318\"\n", StringComparison.Ordinal),
            "Observability publishes the alert intake's port beside the unchanged plain receivers and has a direct name for it, which doesn't need a domain to install.");
        foreach (var bad in new Dictionary<string, string>[] { new() { ["alerts-host"] = "grafana" }, new() { ["alerts-host"] = "otlp" }, new() { ["alerts-port"] = "4318" },
                     new() { ["alerts-port"] = "2055" }, new() { ["alerts-host"] = "a.b" } })
            check(Throws(() => observability.Render(StackCatalog.Settings(observability, bad), node, new Dictionary<string, string>()), 400),
                $"Observability must refuse {string.Join(",", bad.Select(pair => pair.Key + "=" + pair.Value))}.");
    }

    private static string N(string text) => text.ReplaceLineEndings("\n");

    /// <summary>
    /// With LUCIA_RE2_CORPUS set, writes every regex the checks use, whether Lucia accepts it, and what .NET matches on the breadth
    /// samples, for <c>tools/security/re2-crosscheck.go</c> to replay with Go's regexp (RE2, as CrowdSec runs it).
    /// </summary>
    private static void Re2Corpus()
    {
        if (Environment.GetEnvironmentVariable("LUCIA_RE2_CORPUS") is not { Length: > 0 } output) return;
        string[] samples = [.. CrowdSecRules.ProtectedPaths, .. CrowdSecRules.ProtectedAgents, .. CrowdSecRules.HeaderValues,
            "1", "true", "en", "search", "/wp-admin/x", "/wp-login.php", "/cgi-bin/a.pl", "/.env", "sqlmap/1.7", "/A_b-9xyzw.bak", "/x?y", "/repo/.git/config",
            "union all select", "0.0.12-evil", "nikto/2.5", "/admin", "/ADMIN", "/a{,5}", "aa", "abc-def", "]", "/[x]", "a.b", "a\tb"];
        string[] patterns =
        [
            @"^/wp-(?:admin|login)\.php$", @"[A-Za-z0-9_\-]{8,}\.bak$", @"^/cgi-bin/.*?\.(pl|sh)$", @"^/[^/]+/\.git/[\w.-]+", @"(?:union|UNION)\s+(?:all\s+)?select",
            @"^0\.0\.[0-9]{1,2}-evil$", "^(nikto|masscan)/", @"^/cgi-bin/.*\.pl$", @"^/a(?:b|c)*?[x-z\]\-]{1,3}\.d$", @"\d{2,}", @"[^a-z]+$", @"\S\s\S", @"\W",
            @"[\w]{3}", @"a|b|", "x?", ".", ".*", "^$", @"\.", "[.]", @"[\^a]", "[a^]", "[-a]", "[a-]", @"\/admin", @"\{", @"\}", @"\]", @"\[x\]", "(a)(b)?",
            "a{2}", "a{2,}", "a{1,3}?", @"\$", @"\|", @"\(", @"\)", @"\*", @"\+", @"\?",
            "[[:print:]]+", "[[:alpha:]]", "/[a-z-[aeiou]]+", "(?i)/ADMIN", "/a{,5}", "/[]a]", "/[a-c-e]",
        ];
        var corpus = patterns.Select(pattern =>
        {
            var accepted = CrowdSecRules.RegexProblem(pattern) is null;
            var regex = accepted ? new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.NonBacktracking | System.Text.RegularExpressions.RegexOptions.CultureInvariant) : null;
            return new { pattern, accepted, matches = samples.Select(sample => regex?.IsMatch(sample)).ToArray() };
        });
        File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(new { samples, patterns = corpus }));
    }

    private static bool Throws(Action action, int? status)
    {
        try { action(); return false; }
        catch (HardwareOnboardingException error) { return status is null || error.StatusCode == status; }
    }

    private static async Task<bool> ThrowsAsync(Func<Task> action, int? status)
    {
        try { await action(); return false; }
        catch (HardwareOnboardingException error) { return status is null || error.StatusCode == status; }
        catch (Exception error) when (status is null && error is InvalidDataException or JsonException) { return true; }
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "tools", "identity", "provision.py")))
                return directory.FullName;
        throw new InvalidOperationException("The checkout root was not found.");
    }
}
