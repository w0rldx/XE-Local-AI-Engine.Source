# ADR 0019: Preview sandbox mechanisms are operator-enabled, and on Windows the AppContainer boundary satisfies the filesystem floor

- **Status:** Accepted (2026-10-07) — drafted with the S2b slice, completed with the MXC integration slice (S3). Windows live
  evidence on the operator's box is still owed before the branch merges (see "Still owed").
- **Date:** 2026-10-07
- **Scope:** How a sandbox launch MECHANISM's maturity is recorded, who may enable a Preview mechanism, and what the
  `SandboxIsolationMode.Filesystem` floor means on Windows when the Microsoft MXC SDK (`Microsoft.Mxc.Sdk` 1.0.0) serves it
  through an AppContainer boundary inside the `process` provider. It changes nothing on Linux: the bubblewrap chain keeps
  serving the floor there, at `Stable` maturity, with the same paths and the same refusals.
- **Authority:** Operator decisions of 2026-10-07 (Q1 stored node setting with a confirmation dialog, Q3 timeout-only
  execution accepted, Q5 AppContainer boundary satisfies the floor, D4 Preview never reported as `Isolated`), recorded in
  `Plans/execution-runtime-2026-09-26/KICKOFF-r3-2026-10-07.md` (the plan directory is git-ignored; it is the record of
  the decisions, not a published document).
- **Amends:** [ADR 0007](0007-sandbox-execution-substrate-and-backend-selection.md) only in what the
  `SupportsHostFilesystemBoundary` property may be made of on Windows. The selector, the backend ranking and the
  per-workload allowed-backend table are unchanged.

## Context

`run_python` and Sandboxed stdio MCP servers declare a `Filesystem` isolation floor. On Linux the `process` provider
serves it with a bubblewrap mount namespace: the host filesystem is absent, the child sees `/work`, `/work/home` and
`/tmp`, and callers composed `HOME`/`TMPDIR` from those fixed POSIX constants. On Windows nothing served it, so both
workloads were refused there.

MXC can run a child in an AppContainer with a DACL-granted jail, a denied user profile, node data directory and engine
install directory, and no network capability. That is a real boundary, but not the same one: there is no namespace, the
child sees host path names, OS system trees stay readable, and the ProcessContainer has no CPU, memory or process-count
ceiling. The SDK is new, so the mechanism must not become the default, and an operator who enables it must know what it
does not do.

## Decision

1. **Maturity belongs to a launch mechanism, not a provider.** `SandboxMechanismMaturity { Stable, Preview }`.
   `setsid`, `systemd-run`, `unshare` and bubblewrap are `Stable`; the MXC AppContainer boundary is `Preview`. The host
   containment probe MEASURES a mechanism whenever the host can run it (a read-only probe) and records its maturity in
   `SandboxContainment.AppContainerBoundary`; measurement never depends on the operator's setting.
2. **Eligibility is a separate, per-call decision.** A `Preview` mechanism may serve a role only while
   `IExecutionPreviewPolicy.PreviewMechanismsEnabled` is true, backed by the stored node setting
   `ExecutionPreviewsEnabled` (effective default off; configuration seed `ExecutionPreviews:Enabled`). It is read on
   every `Capabilities` read and every sandbox create, never cached in the probe, so turning previews off withdraws the
   mechanism for the next sandbox without a restart. One rule, `SandboxContainment.EligibleAppContainerBoundary`, is
   shared by advertisement, enforcement and the capability summary.
3. **`SupportsAppContainerBoundary` is its own capability flag** (`1 << 13`) with this contract: no access to the user
   profile, `INodeDataDirectory.Root`, `AppContext.BaseDirectory` or any path not granted; engine-granted read-only trees
   plus one writable jail; OS system trees readable; host path names visible.
4. **On Windows, while eligible, that boundary satisfies the `Filesystem` floor (Q5).** The `process` provider then
   advertises `SupportsAppContainerBoundary | SupportsFilesystemIsolation | SupportsHostFilesystemBoundary` together, and
   `SandboxLifecycleRegistry` accepts an isolated create, recording the serving boundary on the sandbox's launch policy.
   The bubblewrap reserved-mount-point rule does not apply to it.
5. **Callers compose paths from the sandbox they were given, not from constants.** `SandboxIsolatedPaths` is a record
   carried on `SandboxHandle.IsolatedPaths`: `Posix` under bubblewrap, the jail's own host paths under the AppContainer
   boundary. `ComputeToolGateway` and `SandboxedMcpStdioTransport` set `HOME`, `TMPDIR`, `TMP`, `TEMP` from it (on
   Windows also `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`, pointed at the jail home). An isolated handle without paths
   is a provider bug and is refused, never guessed.
6. **A Preview-served role is never reported as `Isolated`.** The capability summary reports `Level = "PreviewIsolated"`,
   `Maturity = "Preview"` and `Backend = "appcontainer"`; with previews off, a measured boundary reports the role
   unserved with the reason "execution previews are disabled", so the operator sees a switch rather than a missing
   install. The UI renders the level in its own colour and shows the maturity column.
7. **Only the operator can turn previews on.** The field is written only through the Operator settings endpoint (REST). It
   is absent from `NodeSettingsAgenticPatch`, the node-admin MCP tool arguments and `NodeSettingsAgenticView`, so no MCP
   client and no agent can write it, and a test asserts that absence. The UI asks for confirmation on the off-to-on change
   inside the save flow and names the residual risk below.
