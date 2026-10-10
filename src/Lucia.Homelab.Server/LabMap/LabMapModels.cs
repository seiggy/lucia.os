namespace Lucia.Homelab.Server.LabMap;

/// <summary>The lab map's structure: UniFi's network, Lucia's servers and apps and the NAS, joined into one tree under the gateway.</summary>
/// <param name="Traffic"><c>full</c> while Prometheus answers, <c>reduced</c> with UniFi's rates only, else <c>none</c>.</param>
public sealed record LabMapView(DateTimeOffset GeneratedAt, LabUniFi Unifi, string Traffic, LabSources Sources, MapDevice? Gateway, MapWan Wan,
    MapNetwork[] Networks, MapZone[] Zones, MapDevice[] Devices, MapHost[] Hosts, MapApp[] Apps, MapStorage[] Storage, MapClientGroup[] ClientGroups);
/// <param name="State"><c>connected</c>, <c>not-connected</c> or <c>error</c>.</param>
public sealed record LabUniFi(string State, string? Message, string? Site);
/// <summary>Whether Prometheus holds recent series from each optional source.</summary>
public sealed record LabSources(bool Snmp, bool Netflow, bool DockerStats);
public sealed record MapWan(string Id, string Name, string? Address, string? Isp, string Health);
public sealed record MapNetwork(string Id, string Name, int? Vlan, string? Subnet, string? Purpose, string? ZoneId);
public sealed record MapZone(string Id, string Name, string[] NetworkIds);
/// <param name="Kind"><c>gateway</c>, <c>switch</c> or <c>ap</c>.</param>
/// <param name="ParentId">The device it uplinks to; the gateway's is <c>wan</c>.</param>
public sealed record MapDevice(string Id, string Kind, string Name, string? Model, string? Address, string Mac, string Health, string? ParentId,
    int ClientCount, bool UpdateAvailable, MapPort[] Ports);
public sealed record MapPort(int Index, string Name, bool Up, int? SpeedMbps, bool Poe);
/// <param name="Kind"><c>spark</c> or <c>node</c>.</param>
/// <param name="Category">One of <see cref="LabCategories.All"/> or the owner's own; <see cref="CategoryChosen"/> when the owner chose it.</param>
public sealed record MapHost(string Id, string Kind, string Name, string? Address, string? NetworkId, string? ParentId, string Health,
    DateTimeOffset? LastSeenAt, double? CpuPercent, double? MemoryPercent, int UpdatesAvailable, string? Gpu, string Href,
    string Category = LabCategories.Server, bool CategoryChosen = false);
public sealed record MapApp(string Id, string Name, string HostId, string Desired, string Health, string? Address, string? NetworkId, string Href,
    int UpdateCount, MapContainer[] Containers, string Category = LabCategories.Other, bool CategoryChosen = false);
/// <param name="Ports">Docker's published host ports.</param>
public sealed record MapContainer(string Id, string Name, string Image, string State, string Health, MapContainerNetwork[] Networks,
    MapContainerMount[] Mounts, MapListen[] Ports);
/// <param name="Protocol"><c>tcp</c> or <c>udp</c>.</param>
/// <param name="Service">One of <see cref="TrafficKinds.Services"/>.</param>
public sealed record MapListen(int Port, string Protocol, string Service);
public sealed record MapContainerNetwork(string Name, string? Address);
/// <param name="StorageId">The NAS (<c>nas:&lt;id&gt;</c>) whose share it mounts, if any.</param>
public sealed record MapContainerMount(string Source, string Destination, string? StorageId);
public sealed record MapStorage(string Id, string Name, string Kind, string? Address, string Health, string[] Shares, MapStorageMount[] Mounts,
    string Category = LabCategories.Storage, bool CategoryChosen = false);
public sealed record MapStorageMount(string HostId, string Share, string Health);
/// <param name="ParentId">The network's busiest uplink device, or the gateway.</param>
public sealed record MapClientGroup(string Id, string NetworkId, string Group, int Online, int Total, MapClient[] Members, string ParentId);
/// <param name="ParentId">The AP or switch it's connected through, or for an offline client the one UniFi last saw it on, if it says.</param>
public sealed record MapClient(string Mac, string Name, string? Address, bool Online, string Group, bool Overridden, string? ParentId,
    string? Vendor, DateTimeOffset? LastSeenAt, string Category = LabCategories.Other, bool CategoryChosen = false);

/// <summary>What changes between structure reads: each object's link rate to its parent, load, health and NetFlow's top talkers.</summary>
/// <param name="Mix">LAN object id → service → bits per second, both directions.</param>
/// <param name="Listening">LAN object id → the server ports it answered on, busiest first.</param>
/// <param name="Destinations">The busiest internet destinations.</param>
public sealed record LabMapLive(DateTimeOffset At, string Traffic, Dictionary<string, LabRate> Rates, Dictionary<string, LabLoad> Load,
    Dictionary<string, string> Health, LabFlow[] Flows, Dictionary<string, Dictionary<string, double>> Mix, Dictionary<string, LabListen[]> Listening,
    LabDestination[] Destinations);
public sealed record LabRate(double RxBps, double TxBps);
public sealed record LabLoad(double? CpuPercent, double? MemoryPercent);
/// <param name="To">An object id, <c>wan</c>, or an internet destination <c>dest:&lt;key&gt;</c>.</param>
/// <param name="Service">One of <see cref="TrafficKinds.Services"/>.</param>
public sealed record LabFlow(string From, string To, string? Label, double Bps, string Service);
public sealed record LabListen(int Port, string Protocol, string Service, double Bps);
/// <param name="Id"><c>dest:&lt;key&gt;</c>: the registrable domain AdGuard resolved, else <c>as&lt;number&gt;</c>, else the address's /24 (/48 for IPv6).</param>
/// <param name="Category">One of <see cref="TrafficKinds.Categories"/>, or the owner's own district name.</param>
/// <param name="Domains">The busiest names AdGuard resolved to it, at most three.</param>
/// <param name="Mix">Service → bits per second.</param>
/// <param name="Chosen">Whether the owner chose the district rather than Lucia.</param>
public sealed record LabDestination(string Id, string Name, string Category, string? Org, string? Asn, string[] Domains, double Bps,
    Dictionary<string, double> Mix, bool Chosen = false);

public sealed record LabClientOverrideRequest(string? Group);
/// <param name="Site">The destination's id, <c>dest:&lt;key&gt;</c>.</param>
/// <param name="District">A built-in category id, a district name of the owner's, or null to let Lucia decide.</param>
public sealed record LabSiteDistrictRequest(string Site, string? District);
/// <param name="Object">A client, app, server or NAS id from the map.</param>
/// <param name="Category">A built-in category id, a category name of the owner's, or null to let Lucia decide.</param>
public sealed record LabCategoryRequest(string Object, string? Category);

/// <summary>What the owner sees of the SNMP settings. The passphrases are write-only and never part of it.</summary>
public sealed record SnmpStatus(bool Configured, string? Username, string? AuthProtocol, string? PrivProtocol, DateTimeOffset? LastScrapeAt, string? Message);

/// <summary>Write-only SNMPv3 settings. Not a record, so a generated ToString can't print the passphrases.</summary>
public sealed class SnmpSettingsRequest
{
    public string Username { get; init; } = "";
    public string AuthProtocol { get; init; } = "";
    public string AuthPassphrase { get; init; } = "";
    public string PrivProtocol { get; init; } = "";
    public string PrivPassphrase { get; init; } = "";
    public override string ToString() => nameof(SnmpSettingsRequest);
}
