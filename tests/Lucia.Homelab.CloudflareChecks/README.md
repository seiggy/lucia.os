# Cloudflare domain credentials: isolated checks and integration

Run from the repository root:

```powershell
dotnet run --project tests\Lucia.Homelab.CloudflareChecks\Lucia.Homelab.CloudflareChecks.csproj
```

The executable links only `Domains\Cloudflare*.cs`, the shared ASP.NET framework,
and the server's **already-restored** OpenTelemetry assemblies. It does not build
or load the server's GPU/native dependencies and adds no NuGet dependency. A normal
server dependency restore must previously have populated the package cache; the
assembly versions here track the server's pinned versions. All Cloudflare HTTP
responses are synthetic. Real Data Protection keys, private credential files,
and a loopback-only HTTP app are created below this check project and removed
after the run. No Cloudflare account, DNS mutation, Certbot invocation, or hostname
activation is used. Symlink checks run when Windows permits creating a symlink.

## Parent integration contract

Call `builder.AddCloudflareDomains()` and `app.MapCloudflareDomains()` in the
existing host behind its authentication and CSRF middleware. These are deliberately
not wired into Program/AppHost by this change. The namespace is
`Lucia.Homelab.Server.Domains`.

Configure the absolute private directory with
`CloudflareDomains:CredentialsDirectory` (`CloudflareDomainOptions`).
The default is user-local `Lucia/network-credentials`; a managed deployment can
set `/data/network-credentials`. The sole persistent record is
`cloudflare-domains.json`, protected with the host's existing Data Protection
provider and purpose `Lucia.Homelab.CloudflareDomainCredentials.v1`. Keep the
existing Data Protection keys persistent and private. The record and its directory
use 0600/0700 on Unix or a non-inheriting, current-user-only ACL on Windows.
Disconnect persists an encrypted explicit marker; environment credentials are never
consulted. Corrupt records fail closed; disconnect can replace a corrupt regular
file, but no operation follows a symlink/reparse point.

### HTTP API (HostOwner only, no-store)

Under `/api/host/domains/cloudflare`:

| Method | Route | Body / response |
| --- | --- | --- |
| GET | `/credentials` | `CloudflareCredentialStatus` |
| PUT | `/credentials` | Exactly `{"accountId":"32 hex characters","token":"opaque visible ASCII token"}`; returns status |
| DELETE | `/credentials` | Returns disconnected status |
| GET | `/zones` | Array of `CloudflareZone` |

PUT accepts JSON only, at most 8192 bytes (including chunked requests), and a
1–4096-character visible-ASCII token without assuming a prefix. Credentials never
come from the URL, query, browser storage or the Lucia bearer header. Provider
401/403 map to 422, not an application-login failure. Errors are
`{"error":{"code":"…","message":"safe guidance","retryAfterSeconds":null}}`;
429 retries are bounded to 1–3600 seconds when provided. Logs contain error
codes/types only. ASP.NET HTTP logging and inbound OpenTelemetry traces are
disabled on these routes; outbound provider instrumentation is suppressed.

### C# service / DTOs

All methods accept an optional final `CancellationToken`:

```csharp
Task<CloudflareCredentialStatus> GetStatusAsync();
Task<CloudflareCredentialStatus> SaveAsync(CloudflareCredentialRequest request);
Task<CloudflareCredentialStatus> DisconnectAsync();
Task<IReadOnlyList<CloudflareZone>> ListZonesAsync();
Task<CloudflareZone> GetZoneAsync(string zoneId);
Task<CloudflareToken> GetTokenAsync();
Task<IReadOnlyList<CloudflareChallengeRecord>> ListChallengeRecordsAsync(
    string zoneId, IReadOnlyCollection<string> names);
```

* `CloudflareCredentialRequest(accountId, token)` has `AccountId` and a
  JSON-ignored `Token`; HTTP parsing is explicit, not model binding.
* Status JSON is `{configured, accountId, verifiedAt, expiresAt, zoneCount}`.
  `zoneCount` is the count at successful credential verification, not a live cache.
  An expired stored connection remains configured in status, but token/zone use
  rejects expiry with actionable 422 guidance.
* Zone JSON is `{id, name, status, nameServers: string[]}`; only active, full
  zones belonging to the selected account are accepted. Names use canonical
  lowercase IDNA with label/boundary validation. Account names and other private
  zone metadata are not returned.
* `CloudflareToken` is a server-only class: `AccountId`, `[JsonIgnore] Token`,
  and a non-secret `ToString()`. Disconnected returns `Token = null`.
  Never expose it as an API response, command argument, log, or browser value.
* `CloudflareChallengeRecord` is server-only `{Id, Name, Type, Content}`. The
  helper validates the zone again, accepts 1–64 exact names inside it, and uses
  exact-name filters with bounded complete pagination. It returns no broad DNS
  listing, and never adopts, deletes or writes records. Include challenge names
  and relevant ancestors to detect CNAME/NS delegation. The parent must separately
  check public DNS authority/delegation.

Save verifies the candidate and completely lists accessible zones **before**
atomically replacing the old connection. Selected-zone apply must call
`GetZoneAsync` again and compare the returned canonical name with the parent’s
plan. Lists reject inconsistent/incomplete pagination and anything beyond
20 pages / 1000 entries instead of truncating. Requests have fixed Cloudflare HTTPS
origin, native TLS, no cookies/proxy/redirects, a 15-second request/body deadline,
a 1,000,000-byte body limit, and a 45-second service-operation deadline.

The parent must coordinate connection replacement/disconnect with active domain
jobs. This service does not own Certbot, staging gates, certificates, DNS writes,
registrar changes, or hostname activation.

## Suggested permissions/help copy

> In Cloudflare, choose **Manage Account → Account API Tokens** and create an
> account-owned API token. Creating account tokens requires the account's
> **Super Administrator** role. Grant the token only **Zone → DNS → Edit** and
> **Zone → Zone → Read**, restricted to the **specific selected zone**.
> No registrar, account-administration, or account-wide DNS permission is needed.
> Cloudflare must already be authoritative DNS for an active full-setup zone;
> transferring the domain's registration to Cloudflare is not necessary.
>
> Connecting verifies the token's active status and validity dates using
> `/accounts/{account_id}/tokens/verify` and discovers accessible zones.
> This does **not** prove DNS-write permission. Before activation, Lucia must
> separately pass a real Certbot **staging DNS-01** challenge. That gate also
> catches provider/plugin incompatibilities such as different `per_page` minimums.

Contract evidence supplied with the task: Cloudflare OpenAPI revision
`d92075c0a1e3efd10d7b7a19e44e4cfa1f4eb44f` and docs revision
`f491caea8555a2df0f4805e02ca4f172356a0c08`.
