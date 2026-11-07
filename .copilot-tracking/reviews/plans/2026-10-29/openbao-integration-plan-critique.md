# RPI Plan Critique: OpenBao integration

## Metadata

* Task ID: `lucia-openbao`
* Critique date: 2026-10-29
* Plan: `.copilot-tracking/plans/2026-10-29/openbao-integration-plan.md`
* Critique execution: Complete after parent coverage of partial lens returns.
* Critique depth: standard.
* Depth provenance: default planning protocol.
* Critique type: initial.
* Earlier critique: not applicable.

## Inputs and Criterion Boundary

* User selected OpenBao, one controller-hosted instance with off-host backups, manual unseal/offline custody, and retained `step-ca`.
* Supplied evidence: the complete plan, `.copilot-tracking/research/2026-10-29/openbao-integration-research.md`, and task-cited source paths.
* Boundary: planning credibility across FR-001 through FR-010, NFR-001 through NFR-004, all phases/tasks, dependencies, risks, and operating gates. No live system or implementation qualification.

## Lens Assessments

| Lens | Scope | Reasoning effort and source | Status | Candidates |
|---|---|---|---|---|
| Coverage | All requirements and task contracts | Host default; no override | Partial return; remainder assessed directly | Adopted targeted certificate/cross-owner/race coverage as PC-007. Rejected requirements-restatement candidates: cited FR/NFR definitions and task Details already establish checkable criteria. Rejected documentation sequencing claim because P05-T02 explicitly requires incremental docs alongside each owning task. |
| Evidence | Plan against supplied research and task evidence | Host default; no override | Partial return; remainder assessed directly | Adopted planner-default provenance clarification as PC-008. Rejected root-revocation and role-revocation source absence as defects: these are conservative required behaviors with implementation checks, not claims of performed qualification. W9 is relevant to P01 audit setup. Domain migration inventory is an explicit task and current security-model evidence names the stores. |
| Executability | Phase order, gates, environments, budgets | Host default; no override | Partial return; remainder assessed directly | Adopted pre-cutover gate scheduling as PC-001 and root sequencing contradiction as PC-002. Adopted fixture/environment ambiguity into PC-004. Rejected version selection as a hidden prerequisite: P01-T01 explicitly owns selecting/pinning the release. All mentioned disposable checks can use the single budgeted runner. |
| Risk | All phases, decisions, risks, irreversible effects | Host default; no override | Complete | Adopted archive ownership, restore custody, stalled move, and irreversible share-loss candidates as PC-003 through PC-006. Rejected selectable fail-open audit behavior: native all-device failure must remain fail-closed; clarify rather than offer a bypass. Rejected root-token-as-unseal-fallback implication: a root token does not decrypt a sealed instance. |

Parent completion of partial boundaries used the supplied plan and evidence already in context. P03-P05 preserve domain semantics, capability-gated node rollout, private certificate material, and archive exclusions; remaining material omissions are captured below. No new research or second lens batch was used.

## Coverage Assessment

| ID | Coverage | Evidence or concern |
|---|---|---|
| FR-001, FR-002, FR-003; P01 | Partial | Deployment and recovery are covered; PC-002 and PC-004 clarify executable setup/custody. |
| FR-004, FR-005; P02 | Covered with qualification gates | Exact policies, client proof, API qualification, TTL lifecycle, and revocation tests exist. PC-008 distinguishes defaults from sourced requirements. |
| FR-006, FR-008; P03 | Partial | Pilot and journal are covered; PC-001 and PC-003 clarify gate order and retained archive ownership. |
| FR-007, FR-008; P04 | Partial | References and delivery are covered; PC-005 and PC-007 close stalled-move and negative-fixture gaps. |
| FR-009, FR-010; P05 | Partial | Operational status and incremental documentation exist; pre-cutover evidence must move to owning earlier tasks under PC-001. |
| NFR-001 through NFR-004 | Covered subject to findings | Secret-sentinel, concurrency, recovery, architecture and pinning criteria are explicit. No need to duplicate all definitions under every task. |

