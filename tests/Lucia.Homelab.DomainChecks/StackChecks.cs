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
        foreach (var name in new[] { "", "Plex", "1plex", "plex-", "plex_x", "../etc", new string('a', 41) })
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
        check(StackRequirements.Unmet(["memory>=1G"], null) == "memory>=1G", "A node that never reported met a requirement.");

        foreach (var bad in new[] { "cuda", "cuda=11", "cuda>=12", "cuda=12.4" })
            Rejects(() => StackRequirements.Normalize([bad]), $"The requirement '{bad}' was accepted.");
        var uuid = "GPU-cbeac6c4-3134-d34a-9fb5-fc0a0daf1981";
        var cudaRuntime = host.Runtime! with { CudaVersion = "13.3", Gpus = [host.Runtime.Gpus[0] with { Uuid = uuid }] };
        var cudaHost = host with { Runtime = cudaRuntime };
        var pinned12 = new NodeGpuSettings(12, false, []);
        check(StackRequirements.Unmet(["cuda=12"], cudaHost, pinned12) is null, "A server pinned to CUDA 12 didn't meet cuda=12.");
        check(StackRequirements.Unmet(["cuda=13"], cudaHost, pinned12) == "cuda=13", "A CUDA 12 server met cuda=13.");
        check(StackRequirements.Unmet(["cuda=12"], cudaHost) == "cuda=12", "A server without a pinned line met a cuda requirement.");
        check(StackRequirements.Unmet(["cuda=13"], cudaHost with { Runtime = cudaRuntime with { CudaVersion = "12.8" } },
            new NodeGpuSettings(13, false, [])) == "cuda=13", "A pinned line the driver no longer supports still met its requirement.");
        check(CudaLines.Unsupported(13, cudaRuntime) is null && CudaLines.Unsupported(12, cudaRuntime) is null,
            "A current driver and GPU were ruled out.");
        check(CudaLines.Unsupported(12, cudaRuntime with { CudaVersion = null }) is not null
            && CudaLines.Unsupported(13, cudaRuntime with { Gpus = [cudaRuntime.Gpus[0] with { ComputeCapability = "6.1" }] }) is { } pascal
            && pascal.Contains("7.5") && CudaLines.Unsupported(12, cudaRuntime with { Gpus = [] }) is not null,
            "CUDA line support ignored the driver's CUDA version, a GPU's compute capability, or a server without GPUs.");
        check(CudaLines.Validate(new(13, true, [uuid, uuid]), cudaRuntime) is { CudaLine: 13, Inference: true, InferenceGpus: [var only] } && only == uuid
            && CudaLines.Validate(new(12, false, [uuid]), cudaRuntime).InferenceGpus.Length == 0,
            "Local AI GPU choices weren't de-duplicated, or were kept with local AI off.");
        foreach (var (bad, why, runtime) in new (SaveNodeGpuRequest, string, NodeRuntime)[] {
            (new(null, true, [uuid]), "without a CUDA line", cudaRuntime), (new(13, true, []), "without a GPU", cudaRuntime),
            (new(13, true, ["GPU-00000000-0000-0000-0000-000000000000"]), "with a GPU the server doesn't have", cudaRuntime),
            (new(11, false, null), "on an unknown line", cudaRuntime),
            (new(13, true, [uuid]), "on a server whose containers can't use its GPUs", cudaRuntime with { GpuContainers = false }) })
        {
            try { CudaLines.Validate(bad, runtime); throw new InvalidOperationException($"Local AI settings {why} were accepted."); }
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
