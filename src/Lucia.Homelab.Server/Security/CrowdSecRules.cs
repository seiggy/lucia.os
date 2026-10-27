using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Onboarding;

namespace Lucia.Homelab.Server.Security;

/// <param name="Field">path, query (any query parameter's value), user-agent, header (the one named in <paramref name="Header"/>) or method.</param>
/// <param name="Operator">equals, prefix, contains or regex (RE2, as CrowdSec runs it).</param>
public sealed record CrowdSecRuleRequest(string? Field, string? Header, string? Operator, string? Value, string? Reason);
/// <param name="Name">The rule's name in CrowdSec, <c>lucia/custom-&lt;id&gt;</c>; an AppSec block it causes is an alert of that name.</param>
public sealed record CrowdSecRuleView(string Id, string Name, string Field, string? Header, string Operator, string Value, string Reason,
    DateTimeOffset CreatedAt, string CreatedBy);
public sealed record CrowdSecRulesView(CrowdSecRuleView[] Rules, int Max);
internal sealed record CrowdSecRulesState(int SchemaVersion, CrowdSecRuleView[] Rules);

/// <summary>
/// The owner's custom AppSec rules: requests that match one are refused with a 403 at the gateway, on every route AppSec
/// checks. Lucia keeps the list in the host's data and renders it into the CrowdSec app the way it carries the safe harbor;
/// the engine reloads (CrowdSec's own SIGHUP, not a restart) when they change. Safe-harbor addresses skip them, as they skip
/// all of AppSec. Each rule matches one part of the request with one operator, and Lucia refuses rules that would match
/// everything or Lucia's own console.
/// </summary>
public sealed partial class CrowdSecRules(GatewaySecurityOptions options, ILogger<CrowdSecRules> logger)
{
    public const int Max = 50, MaxValue = 200, MaxReason = 200;
    internal const string StateFileName = "crowdsec-appsec-rules.json";
    public static readonly string[] Fields = ["path", "query", "user-agent", "header", "method"];
    public static readonly string[] Operators = ["equals", "prefix", "contains", "regex"];
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Raised after the list changes.</summary>
    public event Action? Changed;

    private string StatePath => Path.Combine(options.StateDirectory, StateFileName);

