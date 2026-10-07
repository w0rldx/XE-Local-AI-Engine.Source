# Agent Mode reference: AI.Agent runtime

> Reference page for [Agent Mode](../04-agent-mode.md) §1 · Reviewed: 2026-10-07 · Code-grounded.

It lists the composition-root registrations, the per-hop pipeline rules, the tool-registry table, tool-relevance and provider-budget mechanics, outbound MCP and Custom Tools boundaries, the risk taxonomy, and the agent-building, sampling, reasoning-effort and orchestration details.

## 1.1 `AddLocalAiAgentRuntime` — the composition root

`AgentServiceCollectionExtensions.AddLocalAiAgentRuntime` is the single registration entry point
(`XE-Local-AI-Engine.AI.Agent/DependencyInjection/AgentServiceCollectionExtensions.cs`). In order, it:

1. **Binds + validates options** — `LocalChatAgentOptions`, `InvocationAgentOptions`,
   `OrchestrationAgentOptions`, `AgentToolPipelineOptions`, `ProviderCallBudgetOptions` and
   `ToolRelevanceOptions`, each `Bind` → `ValidateDataAnnotations` → `ValidateOnStart`, plus
   `AgentTelemetryOptions` (`Bind` → `ValidateOnStart`). The first four have a dedicated `IValidateOptions<>`
   validator (`Configuration/Validation/*Validator.cs`). The root config key is `"Agent"`
   (`AgentRuntimeOptions.Section`).
2. **Decorates the `IChatClient` pipeline** via `DecorateChatClientPipeline` (see §1.2). The host
   **must** register a base `IChatClient` *before* calling this method — the decorator wraps it. The
   pipeline also registers `ITokenEstimatorCalibrationStore` (`TryAddSingleton`).
3. **Registers the instruction provider and the three in-memory tool registries** as singletons (see §1.3):
   `IAgentInstructionProvider → AgentInstructionProvider`,
   `IAgentToolRegistry → LocalAgentToolRegistry`, `IClientLocalToolRegistry → ClientLocalToolRegistry`,
   `IMcpToolRegistry → McpToolRegistry`, plus `TryAddSingleton` floors the node composition root replaces:
   `IToolApprovalPolicy → PermissiveToolApprovalPolicy` and `IToolRelevanceSelector → LexicalToolRelevanceSelector`.
4. **Registers the agent factories** — `IInvocationAgentFactory → InvocationAgentFactory` (single
   agent) and `IOrchestrationAgentFactory → OrchestrationAgentFactory` (multi-agent handoff).
5. **Registers the gated runner** — `IPlaybookEvalAgentRunner → MafPlaybookEvalAgentRunner` (golden
   eval).

## 1.2 The chat-client decorator pipeline

