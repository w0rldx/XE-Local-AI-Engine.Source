# AI/inference stack performance and efficiency audit

- **Date:** 2026-08-07
- **Source revision:** `f0834625`
- **Scope:** local chat/agent inference, embedding, reranking, llama.cpp lifecycle, hardware admission, Microsoft Agent Framework (MAF), Microsoft.Extensions.AI (MEAI), streaming, scheduling, caching, and performance evidence
- **Target machines:** 16–64 GB system RAM, usually 16 or 32 GB, with 0–16 GB VRAM
- **Change decision:** assessment and experiment plan only; no production tuning is justified by this audit alone

## Executive assessment

This is not an unoptimized “launch llama-server with defaults” integration. The application already has unusually strong local-runtime controls: a pinned and provenance-checked llama.cpp runtime, hardware-device audit, fit-based GPU placement, RAM/VRAM reserves, context down-tiering, Q8 KV cache plus flash attention, pre-first-token retry, model leases, idle eviction, role-specific endpoints, a fail-closed benchmark harness, and a live GPU smoke that proves work occurred on the GPU rather than merely checking answer correctness.

The highest-value work is therefore **not** to turn on every new llama.cpp flag. It is to remove several application-level memory/stability risks, make queueing and residency evidence-driven rather than implicit, and close the evidence gap on representative consumer hardware and models.

### Highest-priority opportunities

1. **Bound and coalesce streaming fan-out.** Send, regenerate, and resume paths contain unbounded channels, while a disconnected subscriber does not immediately end a resumable server-side run. The existing whole-invocation watchdog bounds total lifetime, but a slow or absent browser can still retain events, invocation state, model leases, and tool-loop state without a subscriber-aware buffer ceiling.
2. **Make invocation queueing observable before redesigning concurrency.** A global application dispatcher currently serializes local and remote invocations before they reach llama.cpp; `--parallel 1` is a second gate, not today’s first bottleneck. Multiple llama slots require a multi-invocation application-state redesign before a server-flag experiment can be meaningful.
3. **Close the admission loop with post-load measurements.** Admission is strong before spawn but residency is governed mainly by process count and TTL. Feed actual post-load RAM/VRAM, effective context, offload, and KV allocation back into the scheduler before starting or retaining another model.
4. **Correct speculative-mode classification before benchmarking it.** External-draft modes need a second-model budget, native MTP uses heads in the main model, and n-gram modes are draftless. The current application incorrectly treats `draft-mtp` as requiring a draft GGUF.
5. **Reduce agent/prompt preparation overhead before inference.** Context trimming already occurs before agent/tool construction, but persistence selection, runtime-package/history materialization, tool-budget projection, and per-invocation agent/options construction can still create avoidable CPU and allocation cost. Instrument first, then cache only immutable pieces proven hot.
6. **Preserve the pooled-role micro-batch correctness contract.** Embedding/reranker inputs must fit in one physical micro-batch. Reduce transient memory only by changing admitted input/chunk limits, context, and `-b/-ub` atomically—not by independently shrinking `-ub`.
7. **Extend the existing evidence pipeline to representative hardware and workloads.** Current committed evidence is rigorous but primarily from WSL2 plus an RTX 5090 and small/synthetic corpora. It cannot authorize settings for 16 GB RAM, 8–16 GB VRAM, Windows WDDM contention, large agent prompts, or model switching.

### Expected payoff

| Work | Main benefit | Expected impact | Risk |
|---|---|---:|---:|
| Bounded stream channels and disconnected-run policy | Stability and memory ceiling | High | Medium |
| Post-load memory feedback and byte-aware residency | Fewer OOMs, less paging, safer switching | High | Medium |
| Invocation queue telemetry and multi-invocation design | Throughput and p95 queue latency | Potentially medium–high | High complexity |
| Prompt/tool/history materialization instrumentation | TTFT, prompt processing, allocations | Medium | Low–medium |
| Correct speculation modes, footprint, and acceptance gates | Prevent launch and memory regressions | High stability value | Low while default-off |
| Atomic pooled input/context/micro-batch policy | Peak memory and background responsiveness | Medium | Medium–high |
| Immutable agent/tool component reuse | CPU/GC reduction | Low–medium until profiled | Medium |

## Method and evidence boundaries

The assessment combines:

- a repository trace from SignalR chat entry through invocation/MAF/MEAI into the llama.cpp supervisor;
- separate specialist reviews of runtime/hardware policy, MAF/application architecture, and current external inference techniques;
- the repository’s prior performance artifacts and corrected no-change decisions under `docs/performance/`;
- an isolated Aspire run using the development administrator account and a locally staged 0.5B Q4 GGUF;
- current upstream llama.cpp, Microsoft, paper, Unsloth, and model-repository material listed in [External references](#external-references).

Evidence labels used below:

- **Observed:** source or runtime behavior directly inspected in this revision.
- **Measured:** retained in an existing committed benchmark artifact with the repository’s provenance contract.
- **Exploratory:** observed in the isolated live audit run without a committed comparable capture.
- **Upstream:** documented current capability; availability in the pinned build still requires a pin-specific probe.
- **Inference:** a likely consequence that needs a controlled experiment before a product change.

### Live-run limitation

The available host reported about 33.7 GB system memory and an RTX 5090-class CUDA device with about 34.2 GB visible VRAM, while the live model was only 0.37 GiB. This run validates wiring, launch arguments, observed prefix retention, device placement, and telemetry. It did not produce a committed capture with exact model revision/SHA, runtime dependency hashes, assembly identity, cache preparation, or raw counters, so its timings are non-reproducible exploratory observations under this repository’s evidence rules. They do not rank or authorize an optimization and are **not** transferable performance evidence for the target ceiling of 16 GB VRAM or for 9B–27B models.

## End-to-end architecture

### Chat and agent path

1. The React client opens a persistent SignalR connection at `/api/local/v1/chat/hub` and starts `SendMessage` streams (`XE-Local-AI-Engine.Client.React/src/features/chat/api/NodeChatConnection.ts`, `NodeChatConnectionManager`; `XE-Local-AI-Engine.Client.React/src/features/chat/api/NodeChatAdapter.ts`, `signalRStream`).
2. `LocalChatHub` delegates to `INodeChatStreamService` (`XE-Local-AI-Engine.Client/Hubs/LocalChatHub.cs`, `SendMessage`).
3. `NodeChatStreamService` persists the turn, constructs correlation state, emits runtime phases, runs invocation/event producers, and maps state into stream events (`XE-Local-AI-Engine.Client.Application/Services/Chat/Implementation/NodeChatStreamService.cs`, `SendMessageCoreAsync`).
4. Invocation construction resolves the selected agent, history, tools, sampling, and run options. `InvocationAgentFactory` builds a `ChatClientAgent` and seeds context for each invocation (`XE-Local-AI-Engine.AI.Agent/Invocation/Implementation/InvocationAgentFactory.cs`, `CreateAsync`).
5. The chat-client chain includes MAF approval wrappers, MEAI function invocation, tool observability, provider-call budgeting, OpenTelemetry, active-provider routing, and the deferred local client. The live failure stack confirmed this chain in that order.
6. `DeferredLlamaServerChatClient` obtains a model lease, lazily creates the OpenAI-compatible adapter, transforms per-request options/tools, and permits a retry only before the first streamed chunk (`XE-Local-AI-Engine.Providers.LlamaServer/Implementation/DeferredLlamaServerChatClient.cs`, `GetStreamingResponseAsync` and `EnsureInnerAsync`).
7. `LlamaServerProcessSupervisor` admits or evicts a process, computes launch policy, starts a per-model/role `llama-server`, probes readiness, records effective context/offload, and retries/down-tiers on eligible startup failures (`XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaServerProcessSupervisor.cs`, `SpawnCoreAsync`, `BuildLaunchPlanCandidatesAsync`, and `BuildLaunchSpec`).
8. llama.cpp exposes an OpenAI-compatible `/v1` endpoint plus metrics. Streaming updates return through MEAI/MAF, persistence pumps, channels, SignalR, and the React state reducer.

### Embedding and reranker path

- Knowledge ingestion is bounded at the worker level, but `KnowledgeChunkEmbedder` creates per-document inputs and retains a document’s vectors in `byte[][]` while issuing batches sequentially (`XE-Local-AI-Engine.Client.Application/Services/Knowledge/KnowledgeChunkEmbedder.cs`, `EmbedAsync`).
- Retrieval batches cache misses and the query through `EmbeddingPlaybookRetrievalRanker`; the query is re-embedded, candidate/result structures are allocated, and the embedding cache is count-bounded rather than byte-bounded (`XE-Local-AI-Engine.Client.Application/Services/Agents/Implementation/EmbeddingPlaybookRetrievalRanker.cs`, `RankByEmbeddingAsync` and `StoreInCache`).
- llama.cpp launches separate role processes for chat, embedding, and reranking, all governed by the same supervisor and role-specific arguments.

## What is already implemented well

These controls should be preserved while optimizing:

1. **Pinned runtime identity and managed acquisition.** The current pin is llama.cpp `b10201`, source commit `8f4646a…` (`XE-Local-AI-Engine.Providers.LlamaServer/LlamaCppReleasePins.cs`, `PinnedTag` and `PinnedSourceCommitSha`). Runtime APIs distinguish official source builds and expose update information.
2. **Outcome-based device verification.** Startup/device audit checks the effective backend and layer placement instead of trusting the configured variant. This prevents silent CPU fallback from being called a GPU pass.
3. **Conservative fit and fallback.** The launch policy reserves both RAM and VRAM, uses context ladders, attempts GPU-friendly Q8 KV plus flash attention, and falls back safely (`XE-Local-AI-Engine.Providers.LlamaServer/Options/LlamaServerLaunchPolicyOptions.cs`, `LlamaServerLaunchPolicyOptions`; `XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaServerProcessSupervisor.cs`, `AppendContextPlacementAndThreadArgs`).
4. **Protected model lifecycle.** Leases prevent eviction while a request is active; the supervisor applies model caps, TTL, readiness checks, restart handling, and containment (`XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaServerProcessSupervisor.cs`, `TryAcquireInferenceLease`, `DecideEnsureAsync`, and `ReapIdleOnceAsync`).
5. **Prefix retention is active.** Chat launches include `--cache-reuse 256`, and the exploratory warm turn processed only the changed suffix. A controlled flag-off run is still required to attribute the benefit specifically to `--cache-reuse` rather than ordinary slot prefix retention.
6. **Role-aware, fail-closed benchmarking exists.** `InferenceBenchmarkHarness` measures chat TTFT/throughput/cache/tool loop and embedding/reranker correctness, latency, RAM, and VRAM evidence (`XE-Local-AI-Engine.Client.Application/Services/Inference/InferenceBenchmarkHarness.cs`, `RunAsync`, `RunChatAsync`, and `RunEmbeddingAsync`; `XE-Local-AI-Engine.Client.Application/Services/Inference/IInferenceBenchmarkHarness.cs`, `InferenceBenchmarkSpec` and `InferenceBenchmarkMetrics`).
7. **The project already rejected unsafe “faster” settings.** The corrected 24-cell embedding/reranker scheduling grid found semantic divergence in every candidate and significant RSS regressions in larger physical batches, so no production change shipped (`docs/performance/2026-07-26-lane4-no-change.md`, “Bounded grid and hard gates” and “Decision”).
8. **Evidence provenance is unusually rigorous.** Captures bind source, packages, assemblies, model/corpus hashes, runtime binaries/dependencies, device identity, arguments, and gaps; comparisons fail closed (`docs/performance/inference-capture-workflow.md`, “Fixed capture contract”).
9. **Live smoke tests distinguish correctness from acceleration.** The GPU smoke requires measured utilization/VRAM change and verifies eject, rather than accepting a correct CPU-fallback answer.

## Exploratory live Aspire observations

### Effective launch

The live chat process used:

```text
llama-server
  -m Qwen2.5-0.5B-Instruct-Q4_K_M.gguf
  --host 127.0.0.1 --port 18100
  --parallel 1 --no-warmup --fit on --metrics
  -c 32512 -fa on -ctk q8_0 -ctv q8_0
  --jinja --cache-reuse 256 -lv 4
```

Runtime logs/properties additionally reported:

- CUDA execution and 25/25 layers offloaded;
- one sequence slot (`n_seq_max=1`, `/props total_slots=1`);
- effective context 32,512 tokens;
- logical batch 2,048 and physical batch 512;
- flash attention enabled;
- Q8 key/value KV cache, about 202.4 MiB for this tiny model/context;
- model ready inside llama.cpp in about 1.01 s and ready through the application in about 1.42 s;
- prompt cache/checkpoint reuse enabled;
- no speculative decoder.

### Cold and warm application turns

| Measurement | Cold first turn | Warm follow-up |
|---|---:|---:|
| Input tokens reported by application | 406 | 439 |
| Output tokens | 5 | 10 |
| Loading-model phase observed | 158 ms | 84 ms |
| Generating phase observed | 1,574 ms | 85 ms |
| Time to first token | 1,723 ms | 141 ms |
| Total stream time | 1,750 ms | 228 ms |

After the cold turn, llama.cpp exposed 406 prompt tokens processed. After the warm turn, the cumulative counter was 435, so only 29 additional prompt tokens were evaluated even though the application reported a 439-token input. This observes prefix retention in the current stack, but without an otherwise-identical `--cache-reuse 0` control it does not isolate the value of that flag. The exact ratio should not be generalized from one two-turn conversation.

The small 0.5B model did not reliably follow exact-output instructions. These measurements are transport/runtime evidence, not a quality benchmark.

### Live observations that affect priorities

- A trivial one-sentence request became a 406-token first input before generation. That is reasonable for a framework/system prompt, but it establishes that prompt/tool/history byte and token accounting belongs in routine telemetry.
- The server allocated almost the model’s entire trained context because abundant VRAM made it safe on this host. This does not prove that maximum context is the right responsiveness/memory trade-off for larger models on 8–16 GB GPUs.
- The local model list returned a usable llama.cpp item while also reporting `error: "Local model provider is unavailable."` from the degraded Ollama lane. That mixed availability contract can mislead diagnostics and should identify provider-specific status.
- An intentionally incorrect filename-style model key failed quickly with “requested model is not installed,” while the canonical repository/quant key succeeded. Internal call sites should continue to carry stable model IDs rather than reconstructing them from filenames.

## Prioritized findings and recommendations

### P0 — bound streaming and detached-invocation memory

**Evidence.** `NodeChatStreamService` creates one unbounded `Channel<InvocationState>` and one unbounded `Channel<ChatStreamEvent>` with multiple producers (`XE-Local-AI-Engine.Client.Application/Services/Chat/Implementation/NodeChatStreamService.cs`, `SendMessageCoreAsync`). `NodeChatRegenerationService` duplicates the same two-channel and disconnect-independent lifecycle (`XE-Local-AI-Engine.Client.Application/Services/Chat/Implementation/NodeChatRegenerationService.cs`, `RegenerateCoreAsync`). `InvocationResumeRegistry` creates unbounded per-subscriber channels and replays/clones state for late subscribers (`XE-Local-AI-Engine.Client.Application/Services/Chat/Implementation/InvocationResumeRegistry.cs`, `LiveInvocation.Subscribe`). A transport disconnect does not necessarily cancel the server run because persistence and resume are supported features.

**Risk.** Slow browsers, repeated reconnects, large reasoning/tool streams, or abandoned invocations can grow buffered objects and retain the invocation, transcript, tool results, and llama-server lease until the existing watchdog expires. `InvocationRunner.RegisterActiveInvocation` applies the package `InvocationTimeout` with `CancelAfter`, and bounded approval/question waits deliberately re-arm that deadline (`XE-Local-AI-Engine.Client.Application/Services/Invocation/Implementation/InvocationRunner.cs`, `RegisterActiveInvocation` and the approval/question wait paths; `XE-Local-AI-Engine.Client.Application/Models/TimeoutSettings.cs`, `InvocationTimeoutSeconds`). The default whole-invocation ceiling is therefore finite—300 seconds in this revision—but it is not a stream-buffer budget or a subscriber-aware disconnect policy. Under consumer RAM pressure this remains a stability concern before it is a throughput concern.

**Recommendation.** Define an explicit stream budget:

- bounded channels by event count and estimated bytes;
- coalesce adjacent text/reasoning deltas when the consumer lags;
- never drop terminal, approval, question, tool-lifecycle, or phase events;
- cap replay snapshot bytes and subscriber count per invocation;
- detach a subscriber without immediately cancelling a resumable run, but integrate a shorter subscriber-aware post-disconnect grace deadline with the existing whole-invocation watchdog;
- preserve the existing bounded approval/question wait semantics when the watchdog is re-armed;
- release the model lease and terminalize persistence once the applicable grace or whole-invocation deadline expires;
- publish counters for queued events/bytes, coalesced deltas, detached invocations, oldest detached age, and leases held without subscribers.

**Acceptance experiment.** Exercise both send and regenerate with a long response, throttle and disconnect the SignalR consumer, reconnect twice, and assert: bounded managed-memory growth, terminal replay correctness, no duplicate tool events, and lease release after the configured grace period.

### P1 — expose the global invocation queue before considering multiple model slots

**Evidence.** `WorkerEventDispatcher.ReportInvocationAssignedAsync` waits on one `_remoteInvocationQueue` and holds that lease until the local run terminates; local and remote invocations are intentionally mutually exclusive (`XE-Local-AI-Engine.Client.Application/Services/Events/Implementation/WorkerEventDispatcher.cs`, `ReportInvocationAssignedAsync`). `IWorkerEventDispatcher` exposes only one `CurrentInvocation` (`XE-Local-AI-Engine.Client.Application/Services/Events/IWorkerEventDispatcher.cs`, `CurrentInvocation`). llama.cpp is also launched with `--parallel 1` (`XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaServerProcessSupervisor.cs`, `BuildLaunchSpec`), but that is the second serialization gate, not the first. The exploratory `/props` confirmed one server slot.

**Impact.** A second llama.cpp slot would currently remain unused because another invocation cannot pass the dispatcher. Queue time can dominate p95 responsiveness, but the product has a single-invocation state model that also owns approvals, questions, cancellation, persistence, and event routing.

**Recommendation.** Keep one invocation and one llama slot as the safe contract. First add queue depth, wait duration, cancellation-while-queued, foreground/background source, and GPU-idle-during-queue metrics. Only if representative traces show material user-visible queueing should the application be redesigned around multiple invocation states keyed by invocation ID, including:

- approval/question/result routing;
- cancellation, resume, collision, and persistence ownership;
- fair foreground/background admission and bounded queues;
- per-invocation event state rather than one `CurrentInvocation`;
- measured post-load headroom for each slot’s context/KV/scratch allocation.

After that redesign passes its application correctness tests, a one-versus-two-slot llama.cpp experiment becomes meaningful. Prefer admission control and a small bounded queue over multiple resident copies of the same model. Do not change `--parallel` independently.

**Acceptance experiment.** Phase A measures the current application queue under single, foreground-plus-background, and cancelled queued workloads. Phase B, only after a multi-invocation redesign, compares one versus two slots using two short requests, one long plus one short request, and two agent/tool loops. Gate on approval/question isolation, resume/cancellation correctness, p50/p95 queue and TTFT, makespan, per-request decode rate, RAM/VRAM peak, effective per-slot context, and output/tool equivalence.

### P1 — feed actual loaded footprint back into admission and residency

**Evidence.** Pre-spawn fit/reserve logic is sophisticated, but resident capacity is primarily limited by a fixed maximum of three processes and a 15-minute TTL (`XE-Local-AI-Engine.Providers.LlamaServer/Options/LlamaServerSupervisorOptions.cs`, `MaxLoadedProcesses` and `IdleTimeToLive`). Device choice is based on the largest free device rather than the sum of a fully validated multi-GPU placement. The live host had enough memory that a tiny model received a 32.5K context.

**Risk.** File size and pre-spawn free memory do not fully predict weights after backend placement, KV buffers, graph/scratch allocation, draft models, driver overhead, or external workloads appearing after load. Count-based residency can keep three “small by count, large by bytes” processes or evict a valuable warm model too late.

**Recommendation.** Maintain a measured residency ledger per process:

- model weights/file identity and role;
- effective context, parallel slots, batch/ubatch, KV types;
- effective device and layers offloaded;
- post-ready process RSS and GPU memory delta where observable;
- prompt-cache resident bytes where supported;
- active/queued lease counts, last-used time, load cost, and recent TTFT benefit.

Before every spawn or promotion to a larger context, recalculate the total resident budget. Evict by a byte-aware score such as `reclaimable bytes / expected reload penalty`, while protecting active leases. If post-load use exceeds the estimate, correct the ledger and reject/evict before the next request rather than waiting for OOM.

### P1 — correct speculative-mode contracts before measuring them

**Evidence.** Speculation is off by default and the application exposes simple draft, EAGLE3, MTP, and n-gram modes (`XE-Local-AI-Engine.Providers.LlamaServer/Options/SpeculativeDecodingSettings.cs`, `AllowedModes`). Its `IsDraftMode` currently classifies every `draft-*` mode as requiring `DraftModelPath`, and `AppendSpeculativeArgs` emits `--spec-draft-model` for all of them. In the pinned/current llama.cpp contract, `draft-mtp` uses MTP heads in the main model and does not require a second draft GGUF. External-draft modes and draftless n-gram modes have different lifecycle and memory requirements. Upstream also documents newer DFlash/DSpark modes that are not in the compiled allowlist.

**Risk.** The current MTP mode cannot form the correct launch without a bogus draft path. Conversely, real external-draft modes consume second-model weights, KV/scratch, and bandwidth that may force the target to CPU, reduce context, or cause paging. N-gram and MTP modes should not be charged for a nonexistent second model.

**Recommendation.** Split speculation into three explicit capability classes and test the pinned runtime contract:

- **external draft** (`draft-simple`, EAGLE3, and future DFlash/DSpark where supported): require a draft path and admit target plus draft atomically;
- **main-model heads** (`draft-mtp`): emit no external draft-model flag and validate that the GGUF contains compatible MTP tensors;
- **draftless** n-gram modes: no draft weights, separate cache/state accounting.

Only after fixing and compatibility-testing that launch contract should Qwen3.6-27B-MTP be compared on/off. For all classes record proposed/accepted tokens, acceptance rate, target verification cost, TTFT, steady-state tokens/s, power, and actual memory. Disable automatically when acceptance or net latency falls below a per-model threshold.

### P1 — reduce pooled-role memory only with an atomic input/context/micro-batch contract

**Evidence.** `AppendPooledForwardPassBatchArgs` deliberately sets both `-b` and `-ub` to the effective context because a non-causal embedding/rerank input must fit inside one physical micro-batch; llama-server rejects rather than splits a longer input (`XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaServerProcessSupervisor.cs`, `AppendPooledForwardPassBatchArgs`; `docs/agent-knowledge.md`, “A pooled (embedding/rerank) forward pass must fit in ONE physical micro-batch”). `KnowledgeChunkEmbedder` also retains all vectors for a document before returning. The prior corrected scheduling grid found semantic changes across all parallel/batch candidates and RSS regressions of 12.4–38.1% in 1024-physical-batch variants.

**Recommendation.** Preserve `n_ubatch >= maximum admitted input tokens`; never independently lower the physical batch. To reduce transients, change these as one versioned contract:

- tokenizer-aware maximum input or an estimator with a proven safety margin;
- chunking/rechunking and explicit over-limit rejection behavior;
- advertised/effective pooled context;
- logical and physical batch at least as large as that maximum input;
- backend/device scratch budget and foreground-chat priority.

Start from the current known-correct vector. Any smaller `-ub` experiment must first lower/rechunk the admitted input ceiling and prove the maximum-length preflight. Larger values still need an explicit throughput win and exact semantic-equivalence gate. Stream/persist embedding vectors by bounded chunk instead of retaining an unbounded document aggregate. Give foreground chat priority over background indexing and expose pause/backpressure when system memory is tight.

### P1 — measure and reduce prompt/tool/history preparation cost

**Evidence.** `InvocationRunner.RunSingleAgentAsync` already applies `ApplyContextBudgetAsync` before it builds the invocation definition and calls `InvocationAgentFactory.CreateAsync` (`XE-Local-AI-Engine.Client.Application/Services/Invocation/Implementation/InvocationRunner.cs`, `RunSingleAgentAsync`). However, the runtime package/conversation has already been selected and materialized by that point; budget calculation projects tool definitions, and the factory subsequently builds the real tools, seed messages, options, and a new agent (`XE-Local-AI-Engine.Client.Application/Services/Invocation/Implementation/InvocationRunner.AgentBuilders.cs`, `ApplyContextBudgetAsync` and `BuildInvocationDefinition`; `XE-Local-AI-Engine.AI.Agent/Invocation/Implementation/InvocationAgentFactory.cs`, `CreateAsync`). The deferred client clones/transforms request options/tools (`XE-Local-AI-Engine.Providers.LlamaServer/Implementation/DeferredLlamaServerChatClient.cs`, `ApplyThinkingSwitch` and `ApplyToolSchemaCompatibility`). MAF/MEAI tool invocation sends tools and accumulated model/tool messages across repeated provider calls. The exploratory one-sentence first request reached 406 input tokens.

**Recommendation order:**

1. Add spans/metrics for history load, selected-path assembly, attachment/knowledge composition, tool discovery, JSON-schema sanitation, agent construction, request serialization, queue delay, runtime load, prompt evaluation, first token, tool execution, and provider rounds.
2. Record counts and bytes: messages, prompt tokens, tool count, raw/sanitized schema bytes, attachments, knowledge context, serialized request bytes, clones, and provider calls.
3. Profile the earlier persistence selection, runtime-package assembly, attachment/knowledge composition, and `BuildChatMessages` materialization boundary. Do not add another trimming pass; the existing context budget is already correctly placed before agent construction.
4. Reuse validated tool-budget metadata and cache immutable agent-definition/sanitized-tool descriptors keyed by definition/tool version only if profiles show repeated construction is material. Keep per-run session, approvals, cancellation, and mutable options isolated.
5. Avoid cloning `ChatOptions`/tool arrays unless a downstream layer mutates them; verify ownership contracts before changing this.

Do not bypass MAF approval or tool-loop middleware for speed. Microsoft’s agent pipeline/middleware abstractions are the correct observability seam; optimize construction and payloads around them.

### P2 — byte-bound and de-duplicate embedding caches

**Evidence.** Retrieval uses a 512-entry embedding cache rather than a byte budget, re-embeds queries, and does not coalesce concurrent misses (`XE-Local-AI-Engine.Client.Application/Services/Agents/Implementation/EmbeddingPlaybookRetrievalRanker.cs`, `RankByEmbeddingAsync` and `StoreInCache`). Vector size varies with embedding dimension, so an entry count is not a memory ceiling.

**Recommendation.** Use a byte-accounted LRU keyed by model/revision, normalized input, prefix/task type, and relevant encoding version. Coalesce identical in-flight requests. Cache repeated query embeddings only with a short TTL and privacy-compatible scope. Emit hit/miss/eviction bytes and inflight-dedup counters.

### P2 — make prompt-cache RAM explicit on consumer machines

**Evidence.** The application enables prefix reuse but does not pass an explicit `--cache-ram` in the observed chat launch. Current upstream llama-server documents an in-memory prompt-cache budget whose default can be material on a 16 GB machine. Pin-specific behavior must be probed because the application is on b10201, not current master.

**Recommendation.** Probe `/props`/help for the pinned build and explicitly cap server-side cache memory in the **host-RAM** residency ledger. Keep the current `--cache-reuse 256` pending a controlled on/off comparison covering exact-prefix follow-up, changed prefixes, and conversation switching. Never spend several GiB of system RAM on prompt cache if it induces swap or crowds out model/KV host allocations.

### P2 — profile the HTTP/JSON boundary; do not replace it with P/Invoke speculatively

**Evidence.** Local inference crosses a loopback OpenAI-compatible HTTP boundary and maps JSON streaming updates through multiple MEAI/MAF decorators. This adds serialization, strings, option/tool projection, and event-mapping allocations. It also provides native-crash containment, independent model-process lifecycle, runtime replacement, health probing, and reliable VRAM release.

**Recommendation.** Measure request bytes, serialization/deserialization allocation, chunk count/size, socket reuse, and CPU time separately from prompt evaluation. Reuse transports and coalesce application-facing deltas when it reduces overhead without delaying first-token delivery. Do not move llama.cpp in-process through P/Invoke unless profiles show loopback/JSON is a material fraction of TTFT or CPU at target model speeds: an in-process binding would add native lifetime and memory-safety risk while weakening the supervisor’s strongest stability properties.

### P2 — close lifecycle races and configuration drift

1. **Port allocation:** the supervisor reserves a number and separately probes whether it is free before llama-server binds it (`XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaServerProcessSupervisor.cs`, `AdmitAndAllocatePortAsync`, `AllocatePort`, and `IsPortFree`), leaving a time-of-check/time-of-use race. Prefer an atomic listener reservation/hand-off if llama-server permits it, or retry bind collisions with a newly reserved port.
2. **Pin drift:** code pins b10201, while multiple comments/assumptions still mention b9692. The live updater reported b10308 upstream. Keep deliberate pinning, but add a compatibility matrix/test that probes supported flags, modes, `/props`, metrics, grammar behavior, and helper availability before a pin bump.
3. **Speculation enum drift:** upstream mode names are moving. Reject unknown modes safely, but derive UI/help capability from the installed runtime probe rather than only a compiled allowlist.
4. **Mixed provider status:** return provider-scoped availability/errors so an unavailable Ollama endpoint does not make a healthy llama.cpp catalog response look contradictory.
5. **Device selection:** largest-free-device policy is conservative and suitable for most single-GPU consumers. Treat row/tensor split and multi-GPU aggregation as an expert experiment, not a default; measure cross-device bandwidth and stability.

## Current optimization techniques: applicability to this application

| Technique | Current state | Recommendation |
|---|---|---|
| Maximum safe GPU offload | Implemented through fit and verified layer audit | Preserve; add post-load correction and target-hardware captures |
| Flash attention | Enabled on successful GPU launch | Preserve with compatibility fallback; validate every pin/model family |
| Q8 KV cache | Enabled on observed GPU launch | Good default candidate; compare Q8/Q4/F16 by quality, context, and memory per model/backend |
| Prompt prefix retention | Observed; `--cache-reuse 256` attribution not isolated | Add explicit host-cache budget/telemetry and run a flag-off control |
| Continuous batching / multiple slots | Blocked first by the single-invocation dispatcher, then by `--parallel 1` | Instrument the queue; redesign multi-invocation state before a slot experiment |
| Model mmap/load modes | Upstream default mmap is appropriate | Keep mmap; use mlock only when RAM headroom proves no paging risk |
| External-draft speculation | Supported experimentally, off by default | Keep default-off; atomic target-plus-draft budget and acceptance-rate gate |
| MTP | Exposed but currently misclassified as needing a draft GGUF | Fix the main-model-head launch contract before evaluating Qwen3.6 |
| N-gram speculation | Application exposes modes | Test for repetitive code/edit contexts; lower memory than draft models |
| Quantization | GGUF quant selection supported | Prefer tested Q4/Q5/IQ trade-offs; record quantizer/runtime/model provenance |
| CPU thread tuning | Configured by policy | Benchmark physical-core-aware counts; avoid prior 2× oversubscription mistake |
| Pooled batch/ubatch | Full-input physical micro-batch is a correctness invariant | Change admitted input/context/batch atomically; prior variants failed gates |
| Multi-GPU row/tensor split | Upstream/experimental surface | Low priority for consumer target; expert opt-in only |
| Paged KV / vLLM-style paging | Research/prototype in llama.cpp discussions | Do not architect around it until upstream stabilizes and pin supports it |
| Unsloth kernels/training optimizations | Not directly portable to C#/llama.cpp serving | Use its quantized model artifacts and measurement ideas, not Python/CUDA assumptions |

## Lessons from other current inference engines

The comparison supports retaining llama.cpp. Other engines provide useful scheduling ideas, but replacing the backend would add model formats, Python/Torch/CUDA dependencies, or data-center-oriented complexity without first proving that llama.cpp is the limiting layer.

| Project | Transferable lesson | Assessment for this application |
|---|---|---|
| vLLM / PagedAttention | Treat KV as block-managed capacity; admit by available KV, expose prefix-hit tokens, and evict by actual blocks/bytes | Valuable design reference. Replacement is not justified for a primarily single-user consumer application unless sustained high-concurrency KV fragmentation is measured |
| SGLang / RadixAttention | Normalize common system/tool prefixes so reuse is token-identical; consider prefix-aware scheduling only when reuse outweighs extra queueing | Useful for repeated agent/tool prefixes. Its Python/CUDA multi-tenant serving surface is not a good default dependency here |
| ExLlamaV2 / TabbyAPI | GPU-specialized dynamic batching, paged caches, cache quantization, and speculation show the upper bound for NVIDIA-only fully resident models | A reference benchmark lane only. EXL formats and Python/Torch/CUDA would weaken the current GGUF and cross-backend product contract |
| KTransformers | Heterogeneous CPU/GPU placement can make oversized or MoE models usable, but transfers and synchronization become dominant risks | Relevant only to an explicitly slower large/MoE quality profile; not the 16/32 GB responsive default |
| Ollama scheduler | Bound the queue, multiply context/KV by parallel requests, and make loaded-model/keep-alive policy explicit | Directly transferable above llama.cpp; no reason to add another model manager to obtain these policies |

Unsloth Studio is also not a separate inference-kernel breakthrough to transplant. Its current Beta backend selects and orchestrates `llama-server` for GGUF inference while adding product/UI/API/configuration behavior. It is a useful compatibility, preset, GPU-detection, template, and telemetry reference, but any speed claim must be attributed to the exact bundled llama.cpp build and launch vector. Studio’s release history also shows proxy/tool-call and backend-update churn, reinforcing the need for this project’s existing pin and API/tool compatibility tests.

## Consumer-hardware policy guidance

Use **budgets and fit results**, not model-parameter labels alone. GGUF file size does not include KV, scratch, prompt cache, driver allocations, draft models, or application RAM.

### Approximately 16 GB RAM

- Default to one resident generation model and one active slot.
- Avoid `mlock`; preserve OS/app headroom and fail before swap thrash.
- Prefer 7B–9B-class Q4 variants with 4K–8K starting context; increase context only after measured fit.
- Pause or heavily throttle background embeddings while generating.
- Keep host-RAM prompt cache explicitly small and evict secondary-role servers aggressively.
- Speculative draft models are generally unattractive unless very small and conclusively beneficial.

### Approximately 32 GB RAM

- 9B–14B Q4/Q5 is the practical high-responsiveness tier; larger models need quality/latency trade-off acceptance.
- A 27B low-bit quant may fit in system RAM, but partial CPU/GPU offload can have poor TTFT/decode responsiveness. Treat it as an optional quality profile, not the default agent profile.
- After a multi-invocation application redesign, two chat slots may be viable for smaller fully offloaded models on 12–16 GB VRAM, provided per-slot context and KV budgets pass.
- Keep model switching and embedding concurrency bounded; two or three server processes by count may still be too many by bytes.

### Approximately 64 GB RAM

- Larger 27B-class Q4 models become realistic in RAM, but up to 16 GB VRAM still limits full offload.
- Prefer explicit “responsive” and “quality” profiles rather than silently accepting CPU-heavy offload.
- A draft model or warm secondary role can be resident only when the measured ledger preserves application/OS headroom.

### VRAM-specific rule

Maintain separate host-RAM and device-VRAM ledgers. For 8–16 GB VRAM, allocate device memory in this order: target weights/offload, required KV for minimum context/slots, compute scratch, driver/desktop reserve, then an optional external draft model or second slot. Charge saved prompt-cache state to host RAM unless a pin-specific measurement proves another placement. Reduce context or parallelism before allowing system-wide paging. A correct answer produced by CPU fallback is not a successful GPU profile.

## Model-focused experiments

### `unsloth/Ornith-1.0-9B-GGUF`

Use as the primary coding/agent model for 8–16 GB VRAM. Its current model card identifies it as a reasoning model and calls for recent runtime/template support, so reasoning separation and tool-call parsing are preflight gates. Compare available approximately 4-bit, 5-bit, and suitable IQ variants across 4K/8K/16K contexts. Measure tool-call/structured-output correctness in addition to code quality. After any multi-invocation application redesign, this is the best first target for a one-versus-two-slot experiment.

### `unsloth/Qwen3.6-27B-MTP-GGUF`

Use for the quality/large-model lane on 32–64 GB RAM. Compare CPU-heavy partial offload against a smaller fully offloaded model using wall-clock task completion, not only tokens/s. After fixing the MTP launch contract, run native MTP on/off with acceptance and main-model memory telemetry; no external draft GGUF should be required. Expect this to be unsuitable as the default on many 16/32 GB machines.

### `unsloth/gemma-4-12b-it-GGUF`

Use as the general-purpose middle tier. Compare standard GGUF quants with Google’s QAT Q4 artifact when tokenizer/template compatibility is proven. Evaluate long-context quality, tool compatibility, and Q8 versus lower-bit KV cache.

For all three, pin repository revision, exact GGUF SHA-256, tokenizer/chat template, quantizer provenance, llama.cpp binary/dependencies, and prompt corpus. Split files must be treated as one immutable model identity.

## Benchmark and experiment plan

### Phase 0 — extend observability before tuning

Add these per-turn fields/spans to the existing OpenTelemetry path and sanitized benchmark output:

- request queue time, agent/history/tool preparation time, JSON serialization time;
- runtime admission, model load/readiness, prompt evaluation, first token, decode, and total time;
- messages/tool schemas/attachments/knowledge bytes and input/output tokens;
- provider rounds, tool-loop count/time, cache-reused prompt tokens, context truncation;
- effective launch vector, context, slots, batch/ubatch, KV types, offloaded layers, backend;
- process RSS, observable GPU memory, host prompt-cache bytes, external-draft bytes where applicable, and separate host/device residency ledgers;
- stream queue events/bytes, detached-subscriber age, and active leases.

Do not emit prompt contents, credentials, absolute paths, or raw tool results.

### Phase 1 — representative baseline matrix

Capture at minimum:

| Dimension | Required values |
|---|---|
| RAM | 16 GB, 32 GB, 64 GB or trustworthy constrained equivalents |
| VRAM | CPU-only, 8 GB, 12 GB, 16 GB; CUDA plus one supported non-CUDA backend |
| OS | Native Windows WDDM and native Linux; keep WSL evidence separate |
| Models | Ornith 9B, Gemma 12B, Qwen3.6 27B MTP; exact quants/revisions pinned |
| Cache | cold OS/model, warm model/cold prompt, warm prefix, conversation switch |
| Load | one short request, one long request, two concurrent, chat plus indexing |
| Agent | plain chat, full tool offer, real tool loop, multi-round tool loop, cancellation |
| Lifecycle | first load, eject, reload, switch A→B→A, idle TTL, external VRAM pressure |

### Phase 2 — controlled experiments

Run one variable at a time:

1. current global invocation-queue characterization, followed by one versus two chat slots only after a multi-invocation redesign;
2. context ladder per hardware/model;
3. Q8/Q4/F16 KV where supported;
4. explicit prompt-cache RAM budgets;
5. maximum admitted pooled input/context with matching batch/ubatch for embedding and reranking;
6. model-residency score/TTL;
7. speculation off versus corrected MTP, external-draft, and n-gram modes;
8. physical-core-aware CPU threads and affinity;
9. mmap default versus conditional mlock on high-headroom native Linux only.

### Required metrics

- cold and warm model startup, model-switch cost, and eject time;
- TTFT, prompt tokens/s, inter-token latency/TPOT, decode tokens/s, total task time;
- p50/p95/p99 queue and request latency, throughput, cancellation response;
- process RSS/peak, system commit, swap/page faults, GPU memory/utilization, power/thermal throttling;
- cache reuse, prompt-cache bytes, KV bytes, slots busy/deferred;
- tool/structured-output validity, exact semantic equivalence where required, task-quality score;
- crashes, OOM recovery, retries, down-tiering, orphaned processes, and lease release.

### Suggested gates

- No correctness, tool-schema, approval, resume, cancellation, or deterministic-role regression.
- No OS swap/page-thrash during the steady workload.
- No more than 5% peak RAM/VRAM regression unless the experiment explicitly trades memory for a larger, independently approved user benefit.
- At least 15–20% improvement in the target user metric or a material p95/availability improvement. Small synthetic tok/s gains do not justify complexity.
- Every result must retain the existing identity/provenance and explicit-gap contract.

The current `capture_inference_evidence.py`, scheduling grid, `InferenceBenchmarkHarness`, GPU smoke, and tool-grammar smoke are the correct foundation. Extend their workloads and hardware coverage rather than introducing a second, weaker benchmark system.

## Phased implementation roadmap

### Near term: stability and measurement

1. Bound/coalesce send, regenerate, and resume channels; add a subscriber-aware post-disconnect grace deadline without replacing the existing whole-invocation watchdog or its human-wait semantics.
2. Add prompt/tool/history preparation and stream-buffer telemetry.
3. Make provider availability provider-scoped.
4. Add post-load resource snapshots and a resident-byte ledger in report-only mode.
5. Make prompt-cache memory explicit after probing b10201 support/defaults.

### Next: safe efficiency experiments

1. Atomic pooled input/chunk/context/micro-batch profiles with maximum-length preflight and the existing semantic gate.
2. Byte-aware embedding cache plus in-flight miss coalescing.
3. Cache immutable agent/tool descriptors after allocation profiles prove value.
4. Fix port reservation/retry and add runtime capability probes for pin drift.

### Later: throughput features

1. Multi-invocation dispatcher/state design, then an experimental adaptive two-slot chat profile if queue evidence justifies it.
2. Byte-aware model residency and switching policy.
3. Corrected MTP, n-gram, and external-draft speculation per model/hardware profile.
4. Expert multi-GPU placement only if a real target cohort justifies it.

## External references

Primary or upstream sources consulted as of 2026-08-07:

- llama.cpp server arguments, caching, slots, batching, KV, GPU placement, and metrics: [llama-server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- llama.cpp server scheduling/slot architecture: [server development README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README-dev.md)
- llama.cpp speculative decoding modes and constraints: [speculative decoding documentation](https://github.com/ggml-org/llama.cpp/blob/master/docs/speculative.md)
- pinned llama.cpp speculative contract used for implementation decisions: [b10201 speculative decoding documentation](https://github.com/ggml-org/llama.cpp/blob/b10201/docs/speculative.md)
- llama.cpp quantization tooling: [quantize README](https://github.com/ggml-org/llama.cpp/blob/master/tools/quantize/README.md)
- llama.cpp CUDA build/backend controls: [CUDA build documentation](https://github.com/ggml-org/llama.cpp/blob/master/docs/build.md#cuda)
- llama.cpp backend/model feature coverage: [feature matrix](https://github.com/ggml-org/llama.cpp/wiki/Feature-matrix)
- prompt-cache implementation trade-offs: [llama.cpp issue 22942](https://github.com/ggml-org/llama.cpp/issues/22942)
- paged KV prototype status: [llama.cpp discussion 21961](https://github.com/ggml-org/llama.cpp/discussions/21961)
- MEAI function/tool loop behavior: [Microsoft .NET tool-calling guidance](https://learn.microsoft.com/en-us/dotnet/ai/conceptual/calling-tools)
- MAF middleware and observability seam: [MAF middleware](https://learn.microsoft.com/en-us/agent-framework/journey/adding-middleware)
- MAF agent pipeline: [MAF agent pipeline](https://learn.microsoft.com/en-us/agent-framework/agents/agent-pipeline)
- MEAI abstractions: [Microsoft.Extensions.AI overview](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- IO-aware attention basis: [FlashAttention paper](https://arxiv.org/abs/2205.14135)
- KV paging/background for comparison with mature serving engines: [PagedAttention/vLLM paper](https://arxiv.org/abs/2309.06180)
- current vLLM prefix-block management: [vLLM automatic prefix caching](https://docs.vllm.ai/en/v0.14.0/design/prefix_caching/)
- SGLang shared-prefix scheduling design: [SGLang RadixAttention](https://sgl-project-sglang-93.mintlify.app/concepts/radix-attention) and [SGLang paper](https://arxiv.org/abs/2312.07104)
- consumer-NVIDIA dynamic batching/cache reference: [ExLlamaV2](https://github.com/turboderp-org/exllamav2) and [dynamic generator design](https://github.com/turboderp-org/exllamav2/blob/master/doc/dynamic.md)
- heterogeneous CPU/GPU inference reference: [KTransformers](https://github.com/kvcache-ai/ktransformers)
- loaded-model, parallel-context, and queue policy reference: [Ollama FAQ](https://docs.ollama.com/faq)
- Unsloth implementation/project reference: [Unsloth repository](https://github.com/unslothai/unsloth)
- Unsloth Studio llama.cpp orchestration entry point: [Studio backend source](https://github.com/unslothai/unsloth/blob/main/studio/backend/main.py) and [release notes](https://github.com/unslothai/unsloth/releases)
- Ornith GGUF artifacts: [unsloth/Ornith-1.0-9B-GGUF](https://huggingface.co/unsloth/Ornith-1.0-9B-GGUF/tree/main)
- Qwen3.6 MTP GGUF artifacts: [unsloth/Qwen3.6-27B-MTP-GGUF](https://huggingface.co/unsloth/Qwen3.6-27B-MTP-GGUF/tree/main)
- Gemma 4 GGUF artifacts: [unsloth/gemma-4-12b-it-GGUF](https://huggingface.co/unsloth/gemma-4-12b-it-GGUF/tree/main)
- Google QAT Q4 comparison artifact: [google/gemma-4-12B-it-qat-q4_0-gguf](https://huggingface.co/google/gemma-4-12B-it-qat-q4_0-gguf)

## Final decision

No universal llama.cpp setting change should ship from this audit. The current defaults deliberately optimize for safe single-user execution and prior evidence already disproved several tempting scheduling changes. The concrete next release work should be:

1. bound send/regenerate/resume memory and add a subscriber-aware post-disconnect grace policy within the existing whole-invocation watchdog;
2. instrument prompt/tool/history preparation and queue/resource state;
3. close admission with measured post-load bytes;
4. make host prompt-cache memory and the pooled input/context/micro-batch contract explicit;
5. fix MTP classification, characterize the global invocation queue, and only then evaluate multi-invocation/two-slot execution and speculation on representative 16/32 GB machines and 8–16 GB GPUs.

That sequence improves stability first, produces the evidence needed for safe tuning, and avoids turning high-end-host benchmark wins into regressions on the consumer machines the application is designed to support.
