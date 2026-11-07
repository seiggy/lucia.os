# Heimdall Gateway AppRole Implementation Changes

## Scope and State

* Plan: [.copilot-tracking/plans/2026-10-30/heimdall-openbao-approle-plan.md](../../plans/2026-10-30/heimdall-openbao-approle-plan.md).
* Owner now requests: "ok, can you create the approle and generate the credentials please".
* Active scope: P01 qualification and P02-T01 role/credential provisioning. Application configuration, image changes, deployment and gateway restarts are not authorized by this request.
* This is an explicit request for one early application grant. It does not resume the paused operational restore, broader identity/migration rollout or unrelated apps.
* Execution complete for the bounded scope: P01 and P02-T01. Live auth mount, scoped policy, AppRole and credential files now exist. P02-T02 remains unchecked and outside current authorization.
* The owner entered the scoped operator password into a hidden terminal prompt. The password was not exposed to the assistant or stored; temporary operator and application tokens were revoked.

## Plan Updates

* Current owner direction narrows execution to credential provisioning. The prior application-installation/version prerequisite remains appropriate for gateway wiring, not for creating a role and delivering files to a ready private node destination.
* Reconciled all affected scope/readiness/risk/dependency sections. Moved gateway-version, Compose editing/restart and installation gates into P02-T02, preserving the same phases/task IDs and diagrams. P02-T01 is now bounded to secure credential delivery and direct AppRole verification.

## Validation

* Passed: pinned OpenBao 2.7.2 production-mode Raft/TLS fixture, with original synthetic root revoked and all labeled disposable resources removed. Twenty-two authorization/lifecycle groups passed; actual private helper verified collision refusal, role/policy drift refusal, stable RoleID, unrelated-role preservation and token capability/lifetime checks.
* Fixture correction: `local_secret_ids` is creation-only. The deliberate synthetic drift-restoration update now omits it; the actual provisioner only supplies it when creating a missing role and never rewrites a matching role.
* Native runnable check and sanitized result retained privately as `check-heimdall-openbao.mjs` and `heimdall-native-result.log` in session artifacts.
* Passed: six synthetic private-filesystem groups on the actual receiving node, with temporary state removed: UID 1654 / 0400 consumer files, root / 0600 state, idempotent retry, file drift refusal, uncertain-issuance refusal and recovery after the credential bundle commits but before source-file materialization. The filesystem test stubs its API and does not contact production OpenBao.
* Passed: root-owned interactive helper staged on the receiving node, Linux-normalized source SHA-256 verified, node/foundation container baseline retained privately without environment/configuration dumps.
* Passed: actual private operator provisioning, live role/policy read-back, AppRole login, exact positive/negative capabilities, initial token TTL and one-hour hard maximum. No application values were read.
* Passed: independent private verification of persisted file contents against the private bundle, UID/mode, absence of an interrupted-issuance marker, contract hash and a second AppRole login/capability check with token revocation.
* Passed: all receiving-node and foundation container IDs unchanged from the pre-provisioning baseline; backup timer remains active. OpenBao remained initialized/unsealed and the gateway was not reconfigured or restarted.
* Passed: `git diff --check`. No commit or push requested or performed.

## Completed Work

### P01-T01: Exact AppRole and policy contract

* Session-only `provision-heimdall-openbao.py` fixes the approved names, KV-v2 prefix and permissions. It accepts only scoped operator userpass authentication over verified direct HTTPS, rejects resource drift, preserves matching RoleIDs/unrelated roles and never restarts services.
* It durably journals SecretID issuance before the request, refuses unexplained duplicate issuance on retry, retains a root-only credential/accessor bundle, prepares UID 1654 read-only source files and verifies/revokes its temporary application and operator tokens.
* No product source, dependency, generic provisioner or gateway configuration changed.
* Validation: the native fixture exercised the actual helper's configuration/login/capability functions against OpenBao 2.7.2, including deliberately incompatible mount, role and policy cases.

### P01-T02: Synthetic isolation and credential lifecycle

* Confirmed create/read/update, prefix/nested listing and metadata reads; unrelated apps, ancestor enumeration, controller/backup canaries, destructive KV operations, auth/policy/mount administration and SecretID issuance are denied.
* Confirmed repeated logins, no SecretID expiry/use limit, only app/default policies, 15-minute initial and one-hour hard token bounds, wrong-credential rejection and bounded renewal.
* Replacement can overlap with the original. Revoking the old SecretID blocks its new logins without invalidating its existing token; revoking the known token accessor then denies that token.
* All fixtures are synthetic and isolated. No production authorization or credentials were used.

### P02-T01: Private AppRole and credential delivery

* Provisioned only the qualified `lucia-approle` mount, `lucia-heimdall-gateway` policy and `heimdall-gateway` role, and generated the application's non-expiring, repeatedly usable SecretID. Matching resources are reused without overwriting them; existing roles and unrelated identity/backup resources were preserved.
* Delivered `role-id` and `secret-id` into root-only `/etc/heimdall-gateway/openbao` on the owner-selected managed node. The two consumer files are UID 1654 / 0400. Root-only `credentials.json` retains the SecretID accessor and issuance ownership; it must not be mounted into the application.
* A matching private retry reuses this credential and RoleID; it does not mint another SecretID. Uncertain issuance without a committed private bundle stops for inspection rather than silently retrying.
* Credential-free contract/receipt hash: `6322b351c3ef756d870cc8ae33aca1267e32bea488c9f036b01ac54a8215df34`.
* Session evidence: `heimdall-provisioning-receipt.json`, `heimdall-service-preservation.json`, `heimdall-provisioning-baseline.json`, the qualified creator and independent verifier. No credential values are in these artifacts or chat.
* Removed only the three named temporary source/test staging files and their empty directory from the receiving user's cache. The root-owned operational helper and real private credential directory remain. Disposable fixture state was removed by its fixture cleanup.
* Current-scope review readiness: ready; no business-alignment divergence or additional follow-up item. This is not completion of the full two-phase plan.

## Blockers and Remaining Work

* Receiving node confirmed by owner. Root-only `/etc/heimdall-gateway/openbao` is available for private files; gateway source files will be readable by UID 1654 only and retained accessor/state by root only.
* No blocker remains for the requested role and credential delivery.
* P02-T02 application wiring, restart, native gateway read qualification and actual runtime rotation remain outside current authorization. Existing private source files must be mounted individually at the planned `/run/heimdall-openbao` targets; do not copy credentials into chat or mount the root-only revocation bundle.
* The parent operational restore and broader secrets migration remain paused/unimplemented.

## Business Alignment

* None.

## Follow-Up Items

* None.
