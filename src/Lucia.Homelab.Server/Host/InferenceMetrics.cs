using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace Lucia.Homelab.Server.Host;

/// <summary>
/// Local AI's per-request performance as OpenTelemetry metrics. TensorSharp reports each finished chat request only as a
/// log entry, so this logger provider reads that entry's numbers and records them; the entry's text (the prompt and the
/// answer) never leaves it. Prompt tok/s is (input − cached) / time to first token, generation tok/s is output /
/// (duration − time to first token), and each duration's count is the request count. Requests take turns on the one loaded chat
/// model, so <see cref="LoadedModel"/> names the model each entry is about.
/// </summary>
public sealed class InferenceMetrics : ILoggerProvider
{
    public const string MeterName = "Lucia.Inference";
    private const string Source = "TensorSharp.Server.ModelService";
    private static readonly Meter Meter = new(MeterName);
    private static readonly InstrumentAdvice<double> Seconds = new() { HistogramBucketBoundaries = [0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 20, 40, 80, 160, 320] };
    private static readonly Counter<long> Input = Meter.CreateCounter<long>("lucia.inference.input_tokens", "{token}", "Prompt tokens, including those reused from the KV cache.");
    private static readonly Counter<long> Cached = Meter.CreateCounter<long>("lucia.inference.cached_tokens", "{token}", "Prompt tokens reused from the KV cache.");
    private static readonly Counter<long> Output = Meter.CreateCounter<long>("lucia.inference.output_tokens", "{token}", "Generated tokens.");
    private static readonly Histogram<double> FirstToken = Meter.CreateHistogram("lucia.inference.time_to_first_token", "s", "Time until the first generated token.", advice: Seconds);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram("lucia.inference.duration", "s", "Whole request: prompt processing and generation.", advice: Seconds);
    public static Func<string?> LoadedModel { get; set; } = () => null;

    public ILogger CreateLogger(string categoryName) => new Logger(this);
    public void Dispose() { }

    private void Record(IReadOnlyList<KeyValuePair<string, object?>> entry)
    {
        var values = entry.ToDictionary(item => item.Key, item => item.Value);
        if (values.GetValueOrDefault("{OriginalFormat}") is not string format
            || !(format.StartsWith("chat.complete ", StringComparison.Ordinal) || format.StartsWith("chat.cancelled ", StringComparison.Ordinal)))
            return;
        double Number(string key) => values.GetValueOrDefault(key) is IConvertible value ? value.ToDouble(CultureInfo.InvariantCulture) : 0;
        var tags = new TagList { { "model", LoadedModel() ?? "unknown" }, { "outcome", format.StartsWith("chat.complete ", StringComparison.Ordinal) ? "completed" : "cancelled" } };
        var (output, firstToken) = ((long)Number("Tokens"), Number("TimeToFirstTokenMs") / 1000);
        Input.Add((long)Number("PromptTokens"), tags);
        Cached.Add((long)Number("KvReusedTokens"), tags);
        Output.Add(output, tags);
        if (output > 0) FirstToken.Record(firstToken, tags);
        Duration.Record(Number("ElapsedMs") / 1000, tags);
    }

    private sealed class Logger(InferenceMetrics owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> entry) owner.Record(entry);
        }
    }

    /// <summary>Records TensorSharp's request metrics and keeps its (prompt-bearing) information logs out of every other provider.</summary>
    public static ILoggingBuilder Add(ILoggingBuilder logging)
    {
        logging.AddProvider(new InferenceMetrics());
        logging.AddFilter<InferenceMetrics>(null, LogLevel.None);
        logging.AddFilter<InferenceMetrics>(Source, LogLevel.Information);
        return logging.AddFilter("TensorSharp", LogLevel.Warning);
    }
}
