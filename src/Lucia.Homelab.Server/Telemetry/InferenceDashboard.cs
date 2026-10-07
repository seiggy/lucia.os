using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lucia.Homelab.Server.Telemetry;

/// <summary>
/// Grafana's Inference dashboard: raw performance of every Local AI engine Lucia runs, side by side. Lucia Inference
/// (TensorSharp) reports <c>lucia_inference_*</c>; the relays scrape vLLM's, llama.cpp's and the Spark's TensorFold
/// metrics. llama.cpp keeps only running totals, so it has no time to first token, request time or request count, and
/// TensorFold doesn't count KV cache reuse.
/// </summary>
internal static class InferenceDashboard
{
    private static readonly object Prometheus = new { type = "prometheus", uid = "prometheus" };
    private const string Node = "lucia_node=~\"$node\"";
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private const string TokensPerSecond = "suffix: tok/s";
    public const string Uid = "lucia-inference";

    private sealed record Engine(string Name, string Model, string? Requests, string? Duration, string? FirstToken,
        string Input, string? Cached, string Output, string PromptSeconds, string? GenerationSeconds, string GenerationTokens);

    // Each engine's per-request sums. Prompt time is the time to first token (Lucia) or prefill (vLLM, llama.cpp), and
    // generation time excludes it, so tokens/second is per request, however many run at once.
    private static readonly Engine[] Engines =
    [
        new("Lucia", "model", "lucia_inference_duration_seconds_count", "lucia_inference_duration_seconds_sum", "lucia_inference_time_to_first_token_seconds_sum",
            "lucia_inference_input_tokens_total", "lucia_inference_cached_tokens_total", "lucia_inference_output_tokens_total",
            "lucia_inference_time_to_first_token_seconds_sum", null, "lucia_inference_output_tokens_total"),
        new("vLLM", "model_name", "vllm:request_success_total", "vllm:e2e_request_latency_seconds_sum", "vllm:time_to_first_token_seconds_sum",
            "vllm:prompt_tokens_total", "vllm:prompt_tokens_cached_total", "vllm:generation_tokens_total",
            "vllm:request_prefill_time_seconds_sum", "vllm:request_decode_time_seconds_sum", "vllm:generation_tokens_total"),
        new("llama.cpp", "model", null, null, null,
            "llamacpp:prompt_tokens_total", "llamacpp:prompt_tokens_cached_total", "llamacpp:tokens_predicted_total",
            "llamacpp:prompt_seconds_total", "llamacpp:tokens_predicted_seconds_total", "llamacpp:tokens_predicted_total"),
        new("TensorFold", "model", "tensorfold:request_latency_seconds_count", "tensorfold:request_latency_seconds_sum", "tensorfold:time_to_first_token_seconds_sum",
            "tensorfold:prompt_tokens_total", null, "tensorfold:generation_tokens_total",
            "tensorfold:time_to_first_token_seconds_sum", null, "tensorfold:generation_tokens_total"),
    ];

    private static string Rate(string metric, string by) => $"sum by (lucia_node, {by}) (rate({metric}{{{Node}}}[$__rate_interval]))";
    private static string Range(string metric) => $"(sum(increase({metric}{{{Node}}}[$__range])) or vector(0))";

    // llama.cpp counts cached prompt tokens apart from processed ones; the others count every prompt token.
    private static string Input(Engine engine, Func<string, string> of) => engine.Name == "llama.cpp" ? $"({of(engine.Input)} + {of(engine.Cached!)})" : of(engine.Input);
    private static string Processed(Engine engine, Func<string, string> of) =>
        engine.Name == "llama.cpp" || engine.Cached is null ? of(engine.Input) : $"({of(engine.Input)} - {of(engine.Cached)})";
    private static string Generating(Engine engine, Func<string, string> of) =>
        engine.GenerationSeconds is { } seconds ? of(seconds) : $"({of(engine.Duration!)} - {of(engine.FirstToken!)})";

