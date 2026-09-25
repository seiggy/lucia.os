using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lucia.Desktop.Core;

internal sealed class HostPins(string directory)
{
    internal void Check(HostKeyInfo key)
    {
        Validate(key);
        var existing = Read(key);
        if (existing is null) throw new HostKeyConfirmationRequiredException(key);
        Match(existing, key);
    }

    internal void Trust(HostKeyInfo key)
    {
        Validate(key);
        var existing = Read(key);
        if (existing is not null) { Match(existing, key); return; }
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            using var file = new FileStream(PathFor(key), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(file, key with { Host = BootstrapProtocol.Host(key.Host) });
            file.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(PathFor(key)))
        {
            existing = Read(key) ?? throw new InvalidOperationException("The SSH host pin could not be saved. Check local application-data permissions.");
            Match(existing, key);
        }
    }

    private HostKeyInfo? Read(HostKeyInfo key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 4096) throw new InvalidDataException();
            var result = JsonSerializer.Deserialize<HostKeyInfo>(file) ?? throw new InvalidDataException();
            Validate(result);
            return result;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or ArgumentException)
        {
            throw new InvalidOperationException("The saved SSH host pin is unreadable. Inspect your local Lucia host-pin files; do not replace the pin without independently verifying Spark.");
        }
    }

    private string PathFor(HostKeyInfo key) => Path.Combine(directory,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{BootstrapProtocol.Host(key.Host)}:{key.Port}"))) + ".json");

    private static void Match(HostKeyInfo existing, HostKeyInfo received)
    {
        if (BootstrapProtocol.Host(existing.Host) != BootstrapProtocol.Host(received.Host) ||
            existing.Port != received.Port || existing.Algorithm != received.Algorithm ||
            existing.Fingerprint != received.Fingerprint)
            throw new InvalidOperationException("Spark's SSH host key changed. Connection blocked before authentication. Independently verify the new key and remove the old local pin manually only if the change is legitimate.");
    }

    private static void Validate(HostKeyInfo key)
    {
        ArgumentNullException.ThrowIfNull(key);
        BootstrapProtocol.Host(key.Host);
        if (key.Port is < 1 or > 65535 || key.Algorithm is null || key.Fingerprint is null ||
            !Regex.IsMatch(key.Algorithm, @"\A[A-Za-z0-9@._+-]{1,128}\z") ||
            !Regex.IsMatch(key.Fingerprint, @"\ASHA256:[A-Za-z0-9+/]{43}\z"))
            throw new ArgumentException("Invalid SSH host fingerprint.");
        if (Convert.ToBase64String(Convert.FromBase64String(key.Fingerprint[7..] + "=")).TrimEnd('=') != key.Fingerprint[7..])
            throw new ArgumentException("Invalid SSH host fingerprint.");
    }
}
