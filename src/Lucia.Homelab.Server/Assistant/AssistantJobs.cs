using System.Collections.Concurrent;
using System.Text.Json;
using Cronos;
using Lucia.Homelab.Server.Domains;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Assistant;

/// <summary>
/// Saved prompts the assistant runs on a schedule, unattended, in a new chat each time. <see cref="Tools"/> and <see cref="Hosts"/>
/// may run without asking; <see cref="Actor"/> and <see cref="User"/> are who saved it, which the job then acts as.
/// </summary>
public sealed record AssistantJob(string Id, string Name, string[] Prompts, string Cron, string TimeZone, string? Model, bool Enabled,
    string[] Tools, string[] Hosts, string Actor, string? User, DateTimeOffset Created, DateTimeOffset Updated);

public sealed record AssistantJobRequest(string? Name, string[]? Prompts, string? Cron, string? TimeZone, string? Model, bool Enabled,
    string[]? Tools, string[]? Hosts);

/// <summary>One run of a job: its chat, what started it, and how it ended.</summary>
public sealed record AssistantJobRun(string Id, string SessionId, string Trigger, DateTimeOffset Started, DateTimeOffset? Finished,
    string Status, string? Error);

public sealed record AssistantSchedule(string? Cron, string? TimeZone);

public sealed class AssistantJobs : BackgroundService
{
    private const int MaxJobs = 50, MaxPrompts = 10, KeptRuns = 50;
    private readonly ConcurrentDictionary<string, AssistantJobRun> _active = new();
    private readonly ConcurrentDictionary<string, AssistantJobWaits> _waits = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IOptions<AssistantOptions> _options;
    private readonly AssistantRuns _runs;
    private readonly AssistantTools? _tools;
    private readonly AssistantPush? _push;
    private readonly TimeProvider _time;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AssistantJobs> _logger;

    public AssistantJobs(IOptions<AssistantOptions> options, AssistantRuns runs, IHostApplicationLifetime lifetime, ILogger<AssistantJobs> logger,
        AssistantTools? tools = null, AssistantPush? push = null, TimeProvider? time = null)
    {
        (_options, _runs, _lifetime, _logger, _tools, _push, _time) = (options, runs, lifetime, logger, tools, push, time ?? TimeProvider.System);
        runs.Jobs = this;
    }

    private string Folder(string owner) => Path.Combine(Path.GetFullPath(_options.Value.Directory), "users", owner, "jobs");
    private string JobPath(string owner, string id) => Path.Combine(Folder(owner), JobId(id) + ".json");
    private string RunsPath(string owner, string id) => Path.Combine(Folder(owner), JobId(id) + ".runs.json");

    public async Task<object> ListAsync(string owner, CancellationToken ct)
    {
        var jobs = new List<object>();
        foreach (var job in await JobsAsync(owner, ct))
        {
            var runs = await RunsAsync(owner, job.Id, ct);
            jobs.Add(View(owner, job, runs.FirstOrDefault()));
        }
        return jobs;
    }

    public async Task<object> GetAsync(string owner, string id, CancellationToken ct)
    {
        var job = await ReadAsync(owner, id, ct) ?? throw NotFound();
        return View(owner, job, (await RunsAsync(owner, job.Id, ct)).FirstOrDefault());
    }

