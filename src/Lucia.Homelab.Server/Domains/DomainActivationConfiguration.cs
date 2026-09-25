using System.Text.Json;
using System.Text.Json.Serialization;
using Lucia.Homelab.Server.Host;

namespace Lucia.Homelab.Server.Domains;

/// <summary>
/// Owner-reviewed runtime settings, not a DNS/certificate/SSO verification receipt.
/// Changing origins requires a restart and fresh browser SSO. JWTs with the old
/// issuer are intentionally rejected; legacy-origin API keys remain supported.
/// Private LDAP trust, OIDC credentials and Data Protection keys are not migrated.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DomainRuntimeProfile(
    [property: JsonRequired, JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonRequired, JsonPropertyName("verifiedZone")] string VerifiedZone,
    [property: JsonRequired, JsonPropertyName("canonicalLuciaOrigin")] string CanonicalLuciaOrigin,
    [property: JsonRequired, JsonPropertyName("canonicalAuthentikOrigin")] string CanonicalAuthentikOrigin,
    [property: JsonRequired, JsonPropertyName("sparkOrigin")] string SparkOrigin,
    [property: JsonRequired, JsonPropertyName("legacyLuciaOrigin")] string LegacyLuciaOrigin,
    [property: JsonRequired, JsonPropertyName("legacyAuthority")] string LegacyAuthority,
    [property: JsonRequired, JsonPropertyName("profileId")] Guid ProfileId,
    [property: JsonRequired, JsonPropertyName("activatedAt")] DateTimeOffset ActivatedAt)
{
    public void Validate(string originalLuciaOrigin, string originalAuthority)
    {
        if (SchemaVersion != 1 || ProfileId == Guid.Empty || ActivatedAt == default
            || ActivatedAt > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new ArgumentException("The active domain profile has an invalid version, identity, or activation time.");
        var zone = DomainNames.Hostname(VerifiedZone);
        if (zone != VerifiedZone || !zone.Contains('.'))
            throw new ArgumentException("The verified zone must be a normalized DNS zone.");
        var origins = new[] { CanonicalLuciaOrigin, CanonicalAuthentikOrigin, SparkOrigin };
        var hosts = origins.Select(value => DomainNames.ServiceHost(value, zone)).ToArray();
        if (hosts.Distinct(StringComparer.Ordinal).Count() != 3
            || origins.Where((value, index) => value != "https://" + hosts[index]).Any())
            throw new ArgumentException("The profile must contain three distinct normalized HTTPS origins without explicit ports.");
        if (!HostAuthenticationOptions.IsHttpsOrigin(LegacyLuciaOrigin, out var legacyLucia)
            || !Uri.TryCreate(LegacyAuthority, UriKind.Absolute, out var authority)
            || authority.AbsolutePath != "/application/o/lucia/"
            || !HostAuthenticationOptions.IsHttpsOrigin(authority.GetLeftPart(UriPartial.Authority), out _)
            || LegacyAuthority != authority.GetLeftPart(UriPartial.Authority) + "/application/o/lucia/"
            || LegacyLuciaOrigin != originalLuciaOrigin || LegacyAuthority != originalAuthority)
            throw new ArgumentException("The profile's recovery origins do not match the original HostAuthentication settings.");
        if (hosts.Any(host => string.Equals(host, legacyLucia.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, authority.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Use distinct public hostnames to preserve private recovery addresses.");
    }
}

public static class DomainActivationConfiguration
{
    public const int MaximumProfileBytes = 16384;
    public static string DefaultStateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lucia", "data", "domains");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        MaxDepth = 4,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>Call once before AddHostAuthentication. Missing active.json leaves all legacy settings unchanged.</summary>
    public static DomainRuntimeProfile? Apply(WebApplicationBuilder builder)
    {
        var directory = builder.Configuration["DomainOnboarding:StateDirectory"] ?? DefaultStateDirectory;
        var profile = Read(directory);
        if (profile is null) return null;
        var section = builder.Configuration.GetSection("HostAuthentication");
        profile.Validate(section["PublicOrigin"] ?? "", section["Authority"] ?? "");
        // Configuration arrays merge by index. Refuse an unreviewed existing alias set
        // rather than letting a lower-priority source append aliases to this profile.
        if (section.GetSection("AdditionalPublicOrigins").GetChildren().Any())
            throw new ArgumentException("Active domain profiles require the original HostAuthentication alias list to be empty.");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HostAuthentication:PublicOrigin"] = profile.CanonicalLuciaOrigin,
            ["HostAuthentication:Authority"] = profile.CanonicalAuthentikOrigin + "/application/o/lucia/",
            ["HostAuthentication:AuthorityUsesSystemTrust"] = "true",
            ["HostAuthentication:AdditionalPublicOrigins:0"] = profile.LegacyLuciaOrigin
        });
        return profile;
    }

    public static DomainRuntimeProfile? Read(string stateDirectory)
    {
        if (string.IsNullOrWhiteSpace(stateDirectory) || !Path.IsPathFullyQualified(stateDirectory))
            throw new ArgumentException("DomainOnboarding:StateDirectory must be an absolute persistent directory.");
        var path = Path.Combine(stateDirectory, "active.json");
        RejectLinks(path);
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("The active domain profile must be a regular file.");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        if (!OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException("The active domain profile must be private (0600).");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumProfileBytes)
            throw new IOException("The active domain profile is empty or exceeds the size limit.");
        var bytes = new byte[MaximumProfileBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count > MaximumProfileBytes) throw new IOException("The active domain profile exceeds the size limit.");
        var profile = JsonSerializer.Deserialize<DomainRuntimeProfile>(bytes.AsSpan(0, count), JsonOptions)
            ?? throw new JsonException("The active domain profile must be an object.");
        profile.Validate(profile.LegacyLuciaOrigin, profile.LegacyAuthority);
        return profile;
    }

    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            var file = new FileInfo(current);
            var directory = new DirectoryInfo(current);
            if (file.LinkTarget is not null || directory.LinkTarget is not null
                || (file.Exists && file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                || (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                throw new IOException("Domain activation paths must not contain symbolic links or reparse points.");
        }
    }
}
