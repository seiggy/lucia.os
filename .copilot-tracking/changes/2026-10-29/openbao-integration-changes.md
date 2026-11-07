# RPI Changes: OpenBao integration

## Metadata

* Task ID: `lucia-openbao`
* Related plan: [.copilot-tracking/plans/2026-10-29/openbao-integration-plan.md](../../plans/2026-10-29/openbao-integration-plan.md)
* Implementation date: 2026-10-29

## Execution Status

* Status: Paused by owner at P01-T02 after verified initial backup/timer; keep daily backups running. Isolated operational restore remains unapproved.
* Declared invocation scope: full plan.
* Completed scope markers: P01-T01 and the separately approved P01-T03 access/DNS rollout.
* All remaining active-plan markers: P01-T02, P02-T01, P02-T02, P03-T01, P03-T02, P04-T01, P04-T02, P05-T01, P05-T02.
* Status basis: Foundation and synthetic recovery pass on AMD64/native ARM64; owner initialization/root handoff and actual encrypted NAS snapshot passed. Daily timer and NAS mount are enabled/active. Isolated operational restore remains incomplete.

## Execution Summary

Implementation is authorized by the direct `/rpi-implement` request. Foundation source, incremental documentation and native recovery qualification are complete. A subsequent explicit approval authorized the completed uninitialized-only deployment. Operational initialization/unseal remain user-owned; migration, operational restore, unrelated deployments, commits and pushes are not authorized.

## Completed Work

### P01-T01: Opt-in foundation with verified native lifecycle

* Foundation: `tools/identity/provision.py`, `src/Lucia.Homelab.Identity.AppHost/AppHost.cs`, `deployment/identity/start-openbao.sh` and `openbao-health.sh` now prepare an explicit private IPv4 listener with local Raft storage, non-root ownership, restricted mounts and no automatic initialization/unseal.
* Existing identity services remain independent. OpenBao reloads the step-ca-issued service leaf only after its published hashes verify; provisioning reports its seal state separately.
* Updates: shared C#/Python pins, the update worker and `src/frontend/src/platformImages.ts` recognize the optional image and operator-managed update policy. Ordinary update requests cannot enable or automatically roll back OpenBao.
* Delivery: desktop/bootstrap and host-deployment allowlists include the scripts; existing installations are not opted in implicitly.
* Checks: `tools/identity/check_openbao.py`, the shared `tools/security/check_openbao.mjs`, and existing domain, Python platform and frontend checks exercise rendering, policy and actual lifecycle behavior.
* Docs: new deploy/code secrets pages, related identity/security/platform/guide updates and `docs-map.json` describe only implemented foundation behavior and the remaining recovery/migration gates.
* Validation: local checks and documentation pass. Both local AMD64 and native ARM64 lifecycle pass on identical HCL SHA-256 `a9ba2f0ddb2a782786fba5fa8a3d042b760a8eff61e75771637344690a3a2bbb`. The native fixture left existing container IDs unchanged and removed its containers, volumes and network.
* Local ARM64 emulation still fails after restart; native ARM64 success resolves target-readiness uncertainty without hiding that local limitation. No operational vault was deployed or initialized.

## Implementation-Time Plan Updates

### Beginning the approved implementation

* Affected areas: invocation state and P01-T01.
* What changed: planning-only invocation is now implementation preparation; operational gates remain intact.
* Why: direct user request to execute the plan.
* Triggering evidence: user invocation and current ready plan with PC-001 through PC-008 resolved.
* Reconciliation: plan implementation state updated; no task checked in advance.
* Planning/critique state: current; no follow-up critique requested.

### Opt-in rollout and manual image policy

* Affected task: P01-T01.
* Foundation source now requires an explicit private bind address in separate `openbao-settings.json`; ordinary existing deployments remain unchanged.
* The pinned OpenBao image uses the existing platform manifest with a `manual` policy. Version and rebuild offers remain visible, but host and worker do not apply them automatically. Ordinary container-image rollback cannot restore Raft data.
* The deployed listener uses the existing step-ca-issued service leaf directly, with verified renewal reload. No CA key or Raft state reaches the web host.
* Source, payload lists, existing policy/frontend checks, identity render checks, and deploy/code documentation have been updated together.
* Planning/critique state: implementation details within the existing safety requirements; no confirmed user architecture decision changed.

### Foundation qualification in progress

* AMD64 disposable lifecycle passed with verified TLS, unrelated-CA rejection, repeated-init refusal, manual unseal, renewed-leaf reload, sealed restart and persisted KV-v2 data.
* Identity offline checks, disabled/opt-in Aspire rendering, 917 domain assertions, 46 Python platform assertions, frontend platform checks, 293 desktop checks and documentation build/prose checks passed.
* AppHost builds with an existing NU1902 MessagePack advisory warning; no package version changed.
* Early fixture failures were corrected: quote-aware Compose assertions, actual pinned gateway-plugin preparation for rendering, a bounded initialization deadline, and reacquisition of Docker's ephemeral port after restart.
* ARM64 emulation reaches ready state and renewal, but restart fails to serve its listener on both Windows bind-mounted and Docker-local named-volume Raft storage. Container-local `bao status` also exits 1; certificate hashes still verify. Synthetic shutdown logs include `unlocking HA lock failed: cannot find peer`. Whether this is an emulation limitation or a native release/wrapper defect is unresolved. No ARM64 runtime pass is claimed.
* The final fixture uses Linux-local Docker volumes for Raft/audit state. AMD64 passes again with that fixture and the explicit client-certificate TLS setting. All labeled disposable resources were removed; cleanup listings are empty.
* No task marker is checked while this qualification remains unresolved.

