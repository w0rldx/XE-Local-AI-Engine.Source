# ADR 0004: Docker is permitted for Development Mode execution only, as a stopgap ahead of MXC

- **Status:** Accepted
- **Date:** 2026-07-29
- **Scope:** Development Mode build/test/lint execution. Nothing else.
- **Authority:** Decision D0 of the 2026-07-28 Development Mode container-sandbox and command-profiles plan, resolved by the maintainer on 2026-07-28. Accepted by the repository owner (`w0rldx`) on 2026-07-29. This record does not reopen that decision; it states it precisely enough to be enforced and to be revisited.
- **Amends:** the 2026-06-17 runtime re-architecture decision that Docker was to leave the runtime path entirely — its locked decision 2 (`epic:46`) and the acceptance criterion at `epic:29`.
- **Living implementation status:** [`docs/roadmaps/development-mode-container-status.md`](../roadmaps/development-mode-container-status.md)

## Context

The runtime re-architecture epic (2026-06-17) removed Docker from this product entirely. Two separate uses were dropped in the same sweep: hosting Ollama for inference, and sandboxing tool execution. Locked decision 2 recorded the outcome as *"Docker — Removed entirely — no Docker dependency anywhere"* (`epic:46`), and the epic's acceptance criteria added an enforcement clause: *"No Docker daemon present anywhere; `grep`-clean of Docker.DotNet / sandbox-gRPC from the build."* (`epic:29`). Both were correct for what they were solving. The driving goal was GPU inference with a **driver-only footprint** — no CUDA toolkit, no WSL requirement, no daemon — and that goal is met today and is not in question here.

Isolation was handled by leaving a seam rather than by building one. The epic states this in four places: hard isolation is deferred behind `ISandboxRuntimeProvider` with **MXC** named as the eventual backend (`epic:16`, `epic:23`, `epic:54`, `epic:72`), and MXC is defined and qualified in the glossary — Microsoft Execution Containers, policy-driven cross-platform OS-kernel isolation, at the time `0.6.0-alpha`, TypeScript-SDK-only, and per its own README **explicitly not to be treated as a security boundary** (`epic:118`). The shipped provider says the same thing in its own class documentation: soft guards only, a future hardware-isolated MXC provider *replaces the whole provider, not the contract* (`ProcessSandboxRuntimeProvider`, class doc — anchors refreshed 2026-07-29 after that doc was rewritten; grep the quoted phrase rather than a line number). The README repeats it for users.

