using System.Text.Json;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;

namespace Lucia.Homelab.Server.Stacks;

/// <summary>
/// An app's web address, <c>host.namespace</c> under the active domain. Lucia's gateway on the controller answers for it
/// with the domain's certificate and forwards to the app's server, and local DNS points the name at the gateway.
/// </summary>
/// <param name="Host">One DNS label, such as <c>grafana</c>.</param>
/// <param name="Port">The server port that answers HTTP.</param>
/// <param name="GrpcPort">A server port that answers gRPC in cleartext HTTP/2, for gRPC requests to the same name.</param>
public sealed record StackRoute(string Host, int Port, int? GrpcPort = null);
/// <summary>A route and the current address of the server that runs its app.</summary>
public sealed record ActiveRoute(string Stack, StackRoute Route, string Address);

public sealed partial class StackStore
{
    private const int MaxRoutes = 8;
    private string _routeKey = "";

    /// <summary>Raised when any app's routes, or the server they run on, change.</summary>
    public event Action? RoutesChanged;

    /// <summary>Every app route with the current address of the server it runs on.</summary>
    public async Task<ActiveRoute[]> ActiveRoutes(CancellationToken ct)
    {
        var addresses = (await nodes.Addresses(ct)).GroupBy(item => item.Hostname).ToDictionary(group => group.Key, group => group.First().Address);
        return [.. (await Read(ct)).SelectMany(stack => (stack.Manifest.Address ?? addresses.GetValueOrDefault(stack.Assigned)) is { } address
            ? (stack.Manifest.Routes ?? []).Select(route => new ActiveRoute(stack.Name, route, address)) : [])];
    }

    /// <summary>The routes, checked: at most eight, each a free DNS label on valid ports.</summary>
    private async Task<StackRoute[]?> ValidRoutes(StackRoute[]? routes, string name, IEnumerable<StoredStack> stacks, CancellationToken ct)
    {
        if (routes is null || routes.Length == 0) return null;
        if (routes.Length > MaxRoutes) throw new HardwareOnboardingException(400, "too_many_routes", $"An app has up to {MaxRoutes} web addresses.");
        var naming = await ManagedNodeDns.ActiveNaming(domains, ct);
        var taken = (await nodes.Names(ct)).Select(item => item.Hostname.ToLowerInvariant())
            .Concat((naming?.LocalHostnames ?? []).Select(fqdn => fqdn.Split('.')[0].ToLowerInvariant())).ToHashSet(StringComparer.Ordinal);
        var result = new List<StackRoute>();
        foreach (var route in routes)
        {
            var host = (route.Host ?? "").Trim().ToLowerInvariant();
            if (!RouteHostPattern().IsMatch(host))
                throw new HardwareOnboardingException(400, "invalid_route", "A web address name uses lowercase letters, digits and hyphens, such as grafana.");
            if (route.Port is < 1 or > 65535 || route.GrpcPort is < 1 or > 65535)
                throw new HardwareOnboardingException(400, "invalid_route", "A web address needs ports from 1 to 65535.");
            if (result.Any(item => item.Host == host))
                throw new HardwareOnboardingException(400, "invalid_route", $"The app lists {host} twice.");
            if (stacks.FirstOrDefault(stack => stack.Name != name && (stack.Manifest.Routes ?? []).Any(item => item.Host == host)) is { } other)
                throw new HardwareOnboardingException(409, "route_in_use", $"{other.Name} already uses {host}.");
            if (taken.Contains(host))
                throw new HardwareOnboardingException(409, "route_in_use", $"{host} is already one of Lucia's own names.");
            result.Add(route with { Host = host });
        }
        return [.. result];
    }


    /// <summary>The route variables a node's environment gets, <c>LUCIA_URL_GRAFANA=https://grafana.homelab.example.com</c>.</summary>
    internal static string RouteEnv(StackManifest manifest, string? ns) => ns is null ? ""
        : string.Concat((manifest.Routes ?? []).Select(route => $"{RouteVariable(route.Host)}=https://{route.Host}.{ns}\n"));

    internal static string RouteVariable(string host) => "LUCIA_URL_" + host.ToUpperInvariant().Replace('-', '_');

    private void NoticeRoutes(IEnumerable<StoredStack> stacks)
    {
        var key = string.Join(";", stacks.Where(stack => stack.Manifest.Routes is { Length: > 0 })
            .Select(stack => stack.Name + "@" + stack.Assigned + ":" + JsonSerializer.Serialize(stack.Manifest.Routes)));
        if (Interlocked.Exchange(ref _routeKey, key) != key) RoutesChanged?.Invoke();
    }

    [GeneratedRegex(@"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z")]
    internal static partial Regex RouteHostPattern();
}

/// <summary>
/// The gateway's app routes, in <c>apps.yml</c> next to <c>domain.yml</c>. The domain's wildcard certificate covers them.
/// gRPC requests (by content type) go to the route's gRPC port in cleartext HTTP/2; everything else to its HTTP port.
/// </summary>
public static class AppGateway
{
    public const string FileName = "apps.yml";

    public static string? Build(IEnumerable<ActiveRoute> routes, string ns)
    {
        var routers = new Dictionary<string, object>();
        var services = new Dictionary<string, object>();
        foreach (var (stack, route, address) in routes.OrderBy(item => item.Route.Host, StringComparer.Ordinal))
        {
            var id = $"app-{stack}-{route.Host}";
            var rule = $"Host(`{route.Host}.{ns}`)";
            routers[id] = new { entryPoints = new[] { "host" }, rule, service = id, tls = new { } };
            services[id] = Service($"http://{address}:{route.Port}");
            if (route.GrpcPort is { } grpc)
            {
                routers[id + "-grpc"] = new
                {
                    entryPoints = new[] { "host" }, rule = rule + " && HeaderRegexp(`Content-Type`, `^application/grpc`)", service = id + "-grpc", tls = new { },
                };
                services[id + "-grpc"] = Service($"h2c://{address}:{grpc}");
            }
        }
        return routers.Count == 0 ? null : JsonSerializer.Serialize(new { http = new { routers, services } });
    }

    private static object Service(string url) => new { loadBalancer = new { servers = new[] { new { url } } } };

    /// <summary>Writes the file atomically, or removes it when no app has a route.</summary>
    public static void Publish(string gatewayDirectory, string? json)
    {
        if (!Path.IsPathFullyQualified(gatewayDirectory))
            throw new ArgumentException("The mounted domain gateway directory must be absolute.");
        DomainActivationConfiguration.RejectLinks(gatewayDirectory);
        var target = Path.Combine(gatewayDirectory, FileName);
        DomainActivationConfiguration.RejectLinks(target);
        if (json is null)
        {
            if (!File.Exists(target)) return;
            File.Delete(target);
            DomainIngressConfiguration.NotifyGateway(gatewayDirectory);
            return;
        }
        if (File.Exists(target) && File.ReadAllText(target) == json) return;
        var pending = Path.Combine(gatewayDirectory, $".apps-{Guid.NewGuid():N}.pending");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(pending, options))
            {
                stream.Write(System.Text.Encoding.UTF8.GetBytes(json));
                stream.Flush(flushToDisk: true);
            }
            File.Move(pending, target, overwrite: true);
            DomainIngressConfiguration.NotifyGateway(gatewayDirectory);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
}
