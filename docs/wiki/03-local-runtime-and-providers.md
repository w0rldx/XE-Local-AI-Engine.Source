# Local Runtime & Model Providers

> Reviewed: 2026-10-07 · Code-grounded.

**What this page covers.** How the node runs models: the provider-neutral seams in `Providers.Abstractions`, the `Providers.LlamaServer` supervisor that spawns and tree-kills localhost-bound `llama-server` children, runtime binary acquisition (prebuilt, bring-your-own override, in-app source build), the satellite providers and cloud connections. Inference is always a host child process owned by a singleton supervisor, never a container.

**Read this if you are** changing launch flags or model loading, debugging a `llama-server` that will not start or gets evicted, touching binary acquisition, or changing cloud-vs-local routing. **Skip to** [§5](#5-end-to-end-how-a-model-gets-selected-loaded-and-served) for the end-to-end path; **reference pages** with the launch flags, launch-policy defaults, supervisor internals, inference profiles and provider deep dives are listed under [Reference pages](#reference-pages); **related pages:** [07](07-model-fit.md), [14](14-image-generation.md), [01](01-architecture-overview.md).

## Contents

- [1. The provider seam: Providers.Abstractions](#1-the-provider-seam-providersabstractions)
- [2. Providers.LlamaServer — the host inference runtime](#2-providersllamaserver--the-host-inference-runtime)
- [3. Satellite providers](#3-satellite-providers)
- [4. Providers.HuggingFace — GGUF discovery & download store](#4-providershuggingface--gguf-discovery--download-store)
- [5. End-to-end: how a model gets selected, loaded, and served](#5-end-to-end-how-a-model-gets-selected-loaded-and-served)
- [6. Cloud connections — Azure Foundry, Entra ID, and cloud-vs-local routing](#6-cloud-connections--azure-foundry-entra-id-and-cloud-vs-local-routing)
- [Related pages](#related-pages)

This page is the heart of the 2026-06-17 runtime re-architecture. It explains how XE Local AI Engine runs models through node-owned host child processes: the provider-neutral seams in `Providers.Abstractions`, the host **llama.cpp** process supervisor that spawns and tree-kills `llama-server` children, runtime-binary acquisition (prebuilt download, operator bring-your-own override, and the in-app **source build**), and the satellite providers (Ollama, HuggingFace GGUF store, capability detection, Codex OAuth cloud chat). Model *recommendation* (hardware-aware GGUF fit) is owned by [07-model-fit.md](07-model-fit.md); this page covers only how a model gets selected, loaded, and served.

The big picture: there is **no Docker** and **no container sandbox** in the inference path, and the old `HostAgent` connection layer has been deleted. Inference = a host `llama-server` child process, localhost-bound, owned by a singleton supervisor. See [01-architecture-overview.md](01-architecture-overview.md) for where this fits in the node.

> **Scoped exception.** [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md) permits Docker for Development Mode build/test/lint execution only; nothing on this page is affected, since model hosting, acquisition, embeddings and image generation keep a **driver-only** footprint with no container dependency (details: [Architecture Overview](01-architecture-overview.md#what-the-system-is)).

> **The second supervised runtime.** `llama-server` is not the only child process the node owns: `XE-Local-AI-Engine.Providers.StableDiffusionCpp` supervises `sd-server` (stable-diffusion.cpp) the same way — pinned binary acquisition, one resident daemon per model on a private loopback port range, OS-specific tree-kill containment, stale-daemon reaper. It implements `IImageRuntime`, **not** `ILocalModelProvider`, so it sits outside every seam described on this page and is documented end-to-end in [14-image-generation.md](14-image-generation.md). The one place the two runtimes meet is the shared GPU-load admission gate (§2.5).

> **The third supervised runtime.** `XE-Local-AI-Engine.Providers.WhisperCpp` supervises `whisper-server` (whisper.cpp) the same way — pinned, hash-verified binary acquisition, one resident daemon on a private loopback port range (18300–18399), OS-specific tree-kill containment, a startup stale-daemon reaper and an idle-TTL reaper. It implements neither `ILocalModelProvider` nor `IChatClient`: it publishes `IWhisperTranscriber` and `IWhisperServerSupervisor`, so it sits outside every seam described on this page. Like the image runtime it meets the others at the shared GPU-load admission gate (§2.5), and it stays out of the `CapacityService` byte ledger. See §3 below.

---

## 1. The provider seam: `Providers.Abstractions`

All application code depends on **provider-neutral contracts**, never on a provider's SDK types. The provider-specific transport types (OllamaSharp, the OpenAI adapter, the HF HTTP clients) stay inside their own provider projects.

### `ILocalModelProvider`

`XE-Local-AI-Engine.Providers.Abstractions/ILocalModelProvider.cs` is the 10-member boundary every local runtime implements (9 required + one defaulted):

| Member | Purpose |
|---|---|
| `ProviderName` | Stable key used in capability payloads and `LocalModelSelection` routing |
| `CheckHealthAsync` | Is the runtime reachable / operational? Returns `ModelProviderHealth` |
| `ListModelsAsync` | Installed models as normalized `LocalModelDescriptor` |
| `PullModelAsync` | Download/update a model, reporting `PullProgress` |
| `DeleteModelAsync` | Remove a locally installed model |
| `WarmModelAsync` | Pre-load so first-token latency is paid early |
| `GetRuntimeInfoAsync` | Default-implemented (`null`): the effective per-slot context window the runtime actually loaded (§ health probe) |
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

Other abstraction-project contracts worth knowing: `IModelCapabilityClient` (runtime-reachability and installed-model probes), `INodeDataDirectory` (where node data lives), `IProcessVramBudgetProbe` (llama.cpp's process-local VRAM budget, §2.5), `IGgufMetadataReader` (header facts incl. MoE expert count), and the `Gguf/*` family (`IGgufModelStore`, `IGgufModelRegistry`, `IHfTokenStore`, `IHuggingFaceGgufDiscovery`, `GgufModelName`, `GgufFilePath`) which is the shared GGUF vocabulary the llama-server and HuggingFace projects both speak. The same `Gguf/` folder now also holds the **quant-quality single source of truth** — `QuantLadder` (the curated best→worst quant ladder + `Q3_K_M` quality floor) and `GgufQuantQuality` (coarse `GgufQuantTier` classifier) — moved here so both the advisor's memory-fit step-down and the download picker's per-row badge read one table. The advisor side is detailed in [07-model-fit.md](07-model-fit.md).

---

## 2. `Providers.LlamaServer` — the host inference runtime

This is the default dev + production runtime. It maps `ILocalModelProvider` onto a process supervisor that owns real `llama-server` children.

### `LlamaServerLocalModelProvider`

`ProviderName` is `"llamacpp"`. It holds two collaborators: `ILlamaServerProcessSupervisor` (process lifecycle) and `IGgufModelStore` (model inventory + file resolution + pull/delete).

- `ListModelsAsync` / `PullModelAsync` / `DeleteModelAsync` delegate to the **GGUF store** — GGUF acquisition is NOT the supervisor's job. `PullModelAsync` parses the bare name via `GgufModelName.Parse` (`{repo}[:{quant}]`) into a `GgufModelRequest` and calls `store.EnsureModelAsync`.
- `WarmModelAsync` calls `supervisor.EnsureRunningAsync(model, ModelRole.Chat)`.
- `UnloadModelAsync` evicts **every** `ModelRole` (Chat, Embedding and Reranker) through `EvictAllRolesAsync` — eviction is idempotent. Interactive warm and runtime-info stay chat-only.
- `CheckHealthAsync` reports healthy iff the supervisor answered the aggregation; an empty process list means "operational, no models loaded".

### Deferred chat / embedding clients

`CreateChatClient` and `CreateEmbeddingGenerator` return deferred clients that ensure the process is running on the first request, then self-heal, hold inference leases and classify failures.

See [the reference page](reference/03-llama-server-clients.md#deferred-chat--embedding-clients) for the rest of this section.

#### Request-body patches on the llama.cpp path

MEAI's OpenAI adapter maps only part of what llama-server accepts, so `DeferredLlamaServerChatClient` patches the outbound body through `OpenAICompatibleRequestBody.Chain`. Each patch is a no-op without its trigger, so every other request stays byte-identical, and a pre-existing `ChatOptions.RawRepresentationFactory` is composed rather than dropped.

See [the reference page](reference/03-llama-server-clients.md#request-body-patches-on-the-llamacpp-path) for the rest of this section.

#### The grammar repetition bound

llama-server compiles the whole `tools` array into ONE constrained grammar up front — for any chat template without a reasoning preamble, since a reasoning model simply never enters the constrained branch, which is why this looks model-specific and is not. A repetition bound that is too large is rejected outright with `parse: error parsing grammar: number of repetitions exceeds sane defaults`, which reaches the app as HTTP 400 `Failed to initialize samplers: failed to parse grammar` and the request never reaches inference.

Two rules follow for anyone authoring a tool's parameter JSON schema.

See [the reference page](reference/03-llama-server-clients.md#the-grammar-repetition-bound) for the rest of this section.

### `LlamaServerProcessSupervisor` — process lifecycle

`ILlamaServerProcessSupervisor` is the public contract; the implementation's ctor is **internal** (it takes internal launcher/health-probe seams). It is registered as a strict **singleton** — it owns every `llama-server` child for the node and disposes them on shutdown. The reaper loop starts in the ctor.

Contract members:

- `EnsureRunningAsync(modelName, role, ct)` → `LlamaServerEndpoint` (reuse-or-spawn). The spawn/load runs **detached** from the caller's token (a shared per-key task under the shutdown token): a caller cancelling only abandons its wait; the load continues and the model becomes warm for the next send. Single-flight is preserved.
- `EvictAsync(modelName, role, ct)` — **immediate** tree-kill if running, release its port, idempotent. Used internally (profiling exclusivity, provider unload); does NOT wait for in-flight work.
- `EjectAsync(modelName, role, force, ct)` → `LlamaServerEjectOutcome` — the **operator** eject: mark evicting (no new leases), drain in-flight inference for a bounded `EjectDrainTimeout`, then tear down. Returns `Ejected` (idle/drained cleanly), `TimedOutStillBusy` (busy and not forced — **left running**, never killed silently), `ForcedWhileBusy` (`force:true` — killed anyway, run marked operator-ejected), or `NotRunning`.
- `TryAcquireInferenceLease(modelName, role)` → `ILlamaServerInferenceLease?` — a per-request lease the chat client holds so a graceful eject waits for the turn; `WasEjected` lets an interrupted request classify a force-eject drop as operator-ejected rather than a generic failure.
- `CheckHealthAsync(ct)` — aggregate every running process's health.

**Keying.** Each distinct `(model, role)` is a distinct process (`ProcessKey`) and counts against the loaded-cap. A chat process launches with `--jinja`; an embedding process with `--embeddings --pooling mean`.

See [the reference page](reference/03-llama-server-supervisor.md#llamaserverprocesssupervisor--process-lifecycle) for the rest of this section.

#### The runtime-mutation gate

`LlamaServerRuntimeMutationGate` orders ordinary ensures against the rare operator runtime mutation — a runtime install or remove, a source build, exclusive profiling. Ensures take it SHARED and run concurrently; a mutation takes it EXCLUSIVE. What an exclusive holder relies on is unchanged from the single semaphore it replaces: a mutation waits for every in-flight ensure DECISION, and no new decision starts while it holds the gate. What changed is that an ensure no longer head-of-line blocks an unrelated role behind its own liveness probe, worth up to `ReuseLivenessProbeTimeout` (2 s). Single-flight per process is not this gate's job — the supervisor's per-`(model, role)` ensure gates already provide it.

`AsyncSharedExclusiveGate` underneath is the smallest such gate the supervisor needs, written here because the BCL has no async reader/writer lock and nothing in this codebase justifies a general one. Its invariants:

1. Exclusion is mutual and complete in BOTH directions: no shared holder is admitted while an exclusive holder runs, and `EnterExclusiveAsync` does not return until every shared holder admitted before it has called `ExitShared`.
2. Shared holders never exclude each other, so one caller's slow work cannot head-of-line block another.
3. Admission is FIFO — `SemaphoreSlim`'s own ordering — so a pending exclusive acquire cannot be starved by a continuous stream of shared acquires: it is served ahead of every shared acquire that arrives after it.
4. A shared acquire holds the underlying semaphore only for an O(1) counter update, never for the caller's work. That is the whole point of the type.

Neither side is re-entrant, and an exit must be paired with a successful enter; a cancelled or faulted enter has already undone itself.

### Spawn attempt sequencing, startup capture and one-shot fallbacks

`SpawnCoreAsync` (`LlamaServerProcessSupervisor.Spawn.cs`) walks a short list of launch *candidates* — the optimized plan first, at most one safe retry, plus a context down-tier appended when the startup output classifies as OOM. Each candidate allocates its own port, launches, and either registers a `RunningProcess` or tree-kills the child and releases the port (the reserved-port set backs the cap count, so the release happens under the admission gate).

See [the reference page](reference/03-llama-server-supervisor.md#spawn-attempt-sequencing-startup-capture-and-one-shot-fallbacks) for the rest of this section.

#### Startup failure classification

`LlamaStartupFailureClassifier` classifies the bounded startup capture PER LINE and then reduces, never as a substring test over the joined buffer. Joining first lets any one line's vocabulary decide the whole diagnosis: llama.cpp raises `failed to allocate buffer for kv cache` on a genuine KV-cache allocation failure, so a joined buffer holding both that line and a `cudaMalloc failed: out of memory` line matched the compatibility branch and hid the out-of-memory verdict the context down-tier is gated on.

See [the reference page](reference/03-llama-server-supervisor.md#startup-failure-classification) for the rest of this section.

### Per-role launch flags and the pooled batch-size rule

`LlamaServerLaunchArgumentComposer.BuildLaunchSpec` emits every flag from ONE `LlamaServerLaunchProjection` of the spawn's launch shape, so the vector that reaches the process and the shape a receipt records can never drift into two independent derivations. A **null** plan (replay profiling) reproduces the supplied replay vector byte-for-byte.

See [the reference page](reference/03-llama-server-launch-flags.md#per-role-launch-flags-and-the-pooled-batch-size-rule) for the rest of this section.

#### The accepted `--spec-type` set and its capability classes

`SpeculativeDecodingSettings.ModeClasses` is the single authority for which `--spec-type` modes exist and what each one requires.

See [the reference page](reference/03-llama-server-launch-flags.md#the-accepted---spec-type-set-and-its-capability-classes) for the rest of this section.

### Launch-policy defaults: context windows, KV quantization and CPU threads

`LlamaServerLaunchPolicyOptions` holds the node-configurable context, KV-cache and CPU-thread defaults, validated at host build.

See [the reference page](reference/03-llama-server-launch-flags.md#launch-policy-defaults-context-windows-kv-quantization-and-cpu-threads) for the rest of this section.

### Health probe — readiness vs liveness

`LlamaServerHealthProbe` polls `/health` on a dedicated, resilience-free `HttpClient` against a size-aware readiness deadline.

See [the reference page](reference/03-llama-server-supervisor.md#health-probe--readiness-vs-liveness) for the rest of this section.

### Reuse-path liveness, retry classes and the two HTTP network floors

`LlamaServerSupervisorOptions.Validate()` is called by the supervisor's constructor, so a structurally invalid value surfaces at startup rather than as a runtime stall.

See [the reference page](reference/03-llama-server-supervisor.md#reuse-path-liveness-retry-classes-and-the-two-http-network-floors) for the rest of this section.

### Eviction & reaper

`LlamaServerSupervisorOptions` sets the loaded-process cap and the idle TTLs; a background reaper loop evicts exited or idle processes, and admission evicts the least-recently-used idle process to free a slot or memory.

See [the reference page](reference/03-llama-server-supervisor.md#eviction--reaper) for the rest of this section.

### No-orphan shutdown guarantee

This is the safety property the launcher exists for. `LlamaServerProcessLauncher.Launch` picks an OS-specific containment primitive so closing the handle tree-kills the whole descendant tree. The three handles live in `Providers.ProcessSupervision` and are shared with the sd-server and whisper-server launchers:

| OS | Containment | Tree-kill mechanism |
|---|---|---|
| Windows | `WindowsJobObjectProcessHandle` | Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` — disposing the job handle kills the tree |
| Linux | `LinuxProcessGroupHandle` | child launched under `setsid` (new session/process-group); teardown does `kill(-pgid)` |
| macOS / other Unix | `PlainProcessHandle` | plain process; own-tree-kill (CPU floor only — no dedicated primitive) |

Each native path is reached only under its own `OperatingSystem.Is*` guard, so no cross-OS native call leaks. Teardown is two steps in `LlamaServerIdleReaper`: `DetachProcess` removes the process from the map, retires its layer placement and releases the port, then `KillDetachedProcess` tree-kills + disposes the handle; `RemoveProcessAsync` runs both for eviction (`EvictCoreAsync`), the reaper and prune. `DisposeAsync` cancels the reaper, takes the runtime gate exclusively (`EnterExclusiveForTeardownAsync`), detaches and kills every remaining process, and disposes all gates — guaranteeing no orphaned `llama-server` survives a clean node shutdown.

### Startup orphan reap and spawn receipts

A per-runtime startup `StaleProcessReaper` kills `llama-server`, `sd-server` and `whisper-server` orphans that a hard kill of the node left behind, matched by managed binary path or, on Linux, by a spawn receipt.

See [the reference page](reference/03-llama-server-supervisor.md#startup-orphan-reap-and-spawn-receipts) for the rest of this section.

### GPU variant selection

For *asset selection*, the supervisor does **only enough** hardware probing to pick the prebuilt binary — the full VRAM/memory-fit math lives in the Model Advisor ([07-model-fit.md](07-model-fit.md)), explicitly NOT here. (The separate process-local VRAM-budget probe used by the variant recommender and the profile/benchmark services is `LlamaListDevicesProcessVramBudgetProbe`, §2.5.)

- `IGpuVendorProbe` → `ProcessGpuVendorProbe` detects the vendor (`DetectedGpuVendor`: Nvidia/Amd/Intel/none).
- `IGpuVariantSelector` → `GpuVariantSelector.SelectVariantAsync` (`Implementation/GpuVariantSelector.cs`) applies three rules in order:

```
1. operator BYO override active  -> the override's configured variant  (vendor probe skipped entirely)
2. an adopted source build       -> ICudaManagedBuildSignal.ActiveVariant  (cached flag, no store read)
3. otherwise SelectForVendor(vendor, isWindows):
     NVIDIA    -> CUDA   on Windows
               -> Vulkan on Linux   (llama.cpp ships NO prebuilt Linux CUDA asset)
     AMD/Intel -> Vulkan
     none/unknown -> CPU
```

`GpuVariant` is the resulting enum (`Cuda` / `Vulkan` / `Cpu`).

Rule 2 is what makes an in-app CUDA build usable on Linux: `ICudaManagedBuildSignal` (`Implementation/CudaManagedBuildSignal.cs`) is a process-wide volatile latch — `SetActive(variant)` on adopt and at startup seeding, `Clear()` on remove or when a serve-time validation fails — so the hot selection path never reads `installed-runtime.json`. It is deliberately **optimistic**: it does not prove the binary is on disk and hash-valid right now, because the binary manager re-validates authoritatively on every serve and clears a stale flag there. Its `Version` counter is bumped on every set/clear so a cache that memoized the selected variant recomputes after an adopt/remove: such a cache records the stamp it saw and recomputes when it differs, so an adopt or remove that flips the selection never leaves a stale memo. The counter is lock-free to read and only ever grows, and `Clear()` is called both on remove and when a serve finds the recorded build missing or invalid.

#### Vendor detection: probe order, the Windows adapter list and the single timeout model

`ProcessGpuVendorProbe` tries NVML driver presence, then `nvidia-smi`, then a platform adapter list, and degrades to the CPU floor on any failure.

See [the reference page](reference/03-runtime-binaries-and-tuning.md#vendor-detection-probe-order-the-windows-adapter-list-and-the-single-timeout-model) for the rest of this section.

### Binary manager + dynamic updater

`LlamaCppBinaryManager` resolves the `llama-server` executable from three sources in order: the operator override, an adopted source build, then a verified prebuilt asset.

See [the reference page](reference/03-runtime-binaries-and-tuning.md#binary-manager--dynamic-updater) for the rest of this section.

#### The read-only installed-binary query

`ILlamaCppBinaryManager.TryGetInstalledBinaryAsync` is the surface for diagnostics that only REPORT on the runtime — the device-inventory probe behind the hardware-profile page. A read-only GET must not have a multi-hundred-megabyte side effect on any external access profile, so those callers ask this rather than `EnsureBinaryAsync` and degrade to "unknown" on a null answer.

See [the reference page](reference/03-runtime-binaries-and-tuning.md#the-read-only-installed-binary-query) for the rest of this section.

#### The acquisition throttle rule

`RuntimeAcquisitionStatusRegistry` throttles only repeated byte updates within one phase and step; every phase change, step change and terminal status pushes immediately.

See [the reference page](reference/03-runtime-binaries-and-tuning.md#the-acquisition-throttle-rule) for the rest of this section.

### `ModelRole` and the external-endpoint option

- `ModelRole` (enum, `ModelRole.cs`): `Chat`, `Embedding`, `Reranker`. Drives both the launch flags and the process key — the three roles need mutually exclusive flags (`--jinja` / a non-`none` pooling type / `--rerank --pooling rank`), so a distinct `(model, role)` is always a distinct process and each counts against the shared loaded-cap. The reranker role serves `/v1/rerank` and backs the Knowledge Base's local cross-encoder ([15-knowledge-base.md](15-knowledge-base.md)).
- `LlamaServerExternalEndpointOptions` is an optional **hybrid attach** map: `(modelName, role) → external OpenAI-compatible base URL`. A match short-circuits `EnsureRunningAsync` entirely — the supervisor returns the configured endpoint and never owns a process for it. Empty by default (pure spawn-and-supervise); bound from node config at DI time.

### DI wiring

`AddLlamaServerLocalModelProvider` registers the whole stack as singletons, and which HTTP client each seam gets is a correctness decision.

See [the reference page](reference/03-llama-server-supervisor.md#di-wiring) for the rest of this section.

### 2.5 Inference profiles / per-machine tuning

llama.cpp launch args used to be hard-coded (the forced `-ngl 999`). They are now resolved per-spawn by the **inference optimizer**: a node explores a model once on the actual hardware, optionally benchmarks the result against a golden transcript, freezes the winning args, and replays them verbatim on every later spawn of that `(model, role, backend)` — so each box runs the model with placement that was proven on *that* box, not a one-size guess.

See [the reference page](reference/03-runtime-binaries-and-tuning.md#25-inference-profiles--per-machine-tuning) for the rest of this section.

### 2.6 In-app source builds (Linux)

On Linux the node can compile `llama-server` from source in-app and adopt it as a managed runtime, because upstream ships no prebuilt Linux CUDA build.

See [the reference page](reference/03-runtime-binaries-and-tuning.md#26-in-app-source-builds-linux) for the rest of this section.

### 2.7 Per-model extra launch arguments (operator override)

An operator can persist a raw extra launch-argument string per model; the supervisor appends it after the built spec, so llama.cpp's last-wins parsing lets it override a default.

See [the reference page](reference/03-llama-server-launch-flags.md#27-per-model-extra-launch-arguments-operator-override) for the rest of this section.

### 2.8 Process context allocation and the fallback window

`ProcessContextAllocationResolver` picks the `-c` window a spawn launches with, walking the chat context tiers largest-first against the fp16 KV estimate.

See [the reference page](reference/03-llama-server-launch-flags.md#28-process-context-allocation-and-the-fallback-window) for the rest of this section.

---

## 3. Satellite providers

### `Providers.Ollama` — present, de-orchestrated from Aspire dev

`OllamaLocalModelProvider` (`ProviderName = "ollama"`) still fully implements `ILocalModelProvider` over `IOllamaApiClient` (OllamaSharp): list/pull/delete/warm/unload + `CreateChatClient`/`CreateEmbeddingGenerator`. It remains a real, registered provider — but llama.cpp is the dev runtime, so **Ollama is no longer orchestrated by Aspire in dev** (see [11-hosting-and-deployment.md](11-hosting-and-deployment.md)). Notable detail: `AddOllamaLocalModelProvider` sets a short **750 ms `SocketsHttpHandler.ConnectTimeout`** so a probe against an absent Ollama daemon (desktop mode) fails fast instead of stalling on the OS connect timeout; `OllamaConnectFailureHandler` translates a fired connect-timeout (`OperationCanceledException`) into `HttpRequestException` so "Ollama unreachable" handling is uniform. The 5-minute `HttpClient.Timeout` still covers genuine long pulls. `OllamaModelCapabilityClient` implements `IModelCapabilityClient` as thin pass-throughs over the API client.

### `Providers.WhisperCpp` — the supervised speech-to-text runtime

`WhisperServerProcessSupervisor` owns one resident `whisper-server` child, whose readiness is a port accept followed by the health route.

See [the reference page](reference/03-satellite-and-cloud-providers.md#providerswhispercpp--the-supervised-speech-to-text-runtime) for the rest of this section.

### `Providers.Capabilities` — hardware probing

`HardwareProfiler` (impl of `IHardwareProfiler` in the abstractions project) is the **full** hardware probe — CPU/RAM/GPU/VRAM facts (`HardwareProfile`, `GpuVendor`) — distinct from the minimal `IGpuVariantSelector`. It probes the environment via `IHardwareProbeEnvironment` / `IProcessProbe` and feeds the Model Advisor's memory-fit math ([07-model-fit.md](07-model-fit.md)). `CapabilitiesServiceCollectionExtensions` wires it.

**Live memory sampler.** `ILiveMemorySampler` (`Providers.Abstractions/Capabilities`) → `LiveMemorySampler` serves `GET model-fit/resources` for the top-bar gauge: whole-machine RAM (`/proc/meminfo` on Linux, `GlobalMemoryStatusEx` on Windows) and one `nvidia-smi` row per GPU, with no device names. Any failure or a non-NVIDIA host is an empty GPU list, meaning unknown, never zeros. One probe serves every caller for about 2 seconds, in flight or complete, so several polling tabs cost one spawn. Three memory readings exist and each has one job:

- **Cached hardware profile** (`IHardwareProfiler`): the fit and admission baseline, cached for the process lifetime until a caller forces a refresh.
- **Process VRAM budget** (`IProcessVramBudgetProbe`, §2.5): what a llama.cpp process may allocate, from `--list-devices`.
- **Live sampler**: operator display only. It never reads or writes the profiler cache, and never runs `--list-devices`. A poller that forced a profiler refresh instead would spawn a probe per poll and overwrite the baseline fit depends on.

On WSL2 the RAM reading describes the Linux VM, not the Windows host.

### `Providers.CodexOAuth` — ChatGPT-OAuth cloud chat provider

The one **cloud** chat provider. `CodexOAuthChatClientFactory` (`ICodexOAuthChatClientFactory`) builds an `IChatClient` against the ChatGPT/Codex API authenticated by OAuth, with a shared handler chain (`SocketsHttpHandler → CodexAuthHandler`). The `Auth/*` folder holds the OAuth machinery: `CodexAuthService`, `CodexLoginCoordinator`, `CodexTokenStore` (`ICodexTokenStore`), `CodexHeaders`, `CodexTokens`. Codex-specific quirks are encoded here: `CodexResponseStoreDisabling` / `CodexStoreDisabledChatClient` force `store=false` (replaying encrypted reasoning), `CodexModelCatalog` + `CodexProviderCapabilities` declare the model/effort surface. OAuth tokens are a LOCAL secret — never returned to the browser, never logged (see [12-security-and-privacy.md](12-security-and-privacy.md)). Reasoning-effort selection and cloud↔local clamping are covered in [05-chat.md](05-chat.md).

### `Providers.OpenAICompat` — operator-registered external endpoints

One multiplexer `ILocalModelProvider` (`ProviderName = "external"`) serving every connection the operator registered, dispatched by parsing `ext:{connectionId}/{wireId}` through `IExternalProviderRegistry`. Capabilities are DECLARED, never probed: only `POST /v1/chat/completions` is universal across OpenAI-compatible servers, and none of them advertises tool, vision or reasoning support in a way that survives llama.cpp / vLLM / LM Studio / a hosted API alike. The connect-time probe is `GET {base}/models` and nothing else. A connection may carry operator-defined request headers (a gateway's project header, a secret token under its own name), sent on chat, health and the probe; they made the store schema 3.

See [the reference page](reference/03-satellite-and-cloud-providers.md#providersopenaicompat--operator-registered-external-endpoints) for the rest of this section.

### External connections: the store, the registry cache, and the reconciler

The encrypted `external-providers.enc` file is the source of truth for a saved connection; an idempotent reconciler repairs the provider-map rows and the tool allow-list at startup and after every save or delete.

See [the reference page](reference/03-satellite-and-cloud-providers.md#external-connections-the-store-the-registry-cache-and-the-reconciler) for the rest of this section.

### The connect-time probe

`IExternalProviderProbeService` runs one server-side `GET {normalized-base}/models` and reports a verdict rather than an exception. It runs on the NODE, never in the browser: an operator endpoint on a LAN address serves no CORS headers, so a fetch from the settings page would fail for a reason that has nothing to do with whether the endpoint works, and the node is also the only side that can read the stored API key.

See [the reference page](reference/03-satellite-and-cloud-providers.md#the-connect-time-probe) for the rest of this section.

### The model catalog's five sources

`LocalModelCatalogService` gathers the chat picker's whole catalog from five sources that each degrade on their own — Ollama, the installed GGUF registry, a Codex session, a stored Azure Foundry connection, and the operator's external OpenAI-compatible connections (`IExternalProviderRegistry`). No source failure ever fails the catalog: an unreadable encrypted external store yields no external entries, exactly as an unreadable GGUF registry yields no GGUF entries.

See [the reference page](reference/03-satellite-and-cloud-providers.md#the-model-catalogs-five-sources) for the rest of this section.

---

## 4. `Providers.HuggingFace` — GGUF discovery & download store

`HuggingFaceGgufStore` (internal, impl of `IGgufModelStore`) is the model inventory + acquisition backend the llama-server provider delegates to. It deliberately **does not depend on the LlamaServer project** — it tags descriptors with the agreed `llamacpp` provider-name constant.

Responsibilities and collaborators:

- **Discovery** — `HuggingFaceGgufDiscovery` (`IHuggingFaceGgufDiscovery`) enumerates GGUF files/quants for a repo via `HfHubClient`.
- **Download** — `HfDownloadClient` fetches the chosen GGUF to disk (atomic temp-file → rename for offline reuse), serializing concurrent `EnsureModelAsync` for the same name with a per-name gate, guarded by `IFreeSpaceProbe`.
- **Registry** — `GgufModelRegistry` (`IGgufModelRegistry`) is the on-disk record (`GgufModelRegistryEntry`: model name, local path, size, sha, downloaded-at); `ResolveModelFilePathAsync` / `ListInstalledModelsAsync` read it.
- **Header facts** — `GgufHeaderReader` reads the GGUF header once per model; `GgufCapabilityDetector.Detect` classifies tool/reasoning capability **deterministically from the embedded Jinja chat template** (`tokenizer.chat_template`) — tool markers (`tool_calls`, `function_call`, `tools`) and reasoning markers (`<think`, `enable_thinking`, `reasoning_content`). This is why XE needs no Ollama `/api/show` probe for a GGUF (a GGUF has no Ollama entry, and desktop mode runs no Ollama daemon). The same pass answers a THIRD question — `ReasoningBudgetEnforceable`: does the template render a literal reasoning END marker (`</think>`, `</thinking>`, gemma-4's `<channel|>`)? That is the shape llama.cpp turns into the non-empty think-end-tag set its `reasoning_budget_tokens` gate requires; without it the server accepts the budget and silently ignores it, so the node omits the field instead. It rides `LocalModelDescriptor` and the model-list DTO, and is also computed at import (`GgufImportInspector.Classify`, which raises an operator warning for a graded-but-unenforceable template). Its default is `true` everywhere — only a positively-detected closing-tag-less template turns a cap off. See [05-chat.md](05-chat.md#thinking-budget-llamacpp-and-where-it-is-enforceable) for the enforcement evidence. Results are cached by `(path, size, downloadedAt)`; a header-read failure for one model never sinks the list (yields `GgufHeaderFacts.Empty`).
- **Footprint facts** — `ResolveModelFootprintFactsAsync` exposes weight/KV inputs (the public seam consumed by the capacity/advisor layer) again from a single cached header read.
- **Token** — `IHfTokenStore` holds the optional HuggingFace token (a LOCAL secret) for gated repos. Downloads send it, and so do the advisor's remote header reads (`GgufHeaderReader`), so a gated repo with a token gets its header-based estimate.

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
              binaryManager.EnsureBinaryAsync(variant)   # BYO override -> adopted source build
                                                         #   -> prebuilt (live->installed->pinned), SHA-verified
              profileResolver.ResolveAsync(model, role, variant)  # frozen replay args OR explore(--fit on)
              admit + allocate localhost port (cap-checked)
              launcher.Launch(BuildLaunchSpec(..., resolved))  # OS-contained child, --parallel 1 --no-warmup pinned
              healthProbe.WaitForReadyAsync(/health)
       -> build OpenAI adapter over the localhost endpoint
       -> stream tokens
  -> idle 15 min OR explicit unload OR node shutdown -> tree-kill, port released
```

Every `llama-server` child is same-user, unprivileged, and `127.0.0.1`-bound — the node never exposes a model port outward; the runtime layer here is purely local. See [09-api-and-hubs.md](09-api-and-hubs.md) for the local admin endpoints that drive warm/unload/health and [12-security-and-privacy.md](12-security-and-privacy.md) for the trust boundary.

---

## 6. Cloud connections — Azure Foundry, Entra ID, and cloud-vs-local routing

`Client.Application/Services/CloudProviders` holds the stored Azure Foundry connection, its Entra ID sign-ins, and the per-send decision of whether a turn goes to a cloud provider or to the local runtime. Codex's own OAuth machinery lives in `Providers.CodexOAuth` (§3).

### Entra ID sign-in and the token cache

`EntraCachePersistenceFailure.IsPersistenceUnavailable` recognises an unavailable OS token cache that Azure.Identity reports as a wrapped `MsalCachePersistenceException`.

See [the reference page](reference/03-satellite-and-cloud-providers.md#entra-id-sign-in-and-the-token-cache) for the rest of this section.

### Azure Foundry: the two wire surfaces

A stored connection targets one of two surfaces, and the credential plumbing differs between them.

See [the reference page](reference/03-satellite-and-cloud-providers.md#azure-foundry-the-two-wire-surfaces) for the rest of this section.

### Azure Foundry error translation

`AzureFoundryErrorTranslatingChatClient` maps transport exceptions onto the content-filter, `AuthFailed` and `Transport` error kinds.

See [the reference page](reference/03-satellite-and-cloud-providers.md#azure-foundry-error-translation) for the rest of this section.

### The active cloud selection

`ActiveCloudChatClientFactory` answers, per send, which cloud client (if any) serves this turn. An explicit `ChatOptions.ModelId` always wins over the node default, which is what lets the chat model dropdown route ONE send to Azure or to a specific Codex model independent of what is signed in.

See [the reference page](reference/03-satellite-and-cloud-providers.md#the-active-cloud-selection) for the rest of this section.

### Trust is not routing

The factory answers ROUTING: where this send goes now and through which provider. Capacity, the warmer, usage attribution, dev mode's model runner and `RuntimeChatClient` ask it that. TRUST, "does sending to this model leave the node", is answered by `IModelTrustResolver` alone, and every policy gate asks it: the tool offer (`Classify`, synchronous), the sub-agent spawn guard, `CloudModelResolver`, and the capability resolver's locality bit. The rule: a Codex catalog id is cloud whether or not a session is signed in (without one it has no local runtime either); another non-external id is cloud when the routing snapshot selects a provider for it; an `ext:` id is cloud unless its connection is positively declared local; and any lookup failure, registry or routing snapshot, is `Unresolved`, which every gate treats as cloud. `ModelTrustAuthorityGuardTests` keeps a new direct `CodexModelCatalog.IsCodexModel(` call or a private trust-boundary helper from growing back in the host and application projects.

### The local routing client

`ModelRoutingLocalChatClient` is the local branch behind `RuntimeChatClient`. Per request it resolves `ChatOptions.ModelId` to a provider through `ILocalModelProviderResolver` and asks that provider for a model-specific chat client. For llama-server the provider hands back a *deferred* client that ensure-runs the right per-model process on first use; for Ollama it hands back the single-daemon client with a `ModelId` hot-swap. The router builds the request's effective `ModelId` once and caches the client per `(provider, model)`, so repeated sends to one model reuse a single deferred client — which owns the single-flight cold-start.

`ILocalModelProviderResolver` composes two lookups: `ModelName → ProviderName` over the persisted per-model→provider map (with a configured default for unmapped models), then `ProviderName → ILocalModelProvider` over the registered provider set. The first lookup is memoized in a short-TTL bounded cache because provider resolution runs several times per chat turn (capability gating, model resolution, one per orchestration participant) and the map is effectively write-once per model — a GGUF is always `llamacpp`, an Ollama model always `ollama`.

---

## Related pages

- [01-architecture-overview.md](01-architecture-overview.md) — where the runtime fits in the node
- [02-project-layout.md](02-project-layout.md) — the `Providers.*` project inventory
- [07-model-fit.md](07-model-fit.md) — hardware-aware GGUF recommendation & the full hardware profiler
- [05-chat.md](05-chat.md) — chat/reasoning over these chat clients
- [08-data-and-persistence.md](08-data-and-persistence.md) — GGUF files, registry, installed-runtime state
- [09-api-and-hubs.md](09-api-and-hubs.md) — local endpoints/hubs that drive the runtime (incl. the source-build routes + hubs)
- [11-hosting-and-deployment.md](11-hosting-and-deployment.md) — Aspire dev orchestration & desktop mode
- [12-security-and-privacy.md](12-security-and-privacy.md) — local-only secrets, node-local AI ops
- [14-image-generation.md](14-image-generation.md) — `sd-server`, the node's second supervised runtime
- [18-training.md](18-training.md) — `Providers.Training`, the uv-provisioned Python fine-tuning runtime the node spawns as a supervised child process (Linux only)
- [19-compute-tools.md](19-compute-tools.md) and [ADR 0016](../adr/0016-managed-python-shared-uv-layer.md) — `Providers.Python`, the shared managed-Python layer (pinned uv acquisition, `ManagedPythonToolchain`) that training and `run_python` sit on

### Reference pages

- [LlamaServer Chat and Embedding Clients](reference/03-llama-server-clients.md) — lists the deferred chat and embedding clients' self-heal, lease and failure contracts, every request-body patch on the llama.cpp path, and the grammar repetition-bound rules.
- [LlamaServer Supervisor Internals](reference/03-llama-server-supervisor.md) — lists the supervisor's `EnsureRunningAsync` flow and spawn internals, spawn sequencing and fallbacks, startup failure classes, health and liveness probes, eviction and reaper rules, orphan reaping and DI wiring.
- [LlamaServer Launch Flags](reference/03-llama-server-launch-flags.md) — lists the per-role launch flags, the accepted speculative-decoding modes, the launch-policy defaults, per-model extra arguments and the process context allocation.
- [Runtime Binaries and Tuning](reference/03-runtime-binaries-and-tuning.md) — lists GPU vendor detection, the llama.cpp binary manager and updater, runtime-acquisition status, inference profiles and the in-app source build.
- [Satellite and Cloud Providers](reference/03-satellite-and-cloud-providers.md) — lists the whisper.cpp runtime, the external OpenAI-compatible connections, the model catalog sources, and the Azure Foundry and Entra ID details.
