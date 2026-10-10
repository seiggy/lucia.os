using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Lucia.Homelab.Server.LabMap;

/// <summary>
/// The lab map's traffic types: a flow's service from its server port, an internet destination's category from its names, and
/// which addresses are the LAN's.
/// </summary>
public static class TrafficKinds
{
    public const string Other = "other";
    public static readonly string[] Services = ["web", "dns", "streaming", "gaming", "remote", "vpn", "mail", "files", "iot", "time", Other];
    public static readonly string[] Categories = ["streaming", "gaming", "social", "cloud", "cdn", "comms", "updates", Other];

    private static readonly Dictionary<int, string> Ports = new (string Service, int[] Ports)[]
    {
        ("web", [80, 443, 8080, 8443]),
        ("dns", [53, 853]),
        ("streaming", [554, 1935, 32400, 8009, 8096, 8920]),
        ("gaming", [3074, 3075, 3478, 3479, 3480, .. Enumerable.Range(27000, 51), .. Enumerable.Range(9295, 10)]),
        ("remote", [22, 23, 3389, 5900, 5901, 5938]),
        ("vpn", [500, 4500, 1194, 1701, 1723, 51820, 41641]),
        ("mail", [25, 110, 143, 465, 587, 993, 995]),
        ("files", [20, 21, 139, 445, 548, 873, 2049, 111]),
        ("iot", [1883, 8883, 5353, 1900, 5683, 6053, 8123, 1400, 1925]),
        ("time", [123]),
    }.SelectMany(item => item.Ports.Select(port => (Port: port, item.Service))).ToDictionary(item => item.Port, item => item.Service);

    // Checked in this order, so a specific match (googlevideo) wins over a generic one (google).
    private static readonly (string Category, string[] Keywords)[] Keywords =
    [
        ("streaming", ["netflix", "nflx", "youtube", "googlevideo", "ytimg", "twitch", "spotify", "scdn", "disney", "hulu", "hbo", "max.com", "primevideo",
            "aiv-cdn", "plex", "crunchyroll", "paramount", "peacock"]),
        ("gaming", ["steam", "valve", "xbox", "xboxlive", "playstation", "sony interactive", "epicgames", "riot", "nintendo", "blizzard", "battle.net",
            "ea.com", "ubisoft", "roblox"]),
        ("social", ["facebook", "fbcdn", "instagram", "meta", "tiktok", "bytedance", "twitter", "x.com", "twimg", "reddit", "snapchat", "pinterest", "linkedin"]),
        ("comms", ["discord", "zoom", "slack", "teams", "whatsapp", "signal", "telegram", "webex", "skype"]),
        ("updates", ["windowsupdate", "update.microsoft", "swcdn", "mzstatic", "ubuntu", "debian", "archive", "docker.io", "ghcr", "github", "npmjs", "pypi",
            "nvidia"]),
        ("cdn", ["cloudflare", "akamai", "fastly", "edgecast", "cloudfront", "limelight", "bunny", "stackpath"]),
        ("cloud", ["amazon", "aws", "google", "microsoft", "azure", "oracle", "digitalocean", "linode", "hetzner", "ovh", "apple"]),
    ];

