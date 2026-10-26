using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Lucia.Homelab.Server.Security;

/// <summary>
/// The gateway's CrowdSec bouncer middlewares and the safe harbor: addresses CrowdSec never bans and the bouncer never checks.
/// The identity AppHost compiles this file too. It writes both files on every deploy, before the gateway starts with the static
/// configuration that names the middlewares, and the server rewrites them whenever the owner edits the list, from the same inputs.
/// </summary>
public static class GatewaySecurity
{
    /// <summary>The local plugin's name in the gateway's static configuration.</summary>
    public const string PluginName = "crowdsec-bouncer";
    public const string PluginModule = "github.com/maxlerebourg/crowdsec-bouncer-traefik-plugin";
    /// <summary>
    /// The plugin's pinned release. tools/identity/provision.py downloads its Go module zip and the two modules it vendors from
    /// proxy.golang.org, checks each zip's SHA-256 and unpacks them for the gateway; the AppHost checks the unpacked tree again
    /// before the gateway starts.
    /// </summary>
    public const string PluginVersion = "v1.8.1";
    /// <summary>SHA-256 of the unpacked tree's <c>sha256sum</c>-style listing (<see cref="TreeDigest"/>), vendored modules included.</summary>
    public const string PluginTreeSha256 = "8a141217a402fcccb015d7a9b3bef1d1ee078f640df2b2b305985058653e5d90";
    /// <summary>Where the gateway loads local plugins from, below its working directory.</summary>
    public const string PluginDirectory = "plugins-local/src/" + PluginModule;
    /// <summary>Ban decisions from the engine's local API, on every HTTP entrypoint.</summary>
    public const string Middleware = "crowdsec";
    /// <summary>The engine's in-band WAF: on the public entrypoint, and on app routes on the LAN. Never on Lucia's own API.</summary>
    public const string AppSecMiddleware = "crowdsec-appsec";
    public const string FileName = "crowdsec.yml";
    /// <summary>The whole harbor, one address or network per line, for the engine's allowlist.</summary>
    public const string HarborFileName = "safe-harbor.txt";
    /// <summary>The owner's part of the harbor, in the host's data directory.</summary>
    public const string StateFileName = "safe-harbor.json";
    public const string KeyFile = "/run/secrets/crowdsec-bouncer-key";
    public const string Engine = "identity-crowdsec";
    public const int MaxOwnerEntries = 32;
    public static readonly string[] Loopback = ["127.0.0.0/8", "::1"];

    private static readonly IPNetwork[] PrivateNetworks = [.. new[]
    {
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "169.254.0.0/16", "fc00::/7", "fe80::/10",
    }.Select(IPNetwork.Parse)];

