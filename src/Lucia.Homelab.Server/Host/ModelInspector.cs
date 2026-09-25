using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using TensorSharp.Models.Architecture;
using TensorSharp.Runtime;

namespace Lucia.Homelab.Server.Host;

public sealed record ModelInspection(
    string Architecture, ModelKind Kind, long FileBytes, long WeightBytes,
    int NativeContextTokens, int AttentionLayers, long KvBytesPerToken, string[] TensorTypes);

public sealed record ChatKvMetadata(
    string Architecture, int ContextTokens, int Layers, uint KvHeads, uint KeyLength, uint ValueLength,
    uint NextnPredictLayers = 0, uint FullAttentionInterval = 4, string[]? LayerTypes = null);

public sealed record ContextPlan(
    ModelInspection Model, long MemoryLimitBytes, long OsReserveBytes, long ServicesReserveBytes,
    long VoiceReserveBytes, long RuntimeReserveBytes, long ResidentWeightBytes, long OtherResidentBytes,
    long CacheBudgetBytes, int MemoryLimitedContextTokens, int EffectiveContextTokens,
    string Qualification = "Conservative estimate, not a hardware qualification or a guarantee against allocation failure.");

public sealed class ModelInspector(IOptions<HostPlatformOptions> options)
{
    public const long GiB = 1024L * 1024 * 1024;

    public static ModelInspection Inspect(string path, ModelKind kind)
    {
        using var file = new GgufFile(path);
        if (file.Tensors.Count == 0)
            throw new InvalidDataException("The GGUF has no model tensors.");
        var architecture = file.GetString("general.architecture")
            ?? throw new InvalidDataException("The GGUF does not declare its architecture.");
        var context = checked((int)file.GetUint32($"{architecture}.context_length"));
        if (context <= 0)
            throw new InvalidDataException("The GGUF must declare a positive context length.");
        var fileBytes = file.FilePaths.Sum(p => new FileInfo(p).Length);
        var tensorTypes = file.Tensors.Values.Select(t => t.Type.ToString()).Distinct().Order().ToArray();
        if (kind == ModelKind.Embedding)
        {
            if (architecture != "bert" || file.GetString("tokenizer.ggml.model") is not ("bert" or "t5")
                || file.GetUint32("bert.pooling_type") is < 1 or > 3
                || file.GetUint32("bert.embedding_length") == 0
                || !file.Tensors.ContainsKey("token_embd.weight"))
                throw new NotSupportedException("Embeddings require a supported BERT or XLM-R sentence-encoder GGUF with tokenizer, dimensions, pooling, and token embeddings.");
            long floatWeights = 0;
            foreach (var tensor in file.Tensors.Values)
            {
                long elements = 1;
                foreach (var dimension in tensor.Shape)
                    elements = checked(elements * (long)dimension);
                floatWeights = checked(floatWeights + elements * sizeof(float));
            }
            return new(architecture, kind, fileBytes, floatWeights, context, 0, 0, tensorTypes);
        }

        ModelArchitectureRegistry.Resolve(architecture, file);
        if (string.IsNullOrWhiteSpace(file.GetString("tokenizer.ggml.model"))
            || file.GetStringArray("tokenizer.ggml.tokens") is not { Length: > 0 })
            throw new InvalidDataException("The GGUF must include its tokenizer.");
        if (ChatProtocolRegistry.For(architecture) is null)
            throw new NotSupportedException($"Architecture '{architecture}' does not have a chat protocol in this TensorSharp build.");
        var layers = checked((int)file.GetUint32($"{architecture}.block_count"));
        var heads = file.GetUint32($"{architecture}.attention.head_count");
        var kvHeads = file.GetUint32($"{architecture}.attention.head_count_kv", heads);
        var hidden = file.GetUint32($"{architecture}.embedding_length");
        if (layers <= 0 || heads == 0 || kvHeads == 0 || hidden == 0)
            throw new NotSupportedException("This model lacks the attention metadata needed for a safe context estimate.");
        var keyLength = file.GetUint32($"{architecture}.attention.key_length", hidden / heads);
        var valueLength = file.GetUint32($"{architecture}.attention.value_length", keyLength);
        var hybrid = architecture is "qwen35" or "qwen35moe" or "qwen3next";
        return EstimateChatMetadata(new(architecture, context, layers, kvHeads, keyLength, valueLength,
            hybrid ? file.GetUint32($"{architecture}.nextn_predict_layers") : 0,
            hybrid ? file.GetUint32($"{architecture}.full_attention_interval", 4) : 4,
            hybrid ? file.GetStringArray($"{architecture}.layer_types") : null), fileBytes, tensorTypes);
    }

