using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lucia.Homelab.Server.Domains;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Assistant.Skills;

/// <summary>One request to the Spark's skill runner. It names a skill and a script, never a path, flag, image or command.</summary>
/// <param name="Kind"><c>run</c> or <c>check</c>.</param>
/// <param name="Hash">A run's skill content hash, which the worker recomputes from the skill store; null for a check.</param>
/// <param name="Content">A check's file as the editor holds it; null for a run.</param>
/// <param name="Network"><c>none</c> or <c>bridge</c>, from the skill's own settings.</param>
public sealed record SparkSkillRequest(int SchemaVersion, string Id, string Kind, string Skill, string? Hash, string Script, string Language,
    JsonObject? Arguments, string? Content, int TimeoutSeconds, string Network, string ExpiresAt);

/// <param name="RequestHash">SHA-256 of the request file's bytes, so an answer is never taken for another request's.</param>
/// <param name="Status"><c>ok</c> (it ran, whatever the exit code), <c>rejected</c>, <c>expired</c>, <c>unavailable</c> or <c>interrupted</c>.</param>
public sealed record SparkSkillResponse(int SchemaVersion, string Id, string RequestHash, string Status, int? ExitCode, bool TimedOut, bool Truncated,
    string Output, SkillDiagnostic[] Diagnostics, string? Message, long DurationMs, DateTimeOffset FinishedAt);

/// <param name="Image"><c>missing</c>, <c>building</c>, <c>ready</c> or <c>failed</c>.</param>
public sealed record SparkWorkerStatus(bool Ready, string Image, string? Message);

/// <summary>
/// <c>spark</c> mode: runs a skill's script, or checks a file, in a throwaway container on the Spark. The web host holds no Docker
/// access: it writes a request under <c>assistant/skill-runner/requests</c> and the Spark's scoped <c>lucia-skill-runner</c> service,
/// which owns the image and the <c>docker run</c> flags, answers in <c>responses</c> and keeps <c>worker.json</c> fresh.
/// </summary>
public sealed class SparkSkillRunner(IOptions<AssistantOptions> options, SkillStore store, TimeProvider? clock = null)
{
    public const int RunSeconds = 300, MaxArgumentBytes = 16 * 1024, MaxContentBytes = 256 * 1024, QueueSeconds = 120;
    private const int MaxResponseBytes = 1024 * 1024;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Languages =
        [SkillLanguages.Shell, SkillLanguages.Python, SkillLanguages.Javascript, SkillLanguages.Typescript, SkillLanguages.Csharp];

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private string Folder => Path.Combine(Path.GetFullPath(options.Value.Directory), "skill-runner");

    /// <summary>What the service's heartbeat says; not ready when it is missing, stale or unreadable.</summary>
    public SparkWorkerStatus Status()
    {
        var path = Path.Combine(Folder, "worker.json");
        try
        {
            if (!File.Exists(path)) return new(false, "missing", null);
            using var json = JsonDocument.Parse(CertbotFiles.ReadBounded(path, 4096));
            var value = json.RootElement;
            var seen = value.GetProperty("checkedAt").GetDateTimeOffset();
            var now = _clock.GetUtcNow();
            var ready = value.GetProperty("schemaVersion").GetInt32() == 1 && value.GetProperty("ready").GetBoolean()
                && seen <= now.AddSeconds(5) && seen > now.AddSeconds(-45);
            var image = value.TryGetProperty("image", out var state) && state.ValueKind == JsonValueKind.String ? state.GetString()! : "missing";
            var message = value.TryGetProperty("message", out var note) && note.ValueKind == JsonValueKind.String ? note.GetString() : null;
            return new(ready, image, message);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return new(false, "missing", null); }
    }

    /// <summary>Why nothing can be queued right now, as text for the owner or the model; null when the runner is ready.</summary>
    public static string? Unavailable(SparkWorkerStatus status) => status switch
    {
        { Ready: false } => "The Spark's skill runner service isn't running, so this can't run. Check the lucia-skill-runner service on the Spark.",
        { Image: "ready" } => null,
        { Image: "building" } => "The Spark's skill runner is still building its container image, which takes a few minutes the first time. Try again shortly.",
        { Image: "failed" } => "The Spark's skill runner couldn't build its container image" + (status.Message is { Length: > 0 } why ? ": " + why : "."),
        _ => "The Spark's skill runner is starting. Try again shortly.",
    };

