using System.Security.Cryptography;
using System.Text.Json;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Nodes;
using Microsoft.AspNetCore.DataProtection;

namespace Lucia.Homelab.Server.Stacks;

/// <summary>A catalog app that signs in through Lucia's Authentik: its name there, its web address name and its OIDC callback paths.</summary>
public sealed record AppSso(string Name, string Host, params string[] Callbacks)
{
    public bool Equals(AppSso? other) => other is not null && (Name, Host) == (other.Name, other.Host) && Callbacks.SequenceEqual(other.Callbacks);
    public override int GetHashCode() => HashCode.Combine(Name, Host, Callbacks.Length);
}

public sealed partial class StackStore
{
    /// <summary>Variables Lucia adds to an app's environment once its Authentik client is registered.</summary>
    internal const string SsoPrefix = "LUCIA_SSO_";
    /// <summary>The app's OIDC client secret, generated with its other secrets when it renders.</summary>
    internal const string SsoSecret = "SSO_CLIENT_SECRET";
    /// <summary>Variables Lucia adds to a telemetry-sending app's environment while an Observability app runs.</summary>
    internal const string OtlpPrefix = "LUCIA_OTLP_";
    /// <summary>The Observability app's extra collector config and SNMP credentials for the lab map's UniFi devices.</summary>
    internal const string LabPrefix = "LUCIA_LAB_";

    /// <summary>Every installed catalog app that signs in through Lucia, with its client secret.</summary>
    internal async Task<(string Stack, AppSso Sso, string Secret)[]> SsoApps(CancellationToken ct) =>
        [.. (await Read(ct)).Select(stack => stack.Manifest.Template is { } template
                && StackCatalog.Apps.FirstOrDefault(app => app.Id == template.Id) is { } app
                && app.Sso(StackCatalog.Settings(app, template.Settings)) is { } sso
                && StackCatalog.ReadEnv(_protector.Unprotect(stack.ProtectedEnv)).GetValueOrDefault(SsoSecret) is { Length: >= 32 } secret
            ? (stack.Name, sso, secret) : default)
            .Where(item => item.Name is not null)];

    internal Task SetSso(string name, string lines, CancellationToken ct) => SetLucia(name, SsoPrefix, lines, ct);

