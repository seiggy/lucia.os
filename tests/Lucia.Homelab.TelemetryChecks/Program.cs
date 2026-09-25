using Lucia.Homelab.Server.Telemetry;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Reject(Action action)
{
    try { action(); }
    catch (Exception error) when (error is InvalidDataException or FormatException or OverflowException or ArgumentException)
    {
        checks++;
        return;
    }
    throw new InvalidOperationException("Malformed metrics were accepted.");
}
var first = SparkMetricParsing.Cpu("cpu  100 0 100 700 100 0 0 0 50 0\ncpu0 0\ncpu1 0\n");
var second = SparkMetricParsing.Cpu("cpu  150 0 150 780 120 0 0 0 99 0\ncpu0 0\ncpu1 0\n");
Check(first.Total == 1000 && first.Idle == 800 && first.Cores == 2, "CPU guest time was double-counted.");
Check(SparkMetricParsing.CpuPercent(first, second) == 50, "CPU deltas are incorrect.");
Check(SparkMetricParsing.CpuPercent(second, first) is null, "CPU reset became a utilization value.");
Check(SparkMetricParsing.CpuPercent(first, first) is null, "Zero-duration CPU sampling produced a value.");
Reject(() => SparkMetricParsing.Cpu("cpu 0 0 0\n"));
var memory = SparkMetricParsing.Memory("MemTotal: 1000 kB\nMemFree: 100 kB\nMemAvailable: 400 kB\n");
Check(memory == (1024000, 409600), "Memory used MemFree instead of MemAvailable.");
Reject(() => SparkMetricParsing.Memory("MemTotal: 1000 kB\nMemFree: 400 kB\n"));
Reject(() => SparkMetricParsing.Memory("MemTotal: 1000 kB\nMemAvailable: 1001 kB\n"));
Check(SparkMetricParsing.FirstNumber("12345.50 789.0") == 12345.5, "Uptime parsing is incorrect.");
Reject(() => SparkMetricParsing.FirstNumber("NaN"));
const string routes = "Iface Destination Gateway Flags RefCnt Use Metric Mask\n" +
    "enP7s7 00000000 0100A8C0 0003 0 0 100 00000000\n" +
    "docker0 00000000 010011AC 0003 0 0 1000 00000000\n";
var net = SparkMetricParsing.Network("enP7s7: 1000 0 0 0 0 0 0 0 2000 0 0 0 0 0 0 0\n", routes);
Check(net.Interface == "enP7s7", "Network metric chose a container bridge.");
Check(SparkMetricParsing.NetworkRates(net, net with { Received = 2000, Transmitted = 4000 }, 10) == (100, 200), "Network rates are not bytes/second.");
Check(SparkMetricParsing.NetworkRates(net, net with { Received = 1 }, 10) == (null, null), "Network counter reset became traffic.");
Check(SparkMetricParsing.NetworkRates(net, net with { Interface = "other" }, 10) == (null, null), "Interface change became a traffic spike.");
Reject(() => SparkMetricParsing.Network("lo: 0", "Iface Destination"));
var gpu = SparkMetricParsing.Gpu("NVIDIA GB10, 0, 37, 10.11\n");
Check(gpu.Name == "NVIDIA GB10" && gpu.Percent == 0 && gpu.Temperature == 37 && gpu.Power == 10.11, "Real idle GPU values were lost.");
Check(SparkMetricParsing.Gpu("NVIDIA GB10, [N/A], N/A, [Not Supported]").Percent is null, "Unsupported GPU value became zero.");
Reject(() => SparkMetricParsing.Gpu("NVIDIA GB10, 101, 37, 10"));
Reject(() => SparkMetricParsing.Gpu("NVIDIA GB10, invalid, 37, 10"));

var clock = new ManualClock();
var buffer = new SparkTelemetryBuffer(clock);
SparkSample Sample(DateTimeOffset timestamp, string[]? unavailable = null) =>
    new(timestamp, 20, 20, 0.3, 100000, 50000, 1000000, 500000, "enP7s7", 100, 200,
        "NVIDIA GB10", 0, 37, 10, true, 10000, unavailable ?? []);
Check(buffer.Snapshot(false).State == "Unavailable", "Disabled monitoring appeared healthy.");
Check(buffer.Snapshot(true).State == "Starting", "Empty history appeared healthy.");
var start = clock.Now;
for (var i = 0; i <= 400; i++)
{
    clock.Now = start.AddSeconds(i * 10);
    buffer.Add(Sample(clock.Now));
}
var snapshot = buffer.Snapshot(true);
Check(snapshot.History.Length == 361 && snapshot.History[0].Timestamp == clock.Now.AddHours(-1), "One-hour history boundary is incorrect.");
Check(snapshot.State == "Healthy", "Fresh complete telemetry was not available.");
clock.Now += TimeSpan.FromSeconds(31);
Check(buffer.Snapshot(true).State == "Stale", "Old readings were presented as current.");
buffer.Add(Sample(clock.Now, ["GPU"]));
Check(buffer.Snapshot(true).State == "Partial", "A missing metric was reported healthy.");
buffer.Add(Sample(clock.Now) with { StorageAvailableBytes = 1000 });
Check(buffer.Snapshot(true).State == "Attention", "Low host storage was not surfaced.");
buffer.Add(Sample(clock.Now) with { MemoryAvailableBytes = 100 });
Check(buffer.Snapshot(true).State == "Attention", "Low available memory was not surfaced.");
clock.Now += TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1));
Check(buffer.Snapshot(true).History.Length == 0 && buffer.Snapshot(true).Latest is null, "Cache retained data beyond one hour.");
buffer.Add(Sample(clock.Now));
clock.Now -= TimeSpan.FromMinutes(1);
Check(buffer.Add(Sample(clock.Now)) && buffer.Snapshot(true).History.Length == 1, "Clock rollback retained a misleading timeline.");
Console.WriteLine($"Spark telemetry checks passed ({checks} assertions). One-hour cache and real counter semantics verified.");

sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}
