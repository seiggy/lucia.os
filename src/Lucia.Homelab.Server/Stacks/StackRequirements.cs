using System.Globalization;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;

namespace Lucia.Homelab.Server.Stacks;

/// <summary>
/// Placement requirements a stack can ask of its node, checked against the node's last report:
/// <c>gpu</c>, <c>gpu.vendor=nvidia</c>, <c>gpu.model~4090</c>, <c>gpu.vram&gt;=24G</c>, <c>gpu.compute&gt;=8.6</c>,
/// <c>memory&gt;=32G</c>, <c>cuda=12</c>. All <c>gpu.*</c> requirements must hold for the same GPU, and that GPU must be usable
/// from containers. <c>cuda=N</c> holds on servers the owner pinned to that CUDA line, while their driver still supports it.
/// <c>nas=unas/Media</c> holds while the node reports that NAS share mounted; Lucia adds it for compose paths under /mnt/lucia/nas.
/// </summary>
public static partial class StackRequirements
{
    public const int MaxRequirements = 16;

    private sealed record Requirement(string Text, string Key, string? Operator, string? Value);

    public static string[] Normalize(string[]? requirements)
    {
        var values = (requirements ?? []).Select(item => (item ?? "").Trim()).Where(item => item.Length > 0).Distinct().ToArray();
        if (values.Length > MaxRequirements)
            throw new HardwareOnboardingException(400, "invalid_requirements", $"Use at most {MaxRequirements} requirements.");
        foreach (var value in values) Parse(value);
        return values;
    }

    /// <summary>The first requirement this node doesn't meet, or null when it's eligible.</summary>
    /// <param name="mounted">The NAS shares the node reports mounted, as <c>nas/share</c>.</param>
    public static string? Unmet(string[] requirements, NodeHeartbeat? status, NodeGpuSettings? settings = null, IReadOnlySet<string>? mounted = null)
    {
        if (requirements.Length == 0) return null;
        if (status is null) return requirements[0];
        var parsed = requirements.Select(Parse).ToArray();
        foreach (var requirement in parsed.Where(item => item.Key == "nas"))
            if (mounted?.Contains(requirement.Value!) != true) return requirement.Text;
        foreach (var requirement in parsed.Where(item => item.Key == "memory"))
            if (!Size(status.MemoryTotalBytes, requirement)) return requirement.Text;
        foreach (var requirement in parsed.Where(item => item.Key == "cuda"))
            if (settings?.CudaLine is not { } line || line.ToString(CultureInfo.InvariantCulture) != requirement.Value
                || CudaLines.Unsupported(line, status.Runtime) is not null) return requirement.Text;
        var gpu = parsed.Where(item => item.Key.StartsWith("gpu", StringComparison.Ordinal)).ToArray();
        if (gpu.Length == 0) return null;
        var runtime = status.Runtime;
        if (runtime is not { GpuContainers: true }) return gpu[0].Text;
        string? closest = null;
        foreach (var card in runtime.Gpus)
        {
            var miss = gpu.FirstOrDefault(requirement => !Matches(requirement, card));
            if (miss is null) return null;
            closest ??= miss.Text;
        }
        return closest ?? gpu[0].Text;
    }

    private static bool Matches(Requirement requirement, NodeGpu gpu) => requirement.Key switch
    {
        "gpu" => true,
        "gpu.vendor" => Text(gpu.Vendor, requirement),
        "gpu.model" => Text(gpu.Model, requirement),
        "gpu.vram" => gpu.MemoryBytes is { } memory && Size(memory, requirement),
        "gpu.compute" => double.TryParse(gpu.ComputeCapability, NumberStyles.Float, CultureInfo.InvariantCulture, out var compute)
            && Compare(compute, requirement.Operator!, double.Parse(requirement.Value!, CultureInfo.InvariantCulture)),
        _ => false,
    };

    private static bool Text(string actual, Requirement requirement) => requirement.Operator switch
    {
        "=" => actual.Equals(requirement.Value, StringComparison.OrdinalIgnoreCase),
        "!=" => !actual.Equals(requirement.Value, StringComparison.OrdinalIgnoreCase),
        _ => actual.Contains(requirement.Value!, StringComparison.OrdinalIgnoreCase),
    };

    private static bool Compare(double actual, string op, double wanted) => op switch
    {
        ">=" => actual >= wanted,
        "<=" => actual <= wanted,
        _ => Math.Abs(actual - wanted) < 1e-9,
    };

    private static bool Size(long actual, Requirement requirement)
    {
        var match = SizePattern().Match(requirement.Value!);
        var number = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var scale = match.Groups[2].Value.ToUpperInvariant() switch { "M" or "MB" => 1L << 20, "T" or "TB" => 1L << 40, _ => 1L << 30 };
        // Reported sizes run a little under the marketing size (a "24 GB" card reports ~23.6 GiB), so minimums allow 5%.
        return requirement.Operator == ">=" ? actual >= number * scale * 0.95 : actual <= number * scale;
    }

    private static Requirement Parse(string text)
    {
        var match = RequirementPattern().Match(text);
        if (!match.Success) throw Invalid(text);
        var (key, op, value) = (match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value : null,
            match.Groups[3].Success ? match.Groups[3].Value.Trim() : null);
        var valid = key switch
        {
            "gpu" => op is null,
            "gpu.vendor" or "gpu.model" => op is "=" or "!=" or "~" && value is { Length: > 0 and <= 64 },
            "gpu.vram" or "memory" => op is ">=" or "<=" && value is not null && SizePattern().IsMatch(value),
            "gpu.compute" => op is ">=" or "<=" or "=" && value is not null && ComputePattern().IsMatch(value),
            "cuda" => op is "=" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var line) && CudaLines.Known(line),
            "nas" => op is "=" && value is not null && value.Split('/') is [var nas, var share]
                && StackStore.NasIdPattern().IsMatch(nas) && StackStore.ShareNamePattern().IsMatch(share),
            _ => false,
        };
        return valid ? new(text, key, op, value) : throw Invalid(text);
    }

    private static HardwareOnboardingException Invalid(string text) => new(400, "invalid_requirement",
        $"Lucia doesn't understand the requirement \"{(text.Length > 40 ? text[..40] + "…" : text)}\". Use gpu, gpu.vendor=nvidia, gpu.model~4090, gpu.vram>=24G, gpu.compute>=8.6,         memory>=32G, cuda=12 or nas=unas/Media.");

            [GeneratedRegex(@"\A(gpu(?:\.(?:vendor|model|vram|compute))?|memory|cuda|nas)(?:\s*(>=|<=|!=|=|~)\s*(.+))?\z")]
    private static partial Regex RequirementPattern();
    [GeneratedRegex(@"\A(\d{1,6}(?:\.\d{1,3})?)\s*(M|MB|G|GB|T|TB)?\z", RegexOptions.IgnoreCase)]
    private static partial Regex SizePattern();
    [GeneratedRegex(@"\A\d{1,2}(?:\.\d{1,2})?\z")]
    private static partial Regex ComputePattern();
}
