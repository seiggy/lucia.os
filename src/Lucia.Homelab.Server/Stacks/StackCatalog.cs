using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;

namespace Lucia.Homelab.Server.Stacks;

/// <summary>A catalog app's identity in the manifest. Lucia renders the compose and environment from it on every save.</summary>
public sealed record StackTemplate(string Id, int Version, Dictionary<string, string>? Settings = null);
/// <param name="Kind">
/// <c>port</c>, <c>text</c>, <c>gpus</c> (comma-separated GPU UUIDs from the server's report), <c>choice</c> (one of
/// <paramref name="Options"/>) or <c>hidden</c> (set by Lucia's own screens, never shown as a field).
/// </param>
/// <param name="When">Shown only when another setting has a value, written <c>id=value</c>.</param>
/// <param name="Optional">An empty value is allowed.</param>
public sealed record CatalogField(string Id, string Label, string Kind, string? Default = null, string? Help = null,
    CatalogOption[]? Options = null, string? When = null, bool Optional = false);
public sealed record CatalogOption(string Value, string Label, string Help);
public sealed record CatalogGpu(string Uuid, string Model, long? MemoryBytes, string? Unsupported);
/// <param name="Unmet">The placement requirement the server misses, for the portal to describe.</param>
/// <param name="Reason">Why the server can't run the app, in words, when it isn't a plain requirement.</param>
public sealed record CatalogServer(Guid NodeId, string Hostname, string? Unmet, string? Reason, CatalogGpu[]? Gpus = null);
internal sealed record CatalogOutput(string Compose, string Env, string[] Require);

/// <summary>An app Lucia ships ready to install. Its compose belongs to Lucia until the owner converts the app to a custom one.</summary>
public abstract class CatalogApp(string id, int version, string name, string summary, string needs, string[] require, CatalogField[] fields)
{
    public string Id => id;
    public int Version => version;
    public string Name => name;
    public string Summary => summary;
    /// <summary>What a server needs, in words.</summary>
    public string Needs => needs;
    /// <summary>Placement requirements every install has. Rendering may add more for the chosen server.</summary>
    public string[] Require => require;
    public CatalogField[] Fields => fields;
    /// <summary>
    /// True when the rendered compose names things only its server has, such as GPUs: the app can't move, and a server
    /// runs at most one copy.
    /// </summary>
    public virtual bool ServerBound => false;
    /// <summary>True when the app needs an address of its own, which its compose binds as <c>${LUCIA_ADDRESS}</c>.</summary>
    public virtual bool UsesAddress => false;
    /// <summary><c>live</c>, or <c>stop</c> for apps whose data is only consistent while they're stopped.</summary>
    public virtual string BackupMode => "live";
    /// <summary>Paths under the app's directory, such as <c>volumes/models</c>, that backups skip because they're rebuildable.</summary>
    public virtual string[] BackupExclude => [];
    public virtual string? Reason(ManagedNodeFacts node) => null;
    public virtual CatalogGpu[]? Gpus(ManagedNodeFacts node) => null;
    /// <param name="env">The app's current environment, so generated secrets survive re-rendering.</param>
    internal abstract CatalogOutput Render(IReadOnlyDictionary<string, string> settings, ManagedNodeFacts node, IReadOnlyDictionary<string, string> env);
}

public static class StackCatalog
{
    public static readonly CatalogApp[] Apps = [new LocalAiApp(), new AdGuardApp()];

    public static CatalogApp Find(string id) => Apps.FirstOrDefault(app => app.Id == id)
        ?? throw new HardwareOnboardingException(404, "unknown_catalog_app", "Lucia's catalog doesn't have that app.");

