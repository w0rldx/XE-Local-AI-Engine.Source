# Final local-inference optimization audit

**Date:** 2026-08-12
**Repository revision:** `5040e65f5b917ec75361d75f5bea4780b52f1e0d`
**Scope:** research and proposal at the audited revision; the post-approval implementation status is recorded below.
**Upstream research anchor:** llama.cpp `9558fa44c92746a58dd07ad1bf0c889715b938a6`; latest release at retrieval was `b10375`. XE is pinned to `b10201` / `8f4646a63`.

## Executive summary

**Yes, meaningful inference improvements remain, but the remaining value is not in adding a long list of static llama.cpp flags.** XE already implements most of the high-value baseline mechanisms: auto-fit, explicit context allocation, single-slot serving, GPU layer replay, Q8 KV plus Flash Attention with fallback, bounded host prompt-cache RAM, prefix reuse, CPU thread defaults, source/CUDA/Vulkan build paths, role-specific servers, request streaming, a benchmark/freeze workflow, runtime device audit, and correct native-MTP orchestration.

The last worthwhile work is mainly a **measurement and policy closure**:

1. **Close the AMD/Intel VRAM evidence gap without merging two different measurements.** Today Linux AMD/Intel and Windows AMD/Intel can select Vulkan yet still be sized as CPU-only because the generic hardware profile has no global total/free VRAM evidence. llama.cpp's device inventory can prove runtime reachability and a process residency ceiling, but it must remain distinct from global pressure.
2. **Probe runtime capabilities and pin them to the binary identity.** XE supports bundled, source-built, managed-CUDA, and operator-supplied binaries, while launch behavior is presently compiled against the pinned release's CLI contract.
3. **Feed observed resource use and latency back into profiles.** The estimator and benchmark workflow are strong, but XE does not yet close the loop from actual loaded/offloaded bytes, cache reuse, queue time, and startup phases into fit and residency decisions.
4. **Extend the existing optimizer into a very small candidate sweep**, not a benchmark wizard: baseline versus KV/FA, hybrid thread counts, warmup policy, and—only for eligible models—MTP.
5. **Benchmark native Qwen MTP rather than enabling it globally.** llama.cpp support is real and upstream measured large gains on one DGX Spark/Q8 methodology, while other hardware/version reports show net loss, regression, or non-activation. XE's existing `draft-mtp` launch contract is correct; the missing part is eligibility detection, benchmark identity, and an acceptance-aware keep/reject gate.

The architecture does **not** need to embed llama.cpp into .NET, replace llama.cpp with vLLM/SGLang, enable multi-slot datacenter batching, force CUDA kernels, globally use Q4 KV, or add NUMA/affinity magic values. Those moves have poor benefit-to-complexity for XE's consumer, primarily single-GPU workload.

## Evidence boundary

- **Repository facts** below are based on the cited source at the audited revision.
- **Upstream facts** are pinned to primary sources wherever possible.
- **Expected benefits** are conservative classifications unless a cited source provides a reproducible measurement.
- **No local inference throughput result is claimed.** The approved benchmark boundary prohibited a new CUDA build, and the installed Vulkan binary enumerated zero devices. A CPU-fallback run would not answer the GPU questions.
- Vendor performance claims from Unsloth are identified as vendor claims, not universal XE expectations.

## Current XE inference lifecycle

### Model acquisition and selection

- GGUF repository discovery, file selection, registry paths, model download, and header metadata are separated behind `XE-Local-AI-Engine.Providers.Abstractions/Gguf/*` and `XE-Local-AI-Engine.Providers.HuggingFace/*`; application wiring is in `XE-Local-AI-Engine.Client.Application/DependencyInjection/Modules/AddNodeModelRuntimeExtensions.cs:123-144`.
- The model-fit advisor is an estimator workflow, not a hidden launch benchmark. `XE-Local-AI-Engine.Client.Application/Services/ModelFit/*` discovers candidate files, reads GGUF facts, applies memory estimates, and walks a curated quality ladder down to a `Q3_K_M` product floor.
- XE already recognizes K-quants, IQ families, Unsloth Dynamic `UD-*`, MXFP4, and NVFP4. Dynamic labels remain distinct selectable artifacts but are priced against their base family (`XE-Local-AI-Engine.Providers.Abstractions/Gguf/GgufQuantParser.cs:11-38`, `XE-Local-AI-Engine.Providers.Abstractions/Gguf/QuantLadder.cs:24-75,80-128`).
- The download picker already uses a llama.cpp process-VRAM probe when it succeeds (`XE-Local-AI-Engine.Client.Application/Services/ModelFit/Gguf/GgufVariantRecommender.cs:39-106`).

### Runtime acquisition and backend selection

- `LlamaCppBinaryManager*` supports pinned official assets, a `XE_LLAMACPP_SERVER_PATH` override, managed CUDA, and Linux source builds.
- The current pin is b10201 (`XE-Local-AI-Engine.Providers.LlamaServer/LlamaCppReleasePins.cs:28-105`). Linux official assets cover CPU and Vulkan, not CUDA; CUDA is intentionally obtained through the managed/source-build path.
- The source build is Release, disables CURL, selects exactly CUDA or Vulkan, and sets detected CUDA architectures (`XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaCppSourceBuildService.cs:580-599`).
- `LlamaDeviceInventoryProbe` caches `--list-devices` by runtime identity and does not cache failed probes. `LlamaListDevicesProcessVramBudgetProbe` derives a process budget from the enumerated devices.

### Fit, launch, and lifecycle

- `ProcessContextAllocationResolver` chooses a stable context tier, models weights/KV/overhead, and permits only bounded OOM down-tier retries. Its estimator deliberately uses f16 KV sizing because optimized runtime KV can fall back (`XE-Local-AI-Engine.Client.Application/Services/Capacity/ProcessContextAllocationResolver.cs:102-245,417-474`; the conservative invariant is documented in `docs/agent-knowledge.md`).
- `InferenceProfileService` already implements explore -> benchmark -> freeze/invalidate for installed models and fingerprints the runtime/model/launch semantics (`XE-Local-AI-Engine.Client.Application/Services/Inference/InferenceProfileService.cs:94-330`). It benchmarks one explored profile; it is not yet a bounded multi-candidate tuner.
- The supervisor emits localhost-only serving, `--parallel 1`, and `--no-warmup` (`XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaServerProcessSupervisor.cs:1888-1920`). Single-slot is intentional: unused parallel slots would multiply KV allocation and push weights out of VRAM.
- Chat launches add Jinja tool support, optional mmproj, prefix reuse, bounded host prompt-cache RAM, and speculation. Embedding and rerank launches use role-specific endpoints and disable host prompt cache (`XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaServerProcessSupervisor.cs:1932-1997`).
- GPU explore uses `--fit on`; frozen profiles replay exact placement/KV/FA arguments. GPU launches expose metrics. CPU launches get context and CPU thread policy but currently no metrics (`XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaServerProcessSupervisor.cs:2016-2052`).
- A process cap, idle eviction, request leases, mutation gates, and role-aware yielding prevent unsafe concurrent lifecycle changes. Recent performance work also removed streaming O(n^2) behavior and bounded channels.

