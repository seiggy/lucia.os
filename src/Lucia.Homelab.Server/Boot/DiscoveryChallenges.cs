using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Lucia.Homelab.Server.Boot;

public sealed record DiscoveryChallenge(string ChallengeId, string Nonce, DateTimeOffset ExpiresAt);
public sealed record SignedDiscovery(string ChallengeId, string PublicKeyPem, string ReportJson, string Signature);
public sealed record VerifiedDiscovery(string PublicKeyFingerprint, string ReportJson);

public sealed class DiscoveryProtocolException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class DiscoveryChallenges(TimeProvider clock)
{
    private const int Capacity = 128;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly object _issuance = new();
    private sealed record Pending(DiscoveryChallenge Challenge, IPAddress Address);

    public DiscoveryChallenge Issue(IPAddress address)
    {
        lock (_issuance)
        {
            foreach (var item in _pending)
                if (item.Value.Challenge.ExpiresAt <= clock.GetUtcNow())
                    _pending.TryRemove(item.Key, out _);
            if (_pending.Count >= Capacity)
                throw new DiscoveryProtocolException(429, "Too many pending discovery requests. Wait two minutes and retry.");
            var challenge = new DiscoveryChallenge(Guid.NewGuid().ToString("D"),
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), clock.GetUtcNow() + Lifetime);
            if (!_pending.TryAdd(challenge.ChallengeId, new(challenge, address.MapToIPv6())))
                throw new InvalidOperationException("Could not allocate a unique discovery challenge.");
            return challenge;
        }
    }

    public VerifiedDiscovery Verify(SignedDiscovery request, IPAddress address)
    {
        if (request is null || request.ChallengeId is null || request.PublicKeyPem is null
            || request.ReportJson is null || request.Signature is null
            || request.ChallengeId.Length != 36 || request.PublicKeyPem.Length > 1024
            || Encoding.UTF8.GetByteCount(request.ReportJson) is < 2 or > 131072
            || request.Signature.Length != 88)
            throw new DiscoveryProtocolException(400, "The discovery request is incomplete or exceeds its size limits.");
        if (!_pending.TryGetValue(request.ChallengeId, out var pending)
            || pending.Challenge.ExpiresAt <= clock.GetUtcNow()
            || !pending.Address.Equals(address.MapToIPv6()))
            throw new DiscoveryProtocolException(401, "The discovery challenge expired or does not belong to this connection.");

        byte[] publicKey;
        try
        {
            if (!request.PublicKeyPem.Trim().StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal)
                || !request.PublicKeyPem.Trim().EndsWith("-----END PUBLIC KEY-----", StringComparison.Ordinal)
                || request.PublicKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
                throw new CryptographicException();
            using var key = ECDsa.Create();
            key.ImportFromPem(request.PublicKeyPem);
            if (key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new CryptographicException();
            var signature = Convert.FromBase64String(request.Signature);
            var payload = Encoding.UTF8.GetBytes(
                $"lucia-discovery-v1\n{pending.Challenge.ChallengeId}\n{pending.Challenge.Nonce}\n{request.ReportJson}");
            if (signature.Length != 64 || !key.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new CryptographicException();
            publicKey = key.ExportSubjectPublicKeyInfo();
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or FormatException)
        {
            throw new DiscoveryProtocolException(401, "The discovery request did not prove possession of its device key.");
        }

        if (!_pending.TryRemove(request.ChallengeId, out _))
            throw new DiscoveryProtocolException(401, "The discovery challenge has already been used.");
        return new(Convert.ToHexStringLower(SHA256.HashData(publicKey)), request.ReportJson);
    }
}