### User-approved native qualification

* Affected task: P01-T01.
* User selected "Run disposable native ARM64 qualification (Recommended)" after being told that it creates/removes synthetic containers and volumes without changing installed services or initializing an operational vault.
* Native qualification is authorized on the Spark only within that disposable scope. Operational deployment, custody setup, migration, commits and pushes remain unauthorized.
* The plan's current execution state and confirmed direction now record this limited approval.
* Native execution passed. Node was absent, so the fixture used a task-local Node 24.15.0 ARM64 distribution, verified against the official SHA-256 `f3d5a797b5d210ce8e2cb265544c8e482eaedcb8aa409a8b46da7e8595d0dda0`; no system runtime was installed. That temporary runtime and staged source were removed after the run. The runner uses existing `python3` on Linux and `uv` on Windows.
* Evidence is retained in the session artifact `openbao-native-result.log`. Existing installed container IDs matched before/after, and labeled fixture resource listings were empty.

### Restic reuse selected for P01-T02

* User requested restic reuse, with a private NAS Backups share as fallback. No private endpoint is copied into public source/docs.
* Verified prior art: `ResticBackups.cs` uses tag-scoped snapshots and retention in a shared NAS repository; `StackBackups.cs` generates/protects the repository key and exposes a private operator recovery path.
* Reuse restic and its repository/tag conventions, but not the node-sync scheduler: foundation recovery must work with the controller and ordinary managed-stack lifecycle unavailable.
* Plan D8, P01-T02 details/references and current next action now record this choice. Actual repository access, offline key custody and retention remain private operator gates.

### P01-T02: Independent restic backup and recovery preparation

* [tools/identity/openbao_backup.py](../../../tools/identity/openbao_backup.py) takes an authenticated Raft snapshot before invoking restic against an existing NAS repository. A separate foundation tag bounds retention; no app scheduler, web host, repository initialization or automatic unseal is involved.
* [deployment/identity/openbao-backup-policy.hcl](../../../deployment/identity/openbao-backup-policy.hcl) bounds the job to snapshot reads and self-revocation. A dedicated AppRole exchanges privately provisioned bootstrap inputs for short-lived tokens; the repository key and login inputs remain recoverable outside OpenBao. The token is revoked before restic waits for locks or performs retention.
* The job verifies the installed CA and endpoint, refuses redirects/proxy inheritance, checks the live Shamir threshold against private custody metadata, validates the compressed snapshot through EOF and rechecks the real NAS mount before every restic operation. Failed, interrupted, future-dated or older-than-24-hour backups are unhealthy. A failed restic operation or truncated stream cannot prune or publish success.
* An explicit `install` command prepares the non-root setup user's independent systemd service and daily `03:00 UTC` timer; it requires existing restic and user lingering. Ordinary provisioning never installs a timer. No timer has been installed operationally.
* [deployment/identity/openbao-operator-policy.hcl](../../../deployment/identity/openbao-operator-policy.hcl) and the deploy runbook establish a scoped operator userpass login, verify administration, then revoke initial root. The fixture proves a fresh operator login still works after restoration; this does not add the P02 workload identity implementation.
* Existing desktop/bootstrap/host payloads ship the tool and both policies. [docs-site/docs/deploy/secrets.mdx](../../../docs-site/docs/deploy/secrets.mdx), [docs-site/docs/code/secrets.mdx](../../../docs-site/docs/code/secrets.mdx) and `docs-map.json` describe the actual gate, commands and recovery boundaries.
* P01-T02 remains unchecked; D5 private custody/destination/retention and operational authorization remain owner gates.
* Release-specific discovery: the pinned server refuses audit-device creation through the API unless an unsafe compatibility flag is enabled. Use its supported declarative `audit` stanza instead, with HMAC defaults explicit; keep the unsafe flag disabled and audit configuration under the setup user's file boundary. Official reference: https://openbao.org/docs/configuration/audit/. This stays within P01-T02's release-supported audit requirement. The HCL changes from the P01-T01 baseline; its new audit behavior needs separate qualification.
* Actual restore exposed a required writable temporary-file path under the read-only container root. The production wrapper now sets `TMPDIR=/bao/data`, keeping restore staging inside its existing private writable data volume rather than broadening mounts or disabling the read-only root.
* Local AMD64 qualification passes with audit-enabled HCL SHA-256 `d2af1162d3e8169ccd8bd1634cda578ec96e19e77cbc87b5b44c6a925a3441a1`: scoped operator login, initial-root revocation, short-lived snapshot-only auth, audit HMAC and enabled-output failure, restic encrypted round-trip and app-tag retention isolation, the real Python API-to-restic pipeline, and actual isolated restore of that pipeline's snapshot using the original synthetic quorum.
* The latest isolated restore stage took 15.849 seconds on the local fixture, excluding repository retrieval; no operational recovery-time promise is made. Original root remained revoked after restore. The fixture uses restic `0.18.1@sha256:39d9072fb5651c80d75c7a811612eb60b4c06b32ffe87c2e9f3c7222e1797e76`, verified to include AMD64 and ARM64 images.
* The synthetic pipeline replaces only the NAS mount check, because its repository is deliberately local; it still uses the real production API and restic implementation. Actual off-host access and the systemd user runtime remain operational gates, not claimed passes.
* This preparation initially had native evidence only for the P01-T01 baseline. Subsequent owner-approved native recovery qualification and bounded deployment are recorded below; no operational initialization or migration is assumed.