    private static readonly HashSet<string> SecondLevel = new(StringComparer.Ordinal)
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk", "me.uk", "com.au", "net.au", "org.au", "co.nz", "co.jp", "ne.jp", "or.jp", "com.br", "com.cn", "com.mx",
        "co.in", "co.za", "com.tr", "com.sg", "com.hk", "co.kr", "com.tw",
    };

    /// <summary>The service behind a server port, or <c>other</c> without one.</summary>
    public static string Service(int? port, string? protocol)
    {
        if (port is not { } number || protocol is not (null or "" or "tcp" or "udp")) return Other;
        if (Ports.TryGetValue(number, out var service)) return service;
        if (protocol == "udp" && number is 5222 or 5223) return "gaming";
        return protocol != "udp" && number is >= 8000 and <= 8099 ? "web" : Other;
    }

    /// <summary>An internet destination's category: its names decide first, then its network's owner.</summary>
    public static string Category(IEnumerable<string> domains, string? org)
    {
        foreach (var domain in domains)
            if (Match(domain) is { } category) return category;
        return org is not null && Match(org) is { } owner ? owner : Other;
    }

    // Labels or words are compared from their start: "steam" finds steamcontent.com, and "x.com" doesn't find netflix.com.
    private static string? Match(string text)
    {
        var haystack = Normal(text, edges: true);
        foreach (var (category, keywords) in Keywords)
            foreach (var keyword in keywords)
            {
                var needle = "." + Normal(keyword, edges: false);
                if (needle.IndexOf('.', 1) >= 0 ? haystack.Contains(needle + ".", StringComparison.Ordinal) : haystack.Contains(needle, StringComparison.Ordinal))
                    return category;
            }
        return null;
    }

    private static string Normal(string text, bool edges)
    {
        var chars = text.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '.').ToArray();
        return edges ? "." + new string(chars) + "." : new string(chars);
    }

    /// <summary>A name's registrable domain: its last two labels, or three under a common two-level suffix such as co.uk.</summary>
    public static string? Registrable(string? name)
    {
        var labels = name?.Trim().TrimEnd('.').ToLowerInvariant().Split('.');
        if (labels is not { Length: >= 2 } || labels.Any(label => label.Length == 0)) return null;
        var take = labels.Length >= 3 && SecondLevel.Contains(labels[^2] + "." + labels[^1]) ? 3 : 2;
        return string.Join('.', labels[^take..]);
    }

    /// <summary>Docker's published ports (<c>0.0.0.0:8080-&gt;80/tcp, [::]:8080-&gt;80/tcp</c>) as host ports, deduplicated and sorted.</summary>
    public static MapListen[] Listens(string? ports)
    {
        if (string.IsNullOrWhiteSpace(ports)) return [];
        var found = new HashSet<(int Port, string Protocol)>();
        foreach (var entry in ports.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(64))
        {
            var arrow = entry.IndexOf("->", StringComparison.Ordinal);
            if (arrow < 0) continue;
            var slash = entry.LastIndexOf('/');
            var protocol = slash > arrow ? entry[(slash + 1)..].ToLowerInvariant() : "tcp";
            var host = entry[..arrow];
            var range = host[(host.LastIndexOf(':') + 1)..].Split('-');
            if (protocol is not ("tcp" or "udp") || range.Length > 2 || Port(range[0]) is not { } first) continue;
            var last = range.Length == 2 ? Port(range[1]) ?? 0 : first;
            for (var port = first; port <= Math.Min(last, first + 31) && found.Count < 64; port++) found.Add((port, protocol));
        }
        return [.. found.OrderBy(item => item.Port).ThenBy(item => item.Protocol, StringComparer.Ordinal)
            .Select(item => new MapListen(item.Port, item.Protocol, Service(item.Port, item.Protocol)))];
    }

    private static int? Port(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535 ? port : null;

    /// <summary>An address as the collector labels it, with IPv4-mapped IPv6 unwrapped.</summary>
    internal static IPAddress? Address(string? text) =>
        IPAddress.TryParse(text, out var ip) ? ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip : null;

    /// <summary>Private, link-local and shared (CGNAT) IPv4, IPv6 ULA and link-local.</summary>
    internal static bool Lan(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork
            ? b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254 || b[0] == 100 && b[1] is >= 64 and <= 127
            : (b[0] & 0xfe) == 0xfc || b[0] == 0xfe && (b[1] & 0xc0) == 0x80;
    }

    /// <summary>A routable internet address: not the LAN's, loopback, multicast, broadcast or reserved.</summary>
    internal static bool Public(IPAddress ip)
    {
        if (Lan(ip) || IPAddress.IsLoopback(ip)) return false;
        var b = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork ? b[0] is not (0 or >= 224) : ip.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xe0) == 0x20;
    }
}
