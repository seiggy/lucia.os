using System.Net;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Onboarding;

namespace Lucia.Homelab.Server.Nodes;

/// <summary>A machine whose lease Lucia pins. <paramref name="Macs"/> is null for the Spark itself: its container cannot see the host NICs, so the live client at its boot address is taken as the Spark.</summary>
public sealed record DhcpNode(string Hostname, string Address, string[]? Macs);
public sealed record DhcpReservation(string Hostname, string Address, string? Mac, string State, bool Host = false);
public sealed record DhcpReservationSnapshot(bool Enabled, DateTimeOffset? CheckedAt, string? Error, DhcpReservation[] Nodes);
internal sealed record DhcpReserveAction(int Index, UniFiClient Client, string Address, string NetworkId);

/// <summary>
/// Pins the Spark's and each managed node's current DHCP lease to its own MAC in UniFi, so onboarded servers keep their address.
/// Lucia only adds reservations: an existing reservation for the client is the owner's choice and is left alone,
/// and an address another client has reserved is never taken.
/// </summary>
public sealed class ManagedNodeDhcp : BackgroundService
{
    private readonly IDhcpReservations _dhcp;
    private readonly ManagedNodeEnrollment _nodes;
    private readonly HardwareOnboardingStore _onboarding;
    private readonly ILogger<ManagedNodeDhcp> _logger;
    private readonly string? _hostAddress;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private DhcpReservationSnapshot _snapshot = new(false, null, null, []);

    public ManagedNodeDhcp(IDhcpReservations dhcp, ManagedNodeEnrollment nodes, HardwareOnboardingStore onboarding,
        DomainOnboardingOptions domains, ILogger<ManagedNodeDhcp> logger)
    {
        (_dhcp, _nodes, _onboarding, _logger) = (dhcp, nodes, onboarding, logger);
        // The Spark's LAN address, which PXE and local DNS already depend on.
        var host = domains.IngressAddress;
        _hostAddress = IPAddress.TryParse(host, out var parsed) && parsed.ToString() == host ? host : null;
        nodes.AddressChanged += Wake;
        dhcp.Changed += Wake;
    }

    public DhcpReservationSnapshot Snapshot() => _snapshot;

    public void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await Sync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                _snapshot = _snapshot with { CheckedAt = DateTimeOffset.UtcNow, Error = (error as UniFiException)?.Message ?? "UniFi could not be read." };
                _logger.LogWarning("Managed-node DHCP reservations could not be checked ({ErrorType}); retrying later.", error.GetType().Name);
            }
            try { await _wake.WaitAsync(TimeSpan.FromMinutes(5), stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task Sync(CancellationToken ct)
    {
        var status = await _dhcp.GetStatusAsync(ct);
        if (!status.Configured || !status.ReserveNodeAddresses)
        {
            _snapshot = new(false, DateTimeOffset.UtcNow, null, []);
            return;
        }
        var devices = (await _onboarding.GetSnapshotAsync(ct)).Devices.ToDictionary(device => device.Id);
        var nodes = (await _nodes.Addresses(ct)).Select(node => new DhcpNode(node.Hostname, node.Address,
            devices.TryGetValue(node.NodeId, out var device)
                ? device.Hardware.Interfaces.Select(nic => nic.MacAddress).OfType<string>().ToArray() : []));
        if (_hostAddress is not null) nodes = nodes.Prepend(new DhcpNode("Lucia host", _hostAddress, null));
        _snapshot = new(true, DateTimeOffset.UtcNow, null, await Apply(_dhcp, [.. nodes], _logger, ct));
    }

    internal static async Task<DhcpReservation[]> Apply(IDhcpReservations dhcp, DhcpNode[] nodes, ILogger logger, CancellationToken ct)
    {
        if (nodes.Length == 0) return [];
        var (known, online) = await dhcp.ListClientsAsync(ct);
        var (states, actions) = Plan(nodes, known, online);
        foreach (var action in actions)
        {
            try
            {
                await dhcp.ReserveAsync(action.Client, action.Address, action.NetworkId, ct);
                states[action.Index] = states[action.Index] with { State = "Reserved" };
                logger.LogInformation("Reserved {Address} in UniFi for Lucia machine {Hostname}.", action.Address, states[action.Index].Hostname);
            }
            catch (UniFiException error)
            {
                states[action.Index] = states[action.Index] with { State = "Failed" };
                logger.LogWarning("UniFi refused a reservation for Lucia machine {Hostname} ({Code}).", states[action.Index].Hostname, error.Code);
            }
        }
        return states;
    }

    /// <summary>
    /// States: Reserved (the client's fixed IP is its current address), Reserving (Lucia will add it), ReservedElsewhere
    /// (the owner pinned a different address), AddressTaken (another client holds that reservation) and NotSeen
    /// (UniFi has no live lease for the node's MAC at that address yet).
    /// </summary>
    internal static (DhcpReservation[] States, DhcpReserveAction[] Actions) Plan(DhcpNode[] nodes, UniFiClient[] known, UniFiClient[] online)
    {
        var states = new DhcpReservation[nodes.Length];
        var actions = new List<DhcpReserveAction>();
        for (var i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i];
            var macs = node.Macs?.Select(mac => mac.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var lease = IPAddress.TryParse(node.Address, out _)
                ? online.FirstOrDefault(client => (macs is null || macs.Contains(client.Mac)) && client.Address == node.Address) : null;
            if (lease is null) { states[i] = new(node.Hostname, node.Address, null, "NotSeen", macs is null); continue; }
            var record = known.FirstOrDefault(client => client.Id == lease.Id) ?? lease;
            var state = record.UseFixedIp ? (record.FixedIp == node.Address ? "Reserved" : "ReservedElsewhere")
                : known.Concat(online).Any(client => client.Id != lease.Id && client.UseFixedIp && client.FixedIp == node.Address) ? "AddressTaken"
                : lease.NetworkId is null ? "NotSeen" : "Reserving";
            states[i] = new(node.Hostname, node.Address, lease.Mac, state, macs is null);
            if (state == "Reserving") actions.Add(new(i, lease, node.Address, lease.NetworkId!));
        }
        return (states, [.. actions]);
    }
}
