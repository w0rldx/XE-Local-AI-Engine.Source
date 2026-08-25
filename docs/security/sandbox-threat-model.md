# Sandbox threat model

> **Status:** Draft, 2026-08-25. Written as Phase 0 of the secure-agent-execution work
> (`Plans/secure-agent-execution-2026-08-25/`). It describes what is enforced **today** and names every gap as
> `GAP → Gx` against that plan's gap list. It is not a certification, an audit, or a risk acceptance.

Companion records: [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md) (Docker scope),
[ADR 0007](../adr/0007-sandbox-execution-substrate-and-backend-selection.md) (substrate selection, Proposed),
[ADR 0006](../adr/0006-agentic-trust-mcp-key-scopes-and-auto-approval.md) (inbound MCP authority),
[wiki 12 — Security & Privacy](../wiki/12-security-and-privacy.md) (the shipped posture in prose),
[wiki 19 — Compute Tools](../wiki/19-compute-tools.md) (`run_python`).
Symbols are cited by name, never by line number, per `docs/agent-knowledge.md` §0.

## 1. Assets

| # | Asset | Why an attacker wants it |
|---|---|---|
| A1 | The operator's real repositories on disk | Source theft; a planted commit that reaches a remote |
| A2 | Node secrets — operator secret and derived keys, `XE_NODE_SQLITE_KEY`, cloud API keys, OAuth tokens, HF token, MCP server env, inbound MCP keys | Direct credential compromise; cloud spend; lateral movement |
| A3 | The node SQLite database and its encrypted blob stores (`ManagedEncryptedBlobStore` and its four users) | Conversations, knowledge documents, uploads, development artifacts |
| A4 | The host user account the engine runs as | Everything the operator can reach, including SSH keys and other repositories |
| A5 | The Docker daemon socket, where a daemon is configured | On Linux, root-equivalent (ADR 0004 Consequences) |
| A6 | The engine's own control state — `workspace.json`, sandbox markers, attestation store, command profiles | Defeating the controls below rather than attacking through them |
| A7 | The operator's network position — LAN, loopback services, cloud metadata endpoints | Pivot from a machine that is trusted by other machines |
| A8 | GPU and CPU capacity | Denial of service; unmetered compute |

## 2. Attackers and untrusted inputs

Every entry below is untrusted **input**, whether or not anyone is deliberately hostile.

| # | Source | Capability it has today |
|---|---|---|
| T1 | **The model** (local or cloud) | Chooses tool calls and their arguments; authors patches and file contents; authors Python for `run_python`. Cannot choose a command line in Development Mode — the catalog is fixed (`DevelopmentCommandIds`) |
| T2 | **The registered repository** | Ships `Directory.Build.props`/`.targets`, `UsingTask`/`Exec`, source generators, `build.rs`, `postinstall`, tests. All of it executes during validation, and the agent can write it |
| T3 | **Packages resolved at restore** | Arbitrary code at restore, build and test time. **G1 narrowed the window rather than removing it:** the resolve now happens in a short-lived warm sandbox against the BASE COMMIT's manifests before the agent has written anything, and the agent-facing sandbox is denied egress wherever the backend can serve denial. A malicious package still executes — at warm time, on the operator's own committed dependency set. Where the backend cannot deny egress (Windows; any host whose `unshare` probe failed) the pre-G1 posture stands, visibly, on the Development status surface |
| T4 | **Outbound MCP servers** | stdio servers are operator-configured third-party executables, and where they run is now a declared **trust tier** on the registration (`McpTrustTier`, `docs/security/mcp-trust-tiers.md`). `Sandboxed` — the default, and what every existing row migrated to — launches the server inside the substrate under `SandboxWorkloads.McpStdio` (no host filesystem, no network, a disposable jail), fail-closed on a host that cannot serve that. `PrivilegedHost` is the old host launch, kept as an explicit per-server operator grant and offered as `ToolCategory.WriteExecute`. HTTP servers are loopback-only by design (`McpOptions.HttpLoopbackHosts`, exact match) |
| T5 | **MCP tool results and other tool output** | Text that re-enters the model's context and can carry instructions (prompt injection) |
| T6 | **Uploaded files and chat attachments** | Staged into the jail by `IConversationSandboxStager`; content is plaintext while staged; no secret scan before staging |
| T7 | **Sandbox stdout/stderr** | Re-enters the attempt context and, on a cloud-routed attempt, leaves the machine. A test that prints `.env` exfiltrates it without any network access |
| T8 | **Inbound MCP callers** | Bounded operator-equivalent execution on the exposed surface only (ADR 0006), loopback-reachable, key-scoped |
| T9 | **External services** | HuggingFace, GitHub, model registries, custom-tool targets. Three independent SSRF guards, not unified (**GAP → G7**) |
| T10 | **A previous run of this engine** | Orphaned processes, scopes, containers and jails on disk; markers are untrusted after a crash |

