using System.Text.Json;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;

namespace Lucia.Homelab.Server.Domains;

public sealed record DirectoryUser(string Username, string Name, string? Email, bool Active, bool Protected, bool Synced, string[] Groups);
public sealed record DirectoryGroup(string Name, string Description, string Kind, bool Protected, string[] Members);
public sealed record DirectoryApp(string Slug, string Name, string? LaunchUrl, bool Fixed, string[] Groups);
/// <summary>A queued change. State is pending (waiting for the identity service), done or failed.</summary>
public sealed record DirectoryChange(string Id, string? Action, string? Target, string State, string? Message, DateTimeOffset RequestedAt);
public sealed record DirectoryView(
    bool Ready, DateTimeOffset? CheckedAt, bool PasswordChange, string? Actor,
    DirectoryUser[] Users, DirectoryGroup[] Groups, DirectoryApp[] Apps, DirectoryChange[] Changes);
public sealed record DirectoryRequest(
    string? Action, string? Username = null, string? Name = null, string? Email = null, string? Password = null,
    string[]? Groups = null, bool? Active = null, string? Group = null, string? Description = null, string[]? Members = null,
    string? App = null);
internal sealed record DirectorySnapshot(int SchemaVersion, DateTimeOffset CheckedAt, bool PasswordChange,
    DirectoryUser[] Users, DirectoryGroup[] Groups, DirectoryApp[] Apps);
internal sealed record DirectoryResponse(int SchemaVersion, string Id, bool Success, string? Message, DateTimeOffset CheckedAt,
    string? Action, string? Target);

/// <summary>
/// People, groups and app access. The web host holds no directory or Authentik credentials: it queues one change per
/// file for the Spark's identity service (tools/identity/people.py), which applies it, answers, and republishes the
/// secret-free directory.json. Request files can hold a new password, so the service deletes each once it's read.
/// </summary>
public sealed partial class PeopleDirectory(DomainOnboardingStore domains, OwnerSshKeys sshKeys, TimeProvider time)
{
    internal const string Requests = "directory-requests", Responses = "directory-responses";
    internal const string Owners = "lucia-owners";
    private const int MaxQueued = 20;
    private const string Symbols = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly Dictionary<string, string[]> Fields = new(StringComparer.Ordinal)
    {
        ["createUser"] = ["username", "name", "email", "password", "groups"],
        ["updateUser"] = ["username", "name", "email"],
        ["setUserGroups"] = ["username", "groups"],
        ["setPassword"] = ["username", "password"],
        ["setUserActive"] = ["username", "active"],
        ["deleteUser"] = ["username"],
        ["createGroup"] = ["group", "description"],
        ["updateGroup"] = ["group", "description"],
        ["setGroupMembers"] = ["group", "members"],
        ["deleteGroup"] = ["group"],
        ["setAppGroups"] = ["app", "groups"],
    };

