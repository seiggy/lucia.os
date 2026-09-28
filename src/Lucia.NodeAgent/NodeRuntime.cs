using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Lucia.NodeAgent;

public sealed record GpuReport(string Vendor, string Model, long? MemoryBytes, string? ComputeCapability, string? Uuid = null);
/// <remarks><c>CudaVersion</c> is the newest CUDA runtime the loaded driver supports, not an installed toolkit.</remarks>
public sealed record RuntimeReport(string State, string? DockerVersion, string? ComposeVersion, bool GpuContainers,
    GpuReport[] Gpus, string? Message = null, string? DriverVersion = null, string? CudaVersion = null);

/// <summary>
/// Makes a managed node a container host: Debian's Docker Engine and Compose, plus NVIDIA's container toolkit from
/// NVIDIA's signed repository when an NVIDIA driver is loaded. The GPU driver itself stays owner-managed. Only fixed
/// executables and argument arrays run; nothing here comes from the server.
/// </summary>
internal static class NodeRuntime
{
    internal const string DaemonConfigPath = "/etc/docker/daemon.json";
    // Docker's default pools fall back to 192.168.0.0/16, which collides with most home networks.
    internal const string DaemonConfig = "{\n  \"default-address-pools\": [{ \"base\": \"172.16.0.0/12\", \"size\": 24 }],\n  \"log-driver\": \"local\"\n}\n";
    internal const string NvidiaKeyPath = "/etc/apt/keyrings/lucia-nvidia-container.asc";
    internal const string NvidiaSourcePath = "/etc/apt/sources.list.d/lucia-nvidia-container.list";
    internal const string NvidiaSource =
        "deb [signed-by=" + NvidiaKeyPath + "] https://nvidia.github.io/libnvidia-container/stable/deb/$(ARCH) /\n";

    private static RuntimeReport current = new("Preparing", null, null, false, []);
    internal static RuntimeReport Current => Volatile.Read(ref current);

