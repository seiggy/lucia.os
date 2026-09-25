using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Lucia.Homelab.Server.Onboarding;

public sealed record RecoverySshKey(string PublicKey, string Algorithm, string Fingerprint);

public static class RecoverySshKeys
{
    public static RecoverySshKey Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16384 || value.Any(c => char.IsControl(c) && c != '\t'))
            throw Invalid();
        var parts = value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] is not ("ssh-ed25519" or "ssh-rsa" or "ecdsa-sha2-nistp256"))
            throw Invalid();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(parts[1]); }
        catch (FormatException) { throw Invalid(); }
        var offset = 0;
        ReadOnlySpan<byte> Field()
        {
            if (offset + 4 > bytes.Length) throw Invalid();
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
            offset += 4;
            if (length > 8192 || length > bytes.Length - offset) throw Invalid();
            var result = bytes.AsSpan(offset, (int)length);
            offset += (int)length;
            return result;
        }
        if (Encoding.ASCII.GetString(Field()) != parts[0]) throw Invalid();
        if (parts[0] == "ssh-ed25519")
        {
            if (Field().Length != 32) throw Invalid();
        }
        else if (parts[0] == "ssh-rsa")
        {
            var exponent = Field().ToArray();
            var modulus = Field().ToArray();
            if (exponent.Length is < 1 or > 8 || modulus.Length is < 256 or > 1025
                || exponent[0] >= 128 || modulus[0] >= 128) throw Invalid();
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportParameters(new() { Exponent = exponent, Modulus = modulus });
                if (rsa.KeySize < 2048) throw Invalid();
            }
            catch (CryptographicException) { throw Invalid(); }
        }
        else
        {
            if (Encoding.ASCII.GetString(Field()) != "nistp256") throw Invalid();
            var point = Field();
            if (point.Length != 65 || point[0] != 4) throw Invalid();
            try
            {
                using var ec = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = point.Slice(1, 32).ToArray(), Y = point.Slice(33, 32).ToArray() } });
            }
            catch (CryptographicException) { throw Invalid(); }
        }
        if (offset != bytes.Length) throw Invalid();
        return new(parts[0] + " " + Convert.ToBase64String(bytes), parts[0],
            "SHA256:" + Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('='));
    }

    internal static HardwareOnboardingException Invalid() => new(400, "invalid_recovery_key",
        "Supply one OpenSSH public key (Ed25519, RSA 2048-bit or stronger, or ECDSA P-256), without key options or a private key.");
}

public sealed class GithubRecoveryKeys : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5)
    }) { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<object> Read(string username, CancellationToken ct)
    {
        try { return await ReadCore(username, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new HardwareOnboardingException(504, "github_keys_timeout", "GitHub's public-key lookup timed out. Try again or paste your public key."); }
        catch (HttpRequestException)
        { throw new HardwareOnboardingException(502, "github_keys_unavailable", "GitHub could not be reached securely. Try again or paste your public key."); }
        catch (DecoderFallbackException)
        { throw new HardwareOnboardingException(502, "github_keys_invalid", "GitHub did not return a readable public-key list. Paste your public key instead."); }
    }

    private async Task<object> ReadCore(string username, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        ct = deadline.Token;
        if (username.Length is < 1 or > 39 || username[0] == '-' || username[^1] == '-'
            || username.Contains("--", StringComparison.Ordinal) || username.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new HardwareOnboardingException(400, "invalid_github_username", "Enter a GitHub username, not a profile URL.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://github.com/" + username + ".keys");
        request.Headers.UserAgent.ParseAdd("Lucia/0.1");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new HardwareOnboardingException(404, "github_user_not_found", "That GitHub account could not be found.");
        if (!response.IsSuccessStatusCode)
            throw new HardwareOnboardingException(502, "github_keys_unavailable", "GitHub could not provide public keys. Try again later or paste a public key.");
        if (response.Content.Headers.ContentType?.MediaType != "text/plain")
            throw new HardwareOnboardingException(502, "github_keys_invalid", "GitHub did not return a public-key list. Paste a public key or try again later.");
        if (response.Content.Headers.ContentLength > 128 * 1024)
            throw new HardwareOnboardingException(502, "github_keys_too_large", "This account's public-key list is too large. Paste the specific public key instead.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var data = new byte[128 * 1024 + 1];
        var count = await stream.ReadAtLeastAsync(data, data.Length, throwOnEndOfStream: false, ct);
        if (count > 128 * 1024) throw new HardwareOnboardingException(502, "github_keys_too_large", "This public-key list is too large. Paste one key instead.");
        var lines = new UTF8Encoding(false, true).GetString(data, 0, count).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 100) throw new HardwareOnboardingException(502, "github_keys_too_large", "This account has too many keys to import. Paste one public key instead.");
        var keys = new List<RecoverySshKey>();
        var unsupported = 0;
        foreach (var line in lines)
        {
            try { keys.Add(RecoverySshKeys.Parse(line.TrimEnd('\r'))); }
            catch (HardwareOnboardingException error) when (error.Code == "invalid_recovery_key") { unsupported++; }
        }
        return new { username, keys = keys.DistinctBy(key => key.PublicKey).ToArray(), unsupportedCount = unsupported };
    }

    public void Dispose() => _http.Dispose();
}
