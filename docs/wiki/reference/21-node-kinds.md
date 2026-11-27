# Graph Workflow Node Kinds

> Reference page for [Graph Workflows](../21-graph-workflows.md) §4 · Code-grounded.

It lists every node kind's config members, validation, execution lane and output document. The output envelope, the input document and the lane split common to every kind stay in the parent page's §4.

### 4.1 `Start`

Config: `inputSchema`, `defaultInput` — both optional raw JSON. `inputSchema` is authoring metadata in v1 (the run's input
is validated for size, not against the schema). `defaultInput` is the run's input when a start carries none
(`GraphWorkflowRunService.StartAsync` applies it before the size cap, so an API or trigger start behaves like the SPA,
which pre-fills it); an explicit input always wins.

`aliases` is optional: 1 to 32 strings of 1 to 120 characters, trimmed, distinct once normalised. They are the
phrases the intent router matches this definition on (§11). Authoring metadata only: they travel inside the graph, so
export and import carry them, and they grant nothing; a definition is reachable by intent only while its trigger
sources include `Intent`. A bad list is a validation error keyed by the Start node.

Output: `{ "input": <the run's start payload> }`, handed to everything downstream. Who started the run is run
metadata (§3.1), not part of this output.

### 4.2 `Agent`

One headless saved-agent turn, on the model-invocation lane shared with LLM Call and sized at `MaxConcurrentRuns`
(`GraphWorkflowInvocationExecutor`). It drives `IInvocationRunner` from the tick and never inside it; the turn is an
in-process task with no durable handle, which is exactly why an interrupted `Running` row is failed rather than
resumed (§3.5). `InvocationId` is written on the row as the correlation id in the node logs for a turn nothing else
survives.

The turn's contents come from `RunSavedAgentHandler`, the node's other unattended caller of this stack, and five of
its rules carry over: the locality gate runs **before** capacity, the capacity reservation is disposed on every
terminal path, approval-required tools are stripped from the offer (and the web tools by name, §4.3), `IsUnattended` is set, and the terminal state is
read off a `StrongBox<T>` the state-changed handler fills rather than off a return value the runner does not have.
The executor is a **singleton** — the lane and its slot count are the node's and outlive both a tick and a DI scope —
so every scoped collaborator is resolved inside the task body from its own scope: the scope the tick handed the store
is long gone by the time the turn lands.

| Config member | Meaning |
|---|---|
| `agentDefinitionId` | Optional saved agent to bind. Null runs an unbound persona from `instructions` alone. |
| `instructions` | **Required.** The seed user turn. |
| `model` | Optional model pin. Travels as written and is matched against the catalog **at run start**, exactly as an agent definition's own pin is, so a graph does not become unsaveable because a model was uninstalled after it was authored. |
| `reasoningEffort` | Optional, one of `none`, `low`, `medium`, `high`. Checked at **save** — its vocabulary is closed and cannot go stale. |
| `responseJsonSchema` | Optional. Must be an **object** schema (`"type": "object"`), because the parsed answer lands at `output.json` and a condition reads a property off it. |
| `includeUpstreamOutputs` | Defaults **true**. Inlines the `upstream` map into the prompt, budgeted at `MaxRunInputBytes` and truncated with an explicit marker rather than silently. |
| `repository` | Defaults **false**. Points the bound agent's `list_files`, `read_file` and `search_text` at the Development project the run input names in `developmentProjectId`, read-only, instead of the AgentHome workspace. Refused at **save** without an `agentDefinitionId`; the run-time refusals follow below. |

**A `repository` node reads the run's Development project.** The run input carries the project as
`{ "developmentProjectId": "<guid>", … }`; the executor reads it from `run.input` in the node's input document (the run's
stored input) and resolves the root through `IDevelopmentRepositoryBindingService.ResolveProjectAsync`, then opens
`CoderRepositoryScope.Begin(root)` around the invocation, the scope the repository tools read. The scope is an
`AsyncLocal` that reaches the tool handlers the framework calls from inside the runner's tool loop
(`GraphWorkflowAgentRepositoryTests`). Each of these refuses the node run `ValidationFailed` before capacity, naming the cause and
never a host path: a cloud effective model without **Let cloud models read local data** (`AllowCloudModelAccess`) or a
local-data grant on its connection (a repository is node-local data); a bound agent that lists none of the three tools;
a run input with no `developmentProjectId`; Development Mode off; a binding that does not resolve (reconnect the project
on the Development page). Without the option the node reads no repository, whatever the run input names. The node's
output is node-local data too, so it taints its direct consumers like a local-data Tool node (§4.3). A sub-agent the
bound agent spawns does not inherit the scope (`SubAgentSpawnService` opens `CoderRepositoryScope.Begin(null)` for the
child), and neither do the participants of an orchestration agent (`InvocationRunner.RunOrchestrationAsync` does the same),
because their own model pins passed no repository gate. The option is allowed in a `Chat` graph on purpose: the node is single-operator and read-only, so whoever chats
with a graph whose Start `defaultInput` names a project can have that repository's files read and quoted back.

**In a `Chat` graph every Agent's seed prompt also carries the chat request**, whatever `includeUpstreamOutputs` says:
after the instructions and before the upstream map, a `## Conversation request` section with `run.input.message` (same
`MaxRunInputBytes` budget and truncation marker) and, when the run input lists attachments, one `Attachments: a.pdf,
b.png` line (names only; the content reaches an `includeAttachments` node, §3.8). Agent nodes have no `inputBindings`, and after a router the upstream is
the router's choice rather than the message, so without it an agent downstream of a DecisionModel or Condition never sees
what the user asked (live-round finding, 2026-09-23). A `Standard` graph's prompt is unchanged byte for byte.

