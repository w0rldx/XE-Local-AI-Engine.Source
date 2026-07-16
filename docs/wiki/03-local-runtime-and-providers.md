# Local Runtime & Model Providers

> Last reviewed: 2026-06-27 · Code-grounded.

This page is the heart of the 2026-06-17 runtime re-architecture. It explains how XE Local AI Engine runs models **in-process** on the node host: the provider-neutral seams in `Providers.Abstractions`, the host **llama.cpp** process supervisor that spawns and tree-kills `llama-server` children, GPU-variant binary acquisition, and the satellite providers (Ollama, HuggingFace GGUF store, capability detection, Codex OAuth cloud chat). Model *recommendation* (box-aware GGUF fit) is owned by [07-model-fit.md](07-model-fit.md); this page covers only how a model gets selected, loaded, and served.

The big picture: there is **no Docker** and **no container sandbox** in the inference path, and the old `HostAgent` connection layer is deleted (only a teardown plan survives under `Plans/`). Inference = a host `llama-server` child process, localhost-bound, owned by a singleton supervisor. See [01-architecture-overview.md](01-architecture-overview.md) for where this fits in the node.

---

## 1. The provider seam: `Providers.Abstractions`

All application code depends on **provider-neutral contracts**, never on a provider's SDK types. The provider-specific transport types (OllamaSharp, the OpenAI adapter, the HF HTTP clients) stay inside their own provider projects.

### `ILocalModelProvider`

`XE-Local-AI-Engine.Providers.Abstractions/ILocalModelProvider.cs` is the 8-member boundary every local runtime implements:

| Member | Purpose |
|---|---|
| `ProviderName` | Stable key used in capability payloads and `LocalModelSelection` routing |
| `CheckHealthAsync` | Is the runtime reachable / operational? Returns `ModelProviderHealth` |
| `ListModelsAsync` | Installed models as normalized `LocalModelDescriptor` |
| `PullModelAsync` | Download/update a model, reporting `PullProgress` |
| `DeleteModelAsync` | Remove a locally installed model |
| `WarmModelAsync` | Pre-load so first-token latency is paid early |
| `UnloadModelAsync` | Release loaded weights when the runtime supports it |
| `CreateChatClient` | Returns an MEAI `IChatClient` for a `LocalModelSelection` |
| `CreateEmbeddingGenerator` | Returns `IEmbeddingGenerator<string, Embedding<float>>` |

Two invariants are stated directly in the contract doc-comments:

- **The selection's `ProviderName` must equal the provider's `ProviderName`** — every implementation validates this and throws otherwise (`LlamaServerLocalModelProvider.ValidateSelection`, the same check in `OllamaLocalModelProvider.CreateChatClient`).
- **Embeddings are produced by the node-local runtime only, never a shared/cloud endpoint** — so playbook/prompt text never leaves the node (privacy invariant; see [12-security-and-privacy.md](12-security-and-privacy.md)).

### `IChatClient` / `IEmbeddingGenerator` usage

Both come from **Microsoft.Extensions.AI (MEAI)**. The provider returns them; the application layer (`Client.Application/Services/*`, the agent runtime in `XE-Local-AI-Engine.AI.Agent`) consumes them without knowing which runtime is behind them. This is what lets the React UI and platform capability payloads stay provider-agnostic.

### How a provider is chosen at runtime

`Client.Application/Services/CloudProviders/Implementation/LocalModelProviderResolver.cs` collects all registered `ILocalModelProvider`s into a **case-insensitive map keyed by `ProviderName`** (last registration wins, so a host can override a provider). It resolves a `defaultProviderName` and carries `MaxLoadedProcesses`. With Ollama de-orchestrated in dev, the default resolves to `llamacpp`.

Other abstraction-project contracts worth knowing: `IModelCapabilityClient` (runtime/version/installed/running probes), `INodeDataDirectory` (where node data lives), `IAvailableVramProbe` (live free-VRAM, §2.5), `IGgufMetadataReader` (header facts incl. MoE expert count), and the `Gguf/*` family (`IGgufModelStore`, `IGgufModelRegistry`, `IHfTokenStore`, `IHuggingFaceGgufDiscovery`, `GgufModelName`, `GgufFilePath`) which is the shared GGUF vocabulary the llama-server and HuggingFace projects both speak. The same `Gguf/` folder now also holds the **quant-quality single source of truth** — `QuantLadder` (the curated best→worst quant ladder + `Q3_K_M` quality floor) and `GgufQuantQuality` (coarse `GgufQuantTier` classifier) — moved here so both the advisor's memory-fit step-down and the download picker's per-row badge read one table. The advisor side is detailed in [07-model-fit.md](07-model-fit.md).