    /// <summary>
    /// One owner entry as an address or network in canonical form, or null with the reason. Private networks may be as wide
    /// as their range; anything else must be a single site (IPv4 /24, IPv6 /48 or narrower), so the list can never trust the
    /// internet. Cloudflare's proxies are refused: every public visitor arrives through them.
    /// </summary>
    public static string? Canonical(string? value, IEnumerable<string> cloudflare, out string? error)
    {
        error = null;
        var text = (value ?? "").Trim();
        IPNetwork network;
        if (text.Length is 0 or > 64 || text.Contains('%')) { error = "Enter an IP address or a network such as 192.168.1.0/24."; return null; }
        if (text.Contains('/'))
        {
            if (!IPNetwork.TryParse(text, out network) || !IPAddress.TryParse(text[..text.IndexOf('/')], out var written)
                || !written.Equals(network.BaseAddress))
            {
                error = $"{text} isn't a network. Write it like 192.168.1.0/24, with the host part of the address set to zero.";
                return null;
            }
        }
        else if (!text.Contains('%') && IPAddress.TryParse(text, out var single))
            network = new IPNetwork(single, single.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
        else { error = $"{text} isn't an IP address."; return null; }
        var address = network.BaseAddress;
        var v4 = address.AddressFamily == AddressFamily.InterNetwork;
        if (address.IsIPv4MappedToIPv6) { error = $"Write {text} as a plain IPv4 address."; return null; }
        if (network.PrefixLength == 0 || IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || (v4 ? address.GetAddressBytes()[0] >= 224 : address.GetAddressBytes()[0] == 0xff))
        {
            error = $"{text} can't be trusted: it isn't one device or network.";
            return null;
        }
        var isPrivate = PrivateNetworks.Any(range => range.BaseAddress.AddressFamily == address.AddressFamily
            && range.Contains(address) && network.PrefixLength >= range.PrefixLength);
        if (!isPrivate && network.PrefixLength < (v4 ? 24 : 48))
        {
            error = $"{text} is too wide. Trust a single internet address or a small network (/{(v4 ? 24 : 48)} or narrower).";
            return null;
        }
        if (cloudflare.Select(IPNetwork.Parse).Any(range => Overlaps(range, network)))
        {
            error = $"{text} belongs to Cloudflare, which every public visitor comes through.";
            return null;
        }
        return Format(network);
    }

    /// <summary>Lucia's own addresses (loopback, its Docker networks, its LAN address) and the owner's list, without duplicates.</summary>
    public static string[] Harbor(IEnumerable<string> lucia, IEnumerable<string> owner)
    {
        var result = new List<string>();
        foreach (var entry in Loopback.Concat(lucia).Concat(owner))
        {
            var text = entry.Trim();
            if (text.Length == 0) continue;
            var network = text.Contains('/') ? IPNetwork.Parse(text)
                : new IPNetwork(IPAddress.Parse(text), IPAddress.Parse(text).AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
            var formatted = Format(network);
            if (!result.Contains(formatted, StringComparer.Ordinal)) result.Add(formatted);
        }
        return [.. result];
    }

    /// <summary>
    /// The two bouncer middlewares. Both fail open: a stream that can't reach the engine keeps the decisions it has and lets
    /// everyone else through, and an engine that can't answer an AppSec query lets the request through. Visitors are read from
    /// X-Forwarded-For only behind Cloudflare; the gateway drops that header from everyone else before this runs.
    /// </summary>
    public static string Render(IReadOnlyCollection<string> harbor, IReadOnlyCollection<string> cloudflare)
    {
        Dictionary<string, object> Common(string mode) => new()
        {
            ["enabled"] = true,
            ["logLevel"] = "INFO",
            ["crowdsecMode"] = mode,
            ["crowdsecLapiScheme"] = "http",
            ["crowdsecLapiHost"] = Engine + ":8080",
            ["crowdsecLapiKeyFile"] = KeyFile,
            ["httpTimeoutSeconds"] = 5,
            ["forwardedHeadersTrustedIps"] = cloudflare,
            ["clientTrustedIps"] = harbor,
        };
        var decisions = Common("stream");
        decisions["updateIntervalSeconds"] = 15;
        decisions["updateMaxFailure"] = -1;
        decisions["streamStartupBlock"] = false;
        var appsec = Common("appsec");
        appsec["crowdsecAppsecEnabled"] = true;
        appsec["crowdsecAppsecScheme"] = "http";
        appsec["crowdsecAppsecHost"] = Engine + ":7422";
        appsec["crowdsecAppsecFailureBlock"] = false;
        appsec["crowdsecAppsecUnreachableBlock"] = false;
        // HTTP/2 uploads without a length can't be read ahead; they are checked on their headers alone.
        appsec["crowdsecAppsecUnreadableBodyBlock"] = false;
        appsec["crowdsecAppsecBodyLimit"] = 10485760;
        // A hung engine delays each checked request by at most this long, then lets it through.
        appsec["httpTimeoutSeconds"] = 2;
        return JsonSerializer.Serialize(new
        {
            http = new
            {
                middlewares = new Dictionary<string, object>
                {
                    [Middleware] = new { plugin = new Dictionary<string, object> { [PluginName] = decisions } },
                    [AppSecMiddleware] = new { plugin = new Dictionary<string, object> { [PluginName] = appsec } },
                },
            },
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>The owner's saved entries; none when the file doesn't exist yet.</summary>
    public static string[] ReadOwner(string path)
    {
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("The safe-harbor list is oversized.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1
            || !root.TryGetProperty("addresses", out var addresses) || addresses.ValueKind != JsonValueKind.Array
            || addresses.GetArrayLength() > MaxOwnerEntries)
            throw new InvalidDataException("The safe-harbor list is invalid.");
        return [.. addresses.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()!
            : throw new InvalidDataException("The safe-harbor list is invalid."))];
    }

    /// <summary>Writes the middlewares and the engine's list atomically, then touches the directory so the gateway reloads.</summary>
    public static bool Publish(string directory, IReadOnlyCollection<string> harbor, IReadOnlyCollection<string> cloudflare)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("The gateway security directory must be absolute.");
        Directory.CreateDirectory(directory);
        var changed = Write(Path.Combine(directory, HarborFileName), string.Concat(harbor.Select(entry => entry + "\n")));
        changed |= Write(Path.Combine(directory, FileName), Render(harbor, cloudflare));
        // The gateway watches only its top-level directory; a changed time on this one is an event there.
        if (changed) Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow);
        return changed;
    }

    private static bool Write(string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("Gateway security files must not be symbolic links.");
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return false;
        var pending = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}-{Guid.NewGuid():N}.pending");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(pending, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(pending, path, overwrite: true);
            return true;
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    /// <summary>
    /// SHA-256 of "hash  path\n" for every regular file below <paramref name="root"/>, paths relative with forward slashes and
    /// sorted ordinally. Links and other special files are refused.
    /// </summary>
    public static string TreeDigest(string root)
    {
        var files = new List<string>();
        void Walk(string directory)
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("The plugin tree must not contain links.");
                if (entry is DirectoryInfo) Walk(entry.FullName);
                else files.Add(Path.GetRelativePath(root, entry.FullName).Replace('\\', '/'));
            }
        }
        Walk(root);
        files.Sort(StringComparer.Ordinal);
        var listing = new StringBuilder();
        foreach (var file in files)
            listing.Append(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(root, file)))))
                .Append("  ").Append(file).Append('\n');
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(listing.ToString())));
    }

    private static string Format(IPNetwork network) =>
        network.PrefixLength == (network.BaseAddress.AddressFamily == AddressFamily.InterNetwork ? 32 : 128)
            ? network.BaseAddress.ToString() : network.ToString();

    private static bool Overlaps(IPNetwork left, IPNetwork right) =>
        left.BaseAddress.AddressFamily == right.BaseAddress.AddressFamily
        && (left.Contains(right.BaseAddress) || right.Contains(left.BaseAddress));
}
