# Agent Mode reference: Work Sessions

> Reference page for [Agent Mode](../04-agent-mode.md) §5 · Reviewed: 2026-10-07 · Code-grounded.

It lists the step mechanics, the park-event recovery, the transcript bound, the state tools, the checkpoint and repoint rules, the settings table and the per-step consumption record.

## 5.1 One step

Continues [Agent Mode](../04-agent-mode.md#51-one-step), which keeps the section introduction.

Per step it: writes a `StepStarted` event before the send but **publishes the `Step` change only once the turn
is live** (on the first `AssistantStreaming`/`AssistantPhase`/terminal event: earlier, `InvocationResumeRegistry`
has no entry yet and a client told then re-attaches to an empty stream; later than the terminal, the entry is
gone again); composes the state block; drains the stream, mapping
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

## 5.2 The node has one invocation slot

The section introduction is in [Agent Mode](../04-agent-mode.md#52-the-node-has-one-invocation-slot).

### A dropped park event

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

## 5.3 The state block

The section introduction is in [Agent Mode](../04-agent-mode.md#53-the-state-block).

### The transcript bound at the step boundary

Rebuilding the state block bounds what the model *needs*; it does not bound what the send path *sends*.
A step is an ordinary chat turn, so `ConversationContextBuilder.Build` replays every earlier step's state block,
answer and **reasoning** verbatim (tool calls and results live in ordered parts, never in `Content`, and replay
only as excerpts capped at `ConversationContextBudgetOptions.DefaultHistoricalToolResultExcerptChars`, and only
when the turn stays node-local or `KnowledgeBaseOptions.AllowCloudModelAccess` is on — `NodeChatStreamService.AreAttachmentsAllowed`), and the transcript grows for the life of the session. Meanwhile the step's own tool
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
Microsoft.Extensions.AI.OpenAI 10.9.0 and 10.10.0: the Chat Completions client converts text, URI, data and
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
**tighten-only**: a value at or above the node ceiling has no effect, so no run can raise it. Every
turn now also seeds a result budget sized to its model's window (`InvocationRunner`,
`ToolResultBudgetScope.BeginTightenedScope`), which applies only where it is tighter than the step's
seed, so a step's own bound is never loosened.

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

## 5.4 The four state tools

Continues [Agent Mode](../04-agent-mode.md#54-the-four-state-tools), which keeps the section introduction.

They belong to the **turn**, not the agent. The supervisor sends every step inside `WorkSessionTurnScope`, and
`AgentDefinitionResolver.ProjectAllowedTools` unions the four in from the profile pool after the
`AllowedToolNames` intersection, for the Default Assistant as well. A custom agent driving a session, or bound
to a development-workflow Agent node, therefore gets them without listing them. Before this, such an agent ran
every step tool-less until the step cap. The union lifts from the gated pool, so the tool-capable list still
decides, and an ordinary chat turn carries no scope and is never offered them.

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

## 5.5 Checkpoints, and what a repoint may not do

Continues [Agent Mode](../04-agent-mode.md#55-checkpoints-and-what-a-repoint-may-not-do), which keeps the section introduction.

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

## 5.6 Settings

| Key | Default | Note |
|---|---|---|
| `WorkSessions:Enabled` | `true` | Shipped in `appsettings.json`; gates behaviour, never registration. Seeds the `WorkSessionsEnabled` node setting (**Node Settings → General → Features**, live) |
| `WorkSessions:MaxStepsPerRun` | `25` | Per start/resume, not per lifetime. Seeds the node setting (**Node Settings → Workspaces**, restart) |
| `WorkSessions:CheckpointEveryNSteps` | `5` | |
| `WorkSessions:MaxConcurrentSessions` | `1` | Admission cap — see §5.2. Seeds the node setting (restart) |
| `WorkSessions:MaxParkedSeconds` | `300` | Startup validation checks the configured tool-age seed; every park is also capped below the approval coordinator's effective age, including stored overrides and elapsed time for registered approvals |
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

## 5.7 The per-step consumption record

Continues [Agent Mode](../04-agent-mode.md#57-the-per-step-consumption-record), which keeps the section introduction.

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
- **The supervisor's rows are per step *attempt*, the tool handlers' rows per step *number*.** The supervisor keys its event
  operation ids by session, step, phase and the attempt (the session's last sequence when the step began), so a step re-run
  after a park timeout, a pause, an interruption or a crash records its own `StepStarted`/`StepEnded`/`ParkTimedOut` rows and
  its own consumption; treat the sum across attempts as a lower bound only when a crash landed mid-attempt. The four state
  tool handlers still key by step number, so a `complete_work_session` call repeated by a re-run attempt dedups onto the
  first attempt's row (known limit, live QA F-38).

A step stopped through the cancellation registry — paused, cancelled, an expired park, a blown deadline — writes no row
at all, deliberately: the run may still be unwinding when the supervisor sees its terminal, so its counters would be a
race rather than a measurement. `ToolSchemaTokens` counts schema tokens **shipped across rounds**, so it grows with the
round count and is not the size of the offer. `ToolNames` is trailing and optional: `null` means the row predates the
member, never that the step called no tools — `ToolCallsCompleted` answers that.
