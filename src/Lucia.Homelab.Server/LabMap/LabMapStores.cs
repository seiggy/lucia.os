using System.Text.Json;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Host;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.LabMap;

internal sealed record ClientOverrideFile(int SchemaVersion, Dictionary<string, string> Clients);

/// <summary>The owner's choice of group for UniFi clients the classifier sorts wrongly, by lowercase MAC.</summary>
public sealed class ClientOverrideStore
{
    public const int MaxEntries = 2000;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClientOverrideStore(IOptions<AdGuardManagementOptions> options) : this(options.Value.CredentialsDirectory) { }

    internal ClientOverrideStore(string directory) => _path = Path.Combine(Path.GetFullPath(directory), "lab-map-clients.json");

    public async Task<IReadOnlyDictionary<string, string>> Read(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return ReadUnlocked(); }
        finally { _gate.Release(); }
    }

    /// <summary>Sets the client's group, or clears it with null. Returns the MAC as stored.</summary>
    public async Task<string> Set(string mac, string? group, CancellationToken ct)
    {
        mac = Validate(mac, group);
        await _gate.WaitAsync(ct);
        try
        {
            var clients = ReadUnlocked();
            if (group is null ? !clients.Remove(mac) : clients.GetValueOrDefault(mac) == group) return mac;
            if (group is not null)
            {
                if (!clients.ContainsKey(mac) && clients.Count >= MaxEntries)
                    throw new UniFiException(409, "too_many_client_overrides", $"Lucia keeps at most {MaxEntries} client groups. Clear some first.");
                clients[mac] = group;
            }
            await DomainOnboardingStore.WriteJson(_path, new ClientOverrideFile(1, new(clients.OrderBy(item => item.Key, StringComparer.Ordinal))), ct);
            return mac;
        }
        finally { _gate.Release(); }
    }

    internal static string Validate(string mac, string? group)
    {
        if (mac is null || !UniFiValidation.Mac(mac))
            throw new UniFiException(400, "invalid_client_mac", "Use the client's MAC address, like aa:bb:cc:dd:ee:ff.");
        if (group is not null && !ClientClassifier.Groups.Contains(group, StringComparer.Ordinal))
            throw new UniFiException(400, "invalid_client_group", $"Use one of {string.Join(", ", ClientClassifier.Groups)}, or null to clear it.");
        return mac.ToLowerInvariant();
    }

    private Dictionary<string, string> ReadUnlocked()
    {
        DomainOnboardingStore.RejectLinks(_path);
        if (!File.Exists(_path)) return new(StringComparer.Ordinal);
        var file = JsonSerializer.Deserialize<ClientOverrideFile>(CertbotFiles.ReadBounded(_path, 512 * 1024), DomainOnboardingStore.Json);
        if (file is not { SchemaVersion: 1, Clients: not null }) throw new InvalidDataException("Lab map client groups are invalid.");
        return file.Clients.Where(item => UniFiValidation.Mac(item.Key) && ClientClassifier.Groups.Contains(item.Value, StringComparer.Ordinal))
            .Take(MaxEntries).ToDictionary(item => item.Key.ToLowerInvariant(), item => item.Value, StringComparer.Ordinal);
    }
}

internal sealed record SiteDistrictFile(int SchemaVersion, Dictionary<string, string> Sites);

/// <summary>
/// The owner's district for internet destinations the categoriser files wrongly, by destination key (<c>github.com</c>, <c>as13335</c>):
/// one of <see cref="TrafficKinds.Categories"/> or a district name of their own.
/// </summary>
public sealed partial class SiteDistrictStore
{
    public const int MaxEntries = 2000, MaxName = 32;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SiteDistrictStore(IOptions<AdGuardManagementOptions> options) : this(options.Value.CredentialsDirectory) { }

    internal SiteDistrictStore(string directory) => _path = Path.Combine(Path.GetFullPath(directory), "lab-map-sites.json");

