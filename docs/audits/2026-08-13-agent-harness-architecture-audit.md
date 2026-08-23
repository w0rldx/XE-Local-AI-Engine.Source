## Executive Summary

**Yes. XE Local AI Engine has meaningful agent-harness improvement opportunities, but its core design is already substantially better than a typical first-generation local-agent loop.** The largest benefits on consumer hardware do not require replacing Microsoft Agent Framework (MAF), adding more agents, or building distributed infrastructure. They come from making the existing loop more selective and measurable:

1. **Stop background memory extraction from competing blindly with foreground inference.** An eligible completed or failed run can enqueue a separate local-model extraction call; the queue permits two concurrent extractions and is not visibly coordinated with foreground GPU admission. On a one-GPU desktop, that extra call can cost more than many smaller harness optimizations combined.
2. **Bound large tool results by reference, not only by prefix truncation.** XE caps tool text at 65,536 characters, but retains only a leading prefix and has no general artifact handle, head/tail preview, paging, or selective re-read contract. Build logs, stack traces, file contents, and MCP payloads can therefore consume much of an 8k context or lose the diagnostic tail.
3. **Keep the default tool catalog small as MCP/custom catalogs grow.** Bound agent profiles already curate tools well, and Agent Skills already use progressive disclosure. The Default Assistant path, however, can append every live MCP descriptor and every enabled custom tool. A deterministic discovery/activation layer could cut repeated prompt tokens and tool-selection ambiguity without removing capabilities.
4. **Measure the complete harness per invocation before tuning it.** XE has strong raw-inference benchmarking and substantial OpenTelemetry instrumentation, including aggregate provider rounds and context trimming. It does not yet produce one development-mode record that attributes a useful task's end-to-end time to queueing, provider rounds, prompt tokens, tool schemas, tools, retries, persistence, and background work.
5. **Treat multi-agent work as an explicit expensive capability, not the default.** XE's normal single-agent path is lean: one provider round for a no-tool answer and normally two for one tool plus final answer. A one-step delegated subagent necessarily adds at least a parent decision round, a child round, and a parent continuation. Current research supports XE's conservative default: multi-agent designs help mainly when work is genuinely decomposable or parallelizable.

The highest-value target is therefore an **incremental, resource-aware runtime around the existing MAF/MEAI and llama-server integration**: production-shaped benchmark hooks, an inference-aware background-work queue, bounded tool artifacts, canonical/dynamic tool disclosure, settled-boundary checkpoints for long workflows, and minimal per-task telemetry. The normal chat loop, SignalR resume behavior, deterministic context budgets, pre-first-output retry rule, process isolation, encrypted SQLite persistence, and `--parallel 1` consumer-GPU policy should remain.

**Recommended decision:** approve Tier 1 as a sequence of benchmarked increments; prototype Tier 2 behind development flags; retain MAF and the current transport/runtime architecture; reject always-on planner/reviewer/multi-agent layers and distributed infrastructure.

---

**Audit date:** 2026-08-13<br>
**Audited repository revision:** `37b28937`<br>
**Scope:** research and proposal only; no product implementation<br>
**Target environment:** local-first Linux/Windows, 16–64 GB RAM, CPU-only through one consumer GPU with roughly 8–24 GB VRAM<br>
**Evidence boundary:** source and tests at the audited revision plus current upstream primary sources. No local-model performance number is claimed because no running AppHost/model server was present and this task did not authorize a product mutation or a new runtime/model installation.

## Research Method

The audit traced the implementation from React through SignalR, application orchestration, MAF/MEAI, provider routing, llama-server, tools/MCP, persistence, and back to the client. It also inspected preview workflows, inbound MCP runs, adaptive memory, capacity admission, sandbox execution, evaluation code, tests, documentation, and the existing inference benchmark.

External research prioritized official/upstream material:

