# ADR 0007: The sandbox execution substrate is capability-declared, and the backend is selected, never named

- **Status:** Accepted — by the repository owner (`w0rldx`) on 2026-08-25, with one amendment to sequencing: the selection layer is built in Phase 1, before G1, so that Development Mode lands on it rather than being migrated later.
- **Date:** 2026-08-25
- **Scope:** How a feature obtains an execution sandbox, and how a backend is chosen for it. It does not change what any
  backend enforces, and it does not add or remove a backend.
- **Authority:** Decided by the maintainer on 2026-08-25. Drafted from the secure-agent-execution gap mapping and
  the lane evidence beside it.
- **Amends:** [ADR 0004](0004-development-mode-container-execution-docker-stopgap.md) Decision §1 only — see
  [What this amends, and what it deliberately does not](#what-this-amends-and-what-it-deliberately-does-not).

## Context

Three consumers create sandboxes today, and each names its backend by *type*. `ISandboxRuntimeProvider` is the shared
contract; `IAgentSandboxRuntimeProvider`, `IDevelopmentSandboxRuntimeProvider` and `IWorkSessionSandboxRuntimeProvider`
are role markers over it; `SandboxProviderSelector` resolves each role from configuration at DI time.
`ProcessSandboxRuntimeProvider` implements all three, `DockerSandboxRuntimeProvider` implements only the Development
role. That omission is the enforcement mechanism for ADR 0004 §1 and is documented as such in the provider's own source
comment: registering the container provider for AgentHome or Coder is a compile error rather than a review finding.

That design is correct for two consumers and one widening. It does not scale to what is now in front of it, for three
reasons that are visible in the tree rather than speculative.

**It binds a decision that is not a type decision.** What Development Mode actually needs is *a toolchain that is not the
host's* — ADR 0004's Context says so in as many words: "No confinement mechanism supplies a toolchain. A container image
does." What `run_python` needs is *the host filesystem absent from the mount namespace*, and it asks for exactly that
(`SandboxIsolationMode.Filesystem`, refused fail-closed when `SandboxProviderCapabilities.SupportsFilesystemIsolation`
is absent). One of those two consumers expresses its need; the other expresses a backend. The capability vocabulary for
expressing needs already exists on `SandboxCreateRequest` and `SandboxProviderCapabilities` — it is simply not complete,
and the toolchain axis is the missing one.

**The marker set grows as consumers × backends, not as consumers.** Sandboxed outbound stdio MCP (gap G2) and work
sessions are both queued. Under the present scheme each new consumer needs a new marker interface, a new
`SandboxProviderSelector` resolver, a new configuration key, and a decision — encoded as an `implements` clause — about
every backend. `SandboxProviderSelector.ResolveWorkSession` already documents the resulting awkwardness: it invents no
configuration key of its own because "nothing in v1 executes inside this jail".

**A backend named in a feature is a backend the feature is stuck with.** ADR 0004 §3 keeps MXC as the long-term
hard-isolation seam and the 2026-07-28 plan §8.8 records OpenSandbox as *declined with flip conditions* and names its
proper home as "behind the **existing** `ISandboxRuntimeProvider` SPI". Both of those futures are backend swaps. A swap
is only cheap if no consumer named the thing being swapped.

## Decision

1. **A consumer declares requirements; it never names a backend.** A new engine-owned record — provisionally
   `SandboxRequirements` — carries what the workload needs, on axes that already have meaning in this tree: a
   **toolchain source** (the host's toolchain, or a named engine-approved image); a **network posture**
   (`SandboxNetworkPolicy`); an **isolation floor** (provisionally `SandboxIsolationLevel`, subsuming today's
   `SandboxIsolationMode`); **persistence** across calls (a preserved trusted host workspace, or disposable); and a
   **disk ceiling**. It is composed by engine code at the consumer's single creation site, from constants — never from
   configuration, never from a repository, never from anything a model can write.

2. **A selector resolves a backend that can honour the whole declaration, and fails closed when none can.**
   Provisionally `SandboxBackendSelector` over `ISandboxBackend`. Resolution is **minimal-satisfying**: among backends
   that meet every declared requirement, the one with the smallest additional privilege footprint wins. It is not
   most-capable-wins, and that ordering is load-bearing — see §4 below. When no registered backend satisfies the
   declaration the call throws `SandboxCapabilityNotSupportedException` carrying the unmet axis. There is no fallback,
   no downgrade, and no "best effort" resolution, exactly as ADR 0004's Consequences require today.

3. **The layer sits above the concrete backends and changes none of them.** `DockerSandboxHardening`,
   `ProcessSandboxRuntimeProvider`'s jail and isolated chain, `DockerDaemonPreflightService`'s TOFU attestation, and the
   fail-closed read-back contract are untouched by this record. What changes is who decides which of them runs.

4. **The compile-time guard's intent is preserved by three mechanisms that replace the one it used.** ADR 0004 §1 is
   enforced today by an absent `implements` clause. That clause encodes two guarantees, and each is re-established
   explicitly rather than assumed:

   - *A container requirement must not silently spread to a feature that should not need one.* Under
     minimal-satisfying resolution, a consumer that does not declare an image-backed toolchain can never resolve to a
     container backend, because a container backend is strictly more privileged than the process backend on the axis
     that matters here — a live daemon whose socket is root-equivalent on Linux. The declaration itself is an
     engine-owned constant at one site, so widening a consumer's requirements is a source change in a reviewed file,
     which is the same visibility the `implements` clause gave.
   - *There must be no unisolated fallback.* The isolation floor has **no default value**: a declaration that omits it
     does not compile. A new consumer therefore cannot inherit the weakest posture by saying nothing, which is the
     failure mode a defaulted field would introduce.
   - *The guarantee must be mechanically checked, not reasoned about.* An architecture test enumerates every
     consumer's requirements constant and asserts the exact set of backends that may serve it. This is the honest cost
     of the change and is recorded as one below: the guard moves from the compiler to a test.

5. **Provider capability reporting becomes the same vocabulary the requirements are written in.** The axes a backend
   advertises (`SandboxProviderCapabilities`) and the axes a consumer declares are one set, so "can this host run this
   workload, and under what boundary" is answerable without inspecting a provider type. This is what makes gap G6
   (isolation level not surfaced to `CapabilityReportComposer`) a projection rather than a new model.

6. **Names in this record are provisional.** `SandboxRequirements`, `ISandboxBackend`, `SandboxBackendSelector` and
   `SandboxIsolationLevel` are placeholders chosen to make this document readable. The implementation must follow the
   existing naming rather than these: the tree says `ISandboxRuntimeProvider`, `SandboxProviderCapabilities`,
   `SandboxProviderSelector`, `SandboxIsolationMode`, and a rename of the SPI is not part of this decision. Read every
   provisional name as "the thing that plays this role", not as an instruction to add a type with that spelling.

## What this amends, and what it deliberately does not

**Amended — ADR 0004 Decision §1.** "Docker is permitted for Development Mode build, test and lint execution only"
becomes: *Docker is permitted for any workload the substrate selects it for — which, by construction, is only a workload
that declares an engine-approved image-backed toolchain it cannot get from the host.* Development Mode is such a
workload today and remains the only one. The narrowing that mattered is retained by a different mechanism: the permission
is now bounded by a declared need rather than by a feature name, and it is still a reviewed source change to create a
new one.

**Unamended — ADR 0004 Decision §2.** No Docker on the inference path; no Docker in model hosting, acquisition,
embedding, image generation or any chat-path provider; HostAgent and the sandbox-gRPC transport stay deleted. The
substrate has no consumer on the inference path and must never acquire one.

**Unamended — ADR 0004 Decision §3.** MXC remains the long-term hard-isolation seam, with its own README's
"not a security boundary" qualification intact. Under this record MXC is a backend behind the same layer, and so is
OpenSandbox under §8.8's three flip conditions. Neither is adopted here.

**Unamended — ADR 0004 Decision §4.** `PLAN-sandbox-hardening-2026-07-01.md` stays complementary; the process backend
is not superseded, and it remains the backend that serves every consumer with no image-backed toolchain need.

**Unamended — ADR 0004 Decision §5.** Repository-supplied container configuration stays rejected wholesale: engine-
generated canonical mounts only, no socket or named-pipe mounts, no devices, no `--privileged`, no added capabilities,
no host namespaces, digest-pinned images only (`ContainerSandboxOptionsValidator`), no repository Dockerfile builds, no
`${localEnv:*}`. A `SandboxRequirements` value is engine-owned for precisely this reason — a requirements record
derivable from a repository would be `devcontainer.json` under another name.

**Unamended — ADR 0005.** Training stays a uv-managed Python subprocess and is not a substrate consumer.

## Non-goals

- **An egress gateway.** `SandboxNetworkPolicy.Restricted` stays unimplemented and fail-closed under this record. The
  allow-list proxy is ADR 0004 D6's v2, "a project of its own", and the 2026-07-28 plan §8.8 already names the shape to
  copy when it is built. Nothing here brings it forward.
- **Re-evaluating OpenSandbox, gVisor, Kata or Firecracker.** Evaluated 2026-07-29 and recorded in §8.8 with flip
  conditions. Cited, not redone.
- **Windows containment.** The process backend's Windows path is `SandboxContainment.None`
  (`HostSandboxContainmentProbe`: "the Windows Job Object path is not implemented"). This record does not change that; it
  makes the shortfall expressible, which is a prerequisite for reporting it honestly, not a fix.
- **Renaming the SPI, or collapsing the role markers before there is a second consumer that needs it.**
- **A trust model for MCP servers.** Trust tiers (Sandboxed / PrivilegedHost / BuiltInTrusted) are a separate decision;
  remote non-loopback HTTP MCP stays deferred.
- **Content-rich trajectory capture.** Policy first — see `docs/security/agent-trajectory-data-policy.md`.

## Consequences

Stated honestly, including the ones that are costs.

- **The strongest guarantee in the current design is weakened in kind.** "Docker cannot be wired into AgentHome" is a
  compile error today; under this record it is a test failure. A compile error cannot be skipped, disabled, or made
  flaky, and a test can. The mitigation is that the test is an enumeration over engine-owned constants rather than a
  behavioural test, so it fails deterministically and offline — but it is a real reduction and should be weighed as one.

- **Minimal-satisfying resolution has to be defined on an axis that is arguable.** "Least additional privilege" is
  obvious for process-vs-Docker on a given host (a root-equivalent daemon socket on Linux) and will be less obvious the day
  a third backend exists. The ordering must be an explicit, code-owned ranking with the reasoning attached, not an
  emergent property of a `switch`.

- **A consumer can no longer be certain, by reading its own file, which backend it gets.** That is the point of the
  change and it is also its cost: diagnosis moves from "read the injected type" to "read the resolved backend from the
  log line the selector must emit". The selector must therefore record its resolution — declaration, candidates,
  winner, and rejected candidates with reasons — or this trade is a straight loss.

- **Configuration surface shifts, and an existing operator key changes meaning.** `Development:Sandbox:Provider` and
  `AgentHome:Sandbox:Provider` name backends directly today, and `ResolveDevelopment` falls back to the agent key when
  the Development key is unset. Under selection they become at most a *constraint* on the candidate set. Migration must
  be explicit; silently reinterpreting a set key is how a hardened node becomes an unhardened one.

- **Nothing about isolation strength improves.** This record moves a decision; it enforces no new boundary. The process
  backend is still supervised execution with a namespace boundary, not a kernel-hardened one. Docker is still a shared
  kernel. On Linux, Docker-socket access is still root-equivalent — ADR 0004 documents this rather than mitigating it,
  and that remains true.

- **Fail-closed becomes more visible to users, not less.** A consumer declaring an isolation floor no host can meet gets
  a refusal where it previously got a quieter degradation. Development Mode already behaves this way (no daemon, no
  Development Mode). Extending the same honesty to other consumers will surface hosts that were silently running weaker
  than the reader of the code assumed — which is a finding, not a regression.

- **Revisiting is expected.** When a backend supplies both hard isolation and a toolchain, the Development Mode
  declaration should resolve to it with no change at the consumer, and that is the whole point of doing this now. Any
  change to the boundaries retained above is a new operator decision, not an edit to this record.
