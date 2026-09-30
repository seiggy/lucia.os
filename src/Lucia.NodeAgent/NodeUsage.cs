using System.Globalization;

namespace Lucia.NodeAgent;

/// <summary>
/// Live utilization for the Devices page: the CPU's busy share since the previous heartbeat, the CPU package temperature,
/// and NVIDIA GPU load and temperature. Anything the machine doesn't expose stays null rather than guessed.
/// </summary>
internal static class NodeUsage
{
    private static readonly Lock Gate = new();
    private static (long Busy, long Total)? previousCpu;
    private static (double? Percent, double? Temperature) gpu;

    internal static (double? Percent, double? Temperature) Gpu { get { lock (Gate) return gpu; } }

    /// <summary>Samples the GPUs every 30 seconds, off the heartbeat path, since <c>nvidia-smi</c> can take a moment.</summary>
    internal static async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            var reading = File.Exists("/usr/bin/nvidia-smi")
                ? ParseNvidia(await NodeRuntime.TryCaptureAsync("/usr/bin/nvidia-smi",
                    ["--query-gpu=utilization.gpu,temperature.gpu", "--format=csv,noheader,nounits"], token) ?? "")
                : (null, null);
            lock (Gate) gpu = reading;
        } while (await timer.WaitForNextTickAsync(token));
    }

    /// <summary>Averages load and takes the hottest GPU from <c>nvidia-smi --query-gpu=utilization.gpu,temperature.gpu</c>.</summary>
    internal static (double? Percent, double? Temperature) ParseNvidia(string output)
    {
        List<double> loads = [], temperatures = [];
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(16))
        {
            var fields = line.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length != 2) continue;
            if (Number(fields[0]) is { } load && load <= 100) loads.Add(load);
            if (Number(fields[1]) is { } temperature && Temperature(temperature) is not null) temperatures.Add(temperature);
        }
        return (loads.Count > 0 ? loads.Average() : null, temperatures.Count > 0 ? temperatures.Max() : null);
    }

    /// <summary>Busy share of all CPUs since the previous call, from <c>/proc/stat</c>'s first line; null on the first call.</summary>
    internal static double? Cpu(string? stat)
    {
        var sample = ParseStat(stat);
        lock (Gate)
        {
            var before = previousCpu;
            previousCpu = sample;
            if (sample is not { } now || before is not { } then || now.Total <= then.Total || now.Busy < then.Busy) return null;
            return Math.Clamp(100.0 * (now.Busy - then.Busy) / (now.Total - then.Total), 0, 100);
        }
    }

    internal static (long Busy, long Total)? ParseStat(string? stat)
    {
        var line = stat?.Split('\n').FirstOrDefault(line => line.StartsWith("cpu ", StringComparison.Ordinal));
        var fields = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).ToArray();
        if (fields is not { Length: >= 4 }) return null;
        long total = 0;
        var values = new long[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            if (!long.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]) || values[i] > long.MaxValue / 16)
                return null;
            total += values[i];
        }
        // idle + iowait; guest time is already counted in user.
        var idle = values[3] + (values.Length > 4 ? values[4] : 0);
        return (total - idle, total);
    }

    /// <summary>
    /// The CPU package temperature from hwmon: AMD <c>k10temp</c>/<c>zenpower</c> (Tctl/Tdie), Intel <c>coretemp</c>
    /// (Package id 0), or an ARM <c>cpu_thermal</c> zone; then the <c>x86_pkg_temp</c> thermal zone. Virtual machines have none.
    /// </summary>
    internal static double? CpuTemperature(string sysClass)
    {
        string? Read(string path)
        {
            try { return File.Exists(path) ? DiskSafety.Read(path, 256).Trim() : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NodeAgentException or ArgumentException) { return null; }
        }
        double? Millidegrees(string path) => Number(Read(path)) is { } value ? Temperature(value / 1000) : null;
        string[] Directories(string path, string pattern)
        {
            try { return Directory.Exists(path) ? Directory.GetDirectories(path, pattern).Order(StringComparer.Ordinal).Take(64).ToArray() : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }
        var monitors = Directories(Path.Combine(sysClass, "hwmon"), "hwmon*").Select(path => (Path: path, Name: Read(Path.Combine(path, "name")))).ToArray();
        foreach (var (name, labels) in new[] { ("k10temp", new[] { "Tctl", "Tdie" }), ("zenpower", ["Tdie", "Tctl"]),
            ("coretemp", ["Package id 0"]), ("cpu_thermal", []) })
            foreach (var monitor in monitors.Where(monitor => monitor.Name == name))
            {
                for (var i = 1; i <= 32; i++)
                {
                    var label = Read(Path.Combine(monitor.Path, $"temp{i}_label"));
                    if ((labels.Length == 0 || labels.Contains(label)) && Millidegrees(Path.Combine(monitor.Path, $"temp{i}_input")) is { } value)
                        return value;
                }
            }
        foreach (var zone in Directories(Path.Combine(sysClass, "thermal"), "thermal_zone*"))
            if (Read(Path.Combine(zone, "type")) == "x86_pkg_temp" && Millidegrees(Path.Combine(zone, "temp")) is { } value) return value;
        return null;
    }

    private static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value) && value >= 0 ? value : null;

    private static double? Temperature(double celsius) => celsius is > 0 and < 150 ? Math.Round(celsius, 1) : null;
}