    public static CatalogServer Server(CatalogApp app, ManagedNodeFacts node)
    {
        var unmet = node is { Online: true, Status.Runtime.State: "Ready" } ? StackRequirements.Unmet(app.Require, node.Status, node.Gpu) : null;
        var reason = node switch
        {
            { Online: false } => "It isn't checking in.",
            { Status.Runtime.State: not "Ready" } => "Docker isn't ready on it yet.",
            _ => unmet is null ? app.Reason(node) : null,
        };
        return new(node.NodeId, node.Hostname, unmet, reason, app.Gpus(node));
    }

    /// <summary>The given settings with defaults filled in. Unknown settings are refused so typos don't vanish silently.</summary>
    internal static Dictionary<string, string> Settings(CatalogApp app, Dictionary<string, string>? given)
    {
        given ??= [];
        if (given.Keys.FirstOrDefault(key => app.Fields.All(field => field.Id != key)) is { } unknown)
            throw new HardwareOnboardingException(400, "unknown_setting", $"{app.Name} has no setting called \"{unknown}\".");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in app.Fields)
        {
            var value = (given.GetValueOrDefault(field.Id) ?? field.Default ?? "").Trim();
            var valid = (field.Optional && value.Length == 0) || field.Kind switch
            {
                "port" => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535,
                "gpus" => value.Length is > 0 and <= 1024,
                "choice" => field.Options!.Any(option => option.Value == value),
                _ => value.Length is > 0 and <= 128 && !value.Any(char.IsControl),
            };
            if (!valid)
                throw new HardwareOnboardingException(400, "invalid_setting", field.Kind switch
                {
                    "port" => $"{field.Label} must be a port from 1 to 65535.",
                    "gpus" => "Choose at least one GPU.",
                    "choice" => $"{field.Label} must be one of: {string.Join(", ", field.Options!.Select(option => option.Label))}.",
                    _ => $"{field.Label} needs a value of up to 128 characters.",
                });
            result[field.Id] = value;
        }
        return result;
    }

    internal static Dictionary<string, string> ReadEnv(string env)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in env.Split('\n'))
            if (line.IndexOf('=') is > 0 and var split && !line.StartsWith('#')) values[line[..split].Trim()] = line[(split + 1)..].Trim();
        return values;
    }

    internal static string Secret(IReadOnlyDictionary<string, string> env, string key) =>
        env.TryGetValue(key, out var value) && value.Length >= 32 ? value
            : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Local AI on an NVIDIA server, with an OpenAI-compatible <c>/v1</c> on the chosen port. The engine is Lucia Inference
