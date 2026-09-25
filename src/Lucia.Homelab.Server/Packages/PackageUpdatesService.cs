using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Host;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Packages;

public sealed class PackageUpdatesOptions
{
    /// <summary>Exchange directory shared with the root package worker (tools/packages/package_worker.py).</summary>
    public string Directory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lucia", "data", "packages");

    public static bool ModelsPaused(PackageUpdatesOptions options) =>
        File.Exists(Path.Combine(options.Directory, "models-paused.json"));
}

public sealed class PackageUpdatesException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed record PackageInstallRequest(string[]? Packages, bool IncludePlatform, string Restart);
public sealed record PackageScheduleRequest(DateTimeOffset RunAt, string[]? Packages, bool IncludePlatform, string Restart);
public sealed record PackageSchedule(DateTimeOffset RunAt, string[]? Packages, bool IncludePlatform, string Restart, DateTimeOffset CreatedAt);
public sealed record PackageScheduleOutcome(DateTimeOffset RunAt, string State, string Message, DateTimeOffset At);
public sealed record PackageScheduleDocument(int Version, PackageSchedule? Schedule, PackageScheduleOutcome? Last);

public sealed partial class PackageUpdatesService(IOptions<PackageUpdatesOptions> options, InferenceRuntime runtime,
    ModelCatalog catalog, ILogger<PackageUpdatesService> logger)
{
    public const string InstallCommand = "sudo python3 ~/.local/share/lucia/bootstrap/app/tools/packages/package_worker.py install";
    private static readonly string[] Restarts = ["none", "services", "spark"];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string Root => Path.GetFullPath(options.Value.Directory);
    private string SchedulePath => Path.Combine(Root, "schedule.json");
    private string PausePath => Path.Combine(Root, "models-paused.json");

    [GeneratedRegex("^[a-z0-9][a-z0-9+.-]{0,127}$")]
    private static partial Regex PackageName();

    /// <summary>Worker status (null when not installed) and whether it is fresh enough to accept work.</summary>
    public (JsonObject? Status, bool Ready) ReadWorker()
    {
        var path = Path.Combine(Root, "status.json");
        try
        {
            DomainOnboardingStore.RejectLinks(path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 2 * 1024 * 1024) return (null, false);
            var status = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject;
            var checkedAt = status?["checkedAt"]?.GetValue<string>() is { } text && DateTimeOffset.TryParse(text, out var at) ? at : default;
            return (status, status?["schemaVersion"]?.GetValue<int>() == 1 && DateTimeOffset.UtcNow - checkedAt < TimeSpan.FromSeconds(90));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or FormatException)
        {
            logger.LogWarning("The package worker status could not be read ({ErrorType}).", error.GetType().Name);
            return (null, false);
        }
    }

    public async Task<JsonObject> GetAsync(CancellationToken ct)
    {
        var (status, ready) = ReadWorker();
        var result = status ?? new JsonObject();
        result["worker"] = new JsonObject
        {
            ["state"] = status is null ? "missing" : ready ? "ready" : "stale",
            ["installCommand"] = InstallCommand
        };
        var schedule = await ReadScheduleAsync(ct);
        result["schedule"] = JsonSerializer.SerializeToNode(schedule.Schedule, DomainOnboardingStore.Json);
        result["lastSchedule"] = JsonSerializer.SerializeToNode(schedule.Last, DomainOnboardingStore.Json);
        result["pendingRequest"] = PendingRequest();
        result["modelsPaused"] = File.Exists(PausePath);
        CleanResponses();
        return result;
    }

    public Task<object> CheckAsync(CancellationToken ct) => SubmitAsync("check", ct);
    public Task<object> RepairAsync(CancellationToken ct) => SubmitAsync("repair", ct);
    public Task<object> RestartServicesAsync(CancellationToken ct) => SubmitAsync("restart-services", ct);
    public Task<object> RestartSparkAsync(CancellationToken ct) => SubmitAsync("restart-spark", ct);

    public async Task<object> InstallAsync(PackageInstallRequest request, CancellationToken ct)
    {
        var status = RequireReady();
        var names = Resolve(status, request.Packages, request.IncludePlatform, request.Restart);
        if (names.Length == 0)
            throw new PackageUpdatesException(400, "no_packages", "Choose at least one update to install.");
        return await InstallResolvedAsync(status, names, request.IncludePlatform, request.Restart, ct);
    }

    public async Task<object> ChangelogAsync(string? package, CancellationToken ct)
    {
        var (status, ready) = ReadWorker();
        if (status is null || !ready)
            throw new PackageUpdatesException(503, "worker_unavailable", "Lucia's update service isn't responding. It may be restarting.");
        if (package is null || !PackageName().IsMatch(package) || !Updates(status).Any(u => Name(u) == package))
            throw new PackageUpdatesException(400, "unknown_package", "Changelogs are shown only for available updates.");
        var id = await WriteRequestAsync("changelog", new JsonObject { ["package"] = package }, ct);
        var path = Path.Combine(Root, "responses", id + ".json");
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            DomainOnboardingStore.RejectLinks(path);
            if (!File.Exists(path)) continue;
            if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException("Changelog response is oversized.");
            var response = JsonNode.Parse(await File.ReadAllBytesAsync(path, ct))!.AsObject();
            File.Delete(path);
            if (response["success"]?.GetValue<bool>() != true)
                throw new PackageUpdatesException(404, "changelog_unavailable", response["message"]?.GetValue<string>()
                    ?? "This package's source doesn't publish a changelog Lucia can read.");
            return new { package, changelog = response["changelog"]?.GetValue<string>() ?? "" };
        }
        throw new PackageUpdatesException(504, "changelog_timeout", "The changelog took too long to download. Try again later.");
    }

    public async Task<PackageSchedule> SaveScheduleAsync(PackageScheduleRequest request, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (request.RunAt < now.AddMinutes(2) || request.RunAt > now.AddDays(30))
            throw new PackageUpdatesException(400, "invalid_schedule", "Choose a time between two minutes and 30 days from now.");
        var (status, _) = ReadWorker();
        if (status is null)
            throw new PackageUpdatesException(503, "worker_missing", "Lucia's update service isn't set up on the Spark yet.");
        if (request.Packages is { Length: 0 })
        {
            if (request.Restart != "spark")
                throw new PackageUpdatesException(400, "empty_schedule", "Choose updates to install, or schedule a Spark restart.");
        }
        else
            Resolve(status, request.Packages, request.IncludePlatform, request.Restart);
        var saved = new PackageSchedule(request.RunAt.ToUniversalTime(), request.Packages, request.IncludePlatform, request.Restart, now);
        await _gate.WaitAsync(ct);
        try
        {
            var document = await ReadScheduleAsync(ct);
            await DomainOnboardingStore.WriteJson(SchedulePath, document with { Schedule = saved }, ct);
            return saved;
        }
        finally { _gate.Release(); }
    }

    public async Task CancelScheduleAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var document = await ReadScheduleAsync(ct);
            await DomainOnboardingStore.WriteJson(SchedulePath, document with { Schedule = null }, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Scheduler tick: start a due window, or resume Local AI once platform work has finished.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var (status, ready) = ReadWorker();
        await ResumeModelsIfDoneAsync(status, ready, ct);
        PackageScheduleDocument document;
        await _gate.WaitAsync(ct);
        try { document = await ReadScheduleAsync(ct); }
        finally { _gate.Release(); }
        if (document.Schedule is not { } schedule || schedule.RunAt > DateTimeOffset.UtcNow) return;
        var late = DateTimeOffset.UtcNow - schedule.RunAt > TimeSpan.FromMinutes(60);
        if (!late && (!ready || Busy(status) || PendingRequest())) return;
        var outcome = late
            ? new PackageScheduleOutcome(schedule.RunAt, "missed",
                "Lucia or its update service was offline or busy at the scheduled time, so nothing was installed.", DateTimeOffset.UtcNow)
            : await RunScheduleAsync(status!, schedule, ct);
        await _gate.WaitAsync(ct);
        try
        {
            var current = await ReadScheduleAsync(ct);
            if (current.Schedule?.CreatedAt == schedule.CreatedAt)
                await DomainOnboardingStore.WriteJson(SchedulePath, new PackageScheduleDocument(1, null, outcome), ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<PackageScheduleOutcome> RunScheduleAsync(JsonObject status, PackageSchedule schedule, CancellationToken ct)
    {
        string message;
        try
        {
            var names = schedule.Packages is { Length: 0 } ? [] : Resolve(status, schedule.Packages, schedule.IncludePlatform, schedule.Restart);
            if (names.Length > 0)
            {
                await InstallResolvedAsync(status, names, schedule.IncludePlatform, schedule.Restart, ct);
                message = $"Started installing {names.Length} update{(names.Length == 1 ? "" : "s")} in the scheduled window.";
            }
            else if (schedule.Restart == "spark" && status["restart"]?["sparkRequired"]?.GetValue<bool>() == true)
            {
                await SubmitAsync("restart-spark", ct);
                message = "Restarted the Spark in the scheduled window.";
            }
            else
                message = schedule.Packages is { Length: 0 } ? "The Spark didn't need a restart, so Lucia left it running."
                    : "There were no updates to install in the scheduled window.";
        }
        catch (PackageUpdatesException error)
        {
            return new(schedule.RunAt, "failed", error.Message, DateTimeOffset.UtcNow);
        }
        return new(schedule.RunAt, "started", message, DateTimeOffset.UtcNow);
    }

    private async Task<object> InstallResolvedAsync(JsonObject status, string[] names, bool includePlatform, string restart, CancellationToken ct)
    {
        if (Updates(status).Any(u => names.Contains(Name(u)) && u["platform"]?.GetValue<bool>() == true))
            await PauseModelsAsync(ct);
        var id = await WriteRequestAsync("install", new JsonObject
        {
            ["packages"] = new JsonArray(names.Select(n => (JsonNode)n!).ToArray()),
            ["includePlatform"] = includePlatform,
            ["restart"] = restart
        }, ct);
        return new { id, action = "install" };
    }

    private static string[] Resolve(JsonObject status, string[]? packages, bool includePlatform, string restart)
    {
        if (!Restarts.Contains(restart))
            throw new PackageUpdatesException(400, "invalid_restart", "Choose how Lucia should handle restarts.");
        var available = Updates(status).ToDictionary(Name, u => u["platform"]?.GetValue<bool>() == true);
        if (packages is null)
            return available.Where(pair => includePlatform || !pair.Value).Select(pair => pair.Key).Order().ToArray();
        if (packages.Length > 1000 || packages.Distinct().Count() != packages.Length || packages.Any(p => p is null || !PackageName().IsMatch(p)))
            throw new PackageUpdatesException(400, "invalid_packages", "The update selection is invalid.");
        if (packages.FirstOrDefault(p => !available.ContainsKey(p)) is { } missing)
            throw new PackageUpdatesException(409, "update_unavailable", $"{missing} is no longer an available update. Check for updates and review the list again.");
        if (!includePlatform && packages.Any(p => available[p]))
            throw new PackageUpdatesException(400, "platform_confirmation", "Platform updates need their own confirmation.");
        return packages;
    }

    private async Task PauseModelsAsync(CancellationToken ct)
    {
        await DomainOnboardingStore.WriteJson(PausePath, new { version = 1, pausedAt = DateTimeOffset.UtcNow }, ct);
        await runtime.UnloadAsync(ModelKind.Chat, ct, keepSelection: true);
        await runtime.UnloadAsync(ModelKind.Embedding, ct, keepSelection: true);
        runtime.StartupError = "Local AI is paused while platform updates install. Lucia reloads it when they finish.";
        logger.LogInformation("Paused local models for platform updates.");
    }

    private async Task ResumeModelsIfDoneAsync(JsonObject? status, bool ready, CancellationToken ct)
    {
        if (!File.Exists(PausePath)) return;
        // Give up waiting after three hours so a vanished worker can't keep Local AI off forever.
        var stale = DateTime.UtcNow - File.GetLastWriteTimeUtc(PausePath) > TimeSpan.FromHours(3);
        if (!stale && (!ready || Busy(status) || PendingRequest() || status?["rebootingAt"] is JsonValue)) return;
        File.Delete(PausePath);
        runtime.StartupError = null;
        try
        {
            var saved = await catalog.ReadSelectionAsync(ct);
            if (saved?.ChatId is { } chat) await runtime.LoadAsync(chat, saved.ChatContext, ct);
            if (saved?.EmbeddingId is { } embedding) await runtime.LoadAsync(embedding, saved.EmbeddingContext, ct);
            logger.LogInformation("Resumed local models after package updates.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            runtime.StartupError = "Local AI couldn't reload after the updates. Open AI models and load it again.";
            logger.LogError(error, "Local models could not be reloaded after package updates.");
        }
    }

    private JsonObject RequireReady()
    {
        var (status, ready) = ReadWorker();
        if (status is null)
            throw new PackageUpdatesException(503, "worker_missing", "Lucia's update service isn't set up on the Spark yet.");
        if (!ready)
            throw new PackageUpdatesException(503, "worker_unavailable", "Lucia's update service isn't responding. It may be restarting.");
        if (Busy(status) || PendingRequest())
            throw new PackageUpdatesException(409, "update_busy", "Another update task is running. Wait for it to finish.");
        return status;
    }

    private async Task<object> SubmitAsync(string action, CancellationToken ct)
    {
        RequireReady();
        return new { id = await WriteRequestAsync(action, new JsonObject(), ct), action };
    }

    private async Task<string> WriteRequestAsync(string action, JsonObject fields, CancellationToken ct)
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        fields["schemaVersion"] = 1;
        fields["id"] = id;
        fields["action"] = action;
        fields["requestedAt"] = DateTimeOffset.UtcNow.ToString("O");
        await DomainOnboardingStore.WriteJson(Path.Combine(Root, "requests", id + ".json"), fields, ct);
        logger.LogInformation("Submitted package {Action} request {RequestId}.", action, id);
        return id;
    }

    private bool PendingRequest()
    {
        var directory = Path.Combine(Root, "requests");
        return Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.json")
            .Any(path => DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromMinutes(10));
    }

    private void CleanResponses()
    {
        var directory = Path.Combine(Root, "responses");
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromMinutes(10))
                try { File.Delete(path); } catch (IOException) { }
    }

    private async Task<PackageScheduleDocument> ReadScheduleAsync(CancellationToken ct)
    {
        DomainOnboardingStore.RejectLinks(SchedulePath);
        if (!File.Exists(SchedulePath)) return new(1, null, null);
        if (new FileInfo(SchedulePath).Length > 64 * 1024) throw new InvalidDataException("The update schedule is oversized.");
        return JsonSerializer.Deserialize<PackageScheduleDocument>(await File.ReadAllBytesAsync(SchedulePath, ct), DomainOnboardingStore.Json)
            ?? new(1, null, null);
    }

    private static bool Busy(JsonObject? status) => status?["operation"] is JsonObject;
    private static IEnumerable<JsonObject> Updates(JsonObject status) =>
        status["updates"] is JsonArray updates ? updates.OfType<JsonObject>() : [];
    private static string Name(JsonObject update) => update["name"]?.GetValue<string>() ?? "";
}

public sealed class PackageUpdatesScheduler(PackageUpdatesService service, ILogger<PackageUpdatesScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try { await service.TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning("Package update schedule check failed ({ErrorType}).", error.GetType().Name); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
