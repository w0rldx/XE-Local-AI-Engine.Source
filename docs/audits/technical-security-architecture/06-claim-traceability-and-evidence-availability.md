# 06 — Claim Traceability and Evidence Availability

## Review boundary

| Item | Value |
|---|---|
| Frozen application baseline | `7e64ed589e14eecc0e522e807d2e531a1095d19a` |
| Review date | 2026-07-28 |
| Baseline shorthand | **B1** = the frozen commit and review date above |
| Evidence boundary | The matrix records repository implementation, test design, scripts/configuration, dated external-state notes, inference, and explicit gaps. No source package, production telemetry, release transcript, test report, incident record, restore exercise, SBOM, signature, or formal acceptance record is assumed to be delivered with this dossier. |

## How to read the matrix

### Claim state

| State | Meaning |
|---|---|
| Implemented | Present in the frozen source or release script |
| Test-supported | Repository tests exercise the implemented behavior |
| Conditional | Present only when an operator/environment enables or invokes it |
| Observed external state | A dated external-system observation is recorded, but the external evidence is not bundled |
| Gap / not implemented | The reviewed baseline does not produce the control or artifact |
| Not evidenced | The behavior may exist operationally outside the repository, but no evidence was supplied |

### Evidence type

- **Source implementation** supports design and implementation existence.
- **Automated test source** supports intended behavior and testability; only a fresh signed or controlled test result would evidence execution.
- **Script/configuration** supports the defined gate, not proof that the gate ran.
- **Repository documentation** records a reviewed fact or operating instruction, not operating effectiveness.
- **External-system observation** requires separately captured output for recipient verification.
- **Absence review** means the expected artifact or mechanism was not found in the reviewed baseline; it is not proof that no external organizational process exists.

### Recipient availability

| Availability | Meaning |
|---|---|
| Dossier statement only | The recipient receives this description, not the supporting internal artifact |
| Separately exportable | The application or release process can generate an artifact, but none is bundled |
| Not captured/provided | No evidence instance was supplied for the baseline |
| Not produced | The baseline has no mechanism that generates the expected artifact |

## Material-claim matrix

Every row includes the required state, internal traceability, evidence characterization, recipient availability, baseline, limitation, posture, residual risk, acceptance, and ownership fields. `Unassigned` means the reviewed repository and supplied evidence do not name an accountable role; this dossier does not invent one.

