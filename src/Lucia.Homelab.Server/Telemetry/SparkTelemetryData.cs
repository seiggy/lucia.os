using System.Globalization;

namespace Lucia.Homelab.Server.Telemetry;

public sealed record SparkSample(
    DateTimeOffset Timestamp, double? CpuPercent, int? CpuCores, double? LoadAverage,
    long? MemoryTotalBytes, long? MemoryAvailableBytes, long? StorageTotalBytes, long? StorageAvailableBytes,
    string? NetworkInterface, double? ReceiveBytesPerSecond, double? TransmitBytesPerSecond,
    string? GpuName, double? GpuPercent, double? GpuTemperatureCelsius, double? GpuPowerWatts,
    bool UnifiedMemory, double? UptimeSeconds, string[] Unavailable);

public sealed record SparkTelemetrySnapshot(
    bool Enabled, string State, string Message, int SampleIntervalSeconds, int RetentionSeconds,
    SparkSample? Latest, SparkSample[] History);

public readonly record struct CpuCounters(ulong Total, ulong Idle, int Cores);
public readonly record struct NetworkCounters(string Interface, ulong Received, ulong Transmitted);
public sealed record GpuReading(string Name, double? Percent, double? Temperature, double? Power);

public static class SparkMetricParsing
{
    public static CpuCounters Cpu(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var fields = lines.FirstOrDefault(line => line.StartsWith("cpu ", StringComparison.Ordinal))?
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            ?? throw new InvalidDataException("The host CPU counters are missing.");
        if (fields.Length < 9) throw new InvalidDataException("The host CPU counters are incomplete.");
        var values = fields.Skip(1).Take(8).Select(value => ulong.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        // guest/guest_nice are already included in user/nice and must not be counted twice.
        var total = values.Aggregate(0UL, (sum, value) => checked(sum + value));
        var cores = lines.Count(line => line.Length > 3 && line.StartsWith("cpu", StringComparison.Ordinal) && char.IsAsciiDigit(line[3]));
        if (cores < 1 || total == 0) throw new InvalidDataException("The host CPU topology is unavailable.");
        return new(total, checked(values[3] + values[4]), cores);
    }

    public static double? CpuPercent(CpuCounters previous, CpuCounters current)
    {
        if (current.Total <= previous.Total || current.Idle < previous.Idle || current.Cores != previous.Cores) return null;
        var total = current.Total - previous.Total;
        var idle = current.Idle - previous.Idle;
        return idle > total ? null : 100d * (total - idle) / total;
    }

    public static (long Total, long Available) Memory(string text)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0 || fields[0] is not ("MemTotal:" or "MemAvailable:")) continue;
            if (fields.Length != 3 || fields[2] != "kB") throw new InvalidDataException("Unexpected host memory units.");
            values.Add(fields[0], checked(long.Parse(fields[1], CultureInfo.InvariantCulture) * 1024));
        }
        if (!values.TryGetValue("MemTotal:", out var total) || !values.TryGetValue("MemAvailable:", out var available)
            || total <= 0 || available < 0 || available > total)
            throw new InvalidDataException("The host memory counters are incomplete or invalid.");
        return (total, available);
    }

    public static double FirstNumber(string text)
    {
        var field = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? throw new InvalidDataException("The host counter is empty.");
        if (!double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value < 0)
            throw new InvalidDataException("The host counter is invalid.");
        return value;
    }

    public static NetworkCounters Network(string counters, string routes)
    {
        var route = routes.Split('\n').Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length >= 8 && fields[1] == "00000000"
                && uint.TryParse(fields[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags) && (flags & 1) != 0)
            .OrderBy(fields => uint.Parse(fields[6], CultureInfo.InvariantCulture)).FirstOrDefault();
        if (route is null) throw new InvalidDataException("The host has no IPv4 default-route interface.");
        foreach (var line in counters.Split('\n'))
        {
            var separator = line.LastIndexOf(':');
            if (separator < 0 || line[..separator].Trim() != route[0]) continue;
            var fields = line[(separator + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 16) throw new InvalidDataException("The host network counters are incomplete.");
            return new(route[0], ulong.Parse(fields[0], CultureInfo.InvariantCulture), ulong.Parse(fields[8], CultureInfo.InvariantCulture));
        }
        throw new InvalidDataException("The host default-route interface has no counters.");
    }

    public static (double? Receive, double? Transmit) NetworkRates(NetworkCounters previous, NetworkCounters current, double seconds)
    {
        if (previous.Interface != current.Interface || !double.IsFinite(seconds) || seconds <= 0
            || current.Received < previous.Received || current.Transmitted < previous.Transmitted)
            return (null, null);
        return ((current.Received - previous.Received) / seconds, (current.Transmitted - previous.Transmitted) / seconds);
    }

    public static GpuReading Gpu(string text)
    {
        var lines = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 1) throw new InvalidDataException("The GPU query did not return one device.");
        var fields = lines[0].Split(',', StringSplitOptions.TrimEntries);
        if (fields.Length != 4 || string.IsNullOrWhiteSpace(fields[0]) || fields[0].Length > 128 || fields[0].Any(char.IsControl))
            throw new InvalidDataException("The GPU query returned invalid metadata.");
        static double? Metric(string field, double minimum, double maximum)
        {
            if (field is "[N/A]" or "N/A" or "[Not Supported]" or "Not Supported") return null;
            if (!double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value) || value < minimum || value > maximum)
                throw new InvalidDataException("The GPU query returned an invalid measurement.");
            return value;
        }
        return new(fields[0], Metric(fields[1], 0, 100), Metric(fields[2], -50, 200), Metric(fields[3], 0, 10000));
    }
}