8. **MXC is a launch mechanism inside the `process` provider, never a provider.** On Windows `HostSandboxContainmentProbe`
   measures it through `MxcProbe` (`GetPlatformSupport`, then `Probe` on the exact policy every launch uses). The boundary
   is reported only when MXC accepts the policy AND reports no missing host preparation; MXC 1.0.0 only warns when the
   one-time admin `wxc-host-prep prepare-system-drive` / per-boot `prepare-null-device` step is missing, and children then
   fail to start, so that host reports the boundary unavailable with the exact command. The product never runs the prep.
   Resource limits and a separate egress mechanism are reported unavailable on Windows with their reasons. For a sandbox
   created under the boundary, `SandboxLauncher` builds an MXC `ContainerRequest` (through `MxcPolicyMapper`, re-checked by
   `MxcPolicyGuard` before every spawn) instead of an argv chain, and the provider runs it through `ISandboxChildProcess`,
   whose MXC implementation kills the contained tree with MXC's own `Kill()`. No Linux killer (scope unit, process group,
   orphan marker) applies to an MXC launch. The jail disk ceiling still applies.
9. **Explicit deny roots never shadow a grant.** The candidates are the user profile, `INodeDataDirectory.Root` and
   `AppContext.BaseDirectory`; any candidate that is an ancestor of (or equal to) the jail or a granted read-only tree is
   dropped, and the effective list is logged at Debug. On Windows the jail lives under the temp directory, itself under the
   user profile, and under AppContainer + DACL a denied path is a DENY ACE that an inherited deny could make beat the grant
   beneath it. The boundary does not rest on these denies: an AppContainer child reaches nothing it was not granted
   (default deny, proven by the Windows canary tests). The explicit denies are an extra layer where they are safe.
10. **The Preview mechanism serves floor workloads only.** `SandboxRequirements.RequestedIsolation` is the one rule the
    create site and the capability summary share: a `Filesystem` floor always asks for the boundary; a where-advertised
    preference (`AgentHome`, `Coder`, `WorkSession`) asks only when the backend advertises a filesystem boundary that is
    NOT the AppContainer one. So with previews on, `run_python` and Sandboxed MCP run under the boundary while AgentHome,
    Coder and work sessions stay on the plain process path, and the summary row says why. An architecture test enumerates
    every declaration against that rule. Promoting the mechanism to `Stable` is an amendment of this ADR that revisits it.
11. **DACL residue is swept at startup, top level only.** MXC clears its grants on exit (`ClearPolicyOnExit = true`); a
    crash can leave them. Before each spawn the provider journals the granted paths that sit under an engine-owned root
    (the jails under the sandbox container root and the compute venv directory; `MxcGrantJournal`), and a Windows-only
    hosted service drains the journal and removes explicit per-run AppContainer ACEs from each path and its direct
    children (`MxcAclResidueSweeper`), never recursively and never the two well-known package groups. It is the first
    hosted service and its start awaits the sweep, so no launch of this node (the MCP startup connector included) can
    overlap it and lose a live grant.
    Operator-supplied read-only trees, such as an MCP server directory, are never journaled or swept: they may carry
    another application's legitimate AppContainer ACEs. Residue left there is hygiene, not security, because MXC mints a
    fresh AppContainer SID per container and a dead container's stale allow ACE grants nothing to a new one.

## Accepted residual risk

- **Timeout-only execution on Windows (Q3).** Under the AppContainer boundary, `run_python` and sandboxed MCP servers run
  with no CPU, memory or process-count ceiling. The per-command timeout is the only bound; the setting description and
  the confirmation dialog say so. Both roles send no ceiling on Windows and are served, but a create request that carries
  explicit ceilings is refused rather than run with them silently dropped.
- **Host path names are visible, and OS system trees are readable.** The boundary denies the user profile, node data and
  engine directory, and grants only engine-owned trees; it does not hide the shape of the host.
- **The switch is writable by anything that holds an Operator session (compare the SF1 note in the plan).** A stored
  setting has an API path that a configuration file does not: a browser session with Operator rights can enable
  previews, and the server cannot tell a confirmed request from an unconfirmed one. The mitigations are that it is
  Operator-only, never agent-writable, visible on the settings page and in the node-info report, and off by default.
  The analogous configuration-only switch `RequireEgressDenial` is a TIGHTENING switch; this one widens, which is why the
  confirmation exists. [ADR 0020](0020-sandbox-security-profile.md) later made the sandbox security profile a stored,
  Operator-only tightening setting, so the configuration-only ruling no longer holds for the profile.

## Consequences

- Linux behaviour is unchanged and asserted: the bubblewrap handle reports `/work`, `/work/home`, `/tmp`, and the MCP
  transport's default `PATH` and reserved-mount check are unchanged there.
- The architecture test's maximum for the `process` backend gains `SupportsAppContainerBoundary`, and a test pins that a
  disabled Preview mechanism serves no filesystem-floor declaration.
- AgentHome, Coder and work sessions are unchanged on Windows whatever the switch says (Decision 10).
- The refusal texts of `run_python` and Sandboxed MCP name the Windows mechanism and the switch on a Windows host instead of
  bubblewrap.
- The `windows-sandbox` CI leg (`windows-2025`, host prep run first) runs the real-SDK tests, including a
  provider-level `cmd.exe` canary test, and fails on any skip.

## Still owed

- `run_python` on Windows: the boundary serves it, but the compute runtime is still provisioned on Linux only
  (`ComputePythonEnvironment.GetRuntimeAsync`; the uv pin and lockfile resolve for Linux x64). Until that separate gate is
  lifted, Sandboxed MCP servers are the only Windows workload the boundary actually runs.

- The operator's Windows live round: `run_python` and a Sandboxed MCP server under the boundary with the canary absent and
  the network denied, the residue sweep after a forced kill, and the capability page reading `PreviewIsolated`.
- The pinned Windows UBR the round ran on, recorded with that evidence.
