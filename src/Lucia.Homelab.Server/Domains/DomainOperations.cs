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
    ILocalDnsProvider dns, ILogger<DomainOperationsService> logger, Nodes.ManagedNodeEnrollment? nodes = null,
    Stacks.StackStore? stacks = null)
{
    public async Task<DomainOperationsSnapshot> Read(CancellationToken ct)
    {
        var job = (await store.Read(ct)).Job;
        var profile = DomainActivationConfiguration.Read(store.Root);
        if (profile is null || job is null || job.Id != profile.ProfileId)
            throw new InvalidOperationException("An activated domain is required to view its managed configuration.");
        var nodeRecords = nodes is null ? [] : Nodes.ManagedNodeDns.Wanted(job.Plan.Naming, await nodes.Addresses(ct));
        var appRoutes = stacks is null ? [] : await stacks.ActiveRoutes(ct);
        var appRecords = Nodes.ManagedNodeDns.Wanted(job.Plan.Naming, [], job.Plan.IngressAddress, appRoutes);
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
            if (appRoutes.Length > 0)
            {
                var apps = Path.Combine(options.GatewayDirectory, Stacks.AppGateway.FileName);
                DomainOnboardingStore.RejectLinks(apps);
                var published = !File.Exists(apps) ? new JsonObject()
                    : new FileInfo(apps).Length > 262144 ? throw new InvalidDataException("App gateway configuration is oversized.")
                    : JsonNode.Parse(await File.ReadAllBytesAsync(apps, ct));
                routes = [.. routes, .. AppRoutes(appRoutes, job.Plan.Naming.Namespace, published)];
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            logger.LogWarning("Managed gateway configuration could not be read ({ErrorType}).", error.GetType().Name);
            gatewayError = "Lucia could not read its published gateway configuration. The addresses below are the saved configuration, not a confirmed live state.";
            routes = [.. Routes(job, null, null), .. AppRoutes(appRoutes, job.Plan.Naming.Namespace, null)];
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
            routes, [.. Records(job, records, health), .. Records(nodeRecords, records, health),
                .. appRecords.Select(record => Record(record.Domain, record.Answer, "App address, pointed at Lucia's gateway", records, health))]);
    }

    /// <summary>App routes from the gateway's <c>apps.yml</c>, compared with what Lucia would publish for them now.</summary>
    internal static ManagedDomainRoute[] AppRoutes(Stacks.ActiveRoute[] routes, string ns, JsonNode? actual)
    {
        var expected = JsonNode.Parse(Stacks.AppGateway.Build(routes, ns) ?? "{}");
        return routes.OrderBy(item => item.Route.Host, StringComparer.Ordinal).Select(item =>
        {
            var id = $"app-{item.Stack}-{item.Route.Host}";
            string[] ids = item.Route.GrpcPort is null ? [id] : [id, id + "-grpc"];
            var target = Target(actual?["http"]?["services"]?[id]?["loadBalancer"]?["servers"]?[0]?["url"]);
            var state = actual is null ? "Unavailable" : actual["http"]?["routers"]?[id] is null ? "Missing"
                : ids.All(key => JsonNode.DeepEquals(actual["http"]?["routers"]?[key], expected?["http"]?["routers"]?[key])
                    && JsonNode.DeepEquals(actual["http"]?["services"]?[key], expected?["http"]?["services"]?[key])) ? "Published" : "Changed";
            var name = Stacks.StackCatalog.Apps.FirstOrDefault(app => app.Id == item.Stack)?.Name ?? item.Stack;
            return new ManagedDomainRoute(name, $"https://{item.Route.Host}.{ns}", "Proxy", target, state);
        }).ToArray();
    }

    private static string? Target(JsonNode? node)
    {
        var target = node?.GetValue<string>();
        if (target is not null && (!Uri.TryCreate(target, UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0
            || url.Query.Length != 0 || url.Fragment.Length != 0 || target.Length > 2048))
            throw new InvalidDataException("Unsafe gateway destination metadata.");
        return target;
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
            var target = Target(actualTarget);
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