## 3. Trust boundaries

```
                        ┌──────────────────────────────────────────────┐
   operator (browser) ──┤ B0  loopback + Host/Origin + auth            │
                        │     LocalApiSecurityMiddleware, LoopbackBindGuard
                        └───────────────────┬──────────────────────────┘
                                            │
   inbound MCP (loopback, ADR 0006) ────────┤
                                            v
   ╔════════════════════════════════════════════════════════════════════════╗
   ║  ENGINE PROCESS — host user, holds A2/A3/A6. Trusted.                   ║
   ║                                                                        ║
   ║   B1 approval / policy      ClientLocalToolRegistry, IToolApprovalPolicy║
   ║   B2 substrate selection    SandboxProviderSelector  (→ ADR 0007)       ║
   ║   B3 apply gate             DevelopmentApplyService.ApplyRevalidatedAsync
   ╚═══════╤════════════════╤═══════════════╤════════════════╤══════════════╝
           │                │               │                │
   ┌───────v──────┐ ┌───────v───────┐ ┌─────v───────┐ ┌──────v───────────┐
   │ B4 process   │ │ B5 bwrap      │ │ B6 Docker   │ │ B7 NONE          │
   │ jail         │ │ mount-ns      │ │ container   │ │ host process     │
   │ AgentHome,   │ │ run_python,   │ │ Dev Mode    │ │ stdio MCP at the │
   │ Coder, Dev   │ │ stdio MCP     │ │ net: DENIED │ │ PrivilegedHost   │
   │ Mode default │ │ (Sandboxed)   │ │ (the warm   │ │ tier — an        │
   │ net: denied  │ │ net: DENIED   │ │  restore is │ │ explicit grant   │
   │ where probed │ │               │ │  a second)  │ │                  │
   └──────────────┘ └───────────────┘ └─────────────┘ │ training (uv,    │
                                                      │ ADR 0005, by     │
                                                      │ decision)        │
                                                      └──────────────────┘
           │                │               │
           └────────────────┴───────────────┴──► A1 real repository is reached
                                                 ONLY through B3. Sandboxes see
                                                 an engine-owned standalone clone.
```

Three properties of this picture are worth stating plainly. **B7 no longer holds anything the operator did not put
there deliberately** — after G2 a stdio MCP server defaults to B5, and reaching B7 takes a per-server `PrivilegedHost`
grant on the registration. What remains true is that B7 is still B7: an explicit grant is a decision, not a boundary,
and on Windows (or any host without bubblewrap) the `Sandboxed` tier is unavailable and its servers refuse to start
rather than falling into B7 quietly. **B4's strength is measured per host, not
assumed** (`SandboxContainment`); off Linux it is `SandboxContainment.None` and B4 collapses into B7 in everything but
supervision. **B6 is the strongest boundary on the toolchain axis and, after G1, no longer the weakest on the egress axis** —
Development Mode's agent-facing sandbox now requests denial on both B4 and B6. The remaining egress hole is not a
boundary but a *host*: where the process backend cannot confine networking, its capability-gated request falls back to
`Unrestricted` and B4 carries an open network for Development Mode. That is reported, not assumed — see invariant 4.

## 4. Invariants, restated as testable properties

The fourteen invariants of the 2026-08-25 proposal (§41), each rewritten as a property a test can assert, with the
symbol or test that enforces it today.