| Claim / control objective | Claim state | Internal traceability | Evidence type | Recipient availability | Baseline | Limitation | Current posture | Residual risk | Acceptance status | Owner role / unassigned |
|---|---|---|---|---|---|---|---|---|---|---|
| O-01 — Retain recent host logs with request/trace correlation | Implemented; test-supported in parts | `LoggerExtensions.cs`; `Program.cs`; `AccessTokenQueryRedactor.cs` | Source implementation; automated test source | Dossier statement only; local logs separately exportable | B1 | Seven rolled files; daily/50 MiB roll; no centralized or immutable store evidenced | Local bounded rolling logs outside `Testing` | Local deletion/eviction, host loss, or missing export can remove evidence | No formal acceptance evidence | Unassigned |
| O-02 — Export traces, metrics, and structured logs | Conditional; test-supported | `ServiceDefaults/Extensions.cs`; `Program.cs`; `AgentTelemetryOptions.cs`; `ServiceDefaultsTelemetryTests.cs` | Source implementation; automated test source; operator runbook | Dossier statement only; OTLP output not captured/provided | B1 | Exporter exists only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; desktop/RC default has no exporter | Instrumented, opt-in export; sensitive message capture separately defaults off | Telemetry evaporates on exit by default; no collector retention or alert operation evidenced | No formal acceptance evidence | Unassigned |
| O-03 — Expose liveness and readiness | Implemented; test-supported | `Program.cs`; `ConfigureServices.cs`; `NodeSqliteHealthCheck.cs`; `WorkerHealthCheck.cs`; health-check tests | Source implementation; automated test source | Dossier statement only; runtime samples not captured/provided | B1 | Liveness has no dependency checks; degraded readiness returns HTTP 200 | Local health endpoints with per-check readiness body | A status-only monitor can miss degradation; no alert ownership or polling evidence | No formal acceptance evidence | Unassigned |
| O-04 — Preserve durable invocation metadata without OTLP | Implemented | run-envelope and execution-log endpoints/stores; approval audit recorder | Source implementation | Dossier statement only; API export not provided | B1 | Metadata intentionally excludes message content; restart backfills can omit model/usage/duration | Durable metadata supports correlation and terminal-state review | Incomplete reconstruction and no evidence-retention policy | No formal acceptance evidence | Unassigned |
| O-05 — Capture client diagnostic context without automatic transmission | Implemented; test-supported | React diagnostics `Redact.ts`, `BuildSnapshot.ts`, `SnapshotStore.ts`, `ExportSnapshot.ts`, related tests | Source implementation; automated test source | Local zip separately exportable; no baseline bundle provided | B1 | Browser-local; cap of 25 snapshots/about 25 MiB; persistence request is best-effort; zip unsigned and unencrypted | Redacted local snapshots with manual import/export | Profile deletion/eviction or missing export loses evidence; arbitrary error text can still carry sensitive context | No formal acceptance evidence | Unassigned |
| R-01 — Supervise local inference child processes | Implemented; test-supported | llama and image process supervisors; supervisor crash/race tests | Source implementation; automated test source | Dossier statement only; process logs/results not provided | B1 | Covers local child processes, not node redundancy or remote failover | Readiness/liveness probes, bounded retries, reaping, tree-kill teardown | Node/host failure still interrupts work; automatic output recovery is not provided | No formal acceptance evidence | Unassigned |
| R-02 — Bound graceful worker shutdown | Implemented; test-supported | `WorkerShutdownDrainService.cs`; `WorkerShutdownDrainOptions.cs`; `Program.cs`; shutdown tests | Source implementation; automated test source | Dossier statement only; shutdown sample not provided | B1 | Default 30-second drain plus five-second outer grace; incomplete stages are abandoned/logged | Best-effort bounded drain | Active work or queued telemetry can remain incomplete at deadline | No formal acceptance evidence | Unassigned |
| R-03 — Reconcile interrupted work after restart | Implemented; test-supported | `NodeChatRestartRecoveryService.cs`; `ScheduledJobRunStore.cs`; `Program.cs`; recovery/store tests | Source implementation; automated test source | Dossier statement only; restart exercise not provided | B1 | Terminalizes records; does not resume/re-dispatch or reconstruct volatile output and usage details | Prevents indefinitely pending/running records | Interrupted work requires user/operator action and can lose generation detail | No formal acceptance evidence | Unassigned |
| B-01 — Take a consistent database snapshot before migrations | Implemented; test-supported | `NodeDbBackupService.cs`; `NodeDbBackupOptions.cs`; `Program.cs`; `NodeDbBackupServiceTests.cs` | Source implementation; automated test source | Local snapshots separately exportable; no baseline snapshot provided | B1 | Runs only for pending node-chat migrations; failure is logged and swallowed; retains three by default | Best-effort local migration safety net | A migration can proceed without a fresh snapshot; other node state is not covered as one recovery set | No formal acceptance evidence | Unassigned |
| B-02 — Provide recoverability/restore and disaster-recovery objectives | Gap / not implemented; not evidenced externally | Absence review across application docs, source, and release/runbook surfaces | Absence review | Not captured/provided | B1 | No restore automation/exercise, periodic or off-device backup, RTO, RPO, or complete-node recovery procedure found | Pre-migration snapshot only | Data loss or extended recovery is possible and recoverability is unproven | No formal acceptance evidence | Unassigned |
| I-01 — Operate a defined incident-response process | Not evidenced | Repository review; chapter 04 incident-response gap | Absence review | Not captured/provided | B1 | No application-specific roles, severity, escalation, SLA, evidence custody, notification decision, or post-incident process supplied | Diagnostic primitives exist; governance process not evidenced | Delayed/inconsistent response and loss of evidence | No formal acceptance evidence | Unassigned |
| S-01 — Make dependency versions reviewable and repeatable | Implemented; partial | `Directory.Packages.props`; `Directory.Build.props`; `global.json`; `dotnet-tools.json`; `package.json`; `pnpm-lock.yaml` | Configuration and lockfile | Dossier statement only; files not bundled | B1 | No NuGet `packages.lock.json`; SDK allows latest-feature roll-forward | Central .NET pinning/transitive pinning and frozen pnpm install | Tool/resolved-graph differences can remain across environments | No formal acceptance evidence | Unassigned |
| S-02 — Inventory third-party licenses | Implemented; partial | `NOTICE`; `GenerateLicenses.mjs`; generated license JSON; template license/provenance files | Generated repository artifact; script | License JSON separately deliverable; not bundled | B1 | Direct frontend and top-level backend packages only; backend fallback can reuse old entries; no explicit allow/deny policy | 96-entry direct/top-level manifest, zero `Unknown` entries at B1 | Transitive/license-policy gaps and stale fallback can leave incomplete disclosure | No formal acceptance evidence | Unassigned |
| S-03 — Block releases with known dependency advisories | Implemented in manual gate; test-supported parser | `package-tester-win.ps1`; Pester tests | Release script; automated test source | Audit outputs not captured/provided | B1 | Point-in-time package-manager advisory checks; no continuous scan, binary scan, runtime/model scan, or penetration test | Frontend high/critical and NuGet top-level/transitive findings stop manual packaging | Advisories can appear between releases; unscanned artifacts/sources remain | No formal acceptance evidence | Unassigned |
| G-01 — Enforce validation before publishing tester RCs | Implemented in manual gate | `package-tester-win.ps1`; `publish/README.md`; release-script lint/Pester tests | Release script; documentation; automated test source | Release transcript/test outputs not captured/provided | B1 | Manually invoked on a Windows workstation; smoke-test content/operator/environment are not recorded by the hash check | Effective release lane runs frontend/backend gates, draft-first packaging, and hash verification | Operator/environment dependence and missing retained evidence reduce independent assurance | No formal acceptance evidence | Unassigned |
| G-02 — Gate branch/merge changes with CI | Observed external state: not operating | `agent-knowledge.md`; validation matrix; tracked workflow YAML | Dated external-system observation; repository documentation | GitHub settings/API output not captured/provided | B1; external state last checked 2026-07-24 | YAML declares intent, but build/release workflows are recorded `disabled_manually`; E2E unregistered; 6 runs, 0 successes | No repository-evidenced or GitHub Actions branch/merge CI safety net | Defects can reach release preparation before the effective gate runs | No formal acceptance evidence | Unassigned |
| P-01 — Bind release assets to source reference and content hashes | Implemented in manual gate | `package-tester-win.ps1` manifest/draft-publication functions | Release script; generated SHA-256 JSON when run | Manifest separately generatable; no baseline manifest provided | B1 | Manifest and hashes are unsigned; smoke-test assertion records a hash, not the test procedure/result | Source commit/tag plus five-asset SHA-256 manifest and re-download verification | A coordinated replacement of artifact and unsigned manifest is not prevented | No formal acceptance evidence | Unassigned |
| P-02 — Authenticate publisher and provide signed provenance | Gap / not implemented | Manual pack command; dormant release workflow signing follow-up | Source/script absence review | Not produced | B1 | No code signing, signed manifest, builder attestation, or certificate chain | Unsigned artifacts | Recipient cannot cryptographically authenticate publisher/build identity from the artifact set | No formal acceptance evidence | Unassigned |
| P-03 — Produce a complete release SBOM | Gap / not implemented | Effective release-path absence review; direct license-manifest generator | Absence review | Not produced | B1 | No CycloneDX/SPDX output; direct-license JSON lacks transitive graph and SBOM relationships | No release SBOM | Incident/advisory response cannot rely on a release-specific complete component inventory | No formal acceptance evidence | Unassigned |
| C-01 — Restrict local API access and protect stored sensitive payloads | Implemented; test-supported in constituent controls | loopback security middleware; auth/JWT configuration; encryption interceptors/key services; security tests | Source implementation; automated test source | Dossier statement only; source/test artifacts not bundled | B1 | Design evidence only; no deployed configuration/penetration/operating sample supplied | Loopback, authentication, authorization, and application-layer encryption controls exist | Misconfiguration, endpoint defects, key loss, or untested operating conditions remain | No formal acceptance evidence | Unassigned |
| C-02 — Treat Development Mode as an OS isolation boundary | Claim explicitly rejected | Development process execution and sandbox paths; architecture context | Source implementation; design analysis | Dossier statement only | B1 | Development commands execute as host-user processes; path/process guards do not create a separate OS security boundary | Development Mode is privileged local automation, not containment equivalent to a VM/container sandbox | Compromised or malicious approved work can affect host-user-accessible resources | No formal acceptance evidence | Unassigned |
| C-03 — Bind Development command execution and evidence to code-owned profiles | Implemented; test-supported | `DevelopmentCommandProfileCatalog.cs`; `DevelopmentCommandProfileImport.cs`; `DevelopmentManagementService.ResolveCommandProfile`; `DevelopmentWorkspaceTools.cs`; command-profile and guard tests | Source implementation; automated test source; design analysis | Dossier statement only | B1 | React confirmation is a UI workflow control; omitted profiles fall back to import then detection; `generic-git` provides only status and whitespace validation | Code-owned profile selection; separate import/worktree/canonical-profile digests and protocol/catalog versions; catalog/version or canonical-content drift, and within-attempt mutation of the worktree import file, fail closed; D3 protects pre-existing tests from modify/delete/rename while allowing added/copied tests | A direct backend caller can omit UI confirmation; generic repositories have no build/test command evidence; approved host-user commands remain outside OS isolation | No formal acceptance evidence | Unassigned |
| C-04 — Use a confidential, installation-specific operator secret | Mixed: desktop implemented; Aspire development gap | `DesktopBootstrap.cs`; `AppHost.cs`; `XE-Local-AI-Engine.AppHost/appsettings.Development.json`; operator-secret and key-holder implementations | Source implementation; configuration review; design analysis | Dossier statement only | B1 | Desktop generates `node.key`; AppHost marks its parameter sensitive but B1 commits one shared development-only default and does not enforce a confidential override | Packaged desktop has a per-installation key; default Aspire development data is protected only from principals who do not know the tracked value | Any source holder can derive keys for data produced under the unchanged AppHost default; development data may be falsely treated as confidential | No formal acceptance evidence | Unassigned |
| E-01 — Demonstrate control operating effectiveness | Not evidenced | Entire repository evidence set | Source/test/script evidence only | Not captured/provided | B1 | No controlled sample period, production logs, signed test report, incident sample, restore exercise, or release evidence pack | Implementation design is documented; operation is not demonstrated | Controls may be misconfigured, bypassed, or unused in deployment | No formal acceptance evidence | Unassigned |

