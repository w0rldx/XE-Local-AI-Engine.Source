# Agent Mode & the AI Agent Runtime

> Last reviewed: 2026-07-24 · Code-grounded.

Agent Mode is XE Local AI Engine's governed agentic layer. It is split across two assemblies:
`XE-Local-AI-Engine.AI.Agent` owns the Microsoft Agent Framework (MAF) / `Microsoft.Extensions.AI`
(MEAI) wiring — agent construction, the tool-execution pipeline, single-agent invocation, and
multi-agent handoff orchestration — while `XE-Local-AI-Engine.Client.Application/Services/*` owns the
*application* decisions: agent definitions, the AgentHome write-back loop, the governed Playbook
lifecycle (manual → feedback → analysis → eval gate → monitoring/retrieval), adaptive memory, capacity
gating, sub-agent spawn, and read-only Coder mode. The seam between them is deliberate: all
`Microsoft.Agents.AI.Workflows` types stay confined behind interfaces so the application layer never
references MAF workflow primitives directly.

---

## 1. The AI.Agent runtime (`XE-Local-AI-Engine.AI.Agent`)

### 1.1 `AddLocalAiAgentRuntime` — the composition root

`AgentServiceCollectionExtensions.AddLocalAiAgentRuntime` is the single registration entry point
(`XE-Local-AI-Engine.AI.Agent/DependencyInjection/AgentServiceCollectionExtensions.cs:37`). It does
five things, in order:

1. **Binds + validates options** — `LocalChatAgentOptions`, `InvocationAgentOptions`,
   `OrchestrationAgentOptions`, each `Bind` → `ValidateDataAnnotations` → `ValidateOnStart`, with a
   dedicated `IValidateOptions<>` validator (`Configuration/Validation/*Validator.cs`). The root config
   key is `"Agent"` (`AgentRuntimeOptions.Section`).
2. **Decorates the `IChatClient` pipeline** via `DecorateChatClientPipeline` (see §1.2). The host
   **must** register a base `IChatClient` *before* calling this method — the decorator wraps it.
3. **Registers the three tool registries** as singletons (see §1.3):
   `IAgentToolRegistry → LocalAgentToolRegistry`, `IClientLocalToolRegistry → ClientLocalToolRegistry`,
   `IMcpToolRegistry → McpToolRegistry`.
4. **Registers the agent factories** — `IInvocationAgentFactory → InvocationAgentFactory` (single
   agent) and `IOrchestrationAgentFactory → OrchestrationAgentFactory` (multi-agent handoff).
5. **Registers the gated runners** — `IPlaybookEvalAgentRunner → MafPlaybookEvalAgentRunner` (golden
   eval) and `IPreviewWorkflowRunner → PreviewWorkflowRunner` (Open Canvas preview).

### 1.2 The chat-client decorator pipeline

`DecorateChatClientPipeline` (`AgentServiceCollectionExtensions.cs:95`) decorates the registered
`IChatClient` so that **every** code path — local chat, platform invocations, ClientLocal tools, MCP
tools — shares one execution pipeline:

```
base IChatClient
   └─ ToolInvocationObservabilityChatClient   // emits tool-call lifecycle events
        └─ UseFunctionInvocation (FICC)        // MEAI FunctionInvokingChatClient: auto-executes tools
```

`ToolInvocationObservabilityChatClient` lives at
`XE-Local-AI-Engine.AI.Agent/Chat/ToolInvocationObservabilityChatClient.cs`. The decoration is exposed
as a **public** method specifically so test harnesses that swap the base client for a fake (e.g.
FakeOllama) can re-apply the full pipeline after their `RemoveAll`/`AddSingleton`.

> **Seam to respect:** because the base client is already FICC-wrapped, `ChatClientAgent`'s constructor
> detects the existing `FunctionInvokingChatClient` and registers the agent's own tools as
> `AdditionalTools` rather than re-wrapping. This is what lets the handoff builder inject bodyless
> `handoff_to_*` declarations that the outer FICC leaves unserviced (the workflow executor routes them).

### 1.3 Tool registries — three sources, one offer

All three resolve to `Microsoft.Extensions.AI.AITool` and are model-agnostic so the agent factories
treat them uniformly.