### P01-T02: Owner selected the Shamir quorum

* User selected "3 shares, 2 required (Recommended)".
* Plan D5 and current operator gates now record 3 shares with a threshold of 2. This changes no runtime defaults or existing vault state and authorizes no initialization or deployment.
* Independent custody locations still need to be recorded privately. Destination/retention, native recovery qualification and explicit operational approval remain open.
* The owner confirmed the custody locations are not yet recorded and requested guidance. Operational initialization stays paused while the owner creates an independently accessible private recovery note; no locations or share values are requested in chat.

### P01-T02: Owner approved uninitialized-only deployment

* After the manual status command reported no OpenBao container, the user selected "Yes—qualify and deploy the uninitialized service".
* Authorized scope: native disposable recovery qualification followed by deployment of the uninitialized OpenBao service on the Spark, with existing services unchanged. No operational initialization, unseal, migration, unrelated host deployment, commits or pushes.
* Deployment must target only the new service, preserve existing private settings, use the pinned image and verified TLS, and compare existing container IDs before/after. Record the private endpoint operationally without publishing new lab addresses.
* Independent custody/repository/retention gates remain before owner initialization. P01-T02 stays unchecked until its full recovery requirements hold.
* Approved native ARM64 qualification passed with the audit-enabled HCL SHA-256 `d2af1162d3e8169ccd8bd1634cda578ec96e19e77cbc87b5b44c6a925a3441a1`; full recovery cases match AMD64. The isolated restore stage took 13.956 seconds. Existing running container IDs were unchanged. Session evidence: `openbao-recovery-native-result.log`.
* The uninitialized service will bind only loopback during this bounded deployment, preventing another LAN client from initializing it. Private network exposure and production backup connectivity remain later explicitly reviewed configuration steps; no migration is enabled.

### P01-T02: Uninitialized-only service deployed

* The approved service now runs as `lucia-identity-openbao`, using the qualified immutable image, private durable state, non-root ownership, read-only root, dropped capabilities, verified existing TLS and loopback-only port publication. Its independent Compose healthcheck passes; host-side CA-verified `/v1/sys/health` returns HTTP 501 with `initialized=false` and `sealed=true`.
* Generated the canonical AppHost's prepared Aspire artifacts against synthetic publication state, then applied only its `identity-openbao` service with `--no-deps`. Deployment bind paths and the existing project/network were supplied as target parameters. The private durable slice and receipt live under `openbao/deployment`; live foundation Compose/env files, installed source and platform manifests were not replaced.
* Existing bootstrap/provisioning locks were held. All previously running container IDs and protected identity settings, trust, private key, secret files and deployment inputs were unchanged afterward. No initialization, unseal, backup timer, credential migration, unrelated service restart, commit or push occurred.
* Spark's existing SDK is 10.0.401; only the disposable publication copy selected it instead of the repository's 10.0.403. No system SDK/runtime was installed or repository pin changed. Operational HCL matches the qualified publication HCL except for its existing installation API hostname.
* The initial isolated slice failed Compose validation because the generated service's network key is `aspire`, not `default`. Validation stopped before container creation; preserving that canonical key and supplying the existing network resolved it. No unrelated resource was targeted.
* Native fixture resources are gone and runtime mounts do not reference the temporary publication directory. Task-owned remote staging and named local transfer artifacts were removed; the deployed service remained healthy afterward. Session artifacts retain native qualification evidence and `openbao-deployment-receipt.json`.
* Plan guidance/readiness and qualification risk now reflect native success and actual deployment. A future whole-foundation redeploy must ship the new AppHost/provisioner and manual image-policy integration first; older installed source/manifests were intentionally preserved under the bounded approval. LAN exposure and production backup connectivity remain reviewed rollout steps. P01-T02 remains unchecked; business alignment is unchanged.

## Business Alignment

* None.

## Confirmed Long-Term Backup Identity Direction

* The owner selected dedicated `step-ca` client-certificate authentication with automated renewal as the backup job's long-term solution, rather than granting it AppRole SecretID self-rotation permissions.
* P02-T01 now explicitly includes the dedicated backup identity, snapshot-only policy, independent recovery/renewal boundary, negative/renewal qualification and revocation of the temporary AppRole credentials after a verified cutover. This extends the already-selected certificate identity approach to the actual foundation backup job; no certificate reuse or new generic rotation subsystem is implied.
* Planning only: no running auth, certificate, policy or timer changed. The current backup SecretID still needs rotation before 2026-11-29 unless a qualified cutover precedes it. The owner's operational restore pause remains in effect; P01-T02 and dependent P02-P05 remain incomplete.
* A read-only availability check confirms OpenBao is initialized, unsealed and healthy, published only at `127.0.0.1:8200`, with the last backup successful. This confirms service availability, not completion of app identities, network exposure, .NET integration or recovery qualification.

