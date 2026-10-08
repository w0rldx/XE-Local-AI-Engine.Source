# Model fit, discovery, capacity and spawn

Scope: GGUF discovery and recommendation, llama-server capacity/spawn/lifecycle. Read when: changing model-fit or
HuggingFace discovery, `CapacityService` or the llama-server supervisor. Architecture lives in `docs/wiki/03` and `07`;
this file holds only the traps those pages lack. Benchmarks, knowledge base, training and transcription:
[benchmarks-kb-training-transcription](benchmarks-kb-training-transcription.md).

## Model discovery and recommendation

### Recommendation: walk the quant ladder, never pick one quant

**Rule:** both advisor lanes select through `GgufFileSelector.SelectBestFit` (ladder walk: `docs/wiki/07-model-fit.md`); never duplicate selection. MTP draft GGUFs are a distinct identity (`MTP-<quant>`), recognized only by an `MTP/` path segment or `mtp-` filename prefix, never a bare `MTP` token in a base-model name. Drafters sort last, are never advisor picks, register as Draft, stay out of chat defaults and remain selectable in the draft slot. **Prevents:** a real base model filtered as a drafter, or a drafter recommended for chat. **Authority:** `GgufFileSelector`, `QuantLadder`.

### Recommendation ranking is capability-bucketed

**Rule:** explore ranking is GiB size bucket → downloads → last modified → trusted publisher → repo ID; catalog ranking is tier → MoE verdict → quant quality → release date → ID. "Recommended" needs positive headroom and a quant at/above Q4_K_M. **Prevents:** raw size/headroom ordering regressing the explore lane to tiny models. **Authority:** `CatalogRecommendationService.BuildRecommendationsAsync`.

### The advisor is two lanes, and one may fail

**Rule:** catalog and live-HF explore lanes concatenate; a catalog failure degrades to an empty catalog lane and must not fail explore. **Prevents:** one lane's outage blanking all recommendations. **Authority:** `CatalogRecommendationService`.

### MoE models need `MoeFacts`, not naive VRAM math

**Rule:** fit MoE models through `MemoryFitEstimator` with `MoeFacts` (`ActiveParamCount`, `ExpertCount`, `ExpertUsedCount`) and prefer the curated active-parameter count; expert counts alone fall back to the deliberately conservative `DefaultExpertWeightShareFraction`. Catalog ranking use: `docs/wiki/07-model-fit.md`. **Prevents:** total-weight-only math rejecting or mis-scoring MoE models. **Authority:** `MemoryFitEstimator`, `MoeFacts`, `CatalogRecommendationService.BuildMoeFacts`.

### Multi-part GGUF shards are ONE model

**Rule:** `HuggingFaceGgufDiscovery.GroupShards` groups `<base>-00001-of-00003.gguf`, sums sizes, uses the first split, and drops the shard group when an equivalent merged file exists. Later splits are headerless and never candidates. **Prevents:** shards listed as separate models, or a header read on a headerless split. **Authority:** `HuggingFaceGgufDiscovery.GroupShards`.

### NVFP4 **GGUF** runs here, natively — NVFP4 **safetensors** never will

