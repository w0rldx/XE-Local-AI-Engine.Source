# Compute Tools — Sandboxed Code Execution for Agents

> Baseline: `test/math-agent-eval` · Reviewed: 2026-08-24 · Code-grounded.

The **compute tools** subsystem allows governed agents to execute short scripts in a **sandboxed, offline interpreter** for numeric and symbolic computation. The first and only v1 tool is `run_python`, which runs arbitrary Python 3 code with numpy, scipy, and sympy available — no network, no filesystem persistence, no conversation access.

This is distinct from the **Custom Tools** library (`CustomToolCatalog`), which executes operator-authored, node-local commands through an uncontained host-process executor. Compute tools route through the **process-role sandbox** (`ISandboxRuntimeProvider`), the same containment primitive that supervises AgentHome and Coder commands, and they are held out of the default tool offer entirely — profile-opt-in only.

---

## 1. What the tool does — `run_python`

The `run_python` tool (`ComputeToolDefinition`, `XE-Local-AI-Engine.Client.Application/Services/Compute/`) is a first-class agent tool that accepts a Python 3 script as input and returns its exit code, stdout, and stderr:

```csharp
// Offered schema (ComputeToolDefinition.ParameterSchema)
{
  "type": "object",
  "additionalProperties": false,
  "required": ["code"],
  "properties": {
    "code": { "type": "string", "minLength": 1 }
  }
}
```

**What it is:**
- A **short-form executor** for arithmetic, algebra, calculus, and numeric/symbolic claims a model wants to verify before asserting them.
- A **research loop facilitator**: models can call it many times per turn to iteratively check, fix, and refine calculations.
- **No magic inference**: the model is not given an expression's evaluated form implicitly — it must `print()` what it wants to see.
- **Deterministic**: the same script yields the same output every run, suitable for verification and debugging.

**What it is not:**
- Not a **data-science environment** — the full scipy/numpy/pandas stack is not provisioned; the closure is deliberately minimal (numpy, scipy, sympy only) to keep provisioning fast and safe.
- Not a **code-generation tool** — the agent author defines what tools the agent has access to; a tool is never offered speculatively.
- Not **filesystem persistent** — the jail the script runs in and the `HOME`/`TMPDIR` scratch it is pointed at are both created per call and deleted when it returns, so one call's files are never readable by the next. (A script writing to an *absolute* host path outside both is a separate matter: the process provider has no mount layer — see §2.2.)

**Available libraries:**
- **numpy** — arrays, linear algebra, broadcasting, dtypes.
- **scipy** — integration, optimization, statistics, special functions, sparse arrays, signal processing.
- **sympy** — symbolic algebra, calculus, differential equations, exact rational arithmetic, matrix algebra.

No torch, no pandas, no network libraries, no system shell. All three libraries are pinned in `tools/compute/pyproject.toml` and `uv.lock`.

---

## 2. Execution path — process sandbox isolation

`run_python` routes through the **process-role `ISandboxRuntimeProvider`** (`ProcessSandboxRuntimeProvider`, `XE-Local-AI-Engine.Client.Application/Services/Sandbox/`), the same containment primitive that AgentHome and Coder use today. This is *not* Docker, *not* a custom HostProcessExecutor, and it is *not* a lightweight fork of system Python.

### 2.1 Execution flow

1. **Handler validation** (`RunPythonToolHandler.ExecuteAsync`): The node kill-switch `Compute:Enabled` (default `false`) short-circuits; if enabled, the JSON request is deserialized and validated (non-empty, ≤20,000 chars).

