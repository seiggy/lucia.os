namespace Lucia.Homelab.Server.Telemetry;

/// <summary>
/// The Observability app keeps raw metrics for an hour. Thanos rulers average the metrics Lucia charts into tiers of
/// 1-minute, 30-minute, 1-hour and 4-hour slices, each built from the one before, and its compactor deletes each tier
/// after 6 hours, 24 hours, 7 days and 30 days. Gauges keep their average (<c>:avg</c>), counters their average rate
/// (<c>:rate</c>) and delta counters their total (<c>:sum</c>).
/// </summary>
internal static class MetricTiers
{
    /// <summary>Each tier: its name (also its slice), how far back a chart looks for its latest slice, and how long it's kept.</summary>
    internal static readonly (string Name, string Lookback, string Keep)[] Tiers =
        [("1m", "2m", "6h"), ("30m", "1h", "24h"), ("1h", "2h", "7d"), ("4h", "8h", "30d")];

    // Container and bridge interfaces would double count the physical ones.
    internal const string Nics = "device!~\"lo|veth.*|br-.*|docker.*|virbr.*|cni.*|flannel.*|cali.*|tap.*\"";
    internal const string Disks = "fstype!~\"tmpfs|overlay|squashfs|nsfs|ramfs|nfs.*|fuse.*|autofs|vfat\"";

    internal static readonly (string Metric, string Selector)[] Gauges =
    [
        ("node_memory_MemAvailable_bytes", ""), ("node_memory_MemTotal_bytes", ""), ("node_filesystem_avail_bytes", Disks),
        ("node_filesystem_size_bytes", Disks), ("node_hwmon_temp_celsius", ""), ("node_hwmon_chip_names", ""), ("node_load1", ""),
        ("node_uname_info", ""), ("nvidia_smi_gpu_info", ""), ("nvidia_smi_utilization_gpu_ratio", ""), ("nvidia_smi_temperature_gpu", ""),
        ("nvidia_smi_power_draw_watts", ""), ("nvidia_smi_memory_used_bytes", ""), ("container_cpu_utilization_ratio", ""),
        ("container_memory_percent_ratio", ""), ("container_memory_usage_total_bytes", ""), ("container_memory_usage_limit_bytes", ""),
    ];

    internal static readonly (string Metric, string Selector)[] Counters =
    [
        ("node_cpu_seconds_total", ""), ("node_disk_read_bytes_total", ""), ("node_disk_written_bytes_total", ""),
        ("node_network_receive_bytes_total", Nics), ("node_network_transmit_bytes_total", Nics),
        ("container_network_io_usage_rx_bytes_total", ""), ("container_network_io_usage_tx_bytes_total", ""), ("container_blockio_io_service_bytes_recursive_total", ""),
        ("lucia_snmp_interface_io_bytes_total", ""),
        .. InferenceDashboard.Counters.Select(metric => (metric, "")),
    ];

    /// <summary>NetFlow's bytes arrive as deltas, under either name.</summary>
    internal const string Flows = "lucia_netflow_bytes";

    /// <summary>The recording rules for tier <paramref name="index"/>, each read through Thanos Query from the tier before it.</summary>
    internal static string Rules(int index)
    {
        var (name, _, _) = Tiers[index];
        var from = index == 0 ? "raw" : Tiers[index - 1].Name;
        string Rule(string record, string expr) =>
            $"      - record: {record}\n        expr: 'label_replace({expr}, \"tier\", \"\", \"\", \"\")'\n";
        string Raw(string metric, string selector) => $"{metric}{{{(selector.Length > 0 ? selector + "," : "")}tier=\"raw\"}}";
        var rules = string.Concat(Gauges
            .Select(item => Rule(item.Metric + ":avg", index == 0
                ? $"avg_over_time({Raw(item.Metric, item.Selector)}[1m])" : $"avg_over_time({item.Metric}:avg{{tier=\"{from}\"}}[{name}])"))
            .Concat(Counters.Select(item => Rule(item.Metric + ":rate", index == 0
                ? $"rate({Raw(item.Metric, item.Selector)}[2m])" : $"avg_over_time({item.Metric}:rate{{tier=\"{from}\"}}[{name}])")))
            .Append(Rule(Flows + ":sum", index == 0
                ? $"sum_over_time({{__name__=~\"{Flows}(_total)?\",tier=\"raw\"}}[1m])" : $"sum_over_time({Flows}:sum{{tier=\"{from}\"}}[{name}])")));
        return $"groups:\n  - name: tier-{name}\n    interval: {name}\n    rules:\n{rules}";
    }

    /// <summary>A gauge at its finest resolution kept at each moment: raw, then each tier's latest slice.</summary>
    internal static string Gauge(string metric, string selector) => Tiered($"{metric}{{{selector}}}", metric + ":avg", selector);

    /// <summary>A counter's per-second rate at its finest resolution kept at each moment.</summary>
    internal static string Rate(string metric, string selector) =>
        Tiered($"rate({metric}{{{selector}}}[$__rate_interval])", metric + ":rate", selector);

    private static string Tiered(string raw, string recorded, string selector) =>
        "(" + string.Join(" or ", Tiers.Select(tier =>
                $"max without (tier) (last_over_time({recorded}{{{(selector.Length > 0 ? selector + "," : "")}tier=\"{tier.Name}\"}}[{tier.Lookback}]))")
            .Prepend($"max without (tier) ({raw})")) + ")";
}
