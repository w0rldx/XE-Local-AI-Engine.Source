# Agents, tools, cloud providers and MCP-only mode

Scope: Agent Mode, MAF/MEAI wiring, tool calling and approval, cloud providers, chat state, workflow engines,
and the `--mcp-only` launch. Read when: touching `AI.Agent`, tool invocation or approval, a cloud or MCP provider,
`Services/GraphWorkflows` / `Services/DevWorkflows`, `LaunchMode.McpOnly`, or bumping the MAF / MEAI / `OpenAI` packages.
Sandbox, compute and AgentHome containment: [sandbox-and-compute](sandbox-and-compute.md).

## Tools, approval and invocation

### Sub-agent spawn: depth cap is structural first

**Rule:** strip `spawn_subagent` and every `ApprovalRequiredAIFunction` from child tools unconditionally (drop wrappers, never unwrap into auto-execution); the depth check is defense-in-depth. Skills are the one exception: `SubAgentSpawnService.ChildConfiguration.AttachSkillsProvider` (via `InvocationSkillsProvider.CreateForSubAgentChild`) sets `DisableLoadSkillApproval`/`DisableReadSkillResourceApproval`, because those tools bypass `CurateChildTools`; `run_skill_script` stays gated. Children never spawn. **Prevents:** a child escaping approval or spawning recursively. **Authority:** `SubAgentSpawnService`, `SubAgentSpawnServiceTests`.

### Sub-agent spawn: a child is bound once, at construction

**Rule:** bind the child (`ChatOptions.ModelId`, reasoning via `ParticipantReasoningOptions.Build` on the child model, `AgentSkillsProvider`) from one `ResolvedAgentRuntime`: `AsAIFunction` gets no per-run options, and its input is `query`. A child inherits injected playbook memory but gets no post-run memory extraction; a model-id-only child keeps the raw request instructions with no reasoning or skills. **Prevents:** a child assembled from two definition versions, or per-run options silently ignored. **Authority:** `SubAgentSpawnService.Binding`, `SubAgentSpawnServiceTests`.

### Tool-approval policy: the enforcement seams are plural

**Rule:** apply tighten-only policy projection through `IToolApprovalPolicy` at every offer seam: `AgentDefinitionResolver.ProjectAllowedTools` (incl. its mode-off early return), `OrchestrationResolver.ProjectAllowedTools`, and the deleted-agent `resolved == null` fallback in send/regenerate; `InvocationToolResolver` only adds wrappers; Unknown fails closed. Session-scope memo (`SessionApprovalEligibility`) is identity-specific: only packaged non-imported `load_skill`/`read_skill_resource` and version-bound Fixed custom tools; never `run_skill_script` or Parameterized tools. Every query over `agent_execution_logs` filters `record_kind`. **Prevents:** a missing projection silently bypassing policy; approval rows misread as usage. **Authority:** the named resolvers, `SessionApprovalEligibility`.

### A tool handler can NEVER block waiting for a human

**Rule:** `StreamIdleWatchdog.WithIdleTimeout` (60 s) wraps the whole streaming call, tool execution included, so a human wait must end the segment with `ToolApprovalRequestContent` (via `ApprovalRequiredAIFunction`) and resume in the runner's approval loop. `ask_user` approval is structural. Children, schedulers and delegate-scope MCP strip approval-required tools; only agentic-scope inbound root runs may auto-answer, and only after the audit write succeeds before invocation. **Prevents:** a blocked handler tripping the idle watchdog. **Authority:** `StreamIdleWatchdog`; ADR 0006.

### A pending chat approval does not survive a restart, and that is the accepted behaviour