2. **Gateway setup** (`ComputeToolGateway.ExecuteAsync`):
   - Resolves the pinned-venv Python interpreter path (`IComputePythonEnvironment.GetInterpreterPathAsync`).
   - Acquires the node's identity (OwnerUserId, NodeId) from `IAgentHomeIdentityProvider` (shared with AgentHome).
   - Creates a sandbox with a distinct runtime profile (`"compute-python"`, separate from AgentHome's `"dotnet-agent-home"`), so compute scripts never share a jail with workspace operations. The attach key carries that profile **plus this invocation's id**: the registry attaches *by* the key, so a constant one hands two concurrent `run_python` calls a single live jail — one shared working directory, and the first call to finish tearing it down under the other.
   - Creates a fresh per-invocation directory for the script's `HOME`/`TMPDIR`.

3. **Sandbox execution** (`IAgentSandboxRuntimeProvider.ExecuteAsync`):
   - The Python interpreter is invoked with `-I` (isolated mode: no PYTHONPATH, no user site-packages, no script directory import).
   - The script is passed on stdin with `-` (not written to disk, not exposed in the process table).
   - Execution runs inside the process sandbox with:
     - **Working directory jail**: the script cannot `cd` outside its assigned directory.
     - **Environment scrub**: only whitelisted environment variables are inherited; `HOME` and `TMPDIR` are pointed at the call's own scratch directory to prevent library side-effects (e.g. caching to `~/.cache`).
     - **Network policy**: empty network namespace (if the host supports it) — no DNS, no IP stack, no loopback access *to the outside*.
     - **Resource ceilings**: configurable CPU cores, resident memory, and PID limits, applied where the host can enforce them (systemd cgroup v2; silently degraded on older systems per the sandbox provider's fail-closed contract).
     - **Wall-clock timeout**: process tree is killed if execution exceeds the configured ceiling (default 30 seconds).
     - **Output byte caps**: stdout and stderr are each truncated at `MaxOutputBytes` (default 64 KiB) with a truncation marker `…[output truncated]`.

4. **Teardown** (`finally`): The jail is killed and the scratch directory deleted, which is what makes the statelessness above real rather than advertised — the jail root and that directory are the only two places a script can write. It runs on failure and cancellation too, since that is when a script is most likely to have left something behind. The provisioned venv sits outside both and is never touched, so a fresh jail per call costs a directory create, not a re-provision. Covered by `ComputeSandboxLiveTests.RunPython_CannotSeeWhatAnEarlierCallWrote` (write in call 1, assert absent in call 2).

5. **Result formatting** (`ComputeToolGateway.FormatResult`): Exit code, stdout, and stderr are formatted the same way `HostProcessExecutor.FormatResult` does, so all command-shaped tools in the product read uniformly to agents:

   ```
   Exit code: 0
   STDOUT:
   5.0
   STDERR:
   ```

### 2.2 Why process sandbox, not Docker or HostProcessExecutor

**Docker**: [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md) explicitly excludes the chat/inference path from Docker with no fallback. Requiring a container daemon for every chat turn contradicts the product's driver-only footprint, and the containment Docker provides (toolchain supply, language isolation) is not needed here — only resource isolation and network denial, both of which the process sandbox already provides.

**HostProcessExecutor / uncontained host process**: This is what Custom Tools use. The `HostExecutableGuard` denylist blocks bare interpreters because the executor offers zero filesystem boundary and zero network isolation — an operator-chosen path with no confinement is inherently a foot-gun. The process sandbox is *different* — it is a *different execution role* than the Custom Tool host executor, which is precisely why routing through it does not undermine the denylist. An agent can never select the interpreter path (it is the one fixed, digest-pinned venv Python the engine provisions), and it cannot reach the network. Note what this does *not* buy: the process provider has no mount layer, so a script still sees the host filesystem as the worker user — that is why the tool is `WriteExecute`, approval-required, off by default, and never offered to a cloud-hosted model.

**System Python via detection / custom wrapper**: [ADR 0005](../adr/0005-training-runtime-python-exclusivity-and-project-placement.md) decided this for Training: "the host's Python is not usable." Fragmentation, version drift, missing or bloated site-packages, no clean uninstall — those problems do not go away for compute tools. The pinned-venv approach (uv binary download, `uv sync` with a repo-committed lockfile) gives every node the *exact same* packages with no operator setup, no version surprises, and a clean teardown (delete the venv directory).

### 2.3 On-disk venv provisioning and lifecycle

`IComputePythonEnvironment` (`XE-Local-AI-Engine.Client.Application/Services/Compute/`) manages the venv:

- **Acquisition**: A shared `uv`-acquisition helper (factored from `Providers.Training`) downloads the pinned `uv` binary (digest-validated, same mechanism as `LlamaCppBinaryManager`) and runs `uv sync --locked` against `tools/compute/uv.lock` to install numpy, scipy, and sympy.

- **Location**: The venv lives in a node-owned directory under `LocalDataDirectories.ComputeRuntimeDirectory` (typically `.xe-local-ai-engine/compute-python-venv/` on Linux, `.xe-local-ai-engine\compute-python-venv\` on Windows; paths are platform-specific).

- **Cold-start provisioning**: The first call to `GetInterpreterPathAsync` triggers provisioning if the venv is missing, which takes ~1 minute and ~220 MB for numpy/scipy/sympy on a typical box. Subsequent calls reuse it.

- **Cleanup**: The venv directory is never cleaned up automatically — it persists across node restarts, which is the point: it is the expensive part, it is read-only to scripts, and it is what a per-call jail teardown must *not* take with it. An operator can delete it manually; the next compute call re-provisions it. Per-call state (the jail, the `HOME`/`TMPDIR` scratch) is the opposite and is deleted every call — see step 4 of the execution flow.

- **Pinning discipline**: `tools/compute/pyproject.toml` and `uv.lock` are both committed to the repo. Adding a dependency is a deliberate decision, never a convenience — every package here is code a model can execute.

---

## 3. Security and gating

### 3.1 ToolCategory and approval

- **Category**: `ToolCategory.WriteExecute` — the existing "can write files or run commands on the node" class. The UI badges and approval round-trip key off this category the same way they do for `run_in_agent_home`.

- **RequiresApproval**: `true` — every invocation requires an out-of-stream human approval round-trip (or an operator can tighten the policy to require approval only on first use per conversation, or loosen it to auto-approve, via `IToolApprovalPolicy`).

### 3.2 Offer gating — profile opt-in + cloud-model exclusion

`run_python` is held out of the default whole tool offer. It is **never offered** in:
- Mode-off (default) chat turns.
- Any turn routed through a cloud-hosted model.
- Unattended paths (Scheduler, spawned children, delegate-scope inbound MCP).

It is **offered only when**:
- The agent's `AllowedToolNames` explicitly includes `"run_python"` (profile-opt-in).
- The active model is tool-capable.

**Rationale**: The tool executes model-authored code on the node. Offering it to a remote model — especially when the model cannot see the code's effect before committing to it — is a sharper version of the "arbitrary code execution offered to a model" concern the Custom Tool denylist was built to prevent. The operator who shapes an agent definition chooses its tools; the tool is never speculatively offered.

Seeded agent example: `MathematicianAgentSeeder` (`Services/Agents/Implementation/MathematicianAgentSeeder.cs`) names `run_python` in its `AllowedToolNames`, which is how a Mathematician agent gets access to it.

### 3.3 Configuration kill-switch and defaults

`ComputeOptions` (section name `"Compute"`, configuration file key `Compute`) has:

| Setting | Type | Default | Notes |
|---------|------|---------|-------|
| `Enabled` | `bool` | `false` | Master kill-switch. Off unless explicitly set to `true`. Short-circuits before any venv/sandbox work. |
| `TimeoutSeconds` | `int` | `30` | Wall-clock ceiling per script. Shorter than AgentHome's (which can be minutes for workspace operations) because research loops call this many times. |
| `MaxOutputBytes` | `int` | `65536` | Byte ceiling per stream (stdout/stderr independently). Truncated with `…[output truncated]`. |
| `MemoryMb` | `int` | `2048` | Resident-memory ceiling for the sandbox, applied where the host can enforce it. |
| `CpuCount` | `double` | `2` | CPU-core ceiling for the sandbox, applied where the host can enforce it. |
| `PidsLimit` | `int` | `64` | Process/thread ceiling for the sandbox, applied where the host can enforce it. |

All resource limits are **capability-gated** — the sandbox provider fails closed if the host cannot deliver the requested containment. An old system without cgroup v2 or systemd --user will report its limitations and requests will fail with a clear message rather than silently degrading.

**Maintainer rule**: The `Enabled` kill-switch is the single source of truth for "is this node allowed to execute code" — it parallels `AgentHome:Enabled` and is read the same way. Never add a code path that runs Python without checking this flag first.

---

## 4. Linux-only v1, Windows planned for v2

`run_python` is **Linux-only in v1** and requires:

- The **process-role sandbox provider** to be functional (Linux with or without systemd cgroup v2 support; other OSes degrade capabilities but sandbox still runs).
- **`uv` binary** — pinned download, same `uv` platform detection as Training.
- **Python 3.13** as the target interpreter (locked in `tools/compute/pyproject.toml` `requires-python`).

On Windows, the process sandbox provider is not currently available. Windows compute support is planned for v2 once the Windows sandbox strategy is finalized (pending ADR or implementation completion).

---

## 5. How an operator enables it

### 5.1 Enable the tool on the node

Set `Compute:Enabled=true` in configuration:

**Aspire dev** (`appsettings.json` or environment):
```json
{
  "Compute": {
    "Enabled": true
  }
}
```

**Desktop** (via UI settings or `node-settings.json`):
```json
{
  "compute": {
    "enabled": true
  }
}
```

### 5.2 Offer the tool to an agent profile

Add `"run_python"` to the agent definition's `AllowedToolNames`. Example:

```csharp
// In MathematicianAgentSeeder (Services/Agents/Implementation/MathematicianAgentSeeder.cs)
IReadOnlyList<string> allowedToolNames = [ComputeToolDefinition.ToolName];

// Or in a custom agent definition via the operator UI/API
{
  "name": "MyMathAgent",
  "modelId": "some-model",
  "allowedToolNames": ["run_python", "get_current_time"]
}
```

### 5.3 Approval policy (optional)

By default, `run_python` requires approval on every call. The operator can adjust this via `IToolApprovalPolicy` (per-node config, not per-tool):

- **Tighten only**: the policy can turn a default-off tool into approval-required, but never waive a catalog default. `run_python`'s default is `RequiresApproval=true`.
- **Example loosen** (if approved by operator): configure `Tool:Category:WriteExecute:Approval=false` to auto-approve all WriteExecute tools, including `run_python`.
- **Example tighten**: the default already requires approval; no further tightening needed unless customized.

---

## 6. When to use compute tools vs. other approaches

| Scenario | Use | Why not |
|----------|-----|--------|
| An agent needs to verify arithmetic or symbolic algebra | `run_python` | The tool is designed for this. |
| An agent needs to read/write files in the workspace | `run_in_agent_home` | AgentHome is the workspace interface; compute has no filesystem boundary. |
| An operator needs to define a custom command the agent can call | Custom Tool (Command type) | Custom Tools execute on the host with no sandbox; reserved for trusted operator-authored programs. |
| An agent needs to integrate with an external API or service | `run_python` + network-blocked | Cannot reach the network — use Custom Tool's HTTP Fetch type instead, if appropriate. |
| An AI system needs fine-tuning or model training | Training runtime (`Providers.Training`) | Separate runtime, separate venv, separate provisioning — compute is for inference-time math, not training. |

---

## 7. Testing and validation

### 7.1 Unit tests

- **Handler**: `RunPythonToolHandlerTests` validates the kill-switch, JSON parsing, request validation (empty/oversized code), and cancellation propagation.
- **Offer provider**: `LocalToolOfferProviderTests` verifies `run_python` is absent from the default offer, present only when `AllowedToolNames` opts in, and absent from the cloud/no-local-data variant.
- **Schema compatibility**: `ComputeToolSchemaCompatibilityTests` confirms the parameter schema compiles through the GBNF sanitizer (parity with `LlamaGrammarToolOffer` tests).

### 7.2 Integration tests

Real-sandbox tests (Linux CI, skip-not-pass on hosts without the sandbox):
- Network attempt inside a script is denied (empty netns confirmation).
- A `while True: pass` script is killed at timeout, reported as timed-out.
- Output over the byte cap is truncated with the marker.
- A script that writes past `MaxJailDiskBytes` is terminated (jail-growth watchdog).
- `import numpy, scipy, sympy` succeeds (venv provisioning end-to-end).

### 7.3 E2E smoke test

Replay the original math-verification loop (10-call iterative debug cycle) through the real first-class tool and confirm behavior parity with the prototype that motivated this feature.

### 7.4 Validation command

Run the compute tool against a known script to verify provisioning and containment:

```bash
# Locally (requires Aspire or desktop mode running)
curl -X POST http://localhost:5000/api/local/v1/agents/invoke \
  -H "Authorization: Bearer <JWT>" \
  -H "Content-Type: application/json" \
  -d '{
    "agentId": "<mathematician-id>",
    "messages": [{
      "role": "user",
      "content": "Use run_python to verify: what is the square root of 2?"
    }]
  }'
```

---

## 8. Files and components

| File | Role |
|------|------|
| `Services/Compute/ComputeToolDefinition.cs` | Tool name, description, parameter schema (20K char code ceiling) |
| `Services/Compute/ComputeRunToolRequest.cs` | Typed request DTO + validator (non-empty, length check) |
| `Services/Compute/ComputeOptions.cs` | Configuration section (Enabled, timeouts, resource limits) with `ValidateOnStart` |
| `Services/Compute/IComputeToolGateway.cs` | Sandbox executor contract |
| `Services/Compute/Implementation/RunPythonToolHandler.cs` | `IClientLocalToolHandler` bridge; reads kill-switch, deserializes, validates, delegates to gateway |
| `Services/Compute/Implementation/ComputeToolGateway.cs` | Resolves venv, acquires sandbox, builds/formats `SandboxCommandRequest`, formats result |
| `Services/Compute/IComputePythonEnvironment.cs` | Venv provisioning contract |
| `Services/Compute/Implementation/ComputePythonEnvironment.cs` | Resolves/provisions the venv via shared `uv` helper against `tools/compute/uv.lock` |
| `Services/Chat/Implementation/LocalToolOfferProvider.cs` | Merges compute offer DTO; gates profile-opt-in and cloud-model exclusion |
| `Services/Agents/Implementation/MathematicianAgentSeeder.cs` | Seeded Mathematician agent with `run_python` in `AllowedToolNames` |
| `DependencyInjection/Modules/AddNodeComputeExtensions.cs` | DI module; registers handler, gateway, options, environment; mirrors `AddNodeAgentHomeExtensions` |
| `tools/compute/pyproject.toml` | Venv dependency specification (numpy, scipy, sympy pins) |
| `tools/compute/uv.lock` | Locked venv closure (committed to repo, used by `uv sync --locked`) |

---

## 9. Related architecture

- **[Agent Mode](04-agent-mode.md)** — tool registries, `IClientLocalToolHandler`, approval policy, `AllowedToolNames`.
- **[Sandbox & Containment](../services/sandbox.md)** (not yet documented; see `Services/Sandbox/`) — process-role `ISandboxRuntimeProvider`, resource limits, network policy, attachment keys.
- **[Security & Privacy](12-security-and-privacy.md) §4** — Custom Tools, approval audit, execute-on-host guards.
- **[Training](18-training.md)** — sibling `uv`-provisioned Python runtime; shared binary-acquisition helper.
- **[ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md)** — Development Mode sandbox boundary; explains why Docker is not on the inference path.
- **[ADR 0005](../adr/0005-training-runtime-python-exclusivity-and-project-placement.md)** — Rationale for pinned-venv Python over system detection.

---

## Changes from baseline

This page documents the `run_python` compute tool added on branch `test/math-agent-eval` (2026-08-24). It is a first-class agent tool for numeric/symbolic computation, distinct from Custom Tools, gated as profile-opt-in, and routed through the process-role sandbox.
