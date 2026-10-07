using System.Net;
using System.Text.Json;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Host;

namespace Lucia.Homelab.Server.Nodes;

internal sealed record ManagedNodeDnsState(int SchemaVersion, AdGuardRewrite[] Records);

/// <summary>
/// Keeps an AdGuard rewrite <c>hostname.namespace → last heartbeat address</c> for each managed node once a
/// domain is active. Lucia changes or removes only records it recorded as its own; any other record for the
/// same name is left alone and reported as a conflict on the Domains page.
/// </summary>
public sealed class ManagedNodeDns : BackgroundService
{
    private readonly DomainOnboardingStore _domains;
    private readonly DomainOnboardingOptions _options;
    private readonly ManagedNodeEnrollment _nodes;
    private readonly Stacks.StackStore _stacks;
    private readonly ILocalDnsProvider _dns;
    private readonly AdGuardFleet _fleet;
    private readonly ILogger<ManagedNodeDns> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);

    public ManagedNodeDns(DomainOnboardingStore domains, DomainOnboardingOptions options, ManagedNodeEnrollment nodes, Stacks.StackStore stacks,
        ILocalDnsProvider dns, AdGuardFleet fleet, ILogger<ManagedNodeDns> logger)
    {
        (_domains, _options, _nodes, _stacks, _dns, _fleet, _logger) = (domains, options, nodes, stacks, dns, fleet, logger);
        nodes.AddressChanged += Wake;
        stacks.RoutesChanged += Wake;
        fleet.Changed += Wake;
    }

    public void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await Sync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                _logger.LogWarning("Managed-node DNS records could not be updated ({ErrorType}); retrying later.", error.GetType().Name);
            }
            try { await _wake.WaitAsync(TimeSpan.FromMinutes(5), stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>The records Lucia wants for managed nodes and app routes under the active domain, or null without one.</summary>
    public async Task<AdGuardRewrite[]?> Wanted(CancellationToken ct) =>
        await ActiveJob(_domains, ct) is { Plan: var plan } job
            ? Wanted(plan.Naming, await _nodes.Addresses(ct), plan.IngressAddress, await _stacks.ActiveRoutes(ct), job.Public ? plan.Naming.Domain : null,
                await _fleet.RecordsAsync(plan.Naming.Namespace, ct)) : null;

    internal static async Task<DomainSetupJob?> ActiveJob(DomainOnboardingStore domains, CancellationToken ct)
    {
        var job = (await domains.Read(ct)).Job;
        var profile = DomainActivationConfiguration.Read(domains.Root);
        return profile is null || job is null || job.Id != profile.ProfileId ? null : job;
    }

    internal static async Task<DomainSetupPlan?> ActivePlan(DomainOnboardingStore domains, CancellationToken ct) => (await ActiveJob(domains, ct))?.Plan;

    /// <summary>The zone public names sit under while public access is on, otherwise null.</summary>
    internal static async Task<string?> PublicZone(DomainOnboardingStore domains, CancellationToken ct) =>
        await ActiveJob(domains, ct) is { Public: true } job ? job.Plan.Naming.Domain : null;

    internal static async Task<DomainNamingPlan?> ActiveNaming(DomainOnboardingStore domains, CancellationToken ct) =>
        (await ActivePlan(domains, ct))?.Naming;

    /// <summary>
    /// Node names point at each node; app route names point at the gateway, which forwards to the app's server. With public
    /// access, public names point at the gateway too, so the network reaches them directly rather than through Cloudflare.
    /// </summary>
    internal static AdGuardRewrite[] Wanted(DomainNamingPlan naming, IEnumerable<ManagedNodeAddress> nodes, string? ingress = null,
        IEnumerable<Stacks.ActiveRoute>? routes = null, string? zone = null, IEnumerable<AdGuardRewrite>? fleet = null) =>
        nodes.Where(node => IPAddress.TryParse(node.Address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && AdGuardTransport.IsPrivate(ip))
            .Select(node => new AdGuardRewrite($"{node.Hostname}.{naming.Namespace}".ToLowerInvariant(), node.Address))
            .Concat(ingress is null ? [] : (routes ?? []).Select(route => new AdGuardRewrite($"{route.Route.Host}.{naming.Namespace}".ToLowerInvariant(), ingress)))
            .Concat(ingress is null || zone is null ? [] : (routes ?? []).Where(route => route.Route.Public is not null)
                .Select(route => new AdGuardRewrite($"{route.Route.Public}.{zone}".ToLowerInvariant(), ingress)))
            .Concat(fleet ?? [])
            .Where(record => !naming.LocalHostnames.Contains(record.Domain, StringComparer.OrdinalIgnoreCase))
            .GroupBy(record => record.Domain, StringComparer.Ordinal).Where(group => group.Count() == 1)
            .Select(group => group.Single()).ToArray();

    internal async Task Sync(CancellationToken ct)
    {
        var job = await ActiveJob(_domains, ct);
        if (Path.IsPathFullyQualified(_options.GatewayDirectory) && Directory.Exists(_options.GatewayDirectory))
            Stacks.AppGateway.Publish(_options.GatewayDirectory, job is null ? null
                : Stacks.AppGateway.Build(await _stacks.ActiveRoutes(ct), job.Plan.Naming.Namespace, job.Public ? job.Plan.Naming.Domain : null));
        // AdGuard's own names follow the fleet, replacing a record set up by hand before it.
        var claimed = job is null ? null : (await _fleet.RecordsAsync(job.Plan.Naming.Namespace, ct)).Select(record => record.Domain).ToHashSet(StringComparer.Ordinal);
        foreach (var name in await Apply(_dns, _nodes.DnsStatePath, await Wanted(ct) ?? [], ct, claimed))
            _logger.LogInformation("Published local DNS for {Name}.", name);
    }

    internal static async Task<string[]> Apply(ILocalDnsProvider dns, string statePath, AdGuardRewrite[] wanted, CancellationToken ct,
        IReadOnlySet<string>? claimed = null)
    {
        var owned = ReadState(statePath);
        if (wanted.Length == 0 && owned.Count == 0) return [];
        var existing = await dns.ListRewritesAsync(ct);
        var published = new List<string>();
        foreach (var record in wanted)
        {
            var same = Same(existing, record.Domain);
            if (same.Any(entry => entry.Answer == record.Answer)) continue;
            // Another client's record for this name wins; only replace answers Lucia added itself, or claims.
            if (claimed?.Contains(record.Domain) != true && same.Any(entry => !owned.Contains(Key(entry)))) continue;
            owned.Add(Key(record));
            await WriteState(statePath, owned, ct);
            foreach (var old in same)
            {
                await dns.DeleteRewriteAsync(old, ct);
                owned.Remove(Key(old));
                await WriteState(statePath, owned, ct);
            }
            await dns.AddRewriteAsync(record, ct);
            published.Add(record.Domain);
        }
        var keep = wanted.Select(Key).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in owned.Where(key => !keep.Contains(key)).ToArray())
        {
            var (domain, answer) = Split(stale);
            foreach (var entry in Same(existing, domain).Where(entry => entry.Answer == answer))
                await dns.DeleteRewriteAsync(entry, ct);
            owned.Remove(stale);
            await WriteState(statePath, owned, ct);
        }
        return [.. published];
    }

    private static HashSet<string> ReadState(string path)
    {
        DomainOnboardingStore.RejectLinks(path);
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        var state = JsonSerializer.Deserialize<ManagedNodeDnsState>(CertbotFiles.ReadBounded(path, 262144), DomainOnboardingStore.Json);
        if (state is not { SchemaVersion: 1 }) throw new InvalidDataException("Managed-node DNS state is invalid.");
        return state.Records.Select(Key).ToHashSet(StringComparer.Ordinal);
    }

    private static Task WriteState(string path, HashSet<string> owned, CancellationToken ct)
    {
        DomainOnboardingStore.EnsureDirectory(Path.GetDirectoryName(path)!);
        return DomainOnboardingStore.WriteJson(path, new ManagedNodeDnsState(1,
            owned.Order(StringComparer.Ordinal).Select(key => { var (domain, answer) = Split(key); return new AdGuardRewrite(domain, answer); }).ToArray()), ct);
    }

    private static AdGuardRewrite[] Same(IEnumerable<AdGuardRewrite> entries, string domain) =>
        entries.Where(entry => entry.Domain.TrimEnd('.').Equals(domain, StringComparison.OrdinalIgnoreCase)).ToArray();

    private static string Key(AdGuardRewrite record) => record.Domain.TrimEnd('.').ToLowerInvariant() + " " + record.Answer;

    private static (string Domain, string Answer) Split(string key)
    {
        var space = key.IndexOf(' ');
        return (key[..space], key[(space + 1)..]);
    }
}
