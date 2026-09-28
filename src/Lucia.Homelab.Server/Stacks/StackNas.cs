using System.Text.Json;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Onboarding;
using Microsoft.AspNetCore.DataProtection;

namespace Lucia.Homelab.Server.Stacks;

/// <param name="Name">The folder it mounts as, under <c>/mnt/lucia/nas/&lt;nas&gt;/</c>.</param>
/// <param name="Path">An NFS export path (<c>/var/nfs/shared/Media</c>) or an SMB share name (<c>Media</c>).</param>
public sealed record NasShare(string Name, string Path);
/// <param name="Password">SMB only. Null keeps the saved password.</param>
public sealed record SaveNasRequest(string Kind, string Host, NasShare[] Shares, string? Username = null, string? Password = null);
internal sealed record StoredNas(string Id, string Kind, string Host, NasShare[] Shares, string? Username, string? ProtectedPassword,
    DateTimeOffset UpdatedAt, string UpdatedBy);
internal sealed record NasFile(int SchemaVersion, StoredNas[] Servers);
/// <summary>A share every managed node keeps mounted at <c>/mnt/lucia/nas/&lt;Nas&gt;/&lt;Share&gt;</c>.</summary>
/// <param name="Source"><c>host:/export</c> for NFS, <c>//host/share</c> for SMB.</param>
public sealed record NodeMount(string Nas, string Share, string Kind, string Source, string? Username = null, string? Password = null);
/// <param name="State">Mounted, Pending or Failed.</param>
public sealed record NodeMountStatus(string Nas, string Share, string State, string? Message = null);

/// <summary>
/// NAS connections: NFS and SMB servers whose shares every managed node mounts, so an app placed anywhere can use them
/// by path, and placement can require that the node actually has the share mounted.
/// </summary>
public sealed partial class StackStore
{
    public const int MaxNas = 16, MaxSharesPerNas = 32;
    internal const string NasRoot = "/mnt/lucia/nas";
    private readonly IDataProtector _nasProtector = protection.CreateProtector("Lucia.Homelab.NasCredentials.v1");
    private string NasPath => Path.Combine(Path.GetDirectoryName(options.Value.StateDirectory)!, "stacks", "nas.json");

    /// <summary>Every NAS with its shares and, for each share, whether each managed server has it mounted.</summary>
    public async Task<object> NasList(CancellationToken ct)
    {
        var facts = await nodes.Facts(ct);
        StoredNas[] servers;
        StoredStack[] stacks;
        await _gate.WaitAsync(ct);
        try { (servers, stacks) = (ReadNasUnlocked(), ReadUnlocked()); }
        finally { _gate.Release(); }
        return new
        {
            servers = servers.Select(nas => new
            {
                nas.Id, nas.Kind, nas.Host, nas.Username, hasPassword = nas.ProtectedPassword is not null, nas.UpdatedAt, nas.UpdatedBy,
                shares = nas.Shares.Select(share => new
                {
                    share.Name, share.Path, mountPath = $"{NasRoot}/{nas.Id}/{share.Name}",
                    usedBy = stacks.Where(stack => Uses(stack, nas.Id, share.Name)).Select(stack => stack.Name).ToArray(),
                    mounts = facts.Select(node => new
                    {
                        node = node.Hostname,
                        status = Fresh(node.NodeId)?.Report.Mounts?.FirstOrDefault(item => item.Nas == nas.Id && item.Share == share.Name),
                    }).ToArray(),
                }).ToArray(),
            }).ToArray(),
        };
    }

