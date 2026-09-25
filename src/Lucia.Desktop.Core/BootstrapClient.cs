using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Lucia.Desktop.Core;

public sealed class BootstrapClient
{
    private readonly string payloadDirectory;
    private readonly string hostPayloadDirectory;
    private readonly HostPins pins;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);
    // SSH.NET takes chmod digits, not the corresponding raw POSIX bitmask.
    internal const short PrivateDirectoryMode = 700;
    internal const short PrivateFileMode = 600;

    public BootstrapClient(string? payloadDirectory = null, string? localStateDirectory = null)
    {
        this.payloadDirectory = Path.GetFullPath(payloadDirectory ?? Path.Combine(AppContext.BaseDirectory, "BootstrapPayload"));
        hostPayloadDirectory = Path.GetFullPath(Path.Combine(this.payloadDirectory, "..", "HostPayload"));
        pins = new HostPins(Path.Combine(Path.GetFullPath(localStateDirectory ?? LocalStateDirectory), "host-pins"));
    }

    internal static string LocalStateDirectory
    {
        get
        {
            var path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(path)) throw new IOException("The current user's local application-data directory is unavailable.");
            return Path.Combine(path, "Lucia", "Desktop");
        }
    }

    public void TrustHost(HostKeyInfo key) => pins.Trust(key);

    public async Task<InspectionResult> InspectAsync(ConnectionOptions connection, CancellationToken cancellationToken = default)
    {
        BootstrapProtocol.Connection(connection);
        cancellationToken.ThrowIfCancellationRequested();
        var script = LoadScript();
        var response = await ExecuteAsync(connection, ScriptCommand(script, "inspect"), null, cancellationToken).ConfigureAwait(false);
        var inspection = BootstrapProtocol.Inspection(response, connection.Host, new Redactor(connection));
        if (inspection.HostPackageRequired && inspection.ActiveJobId is null)
        {
            ReadinessCheck packageCheck;
            try
            {
                var artifact = HostArtifact.Load(hostPayloadDirectory);
                packageCheck = new("Host package", "ready", $"Linux ARM64 host and dashboard package is available ({artifact.Size / 1024 / 1024} MiB).");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
            {
                packageCheck = new("Host package", "blocked", "This desktop build is missing a valid Linux ARM64 host bundle. Use the complete desktop package before deploying Lucia.");
            }
            inspection = inspection with
            {
                Checks = [.. inspection.Checks, packageCheck],
                CanInstall = inspection.CanInstall && packageCheck.Status == "ready"
            };
        }
        return inspection;
    }

    public async Task<InstallationResult> RunAsync(ConnectionOptions connection, InspectionResult inspection,
        SetupOptions options, IProgress<BootstrapEvent> progress, CancellationToken cancellationToken = default)
    {
        BootstrapProtocol.Connection(connection);
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(progress);
        BootstrapProtocol.Setup(options, inspection.ActiveJobId is not null, inspection.OwnerReady);
        cancellationToken.ThrowIfCancellationRequested();
        var script = LoadScript();
        var redact = new Redactor(connection, options);
        var jobId = inspection.ActiveJobId;
        if (jobId is null)
        {
            if (!inspection.CanInstall)
                throw new InvalidOperationException("Spark is not ready to install. Resolve the inspection checks and inspect again.");
            progress.Report(new BootstrapEvent("upload", "Packaging the reviewed bootstrap source."));
            (byte[] Archive, string Sha256) payload;
            try { payload = await Task.Run(() => BootstrapPayload.Build(payloadDirectory, cancellationToken), cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("The bundled bootstrap payload is missing, unsafe, or unreadable. Reinstall Lucia Desktop from a complete package.");
            }
            HostArtifact? hostArtifact = null;
            if (options.ConfigureHost && inspection.HostPackageRequired)
            {
                try { hostArtifact = HostArtifact.Load(hostPayloadDirectory); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
                {
                    throw new InvalidOperationException("The Linux ARM64 host bundle is missing or invalid. Use the complete desktop package.");
                }
                progress.Report(new BootstrapEvent("upload", "Verifying the host runtime package before upload."));
                await hostArtifact.VerifyAsync(cancellationToken).ConfigureAwait(false);
            }
            var uploaded = await UploadArtifactsAsync(connection, payload.Archive, hostArtifact, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var request = BootstrapProtocol.Request(options, uploaded.HostPackage);
            try
            {
                // Once start is sent, receive its durable job ID even if monitoring is cancelled.
                var started = await ExecuteAsync(connection, ScriptCommand(script, "start", "--archive", uploaded.SourceArchive,
                    "--sha256", payload.Sha256), request, CancellationToken.None).ConfigureAwait(false);
                jobId = BootstrapProtocol.Started(started);
            }
            finally { CryptographicOperations.ZeroMemory(request); }
        }
        BootstrapProtocol.JobId(jobId);
        progress.Report(new BootstrapEvent("monitor", "Monitoring the durable Spark job. Cancelling stops monitoring, not remote work."));
        var reported = new HashSet<BootstrapEvent>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await ExecuteAsync(connection, ScriptCommand(script, "status", "--job-id", jobId),
                null, cancellationToken).ConfigureAwait(false);
            var status = BootstrapProtocol.Status(response, jobId, options, redact, inspection);
            foreach (var item in status.Events)
                if (reported.Add(item)) progress.Report(item);
            if (status.Status == "failed")
                throw new InvalidOperationException("Spark bootstrap failed. " +
                    (status.Error ?? "Inspect Spark again to review its current state and retry."));
            if (status.Status == "succeeded")
                return status.Result ?? throw BootstrapProtocol.Invalid("missing successful installation result");
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string ScriptCommand(string script, params string[] arguments) =>
        "python3 -c " + BootstrapProtocol.Quote(script) + " " + string.Join(' ', arguments.Select(BootstrapProtocol.Quote));

    private string LoadScript()
    {
        try { return BootstrapPayload.Script(payloadDirectory); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The bundled bootstrap entry point is missing, unsafe, or unreadable. Reinstall Lucia Desktop from a complete package.");
        }
    }

    internal Task<string> ExecuteAsync(ConnectionOptions connection, string commandText, byte[]? request,
        CancellationToken cancellationToken) => Task.Run(async () =>
    {
        using var privateKey = LoadKey(connection);
        using var client = new SshClient(ConnectionInfo(connection, privateKey));
        await ConnectAsync(client, connection, cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = client.CreateCommand(commandText);
            command.CommandTimeout = request is null ? CommandTimeout : TimeSpan.FromMinutes(10);
            using var registration = cancellationToken.Register(client.Dispose);
            var execution = command.ExecuteAsync();
            var output = ReadBoundedAsync(command.OutputStream, client);
            var error = ReadBoundedAsync(command.ExtendedOutputStream, client);
            if (request is not null)
            {
                using var input = command.CreateInputStream();
                await input.WriteAsync(request, CancellationToken.None).ConfigureAwait(false);
                await input.WriteAsync("\n"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
            }
            await Task.WhenAll(execution, output, error).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (command.ExitStatus != 0 || command.ExitSignal is not null)
                throw new InvalidOperationException("Spark could not run the bootstrap command. Check that Python 3 is available and inspect Spark's bootstrap state before retrying. Remote diagnostic output was withheld to protect credentials.");
            return await output.ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Monitoring stopped. Any durable job already started on Spark continues running.", cancellationToken);
        }
        catch (Exception exception) when (exception is SshException or SocketException or IOException or ObjectDisposedException or CryptographicException)
        {
            throw new InvalidOperationException("The Spark command connection failed or timed out. A started job may still be running; reconnect and inspect to resume monitoring. Check SSH access and Python 3.");
        }
    }, cancellationToken);

    internal async Task<string> UploadAsync(ConnectionOptions connection, byte[] archive, CancellationToken cancellationToken) =>
        (await UploadArtifactsAsync(connection, archive, null, null, cancellationToken).ConfigureAwait(false)).SourceArchive;

    private Task<(string SourceArchive, HostPackageUpload? HostPackage)> UploadArtifactsAsync(ConnectionOptions connection,
        byte[] archive, HostArtifact? hostArtifact, IProgress<BootstrapEvent>? progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            using var privateKey = LoadKey(connection);
            using var client = new SftpClient(ConnectionInfo(connection, privateKey)) { OperationTimeout = CommandTimeout };
            await ConnectAsync(client, connection, cancellationToken).ConfigureAwait(false);
            var step = "checking the SFTP home directory";
            try
            {
                using var registration = cancellationToken.Register(client.Dispose);
                var home = client.WorkingDirectory.TrimEnd('/');
                if (!home.StartsWith('/') || home.Length < 2 || home.Any(char.IsControl) ||
                    home.Split('/').Any(segment => segment is "." or ".."))
                    throw new InvalidDataException("SFTP did not provide a safe user-local home directory.");
                var ownerId = client.GetAttributes(home).UserId;
                step = "checking the user's cache directory";
                var cache = await EnsureDirectoryAsync(client, home, ".cache", ownerId, false, cancellationToken).ConfigureAwait(false);
                step = "securing Lucia's upload cache";
                var root = await EnsureDirectoryAsync(client, cache, "lucia-desktop", ownerId, true, cancellationToken).ConfigureAwait(false);
                step = "creating a private staging directory";
                var stage = root + "/" + Guid.NewGuid().ToString("N");
                await client.CreateDirectoryAsync(stage, cancellationToken).ConfigureAwait(false);
                client.ChangePermissions(stage, PrivateDirectoryMode);
                var mode = client.GetAttributes(stage);
                if (!HasPrivatePermissions(mode, ownerId, directory: true))
                    throw new InvalidDataException("Spark did not apply private staging-directory permissions.");
                var path = stage + "/payload.tar.gz";
                async Task Send(Stream input, string destination, long length, string label)
                {
                    step = "uploading " + label;
                    await client.UploadFileAsync(input, destination, canOverride: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                    step = "verifying " + label + " permissions";
                    client.ChangePermissions(destination, PrivateFileMode);
                    var uploaded = client.GetAttributes(destination);
                    if (!HasPrivatePermissions(uploaded, ownerId, directory: false) || uploaded.Size != length)
                        throw new InvalidDataException("The uploaded file's ownership, private permissions, or size did not match.");
                }
                using (var input = new MemoryStream(archive, writable: false))
                    await Send(input, path, archive.LongLength, "the bootstrap archive").ConfigureAwait(false);
                HostPackageUpload? hostPackage = null;
                if (hostArtifact is not null)
                {
                    progress?.Report(new BootstrapEvent("upload", $"Uploading the host and dashboard runtime ({hostArtifact.Size / 1024 / 1024} MiB). Installation has not started; an interrupted upload restarts on retry."));
                    var hostPath = stage + "/host.tar.gz";
                    var manifestPath = stage + "/host-manifest.json";
                    await using (var input = File.OpenRead(hostArtifact.ArchivePath))
                        await Send(input, hostPath, hostArtifact.Size, "the host runtime").ConfigureAwait(false);
                    await using (var input = File.OpenRead(hostArtifact.ManifestPath))
                        await Send(input, manifestPath, input.Length, "the host manifest").ConfigureAwait(false);
                    hostPackage = new HostPackageUpload(hostPath, manifestPath, hostArtifact.Sha256, hostArtifact.Size);
                }
                return (path, hostPackage);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Upload stopped. No remote worker was cancelled.", cancellationToken);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidOperationException($"Upload stopped while {step}: {exception.Message} This attempt did not start an installer job.");
            }
            catch (Exception exception) when (exception is SshException or SocketException or IOException or ObjectDisposedException or CryptographicException)
            {
                throw new InvalidOperationException($"SFTP failed while {step} ({exception.GetType().Name}). Check SFTP access, free space, and ownership of ~/.cache/lucia-desktop. This attempt did not start an installer job.");
            }
        }, cancellationToken);

    private static async Task<string> EnsureDirectoryAsync(SftpClient client, string parent, string name, int ownerId, bool applicationCache,
        CancellationToken cancellationToken)
    {
        var path = parent + "/" + name;
        Renci.SshNet.Sftp.SftpFileAttributes? attributes = null;
        await foreach (var entry in client.ListDirectoryAsync(parent, cancellationToken).ConfigureAwait(false))
            if (entry.Name == name) attributes = entry.Attributes;
        if (attributes is null)
        {
            await client.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
            client.ChangePermissions(path, PrivateDirectoryMode);
            attributes = client.GetAttributes(path);
        }
        ValidateDirectory(attributes, ownerId, applicationCache);
        if (applicationCache)
        {
            client.ChangePermissions(path, PrivateDirectoryMode);
            if (!HasPrivatePermissions(client.GetAttributes(path), ownerId, directory: true))
                throw new InvalidDataException("Lucia's upload cache could not be restricted to your SSH account.");
        }
        return path;
    }

    internal static void ValidateDirectory(Renci.SshNet.Sftp.SftpFileAttributes attributes, int ownerId, bool applicationCache)
    {
        if (!attributes.IsDirectory || attributes.IsSymbolicLink || attributes.UserId != ownerId)
            throw new InvalidDataException("The staging path must be a real directory owned by your SSH account; links and other owners are not accepted.");
        if (!applicationCache && (attributes.GroupCanWrite || attributes.OthersCanWrite))
            throw new InvalidDataException("The shared .cache directory is writable by another account. Secure that parent directory before retrying; Lucia will not change a shared directory's permissions.");
    }

    internal static bool HasPrivatePermissions(Renci.SshNet.Sftp.SftpFileAttributes attributes, int ownerId, bool directory) =>
        (directory ? attributes.IsDirectory : attributes.IsRegularFile) && !attributes.IsSymbolicLink &&
        attributes.UserId == ownerId && attributes.OwnerCanRead && attributes.OwnerCanWrite &&
        attributes.OwnerCanExecute == directory &&
        !attributes.GroupCanRead && !attributes.GroupCanWrite && !attributes.GroupCanExecute &&
        !attributes.OthersCanRead && !attributes.OthersCanWrite && !attributes.OthersCanExecute &&
        !attributes.IsUIDBitSet && !attributes.IsGroupIDBitSet && !attributes.IsStickyBitSet;

    private async Task ConnectAsync(BaseClient client, ConnectionOptions connection, CancellationToken cancellationToken)
    {
        Exception? rejected = null;
        client.HostKeyReceived += (_, args) =>
        {
            args.CanTrust = false;
            try
            {
                pins.Check(new HostKeyInfo(BootstrapProtocol.Host(connection.Host), connection.Port,
                    args.HostKeyName, "SHA256:" + Convert.ToBase64String(SHA256.HashData(args.HostKey)).TrimEnd('=')));
                args.CanTrust = true;
            }
            catch (Exception exception) { rejected = exception; }
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectionTimeout);
        try { await client.ConnectAsync(timeout.Token).ConfigureAwait(false); }
        catch (Exception) when (rejected is not null) { throw rejected; }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Connection cancelled. Existing Spark jobs continue running.", cancellationToken);
        }
        catch (SshAuthenticationException)
        {
            throw new InvalidOperationException("SSH authentication was rejected. Check the SSH username and password or private key.");
        }
        catch (Exception exception) when (exception is SshException or SocketException or IOException or OperationCanceledException or CryptographicException)
        {
            throw new InvalidOperationException("Cannot connect to Spark over SSH. Check the hostname, SSH port, network, and that the SSH server is running.");
        }
    }

    internal static ConnectionInfo ConnectionInfo(ConnectionOptions connection, PrivateKeyFile? key)
    {
        AuthenticationMethod authentication = key is null
            ? new PasswordAuthenticationMethod(connection.Username, connection.Password!)
            : new PrivateKeyAuthenticationMethod(connection.Username, key);
        return new ConnectionInfo(connection.Host, connection.Port, connection.Username, authentication)
        {
            Timeout = ConnectionTimeout,
            ChannelCloseTimeout = TimeSpan.FromSeconds(5),
            RetryAttempts = 1
        };
    }

    private static PrivateKeyFile? LoadKey(ConnectionOptions connection)
    {
        if (string.IsNullOrEmpty(connection.PrivateKeyPath)) return null;
        try
        {
            using var stream = new FileStream(connection.PrivateKeyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 1024 * 1024) throw new InvalidDataException();
            return new PrivateKeyFile(stream, connection.PrivateKeyPassphrase);
        }
        catch (Exception exception) when (exception is SshException or IOException or InvalidDataException or UnauthorizedAccessException or
            CryptographicException or FormatException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new InvalidOperationException("Cannot read the SSH private key. Check the selected file, supported key format, and passphrase.");
        }
    }

    private static Task<string> ReadBoundedAsync(Stream stream, BaseClient client) => Task.Run(async () =>
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > BootstrapProtocol.MaximumResponseBytes)
            {
                client.Dispose();
                throw BootstrapProtocol.Invalid("command output exceeded the safety limit");
            }
            output.Write(buffer, 0, count);
        }
        try { return new UTF8Encoding(false, true).GetString(output.ToArray()); }
        catch (DecoderFallbackException) { throw BootstrapProtocol.Invalid("response was not UTF-8"); }
    });
}
