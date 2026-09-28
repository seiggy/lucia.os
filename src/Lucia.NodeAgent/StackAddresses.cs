using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lucia.NodeAgent;

/// <param name="State"><c>Held</c>, <c>InUse</c> (another device answers for it) or <c>NoSubnet</c>.</param>
internal sealed record NodeAddressStatus(string Address, string State, string? Message = null);
internal sealed record OwnedAddress(string Address, string Interface, int Prefix);

/// <summary>
/// Extra IPv4 addresses that stacks own, such as a DNS server's. This server takes one only after checking that no other
/// device answers for it, adds it beside its own address on the matching network, and announces it. Addresses aren't
/// persistent: a reboot drops them and the next sync takes them again after the same check.
/// </summary>
internal static partial class StackAddresses
{
    private const string Ip = "/usr/bin/ip", Arping = "/usr/bin/arping";
    private const string OwnedPath = "/run/lucia-agent/addresses.json";
    internal const string Waiting = "Waiting for this server to take the address ";
    private static readonly ConcurrentDictionary<string, NodeAddressStatus> status = new();
    private static readonly SemaphoreSlim install = new(1, 1);

    internal static NodeAddressStatus[] Report() => [.. status.Values.Take(64)];
    internal static bool Held(string address) => status.TryGetValue(address, out var item) && item.State == "Held";
    internal static string Problem(string address) =>
        status.TryGetValue(address, out var item) && item.Message is { } message ? $"{Waiting}{address}. {message}" : $"{Waiting}{address}.";

