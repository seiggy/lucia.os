using System.Net;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Onboarding;

namespace Lucia.Homelab.Server.Security;

/// <param name="GatewayDirectory">The gateway's security configuration, mounted from the identity state. Empty when Lucia doesn't run the gateway.</param>
/// <param name="StateDirectory">Where the owner's list is kept, in the host's data.</param>
public sealed class GatewaySecurityOptions
{
    public string GatewayDirectory { get; set; } = "";
    public string StateDirectory { get; set; } = "";
}

public sealed record SafeHarborRequest(string[]? Addresses);
/// <param name="Enabled">Whether Lucia configures the gateway's protection here; the list is kept either way.</param>
/// <param name="Lucia">Addresses Lucia always trusts: loopback, its own Docker networks and its LAN address.</param>
public sealed record SafeHarborView(bool Enabled, string[] Addresses, string[] Lucia, DateTimeOffset? UpdatedAt, string? UpdatedBy);
internal sealed record SafeHarborState(int SchemaVersion, string[] Addresses, DateTimeOffset UpdatedAt, string UpdatedBy);

/// <summary>
/// The owner's safe harbor: devices CrowdSec never bans and the gateway's bouncer never checks, such as the owner's desktop.
/// The list lives in the host's data. <see cref="CrowdSec"/> carries it to the gateway's middlewares at once and to the
/// engine's allowlist within a minute.
/// </summary>
public sealed class SafeHarbor(GatewaySecurityOptions options, HostAuthenticationOptions authentication, DomainOnboardingOptions domains,
    ILogger<SafeHarbor> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Raised after the owner saves the list.</summary>
    public event Action? Changed;

    internal bool Enabled => Path.IsPathFullyQualified(options.GatewayDirectory);
    private string StatePath => Path.Combine(options.StateDirectory, GatewaySecurity.StateFileName);

    internal string[] Lucia => [.. GatewaySecurity.Harbor(authentication.TrustedProxyNetworks
        .Concat(IPAddress.TryParse(domains.IngressAddress, out _) ? [domains.IngressAddress!] : []), [])];

    public async Task<SafeHarborView> Read(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return View(ReadState()); }
        finally { _gate.Release(); }
    }

    /// <summary>The whole harbor: Lucia's own addresses, then the owner's. A saved list that can't be read counts as empty.</summary>
    public async Task<string[]> Entries(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            string[] owner;
            try { owner = ReadState()?.Addresses ?? []; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
                or System.Text.Json.JsonException or HardwareOnboardingException)
            {
                logger.LogWarning("The saved safe-harbor list could not be read ({ErrorType}); only Lucia's own addresses are trusted.", error.GetType().Name);
                owner = [];
            }
            return GatewaySecurity.Harbor(Lucia, owner);
        }
        finally { _gate.Release(); }
    }

    public async Task<SafeHarborView> Save(SafeHarborRequest request, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var addresses = Validate(request.Addresses);
        SafeHarborState state;
        await _gate.WaitAsync(ct);
        try
        {
            state = new SafeHarborState(1, addresses, now, actor);
            await DomainOnboardingStore.WriteJson(StatePath, state, ct);
            logger.LogInformation("The gateway's safe harbor now has {Count} owner entries.", addresses.Length);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
        return View(state);
    }

    /// <summary>The owner's entries in canonical form, or a 400 naming the first that isn't acceptable.</summary>
    internal static string[] Validate(string[]? entries)
    {
        if (entries is null) throw new HardwareOnboardingException(400, "invalid_body", "Send the list as {\"addresses\": [...]}.");
        if (entries.Length > GatewaySecurity.MaxOwnerEntries)
            throw new HardwareOnboardingException(400, "too_many_addresses", $"Trust up to {GatewaySecurity.MaxOwnerEntries} addresses or networks.");
        var result = new List<string>();
        foreach (var entry in entries)
        {
            var canonical = GatewaySecurity.Canonical(entry, DomainIngressConfiguration.CloudflareRanges, out var error)
                ?? throw new HardwareOnboardingException(400, "invalid_address", error!);
            if (!result.Contains(canonical, StringComparer.Ordinal)) result.Add(canonical);
        }
        return [.. result];
    }

    private SafeHarborState? ReadState()
    {
        if (!Path.IsPathFullyQualified(options.StateDirectory)) return null;
        DomainOnboardingStore.RejectLinks(StatePath);
        if (!File.Exists(StatePath)) return null;
        var addresses = GatewaySecurity.ReadOwner(StatePath);
        var state = System.Text.Json.JsonSerializer.Deserialize<SafeHarborState>(File.ReadAllBytes(StatePath), DomainOnboardingStore.Json)
            ?? throw new InvalidDataException("The safe-harbor list is invalid.");
        return state with { Addresses = Validate(addresses) };
    }

    private SafeHarborView View(SafeHarborState? state) =>
        new(Enabled, state?.Addresses ?? [], Lucia, state?.UpdatedAt, state?.UpdatedBy);
}
