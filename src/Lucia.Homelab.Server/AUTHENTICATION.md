# Host authentication

Managed hosting requires `HostAuthentication:Enabled=true`. Disabled SSO is
accepted only in `Development`, where existing `HostPlatform:ApiKey` and
`HostPlatform:InferenceApiKey` behavior remains available.

## Configuration

| `HostAuthentication` field | Required value when enabled |
| --- | --- |
| `Enabled` | `true` |
| `Authority` | Exact HTTPS Authentik issuer, including its trailing slash, e.g. `https://identity.example:9443/application/o/lucia/` |
| `ClientId` | Managed confidential OIDC client ID; also the required API-token audience |
| `ClientSecretFile` | Absolute path to a readable secret file; trailing CR/LF is removed |
| `PublicOrigin` | Exact public HTTPS origin, e.g. `https://identity.example` |
| `CaCertificatePath` | Absolute path to the installer-provided public PEM CA certificate, never a private key |
| `DataProtectionKeysDirectory` | Absolute persistent directory writable by the host service |
| `TrustedProxyNetworks` | Explicit isolated Docker network CIDRs within RFC1918 IPv4 or IPv6 ULA; no public, default-route, or loopback trust |

Use environment keys such as `HostAuthentication__ClientId` and
`HostAuthentication__TrustedProxyNetworks__0` for container configuration.
Existing machine keys remain separate from Authentik credentials; never embed
them in SPA configuration.

