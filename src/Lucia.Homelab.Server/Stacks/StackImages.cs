using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Onboarding;
using Microsoft.AspNetCore.DataProtection;

namespace Lucia.Homelab.Server.Stacks;

/// <summary>A newer tag of one of a stack's images, found in its registry.</summary>
/// <param name="Image">The image as the compose writes it, without tag or digest.</param>
/// <param name="Major">Its first version number changed, which usually means breaking changes.</param>
/// <param name="Notes">Its project's release notes, when the image names its GitHub source.</param>
public sealed record ImageUpdate(string Image, string Current, string Tag, string Digest, bool Major, string? Notes = null);
/// <param name="Major">Also take newer major versions.</param>
public sealed record UpgradeImagesRequest(bool Major = false);

/// <summary>An image reference in a compose file: <c>repository:tag</c>, optionally pinned <c>@sha256:…</c>.</summary>
internal sealed record ImageRef(string Repository, string Tag, string? Digest)
{
    /// <summary>The registry host and repository path to ask, with Docker Hub's defaults filled in.</summary>
    public (string Registry, string Path) Source
    {
        get
        {
            var slash = Repository.IndexOf('/');
            var host = slash > 0 ? Repository[..slash] : "";
            var (registry, path) = host.Contains('.') || host.Contains(':') || host == "localhost" ? (host, Repository[(slash + 1)..]) : ("docker.io", Repository);
            return registry == "docker.io" ? ("registry-1.docker.io", path.Contains('/') ? path : "library/" + path) : (registry, path);
        }
    }
}

/// <summary>Reads, compares and rewrites the image tags in compose files.</summary>
internal static partial class StackImages
{
    /// <summary>The compose's images, less Lucia's own helper, which updates with Lucia.</summary>
    public static IEnumerable<ImageRef> Refs(string compose)
    {
        foreach (Match line in ImageLine().Matches(compose))
            if (line.Groups["ref"].Value != ObservabilityApp.Alpine && Parse(line.Groups["ref"].Value) is { } image) yield return image;
    }

    public static ImageRef? Parse(string written)
    {
        if (written.Contains('$')) return null;
        var at = written.IndexOf('@');
        var (name, digest) = at < 0 ? (written, null) : (written[..at], written[(at + 1)..]);
        var colon = name.LastIndexOf(':');
        return colon > name.LastIndexOf('/') && colon > 0 ? new(name[..colon], name[(colon + 1)..], digest) : null;
    }

