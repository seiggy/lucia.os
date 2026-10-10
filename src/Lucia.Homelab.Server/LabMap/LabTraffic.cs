using System.Globalization;
using System.Net;
using System.Text.Json;
using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Stacks;

namespace Lucia.Homelab.Server.LabMap;

/// <summary>One series of an instant query: its labels and value.</summary>
internal sealed record PromSample(IReadOnlyDictionary<string, string> Labels, double Value);

/// <summary>
/// Instant PromQL queries against the Observability app's Prometheus, through Grafana's data source proxy with the admin
/// credentials Lucia generated for Grafana: Prometheus itself stays unpublished.
/// </summary>
public sealed class PrometheusQuery(StackStore stacks)
{
    internal const int MaxResponseBytes = 4 * 1024 * 1024, MaxSeries = 5000;
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(3),
    }) { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Each query's series, or null for one that failed. Null overall when Prometheus can't be reached at all.</summary>
    internal async Task<PromSample[]?[]?> Query(string[] queries, CancellationToken ct)
    {
        if (await stacks.GrafanaEndpoint(ct) is not { } grafana) return null;
        var results = await Task.WhenAll(queries.Select(query => One(grafana.Endpoint, grafana.Authorization, query, ct)));
        return results.All(result => result is null) ? null : results;
    }

    private static async Task<PromSample[]?> One(Uri grafana, string authorization, string query, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(grafana, "/api/datasources/proxy/uid/prometheus/api/v1/query?query=" + Uri.EscapeDataString(query)));
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxResponseBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return Parse(await AdGuardTransport.ReadBoundedAsync(stream, MaxResponseBytes, ct));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException
            or AdGuardManagementException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>A <c>vector</c> result's series; anything else is no answer.</summary>
    internal static PromSample[]? Parse(byte[] body)
    {
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var status) || status.GetString() != "success"
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            return null;
        var samples = new List<PromSample>();
        foreach (var series in result.EnumerateArray().Take(MaxSeries))
        {
            if (series.ValueKind != JsonValueKind.Object || !series.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array
                || value.GetArrayLength() != 2 || value[1].ValueKind != JsonValueKind.String
                || !double.TryParse(value[1].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                continue;
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (series.TryGetProperty("metric", out var metric) && metric.ValueKind == JsonValueKind.Object)
                foreach (var label in metric.EnumerateObject().Take(32))
                    if (label.Value.ValueKind == JsonValueKind.String) labels[label.Name] = label.Value.GetString()!;
            samples.Add(new(labels, number));
        }
        return [.. samples];
    }
}

/// <summary>What Prometheus says about the lab's traffic and load right now. Rates are bits per second.</summary>
internal sealed record LabTrafficReading(LabSources Sources, Dictionary<string, LabRate> Nodes, Dictionary<(string Node, string Container), LabRate> Containers,
    Dictionary<string, LabRate> Devices, Dictionary<(string Node, string Container),     LabLoad> ContainerLoad, LabFlowSample[] Flows,
    DateTimeOffset? LastSnmpAt);

    /// <summary>
    /// One NetFlow series: raw addresses, transport, and the server port with the end that owns it (<c>src</c> or <c>dst</c>) when
    /// either port was below the ephemeral range. Bits per second.
    /// </summary>
    internal sealed record LabFlowSample(string Source, string Destination, int? Port, string Protocol, string? Server, double Bps);

/// <summary>The lab map's batch of PromQL queries, averaged over a chosen window and reused briefly.</summary>
public sealed class LabTraffic(PrometheusQuery prometheus)
{
    // Physical interfaces only: Docker's bridges and veths would count each container's traffic again.
    private const string Physical = "device!~\"lo|veth.*|br-.*|docker.*|virbr.*|cni.*|flannel.*|cali.*|tap.*|macvlan.*|lucia.*\"";
    internal static readonly string[] Queries =
    [
        $"sum by (lucia_node) (rate(node_network_receive_bytes_total{{{Physical}}}[1m]))",
        $"sum by (lucia_node) (rate(node_network_transmit_bytes_total{{{Physical}}}[1m]))",
        "sum by (lucia_node, container_name) (rate(container_network_io_usage_rx_bytes_total[1m]))",
        "sum by (lucia_node, container_name) (rate(container_network_io_usage_tx_bytes_total[1m]))",
        "sum by (lucia_device, if_name, direction) (rate(lucia_snmp_interface_io_bytes_total[1m]))",
        "topk(600, sum by (lucia_src, lucia_dst, lucia_port, lucia_proto, lucia_server) (sum_over_time({__name__=~\"lucia_netflow_bytes(_total)?\"}[1m])) / 60)",
        "max by (lucia_node, container_name) (container_cpu_utilization_ratio)",
        "max by (lucia_node, container_name) (container_memory_percent_ratio)",
        "max(timestamp(lucia_snmp_interface_io_bytes_total))",
    ];
    /// <summary>The rate windows the map offers, each with how long its reading is reused.</summary>
    internal static readonly IReadOnlyDictionary<string, TimeSpan> Windows = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
    {
        ["1m"] = TimeSpan.FromSeconds(5), ["15m"] = TimeSpan.FromSeconds(30), ["1h"] = TimeSpan.FromMinutes(1), ["24h"] = TimeSpan.FromMinutes(5),
    };
    internal static string[] QueriesFor(string window, int series = 600) => [.. Queries.Select(query => query
        .Replace("[1m]", $"[{window}]", StringComparison.Ordinal).Replace("topk(600,", $"topk({series},", StringComparison.Ordinal)
        .Replace(") / 60)", $") / {window switch { "15m" => 900, "1h" => 3600, "24h" => 86400, _ => 60 }})", StringComparison.Ordinal))];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset At, LabTrafficReading? Reading)> _last = new(StringComparer.Ordinal);

    /// <summary>
    /// The latest reading averaged over <paramref name="window"/>, with enough NetFlow series for <paramref name="sites"/> destinations;
    /// null when Prometheus can't be reached.
    /// </summary>
    internal async Task<LabTrafficReading?> Read(CancellationToken ct, string window = "1m", int sites = LabFlows.MaxDestinations)
    {
        if (!Windows.TryGetValue(window, out var reuse)) window = "1m";
        var series = Math.Clamp(sites * 4, 600, PrometheusQuery.MaxSeries);
        var key = $"{window}/{series}";
        await _gate.WaitAsync(ct);
        try
        {
            if (_last.TryGetValue(key, out var last) && DateTimeOffset.UtcNow - last.At < reuse) return last.Reading;
            var reading = await prometheus.Query(QueriesFor(window, series), ct) is { } results ? Reading(results) : null;
            _last[key] = (DateTimeOffset.UtcNow, reading);
            return reading;
        }
        finally { _gate.Release(); }
    }

    internal static LabTrafficReading Reading(PromSample[]?[] results)
    {
        PromSample[] At(int index) => results.ElementAtOrDefault(index) ?? [];
        string Label(PromSample sample, string name) => sample.Labels.GetValueOrDefault(name) ?? "";

        var nodes = Rates(At(0), At(1), sample => Label(sample, "lucia_node"));
        var containers = Rates(At(2), At(3), sample => (Label(sample, "lucia_node"), Label(sample, "container_name").TrimStart('/')));
        // A device's link to its parent is taken to be its busiest interface: the uplink carries everything below it.
        var devices = At(4).Where(sample => Label(sample, "lucia_device").Length > 0)
            .GroupBy(sample => (Device: Label(sample, "lucia_device"), Port: Label(sample, "if_name")))
            .Select(port => (port.Key.Device, Rate: new LabRate(
                8 * port.Where(item => Label(item, "direction") == "receive").Sum(item => item.Value),
                8 * port.Where(item => Label(item, "direction") == "transmit").Sum(item => item.Value))))
            .GroupBy(port => port.Device)
            .ToDictionary(device => device.Key, device => device.MaxBy(port => port.Rate.RxBps + port.Rate.TxBps).Rate, StringComparer.Ordinal);
        Dictionary<(string, string), double> Latest(PromSample[] samples) =>
            samples.GroupBy(sample => (Label(sample, "lucia_node"), Label(sample, "container_name").TrimStart('/')))
                .ToDictionary(group => group.Key, group => group.Max(sample => sample.Value));
        var cpu = Latest(At(6));
        var memory = Latest(At(7));
        var load = cpu.Keys.Union(memory.Keys).ToDictionary(key => key,
            key => new LabLoad(cpu.TryGetValue(key, out var c) ? Math.Round(c, 1) : null, memory.TryGetValue(key, out var m) ? Math.Round(m, 1) : null));
        var flows = At(5).Where(sample => Label(sample, "lucia_src").Length > 0 && Label(sample, "lucia_dst").Length > 0)
            .Select(sample => new LabFlowSample(Label(sample, "lucia_src"), Label(sample, "lucia_dst"),
                int.TryParse(Label(sample, "lucia_port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536 ? port : null,
                Label(sample, "lucia_proto").ToLowerInvariant(), Label(sample, "lucia_server") is "src" or "dst" ? Label(sample, "lucia_server") : null, 8 * sample.Value))
            .OrderByDescending(flow => flow.Bps).ToArray();
        DateTimeOffset? lastSnmp = At(8).FirstOrDefault() is { Value: > 0 and < 1e11 } stamp ? DateTimeOffset.FromUnixTimeMilliseconds((long)(stamp.Value * 1000)) : null;
        return new(new(At(4).Length > 0 || lastSnmp is not null, At(5).Length > 0, At(2).Length + At(3).Length > 0),
            nodes, containers, devices, load, flows, lastSnmp);
    }

    private static Dictionary<TKey, LabRate> Rates<TKey>(PromSample[] received, PromSample[] sent, Func<PromSample, TKey> key) where TKey : notnull
    {
        var rx = received.GroupBy(key).ToDictionary(group => group.Key, group => group.Sum(item => item.Value));
        var tx = sent.GroupBy(key).ToDictionary(group => group.Key, group => group.Sum(item => item.Value));
        return rx.Keys.Union(tx.Keys).ToDictionary(item => item, item => new LabRate(8 * rx.GetValueOrDefault(item), 8 * tx.GetValueOrDefault(item)));
    }
}