---

## 2. `Providers.LlamaServer` — the host inference runtime

This is the default dev + production runtime. It maps `ILocalModelProvider` onto a process supervisor that owns real `llama-server` children.

### `LlamaServerLocalModelProvider`

`ProviderName` is `"llamacpp"`. It holds two collaborators: `ILlamaServerProcessSupervisor` (process lifecycle) and `IGgufModelStore` (model inventory + file resolution + pull/delete).

- `ListModelsAsync` / `PullModelAsync` / `DeleteModelAsync` delegate to the **GGUF store** — GGUF acquisition is NOT the supervisor's job. `PullModelAsync` parses the bare name via `GgufModelName.Parse` (`{repo}[:{quant}]`) into a `GgufModelRequest` and calls `store.EnsureModelAsync`.
- `WarmModelAsync` calls `supervisor.EnsureRunningAsync(model, ModelRole.Chat)`.
- `UnloadModelAsync` evicts **both** roles (Chat and Embedding) — eviction is idempotent.
- `CheckHealthAsync` reports healthy iff the supervisor answered the aggregation; an empty process list means "operational, no models loaded".

### Deferred chat / embedding clients

`CreateChatClient` / `CreateEmbeddingGenerator` are **synchronous factory methods**, but starting a process is async. The resolution: return a **deferred** client (`DeferredLlamaServerChatClient`, `DeferredLlamaServerEmbeddingGenerator`) that pays the cold-start cost on the **first** `GetResponseAsync`/`GetStreamingResponseAsync` — a normal first-token delay rather than a blocking sync call. Internally (`DeferredLlamaServerChatClient.EnsureInnerAsync`):

1. Single-flight gate (`SemaphoreSlim`) so concurrent first calls ensure-run once.
2. `supervisor.EnsureRunningAsync(model, ModelRole.Chat)` → resolved endpoint.
3. `LlamaServerOpenAIAdapterFactory.CreateChatClient(endpoint, model)` builds the MEAI OpenAI adapter once, keyed by endpoint.

The supervisor owns the process; the deferred wrapper owns only the inner adapter and disposes it on `Dispose`.

### `LlamaServerProcessSupervisor` — process lifecycle

`ILlamaServerProcessSupervisor` is the public contract; the implementation's ctor is **internal** (it takes internal launcher/health-probe seams). It is registered as a strict **singleton** — it owns every `llama-server` child for the node and disposes them on shutdown. The reaper loop starts in the ctor.

Contract members:

- `EnsureRunningAsync(modelName, role, ct)` → `LlamaServerEndpoint` (reuse-or-spawn). The spawn/load runs **detached** from the caller's token (a shared per-key task under the shutdown token): a caller cancelling only abandons its wait; the load continues and the model becomes warm for the next send. Single-flight is preserved.
- `EvictAsync(modelName, role, ct)` — **immediate** tree-kill if running, release its port, idempotent. Used internally (profiling exclusivity, provider unload); does NOT wait for in-flight work.
- `EjectAsync(modelName, role, force, ct)` → `LlamaServerEjectOutcome` — the **operator** eject: mark evicting (no new leases), drain in-flight inference for a bounded `EjectDrainTimeout`, then tear down. Returns `Ejected` (idle/drained cleanly), `TimedOutStillBusy` (busy and not forced — **left running**, never killed silently), `ForcedWhileBusy` (`force:true` — killed anyway, run marked operator-ejected), or `NotRunning`.
- `TryAcquireInferenceLease(modelName, role)` → `ILlamaServerInferenceLease?` — a per-request lease the chat client holds so a graceful eject waits for the turn; `WasEjected` lets an interrupted request classify a force-eject drop as operator-ejected rather than a generic failure.
- `CheckHealthAsync(ct)` — aggregate every running process's health.

**Keying.** Each distinct `(model, role)` is a distinct process (`ProcessKey`) and counts against the loaded-cap. A chat process launches with `--jinja`; an embedding process with `--embeddings --pooling mean`.

**`EnsureRunningAsync` flow** (`LlamaServerProcessSupervisor.cs:118`):