public sealed class SparkTelemetryBuffer(TimeProvider clock)
{
    public const int IntervalSeconds = 10;
    public const int RetentionSeconds = 3600;
    public const int MaximumSamples = RetentionSeconds / IntervalSeconds + 1;
    private readonly Queue<SparkSample> _samples = new();
    private readonly object _gate = new();
    private bool _hasCollected;

    public bool Add(SparkSample sample)
    {
        lock (_gate)
        {
            var reset = _samples.Count > 0 && sample.Timestamp < _samples.Last().Timestamp;
            if (reset) _samples.Clear();
            _samples.Enqueue(sample);
            _hasCollected = true;
            Prune(clock.GetUtcNow());
            return reset;
        }
    }

    public SparkTelemetrySnapshot Snapshot(bool enabled)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            Prune(now);
            var history = _samples.ToArray();
            var latest = history.LastOrDefault();
            var (state, message) = !enabled ? ("Unavailable", "Spark monitoring is not configured.")
                : latest is null ? (_hasCollected ? ("Unavailable", "No host readings are available from the last hour.")
                    : ("Starting", "Waiting for the first host readings."))
                : now - latest.Timestamp > TimeSpan.FromSeconds(30) ? ("Stale", "The latest host readings are out of date.")
                : latest.CpuCores is null && latest.MemoryTotalBytes is null && latest.StorageTotalBytes is null && latest.GpuName is null
                    ? ("Unavailable", "Host readings are unavailable.")
                : latest.StorageTotalBytes is > 0 && latest.StorageAvailableBytes / (double)latest.StorageTotalBytes < 0.05
                    ? ("Attention", "The host volume has less than 5% space available.")
                : latest.MemoryTotalBytes is > 0 && latest.MemoryAvailableBytes / (double)latest.MemoryTotalBytes < 0.03
                    ? ("Attention", "The host has less than 3% memory available.")
                : latest.Unavailable.Length > 0 ? ("Partial", "Some host readings are unavailable.")
                : ("Healthy", "Spark is reporting normally.");
            return new(enabled, state, message, IntervalSeconds, RetentionSeconds, latest, history);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromSeconds(RetentionSeconds);
        while (_samples.Count > 0 && (_samples.Peek().Timestamp < cutoff || _samples.Count > MaximumSamples))
            _samples.Dequeue();
    }
}
