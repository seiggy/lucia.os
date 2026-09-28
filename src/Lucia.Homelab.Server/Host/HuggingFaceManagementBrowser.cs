using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lucia.Homelab.Server.Host;

public sealed record HuggingFaceSearchItem(string Repository, string? PipelineTag, long? Downloads, long? Likes, bool Gated, bool Private,
    string? Architecture = null, long? Parameters = null, string? Quantization = null);
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
    private static string? QuantizationOf(string file) => ModelCatalog.QuantizationOf(file);
    private static readonly Regex Auxiliary = new(@"(?:\A|[._/-])(mmproj|projector|adapter|lora)(?:[._/-]|\z)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public async Task<HuggingFaceSearchResult> SearchAsync(string? query, ModelKind kind, CancellationToken cancellationToken = default,
        ModelFormat format = ModelFormat.Gguf)
    {
        ValidateKind(kind, format);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 100 || query.Any(char.IsControl)
            || query.Contains("hf_", StringComparison.OrdinalIgnoreCase))
            throw new HuggingFaceManagementException(400, "invalid_query", "Search must contain 1 to 100 characters and must not contain an access token or control characters.");
        query = query.Trim();
        var access = await credentials.GetDownloadCredentialsAsync(cancellationToken);
        // HF's official HfApi.list_models uses filter, pipeline_tag, search, expand, and limit.
        // One page only: no Link traversal or full-Hub crawl.
        var pipeline = kind == ModelKind.Chat ? "text-generation" : "feature-extraction";
        var vllm = format == ModelFormat.Safetensors;
        var path = $"/api/models?search={Uri.EscapeDataString(query)}&filter={(vllm ? "safetensors" : "gguf")}&pipeline_tag={pipeline}&limit={SearchLimit}"
            + "&sort=downloads&direction=-1&expand=pipeline_tag&expand=downloads&expand=likes&expand=gated&expand=private"
            + (vllm ? "&expand=config&expand=safetensors&expand=library_name&expand=tags" : "");
        using var document = await HuggingFaceManagementHttp.GetJsonAsync(http, path, access.Token, vllm ? 1024 * 1024 : 256 * 1024, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw HuggingFaceManagementHttp.InvalidResponse();
        var items = new List<HuggingFaceSearchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray().Take(SearchLimit))
        {
            var repository = HuggingFaceManagementHttp.String(item, "id");
            if (!ValidRepository(repository) || !seen.Add(repository!)) continue;
            string? architecture = null, quantization = null;
            long? parameters = null;
            if (vllm)
            {
                // vLLM loads by architecture, so a repository whose config names one the image lacks can't run.
                (architecture, quantization) = Config(item);
                if (architecture is null || !VllmArchitectures.Supported.Contains(architecture) || IsMlx(item)) continue;
                if (item.TryGetProperty("safetensors", out var weights)) parameters = NonnegativeNumber(weights, "total");
            }
            items.Add(new(repository!, SafeLabel(HuggingFaceManagementHttp.String(item, "pipeline_tag"), 80),
                NonnegativeNumber(item, "downloads"), NonnegativeNumber(item, "likes"), IsGated(item), IsTrue(item, "private"),
                architecture, parameters, quantization));
        }
        return new(query, kind, items, SearchLimit, document.RootElement.GetArrayLength() >= SearchLimit, Compatibility);
    }

    /// <summary>MLX conversions keep the original architecture name but store weights vLLM can't read.</summary>
    private static bool IsMlx(JsonElement item) =>
        HuggingFaceManagementHttp.String(item, "library_name") == "mlx"
        || (item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            && tags.EnumerateArray().Any(tag => tag.ValueKind == JsonValueKind.String && tag.GetString() == "mlx"));

    /// <summary>The first architecture and the quantization method from a model's config summary.</summary>
    private static (string? Architecture, string? Quantization) Config(JsonElement root)
    {
        if (!root.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object) return (null, null);
        var architecture = config.TryGetProperty("architectures", out var list) && list.ValueKind == JsonValueKind.Array
            && list.GetArrayLength() > 0 && list[0].ValueKind == JsonValueKind.String ? SafeLabel(list[0].GetString(), 120) : null;
        var quantization = config.TryGetProperty("quantization_config", out var quant)
            ? SafeLabel(HuggingFaceManagementHttp.String(quant, "quant_method"), 40) : null;
        return (architecture, quantization);
    }

    public async Task<HuggingFaceRepositoryResult> GetRepositoryAsync(string? repository, string? revision = null,
        ModelKind kind = ModelKind.Chat, CancellationToken cancellationToken = default, ModelFormat format = ModelFormat.Gguf)
    {
        ValidateKind(kind, format);
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
        if (format == ModelFormat.Safetensors) return Snapshot(repository!, revision, sha.ToLowerInvariant(), root, siblings);

        var files = new Dictionary<string, long>(StringComparer.Ordinal);
        var warnings = new List<string>
        {
            "Filename labels don’t prove a model will run. Lucia inspects the full file after it downloads."
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
            var quant = QuantizationOf(file);
            choices.Add(new(file, parts, total, quant,
                quant is not null ? "filename_inferred" : "unknown", Compatibility,
                new ModelDownloadRequest("huggingface", repository!, file, kind, sha.ToLowerInvariant(), Pro: false, SizeBytes: total)));
        }
        if (excluded > 0) warnings.Add("Projector, adapter, LoRA, or unsafe paths were excluded from standalone choices.");
        if (unsized > 0) warnings.Add("Some GGUF files had no usable byte size and were excluded.");
        if (incomplete) warnings.Add("Incomplete, invalid, or oversized split GGUF groups were excluded. Select only a complete first-shard group (up to 128 shards).");
        if (choices.Count == 0) warnings.Add("No standalone GGUF with a known size and a complete shard group is available at this revision.");
        if (IsGated(root)) warnings.Add("This repository is gated. Downloads may require a read token, accepted terms, and approval on Hugging Face.");
        return new(repository!, revision, sha.ToLowerInvariant(), kind, IsGated(root), IsTrue(root, "private"),
            choices.Count == 0 ? "no_standalone_gguf" : "available", choices, Metadata(root), warnings);
    }

    /// <summary>A repository as vLLM uses it: one choice covering the files <see cref="ModelCatalog.SnapshotFiles"/> selects.</summary>
    private static HuggingFaceRepositoryResult Snapshot(string repository, string revision, string sha, JsonElement root, JsonElement siblings)
    {
        var files = new List<HuggingFaceFile>();
        foreach (var sibling in siblings.EnumerateArray())
        {
            var file = HuggingFaceManagementHttp.String(sibling, "rfilename");
            if (file is null || !ValidFile(file) || !ModelCatalog.SnapshotFiles.Any(pattern => file.EndsWith(pattern[1..], StringComparison.Ordinal))) continue;
            var size = NonnegativeNumber(sibling, "size");
            if (size is null && sibling.TryGetProperty("lfs", out var lfs)) size = NonnegativeNumber(lfs, "size");
            files.Add(new(file, size ?? 0));
        }
        var (architecture, quantization) = Config(root);
        var weights = files.Where(file => file.Path.EndsWith(".safetensors", StringComparison.Ordinal)).ToArray();
        var total = files.Sum(file => file.SizeBytes);
        var gated = IsGated(root);
        var warnings = new List<string>();
        var availability = weights.Length == 0 || files.All(file => file.Path != "config.json") ? "no_safetensors"
            : IsMlx(root) ? "mlx"
            : architecture is null ? "unknown_architecture"
            : !VllmArchitectures.Supported.Contains(architecture) ? "unsupported_architecture"
            : gated ? "gated" : "available";
        warnings.Add(availability switch
        {
            "no_safetensors" => "This repository has no safetensors weights with a config.json, so vLLM can't load it. GGUF repositories run on Lucia Inference.",
            "mlx" => "This is an MLX conversion for Apple silicon, which vLLM can't load. Look for the original repository instead.",
            "unknown_architecture" => "Its config doesn't name a model architecture, so Lucia can't tell whether vLLM supports it.",
            "unsupported_architecture" => $"vLLM {VllmVersion} doesn't support the {architecture} architecture.",
            "gated" => "This repository is gated. Servers download without a Hugging Face account, so only public models are available.",
            _ => "vLLM loads the whole repository into GPU memory. The weights need to fit, with room left for context.",
        });
        var choices = availability == "available" && total > 0
            ? new[] { new HuggingFaceModelChoice("config.json", files, total, quantization?.ToUpperInvariant(), quantization is null ? "unknown" : "config",
                Compatibility, new ModelDownloadRequest("huggingface", repository, "config.json", ModelKind.Chat, sha, Pro: false, SizeBytes: total,
                    Format: ModelFormat.Safetensors)) }
            : [];
        return new(repository, revision, sha, ModelKind.Chat, gated, IsTrue(root, "private"), availability, choices,
            architecture is null ? null : new("huggingface_api", "repository", architecture, null), warnings);
    }

    public const string VllmVersion = "0.30.0";

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

    private static void ValidateKind(ModelKind kind, ModelFormat format = ModelFormat.Gguf)
    {
        if (!Enum.IsDefined(kind))
            throw new HuggingFaceManagementException(400, "invalid_kind", "Kind must be Chat or Embedding.");
        if (!Enum.IsDefined(format) || (format == ModelFormat.Safetensors && kind != ModelKind.Chat))
            throw new HuggingFaceManagementException(400, "invalid_format", "Safetensors repositories are served by vLLM, which Lucia uses for LLMs only.");
    }
}
