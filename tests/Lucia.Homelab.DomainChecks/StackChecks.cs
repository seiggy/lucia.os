using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Stacks;

internal static class StackChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        void Rejects(Action action, string message)
        {
            try { action(); }
            catch (HardwareOnboardingException error) when (error.StatusCode == 400) { check(true, message); return; }
            throw new InvalidOperationException(message);
        }

        foreach (var name in new[] { "plex", "media-automation", "a", "a1" }) StackStore.ValidateName(name);
        check(true, "Valid stack names were rejected.");
        foreach (var name in new[] { "", "Plex", "1plex", "plex-", "plex_x", "../etc", new string('a', 41), "catalog", "install", "new" })
            Rejects(() => StackStore.ValidateName(name), $"The stack name '{name}' was accepted.");

        check(StackStore.Normalize("services:\r\n  web:\r\n\timage: x\r\n", 1024, "compose", true) == "services:\n  web:\n\timage: x\n",
            "Compose line endings weren't normalised.");
        Rejects(() => StackStore.Normalize("services:\u0000", 1024, "compose", true), "Control characters were accepted.");
        Rejects(() => StackStore.Normalize(new string('x', 2048), 1024, "compose", true), "An oversized compose file was accepted.");
        Rejects(() => StackStore.Normalize("  \n", 1024, "compose", true), "An empty compose file was accepted.");
        check(StackStore.Normalize(null, 1024, "environment", false) == "", "A missing environment file was rejected.");

        StackStore.ValidateEnv("# comment\nTZ=America/Chicago\n\nPUID=1000\nEMPTY=\n");
        check(true, "A valid environment file was rejected.");
        Rejects(() => StackStore.ValidateEnv("TZ America/Chicago"), "An environment line without '=' was accepted.");
        Rejects(() => StackStore.ValidateEnv("1TZ=x"), "An environment name starting with a digit was accepted.");

        var id = new string('a', 64);
        var report = new NodeStackReport(
            [new("plex", "Running", 3, null, [new("plex", "running", "healthy", "plexinc/pms-docker", null)])],
            [new(id, "lucia-plex-plex-1", "plexinc/pms-docker", "running", "Up 2 hours (healthy)", "lucia-plex", "plex", "32400/tcp")],
            [new("tcp", "0.0.0.0", 32400), new("udp", "::", 1900)]);
        StackStore.ValidateReport(report);
        check(true, "A valid stack report was rejected.");
        Rejects(() => StackStore.ValidateReport(report with { Stacks = [report.Stacks[0] with { State = "Exploded" }] }),
            "An unknown stack state was accepted.");
        Rejects(() => StackStore.ValidateReport(report with { Containers = [report.Containers[0] with { Id = "not-an-id" }] }),
            "A malformed container id was accepted.");
        Rejects(() => StackStore.ValidateReport(report with { Listeners = [new("sctp", "0.0.0.0", 1)] }),
            "An unknown listener protocol was accepted.");
        Rejects(() => StackStore.ValidateReport(report with { Listeners = [new("tcp", "0.0.0.0", 70000)] }),
            "An out-of-range port was accepted.");

        var snapshot = new string('c', 64);
        StackStore.ValidateReport(report with
        {
            Stacks = [report.Stacks[0] with { Backup = new(Guid.NewGuid(), "Succeeded", DateTimeOffset.UtcNow, Snapshot: snapshot, Added: 10, Total: 20) }],
            Snapshots = [new(snapshot, "plex", "lucialab01", DateTimeOffset.UtcNow, 20)],
        });
        check(true, "A valid backup report was rejected.");
        Rejects(() => StackStore.ValidateReport(report with { Stacks = [report.Stacks[0] with { Backup = new(Guid.NewGuid(), "Exploded", DateTimeOffset.UtcNow) }] }),
            "An unknown backup state was accepted.");
        Rejects(() => StackStore.ValidateReport(report with { Snapshots = [new("abc", "plex", "lucialab01", DateTimeOffset.UtcNow)] }),
            "A malformed snapshot id was accepted.");
        Rejects(() => StackStore.ValidateReport(report with { Snapshots = [new(snapshot, "../etc", "lucialab01", DateTimeOffset.UtcNow)] }),
            "A snapshot of an invalid stack name was accepted.");
        Rejects(() => StackStore.ValidBackup(new(true, "pause")), "An unknown backup mode was accepted.");

        check(StackStore.ValidAddress(" 192.168.1.230 ") == "192.168.1.230" && StackStore.ValidAddress("") is null,
            "A valid app address was rejected.");
        foreach (var bad in new[] { "8.8.8.8", "192.168.1.0", "192.168.1.255", "192.168.01.5", "192.168.1", "::1", "fd00::53", "10.0.0.5/24" })
            Rejects(() => StackStore.ValidAddress(bad), $"The app address '{bad}' was accepted.");
        Rejects(() => StackStore.ValidateEnv("LUCIA_ADDRESS=192.168.1.9"), "An environment that sets Lucia's address was accepted.");
        var manifest = new StackManifest(1, new(), Address: "192.168.1.230");
        check(StackStore.NodeEnv("TZ=UTC\n", manifest) == "TZ=UTC\nLUCIA_ADDRESS=192.168.1.230\n"
            && StackStore.NodeEnv("", manifest) == "LUCIA_ADDRESS=192.168.1.230\n" && StackStore.NodeEnv("TZ=UTC\n", manifest with { Address = null }) == "TZ=UTC\n",
            "The node must get the app's address in its environment.");
        var adguard = StackCatalog.Find("adguard");
        var adguardCompose = adguard.Render(StackCatalog.Settings(adguard, null), null!, new Dictionary<string, string>()).Compose;
        check(adguard.UsesAddress && !adguard.ServerBound && adguardCompose.Contains("      - \"${LUCIA_ADDRESS}:53:53/udp\"\n", StringComparison.Ordinal)
            && adguardCompose.Contains("      - \"${LUCIA_ADDRESS}:853:853/tcp\"\n", StringComparison.Ordinal)
            && !System.Text.RegularExpressions.Regex.IsMatch(adguardCompose, @"- ""\d"), "AdGuard must publish every port on its own address only.");
        StackStore.ValidateReport(report with { Addresses = [new("192.168.1.230", "Held"), new("192.168.1.231", "InUse", "aa:bb:cc:dd:ee:ff")] });
        check(true, "A valid address report was rejected.");
        Rejects(() => StackStore.ValidateReport(report with { Addresses = [new("192.168.1.230", "Stolen")] }), "An unknown address state was accepted.");
        Rejects(() => StackStore.ValidateReport(report with { Addresses = [new("eth0; rm", "Held")] }), "A malformed reported address was accepted.");
        check(StackStore.ValidBackup(new(false, "stop")) == new StackBackup(false, "stop"), "A valid backup setting was rejected.");

        var chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        check(StackStore.NextRun(DateTimeOffset.Parse("2025-06-01T12:00:00Z"), chicago) == DateTimeOffset.Parse("2025-06-02T08:00:00Z")
            && StackStore.NextRun(DateTimeOffset.Parse("2025-06-02T07:59:00Z"), chicago) == DateTimeOffset.Parse("2025-06-02T08:00:00Z")
            && StackStore.NextRun(DateTimeOffset.Parse("2025-06-02T08:00:00Z"), chicago) == DateTimeOffset.Parse("2025-06-03T08:00:00Z"),
            "Backups must run at the next 03:00 in the owner's zone, never at the instant the last one was asked for.");
        check(StackStore.NextRun(DateTimeOffset.Parse("2025-03-08T10:00:00Z"), chicago) == DateTimeOffset.Parse("2025-03-09T08:00:00Z")
            && StackStore.NextRun(DateTimeOffset.Parse("2025-11-01T09:00:00Z"), chicago) == DateTimeOffset.Parse("2025-11-02T09:00:00Z"),
            "Backups must follow 03:00 local time across daylight-saving changes.");
        // A zone whose clocks jump from 03:00 to 04:00 has no 03:00 that day; the backup runs an hour later instead of skipping it.
        var jump = TimeZoneInfo.CreateCustomTimeZone("Jump", TimeSpan.Zero, "Jump", "Jump", "Jump Summer",
            [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2030, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 3, 30),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 26))]);
        check(StackStore.NextRun(DateTimeOffset.Parse("2025-03-29T12:00:00Z"), jump) == DateTimeOffset.Parse("2025-03-30T03:00:00Z"),
            "A day without a 03:00 must still get its backup.");

        var requests = new NodeRequests();
        var node = Guid.NewGuid();
        var logs = requests.Logs(node, "lucia-plex-plex-1", 50, CancellationToken.None);
        var pending = await requests.Wait(node, CancellationToken.None);
        check(pending is [{ Kind: "logs", Container: "lucia-plex-plex-1", Tail: 50 }], "The node didn't receive the logs request.");
        requests.Complete(Guid.NewGuid(), new(pending[0].RequestId, true, "spoofed", null));
        requests.Complete(node, new(pending[0].RequestId, true, "line one", null));
        check((await logs).Output == "line one", "Another node answered a request, or the answer was lost.");
        try
        {
            await requests.Logs(node, "bad name; rm -rf /", 50, CancellationToken.None);
            throw new InvalidOperationException("A container name with shell characters was accepted.");
        }
        catch (HardwareOnboardingException error) when (error.StatusCode == 400) { check(true, "Unsafe container name rejected."); }

        foreach (var bad in new[] { "gpus", "gpu.vram>=lots", "gpu.vendor", "memory=32G", "gpu.compute>=x", "gpu;rm" })
            Rejects(() => StackRequirements.Normalize([bad]), $"The requirement '{bad}' was accepted.");
        Rejects(() => StackRequirements.Normalize(Enumerable.Range(0, 17).Select(i => $"memory>={i}G").ToArray()),
            "Too many requirements were accepted.");
        check(StackRequirements.Normalize([" gpu ", "", "gpu"]) is ["gpu"], "Requirements weren't trimmed and de-duplicated.");
        var gib = 1L << 30;
        var host = new Lucia.Homelab.Server.Nodes.NodeHeartbeat(Guid.NewGuid(), "spark", "Debian 13", 1, null, 64 * gib, 32 * gib, null, null,
            new("Ready", "28", "2.30", true, [new("nvidia", "NVIDIA GeForce RTX 4090", (long)(23.6 * gib), "8.9"),
                new("nvidia", "NVIDIA T400", 2 * gib, "7.5")]));
        check(StackRequirements.Unmet(["gpu", "gpu.vendor=nvidia", "gpu.vram>=24G", "gpu.compute>=8.6", "memory>=64G"], host) is null,
            "A node meeting every requirement was ruled out (a 24 GB card reports a little under 24 GiB).");
        check(StackRequirements.Unmet(["gpu.model~T400", "gpu.vram>=8G"], host) is not null,
            "GPU requirements were met by two different cards.");
        check(StackRequirements.Unmet(["memory<=32G"], host) == "memory<=32G", "A memory maximum was ignored.");
        check(StackRequirements.Unmet(["gpu"], host with { Runtime = host.Runtime! with { GpuContainers = false } }) == "gpu",
            "A GPU that containers can't use met a gpu requirement.");

        foreach (var bad in new[] { "nas", "nas=unas", "nas=Unas/Media", "nas=unas/../x", "nas=unas/Media/x", "nas~unas/Media" })
            Rejects(() => StackRequirements.Normalize([bad]), $"The requirement '{bad}' was accepted.");
        check(StackRequirements.Unmet(["nas=unas/Media"], host, null, new HashSet<string> { "unas/Media" }) is null
            && StackRequirements.Unmet(["nas=unas/Media"], host, null, new HashSet<string> { "unas/Models" }) == "nas=unas/Media"
            && StackRequirements.Unmet(["nas=unas/Media"], host) == "nas=unas/Media", "NAS requirements must follow the node's mounts.");
        (string, string)[] shares = [("unas", "Media"), ("unas", "Models")];
        check(StackStore.NasRequirements("volumes:\n  - /mnt/lucia/nas/unas/Media/Movies:/movies:ro\n  - \"/mnt/lucia/nas/unas/Media:/m\"\n", shares)
            is ["nas=unas/Media"] && StackStore.NasRequirements("services: {}", shares) is [],
            "Compose NAS paths must become requirements.");
        Rejects(() => StackStore.NasRequirements("- /mnt/lucia/nas/unas/Photos:/p", shares), "A compose path to an unknown share was accepted.");
        Rejects(() => StackStore.ValidateNasId("Bad Name"), "An invalid NAS name was accepted.");
        StackStore.ValidateReport(report with { Mounts = [new("unas", "Media", "Mounted"), new("unas", "Models", "Failed", "access denied")] });
        check(true, "A valid mount report was rejected.");
        Rejects(() => StackStore.ValidateReport(report with { Mounts = [new("unas", "Media", "Exploded")] }), "An unknown mount state was accepted.");
        check(StackRequirements.Unmet(["memory>=1G"], null) == "memory>=1G", "A node that never reported met a requirement.");

        foreach (var bad in new[] { "cuda", "cuda=11", "cuda>=12", "cuda=12.4" })
            Rejects(() => StackRequirements.Normalize([bad]), $"The requirement '{bad}' was accepted.");
        var uuid = "GPU-cbeac6c4-3134-d34a-9fb5-fc0a0daf1981";
        var cudaRuntime = host.Runtime! with { CudaVersion = "13.3", Gpus = [host.Runtime.Gpus[0] with { Uuid = uuid }] };
        var cudaHost = host with { Runtime = cudaRuntime };
        var pinned12 = new NodeGpuSettings(12);
        check(StackRequirements.Unmet(["cuda=12"], cudaHost, pinned12) is null, "A server pinned to CUDA 12 didn't meet cuda=12.");
        check(StackRequirements.Unmet(["cuda=13"], cudaHost, pinned12) == "cuda=13", "A CUDA 12 server met cuda=13.");
        check(StackRequirements.Unmet(["cuda=12"], cudaHost) == "cuda=12", "A server without a pinned line met a cuda requirement.");
        check(StackRequirements.Unmet(["cuda=13"], cudaHost with { Runtime = cudaRuntime with { CudaVersion = "12.8" } },
            new NodeGpuSettings(13)) == "cuda=13", "A pinned line the driver no longer supports still met its requirement.");
        check(CudaLines.Unsupported(13, cudaRuntime) is null && CudaLines.Unsupported(12, cudaRuntime) is null,
            "A current driver and GPU were ruled out.");
        check(CudaLines.Unsupported(12, cudaRuntime with { CudaVersion = null }) is not null
            && CudaLines.Unsupported(13, cudaRuntime with { Gpus = [cudaRuntime.Gpus[0] with { ComputeCapability = "6.1" }] }) is { } pascal
            && pascal.Contains("7.5") && CudaLines.Unsupported(12, cudaRuntime with { Gpus = [] }) is not null,
            "CUDA line support ignored the driver's CUDA version, a GPU's compute capability, or a server without GPUs.");
        foreach (var (bad, why, runtime) in new (SaveNodeGpuRequest, string, NodeRuntime)[] {
            (new(11), "on an unknown line", cudaRuntime),
            (new(13), "on a Pascal GPU", cudaRuntime with { Gpus = [cudaRuntime.Gpus[0] with { ComputeCapability = "6.1" }] }) })
        {
            try { CudaLines.Validate(bad, runtime); throw new InvalidOperationException($"A CUDA line {why} was accepted."); }
            catch (HardwareOnboardingException error) when (error.StatusCode is 400 or 409) { check(true, why); }
        }

        var localAi = StackCatalog.Find("local-ai");
        var gpuNode = new ManagedNodeFacts(Guid.NewGuid(), "lucialab01", true, cudaHost, new(13));
        var settings = StackCatalog.Settings(localAi, new() { ["gpus"] = uuid });
        check(settings["port"] == "8080", "Local AI's default port wasn't filled in.");
        Rejects(() => StackCatalog.Settings(localAi, new() { ["gpus"] = uuid, ["prot"] = "1" }), "An unknown catalog setting was accepted.");
        Rejects(() => StackCatalog.Settings(localAi, new() { ["gpus"] = uuid, ["port"] = "80a" }), "A malformed port was accepted.");
        Rejects(() => StackCatalog.Settings(localAi, new()), "Local AI without GPUs was accepted.");
        check(StackCatalog.Server(localAi, gpuNode) is { Unmet: null, Reason: null, Gpus: [{ Unsupported: null }] }
            && StackCatalog.Server(localAi, gpuNode with { Gpu = null }).Reason is not null
            && StackCatalog.Server(localAi, gpuNode with { Online = false }).Reason is not null,
            "Local AI eligibility ignored the CUDA line or the server being offline.");
        var rendered = localAi.Render(settings, gpuNode, new Dictionary<string, string>());
        var env = StackCatalog.ReadEnv(rendered.Env);
        check(!rendered.Compose.Contains('\r'), "Local AI's compose has Windows line endings.");
        check(rendered.Compose.Contains("lucia-inference:0.1.4-cuda13@sha256:") && rendered.Compose.Contains($"device_ids: [\"{uuid}\"]")
            && rendered.Compose.Contains("HostPlatform__MemoryBudgetGiB: \"23\"") && rendered.Require.Contains("cuda=13")
            && env["LUCIA_WORKER_KEY"].Length >= 32 && env["LUCIA_WORKER_KEY"] != env["LUCIA_INFERENCE_KEY"],
            "Local AI rendered the wrong image, GPUs, memory budget, requirements or keys.");
        check(localAi.Render(settings, gpuNode, env).Env == rendered.Env, "Re-rendering Local AI replaced its keys.");
        check(settings["engine"] == "lucia" && !rendered.Compose.Contains("vllm") && rendered.Compose.Contains("- models:/models")
            && rendered.Compose.Contains("HostPlatform__ModelDirectory: /models\n"), "Local AI's default engine isn't Lucia Inference with a local library.");
        Rejects(() => StackCatalog.Settings(localAi, new() { ["gpus"] = uuid, ["engine"] = "ollama" }), "An unknown engine was accepted.");
        var model = Guid.NewGuid();
        var vllmSettings = StackCatalog.Settings(localAi, new()
        {
            ["gpus"] = uuid, ["engine"] = "vllm", ["library"] = "192.168.0.172:/var/nfs/shared/Models",
            ["vllm-model"] = model.ToString(), ["vllm-name"] = "Qwen/Qwen3-0.6B",
        });
        var library = localAi.Render(vllmSettings, gpuNode, env).Compose;
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "lucia-vllm-compose.yml"), library);
        check(library.Contains("vllm/vllm-openai:v0.30.0@sha256:") && library.Contains("\"8080:8000\"") && library.Contains("\"8081:8080\"")
            && library.Contains($"command: [\"/models/lucialab01/{model:N}/files\", \"--served-model-name\", \"Qwen/Qwen3-0.6B\", \"--max-model-len\", \"auto\", \"--tensor-parallel-size\", \"1\"]")
            && library.Contains("device: \":/var/nfs/shared/Models\"") && library.Contains("addr=192.168.0.172,nfsvers=3")
            && library.Contains("- library:/models:ro,nocopy") && library.Contains("- library:/models:nocopy") && library.Contains("Worker__LibraryOnly: \"true\"") && library.Contains("  vllm-cache:")
            && library.Split("device_ids").Length == 2 && !library.Contains("\n\n\n"),
            "vLLM rendered the wrong image, ports, command, NFS library or GPU reservation.");
        var idle = localAi.Render(new Dictionary<string, string>(vllmSettings) { ["vllm-model"] = "" }, gpuNode, env).Compose;
        check(!idle.Contains("vllm:") && !idle.Contains("vllm-cache") && idle.Contains("\"8081:8080\""), "vLLM without a model still rendered a vLLM service.");
        foreach (var (key, value, why) in new[] {
            ("library", "nas:relative", "a library that isn't host:/path"), ("library", "nas:/a b", "a library path with a space"),
            ("vllm-name", "../etc", "a served name that isn't a repository"), ("vllm-context", "12", "a context below 256"),
            ("vllm-model", "not-a-guid", "a model that isn't an id") })
        {
            var bad = new Dictionary<string, string>(vllmSettings) { [key] = value };
            try { localAi.Render(bad, gpuNode, env); throw new InvalidOperationException($"vLLM with {why} was accepted."); }
            catch (HardwareOnboardingException error) when (error.StatusCode is 400) { check(true, why); }
        }
        try
        {
            localAi.Render(vllmSettings, gpuNode with { Gpu = new(12), Status = cudaHost with { Runtime = cudaRuntime with { CudaVersion = "12.8" } } }, env);
            throw new InvalidOperationException("vLLM on a CUDA 12.8 driver was accepted.");
        }
        catch (HardwareOnboardingException error) when (error.Code == "vllm_driver_too_old") { check(true, "old driver"); }
        // llama.cpp serves the whole library through its router, so it runs without a chosen model.
        var llamaSettings = new Dictionary<string, string>(vllmSettings) { ["engine"] = "llamacpp", ["vllm-model"] = "", ["vllm-name"] = "" };
        var llama = localAi.Render(llamaSettings, gpuNode, env).Compose;
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "lucia-llama-compose.yml"), llama);
        check(llama.Contains("llama.cpp:server-cuda13-b11206@sha256:") && llama.Contains("\"8080:8080\"") && llama.Contains("\"8081:8080\"")
            && llama.Contains("command: [\"--models-preset\", \"/cache/llama-models.ini\", \"--models-max\", \"1\", \"--host\", \"0.0.0.0\", \"--port\", \"8080\"]")
            && llama.Contains("HostPlatform__LlamaCache: /cache") && llama.Contains("- llama-cache:/cache\n") && llama.Contains("- llama-cache:/cache:ro")
            && llama.Contains("\n  llama-cache:") && llama.Split("/models").Length == 3 && llama.Contains("condition: service_healthy")
            && llama.Contains("LLAMA_API_KEY: ${LUCIA_INFERENCE_KEY}") && !llama.Contains("vllm") && llama.Split("device_ids").Length == 2 && !llama.Contains("\n\n\n"),
            "llama.cpp rendered the wrong image, ports, command, cache, key or GPU reservation.");
        check(!library.Contains("LlamaCache") && !library.Contains("llama-cache"), "vLLM's worker mirrored models for llama.cpp.");
        var created = DateTimeOffset.UnixEpoch;
        LocalModel Gguf(string repository, string file, ModelDownloadState state = ModelDownloadState.Ready, ModelKind kind = ModelKind.Chat) =>
            new(Guid.NewGuid(), new("huggingface", repository, file, kind), state, created = created.AddMinutes(1), created);
        var first = Gguf("Doctor-Shotgun/L3.3-70B-Magnum-Diamond-GGUF", "L3.3-70B-Magnum-Diamond-Q4_K_M.gguf");
        var sharded = Gguf("unsloth/Big-GGUF", "Q5_K_M/Big-Q5_K_M-00001-of-00002.gguf");
        var plain = Gguf("a/plain-GGUF", "plain.gguf");
        var presets = ModelCatalog.LlamaPresets("/models/lucialab01", [first, sharded,
            Gguf("Doctor-Shotgun/L3.3-70B-Magnum-Diamond-GGUF", "copy/L3.3-70B-Magnum-Diamond-Q4_K_M.gguf"),
            Gguf("a/b-GGUF", "b-Q4_K_M.gguf", ModelDownloadState.Downloading), Gguf("a/embed-GGUF", "e-Q8_0.gguf", kind: ModelKind.Embedding),
            Gguf("a/odd-GGUF", "odd;x-Q4_K_M.gguf"), plain]);
        check(presets == "version = 1\n"
            + $"\n[Doctor-Shotgun/L3.3-70B-Magnum-Diamond-GGUF:Q4_K_M]\nmodel = /models/lucialab01/{first.Id:N}/files/L3.3-70B-Magnum-Diamond-Q4_K_M.gguf\n"
            + $"\n[unsloth/Big-GGUF:Q5_K_M]\nmodel = /models/lucialab01/{sharded.Id:N}/files/Q5_K_M/Big-Q5_K_M-00001-of-00002.gguf\n"
            + $"\n[a/plain-GGUF:plain]\nmodel = /models/lucialab01/{plain.Id:N}/files/plain.gguf\n",
            "llama.cpp presets listed a model that isn't a Ready GGUF LLM, a duplicate name, or a path INI can't hold.");
        foreach (var (bad, why, target) in new (Dictionary<string, string>, string, ManagedNodeFacts)[] {
            (new() { ["gpus"] = "GPU-00000000-0000-0000-0000-000000000000", ["port"] = "8080" }, "with a GPU the server doesn't have", gpuNode),
            (settings, "without a CUDA line", gpuNode with { Gpu = null }),
            (settings, "on a Pascal GPU with CUDA 12", gpuNode with { Gpu = new(12),
                Status = cudaHost with { Runtime = cudaRuntime with { Gpus = [cudaRuntime.Gpus[0] with { ComputeCapability = "6.1" }] } } }) })
        {
            try { localAi.Render(bad, target, new Dictionary<string, string>()); throw new InvalidOperationException($"Local AI {why} was accepted."); }
            catch (HardwareOnboardingException error) when (error.StatusCode is 400 or 409) { check(true, why); }
        }

        var transfers = new StackTransfers(TimeProvider.System);
        var move = Guid.NewGuid();
        using var received = new MemoryStream();
        var receiving = transfers.Receive(move, received, CancellationToken.None);
        var payload = Enumerable.Range(0, 600_000).Select(i => (byte)i).ToArray();
        var sent = await transfers.Send(move, new MemoryStream(payload), payload.Length, CancellationToken.None);
        check(await receiving && sent == payload.Length && received.ToArray().SequenceEqual(payload)
            && transfers.Status(move) is { Bytes: 600_000, Total: 600_000 }, "A move's data didn't reach the receiving node intact.");
        var cancelled = Guid.NewGuid();
        var abandoned = transfers.Receive(cancelled, new MemoryStream(), CancellationToken.None);
        transfers.Forget(cancelled);
        check(!await abandoned.WaitAsync(TimeSpan.FromSeconds(5)), "A cancelled move left its receiver waiting.");
    }
}