- [Microsoft Agent Framework 1.0 announcement](https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-version-1-0/), [agent middleware](https://learn.microsoft.com/en-us/agent-framework/agents/middleware/), [Agent Harness](https://learn.microsoft.com/en-us/agent-framework/agents/harness), [workflows](https://learn.microsoft.com/en-us/agent-framework/workflows/), [handoff orchestration](https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/handoff), and [workflow checkpoints](https://learn.microsoft.com/en-us/agent-framework/workflows/checkpoints).
- Current MCP material: [2026-07-28 specification update](https://blog.modelcontextprotocol.io/posts/2026-07-28/), [cache hints](https://modelcontextprotocol.io/specification/draft/server/utilities/caching), [tool semantics](https://modelcontextprotocol.io/specification/draft/server/tools), and [.NET Tasks](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/tasks/tasks.html).
- llama.cpp's current [server documentation](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md?plain=1), especially common-prefix prompt reuse, slots, cache state, and parallelism.
- Transferable harness mechanisms from [OpenAI Agents SDK orchestration](https://openai.github.io/openai-agents-python/multi_agent/) and [usage accounting](https://openai.github.io/openai-agents-python/usage/), [PydanticAI tool-output limits](https://pydantic.dev/docs/ai/harness/tool-output-limits/), [tool search](https://pydantic.dev/docs/ai/capabilities/tool-search/), [dynamic tool preparation](https://pydantic.dev/docs/ai/capabilities/prepare-tools/), and [step persistence](https://pydantic.dev/docs/ai/harness/step-persistence/), plus [LangGraph persistence](https://docs.langchain.com/oss/python/langgraph/persistence) and [interrupt semantics](https://docs.langchain.com/oss/python/langgraph/interrupts).
- Recent multi-agent evidence: the peer-reviewed Nature study [“Towards a science of scaling agent systems”](https://www.nature.com/articles/s42256-026-01268-y), the preprint [“Do More Agents Help?”](https://huggingface.co/papers/2606.05670), and [“Efficient Agents”](https://arxiv.org/abs/2508.02694). These results are directional rather than directly portable to a local 27B model, but they consistently argue against unconditional multi-agent expansion.

## Current Harness Architecture

### End-to-end flow

```text
React chat UI
  │  shared SignalR connection, reconnect/resume/repair
  ▼
LocalChatHub (authorized Operator endpoint)
  │
  ▼
NodeChatStreamService
  ├── validate mutable conversation / turn ownership
  ├── persist user message + assistant placeholder
  ├── resolve agent, model, knowledge, attachments
  └── create invocation + collision/cancellation state
         │
         ▼
InvocationRunner
  ├── outer deterministic conversation budget
  ├── agent factory + stable tools + one leading system message
  ├── single-agent loop OR explicit MAF workflow
  └── FunctionInvokingChatClient tool loop
         │
         ▼
RuntimeChatClient
  ├── authorized cloud client, when explicitly active
  └── ModelRoutingLocalChatClient
         │
         ▼
DeferredLlamaServerChatClient
  ├── capacity/inference lease
  ├── single-flight ensure-running/self-heal before first output
  └── OpenAI-compatible streaming adapter
         │
         ▼
llama-server (--parallel 1)
         │
         ├── model response
         └── tool request → validated local/MCP/custom tool → result → next model round
         │
         ▼
ChatInvocationStatePump
  ├── immediate/coalesced stream events
  └── growth-triggered + terminal SQLite persistence
         │
         ▼
SignalR → React stream reducer
```

The public chat transport is **SignalR**, not a direct browser SSE endpoint. A few server comments use SSE terminology, but the implementation is an authorized hub and a typed SignalR stream. `LocalChatHub` validates input before persistence and delegates send/regenerate/resume operations (`XE-Local-AI-Engine.Client/Hubs/LocalChatHub.cs:18-173`). The React client maintains one reconnecting connection and supports stream repair/resume (`XE-Local-AI-Engine.Client.React/src/features/chat/api/NodeChatConnection.ts:64-202`; `XE-Local-AI-Engine.Client.React/src/features/chat/api/NodeChatAdapter.ts:340-427,556-600`).

### Lifecycle and execution loop

`NodeChatStreamService` owns chat admission, selected-path persistence, user/assistant records, model/agent resolution, invocation construction, and detach/reconnect behavior (`XE-Local-AI-Engine.Client.Application/Services/Chat/Implementation/NodeChatStreamService.cs:21-118`). `InvocationRunner` registers an active invocation and timeout, warms/resolves the model, seeds per-invocation context and raw-provider budgets, then chooses the single-agent or preview-workflow path (`XE-Local-AI-Engine.Client.Application/Services/Invocation/Implementation/InvocationRunner.cs:294-495`).

The effective normal loop is intentionally simple:

```text
provider round → [optional tool call → validated execution → provider continuation]* → final stream
```

The runner constructs stable tool definitions once per invocation and calls `ChatClientAgent.RunStreamingAsync` with `session: null`. Function execution occurs below the agent through MEAI's `FunctionInvokingChatClient` (`InvocationRunner.cs:869-1157`). Therefore:

- no-tool answer: **one raw provider round**;
- one automatic tool and final answer: normally **two raw provider rounds**;
- each further automatic tool turn: normally **one more provider round**;
- approval: one paused tool boundary plus a continuation after the human answer;
- invalid arguments: deterministic repair where possible, otherwise a bounded corrective provider round.

XE does **not** add a default router, planner, reviewer, formatter, or automatic summarizer call. This is a major strength for slow local inference.

Each normal invocation gets a fresh `ChatClientAgent`, but not a fresh transport or model process. `InvocationAgentFactory` deliberately sends instructions once as a leading system seed and leaves the agent's own instructions null, preventing duplicate system prompts (`XE-Local-AI-Engine.AI.Agent/Invocation/Implementation/InvocationAgentFactory.cs:65-250`). The package set is MEAI 10.8.3, MAF 1.17.0, MCP .NET 2.0.0, OpenAI 2.12.0, and OllamaSharp 5.4.30 (`Directory.Packages.props:63-92`). Some comments still cite MAF 1.15.0; that is documentation drift, not the effective dependency.

### Model/provider lifecycle

`RuntimeChatClient` is a singleton wrapper that chooses the authorized cloud or local branch on every request, while cloud selection is fingerprint-cached (`XE-Local-AI-Engine.Client.Application/Services/CloudProviders/Implementation/RuntimeChatClient.cs:6-100`). The local router resolves the effective model and provider per request and caches clients by `(provider, model)` (`ModelRoutingLocalChatClient.cs:9-30,125-152`). The deferred llama client single-flights inner adapter creation, ensures the process is running, takes an inference lease, and only self-heals before any output has been emitted. It does not replay a partially visible answer (`XE-Local-AI-Engine.Providers.LlamaServer/Implementation/DeferredLlamaServerChatClient.cs:12-29,74-210`).

The llama supervisor launches one slot with `--parallel 1`, explicitly protecting consumer VRAM from multiplied KV allocation (`XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaServerProcessSupervisor.cs:2043-2047`). Repeated sends reuse the process and cached adapter. This is a sound default for a single-user, single-GPU desktop.

### Conversation state, context, and memory

Conversation content and selected branches are encrypted/persisted in SQLite. The outer `ConversationContextBudgeter` is deterministic and LLM-free: it accounts for fixed system/tool overhead, caches framed fixed overhead, truncates historical tool outputs first, drops oldest whole turns next, and protects the most recent turns (`XE-Local-AI-Engine.Client.Application/Services/Invocation/Context/ConversationContextBudgeter.cs:7-25,37-162`). A second, lower `ProviderCallBudgetChatClient` sees every raw provider round after tools are appended, so it can enforce the real provider-facing budget across tool continuations (`XE-Local-AI-Engine.AI.Agent/Chat/ProviderCallBudgetChatClient.cs:12-38,81-150`). `ProviderCallBudget` also caps runaway invocations at 200 provider calls and four million estimated input tokens; those are safety ceilings, not target efficiency (`XE-Local-AI-Engine.AI.Agent/Invocation/ProviderCallBudget.cs:14-80`).

Long-conversation compaction is currently manual. It retains recent turns, folds older selected-path content into a bounded encrypted summary through one or more local-model batches, and leaves originals intact (`XE-Local-AI-Engine.Client.Application/Services/Chat/Compaction/ConversationCompactionService.cs:6-119`; `ConversationSummarizer.cs:47-130`). Consequently, normal turns pay **zero automatic summarization calls**.

Adaptive memory is separate from current conversation context. For agents with playbooks and memory extraction enabled, a completed or failed run can enqueue a fire-and-forget extraction job (`XE-Local-AI-Engine.Client.Application/Services/Chat/Implementation/ChatMemoryExtractionHook.cs:8-60`). The default extraction model is the configured node chat model (`XE-Local-AI-Engine.Client.Application/DependencyInjection/Modules/AddNodeAdaptiveMemoryExtensions.cs:14-23`). A bounded in-memory dispatcher drops rather than blocking when full, but its worker permits two concurrent local extraction calls (`MemoryExtractionOptions.cs:3-31`; `MemoryExtractionWorker.cs:8-45,79-108`). Extraction itself is a structured-output model call followed by lexical/semantic duplicate handling (`DefaultMemoryExtractionAgent.cs:29-78`; `MemoryExtractionService.cs:62-160`). This separation is conceptually good, but scheduling is the audit's clearest resource-contention gap.

### Tools and MCP

Built-in tool instances and schemas are cached in `LocalAgentToolRegistry` (`XE-Local-AI-Engine.AI.Agent/Tools/Implementation/LocalAgentToolRegistry.cs:7-48`). `AgentDefinitionResolver` distinguishes the Default Assistant's capability-gated full offer from bound agent profiles, which intersect a static profile with allowed tools. Imported Agent Skills use progressive disclosure and deterministic identifiers/order (`AgentDefinitionResolver.cs:320-445`).

`LocalToolOfferProvider` combines built-ins, coder/knowledge tools, `ask_user`, live MCP descriptors, enabled custom tools, and profile-enabled subagent spawn. In the full/default path, all current MCP descriptors and enabled custom tools are appended; bound profiles can already be substantially narrower (`XE-Local-AI-Engine.Client.Application/Services/Chat/Implementation/LocalToolOfferProvider.cs:57-105,242-333`). `InvocationToolResolver` then intersects offered and actually executable tools and can only tighten approval requirements (`InvocationToolResolver.cs:34-181`).

Arguments are schema-validated, common errors are deterministically coerced, and invalid calls return structured correction feedback with a default maximum of three invalid-argument iterations. Automatic tool-loop allowance defaults to 40 iterations (`AgentToolPipelineOptions.cs:10-43`). Text results are capped at 65,536 characters, but the general fallback keeps only a prefix plus a truncation marker (`ToolResultBudget.cs:7-102`).

MCP clients are persistent singleton connections. Refresh is serialized; unchanged server clients are reused; discovery failures and timeouts are isolated per server; and the public snapshot is immutable, stable, and sorted (`McpServerConnectionManager.cs:19-24,92-293`; `McpToolRegistry.cs:8-66`). This is already a strong connection-management design. It lacks a model-facing lazy discovery layer for large catalogs.

Inbound MCP agent runs use durable admission, deduplication/fingerprints, quotas, CAS-style ownership, bounded worker concurrency, a watchdog, and encrypted payload compaction (`McpAgentRunCoordinator.cs:53-117`; `McpAgentRunOptions.cs:3-24`; `McpAgentRunCompactionService.cs:7-65`). On application restart, prior non-replayable claims are terminalized rather than resumed (`McpAgentRunRecoveryService.cs:6-40`).

### Planning, workflows, multi-agent behavior, and scheduling

The ordinary path has no separate planner. Preview workflows are explicit, strictly linear MAF workflows and currently in-memory; structured branching/model-switch features are intentionally deferred (`XE-Local-AI-Engine.AI.Agent/PreviewWorkflows/PreviewWorkflowDefinition.cs:3-85`; `PreviewWorkflowExecutionService.cs:147-228,293-336,528-707`). They support bounded concurrency, cancellation, pause, and streaming, but not restart-durable computation.

Subagents are exposed as tools. A child is lazily constructed with its own instructions/tools and is invoked through an `AIFunction`; depth/fan-out are bounded, and same-model spawn work is serialized under capacity policy (`XE-Local-AI-Engine.Client.Application/Services/Capacity/SubAgentSpawnService.cs:120-225,392-459`). `CapacityService` distinguishes admission outcomes including same-model queueing (`CapacityDecision.cs:6-36`). This is more conservative than “spawn freely,” but background memory extraction and some workflow lanes are not yet visibly governed by one unified foreground-aware inference scheduler.

### Streaming, cancellation, persistence, and recovery

`ChatInvocationStatePump` separates fast emission from slow persistence. It immediately emits the first delta, coalesces bursts to roughly 25 updates per second, persists only after meaningful growth, and forces terminal persistence (`XE-Local-AI-Engine.Client.Application/Services/Chat/Implementation/ChatInvocationStatePump.cs:9-27,54-212`). `PartialFlushPolicy` prevents quadratic rewriting as the assistant message grows (`PartialFlushPolicy.cs:5-21`). There are no per-token database writes.

Client disconnection detaches the subscriber rather than immediately cancelling useful work; resume can replay persisted/retained state. A reaper cancels abandoned work after a grace period. Provider retry is restricted to transient failures before the first output; per-model circuit breaking prevents repeated storms, and partial visible output is never silently replayed (`XE-Local-AI-Engine.Client.Application/Services/Invocation/Resilience/ProviderStreamResilience.cs:8-16,35-215`). On host restart, incomplete chat/inbound MCP activity is safely terminalized rather than pretending to resume side effects.

### Sandbox/tool execution

The process sandbox has explicit containment, scrubbed environment, timeout/tree termination, path/symlink guards, output caps, and in-flight tracking (`XE-Local-AI-Engine.Client.Application/Services/Sandbox/Implementation/ProcessSandboxRuntimeProvider.cs:18-80,320-538`). Sandboxes/workspaces can be created or reattached by a stable key, avoiding repeated environment construction (`ProcessSandboxRuntimeProvider.cs:263-303`). Each command still starts a process, which is a reasonable security boundary; a persistent shell should be considered only for measured coding workloads and never by weakening isolation.

### Specialized Development/coding harness

XE also contains a separate, more prescriptive Development harness rather than forcing coding work through ordinary chat. Its lifecycle is:

```text
durable task
  → isolated coder attempt
  → code-owned validation command profile
  → independent read-only reviewer attempt
  → exact-subject approval
  → operator preview/apply
```

The local coder sees a fixed nine-tool surface: bounded list/read/fixed-text search, complete-file write, Git patch application, status/diff, code-owned command IDs, and a typed `submit_implementation` close operation. Multiple tool calls per provider response are disabled, tool/provider rounds are bounded, and claimed changed files/commands are reconciled against exact host evidence (`XE-Local-AI-Engine.Client.Application/Services/Development/DevelopmentCoderModel.cs:41-175,191-305`; `DevelopmentCoderAttemptRunner.cs:63-124,276-310`). Patch/diff editing is therefore supported and preferred for multi-file changes, while complete-file write remains available for bounded files. There is no AST/LSP edit layer or repository symbol index in this harness; search is a literal, managed, byte-bounded scan (`DevelopmentWorkspaceTools.cs:130-190`). Adding an LSP is not automatically an optimization—it should be justified by fewer file reads/edit failures on representative repositories.

After a successful coder attempt, engine-owned validation runs the immutable project command profile and moves only a passing exact subject to review (`DevelopmentValidationRunner.cs:51-122`). The reviewer is read-only, sees the validated subject and structured test counts, must submit one typed verdict, and cannot apply changes (`DevelopmentReviewerModel.cs:48-168`; `DevelopmentReviewerAttemptRunner.cs:55-161`). Approval is tied to patch/manifest/validation artifact hashes, so a changed subject invalidates it. This is an expensive extra model phase, but it is not an always-on observer: it runs once only after deterministic validation and before host apply.

Development already has several mechanisms that ordinary chat can reuse. `DevelopmentProgressDetector` identifies repeated tool calls, repeated command failures, subject oscillation, repeated review findings, low headroom, and no-progress intervals (`DevelopmentProgressDetector.cs:5-190`). `ManagedDevelopmentArtifactBlobStore` plus typed artifact rows already provides a security-aware local artifact model. Command evidence is bounded and parsed from raw output before truncation so the test summary at the tail is not lost (`DevelopmentWorkspaceTools.cs:353-387,483-517`). A general artifact/result layer should reuse these patterns rather than create an unrelated store.

No A2A protocol implementation or committed A2A plan was found at this revision. Agent-to-agent behavior is currently internal MAF workflow handoff or bounded subagent-as-tool invocation. External A2A should remain a product-driven interoperability decision.

### Observability and evaluation

XE already records model-readiness and load phases, TTFT, usage, detach/reap/failure events, capacity outcomes, context-budget drops/truncations, raw provider-round totals, approvals, MCP timeouts, SQLite contention, and tool spans. `NodeMetrics` includes model-ready-to-first-output and turn-to-load timing (`XE-Local-AI-Engine.Client.Application/Common/Telemetry/NodeMetrics.cs:219-272`). The raw provider budget layer emits aggregate counters for provider rounds and trimming.

`InferenceBenchmarkHarness` already measures cold and warm prompts, TTFT, prompt processing/generation, cache reuse, a tool loop, and long-context behavior. It calls the provider/server path directly, not the complete NodeChat → agent → persistence → transport pipeline. `MafPlaybookEvalAgentRunner` reproduces a threadless no-tool MAF conversation for golden evaluations, but is likewise not a full harness scenario. The gap is integration and attribution, not a lack of metrics or tests.

## Current Microsoft Agent Framework Assessment

Microsoft describes Agent Framework 1.0, released 2026-04-03 for .NET and Python, as production-ready with stable APIs and long-term support. That status applies to the core release, not uniformly to every newly layered helper. The current Agent Harness documentation says the harness creation API is released while background agents, file access, and looping remain experimental, and its shell tooling comes from a prerelease package ([MAF 1.0 announcement](https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-version-1-0/), [Agent Harness status](https://learn.microsoft.com/en-us/agent-framework/agents/harness)). XE's 1.17.0 dependency is therefore current enough to evaluate these APIs, but status and provider support must be checked feature by feature.

| MAF capability | Current upstream state | XE usage/gap | Audit disposition |
|---|---|---|---|
| Core agent/provider abstraction | 1.0 stable/production-ready; provider capabilities differ | Used through `ChatClientAgent` and MEAI over local/cloud `IChatClient` routers | Retain; no replacement case |
| Sessions, state, and runtime context | Released session/state APIs, runtime `StateBag`, and context-provider hooks | Normal chat is intentionally threadless (`session: null`); XE reconstructs the selected durable path and owns encrypted persistence | Do not move chat history into MAF merely for fashion; evaluate StateBag for ephemeral workflow state only |
| Middleware/agent pipeline | Released middleware around run/function invocation with DI/runtime context | XE uses MEAI wrappers and application services for budgets, observability, approvals, retries, and tools rather than one MAF-only pipeline | Adopt only where it deletes a duplicate pass or improves correlation; benchmark wrapper overhead rather than assuming it is material |
| Tool execution and MCP | Core tool/function and MCP integrations are part of the supported framework surface; application still owns safety/resource limits | XE uses `FunctionInvokingChatClient`, cached local schemas, persistent MCP clients, approvals, argument repair, result budgets, and sandbox policy | Current application-owned boundary is stronger for local security; use newer MCP cache/subscription features incrementally |
| Structured output | Supported where the selected provider exposes the necessary capability; provider behavior is not uniform | Used for memory extraction; XE additionally sanitizes llama grammar schemas and runs a real negative-control smoke | Keep XE compatibility layer and add failure attribution; do not assume framework support removes llama constraints |
| Streaming and cancellation | Core agent/workflow streaming and cancellation are supported | XE streams through MAF/MEAI, then its own state pump and SignalR resume layer; cancellation/retry semantics are stricter than a generic stream | Retain product transport/recovery; no MAF feature replaces detached stream resume |
| Telemetry | Framework pipeline and OpenTelemetry hooks exist | XE already has richer node/model/capacity/persistence metrics but lacks one task-correlated efficiency record | Correlate rather than replace |
| Context providers and memory | Context-provider/memory surfaces are available; newer harness file-memory/compaction components have mixed maturity | XE has deterministic double budgeting, manual compaction, encrypted adaptive memory, Agent Skills, and bounded knowledge composition | Evaluate components only against explicit deletion/quality goals; never add automatic summary calls without a benchmark |
| Handoffs and multi-agent orchestration | Workflow/handoff orchestration is released; current handoff synchronizes context and filters handoff control messages | XE uses bounded subagent-as-tool and explicit preview workflows; compact handoff/cost accounting is still an opportunity | Keep opt-in and task-gated; do not default to more agents |
| Workflow concurrency, pause, checkpoints, resumability | Workflow scheduling and checkpoints are released APIs | XE preview workflows support in-memory concurrency/pause/cancel but not restart replay; chat and inbound MCP safely terminalize non-replayable work | Best incremental MAF candidate: prototype SQLite-backed checkpoints for preview workflows at side-effect-safe boundaries |
| Agent Harness planning/todos/compaction/files/shell/loop | Harness creation is released; several constituent behaviors remain experimental or prerelease | Overlaps XE's existing tools, AgentHome/Development sandbox, context budgets, memory, and loop | Monitor/prototype isolated pieces; do not wrap the whole XE runtime |
| A2A interoperability | Advertised in the 1.0 framework ecosystem | No A2A implementation or committed plan found in XE at this revision | Add only for a concrete external-agent product requirement |

The net assessment is favorable: **XE is using the stable MAF core at an appropriate abstraction boundary and independently owns the local-desktop concerns MAF cannot decide.** The most promising upstream reuse is workflow checkpoint storage/context synchronization. The least promising is replacing XE's normal chat state, sandbox, or full harness with the newer all-in-one HarnessAgent.

## Existing Strengths

1. **Lean default loop.** Plain chat has no extra routing/planning/review/formatting calls.
2. **System prompt is not duplicated.** The agent receives one leading system message, preserving tokens and a stable prefix.
3. **Two-level deterministic context protection.** The outer conversation budget and inner provider-round budget cover both initial history and tool continuations without automatic summarization.
4. **Streaming and persistence are deliberately decoupled.** Immediate output, bounded update frequency, and growth-based writes protect TTFT and SQLite.
5. **Safe retry semantics.** Retry/self-heal stops after the first visible output, avoiding duplicate side effects or incoherent streams.
6. **Consumer-aware model lifecycle.** Cached clients, single-flight starts, leases, idle management, and `--parallel 1` match one-GPU local use.
7. **Tool security remains fail-closed.** Offer/executable intersection, approval tightening, argument validation/repair, timeouts, and sandbox/path controls are strong.
8. **MCP connections and snapshots are well managed.** Persistent clients, isolated refresh failure, stable names/order, immutable snapshots, and bounded results are good foundations.
9. **Skills and bound profiles already avoid universal context.** Progressive disclosure and curated profiles mean dynamic tool work should be incremental, not a replacement.
10. **Durable application state is separated from ephemeral computation.** SQLite selected-path content, encrypted memory, resumable client streams, and safe terminalization provide auditability without claiming unsupported workflow replay.
11. **Model/provider abstraction is pragmatic.** MAF/MEAI sits over cached provider clients, while local process supervision remains XE-owned and hardware-aware.
12. **Subagent use is bounded.** Spawn is opt-in, depth/fan-out limited, and capacity-aware rather than the ordinary execution mode.
13. **The Development harness is evidence-first.** A bounded coder, code-owned validation, exact-subject artifacts, read-only reviewer, and operator apply gate are substantially safer than giving an unconstrained chat agent host write/shell access.

## Findings

### 1. Full-harness benchmark and per-invocation efficiency record

**Type:** Harness optimization<br>
**Area:** observability, evaluation, performance<br>
**Current XE Behavior:** XE has a strong provider-level inference benchmark and broad aggregate telemetry, but no single development-mode result that follows a representative task through chat admission, context construction, raw provider rounds, tools, persistence, streaming, retries, and post-turn work. Aggregate provider-round counters cannot answer “which task paid for which rounds?”<br>
**Proposed Improvement:** Extend the existing benchmark infrastructure with production-shaped, non-destructive scenarios: no-tool question, one-tool task, multi-step tool task, long-context task, and repeated-prefix turns. Emit an invocation correlation record containing wall time, queue time, TTFT, raw model rounds, actual/estimated input and output tokens per round, tool-schema characters/tokens, cache-reused prompt tokens when available, tool durations/result sizes, persistence duration, retry/repair count, handoffs, model reloads, and background extraction. Keep this opt-in for development/evaluation rather than logging full content.<br>
**Why It Matters:** Every other optimization risks moving cost rather than removing it. Local inference makes one avoided round or one avoided long prefill more valuable than many allocation-level tweaks.<br>
**Evidence:** OpenAI's Agents SDK exposes run usage including request/token and cached-token accounting ([usage documentation](https://openai.github.io/openai-agents-python/usage/)). XE already has the raw components in `InferenceBenchmarkHarness`, `ProviderCallBudgetChatClient`, tool spans, and `NodeMetrics`; the change is correlation and scenario coverage, not a new observability stack.<br>
**Expected Benefit:** **Very high.** Decision quality; indirect **high** latency/model-call/token benefit by identifying real bottlenecks. Affects latency, model calls, prompt/generation tokens, TTFT, RAM/VRAM pressure attribution, reliability, and agent quality measurement.<br>
**Impact:** Latency, model calls, prompt tokens, generation tokens, TTFT, RAM, VRAM, reliability, and agent quality: measured/attributed rather than directly changed.<br>
**Consumer Hardware Impact:** Proves whether a change helps 16/32/64 GB systems and prevents cloud-shaped assumptions from being applied to one GPU.<br>
**Complexity:** Medium.<br>
**Risk:** Low.<br>
**Confidence:** High.<br>
**Recommendation:** **implement.**

### 2. Foreground-aware scheduling for adaptive memory extraction

**Type:** Harness optimization<br>
**Area:** scheduling, memory, resource management<br>
**Current XE Behavior:** Eligible terminal runs enqueue a separate structured-output extraction using the configured node chat model; the in-memory queue is non-blocking and bounded, but permits two concurrent extraction workers. The path is not visibly admitted through the same foreground inference policy as the user's active turn. Semantic dedup may add embedding work after extraction.<br>
**Proposed Improvement:** Treat memory extraction as low-priority background work. Default local-model concurrency to one, start only when no foreground inference is ready/running, cancel or defer on pressure, coalesce multiple completed runs, and make jobs stale-aware. Add a deterministic novelty/eligibility gate using existing facts such as explicit preference/project-fact cues, minimum substantive content, duplicate fingerprints, failure category, and prior extraction status. Preserve a manual “remember this” path.<br>
**Why It Matters:** On a one-GPU machine, a second local-model call can dominate time, evict useful cache state, or delay the next visible user response. Running two background generations concurrently can increase VRAM pressure even if the UI considers the turn complete.<br>
**Evidence:** Repository paths are `ChatMemoryExtractionHook.cs:26-60`, `MemoryExtractionWorker.cs:79-108`, and `DefaultMemoryExtractionAgent.cs:29-78`. The application's own llama policy uses one slot because parallel KV allocation is expensive. MAF's application-owned safety guidance notes that IO/rate/resource limits remain an application responsibility ([MAF safety](https://learn.microsoft.com/en-us/agent-framework/agents/safety)).<br>
**Expected Benefit:** **Very high.** For agents where extraction is enabled: fewer model calls, lower background latency and VRAM pressure, less cache disruption, and more predictable foreground TTFT.<br>
**Impact:** Latency, model calls, TTFT, RAM, VRAM, reliability, and memory quality: material; prompt/generation tokens: reduced when extraction is skipped or batched.<br>
**Consumer Hardware Impact:** Most important on 8–16 GB VRAM and CPU-only systems; still helpful on 24 GB cards because model context/KV often consumes the remaining headroom.<br>
**Complexity:** Medium.<br>
**Risk:** Medium.<br>
**Confidence:** Medium.<br>
**Recommendation:** **implement.**

### 3. Artifact-backed, head/tail tool-result budgets

**Type:** Harness capability<br>
**Area:** tools, context, persistence<br>
**Current XE Behavior:** General tool text is limited to 65,536 characters and truncated to a leading prefix with a marker. Historical tool results can be excerpted again by context budgeting. There is no generic artifact identifier, tail preservation, paging, or targeted re-read protocol for oversized results.<br>
**Proposed Improvement:** Extend the existing managed Development artifact pattern into a bounded, access-controlled temporary result store rather than inventing a second unrelated persistence design. Return a compact structured envelope with artifact ID, media/type, byte/line count, hash, expiry, a head/tail diagnostic preview, and truncation reason. Expose deterministic bounded operations such as `read_artifact(range)`, `search_artifact(pattern, max_hits)`, and `extract_diagnostics` using parsers—not another LLM by default. Apply the same envelope to local, sandbox, custom, and MCP results.<br>
**Why It Matters:** Compiler errors and stack traces often end in the tail. A 65,536-character result is roughly 16k tokens under a coarse four-characters-per-token estimate—already larger than XE's common default context budget. Prefix-only truncation can be both expensive and incomplete.<br>
**Evidence:** `ToolResultBudget.cs:7-102`, `AgentToolPipelineOptions.cs:10-43`, and the existing `ManagedDevelopmentArtifactBlobStore`/typed Development artifacts. Development command handling already parses raw test structure before keeping bounded evidence (`DevelopmentWorkspaceTools.cs:353-387,483-517`). PydanticAI documents the same transferable pattern: head/tail limits or spill-to-artifact with bounded retrieval ([tool-output limits](https://pydantic.dev/docs/ai/harness/tool-output-limits/)).<br>
**Expected Benefit:** **High.** For coding/MCP/log workloads: prompt tokens, prefill latency, context-overflow reliability, and diagnostic quality. Low for small simple tools.<br>
**Impact:** Prompt tokens, latency, RAM/context pressure, reliability, and agent quality: material; model calls, generation tokens, TTFT, and VRAM: indirect or workload-dependent.<br>
**Consumer Hardware Impact:** Long prefills are especially costly locally and larger contexts raise KV RAM/VRAM use. The artifact itself can remain on local disk/SQLite with strict quotas.<br>
**Complexity:** Medium.<br>
**Risk:** Medium.<br>
**Confidence:** High.<br>
**Recommendation:** **implement.**

### 4. Canonical dynamic tool and MCP disclosure

**Type:** Harness optimization<br>
**Area:** tools, MCP, context, caching<br>
**Current XE Behavior:** Static schemas are cached, bound profiles curate tools, and Agent Skills are progressively disclosed. The full/default offer nevertheless appends all live MCP descriptors and all enabled custom tools. Every provider round repeats those definitions. Stable sorting helps prefix reuse but does not remove token cost or ambiguity.<br>
**Proposed Improvement:** Preserve a small, canonical core offer. When the catalog exceeds a measured token/count threshold, expose a deterministic local `search_tools`/`activate_tool_namespace` capability whose index uses names, descriptions, tags, server, and argument signatures. Activated schemas remain stable for the rest of the invocation and are appended in canonical order. Prefer rule-based state filters where the task state is explicit (for example, no write tools before a workspace exists); use model-driven search only when needed. Bound profiles remain authoritative and `ask_user` remains available.<br>
**Why It Matters:** Tool schemas are paid again on every raw provider round. Large catalogs can also reduce tool selection quality. The opportunity grows with MCP adoption, even though today's built-in offer is manageable.<br>
**Evidence:** `LocalToolOfferProvider.cs:242-333` and `AgentDefinitionResolver.cs:405-445`. PydanticAI supports deferred tool search and dynamic preparation ([tool search](https://pydantic.dev/docs/ai/capabilities/tool-search/), [prepare tools](https://pydantic.dev/docs/ai/capabilities/prepare-tools/)). The current MCP specification adds cacheable deterministic list semantics, but model-facing disclosure remains a harness choice ([MCP caching](https://modelcontextprotocol.io/specification/draft/server/utilities/caching)).<br>
**Expected Benefit:** **High.** With large MCP/custom catalogs; **medium/low** with current curated profiles. Reduces prompt tokens, prefill latency, context pressure, and potentially tool-selection errors.<br>
**Impact:** Prompt tokens, prefill latency/TTFT, RAM/VRAM context pressure, reliability, and tool-selection quality: material above a catalog threshold; model calls/generation tokens: usually unchanged.<br>
**Consumer Hardware Impact:** Repeated schema prefill is visible on local models; avoiding 2k schema tokens across five raw rounds avoids processing roughly 10k input tokens even though the logical catalog changed only once. That example is illustrative, not a repository measurement.<br>
**Complexity:** Medium.<br>
**Risk:** Medium.<br>
**Confidence:** Medium.<br>
**Recommendation:** **prototype first.**

### 5. Prefix-cache-aware prompt canonicalization and measurement

**Type:** Harness optimization<br>
**Area:** context, caching, llama-server integration<br>
**Current XE Behavior:** XE already does many correct things: one leading system message, stable tool order, deterministic agent configuration hashes/nonces, cached schemas, stable process/client reuse, and llama-server prefix reuse. Volatile conversation content follows the stable prefix. What is not proven is the actual cache-hit behavior of production-shaped multi-round agent requests, particularly when active tools change, knowledge/attachments are inserted, or handoffs rebuild seeds.<br>
**Proposed Improvement:** Add a prompt-layout contract/test that canonicalizes system text, tool order, JSON-schema serialization, whitespace, and stable knowledge sections; places timestamps/request IDs/volatile state after the stable prefix; and reports cached/reused prompt tokens per raw round when llama-server exposes them. Compare a fixed catalog with dynamic activation before choosing one globally. Avoid explicit slot pinning until measured.<br>
**Why It Matters:** llama.cpp common-prefix reuse depends on exact token identity. A semantically equivalent reordering can cause a full prefill.<br>
**Evidence:** llama-server documents prompt caching/common-prefix reuse and slot controls in its [server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md?plain=1). Repository evidence includes `InvocationAgentFactory.cs:179-250`, deterministic MCP snapshots (`McpServerConnectionManager.cs:282-293`), and the existing inference benchmark's cold/warm cache metric.<br>
**Expected Benefit:** **Medium.** Workload-dependent upside: lower repeated prompt-processing latency and energy use; no reduction in logical prompt tokens, but fewer physically reprocessed prefix tokens.<br>
**Impact:** Physical prompt processing, TTFT, latency, and energy: potentially material; logical prompt/generation tokens, model calls, RAM, VRAM, reliability, and agent quality: unchanged except through cache behavior.<br>
**Consumer Hardware Impact:** Helpful when prompt processing is slow or context/tool prefixes are large; avoids extra RAM/VRAM mechanisms.<br>
**Complexity:** Small.<br>
**Risk:** Low.<br>
**Confidence:** Medium.<br>
**Recommendation:** **benchmark first.**

### 6. Deterministic no-progress and task-specific loop budgets

**Type:** Harness optimization<br>
**Area:** agent loop, retries, reliability<br>
**Current XE Behavior:** Tool arguments are deterministically repaired where possible, invalid attempts are bounded, and provider/invocation ceilings prevent unlimited work. The default maximum of 40 tool iterations and 200 provider calls are broad safety backstops. The specialized Development harness already warns on repeated tools, command failures, subject oscillation, and no progress, but the ordinary chat/tool loop does not clearly enforce equivalent repeated-call/result or cycle protection.<br>
**Proposed Improvement:** Generalize the proven `DevelopmentProgressDetector` pattern at the shared invocation layer. Add deterministic fingerprints for `(tool, normalized arguments, relevant state version)` and returned result hashes. Stop or request clarification on exact repeats without intervening state change; detect small cycles; use lower soft budgets by mode/profile; and permit explicit extensions for long-running approved tasks. Emit structured validation feedback rather than resending a generic enormous prompt.<br>
**Why It Matters:** One runaway local loop can tie up the only GPU for minutes. Exact duplicate detection is cheaper and more reliable than a reviewer-model call.<br>
**Evidence:** `AgentToolPipelineOptions.cs:10-43`, `ToolArgumentRepairAIFunction`, `ProviderCallBudget.cs:14-80`, and `DevelopmentProgressDetector.cs:32-190`. OpenAI Agents SDK explicitly distinguishes deterministic code orchestration from LLM orchestration for predictability/cost/performance ([orchestration guide](https://openai.github.io/openai-agents-python/multi_agent/)).<br>
**Expected Benefit:** **High.** Reliability and **medium** tail-latency/model-call reduction, little impact on normal successful tasks.<br>
**Impact:** Model calls, prompt/generation tokens, tail latency, reliability, and agent quality: material for loops; TTFT, RAM, and VRAM: indirect.<br>
**Consumer Hardware Impact:** Protects a scarce single inference slot and battery/thermal budget.<br>
**Complexity:** Medium.<br>
**Risk:** Medium.<br>
**Confidence:** High.<br>
**Recommendation:** **implement.**

### 7. Effect-aware parallel tool execution, not parallel model inference

**Type:** Harness optimization<br>
**Area:** concurrency, tools, scheduling<br>
**Current XE Behavior:** The model runtime is intentionally single-slot. Some application operations are concurrent, but the general agent tool loop does not expose a clear effect classification that guarantees independent read-only calls can run together while writes remain ordered.<br>
**Proposed Improvement:** Add tool metadata for read-only/idempotent, workspace read, workspace write, external side effect, approval required, and resource class. Permit bounded parallel execution only when one model response produces multiple independent read-only calls and the provider/framework preserves call/result identity. Serialize writes, approvals, MCP tools without declared semantics, model invocations, and commands sharing mutable working-directory state.<br>
**Why It Matters:** Parallel file metadata/search or independent retrieval can reduce wall time without allocating a second KV cache. Parallel generation on one consumer GPU commonly increases contention or OOM risk and should remain off by default.<br>
**Evidence:** XE's `--parallel 1` policy and capacity controls. MAF workflows support concurrency, but its presence is not evidence that every local operation should be concurrent ([workflow docs](https://learn.microsoft.com/en-us/agent-framework/workflows/)).<br>
**Expected Benefit:** **Medium.** Tool-latency benefit in retrieval-heavy tasks; no model-call reduction.<br>
**Impact:** Tool latency and total task latency: potentially material; model calls/tokens/TTFT: unchanged; RAM, VRAM, reliability, and agent quality: risk-dependent.<br>
**Consumer Hardware Impact:** Uses CPU/I/O concurrency while protecting the one GPU. Limits should account for RAM, disk, and sandbox processes.<br>
**Complexity:** Medium.<br>
**Risk:** Medium.<br>
**Confidence:** Medium.<br>
**Recommendation:** **benchmark first.**

### 8. Settled-boundary checkpoints for long workflows

**Type:** Harness capability<br>
**Area:** checkpoints, persistence, recovery, workflows<br>
**Current XE Behavior:** Conversation content, stream state, and inbound MCP ownership are durable, but active computation is not replayable. Preview workflows are in-memory; host restart terminalizes incomplete runs. This is safe but loses long task progress.<br>
**Proposed Improvement:** For preview/long-running workflows—not ordinary chat initially—persist a compact checkpoint after a settled boundary: completed step ID, topology/version, selected model/agent fingerprints, durable facts/artifact references, outstanding approval, and idempotency keys. Resume only from a side-effect-safe boundary; never replay a tool effect solely because the last assistant text is absent. Use SQLite and existing encrypted/event-ledger patterns.<br>
**Why It Matters:** A local desktop can sleep, update, or restart during long work. Restarting from the original prompt repeats expensive inference and potentially unsafe tools.<br>
**Evidence:** MAF 1.0 workflows provide checkpoint/resume support ([checkpoints](https://learn.microsoft.com/en-us/agent-framework/workflows/checkpoints)); current handoff orchestration synchronizes context and filters handoff artifacts ([handoff docs](https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/handoff)). LangGraph's transferable principle is durable state at explicit interrupt/checkpoint boundaries with idempotent side effects ([persistence](https://docs.langchain.com/oss/python/langgraph/persistence), [interrupts](https://docs.langchain.com/oss/python/langgraph/interrupts)).<br>
**Expected Benefit:** **High.** Reliability for long workflows; may avoid many repeated model/tool calls after a crash; negligible for short chat.<br>
**Impact:** Reliability and recoverability: material; repeated model calls, prompt/generation tokens, latency, and agent quality after interruption: potentially material; TTFT/RAM/VRAM: small steady-state cost.<br>
**Consumer Hardware Impact:** Saves the most on slow CPU-only and large local-model runs. SQLite is proportional to a desktop app; no distributed workflow service is needed.<br>
**Complexity:** Large.<br>
**Risk:** High.<br>
**Confidence:** Medium.<br>
**Recommendation:** **prototype first.**

### 9. Delegation gate and compact handoff contract

**Type:** Harness optimization<br>
**Area:** multi-agent, planning, context<br>
**Current XE Behavior:** Subagents are optional tools with bounded depth/fan-out and capacity admission. A delegated one-step task still requires at least a parent round to select delegation, one or more child rounds, and a parent continuation; agent/system/tool context is rebuilt for the child. Preview handoffs similarly add participant rounds.<br>
**Proposed Improvement:** Require an explicit estimated-benefit gate: delegate only for genuinely separable specialist context, independent parallel research on hardware that can support it, or a smaller already-loaded model with a proven cost advantage. Hand off a structured task packet—goal, constraints, artifact references, selected facts, expected output, and stop condition—rather than full parent history. Record call/token cost by participant. Keep single-agent-with-tools as the default.<br>
**Why It Matters:** With a local 27B model, a conceptual separation can add minutes even when it looks architecturally clean.<br>
**Evidence:** `SubAgentSpawnService.cs:120-225,392-459`. OpenAI Agents SDK recommends choosing code orchestration when deterministic behavior, speed, and cost matter ([multi-agent orchestration](https://openai.github.io/openai-agents-python/multi_agent/)). Recent empirical work finds multi-agent value highly task-dependent; the Nature study reports benefits for parallelizable tasks and degradation for sequential ones ([Nature](https://www.nature.com/articles/s42256-026-01268-y)). “Do More Agents Help?” likewise reports that most matched multi-agent variants did not beat the single-agent baseline ([paper page](https://huggingface.co/papers/2606.05670)); treat its exact results as external preprint evidence, not XE measurements.<br>
**Expected Benefit:** **High.** Model-call and latency avoidance when delegation would not add capability; possible quality improvement through cleaner handoffs.<br>
**Impact:** Model calls, prompt/generation tokens, latency, RAM/VRAM pressure, and agent quality: material when delegation is considered; TTFT and reliability: workload-dependent.<br>
**Consumer Hardware Impact:** Avoids concurrent or serial duplicate prefill on one GPU.<br>
**Complexity:** Medium.<br>
**Risk:** Low.<br>
**Confidence:** High.<br>
**Recommendation:** **implement.**

### 10. Loaded-model-aware routing only

**Type:** Harness capability<br>
**Area:** model routing, scheduling, resource management<br>
**Current XE Behavior:** Model selection is explicit/per request and clients are cached. Local llama processes are supervised and capacity-aware. Memory extraction defaults to the main chat model. The harness does not yet choose a smaller model by task economics, which avoids hidden reloads but misses an opportunity on machines that can keep multiple suitable models resident.<br>
**Proposed Improvement:** Add a small explainable routing policy only after resource/quality benchmarks exist: prefer the already-loaded capable model; use deterministic code instead of a router call; use a smaller model only if it is already resident or its amortized load plus expected future calls beats the current model; never evict the main model for one classification/summarization call unless explicitly configured. Include task type, schema/tool support, context length, RAM/VRAM headroom, load time, queue depth, and quality floor.<br>
**Why It Matters:** Model switching can cost more than the task. Conversely, on 64 GB RAM or a high-VRAM system, an already-resident small model could handle extraction/classification cheaply.<br>
**Evidence:** `ModelRoutingLocalChatClient.cs:125-152` and current capacity/process supervision. This is primarily a repository-derived economic constraint; no external cloud router benchmark should be treated as portable to model-loading desktops.<br>
**Expected Benefit:** **Uncertain.** Hardware/workload dependent. Potential lower latency, energy, and VRAM occupancy; potential severe regression if reloads are frequent.<br>
**Impact:** Latency, TTFT, RAM, VRAM, model calls, prompt/generation tokens, reliability, and agent quality: all hardware/workload-dependent and potentially positive or negative.<br>
**Consumer Hardware Impact:** Likely unsuitable by default on 16 GB RAM/8 GB VRAM; more plausible at 64 GB RAM/24 GB VRAM or CPU+GPU split residency.<br>
**Complexity:** Large.<br>
**Risk:** High.<br>
**Confidence:** Experimental.<br>
**Recommendation:** **prototype first.**

### 11. Structured-output failure telemetry and targeted repair

**Type:** Harness optimization<br>
**Area:** structured output, retries, reliability<br>
**Current XE Behavior:** XE sanitizes tool schemas for llama grammar compatibility, validates arguments deterministically, and uses MEAI structured output for memory extraction. Provider retry is bounded and pre-first-output only. The audit did not find evidence that malformed structured-output rates, grammar failures, and repair rounds are attributed per schema/model.<br>
**Proposed Improvement:** Record schema fingerprint/size, constrained-decoding mode, validation outcome, and repair count. Prefer native JSON-schema/grammar constraints where the selected provider supports them and XE's sanitizer confirms compatibility. On failure, retry with only validation errors plus the invalid fragment where safe—not the unchanged full context. Apply deterministic coercion first and bound repair to one or a small profile-specific number.<br>
**Why It Matters:** Exact constraints can replace repeated inference, but a huge grammar or unsupported schema can fail before sampling. XE has already paid for a real llama grammar-bound issue, so observability and the existing negative-control smoke remain load-bearing.<br>
**Evidence:** `DeferredLlamaServerChatClient.cs:274-323`, `ToolArgumentRepairAIFunction`, and `scripts/run-tool-grammar-smoke-local.sh`. Current MAF provider features remain provider-dependent ([provider docs](https://learn.microsoft.com/en-us/agent-framework/agents/providers/)).<br>
**Expected Benefit:** **Medium.** Reliability and tail-latency/model-call reduction; high for schemas with current repair failures, otherwise low.<br>
**Impact:** Retry model calls, prompt/generation tokens, tail latency, reliability, and structured-output quality: material when failures occur; TTFT/RAM/VRAM: indirect.<br>
**Consumer Hardware Impact:** Avoids repeating a long local prefill for a small JSON error.<br>
**Complexity:** Medium.<br>
**Risk:** Low.<br>
**Confidence:** Medium.<br>
**Recommendation:** **implement.**

### 12. Unified lightweight resource queue

**Type:** Harness capability<br>
**Area:** scheduling, concurrency, backpressure<br>
**Current XE Behavior:** XE has strong model lifecycle leases, capacity decisions, same-model subagent queueing, bounded MCP workers, bounded memory extraction, and single-slot llama processes. These policies are distributed by subsystem, so priority between foreground chat, spawned agents, memory extraction, embeddings, preview workflows, and inbound MCP runs is not expressed in one small policy surface.<br>
**Proposed Improvement:** Introduce a lightweight in-process resource arbiter above existing leases, not a new distributed scheduler. It should expose resource class, priority, model/role, cancellation, enqueue time, and estimated memory/context class. Suggested order: interactive continuation/approval resume; interactive first turn; already-running side-effect-safe continuation; explicit inbound run; subagent; memory extraction/evaluation. Permit concurrent CPU/I/O tools under caps while serializing model work per loaded process. Persist only long-lived accepted work that already has a durable contract.<br>
**Why It Matters:** Independent bounded queues can each be locally correct while collectively overcommitting one GPU or starving an interactive turn.<br>
**Evidence:** `CapacityService`, `SubAgentSpawnService`, `MemoryExtractionWorker`, `McpAgentRunOptions`, and the supervisor's one-slot rule.<br>
**Expected Benefit:** **High.** Predictability/TTFT and reliability under mixed workloads; little effect when only one chat runs.<br>
**Impact:** Queue time, TTFT, latency, RAM, VRAM, reliability, and fairness: material under mixed load; model calls/tokens and agent quality: indirect.<br>
**Consumer Hardware Impact:** Directly reflects one-GPU/RAM constraints without Kubernetes-style complexity.<br>
**Complexity:** Large.<br>
**Risk:** Medium.<br>
**Confidence:** Medium.<br>
**Recommendation:** **prototype first.**

### 13. MCP 2026 compatibility and cache-hint adoption

**Type:** Harness capability<br>
**Area:** MCP, interoperability, caching<br>
**Current XE Behavior:** XE pins MCP .NET 2.0.0, keeps clients alive, lists tools at refresh, uses stable ordering, applies timeouts/approval/result budgets, and maintains durable inbound agent-run semantics. It does not visibly consume the new specification's deterministic list cache hints, subscription changes, or Tasks extension as a general harness feature.<br>
**Proposed Improvement:** Add negotiated-protocol/capability telemetry and compatibility tests. Where the .NET SDK exposes it, honor `ttlMs`/`cacheScope` for deterministic capability lists and invalidate on subscription notification. Consider MCP Tasks only for genuinely long external operations; do not map every local agent turn onto Tasks. Keep tool discovery metadata out of every model round through Finding 4.<br>
**Why It Matters:** Correct capability caching can avoid repeated discovery while preserving freshness; Tasks could improve cancellation/status for long external calls. Protocol layers provide no benefit if added without an external interoperability need.<br>
**Evidence:** The [2026-07-28 MCP update](https://blog.modelcontextprotocol.io/posts/2026-07-28/), [cache-hint specification](https://modelcontextprotocol.io/specification/draft/server/utilities/caching), and [.NET Tasks guide](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/tasks/tasks.html).<br>
**Expected Benefit:** **Medium.** Reliability/discovery efficiency for dynamic MCP deployments; low for a few static local servers.<br>
**Impact:** Discovery latency, metadata prompt tokens, reliability, and cancellation: potentially material for dynamic MCP use; model calls, generation tokens, TTFT, RAM, VRAM, and agent quality: mostly indirect.<br>
**Consumer Hardware Impact:** Small local metadata savings; no extra infrastructure.<br>
**Complexity:** Medium.<br>
**Risk:** Medium.<br>
**Confidence:** Medium.<br>
**Recommendation:** **monitor upstream.**

### 14. Do not replace MAF; selectively evaluate its newer harness/checkpoint APIs

**Type:** Architectural alternative<br>
**Area:** framework, maintainability<br>
**Current XE Behavior:** XE uses MAF 1.17/MEAI for agent and workflow abstractions but intentionally owns transport resume, selected-path context, encrypted persistence, local process capacity, sandbox security, and provider recovery. It uses `session: null` for the normal threadless turn and custom double budgeting. Preview workflows use MAF executors in memory.<br>
**Proposed Improvement:** Retain the current framework boundary. Evaluate current MAF `HarnessAgent`, context providers/StateBag, middleware, handoff context synchronization, and checkpoint storage only against a concrete deletion or capability goal. The best first candidate is preview-workflow checkpointing; the worst is replacing XE's chat persistence or forcing every turn into a stateful `AgentSession`.<br>
**Why It Matters:** MAF 1.0 is now GA at the core, and newer harness/workflow APIs may remove custom glue. Some features remain evolving/provider-dependent. XE's local lifecycle and security constraints are product-specific and cannot be delegated safely to a generic agent framework.<br>
**Evidence:** [MAF 1.0 announcement](https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-version-1-0/), [Agent Harness](https://learn.microsoft.com/en-us/agent-framework/agents/harness), [middleware](https://learn.microsoft.com/en-us/agent-framework/agents/middleware/), [runtime context](https://learn.microsoft.com/en-us/agent-framework/agents/middleware/runtime-context), and [checkpoints](https://learn.microsoft.com/en-us/agent-framework/workflows/checkpoints).<br>
**Expected Benefit:** **Medium.** Maintainability/capability if a new API replaces proven custom code; **negative** if it duplicates persistence/context layers or introduces opaque calls.<br>
**Impact:** Maintainability and reliability: potentially material; latency, model calls, prompt/generation tokens, TTFT, RAM, VRAM, and agent quality: no inherent benefit from framework replacement.<br>
**Consumer Hardware Impact:** Framework replacement does not itself reduce provider rounds or tokens. Only adopt features with measured call/context effects.<br>
**Complexity:** Architectural.<br>
**Risk:** High.<br>
**Confidence:** High.<br>
**Recommendation:** **monitor upstream.**

### 15. Retain the gated Development reviewer; benchmark any risk-based bypass

**Type:** Harness optimization<br>
**Area:** coding agent, reviewer, model-call efficiency, security<br>
**Current XE Behavior:** A successful Development coder attempt is followed by deterministic validation. Only a passing exact subject enters one independent read-only reviewer attempt, and only reviewer approval moves it to operator apply. Failed validation returns to coding without paying for a reviewer. Review rounds are bounded, evidence hashes prevent stale approval, and the reviewer lacks write/command/apply capabilities (`DevelopmentManagementService.cs:267-334`; `DevelopmentValidationRunner.cs:81-122`; `DevelopmentReviewerAttemptRunner.cs:101-161`).<br>
**Proposed Improvement:** Keep “always review after passing validation” as the safe default. Add task-level reviewer cost/defect-catch telemetry. Only if evidence shows high latency and near-zero incremental catches should XE prototype an explicit trusted/low-risk mode that can stop at deterministic validation plus operator diff review for narrowly classified changes. Never silently skip review for security-sensitive files, failed/partial tests, dependency/config changes, broad diffs, cloud-generated patches, or operator-configured protected paths. Event-triggered review is a product policy, not a default optimization.<br>
**Why It Matters:** The reviewer is an additional local-model invocation and read-tool loop, so it can be expensive. It is also the only semantic, independent check after deterministic commands; removing it without evidence trades quality and security for latency.<br>
**Evidence:** Repository flow above. The multi-agent literature finds reviewer/agent benefits task-dependent rather than universal ([Nature study](https://www.nature.com/articles/s42256-026-01268-y); [Efficient Agents](https://arxiv.org/abs/2508.02694)). Deterministic tests cannot prove requirements, but a reviewer that never catches additional issues is not cost-effective.<br>
**Expected Benefit:** **Uncertain.** Telemetry is high value; an approved bypass could save one or more provider rounds on low-risk tasks but may reduce agent quality/reliability. Affects model calls, prompt/generation tokens, latency, and safety.<br>
**Impact:** Reviewer model calls, prompt/generation tokens, latency, reliability, security, and agent quality: material trade-off; TTFT/RAM/VRAM: workload-dependent.<br>
**Consumer Hardware Impact:** One reviewer run can be a large fraction of total coding-task time on CPU or one GPU. The current gating already avoids review when validation fails, which is the correct first optimization.<br>
**Complexity:** Medium.<br>
**Risk:** High.<br>
**Confidence:** Experimental.<br>
**Recommendation:** **benchmark first.**

## Prioritized Result

### Tier 1 — High Value / Low Risk

1. Full-harness benchmark and correlated per-invocation efficiency record.
2. Foreground-aware memory-extraction scheduling, initially without changing extraction semantics.
3. Artifact-backed head/tail tool-result envelopes and bounded retrieval.
4. Prefix canonicalization tests and production-shaped prompt-cache measurement.
5. Deterministic duplicate/no-progress protection and task-specific soft budgets.
6. Structured-output failure/repair telemetry.

### Tier 2 — High Potential / Benchmark First

1. Deterministic/event-triggered memory extraction and batching.
2. Dynamic tool/MCP discovery above a measured catalog threshold.
3. Effect-aware parallel read-only tools.
4. One lightweight resource queue across foreground/background inference classes.
5. Native constrained output for additional non-chat classifiers/extractors.
6. Reviewer catch-rate/cost measurement; any risk-based bypass remains explicit and experimental.

### Tier 3 — Strategic Harness Capabilities

1. Settled-boundary SQLite checkpoints for preview and long-running workflows.
2. A reusable local artifact/reference layer for tool output and agent handoffs.
3. Development-mode full-harness evaluation scenarios and regression budgets.
4. Compact structured subagent/handoff packets with participant cost accounting.
5. Incremental MCP 2026 cache/subscription/Tasks compatibility where demanded.

### Tier 4 — Hardware / Workload Specific

1. Already-loaded smaller-model routing on high-memory systems.
2. More than one simultaneous model request only after per-device throughput/VRAM evidence.
3. Persistent interactive shell sessions for coding agents only if process startup is measured as material and state can be securely reset.
4. More aggressive tool parallelism on high-core/high-I/O systems.

### Tier 5 — Monitor / Reject

- **Reject:** replacing MAF/MEAI with LangGraph, PydanticAI, AutoGen, or another runtime solely because it is newer. Their useful mechanisms can be implemented incrementally in .NET.
- **Reject:** always-on router → planner → worker → reviewer → formatter chains.
- **Reject:** default debate, best-of-N, or multi-agent swarms on one GPU.
- **Reject:** Redis, Kafka, Kubernetes, or a distributed workflow/database layer.
- **Reject:** a vector database by default; use explicit local memory categories and deterministic retrieval first.
- **Reject:** weakening tool approvals, path validation, sandbox process isolation, cancellation, or audit persistence for speed.
- **Reject:** automatic LLM compaction on every turn; deterministic trimming is already free and compaction is appropriately explicit.
- **Reject:** removing the Development reviewer by default before measuring its defect catch rate and defining a user-approved trust policy.
- **Monitor:** MAF `HarnessAgent` as its APIs stabilize, but do not place it over XE's entire product-specific transport/persistence stack.
- **Monitor:** MCP Tasks/A2A-style external interoperability until a concrete external-agent product case exists.
- **Monitor:** explicit llama-server slot pinning or multi-slot cache policies; current `--parallel 1` makes this low priority.

## Top Bottlenecks

| Rank | Bottleneck | Relative importance | Boundary |
|---:|---|---|---|
| 1 | Post-turn adaptive-memory model work can compete with foreground inference and adds a full local model call for eligible terminal runs | Very high where enabled | Needs task-correlated measurement; not every agent/run is eligible |
| 2 | Broad Default Assistant/MCP/custom tool definitions are repeated across every raw provider round | High as catalogs grow | Bound profiles and skills already mitigate this |
| 3 | Large tool/MCP/sandbox results are inline and prefix-truncated rather than artifact-backed | High in coding/log workloads | Low for ordinary small tools |
| 4 | Missing complete per-invocation efficiency attribution hides queue, repeated-round, schema, tool, persistence, and background cost | High strategic bottleneck | Existing raw benchmark/metrics are strong but fragmented |
| 5 | Subagent/handoff/workflow use multiplies local provider rounds and duplicated prefixes | High when invoked, low on default path | Current bounded/opt-in design is already conservative |

Prompt-cache misses from unstable production prefixes could move into the top five, but the repository shows multiple deliberate stability mechanisms. It should be measured before being labeled a current defect.

## Model-Call Reduction Opportunities

| Scenario | Current calls | Proposed calls | Quality impact | Latency impact | Deterministic support |
|---|---:|---:|---|---|---|
| Plain no-tool chat | 1 raw provider round | 1 | None | Already optimal | Keep current direct path |
| One open-ended tool + final answer | Normally 2 | Normally 2 | Reducing to 1 would usually remove either selection or synthesis | Irreducible generic minimum | Argument validation remains deterministic |
| Each additional tool turn | +1 | +1 only when state changed/useful | Duplicate/cycle breaker may stop waste | Medium tail reduction | Fingerprint normalized call/result/state |
| Eligible adaptive-memory extraction | Main run + 1 extraction, potentially followed by embedding dedup work | Main run + 0 for most non-novel runs; batch N eligible runs into 1 where safe | Possible missed memory if gate is too strict | Very high on avoided local call | Novelty/duplicate/event gate, explicit remember escape hatch |
| Manual conversation compaction | 1..N summarizer calls only when requested | Keep 1..N when requested; 0 in hot path | Current explicit behavior preserves quality | No hot-path change | Existing deterministic trimming stays first line |
| Invalid tool arguments | Initial call + up to 3 corrective iterations | Initial + 0 for deterministically repairable errors; bounded targeted retry otherwise | Usually positive | Medium when errors occur | Schema coercion, validation feedback, no-progress detection |
| One-step subagent delegation | At least parent decision + child call(s) + parent continuation (≥3) | 1–2 direct rounds when specialist separation adds no value | May lose specialist focus; gate rather than prohibit | High for avoided delegation | Benefit rule, compact task packet, task capability metadata |
| Preview participant/handoff chain | At least one provider round per invoked participant/step | Only explicitly valuable steps | Depends on workflow | High if redundant steps removed | Code-defined routing/branching before LLM routing |
| Development reviewer | One read-only reviewer tool loop after successful deterministic validation | Keep one by default; potentially 0 only in an explicit measured low-risk mode | Potentially material quality/security loss | One reviewer loop saved | Exact risk rules, passing structured validation, operator diff gate |
| Transient provider failure | 1, up to bounded pre-output retries | Keep | Reliability would regress if removed | Negative only on failure | Current typed transient classifier/circuit breaker |
| Router/planner/reviewer/formatter | Currently 0 by default | 0 by default | Preserve current quality/cost balance | Avoids four common extra calls | Use deterministic routing/validation/tests |

The audit found **no hidden default formatting or planning call to remove**. The biggest practical call-reduction opportunity is post-turn memory work, followed by preventing duplicate tool rounds and unjustified delegation.

## Context Optimization Opportunities

| Context component | Current state | Opportunity | Approximate saving boundary |
|---|---|---|---|
| System instructions | Sent once as leading system seed | Preserve canonical bytes/order; move volatile metadata later | No logical token reduction; potentially large physical prefill reuse |
| Tool definitions | Cached/stable; full/default path can include all MCP/custom tools | Core + deterministic discovery/activation above threshold | Saving equals excluded schema/description tokens on every raw round |
| Conversation history | Deterministically tool-truncated/old-turn-pruned; recent turns protected | Add task-state/artifact references before more LLM summarization | Already bounded; do not promise large savings without traces |
| Manual summary | Maximum roughly 4,000 characters and user-triggered | Keep explicit; later checkpoint summaries can be structured | About 1k tokens under a coarse 4 chars/token estimate |
| Tool results | General maximum 65,536 characters, then prefix-only | 2–8k character head/tail envelope + artifact paging | Worst-case illustrative saving can exceed 14k tokens per subsequent round |
| Agent handoffs | Child/participant receives a new seed and tools | Structured goal/constraints/facts/artifact IDs only | Workload-dependent; measure participant input totals |
| Project/knowledge context | Bounded/fenced; Agent Skills progressively disclosed | Reuse artifact IDs and stable repository/symbol summaries | Workload-dependent; current design already avoids universal injection |
| MCP metadata | Stable, sorted, persistent discovery snapshot | Cache hints + model-facing lazy disclosure | High only with large/dynamic MCP catalogs |

All token conversions above are coarse planning estimates, not tokenizer measurements. The benchmark should use the selected model tokenizer or actual server usage.

## Harness Architecture Comparison

Only transferable mechanisms—not framework replacements—are relevant:

1. **Code orchestration before LLM orchestration.** OpenAI Agents SDK explicitly frames deterministic code orchestration as predictable in speed/cost/performance. XE already follows this in its direct loop; it should extend the principle to duplicate-call detection, memory gating, and resource scheduling.
2. **Deferred tool discovery and dynamic preparation.** PydanticAI's tool-search/prepare mechanisms show how a large catalog can remain discoverable without sending every schema on every round. XE can implement a local deterministic index over its existing stable registry.
3. **Artifact spill for oversized tool results.** PydanticAI's output-limit design demonstrates bounded previews plus references. This directly fits XE's local SQLite/temp-artifact and sandbox security model.
4. **Settled-boundary checkpointing.** Current MAF workflows and LangGraph both persist explicit workflow state around safe boundaries. XE can apply the idea to preview/long workflows without changing ordinary chat or importing a Python runtime.
5. **Per-run usage attribution.** OpenAI Agents SDK exposes request/token/cached-token usage per run. XE has richer local infrastructure metrics but needs the same task-level correlation to optimize useful-work latency.

MAF's own current Agent Harness combines planning, todos, compaction, file memory, shell/file tools, and an execution loop. It is strategically relevant as an upstream source of components, but adopting it wholesale would overlap XE's existing sandbox, context, persistence, and resource policies. The right comparison is “which component deletes proven custom code?” rather than “which framework has more features?”

## Recommended Target Architecture

```text
React / SignalR resume
          │
          ▼
Chat Admission + Durable Conversation Store   (retain)
          │
          ▼
Agent Runtime
  ├── Invocation Efficiency Record             [new correlation, dev/eval]
  ├── Deterministic Context Manager             [retain + artifact references]
  ├── Canonical Tool Registry                   [retain]
  │     └── Tool Discovery/Activation           [new above catalog threshold]
  ├── Loop Guard                                [new duplicate/cycle/soft budget]
  ├── Lightweight Resource Arbiter              [incremental]
  │     ├── foreground model queue
  │     ├── bounded CPU/I/O tool queue
  │     └── idle/background memory queue
  ├── Workflow Checkpoint Adapter               [later, preview workflows]
  └── Encrypted SQLite + Local Artifact Store   [extend existing persistence]
          │
          ▼
MAF / MEAI single-agent or explicit workflow   (retain)
          │
          ▼
Cached Provider Router + llama-server lease     (retain)
          │
          ├── validated local/MCP/custom tools
          │       └── sandbox / artifact paging
          └── one consumer-GPU inference slot by default
          │
          ▼
ChatInvocationStatePump → SignalR → React       (retain)
```

This target adds small policy/control surfaces around the current implementation. It does not insert a new model call, a new service, or a second database into the hot path.

## Suggested Implementation Order

```text
1. Freeze representative harness scenarios and success criteria
        ↓
2. Add per-invocation provider/tool/context/queue correlation
        ↓
3. Record baseline on CPU-only and at least one 8–16 GB and one 20–24 GB GPU class
        ↓
4. Make memory extraction foreground-aware (same semantics first)
        ↓
5. Benchmark; then add deterministic/event-triggered extraction gate
        ↓
6. Add artifact-backed tool results and head/tail previews
        ↓
7. Benchmark long-output coding/MCP scenarios
        ↓
8. Add canonical tool-catalog measurement; prototype discovery above threshold
        ↓
9. Add duplicate/cycle soft budgets and structured-output repair attribution
        ↓
10. Benchmark effect-aware parallel read-only tools
        ↓
11. Prototype unified resource arbitration using existing leases
        ↓
12. Prototype SQLite workflow checkpoints on preview workflows only
        ↓
13. Evaluate loaded-model routing on qualifying hardware
```

Every bold change should retain an A/B benchmark checkpoint. Do not combine context, scheduling, tool artifacts, and checkpoints into one refactor.

## Things That Should Not Be Changed

- **SignalR reconnect/resume and stream repair.** It matches product needs better than replacing the transport for fashion.
- **Fast emit / slow persistence.** The state pump and growth-based flush policy are already a strong TTFT/SQLite design.
- **One leading system message.** Do not move the same instructions into both MAF agent instructions and seed history.
- **Deterministic double context budgeting.** It avoids automatic summarizer calls and covers raw tool continuations.
- **Pre-first-output-only retry/self-heal.** Never replay visible partial output or side effects transparently.
- **MAF/MEAI abstraction boundary.** It provides useful agent/workflow/tool integration while XE retains product-specific control.
- **Cached model/provider clients and single-flight startup.** Agent recreation is not equivalent to model/transport recreation.
- **`--parallel 1` default.** Multiple model slots on one consumer GPU require evidence, not optimism.
- **Bound agent profiles and Agent Skills progressive disclosure.** Dynamic tools should complement these mechanisms.
- **Persistent MCP connections, stable qualified names, immutable sorted snapshots, and per-server isolation.**
- **Tool approvals, argument validation, grammar-schema sanitization, path/symlink rules, and process isolation.**
- **Encrypted SQLite conversation/memory state and durable MCP admission/accounting.**
- **Explicit/manual conversation compaction.** Do not add an automatic local-model summary call to every turn.
- **Subagents as bounded opt-in tools.** Do not make every request a multi-agent workflow.
- **Development's coder → deterministic validation → read-only reviewer → operator apply sequence.** Measure the reviewer, but do not bypass it by default.

## Open Questions

These are product decisions rather than facts the repository can decide:

1. **Memory semantics:** Should adaptive memory remain enabled by default for every playbook-enabled agent, or should it become opt-in/event-triggered with an explicit “remember” control? This determines acceptable recall loss versus saved model calls.
2. **Crash-resume promise:** Is restart-durable continuation a product requirement for preview/inbound long workflows, or is safe terminalization sufficient? Checkpointing complexity is justified only by the former.
3. **MCP scale target:** How many enabled MCP servers/tools should the Default Assistant support before discovery is required—tens, hundreds, or operator-curated only?
4. **Artifact retention:** Should large tool artifacts survive only the invocation, the conversation, or an operator-defined retention window? Security, disk quota, and resume behavior differ.
5. **Hardware benchmark matrix:** Which machines/models are the release acceptance floor for harness work: CPU-only 16 GB, 8 GB GPU, 12/16 GB GPU, and 24 GB GPU? At least two materially different local configurations are needed before adaptive routing/concurrency defaults are safe.
6. **External interoperability:** Is invoking/resuming external agents through MCP Tasks or A2A an intended near-term product feature? If not, monitor rather than add a protocol layer.

## Audit Validation

- `aspire --version` reported 13.4.6; the worktree contains a discoverable AppHost.
- `aspire ps --non-interactive` found no running AppHost. No llama-server or Ollama server was available, so starting the UI would not have produced a representative agent benchmark. No product behavior changed, and a Chrome run was therefore not useful for this research-only deliverable.
- All 25 distinct external report links returned HTTP 200 when retrieved on 2026-08-13.
- A structural check confirmed every required report section and every required recommendation field; all 15 findings use one allowed complexity/risk/confidence/recommendation classification.
- A repository-reference check validated every explicit path and cited line range in the report.
- `git diff --check` passed. No backend/frontend build or test suite was run because the only change is this Markdown audit and no product source, dependency, generated contract, or runtime configuration changed.

## Final Approval Gate

At publication time, this audit implemented **no product or harness change**. It proposed an incremental optimization plan and stopped for approval. Each recommendation required explicit user approval, rejection, modification, or additional research before implementation began.

## Post-Audit Approved Implementation

On 2026-08-13, the user approved the first recommended step: establish a production-shaped harness-efficiency record and a repeatable capture workflow before changing agent behavior.

The approved follow-up adds:

- one content-free terminal efficiency record per admitted invocation, correlated through the existing invocation activity; local-chat totals begin before admission/context/persistence and separately expose aggregate pre-run and queue time;
- bounded `agent_harness_*` metrics for end-to-end/queue/readiness/first-output/provider-round/tool-request latency, provider/tool calls, estimated and reported token counts, repeated tool-schema cost, retries, repairs, handoffs, and deterministic context reductions;
- instrumentation at the existing provider-budget, tool-observability, retry, repair, streaming, and orchestration seams rather than a new parallel execution path;
- `docs/performance/agent-harness-capture-workflow.md`, defining five production-shaped scenarios and A/B comparison gates.

The implementation deliberately adds no API, database table, dependency, prompt/model/tool content logging, or harness behavior change. A tool latency is explicitly request-to-result rather than delegate execution time, and streamed provider elapsed time includes backpressure while the request remains open. Provider APIs do not currently expose per-request cached-prefix tokens, raw usage for every intermediate streaming round, physical reload attribution, or attributable RAM/VRAM peaks, so those remain separate provider/device evidence rather than inferred values.

Validation after approval:

- Release restore and build passed with zero warnings and errors.
- The full Release solution test run passed: 6,006 succeeded, 13 skipped, 0 failed; the assembly guard reported no contamination.
- Aspire boot validation passed with the backend, React client, SQLite resource, and dashboard healthy; `/health/live` and `/health/ready` succeeded; the application resource emitted no error-level OpenTelemetry logs.
- No representative local model runtime was installed/running, so no task-level latency baseline is claimed. The new capture workflow is the required next evidence step on the selected hardware/model matrix.