### Request and agent path

- `DeferredLlamaServerChatClient` starts or reuses the supervised server lazily, leases it for each request, and self-heals only before the first token.
- `LlamaServerOpenAIAdapterFactory` reuses a configured OpenAI-compatible client with explicit timeouts and zero retries. Loopback HTTP is not visibly a dominant cost compared with model execution.
- MAF/MEAI consumes the node-local `IChatClient`; full agent history, system instructions, and tools flow through the OpenAI-compatible chat endpoint. XE already enables `--cache-reuse 256` and a bounded `--cache-ram`, so repeated-prefix work is not wholly repeated by design.

## Mechanisms already implemented — do not duplicate

| Mechanism | Current state |
|---|---|
| GPU offload | llama.cpp `--fit on` exploration plus replayable `-ngl`/placement arguments. |
| Context sizing | Metadata-aware tier selection with stable RAM/VRAM budgets and bounded OOM down-tiering. |
| KV cache | GPU explore defaults to Q8 K/V plus Flash Attention; readiness failure records a safe fallback. Estimation stays conservatively f16. |
| Parallelism | Explicit `--parallel 1`; matches the application's single-user dispatcher and protects consumer VRAM. |
| Prompt caching | `--cache-reuse` plus an explicit RAM budget; pooled roles disable host prompt cache. |
| CPU fallback | Separate CPU binary/launch policy; runtime device audit detects confirmed silent GPU fallback. |
| CPU threads | Physical-core heuristic with host reserve and separate batch threads for CPU builds. |
| Vulkan/CUDA/source build | Official CPU/Vulkan assets, managed CUDA, BYO path, and Linux source-build lifecycle. |
| Continuous batching | Inherited llama-server behavior; intentionally one application slot. |
| Speculation classes | External draft, main-model MTP, and draftless n-gram modes are distinguished correctly. |
| MTP launch | `draft-mtp` uses the main GGUF heads and does not require a second model (`XE-Local-AI-Engine.Providers.LlamaServer/Options/SpeculativeDecodingSettings.cs:14-33,71-83,124-166`). |
| Streaming | Delta-only transport/persistence and bounded channels were implemented in `f75482b8`. |
| Benchmark/freeze | Golden prompt/agent/cache/tool/embedding/rerank metrics and profile fingerprinting exist. |
| Model quant discovery | K/IQ/Dynamic/native FP4 parsing, quality tiers, and a conservative automatic quality floor exist. |

## Backend and current-flag disposition

