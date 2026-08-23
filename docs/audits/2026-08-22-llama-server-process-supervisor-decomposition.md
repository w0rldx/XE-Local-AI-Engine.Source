# Llama server process supervisor decomposition report

**Date:** 2026-08-22
**Base:** `be342034bdeea92b5bd0976486b30cd4a7b97797`
**Scope:** read-only analysis of `LlamaServerProcessSupervisor`; no production refactor is authorized by this report.

## Verdict

Defer production changes. The supervisor is large, but its remaining responsibilities share live process state and several independent ordering mechanisms. A split based on line count would be riskier than the current implementation. Any future extraction should begin with one named lifecycle cluster, preserve the existing concurrency tests, and receive a separate plan and approval.

The safest future boundary is the liveness/health decision cluster. The spawn pipeline is the highest-risk boundary and should remain in place until its artifact resolution, candidate fallback, readiness, telemetry, and registration transitions can be characterized independently.

## Current lifecycle

The authoritative process identity is `LlamaServerProcessSupervisor.ProcessKey`: a case-insensitive model name plus `ModelRole`. Each key moves through these observable states:

1. **Absent** — no live `_processes` entry and no `_inflightSpawns` entry.
2. **Decision pending** — `EnsureRunningAsync` owns the shared runtime-mutation gate and `DecideEnsureAsync` owns the per-key `_ensureGates` semaphore.
3. **Spawning detached** — an immutable `InflightSpawn` is registered before `StartDetachedSpawn` begins. Caller cancellation abandons only `AwaitDetachedSpawnAsync`; the shared load continues under the supervisor shutdown token.
4. **Launching / waiting for readiness** — `SpawnWithRestartAsync` and `SpawnCoreAsync` resolve the model, runtime, capability manifest, launch arguments, load admission, cap slot, and port; launch; wait for readiness; capture effective context and placement; then register a `RunningProcess`.
5. **Running** — `TryReuseAsync` returns the endpoint and refreshes `LastUsedUtc`. A rate-limited liveness claim can move an unresponsive process toward teardown after consecutive failures.
6. **Evicting** — `RunningProcess.MarkEvicting` refuses new inference leases while `EjectCoreAsync` drains active leases. A cancelled or timed-out non-forced eject clears the flag and returns to Running.
7. **Detached** — `LlamaServerIdleReaper.DetachProcess` is the single removal point. It atomically wins the process-table race, retires layer-placement evidence, and releases the port reservation.
8. **Terminated** — `LlamaServerIdleReaper.KillDetachedProcess` tree-kills and disposes the child outside the admission gate.
9. **Disposed** — `LlamaServerRuntimeMutationGate.TryMarkDisposed` prevents new public operations; shutdown cancels the reaper, drains admitted operations and detached spawns, detaches all remaining processes, and disposes gates.

Additional transitions are deliberately distinct:

- A configured external endpoint short-circuits from Absent directly to an unmanaged endpoint; it never enters the process tables.
- An exited or wedged Running process is detached before the next spawn decision.
- Cap admission may detach an exited, idle, or in-window pooled-role victim, but never a live leased or profiling-pinned process.
- A forced eject marks `WasEjected` before teardown so an interrupted leaseholder can classify the failure as operator-driven.
- Exclusive profiling waits for same-model detached spawns, evicts every role for the model, spawns a transient process, pins it against the reaper for the measurement body, then always removes it.

## Shared state and synchronization