    /// <summary>
    /// A tag's version: its numbers, and its shape with each number replaced so only tags of the same kind compare
    /// (<c>v8.9.0-ls104</c> with <c>v9.1.0-ls108</c>, never with <c>9.1.0-dev</c>). Commit hashes, as in Plex's tags, are ignored.
    /// </summary>
    public static (string Shape, long[] Numbers)? Version(string tag)
    {
        var shape = new StringBuilder();
        var numbers = new List<long>();
        foreach (var part in Separator().Split(tag))
        {
            if (part.Length == 0) continue;
            if (part is "." or "-" or "_") { shape.Append(part); continue; }
            if (part.Length >= 7 && part.All(char.IsAsciiHexDigitLower) && part.Any(char.IsAsciiLetter) && part.Any(char.IsAsciiDigit)) { shape.Append('#'); continue; }
            foreach (Match token in Token().Matches(part))
            {
                if (!char.IsAsciiDigit(token.Value[0])) { shape.Append(token.Value); continue; }
                if (!long.TryParse(token.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return null;
                numbers.Add(number);
                shape.Append('0');
            }
        }
        return numbers.Count == 0 ? null : (shape.ToString(), numbers.ToArray());
    }

    /// <summary>Positive when <paramref name="candidate"/> is a newer tag of the same shape as <paramref name="current"/>.</summary>
    public static int Compare(string current, string candidate) =>
        Version(current) is { } a && Version(candidate) is { } b && a.Shape == b.Shape
            ? a.Numbers.Zip(b.Numbers, (x, y) => y.CompareTo(x)).FirstOrDefault(order => order != 0) : 0;

    /// <summary>Databases can need their data migrated between versions, so Lucia only offers them patch releases (a change in the last number).</summary>
    public static bool DataStore(string repository) => DataStoreName().IsMatch(repository.Split('/')[^1]);

    /// <summary>
    /// Replaces the tag (and digest, when the compose pins one) of every image named in <paramref name="images"/>, whose
    /// values are <c>tag@digest</c>.
    /// </summary>
    public static string Apply(string compose, IReadOnlyDictionary<string, string>? images) => images is not { Count: > 0 } ? compose
        : ImageLine().Replace(compose, line => Parse(line.Groups["ref"].Value) is { } image && images.TryGetValue(image.Repository, out var pinned)
            ? line.Groups["lead"].Value + image.Repository + ":" + (image.Digest is null ? pinned.Split('@')[0] : pinned)
            : line.Value);

    /// <summary>The overrides still newer than what the catalog renders, so a catalog update that catches up drops them.</summary>
    public static Dictionary<string, string>? Keep(string compose, IReadOnlyDictionary<string, string>? images)
    {
        if (images is not { Count: > 0 }) return null;
        var rendered = Refs(compose).ToArray();
        var kept = images.Where(item => rendered.Any(image => image.Repository == item.Key && Compare(image.Tag, item.Value.Split('@')[0]) > 0))
            .ToDictionary(StringComparer.Ordinal);
        return kept.Count == 0 ? null : kept;
    }

    [GeneratedRegex(@"(?m)^(?<lead>[ \t]*image:[ \t]*[""']?)(?<ref>[^\s""'#]+)")]
    private static partial Regex ImageLine();
    [GeneratedRegex(@"([._-])")]
    private static partial Regex Separator();
    [GeneratedRegex(@"\d+|\D+")]
    private static partial Regex Token();
    [GeneratedRegex(@"postgres|postgis|pgvecto|mariadb|mysql|mongo|redis|valkey|keydb|elasticsearch|opensearch|clickhouse|influxdb|-db$")]
    private static partial Regex DataStoreName();
}

/// <summary>
/// What each stack image's registry offers. Every image is checked every few hours; images Lucia hasn't seen yet, such
/// as a new app's or a just-updated one's, within a minute.
/// </summary>
public sealed partial class ImageRegistry(TimeProvider time, ILogger<ImageRegistry> logger)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);
    private const int MaxPages = 50;
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 8 * 1024 * 1024,
    };
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, ImageUpdate[] Updates)> _found = new();

    /// <summary>The newest tag of each image's major version and, unless it's a database, the newest overall when that's a new major.</summary>
    internal ImageUpdate[] Updates(string compose) =>
        [.. StackImages.Refs(compose).DistinctBy(image => image.Repository + ":" + image.Tag)
            .SelectMany(image => _found.TryGetValue(Key(image), out var found) ? found.Updates : [])];

    /// <summary>Checks this compose's images again on the next pass.</summary>
    internal void Forget(string compose)
    {
        foreach (var image in StackImages.Refs(compose)) _found.TryRemove(Key(image), out _);
    }

    internal async Task Check(IEnumerable<string> composes, CancellationToken ct)
    {
        var stale = composes.SelectMany(StackImages.Refs).Where(image => StackImages.Version(image.Tag) is not null)
            .DistinctBy(Key).Where(image => !_found.TryGetValue(Key(image), out var found) || found.At < time.GetUtcNow() - Lifetime).ToArray();
        foreach (var repository in stale.GroupBy(image => image.Source))
        {
            try
            {
                var token = new string?[1];
                var tags = await Tags(repository.Key.Registry, repository.Key.Path, token, ct);
                foreach (var image in repository)
                {
                    var first = StackImages.Version(image.Tag)!.Value.Numbers[0];
                    var newer = tags.Where(tag => StackImages.Compare(image.Tag, tag) > 0).ToArray();
                    var numbers = StackImages.Version(image.Tag)!.Value.Numbers;
                    var dataStore = StackImages.DataStore(image.Repository);
                    string? best(bool major) => newer.Where(tag => (StackImages.Version(tag)!.Value.Numbers[0] != first) == major
                            && (!dataStore || StackImages.Version(tag)!.Value.Numbers[..^1].SequenceEqual(numbers[..^1])))
                        .MaxBy(tag => tag, Comparer<string>.Create((a, b) => StackImages.Compare(b, a)));
                    var updates = new List<ImageUpdate>();
                    foreach (var (tag, major) in new[] { (best(false), false), (dataStore ? null : best(true), true) })
                        if (tag is not null && await Digest(repository.Key.Registry, repository.Key.Path, tag, token, ct) is { } digest)
                            updates.Add(new(image.Repository, image.Tag, tag, digest, major, await Notes(repository.Key.Registry, repository.Key.Path, tag, digest, token, ct)));
                    _found[Key(image)] = (time.GetUtcNow(), [.. updates]);
                }
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException
                && !ct.IsCancellationRequested)
            {
                logger.LogInformation("Couldn't check {Registry}/{Repository} for newer images ({ErrorType}).", repository.Key.Registry, repository.Key.Path, error.GetType().Name);
                foreach (var image in repository) _found[Key(image)] = (time.GetUtcNow(), []);
            }
        }
    }

    private static string Key(ImageRef image) => image.Repository + ":" + image.Tag;

    private static async Task<List<string>> Tags(string registry, string path, string?[] token, CancellationToken ct)
    {
        var tags = new List<string>();
        var next = $"/v2/{path}/tags/list?n=1000";
        for (var page = 0; next is not null && page < MaxPages; page++)
        {
            using var response = await Send(HttpMethod.Get, new Uri($"https://{registry}{next}"), registry, path, token, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (body.TryGetProperty("tags", out var list) && list.ValueKind == JsonValueKind.Array)
                tags.AddRange(list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!));
            next = response.Headers.TryGetValues("Link", out var links) && NextLink().Match(string.Join(",", links)) is { Success: true } link
                && link.Groups[1].Value.StartsWith("/v2/", StringComparison.Ordinal) ? link.Groups[1].Value : null;
        }
        return tags;
    }

    private static async Task<string?> Digest(string registry, string path, string tag, string?[] token, CancellationToken ct)
    {
        using var response = await Send(HttpMethod.Head, new Uri($"https://{registry}/v2/{path}/manifests/{Uri.EscapeDataString(tag)}"), registry, path, token, ct);
        return response.IsSuccessStatusCode && response.Headers.TryGetValues("Docker-Content-Digest", out var values)
            && values.FirstOrDefault() is { } digest && DigestPattern().IsMatch(digest) ? digest : null;
    }

    /// <summary>
    /// The release notes page of the project an image names as its source (the OCI <c>org.opencontainers.image.source</c>
    /// label). LinuxServer tags its GitHub releases exactly as its images, so those link straight to the release.
    /// </summary>
    private async Task<string?> Notes(string registry, string path, string tag, string digest, string?[] token, CancellationToken ct)
    {
        try
        {
            var manifest = await Json(new Uri($"https://{registry}/v2/{path}/manifests/{digest}"), registry, path, token, ct);
            if (manifest.TryGetProperty("manifests", out var platforms) && platforms.ValueKind == JsonValueKind.Array
                && platforms.EnumerateArray().FirstOrDefault(item => !(item.TryGetProperty("platform", out var platform)
                    && platform.TryGetProperty("os", out var os) && os.GetString() == "unknown")) is { ValueKind: JsonValueKind.Object } chosen
                && chosen.GetProperty("digest").GetString() is { } inner && DigestPattern().IsMatch(inner))
                manifest = await Json(new Uri($"https://{registry}/v2/{path}/manifests/{inner}"), registry, path, token, ct);
            if (manifest.GetProperty("config").GetProperty("digest").GetString() is not { } config || !DigestPattern().IsMatch(config)) return null;
            var labels = (await Json(new Uri($"https://{registry}/v2/{path}/blobs/{config}"), registry, path, token, ct)).GetProperty("config").GetProperty("Labels");
            if (labels.ValueKind != JsonValueKind.Object || !labels.TryGetProperty("org.opencontainers.image.source", out var source)
                || GitHubRepository().Match(source.GetString() ?? "") is not { Success: true } repository) return null;
            var releases = $"https://github.com/{repository.Groups[1].Value}/{repository.Groups[2].Value}/releases";
            return repository.Groups[1].Value.Equals("linuxserver", StringComparison.OrdinalIgnoreCase) ? $"{releases}/tag/{Uri.EscapeDataString(tag)}" : releases;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException
            && !ct.IsCancellationRequested)
        {
            logger.LogDebug("Couldn't read {Registry}/{Repository}:{Tag}'s source ({ErrorType}).", registry, path, tag, error.GetType().Name);
            return null;
        }
    }

    private static async Task<JsonElement> Json(Uri uri, string registry, string path, string?[] token, CancellationToken ct)
    {
        using var response = await Send(HttpMethod.Get, uri, registry, path, token, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    /// <summary>Sends anonymously, fetching a pull token when the registry asks for one.</summary>
    private static async Task<HttpResponseMessage> Send(HttpMethod method, Uri uri, string registry, string path, string?[] token, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, uri);
            if (token[0] is { } bearer) request.Headers.Authorization = new("Bearer", bearer);
            if (uri.AbsolutePath.Contains("/manifests/", StringComparison.Ordinal))
                foreach (var type in new[] { "application/vnd.oci.image.index.v1+json", "application/vnd.docker.distribution.manifest.list.v2+json",
                    "application/vnd.oci.image.manifest.v1+json", "application/vnd.docker.distribution.manifest.v2+json" })
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(type));
            var response = await Http.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0
                || response.Headers.WwwAuthenticate.FirstOrDefault(item => item.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)) is not { Parameter: { } parameter })
                return response;
            response.Dispose();
            var values = AuthParameter().Matches(parameter).ToDictionary(match => match.Groups[1].Value.ToLowerInvariant(), match => match.Groups[2].Value);
            if (!values.TryGetValue("realm", out var realm) || !Uri.TryCreate(realm, UriKind.Absolute, out var realmUri) || realmUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException($"{registry} asked for sign-in Lucia can't do.");
            var query = $"scope={Uri.EscapeDataString($"repository:{path}:pull")}"
                + (values.TryGetValue("service", out var service) ? $"&service={Uri.EscapeDataString(service)}" : "");
            using var granted = await Http.GetAsync(new UriBuilder(realmUri) { Query = query }.Uri, ct);
            granted.EnsureSuccessStatusCode();
            var body = await granted.Content.ReadFromJsonAsync<JsonElement>(ct);
            token[0] = (body.TryGetProperty("token", out var value) || body.TryGetProperty("access_token", out value)) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : throw new InvalidOperationException($"{registry} returned no pull token.");
        }
    }

    [GeneratedRegex(@"<([^>]+)>\s*;\s*rel=""?next""?")]
    private static partial Regex NextLink();
    [GeneratedRegex(@"(\w+)=""([^""]*)""")]
    private static partial Regex AuthParameter();
    [GeneratedRegex(@"\Asha256:[0-9a-f]{64}\z")]
    private static partial Regex DigestPattern();
    [GeneratedRegex(@"\Ahttps://github\.com/([A-Za-z0-9-]+)/([A-Za-z0-9._-]+?)(?:\.git)?/?\z")]
    private static partial Regex GitHubRepository();
}

