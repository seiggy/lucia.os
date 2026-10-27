using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Onboarding;

namespace Lucia.Homelab.Server.Security;

/// <param name="Target">An address, or a network written with its host bits clear (IPv4 /16 or narrower, IPv6 /48 or narrower).</param>
/// <param name="Duration">How long, such as 30m, 4h or 7d: from a minute to 30 days.</param>
/// <param name="Reason">Why, in a line; it's kept with the ban.</param>
public sealed record CrowdSecBanRequest(string? Target, string? Duration, string? Reason);
/// <param name="Kind">crowdsec (a scenario), waf (an AppSec block), or neither for both.</param>
/// <param name="Since">Only alerts newer than this, such as 1h or 7d; 30 days at most.</param>
public sealed record CrowdSecAlertQuery(string? Ip = null, string? Range = null, string? Scenario = null, string? Kind = null, string? Since = null, int? Limit = null);
public sealed record CrowdSecBanView(long? Alert, string Target, string Scope, string Duration, DateTimeOffset Until, string Origin, string Reason);
public sealed record CrowdSecEventView(DateTimeOffset? At, IReadOnlyDictionary<string, string> Meta);
/// <param name="Context">The alert's context as CrowdSec keeps it: paths, methods, user agents and the like, each a short list.</param>
public sealed record CrowdSecAlertDetail(CrowdSecAlertView Alert, string? Message, string? MachineId, string? AsNumber, string? Range,
    IReadOnlyDictionary<string, string[]> Context, CrowdSecEventView[] Events, CrowdSecDecisionView[] Decisions);

public sealed partial class CrowdSec
{
    /// <summary>The scenario Lucia files its own bans under; the reason goes in the alert's message.</summary>
    internal const string ManualScenario = "lucia/manual-ban";
    /// <summary>The origin of a ban the assistant places; one the owner places on the Gateway protection page is <see cref="OwnerOrigin"/>.</summary>
    internal const string AssistantOrigin = "lucia-assistant", OwnerOrigin = "lucia";

    /// <summary>
    /// Bans an address or network for a while, through the local API as the <c>lucia-host</c> machine, the way cscli does.
    /// Refused for anything Lucia trusts: the safe harbor, Lucia's own addresses (its networks, servers and apps) and Cloudflare.
    /// </summary>
    public async Task<CrowdSecBanView> Ban(CrowdSecBanRequest request, string actor, string origin, CancellationToken ct)
    {
        var (target, scope, network) = CrowdSecGuard.Target(request.Target);
        var duration = CrowdSecGuard.Duration(request.Duration);
        var reason = CrowdSecGuard.Reason(request.Reason);
        CrowdSecGuard.RefuseProtected(network, await Protected(ct));
        var (lapi, password) = await LapiTarget(ct);
        var now = time.GetUtcNow();
        var body = CrowdSecLapi.BanAlert(target, scope, duration, reason, actor, origin, now);
        using var result = await Call(() => Lapi().Send(lapi, password, HttpMethod.Post, "/v1/alerts", ct, body), ct);
        long? id = result.RootElement.ValueKind == JsonValueKind.Array && result.RootElement.GetArrayLength() > 0
            && result.RootElement[0] is { ValueKind: JsonValueKind.String } first && long.TryParse(first.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number : null;
        logger.LogInformation("{Actor} banned {Target} for {Minutes} minutes through CrowdSec ({Origin}).", actor, target, (int)duration.TotalMinutes, origin);
        return new(id, target, scope, CrowdSecGuard.Format(duration), now + duration, origin, reason);
    }

    /// <summary>Alerts, newest first, filtered by address, network, scenario, kind and age. The community blocklist stays out.</summary>
    public async Task<CrowdSecAlertView[]> Alerts(CrowdSecAlertQuery query, CancellationToken ct)
    {
        var (path, kind, range, take) = CrowdSecGuard.AlertsPath(query);
        var (origin, password) = await LapiTarget(ct);
        using var alerts = await Call(() => Lapi().Send(origin, password, HttpMethod.Get, path, ct), ct);
        return [.. CrowdSecLapi.ParseAlerts(alerts.RootElement).Where(alert => (kind is null || alert.Kind == kind)
            && (range is not { } network || (IPAddress.TryParse(alert.Value, out var address) && network.Contains(address)))).Take(take)];
    }

    /// <summary>One alert with its context and events: the paths, methods and user agents behind it.</summary>
    public async Task<CrowdSecAlertDetail> Alert(long id, CancellationToken ct)
    {
        if (id <= 0) throw new HardwareOnboardingException(400, "invalid_alert", "Give the alert's id from the list of alerts.");
        var (origin, password) = await LapiTarget(ct);
        using var alert = await Call(() => Lapi().Send(origin, password, HttpMethod.Get, "/v1/alerts/" + id.ToString(CultureInfo.InvariantCulture), ct), ct);
        return CrowdSecLapi.ParseAlert(alert.RootElement, time.GetUtcNow())
            ?? throw new HardwareOnboardingException(404, "alert_not_found", $"CrowdSec has no alert {id}.");
    }

    /// <summary>Everything a ban must never touch, each with the words a refusal uses.</summary>
    private async Task<(IPNetwork Network, string What)[]> Protected(CancellationToken ct)
    {
        var lucia = GatewaySecurity.Loopback.Concat(harbor.Lucia)
            .Concat((await nodes.Addresses(ct)).Select(node => node.Address)).Concat(await stacks.AppAddresses(ct))
            .Select(entry => CrowdSecGuard.Network(entry)).OfType<IPNetwork>().ToHashSet();
        var trusted = (await harbor.Entries(ct)).Select(entry => CrowdSecGuard.Network(entry)).OfType<IPNetwork>().Where(network => !lucia.Contains(network));
        return
        [
            .. lucia.Select(network => (network, "Lucia's own")),
            .. trusted.Select(network => (network, "in the safe harbor")),
            .. DomainIngressConfiguration.CloudflareRanges.Concat(CrowdSecGuard.CloudflareV6)
                .Select(range => (IPNetwork.Parse(range), "Cloudflare's, which every public visitor comes through")),
        ];
    }
}

/// <summary>The checks every ban and alert query passes, whoever asks: the owner's page or the assistant.</summary>
internal static partial class CrowdSecGuard
{
    internal static readonly TimeSpan MinBan = TimeSpan.FromMinutes(1), MaxBan = TimeSpan.FromDays(30), MaxSince = TimeSpan.FromDays(30);
    internal const int MaxReason = 200;
    /// <summary>
    /// Cloudflare's IPv6 proxies (cloudflare.com/ips-v6). The gateway trusts only the IPv4 list, but a ban never touches either.
    /// </summary>
    internal static readonly string[] CloudflareV6 =
        ["2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32", "2405:8100::/32", "2a06:98c0::/29", "2c0f:f248::/32"];