/// (TensorSharp, GGUF models loaded on demand), vLLM (one safetensors model, served from startup) or llama.cpp (one GGUF
/// model, served from startup). Either way Lucia's
/// worker owns the model library, optionally on an NFS share. Images follow the server's pinned CUDA line and get only the
/// chosen GPUs.
/// </summary>
internal sealed partial class LocalAiApp() : CatalogApp("local-ai", 6, "Local AI",
    "Serve models on this server's NVIDIA GPUs, with an OpenAI-compatible API.",
    "An NVIDIA GPU (compute 7.0 or newer) and a CUDA line chosen in Devices.",
    ["gpu.vendor=nvidia", "gpu.compute>=7.0"],
    [
        new("engine", "Engine", "choice", "lucia", Options:
        [
            new("lucia", "Lucia Inference", "Runs GGUF models and switches between them as you choose them. Best for trying models."),
            new("vllm", "vLLM", "Serves one safetensors model, fast, to many requests at once. Best for a model every app shares."),
                       new("llamacpp", "llama.cpp", "Serves every downloaded GGUF LLM, loading the one each request names. Best for large quantized models."),
                   ]),
        new("gpus", "GPUs", "gpus", Help: "Local AI gets these GPUs to itself. Their memory sets how large a model it can load."),
        new("port", "Port", "port", "8080", "Apps on your network reach local AI at http://<server>:<port>/v1."),
        new("library-port", "Library port", "port", "8081", "Lucia manages the model library through this port.", When: "engine=vllm|llamacpp"),
        new("library", "Model library", "text", Help: "An NFS share to keep models on, written host:/path, such as a NAS. "
            + "Downloads go to a folder named after this server. Leave it blank to keep models on this server.", Optional: true),
        // vLLM's served model, set from the Models page. llama.cpp serves the whole library, so it ignores these;
        // vllm-file stays because saved installs carry it and unknown settings are rejected.
        new("vllm-model", "Served model", "hidden", Optional: true),
        new("vllm-name", "Served model name", "hidden", Optional: true),
        new("vllm-context", "Served context", "hidden", "auto"),
        new("vllm-file", "Served GGUF file", "hidden", Optional: true),
    ])
{
    // Pinned by digest so every server runs exactly what was tested. Published from deployment/inference/Dockerfile.
    private static readonly Dictionary<int, string> Images = new()
    {
        [12] = "seiggy/lucia-inference:0.1.4-cuda12@sha256:8e2c0b81e425db1dc5fcf038a4d4a815226f0d79ecbd3211f59d95d89fe3827b",
        [13] = "seiggy/lucia-inference:0.1.4-cuda13@sha256:af3de9cefd991df065fd922694bbe80aa94bc49fcf5b89baaf3d84da5d4da171",
    };

    private static readonly Dictionary<int, string> VllmImages = new()
    {
        [12] = "vllm/vllm-openai:v0.30.0-cu129@sha256:a67f8f186d4567612ac37a55bd82295006002b18858eb12a8af2f05f86c2ae3f",
        [13] = "vllm/vllm-openai:v0.30.0@sha256:8a69ffad015f138d7170c4ddc429e230a3bc1c1719f67e14324749df200a4b90",
    };

    private static readonly Dictionary<int, string> LlamaImages = new()
    {
        [12] = "ghcr.io/ggml-org/llama.cpp:server-cuda-b11206@sha256:3e7673cce183a55f97a1bc3c80817f3c61452483c13bc088a6766388af4775fe",
        [13] = "ghcr.io/ggml-org/llama.cpp:server-cuda13-b11206@sha256:91ac61def9ed19af96b9b2d2a0c1570a0903046f4e2dcbac7e3d965e5917b4ba",
    };

    public override bool ServerBound => true;
    // Models come back from the library or Hugging Face; only the worker's catalog and settings in data/ are worth keeping.
    public override string[] BackupExclude => ["volumes/models", "volumes/llama-cache", "volumes/vllm-cache"];

    public override string? Reason(ManagedNodeFacts node)
    {
        if (node.Gpu?.CudaLine is not { } line) return "Choose a CUDA line for it in Devices first.";
        if (CudaLines.Unsupported(line, node.Status?.Runtime) is { } unsupported) return unsupported;
        return Gpus(node)!.All(gpu => gpu.Unsupported is not null) ? $"None of its GPUs can run local AI on CUDA {line}." : null;
    }

    public override CatalogGpu[]? Gpus(ManagedNodeFacts node) =>
        (node.Status?.Runtime?.Gpus ?? []).Where(gpu => GpuUuid().IsMatch(gpu.Uuid ?? "") && gpu.Vendor.Equals("nvidia", StringComparison.OrdinalIgnoreCase))
            .Select(gpu => new CatalogGpu(gpu.Uuid!, gpu.Model, gpu.MemoryBytes,
                node.Gpu?.CudaLine is { } line ? CudaLines.LocalAiUnsupported(line, gpu) : null)).ToArray();

    internal override CatalogOutput Render(IReadOnlyDictionary<string, string> settings, ManagedNodeFacts node, IReadOnlyDictionary<string, string> env)
    {
        if (Reason(node) is { } reason) throw new HardwareOnboardingException(409, "node_not_eligible", $"{node.Hostname}: {reason}");
        var line = node.Gpu!.CudaLine!.Value;
        var available = Gpus(node)!;
        var chosen = settings["gpus"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
        var gpus = chosen.Select(uuid => available.FirstOrDefault(gpu => gpu.Uuid == uuid)
            ?? throw new HardwareOnboardingException(409, "unknown_gpu", $"{node.Hostname} doesn't have that GPU anymore. Choose again.")).ToArray();
        if (gpus.Length == 0) throw new HardwareOnboardingException(400, "invalid_setting", "Choose at least one GPU.");
        if (gpus.FirstOrDefault(gpu => gpu.Unsupported is not null) is { } old)
            throw new HardwareOnboardingException(409, "local_ai_gpu_unsupported", old.Unsupported!);
        if (gpus.Any(gpu => gpu.MemoryBytes is null))
            throw new HardwareOnboardingException(409, "gpu_memory_unknown", "One of those GPUs didn't report its memory, so Lucia can't size models for it.");
        var budget = Math.Floor(gpus.Sum(gpu => gpu.MemoryBytes!.Value) / (double)(1L << 30));

        var engine = settings["engine"];
        // vLLM and llama.cpp own the GPUs; the worker only keeps the model library for them.
        var dedicated = engine != "lucia";
        var library = settings["library"];
        var share = NfsShare().Match(library);
        if (library.Length > 0 && !share.Success)
            throw new HardwareOnboardingException(400, "invalid_setting", "Model library must be an NFS share written host:/path, such as 192.168.1.10:/models.");
        if (library.Length > 0 && !HostName().IsMatch(node.Hostname))
            throw new HardwareOnboardingException(409, "invalid_hostname", $"{node.Hostname} can't name a folder on the model library.");
        // A share can hold several servers' libraries, each with its own catalog lease. The volume has its own name so
        // switching between local and shared storage never asks Compose to recreate a volume that holds models.
        var root = library.Length > 0 ? $"/models/{node.Hostname.ToLowerInvariant()}" : "/models";
        var volume = library.Length > 0 ? "library" : "models";
        // NFS shares squash ownership, so Docker's first-mount copy (and its chmod) must be skipped for them.
        var nocopy = library.Length > 0 ? ":nocopy" : "";
        var devices = $$"""
                deploy:
                  resources:
                    reservations:
                      devices:
                        - driver: nvidia
                          device_ids: [{{string.Join(", ", gpus.Select(gpu => $"\"{gpu.Uuid}\""))}}]
                          capabilities: [gpu]
            """;
        var worker = $$"""
              inference:
                image: {{Images[line]}}
                restart: unless-stopped
                ports:
                  - "{{settings[dedicated ? "library-port" : "port"]}}:8080"
                environment:
                  HostPlatform__ApiKey: ${LUCIA_WORKER_KEY}
                  HostPlatform__InferenceApiKey: ${LUCIA_INFERENCE_KEY}
                  HostPlatform__MemoryBudgetGiB: "{{budget.ToString(CultureInfo.InvariantCulture)}}"
                  HostPlatform__ModelDirectory: {{root}}
            {{(dedicated ? "      Worker__LibraryOnly: \"true\"\n" : "")}}{{(engine == "llamacpp" ? "      HostPlatform__LlamaCache: /cache\n" : "")}}    volumes:
                  - {{volume}}:/models{{nocopy}}
                  - data:/data
            {{(engine == "llamacpp" ? "      - llama-cache:/cache\n" : "")}}{{(dedicated ? "" : devices)}}
            """;
        var server = engine switch
        {
            "vllm" when settings["vllm-model"].Length > 0 => Vllm(settings, node, line, gpus.Length, root, volume, devices),
            "llamacpp" => Llama(settings, node, line, devices),
            _ => "",
        };
        var volumes = library.Length > 0
            ? $$"""
                library:
                  driver: local
                  driver_opts:
                    type: nfs
                    o: "addr={{share.Groups["host"].Value}},nfsvers=3,nolock,hard,rsize=1048576,wsize=1048576"
                    device: ":{{share.Groups["path"].Value}}"
              """
            : "  models:";
        var compose = $$"""
            # Installed from Lucia's catalog (local-ai, version {{Version}}). Lucia rewrites this file when you change the
            # app's settings. Convert the app to a custom app to edit it by hand.
            services:
            {{worker.TrimEnd()}}
            {{server}}volumes:
            {{volumes}}
              data:{{(engine == "vllm" && server.Length > 0 ? "\n  vllm-cache:" : "")}}{{(engine == "llamacpp" ? "\n  llama-cache:" : "")}}

            """;
        var envText = $"LUCIA_WORKER_KEY={StackCatalog.Secret(env, "LUCIA_WORKER_KEY")}\n"
            + $"LUCIA_INFERENCE_KEY={StackCatalog.Secret(env, "LUCIA_INFERENCE_KEY")}\n";
        return new(compose.ReplaceLineEndings("\n").Replace("\n\n\n", "\n\n"), envText, [.. Require, $"cuda={line}"]);
    }

    /// <summary>vLLM serving the chosen library model read-only, under its repository name.</summary>
    private static string Vllm(IReadOnlyDictionary<string, string> settings, ManagedNodeFacts node, int line, int gpus, string root,
        string volume, string devices)
    {
        var (model, name, context) = Served(settings, node, line, "vLLM", new System.Version(12, 9));
        return $$"""
              vllm:
                image: {{VllmImages[line]}}
                restart: unless-stopped
                ports:
                  - "{{settings["port"]}}:8000"
                shm_size: 8gb
                environment:
                  VLLM_API_KEY: ${LUCIA_INFERENCE_KEY}
                  HF_HUB_OFFLINE: "1"
                  VLLM_NO_USAGE_STATS: "1"
                  DO_NOT_TRACK: "1"
                command: ["{{root}}/{{model:N}}/files", "--served-model-name", "{{name}}", "--max-model-len", "{{context}}", "--tensor-parallel-size", "{{gpus}}"]
                volumes:
                  - {{volume}}:/models:ro{{(volume == "library" ? ",nocopy" : "")}}
                  - vllm-cache:/root/.cache/vllm
                healthcheck:
                  test: ["CMD", "curl", "-fsS", "http://localhost:8000/health"]
                  interval: 30s
                  timeout: 5s
                  retries: 3
                  start_period: 30m
                  start_interval: 5s
            {{devices}}

            """;
    }

    /// <summary>
    /// llama.cpp's router serving every library GGUF the worker has copied to the node's own disk, one loaded at a time.
    /// </summary>
    private static string Llama(IReadOnlyDictionary<string, string> settings, ManagedNodeFacts node, int line, string devices)
    {
        DriverRuns(node, line, "llama.cpp", new System.Version(12, 8));
        // Each model's context comes from the model, lowered by --fit (on by default) until weights and cache fit the GPUs.
        return $$"""
              llama:
                image: {{LlamaImages[line]}}
                restart: unless-stopped
                depends_on:
                  inference:
                    condition: service_healthy
                ports:
                  - "{{settings["port"]}}:8080"
                environment:
                  LLAMA_API_KEY: ${LUCIA_INFERENCE_KEY}
                command: ["--models-preset", "/cache/llama-models.ini", "--models-max", "1", "--host", "0.0.0.0", "--port", "8080"]
                volumes:
                  - llama-cache:/cache:ro
                healthcheck:
                  test: ["CMD", "curl", "-fsS", "http://localhost:8080/health"]
                  interval: 30s
                  timeout: 5s
                  retries: 3
                  start_period: 2m
                  start_interval: 5s
            {{devices}}

            """;
    }

    /// <summary>The served model's id, name and context, after checking the driver runs the engine's CUDA 12 build.</summary>
    private static (Guid Model, string Name, string Context) Served(IReadOnlyDictionary<string, string> settings, ManagedNodeFacts node, int line,
        string engine, System.Version cuda12)
    {
        if (!Guid.TryParse(settings["vllm-model"], out var model))
            throw new HardwareOnboardingException(400, "invalid_setting", $"Choose the {engine} model from the Models page.");
        var name = settings["vllm-name"];
        if (!RepositoryName().IsMatch(name))
            throw new HardwareOnboardingException(400, "invalid_setting", $"The {engine} model name must be a Hugging Face repository, such as Qwen/Qwen3-8B.");
        var context = settings["vllm-context"];
        if (context != "auto" && !(int.TryParse(context, NumberStyles.None, CultureInfo.InvariantCulture, out var tokens) && tokens is >= 256 and <= 4_194_304))
            throw new HardwareOnboardingException(400, "invalid_setting", $"The {engine} context must be auto or a token count from 256.");
        DriverRuns(node, line, engine, cuda12);
        return (model, name, context);
    }

    // The CUDA 12 images are built for a newer CUDA 12 than the line's floor.
    private static void DriverRuns(ManagedNodeFacts node, int line, string engine, System.Version cuda12)
    {
        if (line == 12 && System.Version.TryParse(node.Status?.Runtime?.CudaVersion, out var driver) && driver < cuda12)
            throw new HardwareOnboardingException(409, engine == "vLLM" ? "vllm_driver_too_old" : "llamacpp_driver_too_old",
                $"The NVIDIA driver supports up to CUDA {node.Status!.Runtime!.CudaVersion}. {engine} needs CUDA {cuda12}: update the driver, or use Lucia Inference.");
    }

    // UUIDs go into the compose file, so only the exact NVIDIA form is accepted.
    [GeneratedRegex(@"\AGPU-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegexOptions.IgnoreCase)]
    private static partial Regex GpuUuid();

    [GeneratedRegex(@"\A(?<host>[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?):(?<path>/[A-Za-z0-9._/-]{0,200})\z")]
    private static partial Regex NfsShare();

    [GeneratedRegex(@"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\z")]
    private static partial Regex HostName();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,95}/[A-Za-z0-9][A-Za-z0-9._-]{0,95}\z")]
    private static partial Regex RepositoryName();
}

/// <summary>
/// AdGuard Home, answering DNS for the network on an address of its own so clients never follow it between servers. Its
/// setup and settings stay in AdGuard: first run is the wizard on port 3000, and Lucia's DNS rewrites reach it through
/// its connection in Settings.
/// </summary>
internal sealed class AdGuardApp() : CatalogApp("adguard", 1, "AdGuard Home",
    "Block ads and trackers for every device on your network, and answer for Lucia's own names.",
    "Any server with Docker ready, and a free address on your network for devices to use as their DNS server.",
    [], [])
{
    private const string Image = "adguard/adguardhome:v0.107.79@sha256:aba9e3bf0613be3ba3755e1fc311b126e2c24bec25e18b6483894a88283074f0";

    public override bool UsesAddress => true;

    internal override CatalogOutput Render(IReadOnlyDictionary<string, string> settings, ManagedNodeFacts node, IReadOnlyDictionary<string, string> env)
    {
        // DNS, the web interface, DNS-over-HTTPS (and HTTP/3), DNS-over-TLS and -QUIC, and the first-run wizard.
        string[] ports = ["53/tcp", "53/udp", "80/tcp", "443/tcp", "443/udp", "853/tcp", "853/udp", "3000/tcp"];
        var compose = $$"""
            # Installed from Lucia's catalog (adguard, version {{Version}}). Lucia rewrites this file when the catalog
            # updates the app. Convert the app to a custom app to edit it by hand.
            services:
              adguard:
                image: {{Image}}
                restart: unless-stopped
                ports:
            {{string.Join("\n", ports.Select(port => $"      - \"${{LUCIA_ADDRESS}}:{port.Split('/')[0]}:{port}\""))}}
                volumes:
                  - work:/opt/adguardhome/work
                  - conf:/opt/adguardhome/conf
            volumes:
              work:
              conf:

            """;
        return new(compose.ReplaceLineEndings("\n"), "", []);
    }
}