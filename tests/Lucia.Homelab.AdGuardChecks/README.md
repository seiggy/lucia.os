# AdGuard connection capability

Run the console checks from the repository root:

```powershell
dotnet run --project tests\Lucia.Homelab.AdGuardChecks\Lucia.Homelab.AdGuardChecks.csproj --no-launch-profile
```

The checks use fake HTTP handlers, scripted socket streams, real ephemeral Data
Protection keys under this test directory, and a loopback Lucia test host. They
never contact AdGuard, load a model/GPU, or change real DNS records. The project
reuses the server's existing dependencies and shared ASP.NET framework; no new
packages are introduced. Windows directory-junction checks do not require
symlink privilege; file-symlink checks are reported as skipped when unavailable.

## Integration contract

Register `builder.AddAdGuardManagement()` and map `app.MapAdGuardManagement()`
behind the existing host authentication and global `UseHostCsrf()` middleware.
Configure `AdGuardManagement:CredentialsDirectory=/data/network-credentials` in
production and preserve the host's existing Data Protection key persistence.
These source files deliberately do not alter Program, AppHost or Settings UI.

`AdGuardConnectionService` exposes:

* `Task<AdGuardConnectionStatus> GetStatusAsync(CancellationToken = default)`
* `Task<AdGuardConnectionStatus> SaveAsync(AdGuardConnectionRequest, CancellationToken = default)`
* `Task<AdGuardConnectionStatus> VerifyAsync(CancellationToken = default)`
* `Task<AdGuardConnectionStatus> DisconnectAsync(CancellationToken = default)`

`AdGuardConnectionRequest` has `BaseUrl`, `Username`, `Password`,
`AllowInsecureHttp`. It is not a record; `ToString()` and serialization suppress
its password. HTTP PUT is manually bounded and parsed, not model-bound.

The Owner-only routes are GET/PUT/DELETE `/api/host/connections/adguard` and
POST `/api/host/connections/adguard/verify`. PUT requires exactly:

```json
{"baseUrl":"https://adguard.example:8443","username":"owner","password":"example-only","allowInsecureHttp":false}
```

All successful routes return the same public status:

```json
{"configured":true,"baseUrl":"https://adguard.example:8443","username":"owner","allowInsecureHttp":false,"version":"v0.107.79","lastVerifiedAt":"2026-09-23T14:00:00Z"}
```

The example address is not a default or a discovered address. Neither an API port
nor the user's existing AdGuard host is assumed. `configured` means credentials
were saved after verification, not that the server is currently reachable.
`lastVerifiedAt` is the last successful verification. A never-configured or
explicitly disconnected connection returns `configured:false`, null string/date
fields and `allowInsecureHttp:false`. Disconnect is persistent and **does not
remove remote rewrites**. Status/disconnect perform no remote access. Failed
save/verification does not replace the previous good record.

Errors are `{error:{code,message}}` with static messages, no remote body,
exception details or secrets. Responses are `no-store`. Codes include
`adguard_not_configured` (409), `adguard_access_denied` (403),
`adguard_rate_limited` (429), `adguard_timeout` (504),
`adguard_storage_unavailable` (503), and `invalid_adguard_response` (502).

## DNS workflow contract

DI resolves `ILocalDnsProvider` to that same singleton connection service:

* `GetConnectionAsync(CancellationToken = default)` returns the public status.
* `GetHealthAsync(CancellationToken = default)` returns
  `AdGuardHealth(Running, ProtectionEnabled, RewritesEnabled)`, with additional
  read-only result properties `Version`, `DnsPort` and `DnsAddresses`. These are
  live server status, not assumptions about the configured HTTP API port.
* `ListRewritesAsync(CancellationToken = default)` returns
  `Task<IReadOnlyList<AdGuardRewrite>>`.
* `AddRewriteAsync(AdGuardRewrite, CancellationToken = default)` and
  `DeleteRewriteAsync(AdGuardRewrite, CancellationToken = default)` return `Task`.

`AdGuardRewrite(string Domain, string Answer)` also exposes `Enabled`, defaulting
to true when an older server omits it. Listing preserves existing public-IP,
CNAME and disabled entries for collision review; it never converts them into
new private-IP rules. Writes accept only ASCII hostnames (optional leftmost
`*.` and trailing dot) and canonical RFC1918/IPv6 ULA IP answers, never CNAMEs,
shell syntax or arbitrary URL schemes. Add only creates enabled entries.
Delete may remove an exact existing disabled entry without enabling it.

Every mutation checks running/protection/rewrite settings first and returns
`adguard_rewrites_disabled` (409) with owner guidance if a global prerequisite
is disabled. It never changes those global settings. Add refuses any existing
entry for the same domain, ignoring case and a trailing dot, including disabled
entries (`adguard_rewrite_conflict`, 409). Delete requires an ordinal exact
domain/answer pair from the list (`adguard_rewrite_not_found`, 409). It sends
only that pair; AdGuard removes **all** matching domain/answer duplicates,
regardless of their enabled state. It is not a domain-wide deletion, clear-all,
update or replacement. Listing preserves duplicate entries and each enabled
state so the ownership journal can account for this behavior.
Wildcard overlaps and other clients' races are not conditional operations:
AdGuard has no compare-and-swap API.

