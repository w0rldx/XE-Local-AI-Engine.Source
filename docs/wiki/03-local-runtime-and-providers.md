# Local Runtime & Model Providers

> Last reviewed: 2026-06-24 · Code-grounded.

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

Other abstraction-project contracts worth knowing: `IModelCapabilityClient` (runtime/version/installed/running probes), `INodeDataDirectory` (where node data lives), and the `Gguf/*` family (`IGgufModelStore`, `IGgufModelRegistry`, `IHfTokenStore`, `IHuggingFaceGgufDiscovery`, `GgufModelName`, `GgufFilePath`) which is the shared GGUF vocabulary the llama-server and HuggingFace projects both speak.

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

Three contract members:

- `EnsureRunningAsync(modelName, role, ct)` → `LlamaServerEndpoint` (reuse-or-spawn).
- `EvictAsync(modelName, role, ct)` — tree-kill if running, release its port, idempotent.
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

**Spawn internals.** `SpawnWithRestartAsync` → `SpawnOnceAsync` → `BuildLaunchSpec`. `AdmitAndAllocatePortAsync` takes the admission gate, prunes exited processes, enforces the loaded-cap (evict idle LRU via `TryEvictIdleLeastRecentlyUsed`, else throw `CapReached`), and allocates a port from the configured range.

`BuildLaunchSpec(key, exe, modelFile, port, variant)` builds the exact ordered argv:

```
-m <modelFile> --host 127.0.0.1 --port <port>
  [--n-gpu-layers <all>]   # added for any non-CPU variant
  --jinja                  # ModelRole.Chat
  --embeddings --pooling mean  # ModelRole.Embedding
```

Note the **`--host 127.0.0.1`** localhost-only bind and the deliberate `--n-gpu-layers` flag: the GPU variant only selects the GPU-*enabled* build; without this flag `llama-server` defaults to 0 offloaded layers and runs on CPU even when CUDA is detected (the documented "model loaded in RAM, CUDA detected" symptom).

### Health probe — readiness vs liveness

`LlamaServerHealthProbe` (impl of `ILlamaServerHealthProbe`) polls the `llama-server` **`/health`** endpoint over HTTP (`/health` is a sibling of the `/v1` base). `WaitForReadyAsync` polls every 250 ms until a 200 or the readiness deadline — connection-refused during warm-up is normal and retried. `CheckResponsiveAsync` is a single probe used by health aggregation.

### Eviction & reaper

- `LlamaServerSupervisorOptions`: `MaxLoadedProcesses`, `IdleTimeToLive` (default **15 min**), `PortRangeStart`/`PortRangeEnd`, `MaxRestartAttempts`. The host overrides these from node config (`AddNodeModelRuntimeExtensions.BuildSeededLlamaServerSupervisorOptions`).
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

The supervisor does **only enough** hardware probing to pick the prebuilt asset — the full VRAM/memory-fit math lives in the Model Advisor ([07-model-fit.md](07-model-fit.md)), explicitly NOT here.

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

`LlamaServerServiceCollectionExtensions.AddLlamaServerLocalModelProvider` registers the whole stack as singletons via `TryAdd*`: vendor probe, variant selector, the live catalog + installed-runtime store + update state, the binary manager (built by factory with `cacheRoot/activeTag = null` so it self-defaults), default supervisor + external-endpoint options, the launcher and health probe, and the supervisor itself (explicit factory because its ctor is internal). The host must register an `HttpClient` (`AddHttpClient`) for binary downloads and supply an `IGgufModelStore` (the HuggingFace GGUF store).

---

## 3. Satellite providers

### `Providers.Ollama` — present, de-orchestrated from Aspire dev

`OllamaLocalModelProvider` (`ProviderName = "ollama"`) still fully implements `ILocalModelProvider` over `IOllamaApiClient` (OllamaSharp): list/pull/delete/warm/unload + `CreateChatClient`/`CreateEmbeddingGenerator`. It remains a real, registered provider — but llama.cpp is the dev runtime, so **Ollama is no longer orchestrated by Aspire in dev** (see [11-hosting-and-deployment.md](11-hosting-and-deployment.md)). Notable detail: `AddOllamaLocalModelProvider` sets a short **750 ms `SocketsHttpHandler.ConnectTimeout`** so a probe against an absent Ollama daemon (desktop mode) fails fast instead of stalling on the OS connect timeout; `OllamaConnectFailureHandler` translates a fired connect-timeout (`OperationCanceledException`) into `HttpRequestException` so "Ollama unreachable" handling is uniform. The 5-minute `HttpClient.Timeout` still covers genuine long pulls. `OllamaModelCapabilityClient` implements `IModelCapabilityClient` as thin pass-throughs over the API client.

### `Providers.Capabilities` — hardware probing

`HardwareProfiler` (impl of `IHardwareProfiler` in the abstractions project) is the **full** hardware probe — CPU/RAM/GPU/VRAM facts (`HardwareProfile`, `GpuVendor`) — distinct from the minimal `IGpuVariantSelector`. It probes the environment via `IHardwareProbeEnvironment` / `IProcessProbe` and feeds the Model Advisor's memory-fit math ([07-model-fit.md](07-model-fit.md)). `CapabilitiesServiceCollectionExtensions` wires it.

### `Providers.CodexOAuth` — ChatGPT-OAuth cloud chat provider

The one **cloud** chat provider. `CodexOAuthChatClientFactory` (`ICodexOAuthChatClientFactory`) builds an `IChatClient` against the ChatGPT/Codex API authenticated by OAuth, with a shared handler chain (`SocketsHttpHandler → CodexAuthHandler`). The `Auth/*` folder holds the OAuth machinery: `CodexAuthService`, `CodexLoginCoordinator`, `CodexTokenStore` (`ICodexTokenStore`), `CodexHeaders`, `CodexTokens`. Codex-specific quirks are encoded here: `CodexResponseStoreDisabling` / `CodexStoreDisabledChatClient` force `store=false` (replaying encrypted reasoning), `CodexModelCatalog` + `CodexProviderCapabilities` declare the model/effort surface. OAuth tokens are a LOCAL secret — never returned to the browser, never logged (see [12-security-and-privacy.md](12-security-and-privacy.md)). Reasoning-effort selection and cloud↔local clamping are covered in [05-chat.md](05-chat.md).

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
              binaryManager.EnsureBinaryAsync(variant)   # live->installed->pinned, SHA-verified
              variantSelector.SelectVariantAsync()       # CUDA/Vulkan/CPU
              modelStore.ResolveModelFilePath(model)     # HF GGUF on disk
              admit + allocate localhost port (cap-checked)
              launcher.Launch(BuildLaunchSpec(...))      # OS-contained child
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
