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
/// <param name="Public">A DNS label for a public name, <c>label.zone</c>, served to the internet through Cloudflare while public access is on.</param>
public sealed record StackRoute(string Host, int Port, int? GrpcPort = null, string? Public = null);
/// <summary>A route and the current address of the server that runs its app.</summary>
public sealed record ActiveRoute(string Stack, StackRoute Route, string Address);
/// <summary>A web address for a service Lucia doesn't run, such as Home Assistant: the gateway forwards to it over HTTP.</summary>
/// <param name="Address">Its private IPv4 address.</param>
public sealed record ExternalRoute(string Host, string Address, int Port, string? Public = null);
internal sealed record ExternalRouteFile(int SchemaVersion, ExternalRoute[] Routes);
public sealed record SaveExternalRoutesRequest(ExternalRoute[] Routes);
/// <param name="Public">A DNS label under the zone, or empty to keep the address internal.</param>
public sealed record StackPublicRequest(string Host, string? Public);

public sealed partial class StackStore
{
    private const int MaxRoutes = 8, MaxExternalRoutes = 32;
    /// <summary>The owner of external routes in gateway router names; never a valid stack name.</summary>
    public const string ExternalOwner = "_external";
    private string _routeKey = "";
    private string ExternalPath => Path.Combine(Path.GetDirectoryName(options.Value.StateDirectory)!, "stacks", "external.json");

    /// <summary>Raised when any app's routes, or the server they run on, change.</summary>
    public event Action? RoutesChanged;

    /// <summary>Every app route with the current address of the server it runs on, then the external routes.</summary>
    public async Task<ActiveRoute[]> ActiveRoutes(CancellationToken ct)
    {
        var addresses = (await nodes.Addresses(ct)).GroupBy(item => item.Hostname).ToDictionary(group => group.Key, group => group.First().Address);
        return [.. (await Read(ct)).SelectMany(stack => (stack.Manifest.Address ?? addresses.GetValueOrDefault(stack.Assigned)) is { } address
            ? (stack.Manifest.Routes ?? []).Select(route => new ActiveRoute(stack.Name, route, address)) : []),
            .. (await ExternalRoutes(ct)).Select(route => new ActiveRoute(ExternalOwner, new(route.Host, route.Port, null, route.Public), route.Address))];
    }

