# Image Generation (stable-diffusion.cpp)

> Reviewed: 2026-09-15 · Code-grounded.

The node generates images **locally** with [stable-diffusion.cpp](https://github.com/leejet/stable-diffusion.cpp). It mirrors the llama.cpp text runtime: the app resolves either a pinned prebuilt or a managed source-built `sd-server`, supervises **one resident daemon per model** on a private loopback port range, and drives generation through a coordinator that serializes work to one job at a time and persists every produced image **encrypted-at-rest**. Nothing about a prompt, an image, or the daemon's HTTP shape ever leaves the node. The feature **ships enabled by default** — there is no off switch in `StableDiffusionRuntimeOptions`; the runtime is always wired.

## Where the code lives

| Concern | Project / path |
|---|---|
| Job coordinator (queue / cancel / replay) | `XE-Local-AI-Engine.Client.Application/Services/Images/Implementation/ImageJobCoordinator.cs` (`IImageJobCoordinator`) |
| Runtime orchestration boundary | `XE-Local-AI-Engine.Providers.StableDiffusionCpp/Implementation/StableDiffusionCppRuntime.cs` (`IImageRuntime`) |
| Process supervisor (spawn / reuse / evict / tree-kill) | `…/StableDiffusionCpp/Implementation/ImageServerProcessSupervisor.cs` (`IImageServerSupervisor`) |
| Binary manager (managed selection or download / verify / cache) | `…/StableDiffusionCpp/Implementation/StableDiffusionCppBinaryManager.cs` |
| Managed source build (probe / fetch / build / adopt / recover) | `…/StableDiffusionCpp/Implementation/StableDiffusionCppSourceBuildService.cs` |
| Installed-runtime state + mutation gate | `…/StableDiffusionCpp/Implementation/StableDiffusionInstalledRuntimeStore.cs`, `…/StableDiffusionCpp/Implementation/ImageRuntimeActivityGate.cs` (`Contracts/IImageRuntimeActivityGate.cs`) |
| Stale-daemon reaper | `…/StableDiffusionCpp/Implementation/StaleImageServerReaper.cs` |
| Runtime endpoints' door onto the provider (endpoint-dependency rule) | `XE-Local-AI-Engine.Client.Application/Services/Images/ImageRuntimeOrchestrationService.cs` — pass-through over `IStableDiffusionCppSourceBuildService`, `IStableDiffusionCppSourceBuildPrerequisiteProbe`, `IStableDiffusionInstalledRuntimeStore`, `IImageRuntimeActivityGate` and `IImageServerSupervisor`; the eight runtime/source-build/job endpoints inject it, never the provider contracts |
| Runtime options | `…/StableDiffusionCpp/Options/StableDiffusionRuntimeOptions.cs` |
| SignalR hubs + publishers | `XE-Local-AI-Engine.Client/Hubs/ImageJobHub.cs`, `…/ImageJobEventPublisher.cs`, `…/StableDiffusionCppSourceBuildHub.cs`, `…/StableDiffusionCppSourceBuildEventPublisher.cs` |
| Local endpoints | `XE-Local-AI-Engine.Client/Endpoints/Images/V1/` |
| React feature | `XE-Local-AI-Engine.Client.React/src/features/images/` |

## Architecture at a glance

```
Operator (React) ──REST /api/local/v1/images/* ──▶ FastEndpoints
                                                        │
                                       IImageJobCoordinator  (Singleton)
                                       · persist Queued job row (image_jobs)
                                       · mint per-job CancellationTokenSource
                                       · run detached through a single-slot semaphore
                                                        │
                                          IImageRuntime (StableDiffusionCppRuntime)
                                          · EnsureRunningAsync(model) → daemon endpoint
                                          · submit + poll job over SdServerJobClient
                                                        │
   IImageServerSupervisor ──spawn/reuse──▶ sd-server child (loopback 127.0.0.1:18200–18299)
                                                        │
   IGeneratedImageStore (encrypted at rest) ◀── decoded image, persisted BEFORE job marked Succeeded
                                                        │
   ImageJobHub (IHubContext) ──push──▶ React useImageJobHub → invalidate TanStack Query
```

## The job coordinator (serialized, singleton)

`ImageJobCoordinator` (`IImageJobCoordinator`, **Singleton**) is modelled on the GGUF download coordinator: a per-job in-flight `CancellationTokenSource` registry, throttled coarse status push, and detached run tasks. The one invariant that shapes everything: **generation is serialized to at most one running job** through a single-slot `SemaphoreSlim`. Extra jobs wait in their run task holding `ImageJobStatus.Queued` and are **never submitted to the runtime** until the slot frees. This bounds the blast radius of a cancel-that-must-kill the daemon to exactly one job.

- **`EnqueueAsync`** persists a `Queued` job to `image_jobs`, mints its token, kicks the serialized worker, and returns the job id. Generation runs **detached** after the call returns (the registry is Singleton so it outlives the request).
- **`CancelAsync`** signals the tracked job's token: a still-queued job is dropped to `Cancelled` **without ever calling the runtime**; a generating job's token is cancelled so the runtime performs the queued-cancel or kill+restart. Returns `false` for an unknown or already-terminal job.
- **`GetAsync` / `ListAsync`** read the persisted status view (newest first); `ListAsync` is paged (`limit`/`offset`) and reports the unpaged total.
- **`DeleteAsync`** removes a terminal job with its images — see [Deleting a job](#deleting-a-job).
- **`SnapshotBufferedEvents`** returns a late hub subscriber's replay log. The coordinator keeps a per-job ordered event buffer (cap 128) that lingers ~5 minutes after a terminal event so a client that connects late can catch up.

Progress carries the coarse status plus the runtime's generation timeline (phase, step counters, seconds per iteration, estimated remaining) — **never the prompt and never a path**. Job state is persisted to `image_jobs` through a fresh DI scope per operation.

Which pushes go out, and which are buffered, follows one rule. A **milestone** — the initial push, every terminal push, and every generation-phase transition — always goes out and is always buffered, so an operator-visible phase change is never delayed and never dropped. A **step tick inside the current phase** is throttled to at most one per second per job (a fast GPU samples several steps per second) and is never buffered: at a couple of pushes a second a minute-long job would evict its own opening events off the front of the 128-entry log, and a late subscriber would replay stale step counters and no phase transitions at all. A cadence-driven sweep evicts expired replay logs, so the last jobs' logs do not linger on an idle node waiting for another job to arrive.

On success the image is persisted encrypted-at-rest **before** the job is marked `Succeeded`.

## The runtime (stable-diffusion.cpp)

`StableDiffusionCppRuntime` (`IImageRuntime`) is the orchestration boundary: it ensures a resident `sd-server` via the supervisor, submits the job and polls it over `SdServerJobClient`, maps coarse status transitions to `ImageGenProgress`, and decodes the base64 image inline on completion. **No sd-server flag, route, or HTTP shape escapes this project** (architecture invariant §3).

**Cancellation is two-mode.** When the token is signalled the runtime asks sd-server to cancel the job: a still-*queued* job cancels cleanly (HTTP 200); a job already *generating* cannot be interrupted (HTTP 409), so the runtime asks the supervisor to **tree-kill + restart** the daemon, dropping the one active job. Because the coordinator serializes to one job, a kill+restart can only ever affect that single job.

The process-wide activity gate (`IImageRuntimeActivityGate` → `ImageRuntimeActivityGate`) also serializes runtime mutation against generation, spawn/readiness, and resident-daemon eviction. A source build or removal returns `409 runtime-busy` while one of those leases is active. Eject the resident image runtime first, then build/remove.

## The process supervisor

`ImageServerProcessSupervisor` (`IImageServerSupervisor`, **Singleton**, `IAsyncDisposable`) owns every resident `sd-server` child. It mirrors `LlamaServerProcessSupervisor` (reduced: no role split, no benchmark profiling, no external-endpoint attach — the image runtime is one resident daemon per model):

- **Reuse-or-spawn** one daemon per model behind a per-model single-flight gate.
- **Readiness-gate** on start by polling `/sdcpp/v1/capabilities`; the daemon binds its socket only after synchronous model load, so a readiness timeout (`ReadinessTimeout`, default 2 min) is a load failure.
- **Loopback port allocation** with collision-retry from the range **18200–18299** — distinct from the llama.cpp range (18100–18199) so the two runtimes never contend for a port.
- **Idle-TTL eviction** via a background reaper (`IdleTimeToLive`, default 15 min) to free VRAM; a reuse-path liveness probe (throttled, timeout-bounded) tears down and respawns a wedged daemon after `MaxReuseLivenessFailures` (default 3) consecutive failures.
- **Per-OS tree-kill teardown** on eviction, abort, and shutdown (Linux process group / Windows Job Object), so no `sd-server` survives a supervisor stop.
- `MaxLoadedProcesses` defaults to **1** — sd-server is VRAM-heavy and typically co-resident with a chat model, so a spawn for a new model evicts an idle LRU daemon first.

Like the text runtime, a `StaleImageServerReaper` runs at startup to reap `sd-server` orphans left by a previous run of **this** app — matched strictly against the app's own binaries root (`{LocalApplicationData}/XE-Local-AI-Engine/stable-diffusion.cpp`) so an unrelated install is never touched. See [Local Runtime & Providers](03-local-runtime-and-providers.md) for the shared supervisor pattern and [Hosting & Deployment](11-hosting-and-deployment.md) for process reaping.

## Binary provisioning and managed source builds

`StableDiffusionCppBinaryManager` first checks the authoritative installed-runtime record. An active managed runtime is revalidated by backend, path, permissions, and SHA256 before use. Drift tombstones the record and fails closed: the manager does **not** silently fall back to a different prebuilt while the operator-selected managed runtime is invalid. Node Settings exposes eject/remove recovery for that state even when Development Mode is disabled.

Without a managed selection, the manager downloads the **exact pinned prebuilt for the selected backend**, verifies its integrity, extracts it into the per-user cache, and returns its path. A missing GPU prebuilt is not silently replaced by a CPU asset; the selector must explicitly choose CPU. This matters on Linux NVIDIA hosts: the default prebuilt path remains Vulkan when a Vulkan device enumerates, otherwise CPU, because upstream ships no Linux CUDA prebuilt.

On Linux, Development Mode can build CPU, Vulkan, or CUDA from source. The operator can select the engine-pinned official revision, a custom GitHub repository's default branch, or an explicit 40-hex commit with the custom-source risk acknowledgement. Git and CMake run with an allowlisted environment, isolated `HOME`/`TMPDIR`, disabled credential prompts/config rewriting, and shallow fetch-by-SHA. The result is smoke-tested, hash-recorded, permission-hardened, and adopted through a crash-recoverable journal before it becomes active.

Cache/state layout, where `cacheRoot` is `{LocalApplicationData}/XE-Local-AI-Engine`:

```text
stable-diffusion.cpp/
├── {tag}/{backend}/                       # downloaded, hash-verified prebuilt
├── managed/{backend}/{resolvedCommit}/    # adopted source-build tree
├── source-build/
│   ├── .work/                             # disposable isolated build workspace
│   └── adoption-journal.json              # present only while adoption needs recovery
├── installed-runtime.json                 # authoritative active/invalid managed record
└── desired-runtime.json                   # redundant fail-closed recovery intent
```

## Endpoints

Routes under `images/*` (`LocalApiRoutes.Images`), one endpoint class per file in `Endpoints/Images/V1/` — `ls` that folder for the current set:

| Endpoint | Route | Role |
|---|---|---|
| `CreateImageJobEndpoint` | `POST images/jobs` | Enqueue a new generation job (prompt, negative prompt, width/height/steps/sampler). Returns the job id. |
| `ListImageJobsEndpoint` | `GET images/jobs` | One page of persisted jobs, newest first (`limit`/`offset`, with the unpaged `totalCount`). |
| `GetImageJobEndpoint` | `GET images/jobs/{jobId}` | One job's current status view. |
| `CancelImageJobEndpoint` | `POST images/jobs/{jobId}/cancel` | Request cancellation of a tracked job. |
| `DeleteImageJobEndpoint` | `DELETE images/jobs/{jobId}` | Delete a terminal job with its image(s) — rows and encrypted blobs. 409 while the job is still queued or generating. |
| `RetrieveImageEndpoint` | `GET images/{imageId}` | Fetch the produced image bytes for a succeeded job (decrypted on read). |
| `ListImageModelsEndpoint` | `GET images/models` | Installed image models available to the runtime. |
| `DeleteImageModelEndpoint` | `DELETE images/models/{modelName}` | Remove an installed image model. |
| `StartImageModelDownloadEndpoint` | `POST images/models/downloads` | Begin downloading an image model. |
| `ListImageModelDownloadsEndpoint` | `GET images/models/downloads` | Every tracked download — in flight and recently finished — with phase, completed/total bytes, part index/count and a sanitized failure reason. This is how a failed weight download becomes visible instead of a model that never appears. Mirrors `GET model-fit/gguf/downloads`; no path, URL or token is returned. |
| `CancelImageModelDownloadEndpoint` | `POST images/models/downloads/cancel` | Cancel a tracked download. |
| `GetImageModelCatalogEndpoint` | `GET images/models/catalog` | The curated image-model catalog. |
| `BrowseImageRepositoriesEndpoint` / `InspectImageRepositoryEndpoint` | `GET images/models/browse` · `…/inspect` | Hugging Face image-repository discovery and per-repo file inspection. |
| `GetImageRuntimeStatusEndpoint` | `GET images/runtime` | Inspect managed-runtime validity and the process-wide activity gate. |
| `EjectImageRuntimeEndpoint` | `POST images/runtime/eject` | Evict resident `sd-server` processes before a build/remove mutation. |
| `GetStableDiffusionCppSourceBuildPrerequisitesEndpoint` / `GetStableDiffusionCppSourceBuildStatusEndpoint` | `GET images/runtime/source-build/prerequisites` · `…/status` | Probe Linux build prerequisites for the selected backend, and hydrate build status (the hub pushes the rest). |
| `Start`/`Cancel`/`RemoveStableDiffusionCppSourceBuildEndpoint` | `POST images/runtime/source-build(/cancel\|remove)` | Manage the source-build lifecycle and installed runtime. |

All endpoints are loopback/local-only, operator-authenticated, and secret-redacted — see [Security & Privacy](12-security-and-privacy.md). They are surfaced to React via OpenAPI → hey-api; see [API & Hubs](09-api-and-hubs.md).

> **Download progress is polled, not pushed.** Unlike generation jobs, image-model downloads have no hub: `GET images/models/downloads` is the progress surface, and the React model manager polls it while a download is pending. Byte counts plus part index/count are reported, so a multi-part weight download is legible; cancellation goes through `POST images/models/downloads/cancel`.

## Restart and recovery

`DisposeAsync` is the graceful path the DI container prefers: it cancels every in-flight job, then drains the run tasks for a short bound so they can persist their terminal state (`Cancelled`) before the process exits.

Anything that outlives that drain — a hard crash, a kill, a drain timeout — is terminalized by `ImageJobStartupReconciler` on the next boot. The coordinator's in-memory registry does not survive a restart, so without it a row left `Queued` or `Generating` would never be transitioned again and would show as stuck forever. The reconciler marks them `Failed` with a content-free reason (`ImageJobStartupReconciler.InterruptedReason` — never the prompt or a path) and pushes a status event so a connected UI updates.

**Interrupted jobs are never auto-retried.** Image generation is expensive and nondeterministic, so the operator resubmits explicitly. This mirrors the scheduler's stale-run reconciliation in `Program`.

The ordering that makes the pass race-free: migrations are applied in `Program` before the host runs; hosted services then start in registration order; and the web host (Kestrel) starts after all of them. Since the create-job endpoint is the only production enqueue path, reconciliation always completes before a new job could race it.

## Model fit for a diffusion set

`ImageModelFitEstimator` scores a file-set against the host's memory budget before an operator commits to a multi-gigabyte install.

It is deliberately **not** `MemoryFitEstimator.Estimate`. That estimator's whole model is a transformer LLM's: it needs block counts, attention head counts, an embedding length and a llama.cpp quant-byte table to size a KV cache. A diffusion transformer has no KV cache, and a GGUF diffusion file exposes none of those fields, so feeding it here would produce a confident number with nothing behind it. What genuinely reuses is the **hardware probe**: the image estimator shares `MemoryFitEstimator.ResolveFitBudgetBytes`, so an image verdict is scored against the identical budget the LLM advisor uses and cannot drift from it.

**Only the diffusion part is a VRAM cost.** `ImageServerArgumentBuilder.BuildBackendSpec` pins the text encoder and VAE to the CPU on every GPU backend (`diffusion=cuda0,te=cpu,vae=cpu`), so charging an 18 GB Qwen-Image set's full weight against VRAM would reject a set that runs fine. In CPU mode there is no such split and the whole set is resident in RAM.

A set is called a comfortable `Fits` below 80% of the budget rather than tight; the remaining headroom absorbs the runtime's own allocations and the working buffers a diffusion step needs beyond the weights. `Unknown` is a first-class verdict, not a soft "probably fine": `HardwareProfiler` leaves VRAM unmeasured on every non-NVIDIA GPU (and on NVIDIA without `nvidia-smi`), there is no budget to score against, and the CPU budget is the wrong one because the box would run on the GPU.

## Deleting a job

Nothing is deleted until an operator asks. `IImageJobCoordinator.DeleteAsync` is the whole path. The node connection enforces foreign keys, so the `ON DELETE CASCADE` declared on `generated_images` does fire; the explicit ordered delete in `ImageJobStore.DeleteAsync` stays because it also collects the storage paths the blob teardown needs, which no cascade can do.

1. **Refuse a job that is not terminal.** A `Queued` or `Generating` job answers **409** with an `outcome` member of `NotTerminal`; cancel it first (`POST images/jobs/{jobId}/cancel`). The node refuses rather than cancelling on the operator's behalf — the same posture [benchmark project delete](20-benchmarks.md) takes for an active run. Terminal is a one-way door, so reading the status and then deleting needs no lock.
2. **Delete the rows in one transaction** (`IImageJobStore.DeleteAsync`): the job's `generated_images` rows first, then the `image_jobs` row. It hands back the `storage_path` of every blob it unreferenced, or `null` when the job never existed (→ **404**).
3. **Unlink the blobs best-effort** (`IGeneratedImageStore.RemoveJobBlobs`), then the job's now-empty directory. Each path must first resolve **under the image blob root** — the stored `storage_path` is server-computed, but the deletion boundary enforces that itself rather than trusting the column, so a legacy or hand-edited row can never unlink a file the feature does not own; one that escapes is skipped with a warning. A file that cannot be removed is logged and left behind: the rows are already gone, and no sweep will revisit it — an orphaned blob is the accepted cost of never resurrecting a job an operator deleted.

The job's replay log is dropped with it, so a late hub subscriber replays nothing for a job that no longer exists. There is no retention cap and no bulk purge: growth is bounded by the operator, not by a policy.

## React feature

`src/features/images/` (`pages/`, `hooks/`, `queries/`) renders the generation form, the job list, and the produced images. It follows the standard client conventions: TanStack Query for server state, a SignalR hub (`useImageJobHub`) that **invalidates** the matching query on each pushed job event (notification-only; the query refetches canonical state). See [React Client](10-react-client.md).

## Invariants a maintainer must respect

1. **Generation is serialized to one job.** The single-slot semaphore is what makes a kill+restart cancel safe — never widen it without redesigning cancellation.
2. **The image is persisted before the job is marked succeeded**, encrypted-at-rest.
3. **No sd-server flag/route/HTTP shape escapes `Providers.StableDiffusionCpp`** (architecture invariant §3).
4. **Progress never carries the prompt or a path** (privacy §10). The coarse status and the generation timeline (phase, step counters, rate, estimate) are what a push may contain.
5. **The sd-server port range (18200–18299) is disjoint from llama.cpp's (18100–18199)** — keep them from ever colliding.
6. **Managed runtime records are authoritative and fail closed.** Never fall back to another binary after drift without an explicit operator remove/repair.
7. **Eject before build/remove.** Runtime mutation must not race active jobs, spawn/readiness, or a resident daemon.
8. **Nothing outside the image blob root is ever unlinked.** `RemoveJobBlobs` proves containment for every recorded path and for the job directory before deleting either.
9. **Delete rows before blobs, children before parents.** The declared cascade would remove `generated_images` on its own, but `ImageJobStore.DeleteAsync` deletes them explicitly in the same transaction because it must read their storage paths first — the blob teardown has nothing to unlink once the rows are gone.

## Related pages

- [Local Runtime & Providers](03-local-runtime-and-providers.md) — the llama.cpp supervisor this runtime mirrors, binary provisioning, process reaping.
- [Chat](05-chat.md) — the text runtime that typically co-resides with the image daemon.
- [Data & Persistence](08-data-and-persistence.md) — the `image_jobs` table and encrypted image store.
- [API & Hubs](09-api-and-hubs.md) — `/api/local/v1` mapping, SignalR hubs, OpenAPI → hey-api.
- [React Client](10-react-client.md) — TanStack Query + SignalR conventions used by this feature.
- [Security & Privacy](12-security-and-privacy.md) — local-only endpoints, secret redaction, node-local privacy.
- [Architecture Overview](01-architecture-overview.md) · [Project Layout](02-project-layout.md)