    internal static async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await EnsureAsync(token);
                Volatile.Write(ref current, await InspectAsync(token));
            }
            catch (Exception ex) when (ex is NodeAgentException or IOException or UnauthorizedAccessException
                || ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                var message = ex is OperationCanceledException ? "Package installation timed out." : ex.Message;
                Console.Error.WriteLine("Container runtime setup failed; retrying in 15 minutes. " + message);
                var report = await InspectAsync(token);
                Volatile.Write(ref current, report with { State = "Failed", Message = Bounded(message, 512) });
            }
            await Task.Delay(TimeSpan.FromMinutes(15), token);
        }
    }

    private static async Task EnsureAsync(CancellationToken token)
    {
        var nvidia = File.Exists("/proc/driver/nvidia/version");
        SecureStateDirectory.MakeReadableDirectory("/etc/docker");
        var restart = WriteIfChanged(DaemonConfigPath, Encoding.UTF8.GetBytes(DaemonConfig));
        // Debian 13 ships the CLI separately and only recommends it; Compose needs it too.
        List<string> packages = ["docker.io", "docker-cli", "docker-compose"];
        if (nvidia)
        {
            // apt reads Signed-By keys as _apt, so the key and its directory must be world-readable.
            SecureStateDirectory.MakeReadableDirectory("/etc/apt/keyrings");
            WriteIfChanged(NvidiaKeyPath, NvidiaKey());
            WriteIfChanged(NvidiaSourcePath, Encoding.UTF8.GetBytes(NvidiaSource));
            packages.Add("nvidia-container-toolkit");
        }
        var missing = new List<string>();
        foreach (var package in packages)
            if (await InstalledVersionAsync(package, token) is null) missing.Add(package);
        if (missing.Count > 0)
        {
            Console.Error.WriteLine("Installing container runtime packages: " + string.Join(' ', missing));
            await RunAsync("/usr/bin/apt-get", ["-o", "DPkg::Lock::Timeout=600", "update"], TimeSpan.FromMinutes(10), token);
            await RunAsync("/usr/bin/apt-get", ["-o", "DPkg::Lock::Timeout=600", "-o", "Dpkg::Options::=--force-confold",
                "install", "-y", "--no-install-recommends", .. missing], TimeSpan.FromMinutes(30), token);
            // Docker looks for NVIDIA's runtime hook only when the daemon starts.
            restart = true;
        }
        if (restart)
        {
            await RunAsync("/usr/bin/systemctl", ["enable", "docker.service"], TimeSpan.FromMinutes(1), token);
            await RunAsync("/usr/bin/systemctl", ["restart", "docker.service"], TimeSpan.FromMinutes(3), token);
        }
    }

    private static async Task<RuntimeReport> InspectAsync(CancellationToken token)
    {
        string? Version(string? package) => package is null ? null : Bounded(package.Split('+', '-')[0], 64);
        var docker = await InstalledVersionAsync("docker.io", token);
        var compose = await InstalledVersionAsync("docker-compose", token);
        var toolkit = await InstalledVersionAsync("nvidia-container-toolkit", token);
        var active = (await TryCaptureAsync("/usr/bin/systemctl", ["is-active", "docker.service"], token))?.Trim() == "active";
        var smi = File.Exists("/usr/bin/nvidia-smi");
        var gpus = smi
            ? ParseNvidiaGpus(await TryCaptureAsync("/usr/bin/nvidia-smi",
                ["--query-gpu=name,memory.total,compute_cap,uuid", "--format=csv,noheader,nounits"], token) ?? "")
            : [];
        var (driver, cuda) = smi && gpus.Length > 0 ? ParseNvidiaHeader(await TryCaptureAsync("/usr/bin/nvidia-smi", [], token) ?? "") : (null, null);
        return new(active ? "Ready" : "Failed", Version(docker), Version(compose), active && toolkit is not null && gpus.Length > 0,
            gpus, active ? null : docker is null ? "Docker is not installed yet." : "The Docker service is not running.", driver, cuda);
    }

    /// <summary>Parses <c>nvidia-smi --query-gpu=name,memory.total,compute_cap,uuid --format=csv,noheader,nounits</c>.</summary>
    internal static GpuReport[] ParseNvidiaGpus(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(16).Select(line =>
        {
            var fields = line.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length != 4 || Bounded(fields[0], 128) is not { } model) return null;
            long? memory = long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var mib)
                && mib is > 0 and < 16L * 1024 * 1024 ? mib * 1024 * 1024 : null;
            var capability = System.Text.RegularExpressions.Regex.IsMatch(fields[2], @"\A[0-9]{1,2}\.[0-9]{1,2}\z") ? fields[2] : null;
            var uuid = System.Text.RegularExpressions.Regex.IsMatch(fields[3], @"\AGPU-[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}\z") ? fields[3] : null;
            return new GpuReport("nvidia", model, memory, capability, uuid);
        }).OfType<GpuReport>().ToArray();

    /// <summary>Reads the driver and newest supported CUDA version from the <c>nvidia-smi</c> banner. Drivers from 610 on
    /// print <c>KMD Version</c> and <c>CUDA UMD Version</c>; older ones print <c>Driver Version</c> and <c>CUDA Version</c>.</summary>
    internal static (string? Driver, string? Cuda) ParseNvidiaHeader(string output)
    {
        var driver = System.Text.RegularExpressions.Regex.Match(output, @"(?:KMD|Driver) Version:\s*([0-9]{2,4}(?:\.[0-9]{1,3}){1,2})\b");
        var cuda = System.Text.RegularExpressions.Regex.Match(output, @"CUDA (?:UMD )?Version:\s*([0-9]{1,2}\.[0-9]{1,2})\b");
        return (driver.Success ? driver.Groups[1].Value : null, cuda.Success ? cuda.Groups[1].Value : null);
    }

    private static byte[] NvidiaKey()
    {
        using var stream = typeof(NodeRuntime).Assembly.GetManifestResourceStream("nvidia-container.asc")
            ?? throw new NodeAgentException("The NVIDIA repository key is missing from the agent.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static bool WriteIfChanged(string path, byte[] bytes)
    {
        try
        {
            if (SecureStateDirectory.ReadSystemFile(path).AsSpan().SequenceEqual(bytes)) return false;
        }
        catch (Exception ex) when (ex is NodeAgentException or IOException) { }
        SecureStateDirectory.WriteSystemFile(path, bytes, publicRead: true);
        return true;
    }

    private static async Task<string?> InstalledVersionAsync(string package, CancellationToken token)
    {
        var output = await TryCaptureAsync("/usr/bin/dpkg-query", ["-W", "-f=${db:Status-Status} ${Version}", package], token);
        var parts = output?.Split(' ', 2);
        return parts is ["installed", { Length: > 0 } version] ? version.Trim() : null;
    }

    private static async Task<string?> TryCaptureAsync(string executable, string[] arguments, CancellationToken token)
    {
        try { return await RunAsync(executable, arguments, TimeSpan.FromSeconds(30), token, allowFailure: true); }
        catch (Exception ex) when (ex is NodeAgentException or System.ComponentModel.Win32Exception
            || ex is OperationCanceledException && !token.IsCancellationRequested) { return null; }
    }

    private static async Task<string?> RunAsync(string executable, string[] arguments, TimeSpan limit, CancellationToken token,
        bool allowFailure = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(limit);
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        start.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
        using var process = Process.Start(start) ?? throw new NodeAgentException("A container runtime command could not start.");
        process.StandardInput.Close();
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr);
            if (process.ExitCode == 0) return await stdout;
            if (allowFailure) return null;
            var detail = (await stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            throw new NodeAgentException($"'{Path.GetFileName(executable)} {arguments.LastOrDefault()}' exited with code {process.ExitCode}."
                + (Bounded(detail, 300) is { } text ? " " + text : ""));
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
            if (text.Length < 64 * 1024) text.Append(buffer, 0, Math.Min(count, 64 * 1024 - text.Length));
        return text.ToString();
    }

    private static string? Bounded(string? value, int maximum)
    {
        var clean = new string((value ?? "").Where(c => !char.IsControl(c) && !char.IsSurrogate(c)).ToArray()).Trim();
        return clean.Length == 0 ? null : clean.Length > maximum ? clean[..maximum] : clean;
    }
}