```
1. External-endpoint short-circuit: _externalEndpoints.Resolve(model, role)
   -> if configured, return LlamaServerEndpoint pointing at it. NO spawn, NO supervision.
2. Fast path: an already-running, live process for the key is reused
   (MarkUsed timestamp) WITHOUT taking the spawn gate.
3. Single-flight: per-key SemaphoreSlim ensures concurrent callers spawn exactly once.
   Re-check under the gate; reap any crashed/exited lingering process; then
   SpawnWithRestartAsync (linear backoff up to MaxRestartAttempts).
```

**Spawn internals.** `SpawnWithRestartAsync` → `SpawnOnceAsync` → `SpawnCoreAsync` → `BuildLaunchSpec`. `SpawnOnceAsync` (`LlamaServerProcessSupervisor.cs:268`) no longer builds args itself: it hands `SpawnCoreAsync` a resolver delegate `(variant, ct) => _profileResolver.ResolveAsync(model, role, variant, ct)` (`:274`) that returns the launch arguments for this `(model, role, backend)`. `SpawnCoreAsync` resolves the model file + variant + binary, awaits that delegate **before** taking the admission gate (so a slow profile read never stalls admission for other keys), then `AdmitAndAllocatePortAsync` takes the admission gate, prunes exited processes, enforces the loaded-cap (evict idle LRU via `TryEvictIdleLeastRecentlyUsed`, else throw `CapReached`), and allocates a port.

`BuildLaunchSpec(key, exe, modelFile, port, variant, resolved)` (`:446`) builds the exact ordered argv from the resolved arguments:

```
-m <modelFile> --host 127.0.0.1 --port <port>
  --parallel 1             # pinned on EVERY spawn (single-slot serving)
  --no-warmup              # pinned on EVERY spawn (skip empty-run warmup)
  <GPU placement args>     # non-CPU variant only — see AppendGpuPlacementArgs below
  --jinja                  # ModelRole.Chat
  --embeddings --pooling mean  # ModelRole.Embedding
```

Two flags are now pinned on **every** spawn (`:466,473`; commit 2665d965):

- **`--parallel 1`** forces single-slot serving. Without it `llama-server` auto-selects `n_parallel=4`, which reserves 4× the KV cache and starves the auto-fit weight offload — a model that would fit on the GPU spills weights to system RAM for KV slots it never uses and runs slow on the CPU.
- **`--no-warmup`** skips the empty-run warmup (45–110 s on a large model) that would otherwise overrun the readiness budget and tree-kill the half-ready process in a respawn loop (observed as a chat inter-chunk stall and an explore "did not become ready in time").

**GPU placement is no longer forced — the old `--n-gpu-layers 999` is gone** (`:477`). For a non-CPU variant `AppendGpuPlacementArgs` (`:508`) emits one of two **mutually exclusive** arg sets supplied by the profile resolver (`ResolvedLaunchArguments`):

- **Explore mode** — `--fit on --metrics`: lets llama.cpp's own `--fit` auto-fit choose layer/expert placement and print the chosen params for capture (the `--metrics` gauges feed the benchmark). Any *explicit* fit-arg would disable auto-fit, so explore emits none.
- **Replay mode** — the frozen/explored profile's explicit args verbatim: `-c <ctx>` plus `-ngl/-ts/-ot` and matched KV-cache types (`-ctk/-ctv` with `--flash-attn on`). `--fit` is intentionally absent (an explicit fit-arg disables it).

