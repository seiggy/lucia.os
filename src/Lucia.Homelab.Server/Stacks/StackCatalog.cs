using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Telemetry;

namespace Lucia.Homelab.Server.Stacks;

/// <summary>A catalog app's identity in the manifest. Lucia renders the compose and environment from it on every save.</summary>
public sealed record StackTemplate(string Id, int Version, Dictionary<string, string>? Settings = null);
/// <param name="Kind">
/// <c>port</c>, <c>text</c>, <c>gpus</c> (comma-separated GPU UUIDs from the server's report), <c>choice</c> (one of
/// <paramref name="Options"/>), <c>secret</c> (kept in the app's environment as the id in upper snake case, never in its
/// manifest; blank keeps the saved value) or <c>hidden</c> (set by Lucia's own screens, never shown as a field).
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
/// <param name="Routes">The app's web addresses, checked and stored in its manifest.</param>
internal sealed record CatalogOutput(string Compose, string Env, string[] Require, StackRoute[]? Routes = null);

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
    /// <summary>The app's sign-in through Lucia's Authentik, when it has one. Its render must generate <c>SSO_CLIENT_SECRET</c>.</summary>
    public virtual AppSso? Sso(IReadOnlyDictionary<string, string> settings) => null;
    /// <summary>
    /// True when the app sends OpenTelemetry: while an Observability app runs, Lucia keeps its OTLP/HTTP address and
    /// <c>Authorization</c> header in the app's environment as <c>LUCIA_OTLP_ENDPOINT</c> and <c>LUCIA_OTLP_AUTHORIZATION</c>.
    /// </summary>
    public virtual bool Telemetry => false;
    /// <param name="env">The app's current environment, so generated secrets survive re-rendering.</param>
    internal abstract CatalogOutput Render(IReadOnlyDictionary<string, string> settings, ManagedNodeFacts node, IReadOnlyDictionary<string, string> env);
}

