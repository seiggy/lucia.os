using System.Text.Json;
using Lucia.Homelab.Server.Host;

namespace Lucia.Homelab.Server.Domains;

public sealed record AdGuardCertificateState(bool Enabled, string? Name = null, string? Lineage = null,
    CertbotCertificateReceipt? Certificate = null, DateTimeOffset? CheckedAt = null, DateTimeOffset? PushedAt = null,
    string? Error = null);

public sealed record AdGuardCertificateStatus(bool Enabled, string? Name, DateTimeOffset? NotAfter,
    DateTimeOffset? CheckedAt, DateTimeOffset? PushedAt, string? Error, IReadOnlyList<string>? CoveredNames = null);

/// <summary>A null Name keeps the saved one; an empty Name means the connection's host name.</summary>
public sealed record AdGuardCertificateToggle(bool Enabled, string? Name = null);

/// <summary>Owner opt-in. Issues a certificate for the name devices use (by default the AdGuard connection's
/// host name) through the domain setup's Cloudflare token and ACME account, pushes it to AdGuard and renews it
/// twice a day. The connection's host name is always covered too, so Lucia keeps reaching AdGuard while the
/// owner moves it to a new name. Turning it off leaves AdGuard's TLS settings as they are.</summary>
public sealed class AdGuardCertificateWorker(DomainOnboardingStore store, CertbotCertificateService certificates,
    CloudflareDomainService cloudflare, AdGuardConnectionService adguard, ILogger<AdGuardCertificateWorker> logger, AdGuardFleet? fleet = null)
    : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private string FilePath => Path.Combine(store.Root, "adguard-certificate.json");

    public async Task<AdGuardCertificateStatus> GetStatusAsync(CancellationToken ct) => Status(await ReadAsync(ct));

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (fleet is not null) fleet.Changed += Wake;
        return base.StartAsync(cancellationToken);
    }

    /// <summary>The certificate last issued for AdGuard, names[0] being the name it serves, for setting up a new instance.</summary>
    internal static async Task<(string[] Names, string Chain, string Key)?> InstalledAsync(string root, CancellationToken ct)
    {
        var path = Path.Combine(root, "adguard-certificate.json");
        if (!File.Exists(path)) return null;
        var state = JsonSerializer.Deserialize<AdGuardCertificateState>(await File.ReadAllBytesAsync(path, ct), Json);
        if (state is not { Enabled: true, Certificate: { } receipt } || !File.Exists(receipt.CertificateFile)) return null;
        string[] names = [.. (state.Name is { } name ? [name] : Array.Empty<string>()).Concat(receipt.DnsNames).Distinct(StringComparer.Ordinal)];
        return (names, await File.ReadAllTextAsync(receipt.CertificateFile, ct), await File.ReadAllTextAsync(receipt.KeyFile, ct));
    }

    public async Task<AdGuardCertificateStatus> SetEnabledAsync(bool enabled, string? name, CancellationToken ct)
    {
        if (name is { Length: > 0 })
        {
            try { name = CertbotCertificateService.ValidateDnsName(name.Trim().TrimEnd('.')); }
            catch (CertbotException)
            { throw new AdGuardManagementException(400, "adguard_certificate_name_invalid", "Enter a DNS name such as adguard.example.com."); }
            if (name.StartsWith("*.", StringComparison.Ordinal))
                throw new AdGuardManagementException(400, "adguard_certificate_name_invalid", "Enter one DNS name, not a wildcard.");
        }
        var state = await UpdateAsync(current =>
        {
            var next = name is null ? current.Name : name.Length == 0 ? null : name;
            return current with { Enabled = enabled, Error = null, Name = next, PushedAt = next == current.Name ? current.PushedAt : null };
        }, ct);
        if (enabled) Wake();
        return Status(state);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromHours(12);
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                logger.LogError("AdGuard certificate update failed ({ErrorType}).", error.GetType().Name);
                delay = TimeSpan.FromMinutes(30);
                var message = error switch
                {
                    AdGuardCertificateException or AdGuardManagementException or CertbotException => error.Message,
                    _ => "The AdGuard certificate could not be issued or applied. Check that the Cloudflare token can edit DNS for this name."
                };
                await UpdateAsync(current => current with { CheckedAt = DateTimeOffset.UtcNow, Error = message }, CancellationToken.None);
            }
            try { await _wake.WaitAsync(delay, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var state = await ReadAsync(ct);
        if (!state.Enabled) return;
        var job = (await store.Read(ct)).Job;
        if (job?.Certificate is null)
            throw new AdGuardCertificateException("Finish domain setup first. Lucia issues this certificate with the same Cloudflare token and Let’s Encrypt account.");
        string host;
        try { host = CertbotCertificateService.ValidateDnsName(await adguard.CertificateNameAsync(ct)); }
        catch (CertbotException)
        { throw new AdGuardCertificateException("Connect AdGuard by DNS name, not IP address, so its certificate has a name to cover."); }
        // Each AdGuard instance's own name too, which adguardhome-sync reaches it by.
        var ns = (await Nodes.ManagedNodeDns.ActiveNaming(store, ct))?.Namespace;
        string[] names = [.. new[] { state.Name ?? host, host }.Concat(ns is null || fleet is null ? [] : (await fleet.RecordsAsync(ns, ct)).Select(record => record.Domain))
            .Distinct(StringComparer.Ordinal)];
        var access = await cloudflare.GetTokenAsync(ct);
        var token = access.Token ?? throw new AdGuardCertificateException("Reconnect Cloudflare in domain setup.");
        var fresh = state.Lineage is null || state.Certificate is null || !state.Certificate.DnsNames.Order().SequenceEqual(names.Order());
        var lineage = fresh ? CertbotCertificateRequest.NewLineageName() : state.Lineage!;
        var request = new CertbotCertificateRequest(lineage, names, job.Plan.Email, true, job.Plan.TermsUrl, job.Plan.PropagationSeconds);
        CertbotCertificateReceipt receipt;
        if (fresh)
        {
            await certificates.StageAsync(request, token, ct);
            receipt = await certificates.IssueAsync(request, token, ct);
        }
        else receipt = await certificates.RenewAsync(request, token, ct);
        await UpdateAsync(current => current with { Lineage = lineage, Certificate = receipt }, ct);
        var (chain, key) = (await File.ReadAllTextAsync(receipt.CertificateFile, ct), await File.ReadAllTextAsync(receipt.KeyFile, ct));
        var pushed = false;
        AdGuardManagementException? failure = null;
        var targets = fleet is null ? [] : await fleet.TargetsAsync(ct);
        foreach (var address in targets.Length == 0 ? [null] : targets.Cast<System.Net.IPAddress?>())
        {
            try { pushed |= await adguard.PushCertificateAsync(names, chain, key, ct, address); }
            catch (AdGuardManagementException error) { failure ??= error; }
        }
        if (failure is not null) throw failure;
        var now = DateTimeOffset.UtcNow;
        await UpdateAsync(current => current with { CheckedAt = now, PushedAt = pushed || current.PushedAt is null ? now : current.PushedAt, Error = null }, ct);
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task<AdGuardCertificateState> ReadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await ReadFileAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task<AdGuardCertificateState> UpdateAsync(Func<AdGuardCertificateState, AdGuardCertificateState> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = change(await ReadFileAsync(ct));
            var temporary = FilePath + ".tmp";
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(state, Json), ct);
            File.Move(temporary, FilePath, overwrite: true);
            return state;
        }
        finally { _gate.Release(); }
    }

    private async Task<AdGuardCertificateState> ReadFileAsync(CancellationToken ct) => File.Exists(FilePath)
        ? JsonSerializer.Deserialize<AdGuardCertificateState>(await File.ReadAllBytesAsync(FilePath, ct), Json) ?? new(false)
        : new(false);

    private static AdGuardCertificateStatus Status(AdGuardCertificateState state) =>
        new(state.Enabled, state.Name, state.Certificate?.NotAfter, state.CheckedAt, state.PushedAt, state.Error, state.Certificate?.DnsNames);
}

public sealed class AdGuardCertificateException(string message) : Exception(message);
