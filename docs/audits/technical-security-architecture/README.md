# Technical and Security Architecture Dossier

> [!IMPORTANT]
> **Non-assurance and non-compliance statement.** This dossier is a repository-grounded technical description of the application baseline identified below. It is not an audit opinion, certification, attestation, penetration-test report, compliance determination, control-operating-effectiveness assessment, and it is not a risk acceptance. No production environment, operator process, external service, release execution, incident record, backup restore, or deployed control sample was examined unless a chapter says so explicitly.

## Review identity

| Item | Value |
|---|---|
| Application | XE Local AI Engine |
| Frozen application baseline | `7e64ed589e14eecc0e522e807d2e531a1095d19a` |
| Review date | 2026-07-28 |
| Intended reader | External technical or security reviewer without source-code access |
| Primary evidence | Repository implementation, tests, configuration, scripts, and documentation at the frozen baseline |
| Excluded evidence | Production telemetry, host configuration, identity-provider records, release transcripts, incident records, restore exercises, access reviews, and control-owner attestations |

The frozen baseline hash belongs to the pre-consolidation history and no longer resolves in this repository, which is now the single home for source and releases (see `docs/agent-knowledge.md`, “Consolidated to one repo.”). It identifies the reviewed snapshot; it is not a commit you can check out here.

The source paths named in the chapters are internal traceability references. They identify where a statement was checked; they are not evidence assumed to be available to the recipient.

## How to read control statements

The dossier keeps four evidence states separate:

- **Implemented** — the behavior is present in the frozen source baseline.
- **Test-supported** — repository tests exercise the stated behavior. Test source, or a passing local test result, does not establish that the control operated in a deployed environment.
- **Documented** — a design, procedure, or limitation is described in repository documentation. Documentation alone does not prove implementation or operation.
- **Gap / not evidenced** — the repository does not define the control, or the operating evidence needed to substantiate it was not supplied.

Terms such as “protects,” “rejects,” and “encrypts” describe code paths at the frozen baseline, not a guarantee against every attack or a claim of operating effectiveness. Residual-risk observations are technical review findings; ownership, treatment, priority, and acceptance remain unassigned unless expressly evidenced.

## Dossier map

1. [System Context and Trust Boundaries](01-system-context-and-trust-boundaries.md)
   Deployment modes, components, local and external interfaces, privilege boundaries, and the meaning of the process execution boundary.
2. [Sensitive Assets, Data, and Credential Lifecycle](02-sensitive-assets-data-and-credential-lifecycle.md)
   Representative sensitive assets, key derivation, token and credential storage, selective encryption, local files, data egress, and deletion/recovery boundaries.
3. [Threat Scenarios, Controls, and Residual Risk](03-threat-scenarios-controls-and-residual-risk.md)
   Abuse scenarios, implemented controls, repository test evidence, remaining exposure, and unassigned treatment decisions.
4. [Operations, Resilience, and Response](04-operations-resilience-and-response.md)
   Logs, telemetry, health, diagnostics, child-process supervision, shutdown/restart behavior, backup, restore, and incident-response evidence.
5. [Supply Chain, Release, and Governance](05-supply-chain-release-and-governance.md)
   Dependency resolution, vulnerability and license checks, manual release gates, artifact integrity, signing/SBOM gaps, and governance boundaries.
6. [Claim Traceability and Evidence Availability](06-claim-traceability-and-evidence-availability.md)
   Material-claim register, source and test traceability, evidence availability, acceptance status, and control ownership.

## Executive technical posture

### Controls present in the baseline

- The supported local administration surface is designed for same-machine use. The host applies a loopback bind guard and the local API applies transport-peer, `Host`, and `Origin` checks before routing.
- Local API endpoints add authentication and role policy on top of the loopback gate. Access JWTs are short-lived by default, refresh tokens are rotated, only refresh-token hashes are stored in the identity database, and the browser refresh cookie is `HttpOnly`, `Secure`, `SameSite=Strict`, and limited to the auth path.
- One 32-byte operator secret is a root input to separate HKDF-SHA256 derivations for SQLite field/blob protection, JWT signing, and non-Windows Data Protection key-ring wrapping. Distinct derivation contexts keep those derived keys separate.
- Selected database fields and managed blobs use AES-256-GCM with random nonces and context-binding associated data. The database is nevertheless plain SQLite, not whole-file encryption.
- Cloud, Hugging Face, GitHub, Codex OAuth, and central-platform worker credentials are protected through ASP.NET Core Data Protection and stored in purpose-separated files with owner-oriented file permissions.
- Local model servers and tool/Development commands are supervised host child processes, but their controls differ. Model-runtime supervisors provide readiness/liveness checks, retry/reaping, and tree-kill behavior; their normal launchers inherit the host process environment. The process sandbox used for tool and Development commands clears the inherited environment, applies path/symlink guards, bounds output and execution time, and tree-kills its children.
- The repository includes tests for important security behaviors, including local API rejection, authentication/refresh behavior, AES-GCM tamper rejection, Data Protection key-ring failure, credential-store handling, process-jail path guards, and Development workspace identity checks.

### Boundaries that must remain visible

- **Development Mode and the process “sandbox” are not operating-system isolation.** Repository builds, tests, scripts, source generators, and other executed code run as the host user and share the host network. The application adds workflow and path controls, but it does not confine malicious code from the host user’s other accessible resources.
- **The tracked Aspire development default is not a confidential or installation-unique operator secret.** Although Aspire marks the parameter as sensitive in its UI, B1 commits one shared development-only default. Anyone with the source can derive the same keys for data created under that unchanged default. Desktop packaging instead generates and persists a per-installation `node.key`.
- Loopback restrictions assume the supported same-user, same-machine deployment. Same-host reverse proxying or routable/headless exposure is not a supported security posture.
- SQLite protection is selective field/blob encryption. Structural fields, indexes, and deliberately searchable content can remain plaintext.
- The operator secret and Data Protection key ring are recovery-critical. The repository does not provide a complete key-rotation, escrow, coordinated backup, or recovery procedure.
- Use of cloud models, Hugging Face, GitHub, Codex OAuth, MCP servers, update sources, or the central WorkerHub creates explicit external trust and data-egress boundaries.
- Logs, browser diagnostic bundles, durable run metadata, local database snapshots, release scripts, and hashes are useful technical evidence primitives. They are not substitutes for centralized monitoring, incident response, tested restore, artifact signing, provenance, or retained operational evidence.

## Evidence limitations

This dossier does not claim:

- that every deployed endpoint, dependency, process, data field, or external destination was observed;
- that a repository test was run in a production-equivalent environment;
- that the controls were continuously enabled, monitored, reviewed, or effective;
- that no secret or personal data can appear in logs, diagnostic text, model prompts, tool output, or third-party services;
- that encrypted data is recoverable after loss or change of the operator secret or key ring;
- that a released binary corresponds to the reviewed source without a recipient-verifiable signed provenance chain; or
- that any named residual risk has an assigned owner, approved treatment, due date, or formal acceptance.

The detailed claim register and recipient-availability status are in [Chapter 06](06-claim-traceability-and-evidence-availability.md).
