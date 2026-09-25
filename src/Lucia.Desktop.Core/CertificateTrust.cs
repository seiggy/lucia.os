using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace Lucia.Desktop.Core;

public static class CertificateTrust
{
    public static string ScopeDescription => OperatingSystem.IsWindows()
        ? "Adds this public CA to your Windows current-user Trusted Root store, not the machine-wide store. Applications using that store will trust certificates issued by it; browsers with separate stores may need a manual import."
        : OperatingSystem.IsMacOS()
            ? "Adds this public CA as a trusted root in your macOS login keychain with normal native approval. Applications using that keychain will trust certificates issued by it."
            : OperatingSystem.IsLinux()
                ? "Adds this public CA to the system-wide Linux CA store for all users, only after privileged native approval. Applications with separate certificate stores may still require a manual import."
                : "Automatic trust is unavailable on this platform. Export the public CA and explicitly import it into the desired trust store.";

    public static void Validate(InstallationResult installation)
    {
        using var certificate = ReadCertificate(installation);
    }

    public static async Task ExportAsync(InstallationResult installation, string destination,
        CancellationToken cancellationToken = default)
    {
        using var certificate = ReadCertificate(installation);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(destination);
        if (File.Exists(path))
        {
            await VerifyExportAsync(installation, path, cancellationToken).ConfigureAwait(false);
            return;
        }
        var bytes = Encoding.ASCII.GetBytes(certificate.ExportCertificatePem() + "\n");
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            await using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.Asynchronous))
            {
                await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(pending, path, overwrite: false);
            await VerifyExportAsync(installation, path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            throw new IOException("The public CA could not be exported or the destination changed. Choose a new writable certificate path.");
        }
        finally
        {
            if (File.Exists(pending)) File.Delete(pending);
        }
    }

    public static async Task<TrustResult> InstallAsync(InstallationResult installation,
        CancellationToken cancellationToken = default)
    {
        using var certificate = ReadCertificate(installation);
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(BootstrapClient.LocalStateDirectory, "certificates");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, installation.RootFingerprint.ToLowerInvariant() + ".crt");
        await ExportAsync(installation, path, cancellationToken).ConfigureAwait(false);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (InStore(certificate, StoreLocation.CurrentUser))
                    return new TrustResult(true, "The matching public CA is already in your current-user Trusted Root store.", path);
                var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "certutil.exe");
                var imported = await NativeAsync(executable, ["-user", "-addstore", "Root", path], cancellationToken).ConfigureAwait(false);
                if (imported.ExitCode == 0 && InStore(certificate, StoreLocation.CurrentUser))
                    return new TrustResult(true, "The exact public CA fingerprint was verified in your current-user Trusted Root store.", path);
                return NotInstalled(path, "Windows did not confirm the current-user root import. Import this public certificate into Current User → Trusted Root Certification Authorities, then verify its SHA-256 fingerprint.");
            }
            if (OperatingSystem.IsMacOS())
            {
                var keychain = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Keychains", "login.keychain-db");
                if (!File.Exists(keychain))
                    return NotInstalled(path, "The login keychain was not found. Import this public certificate with Keychain Access and explicitly approve its trust.");
                var imported = await NativeAsync("/usr/bin/security",
                    ["add-trusted-cert", "-r", "trustRoot", "-k", keychain, path], cancellationToken).ConfigureAwait(false);
                if (imported.ExitCode == 0)
                {
                    var stored = await NativeAsync("/usr/bin/security", ["find-certificate", "-a", "-p", keychain], cancellationToken).ConfigureAwait(false);
                    var verified = await NativeAsync("/usr/bin/security", ["verify-cert", "-c", path, "-k", keychain], cancellationToken).ConfigureAwait(false);
                    if (stored.ExitCode == 0 && verified.ExitCode == 0 && ContainsCertificate(stored.Output, certificate))
                        return new TrustResult(true, "The exact public CA fingerprint and trust were verified in your login keychain.", path);
                }
                return NotInstalled(path, "macOS did not confirm login-keychain trust. Use Keychain Access to import the exported public CA and explicitly approve trust after checking its fingerprint.");
            }
            if (OperatingSystem.IsLinux())
            {
                if (InStore(certificate, StoreLocation.LocalMachine))
                    return new TrustResult(true, "The matching public CA is already present in the system root store.", path);
                string? helper = null;
                string? anchors = null;
                string? bundle = null;
                string[] helperArguments = [];
                if (File.Exists("/usr/sbin/update-ca-certificates") && Directory.Exists("/usr/local/share/ca-certificates"))
                {
                    helper = "/usr/sbin/update-ca-certificates";
                    anchors = "/usr/local/share/ca-certificates";
                    bundle = "/etc/ssl/certs/ca-certificates.crt";
                }
                else if (File.Exists("/usr/bin/update-ca-trust") && Directory.Exists("/etc/pki/ca-trust/source/anchors"))
                {
                    helper = "/usr/bin/update-ca-trust";
                    anchors = "/etc/pki/ca-trust/source/anchors";
                    bundle = "/etc/pki/tls/certs/ca-bundle.crt";
                    helperArguments = ["extract"];
                }
                if (helper is null || !File.Exists("/usr/bin/pkexec") || !File.Exists("/usr/bin/install") ||
                    !File.Exists("/usr/bin/openssl") || !File.Exists("/bin/sh"))
                    return NotInstalled(path, "Automatic privileged trust is unavailable. Ask an administrator to import this public CA with your distribution's native CA-trust tool, after checking its SHA-256 fingerprint.");
                var target = anchors + "/lucia-" + installation.RootFingerprint.ToLowerInvariant() + ".crt";
                // One native privilege prompt; no password is collected by Lucia.
                var script = "set -eu; /usr/bin/install -m 0644 -- " + BootstrapProtocol.Quote(path) + " " +
                    BootstrapProtocol.Quote(target) + "; " + BootstrapProtocol.Quote(helper) + " " +
                    string.Join(' ', helperArguments.Select(BootstrapProtocol.Quote));
                var imported = await NativeAsync("/usr/bin/pkexec", ["/bin/sh", "-c", script], cancellationToken).ConfigureAwait(false);
                if (imported.ExitCode == 0 && File.Exists(bundle) &&
                    ContainsCertificate(await File.ReadAllTextAsync(bundle, cancellationToken).ConfigureAwait(false), certificate))
                {
                    var verified = await NativeAsync("/usr/bin/openssl", ["verify", "-CAfile", bundle!, path], cancellationToken).ConfigureAwait(false);
                    if (verified.ExitCode == 0)
                        return new TrustResult(true, "The exact public CA fingerprint and trust were verified in the system CA bundle for all users.", path);
                }
                return NotInstalled(path, "The privileged trust operation was declined, failed, or could not be verified. A public anchor file may remain in the system CA directory. Ask an administrator to inspect it and run the native CA-trust helper; do not bypass browser certificate warnings.");
            }
            return NotInstalled(path, "Import the exported public CA into your chosen trust store after checking its SHA-256 fingerprint.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return NotInstalled(path, "The native trust operation timed out and installation could not be verified. Check the exported certificate in your platform's certificate manager.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or
            System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return NotInstalled(path, "The native trust operation could not be verified. The public CA was exported; use the platform's certificate manager to check or complete installation. Do not bypass certificate warnings.");
        }
    }

    private static X509Certificate2 ReadCertificate(InstallationResult installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.RootCertificatePem is null || installation.RootCertificatePem.Length > 32768 ||
            installation.RootFingerprint is null || !Regex.IsMatch(installation.RootFingerprint, @"\A[0-9a-fA-F]{64}\z"))
            throw new InvalidOperationException("The public CA certificate or SHA-256 fingerprint is missing or malformed.");
        var pem = installation.RootCertificatePem.AsSpan().Trim();
        if (!PemEncoding.TryFind(pem, out var fields) || !pem[fields.Label].SequenceEqual("CERTIFICATE") ||
            fields.Location.GetOffsetAndLength(pem.Length) != (0, pem.Length))
            throw new InvalidOperationException("Expected exactly one public CERTIFICATE block, without private keys or other PEM content.");
        X509Certificate2? certificate = null;
        try
        {
            var der = Convert.FromBase64String(pem[fields.Base64Data].ToString());
            certificate = X509CertificateLoader.LoadCertificate(der);
            var constraints = certificate.Extensions.Cast<X509Extension>().Where(extension => extension.Oid?.Value == "2.5.29.19").ToArray();
            if (certificate.HasPrivateKey || !certificate.RawData.AsSpan().SequenceEqual(der) || constraints.Length != 1 ||
                !new X509BasicConstraintsExtension(constraints[0], constraints[0].Critical).CertificateAuthority)
                throw new InvalidOperationException("The supplied certificate is not a public CA with valid Basic Constraints.");
            if (certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                throw new InvalidOperationException("The public CA certificate is expired or not yet valid. Correct the clock or renew the Spark CA.");
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(der), Convert.FromHexString(installation.RootFingerprint)))
                throw new InvalidOperationException("The public CA fingerprint does not match the fingerprint received over verified SSH.");
            return certificate;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or InvalidOperationException)
        {
            certificate?.Dispose();
            if (exception is InvalidOperationException) throw;
            throw new InvalidOperationException("The public CA certificate could not be decoded safely.");
        }
    }

    private static async Task VerifyExportAsync(InstallationResult installation, string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length > 32768 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The certificate destination is unsafe or contains a different certificate; choose a new path.");
        var pem = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        using var certificate = ReadCertificate(installation with { RootCertificatePem = pem });
    }

    private static bool InStore(X509Certificate2 certificate, StoreLocation location)
    {
        using var store = new X509Store(StoreName.Root, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var certificates = store.Certificates;
        try
        {
            return certificates.Cast<X509Certificate2>().Any(stored => stored.RawData.AsSpan().SequenceEqual(certificate.RawData));
        }
        finally { foreach (var stored in certificates) stored.Dispose(); }
    }

    private static bool ContainsCertificate(string pem, X509Certificate2 expected)
    {
        var collection = new X509Certificate2Collection();
        try
        {
            collection.ImportFromPem(pem);
            return collection.Cast<X509Certificate2>().Any(certificate => certificate.RawData.AsSpan().SequenceEqual(expected.RawData));
        }
        finally { foreach (var certificate in collection) certificate.Dispose(); }
    }

    private static TrustResult NotInstalled(string path, string message) => new(false, message, path);

    private static async Task<(int ExitCode, string Output)> NativeAsync(string executable, string[] arguments,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true, CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new IOException("The native certificate tool could not be started.");
        process.StandardInput.Close();
        try
        {
            var output = ReadNativeAsync(process.StandardOutput, timeout.Token);
            var error = ReadNativeAsync(process.StandardError, timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false));
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static async Task<string> ReadNativeAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > 4 * 1024 * 1024) throw new IOException("The native certificate tool returned too much output.");
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }
}