**Rule:** NVFP4 GGUF is recognized, ranked and run by the pinned llama.cpp; compressed-tensors/ModelOpt NVFP4 safetensors cannot be converted by the current script. Check the container before calling the format unsupported. An unknown quant token makes a file disappear, not merely misprice, so add a new native format to parsing and usability together. Use the real blob size when available; the 4.25 bpw estimate is empirical. **Prevents:** a supported format declared unsupported, or silently dropped. **Authority:** `QuantLadder`. [evidence](../agent-knowledge-evidence.md#nvfp4-live-run)

### GGUF filenames from a repo are untrusted input

**Rule:** call `GgufFilePath.IsSafeRelativePath` and `ResolveContainedPath` immediately before opening; reject roots and `.`/`..` segments; never trust Hub sibling names. **Prevents:** path traversal out of the model store. **Authority:** `GgufFilePath`, `GgufFilePathTests`.

### HuggingFace API facts that bite

**Rule:** use `filter=gguf`, not `library=gguf`; `gated` is a string union, not a bool; request `?blobs=true` for sizes; .NET strips `Authorization` on a cross-host CDN redirect; filter `mmproj*.gguf` companions everywhere. **Prevents:** empty listings, parse failures, missing sizes, gated downloads failing after redirect, projectors offered as models. **Authority:** `HuggingFaceGgufDiscovery`.

### Model kind classification

**Rule:** `ModelKind` includes Reranker; test reranker names before embedding names. The capability-classification cache is digest-keyed, so an Ollama capability change without a digest change stays cached. **Prevents:** rerankers classified as embedding models; expecting a re-probe that never happens. **Authority:** `ModelKind` (`Client.Persistence`).

### Local-default chat resolution must stay Ollama-blind

**Rule:** resolve only installed llama.cpp GGUF Chat models from persisted classification; never live-probe Ollama on the send hot path. No chat model is `ModelNotInstalled`, not provider-unreachable. **Prevents:** send latency and a misleading error. **Authority:** `LocalDefaultChatModelResolver`, `NoChatModelInstalledException`.

### Reasoning ("think") has counter-intuitive Ollama semantics

**Rule:** for a model without Ollama's `thinking` capability, `think:true`/levels answer 400, `think:false` suppresses template-default reasoning, and omission preserves it: requested reasoning → omit; off/unspecified → false. Thinking-capable models receive false/levels. Keep `InvocationAgentFactory` and `ParticipantReasoningOptions` aligned; a new effort value also updates both factories, `ReasoningEffortNormalizer`, `RuntimePackageValidator` and `RuntimePackageConfigHash`. **Prevents:** 400s on non-thinking models and reasoning silently on or off. **Authority:** the symbols named.

## Capacity, spawn and process lifecycle

### Capacity gate: dispose the reservation

**Rule:** only a local `CapacityDecision.Allow` carries an `IDisposable` reservation; dispose it when the child exits. Warm `IRuntimeDeviceAudit` before entering the ledger decision gate. **Prevents:** leaked pending bytes that make later spawns reject. **Authority:** `CapacityService`.

### llama-server spawn invariants

**Rule:** the flag set, policy precedence, context tiers and KV defaults are in `docs/wiki/03-local-runtime-and-providers.md` ("Per-role launch flags…", "Launch-policy defaults…", "Spawn attempt sequencing…"); fitted GPU placement comes only from `llama-fit-params` ("`ExploreAsync`"). Not stated there: never enable `--ui-mcp-proxy` (MCP sits above llama-server); rerank scores map by the returned index, never response order, and a rerank failure degrades to RRF order; an unknown KV token degrades to fp16, and the default allocation-cache key must stay byte-identical when KV type is null so chat keeps its identity; read effective `n_ctx` from `/props` after readiness and feed both budgeters (unknown providers fall back to 8192). **Prevents:** a forked allocation identity; misattributed rerank scores. **Authority:** `LlamaServerLaunchPolicy`, `LlamaServerProps.ReadAsync`.

### llama-server readiness, load lifetime, and eject (Audit-4)

**Rule:** readiness, retry classes and reaper rules: `docs/wiki/03-local-runtime-and-providers.md` ("Health probe…", "Reuse-path liveness…", "Eviction & reaper"). Eject tri-state: `TryAcquireInferenceLease` is granted, refused-evicting or refused-absent. Absent may ensure/spawn; evicting must fail at once with `LlamaServerModelEjectedException`. Clear the eject mark on every path where teardown did not complete, including cancellation of the eject request mid-drain. Add new `InvocationState` fields to `Clone()`; warm local models before arming the stream-idle watchdog. **Prevents:** a request slipping under the drain and respawning the model the operator just ejected; every later lease refused forever. **Authority:** `LlamaServerProcessSupervisor`, `SupervisorLifecycleTests`.

### Context allocation is a stable process decision, not a live-memory sample

**Rule:** tier mechanics: `docs/wiki/03-local-runtime-and-providers.md` ("2.8 Process context allocation…"). `CapacityService.DecideAsync` must reject, never down-tier, below a caller's `RequiredContextTokens`: `_adjustedAllocations` is process-lifetime, monotonically downward and never cleared on eject, so one down-tier pinned by `TryCommitAdmissionFootprint` fails every later required-window admission until restart. Benchmark executors share `BenchmarkCapacityAdmission.AdmitAsync` (24 × 5 s default); a direct executor test injects `new BenchmarkAdmissionRetry(MaxRetries: 0, TimeSpan.Zero)`. **Prevents:** a sticky poisoned admission; two-minute test sleeps. **Authority:** the symbols named. [evidence](../agent-knowledge-evidence.md#context-and-admission-latch)

### Context allocation precedence: frozen, then deterministic override, then automatic

**Rule:** `ProcessContextAllocationResolver` resolves `FrozenProfile` > `DeterministicOverride` > `HardwareTier`. Only a `HardwareTier` allocation down-tiers (`TryDownTierForAdmission`, or `TryDownTierAfterOutOfMemory` at most `MaximumAutomaticDownTiers` (2) times per key); frozen and override allocations never mutate after a failure. A request can shrink an existing process's window, never enlarge it. The launch-policy fingerprint and tier decision never include live free VRAM, and admission retries re-evaluate capacity without reserving speculative bytes. Keep four values separately named through APIs and DTOs: launched `-c`, request budget, train-context ceiling, frozen replay override. **Prevents:** a frozen replay silently measuring a different window; one context name meaning four things. **Authority:** `ProcessContextAllocationResolver`, `ProcessContextAllocationSource`.

### llama-server node settings are captured once per boot — a `PUT` after that changes nothing until a restart

**Rule:** `speculativeMode` and `chatCacheReuse` are captured when the `LlamaServerSupervisorOptions` singleton is first resolved, and `kvCacheType` when `LlamaServerLaunchPolicyOptions` is (both lazy factories, not a reliable "first spawn" marker). A `PUT node-settings` after that does not reach a later spawn: restart with `scripts/dev-stop.sh` + `scripts/dev-start.sh` and read the values back with a `GET` before measuring. **Prevents:** an in-boot A/B concluding "the knob does nothing". **Authority:** `AddNodeModelRuntimeExtensions.BuildSeededLlamaServerSupervisorOptions`, `BuildSeededLlamaServerLaunchPolicyOptions`. [evidence](../agent-knowledge-evidence.md#llama-server-node-settings-are-captured-once-per-boot--a-put-after-that-changes-nothing-until-a-restart)

### a callback that runs under the supervisor's per-key gate must never re-enter the gated path for that key

**Rule:** `RunExclusiveProfilingCoreAsync` holds the non-reentrant `_ensureGates[key]` across its `body` (for a benchmark, the whole invocation pipeline). A guard excluding a profiling-owned process must answer EVERY same-key call the body reaches (`EnsureRunningAsync`, `GetRuntimeInfo`) from the pinned process via an identity-matched bypass (an `AsyncLocal` marker naming the pinned `ProcessKey` and INSTANCE, cleared before the body unwinds). A marker whose process is gone fails with the classified error, never falls through to `DecideEnsureAsync`; a mocked runner hides it. **Prevents:** local benchmarks hanging to timeout or failing `EffectiveContextUnavailable`. **Authority:** `SupervisorProfilingReentrancyTests`, `SupervisorProfilingTests`. [evidence](../agent-knowledge-evidence.md#a-callback-that-runs-under-the-supervisors-per-key-gate-must-never-re-enter-the-gated-path-for-that-key)

### a provider's `TryAddSingleton(new XOptions())` binds NOTHING; prove a config key end to end before measuring with it

**Rule:** a provider module registering `services.TryAddSingleton(new XOptions())` gives the host a bare default; the class's `SectionName` is only a promise the host keeps by binding the section before registering the module. Before a live round relies on a config/env knob, prove it reached the process (`ps -o args` for a launch flag, else a log line). A unit test handing the options object to the consumer proves the consumer, not the binding. **Prevents:** a void live round measuring defaults. **Authority:** `AddNodeImagesExtensions.BindStableDiffusionRuntimeOptions`, `StableDiffusionRuntimeOptionsBindingTests`. [evidence](../agent-knowledge-evidence.md#a-providers-tryaddsingletonnew-xoptions-binds-nothing-prove-a-config-key-end-to-end-before-measuring-with-it)

### A polling UI reads `ILiveMemorySampler`, never a forced hardware-profile refresh

**Rule:** anything that polls memory for display reads `ILiveMemorySampler` (`GET model-fit/resources`), never `IHardwareProfiler.GetProfileAsync(forceRefresh: true)` or `model-fit/hardware-profile?refresh=true`. **Prevents:** a forced refresh per poll spawns a full probe each time and overwrites `HardwareProfiler`'s process-lifetime cache, the baseline model fit and profile invalidation read, with a transient reading. **Authority:** `LiveMemorySampler` (never touches the profiler cache, one shared probe per ~2 s window), `HardwareProfiler.GetProfileAsync`, `LiveMemorySamplerTests`; wiki 03 "Live memory sampler", 2026-09-29.

### llama.cpp "offloaded N/N layers to GPU" does not mean GPU-resident under expert offload

**Rule:** read placement from the launch argv (`--cpu-moe`, `-ot …exps=CPU`), not from the load banner: llama.cpp counts a layer offloaded when only its attention tensors are on the GPU. Pick a chat tier GPU-resident before expert offload (down to 16,384), and size hybrid recurrent stacks from `{arch}.full_attention_interval`. **Prevents:** a 35B-A3B that fits resident at 32k launched `-c 65536 --cpu-moe` (45 vs 222 tok/s) and shown as 41/41 on GPU. **Authority:** `ProcessContextAllocationResolver.ResolveCoreAsync`, `MemoryFitEstimator.TotalKvTokensAcrossLayers`, `LlamaServerProcessSupervisor.PinsExpertsToCpu`; model-matrix round F17, 2026-10-04.

### An exclusive profiling or benchmark body holds the per-key ensure gate, never the GPU-load gate

**Rule:** the GPU-load admission gate (wiki 03, "GPU-load admission (AUD4-06)") covers one load, not the work after it.
Never hold it across a `RunExclusiveProfilingAsync`/`RunExclusiveBenchmarkAsync` body; that body holds only the per-key
ensure gate. **Prevents:** one long profile or benchmark run blocking every other model's load on the node.
**Authority:** `LlamaServerProcessSupervisor.RunExclusiveProfilingAsync`, `RunExclusiveBenchmarkAsync`.

## Layering

### a reverse provider→application `ProjectReference` fails at RESTORE, not in an architecture test

**Rule:** a break-proof adding a `ProjectReference` back up the layering graph never reaches a test: `dotnet restore XE-Local-AI-Engine.slnx` fails first with `MSB4006` circular dependency (`_GenerateRestoreProjectPathWalk`), naming the mutated csproj. Report that restore failure as the proof. **Prevents:** concluding the architecture test is broken because it never ran. **Authority:** the `MSB4006` diagnostic restore prints. [evidence](../agent-knowledge-evidence.md#a-reverse-providerapplication-projectreference-fails-at-restore-not-in-an-architecture-test)

## Stale beliefs

Superseded claims; the entries above are the active rules.

| Stale belief | Current correction |
|---|---|
| llama.cpp native MCP should replace the app client. | Its MCP proxy is for llama-server's browser UI; keep the .NET MCP integration and approval boundary (§3). |
| NVFP4 is unsupported. | NVFP4 GGUF works; NVFP4 safetensors conversion does not (§3). |
| Advisor runs an llmfit container/HostAgent. | It uses in-process fit estimation and live HF discovery (§3). |
| Recommendation is raw-size descending. | Explore ranking is capability-bucketed (§3). |
| `ModelKind` has only Unknown/Chat/Embedding. | Reranker is a fourth kind and is checked first (§3). |
