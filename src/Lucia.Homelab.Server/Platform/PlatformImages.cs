using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lucia.Homelab.Server.Platform;

/// <summary>A third-party image the Spark runs, as the identity AppHost pins it.</summary>
/// <param name="Service">Lucia's key for it: the platform update worker and the host use it, never a container name.</param>
/// <param name="Registry"><c>docker.io</c> or <c>ghcr.io</c>.</param>
/// <param name="Policy">What Lucia offers: <c>patch</c>, <c>authentik</c> or <c>minor</c> (see <see cref="PlatformImages.Candidates"/>).</param>
/// <param name="Containers">The Compose services that run it; they always change together.</param>
public sealed record PlatformPin(string Service, string Name, string Registry, string Repository, string Tag, string Digest, string Policy,
    string[] Containers)
{
    /// <summary>The reference a registry and <c>docker inspect</c> know, without a tag.</summary>
    public string Image => Registry == "docker.io" ? Repository : Registry + "/" + Repository;
    public string Sha256 => Digest["sha256:".Length..];
}

/// <summary>
/// The Spark's platform images: the built-in pins, the owner's newer pins from <c>images.override.json</c> (written by the
/// Spark's <c>lucia-platform-update</c> worker), and the policy that decides which newer versions Lucia offers. Shared by the
/// identity AppHost, which deploys them, and the server, which finds updates. <c>tools/runners/platform_images.py</c> mirrors
/// the policy and the version rules for the worker.
/// </summary>
public static partial class PlatformImages
{
    public const string OverrideFile = "images.override.json";
    /// <summary>What the AppHost last deployed, in the identity state (trusted by the worker) and the host's data folder.</summary>
    public const string ManifestFile = "platform-images.json";
    /// <summary>The worker's exchange folder in the host's data directory: request, status and the manifest's copy.</summary>
    public const string Folder = "lucia-platform-updates";
    public const string Patch = "patch", Authentik = "authentik", Minor = "minor", Plugin = "plugin";
    private const int MaxOverrideBytes = 16 * 1024;

    // Lucia's releases bump these; an override only bridges the gap until they catch up.
    public static readonly PlatformPin[] BuiltIn =
    [
        new("authentik", "Authentik", "ghcr.io", "goauthentik/server", "2026.8.5",
            "sha256:602227a86455d6772e3d16b7d852713c4f864f40df5df894abd1285bb53979a5", Authentik, ["identity-server", "identity-worker"]),
        new("gateway", "Traefik gateway", "docker.io", "traefik", "v3.7.16",
            "sha256:1336e85aaed293bd63d005086e0dd013b04078b26aaeb1c18b26a2f928af7e65", Minor, ["identity-gateway"]),
        new("ca", "step-ca certificate authority", "docker.io", "smallstep/step-ca", "0.30.2",
            "sha256:a2b17872915c193259b75a5474c398326f41bd199f0842093e52cf4182bc8270", Minor, ["identity-ca", "identity-renewer"]),
        new("database", "PostgreSQL", "docker.io", "postgres", "16.15-alpine",
            "sha256:0cefaabd882e42bcb2a2f3dc56d3ce4770560ba62fd7f197f1a2e008fd5731ab", Patch, ["identity-db"]),
        new("directory", "OpenLDAP", "docker.io", "vegardit/openldap", "2.6.10",
            "sha256:a151f73be24a7c7ce086b1df15880fd9f5a21f9c0b09bc8fb0768f5a890e44f4", Patch, ["identity-ldap"]),
        new("telemetry-relay", "OpenTelemetry relay", "docker.io", "otel/opentelemetry-collector-contrib", "0.164.0",
            "sha256:340c9ca6a36617084c6661bdc4f0fe92b7cda5a805d61673ec8db375fddc522b", Minor, ["telemetry-relay"]),
        new("node-exporter", "Node exporter", "docker.io", "prom/node-exporter", "v1.12.1",
            "sha256:1b4e4438faca4dd7e001dd445d161a4a2091b0fededa84093b3a8dfeae1f1be0", Minor, ["telemetry-node-exporter"]),
        new("gpu-exporter", "NVIDIA GPU exporter", "docker.io", "utkuozdemir/nvidia_gpu_exporter", "1.16.0",
            "sha256:cec17e3ddfce7baefbdf116d1a259e208bb1519e4e0b18b7b0879bde0fcad5a2", Minor, ["telemetry-gpu-exporter"]),
    ];

