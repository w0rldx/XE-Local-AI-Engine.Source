# Agent Mode & the AI Agent Runtime

> Reviewed: 2026-10-07 · Code-grounded.

**What this page covers.** Agent Mode is the governed agentic layer: `XE-Local-AI-Engine.AI.Agent` owns the Microsoft Agent Framework wiring (agent construction, the tool pipeline, invocation, handoff), and `Client.Application/Services/*` owns the application decisions (agent definitions, AgentHome, Playbooks, memory, capacity, sub-agent spawn, Agent Skills, Work Sessions, Coder). Workflow-framework types stay behind interfaces, and approval-required tools never reach an unattended caller.

**Read this if you are** adding or gating a tool, changing how an agent is invoked, working on AgentHome, Playbooks, Skills or Work Sessions, or debugging a turn that timed out. **Skip to** [Key invariants for maintainers](#key-invariants-for-maintainers) for the rules; **reference pages** hold the lookup material (tool sources, options, timeouts and budgets, the AgentHome contract, skill and work-session tables): [AI.Agent runtime](reference/04-ai-agent-runtime.md), [application services](reference/04-application-services.md), [Agent Skills](reference/04-agent-skills.md) and [Work Sessions](reference/04-work-sessions.md); **related pages:** [05](05-chat.md), [12](12-security-and-privacy.md), [21](21-graph-workflows.md).

## Contents

- [1. The AI.Agent runtime (XE-Local-AI-Engine.AI.Agent)](#1-the-aiagent-runtime-xe-local-ai-engineaiagent)
- [2. Application services (Client.Application/Services/*)](#2-application-services-clientapplicationservices)
- [3. The governed Playbook lifecycle (P1–P5)](#3-the-governed-playbook-lifecycle-p1p5)
- [4. Agent Skills](#4-agent-skills)
- [5. Work Sessions (Client.Application/Services/WorkSessions/*)](#5-work-sessions-clientapplicationservicesworksessions)
- [Key invariants for maintainers](#key-invariants-for-maintainers)
- [Related pages](#related-pages)

Agent Mode is XE Local AI Engine's governed agentic layer. It is split across two assemblies:
`XE-Local-AI-Engine.AI.Agent` owns the Microsoft Agent Framework (MAF) / `Microsoft.Extensions.AI`
(MEAI) wiring — agent construction, the tool-execution pipeline, single-agent invocation, and
multi-agent handoff orchestration — while `XE-Local-AI-Engine.Client.Application/Services/*` owns the
*application* decisions: agent definitions, the AgentHome write-back loop, the governed Playbook
lifecycle (manual → feedback → analysis → eval gate → monitoring/retrieval), adaptive memory, capacity
gating, sub-agent spawn, the node-local Custom Tools library, and read-only Coder mode. The seam between them is deliberate: all
`Microsoft.Agents.AI.Workflows` types stay confined behind interfaces so the application layer never
references MAF workflow primitives directly.

---

## 1. The AI.Agent runtime (`XE-Local-AI-Engine.AI.Agent`)

### 1.1 `AddLocalAiAgentRuntime` — the composition root

This covers what `AgentServiceCollectionExtensions.AddLocalAiAgentRuntime`, the single registration entry point, binds, decorates and registers, in order. See [the reference page](reference/04-ai-agent-runtime.md#11-addlocalaiagentruntime--the-composition-root).

### 1.2 The chat-client decorator pipeline

`DecorateChatClientPipeline` (`AgentServiceCollectionExtensions.cs`) decorates the registered
`IChatClient` so that **every** code path — local chat, platform invocations, ClientLocal tools, MCP
tools — shares one execution pipeline. It is built in two stages: a **provider client** (relevance,
budget, OpenTelemetry over the base client), then FICC over that provider client. The returned client
branches on the offered tools, and within each chain the first `.Use` is the **outermost** hop:

```
ToolInvocationObservabilityChatClient       // tool-call request/completion spans + logs
   └─ EmptyToolOfferChatClient              // branches on ChatOptions.Tools
        ├─ tools offered ──► UseFunctionInvocation (FICC)  // MEAI FunctionInvokingChatClient: auto-executes tools
        │                        └─ provider client (below)
        └─ Tools null/empty ──► provider client (below)     // no FICC round at all
provider client:
   ToolRelevanceChatClient                  // narrows the offered tools array, provider call only
      └─ ProviderCallBudgetChatClient       // per-round input budget + cumulative ceilings
           └─ UseOpenTelemetry              // one gen_ai span per provider round
                └─ base IChatClient
```

On the tool path the per-round hop order is unchanged. On the empty-offer path a turn skips FICC
entirely: with nothing offered, FICC would otherwise answer a model's unsolicited function call with a
synthetic "not found" result and run one more provider round. The bypass removes that extra round and
the synthetic transcript entry for tool-free turns; graph-workflow LLM call nodes are the first such
caller. It is not a security boundary — FICC never executed a tool that was not offered.

The decoration is exposed as a **public** method specifically so test harnesses that swap the base
client for a fake (e.g. FakeOllama) can re-apply the full pipeline after their
`RemoveAll`/`AddSingleton`. The decoration factory resolves its options and the relevance selector
**defensively** (`GetService` plus a pinned default): re-decoration is re-entrant and the factory runs
lazily at `IChatClient` resolution, so a missing registration has to fall back rather than throw
during a partial re-decoration.

> **Seam to respect:** because the base client is already FICC-wrapped, `ChatClientAgent`'s constructor
> detects the existing `FunctionInvokingChatClient` (still reachable through `GetService` traversal:
> `EmptyToolOfferChatClient` delegates it to its FICC inner client) and registers the agent's own tools as
> `AdditionalTools` rather than re-wrapping. This is what lets the handoff builder inject bodyless
> `handoff_to_*` declarations that the outer FICC leaves unserviced (the workflow executor routes them).

#### Why each hop sits where it does, and what it may mutate

This covers why each hop of the decorator pipeline sits where it does and what it may mutate, plus the pinned telemetry and FICC policies. See [the reference page](reference/04-ai-agent-runtime.md#why-each-hop-sits-where-it-does-and-what-it-may-mutate).

#### Tool-relevance narrowing

This covers how `ToolRelevanceChatClient` narrows the array sent to the provider while leaving the offer, the config hash and the approval wrap untouched. See [the reference page](reference/04-ai-agent-runtime.md#tool-relevance-narrowing).

##### The lexical ranker and the `list_tools` escape hatch

This covers the default `LexicalToolRelevanceSelector` scoring and the `list_tools` escape hatch that reveals withheld tools. See [the reference page](reference/04-ai-agent-runtime.md#the-lexical-ranker-and-the-list_tools-escape-hatch).

##### The per-turn relevance scope

This covers `ToolRelevanceScope`, the per-turn `AsyncLocal` holder the relevance hop and `list_tools` share. See [the reference page](reference/04-ai-agent-runtime.md#the-per-turn-relevance-scope).

#### Provider-boundary budgeting

This covers why `ProviderCallBudgetChatClient` is the only hop that can bound the inner tool-loop and MAF participant rounds. See [the reference page](reference/04-ai-agent-runtime.md#provider-boundary-budgeting).

##### What the per-round reducer keeps

This covers the policy of `ProviderCallBudgeter`, the deterministic reducer that fits one provider round into the window. See [the reference page](reference/04-ai-agent-runtime.md#what-the-per-round-reducer-keeps).

##### The token estimator

This covers `ProviderMessageTokenEstimator` and its conservative, non-ASCII-weighted estimate. See [the reference page](reference/04-ai-agent-runtime.md#the-token-estimator).

##### The tool-call observation pair

This covers how `ToolInvocationObservabilityChatClient` emits exactly one requested and one completion span per streamed `CallId`. See [the reference page](reference/04-ai-agent-runtime.md#the-tool-call-observation-pair).

### 1.3 Tool registries and catalog — four sources, one offer

All four resolve to `Microsoft.Extensions.AI.AITool` and are model-agnostic so the agent factories
treat them uniformly.
`InvocationToolResolver` (`Tools/InvocationToolResolver.cs`) merges the three registries plus the asynchronous custom-tool catalog into the
concrete tool list passed to each agent; `InvocationToolBridge` adapts metadata tool functions
(`Tools/Implementation/MetadataToolFunction.cs`).

The four registries, their implementations, tool sources and notes are tabled on the reference page. See [the reference page](reference/04-ai-agent-runtime.md#13-tool-registries-and-catalog--four-sources-one-offer).

#### Outbound MCP tools: sessions, failures and results

This covers `McpServerConnectionManager`: tool naming and slugs, session lifecycle, failure handling and result shaping for outbound MCP tools. See [the reference page](reference/04-ai-agent-runtime.md#outbound-mcp-tools-sessions-failures-and-results).

#### Effective approval policy

`IToolApprovalPolicy` applies a **node-level, tighten-only** approval layer after the tool offer is
resolved. For each tool, the effective flag is:

```
catalog default
OR uncategorized tool
OR node category rule
OR node per-tool-name override
```

The policy can turn a default-off tool into an approval-required tool, but it can never waive a
catalog default. `ToolCategory.Unknown` fails closed, so a newly introduced uncategorized tool never
auto-executes. The structural floor remains independent: MCP tools and `run_in_agent_home` are already
approval-wrapped at their registries. Persisted category/name rules are loaded when the node composes
the runtime, so operator changes take effect after the next node restart.

##### The risk taxonomy (`ToolCategory`)

This covers the `ToolCategory` values the approval policy reads and what each one covers. See [the reference page](reference/04-ai-agent-runtime.md#the-risk-taxonomy-toolcategory).

#### Custom Tools execution boundary

This covers the operator-authored `HttpFetch` and `Command` tools: validation, secret masking, schema compilation and execution guards. See [the reference page](reference/04-ai-agent-runtime.md#custom-tools-execution-boundary).

### 1.4 Single-agent invocation — `InvocationAgentFactory`

`InvocationAgentFactory.CreateAsync` (`Invocation/Implementation/InvocationAgentFactory.cs`) builds
an `InvocationAgentContext` from an `InvocationAgentDefinition`. It:

- resolves executable tools from the registries and custom-tool catalog (`ResolveExecutableToolsAsync`),
- builds the `ChatClientAgent` (`BuildAgent`) with resolved skills (MAF progressive disclosure),
- builds seed messages (`BuildSeedMessages` — a leading `System(instructions)` message), and
- assembles a `ChatOptions` carrying `ModelId` and a reasoning `think` option computed from the
  model's thinking capability.

> **Reasoning gotcha (`InvocationAgentFactory.BuildAgent`):** for a **thinking-capable** model the
> requested effort is honored (`think: false|low|medium|high`); for a **non-thinking** model that has
> reasoning *requested* the `think` field is **omitted entirely** (Ollama returns HTTP 400 for an
> unknown think level, but omission lets chat-template-baked reasoning through); only "none"/unspecified
> sends `think: false`. A Codex side-channel key carries the raw effort for the Responses boundary.
> Per-send sampling overrides (`ApplySamplingOptions`) are null/no-op by default to keep the mode-off
> path byte-identical.

#### Building the agent: instructions once, skills through a context provider

This covers why `InvocationAgentFactory.BuildAgent` sends instructions exactly once as the seed message and attaches skills through a context provider. See [the reference page](reference/04-ai-agent-runtime.md#building-the-agent-instructions-once-skills-through-a-context-provider).

#### Per-send sampling reaches two runtimes

This covers how `ApplySamplingOptions` carries per-send sampling overrides to both runtimes. See [the reference page](reference/04-ai-agent-runtime.md#per-send-sampling-reaches-two-runtimes).

#### The reasoning-effort matrix and the thinking budget

This covers `ReasoningOptionsResolver`, the single effort-to-provider mapping, and the thinking budget. See [the reference page](reference/04-ai-agent-runtime.md#the-reasoning-effort-matrix-and-the-thinking-budget).

### 1.5 Multi-agent handoff orchestration — `OrchestrationAgentFactory` + `OrchestrationRunSession`

`OrchestrationAgentFactory.CreateAsync` (`Invocation/Orchestration/Implementation/OrchestrationAgentFactory.cs`)
builds **one `ChatClientAgent` per participant** over the shared decorated `IChatClient` and the same
tool registries, then assembles a MAF handoff `Workflow`:

- `AgentWorkflowBuilder.CreateHandoffBuilderWith(triageAgent)` (a deliberately-adopted API that is stable at
  the pinned version; `MAAIW001` no longer fires);
- **no explicit `OrchestrationEdge`s ⇒ fully-connected mesh** (every agent can hand off to every other);
  explicit edges constrain routing. An agent's `Name`/`Description` drive routing — the target's
  Description is the routing reason.
- The workflow is driven by `InProcessExecution.RunStreamingAsync`; a `TurnToken` is sent to actually
  start the conversation (HandoffStart only *accumulates* the seed without it).

The factory returns an **`IOrchestrationRunSession`** (`Invocation/Orchestration/IOrchestrationRunSession.cs`)
— the boundary that confines every `Microsoft.Agents.AI.Workflows` type. `OrchestrationRunSession`
(`.../OrchestrationRunSession.cs`) exposes:

- `WatchAsync` — drains the `StreamingRun`, maps each `WorkflowEvent` to an `OrchestrationUpdate`
  (streaming update, approval request, terminal, or failure). The idle timeout is a **stall bound while
  the provider is producing output**: the source event decides the clock, so it is **suspended** at the
  start of the watch and after any function call or result (a round's first output and server-side tool
  runs are bounded by the turn deadline), re-armed on visible text or reasoning, and **suspended while a
  tool-approval is pending** (the consumer may block on a human decision for minutes);
- `RespondToApprovalAsync` — resolves a pending `ToolApprovalRequestContent`, sends the
  `ExternalResponse`, and restarts the idle clock unless the run still owes its next output.

#### The idle guard: bounding a non-cooperative provider

This covers `IdleStreamGuard`, the wall-clock idle bound on a streamed orchestration a non-cooperative provider cannot defeat. See [the reference page](reference/04-ai-agent-runtime.md#the-idle-guard-bounding-a-non-cooperative-provider).

#### When orchestration does not compile — the degrade notice

This covers the `OrchestrationResolution` outcomes that degrade an orchestrator definition and the notice each one produces. See [the reference page](reference/04-ai-agent-runtime.md#when-orchestration-does-not-compile--the-degrade-notice).

### 1.6 Other AI.Agent runners

- **`MafPlaybookEvalAgentRunner`** (`Eval/Implementation/MafPlaybookEvalAgentRunner.cs`) — the golden
  eval gate's executor. Builds a `ChatClientAgent` over a **caller-supplied node-local** `IChatClient`
  with an **empty tool set** and runs it **threadless** (`session: null`). It mirrors the real worker
  loop's prompt assembly so the eval measures the injected prompt's effect, not tool behaviour. The
  client is owned by the caller and intentionally not disposed.

[Graph Workflows](21-graph-workflows.md) do **not** add a runner here. An `Agent` node drives the same
headless `IInvocationRunner` stack a scheduled saved-agent run uses, from `Client.Application`, and the
`PreviewWorkflowRunner` that used to sit beside the eval runner went with Open Canvas.

---

## 2. Application services (`Client.Application/Services/*`)

These services own the product behaviour and are wired in the Client host. They depend only on the
AI.Agent interfaces, provider seams (`ILocalModelProvider`, `IChatClient`, `IEmbeddingGenerator` — see
[Local Runtime & Providers](03-local-runtime-and-providers.md)), and persistence stores (see
[Data & Persistence](08-data-and-persistence.md)).

The service areas, their key types and responsibilities are tabled on the reference page. See [the reference page](reference/04-application-services.md#2-application-services-clientapplicationservices).

### 2.1 Per-message agent selection & attribution

`AgentDefinitionResolver.ResolveAsync`
(`Services/Agents/Implementation/AgentDefinitionResolver.cs`) is the per-turn entry point:

- **Unbound conversation ⇒ `null`** → the default persona (embedded prompt, full offer, version 1).
- A binding to a **deleted** definition degrades to the default persona (logged) rather than failing
  the turn — there is no FK on the conversation column by design.
- The definition's **pinned `ModelProfile`** (when set) is the model the turn actually runs on, so the
  tool offer is gated by it, keeping capability-gating and runtime model consistent.

**Tool-offer security invariant** (`ProjectAllowedTools`): only the seeded **"Default Assistant"**
(mode-off persona, identified by forge-proof `Source=Seeded` + `SeedSlug`) receives the *full*
capability-gated offer. **Every other definition is intersected** down to its `AllowedToolNames` — a
selected agent's offer is never widened beyond its allowed set, except by `ask_user` on an interactive turn
and by the four work-session state tools inside a work-session step (§5.4). `spawn_subagent`, `run_python` and
`run_in_agent_home` are opt-in only (they live in the *profile* pool, not the default offer), and a
non-tool-capable model gets an **empty** offer before per-name gating. In chat that empty offer is not silent: a turn
that asked for tools gets one `ToolsWithheld` notice naming the model ([Chat](05-chat.md)). `web_search` and `web_fetch` join the
*default* offer only while the node's Web access setting is on, and for a model outside the trust boundary only while
the `AllowCloudModelWebTools` Privacy switch is also on (MCP tools follow `AllowCloudModelMcpTools` the same way); a
bound agent gets them only through `AllowedToolNames`. Both are approval-flagged like `ask_user`: the flag is the
pause the request consent and result review use, so unattended paths strip them, and orchestration participants and agentic MCP scope
are never offered them ([Security and privacy](12-security-and-privacy.md), ADR 0017). See [Chat](05-chat.md) for how
the selected agent surfaces as per-message attribution.

The derived model requirements, the per-surface unattended refusal table and what is not checked are on the reference page. See [the reference page](reference/04-application-services.md#21-per-message-agent-selection--attribution).

#### The resolved runtime projection

This covers the member order of `ResolvedAgentRuntime`, which members sit outside the config hash and which stay off the wire. See [the reference page](reference/04-application-services.md#the-resolved-runtime-projection).

### 2.2 The AgentHome write-back loop

`AgentHomeService.RunLifecycleAsync` (`Services/AgentHome/Implementation/AgentHomeService.cs`)
drives a sandboxed workspace lifecycle through `ISandboxRuntimeProvider` — for AgentHome that is the
**process-jail provider**, and it stays that way: [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md)
selects a provider **per feature**, giving the container provider to Development Mode only (see
[Local Runtime & Providers](03-local-runtime-and-providers.md) for why inference itself carries no
container dependency). It resolves
selected folders into the sandbox (`ISelectedFolderResolver` via a short-lived scope, since the service
is a singleton and `NodeChatDbContext` isn't thread-safe), runs the agent, applies patches
(`NodePatchApplyService` with `O_NOFOLLOW`/byte-recheck guards). The
`run_in_agent_home` tool is a ClientLocal handler (`Tools/Implementation/RunInAgentHomeToolHandler.cs`).
**One run per node, whoever owns it.** The node has one `agent-home` tree, and an owner mismatch in its manifest wipes
that tree, so `AgentHomeExecutionLeaseManager` keeps **one gate per node**, not per owner-node key. Acquisition never
queues: while any run is in flight, a second run, from the same owner or a different one, gets `AgentHomeBusyException`
(the tool result reads "run_in_agent_home rejected: an AgentHome run is already in progress for this node.").
`AgentHomeManifestService.InitializeAsync` takes the same node lease for the call, borrowing it when the caller already
holds it, so no path reaches the owner-mismatch wipe while a run is in flight; on an idle node the wipe still happens.
Poison (isolation-recovery quarantine) and the ambient lease borrow stay per owner-node key. The gate is released when
the run's lease is disposed, after preparation and execution both complete.

How `run_in_agent_home` is offered, what a run does with its goal, the loop bounds and confinement, the hardened patch export, the result contract, the sandbox posture and conversation-attachment staging are on the reference page. See [the reference page](reference/04-application-services.md#22-the-agenthome-write-back-loop).

#### Resolving a selected folder by id or alias

This covers how `SelectedFolderResolver.ResolveAsync` accepts a folder GUID or alias and why the GUID is tried first. See [the reference page](reference/04-application-services.md#resolving-a-selected-folder-by-id-or-alias).

### 2.3 Capacity gate & sub-agent spawn

`SubAgentSpawnService.SpawnAsync` (`Services/Agents/Implementation/SubAgentSpawnService.cs`) implements the
`spawn_subagent` tool with layered safety:

1. **Validation** — non-blank task and exactly one binding.
2. **Runtime depth guard** — a child runs at `SpawnContext.Current.Depth >= 1` and its tool set already
   omits `spawn_subagent`, so recursion is structurally impossible; a missing context defaults SAFE
   (rejected). Right after it, the **parent trust guard** refuses a root model outside the trust boundary
   (cloud or `Unresolved`) with `ReasonParentOutsideTrustBoundary` unless the operator turned on **Let cloud
   models delegate to sub-agents** (`AllowCloudModelSubAgents`, Node Settings → Privacy, read per call). The
   offer withholds `spawn_subagent` under the same switch; this seam check catches a caller that reached the
   service around the offer. A child keeps its own model's gates.
3. **Per-root fan-out lease** — `context.TryEnterFanOut()`; a missing context is rejected
   conservatively.
4. **Capacity decision** — `ICapacityService.DecideAsync(modelName, ModelRole.Chat, ct)` returns
   `Allow` / `QueueSameModel` / reject:
   - **Allow** consumes a local ledger reservation (released on child exit) or a **cloud-spawn budget**
     unit (a DoS-of-wallet cap);
   - **QueueSameModel** serializes against the one resident process via `ISpawnSerializer` with a
     bounded wait (no second model load).

The child is built like an orchestration participant (`ChatClientAgent`) with the **curated**
binding-resolved tool set (spawn already filtered out) and run as an `AIFunction` inside a `Depth+1`
`SpawnContext` scope. Spawn is restricted to explicit profiles, never the mode-off chat path.

#### What the capacity gate admits without probing

This covers the two short-circuits `CapacityService.DecideAsync` takes before it reads any byte budget. See [the reference page](reference/04-application-services.md#what-the-capacity-gate-admits-without-probing).

#### What a profile-bound child inherits

This covers what a profile-bound child inherits from the complete `ResolvedAgentRuntime`. See [the reference page](reference/04-application-services.md#what-a-profile-bound-child-inherits).

### 2.4 Usage and estimated cost

Completed agent run envelopes retain metadata-only usage: model, provider, UTC timestamp, and
prompt/completion/reasoning/total token counts. `GET /api/local/v1/agents/usage-summary` is
operator-gated and aggregates retained rows by `(model, provider, UTC day)`, with grand totals and a
per-provider rollup. Optional `fromEpochMs` / `toEpochMs` query values form a lower-inclusive,
upper-exclusive range.
`IUsageRateResolver` attaches a server-computed USD estimate using operator overrides or the built-in
rate table. Reasoning tokens are priced as output tokens; local and unpriced models report zero.
These values are estimates, not provider invoices. The response states the execution-log retention
horizon, and neither the ledger nor the summary contains message content.

How the terminal telemetry rides all three terminal paths is on the reference page. See [the reference page](reference/04-application-services.md#24-usage-and-estimated-cost).

### 2.5 The composite turn budget

`TurnPolicy` (`Services/Invocation/Policy/TurnPolicy.cs`) is the immutable per-turn snapshot of every
timeout, retry and budget knob that governs one invocation. `InvocationRunner.RunAsync` resolves it **once**
and flows it unchanged through both the single-agent and the orchestration path, so the two enforce identical
policy for one turn. It is a resolution and documentation seam only: every field is copied from an existing
configured source — the package's `TimeoutSettings` and the `Agent:ConversationContextBudget`,
`Agent:ProviderResilience` and `Agent:ToolPipeline` sections — and nothing on the record is itself persisted
or folded into a runtime package's config hash.

The timeout ladder, the deliberate splits in it, context budgeting and `WithEffectiveContext` are on the reference page. See [the reference page](reference/04-application-services.md#25-the-composite-turn-budget).

### 2.6 The coder reader

`CoderWorkspaceReader` (`Services/Coder/Implementation/CoderWorkspaceReader.cs`) is the single read-only gateway behind
`list_files`, `read_file` and `search_text`. All three are **provider** operations — `ISandboxRuntimeProvider.ListFilesAsync`,
`ReadFileAsync` and `SearchTextAsync` — not argument vectors handed to `ExecuteAsync`.

Why provider operations replace argument vectors, the secret exclusions and the untrusted-content fencing are on the reference page. See [the reference page](reference/04-application-services.md#26-the-coder-reader).

### 2.7 Post-run adaptive memory: the extraction worker's shutdown contract

A completed or failed run may enqueue a fire-and-forget extraction job on `MemoryExtractionDispatcher`;
`MemoryExtractionWorker` (`Services/Memory/Implementation/MemoryExtractionWorker.cs`) drains that queue, running each
job in **its own DI scope** and bounding concurrency with `MemoryExtractionOptions.MaxConcurrentExtractions`.
The load-bearing decision is which token the work runs on. Jobs and the read loop both observe a private
**drain-deadline** token, never the chat send token and never the host stopping token, and ordinary operation never
cancels it. Without that, a client-side cancel or a disposed request scope would lose a completed run's memory — the
one thing this pipeline exists to capture. A host stop completes the queue's writer instead, so the read loop drains
what is buffered and exits on its own.

The shutdown drain, the post-deadline grace and the abandoned-job accounting are on the reference page. See [the reference page](reference/04-application-services.md#27-post-run-adaptive-memory-the-extraction-workers-shutdown-contract).

---

## 3. The governed Playbook lifecycle (P1–P5)

A Playbook is a set of per-agent "actions" (learned instructions) that are folded into the agent's
prompt. Their promotion is governed so that nothing reaches the live prompt without passing the gates:

```
 P1 manual        operator authors an action  ─────────────┐
 P2 feedback      👍/👎 aggregated per agent (n≥3)          │  Insights / FeedbackInsightsService
 P3 analysis      node-local model proposes  ──► Suggested  │  Analysis / DefaultPlaybookAnalysisAgent
 P4 eval gate     golden conversations re-run ──► Enabled   │  Eval / MafPlaybookEvalAgentRunner
 P5 monitoring +  cohort monitoring + relevance retrieval   │  Monitoring + PlaybookRetrievalSelector
    retrieval     (top-k injected, cap MaxEnabledActions=20)─┘
```

- **P1 Manual** — operator authors actions; `PlaybookActionService` owns the state machine.
- **P2 Feedback** — `FeedbackInsightsService` aggregates 👍/👎 per agent (read-only, n≥3 threshold).
- **P3 Analysis** — `PlaybookAnalysisService` + `DefaultPlaybookAnalysisAgent` stage **Suggested**
  actions. **Privacy invariant: this runs on a node-local model only** (no cloud), so user
  conversation content never leaves the host for analysis.
- **P4 Eval gate** — golden conversations are **re-run through the real MAF loop** node-local
  (`MafPlaybookEvalAgentRunner`) to gate **Suggested → Enabled**; golden conversations are stored
  encrypted.
- **P5 Monitoring + retrieval** — `PlaybookMonitorService` does cohort monitoring; at inject time the
  prompt composer applies **relevance retrieval**.

### 3.1 Relevance retrieval at prompt-compose time

In `AgentDefinitionResolver.ComposePromptAsync`: when the playbook is **disabled**, or the effective
model is cloud and `KnowledgeBase:AllowCloudModelAccess` is off (orchestration participants: per
participant), the base instructions flow through unchanged (keeping the runtime config hash
byte-identical). When enabled, `PlaybookRetrievalSelector.SelectAsync` chooses what to inject:

- **At/below `RetrievalThreshold`, or a blank query** → the static prepend in store order, trimmed from
  its tail to the token budgets; a set within budget is byte-identical to the pre-retrieval path.
- **Above the threshold with a non-blank query** → only the top-k most relevant actions, ranked by
  `IPlaybookRetrievalRanker`. The default ranker is **`EmbeddingPlaybookRetrievalRanker`** (cosine over
  embeddings via `ILocalModelProvider.CreateEmbeddingGenerator`), with **`LexicalPlaybookRetrievalRanker`**
  as a fallback. `PlaybookPromptComposer.Compose` then folds the selection into the prompt. Token
  budgets (`MaxInjectedMemoryTokens`, `MaxInjectedFailureMemoryTokens`) bound the injection on both
  paths; every write path also caps `Behavior` at 1000 and `TriggerCondition` at 500 characters
  (`PlaybookActionOptions`), dropping an over-length model proposal rather than truncating it. See
  [Local Runtime & Providers](03-local-runtime-and-providers.md) and [Data & Persistence](08-data-and-persistence.md)
  for embeddings and storage.

---

## 4. Agent Skills

An Agent Skill is a `SKILL.md`-shaped document (name + description + markdown body, optionally
bundled files) that an agent definition selects into via `AllowedSkillIds` and MAF loads on demand —
progressive disclosure, not a static prompt prepend. The implementation conforms to the open
[Agent Skills specification](https://agentskills.io/specification) and to the pinned
`Microsoft.Agents.AI` version in `Directory.Packages.props`, not to Claude Code's product
extensions (`disallowed-tools`, `${CLAUDE_SKILL_DIR}`, nested skills) — those are not part of
the standard.

### 4.1 Data model

This covers the skill tables and their columns, including the plaintext, promote-only `origin` and the content `version`. See [the reference page](reference/04-agent-skills.md#41-data-model).

### 4.2 Resolution — the single choke point

`AgentDefinitionResolver.ProjectSkill` (`Services/Agents/Implementation/AgentDefinitionResolver.cs`)
is the **only** place a stored `AgentSkill` becomes a `ResolvedSkill` that reaches an agent, for both the
invocation path and the sub-agent spawn path. Three things happen there, all load-bearing:

The three resolution rules (enabled and assigned only, MAF-invalid names dropped fail-soft, imported content fenced) are on the reference page. See [the reference page](reference/04-agent-skills.md#42-resolution--the-single-choke-point).

### 4.3 Runtime — MAF progressive disclosure and the three tools

Both agent-construction sites — `InvocationAgentFactory.CreateAsync` (builds the `ChatClientAgent`,
resolves executable tools, then attaches skills) and `SubAgentSpawnService`'s child-binding path — build
a MAF `AgentSkillsProvider` from the resolved skills as `AgentInlineSkill`s through the one
`InvocationSkillsProvider` helper in `AI.Agent` and attach it via
`ChatClientAgentOptions.AIContextProviders`, not through the ordinary tool registries. `AgentSkillsProvider`
/ `AgentInlineSkill` are stable at the pinned MAF version, so that helper carries no `MAAI001` suppression.

The three MAF skill tools, their approval defaults and the inert `run_skill_script` are on the reference page. See [the reference page](reference/04-agent-skills.md#43-runtime--maf-progressive-disclosure-and-the-three-tools).

### 4.4 The sub-agent waiver

A spawned child ordinarily has **every** approval-required tool stripped from its offer
(`SubAgentSpawnService.CurateChildTools`), because a child runs as an `AIFunction` via `AsAIFunction()`
with no per-run options and no human-in-the-loop round-trip: an approval-gated tool would surface a
`ToolApprovalRequestContent` the child can never answer, silently failing every call. Before this work,
`AttachSkillsProvider` attached the provider with its default (all-gated) options anyway, because it
rides `AIContextProviders` and bypasses `CurateChildTools` entirely — so **a skill assigned to a spawned
child could never be loaded**.

The waiver itself and why `run_skill_script` is never waived are on the reference page. See [the reference page](reference/04-agent-skills.md#44-the-sub-agent-waiver).

### 4.5 Import pipeline

`ISkillImportService` (`Services/Agents/ISkillImportService.cs`) is a **two-phase, dry-run-first**
pipeline — the entire reason it exists is that operators overwhelmingly *import* skills
(`npx skills add owner/repo` is the ecosystem norm) rather than author them, and this engine had no
import path at all:
```
source ──► fetch ──► extract ──► parse ──► validate ──► REPORT ──►[operator acknowledgement]──► persist
```

Each pipeline stage and its validation, size and trust rules are on the reference page. See [the reference page](reference/04-agent-skills.md#45-import-pipeline).

### 4.6 Approval scoping

An assigned skill's `load_skill` call demands operator approval on every single load under MAF's
defaults (§4.3) — tolerable once, but re-approving the same skill turn after turn is exactly the
approval fatigue that trains an operator to click "yes" without reading. Approval now carries a scope,
resolved in `ToolApprovalCoordinator.RequestToolApprovalAsync`:

The scope values, the session memo key and its fields, and the unattended fail-fast check are on the reference page. See [the reference page](reference/04-agent-skills.md#46-approval-scoping).

---

## 5. Work Sessions (`Client.Application/Services/WorkSessions/*`)

A **work session** runs one objective as a bounded sequence of *steps*, detached from any HTTP or
SignalR caller. Each step is an ordinary chat turn on the session's own conversation, so nothing about
message persistence, ordered parts, approvals or `ask_user` is re-implemented — the session layer adds
durable structure around turns the chat path already knows how to run.

Two kinds ship: **General** and **Research** (which adds the read-only knowledge-base tools).
`Development` is reserved and the store refuses it. `WorkSessions:Enabled` ships `true` in
`XE-Local-AI-Engine.Client/appsettings.json` (the compiled-in property default is `false`, which only a
host binding a configuration source without the key ever sees) and gates *behaviour* in the supervisor
and in every tool handler — never registration, so a disabled node answers `404` from request-path
middleware ahead of authentication instead of 500-ing out of an empty container.

### 5.1 One step

`WorkSessionExecutionSupervisor` (hosted service + singleton) drives
`INodeChatStreamService.SendMessageAsync` and drains the returned stream. It does **not** call
`IInvocationRunner.RunAsync`: the runner persists nothing into a conversation — the message rows, the
ordered parts, the pump's terminalization, the resume registry a reloading browser re-attaches through,
and the approval/question lifecycle all live in the send path.

The per-step event order, why stops never cancel the enumeration, and the per-scope store writes are on the reference page. See [the reference page](reference/04-work-sessions.md#51-one-step).

### 5.2 The node has one invocation slot

`MaxConcurrentSessions` (default `1`) is an **admission cap, not concurrency**.
`WorkerEventDispatcher` holds a `SemaphoreSlim(1, 1)` that *every* invocation takes, so a second
admitted session buys queue depth, not parallelism — and **a running step delays the operator's own
chat turn, every scheduled run and every benchmark until it finishes**. That is a node-wide behavioural
consequence of shipping work sessions, not a page-local feature.

`MaxParkedSeconds` (default 300) is what bounds its worst case: a step that parks on an approval nobody
answers is cancelled, the session is checkpointed and paused, and the unanswered prompt is recorded as
an `OpenQuestion` finding so the next step re-asks it. The park itself is in-memory and survives neither
the timeout nor a restart; the finding is what makes the question durable. The checkpoint commits
**before** the `Paused` status: a crash in that window reconciles to `Interrupted` off a valid
checkpoint, where status-first would resume from a stale state block.

Each park is additionally capped one second below `ToolApprovalCoordinator.PendingToolCallAge`, the same effective
startup snapshot (including stored Node Settings) that actually bounds the human wait. A registered approval's
elapsed age is subtracted too; questions use their stream producer timestamp because they have no registry row.
A delayed/reconciled event cannot restart the wait lifetime. Changing settings after
startup does not make the supervisor and approval coordinator disagree; both use the coordinator's snapshot.

#### A dropped park event

This covers how the supervisor re-arms a park whose `ApprovalRequested` or `QuestionRequested` event the bounded sink dropped. See [the reference page](reference/04-work-sessions.md#a-dropped-park-event).

### 5.3 The state block

The step prompt carries only state, rebuilt from the database every step: the objective, the current
task, the open tasks, the recent non-superseded findings, the artifact names, and the last checkpoint's
synopsis. Rebuilding is load-bearing — a tool-only assistant turn is dropped from later context
entirely (the send path keeps only completed, non-empty messages), and older history is bounded by
compaction.

Everything agent-authored in the block sits inside **one `UntrustedContentFraming` fence**: task titles
and details, finding text and `sourceRef`, artifact names, the synopsis. All of it has derived
provenance and may be verbatim knowledge-base or MCP output. The objective stays outside the fence — it
is the operator's own text and the one instruction in the block meant to be followed.

#### The transcript bound at the step boundary

This covers how the send path bounds the replayed transcript of earlier steps at the step boundary. See [the reference page](reference/04-work-sessions.md#the-transcript-bound-at-the-step-boundary).

### 5.4 The four state tools

`update_work_plan`, `record_finding`, `save_artifact` and `complete_work_session` are
`IClientLocalToolHandler`s, all `ToolCategory.WriteExecute` with `RequiresApproval = false`. They are
held out of the whole chat offer and appended only in `GetOfferedToolsForProfile[Async]`, beside
`spawn_subagent` — the same profile-opt-in seam (**HIGH-1**: registering a handler in DI surfaces it in
the resolution seam only; without the offer merge the seeded personas intersect to an empty tool set).

Each state tool, its gates and its limits are on the reference page. See [the reference page](reference/04-work-sessions.md#54-the-four-state-tools).

### 5.5 Checkpoints, and what a repoint may not do

`WorkSessionCheckpointComposer` writes the structured state (current task, open task ids, key finding
ids — decisions and open questions first) plus the prose synopsis from the **existing**
`IConversationCompactionService`. That one call both bounds the owned conversation's raw history and
produces the summary, so no new summarizer seam exists. Every compaction no-op is non-fatal and the
summary is `string?` end to end: a node with no local chat model produces none, and a placeholder would
be a lie a resumed session reads as fact.

What a checkpoint stores, how resume uses it and what a repoint may not do are on the reference page. See [the reference page](reference/04-work-sessions.md#55-checkpoints-and-what-a-repoint-may-not-do).

### 5.6 Settings

This covers the `WorkSessions:*` settings, their defaults and the node settings they seed. See [the reference page](reference/04-work-sessions.md#56-settings).

### 5.7 The per-step consumption record

Every `StepEnded` / `StepFailed` row carries `WorkSessionStepConsumptionDetail` — counts plus a bounded set of tool
**names**, never a prompt, model output, tool argument or tool result — so the per-step provider-call cap can be sized
from what steps actually consume. It is written for *every* such step and not only the clipped ones: a record that
exists only when a bound trips measures the bound rather than the work.

The fields of `WorkSessionStepConsumptionDetail` and how to read them are on the reference page. See [the reference page](reference/04-work-sessions.md#57-the-per-step-consumption-record).

---

## Key invariants for maintainers

- **MAF stays behind interfaces.** `Microsoft.Agents.AI.Workflows` types live only inside AI.Agent
  runners and sessions — today `IOrchestrationRunSession` is the sole holder — and the application layer
  never references them. [Graph Workflows](21-graph-workflows.md) are not an exception to this: they route
  on their own state machine in `Client.Application` and reference no MAF workflow type at all.
- **One decorated pipeline.** Never bypass `DecorateChatClientPipeline`; tool observability + automatic
  invocation must wrap every send. Re-apply it after swapping the base client in tests.
- **Offer is never widened.** Only the seeded Default Assistant gets the full offer; all other agents
  are intersected to `AllowedToolNames`; `spawn_subagent` is opt-in via profile only.
- **Approval is tighten-only and uncategorized tools fail closed.** Apply the node policy after offer
  projection on every path; never let a node override clear a catalog-required approval.
- **Usage summaries are metadata-only estimates.** Preserve the retained token ledger and provider/model
  attribution without adding prompts, responses, tool arguments, or tool results.
- **Knowledge tools are node-local by default.** The read-only knowledge-base tools
  (`search_knowledge_base`, `read_document`, `read_surrounding_chunks`) are offered only to node-local
  models; a cloud model (Codex / Azure Foundry) is withheld them unless the operator sets
  `KnowledgeBase:AllowCloudModelAccess=true`. The gate keys on the **effective model** (after any
  agent/profile pin), classified through the shared `IModelCapabilityResolver` — so a cloud-pinned
  agent, orchestration participant, or spawned sub-agent is withheld the knowledge tools even on a
  local-active turn, closing the pin-bypass. Node-local document/chunk/query content is therefore not
  handed to a cloud provider through a tool call. The coder workspace file tools
  (`list_files`/`read_file`/`search_text`) and conversation attachments are gated the **same way**: for a
  cloud effective model without the opt-in, the file tools are withheld from the offer, attachments are
  neither staged nor inlined, and the user gets a visible turn notice naming the effective model. The
  opt-in `AllowCloudModelAccess` covers knowledge tools, file tools, and attachments. It is a node setting
  (**Node Settings → Privacy**), read per turn; `KnowledgeBase:AllowCloudModelAccess` only seeds it. It is one of
  five **Cloud models** switches there; the others open unattended runs, web tools, MCP tools and sub-agent spawn
  for a cloud model, and none opens `run_python` or `run_in_agent_home` ([Security & Privacy](12-security-and-privacy.md)).
  Attachment content that does reach a model is fenced as untrusted data with a server-secret-derived
  nonce (client cannot forge the fence). See [Knowledge Base](15-knowledge-base.md) and [Security & Privacy](12-security-and-privacy.md).
- **Privacy-sensitive ops are node-local only.** Playbook analysis (P3), the eval gate (P4), and memory
  extraction all run on node-local models — never cloud. See [Security & Privacy](12-security-and-privacy.md).
- **Spawn is bounded.** Depth cap (child omits the tool), per-root fan-out lease, a cloud-spawn
  wallet cap, and the parent trust guard (`AllowCloudModelSubAgents`) are all enforced in `SubAgentSpawnService`.
- **A work session's state block is rebuilt from the database every step, and fenced.** Never source it
  from surviving conversation history, and never emit an agent-authored string outside the
  `UntrustedContentFraming` fence. The session id reaches a state tool only through
  `AgentRunConversationContext`, never through a tool argument.
- **Imported skill content is untrusted, and it is fenced at one place.** `AgentDefinitionResolver`
  wraps an `Origin == Imported` skill's body and every resource through `UntrustedContentFraming`
  before either invocation or sub-agent construction sees it; a new skill-resolution path that bypasses
  `ProjectSkill` reopens the unfenced-instruction hole §4.2 closed. `run_skill_script`'s approval gate is
  never disabled, on any path, including the sub-agent waiver (§4.4) — it is the one skill tool that
  could execute something.

---

## Related pages

- [Architecture Overview](01-architecture-overview.md)
- [Project Layout](02-project-layout.md)
- [Local Runtime & Providers](03-local-runtime-and-providers.md)
- [Chat](05-chat.md)
- [Scheduler](06-scheduler.md)
- [Data & Persistence](08-data-and-persistence.md)
- [API & Hubs](09-api-and-hubs.md)
- [React Client](10-react-client.md)
- [Security & Privacy](12-security-and-privacy.md)
- [Testing & Validation](13-testing-and-validation.md)

### Reference pages

- [AI.Agent runtime](reference/04-ai-agent-runtime.md) — §1 lookup material: pipeline hops, tool relevance, provider budgeting, tool registries, invocation and orchestration details.
- [Application services](reference/04-application-services.md) — §2 lookup material: service areas, agent resolution, the AgentHome contract, capacity and spawn, turn budget, coder and memory worker.
- [Agent Skills](reference/04-agent-skills.md) — §4 lookup material: data model, resolution, runtime tools, import pipeline and approval scoping.
- [Work Sessions](reference/04-work-sessions.md) — §5 lookup material: step mechanics, state tools, checkpoints, settings and the consumption record.
