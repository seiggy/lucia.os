using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Lucia.NodeAgent;

// Linux descriptor-relative operations prevent check-then-open symlink escapes.
// The deployment user (normally root in d-i) and root-owned ancestors are trusted.
public sealed class SecureStateDirectory : IDisposable
{
    private const int CloseOnExec = 0x80000;
    private const int NonBlock = 0x800;
    private static int DirectoryFlag => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? 0x4000 : 0x10000;
    private static int NoFollow => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;
    private readonly SafeFileHandle directory;

    public SecureStateDirectory(string path) => directory = OpenDirectory(path, privateLeaf: true, createLeaf: true);

    public ECDsa LoadOrCreateKey() => LoadKey(create: true);

    internal ECDsa LoadExistingKey() => LoadKey(create: false);

    private ECDsa LoadKey(bool create)
    {
        const string name = "identity.pem";
        var pem = Read(name, 8192, optional: create);
        if (pem is null)
        {
            using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var bytes = Encoding.UTF8.GetBytes(generated.ExportPkcs8PrivateKeyPem());
            try { WriteAtomic(name, bytes, replace: false); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            pem = Read(name, 8192, optional: false)!;
        }
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(Encoding.UTF8.GetString(pem));
            var parameters = key.ExportParameters(true);
            try
            {
                if (key.KeySize != 256 || parameters.Curve.Oid.Value != "1.2.840.10045.3.1.7" || parameters.D is null)
                    throw new CryptographicException();
            }
            finally
            {
                if (parameters.D is not null) CryptographicOperations.ZeroMemory(parameters.D);
            }
            return key;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            key.Dispose();
            throw new NodeAgentException("The stored identity is not a valid ECDSA P-256 private key. Preserve the state directory and investigate.");
        }
        finally { CryptographicOperations.ZeroMemory(pem); }
    }