## Verdict

* Verdict: Revise.
* Rationale: two sequencing contradictions and six bounded operational/qualification clarifications require planner-owned corrections. User architecture choices need not be reopened.

## Findings

<!-- rpi:critique id=PC-001 -->
### PC-001 [High]: Pre-cutover checks are assigned to a phase after cutover

* Related IDs: P01-T02, P02-T02, P03-T01, P04-T02, P05-T01; FR-003, FR-008, FR-009.
* Evidence: P05-T01 says "Before real cutover, a fixture exercises" the failure matrix, but depends on completed P03/P04 migrations. Audit enablement is also reiterated there as before migration despite being partially established in P01.
* Concern: the plan does not cleanly distinguish owning preflight checks from final aggregate evidence.
* Impact: an executor could migrate before required qualification or interpret the dependencies as a cycle.
* Smallest useful change: assign foundation/audit checks to P01, API checks to P02, migration checks to the P03 pilot, and app/move checks to P04 before each affected live cutover. P05 aggregates/reruns the completed matrix.
* Action owner: planning parent.
* Exact resolving evidence: task-local fixture ownership and rollout gates are explicit; P05 no longer owns prerequisites of already-completed migration.
* Decision route: direct planner correction.

<!-- rpi:critique id=PC-002 -->
### PC-002 [High]: Root-token revocation has contradictory prerequisites

* Related IDs: P01-T02, P02-T01; FR-002, FR-004.
* Evidence: P01 revokes root after provisioning and establishes scoped administration; top-level Dependencies additionally requires runtime/policy-worker identities before root revocation, though P02 creates them after P01.
* Concern: the plan mixes two viable bootstrap sequences.
* Impact: executor ambiguity about whether to retain root or how to administer P02.
* Smallest useful change: explicitly establish and verify restricted scoped operator administration in P01, revoke initial root there, and use that scoped operator for P02 runtime-role provisioning. Remove the conflicting runtime-identity prerequisite of root revocation.
* Action owner: planning parent.
* Exact resolving evidence: P01 Requirements/Details and top-level Dependencies name one consistent bootstrap sequence and tested administration handoff.
* Decision route: direct planner correction.

<!-- rpi:critique id=PC-003 -->
### PC-003 [Medium]: Recovery-archive cleanup needs an explicit owner and trigger

* Related IDs: P03-T01, P05-T02; FR-008.
* Evidence: old protected data is retained "until qualification"; P05 later offers operator cleanup.
* Concern: ownership and the connection between archive retention and cleanup are implicit.
* Impact: stale secret-bearing archives may remain without deliberate disposition.
* Smallest useful change: name the operator as archive custodian, link the P05 qualification/cleanup trigger, and record retained-versus-destroyed disposition without automatic deletion.
* Action owner: planning parent.
* Exact resolving evidence: P03 retention record links to P05 closure and names the approval-gated disposition.
* Decision route: direct planner correction.

<!-- rpi:critique id=PC-004 -->
### PC-004 [Medium]: Restore-fixture environment and share handling are ambiguous

* Related IDs: P01-T02, P05-T01; FR-003, NFR-003.
* Evidence: restore into a disposable instance with "appropriate original unseal material"; later live drills require owner deployment approval.
* Concern: synthetic qualification and a restore of operational data need distinct custody and approval boundaries.
* Impact: unsafe share exposure or an unclear execution gate.
* Smallest useful change: name a single disposable qualification runner with test-only keys; separately require approved, isolated handling for any real snapshot drill. Never feed real shares through automated fixtures, arguments, or logs.
* Action owner: planning parent.
* Exact resolving evidence: fixture environment/prerequisites and real-data drill channel/cleanup requirements are explicit.
* Decision route: direct planner correction.

