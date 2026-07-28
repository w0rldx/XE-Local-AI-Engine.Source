# 03 — Threat Scenarios, Controls, and Residual Risk

## Review boundary

| Item | Value |
|---|---|
| Frozen application baseline | `7e64ed589e14eecc0e522e807d2e531a1095d19a` |
| Review date | 2026-07-28 |
| Scope | Representative misuse and failure scenarios across local access, identity, cryptography, persistence, providers, tools, Development Mode, supply chain, diagnostics, availability, and recovery |
| Evidence boundary | Repository implementation, tests, configuration, scripts, and documentation. No penetration test, runtime control sample, production event set, host hardening review, third-party assessment, or formal risk register was supplied. |

This chapter is a threat-oriented technical review, not proof that attacks are prevented. “Test-supported” identifies repository tests, not a deployed control test. All treatment ownership and formal acceptance entries remain unassigned because no contrary evidence was supplied.

## Threat model assumptions

The implemented design assumes:

- one local operator and one host-user security context;
- supported local API access from the same machine over loopback;
- the host OS and user session are trusted more than model output, downloaded content, selected repositories, and external services;
- cloud providers, MCP servers, update/model sources, and the central WorkerHub are separate trust domains;
- outside the tracked Aspire development default, the operator secret remains confidential and stable with the node name; B1's shared AppHost default provides no confidentiality for data created without an override;
- local child processes can be supervised but are not isolated from the host user; and
- application workflow controls can reduce accidental or model-driven actions but cannot contain malicious host-user code.

If the application is placed behind a same-host proxy, bound routably, shared among untrusted OS users, or asked to execute untrusted repositories without accepting host-user code execution, the baseline threat model no longer applies.

## Evidence-state convention

| State | Meaning |
|---|---|
| Implemented | Control exists in the frozen baseline |
| Test-supported | Repository tests exercise the stated behavior |
| Documented | Design or limitation is described, without separate implementation/operation proof |
| Gap / not evidenced | Control is missing from the repository or deployed operating evidence was not supplied |

## Threat scenario register