| # | Testable property | Enforced by |
|---|---|---|
| 1 | No agent-authored command line or agent-authored code executes as a plain host child. Development Mode executes only ids from `DevelopmentCommandIds`, resolved through `DevelopmentCommandProfile.ResolveCommand`, which throws for anything else. A stdio MCP server's command line is OPERATOR-authored, not agent-authored, and after G2 it runs inside the substrate by default — the model reaches its tools, never its argv | `DevelopmentCommandProfileCatalog`, `DevelopmentProfileGuardTests`, `SandboxedMcpStdioTransportTests`. **Partial:** an operator may still grant `PrivilegedHost` per server, which is the point of the tier rather than a gap |
| 2 | A sandbox write cannot reach the registered repository path. The workspace is an engine-owned standalone clone (`StandaloneGitClone.IsStandalone`) on a detached HEAD; a symbolic ref makes `DevelopmentWorkspaceProvider` throw `DevelopmentWorkspaceSecurityException` | `DevelopmentWorkspaceProvider.PrepareAsync`, `DevelopmentWorkspaceAndCoderTests` |
| 3 | The child's environment contains no inherited engine secret. `ProcessSandboxRuntimeProvider.ExecuteAsync` clears and repopulates from `InheritableEnvironmentAllowlist`; the isolated chain uses `--clearenv`; a `PrivilegedHost` stdio MCP server sets `InheritEnvironmentVariables = false`, and a `Sandboxed` one goes through the isolated chain's `--clearenv` like any other sandboxed command. The stored environment is AEAD-encrypted at rest (`EnvJson`, AAD `env`) and is MASKED on the way out of the node (`McpEnvironmentMask`) | `ProcessSandboxRuntimeProviderTests`, `SandboxContractGuardTests`, the `BuildStdioTransportOptions` hardening test, `McpServerEndpointTests.ListServers_MasksEveryEnvironmentValue_AndCarriesTheTrustTier` |
| 4 | A sandbox with no declared network need has no route off the box. True for AgentHome/Coder where the probe succeeded, for `run_python` (bwrap `--unshare-net`, refused fail-closed otherwise), and **now for Development Mode's agent-facing sandbox** — G1 requests `None` wherever the backend advertises `SupportsNetworkPolicy`, made workable by an engine-run warm restore against the base commit and by `dependency_manifest_changed` failing any attempt that would need a new resolve | `AgentHomeService.ResolveNetworkPolicy`, `DevelopmentWorkspaceProvider.ResolveAgentFacingNetworkPolicy`, `ComputeToolGateway`, `SandboxIsolationLiveTests`, `DevelopmentSandboxEgressTests` (live: a cold restore inside a denied sandbox fails, the whole profile passes against the warmed cache), `DockerSandboxRealDaemonTests`. **Two carve-outs, both by design:** the WARM sandbox is `Unrestricted` — it is what fetches the packages, runs only the frozen profile's `dotnet_restore`, only from a clean tracked tree at the base commit, and is killed before the attempt starts; and on a host with no network-confinement mechanism the request falls back to `Unrestricted` (Option B, operator ruling 2026-08-25). The **served** posture, not the requested one, is what the Development status surface reports |
| 5 | No sandbox mount resolves to a daemon socket or named pipe, and the created container's mounts read back equal to the requested set | `DockerSandboxHardening.VerifyMounts`, `DockerSandboxRuntimeProvider` mount validation, `DockerSandboxMountBrokerTests` |
| 6 | Every mount is engine-generated. No mount is derivable from repository content (ADR 0004 §5, plan D7). Control state is excluded structurally: named subdirectories of `RuntimePath` are mounted, never the parent that holds `workspace.json` | `DevelopmentWorkspaceProvider.BuildMounts`, `DevelopmentMountBrokerTests` |
| 7 | Output cannot name a host path. Reads and writes pass `ResolveJailPath` + `EnsureNoSymlinkComponentsUnderJail` + `O_NOFOLLOW`; apply runs `git apply --index` against a pinned base through `TrustedDevelopmentHostApplyPort` | `SandboxJailPathGuardTests`, `TrustedDevelopmentHostApplyPortHardeningTests`. Note the two apply implementations (`NodePatchApplyService` and the Development port) are unreconciled |
| 8 | Apply mutates the repository only when the subject hash still equals the reviewed one, and a replayed `operationId` is a no-op | `DevelopmentApplyService.PreviewAsync`, then `IDevelopmentCoordinator.ApplyRevalidatedAsync` — which re-previews and throws `DevelopmentInvalidTransitionException` on mismatch; `DevelopmentValidationReviewAndApplyTests` |
| 9 | No agent tool can change what validation runs. The profile is operator-confirmed at project creation, frozen into the attempt snapshot, and re-derived from the code-owned catalog on load — a stored profile whose canonical bytes no longer match is rejected, not reinterpreted | `DevelopmentCommandProfileCatalog.ResolveStored`, `DevelopmentCommandProfile.ComputeDigest`, `DevelopmentProfileGuardTests`. The reward-hacking sibling — the agent may add tests but not modify or delete one that existed at the base commit — is `DevelopmentTestWritePolicy.Ensure` |
| 10 | An operator-configured MCP server cannot execute outside a sandbox boundary WITHOUT an explicit per-server grant. `McpClientFactory.BuildStdioTransport` routes a `Sandboxed` registration — the default, and what every pre-existing row migrated to — through `SandboxedMcpStdioTransport`, which starts the server under `SandboxWorkloads.McpStdio` and refuses fail-closed where the backend cannot supply the boundary. HTTP is loopback-only (`McpOptions.HttpLoopbackHosts`), which is a reachability control, not a containment one | `SandboxedMcpStdioTransportTests`, `SandboxedMcpStdioLiveTests` (live: tools list succeeds, a host canary is absent, the network namespace is empty), `SandboxSubstrateSelectionArchitectureTests`. **Bounded:** `PrivilegedHost` is an operator decision recorded on the row and reported as `WriteExecute`; unavailable on Windows until G12, where the tier refuses rather than degrading |
| 11 | A privileged host capability is reachable only under an explicit, auditable grant. Approval is composed tighten-only (`catalogDefault \|\| nodePolicy \|\| perAgent`), `ToolCategory.Unknown` fails closed, and agentic auto-approval writes a metadata-only `ApprovalDecision` **before** the inner function runs — audit failure blocks the tool | `NodeToolApprovalPolicy`, `SessionApprovalEligibility`, ADR 0006 §6. **Partial:** there is no named PrivilegedHost tier (**GAP → G11**) |
| 12 | Nothing survives a hard kill of the engine. Process jails, systemd scopes and their markers are swept at startup behind three gates (owner liveness, pid-reuse start-time match, strict path ownership under `SandboxPaths.ContainerRoot`), with a 30-second grace for unreferenced scopes | `SandboxOrphanReaper`, `SandboxOrphanReaperTests`. **GAP → G3:** no Docker sweep, although `DockerSandboxHardening.OwnerLabel` / `SandboxIdLabel` are already set on every container |
| 13 | A cache shared between sandboxes cannot be poisoned by one of them. `NUGET_PACKAGES` and `DOTNET_CLI_HOME` are **per-task** roots under `RuntimePath` (`DevelopmentWorkspaceTools.BuildEnvironment`), so no cache is shared BETWEEN tasks. G1's warm restore made it shared between the two sandboxes of ONE task, in one direction only: the warm sandbox writes it from the base commit and the agent-facing sandbox reads and may write it. An attempt can therefore poison its own task's cache, which changes nothing — it already controls the code that would consume it. It becomes a live property the moment a cache is shared across tasks or G7's package cache exists, and must be re-asserted then | `DevelopmentWorkspaceTools`, `DevelopmentWarmRestoreTests` |
| 14 | Every artifact crossing a boundary is content-addressed and attributable. `DevelopmentArtifact` carries `BaseCommit`, `SubjectHash`, `ManifestHash`, `ContentHash`, `IsValid` and an input-artifact DAG; blobs are SHA-256 addressed with encrypt→verify→atomic-move | `DevelopmentEvidenceService`, `ManagedEncryptedBlobStore`. **Partial: GAP → G9** — no unified artifact entity (`MediaType`, `Origin`, `ParentArtifactId`) across the four blob-store users |

