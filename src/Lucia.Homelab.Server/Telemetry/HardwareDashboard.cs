using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lucia.Homelab.Server.Telemetry;

/// <summary>
/// Grafana's Hardware dashboard: every machine Lucia manages, from the metrics their telemetry relays scrape
/// (node-exporter and nvidia_gpu_exporter, labelled with the machine's name as <c>instance</c>).
/// </summary>
internal static class HardwareDashboard
{
    private static readonly object Prometheus = new { type = "prometheus", uid = "prometheus" };
    private const string Node = "instance=~\"$node\"";
    private const string Nics = MetricTiers.Nics, Disks = MetricTiers.Disks;
    // Raw metrics last an hour; the tiers remember machines for longer ranges.
    private const string Machines = "label_values({__name__=~\"node_uname_info(:avg)?\"}, instance)";
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public const string Uid = "lucia-hardware";

    public static string Json()
    {
        // Every metric reads through MetricTiers, so long ranges fall back to its averaged tiers.
        static string G(string metric, string selector = Node) => MetricTiers.Gauge(metric, selector);
        static string R(string metric, string selector = Node) => MetricTiers.Rate(metric, selector);
        var idle = R("node_cpu_seconds_total", $"mode=\"idle\",{Node}");
        var Cpu = $"100 * (1 - avg by (instance) ({idle}))";
        var Memory = $"100 * (1 - max by (instance) ({G("node_memory_MemAvailable_bytes")}) / max by (instance) ({G("node_memory_MemTotal_bytes")}))";
        var root = $"mountpoint=\"/\",{Disks},{Node}";
        var Root = $"100 * (1 - max by (instance) ({G("node_filesystem_avail_bytes", root)}) / max by (instance) ({G("node_filesystem_size_bytes", root)}))";
        var Gpu = $"on (instance, uuid) group_left (name) max by (instance, uuid, name) ({G("nvidia_smi_gpu_info")})";
        (string Name, string Expr, string Unit, bool Gauge)[] columns =
        [
            ("CPU", Cpu, "percent", true),
            ("Memory", Memory, "percent", true),
            ("Disk /", Root, "percent", true),
            ("Cores", $"count by (instance) ({idle})", "none", false),
            ("RAM", $"max by (instance) ({G("node_memory_MemTotal_bytes")})", "bytes", false),
            ("Hottest sensor", $"max by (instance) ({G("node_hwmon_temp_celsius")})", "celsius", false),
            ("GPU", $"100 * avg by (instance) ({G("nvidia_smi_utilization_gpu_ratio")})", "percent", true),
            ("GPU temp", $"max by (instance) ({G("nvidia_smi_temperature_gpu")})", "celsius", false),
            ("GPU power", $"sum by (instance) ({G("nvidia_smi_power_draw_watts")})", "watt", false),
        ];
        var id = 0;
        object Row(string title, int y) => new { type = "row", id = ++id, title, collapsed = false, gridPos = new { x = 0, y, w = 24, h = 1 }, panels = Array.Empty<object>() };
        object Series(string title, string description, int x, int y, int w, string unit, (string Expr, string Legend)[] targets,
            bool mirrored = false, double? max = null) => new
        {
            type = "timeseries", id = ++id, title, description, datasource = Prometheus, gridPos = new { x, y, w, h = 8 },
            fieldConfig = new
            {
                defaults = new
                {
                    unit, min = mirrored ? (double?)null : 0, max,
                    custom = new { drawStyle = "line", lineWidth = 1, fillOpacity = 8, showPoints = "never", spanNulls = true },
                },
                // Received above the axis, sent (or written) below it.
                overrides = mirrored
                    ? new object[] { new { matcher = new { id = "byRegexp", options = ".*(sent|write)$" }, properties = new object[] { new { id = "custom.transform", value = "negative-Y" } } } }
                    : [],
            },
            options = new { legend = new { displayMode = "list", placement = "bottom", showLegend = true }, tooltip = new { mode = "multi", sort = "desc" } },
            targets = targets.Select((target, index) => new { refId = ((char)('A' + index)).ToString(), datasource = Prometheus, expr = target.Expr, legendFormat = target.Legend }),
        };
        static object Step(string color, double? value) => new Dictionary<string, object?> { ["color"] = color, ["value"] = value };
        var table = new
        {
            type = "table", id = ++id, title = "Machines", description = "Now, for every machine Lucia manages. Empty GPU columns mean no NVIDIA GPU.",
            datasource = Prometheus, gridPos = new { x = 0, y = 1, w = 24, h = 7 },
            targets = columns.Select((column, index) => new
            {
                refId = ((char)('A' + index)).ToString(), datasource = Prometheus, expr = column.Expr, instant = true, range = false, format = "table",
            }),
            transformations = new object[]
            {
                new { id = "merge", options = new { } },
                new
                {
                    id = "organize",
                    options = new
                    {
                        excludeByName = new Dictionary<string, bool> { ["Time"] = true },
                        renameByName = columns.Select((column, index) => (Key: "Value #" + (char)('A' + index), column.Name))
                            .Append((Key: "instance", Name: "Machine")).ToDictionary(item => item.Key, item => item.Name),
                    },
                },
            },
            fieldConfig = new
            {
                defaults = new { custom = new { align = "auto", cellOptions = new { type = "auto" } }, decimals = 0 },
                overrides = columns.Select(column => new
                {
                    matcher = new { id = "byName", options = column.Name },
                    properties = column.Gauge
                        ? new object[]
                        {
                            new { id = "unit", value = (object)column.Unit }, new { id = "min", value = (object)0 }, new { id = "max", value = (object)100 },
                            new { id = "custom.cellOptions", value = (object)new { type = "gauge", mode = "basic", valueDisplayMode = "text" } },
                            new { id = "thresholds", value = (object)new { mode = "absolute", steps = new[] { Step("green", null), Step("#EAB839", 75), Step("red", 90) } } },
                        }
                        : [new { id = "unit", value = (object)column.Unit }],
                }),
            },
            options = new { showHeader = true, cellHeight = "sm", sortBy = new[] { new { displayName = "Machine", desc = false } } },
        };
        object[] panels =
        [
            Row("Machines", 0),
            table,
            Row("CPU and memory", 8),
            Series("CPU", "Busy share of all cores.", 0, 9, 8, "percent", [(Cpu, "{{instance}}")], max: 100),
            Series("Memory", "Used share of RAM, not counting reclaimable cache.", 8, 9, 8, "percent", [(Memory, "{{instance}}")], max: 100),
            Series("Load per core", "1-minute load average divided by cores. Above 1 means work is waiting.", 16, 9, 8, "none",
                [($"max by (instance) ({G("node_load1")}) / count by (instance) ({idle})", "{{instance}}")]),
            Row("Storage and network", 17),
            Series("Disk space", "Used share of each local filesystem.", 0, 18, 8, "percent",
                [($"100 * (1 - max by (instance, mountpoint) ({G("node_filesystem_avail_bytes", $"{Disks},{Node}")}) / max by (instance, mountpoint) ({G("node_filesystem_size_bytes", $"{Disks},{Node}")}))",
                    "{{instance}} {{mountpoint}}")], max: 100),
            Series("Disk throughput", "Bytes read (above) and written (below) across all disks.", 8, 18, 8, "Bps",
                [($"sum by (instance) ({R("node_disk_read_bytes_total")})", "{{instance}} read"),
                 ($"sum by (instance) ({R("node_disk_written_bytes_total")})", "{{instance}} write")], mirrored: true),
            Series("Network", "Bytes received (above) and sent (below) on physical interfaces.", 16, 18, 8, "Bps",
                [($"sum by (instance) ({R("node_network_receive_bytes_total", $"{Nics},{Node}")})", "{{instance}} received"),
                 ($"sum by (instance) ({R("node_network_transmit_bytes_total", $"{Nics},{Node}")})", "{{instance}} sent")], mirrored: true),
            Row("Temperatures", 26),
            Series("Sensors", "The hottest reading of each sensor chip.", 0, 27, 12, "celsius",
                [($"max by (instance, chip) ({G("node_hwmon_temp_celsius")}) * on (instance, chip) group_left (chip_name) max by (instance, chip, chip_name) ({G("node_hwmon_chip_names")})",
                    "{{instance}} {{chip_name}}")]),
            Series("GPUs", "Each NVIDIA GPU's core temperature.", 12, 27, 12, "celsius", [($"max by (instance, uuid) ({G("nvidia_smi_temperature_gpu")}) * {Gpu}", "{{instance}} {{name}}")]),
            Row("GPUs", 35),
            Series("GPU utilization", "Share of time each GPU was busy.", 0, 36, 8, "percent",
                [($"100 * max by (instance, uuid) ({G("nvidia_smi_utilization_gpu_ratio")}) * {Gpu}", "{{instance}} {{name}}")], max: 100),
            Series("GPU memory", "Memory in use on each GPU. GPUs that share system memory don't report it.", 8, 36, 8, "bytes",
                [($"max by (instance, uuid) ({G("nvidia_smi_memory_used_bytes")}) * {Gpu}", "{{instance}} {{name}}")]),
            Series("GPU power", "Power draw of each GPU.", 16, 36, 8, "watt",
                [($"max by (instance, uuid) ({G("nvidia_smi_power_draw_watts")}) * {Gpu}", "{{instance}} {{name}}")]),
        ];
        return JsonSerializer.Serialize(new
        {
            uid = Uid, title = "Hardware", tags = new[] { "lucia" }, editable = false, graphTooltip = 1, refresh = "30s", schemaVersion = 41,
            time = new { from = "now-6h", to = "now" },
            templating = new
            {
                list = new[]
                {
                    new
                    {
                        name = "node", label = "Machine", type = "query", datasource = Prometheus,
                        query = new { query = Machines, refId = "machines" }, definition = Machines, refresh = 2, multi = true, includeAll = true, sort = 1,
                        current = new { text = new[] { "All" }, value = new[] { "$__all" } },
                    },
                },
            },
            panels,
        }, Options);
    }
}
