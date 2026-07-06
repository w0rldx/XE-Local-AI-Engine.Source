# Image Generation (stable-diffusion.cpp)

> Last reviewed: 2026-07-06 · Code-grounded.

The node generates images **locally** with [stable-diffusion.cpp](https://github.com/leejet/stable-diffusion.cpp). It mirrors the llama.cpp text runtime: the app self-provisions a pinned `sd-server` binary, supervises **one resident daemon per model** on a private loopback port range, and drives generation through a coordinator that serializes work to one job at a time and persists every produced image **encrypted-at-rest**. Nothing about a prompt, an image, or the daemon's HTTP shape ever leaves the node. The feature **ships enabled by default** — there is no off switch in `StableDiffusionRuntimeOptions`; the runtime is always wired.

## Where the code lives

| Concern | Project / path |
|---|---|
| Job coordinator (queue / cancel / replay) | `XE-Local-AI-Engine.Client.Application/Services/Images/Implementation/ImageJobCoordinator.cs` (`IImageJobCoordinator`) |
| Runtime orchestration boundary | `XE-Local-AI-Engine.Providers.StableDiffusionCpp/Implementation/StableDiffusionCppRuntime.cs` (`IImageRuntime`) |
| Process supervisor (spawn / reuse / evict / tree-kill) | `…/StableDiffusionCpp/Implementation/ImageServerProcessSupervisor.cs` (`IImageServerSupervisor`) |
| Binary manager (download / verify / cache) | `…/StableDiffusionCpp/Implementation/StableDiffusionCppBinaryManager.cs` |
| Stale-daemon reaper | `…/StableDiffusionCpp/Implementation/StaleImageServerReaper.cs` |
| Runtime options | `…/StableDiffusionCpp/Options/StableDiffusionRuntimeOptions.cs` |
| SignalR hub + publisher | `XE-Local-AI-Engine.Client/Hubs/ImageJobHub.cs`, `…/Hubs/ImageJobEventPublisher.cs` |
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
- **`GetAsync` / `ListAsync`** read the persisted status view (newest first).
- **`SnapshotBufferedEvents`** returns a late hub subscriber's replay log. The coordinator keeps a per-job ordered event buffer (cap 128) that lingers ~5 minutes after a terminal event so a client that connects late can catch up.

Progress is **coarse status only** — never the prompt, a path, or a step/percent — and non-terminal pushes are throttled to at most one per second per job. On success the image is persisted encrypted-at-rest **before** the job is marked `Succeeded`.

## The runtime (stable-diffusion.cpp)

`StableDiffusionCppRuntime` (`IImageRuntime`) is the orchestration boundary: it ensures a resident `sd-server` via the supervisor, submits the job and polls it over `SdServerJobClient`, maps coarse status transitions to `ImageGenProgress`, and decodes the base64 image inline on completion. **No sd-server flag, route, or HTTP shape escapes this project** (architecture invariant §3).

**Cancellation is two-mode.** When the token is signalled the runtime asks sd-server to cancel the job: a still-*queued* job cancels cleanly (HTTP 200); a job already *generating* cannot be interrupted (HTTP 409), so the runtime asks the supervisor to **tree-kill + restart** the daemon, dropping the one active job. Because the coordinator serializes to one job, a kill+restart can only ever affect that single job.

## The process supervisor

`ImageServerProcessSupervisor` (`IImageServerSupervisor`, **Singleton**, `IAsyncDisposable`) owns every resident `sd-server` child. It mirrors `LlamaServerProcessSupervisor` (reduced: no role split, no benchmark profiling, no external-endpoint attach — the image runtime is one resident daemon per model):

- **Reuse-or-spawn** one daemon per model behind a per-model single-flight gate.
- **Readiness-gate** on start by polling `/sdcpp/v1/capabilities`; the daemon binds its socket only after synchronous model load, so a readiness timeout (`ReadinessTimeout`, default 2 min) is a load failure.
- **Loopback port allocation** with collision-retry from the range **18200–18299** — distinct from the llama.cpp range (18100–18199) so the two runtimes never contend for a port.
- **Idle-TTL eviction** via a background reaper (`IdleTimeToLive`, default 15 min) to free VRAM; a reuse-path liveness probe (throttled, timeout-bounded) tears down and respawns a wedged daemon after `MaxReuseLivenessFailures` (default 3) consecutive failures.
- **Per-OS tree-kill teardown** on eviction, abort, and shutdown (Linux process group / Windows Job Object), so no `sd-server` survives a supervisor stop.
- `MaxLoadedProcesses` defaults to **1** — sd-server is VRAM-heavy and typically co-resident with a chat model, so a spawn for a new model evicts an idle LRU daemon first.

Like the text runtime, a `StaleImageServerReaper` runs at startup to reap `sd-server` orphans left by a previous run of **this** app — matched strictly against the app's own binaries root (`{LocalApplicationData}/XE-Local-AI-Engine/stable-diffusion.cpp`) so an unrelated install is never touched. See [Local Runtime & Providers](03-local-runtime-and-providers.md) for the shared supervisor pattern and [Hosting & Deployment](11-hosting-and-deployment.md) for process reaping.

## Binary provisioning

`StableDiffusionCppBinaryManager` downloads a **pinned** `sd-server` release, verifies its integrity, extracts it into the per-user cache directory, and returns the resolved path. It **never source-builds**. Cache layout mirrors llama.cpp: `{cacheRoot}/stable-diffusion.cpp/{tag}/{backend}/`, where `cacheRoot` is `{LocalApplicationData}/XE-Local-AI-Engine`. Because acquisition is on-demand, a fresh node downloads the image runtime the first time a generation is requested.

## Endpoints

Routes under `images/*`, one endpoint class per file in `Endpoints/Images/V1/`:

| Endpoint | Role |
|---|---|
| `CreateImageJobEndpoint` | Enqueue a new generation job (prompt, negative prompt, width/height/steps/sampler). Returns the job id. |
| `GetImageJobEndpoint` | One job's current status view. |
| `ListImageJobsEndpoint` | All persisted jobs, newest first. |
| `CancelImageJobEndpoint` | Request cancellation of a tracked job. |
| `RetrieveImageEndpoint` | Fetch the produced image bytes for a succeeded job (decrypted on read). |
| `ListImageModelsEndpoint` | Installed image models available to the runtime. |
| `StartImageModelDownloadEndpoint` | Begin downloading an image model. |

All endpoints are loopback/local-only, operator-authenticated, and secret-redacted — see [Security & Privacy](12-security-and-privacy.md). They are surfaced to React via OpenAPI → hey-api; see [API & Hubs](09-api-and-hubs.md).

> **Known RC limitation.** Image-model download has **no progress or cancel** yet (`StartImageModelDownloadEndpoint`); a large model download runs to completion silently. Tracked as audit finding P2-2.

## React feature

`src/features/images/` (`pages/`, `hooks/`, `queries/`) renders the generation form, the job list, and the produced images. It follows the standard client conventions: TanStack Query for server state, a SignalR hub (`useImageJobHub`) that **invalidates** the matching query on each pushed job event (notification-only; the query refetches canonical state). See [React Client](10-react-client.md).

## Invariants a maintainer must respect

1. **Generation is serialized to one job.** The single-slot semaphore is what makes a kill+restart cancel safe — never widen it without redesigning cancellation.
2. **The image is persisted before the job is marked succeeded**, encrypted-at-rest.
3. **No sd-server flag/route/HTTP shape escapes `Providers.StableDiffusionCpp`** (architecture invariant §3).
4. **Progress is coarse status only** — never the prompt, a path, or a step/percent (privacy §10).
5. **The sd-server port range (18200–18299) is disjoint from llama.cpp's (18100–18199)** — keep them from ever colliding.

## Related pages

- [Local Runtime & Providers](03-local-runtime-and-providers.md) — the llama.cpp supervisor this runtime mirrors, binary provisioning, process reaping.
- [Chat](05-chat.md) — the text runtime that typically co-resides with the image daemon.
- [Data & Persistence](08-data-and-persistence.md) — the `image_jobs` table and encrypted image store.
- [API & Hubs](09-api-and-hubs.md) — `/api/local/v1` mapping, SignalR hubs, OpenAPI → hey-api.
- [React Client](10-react-client.md) — TanStack Query + SignalR conventions used by this feature.
- [Security & Privacy](12-security-and-privacy.md) — local-only endpoints, secret redaction, node-local privacy.
- [Architecture Overview](01-architecture-overview.md) · [Project Layout](02-project-layout.md)
