using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Host;

public sealed class HostAuthenticationOptions
{
    public bool Enabled { get; set; }
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecretFile { get; set; } = "";
    public string PublicOrigin { get; set; } = "";
    public string[] AdditionalPublicOrigins { get; set; } = [];
    public bool AuthorityUsesSystemTrust { get; set; }
    public string CaCertificatePath { get; set; } = "";
    public string DataProtectionKeysDirectory { get; set; } = "";
    public string[] TrustedProxyNetworks { get; set; } = [];

    public void Validate(bool development)
    {
        if (!Enabled)
        {
            if (!development)
                throw Invalid("Enabled must be true outside Development. Configure Authentik before starting the managed host.");
            return;
        }

        if (!IsHttpsUri(Authority, out _))
            throw Invalid("Authority must be an absolute HTTPS issuer URL without user info, query, or fragment.");
        if (!IsHttpsOrigin(PublicOrigin, out var origin))
            throw Invalid("PublicOrigin must be an HTTPS origin without a path, user info, query, or fragment.");
        PublicOrigin = origin.GetLeftPart(UriPartial.Authority);
        if (AdditionalPublicOrigins is null || AdditionalPublicOrigins.Length > 4)
            throw Invalid("AdditionalPublicOrigins must contain at most four exact HTTPS origins.");
        AdditionalPublicOrigins = AdditionalPublicOrigins.Select(value =>
            IsHttpsOrigin(value, out var alias) ? alias.GetLeftPart(UriPartial.Authority)
                : throw Invalid("AdditionalPublicOrigins must contain exact HTTPS origins, never wildcard hosts or paths."))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (string.IsNullOrWhiteSpace(ClientId))
            throw Invalid("ClientId is required.");
        foreach (var (name, value) in new[]
        {
            (nameof(ClientSecretFile), ClientSecretFile),
            (nameof(CaCertificatePath), CaCertificatePath),
            (nameof(DataProtectionKeysDirectory), DataProtectionKeysDirectory)
        })
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
                throw Invalid($"{name} must be an absolute path.");
        if (TrustedProxyNetworks.Length == 0 || TrustedProxyNetworks.Any(network => !IsPrivateNetwork(network)))
            throw Invalid("TrustedProxyNetworks must contain explicit private CIDRs for the isolated proxy network.");
    }

    public string ReadClientSecret()
    {
        try
        {
            var secret = File.ReadAllText(ClientSecretFile).TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(secret))
                throw Invalid("ClientSecretFile is empty.");
            return secret;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Invalid("ClientSecretFile cannot be read. Check the mounted secret file and service permissions.");
        }
    }

    public X509Certificate2 ReadCertificateAuthority()
    {
        try
        {
            var pem = File.ReadAllText(CaCertificatePath);
            if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
                throw Invalid("CaCertificatePath must contain only the public CA certificate.");
            var certificate = X509Certificate2.CreateFromPem(pem);
            if (certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow
                || !certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority)
                || certificate.Extensions.OfType<X509KeyUsageExtension>()
                    .Any(extension => !extension.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign)))
            {
                certificate.Dispose();
                throw Invalid("CaCertificatePath must contain a currently valid public CA certificate, not a private key or leaf certificate.");
            }
            return certificate;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw Invalid("CaCertificatePath cannot be loaded. Mount the installer-provided public PEM CA certificate.");
        }
    }

    public static SocketsHttpHandler CreateBackchannelHandler(X509Certificate2 root, bool authorityUsesSystemTrust = false)
    {
        // Leave SslStream's native chain, hostname, lifetime and server-EKU validation intact.
        // In particular, do not add the private LDAP CA to the public authority's trust store.
        if (authorityUsesSystemTrust)
            return new SocketsHttpHandler { AllowAutoRedirect = false };
        // SslStream still performs hostname validation; only the trust roots are replaced.
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            VerificationFlags = X509VerificationFlags.NoFlag,
            // The CA contract provides no CRL/OCSP service. Chain, validity, EKU, and DNS checks remain mandatory.
            RevocationMode = X509RevocationMode.NoCheck
        };
        policy.CustomTrustStore.Add(root);
        policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            SslOptions = new() { CertificateChainPolicy = policy }
        };
    }

    public static bool IsHttpsOrigin(string value, out Uri uri)
    {
        if (!IsHttpsUri(value, out uri) || uri.AbsolutePath != "/") return false;
        var slash = value.IndexOf('/', "https://".Length);
        return (slash == -1 || slash == value.Length - 1) && !value.TrimEnd('/').EndsWith(':');
    }

    private static bool IsHttpsUri(string value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!)
        && uri.IsWellFormedOriginalString()
        && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.Port is > 0 and <= 65535
        && uri.HostNameType is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6
        && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl)
        && !value.Contains('*') && !value.Contains('\\') && !value.Contains('%')
        && !value.Contains('?') && !value.Contains('#');

    private static bool IsPrivateNetwork(string value) =>
        IPNetwork.TryParse(value, out var network)
        && new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7" }
            .Select(IPNetwork.Parse)
            .Any(range => network.PrefixLength >= range.PrefixLength && range.Contains(network.BaseAddress));

    private static OptionsValidationException Invalid(string message) =>
        new(nameof(HostAuthenticationOptions), typeof(HostAuthenticationOptions), [$"HostAuthentication: {message}"]);
}
