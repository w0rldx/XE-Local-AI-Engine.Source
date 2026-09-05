# Local Runtime & Model Providers

> Baseline: `65de769ded3eb6e7b59eabb5daf6a8d0b89531ba` · Reviewed: 2026-08-17 · Code-grounded.

This page is the heart of the 2026-06-17 runtime re-architecture. It explains how XE Local AI Engine runs models through node-owned host child processes: the provider-neutral seams in `Providers.Abstractions`, the host **llama.cpp** process supervisor that spawns and tree-kills `llama-server` children, runtime-binary acquisition (prebuilt download, operator bring-your-own override, and the in-app **source build**), and the satellite providers (Ollama, HuggingFace GGUF store, capability detection, Codex OAuth cloud chat). Model *recommendation* (box-aware GGUF fit) is owned by [07-model-fit.md](07-model-fit.md); this page covers only how a model gets selected, loaded, and served.

The big picture: there is **no Docker** and **no container sandbox** in the inference path, and the old `HostAgent` connection layer has been deleted. Inference = a host `llama-server` child process, localhost-bound, owned by a singleton supervisor. See [01-architecture-overview.md](01-architecture-overview.md) for where this fits in the node.

> **What "no Docker" now means precisely.** [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md) (Accepted 2026-07-29) narrows the epic's "no Docker anywhere" to **no Docker on the inference path**, permitting it for **Development Mode build/test/lint execution only** as a stopgap ahead of MXC. Nothing on this page changes: model hosting, model acquisition, embeddings and image generation carry no container dependency, run with a **driver-only** footprint, and are not to acquire one. Development Mode's execution sandbox is a separate feature documented in [04-agent-mode.md](04-agent-mode.md) and [12-security-and-privacy.md](12-security-and-privacy.md).

> **The second supervised runtime.** `llama-server` is not the only child process the node owns: `XE-Local-AI-Engine.Providers.StableDiffusionCpp` supervises `sd-server` (stable-diffusion.cpp) the same way — pinned binary acquisition, one resident daemon per model on a private loopback port range, OS-specific tree-kill containment, stale-daemon reaper. It implements `IImageRuntime`, **not** `ILocalModelProvider`, so it sits outside every seam described on this page and is documented end-to-end in [14-image-generation.md](14-image-generation.md). The one place the two runtimes meet is the shared GPU-load admission gate (§2.5).

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

