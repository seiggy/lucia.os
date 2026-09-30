using System.Text.Json;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Onboarding;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Stacks;

/// <param name="Repositories">owner/repo entries and organization names, comma-separated, as the catalog app takes them.</param>
/// <param name="Token">A new GitHub token, or empty to keep the saved one.</param>
public sealed record SparkRunnerRequest(string? Repositories, string? Labels, int? IdleMinutes, string? Token);

/// <summary>
/// The GitHub Actions runner on the Spark, for jobs that need a native ARM64 machine. It runs only after the owner starts
/// it, and stops itself once no job has run for the chosen number of minutes. The web host holds no Docker access: it writes
/// the owner's choices to <c>spark-runner/request.json</c>, and the Spark's scoped <c>lucia-spark-runner</c> service, which
/// owns the compose file and its pinned images, runs the runners and reports in <c>status.json</c>.
/// </summary>
public sealed class SparkRunner(IOptions<HardwareOnboardingOptions> options, TimeProvider time)
{
    /// <summary>The label every Spark runner has, besides self-hosted, linux and ARM64, and its runners' name prefix.</summary>
    public const string MachineLabel = "spark";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string Folder => Path.Combine(Path.GetDirectoryName(options.Value.StateDirectory)!, "spark-runner");

    /// <param name="Desired"><c>running</c>, <c>stopped</c> or <c>removed</c> (stopped, with its Docker data deleted).</param>
    /// <param name="Generation">Raised on every change, so the service applies each one once.</param>
    internal sealed record Request(int SchemaVersion, long Generation, string Desired, string[] Repositories, string[] Labels, int IdleMinutes, string Token);

    /// <param name="State"><c>starting</c>, <c>running</c>, <c>stopped</c>, <c>failed</c> or <c>removed</c>.</param>
    /// <param name="Reason">Why it stopped: <c>owner</c>, <c>idle</c> or <c>exited</c>.</param>
    /// <param name="LastJobAt">When the service last saw a job running.</param>
    internal sealed record Status(int SchemaVersion, long Generation, string State, string? Reason, DateTimeOffset? Since, DateTimeOffset? LastJobAt,
        bool Busy, string? Message, DateTimeOffset CheckedAt);

    public async Task<object> Get(CancellationToken ct)
    {
        var request = await Read(ct);
        var status = ReadStatus();
        var ready = status is not null && time.GetUtcNow() - status.CheckedAt < TimeSpan.FromMinutes(1);
        if (request is null or { Desired: "removed" }) return new { configured = false, workerReady = ready, label = MachineLabel };
        var applied = status?.Generation == request.Generation ? status : null;
        var state = applied?.State ?? (request.Desired == "running" ? "starting" : "stopping");
        DateTimeOffset? active = applied?.LastJobAt > applied?.Since ? applied?.LastJobAt : applied?.Since;
        return new
        {
            configured = true, request.Repositories, request.Labels, request.IdleMinutes, state, reason = applied?.Reason, since = applied?.Since,
            lastJobAt = status?.LastJobAt, busy = applied?.Busy ?? false, message = applied?.Message, workerReady = ready, label = MachineLabel,
            stopsAt = state == "running" && applied?.Busy == false ? active?.AddMinutes(request.IdleMinutes) : null,
        };
    }

    public async Task<object> Save(SparkRunnerRequest input, CancellationToken ct)
    {
        var repositories = GitHubRunnerApp.Repositories(input.Repositories ?? "");
        var labels = GitHubRunnerApp.Labels(input.Labels ?? "", MachineLabel)[1..];
        if (input.IdleMinutes is { } minutes and not (>= 5 and <= 1440))
            throw new HardwareOnboardingException(400, "invalid_setting", "Stop after idle must be from 5 to 1440 minutes.");
        var token = (input.Token ?? "").Trim();
        if (token.Length > 0 && !GitHubRunnerApp.Token().IsMatch(token))
            throw new HardwareOnboardingException(400, "invalid_setting", "The GitHub token must be a personal access token, such as github_pat_….");
        await _gate.WaitAsync(ct);
        try
        {
            var existing = Configured(ReadUnlocked());
            token = token.Length > 0 ? token : existing?.Token
                ?? throw new HardwareOnboardingException(400, "invalid_setting", "Enter a GitHub personal access token.");
            // Saving keeps a stopped runner stopped, and restarts a running one with the new settings.
            await Write(new(1, (existing?.Generation ?? ReadUnlocked()?.Generation ?? 0) + 1, Running(existing) ? "running" : "stopped",
                repositories, labels, input.IdleMinutes ?? existing?.IdleMinutes ?? 30, token), ct);
        }
        finally { _gate.Release(); }
        return await Get(ct);
    }

    /// <param name="desired"><c>running</c>, <c>stopped</c> or <c>removed</c>.</param>
    public async Task<object> Set(string desired, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var existing = Configured(ReadUnlocked())
                ?? throw new HardwareOnboardingException(404, "spark_runner_not_found", "The Spark has no runner. Set one up from the catalog.");
            await Write(desired == "removed" ? new(1, existing.Generation + 1, desired, [], [], existing.IdleMinutes, "")
                : existing with { Generation = existing.Generation + 1, Desired = desired }, ct);
        }
        finally { _gate.Release(); }
        return await Get(ct);
    }

    private bool Running(Request? request)
    {
        if (request is null) return false;
        var status = ReadStatus();
        return status?.Generation == request.Generation ? status.State is "starting" or "running" : request.Desired == "running";
    }

    private static Request? Configured(Request? request) => request is { Desired: not "removed" } ? request : null;

    private async Task<Request?> Read(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return ReadUnlocked(); }
        finally { _gate.Release(); }
    }

    private Request? ReadUnlocked()
    {
        var path = Path.Combine(Folder, "request.json");
        DomainOnboardingStore.RejectLinks(path);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<Request>(CertbotFiles.ReadBounded(path, 64 * 1024), DomainOnboardingStore.Json) is { SchemaVersion: 1 } request
            ? request : throw new InvalidDataException("The Spark runner's settings are invalid.");
    }

    private Status? ReadStatus()
    {
        var path = Path.Combine(Folder, "status.json");
        try
        {
            DomainOnboardingStore.RejectLinks(path);
            return File.Exists(path) && JsonSerializer.Deserialize<Status>(CertbotFiles.ReadBounded(path, 16 * 1024), DomainOnboardingStore.Json)
                is { SchemaVersion: 1 } status ? status : null;
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidDataException) { return null; }
    }

    private Task Write(Request request, CancellationToken ct) => DomainOnboardingStore.WriteJson(Path.Combine(Folder, "request.json"), request, ct);
}