| Registry | Interface / impl | Source of tools | Notes |
|---|---|---|---|
| Built-in catalog | `IAgentToolRegistry` / `Tools/Implementation/LocalAgentToolRegistry.cs` | `AIFunctionFactory.Create` over in-process methods (`GetCurrentTime`, `Calculate`) | Descriptors are derived **from** the generated `AIFunction.JsonSchema` so the offered contract can't drift from what executes. Their catalog approval default is false, but the effective node policy can tighten it. |
| ClientLocal (server-driven) | `IClientLocalToolRegistry` / `Tools/Implementation/ClientLocalToolRegistry.cs` | `IClientLocalToolHandler` implementations registered by the application layer (e.g. `run_in_agent_home`, `spawn_subagent`) | In-process handlers, **not** SignalR. The registry holds the handler-backed tools; the worker app layer registers the handlers. |
| MCP | `IMcpToolRegistry` / `Tools/Implementation/McpToolRegistry.cs` | An immutable `AITool` snapshot pushed in by the MCP connection manager as servers connect | The registry is MCP-agnostic (only holds `AITool`); the application layer owns the MCP client lifecycle. See [Chat](05-chat.md) and [API & Hubs](09-api-and-hubs.md). |

`InvocationToolResolver` (`Tools/InvocationToolResolver.cs`) merges the three registries into the
concrete tool list passed to each agent; `InvocationToolBridge` adapts metadata tool functions
(`Tools/Implementation/MetadataToolFunction.cs`).

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

The same policy is applied to the seeded Default Assistant, bound agents, orchestration participants,
and regeneration. Approval decisions are recorded through `IToolApprovalAuditRecorder` as
content-free operational metadata; arguments and tool results do not enter that audit record.
Unattended `run-agent` scheduler jobs have no human approval round trip and therefore remove every
approval-required tool from their offer before execution. See [Scheduler](06-scheduler.md).

### 1.4 Single-agent invocation — `InvocationAgentFactory`

`InvocationAgentFactory.CreateAsync` (`Invocation/Implementation/InvocationAgentFactory.cs:71`) builds
an `InvocationAgentContext` from an `InvocationAgentDefinition`. It:

- resolves executable tools from the three registries (`ResolveExecutableTools`),
- builds the `ChatClientAgent` (`BuildAgent`) with resolved skills (MAF progressive disclosure),
- builds seed messages (`BuildSeedMessages` — a leading `System(instructions)` message), and
- assembles a `ChatOptions` carrying `ModelId` and a reasoning `think` option computed from the
  model's thinking capability.

> **Reasoning gotcha (documented in-code, lines ~84–125):** for a **thinking-capable** model the
> requested effort is honored (`think: false|low|medium|high`); for a **non-thinking** model that has
> reasoning *requested* the `think` field is **omitted entirely** (Ollama returns HTTP 400 for an
> unknown think level, but omission lets chat-template-baked reasoning through); only "none"/unspecified
> sends `think: false`. A Codex side-channel key carries the raw effort for the Responses boundary.
> Per-send sampling overrides (`ApplySamplingOptions`) are null/no-op by default to keep the mode-off
> path byte-identical.

### 1.5 Multi-agent handoff orchestration — `OrchestrationAgentFactory` + `OrchestrationRunSession`

`OrchestrationAgentFactory.CreateAsync` (`Invocation/Orchestration/Implementation/OrchestrationAgentFactory.cs:42`)
builds **one `ChatClientAgent` per participant** over the shared decorated `IChatClient` and the same
tool registries, then assembles a MAF handoff `Workflow`:

- `AgentWorkflowBuilder.CreateHandoffBuilderWith(triageAgent)` (a deliberately-adopted `[Experimental]`
  API, `#pragma warning disable MAAIW001`);
- **no explicit `OrchestrationEdge`s ⇒ fully-connected mesh** (every agent can hand off to every other);
  explicit edges constrain routing. An agent's `Name`/`Description` drive routing — the target's
  Description is the routing reason.
- The workflow is driven by `InProcessExecution.RunStreamingAsync`; a `TurnToken` is sent to actually
  start the conversation (HandoffStart only *accumulates* the seed without it).

