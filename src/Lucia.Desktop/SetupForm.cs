using System.Net;
using System.Text.RegularExpressions;
using Lucia.Desktop.Core;

namespace Lucia.Desktop;

internal sealed class FormException(string field, string message) : ArgumentException(message)
{
    public string Field { get; } = field;
}

internal static partial class SetupForm
{
    internal static string Host(string value, string field)
    {
        var host = value.Trim();
        if (host.Length is 0 or > 253 || host.Contains('%') ||
            (!IPAddress.TryParse(host, out _) && (!HostPattern().IsMatch(host) ||
                host.Split('.').Any(label => label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-')))))
            throw new FormException(field, "Enter a DNS name or IP address, without a scheme, path, or port.");
        return host;
    }

    internal static ConnectionOptions Connection(string host, string port, string username, bool useKey,
        string password, string keyPath, string passphrase)
    {
        host = Host(host, "HostInput");
        if (!int.TryParse(port, out var number) || number is < 1 or > 65535)
            throw new FormException("PortInput", "The SSH port must be a number between 1 and 65535.");
        username = username.Trim();
        if (!SshUsernamePattern().IsMatch(username))
            throw new FormException("SshUsernameInput", "Enter your DGX OS username, without spaces or shell characters.");
        if (useKey && !File.Exists(keyPath))
            throw new FormException("KeyPathInput", "Choose an existing private key file on this computer.");
        if (!useKey && string.IsNullOrEmpty(password))
            throw new FormException("SshPasswordInput", "Enter the password for your Spark account.");
        return new ConnectionOptions
        {
            Host = host, Port = number, Username = username,
            Password = useKey ? null : password, PrivateKeyPath = useKey ? Path.GetFullPath(keyPath) : null,
            PrivateKeyPassphrase = useKey && passphrase.Length > 0 ? passphrase : null
        };
    }

    internal static SetupOptions Setup(InspectionResult inspection, string host, string username,
        string password, string confirmation, string sudoPassword, string? modelDirectory = null)
    {
        host = Host(host, "PublicHostInput");
        username = username.Trim();
        if (!OwnerPattern().IsMatch(username) || username is "root" or "admin" or "akadmin" or "authentik" or "nobody")
            throw new FormException("OwnerUsernameInput", "Use 1-32 lowercase letters, digits, underscores, or hyphens, starting with a letter. Choose a personal name, not a reserved administrator or service name.");
        if (inspection.OwnerReady && string.IsNullOrEmpty(password))
            throw new FormException("OwnerPasswordInput", "Enter your existing Lucia password to verify sign-in. It will not be changed.");
        if (!inspection.OwnerReady && (password.Length is < 14 or > 1024 || password.IndexOfAny(['\r', '\n', '\0']) >= 0))
            throw new FormException("OwnerPasswordInput", "Use a single-line password or passphrase with at least 14 characters.");
        if (!inspection.OwnerReady && password != confirmation)
            throw new FormException("ConfirmPasswordInput", "The passwords don't match. Enter the same Lucia password again.");
        if (inspection.PublicHost is not null && !string.Equals(host, inspection.PublicHost, StringComparison.OrdinalIgnoreCase))
            throw new FormException("PublicHostInput", "This installation already has an address. Changing it requires a certificate migration.");
        if (inspection.OwnerUsername is not null && username != inspection.OwnerUsername)
            throw new FormException("OwnerUsernameInput", "This installation already has an owner. Lucia will not rename or replace that account.");
        if (inspection.RequiresSudo && string.IsNullOrEmpty(sudoPassword))
            throw new FormException("SudoPasswordInput", "Enter your Spark administrator password to approve the prerequisite changes.");
        modelDirectory = string.IsNullOrEmpty(modelDirectory) ? inspection.ModelDirectory : modelDirectory;
        if (modelDirectory is not null && (modelDirectory.Length > 4096 || modelDirectory == "/" ||
            !modelDirectory.StartsWith('/') || modelDirectory.StartsWith("//", StringComparison.Ordinal) ||
            modelDirectory.Contains('\\') || modelDirectory.Any(char.IsControl) ||
            modelDirectory.Split('/').Any(part => part is "." or "..")))
            throw new FormException("ModelDirectoryInput", "Use an absolute Linux model directory, without traversal or control characters.");
        if (inspection.ModelDirectory is not null && modelDirectory != inspection.ModelDirectory)
            throw new FormException("ModelDirectoryInput", "The existing model directory is preserved. Changing it requires an explicit migration.");
        return new SetupOptions
        {
            PublicHost = host, OwnerUsername = username, OwnerPassword = password,
            InstallPrerequisites = inspection.Checks.Any(check => check.Status == "action"),
            SudoPassword = inspection.RequiresSudo ? sudoPassword : null, VerifyOnly = inspection.OwnerReady,
            ConfigureHost = true, ModelDirectory = modelDirectory
        };
    }

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?$")]
    private static partial Regex HostPattern();
    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_.-]{0,63}$")]
    private static partial Regex SshUsernamePattern();
    [GeneratedRegex(@"^[a-z][a-z0-9_-]{0,31}$")]
    private static partial Regex OwnerPattern();
}