    /// <summary>The address or network to ban in canonical form, with CrowdSec's scope for it.</summary>
    internal static (string Value, string Scope, IPNetwork Network) Target(string? text)
    {
        static HardwareOnboardingException Bad(string message) => new(400, "invalid_address", message);
        var value = (text ?? "").Trim();
        if (value.Length is 0 or > 64 || value.Contains('%')) throw Bad("Name an address such as 203.0.113.7, or a network such as 203.0.113.0/24.");
        IPNetwork network;
        if (value.Contains('/'))
        {
            if (!IPNetwork.TryParse(value, out network) || !IPAddress.TryParse(value[..value.IndexOf('/')], out var written) || !written.Equals(network.BaseAddress))
                throw Bad($"{value} isn't a network. Write it like 203.0.113.0/24, with the host part of the address set to zero.");
        }
        else if (IPAddress.TryParse(value, out var single)) network = new IPNetwork(single, single.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
        else throw Bad($"{value} isn't an IP address.");
        var address = network.BaseAddress;
        var v4 = address.AddressFamily == AddressFamily.InterNetwork;
        var bytes = address.GetAddressBytes();
        if (address.IsIPv4MappedToIPv6) throw Bad($"Write {value} as a plain IPv4 address.");
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || (v4 ? bytes[0] >= 224 || bytes[0] == 0 : bytes[0] == 0xff))
            throw Bad($"{value} isn't a device or network that can be banned.");
        var full = v4 ? 32 : 128;
        if (network.PrefixLength < (v4 ? 16 : 48))
            throw Bad($"{value} is too wide. Ban at most a /{(v4 ? 16 : 48)} at a time.");
        return network.PrefixLength == full ? (address.ToString(), "Ip", network) : (network.ToString(), "Range", network);
    }

    /// <summary>A duration such as 90m, 4h, 1h30m or 7d, from a minute to 30 days.</summary>
    internal static TimeSpan Duration(string? text)
    {
        var value = (text ?? "").Trim().ToLowerInvariant();
        var match = DurationText().Match(value);
        if (value.Length == 0 || !match.Success)
            throw new HardwareOnboardingException(400, "invalid_duration", "Give the ban's length, from a minute to 30 days, like 30m, 4h, 1h30m or 7d.");
        long Part(int group) => match.Groups[group].Success ? long.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture) : 0;
        if (match.Groups[1].Value.Length > 4 || match.Groups[2].Value.Length > 6 || match.Groups[3].Value.Length > 7)
            throw new HardwareOnboardingException(400, "invalid_duration", "A ban lasts from a minute to 30 days.");
        var duration = TimeSpan.FromDays(Part(1)) + TimeSpan.FromHours(Part(2)) + TimeSpan.FromMinutes(Part(3));
        if (duration < MinBan || duration > MaxBan)
            throw new HardwareOnboardingException(400, "invalid_duration", "A ban lasts from a minute to 30 days.");
        return duration;
    }

