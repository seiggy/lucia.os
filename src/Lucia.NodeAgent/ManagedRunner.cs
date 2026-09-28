using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Lucia.NodeAgent;

internal static class ManagedRunner
{
    internal static async Task RunAsync(CancellationToken token)
    {
        SecureStateDirectory.RequireRoot();
        if (Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) != "/usr/lib/lucia/agent"
            || Environment.ProcessPath != "/usr/lib/lucia/agent/lucia-node-agent")
            throw new NodeAgentException("managed-run is permitted only from the installed runtime on the installed boot root.");
        var origin = JsonSerializer.Deserialize<ManagedOrigin>(
            SecureStateDirectory.ReadSystemFile("/etc/lucia/managed.json"), AgentJson.Options)
            ?? throw new NodeAgentException("The installed origin configuration is missing.");
        using var authority = DiscoveryClient.LoadAuthority("/etc/lucia/public-ca.crt");
        using var client = new DiscoveryClient(origin.Server, authority);
        using var state = new SecureStateDirectory(ManagedFiles.StatePath);
        using var stateLock = state.Lock();
        var plan = state.ReadJson<InstallPlan>("install-plan.json")!;
        var grant = state.ReadJson<InstallationGrant>("grant.json")!;
        InstallationRules.ValidateGrant(grant, plan, grant.RequestId, DateTimeOffset.UtcNow, forErase: false);
        var disks = new DiskSafety();
        if (disks.BootId() == plan.BootId)
            throw new NodeAgentException("Managed enrollment is not allowed in the installation boot.");
        disks.VerifyInstalledMount(new HardwareInspector().Inspect(), plan, "/");
        // /etc/hostname, not the kernel name: dhcpcd may rewrite the latter to the DHCP-supplied FQDN.
        if (DiskSafety.Read("/etc/hostname", 128).Trim() != plan.Hostname)
            throw new NodeAgentException("The installed hostname does not match the approved node identity.");
        using var key = state.LoadExistingKey();
        var csrBytes = state.ReadPrivate("node.csr", 8192, optional: true);
        if (csrBytes is null)
        {
            state.WritePrivate("node.csr", Encoding.UTF8.GetBytes(ManagedIdentity.CreateCsr(plan, key)), replace: false);
            csrBytes = state.ReadPrivate("node.csr", 8192)!;
        }
        var csr = Encoding.UTF8.GetString(csrBytes);
        ManagedIdentity.ValidateCsr(csr, plan, key);
        var configuration = state.ReadJson<ManagedConfiguration>("managed-configuration.json", optional: true);
        while (configuration is null)
        {
            token.ThrowIfCancellationRequested();
            var credentials = state.LoadCredentials();
            if (credentials.DeviceId != plan.DeviceId || grant.ProgressExpiresAt <= DateTimeOffset.UtcNow)
                throw new NodeAgentException("First-boot enrollment authority expired. Owner intervention is required.");
            try
            {
                configuration = await client.EnrollAsync(credentials, plan, grant, csr, key, token);
                if (configuration is not null)
                {
                    using var checkedCertificate = ManagedIdentity.ValidateConfiguration(configuration, plan, key, client.Server);
                    state.WriteJson("managed-configuration.json", configuration);
                }
                else Console.Error.WriteLine("Native enrollment is pending. The node is not managed yet.");
            }
            catch (NodeAgentException ex)
            {
                configuration = null;
                Console.Error.WriteLine("First-boot enrollment is unavailable or invalid; retrying within the enrollment deadline. " + ex.Message);
            }
            if (configuration is null) await Task.Delay(TimeSpan.FromSeconds(30), token);
        }
        _ = Task.Run(() => NodeRuntime.RunAsync(token), token);
        // The closures read the current configuration, so renewed certificates carry over.
        _ = Task.Run(() => StackRunner.RunAsync(client, plan.DeviceId, () => configuration!.CertificatePem, key, token), token);
        _ = Task.Run(() => NodeRequests.RunAsync(client, plan.DeviceId, () => configuration!.CertificatePem, key, token), token);
        var nextRenewal = DateTimeOffset.MinValue;
        var renewalFailures = 0;
        var directoryConfigured = false;
        using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (true)
        {
            token.ThrowIfCancellationRequested();
            using var cached = ManagedIdentity.ValidateConfiguration(configuration, plan, key, client.Server, allowExpiredForRenewal: true);
            if (cached.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            {
                if (nextRenewal <= DateTimeOffset.UtcNow)
                {
                    try
                    {
                        var recovered = await RecoverExpiredAsync(client, configuration, plan, csr, key, authority, token);
                        if (recovered is not null)
                        {
                            state.WriteJson("managed-configuration.json", recovered);
                            configuration = recovered;
                            directoryConfigured = false;
                            renewalFailures = 0;
                        }
                        else renewalFailures = Math.Min(renewalFailures + 1, 5);
                    }
                    catch (NodeAgentException ex)
                    {
                        renewalFailures = Math.Min(renewalFailures + 1, 5);
                        Console.Error.WriteLine("Expired identity recovery is pending or rejected. No heartbeat was sent. " + ex.Message);
                    }
                    nextRenewal = DateTimeOffset.UtcNow.AddSeconds(Math.Min(900, 30 * (1 << renewalFailures)));
                }
                await heartbeatTimer.WaitForNextTickAsync(token);
                continue;
            }
            using var certificate = ManagedIdentity.ValidateConfiguration(configuration, plan, key, client.Server);
            if (!directoryConfigured)
            {
                await ManagedFiles.ConfigureDirectoryAsync(configuration, client.Server, token);
                directoryConfigured = true;
                Console.Error.WriteLine("Directory configuration installed. Awaiting an authenticated heartbeat.");
            }
            if (NeedsRenewal(certificate.NotAfter.ToUniversalTime(), DateTimeOffset.UtcNow)
                && nextRenewal <= DateTimeOffset.UtcNow)
            {
                try
                {
                    var renewed = await client.RenewAsync(configuration.CertificatePem, plan, csr, key, token);
                    if (renewed is not null)
                    {
                        using var renewedCertificate = ManagedIdentity.ValidateConfiguration(renewed, plan, key, client.Server);
                        if (renewedCertificate.NotAfter <= certificate.NotAfter)
                            throw new NodeAgentException("Renewal did not extend certificate validity.");
                        state.WriteJson("managed-configuration.json", renewed);
                        configuration = renewed;
                        directoryConfigured = false;
                        await ManagedFiles.ConfigureDirectoryAsync(renewed, client.Server, token);
                        directoryConfigured = true;
                        renewalFailures = 0;
                    }
                    else renewalFailures = Math.Min(renewalFailures + 1, 5);
                }
                catch (NodeAgentException ex)
                {
                    renewalFailures = Math.Min(renewalFailures + 1, 5);
                    Console.Error.WriteLine("Certificate renewal remains pending or failed validation. The still-valid identity is retained. " + ex.Message);
                }
                nextRenewal = DateTimeOffset.UtcNow.AddSeconds(Math.Min(900, 30 * (1 << renewalFailures)));
            }
            if (!directoryConfigured)
            {
                await heartbeatTimer.WaitForNextTickAsync(token);
                continue;
            }
            try
            {
                using var heartbeatIdentity = ManagedIdentity.ValidateConfiguration(configuration, plan, key, client.Server);
                var sshKeys = await client.HeartbeatAsync(configuration.CertificatePem, ReadMetrics(plan), key, token);
                Console.Error.WriteLine("Authenticated managed heartbeat accepted.");
                // An older Lucia omits the list; keep whatever is installed rather than revoking everything.
                if (sshKeys is not null)
                {
                    try
                    {
                        if (ManagedFiles.WriteSshKeys(sshKeys))
                            Console.Error.WriteLine($"Owner SSH keys updated for {sshKeys.Count} account(s).");
                    }
                    catch (Exception ex) when (ex is NodeAgentException or IOException or UnauthorizedAccessException)
                    {
                        Console.Error.WriteLine("Owner SSH keys could not be updated; the installed keys were kept. " + ex.Message);
                    }
                }
            }
            catch (NodeAgentException ex)
            {
                Console.Error.WriteLine("Authenticated heartbeat was not accepted; management freshness is not confirmed. " + ex.Message);
            }
            await heartbeatTimer.WaitForNextTickAsync(token);
        }
    }