**A steered row's prompt ends with an `## Operator steering` section** (§3.7): the row's applied steers, numbered,
oldest first, after everything above, the `## Attachments` section (§3.8) included. An LLM Call gets the same section after its bound prompt. A row nobody steered
sends a prompt byte-identical to one before steering existed.

Output: `{ "text": …, "json": … | null, "usage": { inputTokens, outputTokens, totalTokens, reasoningTokens,
durationMs, finishReason, model } }`. Every usage member is nullable: the runner reports what its provider gave it,
and no provider reports all of them. `text` is the answer after the turn's last tool call, as chat persists it
(`NodeChatPartAccumulator.FinalSegment`): the narration a model streams before each call ("Let me read every file")
is not handed to the next node or the approver.

**What a response schema actually enforces.** llama-server compiles it into a real GBNF grammar and applies that
grammar from the first output token, so the **structure** is guaranteed: the object shape, the declared types, an
`enum`'s member list, the required keys. The compiled `root` rule leaves the model's `<think>` block optional and
unconstrained and forces the schema only on what follows, so a reasoning model is not fighting the grammar.

**The value bounds are guaranteed too, on llama-server.** `minLength`, `maxLength`, `pattern`, `format`, the
numeric minimum and maximum, the item counts and the rest now reach llama.cpp as written, and so does the author's own
`required` list — a property left optional stays optional, and an object that omits `additionalProperties` stays open.
`DeferredLlamaServerChatClient.ApplyResponseSchemaPassthrough` writes the authored schema onto the request body
itself, and the MEAI OpenAI adapter fills its own response format in only with `??=`, so its unconditional
strict-schema transform is never invoked on this lane. The one exception is a repetition bound above
`LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound` (1024), which is still stripped because llama.cpp's
GBNF converter cannot compile it into a grammar at all — see §8.

**On every other runtime the old rule stands.** The strict-schema transform relocates twenty-two value keywords into
the schema's `description` string on the way out of the .NET process, where they are advice to the model rather than a
constraint (a `maxLength: 3` produced a 1302-character field in a live run), marks every declared property
`required`, and closes an object that has `properties` and omits `additionalProperties`. There, validate a value bound
downstream — in an edge condition or the consuming node — and never in the schema alone. You do not have to notice
this unaided: saving or validating a graph raises the non-blocking warning of §2.4 on an Agent node whose schema asks
for it, and that warning is now raised only for nodes that are **not** pinned to a llama-server model. See
`docs/agent-knowledge.md` §2 for the evidence and the `--verbose` recipe that shows the compiled grammar.

A node declaring a response schema must come back a JSON **object**. A parse failure fails `NodeFailed` — the
retryable class, since a re-ask under the same grammar can land where one attempt did not — naming the finish reason,
because a truncated answer is still `Completed` and `length` is the common cause. There is deliberately no salvage
path: grammar-constrained output carries no fences, and stripping some would quietly mask a broken grammar. See §8
for what a response schema costs at the llama.cpp grammar layer.

**An Agent node is node-local unless the operator allows cloud.** If the node's effective model resolves to a cloud
(or unresolved) model the node run is refused `ValidationFailed` before any capacity is reserved, with a reason naming
the switch and the unattended-runs grant on the model's external connection, unless **Let cloud models run
unattended** (`AllowCloudModelUnattendedRuns`, Node Settings → Privacy) is on or that connection grants unattended
runs: by default an unattended run never hands the operator's content to a cloud provider. A cloud run admitted by the
switch resolves its agent and its offer with the real cloud flag, so the other cloud switches still decide which tools,
playbook memory and attachments it gets ([Security & Privacy](../12-security-and-privacy.md)). Approval-required tools are
likewise stripped from the offer, and the turn runs
with `IsUnattended` set — the same posture a scheduled saved-agent run holds. A node bound to an agent that requires
tools (it lists some, or orchestrates) is refused `ValidationFailed` the same way when its effective model cannot call
them; the reason is `AgentModelRequirements.ToolRefusal`'s sentence. The editor's Agent form shows a yellow warning when
the node's model override is such a model; with no override the server decides.

**A second gate guards node-local data on a direct edge.** When an inbound edge from a Tool node that reads node-local
data (§4.3) was satisfied and `includeUpstreamOutputs` is on, a cloud effective model is refused `ValidationFailed` unless
**Let cloud models read local data** (`AllowCloudModelAccess`) is on or the model's connection grants local data. It
runs right after the unattended gate, so an unattended refusal wins when both apply, and before attachments, capacity
and the package: nothing is sent. The reason names the Tool node, its tool, the model, the switch and the connection
grant. With `includeUpstreamOutputs` off the seed inlines no upstream, so the node runs.

The runner's own watchdog reports a timeout as a failed terminal, which is why the executor classes it `Timeout`
rather than `NodeFailed` — a node that ran out of time deserves a different answer from one whose provider said no.
Provider errors never reach the row: the reason says "see the node logs" and the detail stays there.

### 4.2a `LlmCall` — LLM Call

One logical chat-model invocation per node attempt, through `GraphWorkflowInvocationExecutor.RunLlmTurnAsync` and the shared
`ILocalChatRuntimePackageBuilder` / `IInvocationRunner` stack. The node does not resolve an agent definition or add
a persona, tools, skills, memory, or orchestration. Existing bounded transport retries before the first output
remain enabled; graph retries are separate attempts governed by `maxAttempts`. Tool-free requests bypass the
function-invocation loop, so unsolicited tool-call content cannot trigger a recovery round.

The package's explicit `OmitSystemPrompt` flag carries absence through validation, configuration hashing and
`InvocationAgentFactory.BuildSeedMessages`. It defaults to false, so existing chat, Agent and benchmark callers
retain their non-empty system-prompt requirement and their existing configuration hashes.