After a dispatched mutation, an error, timeout or cancellation is
`adguard_mutation_indeterminate` (502), not success or an automatic retry. The
domain coordinator owns the durable per-record journal, reconciliation,
idempotency, external conflicts and rollback. Refresh the list to determine
whether an indeterminate mutation applied. There are no browser rewrite CRUD
routes. A later Lucia-deployed AdGuard can feed the same connection service;
the domain workflow depends on `ILocalDnsProvider`, not external-host lifecycle.

## Security and actual bounds

* Origin-only HTTP(S), optional explicit port, URL maximum 512 characters and
  host maximum 253. No path prefixes, credentials in URLs, query/fragment,
  percent-encoded authorities, whitespace or traversal. Native HTTPS
  certificate/hostname validation only. No custom trust callback or reuse of
  HostAuthentication's private root; install an appropriate CA in the OS trust
  store if needed. No silent certificate bypass.
* HTTP requires explicit `allowInsecureHttp:true`; the UI must clearly surface
  that flag and that Basic credentials are exposed on the LAN.
* Before any remote operation, `/control/profile` must return a nonempty user
  name matching the configured username. Userless AdGuard returns HTTP 200
  even with arbitrary Basic credentials, but its empty profile is rejected
  (`adguard_authentication_required`, 403). A different identity is rejected
  (`adguard_identity_mismatch`, 403). This check repeats on each operation, so
  disabling authentication after connection cannot silently permit mutations.
  AdGuard provides no API roles or scoped token; the supplied account is not a
  least-privilege DNS-only credential.
* Both HTTPS and HTTP must resolve **exclusively** to RFC1918 IPv4 or IPv6
  ULA addresses (at most 64 answers). Reject loopback, public, link-local,
  metadata, multicast, IPv4-mapped IPv6 and scoped IPv6 destinations. Resolve
  once per operation, validate every result, and pin the first address to the
  actual socket across reconnects. No DNS resolution in the socket callback.
  No automatic alternate-address fallback or application retries.
* No proxy, redirects, cookies, ambient credentials or HTTP trace propagation.
  OpenTelemetry instrumentation is suppressed around credential requests.
  Credentials are sent as UTF-8 Basic headers, never as URL parameters.
* One 10-second deadline covers DNS resolution, all remote requests and body
  reads for an operation. Connect timeout is 5 seconds. Response headers are
  bounded to 16 KiB, each response body to 1 MiB and JSON depth to 8. No
  decompression is requested. Request bodies are bounded to 16 KiB/depth 2.
* Username 1–128 UTF-16 code units, no colon/control characters; password
  1–1024 code units, no CR/LF. Valid Unicode is required and whitespace is
  preserved. Version metadata is bounded to 64 characters.
* The complete stored record (including username/password) is protected using
  the host Data Protection provider and a dedicated purpose. Protected JSON
  is bounded to 32 KiB, plaintext to 16 KiB, depth to 4. Writes are
  same-directory, flushed, private staging files followed by atomic replacement.
  Directory/file permissions are 0700/0600 on Unix and protected current-user
  ACLs on Windows. All existing path components are checked for
  symlinks/junctions. Corrupt reads fail closed. Explicit disconnect can replace
  a corrupt regular record but cannot recover an unsafe path.
* Storage is for a **single host process**, with serialized operations. The
  current service identity and machine administrator remain trusted; this is
  not cross-process locking or a defense against a malicious same-identity
  process racing filesystem checks.

## Verified upstream API

Official schema:
<https://raw.githubusercontent.com/AdguardTeam/AdGuardHome/05ba17b282da1c4393d6a4ba4db0cf519194a362/openapi/openapi.yaml>

Release **v0.107.79**, pin **05ba17b282da1c4393d6a4ba4db0cf519194a362**.
Schema server prefix `/control`, global security
scheme `basicAuth` (`type:http`, `scheme:basic`).

| Operation | Exact upstream request |
| --- | --- |
| Every remote operation, read-only authentication prerequisite | GET `/control/profile` |
| Save/verify, read-only, after profile | GET `/control/status`, GET `/control/rewrite/list`, GET `/control/rewrite/settings` |
| Health / mutation prerequisite, after profile | GET `/control/status`, GET `/control/rewrite/settings` |
| List, after profile | GET `/control/rewrite/list` |
| Add | POST `/control/rewrite/add`, JSON `{domain,answer}` |
| Delete | POST `/control/rewrite/delete`, JSON `{domain,answer}` |

Save/verify require a named authenticated profile, the documented status fields
with `running:true`, a well-formed rewrite list and valid rewrite settings.
Stopped DNS fails with `adguard_dns_not_running` (409), preserving the prior
connection. Global protection/rewrites may be disabled while connecting, but
then block mutations; read-only health reports their disabled states. An
unsupported settings endpoint fails safely rather than assuming enabled.

Upstream `RewriteEntry.enabled` is optional and defaults to true for add.
Upstream add appends duplicate entries, delete removes all exact domain/answer
pairs ignoring enabled, and update changes the first matching pair. This
connector never calls update and refuses pre-existing domains before add;
the domain workflow must still maintain its ownership journal for concurrent
clients and indeterminate outcomes.

No login/session APIs, query logs, statistics, privacy data, global protection
setters or settings-update APIs are read/called. Recommended editable Lucia,
authentication and Spark application URLs do not alter this connector API.
