using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Lucia.NodeAgent;

internal static class InstallationRules
{
    internal static string Hostname(string? value)
    {
        if (value is null || !Regex.IsMatch(value, @"\A[a-z][a-z0-9-]{0,61}[a-z0-9]\z|\A[a-z]\z", RegexOptions.CultureInvariant)
            || value is "localhost") throw new NodeAgentException("The approved hostname is invalid.");
        return value;
    }

    internal static string DiskId(string? value)
    {
        if (value is null || !Regex.IsMatch(value, @"\A/dev/disk/by-id/[A-Za-z0-9][A-Za-z0-9._:+-]{0,199}\z")
            || value.Contains("..", StringComparison.Ordinal) || Regex.IsMatch(value, @"-part[0-9]+\z"))
            throw new NodeAgentException("Approval requires one safe, stable whole-disk by-id path.");
        return value;
    }

    internal static string RecoveryKey(string? value)
    {
        if (value is null || value.Length > 8192 || value.Any(char.IsControl)
            || value.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new NodeAgentException("The approved recovery public key is invalid.");
        var parts = value.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] is not ("ssh-ed25519" or "ssh-rsa" or "ecdsa-sha2-nistp256")
            || parts[1].Length > 4096 || parts[1].Any(char.IsWhiteSpace))
            throw new NodeAgentException("The approved recovery public key format is unsupported.");
        try
        {
            var bytes = Convert.FromBase64String(parts[1]);
            var offset = 0;
            byte[] Field()
            {
                if (offset + 4 > bytes.Length) throw new FormatException();
                var size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
                offset += 4;
                if (size > 4096 || size > bytes.Length - offset) throw new FormatException();
                var result = bytes.AsSpan(offset, (int)size).ToArray();
                offset += (int)size;
                return result;
            }
            if (Encoding.ASCII.GetString(Field()) != parts[0]) throw new FormatException();
            switch (parts[0])
            {
                case "ssh-ed25519":
                    var edwards = Field();
                    if (edwards.Length != 32 || edwards.All(b => b == 0) || (edwards[0] == 1 && edwards.Skip(1).All(b => b == 0)))
                        throw new FormatException();
                    break;
                case "ecdsa-sha2-nistp256":
                    if (Encoding.ASCII.GetString(Field()) != "nistp256") throw new FormatException();
                    var point = Field();
                    if (point.Length != 65 || point[0] != 4) throw new FormatException();
                    using (var key = System.Security.Cryptography.ECDsa.Create(new System.Security.Cryptography.ECParameters
                    {
                        Curve = System.Security.Cryptography.ECCurve.NamedCurves.nistP256,
                        Q = new() { X = point[1..33], Y = point[33..65] }
                    })) { }
                    break;
                case "ssh-rsa":
                    BigInteger PositiveInteger()
                    {
                        var field = Field();
                        if (field.Length == 0 || (field[0] & 0x80) != 0
                            || (field[0] == 0 && (field.Length == 1 || field[1] < 0x80))) throw new FormatException();
                        return new(field, isUnsigned: false, isBigEndian: true);
                    }
                    var exponent = PositiveInteger();
                    var modulus = PositiveInteger();
                    if (exponent < 3 || exponent.IsEven || exponent >= modulus || modulus.IsEven || modulus.GetBitLength() < 2048)
                        throw new FormatException();
                    break;
            }
            if (offset != bytes.Length) throw new FormatException();
            return parts[0] + " " + Convert.ToBase64String(bytes);
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or ArgumentException)
        { throw new NodeAgentException("The approved recovery public key is malformed."); }
    }

    internal static DiskReport SelectDisk(HardwareReport report, string id)
    {
        DiskId(id);
        if (report.Architecture != "x86_64" || report.BootMode != "uefi")
            throw new NodeAgentException("Installation requires x86_64 booted in UEFI mode.");
        var matches = report.Disks.Where(d => d.Id == id).ToArray();
        if (matches.Length != 1 || matches[0].IsReadOnly || matches[0].IsRemovable || matches[0].SizeBytes < 8L * 1024 * 1024 * 1024
            || !Regex.IsMatch(matches[0].Path, @"\A/dev/[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")
            || matches[0].Path.Contains("..", StringComparison.Ordinal))
            throw new NodeAgentException("The approved disk is not a unique writable, non-removable whole disk of at least 8 GiB.");
        return matches[0];
    }

    internal static InstallPlan Approve(InstallationConfiguration config, DiscoveryCredentials credentials, HardwareReport report,
        string bootId, string deviceNumber, DateTimeOffset now)
    {
        if (!config.CanRequestInstallationGrant || config.DeviceId != credentials.DeviceId || config.InventoryRevision < 1
            || config.TaskId is null || config.TaskId == Guid.Empty || config.AuthorityExpiresAt is null
            || config.AuthorityExpiresAt <= now || config.AuthorityExpiresAt > credentials.ExpiresAt || credentials.ExpiresAt <= now)
            throw new NodeAgentException("Owner approval is absent, expired, or inconsistent. Installation is blocked.");
        SelectDisk(report, DiskId(config.DiskId));
        return new(credentials.DeviceId, config.TaskId.Value, config.InventoryRevision, config.AuthorityExpiresAt.Value,
            Hostname(config.Hostname), config.DiskId!, RecoveryKey(config.RecoveryPublicKey), bootId, report, deviceNumber);
    }

    internal static void ValidatePlan(InstallPlan plan)
    {
        if (plan.DeviceId == Guid.Empty || plan.TaskId == Guid.Empty || plan.InventoryRevision < 1
            || plan.AuthorityExpiresAt == default || !Guid.TryParseExact(plan.BootId, "D", out var boot) || boot == Guid.Empty
            || !Regex.IsMatch(plan.DeviceNumber ?? "", @"\A[0-9]{1,10}:[0-9]{1,10}\z"))
            throw new NodeAgentException("The private installation plan is invalid.");
        Hostname(plan.Hostname);
        RecoveryKey(plan.RecoveryPublicKey);
        SelectDisk(plan.Inventory, plan.DiskId);
    }

    internal static void SameDisk(InstallPlan plan, HardwareReport fresh)
    {
        ValidatePlan(plan);
        if (SelectDisk(plan.Inventory, plan.DiskId) != SelectDisk(fresh, plan.DiskId))
            throw new NodeAgentException("The current disk no longer matches the approved disk.");
    }

    internal static void ValidateGrant(InstallationGrant grant, InstallPlan plan, Guid requestId, DateTimeOffset now, bool forErase)
    {
        ValidatePlan(plan);
        if (requestId == Guid.Empty || grant.DeviceId != plan.DeviceId || grant.TaskId != plan.TaskId || grant.RequestId != requestId
            || grant.Hostname != plan.Hostname || grant.DiskId != plan.DiskId || grant.InventoryRevision != plan.InventoryRevision
            || grant.RecoveryPublicKey != plan.RecoveryPublicKey || grant.OperatingSystem != "debian-13.7"
            || grant.ExpiresAt > plan.AuthorityExpiresAt || grant.ExpiresAt == default
            || grant.ProgressExpiresAt < grant.ExpiresAt || grant.ProgressExpiresAt > plan.AuthorityExpiresAt.AddHours(2)
            || (forErase && (grant.ExpiresAt <= now || plan.AuthorityExpiresAt <= now)))
            throw new NodeAgentException("The installation grant is expired or does not exactly match the approved plan.");
    }

    internal static string Preseed(InstallPlan plan)
    {
        ValidatePlan(plan);
        if (plan.AuthorityExpiresAt <= DateTimeOffset.UtcNow)
            throw new NodeAgentException("The installation authority has expired.");
        return $$"""
            d-i debian-installer/locale string en_US.UTF-8
            d-i keyboard-configuration/xkb-keymap select us
            d-i time/zone string UTC
            d-i clock-setup/utc boolean true
            d-i netcfg/choose_interface select auto
            d-i netcfg/disable_autoconfig boolean false
            d-i netcfg/get_hostname string {{plan.Hostname}}
            d-i netcfg/hostname string {{plan.Hostname}}
            d-i netcfg/get_domain string
            d-i mirror/protocol string https
            d-i mirror/country string manual
            d-i mirror/https/hostname string deb.debian.org
            d-i mirror/https/directory string /debian
            d-i mirror/https/proxy string
            d-i mirror/suite string trixie
            d-i apt-setup/non-free-firmware boolean true
            d-i apt-setup/non-free boolean false
            d-i apt-setup/contrib boolean false
            d-i apt-setup/services-select multiselect security, updates
            d-i passwd/root-login boolean true
            d-i passwd/root-password-crypted password *
            d-i passwd/make-user boolean false
            d-i partman/early_command string /usr/lib/lucia/installation-guard
            d-i partman-auto/disk string {{plan.DiskId}}
            d-i partman-auto/method string regular
            d-i partman-partitioning/default_label string gpt
            d-i partman-partitioning/choose_label select gpt
            d-i partman-auto/expert_recipe string lucia :: 512 512 512 free $iflabel{ gpt } $reusemethod{ } method{ efi } format{ } . 4096 10000 -1 ext4 $primary{ } method{ format } format{ } use_filesystem{ } filesystem{ ext4 } mountpoint{ / } .
            d-i partman-auto/choose_recipe select lucia
            d-i partman-basicfilesystems/no_swap boolean false
            d-i partman-partitioning/confirm_write_new_label boolean true
            d-i partman/choose_partition select finish
            d-i partman/confirm boolean true
            d-i partman/confirm_nooverwrite boolean true
            d-i grub-installer/bootdev string {{plan.DiskId}}
            d-i grub-installer/only_debian boolean true
            d-i grub-installer/with_other_os boolean false
            tasksel tasksel/first multiselect standard
            d-i pkgsel/include string openssh-server sudo sssd-ldap libnss-sss libpam-sss libpam-modules ca-certificates curl firmware-realtek libssl3t64 libstdc++6 libgcc-s1 zlib1g
            d-i pkgsel/upgrade select safe-upgrade
            d-i popularity-contest/participate boolean false
            d-i preseed/late_command string /usr/lib/lucia/finish-install
            d-i finish-install/reboot_in_progress note
            """ + "\n";
    }
}
