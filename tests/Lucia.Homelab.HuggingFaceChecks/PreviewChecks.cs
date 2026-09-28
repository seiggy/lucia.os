using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lucia.Homelab.Server.Host;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

internal static class PreviewChecks
{
    private const string Sha = "1234567890abcdef1234567890abcdef12345678";
    private const string Token = "hf_SyntheticPreviewSecretNotLive";
    private const string SignedQuery = "X-Amz-Signature=syntheticSignedLocation";

    public static async Task<int> RunAsync(string storage)
    {
        var count = 0;
        void Check(bool value, string reason)
        {
            if (!value) throw new InvalidOperationException(reason);
            count++;
        }
        void Safe(object result)
        {
            var text = JsonSerializer.Serialize(result);
            Check(!text.Contains(Token) && !text.Contains(SignedQuery) && !text.Contains("do-not-return-this-template"),
                "Preview returned a credential, signed location, or instruction string.");
        }
        using var handler = new PreviewHandler();
        using var http = new HttpClient(handler);
        var credentialOptions = Options.Create(new HuggingFaceManagementOptions { CredentialsDirectory = Path.Combine(storage, "preview-credentials") });
        var credentials = new HuggingFaceCredentialService(credentialOptions,
            Options.Create(new HostPlatformOptions { HuggingFaceToken = Token }),
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(storage, "preview-keys"))), http);
        var preview = new HuggingFacePreviewService(new HuggingFaceBrowserService(credentials, http), credentials, http);
        var metadata = Metadata();
        long size = ModelInspector.GiB;
        string[] files = ["model-Q4_K_M.gguf"];
        TrackingStream? lastStream = null;
        Func<HttpRequestMessage, HttpResponseMessage> response = _ => Header(metadata);
        handler.Respond = request => request.RequestUri!.AbsolutePath.StartsWith("/api/models/", StringComparison.Ordinal)
            ? new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    id = "owner/model", sha = Sha,
                    gguf = new { architecture = "incorrect-advisory-architecture", context_length = 1 },
                    siblings = files.Select(file => new { rfilename = file, size })
                }), Encoding.UTF8, "application/json")
            }
            : response(request);

        HttpResponseMessage Header(byte[] prefix, bool ignoreRange = false, bool fillAfterPrefix = false, bool chunked = false)
        {
            lastStream = new TrackingStream(prefix, fillAfterPrefix);
            var result = new HttpResponseMessage(ignoreRange ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            { Content = new StreamContent(lastStream) };
            var length = Math.Min(size, HuggingFacePreviewService.MaximumHeaderBytes);
            if (!ignoreRange) result.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, length - 1, size);
            if (!chunked) result.Content.Headers.ContentLength = ignoreRange ? size : length;
            return result;
        }

        Task<HuggingFaceContextPreview> Read(string? file = null, ModelKind kind = ModelKind.Chat) =>
            preview.PreviewAsync("owner/model", Sha, file ?? files[0], kind);
        async Task Unavailable(string reason)
        {
            var result = await Read();
            Check(result.Inspection is null && result.Reason == reason, $"Expected unavailable {reason}, got {result.Reason}.");
            Check(result.Source.BytesRead <= HuggingFacePreviewService.MaximumHeaderBytes, "Preview exceeded its byte cap.");
            Safe(result);
        }

        var result = await Read();
        Check(result.Inspection is { Architecture: "llama", Kind: ModelKind.Chat, NativeContextTokens: 262144,
            AttentionLayers: 32, KvBytesPerToken: 131072, TensorTypes.Length: 0 }, "Selected header KV calculation is wrong.");
        Check(result.Inspection!.FileBytes == size && result.Inspection.WeightBytes == size, "Wrong selected file byte estimate.");
        Check(result.Source is { Signature: "GGUF", GgufVersion: 3, MetadataComplete: true, Revision: Sha }
            && result.Source.MetadataSha256 == Convert.ToHexStringLower(SHA256.HashData(metadata)), "Header provenance hash or pinned revision was lost.");
        Check(lastStream!.BytesRead == metadata.Length && lastStream.Disposed, "Preview read past metadata or failed to close the body.");
        Safe(result);
        var reserveSettings = new HostPlatformOptions();
        var small = ModelInspector.Calculate(result.Inspection, reserveSettings, 32 * ModelInspector.GiB);
        Check(small.OsReserveBytes == 4 * ModelInspector.GiB && small.ServicesReserveBytes == 3 * ModelInspector.GiB
            && small.VoiceReserveBytes == 8 * ModelInspector.GiB && small.RuntimeReserveBytes == 3 * ModelInspector.GiB,
            "Preview bypassed existing host reserves.");
        var occupied = ModelInspector.Calculate(result.Inspection, reserveSettings, 32 * ModelInspector.GiB, 2 * ModelInspector.GiB);
        Check(occupied.MemoryLimitedContextTokens < small.MemoryLimitedContextTokens, "Opposite-slot memory was ignored.");
        size = 8 * ModelInspector.GiB;
        var bigger = await Read();
        Check(ModelInspector.Calculate(bigger.Inspection!, reserveSettings, 32 * ModelInspector.GiB).MemoryLimitedContextTokens
            < small.MemoryLimitedContextTokens, "Larger quantization did not reduce context capacity.");
        size = ModelInspector.GiB;

        response = _ => Header(metadata, ignoreRange: true, fillAfterPrefix: true);
        result = await Read();
        Check(result.Inspection is not null && lastStream!.BytesRead == metadata.Length, "Range-ignoring response read weight bytes.");
        response = _ => Header(metadata, ignoreRange: true, fillAfterPrefix: true, chunked: true);
        result = await Read();
        Check(result.Inspection is not null && lastStream!.BytesRead == metadata.Length, "Chunked Range-ignoring response read weights.");
        var previousRequests = handler.Requests.Count;
        result = await Read(kind: ModelKind.Embedding);
        Check(result is { Inspection: null, Reason: "embedding_requires_local_inspection", Source.BytesRead: 0 }
            && handler.Requests.Count == previousRequests + 1, "Embedding preview attempted header/weight inspection.");

        foreach (var bad in new[] { "main", "", "../main" })
        {
            previousRequests = handler.Requests.Count;
            try { await preview.PreviewAsync("owner/model", bad, files[0], ModelKind.Chat); throw new Exception("Unpinned preview was accepted."); }
            catch (HuggingFaceManagementException exception)
            {
                Check(exception.Code == "pinned_revision_required" && exception.StatusCode == 400
                    && handler.Requests.Count == previousRequests, "Invalid pin caused a request.");
            }
        }
        foreach (var bad in new[] { "../escape.gguf", "missing.gguf", "https://elsewhere.invalid/model.gguf" })
        {
            previousRequests = handler.Requests.Count;
            try { await Read(bad); throw new Exception("Unlisted selection was accepted."); }
            catch (HuggingFaceManagementException exception)
            {
                Check(exception.Code == "invalid_preview_file" && handler.Requests.Count == previousRequests + 1,
                    "Unlisted file caused a resolve request.");
            }
        }

        response = _ => Header(metadata[..^1]);
        await Unavailable("truncated_header");
        foreach (var (offset, value, reason) in new (int, ulong, string)[]
        {
            (0, 0, "invalid_gguf_signature"), (4, 1, "unsupported_gguf_version"),
            (8, 0, "invalid_metadata_counts"), (8, 1_000_001, "invalid_metadata_counts"),
            (16, 4097, "invalid_metadata_counts")
        })
        {
            var bad = (byte[])metadata.Clone();
            if (offset < 8) BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(offset), (uint)value);
            else BinaryPrimitives.WriteUInt64LittleEndian(bad.AsSpan(offset), value);
            response = _ => Header(bad);
            await Unavailable(reason);
        }
        response = _ => Header(Metadata("deepseek2"));
        await Unavailable("unsupported_cache_layout");
        response = _ => Header(Metadata("llama", extra: writer => Entry(writer, "general.architecture", "llama")));
        await Unavailable("invalid_metadata_key");
        response = _ => Header(Metadata("llama", extra: writer => { Text(writer, "bad.type"); writer.Write(99u); }));
        await Unavailable("invalid_metadata_type");
        response = _ => Header(Metadata("llama", extra: writer => { Text(writer, "bad.array"); writer.Write(9u); writer.Write(9u); writer.Write(1ul); }));
        await Unavailable("unsupported_metadata_array");
        response = _ => Header(Metadata("llama", extra: writer => { Text(writer, "bad.array"); writer.Write(9u); writer.Write(8u); writer.Write(1_000_001ul); }));
        await Unavailable("invalid_metadata_array");
        response = _ => Header(Metadata("llama", extra: writer => { Text(writer, "bad.string"); writer.Write(8u); writer.Write(ulong.MaxValue); }));
        await Unavailable("header_limit_exceeded");
        response = _ => Header(Metadata(new string('x', 65)));
        await Unavailable("metadata_string_too_large");
        response = _ => Header(Metadata("llama", extra: writer => Text(writer, new string('k', 513))));
        await Unavailable("metadata_string_too_large");
        response = _ => Header(Metadata("llama", extra: writer =>
        {
            Text(writer, "llama.attention.key_length"); writer.Write(9u); writer.Write(4u); writer.Write(2ul); writer.Write(128u); writer.Write(128u);
        }));
        await Unavailable("unsupported_attention_metadata_type");
        response = _ => Header(Metadata("llama", extra: writer => Number(writer, "llama.attention.key_length", 0)));
        await Unavailable("invalid_attention_metadata");
        var missing = Metadata().ToArray();
        var missingKey = Encoding.UTF8.GetBytes("llama.context_length");
        var missingOffset = missing.AsSpan().IndexOf(missingKey);
        Encoding.UTF8.GetBytes("llama.ignored_length").CopyTo(missing, missingOffset);
        response = _ => Header(missing);
        await Unavailable("missing_attention_metadata");
        response = _ => Header(Metadata("llama", extra: writer =>
        {
            Text(writer, "llama.attention.key_length"); writer.Write(10u); writer.Write(ulong.MaxValue);
        }));
        await Unavailable("unsupported_attention_metadata_type");
        var invalidUtf8 = (byte[])metadata.Clone();
        invalidUtf8[32] = 0xff;
        response = _ => Header(invalidUtf8);
        await Unavailable("invalid_attention_metadata");

        // Fill precisely the remaining bounded header with an irrelevant string, then require one more key.
        using (var buffer = new MemoryStream())
        {
            using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
            writer.Write(0x46554747u); writer.Write(3u); writer.Write(1ul); writer.Write(2ul);
            Text(writer, "ignored.padding"); writer.Write(8u);
            writer.Write((ulong)(HuggingFacePreviewService.MaximumHeaderBytes - buffer.Position - 8));
            var prefix = buffer.ToArray();
            response = _ => Header(prefix, ignoreRange: true, fillAfterPrefix: true);
            await Unavailable("header_limit_exceeded");
            Check(lastStream!.BytesRead == HuggingFacePreviewService.MaximumHeaderBytes && lastStream.Disposed,
                "Range-ignoring body did not stop exactly at its cap.");
        }

        var v2 = (byte[])metadata.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(v2.AsSpan(4), 2);
        response = _ => Header(v2);
        Check((await Read()).Source.GgufVersion == 2, "GGUF v2 metadata was not supported.");
        response = _ => Header(Metadata("llama", extra: writer =>
        {
            Text(writer, "llama.attention.key_length"); writer.Write(10u); writer.Write(64ul);
        }));
        Check((await Read()).Inspection!.KvBytesPerToken == 65536, "Unsigned-64 attention metadata or value-length fallback is wrong.");
        response = _ => Header(Metadata("qwen35moe"));
        var hybrid = await Read();
        Check(hybrid.Inspection is { AttentionLayers: 1, KvBytesPerToken: 64 }, "Hybrid interval/nextn calculation changed.");
        response = _ => Header(Metadata("qwen35moe", extra: writer =>
            Strings(writer, "qwen35moe.layer_types", ["full_attention", "linear_attention", "full_attention", "linear_attention"])));
        Check((await Read()).Inspection is { AttentionLayers: 2, KvBytesPerToken: 128 }, "Explicit hybrid layers were ignored.");
        response = _ => Header(Metadata("qwen35moe", extra: writer => Strings(writer, "qwen35moe.layer_types", ["full_attention"])));
        await Unavailable("invalid_attention_metadata");
        response = _ => Header(Metadata("qwen35moe", extra: writer => Strings(writer, "qwen35moe.layer_types", ["new_attention"])));
        await Unavailable("unsupported_cache_layout");

        files = ["model-Q4_K_M-00001-of-00002.gguf", "model-Q4_K_M-00002-of-00002.gguf"];
        response = _ => Header(Metadata(split: true));
        result = await Read();
        Check(result.Inspection?.WeightBytes == 2 * size && result.Source.Files.Count == 2, "Split weights lost total-group size.");
        response = _ => Header(metadata);
        await Unavailable("shard_metadata_mismatch");
        try { await Read(files[1]); throw new Exception("Second shard was selectable."); }
        catch (HuggingFaceManagementException exception) { Check(exception.Code == "invalid_preview_file", "Second shard was resolved."); }
        files = ["model-Q4_K_M.gguf"];

        var redirects = 0;
        response = request =>
        {
            if (redirects++ < HuggingFacePreviewService.MaximumRedirects)
                return Redirect($"https://cas-bridge.xethub.hf.co/header/{redirects}?{SignedQuery}");
            return Header(metadata);
        };
        previousRequests = handler.Requests.Count;
        result = await Read();
        Check(result.Inspection is not null && redirects == 5, "Valid bounded CDN redirects failed.");
        var downloadRequests = handler.Requests.Skip(previousRequests + 1).ToArray();
        Check(downloadRequests[0].Authorization == "Bearer " + Token
            && downloadRequests.Skip(1).All(request => request.Authorization is null), "HF Authorization leaked to a CDN.");
        Check(downloadRequests.All(request => request.Range == $"bytes=0-{HuggingFacePreviewService.MaximumHeaderBytes - 1}" && request.AcceptEncoding == "identity"),
            "Range or encoding changed across redirects.");
        Safe(result);
        foreach (var destination in new[]
        {
            "http://cdn-lfs.hf.co/file", "https://cdn.hf.co.evil.invalid/file", "https://hf.co:444/file",
            "https://user:pass@huggingface.co/file", "https://127.0.0.1/file", "https://hf.co/file#fragment",
            "https://evil.invalid/?token=" + Token, "https://cdn-lfs.hf.co/?token=" + Token,
            "https://cdn-lfs.hf.co/?token=hf_%53yntheticPreviewSecretNotLive"
        })
        {
            response = _ => Redirect(destination);
            previousRequests = handler.Requests.Count;
            await Unavailable("unsafe_provider_redirect");
            Check(handler.Requests.Count == previousRequests + 2, "An unsafe redirect was contacted.");
        }
        response = _ => Redirect("https://cdn-lfs.hf.co/loop?" + SignedQuery);
        await Unavailable("redirect_limit_exceeded");
        response = request => request.RequestUri!.Host == "huggingface.co"
            ? Redirect("https://cdn-lfs.huggingface.co/file?" + SignedQuery) : Header(metadata);
        Check((await Read()).Inspection is not null && handler.Requests.Last().Authorization is null, "Legacy CDN received an authorization header.");
        foreach (var (status, reason) in new[]
        {
            (HttpStatusCode.Forbidden, "provider_access_denied"), (HttpStatusCode.Unauthorized, "provider_access_denied"),
            (HttpStatusCode.TooManyRequests, "provider_rate_limited"), (HttpStatusCode.NotFound, "provider_not_found")
        })
        {
            response = _ => new(status) { Content = new StringContent(Token + SignedQuery) };
            await Unavailable(reason);
        }
        response = _ => throw new HttpRequestException(Token + SignedQuery);
        await Unavailable("provider_unavailable");
        response = _ => throw new OperationCanceledException(SignedQuery);
        await Unavailable("provider_timeout");
        response = _ =>
        {
            var value = Header(metadata);
            value.Content.Headers.ContentRange = new ContentRangeHeaderValue(1, 100, size);
            return value;
        };
        await Unavailable("invalid_content_range");
        response = _ =>
        {
            var value = Header(metadata);
            value.Content.Headers.ContentEncoding.Add("gzip");
            return value;
        };
        await Unavailable("encoded_header_response");
        response = _ =>
        {
            var value = Header(metadata, ignoreRange: true);
            value.Content.Headers.ContentLength = size + 1;
            return value;
        };
        await Unavailable("file_size_changed");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { await preview.PreviewAsync("owner/model", Sha, files[0], ModelKind.Chat, canceled.Token); throw new Exception("Cancellation ignored."); }
            catch (OperationCanceledException) { count++; }
        }

        // A real tiny local GGUF exercises the refactor through the unchanged admission inspector.
        var localPath = Path.Combine(storage, "local-kv-regression.gguf");
        var localHeader = Metadata("qwen35moe");
        using (var writer = new BinaryWriter(File.Create(localPath)))
        {
            writer.Write(localHeader);
            Text(writer, "token_embd.weight"); writer.Write(2u); writer.Write(64ul); writer.Write(2ul); writer.Write(0u); writer.Write(0ul);
            while (writer.BaseStream.Position % 32 != 0) writer.Write((byte)0);
            writer.Write(new byte[64 * 2 * sizeof(float)]);
        }
        var local = ModelInspector.Inspect(localPath, ModelKind.Chat);
        size = new FileInfo(localPath).Length;
        response = _ => Header(File.ReadAllBytes(localPath));
        result = await Read();
        Check(result.Inspection is not null && result.Inspection.KvBytesPerToken == local.KvBytesPerToken
            && result.Inspection.AttentionLayers == local.AttentionLayers && result.Inspection.NativeContextTokens == local.NativeContextTokens
            && result.Inspection.FileBytes == local.FileBytes && local.TensorTypes.SequenceEqual(["F32"]),
            "Local inspector and preview no longer use the same exact KV calculation.");
        Check(lastStream!.BytesRead == localHeader.Length, "Preview read the local fixture's tensor table or weights.");
        return count;
    }

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found)
    { Headers = { Location = new Uri(location) } };

    private static byte[] Metadata(string architecture = "llama", Action<BinaryWriter>? extra = null, bool split = false)
    {
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data, Encoding.UTF8, leaveOpen: true);
        var hybrid = architecture == "qwen35moe";
        writer.Write(0x46554747u); writer.Write(3u); writer.Write(1ul);
        writer.Write((ulong)(11 + (hybrid ? 2 : 0) + (split ? 2 : 0) + (extra is null ? 0 : 1)));
        Entry(writer, "general.architecture", architecture);
        Number(writer, architecture + ".context_length", 262144);
        Number(writer, architecture + ".block_count", hybrid ? 5u : 32u);
        Number(writer, architecture + ".embedding_length", hybrid ? 64u : 4096u);
        Number(writer, architecture + ".attention.head_count", hybrid ? 8u : 32u);
        Number(writer, architecture + ".attention.head_count_kv", hybrid ? 2u : 8u);
        Entry(writer, "tokenizer.ggml.model", "gpt2");
        Strings(writer, "tokenizer.ggml.tokens", ["a", "b", "do-not-return-this-template"]);
        Entry(writer, "tokenizer.chat_template", "do-not-return-this-template");
        Text(writer, "ignored.float_array"); writer.Write(9u); writer.Write(6u); writer.Write(2ul); writer.Write(0.25f); writer.Write(0.5f);
        Entry(writer, "general.name", "Ignored descriptive model name");
        if (hybrid)
        {
            Number(writer, architecture + ".nextn_predict_layers", 1);
            Number(writer, architecture + ".full_attention_interval", 4);
        }
        if (split) { Number(writer, "split.no", 0); Number(writer, "split.count", 2); }
        extra?.Invoke(writer);
        return data.ToArray();
    }

    private static void Text(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length); writer.Write(bytes);
    }
    private static void Entry(BinaryWriter writer, string key, string value)
    { Text(writer, key); writer.Write(8u); Text(writer, value); }
    private static void Number(BinaryWriter writer, string key, uint value)
    { Text(writer, key); writer.Write(4u); writer.Write(value); }
    private static void Strings(BinaryWriter writer, string key, string[] values)
    {
        Text(writer, key); writer.Write(9u); writer.Write(8u); writer.Write((ulong)values.Length);
        foreach (var value in values) Text(writer, value);
    }

    private sealed class PreviewHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => throw new NotImplementedException();
        public List<(Uri Uri, string? Authorization, string? Range, string AcceptEncoding)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString(), request.Headers.Range?.ToString(),
                request.Headers.AcceptEncoding.ToString()));
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class TrackingStream(byte[] prefix, bool fillAfterPrefix) : Stream
    {
        public long BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = Math.Max(0, prefix.Length - BytesRead);
            if (remaining == 0 && !fillAfterPrefix) return ValueTask.FromResult(0);
            var read = (int)Math.Min(buffer.Length, remaining == 0 ? buffer.Length : remaining);
            if (remaining > 0)
            {
                read = Math.Min(read, 13); // Exercise partial network reads through every scalar boundary.
                prefix.AsSpan((int)BytesRead, read).CopyTo(buffer.Span);
            }
            else buffer.Span[..read].Clear();
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