public static class StackCatalog
{
    public static readonly CatalogApp[] Apps = [new LocalAiApp(), new AdGuardApp(), new ObservabilityApp(), new MusicBrainzApp(), new ImmichApp(), new LiteLlmApp()];

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
                // It's written to the app's .env unquoted.
                "secret" => value.Length <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || "._~+/=-".Contains(c)),
                _ => value.Length is > 0 and <= 128 && !value.Any(char.IsControl),
            };
            if (!valid)
                throw new HardwareOnboardingException(400, "invalid_setting", field.Kind switch
                {
                    "port" => $"{field.Label} must be a port from 1 to 65535.",
                    "gpus" => "Choose at least one GPU.",
                    "choice" => $"{field.Label} must be one of: {string.Join(", ", field.Options!.Select(option => option.Label))}.",
                    "secret" => $"{field.Label} must be up to 128 letters, digits and . _ ~ + / = -",
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

    internal static string SecretKey(CatalogField field) => field.Id.ToUpperInvariant().Replace('-', '_');

    /// <summary>Moves typed <c>secret</c> settings into <paramref name="env"/> and blanks them in <paramref name="settings"/>.</summary>
    internal static Dictionary<string, string> KeepSecrets(CatalogApp app, Dictionary<string, string> settings, Dictionary<string, string> env)
    {
        foreach (var field in app.Fields.Where(field => field.Kind == "secret"))
        {
            if (settings[field.Id].Length > 0) env[SecretKey(field)] = settings[field.Id];
            settings[field.Id] = "";
        }
        return env;
    }
}

/// <summary>
/// Local AI on an NVIDIA server, with an OpenAI-compatible <c>/v1</c> on the chosen port. The engine is Lucia Inference
/// (TensorSharp, GGUF models loaded on demand), vLLM (one safetensors model, served from startup) or llama.cpp (one GGUF
/// model, served from startup). Either way Lucia's
/// worker owns the model library, optionally on an NFS share. Images follow the server's pinned CUDA line and get only the
/// chosen GPUs.
/// </summary>
internal sealed partial class LocalAiApp() : CatalogApp("local-ai", 8, "Local AI",
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
        [12] = "seiggy/lucia-inference:0.1.5-cuda12@sha256:20115802198c371465c50ccbe5864fd8b8bff04743c10d6f7a1782261034555f",
        [13] = "seiggy/lucia-inference:0.1.5-cuda13@sha256:c82e4d1a137184b7a6739f9a7bf75cb636e9518bf3e87d1b541f1514ad465a95",
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
                  # Prometheus metrics at /metrics?model=<name>, which Lucia's telemetry relay scrapes.
                  LLAMA_ARG_ENDPOINT_METRICS: "1"
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

/// <summary>
/// Lucia's telemetry backend: an OpenTelemetry collector that takes OTLP with a password and stores traces in Tempo,
/// metrics in Prometheus and logs in Loki, with Grafana to explore them. Both get web addresses under the active domain,
/// and Grafana signs in through Lucia's Authentik there: owners become Grafana admins.
/// </summary>
internal sealed class ObservabilityApp() : CatalogApp("observability", 5, "Observability",
    "Collect traces, metrics and logs from your servers and apps, and explore them in Grafana.",
    "Any server with Docker ready, and room for about 50 GB of telemetry.",
    [],
    [
        new("grafana-host", "Grafana name", "text", "grafana", "Grafana's web address is this name under your domain."),
        new("otlp-host", "OTLP name", "text", "otlp", "Apps send telemetry to this name under your domain, over gRPC or HTTP."),
        new("grafana-port", "Grafana port", "port", "3030", "Grafana also answers on http://<server>:<port>."),
        new("otlp-grpc-port", "OTLP gRPC port", "port", "4317"),
        new("otlp-http-port", "OTLP HTTP port", "port", "4318"),
    ])
{
    internal const string Collector = "otel/opentelemetry-collector-contrib:0.161.0@sha256:fd328de2552466ad78385e1b1289c3f2402b1c45f265b252aab1955b42845ac1";
    private const string Prometheus = "prom/prometheus:v3.15.0@sha256:efd719c99d83b060d9daefdcf00360461adf279f45ef5391f8d111892118753e";
    private const string Loki = "grafana/loki:3.7.8@sha256:1107dd5274e0ada47e42472b7a7e71f3b2a2fe878878108f3e2f9e51528f0193";
    private const string Tempo = "grafana/tempo:2.10.8@sha256:f0561deb1c68ec44d6e6e7e4487f30106c4e5e768642077695b37958b105812a";
    private const string Grafana = "grafana/grafana:13.2.2@sha256:ac461fb352abc50da10a51c7d02462e9c05488f11f53f14b3ad79a8145f638a0";
    private const string Alpine = "alpine:3.22.1@sha256:4bcff63911fcb4448bd4fdacec207030997caf25e9bea4045fa6c8c44de311d1";
    public const string User = "lucia";

    public override AppSso Sso(IReadOnlyDictionary<string, string> settings) =>
        new("Grafana", settings["grafana-host"].ToLowerInvariant(), "/login/generic_oauth");

    internal override CatalogOutput Render(IReadOnlyDictionary<string, string> settings, ManagedNodeFacts node, IReadOnlyDictionary<string, string> env)
    {
        var (grafanaHost, otlpHost) = (settings["grafana-host"].ToLowerInvariant(), settings["otlp-host"].ToLowerInvariant());
        var (grafanaPort, grpcPort, httpPort) = (settings["grafana-port"], settings["otlp-grpc-port"], settings["otlp-http-port"]);
        // The collector expands ${env:...} itself, so compose must leave it alone; Grafana expands $var, so its literal needs a second escape.
        var collector = """
            extensions:
              basicauth/server:
                htpasswd:
                  inline: |
                    $${env:OTLP_USERNAME}:$${env:OTLP_PASSWORD}
              file_storage:
                directory: /var/lib/otelcol
                create_directory: true
                compaction:
                  directory: /var/lib/otelcol
                  on_start: true
                  on_rebound: true
              health_check:
                endpoint: 0.0.0.0:13133
            receivers:
              otlp:
                protocols:
                  grpc:
                    endpoint: 0.0.0.0:4317
                    auth:
                      authenticator: basicauth/server
                  http:
                    endpoint: 0.0.0.0:4318
                    auth:
                      authenticator: basicauth/server
            processors:
              memory_limiter:
                check_interval: 1s
                limit_percentage: 65
                spike_limit_percentage: 15
              batch:
                timeout: 5s
                send_batch_size: 1024
                send_batch_max_size: 2048
            exporters:
              otlp/tempo:
                endpoint: tempo:4317
                tls:
                  insecure: true
                sending_queue: &queue
                  enabled: true
                  storage: file_storage
                  queue_size: 5000
                  num_consumers: 2
                retry_on_failure: &retry
                  enabled: true
                  max_elapsed_time: 0s
              otlphttp/prometheus:
                endpoint: http://prometheus:9090/api/v1/otlp
                sending_queue: *queue
                retry_on_failure: *retry
              otlphttp/loki:
                endpoint: http://loki:3100/otlp
                sending_queue: *queue
                retry_on_failure: *retry
            service:
              extensions: [basicauth/server, file_storage, health_check]
              telemetry:
                metrics:
                  readers:
                    - pull:
                        exporter:
                          prometheus:
                            host: 0.0.0.0
                            port: 8888
              pipelines:
                traces:
                  receivers: [otlp]
                  processors: [memory_limiter, batch]
                  exporters: [otlp/tempo]
                metrics:
                  receivers: [otlp]
                  processors: [memory_limiter, batch]
                  exporters: [otlphttp/prometheus]
                logs:
                  receivers: [otlp]
                  processors: [memory_limiter, batch]
                  exporters: [otlphttp/loki]
            """;
        var prometheus = """
            global:
              scrape_interval: 30s
              evaluation_interval: 30s
            otlp:
              promote_resource_attributes: [service.instance.id, service.name, service.namespace, host.name, lucia.node, lucia.app, container.name]
            storage:
              tsdb:
                out_of_order_time_window: 30m
            scrape_configs:
              - job_name: otel-collector
                static_configs:
                  - targets: [collector:8888]
            """;
        var loki = """
            auth_enabled: false
            server:
              http_listen_port: 3100
            common:
              path_prefix: /var/loki
              replication_factor: 1
              ring:
                kvstore:
                  store: inmemory
            schema_config:
              configs:
                - from: 2024-04-01
                  store: tsdb
                  object_store: filesystem
                  schema: v13
                  index:
                    prefix: index_
                    period: 24h
            storage_config:
              filesystem:
                directory: /var/loki/chunks
              tsdb_shipper:
                active_index_directory: /var/loki/index
                cache_location: /var/loki/index_cache
            compactor:
              working_directory: /var/loki/compactor
              retention_enabled: true
              delete_request_store: filesystem
            limits_config:
              allow_structured_metadata: true
              retention_period: 168h
            analytics:
              reporting_enabled: false
            """;
        var tempo = """
            stream_over_http_enabled: true
            server:
              http_listen_port: 3200
            distributor:
              receivers:
                otlp:
                  protocols:
                    grpc:
                      endpoint: 0.0.0.0:4317
            ingester:
              max_block_duration: 5m
            compactor:
              compaction:
                block_retention: 336h
            storage:
              trace:
                backend: local
                wal:
                  path: /var/tempo/wal
                local:
                  path: /var/tempo/blocks
            usage_report:
              reporting_enabled: false
            """;
        var datasources = """
            apiVersion: 1
            datasources:
              - name: Prometheus
                type: prometheus
                uid: prometheus
                access: proxy
                url: http://prometheus:9090
                isDefault: true
                jsonData:
                  timeInterval: 30s
              - name: Loki
                type: loki
                uid: loki
                access: proxy
                url: http://loki:3100
                jsonData:
                  derivedFields:
                    - name: TraceID
                      matcherRegex: '"trace_id"[=:]"?([a-f0-9]{32})'
                      datasourceUid: tempo
                      url: '$$$${__value.raw}'
              - name: Tempo
                type: tempo
                uid: tempo
                access: proxy
                url: http://tempo:3200
                jsonData:
                  tracesToLogsV2:
                    datasourceUid: loki
                    spanStartTimeShift: -5m
                    spanEndTimeShift: 5m
                    tags: [service.name]
                    filterByTraceID: true
                  tracesToMetrics:
                    datasourceUid: prometheus
                    spanStartTimeShift: -5m
                    spanEndTimeShift: 5m
                    tags:
                      - key: service.name
                        value: service_name
                  nodeGraph:
                    enabled: true
                  serviceMap:
                    datasourceUid: prometheus
            """;
        var dashboards = """
            apiVersion: 1
            providers:
              - name: Lucia
                folder: Lucia
                type: file
                disableDeletion: true
                allowUiUpdates: false
                options:
                  path: /etc/grafana/dashboards/lucia
            """;
        static string Content(string text) => string.Join("\n", text.ReplaceLineEndings("\n").Split('\n').Select(line => "      " + line));
        // Compose before 2.30 keeps a container when only its inline configs change; the label makes it recreate.
        static string Hash(params string[] texts) =>
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\0', texts))))[..16];
        var hardware = HardwareDashboard.Json().Replace("$", "$$");
        var inference = InferenceDashboard.Json().Replace("$", "$$");
        // Grafana links to itself at its web address once the domain is active, and at the server's port until then.
        var rootUrl = "${" + StackStore.RouteVariable(grafanaHost) + $":-http://{node.Hostname}:{grafanaPort}}}";
        var compose = $$"""
            # Installed from Lucia's catalog (observability, version {{Version}}). Lucia rewrites this file when the catalog
            # updates the app. Convert the app to a custom app to edit it by hand.
            services:
              # Each image runs as its own user; give each its data directory once.
              init:
                image: {{Alpine}}
                restart: "no"
                user: "0:0"
                command:
                  - sh
                  - -c
                  - |
                    own() { [ "$$(stat -c %u:%g "$$2")" = "$$1" ] || chown -R "$$1" "$$2"; }
                    own 10001:10001 /data/collector && own 10001:10001 /data/loki && own 10001:10001 /data/tempo \
                      && own 65534:65534 /data/prometheus && own 472:0 /data/grafana
                volumes:
                  - collector:/data/collector
                  - loki:/data/loki
                  - tempo:/data/tempo
                  - prometheus:/data/prometheus
                  - grafana:/data/grafana
              collector:
                image: {{Collector}}
                restart: unless-stopped
                mem_limit: 512m
                command: ["--config=/etc/otelcol-contrib/config.yaml"]
                environment:
                  OTLP_USERNAME: ${OTLP_USERNAME}
                  OTLP_PASSWORD: ${OTLP_PASSWORD}
                labels:
                  lucia.configs: "{{Hash(collector)}}"
                configs:
                  - source: collector
                    target: /etc/otelcol-contrib/config.yaml
                volumes:
                  - collector:/var/lib/otelcol
                ports:
                  - "{{grpcPort}}:4317"
                  - "{{httpPort}}:4318"
                depends_on:
                  init:
                    condition: service_completed_successfully
                healthcheck:
                  test: ["CMD", "/otelcol-contrib", "validate", "--config=/etc/otelcol-contrib/config.yaml"]
                  interval: 30s
                  timeout: 10s
                  retries: 3
              prometheus:
                image: {{Prometheus}}
                restart: unless-stopped
                command:
                  - --config.file=/etc/prometheus/prometheus.yaml
                  - --storage.tsdb.path=/prometheus
                  - --storage.tsdb.retention.time=30d
                  - --storage.tsdb.retention.size=40GB
                  - --web.enable-otlp-receiver
                labels:
                  lucia.configs: "{{Hash(prometheus)}}"
                configs:
                  - source: prometheus
                    target: /etc/prometheus/prometheus.yaml
                volumes:
                  - prometheus:/prometheus
                depends_on:
                  init:
                    condition: service_completed_successfully
              loki:
                image: {{Loki}}
                restart: unless-stopped
                command: ["-config.file=/etc/loki/config.yaml"]
                labels:
                  lucia.configs: "{{Hash(loki)}}"
                configs:
                  - source: loki
                    target: /etc/loki/config.yaml
                volumes:
                  - loki:/var/loki
                depends_on:
                  init:
                    condition: service_completed_successfully
              tempo:
                image: {{Tempo}}
                restart: unless-stopped
                command: ["-config.file=/etc/tempo/config.yaml"]
                labels:
                  lucia.configs: "{{Hash(tempo)}}"
                configs:
                  - source: tempo
                    target: /etc/tempo/config.yaml
                volumes:
                  - tempo:/var/tempo
                depends_on:
                  init:
                    condition: service_completed_successfully
              grafana:
                image: {{Grafana}}
                restart: unless-stopped
                environment:
                  GF_SECURITY_ADMIN_USER: admin
                  GF_SECURITY_ADMIN_PASSWORD: ${GRAFANA_ADMIN_PASSWORD}
                  GF_USERS_ALLOW_SIGN_UP: "false"
                  GF_ANALYTICS_REPORTING_ENABLED: "false"
                  GF_SERVER_ROOT_URL: {{rootUrl}}
                  GF_DASHBOARDS_DEFAULT_HOME_DASHBOARD_PATH: /etc/grafana/dashboards/lucia/hardware.json
                  # Lucia sets LUCIA_SSO_* once Grafana's Authentik client exists. /login?disableAutoLogin=true reaches the admin form.
                  GF_AUTH_GENERIC_OAUTH_ENABLED: ${LUCIA_SSO_ENABLED:-false}
                  GF_AUTH_OAUTH_AUTO_LOGIN: ${LUCIA_SSO_ENABLED:-false}
                  GF_AUTH_GENERIC_OAUTH_NAME: Lucia
                  GF_AUTH_GENERIC_OAUTH_CLIENT_ID: ${LUCIA_SSO_CLIENT_ID:-}
                  GF_AUTH_GENERIC_OAUTH_CLIENT_SECRET: ${SSO_CLIENT_SECRET}
                  GF_AUTH_GENERIC_OAUTH_SCOPES: openid profile email offline_access
                  GF_AUTH_GENERIC_OAUTH_AUTH_URL: ${LUCIA_SSO_ORIGIN:-}/application/o/authorize/
                  GF_AUTH_GENERIC_OAUTH_TOKEN_URL: ${LUCIA_SSO_ORIGIN:-}/application/o/token/
                  GF_AUTH_GENERIC_OAUTH_API_URL: ${LUCIA_SSO_ORIGIN:-}/application/o/userinfo/
                  GF_AUTH_GENERIC_OAUTH_USE_PKCE: "true"
                  GF_AUTH_GENERIC_OAUTH_LOGIN_ATTRIBUTE_PATH: preferred_username
                  # Grafana needs an email; directory accounts often have none.
                  GF_AUTH_GENERIC_OAUTH_EMAIL_ATTRIBUTE_PATH: "email || join('@', [preferred_username, 'lucia.invalid'])"
                  GF_AUTH_GENERIC_OAUTH_ROLE_ATTRIBUTE_PATH: "contains(groups, 'lucia-owners') && 'Admin' || 'Viewer'"
                  GF_AUTH_SIGNOUT_REDIRECT_URL: ${LUCIA_SSO_SIGNOUT:-}
                labels:
                  lucia.configs: "{{Hash(datasources, dashboards, hardware, inference)}}"
                configs:
                  - source: datasources
                    target: /etc/grafana/provisioning/datasources/lucia.yaml
                  - source: dashboards
                    target: /etc/grafana/provisioning/dashboards/lucia.yaml
                  - source: hardware
                    target: /etc/grafana/dashboards/lucia/hardware.json
                  - source: inference
                    target: /etc/grafana/dashboards/lucia/inference.json
                volumes:
                  - grafana:/var/lib/grafana
                ports:
                  - "{{grafanaPort}}:3000"
                depends_on:
                  - prometheus
                  - loki
                  - tempo
            configs:
              collector:
                content: |
            {{Content(collector)}}
              prometheus:
                content: |
            {{Content(prometheus)}}
              loki:
                content: |
            {{Content(loki)}}
              tempo:
                content: |
            {{Content(tempo)}}
              datasources:
                content: |
            {{Content(datasources)}}
              dashboards:
                content: |
            {{Content(dashboards)}}
              hardware:
                content: |
            {{Content(hardware)}}
              inference:
                content: |
            {{Content(inference)}}
            volumes:
              collector:
              prometheus:
              loki:
              tempo:
              grafana:

            """;
        var envText = $"OTLP_USERNAME={User}\n"
            + $"OTLP_PASSWORD={StackCatalog.Secret(env, "OTLP_PASSWORD")}\n"
            + $"GRAFANA_ADMIN_PASSWORD={StackCatalog.Secret(env, "GRAFANA_ADMIN_PASSWORD")}\n"
            + $"{StackStore.SsoSecret}={StackCatalog.Secret(env, StackStore.SsoSecret)}\n";
        return new(compose.ReplaceLineEndings("\n"), envText, [],
            [new(grafanaHost, int.Parse(grafanaPort, CultureInfo.InvariantCulture)),
             new(otlpHost, int.Parse(httpPort, CultureInfo.InvariantCulture), int.Parse(grpcPort, CultureInfo.InvariantCulture))]);
    }
}

/// <summary>
/// A MusicBrainz mirror: the website and its <c>/ws/2</c> API over a copy of MusicBrainz's database, with Solr search.
/// A new install imports the latest data dump once. With a MetaBrainz access token it replicates MusicBrainz's changes every
/// hour; the search indexes, which replication doesn't reach, are rebuilt weekly.
/// </summary>
internal sealed class MusicBrainzApp() : CatalogApp("musicbrainz", 1, "MusicBrainz",
    "Mirror MusicBrainz's music database and search, for Picard and other taggers.",
    "Any server with Docker ready, 16 GB of memory and about 350 GB of disk. The first import takes a few hours.",
    [],
    [
        new("web-host", "Web name", "text", "musicbrainz", "The mirror's web address is this name under your domain."),
        new("port", "Web port", "port", "5000", "The mirror also answers on http://<server>:<port>."),
        new("metabrainz-access-token", "MetaBrainz access token", "secret", Optional: true,
            Help: "From metabrainz.org/profile. With it, the mirror fetches MusicBrainz's changes every hour."),
    ])
{
    private const string Server = "metabrainz/musicbrainz-docker-musicbrainz:v-2026-09-21.0-build0@sha256:791a31e4a7933aa6c55ae5f59054daf2ecc870dd329eb85fbb87f87a8b60e716";
    private const string Db = "metabrainz/musicbrainz-docker-db:18-build0@sha256:15809586e1a1ebd89c328bb680ea164b0e6a1d0a2c3ef6db7934d88062e34fc8";
    private const string Solr = "metabrainz/mb-solr:4.1.1@sha256:83e59a49f465771006f86a6ce806198abee5109e9f32b4be3c2622c0bd22473b";
    private const string Sir = "metabrainz/sir:5.0.0-rc.3@sha256:ef2463e04676b542130b8db3e84ccce630d195ec8bf5e150d790994f54a5acd2";
    private const string Valkey = "valkey/valkey:9-alpine@sha256:48332870af354a799964c0012ae1194a0bf2bf894eb508f945810596dc2d8d11";
    internal const string TokenKey = "METABRAINZ_ACCESS_TOKEN";

    public override string BackupMode => "stop";
    public override string[] BackupExclude => ["volumes/dbdump", "volumes/solrdata", "volumes/indexer"];

    internal override CatalogOutput Render(IReadOnlyDictionary<string, string> settings, ManagedNodeFacts node, IReadOnlyDictionary<string, string> env)
    {
        var (host, port) = (settings["web-host"].ToLowerInvariant(), settings["port"]);
        var replicating = env.GetValueOrDefault(TokenKey) is { Length: > 0 };
        // sir expands ${...} itself.
        var indexer = """
            [database]
            dbname = musicbrainz_db
            host = db
            password = $${POSTGRES_PASSWORD}
            port = 5432
            user = musicbrainz

            [solr]
            uri = http://search:8983/solr
            batch_size = 200

            [sir]
            import_threads = 8
            index_limit = 200000
            live_index_batch_size = 100
            max_retries = 4
            poll_interval = 5
            process_delay = 15
            query_batch_size = 5000
            wscompat = on

            [sentry]
            dsn =
            """;
        var cron = """
            SHELL=/bin/bash
            BASH_ENV=/noninteractive.bash_env
            0 * * * * /usr/local/bin/replication.sh
            """;
        static string Content(string text) => string.Join("\n", text.ReplaceLineEndings("\n").Split('\n').Select(line => "      " + line));
        static string Hash(params string[] texts) =>
            Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\0', texts))))[..16];
        var replication = !replicating ? "" : $$"""
                labels:
                  lucia.configs: "{{Hash(cron)}}"
                configs:
                  - source: cron
                    target: /crons.conf
                secrets:
                  - metabrainz_access_token

            """;
        var compose = $$"""
            # Installed from Lucia's catalog (musicbrainz, version {{Version}}). Lucia rewrites this file when the catalog
            # updates the app. Convert the app to a custom app to edit it by hand.
            services:
              db:
                image: {{Db}}
                restart: unless-stopped
                command: postgres -c shared_buffers=2048MB
                shm_size: 2gb
                environment:
                  POSTGRES_USER: musicbrainz
                  POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
                volumes:
                  - pgdata:/var/lib/postgresql
              valkey:
                image: {{Valkey}}
                restart: unless-stopped
              search:
                image: {{Solr}}
                restart: unless-stopped
                # Solr's start script runs lsof, which takes minutes per call under Docker's default open-file limit.
                ulimits:
                  nofile: 65536
                environment:
                  SOLR_HEAP: 2g
                  LOG4J_FORMAT_MSG_NO_LOOKUPS: "true"
                volumes:
                  - solrdata:/var/solr
              # Imports the latest data dump when the database doesn't exist yet, marks it ready, then idles so the app reads as
              # running. A marker left by an interrupted import means the database is partial, so it starts over. A failure
              # turns the service unhealthy until the app is restarted.
              import:
                image: {{Server}}
                restart: unless-stopped
                init: true
                environment:
                  POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
                command:
                  - bash
                  - -c
                  - |
                    cd /media/dbdump && rm -f .failed
                    fail() { echo "$$1 Restart the app to try again."; touch .failed; exec sleep infinity; }
                    dockerize -wait tcp://db:5432 -timeout 600s true || fail "The database didn't start."
                    carton exec -- /musicbrainz-server/script/database_exists MAINTENANCE && found=0 || found=$$?
                    if [ "$$found" = 0 ] && [ ! -e .importing ]; then touch .ready; exec sleep infinity; fi
                    if [ "$$found" = 0 ]; then
                      PGPASSWORD="$$POSTGRES_PASSWORD" psql -h db -U musicbrainz -d postgres -c 'DROP DATABASE musicbrainz_db WITH (FORCE)' \
                        || fail "The partial import couldn't be removed."
                    elif [ "$$found" != 1 ]; then fail "The database couldn't be checked."; fi
                    rm -f .ready && touch .importing
                    createdb.sh -fetch || fail "The import failed."
                    find /media/dbdump -mindepth 1 -maxdepth 1 ! -name '.*' -exec rm -rf {} +
                    rm .importing && touch .ready
                    exec sleep infinity
                healthcheck:
                  test: ["CMD", "bash", "-c", "[ ! -e /media/dbdump/.failed ]"]
                  interval: 1m
                volumes:
                  - dbdump:/media/dbdump
              musicbrainz:
                image: {{Server}}
                restart: unless-stopped
                # Links use the web address once the domain is active, and the server's port until then.
                entrypoint:
                  - bash
                  - -c
                  - |
                    until [ -e /media/dbdump/.ready ]; do echo "Waiting for the database import."; sleep 60; done
                    if [ -n "$$LUCIA_URL" ]; then export MUSICBRAINZ_WEB_SERVER_HOST="$${LUCIA_URL#https://}" MUSICBRAINZ_WEB_SERVER_PORT=443; fi
                    exec docker-entrypoint.sh "$$@"
                  - musicbrainz
                command: ["start.sh"]
                environment:
                  POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
                  MUSICBRAINZ_SERVER_PROCESSES: "10"
                  MUSICBRAINZ_USE_PROXY: "1"
                  MUSICBRAINZ_WEB_SERVER_HOST: {{node.Hostname}}
                  MUSICBRAINZ_WEB_SERVER_PORT: "{{port}}"
                  LUCIA_URL: ${{{StackStore.RouteVariable(host)}}:-}
            {{replication}}    volumes:
                  - dbdump:/media/dbdump:ro
                ports:
                  - "{{port}}:5000"
                depends_on:
                  - db
                  - valkey
                  - search
              # Replication doesn't reach the search indexes, so they're rebuilt weekly. Search keeps answering meanwhile.
              indexer:
                image: {{Sir}}
                restart: unless-stopped
                environment:
                  POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
                command:
                  - bash
                  - -c
                  - |
                    until [ -e /media/dbdump/.ready ]; do sleep 60; done
                    while :; do
                      if [ -z "$$(find /state/indexed -mtime -7 2>/dev/null)" ]; then python -m sir reindex && touch /state/indexed; fi
                      sleep 300
                    done
                labels:
                  lucia.configs: "{{Hash(indexer)}}"
                configs:
                  - source: indexer
                    target: /code/config.ini
                volumes:
                  - dbdump:/media/dbdump:ro
                  - indexer:/state
                depends_on:
                  - db
                  - search
            configs:
              indexer:
                content: |
            {{Content(indexer)}}
            {{(replicating ? $"  cron:\n    content: |\n{Content(cron)}\nsecrets:\n  metabrainz_access_token:\n    environment: {TokenKey}\n" : "")}}volumes:
              pgdata:
              solrdata:
              dbdump:
              indexer:

            """;
        var envText = $"POSTGRES_PASSWORD={StackCatalog.Secret(env, "POSTGRES_PASSWORD")}\n"
            + (replicating ? $"{TokenKey}={env[TokenKey]}\n" : "");
        return new(compose.ReplaceLineEndings("\n"), envText, [], [new(host, int.Parse(port, CultureInfo.InvariantCulture))]);
    }
}

/// <summary>
/// Immich, a photo and video library with face and smart search. Photos go to a folder on a NAS share, or to the server when
/// none is given. Machine learning runs on the CPU or on an NVIDIA GPU.
/// </summary>
internal sealed partial class ImmichApp() : CatalogApp("immich", 1, "Immich",
    "Back up and browse your photos and videos, with face and smart search.",
    "Any server with Docker ready and 6 GB of memory. For GPU machine learning, an NVIDIA GPU.",
    [],
    [
        new("web-host", "Web name", "text", "photos", "Immich's web address is this name under your domain."),
        new("port", "Web port", "port", "2283", "Immich also answers on http://<server>:<port>, which its mobile app can use."),
        new("library", "Photo library", "text", Optional: true,
            Help: "A folder on a NAS share, such as /mnt/lucia/nas/unas/Media/Photos/library. Leave it blank to keep photos on this server."),
        new("machine-learning", "Machine learning", "choice", "cpu", Options:
        [
            new("cpu", "CPU", "Runs on any server."),
            new("cuda", "NVIDIA GPU", "Faster face and smart search. The server needs an NVIDIA GPU."),
        ]),
    ])
{
    private const string Server = "ghcr.io/immich-app/immich-server:v3.2.4@sha256:d317916b28090c33eb36b308464ea391f8b7df1d850fcfea227a39ec879718c2";
    private const string Ml = "ghcr.io/immich-app/immich-machine-learning:v3.2.4@sha256:e16c2f166a8174901959fdf85e2e4c7bd1ebc4b37e0b6655de97c41408a260c4";
    private const string MlCuda = "ghcr.io/immich-app/immich-machine-learning:v3.2.4-cuda@sha256:b9fdebfe7f07ff71f77e9d67d509d83c5e055da486a07c12080669c82a80c65e";
    private const string Db = "ghcr.io/immich-app/postgres:14-vectorchord0.4.3-pgvectors0.2.0@sha256:bcf63357191b76a916ae5eb93464d65c07511da41e3bf7a8416db519b40b1c23";
    private const string Valkey = "docker.io/valkey/valkey:9@sha256:70739f85ad2ee01a726a965584a0f94895f01b0c60b3cc8b0aeef11eaa6888cf";

    // The database is only consistent while it's stopped; models download again.
    public override string BackupMode => "stop";
    public override string[] BackupExclude => ["volumes/model-cache"];

    public override AppSso Sso(IReadOnlyDictionary<string, string> settings) =>
        new("Immich", settings["web-host"].ToLowerInvariant(), "/auth/login", "/user-settings", "/api/oauth/mobile-redirect");

    internal override CatalogOutput Render(IReadOnlyDictionary<string, string> settings, ManagedNodeFacts node, IReadOnlyDictionary<string, string> env)
    {
        var (host, port, library) = (settings["web-host"].ToLowerInvariant(), settings["port"], settings["library"].TrimEnd('/'));
        var share = NasFolder().Match(library);
        if (library.Length > 0 && !share.Success)
            throw new HardwareOnboardingException(400, "invalid_setting", "Photo library must be a folder under a NAS share, such as /mnt/lucia/nas/unas/Media/Photos.");
        var cuda = settings["machine-learning"] == "cuda";
        var devices = !cuda ? "" : """
                deploy:
                  resources:
                    reservations:
                      devices:
                        - driver: nvidia
                          count: 1
                          capabilities: [gpu]

            """;
        var compose = $$"""
            # Installed from Lucia's catalog (immich, version {{Version}}). Lucia rewrites this file when you change the
            # app's settings. Convert the app to a custom app to edit it by hand.
            services:
              server:
                image: {{Server}}
                restart: unless-stopped
                environment:
                  DB_HOSTNAME: database
                  DB_USERNAME: postgres
                  DB_PASSWORD: ${DB_PASSWORD}
                  DB_DATABASE_NAME: immich
                  REDIS_HOSTNAME: redis
                  IMMICH_MACHINE_LEARNING_URL: http://machine-learning:3003
                volumes:
                  - {{(library.Length > 0 ? library : "library")}}:/data
                  - /etc/localtime:/etc/localtime:ro
                ports:
                  - "{{port}}:2283"
                depends_on:
                  - redis
                  - database
              machine-learning:
                image: {{(cuda ? MlCuda : Ml)}}
                restart: unless-stopped
                volumes:
                  - model-cache:/cache
            {{devices}}  redis:
                image: {{Valkey}}
                restart: unless-stopped
                healthcheck:
                  test: redis-cli ping | grep -q PONG || exit 1
              database:
                image: {{Db}}
                restart: unless-stopped
                environment:
                  POSTGRES_USER: postgres
                  POSTGRES_PASSWORD: ${DB_PASSWORD}
                  POSTGRES_DB: immich
                  POSTGRES_INITDB_ARGS: --data-checksums
                shm_size: 128mb
                volumes:
                  - pgdata:/var/lib/postgresql/data
              # Once Lucia registers Immich's Authentik client, writes it into Immich's sign-in settings (read on every
              # sign-in), then waits. The phone app signs in through the web address's mobile redirect.
              sso:
                image: {{Db}}
                restart: unless-stopped
                init: true
                environment:
                  PGHOST: database
                  PGUSER: postgres
                  PGPASSWORD: ${DB_PASSWORD}
                  PGDATABASE: immich
                  ENABLED: ${LUCIA_SSO_ENABLED:-false}
                  CLIENT_ID: ${LUCIA_SSO_CLIENT_ID:-}
                  CLIENT_SECRET: ${SSO_CLIENT_SECRET}
                  ORIGIN: ${LUCIA_SSO_ORIGIN:-}
                  APP_URL: ${{{StackStore.RouteVariable(host)}}:-}
                entrypoint: ["/bin/sh", "-c"]
                command:
                  - |
                    set -e
                    rm -f /tmp/done
                    until [ "$$(psql -Atc "select to_regclass('system_metadata') is not null" 2>/dev/null)" = t ]; do sleep 5; done
                    if [ "$$ENABLED" = true ]; then
                      psql -v ON_ERROR_STOP=1 -q -v id="$$CLIENT_ID" -v secret="$$CLIENT_SECRET" \
                        -v issuer="$$ORIGIN/application/o/$$CLIENT_ID/" -v account="$$ORIGIN/if/user/" \
                        -v mobile="$$APP_URL/api/oauth/mobile-redirect" <<'SQL'
                    insert into system_metadata (key, value) values ('system-config', '{}') on conflict (key) do nothing;
                    update system_metadata set value = jsonb_set(value, '{oauth}', coalesce(value -> 'oauth', '{}') || jsonb_build_object(
                      'enabled', true, 'autoLaunch', true, 'issuerUrl', :'issuer', 'clientId', :'id', 'clientSecret', :'secret',
                      'accountManagementUrl', :'account', 'mobileOverrideEnabled', true, 'mobileRedirectUri', :'mobile'))
                      where key = 'system-config';
                    SQL
                    fi
                    touch /tmp/done
                    exec sleep infinity
                healthcheck:
                  test: ["CMD", "test", "-e", "/tmp/done"]
                  interval: 10s
                  start_period: 10m
                depends_on:
                  - database
            volumes:
              pgdata:
              model-cache:
            {{(library.Length > 0 ? "" : "  library:\n")}}
            """;
        // Immich's database password allows only letters and digits.
        var password = env.GetValueOrDefault("DB_PASSWORD") is { Length: >= 32 } kept && kept.All(char.IsAsciiLetterOrDigit) ? kept
            : Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        string[] require = [.. share.Success ? [$"nas={share.Groups["nas"].Value}/{share.Groups["share"].Value}"] : Array.Empty<string>(),
            .. cuda ? ["gpu.vendor=nvidia"] : Array.Empty<string>()];
        return new(compose.ReplaceLineEndings("\n"), $"DB_PASSWORD={password}\n{StackStore.SsoSecret}={StackCatalog.Secret(env, StackStore.SsoSecret)}\n",
            require, [new(host, int.Parse(port, CultureInfo.InvariantCulture))]);
    }

    [GeneratedRegex(@"\A/mnt/lucia/nas/(?<nas>[A-Za-z0-9][A-Za-z0-9._-]{0,63})/(?<share>[A-Za-z0-9][A-Za-z0-9._-]{0,63})(?:/[A-Za-z0-9][A-Za-z0-9._-]{0,127})*\z")]
    private static partial Regex NasFolder();
}
/// <summary>
/// LiteLLM, one OpenAI-compatible gateway in front of every model endpoint, with virtual keys, budgets and usage. Models
/// are added in its admin UI and kept in its database. Owners sign in to the UI through Lucia's Authentik as proxy
/// admins; while an Observability app runs, it sends its traces and metrics there.
/// </summary>
internal sealed class LiteLlmApp() : CatalogApp("litellm", 1, "LiteLLM",
    "One OpenAI-compatible gateway for all your models, with keys, budgets and usage tracking.",
    "Any server with Docker ready and 2 GB of memory.",
    [],
    [
        new("web-host", "Web name", "text", "litellm", "LiteLLM's web address is this name under your domain."),
        new("port", "Web port", "port", "4000", "LiteLLM also answers on http://<server>:<port>."),
    ])
{
    private const string Image = "docker.litellm.ai/berriai/litellm-database:v1.102.1@sha256:c38fe5eff11874941a21f5630ceb842f640dee7d5d5cde440b60ed0a71798f33";
    private const string Postgres = "postgres:16.15@sha256:1a6ab3f5345eb6dbe04a1349529caabdb0ab09293a09590fad07b2246bfa4b54";

    public override string BackupMode => "stop";
    public override bool Telemetry => true;

    public override AppSso Sso(IReadOnlyDictionary<string, string> settings) =>
        new("LiteLLM", settings["web-host"].ToLowerInvariant(), "/sso/callback");

    internal override CatalogOutput Render(IReadOnlyDictionary<string, string> settings, ManagedNodeFacts node, IReadOnlyDictionary<string, string> env)
    {
        var (host, port) = (settings["web-host"].ToLowerInvariant(), settings["port"]);
        var baseUrl = "${" + StackStore.RouteVariable(host) + $":-http://{node.Hostname}:{port}}}";
        var compose = $$"""
            # Installed from Lucia's catalog (litellm, version {{Version}}). Lucia rewrites this file when you change the
            # app's settings. Convert the app to a custom app to edit it by hand.
            services:
              litellm:
                image: {{Image}}
                restart: unless-stopped
                environment:
                  DATABASE_URL: postgresql://litellm:${POSTGRES_PASSWORD}@db:5432/litellm
                  LITELLM_MASTER_KEY: ${LITELLM_MASTER_KEY}
                  # Encrypts the provider keys it stores; it must never change.
                  LITELLM_SALT_KEY: ${LITELLM_SALT_KEY}
                  STORE_MODEL_IN_DB: "True"
                  PROXY_BASE_URL: {{baseUrl}}
                  # Lucia sets LUCIA_SSO_* once LiteLLM's Authentik client exists; until then the entrypoint drops the client
                  # so the UI keeps its admin form (admin and the master key). Owners sign in as proxy admins.
                  SSO: ${LUCIA_SSO_ENABLED:-false}
                  GENERIC_CLIENT_ID: ${LUCIA_SSO_CLIENT_ID:-}
                  GENERIC_CLIENT_SECRET: ${SSO_CLIENT_SECRET}
                  GENERIC_AUTHORIZATION_ENDPOINT: ${LUCIA_SSO_ORIGIN:-}/application/o/authorize/
                  GENERIC_TOKEN_ENDPOINT: ${LUCIA_SSO_ORIGIN:-}/application/o/token/
                  GENERIC_USERINFO_ENDPOINT: ${LUCIA_SSO_ORIGIN:-}/application/o/userinfo/
                  GENERIC_SCOPE: openid email profile
                  GENERIC_ROLE_MAPPINGS_GROUP_CLAIM: groups
                  GENERIC_ROLE_MAPPINGS_ROLES: "{'proxy_admin': ['lucia-owners']}"
                  GENERIC_ROLE_MAPPINGS_DEFAULT_ROLE: internal_user_viewer
                  # Lucia sets LUCIA_OTLP_* while an Observability app runs; the config turns the exporter on only then.
                  LITELLM_OTEL_INTEGRATION_ENABLE_METRICS: "true"
                  OTEL_EXPORTER: otlp_http
                  OTEL_ENDPOINT: ${LUCIA_OTLP_ENDPOINT:-}/v1/traces
                  OTEL_HEADERS: Authorization=${LUCIA_OTLP_AUTHORIZATION:-}
                  OTEL_SERVICE_NAME: litellm
                entrypoint:
                  - /bin/sh
                  - -c
                  - '[ "$$SSO" = true ] || unset GENERIC_CLIENT_ID GENERIC_CLIENT_SECRET; exec docker/prod_entrypoint.sh "$$@"'
                  - litellm
                command: ["--config", "/app/config.yaml", "--port", "4000"]
                configs:
                  - source: config
                    target: /app/config.yaml
                ports:
                  - "{{port}}:4000"
                healthcheck:
                  test: ["CMD", "python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:4000/health/liveliness')"]
                  interval: 30s
                  start_period: 5m
                depends_on:
                  db:
                    condition: service_healthy
              db:
                image: {{Postgres}}
                restart: unless-stopped
                environment:
                  POSTGRES_USER: litellm
                  POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
                  POSTGRES_DB: litellm
                healthcheck:
                  test: ["CMD-SHELL", "pg_isready -U litellm -d litellm"]
                  interval: 5s
                  retries: 10
                volumes:
                  - pgdata:/var/lib/postgresql/data
            configs:
              config:
                content: |
                  general_settings:
                    background_health_checks: true
                    health_check_interval: 300
                    health_check_details: false
                    disable_env_credential_login: ${LUCIA_SSO_ENABLED:-false}
                  litellm_settings:
                    callbacks: [${LUCIA_OTLP_ENDPOINT:+otel}]
            volumes:
              pgdata:

            """;
        var master = env.GetValueOrDefault("LITELLM_MASTER_KEY") is { Length: >= 35 } kept && kept.StartsWith("sk-", StringComparison.Ordinal) ? kept
            : "sk-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var envText = $"POSTGRES_PASSWORD={StackCatalog.Secret(env, "POSTGRES_PASSWORD")}\n"
            + $"LITELLM_MASTER_KEY={master}\n"
            + $"LITELLM_SALT_KEY={StackCatalog.Secret(env, "LITELLM_SALT_KEY")}\n"
            + $"{StackStore.SsoSecret}={StackCatalog.Secret(env, StackStore.SsoSecret)}\n";
        return new(compose.ReplaceLineEndings("\n"), envText, [], [new(host, int.Parse(port, CultureInfo.InvariantCulture))]);
    }
}