    internal static string Format(TimeSpan duration) => ((long)duration.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m";

    internal static string Reason(string? text)
    {
        var reason = (text ?? "").Trim();
        if (reason.Length == 0) throw new HardwareOnboardingException(400, "invalid_reason", "Say why, in a line: it's kept with the ban.");
        if (reason.Length > MaxReason || reason.Any(char.IsControl))
            throw new HardwareOnboardingException(400, "invalid_reason", $"Keep the reason to one line of {MaxReason} characters.");
        return reason;
    }

    /// <summary>Refuses a target that is, contains or sits inside anything Lucia must never ban.</summary>
    internal static void RefuseProtected(IPNetwork target, IEnumerable<(IPNetwork Network, string What)> protectedRanges)
    {
        foreach (var (network, what) in protectedRanges)
            if (network.BaseAddress.AddressFamily == target.BaseAddress.AddressFamily
                && (network.Contains(target.BaseAddress) || target.Contains(network.BaseAddress)))
                throw new HardwareOnboardingException(409, "protected_address",
                    $"Lucia never bans {Show(target)}: {(target.PrefixLength < network.PrefixLength ? "it covers " + Show(network) + ", which is" : "it's")} {what}.");
    }

    internal static IPNetwork? Network(string entry)
    {
        var text = entry.Trim();
        if (text.Contains('/')) return IPNetwork.TryParse(text, out var network) ? network : null;
        return IPAddress.TryParse(text, out var address) ? new IPNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128) : null;
    }

    private static string Show(IPNetwork network) =>
        network.PrefixLength == (network.BaseAddress.AddressFamily == AddressFamily.InterNetwork ? 32 : 128) ? network.BaseAddress.ToString() : network.ToString();

    /// <summary>
    /// The local API path for an alert query, with the kind and network to keep and how many to return. CrowdSec 1.8.1's own
    /// ip and range filters miss AppSec alerts, so an address is matched by value and a network here, from the latest 500.
    /// </summary>
    internal static (string Path, string? Kind, IPNetwork? Range, int Take) AlertsPath(CrowdSecAlertQuery query)
    {
        static HardwareOnboardingException Bad(string code, string message) => new(400, code, message);
        var take = Math.Clamp(query.Limit ?? 50, 1, 200);
        var kind = (query.Kind ?? "").Trim().ToLowerInvariant() switch
        {
            "" => null,
            "crowdsec" or "scenario" => "crowdsec",
            "waf" or "appsec" => "waf",
            _ => throw Bad("invalid_kind", "Ask for kind crowdsec (scenarios) or waf (AppSec blocks), or leave it out for both."),
        };
        IPNetwork? filter = null;
        if (!string.IsNullOrWhiteSpace(query.Range))
        {
            var range = query.Range.Trim();
            if (!IPNetwork.TryParse(range, out var network) || !range.Contains('/') || !IPAddress.TryParse(range[..range.IndexOf('/')], out var written) || !written.Equals(network.BaseAddress))
                throw Bad("invalid_range", $"{range} isn't a network. Write it like 203.0.113.0/24.");
            filter = network;
        }
        var parts = new List<string> { "include_capi=false", "limit=" + (kind is null && filter is null ? take : 500).ToString(CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(query.Ip))
        {
            if (!IPAddress.TryParse(query.Ip.Trim(), out var ip) || query.Ip.Contains('%')) throw Bad("invalid_address", $"{query.Ip.Trim()} isn't an IP address.");
            parts.Add("value=" + Uri.EscapeDataString(ip.ToString()));
        }
        if (!string.IsNullOrWhiteSpace(query.Scenario))
        {
            if (!ScenarioName().IsMatch(query.Scenario.Trim())) throw Bad("invalid_scenario", "Give a scenario's name, such as crowdsecurity/http-probing.");
            parts.Add("scenario=" + Uri.EscapeDataString(query.Scenario.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(query.Since))
        {
            var match = SinceText().Match(query.Since.Trim().ToLowerInvariant());
            if (!match.Success) throw Bad("invalid_since", "Give how far back to look like 30m, 6h or 7d.");
            var amount = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var since = match.Groups[2].Value switch { "m" => TimeSpan.FromMinutes(amount), "h" => TimeSpan.FromHours(amount), _ => TimeSpan.FromDays(amount) };
            if (since <= TimeSpan.Zero || since > MaxSince) throw Bad("invalid_since", "Look back at most 30 days.");
            parts.Add("since=" + ((long)since.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m");
        }
        return ("/v1/alerts?" + string.Join('&', parts), kind, filter, take);
    }

    [GeneratedRegex(@"\A(?:(\d+)d)?(?:(\d+)h)?(?:(\d+)m)?\z")] private static partial Regex DurationText();
    [GeneratedRegex(@"\A(\d{1,5})(m|h|d)\z")] private static partial Regex SinceText();
    [GeneratedRegex(@"\A[A-Za-z0-9_.:/-]{1,128}\z")] private static partial Regex ScenarioName();
}