    public static string Json()
    {
        var id = 0;
        object Row(string title, int y) => new { type = "row", id = ++id, title, collapsed = false, gridPos = new { x = 0, y, w = 24, h = 1 }, panels = Array.Empty<object>() };
        object Target(int index, string expr, string? legend = null) =>
            new { refId = ((char)('A' + index)).ToString(), datasource = Prometheus, expr, legendFormat = legend };
        object Stat(string title, string description, int slot, string unit, string expr, int decimals) => new
        {
            type = "stat", id = ++id, title, description, datasource = Prometheus, gridPos = new { x = slot % 4 * 6, y = 1 + slot / 4 * 4, w = 6, h = 4 },
            fieldConfig = new { defaults = new { unit, decimals, color = new { mode = "fixed", fixedColor = "text" } }, overrides = Array.Empty<object>() },
            options = new { reduceOptions = new { calcs = new[] { "lastNotNull" }, fields = "", values = false }, colorMode = "none", graphMode = "none", textMode = "value", justifyMode = "center" },
            targets = new[] { Target(0, expr) },
        };
        // One query per engine, so each legend names its engine.
        object Series(string title, string description, int x, int y, int w, string unit, Func<Engine, Func<string, string>, string?> expr) => new
        {
            type = "timeseries", id = ++id, title, description, datasource = Prometheus, gridPos = new { x, y, w, h = 8 },
            fieldConfig = new
            {
                defaults = new { unit, min = 0, custom = new { drawStyle = "line", lineWidth = 1, fillOpacity = 8, showPoints = "never", spanNulls = true } },
                overrides = Array.Empty<object>(),
            },
            options = new { legend = new { displayMode = "list", placement = "bottom", showLegend = true }, tooltip = new { mode = "multi", sort = "desc" } },
            targets = Engines.Select(engine => (engine, expr: expr(engine, metric => Rate(metric, engine.Model))))
                .Where(item => item.expr is not null)
                .Select((item, index) => Target(index, item.expr!, $"{{{{{item.engine.Model}}}}} · {{{{lucia_node}}}} ({item.engine.Name})")),
        };
        string Total(Func<Engine, string?> part) => string.Join(" + ", Engines.Select(part).OfType<string>());
        string Ratio(Func<Engine, string?> top, Func<Engine, string?> bottom) => $"({Total(top)}) / ({Total(bottom)})";
        const string NoLlama = " llama.cpp doesn't report this.";
        object[] panels =
        [
            Row("Selected time range, all engines", 0),
            Stat("Requests", "Finished requests." + NoLlama, 0, "short",
                Total(engine => engine.Requests is null ? null : Range(engine.Requests)), 0),
            Stat("Input tokens", "Prompt tokens, including those reused from the KV cache.", 1, "short", Total(engine => Input(engine, Range)), 0),
            Stat("Output tokens", "Generated tokens.", 2, "short", Total(engine => Range(engine.Output)), 0),
            Stat("KV cache reused", "Prompt tokens served from the KV cache instead of being processed again.", 3, "short", Total(engine => engine.Cached is null ? null : Range(engine.Cached)), 0),
            Stat("Time to first token", "Average wait for the first generated token." + NoLlama, 4, "s",
                Ratio(engine => engine.FirstToken is null ? null : Range(engine.FirstToken), engine => engine.FirstToken is null ? null : Range(engine.FirstToken.Replace("_sum", "_count"))), 2),
            Stat("Prompt processing", "Prompt tokens processed per second of prompt processing, excluding reused ones.", 5, TokensPerSecond,
                Ratio(engine => Processed(engine, Range), engine => Range(engine.PromptSeconds)), 0),
            Stat("Generation", "Tokens generated per second of generation, per request.", 6, TokensPerSecond,
                Ratio(engine => Range(engine.GenerationTokens), engine => Generating(engine, Range)), 1),
            Stat("Total elapsed time", "Average time for a whole request." + NoLlama, 7, "s",
                Ratio(engine => engine.Duration is null ? null : Range(engine.Duration), engine => engine.Duration is null ? null : Range(engine.Duration.Replace("_sum", "_count"))), 2),
            Row("Latency", 9),
            Series("Time to first token", "Average wait for the first generated token, per model." + NoLlama, 0, 10, 12, "s",
                (engine, of) => engine.FirstToken is null ? null : $"{of(engine.FirstToken)} / {of(engine.FirstToken.Replace("_sum", "_count"))}"),
            Series("Total elapsed time", "Average time for a whole request, per model." + NoLlama, 12, 10, 12, "s",
                (engine, of) => engine.Duration is null ? null : $"{of(engine.Duration)} / {of(engine.Duration.Replace("_sum", "_count"))}"),
            Row("Speed", 18),
            Series("Prompt processing", "Prompt tokens processed per second, excluding ones reused from the KV cache.", 0, 19, 12, TokensPerSecond,
                (engine, of) => $"{Processed(engine, of)} / {of(engine.PromptSeconds)}"),
            Series("Generation", "Tokens generated per second while generating, per request.", 12, 19, 12, TokensPerSecond,
                (engine, of) => $"{of(engine.GenerationTokens)} / {Generating(engine, of)}"),
            Row("Volume", 27),
            Series("Requests per minute", "Finished requests." + NoLlama, 0, 28, 6, "short",
                (engine, of) => engine.Requests is null ? null : $"60 * {of(engine.Requests)}"),
            Series("Input tokens per minute", "Prompt tokens, including those reused from the KV cache.", 6, 28, 6, "short",
                (engine, of) => $"60 * {Input(engine, of)}"),
            Series("Output tokens per minute", "Generated tokens.", 12, 28, 6, "short", (engine, of) => $"60 * {of(engine.Output)}"),
            Series("KV cache reused per minute", "Prompt tokens served from the KV cache.", 18, 28, 6, "short", (engine, of) => engine.Cached is null ? null : $"60 * {of(engine.Cached)}"),
        ];
        return JsonSerializer.Serialize(new
        {
            uid = Uid, title = "Inference", tags = new[] { "lucia" }, editable = false, graphTooltip = 1, refresh = "30s", schemaVersion = 41,
            time = new { from = "now-6h", to = "now" },
            templating = new
            {
                list = new[]
                {
                    new
                    {
                        name = "node", label = "Machine", type = "query", datasource = Prometheus,
                        query = new { query = "label_values(lucia_node)", refId = "machines" },
                        definition = "label_values(lucia_node)", refresh = 2, multi = true, includeAll = true, sort = 1,
                        current = new { text = new[] { "All" }, value = new[] { "$__all" } },
                    },
                },
            },
            panels,
        }, Options);
    }
}