    /// <summary>Creates a job when <paramref name="id"/> is null, else replaces it. The saver becomes who the job acts as.</summary>
    public async Task<object> SaveAsync(string owner, string? id, AssistantJobRequest request, string actor, string? user, CancellationToken ct)
    {
        var name = request.Name?.Trim() is { Length: > 0 and <= 80 } trimmed ? trimmed
            : throw Invalid("Name the job in 1 to 80 characters.");
        var prompts = (request.Prompts ?? []).Select(prompt => prompt?.Trim() ?? "").ToArray();
        if (prompts.Length is 0 or > MaxPrompts || prompts.Any(prompt => prompt.Length is 0 or > 32_768))
            throw Invalid($"Give 1 to {MaxPrompts} prompts of up to 32,768 characters each.");
        var (cron, zone) = Schedule(request.Cron, request.TimeZone);
        if (request.Model is { } model && !AssistantRuns.ModelPattern().IsMatch(model)) throw Invalid("The model is invalid.");
        var allowed = _tools?.Create("owner", null).Where(tool => tool.Tier is ToolTier.Change or ToolTier.Destructive)
            .Select(tool => tool.Name).ToHashSet() ?? [];
        var tools = (request.Tools ?? []).Distinct().ToArray();
        if (tools.Any(tool => !allowed.Contains(tool))) throw Invalid("Permissions must be the assistant's change or destructive tools.");
        var hosts = (request.Hosts ?? []).Select(AssistantBroker.Host).ToArray();
        if (hosts.Length > AssistantBroker.MaxHosts || hosts.Any(host => host is null))
            throw Invalid($"Give up to {AssistantBroker.MaxHosts} valid site names.");
        await _gate.WaitAsync(ct);
        try
        {
            var existing = id is null ? null : await ReadAsync(owner, id, ct) ?? throw NotFound();
            if (existing is null && (await JobsAsync(owner, ct)).Count >= MaxJobs) throw Invalid($"Lucia keeps at most {MaxJobs} jobs.");
            var now = _time.GetUtcNow();
            var job = new AssistantJob(existing?.Id ?? Guid.NewGuid().ToString("N"), name, prompts, cron, zone.Id, request.Model,
                request.Enabled, tools, [.. hosts.OfType<string>().Distinct()], actor, user, existing?.Created ?? now, now);
            await DomainOnboardingStore.WriteJson(JobPath(owner, job.Id), job, ct, AssistantStream.Json);
            return View(owner, job, (await RunsAsync(owner, job.Id, ct)).FirstOrDefault());
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string owner, string id, CancellationToken ct)
    {
        if (_active.ContainsKey(Key(owner, JobId(id))))
            throw new AssistantException(409, "job_running", "This job is running. Stop its chat first.");
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(JobPath(owner, id))) throw NotFound();
            File.Delete(JobPath(owner, id));
            File.Delete(RunsPath(owner, id));
        }
        finally { _gate.Release(); }
    }

    /// <summary>The newest runs first. A run Lucia lost track of, because it restarted, shows as failed.</summary>
    public async Task<IReadOnlyList<AssistantJobRun>> RunsAsync(string owner, string id, CancellationToken ct)
    {
        var path = RunsPath(owner, id);
        List<AssistantJobRun> runs;
        try
        {
            if (!File.Exists(path)) return [];
            DomainOnboardingStore.RejectLinks(path);
            await using var file = File.OpenRead(path);
            runs = await JsonSerializer.DeserializeAsync<List<AssistantJobRun>>(file, AssistantStream.Json, ct) ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("A job's runs could not be read ({ErrorType}).", e.GetType().Name);
            return [];
        }
        return [.. runs.Select(run => run.Status == "running" && _active.GetValueOrDefault(Key(owner, id))?.Id != run.Id
            ? run with { Status = "failed", Error = "Lucia restarted during this run." } : run)];
    }

    /// <summary>Runs the job now, in a new chat. Returns the run, whose chat the page can open.</summary>
    public async Task<AssistantJobRun> RunNowAsync(string owner, string id, CancellationToken ct) =>
        await StartAsync(owner, await ReadAsync(owner, id, ct) ?? throw NotFound(), "manual", ct)
        ?? throw new AssistantException(409, "job_running", "This job is already running.");

    /// <summary>The next times a schedule fires, for the editor's preview.</summary>
    public static IReadOnlyList<DateTimeOffset> Next(AssistantSchedule schedule, DateTimeOffset from, int count = 3)
    {
        var (cron, zone) = Schedule(schedule.Cron, schedule.TimeZone);
        return [.. CronExpression.Parse(cron, CronFormat.Standard).GetOccurrences(from, from.AddYears(5), zone, fromInclusive: false).Take(count)];
    }

    /// <summary>The job's permissions for its chat; "always" adds to them, and a call left waiting during a run notifies the owner.</summary>
    internal async Task<AssistantJobGrants?> GrantsAsync(string owner, string id, string sessionId, CancellationToken ct)
    {
        if (await ReadAsync(owner, id, ct) is not { } job) return null;
        var active = _active.GetValueOrDefault(Key(owner, id)) is { } run && run.SessionId == sessionId ? run : null;
        return new AssistantJobGrants(job.Name, [.. job.Tools.Select(tool => "tool:" + tool), .. job.Hosts.Select(host => "host:" + host)],
            grant => AddGrantAsync(owner, id, grant),
            async tool =>
            {
                if (_push is null || active is null) return;
                await _push.SendAsync(owner, job.Name + " needs your approval", $"The job wants to use {tool}. Open its chat to allow or deny it.",
                    "/#/?chat=" + sessionId, CancellationToken.None);
            },
            active is null ? null : _waits.GetOrAdd(active.Id, _ => new()));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var last = _time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _time.GetUtcNow();
            var minute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero).AddMinutes(1);
            try { await Task.Delay(minute - now + TimeSpan.FromMilliseconds(200), _time, stoppingToken); }
            catch (OperationCanceledException) { return; }
            now = _time.GetUtcNow();
            try { await TickAsync(last, now, stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogError("Checking the assistant's jobs failed ({ErrorType}).", e.GetType().Name);
            }
            last = now;
        }
    }

    /// <summary>Starts each enabled job that fell due after <paramref name="last"/>. A job still running skips its turn.</summary>
    internal async Task TickAsync(DateTimeOffset last, DateTimeOffset now, CancellationToken ct)
    {
        var users = Path.Combine(Path.GetFullPath(_options.Value.Directory), "users");
        if (!Directory.Exists(users)) return;
        foreach (var owner in Directory.EnumerateDirectories(users).Select(Path.GetFileName).OfType<string>())
            foreach (var job in await JobsAsync(owner, ct))
            {
                if (!job.Enabled) continue;
                DateTimeOffset? due;
                try
                {
                    due = CronExpression.Parse(job.Cron, CronFormat.Standard)
                        .GetNextOccurrence(last, TimeZoneInfo.FindSystemTimeZoneById(job.TimeZone));
                }
                catch (Exception e) when (e is CronFormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    _logger.LogWarning("A job's schedule is invalid ({ErrorType}).", e.GetType().Name);
                    continue;
                }
                if (due is null || due > now) continue;
                if (await StartAsync(owner, job, "schedule", ct) is null)
                    _logger.LogInformation("A scheduled job skipped its turn: its last run is still going.");
            }
    }

    private async Task<AssistantJobRun?> StartAsync(string owner, AssistantJob job, string trigger, CancellationToken ct)
    {
        var run = new AssistantJobRun(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), trigger, _time.GetUtcNow(), null, "running", null);
        if (!_active.TryAdd(Key(owner, job.Id), run)) return null;
        try { await RecordAsync(owner, job.Id, run, ct); }
        catch
        {
            _active.TryRemove(Key(owner, job.Id), out _);
            throw;
        }
        _ = Task.Run(() => RunAsync(owner, job, run));
        return run;
    }

    private async Task RunAsync(string owner, AssistantJob job, AssistantJobRun run)
    {
        string status;
        string? error;
        try { (status, error) = await _runs.RunJobAsync(owner, job, run.SessionId, _lifetime.ApplicationStopping); }
        catch (OperationCanceledException) when (_lifetime.ApplicationStopping.IsCancellationRequested)
        { (status, error) = ("failed", "Lucia stopped during this run."); }
        catch (AssistantException e) { (status, error) = ("failed", e.Message); }
        catch (Exception e)
        {
            _logger.LogError("A job failed to run ({ErrorType}).", e.GetType().Name);
            (status, error) = ("failed", "The job could not run.");
        }
        if (_waits.TryRemove(run.Id, out var waits) && waits.Expired > 0 && status == "succeeded")
            (status, error) = ("unanswered", waits.Expired == 1 ? "An approval went unanswered for 30 minutes, so that step didn't run."
                : $"{waits.Expired} approvals went unanswered for 30 minutes, so those steps didn't run.");
        try { await RecordAsync(owner, job.Id, run with { Finished = _time.GetUtcNow(), Status = status, Error = error }, CancellationToken.None); }
        catch (Exception e) { _logger.LogError("A job's run could not be saved ({ErrorType}).", e.GetType().Name); }
        finally { _active.TryRemove(Key(owner, job.Id), out _); }
        if (status is "failed" or "unanswered" && _push is not null)
            await _push.SendAsync(owner, job.Name + (status == "failed" ? " failed" : " skipped a step"), error ?? "The job failed.",
                "/#/?chat=" + run.SessionId, CancellationToken.None);
    }

    private async Task RecordAsync(string owner, string id, AssistantJobRun run, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(JobPath(owner, id))) return;
            var runs = (await RunsAsync(owner, id, ct)).Where(other => other.Id != run.Id).Prepend(run).Take(KeptRuns).ToList();
            await DomainOnboardingStore.WriteJson(RunsPath(owner, id), runs, ct, AssistantStream.Json);
        }
        finally { _gate.Release(); }
    }

    private async Task AddGrantAsync(string owner, string id, string grant)
    {
        await _gate.WaitAsync();
        try
        {
            if (await ReadAsync(owner, id, CancellationToken.None) is not { } job) return;
            var (kind, name) = (grant[..grant.IndexOf(':')], grant[(grant.IndexOf(':') + 1)..]);
            job = kind == "host" ? job with { Hosts = [.. job.Hosts.Append(name).Distinct()] } : job with { Tools = [.. job.Tools.Append(name).Distinct()] };
            await DomainOnboardingStore.WriteJson(JobPath(owner, id), job with { Updated = _time.GetUtcNow() }, json: AssistantStream.Json);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<AssistantJob>> JobsAsync(string owner, CancellationToken ct)
    {
        var folder = Folder(owner);
        if (!Directory.Exists(folder)) return [];
        var jobs = new List<AssistantJob>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.json").Where(path => !path.EndsWith(".runs.json", StringComparison.Ordinal)))
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _) && await ReadAsync(owner, Path.GetFileNameWithoutExtension(path), ct) is { } job)
                jobs.Add(job);
        return [.. jobs.OrderBy(job => job.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private async Task<AssistantJob?> ReadAsync(string owner, string id, CancellationToken ct)
    {
        var path = JobPath(owner, id);
        try
        {
            if (!File.Exists(path)) return null;
            DomainOnboardingStore.RejectLinks(path);
            await using var file = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<AssistantJob>(file, AssistantStream.Json, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("A job could not be read ({ErrorType}).", e.GetType().Name);
            return null;
        }
    }

    private object View(string owner, AssistantJob job, AssistantJobRun? last)
    {
        DateTimeOffset? next = null;
        if (job.Enabled)
        {
            try { next = Next(new(job.Cron, job.TimeZone), _time.GetUtcNow(), 1).FirstOrDefault() is var at && at != default ? at : null; }
            catch (AssistantException) { }
        }
        var active = _active.GetValueOrDefault(Key(owner, job.Id));
        var waiting = active is not null && _waits.TryGetValue(active.Id, out var waits) && !waits.Open.IsEmpty ? waits.Open.Values.Min() : (DateTimeOffset?)null;
        return new
        {
            job.Id, job.Name, job.Prompts, job.Cron, job.TimeZone, job.Model, job.Enabled, job.Tools, job.Hosts, job.Created, job.Updated,
            next, last, running = active is not null, waiting,
        };
    }

    private static (string Cron, TimeZoneInfo Zone) Schedule(string? cron, string? timeZone)
    {
        var expression = cron?.Trim() ?? "";
        try { CronExpression.Parse(expression, CronFormat.Standard); }
        catch (CronFormatException) { throw Invalid("The schedule must be a 5-field cron expression, such as 0 7 * * *."); }
        try { return (expression, TimeZoneInfo.FindSystemTimeZoneById(timeZone is { Length: > 0 and <= 64 } zone ? zone : "UTC")); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw Invalid("The time zone isn't one Lucia knows."); }
    }

    private static string Key(string owner, string id) => owner + "/" + id;

    private static string JobId(string id) =>
        Guid.TryParseExact(id, "N", out var guid) && guid.ToString("N") == id ? id : throw NotFound();

    private static AssistantException Invalid(string message) => new(400, "invalid_job", message);
    private static AssistantException NotFound() => new(404, "job_not_found", "That job doesn't exist.");
}
