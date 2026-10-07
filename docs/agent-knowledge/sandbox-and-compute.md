# Sandbox, compute and AgentHome containment

Scope: the process sandbox and its bubblewrap-isolated mode, path containment, the compute gateway and AgentHome
runs and patches. Read when: touching `Services/Sandbox`, `Services/Compute`, `Services/AgentHome`, bubblewrap or any
containment check (jail paths, symlink guards, path containment, patch apply).

## Sandbox and containment

### Sandbox: the two guards are mandatory *together*

**Rule:** every sandbox read/write/list/search calls `ResolveJailPath` (lexical prefix containment) and, immediately before open, `EnsureNoSymlinkComponentsUnderJail`. Open the leaf with raw `open(O_RDONLY|O_NOFOLLOW|O_CLOEXEC)` (errno 40/ELOOP = symlink refusal); never cast `O_NOFOLLOW` into `FileOptions`. Size, read exactly that many bytes, then probe one more: growth rejects the copy. Survey operations are `ISandboxRuntimeProvider` operations, never routed through `ExecuteAsync`. **Prevents:** a planted symlink escaping prefix-only containment, torn copies of a growing file, and weaker confinement rebuilt in command arguments. **Authority:** `ResolveJailPath`, `EnsureNoSymlinkComponentsUnderJail`, `SandboxFileSurveyOperations`. [evidence](../agent-knowledge-evidence.md#sandbox-no-follow-failures)

### Dev Mode's file guard has TWO predicates on purpose — do not merge them

**Rule:** `IsSecret(name)` gates reads; `IsExcluded(name,isDir)` gates copies and adds build/cache directories. `.npmrc`, `.env.*` (including `.env.example`) and certificate/key patterns count as credentials, so private-registry restores or cert fixtures may be missing in copied homes; do not loosen that as a convenience fix. Execution can still print a secret to captured stdout: that channel is explicitly unclosed. Rotate dev databases written under the removed tracked key. **Prevents:** copy exclusions hiding diagnostics (`obj/project.assets.json`) from reads. **Authority:** `IsSecret` / `IsExcluded`.

### A lexical path-containment check is one shared helper, and the root is not its own descendant

**Rule:** containment goes through `PathContainment.IsUnderRoot`, never a private copy: it normalises both sides, compares against `root + separator`, requires the candidate to be strictly longer, and fails closed on a malformed path. `SandboxJailPathGuard.IsUnderJailRoot` is deliberately different (admits the jail root; called per component in the symlink walk). **Prevents:** `Path.GetFullPath` preserving a trailing separator so a root reads as its own descendant in front of a recursive delete or tree kill, and copies drifting in which exceptions they catch. **Authority:** `Providers.Abstractions/PathContainment.cs`, `PathContainmentTests`. [evidence](../agent-knowledge-evidence.md#a-lexical-path-containment-check-is-one-shared-helper-and-the-root-is-not-its-own-descendant)

### The bubblewrap-isolated sandbox mode: seven traps, all paid for once

**Rule:**
- Reject read-only bind sources under chain-owned `/tmp` or `/work` (later mounts shadow them silently) and special `/usr`, `/dev`, `/proc` conflicts; do not reorder.
- `/proc` may answer ENOENT; use `/dev` EROFS as the read-only control.
- Bind descriptors stay non-CLOEXEC through setsid/systemd-run/bwrap.
- Symlink mode bits are meaningless; follow to the canonical target before trust checks.
- Ownership fixtures never live under world-writable `/tmp` (a different guard trips first).
- chmod jail/home/tmp to 0700 explicitly.
- Live tests skip only when trusted bwrap is absent; a present bwrap that cannot isolate is a failure.

**Authority:** `SandboxIsolatedChain`, `HostSandboxContainmentProbe`. [evidence](../agent-knowledge-evidence.md#bubblewrap-live-controls)

### Wiring a caller onto the isolated mode: five things the generic layer will not tell you

- Bind the venv **and** uv's `python/pythons` root read-only (venv Python points through a version alias); never the compute root or the wider toolchain store.
- Execute the venv symlink, not its realpath, or `sys.prefix` loses site-packages.
- Descriptor `RESOLVE_NO_SYMLINKS` checks every bind-source component; `Path.GetFullPath` is not canonicalization. Report the offending component.
- Env paths are sandbox paths (`/work`, `/work/home`, `/tmp`); caller env is emitted last and overrides chain defaults, so never pass host jail paths or thread variables (those come only from `SandboxCreateRequest.ThreadLimit`).
- Isolation uses bwrap's own `--unshare-net`; never gate it on the non-isolated `SupportsNetworkPolicy` probe.
- Synthetic `/etc` is a rewound, non-CLOEXEC sealed memfd. Kill authority is the transient scope cgroup, not PID/PGID trees.

### Sandboxed MCP = self-contained servers; the registration `PATH` replaces the jail `PATH`

**Rule:** a `Sandboxed` stdio MCP server sees only its command's directory, its working directory and `/usr`, read-only: a symlinked command's target and a script's interpreter are not bound, so npm/npx/uv/uvx/mise/nvm/venv installs need `PrivilegedHost`. The jail `PATH` is `/usr/bin:/bin`; a registration `PATH` variable REPLACES it (the chain applies registration env last), it never extends it, and this node's `PATH` never reaches the jail. Only a server that dies before its first message gets a stderr tail. **Prevents:** debugging a sandboxed server by fixing the host `PATH`, or adding one directory to `PATH` and losing `/usr/bin`. **Authority:** `SandboxIsolatedChain.SandboxPath`, `SandboxedMcpStdioTransport.JailSearchPath`, `SandboxedMcpStdioTransportTests`; `docs/security/mcp-trust-tiers.md`.

## Compute and AgentHome

### `ExecuteDetailedAsync` is the single compute execution boundary

**Rule:** `IComputeToolGateway.ExecuteDetailedAsync(request, requireResourceLimits, ct)` is the only execution path reading `Compute:Enabled`, then runs `ComputeRunToolRequestValidator`, the filesystem-isolation refusal (before provisioning), the optional resource-ceiling refusal and the jail-root refusal. `ExecuteAsync` = same path with `requireResourceLimits: false` + the model-facing formatter. `run_python` (human-approved) passes `false`; the benchmark verifier (unattended) passes `true`. Never move the switch or validation into a caller. **Prevents:** a second caller running model code unbounded on a node that never opted in. **Authority:** `ExecuteDetailedAsync_IsTheOnlyExecutionPathThatReadsComputeEnabled`, `RunPython_WithoutResourceLimits_StillRuns_BehaviourUnchanged`. [evidence](../agent-knowledge-evidence.md#executedetailedasync-is-the-single-compute-execution-boundary)

### The AgentHome patch export STAGES before it diffs, and the apply side refuses a link entry by name

**Rule:** `AgentHomePatchService.ExportPatchAsync` runs `add -A` (never `--force`; `.gitignore` still decides) then diffs `--cached … HEAD`. `NodePatchApplyService` parsing refuses a symlink (`120000`) or gitlink (`160000`) entry by name in both the mode-line and index-line arm. A test that asserts a brand-new path in the export fails against a working-tree diff; modify a copied file to exercise the old shape. **Prevents:** an export that reports "no file changes" for runs that created files, and apply planting a model-named host link. **Authority:** `AgentHomePatchServiceTests.ExportPatchAsync_StagesTheWorkspaceBeforeItDiffs`; `NodePatchApplyService.Parsing` (`SymlinkMode`/`GitlinkMode`), `NodePatchApplyServiceTests`.

### The `run_in_agent_home` result's first line is node-authored, and it is the ONLY place a run id may be read from

**Rule:** `AgentHomeToolGateway.BuildHeader` writes `[agent-home run=… outcome=… patch=…]` as line one, before any model-influenced byte; every reader parses it start-anchored, no multiline flag, no body fallback (no header = no run). The outcome token is spelled per status, never `ToString()`'d, so a new status throws. **Prevents:** the model's echoed `run_command` text, which sits above the real patch path, steering "review and apply" at another run's patch. **Authority:** `AgentHomeToolGateway.BuildHeader`/`OutcomeToken`, SPA `agentHomeRunIdWithPatch` in `AgentHomePatchToolResult.ts`; `AgentHomeToolResultContainmentTests`, `AgentHomePatchToolResult.test.ts`.

## Stale beliefs

Superseded claims; the entries above are the active rules.

| Stale belief | Current correction |
|---|---|
| TOCTOU guards live in a Docker provider. | They live in `ProcessSandboxRuntimeProvider` and apply to provider operations (§4). |
| Modern `git apply` rejects `--binary` patches. | It applies them; never depend on rejection for security. |
| `run_python` can see the host filesystem. | It requires a filesystem boundary and refuses otherwise: bubblewrap on Linux, the MXC AppContainer boundary (Preview, operator-enabled) on Windows (ADR 0019); other sandboxes are unchanged (§4). |
| MXC is TypeScript-only, has no NuGet package, is "not a security boundary", and needs build 26100. | MXC 1.0.0 ships `Microsoft.Mxc.Sdk` on NuGet, the README caveat is gone, and the floor is whatever `GetPlatformSupport` + `Probe` report on the host; it is a Preview launch mechanism here (ADR 0019). |
| Compute venv chmod can undo read-only mode. | Inside the namespace the read-only bind makes chmod/write fail. |
| Compute egress is gated by `SupportsNetworkPolicy`. | Filesystem isolation/bwrap owns the network namespace; use that boundary (§4). |
