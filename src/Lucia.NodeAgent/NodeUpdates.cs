using System.Text.RegularExpressions;

namespace Lucia.NodeAgent;

internal sealed record PackageUpdate(string Name, string? Current, string Candidate, bool Security);
/// <param name="State">Idle, Checking, Installing or Failed; <paramref name="Message"/> says what happened last.</param>
/// <param name="Packages">The last successful check, security updates first, capped at <see cref="NodeUpdates.MaxPackages"/>.</param>
internal sealed record UpdatesReport(string State, DateTimeOffset? CheckedAt, int Count, int SecurityCount, PackageUpdate[] Packages,
    bool RestartRequired, string? Message = null);

/// <summary>
/// Debian updates and the owner's machine actions. Checks run every six hours and on request; installs, restarts and
/// agent updates run only when the owner asks. One runs at a time, with fixed executables and argument arrays.
/// </summary>
internal static partial class NodeUpdates
{
    internal const int MaxPackages = 50, MaxText = 100;
    private const string RebootRequired = "/run/reboot-required";
    private static UpdatesReport current = new("Idle", null, 0, 0, [], false);
    private static int busy;

    internal static UpdatesReport Current => Volatile.Read(ref current) with { RestartRequired = File.Exists(RebootRequired) };

    internal static async Task RunAsync(CancellationToken token)
    {
        // Leave apt to the container runtime's first setup.
        await Task.Delay(TimeSpan.FromMinutes(2), token);
        while (!token.IsCancellationRequested)
        {
            if (Begin("Checking")) await Finish(CheckAsync, token);
            await Task.Delay(TimeSpan.FromHours(6), token);
        }
    }

    /// <summary>Starts an owner action and answers at once; progress shows in the heartbeat.</summary>
    internal static NodeRequestResult Start(NodeRequest request, Func<CancellationToken, Task<string>> updateAgent, CancellationToken token)
    {
        NodeRequestResult Answer(bool success, string message) => new(request.RequestId, success, null, message);
        var (state, message, work) = request.Kind switch
        {
            "check-updates" => ("Checking", "Checking for updates.", (Func<CancellationToken, Task<string>>)CheckAsync),
            "install-updates" => ("Installing", "Installing updates. Apps may restart briefly.", InstallAsync),
            "restart" => ("Idle", "Restarting now. The server is back in a few minutes.", RestartAsync),
            "update-agent" => ("Idle", "Updating the agent. It reconnects in about a minute.", updateAgent),
            _ => (null, null, null),
        };
        if (state is null) return Answer(false, "This node doesn't accept that request.");
        if (!Begin(state)) return Answer(false, "This server is busy with another update. Try again when it finishes.");
        _ = Task.Run(() => Finish(work!, token), token);
        return Answer(true, message!);
    }

    private static bool Begin(string state)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return false;
        Volatile.Write(ref current, Volatile.Read(ref current) with { State = state });
        return true;
    }

    private static async Task Finish(Func<CancellationToken, Task<string>> work, CancellationToken token)
    {
        try
        {
            var message = await work(token);
            Volatile.Write(ref current, Volatile.Read(ref current) with { State = "Idle", Message = message });
        }
        catch (Exception ex) when (ex is NodeAgentException or IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidDataException or FormatException or HttpRequestException
            || ex is OperationCanceledException && !token.IsCancellationRequested)
        {
            var message = ex is OperationCanceledException ? "It took too long and was stopped." : ex.Message;
            Console.Error.WriteLine("A machine update failed. " + message);
            Volatile.Write(ref current, Volatile.Read(ref current) with { State = "Failed", Message = Bounded(message, 500) });
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    private static async Task<string> CheckAsync(CancellationToken token)
    {
        await NodeRuntime.RunAsync("/usr/bin/apt-get", ["-o", "DPkg::Lock::Timeout=600", "update"], TimeSpan.FromMinutes(15), token);
        var plan = await NodeRuntime.RunAsync("/usr/bin/apt-get", ["-o", "DPkg::Lock::Timeout=600", "-s", "upgrade", "--with-new-pkgs"],
            TimeSpan.FromMinutes(5), token) ?? "";
        var packages = ParsePlan(plan);
        Volatile.Write(ref current, Volatile.Read(ref current) with
        {
            CheckedAt = DateTimeOffset.UtcNow, Count = packages.Length, SecurityCount = packages.Count(item => item.Security),
            Packages = packages.Take(MaxPackages).ToArray(),
        });
        return packages.Length == 0 ? "Up to date." : $"{packages.Length} update{(packages.Length == 1 ? "" : "s")} available.";
    }

    private static async Task<string> InstallAsync(CancellationToken token)
    {
        // A transient unit keeps apt running if the agent itself restarts mid-upgrade, and the fixed name keeps it to one.
        await NodeRuntime.RunAsync("/usr/bin/systemd-run", ["--unit=lucia-apt-upgrade", "--collect", "--wait", "--pipe", "--quiet",
            "--setenv=DEBIAN_FRONTEND=noninteractive", "/usr/bin/apt-get", "-y", "-o", "DPkg::Lock::Timeout=600",
            "-o", "Dpkg::Options::=--force-confdef", "-o", "Dpkg::Options::=--force-confold", "upgrade", "--with-new-pkgs"],
            TimeSpan.FromHours(1), token);
        await CheckAsync(token);
        return File.Exists(RebootRequired) ? "Updates installed. Restart the server to finish." : "Updates installed.";
    }

    private static async Task<string> RestartAsync(CancellationToken token)
    {
        // Let the answer reach Lucia first.
        await Task.Delay(TimeSpan.FromSeconds(3), token);
        await NodeRuntime.RunAsync("/usr/bin/systemctl", ["reboot"], TimeSpan.FromMinutes(1), token);
        return "Restarting.";
    }

    /// <summary>
    /// Reads <c>apt-get -s upgrade</c>: lines like <c>Inst name [current] (candidate origin [arch])</c>, where a new
    /// package has no current version. An origin ending in <c>-security</c> marks a security update.
    /// </summary>
    internal static PackageUpdate[] ParsePlan(string output) =>
        output.Split('\n').Select(line => PlanLine().Match(line.TrimEnd('\r'))).Where(match => match.Success)
            .Select(match => new PackageUpdate(Bounded(match.Groups["name"].Value, MaxText)!,
                match.Groups["current"].Success ? Bounded(match.Groups["current"].Value, MaxText) : null,
                Bounded(match.Groups["candidate"].Value, MaxText)!,
                match.Groups["origin"].Value.Split(", ").Any(origin => origin.Contains("-security", StringComparison.OrdinalIgnoreCase)
                    || origin.StartsWith("Debian-Security", StringComparison.OrdinalIgnoreCase))))
            .DistinctBy(item => item.Name).OrderByDescending(item => item.Security).ThenBy(item => item.Name, StringComparer.Ordinal).ToArray();

    private static string? Bounded(string? value, int limit)
    {
        if (value is null) return null;
        var clean = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? null : clean.Length <= limit ? clean : clean[..limit];
    }

    [GeneratedRegex(@"\AInst (?<name>\S+) (?:\[(?<current>[^\]]+)\] )?\((?<candidate>\S+) (?<origin>.*?)(?: \[[^\]]+\])?\)")]
    private static partial Regex PlanLine();
}