The factory returns an **`IOrchestrationRunSession`** (`Invocation/Orchestration/IOrchestrationRunSession.cs`)
— the boundary that confines every `Microsoft.Agents.AI.Workflows` type. `OrchestrationRunSession`
(`.../OrchestrationRunSession.cs`) exposes:

- `WatchAsync` — drains the `StreamingRun`, maps each `WorkflowEvent` to an `OrchestrationUpdate`
  (streaming update, approval request, terminal, or failure), with a **per-quiescence idle timeout**
  that is **suspended while a tool-approval is pending** (the consumer may block on a human decision
  for minutes) and reset after each productive event;
- `RespondToApprovalAsync` — resolves a pending `ToolApprovalRequestContent`, sends the
  `ExternalResponse`, and restarts the idle clock.

### 1.6 Other AI.Agent runners

- **`MafPlaybookEvalAgentRunner`** (`Eval/Implementation/MafPlaybookEvalAgentRunner.cs`) — the golden
  eval gate's executor. Builds a `ChatClientAgent` over a **caller-supplied node-local** `IChatClient`
  with an **empty tool set** and runs it **threadless** (`session: null`). It mirrors the real worker
  loop's prompt assembly so the eval measures the injected prompt's effect, not tool behaviour. The
  client is owned by the caller and intentionally not disposed.
- **`PreviewWorkflowRunner`** (`PreviewWorkflows/Implementation/PreviewWorkflowRunner.cs`) — the Open
  Canvas (Preview) visual-workflow runner over a raw MAF `WorkflowBuilder`; again confines all
  `Workflows` types to the runner.

---

## 2. Application services (`Client.Application/Services/*`)

These services own the product behaviour and are wired in the Client host. They depend only on the
AI.Agent interfaces, provider seams (`ILocalModelProvider`, `IChatClient`, `IEmbeddingGenerator` — see
[Local Runtime & Providers](03-local-runtime-and-providers.md)), and persistence stores (see
[Data & Persistence](08-data-and-persistence.md)).

| Area | Key types | Responsibility |
|---|---|---|
| **Agents** | `AgentDefinitionService`, `AgentDefinitionResolver`, `AgentSkillService`, `AgentTemplateCatalog`/`Import`, `DefaultAgentSeeder`, `CoderAgentSeeder`, `OrchestrationResolver` | CRUD of agent definitions; per-turn resolution into a `ResolvedAgentRuntime` (prompt + gated tool offer + pinned model + skills + flags). |
| **AgentHome** | `AgentHomeService`, `AgentHomeManifestService`, `AgentHomeWorkspaceService`, `AgentHomePatchService`, `NodePatchApplyService`, `MemoryProposalSecretScanner`, `IConversationSandboxStager`, `Tools/RunInAgentHomeToolHandler` | The write-back loop: sandboxed git workspace, patch apply, memory proposals, conversation-attachment staging. |
| **Analysis** | `PlaybookAnalysisService`, `OllamaPlaybookAnalysisAgent`, `IPlaybookAnalysisAgent` | Playbook analysis → **Suggested** staging (node-local model only). |
| **Eval** | (uses AI.Agent `IPlaybookEvalAgentRunner`) + `PlaybookActionService` gate logic | Golden-conversation eval gate (Suggested → Enabled). |
| **Insights** | `FeedbackInsightsService` / `IFeedbackInsightsService` | Read-only per-agent feedback aggregation (n≥3 threshold). |
| **Monitoring** | `PlaybookMonitorService` / `IPlaybookMonitorService` | Cohort monitoring of enabled playbook actions. |
| **Memory** | `MemoryExtractionService`, `MemoryExtractionDispatcher`, `OllamaMemoryExtractionAgent` | Adaptive agent memory: post-run node-local extraction → Suggested/Extracted, token-budgeted. |
| **Approval/audit** | `NodeToolApprovalPolicy`, `IToolApprovalAuditRecorder`, `ToolApprovalAuditRecorder` | Tighten-only category/name approval policy plus content-free approval-decision telemetry. |
| **Usage** | `IAgentExecutionLogStore`, `IUsageRateResolver`, `GetAgentUsageSummaryEndpoint` | Retained token-usage aggregation and operator-configured USD cost estimates; no message content. |
| **Capacity** | `CapacityService`, `ModelFootprintProvider`, `PendingFootprintLedger`, `SpawnSerializer`, `SpawnContext` | Capacity gate for spawning a model process (Allow / QueueSameModel / reject). |
| **Capacity/Sub-agent** | `SubAgentSpawnService` + `Tools/SpawnSubAgentToolHandler` | The `spawn_subagent` tool: capacity-gated, depth- and fan-out-capped child agents. |
| **Coder** | `CoderWorkspaceReader`, `Tools/{ListFiles,ReadFile,SearchText}ToolHandler`, `WorkspacePathGuard` | Read-only "coder mode": list/read/search files behind a path guard. |

