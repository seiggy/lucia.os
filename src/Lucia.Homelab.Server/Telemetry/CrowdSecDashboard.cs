using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lucia.Homelab.Server.Telemetry;

/// <summary>
/// Grafana's Lucia CrowdSec dashboard. Alerts come from Loki: the engine sends each one to Observability's collector as a log
/// record (service <c>crowdsec</c>, labelled by kind) with the address, scenario, request and decision as structured metadata.
/// Bans and AppSec counters come from the engine's Prometheus metrics. Each offender row carries a line to paste to Lucia's
/// assistant, and the dashboard links to the Gateway protection page in Lucia.
/// </summary>
internal static class CrowdSecDashboard
{
    private static readonly object Loki = new { type = "loki", uid = "loki" };
    private static readonly object Prometheus = new { type = "prometheus", uid = "prometheus" };
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public const string Uid = "lucia-crowdsec";
    /// <summary>Stands for Lucia's web address in the JSON; the Observability app's compose swaps in <c>LUCIA_CROWDSEC_CONSOLE</c>.</summary>
    internal const string ConsolePlaceholder = "LUCIA_CONSOLE_ORIGIN";
    internal static readonly string[] Gauges = ["cs_active_decisions"];
    internal static readonly string[] Counters = ["cs_appsec_reqs_total", "cs_appsec_block_total", "cs_httpsource_hits_total"];
    /// <summary>CrowdSec's community dashboards, adapted by tools/telemetry/crowdsec-dashboards.mjs: their file names in Grafana.</summary>
    internal static readonly string[] Community = ["overview.json", "instance.json", "insight.json", "lapi.json"];