Two invariants of this repository's own that the proposal does not list, and that must not be traded away:

- **Audit is metadata-only by design.** `AgentExecutionLog` stores no content
  (`AgentExecutionLog_StoresMetadataOnly_NoContent`); ADR 0006 §7 enumerates what a structured invocation log may
  contain. Changing this requires the policy in `agent-trajectory-data-policy.md`, not a code change.
- **The reviewer holds no write authority.** `DevelopmentReviewerAttemptRunner` offers no write, patch, command or
  apply tool (`ReviewerModel_OffersNoWritePatchCommandOrApplyCapability`).

## 5. Abuse cases

| # | Abuse case | Status today |
|---|---|---|
| AB1 | A malicious package's restore/build hook reads the cloned repository and POSTs it out | **Narrowed, not closed (G1).** Restore now runs in a short-lived warm sandbox against the base commit's own manifests, before the agent has written anything, and the agent-facing sandbox is denied egress wherever the backend can serve denial — so build and test hooks have no route out. A hook in a package the operator's OWN committed manifests resolve still executes with egress at warm time. **On a host with no network-confinement mechanism the pre-G1 posture stands in full**, and the Development status surface is what says so |
| AB2 | A test prints `.env` so its bytes land in captured stdout and ride the attempt context to a cloud model | **Reduced by G5 where the backend has a mount layer, live otherwise.** A committed credential is detected on every prepare and recorded as an operator-visible event; where `SupportsReadOnlyMounts` is advertised it is additionally shadowed by an empty read-only mount, so the test reads nothing. On the process backend — today's default — detection is the whole control and this remains live. Read guards gate the engine's tools, not repository execution; `DevelopmentArtifactSanitizer` + `MemoryProposalSecretScanner` reduce persistence, not the model's exposure |
| AB3 | An operator installs an MCP server from a README; it reads `~/.ssh` and the node database on first tool call | **Closed at the default tier (G2).** A `Sandboxed` server runs with the host filesystem absent from its mount namespace, and the two read-only trees it IS given cannot cover a sensitive host root: home, `~/.ssh` / `~/.gnupg` / `~/.aws` / `~/.azure` / `~/.config` / `~/.docker` / `~/.kube`, `INodeDataDirectory.Root`, `AppContext.BaseDirectory`, `/root`, `/etc`, `/var`, `/` are refused equals-or-ancestor, on resolved paths, at one gate both the command directory and the working directory pass through (`SandboxedMcpStdioTransport.AddBindableTree`; `docs/security/mcp-trust-tiers.md`). Live-evidenced: the fixture server reports `CANARY=ABSENT ENV=OK INSIDE=PRESENT CWD=/work NET=DENIED` — it sees the one subtree it was given and nothing above it — and a registration whose working directory is `$HOME` is refused before any process starts. Two residues, both visible rather than silent: an operator who grants `PrivilegedHost` re-opens it deliberately for that server, and on a host without bubblewrap the tier is unavailable and the server refuses to start |
| AB4 | Tool output carries instructions the model follows ("now call `write_file` on …") | **Partially mitigated.** Approval is tighten-only and `WriteExecute` forces approval; MCP results are not fenced as untrusted content |
| AB5 | The agent deletes the failing test instead of fixing the bug | **Closed.** `DevelopmentTestWritePolicy` — additions allowed, modification/deletion/rename of a base-commit test refused |
| AB6 | The agent edits the command profile or `Directory.Build.props` to make validation vacuous | **Profile: closed** (frozen + digest re-derivation, plus `DevelopmentWorkspaceTools.EnsureCommandProfileImportUnchanged`, which re-derives `DevelopmentCommandProfileImport.TryComputeDigest` against a baseline taken before any command ran). **Build config: partially** — `EnsureBuildConfigurationBarrier` and the `no tests executed` verdict close the two known shapes |
| AB7 | A repository ships `.gitattributes` selecting a filter driver defined in `.git/config` | **Closed** where the backend advertises read-only mounts: `.git/config` is mounted read-only over a writable worktree |
| AB8 | A planted symlink redirects a read or a bind after path resolution | **Closed.** `EnsureNoSymlinkComponentsUnderJail`, `O_NOFOLLOW` + `ELOOP`, and `openat2(RESOLVE_BENEATH\|RESOLVE_NO_SYMLINKS)` descriptors for every bwrap bind — with no pathname fallback |
| AB9 | A sandboxed child escapes its cgroup by reaching the per-user systemd bus over a UNIX socket a netns does not confine | **Closed, and it was live.** `XDG_RUNTIME_DIR` and friends reach the launch wrapper only and are stripped by `env -u` before exec |
| AB10 | A crash leaves containers running; the next start does not know about them | **Live (G3)** — resource leak, not escape |
| AB11 | A daemon is swapped for a hostile one between runs | **Detected.** `DockerDaemonAttestationStore` TOFU; `DockerDaemonPreflightService` reports `DaemonIdentityChanged` |
| AB12 | An unpinned or mutable image tag is substituted upstream | **Closed.** `ContainerSandboxOptionsValidator` requires `@sha256:`. A digest pins bytes, not hermeticity |
| AB13 | A `run_python` script chmods `site-packages` to persist across calls | **Closed.** The venv and the CPython root are bound read-only; `os.chmod` answers `EROFS` |
| AB14 | A runaway sandbox fills the disk | **Bounded, not quotaed.** tmpfs `--size` in Docker; a 2-second visible-file watchdog in the process backend, which an unlink-then-write loop bypasses. A real quota is an open follow-up |
| AB15 | A syscall-level kernel exploit from inside any sandbox | **Out of scope for every backend here.** No seccomp pin (G4), no LSM profile, no user-namespace-free design |