    /// <summary>Memory arithmetic only; callers must separately validate model files and runtime compatibility.</summary>
    public static ModelInspection EstimateChatMetadata(ChatKvMetadata metadata, long fileBytes, string[]? tensorTypes = null)
    {
        var (architecture, context, layers, kvHeads, keyLength, valueLength, nextn, interval, layerTypes) = metadata;
        if (context <= 0 || layers <= 0 || kvHeads == 0 || fileBytes <= 0)
            throw new InvalidDataException("Invalid chat context metadata.");
        if (keyLength == 0 || valueLength == 0)
            throw new InvalidDataException("Invalid attention head dimensions.");
        var attentionLayers = layers;
        if (architecture is "qwen35" or "qwen35moe" or "qwen3next")
        {
            var decoderLayers = checked(layers - (int)nextn);
            if (decoderLayers <= 0 || interval == 0)
                throw new InvalidDataException("Invalid hybrid attention configuration.");
            attentionLayers = layerTypes?.Length == decoderLayers
                ? layerTypes.Count(t => !string.Equals(t, "linear_attention", StringComparison.OrdinalIgnoreCase))
                : checked(decoderLayers / (int)interval);
        }
        // ponytail: conservative F32 K/V plus a host/device mirror; calibrate from measured backend allocations.
        var kvBytes = checked((long)attentionLayers * kvHeads * ((long)keyLength + valueLength) * sizeof(float) * 2);
        if (kvBytes <= 0)
            throw new NotSupportedException("A context estimate is unavailable for this cache layout.");
        return new(architecture, ModelKind.Chat, fileBytes, fileBytes, context, attentionLayers, kvBytes, tensorTypes ?? []);
    }

    public ContextPlan Plan(ModelInspection model, long otherResidentBytes = 0, int? requestedContext = null)
    {
        var settings = options.Value;
        var detected = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (model.Kind == ModelKind.Chat && (settings.Backend is "cuda" or "ggml_cuda") && settings.MemoryBudgetGiB is null
            && !(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64))
            throw new InvalidOperationException("Set HostPlatform:MemoryBudgetGiB to the usable device-memory budget on a non-Spark GPU host. Host RAM is not discrete GPU VRAM.");
        var limit = model.Kind == ModelKind.Chat && settings.MemoryBudgetGiB is { } configured
            ? checked((long)(configured * GiB)) : detected;
        if (detected > 0)
            limit = Math.Min(limit, detected);
        if (limit <= 0)
            throw new InvalidOperationException("Memory capacity could not be detected. Configure MemoryBudgetGiB explicitly.");
        return Calculate(model, settings, limit, otherResidentBytes, requestedContext);
    }

    public static ContextPlan Calculate(ModelInspection model, HostPlatformOptions settings, long memoryLimit,
        long otherResidentBytes = 0, int? requestedContext = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(otherResidentBytes);
        if (model.Kind == ModelKind.Chat && model.KvBytesPerToken <= 0)
            throw new ArgumentException("A positive KV-cache estimate is required for chat models.", nameof(model));
        var os = checked((long)(settings.OsReserveGiB * GiB));
        var services = checked((long)(settings.ServicesReserveGiB * GiB));
        var voice = checked((long)(settings.VoiceReserveGiB * GiB));
        var runtime = checked((long)(settings.RuntimeReserveGiB * GiB));
        var weights = checked((long)Math.Ceiling(model.WeightBytes * settings.WeightMemoryMultiplier));
        var cacheBudget = Math.Max(0, checked(memoryLimit - os - services - voice - runtime - weights - otherResidentBytes));
        var capacity = cacheBudget == 0 ? 0 : model.Kind == ModelKind.Embedding ? model.NativeContextTokens
            : checked((int)Math.Min(model.NativeContextTokens, cacheBudget / model.KvBytesPerToken));
        if (model.Kind == ModelKind.Chat)
            capacity = capacity / 256 * 256;
        var desired = requestedContext ?? (model.Kind == ModelKind.Chat ? settings.ContextTokens : model.NativeContextTokens);
        if (desired < (model.Kind == ModelKind.Chat ? 256 : 1) || (requestedContext.HasValue && desired > capacity))
            throw new ArgumentOutOfRangeException(nameof(requestedContext), $"Requested context does not fit the estimated {capacity}-token capacity.");
        var effective = Math.Min(desired, capacity);
        if (model.Kind == ModelKind.Chat)
            effective = effective / 256 * 256;
        return new(model, memoryLimit, os, services, voice, runtime, weights, otherResidentBytes,
            cacheBudget, capacity, effective);
    }
}
