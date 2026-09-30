using System.Text.Json;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Onboarding;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Nodes;

public sealed record OwnerSshKey(Guid Id, string PublicKey, string Algorithm, string Fingerprint, string Label, DateTimeOffset AddedAt);
public sealed record AddOwnerSshKeyRequest(string? PublicKey, string? Label);
public sealed record OwnerSshKeyList(string Username, OwnerSshKey[] Keys);
internal sealed record OwnerSshKeyState(int SchemaVersion, Dictionary<string, OwnerSshKey[]> Users);

/// <summary>
/// SSH public keys that owners register for their own directory account. Managed nodes receive them in
/// each heartbeat reply and install them as root-owned authorized-keys files; sshd still requires the
/// account to be in <c>lucia-owners</c>.
/// </summary>
public sealed class OwnerSshKeys(IOptions<HardwareOnboardingOptions> options)
{
    public const int MaxKeysPerUser = 10;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string Path => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(options.Value.StateDirectory)!, "nodes", "ssh-keys.json");

    public static string Username(string? value) =>
        value is not null && Regex.IsMatch(value, @"\A[a-z_][a-z0-9_.-]{0,31}\z", RegexOptions.CultureInvariant)
            && value is not ("root" or "lucia-recovery")
            ? value
            : throw new HardwareOnboardingException(400, "unsupported_username",
                "Your Lucia username cannot be used as a server login name, so SSH keys cannot be attached to it.");

    public async Task<OwnerSshKeyList> List(string username, CancellationToken ct)
    {
        Username(username);
        await _gate.WaitAsync(ct);
        try { return new(username, Read().Users.GetValueOrDefault(username) ?? []); }
        finally { _gate.Release(); }
    }

    public async Task<OwnerSshKeyList> Add(string username, AddOwnerSshKeyRequest request, DateTimeOffset now, CancellationToken ct)
    {
        Username(username);
        var text = request.PublicKey?.Trim() ?? "";
        if (text.Contains('\n') || text.Contains('\r')) throw new HardwareOnboardingException(400, "invalid_ssh_key", "Add one public key at a time.");
        var parsed = RecoverySshKeys.Parse(text);
        var label = Label(request.Label) ?? Label(string.Join(' ', text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Skip(2)))
            ?? parsed.Algorithm;
        await _gate.WaitAsync(ct);
        try
        {
            var state = Read();
            var keys = state.Users.GetValueOrDefault(username) ?? [];
            if (keys.Any(key => key.PublicKey == parsed.PublicKey))
                throw new HardwareOnboardingException(409, "duplicate_ssh_key", "This key is already on your account.");
            if (keys.Length >= MaxKeysPerUser)
                throw new HardwareOnboardingException(409, "too_many_ssh_keys", $"Remove a key first. Each account can have up to {MaxKeysPerUser}.");
            // Every key rides in each node heartbeat reply, which the agent caps at 64 KiB.
            if (state.Users.Sum(user => user.Key.Length + 8 + user.Value.Sum(key => key.PublicKey.Length + 3)) + parsed.PublicKey.Length > 40000)
                throw new HardwareOnboardingException(409, "ssh_key_list_full", "Lucia's shared SSH key list is full. Remove unused keys first.");
            state.Users[username] = [.. keys, new(Guid.NewGuid(), parsed.PublicKey, parsed.Algorithm, parsed.Fingerprint, label, now)];
            await Write(state, ct);
            return new(username, state.Users[username]);
        }
        finally { _gate.Release(); }
    }

    public async Task<OwnerSshKeyList> Remove(string username, Guid id, CancellationToken ct)
    {
        Username(username);
        await _gate.WaitAsync(ct);
        try
        {
            var state = Read();
            var keys = state.Users.GetValueOrDefault(username) ?? [];
            if (keys.All(key => key.Id != id)) throw new HardwareOnboardingException(404, "ssh_key_not_found", "That key is no longer on your account.");
            keys = keys.Where(key => key.Id != id).ToArray();
            if (keys.Length == 0) state.Users.Remove(username);
            else state.Users[username] = keys;
            await Write(state, ct);
            return new(username, keys);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Drops a deleted person's keys so heartbeats stop carrying them.</summary>
    public async Task RemoveUser(string username, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = Read();
            if (state.Users.Remove(username)) await Write(state, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Username → normalized public keys, for heartbeat replies.</summary>
    public async Task<Dictionary<string, string[]>> Authorized(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return Read().Users.ToDictionary(item => item.Key, item => item.Value.Select(key => key.PublicKey).ToArray(), StringComparer.Ordinal); }
        finally { _gate.Release(); }
    }

    private static string? Label(string? value)
    {
        var cleaned = new string((value ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length == 0 ? null : cleaned.Length > 64 ? cleaned[..64] : cleaned;
    }

    private OwnerSshKeyState Read()
    {
        DomainOnboardingStore.RejectLinks(Path);
        if (!File.Exists(Path)) return new(1, new(StringComparer.Ordinal));
        var state = JsonSerializer.Deserialize<OwnerSshKeyState>(CertbotFiles.ReadBounded(Path, 1024 * 1024), DomainOnboardingStore.Json);
        if (state is not { SchemaVersion: 1, Users: not null }) throw new InvalidDataException("Owner SSH key state is invalid.");
        return state with { Users = new(state.Users, StringComparer.Ordinal) };
    }

    private Task Write(OwnerSshKeyState state, CancellationToken ct)
    {
        DomainOnboardingStore.EnsureDirectory(System.IO.Path.GetDirectoryName(Path)!);
        return DomainOnboardingStore.WriteJson(Path, state, ct);
    }
}

public static class OwnerSshKeyEndpoints
{
    public static void MapOwnerSshKeys(this WebApplication app)
    {
        var group = app.MapGroup("/api/host/ssh-keys").WithTags("SSH keys")
            .RequireAuthorization("HostOwner").AddEndpointFilter<HardwareOnboardingErrorFilter>();
        group.MapGet("", (HttpContext context, OwnerSshKeys keys, CancellationToken ct) => keys.List(User(context), ct));
        group.MapPost("", async (HttpContext context, OwnerSshKeys keys, TimeProvider time, CancellationToken ct) =>
        {
            if (!context.Request.HasJsonContentType() || context.Request.ContentLength > 20000)
                throw new HardwareOnboardingException(400, "invalid_body", "Send one public key as JSON.");
            var request = await context.Request.ReadFromJsonAsync<AddOwnerSshKeyRequest>(HardwareOnboardingJson.Options, ct)
                ?? throw new HardwareOnboardingException(400, "invalid_body", "Send one public key as JSON.");
            return await keys.Add(User(context), request, time.GetUtcNow(), ct);
        });
        group.MapDelete("/{id:guid}", (Guid id, HttpContext context, OwnerSshKeys keys, CancellationToken ct) =>
            keys.Remove(User(context), id, ct));
    }

    // Owner API keys carry no directory username; only a signed-in person can manage their own keys.
    private static string User(HttpContext context) => context.User.FindFirst("preferred_username")?.Value is { } name
        ? OwnerSshKeys.Username(name)
        : throw new HardwareOnboardingException(403, "browser_session_required", "Sign in to Lucia in a browser to manage your SSH keys.");
}