    public async Task<CrowdSecRulesView> List(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return new(ReadState(), Max); }
        finally { _gate.Release(); }
    }

    /// <summary>The rules as the engine reads them, one YAML document each. A list that can't be read counts as empty.</summary>
    public async Task<string> Rendered(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            try { return Render(ReadState()); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or HardwareOnboardingException)
            {
                logger.LogWarning("The custom AppSec rules could not be read ({ErrorType}); the engine gets none.", error.GetType().Name);
                return "";
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<CrowdSecRuleView> Add(CrowdSecRuleRequest request, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var (field, header, op, value, reason) = Validate(request);
        if (!Path.IsPathFullyQualified(options.StateDirectory))
            throw new HardwareOnboardingException(409, "crowdsec_rules_unavailable", "Custom AppSec rules need Lucia's gateway security, which isn't set up here.");
        CrowdSecRuleView rule;
        await _gate.WaitAsync(ct);
        try
        {
            var rules = ReadState();
            if (rules.Length >= Max)
                throw new HardwareOnboardingException(409, "too_many_rules", $"Lucia keeps at most {Max} custom AppSec rules. Remove one first.");
            if (rules.FirstOrDefault(item => item.Field == field && string.Equals(item.Header, header, StringComparison.OrdinalIgnoreCase)
                    && item.Operator == op && item.Value == value) is { } same)
                throw new HardwareOnboardingException(409, "rule_exists", $"Rule {same.Id} already does this.");
            string id;
            do id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            while (rules.Any(item => item.Id == id));
            rule = new(id, RuleName(id), field, header, op, value, reason, now, Actor(actor));
            await DomainOnboardingStore.WriteJson(StatePath, new CrowdSecRulesState(1, [.. rules, rule]), ct);
        }
        finally { _gate.Release(); }
        logger.LogInformation("{Actor} added the custom AppSec rule {Rule}: {Field} {Operator}.", Actor(actor), rule.Name, rule.Field, rule.Operator);
        Changed?.Invoke();
        return rule;
    }

    public async Task<CrowdSecRuleView> Remove(string? id, string actor, CancellationToken ct)
    {
        var key = (id ?? "").Trim().ToLowerInvariant();
        if (key.StartsWith("lucia/custom-", StringComparison.Ordinal)) key = key["lucia/custom-".Length..];
        if (!RuleId().IsMatch(key)) throw new HardwareOnboardingException(400, "invalid_rule", "Name the rule by its id from the list, such as 3f2a9c01.");
        CrowdSecRuleView rule;
        await _gate.WaitAsync(ct);
        try
        {
            var rules = ReadState();
            rule = rules.FirstOrDefault(item => item.Id == key)
                ?? throw new HardwareOnboardingException(404, "rule_not_found", $"There's no custom AppSec rule {key}.");
            await DomainOnboardingStore.WriteJson(StatePath, new CrowdSecRulesState(1, [.. rules.Where(item => item.Id != key)]), ct);
        }
        finally { _gate.Release(); }
        logger.LogInformation("{Actor} removed the custom AppSec rule {Rule}.", Actor(actor), rule.Name);
        Changed?.Invoke();
        return rule;
    }

    internal static string RuleName(string id) => "lucia/custom-" + id;

    private static string Actor(string actor) => new([.. actor.Where(ch => !char.IsControl(ch)).Take(120)]);

    private CrowdSecRuleView[] ReadState()
    {
        if (!Path.IsPathFullyQualified(options.StateDirectory)) return [];
        DomainOnboardingStore.RejectLinks(StatePath);
        if (!File.Exists(StatePath)) return [];
        if (new FileInfo(StatePath).Length > 1024 * 1024) throw new InvalidDataException("The custom AppSec rules are oversized.");
        var state = JsonSerializer.Deserialize<CrowdSecRulesState>(File.ReadAllBytes(StatePath), DomainOnboardingStore.Json);
        if (state is not { SchemaVersion: 1, Rules: { } rules } || rules.Length > Max) throw new InvalidDataException("The custom AppSec rules are invalid.");
        // A saved rule passes the same checks as a new one, so a hand-edited file can't smuggle anything into the engine.
        foreach (var rule in rules)
        {
            var (field, header, op, value, reason) = Validate(new(rule.Field, rule.Header, rule.Operator, rule.Value, rule.Reason));
            if (!RuleId().IsMatch(rule.Id ?? "") || rule.Name != RuleName(rule.Id!) || (field, header, op, value, reason) != (rule.Field, rule.Header, rule.Operator, rule.Value, rule.Reason))
                throw new InvalidDataException("The custom AppSec rules are invalid.");
        }
        return rules;
    }

    // Paths of Lucia's own console and API, and of any web app's front page: a rule may never block them.
    internal static readonly string[] ProtectedPaths =
    [
        "/", "/index.html", "/favicon.ico", "/login", "/assets/index.js", "/api/host/security/crowdsec", "/api/auth/session", "/api/auth/login",
        "/api/assistant/chat", "/api/host/stacks", "/api/host/telemetry/relay/v1/logs", "/lucia.os/docs/", "/v1/logs", "/v1/metrics", "/v1/traces",
    ];

    // Ordinary browsers and Lucia's own clients: a user-agent rule that matches one would lock out everyone or Lucia itself.
    internal static readonly string[] ProtectedAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Safari/605.1.15",
        "Mozilla/5.0 (X11; Linux x86_64; rv:143.0) Gecko/20100101 Firefox/143.0",
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Mobile/15E148 Safari/604.1",
        "Mozilla/5.0 (Linux; Android 15) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Mobile Safari/537.36",
        "OpenTelemetry Collector Contrib/0.161.0 (linux/amd64)", "lucia-host/1", "lucia-agent/1", "Go-http-client/1.1", "Traefik",
    ];

    // Ordinary query values: a query rule that matches all of them matches nearly every request with a query.
    private static readonly string[] CommonValues = ["1", "true", "en", "search", "10", "abc", "2026-01-01", "id"];

    /// <summary>
    /// A request in canonical form, or a 400 naming what's wrong. Values are plain text without quotes, backslashes (except in
    /// a regex), <c>%{</c> or control characters, so nothing can reach CrowdSec's rule language as anything but a value.
    /// </summary>
    internal static (string Field, string? Header, string Operator, string Value, string Reason) Validate(CrowdSecRuleRequest request)
    {
        static HardwareOnboardingException Bad(string message) => new(400, "invalid_rule", message);
        var field = (request.Field ?? "").Trim().ToLowerInvariant();
        if (field is "user_agent" or "useragent" or "ua") field = "user-agent";
        if (!Fields.Contains(field)) throw Bad("Match one of: path, query, user-agent, header or method.");
        var op = (request.Operator ?? "").Trim().ToLowerInvariant();
        if (op is "startswith" or "starts-with") op = "prefix";
        if (!Operators.Contains(op)) throw Bad("Use one of the operators equals, prefix, contains or regex.");
        string? header = null;
        if (field == "header")
        {
            header = (request.Header ?? "").Trim();
            if (!HeaderName().IsMatch(header)) throw Bad("Name the header, such as X-Api-Version: letters, digits and dashes, up to 64.");
            if (header.Equals("Host", StringComparison.OrdinalIgnoreCase))
                throw Bad("A rule can't match the Host header: that's how every request reaches its app, Lucia's console included.");
            if (header.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) (field, header) = ("user-agent", null);
            else if (CommonHeader(header))
                throw Bad($"A rule can't match {header}: browsers, Cloudflare, the gateway or Lucia's own clients send it on ordinary requests. Match a path, a user agent or an unusual header instead.");
        }
        else if (!string.IsNullOrEmpty(request.Header)) throw Bad("Only a header rule names a header.");
        var value = request.Value ?? "";
        if (value.Length == 0 || value.Trim().Length == 0) throw Bad("Give the text to match; an empty pattern would match everything.");
        if (value.Length > MaxValue) throw Bad($"Keep the pattern to {MaxValue} characters.");
        if (value.Any(ch => ch is < ' ' or > '~'))
            throw Bad("Keep the pattern to printable ASCII: letters, digits, spaces and punctuation, no accents, emoji or control characters.");
        if (value.Contains('"') || value.Contains("%{", StringComparison.Ordinal) || (op != "regex" && value.Contains('\\')))
            throw Bad("The pattern can't contain quotes, %{ or (outside a regex) backslashes.");
        var reason = (request.Reason ?? "").Trim();
        if (reason.Length == 0) throw Bad("Say why, in a few words, so the rule list explains itself.");
        if (reason.Length > MaxReason || reason.Any(char.IsControl)) throw Bad($"Keep the reason to one line of {MaxReason} characters.");
        // CrowdSec reads the rules with go-yaml, which refuses an escaped surrogate pair (\uD83E\uDD16), so emoji and other
        // characters above U+FFFF can't be quoted for it; U+FFFE and U+FFFF aren't YAML characters at all.
        if (reason.Any(ch => char.IsSurrogate(ch) || ch is '\uFFFE' or '\uFFFF'))
            throw Bad("The reason can't contain emoji or other characters CrowdSec's rule files can't hold. Use plain text.");

        if (field == "method")
        {
            value = value.Trim().ToUpperInvariant();
            if (op != "equals") throw Bad("A method rule uses equals, such as PROPFIND.");
            if (!MethodName().IsMatch(value)) throw Bad("Give one HTTP method, such as PROPFIND or TRACE.");
            if (value is "GET" or "HEAD" or "POST" or "OPTIONS")
                throw Bad($"Blocking every {value} request would take down every app. Match a path or user agent instead.");
        }

        Func<string, bool> matches;
        if (op == "regex")
        {
            if (RegexProblem(value) is { } problem)
                throw Bad($"CrowdSec runs RE2, and Lucia accepts only the part of it that .NET reads the same way, so it can check what the rule matches: {problem} isn't in that part.");
            if (NestedRepeat().IsMatch(value)) throw Bad("The pattern repeats a repeated group, such as (a+)+. Simplify it.");
            Regex regex;
            try { regex = new Regex(value, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            {
                throw Bad("That isn't a regular expression CrowdSec can run. Use RE2 syntax without lookarounds or backreferences.");
            }
            if (regex.IsMatch("")) throw Bad("That pattern matches empty text, so it would match every request.");
            matches = regex.IsMatch;
        }
        else
        {
            if (field is "path" && value.Trim() == "/") throw Bad("A rule on / would match every request.");
            if (field is "path" && !value.StartsWith('/') && op is "equals" or "prefix") throw Bad("A path starts with /, such as /wp-login.php.");
            matches = op switch
            {
                "equals" => text => text == value,
                "prefix" => text => text.StartsWith(value, StringComparison.Ordinal),
                _ => text => text.Contains(value, StringComparison.Ordinal),
            };
        }
        switch (field)
        {
            case "path" when ProtectedPaths.FirstOrDefault(matches) is { } path:
                throw Bad($"That would also block {path}, which Lucia's console or every app needs. Make it more specific.");
            case "user-agent" when ProtectedAgents.FirstOrDefault(matches) is { } agent:
                throw Bad($"That would also block ordinary browsers or Lucia itself ({agent[..Math.Min(40, agent.Length)]}…). Make it more specific.");
            case "query" when CommonValues.All(matches):
                throw Bad("That would match almost any query. Make it more specific.");
            case "header" when HeaderValues.FirstOrDefault(matches) is { } sample:
                throw Bad($"That would also match an ordinary header value ({sample}). Make it more specific.");
        }
        return (field, header, op, value, reason);
    }

    /// <summary>The rules in CrowdSec's appsec-rules format, one YAML document each. Strings go out JSON-quoted, which YAML reads as is.</summary>
    internal static string Render(IEnumerable<CrowdSecRuleView> rules)
    {
        static string Quote(string text) => JsonSerializer.Serialize(text);
        var text = new StringBuilder();
        foreach (var rule in rules)
        {
            var (zone, variable) = rule.Field switch
            {
                "path" => ("URI", null),
                "query" => ("ARGS", null),
                "user-agent" => ("HEADERS", "User-Agent"),
                "header" => ("HEADERS", rule.Header),
                _ => ("METHOD", (string?)null),
            };
            var type = rule.Operator switch { "prefix" => "startsWith", "regex" => "regex", var op => op };
            if (text.Length > 0) text.Append("---\n");
            text.Append($"name: {RuleName(rule.Id)}\n")
                .Append($"description: {Quote("Lucia custom rule: " + rule.Reason)}\n")
                .Append("rules:\n")
                .Append($"  - zones:\n      - {zone}\n");
            if (variable is not null) text.Append($"    variables:\n      - {Quote(variable)}\n");
            text.Append($"    match:\n      type: {type}\n      value: {Quote(rule.Value)}\n")
                .Append("labels:\n  type: exploit\n  service: http\n  confidence: 3\n  spoofable: 0\n  behavior: \"http:exploit\"\n")
                .Append($"  label: {Quote("Lucia custom rule " + rule.Id)}\n");
        }
        return text.ToString();
    }

    // Headers every request, or every request through Cloudflare or the gateway, may carry; a rule on one is a rule on everyone.
    // User-Agent becomes a user-agent rule and Host is refused with its own reason.
    private static readonly string[] CommonHeaderPrefixes = ["accept", "content-", "cf-", "x-forwarded-", "sec-"];
    private static readonly string[] CommonHeaders = ["cookie", "connection", "x-real-ip", "authorization", "cdn-loop"];

    internal static bool CommonHeader(string header)
    {
        var name = header.ToLowerInvariant();
        return CommonHeaders.Contains(name) || CommonHeaderPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
    }

    // Values ordinary headers carry: a header rule that matches one of them is too broad to know what it would block.
    internal static readonly string[] HeaderValues =
    [
        "*/*", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", "gzip, deflate, br", "192.0.2.10", "2001:db8::10", "a=b",
        "3f2a9c01-5b6d-4e7f-8a9b-0c1d2e3f4a5b", "en-US,en;q=0.9", "keep-alive", "no-cache", "max-age=0", "https://lucia.lab.example.com",
    ];

    /// <summary>
    /// Null when <paramref name="pattern"/> stays inside the regex syntax RE2 and .NET read the same way, else the first construct
    /// outside it. Allowed: printable ASCII literals; <c>.</c>; <c>^</c> and <c>$</c>; escaped punctuation and
    /// <c>\d \D \w \W \s \S</c>; classes <c>[...]</c> and <c>[^...]</c> of literals, ascending ranges and those escapes; groups
    /// <c>(...)</c> and <c>(?:...)</c>; <c>|</c>; and <c>* + ?</c> or <c>{n}</c>, <c>{n,}</c>, <c>{n,m}</c> up to 100, each
    /// optionally lazy. Everything else (POSIX classes, class subtraction, flags, named groups, lookarounds, backreferences,
    /// <c>\p</c>, <c>\x</c>, <c>\Q</c>, <c>\A</c>, a bare <c>{</c> or <c>]</c>) is refused: there the two engines disagree or one
    /// doesn't parse it, and Lucia's breadth checks run in .NET while CrowdSec runs RE2.
    /// </summary>
    internal static string? RegexProblem(string pattern)
    {
        const string Punctuation = @"\.^$*+?()[]{}|/-";
        const string Classes = "dDwWsS";
        int depth = 0, i = 0;
        var repeatable = false;
        while (i < pattern.Length)
        {
            var ch = pattern[i];
            switch (ch)
            {
                case < ' ' or > '~':
                    return "a character outside printable ASCII";
                case '\\':
                    if (i + 1 == pattern.Length) return "a lone \\ at the end";
                    if (!Punctuation.Contains(pattern[i + 1]) && !Classes.Contains(pattern[i + 1])) return $"the escape \\{pattern[i + 1]}";
                    i += 2;
                    repeatable = true;
                    continue;
                case '[':
                    if (ClassEnd(pattern, i, out var problem) is not { } end) return problem;
                    i = end + 1;
                    repeatable = true;
                    continue;
                case '(':
                    if (i + 1 < pattern.Length && pattern[i + 1] == '?')
                    {
                        if (i + 2 >= pattern.Length || pattern[i + 2] != ':') return "a (? group other than (?:...)";
                        i += 2;
                    }
                    depth++;
                    repeatable = false;
                    break;
                case ')':
                    if (depth == 0) return "a ) without its (";
                    depth--;
                    repeatable = true;
                    break;
                case '|' or '^' or '$':
                    repeatable = false;
                    break;
                case '*' or '+' or '?' or '{':
                    if (!repeatable) return $"a {ch} with nothing to repeat";
                    if (ch == '{')
                    {
                        if (CountedRepeat().Match(pattern, i) is not { Success: true } counted || counted.Index != i)
                            return "a { that isn't a counted repeat such as {2,5} (write \\{ for the character)";
                        var low = int.Parse(counted.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                        var high = !counted.Groups[2].Success ? low
                            : counted.Groups[3].Value is { Length: > 0 } upper ? int.Parse(upper, System.Globalization.CultureInfo.InvariantCulture) : low;
                        if (low > 100 || high > 100) return "a counted repeat over 100";
                        if (high < low) return "a counted repeat whose upper bound is below its lower one";
                        i += counted.Length - 1;
                    }
                    if (i + 1 < pattern.Length && pattern[i + 1] == '?') i++;
                    repeatable = false;
                    break;
                case '}' or ']':
                    return $"a bare {ch} (write \\{ch} for the character)";
                default:
                    repeatable = true;
                    break;
            }
            i++;
        }
        return depth == 0 ? null : "a ( without its )";
    }

    /// <summary>The index of the <c>]</c> that closes the class opening at <paramref name="start"/>, or null with the reason.</summary>
    private static int? ClassEnd(string pattern, int start, out string problem)
    {
        const string Escapable = @"\]-[^.";
        problem = "";
        var i = start + 1;
        if (i < pattern.Length && pattern[i] == '^') i++;
        var first = i;
        while (i < pattern.Length && pattern[i] != ']')
        {
            char low;
            if (pattern[i] == '[') { problem = "a [ inside a class, as in [[:alpha:]] or [a-z-[aeiou]] (write \\[ for the character)"; return null; }
            if (pattern[i] == '\\')
            {
                if (i + 1 == pattern.Length) { problem = "a lone \\ at the end"; return null; }
                var escaped = pattern[i + 1];
                if (escaped is 'd' or 'D' or 'w' or 'W' or 's' or 'S')
                {
                    if (i + 2 < pattern.Length && pattern[i + 2] == '-' && i + 3 < pattern.Length && pattern[i + 3] != ']')
                    { problem = "a range that starts at a class escape such as \\d"; return null; }
                    i += 2;
                    continue;
                }
                if (!Escapable.Contains(escaped)) { problem = $"the escape \\{escaped} in a class"; return null; }
                low = escaped;
                i += 2;
            }
            else if (pattern[i] == '-')
            {
                if (i != first && i + 1 < pattern.Length && pattern[i + 1] != ']')
                { problem = "a - in a class that isn't first, last or a simple range like a-z (write \\- for the character)"; return null; }
                i++;
                continue;
            }
            else if (pattern[i] is < ' ' or > '~') { problem = "a character outside printable ASCII"; return null; }
            else low = pattern[i++];
            if (i + 1 < pattern.Length && pattern[i] == '-' && pattern[i + 1] != ']')
            {
                char high;
                if (pattern[i + 1] == '\\')
                {
                    if (i + 2 >= pattern.Length || !Escapable.Contains(pattern[i + 2])) { problem = "a range that ends at a class escape"; return null; }
                    high = pattern[i + 2];
                    i += 3;
                }
                else if (pattern[i + 1] == '[') { problem = "a [ inside a class, as in [a-z-[aeiou]] (write \\[ for the character)"; return null; }
                else { high = pattern[i + 1]; i += 2; }
                if (high < low) { problem = $"the range {low}-{high}, which runs backwards"; return null; }
            }
        }
        if (i >= pattern.Length) { problem = "a [ without its ]"; return null; }
        if (i == first) { problem = "an empty class [] (write \\] for the character)"; return null; }
        return i;
    }

    [GeneratedRegex(@"\A[0-9a-f]{8}\z")] private static partial Regex RuleId();
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9-]{0,63}\z")] private static partial Regex HeaderName();
    [GeneratedRegex(@"\A[A-Z]{3,16}\z")] private static partial Regex MethodName();
    [GeneratedRegex(@"\((?:[^()\\]|\\.)*[*+](?:[^()\\]|\\.)*\)[*+{]")] private static partial Regex NestedRepeat();
    [GeneratedRegex(@"\G\{([0-9]{1,3})(,([0-9]{0,3}))?\}")] private static partial Regex CountedRepeat();
}