## 6. Container-escape strength, per platform

Stated as what the boundary *is*, not as a rating.

- **Linux, rootful Docker.** Shared kernel. `--cap-drop ALL`, `no-new-privileges`, read-only rootfs, no host namespaces,
  engine-only mounts, and an identity that is non-root and maps to the engine's own uid — all read back after create and
  fail-closed on any mismatch (`DockerSandboxHardening.FindViolations`). seccomp is the **daemon's implicit default**:
  neither requested nor verified (**GAP → G4**). Access to the daemon socket remains root-equivalent, and the product
  documents rather than mitigates this. A kernel LPE defeats the boundary.
- **Linux, rootless Docker.** Same controls; container uid 0 maps to the invoking user's unprivileged host account, so it
  is strictly less privileged than the engine process. This is verified by **outcome** — a probe file written from inside
  and `lstat`-ed host-side — because `inspect` can only echo the uid that was asked for, never what it maps to
  (2026-07-28 plan §3.8.1). Escape lands as the engine's own user, which already holds A2/A3/A6.
- **Windows, Docker Desktop.** The daemon runs in a Linux VM; escape from the container lands inside that VM, and the
  hypervisor is a second boundary the Linux cases do not have. That is not a claim of *more* security here: it is
  untested by this repository, and the engine is a native Windows process talking to it over a named pipe
  (`DockerDaemonEndpointResolver`). Treat it as unmeasured.