## Newly Requested Early UI/API and Authentik Rollout

* The owner requests OpenBao UI and API routes through the existing Traefik gateway and Authentik OAuth/OIDC protection, before long-term client-certificate work. This advances the formerly deferred operator OIDC feature, but does not approve operational restore or existing-credential migration.
* Official OpenBao JWT/OIDC documentation confirms browser OIDC login and JWT API login exchange the external identity for an OpenBao policy-bound token. An Authentik OAuth bearer is not automatically a replacement for an OpenBao token on arbitrary secret API requests. Confirm the user's intended client model before implementation; endpoint naming, authorized users/groups and workload role boundaries remain open.
* Initial inspection found reusable identity application registration and dynamic gateway configuration patterns. No exposure, Authentik provider, OpenBao auth mount/policy or running service has changed. Existing loopback backup and private operator recovery must remain independent. TLS-terminated UI/JWT routes must not be presented as future client-certificate passthrough.
* Confirmed choices: native OIDC/JWT exchange; one private HTTPS origin for UI/API; `lucia-owners` receive the existing scoped operator policy; LAN/VPN only; no application roles or grants until each application is integrated. The chosen hostname remains in private deployment inputs. Added dependency-ready P01-T03 for this bounded access work; P01-T02 restore and P02 workload work remain paused.
* Live read-only inspection confirms the existing gateway private/public entrypoints, shared OpenBao network and wildcard certificate coverage. The selected private hostname does not currently resolve. Planned validation covers registration ownership/retry, exact callbacks/claims, private routing with verified backend TLS, native login/negative authorization and unchanged local backup/recovery. Live OpenBao configuration needs the owner to enter the scoped operator password privately.
* P01-T03 source now adds [tools/identity/openbao_access.py](../../../tools/identity/openbao_access.py), shipped through existing desktop/host payload lists. It owns a dedicated Authentik application/provider/operator mapping, preserves client secrets, refuses drift/extra bindings, and separates native `lucia-oidc` from a role-free `lucia-jwt` mount. Hidden scoped-userpass configuration revokes its temporary token before publishing a private-only route with verified backend TLS; no service recreation is required.
* Validation passed: 8 offline access tests; 28 existing Authentik registration tests (one pre-existing Windows link-fixture skip); existing OpenBao foundation/backup checks; AMD64 production-mode foundation/recovery fixture extended with native RSA JWT exchange and negative issuer/audience/subject/claim/expiry/signature/path tests; Docusaurus build, prose lint and whitespace checks. Native JWT checks use synthetic-only roles/keys in disposable storage, not app grants on the live service. Operational restore remains paused.
* Updated [docs-site/docs/deploy/secrets.mdx](../../../docs-site/docs/deploy/secrets.mdx), [docs-site/docs/code/secrets.mdx](../../../docs-site/docs/code/secrets.mdx), related identity deployment prose and [docs-map.json](../../../docs-map.json). P01-T03 remains incomplete pending bounded live staging, private DNS and owner-operated configuration/browser verification.
* Bounded live staging succeeded: only the new access/backup Python source files were added to installed identity tools, without replacing the older AppHost/provisioner or touching runtime backup copies. Dedicated Authentik registration reports `registered=true, openbao_configured=false`; it verified enrolled-owner access and the owner claim. No OpenBao auth mount or gateway route has been applied yet. Desktop payload qualification now passes 303 checks (existing local link-fixture skip).
* Owner completed the private configuration command successfully. Independent live checks now pass through CA-verified Traefik TLS: UI and unsealed health, UI/direct OIDC authorization requests, exact callback rejection, unauthenticated/invalid-token/OAuth-bearer secret denial, unconfigured app-login denial, and private-route/public-entrypoint separation. Gateway/OpenBao container IDs are unchanged, the existing backup remains successful and its timer active. A private `openbao/access-deployment.json` receipt records those checks and deployed source hashes without tokens or passwords.
* Remaining P01-T03 gate: the selected private hostname still has no DNS answer, so browser login cannot yet be completed. Owner must add the exact private DNS rewrite and complete Authentik sign-in; API authorization-request success is not being reported as browser SSO proof.
* Owner correctly challenged the manual DNS step: Lucia already manages AdGuard DNS. The missing integration is foundation-service registration. [src/Lucia.Homelab.Server/Nodes/ManagedNodeDns.cs](../../../src/Lucia.Homelab.Server/Nodes/ManagedNodeDns.cs) derives records from managed nodes, app routes, direct app names and the DNS fleet; it does not see this separately published foundation route. Existing external routes support HTTP-only upstreams, so using one as a DNS-only placeholder would misrepresent the HTTPS service and weaken fallback behavior.
* Proposed correction: publish only approved nonsecret OpenBao origin metadata through the existing host domain state, then have the existing DNS reconciler use its current AdGuard provider, ownership/conflict handling and fleet behavior. No second DNS credential, manual AdGuard record or fake app route. This needs a narrowly scoped controller source update/deployment; approval for that controller restart is being requested. The vault, auth mounts and backup stay unchanged.
* Owner approved the targeted controller update/restart. P01-T03 now includes a profile-bound nonsecret DNS intent, existing reconciler/operations-view integration and app-route name reservation. Validation will cover absence, profile/namespace mismatch, conflicting state, ownership-preserving reconciliation and live private DNS. Deployment must recreate only the controller service, preserving gateway/OpenBao IDs and backup/auth state; the generic whole-foundation deploy script is out of scope.
* Automatic DNS correction is deployed. The canonical ARM64 host artifact was repackaged with exactly one changed file (`Lucia.Homelab.Server.dll`), preserving every web asset, node payload and dependency. Canonical host image/settings preparation was followed by a single-service Compose update on the existing external identity network; no whole-foundation publication or deployment ran. The first staging attempt stopped on a read-only scratch assembly before any restart; the retry used a writable private copy and succeeded. Rollback inputs and a private deployment receipt are retained.
* Controller deployment preserved model slots, inference-key state, the active domain, credential/certificate/protected-state hashes and every other container ID. OpenBao remains unsealed; the gateway and identity services were not recreated, and the successful backup plus daily timer remain intact. Private DNS intent is profile-bound, the Domains view reports an exact matching record, and the existing node DNS journal owns it. Both the controller and workstation now resolve the chosen name; UI and API return success through normal DNS and certificate-verified HTTPS.
* DNS correction checks: 936 domain assertions and nine access unit checks passed, including absent/stale/cross-namespace registration, strict fields/duplicates/bounds, repeat reconciliation and refusal to replace an unowned conflicting record. The docs prose check/build and whitespace check passed. Native OIDC callback generation and negative access checks remain successful. Owner browser sign-in is the final P01-T03 gate; auth URL generation alone is not being counted as proof.
* Owner confirms successful Authentik browser sign-in in Firefox, completing P01-T03. Copilot's embedded browser instead reports `Cannot read properties of null (reading 'postMessage')`. The pinned OpenBao 2.7.2 callback source (`ui/app/routes/vault/cluster/oidc-callback.js`) calls `window.opener.postMessage(payload, window.origin)`; the embedded popup has no opener. Documented standalone-browser use without changing redirect allowlists, origin checks or proxy protections. The private receipt distinguishes owner-confirmed Firefox sign-in from automated checks; no credentials or login tokens were collected. P01-T02 operational restore and later workload/migration work remain unfinished.

