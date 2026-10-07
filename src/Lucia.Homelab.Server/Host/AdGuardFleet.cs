using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Stacks;

namespace Lucia.Homelab.Server.Host;

/// <param name="Setup">Lucia created it and hasn't finished its first-run setup and certificate yet.</param>
public sealed record AdGuardInstanceState(bool Setup = false, DateTimeOffset? HealthyAt = null, DateTimeOffset? SyncedAt = null, string? Error = null,
    string? SyncError = null);
/// <param name="Seen">Servers that have had an instance. Lucia doesn't put one back on a server where the owner deleted it.</param>
public sealed record AdGuardFleetState(string? Primary = null, string[]? Seen = null, Dictionary<string, AdGuardInstanceState>? Instances = null,
    DateTimeOffset? FailingSince = null, DateTimeOffset? PromotedAt = null, string? PromotedFrom = null);
public sealed record AdGuardInstanceStatus(string Name, string Node, string? Address, string? HostName, bool Primary, bool Healthy, bool Setup,
    DateTimeOffset? HealthyAt, DateTimeOffset? SyncedAt, string? Error);
public sealed record AdGuardFleetStatus(AdGuardInstanceStatus[] Instances, string? Problem, DateTimeOffset? PromotedAt, string? PromotedFrom);

/// <summary>
/// One AdGuard Home per managed server, each on an address of its own, so the network keeps DNS when a server is down. The primary
/// (the first instance, then whichever the owner or a failover picks) is the one the AdGuard connection, Lucia's DNS records and the
/// AdGuard name point at. Every minute adguardhome-sync copies its settings, lists, clients and rewrites to the others. When the
/// primary stops answering for three minutes, the healthy instance that synced most recently (within ten minutes) takes over.
/// Lucia never fails back by itself.
/// </summary>
public sealed class AdGuardFleet(StackStore stacks, ManagedNodeEnrollment nodes, DomainOnboardingStore domains,
    AdGuardConnectionService adguard, TimeProvider time, ILogger<AdGuardFleet> logger) : BackgroundService
{
    internal const string Template = "adguard", SyncTool = "/opt/adguardhome-sync/adguardhome-sync";
    internal static readonly TimeSpan FailoverAfter = TimeSpan.FromMinutes(3), SyncedWithin = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30), SyncEvery = TimeSpan.FromMinutes(1), Fresh = TimeSpan.FromSeconds(90);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private DateTimeOffset _lastSync;
    private string? _problem;
    private string FilePath => Path.Combine(domains.Root, "adguard-fleet.json");

    /// <summary>Raised when the primary or the set of instances changes, so DNS records and the certificate follow.</summary>
    public event Action? Changed;

    internal static string HostName(string node, string ns) => $"adguard-{node}.{ns}".ToLowerInvariant();

    public async Task<AdGuardFleetStatus> StatusAsync(CancellationToken ct)
    {
        var instances = await stacks.Instances(Template, ct);
        var ns = (await ManagedNodeDns.ActiveNaming(domains, ct))?.Namespace;
        var state = await ReadAsync(ct);
        var now = time.GetUtcNow();
        return new(instances.OrderBy(item => item.CreatedAt).Select(item =>
        {
            var entry = state.Instances?.GetValueOrDefault(item.Name) ?? new();
            return new AdGuardInstanceStatus(item.Name, item.Node, item.Address, ns is null ? null : HostName(item.Node, ns), item.Name == state.Primary,
                entry.HealthyAt > now - Fresh, entry.Setup, entry.HealthyAt, entry.SyncedAt, entry.Error ?? entry.SyncError);
        }).ToArray(), _problem, state.PromotedAt, state.PromotedFrom);
    }

    /// <summary>Makes a healthy instance the primary. Its settings replace the others' on the next sync.</summary>
    public async Task<AdGuardFleetStatus> MakePrimaryAsync(string name, CancellationToken ct)
    {
        var instance = (await stacks.Instances(Template, ct)).FirstOrDefault(item => item.Name == name)
            ?? throw new AdGuardManagementException(404, "adguard_instance_unknown", "There's no such AdGuard instance.");
        await _gate.WaitAsync(ct);
        try
        {
            var state = await ReadFileAsync(ct);
            if (state.Instances?.GetValueOrDefault(name) is not { Setup: false } entry || !(entry.HealthyAt > time.GetUtcNow() - Fresh)
                || !IPAddress.TryParse(instance.Address, out var address))
                throw new AdGuardManagementException(409, "adguard_instance_unhealthy", "Only a running, set-up instance can become the primary.");
            if (state.Primary != name)
            {
                await WriteAsync(state with { Primary = name, FailingSince = null }, ct);
                adguard.PrimaryAddress = address;
                logger.LogInformation("AdGuard primary changed to {Name} by the owner.", name);
                Changed?.Invoke();
            }
        }
        finally { _gate.Release(); }
        return await StatusAsync(ct);
    }

    /// <summary>Each instance's own name, and the connection's name pointing at the primary. Empty unless the fleet is running.</summary>
    internal async Task<AdGuardRewrite[]> RecordsAsync(string ns, CancellationToken ct)
    {
        var instances = await stacks.Instances(Template, ct);
        var primary = (await ReadAsync(ct)).Primary;
        var connection = await adguard.GetStatusAsync(ct);
        var records = instances.Where(item => item.Address is not null).Select(item => new AdGuardRewrite(HostName(item.Node, ns), item.Address!)).ToList();
        if (connection.BaseUrl is { } url && new Uri(url).IdnHost.ToLowerInvariant() is var host && host.EndsWith("." + ns, StringComparison.Ordinal)
            && instances.FirstOrDefault(item => item.Name == primary)?.Address is { } address)
            records.Add(new(host, address));
        return [.. records];
    }

    /// <summary>The set-up instances the certificate goes to, by address.</summary>
    internal async Task<IPAddress[]> TargetsAsync(CancellationToken ct)
    {
        var state = await ReadAsync(ct);
        return [.. (await stacks.Instances(Template, ct)).Where(item => state.Instances?.GetValueOrDefault(item.Name)?.Setup != true)
            .Select(item => IPAddress.TryParse(item.Address, out var address) ? address : null).OfType<IPAddress>()];
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await RunOnceAsync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning("AdGuard fleet check failed ({ErrorType}); retrying.", error.GetType().Name); }
            try { await _wake.WaitAsync(Tick, stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        bool changed;
        try { changed = await ReconcileAsync(ct); }
        finally { _gate.Release(); }
        if (changed) Changed?.Invoke();
    }

    private async Task<bool> ReconcileAsync(CancellationToken ct)
    {
        var connection = await adguard.GetStatusAsync(ct);
        var ns = (await ManagedNodeDns.ActiveNaming(domains, ct))?.Namespace;
        var instances = await stacks.Instances(Template, ct);
        var before = await ReadFileAsync(ct);
        var entries = (before.Instances ?? []).Where(pair => instances.Any(item => item.Name == pair.Key)).ToDictionary();
        var state = before with { Instances = entries, Seen = [.. (before.Seen ?? []).Union(instances.Select(item => item.Node)).Order(StringComparer.Ordinal)] };
        var now = time.GetUtcNow();
        _problem = !connection.Configured ? "Connect AdGuard first." : ns is null ? "Finish domain setup first." : instances.Length == 0 ? "Install AdGuard Home from Apps first." : null;
        if (_problem is not null)
        {
            adguard.PrimaryAddress = null;
            await WriteAsync(state with { Primary = instances.Length == 0 ? null : state.Primary }, ct);
            return before.Primary != state.Primary;
        }
        var primary = instances.FirstOrDefault(item => item.Name == state.Primary)
            ?? (state.Primary is null ? null : instances.FirstOrDefault(item => item.Name == Successor(instances, entries, now)))
            ?? instances.FirstOrDefault(item => item.Name == "adguard") ?? instances.MinBy(item => item.CreatedAt)!;

        foreach (var instance in instances)
        {
            var entry = entries.GetValueOrDefault(instance.Name) ?? new();
            if (!IPAddress.TryParse(instance.Address, out var address)) { entries[instance.Name] = entry with { Error = "It has no address of its own." }; continue; }
            try
            {
                if (entry.Setup) entry = await SetUpAsync(instance, address, entry, ct);
                if (!entry.Setup)
                    entry = await adguard.ProbeAsync(address, ct) ? entry with { HealthyAt = now, Error = null }
                        : entry with { Error = "AdGuard DNS isn't running." };
            }
            catch (AdGuardManagementException error) { entry = entry with { Error = error.Message }; }
            catch (HardwareOnboardingException error) { entry = entry with { Error = error.Message }; }
            entries[instance.Name] = entry;
        }
        bool Healthy(string name) => entries.GetValueOrDefault(name)?.HealthyAt == now;

        if (Healthy(primary.Name)) state = state with { FailingSince = null };
        else
        {
            var since = state.FailingSince ?? now;
            state = state with { FailingSince = since };
            if (now - since >= FailoverAfter && instances.FirstOrDefault(item => item.Name == Successor(instances.Where(item => item.Name != primary.Name), entries, now)) is { } next)
            {
                logger.LogWarning("AdGuard primary {Old} hasn't answered since {Since}; {New} takes over.", primary.Name, since, next.Name);
                state = state with { FailingSince = null, PromotedAt = now, PromotedFrom = primary.Name };
                primary = next;
            }
        }
        state = state with { Primary = primary.Name };
        adguard.PrimaryAddress = IPAddress.TryParse(primary.Address, out var pin) ? pin : null;

        if (Healthy(primary.Name))
        {
            foreach (var node in (await nodes.Facts(ct)).Where(node => node is { Online: true, Status.Runtime.State: "Ready" }
                && !state.Seen!.Contains(node.Hostname) && instances.All(item => item.Node != node.Hostname)))
            {
                var name = $"adguard-{node.Hostname}".ToLowerInvariant();
                if (NextAddress(instances.Select(item => item.Address), await stacks.TakenAddresses(ct)) is not { } address)
                { _problem = "There's no free address after the AdGuard addresses for another instance."; break; }
                try
                {
                    await stacks.Save(name, new(null, null, new(1, new(node.Hostname), new(Template, StackCatalog.Find(Template)!.Version), Address: address)), "Lucia", ct);
                    entries[name] = new(Setup: true);
                    state = state with { Seen = [.. state.Seen!.Append(node.Hostname).Order(StringComparer.Ordinal)] };
                    logger.LogInformation("Installing AdGuard {Name} on {Node} at {Address}.", name, node.Hostname, address);
                }
                catch (HardwareOnboardingException error) { _problem = $"Lucia couldn't add AdGuard to {node.Hostname}: {error.Message}"; }
            }
            if (now - _lastSync >= SyncEvery)
            {
                _lastSync = now;
                if (!File.Exists(SyncTool)) _problem ??= "adguardhome-sync isn't in this host image, so instances aren't kept in sync.";
                else
                {
                    var (username, password) = await adguard.AccountAsync(ct);
                    foreach (var replica in instances.Where(item => item.Name != primary.Name && Healthy(item.Name)))
                    {
                        var error = await SyncAsync(HostName(primary.Node, ns!), HostName(replica.Node, ns!), username, password, ct);
                        if (error is not null) logger.LogWarning("AdGuard sync to {Replica} failed: {Error}", replica.Name, error);
                        entries[replica.Name] = entries[replica.Name] with { SyncedAt = error is null ? now : entries[replica.Name].SyncedAt, SyncError = error };
                    }
                }
            }
        }
        await WriteAsync(state, ct);
        return before.Primary != state.Primary || !entries.Select(pair => (pair.Key, pair.Value.Setup)).Order()
            .SequenceEqual((before.Instances ?? []).Select(pair => (pair.Key, pair.Value.Setup)).Order());
    }

    /// <summary>
    /// A new instance: off an address another device answers on, through AdGuard's first-run wizard with the connection's account, then
    /// AdGuard's current certificate over plain HTTP to its address, once. Everything after that uses HTTPS.
    /// </summary>
    private async Task<AdGuardInstanceState> SetUpAsync(AppInstance instance, IPAddress address, AdGuardInstanceState entry, CancellationToken ct)
    {
        if (instance.AddressState == "InUse")
        {
            var instances = await stacks.Instances(Template, ct);
            if (NextAddress(instances.Select(item => item.Address), await stacks.TakenAddresses(ct)) is not { } next)
                return entry with { Error = $"Another device answers on {instance.Address}, and there's no free address after it." };
            await stacks.SaveStackAddress(instance.Name, new(next), "Lucia", ct);
            return entry with { Error = $"Another device answers on {instance.Address}; moving to {next}." };
        }
        await adguard.InstallAsync(address, ct);
        if (await AdGuardCertificateWorker.InstalledAsync(domains.Root, ct) is not { } certificate)
            return entry with { Error = "Waiting for AdGuard's certificate. Turn on Manage AdGuard's certificate." };
        try { await adguard.PushCertificateAsync(certificate.Names, certificate.Chain, certificate.Key, ct, address, plainHttp: true); }
        catch (AdGuardManagementException) { return entry with { Error = "Waiting for AdGuard to start." }; }
        logger.LogInformation("AdGuard {Name} is set up.", instance.Name);
        return entry with { Setup = false, Error = null };
    }

    /// <summary>The healthy instance that synced most recently, within ten minutes.</summary>
    internal static string? Successor(IEnumerable<AppInstance> candidates, IReadOnlyDictionary<string, AdGuardInstanceState> entries, DateTimeOffset now) =>
        candidates.Select(item => (item.Name, Entry: entries.GetValueOrDefault(item.Name)))
            .Where(item => item.Entry is { Setup: false } entry && entry.HealthyAt == now && entry.SyncedAt >= now - SyncedWithin)
            .OrderByDescending(item => item.Entry!.SyncedAt).Select(item => item.Name).FirstOrDefault();

    /// <summary>The first free address after the highest AdGuard address, in its /24.</summary>
    internal static string? NextAddress(IEnumerable<string?> fleet, IReadOnlySet<string> taken)
    {
        var highest = fleet.Select(item => IPAddress.TryParse(item, out var address) ? address.GetAddressBytes() : null).OfType<byte[]>()
            .Where(bytes => bytes.Length == 4).MaxBy(bytes => (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt32(bytes)));
        if (highest is null) return null;
        for (var last = highest[3] + 1; last < 255; last++)
        {
            var candidate = new IPAddress([highest[0], highest[1], highest[2], (byte)last]).ToString();
            if (!taken.Contains(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>adguardhome-sync's settings for one run from origin to replica: everything except DHCP and TLS, which stay per instance.</summary>
    internal static Dictionary<string, string> SyncEnvironment(string origin, string replica, string username, string password) => new()
    {
        ["ORIGIN_URL"] = $"https://{origin}", ["ORIGIN_USERNAME"] = username, ["ORIGIN_PASSWORD"] = password,
        ["REPLICA1_URL"] = $"https://{replica}", ["REPLICA1_USERNAME"] = username, ["REPLICA1_PASSWORD"] = password,
        ["REPLICA1_AUTO_SETUP"] = "false", ["RUN_ON_START"] = "true", ["API_PORT"] = "0", ["LOG_FORMAT"] = "json",
        ["FEATURES_DHCP_SERVER_CONFIG"] = "false", ["FEATURES_DHCP_STATIC_LEASES"] = "false", ["FEATURES_TLS_CONFIG"] = "false",
        ["HOME"] = Path.GetTempPath(),
    };

    /// <summary>
    /// One sync run. Without a cron schedule adguardhome-sync syncs once and exits. It exits 0 even when a step fails and only logs the
    /// error, so any error-level log line is a failure. Returns the error, or null.
    /// </summary>
    private static async Task<string?> SyncAsync(string origin, string replica, string username, string password, CancellationToken ct)
    {
        var start = new ProcessStartInfo(SyncTool, "run") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment.Clear();
        foreach (var (key, value) in SyncEnvironment(origin, replica, username, password)) start.Environment[key] = value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("adguardhome-sync didn't start.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(1));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (ct.IsCancellationRequested) throw;
            return "The sync took longer than a minute.";
        }
        return SyncError((await output) + "\n" + (await errors), process.ExitCode, password);
    }

    internal static string? SyncError(string log, int exitCode, string password)
    {
        foreach (var line in log.Split('\n'))
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || AdGuardValidation.String(root, "level")?.ToLowerInvariant() is not ("error" or "fatal" or "panic")) continue;
                var message = string.Join(": ", new[] { AdGuardValidation.String(root, "msg"), AdGuardValidation.String(root, "error")?.TrimStart(':', ' ') }.Where(item => !string.IsNullOrWhiteSpace(item)));
                return message.Length == 0 || message.Contains(password, StringComparison.Ordinal) ? "The sync failed." : message[..Math.Min(message.Length, 300)];
            }
            catch (JsonException) { }
        }
        return exitCode == 0 ? null : $"The sync exited with code {exitCode}.";
    }

    // Writes replace the file atomically, so reads need no lock and don't wait out a sync.
    private Task<AdGuardFleetState> ReadAsync(CancellationToken ct) => ReadFileAsync(ct);

    private async Task<AdGuardFleetState> ReadFileAsync(CancellationToken ct) => File.Exists(FilePath)
        ? JsonSerializer.Deserialize<AdGuardFleetState>(await File.ReadAllBytesAsync(FilePath, ct), Json) ?? new() : new();

    private async Task WriteAsync(AdGuardFleetState state, CancellationToken ct)
    {
        var temporary = FilePath + ".tmp";
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(state, Json), ct);
        File.Move(temporary, FilePath, overwrite: true);
    }
}
