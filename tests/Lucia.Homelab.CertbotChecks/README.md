# Certbot checks and integration contract

Run `dotnet run --project tests\Lucia.Homelab.CertbotChecks` from the repository root.
These source-linked .NET 10 console checks need no packages, server project, GPU, accounts,
DNS access, ACME access, or installation of Certbot. Files are created beneath the check
project and removed afterwards. Windows requires permission to create symbolic links.
Without that privilege, `-- --without-symlinks` runs the five non-symlink groups, including
Windows ACL checks; run all eight groups on Linux. `--drvfs` is an explicit test-only mode
for a local WSL run on a Windows/DrvFS checkout: it skips POSIX mode assertions because
that filesystem uses Windows ACLs (verify those with the companion Windows run).
The internal executor/test-root seams are not available through the production public API.
The process checks execute this check program by its absolute path, never a PATH fake.

## Caller contract

- Construct `CertbotCertificateService(new CertbotOptions(stateDirectory, publishedCertificatesDirectory))`.
  Both paths must be absolute, dedicated, on private storage owned by the service identity.
  Published storage must not overlap the service's `stateDirectory/certbot` subtree.
  Run as a non-root Linux identity with system trust roots and the pinned
  `/opt/certbot/bin/certbot` installation. No container socket or host PID access is needed.
  The service identity must be the only writer to these private directories. As with
  Certbot itself, processes already running under that same identity are not a security boundary.
- Generate and persist `CertbotCertificateRequest.NewLineageName()` once per lineage.
  Pass 1–20 ASCII/punycode DNS names (including optional leftmost wildcards), email,
  explicit terms acceptance plus the accepted HTTPS terms URL, and 10–600 second propagation.
  Duplicate names, paths, IP literals, and arbitrary lineage strings are rejected.
  The parent obtains the legal agreement and decides whether a previously accepted agreement
  still applies. The terms URL is metadata, not a Certbot argument.
- `StageAsync(request, token, ct)` **modifies public DNS TXT records and contacts ACME staging**.
  Call only after explicit parent-workflow approval. It never publishes a certificate.
  A passed staging run is not production issuance or ingress activation.
- `IssueAsync` uses `certonly --keep-until-expiring`; `RenewAsync` uses the selected
  lineage with `renew --no-random-sleep-on-renew`. Existing renewal hooks are refused,
  directory hooks disabled, and system/user Certbot CLI configuration is not loaded.
  Tokens must never be logged by the caller. The parent decrypts its separately protected
  token immediately before calling; this helper writes the verbatim token to a stable private
  `certbot/credentials/cloudflare.ini`, restores it for every invocation (including rotations),
  and deletes it in a non-cancellable finally block. Renewal config stores that path only.
  A hard service/host crash cannot execute finally: private storage remains private, and a
  subsequent invocation replaces a leftover credential. Do not start an independent
  Certbot timer: the parent must supply credentials for each invocation.
- A semaphore rejects overlapping operations in one instance; an exclusive, retained lock
  file rejects other instances sharing state. Staging has a 10-minute deadline; production
  and renewal have 20 minutes (plus bounded child-exit/pipe cleanup grace). Cancellation/timeout stop only the owned child PID, not
  unrelated processes or descendants. stdout/stderr are drained into bounded buffers and
  discarded. Private Certbot logs are retained, made private, and exact token occurrences
  redacted after every invocation, including failures. Never expose raw logs in an API.
- Production output is accepted only from Certbot's canonical `live/<lineage>/*.pem`
  symlinks pointing directly into `config/archive/<same-lineage>/`, with matching generations
  and no linked parent/target components. Saved renewal file paths must also match that
  lineage and the stable credential file; mismatched renewal SANs are refused before execution.
  Certificate/key matching, current validity with
  more than seven days remaining, SAN coverage in both directions, server-auth system trust,
  and non-staging issuers are mandatory. Wildcards cover one label, never the apex.
  Intermediates come from the fullchain; trust downloads are disabled and revocation checks
  are not performed here. System trust is used unchanged; no CA keys or trust bypass exist.
- The receipt contains lineage, validity, actual DNS SANs, lowercase SHA-256 of leaf DER,
  immutable versioned fullchain/key paths, and `Renewed` (whether the leaf changed versus
  Certbot's pre-invocation leaf). An unchanged renewal returns the existing immutable pair.
  Files are 0600 and directories 0700, or current-user-only protected Windows ACLs.
  Publication uses a same-parent directory rename after both files are flushed. No "current"
  pointer or gateway config is changed; failed operations preserve every old published pair.
  The parent separately configures/reloads ingress and records activation success.

## Safe failures

`CertbotException.Code` is machine-readable; `Message` is operator-safe, and `ExitCode`
is present for a completed nonzero Certbot exit. No raw output, token, or inner exception
is attached. Caller cancellation throws `OperationCanceledException`.

| Code | Action |
| --- | --- |
| `certbot_request_invalid` / `certbot_token_invalid` | Correct validated input without logging the token. |
| `certbot_consent_required` | Obtain explicit ACME terms acceptance. |
| `certbot_busy` | Wait for the owning operation; do not delete its lock file. |
| `certbot_lineage_missing` | Issue before renewal. |
| `certbot_host_unsupported` / `certbot_execution_failed` | Check non-root Linux host and pinned installation. |
| `certbot_timeout` / `certbot_child_stop_failed` | Check the owned process, DNS propagation, and host health before retry. |
| `certbot_failed` | Inspect private logs, Cloudflare permissions, propagation, and ACME limits. |
| `certbot_storage_unsafe` / `certbot_hooks_forbidden` | Correct private storage, symlinks, space, or saved hooks. |
| `certbot_certificate_invalid` / `certbot_chain_untrusted` | Do not activate; inspect dates, SANs, pair, chain, and system trust. |

## Version qualification limits

The top-level requirements pin `certbot==5.8.0` and `certbot-dns-cloudflare==5.8.0`.
Both non-yanked releases were verified using official PyPI version metadata on 2026-09-23:

- <https://pypi.org/pypi/certbot/5.8.0/json>
- <https://pypi.org/pypi/certbot-dns-cloudflare/5.8.0/json>

The official Certbot 5.8.0 source distribution confirms `--no-random-sleep-on-renew`,
`--no-eff-email`, `--strict-permissions`, and `--no-directory-hooks`. The plugin metadata
requires `cloudflare>=4.0`; these two top-level pins are not a transitive dependency lock.
No live Certbot/DNS/ACME qualification was performed. Account-owned token compatibility
and the plugin's zone-list `per_page=1` versus documented API minimum of 5 require the
parent's explicit real-DNS staging gate. The plugin is not patched here.