/// <summary>Keeps every app's newer image tags current, so its page can offer them.</summary>
public sealed class ImageUpdateChecks(StackStore stacks, ImageRegistry registry, ILogger<ImageUpdateChecks> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), stop); }
            catch (OperationCanceledException) { return; }
            try { await registry.Check(await stacks.CheckedComposes(stop), stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning("Image update checks failed ({ErrorType}); retrying.", error.GetType().Name); }
        }
    }
}

public sealed partial class StackStore
{
    /// <summary>
    /// Every stack's compose except apps set up for one server's hardware, whose images follow its CUDA line and change
    /// only with Lucia's catalog.
    /// </summary>
    internal async Task<string[]> CheckedComposes(CancellationToken ct) =>
        [.. (await Read(ct)).Where(stack => !FollowsCatalog(stack)).Select(stack => stack.Compose)];

    private static bool FollowsCatalog(StoredStack stack) =>
        stack.Manifest.Template is { } template && StackCatalog.Apps.FirstOrDefault(app => app.Id == template.Id) is { ServerBound: true };

    private ImageUpdate[] Updates(StoredStack stack) => FollowsCatalog(stack) ? [] : images.Updates(stack.Compose);

    /// <summary>
    /// Moves the app to the newer image tags Lucia found: a catalog app keeps them as overrides until its catalog version
    /// catches up; a custom app's compose file is rewritten.
    /// </summary>
    public async Task<object> UpgradeImages(string name, UpgradeImagesRequest request, string actor, CancellationToken ct)
    {
        var stack = Find(await Read(ct), name);
        var chosen = Updates(stack).GroupBy(update => update.Image)
            .Select(group => (request.Major ? group.FirstOrDefault(update => update.Major) : null) ?? group.FirstOrDefault(update => !update.Major))
            .OfType<ImageUpdate>().ToDictionary(update => update.Image, update => $"{update.Tag}@{update.Digest}", StringComparer.Ordinal);
        if (chosen.Count == 0)
            throw new HardwareOnboardingException(409, "no_image_updates", "Lucia hasn't found newer images for this app.");
        if (stack.Manifest.Template is not { } template)
            return await Save(name, new(StackImages.Apply(stack.Compose, chosen), _protector.Unprotect(stack.ProtectedEnv), stack.Manifest, stack.Revision), actor, ct);
        var pinned = new Dictionary<string, string>(template.Images ?? [], StringComparer.Ordinal);
        foreach (var (image, value) in chosen) pinned[image] = value;
        return await Save(name, new(null, null, stack.Manifest with { Template = template with { Images = pinned } }, stack.Revision), actor, ct);
    }
}