    public void SaveCredentials(DiscoveryCredentials credentials)
    {
        DiscoveryClient.ValidateCredentials(credentials);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(credentials, AgentJson.Options);
        try { WriteAtomic("discovery.json", bytes, replace: true); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public DiscoveryCredentials LoadCredentials()
    {
        var bytes = Read("discovery.json", 16 * 1024, optional: false)!;
        try
        {
            var credentials = JsonSerializer.Deserialize<DiscoveryCredentials>(bytes, AgentJson.Options)
                ?? throw new JsonException();
            DiscoveryClient.ValidateCredentials(credentials);
            return credentials;
        }
        catch (JsonException)
        {
            throw new NodeAgentException("Stored discovery credentials are malformed. Preserve the state directory and investigate.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal byte[]? ReadPrivate(string name, int limit = 64 * 1024, bool optional = false)
    {
        ValidateName(name);
        if (limit is < 1 or > 128 * 1024) throw StorageError();
        return Read(name, limit, optional);
    }

    internal void WritePrivate(string name, byte[] bytes, bool replace = true)
    {
        ValidateName(name);
        if (bytes.Length is < 1 or > 128 * 1024) throw StorageError();
        WriteAtomic(name, bytes, replace);
    }

    internal T? ReadJson<T>(string name, bool optional = false) where T : class
    {
        var bytes = ReadPrivate(name, optional: optional);
        if (bytes is null) return null;
        try { return JsonSerializer.Deserialize<T>(bytes, AgentJson.Options) ?? throw new JsonException(); }
        catch (JsonException) { throw new NodeAgentException("Private installation state is malformed. Owner inspection is required."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal void WriteJson<T>(string name, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, AgentJson.Options);
        try { WritePrivate(name, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal IDisposable Lock()
    {
        // Lock the opened directory inode: no lock-file symlink or stale-PID recovery.
        if (Flock(Fd(directory), 2 | 4) != 0)
            throw new NodeAgentException("Another agent operation holds this state directory.");
        return new StateLock(directory);
    }

    private sealed class StateLock(SafeFileHandle handle) : IDisposable
    {
        public void Dispose() => Flock(Fd(handle), 8);
    }

    private static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 64 || name.Contains("..", StringComparison.Ordinal)
            || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')))
            throw StorageError();
    }

    internal static void RequireRoot()
    {
        RequireLinux();
        if (RuntimeInformation.OSArchitecture != Architecture.X64 || GetEuid() != 0)
            throw new NodeAgentException("Installation and managed operations require root on Linux x86_64.");
    }

    internal static void EnsureDirectory(string path)
    {
        using var handle = OpenDirectory(path, privateLeaf: false, createLeaf: true);
    }

    internal static void MakePrivateDirectory(string path)
    {
        using var handle = OpenDirectory(path, privateLeaf: false, createLeaf: true);
        if (Stat(handle).UserId != GetEuid() || Fchmod(Fd(handle), 0x1c0) != 0) throw StorageError();
    }

    // Root-owned and not writable by others, but readable: sshd opens authorized-keys files as the login user.
    internal static void MakeReadableDirectory(string path)
    {
        using var handle = OpenDirectory(path, privateLeaf: false, createLeaf: true);
        if (Stat(handle).UserId != GetEuid() || Fchmod(Fd(handle), 0x1ed) != 0) throw StorageError();
    }

    internal static void DeleteSystemFile(string path)
    {
        ValidatePath(path);
        using var parent = OpenDirectory(Path.GetDirectoryName(path)!, privateLeaf: false, createLeaf: false);
        if (UnlinkAt(Fd(parent), Path.GetFileName(path), 0) != 0 && Marshal.GetLastPInvokeError() != 2) throw StorageError();
    }

    internal static void WriteSystemFile(string path, byte[] bytes, bool executable = false, bool publicRead = false, bool sudoers = false)
    {
        ValidatePath(path);
        using var parent = OpenDirectory(Path.GetDirectoryName(path)!, privateLeaf: false, createLeaf: false);
        using (var existing = OpenFile(parent, Path.GetFileName(path), 0, 0, optional: true))
        {
            if (existing is not null)
            {
                var info = Stat(existing);
                if ((info.Mode & 0xf000) != 0x8000 || info.Links != 1 || info.UserId != GetEuid()
                    || (info.Mode & 0x12) != 0) throw StorageError();
            }
        }
        var staging = ".write-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var handle = OpenFile(parent, staging, 1 | 0x40 | 0x80, 0x180, optional: false)!)
            {
                if (Fchmod(Fd(handle), sudoers ? 0x120u : executable ? 0x1c0u : publicRead ? 0x1a4u : 0x180u) != 0) throw StorageError();
                using var stream = new FileStream(handle, FileAccess.Write);
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (RenameAt2(Fd(parent), staging, Fd(parent), Path.GetFileName(path), 0) != 0
                || Fsync(Fd(parent)) != 0) throw StorageError();
        }
        finally { UnlinkAt(Fd(parent), staging, 0); }
    }

    internal static byte[] ReadSystemFile(string path, int limit = 64 * 1024)
    {
        ValidatePath(path);
        using var parent = OpenDirectory(Path.GetDirectoryName(path)!, privateLeaf: false, createLeaf: false);
        using var handle = OpenFile(parent, Path.GetFileName(path), 0, 0, optional: false)!;
        CheckFilesystem(handle);
        var info = Stat(handle);
        if ((info.Mode & 0xf000) != 0x8000 || info.Links != 1 || info.Size > (ulong)limit
            || (info.Mode & 0x12) != 0 || info.UserId != GetEuid()) throw StorageError();
        return ReadHandle(handle, limit);
    }

    internal static byte[] ReadPublicCa(string path)
    {
        RequireLinux();
        ValidatePath(path);
        using var parent = OpenDirectory(Path.GetDirectoryName(path)!, privateLeaf: false, createLeaf: false);
        using var handle = OpenFile(parent, Path.GetFileName(path), 0, 0, optional: false)!;
        CheckFilesystem(handle);
        var info = Stat(handle);
        if ((info.Mode & 0xf000) != 0x8000 || info.Links != 1 || info.Size is 0 or > 64 * 1024
            || (info.Mode & 0x12) != 0 || (info.UserId != 0 && info.UserId != GetEuid()))
            throw new NodeAgentException("The public CA must be a local, regular, non-writable-by-others certificate file owned by root or the current user.");
        return ReadHandle(handle, 64 * 1024);
    }

    private byte[]? Read(string name, int limit, bool optional)
    {
        using var handle = OpenFile(directory, name, 0, 0, optional);
        if (handle is null) return null;
        CheckPrivateFile(handle, limit);
        return ReadHandle(handle, limit);
    }

    private static byte[] ReadHandle(SafeFileHandle handle, int limit)
    {
        using var stream = new FileStream(handle, FileAccess.Read);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        try
        {
            int count;
            while ((count = stream.Read(buffer, 0, Math.Min(buffer.Length, limit + 1 - (int)output.Length))) > 0)
            {
                output.Write(buffer, 0, count);
                if (output.Length > limit) throw new NodeAgentException("A local credential or CA exceeds its size limit.");
            }
            return output.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(output.GetBuffer());
        }
    }

    private void WriteAtomic(string name, byte[] bytes, bool replace)
    {
        using (var existing = OpenFile(directory, name, 0, 0, optional: true))
        {
            if (existing is not null)
            {
                CheckPrivateFile(existing, 128 * 1024);
                if (!replace) return;
            }
        }
        var staging = ".write-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var handle = OpenFile(directory, staging, 1 | 0x40 | 0x80, 0x180, optional: false)!)
            {
                if (Fchmod(Fd(handle), 0x180) != 0) throw StorageError();
                using var stream = new FileStream(handle, FileAccess.Write);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            var result = RenameAt2(Fd(directory), staging, Fd(directory), name, replace ? 0u : 1u);
            if (result != 0 && !(Marshal.GetLastPInvokeError() == 17 && !replace))
                throw StorageError();
            if (Fsync(Fd(directory)) != 0) throw StorageError();
        }
        finally { UnlinkAt(Fd(directory), staging, 0); }
    }

    private static void CheckPrivateFile(SafeFileHandle handle, int limit)
    {
        CheckFilesystem(handle);
        var info = Stat(handle);
        if ((info.Mode & 0xf000) != 0x8000 || (info.Mode & 0xfff) != 0x180
            || info.UserId != GetEuid() || info.Links != 1 || info.Size == 0 || info.Size > (ulong)limit)
            throw new NodeAgentException("Credential files must be regular, single-link files owned by the current user with mode 0600 and bounded content.");
    }

    private static SafeFileHandle OpenDirectory(string path, bool privateLeaf, bool createLeaf)
    {
        RequireLinux();
        ValidatePath(path);
        var components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (privateLeaf && components.Length == 0)
            throw new NodeAgentException("The filesystem root cannot be used as a state directory.");
        var current = Handle(Open("/", DirectoryFlag | NoFollow | CloseOnExec, 0));
        try
        {
            CheckDirectory(current, false);
            for (var index = 0; index < components.Length; index++)
            {
                var leaf = index == components.Length - 1;
                var nextFd = OpenAt(Fd(current), components[index], DirectoryFlag | NoFollow | CloseOnExec, 0);
                if (nextFd < 0 && Marshal.GetLastPInvokeError() == 2 && leaf && createLeaf)
                {
                    if (MkdirAt(Fd(current), components[index], 0x1c0) != 0 && Marshal.GetLastPInvokeError() != 17)
                        throw StorageError();
                    nextFd = OpenAt(Fd(current), components[index], DirectoryFlag | NoFollow | CloseOnExec, 0);
                }
                var next = Handle(nextFd);
                current.Dispose();
                current = next;
                CheckDirectory(current, leaf && privateLeaf);
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }

    private static void CheckDirectory(SafeFileHandle handle, bool privateLeaf)
    {
        var info = Stat(handle);
        if ((info.Mode & 0xf000) != 0x4000 || (info.Mode & 0x12) != 0
            || (info.UserId != 0 && info.UserId != GetEuid())
            || (privateLeaf && (info.UserId != GetEuid() || (info.Mode & 0xfff) != 0x1c0)))
            throw new NodeAgentException("State directory must be mode 0700, owned by the current user, with non-writable-by-others, non-symlink ancestors.");
        CheckFilesystem(handle);
    }

    private static void CheckFilesystem(SafeFileHandle handle)
    {
        if (Fstatfs(Fd(handle), out var fs) != 0
            || (ulong)fs.Type is not (0xEF53 or 0x58465342 or 0x9123683E or 0x01021994 or 0x858458F6 or 0x794c7630))
            throw new NodeAgentException("Credential and CA paths must use a local ext, XFS, Btrfs, tmpfs, ramfs or overlay filesystem; remote filesystems are rejected.");
    }

    private static SafeFileHandle? OpenFile(SafeFileHandle parent, string name, int flags, uint mode, bool optional)
    {
        var fd = OpenAt(Fd(parent), name, flags | NoFollow | CloseOnExec | NonBlock, mode);
        if (fd < 0 && optional && Marshal.GetLastPInvokeError() == 2) return null;
        return Handle(fd);
    }

    private static SafeFileHandle Handle(int fd) => fd < 0 ? throw StorageError() : new((IntPtr)fd, ownsHandle: true);
    private static int Fd(SafeFileHandle handle) => handle.DangerousGetHandle().ToInt32();

    private static FileStatus Stat(SafeFileHandle handle)
    {
        if (Statx(Fd(handle), "", 0x1000, 0x7ff, out var value) != 0 || (value.Mask & 0x20f) != 0x20f)
            throw StorageError();
        return value;
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path[0] != '/'
            || path.Contains("//", StringComparison.Ordinal) || path.Contains('\\') || path.Any(char.IsControl)
            || path.Split('/').Any(part => part is "." or "..") || path.Split('/').Length > 64)
            throw new NodeAgentException("Credential and CA paths must be bounded absolute local paths without dot segments or symlinks.");
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new NodeAgentException("Secure discovery state requires Linux x86_64 or aarch64.");
    }

    private static NodeAgentException StorageError() =>
        new("Cannot safely access local discovery state or CA. Check absolute paths, ownership, permissions, filesystem and absence of symlinks.");

    public void Dispose() => directory.Dispose();

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct FileStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public uint UserId;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(40)] public ulong Size;
    }

    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct FileSystemStatus
    {
        [FieldOffset(0)] public long Type;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directory, string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MkdirAt(int directory, string path, uint mode);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, string path, int flags, uint mask, out FileStatus stat);
    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static extern int Fstatfs(int fd, out FileSystemStatus stat);
    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int Fchmod(int fd, uint mode);
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fd);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(int oldDirectory, string oldName, int newDirectory, string newName, uint flags);
    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAt(int directory, string path, int flags);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fd, int operation);
}
