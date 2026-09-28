using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Lucia.NodeAgent;

internal static class InstallationChecks
{
    internal static async Task<int> RunAsync(string fixture)
    {
        var count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            count++;
        }
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (NodeAgentException) { count++; return; }
            throw new InvalidOperationException(message);
        }
        async Task RejectAsync(Func<Task> action, string message)
        {
            try { await action(); }
            catch (NodeAgentException) { count++; return; }
            throw new InvalidOperationException(message);
        }
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var publicKey = RecoveryKey();
        var disk = new DiskReport("/dev/disk/by-id/ata-fixture", "/dev/sda", "SSD", "serial",
            64L * 1024 * 1024 * 1024, false, false);
        var report = new HardwareReport("x86_64", "uefi", false, null, null, null, null, "CPU", 2, 8L * 1024 * 1024 * 1024,
            [new("eth0", "aa:bb:cc:00:11:22", ["192.0.2.2"])], [disk]);
        var credentials = new DiscoveryCredentials("https://controller.test/", id, "d1.fixture.secret", now.AddMinutes(30), "AB12-CD34");
        var config = new InstallationConfiguration(id, 1, taskId, now.AddMinutes(15), true, "lucia-node", disk.Id, publicKey);
        var bootId = Guid.NewGuid().ToString("D");
        var plan = InstallationRules.Approve(config, credentials, report, bootId, "8:0", now);
        Check(plan.Hostname == "lucia-node" && plan.DiskId == disk.Id, "Approved plan binding failed.");
        foreach (var changed in new[]
        {
            config with { CanRequestInstallationGrant = false }, config with { DeviceId = Guid.NewGuid() },
            config with { TaskId = null }, config with { TaskId = Guid.Empty }, config with { InventoryRevision = 0 },
            config with { AuthorityExpiresAt = now }, config with { AuthorityExpiresAt = now.AddHours(1) }
        })
            Reject(() => InstallationRules.Approve(changed, credentials, report, bootId, "8:0", now), "Unapproved/stale plan passed.");
        foreach (var host in new[] { "", "UPPER", "-host", "host-", "host.test", "localhost", "host\ncommand", "x;reboot", new string('a', 64) })
            Reject(() => InstallationRules.Hostname(host), "Unsafe hostname passed.");
        foreach (var badDisk in new[] { "/dev/sda", "/dev/disk/by-id/ata-thing-part1", "/dev/disk/by-id/../sda", "/dev/disk/by-id/a b", "/dev/disk/by-id/a;sh" })
            Reject(() => InstallationRules.DiskId(badDisk), "Unsafe disk passed.");
        foreach (var badKey in new[] { publicKey + "\ncommand", "command=\"whoami\" " + publicKey, "ssh-ed25519 AAAA",
            publicKey.Replace("ssh-ed25519", "ssh-rsa", StringComparison.Ordinal), "restrict " + publicKey,
            publicKey + "\r", publicKey + " -----BEGIN PRIVATE KEY-----" })
            Reject(() => InstallationRules.RecoveryKey(badKey), "Unsafe/malformed authorized key passed.");
        Check(InstallationRules.RecoveryKey(publicKey + " owner@github recovery key") == publicKey,
            "GitHub recovery key comments were not dropped.");
        Check(InstallationRules.RecoveryKey("  " + publicKey.Replace(" ", "   ", StringComparison.Ordinal) + "  comment") == publicKey,
            "Recovery key whitespace was not normalized to algorithm/base64.");
        var commentedApproval = InstallationRules.Approve(config with { RecoveryPublicKey = publicKey + " imported comment" },
            credentials, report, bootId, "8:0", now);
        Check(commentedApproval.RecoveryPublicKey == publicKey, "Approved plan did not pin a single normalized key.");

        using var rsaRecovery = RSA.Create(2048);
        var rsaParameters = rsaRecovery.ExportParameters(false);
        byte[] Mpint(byte[] number) => number[0] >= 0x80 ? [0, .. number] : number;
        var rsaKey = WireKey("ssh-rsa", Mpint(rsaParameters.Exponent!), Mpint(rsaParameters.Modulus!));
        Check(InstallationRules.RecoveryKey(rsaKey + " Github RSA key") == rsaKey, "RSA recovery key did not normalize.");
        var smallModulus = new byte[256];
        smallModulus[0] = 0x7f;
        smallModulus[^1] = 3;
        Reject(() => InstallationRules.RecoveryKey(WireKey("ssh-rsa", [1, 0, 1], smallModulus)), "2047-bit RSA recovery key passed.");
        var oddSizeModulus = new byte[257];
        oddSizeModulus[0] = 1;
        oddSizeModulus[^1] = 3;
        var oddSizeKey = WireKey("ssh-rsa", [1, 0, 1], oddSizeModulus);
        Check(InstallationRules.RecoveryKey(oddSizeKey) == oddSizeKey, "Valid 2049-bit positive SSH mpint was rejected.");
        Reject(() => InstallationRules.RecoveryKey(WireKey("ssh-rsa", [1], Mpint(rsaParameters.Modulus!))), "RSA exponent one passed.");
        Reject(() => InstallationRules.RecoveryKey(WireKey("ssh-rsa", [2], Mpint(rsaParameters.Modulus!))), "RSA even exponent passed.");
        Reject(() => InstallationRules.RecoveryKey(WireKey("ssh-rsa", [0, 1, 0, 1], Mpint(rsaParameters.Modulus!))), "Noncanonical RSA mpint passed.");
        Reject(() => InstallationRules.RecoveryKey(WireKey("ssh-rsa", [1, 0, 1], rsaParameters.Modulus!)), "Negative RSA modulus passed.");

        using var ecRecovery = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ecPoint = ecRecovery.ExportParameters(false).Q;
        byte[] uncompressed = [4, .. ecPoint.X!, .. ecPoint.Y!];
        var ecdsaKey = WireKey("ecdsa-sha2-nistp256", Encoding.ASCII.GetBytes("nistp256"), uncompressed);
        Check(InstallationRules.RecoveryKey(ecdsaKey + " GitHub P256 key") == ecdsaKey, "P256 recovery key did not normalize.");
        Reject(() => InstallationRules.RecoveryKey(WireKey("ecdsa-sha2-nistp256", Encoding.ASCII.GetBytes("nistp384"), uncompressed)),
            "P256 key accepted another curve.");
        Reject(() => InstallationRules.RecoveryKey(WireKey("ecdsa-sha2-nistp256", Encoding.ASCII.GetBytes("nistp256"), uncompressed[..64])),
            "Truncated P256 point passed.");
        Reject(() => InstallationRules.RecoveryKey(WireKey("ecdsa-sha2-nistp256", Encoding.ASCII.GetBytes("nistp256"), [2, .. uncompressed[1..]])),
            "Non-uncompressed P256 point passed.");
        Reject(() => InstallationRules.RecoveryKey(WireKey("ssh-ed25519", RandomNumberGenerator.GetBytes(31))), "Short Ed25519 key passed.");
        Reject(() => InstallationRules.RecoveryKey(WireKey("ssh-ed25519", RandomNumberGenerator.GetBytes(33))), "Long Ed25519 key passed.");
        Reject(() => InstallationRules.SelectDisk(report with { Disks = [disk, disk] }, disk.Id!), "Duplicate stable disk passed.");
        Reject(() => InstallationRules.SelectDisk(report with { Disks = [disk with { IsReadOnly = true }] }, disk.Id!), "Read-only disk passed.");
        Reject(() => InstallationRules.SelectDisk(report with { Disks = [disk with { IsRemovable = true }] }, disk.Id!), "Removable disk passed.");
        Reject(() => InstallationRules.SelectDisk(report with { BootMode = "bios" }, disk.Id!), "BIOS install passed.");
        Reject(() => InstallationRules.SelectDisk(report with { Architecture = "aarch64" }, disk.Id!), "Non-x64 install passed.");
        Reject(() => InstallationRules.SameDisk(plan, report with { Disks = [disk with { Serial = "replaced" }] }), "Replaced disk passed.");
        Reject(() => InstallationRules.SameDisk(plan, report with { Disks = [disk with { Path = "/dev/sdb" }] }), "Changed canonical device passed.");
        var preseed = InstallationRules.Preseed(plan);
        Check(preseed.Contains("mirror/https/hostname string deb.debian.org", StringComparison.Ordinal)
            && !preseed.Contains("mirror/http/hostname", StringComparison.Ordinal), "HTTPS mirror uses the wrong protocol-specific debconf keys.");
        Check(preseed.Contains("partman-auto/disk string " + disk.Id, StringComparison.Ordinal)
            && preseed.Contains("grub-installer/bootdev string " + disk.Id, StringComparison.Ordinal)
            && preseed.Contains("installation-guard", StringComparison.Ordinal) && preseed.Contains("finish-install", StringComparison.Ordinal),
            "Preseed is not single-disk and guarded.");
        Check(!preseed.Contains(credentials.Token, StringComparison.Ordinal) && !preseed.Contains(publicKey, StringComparison.Ordinal)
            && !preseed.Contains("device_remove_lvm", StringComparison.Ordinal) && !preseed.Contains("device_remove_md", StringComparison.Ordinal)
            && !preseed.Contains("sh -c", StringComparison.Ordinal) && !preseed.Contains("wget", StringComparison.Ordinal),
            "Preseed includes credentials, arbitrary shell or multi-disk removal.");
        Check(preseed.Contains("passwd/root-login boolean true", StringComparison.Ordinal)
            && preseed.Contains("root-password-crypted password *", StringComparison.Ordinal)
            && preseed.Contains("passwd/make-user boolean false", StringComparison.Ordinal)
            && preseed.Contains("firmware-realtek", StringComparison.Ordinal) && preseed.Contains("mirror/suite string trixie", StringComparison.Ordinal),
            "Preseed OS/security defaults are wrong.");
        Reject(() => InstallationRules.Preseed(plan with { AuthorityExpiresAt = now.AddSeconds(-1) }), "Expired preseed passed.");
        var grant = new InstallationGrant(taskId, id, Guid.NewGuid(), plan.Hostname, disk.Id!, 1, now.AddMinutes(5), "debian-13.7", publicKey, now.AddHours(2));
        InstallationRules.ValidateGrant(grant, plan, grant.RequestId, now, true);
        foreach (var bad in new[]
        {
            grant with { DiskId = "/dev/disk/by-id/other" }, grant with { DeviceId = Guid.NewGuid() }, grant with { TaskId = Guid.NewGuid() },
            grant with { RequestId = Guid.NewGuid() }, grant with { Hostname = "other" }, grant with { InventoryRevision = 2 },
            grant with { OperatingSystem = "debian-12" }, grant with { RecoveryPublicKey = publicKey + " modified" },
            grant with { ExpiresAt = now }, grant with { ExpiresAt = now.AddHours(1) }, grant with { ProgressExpiresAt = now.AddDays(1) }
        })
            Reject(() => InstallationRules.ValidateGrant(bad, plan, grant.RequestId, now, true), "Mismatched/stale grant passed.");
        InstallationRules.ValidateGrant(grant, plan, grant.RequestId, now.AddMinutes(31), false);
        Check(true, "Progress authority was incorrectly limited to discovery expiry.");

        using var identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = ManagedIdentity.CreateCsr(plan, identity);
        ManagedIdentity.ValidateCsr(csr, plan, identity);
        using var otherIdentity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Reject(() => ManagedIdentity.ValidateCsr(csr, plan, otherIdentity), "CSR accepted another private key.");
        Reject(() => ManagedIdentity.ValidateCsr(csr + identity.ExportPkcs8PrivateKeyPem(), plan, identity), "CSR allowed a private key to leave the node.");
        Reject(() => ManagedIdentity.ValidateCsr(csr + csr, plan, identity), "Duplicate CSR PEM blocks passed.");
        Reject(() => ManagedIdentity.ValidateCsr(csr, plan with { Hostname = "other" }, identity), "CSR accepted another hostname.");
        var parsedCsr = CertificateRequest.LoadSigningRequestPem(csr, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        Check(parsedCsr.CertificateExtensions.Count == 1 && parsedCsr.SubjectName.Name == "CN=" + id.ToString("D"), "CSR has extra extensions or wrong CN.");
        var csrTampered = csr[..100] + (csr[100] == 'A' ? "B" : "A") + csr[101..];
        Reject(() => ManagedIdentity.ValidateCsr(csrTampered, plan, identity), "Tampered CSR signature passed.");
        using var authorityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var authority = MakeAuthority(authorityKey);
        using var leaf = MakeLeaf(authority, identity, plan);
        var managed = new ManagedConfiguration(plan.Hostname, leaf.ExportCertificatePem(), authority.ExportCertificatePem(),
            "ldaps://controller.test:636", "dc=lucia,dc=home,dc=arpa", $"uid=node-{id:N},ou=Services,dc=lucia,dc=home,dc=arpa",
            "ReadOnlyFixtureSecret123", "cn=lucia-owners,ou=Groups,dc=lucia,dc=home,dc=arpa");
        using (ManagedIdentity.ValidateConfiguration(managed, plan, identity, new(credentials.Server))) { count++; }
        foreach (var wrongBind in new[]
        {
            $"uid={id:N},ou=Services,{managed.LdapBaseDn}",
            $"uid=node-{Guid.NewGuid():N},ou=Services,{managed.LdapBaseDn}",
            $"cn=node-{id:N},ou=Services,{managed.LdapBaseDn}",
            $"uid=node-{id:N},ou=People,{managed.LdapBaseDn}"
        })
            Reject(() => ManagedIdentity.ValidateConfiguration(managed with { LdapBindDn = wrongBind }, plan, identity, new(credentials.Server)),
                "Another or non-native node bind account was accepted.");
        using (ManagedIdentity.ValidateConfiguration(managed with { LdapUri = "ldaps://spark-9423:636" },
                   plan, identity, new("https://spark-9423"))) { count++; }
        Reject(() => ManagedIdentity.ValidateConfiguration(managed with { LdapUri = "ldaps://spark-9423:636" },
            plan, identity, new("https://public-portal.test")), "The public portal silently replaced the private controller origin.");
        var leafExpiry = new DateTimeOffset(leaf.NotAfter.ToUniversalTime());
        Check(leafExpiry - DateTimeOffset.UtcNow is var remainingValidity
            && remainingValidity > TimeSpan.FromHours(23) && remainingValidity <= TimeSpan.FromHours(24),
            "Node certificate fixture is not governed by the existing 24-hour policy.");
        Check(!ManagedRunner.NeedsRenewal(leafExpiry, leafExpiry.AddHours(-24))
            && !ManagedRunner.NeedsRenewal(leafExpiry, leafExpiry.AddHours(-6))
            && ManagedRunner.NeedsRenewal(leafExpiry, leafExpiry.AddHours(-6).AddSeconds(1))
            && !ManagedRunner.NeedsRenewal(leafExpiry, leafExpiry),
            "Renewal does not begin strictly below six hours of remaining valid lifetime.");
        using var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var intermediate = MakeIntermediate(authority, intermediateKey);
        using var chainedLeaf = MakeLeaf(intermediate, identity, plan);
        var chained = managed with { CertificatePem = chainedLeaf.ExportCertificatePem() + "\n" + intermediate.ExportCertificatePem() };
        using (var validated = ManagedIdentity.ValidateConfiguration(chained, plan, identity, new(credentials.Server)))
            Check(validated.RawData.SequenceEqual(chainedLeaf.RawData), "Leaf/intermediate chain returned a CA instead of the bound node leaf.");
        // step-ca's default leaf template omits basicConstraints; absence means end-entity (RFC 5280).
        using var stepLeaf = MakeLeaf(intermediate, identity, plan, isCa: null);
        using (ManagedIdentity.ValidateConfiguration(chained with { CertificatePem = stepLeaf.ExportCertificatePem() + "\n" + intermediate.ExportCertificatePem() },
                   plan, identity, new(credentials.Server))) { count++; }
        Reject(() => ManagedIdentity.ValidateConfiguration(chained with { CertificatePem = chainedLeaf.ExportCertificatePem() },
            plan, identity, new(credentials.Server)), "Missing required intermediate was accepted without downloads.");
        Reject(() => ManagedIdentity.ValidateConfiguration(chained with
        {
            CertificatePem = intermediate.ExportCertificatePem() + "\n" + chainedLeaf.ExportCertificatePem()
        }, plan, identity, new(credentials.Server)), "CA-first enrollment chain passed.");
        Reject(() => ManagedIdentity.ValidateConfiguration(chained with { CertificatePem = chained.CertificatePem + identity.ExportPkcs8PrivateKeyPem() },
            plan, identity, new(credentials.Server)), "Enrollment certificate chain carried a private key.");
        Reject(() => ManagedIdentity.ValidateConfiguration(chained with { CertificatePem = chained.CertificatePem + "\n" + intermediate.ExportCertificatePem() },
            plan, identity, new(credentials.Server)), "Duplicate intermediate certificates passed.");
        Reject(() => ManagedIdentity.ValidateConfiguration(chained with { CertificatePem = chained.CertificatePem + "\nunrelated text" },
            plan, identity, new(credentials.Server)), "Certificate chain accepted unrelated trailing content.");
        Reject(() => ManagedIdentity.ValidateConfiguration(managed, plan, otherIdentity, new(credentials.Server)), "Certificate accepted another key.");
        using var expired = MakeLeaf(authority, identity, plan, expired: true);
        using var caLeaf = MakeLeaf(authority, identity, plan, isCa: true);
        using var extraSan = MakeLeaf(authority, identity, plan, extraSan: true);
        using var wrongSubject = MakeLeaf(authority, identity, plan with { DeviceId = Guid.NewGuid() });
        foreach (var invalid in new[] { expired, caLeaf, extraSan, wrongSubject })
            Reject(() => ManagedIdentity.ValidateConfiguration(managed with { CertificatePem = invalid.ExportCertificatePem() },
                plan, identity, new(credentials.Server)), "Invalid node certificate accepted.");
        using var otherCaKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherCa = MakeAuthority(otherCaKey);
        Reject(() => ManagedIdentity.ValidateConfiguration(chained with { CertificatePem = chained.CertificatePem + "\n" + otherCa.ExportCertificatePem() },
            plan, identity, new(credentials.Server)), "Unused unrelated chain authority passed.");
        Reject(() => ManagedIdentity.ValidateConfiguration(managed with { CaPem = otherCa.ExportCertificatePem() }, plan, identity, new(credentials.Server)),
            "Wrong certificate chain passed.");
        using var expiredChainedLeaf = MakeLeaf(intermediate, identity, plan, expired: true);
        var expiredCached = managed with
        {
            CertificatePem = expiredChainedLeaf.ExportCertificatePem() + "\n" + intermediate.ExportCertificatePem()
        };
        using (ManagedIdentity.ValidateConfiguration(expiredCached, plan, identity, new(credentials.Server), allowExpiredForRenewal: true)) { count++; }
        Reject(() => ManagedIdentity.ValidateConfiguration(expiredCached, plan, identity, new(credentials.Server)),
            "Historical recovery validation leaked into normal identity validation.");
        var recoveryCalls = new List<string>();
        var recoveryChallenge = new DiscoveryChallenge("recovery-challenge", "fresh-recovery-nonce", DateTimeOffset.UtcNow.AddMinutes(2));
        using (var recoveryClient = new DiscoveryClient(new(credentials.Server), new InstallationHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            recoveryCalls.Add(path);
            Check(request.Headers.Authorization is null, "Recovery used the revoked installation capability.");
            if (path.EndsWith("/challenge", StringComparison.Ordinal)) return Response(recoveryChallenge);
            var body = JsonSerializer.Deserialize<MachineRequest>(await request.Content!.ReadAsStringAsync(), AgentJson.Options)!;
            if (path.EndsWith("/renew", StringComparison.Ordinal))
            {
                Check(body.CertificatePem == expiredCached.CertificatePem, "Recovery did not present its known historical node identity.");
                var renewal = JsonSerializer.Deserialize<EnrollmentProof>(body.Proof.ReportJson, AgentJson.Options)!;
                var signedMessage = Encoding.UTF8.GetBytes("lucia-discovery-v1\n" + recoveryChallenge.ChallengeId + "\n"
                    + recoveryChallenge.Nonce + "\n" + body.Proof.ReportJson);
                Check(renewal.CsrPem == csr && renewal.NodeId == id && renewal.TaskId == taskId
                    && identity.VerifyData(signedMessage, Convert.FromBase64String(body.Proof.Signature),
                        HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
                    "Recovery did not use the persisted CSR and registered key to prove a fresh nonce.");
                return Response(managed);
            }
            Check(path.EndsWith("/heartbeat", StringComparison.Ordinal) && body.CertificatePem == managed.CertificatePem,
                "Recovery sent a heartbeat using the expired certificate.");
            return Response(new { accepted = true });
        })))
        {
            var metrics = new NodeMetrics(id, plan.Hostname, null, null, null, null, null, null, null);
            await RejectAsync(() => recoveryClient.HeartbeatAsync(expiredCached.CertificatePem, metrics, identity, default),
                "An expired heartbeat certificate was transmitted.");
            Check(recoveryCalls.Count == 0, "Expired heartbeat fetched a challenge before renewal.");
            var recovered = await ManagedRunner.RecoverExpiredAsync(recoveryClient, expiredCached, plan, csr, identity, authority, default);
            Check(recovered == managed && recoveryCalls.Count == 2
                && recoveryCalls[1].EndsWith("/renew", StringComparison.Ordinal), "Expired cache did not renew before heartbeat.");
            await recoveryClient.HeartbeatAsync(recovered!.CertificatePem, metrics, identity, default);
            Check(recoveryCalls.Count == 4 && recoveryCalls[^1].EndsWith("/heartbeat", StringComparison.Ordinal),
                "The recovered valid identity could not heartbeat.");
        }
        var rejectedRecoveryCalls = 0;
        using (var recoveryClient = new DiscoveryClient(new(credentials.Server), new InstallationHandler(_ =>
        {
            rejectedRecoveryCalls++;
            return Task.FromResult(Response(managed));
        })))
        {
            await RejectAsync(() => ManagedRunner.RecoverExpiredAsync(recoveryClient,
                expiredCached with { CaPem = otherCa.ExportCertificatePem() }, plan, csr, identity, authority, default),
                "Unknown cached root was accepted for recovery.");
            await RejectAsync(() => ManagedRunner.RecoverExpiredAsync(recoveryClient, expiredCached, plan, csr, otherIdentity, authority, default),
                "Changed private key was accepted for recovery.");
            await RejectAsync(() => ManagedRunner.RecoverExpiredAsync(recoveryClient, expiredCached, plan, csr + identity.ExportPkcs8PrivateKeyPem(),
                identity, authority, default), "Corrupt CSR leaked private identity material in recovery.");
            Check(rejectedRecoveryCalls == 0, "Invalid cached identity contacted the renewal endpoint.");
        }
        using var wrongRecoveryKeyLeaf = MakeLeaf(authority, otherIdentity, plan);
        foreach (var response in new[] { expiredCached, managed with { CaPem = otherCa.ExportCertificatePem() },
                     managed with { CertificatePem = wrongRecoveryKeyLeaf.ExportCertificatePem() } })
        {
            using var recoveryClient = new DiscoveryClient(new(credentials.Server), new InstallationHandler(request =>
                Task.FromResult(request.Method == HttpMethod.Get ? Response(recoveryChallenge) : Response(response))));
            await RejectAsync(() => ManagedRunner.RecoverExpiredAsync(recoveryClient, expiredCached, plan, csr, identity, authority, default),
                "Recovery accepted an expired response, changed key or changed pinned CA.");
        }
        var pendingRecoveryCalls = 0;
        using (var recoveryClient = new DiscoveryClient(new(credentials.Server), new InstallationHandler(request =>
        {
            pendingRecoveryCalls++;
            Check(!request.RequestUri!.AbsolutePath.EndsWith("/heartbeat", StringComparison.Ordinal), "Pending recovery sent a heartbeat.");
            return Task.FromResult(request.Method == HttpMethod.Get ? Response(recoveryChallenge)
                : Response(new { state = "Pending" }, HttpStatusCode.Accepted));
        })))
            Check(await ManagedRunner.RecoverExpiredAsync(recoveryClient, expiredCached, plan, csr, identity, authority, default) is null
                && pendingRecoveryCalls == 2, "Pending expired-node recovery was not preserved.");
        foreach (var invalid in new[]
        {
            managed with { LdapUri = "ldap://controller.test" }, managed with { LdapUri = "ldaps://attacker.test:636" },
            managed with { LdapUri = "ldaps://user:password@controller.test:636" }, managed with { LdapUri = "ldaps://controller.test:636/path" },
            managed with { LdapUri = "ldaps://controller.test:636\nx=y" }, managed with { LdapBaseDn = "dc=other\nx=y" },
            managed with { LdapBindDn = "cn=admin,dc=elsewhere" }, managed with { OwnerGroupDn = "cn=everyone," + managed.LdapBaseDn },
            managed with { LdapBindDn = "cn=admin," + managed.LdapBaseDn },
            managed with { LdapBindPassword = "password\nldap_tls_reqcert=never" }
        })
            Reject(() => ManagedIdentity.Sssd(invalid, new(credentials.Server)), "Unsafe directory config passed.");
        var sssd = ManagedIdentity.Sssd(managed, new(credentials.Server));
        Check(sssd.Contains("ldap_schema = rfc2307bis", StringComparison.Ordinal)
            && sssd.Contains("ldap_group_member = uniqueMember", StringComparison.Ordinal)
            && sssd.Contains("ldap_tls_reqcert = demand", StringComparison.Ordinal)
            && sssd.Contains("simple_allow_groups = lucia-owners", StringComparison.Ordinal), "SSSD lacks required auth boundaries.");
        Check(ManagedIdentity.Ssh.Contains("PermitRootLogin no", StringComparison.Ordinal)
            && ManagedIdentity.Ssh.Contains("AllowGroups lucia-owners lucia-recovery", StringComparison.Ordinal)
            && ManagedIdentity.Ssh.Contains("AuthenticationMethods publickey", StringComparison.Ordinal), "SSH access is not scoped.");
        Check(ManagedIdentity.Ssh.Contains("AuthorizedKeysFile .ssh/authorized_keys /etc/ssh/lucia-authorized-keys/%u", StringComparison.Ordinal),
            "Owner SSH keys from Lucia are not read by sshd.");
        var keyFiles = ManagedFiles.SshKeyFiles(new Dictionary<string, string[]> { ["zack"] = [publicKey + " laptop", publicKey] });
        Check(keyFiles["zack"] == publicKey + "\n" + publicKey + "\n", "Owner SSH keys were not normalized without comments.");
        foreach (var bad in new Dictionary<string, string[]>[]
        {
            new() { ["root"] = [publicKey] }, new() { ["lucia-recovery"] = [publicKey] }, new() { ["../etc"] = [publicKey] },
            new() { ["Zack"] = [publicKey] }, new() { ["zack"] = [] }, new() { ["zack"] = ["command=\"id\" " + publicKey] },
            new() { ["zack"] = [publicKey + "\n" + publicKey] }
        })
            Reject(() => ManagedFiles.SshKeyFiles(bad), "Unsafe owner SSH key list passed.");
        Check(ManagedIdentity.Service.Contains("UMask=0077", StringComparison.Ordinal)
            && ManagedIdentity.Service.Contains("ExecStart=/usr/lib/lucia/agent/lucia-node-agent managed-run", StringComparison.Ordinal)
            && !ManagedIdentity.Service.Contains("secret", StringComparison.OrdinalIgnoreCase), "Service is not fixed/private.");
        Check(ManagedFiles.OwnersSudoers == "%lucia-owners ALL=(ALL:ALL) ALL\n"
            && !ManagedFiles.OwnersSudoers.Contains("NOPASSWD", StringComparison.Ordinal),
            "LDAP owner sudo privileges must be fixed, group-scoped and password-required.");
        const string nss = "passwd: files systemd\ngroup: files\nshadow: files # keep\nhosts: files dns\n";
        var rewritten = ManagedFiles.Nsswitch(nss);
        var hosts = ManagedFiles.Hosts("127.0.0.1 localhost\n127.0.1.1 debian\n::1 localhost ip6-localhost\n", "approved-node");
        Check(hosts.Contains("127.0.1.1\tapproved-node", StringComparison.Ordinal) && hosts.Contains("::1 localhost ip6-localhost", StringComparison.Ordinal)
            && hosts == ManagedFiles.Hosts(hosts, "approved-node"), "Approved hostname did not replace only the installed local-host mapping.");
        Reject(() => ManagedFiles.Hosts("127.0.1.1 first\n127.0.1.1 second\n", "approved-node"), "Ambiguous installed hosts mappings were overwritten.");
        Check(rewritten == ManagedFiles.Nsswitch(rewritten) && rewritten.Contains("shadow: files sss # keep", StringComparison.Ordinal)
            && rewritten.Contains("hosts: files dns", StringComparison.Ordinal), "NSS update isn't idempotent/scoped.");
        Check(ManagedFiles.HomeSession("session required pam_unix.so\n") ==
            ManagedFiles.HomeSession(ManagedFiles.HomeSession("session required pam_unix.so\n")), "PAM home update isn't idempotent.");
        var groupLookups = 0;
        var groupDelays = 0;
        Task LookupDelay(TimeSpan delay, CancellationToken token)
        {
            Check(delay == TimeSpan.FromSeconds(2), "Owner-group retry delay changed.");
            token.ThrowIfCancellationRequested();
            groupDelays++;
            return Task.CompletedTask;
        }
        await ManagedFiles.VerifyOwnerGroupAsync(default, (executable, arguments, token) =>
        {
            Check(executable == "/usr/bin/getent" && arguments.SequenceEqual(new[] { "group", "lucia-owners" })
                && token.CanBeCanceled, "Owner-group verification is not a fixed bounded command.");
            groupLookups++;
            if (groupLookups == 1) throw new NodeAgentException("private-lookup-failure");
            if (groupLookups == 2) throw new OperationCanceledException("simulated bounded attempt timeout");
            return Task.CompletedTask;
        }, LookupDelay);
        Check(groupLookups == 3 && groupDelays == 2, "SSSD startup retry did not wait for successful group resolution.");
        groupLookups = groupDelays = 0;
        try
        {
            await ManagedFiles.VerifyOwnerGroupAsync(default, (_, _, _) =>
            {
                groupLookups++;
                throw new NodeAgentException("private-lookup-secret");
            }, LookupDelay);
            throw new InvalidOperationException("Unavailable owner group allowed management readiness.");
        }
        catch (NodeAgentException ex)
        {
            Check(groupLookups == 3 && groupDelays == 2 && !ex.Message.Contains("private-lookup", StringComparison.Ordinal)
                && ex.Message.Contains("No managed heartbeat", StringComparison.Ordinal),
                "Failed lookup was not bounded or exposed raw command output.");
        }
        using (var stoppedLookup = new CancellationTokenSource())
        {
            stoppedLookup.Cancel();
            try
            {
                await ManagedFiles.VerifyOwnerGroupAsync(stoppedLookup.Token, (_, _, _) =>
                    throw new InvalidOperationException("Cancelled owner-group lookup executed a command."), LookupDelay);
                throw new InvalidOperationException("Owner-group cancellation was ignored.");
            }
            catch (OperationCanceledException) { count++; }
        }

        var challenge = new DiscoveryChallenge("installation-fixture", "nonce", now.AddMinutes(2));
        var requests = new List<string>();
        var postCount = 0;
        using (var client = new DiscoveryClient(new(credentials.Server), new InstallationHandler(async request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            var path = request.RequestUri.AbsolutePath;
            if (path.StartsWith("/api/boot/", StringComparison.Ordinal))
                Check(request.Headers.Authorization?.Parameter == credentials.Token, "Device request lacks bearer.");
            else Check(request.Headers.Authorization is null, "Machine request leaked bootstrap bearer.");
            if (path.EndsWith("/challenge", StringComparison.Ordinal)) return Response(challenge);
            if (path.EndsWith("/installation", StringComparison.Ordinal)) return Response(config);
            if (path.EndsWith("/grant", StringComparison.Ordinal))
            {
                postCount++;
                var body = JsonSerializer.Deserialize<GrantRequest>(await request.Content!.ReadAsStringAsync(), AgentJson.Options)!;
                Check(body.DiskId == disk.Id && body.InventoryRevision == 1 && body.Proof.ReportJson == AgentJson.SerializeReport(report), "Grant wire contract mismatch.");
                return Response(grant);
            }
            if (path.EndsWith("/progress", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadAsStringAsync();
                Check(body.Contains("AwaitingEnrollment", StringComparison.Ordinal) && !body.Contains(credentials.Token, StringComparison.Ordinal), "Progress body is not bounded/static.");
                return Response(new { accepted = true });
            }
            var machine = path.EndsWith("/enroll", StringComparison.Ordinal)
                ? JsonSerializer.Deserialize<EnrollmentRequest>(await request.Content!.ReadAsStringAsync(), AgentJson.Options)!.Proof
                : JsonSerializer.Deserialize<MachineRequest>(await request.Content!.ReadAsStringAsync(), AgentJson.Options)!.Proof;
            if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                var metrics = JsonSerializer.Deserialize<NodeMetrics>(machine.ReportJson, AgentJson.Options)!;
                Check(metrics.NodeId == id && metrics.Hostname == plan.Hostname && metrics.LoadAverage is null, "Heartbeat serialization changed unavailable metrics.");
                return Response(new { accepted = true });
            }
            var proof = JsonSerializer.Deserialize<EnrollmentProof>(machine.ReportJson, AgentJson.Options)!;
            Check(proof.NodeId == id && proof.TaskId == taskId && proof.CsrPem == csr && !machine.ReportJson.Contains("PRIVATE KEY", StringComparison.Ordinal), "Enrollment/renewal payload is wrong.");
            return path.EndsWith("/enroll", StringComparison.Ordinal)
                ? Response(new { state = "Pending" }, HttpStatusCode.Accepted) : Response(managed);
        })))
        {
            Check(await client.InstallationAsync(credentials, default) == config, "Installation polling deserialization failed.");
            var proof = DiscoveryClient.SignReport(challenge, AgentJson.SerializeReport(report), identity);
            Check(await client.GrantAsync(credentials, new(grant.RequestId, 1, disk.Id!, proof), default) == grant, "Grant deserialization failed.");
            await client.ProgressAsync(credentials with { ExpiresAt = now.AddMinutes(-1) }, grant, "AwaitingEnrollment", default);
            Check(await client.EnrollAsync(credentials, plan, grant, csr, identity, default) is null, "202 Pending enrollment wasn't preserved.");
            await client.HeartbeatAsync(managed.CertificatePem, new(id, plan.Hostname, null, null, null, null, null, null, null), identity, default);
            Check(await client.RenewAsync(managed.CertificatePem, plan, csr, identity, default) == managed, "Renewal config deserialization failed.");
            await RejectAsync(() => client.ProgressAsync(credentials, grant, "run-shell", default), "Arbitrary progress phase accepted.");
            await RejectAsync(() => client.InstallationAsync(credentials with { Server = "https://attacker.test/" }, default), "Cross-origin bootstrap capability used.");
        }
        Check(postCount == 1 && requests.Count == 9, "Unexpected API calls occurred.");
        postCount = 0;
        using (var client = new DiscoveryClient(new(credentials.Server), new InstallationHandler(_ =>
        {
            postCount++;
            throw new HttpRequestException("private-transport-secret");
        })))
            await RejectAsync(() => client.GrantAsync(credentials, new(grant.RequestId, 1, disk.Id!,
                DiscoveryClient.SignReport(challenge, AgentJson.SerializeReport(report), identity)), default), "Failed grant POST accepted.");
        Check(postCount == 1, "Uncertain grant POST was retried.");
        using (var client = new DiscoveryClient(new(credentials.Server), new InstallationHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("private-http-body-credential") }))))
        {
            try { await client.InstallationAsync(credentials, default); throw new InvalidOperationException("Denied installation polling passed."); }
            catch (NodeAgentException ex)
            {
                Check(!ex.Message.Contains("private-http-body", StringComparison.Ordinal), "Installation error exposed server credentials.");
            }
        }
        using (var client = new DiscoveryClient(new(credentials.Server), new InstallationHandler(_ =>
        {
            var withCommand = JsonSerializer.Serialize(config, AgentJson.Options).TrimEnd('}')
                + ",\"command\":\"do-not-execute\",\"secret\":\"do-not-print\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(withCommand) });
        })))
        {
            var bounded = await client.InstallationAsync(credentials, default);
            var output = InstallationRules.Preseed(InstallationRules.Approve(bounded, credentials, report, bootId, "8:0", now));
            Check(!output.Contains("do-not-", StringComparison.Ordinal), "Unrecognized server commands or secrets reached preseed.");
        }
        using (var client = new DiscoveryClient(new(credentials.Server), new InstallationHandler(_ => Task.FromResult(
            Response(new { state = "Pending" }, HttpStatusCode.Accepted)))))
            await RejectAsync(() => client.HeartbeatAsync(managed.CertificatePem, new(id, plan.Hostname, null, null, null, null, null, null, null), identity, default),
                "Pending heartbeat marked a node managed.");

        var metricsProc = Path.Combine(fixture, "metrics");
        Directory.CreateDirectory(metricsProc);
        File.WriteAllText(Path.Combine(metricsProc, "uptime"), "123.75 20.1\n");
        File.WriteAllText(Path.Combine(metricsProc, "loadavg"), "0.42 0.41 0.40 1/22 99\n");
        File.WriteAllText(Path.Combine(metricsProc, "meminfo"), "MemTotal: 8192 kB\nMemAvailable: 4096 kB\n");
        var os = Path.Combine(metricsProc, "os-release");
        File.WriteAllText(os, "PRETTY_NAME=\"Debian GNU/Linux 13 (trixie)\"\n");
        var metricsResult = ManagedRunner.ReadMetrics(plan, metricsProc, os, () => (1000, 500));
        Check(metricsResult.UptimeSeconds == 123.75 && metricsResult.LoadAverage == 0.42
            && metricsResult.MemoryTotalBytes == 8192 * 1024 && metricsResult.MemoryAvailableBytes == 4096 * 1024
            && metricsResult.StorageAvailableBytes == 500 && metricsResult.OsVersion == "Debian GNU/Linux 13 (trixie)", "Live metrics parser fabricated or lost values.");
        File.WriteAllText(Path.Combine(metricsProc, "loadavg"), "NaN invalid");
        File.Delete(Path.Combine(metricsProc, "meminfo"));
        var unavailable = ManagedRunner.ReadMetrics(plan, metricsProc, os + "-missing", () => throw new IOException());
        Check(unavailable.LoadAverage is null && unavailable.MemoryTotalBytes is null && unavailable.StorageTotalBytes is null && unavailable.OsVersion is null,
            "Unavailable metrics were invented.");
        var gpus = NodeRuntime.ParseNvidiaGpus("NVIDIA CMP 170HX, 65536, 8.0, GPU-cbeac6c4-3134-d34a-9fb5-fc0a0daf1981\n"
            + "NVIDIA GeForce RTX 4090, [N/A], [N/A], [N/A]\nbroken line\n");
        Check(gpus.Length == 2 && gpus[0] == new GpuReport("nvidia", "NVIDIA CMP 170HX", 65536L * 1024 * 1024, "8.0", "GPU-cbeac6c4-3134-d34a-9fb5-fc0a0daf1981")
            && gpus[1] is { Model: "NVIDIA GeForce RTX 4090", MemoryBytes: null, ComputeCapability: null, Uuid: null }, "nvidia-smi GPU parsing invented or lost values.");
        Check(NodeRuntime.ParseNvidiaHeader("| NVIDIA-SMI 610.43.03    KMD Version: 610.43.03     CUDA UMD Version: 13.3     |") == ("610.43.03", "13.3")
            && NodeRuntime.ParseNvidiaHeader("| NVIDIA-SMI 550.54.14    Driver Version: 550.54.14      CUDA Version: 12.4     |") == ("550.54.14", "12.4")
            && NodeRuntime.ParseNvidiaHeader("NVIDIA-SMI has failed") == (null, null), "nvidia-smi driver or CUDA version parsing is wrong.");
        Check(NodeRuntime.DaemonConfig.Contains("172.16.0.0/12", StringComparison.Ordinal)
            && System.Text.Json.JsonDocument.Parse(NodeRuntime.DaemonConfig).RootElement.GetProperty("log-driver").GetString() == "local",
            "Docker daemon config must keep container networks off home LAN ranges.");

        var tcp = "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n"
            + "   0: 0100007F:0035 00000000:0000 0A 00000000:00000000 00:00000000 00000000   0 0 1 1\n"
            + "   1: 00000000:7E90 00000000:0000 0A 00000000:00000000 00:00000000 00000000   0 0 2 1\n"
            + "   2: F100A8C0:0016 0B00A8C0:D431 01 00000000:00000000 00:00000000 00000000   0 0 3 1\n";
        var tcpListeners = StackRunner.ParseListeners(tcp, "tcp").ToArray();
        Check(tcpListeners.SequenceEqual([new NodeListener("tcp", "127.0.0.1", 53), new NodeListener("tcp", "0.0.0.0", 32400)]),
            "TCP listener parsing lost a listener or reported an established connection.");
        var tcp6 = "  sl  local_address rem_address st\n   0: 00000000000000000000000000000000:1F90 00000000000000000000000000000000:0000 0A\n"
            + "   1: 00000000000000000000000001000000:0277 00000000000000000000000000000000:0000 0A\n";
        Check(StackRunner.ParseListeners(tcp6, "tcp").SequenceEqual([new NodeListener("tcp", "::", 8080), new NodeListener("tcp", "::1", 631)]),
            "IPv6 listener addresses were decoded in the wrong byte order.");
        var udp = "  sl  local_address rem_address   st\n   0: 00000000:076C 00000000:0000 07\n   1: F100A8C0:A1B2 0B00A8C0:0035 01\n";
        Check(StackRunner.ParseListeners(udp, "udp").SequenceEqual([new NodeListener("udp", "0.0.0.0", 1900)]),
            "UDP parsing must report only unconnected bound sockets.");
        var containerId = new string('c', 64);
        var owned = StackRunner.ParseListeners(tcp.Replace("0 0 3 1", "0 0 3 1\n   3: 00000000:0801 00000000:0000 0A 00000000:00000000 00:00000000 00000000   0 0 0 1"), "tcp",
            new Dictionary<long, SocketOwner> { [2] = new("plex", containerId) }).ToArray();
        Check(owned.SequenceEqual([new NodeListener("tcp", "127.0.0.1", 53, "kernel"), new NodeListener("tcp", "0.0.0.0", 32400, "plex", containerId),
            new NodeListener("tcp", "0.0.0.0", 2049, "kernel")]), "Listener owners must come from the socket inode; unowned sockets are the kernel's.");
        Check(StackRunner.CgroupContainer($"0::/system.slice/docker-{containerId}.scope\n") == containerId && StackRunner.CgroupContainer($"12:pids:/docker/{containerId}\n") == containerId
            && StackRunner.CgroupContainer("0::/system.slice/ssh.service\n") is null, "Container cgroups were not recognized.");

        var ps = """
            {"Command":"\"/init\"","ID":"%ID%","Image":"lscr.io/linuxserver/sonarr:latest","Labels":"com.docker.compose.project=lucia-media,com.docker.compose.service=sonarr,org.opencontainers.image.title=sonarr","Names":"lucia-media-sonarr-1","Ports":"0.0.0.0:8989->8989/tcp","State":"running","Status":"Up 3 hours (healthy)"}
            {"ID":"%ID2%","Image":"hello-world","Labels":"","Names":"eager_turing","Ports":"","State":"exited","Status":"Exited (137) 2 days ago"}
            not json
            {"ID":"../../etc","Image":"x","Names":"bad","State":"running"}
            """.Replace("%ID%", new string('a', 64)).Replace("%ID2%", new string('b', 64));
        var containers = StackRunner.ParseContainers(ps);
        Check(containers.Length == 2 && containers[0] is { Project: "lucia-media", Service: "sonarr", State: "running", Ports: "0.0.0.0:8989->8989/tcp" }
            && containers[1] is { Project: null, Service: null, Name: "eager_turing" }, "docker ps parsing lost or invented container fields.");
        Check(StackRunner.Health(containers[0].Status) == "healthy" && StackRunner.ExitCode(containers[1].Status) == 137
            && StackRunner.ExitCode(containers[0].Status) is null, "Container health or exit code parsing is wrong.");

        var composeConfig = """
            {"name":"lucia-media","services":{},"volumes":{"config":{"name":"lucia-media_config"},"shared":{"name":"shared","external":true},
             "nfs":{"name":"lucia-media_nfs","driver":"local","driver_opts":{"type":"nfs"}},"plugin":{"name":"p","driver":"rexray"}}}
            """;
        var redirect = StackRunner.VolumeOverride(composeConfig, "/srv/lucia/stacks/media");
        Check(redirect is { } value && value.Paths.SequenceEqual(["/srv/lucia/stacks/media/volumes/config"])
            && System.Text.Json.JsonDocument.Parse(value.Json).RootElement.GetProperty("volumes").EnumerateObject().Select(item => item.Name)
                .SequenceEqual(["config"])
            && value.Json.Contains("\"device\":\"/srv/lucia/stacks/media/volumes/config\"", StringComparison.Ordinal),
            "Only plain named volumes may be redirected to the stack directory.");
        Check(StackRunner.VolumeOverride("""{"services":{}}""", "/srv/lucia/stacks/x") is null, "A stack without volumes needs no override.");

        var snapshotId = new string('d', 64);
        var backupOutput = """
            {"message_type":"verbose_status","action":"new","item":"/srv/lucia/stacks/ai/data/x"}
            {"message_type":"summary","files_new":3,"data_added":1234,"total_bytes_processed":98765,"snapshot_id":"%ID%"}
            """.Replace("%ID%", snapshotId);
        Check(ResticBackups.ParseSummary(backupOutput) == (snapshotId, 1234L, 98765L) && ResticBackups.ParseSummary("{\"message_type\":\"status\"}") is null
            && ResticBackups.ParseSummary("""{"message_type":"summary","snapshot_id":"../x"}""") is null,
            "restic's backup summary must yield the snapshot it made, and nothing else.");
        var listed = ResticBackups.ParseSnapshots("""
            [{"time":"2025-06-02T03:00:12.5-05:00","hostname":"lucialab01","tags":["lucia:stack=ai","lucia:version=1"],"id":"%ID%","summary":{"total_bytes_processed":5000}},
             {"time":"2025-06-02T03:00:12Z","hostname":"laptop","tags":["personal"],"id":"%OTHER%"},
             {"time":"2025-06-02T03:00:12Z","hostname":"x","tags":["lucia:stack=../etc"],"id":"%OTHER%"}]
            """.Replace("%ID%", snapshotId).Replace("%OTHER%", new string('e', 64)));
        Check(listed is [{ Stack: "ai", Host: "lucialab01", Size: 5000 } only] && only.Id == snapshotId && only.Time == DateTimeOffset.Parse("2025-06-02T08:00:12.5Z"),
            "Only Lucia's own snapshots, with their stack, host, time and size, may be listed.");
        var excludes = ResticBackups.Excludes("/srv/lucia/stacks/ai", ["volumes/models", "../etc", "volumes/*", "/abs"]);
        Check(excludes.Contains("/srv/lucia/stacks/ai/volumes/models") && excludes.Contains("/srv/lucia/stacks/ai/.lucia-applied.json")
            && !excludes.Any(path => path.Contains("..", StringComparison.Ordinal) || path.Contains('*') || path.EndsWith("//abs", StringComparison.Ordinal))
            && !excludes.Contains("/srv/lucia/stacks/ai/.env"),
            "Backups must skip Lucia's bookkeeping and the app's declared caches, and nothing outside the app.");

        var links = StackAddresses.ParseLinks("""
            [{"ifname":"lo","addr_info":[{"local":"127.0.0.1","prefixlen":8}]},
             {"ifname":"eno1","addr_info":[{"local":"192.168.0.241","prefixlen":23}]},
             {"ifname":"docker0","addr_info":[{"local":"172.17.0.1","prefixlen":16}]},
             {"ifname":"wlan0"}]
            """);
        Check(StackAddresses.Network(links, "192.168.1.230") == ("eno1", 23) && StackAddresses.Network(links, "192.168.2.5") is null
            && StackAddresses.Network(links, "172.17.0.9") is null && StackAddresses.Network(links, "192.168.1.255") is null
            && StackAddresses.Network(links, "192.168.0.0") is null,
            "An app address must go on the physical network whose subnet holds it, never on Docker's bridge or a subnet edge.");
        Check(StackAddresses.Valid("192.168.1.230") is not null && StackAddresses.Valid("8.8.8.8") is null && StackAddresses.Valid("192.168.1.230; x") is null
            && StackAddresses.Valid("::1") is null, "Only private dotted-quad IPv4 app addresses may reach ip or arping.");

        var nfs = new NodeMount("home-nas", "Media-4K", "nfs", "192.168.0.172:/var/nfs/shared/Media");
        var smb = new NodeMount("office", "Photos", "smb", "//nas.lan/Photos", "zack", "p@ss word");
        Check(NasMounts.UnitName(nfs) == @"mnt-lucia-nas-home\x2dnas-Media\x2d4K.mount", "NAS unit names must follow systemd's path escaping.");
        var nfsUnit = NasMounts.UnitText(nfs);
        var smbUnit = NasMounts.UnitText(smb);
        Check(nfsUnit.StartsWith("# Managed by Lucia.", StringComparison.Ordinal) && nfsUnit.Contains("\nWhat=192.168.0.172:/var/nfs/shared/Media\n", StringComparison.Ordinal)
            && nfsUnit.Contains("\nWhere=/mnt/lucia/nas/home-nas/Media-4K\n", StringComparison.Ordinal) && nfsUnit.Contains("\nType=nfs\n", StringComparison.Ordinal)
            && nfsUnit.Contains("\nWantedBy=remote-fs.target\n", StringComparison.Ordinal) && nfsUnit.Contains("nofail", StringComparison.Ordinal)
            && smbUnit.Contains("\nType=cifs\n", StringComparison.Ordinal) && smbUnit.Contains("credentials=/etc/lucia/nas/office.credentials", StringComparison.Ordinal)
            && !smbUnit.Contains("p@ss", StringComparison.Ordinal), "NAS mount units are wrong or leak the SMB password.");
        Check(NasMounts.Valid(nfs) && NasMounts.Valid(smb)
            && !NasMounts.Valid(nfs with { Source = "192.168.0.172:/var/../etc" }) && !NasMounts.Valid(nfs with { Source = "h:/a\nOptions=x" })
            && !NasMounts.Valid(nfs with { Share = "../x" }) && !NasMounts.Valid(nfs with { Nas = "Bad" }) && !NasMounts.Valid(nfs with { Password = "x" })
            && !NasMounts.Valid(smb with { Password = "a\nb" }) && !NasMounts.Valid(smb with { Source = "//nas.lan/a b" })
            && !NasMounts.Valid(smb with { Password = null }) && !NasMounts.Valid(nfs with { Kind = "ftp" }), "Unsafe NAS mounts must be refused.");
        Check(NasMounts.Unmounted("services: {}") is null
            && NasMounts.Unmounted("volumes:\n  - /mnt/lucia/nas/unas/Media/Movies:/movies:ro") == "unas/Media", "Compose NAS paths must be found.");

        var merged = NodeRequests.MergeLogs("2024-05-01T10:00:00.5Z out two\n2024-05-01T10:00:00.123456789Z \u001b[32mout one\u001b[0m\n",
            "2024-05-01T10:00:00.2Z err\u0007 one\n");
        Check(merged == "2024-05-01T10:00:00.123456789Z out one\n2024-05-01T10:00:00.2Z err one\n2024-05-01T10:00:00.5Z out two",
            "Log merging must order by timestamp and strip terminal escapes.");
        var refused = await NodeRequests.AnswerAsync(new(Guid.NewGuid(), "exec", "plex", 10), CancellationToken.None);
        var unsafeName = await NodeRequests.AnswerAsync(new(Guid.NewGuid(), "logs", "-f", 10), CancellationToken.None);
        Check(refused is { Success: false } && unsafeName is { Success: false }, "The node accepted a request outside the allowlist.");

        if (OperatingSystem.IsLinux())
            count += await DiskChecksAsync(fixture, report, plan, credentials, grant, challenge, identity);
        else
            Console.WriteLine("SKIP: Linux disk/proc and private-file checks require Linux; portable checks never invoke live installation.");
        Console.WriteLine($"PASS: {count} installation and managed-node isolated checks.");
        return count;
    }

    [SupportedOSPlatform("linux")]
    private static async Task<int> DiskChecksAsync(string fixture, HardwareReport report, InstallPlan plan, DiscoveryCredentials credentials,
        InstallationGrant grant, DiscoveryChallenge challenge, ECDsa identity)
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); count++; }
        void Reject(Action action, string message)
        {
            try { action(); } catch (NodeAgentException) { count++; return; }
            throw new InvalidOperationException(message);
        }
        void Write(string path, string value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }
        var root = Path.Combine(fixture, "safety");
        var roots = new InventoryRoots(Path.Combine(root, "proc"), Path.Combine(root, "sys"), Path.Combine(root, "dev"));
        var sysDisk = Path.Combine(roots.Sys, "devices", "fixture", "sda");
        var sysChild = Path.Combine(sysDisk, "sda1");
        Write(Path.Combine(sysDisk, "dev"), "8:0");
        Write(Path.Combine(sysDisk, "ro"), "0");
        Write(Path.Combine(sysChild, "dev"), "8:1");
        Write(Path.Combine(sysChild, "ro"), "0");
        Write(Path.Combine(sysChild, "partition"), "1");
        Directory.CreateDirectory(Path.Combine(sysDisk, "holders"));
        Directory.CreateDirectory(Path.Combine(sysChild, "holders"));
        Directory.CreateDirectory(Path.Combine(roots.Sys, "class", "block"));
        Directory.CreateSymbolicLink(Path.Combine(roots.Sys, "class", "block", "sda"), sysDisk);
        Directory.CreateSymbolicLink(Path.Combine(roots.Sys, "class", "block", "sda1"), sysChild);
        Directory.CreateDirectory(Path.Combine(roots.Dev, "disk", "by-id"));
        Write(Path.Combine(roots.Dev, "sda"), "");
        Write(Path.Combine(roots.Dev, "sda1"), "");
        var alias = Path.Combine(roots.Dev, "disk", "by-id", "ata-fixture");
        File.CreateSymbolicLink(alias, Path.Combine(roots.Dev, "sda"));
        var mounts = Path.Combine(roots.Proc, "self", "mountinfo");
        const string baseMounts = "1 0 0:1 / / rw - tmpfs tmpfs rw\n";
        Write(mounts, baseMounts);
        var swaps = Path.Combine(roots.Proc, "swaps");
        Write(swaps, "Filename Type Size Used Priority\n");
        Write(Path.Combine(roots.Proc, "sys", "kernel", "random", "boot_id"), plan.BootId);
        string Number(string path)
        {
            var actual = new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path;
            return Path.GetFileName(actual) switch { "sda" => "8:0", "sda1" => "8:1", _ => throw new NodeAgentException("Fixture block mismatch.") };
        }
        var safety = new DiskSafety(roots, Number);
        Check(safety.VerifyUnused(report, plan.DiskId) == "8:0" && safety.BootId() == plan.BootId, "Unused fixture disk failed.");
        Write(mounts, baseMounts + "2 1 8:1 / /cdrom ro - ext4 /dev/sda1 ro\n");
        Reject(() => safety.VerifyUnused(report, plan.DiskId), "Installer child mount passed unused guard.");
        Write(mounts, baseMounts);
        Write(swaps, "Filename Type Size Used Priority\n/dev/sda1 partition 100 0 -2\n");
        Reject(() => safety.VerifyUnused(report, plan.DiskId), "Active child swap passed unused guard.");
        Write(swaps, "Filename Type Size Used Priority\n");
        Write(Path.Combine(sysChild, "holders", "dm-0"), "");
        Reject(() => safety.VerifyUnused(report, plan.DiskId), "Child holder passed unused guard.");
        File.Delete(Path.Combine(sysChild, "holders", "dm-0"));
        File.Delete(alias);
        File.CreateSymbolicLink(alias, Path.Combine(roots.Dev, "sda1"));
        Reject(() => safety.VerifyUnused(report, plan.DiskId), "Repointed by-id alias passed.");
        File.Delete(alias);
        File.CreateSymbolicLink(alias, Path.Combine(roots.Dev, "sda"));
        Write(mounts, baseMounts + "2 1 8:1 / /target rw - ext4 /dev/sda1 rw\n");
        safety.VerifyInstalledMount(report, plan, "/target");
        count++;
        Write(mounts, baseMounts + "2 1 8:1 / /target rw - ext4 /dev/sda1 rw\n3 2 0:5 / /target/etc rw - tmpfs tmpfs rw\n");
        Reject(() => safety.VerifyInstalledMount(report, plan, "/target"), "Overmounted target etc passed.");
        Write(mounts, baseMounts + "2 1 8:1 /subdir /target rw - ext4 /dev/sda1 rw\n");
        Reject(() => safety.VerifyInstalledMount(report, plan, "/target"), "Target subdirectory bind passed.");
        Write(mounts, baseMounts);
        Reject(() => safety.VerifyInstalledMount(report, plan, "/target"), "Unmounted target passed.");

        var runtime = Path.Combine(root, "runtime");
        Directory.CreateDirectory(runtime);
        const string library = "libstdc++.so.6.0.33";
        const string aliasName = "libstdc++.so.6";
        var libraryBytes = Encoding.UTF8.GetBytes("fixture library bytes, never executed");
        File.WriteAllBytes(Path.Combine(runtime, library), libraryBytes);
        File.CreateSymbolicLink(Path.Combine(runtime, aliasName), library);
        Check(ManagedFiles.ResolveRuntimeFile(runtime, library) == Path.Combine(runtime, library),
            "A regular plus-name library changed its source path.");
        Check(ManagedFiles.ResolveRuntimeFile(runtime, aliasName) == Path.Combine(runtime, library),
            "The real relative library alias did not resolve within the flat runtime.");
        foreach (var (name, destination) in new[]
        {
            ("absolute.so", Path.Combine(runtime, library)),
            ("escape.so", "../system/nss"),
            ("nested.so", "child/" + library),
            ("chain.so", aliasName),
            ("self.so", "self.so")
        })
        {
            File.CreateSymbolicLink(Path.Combine(runtime, name), destination);
            Reject(() => ManagedFiles.ResolveRuntimeFile(runtime, name),
                "An absolute, escaping, nested or chained runtime alias was accepted.");
        }
        Reject(() => ManagedFiles.ResolveRuntimeFile(runtime, "../system/nss"), "Runtime basename escaped its flat source directory.");

        // Native state remains fixture-only. The live staging/configuration entrypoints are never called.
        var statePath = Path.Combine(root, "private");
        SecureStateDirectory state;
        try { state = new(statePath); }
        catch (NodeAgentException)
        {
            Console.WriteLine("SKIP: installation persistence checks require safe local Linux fixture storage.");
            return count;
        }
        using (state)
        {
            state.WriteJson("install-plan.json", plan);
            state.WriteJson("grant.json", grant);
            state.WritePrivate("identity.pem", Encoding.UTF8.GetBytes(identity.ExportPkcs8PrivateKeyPem()));
            using var key = state.LoadExistingKey();
            Check(key.ExportSubjectPublicKeyInfoPem() == identity.ExportSubjectPublicKeyInfoPem(), "Installation identity changed.");
            var restored = state.ReadJson<InstallPlan>("install-plan.json")!;
            Check(restored.DeviceId == plan.DeviceId && restored.Inventory.Disks.Single() == report.Disks.Single(), "Plan persistence lost disk binding.");
            Check(File.GetUnixFileMode(Path.Combine(statePath, "install-plan.json")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Plan is not private.");
            using (state.Lock())
            {
                using var second = new SecureStateDirectory(statePath);
                Reject(() => second.Lock(), "Concurrent state operation was allowed.");
            }
            state.WritePrivate("node.csr", Encoding.UTF8.GetBytes(ManagedIdentity.CreateCsr(plan, key)));
            var storedCsr = Encoding.UTF8.GetString(state.ReadPrivate("node.csr")!);
            ManagedIdentity.ValidateCsr(storedCsr, plan, key);
            count++;
            File.CreateSymbolicLink(Path.Combine(statePath, "managed-configuration.json"), Path.Combine(statePath, "identity.pem"));
            Reject(() => state.ReadJson<ManagedConfiguration>("managed-configuration.json"), "Symlink managed configuration passed.");
            Reject(() => state.WritePrivate("../escaped", [1]), "Private name escaped descriptor root.");
            var parent = Path.Combine(root, "system");
            SecureStateDirectory.EnsureDirectory(parent);
            SecureStateDirectory.WriteSystemFile(Path.Combine(parent, "nss"), Encoding.UTF8.GetBytes("safe"), publicRead: true);
            Check(File.GetUnixFileMode(Path.Combine(parent, "nss")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead),
                "NSS/public system file is not readable by accounts.");
            SecureStateDirectory.WriteSystemFile(Path.Combine(parent, "sudoers"), Encoding.UTF8.GetBytes(ManagedFiles.OwnersSudoers), sudoers: true);
            Check(File.GetUnixFileMode(Path.Combine(parent, "sudoers")) == (UnixFileMode.UserRead | UnixFileMode.GroupRead), "Sudoers file is not 0440.");
            Check(File.ReadAllText(Path.Combine(parent, "sudoers")) == ManagedFiles.OwnersSudoers,
                "The installed owners sudo rule changed during private system-file publication.");
            File.CreateSymbolicLink(Path.Combine(parent, "bad"), Path.Combine(statePath, "identity.pem"));
            Reject(() => SecureStateDirectory.WriteSystemFile(Path.Combine(parent, "bad"), [1]), "System writer followed a symlink.");
            SecureStateDirectory.EnsureDirectory(runtime);
            SecureStateDirectory.WriteSystemFile(Path.Combine(runtime, library), libraryBytes);
            Check(ManagedFiles.ReadRuntimeFile(runtime, library).SequenceEqual(libraryBytes),
                "A regular runtime library containing plus signs was rejected.");
            var materialized = ManagedFiles.ReadRuntimeFile(runtime, aliasName);
            Check(materialized.SequenceEqual(libraryBytes), "A safe relative runtime alias did not read its regular same-directory target.");
            SecureStateDirectory.WriteSystemFile(Path.Combine(parent, aliasName), materialized);
            Check(new FileInfo(Path.Combine(parent, aliasName)).LinkTarget is null
                && File.GetUnixFileMode(Path.Combine(parent, aliasName)) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                "Runtime library alias was not materialized as a private regular file.");
            Reject(() => SecureStateDirectory.ReadSystemFile(Path.Combine(runtime, aliasName)),
                "The runtime exception weakened the common no-follow file reader.");

            Write(Path.Combine(roots.Proc, "cpuinfo"), "processor: 0\nprocessor: 1\nmodel name: CPU\n");
            Write(Path.Combine(roots.Proc, "meminfo"), "MemTotal: 8388608 kB\n");
            Directory.CreateDirectory(Path.Combine(roots.Sys, "firmware", "efi"));
            Write(Path.Combine(roots.Sys, "class", "net", "eth0", "address"), "aa:bb:cc:00:11:22");
            Write(Path.Combine(sysDisk, "size"), (report.Disks[0].SizeBytes / 512).ToString());
            Write(Path.Combine(sysDisk, "removable"), "0");
            Write(Path.Combine(sysDisk, "device", "model"), "SSD");
            Write(Path.Combine(sysDisk, "device", "serial"), "serial");
            var inspector = new HardwareInspector(roots, Architecture.X64,
                () => new Dictionary<string, string[]> { ["eth0"] = ["192.0.2.2"] });
            var exactPlan = plan with { Inventory = inspector.Inspect() };
            state.WriteJson("install-plan.json", exactPlan);
            state.SaveCredentials(credentials);
            File.Delete(Path.Combine(statePath, "grant.json"));
            var posts = 0;
            using var uncertain = new DiscoveryClient(new(credentials.Server), new InstallationHandler(request =>
            {
                if (request.Method == HttpMethod.Get) return Task.FromResult(Response(challenge));
                posts++;
                throw new HttpRequestException("never-log-private-server-body");
            }));
            var runner = new InstallationRunner(uncertain, state, inspector, safety);
            async Task MustFail()
            {
                try { await runner.GuardAsync(default); }
                catch (NodeAgentException ex)
                {
                    Check(!ex.Message.Contains("never-log", StringComparison.Ordinal), "Transport secret leaked from guard.");
                    return;
                }
                throw new InvalidOperationException("Uncertain installation guard succeeded.");
            }
            await MustFail();
            Check(posts == 1 && state.ReadPrivate("grant-attempt.json") is not null, "Grant request ID was not durable before an uncertain POST.");
            await MustFail();
            Check(posts == 1, "Second guard invocation retried an uncertain grant.");
            using var attempt = JsonDocument.Parse(state.ReadPrivate("grant-attempt.json")!);
            var requestId = attempt.RootElement.GetProperty("requestId").GetGuid();
            var acknowledgedGrant = grant with { RequestId = requestId };
            state.WriteJson("grant.json", acknowledgedGrant);
            var progressCalls = 0;
            using var acknowledged = new DiscoveryClient(new(credentials.Server), new InstallationHandler(request =>
            {
                Check(request.RequestUri!.AbsolutePath.EndsWith("/progress", StringComparison.Ordinal),
                    "Guard with an existing grant requested new disk authority.");
                progressCalls++;
                return Task.FromResult(Response(new { accepted = true }));
            }));
            runner = new(acknowledged, state, inspector, safety);
            await runner.GuardAsync(default);
            Check(progressCalls == 1 && state.ReadPrivate("installation-started.json") is not null,
                "Installing progress was not acknowledged before the guard succeeded.");
            await runner.GuardAsync(default);
            Check(progressCalls == 2, "Repeated valid guard did not preserve its existing grant.");
            state.WriteJson("grant.json", acknowledgedGrant with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
            await MustFail();
            Check(progressCalls == 2, "Expired guard contacted the server or authorized another installation.");
            state.WriteJson("grant.json", acknowledgedGrant);
            Write(Path.Combine(sysDisk, "device", "serial"), "replaced");
            await MustFail();
            Check(progressCalls == 2, "Changed disk with stored grant bypassed inventory validation.");
        }
        await Task.CompletedTask;
        return count;
    }

    private static string RecoveryKey() => WireKey("ssh-ed25519", RandomNumberGenerator.GetBytes(32));

    private static string WireKey(string algorithm, params byte[][] fields)
    {
        using var stream = new MemoryStream();
        foreach (var bytes in new[] { Encoding.ASCII.GetBytes(algorithm) }.Concat(fields))
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }
        return algorithm + " " + Convert.ToBase64String(stream.ToArray());
    }

    private static X509Certificate2 MakeAuthority(ECDsa key)
    {
        var request = new CertificateRequest("CN=Node managed fixture CA", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(20));
    }

    private static X509Certificate2 MakeLeaf(X509Certificate2 ca, ECDsa key, InstallPlan plan, bool expired = false, bool? isCa = false, bool extraSan = false)
    {
        var request = new CertificateRequest("CN=" + plan.DeviceId.ToString("D"), key, HashAlgorithmName.SHA256);
        if (isCa is { } authority) request.CertificateExtensions.Add(new X509BasicConstraintsExtension(authority, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(plan.Hostname);
        if (extraSan) san.AddDnsName("other");
        request.CertificateExtensions.Add(san.Build());
        return request.Create(ca, expired ? DateTimeOffset.UtcNow.AddDays(-2) : DateTimeOffset.UtcNow.AddMinutes(-5),
            expired ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddHours(24), RandomNumberGenerator.GetBytes(16));
    }

    private static X509Certificate2 MakeIntermediate(X509Certificate2 ca, ECDsa key)
    {
        var request = new CertificateRequest("CN=Fixture native intermediate", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var certificate = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow.AddDays(5),
            RandomNumberGenerator.GetBytes(16));
        return certificate.CopyWithPrivateKey(key);
    }

    private static HttpResponseMessage Response<T>(T value, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(value, AgentJson.Options), Encoding.UTF8, "application/json") };

    private sealed class InstallationHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return send(request);
        }
    }
}
