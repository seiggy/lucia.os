using System.Collections.Frozen;

namespace Lucia.Homelab.Server.Host;

/// <summary>
/// The model architectures the pinned vLLM images can serve, so the portal only offers repositories they can load.
/// <see cref="Supported"/> is vLLM v0.32.0's text-generation and multimodal registries, including the models served through its
/// Transformers backend; <see cref="NightlyOnly"/> is what the pinned nightly adds. Regenerate when an image changes, either by
/// running <c>python3 -c "from vllm.model_executor.models.registry import _TEXT_GENERATION_MODELS as t, _MULTIMODAL_MODELS as m, _TRANSFORMERS_SUPPORTED_MODELS as s, _TRANSFORMERS_BACKEND_MODELS as b; print(*sorted({*t, *m, *s, *(k for k in b if k.endswith('ForCausalLM'))}))"</c>
/// in the image, or statically: fetch vllm/model_executor/models/registry.py at the release tag (or nightly commit) and take the
/// quoted keys of the _TEXT_GENERATION_MODELS, _MULTIMODAL_MODELS and _TRANSFORMERS_SUPPORTED_MODELS dicts (where the Transformers-backed
/// Phi, GPTNeoX, Olmo and others live), plus the *ForCausalLM keys of _TRANSFORMERS_BACKEND_MODELS. All of them are merged into
/// the served registry. The v0.30.0 list matched this method with the first two dicts.
/// </summary>
public static class VllmArchitectures
{
    public static readonly FrozenSet<string> Supported = new[]
    {
        "AXK1ForCausalLM", "AfmoeForCausalLM", "ApertusForCausalLM", "ArceeForCausalLM",
        "AriaForConditionalGeneration", "AudioFlamingo3ForConditionalGeneration", "BagelForConditionalGeneration",
        "BailingMoeForCausalLM", "BailingMoeV2ForCausalLM", "BailingMoeV2_5ForCausalLM", "BailingMoeV3ForCausalLM",
        "BailingMoeV3VLForConditionalGeneration", "BeeForConditionalGeneration", "Blip2ForConditionalGeneration",
        "BloomForCausalLM", "ChatGLMForConditionalGeneration", "ChatGLMModel", "Cohere2ForCausalLM",
        "Cohere2MoeForCausalLM", "Cohere2VisionForConditionalGeneration", "CohereAsrForConditionalGeneration",
        "CohereCompassForConditionalGeneration", "CohereForCausalLM", "Cosmos3EdgeForConditionalGeneration",
        "Cosmos3ForConditionalGeneration", "CwmForCausalLM", "DbrxForCausalLM", "DeciLMForCausalLM",
        "DeepseekForCausalLM", "DeepseekOCR2ForCausalLM", "DeepseekOCRForCausalLM", "DeepseekV2ForCausalLM",
        "DeepseekV32ForCausalLM", "DeepseekV3ForCausalLM", "DeepseekV41ForCausalLM", "DeepseekV4ForCausalLM",
        "DeepseekV4ForConditionalGeneration", "DeepseekVLV2ForCausalLM", "DiffusionGemmaForBlockDiffusion",
        "Dots3NoteForCausalLM", "DotsOCRForCausalLM", "Eagle2_5_VLForConditionalGeneration",
        "Emu3ForConditionalGeneration", "Ernie4_5ForCausalLM", "Ernie4_5_MoeForCausalLM",
        "Ernie4_5_VLMoeForConditionalGeneration", "Exaone4ForCausalLM", "Exaone4_5_ForConditionalGeneration",
        "ExaoneForCausalLM", "ExaoneMoEForCausalLM", "ExaoneMoeForCausalLM", "FalconForCausalLM",
        "FalconH1ForCausalLM", "FalconMambaForCausalLM", "FireRedASR2ForConditionalGeneration", "FlexOlmoForCausalLM",
        "FunASRForConditionalGeneration", "FunAudioChatForConditionalGeneration", "GLM4VForCausalLM",
        "GPT2LMHeadModel", "GPTBigCodeForCausalLM", "GPTJForCausalLM", "GPTNeoXForCausalLM", "Gemma2ForCausalLM",
        "Gemma3ForCausalLM", "Gemma3ForConditionalGeneration", "Gemma3nForCausalLM",
        "Gemma3nForConditionalGeneration", "Gemma4ForCausalLM", "Gemma4ForConditionalGeneration",
        "Gemma4UnifiedForConditionalGeneration", "GemmaForCausalLM", "Glm4ForCausalLM", "Glm4MoeForCausalLM",
        "Glm4MoeLiteForCausalLM", "Glm4vForConditionalGeneration", "Glm4vMoeForConditionalGeneration",
        "Glm5NextForCausalLM", "Glm5NextForConditionalGeneration", "GlmAsrForConditionalGeneration", "GlmForCausalLM",
        "GlmMoeDsaForCausalLM", "GlmOcrForConditionalGeneration", "GptOssForCausalLM",
        "Granite4VisionForConditionalGeneration", "GraniteForCausalLM", "GraniteMoeForCausalLM",
        "GraniteMoeHybridForCausalLM", "GraniteMoeSWAForCausalLM", "GraniteMoeSharedForCausalLM",
        "GraniteSWAForCausalLM", "GraniteSpeechForConditionalGeneration", "GraniteSpeechPlusForConditionalGeneration",
        "H2OVLChatModel", "HCXVisionV2ForCausalLM", "HYV3ForCausalLM", "HYV4ForCausalLM", "HfMoondream",
        "HrmTextForCausalLM", "HunYuanDenseV1ForCausalLM", "HunYuanMoEV1ForCausalLM",
        "HunYuanVLForConditionalGeneration", "HyperCLOVAXForCausalLM", "IQuestCoderForCausalLM",
        "IQuestLoopCoderForCausalLM", "Idefics3ForConditionalGeneration", "InklingForCausalLM",
        "InklingForConditionalGeneration", "InternLM2ForCausalLM", "InternLM3ForCausalLM",
        "InternS1ForConditionalGeneration", "InternS1ProForConditionalGeneration",
        "InternS2MobiusForConditionalGeneration", "InternS2PreviewForConditionalGeneration", "InternVLChatModel",
        "InternVLForConditionalGeneration", "IsaacForConditionalGeneration", "Jais2ForCausalLM", "JambaForCausalLM",
        "K2HorizonForCausalLM", "KananaVForConditionalGeneration", "KeyeForConditionalGeneration",
        "KeyeVL1_5ForConditionalGeneration", "KimiK25ForConditionalGeneration", "KimiK3ForConditionalGeneration",
        "KimiLinearForCausalLM", "KimiVLForConditionalGeneration", "LLaMAForCausalLM", "LagunaForCausalLM",
        "Lfm2ForCausalLM", "Lfm2MoeForCausalLM", "Lfm2VlForConditionalGeneration",
        "LightOnOCRForConditionalGeneration", "Llama4ForCausalLM", "Llama4ForConditionalGeneration",
        "LlamaForCausalLM", "Llama_Nemotron_Nano_VL", "LlavaForConditionalGeneration",
        "LlavaNextForConditionalGeneration", "LlavaNextVideoForConditionalGeneration",
        "LlavaOnevision2ForConditionalGeneration", "LlavaOnevisionForConditionalGeneration",
        "LongcatFlashForCausalLM", "LongcatFlashNgramForCausalLM", "Mamba2ForCausalLM", "MambaForCausalLM",
        "MellumForCausalLM", "MiDashengLMModel", "MiMoForCausalLM", "MiMoV2FlashForCausalLM", "MiMoV2ForCausalLM",
        "MiMoV2OmniForCausalLM", "MiniCPM3ForCausalLM", "MiniCPMForCausalLM", "MiniCPMO", "MiniCPMV",
        "MiniCPMV4_6ForConditionalGeneration", "MiniCPMV4_7ForConditionalGeneration", "MiniMaxM2ForCausalLM",
        "MiniMaxM3SparseForCausalLM", "MiniMaxM3SparseForConditionalGeneration", "Ministral3ForCausalLM",
        "Mistral3ForConditionalGeneration", "MistralForCausalLM", "MistralLarge3ForCausalLM", "MixtralForCausalLM",
        "Molmo2ForConditionalGeneration", "MolmoForCausalLM", "Moondream3ForCausalLM", "MoonshotKimiaForCausalLM",
        "MossAudioModel", "MossTranscribeDiarizeForConditionalGeneration", "MuseGlimmerForCausalLM",
        "MuseGlimmerForConditionalGeneration", "NVLM_D", "NanbeigeForCausalLM", "NemotronForCausalLM",
        "NemotronHForCausalLM", "NemotronHPuzzleForCausalLM", "NemotronH_Nano_Omni_Reasoning_V3",
        "NemotronH_Nano_VL_V2", "NemotronH_Omni_Reasoning_V3", "NemotronH_Super_Omni_Reasoning_V3",
        "NemotronParseForConditionalGeneration", "OPTForCausalLM", "Olmo2ForCausalLM", "Olmo3ForCausalLM",
        "OlmoForCausalLM", "OlmoHybridForCausalLM", "OlmoeForCausalLM", "OpenCUAForConditionalGeneration",
        "OpenPanguVLForConditionalGeneration", "OpenVLAForActionPrediction", "OrionForCausalLM", "Ovis", "Ovis2_5",
        "Ovis2_6ForCausalLM", "Ovis2_6_MoeForCausalLM", "PaddleOCRVLForConditionalGeneration",
        "PaliGemmaForConditionalGeneration", "PanguEmbeddedForCausalLM", "PanguProMoEV2ForCausalLM",
        "PanguUltraMoEForCausalLM", "Param2MoEForCausalLM", "Phi3ForCausalLM", "Phi3VForCausalLM", "Phi4ForCausalLMV",
        "Phi4MMForCausalLM", "PhiForCausalLM", "PhiMoEForCausalLM", "PixtralForConditionalGeneration",
        "Plamo3ForCausalLM", "QianfanOCRForConditionalGeneration", "Qwen2AudioForConditionalGeneration",
        "Qwen2ForCausalLM", "Qwen2MoeForCausalLM", "Qwen2VLForConditionalGeneration",
        "Qwen2_5OmniForConditionalGeneration", "Qwen2_5OmniModel", "Qwen2_5_VLForConditionalGeneration",
        "Qwen3ASRForConditionalGeneration", "Qwen3ASRRealtimeGeneration", "Qwen3ForCausalLM", "Qwen3MoeForCausalLM",
        "Qwen3NextForCausalLM", "Qwen3OmniMoeForConditionalGeneration", "Qwen3VLForConditionalGeneration",
        "Qwen3VLMoeForConditionalGeneration", "Qwen3_5ForCausalLM", "Qwen3_5ForConditionalGeneration",
        "Qwen3_5MoeForCausalLM", "Qwen3_5MoeForConditionalGeneration", "Qwen4ExpForCausalLM",
        "Qwen4ExpForConditionalGeneration", "RForConditionalGeneration", "Rnj1ForCausalLM", "SarvamMLAForCausalLM",
        "SarvamMoEForCausalLM", "SeedOssForCausalLM", "SkyworkR1VChatModel", "SmolLM3ForCausalLM",
        "SmolVLMForConditionalGeneration", "SolarForCausalLM", "StableLmForCausalLM", "Starcoder2ForCausalLM",
        "Step1ForCausalLM", "Step3TextForCausalLM", "Step3VLForConditionalGeneration", "Step3p5ForCausalLM",
        "Step3p7ForConditionalGeneration", "StepVLForConditionalGeneration", "TeleChat2ForCausalLM",
        "TeleChat3ForCausalLM", "TeleFLMForCausalLM", "TransformersForCausalLM", "TransformersMoEForCausalLM",
        "TransformersMultiModalForCausalLM", "TransformersMultiModalMoEForCausalLM", "UltravoxModel",
        "UnlimitedOCRForCausalLM", "VaultGemmaForCausalLM", "VibeVoiceAsrForConditionalGeneration",
        "VoxtralForConditionalGeneration", "VoxtralRealtimeGeneration", "WhisperForConditionalGeneration",
        "Zamba2ForCausalLM"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// In the pinned nightly (commit e68efc685ab1, 2026-10-19) but not in 0.32.0. D3ForSequenceClassification lives in vLLM's
    /// sequence-classification registry; Decision 3.0 repositories name Qwen3_5Model and get it through <see cref="IsDecisionModel"/>.
    /// </summary>
    public static readonly FrozenSet<string> NightlyOnly = new[]
    {
        "AliceAIForCausalLM", "BeamForCausalLM", "D3ForSequenceClassification", "Kolibri1ForCausalLM"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// A Decision 3.0 repository: its config says Qwen3_5Model and it ships decision_config.json, which the nightly's
    /// architecture converter turns into D3ForSequenceClassification.
    /// </summary>
    public static bool IsDecisionModel(string architecture, IEnumerable<string> files) =>
        architecture == "Qwen3_5Model" && files.Contains("decision_config.json", StringComparer.Ordinal);
}
