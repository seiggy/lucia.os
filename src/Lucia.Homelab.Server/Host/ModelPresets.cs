namespace Lucia.Homelab.Server.Host;

public sealed record ModelPreset(
    string Id,
    string Name,
    ModelDownloadRequest Source,
    long SizeBytes,
    string Sha256,
    string Architecture,
    int NativeContextTokens,
    string LicenseUrl,
    string Qualification);

public static class ModelPresets
{
    public static readonly ModelPreset Bundled = new(
        "qwen36-35b-a3b-q6",
        "Qwen3.6 35B-A3B MTP / UD-Q6_K_XL",
        new("huggingface", "unsloth/Qwen3.6-35B-A3B-MTP-GGUF",
            "Qwen3.6-35B-A3B-UD-Q6_K_XL.gguf", ModelKind.Chat,
            "5bc3e238d916f48a861bac2f8a1990a0e9b7e98d"),
        32_611_711_264,
        "35fce994cd36104a7dc1bd8a4bdf13778145664c00fdef6773aebc9246e5019c",
        "qwen35moe",
        262_144,
        "https://huggingface.co/Qwen/Qwen3.6-35B-A3B/raw/995ad96eacd98c81ed38be0c5b274b04031597b0/LICENSE",
        "Selected for bundling; exact-file TensorSharp and DGX Spark qualification is pending.");

    public static ModelPreset? Match(ModelDownloadRequest request) =>
        request with { Pro = false, SizeBytes = null } == Bundled.Source ? Bundled : null;
}
