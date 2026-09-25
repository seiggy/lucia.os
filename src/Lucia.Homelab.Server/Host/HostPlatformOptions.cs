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

    [Range(0, 1024)]
    public double OsReserveGiB { get; set; } = 8;

    [Range(0, 1024)]
    public double ServicesReserveGiB { get; set; } = 8;

    [Range(8, 1024)]
    public double VoiceReserveGiB { get; set; } = 8;

    [Range(1, 1024)]
    public double RuntimeReserveGiB { get; set; } = 8;

    [Range(1, 8)]
    public double WeightMemoryMultiplier { get; set; } = 2;

    public Guid? ChatModelId { get; set; }
    public Guid? EmbeddingModelId { get; set; }
}