    /// <summary>
    /// Replaces the stack's variables that start with <paramref name="prefix"/>, which Lucia owns. The stack reapplies when
    /// they change; a moving or restoring stack waits.
    /// </summary>
    internal async Task SetLucia(string name, string prefix, string lines, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var stacks = ReadUnlocked().ToList();
            if (stacks.FirstOrDefault(stack => stack.Name == name) is not { Move: null, Restore: null } stack) return;
            var env = _protector.Unprotect(stack.ProtectedEnv);
            if (string.Concat(env.Split('\n').Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).Select(line => line + "\n")) == lines) return;
            var kept = string.Join('\n', env.Split('\n').Where(line => !line.StartsWith(prefix, StringComparison.Ordinal)));
            var wanted = lines.Length == 0 || kept.Length == 0 || kept.EndsWith('\n') ? kept + lines : $"{kept}\n{lines}";
            if (wanted == env) return;
            stacks[stacks.IndexOf(stack)] = stack with
            {
                ProtectedEnv = _protector.Protect(wanted), Revision = stack.Revision + 1, UpdatedAt = time.GetUtcNow(), UpdatedBy = "lucia",
            };
            await Write(stacks, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>The stack's sign-in, telemetry and lab map variables, which Lucia sets and keeps when its catalog app re-renders.</summary>
    internal static string LuciaLines(string env) =>
        string.Concat(env.Split('\n').Where(line => line.StartsWith(SsoPrefix, StringComparison.Ordinal) || line.StartsWith(OtlpPrefix, StringComparison.Ordinal)
                || line.StartsWith(LabPrefix, StringComparison.Ordinal))
            .Select(line => line + "\n"));
}

/// <summary>
/// Registers catalog apps that sign in through Lucia, such as Grafana, as Authentik OIDC clients once the domain is active.
/// The web host holds no Authentik credentials: it writes one request per app for the Spark's scoped identity service,
/// which keeps an owner-only client for each request and removes clients whose request is gone. An app's sign-in turns on
/// only after that service reports the exact current request done.
/// </summary>
public sealed class AppSsoRegistrations(StackStore stacks, DomainOnboardingStore domains, ILogger<AppSsoRegistrations> logger) : BackgroundService
{
    internal const string Requests = "app-sso-requests", Responses = "app-sso-responses", ClientPrefix = "lucia-app-";

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await Sync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                logger.LogWarning("App sign-in registrations could not be updated ({ErrorType}); retrying.", error.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task Sync(CancellationToken ct)
    {
        var ns = (await ManagedNodeDns.ActiveNaming(domains, ct))?.Namespace;
        var authority = DomainActivationConfiguration.Read(domains.Root)?.CanonicalAuthentikOrigin;
        var (requests, responses) = (Path.Combine(domains.Root, Requests), Path.Combine(domains.Root, Responses));
        DomainOnboardingStore.EnsureDirectory(requests);
        var apps = ns is null || authority is null ? [] : await stacks.SsoApps(ct);
        var zone = await ManagedNodeDns.PublicZone(domains, ct);
        var routes = zone is null ? [] : await stacks.ActiveRoutes(ct);
        foreach (var (stack, sso, secret) in apps)
        {
            var url = $"https://{sso.Host}.{ns}";
            var publicUrl = routes.FirstOrDefault(item => item.Stack == stack && item.Route.Host == sso.Host)?.Route.Public is { } label
                ? $"https://{label}.{zone}" : null;
            string[] origins = publicUrl is null ? [url] : [url, publicUrl];
            // Public apps launch at their public name (AdGuard answers it on the LAN, Cloudflare elsewhere); the internal name stays allowed.
            var request = new Dictionary<string, object>
            {
                ["schemaVersion"] = 1, ["stack"] = stack, ["name"] = sso.Name, ["clientId"] = ClientPrefix + stack, ["clientSecret"] = secret,
                ["redirectUris"] = origins.SelectMany(origin => sso.Callbacks.Select(callback => origin + callback)).ToArray(),
                ["launchUrl"] = (publicUrl ?? url) + "/",
            };
            if (publicUrl is not null) request["aliasUrl"] = url + "/";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request, DomainOnboardingStore.Json);
            var path = Path.Combine(requests, stack + ".json");
            DomainOnboardingStore.RejectLinks(path);
            if (!File.Exists(path) || !(await File.ReadAllBytesAsync(path, ct)).AsSpan().SequenceEqual(bytes))
                await DomainOnboardingStore.WriteJson(path, JsonDocument.Parse(bytes).RootElement, ct);
            var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, ct)));
            var done = Done(Path.Combine(responses, stack + ".json"), stack, hash);
            await stacks.SetSso(stack, done
                ? $"{StackStore.SsoPrefix}ENABLED=true\n{StackStore.SsoPrefix}CLIENT_ID={ClientPrefix + stack}\n{StackStore.SsoPrefix}ORIGIN={authority}\n"
                    + $"{StackStore.SsoPrefix}SIGNOUT={authority}/application/o/{ClientPrefix + stack}/end-session/\n"
                : "", ct);
        }
        var wanted = apps.Select(app => app.Stack + ".json").ToHashSet(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(requests, "*.json").Where(path => !wanted.Contains(Path.GetFileName(path))))
        {
            File.Delete(path);
            logger.LogInformation("Requested removal of {Stack}'s sign-in client.", Path.GetFileNameWithoutExtension(path));
        }
        // Apps whose domain or client went away sign in with their own accounts again.
        foreach (var (stack, _, _) in ns is null || authority is null ? await stacks.SsoApps(ct) : [])
            await stacks.SetSso(stack, "", ct);
    }

    private static bool Done(string path, string stack, string hash)
    {
        DomainOnboardingStore.RejectLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 4096) return false;
        try
        {
            using var response = JsonDocument.Parse(File.ReadAllBytes(path));
            var value = response.RootElement;
            return value.GetProperty("schemaVersion").GetInt32() == 1 && value.GetProperty("stack").GetString() == stack
                && value.GetProperty("requestHash").GetString() == hash && value.GetProperty("success").GetBoolean();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }
}
