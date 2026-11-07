# RPI Plan Critique: Heimdall Gateway OpenBao AppRole

## Metadata

* Task ID: heimdall-openbao-approle
* Critique date: 2026-10-30
* Plan: .copilot-tracking/plans/2026-10-30/heimdall-openbao-approle-plan.md
* Critique execution: Complete
* Critique depth: standard
* Depth provenance: skill default
* Critique type: initial
* Earlier critique: not applicable

## Inputs and Criterion Boundary

* User requested AppRole planning, then confirmed create/read/update/list within the application's prefix, a Lucia-managed node runtime and a non-expiring manually rotated SecretID with short-lived tokens.
* Assessed both phases, all four tasks, FR-001 through FR-005, NFR-001 through NFR-005, D1-D6, dependencies and declared scope/addition limits.
* Supplied evidence: deployment/identity/openbao-operator-policy.hcl, tools/security/check_openbao.mjs, tools/identity/openbao_access.py and .copilot-tracking/plans/2026-10-29/openbao-integration-plan.md.
* Public application evidence supplied by the user: the Heimdall Compose guide, upstream-auth page and Vault configuration reference cited in the plan. OpenBao AppRole documentation establishes the credential model.
* Four lenses assessed directly and sequentially under the session's default no-delegation guidance. No subagents, live lab calls, production changes or additional research.

## Lens Assessments

| Lens | Scope | Reasoning effort and source | Status | Candidates |
|---|---|---|---|---|
| Coverage | All FR/NFR IDs and four tasks | low, selected standard scope | direct | No separate finding; positive and negative policy/lifecycle coverage is present. |
| Evidence | Documented client, deployment assumptions and policy compatibility | medium, selected standard scope | direct | PC-002 adopted. No invented plaintext secret-read API is permitted. |
| Executability | P01/P02 sequencing, inputs and runtime checks | medium, selected standard scope | direct | PC-001 adopted; PC-002 merged from evidence. |
| Risk | Credentials, authorization gates, revocation and shared-state effects | medium, selected standard scope | direct | No additional finding. Rejected mandatory-expiry recommendation because it contradicts the user's explicit decision. |

## Coverage Assessment

| Requirement, phase or task ID | Coverage | Evidence or concern |
|---|---|---|
| FR-001, FR-004, P01 | Covered | Exact policy, role configuration and sibling/ancestor/admin/destructive-operation negatives. |
| FR-002, P02-T01 | Covered with execution inputs | Documented custom auth mount and credential-file settings; version compatibility must be checked privately. |
| FR-003, NFR-004 | Covered | Repeated login, replacement, accessor destruction and existing-token survival are explicitly distinguished. |
| FR-005, NFR-001, NFR-002, NFR-005 | Covered | Protected files outside the generic file resolver, TLS verification, permission and bind-mount refresh constraints. |
| P02 deployment prerequisites | Partial | PC-001: existing-installation assumption is unconfirmed. |
| P02-T02 native resolver read | Partial | PC-002: documented management APIs cannot return a plaintext value; the actual read probe needs a supported route and authorization gate. |
| D6 parent recovery/live authorization | Covered | Explicitly blocks production actions without completing recovery or an early-app exception. |

## Verdict

* Verdict: Revise
* Rationale: No Critical or High finding. Two Medium executability gaps need small planner-owned clarifications before handoff; neither requires changing the confirmed AppRole permissions or SecretID decision.
* Counts: Critical 0, High 0, Medium 2, Low 0.

## Findings

<!-- rpi:critique id=PC-001 -->
### PC-001 [Medium]: Gateway recreation presumes an existing installation

* Related IDs: D3, P02-T01, P02-T02.
* Evidence: P02-T01 says to recreate only the gateway, preserve its existing Compose files and perform no database migration. The user identified a managed-node runtime, not whether the application is already installed. The supplied Compose guide requires initial database/volume preparation.
* Concern: A first installation cannot follow a gateway-only recreation path, and initial app deployment is outside this plan.
* Impact: An implementer could widen the work to database/bootstrap deployment or be blocked after generating a real credential.
* Smallest useful change: Require an already-installed compatible stack before live provisioning; if absent, stop for a separately scoped app deployment rather than create credentials or claim end-to-end readiness.
* Action owner: Planning parent.
* Exact resolving evidence: P02-T01 preflight explicitly checks an existing compatible installation and stops before credential generation if it is absent; the risk/readiness record distinguishes that execution prerequisite.
* Decision route: Direct planner correction; no new user decision.

<!-- rpi:critique id=PC-002 -->
### PC-002 [Medium]: The native gateway read probe is not yet a defined supported operation

* Related IDs: FR-002, NFR-003, P02-T02.
* Evidence: P02-T02 requires resolving a synthetic value through a bounded integration test. The supplied upstream-auth page states that PUT writes a value and GET lists names; neither returns a value. Native resolution occurs during an actual upstream operation, with its own destination and credentials.
* Concern: A successful list/write or a direct OpenBao read is not proof that the gateway resolved a reference, while adding a plaintext read endpoint or sending credentials to an arbitrary test endpoint would exceed scope.
* Impact: Qualification could report a proxy check as actual client-read evidence, or introduce an unapproved external call/test service.
* Smallest useful change: Separate synthetic policy/read checks from native gateway resolution. Gate the latter on an owner-approved existing provider/MCP/model test that uses the intended destination, with cost/side effects understood; leave the task pending when unavailable. Do not invent a plaintext read endpoint or alter the gateway.
* Action owner: Planning parent.
* Exact resolving evidence: P02-T02 identifies the documented integration-test path, prohibits unrelated credential destinations/new read APIs and explicitly remains pending if a native read test has not succeeded.
* Decision route: Direct planner correction. Runtime test authorization stays with the owner during execution.

## Strengths and Residual Risk

* Existing `lucia-*` operator boundaries fit the selected resource names, without expanding owner privileges or sharing the backup identity.
* Long-lived SecretID compromise remains an explicit owner-selected risk. Short-lived tokens do not remove it.
* Unknown exact node/image, production authorization and the paused recovery gate are correctly private execution prerequisites.
* SecretID destruction is correctly not presented as immediate revocation of all previously issued tokens.

## Questions or Blocking Evidence Gaps

* No planning question is required for these corrections. The future runtime preflight and native read test remain gated rather than assumed.

## Limitations

* Public documentation was assessed, not the private gateway source or a running image. P02 must verify version compatibility.
* No synthetic or live qualification ran in this planning critique.

## Recommended Next Action

* Highest-impact findings: PC-001 and PC-002.
* Action owner: Planning parent.
* Smallest next action: Apply both bounded clarifications, record their disposition and finalize the plan without another critique.
* User response required: no.