- **Linux, bwrap (`SandboxIsolationMode.Filesystem`).** A namespace boundary, not a kernel-hardened one. PID/IPC/UTS/net
  unshared, `--disable-userns --assert-userns-disabled`, synthetic `/etc` from sealed `memfd`s, descriptor-based binds.
  Advertised only after `HostSandboxContainmentProbe` exercises the production chain and passes fifteen positive
  controls. No seccomp, no LSM.
- **Linux, process jail without bwrap.** Supervised execution: fixed executables, working-directory jail, scrubbed
  environment, timeouts, output caps, plus whatever of `setsid` / `systemd-run --user` / `unshare` the host actually
  delivers. Each is probed by performing it. Not an OS isolation boundary.
- **Windows, process jail.** `SandboxContainment.None` — `HostSandboxContainmentProbe` reports "the Windows Job Object
  path is not implemented". Commands run as the signed-in user with full host network and no ceiling; the supervision
  guarantees still apply, nothing underneath them does (**GAP → G12**). A container-configured node is the only path
  that changes this.

## 7. Residual risk, stated once

The engine process is trusted and holds every node secret. Every boundary above is *below* it. Nothing here protects the
operator from a compromise of the engine process itself, from a kernel exploit, or from an operator who approves a
hostile tool. Approval gating is the interim control wherever a containment mechanism is inactive, and an approval is a
human decision this document cannot make safe.