    internal static bool NeedsRenewal(DateTimeOffset notAfter, DateTimeOffset now) =>
        notAfter > now && notAfter - now < TimeSpan.FromHours(6);

    internal static async Task<ManagedConfiguration?> RecoverExpiredAsync(DiscoveryClient client, ManagedConfiguration cached,
        InstallPlan plan, string csr, ECDsa key, X509Certificate2 pinnedAuthority, CancellationToken token)
    {
        using var root = DiscoveryClient.ParseAuthority(cached.CaPem);
        if (!root.RawData.SequenceEqual(pinnedAuthority.RawData))
            throw new NodeAgentException("Expired identity recovery cannot change the installed private CA.");
        using var historical = ManagedIdentity.ValidateConfiguration(cached, plan, key, client.Server, allowExpiredForRenewal: true);
        if (historical.NotAfter.ToUniversalTime() > DateTime.UtcNow)
            throw new NodeAgentException("Expired identity recovery requires an expired, previously valid node leaf.");
        ManagedIdentity.ValidateCsr(csr, plan, key);
        var recovered = await client.RenewAsync(cached.CertificatePem, plan, csr, key, token);
        if (recovered is null) return null;
        using var recoveredRoot = DiscoveryClient.ParseAuthority(recovered.CaPem);
        if (!recoveredRoot.RawData.SequenceEqual(pinnedAuthority.RawData))
            throw new NodeAgentException("The recovery response changed the installed private CA.");
        using var current = ManagedIdentity.ValidateConfiguration(recovered, plan, key, client.Server);
        return recovered;
    }

