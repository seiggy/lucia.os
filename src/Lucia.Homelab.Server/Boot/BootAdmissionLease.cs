using System.Text.Json;

namespace Lucia.Homelab.Server.Boot;

public sealed class BootAdmissionLease : IDisposable
{
    private readonly string _directory;
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly object _changes = new();
    private bool _disposed;

    public BootAdmissionLease(string directory, TimeProvider clock)
    {
        BootOptions.ValidateControlDirectory(directory);
        _directory = Path.GetFullPath(directory);
        _path = Path.Combine(_directory, "admission.json");
        _clock = clock;
        RejectLinks(_directory);
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(_directory);
        else
        {
            const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            Directory.CreateDirectory(_directory, mode);
            if (File.GetUnixFileMode(_directory) != mode)
                throw new IOException("The dedicated boot control directory must use 0755 permissions; existing permissions were not changed.");
        }
        Update(null);
    }

    public void Update(DateTimeOffset? windowExpiresAt)
    {
        lock (_changes)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RejectLinks(_path);
            var now = _clock.GetUtcNow();
            if (windowExpiresAt is not { } window || window <= now)
            {
                File.Delete(_path);
                return;
            }
            var expires = now.AddSeconds(10);
            if (expires > window) expires = window;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, issuedAt = now, expiresAt = expires, windowExpiresAt = window
            });
            var temporary = Path.Combine(_directory, $".admission-{Guid.NewGuid():N}.tmp");
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                using (var file = new FileStream(temporary, options))
                {
                    file.Write(bytes);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Boot control paths must not contain symbolic links or junctions.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public void Dispose()
    {
        lock (_changes)
        {
            if (_disposed) return;
            Update(null);
            _disposed = true;
        }
    }
}
