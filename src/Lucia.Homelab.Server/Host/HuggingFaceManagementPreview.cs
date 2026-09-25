using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OpenTelemetry;

namespace Lucia.Homelab.Server.Host;

public sealed record HuggingFacePreviewSource(
    string Provider, string Repository, string Revision, string File, IReadOnlyList<HuggingFaceFile> Files,
    long TotalSizeBytes, long BytesRead = 0, string? Signature = null, uint? GgufVersion = null,
    bool MetadataComplete = false, string? MetadataSha256 = null);

public sealed record HuggingFaceContextPreview(
    ModelInspection? Inspection, string Reason, HuggingFacePreviewSource Source, string Qualification);

public sealed class HuggingFacePreviewService(
    HuggingFaceBrowserService browser, HuggingFaceCredentialService credentials, HttpClient http)
{
    public const int MaximumHeaderBytes = 16 * 1024 * 1024;
    public const int MaximumRedirects = 4;
    private const string Qualification =
        "Selected-file header estimate only, not TensorSharp compatibility, download admission, or allocation proof. "
        + "Weights use repository-reported total shard sizes; tensor data, tokenizer correctness, and other shard headers are not inspected. "
        + "Apply the existing host memory planner, all reserves, and current opposite-slot residency before comparing context capacity.";

    /// <summary>Validates a pinned repository selection, then reads only its first GGUF shard's bounded metadata.</summary>
    public async Task<HuggingFaceContextPreview> PreviewAsync(string repository, string revision, string file, ModelKind kind,
        CancellationToken cancellationToken = default)
    {
        if (revision is null || !Regex.IsMatch(revision, @"\A[0-9a-fA-F]{40}\z", RegexOptions.CultureInvariant))
            throw new HuggingFaceManagementException(400, "pinned_revision_required", "Context preview requires the concrete commit SHA returned by the repository browser.");
        var details = await browser.GetRepositoryAsync(repository, revision, kind, cancellationToken);
        var choice = details.Choices.FirstOrDefault(candidate => candidate.File == file)
            ?? throw new HuggingFaceManagementException(400, "invalid_preview_file", "Choose a standalone GGUF or a complete group's first shard from the repository browser.");
        var source = new HuggingFacePreviewSource("huggingface", details.Repository, details.Revision,
            choice.File, choice.Files, choice.TotalSizeBytes);
        if (kind == ModelKind.Embedding)
            return new(null, "embedding_requires_local_inspection", source, Qualification);
        var access = await credentials.GetDownloadCredentialsAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        HeaderReader? reader = null;
        try
        {
            // Signed CDN locations must not be captured by the host's HTTP OpenTelemetry instrumentation.
            using var suppression = SuppressInstrumentationScope.Begin();
            using var response = await OpenHeaderAsync(source, access.Token, deadline.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bodyLimit = Math.Min(MaximumHeaderBytes, source.Files[0].SizeBytes);
            if (response.Content.Headers.ContentRange?.To is { } lastByte) bodyLimit = Math.Min(bodyLimit, lastByte + 1);
            using (reader = new HeaderReader(stream, (int)bodyLimit, deadline.Token))
            {
                var metadata = await reader.ParseAsync(choice.Files.Count);
                source = reader.Provenance(source);
                var inspection = ModelInspector.EstimateChatMetadata(metadata, choice.TotalSizeBytes);
                return new(inspection, "selected_header_estimate", source, Qualification);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Unavailable("provider_timeout"); }
        catch (PreviewUnavailable exception) { return Unavailable(exception.Reason); }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        { return Unavailable("provider_unavailable"); }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException or NotSupportedException or DecoderFallbackException)
        { return Unavailable("invalid_attention_metadata"); }
        catch (FormatException) { return Unavailable("invalid_provider_response"); }

        HuggingFaceContextPreview Unavailable(string reason) =>
            new(null, reason, reader?.Provenance(source) ?? source, Qualification);
    }

    internal static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null,
        AutomaticDecompression = DecompressionMethods.None, MaxResponseDrainSize = 0,
        ActivityHeadersPropagator = null, ConnectTimeout = TimeSpan.FromSeconds(5), MaxResponseHeadersLength = 32
    }) { Timeout = TimeSpan.FromSeconds(20) };

    private async Task<HttpResponseMessage> OpenHeaderAsync(HuggingFacePreviewSource source, string? token, CancellationToken ct)
    {
        var path = string.Join("/", source.File.Split('/').Select(Uri.EscapeDataString));
        var uri = new Uri($"https://huggingface.co/{source.Repository}/resolve/{source.Revision}/{path}");
        var firstSize = source.Files[0].SizeBytes;
        var lastByte = Math.Min(MaximumHeaderBytes, firstSize) - 1;
        for (var hop = 0; ; hop++)
        {
            if (!Allowed(uri) || (token is not null && Uri.UnescapeDataString(uri.AbsoluteUri).Contains(token, StringComparison.Ordinal)))
                throw new PreviewUnavailable("unsafe_provider_redirect");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(0, lastByte);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (uri.Host == "huggingface.co" && token is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            try
            {
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (hop == MaximumRedirects) throw new PreviewUnavailable("redirect_limit_exceeded");
                    if (response.Headers.Location is not { } location || !Uri.TryCreate(uri, location, out var next))
                        throw new PreviewUnavailable("unsafe_provider_redirect");
                    uri = next;
                    response.Dispose();
                    continue;
                }
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new PreviewUnavailable("provider_access_denied");
                if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new PreviewUnavailable("provider_rate_limited");
                if (response.StatusCode == HttpStatusCode.NotFound) throw new PreviewUnavailable("provider_not_found");
                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                    throw new PreviewUnavailable("provider_unavailable");
                if (response.Content.Headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    throw new PreviewUnavailable("encoded_header_response");
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (range is null || range.Unit != "bytes" || range.From != 0 || range.To is null
                        || range.To > lastByte || range.Length != firstSize
                        || (response.Content.Headers.ContentLength is { } length && length != range.To + 1))
                        throw new PreviewUnavailable("invalid_content_range");
                }
                else if (response.Content.Headers.ContentLength is { } length && length != firstSize)
                    throw new PreviewUnavailable("file_size_changed");
                return response;
            }
            catch { response.Dispose(); throw; }
        }
    }

    private static bool Allowed(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.AbsoluteUri.Length <= 16384
        && (uri.Host is "huggingface.co" or "hf.co" or "cdn-lfs.huggingface.co"
            || uri.Host.EndsWith(".hf.co", StringComparison.Ordinal));

    private sealed class PreviewUnavailable(string reason) : Exception(reason)
    {
        public string Reason { get; } = reason;
    }

    // GGUF v2/v3 metadata only. No GgufFile, tensor table, weights, or retained tokenizer/template values.
    private sealed class HeaderReader(Stream stream, int byteLimit, CancellationToken ct) : IDisposable
    {
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private static readonly HashSet<string> Architectures = new(StringComparer.Ordinal)
        { "llama", "qwen2", "qwen2moe", "qwen3", "qwen3moe", "qwen35", "qwen35moe", "qwen3next" };
        private static readonly HashSet<string> Numbers = new(StringComparer.Ordinal)
        {
            "context_length", "block_count", "embedding_length", "attention.head_count", "attention.head_count_kv",
            "attention.key_length", "attention.value_length", "nextn_predict_layers", "full_attention_interval"
        };
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] _scalar = new byte[8];
        private readonly byte[] _skip = new byte[8192];
        private long _read;
        private string? _signature;
        private uint? _version;
        private bool _complete;
        private string? _digest;

        public HuggingFacePreviewSource Provenance(HuggingFacePreviewSource source) =>
            source with { BytesRead = _read, Signature = _signature, GgufVersion = _version, MetadataComplete = _complete, MetadataSha256 = _digest };

        public async Task<ChatKvMetadata> ParseAsync(int shardCount)
        {
            if (await UInt32Async() != 0x46554747) throw new PreviewUnavailable("invalid_gguf_signature");
            _signature = "GGUF";
            var version = await UInt32Async();
            if (version is not (2 or 3)) throw new PreviewUnavailable("unsupported_gguf_version");
            _version = version;
            var tensors = await UInt64Async();
            var count = await UInt64Async();
            if (tensors is 0 or > 1_000_000 || count is 0 or > 4096) throw new PreviewUnavailable("invalid_metadata_counts");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var numbers = new Dictionary<string, uint>(StringComparer.Ordinal);
            var layerTypes = new Dictionary<string, string[]>(StringComparer.Ordinal);
            string? architecture = null;
            for (ulong i = 0; i < count; i++)
            {
                var key = await TextAsync(512);
                if (key.Length == 0 || key.Any(char.IsControl) || !keys.Add(key)) throw new PreviewUnavailable("invalid_metadata_key");
                var type = await UInt32Async();
                var separator = key.IndexOf('.');
                var prefix = separator < 0 ? "" : key[..separator];
                var suffix = separator < 0 ? "" : key[(separator + 1)..];
                if (key == "general.architecture")
                {
                    if (type != 8) throw new PreviewUnavailable("invalid_attention_metadata");
                    architecture = await TextAsync(64);
                }
                else if (key is "split.no" or "split.count" || (Architectures.Contains(prefix) && Numbers.Contains(suffix)))
                    numbers.Add(key, await UnsignedAsync(type));
                else if (Architectures.Contains(prefix) && suffix == "layer_types")
                {
                    if (type != 9 || await UInt32Async() != 8) throw new PreviewUnavailable("invalid_attention_metadata");
                    var length = await UInt64Async();
                    if (length is 0 or > 4096) throw new PreviewUnavailable("invalid_attention_metadata");
                    var values = new string[(int)length];
                    for (var j = 0; j < values.Length; j++)
                    {
                        values[j] = await TextAsync(64);
                        if (!values[j].Equals("linear_attention", StringComparison.OrdinalIgnoreCase)
                            && !values[j].Equals("full_attention", StringComparison.OrdinalIgnoreCase))
                            throw new PreviewUnavailable("unsupported_cache_layout");
                    }
                    layerTypes.Add(prefix, values);
                }
                else await SkipValueAsync(type);
            }
            _complete = true;
            _digest = Convert.ToHexStringLower(_hash.GetHashAndReset());
            if (architecture is null) throw new PreviewUnavailable("missing_attention_metadata");
            if (!Architectures.Contains(architecture)) throw new PreviewUnavailable("unsupported_cache_layout");
            if (shardCount > 1 && (!numbers.TryGetValue("split.no", out var index) || index != 0
                || !numbers.TryGetValue("split.count", out var splitCount) || splitCount != shardCount))
                throw new PreviewUnavailable("shard_metadata_mismatch");
            if (numbers.GetValueOrDefault("split.no") != 0 || numbers.GetValueOrDefault("split.count", (uint)shardCount) != shardCount)
                throw new PreviewUnavailable("shard_metadata_mismatch");
            var context = Required("context_length");
            var layers = Required("block_count");
            var heads = Required("attention.head_count");
            var hidden = Required("embedding_length");
            var kvHeads = Optional("attention.head_count_kv", heads);
            if (context > int.MaxValue || layers > 4096 || heads > 65536 || kvHeads == 0
                || kvHeads > heads || heads % kvHeads != 0)
                throw new PreviewUnavailable("invalid_attention_metadata");
            if (!numbers.ContainsKey(architecture + ".attention.key_length") && hidden % heads != 0)
                throw new PreviewUnavailable("invalid_attention_metadata");
            var keyLength = Optional("attention.key_length", hidden / heads);
            var valueLength = Optional("attention.value_length", keyLength);
            if (keyLength is 0 or > 65536 || valueLength is 0 or > 65536)
                throw new PreviewUnavailable("invalid_attention_metadata");
            var nextn = Optional("nextn_predict_layers", 0);
            var interval = Optional("full_attention_interval", 4);
            var types = layerTypes.GetValueOrDefault(architecture);
            if (architecture is "qwen35" or "qwen35moe" or "qwen3next")
            {
                if (nextn >= layers || interval is 0 or > int.MaxValue || (types is not null && types.Length != layers - nextn))
                    throw new PreviewUnavailable("invalid_attention_metadata");
            }
            return new(architecture, (int)context, (int)layers, kvHeads, keyLength, valueLength, nextn, interval, types);

            uint Required(string key) => numbers.TryGetValue(architecture + "." + key, out var value) && value > 0
                ? value : throw new PreviewUnavailable("missing_attention_metadata");
            uint Optional(string key, uint fallback) => numbers.GetValueOrDefault(architecture + "." + key, fallback);
        }

        private async Task<uint> UnsignedAsync(uint type)
        {
            if (type == 0) { await ReadAsync(_scalar.AsMemory(0, 1)); return _scalar[0]; }
            if (type == 2) { await ReadAsync(_scalar.AsMemory(0, 2)); return BinaryPrimitives.ReadUInt16LittleEndian(_scalar); }
            if (type == 4) return await UInt32Async();
            if (type == 10)
            {
                var value = await UInt64Async();
                if (value <= uint.MaxValue) return (uint)value;
            }
            throw new PreviewUnavailable("unsupported_attention_metadata_type");
        }

        private async Task SkipValueAsync(uint type)
        {
            if (type == 8) { await SkipAsync(await UInt64Async()); return; }
            if (type == 9)
            {
                var element = await UInt32Async();
                var length = await UInt64Async();
                if (element > 12 || length > 1_000_000) throw new PreviewUnavailable("invalid_metadata_array");
                if (element == 9) throw new PreviewUnavailable("unsupported_metadata_array");
                if (element == 8)
                {
                    for (ulong i = 0; i < length; i++) await SkipAsync(await UInt64Async());
                }
                else await SkipAsync(checked(length * (ulong)Width(element)));
                return;
            }
            await SkipAsync((ulong)Width(type));
        }

        private static int Width(uint type) => type switch
        {
            0 or 1 or 7 => 1,
            2 or 3 => 2,
            4 or 5 or 6 => 4,
            10 or 11 or 12 => 8,
            _ => throw new PreviewUnavailable("invalid_metadata_type")
        };

        private async Task SkipAsync(ulong length)
        {
            if (length > (ulong)(byteLimit - _read)) throw new PreviewUnavailable("header_limit_exceeded");
            while (length > 0)
            {
                var chunk = (int)Math.Min(length, (ulong)_skip.Length);
                await ReadAsync(_skip.AsMemory(0, chunk));
                length -= (ulong)chunk;
            }
        }

        private async Task<string> TextAsync(int maximum)
        {
            var length = await UInt64Async();
            if (length > (ulong)maximum) throw new PreviewUnavailable("metadata_string_too_large");
            var bytes = new byte[(int)length];
            await ReadAsync(bytes);
            return Utf8.GetString(bytes);
        }

        private async Task<uint> UInt32Async()
        {
            await ReadAsync(_scalar.AsMemory(0, 4));
            return BinaryPrimitives.ReadUInt32LittleEndian(_scalar);
        }

        private async Task<ulong> UInt64Async()
        {
            await ReadAsync(_scalar);
            return BinaryPrimitives.ReadUInt64LittleEndian(_scalar);
        }

        private async Task ReadAsync(Memory<byte> buffer)
        {
            if (buffer.Length > byteLimit - _read) throw new PreviewUnavailable("header_limit_exceeded");
            while (buffer.Length > 0)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0) throw new PreviewUnavailable("truncated_header");
                _hash.AppendData(buffer.Span[..read]);
                _read += read;
                buffer = buffer[read..];
            }
        }

        public void Dispose() => _hash.Dispose();
    }
}