    public async Task<object> SaveNas(string id, SaveNasRequest request, string actor, CancellationToken ct)
    {
        ValidateNasId(id);
        var kind = request.Kind is "nfs" or "smb" ? request.Kind
            : throw new HardwareOnboardingException(400, "invalid_nas", "Choose NFS or SMB.");
        var host = (request.Host ?? "").Trim();
        if (!HostPattern().IsMatch(host))
            throw new HardwareOnboardingException(400, "invalid_nas_host", "Enter the NAS's IP address or hostname, such as 192.168.1.10.");
        var shares = (request.Shares ?? []).Select(share => new NasShare((share?.Name ?? "").Trim(), (share?.Path ?? "").Trim())).ToArray();
        if (shares.Length is 0 or > MaxSharesPerNas)
            throw new HardwareOnboardingException(400, "invalid_nas_shares", $"Add between 1 and {MaxSharesPerNas} shares.");
        foreach (var share in shares)
        {
            if (!ShareNamePattern().IsMatch(share.Name))
                throw new HardwareOnboardingException(400, "invalid_share_name",
                    $"\"{Short(share.Name)}\" can't be a folder name. Use letters, digits, dots, dashes and underscores, starting with a letter or digit.");
            if (!(kind == "nfs" ? ExportPattern().IsMatch(share.Path) && !share.Path.Split('/').Any(part => part is "." or "..")
                    : SmbSharePattern().IsMatch(share.Path)))
                throw new HardwareOnboardingException(400, "invalid_share_path", kind == "nfs"
                    ? $"\"{Short(share.Path)}\" isn't an NFS export path. Write it as the NAS shows it, such as /var/nfs/shared/Media."
                    : $"\"{Short(share.Path)}\" isn't an SMB share name. Use the name alone, such as Media, without slashes or spaces.");
        }
        if (shares.Select(share => share.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != shares.Length)
            throw new HardwareOnboardingException(400, "duplicate_share", "Give each share a different folder name.");
        var username = kind == "smb" ? (request.Username ?? "").Trim() : null;
        if (username is not null && !SmbUserPattern().IsMatch(username))
            throw new HardwareOnboardingException(400, "invalid_nas_user", "Enter the SMB username, without spaces.");
        if (request.Password is { } typed && (typed.Length is 0 or > 256 || typed.Any(char.IsControl)))
            throw new HardwareOnboardingException(400, "invalid_nas_password", "The password must be 1 to 256 characters on one line.");
        await _gate.WaitAsync(ct);
        try
        {
            var servers = ReadNasUnlocked().ToList();
            var existing = servers.FirstOrDefault(nas => nas.Id == id);
            if (existing is null && servers.Count >= MaxNas)
                throw new HardwareOnboardingException(409, "too_many_nas", $"Lucia connects up to {MaxNas} NAS servers.");
            var stacks = ReadUnlocked();
            foreach (var share in existing?.Shares ?? [])
                if (!shares.Any(item => item.Name == share.Name) && stacks.FirstOrDefault(stack => Uses(stack, id, share.Name)) is { } user)
                    throw new HardwareOnboardingException(409, "share_in_use", $"{user.Name} uses {id}/{share.Name}. Change that app first.");
            var password = kind == "smb"
                ? request.Password is { } given ? _nasProtector.Protect(given)
                    : existing?.Kind == "smb" ? existing.ProtectedPassword : null
                : null;
            if (kind == "smb" && password is null)
                throw new HardwareOnboardingException(400, "invalid_nas_password", "Enter the SMB password.");
            var saved = new StoredNas(id, kind, host, shares, username, password, time.GetUtcNow(), actor);
            if (existing is not null) servers.Remove(existing);
            servers.Add(saved);
            await WriteNas(servers, ct);
            return new { saved.Id };
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteNas(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var servers = ReadNasUnlocked().ToList();
            var nas = servers.FirstOrDefault(item => item.Id == id)
                ?? throw new HardwareOnboardingException(404, "nas_not_found", "That NAS isn't connected.");
            if (ReadUnlocked().FirstOrDefault(stack => nas.Shares.Any(share => Uses(stack, id, share.Name))) is { } user)
                throw new HardwareOnboardingException(409, "share_in_use", $"{user.Name} uses a share on {id}. Change that app first.");
            servers.Remove(nas);
            await WriteNas(servers, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>The shares every node should keep mounted, with SMB credentials, for its sync.</summary>
    internal async Task<NodeMount[]> DesiredMounts(CancellationToken ct)
    {
        StoredNas[] servers;
        await _gate.WaitAsync(ct);
        try { servers = ReadNasUnlocked(); }
        finally { _gate.Release(); }
        return servers.SelectMany(nas => nas.Shares.Select(share => nas.Kind == "nfs"
            ? new NodeMount(nas.Id, share.Name, "nfs", $"{nas.Host}:{share.Path}")
            : new NodeMount(nas.Id, share.Name, "smb", $"//{nas.Host}/{share.Path}", nas.Username, _nasProtector.Unprotect(nas.ProtectedPassword!))))
            .ToArray();
    }

    /// <summary>
    /// The <c>nas=</c> requirements a custom app's compose implies: each share it names by path under /mnt/lucia/nas,
    /// which must be a share Lucia knows.
    /// </summary>
    internal static string[] NasRequirements(string compose, IEnumerable<(string Nas, string Share)> known)
    {
        var shares = known.ToHashSet();
        var found = new List<string>();
        foreach (Match match in NasPathPattern().Matches(compose))
        {
            var (nas, share) = (match.Groups[1].Value, match.Groups[2].Value);
            if (!shares.Contains((nas, share)))
                throw new HardwareOnboardingException(400, "unknown_nas_share",
                    $"The compose uses {NasRoot}/{nas}/{share}, but no NAS {nas} with a share {share} is connected. Add it in Settings → Storage.");
            found.Add($"nas={nas}/{share}");
        }
        return found.Distinct().ToArray();
    }

    private async Task<IEnumerable<(string, string)>> KnownShares(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return ReadNasUnlocked().SelectMany(nas => nas.Shares.Select(share => (nas.Id, share.Name))).ToArray(); }
        finally { _gate.Release(); }
    }

    /// <summary>The shares a node reported mounted in its last sync, as <c>nas/share</c>.</summary>
    private IReadOnlySet<string> Mounted(Guid? nodeId) =>
        (nodeId is { } id ? Fresh(id)?.Report.Mounts : null)?.Where(item => item.State == "Mounted")
            .Select(item => $"{item.Nas}/{item.Share}").ToHashSet() ?? [];

    private static bool Uses(StoredStack stack, string nas, string share) =>
        (stack.Manifest.Placement.Require ?? []).Contains($"nas={nas}/{share}");

    internal static void ValidateNasId(string id)
    {
        if (!NasIdPattern().IsMatch(id))
            throw new HardwareOnboardingException(400, "invalid_nas_id",
                "Use up to 32 lowercase letters, digits and hyphens for the NAS name, starting with a letter.");
    }

    internal static void ValidateMounts(NodeMountStatus[]? mounts)
    {
        if (mounts is null) return;
        HardwareInventoryValidation.Require(mounts.Length <= MaxNas * MaxSharesPerNas, "The mount report is oversized.");
        foreach (var mount in mounts)
        {
            HardwareInventoryValidation.Require(NasIdPattern().IsMatch(mount.Nas ?? "") && ShareNamePattern().IsMatch(mount.Share ?? "")
                && mount.State is "Mounted" or "Pending" or "Failed", "The mount report is invalid.");
            HardwareInventoryValidation.Text(mount.Message, 512);
        }
    }

    private static string Short(string text) => text.Length > 40 ? text[..40] + "…" : text;

    private StoredNas[] ReadNasUnlocked()
    {
        DomainOnboardingStore.RejectLinks(NasPath);
        if (!File.Exists(NasPath)) return [];
        var file = JsonSerializer.Deserialize<NasFile>(CertbotFiles.ReadBounded(NasPath, 1024 * 1024), DomainOnboardingStore.Json);
        if (file is not { SchemaVersion: 1, Servers: not null }) throw new InvalidDataException("NAS state is invalid.");
        return file.Servers;
    }

    private Task WriteNas(IEnumerable<StoredNas> servers, CancellationToken ct) =>
        DomainOnboardingStore.WriteJson(NasPath, new NasFile(1, servers.OrderBy(nas => nas.Id, StringComparer.Ordinal).ToArray()), ct);

    [GeneratedRegex(@"\A[a-z](?:[a-z0-9-]{0,30}[a-z0-9])?\z")]
    internal static partial Regex NasIdPattern();
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    internal static partial Regex ShareNamePattern();
    [GeneratedRegex(@"\A(?=.{1,253}\z)[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\z")]
    private static partial Regex HostPattern();
    [GeneratedRegex(@"\A/(?:[A-Za-z0-9._-]+/?){0,16}\z")]
    private static partial Regex ExportPattern();
    [GeneratedRegex(@"\A[A-Za-z0-9._$-]{1,80}\z")]
    private static partial Regex SmbSharePattern();
    [GeneratedRegex(@"\A[A-Za-z0-9._@-]{1,64}\z")]
    private static partial Regex SmbUserPattern();
    [GeneratedRegex(@"/mnt/lucia/nas/([a-z](?:[a-z0-9-]{0,30}[a-z0-9])?)/([A-Za-z0-9][A-Za-z0-9._-]{0,63})(?=[/:""'\s]|\z)")]
    private static partial Regex NasPathPattern();
}