**Rule:** `PendingToolCallRegistry` is a per-process dictionary; a restart drops parked approvals and `NodeChatRestartRecoveryService.RecoverInterruptedMessagesAsync` forces `Pending`/`Queued`/`Streaming` assistant rows to `Interrupted` (backfilling the run-envelope audit row). Accepted product behaviour (operator ruling, ledger D5), not a bug. Graph Workflows is the deliberate exception: `GraphWorkflowStartupReconciler` leaves `WaitingForApproval` rows alone and `IGraphWorkflowRunService.DecideAsync` resolves them idempotently. **Prevents:** filing chat's behaviour as a bug, or copying the durable graph wait into chat without a ruling. **Authority:** the three types above; ledger D5.

### A tool handler that throws does NOT end the turn on Microsoft.Extensions.AI 10.9.0

**Rule:** a throw does not end the function-invocation loop; the model gets another round with the handler's message replaced by the fixed `Error: Function failed.` (`IncludeDetailedErrors` is `false`), so it retries blind. Return a message instead of throwing when the model should see why. Measured at MEAI 10.9.0; the pin is now 10.10.0, re-verify on bump. **Prevents:** reasoning that throw-vs-return is about turn lifetime. **Authority:** standalone probe against the pinned package; `EmitOutputToolHandler.ExecuteAsync`. [evidence](../agent-knowledge-evidence.md#a-tool-handler-that-throws-does-not-end-the-turn-on-microsoftextensionsai-1090)

### Tool invocation is sequential and blind on purpose, and both flags are pinned in code

**Rule:** the `UseFunctionInvocation` callback in `AgentServiceCollectionExtensions` sets `AllowConcurrentInvocation = false`, `IncludeDetailedErrors = false`, `MaximumConsecutiveErrorsPerRequest = 3`, `TerminateOnUnknownCalls = false` explicitly. `ToolArgumentRepairScope`, `ToolResultBudgetScope`, `ProviderCallBudget.Current` and `ToolInvocationObservabilityChatClient`'s `CallId` pairing are `AsyncLocal` per-request state never shown safe under concurrent calls; detailed errors stay off for privacy. Enabling concurrency needs each scope audited and a measurement, not a flag flip. **Prevents:** flipping them for latency, or a bump changing a default silently. **Authority:** `AgentToolPipelinePolicyTests.MultipleFunctions_AreInvokedSequentially`, `HandlerExceptions_AbortAtPinnedConsecutiveErrorLimit`.

### A blank tool-call id is the only id-less shape Microsoft.Extensions.AI 10.9.0 can hand you

