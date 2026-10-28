using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Packages;
using Lucia.Homelab.Server.Security;
using Lucia.Homelab.Server.Stacks;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Platform;

/// <summary>A newer version of a platform image that Lucia found in its registry.</summary>
/// <param name="Kind"><c>rebuild</c> (same tag, newer digest), <c>patch</c>, <c>minor</c>, <c>release-line</c> (Authentik's
/// next line) or <c>major</c> (shown, never applied: it needs a code change).</param>
/// <param name="UpgradeNotes">For an Authentik release line, its upgrade notes.</param>
/// <param name="FirstSeenAt">When Lucia first offered it, so a waiting security patch can be flagged.</param>
public sealed record PlatformOffer(string Tag, string Digest, string Kind, bool Applicable, string? Notes, string? UpgradeNotes,
    DateTimeOffset FirstSeenAt);

/// <param name="Tag">One of the service's offered tags; its best applicable offer when left out.</param>
public sealed record PlatformUpdateChoice(string Service, string? Tag = null);

/// <summary>One service as the page and the Assistant read it.</summary>
public sealed record PlatformImageRow(string Service, string Name, string Policy, string Image, string Tag, string Digest, bool Override,
    bool Security, string Disruption, PlatformOffer[] Offers, string? Problem);

/// <summary>Explicit choices from the page, or services with <paramref name="PatchOnly"/> as the Assistant asks.</summary>
public sealed record PlatformUpdateRequest(PlatformUpdateChoice[]? Updates = null, string[]? Services = null, bool PatchOnly = false);