| ID | Threat scenario and affected assets | Principal entry/path | Implemented controls | Test or documented evidence | Evidence state | Residual exposure / limitation | Control owner | Acceptance |
|---|---|---|---|---|---|---|---|---|
| T-01 | A routable or cross-origin caller reaches the local administration API, including anonymous first-run setup | Kestrel local API; forged `Host`/`Origin`; DNS rebinding; deliberate bind override | Post-start bind guard; loopback socket-peer gate; exact loopback `Host`; matching scheme/host/port for present `Origin`; protected endpoints also require JWT role policy | `LocalApiSecurityMiddlewareTests`, `LocalApiSecurityTests`, protected-local-API integration tests | Implemented; test-supported; deployed network behavior not evidenced | Same-host reverse proxying defeats the peer interpretation; explicit non-loopback opt-out exists; routes outside the local prefix need separate review | Unassigned | No formal acceptance evidence |
| T-02 | An attacker wins first-run setup or brute-forces the local administrator | Anonymous setup/login endpoints; unattended first launch | Setup semaphore and serializable transaction; completed-setup recheck; auth rate-limit policy; ASP.NET Core Identity password hashing and lockout-on-failure | `NodeAuthEndpointTests`, `NodeLoginLockoutIntegrationTests` | Implemented; test-supported; operating sample not evidenced | No out-of-band bootstrap secret or MFA; protection depends on loopback boundary and timely legitimate setup | Unassigned | No formal acceptance evidence |
| T-03 | Access or refresh token theft enables replay | Browser memory, cookie, request headers, local malware, diagnostic leakage | 15-minute default access JWT; signed issuer/audience/role claims; 64-byte random refresh token; hash-only DB record; rotation/revocation; `HttpOnly`/`Secure`/`SameSite=Strict`/path-scoped cookie; access-token query redaction | `NodeTokenServiceTests`, `NodeAuthEndpointTests`, `AccessTokenQueryRedactorTests` | Implemented; test-supported | Access JWT is bearer-only and remains valid until expiry; refresh token is not device-bound; same-user compromise remains decisive | Unassigned | No formal acceptance evidence |
| T-04 | Theft, loss, substitution, or reuse of operator-secret/key-ring material exposes or permanently orphans protected data | Environment/secret file, tracked AppHost development default, desktop `node.key`, node data directory, backup/restore, host-user compromise | Exact 32-byte root validation; desktop per-installation key generation; distinct HKDF contexts; zero-on-dispose; Windows current-user DPAPI; non-Windows AES-GCM key-ring wrapping; fail-closed encrypted-key resolution | `NodeDataProtectionKeyRingEncryptionTests`, persistence encryption tests | Implemented; test-supported for key behaviors; development-default gap | B1's shared tracked AppHost default is recoverable by source holders; per-developer override is not enforced. Same host user can access process/files; no complete rotation, escrow, coordinated backup, or recovery procedure; legacy plaintext ring elements remain compatible/readable | Unassigned | No formal acceptance evidence |
| T-05 | Offline database/blob theft or ciphertext substitution discloses or alters conversation and agent data | SQLite files, managed blobs, local snapshots | AES-256-GCM on selected fields and blobs; random nonce; AAD binds record/context/column/schema; authentication-tag failure; owner-scoped node data posture | `NodeAeadCipherTests`, `PersistenceEncryptionTests`, `ConversationUploadedFileStoreTests` | Implemented; test-supported | Plain SQLite structure, indexes, and intentionally searchable content remain plaintext; whole-dataset theft plus key theft defeats confidentiality; availability attacks remain | Unassigned | No formal acceptance evidence |
| T-06 | Sensitive prompt/document data is sent to or retained by an external AI/provider service | Cloud model, Codex OAuth, Hugging Face, MCP, WorkerHub, OTLP configuration | Explicit provider configuration/selection; purpose-protected credentials; local-provider paths; sensitive AI telemetry content defaults off; Development cloud authorization seam | Provider/configuration tests and repository design documentation | Implemented in part; test-supported in part; external operation not evidenced | Third-party retention, training, logging, jurisdiction, compromise, and prompt-injection handling are outside repository control; no deployed egress capture supplied | Unassigned | No formal acceptance evidence |
| T-07 | Model output or untrusted tool content causes an unauthorized local/external side effect | Agent tool call, MCP tool, file/process tool, approval workflow | Tool capability seams; approval policy and audit records; fixed application-authored command profiles in protected paths; path and patch validation | Tool-approval and Development validation tests; audit recorder implementation | Implemented; test-supported for selected paths | Policy/configuration can authorize risky actions; an approved action can still be harmful; MCP server behavior is external; operating approval samples were not supplied | Unassigned | No formal acceptance evidence |
| T-08 | Path traversal, symlink/reparse manipulation, or time-of-check/time-of-use race escapes an application workspace | AgentHome copy/read/write; selected folders; Development repository/worktree | Canonical root checks; under-root resolution; rejection of symlink components; Linux `O_NOFOLLOW` leaf operations; byte recheck/caps; protected path prefixes; repository identity binding | `ProcessSandboxRuntimeProviderTests`, `AgentHomeProcessWriteBackLoopTests`, selected-folder and Development workspace tests | Implemented; test-supported | Non-Linux no-follow fallback is weaker at the final open; host-user code can bypass application APIs entirely; filesystem semantics vary | Unassigned | No formal acceptance evidence |
| T-09 | A malicious or compromised repository executes code with the host user’s privileges during Development build/test | Registered repository, detached worktree, build/test/source generator/package hook | Explicit selected-folder registration; identity hash; detached engine-owned worktree; manifest/base/common-Git-dir checks; scrubbed inherited environment; reviewed apply gate; emergency feature disable | `DevelopmentWorkspaceAndCoderTests`, `DevelopmentProfileGuardTests`, Development validation/review/apply tests | Implemented; test-supported; residual risk documented | **Development Mode is not OS isolation.** Executed code shares host-user filesystem and network and can attack resources outside application path guards | Unassigned | No formal acceptance evidence |
| T-10 | A downloaded model, native runtime, update, or dependency is malicious, vulnerable, or substituted | Hugging Face/GitHub downloads, llama.cpp/stable-diffusion binaries, package restore, tester release | Version/pin controls; package-time vulnerability/license gates; selected hashes/registries; supervised runtime launch; draft-first release hash checks | Packaging scripts/tests and runtime tests; Chapter 05 traceability | Implemented in part; test-supported in part | No uniform signed model/runtime provenance, release signing, SBOM, or signed attestation; active continuous CI scan not evidenced | Unassigned | No formal acceptance evidence |
| T-11 | A local child process crashes, hangs, exhausts resources, or leaves descendants | llama-server, image server, tool/Development command | Model-runtime readiness/liveness probes, bounded retries, single-flight/lease control, reaping, and tree-kill; sandbox-command timeout, captured-output caps, cancellation, and tree-kill | Llama supervisor tests, image supervisor tests, process provider tests | Implemented; test-supported | Model-server launchers inherit the host process environment; the process sandbox has no CPU/memory/PID ceiling; all children share the host user and network | Unassigned | No formal acceptance evidence |
| T-12 | Secrets or personal/confidential content leak through logs, telemetry, browser diagnostics, or exported support data | Exceptions, URLs, model/tool text, OTLP, rolling logs, IndexedDB, diagnostic zip | Access-token query redaction; production exception detail suppression; sensitive AI telemetry content defaults off; known-key redaction; no automatic diagnostic upload; bounded local retention | Redaction, exception-handler, telemetry configuration, and snapshot-store tests | Implemented; test-supported for selected patterns | Arbitrary exception/tool/model text can carry unknown sensitive content; exported zip is unsigned/unencrypted; no centralized access/custody evidence | Unassigned | No formal acceptance evidence |
| T-13 | Shutdown, crash, or restart loses in-flight work or leaves misleading “running” records | Active invocation, streaming response, scheduler run, outbox | Bounded graceful drain; stop-new-work step; outbox flush attempt; disconnect; restart terminalization/backfill of selected records; child-process cleanup | Shutdown drain, restart recovery, scheduler store, and process supervisor tests | Implemented; test-supported | Drain is best-effort and bounded; interrupted output/in-memory state is not resumed; reconciliation can lack duration/token/model details | Unassigned | No formal acceptance evidence |
| T-14 | Database corruption, migration failure, ransomware, host loss, or key loss prevents recovery | Node SQLite, identity store, key ring, credentials, blobs, models, configuration | Best-effort pre-migration `VACUUM INTO` snapshot retaining three by default; migration locking; fail-closed key behavior | `NodeDbBackupServiceTests`; Chapter 04 traceability | Implemented migration safety net; test-supported; restore operation not evidenced | Snapshot is local and migration-triggered; backup can fail without blocking migration; no coordinated backup, restore automation, restore drill, RTO, or RPO | Unassigned | No formal acceptance evidence |
| T-15 | An attacker uses a same-host user/session compromise to read application data or invoke local APIs | Browser profile, node files, process memory, loopback socket | Owner-oriented file permissions, encryption of selected assets, authentication, short-lived access token, environment scrubbing for sandbox/tool/Development command children | Multiple repository tests noted above | Implemented in part; test-supported in part | The host user is a high-trust principal. Model-server launchers inherit the host process environment. The application does not claim protection against malware or arbitrary code already running as that user | Unassigned | No formal acceptance evidence |
| T-16 | Security controls regress because release/branch validation does not run or evidence is not retained | Disabled/dormant CI, manual packaging workstation, local developer workflow | Manual release script gates; release-script lint; test suites; integrity manifest | Chapter 05; packaging-script tests where invoked | Implemented manual controls; active enforcement not evidenced | GitHub Actions are dormant at the baseline; one operator/workstation can become the effective gate; no retained transcript supplied | Unassigned | No formal acceptance evidence |