## Latest Operator Confirmation

* P01-T02: after clarification that custody locations are owner-chosen storage places rather than GUI settings, the owner confirmed that this is done. D5 now records privately documented independent custody locations; neither locations nor share values were requested or recorded here.
* The owner also confirmed that the restic repository password and NAS access details are stored outside Lucia/OpenBao. No credentials or private destination values were requested or recorded.
* The owner accepted 7 daily, 4 weekly and 6 monthly OpenBao snapshot retention. D5's pre-initialization confirmations are complete; user-owned initialization/unseal instructions can now proceed. No assistant initialization or unseal was performed. Actual backup configuration/runtime, scoped operator/root handoff and approved operational recovery remain incomplete, so P01-T02 stays unchecked.
* The owner subsequently confirmed completion of private initialization, independent share storage and manual unseal. Read-only `bao status -format=json` independently verified initialized/unsealed with 3 shares/2 required. The assistant collected no share, password or root-token values. Scoped operator setup is next; initial root must not be revoked until a separate scoped login and intended administration work.
* The owner reported success creating the `lucia-operator` policy, userpass auth mount and operator user in a private, non-caching root channel. Separate scoped login/administration and initial-root revocation are not yet confirmed. No password or token values were requested or collected.
* The owner supplied nonsecret output confirming `[default lucia-operator]`, the enabled `lucia-file/` audit device, successful empty `lucia-secrets` KV-v2 mount and `lucia-openbao-backup` policy creation, temporary operator-session revocation and the script's passing raw-storage-denial check. Initial root remains pending revocation; no credential migration occurred and no credential values were collected.
* The owner then confirmed `Root handoff verified.` from the private handoff script: root-policy identity checked before self-revocation, old root rejected with HTTP 403, fresh operator login retained audit/policy access, and its temporary session was revoked. Initial root is no longer an operational credential. No token/password/share values were collected; deletion of only the revoked root-token entry is private owner housekeeping.
* The owner approved configuring/testing daily OpenBao backups with the existing restic repository and accepted retention: scoped backup login, private configuration, initial encrypted snapshot and daily timer, without unrelated restarts or operational restore. New software or a NAS mount must be flagged separately.
* Read-only Spark preflight found no restic executable, no mounted CIFS/NFS share, no installed OpenBao backup tool and no backup timer; user lingering is already enabled. No credentials were read and no packages, mounts, backup auth or timers were changed. Prerequisite software/mount approval is next. The existing loopback-only deployment also requires a verified local API connection path before activating the backup job; opening LAN exposure is not assumed.
* The owner paused the prerequisite question to ask whether existing backups/NAS setup were on nodes. Source confirms that `StackBackups.cs` coordinates requests and stores encrypted repository configuration on the controller, while `ResticBackups.cs` executes on nodes and `NasMounts.cs` owns their NAS mounts. The missing-prerequisite finding applies only to Spark, not the existing node backup setup. No software/mount approval was inferred and no such changes were made.
* The owner subsequently explicitly approved adding restic and NAS mounts to Spark to support this backup job. Scope remains reuse of the existing repository/share, no node-backup changes or existing-service restarts, private owner credential entry and no operational restore. Read-only preflight confirms noninteractive sudo, existing SMB/NFS helpers, user lingering and the unsealed 3/2 vault; only restic and the actual NAS mount are absent.

