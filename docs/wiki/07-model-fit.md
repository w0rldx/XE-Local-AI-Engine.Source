# Model-fit / Model Advisor

> Baseline: `65de769ded3eb6e7b59eabb5daf6a8d0b89531ba` · Reviewed: 2026-08-17 · Code-grounded.

Model-fit is the node's **box-aware GGUF recommendation advisor**: given the operator's use-case, it profiles the local hardware (RAM / VRAM / GPU vendor), discovers candidate GGUF repos on Hugging Face, estimates each model's memory footprint with a pure I/O-free formula, ranks the ones that fit, and caches the ranked snapshot. The React page is **cache-first** — it reads the last cached snapshot and never runs the advisor inline; the only way to (re)run the advisor is to fire the seeded Quartz `model-recommendation-check` job. This page covers the hardware profiler, the memory-fit estimator, the refresh service, the cache-read query service, the GGUF download coordinator, the Quartz wiring, the local endpoints, and the React feature.

> **Discrepancy vs. older notes (CODE WINS).** Earlier docs/plans describe a "digest-pinned approved utility image" run in a container to **benchmark** models. That concept **no longer exists** — it was removed in the [runtime re-architecture](03-local-runtime-and-providers.md), which took Docker off the model path entirely, and the orphaned `approved_utility_images` table was dropped by migration. ([ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md) later permitted Docker for **Development Mode execution only**; it does not reopen a container path here.) The advisor now runs box-aware GGUF recommendation **in-process** against the [host llama.cpp runtime](03-local-runtime-and-providers.md). The **advisor refresh** still has no benchmark mode: it rejects anything but `Recommend` with *"Benchmark refresh is not yet enabled."* (`ModelFitRefreshService.RefreshAsync` — grep the message rather than a line number; it has already drifted once), and the handler's parameter schema dropped the `approved-image` and `provider-name` fields (`ModelRecommendationCheckHandler.cs`). A *separate* real benchmark now exists — the [Inference Optimizer](#inference-optimizer-operator-surface) replays a candidate profile under a metrics-enabled `llama-server` against a golden transcript — but it tunes **launch arguments for an already-chosen model**, not the advisor's model ranking. The two are distinct: the recommendation advisor stays estimator-only and **never** spawns a process.

## Where the code lives

| Concern | Project / path |
|---|---|
| Application services (advisor logic) | `XE-Local-AI-Engine.Client.Application/Services/ModelFit/` |
| Pure memory-fit estimator | `…/Services/ModelFit/Fit/MemoryFitEstimator.cs` |
| Quant ladder + quality tier (single source of truth) | `XE-Local-AI-Engine.Providers.Abstractions/Gguf/QuantLadder.cs` · `GgufQuantQuality.cs` |
| GGUF variant recommender (quant picker) | `…/Services/ModelFit/Gguf/GgufVariantRecommender.cs` (`IGgufVariantRecommender`) |
| Inference Optimizer orchestrator | `…/Services/Inference/InferenceProfileService.cs` (`IInferenceProfileService`) |
| Hardware profiler (provider seam impl) | `XE-Local-AI-Engine.Providers.Capabilities/Implementation/HardwareProfiler.cs` |
| Process VRAM-budget probe | `XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaListDevicesProcessVramBudgetProbe.cs` (`IProcessVramBudgetProbe`) |
| Quartz handler | `XE-Local-AI-Engine.Client.Application/Services/Scheduler/Handlers/ModelRecommendationCheckHandler.cs` |
| Local endpoints | `XE-Local-AI-Engine.Client/Endpoints/ModelFit/V1/` |
| Route constants | `XE-Local-AI-Engine.Client/Endpoints/Common/LocalApiRoutes.cs` (`ModelFit`) |
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
          → writes new snapshot + recommendation rows to SQLite
          → SignalR scheduler hub broadcasts terminal run → page invalidates the latest cache & refetches
