using System.Security.Cryptography;
using System.Text;

namespace Lucia.NodeAgent;

internal sealed class InstallationRunner(DiscoveryClient client, SecureStateDirectory state, HardwareInspector inspector, DiskSafety disks)
{
    internal async Task WaitAsync(CancellationToken token)
    {
        var credentials = state.LoadCredentials();
        var registered = state.ReadJson<HardwareReport>("inventory.json")!;
        Console.Error.WriteLine($"Physical verification code: {credentials.VerificationCode}");
        Console.Error.WriteLine("Awaiting owner approval. No disk has been authorized for installation.");
        while (credentials.ExpiresAt > DateTimeOffset.UtcNow)
        {
            token.ThrowIfCancellationRequested();
            var configuration = await client.InstallationAsync(credentials, token);
            if (configuration.DeviceId != credentials.DeviceId || configuration.InventoryRevision < 1)
                throw new NodeAgentException("The installation configuration is not for the registered device.");
            if (configuration.CanRequestInstallationGrant)
            {
                var fresh = inspector.Inspect();
                if (AgentJson.SerializeStable(fresh) != AgentJson.SerializeStable(registered))
                    throw new NodeAgentException("Hardware inventory changed since discovery. Owner review must restart.");
                var plan = InstallationRules.Approve(configuration, credentials, fresh, disks.BootId(),
                    disks.VerifyUnused(fresh, InstallationRules.DiskId(configuration.DiskId)), DateTimeOffset.UtcNow);
                var existing = state.ReadJson<InstallPlan>("install-plan.json", optional: true);
                if (existing is not null && Serialize(existing) != Serialize(plan))
                    throw new NodeAgentException("A different installation plan already exists. Owner inspection is required.");
                state.WriteJson("install-plan.json", plan);
                using var runtime = new SecureStateDirectory("/run/lucia");
                runtime.WritePrivate("install.cfg", Encoding.UTF8.GetBytes(InstallationRules.Preseed(plan)));
                Console.Error.WriteLine("Approved installation plan staged. Partitioning still requires a fresh one-time server grant.");
                return;
            }
            if (configuration.AuthorityExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
                throw new NodeAgentException("Installation approval expired while waiting.");
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
        throw new NodeAgentException("The 30-minute discovery authority expired while awaiting owner approval.");
    }

    internal async Task GuardAsync(CancellationToken token)
    {
        var credentials = state.LoadCredentials();
        var plan = LoadPlan();
        var now = DateTimeOffset.UtcNow;
        if (credentials.DeviceId != plan.DeviceId || credentials.ExpiresAt <= now || plan.AuthorityExpiresAt <= now
            || plan.AuthorityExpiresAt > credentials.ExpiresAt || plan.BootId != disks.BootId())
            throw new NodeAgentException("The current boot no longer holds fresh installation authority.");
        VerifyFresh(plan);
        var grant = state.ReadJson<InstallationGrant>("grant.json", optional: true);
        var attempt = state.ReadJson<GrantAttempt>("grant-attempt.json", optional: true);
        if (grant is null)
        {
            if (attempt is not null)
                throw new NodeAgentException("A previous grant submission has an uncertain outcome. It will not be retried; owner inspection is required.");
            attempt = new(Guid.NewGuid());
            state.WriteJson("grant-attempt.json", attempt);
            using var key = state.LoadExistingKey();
            var challenge = await client.DeviceChallengeAsync(credentials, credentials.ExpiresAt, token);
            var fresh = VerifyFresh(plan);
            var proof = DiscoveryClient.SignReport(challenge, AgentJson.SerializeReport(fresh), key);
            grant = await client.GrantAsync(credentials, new(attempt.RequestId, plan.InventoryRevision, plan.DiskId, proof), token);
            InstallationRules.ValidateGrant(grant, plan, attempt.RequestId, DateTimeOffset.UtcNow, forErase: true);
            state.WriteJson("grant.json", grant);
        }
        if (attempt is null) throw new NodeAgentException("The persisted grant attempt is missing.");
        InstallationRules.ValidateGrant(grant, plan, attempt.RequestId, DateTimeOffset.UtcNow, forErase: true);
        VerifyFresh(plan);
        await client.ProgressAsync(credentials, grant, "Installing", token);
        // The acknowledgment is durable before returning success to partman's guard.
        state.WriteJson("installation-started.json", new GrantAttempt(grant.RequestId));
        VerifyFresh(plan);
        InstallationRules.ValidateGrant(grant, plan, attempt.RequestId, DateTimeOffset.UtcNow, forErase: true);
        Console.Error.WriteLine("Fresh one-time installation grant and exact unused disk verified.");
    }

    internal async Task StageAsync(string caFile, CancellationToken token)
    {
        var plan = LoadPlan();
        var credentials = state.LoadCredentials();
        var grant = state.ReadJson<InstallationGrant>("grant.json")!;
        var attempt = state.ReadJson<GrantAttempt>("grant-attempt.json")!;
        var started = state.ReadJson<GrantAttempt>("installation-started.json")!;
        InstallationRules.ValidateGrant(grant, plan, attempt.RequestId, DateTimeOffset.UtcNow, forErase: false);
        if (started.RequestId != grant.RequestId || credentials.DeviceId != plan.DeviceId
            || DiscoveryClient.ValidateServer(credentials.Server) != client.Server || plan.BootId != disks.BootId()
            || grant.ProgressExpiresAt <= DateTimeOffset.UtcNow)
            throw new NodeAgentException("The current boot does not hold an acknowledged installation grant.");
        var fresh = inspector.Inspect();
        disks.VerifyInstalledMount(fresh, plan, "/target");
        await ManagedFiles.StageAsync(state, plan, client.Server, caFile, token);
        disks.VerifyInstalledMount(inspector.Inspect(), plan, "/target");
        await client.ProgressAsync(credentials, grant, "AwaitingEnrollment", token);
        Console.Error.WriteLine("Managed runtime and recovery access staged. Enrollment and authenticated heartbeat remain pending.");
    }

    private InstallPlan LoadPlan()
    {
        var plan = state.ReadJson<InstallPlan>("install-plan.json")!;
        InstallationRules.ValidatePlan(plan);
        return plan;
    }

    internal async Task ReportFailureAsync(CancellationToken token)
    {
        try
        {
            var grant = state.ReadJson<InstallationGrant>("grant.json", optional: true);
            if (grant is null || grant.ProgressExpiresAt <= DateTimeOffset.UtcNow) return;
            var plan = LoadPlan();
            var credentials = state.LoadCredentials();
            InstallationRules.ValidateGrant(grant, plan, grant.RequestId, DateTimeOffset.UtcNow, forErase: false);
            if (credentials.DeviceId != plan.DeviceId) return;
            await client.ProgressAsync(credentials, grant, "Failed", token);
        }
        catch (Exception) { /* A reporting failure never converts the original failure into permission to continue. */ }
    }

    private HardwareReport VerifyFresh(InstallPlan plan)
    {
        var fresh = inspector.Inspect();
        InstallationRules.SameDisk(plan, fresh);
        if (AgentJson.SerializeStable(fresh) != AgentJson.SerializeStable(plan.Inventory)
            || disks.VerifyUnused(fresh, plan.DiskId) != plan.DeviceNumber)
            throw new NodeAgentException("The current hardware no longer exactly matches the approved inventory.");
        return fresh;
    }

    private static string Serialize(InstallPlan plan) =>
        System.Text.Json.JsonSerializer.Serialize(plan, AgentJson.Options);

    private sealed record GrantAttempt(Guid RequestId);
}