| Area | Current evidence and XE disposition |
|---|---|
| CUDA graphs and kernel selection | XE's managed/source build targets the detected CUDA architecture. Current upstream compiles CUDA graphs and CUDA Flash Attention in by default, auto-selects their runtime use where applicable, and selects MMQ/cuBLAS heuristically. Preserve those defaults; forcing MMQ/cuBLAS, disabling graphs, or enabling unified-memory fallback is not justified without a card/model/workload benchmark. `GGML_CUDA_FA_ALL_QUANTS` is a separate build candidate only when a tested KV format needs it. See the [upstream build guide](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/docs/build.md). |
| Vulkan | XE ships/pins an official Vulkan path and supports a source build, but performance and FA shader selection depend on vendor extensions, driver, model, and KV type. The local b10201 binary found no Vulkan device in WSL, so no throughput inference was made. Treat Vulkan FA/KV as a per-device/driver profile, not a global switch; prefer reliability over a small gain. See the [server options](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/tools/server/README.md) and [Vulkan build requirements](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/docs/build.md#vulkan). |
| CPU x86 | llama.cpp supplies architecture-specific x86 kernels, while native machine builds and portable distributed builds have different ISA tradeoffs. XE should retain portable official binaries and let a local source-build profile use detected ISA. BLAS is a PP candidate, not a TG optimization. Thread sweeps matter more than affinity magic. See the [CPU/build guidance](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/docs/build.md) and [thread evidence](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/docs/development/token_generation_performance_tips.md). |
| CPU ARM/AMX | XE already pins Windows ARM64 CPU, Linux ARM64 CPU/Vulkan, and macOS ARM64 assets (`XE-Local-AI-Engine.Providers.LlamaServer/LlamaCppReleasePins.cs:85-103`), but the live audit host is x64 and produced no ARM performance evidence. Treat ARM performance validation—not packaging existence—as a separate hardware lane. AMX/native-ISA builds are optional machine-local candidates only after the capability manifest prevents an incompatible binary from being distributed. |
| Batch and micro-batch | Current upstream defaults are logical batch 2048 and physical micro-batch 512. XE explicitly raises these for pooled roles because an entire non-causal input must fit one micro-batch; that is correctness-sensitive. Chat batch/ubatch should be a bounded profile candidate only when PP and memory evidence justify it. |
| `--no-host`, operation offload, repack | Current upstream defaults keep operation offload and repacking enabled; `--no-host` changes host-buffer placement. XE does not override them. Preserve defaults: they are backend/model dependent and a poor universal tuning surface. |
| Unified KV, idle-slot cache, checkpoints | llama-server exposes unified/shared KV and host prompt-cache/slot controls. XE's single-slot process already avoids multi-slot KV multiplication, and explicit host-cache RAM is bounded. Idle-slot and checkpoint persistence add little for one active slot and create memory/state complexity; monitor rather than enable by default. |
| Multi-GPU | `layer` is the stable documented path; tensor split is experimental and row split is deprecated. These are outside XE's primary single-GPU target and must not complicate the default policy. See [multi-GPU documentation](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/docs/multi-gpu.md). |

## Local validation and benchmark preflight

### Reproducible environment

- OS: WSL2 Linux (6.18-series kernel), x86_64.
- Visible CPU: x86-64 desktop CPU, 8 threads exposed to WSL, one NUMA node.
- GPU: NVIDIA GeForce RTX 5090; driver 610.88; 32,607 MiB reported total VRAM.
- Installed XE runtime: official Vulkan b10201, commit `8f4646a63`, GNU 11.4.0; server SHA-256 `972e6785e0caf0dbe761ba02654c4e102845d19d75b5c57799c59a602f4f331f`.
- Existing approved model asset: `Qwen3.6-27B-Q4_K_M.gguf`, 17,106,773,120 bytes.

### Device preflight

Command:

```text
~/.local/share/XE-Local-AI-Engine/llama.cpp/b10201/vulkan/llama-b10201/llama-server --list-devices
```

Result:

```text
Available devices:
  (none)
```

This is configuration/outcome divergence: the installed record says Vulkan, but this binary has no usable Vulkan device in the current WSL environment. The existing GPU smoke's outcome-based design is therefore correct. No Qwen throughput run was performed because it would be a CPU-fallback benchmark presented under a GPU research question.

### Aspire validation

The worktree AppHost compiled and started under `aspire start --isolated`; the dashboard reached `Running`/ready at `https://localhost:44863`. The detached AppHost was no longer present before application/UI inspection, and `aspire ps --apphost ...` was rejected because `ps` does not accept that option. No production behavior was changed, so this does not invalidate a code path; it limits live-UI evidence for this report. No Chrome validation was useful after the host exited.

## Detailed proposals

### 1. Cross-vendor dual-axis VRAM evidence

**Optimization**

Obtain independent global-pressure evidence for AMD/Intel while preserving llama.cpp's process budget as a separate axis.

**Current XE behavior**

`HardwareProfiler` obtains NVIDIA total/free bytes via `nvidia-smi`. Linux AMD/Intel returns unknown VRAM; the Windows non-NVIDIA DXGI seam is also deferred (`XE-Local-AI-Engine.Providers.Capabilities/Implementation/HardwareProfiler.cs:110-140,197-206,302-321`). Unknown VRAM forces `GpuAccelAvailable=false`. The runtime audit separately calls `--list-devices`, and the optimizer already records global-free and llama.cpp process-budget readings independently (`XE-Local-AI-Engine.Client.Application/Services/Inference/InferenceProfileService.cs:305-317`). The context allocator requires stable host evidence before consuming the process budget (`XE-Local-AI-Engine.Client.Application/Services/Capacity/ProcessContextAllocationResolver.cs:417-474`). This separation is load-bearing: under WDDM, XE measured 492 MiB globally free while llama.cpp reported a 29,697 MiB process residency budget ([WSL2 hardware and VRAM readers](../agent-knowledge-evidence.md#wsl2-hardware-and-vram-readers)).

**Proposed change**

Keep two typed measurements: (1) independent global total/free or budget/usage evidence for admission/invalidation and (2) llama.cpp's selected-device/process ceiling for process fit. Investigate platform-native, dependency-free sources such as validated Linux DRM/vendor memory files and Windows DXGI video-memory budget/usage APIs. Integrated/shared-memory GPUs need a separate conservative policy. A successful llama inventory may mark the backend/device as reachable, but it must never fill a missing global-free field. Until both axes are understood for a platform/vendor, keep admission fail-closed or explicitly “unknown” rather than manufacturing capacity.

**Evidence**

llama.cpp documents `--list-devices` as the runtime authority for reachable offload devices in the current [server arguments](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/tools/server/README.md). XE's own WDDM measurements prove it is not a substitute for global-free evidence. The proposal therefore extends host evidence while preserving the current deliberate split.

**Expected benefit**

**High on affected AMD/Intel systems if a trustworthy source is validated; negligible on NVIDIA systems already covered by `nvidia-smi`.** Correct admission, GPU model-fit recommendations, context choice, and fewer misleading CPU-mode decisions.

**Applicable hardware**

Linux Vulkan on AMD/Intel; Windows Vulkan on AMD/Intel when the runtime exposes byte counts; potentially nonstandard NVIDIA environments where `nvidia-smi` is unavailable but llama.cpp can enumerate the device.

**Tradeoffs**

Platform APIs may report budgets rather than physical free bytes; integrated/shared memory is not dedicated VRAM; WDDM changes semantics; and Linux vendor files differ. Multi-GPU totals must not be summed without placement semantics.

**Implementation complexity**

Medium to large.

**Confidence**

Medium until validated on real AMD and Intel machines.

**Recommendation**

**Benchmark first.** Implement per-platform sources only after tests prove their semantics under external pressure.

### 2. Binary capability manifest and benchmarked pin refresh

**Optimization**

Probe each resolved llama-server binary once and gate launch features by its actual CLI/capability identity.

**Current XE behavior**

The launch contract is coded against b10201 behavior, while XE can run pinned, managed-source, latest/custom-source, and operator-supplied binaries. `LlamaDeviceInventoryProbe` already demonstrates the right cache key shape (binary path plus identity), but no equivalent cached `--help`/`--version` feature manifest exists. The pinned source comments are periodically manually refreshed. At research time upstream had advanced from XE's b10201 to b10375.

**Proposed change**

Resolve a capability manifest keyed by executable path, modification time/hash, and reported version. Record support for at least `--fit`, `--cache-ram`, `--load-mode`, accepted KV types, Flash Attention mode syntax, metrics, MTP/n-gram modes, and any build-specific limitations XE uses. Fail closed for mandatory correctness flags; omit or fall back for optional optimizations. Couple pin updates to the existing golden benchmark and GPU/tool grammar smokes rather than to freshness alone.

**Evidence**

The current llama-server surface includes `--fit`, `--load-mode`, KV type lists, metrics, and speculation, but this project changes quickly; compare XE's pin with [release b10375](https://github.com/ggml-org/llama.cpp/releases/tag/b10375) and the pinned [current server options](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/tools/server/README.md). Native MTP alone landed in May 2026 in [PR #22673](https://github.com/ggml-org/llama.cpp/pull/22673), illustrating why version labels are not enough.

**Expected benefit**

**High reliability, medium indirect performance.** It safely unlocks newer optimizations, prevents restart loops from unsupported flags, and makes BYO/source builds first-class rather than optimistic.

**Applicable hardware**

All systems; especially managed CUDA and custom/BYO runtime users.

**Tradeoffs**

CLI help parsing is not a perfect ABI. Keep a small semantic probe set, cache it, and retain startup/readiness fallback.

**Implementation complexity**

Small to medium.

**Confidence**

High.

**Recommendation**

**Implement.**

### 3. Production phase and placement feedback

**Optimization**

Correlate existing benchmark metrics with missing load/queue/placement/speculation phases before using production outcomes for policy.

**Current XE behavior**

XE already persists a substantial benchmark record: PP/TG, TTFT, total and tool-loop latency, a cache-hit ratio derived from cold-versus-warm prompt-token deltas, separate global-free/process-budget minima, peak process RAM, pooled-role latency/throughput, and correctness fields (`XE-Local-AI-Engine.Client.Application/Services/Inference/IInferenceBenchmarkHarness.cs:184-232`; cache derivation at `XE-Local-AI-Engine.Client.Application/Services/Inference/InferenceBenchmarkHarness.cs:759-768`). GPU launches expose metrics; CPU launches omit them. The remaining gap is not another general benchmark store: it is correlation of cold load/readiness, queue/deferred time, actual placement, speculation acceptance, and comparable production phases with the frozen profile.

**Proposed change**

Extend the existing profile/benchmark diagnostics—not a second capacity ledger—with binary identity, resolved args, cold load/readiness phases, machine-readable fitted placement, active/deferred request deltas, speculation draft/accept counters, cancellation outcome, and matching production phase timings. Enable metrics on CPU if the capability probe supports them. Keep the first release report-only; later policy may invalidate a materially regressed profile or suggest recalibration, but capacity admission remains owned by the existing ledgers and load gate.

**Evidence**

llama-server exposes active/deferred requests, prompt/decode counters and throughput, busy-slot state, and speculative counters through its [metrics endpoint](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/tools/server/README.md#available-metrics). It does **not** expose direct KV-byte or cache-reused-token metrics in the audited surface; XE's cache-hit value is an inference from prompt-token deltas. XE already enables metrics on GPU replay/explore (`XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaServerProcessSupervisor.cs:2021-2027`).

**Expected benefit**

**Medium.** Clear load/queue/agent-loop attribution and evidence for recalibration. Direct token/s improvement is indirect and depends on a later policy consuming the evidence.

**Applicable hardware**

All systems and all roles.

**Tradeoffs**

Telemetry cardinality/privacy must be controlled; never persist prompt text or tool arguments. Sampling and bounded retention are sufficient.

**Implementation complexity**

Medium.

**Confidence**

High.

**Recommendation**

**Implement.**

### 4. Bounded per-hardware/model auto-calibration

**Optimization**

Extend explore -> benchmark -> freeze into a small, one-variable-at-a-time candidate sweep.

**Current XE behavior**

`InferenceProfileService` explores one auto-fit result, benchmarks that result, and lets the operator freeze it (`XE-Local-AI-Engine.Client.Application/Services/Inference/InferenceProfileService.cs:94-330`). Per-model extra llama.cpp arguments exist for advanced users, but the application does not automatically compare a small set of alternatives.

**Proposed change**

After the prerequisite evidence work, generate at most a handful of guarded candidates from detected facts, not a combinatorial search. Suggested order: baseline safe config -> KV/FA candidate -> partial-offload threads if applicable -> batch/ubatch only for a measured PP bottleneck -> post-ready synthetic warm -> eligible MTP or n-gram candidate. Stop early when gains are below noise or memory/quality gates fail. Cache the winner by model content hash/quant, runtime hash, backend/device/driver, RAM/VRAM tier, context, role, and workload class. Always retain the safe baseline.

**Evidence**

Upstream explicitly recommends measuring thread count rather than using all logical cores and documents hardware-dependent batch/ubatch, FA, KV, and speculation behavior. See [performance tips](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/docs/development/token_generation_performance_tips.md), [server options](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/tools/server/README.md), and [speculation guidance](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/docs/speculative.md).

**Expected benefit**

**High potential, hardware dependent.** Avoids pathological defaults and may improve PP, TG, TTFT, VRAM, or agent-loop latency. The largest value is robust defaults, not chasing 1-3% wins.

**Applicable hardware**

All systems, with backend-specific candidate sets.

**Tradeoffs**

Calibration costs time and can consume memory. It must be opt-in/on-idle, cancellable, bounded, and invalidated on material fingerprint changes.

**Implementation complexity**

Medium to large.

**Confidence**

Medium.

**Recommendation**

**Implement after the Tier 1 measurement work; benchmark each candidate family separately.**

### 5. Capability-aware native MTP profile

**Optimization**

Automatically recognize MTP-capable GGUFs and benchmark `draft-mtp` off versus on, beginning with draft ceiling 2.

**Current XE behavior**

XE correctly classifies `draft-mtp` as using the main model's heads and emits no external draft model (`XE-Local-AI-Engine.Providers.LlamaServer/Options/SpeculativeDecodingSettings.cs:14-33,71-83`). Speculation defaults off. The setting is currently documented as orthogonal to the frozen inference profile and does not invalidate that profile (`XE-Local-AI-Engine.Providers.LlamaServer/Options/SpeculativeDecodingSettings.cs:45-48`). The launcher can also add mmproj independently, which is risky while current vendor instructions still say MTP plus mmproj is unsupported.

**Proposed change**

Detect MTP metadata/model architecture during GGUF inspection; require a capability-manifest-positive runtime. Make speculation configuration part of benchmark identity. Compare MTP off with `--spec-type draft-mtp --spec-draft-n-max 2`, f16 draft KV, `--parallel 1`, and no projector until the pinned build is explicitly validated. Record TG, PP, TTFT, peak memory, acceptance, tool/coding quality, and deterministic greedy output. Retain MTP only when total latency improves by a material threshold; never assume the vendor multiplier.

**Evidence**

Native MTP was merged in llama.cpp [PR #22673](https://github.com/ggml-org/llama.cpp/pull/22673). Its reported nine-prompt **DGX Spark, Q8** run reduced total wall time from 201.07s to 83.8s with draft ceiling 3 and to 90.44s with ceiling 2, but also noted a prompt-processing penalty; it is not consumer-Q4 evidence. That merged PR says vision and parallel decoding are compatible, while the current Unsloth [Qwen3.6-27B card](https://huggingface.co/unsloth/Qwen3.6-27B-MTP-GGUF) and [35B-A3B card](https://huggingface.co/unsloth/Qwen3.6-35B-A3B-MTP-GGUF) say `--mmproj` and `-np > 1` are not yet supported, claim 1.5-2x, and recommend ceiling 2. Treat the conflict as an exact-model/exact-pin validation gate. Counter-evidence includes a historical [Vulkan b9265-versus-b9354 regression report](https://github.com/ggml-org/llama.cpp/issues/23774)—not proof of a b10201 or b10375 defect—a [Metal net-loss report](https://github.com/ggml-org/llama.cpp/issues/23752), and an open [Turing/MoE non-activation report](https://github.com/ggml-org/llama.cpp/issues/24670).

**Expected benefit**

**High potential when it works; negative when it does not.** Primary gain is generation throughput and multi-step agent latency. PP/TTFT and memory may regress. No multiplier is assumed for consumer Q4.

**Applicable hardware**

MTP-capable GGUFs; most promising first on recent CUDA single-GPU systems with spare VRAM. Vulkan/Metal/older NVIDIA require separate proof. Dense and MoE must be evaluated separately.

**Tradeoffs**

Extra context/compute memory, backend regressions, prompt-processing penalty, acceptance variability, output/determinism risk, and current concurrency/vision uncertainty.

**Implementation complexity**

Medium.

**Confidence**

Medium/experimental.

**Recommendation**

**Benchmark first.**

### 6. Backend/model-specific KV plus Flash Attention matrix

**Optimization**

Replace the single optimized/safe binary choice with a measured, capability-gated KV/FA candidate matrix.

**Current XE behavior**

GPU explore tries symmetric Q8 K/V plus Flash Attention, then records one backend-wide safe fallback if readiness fails (`XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaServerLaunchPolicy.cs:52-79`). The context estimator intentionally budgets f16. Frozen profiles can preserve explicit KV/FA arguments. This is safe and already saves memory when the optimized path works, but it cannot distinguish model, GPU generation, driver, context, or a fast versus merely loadable combination.

**Proposed change**

Benchmark only supported combinations: f16/FA-auto baseline; current q8_0/q8_0 plus FA; and q4 only as an explicit memory-pressure candidate with task-quality checks. Key the result to runtime/backend/device/driver/model/context. Keep K/V symmetry by default. If testing additional CUDA KV quants, ensure the managed build exposes the required kernels/capabilities rather than assuming `GGML_CUDA_FA_ALL_QUANTS`. Keep the estimator conservative until observed evidence justifies a profile-specific adjustment.

**Evidence**

Current llama-server allows f16, q8, q4, IQ and related K/V formats; quantized V requires Flash Attention, and some multi-GPU modes are incompatible. See the [current server options](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/tools/server/README.md). CUDA FA is built by default; `GGML_CUDA_FA_ALL_QUANTS` expands combinations at build-time cost, per the [build guide](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/docs/build.md). Raw cache element storage is roughly halved from f16 to q8 and quartered from f16 to q4 before quant block overhead, but real model quality/speed is not free.

**Expected benefit**

**High VRAM/model-fit potential; medium and uncertain speed benefit.** Can permit more context or more weight offload. Quality and backend throughput may decline.

**Applicable hardware**

CUDA and Vulkan separately; most relevant to long context and 8-16 GB VRAM.

**Tradeoffs**

Quality changes, backend-specific kernel cliffs, extra calibration time, and a larger profile matrix.

**Implementation complexity**

Medium.

**Confidence**

Medium.

**Recommendation**

**Benchmark first.** Do not globally enable Q4 KV.

### 7. Partial-offload CPU thread calibration

**Optimization**

Apply a bounded CPU decode/batch thread sweep when a GPU launch actually retains layers or experts on CPU.

**Current XE behavior**

CPU builds use a physical-core heuristic. Every GPU launch, including partial offload and CPU-MoE placement, omits `--threads` and `--threads-batch` (`XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaServerLaunchPolicy.cs:38-72,82-105`).

**Proposed change**

Only when fit/startup output proves hybrid placement, compare a few topology-aware candidates: 1, half physical, and physical-minus-host-reserve for decode; physical cores for batch. Preserve explicit operator overrides. Do not add affinity, priority, polling, or NUMA changes without separate evidence.

**Evidence**

llama.cpp warns that too many CPU threads can reduce performance even with GPU offload and recommends a sweep. Its illustrative A6000/30B example measured 5.5 tok/s at one thread, 8.7 at seven, and 9.1 at four; this proves non-monotonicity, not a transferable value. See [upstream performance tips](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/docs/development/token_generation_performance_tips.md).

**Expected benefit**

**Medium on hybrid placement; negligible when fully offloaded.** Generation and PP can improve, and host responsiveness can be protected.

**Applicable hardware**

RAM-rich/VRAM-constrained systems, partial GPU offload, and MoE expert CPU placement.

**Tradeoffs**

Topology/OS sensitivity and calibration cost; WSL/container CPU visibility may differ from the host.

**Implementation complexity**

Small to medium.

**Confidence**

Medium-high.

**Recommendation**

**Benchmark first, then implement only for proven hybrid profiles.**

### 8. Agent-prefix reuse, prompt stability, and SWA

**Optimization**

Measure whether agent turns hit the existing prefix cache and fix only demonstrated prefix instability.

**Current XE behavior**

Chat servers emit `--cache-reuse 256` and an explicit host-cache budget (`XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaServerProcessSupervisor.cs:1947-1966`; `XE-Local-AI-Engine.Providers.LlamaServer/Options/LlamaServerSupervisorOptions.cs`). The application resends selected conversation history, system instructions, and tools. The benchmark harness derives a cold-versus-warm cache-hit ratio, but production evidence does not clearly attribute reuse and prompt evaluation to individual agent steps. The prior audit also found an unresolved Sliding Window Attention interaction: without `--swa-full`, out-of-window KV may be unavailable for prefix reuse, while full-size SWA materially increases KV memory (`docs/audits/2026-08-07-ai-inference-stack-performance-audit-v2.md:85-93,148`).

**Proposed change**

Add an agent fixture with a large stable system prompt and tool schema, then three short sequential tool/chat calls. Record prompt-token deltas/cache-hit inference, PP, TTFT, and total step latency. Compare cache reuse off/on and inspect whether system/tool serialization is byte/token stable. Include an SWA model and compare the default with `--swa-full` only when the memory estimator proves headroom; on constrained machines, preferring bounded KV over prefix reuse is acceptable. Only if avoidable misses are observed, make ordering/serialization deterministic at the highest safe layer. Do not cache across users or persist slot state by default.

**Evidence**

llama-server prompt caching is default-on; `--cache-reuse`, slot similarity, slot save/restore, and `--swa-full` are documented in the [server options](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/tools/server/README.md). These mechanisms only help shared prefixes and interact with the model's attention window, so an agent-specific trace is more useful than a generic single-turn benchmark.

**Expected benefit**

**Potentially high for agent-loop TTFT; uncertain incremental benefit because XE already enables the mechanism.**

**Applicable hardware**

All backends; strongest for large system/tool prompts and many short generation steps.

**Tradeoffs**

Host RAM, invalidation complexity, possible sensitive cross-session state, and limited benefit when prompts genuinely change.

**Implementation complexity**

Small for telemetry/fixture; medium if prompt construction must change.

**Confidence**

Medium.

**Recommendation**

**Benchmark first.** Reject persistent cross-session slot caches as a default.

### 9. Post-ready warm profiling and pressure-informed residency

**Optimization**

Keep the mandatory `--no-warmup` launch invariant, but measure an optional post-ready synthetic warm and pressure/hotness-informed eviction.

**Current XE behavior**

Every process uses `--no-warmup` because large-model warmup previously exceeded readiness and caused respawn loops (`XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaServerProcessSupervisor.cs:1916-1920`). Model loading otherwise inherits upstream's default auto load mode (normally mmap). XE caps loaded processes at three and uses a 15-minute idle TTL, with role-aware yielding.

**Proposed change**

Every normal spawn must continue to emit `--no-warmup --parallel 1`; do not benchmark removing that launch invariant. Instead, after readiness, A/B a bounded synthetic warm against first-real-request TTFT for profiles expected to remain resident. For eviction, consume existing process roles/hotness, `PendingFootprintLedger`, independent global pressure, and the serialized GPU-load re-evaluation; do not create a second authoritative byte ledger. Preserve mmap/OS page-cache behavior. Make mlock only an advanced high-headroom option.

**Evidence**

Current upstream replaces mmap/mlock flags with `--load-mode`; `auto` normally chooses mmap, while mlock prevents paging and consumes real resident RAM. See [server loading options](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/tools/server/README.md). XE's measured 45-110s launch warmup history is preserved under [large-model launch warmup timing](../agent-knowledge-evidence.md#large-model-launch-warmup-timing); together with the paid-for spawn/load invariants, it outweighs a generic upstream default for the normal spawn path.

**Expected benefit**

**Medium.** Lower first-user TTFT and fewer reloads/model-switch stalls without increasing OOM risk.

**Applicable hardware**

All systems; pressure/hotness-informed retention is most useful on 16-32 GB RAM and 8-16 GB VRAM.

**Tradeoffs**

Background warm consumes compute; longer residency consumes RAM/VRAM and may block a model switch. mlock can harm the OS on constrained hosts.

**Implementation complexity**

Medium.

**Confidence**

Medium.

**Recommendation**

**Benchmark first.** Retain mmap/auto; do not globally enable mlock.

### 10. Build-profile experiments, not global build flags

**Optimization**

Benchmark a small number of reproducible source-build profiles and record their capabilities.

**Current XE behavior**

The managed/source build already uses Release, the selected backend, and an exact detected CUDA architecture. Upstream defaults already enable native CPU targeting for a machine-local build and CUDA graphs/Flash Attention where supported. XE does not explicitly enable BLAS, LTO, `GGML_CUDA_FA_ALL_QUANTS`, or force MMQ/cuBLAS.

**Proposed change**

Keep official distributed binaries portable. For machine-local source builds, compare only justified profiles: baseline; OpenBLAS for CPU-heavy prompt processing; and CUDA all-quant FA only if the KV candidate set needs it. Record compiler, CMake options, runtime hash, driver, and supported features. Do not force MMQ/cuBLAS, disable graphs, or enable unified-memory fallback as a default.

**Evidence**

The [llama.cpp build guide](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/docs/build.md) states BLAS mainly helps prompt processing above modest batch sizes, not token generation; forced MMQ/cuBLAS have hardware- and workload-dependent memory/performance tradeoffs. Unified-memory fallback is a survivability mechanism on discrete GPUs, not a speed optimization.

**Expected benefit**

**Low to medium.** CPU prefill may improve materially on some systems; decode is unlikely to. CUDA kernel/build changes may be negligible or negative.

**Applicable hardware**

CPU-only and CPU-assisted inference; custom CUDA builds.

**Tradeoffs**

Build time, binary-cache complexity, compiler/platform variability, and portability risk.

**Implementation complexity**

Medium.

**Confidence**

Medium-low until measured.

**Recommendation**

**Optional advanced setting / benchmark first.**

### 11. Quantization-aware recommendations with task-quality evidence

**Optimization**

Keep current quant recognition, but make recommendations publisher/artifact-aware and quality-gated rather than treating nominal names as universal.

**Current XE behavior**

XE already recognizes Q4_K_M, IQ families, Dynamic `UD-*`, and native FP4, preserves Dynamic identity, and has a Q3_K_M automatic floor. It does not quantize models. Dynamic quants are ranked using the base token, which is conservative but cannot express a publisher's per-tensor recipe or task-specific quality.

**Proposed change**

Do not change the global Q4_K_M baseline automatically. Attach artifact provenance and a small evaluation label to benchmarked variants. For a given model family, compare standard Q4_K_M, a reputable Dynamic Q4-Q5 artifact, and a quality-first Q5/Q6 candidate only when memory permits. Low-bit IQ should remain an explicit capacity-first choice with coding, tool-call, structured-output, and long-context checks.

**Evidence**

llama.cpp's [quantizer documentation](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/tools/quantize/README.md) supports importance matrices and per-tensor overrides and warns against requantizing already quantized models. [Unsloth Dynamic 2.0](https://unsloth.ai/blog/dynamic-v2) describes model-specific per-layer recipes and reports vendor quality results; those results do not prove faster kernels or universal task quality.

**Expected benefit**

**Medium model-fit/quality benefit; low direct runtime speed benefit.** The main value is fitting a stronger artifact without silently sacrificing agent quality.

**Applicable hardware**

All systems, especially RAM/VRAM-constrained machines.

**Tradeoffs**

Evaluation cost, publisher trust/provenance, and model-specific results. Nominal bpw does not predict backend speed.

**Implementation complexity**

Small to medium for metadata; large if expanded into a full evaluation service.

**Confidence**

Medium.

**Recommendation**

**Optional advanced setting / benchmark first.** Do not turn XE into a quantizer.

### 12. Draftless and external speculative decoding

**Optimization**

Consider `ngram-mod` for repetitive coding/agent workloads; leave second-model draft methods advanced-only.

**Current XE behavior**

XE already exposes draft-simple, EAGLE3, native MTP, and several n-gram modes, default off (`XE-Local-AI-Engine.Providers.LlamaServer/Options/SpeculativeDecodingSettings.cs:71-83`). External drafts require a second GGUF and additional weight/KV memory; n-gram modes do not.

**Proposed change**

After MTP infrastructure exists, benchmark `ngram-mod` against the repeated agent/coding fixture because it costs little memory. Expose acceptance and net latency. Keep EAGLE3/DFlash/DSpark/external simple drafts advanced-only and require explicit compatible model metadata and a full memory estimate.

**Evidence**

The current [speculation documentation](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/docs/speculative.md) describes `ngram-mod` as a lightweight shared-pool option and newer EAGLE3/DFlash/DSpark as model-specific methods. Their benefit depends on cheap drafting and high acceptance.

**Expected benefit**

**Medium for repetitive code/rewrite prompts; low for ordinary unique chat.** External draft gains can be high but often lose the consumer-memory tradeoff.

**Applicable hardware**

N-gram: all systems. External drafts: higher-VRAM systems with a compatible small drafter.

**Tradeoffs**

Acceptance variability, more configuration, second-model VRAM/RAM, tokenizer/model compatibility, and quality/determinism checks.

**Implementation complexity**

Small for a benchmark candidate; medium/large for automatic draft-model management.

**Confidence**

Medium for n-gram; experimental for newer external methods.

**Recommendation**

**Optional advanced setting / benchmark first.** Prefer native MTP or n-gram over a second model on constrained hardware.

## Rejected or monitor-only items

| Item | Decision | Reason |
|---|---|---|
| Increase `--parallel` / multi-user batching | **Reject for now** | XE's application dispatcher and typical single-user workload cannot use extra slots enough to justify multiplied KV memory. Revisit only with real concurrent queue telemetry. |
| Multi-GPU tensor/row split tuning | **Optional/monitor** | Single GPU is the primary target. Upstream tensor mode is experimental and has KV/FA constraints; row mode is deprecated. See [multi-GPU docs](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/docs/multi-gpu.md). |
| Static batch/ubatch magic values | **Reject** | Workload, context, backend, and memory dependent. Pooled role batch values are correctness-sensitive; chat should be calibrated, not globally raised. |
| Global CPU affinity, polling, priority, NUMA | **Reject as defaults** | One NUMA-node consumer systems rarely justify the complexity. Keep expert overrides; measure on exceptional hosts. |
| Global mlock or disabling mmap | **Reject** | Auto/mmap supports fast warm starts and OS page cache. mlock can starve 16-32 GB machines; non-mmap generally worsens cold load. |
| Force CUDA MMQ or cuBLAS | **Reject as defaults** | Upstream documents workload/card-specific tradeoffs. Current auto selection is safer. |
| CUDA unified-memory fallback as an optimization | **Reject** | Avoids OOM by paging into RAM but is normally much slower on a discrete GPU. |
| Embed llama.cpp in the .NET process | **Reject** | **Architectural inference:** loopback serialization is unlikely to dominate model execution, while the supervised process supplies isolation, upgrade, device-probe, lifecycle, and crash-containment value. Profile transport before revisiting. |
| Replace llama.cpp with vLLM/SGLang/TensorRT-LLM | **Reject** | Qwen's [model card](https://huggingface.co/unsloth/Qwen3.6-27B-MTP-GGUF) positions vLLM/SGLang for high-throughput serving with original-model stacks. **Architectural inference:** adding those CUDA/server-oriented paths would be a second engine, weaken CPU/Vulkan/Windows coverage, and is not justified by consumer single-user evidence. |
| Unsloth PyTorch/Triton training/inference tricks | **Not transferable** | Unsloth documents `fast_inference=True` as a [vLLM integration](https://unsloth.ai/docs/new/vision-reinforcement-learning-vlm-rl). Its transferable value here is GGUF artifacts and orchestration/capability UX, not PyTorch/Triton kernels inside llama.cpp. |
| Persistent cross-session slot cache | **Reject as default** | Privacy, invalidation, disk/RAM, and model/profile identity risks exceed the unmeasured benefit. |
| Paged KV/scheduler prototype | **Monitor upstream** | Upstream [discussion #21961](https://github.com/ggml-org/llama.cpp/discussions/21961) is design/prototype work, not a stable server feature. |
| KV-Direct | **Monitor upstream** | Upstream [discussion #21911](https://github.com/ggml-org/llama.cpp/discussions/21911) is research/design work, not a deployable llama.cpp facility. |
| Backend sampling | **Monitor upstream** | The current [speculation documentation](https://github.com/ggml-org/llama.cpp/blob/9558fa44c92746a58dd07ad1bf0c889715b938a6/docs/speculative.md) marks it experimental and describes compatibility/fallback limits. |
| EAGLE3, DFlash, DSpark auto-default | **Monitor/advanced only** | The merged implementations are recent and model-specific: [EAGLE3 #18039](https://github.com/ggml-org/llama.cpp/pull/18039), [DFlash #22105](https://github.com/ggml-org/llama.cpp/pull/22105), and [DSpark #25173](https://github.com/ggml-org/llama.cpp/pull/25173). They require compatible draft artifacts and are unsuitable as a general consumer default. |

## Prioritized implementation candidates

### Tier 1 — High-value / low-risk

1. Cached binary capability manifest plus benchmarked pin-upgrade gate.
2. Report-only production phase/placement feedback, including CPU metrics where supported and agent-step attribution.

### Tier 2 — Valuable but benchmark first

1. Cross-vendor dual-axis global-pressure/process-budget evidence, validated on real AMD/Intel machines.
2. Bounded per-model/hardware auto-calibration on top of the existing explore/benchmark/freeze workflow.
3. Native MTP off/on candidate with eligibility, memory, acceptance, and quality gates.
4. Backend/model/context-specific KV plus Flash Attention candidates.
5. Partial-offload CPU thread sweep.
6. Agent-prefix/SWA/cache evidence and prompt-stability repair only if misses are demonstrated.
7. Post-ready synthetic warm plus pressure/hotness-informed residency using existing ledgers.

### Tier 3 — Optional / hardware-specific

1. CPU BLAS/custom build profile for prompt-heavy CPU workloads.
2. CUDA all-quant FA build only when a measured KV candidate needs it.
3. Quant-artifact provenance and task-quality labels for Dynamic/IQ choices.
4. N-gram speculation for repetitive coding/agent traces.
5. Compatible external draft models on high-headroom systems.
6. Advanced mlock, affinity, NUMA, and explicit device/split controls.

### Tier 4 — Not recommended / monitor upstream

1. More parallel slots before the application has genuine concurrent demand.
2. Multi-GPU tensor mode as a consumer default.
3. Global Q4 KV, forced CUDA kernels, unified-memory fallback, or static batch/thread magic values.
4. Embedded llama.cpp or a replacement inference engine.
5. Paged KV, KV-Direct, backend sampling, and automatic EAGLE3/DFlash/DSpark until upstream stabilizes and compatible models are common.

## Suggested implementation and benchmark order

Keep the lanes separate so each result is attributable:

```text
Capability manifest
  -> unit/contract tests against pinned, old, and fake/BYO help outputs
  -> pin-upgrade smoke checkpoint

Cross-vendor dual-axis VRAM evidence
  -> prove global-pressure and process-budget sources stay distinct in tests
  -> validate Linux AMD, Linux Intel, and Windows AMD/Intel under external pressure
  -> remain fail-closed where no trustworthy global source exists
  -> admission/model-fit/context comparison checkpoint

Report-only observed telemetry
  -> existing-metrics regression lock
  -> load/queue/placement/speculation correlation
  -> agent workload fixture and cache-hit inference

Bounded candidate runner
  -> safe baseline repeatability/noise threshold
  -> KV/FA A/B
  -> hybrid thread A/B
  -> PP-only batch/ubatch A/B when indicated
  -> post-ready synthetic-warm/residency A/B (normal launch retains --no-warmup)

MTP eligibility + profile identity
  -> Qwen3.6-27B MTP dense A/B at draft ceiling 2
  -> quality/tool/coding checkpoint
  -> Qwen3.6-35B-A3B MTP MoE A/B
  -> Vulkan/older-GPU deny or allow profile from evidence

Agent-prefix follow-up
  -> SWA default versus memory-qualified --swa-full
  -> only change serialization/cache policy if trace evidence shows avoidable misses

Optional build profiles
  -> CPU BLAS PP benchmark
  -> CUDA additional-quant build only if required by winning KV tests
```

At every checkpoint record the exact model file/revision/quant, runtime commit/hash, backend and build flags, CPU/GPU/driver/RAM/VRAM, context, placement, batch/ubatch, K/V types, FA, threads, parallelism, load/TTFT/PP/TG/total latency, peak RAM/VRAM, cache-hit inference, and speculation acceptance. Keep global-free and process-budget VRAM as separate fields. Use at least one simple chat trace and one repeated-prefix multi-step tool/agent trace. Treat 1-3% as noise unless repeated trials show a stable effect.

## Final assessment

XE's inference path is already substantially mature. The most valuable remaining work is to keep **independent host-pressure and runtime-process facts explicit**, make **binary capabilities explicit**, and make **measured outcomes feed policy**. After that, native MTP, KV/FA selection, hybrid threads, and agent-prefix behavior are credible optimization candidates—but only as profiled, reversible choices. Static flag accumulation and datacenter-oriented architecture changes would add more complexity than consumer benefit.

At publication, no recommendation in this report had been implemented. The user subsequently approved the Tier 1 tranche; its implementation status follows.

## Post-approval implementation status

### Implemented

- **Binary capability manifest and launch gate** (`add76130`). Each resolved llama-server binary is probed through a bounded command runner, and the cached manifest is keyed by requested/runtime version plus path, length, modification time, and verified SHA-256. Mandatory option spellings and option-scoped values fail closed. Optional optimizations are omitted only on ordinary serving candidates; exact profiling/replay vectors are rejected rather than silently changed. KV/FA incompatibility selects the existing explicit safe candidate, preserving the paid-for one-shot readiness fallback.
- **Report-only load and benchmark correlation** (`9454b57f`). The supervisor records monotonic spawn-through-readiness duration, outcome, measured CPU/full/partial/unknown placement, explicit primary/safe-retry candidate kind, runtime identity, and bounded speculation class. Application metrics exclude model/path/argv/hash labels. Encrypted benchmark diagnostics correlate runtime identity and a semantic launch-vector hash with terminal request gauges, context/busy-slot observations, and speculative draft/acceptance counters. Global-free and process-budget VRAM remain separate fields and no second memory ledger or admission authority was introduced.

### Intentionally not enabled

- The llama.cpp pin was not advanced merely because b10375 was newer. A pin change still requires complete official asset digests plus platform/runtime, GPU, tool-grammar, and benchmark checkpoints.
- Tier 2 and Tier 3 candidates remain benchmark-gated: cross-vendor global-pressure sources, bounded KV/FA/thread/batch calibration, MTP, SWA/cache experiments, synthetic post-ready warming, residency changes, and optional build profiles.
- No static tuning values, extra parallel slots, second inference engine, or experimental upstream patch were added.

### Validation result

- Release restore and full solution build succeeded with zero warnings and errors.
- The guarded Release solution run passed 5,995 tests; 13 platform/live-integration tests were skipped, and the assembly guard found no contamination.
- On fresh worktree-local state, `aspire wait app` reached healthy; backend and React resources were healthy, the ready endpoint succeeded, and the application had no Error-level structured logs.
- The live GPU smoke correctly refused to pass: installed b10201 Vulkan enumerated no usable device under this WSL2 host (`backend=cpu`, `cpuFallback=true`), and the fresh node database had no registered chat model. No CUDA build, model registration, or large download was performed to manufacture a benchmark result.
