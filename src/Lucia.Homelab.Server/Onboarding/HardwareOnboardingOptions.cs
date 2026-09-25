using System.Net;
using System.Text.Json;

namespace Lucia.Homelab.Server.Onboarding;

public sealed class HardwareOnboardingOptions
{
    public string StateDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lucia", "onboarding");
    public string? DiscoveryNetworkCidr { get; set; }
    public string? BootBaseUrl { get; set; }
    public bool DiscoveryAdapterQualified { get; set; }
    public bool BootArtifactsQualified { get; set; }
    public bool EnrollmentQualified { get; set; }
    public bool InstallationEnabled { get; set; }
    public string? EnrollmentStatusFile { get; set; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(StateDirectory) || !Path.IsPathFullyQualified(StateDirectory))
            throw new ArgumentException("HardwareOnboarding:StateDirectory must be an absolute persistent directory.");
        if (EnrollmentStatusFile is not null && !Path.IsPathFullyQualified(EnrollmentStatusFile))
            throw new ArgumentException("HardwareOnboarding:EnrollmentStatusFile must be an absolute private path.");
        if (DiscoveryNetworkCidr is not null && !IsPrivateNetwork(DiscoveryNetworkCidr))
            throw new ArgumentException("HardwareOnboarding:DiscoveryNetworkCidr must be an explicit private IPv4 subnet (/8 or narrower).");
        if (BootBaseUrl is not null && (!Uri.TryCreate(BootBaseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/" || BootBaseUrl.Length > 256 || BootBaseUrl.Any(char.IsControl)))
            throw new ArgumentException("HardwareOnboarding:BootBaseUrl must be an HTTPS origin without credentials, query, fragment, or path.");
    }

    internal HardwareOnboardingReadiness Readiness()
    {
        List<string> reasons = [];
        if (DiscoveryNetworkCidr is null) reasons.Add("A private discovery network CIDR is not configured.");
        if (BootBaseUrl is null) reasons.Add("A private-CA HTTPS boot origin is not configured.");
        if (!DiscoveryAdapterQualified) reasons.Add("The read-only, session-authenticated discovery adapter has not been qualified.");
        var canDiscover = reasons.Count == 0;
        if (!BootArtifactsQualified) reasons.Add("Debian 13.7 x86-64 UEFI boot and installer artifacts have not been qualified.");
        if (!EnrollmentQualified) reasons.Add("Private CA and directory-backed node enrollment have not been qualified.");
        if (!InstallationEnabled) reasons.Add("Installation is disabled pending explicit coordinator qualification.");
        if (EnrollmentStatusFile is not null && !EnrollmentServiceReady())
            reasons.Add("The native CA and directory enrollment service is not ready.");
        return new(canDiscover, reasons.Count == 0, reasons.ToArray());
    }

    private bool EnrollmentServiceReady()
    {
        try
        {
            var file = new FileInfo(EnrollmentStatusFile!);
            if (!file.Exists || file.LinkTarget is not null || file.Length > 4096) return false;
            using var document = JsonDocument.Parse(File.ReadAllBytes(file.FullName));
            var value = document.RootElement;
            var checkedAt = value.GetProperty("checkedAt").GetDateTimeOffset();
            return value.GetProperty("schemaVersion").GetInt32() == 1 && value.GetProperty("ready").GetBoolean()
                && checkedAt <= DateTimeOffset.UtcNow.AddSeconds(5) && checkedAt > DateTimeOffset.UtcNow.AddSeconds(-45);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException) { return false; }
    }

    private static bool IsPrivateNetwork(string value)
    {
        if (value.Length > 32 || !IPNetwork.TryParse(value, out var network) || network.PrefixLength is < 8 or > 32
            || network.BaseAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        var bytes = network.BaseAddress.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31 && network.PrefixLength >= 12)
            || (bytes[0] == 192 && bytes[1] == 168 && network.PrefixLength >= 16);
    }
}