    public async Task<IReadOnlyDictionary<string, string>> Read(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return ReadUnlocked(); }
        finally { _gate.Release(); }
    }

    /// <summary>Sets the destination's district, or clears it with null. Returns the key and district as stored.</summary>
    public async Task<(string Key, string? District)> Set(string site, string? district, CancellationToken ct)
    {
        var (key, value) = Validate(site, district);
        await _gate.WaitAsync(ct);
        try
        {
            var sites = ReadUnlocked();
            if (value is null ? !sites.Remove(key) : sites.GetValueOrDefault(key) == value) return (key, value);
            if (value is not null)
            {
                if (!sites.ContainsKey(key) && sites.Count >= MaxEntries)
                    throw new UniFiException(409, "too_many_site_districts", $"Lucia keeps at most {MaxEntries} site districts. Clear some first.");
                sites[key] = value;
            }
            await DomainOnboardingStore.WriteJson(_path, new SiteDistrictFile(1, new(sites.OrderBy(item => item.Key, StringComparer.Ordinal))), ct);
            return (key, value);
        }
        finally { _gate.Release(); }
    }

    /// <summary>The destination's key from its id (<c>dest:github.com</c>) and its district: a built-in category's id, or the name tidied.</summary>
    internal static (string Key, string? District) Validate(string site, string? district)
    {
        var key = site?.StartsWith("dest:", StringComparison.Ordinal) == true ? site[5..] : site;
        if (key is null || !Key(key))
            throw new UniFiException(400, "invalid_site", "Use the destination's id from the live map, like dest:github.com.");
        return (key, district is null ? null : District(district)
            ?? throw new UniFiException(400, "invalid_site_district", $"Name the district in 1 to {MaxName} letters, digits, spaces or &'.+-, or use null to clear it."));
    }

    private static bool Key(string key) => key.Length <= 253 && KeyPattern().IsMatch(key);

    internal static string? District(string name) => Named(name, TrafficKinds.Categories);

    /// <summary>A built-in's id when the name is one, else the name with its spaces tidied, or null when it isn't a safe 1–32 character name.</summary>
    internal static string? Named(string name, string[] builtIns)
    {
        var tidy = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (builtIns.FirstOrDefault(item => item.Equals(tidy, StringComparison.OrdinalIgnoreCase)) is { } builtIn) return builtIn;
        return tidy.Length is > 0 and <= MaxName && NamePattern().IsMatch(tidy) ? tidy : null;
    }

    private Dictionary<string, string> ReadUnlocked()
    {
        DomainOnboardingStore.RejectLinks(_path);
        if (!File.Exists(_path)) return new(StringComparer.Ordinal);
        var file = JsonSerializer.Deserialize<SiteDistrictFile>(CertbotFiles.ReadBounded(_path, 512 * 1024), DomainOnboardingStore.Json);
        if (file is not { SchemaVersion: 1, Sites: not null }) throw new InvalidDataException("Lab map site districts are invalid.");
        return file.Sites.Where(item => Key(item.Key) && District(item.Value) == item.Value)
            .Take(MaxEntries).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.:/-]*$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} &'.+-]*$")]
    private static partial Regex NamePattern();
}

internal sealed record CategoryFile(int SchemaVersion, Dictionary<string, string> Objects);

/// <summary>
/// The owner's category for lab map buildings Lucia guesses wrongly, by object id (<c>client:aa:bb:…</c>, <c>app:plex</c>, <c>host:lucialab01</c>):
/// one of <see cref="LabCategories.All"/> or a category name of their own.
/// </summary>
public sealed partial class CategoryStore
{
    public const int MaxEntries = 2000;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CategoryStore(IOptions<AdGuardManagementOptions> options) : this(options.Value.CredentialsDirectory) { }

    internal CategoryStore(string directory) => _path = Path.Combine(Path.GetFullPath(directory), "lab-map-categories.json");