Other abstraction-project contracts worth knowing: `IModelCapabilityClient` (runtime/version/installed/running probes), `INodeDataDirectory` (where node data lives), `IProcessVramBudgetProbe` (llama.cpp's process-local VRAM budget, §2.5), `IGgufMetadataReader` (header facts incl. MoE expert count), and the `Gguf/*` family (`IGgufModelStore`, `IGgufModelRegistry`, `IHfTokenStore`, `IHuggingFaceGgufDiscovery`, `GgufModelName`, `GgufFilePath`) which is the shared GGUF vocabulary the llama-server and HuggingFace projects both speak. The same `Gguf/` folder now also holds the **quant-quality single source of truth** — `QuantLadder` (the curated best→worst quant ladder + `Q3_K_M` quality floor) and `GgufQuantQuality` (coarse `GgufQuantTier` classifier) — moved here so both the advisor's memory-fit step-down and the download picker's per-row badge read one table. The advisor side is detailed in [07-model-fit.md](07-model-fit.md).

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

**`EnsureRunningAsync` flow** (`LlamaServerProcessSupervisor.cs`):

```
1. External-endpoint short-circuit: _externalEndpoints.Resolve(model, role)
   -> if configured, return LlamaServerEndpoint pointing at it. NO spawn, NO supervision.
2. Fast path: an already-running, live process for the key is reused
   (MarkUsed timestamp) WITHOUT taking the spawn gate.
3. Single-flight: per-key SemaphoreSlim ensures concurrent callers spawn exactly once.
   Re-check under the gate; reap any crashed/exited lingering process; then
   SpawnWithRestartAsync (linear backoff up to MaxRestartAttempts).
```

**Spawn internals.** `SpawnWithRestartAsync` → `SpawnOnceAsync` → `SpawnCoreAsync` → `BuildLaunchSpec`. `SpawnOnceAsync` (`LlamaServerProcessSupervisor.cs`) no longer builds args itself: it hands `SpawnCoreAsync` a resolver delegate `(variant, ct) => _profileResolver.ResolveAsync(model, role, variant, ct)` that returns the launch arguments for this `(model, role, backend)`. `SpawnCoreAsync` resolves the model file + variant + binary, awaits that delegate **before** taking the admission gate (so a slow profile read never stalls admission for other keys), then `AdmitAndAllocatePortAsync` takes the admission gate, prunes exited processes, enforces the loaded-cap (evict idle LRU via `TryEvictIdleLeastRecentlyUsed`, else throw `CapReached`), and allocates a port.

`BuildLaunchSpec(key, exe, modelFile, port, variant, resolved)` builds the exact ordered argv from the resolved arguments:

```
-m <modelFile> --host 127.0.0.1 --port <port>
  --parallel 1             # pinned on EVERY spawn (single-slot serving)
  --no-warmup              # pinned on EVERY spawn (skip empty-run warmup)
  <context/placement/thread args>   # from the launch policy — see below
  --jinja                  # ModelRole.Chat
  --embeddings --pooling mean  # ModelRole.Embedding
  --rerank --pooling rank      # ModelRole.Reranker
```

Two flags are pinned on **every** spawn:

- **`--parallel 1`** forces single-slot serving. Without it `llama-server` auto-selects `n_parallel=4`, which reserves 4× the KV cache and starves the auto-fit weight offload — a model that would fit on the GPU spills weights to system RAM for KV slots it never uses and runs slow on the CPU.
- **`--no-warmup`** skips the empty-run warmup (45–110 s on a large model) that would otherwise overrun the readiness budget and tree-kill the half-ready process in a respawn loop (observed as a chat inter-chunk stall and an explore "did not become ready in time").

**Context, KV/flash-attention, and CPU threads now come from a central launch policy** (`LlamaServerLaunchPolicy` + `LlamaServerLaunchPolicyOptions`, AUD4-02/05/17). `BuildLaunchSpec` takes a `LlamaServerLaunchPlan` the policy produced (a **null** plan for operator profiling = no policy interference). Precedence, highest first: **frozen inference profile replayed verbatim** (never overridden) > per-send/user config > role defaults. Emission by variant + explore/replay mode:

- **GPU explore** — `--fit on --metrics` (auto-fit chooses layer/expert placement and prints it for capture) **plus** the policy's deterministic `-c <ctx>` (auto-fit respects an explicit `-c` and fits ngl/batch around it) **plus** the KV-cache quant + flash attention optimization `-fa on -ctk q8_0 -ctv q8_0`. `-c`/`-fa`/`-ctk`/`-ctv` are not *placement* flags, so `--fit` stays active.
- **GPU replay** — the frozen/explored profile's explicit args verbatim: `-c <ctx>` plus `-ngl/-ts/-ot` and matched `-ctk/-ctv` with `--flash-attn on`. `--fit` is intentionally absent (an explicit placement flag disables it). The policy leaves context/KV to the profile.
- **CPU** — `-c <ctx>` (deterministic; previously the CPU variant emitted **no** `-c` ⇒ full-train-ctx KV in RAM) **plus** the CPU thread policy `-t`/`-tb` (physical-core estimate minus a host reserve). **No** `--fit`/`--metrics`/`-ngl`/`-ctk` — a frozen GPU profile does not transfer to a CPU spawn, and KV stays f16 / flash-attention auto.

Context allocation in the composed application is capacity-tiered: chat selects the largest stable dual-axis tier from **65536/32768/16384/8192/4096/2048**, while embedding/reranker use **2048**; each is capped and aligned to the model's GGUF train context. The `ChatContextTokens=16384` option is the provider-only fallback when the application capacity resolver is not composed, not a fixed shipping-app default. **KV-quant one-shot fallback:** if the optimized GPU spawn can't reach readiness, the supervisor retries once with the safe config (no `-ctk/-ctv`, `-fa auto`) and — only when the safe retry succeeds — records the fallback per (backend, KV type) in `llama-launch-fallback.json` (`ILlamaServerLaunchFallbackStore`) so later spawns skip the known-bad config while the same backend's other KV types keep working; legacy un-keyed entries written before that keying are ignored and dropped on the first read. The file is USER-level and shared by every node process on the box, so a write re-reads and merges under an OS lock held on a sibling `llama-launch-fallback.json.lock` (never on the state file itself, or the atomic replace over it would fail on Windows) and — while that lock is held — sibling node processes cannot lose each other's verdict. Acquisition is bounded (a few short retries); a write that could not take the lock proceeds unlocked, logs a Warning, and can still lose a concurrent sibling write. The **`--host 127.0.0.1`** localhost-only bind is unchanged. See [§2.5 Inference profiles / per-machine tuning](#25-inference-profiles--per-machine-tuning) for where the frozen `resolved` args come from.

### Health probe — readiness vs liveness

`LlamaServerHealthProbe` (impl of `ILlamaServerHealthProbe`) polls the `llama-server` **`/health`** endpoint over HTTP (`/health` is a sibling of the `/v1` base). `WaitForReadyAsync` polls every 250 ms until a 200 or the readiness deadline — connection-refused during warm-up is normal and retried. `CheckResponsiveAsync` is a single probe used by health aggregation. The readiness deadline is **size-aware** (`LlamaServerSupervisorOptions.ResolveReadinessTimeout(bytes)` — base + per-GiB extension above a threshold, capped), not a fixed constant, so a large model gets proportionally longer to load. The probe runs on a **dedicated, resilience-free `HttpClient`** with a ~1 s per-attempt bound: routing it through the app's `IHttpClientFactory` inherited the standard resilience handler's exponential retries and detected readiness up to ~5 s late.

`TryReadEffectiveContextTokensAsync` (AUD4-02) reads the server's **`/props`** `default_generation_settings.n_ctx` once after readiness — the effective per-slot context window the server actually loaded (the launched `-c` as clamped). The supervisor stores it on the running process; `GetRuntimeInfo(model, role)` / `ILocalModelProvider.GetRuntimeInfoAsync` expose it so the invocation runner can size **both** context budgeters (outer `TurnPolicy.ContextCapacityTokens` and inner `num_ctx`) against the real window, and the chat context-usage meter can show it (`LocalModelDetailsResponse.EffectiveContextTokens`). Best-effort — a `/props` failure just leaves the effective context unknown (the app falls back to its default window).

### Eviction & reaper

- `LlamaServerSupervisorOptions`: `MaxLoadedProcesses`, `IdleTimeToLive` (default **15 min**), `PortRangeStart`/`PortRangeEnd`, `MaxRestartAttempts`, plus the Audit-4 readiness/eject knobs — `ReadinessBaseTimeout` (default 120 s), `ReadinessTimeoutModelSizeThresholdGiB`/`ReadinessTimeoutSecondsPerGiB`/`ReadinessTimeoutCap` (size-aware deadline), `MaxReadinessTimeoutRetries` (default 1 — a readiness timeout is retried at most this many times, NOT `MaxRestartAttempts`), and `EjectDrainTimeout` (bounded graceful-eject drain). `Validate()` fails fast on structurally invalid values. The host overrides cap/TTL/launch-flag knobs from node config (`AddNodeModelRuntimeExtensions.BuildSeededLlamaServerSupervisorOptions`).
- `ReapIdleLoopAsync` runs on a background loop (interval = TTL/4, min 1 s) and evicts processes idle beyond `IdleTimeToLive` or already exited.
- `TryEvictIdleLeastRecentlyUsed` runs synchronously under the admission gate to free a slot for a new admission. It prefers exited or past-TTL entries, may let an unleased in-window embedding/reranker process yield to a new load, and never selects an in-window chat process or a live leased/profiling-pinned process.

The supervisor's present lifecycle boundaries and remaining concurrency risks are mapped in the read-only
[Llama server process supervisor decomposition report](../audits/2026-08-22-llama-server-process-supervisor-decomposition.md).
That report authorizes no production refactor; the existing launch composer, idle reaper, port allocator, and runtime
mutation gate remain the implemented seams.

### No-orphan shutdown guarantee

This is the safety property the launcher exists for. `LlamaServerProcessLauncher.Launch` picks an OS-specific containment primitive so closing the handle tree-kills the whole descendant tree:

| OS | Containment | Tree-kill mechanism |
|---|---|---|
| Windows | `WindowsJobObjectProcessHandle` | Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` — disposing the job handle kills the tree |
| Linux | `LinuxProcessGroupHandle` | child launched under `setsid` (new session/process-group); teardown does `kill(-pgid)` |
| macOS / other Unix | `PlainProcessHandle` | plain process; own-tree-kill (CPU floor only — no dedicated primitive) |

Each native path is reached only under its own `OperatingSystem.Is*` guard, so no cross-OS native call leaks. `TeardownProcess` (called from `DisposeAsync`, the reaper, eviction, and prune) removes the process from the map, tree-kills + disposes the handle, and releases the port. `DisposeAsync` cancels the reaper, tears down every remaining process, and disposes all gates — guaranteeing no orphaned `llama-server` survives a clean node shutdown.

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

Rule 2 is what makes an in-app CUDA build usable on Linux: `ICudaManagedBuildSignal` (`Implementation/CudaManagedBuildSignal.cs`) is a process-wide volatile latch — `SetActive(variant)` on adopt and at startup seeding, `Clear()` on remove or when a serve-time validation fails — so the hot selection path never reads `installed-runtime.json`. It is deliberately **optimistic**: it does not prove the binary is on disk and hash-valid right now, because the binary manager re-validates authoritatively on every serve and clears a stale flag there. Its `Version` counter is bumped on every set/clear so a cache that memoized the selected variant recomputes after an adopt/remove.

### Binary manager + dynamic updater

`ILlamaCppBinaryManager` → `LlamaCppBinaryManager` (a partial class split across `LlamaCppBinaryManager.cs`, `.Override.cs`, `.ManagedCuda.cs`) resolves the `llama-server` executable for the selected `GpuVariant`. **Three acquisition sources exist**, checked in this order by `EnsureBinaryAsync` (`LlamaCppBinaryManager.cs`):

| # | Source | When it wins | Verification |
|---|---|---|---|
| 1 | **Operator BYO override** | `LlamaServerRuntimeOverrideOptions.IsActive` — i.e. `XE_LLAMACPP_SERVER_PATH` is set in the process environment | Path/perms/exec/ownership + smoke test + GPU-device check at acquisition; **no** SHA256 pin (there is no publisher digest for a local binary) |
| 2 | **Adopted source build** | `installed-runtime.json` carries a non-empty `SourceBuildPath` | Full path-chain perms walk + recompare of the SHA256 recorded at adoption, **on every serve** |
| 3 | **Verified prebuilt asset** | neither of the above | SHA256 verified against the pin table or the publisher digest before extraction |

Sources 1 and 2 short-circuit acquisition completely — no download, no cache write, no `installed-runtime.json` mutation. Both are constrained by a **no-silent-CPU invariant**: a configured-but-broken override, or a recorded source build that is missing/invalid/variant-mismatched, throws a sanitized failure (`ManagedSourceBuildUnavailableMessage`) rather than falling through to a CPU binary that would then be launched with GPU placement flags. For a recorded-but-invalid source build the record and the cached signal are cleared first, so the node self-heals to the normal path on the next serve.

**The BYO override** (`Configuration/LlamaServerRuntimeOverrideOptions.cs`) is built **only** from process environment variables — `XE_LLAMACPP_SERVER_PATH` (absolute path) and `XE_LLAMACPP_VARIANT` (`cpu`/`cuda`/`vulkan`, default `cuda`) — via `FromEnvironment()`. It is never bound from `IConfiguration`, the node-settings store, or any request DTO: a lower-trust write to that path would be arbitrary-binary execution at app privilege, and skipping the SHA256 pin is sound *only* under that containment. A set-but-unparseable variant fails fast at startup. The binary is served as the override's **own** declared variant, never the caller-passed one.

**3-tier tag resolution** for the prebuilt path (`ResolveActiveTagAsync`, `LlamaCppBinaryManager.cs`):

1. **Live** — `GitHubLlamaCppReleaseCatalog` (`ILlamaCppReleaseCatalog`) queries the `ggml-org/llama.cpp` GitHub Releases API for the recommended tag. Best-effort: any network failure (DNS/connect/timeout/rate-limit) is treated as "offline" and falls through; acquisition never depends on the network.
2. **Installed** — `IInstalledRuntimeStore` / `InstalledRuntimeStore` reads `installed-runtime.json` (the tag actually on disk, written only after a verified, smoke-tested install). Atomic temp-file write, owner-only `0600` on non-Windows, tolerant deserialize (corrupt → null). `InstalledRuntimeState` (`Contracts/IInstalledRuntimeStore.cs`) is `(Tag, Asset, Sha256, Variant, InstalledAtUtc)` plus six **optional trailing** source-provenance fields — `SourceBuildPath`, `SourceRepository`, `SourceCommit`, `SourceRevisionMode`, `SourceRequestedCommit`, `SourceSelection`. They are optional positionals precisely so an older file deserializes with nulls and needs no migration step. **`SourceBuildPath` presence is the single signal that the record describes a source build**; readers must key off it (or the wire `isSourceBuild` flag) and never parse the sentinel `Asset` value `(source-build:cuda)`.
3. **Pinned floor** — `LlamaCppReleasePins` (`LlamaCppReleasePins.PinnedTag`; **read the constant, do not trust a tag quoted in prose** — this page has already gone stale against it once). The pin table keys `(OS, arch, GpuVariant)` → `(AssetName, Sha256, ServerRelativePath)`; SHA256 digests come from the GitHub release-assets `digest` field (llama.cpp publishes no `.sha256` sidecars). The offline last-resort and the asset-name template source. The same file also pins `PinnedSourceCommitSha` — the exact upstream commit `PinnedTag` resolves to — which is what the source build verifies its clone against; **re-pin both together**, along with `StoredNodeSettings.DefaultRecommendedLlamaCppTag`, which is an independent literal in a layer that may not reference this one and is the value the UI shows as "Recommended".

Prebuilt acquisition: resolve tag → resolve pin → reuse cached binary if present → else download, **verify SHA256**, extract under `{cacheRoot}/llama.cpp/{tag}/{variantSlug}`. A **GPU** variant must resolve a genuine `(os, arch, variant)` row via `TryResolveExact`; plain `Resolve()` would substitute the CPU floor, and serving a CPU archive as a GPU `LlamaBinary` would make the supervisor emit GPU placement flags against a CPU build. A missing GPU prebuilt therefore **throws** the sanitized "no prebuilt for this OS and CPU architecture" — only the CPU variant uses `Resolve` (its exact pin *is* the floor). Windows CUDA additionally pairs a `cudart-…` companion archive before the binary is served (`EnsureCudartRuntimeAsync`); a CUDA build without it silently degrades to CPU-only, so a cudart failure deletes the half-CUDA variant dir and throws.

**First-run acquisition visibility.** `FirstRunModelProvisioningService` publishes `DetectingGpu` before its hardware
probe; `LlamaCppBinaryManager` then publishes `Downloading → Verifying → Extracting → Completed | Failed` through
`IRuntimeAcquisitionStatusRegistry` only when real cache-miss/install work occurs. Cache hits and operator/source
overrides stay silent. The registry owns one monotonic, sanitized snapshot and throttles
byte-progress pushes through `RuntimeAcquisitionEventPublisher` / `RuntimeAcquisitionHub`; the read-only
`GetRuntimeAcquisitionStatusEndpoint` provides the late-join hydrate because acquisition can begin before the browser
authenticates. React's global `RuntimeAcquisitionBanner` merges hydrate and hub push by `sequence`, refetches after a
reconnect, shows multi-archive step progress for Windows CUDA, and offers retry only after a sanitized failure. The hub
is one of the unconditional local hubs in the `MapHub` block of `Client/Program.cs` (see
[01-architecture-overview.md](01-architecture-overview.md#the-local-surface-browser--node) for the full list);
route and DTO details are in
[API & Hubs](09-api-and-hubs.md), and the UI placement is in [React Client](10-react-client.md).

`InstallTagAsync` (the operator updater path) gates the live asset name against a strict allow-list (no path/URL metacharacters) since it is interpolated into a temp path and download URL, verifies the 64-hex digest, and enforces a disk-space guard. It **refuses outright** while a source build is recorded ("Remove the installed source-built llama.cpp runtime before installing a prebuilt runtime") — prebuilt install and source build are mutually exclusive, serialized by the manager's `_sourceMutationGate`. `ILlamaCppUpdateState` / `LlamaCppUpdateState` is the shared "is a newer runtime available?" snapshot, written by the startup check and surfaced by a read-only runtime-status endpoint — decoupled from any app-package update channel.

> Upstream constraint, documented in the pins: llama.cpp ships **no prebuilt Linux CUDA asset**. That gap is exactly what §2.6 exists to close — a Linux NVIDIA box either runs Vulkan, or the operator supplies a binary via the BYO override, or it builds CUDA in-app.

### `ModelRole` and the external-endpoint option

- `ModelRole` (enum, `ModelRole.cs`): `Chat`, `Embedding`, `Reranker`. Drives both the launch flags and the process key — the three roles need mutually exclusive flags (`--jinja` / a non-`none` pooling type / `--rerank --pooling rank`), so a distinct `(model, role)` is always a distinct process and each counts against the shared loaded-cap. The reranker role serves `/v1/rerank` and backs the Knowledge Base's local cross-encoder ([15-knowledge-base.md](15-knowledge-base.md)).
- `LlamaServerExternalEndpointOptions` is an optional **hybrid attach** map: `(modelName, role) → external OpenAI-compatible base URL`. A match short-circuits `EnsureRunningAsync` entirely — the supervisor returns the configured endpoint and never owns a process for it. Empty by default (pure spawn-and-supervise); bound from node config at DI time.

### DI wiring

`LlamaServerServiceCollectionExtensions.AddLlamaServerLocalModelProvider` registers the whole stack as singletons via `TryAdd*`: vendor probe, variant selector, the live catalog + installed-runtime store + update state, the binary manager (built by factory with `cacheRoot/activeTag = null` so it self-defaults), default supervisor + external-endpoint options, the launcher and health probe, the **`DefaultInferenceProfileResolver`** (TryAdd, see §2.5), the source-build stack (§2.6: prerequisite probes, the `LlamaCppSourceBuildService`, the `LegacyCudaBuildServiceAdapter` behind `ICudaBuildService`, the no-op event publishers, `ILlamaCppSourceBuildActivity`, `ICudaManagedBuildSignal`, and the `CudaBuildStartupService` hosted service), and the supervisor itself (explicit factory because its ctor is internal). The host must register an `HttpClient` (`AddHttpClient`) for binary downloads and supply an `IGgufModelStore` (the HuggingFace GGUF store).

### 2.5 Inference profiles / per-machine tuning

llama.cpp launch args used to be hard-coded (the forced `-ngl 999`). They are now resolved per-spawn by the **inference optimizer**: a node explores a model once on the actual hardware, optionally benchmarks the result against a golden transcript, freezes the winning args, and replays them verbatim on every later spawn of that `(model, role, backend)` — so each box runs the model with placement that was proven on *that* box, not a one-size guess.

**The resolver seam.** `IInferenceProfileResolver` (`Providers.LlamaServer/Contracts/IInferenceProfileResolver.cs`) is the dependency-inversion boundary the supervisor calls on the cold-spawn path. It is **defined in `Providers.LlamaServer`** so the supervisor never depends on `Client.Application` (the one-way `Application → Providers` arrow is preserved). Two implementations:

- **`DefaultInferenceProfileResolver`** (ships in the provider, `internal`) always returns `ResolvedLaunchArguments.Explore()` — a node with no profile store self-satisfies and launches under llama.cpp auto-fit. Registered with `TryAddSingleton`.
- **`InferenceProfileResolver`** (`Client.Application/Services/Inference/InferenceProfileResolver.cs`) is the real DB-backed resolver, registered **last** so it wins. It keys a lookup by `(machineKey, model, role, backend)`, then: an **Explored** or non-stale **Frozen** profile replays its persisted args; a Frozen profile is first re-checked through `IInferenceInvalidationEvaluator.IsStaleAsync` and demoted to **Stale** (→ explore) when its baseline no longer holds; CPU spawns and any missing/Stale/corrupt row fall back to explore. This path **never throws** — a bad persisted arg combo degrades to auto-fit rather than escaping the supervisor's spawn. `IInferenceProfileStore` is scoped, so the singleton resolver opens a fresh DI scope per call.

**Machine key.** `IMachineKeyProvider` → `MachineKeyProvider` (`Services/Inference/MachineKeyProvider.cs`) reads/mints a per-box GUID (`"N"` format) persisted in node settings (generate-once is gated so two concurrent first-callers can't mint two keys). Profiles are keyed to this machine key and are **local-only** — the key is never emitted in telemetry, aggregates, logs, or the transport view (`InferenceProfileView` deliberately omits it). This is what lets a profile re-explore when the box, build, or free-VRAM baseline changes rather than blindly replaying stale args.

**Process VRAM-budget probe.** `IProcessVramBudgetProbe` → `LlamaListDevicesProcessVramBudgetProbe` (`Providers.LlamaServer/Implementation/LlamaListDevicesProcessVramBudgetProbe.cs`) runs a short-lived `llama-server --list-devices` (15 s cap, tree-killed on overrun), parses the per-device `"<total> MiB, <free> MiB free"` column and returns the **largest free** figure in bytes. It is vendor-agnostic — it reads llama.cpp's own device report (CUDA / Vulkan / SYCL), never `nvidia-smi`, so one code path serves every GPU backend. **Degrade, never throw:** a CPU/unknown/blank backend (no process is even spawned), an empty device list, a timeout, or any failure degrades to `null` ("unknown"). A `TryAddSingleton` floor (`UnknownProcessVramBudgetProbe`) reports "unknown" wherever the real probe is not registered.

> **The rename is the documentation.** This is *not* system-wide free VRAM. On WDDM (Windows and WSL2) `--list-devices` is built on `cudaMemGetInfo`, which reports the **calling process's residency budget** and can ignore VRAM held by games or other model processes — a measured divergence of **492 MiB versus 29697 MiB** against `nvidia-smi` while another process held memory. So the two readers legitimately disagree, and the split is deliberate: consumers needing *global* admission or invalidation semantics read `HardwareProfile.AvailableVramBytes` (→ `nvidia-smi`, §3) instead. `InferenceInvalidationEvaluator` does exactly that — **profile invalidation no longer uses this figure at all**. What it does feed is the GGUF variant recommender ([07-model-fit.md](07-model-fit.md)), `InferenceProfileService`, `InferenceBenchmarkHarness`, and `ProcessContextAllocationResolver`.

**Device audit (AUD4-03).** A sibling probe, `ILlamaDeviceInventoryProbe` → `LlamaDeviceInventoryProbe`, runs the same `--list-devices` (via the shared `LlamaListDevicesProcessRunner`) but returns the **structured device list** `{variant, devices[], probeSucceeded}` cached per binary (path+mtime). The Application-layer `IRuntimeDeviceAudit` → `RuntimeDeviceAuditService` composes it with the hardware profile + the selected variant into `RuntimeDeviceAuditState {inferenceBackend, gpuExpected, cpuFallback, reason, remediation}`: a GPU box whose selected runtime enumerates **zero** devices (or a CPU variant on a GPU box) is a **silent CPU fallback** — the audited WSL2-Vulkan-no-ICD case. An indeterminate probe is "unknown", never a false alarm. `GetEffectiveProfileAsync` degrades the profile to CPU-mode on fallback so the advisor ([07-model-fit.md](07-model-fit.md)) and `CapacityService` size against RAM; the `GET model-fit/hardware-profile` endpoint returns the raw profile PLUS this audit block; a `device_fallback` metric + a Warning fire once per binary.

**GPU-load admission (AUD4-06).** `IGpuModelLoadAdmission` (Abstractions interface + `NoOp` floor; real `GpuModelLoadAdmission` in Application, one singleton) is a process-wide `SemaphoreSlim(1,1)` that serializes the **spawn-through-readiness** window of GPU-backed loads across BOTH this supervisor and the image supervisor ([14-image-generation.md](14-image-generation.md)), so two `--fit` loads never race the same free-VRAM read. It is acquired inside `SpawnCoreAsync` after variant selection, **GPU variants only** (CPU bypasses; reuse never touches it), released on ready/failure via `using`, and the acquire runs under the detached-spawn token so a cancelled caller never holds it. A bounded max-wait surfaces a typed `GpuModelLoadAdmissionTimeoutException` (non-retryable) rather than hanging. Lock ordering: the capacity ledger decision gate is released before the load gate is acquired (they never nest).

**MoE detection.** `IGgufMetadataReader` surfaces `IsMoe` / `ExpertCount` from the GGUF header (a positive declared expert count ⇒ MoE), carried onto the persisted profile so the operator orchestrator and UI can reason about expert placement (`-ot` override-tensor) for mixture-of-experts models.

**The operator orchestrator.** `IInferenceProfileService` → `InferenceProfileService` (`Services/Inference/InferenceProfileService.cs`, **scoped**) is the explore → benchmark → freeze lifecycle, exposed over the `model-fit/profiles/*` endpoints (see [07-model-fit.md](07-model-fit.md#inference-optimizer-operator-surface) and [09-api-and-hubs.md](09-api-and-hubs.md)):

- **`ExploreAsync`** spawns one auto-fit `llama-server` (via the supervisor's exclusive profiling path `RunExclusiveProfilingAsync`), parses the fitted args from the captured startup banner (falling back to the GGUF native context when unparseable), and upserts the single **Explored** profile for the key. Only node-local GGUF models are eligible — a cloud or missing model is rejected without spawning.
- **`BenchmarkAsync`** replays the drafted profile under a metrics-enabled spawn, runs the fixed golden transcript, and persists a benchmark snapshot + metric row (marked Succeeded/Failed). It does **not** freeze.
- **`FreezeAsync`** promotes an Explored profile to **Frozen** — **gated on a most-recent successful benchmark** (returns a failed result, never throws, when no justifying benchmark exists).
- **`InvalidateAsync`** is the operator-triggered manual demotion to **Stale** (forces a re-explore on the next spawn).

The persisted store + benchmark metrics are migration `20260626234754_AddInferenceProfilesAndBenchmarkMetrics` ([08-data-and-persistence.md](08-data-and-persistence.md)).

### 2.6 In-app source builds (Linux)

Upstream ships no prebuilt Linux CUDA `llama-server`. Rather than leave a Linux NVIDIA box on Vulkan, the node can **compile `llama-server` from source in-app** and adopt the result as a managed runtime. This is a real, tested subsystem — not a plan — and the code lives in `Providers.LlamaServer/Implementation/` (`LlamaCppSourceBuildService.cs`, `LlamaCppSourceBuildActivity.cs`, `LlamaCppSourceBuildPrerequisiteProbe.cs`, `CudaBuildService.cs`, `CudaBuildStartupService.cs`, `CudaBuildPrerequisiteProbe.cs`, `CudaManagedBuildSignal.cs`, `LegacyCudaBuildServiceAdapter.cs`, `LlamaCppBinaryManager.ManagedCuda.cs`), with the operator surface at `LocalApiRoutes.ModelFit.SourceBuild*` / `CudaBuild*` and two push hubs ([09-api-and-hubs.md](09-api-and-hubs.md)).

**Prebuilt download remains the default and only automatic path.** A source build never happens implicitly: it is always an explicit, Operator-gated POST. Once a build is adopted it becomes authoritative (§ binary manager, source 2) until it is explicitly removed.

**Two contracts, one implementation.** `ILlamaCppSourceBuildService` is the generalized surface — `StartAsync` / `GetStatus` / `Cancel` / `CancelLegacyPinnedCuda` / `RecoverAsync` / `ShutdownAsync`. The older CUDA-only `ICudaBuildService` still exists but is now satisfied by `LegacyCudaBuildServiceAdapter`, which forwards to the generalized service with a fixed `(Cuda, Official)` request and reports status **only** when the current build matches the legacy shape (`LlamaCppSourceBuildCompatibility.IsLegacyPinnedCuda`) — otherwise it answers `Idle`. Both route families stay live so the older CUDA UI card keeps working.

**Request model** (`Contracts/ILlamaCppSourceBuildService.cs`, `LlamaCppSourceBuildRequestValidation.cs`):

- `Backend` ∈ `{Cpu, Vulkan, Cuda}` → `GpuVariant`.
- `Source` ∈ `{Official, Custom}`. **Official** means the server-selected `https://github.com/ggml-org/llama.cpp` at the engine-pinned revision (`RevisionMode = EnginePinned`, resolved commit = `LlamaCppReleasePins.PinnedSourceCommitSha`); a client-supplied repository or commit is rejected. **Custom** requires `AcknowledgeCustomSourceRisk = true` (the repository's code executes with the app user's privileges), a canonical public GitHub HTTPS URL (scheme/host/default-port/no userinfo/query/fragment, exactly `owner/repo`), and an optional full 40-hex commit SHA.
- `Normalize` is **idempotent by contract**, and that contract is load-bearing: it runs both at the FluentValidation edge and again inside `StartAsync`. When the endpoint *also* pre-normalized, the first pass wrote the canonical official repository and the strict "the official repository is selected by the server" rule then rejected its own output — every official-source build answered `409 {"reason":"prerequisites"}` while custom-source builds worked (fixed in `2cab52ec`; the endpoint's redundant pass was dropped and `Normalize` now admits the canonical value it selects itself).

**Start is a serialized transaction** (`LlamaCppSourceBuildService.StartAsync`), under `_startGate`, in this order: Linux-only guard → normalize → already-running check → `RecoverAsync` → prerequisite probe → **runtime mutation lease** → activity reservation → detached build task. Failure modes are typed, not exceptions: `AlreadyRunning`, `InsufficientDisk`, `MissingPrerequisites`, `ProcessesRunning` (carrying the running-process count), `RuntimeBusy`. The endpoint maps each to a `409` with a machine-readable `reason` (`already-building` / `disk` / `prerequisites` / `processes-running` / `runtime-busy`) — this is the **eject-first gate**: a build cannot start while any `llama-server` process is alive, because adoption swaps the binary tree underneath it.

`ILlamaCppSourceBuildActivity` is the process-wide reservation that keeps builds and model spawns mutually exclusive; release is **identity-scoped by `BuildId`** so cleanup from an older build cannot clear a newer build's reservation.

**Prerequisites** (`LlamaCppSourceBuildPrerequisiteProbe.ProbeAsync`) is an itemized checklist, each item `(Key, Satisfied, Detail)`; `canBuild` is true only when **every** item is satisfied. The base set is `os-is-linux`, `cmake`, `gcc`, `g++`, `make-or-ninja` (satisfied by either), `git`, `free-disk` (against the cache root's drive). Backend-specific items are inserted near the front: CUDA adds `nvidia-gpu`, `nvcc` and `nvidia-smi`; Vulkan adds `glslc` and `vulkaninfo`. A non-Linux host short-circuits to a **single** unsatisfied `os-is-linux` item — the endpoint is deliberately callable from any OS so the UI can render the checklist and explain why the build is unavailable. `free-disk` is the one item the start path re-reads, to answer `InsufficientDisk` rather than the generic `MissingPrerequisites`.

**Build phases** (`LlamaCppSourceBuildPhase`, each pushed over the hub with the appended log lines): `Cloning → Verifying → Configuring → Building → Adopting → Completed | Cancelled | Failed`.

1. **Clone** the selected repository/revision, no submodules, 15-minute cap.
2. **Verify** `git rev-parse HEAD` equals the expected commit **before any cmake runs** — the pinned SHA for `EnginePinned`, the requested SHA for `ExplicitCommit`, unconstrained for `DefaultBranch`. A mismatch aborts.
3. **Resolve compute architectures** (CUDA only) from `nvidia-smi`'s `compute_cap`, validated, falling back to `75;86;89`.
4. **cmake configure** (15-minute cap), then **cmake build** of the `llama-server` target with `-j min(nproc, 8)` (120-minute cap).
5. **Adopt** — stage, validate, swap, record.

Everything runs under a **scrubbed, allowlisted environment** with an isolated `HOME` and `TMPDIR`, in an owner-only (0700) work directory **inside the cache root — never `/tmp`**.

**The adopt swap is the part worth reading twice** (`LlamaCppSourceBuildService.cs`). The built tree is moved to a sibling `.staging` dir (same filesystem ⇒ atomic moves), symlink-validated and permission-hardened there, and given a `.source-build-manifest.json`. Only then, under a freshly acquired runtime-mutation lease: any previous runtime is moved to `.backup`, the staged tree is moved to `active`, and `LlamaCppBinaryManager.AdoptSourceBuildAsync` validates the **final** binary (device smoke check + SHA256 record) and writes the provenance into `installed-runtime.json`. On any failure the staged and half-swapped trees are deleted and the backup is moved back — **a failed rebuild never loses a working runtime** (`6dec9feb`, `64a1bf2d`). The layout is:

```
{cacheRoot}/llama.cpp/source-build/
  active/build/bin/llama-server     # the adopted runtime (SourceBuildPath points here)
  .staging/                         # validated-then-swapped; deleted on success/failure
  .backup/                          # previous runtime, kept until the new one adopts
  .work/                            # clone + cmake tree, 0700, deleted on terminal
```

**Crash recovery.** `CudaBuildStartupService` (an `IHostedService`) runs `RecoverAsync` at startup and seeds the cached active-source signal from the installed record; **reconciliation failure is fatal to startup**, so readiness is never reported against ambiguous runtime state. `RecoverAsync` deletes `.work` and `.staging`, then `ReconcileActiveAndBackupAsync` decides between `active` and `.backup` by re-validating each tree against the recorded provenance (`TreeMatchesRecordAsync`): the matching tree wins and is moved into `active` with the record rewritten; if neither matches, both are deleted and the source record is cleared so the node falls back to the prebuilt path. A pre-provenance legacy CUDA record (written before the provenance fields existed) is recognized by path shape and separately re-validated.

**Cancel / remove.** `Cancel()` signals the build token — the partial `.work`/`.staging` trees are dropped and `active` is untouched. `RemoveSourceBuildAsync` deletes the adopted tree, clears the record and the cached signal, and returns resolution to the prebuilt 3-tier path.

**Test coverage** lives in `XE-Local-AI-Engine.Tests/Providers/LlamaServer/`: `LlamaCppSourceBuildCoreTests.cs`, `LlamaCppSourceBuildServiceTests.cs`, `LlamaCppSourceBuildTransportTests.cs`, `LlamaCppSourceBuildPrerequisiteTests.cs`, `SourceBuildRecoveryTests.cs`, `ManagedSourceBuildSafetyTests.cs`, `CudaBuildServiceTests.cs`, `CudaManagedRuntimeTests.cs`.

### 2.7 Per-model extra launch arguments (operator override)

Distinct from the machine-wide launch policy above, an operator can persist a raw extra-argument string
**per model**. `LlamaServerExtraLaunchArgumentsResolver` (`Services/Inference/LlamaServerExtraLaunchArgumentsResolver.cs`)
implements the provider's `ILlamaServerExtraLaunchArgumentsResolver` seam — the provider ships an empty default
(`EmptyLlamaServerExtraLaunchArgumentsResolver`) and `AddNodeModelRuntime` registers this one last, so it wins —
reading the stored string through `IModelLaunchArgumentsStore`
(entity `ModelLaunchArguments`, migration `AddModelLaunchArguments`, CRUD at
`{Get,Put,Delete}ModelLaunchArgumentsEndpoint`). The supervisor resolves it in `SpawnCoreAsync` alongside the
profile args and **before** the admission gate, then appends the tokens **after** the built spec, where llama.cpp's
last-wins parsing lets a later scalar flag override a bundled tuning default.

Three properties are load-bearing:

- **Two flag families are refused on write and stripped on read** by `LlamaLaunchArgumentParser.ParseSanitized`
  (`Services/Inference/LlamaLaunchArgumentParser.cs`): *reachability* (`-m`/`--model`, `--host`, `--port`) and the
  *memory-fit placement* family (`-c`, `-ngl`, `-ts`, `-ot`, `-ctk`/`-ctv`, `-fa`, `--parallel`, `-b`/`-ub` and
  their long aliases), plus `--lora`/`--lora-scaled`. Placement is decided before admission and recorded in the
  memory ledger, so a post-hoc override would invalidate the ledger, defeat the KV-quant safe-config retry, and
  overcommit RAM/VRAM. Everything else llama.cpp accepts (sampling, RoPE, penalties, samplers, grammar, mirostat)
  stays available — re-exploring is the supported way to change placement.
- **Profiling and benchmark spawns never see it.** The resolve is gated on `applyLaunchPolicy`, so a measurement
  spawn stays a pure measurement rather than carrying the operator's experimentation flags.
- **It can never break a spawn.** A store read failure is logged and degrades to "no extra args"; the resolver is a
  singleton that opens a fresh scope per call because the store is scoped.

---

## 3. Satellite providers

### `Providers.Ollama` — present, de-orchestrated from Aspire dev

`OllamaLocalModelProvider` (`ProviderName = "ollama"`) still fully implements `ILocalModelProvider` over `IOllamaApiClient` (OllamaSharp): list/pull/delete/warm/unload + `CreateChatClient`/`CreateEmbeddingGenerator`. It remains a real, registered provider — but llama.cpp is the dev runtime, so **Ollama is no longer orchestrated by Aspire in dev** (see [11-hosting-and-deployment.md](11-hosting-and-deployment.md)). Notable detail: `AddOllamaLocalModelProvider` sets a short **750 ms `SocketsHttpHandler.ConnectTimeout`** so a probe against an absent Ollama daemon (desktop mode) fails fast instead of stalling on the OS connect timeout; `OllamaConnectFailureHandler` translates a fired connect-timeout (`OperationCanceledException`) into `HttpRequestException` so "Ollama unreachable" handling is uniform. The 5-minute `HttpClient.Timeout` still covers genuine long pulls. `OllamaModelCapabilityClient` implements `IModelCapabilityClient` as thin pass-throughs over the API client.

### `Providers.Capabilities` — hardware probing

`HardwareProfiler` (impl of `IHardwareProfiler` in the abstractions project) is the **full** hardware probe — CPU/RAM/GPU/VRAM facts (`HardwareProfile`, `GpuVendor`) — distinct from the minimal `IGpuVariantSelector`. It probes the environment via `IHardwareProbeEnvironment` / `IProcessProbe` and feeds the Model Advisor's memory-fit math ([07-model-fit.md](07-model-fit.md)). `CapabilitiesServiceCollectionExtensions` wires it.

### `Providers.CodexOAuth` — ChatGPT-OAuth cloud chat provider

The one **cloud** chat provider. `CodexOAuthChatClientFactory` (`ICodexOAuthChatClientFactory`) builds an `IChatClient` against the ChatGPT/Codex API authenticated by OAuth, with a shared handler chain (`SocketsHttpHandler → CodexAuthHandler`). The `Auth/*` folder holds the OAuth machinery: `CodexAuthService`, `CodexLoginCoordinator`, `CodexTokenStore` (`ICodexTokenStore`), `CodexHeaders`, `CodexTokens`. Codex-specific quirks are encoded here: `CodexResponseStoreDisabling` / `CodexStoreDisabledChatClient` force `store=false` (replaying encrypted reasoning), `CodexModelCatalog` + `CodexProviderCapabilities` declare the model/effort surface. OAuth tokens are a LOCAL secret — never returned to the browser, never logged (see [12-security-and-privacy.md](12-security-and-privacy.md)). Reasoning-effort selection and cloud↔local clamping are covered in [05-chat.md](05-chat.md).

### `Providers.OpenAICompat` — operator-registered external endpoints

One multiplexer `ILocalModelProvider` (`ProviderName = "external"`) serving every connection the operator registered, dispatched by parsing `ext:{connectionId}/{wireId}` through `IExternalProviderRegistry`. Capabilities are DECLARED, never probed: only `POST /v1/chat/completions` is universal across OpenAI-compatible servers, and none of them advertises tool, vision or reasoning support in a way that survives llama.cpp / vLLM / LM Studio / a hosted API alike. The connect-time probe is `GET {base}/models` and nothing else.

Four invariants are load-bearing, and each has a test:

- **Reasoning is never replayed.** A model's reasoning output is surfaced (MEAI's OpenAI adapter lifts `reasoning_content` into `TextReasoningContent`; the provider's thin rewriter adds only the newer vLLM `reasoning` field and an inline `<think>` fallback), but it is **not sent back to the server on a later turn**. Chat Completions drops historical `TextReasoningContent` by design — identical to the llama.cpp path today, and unlike Codex, which replays encrypted reasoning because the Responses API requires it. A server that needs its own reasoning replayed to stay coherent across turns is out of scope for v1. Pinned by a two-turn wire test asserting the replayed request body carries none.
- **A key belongs to ONE origin.** The registry hands the transport an endpoint and its credential as one atomic `ExternalProviderTransportBinding` read from a single snapshot generation, so no arrangement of concurrent edits can present one connection's key at another's address. Changing a stored connection's base-URL ORIGIN requires the key to be re-entered or explicitly cleared, and the probe's stored-key fallback applies only when the probed origin equals the stored one. Without both, an operator-API caller who cannot read the encrypted key could repoint the endpoint at a listener they control and have the node send the secret to it.
- **Every invocation's binding is pinned.** Tool authorization happens once, before the first send, from the connection's declared locality; a tool loop then sends many times. `ExternalProviderBindingPinScope` (an `AsyncLocal`) records the generation, locality and FULL normalized base address — path included, because two OpenAI-compatible services routinely sit behind one origin — each invocation was authorized against, keyed by the canonical model id so a case variant out of the NOCASE provider map still finds its pin. `ExternalOpenAiChatClient` re-checks them on every send: a mid-invocation Local→Cloud flip, host move or base-path move throws `ExternalProviderBindingChangedException` instead of quietly redirecting already-authorized local tool results to the new endpoint. Every path that runs a model pins it — the turn in `InvocationRunner` beside the spawn context, each orchestration participant, and a spawned sub-agent's own child binding — and pins stack, so a child's never evicts its parent's. `ExternalProviderInvocationPin` only RESOLVES the pins: the scope is opened synchronously by the caller, because an `AsyncLocal` written inside an `async` method is invisible to that method's caller and a helper that seeded it there pinned nothing at all.
- **Delegation follows the same trust gate as `run_python`.** A declared-cloud or unresolved external model is offered neither `run_python` nor `spawn_subagent`, and `SubAgentSpawnService` refuses a spawn whose parent sits outside the trust boundary. A child resolves its own model and its own tool set, so an ungated spawn offer is a bypass of the workspace, knowledge-base and custom-tool gates rather than a capability of its own.

### The model catalog's five sources

`LocalModelCatalogService` gathers the chat picker's whole catalog from five sources that each degrade on their own — Ollama, the installed GGUF registry, a Codex session, a stored Azure Foundry connection, and the operator's external OpenAI-compatible connections (`IExternalProviderRegistry`). No source failure ever fails the catalog: an unreadable encrypted external store yields no external entries, exactly as an unreadable GGUF registry yields no GGUF entries.

`ListLocalModelsEndpoint` maps them through `LocalModelsMapper` in one fixed order — Ollama, GGUF, cloud, external — and each entry carries four nullable identity fields beyond its `provider` tag:

| Field | Populated for | Why the tag alone is not enough |
|---|---|---|
| `displayLabel` | external models, Azure deployments | The operator's friendly name. Azure stored one all along and the list DTO had nowhere to put it, so it was dropped before the picker. |
| `externalConnectionId` | external models | Every external model shares one `provider: "external"` tag (the provider is a multiplexer), so this is what sections the picker per connection. |
| `externalConnectionName` | external models | The section heading and the "Sent to {connection}" egress cue. |
| `declaredLocality` | external models | `local` \| `cloud`, the operator's DECLARATION — never inferred from the base URL. It decides whether the entry is badged and grouped as local or as cloud. |

`GetLocalModelDetailsEndpoint` repeats the same four on its external branch, because a details view reached by deep link has no list entry to read them from. External models never appear in the running/loaded-models view: "running" means resident in this node's memory, and the node owns no process for them.

> **Not a runtime provider:** voice/text-to-speech. The backend exposes only the `VoiceFeatureEnabled` node setting; the React client speaks through the browser/operating-system Web Speech implementation. The repository ships no voice model, download path, worker, cache, model port, or node inference process. Voice availability and local-versus-network behavior belong to the platform speech service and are outside repository control. See [10-react-client.md](10-react-client.md) for the client runtime.

---

## 4. `Providers.HuggingFace` — GGUF discovery & download store

`HuggingFaceGgufStore` (internal, impl of `IGgufModelStore`) is the model inventory + acquisition backend the llama-server provider delegates to. It deliberately **does not depend on the LlamaServer project** — it tags descriptors with the agreed `llamacpp` provider-name constant.

Responsibilities and collaborators:

- **Discovery** — `HuggingFaceGgufDiscovery` (`IHuggingFaceGgufDiscovery`) enumerates GGUF files/quants for a repo via `HfHubClient`.
- **Download** — `HfDownloadClient` fetches the chosen GGUF to disk (atomic temp-file → rename for offline reuse), serializing concurrent `EnsureModelAsync` for the same name with a per-name gate, guarded by `IFreeSpaceProbe`.
- **Registry** — `GgufModelRegistry` (`IGgufModelRegistry`) is the on-disk record (`GgufModelRegistryEntry`: model name, local path, size, sha, downloaded-at); `ResolveModelFilePathAsync` / `ListInstalledModelsAsync` read it.
- **Header facts** — `GgufHeaderReader` reads the GGUF header once per model; `GgufCapabilityDetector.Detect` classifies tool/reasoning capability **deterministically from the embedded Jinja chat template** (`tokenizer.chat_template`) — tool markers (`tool_calls`, `function_call`, `tools`) and reasoning markers (`<think`, `enable_thinking`, `reasoning_content`). This is why XE needs no Ollama `/api/show` probe for a GGUF (a GGUF has no Ollama entry, and desktop mode runs no Ollama daemon). The same pass answers a THIRD question — `ReasoningBudgetEnforceable`: does the template render a literal reasoning END marker (`</think>`, `</thinking>`, gemma-4's `<channel|>`)? That is the shape llama.cpp turns into the non-empty think-end-tag set its `reasoning_budget_tokens` gate requires; without it the server accepts the budget and silently ignores it, so the node omits the field instead. It rides `LocalModelDescriptor` and the model-list DTO, and is also computed at import (`GgufImportInspector.Classify`, which raises an operator warning for a graded-but-unenforceable template). Its default is `true` everywhere — only a positively-detected closing-tag-less template turns a cap off. See [05-chat.md](05-chat.md#thinking-budget-llamacpp-and-where-it-is-enforceable) for the enforcement evidence. Results are cached by `(path, size, downloadedAt)`; a header-read failure for one model never sinks the list (yields `GgufHeaderFacts.Empty`).
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

Every `llama-server` child is same-user, unprivileged, and `127.0.0.1`-bound — the node never exposes a model port outward. Only the Node Web Server talks to the platform (over WorkerHub); the runtime layer here is purely local. See [09-api-and-hubs.md](09-api-and-hubs.md) for the local admin endpoints that drive warm/unload/health and [12-security-and-privacy.md](12-security-and-privacy.md) for the trust boundary.

---

## Related pages

- [01-architecture-overview.md](01-architecture-overview.md) — where the runtime fits in the node
- [02-project-layout.md](02-project-layout.md) — the `Providers.*` project inventory
- [07-model-fit.md](07-model-fit.md) — box-aware GGUF recommendation & the full hardware profiler
- [05-chat.md](05-chat.md) — chat/reasoning over these chat clients
- [08-data-and-persistence.md](08-data-and-persistence.md) — GGUF files, registry, installed-runtime state
- [09-api-and-hubs.md](09-api-and-hubs.md) — local endpoints/hubs that drive the runtime (incl. the source-build routes + hubs)
- [11-hosting-and-deployment.md](11-hosting-and-deployment.md) — Aspire dev orchestration & desktop mode
- [12-security-and-privacy.md](12-security-and-privacy.md) — local-only secrets, node-local AI ops
- [14-image-generation.md](14-image-generation.md) — `sd-server`, the node's second supervised runtime
- [18-training.md](18-training.md) — `Providers.Training`, the uv-provisioned Python fine-tuning runtime the node spawns as a supervised child process (Linux only)
