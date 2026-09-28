using System.Globalization;
using Lucia.Homelab.Server.Onboarding;

namespace Lucia.Homelab.Server.Nodes;

/// <summary>
/// The owner's GPU choice for a server: the CUDA line its GPU containers use. Lucia never changes the line on its own; a
/// driver that stops supporting it only flags the server. Which GPUs local AI uses is a setting of the Local AI app.
/// </summary>
/// <param name="Inference">Retired with <paramref name="InferenceGpus"/>; kept so older node files still read.</param>
public sealed record NodeGpuSettings(int? CudaLine, bool Inference = false, string[]? InferenceGpus = null)
{
    public static readonly NodeGpuSettings None = new(CudaLine: null);
}

public sealed record SaveNodeGpuRequest(int? CudaLine);

public static class CudaLines
{
    /// <summary>Oldest compute capability each line's toolkit still builds for: CUDA 13 dropped Maxwell, Pascal and Volta.</summary>
    private static readonly Dictionary<int, double> MinimumCompute = new() { [12] = 5.0, [13] = 7.5 };
    /// <summary>Oldest compute capability Lucia's inference service is built for on each line.</summary>
    private static readonly Dictionary<int, double> LocalAiMinimumCompute = new() { [12] = 7.0, [13] = 7.5 };

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

    /// <summary>Why local AI can't use this GPU on the line, or null when it can. Assumes the line itself is supported.</summary>
    public static string? LocalAiUnsupported(int line, NodeGpu gpu) =>
        LocalAiMinimumCompute.TryGetValue(line, out var minimum)
        && double.TryParse(gpu.ComputeCapability, NumberStyles.Float, CultureInfo.InvariantCulture, out var compute)
        && compute < minimum
            ? $"Local AI on CUDA {line} needs compute {minimum:0.0} or newer. The {gpu.Model} is {gpu.ComputeCapability}."
            : null;

    internal static NodeGpuSettings Validate(SaveNodeGpuRequest request, NodeRuntime? runtime)
    {
        if (request.CudaLine is { } line && Unsupported(line, runtime) is { } reason)
            throw new HardwareOnboardingException(409, "cuda_line_unsupported", reason);
        return new(request.CudaLine);
    }
}
