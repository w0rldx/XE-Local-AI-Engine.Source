# Agent Mode & the AI Agent Runtime

> Reviewed: 2026-09-15 · Code-grounded.

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

`AgentServiceCollectionExtensions.AddLocalAiAgentRuntime` is the single registration entry point
(`XE-Local-AI-Engine.AI.Agent/DependencyInjection/AgentServiceCollectionExtensions.cs`). It does
five things, in order:

1. **Binds + validates options** — `LocalChatAgentOptions`, `InvocationAgentOptions`,
   `OrchestrationAgentOptions`, each `Bind` → `ValidateDataAnnotations` → `ValidateOnStart`, with a
   dedicated `IValidateOptions<>` validator (`Configuration/Validation/*Validator.cs`). The root config
   key is `"Agent"` (`AgentRuntimeOptions.Section`).
2. **Decorates the `IChatClient` pipeline** via `DecorateChatClientPipeline` (see §1.2). The host
   **must** register a base `IChatClient` *before* calling this method — the decorator wraps it.
3. **Registers the three in-memory tool registries** as singletons (see §1.3):
   `IAgentToolRegistry → LocalAgentToolRegistry`, `IClientLocalToolRegistry → ClientLocalToolRegistry`,
   `IMcpToolRegistry → McpToolRegistry`.
4. **Registers the agent factories** — `IInvocationAgentFactory → InvocationAgentFactory` (single
   agent) and `IOrchestrationAgentFactory → OrchestrationAgentFactory` (multi-agent handoff).
5. **Registers the gated runner** — `IPlaybookEvalAgentRunner → MafPlaybookEvalAgentRunner` (golden
   eval).

### 1.2 The chat-client decorator pipeline

`DecorateChatClientPipeline` (`AgentServiceCollectionExtensions.cs`) decorates the registered
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

### 1.3 Tool registries and catalog — four sources, one offer

All four resolve to `Microsoft.Extensions.AI.AITool` and are model-agnostic so the agent factories
treat them uniformly.

| Registry | Interface / impl | Source of tools | Notes |
|---|---|---|---|
| Built-in catalog | `IAgentToolRegistry` / `Tools/Implementation/LocalAgentToolRegistry.cs` | `AIFunctionFactory.Create` over in-process methods (`GetCurrentTime`, `Calculate`) | Descriptors are derived **from** the generated `AIFunction.JsonSchema` so the offered contract can't drift from what executes. Their catalog approval default is false, but the effective node policy can tighten it. |
| ClientLocal (server-driven) | `IClientLocalToolRegistry` / `Tools/Implementation/ClientLocalToolRegistry.cs` | `IClientLocalToolHandler` implementations registered by the application layer (e.g. `run_in_agent_home`, `spawn_subagent`) | In-process handlers, **not** SignalR. The registry holds the handler-backed tools; the worker app layer registers the handlers. |
| MCP | `IMcpToolRegistry` / `Tools/Implementation/McpToolRegistry.cs` | An immutable `AITool` snapshot pushed in by the MCP connection manager as servers connect | The registry is MCP-agnostic (only holds `AITool`); the application layer owns the MCP client lifecycle. See [Chat](05-chat.md) and [API & Hubs](09-api-and-hubs.md). |
| Custom Tools | `ICustomToolCatalog` / `Services/CustomTools/Implementation/CustomToolCatalog.cs` | Enabled, acknowledged `custom__*` definitions read live from SQLite on every offer/resolve | HTTP-fetch and host-command tools. The node kill-switch defaults off, each tool must be assigned to the agent, and every executable is unconditionally wrapped in `ApprovalRequiredAIFunction`. |

`InvocationToolResolver` (`Tools/InvocationToolResolver.cs`) merges the three registries plus the asynchronous custom-tool catalog into the
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

#### Custom Tools execution boundary

Custom Tools are operator-authored node-local definitions with two kinds: `HttpFetch` and `Command`, each either
`Fixed` or `Parameterized`. `CustomToolService` owns CRUD validation and masks secret header/environment values on
reads; `CustomToolCatalog` reads the encrypted store live so an edit affects the next turn without a restart. The
model-facing schema is compiled by `CustomToolSchemaCompiler`; fixed tools expose no model parameters, while
parameterized tools reject undeclared properties and substitute only declared, type-checked placeholders.

Execution is guarded below the catalog. `HttpFetchExecutor` uses `CustomToolSsrfGuard`, a proxy-disabled dedicated
client, address pinning, and no redirects. `HostProcessExecutor` uses `HostExecutableGuard`, a scrubbed environment,
timeout, tree-kill, output cap, and a process-wide concurrency limiter. Both use the shared argument-repair and result-
budget wrappers, and approval remains an unconditional outer wrapper. The offer therefore requires **all** of: the
node setting `CustomToolsEnabled` (default `false`), an enabled + acknowledged stored definition, model tool capability,
and the agent's `AllowedToolNames`. A `Fixed` tool may reuse an explicit conversation-scoped approval, keyed to the
tool version so any edit re-prompts. A `Parameterized` tool is never memoized: every model-selected argument set prompts
again. Scheduler, spawned-child, and delegate-scope inbound-MCP paths strip approval-required tools
before execution, so they cannot run a Custom Tool or reuse a session approval. A trusted
agentic-scope root inbound run is the deliberate exception: it may invoke an approval-required tool
only through ADR 0006's strict metadata-only audit-before-auto-approval path. Spawned children do not
inherit that elevation.

The node setting `ToolRelevanceEnabled` (default `false`, read live per turn) narrows a large tool offer to an
always-on core plus a relevance-ranked fill, and the model recovers anything held back by calling `list_tools`; the
per-agent `DisableToolRelevanceFilter` opts a single agent out even when the node switch is on. It is a context
budget, never an authorisation boundary: hiding a tool neither widens nor narrows what an agent may call.

`InvocationRunner` reads that switch live per turn rather than caching it in a field, so an operator save applies to
the next turn without a restart, and opens the `ToolRelevanceScope` **before the agent is built** — an `AsyncLocal`
written in a callee never reaches its caller, and the send-time relevance hop runs several awaited frames below the
runner. The read sits **inside** the turn's `try`, because a throw above it would fail the turn with no failure ever
reported, and binds to the invocation's own cancellation token (what an operator Cancel or the turn watchdog
cancels), not the caller's. The core set is read **only when the switch is on**: `GetCoreToolNames` reads the MCP
registry live and, with any server connected, allocates a fresh catalog list plus a set per turn, which on the
shipped default would build a set nothing reads on the hot path of every chat turn. With the switch off the hop is a
reference-equality pass-through and `list_tools` is never appended.

The same policy is applied to the seeded Default Assistant, bound agents, orchestration participants,
and regeneration. Approval decisions are recorded through `IToolApprovalAuditRecorder` as
content-free operational metadata; arguments and tool results do not enter that audit record.
Unattended `run-agent` scheduler jobs have no human approval round trip and therefore remove every
approval-required tool from their offer before execution. See [Scheduler](06-scheduler.md).

### 1.4 Single-agent invocation — `InvocationAgentFactory`

`InvocationAgentFactory.CreateAsync` (`Invocation/Implementation/InvocationAgentFactory.cs`) builds
an `InvocationAgentContext` from an `InvocationAgentDefinition`. It:

- resolves executable tools from the registries and custom-tool catalog (`ResolveExecutableTools`),
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

### 1.5 Multi-agent handoff orchestration — `OrchestrationAgentFactory` + `OrchestrationRunSession`

`OrchestrationAgentFactory.CreateAsync` (`Invocation/Orchestration/Implementation/OrchestrationAgentFactory.cs`)
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

#### When orchestration does not compile — the degrade notice

A `Kind=Orchestrator` definition does not always produce a mesh. `IOrchestrationResolver` /
`OrchestrationResolver` (`Client.Application/Services/Agents/`) compiles the definition plus its
`OrchestrationTopologyJson` into the spec carried on the runtime package, and returns an
`OrchestrationResolution` whose `Orchestration` is `null` when it cannot. Four of those outcomes carry a typed
`OrchestrationDegradationReason`:

| Reason | Meaning |
|---|---|
| `TopologyInvalid` | `OrchestrationTopologyJson` is missing, empty, or does not parse |
| `ModelNotToolCapable` | the orchestrator's effective model does not advertise (or is not allow-listed for) tool calling |
| `TriageMissing` | the topology's triage participant is missing, deleted, or was dropped as not tool-capable |
| `TooFewCapableParticipants` | fewer than the two capable participants handoff routing needs survived resolution |