### P01-T02: Spark backup prerequisites and NAS access block

* Installed only the distribution `restic` package, version 0.16.4 on ARM64. Apt reported no service/container restarts needed; existing SMB/NFS helpers and user lingering were already present. No repository was initialized or replaced.
* Retrieved only nonsecret backup destination metadata through the existing verified owner API into a private Spark state file; no repository password or NAS credentials were retrieved. Existing node reports show their backup mounts are `Mounted`.
* Staged a dedicated root-owned NFS systemd mount for that existing export, below a setup-user-only parent directory, without changing node mounts or Docker ordering. The NAS rejected the Spark with `access denied by server`. The task-owned mount unit was disabled afterward, so it will not retry at boot before export access is resolved. No backup configuration, AppRole, initial snapshot or timer was installed. OpenBao remained healthy/unsealed. Remote transfer scripts were removed.
* Corrected the backup client's loopback endpoint choice in [tools/identity/openbao_backup.py](../../../tools/identity/openbao_backup.py): `127.0.0.1` publication now uses CA-verified `localhost`, whose SAN is already on the existing service certificate, rather than the controller's LAN hostname. Other bindings retain the identity hostname. No listener or TLS-verification setting changed.
* [tools/identity/check_openbao.py](../../../tools/identity/check_openbao.py) now checks both loopback and private-LAN bindings with a distinct controller hostname. Targeted identity/backup checks, prose lint, Docusaurus build and whitespace checks passed. Deploy/code secrets documentation now explains independent Spark-side repository access and local verified API routing.
* Existing full synthetic recovery evidence remains applicable to the unchanged snapshot/restic pipeline; operational compatibility with the installed distribution restic and actual NAS transport still awaits repository access and private credentials. P01-T02 remains unchecked. Owner decision is needed for NAS export access correction; no NAS access rules were changed.

### P01-T02: NAS access resolved and private backup job staged

* The owner corrected NAS NFS client access and confirmed completion. Retrying Spark's existing staged unit succeeded; it is now mounted and enabled for boot. The assistant made no NAS-side access changes.
* Existing destination `source` names the repository's full NFS path, including its folder. The first staging validation incorrectly appended that folder again and stopped before writing backup configuration. Corrected the deployment-only mapping to use the verified mounted source as the repository root; no repository or data was modified.
* Verified that the existing repository `config` is present at the canonical mount root and the non-root setup user has read/write/traverse access. Wrote the accepted private 3/2 custody confirmations and 7/4/6 retention configuration. No repository initialization was attempted.
* Staged canonical [tools/identity/openbao_backup.py](../../../tools/identity/openbao_backup.py) and [tools/identity/provision.py](../../../tools/identity/provision.py) in private `openbao/job`, without replacing the older installed foundation source or redeploying any container. A private source receipt records their hashes. A session-authored first-setup helper lives beside them for owner-only hidden credential prompts and initial backup/timer setup.
* Verified the local unsealed API over CA-validated `https://localhost:8200`, private Python syntax, persistent NFS unit and installed restic's required `--retry-lock` support. Remote transfer scripts were removed. No backup credentials have been entered, AppRole created, snapshot taken or timer enabled.
* Next owner command is `python3 ~/.local/share/lucia/identity/openbao/job/finish-openbao-backup.py` from a private SSH terminal. It accepts only the existing repository password and scoped operator password interactively, verifies repository decryption, creates snapshot-only login files, revokes its temporary operator token and requires a successful real snapshot before enabling the daily timer. It never uses root or unseal shares. P01-T02 remains unchecked pending runtime and separately approved recovery evidence.

### P01-T02: Private helper permission fix and exact partial-state resume

* Owner setup successfully opened the existing encrypted repository using Lucia's recovery key (the restic repository password), then failed with `TypeError` after operator authentication. The bug was in the session-authored helper: `os.chmod(output, 0o600)` passed a Python file object rather than a path or descriptor. This was reproduced on native Linux; it was not a password or vault-state error.
* Redacted audit metadata and file-size checks confirm backup auth/role creation succeeded, one SecretID was issued but not stored, the role-ID file is empty and no SecretID file/snapshot status/timer exists. Audit metadata confirms temporary operator self-revocation succeeded. No credential values were printed or collected.
* Repaired helper file permissions and private atomic credential writes. Its recovery path accepts only the observed partial state, reuses the preserved repository password, requires a fresh private operator login, verifies the existing role's intended policy/TTLs, revokes only the single unstored SecretID and creates a replacement without recreating the mount/role. Unexpected role state or accessor count stops explicitly.
* Native synthetic checks reproduce the original failure and pass fresh setup, exact partial-state resume, mismatched-role refusal, private credential modes, session self-revocation, ordered run/status/install and output-redaction checks. These tests use only synthetic values and invoke no real vault, restic operation or timer. Updated the private source receipt and removed the remote test transfer script.
* The repaired helper is deployed. Actual owner retry has not run, and the real initial backup/timer remains unproven. Owner command is unchanged; only the operator password is needed on this retry. P01-T02 remains unchecked.

