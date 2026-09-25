namespace Lucia.Desktop.Core;

public sealed class ConnectionOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string Username { get; init; }
    public string? Password { get; init; }
    public string? PrivateKeyPath { get; init; }
    public string? PrivateKeyPassphrase { get; init; }
    public override string ToString() => $"{Username}@{Host}:{Port}";
}

public sealed class SetupOptions
{
    public required string PublicHost { get; init; }
    public required string OwnerUsername { get; init; }
    public string? OwnerPassword { get; init; }
    public bool InstallPrerequisites { get; init; }
    public string? SudoPassword { get; init; }
    public bool VerifyOnly { get; init; }
    public bool ConfigureHost { get; init; } = true;
    public string? ModelDirectory { get; init; }
    public override string ToString() => $"Identity setup for {PublicHost} (credentials omitted)";
}

public sealed record HostKeyInfo(string Host, int Port, string Algorithm, string Fingerprint);

public sealed class HostKeyConfirmationRequiredException(HostKeyInfo key)
    : Exception("Confirm this Spark's SSH fingerprint before sending credentials.")
{
    public HostKeyInfo Key { get; } = key;
}

public sealed record ReadinessCheck(string Name, string Status, string Message);

public sealed record InspectionResult(
    string Hostname,
    string Architecture,
    string OperatingSystem,
    string StateDirectory,
    bool IsInstalled,
    bool OwnerReady,
    string? OwnerUsername,
    string? PublicHost,
    bool CanInstall,
    bool RequiresSudo,
    string? ActiveJobId,
    IReadOnlyList<ReadinessCheck> Checks,
    IReadOnlyList<string> PlannedChanges,
    string? AuthentikUrl,
    bool HostReady = false,
    bool ApplicationReady = false,
    string? HostUrl = null,
    bool HostPackageRequired = true,
    string? ModelDirectory = null,
    bool ActiveJobConfigureHost = false);

public sealed record BootstrapEvent(string Phase, string Message, string Level = "info");

public sealed record InstallationResult(
    string AuthentikUrl,
    string LdapUrl,
    string OwnerUsername,
    string RootCertificatePem,
    string RootFingerprint,
    bool OwnerLoginVerified,
    string? HostUrl = null,
    bool HostReady = false,
    bool ApplicationReady = false);

internal sealed record HostPackageUpload(string ArchivePath, string ManifestPath, string Sha256, long Size);

public sealed record TrustResult(bool Installed, string Message, string? CertificatePath = null);
