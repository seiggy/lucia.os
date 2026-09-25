using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lucia.Homelab.Server.Telemetry;

public sealed class SparkTelemetryOptions
{
    public bool Enabled { get; set; }
    public string ProcDirectory { get; set; } = "/host-metrics";
    public string StoragePath { get; set; } = "/data";
}

public static class SparkTelemetryExtensions
{
    public static void AddSparkTelemetry(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("SparkTelemetry").Get<SparkTelemetryOptions>() ?? new();
        if (options.Enabled && (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(options.ProcDirectory)
            || !Path.IsPathFullyQualified(options.StoragePath)))
            throw new InvalidOperationException("SparkTelemetry requires Linux and explicit absolute host-counter/storage paths.");
        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<SparkTelemetryBuffer>();
        builder.Services.AddHostedService<SparkTelemetryService>();
    }

    public static void MapSparkTelemetry(this WebApplication app) =>
        app.MapGet("/api/host/telemetry", (HttpContext context, SparkTelemetryBuffer buffer, SparkTelemetryOptions options) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(buffer.Snapshot(options.Enabled));
        }).RequireAuthorization("HostOwner").WithTags("Spark health");
}

internal sealed class SparkTelemetryService(SparkTelemetryOptions options, SparkTelemetryBuffer buffer,
    TimeProvider clock, ILogger<SparkTelemetryService> logger) : BackgroundService
{
    private CpuCounters? _cpu;
    private NetworkCounters? _network;
    private long? _networkTimestamp;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(SparkTelemetryBuffer.IntervalSeconds), clock);
        do
        {
            await Collect(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task Collect(CancellationToken cancellationToken)
    {
        var unavailable = new List<string>();
        double? cpuPercent = null, load = null, receive = null, transmit = null, uptime = null;
        int? cores = null;
        long? total = null, available = null, diskTotal = null, diskAvailable = null;
        string? networkInterface = null;
        GpuReading? gpu = null;
        async Task Read(string name, Func<Task> action)
        {
            try { await action(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException
                or OverflowException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                unavailable.Add(name);
                logger.LogWarning("Spark {Metric} reading unavailable ({ErrorType}).", name, error.GetType().Name);
            }
        }
        Task<string> FileText(string name) => File.ReadAllTextAsync(Path.Combine(options.ProcDirectory, name), cancellationToken);
        await Read("CPU", async () =>
        {
            var counters = SparkMetricParsing.Cpu(await FileText("stat"));
            cores = counters.Cores;
            cpuPercent = _cpu is { } previous ? SparkMetricParsing.CpuPercent(previous, counters) : null;
            _cpu = counters;
        });
        if (cores is null) _cpu = null;
        await Read("Memory", async () => { (total, available) = SparkMetricParsing.Memory(await FileText("meminfo")); });
        await Read("Load average", async () => load = SparkMetricParsing.FirstNumber(await FileText("loadavg")));
        await Read("Uptime", async () => uptime = SparkMetricParsing.FirstNumber(await FileText("uptime")));
        await Read("Network", async () =>
        {
            var counters = SparkMetricParsing.Network(await FileText("netdev"), await FileText("route"));
            var timestamp = clock.GetTimestamp();
            networkInterface = counters.Interface;
            if (_network is { } previous && _networkTimestamp is { } prior)
                (receive, transmit) = SparkMetricParsing.NetworkRates(previous, counters, clock.GetElapsedTime(prior, timestamp).TotalSeconds);
            _network = counters;
            _networkTimestamp = timestamp;
        });
        if (networkInterface is null) { _network = null; _networkTimestamp = null; }
        await Read("Host storage", () =>
        {
            var disk = new DriveInfo(options.StoragePath);
            var size = disk.TotalSize;
            var free = disk.AvailableFreeSpace;
            if (size <= 0 || free < 0 || free > size) throw new InvalidDataException("Host storage returned invalid capacity.");
            diskTotal = size;
            diskAvailable = free;
            return Task.CompletedTask;
        });
        await Read("GPU", async () => gpu = SparkMetricParsing.Gpu(await QueryGpu(cancellationToken)));
        if (gpu is { Percent: null } or { Temperature: null } or { Power: null })
            unavailable.Add("Some GPU sensors are not reported by the driver");
        var sample = new SparkSample(clock.GetUtcNow(), cpuPercent, cores, load, total, available, diskTotal, diskAvailable,
            networkInterface, receive, transmit, gpu?.Name, gpu?.Percent, gpu?.Temperature, gpu?.Power,
            gpu?.Name.Contains("GB10", StringComparison.OrdinalIgnoreCase) == true, uptime, unavailable.ToArray());
        if (buffer.Add(sample))
            logger.LogWarning("Spark telemetry history was restarted after a system clock adjustment.");
    }

    private static async Task<string> QueryGpu(CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("nvidia-smi")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "--id=0", "--query-gpu=name,utilization.gpu,temperature.gpu,power.draw", "--format=csv,noheader,nounits" }
            }
        };
        process.StartInfo.Environment["LC_ALL"] = "C";
        if (!process.Start()) throw new InvalidOperationException("The NVIDIA metrics command did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var output = ReadBounded(process.StandardOutput, timeout.Token);
            var errors = ReadBounded(process.StandardError, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text = await output;
            await errors;
            if (process.ExitCode != 0 || text.Length > 4096)
                throw new InvalidOperationException("The NVIDIA metrics command failed or returned oversized output.");
            return text;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The NVIDIA metrics command timed out.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

        }
    }

    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4097];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(count), cancellationToken);
            if (read == 0) break;
            count += read;
        }
        if (count > 4096) throw new InvalidDataException("The metrics command exceeded its output limit.");
        return new string(buffer, 0, count);
    }
}