<!-- rpi:critique id=PC-005 -->
### PC-005 [Medium]: A failed move-health confirmation has no bounded disposition

* Related IDs: P04-T02; FR-008, NFR-002.
* Evidence: moves finish only after application health confirms the intended revision.
* Concern: failure to reach health needs a defined state after access reconciliation.
* Impact: indefinite pending operations and ambiguous target/source permissions.
* Smallest useful change: require bounded health confirmation, visible failed/pending-recovery state, no automatic regrant to the old node, and an explicit authorized retry/rollback retaining recoverable data.
* Action owner: planning parent.
* Exact resolving evidence: timeout/failed-health fixture proves grant and applied-revision behavior.
* Decision route: direct planner correction.

<!-- rpi:critique id=PC-006 -->
### PC-006 [Medium]: Permanent loss of Shamir quorum is not explicit in the risk register

* Related IDs: P01-T02; FR-002, FR-003.
* Evidence: custody selection is recorded, but no dedicated risk states that snapshots cannot recover sealed data without sufficient shares.
* Concern: the most irreversible custody failure should not be inferred from general backup prose.
* Impact: false confidence in backups.
* Smallest useful change: add the irreversible-loss risk with user custody ownership, independent protected storage, and quorum verification.
* Action owner: planning parent.
* Exact resolving evidence: risk row explicitly separates snapshot preservation from unseal recovery and does not suggest root tokens can replace shares.
* Decision route: direct planner correction.

<!-- rpi:critique id=PC-007 -->
### PC-007 [Medium]: A few negative integration cases lack explicit fixture ownership

* Related IDs: P03-T02, P04-T02, P05-T01; FR-006 through FR-008.
* Evidence: per-owner isolation and atomic certificate bundles are required, but the aggregate failure list does not name cross-owner access, mismatched key/certificate, or partial bundle replacement; move races are named as a risk.
* Concern: these materially different paths could be omitted from qualification.
* Impact: incomplete acceptance evidence for the changed surfaces.
* Smallest useful change: assign those negative cases, plus assignment changes during resolution, to their owning existing check suites and reusable integration runner.
* Action owner: planning parent.
* Exact resolving evidence: P03/P04 fixture requirements include the cases and maintain the existing additions budget.
* Decision route: direct planner correction.

<!-- rpi:critique id=PC-008 -->
### PC-008 [Medium]: Operational defaults should be distinguished from user decisions

* Related IDs: P01-T02, P02-T01; NFR-003.
* Evidence: daily snapshots, 24-hour stale threshold, 15-minute token TTL, and one-hour hard maximum were selected in the draft; the research establishes mechanisms rather than those specific values.
* Concern: provenance and operational rationale are implicit.
* Impact: future operators may mistake planner defaults for vendor constraints or explicit user choices.
* Smallest useful change: identify them as planner-selected initial defaults, state rationale, and qualify renewal under those values. Meaningful availability/recovery changes still require user approval.
* Action owner: planning parent.
* Exact resolving evidence: decisions record and task-local qualification distinguish defaults from confirmed D1-D3.
* Decision route: direct planner correction.

## Strengths and Residual Risk

* The selected single-host/manual-unseal availability boundary and retained issuer are authoritative user decisions, not defects.
* Existing domain validation, reference/version gating, least privilege, and Data Protection bootstrap separation are preserved.

## Questions or Blocking Evidence Gaps

* None needing a new architecture decision. Private custody/destination details remain an execution gate before initialization/migration.

## Limitations

* No implementation, release/client qualification, lab inspection, or rendered-diagram check.
* Some lens returns were partial; parent completed the remaining material evidence/coverage boundary directly rather than rerunning agents.
* This is one initial assessment; plan corrections do not require a second critique.

## Recommended Next Action

* Highest-impact findings: PC-001 and PC-002.
* Action owner: planning parent.
* Smallest next action: apply one coherent correction batch, record every disposition, and finalize if no blocking item remains.
* User response required: no.