    public async Task<ExternalRoute[]> ExternalRoutes(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return ReadExternalUnlocked(); }
        finally { _gate.Release(); }
    }

    /// <summary>Replaces the external routes. Their names share the checks, and the uniqueness, of app routes.</summary>
    public async Task<ExternalRoute[]> SaveExternalRoutes(ExternalRoute[] routes, CancellationToken ct)
    {
        if ((routes ?? []).Length > MaxExternalRoutes)
            throw new HardwareOnboardingException(400, "too_many_routes", $"Lucia forwards up to {MaxExternalRoutes} outside services.");
        foreach (var route in routes ?? [])
            if (!System.Net.IPAddress.TryParse(route.Address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                || !Host.AdGuardTransport.IsPrivate(ip) || ip.ToString() != route.Address)
                throw new HardwareOnboardingException(400, "invalid_route", $"{route.Host} needs a private IPv4 address, such as 192.168.1.20.");
        await _gate.WaitAsync(ct);
        try
        {
            var stacks = ReadUnlocked();
            var valid = await Valid([.. (routes ?? []).Select(route => new StackRoute(route.Host, route.Port, null, route.Public))],
                stacks.SelectMany(stack => (stack.Manifest.Routes ?? []).Select(route => (stack.Name, route))), ct) ?? [];
            ExternalRoute[] saved = [.. valid.Select((route, index) => new ExternalRoute(route.Host, routes![index].Address, route.Port, route.Public))
                .OrderBy(route => route.Host, StringComparer.Ordinal)];
            await DomainOnboardingStore.WriteJson(ExternalPath, new ExternalRouteFile(1, saved), ct);
            RoutesChanged?.Invoke();
            return saved;
        }
        finally { _gate.Release(); }
    }

    private ExternalRoute[] ReadExternalUnlocked()
    {
        DomainOnboardingStore.RejectLinks(ExternalPath);
        if (!File.Exists(ExternalPath)) return [];
        var file = JsonSerializer.Deserialize<ExternalRouteFile>(CertbotFiles.ReadBounded(ExternalPath, 256 * 1024), DomainOnboardingStore.Json);
        if (file is not { SchemaVersion: 1, Routes: not null }) throw new InvalidDataException("External route state is invalid.");
        return file.Routes;
    }

    /// <summary>The routes, checked: at most eight, each a free DNS label on valid ports.</summary>
    private Task<StackRoute[]?> ValidRoutes(StackRoute[]? routes, string name, IEnumerable<StoredStack> stacks, CancellationToken ct)
    {
        if (routes is { Length: > MaxRoutes }) throw new HardwareOnboardingException(400, "too_many_routes", $"An app has up to {MaxRoutes} web addresses.");
        return Valid(routes, stacks.Where(stack => stack.Name != name).SelectMany(stack => (stack.Manifest.Routes ?? []).Select(route => (stack.Name, route)))
            .Concat(ReadExternalUnlocked().Select(route => ("an outside service", new StackRoute(route.Host, route.Port, null, route.Public)))), ct);
    }

    private async Task<StackRoute[]?> Valid(StackRoute[]? routes, IEnumerable<(string Owner, StackRoute Route)> others, CancellationToken ct)
    {
        if (routes is null || routes.Length == 0) return null;
        var other = others.ToArray();
        var naming = await ManagedNodeDns.ActiveNaming(domains, ct);
        var taken = (await nodes.Names(ct)).Select(item => item.Hostname.ToLowerInvariant())
            .Concat((naming?.LocalHostnames ?? []).Select(fqdn => fqdn.Split('.')[0].ToLowerInvariant())).ToHashSet(StringComparer.Ordinal);
        // Public names sit directly under the zone, next to Authentik's public name and the namespace itself.
        var reserved = naming is null ? [] : naming.LocalHostnames.Append(naming.Namespace).Append("auth." + naming.Domain)
            .Where(fqdn => fqdn.EndsWith("." + naming.Domain, StringComparison.Ordinal))
            .Select(fqdn => fqdn[..^(naming.Domain.Length + 1)].Split('.')[^1]).ToHashSet(StringComparer.Ordinal);
        var result = new List<StackRoute>();
        foreach (var route in routes)
        {
            var host = (route.Host ?? "").Trim().ToLowerInvariant();
            if (!RouteHostPattern().IsMatch(host))
                throw new HardwareOnboardingException(400, "invalid_route", "A web address name uses lowercase letters, digits and hyphens, such as grafana.");
            if (route.Port is < 1 or > 65535 || route.GrpcPort is < 1 or > 65535)
                throw new HardwareOnboardingException(400, "invalid_route", "A web address needs ports from 1 to 65535.");
            if (result.Any(item => item.Host == host))
                throw new HardwareOnboardingException(400, "invalid_route", $"{host} is listed twice.");
            if (other.FirstOrDefault(item => item.Route.Host == host) is { Owner: { } owner })
                throw new HardwareOnboardingException(409, "route_in_use", $"{owner} already uses {host}.");
            if (taken.Contains(host))
                throw new HardwareOnboardingException(409, "route_in_use", $"{host} is already one of Lucia's own names.");
            var name = string.IsNullOrWhiteSpace(route.Public) ? null : route.Public.Trim().ToLowerInvariant();
            if (name is not null)
            {
                if (!RouteHostPattern().IsMatch(name))
                    throw new HardwareOnboardingException(400, "invalid_route", "A public name uses lowercase letters, digits and hyphens, such as photos.");
                if (reserved.Contains(name))
                    throw new HardwareOnboardingException(409, "route_in_use", $"{name} is already one of Lucia's own public names.");
                if (result.Any(item => item.Public == name))
                    throw new HardwareOnboardingException(400, "invalid_route", $"The public name {name} is listed twice.");
                if (other.FirstOrDefault(item => item.Route.Public == name) is { Owner: { } user })
                    throw new HardwareOnboardingException(409, "route_in_use", $"{user} already uses the public name {name}.");
            }
            result.Add(route with { Host = host, Public = name });
        }
        return [.. result];
    }


    /// <summary>Gives one of an app's web addresses a public name, changes it or, when empty, removes it.</summary>
    public async Task<object> SaveStackPublic(string name, StackPublicRequest request, string actor, CancellationToken ct)
    {
        var names = await NodeNames(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var stacks = ReadUnlocked().ToList();
            var stack = Find(stacks, name);
            RequireSettled(stack);
            var routes = stack.Manifest.Routes ?? [];
            if (!routes.Any(route => route.Host == request.Host))
                throw new HardwareOnboardingException(404, "unknown_route", $"{name} has no web address called {request.Host}.");
            var changed = await ValidRoutes([.. routes.Select(route => route.Host == request.Host ? route with { Public = request.Public } : route)], name, stacks, ct);
            if (changed!.SequenceEqual(routes)) return Summary(stack, names);
            var saved = stack with
            {
                Manifest = stack.Manifest with { Routes = changed }, Revision = stack.Revision + 1, UpdatedAt = time.GetUtcNow(), UpdatedBy = actor,
            };
            stacks[stacks.IndexOf(stack)] = saved;
            await Write(stacks, ct);
            return Summary(saved, names);
        }
        finally { _gate.Release(); }
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

    /// <param name="zone">The domain's zone while public access is on: routes with a public name also answer at <c>name.zone</c> on the public entrypoint.</param>
    public static string? Build(IEnumerable<ActiveRoute> routes, string ns, string? zone = null)
    {
        var routers = new Dictionary<string, object>();
        var services = new Dictionary<string, object>();
        foreach (var (stack, route, address) in routes.OrderBy(item => item.Route.Host, StringComparer.Ordinal))
        {
            var id = $"app-{stack}-{route.Host}";
            var rule = $"Host(`{route.Host}.{ns}`)";
            routers[id] = new { entryPoints = new[] { "host" }, rule, service = id, tls = new { } };
            services[id] = Service($"http://{address}:{route.Port}");
            if (zone is not null && route.Public is { } name)
                routers[id + "-public"] = new { entryPoints = new[] { "host", DomainIngressConfiguration.PublicEntryPoint },
                    rule = $"Host(`{name}.{zone}`)", service = id, tls = new { } };
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