**Rule:** `FunctionCallContent`/`FunctionResultContent` reject a null `CallId` in the constructor and it is read-only, so an id-less provider streams `""`; a `callId ?? name` fallback or null guard is unreachable. `InvocationRunner.RunSingleAgentAsync` mints surrogate ids for a blank id: bare tool name, then `<name>#N` for overlapping or later same-name calls. Measured at MEAI 10.9.0; pin is now 10.10.0, re-verify on bump. **Authority:** `InvocationRunnerTests.RunAsync_WhenTheProviderStreamsTwoSameNameCallsWithABlankCallId_PairsEachResultWithItsOwnCall`. [evidence](../agent-knowledge-evidence.md#a-blank-tool-call-id-is-the-only-id-less-shape-microsoftextensionsai-1090-can-hand-you)

### A second copy of the tool-invocation logic drifts, and the drift looks like a product bug

**Rule:** anything that executes a tool for real calls `IToolInvocationService.InvokeAsync`; a caller may add its own offer composition or envelope shaping, never its own resolve/validate/call/classify chain. **Prevents:** the drifts the training path's `HeadlessToolExecutor` accumulated: skipping `IClientLocalToolRegistry` (worker-owned tools like `read_file` failed) and logging a `ToolArgumentRepairAIFunction` repair envelope as a successful dataset sample. **Authority:** `HeadlessToolExecutorTests`; `ToolInvocationService.TryAdmit` is the same seam the Graph Workflow Tool gate reads. [evidence](../agent-knowledge-evidence.md#a-second-copy-of-the-tool-invocation-logic-drifts-and-the-drift-looks-like-a-product-bug)

### Persisted chat parts are a render/reload record, not model context

**Rule:** a `NodeChatMessagePart` reaches the model only via `ConversationContextBuilder.Build(includeToolHistory: true)` → `ConversationMessageDto.ToolExchanges` → `InvocationRunner.BuildChatMessages`, requested only by the integration execution coordinator for a `CallerManaged` session, and only off the FULL read (`GetConversationAsync`). `GetConversationForTurnAsync` blanks `metadata_json` for compaction-covered rows, so a replay fed by it is empty after compaction. Normal chat never replays parts. **Prevents:** assuming a continued turn sees its own tool calls because the SPA renders them. **Authority:** `IntegrationContinuationTests.ACallerManagedContinuationReplaysTheCallItsResultAndThenTheTurnsText`, `NodeChatTurnReadCapTests`. [evidence](../agent-knowledge-evidence.md#persisted-chat-parts-are-a-renderreload-record-not-model-context)

## MAF, MEAI and package cohorts

### MAF traps

- `ChatClientAgentOptions` has no `Instructions`. Instructions go once via the System seed; pass null to the ctor's `instructions`, use named arguments (positional order moved once), assert containment (skill preambles).
- Fakes inspect messages and `options.Instructions`.
- `AgentSkillsProvider` needs scoped `MAAI001` suppression.
- Log tool args/results as length + 12-hex SHA-256 prefix only.
- `Executor<TInput>` yields only `YieldsOutputTypes`; `Run.NewEvents` drains on read (`WorkflowBuilderProbeTests`).
- `FileMemoryProvider`/`file_memory_*` tools are ungated: wrap before wiring. [evidence](../agent-knowledge-evidence.md#maf-traps)

### MAF traps: approval is a wrapper, and runs stay sessionless

**Rule:** wrapping in `ApprovalRequiredAIFunction` is the gate; middleware alone gates nothing. Sessionless runs are deliberate (a resume accepts forged approvals because MAF keeps no consumption ledger); keep `ApprovalResponseValidatingAgent`. Do not re-open this because session serialization round-trips: the gap is the missing ledger. **Prevents:** an ungated tool, or a replayed approval executing twice. **Authority:** `FrameworkApprovalGateTests`, `BudgetedApprovalReplayTests`.

### The four `gen_ai.tool.*` attribute names track MEAI, and two of them carry a digest rather than the payload

**Rule:** `ToolInvocationObservabilityChatClient` tags `gen_ai.tool.call.id`, `gen_ai.tool.name`, `gen_ai.tool.call.arguments`, `gen_ai.tool.call.result`, never `gen_ai.operation.name` (MEAI's `FunctionInvokingChatClient` owns `execute_tool`). `.arguments`/`.result` carry the length + SHA-256-prefix digest, never the payload. The names are OTel-Deprecated but are what MEAI emits (read at 10.9.0; re-verify on bump). `GenAiErrorDescriptionRedactionProcessor` rewrites MEAI's `error.Message` span description to `error.type`. **Prevents:** payload leaks, double-counted executions, renaming off MEAI. **Authority:** `ToolInvocationObservabilityChatClientTests`, `AgentToolPipelinePolicyTests`, `GenAiErrorDescriptionRedactionProcessorTests`. [evidence](../agent-knowledge-evidence.md#the-four-gen_aitool-attribute-names-track-meai-and-two-of-them-carry-a-digest-rather-than-the-payload)

### A MAF-trio bump above patch level is not merged on a green build alone

**Rule:** `Microsoft.Agents.AI`, `.Abstractions` and `.Workflows` move as one cohort in `Directory.Packages.props`. A patch bump may land on CI alone. Any minor bump, or any bump moving `.Workflows`, needs before merge: `HandoffWorkflowSpikeTests`, `FrameworkApprovalGateTests`, `AgentSkillsProviderContractTests` run and named; the O1 workflow-backbone axes re-run and the verdict re-recorded; the MAF traps above re-read and corrected. **Prevents:** a silent positional-ctor reorder and O1's premise going stale unnoticed. **Authority:** ledger D13, operator ruling. [evidence](../agent-knowledge-evidence.md#a-maf-trio-bump-above-patch-level-is-not-merged-on-a-green-build-alone)

### `Microsoft.Extensions.AI.OpenAI` and the plain `OpenAI` SDK move together, and NuGet will not tell you

**Rule:** treat `Microsoft.Extensions.AI.OpenAI` and `OpenAI` as one cohort; raise `OpenAI` only to the version the adapter release was built against, never merely to what its nuspec floor allows. A too-new `OpenAI` compiles clean and throws `TypeLoadException` on every Responses call carrying tools (the whole Codex tool loop). Diagnosis tell: the request never reaches the transport; unwrap the exception before blaming the wrapper. **Authority:** `CodexToolCallingWireTests.FirstTurn_WithTool_WrapperAddsEncryptedReasoningInclude_AndKeepsTool`; the reason is recorded on the `OpenAI` pin. [evidence](../agent-knowledge-evidence.md#microsoftextensionsaiopenai-and-the-plain-openai-sdk-move-together-and-nuget-will-not-tell-you)

### `RuntimeChatClient.GetService` forces the three cloud chat-client factories synchronous

**Rule:** `ActiveCloudChatClientFactory.ResolveSnapshot`, `AzureFoundryChatClientFactory` and `CodexOAuthChatClientFactory.Create` block on `GetAwaiter().GetResult()` because `RuntimeChatClient.GetService(Type, object?)` (an `IChatClient` member with no async overload) reaches them through the same `ResolveActiveClient` path as the two async calls. Those sites carry `#pragma warning disable MA0045` with that reason; do not make `Create` async without first moving `GetService` off the shared path. **Prevents:** a half-async chain that still blocks, or an unreviewed change on a credential/token path. **Authority:** `RuntimeChatClient` (`GetResponseAsync`, `GetStreamingResponseAsync`, `GetService` → `ResolveActiveClient`).

## Cloud providers, MCP and hubs

### Cloud providers: Codex

**Rule:** fold System messages into `ChatOptions.Instructions` only at the wrapper. Overwrite `ModelId` with the resolved Codex id (never a local name); clear incompatible output settings. Reasoning uses the Codex side channel (UI Highest = SDK High). Force `store=false`; keep `ResponseItem` raw items so encrypted reasoning replays. **Authority:** `CodexToolCallingWireTests`.

### Cloud providers

**Azure / Entra:** route per request by `ChatOptions.ModelId` (Azure deployment > Codex > default > local), never by connection presence. APIM needs an operator-added host suffix; never weaken `AzureFoundryEndpoints`' suffix allowlist to fix an auth failure. Bearer auth goes in `OpenAIClient(AuthenticationPolicy, options)` (a per-call policy is overwritten by API-key auth); assert final wire headers via a request-capturing transport, never DI. App-only tokens carry `roles`, not `scp`; delegated auth is MSAL confidential-client. Walk inner exceptions for AADSTS codes. **Authority:** `AzureFoundryEndpoints`, `AzureFoundryV1PipelineExecutionTests` (capturing transport). [evidence](../agent-knowledge-evidence.md#maf-and-cloud-incidents)

### MCP transport and skill import

**Rule:** HTTP MCP connect-time validation requires both `IsHttpScheme` (only `http`/`https`) and `IsLoopbackHost` (exact ordinal match in `McpOptions.HttpLoopbackHosts`), kept over CRUD validation; never replace the list with `IPAddress.IsLoopback`. The inbound `McpServer` policy lists only `McpApiKey`, and `MapMcp` stays inside `/api/local/v1` (`LocalApiSecurityMiddleware` is prefix-based). Optional tool parameters need C# defaults; nullability does not remove them from `required`. `GitHubSkillArchiveDownloader`'s real HEAD.zip redirect is a pre-RC manual check. **Prevents:** metadata addresses, userinfo, rebinding suffixes or alternate IPv6 spellings passing the boundary. **Authority:** the named predicates.

### SignalR does not replay to late joiners

**Rule:** for push hubs, assign a per-run monotonic `Seq`; keep a bounded replay buffer outside the live-run dictionary for a short retention window (a fast run can finish before HTTP returns the id to subscribe with); join the group before replaying; dedupe client-side with high-water mark + gaps. Cancel publishes a terminal directly. **Prevents:** lost events for late subscribers, and UI stuck running behind a model call that never unwinds.

### A live-audio hub method's size cap is application-level, not the SignalR default

**Rule:** the node's SignalR `MaximumReceiveMessageSize` is 512 KB (`ConfigureServices.cs`), not the 32 KB default, so a per-frame bound is enforced in the hub method (`TranscriptionHub.MaxFrameBytes`) and tested as a unit test of that method, not of the transport. **Prevents:** an oversized frame accepted silently. **Authority:** `TranscriptionHubTests`.

### Chat message status is a table-enforced state machine

**Rule:** `NodeChatMessageTransitions` is the sole allowed-source table, applied inside one SQL `UPDATE … AND status IN (...)`; never read-then-write. Cancel/flush/recovery only from non-terminal. Queued follows pending; streaming follows pending (platform) or queued (local). If a lifecycle mark loses, send/regenerate returns the persisted terminal and never invokes the model. Sources derive from the TARGET: `Interrupted` never overwrites `Cancelled`; a true completed/failed/cancelled may supersede an optimistic `Cancelled`. Run envelope and SSE use the persisted winning status. **Authority:** `NodeChatMessageTransitions`.

## Workflow engines

### Graph Workflows: a cap has to be enforced BEFORE the work it bounds, not after it

**Rule:** both parsers refuse on the declared `nodes` array length before reading a node: `GraphWorkflowGraph.Parse(graphJson, maxNodes)` (200, 1 MiB body) and `DevWorkflowGraph.Parse(graphJson, maxNodes)` via `DevWorkflowGraphContract.ValidateAndCountNodes` (`DevWorkflowOptions.MaxNodesPerDefinition` 500; `DevWorkflowRequestSizeLimit` 2 MiB on create/update). Cycle and ancestor walks (`EnsureAcyclic`, `AncestorsFirst`) use an explicit stack. Re-parses of a stored graph (and `DevWorkflowDefinitionSeeder`) stay uncapped on purpose. **Prevents:** a deep chain overflowing the stack (a process kill no `catch` sees). **Authority:** `GraphWorkflowGraphContractTests`, `DevWorkflowGraphTests.Parse_WithAChainDeeperThanTheStackWouldCarry_IsWalkedWithoutRecursion`. [evidence](../agent-knowledge-evidence.md#graph-workflows-a-cap-has-to-be-enforced-before-the-work-it-bounds-not-after-it)

### Graph Workflows: the response-schema warning mirrors a THIRD-PARTY transform, so it drifts on a package bump

**Rule:** `GraphWorkflowGraph.ResponseSchemaWarnings` (`DroppedSchemaKeywords`, `NestedSchemaMembers`) hand-copies the `Microsoft.Extensions.AI.OpenAI` strict-schema transform: re-read the adapter's keyword list and descent members on every bump of that package. **Prevents:** a warning that lies in whichever direction upstream moved, false or missing. **Authority:** `GraphWorkflowGraphTests`, `GraphWorkflowValidateEndpointTests`; mechanism in `docs/wiki/21-graph-workflows.md` §2.4.

### Graph Workflows: `GraphWorkflowCondition.Order` and `DevWorkflowCondition.Order` are a knowingly duplicated pair

**Rule:** a change to either comparison ladder is a change to both: edit both files and run `GraphWorkflowConditionTests` and `DevWorkflowConditionTests`. There is no shared evaluator by decision (graph-core extraction shelved), not debt. **Prevents:** fixing one engine and shipping the other wrong with both suites green. **Authority:** operator ruling R7-1; the "Mirrored in GraphWorkflowCondition.Order … change both" comment in `DevWorkflowCondition`.

### Dev Workflows has a runtime reference and a separate divergence register

**Rule:** the runtime reference is `docs/wiki/25-dev-workflows.md`; differences from Graph Workflows are in `docs/wiki/22-workflow-engines-divergence-register.md`; implementation contracts are the XML docs on `DevWorkflowGraph`, `DevWorkflowStateMachine`, `DevWorkflowOptions`. **Prevents:** acting on the superseded no-wiki-page restriction, or treating the register as the runtime reference. **Authority:** `docs/wiki/Home.md`.

### A fix in one workflow engine is not automatically engine-local

**Rule:** before changing behaviour in `Services/DevWorkflows/` or `Services/GraphWorkflows/`, read `docs/wiki/22-workflow-engines-divergence-register.md` for the sibling engine's equivalent. The engines were copy-adapted, not shared; most differences are deliberate, but the register also tracks unexamined gaps, and it, not assumption, says which is which. **Prevents:** fixing a restart-recovery, approval or retry defect in one engine and leaving it in the other. **Authority:** the register; operator ruling D10 (write the register, defer convergence).

## Agentic support / MCP-only mode

### `--mcp-only` is a local mode, not a second host

**Rule:** treat `LaunchMode.McpOnly` like Desktop for desktop-only endpoints, local data, provisioning, lease, loopback security and shutdown; a Desktop-only check drops endpoints in the external-agent launch. `DesktopLifecycle` emits one `XE_READY=1` line in exact key order plus canonical `ready.json`; installers read it by prefix and key order, so never wrap or reformat it. **Authority:** `LaunchMode.McpOnly`, `DesktopLifecycle`.

### Agentic authority is explicit

**Rule:** `delegate` and `agentic` share one key row; a mint replaces key and scope atomically (no dual-valid window, no per-call negotiation). Agentic authority travels through `McpInboundExecutionContext`, fingerprints, admission and durable execution, never `AsyncLocal`, and persisted runs keep their captured authority across restart/key rotation (never re-derive from the current key row). It grants only enumerated inbound MCP capability, never Operator JWT/REST. A root approval-required call needs a successful metadata-only audit write first; child curation is unchanged. **Authority:** `McpInboundExecutionContext`; ADR 0006.

### MCP reference is executable documentation

**Rule:** `McpToolsReferenceDriftTests` reflects tool sets, counts and scopes and compares them with the first columns of `skills/xe-local-ai-engine/references/mcp-tools.md`: a tool rename or scope move updates code and table in one change. `skills/xe-local-ai-engine/` is the one skill source; never duplicate or symlink it into a per-user agent skill directory (installers copy the versioned tree; copies break Windows archives and drift). **Authority:** `McpToolsReferenceDriftTests`.

## Covered elsewhere

- `pythonTests`: the process computing the verdict must never execute the graded code — `docs/wiki/20-benchmarks.md` ("5.2.3 `pythonTests` — execution scoring"; a child-named exception re-raises only as a builtin `Exception` subclass, never via `BaseException`, `sys.modules` or `eval`); run-python refusal, venv binds and egress in `docs/wiki/19-compute-tools.md` and `docs/wiki/12-security-and-privacy.md`
- Graph Workflows: `Parallel` and `Join` are labels, not semantics — `docs/wiki/21-graph-workflows.md` ("2.3 `joinPolicy` is on every node")
- Graph Workflows: an edge condition reads the SOURCE node's output, never the target's input — `docs/wiki/21-graph-workflows.md` ("2.2 Edges and conditions", "4.4 `Condition`")
- Graph Workflows: `Pause` parks a node run in `WaitingForApproval`, and there is no `Blocked` state in v1 — `docs/wiki/21-graph-workflows.md` ("3.3 Node-run statuses and admission", "4.6 `Pause`")
- Graph Workflows: a PARKED run holds no concurrency slot, and a resuming run does not re-pass admission — `docs/wiki/21-graph-workflows.md` ("3.4 The tick")
- Graph Workflows: a steer is intent the service commits and the TICK applies; same-node only — `docs/wiki/21-graph-workflows.md` ("3.7 Steer")
- Graph Workflows: a `Tool` node passes TWO gates, and the run-start check is the one that wins — `docs/wiki/21-graph-workflows.md` ("4.3 `Tool`")
- Graph Workflows: a node's `input` is its ONE satisfied predecessor's output, so a node inserted mid-chain REPLACES the content — `docs/wiki/21-graph-workflows.md` ("4.6 `Pause`", "9.2 The mapping")
- an endpoint constructor may only take `Client.Application` / `AI.Contracts` / `Providers.Abstractions` / host / BCL types — enforced by `XE-Local-AI-Engine.Tests/Architecture/EndpointDependencyTests.cs`; service shape in `docs/wiki/16-code-conventions.md`. When moving an endpoint body into a service, register it at the lifetime of what it wraps and move `HandleAsync` verbatim: a rewrite changes behaviour the untouched endpoint tests miss

## Stale beliefs

Superseded claims; the entries above are the active rules.

| Stale belief | Current correction |
|---|---|
| Open Canvas / Preview is the visual workflow builder. | Removed in favour of Graph Workflows; saved canvases were imported once at startup (§4). |
| Context management is truncation only; no cross-turn LLM summary exists. | `ConversationSummarizer` + `ConversationCompactionService` fold older turns into a synopsis on a node-local model, manually via `POST chat/conversations/{id}/compact` and automatically in work sessions (§6). |
| Conversation history always replays verbatim. | Two budgeters excerpt/drop history (§6). |
| Protected recent turns are immutable. | Late budget passes may strip reasoning; protected tool-result excerpting is opt-in (§6). |
| Playbook retrieval is lexical by design. | Embedding ranker is default; lexical is fallback (§6). |
| Skill names only reject edge hyphens. | MAF validation also rejects consecutive hyphens; use its validator. |
| Skills are instructions-only. | Resources/assets are persisted with skill-bound AAD; scripts remain refused. |
| Assigned skills automatically work in children. | Skill read/load approval is waived only for children; script execution remains approval-required (§4). |
| The graph-workflow run GET carries no graph, so a run view draws the definition's. | `GraphWorkflowRunResponse` carries the run's PINNED graph; the run view draws that and a definition edited since is only a notice (§4, wiki 21 §5). |
| A run view falls back to nodes-only whenever `run.graphHash` and `definition.graphHash` disagree. | Only a response carrying no graph at all falls back; a hash mismatch alone is informational (§4). |
| An authored `Agent → Pause → Agent` chain has no affordance and only the importer adds the context edge. | The editor adds it on the connect gesture and the validator warns about the starved node (§4). |
| `WorkSessionAgentSeeder` allow-lists the clock tool as `get_current_time`. | The registry derives the name from the method: it is `GetCurrentTime` (§4, wiki 19). |
| The Open Canvas importer's pause context edge follows a looser rule than the editor and the validator. | All three apply the same rule and the same three guards; only WHEN they run differs (§4, wiki 21 §9.2). |
| `MaxNodesPerDefinition` is applied after the parse, so the parser needs no cap. | `GraphWorkflowGraph.Parse` takes the cap and refuses on the declared node count before anything is read (§4, wiki 21 §2.4). |
| Dev Workflows accepts a definition of any size, and the node cap there is a known-unfixed finding. | Fixed in S7: `DevWorkflowOptions.MaxNodesPerDefinition` (500) is enforced inside the parse and `DevWorkflowRequestSizeLimit` caps create and update at 2 MiB (§4). |
