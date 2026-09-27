using System.Text.Json;
using System.Text.Json.Nodes;
using Lucia.Homelab.Server.Host;

namespace Lucia.Homelab.Server.Domains;

public sealed record ManagedDomainRoute(string Name, string Origin, string Kind, string? Target, string Configuration);
public sealed record ManagedDomainRecord(string Hostname, string ExpectedAddress, string State, string Ownership,
    AdGuardRewrite[] Records, int TotalRecords);
public sealed record DomainOperationsSnapshot(DateTimeOffset CheckedAt, string Namespace, string IngressAddress,
    string? GatewayError, string? DnsError, ManagedDomainRoute[] Routes, ManagedDomainRecord[] DnsRecords);

public sealed class DomainOperationsService(DomainOnboardingStore store, DomainOnboardingOptions options,
    ILocalDnsProvider dns, ILogger<DomainOperationsService> logger, Nodes.ManagedNodeEnrollment? nodes = null)
{
    public async Task<DomainOperationsSnapshot> Read(CancellationToken ct)
    {
        var job = (await store.Read(ct)).Job;
        var profile = DomainActivationConfiguration.Read(store.Root);
        if (profile is null || job is null || job.Id != profile.ProfileId)
            throw new InvalidOperationException("An activated domain is required to view its managed configuration.");
        var nodeRecords = nodes is null ? [] : Nodes.ManagedNodeDns.Wanted(job.Plan.Naming, await nodes.Addresses(ct));
        string? gatewayError = null;
        ManagedDomainRoute[] routes;
        try
        {
            var path = Path.Combine(options.GatewayDirectory, DomainIngressConfiguration.FileName);
            DomainOnboardingStore.RejectLinks(path);
            if (!File.Exists(path)) throw new IOException("Missing gateway configuration.");
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Gateway configuration is oversized.");
            var actual = JsonNode.Parse(await File.ReadAllBytesAsync(path, ct));
            var certificate = job.Certificate ?? throw new InvalidDataException("Certificate receipt is missing.");
            var expected = JsonNode.Parse(DomainIngressConfiguration.Build(job.Plan.Naming, Path.Combine(store.Root, "certificates"),
                certificate.CertificateFile, certificate.KeyFile));
            routes = Routes(job, actual, expected);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            logger.LogWarning("Managed gateway configuration could not be read ({ErrorType}).", error.GetType().Name);
            gatewayError = "Lucia could not read its published gateway configuration. The addresses below are the saved configuration, not a confirmed live state.";
            routes = Routes(job, null, null);
        }
        IReadOnlyList<AdGuardRewrite>? records = null;
        AdGuardHealth? health = null;
        string? dnsError = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            health = await dns.GetHealthAsync(deadline.Token);
            records = await dns.ListRewritesAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning("Managed DNS records could not be read ({ErrorType}).", error.GetType().Name);
            dnsError = error is AdGuardManagementException known ? known.Message
                : "AdGuard could not be checked. Its current records are unavailable; saved expectations are still shown.";
        }
        var after = (await store.Read(ct)).Job;
        if (after?.Id != job.Id || after.Certificate?.CertificateSha256 != job.Certificate?.CertificateSha256)
            throw new InvalidOperationException("The domain configuration changed during this check. Refresh to read its current state.");
        return new(DateTimeOffset.UtcNow, job.Plan.Naming.Namespace, job.Plan.IngressAddress, gatewayError, dnsError,
            routes, [.. Records(job, records, health), .. Records(nodeRecords, records, health)]);
    }

    internal static ManagedDomainRoute[] Routes(DomainSetupJob job, JsonNode? actual, JsonNode? expected)
    {
        return new[] { ("Lucia", "domain-lucia", job.Plan.Naming.ServiceUrls.Lucia),
            ("Authentik", "domain-authentik", job.Plan.Naming.ServiceUrls.Authentik),
            ("Spark alias", "domain-spark", job.Plan.Naming.ServiceUrls.Spark) }.Select(item =>
        {
            var (name, id, origin) = item;
            var router = actual?["http"]?["routers"]?[id];
            var expectedRouter = expected?["http"]?["routers"]?[id];
            var redirect = id == "domain-spark";
            var actualTarget = redirect ? actual?["http"]?["middlewares"]?["domain-spark-redirect"]?["redirectRegex"]?["replacement"]
                : actual?["http"]?["services"]?[id]?["loadBalancer"]?["servers"]?[0]?["url"];
            var target = actualTarget?.GetValue<string>();
            if (target is not null && (!Uri.TryCreate(target, UriKind.Absolute, out var url)
                || url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0
                || url.Query.Length != 0 || url.Fragment.Length != 0 || target.Length > 2048))
                throw new InvalidDataException("Unsafe gateway destination metadata.");
            var actualService = redirect ? actual?["http"]?["middlewares"]?["domain-spark-redirect"] : actual?["http"]?["services"]?[id];
            var expectedService = redirect ? expected?["http"]?["middlewares"]?["domain-spark-redirect"] : expected?["http"]?["services"]?[id];
            var state = actual is null ? "Unavailable" : router is null ? "Missing"
                : JsonNode.DeepEquals(router, expectedRouter) && JsonNode.DeepEquals(actualService, expectedService)
                    && JsonNode.DeepEquals(actual?["tls"], expected?["tls"]) ? "Published" : "Changed";
            return new ManagedDomainRoute(name, origin, redirect ? "Redirect" : "Proxy", target, state);
        }).ToArray();
    }

    internal static ManagedDomainRecord[] Records(DomainSetupJob job, IReadOnlyList<AdGuardRewrite>? entries, AdGuardHealth? health) =>
        job.Plan.Naming.LocalHostnames.Select(name => Record(name, job.Plan.IngressAddress,
            job.CreatedRewrites.Contains(name) ? "Created by Lucia during setup" : "Pre-existing record", entries, health)).ToArray();

    internal static ManagedDomainRecord[] Records(IEnumerable<AdGuardRewrite> nodes, IReadOnlyList<AdGuardRewrite>? entries, AdGuardHealth? health) =>
        nodes.Select(node => Record(node.Domain, node.Answer, "Managed node, kept current from its heartbeats", entries, health)).ToArray();

    private static ManagedDomainRecord Record(string name, string expected, string ownership,
        IReadOnlyList<AdGuardRewrite>? entries, AdGuardHealth? health)
    {
        var found = entries?.Where(entry => entry.Domain.TrimEnd('.').Equals(name, StringComparison.OrdinalIgnoreCase)
            || (entry.Domain.StartsWith("*.", StringComparison.Ordinal)
                && name.EndsWith(entry.Domain[1..].TrimEnd('.'), StringComparison.OrdinalIgnoreCase))).ToArray() ?? [];
        var exact = found.Where(entry => entry.Domain.TrimEnd('.').Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        var state = entries is null || health is null ? "Unavailable"
            : !health.Running || !health.ProtectionEnabled || !health.RewritesEnabled ? "Disabled"
            : found.Length == 0 ? "Missing"
            : exact.Length == 1 && found.All(entry => entry.Enabled && entry.Answer == expected) ? "Matches"
            : exact.Length == 1 && !exact[0].Enabled && found.Length == 1 ? "Disabled" : "Conflict";
        return new ManagedDomainRecord(name, expected, state, ownership, found.Take(20).ToArray(), found.Length);
    }
}