    /// <param name="script">The script's path inside the skill, such as scripts/check.py.</param>
    /// <param name="arguments">What the model passed; it goes to the script as JSON on stdin and in LUCIA_SKILL_ARGS.</param>
    /// <returns>The result as text for the model; Lucia's own refusals read "HOST: ..." so the model can tell them from the script's output.</returns>
    public async Task<string> RunAsync(string skill, string script, JsonElement? arguments, CancellationToken ct)
    {
        var (passed, error, _) = SkillArguments.Normalize(arguments, null, SkillExecution.Spark);
        if (error is not null) return error;
        if (Encoding.UTF8.GetByteCount(passed!.ToJsonString()) > MaxArgumentBytes) return $"HOST: the arguments are too large; the limit is {MaxArgumentBytes / 1024} KiB.";
        if (Unavailable(Status()) is { } why) return "HOST: " + why;
        var language = SkillLanguages.Language(script);
        if (language is null || !Languages.Contains(language) || !SkillLanguages.IsScript(script)) return $"HOST: {script} isn't a script that runs on the Spark.";
        if (store.HashOf(skill) is not { } hash) return "HOST: that skill no longer exists.";
        var request = new SparkSkillRequest(1, Guid.NewGuid().ToString("D"), "run", skill, hash, script, language, passed, null, RunSeconds,
            store.SettingsOf(skill).Network, Expires(RunSeconds));
        try
        {
            var response = await SubmitAsync(request, TimeSpan.FromSeconds(RunSeconds + QueueSeconds), ct);
            if (response.Status != "ok") return "HOST: " + (response.Message ?? "the Spark's skill runner refused this script.");
            var note = response.TimedOut ? $"\nStopped: it ran for more than {RunSeconds} seconds."
                : response.Truncated ? "\n(The output is cut to its last 48 KiB.)" : "";
            return $"Ran {script} on the Spark in a throwaway container{(request.Network == SkillNetwork.Bridge ? " with network access" : " without network access")}. "
                + $"Exit code {(response.ExitCode is { } code ? code.ToString(CultureInfo.InvariantCulture) : "unknown")}.{note}\n"
                + (string.IsNullOrEmpty(response.Output) ? "(no output)" : response.Output);
        }
        catch (AssistantException e) { return "HOST: " + e.Message; }
    }

    /// <summary>A syntax-only check of <paramref name="content"/> as the file at <paramref name="path"/>; nothing in it runs.</summary>
    public async Task<SkillCheckResult> CheckAsync(string skill, string path, string content, CancellationToken ct)
    {
        const string where = "the Spark";
        if (Unavailable(Status()) is { } why) return new(false, [], null, why);
        var language = SkillLanguages.Language(path);
        if (language is null || !Languages.Contains(language) || !SkillLanguages.IsScript(path) || !path.StartsWith("scripts/", StringComparison.Ordinal))
            return new(false, [], null, "Only scripts in the scripts folder have a syntax check.");
        if (Encoding.UTF8.GetByteCount(content) > MaxContentBytes) return new(false, [], null, "This file is too big to check (the limit is 256 KiB).");
        var seconds = language == SkillLanguages.Csharp ? 180 : 60;
        var request = new SparkSkillRequest(1, Guid.NewGuid().ToString("D"), "check", skill, null, path, language, null, content, seconds,
            store.SettingsOf(skill).Network, Expires(seconds));
        try
        {
            var response = await SubmitAsync(request, TimeSpan.FromSeconds(seconds + QueueSeconds), ct);
            if (response.Status != "ok") return new(false, [], null, response.Message ?? "The Spark's skill runner refused this check.");
            if (response.TimedOut || response.ExitCode is null) return new(false, [], where, "The check didn't finish.");
            if (response.ExitCode == 0) return new(true, [], where, null);
            var markers = response.Diagnostics.Length > 0 ? response.Diagnostics : SkillChecker.Parse(Path.GetExtension(path), response.Output);
            return new(false, markers, where, null);
        }
        catch (AssistantException e) { return new(false, [], null, e.Message); }
    }

    // expiresAt is the latest moment the worker may START the run; the wait below covers queueing plus the run itself,
    // so a run that starts never outlives the host's wait and gets reported as not run.
    private string Expires(int seconds) =>
        _clock.GetUtcNow().AddSeconds(QueueSeconds).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Queues the request and waits for the worker's answer; the request is withdrawn if nobody answers in time.</summary>
    internal async Task<SparkSkillResponse> SubmitAsync(SparkSkillRequest request, TimeSpan wait, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var requestPath = Path.Combine(Folder, "requests", request.Id + ".json");
        var responsePath = Path.Combine(Folder, "responses", request.Id + ".json");
        try
        {
            await Write(requestPath, bytes, ct);
            var deadline = _clock.GetUtcNow() + wait;
            while (true)
            {
                if (File.Exists(responsePath))
                {
                    var response = JsonSerializer.Deserialize<SparkSkillResponse>(CertbotFiles.ReadBounded(responsePath, MaxResponseBytes), Json);
                    File.Delete(responsePath);
                    if (response is not { SchemaVersion: 1 } || response.Id != request.Id || response.RequestHash != hash)
                        throw new AssistantException(502, "spark_runner_invalid", "The Spark's skill runner answered with something that isn't this request's result.");
                    return response;
                }
                if (_clock.GetUtcNow() >= deadline)
                    throw new AssistantException(504, "spark_runner_timeout", "The Spark's skill runner didn't answer in time, so Lucia stopped waiting.");
                await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new AssistantException(503, "spark_runner_unavailable", "Lucia couldn't use the Spark skill runner's queue.");
        }
        finally
        {
            try { File.Delete(requestPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the worker removes what it has answered */ }
        }
    }

    private static async Task Write(string path, byte[] bytes, CancellationToken ct)
    {
        DomainOnboardingStore.RejectLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var settings = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, settings))
            {
                await stream.WriteAsync(bytes, ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally { File.Delete(temporary); }
    }
}
