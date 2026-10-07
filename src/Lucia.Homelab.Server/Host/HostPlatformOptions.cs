using System.ComponentModel.DataAnnotations;

namespace Lucia.Homelab.Server.Host;

public sealed class HostPlatformOptions
{
    [Required, MinLength(32)]
    public string ApiKey { get; set; } = "";

    [MinLength(32)]
    public string? InferenceApiKey { get; set; }

    [Required]
    public string ModelDirectory { get; set; } = "data/models";

    [Required]
    public string HuggingFaceExecutable { get; set; } = "hf";

    public string? HuggingFaceToken { get; set; }
    public string? HuggingFaceHomeDirectory { get; set; }

    [Required]
    public string Backend { get; set; } = "ggml_cuda";

    [Range(1, 32768)]
    public int MaxOutputTokens { get; set; } = 2048;

    public bool SreThinking { get; set; } = true;

    [Range(256, 1048576)]
    public int ContextTokens { get; set; } = 32768;

    [Range(1, 8192)]
    public double? MemoryBudgetGiB { get; set; }

    // Measured on GB10 with a loaded 22.4 GiB model: host use beside Lucia was about 3 GiB, and TensorSharp scratch about 2.3 GiB.
    [Range(0, 1024)]
    public double OsReserveGiB { get; set; } = 4;

    [Range(0, 1024)]
    public double ServicesReserveGiB { get; set; } = 3;

    [Range(8, 1024)]
    public double VoiceReserveGiB { get; set; } = 8;

    [Range(1, 1024)]
    public double RuntimeReserveGiB { get; set; } = 3;

    // Weights are uploaded once; the GGUF mmap stays clean page cache the OS can reclaim.
    [Range(1, 8)]
    public double WeightMemoryMultiplier { get; set; } = 1;

    // Off when another engine (llama.cpp, vLLM) serves the library, so GGUFs TensorSharp can't run still become Ready.
    public bool RequireTensorSharp { get; set; } = true;

    // Beside llama.cpp: a node-local directory the catalog mirrors Ready GGUF LLMs into, with the llama-models.ini
    // preset file its router serves them from. Loading 40 GB from a 1 GbE share takes minutes; from NVMe, seconds.
    public string? LlamaCache { get; set; }

    // Comma-separated llama.cpp model names its router loads when it starts.
    public string? LlamaLoadOnStartup { get; set; }

    public Guid? ChatModelId { get; set; }
    public Guid? EmbeddingModelId { get; set; }
}
