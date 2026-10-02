using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Lucia.Homelab.Server.Host;

public static class HostAuthentication
{
    public const string Scheme = "Host";
    public const string CookieScheme = "HostCookie";
    public const string OidcScheme = "HostOidc";
    public const string JwtScheme = "HostJwt";
    public const string RoleClaim = "lucia_role";
    public const string ApiScope = "lucia_api";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    public static void AddHostAuthentication(this WebApplicationBuilder builder)
    {
        var settings = builder.Configuration.GetSection("HostAuthentication").Get<HostAuthenticationOptions>() ?? new();
        settings.Validate(builder.Environment.IsDevelopment());
        builder.Services.AddSingleton(settings);
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("HostOwner", policy => policy.RequireAuthenticatedUser().RequireRole("Owner"))
            .AddPolicy("HostInference", policy => policy.RequireAuthenticatedUser().RequireRole("Owner", "Inference"));
        var authentication = builder.Services.AddAuthentication(Scheme)
            .AddPolicyScheme(Scheme, Scheme, options => options.ForwardDefaultSelector = context =>
                SelectScheme(context.Request, settings.Enabled,
                    context.RequestServices.GetRequiredService<IOptions<HostPlatformOptions>>().Value))
            .AddScheme<AuthenticationSchemeOptions, HostApiKeyAuthentication>(HostApiKeyAuthentication.SchemeName, null);
        if (!settings.Enabled)
            return;

        var secret = settings.ReadClientSecret();
        var root = settings.ReadCertificateAuthority();
        builder.Services.AddSingleton(root);
        try { Directory.CreateDirectory(settings.DataProtectionKeysDirectory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new OptionsValidationException(nameof(HostAuthenticationOptions), typeof(HostAuthenticationOptions),
                ["HostAuthentication: DataProtectionKeysDirectory cannot be created. Check the persistent volume and service permissions."]);
        }
        builder.Services.AddDataProtection().SetApplicationName("Lucia.Homelab.Host")
            .PersistKeysToFileSystem(new DirectoryInfo(settings.DataProtectionKeysDirectory));
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "__Host-Lucia.Csrf";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
        });
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var network in settings.TrustedProxyNetworks)
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            options.AllowedHosts.Clear();
            foreach (var host in settings.AdditionalPublicOrigins.Prepend(settings.PublicOrigin)
                .Select(value => new Uri(value).Host).Distinct(StringComparer.OrdinalIgnoreCase))
                options.AllowedHosts.Add(host);
        });

        authentication.AddCookie(CookieScheme, options =>
        {
            options.Cookie.Name = "__Host-Lucia.Session";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = SessionLifetime;
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context =>
                WriteErrorAsync(context.HttpContext, 401, "Sign in to access Lucia.", "authentication_required");
            options.Events.OnRedirectToAccessDenied = context =>
                WriteErrorAsync(context.HttpContext, 403, "Access denied.", "access_denied");
        });
        authentication.AddOpenIdConnect(OidcScheme, options =>
        {
            options.Authority = settings.Authority;
            options.ClientId = settings.ClientId;
            options.ClientSecret = secret;
            options.SignInScheme = CookieScheme;
            options.SignOutScheme = CookieScheme;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.UsePkce = true;
            options.RequireHttpsMetadata = true;
            options.CallbackPath = "/signin-oidc";
            options.SignedOutCallbackPath = "/signout-callback-oidc";
            options.SignedOutRedirectUri = settings.PublicOrigin + "/";
            options.RemoteSignOutPath = PathString.Empty;
            options.Scope.Clear();
            foreach (var scope in new[] { "openid", "profile", "email", ApiScope })
                options.Scope.Add(scope);
            options.MapInboundClaims = false;
            options.TokenValidationParameters = ValidationParameters(settings);
            options.UseTokenLifetime = false;
            options.SaveTokens = false;
            options.DisableTelemetry = true;
            options.BackchannelHttpHandler = HostAuthenticationOptions.CreateBackchannelHandler(root, settings.AuthorityUsesSystemTrust);
            options.NonceCookie.Name = "__Host-Lucia.Nonce.";
            options.NonceCookie.Path = "/";
            options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
            options.CorrelationCookie.Name = "__Host-Lucia.Correlation.";
            options.CorrelationCookie.Path = "/";
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Events.OnRedirectToIdentityProvider = context =>
            {
                context.ProtocolMessage.RedirectUri = settings.PublicOrigin + options.CallbackPath;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToIdentityProviderForSignOut = context =>
            {
                context.ProtocolMessage.PostLogoutRedirectUri = settings.PublicOrigin + options.SignedOutCallbackPath;
                return Task.CompletedTask;
            };
            options.Events.OnTokenValidated = context =>
            {
                // Retain only the encrypted id_token_hint required for ordinary OIDC logout.
                context.Properties ??= new AuthenticationProperties();
                context.Properties.StoreTokens([new AuthenticationToken
                {
                    Name = "id_token",
                    Value = context.TokenEndpointResponse?.IdToken ?? context.ProtocolMessage.IdToken
                }]);
                context.Properties.IssuedUtc = DateTimeOffset.UtcNow;
                context.Properties.ExpiresUtc = DateTimeOffset.UtcNow + SessionLifetime;
                context.Properties.IsPersistent = true;
                context.Properties.AllowRefresh = false;
                return Task.CompletedTask;
            };
            options.Events.OnRemoteFailure = context =>
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("HostAuthentication")
                    .LogWarning("OIDC sign-in failed ({FailureType}). Check provider registration, CA trust, and callback configuration.",
                        context.Failure?.GetType().Name);
                context.HandleResponse();
                return WriteErrorAsync(context.HttpContext, 401, "Sign-in failed. Try again or contact the host owner.", "signin_failed");
            };
        });
        authentication.AddJwtBearer(JwtScheme, options =>
        {
            options.Authority = settings.Authority;
            options.Audience = settings.ClientId;
            options.RequireHttpsMetadata = true;
            options.MapInboundClaims = false;
            options.IncludeErrorDetails = false;
            options.SaveToken = false;
            options.BackchannelHttpHandler = HostAuthenticationOptions.CreateBackchannelHandler(root, settings.AuthorityUsesSystemTrust);
            options.TokenValidationParameters = ValidationParameters(settings);
            options.Events.OnTokenValidated = context =>
            {
                // Authentik adds uid/azp only to access tokens; nonce can appear on either token type.
                if (context.Principal is null || !context.Principal.FindAll("scope")
                    .Any(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(ApiScope, StringComparer.Ordinal))
                    || context.Principal.FindFirstValue("azp") != settings.ClientId
                    || string.IsNullOrWhiteSpace(context.Principal.FindFirstValue("uid")))
                    context.Fail("A Lucia API access token with lucia_api scope is required.");
                return Task.CompletedTask;
            };
            options.Events.OnAuthenticationFailed = context =>
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("HostAuthentication")
                    .LogWarning("Rejected API access token ({FailureType}) for {Path}.", context.Exception.GetType().Name, context.Request.Path);
                return Task.CompletedTask;
            };
            options.Events.OnChallenge = context =>
            {
                context.HandleResponse();
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return WriteErrorAsync(context.HttpContext, 401, "A valid Lucia API access token is required.", "invalid_token");
            };
            options.Events.OnForbidden = context =>
                WriteErrorAsync(context.HttpContext, 403, "Access denied.", "access_denied");
        });
    }

    private static TokenValidationParameters ValidationParameters(HostAuthenticationOptions settings) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = settings.Authority,
        IssuerValidator = (issuer, _, _) => string.Equals(issuer, settings.Authority, StringComparison.Ordinal)
            ? issuer : throw new SecurityTokenInvalidIssuerException("Unexpected Lucia token issuer."),
        ValidateAudience = true,
        ValidAudience = settings.ClientId,
        ValidateIssuerSigningKey = true,
        RequireSignedTokens = true,
        RequireExpirationTime = true,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        NameClaimType = "preferred_username",
        RoleClaimType = RoleClaim
    };

    public static string SelectScheme(HttpRequest request, bool enabled, HostPlatformOptions platform)
    {
        // Boot endpoints validate their own short-lived device proof/capability, not browser cookies or human JWTs.
        if (request.Path.StartsWithSegments("/api/boot"))
            return HostApiKeyAuthentication.SchemeName;
        if (!request.Headers.ContainsKey("Authorization"))
            return enabled ? CookieScheme : HostApiKeyAuthentication.SchemeName;
        var values = request.Headers.Authorization;
        if (values.Count != 1 || values[0] is not { } value || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return HostApiKeyAuthentication.SchemeName;
        var token = value["Bearer ".Length..];
        if (HostApiKeyAuthentication.GetRole(token, platform) is not null)
            return HostApiKeyAuthentication.SchemeName;
        if (token.StartsWith(InferenceApiKeyStore.Prefix, StringComparison.Ordinal))
            return HostApiKeyAuthentication.SchemeName;
        return enabled ? JwtScheme : HostApiKeyAuthentication.SchemeName;
    }

    public static void UseHostProxy(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<HostAuthenticationOptions>();
        if (!settings.Enabled)
            return;
        app.UseForwardedHeaders();
        var origins = settings.AdditionalPublicOrigins.Prepend(settings.PublicOrigin).Select(value => new Uri(value)).ToArray();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/health/live")
            {
                await next(context);
                return;
            }
            // The assistant's Copilot CLI runs in this container and reaches its OpenAI API over loopback HTTP.
            // Traefik connects through Docker's network, never loopback; the API key is still required.
            if (context.Connection.LocalIpAddress is { } local
                && System.Net.IPAddress.IsLoopback(local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local)
                && context.Request.Path.StartsWithSegments("/v1")
                && context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }
            // Cookie-free LAN discovery: the Debian installer appends its DHCP domain to the boot host,
            // so it arrives under a LAN alias. Its own network and capability checks still apply.
            if (context.Request.IsHttps && context.Request.Path.StartsWithSegments("/api/boot"))
            {
                await next(context);
                return;
            }
            if (!context.Request.IsHttps || !origins.Any(origin => MatchesOrigin(context.Request, origin)))
            {
                await WriteErrorAsync(context, 400, "Use the configured public HTTPS origin.", "invalid_origin");
                return;
            }
            await next(context);
        });
    }

    public static void UseHostCsrf(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<HostAuthenticationOptions>().Enabled)
            return;
        app.Use(async (context, next) =>
        {
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
                && !HttpMethods.IsOptions(context.Request.Method) && !HttpMethods.IsTrace(context.Request.Method)
                && (await context.AuthenticateAsync(Scheme)).Ticket?.AuthenticationScheme == CookieScheme)
            {
                try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException)
                {
                    context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("HostAuthentication")
                        .LogWarning("Rejected cookie request with invalid antiforgery token for {Path}.", context.Request.Path);
                    await WriteErrorAsync(context, 403, "Refresh the session and supply a valid CSRF token.", "invalid_csrf_token");
                    return;
                }
            }
            await next(context);
        });
    }

    public static void MapHostAuthentication(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<HostAuthenticationOptions>();
        app.MapGet("/api/auth/session", async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var result = await context.AuthenticateAsync(Scheme);
            var authenticated = result.Succeeded;
            var owner = authenticated && context.User.IsInRole("Owner");
            var canAccess = owner || (authenticated && context.User.IsInRole("Inference"));
            var csrfToken = authenticated && result.Ticket?.AuthenticationScheme == CookieScheme
                ? context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context).RequestToken : null;
            return Results.Json(new
            {
                enabled = settings.Enabled,
                authenticated,
                username = authenticated ? context.User.FindFirstValue("preferred_username") ?? context.User.Identity?.Name
                    ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier) : null,
                displayName = authenticated ? context.User.FindFirstValue("name") ?? context.User.Identity?.Name : null,
                isOwner = owner,
                canAccess,
                csrfToken
            });
        }).AllowAnonymous();
        app.MapGet("/auth/login", (HttpContext context, string? returnUrl) =>
        {
            if (!settings.Enabled)
                return Results.NotFound(new { error = "Browser sign-in is disabled in Development." });
            if (!IsLocalReturnUrl(returnUrl))
                return Results.BadRequest(new { error = "returnUrl must be a local path beginning with a single slash." });
            // Correlation/nonce cookies must be issued on the canonical host, not on the API alias.
            if (!MatchesOrigin(context.Request, new Uri(settings.PublicOrigin)))
                return Results.Redirect(settings.PublicOrigin + "/auth/login"
                    + (returnUrl is null ? "" : QueryString.Create("returnUrl", returnUrl).Value));
            return Results.Challenge(new AuthenticationProperties
            {
                RedirectUri = settings.PublicOrigin + (returnUrl ?? "/")
            }, [OidcScheme]);
        }).AllowAnonymous();
        app.MapPost("/auth/logout", async (HttpContext context) =>
        {
            if (!settings.Enabled)
                return Results.NotFound(new { error = "Browser sign-in is disabled in Development." });
            if ((await context.AuthenticateAsync(Scheme)).Ticket?.AuthenticationScheme != CookieScheme)
                return Results.Json(new { error = "A browser session is required." }, statusCode: 403);
            return Results.SignOut(new AuthenticationProperties { RedirectUri = settings.PublicOrigin + "/" }, [CookieScheme, OidcScheme]);
        }).RequireAuthorization();
    }

    private static bool MatchesOrigin(HttpRequest request, Uri origin) =>
        string.Equals(request.Host.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
        && (request.Host.Port ?? 443) == origin.Port;

    public static bool IsLocalReturnUrl(string? value)
    {
        if (value is null)
            return true;
        if (value.Length == 0 || value[0] != '/' || value.StartsWith("//", StringComparison.Ordinal)
            || value.Contains('\\') || value.Any(char.IsControl))
            return false;
        // Reject encoded authority/backslash/control characters, including nested encoding.
        var decoded = Uri.UnescapeDataString(value);
        return !decoded.StartsWith("//", StringComparison.Ordinal) && !decoded.Contains('\\')
            && !decoded.Any(char.IsControl) && !decoded.Contains('%');
    }

    public static Task WriteErrorAsync(HttpContext context, int status, string message, string code)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(new { error = new { message, type = "authentication_error", code } }, context.RequestAborted);
    }
}