The executor first classifies the selected model through `IModelTrustResolver`. For a node-local model the LLM Call
package requires node-managed llama routing throughout dispatch: the shared runtime bypasses cloud selection for that
request and rejects a provider remap before choosing a local client, including a cached client. This keeps a settings
change between validation and inference from sending the call externally, without locking provider settings for the
duration of generation.

A cloud (or unresolved) model is refused `ValidationFailed` with a reason naming the switch unless **Let cloud models
run unattended** (`AllowCloudModelUnattendedRuns`, Node Settings → Privacy) is on. With the switch on, the installed-GGUF
gate is skipped, capacity short-circuits as for any cloud model, and the package is built without the node-managed
llama requirement, so the shared runtime routes the call to the cloud client. A response schema rides the request's
response format as on the local path; a provider that ignores it fails the run through the usual parse or label check,
never silently.

The local-data edge gate of §4.2 applies too, after the unattended check and with the same grants: a cloud or unresolved
model is refused when a local-data Tool node fed this node over a satisfied edge and `inputBindings` is non-empty.
Without bindings no upstream reaches the prompt, so the node runs. The local-data gate does not path-analyse bindings: a binding that only
reads `run.input` is refused too while such a predecessor exists. (The `upstream` shape of a binding is checked separately, at save, as warning L2.)

| Config member | Meaning |
|---|---|
| `model` | Optional chat model. A node-local choice must be an installed node-managed GGUF chat model; external models declared local, uninstalled and non-chat models are refused before inference. A cloud model (Codex, Azure Foundry, an external model declared cloud) runs only while `AllowCloudModelUnattendedRuns` is on. When omitted, the local-default resolver chooses one. The chosen model is not automatically swapped. |
| `systemPrompt` | Optional authored system instructions. Empty means no system-role message; no default assistant prompt is substituted. |
| `prompt` | Required user instructions for this call. |
| `inputBindings` | Optional object mapping names to dot paths in the node's input document. Values form a named JSON data block alongside the prompt; there is no placeholder expansion or implicit upstream context. |
| `reasoningEffort` | Optional, using the same vocabulary as the Agent node. Actual support depends on the selected model. |
| `responseJsonSchema` | Optional object schema, with the same response handling and provider limitations as Agent output. |
| `samplingOptions` | Optional inference overrides. Unset values use runtime defaults. The editor keeps these in a collapsed Advanced section. |

Bindings use `GraphWorkflowDocuments.Resolve`. A missing path fails the node before inference; an explicit JSON
`null` is a valid bound value. `run.input.question` reads the run input, and
`upstream.retrieve.output.result` reads an incoming Tool result. `input.output.result` is a shortcut only when
exactly one incoming predecessor is satisfied. The `upstream` map contains **satisfied immediate predecessors**,
not every ancestor: add a direct dependency edge to consume an earlier result, taking the consumer's join policy
into account. Bound data is size-limited and is not silently truncated into a different JSON value.

Sampling overrides include `temperature`, `topP`, `topK`, `minP`, `maxOutputTokens`, `seed`, `repeatPenalty`,
`repeatLastN`, `presencePenalty`, `frequencyPenalty`, `stop`, and `numCtx`. The last is a request/prompt budget
ceiling; it cannot resize the llama-server context window fixed when the model was loaded.

Output uses the Agent-compatible `{ text, json, usage }` payload inside the normal node envelope. A requested
structured response must parse as a JSON object; otherwise the attempt fails. This is not a separate full JSON
Schema validation pass. A following Condition can route on `output.json.needsReview` without parsing prose.

For example, `Start → Tool → LLM Call → End` can bind `document` to `input.output.result` and use the prompt
"Summarize the bound document and retain the technical findings." Only the named document is supplied to the
model. A second LLM Call can consume the first one's `input.output.text` the same way.

### 4.3 `Tool`

One tool call, on its own lane, through `IToolInvocationService` (`GraphWorkflowToolExecutor`): a built-in read tool,
or an action (below).

| Config member | Meaning |
|---|---|
| `toolName` | **Required.** |
| `arguments` | Optional object of literal arguments. |
| `argumentBindings` | Optional map of argument name → dot path, resolved against the node's input document. A binding **wins** over a literal of the same name. |
| `allowedUrls` | `web_fetch` only, and required there: the link allow-list (below). Any other tool carrying it is a save error. |

Output: `{ "result": … }` — embedded as JSON when the tool answered with an object or an array, and as a string
otherwise. That one try-parse is what lets a downstream `Condition`, which passes its predecessor's output through
verbatim, dot-path into a structured answer. Stated rather than hidden: plain text that happens to be a JSON object
is embedded as JSON, and the tools that do that mean it.

**A Tool node passes two gates, not one** (`GraphWorkflowToolGate`, an operator ruling and
[ADR 0006](../../adr/0006-agentic-trust-mcp-key-scopes-and-auto-approval.md)):

1. the tool must be a **built-in** tool in the `ToolCategory.ReadLocal` envelope, and
2. the **composed** approval policy must answer that it needs no approval.

Both are asked of the same catalog at **definition save** and again at **run start**, and the run-start answer wins.
The one exception besides `web_fetch` is an action (below).
A tool that was read-only when the graph was saved does not execute after a policy tightened. A tool outside the
envelope is an **error**, never a warning: a workflow node runs unattended, so a write, execute or approval-gated
tool has nobody to ask. Errors are keyed by node key, so the editor draws them on the offending node.

**A local-data Tool node taints its direct consumers.** A Tool node whose tool returns node-local data is one in the set
the chat local-data gate withholds from a cloud model (`LocalToolOfferProvider.LocalDataToolNames`: the knowledge-base
read tools and the coder workspace file tools); an Agent node with `repository` taints the same way
(`GraphWorkflowToolGate.ReadsLocalData`). `GraphWorkflowToolGate.LocalDataSource` finds such a node among the keys
of the consumer's `upstream` document, which hold only satisfied edges, so a failed one on an `Any` join taints nothing.
Its Agent, LLM Call or `llm` decision consumer then passes the local-data gate of §4.2 before a cloud model sees the
output. `web_fetch`, `get_current_time`, `calculate` and action tools are not in the set.