### 2.1 Per-message agent selection & attribution

`AgentDefinitionResolver.ResolveAsync`
(`Services/Agents/Implementation/AgentDefinitionResolver.cs:36`) is the per-turn entry point:

- **Unbound conversation ⇒ `null`** → the default persona (embedded prompt, full offer, version 1).
- A binding to a **deleted** definition degrades to the default persona (logged) rather than failing
  the turn — there is no FK on the conversation column by design.
- The definition's **pinned `ModelProfile`** (when set) is the model the turn actually runs on, so the
  tool offer is gated by it, keeping capability-gating and runtime model consistent.

**Tool-offer security invariant** (`ProjectAllowedTools`): only the seeded **"Default Assistant"**
(mode-off persona, identified by forge-proof `Source=Seeded` + `SeedSlug`) receives the *full*
capability-gated offer. **Every other definition is intersected** down to its `AllowedToolNames` — a
selected agent's offer is never widened beyond its allowed set. `spawn_subagent` is opt-in only (it
lives in the *profile* pool, not the default offer), and a non-tool-capable model gets an **empty**
offer before per-name gating. See [Chat](05-chat.md) for how the selected agent surfaces as
per-message attribution.

### 2.2 The AgentHome write-back loop

`AgentHomeService.RunLifecycleAsync` (`Services/AgentHome/Implementation/AgentHomeService.cs:90`)
drives a sandboxed workspace lifecycle through `ISandboxRuntimeProvider` (the process-jail provider —
Docker is removed, see [Local Runtime & Providers](03-local-runtime-and-providers.md)). It resolves
selected folders into the sandbox (`ISelectedFolderResolver` via a short-lived scope, since the service
is a singleton and `NodeChatDbContext` isn't thread-safe), runs the agent, applies patches
(`NodePatchApplyService` with `O_NOFOLLOW`/byte-recheck guards), and stages **memory proposals** that
are secret-scanned (`MemoryProposalSecretScanner`) before they can be exported. The
`run_in_agent_home` tool is a ClientLocal handler (`Tools/Implementation/RunInAgentHomeToolHandler.cs`).

**Conversation-attachment staging** (`IConversationSandboxStager`,
`Services/AgentHome/IConversationSandboxStager.cs`). `AgentHomeService` also implements this narrow public
seam (`AgentHomeService.cs:28`, `PrepareConversationAttachmentsAsync` at `:233`) so the public
`NodeChatStreamService` can stage a chat conversation's uploaded attachments into the per-turn sandbox
without an inconsistent-accessibility error. When an agent-mode turn offers file tools, the stream
service re-stages the owner-node sandbox to hold **only** that conversation's extracted attachments under
the workspace `attachments/` alias (the sandbox is recreated first, so there is no cross-conversation
residue), then hands the model the staged workspace-relative paths so its `list_files` / `read_file` /
`search_text` tools read them directly. Because the same shared singleton implements both interfaces, the
re-stage shares the run-level single-flight guard with `run_in_agent_home`. It is a no-op (empty list)
when Agent Mode is disabled or the conversation has no extracted files. The chat-side wiring — and the
contrasting plain-chat path that *inlines* extracted text instead of staging — is in [Chat](05-chat.md).

### 2.3 Capacity gate & sub-agent spawn

`SubAgentSpawnService.SpawnAsync` (`Services/Capacity/SubAgentSpawnService.cs:87`) implements the
`spawn_subagent` tool with layered safety:

1. **Validation** — non-blank task and exactly one binding.
2. **Runtime depth guard** — a child runs at `SpawnContext.Current.Depth >= 1` and its tool set already
   omits `spawn_subagent`, so recursion is structurally impossible; a missing context defaults SAFE
   (rejected).
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

---

## 3. The governed Playbook lifecycle (P1–P5)

A Playbook is a set of per-agent "actions" (learned instructions) that are folded into the agent's
prompt. Their promotion is governed so that nothing reaches the live prompt without passing the gates:

```
 P1 manual        operator authors an action  ─────────────┐
 P2 feedback      👍/👎 aggregated per agent (n≥3)          │  Insights / FeedbackInsightsService
 P3 analysis      node-local model proposes  ──► Suggested  │  Analysis / OllamaPlaybookAnalysisAgent
 P4 eval gate     golden conversations re-run ──► Enabled   │  Eval / MafPlaybookEvalAgentRunner
 P5 monitoring +  cohort monitoring + relevance retrieval   │  Monitoring + PlaybookRetrievalSelector
    retrieval     (top-k injected, cap MaxEnabledActions=20)─┘
```

- **P1 Manual** — operator authors actions; `PlaybookActionService` owns the state machine.
- **P2 Feedback** — `FeedbackInsightsService` aggregates 👍/👎 per agent (read-only, n≥3 threshold).
- **P3 Analysis** — `PlaybookAnalysisService` + `OllamaPlaybookAnalysisAgent` stage **Suggested**
  actions. **Privacy invariant: this runs on a node-local model only** (no cloud), so user
  conversation content never leaves the box for analysis.
- **P4 Eval gate** — golden conversations are **re-run through the real MAF loop** node-local
  (`MafPlaybookEvalAgentRunner`) to gate **Suggested → Enabled**; golden conversations are stored
  encrypted.
- **P5 Monitoring + retrieval** — `PlaybookMonitorService` does cohort monitoring; at inject time the
  prompt composer applies **relevance retrieval**.

### 3.1 Relevance retrieval at prompt-compose time

In `AgentDefinitionResolver.ComposePromptAsync` (line ~115): when the playbook is **disabled** the
base instructions flow through unchanged (keeping the runtime config hash byte-identical). When
enabled, `PlaybookRetrievalSelector.SelectAsync` chooses what to inject:

- **At/below `RetrievalThreshold`, or a blank query** → the full static prepend (byte-identical to the
  pre-retrieval path).
- **Above the threshold with a non-blank query** → only the top-k most relevant actions, ranked by
  `IPlaybookRetrievalRanker`. The default ranker is **`EmbeddingPlaybookRetrievalRanker`** (cosine over
  embeddings via `ILocalModelProvider.CreateEmbeddingGenerator`), with **`LexicalPlaybookRetrievalRanker`**
  as a fallback. `PlaybookPromptComposer.Compose` then folds the selection into the prompt. Token
  budgets (`MaxInjectedMemoryTokens`, `MaxInjectedFailureMemoryTokens`) bound the injection. See
  [Local Runtime & Providers](03-local-runtime-and-providers.md) and [Data & Persistence](08-data-and-persistence.md)
  for embeddings and storage.

---

## Key invariants for maintainers

- **MAF stays behind interfaces.** `Microsoft.Agents.AI.Workflows` types live only inside AI.Agent
  runners/sessions (`IOrchestrationRunSession`, `IPreviewWorkflowRunner`); the application layer never
  references them.
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
  single opt-in `KnowledgeBase:AllowCloudModelAccess` covers knowledge tools, file tools, and attachments.
  Attachment content that does reach a model is fenced as untrusted data with a server-secret-derived
  nonce (client cannot forge the fence). See [Knowledge Base](15-knowledge-base.md) and [Security & Privacy](12-security-and-privacy.md).
- **Privacy-sensitive ops are node-local only.** Playbook analysis (P3), the eval gate (P4), and memory
  extraction all run on node-local models — never cloud. See [Security & Privacy](12-security-and-privacy.md).
- **Spawn is bounded.** Depth cap (child omits the tool), per-root fan-out lease, and a cloud-spawn
  wallet cap are all enforced in `SubAgentSpawnService`.

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