## Control clusters

### Local-access and identity controls

The strongest layered path is local API access:

1. supported loopback binding;
2. post-start bind verification;
3. request-time peer, `Host`, and `Origin` checks;
4. rate limiting for anonymous auth actions;
5. password verification and lockout;
6. short-lived signed access token; and
7. endpoint role policy.

This design narrows remote exposure but does not convert the application into a safe shared or internet-facing service.

### Cryptographic and storage controls

The design separates three derived keys and uses authenticated encryption for selected fields, blobs, and non-Windows key-ring elements. Credential stores add Data Protection purpose separation and owner-oriented permissions.

Residual risk concentrates in the common host-user boundary and recovery dependency: access to the live user/process context can expose keys and plaintext, while loss of the root secret or key ring can make ciphertext unavailable.

### Tool, process, and Development controls

The process provider’s path guards, no-follow operations, environment scrub, timeouts, output limits, and tree-kill are meaningful controls against accidental leakage and application-mediated escape. Development selected-folder binding, detached worktrees, exact-base validation, protected paths, and reviewed apply further constrain the application workflow.

They do not provide containment. The same repository build/test process can execute arbitrary host-user code with unrestricted host networking. This residual is categorically different from an application path-traversal risk and must not be described as “sandboxed” without the qualification that no OS isolation exists.