The CPU variant stays a pure CPU run and emits **no** GPU/fit args. The **`--host 127.0.0.1`** localhost-only bind is unchanged. See [§2.5 Inference profiles / per-machine tuning](#25-inference-profiles--per-machine-tuning) for where `resolved` comes from.

### Health probe — readiness vs liveness

`LlamaServerHealthProbe` (impl of `ILlamaServerHealthProbe`) polls the `llama-server` **`/health`** endpoint over HTTP (`/health` is a sibling of the `/v1` base). `WaitForReadyAsync` polls every 250 ms until a 200 or the readiness deadline — connection-refused during warm-up is normal and retried. `CheckResponsiveAsync` is a single probe used by health aggregation. The readiness deadline is **size-aware** (`LlamaServerSupervisorOptions.ResolveReadinessTimeout(bytes)` — base + per-GiB extension above a threshold, capped), not a fixed constant, so a large model gets proportionally longer to load. The probe runs on a **dedicated, resilience-free `HttpClient`** with a ~1 s per-attempt bound: routing it through the app's `IHttpClientFactory` inherited the standard resilience handler's exponential retries and detected readiness up to ~5 s late.

### Eviction & reaper

- `LlamaServerSupervisorOptions`: `MaxLoadedProcesses`, `IdleTimeToLive` (default **15 min**), `PortRangeStart`/`PortRangeEnd`, `MaxRestartAttempts`, plus the Audit-4 readiness/eject knobs — `ReadinessBaseTimeout` (default 120 s), `ReadinessTimeoutModelSizeThresholdGiB`/`ReadinessTimeoutSecondsPerGiB`/`ReadinessTimeoutCap` (size-aware deadline), `MaxReadinessTimeoutRetries` (default 1 — a readiness timeout is retried at most this many times, NOT `MaxRestartAttempts`), and `EjectDrainTimeout` (bounded graceful-eject drain). `Validate()` fails fast on structurally invalid values. The host overrides cap/TTL/launch-flag knobs from node config (`AddNodeModelRuntimeExtensions.BuildSeededLlamaServerSupervisorOptions`).
- `ReapIdleLoopAsync` runs on a background loop (interval = TTL/4, min 1 s) and evicts processes idle beyond `IdleTimeToLive` or already exited.
- `TryEvictIdleLeastRecentlyUsed` runs synchronously under the admission gate to free a slot for a new admission — it never evicts an in-window process.

### No-orphan shutdown guarantee

This is the safety property the launcher exists for. `LlamaServerProcessLauncher.Launch` picks an OS-specific containment primitive so closing the handle tree-kills the whole descendant tree:

| OS | Containment | Tree-kill mechanism |
|---|---|---|
| Windows | `WindowsJobObjectProcessHandle` | Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` — disposing the job handle kills the tree |
| Linux | `LinuxProcessGroupHandle` | child launched under `setsid` (new session/process-group); teardown does `kill(-pgid)` |
| macOS / other Unix | `PlainProcessHandle` | plain process; own-tree-kill (CPU floor only — no dedicated primitive) |

Each native path is reached only under its own `OperatingSystem.Is*` guard, so no cross-OS native call leaks. `TeardownProcess` (called from `DisposeAsync`, the reaper, eviction, and prune) removes the process from the map, tree-kills + disposes the handle, and releases the port. `DisposeAsync` cancels the reaper, tears down every remaining process, and disposes all gates — guaranteeing no orphaned `llama-server` survives a clean node shutdown.

### GPU variant selection

For *asset selection*, the supervisor does **only enough** hardware probing to pick the prebuilt binary — the full VRAM/memory-fit math lives in the Model Advisor ([07-model-fit.md](07-model-fit.md)), explicitly NOT here. (The separate *live* free-VRAM probe used by inference-profile invalidation and the variant recommender is `LlamaListDevicesVramProbe`, §2.5.)

- `IGpuVendorProbe` → `ProcessGpuVendorProbe` detects the vendor (`DetectedGpuVendor`: Nvidia/Amd/Intel/none).
- `IGpuVariantSelector` → `GpuVariantSelector` applies the OS-aware rule (`SelectForVendor`):

```
NVIDIA  -> CUDA  on Windows
        -> Vulkan on Linux   (llama.cpp ships NO prebuilt Linux CUDA asset)
AMD/Intel -> Vulkan
none/unknown -> CPU
```

`GpuVariant` is the resulting enum (`Cuda` / `Vulkan` / `Cpu`).

### Binary manager + dynamic updater

`ILlamaCppBinaryManager` → `LlamaCppBinaryManager` acquires the right prebuilt `llama-server` for the selected `GpuVariant`. There is **no source build, ever** — only verified prebuilt assets.

**3-tier tag resolution** (`ResolveActiveTagAsync`):

1. **Live** — `GitHubLlamaCppReleaseCatalog` (`ILlamaCppReleaseCatalog`) queries the `ggml-org/llama.cpp` GitHub Releases API for the recommended tag. Best-effort: any network failure (DNS/connect/timeout/rate-limit) is treated as "offline" and falls through; acquisition never depends on the network.
2. **Installed** — `IInstalledRuntimeStore` / `InstalledRuntimeStore` reads `installed-runtime.json` (the tag actually on disk, written only after a verified, smoke-tested install). Atomic temp-file write, owner-only `0600` on non-Windows, tolerant deserialize (corrupt → null).
3. **Pinned floor** — `LlamaCppReleasePins` (tag **`b9692`**, published 2026-06-17). The pin table keys `(OS, arch, GpuVariant)` → `(AssetName, Sha256, ServerRelativePath)`; SHA256 digests come from the GitHub release-assets `digest` field (llama.cpp publishes no `.sha256` sidecars). The offline last-resort and the asset-name template source.

Acquisition (`EnsureBinaryAsync`): resolve tag → resolve pin (falls back to the CPU floor when no GPU prebuilt exists) → reuse cached binary if present → else download, **verify SHA256**, extract under `{cacheRoot}/llama.cpp/{tag}/{variantSlug}`. `InstallTagAsync` (the updater path) gates the live asset name against a strict allow-list (no path/URL metacharacters) since it is interpolated into a temp path and download URL, verifies the 64-hex digest, and enforces a disk-space guard. `ILlamaCppUpdateState` / `LlamaCppUpdateState` is the shared "is a newer runtime available?" snapshot, written by the startup check and surfaced by a read-only runtime-status endpoint — decoupled from any app-package update channel.

> Known constraint, documented in the pins: llama.cpp ships no prebuilt **Linux CUDA** asset (Linux NVIDIA → Vulkan), and Windows CUDA needs a separate `cudart-…` archive whose handling is a documented follow-up, not modeled in the pin row.

### `ModelRole` and the external-endpoint option

- `ModelRole` (enum): `Chat`, `Embedding`. Drives both the launch flags and the process key.
- `LlamaServerExternalEndpointOptions` is an optional **hybrid attach** map: `(modelName, role) → external OpenAI-compatible base URL`. A match short-circuits `EnsureRunningAsync` entirely — the supervisor returns the configured endpoint and never owns a process for it. Empty by default (pure spawn-and-supervise); bound from node config at DI time.

### DI wiring

`LlamaServerServiceCollectionExtensions.AddLlamaServerLocalModelProvider` registers the whole stack as singletons via `TryAdd*`: vendor probe, variant selector, the live catalog + installed-runtime store + update state, the binary manager (built by factory with `cacheRoot/activeTag = null` so it self-defaults), default supervisor + external-endpoint options, the launcher and health probe, the **`DefaultInferenceProfileResolver`** (TryAdd, see §2.5), and the supervisor itself (explicit factory because its ctor is internal). The host must register an `HttpClient` (`AddHttpClient`) for binary downloads and supply an `IGgufModelStore` (the HuggingFace GGUF store).

### 2.5 Inference profiles / per-machine tuning

llama.cpp launch args used to be hard-coded (the forced `-ngl 999`). They are now resolved per-spawn by the **inference optimizer**: a node explores a model once on the actual hardware, optionally benchmarks the result against a golden transcript, freezes the winning args, and replays them verbatim on every later spawn of that `(model, role, backend)` — so each box runs the model with placement that was proven on *that* box, not a one-size guess.

**The resolver seam.** `IInferenceProfileResolver` (`Providers.LlamaServer/Contracts/IInferenceProfileResolver.cs`) is the dependency-inversion boundary the supervisor calls on the cold-spawn path. It is **defined in `Providers.LlamaServer`** so the supervisor never depends on `Client.Application` (the one-way `Application → Providers` arrow is preserved). Two implementations:

- **`DefaultInferenceProfileResolver`** (ships in the provider, `internal`) always returns `ResolvedLaunchArguments.Explore()` — a node with no profile store self-satisfies and launches under llama.cpp auto-fit. Registered with `TryAddSingleton`.
- **`InferenceProfileResolver`** (`Client.Application/Services/Inference/InferenceProfileResolver.cs`) is the real DB-backed resolver, registered **last** so it wins. It keys a lookup by `(machineKey, model, role, backend)`, then: an **Explored** or non-stale **Frozen** profile replays its persisted args; a Frozen profile is first re-checked through `IInferenceInvalidationEvaluator.IsStaleAsync` and demoted to **Stale** (→ explore) when its baseline no longer holds; CPU spawns and any missing/Stale/corrupt row fall back to explore. This path **never throws** — a bad persisted arg combo degrades to auto-fit rather than escaping the supervisor's spawn. `IInferenceProfileStore` is scoped, so the singleton resolver opens a fresh DI scope per call.

**Machine key.** `IMachineKeyProvider` → `MachineKeyProvider` (`Services/Inference/MachineKeyProvider.cs`) reads/mints a per-box GUID (`"N"` format) persisted in node settings (generate-once is gated so two concurrent first-callers can't mint two keys). Profiles are keyed to this machine key and are **local-only** — the key is never emitted in telemetry, aggregates, logs, or the transport view (`InferenceProfileView` deliberately omits it). This is what lets a profile re-explore when the box, build, or free-VRAM baseline changes rather than blindly replaying stale args.

**Live free-VRAM probe.** `IAvailableVramProbe` → `LlamaListDevicesVramProbe` (`Providers.LlamaServer/Implementation/LlamaListDevicesVramProbe.cs`) runs a short-lived `llama-server --list-devices` (15 s cap, tree-killed on overrun), parses the per-device `"<total> MiB, <free> MiB free"` column and returns the **largest free** figure in bytes. It is vendor-agnostic — it reads llama.cpp's own device report (CUDA / Vulkan / SYCL), never `nvidia-smi`, so one code path serves every GPU backend. **Degrade, never throw:** a CPU/unknown/blank backend (no process is even spawned), an empty device list, a timeout, or any failure degrades to `null` ("unknown"). This figure feeds profile invalidation and the GGUF variant recommender ([07-model-fit.md](07-model-fit.md)). Distinct from `HardwareProfiler`'s static VRAM probe (§3) — this one measures *currently free* VRAM at spawn time.

**Device audit (AUD4-03).** A sibling probe, `ILlamaDeviceInventoryProbe` → `LlamaDeviceInventoryProbe`, runs the same `--list-devices` (via the shared `LlamaListDevicesProcessRunner`) but returns the **structured device list** `{variant, devices[], probeSucceeded}` cached per binary (path+mtime). The Application-layer `IRuntimeDeviceAudit` → `RuntimeDeviceAuditService` composes it with the hardware profile + the selected variant into `RuntimeDeviceAuditState {inferenceBackend, gpuExpected, cpuFallback, reason, remediation}`: a GPU box whose selected runtime enumerates **zero** devices (or a CPU variant on a GPU box) is a **silent CPU fallback** — the audited WSL2-Vulkan-no-ICD case. An indeterminate probe is "unknown", never a false alarm. `GetEffectiveProfileAsync` degrades the profile to CPU-mode on fallback so the advisor ([07-model-fit.md](07-model-fit.md)) and `CapacityService` size against RAM; the `GET model-fit/hardware-profile` endpoint returns the raw profile PLUS this audit block; a `device_fallback` metric + a Warning fire once per binary.

**GPU-load admission (AUD4-06).** `IGpuModelLoadAdmission` (Abstractions interface + `NoOp` floor; real `GpuModelLoadAdmission` in Application, one singleton) is a process-wide `SemaphoreSlim(1,1)` that serializes the **spawn-through-readiness** window of GPU-backed loads across BOTH this supervisor and the image supervisor ([14-image-generation.md](14-image-generation.md)), so two `--fit` loads never race the same free-VRAM read. It is acquired inside `SpawnCoreAsync` after variant selection, **GPU variants only** (CPU bypasses; reuse never touches it), released on ready/failure via `using`, and the acquire runs under the detached-spawn token so a cancelled caller never holds it. A bounded max-wait surfaces a typed `GpuModelLoadAdmissionTimeoutException` (non-retryable) rather than hanging. Lock ordering: the capacity ledger decision gate is released before the load gate is acquired (they never nest).

**MoE detection.** `IGgufMetadataReader` surfaces `IsMoe` / `ExpertCount` from the GGUF header (a positive declared expert count ⇒ MoE), carried onto the persisted profile so the operator orchestrator and UI can reason about expert placement (`-ot` override-tensor) for mixture-of-experts models.

**The operator orchestrator.** `IInferenceProfileService` → `InferenceProfileService` (`Services/Inference/InferenceProfileService.cs`, **scoped**) is the explore → benchmark → freeze lifecycle, exposed over the `model-fit/profiles/*` endpoints (see [07-model-fit.md](07-model-fit.md#inference-optimizer-operator-surface) and [09-api-and-hubs.md](09-api-and-hubs.md)):

- **`ExploreAsync`** spawns one auto-fit `llama-server` (via the supervisor's exclusive profiling path `RunExclusiveProfilingAsync`), parses the fitted args from the captured startup banner (falling back to the GGUF native context when unparseable), and upserts the single **Explored** profile for the key. Only node-local GGUF models are eligible — a cloud or missing model is rejected without spawning.
- **`BenchmarkAsync`** replays the drafted profile under a metrics-enabled spawn, runs the fixed golden transcript, and persists a benchmark snapshot + metric row (marked Succeeded/Failed). It does **not** freeze.
- **`FreezeAsync`** promotes an Explored profile to **Frozen** — **gated on a most-recent successful benchmark** (returns a failed result, never throws, when no justifying benchmark exists).
- **`InvalidateAsync`** is the operator-triggered manual demotion to **Stale** (forces a re-explore on the next spawn).

The persisted store + benchmark metrics are migration `20260626234754_AddInferenceProfilesAndBenchmarkMetrics` ([08-data-and-persistence.md](08-data-and-persistence.md)).

---

## 3. Satellite providers

### `Providers.Ollama` — present, de-orchestrated from Aspire dev

`OllamaLocalModelProvider` (`ProviderName = "ollama"`) still fully implements `ILocalModelProvider` over `IOllamaApiClient` (OllamaSharp): list/pull/delete/warm/unload + `CreateChatClient`/`CreateEmbeddingGenerator`. It remains a real, registered provider — but llama.cpp is the dev runtime, so **Ollama is no longer orchestrated by Aspire in dev** (see [11-hosting-and-deployment.md](11-hosting-and-deployment.md)). Notable detail: `AddOllamaLocalModelProvider` sets a short **750 ms `SocketsHttpHandler.ConnectTimeout`** so a probe against an absent Ollama daemon (desktop mode) fails fast instead of stalling on the OS connect timeout; `OllamaConnectFailureHandler` translates a fired connect-timeout (`OperationCanceledException`) into `HttpRequestException` so "Ollama unreachable" handling is uniform. The 5-minute `HttpClient.Timeout` still covers genuine long pulls. `OllamaModelCapabilityClient` implements `IModelCapabilityClient` as thin pass-throughs over the API client.

### `Providers.Capabilities` — hardware probing

`HardwareProfiler` (impl of `IHardwareProfiler` in the abstractions project) is the **full** hardware probe — CPU/RAM/GPU/VRAM facts (`HardwareProfile`, `GpuVendor`) — distinct from the minimal `IGpuVariantSelector`. It probes the environment via `IHardwareProbeEnvironment` / `IProcessProbe` and feeds the Model Advisor's memory-fit math ([07-model-fit.md](07-model-fit.md)). `CapabilitiesServiceCollectionExtensions` wires it.

### `Providers.CodexOAuth` — ChatGPT-OAuth cloud chat provider

The one **cloud** chat provider. `CodexOAuthChatClientFactory` (`ICodexOAuthChatClientFactory`) builds an `IChatClient` against the ChatGPT/Codex API authenticated by OAuth, with a shared handler chain (`SocketsHttpHandler → CodexAuthHandler`). The `Auth/*` folder holds the OAuth machinery: `CodexAuthService`, `CodexLoginCoordinator`, `CodexTokenStore` (`ICodexTokenStore`), `CodexHeaders`, `CodexTokens`. Codex-specific quirks are encoded here: `CodexResponseStoreDisabling` / `CodexStoreDisabledChatClient` force `store=false` (replaying encrypted reasoning), `CodexModelCatalog` + `CodexProviderCapabilities` declare the model/effort surface. OAuth tokens are a LOCAL secret — never returned to the browser, never logged (see [12-security-and-privacy.md](12-security-and-privacy.md)). Reasoning-effort selection and cloud↔local clamping are covered in [05-chat.md](05-chat.md).

> **Not a runtime provider:** voice/text-to-speech. The backend `VoiceManifestService` (`Client.Application/Services/Voice/`) is **config-only** — it serves the list of audition-able voices; the actual TTS engine runs **in the browser** (WebGPU/Kokoro). No model port, no inference process on the node. See [10-react-client.md](10-react-client.md) for the client runtime.

---

## 4. `Providers.HuggingFace` — GGUF discovery & download store

`HuggingFaceGgufStore` (internal, impl of `IGgufModelStore`) is the model inventory + acquisition backend the llama-server provider delegates to. It deliberately **does not depend on the LlamaServer project** — it tags descriptors with the agreed `llamacpp` provider-name constant.

Responsibilities and collaborators:

- **Discovery** — `HuggingFaceGgufDiscovery` (`IHuggingFaceGgufDiscovery`) enumerates GGUF files/quants for a repo via `HfHubClient`.
- **Download** — `HfDownloadClient` fetches the chosen GGUF to disk (atomic temp-file → rename for offline reuse), serializing concurrent `EnsureModelAsync` for the same name with a per-name gate, guarded by `IFreeSpaceProbe`.
- **Registry** — `GgufModelRegistry` (`IGgufModelRegistry`) is the on-disk record (`GgufModelRegistryEntry`: model name, local path, size, sha, downloaded-at); `ResolveModelFilePathAsync` / `ListInstalledModelsAsync` read it.
- **Header facts** — `GgufHeaderReader` reads the GGUF header once per model; `GgufCapabilityDetector.Detect` classifies tool/reasoning capability **deterministically from the embedded Jinja chat template** (`tokenizer.chat_template`) — tool markers (`tool_calls`, `function_call`, `tools`) and reasoning markers (`<think`, `enable_thinking`, `reasoning_content`). This is why XE needs no Ollama `/api/show` probe for a GGUF (a GGUF has no Ollama entry, and desktop mode runs no Ollama daemon). Results are cached by `(path, size, downloadedAt)`; a header-read failure for one model never sinks the list (yields `GgufHeaderFacts.Empty`).
- **Footprint facts** — `ResolveModelFootprintFactsAsync` exposes weight/KV inputs (the public seam consumed by the capacity/advisor layer) again from a single cached header read.
- **Token** — `IHfTokenStore` holds the optional HuggingFace token (a LOCAL secret) for gated repos.

`HuggingFaceServiceCollectionExtensions.AddHuggingFaceGgufStore` (with `HuggingFaceOptions`) wires the store; the llama-server stack requires it. Persisted GGUF files and the registry are detailed in [08-data-and-persistence.md](08-data-and-persistence.md).

---

## 5. End-to-end: how a model gets selected, loaded, and served

```
React chat / agent run
  -> Application resolves a LocalModelSelection (provider + model)
  -> LocalModelProviderResolver maps ProviderName -> ILocalModelProvider
       (default: "llamacpp")
  -> provider.CreateChatClient(selection)
       -> DeferredLlamaServerChatClient (no process yet)
  -> first GetResponseAsync:
       -> supervisor.EnsureRunningAsync(model, Chat)
            external endpoint? -> attach, done.
            running? -> reuse (MarkUsed).
            else single-flight spawn:
              modelStore.ResolveModelFilePath(model)     # HF GGUF on disk
              variantSelector.SelectVariantAsync()       # CUDA/Vulkan/CPU
              binaryManager.EnsureBinaryAsync(variant)   # live->installed->pinned, SHA-verified
              profileResolver.ResolveAsync(model, role, variant)  # frozen replay args OR explore(--fit on)
              admit + allocate localhost port (cap-checked)
              launcher.Launch(BuildLaunchSpec(..., resolved))  # OS-contained child, --parallel 1 --no-warmup pinned
              healthProbe.WaitForReadyAsync(/health)
       -> build OpenAI adapter over the localhost endpoint
       -> stream tokens
  -> idle 15 min OR explicit unload OR node shutdown -> tree-kill, port released
```

Every `llama-server` child is same-user, unprivileged, and `127.0.0.1`-bound — the node never exposes a model port outward. Only the Node Web Server talks to the platform (over WorkerHub); the runtime layer here is purely local. See [09-api-and-hubs.md](09-api-and-hubs.md) for the local admin endpoints that drive warm/unload/health and [12-security-and-privacy.md](12-security-and-privacy.md) for the trust boundary.

---

## Related pages

- [01-architecture-overview.md](01-architecture-overview.md) — where the runtime fits in the node
- [02-project-layout.md](02-project-layout.md) — the `Providers.*` project inventory
- [07-model-fit.md](07-model-fit.md) — box-aware GGUF recommendation & the full hardware profiler
- [05-chat.md](05-chat.md) — chat/reasoning over these chat clients
- [08-data-and-persistence.md](08-data-and-persistence.md) — GGUF files, registry, installed-runtime state
- [09-api-and-hubs.md](09-api-and-hubs.md) — local endpoints/hubs that drive the runtime
- [11-hosting-and-deployment.md](11-hosting-and-deployment.md) — Aspire dev orchestration & desktop mode
- [12-security-and-privacy.md](12-security-and-privacy.md) — local-only secrets, node-local AI ops
