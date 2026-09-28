using System.Globalization;
using Lucia.Homelab.Server.Onboarding;

namespace Lucia.Homelab.Server.Nodes;

/// <summary>
/// The owner's GPU choices for a server: the CUDA line its GPU containers use, and which GPUs Lucia's inference service may use.
/// Lucia never changes the line on its own; a driver that stops supporting it only flags the server.
/// </summary>
public sealed record NodeGpuSettings(int? CudaLine, bool Inference, string[] InferenceGpus)
{
    public static readonly NodeGpuSettings None = new(null, false, []);
}

public sealed record SaveNodeGpuRequest(int? CudaLine, bool Inference, string[]? InferenceGpus);

public static class CudaLines
{
    /// <summary>Oldest compute capability each line's toolkit still builds for: CUDA 13 dropped Maxwell, Pascal and Volta.</summary>
    private static readonly Dictionary<int, double> MinimumCompute = new() { [12] = 5.0, [13] = 7.5 };

    public static bool Known(int line) => MinimumCompute.ContainsKey(line);

    /// <summary>Why this server can't run the line, in words an owner can act on, or null when it can.</summary>
    public static string? Unsupported(int line, NodeRuntime? runtime)
    {
        if (!MinimumCompute.TryGetValue(line, out var minimum)) return "Choose CUDA 12 or CUDA 13.";
        if (runtime is not { Gpus.Length: > 0 }) return "This server hasn't reported an NVIDIA GPU.";
        if (!Version.TryParse(runtime.CudaVersion, out var supported))
            return "The NVIDIA driver hasn't reported which CUDA versions it supports. Update the node agent or check the driver.";
        if (supported.Major < line)
            return $"The NVIDIA driver supports up to CUDA {runtime.CudaVersion}. Update the driver to use CUDA {line}.";
        foreach (var gpu in runtime.Gpus)
        {
            if (!double.TryParse(gpu.ComputeCapability, NumberStyles.Float, CultureInfo.InvariantCulture, out var compute))
                return $"The {gpu.Model} didn't report its compute capability, so Lucia can't confirm it supports CUDA {line}.";
            if (compute < minimum)
                return $"CUDA {line} doesn't support the {gpu.Model} (compute {gpu.ComputeCapability}). It needs {minimum:0.0} or newer.";
        }
        return null;
    }

    internal static NodeGpuSettings Validate(SaveNodeGpuRequest request, NodeRuntime? runtime)
    {
        var gpus = (request.InferenceGpus ?? []).Distinct(StringComparer.Ordinal).ToArray();
        if (request.CudaLine is { } line && Unsupported(line, runtime) is { } reason)
            throw new HardwareOnboardingException(409, "cuda_line_unsupported", reason);
        if (!request.Inference) return new(request.CudaLine, false, []);
        if (request.CudaLine is null)
            throw new HardwareOnboardingException(400, "cuda_line_required", "Choose a CUDA line before using this server for local AI.");
        if (runtime is not { GpuContainers: true })
            throw new HardwareOnboardingException(409, "gpu_containers_unavailable", "Containers on this server can't use its GPUs yet.");
        if (gpus.Length == 0)
            throw new HardwareOnboardingException(400, "inference_gpus_required", "Choose at least one GPU for local AI.");
        if (gpus.Length > 16 || gpus.Any(uuid => !runtime.Gpus.Any(gpu => gpu.Uuid == uuid)))
            throw new HardwareOnboardingException(409, "unknown_gpu", "One of those GPUs isn't on this server anymore. Refresh and choose again.");
        return new(request.CudaLine, true, gpus);
    }
}