| State | Owner and synchronization | Invariant |
|---|---|---|
| `_processes` | `ConcurrentDictionary<ProcessKey, RunningProcess>` shared by the supervisor and `LlamaServerIdleReaper` | Every removal funnels through `LlamaServerIdleReaper.DetachProcess`; the reaper holds the live table, never a copy. |
| `_inflightSpawns` | Concurrent dictionary plus per-key `_ensureGates` | At most one detached spawn per key; registration in `_processes` happens before removal from `_inflightSpawns`. |
| `_ensureGates` | One `SemaphoreSlim` per key | On the ordinary ensure path it serializes only the reuse-or-spawn decision, not the readiness wait. Exclusive profiling deliberately holds the same key gate across sibling-spawn waits, its own readiness, the profiling body, and teardown. |
| Runtime mutation and teardown | `LlamaServerRuntimeMutationGate` | Shared ensure decisions exclude exclusive runtime mutation; a separate operation counter lets disposal drain whole public calls before gate disposal. |
| Loaded-process cap and ports | `LlamaServerIdleReaper._admissionGate` plus `LlamaServerPortAllocator` | The cap is the reserved-port count, so in-flight spawns count before process registration. Normal-operation allocation, failed-spawn release, and detach occur under the same gate; quiescent supervisor shutdown calls `DetachProcess` directly only after new operations are blocked and admitted work has drained. |
| GPU spawn window | `IGpuModelLoadAdmission` | GPU spawn-through-readiness is serialized with other GPU model loaders; CPU loads bypass it. |
| Launch identity conflicts | `IProcessLaunchAdmissionRegistry` and `IProcessLaunchTicket` | A conflicting or stale admission cannot launch; every transferred ticket is released on completion or failure. |
| Per-process lifecycle | `RunningProcess` uses `Interlocked`/`Volatile` for last-used time, liveness claims/failures, lease count, evicting/ejected flags, and profiling pin | Reaper and eject decisions cannot silently tear down live leased work; racing lease acquisition re-checks the evicting flag. |
| Shutdown | `_shutdownCts`, `_reaperLoop`, runtime-gate operation drain | No new operation enters after disposal latches; detached work cleans up before synchronization primitives are disposed. |

These mechanisms are complementary. Replacing them with one broad lock would change cancellation, cross-key concurrency, admission, and teardown behavior.

## Constructor collaborators

The internal constructor currently accepts 21 collaborators/configuration values. They fall into six responsibility groups:

- **Artifact and runtime resolution:** `ILlamaCppBinaryManager`, `IGpuVariantSelector`, `IGgufModelStore`, `ILlamaServerCapabilityManifestProbe`, `ILlamaCppSourceBuildActivity`.
- **Process I/O and health:** `ILlamaServerProcessLauncher`, `ILlamaServerHealthProbe`, `ILlamaFitParamsRunner`.
- **Launch policy and stored decisions:** `IInferenceProfileResolver`, `ILlamaServerLaunchPolicy`, `IProcessContextAllocationResolver`, `ILlamaServerExtraLaunchArgumentsResolver`, `LlamaServerExternalEndpointOptions`, `LlamaServerSupervisorOptions`.
- **Admission and shared resource accounting:** `IGpuModelLoadAdmission`, `IProcessLaunchAdmissionRegistry`, `ILlamaLayerPlacementReport`.
- **Evidence and diagnostics:** `ILlamaServerLoadTelemetry`, `ILogger<LlamaServerProcessSupervisor>`.
- **Execution/time seams:** `TimeProvider`, `TaskScheduler`.

The count reflects real orchestration inputs; wrapping them in a dependency bag would hide coupling without removing it.

## Existing extracted seams

The following boundaries already remove coherent responsibilities from the supervisor:

- `LlamaServerLaunchArgumentComposer` owns ordered argv composition, launch tuning, and descriptive projection helpers.
- `LlamaServerIdleReaper` owns cap admission, LRU/TTL eviction, exited-process pruning, the single detach path, port-release ordering, and tree-kill teardown.
- `LlamaServerPortAllocator` owns the reservation set and loopback bind probe. It is intentionally unsynchronized: normal operations call it under the reaper admission gate, while final supervisor shutdown releases reservations through `DetachProcess` only after global quiescence has been established.
- `LlamaServerRuntimeMutationGate` owns shared/exclusive runtime ordering, mutation activity, public-operation admission/drain, and disposal ordering.

Other important collaborator seams already keep policy and I/O testable: the process launcher, health and capability probes, launch policy, profile and extra-argument resolvers, load admission, launch-admission registry, fit-params runner, model store, placement report, and telemetry sink.

## Test coverage map

The focused Release baseline ran 178 supervisor and seam tests under the repository build lock and assembly guard. The most direct coverage is:

| Concern | Primary test classes |
|---|---|
| Detached single-flight, caller cancellation, disposal races, crash recovery | `SupervisorLifecycleTests`, `SupervisorRaceTests` |
| Restart bounds, sanitized failures, external endpoints, health aggregation | `SupervisorCrashAndSurfaceTests`, `SupervisorWedgedReuseTests` |
| Loaded cap, LRU/TTL eviction, active leases, pooled-role yielding, exited pruning | `SupervisorEvictionTests`, `SupervisorGateScopeTests` |
| GPU/CPU load admission and mutation visibility | `SupervisorAdmissionTests`, `SupervisorRaceTests`, `SupervisorProfilingTests` |
| Graceful/forced eject and lease classification | `SupervisorLifecycleTests` |
| Profile/benchmark exclusivity, sibling-role eviction, pinning, cleanup | `SupervisorProfilingTests`, `SupervisorBenchmarkReceiptTests` |
| Launch arguments, role flags, replay/explore behavior, fallbacks, LoRA, context down-tier | `SupervisorSpawnArgsTests`, `SupervisorLaunchSpecProfileTests`, `SupervisorLaunchFallbackTests`, `SupervisorLoraLaunchSpecTests`, `SupervisorStartupCaptureWindowTests` |
| Port reservation and bind collisions | `LlamaServerPortAllocatorTests` |
| Shared/exclusive gate semantics, launch-vector helpers, launch-admission tickets | `AsyncSharedExclusiveGateTests`, `LlamaServerLaunchArgumentComposerTests`, `ProcessLaunchAdmissionRegistryTests` |

`StaleLlamaServerReaperTests` and `SourceBuildRecoveryTests` cover adjacent startup/shutdown recovery components rather than the supervisor's in-process reaper and spawn state directly.

## Candidate future boundaries, ranked by risk

1. **Liveness and health decisions — lower risk.** `TryReuseAsync`, `ProbeResponsiveWithTimeoutAsync`, and `CheckHealthCoreAsync` form a narrow probe/classification cluster. A future collaborator would still need explicit commands for mark-used, failure counting, and teardown; it must not own the process table or reaper.
2. **Operator eject and inference-lease state — medium risk.** `EjectCoreAsync`, `DrainLeasesAsync`, `InferenceLease`, and the related `RunningProcess` flags are cohesive, but the lease-versus-eviction race is load-bearing. Extract only with the lifecycle and eviction suites acting as regression locks.
3. **Detached ensure coordinator — medium-high risk.** `DecideEnsureAsync`, `InflightSpawn`, `StartDetachedSpawn`, and `AwaitDetachedSpawnAsync` are a named single-flight cluster. Their publication and cancellation ordering is subtle: the process must register before the in-flight record disappears, and caller cancellation must never cancel the load.
4. **Exclusive profiling session — medium-high risk.** `RunExclusiveProfilingCoreAsync` has a coherent entry/body/cleanup lifecycle, but it composes the runtime mutation gate, per-key gate, launch-admission tickets, sibling-spawn waits, all-role eviction, `SpawnCoreAsync`, pinning, and unconditional teardown.
5. **Spawn pipeline — highest risk.** `SpawnWithRestartAsync`, `SpawnCoreAsync`, `BuildLaunchPlanCandidatesAsync`, readiness/failure classification, placement capture, telemetry, and registration are strongly coupled. An extraction here must preserve exact error markers, fallback order, port cleanup, load-admission lifetime, argument order, capture windows, and successful-candidate-only evidence. Do not begin with this boundary solely because it contains the most lines.

## Non-candidates

- Do not introduce a constructor dependency bag; it obscures the collaboration graph and weakens DI visibility.
- Do not duplicate the process table, port accounting, or `RunningProcess` lifecycle state across components.
- Do not merge the per-key ensure gate, runtime mutation gate, admission gate, GPU load admission, and launch-admission registry into one abstraction; they protect different invariants and have different lifetimes.
- Do not move pure launch-vector logic back into the supervisor; `LlamaServerLaunchArgumentComposer` is the established boundary.

Any implementation based on this report requires a new approved plan. No `LlamaServerProcessSupervisor` source or test file was changed during this phase.