    public async Task<IReadOnlyDictionary<string, string>> Read(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return ReadUnlocked(); }
        finally { _gate.Release(); }
    }

    /// <summary>Sets the object's category, or clears it with null. Returns the id and category as stored.</summary>
    public async Task<(string Id, string? Category)> Set(string id, string? category, CancellationToken ct)
    {
        (id, category) = Validate(id, category);
        await _gate.WaitAsync(ct);
        try
        {
            var objects = ReadUnlocked();
            if (category is null ? !objects.Remove(id) : objects.GetValueOrDefault(id) == category) return (id, category);
            if (category is not null)
            {
                if (!objects.ContainsKey(id) && objects.Count >= MaxEntries)
                    throw new UniFiException(409, "too_many_categories", $"Lucia keeps at most {MaxEntries} categories. Clear some first.");
                objects[id] = category;
            }
            await DomainOnboardingStore.WriteJson(_path, new CategoryFile(1, new(objects.OrderBy(item => item.Key, StringComparer.Ordinal))), ct);
            return (id, category);
        }
        finally { _gate.Release(); }
    }

    internal static (string Id, string? Category) Validate(string id, string? category)
    {
        if (Id(id) is not { } valid)
            throw new UniFiException(400, "invalid_category_object", "Use a client, app, server or NAS id from the map, like client:aa:bb:cc:dd:ee:ff or app:plex.");
        return (valid, category is null ? null : SiteDistrictStore.Named(category, LabCategories.All)
            ?? throw new UniFiException(400, "invalid_category", $"Name the category in 1 to {SiteDistrictStore.MaxName} letters, digits, spaces or &'.+-, or use null to clear it."));
    }

    private static string? Id(string? id) => id switch
    {
        null => null,
        _ when id.StartsWith("client:", StringComparison.Ordinal) => UniFiValidation.Mac(id[7..]) ? "client:" + id[7..].ToLowerInvariant() : null,
        _ => ObjectPattern().IsMatch(id) ? id : null,
    };

    private Dictionary<string, string> ReadUnlocked()
    {
        DomainOnboardingStore.RejectLinks(_path);
        if (!File.Exists(_path)) return new(StringComparer.Ordinal);
        var file = JsonSerializer.Deserialize<CategoryFile>(CertbotFiles.ReadBounded(_path, 512 * 1024), DomainOnboardingStore.Json);
        if (file is not { SchemaVersion: 1, Objects: not null }) throw new InvalidDataException("Lab map categories are invalid.");
        return file.Objects.Where(item => Id(item.Key) == item.Key && SiteDistrictStore.Named(item.Value, LabCategories.All) == item.Value)
            .Take(MaxEntries).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
    }

    [GeneratedRegex(@"^(app|host|nas):[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex ObjectPattern();
}

internal sealed record StoredSnmp(int SchemaVersion, string Username, string AuthProtocol, string ProtectedAuth, string PrivProtocol,
    string ProtectedPriv, DateTimeOffset UpdatedAt)
{
    public override string ToString() => nameof(StoredSnmp);
}

/// <summary>SNMPv3 credentials with their passphrases in plain text, for rendering the collector's environment only.</summary>
internal sealed record SnmpCredentials(string Username, string AuthProtocol, string AuthPassphrase, string PrivProtocol, string PrivPassphrase)
{
    public override string ToString() => nameof(SnmpCredentials);
}

/// <summary>
/// The SNMPv3 user the Observability app's collector reads UniFi devices' interface counters with. Passphrases are kept
/// encrypted with Data Protection and never returned.
/// </summary>
public sealed partial class SnmpSettingsStore
{
    public static readonly string[] AuthProtocols = ["SHA", "SHA256", "SHA512", "MD5"], PrivProtocols = ["AES", "AES192", "AES256", "DES"];
    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Raised after the settings change, so the collector config follows at once.</summary>
    public event Action? Changed;

    public SnmpSettingsStore(IOptions<AdGuardManagementOptions> options, IDataProtectionProvider protection)
        : this(options.Value.CredentialsDirectory, protection) { }

    internal SnmpSettingsStore(string directory, IDataProtectionProvider protection)
    {
        _path = Path.Combine(Path.GetFullPath(directory), "unifi-snmp.json");
        _protector = protection.CreateProtector("Lucia.Homelab.UniFiSnmp.v1");
    }

    /// <summary>The saved settings without their passphrases; Prometheus' last scrape and any message are the caller's to add.</summary>
    public async Task<SnmpStatus> Status(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return ReadUnlocked() is { } stored
                ? new(true, stored.Username, stored.AuthProtocol, stored.PrivProtocol, null, null)
                : new(false, null, null, null, null, null);
        }
        finally { _gate.Release(); }
    }

    internal async Task<SnmpCredentials?> Credentials(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return ReadUnlocked() is { } stored
                ? new(stored.Username, stored.AuthProtocol, _protector.Unprotect(stored.ProtectedAuth), stored.PrivProtocol, _protector.Unprotect(stored.ProtectedPriv))
                : null;
        }
        finally { _gate.Release(); }
    }

    public async Task<SnmpStatus> Save(SnmpSettingsRequest request, CancellationToken ct)
    {
        Validate(request);
        await _gate.WaitAsync(ct);
        try
        {
            await DomainOnboardingStore.WriteJson(_path, new StoredSnmp(1, request.Username, request.AuthProtocol, _protector.Protect(request.AuthPassphrase),
                request.PrivProtocol, _protector.Protect(request.PrivPassphrase), DateTimeOffset.UtcNow), ct);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
        return new(true, request.Username, request.AuthProtocol, request.PrivProtocol, null, null);
    }

    public async Task<SnmpStatus> Delete(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            DomainOnboardingStore.RejectLinks(_path);
            File.Delete(_path);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
        return new(false, null, null, null, null, null);
    }

    internal static void Validate(SnmpSettingsRequest request)
    {
        if (!UserPattern().IsMatch(request.Username ?? ""))
            throw new UniFiException(400, "invalid_snmp_username", "Use the SNMPv3 username from UniFi: up to 32 letters, digits, dots, hyphens and underscores.");
        if (!AuthProtocols.Contains(request.AuthProtocol, StringComparer.Ordinal))
            throw new UniFiException(400, "invalid_snmp_auth", $"Choose an authentication protocol: {string.Join(", ", AuthProtocols)}.");
        if (!PrivProtocols.Contains(request.PrivProtocol, StringComparer.Ordinal))
            throw new UniFiException(400, "invalid_snmp_privacy", $"Choose a privacy protocol: {string.Join(", ", PrivProtocols)}.");
        // The collector reads these from its environment file, where quotes, backslashes and $ would change them.
        if (!PassphrasePattern().IsMatch(request.AuthPassphrase ?? "") || !PassphrasePattern().IsMatch(request.PrivPassphrase ?? ""))
            throw new UniFiException(400, "invalid_snmp_passphrase",
                "Each passphrase needs 8 to 64 characters, without spaces, quotes, backslashes, backticks or $.");
    }

    private StoredSnmp? ReadUnlocked()
    {
        DomainOnboardingStore.RejectLinks(_path);
        if (!File.Exists(_path)) return null;
        var stored = JsonSerializer.Deserialize<StoredSnmp>(CertbotFiles.ReadBounded(_path, 16 * 1024), DomainOnboardingStore.Json);
        return stored is { SchemaVersion: 1 } ? stored : throw new InvalidDataException("SNMP settings are invalid.");
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,32}\z")] private static partial Regex UserPattern();
    [GeneratedRegex("\\A[!#%&()*+,\\-./0-9:;<=>?@A-Z\\[\\]^_a-z{|}~]{8,64}\\z")] private static partial Regex PassphrasePattern();
}
