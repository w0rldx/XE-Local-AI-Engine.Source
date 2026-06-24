# Model-fit / Model Advisor

> Last reviewed: 2026-06-24 · Code-grounded.

Model-fit is the node's **box-aware GGUF recommendation advisor**: given the operator's use-case, it profiles the local hardware (RAM / VRAM / GPU vendor), discovers candidate GGUF repos on Hugging Face, estimates each model's memory footprint with a pure I/O-free formula, ranks the ones that fit, and caches the ranked snapshot. The React page is **cache-first** — it reads the last cached snapshot and never runs the advisor inline; the only way to (re)run the advisor is to fire the seeded Quartz `model-recommendation-check` job. This page covers the hardware profiler, the memory-fit estimator, the refresh service, the cache-read query service, the GGUF download coordinator, the Quartz wiring, the local endpoints, and the React feature.

> **Discrepancy vs. older notes (CODE WINS).** Earlier docs/plans describe a "digest-pinned approved utility image" run in a container to **benchmark** models. That concept **no longer exists** — it was removed in the [runtime re-architecture](03-local-runtime-and-providers.md) (Docker is gone). The advisor now runs box-aware GGUF recommendation **in-process** against the [host llama.cpp runtime](03-local-runtime-and-providers.md). The refresh service explicitly rejects anything but `Recommend` with *"Benchmark refresh is not yet enabled."* (`ModelFitRefreshService.cs:103-106`), and the handler's parameter schema dropped the `approved-image` and `provider-name` fields (`ModelRecommendationCheckHandler.cs:37-57`). Benchmark stays **gated**.

## Where the code lives

| Concern | Project / path |
|---|---|
| Application services (advisor logic) | `XE-Local-AI-Engine.Client.Application/Services/ModelFit/` |
| Pure memory-fit estimator | `…/Services/ModelFit/Fit/MemoryFitEstimator.cs` |
| Hardware profiler (provider seam impl) | `XE-Local-AI-Engine.Providers.Capabilities/HardwareProfiler.cs` |
| Quartz handler | `XE-Local-AI-Engine.Client.Application/Services/Scheduler/Handlers/ModelRecommendationCheckHandler.cs` |
| Local endpoints | `XE-Local-AI-Engine.Client/Endpoints/ModelFit/V1/` |
| Route constants | `XE-Local-AI-Engine.Client/Endpoints/Common/LocalApiRoutes.cs:236` (`ModelFit`) |
| React feature | `XE-Local-AI-Engine.Client.React/src/features/model-fit/` |

## The two paths: cache-read vs. scheduler refresh

This is the single most important invariant of the feature. The "latest" read and the "refresh" trigger are **completely separated** so a page render is cheap and an advisor run is async/auditable.

```
 React page
   │
   ├── READ (cheap, synchronous): useLatestRecommendations()
   │      → GET model-fit/recommendations/latest?useCase=…
   │      → ModelFitQueryService.GetLatestRecommendationsAsync()  ← reads cached snapshot ONLY, never runs advisor
   │      → returns null on cache-miss → page renders empty state
   │
   └── REFRESH (async, audited): useRefreshRecommendations()
          → POST model-fit/recommendations/refresh
          → ModelFitRefreshTrigger.TriggerRecommendationRefreshAsync()  ← template-guarded facade
          → IScheduledJobManagementService.TriggerNowAsync(seeded model-recommendation-check job)
          → Quartz dispatcher → ModelRecommendationCheckHandler.ExecuteAsync()
          → ModelFitRefreshService.RefreshAsync()  ← the ONLY place the advisor actually runs
          → writes new snapshot + recommendation rows to encrypted SQLite
          → SignalR scheduler hub broadcasts terminal run → page invalidates the latest cache & refetches
```

The route comment makes the contract explicit: *"Cache-first: the latest endpoint reads the cached recommendation snapshot and never runs the advisor; the refresh endpoint delegates to the scheduler trigger and never executes the advisor directly."* (`LocalApiRoutes.cs:229-234`).

## The memory-fit estimator (pure core)

`MemoryFitEstimator` (`Fit/MemoryFitEstimator.cs`) is a **stateless, I/O-free, Singleton-safe** estimator implementing the oobabooga "GGUF VRAM formula":

```
total ≈ weights(quant) + KV_cache + ~0.75 GB runtime overhead + safety margin
KV_cache = 2 · n_layers · n_kv_heads · head_dim · ctx · bytesPerKvElement
head_dim = embedding_length / n_heads
```

Key decisions, all in code:

- **Budget selection (`Estimate`, lines 92-94).** Uses GPU VRAM iff `profile.GpuAccelAvailable && profile.VramKnown && profile.VramBytes > 0`; otherwise falls back to `profile.AvailableRamBytes`. The result records which budget it scored against via the `FitMode` enum (`Gpu` / `Cpu`). A model **fits iff `total ≤ budget`**; `HeadroomBytes = budget − estimated` (negative when it doesn't fit).
- **Weights term (`EstimateWeightsBytes`).** Prefers `paramCount × BytesPerWeight(quant)`; when the GGUF header has no param count it **falls back to the on-disk file size** (the already-quantized weights), so a file is never rejected purely for a missing param count.
- **Bytes-per-weight table (`BytesPerWeight`).** Maps llama.cpp quant labels (`Q2_K`…`Q8_0`, `F16`, `F32`) to effective bytes/weight; unknown labels fall back to the `Q4_K_M` density (~0.5625 B/weight ≈ 4.5 bits).
- **Constants.** `DefaultQuant = "Q4_K_M"`, `RuntimeOverheadBytes ≈ 0.75 GB`, `DefaultSafetyMarginFraction = 0.12` (12% applied to weights+KV before adding the fixed overhead). The KV term can be halved by passing `kvCacheQuantized: true` (8-bit instead of fp16 KV cache).

Because it is pure, every input is supplied directly by the caller — there is no GGUF parsing inside the estimator. The discovery layer supplies the header DTO.

## The hardware profiler

`HardwareProfiler` (`Providers.Capabilities/HardwareProfiler.cs`, implements `IHardwareProfiler`) produces a sanitized `HardwareProfile` aggregate (RAM / VRAM / GPU vendor / CPU cores / free disk — **no machine identifiers**). It is a Singleton with a `volatile` cached profile; `GetProfileAsync(forceRefresh, ct)` serves the cache unless `forceRefresh` is set (lock-free — the probe is read-only/idempotent).

Probing logic (`ProbeAsync`):

- **RAM** — Linux parses `/proc/meminfo` (`MemTotal` / `MemAvailable`); otherwise an OS query.
- **GPU vendor** — `nvidia-smi --query-gpu=name` first on every OS (NVIDIA is unambiguous); else Linux reads `/sys/class/drm` PCI vendor ids (NVIDIA/AMD/Intel), Windows uses a DXGI/WMI vendor-name seam.
- **VRAM** — NVIDIA via `nvidia-smi --query-gpu=memory.total` (scans past warning-banner lines to the first parseable line); Windows via a DXGI seam; **Linux non-NVIDIA has no byte-accurate source → VRAM unknown**.
- **Degrade rule (lines 69-71).** `gpuAccelAvailable = vramKnown && vendor ∈ {Nvidia, Amd, Intel}`. **VRAM unknown ⇒ no GPU budget**, even when a vendor is detected — so the estimator scores against RAM in CPU mode. This is why an AMD/Intel Linux box degrades to CPU mode in the tests.

## The refresh service (the advisor)

`ModelFitRefreshService.RefreshAsync` (`Implementation/ModelFitRefreshService.cs:96`) is the advisor proper. It is **scoped** (resolved per Quartz fire by the singleton handler through a fresh DI scope). Flow:

1. **Guard** — non-`Recommend` operations are rejected before any snapshot row exists (benchmark gate, lines 103-106).
2. **Validate** intent (`useCase`, `limit`) via `ModelFitRequestValidator` against the fixed allowlist; provider is fixed to the `"llama.cpp"` sentinel.
3. **Open** a `Running` snapshot row (`IModelFitSnapshotStore.CreateRunningAsync`) and report progress.
4. **Profile** hardware (`IHardwareProfiler.GetProfileAsync`), then `BuildRecommendationsAsync` (line 257):
   - HF discovery search for the use-case, capped, sorted by downloads, wrapped in a **20s per-call timeout** (a stalled search maps to a clean `Failed` run, not a hang).
   - List already-downloaded GGUF keys (best-effort).
   - Inspect candidate repos **in parallel with bounded concurrency** (`SemaphoreSlim`); a stalled/failing repo is skipped (null candidate) so it never fails the whole run.
   - Score each candidate with `MemoryFitEstimator`, then **rank by largest `HeadroomBytes` first**, tie-break by repo id for determinism, and `Take(request.Limit)`.
5. **Serialize** the ranked fits to advisor JSON, parse them through `RecommendationJsonParser`, and write the terminal snapshot (`Succeeded`) plus replace the recommendation rows (`IModelFitRecommendationStore.ReplaceForSnapshotAsync`).

The service **never throws out of `RefreshAsync`** — every failure path records a `Failed`/`Cancelled` snapshot and returns a contractually **sanitized** error string (no path/URL/token), which the handler re-throws as a `ScheduledJobExecutionException` so the dispatcher records an actionable Failed run.

## The query service (cache-read)

`ModelFitQueryService.GetLatestRecommendationsAsync(useCase, providerName, ct)` reads the **latest successful snapshot keyed by use-case** and projects the stored rows into a view. Behaviour confirmed by `ModelFitQueryServiceTests`:

- **Cache-miss → `null`** so the endpoint renders the empty state (no advisor run).
- The latest-successful key **includes use-case**, so a query for a different use-case is a cache-miss.
- **Install state follows the node, not the stored flag** — a stored row's `IsInstalled` is overridden by checking whether the row's pull/tag name is present in the node's installed-model list (matching the bare-vs-`:latest` tag forms). An HF-only row with no resolvable tag is treated as not-installed.

## GGUF download coordinator

A recommendation row is actionable: the operator can download the model. `GgufDownloadCoordinator` (`Implementation/GgufDownloadCoordinator.cs`, implements `IGgufDownloadCoordinator`) is a **Singleton** owning an in-memory registry of in-flight downloads:

- **`StartAsync`** resolves the canonical model name (the same way the store registers it, so track/cancel keys match the installed identity even for variant resolutions), enforces **single-flight per model name** (rejoins an existing download rather than starting a second), and runs the download on a detached task with a per-model `CancellationTokenSource`.
- **`Cancel`** signals the in-flight token (cooperative — stops at the next await/byte boundary).
- **`GetStatus`** returns the latest sanitized `GgufDownloadStatus` (phase + completed/total bytes; **never** a path/URL/token).
- On success it writes the `model_provider_map` row pointing the GGUF at the `llamacpp` provider (through a fresh DI scope, since the map store is scoped) — the **single production writer** that makes a downloaded GGUF reachable by the runtime. Best-effort: a map-write failure never marks the download Failed.

> **UI ownership note.** In the React layer the GGUF browse/download, llama.cpp runtime, HF token, and running-models hooks were **relocated** out of the model-fit feature into Model Management / Node Settings / Loaded Models. A download started from a recommendation row is owned by the Model Management feature (`useModelFit.ts:18-22`). The download/runtime/token endpoints still live physically under `Endpoints/ModelFit/V1/` (see table below) but their UI no longer renders on the advisor page.

## Quartz wiring

| Piece | File | Role |
|---|---|---|
| Handler | `ModelRecommendationCheckHandler` (template id `model-recommendation-check`) | Validates decrypted params against a draft-07 JSON schema, opens a DI scope, invokes `IModelFitRefreshService`. Owns **no** scheduler state. |
| Seeder | `ModelRecommendationScheduleSeeder` (`IHostedService`) | Idempotently seeds **one** enabled `ScheduleKind.Manual` job (durable Quartz job, **no trigger**, never auto-fires) so the React "Refresh now" button works without the operator hand-creating a schedule. **Self-healing** — re-seeds if deleted. |
| Trigger facade | `ModelFitRefreshTrigger` (`IModelFitRefreshTrigger`) | Template-guarded: rejects any job that is not a `model-recommendation-check` job, validates whitelisted per-fire overrides (`useCase` against the six-value allowlist, `limit` bounds, trimmed `quantOverride`, `ctxTarget` min) **before** firing, then delegates to `TriggerNowAsync`. |

Default seeded parameters: `{"operation":"Recommend","useCase":"coding","limit":5}` — no approved-image/provider fields. The handler also supports `Cron`/`OneShot`/`SimpleInterval` for operators who want a recurring refresh, but `Manual` (on-demand) is the recommended kind. See [Scheduler](06-scheduler.md) for the dispatcher, run-history, and SignalR hub mechanics.

## Endpoints

Routes under `model-fit/*` (`LocalApiRoutes.ModelFit`, mapped in `Endpoints/ModelFit/V1/`). The advisor-proper routes are the first three; the rest are thin transport over the llama.cpp binary/supervisor and HF GGUF seams (their UI now lives in sibling features).

| Route constant | Path | Endpoint | Notes |
|---|---|---|---|
| `RecommendationsLatest` | `model-fit/recommendations/latest` | `GetLatestRecommendationsEndpoint` | **Cache-read only.** Filtered by `useCase`. |
| `RecommendationsRefresh` | `model-fit/recommendations/refresh` | `RefreshRecommendationsEndpoint` | Fires the scheduler trigger; returns 200 immediately. |
| `HardwareProfile` | `model-fit/hardware-profile` | `GetHardwareProfileEndpoint` | Sanitized aggregates; `?refresh=true` re-probes. |
| `GgufBrowse` / `GgufInspect` | `model-fit/gguf/browse` · `…/inspect` | `BrowseGgufRepositoriesEndpoint` · `InspectGgufRepositoryEndpoint` | HF GGUF discovery + per-repo quant/size inspection. |
| `Download` / `DownloadCancel` | `model-fit/download` · `…/cancel` | `StartGgufDownloadEndpoint` · `CancelGgufDownloadEndpoint` | Background, cancellable, keyed by model name. |
| `Running` / `RunningEject` | `model-fit/running` · `…/eject` | `ListRunningModelsEndpoint` · `EjectRunningModelEndpoint` | Running llama-server processes; eject tree-kills one. |
| `LlamaCppVersion` / `LlamaCppRuntime` / `LlamaCppUpdate` | `model-fit/llamacpp/version` · `…/runtime` · `…/update` | `GetLlamaCppVersionEndpoint` · `GetLlamaCppRuntimeEndpoint` · `UpdateLlamaCppRuntimeEndpoint` (+ `EnsureLlamaCppBinaryEndpoint`) | Pinned/resolved binary version, dynamic-runtime status, operator-initiated install/update. |
| `HfToken` | `model-fit/hf-token` | `GetHfTokenStatusEndpoint` · `SetHfTokenEndpoint` | **GET reports presence only** — the token is never returned (security gate). |

All endpoints are loopback/local-only, authenticated, and secret-redacted — see [Security & privacy](12-security-and-privacy.md). They are surfaced to React through OpenAPI → hey-api; see [API & hubs](09-api-and-hubs.md) and [React client](10-react-client.md).

## React feature

`src/features/model-fit/` (page `ModelRecommendationsPage.tsx`):

- **`queries/useModelFit.ts`** — `useLatestRecommendations(filters)` (cache-read, `select` maps the optional-field generated DTO into the stricter domain view-model), `useHardwareProfile(refresh)`, and `useRefreshRecommendations()` (a mutation that fires the seeded job and invalidates the latest cache on success). Every generated `*Options()` is wrapped in `withResponseValidation` so a malformed response surfaces as an `ApiError`, never a raw `ZodError`. **All reads are cache-only.**
- **`hooks/useModelFitSchedulerEvents.ts`** — subscribes to the **shared** scheduler SignalR hub (no second hub server), reacts only to terminal runs of the `model-recommendation-check` template, invalidates the latest-recommendations cache (TanStack Query refetches canonical state) and raises a transient toast. SignalR push is primary; a one-shot REST **catch-up** fires on every (re)connect to cover the connect-race / reconnect-gap (deduped by run id). There is **no interval polling**. The effect deliberately keeps `t` and `scheduledJobId` in refs so a new translation function or a late-resolving job id never rebuilds the connection mid-negotiation (a real StrictMode race the comments call out).
- **`components/`** — `HardwareProfileCard.tsx`, `RecommendationTable.tsx`, `ModelFitFormatters.ts`; **`models/`** — domain types + mappers; **`notifications/`** — toast helpers; **`stores/`** — `ModelFitManagementStore.ts`.

## Invariants a maintainer must respect

1. **The advisor runs in exactly one place** — `ModelFitRefreshService.RefreshAsync`, reachable only via the Quartz handler. Never run it from an endpoint or the read path.
2. **`/latest` is read-only.** Returning `null` on cache-miss is contractual (empty state); do not make it trigger a run.
3. **Refresh is async and audited.** Go through `ModelFitRefreshTrigger` (template-guarded) → scheduler; never call the refresh service directly from transport.
4. **Sanitization everywhere.** Snapshot errors, download statuses, and hardware profiles are sanitized of paths/URLs/tokens and machine identifiers before they leave the node.
5. **The HF token is write-only over the wire** — GET reports presence only.
6. **Benchmark is gated** and the approved-image concept is removed — don't reintroduce a container path; inference and fit are host-llama.cpp + pure estimator.

## Related pages

- [Local runtime & providers](03-local-runtime-and-providers.md) — host llama.cpp supervisor, binary manager, HF GGUF discovery/store the advisor depends on.
- [Scheduler](06-scheduler.md) — Quartz dispatcher, run history, and the SignalR hub the refresh path rides.
- [Data & persistence](08-data-and-persistence.md) — snapshot/recommendation tables in encrypted SQLite.
- [API & hubs](09-api-and-hubs.md) — `/api/local/v1` mapping and OpenAPI → hey-api.
- [React client](10-react-client.md) — TanStack Query + SignalR conventions used by this feature.
- [Security & privacy](12-security-and-privacy.md) — local-only endpoints, secret redaction, token-presence gating.
- [Architecture overview](01-architecture-overview.md) · [Project layout](02-project-layout.md)
