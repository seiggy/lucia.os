using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Stacks;

namespace Lucia.Homelab.Server.LabMap;

/// <summary>A UniFi device the collector polls over SNMP: its lab map id, name and address.</summary>
internal sealed record SnmpTarget(string Id, string Name, string Address);

/// <summary>
/// The Observability app's second collector config: one SNMPv3 receiver per UniFi device, reading IF-MIB's interface names,
/// 64-bit octet counters, operational status and speed, each device's series labelled with its lab map id. It travels in the
/// app's environment as <c>LUCIA_LAB_COLLECTOR</c>, single-quoted so compose leaves it alone, and the credentials beside it
/// as <c>LUCIA_LAB_SNMP_*</c>, which the collector expands itself, so they never appear in the compose or the config.
/// </summary>
internal static partial class LabCollector
{
    public const int MaxTargets = 32;

    /// <summary>The environment lines, or empty without SNMP settings or devices.</summary>
    public static string Lines(SnmpCredentials? snmp, IEnumerable<SnmpTarget> targets)
    {
        var chosen = targets.Where(target => target.Id.StartsWith("dev:", StringComparison.Ordinal) && IPAddress.TryParse(target.Address, out var ip)
                && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && AdGuardTransport.IsPrivate(ip))
            .DistinctBy(target => target.Address).OrderBy(target => target.Id, StringComparer.Ordinal).Take(MaxTargets).ToArray();
        if (snmp is null || chosen.Length == 0) return "";
        var prefix = StackStore.LabPrefix;
        return $"{prefix}COLLECTOR='{Config(snmp.AuthProtocol, snmp.PrivProtocol, chosen)}'\n{prefix}SNMP_USER='{snmp.Username}'\n"
            + $"{prefix}SNMP_AUTH='{snmp.AuthPassphrase}'\n{prefix}SNMP_PRIV='{snmp.PrivPassphrase}'\n";
    }

    /// <summary>
    /// One line of YAML flow style. The first receiver carries the shared settings under an anchor and the rest merge it, so
    /// the line stays small. Names are reduced to safe characters: the collector would expand a <c>${…}</c> in them.
    /// </summary>
    internal static string Config(string authType, string privacyType, SnmpTarget[] targets)
    {
        var text = new StringBuilder("{receivers: {");
        for (var i = 0; i < targets.Length; i++)
        {
            var endpoint = $"udp://{targets[i].Address}:161";
            text.Append(i == 0
                ? $"snmp/0: &snmp {{endpoint: \"{endpoint}\", version: v3, user: \"${{env:{StackStore.LabPrefix}SNMP_USER}}\", security_level: auth_priv, "
                    + $"auth_type: {authType}, auth_password: \"${{env:{StackStore.LabPrefix}SNMP_AUTH}}\", privacy_type: {privacyType}, "
                    + $"privacy_password: \"${{env:{StackStore.LabPrefix}SNMP_PRIV}}\", collection_interval: 30s, "
                    + "attributes: {if.name: {oid: \"1.3.6.1.2.1.31.1.1.1.1\"}, direction: {enum: [receive, transmit]}}, metrics: {"
                    + "lucia.snmp.interface.io: {unit: By, sum: {aggregation: cumulative, monotonic: true, value_type: int}, column_oids: ["
                    + "{oid: \"1.3.6.1.2.1.31.1.1.1.6\", attributes: [{name: if.name}, {name: direction, value: receive}]}, "
                    + "{oid: \"1.3.6.1.2.1.31.1.1.1.10\", attributes: [{name: if.name}, {name: direction, value: transmit}]}]}, "
                    + "lucia.snmp.interface.status: {unit: \"1\", gauge: {value_type: int}, column_oids: [{oid: \"1.3.6.1.2.1.2.2.1.8\", attributes: [{name: if.name}]}]}, "
                    + "lucia.snmp.interface.speed: {unit: Mbit/s, gauge: {value_type: int}, column_oids: [{oid: \"1.3.6.1.2.1.31.1.1.1.15\", attributes: [{name: if.name}]}]}}}"
                : $", snmp/{i}: {{<<: *snmp, endpoint: \"{endpoint}\"}}");
        }
        text.Append("}, processors: {");
        text.AppendJoin(", ", targets.Select((target, i) => $"resource/snmp-{i}: {{attributes: [{{key: lucia.device, value: \"{target.Id}\", action: upsert}}, "
            + $"{{key: lucia.device.name, value: \"{Safe(target.Name)}\", action: upsert}}, {{key: lucia.device.address, value: \"{target.Address}\", action: upsert}}]}}"));
        text.Append("}, service: {pipelines: {");
        text.AppendJoin(", ", targets.Select((_, i) => $"metrics/snmp-{i}: {{receivers: [snmp/{i}], processors: [memory_limiter, resource/snmp-{i}, batch], "
            + "exporters: [otlphttp/prometheus]}"));
        text.Append("}}}");
        return text.ToString();
    }

    private static string Safe(string name)
    {
        var safe = UnsafeName().Replace(name, "-");
        return safe.Length > 64 ? safe[..64] : safe;
    }

    [GeneratedRegex(@"[^A-Za-z0-9 ._()-]")] private static partial Regex UnsafeName();
}

/// <summary>Keeps the Observability app's SNMP receivers in step with the SNMP settings and UniFi's devices.</summary>
public sealed class LabCollectorSync(SnmpSettingsStore snmp, LabMapService map, StackStore stacks, ILogger<LabCollectorSync> logger) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        snmp.Changed += Wake;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try { await Sync(stop); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    logger.LogWarning("The lab map's SNMP collector config could not be updated ({ErrorType}); retrying.", error.GetType().Name);
                }
                try { await _wake.WaitAsync(TimeSpan.FromSeconds(60), stop); }
                catch (OperationCanceledException) { return; }
            }
        }
        finally { snmp.Changed -= Wake; }
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    internal async Task Sync(CancellationToken ct)
    {
        var credentials = await snmp.Credentials(ct);
        if (credentials is null)
        {
            await stacks.SetLabCollector("", ct);
            return;
        }
        // Without a fresh read of UniFi's devices the current receivers stay as they are.
        if (await map.Devices(ct) is not { } devices) return;
        await stacks.SetLabCollector(LabCollector.Lines(credentials,
            devices.Where(device => device.Address is not null).Select(device => new SnmpTarget(device.Id, device.Name, device.Address!))), ct);
    }
}