### P01-T02: Initial encrypted snapshot and daily timer verified

* Owner retry succeeded: the existing repository opened, the single unstored SecretID was revoked, the existing role was preserved and snapshot-only login files were stored privately. No credential values were supplied in chat.
* The actual Python-to-restic job completed at `2026-10-30T14:47:08Z`; private status reports `succeeded` and `healthy=true`. Owner output records the snapshot ID; no repository initialization or vault reinitialization occurred.
* Read-only verification independently confirms the healthy backup, active/enabled `lucia-openbao-backup.timer` and active/enabled dedicated NFS mount. The next timer run is `2026-10-31T03:00:00Z`, displayed by the Spark as the preceding local evening. Accepted retention remains 7 daily/4 weekly/6 monthly.
* The backup SecretID has a 30-day lifetime and needs private rotation before `2026-11-29`; no automatic rotation or reminder is claimed. No operational restore has run or been approved. P01-T02 remains unchecked until its isolated recovery requirements hold.
* The owner selected "Pause here—keep backups running" rather than approving the proposed isolated operational restore. Leave the existing daily backup timer and NAS mount enabled/active. No drill staging/execution, dependent migration, commit or push is authorized by this pause.

## Validation Record

| Check | Scope | Status | Evidence or reason |
|---|---|---|---|
| Docker engine availability | Disposable local qualification | Passed | Linux x86_64 engine responds; existing services will not be modified |
| Aspire API discovery | P01-T01 | Passed | Container bind-mount and Compose CapDrop/ReadOnly APIs verified read-only |
| Existing identity checks and new offline/render checks | P01-T01 | Passed | Disabled and opt-in Aspire artifacts publish without deployment |
| AppHost build | P01-T01 | Passed with warning | NU1902 existing MessagePack advisory; no dependencies changed |
| Domain/platform/frontend contracts | P01-T01 | Passed | 918 .NET assertions, 49 Python assertions, frontend type-check and platform tests |
| Desktop/bootstrap payload | P01-T01 | Passed | 293 offline checks and actual allowlisted payload; Windows symlink fixture unavailable |
| Disposable AMD64 foundation lifecycle | P01-T01 | Passed | TLS, trust rejection, repeat-init refusal, unseal, renewal reload, sealed restart, persistence and unavailable state; final config SHA-256 `a9ba2f0ddb2a782786fba5fa8a3d042b760a8eff61e75771637344690a3a2bbb` |
| ARM64 emulated lifecycle | P01-T01 | Failed | Post-restart TLS unavailable; also reproduced with Linux-local Raft volumes. Native ARM64 passes separately |
| Native ARM64 foundation lifecycle | P01-T01 | Passed | User-approved synthetic Spark fixture; same lifecycle and HCL as AMD64, existing container IDs unchanged, fixture resources removed |
| Documentation | P01-T01 incremental | Passed | Prose lint and production Docusaurus build, including new linked deploy/code pages |
| Identity/backup offline contracts and Aspire rendering | P01-T02 preparation | Passed | Custody/retention/path gates, NAS-local refusal, freshness/future-time checks, token cleanup, truncated-stream failure and opt-in/disabled rendering |
| Disposable AMD64 audit/backup/restore | P01-T02 preparation | Passed | Real API-to-restic pipeline, tag isolation, enabled audit failure closed, initial-root revocation, original-quorum restore and persistent operator login; latest 15.849-second isolated restore stage |
| Desktop/bootstrap payload | P01-T02 additions | Passed | 302 offline checks, actual 222,539-byte allowlisted payload packaged; existing Windows symlink-creation limitation remains |
| Documentation | P01-T02 preparation | Passed | Prose lint and Docusaurus production build |
| Native ARM64 recovery configuration | P01-T02 additions | Passed | Renewed user approval; full fixture passed with matching audit-enabled HCL, 13.956-second isolated restore stage and existing running container IDs unchanged |
| Uninitialized-only operational deployment | P01-T02 bounded rollout | Passed | Healthy container; host-side verified TLS returns HTTP 501 and uninitialized/sealed state; loopback-only publication; existing running container IDs and protected inputs unchanged |
| Private initialization and scoped operator checks | P01-T02 owner-operated setup | Passed | Read-only status verified initialized/unsealed with 3 shares/2 required; owner supplied nonsecret output for scoped login, audit device, KV-v2 mount, backup policy, raw-storage denial and temporary-session revocation |
| Initial-root handoff | P01-T02 owner-operated setup | Passed, owner-confirmed | `Root handoff verified.` requires initial-root self-revocation, explicit HTTP 403 rejection, fresh scoped login/audit/policy access and temporary-session revocation |
| Spark backup prerequisites | P01-T02 rollout | Passed | Distribution restic installed with required bounded-lock flag; owner corrected NFS access; dedicated mount enabled/mounted and setup-user existing repository access verified |
| Local API routing and related docs | P01-T02 rollout preparation | Passed | Identity/backup checks cover localhost vs distinct controller hostname, prose lint and Docusaurus build pass; no API exposure change |
| Real NAS transport and systemd timer runtime | P01-T02 rollout | Passed initial setup | Actual encrypted snapshot succeeded, private status healthy; NFS mount and daily backup timer enabled/active. Next scheduled run remains future, not claimed executed |
| Private setup recovery helper | P01-T02 rollout repair | Passed | Native synthetic fresh/resume/refusal checks and actual owner retry passed; orphan SecretID revoked, existing role preserved and private credentials saved |
| Changed-source parsing and whitespace | P01-T02 preparation | Passed | Python AST parsing, Node syntax check and `git diff --check`; final fixture container/volume/network and local temporary-directory listings empty |
| Operational custody and off-host backup | P01-T02 rollout | Passed, initial backup | Private custody/access/retention confirmations and root handoff complete; actual first encrypted snapshot and timer verified |
| Isolated operational restore | P01-T02 rollout | Not run | Separate approval and private owner share entry required; never restore into live Raft state |
| P02-P05 checks | Dependent work | Not run | Prerequisite tasks remain unchecked |

