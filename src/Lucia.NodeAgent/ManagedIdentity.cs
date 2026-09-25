using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Lucia.NodeAgent;

internal static class ManagedIdentity
{
    internal static string CreateCsr(InstallPlan plan, ECDsa key)
    {
        InstallationRules.ValidatePlan(plan);
        var request = new CertificateRequest("CN=" + plan.DeviceId.ToString("D"), key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(plan.Hostname);
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSigningRequestPem();
    }

    internal static void ValidateCsr(string csr, InstallPlan plan, ECDsa key)
    {
        try
        {
            if (csr.Length > 8192 || csr.Contains("PRIVATE KEY", StringComparison.Ordinal)) throw new CryptographicException();
            var pem = csr.AsSpan().Trim();
            if (!PemEncoding.TryFind(pem, out var fields)
                || !pem[fields.Label].SequenceEqual("CERTIFICATE REQUEST")
                || fields.Location.GetOffsetAndLength(pem.Length) != (0, pem.Length))
                throw new CryptographicException();
            var request = CertificateRequest.LoadSigningRequestPem(csr, HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
            if (!request.PublicKey.ExportSubjectPublicKeyInfo().SequenceEqual(key.ExportSubjectPublicKeyInfo())
                || !request.SubjectName.RawData.SequenceEqual(new X500DistinguishedName("CN=" + plan.DeviceId.ToString("D")).RawData)
                || request.CertificateExtensions.Count != 1)
                throw new CryptographicException();
            ExactSan(request.CertificateExtensions, plan.Hostname);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or AsnContentException)
        { throw new NodeAgentException("The persisted certificate request is not bound to the approved node identity."); }
    }

    internal static X509Certificate2 ValidateConfiguration(ManagedConfiguration config, InstallPlan plan, ECDsa key, Uri origin,
        bool allowExpiredForRenewal = false)
    {
        InstallationRules.ValidatePlan(plan);
        if (config.Hostname != plan.Hostname) throw InvalidConfiguration();
        if (config.CertificatePem is null || config.CertificatePem.Length > 16 * 1024
            || config.CaPem is null || config.CaPem.Length > 16 * 1024
            || config.CertificatePem.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw InvalidConfiguration();
        ValidateDirectory(config, origin);
        if (config.LdapBindDn != $"uid=node-{plan.DeviceId:N},ou=Services,{config.LdapBaseDn}")
            throw new NodeAgentException("The directory bind identity is not the native read-only account for this node.");
        using var authority = DiscoveryClient.ParseAuthority(config.CaPem);
        var bundle = new List<X509Certificate2>();
        try
        {
            var remaining = config.CertificatePem.AsSpan().Trim();
            while (!remaining.IsEmpty)
            {
                if (bundle.Count == 4 || !PemEncoding.TryFind(remaining, out var fields)
                    || !remaining[fields.Label].SequenceEqual("CERTIFICATE")
                    || fields.Location.GetOffsetAndLength(remaining.Length).Offset != 0)
                    throw new CryptographicException();
                bundle.Add(X509Certificate2.CreateFromPem(remaining[fields.Location]));
                remaining = remaining[fields.Location.End..].Trim();
            }
            if (bundle.Count == 0 || bundle.Select(c => c.Thumbprint).Distinct(StringComparer.Ordinal).Count() != bundle.Count)
                throw new CryptographicException();
            var certificate = bundle[0];
            var expired = certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow;
            using var publicKey = certificate.GetECDsaPublicKey();
            if (publicKey is null || !publicKey.ExportSubjectPublicKeyInfo().SequenceEqual(key.ExportSubjectPublicKeyInfo())
                || !certificate.SubjectName.RawData.SequenceEqual(new X500DistinguishedName("CN=" + plan.DeviceId.ToString("D")).RawData)
                || !certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => !e.CertificateAuthority)
                || certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority)
                || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || (expired && !allowExpiredForRenewal)
                || !certificate.Extensions.OfType<X509KeyUsageExtension>().Any(e => e.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature)))
                throw new CryptographicException();
            ExactSan(certificate.Extensions.Cast<X509Extension>(), plan.Hostname);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(authority);
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
            if (expired)
            {
                var historicalTime = certificate.NotBefore.ToUniversalTime().AddMinutes(1);
                if (historicalTime >= certificate.NotAfter.ToUniversalTime()) throw new CryptographicException();
                chain.ChainPolicy.VerificationTime = historicalTime;
            }
            foreach (var intermediate in bundle.Skip(1))
            {
                if (!intermediate.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority))
                    throw new CryptographicException();
                chain.ChainPolicy.ExtraStore.Add(intermediate);
            }
            if (!chain.Build(certificate) || chain.ChainElements.Count < 2
                || !chain.ChainElements[^1].Certificate.RawData.SequenceEqual(authority.RawData)
                || bundle.Skip(1).Any(extra => !chain.ChainElements.Cast<X509ChainElement>()
                    .Any(element => element.Certificate.RawData.SequenceEqual(extra.RawData)))
                || chain.ChainElements.Cast<X509ChainElement>().Skip(1).SkipLast(1)
                    .Any(element => !bundle.Skip(1).Any(extra => extra.RawData.SequenceEqual(element.Certificate.RawData))))
                throw new CryptographicException();
            bundle.RemoveAt(0);
            return certificate;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or AsnContentException)
        {
            throw new NodeAgentException("The issued node certificate failed CA, validity, key, CN, SAN or usage validation.");
        }
        finally
        {
            foreach (var certificate in bundle) certificate.Dispose();
        }
    }

    private static void ExactSan(IEnumerable<X509Extension> extensions, string hostname)
    {
        var san = extensions.Cast<X509Extension>().Where(e => e.Oid?.Value == "2.5.29.17").ToArray();
        if (san.Length != 1) throw new CryptographicException();
        var reader = new AsnReader(san[0].RawData, AsnEncodingRules.DER);
        var names = reader.ReadSequence();
        if (names.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 2)) != hostname)
            throw new CryptographicException();
        names.ThrowIfNotEmpty();
        reader.ThrowIfNotEmpty();
    }

    internal static void ValidateDirectory(ManagedConfiguration config, Uri origin)
    {
        if (config.LdapUri is null || config.LdapUri.Length > 2048 || config.LdapUri.Any(char.IsControl)
            || !Uri.TryCreate(config.LdapUri, UriKind.Absolute, out var ldap) || !ldap.IsWellFormedOriginalString()
            || ldap.Scheme != "ldaps" || !string.Equals(ldap.DnsSafeHost, origin.DnsSafeHost, StringComparison.OrdinalIgnoreCase)
            || ldap.UserInfo.Length != 0 || ldap.Query.Length != 0 || ldap.Fragment.Length != 0
            || ldap.AbsolutePath is not ("" or "/") || (ldap.Port != -1 && ldap.Port is < 1 or > 65535)
            || config.LdapUri.Contains('\\') || config.LdapUri.Any(char.IsWhiteSpace))
            throw InvalidConfiguration();
        Dn(config.LdapBaseDn);
        Dn(config.LdapBindDn);
        Dn(config.OwnerGroupDn);
        if (!Regex.IsMatch(config.LdapBaseDn, @"\Adc=[A-Za-z0-9-]+(?:,dc=[A-Za-z0-9-]+)*\z", RegexOptions.IgnoreCase)
            || !config.LdapBindDn.EndsWith("," + config.LdapBaseDn, StringComparison.OrdinalIgnoreCase)
            || !config.OwnerGroupDn.StartsWith("cn=lucia-owners,", StringComparison.OrdinalIgnoreCase)
            || !config.OwnerGroupDn.EndsWith("," + config.LdapBaseDn, StringComparison.OrdinalIgnoreCase)
            || config.LdapBindDn.Equals(config.OwnerGroupDn, StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(config.LdapBindDn, @"\A(?:cn|uid)=(?:admin|administrator|manager|root),", RegexOptions.IgnoreCase)
            || config.LdapBindPassword is null || config.LdapBindPassword.Length is < 16 or > 1024
            || config.LdapBindPassword.Any(c => c < '!' || c > '~' || c is '#' or ';' or '\\'))
            throw InvalidConfiguration();
    }

    private static void Dn(string? value)
    {
        // Deliberately accept the native controller's simple DNs, not the full escaped LDAP grammar.
        if (value is null || value.Length > 512
            || !Regex.IsMatch(value, @"\A(?:cn|ou|dc|uid)=[A-Za-z0-9][A-Za-z0-9._-]*(?:,(?:cn|ou|dc|uid)=[A-Za-z0-9][A-Za-z0-9._-]*)+\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw InvalidConfiguration();
    }

    internal static string Sssd(ManagedConfiguration config, Uri origin)
    {
        ValidateDirectory(config, origin);
        return $$"""
            [sssd]
            config_file_version = 2
            services = nss, pam
            domains = lucia

            [domain/lucia]
            id_provider = ldap
            auth_provider = ldap
            access_provider = simple
            simple_allow_groups = lucia-owners
            cache_credentials = true
            enumerate = false
            min_id = 1000
            ldap_uri = {{config.LdapUri}}
            ldap_search_base = {{config.LdapBaseDn}}
            ldap_default_bind_dn = {{config.LdapBindDn}}
            ldap_default_authtok_type = password
            ldap_default_authtok = {{config.LdapBindPassword}}
            ldap_schema = rfc2307bis
            ldap_group_object_class = groupOfUniqueNames
            ldap_group_member = uniqueMember
            ldap_group_search_base = {{config.OwnerGroupDn}}
            ldap_tls_reqcert = demand
            ldap_tls_cacert = /etc/lucia/directory-ca.pem
            ldap_id_use_start_tls = false
            override_homedir = /home/%u
            fallback_homedir = /home/%u
            default_shell = /bin/bash
            """ + "\n";
    }

    internal const string Ssh = """
        PermitRootLogin no
        PubkeyAuthentication yes
        PasswordAuthentication yes
        KbdInteractiveAuthentication yes
        UsePAM yes
        AllowGroups lucia-owners lucia-recovery
        Match User lucia-recovery
            AuthenticationMethods publickey
            PasswordAuthentication no
            KbdInteractiveAuthentication no
        Match all
        """ + "\n";

    internal const string Service = """
        [Unit]
        Description=Lucia managed node identity and heartbeat
        Wants=network-online.target
        After=network-online.target
        ConditionPathExists=/etc/lucia/managed.json

        [Service]
        Type=simple
        User=root
        Group=root
        UMask=0077
        ExecStart=/usr/lib/lucia/agent/lucia-node-agent managed-run
        Restart=on-failure
        RestartSec=30
        NoNewPrivileges=true
        PrivateTmp=true

        [Install]
        WantedBy=multi-user.target
        """ + "\n";

    private static NodeAgentException InvalidConfiguration() =>
        new("The server-provided directory configuration failed its bounded origin, DN or credential checks.");
}
