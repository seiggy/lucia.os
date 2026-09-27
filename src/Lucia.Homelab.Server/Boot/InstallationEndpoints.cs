using System.Text.Json;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;

namespace Lucia.Homelab.Server.Boot;

public sealed record InstallationGrantRequest(Guid RequestId, long InventoryRevision, string DiskId, SignedDiscovery Proof);
public sealed record InstallationProgressRequest(HardwareDevicePhase Phase, string? Message);

public static class InstallationEndpoints
{
    public static void MapManagedInstallation(this WebApplication app)
    {
        var boot = app.MapGroup("/api/boot/devices/{id:guid}").AllowAnonymous().AddEndpointFilter<BootProtocolErrorFilter>();
        boot.MapGet("/installation", async (Guid id, HttpContext context, BootOptions options, HardwareOnboardingStore store, CancellationToken ct) =>
        {
            HardwareBootExtensions.RequireNetwork(context, options);
            return Results.Json(await store.GetSessionConfigurationAsync(DiscoveryCapability.Read(id, context.Request), ct), HardwareOnboardingJson.Options);
        });
        boot.MapGet("/challenge", async (Guid id, HttpContext context, BootOptions options, HardwareOnboardingStore store,
            DiscoveryChallenges challenges, CancellationToken ct) =>
        {
            var address = HardwareBootExtensions.RequireNetwork(context, options);
            await store.GetSessionStatusAsync(DiscoveryCapability.Read(id, context.Request), ct);
            return Results.Json(challenges.Issue(address));
        });
        boot.MapPost("/grant", async (Guid id, HttpContext context, BootOptions options, HardwareOnboardingStore store,
            DiscoveryChallenges challenges, CancellationToken ct) =>
        {
            var address = HardwareBootExtensions.RequireNetwork(context, options);
            var session = DiscoveryCapability.Read(id, context.Request);
            await store.GetSessionStatusAsync(session, ct);
            var input = await HardwareBootExtensions.ReadRequest<InstallationGrantRequest>(context, ct);
            var proof = challenges.Verify(input.Proof, address);
            if (!proof.PublicKeyFingerprint.Equals(session.SessionKeyFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new DiscoveryProtocolException(403, "Inventory proof must use the approved device's discovery key.");
            var hardware = JsonSerializer.Deserialize<HardwareReport>(proof.ReportJson, HardwareOnboardingJson.Options)
                ?? throw new DiscoveryProtocolException(400, "A fresh hardware inventory is required.");
            await store.RegisterDiscoveryAsync(proof.PublicKeyFingerprint, hardware, false, ct);
            return Results.Json(await store.RequestInstallationGrantAsync(session, input.RequestId, input.DiskId, input.InventoryRevision, ct),
                HardwareOnboardingJson.Options);
        });
        boot.MapPost("/progress", async (Guid id, HttpContext context, BootOptions options, HardwareOnboardingStore store, CancellationToken ct) =>
        {
            HardwareBootExtensions.RequireNetwork(context, options);
            var input = await HardwareBootExtensions.ReadRequest<InstallationProgressRequest>(context, ct);
            await store.ReportStatusAsync(DiscoveryCapability.Read(id, context.Request), input.Phase, input.Message, ct);
            return Results.Ok(new { accepted = true });
        });
        boot.MapPost("/enroll", async (Guid id, HttpContext context, BootOptions options, HardwareOnboardingStore store,
            DiscoveryChallenges challenges, ManagedNodeEnrollment enrollment, CancellationToken ct) =>
        {
            var address = HardwareBootExtensions.RequireNetwork(context, options);
            var session = DiscoveryCapability.Read(id, context.Request);
            await store.GetSessionStatusAsync(session, ct);
            var input = await HardwareBootExtensions.ReadRequest<NodeEnrollmentSubmission>(context, ct);
            var proof = challenges.Verify(input.Proof, address);
            var payload = JsonSerializer.Deserialize<NodeEnrollmentPayload>(proof.ReportJson, HardwareOnboardingJson.Options)
                ?? throw new DiscoveryProtocolException(400, "A signed enrollment request is required.");
            if (input.TaskId != payload.TaskId) throw new DiscoveryProtocolException(403, "The enrollment task does not match.");
            var configuration = await enrollment.Enroll(session, payload, proof.PublicKeyFingerprint, ct);
            return configuration is null ? Results.Json(new { state = "Pending" }, statusCode: 202) : Results.Json(configuration);
        });

        var nodes = app.MapGroup("/api/nodes/{id:guid}").AllowAnonymous().AddEndpointFilter<BootProtocolErrorFilter>();
        nodes.MapGet("/challenge", async (Guid id, HttpContext context, BootOptions options, ManagedNodeEnrollment enrollment,
            DiscoveryChallenges challenges, CancellationToken ct) =>
        {
            var address = HardwareBootExtensions.RequireNetwork(context, options);
            await enrollment.RequireKnown(id, ct);
            return Results.Json(challenges.Issue(address));
        });
        nodes.MapPost("/heartbeat", async (Guid id, HttpContext context, BootOptions options, ManagedNodeEnrollment enrollment,
            DiscoveryChallenges challenges, OwnerSshKeys sshKeys, CancellationToken ct) =>
        {
            var address = HardwareBootExtensions.RequireNetwork(context, options);
            var input = await HardwareBootExtensions.ReadRequest<NodeSignedSubmission>(context, ct);
            var proof = challenges.Verify(input.Proof, address);
            var report = JsonSerializer.Deserialize<NodeHeartbeat>(proof.ReportJson, HardwareOnboardingJson.Options)
                ?? throw new DiscoveryProtocolException(400, "A signed node report is required.");
            await enrollment.Heartbeat(id, report, input.CertificatePem, proof.PublicKeyFingerprint, address, ct);
            return Results.Ok(new { accepted = true, sshKeys = await sshKeys.Authorized(ct) });
        });
        nodes.MapPost("/renew", async (Guid id, HttpContext context, BootOptions options, ManagedNodeEnrollment enrollment,
            DiscoveryChallenges challenges, CancellationToken ct) =>
        {
            var address = HardwareBootExtensions.RequireNetwork(context, options);
            var input = await HardwareBootExtensions.ReadRequest<NodeSignedSubmission>(context, ct);
            var proof = challenges.Verify(input.Proof, address);
            var payload = JsonSerializer.Deserialize<NodeEnrollmentPayload>(proof.ReportJson, HardwareOnboardingJson.Options)
                ?? throw new DiscoveryProtocolException(400, "A signed renewal request is required.");
            var configuration = await enrollment.Renew(id, payload, input.CertificatePem, proof.PublicKeyFingerprint, ct);
            return configuration is null ? Results.Json(new { state = "Pending" }, statusCode: 202) : Results.Json(configuration);
        });
        app.MapGet("/api/host/nodes", (ManagedNodeEnrollment enrollment, CancellationToken ct) => enrollment.Snapshot(ct))
            .RequireAuthorization("HostOwner").AddEndpointFilter<HardwareOnboardingErrorFilter>();
    }
}
