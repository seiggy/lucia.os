using System.Security.Cryptography;

namespace Lucia.NodeAgent;

/// <summary>One enabled skill Lucia wants on this node. <c>Hash</c> is the skill's content hash; <c>Sha256</c> covers the tar.gz.</summary>
internal sealed record SkillEntry(string Name, string Hash, long Size, string Sha256);
internal sealed record SandboxEntry(string Hash, long Size, string Sha256);
internal sealed record SkillManifest(SkillEntry[]? Skills, SandboxEntry? Sandbox);

/// <summary>
/// Keeps <see cref="SkillCache"/> equal to Lucia's skill manifest: polls it every minute and whenever <see cref="AnswerAsync"/> is asked
/// to (the <c>sync-skills</c> request Lucia sends before a run), downloads what is missing, verifies it and prunes the rest. One pass
/// runs at a time. Phase 3's sandbox runtime travels the same way, as the manifest's <c>sandbox</c> entry.
/// </summary>
internal static class SkillSync
{
    internal const int MaxSkills = 200;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60), RequestWait = TimeSpan.FromSeconds(15);
    private static readonly SemaphoreSlim Wake = new(0, 1);
    private static TaskCompletionSource<(bool Ok, string Message)> nextPass = NewPass();

    /// <summary>What this agent can do beyond the basics: run commands, sync skills and, with KVM and Docker, host the skill sandbox.</summary>
    internal static string[] Features => Kvm() ? [.. NodeExec.Features, "skills", "kvm"] : [.. NodeExec.Features, "skills"];

    /// <summary>Skill name to the first 12 characters of the hash held, for the heartbeat; a signed report is limited to 32 KiB.</summary>
    internal static Dictionary<string, string>? Reported()
    {
        var held = SkillCache.Installed(SkillCache.Root).Where(item => item.Key != SkillCache.SandboxName).Take(MaxSkills / 2)
            .ToDictionary(item => item.Key, item => item.Value[..12], StringComparer.Ordinal);
        return held.Count == 0 ? null : held;
    }

    internal static bool Kvm() => Kvm("/dev/kvm", NodeRuntime.Current.DockerVersion);

    internal static bool Kvm(string device, string? dockerVersion) => dockerVersion is { Length: > 0 } && File.Exists(device);

    private static TaskCompletionSource<(bool Ok, string Message)> NewPass() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static async Task RunAsync(DiscoveryClient client, Guid node, Func<string> certificate, ECDsa key, CancellationToken token)
    {
        string? lastError = null;
        while (!token.IsCancellationRequested)
        {
            var report = Interlocked.Exchange(ref nextPass, NewPass());
            var result = (Ok: true, Message: "Skills are up to date.");
            try
            {
                var message = await PassAsync(client, node, certificate(), key, SkillCache.Root, token);
                result = (true, message);
                lastError = null;
            }
            catch (Exception ex) when (ex is NodeAgentException or IOException or HttpRequestException or UnauthorizedAccessException
                || ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                result = (false, StackRunner.Bounded(ex.Message, 500) ?? "Skill sync failed.");
                if (result.Message != lastError) Console.Error.WriteLine("Skill sync failed; retrying. " + result.Message);
                lastError = result.Message;
            }
            report.TrySetResult(result);
            try { await Wake.WaitAsync(Interval, token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
    }

    /// <summary>Starts a pass now and answers when it has finished, or says it's still running after a short wait.</summary>
    internal static async Task<NodeRequestResult> AnswerAsync(NodeRequest request, CancellationToken token)
    {
        var pass = nextPass.Task;
        try { Wake.Release(); }
        catch (SemaphoreFullException) { }
        try
        {
            var (ok, message) = await pass.WaitAsync(RequestWait, token);
            return new(request.RequestId, ok, null, message);
        }
        catch (TimeoutException) { return new(request.RequestId, true, null, "Still downloading skills."); }
    }

    /// <summary>One sync pass: fetch the manifest, install what is missing, prune the rest. Returns what happened.</summary>
    internal static async Task<string> PassAsync(DiscoveryClient client, Guid node, string certificate, ECDsa key, string root, CancellationToken token)
    {
        var manifest = await client.SkillManifestAsync(node, certificate, key, token);
        var skills = manifest.Skills ?? [];
        var wanted = new Dictionary<string, (string Hash, string Sha256, long Size, SkillLimits Limits)>(StringComparer.Ordinal);
        foreach (var skill in skills.Take(MaxSkills))
            if (SkillCache.ValidName(skill.Name) && skill.Name != SkillCache.SandboxName && SkillCache.ValidHash(skill.Hash) && SkillCache.ValidHash(skill.Sha256))
                wanted[skill.Name] = (skill.Hash, skill.Sha256, skill.Size, SkillCache.Skill);
        if (manifest.Sandbox is { } sandbox && SkillCache.ValidHash(sandbox.Hash) && SkillCache.ValidHash(sandbox.Sha256))
            wanted[SkillCache.SandboxName] = (sandbox.Hash, sandbox.Sha256, sandbox.Size, SkillCache.Sandbox);
        var installed = 0;
        var failures = new List<string>();
        foreach (var (name, want) in wanted.ToArray())
        {
            if (Directory.Exists(Path.Combine(root, name, want.Hash))) continue;
            try
            {
                var bytes = await client.DownloadSkillAsync(node, certificate, key, name, want.Hash, want.Limits.DownloadBytes, token);
                if (SkillCache.Install(root, name, want.Hash, bytes, want.Sha256, want.Limits)) installed++;
            }
            catch (Exception ex) when (ex is NodeAgentException or IOException or HttpRequestException)
            {
                failures.Add($"{name}: {ex.Message}");
                wanted.Remove(name);
                // Keep the version already on disk until a good one arrives.
                if (SkillCache.Installed(root).TryGetValue(name, out var held)) wanted[name] = (held, want.Sha256, want.Size, want.Limits);
            }
        }
        var removed = SkillCache.Prune(root, wanted.ToDictionary(item => item.Key, item => item.Value.Hash, StringComparer.Ordinal));
        if (installed > 0 || removed.Length > 0)
            Console.Error.WriteLine($"Skills synced: {installed} installed, {removed.Length} removed.");
        if (failures.Count > 0) throw new NodeAgentException(string.Join("; ", failures.Take(3)));
        return $"Skills synced ({installed} installed, {removed.Length} removed).";
    }
}
