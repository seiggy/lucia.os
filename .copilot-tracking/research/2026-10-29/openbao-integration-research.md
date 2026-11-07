# Task Research: openbao-integration

| Field | Value |
|---|---|
| Date | 2026-10-29 |
| Researcher | Primary planner using `rpi-research` |
| Output mode | Analysis supporting the active plan |

## Executive Summary

Focused research is complete. OpenBao can use integrated Raft storage without an external database, authenticate machines using constrained certificate roles, and render application secrets with its own agent. Deployment topology and unseal custody are owner decisions, not implementation details.

Confidence is high in the documented contracts, but no runtime client compatibility or lab recovery drill has been performed. This evidence supports planning, not a claim that the integration is already qualified.

## What You May Not Know

Auto-unseal recovery keys cannot replace a missing seal key/service. A snapshot alone therefore does not guarantee recoverability. Certificate authentication also requires the actual client TLS connection to reach OpenBao; a normal TLS-terminating reverse proxy does not preserve that proof.

## Findings

### Storage and recovery must be designed together

Raft persists service data locally and supports replication; snapshots are the supported backup interface. Shamir requires unsealing after restart. Auto-unseal introduces a separate key/service dependency; recovery shares are not a substitute if it disappears.

* Questions: Q1.
* Evidence state: evidence-backed finding.
* Evidence: W1, W2, W5, W7.
* Confidence and limits: high for documented behavior; initial host availability, restore time, and backup destination are not established by documentation.

### Existing node identity can inform authentication, but does not confer app isolation

Lucia validates a CA-issued node certificate with a device-bound CN and exact DNS SAN. OpenBao supports certificate roles with name constraints and scoped token policies. Trusting the whole CA without role constraints would admit unintended workloads. A node identity must not grant unrestricted access to every application's secrets.

* Questions: Q2.
* Evidence state: evidence-backed finding for the API contracts; proposed integration still needs a compatibility fixture.
* Evidence: C4, W3, W6.
* Confidence and limits: high for CN/SAN constraints and client-auth requirements; certificate renewal, node removal, token revocation, and stack moves need end-to-end implementation checks.

### Agent templates solve delivery, not application reload or safe outage behavior

OpenBao Agent renders files and can refresh values. Missing-key behavior must be configured explicitly. A changed file does not automatically reload a consumer, update a container environment, or make expired dynamic credentials usable.

* Questions: Q2, Q3.
* Evidence state: evidence-backed finding.
* Evidence: W4, W10.
* Confidence and limits: high for agent behavior; catalog-specific file support and restart choreography remain implementation work.

### OpenBao does not remove host trust or audit availability dependencies

The security model excludes malicious execution on the underlying host. Keeping a static unseal key beside encrypted data changes the threat boundary, rather than eliminating bootstrap secrets. OpenBao explicitly cautions that static auto-unseal should rely on an existing source of trust. Audit writes can also block API operations when all enabled devices fail.

* Questions: Q1, Q3.
* Evidence state: evidence-backed finding.
* Evidence: W5, W8, W9.
* Confidence and limits: high; no independent seal trust source has been established for this installation.

### Current Lucia storage is not one interchangeable secrets provider

Identity bootstrap uses private local files; runtime credentials use ASP.NET Data Protection; stack environment values are protected centrally and sent to assigned nodes; app certificates already cross a scoped signing-worker boundary. A migration must preserve those distinct purposes and existing disconnected/revoked states.

* Questions: Q2, Q3.
* Evidence state: evidence-backed finding.
* Evidence: C1-C4.
* Confidence and limits: representative source paths establish the pattern, not an exhaustive secret inventory.

## Recommendation and Alternatives

OpenBao is already selected by the user. This research does not reopen product selection.