    internal static NodeMetrics ReadMetrics(InstallPlan plan) =>
        ReadMetrics(plan, "/proc", "/etc/os-release", () =>
        {
            var root = new DriveInfo("/");
            return (root.TotalSize, root.AvailableFreeSpace);
        });

    internal static NodeMetrics ReadMetrics(InstallPlan plan, string proc, string osRelease, Func<(long Total, long Available)> storage)
    {
        string? Optional(string file)
        {
            try { return DiskSafety.Read(file, 64 * 1024); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NodeAgentException) { return null; }
        }
        double? FirstNumber(string file)
        {
            var word = Optional(Path.Combine(proc, file))?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return double.TryParse(word, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                && double.IsFinite(value) && value >= 0 ? value : null;
        }
        var memory = Optional(Path.Combine(proc, "meminfo"));
        long? Memory(string name)
        {
            var lines = memory?.Split('\n').Where(line => line.StartsWith(name + ":", StringComparison.Ordinal)).ToArray();
            if (lines?.Length != 1) return null;
            var fields = lines[0][(name.Length + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return fields.Length == 2 && fields[1] == "kB"
                && long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var kib)
                && kib <= long.MaxValue / 1024 ? kib * 1024 : null;
        }
        var version = Optional(osRelease)?.Split('\n').FirstOrDefault(line => line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))?[12..].Trim('"');
        if (version is not null && (version.Length > 256 || version.Any(char.IsControl))) version = null;
        long? total = null, available = null;
        try
        {
            var value = storage();
            if (value.Total > 0 && value.Available >= 0 && value.Available <= value.Total)
                (total, available) = value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var memoryTotal = Memory("MemTotal");
        var memoryAvailable = Memory("MemAvailable");
        if (memoryAvailable > memoryTotal) memoryAvailable = null;
        return new(plan.DeviceId, plan.Hostname, version, FirstNumber("uptime"), FirstNumber("loadavg"),
            memoryTotal, memoryAvailable, total, available, NodeRuntime.Current);
    }
}
