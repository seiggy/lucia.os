using System.Text.Json;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Onboarding;
using Microsoft.AspNetCore.DataProtection;

namespace Lucia.Homelab.Server.Stacks;

/// <param name="Secret">An access token or password. Null keeps the saved one.</param>
public sealed record SaveRegistryRequest(string? Username, string? Secret = null);
internal sealed record StoredRegistry(string Host, string Username, string ProtectedSecret, DateTimeOffset UpdatedAt, string UpdatedBy);
internal sealed record RegistryFile(int SchemaVersion, StoredRegistry[] Registries);
/// <summary>A sign-in every managed node gives Docker, so apps can pull private images from <paramref name="Host"/>.</summary>
public sealed record NodeRegistry(string Host, string Username, string Secret);

/// <summary>
/// Sign-ins for private image registries (Docker Hub, GHCR, a self-hosted registry), one per registry. Lucia checks each
/// with its registry, keeps the secret encrypted and gives it to every managed node's Docker.
/// </summary>
public sealed partial class StackStore
{
    public const int MaxRegistries = 16;
    private readonly IDataProtector _registryProtector = protection.CreateProtector("Lucia.Homelab.RegistryCredentials.v1");
    private string RegistryPath => Path.Combine(Path.GetDirectoryName(options.Value.StateDirectory)!, "stacks", "registries.json");

    /// <summary>Signs in to a registry: null when it accepts the username and secret, otherwise why not.</summary>
    internal Func<string, string, string, CancellationToken, Task<string?>> RegistrySignIn { get; init; } = ImageRegistry.SignIn;

    public async Task<object> RegistryList(CancellationToken ct) => new
    {
        registries = (await ReadRegistries(ct)).Select(item => new { item.Host, item.Username, item.UpdatedAt, item.UpdatedBy }).ToArray(),
    };

    public async Task<object> SaveRegistry(string host, SaveRegistryRequest request, string actor, CancellationToken ct)
    {
        var registry = RegistryHost(host);
        var username = (request.Username ?? "").Trim();
        if (!RegistryUserPattern().IsMatch(username))
            throw new HardwareOnboardingException(400, "invalid_registry_user", "Enter the username, without spaces or colons.");
        if (request.Secret is { } typed && (typed.Length is 0 or > 4096 || typed.Any(char.IsControl)))
            throw new HardwareOnboardingException(400, "invalid_registry_secret", "The token must be 1 to 4096 characters on one line.");
        var secret = request.Secret
            ?? (await ReadRegistries(ct)).Where(item => item.Host == registry).Select(item => _registryProtector.Unprotect(item.ProtectedSecret)).FirstOrDefault()
            ?? throw new HardwareOnboardingException(400, "invalid_registry_secret", "Enter the access token or password.");
        if (await RegistrySignIn(registry, username, secret, ct) is { } problem)
            throw new HardwareOnboardingException(400, "registry_sign_in_failed", problem);
        await _gate.WaitAsync(ct);
        try
        {
            var registries = ReadRegistriesUnlocked().Where(item => item.Host != registry).ToList();
            if (registries.Count >= MaxRegistries)
                throw new HardwareOnboardingException(409, "too_many_registries", $"Lucia keeps up to {MaxRegistries} registry sign-ins.");
            registries.Add(new(registry, username, _registryProtector.Protect(secret), time.GetUtcNow(), actor));
            await WriteRegistries(registries, ct);
            return new { host = registry };
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteRegistry(string host, CancellationToken ct)
    {
        var registry = RegistryHost(host);
        await _gate.WaitAsync(ct);
        try
        {
            var registries = ReadRegistriesUnlocked().ToList();
            if (registries.RemoveAll(item => item.Host == registry) == 0)
                throw new HardwareOnboardingException(404, "registry_not_found", "Lucia has no sign-in for that registry.");
            await WriteRegistries(registries, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Every registry sign-in, with its secret, for the nodes' sync.</summary>
    internal async Task<NodeRegistry[]> DesiredRegistries(CancellationToken ct) =>
        [.. (await ReadRegistries(ct)).Select(item => new NodeRegistry(item.Host, item.Username, _registryProtector.Unprotect(item.ProtectedSecret)))];

    /// <summary>Each signed-in registry's API host with its <c>username:secret</c>, for image update checks.</summary>
    internal async Task<Dictionary<string, string>> RegistryLogins(CancellationToken ct) =>
        (await DesiredRegistries(ct)).ToDictionary(item => RegistryApi(item.Host), item => $"{item.Username}:{item.Secret}");

    /// <summary>The registry's host as Lucia keys it: lowercase, with Docker Hub's other names as <c>docker.io</c>.</summary>
    internal static string RegistryHost(string? host)
    {
        var name = (host ?? "").Trim().ToLowerInvariant();
        if (name is "hub.docker.com" or "index.docker.io" or "registry-1.docker.io" or "registry.hub.docker.com") name = "docker.io";
        return RegistryHostPattern().IsMatch(name) && name.IndexOfAny(['.', ':']) > 0 ? name
            : throw new HardwareOnboardingException(400, "invalid_registry", "Enter the registry's host name, such as docker.io or ghcr.io.");
    }

    /// <summary>Where a registry's API answers: Docker Hub's isn't at docker.io.</summary>
    internal static string RegistryApi(string host) => host == "docker.io" ? "registry-1.docker.io" : host;

    private async Task<StoredRegistry[]> ReadRegistries(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return ReadRegistriesUnlocked(); }
        finally { _gate.Release(); }
    }

    private StoredRegistry[] ReadRegistriesUnlocked()
    {
        DomainOnboardingStore.RejectLinks(RegistryPath);
        if (!File.Exists(RegistryPath)) return [];
        var file = JsonSerializer.Deserialize<RegistryFile>(CertbotFiles.ReadBounded(RegistryPath, 1024 * 1024), DomainOnboardingStore.Json);
        if (file is not { SchemaVersion: 1, Registries: not null }) throw new InvalidDataException("Registry sign-ins are invalid.");
        return file.Registries;
    }

    private Task WriteRegistries(IEnumerable<StoredRegistry> registries, CancellationToken ct) =>
        DomainOnboardingStore.WriteJson(RegistryPath, new RegistryFile(1, registries.OrderBy(item => item.Host, StringComparer.Ordinal).ToArray()), ct);

    [GeneratedRegex(@"\A(?=.{1,260}\z)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)*(?::[0-9]{1,5})?\z")]
    private static partial Regex RegistryHostPattern();
    // Printable ASCII without spaces or colons, so robot accounts (org+robot, robot$name) fit.
    [GeneratedRegex(@"\A[!-9;-~]{1,256}\z")]
    private static partial Regex RegistryUserPattern();
}
