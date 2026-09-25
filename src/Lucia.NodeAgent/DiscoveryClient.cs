using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Lucia.NodeAgent;

public sealed partial class DiscoveryClient : IDisposable
{
    private readonly HttpClient client;
    public Uri Server { get; }
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private const int ResponseLimit = 64 * 1024;

    public DiscoveryClient(string server, X509Certificate2 authority, IPAddress? connectAddress = null)
        : this(ValidateServer(server), CreateHandler(authority, connectAddress)) { }

    internal DiscoveryClient(Uri server, HttpMessageHandler handler)
    {
        Server = ValidateServer(server.AbsoluteUri);
        client = new(handler) { BaseAddress = Server, Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Accept.Add(new("application/json"));
    }

    public static Uri ValidateServer(string value)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.IsWellFormedOriginalString() || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/"
            || value.Any(char.IsControl) || value.Contains('\\'))
            throw new NodeAgentException("--server must be an HTTPS origin without user info, path, query or fragment.");
        return uri;
    }

    public static X509Certificate2 LoadAuthority(string path)
    {
        try
        {
            var pem = Encoding.UTF8.GetString(SecureStateDirectory.ReadPublicCa(path));
            return ParseAuthority(pem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            throw new NodeAgentException("Cannot load the public PEM CA. Check the supplied CA file and its validity.");
        }
    }

    internal static X509Certificate2 ParseAuthority(string pem)
    {
        if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal)
            || pem.Split("-----BEGIN CERTIFICATE-----", StringSplitOptions.None).Length != 2)
            throw new NodeAgentException("The CA file must contain exactly one public CA certificate and no private key.");
        X509Certificate2 certificate;
        try { certificate = X509Certificate2.CreateFromPem(pem); }
        catch (CryptographicException) { throw new NodeAgentException("The supplied public CA certificate is malformed."); }
        if (!certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(value => value.CertificateAuthority)
            || certificate.Extensions.OfType<X509KeyUsageExtension>().Any(value => !value.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign))
            || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
        {
            certificate.Dispose();
            throw new NodeAgentException("The supplied certificate must be a currently valid public certificate authority.");
        }
        return certificate;
    }

    internal static SocketsHttpHandler CreateHandler(X509Certificate2 authority, IPAddress? connectAddress)
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            VerificationFlags = X509VerificationFlags.NoFlag,
            // Same private-CA policy as the host: no CRL/OCSP service is provisioned.
            // Certificate signature, chain, hostname, server EKU and validity are still mandatory.
            RevocationMode = X509RevocationMode.NoCheck,
            DisableCertificateDownloads = true
        };
        policy.CustomTrustStore.Add(authority);
        policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxResponseHeadersLength = 16,
            MaxConnectionsPerServer = 2,
            SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = policy }
        };
        if (connectAddress is not null)
        {
            handler.ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(connectAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(connectAddress, context.DnsEndPoint.Port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            };
        }
        return handler;
    }

    public async Task<DiscoveryChallenge> GetChallengeAsync(CancellationToken cancellationToken)
    {
        var result = Deserialize<DiscoveryChallenge>(await SendAsync(HttpMethod.Get, "/api/boot/challenge", null, null, cancellationToken));
        if (!BoundedVisible(result.ChallengeId, 128) || !BoundedVisible(result.Nonce, 512)
            || result.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new NodeAgentException("The server returned an invalid or expired discovery challenge.");
        return result;
    }

    public static DiscoveryRequest SignReport(DiscoveryChallenge challenge, string reportJson, ECDsa key)
    {
        AgentJson.ValidateReportSize(reportJson);
        if (!BoundedVisible(challenge.ChallengeId, 128) || !BoundedVisible(challenge.Nonce, 512)
            || challenge.ExpiresAt <= DateTimeOffset.UtcNow
            || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            throw new NodeAgentException("Cannot sign an invalid or expired challenge or non-P256 identity.");
        var message = Encoding.UTF8.GetBytes("lucia-discovery-v1\n" + challenge.ChallengeId + "\n" + challenge.Nonce + "\n" + reportJson);
        var signature = key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new(challenge.ChallengeId, key.ExportSubjectPublicKeyInfoPem(), reportJson, Convert.ToBase64String(signature));
    }

    public async Task<DiscoveryRegistration> RegisterAsync(string reportJson, ECDsa key, CancellationToken cancellationToken)
    {
        AgentJson.ValidateReportSize(reportJson);
        var challenge = await GetChallengeAsync(cancellationToken);
        var request = SignReport(challenge, reportJson, key);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, AgentJson.Options);
        var result = Deserialize<DiscoveryRegistration>(await SendAsync(HttpMethod.Post, "/api/boot/discover", bytes, null, cancellationToken));
        ValidateCredentials(new(Server.AbsoluteUri, result.DeviceId, result.Token, result.ExpiresAt, result.VerificationCode));
        if (result.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new NodeAgentException("The server returned an expired discovery session. Check the system clock and server configuration.");
        return result;
    }

    // A bounded read only. The CLI emits only an allowlisted status projection, never server-supplied commands or capabilities.
    public async Task<JsonDocument> GetStatusAsync(DiscoveryCredentials credentials, CancellationToken cancellationToken)
    {
        ValidateCredentials(credentials);
        if (ValidateServer(credentials.Server) != Server)
            throw new NodeAgentException("Stored credentials belong to a different HTTPS origin. Use the original server or a separate state directory.");
        if (credentials.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new NodeAgentException("The local discovery session has expired. Run discover again to request owner review.");
        var bytes = await SendAsync(HttpMethod.Get, "/api/boot/devices/" + credentials.DeviceId.ToString("D"),
            null, credentials.Token, cancellationToken);
        try { return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { throw new NodeAgentException("The server returned malformed discovery status JSON."); }
    }

    public static void ValidateCredentials(DiscoveryCredentials value)
    {
        if (value.Server is null) throw new NodeAgentException("The discovery server origin is missing.");
        ValidateServer(value.Server);
        if (value.DeviceId == Guid.Empty || !BoundedVisible(value.Token, 4096)
            || value.ExpiresAt == default || string.IsNullOrEmpty(value.VerificationCode) || value.VerificationCode.Length > 32
            || value.VerificationCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new NodeAgentException("The server returned invalid discovery credentials or verification code.");
    }

    private static bool BoundedVisible(string? value, int max) =>
        !string.IsNullOrEmpty(value) && value.Length <= max && value.All(c => c is >= '!' and <= '~');

    private async Task<byte[]> SendAsync(HttpMethod method, string path, byte[]? body, string? token, CancellationToken cancellationToken) =>
        (await SendReplyAsync(method, path, body, token, cancellationToken)).Bytes;

    private async Task<(HttpStatusCode Status, byte[] Bytes)> SendReplyAsync(HttpMethod method, string path, byte[]? body,
        string? token, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new("application/json");
            }
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (method == HttpMethod.Get && attempt < 2 && Transient(response.StatusCode))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new NodeAgentException(response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized => "Discovery authentication failed (401). The session may have expired; run discover again.",
                        HttpStatusCode.Forbidden => "Discovery access was denied (403). Ask the owner to check discovery policy.",
                        HttpStatusCode.Conflict => "Discovery conflicted with current server state (409). Ask the owner to inspect this device.",
                        _ when (int)response.StatusCode is >= 300 and < 400 => "Discovery redirects are refused. Supply the final trusted HTTPS origin.",
                        _ => $"Discovery server returned HTTP {(int)response.StatusCode}. Check server readiness and logs."
                    });
                if (response.Content.Headers.ContentLength > ResponseLimit)
                    throw new NodeAgentException("The discovery response exceeds the 64 KiB limit.");
                using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var output = new MemoryStream();
                var buffer = new byte[4096];
                int count;
                while ((count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, ResponseLimit + 1 - (int)output.Length)),
                           timeout.Token)) > 0)
                {
                    output.Write(buffer, 0, count);
                    if (output.Length > ResponseLimit) throw new NodeAgentException("The discovery response exceeds the 64 KiB limit.");
                }
                return (response.StatusCode, output.ToArray());
            }
            catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
            {
                throw new NodeAgentException("TLS validation failed. Check the public CA, HTTPS hostname, server certificate and system clock.");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (method == HttpMethod.Get && attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
                    continue;
                }
                throw new NodeAgentException(method == HttpMethod.Post
                    ? "The authenticated submission did not complete reliably. It was not retried. The owner must inspect server state before another installation attempt."
                    : "Discovery request timed out or failed after bounded retries. Check connectivity, CA, hostname and system clock.");
            }
        }
    }

    private static T Deserialize<T>(byte[] bytes)
    {
        try { return JsonSerializer.Deserialize<T>(bytes, AgentJson.Options) ?? throw new JsonException(); }
        catch (JsonException) { throw new NodeAgentException("The server returned malformed discovery JSON."); }
    }

    private static bool Transient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    public void Dispose() => client.Dispose();
}