    /// <summary>Takes the addresses running stacks want and gives back every other address this agent added.</summary>
    internal static async Task ReconcileAsync(IEnumerable<string> addresses, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) return;
        var wanted = addresses.Where(item => Valid(item) is not null).Distinct().Take(16).ToArray();
        var owned = ReadOwned();
        if (wanted.Length == 0 && owned.Count == 0) { status.Clear(); return; }
        var links = ParseLinks(await Commands.RunAsync(Ip, ["-j", "-4", "addr", "show"], TimeSpan.FromSeconds(10), token));
        foreach (var gone in owned.Where(item => !wanted.Contains(item.Address)).ToArray())
        {
            if (links.Any(link => link.Name == gone.Interface && link.Addresses.Any(item => item.Local == gone.Address)))
                await Commands.CaptureAsync(Ip, ["addr", "del", $"{gone.Address}/{gone.Prefix}", "dev", gone.Interface], TimeSpan.FromSeconds(10), token,
                    64 * 1024, failOnError: false);
            owned.Remove(gone);
            status.TryRemove(gone.Address, out _);
            Console.Error.WriteLine($"Released the address {gone.Address}.");
        }
        foreach (var stale in status.Keys.Except(wanted)) status.TryRemove(stale, out _);
        if (wanted.Length > 0) AllowNonlocalBind();
        foreach (var address in wanted)
        {
            var mine = owned.FirstOrDefault(item => item.Address == address);
            var present = links.FirstOrDefault(link => link.Addresses.Any(item => item.Local == address));
            if (present is not null)
            {
                status[address] = mine is not null ? new(address, "Held")
                    : new(address, "InUse", "This server already uses it as its own address.");
                continue;
            }
            if (Network(links, address) is not { } network)
            {
                status[address] = new(address, "NoSubnet", "None of this server's networks contains it.");
                continue;
            }
            await InstallAsync(token);
            var probe = await Commands.CaptureAsync(Arping, ["-D", "-c", "2", "-w", "3", "-I", network.Interface, address], TimeSpan.FromSeconds(10), token,
                64 * 1024, failOnError: false);
            if (probe.Exit != 0)
            {
                var mac = MacPattern().Match(probe.Stdout) is { Success: true } match ? match.Groups[1].Value.ToLowerInvariant() : null;
                status[address] = new(address, "InUse", mac is null
                    ? "Another device answers for it. Turn that device off or give it another address."
                    : $"The device {mac} answers for it. Turn that device off or give it another address.");
                continue;
            }
            await Commands.RunAsync(Ip, ["addr", "add", $"{address}/{network.Prefix}", "dev", network.Interface], TimeSpan.FromSeconds(10), token);
            owned.Add(new(address, network.Interface, network.Prefix));
            WriteOwned(owned);
            // Neighbours that cached the previous owner's MAC switch to this server now instead of when their entry expires.
            await Commands.CaptureAsync(Arping, ["-U", "-c", "3", "-I", network.Interface, address], TimeSpan.FromSeconds(10), token, 64 * 1024, failOnError: false);
            status[address] = new(address, "Held");
            Console.Error.WriteLine($"Took the address {address} on {network.Interface}.");
        }
        WriteOwned(owned);
    }

    /// <summary>A dotted-quad private IPv4 address, or null.</summary>
    internal static IPAddress? Valid(string? value) =>
        value is not null && QuadPattern().IsMatch(value) && IPAddress.TryParse(value, out var ip) && Private(ip) ? ip : null;

    private static bool Private(IPAddress ip) =>
        ip.GetAddressBytes() is var b && (b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168);

    internal sealed record Link(string Name, (string Local, int Prefix)[] Addresses);

    internal static Link[] ParseLinks(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(link => new Link(link.GetProperty("ifname").GetString() ?? "",
            [.. (link.TryGetProperty("addr_info", out var info) ? info.EnumerateArray() : Enumerable.Empty<JsonElement>())
                .Where(item => item.TryGetProperty("local", out _) && item.TryGetProperty("prefixlen", out _))
                .Select(item => (item.GetProperty("local").GetString() ?? "", item.GetProperty("prefixlen").GetInt32()))]))];
    }

    /// <summary>The physical network whose subnet contains <paramref name="address"/>, skipping Docker's bridges.</summary>
    internal static (string Interface, int Prefix)? Network(Link[] links, string address)
    {
        var target = IPAddress.Parse(address);
        foreach (var link in links.Where(link => !link.Name.StartsWith("docker", StringComparison.Ordinal)
            && !link.Name.StartsWith("br-", StringComparison.Ordinal) && !link.Name.StartsWith("veth", StringComparison.Ordinal) && link.Name != "lo"))
            foreach (var (local, prefix) in link.Addresses)
                if (prefix is >= 8 and <= 30 && IPAddress.TryParse(local, out var own)
                    && new IPNetwork(Mask(own, prefix), prefix).Contains(target) && !Edge(target, prefix))
                    return (link.Name, prefix);
        return null;
    }

    private static IPAddress Mask(IPAddress ip, int prefix)
    {
        var value = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt32(ip.GetAddressBytes())) & (uint.MaxValue << (32 - prefix));
        return new IPAddress(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)value)));
    }

    /// <summary>The network or broadcast address of the subnet.</summary>
    private static bool Edge(IPAddress ip, int prefix)
    {
        var host = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt32(ip.GetAddressBytes())) & ~(uint.MaxValue << (32 - prefix));
        return host == 0 || host == ~(uint.MaxValue << (32 - prefix));
    }

    // Containers publish ports on the address before this server holds it, so a restart never races the check above.
    private static void AllowNonlocalBind()
    {
        const string Setting = "net.ipv4.ip_nonlocal_bind = 1\n", Conf = "/etc/sysctl.d/60-lucia-addresses.conf";
        if (!File.Exists(Conf) || File.ReadAllText(Conf) != Setting) File.WriteAllText(Conf, Setting);
        if (File.ReadAllText("/proc/sys/net/ipv4/ip_nonlocal_bind").Trim() != "1") File.WriteAllText("/proc/sys/net/ipv4/ip_nonlocal_bind", "1");
    }

    private static async Task InstallAsync(CancellationToken token)
    {
        if (File.Exists(Arping)) return;
        await install.WaitAsync(token);
        try
        {
            if (File.Exists(Arping)) return;
            Console.Error.WriteLine("Installing arping for stack addresses.");
            await NodeRuntime.RunAsync("/usr/bin/apt-get", ["-o", "DPkg::Lock::Timeout=600", "update"], TimeSpan.FromMinutes(10), token);
            await NodeRuntime.RunAsync("/usr/bin/apt-get", ["-o", "DPkg::Lock::Timeout=600", "-o", "Dpkg::Options::=--force-confold",
                "install", "-y", "--no-install-recommends", "iputils-arping"], TimeSpan.FromMinutes(15), token);
        }
        finally { install.Release(); }
    }

    private static List<OwnedAddress> ReadOwned()
    {
        try { return JsonSerializer.Deserialize<List<OwnedAddress>>(File.ReadAllText(OwnedPath), AgentJson.Options) ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void WriteOwned(List<OwnedAddress> owned)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OwnedPath)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        StackRunner.WritePrivate(OwnedPath, JsonSerializer.Serialize(owned, AgentJson.Options));
    }

    [GeneratedRegex(@"\A(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\z")]
    private static partial Regex QuadPattern();
    [GeneratedRegex(@"\[([0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5})\]")]
    private static partial Regex MacPattern();
}
