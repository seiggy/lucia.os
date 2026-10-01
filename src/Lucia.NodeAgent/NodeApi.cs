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

    internal async Task<IReadOnlyDictionary<string, string[]>?> HeartbeatAsync(string certificate, NodeMetrics metrics, ECDsa key, CancellationToken token)
    {
        ValidateHeartbeatDates(certificate);
        var challenge = await NodeChallengeAsync(metrics.NodeId, token);
        ValidateHeartbeatDates(certificate);
        var proof = SignReport(challenge, JsonSerializer.Serialize(metrics, AgentJson.Options), key);
        var response = await SendReplyAsync(HttpMethod.Post, $"/api/nodes/{metrics.NodeId:D}/heartbeat",
            JsonSerializer.SerializeToUtf8Bytes(new MachineRequest(certificate, proof), AgentJson.Options), null, token);
        var result = response.Status == HttpStatusCode.OK ? Deserialize<HeartbeatResult>(response.Bytes) : null;
        if (result is not { Accepted: true })
            throw new NodeAgentException("The managed heartbeat was not accepted.");
        return result.SshKeys;
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

    internal async Task<DesiredStacks> StacksAsync(Guid node, string certificate, NodeStackReport report, ECDsa key,
        CancellationToken token) =>
        Deserialize<DesiredStacks>(await BoundAsync(node, certificate, "stacks", $"/api/nodes/{node:D}/stacks", report, key,
            TimeSpan.FromSeconds(30), 4 * 1024 * 1024, token));

    // Exec requests carry scripts of up to 16K characters, which JSON escaping can grow several times over.
    internal async Task<NodeRequest[]> RequestsAsync(Guid node, string certificate, ECDsa key, CancellationToken token) =>
        Deserialize<PendingRequests>(await BoundAsync(node, certificate, "requests", $"/api/nodes/{node:D}/requests", new { }, key,
            TimeSpan.FromSeconds(40), 1024 * 1024, token)).Requests ?? [];

    internal async Task AnswerAsync(Guid node, string certificate, NodeRequestResult result, ECDsa key, CancellationToken token) =>
        await BoundAsync(node, certificate, "request-result", $"/api/nodes/{node:D}/requests/{result.RequestId:D}", result, key,
            TimeSpan.FromSeconds(30), ResponseLimit, token);

    /// <summary>
    /// Signs a small proof that binds a larger body by its SHA-256, so the body isn't held to the 32 KiB report limit.
    /// </summary>
    private async Task<byte[]> BoundAsync<T>(Guid node, string certificate, string purpose, string path, T body, ECDsa key,
        TimeSpan timeout, int limit, CancellationToken token)
    {
        ValidateHeartbeatDates(certificate);
        var challenge = await NodeChallengeAsync(node, token);
        var json = JsonSerializer.Serialize(body, AgentJson.Options);
        var hash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
        var proof = SignReport(challenge, JsonSerializer.Serialize(new { nodeId = node, purpose, bodySha256 = hash }, AgentJson.Options), key);
        var response = await SendReplyAsync(HttpMethod.Post, path,
            JsonSerializer.SerializeToUtf8Bytes(new { certificatePem = certificate, proof, body = json }, AgentJson.Options), null, token,
            timeout, limit);
        if (response.Status != HttpStatusCode.OK) throw new NodeAgentException($"The server answered {purpose} with HTTP {(int)response.Status}.");
        return response.Bytes;
    }

    /// <summary>Waits for the node a stack is leaving and hands its archive to <paramref name="consume"/> as it streams in.</summary>
    internal async Task ReceiveTransferAsync(Guid node, string certificate, Guid move, ECDsa key,
        Func<Stream, CancellationToken, Task> consume, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/nodes/{node:D}/transfers/{move:D}");
        request.Headers.Add("X-Lucia-Node-Proof", await TransferProofAsync(node, certificate, "transfer-receive", move, key, token));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TransferLimit);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new NodeAgentException($"The server answered the transfer with HTTP {(int)response.StatusCode}.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        await consume(stream, timeout.Token);
    }

    /// <summary>Uploads a stack's archive, written by <paramref name="produce"/>, for the node it's moving to.</summary>
    internal async Task SendTransferAsync(Guid node, string certificate, Guid move, ECDsa key, long total,
        Func<Stream, CancellationToken, Task> produce, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/nodes/{node:D}/transfers/{move:D}")
        { Content = new StreamingContent(produce) };
        request.Content.Headers.ContentType = new("application/x-tar");
        request.Headers.Add("X-Lucia-Node-Proof", await TransferProofAsync(node, certificate, "transfer-send", move, key, token));
        request.Headers.Add("X-Lucia-Transfer-Bytes", total.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TransferLimit);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new NodeAgentException(response.StatusCode == HttpStatusCode.Conflict
                ? "The receiving node didn't connect in time."
                : $"The server answered the transfer with HTTP {(int)response.StatusCode}.");
    }

    // Moves run as long as the data takes; this only stops a connection that has silently died from holding the stack forever.
    private static readonly TimeSpan TransferLimit = TimeSpan.FromHours(24);

    /// <summary>Downloads the agent the server ships and hands the tar and its release ID to <paramref name="consume"/>.</summary>
    internal async Task DownloadAgentAsync(Guid node, string certificate, ECDsa key,
        Func<Stream, string, CancellationToken, Task> consume, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/nodes/{node:D}/agent");
        request.Headers.Add("X-Lucia-Node-Proof", await ProofHeaderAsync(node, certificate, "agent-download", "agent", key, token));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new NodeAgentException($"The server answered the agent download with HTTP {(int)response.StatusCode}.");
        var release = response.Headers.TryGetValues("X-Lucia-Agent-Release", out var values) ? values.SingleOrDefault() : null;
        if (release is null || !AgentRelease.IsId(release)) throw new NodeAgentException("The server didn't name the agent release it sent.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        await consume(stream, release, timeout.Token);
    }

    private Task<string> TransferProofAsync(Guid node, string certificate, string purpose, Guid move, ECDsa key, CancellationToken token) =>
        ProofHeaderAsync(node, certificate, purpose, "move:" + move.ToString("D"), key, token);

    private async Task<string> ProofHeaderAsync(Guid node, string certificate, string purpose, string body, ECDsa key, CancellationToken token)
    {
        ValidateHeartbeatDates(certificate);
        var challenge = await NodeChallengeAsync(node, token);
        var hash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body)));
        var proof = SignReport(challenge, JsonSerializer.Serialize(new { nodeId = node, purpose, bodySha256 = hash }, AgentJson.Options), key);
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { certificatePem = certificate, proof, body }, AgentJson.Options));
    }

    private sealed class StreamingContent(Func<Stream, CancellationToken, Task> produce) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => produce(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => produce(stream, token);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    internal sealed record DesiredStacks(NodeDesiredStack[]? Stacks, NodeMount[]? Mounts = null, NodeBackupRepository? Backup = null,
        NodeRegistry[]? Registries = null);
    private sealed record PendingRequests(NodeRequest[]? Requests);
    private sealed record HeartbeatResult(bool Accepted, Dictionary<string, string[]>? SshKeys = null);
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
