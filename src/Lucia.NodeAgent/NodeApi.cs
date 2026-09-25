using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Lucia.NodeAgent;

public sealed partial class DiscoveryClient
{
    private string DevicePath(DiscoveryCredentials credentials, string action, DateTimeOffset expiresAt)
    {
        ValidateCredentials(credentials);
        if (ValidateServer(credentials.Server) != Server || expiresAt <= DateTimeOffset.UtcNow
            || !credentials.Token.StartsWith("d1.", StringComparison.Ordinal))
            throw new NodeAgentException("The device authority is expired or belongs to another origin.");
        return $"/api/boot/devices/{credentials.DeviceId:D}/{action}";
    }

    internal async Task<InstallationConfiguration> InstallationAsync(DiscoveryCredentials credentials, CancellationToken token)
    {
        var path = DevicePath(credentials, "installation", credentials.ExpiresAt);
        return Deserialize<InstallationConfiguration>(await SendAsync(HttpMethod.Get, path, null, credentials.Token, token));
    }

    internal async Task<DiscoveryChallenge> DeviceChallengeAsync(DiscoveryCredentials credentials, DateTimeOffset deadline, CancellationToken token) =>
        ValidateChallenge(Deserialize<DiscoveryChallenge>(await SendAsync(HttpMethod.Get,
            DevicePath(credentials, "challenge", deadline), null, credentials.Token, token)));

    internal async Task<InstallationGrant> GrantAsync(DiscoveryCredentials credentials, GrantRequest request, CancellationToken token) =>
        Deserialize<InstallationGrant>(await SendAsync(HttpMethod.Post, DevicePath(credentials, "grant", credentials.ExpiresAt),
            JsonSerializer.SerializeToUtf8Bytes(request, AgentJson.Options), credentials.Token, token));

    internal async Task ProgressAsync(DiscoveryCredentials credentials, InstallationGrant grant, string phase, CancellationToken token)
    {
        var message = phase switch
        {
            "Installing" => "One-time grant verified; Debian installer may proceed.",
            "AwaitingEnrollment" => "Managed runtime staged; awaiting installed-system enrollment.",
            "Failed" => "The node operation failed closed; owner inspection is required.",
            _ => throw new NodeAgentException("Unsupported installation progress phase.")
        };
        var response = await SendReplyAsync(HttpMethod.Post, DevicePath(credentials, "progress", grant.ProgressExpiresAt),
            JsonSerializer.SerializeToUtf8Bytes(new { phase, message }, AgentJson.Options), credentials.Token, token);
        if (response.Status is not (HttpStatusCode.OK or HttpStatusCode.NoContent))
            throw new NodeAgentException("Installation progress was not synchronously acknowledged.");
    }

    internal async Task<ManagedConfiguration?> EnrollAsync(DiscoveryCredentials credentials, InstallPlan plan,
        InstallationGrant grant, string csr, ECDsa key, CancellationToken token)
    {
        var challenge = await DeviceChallengeAsync(credentials, grant.ProgressExpiresAt, token);
        var proof = SignReport(challenge, JsonSerializer.Serialize(new EnrollmentProof(plan.DeviceId, plan.TaskId, csr), AgentJson.Options), key);
        return await EnrollmentReplyAsync(DevicePath(credentials, "enroll", grant.ProgressExpiresAt),
            new EnrollmentRequest(plan.TaskId, proof), credentials.Token, token);
    }

    internal async Task<DiscoveryChallenge> NodeChallengeAsync(Guid id, CancellationToken token)
    {
        if (id == Guid.Empty) throw new NodeAgentException("A node identity is required.");
        return ValidateChallenge(Deserialize<DiscoveryChallenge>(await SendAsync(HttpMethod.Get,
            $"/api/nodes/{id:D}/challenge", null, null, token)));
    }

    internal async Task HeartbeatAsync(string certificate, NodeMetrics metrics, ECDsa key, CancellationToken token)
    {
        ValidateHeartbeatDates(certificate);
        var challenge = await NodeChallengeAsync(metrics.NodeId, token);
        ValidateHeartbeatDates(certificate);
        var proof = SignReport(challenge, JsonSerializer.Serialize(metrics, AgentJson.Options), key);
        var response = await SendReplyAsync(HttpMethod.Post, $"/api/nodes/{metrics.NodeId:D}/heartbeat",
            JsonSerializer.SerializeToUtf8Bytes(new MachineRequest(certificate, proof), AgentJson.Options), null, token);
        if (response.Status != HttpStatusCode.OK || !Deserialize<HeartbeatResult>(response.Bytes).Accepted)
            throw new NodeAgentException("The managed heartbeat was not accepted.");
    }

    internal async Task<ManagedConfiguration?> RenewAsync(string certificate, InstallPlan plan, string csr, ECDsa key, CancellationToken token)
    {
        var challenge = await NodeChallengeAsync(plan.DeviceId, token);
        var proof = SignReport(challenge, JsonSerializer.Serialize(new EnrollmentProof(plan.DeviceId, plan.TaskId, csr), AgentJson.Options), key);
        return await EnrollmentReplyAsync($"/api/nodes/{plan.DeviceId:D}/renew", new MachineRequest(certificate, proof), null, token);
    }

    private async Task<ManagedConfiguration?> EnrollmentReplyAsync<T>(string path, T request, string? bearer, CancellationToken token)
    {
        var response = await SendReplyAsync(HttpMethod.Post, path, JsonSerializer.SerializeToUtf8Bytes(request, AgentJson.Options), bearer, token);
        if (response.Status == HttpStatusCode.Accepted && Deserialize<PendingResult>(response.Bytes).State == "Pending") return null;
        if (response.Status != HttpStatusCode.OK)
            throw new NodeAgentException("Enrollment returned an unsupported response state.");
        return Deserialize<ManagedConfiguration>(response.Bytes);
    }

    private static DiscoveryChallenge ValidateChallenge(DiscoveryChallenge value)
    {
        if (!BoundedVisible(value.ChallengeId, 128) || !BoundedVisible(value.Nonce, 512) || value.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new NodeAgentException("The server returned an invalid or expired identity challenge.");
        return value;
    }

    private sealed record HeartbeatResult(bool Accepted);
    private sealed record PendingResult(string State);

    private static void ValidateHeartbeatDates(string pem)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(pem);
            if (certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                throw new NodeAgentException("Heartbeat requires a currently valid node certificate; recovery renewal must complete first.");
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        { throw new NodeAgentException("Heartbeat requires a valid node certificate."); }
    }
}
