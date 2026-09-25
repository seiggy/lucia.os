using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lucia.Desktop.Core;

internal static class BootstrapProtocol
{
    internal const int MaximumResponseBytes = 1024 * 1024;

    internal static string Quote(string value)
    {
        if (value.Contains('\0'))
            throw new ArgumentException("Shell arguments cannot contain a NUL character.");
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    internal static string Host(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 253 || value != value.Trim() ||
            value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
            value.IndexOfAny(['/', '\\', '@', '%', '[', ']']) >= 0)
            throw new ArgumentException("Enter a hostname or IP address without a scheme, port, or path.");
        if (IPAddress.TryParse(value, out var address))
            return address.ToString();
        if (Uri.CheckHostName(value) != UriHostNameType.Dns ||
            !Regex.IsMatch(value, @"\A[a-zA-Z0-9](?:[a-zA-Z0-9.-]*[a-zA-Z0-9])?\z") ||
            value.Split('.').Any(label => label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-')))
            throw new ArgumentException("Enter a valid hostname or IP address.");
        return value.ToLowerInvariant();
    }

    internal static void Connection(ConnectionOptions connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Host(connection.Host);
        if (connection.Port is < 1 or > 65535)
            throw new ArgumentException("SSH port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(connection.Username) || connection.Username.Length > 128 ||
            connection.Username.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new ArgumentException("Enter a valid SSH username without whitespace.");
        if (string.IsNullOrEmpty(connection.Password) == string.IsNullOrEmpty(connection.PrivateKeyPath))
            throw new ArgumentException("Choose exactly one SSH authentication method: password or private-key file.");
        if (string.IsNullOrEmpty(connection.PrivateKeyPath) && !string.IsNullOrEmpty(connection.PrivateKeyPassphrase))
            throw new ArgumentException("A private-key passphrase requires a private-key file.");
        if (connection.Password?.Length > 4096 || connection.PrivateKeyPassphrase?.Length > 4096)
            throw new ArgumentException("SSH passwords and private-key passphrases must be at most 4096 characters.");
    }

    internal static void Setup(SetupOptions options, bool resume, bool existingOwner = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        Host(options.PublicHost);
        if (!Regex.IsMatch(options.OwnerUsername ?? "", @"\A[a-z_][a-z0-9_-]{0,31}\z"))
            throw new ArgumentException("Owner username must start with a lowercase letter or underscore and use at most 32 lowercase letters, digits, underscores, or hyphens.");
        if (!resume && string.IsNullOrEmpty(options.OwnerPassword))
            throw new ArgumentException("Enter the owner's password.");
        if (!resume && !options.VerifyOnly && !existingOwner &&
            (options.OwnerPassword!.Length is < 14 or > 1024 || options.OwnerPassword.IndexOfAny(['\r', '\n', '\0']) >= 0))
            throw new ArgumentException("A new owner's password must contain 14–1024 characters and no carriage return, newline, or NUL.");
        if (options.SudoPassword?.Length > 4096)
            throw new ArgumentException("The sudo password is too long.");
        if (options.ModelDirectory is { } modelDirectory &&
            (modelDirectory.Length > 4096 || modelDirectory == "/" || !modelDirectory.StartsWith('/') ||
             modelDirectory.StartsWith("//", StringComparison.Ordinal) || modelDirectory.Contains('\\') ||
             modelDirectory.Any(char.IsControl) || modelDirectory.Split('/').Any(part => part is "." or "..")))
            throw new ArgumentException("An existing model directory must be an absolute Linux path without traversal.");
    }

    internal static byte[] Request(SetupOptions options, HostPackageUpload? hostPackage = null) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema_version = 1,
        public_host = options.PublicHost,
        owner_username = options.OwnerUsername,
        owner_password = options.OwnerPassword,
        sudo_password = options.SudoPassword,
        install_prerequisites = options.InstallPrerequisites,
        verify_only = options.VerifyOnly,
        configure_host = options.ConfigureHost,
        model_directory = options.ModelDirectory,
        host_package = hostPackage is null ? null : new
        {
            archive_path = hostPackage.ArchivePath,
            manifest_path = hostPackage.ManifestPath,
            sha256 = hostPackage.Sha256,
            size = hostPackage.Size
        }
    });

    internal static string JobId(string value)
    {
        if (!Regex.IsMatch(value, @"\A[0-9a-f]{32}\z"))
            throw Invalid("invalid job identifier");
        return value;
    }

    internal static void ServiceUrl(string value, string host, string scheme, int? port)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != scheme || uri.Port is < 1 or > 65535 || (port is not null && uri.Port != port) ||
            !Regex.IsMatch(value, @"\A[a-z]+://(?:\[[^\]]+\]|[^/:?#@]+):[0-9]+/?\z") || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            !string.Equals(Host(uri.IdnHost.Trim('[', ']')), Host(host), StringComparison.OrdinalIgnoreCase))
            throw Invalid("the service URL does not match the reviewed host and service port");
    }

    internal static void LaunchUrl(string value, string host)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            !string.Equals(Host(uri.IdnHost.Trim('[', ']')), Host(host), StringComparison.OrdinalIgnoreCase))
            throw Invalid("the Lucia launch URL does not match the reviewed HTTPS host");
    }

    internal static JsonDocument Document(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumResponseBytes)
            throw Invalid("response is too large");
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { throw Invalid("response is not valid JSON"); }
        try
        {
            Object(document.RootElement);
            if (!document.RootElement.TryGetProperty("schema_version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var number) || number != 1)
                throw Invalid("unsupported protocol version");
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    internal static InspectionResult Inspection(string json, string connectionHost, Redactor redact)
    {
        using var document = Document(json);
        var root = document.RootElement;
        var publicHost = OptionalText(root, "public_host");
        if (publicHost is not null) Host(publicHost);
        var url = OptionalText(root, "authentik_url");
        var isInstalled = Boolean(root, "is_installed");
        if (url is not null) ServiceUrl(url, publicHost ?? connectionHost, "https", isInstalled ? null : 9443);
        var activeJob = OptionalText(root, "active_job_id");
        if (activeJob is not null) JobId(activeJob);
        var checks = Array(root, "checks", 100).Select(check =>
        {
            Object(check);
            return new ReadinessCheck(redact.Clean(Text(check, "name")), redact.Clean(Text(check, "status")),
                redact.Clean(Text(check, "message")));
        }).ToArray();
        var changes = Array(root, "planned_changes", 100).Select(change =>
        {
            if (change.ValueKind != JsonValueKind.String) throw Invalid("invalid planned change");
            return redact.Clean(change.GetString()!);
        }).ToArray();
        var hostUrl = root.TryGetProperty("host_url", out var hostProperty) && hostProperty.ValueKind != JsonValueKind.Null
            ? Text(root, "host_url") : null;
        if (hostUrl is not null) LaunchUrl(hostUrl, publicHost ?? connectionHost);
        return new InspectionResult(redact.Clean(Text(root, "hostname")), redact.Clean(Text(root, "architecture")),
            redact.Clean(Text(root, "operating_system")), redact.Clean(Text(root, "state_directory")),
            isInstalled, Boolean(root, "owner_ready"),
            OptionalText(root, "owner_username") is { } owner ? redact.Clean(owner) : null,
            publicHost, Boolean(root, "can_install"), Boolean(root, "requires_sudo"), activeJob, checks, changes, url,
            root.TryGetProperty("host_ready", out _) && Boolean(root, "host_ready"),
            root.TryGetProperty("application_ready", out _) && Boolean(root, "application_ready"),
            hostUrl, !root.TryGetProperty("host_package_required", out _) || Boolean(root, "host_package_required"),
            root.TryGetProperty("model_directory", out _) ? OptionalText(root, "model_directory") : null,
            root.TryGetProperty("active_job_configure_host", out _) && Boolean(root, "active_job_configure_host"));
    }

    internal static string Started(string json)
    {
        using var document = Document(json);
        var root = document.RootElement;
        if (Text(root, "status") != "running") throw Invalid("start did not return a running job");
        return JobId(Text(root, "job_id"));
    }

    internal static (string Status, BootstrapEvent[] Events, InstallationResult? Result, string? Error)
        Status(string json, string jobId, SetupOptions options, Redactor redact, InspectionResult? inspection = null)
    {
        using var document = Document(json);
        var root = document.RootElement;
        if (Text(root, "job_id") != JobId(jobId)) throw Invalid("job identifier mismatch");
        var status = Text(root, "status");
        if (status is not ("running" or "succeeded" or "failed")) throw Invalid("unknown job status");
        var events = Array(root, "events", 2000).Select(item =>
        {
            Object(item);
            var level = Text(item, "level");
            if (level is not ("info" or "warning" or "error")) throw Invalid("invalid event level");
            return new BootstrapEvent(redact.Clean(Text(item, "phase")), redact.Clean(Text(item, "message")), level);
        }).ToArray();
        var error = OptionalText(root, "error");
        if (!root.TryGetProperty("result", out var result)) throw Invalid("missing job result");
        if (status != "succeeded")
            return (status, events, null, error is null ? null : redact.Clean(error));
        if (error is not null) throw Invalid("successful job also reported an error");
        Object(result);
        var installation = new InstallationResult(Text(result, "authentik_url"), Text(result, "ldap_url"),
            Text(result, "owner_username"), Text(result, "root_certificate_pem"), Text(result, "root_fingerprint"),
            Boolean(result, "owner_login_verified"),
            result.TryGetProperty("host_url", out _) ? OptionalText(result, "host_url") : null,
            result.TryGetProperty("host_ready", out _) && Boolean(result, "host_ready"),
            result.TryGetProperty("application_ready", out _) && Boolean(result, "application_ready"));
        if (!installation.OwnerLoginVerified || installation.OwnerUsername != options.OwnerUsername)
            throw Invalid("the reviewed owner account has not completed a verified login");
        var hasReviewedExistingUrl = inspection is { IsInstalled: true, AuthentikUrl: not null };
        var httpsPort = 9443;
        if (hasReviewedExistingUrl)
        {
            ServiceUrl(inspection!.AuthentikUrl!, options.PublicHost, "https", null);
            httpsPort = new Uri(inspection.AuthentikUrl!).Port;
        }
        ServiceUrl(installation.AuthentikUrl, options.PublicHost, "https", httpsPort);
        ServiceUrl(installation.LdapUrl, options.PublicHost, "ldaps", hasReviewedExistingUrl ? null : 636);
        if (options.ConfigureHost)
        {
            if (!installation.HostReady || !installation.ApplicationReady || installation.HostUrl is null)
                throw Invalid("the managed host and Authentik application have not both been verified");
            LaunchUrl(installation.HostUrl, options.PublicHost);
        }
        CertificateTrust.Validate(installation);
        return (status, events, installation, null);
    }

    private static void Object(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            element.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw Invalid("expected an object with unique property names");
    }

    private static string Text(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()) || property.GetString()!.Length > 32768)
            throw Invalid($"missing or invalid {name}");
        return property.GetString()!;
    }

    private static string? OptionalText(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property)) throw Invalid($"missing {name}");
        return property.ValueKind == JsonValueKind.Null ? null : Text(root, name);
    }

    private static bool Boolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Invalid($"missing or invalid {name}");
        return property.GetBoolean();
    }

    private static JsonElement[] Array(JsonElement root, string name, int limit)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array ||
            property.GetArrayLength() > limit) throw Invalid($"missing or invalid {name}");
        return property.EnumerateArray().ToArray();
    }

    internal static InvalidOperationException Invalid(string detail) => new($"Spark returned an invalid bootstrap response: {detail}.");
}

internal sealed class Redactor
{
    private readonly string[] secrets;

    internal Redactor(ConnectionOptions connection, SetupOptions? options = null)
    {
        secrets = new[] { connection.Password, connection.PrivateKeyPassphrase, options?.OwnerPassword, options?.SudoPassword }
            .Where(value => !string.IsNullOrEmpty(value)).SelectMany(value => new[]
            {
                value!, JsonEncodedText.Encode(value!).ToString(), Uri.EscapeDataString(value!),
                Convert.ToBase64String(Encoding.UTF8.GetBytes(value!))
            }).Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length).ToArray();
    }

    internal string Clean(string text)
    {
        foreach (var secret in secrets)
            text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        if (Regex.IsMatch(text, @"(?i)(?:PRIVATE KEY|password\s*[:=]|passphrase\s*[:=]|token\s*[:=]|secret\s*[:=])"))
            return "Sensitive remote diagnostic omitted. Check the bootstrap state on Spark.";
        text = new string(text.Where(character => !char.IsControl(character) &&
            char.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.Format).ToArray());
        return text.Length > 400 ? text[..400] + "…" : text;
    }
}