    [GeneratedRegex(@"\A[a-z][a-z0-9_-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\A\d{17}-[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    private string Folder(string name) => Path.Combine(domains.Root, name);

    public async Task<DirectoryView> View(string? actor, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var snapshot = Snapshot();
            var now = time.GetUtcNow();
            var ready = snapshot is not null && snapshot.CheckedAt > now.AddMinutes(-5);
            return new(ready, snapshot?.CheckedAt, snapshot?.PasswordChange ?? false, actor,
                snapshot?.Users ?? [], snapshot?.Groups ?? [], snapshot?.Apps ?? [], await Changes(snapshot, now, ct));
        }
        finally { _gate.Release(); }
    }

    public async Task<DirectoryChange> Queue(string actor, DirectoryRequest request, CancellationToken ct)
    {
        var action = request.Action is { } name && Fields.ContainsKey(name) ? name
            : throw Invalid("Choose a supported change.");
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in Fields[action])
        {
            fields[field] = field switch
            {
                "username" => Name(request.Username, "username"),
                "group" => Name(request.Group, "group name"),
                "app" => request.App is { Length: > 0 and <= 64 } app ? app : throw Invalid("Choose an app."),
                "name" => Text(request.Name, 64, "name", required: true),
                "email" => Text(request.Email, 254, "email address", required: false),
                "description" => Text(request.Description, 200, "description", required: false),
                "password" => Password(request.Password),
                "active" => request.Active ?? throw Invalid("Choose whether the account is active."),
                "groups" => Names(request.Groups, 64, "group name"),
                "members" => Names(request.Members, 200, "username"),
                _ => throw new InvalidOperationException(),
            };
        }
        Guard(actor, action, fields);

        await _gate.WaitAsync(ct);
        try
        {
            var queue = Folder(Requests);
            DomainOnboardingStore.EnsureDirectory(queue);
            if (Directory.EnumerateFiles(queue, "*.json").Count() >= MaxQueued)
                throw new HardwareOnboardingException(429, "directory_queue_full", "Lucia is still applying earlier changes. Try again in a moment.");
            var now = time.GetUtcNow();
            var id = $"{now.UtcDateTime:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            var body = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["schemaVersion"] = 1, ["id"] = id, ["action"] = action, ["requestedBy"] = actor, ["requestedAt"] = now,
            };
            foreach (var (key, value) in fields) body[key] = value;
            await DomainOnboardingStore.WriteJson(Path.Combine(queue, id + ".json"), body, ct);
            return new(id, action, Target(fields), "pending", null, now);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Stops owners locking themselves out; the identity service separately protects Lucia's first owner.</summary>
    private static void Guard(string actor, string action, Dictionary<string, object?> fields)
    {
        var self = fields.GetValueOrDefault("username") as string == actor;
        var refused = action switch
        {
            "deleteUser" when self => "You can't delete your own account.",
            "setUserActive" when self && fields["active"] is false => "You can't disable your own account.",
            "setUserGroups" when self && !((string[])fields["groups"]!).Contains(Owners) => "You can't remove yourself from lucia-owners.",
            "setGroupMembers" when fields["group"] as string == Owners && !((string[])fields["members"]!).Contains(actor)
                => "You can't remove yourself from lucia-owners.",
            _ => null,
        };
        if (refused is not null) throw new HardwareOnboardingException(409, "directory_self_change", refused);
    }

    private static string? Target(Dictionary<string, object?> fields) =>
        (fields.GetValueOrDefault("username") ?? fields.GetValueOrDefault("group") ?? fields.GetValueOrDefault("app")) as string;

    private DirectorySnapshot? Snapshot()
    {
        var path = Path.Combine(domains.Root, "directory.json");
        DomainOnboardingStore.RejectLinks(path);
        if (!File.Exists(path)) return null;
        var value = JsonSerializer.Deserialize<DirectorySnapshot>(CertbotFiles.ReadBounded(path, 4 * 1024 * 1024), DomainOnboardingStore.Json);
        return value is { SchemaVersion: 1 } ? value : throw new InvalidDataException("The directory snapshot is invalid.");
    }

    private async Task<DirectoryChange[]> Changes(DirectorySnapshot? snapshot, DateTimeOffset now, CancellationToken ct)
    {
        var changes = new List<DirectoryChange>();
        var queue = Folder(Requests);
        if (Directory.Exists(queue))
            foreach (var path in Directory.EnumerateFiles(queue, "*.json").Order(StringComparer.Ordinal).Take(MaxQueued * 2))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (!IdPattern().IsMatch(id)) continue;
                DomainOnboardingStore.RejectLinks(path);
                try
                {
                    using var json = JsonDocument.Parse(CertbotFiles.ReadBounded(path, 16384));
                    var root = json.RootElement;
                    var target = new[] { "username", "group", "app" }
                        .Select(key => root.TryGetProperty(key, out var item) ? item.GetString() : null).FirstOrDefault(item => item is not null);
                    changes.Add(new(id, root.GetProperty("action").GetString(), target, "pending", null, root.GetProperty("requestedAt").GetDateTimeOffset()));
                }
                catch (Exception error) when (error is JsonException or IOException or KeyNotFoundException or InvalidOperationException) { }
            }
        var answers = Folder(Responses);
        if (!Directory.Exists(answers)) return [.. changes];
        foreach (var path in Directory.EnumerateFiles(answers, "*.json").Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            DomainOnboardingStore.RejectLinks(path);
            if (!IdPattern().IsMatch(id)) continue;
            if (File.GetLastWriteTimeUtc(path) < now.UtcDateTime.AddHours(-1))
            {
                File.Delete(path);
                continue;
            }
            DirectoryResponse? response;
            try { response = JsonSerializer.Deserialize<DirectoryResponse>(CertbotFiles.ReadBounded(path, 4096), DomainOnboardingStore.Json); }
            catch (JsonException) { continue; }
            if (response is not { SchemaVersion: 1 } || response.Id != id) continue;
            // A success is shown once the published directory includes it.
            var state = !response.Success ? "failed" : snapshot is not null && snapshot.CheckedAt >= response.CheckedAt ? "done" : "pending";
            if (response is { Success: true, Action: "deleteUser", Target: { } username }
                && snapshot?.Users.All(user => user.Username != username) == true)
                await sshKeys.RemoveUser(username, ct);
            var requestedAt = DateTime.TryParseExact(id[..17], "yyyyMMddHHmmssfff", null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
                ? new DateTimeOffset(at, TimeSpan.Zero) : response.CheckedAt;
            changes.Add(new(id, response.Action, response.Target, state, response.Message, requestedAt));
        }
        return [.. changes.OrderByDescending(change => change.Id, StringComparer.Ordinal)];
    }

    private static HardwareOnboardingException Invalid(string message) => new(400, "invalid_directory_change", message);

    private static string Name(string? value, string what) =>
        value is not null && NamePattern().IsMatch(value) ? value
            : throw Invalid($"Enter a {what} of 1–32 lowercase letters, digits, - or _, starting with a letter.");

    private static string Text(string? value, int limit, string what, bool required)
    {
        var text = value?.Trim() ?? "";
        if (text.Length > limit || (value ?? "").Any(char.IsControl) || required && text.Length == 0)
            throw Invalid($"Enter a {what} of up to {limit} characters.");
        return text;
    }

    private static string[] Names(string[]? values, int limit, string what)
    {
        if (values is null || values.Length > limit || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw Invalid("Choose each item once.");
        return [.. values.Select(value => Name(value, what)).Order(StringComparer.Ordinal)];
    }

    internal static string Password(string? value)
    {
        if (value is not { Length: >= 14 and <= 256 } || value.Any(char.IsControl) || !value.Any(char.IsAsciiLetterUpper)
            || !value.Any(char.IsAsciiLetterLower) || !value.Any(char.IsAsciiDigit) || !value.Any(Symbols.Contains))
            throw Invalid("Passwords need 14 or more characters, with an uppercase letter, a lowercase letter, a digit and a symbol.");
        return value;
    }
}

public static class PeopleDirectoryEndpoints
{
    public static void MapPeopleDirectory(this WebApplication app)
    {
        var group = app.MapGroup("/api/host/directory").WithTags("People and groups")
            .RequireAuthorization("HostOwner").AddEndpointFilter<HardwareOnboardingErrorFilter>();
        group.MapGet("", (HttpContext context, PeopleDirectory directory, CancellationToken ct) =>
            directory.View(context.User.FindFirst("preferred_username")?.Value, ct));
        group.MapPost("/changes", async (HttpContext context, PeopleDirectory directory, CancellationToken ct) =>
        {
            // Owner API keys carry no directory username, so they can't be checked against self-lockout.
            var actor = context.User.FindFirst("preferred_username")?.Value
                ?? throw new HardwareOnboardingException(403, "browser_session_required", "Sign in to Lucia in a browser to manage people.");
            if (!context.Request.HasJsonContentType() || context.Request.ContentLength > 32000)
                throw new HardwareOnboardingException(400, "invalid_body", "Send one change as JSON.");
            var request = await context.Request.ReadFromJsonAsync<DirectoryRequest>(HardwareOnboardingJson.Options, ct)
                ?? throw new HardwareOnboardingException(400, "invalid_body", "Send one change as JSON.");
            return Results.Accepted(value: await directory.Queue(actor, request, ct));
        });
    }
}
