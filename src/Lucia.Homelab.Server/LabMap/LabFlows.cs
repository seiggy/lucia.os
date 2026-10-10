using System.Net;
using System.Net.Sockets;

namespace Lucia.Homelab.Server.LabMap;

/// <summary>NetFlow's samples joined to the map: flows between objects and out to internet destinations, each object's mix and server ports.</summary>
internal sealed record LabFlowView(LabFlow[] Flows, Dictionary<string, Dictionary<string, double>> Mix, Dictionary<string, LabListen[]> Listening,
    LabDestination[] Destinations);

internal static class LabFlows
{
    public const int MaxFlows = 40, MaxDestinations = 40, MaxListening = 12, MaxDomains = 3;

    /// <summary>The destination cap a client asked for: 40, 100, 1000, or 0 for every one NetFlow's series can hold.</summary>
    internal static int Sites(int? requested) => requested switch { 100 or 1000 => requested.Value, 0 => PrometheusQuery.MaxSeries, _ => MaxDestinations };

    /// <summary>
    /// Joins samples to objects by address. Both directions of a conversation fold together; a sample's server port belongs to the
    /// end the collector named. Samples through the gateway's own internet address are NAT's second copy and are left out.
    /// </summary>
    /// <param name="nameOf">The name AdGuard last resolved to an internet address.</param>
    /// <param name="ownerOf">The network that holds an internet address.</param>
    /// <param name="districts">The owner's district by destination key, over Lucia's guess.</param>
    public static LabFlowView Aggregate(IEnumerable<LabFlowSample> samples, IReadOnlyDictionary<string, string> addresses, string? wanAddress,
        Func<IPAddress, string?> nameOf, Func<IPAddress, AsnOwner?> ownerOf, int sites = MaxDestinations, IReadOnlyDictionary<string, string>? districts = null)
    {
        var mix = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        var listening = new Dictionary<(string Id, int Port, string Protocol), double>();
        var links = new Dictionary<(string From, string To), Link>();
        var remote = new List<(string? Local, IPAddress Far, bool LocalServes, LabFlowSample Sample)>();
        var places = new Dictionary<IPAddress, (string Key, string? Name, AsnOwner? Owner)>();
        var gateway = TrafficKinds.Address(wanAddress);
        string? Resolve(IPAddress ip) => addresses.GetValueOrDefault(ip.ToString());

        void Add(string id, string service, double bps)
        {
            if (!mix.TryGetValue(id, out var services)) mix[id] = services = new(StringComparer.Ordinal);
            services[service] = services.GetValueOrDefault(service) + bps;
        }
        void Listen(string? id, LabFlowSample sample)
        {
            if (id is not null && sample.Port is { } port && sample.Protocol is "tcp" or "udp")
                listening[(id, port, sample.Protocol)] = listening.GetValueOrDefault((id, port, sample.Protocol)) + sample.Bps;
        }
        void Link(string from, string to, string service, double bps, string? label)
        {
            if (!links.TryGetValue((from, to), out var link)) links[(from, to)] = link = new();
            link.Add(service, bps, label);
        }

        foreach (var sample in samples)
        {
            if (sample.Bps <= 0 || TrafficKinds.Address(sample.Source) is not { } source || TrafficKinds.Address(sample.Destination) is not { } destination
                || gateway is not null && (source.Equals(gateway) || destination.Equals(gateway)))
                continue;
            if (TrafficKinds.Lan(source) && TrafficKinds.Lan(destination))
            {
                var (from, to) = (Resolve(source), Resolve(destination));
                var service = TrafficKinds.Service(sample.Port, sample.Protocol);
                if (from is not null) Add(from, service, sample.Bps);
                if (to is not null && to != from) Add(to, service, sample.Bps);
                Listen(sample.Server switch { "src" => from, "dst" => to, _ => null }, sample);
                if (from is not null && to is not null && from != to) Link(from, to, service, sample.Bps, $"{sample.Source} → {sample.Destination} · {service}");
                continue;
            }
            // The internet end: the public one, or with both public (an IPv6 client), the one serving the port.
            var outward = TrafficKinds.Public(destination) && (!TrafficKinds.Public(source) || sample.Server == "dst");
            var inward = TrafficKinds.Public(source) && (!TrafficKinds.Public(destination) || sample.Server == "src");
            if (outward == inward || !TrafficKinds.Lan(outward ? source : destination) && !TrafficKinds.Public(outward ? source : destination)) continue;
            var (local, far) = outward ? (source, destination) : (destination, source);
            if (!places.TryGetValue(far, out var place))
            {
                var name = nameOf(far)?.Trim().TrimEnd('.').ToLowerInvariant();
                var owner = ownerOf(far);
                places[far] = place = (TrafficKinds.Registrable(name) ?? (owner is not null ? "as" + owner.Asn : Block(far)), name, owner);
            }
            remote.Add((Resolve(local), far, sample.Server == (outward ? "src" : "dst"), sample));
        }

        // Destinations are categorised first: streaming and gaming decide their flows' service.
        var destinations = remote.GroupBy(item => places[item.Far].Key, StringComparer.Ordinal).Select(group =>
        {
            var names = group.Where(item => places[item.Far].Name is not null).GroupBy(item => places[item.Far].Name!, StringComparer.Ordinal)
                .Select(byName => (Name: byName.Key, Bps: byName.Sum(item => item.Sample.Bps)))
                .OrderByDescending(item => item.Bps).ThenBy(item => item.Name, StringComparer.Ordinal).Select(item => item.Name).ToArray();
            var owner = group.Where(item => places[item.Far].Owner is not null).GroupBy(item => places[item.Far].Owner!)
                .MaxBy(byOwner => byOwner.Sum(item => item.Sample.Bps))?.Key;
            var chosen = districts?.GetValueOrDefault(group.Key);
            var category = chosen ?? TrafficKinds.Category(names, owner?.Org);
            var name = names.Length > 0 && TrafficKinds.Registrable(names[0]) == group.Key ? group.Key : owner?.Org ?? (owner is not null ? "AS" + owner.Asn : group.Key);
            var items = group.Select(item => (item.Local, item.LocalServes, item.Sample,
                Service: category is "streaming" or "gaming" ? category : TrafficKinds.Service(item.Sample.Port, item.Sample.Protocol))).ToArray();
            return (Key: group.Key, Name: name, Names: names, Owner: owner, Category: category, Chosen: chosen is not null, Items: items, Bps: items.Sum(item => item.Sample.Bps));
        }).OrderByDescending(item => item.Bps).ThenBy(item => item.Key, StringComparer.Ordinal).ToArray();
        var top = destinations.Take(sites).Select(item => new LabDestination("dest:" + item.Key, item.Name, item.Category, item.Owner?.Org,
            item.Owner is { } owner ? "AS" + owner.Asn : null, [.. item.Names.Take(MaxDomains)], Math.Round(item.Bps),
            Round(item.Items.GroupBy(entry => entry.Service, StringComparer.Ordinal).ToDictionary(byService => byService.Key, byService => byService.Sum(entry => entry.Sample.Bps), StringComparer.Ordinal)),
            item.Chosen))
            .ToArray();
        foreach (var (destination, rank) in destinations.Select((item, rank) => (item, rank)))
            foreach (var (local, serves, sample, service) in destination.Items)
            {
                if (local is null) continue;
                Add(local, service, sample.Bps);
                if (serves) Listen(local, sample);
                if (rank < sites) Link(local, "dest:" + destination.Key, service, sample.Bps, $"{destination.Name} · {service}");
                else Link(local, LabMapService.Wan, service, sample.Bps, null);
            }
        var flows = links.Select(item => (item.Key, Bps: item.Value.Bps, Service: item.Value.Service, item.Value.Label))
            .OrderByDescending(item => item.Bps).ThenBy(item => item.Key.From, StringComparer.Ordinal).ThenBy(item => item.Key.To, StringComparer.Ordinal)
            .Take(sites <= MaxDestinations ? MaxFlows : sites + MaxFlows)
            .Select(item => new LabFlow(item.Key.From, item.Key.To,
                item.Key.To == LabMapService.Wan ? $"Internet · {item.Service}" : item.Label, Math.Round(item.Bps), item.Service))
            .ToArray();
        return new(flows, mix.ToDictionary(item => item.Key, item => Round(item.Value), StringComparer.Ordinal),
            listening.GroupBy(item => item.Key.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group
                .OrderByDescending(item => item.Value).ThenBy(item => item.Key.Port).Take(MaxListening)
                .Select(item => new LabListen(item.Key.Port, item.Key.Protocol, TrafficKinds.Service(item.Key.Port, item.Key.Protocol), Math.Round(item.Value)))
                .ToArray(), StringComparer.Ordinal),
            top);
    }

    private static Dictionary<string, double> Round(Dictionary<string, double> services) =>
        services.Where(item => item.Value > 0).OrderByDescending(item => item.Value)
            .ToDictionary(item => item.Key, item => Math.Round(item.Value), StringComparer.Ordinal);

    /// <summary>An address's /24, or /48 for IPv6.</summary>
    internal static string Block(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        var (keep, bits) = ip.AddressFamily == AddressFamily.InterNetwork ? (3, 24) : (6, 48);
        for (var i = keep; i < bytes.Length; i++) bytes[i] = 0;
        return $"{new IPAddress(bytes)}/{bits}";
    }

    /// <summary>A link's traffic: its total and per-service rates, and the label of its busiest sample.</summary>
    private sealed class Link
    {
        private readonly Dictionary<string, double> _services = new(StringComparer.Ordinal);
        private double _busiest;
        public double Bps { get; private set; }
        public string? Label { get; private set; }
        public string Service => _services.MaxBy(item => item.Value).Key;

        public void Add(string service, double bps, string? label)
        {
            Bps += bps;
            _services[service] = _services.GetValueOrDefault(service) + bps;
            if (bps > _busiest) (_busiest, Label) = (bps, label);
        }
    }
}
