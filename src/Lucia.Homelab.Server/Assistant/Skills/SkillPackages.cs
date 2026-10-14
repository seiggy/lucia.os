using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Lucia.Homelab.Server.Assistant.Skills;

/// <summary>One enabled skill a node should hold. <c>Hash</c> is the skill's content hash; <c>Sha256</c> and <c>Size</c> describe the tar.gz.</summary>
public sealed record SkillPackageEntry(string Name, string Hash, long Size, string Sha256);

/// <summary>The sandbox runtime package for nodes with KVM: <c>Hash</c> names its content, <c>Sha256</c> and <c>Size</c> describe the tar.gz.</summary>
public sealed record SkillSandboxEntry(string Hash, long Size, string Sha256);

public sealed record SkillManifest(IReadOnlyList<SkillPackageEntry> Skills, SkillSandboxEntry? Sandbox);

/// <summary>
/// What nodes download: the enabled skills that run on a node or in the sandbox, each as the same deterministic tar.gz
/// <see cref="SkillBundle"/> builds. Packages are kept in memory only while their skill is current, and only up to a cap.
/// </summary>
public sealed class SkillPackages(SkillStore store, SkillSandboxRuntime? sandbox = null)
{
    public const long CacheBytes = 64L * 1024 * 1024;
    private readonly ConcurrentDictionary<string, (string Hash, byte[] Bytes, string Sha256)> _cache = new(StringComparer.Ordinal);

    public Task<SkillManifest> ManifestAsync(CancellationToken ct) => ManifestAsync(kvm: false, ct);

    /// <param name="kvm">Whether the node reports KVM. Only such a node is offered the sandbox runtime, and only while a skill uses it.</param>
    public async Task<SkillManifest> ManifestAsync(bool kvm, CancellationToken ct)
    {
        var entries = new List<SkillPackageEntry>();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var sandboxSkill = false;
        foreach (var skill in await store.ListAsync(ct))
        {
            if (!skill.Enabled || skill.BuiltIn || skill.Problem is not null || skill.Execution is not (SkillExecution.Node or SkillExecution.Sandbox)) continue;
            if (Package(skill.Name, skill.Hash) is not { } package) continue;
            keep.Add(skill.Name);
            sandboxSkill |= skill.Execution == SkillExecution.Sandbox;
            entries.Add(new(skill.Name, skill.Hash, package.Bytes.Length, package.Sha256));
        }
        foreach (var stale in _cache.Keys.Where(name => !keep.Contains(name)).ToArray()) _cache.TryRemove(stale, out _);
        return new(entries, kvm && sandboxSkill && sandbox is not null ? await sandbox.EntryAsync(ct) : null);
    }

    /// <summary>The sandbox runtime at exactly this hash, or null when this controller ships none or it is a different build.</summary>
    public async Task<(byte[] Bytes, string Sha256)?> GetSandboxAsync(string hash, CancellationToken ct) =>
        sandbox is null ? null : await sandbox.GetAsync(hash, ct);

    /// <summary>The package for a skill at exactly this hash, or null when the skill is gone, disabled, changed since, or not for nodes.</summary>
    public async Task<(byte[] Bytes, string Sha256)?> GetAsync(string name, string hash, CancellationToken ct)
    {
        if (!SkillStore.ValidName(name)) return null;
        var skill = await store.FindAsync(name, ct);
        if (skill is not { Enabled: true, Problem: null, BuiltIn: false } || skill.Execution is not (SkillExecution.Node or SkillExecution.Sandbox)
            || !string.Equals(skill.Hash, hash, StringComparison.Ordinal)) return null;
        return Package(name, hash) is { } package ? (package.Bytes, package.Sha256) : null;
    }

    private (byte[] Bytes, string Sha256)? Package(string name, string hash)
    {
        if (_cache.TryGetValue(name, out var cached) && cached.Hash == hash) return (cached.Bytes, cached.Sha256);
        try
        {
            var (folder, paths) = store.PackList(name);
            var bytes = SkillBundle.Pack(folder, paths);
            // A save during packing would pair old bytes with a new hash.
            if (store.HashOf(name) != hash) return null;
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (_cache.Values.Sum(item => (long)item.Bytes.Length) + bytes.Length <= CacheBytes) _cache[name] = (hash, bytes, sha);
            return (bytes, sha);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or AssistantException) { return null; }
    }
}