## Pre-Review Reconciliation

* Plan markers and task-local context: P01-T01 and the separately approved P01-T03 are complete; operational restore remains paused.
* Completed-work entries: P01-T01 and bounded P01-T03 access/DNS evidence above. One separately requested Heimdall credential grant is recorded in its own plan/changes below; it does not complete the parent's generic workload tasks.
* Validation/blockers/remaining work: current.
* Review readiness: Not ready; P01-T02 and dependent integration remain incomplete. Private recovery confirmations, root handoff and actual first backup/timer are complete; isolated operational restore evidence remains.

## Blockers

* P01-T02 awaits separate approval and private owner participation for an isolated operational restore drill. Initial encrypted snapshot, daily timer and NAS mount are verified; custody/access/retention and root handoff are complete. No operational secret values were collected by the assistant. Owner: user/operator.
* Target qualification is resolved by native ARM64 success. Local emulation remains a recorded tooling limitation.

## Remaining Work

* P01-T02: approve and prove isolated operational snapshot recovery before migration. First backup/timer and prior custody/root-handoff gates are complete. Rotate the backup SecretID before 2026-11-29. Preserve verified loopback API access; before a whole-foundation redeploy, promote the new canonical source/manual image integration.
* P02-T01, P02-T02, P03-T01, P03-T02, P04-T01, P04-T02, P05-T01, P05-T02 remain dependency-gated.

## Follow-Up Items

* Canonical list: [.copilot-tracking/plans/2026-10-29/openbao-integration-plan.md](../../plans/2026-10-29/openbao-integration-plan.md), `## Follow-Up Items`.
* HA, auto-unseal, dynamic credentials, optional operator OIDC and issuer migration remain deferred.

## Return-to-Caller State

* Execution: paused by owner after verified initial operational backup/timer at P01-T02; daily backups remain running. Full-plan scope; prior custody, root handoff, native qualification and bounded deployment are complete. Isolated recovery remains unapproved and migration remains blocked on the rest of P01-T02.
* Completed markers: P01-T01 and P01-T03; P01 phase remains unchecked.
* Validation coverage: AMD64 and native ARM64 synthetic lifecycle/recovery plus source/render/docs checks pass. Owner initialization/root handoff and actual first encrypted NAS snapshot passed; daily timer/mount enabled/active. Isolated operational restore remains unproven. Local ARM64 emulated restart remains a recorded tooling failure.
* Business alignment entries: none.
* Continuation owner: user; no review handoff until implementation evidence is reconciled.

## Separately Authorized Early Heimdall AppRole

* Owner explicitly requested the Heimdall AppRole and credentials while keeping operational restore paused. That bounded exception is complete: qualified application-only permissions, private receiving-node credential delivery, live login/capability verification and temporary-token revocation.
* Auth mount `lucia-approle`, role `heimdall-gateway`, policy `lucia-heimdall-gateway`; only `lucia-secrets/apps/heimdall-gateway/` is granted. The prepared `lucia-jwt` mount still has no application roles.
* Gateway wiring/restart was not authorized or performed; every node/foundation container ID was preserved and the daily backup timer remains active. No secret values were collected in chat/artifacts.
* [.copilot-tracking/plans/2026-10-30/heimdall-openbao-approle-plan.md](../../plans/2026-10-30/heimdall-openbao-approle-plan.md): P01 and P02-T01 complete; P02-T02 remains pending.
* [.copilot-tracking/changes/2026-10-30/heimdall-openbao-approle-changes.md](../2026-10-30/heimdall-openbao-approle-changes.md): bounded implementation and private evidence pointers.
* Parent P01-T02 and P02-P05 remain incomplete. This one operational grant does not imply a generic delivery subsystem, certificate-based backup replacement or a broader secrets migration.
