using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Lucia.NodeAgent;

internal static class ManagedFiles
{
    internal const string StatePath = "/var/lib/lucia-agent/private";
    internal const string SshKeysPath = "/etc/ssh/lucia-authorized-keys";
    internal const string OwnersSudoers = "%lucia-owners ALL=(ALL:ALL) ALL\n";

    /// <summary>Validates Lucia's owner key list into authorized_keys file contents, without options.</summary>
    internal static Dictionary<string, string> SshKeyFiles(IReadOnlyDictionary<string, string[]> users)
    {
        if (users.Count > 256) throw InvalidKeys();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (user, keys) in users)
        {
            if (!SshUser(user) || keys is null || keys.Length is 0 or > 20) throw InvalidKeys();
            try { files[user] = string.Concat(keys.Select(key => InstallationRules.RecoveryKey(key) + "\n")); }
            catch (NodeAgentException) { throw InvalidKeys(); }
        }
        return files;
    }

    internal static bool SshUser(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"\A[a-z_][a-z0-9_.-]{0,31}\z") && name is not ("root" or "lucia-recovery");

    /// <summary>Makes the key directory match Lucia's list exactly; returns whether anything changed.</summary>
    internal static bool WriteSshKeys(IReadOnlyDictionary<string, string[]> users)
    {
        var files = SshKeyFiles(users);
        var changed = false;
        foreach (var (user, content) in files)
        {
            var path = SshKeysPath + "/" + user;
            var bytes = Encoding.ASCII.GetBytes(content);
            byte[]? existing = null;
            try { existing = SecureStateDirectory.ReadSystemFile(path); }
            catch (Exception ex) when (ex is NodeAgentException or IOException) { }
            if (existing is not null && existing.AsSpan().SequenceEqual(bytes)) continue;
            SecureStateDirectory.WriteSystemFile(path, bytes, publicRead: true);
            changed = true;
        }
        foreach (var path in Directory.EnumerateFiles(SshKeysPath))
        {
            var name = Path.GetFileName(path);
            if (files.ContainsKey(name) || name.StartsWith(".write-", StringComparison.Ordinal)) continue;
            SecureStateDirectory.DeleteSystemFile(SshKeysPath + "/" + name);
            changed = true;
        }
        return changed;
    }

    private static NodeAgentException InvalidKeys() => new("Lucia returned an invalid SSH key list; the installed keys were kept.");

    internal static async Task StageAsync(SecureStateDirectory source, InstallPlan plan, Uri origin, string caFile, CancellationToken token)
    {
        foreach (var directory in new[]
        {
            "/target/etc/lucia", "/target/usr/lib/lucia", "/target/usr/lib/lucia/agent",
            "/target/var/lib/lucia-agent", "/target/etc/ssh/sshd_config.d", "/target/etc/sudoers.d",
            "/target/etc/systemd/system", "/target/etc/systemd/system/multi-user.target.wants"
        }) SecureStateDirectory.EnsureDirectory(directory);
        SecureStateDirectory.MakePrivateDirectory("/target/usr/lib/lucia/agent");
        SecureStateDirectory.MakePrivateDirectory("/target/var/lib/lucia-agent");

        var runtime = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var entries = Directory.EnumerateFiles(runtime).Take(513).ToArray();
        if (entries.Length is < 5 or > 512 || Directory.EnumerateDirectories(runtime).Any()
            || !entries.Any(path => Path.GetFileName(path) == "lucia-node-agent")
            || !entries.Any(path => Path.GetFileName(path) == "libcoreclr.so")
            || !entries.Any(path => Path.GetFileName(path) == "lucia-node-agent.runtimeconfig.json"))
            throw new NodeAgentException("The complete, flat, self-contained Linux runtime is required for staging.");
        long total = 0;
        foreach (var file in entries)
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            var bytes = ReadRuntimeFile(runtime, name);
            total += bytes.Length;
            if (total > 512 * 1024 * 1024) throw new NodeAgentException("The runtime exceeds its supported size.");
            SecureStateDirectory.WriteSystemFile("/target/usr/lib/lucia/agent/" + name, bytes, executable: name is "lucia-node-agent" or "createdump");
        }
        using var target = new SecureStateDirectory("/target" + StatePath);
        foreach (var name in new[] { "identity.pem", "discovery.json", "inventory.json", "install-plan.json", "grant.json", "grant-attempt.json", "installation-started.json" })
        {
            var bytes = source.ReadPrivate(name)!;
            try
            {
                var existing = target.ReadPrivate(name, optional: true);
                if (existing is not null)
                {
                    try { if (!CryptographicOperations.FixedTimeEquals(existing, bytes)) throw new NodeAgentException("Target identity state already differs from this installation."); }
                    finally { CryptographicOperations.ZeroMemory(existing); }
                }
                target.WritePrivate(name, bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        SecureStateDirectory.WriteSystemFile("/target/etc/lucia/public-ca.crt", SecureStateDirectory.ReadPublicCa(caFile), publicRead: true);
        SecureStateDirectory.WriteSystemFile("/target/etc/hostname", Encoding.UTF8.GetBytes(InstallationRules.Hostname(plan.Hostname) + "\n"), publicRead: true);
        var hosts = Encoding.UTF8.GetString(SecureStateDirectory.ReadSystemFile("/target/etc/hosts"));
        SecureStateDirectory.WriteSystemFile("/target/etc/hosts", Encoding.UTF8.GetBytes(Hosts(hosts, plan.Hostname)), publicRead: true);
        Write("/target/etc/lucia/managed.json", System.Text.Json.JsonSerializer.Serialize(new ManagedOrigin(origin.AbsoluteUri), AgentJson.Options));
        Write("/target/etc/systemd/system/lucia-node-agent.service", ManagedIdentity.Service);

        // Executables and argument arrays are fixed; no shell or server-provided command is ever run.
        SecureStateDirectory.MakePrivateDirectory("/target/home/lucia-recovery");
        SecureStateDirectory.MakePrivateDirectory("/target/home/lucia-recovery/.ssh");
        Write("/target/home/lucia-recovery/.ssh/authorized_keys", InstallationRules.RecoveryKey(plan.RecoveryPublicKey) + "\n");
        await RunAsync("/usr/bin/in-target", ["useradd", "--no-create-home", "--home-dir", "/home/lucia-recovery", "--user-group", "--groups", "sudo", "--shell", "/bin/bash", "lucia-recovery"], token);
        await RunAsync("/usr/bin/in-target", ["usermod", "--lock", "lucia-recovery"], token);
        await RunAsync("/usr/bin/in-target", ["chown", "-R", "lucia-recovery:lucia-recovery", "/home/lucia-recovery"], token);
        SecureStateDirectory.WriteSystemFile("/target/etc/sudoers.d/lucia-recovery",
            Encoding.UTF8.GetBytes("lucia-recovery ALL=(ALL:ALL) NOPASSWD: ALL\n"), sudoers: true);
        await RunAsync("/usr/bin/in-target", ["visudo", "--check", "--file", "/etc/sudoers.d/lucia-recovery"], token);
        SecureStateDirectory.WriteSystemFile("/target/etc/sudoers.d/lucia-owners",
            Encoding.UTF8.GetBytes(OwnersSudoers), sudoers: true);
        await RunAsync("/usr/bin/in-target", ["visudo", "--check", "--file", "/etc/sudoers.d/lucia-owners"], token);
        Write("/target/etc/ssh/sshd_config.d/00-lucia.conf", ManagedIdentity.Ssh);
        // in-target bind-mounts the installer's /run over /target/run, so sshd -t looks for /run/sshd there.
        SecureStateDirectory.EnsureDirectory("/run/sshd");
        await RunAsync("/usr/bin/in-target", ["sshd", "-t"], token);
        await RunAsync("/usr/bin/in-target", ["systemctl", "enable", "lucia-node-agent.service", "ssh.service"], token);
    }

    internal static byte[] ReadRuntimeFile(string runtime, string name) =>
        SecureStateDirectory.ReadSystemFile(ResolveRuntimeFile(runtime, name), 128 * 1024 * 1024);

    internal static string ResolveRuntimeFile(string runtime, string name)
    {
        static void SafeComponent(string value)
        {
            if (value.Length is < 1 or > 160 || value == "." || value.Contains("..", StringComparison.Ordinal)
                || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-' or '+')))
                throw new NodeAgentException("The runtime contains an unsafe filename or library alias.");
        }
        SafeComponent(name);
        var path = Path.Combine(runtime, name);
        var target = new FileInfo(path).LinkTarget;
        if (target is not null)
        {
            SafeComponent(target);
            path = Path.Combine(runtime, target);
            if (new FileInfo(path).LinkTarget is not null)
                throw new NodeAgentException("Chained runtime library aliases are not supported.");
        }
        // Only runtime aliases are materialized. The shared reader still requires a
        // regular, single-link, safely owned file and rejects every symlink component.
        return path;
    }

    internal static async Task ConfigureDirectoryAsync(ManagedConfiguration config, Uri origin, CancellationToken token)
    {
        SecureStateDirectory.MakePrivateDirectory("/etc/sssd");
        // sssd's unit makes its config group-readable on every start, so replace it as a root-owned system file.
        SecureStateDirectory.WriteSystemFile("/etc/sssd/sssd.conf", Encoding.UTF8.GetBytes(ManagedIdentity.Sssd(config, origin)));
        SecureStateDirectory.WriteSystemFile("/etc/lucia/directory-ca.pem", Encoding.UTF8.GetBytes(config.CaPem), publicRead: true);
        SecureStateDirectory.MakeReadableDirectory(SshKeysPath);
        // Rewritten on every start so nodes installed by an older agent adopt the current SSH policy.
        Write("/etc/ssh/sshd_config.d/00-lucia.conf", ManagedIdentity.Ssh);
        var nss = Encoding.UTF8.GetString(SecureStateDirectory.ReadSystemFile("/etc/nsswitch.conf"));
        SecureStateDirectory.WriteSystemFile("/etc/nsswitch.conf", Encoding.UTF8.GetBytes(Nsswitch(nss)), publicRead: true);
        var session = Encoding.UTF8.GetString(SecureStateDirectory.ReadSystemFile("/etc/pam.d/common-session"));
        SecureStateDirectory.WriteSystemFile("/etc/pam.d/common-session", Encoding.UTF8.GetBytes(HomeSession(session)), publicRead: true);
        await RunAsync("/usr/sbin/pam-auth-update", ["--package", "--enable", "sss"], token);
        await RunAsync("/usr/bin/systemctl", ["enable", "sssd.service"], token);
        await RunAsync("/usr/bin/systemctl", ["restart", "sssd.service"], token);
        await VerifyOwnerGroupAsync(token);
        await RunAsync("/usr/sbin/sshd", ["-t"], token);
        await RunAsync("/usr/bin/systemctl", ["reload", "ssh.service"], token);
    }

    internal static async Task VerifyOwnerGroupAsync(CancellationToken token,
        Func<string, string[], CancellationToken, Task>? run = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        run ??= RunAsync;
        delay ??= Task.Delay;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await run("/usr/bin/getent", ["group", "lucia-owners"], timeout.Token);
                return;
            }
            catch (Exception ex) when (ex is NodeAgentException
                || (ex is OperationCanceledException && !token.IsCancellationRequested)) { }
            if (attempt < 2) await delay(TimeSpan.FromSeconds(2), token);
        }
        token.ThrowIfCancellationRequested();
        throw new NodeAgentException("Directory owner-group lookup failed. No managed heartbeat is authorized; local recovery SSH remains configured.");
    }

    internal static string Nsswitch(string content)
    {
        var lines = content.Split('\n');
        foreach (var name in new[] { "passwd", "group", "shadow" })
        {
            var matches = lines.Select((line, index) => (line, index))
                .Where(item => item.line.TrimStart().StartsWith(name + ":", StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1) throw new NodeAgentException("The installed NSS configuration is not supported.");
            var current = matches[0];
            var parts = current.line.Split('#', 2);
            if (!parts[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("sss"))
                lines[current.index] = parts[0].TrimEnd() + " sss" + (parts.Length == 2 ? " #" + parts[1] : "");
        }
        return string.Join('\n', lines);
    }

    internal static string Hosts(string content, string hostname)
    {
        InstallationRules.Hostname(hostname);
        var lines = content.Split('\n').ToList();
        var indexes = lines.Select((line, index) => (line, index)).Where(item =>
            item.line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() == "127.0.1.1")
            .Select(item => item.index).ToArray();
        if (indexes.Length > 1) throw new NodeAgentException("The installed hostname mapping is ambiguous.");
        if (indexes.Length == 1) lines[indexes[0]] = "127.0.1.1\t" + hostname;
        else lines.Add("127.0.1.1\t" + hostname);
        return string.Join('\n', lines).TrimEnd() + "\n";
    }

    internal static string HomeSession(string content)
    {
        if (content.Split('\n').Any(line => !line.TrimStart().StartsWith('#') && line.Contains("pam_mkhomedir.so", StringComparison.Ordinal)))
            return content;
        return content.TrimEnd() + "\nsession required pam_mkhomedir.so skel=/etc/skel umask=0077\n";
    }

    private static void Write(string path, string content) =>
        SecureStateDirectory.WriteSystemFile(path, Encoding.UTF8.GetBytes(content));

    private static async Task RunAsync(string executable, string[] arguments, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        start.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
        using var process = Process.Start(start) ?? throw new NodeAgentException("A fixed installed-system setup operation could not start.");
        try
        {
            // Drain output without retaining or logging command output (which could include account details).
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr);
            if (process.ExitCode != 0)
            {
                var step = Path.GetFileName(executable) == "in-target" ? arguments[0] + " " + arguments.ElementAtOrDefault(1) : Path.GetFileName(executable);
                throw new NodeAgentException($"Installed-system setup step '{step.Trim()}' exited with code {process.ExitCode}. Local owner inspection is required.");
            }
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
