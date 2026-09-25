using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Host;

public sealed class HostApiKeyAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<HostPlatformOptions> platform,
    InferenceApiKeyStore? inferenceKeys = null) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "HostApiKey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Path.StartsWithSegments("/api/boot"))
            return Task.FromResult(AuthenticateResult.NoResult());
        var values = Request.Headers.Authorization;
        if (values.Count != 1 || values[0] is not { } authorization
            || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());
        var presented = authorization["Bearer ".Length..];
        var role = GetRole(presented, platform.Value);
        var managedId = role is null && presented.StartsWith(InferenceApiKeyStore.Prefix, StringComparison.Ordinal)
            ? inferenceKeys?.Match(presented) : null;
        if (managedId is not null)
            role = "Inference";
        if (role is null)
        {
            Logger.LogWarning("Rejected host API authentication for {Path}", Request.Path);
            return Task.FromResult(AuthenticateResult.Fail("Invalid host API key."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, managedId?.ToString("D") ?? (role == "Owner" ? "host-owner" : "inference-client")),
                new Claim(ClaimTypes.Role, role)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    public static string? GetRole(string presented, HostPlatformOptions platform) =>
        Matches(presented, platform.ApiKey) ? "Owner"
            : Matches(presented, platform.InferenceApiKey) ? "Inference" : null;

    public static bool Matches(string presented, string? expected) =>
        expected is { Length: >= 32 } && presented.Length <= 4096
        && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        await Response.WriteAsJsonAsync(new
        {
            error = new { message = "A valid host API bearer token is required.", type = "authentication_error", code = "invalid_api_key" }
        }, Context.RequestAborted);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        HostAuthentication.WriteErrorAsync(Context, StatusCodes.Status403Forbidden, "Access denied.", "access_denied");
}