| Option | Benefits | Costs and risks | Evidence | Disposition |
|---|---|---|---|---|
| Single server with Raft | Simple foundation; compatible storage choice for later replication | Outage during host failure or maintenance; manual Shamir unseal if selected | W1, W2, W7 | Viable; user decision |
| Three voting servers in independent failure domains | Majority can tolerate one voter failure | More deployment, seal, networking, and recovery operations; requires actual independent hosts | W2 | Viable; user decision |
| Manual Shamir | No external unseal service | Owner action after restart; custody must be planned | W1 | Viable; user decision |
| Auto-unseal | Unattended restarts | Requires independently recoverable seal mechanism; local static file weakens separation | W1, W5 | Viable only after owner chooses trust boundary |

## Scope and Questions

* Goal: close decision-critical gaps in the active integration plan.
* Audience: Lucia owner and implementation agent.
* In scope: official OpenBao documentation on storage, sealing, authentication, and agent templates; already-read Lucia contracts.
* Out of scope: live lab inspection, production changes, vendor comparison, and implementation.
* Criteria: no circular recovery dependency; retain controller/privileged-worker isolation; scoped workload credentials.

| ID | Question | Source | Status |
|---|---|---|---|
| Q1 | What are the storage, unseal, and restore implications of single-server versus HA deployment? | Planning gap | Answered; topology/custody belongs to user |
| Q2 | Can existing node certificates and scoped agents support secrets delivery without universal tokens? | Planning gap | Answered at contract level; runtime proof required |
| Q3 | What limitations invalidate assumptions about transparent renewal or Vault compatibility? | Planning gap | Renewal and trust constraints answered; specific client compatibility remains a qualification gate |

## Decisions and Feedback

* OpenBao selection is confirmed; topology and certificate scope remain owned by the planning parent and user.

## Risks and Open Questions

* Owner must select availability expectations and seal custody.
* Verify the chosen .NET client's authentication, KV-v2, renewal, cancellation, and error semantics against the pinned OpenBao release.
* Do not treat KV-v2 static secrets as automatically expiring leases.
* Do not treat snapshots as a backup of external bootstrap/seal keys or external audit files.

## Planning Readiness and Next Step

| Field | Record |
|---|---|
| Research disposition | Executed; complete |
| Decision participation | User-owned in parent; evidence gathering requires no additional intake choice |
| Planning readiness | Evidence ready for drafting; final plan waits on parent architecture choices |
| Continuation owner | Active planning context |
| Next action | Return evidence to the active planner for user-owned topology and scope choices |
| Primary evidence file | `.copilot-tracking/research/2026-10-29/openbao-integration-research.md`; date from user-supplied current date |

## Research Record

### Method and Boundaries

* Posture: focused; bounded parent planning gaps with named official source areas.
* Evidence root: `.copilot-tracking`; default repository tracking root.
* Prior knowledge: prior product/license comparison reused; repository source reads establish current architecture.
* Candidates: OpenBao integrated storage, seal, certificate auth, AppRole, agent templates, snapshots.
* Limits: research only; no lab access or software execution; no helper agents.

### Extensions and Participation

* `AGENTS.md` and `lucia-lab`: selected for no-secrets, public-repository, and operational boundaries.
* `aspire`: selected for existing AppHost ownership; runtime actions not applicable to research.
* Security-assessment and ADR-authoring extensions: skipped; this is architecture evidence gathering, not a vulnerability assessment or formal ADR request.
* No question at intake: evidence questions are defined by the active plan; material architecture decisions return to its user-owned protocol.

### Research Cycle Log

#### Cycle 1

##### Wave 1: Wider

* Breadth: examined seal lifecycle, Raft storage, certificate authentication, and agent rendering.
* Evidence: W1-W4.
* Reflection: independent storage and workload proof are available; unseal custody and TLS termination require deeper treatment.

##### Wave 2: Deeper

* Detail: examined static seal key handling, exact certificate role restrictions, snapshot operations, and AppRole bootstrap.
* Evidence: W5-W7, W11.
* Reflection: supported integration contracts exist; neither node identity nor auto-unseal should be treated as automatically safe across deployment boundaries.

##### Wave 3: Contrarian

* Challenges: considered host compromise, audit backend failure, KV-v2 semantics, and agent renewal/re-render behavior.
* Evidence: W8-W10, W12.
* Reflection: rejected assumptions that encryption protects against host root, that audit is always nonblocking, that all secrets lease-expire, or that updating a secret transparently updates its consumer.