Register **only** `${PublicOrigin}/signin-oidc` and
`${PublicOrigin}/signout-callback-oidc` with Authentik. The authorization-code
flow uses PKCE and requests `openid profile email lucia_api`. Tokens must use the
configured issuer/audience and RS256 signatures. Map `lucia_role` to `Owner` or
`Inference` using trusted group membership, not editable profile attributes.
Enable Authentik's `include_claims_in_id_token` so the browser cookie receives
the profile and `lucia_role` claims without a user-info round trip.
Only access tokens carry `scope=lucia_api`; do not add that scope claim to ID
tokens. API JWTs also require Authentik's native access-token `uid` and `azp`
(`azp` must equal `ClientId`); ID tokens lack those access-token markers.
Authentik access tokens may themselves contain a nonce, so nonce is not used
as a token-type discriminator. Tokens with invalid issuer, audience, signature,
or lifetime are rejected.
The access-token discriminator follows Authentik's
[`IDToken.to_access_token`](https://github.com/goauthentik/authentik/blob/main/authentik/providers/oauth2/id_token.py);
no additional property mapping is needed for `uid` or `azp`.

Metadata, JWKS, and token backchannels trust only the supplied custom root,
retain native TLS hostname, validity, signature-chain, and server-EKU checks.
The CA contract provides no CRL/OCSP service, so online certificate revocation is not
checked; revoke that trust operationally by replacing the trusted CA file and
restarting the host. Backchannels do not follow HTTP
redirects. Configure the final issuer URL and serve a complete certificate
chain. Invalid/incomplete configuration fails startup rather than disabling SSO.

## HTTP contract

`GET /api/auth/session` is anonymous JSON 200, with `Cache-Control: no-store`:

```json
{"enabled":true,"authenticated":false,"username":null,"displayName":null,"isOwner":false,"canAccess":false,"csrfToken":null}
```

Authenticated profiles populate `username` (preferred username), `displayName`
(name), `isOwner`, and `canAccess`. Only a cookie session receives `csrfToken`.
No API key, access token, refresh token, or ID token is returned.

`GET /auth/login?returnUrl=/local/path` starts browser navigation to Authentik.
Omitting `returnUrl` selects `/`; non-local or ambiguous encoded paths get 400.
Redirect/callback URLs are constructed from `PublicOrigin`, never arbitrary Host
headers.

`POST /auth/logout` requires the browser session and its antiforgery token, via
`X-CSRF-TOKEN` or form field `__RequestVerificationToken`. It clears the local
cookie and redirects through normal OIDC end-session with an encrypted
cookie-held `id_token_hint`. Successful logout returns to `${PublicOrigin}/`.
GET logout is not supported.

All unsafe cookie-authenticated requests require antiforgery validation before
application work, including streaming `/v1/chat/completions`. API keys and JWTs
do not require CSRF. Any explicit Authorization header takes precedence over
cookies; invalid credentials cannot fall back to a browser session. Exact opaque
API keys win over JWT selection even when they contain dots.

## Application inference keys

Owners manage named keys through `GET/POST /api/host/inference-keys` and
`DELETE /api/host/inference-keys/{id}`. Cookie mutations retain the normal CSRF
requirement. POST accepts a name and optional `expiresAt`; omission/null means
no expiry. Creation returns `{key,secret}` once with `Cache-Control: no-store`.
List responses contain only metadata and a nonsecret hint. Revocation removes
the key from the active store after the change is persisted.

Generated `lucia_inf_` secrets contain 256 random bits and authorize only
`Inference`. The host stores SHA-256 hashes in a private, atomic, exclusively
leased file under `InferenceKeys:Directory` (`/data/inference-keys` in managed
deployment). Expired/revoked keys cannot fall back to a valid browser cookie.
Revocation applies to subsequent requests; it does not forcibly cancel an
already-running inference request. Existing installation-configured owner and
inference keys remain supported separately and cannot be recovered or rotated
through this page.

Hugging Face provider credentials are different from Lucia inference keys.
Their Owner-only endpoints are under `/api/host/huggingface`; saved tokens are
protected with the existing persistent Data Protection keys and never returned
by credential-status APIs. Removing a token persists an explicit disconnected
marker and disables implicit CLI credentials for future downloads.

## Resource authorization

`/api/host/*` model, host-status, and SRE operations require Owner. `/v1/*`
requires Owner or Inference. Authentication/authorization failures are JSON
401/403, never login-page redirects. Missing/invalid CSRF returns JSON 403.

Hardware onboarding controls under `/api/host/onboarding` and `/api/host/devices`
use the same Owner/CSRF boundary. The separate `/api/boot/*` discovery adapter
does not inherit browser-cookie, machine-key, or human-JWT authority. It verifies
its own network/window eligibility, single-use signed discovery challenge, and
device-specific short-lived capability. This first adapter is read-only and
exposes no installation-grant, LDAP-credential, or managed-enrollment endpoint.

`GET /api/host/telemetry` is Owner-only, including the rolling one-hour metric
history. It never accepts inference-only credentials and is not a public health
probe. Responses use `Cache-Control: no-store`; the bounded history lives only
in the host process and is discarded on restart.

`GET /health/live` is always anonymous and returns only `{"status":"ok"}`.
It checks HTTP process availability, **not** model readiness or IdP availability;
model startup failures remain on authenticated `/api/host/status`.

The server serves `wwwroot` and SPA navigation fallback from the same origin.
Unknown `/api`, `/v1`, `/auth`, health, and OIDC callback paths are not rewritten
to HTML. No CORS policy is enabled. Requests other than liveness must use the
configured HTTPS origin. Only the configured proxy CIDRs may forward scheme,
host, and client IP; at most one hop is processed and only the approved host is
accepted. Keep the container private on that network.

Without `ConnectionStrings:cache`, native in-memory OutputCache is used. A
configured Redis connection retains the existing Aspire Redis output cache.

## Session and operational limits

The Secure, HttpOnly, `__Host-Lucia.Session` cookie is SameSite=Lax and has an
eight-hour, non-sliding authentication lifetime. It is persistent, with a matching
browser expiry, allowing a browser or embedded view that retains its cookie store
to restore a still-valid sign-in after restarting. OIDC nonce/correlation cookies
are Secure, HttpOnly, SameSite=None for the form-post callback. Antiforgery uses
a separate Secure, HttpOnly, SameSite=Strict `__Host-` cookie.

Role changes, disabled users, and upstream token revocation do **not** invalidate
an already-issued host cookie immediately. There is no background token refresh,
cookie introspection, or back/front-channel logout listener. Reauthenticate after
the bounded session expires; explicit browser logout clears the local session.
JWTs are validated locally until their expiration (30-second clock skew).
Only the ID-token logout hint is retained, inside the protected cookie; access
and refresh tokens are not saved.

Identity setup and managed-application reconciliation also upgrade Authentik's
default `seconds=0` login-stage session to `hours=8`. This makes the IdP session
persistent too, without enabling a longer remember-me extension or terminating
other sessions. Explicit operator-configured durations are preserved. Existing
session-only cookies acquire persistence on the next successful sign-in; their
contents and lifetimes are not rewritten remotely.

If refresh preserves sign-in in a regular browser but not an embedded panel,
investigate the embedding application's cookie-store/profile lifecycle. Persistent
expiry cannot preserve cookies that the embedding application clears or isolates.
Do not work around this by exposing tokens to JavaScript or disabling authentication.

Persist and protect Data Protection files with service-account-only filesystem
permissions and an appropriately secured volume. ASP.NET does not automatically
encrypt that Linux volume at rest; the installer owns volume permissions and
storage encryption. Key loss invalidates sessions and outstanding OIDC states.

Dependencies added: `Microsoft.AspNetCore.Authentication.OpenIdConnect` and
`Microsoft.AspNetCore.Authentication.JwtBearer`, both **10.0.11**. Cookies,
antiforgery, Data Protection, TLS, and authorization use the ASP.NET/.NET shared
framework. No additional test package is needed.

Run the GPU-free middleware checks from the repository root:

```powershell
dotnet run --project .\tests\Lucia.Homelab.AuthChecks\Lucia.Homelab.AuthChecks.csproj
```

These use local synthetic certificates, signed JWTs, protected cookies, and real
Kestrel/authentication/antiforgery middleware. They do not validate a live
Authentik callback, group mapping, proxy deployment, or GPU/model readiness.

## Model-directory ownership during migration

Each new host acquires `.lucia-model-catalog.lease` inside its model root before
reading or recovering any manifests, retaining an exclusive `FileShare.None`
handle until catalog disposal (not merely `StopAsync`). Another owner fails
startup with an actionable error. Symlink/reparse-point leases are rejected;
the host never deletes the lease on release or deletes model data for recovery.
Keep the directory writable only by trusted administrators and the host service,
and never unlink or replace an active lease.

.NET 10.0.11 implements Unix `FileShare.None` with nonblocking advisory `flock`
([runtime source](https://github.com/dotnet/runtime/blob/v10.0.11/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs)).
Acquisition also probes a conflicting open and fails closed if locking is
disabled or unsupported by the filesystem. Old binaries do not participate:
explicitly stop the old development host before the first managed cutover.

Run the isolated, GPU-free lease check without starting the HTTP/model host:

```powershell
dotnet run --project .\src\Lucia.Homelab.Server\Lucia.Homelab.Server.csproj --no-launch-profile -- --check-model-lease
```
