using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lucia.Homelab.Server.Host;

public sealed record HuggingFaceSearchItem(string Repository, string? PipelineTag, long? Downloads, long? Likes, bool Gated, bool Private);
public sealed record HuggingFaceSearchResult(string Query, ModelKind Kind, IReadOnlyList<HuggingFaceSearchItem> Items,
    int Limit, bool LimitReached, string Compatibility);
public sealed record HuggingFaceFile(string Path, long SizeBytes);
public sealed record HuggingFaceGgufMetadata(string Source, string Scope, string? Architecture, long? ContextLength);
public sealed record HuggingFaceModelChoice(string File, IReadOnlyList<HuggingFaceFile> Files, long TotalSizeBytes,
    string? Quantization, string LabelSource, string Compatibility, ModelDownloadRequest Download);
public sealed record HuggingFaceRepositoryResult(string Repository, string RequestedRevision, string Revision,
    ModelKind Kind, bool Gated, bool Private, string Availability, IReadOnlyList<HuggingFaceModelChoice> Choices,
    HuggingFaceGgufMetadata? GgufMetadata, IReadOnlyList<string> Warnings);

public sealed class HuggingFaceBrowserService(HuggingFaceCredentialService credentials, HttpClient http)
{
    public const int SearchLimit = 30;
    public const int MaximumRepositoryFiles = 4096;
    public const int MaximumChoices = 256;
    private const string Compatibility = "unverified";
    private static readonly Regex Shard = new(@"-(\d{5})-of-(\d{5})\.gguf\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Quantization = new(@"(?:\A|[._/-])((?:IQ|Q|TQ)[1-8](?:_[A-Z0-9]+)*|BF16|F16|F32)(?=[._/-]|\z)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Auxiliary = new(@"(?:\A|[._/-])(mmproj|projector|adapter|lora)(?:[._/-]|\z)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public async Task<HuggingFaceSearchResult> SearchAsync(string? query, ModelKind kind, CancellationToken cancellationToken = default)
    {
        ValidateKind(kind);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 100 || query.Any(char.IsControl)
            || query.Contains("hf_", StringComparison.OrdinalIgnoreCase))
            throw new HuggingFaceManagementException(400, "invalid_query", "Search must contain 1 to 100 characters and must not contain an access token or control characters.");
        query = query.Trim();
        var access = await credentials.GetDownloadCredentialsAsync(cancellationToken);
        // HF's official HfApi.list_models uses filter, pipeline_tag, search, expand, and limit.
        // One page only: no Link traversal or full-Hub crawl.
        var pipeline = kind == ModelKind.Chat ? "text-generation" : "feature-extraction";
        var path = $"/api/models?search={Uri.EscapeDataString(query)}&filter=gguf&pipeline_tag={pipeline}&limit={SearchLimit}"
            + "&sort=downloads&direction=-1&expand=pipeline_tag&expand=downloads&expand=likes&expand=gated&expand=private";
        using var document = await HuggingFaceManagementHttp.GetJsonAsync(http, path, access.Token, 256 * 1024, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw HuggingFaceManagementHttp.InvalidResponse();
        var items = new List<HuggingFaceSearchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray().Take(SearchLimit))
        {
            var repository = HuggingFaceManagementHttp.String(item, "id");
            if (!ValidRepository(repository) || !seen.Add(repository!)) continue;
            items.Add(new(repository!, SafeLabel(HuggingFaceManagementHttp.String(item, "pipeline_tag"), 80),
                NonnegativeNumber(item, "downloads"), NonnegativeNumber(item, "likes"), IsGated(item), IsTrue(item, "private")));
        }
        return new(query, kind, items, SearchLimit, document.RootElement.GetArrayLength() >= SearchLimit, Compatibility);
    }

    public async Task<HuggingFaceRepositoryResult> GetRepositoryAsync(string? repository, string? revision = null,
        ModelKind kind = ModelKind.Chat, CancellationToken cancellationToken = default)
    {
        ValidateKind(kind);
        if (!ValidRepository(repository))
            throw new HuggingFaceManagementException(400, "invalid_repository", "Repository must be a Hugging Face owner/name identifier, at most 200 characters.");
        revision ??= "main";
        if (!ValidRevision(revision))
            throw new HuggingFaceManagementException(400, "invalid_revision", "Revision must be a branch, tag, or commit of at most 200 characters without traversal or URL syntax.");
        var access = await credentials.GetDownloadCredentialsAsync(cancellationToken);
        // Official HfApi.model_info(revision=..., files_metadata=True) uses this path and blobs=true.
        // The SHA and full sibling list describe the same revision; all download requests use the returned SHA.
        var path = $"/api/models/{repository}/revision/{Uri.EscapeDataString(revision)}?blobs=true";
        using var document = await HuggingFaceManagementHttp.GetJsonAsync(http, path, access.Token, 2 * 1024 * 1024, cancellationToken);
        var root = document.RootElement;
        var sha = HuggingFaceManagementHttp.String(root, "sha");
        if (sha is null || !Regex.IsMatch(sha, @"\A[0-9a-fA-F]{40}\z", RegexOptions.CultureInvariant)
            || HuggingFaceManagementHttp.String(root, "id") != repository
            || (Regex.IsMatch(revision, @"\A[0-9a-fA-F]{40}\z", RegexOptions.CultureInvariant)
                && !sha.Equals(revision, StringComparison.OrdinalIgnoreCase))
            || !root.TryGetProperty("siblings", out var siblings) || siblings.ValueKind != JsonValueKind.Array)
            throw HuggingFaceManagementHttp.InvalidResponse();
        if (siblings.GetArrayLength() > MaximumRepositoryFiles)
            throw new HuggingFaceManagementException(422, "repository_too_large", "This browser supports repositories with at most 4096 files. Choose a smaller repository.");

        var files = new Dictionary<string, long>(StringComparer.Ordinal);
        var warnings = new List<string>
        {
            "GGUF and filename labels do not guarantee TensorSharp compatibility. Installed files must pass local inspection before loading.",
            "Context estimates require model metadata and the host memory planner; quantization labels alone are not an estimate."
        };
        var excluded = 0;
        var unsized = 0;
        foreach (var sibling in siblings.EnumerateArray())
        {
            var file = HuggingFaceManagementHttp.String(sibling, "rfilename");
            if (file is null || !file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
            if (!ValidFile(file) || Auxiliary.IsMatch(file)) { excluded++; continue; }
            var size = NonnegativeNumber(sibling, "size");
            if (size is null && sibling.TryGetProperty("lfs", out var lfs))
                size = NonnegativeNumber(lfs, "size");
            if (size is null or 0) { unsized++; continue; }
            if (!files.TryAdd(file, size.Value)) throw HuggingFaceManagementHttp.InvalidResponse();
        }

        var choices = new List<HuggingFaceModelChoice>();
        var incomplete = false;
        foreach (var (file, size) in files.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var match = Shard.Match(file);
            var parts = new List<HuggingFaceFile>();
            if (match.Success)
            {
                var index = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var count = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (count is < 1 or > 128 || index < 1 || index > count) { incomplete = true; continue; }
                var prefix = file[..match.Index];
                var suffix = file[^5..];
                for (var i = 1; i <= count; i++)
                {
                    var shard = $"{prefix}-{i:D5}-of-{count:D5}{suffix}";
                    if (!files.TryGetValue(shard, out var shardSize)) { incomplete = true; parts.Clear(); break; }
                    parts.Add(new(shard, shardSize));
                }
                if (index != 1 || parts.Count != count) continue;
            }
            else
            {
                // Nonstandard split names cannot safely be treated as standalone files.
                if (Regex.IsMatch(file, @"-\d+-of-\d+\.gguf\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                { incomplete = true; continue; }
                parts.Add(new(file, size));
            }
            if (choices.Count == MaximumChoices)
                throw new HuggingFaceManagementException(422, "too_many_model_choices", "This repository exceeds the browser's limit of 256 standalone model choices.");
            long total = 0;
            foreach (var part in parts)
            {
                if (part.SizeBytes > long.MaxValue - total) throw HuggingFaceManagementHttp.InvalidResponse();
                total += part.SizeBytes;
            }
            var quant = Quantization.Match(file);
            choices.Add(new(file, parts, total, quant.Success ? quant.Groups[1].Value.ToUpperInvariant() : null,
                quant.Success ? "filename_inferred" : "unknown", Compatibility,
                new ModelDownloadRequest("huggingface", repository!, file, kind, sha.ToLowerInvariant(), Pro: false)));
        }
        if (excluded > 0) warnings.Add("Projector, adapter, LoRA, or unsafe paths were excluded from standalone choices.");
        if (unsized > 0) warnings.Add("Some GGUF files had no usable byte size and were excluded.");
        if (incomplete) warnings.Add("Incomplete, invalid, or oversized split GGUF groups were excluded. Select only a complete first-shard group (up to 128 shards).");
        if (choices.Count == 0) warnings.Add("No standalone GGUF with a known size and a complete shard group is available at this revision.");
        if (IsGated(root)) warnings.Add("This repository is gated. Downloads may require a read token, accepted terms, and approval on Hugging Face.");
        return new(repository!, revision, sha.ToLowerInvariant(), kind, IsGated(root), IsTrue(root, "private"),
            choices.Count == 0 ? "no_standalone_gguf" : "available", choices, Metadata(root), warnings);
    }

    private static HuggingFaceGgufMetadata? Metadata(JsonElement root)
    {
        if (!root.TryGetProperty("gguf", out var gguf) || gguf.ValueKind != JsonValueKind.Object) return null;
        var architecture = SafeLabel(HuggingFaceManagementHttp.String(gguf, "architecture"), 80);
        var context = NonnegativeNumber(gguf, "context_length");
        if (context is 0) context = null;
        return architecture is null && context is null ? null : new("huggingface_api", "repository", architecture, context);
    }

    private static long? NonnegativeNumber(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0 ? number : null;

    private static bool IsTrue(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool IsGated(JsonElement root) => IsTrue(root, "gated")
        || HuggingFaceManagementHttp.String(root, "gated") is "auto" or "manual";

    private static string? SafeLabel(string? value, int maximum) =>
        value is not null && value.Length <= maximum && !value.Any(char.IsControl) ? value : null;

    private static bool ValidRepository(string? repository) => repository is { Length: <= 200 }
        && Regex.IsMatch(repository, @"\A[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*\z", RegexOptions.CultureInvariant)
        && !repository.Contains("..", StringComparison.Ordinal) && !repository.Contains("--", StringComparison.Ordinal)
        && !repository.Split('/').Any(part => part.EndsWith('.') || part.EndsWith(".git", StringComparison.OrdinalIgnoreCase));

    private static bool ValidRevision(string revision) => revision.Length is > 0 and <= 200
        && Regex.IsMatch(revision, @"\A[A-Za-z0-9][A-Za-z0-9._/-]*\z", RegexOptions.CultureInvariant)
        && !revision.Contains("..", StringComparison.Ordinal)
        && revision.Split('/').All(part => part.Length > 0 && part != "." && !part.StartsWith('-') && !part.EndsWith('.'));

    private static bool ValidFile(string file) => file.Length <= 400
        && !file.Any(c => char.IsControl(c) || "<>:\"\\|?*".Contains(c))
        && file.Split('/').All(part => part.Length > 0 && part is not "." and not ".."
            && !part.StartsWith('-') && !part.EndsWith('.') && !part.EndsWith(' '));

    private static void ValidateKind(ModelKind kind)
    {
        if (!Enum.IsDefined(kind))
            throw new HuggingFaceManagementException(400, "invalid_kind", "Kind must be Chat or Embedding.");
    }
}