##### Synthesis and Re-entry

* Accepted: OpenBao capabilities and the current Lucia trust boundaries.
* Rejected: universal workload tokens, unconstrained CA trust, automatic safety from a local static unseal key, and transparent application reload.
* Deferred: exact release/client versions, hardware seal mechanism, HA host placement, and optional OpenBao PKI migration until their owner or qualification step resolves them.
* Another cycle: not required for this bounded evidence task. Official contracts answer the research questions; remaining material choices are owner preferences, while runtime verification belongs to implementation.

### Evidence Log

* Helpers: none.

| ID | Claim | Source or location | Retrieved/version | Tool | Confidence |
|---|---|---|---|---|---|
| C1 | Private bootstrap files and existing CA | `src/Lucia.Homelab.Identity.AppHost/AppHost.cs`, `Secret`, `identity-ca` | Workspace baseline | Read | High |
| C2 | Protected provider credential state | `src/Lucia.Homelab.Server/Host/HuggingFaceManagementCredentials.cs`, `HuggingFaceCredentialService` | Workspace baseline | Read | High |
| C3 | Protected stack env and node delivery contract | `src/Lucia.Homelab.Server/Stacks/StackStore.cs`, `StoredStack`, `NodeDesiredStack` | Workspace baseline | Read | High |
| C4 | Scoped signing and node identity | `src/Lucia.Homelab.Server/Security/AppCertificates.cs`, `Ensure`; `src/Lucia.NodeAgent/ManagedIdentity.cs`, `ValidateConfiguration` | Workspace baseline | Read | High |
| W1 | Seal lifecycle and recovery-key limitation | https://openbao.org/docs/concepts/seal/ | 2026-10-29; current docs | Fetch | High |
| W2 | Integrated storage and replication | https://openbao.org/docs/configuration/storage/raft/ | 2026-10-29; current docs | Fetch | High |
| W3 | Client-certificate auth needs real TLS client proof | https://openbao.org/docs/auth/cert/ | 2026-10-29; current docs | Fetch | High |
| W4 | Agent templates and missing-key behavior | https://openbao.org/docs/agent-and-proxy/agent/template/ | 2026-10-29; current docs | Fetch | High |
| W5 | Static auto-unseal trust warning | https://openbao.org/docs/configuration/seal/static/ | 2026-10-29; current docs | Fetch | High |
| W6 | Certificate role CN/SAN constraints and token TTL | https://openbao.org/docs/api/auth/cert/ | 2026-10-29; current docs | Fetch | High |
| W7 | Raft snapshots | https://openbao.org/docs/commands/operator/raft/ | 2026-10-29; current docs | Fetch | High |
| W8 | Host-control threat exclusions | https://openbao.org/docs/internals/security/ | 2026-10-29; current docs | Fetch | High |
| W9 | Audit failure and sensitive-value behavior | https://openbao.org/docs/audit/ | 2026-10-29; current docs | Fetch | High |
| W10 | Certificate rendering is not consumer reload | https://openbao.org/docs/agent-and-proxy/agent/template/ | 2026-10-29; current docs | Fetch | High |
| W11 | Scoped machine roles and bootstrap credentials | https://openbao.org/docs/auth/approle/ | 2026-10-29; current docs | Fetch | High |
| W12 | KV-v2 data/metadata ACL distinction | https://openbao.org/docs/secrets/kv/kv-v2/ | 2026-10-29; current docs | Fetch | High |

#### Contradictions and Conflicts

The broad overview's lease wording is not used to infer expiry of KV static values. The specific KV-v2 and agent documentation governs that distinction.

### Artifact Self-Check

* Summary, findings, evidence IDs, alternatives, decisions, risks, and continuation are synchronized.
* Wider, deeper, and contrarian waves completed without helper agents.
* No secret values, lab addresses, production writes, or runtime qualification claims are included.
* Research-only boundary held; the parent planner owns decisions and implementation sequencing.