```

The route comment makes the contract explicit: *"Cache-first: the latest endpoint reads the cached recommendation snapshot and never runs the advisor; the refresh endpoint delegates to the scheduler trigger and never executes the advisor directly."* (`LocalApiRoutes.cs`).

## The memory-fit estimator (pure core)

`MemoryFitEstimator` (`Fit/MemoryFitEstimator.cs`) is a **stateless, I/O-free, Singleton-safe** estimator implementing the oobabooga "GGUF VRAM formula":

```
total ≈ weights(quant) + KV_cache + ~0.75 GB runtime overhead + safety margin
KV_cache = 2 · n_layers · n_kv_heads · head_dim · ctx · bytesPerKvElement
head_dim = embedding_length / n_heads
```

Key decisions, all in code:

- **Budget selection (`MemoryFitEstimator.Estimate`).** Uses GPU VRAM iff `profile.GpuAccelAvailable && profile.VramKnown && profile.VramBytes > 0`; otherwise falls back to `profile.AvailableRamBytes`. The result records which budget it scored against via the `FitMode` enum (`Gpu` / `Cpu`). A model **fits iff `total ≤ budget`**; `HeadroomBytes = budget − estimated` (negative when it doesn't fit).
- **Weights term (`EstimateWeightsBytes`).** Prefers `paramCount × BytesPerWeight(quant)`; when the GGUF header has no param count it **falls back to the on-disk file size** (the already-quantized weights), so a file is never rejected purely for a missing param count.
- **Bytes-per-weight table (`BytesPerWeight`).** Maps llama.cpp quant labels (`Q2_K`…`Q8_0`, the `IQ1`…`IQ4` I-quants, `F16`, `F32`) to effective bytes/weight; unknown labels fall back to the `Q4_K_M` density (~0.5625 B/weight ≈ 4.5 bits).
- **Constants.** `DefaultQuant = "Q4_K_M"`, `RuntimeOverheadBytes ≈ 0.75 GB`, `DefaultSafetyMarginFraction = 0.12` (12% applied to weights+KV before adding the fixed overhead). The KV term can be halved by passing `kvCacheQuantized: true` (8-bit instead of fp16 KV cache).

Because it is pure, every input is supplied directly by the caller — there is no GGUF parsing inside the estimator. The discovery layer supplies the header DTO.

## The quant ladder & quality tiers

`QuantLadder` (`Providers.Abstractions/Gguf/QuantLadder.cs`) is the **single source of truth** for GGUF quant quality — a curated llama.cpp quant ladder ordered best→worst. Each rung carries **both** a fine-grained `QualityRank` (the array index; rank 0 = best) **and** a coarse `GgufQuantTier` grade. Two consumers read different facets of the one table, so the quant knowledge is defined once:

- the **advisor** (memory-fit) walks the fine `QualityRank` down to `DefaultFloorQuant` (**`Q3_K_M`**, the locked product floor — below it a model is *dropped* rather than offered at degrading quality) to pick the highest quant that fits;
- the **download picker** reads `TierOf` for the per-row badge.

Quality is deliberately **not** a strict function of bytes-per-weight: an I-quant can beat a same-bit K-quant, so the order is a curated quality ranking and `MemoryFitEstimator` supplies the size term separately. Unsloth Dynamic (`UD-`) tokens are priced off their stripped base; an unknown/off-ladder label ranks just below `Q4_K_M` (matching the estimator's 4.5 bpw fallback). `GgufQuantQuality.Classify` (same folder) is the total, never-throwing coarse classifier: it delegates the core tokens to `QuantLadder.TierOf` and adds the off-ladder aliases (the `_L` variants, float aliases) plus a family fallback, defaulting an unrecognized token to the **safe middle** `GgufQuantTier.Balanced`. The `GgufQuantTier` enum is an ordered rank — `Minimal(0) < Small(1) < Balanced(2) < SweetSpot(3) < NearLossless(4)` — so tiers compare directly for "pick the best tier" logic. (`QuantLadder` and `GgufQuantQuality` moved into `Providers.Abstractions/Gguf/` so the runtime and the advisor share one table; see [03-local-runtime-and-providers.md](03-local-runtime-and-providers.md).)

## GGUF variant recommender (the quant picker)

When the operator inspects a repo's selectable GGUF files, the picker no longer blindly leads with the smallest file. `GgufVariantRecommender` (`Services/ModelFit/Gguf/GgufVariantRecommender.cs`, `IGgufVariantRecommender`, **singleton**) annotates each file and flags **exactly one ★ recommended** variant that the UI selects by default. It is wired into `InspectGgufRepositoryEndpoint` — `_recommender.AnnotateAsync(detail.Files, ct)` runs on every `model-fit/gguf/inspect` call and the annotations ride the response.

`AnnotateAsync` produces one `GgufVariantAnnotation(fileName, tier, verdict, isRecommended)` per file:

- **Quality tier** — `GgufQuantQuality.Classify(file.Quant)` (the ladder above).
- **Hardware-fit verdict** — `GgufFitVerdict` (`Unknown` / `WontFit` / `Tight` / `Fits`). It resolves the active backend exactly as the inference profiler does (`IGpuVariantSelector` → `InferenceBackends.FromVariant`), probes the **process-local VRAM budget once** via `IProcessVramBudgetProbe` (`LlamaListDevicesProcessVramBudgetProbe`, [page 03 §2.5](03-local-runtime-and-providers.md#25-inference-profiles--per-machine-tuning)) — note the name is load-bearing: on WDDM/Windows `--list-devices` reports the *calling process's residency budget*, not system-wide free VRAM, so anything needing global admission semantics reads `HardwareProfile.AvailableVramBytes` instead — then compares on-disk size + a runtime-headroom margin (`max(15% of size, ~1 GiB)` — the header-free fast path approximates the unmeasured KV/overhead) against free VRAM. A missing GPU/probe degrades every verdict to `Unknown`; the picker **never 500s** over absent hardware.
- **Recommended pick** — when some files **fit**, the highest quality tier among them wins (ties → larger size); else the best `Tight` file by the same order; else (known GPU, nothing fits) the smallest file; else (VRAM unknown — no probe) the quality **SweetSpot** is preferred, then `Balanced`, then the median by size.

## Inference Optimizer (operator surface)

The advisor recommends *which model*; the **Inference Optimizer** tunes *how an already-installed model launches* on this exact box. The full runtime/resolver mechanics live in [03-local-runtime-and-providers.md §2.5](03-local-runtime-and-providers.md#25-inference-profiles--per-machine-tuning); model-fit owns the **operator-facing surface**. `IInferenceProfileService` → `InferenceProfileService` (`Services/Inference/`, **scoped**) drives an explore → benchmark → freeze lifecycle over node-local GGUF models only (cloud/missing models are rejected without spawning):

- **Explore** — spawns one auto-fit `llama-server`, parses the fitted launch args from its startup banner, and upserts the single **Explored** profile keyed by `(machineKey, model, role, backend)`. The request body carries an optional `contextTokens` operator override that pins **that explore spawn's** `-c` for the one call: it is never persisted (no node setting, no launch-policy option, no fingerprint input), it is silently capped by the model's train ceiling and floor-aligned, so `ctxSize` on the returned profile is the effective window rather than the requested one, and it is **GPU-only** — a non-null value on a CPU-variant node is rejected with a 400 because `llama-fit-params` does not run there.
- **Benchmark** — replays the drafted profile under a metrics-enabled spawn against a fixed golden transcript, persists a benchmark snapshot + metric row (Succeeded/Failed). Does **not** freeze.
- **Freeze** — promotes Explored → **Frozen**, **gated on a most-recent successful benchmark** (fails cleanly, never throws, with no justifying benchmark). A frozen profile is then replayed verbatim on every cold spawn until its baseline (build / hardware / free-VRAM) changes invalidates it back to Stale.
- **Invalidate** — operator-triggered manual demotion to **Stale** (forces re-explore).

These are exposed as four body-carrying POST actions plus a collection GET — see [Endpoints](#endpoints) below and [09-api-and-hubs.md](09-api-and-hubs.md). The React **Inference Profile panel** (`features/model-fit/components/InferenceProfilePanel.tsx` + `queries/useInferenceProfiles.ts`, mutations `explore`/`benchmark`/`freeze`/`invalidate`) renders this surface. The persisted store + metrics are migration `20260626234754_AddInferenceProfilesAndBenchmarkMetrics` ([08-data-and-persistence.md](08-data-and-persistence.md)).

## The hardware profiler

`HardwareProfiler` (`Providers.Capabilities/Implementation/HardwareProfiler.cs`, implements `IHardwareProfiler`) produces a sanitized `HardwareProfile` aggregate (RAM / VRAM / GPU vendor / CPU cores / free disk — **no machine identifiers**). It is a Singleton with a `volatile` cached profile; `GetProfileAsync(forceRefresh, ct)` serves the cache unless `forceRefresh` is set (lock-free — the probe is read-only/idempotent).

Probing logic (`ProbeAsync`):

- **RAM** — Linux parses `/proc/meminfo` (`MemTotal` / `MemAvailable`); otherwise an OS query.
- **GPU vendor** — `nvidia-smi --query-gpu=name` first on every OS (NVIDIA is unambiguous); else Linux reads `/sys/class/drm` PCI vendor ids (NVIDIA/AMD/Intel), Windows uses a DXGI/WMI vendor-name seam.
- **VRAM** — NVIDIA via `nvidia-smi --query-gpu=memory.total` (scans past warning-banner lines to the first parseable line); Windows via a DXGI seam; **Linux non-NVIDIA has no byte-accurate source → VRAM unknown**.
- **Degrade rule (`HardwareProfiler.ProbeAsync`).** `gpuAccelAvailable = vramKnown && vendor ∈ {Nvidia, Amd, Intel}`. **VRAM unknown ⇒ no GPU budget**, even when a vendor is detected — so the estimator scores against RAM in CPU mode. This is why an AMD/Intel Linux box degrades to CPU mode in the tests.

## The refresh service (the advisor)

`ModelFitRefreshService.RefreshAsync` (`Implementation/ModelFitRefreshService.cs`) is the advisor proper. It is **scoped** (resolved per Quartz fire by the singleton handler through a fresh DI scope). Flow:

1. **Guard** — non-`Recommend` operations are rejected before any snapshot row exists (the `operation != Recommend` guard).
2. **Validate** intent (`useCase`, `limit`) via `ModelFitRequestValidator` against the fixed allowlist; provider is fixed to the `"llama.cpp"` sentinel.
3. **Open** a `Running` snapshot row (`IModelFitSnapshotStore.CreateRunningAsync`) and report progress.
4. **Profile** hardware (`IHardwareProfiler.GetProfileAsync`), then `BuildRecommendationsAsync`:
   - **Two-pass discovery per use-case term.** Each mapped HF search term runs **twice** — `GgufSearchSort.Trending` (current download/like velocity) *and* `GgufSearchSort.LastModified` (most recently updated) — and the per-term lists are merged **round-robin** so neither pass dominates. The recency pass surfaces newly-released big models a trending-only search misses once the pool is capped; trending keeps the established-popular repos. Each search is wrapped in a **20 s per-call timeout** (a stalled search maps to a clean `Failed` run, not a hang).
   - List already-downloaded GGUF keys (best-effort).
   - Inspect candidate repos **in parallel with bounded concurrency** (`SemaphoreSlim`); a stalled/failing repo is skipped (null candidate) so it never fails the whole run.
   - **Quant-ladder step-down (per repo).** Instead of taking one fixed quant, the advisor walks the repo's files against the [`QuantLadder`](#the-quant-ladder--quality-tiers) from the highest-quality quant **down** to the `Q3_K_M` quality floor (`QuantLadder.FloorRank`), estimating each with `MemoryFitEstimator`, and keeps the highest-quality quant that **fits** the budget. This is why big new models (Gemma-3-27B, Qwen-3.x) now surface at, say, `Q4_K_M` instead of being dropped because their `Q8_0` didn't fit.
   - **Bucketed capability ranking.** The fitting candidates are ranked **capability-first but bucketed to ~1 GiB** (`EstimatedBytes / CapabilityBucketBytes`, `CapabilityBucketBytes = 1 GiB`) so a trivially-larger model no longer always outranks a much newer or far more popular peer. Within a bucket the order is **downloads (popularity) → last-modified (recency) → trusted-publisher (soft nudge) → repo id (deterministic tie-break)**, then `Take(request.Limit)` (`ModelFitRefreshService.cs`).
   - Each emitted recommendation also carries `release_date` (the repo's last-modified timestamp) and `is_trusted_publisher` for the UI's recency/trust signals.
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

> **UI ownership note.** In the React layer the GGUF browse/download, llama.cpp runtime, HF token, and running-models hooks were **relocated** out of the model-fit feature into Model Management / Node Settings / Loaded Models. A download started from a recommendation row is owned by the Model Management feature (`useModelFit.ts`). The download/runtime/token endpoints still live physically under `Endpoints/ModelFit/V1/` (see table below) but their UI no longer renders on the advisor page.

## Quartz wiring

| Piece | File | Role |
|---|---|---|
| Handler | `ModelRecommendationCheckHandler` (template id `model-recommendation-check`) | Validates decrypted params against a draft-07 JSON schema, opens a DI scope, invokes `IModelFitRefreshService`. Owns **no** scheduler state. |
| Seeder | `ModelRecommendationScheduleSeeder` (`IHostedService`) | Idempotently seeds **one** enabled `ScheduleKind.Manual` job (durable Quartz job, **no trigger**, never auto-fires) so the React "Refresh now" button works without the operator hand-creating a schedule. **Self-healing** — re-seeds if deleted. |
| Trigger facade | `ModelFitRefreshTrigger` (`IModelFitRefreshTrigger`) | Template-guarded: rejects any job that is not a `model-recommendation-check` job, validates whitelisted per-fire overrides (`useCase` against the six-value allowlist, `limit` bounds, trimmed `quantOverride`, `ctxTarget` min) **before** firing, then delegates to `TriggerNowAsync`. |

Default seeded parameters: `{"operation":"Recommend","useCase":"coding","limit":5}` — no approved-image/provider fields. The handler also supports `Cron`/`OneShot`/`SimpleInterval` for operators who want a recurring refresh, but `Manual` (on-demand) is the recommended kind. See [Scheduler](06-scheduler.md) for the dispatcher, run-history, and SignalR hub mechanics.

## Endpoints

Routes under `model-fit/*` (`LocalApiRoutes.ModelFit`, mapped in `Endpoints/ModelFit/V1/`). The advisor-proper routes are the first three; the rest are thin transport over the llama.cpp binary/supervisor and HF GGUF seams (their UI now lives in sibling features). `LocalApiRoutes.ModelFit` is the authority for the complete constant list — the table below names every family it currently holds, and [API & hubs](09-api-and-hubs.md) carries the same inventory alongside the other route families.

| Route constant | Path | Endpoint | Notes |
|---|---|---|---|
| `RecommendationsLatest` | `model-fit/recommendations/latest` | `GetLatestRecommendationsEndpoint` | **Cache-read only.** Filtered by `useCase`. |
| `RecommendationsRefresh` | `model-fit/recommendations/refresh` | `RefreshRecommendationsEndpoint` | Fires the scheduler trigger; returns 200 immediately. |
| `HardwareProfile` | `model-fit/hardware-profile` | `GetHardwareProfileEndpoint` | Sanitized aggregates; `?refresh=true` re-probes. |
| `GgufBrowse` / `GgufInspect` | `model-fit/gguf/browse` · `…/inspect` | `BrowseGgufRepositoriesEndpoint` · `InspectGgufRepositoryEndpoint` | HF GGUF discovery + per-repo quant/size inspection. **Inspect** annotates each file with quality tier + fit verdict + the ★ recommended variant (`IGgufVariantRecommender`). |
| `Download` / `DownloadCancel` | `model-fit/download` · `…/cancel` | `StartGgufDownloadEndpoint` · `CancelGgufDownloadEndpoint` | Background, cancellable, keyed by model name. |
| `Downloads` / `DownloadStatus` / `DownloadOperationStatus` | `model-fit/gguf/downloads` · `…/{modelName}` · `…/operations/{operationId}` | `GetGgufDownloadsEndpoint` · `GetGgufDownloadStatusEndpoint` · `GetGgufDownloadOperationStatusEndpoint` | One-shot hydrate for the download list/one download/one operation. The per-second poll is gone — progress arrives over `GgufDownloadHub` ([API & hubs](09-api-and-hubs.md)). |
| `ImportCapability` / `ImportPreview` / `Import` / `Imports` / `ImportStatus` / `ImportCancel` | `model-fit/gguf/import/capability` · `…/import/preview` · `…/import` · `…/imports` · `…/imports/{operationId}` · `…/imports/{operationId}/cancel` | `GetGgufImportCapabilityEndpoint` · `PreviewGgufImportEndpoint` · `StartGgufImportEndpoint` · `GetGgufImportsEndpoint` · `GetGgufImportStatusEndpoint` · `CancelGgufImportEndpoint` | **Import an already-downloaded local GGUF** into the registry: capability probe, dry-run preview of what would be imported, then a background, cancellable operation tracked by id. Shares the acquisition preflight + importer that promotes a [trained artifact](18-training.md). |
| `CatalogInfo` / `CatalogRefresh` | `model-fit/catalog` · `…/catalog/refresh` | `GetModelCatalogInfoEndpoint` · `RefreshModelCatalogEndpoint` | Local model-catalog snapshot and an operator-initiated refresh. |
| `Running` / `RunningEject` | `model-fit/running` · `…/eject` | `ListRunningModelsEndpoint` · `EjectRunningModelEndpoint` | Running llama-server processes; eject tree-kills one. |
| `LlamaCppVersion` / `LlamaCppRuntime` / `LlamaCppUpdate` | `model-fit/llamacpp/version` · `…/runtime` · `…/update` | `EnsureLlamaCppBinaryEndpoint` (a **POST** on `…/version`) · `GetLlamaCppRuntimeEndpoint` · `UpdateLlamaCppRuntimeEndpoint` | Pinned/resolved binary version, dynamic-runtime status, operator-initiated install/update. There is no `GetLlamaCppVersionEndpoint`: resolving the version can install the binary, so the route is a POST. |
| `CudaBuild*` (5 constants + `CudaBuildHub`) | `model-fit/llamacpp/cuda-build(/prerequisites\|status\|cancel\|remove)` | `Start`/`Cancel`/`Remove CudaBuildEndpoint` · `GetCudaBuildPrerequisitesEndpoint` · `GetCudaBuildStatusEndpoint` | Linux in-app CUDA build lifecycle. A blocked start answers a **typed 409 domain object**, not ProblemDetails — see [API & hubs](09-api-and-hubs.md). Progress rides `CudaBuildHub`. |
| `SourceBuild*` (5 constants + `SourceBuildHub`) | `model-fit/llamacpp/source-build(/prerequisites\|status\|cancel\|remove)` | `Start`/`Cancel`/`Remove LlamaCppSourceBuildEndpoint` · `GetLlamaCppSourceBuildPrerequisitesEndpoint` · `GetLlamaCppSourceBuildStatusEndpoint` | The generalized source-build family (official pin, custom repo, or explicit 40-hex commit). Same typed-409 shape; a source build is also the only runtime that ships `llama-quantize`, which merged [training](18-training.md) exports need. |
| `LlamaCppAcquisition` (+ hub) | `model-fit/llamacpp/acquisition` | `GetRuntimeAcquisitionStatusEndpoint` | Latest snapshot of the host's automatic first-run llama.cpp acquisition; `RuntimeAcquisitionHub` pushes the same sequence-numbered status. |
| `HfToken` | `model-fit/hf-token` | `GetHfTokenStatusEndpoint` · `SetHfTokenEndpoint` | **GET reports presence only** — the token is never returned (security gate). |
| `Profiles` + `ProfilesExplore` / `ProfilesBenchmark` / `ProfilesFreeze` / `ProfilesInvalidate` | `model-fit/profiles` · `…/profiles/{explore,benchmark,freeze,invalidate}` | `ListInferenceProfilesEndpoint` · `Explore`/`Benchmark`/`Freeze`/`InvalidateInferenceProfileEndpoint` | Inference Optimizer (`IInferenceProfileService`). Collection GET lists every persisted node-local profile (**machine key omitted**). The four actions are **body-carrying POSTs** (target in the body, never a route param) so the POST always has a body — sidesteps the FastEndpoints 415-on-bodyless-POST issue. |

All endpoints are loopback/local-only, authenticated, and secret-redacted — see [Security & privacy](12-security-and-privacy.md). They are surfaced to React through OpenAPI → hey-api; see [API & hubs](09-api-and-hubs.md) and [React client](10-react-client.md).

## React feature

`src/features/model-fit/` (page `ModelRecommendationsPage.tsx`):

- **`queries/useModelFit.ts`** — `useLatestRecommendations(filters)` (cache-read, `select` maps the optional-field generated DTO into the stricter domain view-model), `useHardwareProfile(refresh)`, and `useRefreshRecommendations()` (a mutation that fires the seeded job and invalidates the latest cache on success). Every generated `*Options()` is wrapped in `withResponseValidation` so a malformed response surfaces as an `ApiError`, never a raw `ZodError`. **All reads are cache-only.**
- **`hooks/useModelFitSchedulerEvents.ts`** — subscribes to the **shared** scheduler SignalR hub (no second hub server), reacts only to terminal runs of the `model-recommendation-check` template, invalidates the latest-recommendations cache (TanStack Query refetches canonical state) and raises a transient toast. SignalR push is primary; a one-shot REST **catch-up** fires on every (re)connect to cover the connect-race / reconnect-gap (deduped by run id). There is **no interval polling**. The effect deliberately keeps `t` and `scheduledJobId` in refs so a new translation function or a late-resolving job id never rebuilds the connection mid-negotiation (a real StrictMode race the comments call out).
- **`components/`** — `HardwareProfileCard.tsx`, `RecommendationTable.tsx`, `ModelFitFormatters.ts`, and **`InferenceProfilePanel.tsx`** (the Inference Optimizer surface); **`models/`** — domain types + mappers incl. `InferenceProfileModels.ts` / `InferenceProfileMappers.ts`; **`queries/`** — also `useInferenceProfiles.ts` (the `explore`/`benchmark`/`freeze`/`invalidate` mutations, each invalidating the profiles list on success); **`notifications/`** — toast helpers; **`stores/`** — `ModelFitManagementStore.ts`. The GGUF quant picker (which renders the ★ recommended variant from `model-fit/gguf/inspect`) lives in the Model Management feature, not here — see the UI-ownership note above.

## Invariants a maintainer must respect

1. **The advisor runs in exactly one place** — `ModelFitRefreshService.RefreshAsync`, reachable only via the Quartz handler. Never run it from an endpoint or the read path.
2. **`/latest` is read-only.** Returning `null` on cache-miss is contractual (empty state); do not make it trigger a run.
3. **Refresh is async and audited.** Go through `ModelFitRefreshTrigger` (template-guarded) → scheduler; never call the refresh service directly from transport.
4. **Sanitization everywhere.** Snapshot errors, download statuses, and hardware profiles are sanitized of paths/URLs/tokens and machine identifiers before they leave the node.
5. **The HF token is write-only over the wire** — GET reports presence only.
6. **The recommendation advisor is estimator-only** and the approved-image concept is removed — don't reintroduce a container path or make a refresh spawn a process; advisor ranking is pure `MemoryFitEstimator` + quant ladder. This is unaffected by [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md), whose Docker permission is scoped to Development Mode execution. The one place a model *is* spawned for measurement is the **Inference Optimizer** benchmark (launch-arg tuning of an already-chosen model), which is a separate operator-triggered surface — keep the two paths distinct.

## Related pages

- [Local runtime & providers](03-local-runtime-and-providers.md) — host llama.cpp supervisor, binary manager, HF GGUF discovery/store the advisor depends on.
- [Training](18-training.md) — QLoRA fine-tuning; a promoted artifact enters the registry through the same acquisition preflight and importer as a GGUF import.
- [Scheduler](06-scheduler.md) — Quartz dispatcher, run history, and the SignalR hub the refresh path rides.
- [Data & persistence](08-data-and-persistence.md) — snapshot/recommendation tables in the node SQLite database and the exact selected-field encryption boundary.
- [API & hubs](09-api-and-hubs.md) — `/api/local/v1` mapping and OpenAPI → hey-api.
- [React client](10-react-client.md) — TanStack Query + SignalR conventions used by this feature.
- [Security & privacy](12-security-and-privacy.md) — local-only endpoints, secret redaction, token-presence gating.
- [Architecture overview](01-architecture-overview.md) · [Project layout](02-project-layout.md)