/// <summary>
/// The Spark's platform images (Authentik, the gateway, the CA, PostgreSQL, OpenLDAP and the telemetry relay): what runs,
/// from the manifest the identity AppHost writes at every deploy, and the newer versions their registries offer under each
/// image's policy class. Like the Qwen model, the web host holds no Docker access: it writes
/// <c>lucia-platform-updates/request.json</c>, and the Spark's <c>lucia-platform-update</c> service checks it again, updates
/// and reports in <c>status.json</c>.
/// </summary>
public sealed class PlatformImageUpdates(IOptions<HardwareOnboardingOptions> options, TimeProvider time, ILogger<PlatformImageUpdates> logger)
{
    public const string InstallCommand = "python3 ~/.local/share/lucia/bootstrap/app/tools/runners/platform_update_worker.py install";
    /// <summary>Patches to these wait for the owner at most a week before the dashboard flags them.</summary>
    public static readonly string[] SecurityRelevant = ["authentik", "gateway", "ca"];
    private static readonly string[] Traffic = ["authentik", "gateway", "database", "directory"];
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);
    private static readonly HttpClient GitHub = new() { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1024 * 1024 };
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, PlatformOffer[] Offers, string? Problem)> _found = new();
    private (DateTimeOffset At, string? Latest)? _plugin;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _checkedAt;

    internal string Folder => Path.Combine(Path.GetDirectoryName(options.Value.StateDirectory)!, PlatformImages.Folder);

    /// <summary>What the Spark runs, from the AppHost's manifest; the built-in pins until an identity deploy has written one.</summary>
    internal (PlatformPin[] Pins, DateTimeOffset? WrittenAt, string? Problem) Running()
    {
        try
        {
            if (ReadJson(Path.Combine(Folder, PlatformImages.ManifestFile), 64 * 1024) is not JsonObject manifest)
                return (PlatformImages.BuiltIn, null, "Lucia records what the Spark runs at its next identity deploy; until then it shows the built-in versions.");
            if ((int?)manifest["schemaVersion"] != 1 || manifest["images"] is not JsonArray list) throw new InvalidDataException();
            var pins = PlatformImages.BuiltIn.ToArray();
            foreach (var item in list.OfType<JsonObject>())
            {
                var index = Array.FindIndex(pins, pin => pin.Service == (string?)item["service"]);
                var (tag, digest) = ((string?)item["tag"] ?? "", (string?)item["digest"] ?? "");
                if (index < 0 || (string?)item["repository"] != pins[index].Repository || (string?)item["registry"] != pins[index].Registry
                    || !PlatformImages.TagPattern().IsMatch(tag) || !PlatformImages.DigestPattern().IsMatch(digest)) throw new InvalidDataException();
                pins[index] = pins[index] with { Tag = tag, Digest = digest };
            }
            return (pins, DateTimeOffset.TryParse((string?)manifest["writtenAt"], out var at) ? at : null, null);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or FormatException)
        {
            logger.LogWarning("The platform image manifest could not be read ({ErrorType}).", error.GetType().Name);
            return (PlatformImages.BuiltIn, null, "Lucia couldn't read its record of the Spark's images; it shows the built-in versions.");
        }
    }

    /// <summary>Checks each running image with its registry every six hours, and right away once it changes.</summary>
    internal async Task Check(CancellationToken ct)
    {
        LoadSeen();
        foreach (var pin in Running().Pins)
        {
            var key = Key(pin);
            if (_found.TryGetValue(key, out var found) && found.At > time.GetUtcNow() - Lifetime) continue;
            _found[key] = await Offers(pin, ct);
        }
        if (_plugin is not { } plugin || plugin.At < time.GetUtcNow() - Lifetime) _plugin = (time.GetUtcNow(), await PluginRelease(ct));
        _checkedAt = time.GetUtcNow();
        await RememberAsync(ct);
    }

    /// <summary>Asks the registries again on the next pass (within a minute), keeping what they said until then.</summary>
    public void Forget()
    {
        foreach (var (key, found) in _found) _found[key] = found with { At = DateTimeOffset.MinValue };
        if (_plugin is { } plugin) _plugin = plugin with { At = DateTimeOffset.MinValue };
    }

    /// <summary>For checks: what a registry would have offered for a running pin.</summary>
    internal void Found(PlatformPin pin, PlatformOffer[] offers) => _found[Key(pin)] = (time.GetUtcNow(), offers, null);

    private async Task<(DateTimeOffset, PlatformOffer[], string?)> Offers(PlatformPin pin, CancellationToken ct)
    {
        var (registry, path) = new ImageRef(pin.Image, pin.Tag, pin.Digest).Source;
        try
        {
            string?[] token = [null, null];
            var tags = await ImageRegistry.Tags(registry, path, token, ct);
            var offers = new List<PlatformOffer>();
            // Rebuilds: the same tag, rebuilt with OS patches. Lucia treats them as patches for every service.
            if (await ImageRegistry.Digest(registry, path, pin.Tag, token, ct) is { } current && current != pin.Digest)
                offers.Add(Offer(pin, pin.Tag, current, "rebuild", true));
            foreach (var (tag, kind, applicable) in PlatformImages.Candidates(pin.Policy, pin.Tag, tags))
                if (await ImageRegistry.Digest(registry, path, tag, token, ct) is { } digest)
                    offers.Add(Offer(pin, tag, digest, kind, applicable));
            return (time.GetUtcNow(), [.. offers], null);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException
            && !ct.IsCancellationRequested)
        {
            logger.LogInformation("Couldn't check {Registry}/{Repository} for newer platform images ({ErrorType}).", registry, path, error.GetType().Name);
            return (time.GetUtcNow(), [], $"Lucia couldn't reach {pin.Registry} to check {pin.Name}.");
        }
    }

    private PlatformOffer Offer(PlatformPin pin, string tag, string digest, string kind, bool applicable) =>
        new(tag, digest, kind, applicable, Notes(pin.Service, tag),
            kind == "release-line" && PlatformImages.Version(tag) is { Numbers: [var year, var minor, ..] } ? $"https://docs.goauthentik.io/releases/{year}.{minor}/" : null,
            _seen.GetOrAdd(SeenKey(pin.Service, tag, digest), _ => time.GetUtcNow()));

    /// <summary>Each project's release notes for a tag.</summary>
    internal static string? Notes(string service, string tag)
    {
        var escaped = Uri.EscapeDataString(tag);
        return service switch
        {
            "authentik" => $"https://github.com/goauthentik/authentik/releases/tag/version/{escaped}",
            "gateway" => $"https://github.com/traefik/traefik/releases/tag/{escaped}",
            "ca" => $"https://github.com/smallstep/certificates/releases/tag/v{escaped}",
            "database" => PlatformImages.Version(tag) is { Numbers: [var major, var minor, ..] } ? $"https://www.postgresql.org/docs/release/{major}.{minor}/" : null,
            "directory" => "https://www.openldap.org/software/release/changes.html",
            "telemetry-relay" => $"https://github.com/open-telemetry/opentelemetry-collector-contrib/releases/tag/v{escaped}",
            "node-exporter" => $"https://github.com/prometheus/node_exporter/releases/tag/{escaped}",
            "gpu-exporter" => $"https://github.com/utkuozdemir/nvidia_gpu_exporter/releases/tag/v{escaped}",
            _ => null,
        };
    }

    /// <summary>The gateway's CrowdSec bouncer plugin's newest GitHub release; applying one stays a code change (its zip is pinned).</summary>
    private async Task<string?> PluginRelease(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{GatewaySecurity.PluginModule["github.com/".Length..]}/releases/latest");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("lucia-homelab", "1"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await GitHub.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var tag = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("tag_name").GetString();
            return tag is not null && PlatformImages.TagPattern().IsMatch(tag) ? tag : null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException
            && !ct.IsCancellationRequested)
        {
            logger.LogInformation("Couldn't read the CrowdSec bouncer plugin's releases ({ErrorType}).", error.GetType().Name);
            return null;
        }
    }

    public object Get()
    {
        var (pins, writtenAt, problem) = Running();
        var status = ReadStatus();
        var request = ReadJson(Path.Combine(Folder, "request.json"), 16 * 1024, quiet: true) as JsonObject;
        var images = pins.Select(RowFor).ToArray();
        var now = time.GetUtcNow();
        var latest = _plugin?.Latest;
        return new
        {
            Worker = new { State = WorkerState(status), InstallCommand },
            Recorded = writtenAt is not null, RecordedAt = writtenAt, Problem = problem, CheckedAt = _checkedAt,
            Images = images,
            Plugin = new
            {
                Name = "CrowdSec bouncer plugin", Version = GatewaySecurity.PluginVersion, Policy = PlatformImages.Plugin,
                Latest = latest is not null && PlatformImages.Compare(GatewaySecurity.PluginVersion, latest) > 0 ? latest : null,
                Notes = latest is null ? null : $"https://github.com/{GatewaySecurity.PluginModule["github.com/".Length..]}/releases/tag/{Uri.EscapeDataString(latest)}",
                Applicable = false,
            },
            Status = status is null ? null : new
            {
                Generation = (long?)status["generation"], State = (string?)status["state"], Step = (string?)status["step"], Message = (string?)status["message"],
                StartedAt = (string?)status["startedAt"], FinishedAt = (string?)status["finishedAt"], RolledBack = (bool?)status["rolledBack"] ?? false,
                Backup = (string?)status["backup"], Updates = status["updates"]?.DeepClone(), History = status["history"]?.DeepClone(),
            },
            Pending = request is not null && (long?)request["generation"] != (long?)status?["generation"],
            SecurityWaiting = images.Where(row => row.Security).SelectMany(row => row.Offers
                    .Where(offer => offer.Kind is "patch" or "rebuild" && offer.Applicable && now - offer.FirstSeenAt > TimeSpan.FromDays(7))
                    .Select(offer => new { row.Service, row.Name, offer.Tag, Days = (int)(now - offer.FirstSeenAt).TotalDays }))
                .ToArray(),
        };
    }

    internal PlatformImageRow RowFor(PlatformPin pin)
    {
        var builtIn = PlatformImages.Pin(PlatformImages.BuiltIn, pin.Service);
        var (_, offers, problem) = _found.TryGetValue(Key(pin), out var value) ? value : (default, [], null);
        return new(pin.Service, pin.Name, pin.Policy, pin.Image, pin.Tag, pin.Digest, pin.Tag != builtIn.Tag || pin.Digest != builtIn.Digest,
            SecurityRelevant.Contains(pin.Service), Disruption([pin.Service]), offers, problem);
    }

    /// <summary>Every platform image with its offers.</summary>
    public PlatformImageRow[] Rows() => [.. Running().Pins.Select(RowFor)];

    /// <summary>What restarts, said before the owner confirms.</summary>
    public static string Disruption(IEnumerable<string> services)
    {
        var chosen = services.ToArray();
        if (chosen.Any(Traffic.Contains))
            return "Every web route through the gateway restarts: Lucia, sign-in and your apps' web addresses are unreachable for about 10 to 15 seconds.";
        if (chosen.Contains("ca")) return "Certificates can't be issued or renewed for a few seconds. Web traffic isn't touched.";
        return "Only telemetry restarts, so a few seconds of Spark metrics are missing. Web traffic isn't touched.";
    }

    /// <summary>
    /// Asks the Spark's worker to update the chosen services to offers Lucia found. The digest always comes from Lucia's own
    /// check, never the caller, and the worker checks it again with the registry.
    /// </summary>
    public async Task<object> Apply(PlatformUpdateRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var status = ReadStatus();
            if (WorkerState(status) == "missing")
                throw new PackageUpdatesException(503, "worker_missing", "The Spark's platform update service isn't set up yet.");
            if (WorkerState(status) != "ready")
                throw new PackageUpdatesException(503, "worker_unavailable", "The Spark's platform update service isn't responding. It may be restarting.");
            var pending = ReadJson(Path.Combine(Folder, "request.json"), 16 * 1024, quiet: true) as JsonObject;
            if ((string?)status!["state"] == "working" || pending is not null && (long?)pending["generation"] != (long?)status["generation"])
                throw new PackageUpdatesException(409, "update_busy", "A platform update is already running. Wait for it to finish.");
            var rows = Rows().ToDictionary(row => row.Service, StringComparer.Ordinal);
            var chosen = Choose(rows, request);
            if (chosen.Count == 0)
                throw new PackageUpdatesException(409, "no_platform_updates", request.PatchOnly
                    ? "Lucia hasn't found a patch for those services." : "Lucia hasn't found an update it can apply to those services.");
            if (chosen.Count > 1 && chosen.Any(item => item.Offer.Kind == "release-line"))
                throw new PackageUpdatesException(400, "release_line_alone", "Update Authentik to a new release line on its own, after the other updates.");
            var generation = Math.Max((long?)pending?["generation"] ?? 0, (long?)status["generation"] ?? 0) + 1;
            var updates = chosen.Select(item => new { service = item.Service, tag = item.Offer.Tag, digest = item.Offer.Digest }).ToArray();
            await DomainOnboardingStore.WriteJson(Path.Combine(Folder, "request.json"),
                new { schemaVersion = 1, generation, requestedAt = time.GetUtcNow(), updates }, ct);
            logger.LogInformation("Requested platform image updates for {Services}.", string.Join(", ", chosen.Select(item => item.Service)));
            return new
            {
                Generation = generation, Updates = chosen.Select(item => new { item.Service, item.Offer.Tag, item.Offer.Kind, item.Offer.Notes }),
                Disruption = Disruption(chosen.Select(item => item.Service))
                    + (chosen.Any(item => item.Offer.Kind == "release-line") ? " Authentik migrates its database to the new release line; the Spark backs it up first." : ""),
            };
        }
        finally { _gate.Release(); }
    }

    private static List<(string Service, PlatformOffer Offer)> Choose(Dictionary<string, PlatformImageRow> rows, PlatformUpdateRequest request)
    {
        var chosen = new List<(string, PlatformOffer)>();
        if (request.Updates is { } updates)
        {
            if (updates.Length > rows.Count || updates.Select(item => item.Service).Distinct().Count() != updates.Length)
                throw new PackageUpdatesException(400, "invalid_updates", "Choose each service once.");
            foreach (var update in updates)
            {
                var row = rows.GetValueOrDefault(update.Service ?? "")
                    ?? throw new PackageUpdatesException(400, "unknown_service", $"{update.Service} isn't one of the Spark's platform images.");
                var offer = update.Tag is null ? Preferred(row, request.PatchOnly) : row.Offers.FirstOrDefault(item => item.Tag == update.Tag && item.Applicable);
                if (offer is null)
                    throw new PackageUpdatesException(409, "update_unavailable", update.Tag is null
                        ? $"Lucia hasn't found an update it can apply to {row.Name}."
                        : $"{update.Tag} isn't an update Lucia can apply to {row.Name}. Majors and the plugin need a new Lucia release.");
                chosen.Add((row.Service, offer));
            }
            return chosen;
        }
        foreach (var service in (request.Services ?? []).Distinct())
        {
            var row = rows.GetValueOrDefault(service ?? "")
                ?? throw new PackageUpdatesException(400, "unknown_service", $"{service} isn't one of the Spark's platform images. Use list_platform_image_updates.");
            if (Preferred(row, request.PatchOnly) is { } offer) chosen.Add((row.Service, offer));
        }
        return chosen;
    }

    /// <summary>Patches and rebuilds first; otherwise the same-major minor, or Authentik's next release line.</summary>
    private static PlatformOffer? Preferred(PlatformImageRow row, bool patchOnly) =>
        row.Offers.Where(offer => offer.Applicable && (!patchOnly || offer.Kind is "patch" or "rebuild"))
            .OrderBy(offer => offer.Kind switch { "patch" => 0, "rebuild" => 1, "minor" => 2, _ => 3 }).FirstOrDefault();

    private string WorkerState(JsonObject? status) => status is null ? "missing"
        : DateTimeOffset.TryParse((string?)status["checkedAt"], out var at) && time.GetUtcNow() - at < TimeSpan.FromMinutes(1) ? "ready" : "stale";

    internal JsonObject? ReadStatus() => ReadJson(Path.Combine(Folder, "status.json"), 64 * 1024, quiet: true) as JsonObject;

    private JsonNode? ReadJson(string path, int limit, bool quiet = false)
    {
        try
        {
            DomainOnboardingStore.RejectLinks(path);
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (info.Length > limit) throw new InvalidDataException("The file is oversized.");
            return JsonNode.Parse(File.ReadAllBytes(path));
        }
        catch (Exception error) when (quiet && error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning("A platform update file could not be read ({ErrorType}).", error.GetType().Name);
            return null;
        }
    }

    // When Lucia first offered each version, kept across restarts so a week-old security patch stays flagged.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private string? _seenSaved;

    private static string Key(PlatformPin pin) => pin.Image + ":" + pin.Tag + "@" + pin.Digest;
    private static string SeenKey(string service, string tag, string digest) => service + ":" + tag + "@" + digest;

    private void LoadSeen()
    {
        if (_seenSaved is not null) return;
        _seenSaved = "";
        try
        {
            if (ReadJson(Path.Combine(Folder, "seen.json"), 64 * 1024) is JsonObject saved)
                foreach (var (key, value) in saved)
                    if (value is JsonValue text && text.TryGetValue(out string? at) && DateTimeOffset.TryParse(at, out var seen)) _seen[key] = seen;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { }
    }

    private async Task RememberAsync(CancellationToken ct)
    {
        var offered = Running().Pins.SelectMany(pin => (_found.TryGetValue(Key(pin), out var found) ? found.Offers : [])
            .Select(offer => SeenKey(pin.Service, offer.Tag, offer.Digest))).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _seen.Keys.Where(key => !offered.Contains(key))) _seen.TryRemove(key, out _);
        var json = JsonSerializer.Serialize(_seen.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value.ToString("O")));
        if (json == _seenSaved || !Directory.Exists(Folder)) return;
        await DomainOnboardingStore.WriteJson(Path.Combine(Folder, "seen.json"), JsonNode.Parse(json), ct);
        _seenSaved = json;
    }
}
/// <summary>Keeps the platform images' updates current, as <see cref="ImageUpdateChecks"/> does for apps.</summary>
public sealed class PlatformImageChecks(PlatformImageUpdates updates, ILogger<PlatformImageChecks> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), stop); }
            catch (OperationCanceledException) { return; }
            try { await updates.Check(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning("Platform image checks failed ({ErrorType}); retrying.", error.GetType().Name); }
        }
    }
}
