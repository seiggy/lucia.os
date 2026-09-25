using System.Security.Cryptography;
using System.Text.Json;
using Lucia.Homelab.Server.Host;

namespace Lucia.Homelab.Server.Domains;

public sealed class DomainOnboardingWorker(
    DomainOnboardingStore store, DomainOnboardingOptions options, DomainOnboardingService service,
    CloudflareDomainService cloudflare, ILocalDnsProvider dns, DomainDnsPreflight preflight,
    CertbotCertificateService certificates, HostAuthenticationOptions authentication,
    IHostApplicationLifetime lifetime, DomainConnectionGate connections, ILogger<DomainOnboardingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initial = await store.Read(stoppingToken);
        if (initial.Job is { State: "Running" or "Activating" } interrupted)
        {
            if (DomainActivationConfiguration.Read(store.Root)?.ProfileId == interrupted.Id)
                await CompleteActivation(interrupted, stoppingToken);
            else
                await Recover(interrupted.Id, "The host restarted before domain activation committed.", stoppingToken);
        }
        if (initial.Job is { State: "Active", Phase: "Renewing" } interruptedRenewal)
            await Update(interruptedRenewal.Id, job => job with { Phase = "Active", NextRenewalAt = DateTimeOffset.UtcNow.AddMinutes(1),
                RenewalError = "A certificate renewal check was interrupted by a host restart." });
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            var document = await store.Read(stoppingToken);
            if (document.Job is { State: "Queued", Phase: "Recovering" } cleanup)
                await Recover(cleanup.Id, "Retrying cleanup of the previous DNS attempt.", stoppingToken);
            else if (document.Job is { State: "Queued" } job) await Execute(job, stoppingToken);
            else if (document.Job is { State: "Active" or "Activating", NextRenewalAt: { } next } active && next <= DateTimeOffset.UtcNow)
                await Renew(active, stoppingToken);
            else if (document.Job is { State: "Activating" } pending
                && (pending.NextActivationCheckAt is null || pending.NextActivationCheckAt <= DateTimeOffset.UtcNow))
                await CompleteActivation(pending, stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task Execute(DomainSetupJob job, CancellationToken ct)
    {
        var plan = job.Plan;
        var gatewayPath = Path.Combine(options.GatewayDirectory, DomainIngressConfiguration.FileName);
        try
        {
            await Phase(job.Id, "Checking", "Rechecking the reviewed provider, DNS, and activation prerequisites.");
            if (!service.WorkerReady()) throw new InvalidOperationException("The activation service is unavailable.");
            var connection = await dns.GetConnectionAsync(ct);
            if (connection.BaseUrl != plan.AdGuardOrigin || connection.Username != plan.AdGuardUsername)
                throw new InvalidOperationException("The AdGuard connection changed after review.");
            var account = await cloudflare.GetStatusAsync(ct);
            if (account.AccountId != plan.AccountId) throw new InvalidOperationException("The Cloudflare account changed after review.");
            var zone = await cloudflare.GetZoneAsync(plan.ZoneId, ct);
            if (zone.Name != plan.Naming.Domain) throw new InvalidOperationException("The selected DNS zone changed.");
            var review = await preflight.ReviewAsync(plan.Naming, zone, plan.IngressAddress, await dns.ListRewritesAsync(ct), ct);
            if (review.Blockers.Length > 0) throw new InvalidOperationException("DNS configuration changed. Prepare a new review.");
            var access = await cloudflare.GetTokenAsync(ct);
            var token = access.Token ?? throw new InvalidOperationException("Reconnect Cloudflare before issuing certificates.");
            var request = Request(job);
            var challenges = plan.Naming.CertificateNames.Select(name => "_acme-challenge." + name.TrimStart('*', '.')).Distinct().ToArray();
            var beforeTxt = (await cloudflare.ListChallengeRecordsAsync(plan.ZoneId, challenges, ct))
                .Where(record => record.Type == "TXT").Select(record => record.Id).ToHashSet();
            await Phase(job.Id, "Staging", "Testing DNS-01 with Let's Encrypt staging. Temporary public TXT records will be created.");
            await certificates.StageAsync(request, token, ct);
            await CheckChallengeCleanup(plan.ZoneId, challenges, beforeTxt, ct);
            await Phase(job.Id, "Issuing", "Obtaining and validating the production certificate.");
            var certificate = await certificates.IssueAsync(request, token, ct);
            await CheckChallengeCleanup(plan.ZoneId, challenges, beforeTxt, ct);
            var profile = new DomainRuntimeProfile(1, plan.Naming.Domain, plan.Naming.ServiceUrls.Lucia,
                plan.Naming.ServiceUrls.Authentik, plan.Naming.ServiceUrls.Spark,
                authentication.PublicOrigin, authentication.Authority, job.Id, DateTimeOffset.UtcNow);
            profile.Validate(authentication.PublicOrigin, authentication.Authority);
            await Update(job.Id, current => current with { Certificate = certificate, ActivationProfile = profile });
            DomainOnboardingStore.RejectLinks(gatewayPath);
            string? ingressBefore = null;
            if (File.Exists(gatewayPath))
            {
                if (new FileInfo(gatewayPath).Length > 65536) throw new InvalidDataException("Existing domain ingress is oversized.");
                ingressBefore = await File.ReadAllTextAsync(gatewayPath, ct);
            }
            await Phase(job.Id, "Ingress", "Publishing the certificate to the existing gateway and verifying HTTPS before changing local DNS.");
            var published = Path.Combine(store.Root, "certificates");
            var ingressPublished = DomainIngressConfiguration.Build(plan.Naming, published, certificate.CertificateFile, certificate.KeyFile);
            await Update(job.Id, current => current with { IngressBefore = ingressBefore, IngressPublished = ingressPublished });
            DomainIngressConfiguration.Publish(options.GatewayDirectory, plan.Naming, published, certificate.CertificateFile, certificate.KeyFile);
            await WaitForGateway(plan, ct, certificate.CertificateSha256);
            await Phase(job.Id, "LocalDns", "Adding only the reviewed local service rewrites in AdGuard.");
            foreach (var rewrite in plan.Rewrites)
            {
                var existing = await dns.ListRewritesAsync(ct);
                var same = existing.Where(item => item.Domain.TrimEnd('.').Equals(rewrite.Domain, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (same.Length == 1 && same[0].Enabled && same[0].Answer == rewrite.Answer) continue;
                if (same.Length != 0) throw new InvalidOperationException("An AdGuard rewrite changed after review.");
                await Update(job.Id, current => current with { PendingRewrites = [.. current.PendingRewrites ?? [], rewrite.Domain] });
                await dns.AddRewriteAsync(new(rewrite.Domain, rewrite.Answer), ct);
                await Update(job.Id, current => current with { CreatedRewrites = [.. current.CreatedRewrites, rewrite.Domain],
                    PendingRewrites = (current.PendingRewrites ?? []).Where(name => name != rewrite.Domain).ToArray() });
            }
            var health = await dns.GetHealthAsync(ct);
            foreach (var name in plan.Naming.LocalHostnames)
                await DomainConnectivity.VerifyLocalAddress(name, plan.IngressAddress, plan.AdGuardOrigin, health.DnsPort, ct);
            await Phase(job.Id, "Registration", "Requesting the scoped Authentik callback update. Administrator credentials remain outside the web host.");
            await Update(job.Id, current => current with { RegistrationRequested = true });
            await ActivationRequest(job.Id, profile, "prepare", ct);
            await Update(job.Id, current => current with
            {
                State = "Activating", Phase = "Activating", NextRenewalAt = DateTimeOffset.UtcNow.AddHours(12),
                Message = "Restarting the host with the new canonical URLs. Legacy API access remains available."
            });
            await DomainOnboardingStore.WriteJson(Path.Combine(store.Root, "active.json"), profile, ct);
            lifetime.StopApplication();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogError("Domain setup {JobId} failed in its current phase ({ErrorType}).", job.Id, error.GetType().Name);
            var phase = (await store.Read()).Job?.Phase ?? "Unknown";
            var failure = error is DomainProbeException probe
                ? new DomainFailure(probe.Code, phase, probe.Target, probe.StatusCode, error.GetType().Name)
                : new DomainFailure(error is CertbotException certbot ? certbot.DiagnosticCode ?? certbot.Code : "setup_failure",
                    phase, null, null, error.GetType().Name);
            await Update(job.Id, current => current with { Failure = failure });
            var message = error is DomainProbeException checkedFailure ? checkedFailure.Message : error is CertbotException known ? known.Message :
                "DNS setup could not complete. Existing private CA and accounts are preserved; review connections and the recorded phase before retrying.";
            await Recover(job.Id, message, CancellationToken.None);
            await Update(job.Id, current => current with
            {
                Diagnosis = DomainSupportSkill.Diagnose(failure.Code, current.RecoveryRequired, failure),
                Support = new("Queued", null, null, null, DateTimeOffset.UtcNow)
            });
        }
    }

    private async Task Recover(Guid jobId, string cause, CancellationToken ct)
    {
        var job = (await store.Read(ct)).Job ?? throw new InvalidOperationException("The recovery journal is missing.");
        if (job.Id != jobId) throw new InvalidOperationException("The recovery job changed.");
        if (DomainActivationConfiguration.Read(store.Root)?.ProfileId == jobId)
        {
            await Update(jobId, current => current with { State = "Activating", Phase = "ActivationCheck",
                NextActivationCheckAt = DateTimeOffset.UtcNow, NextRenewalAt = current.NextRenewalAt ?? DateTimeOffset.UtcNow.AddHours(12),
                Message = "The domain profile committed before interruption. Restarting to verify it without discarding renewal state." });
            lifetime.StopApplication();
            return;
        }
        await Update(jobId, current => current with { State = "Running", Phase = "Recovering", Message = cause });
        var issues = new List<string>();
        if (job.RegistrationRequested && job.ActivationProfile is { } profile)
        {
            try
            {
                await ActivationRequest(jobId, profile, "rollback", ct);
                await Update(jobId, current => current with { RegistrationRequested = false });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                issues.Add("Authentik registration rollback needs review.");
                logger.LogError("Domain registration recovery failed ({ErrorType}).", error.GetType().Name);
            }
        }
        if (job.CreatedRewrites.Length > 0 || job.PendingRewrites is { Length: > 0 })
        {
            try
            {
                var connection = await dns.GetConnectionAsync(ct);
                if (!connection.Configured || connection.BaseUrl != job.Plan.AdGuardOrigin || connection.Username != job.Plan.AdGuardUsername)
                    throw new InvalidOperationException("The DNS connection changed; do not clean up a different server.");
                foreach (var name in job.PendingRewrites ?? [])
                {
                    var matches = (await dns.ListRewritesAsync(ct)).Where(item =>
                        item.Domain.TrimEnd('.').Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length == 0)
                        await Update(jobId, current => current with { PendingRewrites = (current.PendingRewrites ?? []).Where(item => item != name).ToArray() });
                    else
                        issues.Add($"Addition of {name} had an uncertain outcome. Review that AdGuard rule; it was not adopted or deleted.");
                }
                foreach (var name in job.CreatedRewrites.Reverse())
                {
                    var matches = (await dns.ListRewritesAsync(ct)).Where(item =>
                        item.Domain.TrimEnd('.').Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length == 1 && matches[0].Domain == name && matches[0].Answer == job.Plan.IngressAddress && matches[0].Enabled)
                    {
                        await dns.DeleteRewriteAsync(matches[0], ct);
                        await Update(jobId, current => current with { CreatedRewrites = current.CreatedRewrites.Where(item => item != name).ToArray() });
                    }
                    else if (matches.Length == 0)
                        await Update(jobId, current => current with { CreatedRewrites = current.CreatedRewrites.Where(item => item != name).ToArray() });
                    else issues.Add($"The rewrite for {name} changed. It was left for review.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                issues.Add("DNS cleanup needs review; the outstanding mutation journal is retained.");
                logger.LogError("Domain DNS recovery failed ({ErrorType}).", error.GetType().Name);
            }
        }
        if (job.IngressPublished is not null)
        {
            try
            {
                var gatewayPath = Path.Combine(options.GatewayDirectory, DomainIngressConfiguration.FileName);
                DomainOnboardingStore.RejectLinks(gatewayPath);
                if (File.Exists(gatewayPath) && new FileInfo(gatewayPath).Length > 65536)
                    throw new InvalidDataException("Domain ingress is oversized.");
                var current = File.Exists(gatewayPath) ? await File.ReadAllTextAsync(gatewayPath, ct) : null;
                if (current == job.IngressPublished)
                {
                    if (job.IngressBefore is null) File.Delete(gatewayPath);
                    else await WriteTextAtomically(gatewayPath, job.IngressBefore);
                    current = job.IngressBefore;
                }
                if (current == job.IngressBefore)
                    await Update(jobId, item => item with { IngressPublished = null });
                else issues.Add("Gateway configuration changed. It was not overwritten during recovery.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                issues.Add("Gateway restoration needs review.");
                logger.LogError("Domain ingress recovery failed ({ErrorType}).", error.GetType().Name);
            }
        }
        await Update(jobId, current => current with
        {
            State = "Failed", Phase = "Recovery",
            RecoveryRequired = current.RegistrationRequested || current.CreatedRewrites.Length > 0
                || current.PendingRewrites is { Length: > 0 } || current.IngressPublished is not null,
            Message = cause + (issues.Count == 0 ? " Pre-activation changes were restored; prepare a new review before retrying."
                : " " + string.Join(" ", issues)),
            Diagnosis = current.Diagnosis is { } diagnosis
                ? DomainSupportSkill.Diagnose(diagnosis.Code, current.RegistrationRequested || current.CreatedRewrites.Length > 0
                    || current.PendingRewrites is { Length: > 0 } || current.IngressPublished is not null, current.Failure) : null,
            Support = null
        });
    }

    private async Task CompleteActivation(DomainSetupJob job, CancellationToken ct)
    {
        try
        {
            var profile = DomainActivationConfiguration.Read(store.Root);
            if (profile?.ProfileId != job.Id || authentication.PublicOrigin != job.Plan.Naming.ServiceUrls.Lucia)
                throw new InvalidOperationException("The restarted host did not load the reviewed domain profile.");
            await WaitForGateway(job.Plan, ct, job.Certificate?.CertificateSha256
                ?? throw new InvalidDataException("A committed activation has no certificate receipt."));
            await Update(job.Id, current => current with { State = "Active", Phase = "Active",
                Message = "DNS and HTTPS endpoints are active. Open Lucia at its new address and sign in through Authentik.",
                NextRenewalAt = current.NextRenewalAt ?? DateTimeOffset.UtcNow.AddHours(12), NextActivationCheckAt = null });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogError("Post-restart domain activation check failed ({ErrorType}).", error.GetType().Name);
            await Update(job.Id, current => current with { State = "Activating", Phase = "ActivationCheck",
                NextActivationCheckAt = DateTimeOffset.UtcNow.AddMinutes(1),
                NextRenewalAt = current.NextRenewalAt ?? DateTimeOffset.UtcNow.AddHours(12),
                Message = "The new URLs could not be confirmed after restart. Checks will retry; legacy API access and the renewal schedule are retained." });
        }
    }

    private async Task Renew(DomainSetupJob job, CancellationToken ct)
    {
        await connections.Mutex.WaitAsync(ct);
        try { await Update(job.Id, current => current with { Phase = "Renewing", NextRenewalAt = DateTimeOffset.UtcNow.AddHours(12) }); }
        finally { connections.Mutex.Release(); }
        string? previousIngress = null;
        string? publishedIngress = null;
        var gatewayPath = Path.Combine(options.GatewayDirectory, DomainIngressConfiguration.FileName);
        try
        {
            var access = await cloudflare.GetTokenAsync(ct);
            if (access.AccountId != job.Plan.AccountId) throw new InvalidOperationException("The renewal Cloudflare account differs from the reviewed zone.");
            await cloudflare.GetZoneAsync(job.Plan.ZoneId, ct);
            var certificate = await certificates.RenewAsync(Request(job),
                access.Token ?? throw new InvalidOperationException("Cloudflare is disconnected."), ct);
            if (certificate.CertificateSha256 != job.Certificate?.CertificateSha256)
            {
                DomainOnboardingStore.RejectLinks(gatewayPath);
                if (!File.Exists(gatewayPath) || new FileInfo(gatewayPath).Length > 65536)
                    throw new InvalidDataException("The active domain ingress is missing or invalid.");
                previousIngress = await File.ReadAllTextAsync(gatewayPath, ct);
                publishedIngress = DomainIngressConfiguration.Build(job.Plan.Naming, Path.Combine(store.Root, "certificates"),
                    certificate.CertificateFile, certificate.KeyFile);
                DomainIngressConfiguration.Publish(options.GatewayDirectory, job.Plan.Naming, Path.Combine(store.Root, "certificates"),
                    certificate.CertificateFile, certificate.KeyFile);
            }
            await WaitForGateway(job.Plan, ct, certificate.CertificateSha256);
            await Update(job.Id, current => current with { Phase = "Active", Certificate = certificate, RenewalError = null,
                RenewalCheckedAt = DateTimeOffset.UtcNow,
                RenewalOutcome = certificate.CertificateSha256 != job.Certificate?.CertificateSha256 ? "Renewed" : "NotDue" });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogError("Domain certificate renewal failed ({ErrorType}).", error.GetType().Name);
            if (previousIngress is not null && publishedIngress is not null && File.Exists(gatewayPath)
                && await File.ReadAllTextAsync(gatewayPath) == publishedIngress)
                await WriteTextAtomically(gatewayPath, previousIngress);
            await Update(job.Id, current => current with { Phase = "Active",
                RenewalError = error is DomainProbeException probe ? probe.Message : error is CertbotException certbot ? certbot.Message
                    : "Certificate renewal or deployment needs attention. Check Cloudflare credentials, DNS, and certificate expiry.",
                RenewalCheckedAt = DateTimeOffset.UtcNow, RenewalOutcome = "Failed" });
        }
    }

    private static CertbotCertificateRequest Request(DomainSetupJob job) =>
        new("lucia-" + job.Id.ToString("N"), job.Plan.Naming.CertificateNames, job.Plan.Email, true,
            job.Plan.TermsUrl, job.Plan.PropagationSeconds);

    private async Task CheckChallengeCleanup(string zone, string[] names, HashSet<string> before, CancellationToken ct)
    {
        var after = await cloudflare.ListChallengeRecordsAsync(zone, names, ct);
        if (after.Any(record => record.Type == "TXT" && !before.Contains(record.Id)))
            throw new InvalidOperationException("New challenge TXT records remain. Review cleanup in Cloudflare; unrelated records were not removed.");
    }

    private async Task WaitForGateway(DomainSetupPlan plan, CancellationToken ct, string certificateFingerprint)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                await DomainConnectivity.VerifyGateway(plan.Naming.ServiceUrls.Lucia, plan.IngressAddress, "/health/live",
                    authentication, true, value => value.GetProperty("status").GetString() == "ok", ct, certificateFingerprint);
                await DomainConnectivity.VerifyGateway(plan.Naming.ServiceUrls.Authentik, plan.IngressAddress,
                    "/application/o/lucia/.well-known/openid-configuration", authentication, true,
                    value => value.GetProperty("issuer").GetString() == plan.Naming.ServiceUrls.Authentik + "/application/o/lucia/", ct, certificateFingerprint);
                var active = DomainActivationConfiguration.Read(store.Root);
                var legacyLucia = active?.LegacyLuciaOrigin ?? authentication.PublicOrigin;
                var legacyAuthority = active?.LegacyAuthority ?? authentication.Authority;
                await DomainConnectivity.VerifyGateway(legacyLucia, plan.IngressAddress, "/health/live", authentication, false,
                    value => value.GetProperty("status").GetString() == "ok", ct);
                await DomainConnectivity.VerifyGateway(new Uri(legacyAuthority).GetLeftPart(UriPartial.Authority), plan.IngressAddress,
                    "/application/o/lucia/.well-known/openid-configuration", authentication, false,
                    value => value.GetProperty("issuer").GetString() == legacyAuthority, ct);
                return;
            }
            catch (Exception error) when (error is DomainProbeException or HttpRequestException or InvalidOperationException or JsonException
                || error is OperationCanceledException && !ct.IsCancellationRequested)
            { last = error; await Task.Delay(TimeSpan.FromSeconds(2), ct); }
        }
        if (last is DomainProbeException failure) throw failure;
        throw new DomainProbeException("gateway_probe_failed", plan.Naming.ServiceUrls.Lucia,
            "The HTTPS gateway check failed. The previous private addresses were kept.");
    }

    private async Task ActivationRequest(Guid jobId, DomainRuntimeProfile profile, string action, CancellationToken ct)
    {
        var path = Path.Combine(store.Root, "activation-requests", jobId + ".json");
        await DomainOnboardingStore.WriteJson(path, new { schemaVersion = 1, jobId, expiresAt = DateTimeOffset.UtcNow.AddMinutes(10), action, profile }, ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, ct)));
        var responsePath = Path.Combine(store.Root, "activation-responses", jobId + ".json");
        for (var attempt = 0; attempt < 90; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            DomainOnboardingStore.RejectLinks(responsePath);
            if (File.Exists(responsePath))
            {
                if (new FileInfo(responsePath).Length > 4096) throw new InvalidDataException("Activation response is oversized.");
                using var response = JsonDocument.Parse(await File.ReadAllBytesAsync(responsePath, ct));
                var value = response.RootElement;
                if (value.GetProperty("requestHash").GetString() == hash)
                {
                    if (value.GetProperty("schemaVersion").GetInt32() != 1 || value.GetProperty("jobId").GetGuid() != jobId
                        || value.GetProperty("action").GetString() != action || !value.GetProperty("success").GetBoolean())
                        throw new InvalidOperationException("The scoped registration update was not verified.");
                    return;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new InvalidOperationException("The scoped activation service did not complete in time.");
    }

    private Task<DomainSetupDocument> Phase(Guid id, string phase, string message) =>
        Update(id, job => job with { State = "Running", Phase = phase, Message = message });

    private Task<DomainSetupDocument> Update(Guid id, Func<DomainSetupJob, DomainSetupJob> update) =>
        store.Update(document =>
        {
            var previous = document.Job;
            if (previous?.Id != id) throw new InvalidOperationException("Domain workflow ownership changed.");
            var next = update(previous) with { UpdatedAt = DateTimeOffset.UtcNow };
            if (next.Phase != previous.Phase || next.Message != previous.Message)
                next = next with { Events = previous.Events.Append(new(next.UpdatedAt, next.Phase, next.Message)).TakeLast(100).ToArray() };
            return document with { Job = next };
        });

    private static async Task WriteTextAtomically(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
