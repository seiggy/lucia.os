using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lucia.Homelab.Server.Telemetry;

/// <summary>
/// Grafana's Containers dashboard: CPU, memory, network and disk of every Docker container on every machine Lucia manages,
/// from the <c>docker_stats</c> metrics each telemetry relay sends (labelled <c>lucia_node</c> and <c>container_name</c>).
/// </summary>
internal static class ContainersDashboard
{
    private static readonly object Prometheus = new { type = "prometheus", uid = "prometheus" };
    private const string Scope = "lucia_node=~\"$node\",container_name=~\"$container\"";
    private const string Machines = "label_values({__name__=~\"container_cpu_utilization_ratio(:avg)?\"}, lucia_node)";
    private const string Names = "label_values({__name__=~\"container_cpu_utilization_ratio(:avg)?\",lucia_node=~\"$node\"}, container_name)";
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public const string Uid = "lucia-containers";

    public static string Json()
    {
        static string G(string metric, string selector = Scope) => MetricTiers.Gauge(metric, selector);
        static string R(string metric, string selector = Scope) => MetricTiers.Rate(metric, selector);
        const string By = "lucia_node, container_name";
        string Top(string expr) => $"topk(10, max by ({By}) ({expr}))";
        var cpu = G("container_cpu_utilization_ratio");
        var memory = G("container_memory_percent_ratio");
        var used = G("container_memory_usage_total_bytes");
        var rx = R("container_network_io_usage_rx_bytes_total");
        var tx = R("container_network_io_usage_tx_bytes_total");
        var read = R("container_blockio_io_service_bytes_recursive_total", $"operation=\"read\",{Scope}");
        var write = R("container_blockio_io_service_bytes_recursive_total", $"operation=\"write\",{Scope}");
        (string Name, string Expr, string Unit, bool Gauge)[] columns =
        [
            ("CPU", $"max by ({By}) ({cpu})", "percent", false),
            ("Memory", $"max by ({By}) ({memory})", "percent", true),
            ("Memory used", $"max by ({By}) ({used})", "bytes", false),
            ("Network in", $"sum by ({By}) ({rx})", "Bps", false),
            ("Network out", $"sum by ({By}) ({tx})", "Bps", false),
        ];
        var id = 0;
        object Row(string title, int y) => new { type = "row", id = ++id, title, collapsed = false, gridPos = new { x = 0, y, w = 24, h = 1 }, panels = Array.Empty<object>() };
        object Series(string title, string description, int x, int y, int w, string unit, (string Expr, string Legend)[] targets, bool mirrored = false) => new
        {
            type = "timeseries", id = ++id, title, description, datasource = Prometheus, gridPos = new { x, y, w, h = 8 },
            fieldConfig = new
            {
                defaults = new
                {
                    unit, min = mirrored ? (double?)null : 0,
                    custom = new { drawStyle = "line", lineWidth = 1, fillOpacity = 8, showPoints = "never", spanNulls = true },
                },
                // Received (or read) above the axis, sent (or written) below it.
                overrides = mirrored
                    ? new object[] { new { matcher = new { id = "byRegexp", options = ".*(out|write)$" }, properties = new object[] { new { id = "custom.transform", value = "negative-Y" } } } }
                    : [],
            },
            options = new { legend = new { displayMode = "list", placement = "bottom", showLegend = true }, tooltip = new { mode = "multi", sort = "desc" } },
            targets = targets.Select((target, index) => new { refId = ((char)('A' + index)).ToString(), datasource = Prometheus, expr = target.Expr, legendFormat = target.Legend }),
        };
        static object Step(string color, double? value) => new Dictionary<string, object?> { ["color"] = color, ["value"] = value };
        var table = new
        {
            type = "table", id = ++id, title = "Containers", description = "Now, for every container the selected machines run. CPU is a share of one core, so a busy multi-core container passes 100.",
            datasource = Prometheus, gridPos = new { x = 0, y = 1, w = 24, h = 12 },
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
                            .Append((Key: "lucia_node", Name: "Machine")).Append((Key: "container_name", Name: "Container"))
                            .ToDictionary(item => item.Key, item => item.Name),
                    },
                },
            },
            fieldConfig = new
            {
                defaults = new { custom = new { align = "auto", cellOptions = new { type = "auto" } }, decimals = 1 },
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
            options = new { showHeader = true, cellHeight = "sm", sortBy = new[] { new { displayName = "CPU", desc = true } } },
        };
        const string Legend = "{{container_name}} ({{lucia_node}})";
        object[] panels =
        [
            Row("Containers", 0),
            table,
            Row("CPU and memory", 13),
            Series("CPU", "The ten busiest containers, as a share of one core.", 0, 14, 8, "percent", [(Top(cpu), Legend)]),
            Series("Memory used", "The ten largest containers.", 8, 14, 8, "bytes", [(Top(used), Legend)]),
            Series("Memory share", "Share of each container's memory limit, or of the machine's RAM without one.", 16, 14, 8, "percent", [(Top(memory), Legend)]),
            Row("Network and disk", 22),
            Series("Network", "Bytes received (above) and sent (below) by the ten busiest containers.", 0, 23, 12, "Bps",
                [(Top(rx), "{{container_name}} in"), ($"topk(10, sum by ({By}) ({tx}))", "{{container_name}} out")], mirrored: true),
            Series("Disk I/O", "Bytes read (above) and written (below) by the ten busiest containers.", 12, 23, 12, "Bps",
                [($"topk(10, sum by ({By}) ({read}))", "{{container_name}} read"), ($"topk(10, sum by ({By}) ({write}))", "{{container_name}} write")], mirrored: true),
        ];
        object Variable(string name, string label, string query, bool all) => new
        {
            name, label, type = "query", datasource = Prometheus, query = new { query, refId = name }, definition = query, refresh = 2,
            multi = true, includeAll = true, sort = 1, allValue = all ? ".*" : null,
            current = new { text = new[] { "All" }, value = new[] { "$__all" } },
        };
        return JsonSerializer.Serialize(new
        {
            uid = Uid, title = "Containers", tags = new[] { "lucia" }, editable = false, graphTooltip = 1, refresh = "30s", schemaVersion = 41,
            time = new { from = "now-6h", to = "now" },
            templating = new { list = new[] { Variable("node", "Machine", Machines, true), Variable("container", "Container", Names, true) } },
            panels,
        }, Options);
    }
}