The section introduction is in [Agent Mode](../04-agent-mode.md#12-the-chat-client-decorator-pipeline).

### Why each hop sits where it does, and what it may mutate

| Hop | Placement rule | What it may mutate |
|---|---|---|
| `ToolInvocationObservabilityChatClient` | Above FICC, so it observes the model's *request* to call a tool before the delegate runs | Nothing — it reads the response and emits spans/logs |
| `EmptyToolOfferChatClient` | Below observability so both branches are observed alike; **above** FICC so a request whose `ChatOptions.Tools` is null or empty goes straight to the provider client and never enters the tool loop. It borrows the provider client; the FICC chain it owns disposes it | Nothing — it only picks the branch |
| `UseFunctionInvocation` (FICC) | Owns the autonomous tool loop, and keeps the **whole** executable list | Appends tool-result messages |
| `ToolRelevanceChatClient` | Below FICC so a revealed tool stays immediately callable with its wrapper intact; **above** the budgeter so `EstimateTools` measures the array actually sent | `ChatOptions.Tools`, on a clone only |
| `ProviderCallBudgetChatClient` | Below FICC so it re-budgets **every** inner tool-loop and MAF participant round; above OpenTelemetry so the recorded span reflects the budgeted set actually sent | The message list, and `AdditionalProperties` on a clone when the reasoning budget is narrowed |
| `UseOpenTelemetry` | Innermost — the documented MEAI ordering — so each provider round in a tool-calling loop emits its own `gen_ai` span | Nothing |

The OpenTelemetry source name is pinned to `"Microsoft.Extensions.AI"` because MEAI's own default
(`Experimental.Microsoft.Extensions.AI`) does **not** match the ServiceDefaults wildcard
`AddSource`/`AddMeter("Microsoft.Extensions.AI*")`, and a span emitted under the default is never
exported. `EnableSensitiveData` is set **explicitly** from the code-owned `AgentTelemetryOptions`
(default false) rather than left unset: an unset value defers to the ambient
`OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`, which Aspire injects as *true*, so leaving it
unset silently emits full prompts and completions. Setting it here makes the code the single source of
truth, and turning it on logs one loud warning at decoration time — a privacy-sensitive, deliberate
opt-in an operator must never leave running unnoticed. FICC's recovery, privacy and concurrency
policies (`MaximumConsecutiveErrorsPerRequest = 3`, `IncludeDetailedErrors = false`,
`AllowConcurrentInvocation = false`, `TerminateOnUnknownCalls = false`) are pinned in code against
upgrade drift rather than made operator-tunable; only `MaximumToolIterationsPerRequest` comes from
`AgentToolPipelineOptions`, overlaid from the `ToolPipelineMaxIterationsPerRequest` node setting.

A background job that builds its own chat client — one from `ILocalModelProvider.CreateChatClient(...)`
or from a transient llama-server endpoint — bypasses this pipeline on purpose, because for those paths
the node-boundary invariant is *which provider serves them*; that also costs them their span.
`ProviderChatClientTelemetry.WithProviderTelemetry` gives such a client the metadata-only gen_ai hop
back (model id, token counts, latency, finish reason, tool names — never message content) under the
same pinned source name, and disposing the wrapper disposes the inner client. Its `EnableSensitiveData`
is hard-coded false and deliberately does **not** read `AgentTelemetryOptions.CaptureSensitiveContent`:
the three callers — the conversation summarizer, the memory-extraction agent and the playbook analysis
agent — exist to hold a node-boundary invariant on conversation content, and inheriting the operator's
interactive-pipeline opt-in would export exactly the content those call sites are built to keep on the
node.

### Tool-relevance narrowing

`ToolRelevanceChatClient` sits between FICC and the budgeter. Above the configured threshold the model is shown an always-on core plus a relevance-ranked fill, and
recovers the rest by calling `list_tools`. Filtering **here** rather than in the offer projection is
what makes the byte-identical default structural: the offer, the runtime package, its config hash, the
tighten-only approval wrap and the `AllowedToolNames` intersection are literally unchanged code paths,
and only the options instance handed downstream is narrowed.

**Hidden is not forbidden.** The filter is a context-budget optimisation, never an authorisation
boundary. A hidden tool is one the model was not shown; if the model names it anyway it executes under
exactly today's rules — same wrapper, same policy — and an unresolvable name simply yields a not-found
result and the loop continues. Hiding never widens the authorised set and never waives an approval.

**Fitting the offer to the window (model-matrix F5).** When a single-agent turn's first round does not fit the launched
window and would fit without the tool offer, `InvocationRunner.FitFirstRoundToWindow` narrows the offer through this hop
even with the node setting off: it opens a nested `ToolRelevanceScope` with a `FittedToolOffer`, the estimated tokens
the ranked tools may use beside the pinned `list_tools` (and `ask_user`), and each other tool's cost measured from its own
definition. With it set the hop pins only `list_tools`, `ask_user` and the skill tools, so the node core cannot overflow the
window, ranks the whole array, and offers ranked tools in rank order while they fit the budget: a tool that does not fit
is skipped and the next one tried (input order where the selector did not rank). The budget holds for the whole turn,
`list_tools` included: under a fitted offer it reveals only the held-back tools that fit the budget together, in rank
order, and lists the rest as not callable in this turn; the next round sends the pinned tools, the revealed ones, and the
earlier ranked tools in rank order while budget is left, so a revealed tool displaces the lowest-ranked ones instead of
restoring the offer that did not fit. The attachment is sized against the costliest offer the hop could send in any rank
order, the largest total cost within the budget, not the largest definitions first. The count-based fit it replaced sized the
offer on the largest definitions with a six-slot floor and never ran a turn at 4,096 (V4). The budget keeps room for one
tool round-trip, a call with 128 characters of arguments and a 512-character result measured by the same budgeter
(`FittedRoundTripArgumentCharacters`, `FittedRoundTripResultCharacters`): filled to the edge, the 4B's second round
overflowed after one Calculate call (C2). With the real Default Assistant offer at 4,096 that reserve is about 170
tokens, roughly one ranked tool, and leaves a budget of about 1,050 tokens. A turn whose full offer fits pays nothing; a
larger tool result is left to the per-round trimming. This deliberately overrides the
operator's tool-relevance switch and the node core pins: the alternative is a turn that cannot run at all. The budget is a
cap on every path, including a query with no rankable word or no user text, where the selectors otherwise offer the whole
array. When not even the pinned tools plus the cheapest ranked tool fit, the turn
fails with "The tools offered with this message do not fit this model's context window", never with advice to compact,
but only when the turn would fit with no tools at all; otherwise the generic context-window message applies. The same
tool message replaces the generic one whenever the hard stop would have passed without the tools. A package with
`DisableToolRelevanceFilter` is not narrowed. The orchestration path is not fitted.

**Known limit at a 4,096 window.** The input room is 2,457 tokens (the window less the estimate margin and the
1,024-token output reserve), and the reserve covers one short tool round-trip. Tool definitions are counted in the
single-line form the chat template renders, plus the model's measured tool-template preamble, or
`TokenEstimatorCalibrationStore.DefaultToolTemplatePreambleTokens` until it is measured, so a calibrated and an
uncalibrated node fit the same offer. Measured live at 4,096 (Qwen3.5-0.8B, Qwen3.5-4B, Granite 4.1-3B, three runs
per cell, calibrated and not, 2026-10-05): a turn with one tool call and a turn with two chained tool calls complete
on all three; `list_tools` followed by a discovered tool completes on Granite and ends with the context-window error
on both Qwen models, whose round after the tool list is 41 to 65 estimated tokens over the room. The estimate ran
about 370 tokens above the real prompt there. A rule that kept half the free room for tool rounds was tried and reverted: the two pinned tools alone
exceeded that half, so nearly every agent turn was refused at the fit (model-matrix round, 2026-10-04). From 8,192
the full Default Assistant offer is sent and multi-round turns run. Tool discovery needs a window of 8,192 or more.

Rules the hop holds:

- It refuses to filter any array that does not already carry a `ListToolsFunction` **instance**,
  located by *type* rather than by name. Only the single-agent factory appends one, so orchestration
  participants and spawned sub-agents are inert **by construction** rather than by heuristic — which is
  what makes "a hidden tool with no escape hatch" unreachable, and what stops a foreign tool that
  merely takes the name from switching the filter on. The same pass both gates and binds, because the
  hop needs that exact instance anyway.
- It returns the caller's **own** options instance, reference-equal, on every path that does not filter
  (no ambient scope, an inactive scope, no tools, no `ListToolsFunction`, a count at or below the
  threshold, a blank query), so the shipped default allocates nothing at all.
- **One decision per tool array per turn.** An approval-resume send replays the history plus a user
  message whose only content is `ToolApprovalResponseContent` — no text — so re-deriving a query per
  send would resolve blank there, fall through to the full array mid-turn and strand `list_tools` on
  the previous round's binding. Once an array *has* a decision it is reused whatever the round's text
  looks like; only the no-decision-yet case still needs a query and can still pass through. For the
  same reason the query skips a text-less user message and takes the text-bearing one behind it; the
  root agent-build paths leave `Instructions` null by design (the system prompt rides the seed
  message), so the query comes from the round's messages.
- The decision is bound onto the `ListToolsFunction` in the **incoming** array — the object FICC itself
  resolves against — never onto a substitute in the clone, which nothing would ever invoke. The
  narrowed array is emitted in **input order**, so a fixed set always serialises to the same tools
  array: a stable prompt prefix and one GBNF compilation across the turn's rounds.
- The shared per-array computation runs under **no caller token**: the result is shared, so whichever
  caller happened to arrive first must not be able to cancel it. Its only bound is the one the selector
  applies to itself.
- A selector failure can never fail a turn. `IToolRelevanceSelector` is a **public** interface, so a
  node-side or future selector can throw anything at all; the unfiltered offer goes downstream
  byte-identical and the warning logs **counts plus the exception type name only**. The exception
  *object* is deliberately not passed to the sink: sinks render `Message` and every inner exception,
  and a selector failure carries the query and the tool descriptions into the failing call, so an HTTP
  or provider error that echoed its request body would write raw trajectory content to disk under a
  template that was otherwise scrubbed to counts. A cancel of the caller's own token is not caught —
  the send really is going away.
- Tool **authorisation** is never an input to the core set. The core is a fixed node-wide name set
  (work-session tools plus approval-bearing built-ins), the names this assembly owns (`ask_user`,
  `list_tools`, and the three MAF skill tools), and any tool the agent's own instructions name
  verbatim. MCP and custom tools are deliberately absent and rank like everything else. The
  instruction match is on a `\w` **word boundary** — letters, digits and the underscore snake_case
  names use — not a bare substring: a short built-in name over-pinned on any instruction that merely
  contained it inside a longer word ("ask" inside "task"), spending the saving on a tool the
  instructions never named. The trade cuts both ways: an instruction that names a tool only *inside* a
  longer token no longer pins it, and that tool drops back to being a trimmable candidate. The three
  MAF skill tools are always core because they reach the model through `AIContextProvider`s rather than
  the offer, and a skills agent that cannot see `load_skill` cannot use its skills at all.
- The turn-notice counts are `Interlocked.Exchange`d from **inside** the single-flight factory, not
  added: they are per *array*, not per round, so a turn that rebinds mid-turn on a changed array shape
  reports the array the model ended on rather than a sum that double-counts the tools both arrays held
  and overstates the "of M" the notice claims.

#### The lexical ranker and the `list_tools` escape hatch

`LexicalToolRelevanceSelector` is the shipped default and the fallback every other implementation degrades
to. It scores each non-core candidate by token overlap between the query and `name + " " + description`, in
the same shape as `LexicalPlaybookRetrievalRanker`: uppercase-normalise (CA1308-safe), split on
non-alphanumeric runs, compare ordinally. Ties — including the all-zero case — break by the candidate's
**index** in the input list, so the outcome is reproducible with no dependence on a model or external state
and CI stays deterministic without an embedding process. At or below the threshold, or with a query that is
blank or nothing but function words ("what about it, then?"), the whole array is offered and the ranker is
never touched: every score would be zero and the "ranking" would just be the input order, a worse answer than
offering everything.

Two corrections to the raw overlap shape are forced by a live round where *"Convert 100 euros to dollars,
then give me a stock quote"* hid the one tool that could answer and offered four that could not. Function
words are dropped from **both** sides, so a description cannot win a slot for containing "a", "to" or "then";
and the overlap is divided by the **square root** of the candidate's token count, so a long description cannot
win by sheer volume. Square root rather than a plain division because a full division over-corrects, making a
one-word name beat a three-word match in a paragraph — the opposite failure. The divisor is what makes the
score a `double` rather than a count, and it is computed the same way on every run, so the ordering stays
bit-for-bit reproducible.

Both rules live in `LexicalOverlapScoring`, shared through `InternalsVisibleTo` with the playbook ranker in
`Client.Application`, because the two must tokenise and score identically — two copies of this drifted once
already, and one copy is the fix. Its stop-word set covers English and German articles, prepositions,
conjunctions, pronouns and auxiliaries, and is dropped from the query so those words can never match **and**
from the candidate so they do not inflate the length divisor of a text that is merely wordy.

The core set is never ranked and never trimmed, and the fill is floored at `MinimumRankedSlots`, so a
skills-heavy agent whose core alone approaches the threshold still gets a meaningful set to choose among. The
offered array may therefore exceed the threshold: the threshold *triggers* filtering, and `core + rankedSlots`
caps it.

`ListToolsFunction` is the escape hatch — an argument-free, approval-free listing of the tools the turn held
back, which also **reveals** them so the very next round of the same turn can call one. Three properties are
load-bearing:

- **Binding is by object identity, not by key.** The send-time hop is the only layer that knows which array it
  is filtering, and the function is built long before the hop runs, so the hop binds the instance it finds in
  the **incoming** array — the same object `FunctionInvokingChatClient` resolves calls against. Substituting a
  fresh instance into the hop's clone would be provably dead code: the function-invoking layer never consults
  the clone, so the substitute would never be invoked and the escape hatch would silently do nothing.
- **An unbound invocation is defined, not exceptional.** On a round the hop passed through — at or below the
  threshold, blank query, feature disabled, agent opted out — the slot is null, the function returns an empty
  array, reveals nothing, and the turn continues.
- **A stated exemption.** Because it is appended *after* `InvocationToolResolver.ResolveAsync`, it is subject
  to neither the tighten-only node approval policy nor `AllowedToolNames`, and being absent from the package's
  allowed-tool list it does not feed the turn's `approvalPossible` flag. That is deliberate for an in-process
  listing of names the agent is already authorised for.

It is argument-free by design: an empty object schema keeps the compiled GBNF grammar's cost at zero, and a
function with no parameters is what rules out an ambient "current array" argument ever coming back. Each
listed description is clipped to `MaxDescriptionLength`, because a listing is a menu, not a second copy of the
schema.

#### The per-turn relevance scope

`ToolRelevanceScope` (`Invocation/ToolRelevanceScope.cs`) flows the turn's state as an `AsyncLocal`, in the
same shape as `ToolResultBudgetScope` and `ProviderCallBudget`: the invocation runner seeds exactly one scope
when a turn begins, and the send-time hop several awaited frames below reads it without a parameter on the
MAF / `IChatClient` surface. **The slot holds a mutable holder, not a value** — the hop and the `list_tools`
handler run below the opener, and an `AsyncLocal` assignment made in a callee never propagates back out to
it, so the per-array decisions and the pending notice counts live on a reference type the opener already
holds. That is the one departure from `ToolResultBudgetScope`, which is read-only in the callee and
tighten-only; this one is *written* by the callee, and widens no policy, because everything it carries is
tool names that were already in the turn's executable list.

An array's identity (`ArrayKey`) is a 64-bit ordinal FNV-1a hash of the name sequence **in order**, plus the
sequence itself, with a separator byte per name so `["ab","c"]` and `["a","bc"]` cannot hash alike through
concatenation. Equality compares the hash and then the names, so two arrays that hash alike are two distinct
keys with two distinct decisions rather than one silently shared one: a collision costs one extra decision,
never a wrong answer. The `list_tools` instance is bound to the **decision object**, not to a key, so a
reveal always lands on the array the model was actually looking at, and revealing is a lock-free idempotent
union — calling `list_tools` twice reveals the same set.

Each array's decision is computed **exactly once per turn** under a shared
`Lazy<Task<…>>(LazyThreadSafetyMode.ExecutionAndPublication)`, so a tool-calling loop pays one selection
rather than one per round — which is also what keeps the prompt prefix and the compiled GBNF grammar stable
within the turn. `ConcurrentDictionary.GetOrAdd` may build more than one `Lazy` under contention, but only
the published one is ever materialized, so the factory still runs once per key.

Two rules govern cancellation and eviction there, and they are the reason the code does not simply `await`
the lazy:

- **The shared computation takes no caller token.** Under a shared `Lazy<T>` the *first* caller's token would
  be the one the work runs under, so that caller cancelling — an idle-watchdog expiry, a user stop, a
  pre-first-token retry abandoning attempt 1 — would cancel the work every other waiter is awaiting, for a
  reason that had nothing to do with them, and which caller won would depend on who raced in first. The
  caller token therefore aborts only *that* caller's wait; the shared task keeps running for the others,
  bounded by whatever bound the selector applies to itself.
- **Eviction is keyed to the shared task's own terminal state**, faulted *or* cancelled, and never to a
  caller's cancelled wait: after a caller's wait aborts the shared task is still running, so the entry is
  still valid and the next caller — including a pre-first-token retry re-invoking the whole send factory —
  must reuse it rather than pay a second selection. "Terminal but unsuccessful" rather than merely faulted,
  because a computation that ends `Canceled` (an `OperationCanceledException` escaping the selector; an
  `HttpClient` timeout throws the `TaskCanceledException` subclass) is not `IsFaulted`, and caching it would
  poison the key for the rest of the turn. Eviction cannot depend on a waiter being present either, so a
  continuation hangs off the shared task itself and fires even when nobody is awaiting — otherwise the next
  caller would be handed a dead task and degrade to the unfiltered offer for free. Both removals use the
  value-comparing `TryRemove` overload, so a racing recompute that already replaced the entry is not
  clobbered.

### Provider-boundary budgeting

The outer invocation runner budgets only its two **outer** history-growth points, the initial seed and
each approval-resume. The autonomous tool loop inside `FunctionInvokingChatClient` appends tool results
and calls the provider again without the runner seeing it, and MAF participant turns are likewise
invisible to the runner — so this hop is the only place that sees, and can bound, those inner rounds.
It is gated on an ambient `ProviderCallBudget` scope seeded per invocation by the runner; with no scope
(the eval and preview-workflow runners drive the same shared client without one) it is a transparent
pass-through, except for the tool-call ceiling below.

- **Per-response tool-call ceiling.** `MaximumToolIterationsPerRequest` counts round-trips, so one runaway
  response (a 7B model once emitted 1,584 identical calls in a single turn) was otherwise unbounded. Every
  response, scoped or not, has calls identical in name and arguments collapsed to the first, then at most
  `Agent:ToolPipeline:MaxToolCallsPerResponse` (default 32) kept; the rest never reach the tool loop, so no
  result, persisted part or counter refers to them. One warning per trimmed response. In streaming each
  `FunctionCallContent` is a whole call and is decided on arrival; only an update that loses a call is
  re-yielded as a clone, everything else passes through unchanged.

- **The window** is the per-send `num_ctx` the invocation factory writes onto
  `ChatOptions.AdditionalProperties` when one is set — read here so the per-round window matches the
  window the provider is actually launched with — otherwise `ProviderCallBudgetOptions.DefaultContextTokens`;
  minus the larger of the reserved output floor and the round's own `MaxOutputTokens`. The floor is the
  scope's: the runner seeds the node's, or a package's `ReservedOutputTokensOverride` (benchmark primary only).
- **Two margins, comparison-only.** It measures against `TokenEstimatorCalibrationStore.EstimateSafetyFactor`
  of the window rather than the whole of it, because the char heuristic under-counts by roughly a tenth
  on markdown and JSON and an under-count at the window edge is a provider *rejection* rather than a
  trim. On top of that flat factor it divides by whatever real rounds of **this** model have since shown
  the residual optimism to be — tighten-only, and exactly neutral until a round has been recorded, so an
  uncalibrated model is byte-identical to before.
- **Instructions and tool definitions are fixed per-round input**: the model never sees them as a
  droppable message, but name, description and JSON schema still count against the window. Folding both
  into the overhead is what stops a tool-heavy agent from under-estimating and rounding an over-window
  request through.
- **Calibration feedback.** This hop is the only place in the process that holds both numbers for the
  **same** request: what the provider counted for the prompt, against what this hop estimated for the
  very message set it sent. The invocation runner sees a turn's last usage without the per-round
  estimate that produced it, and the outer conversation budgeter never sees usage at all. Only budgeted
  rounds carry a model name, so the pass-through paths record nothing, and neither do the summarizer and
  the other side calls that build their own per-run client. In streaming, terminal usage arrives as a
  `UsageContent` on one of the updates (llama.cpp reports it on the final chunk), last one wins —
  matching the invocation runner's own reading of the same signal — and it is recorded in the `finally`
  so an abandoned or faulted enumeration still contributes the usage it did report.
- **Elapsed time** for a streamed call covers the complete enumerator lifetime, including consumer
  backpressure while the provider request remains open: this is provider-round elapsed time, not CPU
  time.
- **An irreducible round fails here.** When the pinned set alone still exceeds the window, no further
  trimming can shrink it, and sending it would overrun the model's launched context window or be
  rejected deep inside the provider with an opaque error. Both the sync and the streaming path route
  through `ApplyBudget`, so both throw `ProviderContextWindowExceededException` *before* the inner
  client is called, with a classified, sanitized error; the cumulative-ceiling registration is
  intentionally skipped, because that round never reaches the provider. A streaming call is budgeted —
  and its ceiling enforced — before the first chunk is pulled, so a ceiling breach fails the round up
  front rather than after streaming has begun.
- **Reasoning-budget narrowing.** When the turn carries the llama.cpp thinking-budget marker, the
  provider-side clamp can only see the launched window, so on a long conversation it still permits a
  budget larger than the tokens remaining after the prompt — and a reasoning phase that eats the
  remainder returns no answer, which is the failure the budget exists to prevent. This hop is the one
  place that knows both the window and the round's estimated input, and it allows **half the remainder**,
  matching the provider clamp's split, so at least as many tokens are left for the answer as the model
  may spend thinking. The caller's `ChatOptions` is returned unchanged whenever there is no marker or
  the marker is already smaller — the overwhelming majority of rounds — so nothing is cloned on the
  common path.

#### What the per-round reducer keeps

`ProviderCallBudgeter` is a deterministic, LLM-free reducer that fits a **single** raw provider round into the
effective window —
the innermost analogue of the application layer's turn-grouped budgeter, operating on the flat message
list MAF hands the raw `IChatClient` after appending inner tool results. Its policy:

1. Always keep system messages, the most recent `ProviderCallBudgetOptions.RecentMessagesToKeep`
   messages, and the very last message — the pending tool result the model must see next. The user's current
   message (the last user message with text) is kept too: after a few tool rounds it falls out of the recent
   window, and a request without it is rejected by the chat template (llama-server: "No user query found in
   messages", model-matrix D2). When the set is over the window without dropping it, the round fails with the
   context-window error.
2. Over the window, first **excerpt** oversized tool results anywhere, oldest first, including a recent
   or pending one: excerpting is the primary size backstop and it keeps the pending tool result
   *bounded* rather than dropped.
3. Then **drop** the oldest droppable messages whole, in atomic tool-call/result **units**. A tool-call
   message and every message carrying one of its results (matched by `CallId`) form one unit dropped
   all-or-nothing: dropping only the call would orphan its result, and dropping only the result would
   orphan the call — either shape makes OpenAI/Azure reject the round with a 400. A message with no
   function-call/result content is its own singleton unit, so plain history trims exactly as before,
   and when any member of a unit is protected (system, recent-keep, or last) the whole unit is kept and
   trimming continues with older units.
4. Then **strip superseded plain reasoning**, oldest first, from assistant messages outside the in-flight round
   (the last message's tool-call unit). Only reasoning with neither a provider raw representation nor protected
   data is strippable: that is what the Chat Completions (llama-server, OpenAI-compatible, Azure) and Ollama
   adapters produce. Reasoning a Responses provider returned (Codex: a raw reasoning item, or encrypted protected
   data once streaming updates are coalesced) is never stripped from any round, because the stateless tool loop
   replays it before every retained function call. A message is dropped only when everything it held was
   strippable reasoning; a mixed message loses only its plain parts. On the llama-server path reasoning is not
   sent at all, so there the pass only removes text that was counted and never transmitted.

Units are built with union-find over shared `CallId`s, so a multi-call assistant turn or a tool message
carrying results for several calls transitively merges their components; the higher-index root is
pointed at the lower one, making a unit's canonical root its oldest message so iteration meets the root
at the position it would have dropped the first member. Excerpting is **clone-preserving** — id,
author, provider raw representation and additional properties carry over — because this hop can
re-excerpt a message the outer budgeter already excerpted, and a rewrite that reconstructed from role
plus contents alone would strip a message's identity on the way to the provider, twice over on a long
tool loop. When nothing was reducible, the original set is returned with the overrun flagged.

#### The token estimator

`ProviderMessageTokenEstimator` is conservative and allocation-light: roughly one token per weighted character plus a small fixed
per-message framing overhead, never calling the provider. Non-ASCII characters are weighted up because
byte-pair tokenizers emit far more tokens per character for CJK, structured and emoji content than the
chars/4 English heuristic assumes — the plain divisor badly **under**-counts there, which would let an
over-window round through. Images are charged a flat per-image estimate instead of being char-counted:
llama.cpp vision costs a few hundred to ~1–2k tokens per image depending on resolution and the
projector's patch grid, and counting an image as zero-cost would let a vision round overrun the window.

This is the AI.Agent-layer twin of `HeuristicTokenEstimator` in the application layer, which the outer
budgeter uses. The two live in separate assemblies by the layer arrow (Application → AI.Agent), so the
entry points remain intentionally mirrored: **a change to divisor selection, or to the per-image charge,
must be mirrored in both.** Script-category weighting is shared through `TokenCharacterProfile`.

A tool definition costs its name, description and schema text, the per-message framing, and
`TokenEstimatorCalibrationStore.ToolDefinitionWrapperTokens` (18) for the OpenAI JSON wrapper a chat template renders around
it — both budgets charge exactly that. Measured on Qwen3.8-27B with the 8 Default Assistant tools: 21.9 tokens of wrapper a
tool, 4 of them the framing. The offer (`LocalToolOfferProvider`) carries each tool's model-facing description so the outer
budget sees it; `RuntimePackageConfigHash` and the agentic MCP binding fingerprint still leave it out.

The template's own once-per-request tool instructions (~198 tokens on that template) vary per template, so they are measured
per model: `LlamaTokenEstimatorCalibrationService`, in the same round as the `/tokenize` divisor, POSTs a fixed system + user
pair to llama-server's `/v1/messages/count_tokens` (which renders the model's own chat template, tools included) once without
and once with a fixed two-tool probe set. The preamble is `count(with) - count(without) -` what the budgets already charge for
the probe tools (framed definition + wrapper, at the divisor just measured), clamped to
`0..TokenEstimatorCalibrationStore.MaximumToolTemplatePreambleTokens` and stored per model beside the divisor. Both budgets
add it **once** per request that offers at least one tool (the outer one in its fixed overhead, the inner one in the round's
tool-schema tokens), never without tools. Fail-quiet: no measurement means 0, a failed probe keeps the prior value, and the
divisor is stored either way. Measured on Qwen2.5-0.5B (pinned build, CPU): 26 tokens without, 245 with the probe tools, 135
already charged, preamble 84. The Debug line `calibration measured N chars per token and a tool-template preamble of P tokens`
shows both numbers.

Per-message and per-tool script-category profiles are memoized by instance in a
`ConditionalWeakTable` — no leak, the entry dies with its key. This hop re-estimates the full message
list and the full tool list on **every** inner tool-loop round, and those rounds reuse the same
`ChatMessage` and `AIFunction` instances (the function-invocation loop appends but never mutates prior
messages; the tool array is built once per invocation by the agent factory), so the memo collapses
repeated full-content scans — and, for tools, a full `GetRawText()` materialization of every schema —
to dictionary lookups. It is correct only because both are immutable after construction on these paths,
so a memoized value equals a fresh computation. The final division sits deliberately **outside** the
memo, so a later per-model calibration re-divides the same instance without rescanning its content.

#### The tool-call observation pair

`ToolInvocationObservabilityChatClient` sits above FICC. Streaming is the real chat path: a logical tool call streams across many updates whose argument
fragments share one `CallId`, the tool executes *below* this hop, and its `FunctionResultContent`
update flows back. One pending entry per `CallId` keeps each call to exactly one requested span/log and
one completion span, and a repeat `CallId` is a streamed fragment, not a new call. A completed
non-streaming response carries the whole function-calling turn in order, so pairing works there too —
its durations are near-zero because the tool already ran below this hop before the response returned,
while outcome, name and result hash stay accurate. Request-to-result latency is deliberately named that
way: it can include remaining argument generation and middleware work before execution. A result whose
request never flowed through this hop (only the tail was replayed) is skipped rather than given a
duration-less span.

Neither span sets `gen_ai.operation.name`. MEAI's function-invocation hop owns the conventional
`execute_tool` span for every call — `FunctionInvokingChatClient` starts it on the `ActivitySource` it
takes from the inner client, here the `OpenTelemetryChatClient`'s `"Microsoft.Extensions.AI"` — so
claiming that value again would show a convention-aware backend two executions per call. The pair is
this repo's own request/completion observation, correlated to MEAI's span by `gen_ai.tool.call.id`.
`gen_ai.tool.call.arguments` and `gen_ai.tool.call.result` carry a redacted length + SHA-256-prefix
digest rather than the payload the convention defines; that bend is deliberate and recorded in
`docs/agent-knowledge.md` §4. A non-serializable payload (a reference cycle, say) yields the sentinel
length `-1` with an `"unserializable"` marker rather than faulting the response stream.

A requested tool **name** is recorded into the budget only when it resolves against the tools the
request actually offered. A model can emit any string, and what the budget's name set feeds is durable
— the persisted step detail, and from there a work-session event detail and a node-run column — so a
name nothing offered would be recorded as a tool this run reached for, which it is not. The call is
still counted (a null name records the count alone) and the identifier is dropped; the span and the log
keep it, because an attempted call nobody offered is exactly what an operator reading the trace needs
to see.

## 1.3 Tool registries and catalog — four sources, one offer

Continues [Agent Mode](../04-agent-mode.md#13-tool-registries-and-catalog--four-sources-one-offer), which keeps the section introduction.

| Registry | Interface / impl | Source of tools | Notes |
|---|---|---|---|
| Built-in catalog | `IAgentToolRegistry` / `Tools/Implementation/LocalAgentToolRegistry.cs` | `AIFunctionFactory.Create` over in-process methods (`GetCurrentTime`, `Calculate`) | Descriptors are derived **from** the generated `AIFunction.JsonSchema` so the offered contract can't drift from what executes. Their catalog approval default is false, but the effective node policy can tighten it. |
| ClientLocal (server-driven) | `IClientLocalToolRegistry` / `Tools/Implementation/ClientLocalToolRegistry.cs` | `IClientLocalToolHandler` implementations registered by the application layer (e.g. `run_in_agent_home`, `spawn_subagent`, `web_fetch`, `web_search`) | In-process handlers, **not** SignalR. The registry holds the handler-backed tools; the worker app layer registers the handlers. |
| MCP | `IMcpToolRegistry` / `Tools/Implementation/McpToolRegistry.cs` | An immutable `AITool` snapshot pushed in by the MCP connection manager as servers connect | The registry is MCP-agnostic (only holds `AITool`); the application layer owns the MCP client lifecycle. See [Chat](../05-chat.md) and [API & Hubs](../09-api-and-hubs.md). |
| Custom Tools | `ICustomToolCatalog` / `Services/CustomTools/Implementation/CustomToolCatalog.cs` | Enabled, acknowledged `custom__*` definitions read live from SQLite on every offer/resolve | HTTP-fetch and host-command tools. The node kill-switch defaults off, each tool must be assigned to the agent, and every executable is unconditionally wrapped in `ApprovalRequiredAIFunction`. |

### Outbound MCP tools: sessions, failures and results

`McpServerConnectionManager` (`Services/Mcp/Implementation/`) owns every outbound MCP client and publishes the
tool snapshot the MCP registry serves. A registered tool is named `mcp__{slug}__{tool}`; the slug is assigned on
the server's first connect (oldest registration first, collision suffix `-2`, `-3`, …), persisted on the
registration and never recomputed, so disabling one server or renaming another never re-binds an agent's
allow-list. Each tool's executable is wrapped, innermost first: the per-call timeout
(`McpToolCallTimeoutAIFunction`), schema-driven argument repair (unknown properties allowed, since a server may
under-declare its schema), the result projection, the shared result budget, and `ApprovalRequiredAIFunction`
outermost.

**Connection lifecycle.** The manager watches each client's `Completion`. When a connected session ends — a stdio
process exits (`ServerExited`, with the exit code and a scrubbed stderr tail) or an HTTP server forgets the
session (`SessionLost`, HTTP 404) — the server moves to the error state but its tools **stay offered**. Every call
goes through one routing function (`InvokeRoutedAsync`): it uses the live session, or reconnects once (bounded by
`ConnectTimeoutSeconds`) when the session has ended, or returns a typed failure result,
`[tool error: server_unavailable] MCP server '<name>' is not connected: <reason> The next call retries; use Reconnect on the MCP page if it keeps failing.`,
which the model can read and the audit records as an error. A call whose session is lost mid-flight (404) is
retried once on a fresh session, because the server provably never ran it; any other transport fault is
reported, never retried, because a tool call is not idempotent. A registration change through `McpServerService` refreshes only that
registration (`RefreshAsync(id)`); the startup connector refreshes every enabled server, connecting them in
parallel. Connect failures carry a reason
(`McpConnectionFailureReason`: `AuthenticationRequired` for a 401 with no headers configured, `Authentication`
for a 401 with headers, `Forbidden`, `Tls`, `ServerStartupFailed`, `ServerExited`, `SessionLost`, and the older
ones); see [Security & Privacy](../12-security-and-privacy.md) §7.2 for what the text may contain.

**`tools/list_changed`.** A handler registered at connect re-lists and republishes the snapshot 500 ms after the
last notification of a burst. It fires only for servers negotiating a protocol older than 2026-07-28: on
2026-07-28 the notification arrives only on a `subscriptions/listen` stream the SDK client does not open, and that
revision has no HTTP sessions, so the `SessionLost` retry is likewise an older-protocol path.

**Session scope.** A registration's `SessionScope` is `Shared` (default: one session for every caller) or
`PerConversation`, for a stateful server whose per-session memory must not leak between conversations. The router
keys a per-conversation session by `AgentRunConversationContext.Current`, which only chat and agent runs set;
inbound MCP runs and unattended paths always use the shared session. A per-conversation session idle for 15
minutes is disposed (`TimeProvider`), and a stdio server runs at most 4 per-conversation processes: the fifth
conversation's call fails typed rather than silently sharing a session.

**Result projection.** `McpToolResultProjectionAIFunction` (`AI.Agent/Tools/Implementation/`) turns the raw
`CallToolResult` into the text the model reads: text blocks joined, one `structuredContent` copy unless a text
block already mirrors it, an `isError` result prefixed `[tool error: tool_reported]`, and an image or audio block
replaced by a placeholder such as `[image image/png, 34 KB]`. The binary block rides beside that string
(`ToolResultMedia`), so the chat persists it as an `image` part under the call's card; the model never receives
the base64. The projection sits inside the result budget, so the budget measures what the model reads. The budget
(`ToolResultBudget.Truncate`, shared by every registry) keeps three quarters head and one quarter tail with a
`[truncated: N of M chars shown]` marker, and `InvocationRunner` tightens it per turn to about a quarter of the
model's context window in characters (the window's token count), capped at 65,536 characters. A call that hits
the per-call timeout is audited with outcome `timeout`; the SDK sends the server no `notifications/cancelled`, so
the server-side work is not cancelled.

### Effective approval policy

The section introduction is in [Agent Mode](../04-agent-mode.md#effective-approval-policy).

#### The risk taxonomy (`ToolCategory`)

`ToolCategory` is declared at each tool's own definition site, mirroring how `RequiresApproval` is, and
carried on the offer descriptors into `AllowedToolDto.Category` where the policy layer reads it. The taxonomy
is deliberately coarse: it groups tools by what a call can **do**, not by which tool it is.

| Value | What it covers |
|---|---|
| `ReadLocal` | Read-only, node-local, side-effect-free reads: clock and arithmetic, the read-only coder workspace tools, the read-only knowledge-base tools |
| `WriteExecute` | Tools that can write files or run commands on the node, including a **stdio** MCP tool |
| `Orchestration` | Tools that can spawn or drive other agents or models (`spawn_subagent`) |
| `Network` | Tools reaching an external or out-of-process surface, including every **HTTP** MCP tool |
| `Unknown` | The fail-closed default for a tool that declared no category, so a new uncategorized tool is treated as approval-requiring rather than silently auto-executing |

The four work-session state tools carry `WriteExecute` in the categorized offer, which is what `GRAPH-C4-2`'s
runtime half judges a workflow Agent node on — it excludes those four by name, because every agent node is
offered them. The one write/execute gateway that is **not** in the taxonomy, `run_in_agent_home`, sits on the
ClientLocal registry seam rather than the offer and is floored by that registry's `ApprovalRequiredAIFunction`
pre-wrap instead of by a category.

`LocalChatToolDescriptor` carries the offer surface — name, description, JSON schema, approval flag, category
— without exposing the executable `AIFunction`, and its schema is derived from the function's generated schema
rather than hand-written, so the offered contract cannot drift from what the factory executes. Its
`IsFixedCustomTool` bit is set only by the custom-tool catalog, and is carried on the descriptor rather than
re-read from the store because it is the one thing the node tool-catalog response needs in order to tell an
operator whether "approve for this session" can be honored for that tool (`SessionApprovalEligibility`).

### Custom Tools execution boundary

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
approval-required tool from their offer before execution. See [Scheduler](../06-scheduler.md).

## 1.4 Single-agent invocation — `InvocationAgentFactory`

The section introduction is in [Agent Mode](../04-agent-mode.md#14-single-agent-invocation--invocationagentfactory).

### Building the agent: instructions once, skills through a context provider

`InvocationAgentFactory.BuildAgent` builds the inner `ChatClientAgent` with **no instructions on either
path**: the system instructions are delivered exactly once per request as the leading `System` seed message
(`BuildSeedMessages`, replayed by the invocation runner). Passing them to the constructor's `instructions`
parameter — or to `ChatOptions.Instructions` — as well would double-send them, because MAF forwards both to
the `IChatClient` on every invocation alongside the seed message. The agent's `name` and `description` carry
identity only and are not sent to the model as content. The result is wrapped in
`ApprovalResponseValidatingAgent`.

Both paths construct the agent through `InvocationAgentFactory.BuildApprovalValidatedAgent`, the one
`ChatClientAgentOptions` construction the approval tests also build through. It sets
`DisableApprovalResponseBinding`: since Microsoft.Agents.AI 1.22 MAF's default binding client trusts only approval
requests recorded in an `AgentSession`, so every sessionless resume would fail; `ApprovalResponseValidatingAgent`
enforces that binding instead. With one or more skills the factory builds a MAF `AgentSkillsProvider` over
`AgentInlineSkill` records and passes it on `AIContextProviders`, so the experimental surface is reached only when
an agent actually has skills. That surface (the full-frontmatter
`AgentInlineSkill` constructor plus the `AgentSkill[]` provider constructor) is stable at the pinned
Microsoft.Agents.AI version, so it carries no `MAAI001` suppression. Ownership of the provider transfers to the agent, which disposes its context
providers with itself — the reason for the scoped `CA2000` suppression. On the skills path only the agent's
own tools ride the agent-level `ChatOptions`; the per-turn `RunOptions.ChatOptions` still carries model id,
`think` and sampling.

`InvocationSkillsProvider.BuildInlineSkill` uses the full frontmatter constructor (name, description, instructions, license,
compatibility, allowed-tools, metadata), so a skill carrying no frontmatter and no resources builds exactly
as the 3-argument call it replaced did. Two rules bind it:

- **Resources must be registered before the `AgentSkillsProvider` is constructed.** The provider resolves a
  skill's content once, and the `<available_resources>` block is rendered from the resources present at that
  moment; a resource added afterwards would exist but never be advertised, so the model would have no way to
  learn it can be read.
- `allowedTools` is carried as **frontmatter only**. The Agent Skills standard defines it as pre-approval
  rather than restriction, so nothing here grants or withholds a tool on its account — the tool offer and the
  tighten-only approval policy remain the only authorities. **Scripts are never registered**: an inline
  skill's `AddScript` takes a delegate, and this node has no execution surface to bind one to.

`ResolveExecutableToolsAsync` intersects the definition's offer list with the executable catalogs by name,
through the shared `InvocationToolResolver`, so the single-agent and orchestration factories resolve tools
identically. It is also **the only site in the product that appends `ListToolsFunction`** — above the
pipeline so it is executable, and outside the offer so no runtime config hash moves — which is what makes the
relevance hop inert by construction for orchestration participants and spawned sub-agents. The threshold test
adds a skill-tool term, because the array the hop measures carries the three MAF skill tools and the array
resolved here does not; the count is read off `ToolRelevanceChatClient.SkillToolNames.Length` rather than a
second constant, so the count and the core-name list cannot drift apart. It fails **safe** either way: an
undercount skips the append, the hop then refuses to filter for lack of a `ListToolsFunction`, and the cost is
a missed optimisation on one agent shape, never a hidden tool the model cannot recover. Never relax the hop's
gate.

### Per-send sampling reaches two runtimes

`ApplySamplingOptions` applies the developer-gated per-send overrides. Native knobs (temperature, top_p,
top_k, num_predict, presence_penalty, frequency_penalty, seed, stop) ride the strongly-typed `ChatOptions`
properties; the four without a native property travel as `AdditionalProperties` entries keyed by
`SamplingOptionKeys`, the same channel `think` already proves. Each field is applied only when set and only
when it passes a defensive range guard — NaN, negative or out-of-range is skipped and the model default
kept — with temperature accepted in `[0, 2]` and the penalties in `[-2, 2]` (the UI caps), and a seed floor of
`-1`, which is Ollama's "random seed" sentinel.

Those shared keys reach **both** runtimes. OllamaSharp's `AbstractionMapper` maps all four onto the Ollama
wire (verified against the installed OllamaSharp assembly; the pin is in `Directory.Packages.props`), and
`DeferredLlamaServerChatClient.ApplySamplingPassthrough` patches `min_p`, `repeat_penalty` and
`repeat_last_n` — plus the strongly-typed `TopK`, which the MEAI OpenAI adapter drops — onto the outbound
llama-server body. `num_ctx` is deliberately **not** sent to llama-server, whose window is fixed at process
launch; there it only budgets client-side history.

Two clamps follow. An output budget larger than the context window would be rejected or truncated by the
model, so `MaxOutputTokens` is clamped to `num_ctx` when both are set. And the request budget can never exceed
the process that was actually launched: a smaller explicit `num_ctx` is preserved, while a larger explicit or
default budget is clamped down to `InvocationAgentDefinition.EffectiveContextTokens`.

A third llama.cpp marker rides the same dictionary. `xe.llama.disable_thinking` tells the llama.cpp chat
client to inject `chat_template_kwargs.enable_thinking=false`, and is set **only** when reasoning is
explicitly off on a thinking-capable model: the `think: false` written alongside it suppresses reasoning on
the Ollama wire, but the llama.cpp OpenAI adapter ignores `think`, so a Qwen3-class chat template would keep
emitting a reasoning block. Like the budget marker its literal is duplicated in
`DeferredLlamaServerChatClient`, because the AI.Agent assembly does not reference the LlamaServer provider —
keep the two in sync. An explicit sampling `ReasoningBudgetTokens` wins over the effort-derived ladder, so
benchmark replay stays exact.

### The reasoning-effort matrix and the thinking budget

`ReasoningOptionsResolver` (`Invocation/ReasoningOptionsResolver.cs`) is the single source of truth for the
effort → provider mapping, shared by the single-agent path (`InvocationAgentFactory.CreateAsync`) and the
orchestration participant path (`ParticipantReasoningOptions.Build`), so a new effort level or an Ollama
behaviour change is made once.

It writes up to four things onto `ChatOptions.AdditionalProperties`, and three of them are **in-process
markers that never reach any wire** — the OllamaSharp `AbstractionMapper` reads a fixed option allowlist and
ignores unknown keys, and Codex reads only its own:

| Key | Read by | Carries |
|---|---|---|
| `think` | Ollama | `false` / `"low"` / `"medium"` / `"high"` / `true` |
| `codex_reasoning_effort` | the Codex Responses boundary | the raw normalized effort, `minimal`…`xhigh` |
| `xe.llama.reasoning_budget_tokens` | `DeferredLlamaServerChatClient` | the per-request thinking budget |
| `xe.external.reasoning_effort` | the external OpenAI-compatible provider | the canonical lowercase effort |

Two of those literals are **duplicated** in the provider that consumes them, because the AI.Agent assembly
references no provider project: the llama budget key in `DeferredLlamaServerChatClient`, and the external key
in `ExternalProviderConstants.ReasoningEffortMarkerKey`. A test pins the external pair's two spellings
together; keep all of them in sync.

The `think` value is graded only for a thinking-capable model. `minimal` and `xhigh` are OpenAI Responses
levels Ollama does not understand — it returns 400 on an unknown think level — so they collapse to `true`
there, and the Codex boundary reads the un-collapsed level from its own key instead. A **non**-thinking model
that carries a graded level (an agent definition pinned it, or the composer kept a stale selection across a
model switch) cannot honour it, but the user still asked to reason: the caller omits the `think` field
entirely so the model's chat-template-baked reasoning runs, and only `none` or unspecified sends
`think: false`.

**The budget ladder** is a node setting (`ReasoningBudgets`, read per turn through
`INodeRuntimeSettings.GetReasoningBudgetsAsync`; Node Settings → Chat → Thinking budgets). Shipped defaults:
`minimal` 1024, `low` 2048, `medium` 8192, `high`/`xhigh` 24576 tokens. A blank (unspecified) effort gets the rung
of the **default effort** setting, shipped `low` (model-matrix 2026-10-04, operator decision 3: the earlier flat
`medium` let a 0.8B spend minutes reasoning over a five-word greeting). Only the budget follows that setting: the
turn still sends no effort, so `think` stays `true` for Ollama and an external model keeps its registered default.
Uncapped, a hard prompt reasoned until `finish_reason: length` with no answer (tester round 6). Anything else —
`none` (reasoning is being turned off, so a budget is meaningless), the binary `on` sentinel, an unrecognized
value — sends no budget at all. The marker is inert off the llama.cpp path. `minimal` and `xhigh` are mapped
rather than left null so a definition that pins a Codex-only level onto a local model does not silently get
*more* thinking than `high`. The single-agent definition, every orchestration participant and a profile-bound
sub-agent read the same node ladder.

**Reasoning off on llama.cpp** sends `chat_template_kwargs.enable_thinking=false` **and**
`reasoning_budget_tokens: 0` (`DeferredLlamaServerChatClient.ApplyThinkingSwitch`). A template that renders
`<think>` but never reads `enable_thinking` (LFM2.5) ignores the kwarg; the zero budget is its only off switch,
probed at b10201 (`.tmp/model-matrix-data/phase2/track-1a/probe-summary.txt`), and it leaves the output of a
template that honours the kwarg byte-identical. The forced empty block can surface as `<think></think>` at the
head of the content; `LeadingThinkTagStripper` drops that, and a stray leading `</think>`, from every
llama-server answer.

**When a round ends with no answer after reasoning**, the turn notice says the model stopped while thinking,
whatever the finish reason: a budget-forced end finishes `stop`, the window `length`. A model that keeps
deliberating in content after the budget closed the block and then writes a literal `</think>` has the text before
that tag moved into the reasoning at the end of the turn (`InvocationRunner.ReclassifyLeakedThinkTextAsync`,
`IWorkerEventDispatcher.ReportInvocationTextReclassifiedAsync`); the live stream showed it as answer until the
terminal event replaces it. For a thinking-capable model the check runs on every final round, also with thinking off
(LFM2.5 at effort none closes its in-content deliberation with `</think>`, model-matrix W3), and a whole
`<think>…</think>` block that opens the answer, leading whitespace aside (Qwen3.5 0.8B reopening the block after the
budget, V2), moves to the reasoning too, leaving the text after it as the answer. Only residue at the start counts: a
block that follows answer text (`Use <think>analysis</think> before the answer`, or a template in a code fence) is
quoted markup and stays, tags included. The cleanup never takes a round's only text: when nothing
would remain outside the tags (LFM2.5 at effort none answering `Okay.` followed by a stray `</think>`, or a lone
`<think>X</think>` block), the tags are dropped, that text stays the answer and no empty-answer notice fires. A
round that only reasoned on the reasoning channel and then wrote its deliberation before a stray tag therefore shows
that deliberation as the answer. A model that is not thinking-capable is left alone unless
its round reasoned, and even then a close tag preceded by its own open tag stays as quoted markup. The residual
false positives are a thinking-capable model whose answer opens with a quoted block, and any answer whose first
`</think>` has no open tag before it: the text before that tag moves to the reasoning, because a stray close tag after
deliberation (W3) looks the same.

**A tool call written inside the reasoning** (model-matrix F4: Qwen3.5 0.8B and 9B with thinking on write the next
`<tool_call>` block before `</think>`, and llama-server keeps it as reasoning) is re-prompted once: when a tool-offering
single-agent turn's final round produced nothing and its reasoning contains `<tool_call>`, the runner replays the
segment's executed calls and results without reasoning, adds one user message asking for the call outside the
reasoning, and streams one more round. From the re-prompt on, the rest of the turn runs with thinking off, the switch
effort `none` sets (`think:false`, the llama.cpp disable-thinking marker, no budget): the runner calls
`ProviderCallBudget.TurnThinkingOff` and `ProviderCallBudgetChatClient` applies it to the re-prompt round and every
later provider round of the turn. The next turn has its own budget and starts on its normal effort. A second such round
is not retried; its `EmptyAnswer` notice says the model tried to call a tool inside its reasoning. Probed at b10201:
thinking off avoided it 3/3, the nudge recovered it 5/5 (`.tmp/model-matrix-data/phase2/track-1b/probe-summary.txt`).
Live on the 9B, a thinking-on re-prompt recovered 1 of 3; a thinking-off re-prompt with later rounds back on thinking
completed 2 of 6, because the next call landed in the reasoning again; effort none completed 3 of 3.

Because these are fixed counts and neither caller knows the launched window here, the value is a **ceiling,
not a promise**. `DeferredLlamaServerChatClient.ClampToGenerationRoom` narrows it to half the room a smaller
launched window (or an explicit max-output cap) actually leaves, and `ProviderCallBudgetChatClient` narrows it
again to half of what *this round's* input leaves. llama-server honours the budget only for chat templates
with explicit think-end tags — the Qwen3/DeepSeek-R1 family the capability detector classifies as
graded-reasoning-capable — and ignores the field otherwise; that silent no-op is why
`InvocationAgentDefinition.ReasoningBudgetEnforceable` exists and why a dropped budget is reported once per
model through `ReasoningBudgetSkipLog`.

The external marker is omitted for every non-external model (it would be inert, and the no-override guarantee
says the dictionary stays byte-identical for a model that cannot read it) and for a blank or unrecognized
effort, where its **absence** is meaningful: that is what lets the model's registered default effort apply.
The whole vocabulary is carried otherwise, `none` and the binary `on` sentinel included, even though the
provider sends no field for either — both are turn-level decisions a registered graded default must not
override.

## 1.5 Multi-agent handoff orchestration — `OrchestrationAgentFactory` + `OrchestrationRunSession`

The section introduction is in [Agent Mode](../04-agent-mode.md#15-multi-agent-handoff-orchestration--orchestrationagentfactory--orchestrationrunsession).

### The idle guard: bounding a non-cooperative provider

`IdleStreamGuard` (`Invocation/Orchestration/Implementation/IdleStreamGuard.cs`) wraps a streamed
`IAsyncEnumerator<T>` with a **wall-clock** idle bound a non-cooperative workflow or provider cannot defeat.
It is the AI.Agent-layer twin of the application layer's `StreamIdleWatchdog`; the two live in separate
assemblies by the layer arrow, so the small race/abandon/bounded-dispose primitives are deliberately
duplicated rather than shared across the boundary. Two differences from the watchdog: the deadline is not a
fixed per-wait timeout owned here but an **external, re-armable** idle token — the caller's idle CTS, which
it resets per event and suspends across an approval pause — and the stop surfaces as an ordinary
`OperationCanceledException`, because this layer cannot reference the application's typed watchdog exception
and orchestration idle expiry has always surfaced as a cancellation.

Three token sources are kept deliberately separate:

- The **enumerator** is bound to a `providerCts` linked to the *outer* token only. Cancelling it is the
  cooperative stop signal. Keeping it off the idle deadline is what makes the race deterministic: the
  deadline firing does not itself cancel the pull, so the idle signal reliably wins the race and the pull is
  cancelled only deliberately, inside the timeout branch.
- The **idle token** is the caller's re-armable CTS, which the caller links to the outer token, so it fires on
  both idle expiry and outer cancellation. Outer cancellation therefore takes precedence and is reported as a
  plain cancellation, never as an idle timeout.
- An **enumeration token** from `await foreach (… .WithCancellation(token))` is folded into *both* of the
  guard's own tokens rather than added as a third stop condition — into the outer token so it cancels the
  provider and reports as a cancellation, and into the idle token so the per-pull race resolves on it instead
  of waiting out the deadline. With no enumeration token, or the same token already in the context, both
  links are inert. `GuardAsync` passes `CancellationToken.None` to the iterator because the
  `[EnumeratorCancellation]` parameter is filled in at enumeration time and would replace anything supplied
  there.

**Stop semantics are strict.** Cancellation and idle expiry are observed *before* every advancement and again
*before* every yield, including for items a pre-buffered enumerator produces synchronously, which never reach
the async race. An observed stop halts the stream before the pending item is yielded — it is never emitted —
so a stream that completes synchronously forever cannot outrun a cancel or an expired deadline, while a
not-yet-cancelled stream never has an item dropped. A buffered event that completes synchronously and
successfully is taken on a fast path with no `Task` and no timer.

**On expiry** the provider is asked to stop and given `DefaultAbandonmentGrace` (5 seconds) to unwind — small
enough that a wedged workflow cannot hold an invocation or a shutdown for long, non-zero so a cooperative
workflow unwinds cleanly and is not misreported. A cooperative provider unwinds; a non-cooperative one is
**abandoned**: its stuck `MoveNextAsync` is left running but observed off-thread so it can never raise an
unobserved-task fault, and the enumerator's disposal is handed to that same off-thread cleanup, which disposes
only once the pull has settled — disposing while a `MoveNextAsync` is pending violates the
`IAsyncEnumerator` contract. If the pull never settles the enumerator is never disposed; that is the
documented cost of bounding a provider that ignores cancellation. Because the iterator terminates on expiry, a
late item from an abandoned enumerator can never reach the consumer. Disposal on the normal path is bounded
the same way, so a hung `DisposeAsync` cannot wedge the pipeline, and both abandonment paths report through
`WorkflowWatchdogMetrics`.

### When orchestration does not compile — the degrade notice

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
