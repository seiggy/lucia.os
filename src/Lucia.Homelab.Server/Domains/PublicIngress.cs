using System.Net;
using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Nodes;

namespace Lucia.Homelab.Server.Domains;

/// <param name="State">Published, Adopted, Updated, Conflict or Removed.</param>
public sealed record PublicRecordStatus(string Name, string State, string? Detail = null);
/// <param name="Forward">The router's HTTPS forward: Forwarding, Adopted, Off, or null without UniFi.</param>
public sealed record PublicIngressStatus(DateTimeOffset CheckedAt, bool Enabled, string? WanAddress, string? Forward, string? ForwardError,
    PublicRecordStatus[] Records, string? RecordsError);

/// <summary>
/// While public access is on, keeps the internet's way in: proxied Cloudflare A records for Authentik and every public app
/// name at the network's WAN address, and the router's TCP 443 forward to the gateway's public entrypoint. Lucia changes
/// or removes only records carrying its comment; an existing record is adopted only when it already answers with the WAN
/// address. Off removes Lucia's records and disables its forward.
/// </summary>
public sealed class PublicIngress(DomainOnboardingStore domains, CloudflareDomainService cloudflare, UniFiConnectionService unifi,
    Stacks.StackStore stacks, ILogger<PublicIngress> logger) : BackgroundService
{
    public const string Comment = "Managed by Lucia";
    public const int Port = 8445;
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(10) };
    private readonly SemaphoreSlim _wake = new(0, 1);

    public PublicIngressStatus? Status { get; private set; }

    public void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        stacks.RoutesChanged += Wake;
        while (!stop.IsCancellationRequested)
        {
            try { await Sync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning("Public access could not be checked ({ErrorType}); retrying.", error.GetType().Name); }
            try { await _wake.WaitAsync(TimeSpan.FromMinutes(5), stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task Sync(CancellationToken ct)
    {
        var job = await ManagedNodeDns.ActiveJob(domains, ct);
        if (job is null || job.Phase == "Amending") return;
        var enabled = job.Public;
        string? wan = null, forward = null, forwardError = null, recordsError = null;
        var records = new List<PublicRecordStatus>();
        try { forward = await unifi.SetPublicForwardAsync(enabled, job.Plan.IngressAddress, Port, ct); }
        catch (UniFiException error) { forwardError = error.Message; }
        try
        {
            if ((await cloudflare.GetStatusAsync(ct)).Configured)
            {
                if (enabled) wan = await WanAddress(ct);
                var zone = job.Plan.Naming.Domain;
                var wanted = !enabled ? [] : (await stacks.ActiveRoutes(ct)).Where(route => route.Route.Public is not null)
                    .Select(route => $"{route.Route.Public}.{zone}").Prepend(DomainNames.ServiceHost(job.Plan.Naming.ServiceUrls.Authentik, zone))
                    .Distinct(StringComparer.Ordinal).ToArray();
                records.AddRange(await Reconcile(job.Plan.ZoneId, wanted, wan, ct));
            }
            else if (enabled) recordsError = "Connect Cloudflare to publish public names.";
        }
        catch (Exception error) when (error is CloudflareDomainException or HttpRequestException or InvalidDataException)
        {
            recordsError = error is CloudflareDomainException known ? known.Message : "Lucia couldn't find this network's internet address.";
        }
        Status = new(DateTimeOffset.UtcNow, enabled, wan, forward, forwardError, [.. records], recordsError);
    }

    private async Task<List<PublicRecordStatus>> Reconcile(string zoneId, string[] wanted, string? wan, CancellationToken ct)
    {
        var existing = await cloudflare.ListRecordsAsync(zoneId, ct);
        var result = new List<PublicRecordStatus>();
        foreach (var name in wanted)
        {
            var same = existing.Where(record => record.Name == name && record.Type is "A" or "AAAA" or "CNAME").ToArray();
            if (same.Length == 1 && same[0].Type == "A" && (same[0].Comment == Comment || same[0].Content == wan))
            {
                var record = same[0];
                if (record.Content == wan && record.Proxied && record.Comment == Comment) { result.Add(new(name, "Published")); continue; }
                await cloudflare.SaveAddressRecordAsync(zoneId, record.Id, name, wan!, Comment, ct);
                result.Add(new(name, record.Comment == Comment ? "Updated" : "Adopted"));
            }
            else if (same.Length == 0)
            {
                await cloudflare.SaveAddressRecordAsync(zoneId, null, name, wan!, Comment, ct);
                result.Add(new(name, "Published"));
            }
            else result.Add(new(name, "Conflict", $"Cloudflare already has {string.Join(", ", same.Select(record => $"{record.Type} {record.Content}"))} "
                + "for this name. Point it at this network's address, or delete it, and Lucia takes it over."));
        }
        foreach (var stale in existing.Where(record => record.Comment == Comment && !wanted.Contains(record.Name)))
        {
            await cloudflare.DeleteRecordAsync(zoneId, stale.Id, ct);
            result.Add(new(stale.Name, "Removed"));
            logger.LogInformation("Removed the public record for {Name}.", stale.Name);
        }
        return result;
    }

    /// <summary>The network's public IPv4 address, as Cloudflare sees it.</summary>
    private static async Task<string> WanAddress(CancellationToken ct)
    {
        var trace = await Http.GetStringAsync("https://1.1.1.1/cdn-cgi/trace", ct);
        var line = trace.Split('\n').FirstOrDefault(item => item.StartsWith("ip=", StringComparison.Ordinal))?[3..].Trim();
        return IPAddress.TryParse(line, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && !AdGuardTransport.IsPrivate(ip) && ip.ToString() == line ? line : throw new InvalidDataException("No public IPv4 address.");
    }
}