    public static PlatformPin Pin(IReadOnlyList<PlatformPin> pins, string service) =>
        pins.FirstOrDefault(pin => pin.Service == service) ?? throw new InvalidOperationException($"Unknown platform image {service}.");

    /// <summary>
    /// The pins to deploy. An override replaces a built-in pin while it is newer: a newer tag of the same shape, or a rebuild
    /// of the same tag recorded against the built-in digest still in the code. <paramref name="kept"/> is the file without
    /// the entries a Lucia release caught up with (null when nothing was dropped), so the AppHost can rewrite it.
    /// </summary>
    public static PlatformPin[] Effective(string? overrideJson, out JsonObject? kept)
    {
        kept = null;
        if (overrideJson is null) return BuiltIn;
        if (Encoding.UTF8.GetByteCount(overrideJson) > MaxOverrideBytes) throw Invalid("it is larger than 16 KB");
        JsonObject root;
        try { root = JsonNode.Parse(overrideJson, documentOptions: new() { MaxDepth = 8 }) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw Invalid("it isn't a JSON object"); }
        if (!Keys(root, "schemaVersion", "images") || root["schemaVersion"] is not JsonValue version || version.GetValueKind() != JsonValueKind.Number
            || !version.TryGetValue(out int schema) || schema != 1 || root["images"] is not JsonObject images)
            throw Invalid("it needs exactly schemaVersion 1 and images");
        var pins = BuiltIn.ToArray();
        var keep = new JsonObject();
        foreach (var (service, node) in images)
        {
            var index = Array.FindIndex(pins, pin => pin.Service == service);
            if (node is not JsonObject entry || !Keys(entry, "repository", "tag", "digest", "replaces")
                || entry["replaces"] is not JsonObject replaces || !Keys(replaces, "tag", "digest"))
                throw Invalid($"{service} needs exactly repository, tag, digest and replaces (tag, digest)");
            string Text(JsonObject owner, string name) => owner[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String
                ? value.GetValue<string>() : throw Invalid($"{service}.{name} must be text");
            var (repository, tag, digest) = (Text(entry, "repository"), Text(entry, "tag"), Text(entry, "digest"));
            var (replacedTag, replacedDigest) = (Text(replaces, "tag"), Text(replaces, "digest"));
            if (!TagPattern().IsMatch(tag) || !TagPattern().IsMatch(replacedTag) || !DigestPattern().IsMatch(digest) || !DigestPattern().IsMatch(replacedDigest))
                throw Invalid($"{service} needs plain tags and sha256 digests");
            // An entry a later Lucia release no longer recognises (renamed service, moved image, tighter policy) is dropped, not fatal,
            // so it can never block an identity deploy.
            if (index < 0 || pins[index] is not { } builtIn || repository != builtIn.Image
                || !Allowed(builtIn.Policy, replacedTag, tag) && !(tag == replacedTag && digest != replacedDigest))
                continue;
            var newer = Compare(builtIn.Tag, tag) > 0
                || (tag == builtIn.Tag && digest != builtIn.Digest && replacedTag == builtIn.Tag && replacedDigest == builtIn.Digest);
            if (!newer) continue;
            pins[index] = builtIn with { Tag = tag, Digest = digest };
            keep[service] = entry.DeepClone();
        }
        kept = images.Count == keep.Count ? null : new JsonObject { ["schemaVersion"] = 1, ["images"] = keep };
        return pins;
    }

    /// <summary>What the AppHost deployed: each pin with its built-in counterpart, for the host and the worker.</summary>
    public static string Manifest(IReadOnlyList<PlatformPin> pins, DateTimeOffset at) => new JsonObject
    {
        ["schemaVersion"] = 1,
        ["writtenAt"] = at.ToString("O", CultureInfo.InvariantCulture),
        ["images"] = new JsonArray([.. pins.Select(pin =>
        {
            var builtIn = Pin(BuiltIn, pin.Service);
            return (JsonNode)new JsonObject
            {
                ["service"] = pin.Service, ["name"] = pin.Name, ["registry"] = pin.Registry, ["repository"] = pin.Repository, ["tag"] = pin.Tag,
                ["digest"] = pin.Digest, ["policy"] = pin.Policy, ["containers"] = new JsonArray([.. pin.Containers.Select(name => (JsonNode)name)]),
                ["builtIn"] = new JsonObject { ["tag"] = builtIn.Tag, ["digest"] = builtIn.Digest },
                ["override"] = pin.Tag != builtIn.Tag || pin.Digest != builtIn.Digest,
            };
        })]),
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

    /// <summary>
    /// The newer tags a policy offers from <paramref name="current"/>: every policy a <c>patch</c> (only the last number
    /// changes); <c>authentik</c> also the newest of the next release line only (<c>release-line</c>); <c>minor</c> also the
    /// newest of the same major (<c>minor</c>) and a newer major, which is shown but never applied (<c>major</c>).
    /// </summary>
    public static IEnumerable<(string Tag, string Kind, bool Applicable)> Candidates(string policy, string current, IEnumerable<string> tags)
    {
        if (Version(current) is not { } now) yield break;
        var newer = tags.Distinct(StringComparer.Ordinal).Where(tag => Compare(current, tag) > 0)
            .Select(tag => (Tag: tag, Numbers: Version(tag)!.Value.Numbers)).ToArray();
        string? Best(Func<long[], bool> where) => newer.Where(item => where(item.Numbers)).Select(item => item.Tag)
            .Order(Comparer<string>.Create((a, b) => Compare(b, a))).LastOrDefault();
        bool Same(long[] numbers, int length) => numbers.Take(length).SequenceEqual(now.Numbers.Take(length));
        var head = now.Numbers.Length - 1;
        if (policy is Patch or Authentik or Minor && Best(numbers => Same(numbers, head)) is { } patch)
            yield return (patch, "patch", true);
        if (policy == Authentik && NextLine(now.Numbers, newer.Select(item => item.Numbers)) is { } line
            && Best(numbers => numbers.Take(2).SequenceEqual(line)) is { } release)
            yield return (release, "release-line", true);
        if (policy == Minor)
        {
            if (Best(numbers => Same(numbers, 1) && !Same(numbers, head)) is { } minor) yield return (minor, "minor", true);
            if (Best(numbers => numbers[0] != now.Numbers[0]) is { } major) yield return (major, "major", false);
        }
    }

    /// <summary>
    /// Whether a policy lets <paramref name="current"/> move to the newer tag <paramref name="candidate"/>. For Authentik's
    /// release lines, given the registry's <paramref name="tags"/>, only the next line; without them, any later line.
    /// </summary>
    public static bool Allowed(string policy, string current, string candidate, IEnumerable<string>? tags = null)
    {
        if (Compare(current, candidate) <= 0 || Version(current) is not { } now || Version(candidate) is not { } next) return false;
        return policy switch
        {
            Patch => next.Numbers[..^1].SequenceEqual(now.Numbers[..^1]),
            Minor => next.Numbers[0] == now.Numbers[0],
            Authentik => now.Numbers.Length >= 3 && (next.Numbers[..2].SequenceEqual(now.Numbers[..2]) || tags is null
                || NextLine(now.Numbers, tags.Where(tag => Compare(current, tag) > 0).Select(tag => Version(tag)!.Value.Numbers)) is { } line
                    && next.Numbers[..2].SequenceEqual(line)),
            _ => false,
        };
    }

    /// <summary>The first release line (year.minor) after the current one, among newer versions of the same shape.</summary>
    private static long[]? NextLine(long[] current, IEnumerable<long[]> newer) =>
        current.Length < 3 ? null : newer.Where(numbers => !numbers.Take(2).SequenceEqual(current.Take(2)))
            .Select(numbers => numbers[..2]).OrderBy(numbers => numbers[0]).ThenBy(numbers => numbers[1]).FirstOrDefault();

    /// <summary>A tag's numbers and its shape with each number replaced, read exactly as <c>StackImages.Version</c> reads it.</summary>
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

    private static bool Keys(JsonObject value, params string[] names) => value.Count == names.Length && names.All(value.ContainsKey);

    private static InvalidOperationException Invalid(string why) =>
        new($"images.override.json is invalid ({why}). Remove it to deploy the built-in images, then update again from Lucia.");

    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9._-]{0,127}\z")]
    public static partial Regex TagPattern();
    [GeneratedRegex(@"\Asha256:[a-f0-9]{64}\z")]
    public static partial Regex DigestPattern();
    [GeneratedRegex(@"([._-])")]
    private static partial Regex Separator();
    [GeneratedRegex(@"\d+|\D+")]
    private static partial Regex Token();
}