## Evidence availability register

The following artifacts would materially improve recipient verification. Their listing is a request/evidence map, not a promise that they exist.

| Evidence ID | Requested artifact | Purpose | Current availability |
|---|---|---|---|
| EV-01 | Baseline manifest identifying commit, version, review date, and reviewed file hashes | Reproduce the review boundary without disclosing the full source | Not captured/provided |
| EV-02 | Controlled frontend/backend build, lint, test, and coverage transcript for the release commit | Demonstrate gate execution | Not captured/provided |
| EV-03 | Raw `pnpm audit` and NuGet vulnerability JSON with tool/advisory timestamps | Verify point-in-time dependency review | Not captured/provided |
| EV-04 | Generated third-party-license JSON and generator/tool output | Verify direct/top-level package disclosure | Separately deliverable; not bundled |
| EV-05 | Tester packaging transcript, release notes, SHA-256 manifest, draft metadata, smoke-test record, and publication record | Verify the end-to-end manual release ceremony | Not captured/provided |
| EV-06 | Signature, certificate chain, and signed provenance attestation | Authenticate publisher and builder | Not produced |
| EV-07 | CycloneDX/SPDX release SBOM, ideally signed and tied to the asset hashes | Verify the complete release component graph | Not produced |
| EV-08 | GitHub workflow/settings/API export showing workflow registration and disabled state | Independently verify the dormant CI observation | Not captured/provided |
| EV-09 | Redacted rolling-log sample, health response, browser diagnostic zip, OTLP trace/metric sample, and durable run-metadata export | Demonstrate diagnostic operation and content boundaries | Separately exportable; not provided |
| EV-10 | Backup inventory plus a documented, witnessed restore exercise covering database, keys, identity, and adjacent node state | Demonstrate recoverability | Not captured/provided |
| EV-11 | Incident-response policy, role assignment, exercise/incident sample, evidence-custody record, and post-incident review | Demonstrate response governance | Not captured/provided |
| EV-12 | Formal risk register entry naming owner, decision, scope, expiry/review date, and compensating controls for accepted gaps | Distinguish known residual risk from unmanaged risk | Not captured/provided |

## Assurance boundary

This matrix does not assert:

- certification or compliance with a named framework;
- legal sufficiency of licenses or notices;
- a service level, recovery objective, or backup guarantee;
- that implementation and tests operated in a recipient environment;
- that an external organizational control does not exist; or
- that any residual risk has been formally accepted.

Where source implementation exists but operating evidence is absent, the current posture is **implemented, operating effectiveness not evidenced**. Where an artifact is not produced by the baseline, the posture is **gap / not implemented**. Acceptance and ownership remain **unassigned** unless a separately controlled record is supplied.