> **Anchor maintenance, 2026-07-29** (citations only; no change to this record's reasoning or to any boundary in the Decision section). The two `ProcessSandboxRuntimeProvider.cs` line cites above drifted when that class doc was rewritten in the sandbox-hardening work and one came to point at a bare `</para>`; they are replaced with the symbol plus the quoted phrase, which do not drift. The README sentence originally quoted here verbatim — *"MXC and devcontainer-backed isolation remain future provider work"* — was deliberately narrowed by the Slice 5 sweep to *"MXC remains future provider work"*, because devcontainer-backed isolation is no longer purely future under this ADR. The substance the quote was cited for is unchanged.

What has changed is not the isolation posture but the **product scope**. Decision D0b ratifies that users register or create arbitrary repositories — .NET, Rust, Python, Node — with templates for each. Under that scope, confinement alone is insufficient, and this is a toolchain problem before it is a security problem: a confinement mechanism such as `bwrap` restricts what a process may touch, but the process still runs against **the host's** SDKs. A repository needing .NET 8 on a .NET 10 host, or Rust on a host with no `cargo`, then fails in a way the agent cannot repair — and Development Mode's validation would be measuring the operator's machine rather than the model's patch. No confinement mechanism supplies a toolchain. A container image does.

So Development Mode needs a container runtime for a reason the epic never weighed, because the multi-language product scope did not exist when the epic was locked. Leaving `epic:46` and `epic:29` unamended would mean shipping that work in violation of criteria still on the books.

## Decision

1. **Docker is permitted for Development Mode build, test and lint execution only.** Concretely: a `Docker.DotNet` package reference in the build, and a running Docker daemon as a **hard runtime requirement for Development Mode**.

   > **Package id, for precision** (clarification added 2026-07-29; does not alter this decision's scope). The reference is **`Docker.DotNet.Enhanced`** — the maintained fork at `github.com/testcontainers/Docker.DotNet` — which ships the `Docker.DotNet` **assembly and namespace**, so every `using` and type name reads identically to the original. The original `Docker.DotNet` package (3.125.15) is unmaintained since 2023-05-18, netstandard-only, and pulls in Newtonsoft.Json. **Both compile**, so a from-memory package reference resolves to the wrong one silently. Quote this footnote, not the sentence above, when adding the reference.

2. **Docker is not permitted on the inference path, and is not reintroduced for the model runtime or for HostAgent.** The epic's teardown of both stands in full. Model inference remains a supervised host process (llama.cpp / `llama-server`, optionally native Ollama) with a driver-only footprint. There is no Docker in model hosting, model acquisition, embedding, image generation, or any provider on the chat path. HostAgent and the sandbox-gRPC transport remain deleted and are not to be restored.

3. **MXC remains the long-term hard-isolation seam. Docker is interim and is not a replacement for it.** The `ISandboxRuntimeProvider` seam stays exactly where the epic put it (`epic:16`, `:23`, `:54`, `:72`; `ProcessSandboxRuntimeProvider.cs:20`, `:30`; `README.md:30`). A container provider slots in behind that seam as one more implementation; it does not close the seam, does not retire the MXC plan, and does not license removing the seam because "isolation is solved now". The epic's qualification of MXC also survives unamended: MXC is early-preview and per its own README is not itself a security boundary (`epic:118`), so adopting it later is defense-in-depth rather than a guarantee — and the same honesty applies to Docker in the meantime.

4. **The 2026-07-01 sandbox-hardening plan remains complementary and is not superseded.** Under that plan's decision D2 the provider choice is **per feature**, not global: Development Mode gets the container provider, while **AgentHome (4 injection sites) and Coder (1 site) stay on `ProcessSandboxRuntimeProvider`**. (Count corrected 2026-07-30 when the seam was built: 11 sites total — AgentHome 4, Coder 1, Development 6. This record previously said "Coder (3 sites)", which counted Coder's **tool entry points**; Coder has exactly one injected provider, `CoderWorkspaceReader`, reached from three tools, because it attaches to AgentHome's sandbox rather than creating its own. A citation correction only — it moves no boundary in this section.) Those two features are therefore untouched by this ADR, and without the hardening plan they remain unhardened — it is the only thing that gives them default-deny egress, real cgroup ceilings and an orphan reaper. It also builds the orphan-reaper primitive the container lifecycle work needs. This ADR does not reduce its priority; if anything it isolates its scope so the two can proceed independently.

5. **The permission is narrow by construction, and the narrowness is part of the decision.** Repository-supplied container configuration is rejected wholesale (plan D7): engine-generated canonical mounts only; no socket or named-pipe mounts, no devices, no `--privileged`, no added capabilities, no host PID/network/IPC namespaces; operator-approved digest-pinned images only; no repository Dockerfile builds; no `${localEnv:*}`. A `devcontainer.json` in a repository the agent can write is untrusted input, and a Docker-socket mount is full host compromise. Any future widening of this surface is a new decision, not an implementation detail.

## Consequences

Stated honestly, including the ones that are costs.

- **Development Mode becomes unavailable without a container runtime.** A user with no Docker daemon does not get a degraded Development Mode; they get none. This is a real reduction in reach for the affected feature, and it must fail with an actionable message rather than a generic error.

- **There is no unisolated fallback, by design (plan D2).** Development Mode does not silently fall back to `ProcessSandboxRuntimeProvider` when a daemon is missing. A fallback would mean the product's isolation posture depends on what happens to be installed on the box, and an operator could not tell from the outside which one ran. The cost is that **there is no rollback story**: reverting the container work leaves users without Development Mode until the revert lands. That is a release gate, not a risk to be noted and passed over.

- **The packaging and quality gates gain a Docker requirement.** Real-daemon integration tests are required (plan D4), and *daemon unavailable* must be reported as **blocked or skipped-with-reason, never as a pass* — a suite that goes green because it silently skipped the only tests that exercise isolation is worse than a red one.

- **"Add a dependency" becomes a blocked task class in v1** (plan D6). Agent-facing execution runs with network off, restoring only from the base commit's manifests; a patch touching a dependency manifest fails validation with a specific reason. A restricted package proxy is the intended v2 answer and is a project of its own.

- **A pinned image digest pins bytes, not hermeticity.** Mounts, runtime state, host kernel, platform, dependency resolution and network inputs all remain variable. Do not describe digest pinning as reproducibility.

- **On Linux, access to the Docker socket is root-equivalent.** This is documented, not mitigated. Rootless Docker is the user's option; the product neither depends on it nor claims it.

- **Eleven documents currently assert "no Docker / no container sandbox" and will contradict this record.** *(Status, 2026-07-30: **discharged.** The sweep ran on 2026-07-29 and found the set was larger than this record estimated — sixteen documents, not eleven — and it is merged at `0b6c544e`. The consequence below is retained as written because it is the reasoning that gated the sweep, not a live claim about the tree.)* They are deliberately **not** edited by this ADR — the sweep is plan Slice 5 and follows sign-off, so that no documentation edit can appear to have granted the permission. Until then, this ADR is the newer record and governs. The set is: `docs/agent-knowledge.md` (three places, including the "locked runtime decisions — do not helpfully reintroduce" section and the stale-beliefs table), `docs/wiki/{01,02,03,11,12}`, `Home.md`, `README.md` (×2), and the "Security posture (v1)" block in the `ProcessSandboxRuntimeProvider` class documentation.

- **Revisiting this is expected, not exceptional.** Docker is recorded as a stopgap. When MXC — or another backend behind the same seam — can supply both isolation and a toolchain, the Development Mode provider should move to it, and this ADR should be superseded rather than quietly widened. Any change to the four boundaries in the Decision section requires a new operator decision rather than an edit to this record.

## Implementation status

Implementation progress is intentionally maintained outside this immutable decision record. See the
[Development Mode container implementation status](../roadmaps/development-mode-container-status.md) for the current
tree state and the delivery plan for remaining Slice 3 gates.
