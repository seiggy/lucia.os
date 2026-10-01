using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using OpenTelemetry;

namespace Lucia.Homelab.Server.Assistant;

/// <summary>What the chat bar shows: connected, a pending code to enter on GitHub, disconnected, or how the last attempt ended.</summary>
public sealed record GitHubStatus(string State, string? Login = null, string? UserCode = null, string? VerificationUri = null,
    int? Interval = null, string? Message = null);

/// <summary>
/// Signs each owner in to GitHub through the device flow of Lucia's GitHub App, so the assistant runs on their Copilot plan.
/// Tokens never reach the browser: they are encrypted per owner on the host and renewed here before they expire.
/// </summary>
public sealed partial class GitHubSignIn(IOptions<AssistantOptions> options, IDataProtectionProvider protection,
    ILogger<GitHubSignIn> logger, HttpClient http, TimeProvider time) : IDisposable
{
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";
    private static readonly Uri DeviceUri = new("https://github.com/login/device/code");
    private static readonly Uri TokenUri = new("https://github.com/login/oauth/access_token");
    private static readonly Uri UserUri = new("https://api.github.com/user");
    private static readonly TimeSpan Linger = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, Flow> _flows = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>No redirects, cookies, proxy or retries: a replayed refresh would spend the owner's one-time refresh token.</summary>
    public static HttpClient CreateClient() => new(CloudflareHttp.CreateHandler())
    {
        Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 65_536
    };

    public static AssistantException NotConnected() => new(503, "assistant_not_connected", "Sign in with GitHub to use the assistant.");

    public GitHubStatus Status(string owner)
    {
        var flow = _flows.GetValueOrDefault(Check(owner));
        if (flow?.Status is { State: "pending" } pending) return pending;
        if (Read(owner) is { } credential && Usable(credential)) return new("connected", credential.Login);
        return flow?.Status ?? new("disconnected");
    }

    public async Task<GitHubStatus> StartAsync(string owner, CancellationToken ct)
    {
        // A second tab or a double click gets the code already on screen, so the code the owner types is the one Lucia waits for.
        if (_flows.GetValueOrDefault(Check(owner)) is { Status.State: "pending" } current && current.Expires - time.GetUtcNow() > TimeSpan.FromMinutes(1))
            return current.Status;
        JsonElement reply;
        try { reply = await PostAsync(DeviceUri, new() { ["client_id"] = options.Value.GitHubClientId }, ct); }
        catch (Exception e) when (!ct.IsCancellationRequested && Transient(e))
        {
            logger.LogWarning("Starting a GitHub sign-in failed ({ErrorType}).", e.GetType().Name);
            throw new AssistantException(503, "github_unavailable", "Lucia could not reach GitHub. Check the host's internet connection and try again.");
        }
        if (Text(reply, "error") is { } error)
        {
            logger.LogWarning("GitHub would not start a sign-in ({Error}).", Code(error));
            throw error == "device_flow_disabled"
                ? new AssistantException(503, "github_device_flow_disabled", "Device sign-in is turned off for Lucia's GitHub App.")
                : new AssistantException(502, "github_refused", "GitHub would not start a sign-in. Try again in a few minutes.");
        }
        var seconds = Number(reply, "expires_in");
        if (Text(reply, "device_code") is not { Length: > 0 and <= 512 } device || Text(reply, "user_code") is not { } code
            || !UserCodePattern().IsMatch(code) || !Uri.TryCreate(Text(reply, "verification_uri"), UriKind.Absolute, out var page)
            || page.Scheme != Uri.UriSchemeHttps || page.Host != "github.com" || seconds is not (>= 60 and <= 3600))
            throw new AssistantException(502, "github_refused", "GitHub sent an unexpected sign-in reply. Try again.");
        var flow = new Flow(device, time.GetUtcNow().AddSeconds(seconds.Value),
            new("pending", UserCode: code, VerificationUri: page.AbsoluteUri, Interval: (int)Math.Clamp(Number(reply, "interval") ?? 5, 1, 60)));
        _flows.AddOrUpdate(owner, flow, (_, old) => { old.Stop.Cancel(); return flow; });
        _ = Task.Run(() => PollAsync(owner, flow));
        return flow.Status;
    }

    /// <summary>Stops waiting for a code. Once this returns, that code can no longer sign the owner in.</summary>
    public async Task CancelAsync(string owner)
    {
        if (_flows.TryRemove(Check(owner), out var flow)) flow.Stop.Cancel();
        var gate = Gate(owner);
        await gate.WaitAsync();
        gate.Release();
    }

    /// <summary>Forgets the owner's tokens on this host. GitHub keeps the app authorized until they revoke it in their settings.</summary>
    public async Task DisconnectAsync(string owner)
    {
        if (_flows.TryRemove(Check(owner), out var flow)) flow.Stop.Cancel();
        var gate = Gate(owner);
        await gate.WaitAsync();
        try { Delete(owner); }
        finally { gate.Release(); }
        logger.LogInformation("An owner disconnected GitHub from the assistant.");
    }

    /// <summary>The owner's access token, renewed once less than an hour remains; null when they need to sign in.</summary>
    public async Task<string?> TokenAsync(string owner, CancellationToken ct)
    {
        if (Read(Check(owner)) is not { } credential) return null;
        if (Fresh(credential)) return credential.AccessToken;
        var gate = Gate(owner);
        await gate.WaitAsync(ct);
        try
        {
            // Another request may have renewed it while this one waited.
            if (Read(owner) is not { } current) return null;
            if (Fresh(current)) return current.AccessToken;
            var now = time.GetUtcNow();
            var valid = current.ExpiresAt > now;
            if (current.RefreshToken is null || current.RefreshExpiresAt <= now)
            {
                if (valid) return current.AccessToken;
                logger.LogInformation("An owner's GitHub sign-in expired; they must sign in again.");
                Delete(owner);
                return null;
            }
            JsonElement reply;
            // Not cancellable by the caller: GitHub retires the old tokens as it answers, so the answer must be kept.
            try
            {
                reply = await PostAsync(TokenUri, new()
                {
                    ["client_id"] = options.Value.GitHubClientId, ["grant_type"] = "refresh_token", ["refresh_token"] = current.RefreshToken
                }, CancellationToken.None);
            }
            catch (Exception e) when (Transient(e))
            {
                logger.LogWarning("Renewing a GitHub sign-in failed ({ErrorType}).", e.GetType().Name);
                return valid ? current.AccessToken
                    : throw new AssistantException(503, "github_unavailable", "Lucia could not reach GitHub to renew your sign-in. Try again shortly.");
            }
            if (Text(reply, "error") is not null || Issued(reply) is not { } renewed)
            {
                logger.LogWarning("GitHub would not renew a sign-in ({Error}); the owner must sign in again.", Code(Text(reply, "error")));
                Delete(owner);
                return null;
            }
            await WriteAsync(owner, renewed with { Login = current.Login });
            return renewed.AccessToken;
        }
        finally { gate.Release(); }
    }

    /// <summary>Turns Copilot refusing the owner's token into something they can act on; null for any other failure.</summary>
    public async Task<AssistantException?> ExplainAsync(string owner, Exception error)
    {
        if (!Refused(error)) return null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var gate = Gate(Check(owner));
        try { await gate.WaitAsync(timeout.Token); }
        catch (OperationCanceledException) { return null; }
        try
        {
            if (Read(owner) is not { } credential) return NotConnected();
            var (status, login) = await UserAsync(credential.AccessToken, timeout.Token);
            if (status == 401)
            {
                logger.LogWarning("GitHub no longer accepts an owner's sign-in; they must sign in again.");
                Delete(owner);
                return new(503, "github_signed_out", "GitHub ended Lucia's sign-in. Sign in with GitHub again.");
            }
            return status == 200
                ? new(503, "copilot_unavailable", $"Copilot did not accept {(login is null ? "this GitHub account" : "@" + login)}. Check that the account has GitHub Copilot, then sign in again.")
                : null;
        }
        catch (Exception e) when (Transient(e)) { return null; }
        finally { gate.Release(); }

        // The SDK wraps the CLI's "Failed to fetch Copilot user info: 401 Unauthorized: Bad credentials" in its own exceptions.
        static bool Refused(Exception? e) => e is not null && (e.Message.Contains("Copilot user info", StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains("Bad credentials", StringComparison.OrdinalIgnoreCase) || Refused(e.InnerException));
    }

    private async Task PollAsync(string owner, Flow flow)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(flow.Stop.Token, _stopping.Token);
        try
        {
            if (await ApprovalAsync(flow, stop.Token) is { } credential)
            {
                var gate = Gate(owner);
                await gate.WaitAsync(stop.Token);
                try
                {
                    stop.Token.ThrowIfCancellationRequested();
                    await WriteAsync(owner, credential);
                }
                finally { gate.Release(); }
                _flows.TryRemove(new(owner, flow));
                logger.LogInformation("An owner signed in to GitHub for the assistant.");
                return;
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { return; }
        catch (Exception e)
        {
            logger.LogWarning("A GitHub sign-in failed ({ErrorType}).", e.GetType().Name);
            flow.Status = new("error", Message: "Lucia could not finish the sign-in.");
        }
        // Keep how the sign-in ended on screen for a while, then forget it.
        try { await Task.Delay(Linger, time, stop.Token); }
        catch (OperationCanceledException) { }
        _flows.TryRemove(new(owner, flow));
    }

    /// <summary>Polls GitHub until the owner enters the code; null once the attempt ends another way.</summary>
    private async Task<Credential?> ApprovalAsync(Flow flow, CancellationToken ct)
    {
        var interval = flow.Status.Interval ?? 5;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), time, ct);
            if (time.GetUtcNow() >= flow.Expires) return End(flow, "expired_token");
            JsonElement reply;
            try
            {
                reply = await PostAsync(TokenUri, new()
                {
                    ["client_id"] = options.Value.GitHubClientId, ["device_code"] = flow.DeviceCode, ["grant_type"] = DeviceGrant
                }, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && Transient(e))
            {
                logger.LogDebug("Checking a GitHub sign-in failed ({ErrorType}); trying again.", e.GetType().Name);
                continue;
            }
            switch (Text(reply, "error"))
            {
                case "authorization_pending": continue;
                case "slow_down":
                    interval = (int)Math.Clamp(Number(reply, "interval") ?? interval + 5, 1, 60);
                    continue;
                case null when Issued(reply) is { } credential:
                    return credential with { Login = await LoginAsync(credential.AccessToken, ct) };
                case var error: return End(flow, error);
            }
        }
    }

    private Credential? End(Flow flow, string? error)
    {
        if (error is not ("expired_token" or "access_denied")) logger.LogWarning("GitHub refused a sign-in ({Error}).", Code(error));
        flow.Status = error switch
        {
            "expired_token" => new("expired", Message: "The code expired before it was entered on GitHub."),
            "access_denied" => new("denied", Message: "Access was declined on GitHub."),
            _ => new("error", Message: "GitHub could not finish the sign-in.")
        };
        return null;
    }

    private Credential? Issued(JsonElement reply)
    {
        if (Text(reply, "access_token") is not { } token || !TokenPattern().IsMatch(token)) return null;
        var refresh = Text(reply, "refresh_token") is { } value && TokenPattern().IsMatch(value) ? value : null;
        var now = time.GetUtcNow();
        return new(null, token, Number(reply, "expires_in") is > 0 and long expires ? now.AddSeconds(expires) : null, refresh,
            refresh is not null && Number(reply, "refresh_token_expires_in") is > 0 and long lasts ? now.AddSeconds(lasts) : null);
    }

    private async Task<string?> LoginAsync(string token, CancellationToken ct)
    {
        try { return await UserAsync(token, ct) is (200, var login) ? login : null; }
        catch (Exception e) when (!ct.IsCancellationRequested && Transient(e)) { return null; }
    }

    private async Task<(int Status, string? Login)> UserAsync(string token, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, UserUri);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var suppression = SuppressInstrumentationScope.Begin();
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode != System.Net.HttpStatusCode.OK) return ((int)response.StatusCode, null);
        var login = Text(Parse(await response.Content.ReadAsByteArrayAsync(ct)), "login");
        return (200, login is not null && LoginPattern().IsMatch(login) ? login : null);
    }

    private async Task<JsonElement> PostAsync(Uri uri, Dictionary<string, string> fields, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, uri);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = new FormUrlEncodedContent(fields);
        using var suppression = SuppressInstrumentationScope.Begin();
        using var response = await http.SendAsync(request, ct);
        // OAuth errors arrive as JSON, usually with a 200; a 5xx is an outage.
        if ((int)response.StatusCode >= 500) throw new HttpRequestException($"GitHub answered {(int)response.StatusCode}.");
        return Parse(await response.Content.ReadAsByteArrayAsync(ct));
    }

    private static HttpRequestMessage Request(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd("Lucia-Homelab");
        return request;
    }

    private static JsonElement Parse(byte[] body)
    {
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.Clone() : throw new JsonException("GitHub did not answer with a JSON object.");
    }

    private static bool Transient(Exception e) => e is HttpRequestException or JsonException or OperationCanceledException;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static string Code(string? error) => error is not null && ErrorPattern().IsMatch(error) ? error : "unknown";

    private bool Fresh(Credential credential) => credential.ExpiresAt is not { } expires || expires - time.GetUtcNow() > TimeSpan.FromHours(1);

    private bool Usable(Credential credential)
    {
        var now = time.GetUtcNow();
        return credential.ExpiresAt is not { } expires || expires > now || (credential.RefreshToken is not null && !(credential.RefreshExpiresAt <= now));
    }

    private string Saved(string owner) => Path.Combine(Path.GetFullPath(options.Value.Directory), "github", owner + ".json");

    // The owner is part of the purpose, so one owner's file copied over another's does not decrypt.
    private IDataProtector Protector(string owner) => protection.CreateProtector("Lucia.Homelab.AssistantGitHub.v1", owner);

    private Credential? Read(string owner)
    {
        var path = Saved(owner);
        try
        {
            if (!File.Exists(path)) return null;
            DomainOnboardingStore.RejectLinks(path);
            if (new FileInfo(path).Length > 65_536) throw new InvalidDataException("The saved sign-in is too large.");
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllBytes(path), Json);
            if (envelope is not { Version: 1, Data.Length: > 0 }) throw new InvalidDataException("The saved sign-in has an unknown format.");
            return JsonSerializer.Deserialize<Credential>(Protector(owner).Unprotect(envelope.Data), Json) is { } credential
                && TokenPattern().IsMatch(credential.AccessToken) ? credential : throw new InvalidDataException("The saved sign-in is incomplete.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidDataException)
        {
            logger.LogWarning("A saved GitHub sign-in could not be read ({ErrorType}); the owner must sign in again.", e.GetType().Name);
            return null;
        }
    }

    private Task WriteAsync(string owner, Credential credential) => DomainOnboardingStore.WriteJson(Saved(owner),
        new Envelope(1, Protector(owner).Protect(JsonSerializer.Serialize(credential, Json))), json: Json);

    private void Delete(string owner)
    {
        var path = Saved(owner);
        if (File.Exists(path)) File.Delete(path);
    }

    private SemaphoreSlim Gate(string owner) => _gates.GetOrAdd(owner, static _ => new SemaphoreSlim(1, 1));

    private static string Check(string owner) =>
        OwnerPattern().IsMatch(owner) ? owner : throw new ArgumentException("The owner key is invalid.", nameof(owner));

    public void Dispose()
    {
        _stopping.Cancel();
        http.Dispose();
    }

    private sealed class Flow(string deviceCode, DateTimeOffset expires, GitHubStatus status)
    {
        public string DeviceCode => deviceCode;
        public DateTimeOffset Expires => expires;
        public CancellationTokenSource Stop { get; } = new();
        public volatile GitHubStatus Status = status;
    }

    private sealed record Envelope(int Version, string Data);

    private sealed record Credential(string? Login, string AccessToken, DateTimeOffset? ExpiresAt, string? RefreshToken, DateTimeOffset? RefreshExpiresAt)
    {
        public override string ToString() => nameof(Credential);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex OwnerPattern();

    [GeneratedRegex("^[A-Za-z0-9-]{4,16}$")]
    private static partial Regex UserCodePattern();

    [GeneratedRegex("^[!-~]{1,4096}$")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("^[A-Za-z0-9-]{1,39}$")]
    private static partial Regex LoginPattern();

    [GeneratedRegex("^[a-z_]{1,40}$")]
    private static partial Regex ErrorPattern();
}