**Accepted ceiling: direct edges only.** Taint is not carried further. Content laundered through a local LLM Call or
Agent, a `Condition`, a `Join` or `End`, a later run's `run.previous`, or a local agent's cloud sub-agent reaches a cloud
node ungated. Action tools stay untainted although a command or HTTP action can return local content, matching the chat
classification, where custom tools are never under the local-data rule.

**The one web exception** ([ADR 0017](../../adr/0017-web-access-tools-and-graph-allowlist.md) decision 6; ADR 0006 is its
basis and is not amended). While web access is on, a Tool node may name `web_fetch` if it carries a non-empty
`allowedUrls` list of absolute http(s) URL prefixes (same scheme, host and port; a path prefix on a segment boundary,
and under an entry with a path, a URL whose path carries an encoded `/`, `\` or `.` is refused).
The list, written before the run, is the consent an unattended node cannot ask for, so there is no pause and no result
review. The gate checks the list's shape and any **literal** `url` argument at save and start; a **bound** `url`,
usually upstream model output, is checked at dispatch, which is the real gate: `GraphWorkflowToolExecutor` calls the
fetch service itself with the node's list, and the first URL and every redirect hop must match. Private and local
addresses stay blocked even when listed. `IToolInvocationService` still refuses `web_fetch` (it is `Network`), so no
other caller reaches it this way. A URL outside the list, a blocked address and web access switched off fail the node
`ValidationFailed`; any other refusal about the page (HTTP error, unsupported type, the fetch's own time budget)
succeeds carrying the tool's `{ "error", "message" }` answer, as `read_file`'s own refusal does. `web_search` is never
allowed on a Tool node, and **no** Agent node — bound agent or default persona — is offered either web tool: both are
stripped from the offer by name.

**Actions** ([ADR 0021](../../adr/0021-unattended-actions-in-graph-tool-nodes.md)). `ToolInvocationService` admits a
third kind of name: a custom tool of either kind (`HttpFetch` or `Command`) that is enabled, acknowledged, in `Fixed`
mode and flagged `AllowUnattended`, while custom tools are on for the node (`IUnattendedCustomToolCatalog`). The
invocable list marks it `IsAction`, and `GraphWorkflowToolGate.ErrorsAsync` refuses a Tool node naming it, keyed by
node, unless the definition's `allowsActions` is on. Only the Operator update route sets that flag, and export omits it.
Create never carries it, so a drawn action node is a save error until the flag is on. Import never carries it either,
but admits the action node (`GraphWorkflowDefinitionService.ImportAsync`): a shared graph arrives unable to act, its start
refuses the node, and the operator flags the definition through the update to run it. On invocation the service
needs the node's definition id, writes a metadata-only `ApprovalDecision` row through the strict
`IUnattendedApprovalAuditRecorder` with source `graph-action:<definitionId>`, and only then calls the kind's executor
directly, without the approval wrap the chat path uses. A missing definition id or a failed audit write is a
`NotInvocable` refusal and the executor is never called. `Fixed` mode takes no arguments, so a node's `arguments` and
`argumentBindings` reach nothing an action reads. `GraphWorkflowGraph` gives every Tool node naming a custom tool
**one attempt** whatever it declares (`NamesCustomTool`, decided from the `custom__` name prefix rather than the
catalog, so every reader of a pinned run graph agrees without a store read), so neither the retry stage nor a restart
re-fires a side effect: an action failed `Timeout`, `Faulted` or `Interrupted` stays failed and fails the run naming
the node. An action the executor reports as failed (`CustomToolExecution.Succeeded` false: a guard block, a transport
error or timeout, a non-2xx answer, a command's non-zero exit or failed launch) is `Faulted` with the structural reason
`action-failed` and the executor's text as the outcome's result. The failed node's error stays that structural reason,
but its output document carries the executor's text under output status `Failed`, bounded by `MaxOutputJsonBytes`
(over the cap the envelope is empty). With one attempt it fails the run. The chat path still hands the model the text
alone. In the editor the Tool picker
labels an action and shows an "Action" badge with a note that it runs unattended and once
(`GraphWorkflowToolConfigForm`); while the definition does not allow actions the entry is disabled and the node shows the
save gate's own refusal. The switch lives in the definition's Workflow settings dialog (`GraphWorkflowDefinitionMetaDialog`, shown
only over a stored definition, with a danger note) and sends `allowsActions` only when moved; the list and the toolbar
mark a definition that allows actions.

The lane itself is a `GraphWorkflowInFlightLane`, the same registry the `Agent` lane uses, and the two differ only in
what they run: dispatch to a queue, settle on the poll, a stop that answers no on a repeat, forget what a retry
superseded. A tool call passes **one** queue where an agent turn passes two — there is no node-wide invocation lease
to wait for after the lane slot, so the row reads `Running` the moment the lane hands back an entry. The call itself
does not start until that `Running` write has committed (`GraphWorkflowToolExecutor.DispatchAsync`): a restart
collapses a `Queued` row to `Pending` without spending an attempt (§3.5), so a call fired from one would fire an action
twice; a failed write faults the unstarted call and the poll fails the row. The lane is
therefore what bounds the fan-out: a tool call has no global bottleneck of its own, and a `Parallel` node feeding two
hundred `search_knowledge_base` nodes would otherwise fire all of them at once. It is sized on `MaxConcurrentRuns`
rather than on a knob of its own — the same "how much of this node may be busy at once" question — and is worth a
second option only once the two need different numbers. The whole invocation envelope lives inside
`IToolInvocationService`, so the executor enforces none of it and cannot skip any of it: every refusal arrives as an
outcome and becomes a row.

A landed outcome maps to a terminal: `Executed` succeeds; `UnknownTool`, `NotInvocable` and `InvalidArguments` fail
`ValidationFailed` and are therefore never re-attempted; `Timeout` and `Faulted` fail on the two retryable classes.
The service's own reason is repeated verbatim and is structural by contract — it never echoes an argument value. An
output document over the cap is a real, reachable outcome (a knowledge-base search may legitimately answer with fifty
thousand characters) and is deliberately **not** retryable, since the same call composes the same bytes; the message
names the tool beside the node, because "which node" alone does not say what to shrink.

`graph-workflows/tools` is the picker's feed, the same list the save gate checks (`GraphWorkflowToolGate.ListAsync`:
the invocation envelope with actions flagged `isAction`, plus `web_fetch` flagged `requiresAllowedUrls` while web access
is on), so the picker cannot offer a name the run would then refuse. A missing binding path fails the node
`ValidationFailed`; the `Queued` write happens before bindings resolve, so a refused row keeps its input document.

### 4.4 `Condition`

Config: `path` — the optional node-level **default** dot path its own out-edges inherit.

Output: a **verbatim pass-through** of the predecessor's `output`. This is what makes a Condition node a real router:
edge conditions evaluate against the source node's output document, so without the pass-through a Condition's own
out-edges would inspect `{}` and never fire. A node with several predecessors has no single upstream output to carry
forward and answers `{}` rather than inventing one.

### 4.5 `Parallel` and `Join`

No config — they are shape, not settings, and §2.3 is the whole of their semantics.

`Parallel` passes its predecessor's `output` through exactly as `Condition` does. `Join` answers a per-source map
over its **satisfied** inbound edges, `{ "<nodeKey>": <that node's envelope> }`, so everything downstream of a join
sees every branch rather than whichever one the single-predecessor shortcut would have picked.

### 4.6 `Pause`

| Config member | Meaning |
|---|---|
| `prompt` | **Required.** What the person is being asked. |
| `allowedDecisions` | **Required**, non-empty, distinct, from `Approve` / `Reject`. Empty would be a question nobody could answer. `Answer` is refused: it belongs to `ChatInput` (§4.8). |
| `requireComment` | Defaults false. |

`GraphWorkflowPauseExecutor` is the one lane that drives nothing: parking a row on a human is two status writes, so
there is no work to hold, no slot to wait for and no answer to poll for. It writes `Running` then
`WaitingForApproval` with `PendingDecisionKind` naming the pending act, and no output document — a pause's output is
its answer, and it has none yet. The prompt, the allowed answers and `requireComment` are **not** copied onto the
row: they are already in the pinned graph, and a second copy is a second thing that can drift.

The answer arrives through `POST .../runs/{runId}/nodes/{nodeKey}/decide`, idempotent on a client-minted
`operationId`. Both answers **succeed** the node run — the answer is the node's output, and routing on it is the
edges' job. A rejection reaches the run through an out-edge, never through a node failure. `WaitingForApproval` can
never move to `Skipped`: skipping an open pause would be an operator walking past a decision instead of giving one,
which is the one thing a pause exists to make impossible.

Output: `{ "decision": "Approve" | "Reject", "comment": … | null, "payload": … }`. `decision` is the enum's **name**,
because that is the member every out-edge condition selects on and the exact member the definition-time pre-flight
check writes; the two spellings must produce the same string or a graph that pre-flighted clean would route nowhere.
`comment` and `payload` ride **beside** it rather than above it: they are the operator's, not the router's, and a
condition that could select on them would route on free text.

**A Pause's successor gets only the decision, and the editor wires around it.** A node's `input` is its ONE
satisfied predecessor's output (§3.2), so an authored `X → Pause → Y` hands Y the approval and never X's answer, and a
`Pause` before an `End` loses the result the same way. Two things address that, neither of them a change to what a
Pause writes. The validator raises the non-blocking warning of §2.4 on Y. And the editor adds the missing edge itself:
when an author **connects** an edge into or out of a Pause, `pauseContextEdges` returns the unconditional edges
labelled `context` from the pause's nearest non-`Pause` ancestor to its successors, the canvas adds them, and a notice
says one appeared. It descends from the Open Canvas importer's `CanvasWorkflowImport.AddPauseContextEdges`
(§9.2) — walking back through consecutive pauses, skipping a self-loop — judged per **successor** exactly as the
validator judges it: only a successor whose every inbound edge leaves a Pause is starved (one something else already
feeds is not, so it gets nothing and no warning), and the ancestor is the union of the nearest non-`Pause` ancestors
of every pause feeding it. The importer applies the same rule and the same guards (§9.2), so the three halves cannot
advise different edges. A `Condition` ancestor is skipped, because the added edge carries no
`sourceHandle` and would save as that Condition's second unconditional out-edge, which §2.4 refuses. A pause whose
nearest non-`Pause` ancestor is **not unique** (mutually exclusive branches feeding it) gets nothing, because wiring
both ancestors into an `All` successor would skip it the moment the untaken branch is dead. And a successor with
`joinPolicy: "Any"` — of **any** kind, since the policy is a property of every node (§2.3) — gets nothing, because an
unconditional content edge would admit it while every approval was rejected. Wiring into a pause visits each pause
reachable forward through consecutive pauses with its own ancestry, so `A → P1 → P2 → B` plus `X → P2` adds nothing
for `B` (its ancestors through `P2` are `{A, X}`). Everywhere else Y keeps its default `All` join policy, so it is admitted only once **both**
the content and the approval have arrived, and its `input` is the `upstream` map carrying both.

The pass runs on the **connect gesture and nowhere else** — never on render, never on validate — so an edge the
author deletes stays deleted. Wiring **out of** a Pause considers only the node just connected, since that is the
only successor that gesture can have starved; wiring **into** a Pause considers every successor of it, walking forward
through consecutive pauses so `A → P1` wired last still reaches the `B` behind `P1 → P2 → B`.

A decide call carrying a different `operationId` for an already-answered pause is 409
`GraphWorkflowGateAlreadyDecided`, with the standing decision on the body — a second human act is refused, not
replayed.

### 4.7 `End`

| Config member | Meaning |
|---|---|
| `outcome` | **Required.** The declared outcome string. |
| `resultPath` | Optional dot path into the End node's input document. |
| `publishToChat` | Defaults true in a `Chat` graph, false otherwise; `true` is refused outside a `Chat` graph (§2.1). |

Output: `{ "outcome": …, "result": … }` — the resolved path, or the whole input document when the author named none.
That default result omits `run.previous` (§4), so a run's result never carries the run before it; an explicit
`resultPath` such as `run.previous.output` still reaches it.
A path the document does not carry resolves to `null`; failing the node instead would end a run that did all of its
work over a projection nobody reads.

An `End` node is a terminal node by construction (nothing may leave it), and a run is `Completed` only once one of
them **succeeded**.

### 4.8 `ChatInput`

| Config member | Meaning |
|---|---|
| `prompt` | **Required.** What the chat surface shows while the run waits for the user's next message. |

Legal only in a `Chat` graph, and it needs at least one **unconditional** out-edge (§2.4). It rides the pause lane:
`GraphWorkflowPauseExecutor` owns `Pause` and `ChatInput` alike and writes `PendingDecisionKind` from the node kind —
`Approve` for a pause, **`Answer`** for a chat input — so a reader tells a gate from a question off the row, without
the graph. The run reads `WaitingForApproval` (the wording is the surface's, derived from the pending kind); a restart
leaves the row alone exactly as it leaves a pause, and the cancel drain cancels it the same way.

The answer arrives through the same decide route with `decision: "Answer"` and `payload: { "text": "…" }`.
`GraphWorkflowStateMachine.IsDecidable(kind, status, decision)` is the one place the pairing lives: a `ChatInput`
takes `Answer` and nothing else (409 for `Approve` / `Reject`), and a `Pause` refuses `Answer` (409). A missing, blank
or non-string `payload.text`, or a `comment` beside it, is a 400; so is text over
`min(Security:MaxMessageSizeKb × 1024, MaxOutputJsonBytes / 2)` UTF-8 bytes — the answer is a chat message, and it must
still fit the envelope it is embedded in. `operationId` idempotency and the 409 `GraphWorkflowGateAlreadyDecided`
naming the standing answer work exactly as for a pause.

Output (`GraphWorkflowDocuments.ChatInputOutput`): `{ "decision": "Answer", "text": … }`. `decision` is kept because
the replay and standing-conflict checks read `output.decision` structurally. An edge may condition on `output.text`.
The §2.4 pause-context warning and the editor's `pauseContextEdges` gesture treat a `ChatInput` like a `Pause`: a
successor reached only through one receives the answer document, not the content before the wait.

### 4.9 `DecisionModel`

| Config member | Meaning |
|---|---|
| `question` | **Required.** What is being decided. For `systemone` it is sent as the question's `instructions`. |
| `questionType` | Optional: `choice` (the default), `noul` (a yes-likelihood in [0, 1]) or `score` (a position on ordered levels). |
| `labels` | `choice`: **required**, 2–32 distinct, non-blank strings of at most 64 characters (flat on purpose: for `llm` they become a grammar enum). `score`: **required**, 2–10 levels, lowest first. `noul`: must be absent. |
| `criteria` | Optional, `choice` only: an object mapping some of the labels to a 1–512 character description. A key that is not a label is refused. |
| `provider` | Optional closed vocabulary: `llm` (the default) or `systemone`. A provider this build cannot run is refused at save. `llm` answers `choice` only. |
| `model` | `llm`: optional, as on `LlmCall` (an installed node-managed GGUF chat model, a cloud model while `AllowCloudModelUnattendedRuns` is on, else the local default). `systemone`: **required**, an installed node-managed GGUF classified `Decision`. |
| `inputBindings` | Exactly as on `LlmCall` (§4.2a). A `systemone` node needs at least one: the bound text is what it decides on. |

The save-time matrix: a `noul` or `score` question on `llm` is refused (its grammar is an enum), `systemone` without a
`model` is refused, `systemone` without any `inputBindings` is refused (the client calls this `decisionInputBindingRequired`;
the server, like every graph validation, returns the message keyed by node, not a code), `criteria` outside a `choice` is
refused, and a `noul` with `labels` is refused. A save-time not-installed warning on a `systemone` node names a decision
model, not a chat model.

**`llm` provider.** A classifier node on the invocation lane. `LlmDecisionProvider`
(`Services/GraphWorkflows/Decisions/`) **lowers** the node to an `LlmCall` config — a fixed classifier system prompt,
the question plus the label list (and one line per described label when `criteria` are declared) as the prompt, the
same bindings, `reasoningEffort: "none"`, `temperature: 0` (so the same input routes the same way on a re-run) and the
response schema `{ type: object, properties: { choice: { type: string, enum: labels } }, required: [choice] }` — which
runs through `RunLlmTurnAsync` unchanged, model gate, capacity and binding rules included. The provider then
**interprets** the answer, and the executor holds the choice to the labels: an answer that is not a JSON object, or
names anything outside them, fails `NodeFailed` (retryable, so `AttemptsExhausted` on the last attempt), and the reason
never repeats the model's text.
The lowered call carries the same bindings, so the local-data edge gate of §4.2a applies unchanged.

**`systemone` provider.** `SystemOneDecisionProvider` is an `IGraphWorkflowNativeDecisionProvider`: it answers itself
through a native decision model (Julia-1, Laya, Kev-4B: any GGUF carrying `<arch>.decision.type`) and llama-server's
`POST /v1/systemone`, one question named `q` per request. `RunDecisionTurnAsync` dispatches by provider name; the
native lane (`RunNativeDecisionTurnAsync`) binds the input with the same binder and `MaxRunInputBytes` cap as
`TryBuildBoundPrompt` (the bound data block is the `state`; the question is not repeated in it), gates the model
(node-local, installed, decision type present, else `ValidationFailed`), admits capacity for `ModelRole.Decision`
(`CapacityRejected` on a refusal), then calls `ILlamaServerSystemOneClient`, which leases the model's decision-role
process (starting it when cold) under its own budget of 5 s plus 10 ms per estimated input token, capped at 30 s. Calls on
one model are serialized in the client (the decision-role process is `--parallel 1`), and the budget starts only once a call owns the
model, so concurrent nodes queue against their node timeout rather than time out on the server's queue. The 5–30 s
window does not scale with model size: the small BERT-class models answer in about 0.1 s, but Kev-4B on a CPU build took
about 1.3 s for a 200-token input in a live run, so an input of a few thousand tokens on CPU can exceed 30 s and fail
`Timeout` on every attempt. It
deliberately takes no node-wide invocation slot (the decision process is a pooled companion with its own lease, and
the chat slot would queue it behind chat turns) and runs no invocation runner, so there is no invocation record and no
runner watchdog; attachments and steering do not apply. The local-data edge gate does not run on this lane because
only node-local models do; a provider that ever answers remotely must call `CloudLocalDataGate` with the bound input.
An answer of another type than `questionType`, an off-label
choice or a missing number fails `NodeFailed` (retryable), each with its own reason; HTTP 501 (the model is not a
decision model) fails `ValidationFailed` naming the model; a refused request (HTTP 400, or the HTTP 500 "input (N tokens)
is too large to process" b11669 answers for a prompt longer than the process's physical batch: `-ub` equals the launched
context, 7936 for Julia-1 and Laya, 8192 for Kev-4B) also fails `ValidationFailed`, because a retry would send the same
input again; the client's own timeout fails `Timeout`; transport errors fail `NodeFailed`. The server's text goes to the
node log only.

Output, the same shape from both providers:

```jsonc
{
  "type": "choice",                  // echoes questionType
  "choice": "billing",               // choice: the label. noul: "yes" at 0.5 or above, else "no". score: the nearest level's label
  "noul": null,                      // noul only: the yes-likelihood
  "score": null,                     // score only: continuous, may fall between levels (0 = the first level)
  "legend": null,                    // score only: level index → level name, as the server reports it
  "probabilities": { "billing": 0.99, "shipping": 0.01 },  // systemone choice (keyed by label) / score (keyed by level index "0", "1", …, as upstream sends them); null from llm
  "confidence": 0.99,                // systemone where reported; null from llm
  "provider": "systemone",
  "usage": { "inputTokens": 239, "outputTokens": 0, … }
}
```

`confidence` and `probabilities` are always null from the `llm` provider — a grammar-constrained answer carries no
score, and inventing one would route on noise. The `systemone` numbers are **model-reported and uncalibrated**: upstream
scales them by temperatures stored in the model file and does not guarantee calibration, so a threshold is a routing
heuristic to measure on your own inputs, not a probability. For `noul` and `score`, `noul` and `score` are the truth
and `choice` is a convenience for `eq` routing.

Out-edges route on the output directly or through a `Condition` (ops `eq ne gt gte lt lte exists notExists`). Typical
routes: a choice on `output.choice` `eq "billing"`; a yes/no on `output.noul` `gte 0.7`; urgency on `output.score`
`gte 1.5`; an abstain branch on `output.confidence` `lt 0.6` beside the label edges. There is no threshold knob on the
node itself (ADR 0021's rejection of a confidence threshold stands). An interrupted `Running` DecisionModel row is
failed `Interrupted` at startup like every model turn (§3.5).

### 4.10 `DevTask`

A Development Mode step ([ADR 0023](../../adr/0023-dev-workflows-converge-into-graph-workflows.md)). Graph Workflows owns no
workspace and runs no coder: an `implement` node drives one Development Mode task through Dev Mode's own coder,
validation and review until it awaits apply, and an `apply` node applies that task through
`IDevelopmentManagementService.ApplyAsync`, so the apply gate is called, never re-implemented. `config.action` picks
the action, and a member only the other action reads is refused (`GraphWorkflowGraph.ParseDevTask`). The node is
refused in a `Chat` graph and gets **1** attempt unless it declares `maxAttempts`.

| Action | Config member | Meaning |
|---|---|---|
| `implement` | `requirements` | **Required.** What the task must build. |
| `implement` | `title` | Optional task title. |
| `implement` | `acceptanceCriteria` | Optional array of non-blank strings. |
| `implement` | `instructions` | Optional rules the Dev Mode coder and reviewer read for this task. |
| `implement` | `inputBindings` | Optional, as on `LlmCall` (§4.2a): named input data handed over beside `requirements`, held to the same lint (§2.4). |
| `apply` | `task` | **Required.** The key of the `implement` node whose task it applies. |

**`DEVTASK-1`**, checked in `GraphWorkflowGraph.ValidateDevTask`, so at save and again at run start (§3.1): an `apply`
node's `task` names an `implement` node of the graph, and **every** inbound edge of the `apply` node leaves a `Pause`
node that has that `implement` node as a strict ancestor and fires on `Approve` and on no other answer. "Every inbound
edge" rather than "a pause on every path": the approval has to be the step in front of the apply and be given after the
patch exists, so an extra content edge, a `Reject`-routed or unconditional edge out of the pause, a pause on a sibling
branch, and a pause before the `implement` are each refused, keyed on the `apply` node. Which answer an edge fires on is
asked of the state machine's routing (`GraphWorkflowStateMachine.DecisionEdgeFires`), as the `Pause` pre-flight rule
asks it. Unattended runs (schedule, integrations) cannot pass a `Pause`, so they cannot apply. Because an `apply` reads
its task by key, the pause-context warning (§2.4) is never raised for it: its advice would add the edge `DEVTASK-1`
refuses.

```jsonc
{ "key": "impl",  "kind": "DevTask", "config": { "action": "implement", "requirements": "Fix the login bug.",
                                                  "inputBindings": { "request": "run.input.request" } } },
{ "key": "review", "kind": "Pause",  "config": { "prompt": "Apply the patch?", "allowedDecisions": ["Approve", "Reject"] } },
{ "key": "apply", "kind": "DevTask", "config": { "action": "apply", "task": "impl" } }
// edges: impl → review; review → apply on output.decision Eq "Approve"; review → an End on Eq "Reject"
```

**Start binding.** A graph holding any `DevTask` node works in one Development project, named by the run input's
`developmentProjectId` (the key the Agent node's `repository` option reads, §4.2). `GraphWorkflowRunService.StartAsync`
refuses the start, writing no run, while Development Mode is off on the node (409), when the input carries no
`developmentProjectId` GUID (400), when the project does not exist (400), and when its repository binding does not
resolve (409, never naming the host path). A Start `defaultInput` that carries the key binds the project like an
explicit input does.

**The lane** (`GraphWorkflowDevTaskExecutor`). It holds no slot and no in-flight work: the Development task is durable
and the row names it, so the lane waits by being polled on every tick (the sweep interval, `DispatchIntervalMilliseconds`),
and nothing sleeps. It resolves the Development services per call, so Development Mode off fails the node
`ValidationFailed` rather than the container; the live switch only stops it from starting the next step, so an attempt
already running finishes.

- **implement.** The first dispatch moves the row to `Running`, renders `requirements` with its `inputBindings` (as an
  `LlmCall` prompt), creates ONE task in the run's project and pins it with `SetNodeRunDevelopmentTaskAsync` **before**
  the first `StartNextActionAsync`. The create is keyed on run and node key without the attempt, so a crash between
  create and pin gets the same task back; a pinned row (re-attempt or restart) never creates. A missing `title` is the
  node label; empty `acceptanceCriteria` and the review budget come from the project's first task. `instructions` are
  recorded through `RecordWorkflowPolicyAsync` per attempt and revoked when the row ends. Each poll then reads the task:
  `AwaitingApply` (or already `Completed`) succeeds with `{ developmentTaskId, taskStatus, reviewRound }`; an attempt in
  flight waits; an idle task gets the next `StartNextActionAsync`, keyed on what the tick observed. A failed attempt
  fails `NodeFailed` (`Interrupted` for a restart, `ValidationFailed` for a workspace policy refusal); a `Blocked` task
  fails `ValidationFailed` with Dev Mode's reason; a cancelled task or attempt ends the row `Cancelled`. Once the run
  has ended, a task a missing test runner stood down (`[runner_not_installed]`) is continued from the Development page:
  its next action ("Retry validation") re-validates the same coder attempt through the `RetryValidation` edge, and is
  refused while any run driving the task is live. A task stood down at its round cap is not continued there.
- **apply.** Reads the row of the named `implement` node in THIS run; one that has not succeeded with a task fails
  `ValidationFailed` (a `Pause` with `joinPolicy: Any` can be reached past it). A `Completed` task succeeds with
  `alreadyApplied: true` without asking. Otherwise it calls `ApplyAsync` with the run as the caller
  (`DevelopmentWorkflowRunReference { Engine = GraphWorkflow, RunId }`) and an operation id per run, node key and
  attempt, inside the tick. `ApplyBlocked`, the ownership guard, a stale task and a workspace refusal fail
  `ValidationFailed` with the sanitized reason; a ledger race fails `NodeFailed`.
- **Cancel.** A cancel drain polls the row first: the lane cancels the task's running attempt and ends the row
  `Cancelled`. The Development task survives and is continued from the Development page. A cancel landing between the
  create and the pin declines the pin, and the lane cancels the task it just created, so no unowned task is left.
- **Graph Workflows off.** The ownership guard fails closed: the dispatcher stops, so a live run can neither end nor be
  cancelled, its task stays unapplicable, and the refusal and the Development page banner say to turn Graph Workflows
  back on to finish or cancel that run.
- **Timeout.** An `implement` node gets **no** default node timeout (§3.4): a coder, validation and review chain
  routinely outlasts it, and each Dev Mode attempt carries its own duration budget. Only an explicit `timeoutSeconds` bounds it, and the lane answers that deadline itself, inside the dispatcher's
  grace: it cancels the running Dev Mode attempt (`IDevelopmentManagementService.CancelAttemptAsync`, the path the
  Development page's cancel uses) and fails the row `Timeout`, so no coder round outlives the node. A run cancel
  cancels the attempt the same way.

**In the editor.** The `DevTask` card's panel picks the action, then shows `implement`'s members or, for `apply`, a
picker of this graph's `implement` nodes; renaming an `implement` node carries an `apply` node's `task` with it, and the
Pause `context` edge (§4.6) is never drawn into an `apply` node. `DEVTASK-1` is not mirrored client-side: the save
answers with the server's keyed message. The start dialog shows a start refusal's own sentence (the Development Mode
409 is a `GraphWorkflowRunConflict`, like the version check); the run input's `developmentProjectId` is typed into the
generic input editor or carried by the Start `defaultInput`. A settled node run's panel shows its task's stage and links
to that task on the Development page; the Development page's "driven by a workflow" banner links back by the task's
`workflowEngine`.