In every one of those cases **the turn still runs** — the orchestrator executes as a lone agent on its own prompt
and tools — and the operator is told rather than left to read a server log: `OrchestrationResolution.DegradationNotice`
composes the one sanitized sentence ("Orchestration was not used for this turn: … The agent ran as a single agent
instead.") that **both** the send and the regenerate path emit as a `TurnNoticePayload` with
`TurnNoticeKind.OrchestrationDegraded` and the reason name in `Detail`
(`NodeChatStreamService`, `NodeChatRegenerationService`; rendered by `ChatNoticeRow.tsx`). Composing the sentence on
the resolution — not at each call site — is what keeps the two paths from drifting.

The fifth outcome, `OrchestrationResolution.NotOrchestrated`, is deliberately **silent**: a `Single`-kind agent, an
unbound conversation, or a deleted definition never asked for orchestration, so `ChatTurnResolver.ResolveOrchestrationAsync`
returns it without resolving and no notice is raised. That keeps the overwhelmingly common single-agent path
byte-identical.

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

| Area | Key types | Responsibility |
|---|---|---|
| **Agents** | `AgentDefinitionService`, `AgentDefinitionResolver`, `AgentSkillService`, `ISkillImportService`, `AgentTemplateCatalog`/`Import`, `DefaultAgentSeeder`, `CoderAgentSeeder`, `OrchestrationResolver` | CRUD of agent definitions; per-turn resolution into a `ResolvedAgentRuntime` (prompt + gated tool offer + pinned model + skills + flags); two-phase import of third-party skills (§4.5). |
| **AgentHome** | `AgentHomeService`, `AgentHomeManifestService`, `AgentHomeWorkspaceService`, `AgentHomeGoalExecutor`, `AgentHomePatchService`, `NodePatchApplyService`, `IConversationSandboxStager`, `Tools/RunInAgentHomeToolHandler` | The write-back loop: sandboxed git workspace, bounded inner agent loop, patch export and apply, conversation-attachment staging. |
| **Analysis** | `PlaybookAnalysisService`, `DefaultPlaybookAnalysisAgent`, `IPlaybookAnalysisAgent` | Playbook analysis → **Suggested** staging (node-local model only). |
| **Eval** | (uses AI.Agent `IPlaybookEvalAgentRunner`) + `PlaybookActionService` gate logic | Golden-conversation eval gate (Suggested → Enabled). |
| **Insights** | `FeedbackInsightsService` / `IFeedbackInsightsService` | Read-only per-agent feedback aggregation (n≥3 threshold). |
| **Monitoring** | `PlaybookMonitorService` / `IPlaybookMonitorService` | Cohort monitoring of enabled playbook actions. |
| **Memory** | `MemoryExtractionService`, `MemoryExtractionDispatcher`, `DefaultMemoryExtractionAgent` | Adaptive agent memory: post-run node-local extraction → Suggested/Extracted, token-budgeted. |
| **Approval/audit** | `NodeToolApprovalPolicy`, `IToolApprovalAuditRecorder`, `ToolApprovalAuditRecorder` | Tighten-only category/name approval policy plus content-free approval-decision telemetry. |
| **Usage** | `AgentExecutionLogQueryService` (the endpoint's read door onto `IAgentExecutionLogStore`), `IUsageRateResolver`, `GetAgentUsageSummaryEndpoint` | Retained token-usage aggregation and operator-configured USD cost estimates; no message content. |
| **Capacity** | `CapacityService`, `ModelFootprintProvider`, `PendingFootprintLedger`, `SpawnSerializer`, `SpawnContext` | Capacity gate for spawning a model process (Allow / QueueSameModel / reject). |
| **Capacity/Sub-agent** | `SubAgentSpawnService` + `Tools/SpawnSubAgentToolHandler` | The `spawn_subagent` tool: capacity-gated, depth- and fan-out-capped child agents. |
| **Coder** | `CoderWorkspaceReader`, `Tools/{ListFiles,ReadFile,SearchText}ToolHandler`, `WorkspacePathGuard` | Read-only "coder mode": list/read/search files behind a path guard. |
| **Custom Tools** | `CustomToolService`, `CustomToolCatalog`, `HttpFetchExecutor`, `HostProcessExecutor` | Operator-authored HTTP/command tools; live SQLite catalog, author-time validation, secret masking, execution guards, forced approval. |

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
selected agent's offer is never widened beyond its allowed set. `spawn_subagent`, `run_python` and
`run_in_agent_home` are opt-in only (they live in the *profile* pool, not the default offer), and a
non-tool-capable model gets an **empty** offer before per-name gating. See [Chat](05-chat.md) for how
the selected agent surfaces as per-message attribution.

#### The resolved runtime projection

`ResolvedAgentRuntime` is what the resolver hands back, and its member order is load-bearing. The **first five**
fields map one-to-one onto a `LocalChatRuntimePackageRequest` input, so the existing builder and config-hash plumbing
are reused verbatim. **Everything after them is trailing, defaulted and deliberately outside the config hash** — the
builder reads only the leading five — so adding one changes neither an existing hash nor positional construction:

- `AgentDefinitionId` / `AgentName` — the provenance and display-name snapshot the stream service stamps as
  per-response attribution without a second fetch.
- `Skills` — the enabled, assigned skill set for MAF progressive disclosure. It is **not** folded into
  `ResolvedSystemPrompt`, because bodies load on demand, so the runtime-package builder folds it into the config hash
  separately and threads it to the invocation factory.
- `PlaybookEnabled` / `MemoryExtractionEnabled` — the gates the post-run memory-extraction seam reads without
  re-fetching the definition. Extraction fires only when **both** are true, while retrieval and injection stay gated
  on `PlaybookEnabled` alone, so a retrieval-only agent still injects existing memory but mines no new candidates.
- `Kind` — the definition's execution shape. The resolver has already loaded and decrypted the definition, so
  exposing it lets the chat-turn resolver decide whether to compile an orchestration without a second uncached read
  and AES-GCM decrypt on every send; the common non-orchestrator path skips the reload entirely.
- `DisableToolRelevanceFilter` — the per-agent opt-out from the send-time relevance filter, which narrows only the
  array handed to the provider, never the offer or the prompt.

`DisableToolRelevanceFilter` is also the one member kept **off the wire** (`[JsonIgnore]`). This record is serialized
verbatim into the frozen v1 benchmark runtime snapshot, whose stored bytes are re-hashed to validate
`configurationHash`, so a new member emitting `false` would change the bytes of every already-frozen run and stop each
one replaying with "configuration hash is invalid" (`BenchmarkRuntimeSnapshotV1CompatibilityTests` guards this).
Omitting it is the honest shape as well: `BenchmarkRunExecutor.BuildPrimaryPackage` never threads the flag into the
replayed `RuntimePackage`, so a frozen run always generates under the node-level filter setting whatever the agent
asked for. Nothing else serializes the record — the agent-definition endpoint DTOs carry their own copy of the flag.

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

**How `run_in_agent_home` is offered.** Registering the handler in DI reaches the *resolution* seam only, so the
descriptor is merged into the *offer* seam by `LocalToolOfferProvider` — on `run_python`'s terms, not the coder
tools':

- **Per-agent opt-in.** It lives in the profile pool (`GetOfferedToolsForProfile[Async]`), never in the whole
  chat offer, so a plain/mode-off turn and the seeded Default Assistant are never handed it. An operator must
  list `run_in_agent_home` in an agent definition's `AllowedToolNames` — the agent editor's tool picker can
  offer it because the known-tool catalog lists it ungated by model.
- **Local tool-capable models only.** The `AgentHome:ToolCapableModels` allow-list gate applies (read live per
  offer), and the tool is withheld outright from any model outside the trust boundary — a cloud-hosted, Codex-
  pinned or declared-cloud external id — because `run_commands` / `write_workspace` execute on the operator's
  own machine. That withholding is unconditional, not behind `AllowCloudModelAccess`.
- **Approval always.** The descriptor is `ToolCategory.WriteExecute` with `RequiresApproval: true`, matching the
  handler's hardcoded flag; the node policy can tighten it but never waive it. That is also what makes the
  unattended callers (sub-agent spawn, scheduler, graph workflows, delegate-scope inbound MCP) strip it, since
  each strips every approval-required tool rather than consulting a name list.
- **`AgentHome:Enabled` is enforced at execution, not at the offer**, like the compute and work-session
  kill-switches: the handler re-reads it and refuses before touching anything.
- **Sandbox backend.** A node whose `AgentHome:Sandbox:Provider` is unset resolves the no-op `fake` backend in
  non-Production, which executes nothing; the tool result says so explicitly rather than reporting a clean
  exit 0. Set `AgentHome:Sandbox:Provider=process` (restart-time) to execute for real.
- **Host patch apply is the operator's, and only the operator's.** A run *exports* `changes.patch` under its run
  directory automatically; landing it is a separate, human act. `INodePatchApplyService` is called from the
  `AgentHomePatch` endpoint pair (preview / apply, `NodeOperator`-gated — see
  [API & Hubs](09-api-and-hubs.md)) and from the chat tool-result card's **Review and apply changes** dialog,
  which previews before it offers the button. The model cannot reach it: no `[McpServerTool]` and no
  `IClientLocalToolHandler` names the service, and `HostPatchApplyReachArchitectureTests` fails the build if one
  starts to. The apply also has to echo the hash the preview reported, so the diff the operator read is the diff
  that lands. Two things a run can produce are exported but never landed: a **symbolic link** (`mode 120000`,
  whose content is the link target, so applying it would point a real host link anywhere) and a **nested
  repository** (`mode 160000`), both refused by name in `NodePatchApplyService.ParseBlock` — see
  [Security & Privacy](12-security-and-privacy.md). A path the workspace's `.gitignore` covers is not in the
  patch at all, because staging honours it (below).

**What a run does with the `goal`.** `AgentHomeService.RunAsync` hands it to `IAgentHomeGoalExecutor`
(`Services/AgentHome/Implementation/AgentHomeGoalExecutor.cs`), which runs a **bounded nested agent loop** on the
node: a MAF `ChatClientAgent` over the same `IChatClient` the outer turn runs on, built the way
`SubAgentSpawnService` builds a spawned child, invoked as an `AIFunction` inside a child `SpawnContext` scope.
Its tools work only on the sandbox's workspace copy, and `allowedActions` decides which of them exist at all:

| `allowedActions` value | What the loop is handed |
|---|---|
| `read_workspace` | `list_files` / `read_file` / `search_text`, served by the existing `CoderWorkspaceReader` against the same sandbox |
| `write_workspace` | `write_file` — one bounded UTF-8 file, written through the provider's own jail-guarded copy-in |
| `run_commands` | `run_command` — any executable, jailed, with the workspace copy pinned as its working directory |
| `export_patch` | the post-run patch export (unchanged) |

There is no fifth value. `propose_memory` was **removed** from the schema rather than left standing, because the
node collected the sandbox's proposals and then discarded them; the collector that read them has since been
deleted too, so nothing in the run path reads the sandbox's `memory/proposals/` directory. Persisting a proposal
into the adaptive-memory Suggested pipeline is a later slice that would add its own collector. (The shared
`MemoryProposalSecretScanner` is unrelated to that slice and stays: its live callers are `MemoryExtractionService`
and `DevelopmentArtifactSanitizer` — see [Security & Privacy](12-security-and-privacy.md).)

**What bounds the loop.** Four budgets, separate from the sandbox's own per-command timeout and jail-disk
ceiling, all on `AgentHomeOptions`: `MaxRunSeconds` (the whole loop's wall clock — it matters most, because an
inner loop holds the node's single inference slot for as long as it runs), `MaxInnerToolCalls`,
`MaxWriteFileBytes` / `MaxTotalWriteBytes`, and `MaxCommandOutputBytes` for how much command output re-enters
the model's context. A budget that fires **cuts the run off and says so** in the tool result; it never discards
the partial work, which still exports as a patch. The shipped `MaxRunSeconds` default is a conservative starting
point, not a measured one — a live round on the target model class is what should set it.

**What confines the loop.** The inner tool list is built item by item from `allowedActions`, never read off the
tool offer, so the inner agent structurally cannot reach an MCP tool, a custom tool, `spawn_subagent`,
`ask_user`, a knowledge tool, an approval-gated tool of any kind, or a nested `run_in_agent_home`. It has no
human-in-the-loop route, which is why the operator's single approval of the outer call has to cover the whole
envelope up front. Every model-supplied path goes through `WorkspacePathGuard` and then the provider's
`ResolveJailPath` + `EnsureNoSymlinkComponentsUnderJail` pair, so an absolute path, a `..` climb, or a symlink a
command just planted is refused; writes anywhere under `.git` are refused outright, because that is where the
baseline the exported patch is diffed against lives. File contents and command output are fenced with
`UntrustedContentFraming` before they re-enter the model, and the sandbox's own root is stripped from captured
output so no host path reaches it.

**Export stages before it diffs.** `git diff HEAD` does not see an untracked path, so an export that diffed the
working tree left every file the run *created* out of `changes.patch` and `changed-files.json` while the run
reported it as written — silent data loss the moment the operator can apply a patch. Export therefore runs
`add -A` first and diffs `--cached` against the baseline. Deliberately **not** `--force`: the workspace copy's
baseline `add -A` honours `.gitignore`, so forcing would report every ignored-but-copied file as one the run
added, and the two sides of the comparison have to apply the same rule. A model that hides its own creation
behind a `.gitignore` shrinks what the operator is offered but cannot get an unreviewed file onto the host; the
run's `run_completed` event carries `changed_files` beside `files_written` so the two numbers can be compared.

**The patch export's own git is hardened, and logged.** Export runs `git diff` over the workspace the model just
had `write_file` and `run_command` access to, **after** its turn ended and outside the run's budgets — and git
executes programs named by configuration (`diff.<name>.textconv`, `diff.external`, `filter.<driver>.clean` on any
content conversion, `core.fsmonitor` on an index refresh). Measured on git 2.53.0 against the exact export
argument vector: a repository-local `textconv` and a repository-local `clean` filter both executed, and so did a
driver defined in the **global** config (the sandbox forwards `HOME`). `AgentHomeGit`'s `-c` pins cannot close
that class — driver names are arbitrary and git has no flag that disables attribute processing. Neither do
`--no-textconv` and `--no-ext-diff`: the clean filter still ran under both, so they are belt and braces here, not the
control.
`AgentHomeGitHardening` closes it structurally instead, on the principle that a driver has to be **defined in
configuration** to run, so an in-tree `.gitattributes` naming an undefined one is a no-op:

- the repository's own `.git/config` is rewritten to a node-owned allow-list immediately before every node git
  invocation, reusing Development Mode's `DevelopmentWorkspaceGitConfig` rather than growing a second such rewrite;
- the global and system files are removed from git's search entirely by `GIT_CONFIG_GLOBAL` (pointed at the null
  **device**, which nothing can turn into a config file), `GIT_CONFIG_NOSYSTEM` and `GIT_ATTR_NOSYSTEM`;
- a `.git` that is not a real directory — a gitfile pointing at a model-owned git directory defeats a rewrite
  outright — **fails the export closed** rather than exporting a patch the node cannot vouch for;
- and every git invocation the node makes itself is written to the run's `commands.jsonl` with `actor: node`,
  beside the model's own `actor: model` entries, so an audit sees the whole sequence rather than half of it. That
  includes the export's own staging `add`, and the workspace copy's **baseline** git (`init` / `add -A` /
  `commit`), which runs during *prepare* —
  before a run id or a log exists — and is carried out of preparation on the prepare result and flushed the
  moment the log opens. Each record keeps the timestamp and duration from when its command really ran, and
  carries argv, exit code and duration only: never a byte of what the command printed.

**The `-c` pins and the byte-stability pair.** `AgentHomeGit.Arguments` prefixes every AgentHome git invocation with
the hardened `-c` set, because a command-line `-c` outranks every config file, `include.path` chains included. The
exec-bearing key that is actually live there is `core.fsmonitor`: a value planted in a repository-local `.git/config`
runs as a shell command on the first index refresh — `status`, `reset`, `add` and `diff` all trigger it — and that is
reachable from the **host**, not only the sandbox, because `DevelopmentPatchEvidenceService` runs `reset` and `add -A`
with its working directory set to the workspace and the workspace `.git/config` is writable from inside the container.
`core.sshCommand`, `core.pager` and `core.editor` are not reachable from today's command set (no network operation, and
stdout is always redirected so no pager spawns) and are pinned anyway, so adding a fetch or a paging command later
cannot quietly re-open the hole. `core.autocrlf=false` and `core.filemode=false` are *not* in that shared set:
`AgentHomeGit.WorkspaceArguments` adds them on the command line for the baseline and the export only. On the command
line because the pre-invocation config rewrite would drop a stored value before the diff; not in the shared set because
Development Mode, Dev Workflows, knowledge-repository import and host patch apply run `Arguments` against the
*operator's own* checkouts, where forcing `core.autocrlf` would change how a patch renders or applies on a repository
that legitimately stores CRLF — Development Mode derives that policy from the repository's own index
(`DevelopmentWorkspaceWhitespacePolicy`) precisely so that it is not forced.

Two residuals are stated rather than claimed away. The rewrite happens immediately before the invocation, so a
process a `run_command` deliberately double-forked could in principle rewrite the file again in between — the
same window Development Mode accepts, and not closable on a soft jail with no filesystem boundary. And a model
that rewrote the git objects or HEAD could make the exported patch misrepresent the tree; that bounds what a
patch proves, and the operator reviewing it is the control.

**The result's first line is a contract.** Line one is a node-authored header, start-anchored, in exactly this
shape:

```
[agent-home run=<run-id> outcome=<Token> patch=<exported|none>]
```

Everything after it is prose, and that prose embeds the **model-authored** `run_command` executable *before* the
run's genuine `runs/<id>/patches/changes.patch` string. A reader that recovered the run id by scanning the prose
could therefore be handed `runs/<other-run>/patches/changes.patch` by a model that simply named its executable
that, and would offer the operator a different run's patch. **Position is the control**: every field in the
header is node-derived, the line is emitted before any model-authored byte, and nothing model-authored can reach
index 0 (the one model-authored string the summary renders is also flattened to a single line, so it cannot even
produce a second line that looks like a header). `patch=exported` means `changes.patch` was really written — a
patch over `MaxPatchBytes` reports `none` although `changed-files.json` exists. A **rejection** (busy, unknown
folder id, disallowed runtime profile) carries no header at all: no run was created, so there is nothing to
offer, and a missing header means "no run" rather than "this turn's run". Parse it start-anchored or not at all;
do not reorder the fields, add one, or widen a value.

**What the outer model is told.** The tool returns a plain string (every `IClientLocalToolHandler` does), built
only from facts the **node** derived: the header above, the run id and run-relative output paths, a stop-reason
sentence, the per-folder copy counts, the loop's tool-call /
refusal / written-file counts and elapsed seconds, an aggregate of the commands run with their exit codes, the
patch's changed-file count and byte size, the `fake`-backend honesty notice, and a fixed closing sentence saying
the run has ended and need not be repeated (a live round watched a 27B model re-invoke the tool two and three
times in one turn because the summary was too thin to trust). **Command output and workspace file bytes never
appear there**, and neither do the paths `write_file` wrote: the outer model still holds the node's other tools,
so workspace-authored text arriving as "your tool result" is steering text with a delivery mechanism. The detail
lives on disk, under `runs/<run-id>/`. `AgentHomeToolResultContainmentTests` plants one marker through all three
routes at once — a command's stdout, a written file's content, and the path the model chose for a second file — and
grades the returned string on its absence.

**It only runs from an approved chat turn.** The loop reads the outer model id off the ambient `SpawnContext`
root that `InvocationRunner` seeds, and refuses when there is none or when that model is outside the node's
trust boundary. The unattended entry points seed a root without a model id, so they cannot reach it even if the
approval gate were somehow bypassed.

**`run_command` ships only behind a real filesystem boundary.** The process jail confines a command child's
**network**, **environment** and **resource ceilings** and pins its working directory — but under
`SandboxIsolationMode.None` it is **not a filesystem boundary**: the command reads and writes any path the
engine's user can, including the operator's original registered folder. So AgentHome asks for
`SandboxIsolationMode.Filesystem` wherever the provider advertises it (a request the registry refuses
fail-closed if it cannot be met, which is why it is gated on the advertised capability and can never sink a run),
and the goal loop then reads the boundary it actually **got** off `SandboxHandle.Isolation`:

- **boundary delivered** → `run_command` joins the inner tool list. The child sees a read-only `/usr`, the
  workspace at `/work`, a `HOME` inside the jail, and no network; a path outside is not in its mount namespace at
  all, so it can be neither read nor written.
- **no boundary** (a Linux host without user namespaces or bubblewrap, and **every Windows host today**) →
  `run_command` is **withheld even though `run_commands` was allowed**, the inner prompt tells the model so, and
  the tool result says *"commands were not available: this node cannot isolate the sandbox file system"*. The
  read tools and `write_file` stay, because they are confined by the node's own `WorkspacePathGuard` and the
  provider's no-follow file surface rather than by the jail.

`ProcessSandboxFilesystemReachTests` measures both sides, and `AgentHomeGoalExecutorTests` pins the exact inner
tool list with and without the boundary. See [Security & privacy §7](12-security-and-privacy.md).

**One sandbox posture, asked for once.** The isolation request is made for *every* run, not only when `run_commands`
was granted, for three reasons. It cannot sink a run, because the request is gated on the capability the provider
advertises and that advertisement is mechanical — the same probe the launch path reads. The sandbox is owner-node
scoped and **reused across runs** through `CreateOrAttach`, so a per-run request would be a lie on the attach path:
the second run would silently inherit the first run's boundary while believing it had asked for its own. And a
read/write-only run is no worse off for having the boundary, while the chat attachment re-stage creates the same
sandbox, so one posture keeps the two entry points from disagreeing. **Egress** is requested the same way and
default-denied wherever the provider can enforce it: everything AgentHome and Coder run inside the sandbox is local
(`dotnet --version`, the baseline git commands under `/dev/null` hooks, Coder's `find`/`grep`), so denial costs no
supported capability and removes the child's reach to the node's own loopback API, the LAN and the cloud-metadata
endpoint. Capability-gated rather than unconditional for the same reason — asking for confinement a host cannot
provide would stop AgentHome running rather than harden it — with the degradation visible in the sandbox containment
log rather than silent. A node that wants the refusal instead sets `AgentHome:Sandbox:RequireEgressDenial`, which
`SandboxEgressPolicy` turns into a fail-closed refusal naming that key.

**What `Prepare` does before the loop starts.** `AgentHomeService` builds the attach key, recovers the worker-local
layout, attaches or creates the sandbox, resolves and copies the selected folders, and creates the git baseline;
`Run` then hands the `goal` to the executor and feeds the gated patch export and run-scoped logging. The
service-level tests exercise orchestration, busy/cancel/owner hardening and the gated patch export against the
deterministic provider, while the executor's own tests drive the real inner tools with a scripted chat client — the
configured runtime provider is what supplies real command execution and git behaviour.

**End to end, with the operator holding the last step.** An exported patch is previewed and landed through the
`AgentHomePatch` endpoint pair and the chat card's apply dialog (above). What a run still has no surface for is
listing its own history: runs accumulate on disk with no retention sweep and no run list, so an operator reaches
a patch through the tool result that produced it.

**Conversation-attachment staging** (`IConversationSandboxStager`,
`Services/AgentHome/IConversationSandboxStager.cs`). `AgentHomeService` also implements this narrow public
seam (`AgentHomeService.PrepareConversationAttachmentsAsync`) so the public
`NodeChatStreamService` can stage a chat conversation's uploaded attachments into the per-turn sandbox
without an inconsistent-accessibility error. When an agent-mode turn offers file tools, the stream
service re-stages the owner-node sandbox to hold **only** that conversation's extracted attachments under
the workspace `attachments/` alias (the sandbox is recreated first, so there is no cross-conversation
residue), then hands the model the staged workspace-relative paths so its `list_files` / `read_file` /
`search_text` tools read them directly. Because the same shared singleton implements both interfaces, the
re-stage shares the run-level single-flight guard with `run_in_agent_home`. It is a no-op (empty list)
when Agent Mode is disabled or the conversation has no extracted files. The chat-side wiring — and the
contrasting plain-chat path that *inlines* extracted text instead of staging — is in [Chat](05-chat.md).

#### Resolving a selected folder by id or alias

`SelectedFolderResolver.ResolveAsync` accepts either form the node hands out: the opaque GUID, or the human-facing
**alias** that Node Settings displays and that `run_in_agent_home`'s schema advertises. The GUID is tried first,
because an alias of GUID shape is registrable — `NormalizeAlias` leaves lowercase hex and hyphens untouched and
`AliasShapeRegex` accepts the result — so an opaque id must never be shadowed by an alias that merely looks like one.
Falling through to the alias lookup only when the id lookup misses is what keeps such an alias reachable rather than
permanently masked.

The alias is matched **exactly, never normalized**. Stored aliases are already canonical, because registration
normalizes before persisting, so an exact match on a canonical input finds precisely what exists; refusing to normalize
here keeps this seam from quietly resolving `My Scratch` to `my-scratch` for a caller that passes unvalidated text.
Both lookups go through the same store, which returns `null` for a revoked folder, so alias resolution reaches exactly
the records the GUID path reaches and no others.

### 2.3 Capacity gate & sub-agent spawn

`SubAgentSpawnService.SpawnAsync` (`Services/Capacity/SubAgentSpawnService.cs`) implements the
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

#### What the capacity gate admits without probing

`CapacityService.DecideAsync` short-circuits twice before it reads any byte budget.

A **cloud-routed model** is admitted unprobed: its sends cost this node no bytes and no process. The check is keyed
on the request's `modelName` rather than on the node-default selection, because `RuntimeChatClient` re-selects the
provider per send from the same per-request model id — so an active Codex session must not exempt a spawn that
explicitly names a local model, and a local model must still be admitted on its own footprint while Codex is
signed in.

An **operator-registered external OpenAI-compatible model** is admitted unprobed for the same reason: it runs
entirely on someone else's hardware, the node starts no process and loads no weights, and the footprint provider
has no GGUF to size — so *not* admitting it would be actively wrong, rejecting every such send as "footprint could
not be determined". Two conditions are checked on purpose. The provider-map row written by the save path is the
normal route; the model id's `ext:` scheme is the backstop for the window where that row is missing — a crash
between the encrypted-store commit and the map sync, or a row the reconciliation pass has not repaired yet — in
which the model would otherwise default-route to `llamacpp` and be rejected on a footprint it can never have.
Neither branch grants anything: capacity admission is about local resources only, and an external model's trust and
egress decision is made elsewhere, from its operator-declared locality.

The rest of the decision runs under `IPendingFootprintLedger.EnterDecisionAsync`. The device audit is warmed
**before** that gate, because its bounded, cached `--list-devices` probe would otherwise serialize every capacity
decision behind a one-time probe. The hardware profile is then force-refreshed **under** the gate: an admission
decision runs per model-load — rare, and already serialized — so it reads a live VRAM/RAM snapshot rather than the
profiler's boot-time cache, and the gate bounds it to one probe in flight. `GetEffectiveProfileAsync` degrades that
profile to CPU-mode when the audit reports a silent CPU fallback (see
[03-local-runtime-and-providers.md](03-local-runtime-and-providers.md) §2.5), so a GPU box whose runtime enumerates
no devices sizes against system RAM instead of pretending VRAM exists.

#### What a profile-bound child inherits

A profile-bound child consumes the SAME complete `ResolvedAgentRuntime` a direct agent send does — resolved once,
from the definition snapshot already read rather than by id, so a concurrent edit cannot assemble one child out of
two versions. It therefore inherits the resolved system prompt (scaffold + persona + injected playbook memory),
reasoning effort (gated on the child model's own thinking capability, mirroring `ParticipantReasoningOptions`),
skills (MAF progressive disclosure), AND its curated tools — not just the tool set. This is structurally the
orchestration-participant path: an agent-as-tool never receives the outer runner's per-run `RunOptions`, so
reasoning and skills must be baked into the agent at construction.

A profile-bound child's curated tools are `offer ∩ AllowedToolNames`, minus `spawn_subagent` AND any approval-gated
tool — a child has no HITL route to answer an approval request (§4.4). A model-id-only child (no profile, no
`AllowedToolNames`) stays as it was: raw request instructions, tool-less, no reasoning and no skills. Post-run
adaptive-memory **extraction** stays disabled for a child, an intentional restriction.

**The child pins its own external binding.** `SubAgentSpawnService.RunSubAgentAsync` resolves the child's pin through
`ExternalProviderInvocationPin` and opens `ExternalProviderBindingPinScope` for it, exactly as `InvocationRunner` does
for the parent turn. The parent's pin is keyed by the *parent* model, so without this an external child's sends find no
pin and fall through to the transport's weaker unpinned check — **while the child is running with a tool set that was
authorized against the declaration read here**. That is the reason the mismatch matters: a turn decides once, before
its first send, which tools the model may be offered from the connection's declared locality, and the pin is what makes
every later send in the tool loop verifiable against that same declaration.

The scope is opened **synchronously, in this frame**, and not inside the async helper: an `AsyncLocal<T>` written
inside an `async` method is invisible to its caller, so a pin seeded there would not survive the helper's return. The
child run happens inside this frame's flow, so the scope reaches it; pins stack, so the parent's pin lives on
underneath and is restored on dispose.

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

**The terminal telemetry rides all three terminal paths.** `InvocationRunner` reports the turn's tool-schema token
estimate, the model-readiness duration and the turn's **summed** usage onto the invocation state immediately before
it reports the turn completed, cancelled or failed, so the terminalize write persists them with the envelope row.
Failure and cancellation carry them too, because the numbers are most interesting on a turn that ran out of context;
the readiness duration is `null` on any turn with no local warm (Ollama, a remote provider), which is what that
column means; and the summed usage is the turn's **cost**, as opposed to the last round's counts that
`ReportInvocationCompletedAsync` puts on the message row. The report runs *before* each terminal report and swallows
its own faults, so a throw can neither turn a finished turn into a failed one nor replace a real failure
classification. `CaptureEfficiencySnapshot` is a pure counter read, so calling it on every path is free. Counts
only: no tool name, prompt or tool result reaches this seam.

### 2.5 The composite turn budget

`TurnPolicy` (`Services/Invocation/Policy/TurnPolicy.cs`) is the immutable per-turn snapshot of every
timeout, retry and budget knob that governs one invocation. `InvocationRunner.RunAsync` resolves it **once**
and flows it unchanged through both the single-agent and the orchestration path, so the two enforce identical
policy for one turn. It is a resolution and documentation seam only: every field is copied from an existing
configured source — the package's `TimeoutSettings` and the `Agent:ConversationContextBudget`,
`Agent:ProviderResilience` and `Agent:ToolPipeline` sections — and nothing on the record is itself persisted
or folded into a runtime package's config hash.

The three timeouts are a ladder; a stalled or slow turn trips them in this order, tightest first:

| Bound | Source | Enforced by | What it bounds |
|---|---|---|---|
| `StreamIdleTimeout` | package `TimeoutSettings.StreamIdleTimeoutSeconds` | `StreamIdleWatchdog` | No chunk arrives between two yielded items of ONE streamed segment |
| `ToolResultTimeout` | package `TimeoutSettings.ToolCallTimeoutSeconds`, else the node-global pending-tool-call age | `ApiToolCallBridge` | The wait for a tool call's RESULT |
| `InvocationTimeout` | package `TimeoutSettings.InvocationTimeoutSeconds` | `InvocationLifecycleTracker` | The whole turn's wall clock, every segment and approval round-trip end to end |

Two splits in that ladder are deliberate and stated rather than quietly unified. The orchestration path
enforces an analogous **per-quiescence** idle bound in `OrchestrationRunSession`, but that timer comes from
the node-global `OrchestrationAgentOptions.IdleTimeoutSeconds`, not from the package field above. And the
human-**approval** wait always uses the node-global pending-tool-call age, never the shorter per-tool
`ToolResultTimeout` — a person is not a tool call.

Context budgeting (`ContextCapacityTokens` / `ReservedOutputTokens`) is orthogonal to all three: it bounds
what history is *sent* to the provider, not how long the provider is given to answer.
`RetryEnabled` / `MaxRetries` / `CircuitBreakerEnabled` govern only the pre-first-token send of the **first**
segment (`ProviderStreamResilience`). `MaxToolIterationsPerRequest` and
`MaxConsecutiveInvalidToolCallsPerTool` are the node-global tool-pipeline ceilings that the DI-wired
`FunctionInvokingChatClient` and `ToolArgumentRepairAIFunction` apply; they ride the record as read-only
reference values, so one place documents the whole turn's bounds.

`WithEffectiveContext` folds the window a local model actually launched with back into the policy.
`RequestedContextTokens` is what makes that safe: a user-requested `num_ctx` is a ceiling to keep, while the
untrusted configured default must be **replaced** by the real launched window.

### 2.6 The coder reader

`CoderWorkspaceReader` (`Services/Coder/Implementation/CoderWorkspaceReader.cs`) is the single read-only gateway behind
`list_files`, `read_file` and `search_text`. All three are **provider** operations — `ISandboxRuntimeProvider.ListFilesAsync`,
`ReadFileAsync` and `SearchTextAsync` — not argument vectors handed to `ExecuteAsync`.

That matters twice. A `find`/`grep` argument vector is POSIX-only: on a stock Windows 11 install `grep` does not exist and
`find` resolves to the DOS tool, which rejects the vector. And it puts the confinement in an argument list this class would
have to keep correct, rather than in the component that owns the jail. A typed search request carries the pattern as **data**,
so a value beginning with `-` cannot be read as a flag, and a model-supplied expression runs under a per-line timeout the
shell-out never had.

The **secret exclusions stay with the reader as caller policy**, deliberately. They are supplied to the provider so excluded
trees are pruned *before* the result budgets — a large `.git` baseline sorts ahead of project aliases and would otherwise
consume the whole cap — and then re-applied to the returned paths as defence in depth. The policy is broader than Development
Mode's: Coder drops its whole copy-filter set, not just credentials. The `read_file` post-filter gates on `IsSecret` only,
because a preserved workspace legitimately contains build output and refusing `bin`/`obj`/`node_modules` would cost an agent
real capability while protecting nothing.

Every result the model sees is fenced through `UntrustedContentFraming` with a per-call **random** nonce: workspace file
names, match lines and file content are all attacker-influenced, and a tool result is query-dynamic rather than a
prompt-cache-stable prefix. Node-authored lead lines and truncation notices stay outside the fence.

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
  conversation content never leaves the box for analysis.
- **P4 Eval gate** — golden conversations are **re-run through the real MAF loop** node-local
  (`MafPlaybookEvalAgentRunner`) to gate **Suggested → Enabled**; golden conversations are stored
  encrypted.
- **P5 Monitoring + retrieval** — `PlaybookMonitorService` does cohort monitoring; at inject time the
  prompt composer applies **relevance retrieval**.

### 3.1 Relevance retrieval at prompt-compose time

In `AgentDefinitionResolver.ComposePromptAsync`: when the playbook is **disabled** the
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

## 4. Agent Skills

An Agent Skill is a `SKILL.md`-shaped document (name + description + markdown body, optionally
bundled files) that an agent definition selects into via `AllowedSkillIds` and MAF loads on demand —
progressive disclosure, not a static prompt prepend. The implementation conforms to the open
[Agent Skills specification](https://agentskills.io/specification) and to the pinned
`Microsoft.Agents.AI` version in `Directory.Packages.props`, not to Claude Code's product
extensions (`disallowed-tools`, `${CLAUDE_SKILL_DIR}`, nested skills) — those are not part of
the standard.

### 4.1 Data model

| Table | Key columns | Notes |
|---|---|---|
| `agent_skills` | `name`, `description` (encrypted), `body` (encrypted), `enabled`, `frontmatter_json` (encrypted, nullable), `origin`, `source_uri`, `imported_at_utc`, `content_sha256`, `version` | `AgentSkill.cs`. `frontmatter_json` is **one** encrypted blob holding `{license, compatibility, allowedTools, metadata}` rather than four columns — the fields are optional, sparsely used, and `metadata` is arbitrary operator- or third-party-supplied content. `origin` (`AgentSkillOrigin`: `Local=0`/`Imported=1`) is **plaintext and structural** — the resolver branches on it to decide whether to fence the body (§4.2), and it is **promote-only**: a row can move `Local → Imported`, never back, because demoting would silently strip the untrusted-content fence from a row an operator believes is now theirs. `version` bumps on any content-affecting edit (name/description/body/frontmatter, or a resource add/edit/remove) and drives the runtime config hash, so an edit invalidates a parked resume. `Enabled` toggles do **not** bump it. |
| `agent_skill_resources` | `id`, `skill_id` (FK, **cascade delete**), `name`, `description`, `media_type`, `content` (encrypted), `size_bytes` | `AgentSkillResource.cs`. This is the level-3 payload — the `references/`/`assets/` files a real skill's body links to. `name` is the skill-root-relative path (`references/FAQ.md`) because MAF's generated skill content tells the model to quote the name back exactly, and it is **immutable**: it is part of the content AAD, so renaming a resource is a delete-and-reinsert, never an in-place update. |

**The resource AAD binds `skill_id` **and** the resource `name`, not just the row id** — every other
encrypted column in this schema authenticates only its own row id. That would be wrong here: the
threat is a database *writer*, not a reader. Without `skill_id` in the AAD, anyone with write access
to the DB could re-parent an existing encrypted resource row onto a different skill and have its
content injected into another agent's context without forging a single byte of ciphertext. A test
performs exactly that raw-SQL re-parenting and asserts the read throws `CryptographicException`
(`AgentSkillStoreTests.cs`). `source_uri` stores the **kind only** (`upload`) for an uploaded archive —
an operator-chosen filename must not become the one plaintext free-text string in a table where
everything else is AEAD-sealed — but keeps the full `github:owner/repo` for a GitHub import, since that
value is already public.

Storage stays DB-only and encrypted **by decision**, not by omission: skills never touch disk, so MAF's
own `AgentFileSkillsSource` and its path-traversal/symlink guards — which protect *reading skills from
disk* — cannot be used at runtime. The node's exposure is instead at *import time*, where untrusted
archive entries are parsed (§4.5) — a deliberate trade that keeps the at-rest encryption posture and
reuses the repo's own hardened path helpers rather than inventing new ones.

### 4.2 Resolution — the single choke point

`AgentDefinitionResolver.ProjectSkill` (`Services/Agents/Implementation/AgentDefinitionResolver.cs`)
is the **only** place a stored `AgentSkill` becomes a `ResolvedSkill` that reaches an agent, for both the
invocation path and the sub-agent spawn path. Three things happen there, all load-bearing:

1. **Only enabled and assigned skills resolve.** A skill missing from the resolved set (deleted or
   disabled) is dropped and logged **by id only** — never the name, body, or description. This is the
   strongest control in the whole feature: an imported skill lands `enabled=false` (§4.5), so a
   third-party instruction cannot reach a model until an operator makes a **second, deliberate** act to
   turn it on.
2. **A MAF-invalid name is dropped fail-soft, not thrown.** `AgentSkillFrontmatter.ValidateName` (the
   validation authority — see below) is checked again at resolve time; a skill whose stored name it
   would reject (e.g. a legacy row with consecutive hyphens, `foo--bar`) is dropped with a
   `LogWarning` naming only the definition and skill ids, and the agent still builds. This mirrors the
   existing dropped-tool posture in `ProjectAllowedTools`: degrade, log, never fabricate, never throw.
   Before this guard, an invalid name persisted cleanly through the editor and then threw
   `ArgumentException` out of `AgentInlineSkill`'s constructor at agent-construction time, in **both**
   `InvocationAgentFactory` and `SubAgentSpawnService` — the turn died before the model was ever
   reached, and the skill had to be un-assigned or renamed to recover.
3. **Imported content is fenced.** `Origin == Imported` bodies and every one of that skill's resource
   payloads are wrapped through `UntrustedContentFraming.WrapDocument` before they leave the resolver —
   the same nonce-fencing every other attacker-controlled channel in this engine already gets
   (knowledge-base search/read, chat attachments, coder workspace reads). Before this, imported skill
   markdown was the **only** such channel reaching the model unfenced, and it landed in the *instruction*
   position — ranked above the operator's own documents. The reachable attack needed zero approvals: a
   skill body could direct `search_knowledge_base` + `read_file` (both approval-free) and then
   `spawn_subagent` (also approval-free, caller-chosen model) to hand what it found to a cloud model as
   a prompt — a path the cloud-egress gate does not cover, because it withholds local-data *tools* from
   a cloud model but says nothing about local data already read being forwarded as text.

   The nonce seed is `agent-skill:{id:N}:{version}` (`BuildFenceNonceSeed`,
   `AgentDefinitionResolver.cs`) — the id is a server-minted GUID that never appears in the skill
   file, so a body author cannot derive the nonce and forge a closing marker, and the seed is
   deterministic so the fenced text — and therefore the config hash — stays byte-stable across resolves.
   Local, operator-authored skills are **not** fenced; only `Origin == Imported` rows pay this cost.

   **Residual, stated rather than hidden:** MAF renders each skill's and resource's `name`/`description`
   into the generated skill content **outside** any fence the resolver controls, because they are the
   lookup keys the model must quote back verbatim to call `load_skill` / `read_skill_resource`. Those
   four fields are therefore attacker-chosen text that reaches the model unfenced for an imported skill.
   The mitigations are defence-in-depth, not a fence: MAF's own length caps (name 64, description 1024),
   an import-time charset guard on resource names (§4.5), visibility in the import preview, and the same
   values additionally carried *inside* the fence as metadata.

**Validation authority is `AgentSkillFrontmatter.ValidateName` / `ValidateDescription`**, not a
hand-rolled regex — `AgentSkillService.ValidateAsync` delegates to it directly, so the app and MAF can
no longer drift the way they did for the consecutive-hyphen defect above. `Microsoft.Agents.AI` is a
**direct** `PackageReference` on `Client.Application` for exactly this reason: a validation authority
must not ride a transitive dependency flow.

### 4.3 Runtime — MAF progressive disclosure and the three tools

Both agent-construction sites — `InvocationAgentFactory.CreateAsync` (builds the `ChatClientAgent`,
resolves executable tools, then attaches skills) and `SubAgentSpawnService`'s child-binding path — build
a MAF `AgentSkillsProvider` from the resolved skills as `AgentInlineSkill`s and attach it via
`ChatClientAgentOptions.AIContextProviders`, not through the ordinary tool registries. `AgentSkillsProvider`
/ `AgentInlineSkill` ship `[Experimental]` in this MAF version (`MAAI001`), so every call site carries a
scoped pragma suppression.

The provider injects three tools, MAF-named and not present in this repo's own tool catalog:

| Tool | Purpose | Approval, default options |
|---|---|---|
| `load_skill` | Loads a skill's `SKILL.md` body (level 2) | **Required** |
| `read_skill_resource` | Fetches one bundled resource by name (level 3) | **Required** |
| `run_skill_script` | Would execute a bundled script | **Required**, but always fails closed here |

**All three are approval-gated by default** at the pin — a live regression this feature
uncovered relative to the 1.8.0 baseline the original skills work was verified against, since neither
construction call site set `AgentSkillsProviderOptions` or registered an auto-approval rule. A contract
test (`AgentSkillsProviderContractTests.AgentSkillsProviderOptions_GateEverySkillToolByDefault`) pins
all three defaults so a future MAF bump that flips one fails loudly instead of silently changing
behaviour.

`run_skill_script` is advertised and callable, but this engine never registers a script for it —
`AgentInlineSkill.AddScript` only accepts a `Delegate`, and scripts are a deliberate non-goal (§4.5). It
therefore **always fails closed**: invoking it with no registered script returns
`"Error: Script 'x' not found in skill '<name>'."`, never reaching an execution path. Its approval gate
is never disabled, on any path, including the sub-agent waiver below.

Because these three tools are injected by the context provider rather than resolved through
`InvocationToolResolver`, they never appear in the ordinary tool catalog and would otherwise audit as
`ToolCategory.Unknown`. `ToolApprovalCoordinator` carries its own `SkillToolCategories` map
(`ToolApprovalCoordinator.cs`) — `load_skill`/`read_skill_resource → ReadLocal`, `run_skill_script →
WriteExecute` — consulted **before** the normal offer-based category lookup in
`ResolveApprovalToolCategory`, purely so the approval audit trail can tell a skill-tool decision apart
from a genuinely uncategorized one. This does not put the tools under `IToolApprovalPolicy` (OPP-03):
that policy is tighten-only and is applied by re-projecting an *offered* tool's `RequiresApproval`, and
these tools are never offered — MAF owns their approval decision outright, and the node policy has no
lever over it, which is the correct direction (nothing is being waived) and needed no new mechanism.

### 4.4 The sub-agent waiver

A spawned child ordinarily has **every** approval-required tool stripped from its offer
(`SubAgentSpawnService.CurateChildTools`), because a child runs as an `AIFunction` via `AsAIFunction()`
with no per-run options and no human-in-the-loop round-trip: an approval-gated tool would surface a
`ToolApprovalRequestContent` the child can never answer, silently failing every call. Before this work,
`AttachSkillsProvider` attached the provider with its default (all-gated) options anyway, because it
rides `AIContextProviders` and bypasses `CurateChildTools` entirely — so **a skill assigned to a spawned
child could never be loaded**.

The fix constructs the child's provider with `DisableLoadSkillApproval = true` and
`DisableReadSkillResourceApproval = true` (`SubAgentSpawnService.cs`). The justification is the
same one that already governs every other capability a child inherits: **the operator already approved
the spawn**, and there is no human downstream of that decision to ask. This is a security-relevant
deviation, made deliberately and logged, not a silent default. `run_skill_script`'s approval is **never**
waived, for the child or anyone else — it is inert (§4.3), so there is nothing to gain and the one
tool that could execute something stays gated unconditionally.

### 4.5 Import pipeline

`ISkillImportService` (`Services/Agents/ISkillImportService.cs`) is a **two-phase, dry-run-first**
pipeline — the entire reason it exists is that operators overwhelmingly *import* skills
(`npx skills add owner/repo` is the ecosystem norm) rather than author them, and this engine had no
import path at all:

```
source ──► fetch ──► extract ──► parse ──► validate ──► REPORT ──►[operator acknowledgement]──► persist
```

- **Preview** (`PreviewArchiveAsync` / `PreviewMarkdownAsync` / `PreviewGitHubRepositoryAsync`) parses,
  guards, and returns a `SkillImportPreview` report. **It writes nothing.**
- **Commit** (`CommitAsync`) replays the *materialised preview payload* against a single-use report
  token — it never re-parses the upload or re-fetches the repository. Re-deriving the content at commit
  time would reopen exactly the divergence the two phases exist to close: a GitHub repository can change
  between the two calls, so the operator would be approving one payload and persisting another.
- **Imported skills always land `enabled=false`** with `Origin=Imported` provenance. Enabling is a
  separate, deliberate act — this is the strongest control in the design (§4.2 point 1), not the preview
  or the acknowledgement checkbox.

**Three sources, one archive-extraction path.** Upload (`.zip`), pasted raw `SKILL.md` text, and a
GitHub `owner/repo` (`GitHubSkillArchiveDownloader.cs` — host allowlisted to `github.com` /
`codeload.github.com`, redirect host revalidated on every hop, a pasted URL is **never** accepted). A
pasted document has no containing directory, so its frontmatter `name` is authoritative and it imports
instructions-only. A collection repository (e.g. `microsoft/skills`, ~175 skills) is never bulk-imported
— the report lists every skill found and the operator **selects**.

**Extraction is in-memory** (`SkillArchiveReader.cs`) — nothing is written to disk at any point, which
removes the symlink and TOCTOU classes entirely rather than guarding them. The guards, all fail-closed
with an operator-visible reason, and all bound to `SkillImportOptions` so an operator can tighten them
without a rebuild:

| Guard | Default | What it actually bounds |
|---|---|---|
| Entry count | 8192 | Central-directory enumeration cost only — cheap, so this is deliberately generous |
| Per-entry inflated bytes | 1 MiB | The real per-file memory guard |
| Total inflated bytes | 32 MiB | Zip-bomb ceiling across everything kept |
| Compression ratio | 100:1 | Zip-bomb ceiling per entry |
| Archive size | 50 MiB | Hard cap on the upload as received |
| Resources per skill | 64 | A whole-archive cap alone would let one skill carry hundreds |

The entry-count/size caps started far tighter (512 entries, 10 MiB total) and made the flagship import
target, `microsoft/skills`, **unimportable** — entry count only bounds the cheap enumeration walk, while
the caps that actually bound memory are the per-entry and total *inflated* byte caps, and only entries
the import intends to keep are ever inflated. The limits were widened once that was measured, not
loosened casually.

**The guards bound bytes actually inflated, never `ZipArchiveEntry.Length`/`CompressedLength`** — those
are attacker-authored header fields. A `new byte[entry.Length]` pattern is the naive mistake this avoids:
an over-declared length steers such code into an OOM on an otherwise harmless archive (the *reachable*
lie — measurement during implementation showed an *under*-declared length is not constructible through
`ZipArchive` at all, because its own read path stops at the declared size regardless of what is
requested). UTF-8 decoding uses `new UTF8Encoding(false, throwOnInvalidBytes: true)`, not
`Encoding.UTF8`, which silently substitutes `U+FFFD` and would make that guard a no-op. Duplicate entry
`FullName`s are rejected outright, because `ZipArchive` resolves a duplicate differently when enumerating
than when fetched by name — letting preview and commit silently disagree otherwise. Symlink entries are
dropped **per-entry**, not treated as a reason to abort the whole archive: collection repositories
publish skills through symlinked directories whose targets are real folders in the same archive, so
dropping the link still finds every skill without ever resolving one.

**Resource names are charset-guarded** (`^(?:[A-Za-z0-9._-]+/)*[A-Za-z0-9._-]+$`, 1–200 chars, no `..`)
because a resource name is model-facing, approval-facing, *and* a log field — a newline could inject
instructions above the reviewed body, and a homoglyph or a right-to-left override could make the import
preview render something other than what actually gets stored. A rejected name is never echoed back into
the report.

**Scripts are detected and refused, never imported** (locked decision) — listed in the report so the
operator can see what was withheld, but no execution surface is added by this feature. File-backed
scripts would need `AgentFileSkill` + a custom script runner + on-disk storage, reversing the DB-only
storage decision in §4.1 as well; it is not a small increment.

Endpoints (`Endpoints/Skills/V1/`, routes under `LocalApiRoutes.Skills`): `POST skills/import/preview`
(multipart, all three sources with an explicit discriminator), `POST skills/import` (report token +
selection + `acknowledged: true`), `GET skills/{id}/resources`, `GET skills/{id}/resources/{name}`. An
export endpoint was deliberately **not** shipped — it would stream decrypted skill bodies as a zip
(exfiltration-shaped) and re-materialise attacker-chosen file names onto the operator's filesystem;
the round-trip is instead proven as an in-process unit test. Skill body/resource content is excluded
from every generated OpenAPI example.

### 4.6 Approval scoping

An assigned skill's `load_skill` call demands operator approval on every single load under MAF's
defaults (§4.3) — tolerable once, but re-approving the same skill turn after turn is exactly the
approval fatigue that trains an operator to click "yes" without reading. Approval now carries a scope,
resolved in `ToolApprovalCoordinator.RequestToolApprovalAsync`:

- **`Once`** — today's behaviour, unchanged.
- **`Session`** — remembered for the rest of the conversation, keyed by
  `ApprovalMemoKey(ConversationId, ToolName, SkillName, SkillVersion, ResourceName)`
  (`TryResolveSessionApprovalKey`, `ToolApprovalCoordinator.cs`). Every field is load-bearing:
  - **`SkillVersion`** binds the approval to *content* — an edit, or a re-import that replaces the
    skill, bumps `agent_skills.version` (§4.1) and silently invalidates the memo rather than letting new
    content ride an old consent.
  - **`ResourceName`** is required for `read_skill_resource`; without it, one approval of one reference
    file would blanket-approve every resource the skill carries, including files the operator never saw
    in the import preview.
  - The memo is **hard-allowlisted to `load_skill` and `read_skill_resource` only** — `run_skill_script`
    can never be remembered, by construction, not by a runtime check.
  - **`Origin.Imported` skills are withheld from session scope entirely** — names in that content are
    attacker-chosen, and a durable approval sitting on a phished skill name is the worst case a memo
    could produce.
  - A node-level `NodeToolApprovalPolicy.SkillSessionScopeDisabled` flag turns the whole mechanism off,
    for an operator who wants a skill tool to prompt every time regardless.
  - A memo-suppressed approval **still writes its audit row** (`SessionScopeApprovalDecision`) — a
    approval that leaves no trace would thin the record of what an agent was allowed to do.
  - Denials are **never** remembered, under any scope.
  - The chat card only OFFERS session scope where the node can honor it. `SessionApprovalEligibility`
    is the one predicate `TryResolveSessionApprovalKey` and the node tool-catalog response
    (`ToolCatalogEntryResponse.SessionScopeEligible`) both read, so `ToolCallCard` hides its "Approve
    for this session" button for every tool that can never carry a memo — an MCP tool,
    `run_in_agent_home`, a `Parameterized` custom tool, or anything at all while
    `SkillSessionScopeDisabled` is on. The catalog answer is a tool-identity UPPER BOUND: the runner
    still applies the per-call narrowings above (imported skill, skill not in the package, unnamed
    resource), which only ever remove eligibility. The `tool-catalog` endpoint reaches both this
    predicate and the effective-approval flag through one `Client.Application` service,
    `ToolCatalogService`, rather than taking the approval policy itself. The MAF skill tools are per-agent and therefore
    absent from the node catalog, so an entry the card cannot find keeps offering the button.

**Unattended runs fail fast, and the check runs before the memo, not after.** A scheduled `run-agent`
job carries `RuntimePackage.IsUnattended` (excluded from the config hash, same posture as
`SupportsThinking`) all the way to `RequestToolApprovalAsync`, which throws
`ApprovalUnavailableException("approval required in an unattended run: <tool>/<skill>")` **immediately**
— before registering a pending approval, before consulting the session memo. Ordering here is
security-critical: checking the memo first would let a future pre-authorisation feature that populates
it become a way to satisfy approvals inside a run with no human in it, which is exactly what the
unattended guard exists to prevent. Before this guard, an unattended run with a pending approval simply
**blocked** for the full `_maxPendingToolCallAge` and then failed with a generic timeout — correct in
that it did not hang forever, but slow and unhelpful about *why*. The guard sits at the one place every
approval-required tool funnels through, so its blast radius is every such tool, not only skills, and
that is intended. `ask_user` is deliberately **not** unified with this behaviour: an unattended approval
*fails* (executing a tool nobody sanctioned is not a safe default), while an unattended question
*continues* with "not answered" (the model asked for input it can proceed without) — see
[Chat](05-chat.md) for `ask_user`.

`Scope` rides `ResolveToolApprovalRequest` (the local endpoint DTO) and the Application-internal
dispatcher only. `ApprovalResolvedEvent` in `AI.Contracts` — the cross-repo SignalR contract the
platform hub also produces — is untouched: session scope is a loopback-only concept the hub cannot
produce, since it has no access to the memo.

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

Per step it: writes a `StepStarted` event and **publishes it before the send** (by the time a step
terminalizes, `InvocationResumeRegistry` has dropped its entry, so a client told only afterwards
re-attaches to an empty stream); composes the state block; drains the stream, mapping
`ApprovalRequested`/`QuestionRequested` onto `WaitingForApproval`/`WaitingForInput` and back; then
settles on the terminal — `complete_work_session` → checkpoint + `Completed`, budget reached →
checkpoint + `Paused`, failure → checkpoint + `Failed`.

Stops never cancel the enumeration. Cancelling it only stops the supervisor watching while the run keeps
going, and the loop would never see its terminal. A pause, a cancel, an unanswered park and a step
deadline all stop a step the way the operator's stop button does: through
`INodeChatStreamCancellationRegistry`, so the pump persists a real `Cancelled` terminal.

Every store write runs in its own scope. The tool handlers write the same session row from inside the
turn, and a `DbContext` held across that carries a stale row version into the supervisor's next write.
Those post-step writes also pass `CancellationToken.None`: a checkpoint and a terminal status are the
record of what already happened, and dropping them because a stop was requested is exactly how a session
ends up left mid-flight by the operation meant to settle it. The loop stops between steps instead.

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

#### A dropped park event

The stream sink is a bounded channel with `FullMode = DropWrite` whose `ChatStreamEventSink.TryWrite`
substitutes exactly **one** `AssistantReconcile` for whatever it drops. A browser repairs that by
re-subscribing for a snapshot; the supervisor has no snapshot to re-fetch, so a dropped
`ApprovalRequested`/`QuestionRequested` used to leave the park unarmed and the step sat until the
node-wide pending tool-call age (10 minutes) rather than `MaxParkedSeconds`.

Whether the lost event was the arming one is not a guess. `ToolApprovalCoordinator.RequestToolApprovalAsync`
registers the call in `PendingToolCallRegistry` **before** it broadcasts the lifecycle event the forwarder turns into
`ApprovalRequested`, so the entry is already there when the substitute reconcile arrives, and its
`InvocationId` is the one `NodeChatStreamService` seeded from the step's `RequestId`. An entry means the
turn really is waiting on a human and the park is armed off the reconcile — with no tool name, which
`ParkedQuestionText` already has a branch for. No entry means the drop had nothing to do with an
approval, and arming would stop a healthy turn on ordinary backpressure.

A step that is **already** parked is left alone: re-arming would only push the deadline later, and
reconciles come from sustained backpressure that produces more of them, so the bound stays the original
park deadline and is never extended.

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

Rebuilding the state block bounds what the model *needs*; it does not bound what the send path *sends*.
A step is an ordinary chat turn, so `BuildConversationContext` replays every earlier step's state block,
answer and **reasoning** verbatim (tool calls and results are not replayed — they live in ordered parts,
never in `Content`), and the transcript grows for the life of the session. Meanwhile the step's own tool
loop is the expensive half: one `read_document` result is capped at 50,000 characters — some 16k tokens —
and `Agent:ToolPipeline:MaxToolResultCharacters` (65,536) is larger than that cap, so nothing clips it.

`ConversationStepContextBound` therefore runs **before every send**: it projects what the next step will
replay using the same `ITokenEstimator` the context budgeters use, and over
`WorkSessions:StepContextBudgetTokens` it forces a compaction of the owned conversation through
`IConversationCompactionService` with a keep window of **2** — one step verbatim, the rest folded into
the synopsis `CompactionContextResolver` already splices. That is safe precisely because the state block
is rebuilt from the database; folding costs the model nothing it still needs.

Its projection mirrors `ConversationContextBuilder.Build` exactly — the same selected-path collapse, anchor space
and completed/non-empty filter — and **counts reasoning even where the provider will drop it**. Verified against
Microsoft.Extensions.AI.OpenAI 10.9.0: the Chat Completions client converts text, URI, data and
hosted-file content only, so a historical `TextReasoningContent` never reaches a llama.cpp session;
only the Responses API client (Codex) replays it, and must. Over-counting a Chat-Completions provider
makes the bound fire slightly early, while under-counting a Responses-API one would make it fire too
late — which is the failure it exists to prevent, so the conservative direction wins and no
suppression seam belongs in the projection.

Compaction cannot touch the other half — the results the step's own tool loop produces *within* the
turn. **Three bounds, not one**, because each catches what the others cannot:

| Bound | Setting | Catches |
|---|---|---|
| Transcript fold at the step boundary | `StepContextBudgetTokens` (12,000) | Growth ACROSS steps |
| Tool-result clip inside the step | `MaxToolResultCharacters` (8,000) | One oversized result |
| Provider-call cap inside the step | `MaxProviderCallsPerStep` (10) | Many results, each already clipped |

The third exists because the second is not sufficient. `FunctionInvokingChatClient` re-sends every prior
tool result **and** every reasoning block on each iteration, so a step's context grows *quadratically in
its own tool calls*: on 2026-08-24 one step made 14 calls (10 × `search_knowledge_base`) whose results
were each correctly clipped to ~16k chars, and the re-sending still reached 71,172 tokens against a
65,536 window. Only capping the iterations reaches that.

Both in-step bounds are seeded by the supervisor as `AsyncLocal` scopes before the enumeration begins,
in the same shape as `AgentRunConversationContext`: `ToolResultBudgetScope` (read in
`BudgetedToolResultAIFunction` — the single wrapper every ClientLocal, Custom and MCP tool routes
through, which is why one edit there bounds all three) and `ProviderCallBudget.BeginCallCapScope` (read
when the runner builds its own budget scope, since that scope replaces any the caller seeded). Both are
**tighten-only**: a value at or above the node ceiling has no effect, so no run can raise it, and an
unseeded flow — every ordinary chat turn — is byte-identical.

**A spent call cap ends the STEP, not the session.** `ProviderCallBudgetExceededException` classifies as
a failure, so the supervisor recognises the budget's own fixed terminal message
(`ProviderCallBudget.CeilingExceededMessage`, forwarded verbatim onto the failed row), writes a
`StepEnded` event with outcome `ProviderCallBudget`, and settles the step as if it had completed. The
tools that ran are already persisted and the state block carries the plan, so the next step resumes the
work. Letting it fall through to the failure branch would end a session on its own safety limit.

`StepEnded` is not the cap's row, though — it is written for **every** step that ends without a fault,
and the OUTCOME is what distinguishes them: `Completed` for an ordinary step, `ProviderCallBudget` for
one the call cap clipped, `ToolGate` for one the allow-list check stopped before it was sent (the only
one of the three whose row carries no consumption detail — nothing ran). A record that existed only when
a bound tripped would measure the bound rather than the work.

The checkpoint's own compaction is not that bound. It lands only every `CheckpointEveryNSteps` steps and
runs *after* a step, never before one, so nothing about it bounds the turn that is about to go out.
Without the step-boundary bound a 27B model at a 65,536-token window overflowed at step 5
(2026-08-24, live research session): the transcript had eaten the headroom the step's knowledge-base
reads needed, and because both context budgeters are estimate-gated at `chars/4` — some 12 % optimistic
for Qwen3 on markdown — the round was passed through as fitting and llama.cpp rejected it with
`HTTP 400 exceed_context_size_error` instead of being trimmed.

### 5.4 The four state tools

`update_work_plan`, `record_finding`, `save_artifact` and `complete_work_session` are
`IClientLocalToolHandler`s, all `ToolCategory.WriteExecute` with `RequiresApproval = false`. They are
held out of the whole chat offer and appended only in `GetOfferedToolsForProfile[Async]`, beside
`spawn_subagent` — the same profile-opt-in seam (**HIGH-1**: registering a handler in DI surfaces it in
the resolution seam only; without the offer merge the seeded personas intersect to an empty tool set).

Each resolves its session from `AgentRunConversationContext.Current` plus a conversation-to-session
lookup — **never from the arguments**, which are model-authored. That is what makes the profile-opt-in
offer safe: a work-session agent bound to an ordinary chat resolves no session and gets four inert
tools. Every guard fails closed to a sentence rather than a throw, because a throw inside the
function-invocation pipeline ends the turn.

`complete_work_session` does not terminalize anything. It appends one event and returns, so the turn
finishes cleanly and the supervisor closes the session after the terminal — which also makes the request
survive a crash between the call and the end of the step.

`save_artifact` writes the **blob first, then the row**. The other order would leave a row pointing at
bytes that never existed; this one leaks at worst a blob bounded by `MaxArtifactBytes`.

> **Consequence of the honest category:** tightening `ToolCategory.WriteExecute` in
> `NodeToolApprovalPolicy` makes all four approval-required, so every recorded finding needs a click.
> Labelling them `ReadLocal` would hide the write from the layer whose job is to see it, which is worse.

### 5.5 Checkpoints, and what a repoint may not do

`WorkSessionCheckpointComposer` writes the structured state (current task, open task ids, key finding
ids — decisions and open questions first) plus the prose synopsis from the **existing**
`IConversationCompactionService`. That one call both bounds the owned conversation's raw history and
produces the summary, so no new summarizer seam exists. Every compaction no-op is non-fatal and the
summary is `string?` end to end: a node with no local chat model produces none, and a placeholder would
be a lie a resumed session reads as fact.

It folds with the **session** keep window (`ConversationStepContextBound.SessionKeepVerbatim`, 2),
not the configured `Agent:ConversationCompaction:RecentMessagesToKeepVerbatim` default of eight. At
eight, a session checkpointing before its fourth step has nothing outside the window to fold,
compaction answers `NothingToCompact`, and the prose half stays null — on
exactly the short sessions whose checkpoint is the only record of what happened. Two is safe for the same
reason it is safe at the step boundary: everything durable is in the state block. The deliberate side
effect is that the fold persists the synopsis and advances the send path's compaction cover, so the step
after a checkpoint resumes on the synopsis plus the last exchange, for one on-node summarizer call.

The synopsis it keeps is **any** non-blank one, not only a freshly folded one. The step boundary folds the
same conversation whenever it grows past the budget, so a checkpoint often finds nothing left to fold, and
that "already covered" no-op returns the synopsis *that* fold produced. Taking only the `Compacted`
outcome would pin the checkpoint to a stale summary, or to none, on exactly the sessions the bound is
protecting. Its event operation id is the checkpoint's own, never derived from the step: a step can take
more than one — the park-timeout checkpoint and the pause checkpoint land at the same step count — and a
step-derived key lets the store's idempotency swallow the second, which is the one recording where the
work actually stopped.

`IWorkSessionService.UpdateAsync` refuses to repoint a session that already holds findings at a
cloud-effective agent unless `KnowledgeBase:AllowCloudModelAccess` is set. The knowledge-base cloud gate
is per turn and acts on the *offer*; it says nothing about text a local model already extracted, which
the state block would otherwise carry off the node on the next step.

Create and repoint also check **both** tool gates, through `WorkSessionToolGate` — one seam shared by the
service and the supervisor so they cannot judge a session differently. The model's own capability probe
(`IModelCapabilityResolver`) and the operator's `AgentHome:ToolCapableModels` allow-list
(`ILocalToolOfferProvider.IsToolCapable`, applied by the offer unconditionally — cloud pins included) are
different sources and are free to disagree, and checking only the first made the failure silent: create
succeeded, the step ran with the four state tools missing from its offer, every `update_work_plan` came
back *"Requested function update\_work\_plan not found"*, and the session spent its whole step budget with
an empty plan. Each gate has its own refusal, because their fixes differ — a different agent versus one
line in Node Settings. The allow-list is re-read live per offer, so it can also change mid-run: the
supervisor re-checks it **before the send** (the allow-list alone — `InspectAllowListAsync`, which skips
the capability probe it would not read) and, rather than sending a turn that cannot work, checkpoints and
settles **`Paused`** with the same sentence, over a `StepEnded` row whose outcome is `ToolGate`.

Paused, not Failed, is load-bearing: `ResumeAsync` accepts only `Paused`/`Interrupted` and a repoint only
`Draft`/`Paused`/`Interrupted`, so a `Failed` session could not be started again *after the operator did
exactly what the refusal asked*. The row gets its own operation-id phase for the same reason — that step
is retried, and sharing the `ended` phase would let store idempotency swallow the real row the retried
step writes. A session whose agent definition has since been deleted is not judged at all — create could
not have judged it either — and a store failure inside the check itself only logs and lets the step
proceed: gate 4 is enforced by the offer, so the guard is advisory, and failing closed would stop a
session over a transient read.

**A Codex- or Azure-pinned agent must be listed by hand.** `ToolCapableModelRegistrar` unions a model
into `AgentHome:ToolCapableModels` only from a locally downloaded GGUF's own template-detected
capability, so it never sees a cloud model id. Pinning an agent to one and creating a session against it
is refused until an operator adds that id under **Node Settings → Tools**, and the refusal says so.

### 5.6 Settings

| Key | Default | Note |
|---|---|---|
| `WorkSessions:Enabled` | `true` | Shipped in `appsettings.json`; gates behaviour, never registration |
| `WorkSessions:MaxStepsPerRun` | `25` | Per start/resume, not per lifetime |
| `WorkSessions:CheckpointEveryNSteps` | `5` | |
| `WorkSessions:MaxConcurrentSessions` | `1` | Admission cap — see §5.2 |
| `WorkSessions:MaxParkedSeconds` | `300` | Must stay under the node's `WorkerNode:MaxPendingToolCallAgeMinutes` — `WorkSessionOptionsValidator` checks it at startup against the configured seed; a stored Node-Settings override is not covered |
| `WorkSessions:MaxArtifactBytes` | `1048576` | 1 MiB |
| `WorkSessions:StepTimeoutSeconds` | `0` | 0 inherits the node's maximum message request timeout |
| `WorkSessions:StepContextBudgetTokens` | `12000` | Replayed-transcript budget per step; over it the boundary force-compacts (§5.3). 0 disables |
| `WorkSessions:MaxToolResultCharacters` | `8000` | Tightens the node's tool-result budget for a step (§5.3). Tighten-only; 0 leaves the node value |
| `WorkSessions:MaxProviderCallsPerStep` | `10` | Tool-loop iterations per step (§5.3). Hitting it ends the step cleanly; 0 leaves the node value |

**Where the three context numbers come from.** `StepContextBudgetTokens` is deliberately a flat budget rather than a
fraction of the model's context window: what consumes a research step is its own tool loop — a single `read_document`
is capped at 50,000 characters, some 16k tokens — so the transcript's job is to stay out of the way and the state
block, rebuilt from the database on every step, is what carries the session's state forward. The default leaves the
large majority of a 64k window to the step itself. `MaxToolResultCharacters` tightens a node ceiling that is already
larger than `read_document`'s own cap, so nothing clips a single knowledge-base read today; *several* of them in one
research step is what overran a 64k window. The default of 8,000 (~2–2.5k tokens per result, at the ~3.4–3.6
chars/token this corpus actually runs) clips a full-size read to about a sixth of itself and still leaves a step room
for several. `MaxProviderCallsPerStep` exists because the function-invocation loop re-sends every prior tool result
and every reasoning block on each iteration, so a step's context grows **quadratically** in its own tool calls — 14
calls in one step overran a 65,536-token window with each individual result already clipped. Neither the step-boundary
fold nor the per-result cap can reach that; only a cap on the iterations can. Its default of 10 was a guess meant to be
replaced by a measurement, not by another guess: size it from the distribution of the `StepEnded` / `StepFailed`
consumption rows (§5.7) for the session kind in question.

`IWorkSessionSandboxRuntimeProvider` exists as a role marker with **no consumer in v1**: nothing a
session tool does needs a jail yet, and the role is there so the first one that does gets a per-feature
provider choice rather than a new registration to keep correct.

### 5.7 The per-step consumption record

Every `StepEnded` / `StepFailed` row carries `WorkSessionStepConsumptionDetail` — counts plus a bounded set of tool
**names**, never a prompt, model output, tool argument or tool result — so the per-step provider-call cap can be sized
from what steps actually consume. It is written for *every* such step and not only the clipped ones: a record that
exists only when a bound trips measures the bound rather than the work.

The names are on the row because it is the only **durable** carrier they have. The scope they are collected in is
disposed at the end of the step that seeded it, and anything asking later — a Dev Workflow node run settling on a later
dispatcher tick, in another scope and possibly another process — can read only what was persisted. A name is an
identity, not content: a fixed id for a built-in tool, an operator-authored identifier for an MCP or custom one.

Three things decide how the numbers may be read.

- **Step totals, not turn totals.** Every member is read off the step's own cap scope, which is why the provider's own
  reported token usage is not among them: that is a *turn* number — the last round's counts on the message, the rounds'
  sum on the run envelope — and a step is not the same denominator as a turn. Estimate-versus-truth is measured per
  round instead, where both halves describe the same request, by `ProviderCallBudgetChatClient`'s observed-usage
  write-back into the calibration store.
- **`ProviderCalls` is a ratio against `ProviderCallCap` only while `AttachedBudgets` is 1.** The cap bounds each
  invocation separately, and a step that spawned sub-agents ran more than one — eighteen calls across two budgets is
  two runs that each stayed under ten, not one run that breached it. Read the two together, or the record argues for
  raising a cap nothing hit.
- **The row is per step *number*, not per attempt.** The supervisor derives its event operation id from the session and
  the step, so a step replayed after a crash finds the first attempt's row already recorded and the store's idempotency
  returns it unchanged. The numbers are what the *first* attempt spent; the replay's own spend is added nowhere.
  Aggregate these rows as a lower bound on what a session cost, never as an exact total.

A step stopped through the cancellation registry — paused, cancelled, an expired park, a blown deadline — writes no row
at all, deliberately: the run may still be unwinding when the supervisor sees its terminal, so its counters would be a
race rather than a measurement. `ToolSchemaTokens` counts schema tokens **shipped across rounds**, so it grows with the
round count and is not the size of the offer. `ToolNames` is trailing and optional: `null` means the row predates the
member, never that the step called no tools — `ToolCallsCompleted` answers that.

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
  single opt-in `KnowledgeBase:AllowCloudModelAccess` covers knowledge tools, file tools, and attachments.
  Attachment content that does reach a model is fenced as untrusted data with a server-secret-derived
  nonce (client cannot forge the fence). See [Knowledge Base](15-knowledge-base.md) and [Security & Privacy](12-security-and-privacy.md).
- **Privacy-sensitive ops are node-local only.** Playbook analysis (P3), the eval gate (P4), and memory
  extraction all run on node-local models — never cloud. See [Security & Privacy](12-security-and-privacy.md).
- **Spawn is bounded.** Depth cap (child omits the tool), per-root fan-out lease, and a cloud-spawn
  wallet cap are all enforced in `SubAgentSpawnService`.
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