    /// <summary>One of the community dashboards, as vendored (see <c>Telemetry/CrowdSec/LICENSE</c>).</summary>
    internal static string CommunityJson(string file)
    {
        using var stream = typeof(CrowdSecDashboard).Assembly.GetManifestResourceStream("Lucia.Homelab.Server.Telemetry.CrowdSec." + file)
            ?? throw new InvalidOperationException($"The CrowdSec dashboard {file} isn't embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private const string Alerts = "{service_name=\"crowdsec\"} | crowdsec_source_class=~\"$class\"";
    private const string Blocks = "{service_name=\"crowdsec\", crowdsec_kind=\"waf\"} | crowdsec_source_class=~\"$class\"";
    // One line per alert, ready to paste to Lucia's assistant.
    private const string Assistant = " | line_format \"CrowdSec alert: {{.source_address}} ({{.crowdsec_source_class}}) {{.crowdsec_scenario}}"
        + "{{if .url_path}} on {{.http_request_method}} {{.url_path}}{{end}}{{if .server_address}} at {{.server_address}}{{end}}"
        + "{{if .user_agent_original}}, user agent {{.user_agent_original}}{{end}}{{if .source_geo_country_iso_code}}, from {{.source_geo_country_iso_code}}{{end}}"
        + ", decision {{.crowdsec_decision}}{{if .crowdsec_duration}} {{.crowdsec_duration}}{{end}}\"";

    public static string Json()
    {
        var id = 0;
        var gateway = new { title = "Gateway protection in Lucia", url = ConsolePlaceholder + "/#/security", targetBlank = true };
        object Row(string title, int y) => new { type = "row", id = ++id, title, collapsed = false, gridPos = new { x = 0, y, w = 24, h = 1 }, panels = Array.Empty<object>() };
        object Target(object datasource, string expr, string? legend = null, bool instant = false, string refId = "A") => new
        {
            refId, datasource, expr, legendFormat = legend, queryType = instant ? "instant" : "range", instant = instant ? true : (bool?)null,
            range = instant ? false : (bool?)null,
        };
        object Stat(string title, string description, int x, object target, string color) => new
        {
            type = "stat", id = ++id, title, description, gridPos = new { x, y = 1, w = 4, h = 4 }, targets = new[] { target },
            fieldConfig = new { defaults = new { unit = "none", decimals = 0, color = new { mode = "fixed", fixedColor = color } }, overrides = Array.Empty<object>() },
            options = new { reduceOptions = new { calcs = new[] { "lastNotNull" }, fields = "", values = false }, colorMode = "value", graphMode = "none", textMode = "value" },
        };
        object Series(string title, string description, int x, int y, int w, object[] targets, bool bars = false) => new
        {
            type = "timeseries", id = ++id, title, description, gridPos = new { x, y, w, h = 8 }, targets,
            fieldConfig = new
            {
                defaults = new
                {
                    unit = bars ? "none" : "reqps", min = 0,
                    custom = new { drawStyle = bars ? "bars" : "line", lineWidth = 1, fillOpacity = bars ? 80 : 10, showPoints = "never", spanNulls = !bars,
                        stacking = new { mode = bars ? "normal" : "none" } },
                },
                overrides = Array.Empty<object>(),
            },
            options = new { legend = new { displayMode = "list", placement = "bottom", showLegend = true }, tooltip = new { mode = "multi", sort = "desc" } },
        };
        object Logs(string title, string description, int x, int y, int w, string expr) => new
        {
            type = "logs", id = ++id, title, description, gridPos = new { x, y, w, h = 9 }, targets = new[] { Target(Loki, expr) },
            options = new { showTime = true, wrapLogMessage = true, sortOrder = "Descending", enableLogDetails = true, dedupStrategy = "none", prettifyLogMessage = false },
        };
        static object Override(string field, params object[] properties) => new { matcher = new { id = "byName", options = field }, properties };
        static object Property(string id, object value) => new { id, value };
        // The names groupBy gives its columns, and what the table calls them.
        var columns = new (string Field, string Name)[]
        {
            ("source_address", "Address"), ("crowdsec_scenario", "Scenario"), ("url_path (lastNotNull)", "Path"), ("user_agent_original (lastNotNull)", "User agent"),
            ("source_geo_country_iso_code (lastNotNull)", "Country"), ("crowdsec_source_class (lastNotNull)", "Source"), ("Line (count)", "Alerts"),
            ("Time (max)", "Last seen"), ("crowdsec_decision (lastNotNull)", "Decision"), ("Line (lastNotNull)", "For the assistant"),
        };
        var offenders = new
        {
            type = "table", id = ++id, title = "Offenders",
            description = "Every address with an alert in the time range, one row per address and scenario. Click a cell to copy it: the last column is "
                + "a line to paste to Lucia's assistant. The address links to Gateway protection in Lucia.",
            gridPos = new { x = 0, y = 15, w = 24, h = 12 },
            targets = new[] { Target(Loki, Alerts + Assistant) },
            transformations = new object[]
            {
                new { id = "extractFields", options = new { source = "labels" } },
                new
                {
                    id = "groupBy",
                    options = new
                    {
                        fields = new Dictionary<string, object>
                        {
                            ["source_address"] = new { operation = "groupby" },
                            ["crowdsec_scenario"] = new { operation = "groupby" },
                            ["url_path"] = new { operation = "aggregate", aggregations = new[] { "lastNotNull" } },
                            ["user_agent_original"] = new { operation = "aggregate", aggregations = new[] { "lastNotNull" } },
                            ["source_geo_country_iso_code"] = new { operation = "aggregate", aggregations = new[] { "lastNotNull" } },
                            ["crowdsec_source_class"] = new { operation = "aggregate", aggregations = new[] { "lastNotNull" } },
                            ["crowdsec_decision"] = new { operation = "aggregate", aggregations = new[] { "lastNotNull" } },
                            ["Time"] = new { operation = "aggregate", aggregations = new[] { "max" } },
                            ["Line"] = new { operation = "aggregate", aggregations = new[] { "count", "lastNotNull" } },
                        },
                    },
                },
                new
                {
                    id = "organize",
                    options = new
                    {
                        indexByName = columns.Select((column, index) => (column.Field, index)).ToDictionary(item => item.Field, item => item.index),
                        renameByName = columns.ToDictionary(column => column.Field, column => column.Name),
                    },
                },
                new { id = "sortBy", options = new { sort = new[] { new { field = "Last seen", desc = true } } } },
            },
            fieldConfig = new
            {
                defaults = new { custom = new { align = "auto", cellOptions = new { type = "auto" }, inspect = true, filterable = true } },
                overrides = new[]
                {
                    Override("Address", Property("links", new[] { gateway })),
                    Override("Last seen", Property("unit", "dateTimeAsIso")),
                    Override("Alerts", Property("unit", "none"), Property("custom.width", 80)),
                    Override("For the assistant", Property("custom.width", 420)),
                },
            },
            options = new { showHeader = true, cellHeight = "sm", footer = new { show = false } },
        };
        object[] panels =
        [
            Row("In the time range", 0),
            Stat("Alerts", "Alerts CrowdSec raised: scenarios and AppSec blocks.", 0, Target(Loki, $"sum(count_over_time({Alerts} [$__range])) or vector(0)", instant: true), "orange"),
            Stat("Bans", "Alerts that came with a ban, CrowdSec's own and those placed by hand or by Lucia's assistant.", 4,
                Target(Loki, $"sum(count_over_time({Alerts} | crowdsec_decision=\"ban\" [$__range])) or vector(0)", instant: true), "red"),
            Stat("AppSec blocks", "Requests AppSec refused, the owner's custom rules included.", 8,
                Target(Loki, $"sum(count_over_time({Blocks} [$__range])) or vector(0)", instant: true), "purple"),
            Stat("Addresses", "Distinct addresses with an alert.", 12, Target(Loki, $"count(sum by (source_address) (count_over_time({Alerts} [$__range]))) or vector(0)", instant: true), "blue"),
            Stat("Active bans", "Bans in force now from this lab's alerts, by hand or by Lucia's assistant. The community blocklist isn't counted.", 16,
                Target(Prometheus, "sum(cs_active_decisions{origin!~\"CAPI|lists\"}) or vector(0)", instant: true), "red"),
            Stat("Community blocklist", "Addresses the community blocklist bans now.", 20,
                Target(Prometheus, "sum(cs_active_decisions{origin=~\"CAPI|lists\"}) or vector(0)", instant: true), "text"),
            Row("Over time", 5),
            Series("Alerts by scenario", "How many alerts each scenario or AppSec rule raised.", 0, 6, 12,
                [Target(Loki, $"sum by (crowdsec_scenario) (count_over_time({Alerts} [$__auto]))", "{{crowdsec_scenario}}")], bars: true),
            Series("Alerts by source", "lan: your network, recorded only. public: the internet, banned. trusted: Cloudflare's proxies, never banned.", 12, 6, 6,
                [Target(Loki, $"sum by (crowdsec_source_class) (count_over_time({Alerts} [$__auto]))", "{{crowdsec_source_class}}")], bars: true),
            Series("AppSec", "Requests the gateway asked AppSec about, and how many it blocked.", 18, 6, 6,
                [
                    Target(Prometheus, $"sum({MetricTiers.Rate("cs_appsec_reqs_total", "")})", "checked"),
                    Target(Prometheus, $"sum({MetricTiers.Rate("cs_appsec_block_total", "")})", "blocked", refId: "B"),
                ]),
            Row("Offenders", 14),
            offenders,
            new
            {
                type = "table", id = ++id, title = "Top offenders", description = "The ten addresses with the most alerts in the time range. The address links to Gateway protection in Lucia.",
                gridPos = new { x = 0, y = 27, w = 12, h = 9 },
                targets = new[] { Target(Loki, $"topk(10, sum by (source_address) (count_over_time({Alerts} [$__range])))", "{{source_address}}", instant: true) },
                transformations = new object[]
                {
                    // Grafana reads a Loki instant query as tables of Time, the address and "Value #A"; merged, they're one.
                    new { id = "merge", options = new { } },
                    new { id = "organize", options = new { excludeByName = new Dictionary<string, bool> { ["Time"] = true },
                        renameByName = new Dictionary<string, string> { ["source_address"] = "Address", ["Value #A"] = "Alerts" } } },
                    new { id = "sortBy", options = new { sort = new[] { new { field = "Alerts", desc = true } } } },
                },
                fieldConfig = new
                {
                    defaults = new { custom = new { align = "auto", inspect = true } },
                    overrides = new[]
                    {
                        Override("Address", Property("links", new[] { gateway })),
                        Override("Alerts", Property("unit", "none"), Property("decimals", 0),
                            Property("custom.cellOptions", new { type = "gauge", mode = "basic", valueDisplayMode = "text" })),
                    },
                },
                options = new { showHeader = true, cellHeight = "sm" },
            },
            new
            {
                type = "table", id = ++id, title = "Active bans by scenario", description = "Bans in force now, by why and who placed them: crowdsec, lucia (the owner) or lucia-assistant.",
                gridPos = new { x = 12, y = 27, w = 12, h = 9 },
                targets = new[] { new { refId = "A", datasource = Prometheus, expr = "sum by (reason, origin) (cs_active_decisions{origin!~\"CAPI|lists\"})", instant = true, range = false, format = "table" } },
                transformations = new object[]
                {
                    new { id = "organize", options = new { excludeByName = new Dictionary<string, bool> { ["Time"] = true }, renameByName = new Dictionary<string, string> { ["reason"] = "Scenario", ["origin"] = "Placed by", ["Value"] = "Bans" } } },
                },
                fieldConfig = new { defaults = new { custom = new { align = "auto", inspect = true } }, overrides = Array.Empty<object>() },
                options = new { showHeader = true, cellHeight = "sm" },
            },
            Row("Recent", 36),
            Logs("AppSec blocks", "The newest requests AppSec refused: rule, request and user agent. Copy a line for Lucia's assistant.", 0, 37, 12, Blocks + Assistant),
            Logs("Bans", "The newest bans: CrowdSec's, the owner's and the assistant's.", 12, 37, 12, Alerts + " | crowdsec_decision=\"ban\"" + Assistant),
        ];
        var classes = new[] { "lan", "public", "trusted", "range", "other" };
        return JsonSerializer.Serialize(new
        {
            uid = Uid, title = "Lucia CrowdSec", tags = new[] { "lucia", "crowdsec" }, editable = false, graphTooltip = 1, refresh = "1m", schemaVersion = 41,
            description = "CrowdSec's alerts from Loki and its bans from Prometheus. Copy a row and ask Lucia's assistant to look into it.",
            time = new { from = "now-24h", to = "now" },
            links = new[] { new { title = gateway.title, type = "link", url = gateway.url, targetBlank = true, icon = "shield" } },
            templating = new
            {
                list = new object[]
                {
                    new
                    {
                        name = "class", label = "Source", type = "custom", query = string.Join(",", classes), multi = true, includeAll = true, allValue = ".*",
                        current = new { text = new[] { "All" }, value = new[] { "$__all" } },
                        options = Array.Empty<object>(),
                    },
                },
            },
            panels,
        }, Options);
    }
}
