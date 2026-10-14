using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Lucia.NodeAgent;

internal static class SkillSyncChecks
{
    private static readonly SkillLimits Small = SkillCache.Skill with { Files = 4, FileBytes = 64, TotalBytes = 100 };

    internal static int Run(string fixture)
    {
        var count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            count++;
        }
        void Rejects(Action action, string message)
        {
            try { action(); }
            catch (NodeAgentException) { count++; return; }
            throw new InvalidOperationException(message);
        }

        var root = Path.Combine(fixture, "skills");
        const string name = "check-zfs";
        var hash1 = new string('a', 64);
        var hash2 = new string('b', 64);
        var good = Pack(("SKILL.md", "---\nname: check-zfs\n---\n"), ("scripts/check.py", "print(1)"), ("references/notes.md", "n"));
        var sha = Sha(good);

        // A good package lands whole, in its own hash folder, once.
        Check(SkillCache.Install(root, name, hash1, good, sha, SkillCache.Skill), "A good package wasn't installed.");
        var installed = Path.Combine(root, name, hash1);
        Check(File.ReadAllText(Path.Combine(installed, "scripts", "check.py")) == "print(1)" && File.Exists(Path.Combine(installed, "SKILL.md"))
            && File.Exists(Path.Combine(installed, "references", "notes.md")), "The package's files weren't unpacked.");
        Check(!SkillCache.Install(root, name, hash1, good, sha, SkillCache.Skill), "Installing the same hash twice did something.");
        Check(!Directory.EnumerateFileSystemEntries(root).Any(path => Path.GetFileName(path).StartsWith(".tmp-", StringComparison.Ordinal)),
            "A staging folder was left behind.");
        if (!OperatingSystem.IsWindows())
        {
            Check(File.GetUnixFileMode(Path.Combine(installed, "scripts", "check.py")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite)
                && File.GetUnixFileMode(Path.Combine(installed, "scripts")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)
                && File.GetUnixFileMode(root) == File.GetUnixFileMode(Path.Combine(installed, "scripts")), "Files must be 0600 and folders 0700.");
            var runnable = Pack(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, ("SKILL.md", "x"), ("scripts/run.sh", "echo"));
            SkillCache.Install(root, "exec-bit", hash1, runnable, Sha(runnable), SkillCache.Skill);
            Check((File.GetUnixFileMode(Path.Combine(root, "exec-bit", hash1, "scripts", "run.sh")) & UnixFileMode.UserExecute) == 0,
                "A skill script kept its execute bit.");
            SkillCache.Install(root, SkillCache.SandboxName, hash1, runnable, Sha(runnable), SkillCache.Sandbox);
            Check((File.GetUnixFileMode(Path.Combine(root, SkillCache.SandboxName, hash1, "scripts", "run.sh")) & UnixFileMode.UserExecute) != 0,
                "The sandbox runtime lost its execute bit.");
            var sandboxDir = Path.Combine(root, SkillCache.SandboxName, hash1);
            Check(File.GetUnixFileMode(sandboxDir) == (UnixFileMode)0b111_101_101 && File.GetUnixFileMode(Path.Combine(sandboxDir, "scripts")) == (UnixFileMode)0b111_101_101
                && File.GetUnixFileMode(Path.Combine(sandboxDir, "scripts", "run.sh")) == (UnixFileMode)0b111_101_101
                && (File.GetUnixFileMode(Path.Combine(sandboxDir, "SKILL.md")) & (UnixFileMode.OtherRead | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == UnixFileMode.OtherRead,
                "The sandbox runtime must be readable by the container's unprivileged user (0755 folders and executables, 0644 files).");
        }

        // The checksum Lucia declared must match the bytes, before anything is unpacked.
        Rejects(() => SkillCache.Install(root, "tampered", hash1, good, new string('0', 64), SkillCache.Skill), "A package with the wrong checksum was accepted.");
        Check(!Directory.Exists(Path.Combine(root, "tampered")), "A rejected package left a folder.");
        Rejects(() => SkillCache.Install(root, "../escape", hash1, good, sha, SkillCache.Skill), "A path-like skill name was accepted.");
        Rejects(() => SkillCache.Install(root, "Bad_Name", hash1, good, sha, SkillCache.Skill), "A bad skill name was accepted.");
        Rejects(() => SkillCache.Install(root, name, "abc", good, sha, SkillCache.Skill), "A short hash was accepted.");
        Rejects(() => SkillCache.Install(root, "huge", hash1, good, sha, SkillCache.Skill with { DownloadBytes = 10 }), "An oversized download was accepted.");

        // Unsafe contents are refused whole, whatever the checksum says.
        void Unsafe(byte[] package, string what, SkillLimits? limits = null)
        {
            var folder = "bad-" + what.Replace(' ', '-');
            Rejects(() => SkillCache.Install(root, folder, hash1, package, Sha(package), limits ?? SkillCache.Skill), what + " was accepted.");
            Check(!Directory.Exists(Path.Combine(root, folder)), what + " left files behind.");
        }
        Unsafe(PackEntries(FileEntry("SKILL.md", "x"), Link(TarEntryType.SymbolicLink, "scripts/link", "/etc/passwd")), "a symlink");
        Unsafe(PackEntries(FileEntry("SKILL.md", "x"), Link(TarEntryType.HardLink, "scripts/hard", "SKILL.md")), "a hard link");
        Unsafe(Pack(("SKILL.md", "x"), ("../escape.txt", "x")), "a dot-dot path");
        Unsafe(Pack(("SKILL.md", "x"), ("scripts/../../escape.txt", "x")), "a nested dot-dot path");
        Unsafe(Pack(("SKILL.md", "x"), ("/etc/cron.d/lucia", "x")), "an absolute path");
        Unsafe(Pack(("SKILL.md", "x"), ("scripts\\evil.sh", "x")), "a backslash path");
        Unsafe(Pack(("SKILL.md", "x"), ("C:evil.txt", "x")), "a drive path");
        Unsafe(Pack(("SKILL.md", "x"), ("./scripts/a.sh", "x")), "a dot part");
        Unsafe(Pack(("SKILL.md", "x"), ("scripts/a.sh", "x"), ("SCRIPTS/A.sh", "y")), "a case-clashing duplicate");
        Unsafe(Pack(("SKILL.md", "x"), ("scripts", "x"), ("scripts/a.sh", "y")), "a file used as a folder");
        Unsafe(Pack(("SKILL.md", "x"), ("big.bin", new string('x', 65))), "an oversized file", Small);
        Unsafe(Pack(("SKILL.md", "x"), ("a", new string('x', 40)), ("b", new string('x', 40)), ("c", new string('x', 40))), "too many bytes in total", Small);
        Unsafe(Pack(("SKILL.md", "x"), ("a", "1"), ("b", "1"), ("c", "1"), ("d", "1")), "too many files", Small);
        Unsafe(Pack(("scripts/a.py", "print(1)")), "a package without SKILL.md");
        Unsafe(PackEntries(FileEntry("SKILL.md", "x"), DirEntry("scripts/")), "a folder entry in a skill");
        Unsafe(Encoding.UTF8.GetBytes("this is not a gzip file"), "a damaged package");
        Unsafe(PackEntries(FileEntry("SKILL.md", "x"), new UstarTarEntry(TarEntryType.CharacterDevice, "dev") { DeviceMajor = 1, DeviceMinor = 3 }), "a device node");
        var bomb = Gzip(new byte[3 * 1024 * 1024]);
        Unsafe(bomb, "data outside the tar entries", Small);

        // Sandbox packages may hold folders; names still have to be plain.
        var withFolders = PackEntries(DirEntry("bin/"), FileEntry("bin/runner", "x"), FileEntry("lib/a.dll", "y"));
        Check(SkillCache.Install(root, SkillCache.SandboxName, hash2, withFolders, Sha(withFolders), SkillCache.Sandbox), "A sandbox package with folders wasn't installed.");
        Rejects(() => SkillCache.Install(root, "other", hash1, PackEntries(DirEntry("../up/")), Sha(PackEntries(DirEntry("../up/"))), SkillCache.Sandbox),
            "A sandbox folder escaped.");

        // Pruning keeps exactly what the manifest lists.
        SkillCache.Install(root, "second", hash2, good, sha, SkillCache.Skill);
        SkillCache.Install(root, name, hash2, good, sha, SkillCache.Skill);
        Directory.CreateDirectory(Path.Combine(root, ".tmp-leftover", "x"));
        Directory.CreateDirectory(Path.Combine(root, "Not_A_Skill"));
        File.WriteAllText(Path.Combine(root, "stray.txt"), "x");
        var removed = SkillCache.Prune(root, new Dictionary<string, string> { [name] = hash2, [SkillCache.SandboxName] = hash2 });
        Check(Directory.Exists(Path.Combine(root, name, hash2)) && !Directory.Exists(Path.Combine(root, name, hash1)) && !Directory.Exists(Path.Combine(root, "second"))
            && !Directory.Exists(Path.Combine(root, "exec-bit")) && Directory.Exists(Path.Combine(root, SkillCache.SandboxName, hash2))
            && !Directory.Exists(Path.Combine(root, ".tmp-leftover")) && !Directory.Exists(Path.Combine(root, "Not_A_Skill")) && !File.Exists(Path.Combine(root, "stray.txt")),
            "Pruning removed the wrong things.");
        Check(removed.Contains("second") && removed.Contains(name + "/" + hash1), "Pruning didn't report what it removed.");
        var held = SkillCache.Installed(root);
        Check(held.Count == 2 && held[name] == hash2 && held[SkillCache.SandboxName] == hash2, "Installed() disagrees with the folders.");
        SkillCache.Prune(root, new Dictionary<string, string>());
        Check(SkillCache.Installed(root).Count == 0 && SkillCache.Prune(Path.Combine(root, "missing"), new Dictionary<string, string>()).Length == 0,
            "Pruning an empty manifest should clear the cache and tolerate a missing folder.");

        // KVM needs the device and a Docker install.
        var device = Path.Combine(fixture, "kvm-device");
        File.WriteAllText(device, "");
        Check(SkillSync.Kvm(device, "27.0") && !SkillSync.Kvm(device, null) && !SkillSync.Kvm(device, "") && !SkillSync.Kvm(device + "-missing", "27.0"),
            "kvm is reported only with /dev/kvm and Docker.");
        return count;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static UnixFileMode defaultMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private static UnixFileMode Mode(UnixFileMode mode) => mode;

    private static TarEntry FileEntry(string path, string text, UnixFileMode? mode = null) =>
        new UstarTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)), Mode = mode ?? defaultMode };

    private static TarEntry DirEntry(string path) => new UstarTarEntry(TarEntryType.Directory, path);

    private static TarEntry Link(TarEntryType type, string path, string target) => new UstarTarEntry(type, path) { LinkName = target };

    private static byte[] Gzip(byte[] raw)
    {
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(raw);
        return packed.ToArray();
    }

    private static byte[] PackEntries(params TarEntry[] entries)
    {
        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Ustar, leaveOpen: true))
            foreach (var entry in entries) writer.WriteEntry(entry);
        return Gzip(tar.ToArray());
    }

    private static byte[] Pack(params (string Path, string Text)[] files) => PackEntries([.. files.Select(file => FileEntry(file.Path, file.Text))]);

    private static byte[] Pack(UnixFileMode mode, params (string Path, string Text)[] files) =>
        PackEntries([.. files.Select(file => FileEntry(file.Path, file.Text, mode))]);
}
