using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Stacks;
using Lucia.Homelab.Server.Telemetry;

namespace Lucia.Homelab.Server.LabMap;

/// <summary>
/// Builds the lab map from UniFi's devices, networks and clients, Lucia's servers, apps and NAS shares, and Prometheus' rates.
/// Without UniFi it still draws Lucia's own servers, under a stand-in gateway <c>gw</c>.
/// </summary>
public sealed partial class LabMapService(UniFiConnectionService unifi, ManagedNodeEnrollment nodes, HardwareOnboardingStore hardware,
    StackStore stacks, ClientOverrideStore overrides, CategoryStore categories, SiteDistrictStore siteDistricts, DomainOnboardingOptions domains, SparkTelemetryBuffer spark, SparkTelemetryOptions sparkOptions,
    LabTraffic traffic, LabDnsNames names, LabAsn asn, ILogger<LabMapService> logger)
{
    public const string SparkId = "host:spark", StandInGateway = "gw", Wan = "wan", OtherNetwork = "net:other";
    internal const int MaxClients = 2000;
    /// <summary>Offline clients UniFi hasn't seen for this long drop off the map.</summary>
    internal static readonly TimeSpan ClientLinger = TimeSpan.FromDays(30);
    private readonly SemaphoreSlim _unifiGate = new(1, 1), _viewGate = new(1, 1);
    private (DateTimeOffset At, UniFiTopology? Topology, string State, string? Message)? _unifi;
    private (DateTimeOffset At, Built Map)? _view;

    /// <summary>A built map with what its live readings are joined on.</summary>
    internal sealed record Built(LabMapView View, Dictionary<string, string> HostClients, Dictionary<string, string> Addresses,
        Dictionary<string, (double Rx, double Tx)> ClientRates, Dictionary<string, (double Rx, double Tx)> DeviceRates, (double Rx, double Tx)? WanRate);

    /// <summary>The structure, rebuilt at most once a minute.</summary>
    public async Task<LabMapView> Get(CancellationToken ct) => (await Structure(ct)).View;

    private async Task<Built> Structure(CancellationToken ct)
    {
        await _viewGate.WaitAsync(ct);
        try
        {
            if (_view is { } view && DateTimeOffset.UtcNow - view.At < TimeSpan.FromSeconds(60)) return view.Map;
            var built = await Build(TimeSpan.FromSeconds(60), ct);
            _view = (DateTimeOffset.UtcNow, built);
            return built;
        }
        finally { _viewGate.Release(); }
    }

    /// <summary>UniFi's devices when UniFi answered, for the collector's SNMP targets; null when it didn't.</summary>
    internal async Task<MapDevice[]?> Devices(CancellationToken ct)
    {
        var view = await Get(ct);
        return view.Unifi.State != "connected" ? null : [.. view.Gateway is { } gateway && gateway.Id != StandInGateway ? [gateway] : Array.Empty<MapDevice>(), .. view.Devices];
    }

    /// <summary>Rates, load and health now: UniFi's own numbers at most ten seconds old, Prometheus' where it answers.</summary>
    public async Task<LabMapLive> Live(string? window, int? sites, CancellationToken ct)
    {
        var built = await Build(TimeSpan.FromSeconds(10), ct);
        var view = built.View;
        var cap = LabFlows.Sites(sites);
        var reading = await traffic.Read(ct, window ?? "1m", cap);
        var rates = new Dictionary<string, LabRate>(StringComparer.Ordinal);
        var load = new Dictionary<string, LabLoad>(StringComparer.Ordinal);

        // UniFi's rates first, so Prometheus' replace them where it has the same object.
        UnifiRates(built, rates);
        if (spark.Snapshot(sparkOptions.Enabled).Latest is { ReceiveBytesPerSecond: { } sparkRx, TransmitBytesPerSecond: { } sparkTx })
            rates[SparkId] = Bits((sparkRx, sparkTx));

        foreach (var host in view.Hosts)
            if (host.CpuPercent is not null || host.MemoryPercent is not null) load[host.Id] = new(host.CpuPercent, host.MemoryPercent);
        var flows = new LabFlowView([], [], [], []);
        if (reading is not null)
        {
            foreach (var host in view.Hosts.Where(host => host.Kind == "node"))
                if (reading.Nodes.TryGetValue(host.Name, out var rate)) rates[host.Id] = rate;
            foreach (var (device, rate) in reading.Devices)
                if (device.StartsWith("dev:", StringComparison.Ordinal)) rates[device] = rate;
            foreach (var app in view.Apps)
            {
                var host = view.Hosts.First(item => item.Id == app.HostId);
                var appRates = new List<LabRate>();
                var appLoad = new List<LabLoad>();
                foreach (var container in app.Containers)
                {
                    if (reading.Containers.TryGetValue((host.Name, container.Name), out var rate)) { rates[container.Id] = rate; appRates.Add(rate); }
                    if (reading.ContainerLoad.TryGetValue((host.Name, container.Name), out var used)) { load[container.Id] = used; appLoad.Add(used); }
                }
                if (appRates.Count > 0) rates[app.Id] = new(appRates.Sum(item => item.RxBps), appRates.Sum(item => item.TxBps));
                if (appLoad.Count > 0)
                    load[app.Id] = new(appLoad.Any(item => item.CpuPercent is not null) ? Math.Round(appLoad.Sum(item => item.CpuPercent ?? 0), 1) : null,
                        appLoad.Any(item => item.MemoryPercent is not null) ? Math.Round(appLoad.Sum(item => item.MemoryPercent ?? 0), 1) : null);
            }
            if (reading.Flows.Length > 0)
            {
                names.Refresh();
                flows = LabFlows.Aggregate(reading.Flows, built.Addresses, view.Wan.Address, names.Name, asn.Find, cap, await siteDistricts.Read(ct));
            }
        }
        var health = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, value) in view.Hosts.Select(item => (item.Id, item.Health)).Concat(view.Devices.Select(item => (item.Id, item.Health)))
            .Concat(view.Apps.Select(item => (item.Id, item.Health))).Concat(view.Apps.SelectMany(app => app.Containers).Select(item => (item.Id, item.Health)))
            .Concat(view.Storage.Select(item => (item.Id, item.Health))))
            health[id] = value;
        if (view.Gateway is { } gateway) health[gateway.Id] = gateway.Health;
        health[Wan] = view.Wan.Health;
        return new(DateTimeOffset.UtcNow, reading is not null ? "full" : view.Unifi.State == "connected" ? "reduced" : "none", rates, load, health,
            flows.Flows, flows.Mix, flows.Listening, flows.Destinations);
    }

    static LabRate Bits((double Rx, double Tx) bytes) => new(Math.Round(8 * bytes.Rx), Math.Round(8 * bytes.Tx));

    /// <summary>UniFi's rates in bits per second: devices, the WAN, each client group's sum, each online client and Lucia's servers.</summary>
    internal static void UnifiRates(Built built, Dictionary<string, LabRate> rates)
    {
        foreach (var (id, rate) in built.DeviceRates) rates[id] = Bits(rate);
        if (built.WanRate is { } wan && built.View.Gateway is { } gateway) rates[gateway.Id] = Bits(wan);
        foreach (var group in built.View.ClientGroups)
        {
            var members = group.Members.Where(member => built.ClientRates.ContainsKey(member.Mac)).Select(member => built.ClientRates[member.Mac]).ToArray();
            if (members.Length > 0) rates[group.Id] = Bits((members.Sum(item => item.Rx), members.Sum(item => item.Tx)));
        }
        foreach (var (mac, rate) in built.ClientRates) rates["client:" + mac] = Bits(rate);
        foreach (var (host, mac) in built.HostClients)
            if (built.ClientRates.TryGetValue(mac, out var rate)) rates[host] = Bits(rate);
    }

    /// <summary>Sets or clears a client's group and returns the client as the map now draws it.</summary>
    public async Task<MapClient> SetGroup(string mac, string? group, CancellationToken ct)
    {
        mac = await overrides.Set(mac, group, ct);
        await _viewGate.WaitAsync(ct);
        try { _view = null; }
        finally { _viewGate.Release(); }
        var view = await Get(ct);
        return view.ClientGroups.SelectMany(item => item.Members).FirstOrDefault(client => client.Mac == mac)
            ?? new(mac, mac, null, false, group ?? ClientClassifier.Unknown, group is not null, null, null, null);
    }

    /// <summary>Sets or clears a building's category; returns the id and category as stored.</summary>
    public async Task<LabCategoryRequest> SetCategory(string id, string? category, CancellationToken ct)
    {
        (id, category) = await categories.Set(id, category, ct);
        await _viewGate.WaitAsync(ct);
        try { _view = null; }
        finally { _viewGate.Release(); }
        return new(id, category);
    }

    private async Task<(UniFiTopology? Topology, string State, string? Message)> Topology(TimeSpan age, CancellationToken ct)
    {
        await _unifiGate.WaitAsync(ct);
        try
        {
            if (_unifi is { } cached && DateTimeOffset.UtcNow - cached.At < age) return (cached.Topology, cached.State, cached.Message);
            (UniFiTopology?, string, string?) read;
            try
            {
                read = await unifi.ReadTopologyAsync(ct) is { } topology ? (topology, "connected", null)
                    : (null, "not-connected", "Connect UniFi to draw its network.");
            }
            catch (UniFiException error) { read = (null, "error", error.Message); }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("The lab map could not read UniFi ({ErrorType}).", error.GetType().Name);
                read = (null, "error", "UniFi could not be read.");
            }
            _unifi = (DateTimeOffset.UtcNow, read.Item1, read.Item2, read.Item3);
            return read;
        }
        finally { _unifiGate.Release(); }
    }

    internal async Task<Built> Build(TimeSpan unifiAge, CancellationToken ct)
    {
        var (topology, state, message) = await Topology(unifiAge, ct);
        var facts = await nodes.Facts(ct);
        var onboarding = await hardware.GetSnapshotAsync(ct);
        var lab = await stacks.LabStacks(ct);
        var groups = await overrides.Read(ct);
        var chosen = await categories.Read(ct);
        var reading = await traffic.Read(ct);
        var sparkLatest = spark.Snapshot(sparkOptions.Enabled).Latest;
        var macs = facts.ToDictionary(node => node.Hostname, node => onboarding.Tasks.Where(task => task.Hostname == node.Hostname)
            .SelectMany(task => onboarding.Devices.Where(device => device.Id == task.DeviceId))
            .SelectMany(device => device.Hardware.Interfaces).Select(item => Mac(item.MacAddress)).OfType<string>().ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        return Compose(DateTimeOffset.UtcNow, topology, state, message, facts, macs, lab, groups, reading?.Sources ?? new(false, false, false),
            reading is not null, domains.IngressAddress, sparkOptions.Enabled, sparkLatest, chosen);
    }

    /// <summary>Joins everything into the map. Pure, so the checks can feed it UniFi's JSON directly.</summary>
    internal static Built Compose(DateTimeOffset now, UniFiTopology? topology, string unifiState, string? unifiMessage, ManagedNodeFacts[] facts,
        IReadOnlyDictionary<string, HashSet<string>> nodeMacs, LabStacks lab, IReadOnlyDictionary<string, string> overrides, LabSources sources,
        bool prometheus, string? sparkAddress, bool sparkEnabled, SparkSample? sparkLatest, IReadOnlyDictionary<string, string>? categories = null)
    {
        categories ??= new Dictionary<string, string>();
        (string Category, bool Chosen) Category(string id, string guess) => categories.TryGetValue(id, out var chosen) ? (chosen, true) : (guess, false);
        // UniFi's devices.
        MapDevice? gateway = null;
        JsonElement? gatewayRow = null;
        var devices = new List<MapDevice>();
        var deviceRates = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        var deviceMacs = new HashSet<string>(StringComparer.Ordinal);
        var rows = (topology?.Devices ?? []).Select(row => (Row: row, Mac: Mac(Field(row, "mac")))).Where(item => item.Mac is not null).Take(256).ToArray();
        foreach (var (_, mac) in rows) deviceMacs.Add(mac!);
        var gatewayMac = rows.FirstOrDefault(item => Kind(Field(item.Row, "type")) == "gateway").Mac;
        var gatewayId = gatewayMac is null ? StandInGateway : "dev:" + gatewayMac;
        foreach (var (row, mac) in rows)
        {
            var kind = Kind(Field(row, "type"));
            if (kind is null || kind == "gateway" && mac != gatewayMac) continue;
            var uplink = Child(row, "uplink");
            var parent = kind == "gateway" ? Wan
                : Mac(Field(uplink, "uplink_mac")) is { } up && up != mac && deviceMacs.Contains(up) ? "dev:" + up : gatewayId;
            var ports = Child(row, "port_table") is { ValueKind: JsonValueKind.Array } table
                ? table.EnumerateArray().Take(64).Where(port => Int(Field(port, "port_idx")) is not null)
                    .Select(port => new MapPort(Int(Field(port, "port_idx"))!.Value, Text(Field(port, "name")) ?? $"Port {Field(port, "port_idx")}",
                        Field(port, "up") == "true", Int(Field(port, "speed")) is > 0 and var speed ? speed : null, Field(port, "poe_enable") == "true")).ToArray()
                : [];
            var device = new MapDevice("dev:" + mac, kind, Text(Field(row, "name")) ?? Text(Field(row, "model")) ?? mac!, Text(Field(row, "model")),
                Address(Field(row, "ip")), mac!, Int(Field(row, "state")) switch { 1 => "ok", 0 => "failed", null => "unknown", _ => "warn" }, parent,
                Int(Field(row, "num_sta")) ?? 0, Field(row, "upgradable") == "true", ports);
            if (Rate(uplink) is { } rate) deviceRates[device.Id] = rate;
            if (kind == "gateway") (gateway, gatewayRow) = (device, row);
            else devices.Add(device);
        }
        gateway ??= new(StandInGateway, "gateway", "Gateway", null, null, "", "unknown", Wan, 0, false, []);
        var wanRow = gatewayRow is { } g ? Child(g, "wan1") is { ValueKind: JsonValueKind.Object } wan1 ? wan1 : Child(g, "uplink") : default;
        var wanUp = Field(wanRow, "up");
        var wanView = new MapWan(Wan, Text(Field(wanRow, "name")) ?? "Internet", Address(Field(wanRow, "ip")),
            gatewayRow is { } gr ? Text(Field(Child(Child(gr, "geo_info"), "WAN"), "isp_name")) ?? Text(Field(wanRow, "isp_name")) : null,
            wanUp == "true" ? "ok" : wanUp == "false" ? "failed" : gateway.Id == StandInGateway ? "unknown" : gateway.Health);
        (double, double)? wanRate = Rate(wanRow) ?? (gatewayRow is { } gw2 ? Rate(gw2) : null);

        // Networks and zones.
        var zoneRows = (topology?.Zones ?? []).Select(row => (Row: row, Id: Id(Field(row, "_id")))).Where(item => item.Id is not null).Take(64).ToArray();
        var networks = new List<(MapNetwork Network, (uint Address, uint Mask)? Subnet)>();
        foreach (var row in (topology?.Networks ?? []).Take(256))
        {
            if (Id(Field(row, "_id")) is not { } id || Field(row, "purpose") == "wan") continue;
            var zone = Id(Field(row, "firewall_zone_id")) ?? zoneRows.FirstOrDefault(item => Strings(Child(item.Row, "network_ids")).Contains(id)).Id;
            var subnet = Text(Field(row, "ip_subnet"));
            networks.Add((new("net:" + id, Text(Field(row, "name")) ?? id, Field(row, "vlan_enabled") == "false" ? null : Int(Field(row, "vlan")),
                subnet, Text(Field(row, "purpose")), zone is null ? null : "zone:" + zone), Subnet(subnet)));
        }
        var zones = zoneRows.Select(item => new MapZone("zone:" + item.Id, Text(Field(item.Row, "name")) ?? item.Id!,
            [.. networks.Where(network => network.Network.ZoneId == "zone:" + item.Id).Select(network => network.Network.Id)])).ToArray();
        string? NetworkOf(string? address) => Ip(address) is { } ip
            ? networks.FirstOrDefault(network => network.Subnet is { } subnet && (ip & subnet.Mask) == (subnet.Address & subnet.Mask)).Network?.Id : null;
        string? KnownNetwork(string? id) => Id(id) is { } valid && networks.Any(network => network.Network.Id == "net:" + valid) ? "net:" + valid : null;

        // UniFi's clients, online first, then those it has seen in the last month.
        var clients = new Dictionary<string, (JsonElement Row, bool Online)>(StringComparer.Ordinal);
        foreach (var row in topology?.Online ?? [])
            if (Mac(Field(row, "mac")) is { } mac && clients.Count < MaxClients) clients.TryAdd(mac, (row, true));
        var forgotten = now.Add(-ClientLinger).ToUnixTimeSeconds();
        foreach (var row in topology?.Known ?? [])
            if (Mac(Field(row, "mac")) is { } mac && clients.Count < MaxClients && !(Long(Field(row, "last_seen")) is > 0 and var seen && seen < forgotten))
                clients.TryAdd(mac, (row, false));
        var clientRates = clients.Where(item => item.Value.Online).Select(item => (item.Key, Rate: Rate(item.Value.Row, client: true)))
            .Where(item => item.Rate is not null).ToDictionary(item => item.Key, item => item.Rate!.Value, StringComparer.Ordinal);
        string ClientParent(JsonElement row) => SeenParent(row, ["ap_mac", "sw_mac", "uplink_mac", "gw_mac"]) ?? gatewayId;
        string? SeenParent(JsonElement row, string[] fields) =>
            fields.Select(name => Mac(Field(row, name))).FirstOrDefault(mac => mac is not null && deviceMacs.Contains(mac))
                is { } parent ? (parent == gatewayMac ? gatewayId : "dev:" + parent) : null;
        string? ClientFor(string? address, IEnumerable<string> macs) =>
            macs.FirstOrDefault(mac => clients.TryGetValue(mac, out var client) && client.Online)
            ?? (address is null ? null : clients.Where(item => item.Value.Online && Field(item.Value.Row, "ip") == address).Select(item => item.Key).FirstOrDefault());

        // Lucia's servers.
        var hosts = new List<MapHost>();
        var hostClients = new Dictionary<string, string>(StringComparer.Ordinal);
        var addresses = new Dictionary<string, string>(StringComparer.Ordinal);
        var luciaMacs = new HashSet<string>(StringComparer.Ordinal);
        MapHost Place(MapHost host, string? mac)
        {
            if (mac is not null) { hostClients[host.Id] = mac; luciaMacs.Add(mac); }
            if (host.Address is not null) addresses.TryAdd(host.Address, host.Id);
            var row = mac is not null ? clients[mac].Row : default;
            return host with
            {
                ParentId = mac is not null ? ClientParent(row) : gateway.Id,
                NetworkId = (mac is not null ? KnownNetwork(Field(row, "network_id")) : null) ?? NetworkOf(host.Address),
            };
        }
        var sparkIp = Address(sparkAddress);
        var sparkHealth = !sparkEnabled || sparkLatest is null ? "unknown" : now - sparkLatest.Timestamp <= TimeSpan.FromSeconds(30) ? "ok" : "stale";
        double? sparkMemory = sparkLatest is { MemoryTotalBytes: > 0 and var total, MemoryAvailableBytes: { } available }
            ? Math.Round(100.0 * (total - available) / total, 1) : null;
        hosts.Add(Place(new(SparkId, "spark", "Spark", sparkIp, null, null, sparkHealth, sparkLatest?.Timestamp,
            sparkLatest?.CpuPercent is { } sparkCpu ? Math.Round(sparkCpu, 1) : null, sparkMemory, 0, sparkLatest?.GpuName, "#/ai"), ClientFor(sparkIp, [])));
        foreach (var node in facts.OrderBy(node => node.Hostname, StringComparer.Ordinal))
        {
            var status = node.Status;
            var health = node.LastSeenAt is not { } seen ? "unknown" : now - seen <= TimeSpan.FromMinutes(2) ? "ok"
                : now - seen <= TimeSpan.FromMinutes(15) ? "stale" : "failed";
            var address = Address(node.Address);
            var mine = nodeMacs.GetValueOrDefault(node.Hostname) ?? [];
            luciaMacs.UnionWith(mine);
            hosts.Add(Place(new("host:" + node.Hostname, "node", node.Hostname, address, null, null, health, node.LastSeenAt,
                status?.CpuPercent is { } cpu ? Math.Round(cpu, 1) : null,
                status is { MemoryTotalBytes: > 0 } ? Math.Round(100.0 * (status.MemoryTotalBytes - status.MemoryAvailableBytes) / status.MemoryTotalBytes, 1) : null,
                status?.Updates?.Count ?? 0, status?.Runtime?.Gpus.FirstOrDefault()?.Model, "#/devices"), ClientFor(address, mine)));
        }

        // Apps, their containers and the NAS shares they use.
        var apps = new List<MapApp>();
        foreach (var stack in lab.Stacks.OrderBy(stack => stack.Name, StringComparer.Ordinal))
        {
            if (hosts.FirstOrDefault(host => host.Kind == "node" && host.Name == stack.Node) is not { } host) continue;
            var address = Address(stack.Address);
            var id = "app:" + stack.Name;
            if (address is not null) addresses.TryAdd(address, id);
            var reported = lab.Reports.ContainsKey(stack.Node);
            apps.Add(new(id, stack.Name, host.Id, stack.Desired, reported ? StackHealth(stack) : "stale", address,
                NetworkOf(address) ?? host.NetworkId, "#/apps/" + stack.Name, stack.UpdateCount,
                [.. stack.Containers.OrderBy(container => container.Name, StringComparer.Ordinal).Select(container => new MapContainer(
                    $"ctr:{host.Id}:{container.Name}", container.Name, container.Image, container.State, ContainerHealth(container, stack.Desired),
                    [.. (container.Networks ?? []).Select(network => new MapContainerNetwork(network.Name, network.Address))],
                    [.. (container.Mounts ?? []).Select(mount => new MapContainerMount(mount.Source ?? "", mount.Destination, Storage(mount.Source, lab.Nas)))],
                    TrafficKinds.Listens(container.Ports)))]));
        }
        var storage = lab.Nas.OrderBy(nas => nas.Id, StringComparer.Ordinal).Select(nas =>
        {
            var mounts = lab.Reports.OrderBy(report => report.Key, StringComparer.Ordinal)
                .Where(report => hosts.Any(host => host.Kind == "node" && host.Name == report.Key))
                .SelectMany(report => nas.Shares.Select(share => new MapStorageMount("host:" + report.Key, share,
                    report.Value.Mounts?.FirstOrDefault(mount => mount.Nas == nas.Id && mount.Share == share)?.State switch
                    {
                        "Mounted" => "ok", "Failed" => "failed", _ => "warn",
                    }))).ToArray();
            var health = mounts.Length == 0 ? "unknown" : mounts.All(mount => mount.Health == "ok") ? "ok"
                : mounts.All(mount => mount.Health == "failed") ? "failed" : "warn";
            return new MapStorage("nas:" + nas.Id, nas.Id, "nas", Address(nas.Host), health, nas.Shares, mounts);
        }).ToArray();
        // NAS boxes and UniFi's own devices answer traffic too.
        foreach (var item in storage.Select(nas => (nas.Address, nas.Id)).Concat(devices.Prepend(gateway).Select(device => (device.Address, device.Id))))
            if (item.Address is not null) addresses.TryAdd(item.Address, item.Id);

        // Everyone else on the network, grouped by network and kind; UniFi's devices and Lucia's own machines are drawn already.
        var excluded = addresses.Keys.ToHashSet(StringComparer.Ordinal);
        // UniFi's fingerprint product name ("HP Printer") names clients that have no alias or hostname.
        var models = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in topology?.Active ?? [])
            if (Mac(Field(row, "mac")) is { } mac && Text(Field(row, "model_name")) is { } model) models.TryAdd(mac, model);
        var members = clients.Where(item => !deviceMacs.Contains(item.Key) && !luciaMacs.Contains(item.Key)
                && !(Address(Field(item.Value.Row, "ip")) is { } ip && excluded.Contains(ip)))
            .Select(item =>
            {
                var (row, online) = item.Value;
                var name = Text(Field(row, "name"));
                var hostname = Text(Field(row, "hostname"));
                var vendor = Text(Field(row, "oui"));
                var model = models.GetValueOrDefault(item.Key);
                var overridden = overrides.TryGetValue(item.Key, out var chosen);
                var fingerprint = string.Join(' ', new[] { "dev_cat", "dev_family", "os_name", "dev_vendor" }.Select(field => Field(row, field)).Append(model).OfType<string>());
                var group = overridden ? chosen! : ClientClassifier.Classify(name, hostname, fingerprint, vendor, false);
                var (category, categoryChosen) = Category("client:" + item.Key, LabCategories.Device(name, hostname, fingerprint, vendor, group));
                var address = Address(Field(row, "ip"));
                var network = KnownNetwork(Field(row, "network_id")) ?? NetworkOf(address) ?? OtherNetwork;
                var lastSeen = Long(Field(row, "last_seen")) is > 0 and < 100_000_000_000 and var seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : (DateTimeOffset?)null;
                return (Network: network, Client: new MapClient(item.Key, name ?? hostname ?? model ?? item.Key, address, online, group, overridden,
                    online ? ClientParent(row) : SeenParent(row, ["last_uplink_mac", "ap_mac", "sw_mac"]), vendor, lastSeen, category, categoryChosen));
            }).ToArray();
        var clientGroups = members.GroupBy(member => (member.Network, member.Client.Group))
            .OrderBy(group => group.Key.Network, StringComparer.Ordinal).ThenBy(group => Array.IndexOf(ClientClassifier.Groups, group.Key.Group))
            .Select(group =>
            {
                var list = group.Select(member => member.Client).OrderByDescending(client => client.Online).ThenBy(client => client.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                var parent = list.Where(client => client.ParentId is not null).GroupBy(client => client.ParentId!).MaxBy(byParent => byParent.Count())?.Key ?? gateway.Id;
                return new MapClientGroup($"grp:{group.Key.Network}:{group.Key.Group}", group.Key.Network, group.Key.Group, list.Count(client => client.Online),
                    list.Length, list, parent);
            }).ToArray();
        // A client's address leads its traffic to it; the map draws it inside its group.
        foreach (var group in clientGroups)
            foreach (var client in group.Members)
                if (client.Address is not null) addresses.TryAdd(client.Address, "client:" + client.Mac);
        if (members.Any(member => member.Network == OtherNetwork))
            networks.Add((new(OtherNetwork, "Other", null, null, null, null), null));

        T Categorised<T>(T item, string id, string guess, Func<T, string, bool, T> with)
        {
            var (category, chosen) = Category(id, guess);
            return with(item, category, chosen);
        }
        var view = new LabMapView(now, new(unifiState, unifiMessage, topology?.Site), prometheus ? "full" : topology is not null ? "reduced" : "none",
            sources, gateway, wanView, [.. networks.Select(network => network.Network)], zones, [.. devices],
            [.. hosts.Select(host => Categorised(host, host.Id, host.Category, (item, category, chosen) => item with { Category = category, CategoryChosen = chosen }))],
            [.. apps.Select(app => Categorised(app, app.Id, LabCategories.App(app.Name, app.Containers.Select(container => container.Image)),
                (item, category, chosen) => item with { Category = category, CategoryChosen = chosen }))],
            [.. storage.Select(nas => Categorised(nas, nas.Id, nas.Category, (item, category, chosen) => item with { Category = category, CategoryChosen = chosen }))],
            clientGroups);
        return new(view, hostClients, addresses, clientRates, deviceRates, wanRate);
    }

    internal static string StackHealth(LabStack stack) => stack.Status?.State switch
    {
        null => "stale",
        "Failed" => "failed",
        "Degraded" => "warn",
        "Stopped" => stack.Desired == "Running" ? "warn" : "ok",
        "Running" => stack.Desired == "Running" ? "ok" : "warn",
        _ => "unknown",
    };

    internal static string ContainerHealth(NodeContainer container, string desired)
    {
        var status = container.Status ?? "";
        return container.State switch
        {
            "running" => status.Contains("(unhealthy)", StringComparison.Ordinal) ? "warn" : "ok",
            "exited" => ExitCode().Match(status) is { Success: true } exit && exit.Groups[1].Value != "0" ? "failed" : desired == "Running" ? "warn" : "ok",
            "dead" => "failed",
            "restarting" or "created" or "paused" or "removing" => "warn",
            _ => "unknown",
        };
    }

    private static string? Storage(string? source, LabNas[] nas) =>
        source is not null && source.StartsWith(StackStore.NasRoot + "/", StringComparison.Ordinal)
        && nas.FirstOrDefault(item => source[(StackStore.NasRoot.Length + 1)..].Split('/')[0] == item.Id) is { } match ? "nas:" + match.Id : null;

    private static string? Kind(string? type) => type switch
    {
        "ugw" or "udm" or "uxg" or "ucg" => "gateway",
        "usw" => "switch",
        "uap" => "ap",
        _ => null,
    };

    /// <summary>Bytes per second received and sent, from UniFi's <c>rx_bytes-r</c>/<c>tx_bytes-r</c>. A client's are from its
    /// access point's side, so they swap: what the AP sends, the client receives.</summary>
    private static (double, double)? Rate(JsonElement row, bool client = false)
    {
        if (Double(Field(row, "rx_bytes-r")) is not { } rx || Double(Field(row, "tx_bytes-r")) is not { } tx) return null;
        return client ? (tx, rx) : (rx, tx);
    }

    private static string? Field(JsonElement row, string name) => UniFiSession.Field(row, name);

    private static JsonElement Child(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) ? value : default;

    private static string[] Strings(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array ? [.. array.EnumerateArray().Take(256).Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)] : [];

    private static string? Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length > 128 ? value[..128].Trim() : value.Trim();

    private static int? Int(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
    private static long? Long(string? value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static double? Double(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number >= 0 ? number : null;

    private static string? Mac(string? value) => value is not null && UniFiValidation.Mac(value) ? value.ToLowerInvariant() : null;

    private static string? Id(string? value) => value is not null && IdPattern().IsMatch(value) ? value : null;

    private static string? Address(string? value) =>
        IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && ip.ToString() == value ? value : null;

    private static uint? Ip(string? value) =>
        Address(value) is { } address ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(address).GetAddressBytes()) : null;

    private static (uint Address, uint Mask)? Subnet(string? cidr)
    {
        if (cidr?.Split('/') is not [var address, var bits] || Ip(address) is not { } ip || !int.TryParse(bits, out var length) || length is < 8 or > 32)
            return null;
        return (ip, length == 32 ? uint.MaxValue : ~(uint.MaxValue >> length));
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,64}\z")] private static partial Regex IdPattern();
    [GeneratedRegex(@"\AExited \((-?\d+)\)")] private static partial Regex ExitCode();
}