## Priority residual-risk observations

Priority below is a technical review ranking, not an approved business decision.

| Review priority | Residual risk | Why it matters | Required decision/evidence not present |
|---|---|---|---|
| High | Development/repository code executes as host user without network or OS isolation | A malicious repository, build target, test, generator, or dependency hook can bypass application path controls and reach other host-user resources | Whether the feature is acceptable, who may enable/use it, permitted repository trust level, and whether an OS-isolated provider is required |
| High | The tracked Aspire development operator-secret default is shared and recoverable from source | Data created under the unchanged development default is not confidential from anyone who possesses the source; Aspire's sensitive flag is a presentation attribute, not secret custody | Remove the tracked default or enforce a confidential per-developer override; define development-data handling and rotation expectations |
| High | Operator-secret/key-ring loss can make protected data and credentials unrecoverable | Encryption confidentiality and recoverability depend on coordinated custody of root secret, key ring, database, and blobs | Rotation, escrow/custody, backup set, recovery runbook, and successful restore evidence |
| High | Implemented pre-migration snapshot is not disaster recovery | Host loss, ransomware, or failed snapshot can remove both live state and local copies | Backup scope, off-device protection, monitoring, RTO/RPO, restore ownership, and exercise evidence |
| Medium | Supported loopback posture can be weakened by deliberate routable/proxy deployment | The peer/origin assumptions no longer hold behind a same-host proxy or shared network exposure | Deployment guardrail/installer enforcement and evidence that supported packages remain loopback-only |
| Medium | External provider/MCP/WorkerHub paths can transmit sensitive content to separate trust domains | Application controls do not govern provider retention, compromise, jurisdiction, or tool-side effects | Deployment-specific data-flow inventory, provider assessment, egress policy, and operating logs |
| Medium | Plain SQLite and searchable content expose metadata/chunks despite selective encryption | Offline theft can reveal structure and selected plaintext even without the AES-GCM key | Data-classification decision, accepted plaintext set, host/storage hardening, and formal treatment |
| Medium | Unsigned release/model/runtime provenance | Hashes detect accidental change only when obtained through a trusted channel; they do not establish publisher authenticity | Artifact signing, SBOM, signed provenance, model/runtime verification, and retained release evidence |
| Medium | Local logs and browser exports can contain sensitive context | Pattern redaction cannot prove arbitrary text is safe; local exports lack custody controls | Collection/retention/access procedure, secure transfer, evidence preservation, and deletion process |

## Assurance and acceptance status

No penetration-test report, deployed control sample, incident/restore exercise, external-provider assessment, formal control owner, remediation commitment, or risk-acceptance record was supplied for the scenarios above.

The repository provides meaningful implementation and test evidence. That evidence supports an architecture review; it does not support a conclusion that controls operated continuously or that residual risks were formally accepted. Claim-by-claim availability is recorded in [Chapter 06](06-claim-traceability-and-evidence-availability.md).
