using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Lucia.Homelab.Server.Host;

namespace Lucia.Homelab.Server.Domains;

public sealed record StartDomainSetupRequest(Guid PlanId, string ReviewHash, bool AcceptTerms, bool AcceptDnsChanges);
public sealed record SetPublicAccessRequest(bool Enabled);

public sealed class DomainOnboardingService(
    DomainOnboardingOptions options, DomainOnboardingStore store, CloudflareDomainService cloudflare,
    ILocalDnsProvider dns, DomainDnsPreflight dnsPreflight, HttpClient http, HostAuthenticationOptions authentication,
    DomainConnectionGate connections)
{
    public async Task<object> Status(CancellationToken ct)
    {
        var state = await store.Read(ct);
        var connection = await dns.GetConnectionAsync(ct);
        var cf = await cloudflare.GetStatusAsync(ct);
        var active = DomainActivationConfiguration.Read(options.StateDirectory);
        return new
        {
            adguardConfigured = connection.Configured, cloudflareConfigured = cf.Configured,
            cloudflare = cf,
            workerReady = WorkerReady(), configured = active is not null,
            defaults = new { ingressAddress = options.IngressAddress, propagationSeconds = 60 },
            plan = state.Plan,
            job = state.Job is { } job ? new
            {
                job.Id, job.State, job.Phase, job.Message, job.CreatedAt, job.UpdatedAt, job.Events,
                job.RecoveryRequired, pendingRewrites = job.PendingRewrites ?? [], job.Diagnosis, job.Support, job.Failure,
                serviceUrls = job.Plan.Naming.ServiceUrls, job.NextRenewalAt, job.RenewalError, job.RenewalCheckedAt, job.RenewalOutcome,
                job.Public, job.PublicRequested, job.PublicError, zone = job.Plan.Naming.Domain,
                certificate = job.Certificate is { } certificate ? new
                { certificate.NotAfter, certificate.DnsNames, certificate.CertificateSha256 } : null
            } : null,
            active
        };
    }

    /// <summary>Requests public access on or off; the domain worker applies it and restarts Lucia.</summary>
    public async Task SetPublic(bool enabled, CancellationToken ct)
    {
        if (!(await cloudflare.GetStatusAsync(ct)).Configured)
            throw new InvalidOperationException("Connect Cloudflare before changing public access.");
        await store.Update(current =>
        {
            if (current.Job is not { State: "Active" } job || DomainActivationConfiguration.Read(store.Root)?.ProfileId != job.Id)
                throw new InvalidOperationException("Public access needs an active domain.");
            if (job.Phase != "Active") throw new InvalidOperationException("Wait for the current domain operation to finish.");
            return current with { Job = job with { PublicRequested = enabled == job.Public ? null : enabled, PublicError = null } };
        }, ct);
    }

    public bool WorkerReady()
    {
        var path = Path.Combine(store.Root, "activation-worker.json");
        DomainOnboardingStore.RejectLinks(path);
        if (!File.Exists(path)) return false;
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Activation worker status is oversized.");
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = json.RootElement;
        var checkedAt = root.GetProperty("checkedAt").GetDateTimeOffset();
        return root.GetProperty("schemaVersion").GetInt32() == 1 && root.GetProperty("ready").GetBoolean()
            && root.TryGetProperty("ingressReloadVersion", out var reload) && reload.TryGetInt32(out var version) && version == 1
            && checkedAt <= DateTimeOffset.UtcNow.AddSeconds(5) && checkedAt > DateTimeOffset.UtcNow.AddSeconds(-45);
    }

    public async Task<string> Terms(CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await http.GetStringAsync("https://acme-v02.api.letsencrypt.org/directory", ct));
        var terms = document.RootElement.GetProperty("meta").GetProperty("termsOfService").GetString();
        if (!Uri.TryCreate(terms, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host != "letsencrypt.org" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Let's Encrypt did not provide an expected subscriber agreement URL.");
        return terms!;
    }

    public async Task<DomainSetupPlan> Plan(DomainPlanRequest request, CancellationToken ct)
    {
        var previous = (await store.Read(ct)).Job;
        if (previous?.State is "Queued" or "Running" or "Activating" or "Active")
            throw new InvalidOperationException("A DNS setup is already running or active.");
        if (previous?.RecoveryRequired == true)
            throw new InvalidOperationException("Finish recovery of the previous DNS task before preparing another.");
        if (DomainActivationConfiguration.Read(store.Root) is not null)
            throw new InvalidOperationException("This lab already has an active domain. A later domain change requires a separately reviewed migration.");
        var connection = await dns.GetConnectionAsync(ct);
        if (!connection.Configured) throw new InvalidOperationException("Configure and verify AdGuard before starting DNS onboarding.");
        var health = await dns.GetHealthAsync(ct);
        if (!health.Running || !health.ProtectionEnabled || !health.RewritesEnabled)
            throw new InvalidOperationException("AdGuard DNS, protection, and rewrites must be enabled. Review those settings in AdGuard.");
        if (!IPAddress.TryParse(request.IngressAddress, out var address) || !DomainConnectivity.IsPrivate(address)
            || address.ToString() != request.IngressAddress)
            throw new ArgumentException("Enter the Spark's canonical private IPv4 address. Its existing HTTPS identity will be checked before review.");
        if (request.PropagationSeconds is < 10 or > 600) throw new ArgumentException("DNS propagation time must be between 10 and 600 seconds.");
        if (request.Email.Length > 254 || !MailAddress.TryCreate(request.Email, out var email) || email.Address != request.Email)
            throw new ArgumentException("Enter a valid ACME contact email address.");
        var account = await cloudflare.GetStatusAsync(ct);
        var zone = await cloudflare.GetZoneAsync(request.ZoneId, ct);
        var naming = DomainNames.Plan(new(zone.Name, request.Subdomain, request.SparkName, request.ServiceUrls), zone.Name);
        var recoveryNames = new[] { new Uri(authentication.PublicOrigin).Host, new Uri(authentication.Authority).Host };
        if (naming.LocalHostnames.Any(name => recoveryNames.Contains(name, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Choose new service hostnames distinct from the private recovery endpoints.");
        var review = await dnsPreflight.ReviewAsync(naming, zone, request.IngressAddress, await dns.ListRewritesAsync(ct), ct);
        var blockers = review.Blockers.ToList();
        if (!WorkerReady()) blockers.Add("The scoped activation service is not ready. Complete its Spark setup before starting.");
        if (!File.Exists("/opt/certbot/bin/certbot")) blockers.Add("The pinned Certbot integration is not installed in this host image.");
        if (!Path.IsPathFullyQualified(options.GatewayDirectory) || !Directory.Exists(options.GatewayDirectory))
            blockers.Add("The dedicated domain ingress directory is not mounted.");
        await DomainConnectivity.VerifyGateway(authentication.PublicOrigin, request.IngressAddress, "/health/live",
            authentication, false, value => value.GetProperty("status").GetString() == "ok", ct);
        var plan = new DomainSetupPlan(Guid.NewGuid(), "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30),
            zone.Id, account.AccountId ?? throw new InvalidOperationException("Connect a Cloudflare account first."),
            naming, request.IngressAddress, request.Email, request.PropagationSeconds, await Terms(ct),
            connection.BaseUrl!, connection.Username!, review.Rewrites, blockers.ToArray(),
            [
                "Cloudflare is used only for public DNS-01 challenge TXT records. No public A or AAAA records will be added.",
                "Both the staging test and production issuance create and remove real public DNS TXT records.",
                "Public certificates disclose their names through Certificate Transparency.",
                "Service names remain local through AdGuard. Existing private CA, LDAP, users, and API keys are preserved.",
                "Activation restarts the host and requires a fresh sign-in at the new URL. Existing JWTs with the old issuer must be replaced."
            ]);
        plan = plan with { ReviewHash = DomainOnboardingStore.Hash(plan) };
        await store.Update(current =>
        {
            if (current.Job?.State is "Queued" or "Running" or "Activating")
                throw new InvalidOperationException("A DNS setup started while this review was being prepared.");
            return current with { Plan = plan };
        }, ct);
        return plan;
    }

    public async Task<DomainSetupJob> Start(StartDomainSetupRequest request, string actor, CancellationToken ct)
    {
        if (!request.AcceptTerms || !request.AcceptDnsChanges)
            throw new ArgumentException("Explicitly accept the subscriber agreement and reviewed DNS changes before starting.");
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 512) throw new ArgumentException("A stable Owner identity is required.");
        await connections.Mutex.WaitAsync(ct);
        try
        {
          var previous = await store.Read(ct);
          if (previous.Job is { } accepted && accepted.Plan.Id == request.PlanId && accepted.Plan.ReviewHash == request.ReviewHash)
              return accepted;
          if (DomainActivationConfiguration.Read(store.Root) is not null)
              throw new InvalidOperationException("An active domain cannot be replaced by replaying onboarding.");
          var terms = await Terms(ct);
          if (!WorkerReady()) throw new InvalidOperationException("The activation service is unavailable.");
          return await store.AcceptReview(request, actor, terms, ct);
        }
        finally { connections.Mutex.Release(); }
    }

    public async Task Recover(Guid jobId, CancellationToken ct)
    {
        await connections.Mutex.WaitAsync(ct);
        try
        {
            if (DomainActivationConfiguration.Read(store.Root) is not null)
                throw new InvalidOperationException("A committed domain profile cannot use pre-activation cleanup.");
            await store.Update(current =>
            {
                if (current.Job is not { State: "Failed", RecoveryRequired: true } job || job.Id != jobId)
                    throw new InvalidOperationException("No failed DNS task is waiting for cleanup.");
                return current with { Job = job with { State = "Queued", Phase = "Recovering", Message = "Retrying ownership-checked cleanup of the previous attempt." } };
            }, ct);
        }
        finally { connections.Mutex.Release(); }
    }
}
