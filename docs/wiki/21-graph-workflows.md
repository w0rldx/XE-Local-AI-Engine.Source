# Graph Workflows — Operator-Authored DAGs

> Reviewed: 2026-09-07 · Code-grounded.

**Graph Workflows** let an operator draw a directed acyclic graph of agent turns, tool calls, conditions and human
pauses, save it, and start runs of it. The engine executes the run from the database: every node run is a row, every
change is an append-only event, and the process that advances it holds no authoritative state of its own. Restarting
the node loses at most the work that was in flight.

The module spans the whole stack: `Client.Application/Services/GraphWorkflows/` (the parser, the state machine, the
dispatcher and the node lanes), `Client.Persistence` (four tables, per-column AEAD encryption), the
`LocalApiRoutes.GraphWorkflows` endpoint family plus `GraphWorkflowRunHub`, and
`Client.React/src/features/graphWorkflows/` (a React Flow editor and a read-only run view).

**It replaced Open Canvas.** The Preview / Open Canvas visual builder was removed in the same slice that turned this
feature on by default; saved canvases are converted into Graph Workflow definitions once, automatically, on the first
start of the new build. That conversion is §9 and it is irreversible — read it before you upgrade a node with
canvases you care about.

---

## 1. What it is, and what it is not

**What it is:**

- **Operator-authored.** A definition is a graph the operator drew. Nothing generates one, and no agent may edit one.
- **Manually started.** A run begins because a person pressed Start (or a caller posted to the runs route with an
  idempotency key). There is no schedule, no trigger, no event subscription in v1.
- **Database-as-truth.** `GraphWorkflowDispatcher` re-reads the rows on every tick and writes back through the store.
  Its only in-memory state is a parsed-graph cache keyed by run id, which exists to avoid decrypting and re-parsing
  the pinned blob on every tick and can be dropped at any moment without changing an answer.
- **Durable across a restart.** A run outlives the browser tab and the engine. `GraphWorkflowStartupReconciler` makes
  the node runs a crashed host left in flight judgeable again, exactly once, before the dispatcher's pumps start.
- **Acyclic.** `GraphWorkflowGraph.EnsureAcyclic` refuses a cycle at save time. There is no loop construct.

**What it is not:**

- **Not a general orchestration language.** No boolean algebra in a condition (two comparisons are two edges into an
  `All` join), no expressions, no variables, no sub-workflows, no map over a collection.
- **Not MAF Workflows.** `Microsoft.Agents.AI.Workflows` is not referenced anywhere in `Client.Application`; the
  routing here is this module's own state machine, and an `Agent` node reaches the model through the same headless
  invocation stack a scheduled saved-agent run uses.
- **Not a write surface for agents.** A `Tool` node may only run a built-in read-local tool that needs no approval
  (§4.3). A node runs unattended, so there is nobody to ask.
- **Not Dev Workflows.** [Development Workflows](10-react-client.md) run a fixed template over a code work item with
  gates, interventions and artifacts. Graph Workflows are free-form graphs with no work item and no artifacts, and
  several of their types are deliberate copies trimmed of what has no meaning here — there is no `Blocked` node-run
  state and no `Waived` edge state, because v1 has neither retry routing nor a waiving decision.

---

## 2. The graph contract

One JSON document per definition, stored encrypted as `graph_json`. `GraphWorkflowGraph.Parse` is the only entry
point and **parsing is the validation**: a graph that survives it is one the dispatcher can route without a second
opinion. Save time and run start call the same parser, so a graph accepted at save is a graph that will start.

```jsonc
{
  "schemaVersion": 1,
  "nodes": [ /* … */ ],
  "edges": [ /* … */ ]
}
```

`schemaVersion` is optional but, when present, must be `1`. Anything else is refused outright.

### 2.1 Nodes

| Member | Required | Meaning |
|---|---|---|
| `key` | yes | 1–64 characters of letters, digits, `_` and `-`. Node and edge keys share **one** namespace. |
| `kind` | yes | One of `Start`, `Agent`, `Tool`, `Condition`, `Parallel`, `Join`, `Pause`, `End`, by NAME. |
| `label` | no | Display text. Defaults to the key. |
| `joinPolicy` | no | `All` (default) or `Any`. A property of **every** node — see §2.3. |
| `maxAttempts` | no | Positive. Defaults to **3** for `Agent` and `Tool`, **1** for every other kind. |
| `timeoutSeconds` | no | Positive. Overrides `DefaultNodeTimeoutSeconds` for this node only. |
| `position` | no | `{ x, y }`, both numeric. Authoring metadata the runtime never reads. |
| `config` | no | The per-kind settings, discriminated by `kind` — see §4. |

`config` is closed per kind. `GraphWorkflowGraph.ConfigMembers` lists exactly what each kind reads, and a member no
node of that kind reads is an author-time error rather than a setting that silently does nothing. Writing a Tool
node's `toolName` on an Agent node fails the save.

A node without a `position` is laid out client-side when the definition is opened
(`features/graphWorkflows/models/GraphWorkflowLayout.ts`). That matters for §9: imported graphs carry no positions.

### 2.2 Edges and conditions

| Member | Required | Meaning |
|---|---|---|
| `key` | yes | Same charset and namespace as a node key. Its identity — which is what makes parallel edges expressible. |
| `from`, `to` | yes | Node keys the graph declares. An endpoint the graph does not declare is a structural refusal. |
| `label` | no | The named outcome the source's output document reports as its `branch`. |
| `condition` | no | `{ path, op, value }`. Absent means unconditional. |
| `sourceHandle` | no | Editor metadata. Read past and never stored. |

A condition is a single declarative comparison **against the source node's output document**, evaluated by
`GraphWorkflowCondition.Evaluate`. It never sees the target's input.

- `path` is a **dot path** and nothing else: property names separated by `.`, with no wildcards, no array indexing and
  no functions (`GraphWorkflowTokens.IsDotPath`). `items[0].name` is refused at save rather than saved as a property
  literally called `items[0]`.
- `op` is one of `Eq`, `Ne`, `Gt`, `Gte`, `Lt`, `Lte`, `Exists`, `NotExists`, parsed **by name** (case-insensitively;
  a numeric token is refused, because `Enum.TryParse` would otherwise hand back an operator no member has).
- `value` must be a scalar — string, number, boolean or null. An object or array has no comparison to make, and a
  relational operator against a boolean could never fire, so both are refused at authoring time.
- Evaluation **fails closed**: a path the output does not carry answers `false` for every operator except
  `NotExists`. An edge must never fire on data that is not there.
- Numbers compare exactly — `long`, then `decimal`, then `BigInteger` for integer tokens past decimal's range — so a
  `Gt` over ids past 2^53 answers on the numbers rather than on a rounding artefact. `double` is the fourth and last
  arm (`GraphWorkflowCondition.Order`), reached only by the fractional and exponent tokens no exact arm reads; two
  that differ only past roughly 17 significant digits therefore read as equal, which is the chain's stated ceiling. A
  token not even `double` reads is not an ordering at all. Strings compare ordinally. A type mismatch is not an
  ordering and reads as "no".

A `Condition` node may carry a default `path` in its own config; its out-edges inherit it when their condition omits
one. That is authoring convenience only — the comparison still lives on the edge. An edge that resolves a path from
neither is refused, because fail-closed would otherwise make it an edge that silently never fires.

Two edges over the same `(from, to)` pair are legal and are how an author widens a branch. **At most one of them may
be unconditional**, since a second unconditional edge could only repeat the first.

### 2.3 `joinPolicy` is on every node

This is the trap the module documents most loudly, in `GraphWorkflowEnums.cs`, in `GraphWorkflowStateMachine.Admission`
and again here: **`joinPolicy` is a property of every node, not of `Join` nodes.** An ordinary node with two inbound
edges joins them exactly as a `Join` node does.

- `All` (the default) waits for every inbound edge to be satisfied. One dead inbound edge skips the node.
- `Any` proceeds on one satisfied edge, but only once no sibling could still satisfy one. The parser refuses `Any`
  with fewer than two inbound edges: one edge is an `All` written confusingly, and none would never fire.
- `All` over **zero** inbound edges is vacuously satisfied, and that is load-bearing — it is how the `Start` node
  becomes eligible.

`Pending` outranks `Dead` under both policies. A dead inbound edge already settles what an `All` join will do, but
settling it while a sibling branch is still running would skip the node, and everything after it, in front of work
the run has not finished.

By the same rule, `Parallel` and `Join` are **labels, not semantics**. Fan-out is any node with more than one
satisfied out-edge; fan-in is the join policy. The two kinds exist because they are inline nodes that write two rows
each, which is what makes the timing of a fan-out visible in the event log at all.

### 2.4 Whole-graph rules

Structural failures **throw immediately** — there is nothing useful to say about the rest of a graph nobody can walk.
Everything after them **accumulates**, keyed to the node or edge it belongs to, so an author fixing a canvas gets
every complaint at once (`GraphWorkflowValidationResult`).

Throw-first:

- exactly one `Start` node, with nothing routing into it;
- at least one `End` node, or no run could ever complete;
- acyclic;
- every node reachable from `Start`.

Accumulated, per element:

- a non-`End` node with no outbound edge (a run reaching it would stop without reaching an `End`);
- an `End` node with an outbound edge;
- `joinPolicy: "Any"` with fewer than two inbound edges;
- a `Condition` node with fewer than two out-edges, or more than one unconditional out-edge;
- a `Pause` node offering a decision no out-edge fires on — checked through the state machine's own routing over the
  document a pause actually produces, so the pre-flight rule and the routing cannot disagree;
- a second unconditional edge between one pair of nodes.

**Warnings are a second list, and they never refuse.** `GraphWorkflowGraph.Warnings` is computed on the first ask
rather than during the parse — only the validate endpoint asks, and every dispatcher tick parses — and nothing in it
reaches `GraphWorkflowValidationException`. A graph that warns **saves, validates as `valid`, and runs**. There is one
warning in v1: a node whose inbound edges **all** leave a `Pause` receives the decision document rather than the
content that was approved (§4.6). It is keyed on that node rather than on the pause, because that is the node which
loses the content and so the node an editor draws the badge on, and one warning is raised however many pauses reach
it. The sentence **names** the pause's nearest non-`Pause` ancestor only when that ancestor is unique and is not a
`Condition`: with two candidates the advice would have to pick one, and a `Condition` cannot be named because the edge
it would ask for is that node's second unconditional out-edge, which the parser refuses — advice that turns a warning
into an error is worse than the generic sentence.

The node cap runs **first of all**, ahead of every rule above. `MaxNodesPerDefinition` reaches the parser as an
argument rather than a dependency (`GraphWorkflowGraph.Parse(graphJson, maxNodes)`, defaulted to no cap so the parser
stays testable without a container; `GraphWorkflowGraphContract.ValidateAndCountNodes` passes the option through), and
it is checked against the **declared length of the `nodes` array** before a single node is read or an edge walked. A
cap applied after the parse would bound nothing about the parse that produced it: only the 1 MiB body limit stood
between a request and a chain of thousands of minimal nodes, and the acyclicity walk used to spend one stack frame per
node, so a deep enough chain overflowed the thread-pool thread's stack — a process kill, not a 400. That walk is now
iterative over an explicit stack, which is also what keeps the deliberately **uncapped** re-parses of a stored graph
safe (§3.4). The refusal for a cycle names every node on it in walk order (`a -> b -> c -> a`), starting and ending at
the node the walk came back to.

### 2.5 A complete example

The eight-node graph below is the module's canonical shape — `Start → Agent → Condition → { Pause | Tool } →
Parallel → Join → End`. It is the React tests' `eightNodeGraph` fixture
(`features/graphWorkflows/test/GraphWorkflowFixtures.ts`) with every node's `position` and `analyze`'s
`timeoutSeconds: null` left out for reading — the graph is otherwise the same one.

```jsonc
{
  "schemaVersion": 1,
  "nodes": [
    { "key": "start",  "kind": "Start",  "label": "Start",
      "config": { "inputSchema": null, "defaultInput": null } },
    { "key": "analyze", "kind": "Agent", "label": "Analyze", "maxAttempts": 3,
      "config": { "agentDefinitionId": null,
                  "instructions": "Summarise the request and say whether it needs a human review.",
                  "model": null, "reasoningEffort": null,
                  "responseJsonSchema": { "type": "object",
                                          "properties": { "requiresReview": { "type": "boolean" } } },
                  "includeUpstreamOutputs": true } },
    { "key": "check",  "kind": "Condition", "label": "Needs review?",
      "config": { "path": "output.json.requiresReview" } },
    { "key": "review", "kind": "Pause", "label": "Human review",
      "config": { "prompt": "Approve the analysis?",
                  "allowedDecisions": ["Approve", "Reject"], "requireComment": false } },
    { "key": "lookup", "kind": "Tool", "label": "Read file", "maxAttempts": 3,
      "config": { "toolName": "read_file", "arguments": { "path": "notes.md" },
                  "argumentBindings": { "path": "output.json.path" } } },
    { "key": "fanout", "kind": "Parallel", "label": "Both", "joinPolicy": "Any", "config": {} },
    { "key": "merge",  "kind": "Join",     "label": "Merge", "joinPolicy": "All", "config": {} },
    { "key": "done",   "kind": "End",      "label": "Done",  "joinPolicy": "Any",
      "config": { "outcome": "completed", "resultPath": null } }
  ],
  "edges": [
    { "key": "e1", "from": "start",   "to": "analyze" },
    { "key": "e2", "from": "analyze", "to": "check" },
    { "key": "e3", "from": "check",   "to": "review", "label": "yes",
      "sourceHandle": "true",  "condition": { "op": "Eq", "value": true } },
    { "key": "e4", "from": "check",   "to": "lookup", "label": "no",
      "sourceHandle": "false", "condition": { "op": "Ne", "value": true } },
    { "key": "e5", "from": "review",  "to": "fanout", "label": "approved",
      "sourceHandle": "Approve",
      "condition": { "path": "output.decision", "op": "Eq", "value": "Approve" } },
    { "key": "e6", "from": "lookup",  "to": "fanout" },
    { "key": "e7", "from": "fanout",  "to": "merge" },
    { "key": "e8", "from": "merge",   "to": "done" },
    { "key": "e9", "from": "review",  "to": "done",   "label": "rejected",
      "sourceHandle": "Reject",
      "condition": { "path": "output.decision", "op": "Eq", "value": "Reject" } }
  ]
}
```

Two details in it are the rules of §2.4 doing their job, and both are easy to get wrong:

- **`e9` exists because `review` offers `Reject`.** A `Pause` node whose allowed decision has no out-edge that fires
  on it is refused at save — answering it would strand the run.
- **`fanout` and `done` declare `joinPolicy: "Any"`.** The `check` node's two branches are mutually exclusive, so
  under the default `All` join each would wait forever for a branch that was never taken, and the run could never
  reach an end.

`e3` and `e4` carry no `path`: they inherit `check`'s `config.path`.

---

## 3. Run lifecycle

### 3.1 Starting

`POST graph-workflows/definitions/{definitionId}/runs` answers **202** with the run id.
`IGraphWorkflowRunService.StartAsync` validates, commits a durable intent and signals the dispatcher, which advances
the run out of band — so the run legitimately reads `Pending` when the answer lands.

The caller's `requestId` is the idempotency key. The lookup before the insert is a fast path two concurrent callers
can both pass; the unique index on `request_id` is the real guarantee, and a reused id naming a **different**
definition is refused rather than answered with a run the caller never asked for.

At start the run **pins its own copy of the graph** (`graph_json` and `graph_hash` on the run row), and
`StartRunAsync` commits the run row, **one `Pending` node run per graph node** and the `run.created` event in one
transaction, re-reading the definition inside it so a delete racing the start cannot leave an orphan run. Every node
of the pinned graph therefore has a row from the moment the run begins — a `Pending` row means "not yet judged", not
"not yet reached". Editing or deleting the definition afterwards never rewrites a run.

Two checks happen again at start rather than being trusted from save time, because the world moves in between: the
graph is re-parsed with the same parser, and every `Tool` node's tool is re-checked against the live catalog (§4.3).
The run also refuses a graph over `MaxNodeRunsPerRun` and an input over `MaxRunInputBytes`.

### 3.2 Run statuses

`Pending → Running → WaitingForApproval → Completed | Failed | Cancelled`, plus `Cancelling`
(`GraphWorkflowRunStatus`). There is no `Interrupted`: runs auto-resume after a host restart and only node runs
reconcile. `Cancelling` exists because cancel is fire-and-forget — the endpoint commits an intent and returns 202,
and live node runs drain first.

The run's status is **recomputed from scratch at the end of every tick**, never accumulated
(`GraphWorkflowStateMachine.Recompute`). It is denormalized so a reader can answer "what is this run doing" without a
join. The recomputation is graph-aware, and the ordering matters:

1. A **failed** node run outranks everything: the run is `Failed`, carrying that node's own failure class and reason.
   When several failed, the one with the lowest node key ordinally is picked, so two readers of the same run cannot
   disagree.
2. Otherwise, if a **terminal** node of the graph (one no edge leaves) `Succeeded`, the run is `Completed`.
3. Otherwise the run is `Cancelled`, with a reason naming the ends it did not reach and what became of them. A run
   whose tail was skipped, or whose rejection routed down a branch that skipped the remainder, therefore reads
   `Cancelled` rather than `Completed` like a run that did its job.

While node runs are still live, `WaitingForApproval` outranks `Pending`: every node run exists from the start, so
there are almost always `Pending` rows, and reading those as `Running` would report a run blocked on an unanswered
pause as busy — the one thing the two statuses exist to tell apart.

`GraphWorkflowFailureClass` records why: `None` — the default, carried by everything that has not failed — then
`NodeFailed`, `Timeout`, `AttemptsExhausted`, `OutputTooLarge`, `GateRejected`, `ValidationFailed`, `Cancelled`,
`Interrupted`. `GateRejected` narrows where a reader should look; it
is not a causal proof, because a rejection can route into a branch that then runs perfectly well.

### 3.3 Node-run statuses and admission

`Pending → Queued → Running → Succeeded | Failed | Skipped | Cancelled`, plus `WaitingForApproval`
(`GraphWorkflowNodeRunStatus`). `Queued` and `Running` are separate because an admitted node run is not yet
executing. There is **no `Blocked`**: v1 has no retry routing, so there is no retries-exhausted intervention state to
park a row in.

`GraphWorkflowStateMachine.Admission` answers `Wait`, `Eligible` or `Skip` for a `Pending` row from its inbound edge
states alone. An inbound edge is `Satisfied` when its source succeeded **and** its condition fired, `Dead` when the
source settled any other way or its condition did not fire, and `Pending` until the source is terminal — a source
with no row yet is a wait, not a refusal.

A skipped row records **one** cause, and prefers a branch that broke or was skipped over one a condition merely
routed past: a `Condition` node taking its other branch is the graph working, not news
(`GraphWorkflowStateMachine.SkipReason`).

Retry is **in place**: `Failed → Pending` is the one edge out of a terminal node-run status. A failed row under both
the node's `maxAttempts` and the run's `MaxTotalAttempts` goes back to `Pending` with the attempt incremented, in one
atomic write, and the `node.retried` event carries the failure the row cleared. Only `NodeFailed`, `Timeout` and
`Interrupted` are retryable (`GraphWorkflowFailures.IsRetryable`) — an over-cap document, a refused gate, a cancelled
run and a graph that no longer declares a node all produce the byte-identical answer next time.

A consequence worth stating plainly: a node declaring `maxAttempts: 1` reports `AttemptsExhausted` on its only
attempt, because the node's budget is genuinely why nothing will try again. What actually went wrong survives on the
row's reason and on its `node.failed` event.

### 3.4 The tick

`GraphWorkflowDispatcher` is one loop with two pumps — a signal channel (bounded, drop-on-full, because a signal is a
latency hint) and a sweep every `DispatchIntervalMilliseconds` over every live run. A dropped signal costs at most one
interval of latency, never correctness. The first sweep runs immediately at startup rather than after an interval, so
recovery does not pay for one.

**Every dispatch-side status write happens inside a serialized `AdvanceOnceAsync` call.** A lane's work produces a
pollable *result* and never transitions a row itself; the only other writers to a run are the human command paths.
One tick, in order, and the order is load-bearing:

1. **Poll.** Ask every lane what became of the work it was driving and settle what landed — before anything reads the
   rows for a decision, or the run judges its graph against a row that is only still `Running` because nothing asked.
   A row its lane had nothing to say about is offered to its deadline instead.
2. **Drain** (only while `Cancelling`). A drain admits nothing. Every terminal is reached through it or through the
   "nothing is live any more" recomputation, because writing a terminal over live node runs would strand them under a
   run no tick looks at again.
3. **Retry** the failed rows that still have budget.
4. **Admit** the eligible `Pending` rows, and skip the ones every path into which is dead.
5. **Recompute** the run's own status, against the version it was read at.

Deadlines are re-derived from the row every tick — `started_at_utc` plus the node's `timeoutSeconds` or
`DefaultNodeTimeoutSeconds` — never armed in memory, so they survive the restart that would otherwise leave a node run
bounded by nothing. `GraphWorkflowDeadline.Grace` adds 30 s before the run ends a row itself: the lane bounds its own
turn by the same number from a slightly later moment, and ending the row the instant the number is reached would race
a better answer and sometimes win by milliseconds.

### 3.5 Restart

`GraphWorkflowStartupReconciler` is an `IHostedService` registered **before** the dispatcher, so its pumps cannot
admit a row recovery has not judged. It reads the interrupted set — exactly `Queued ∪ Running`, never
`WaitingForApproval`, which is a durable human wait rather than in-flight work — and hands its verdicts to the store
to apply in one transaction. Exactly-once survives a crash during recovery: a host that dies before that commit
leaves the rows as it found them, and the next boot judges them from the same evidence. Recovery makes at most three
passes, and the last one settles whatever is left rather than walking away from it.

The verdicts:

- A `Queued` row, or a `Running` **inline** row, collapses to `Pending` without touching `Attempt`. Neither is a
  failure, so neither costs an attempt.
- A `Running` **`Agent` or `Tool`** row is **failed** `Interrupted`. The work was an in-process task with no durable
  handle, so its partial output died with the host. Recovery never re-attempts it; the dispatcher's retry stage does
  on its first tick, if and only if the node and run budgets allow.

That is the same "never resume a provider stream, start a fresh attempt instead" posture Development Mode records in
[ADR 0001](../adr/0001-development-mode-restart-recovery.md), applied without that design's replacement-attempt rows:
a graph workflow node run retries **in place** with the attempt incremented, because per-attempt history lives in the
event log rather than in a second row.

The reconciler touches no run row. The dispatcher's first sweep recomputes the run's status from the rows recovery
left behind.

---

## 4. The node kinds

Every kind's output goes through **one writer**, `GraphWorkflowDocuments`, which composes the common envelope, derives
the `branch` and enforces the size cap. No executor composes a document itself, because a second implementation of
any of those is a way for an executor to disagree with the routing the dispatcher will do a moment later.

**The output envelope** — what an edge condition reads:

```jsonc
{ "status": "succeeded" | "failed" | "skipped",
  "attempt": 1,
  "branch": "approved" | null,   // the label of the first CONDITIONAL out-edge that fires; null when none did
  "output": { /* per-kind, below */ } }
```

**The input document** — what an executor is handed:

```jsonc
{ "run":      { "input": /* the run's start payload */ },
  "upstream": { "<nodeKey>": /* that node's output envelope */ },
  "input":    /* the single satisfied predecessor's whole envelope, the upstream map when there
                 is more than one, or null when there is none — which is what Start sees */ }
```

`branch` is written even when null, so a reader can tell "no branch fired" from "this document predates branches".
An unconditional out-edge never names a branch: it accepts everything, so it says nothing about which way the run
went. The composed document is capped at `MaxOutputJsonBytes` in **UTF-8 bytes** — a character count would let astral
text through at four times the cap — and a node whose document exceeds it fails `OutputTooLarge`, which is not
retryable.

`Start`, `End`, `Condition`, `Parallel` and `Join` are **inline** (`GraphWorkflowInlineExecutor`): their work is a
pure function of rows the tick has already read, so they run inside the tick with no `Queued` hop. They still write
two rows each, which is what makes the timing of a fan-out visible in the event log.

### 4.1 `Start`

Config: `inputSchema`, `defaultInput` — both optional raw JSON, and both authoring metadata in v1 (the run's input is
validated for size, not against the schema).

Output: `{ "input": <the run's start payload> }`, handed to everything downstream.

### 4.2 `Agent`

One headless saved-agent turn, on its own in-flight lane sized at `MaxConcurrentRuns`
(`GraphWorkflowAgentExecutor`). It drives `IInvocationRunner` from the tick and never inside it; the turn is an
in-process task with no durable handle, which is exactly why an interrupted `Running` row is failed rather than
resumed (§3.5). `InvocationId` is written on the row as the correlation id in the node logs for a turn nothing else
survives.

| Config member | Meaning |
|---|---|
| `agentDefinitionId` | Optional saved agent to bind. Null runs an unbound persona from `instructions` alone. |
| `instructions` | **Required.** The seed user turn. |
| `model` | Optional model pin. Travels as written and is matched against the catalog **at run start**, exactly as an agent definition's own pin is, so a graph does not become unsaveable because a model was uninstalled after it was authored. |
| `reasoningEffort` | Optional, one of `none`, `low`, `medium`, `high`. Checked at **save** — its vocabulary is closed and cannot go stale. |
| `responseJsonSchema` | Optional. Must be an **object** schema (`"type": "object"`), because the parsed answer lands at `output.json` and a condition reads a property off it. |
| `includeUpstreamOutputs` | Defaults **true**. Inlines the `upstream` map into the prompt, budgeted at `MaxRunInputBytes` and truncated with an explicit marker rather than silently. |

Output: `{ "text": …, "json": … | null, "usage": { inputTokens, outputTokens, totalTokens, reasoningTokens,
durationMs, finishReason, model } }`. Every usage member is nullable: the runner reports what its provider gave it,
and no provider reports all of them.

**What a response schema actually enforces.** llama-server compiles it into a real GBNF grammar and applies that
grammar from the first output token, so the **structure** is guaranteed: the object shape, the declared types, an
`enum`'s member list, the required keys. The compiled `root` rule leaves the model's `<think>` block optional and
unconstrained and forces the schema only on what follows, so a reasoning model is not fighting the grammar. What is
**not** enforced is every value bound — `minLength`, `maxLength`, `pattern`, `format`, the numeric minimum and
maximum, the item counts, the content encoding. Those never reach llama.cpp at all: the
`Microsoft.Extensions.AI.OpenAI` strict-schema transform relocates them into a `description` string on the way out of
the .NET process, where they are advice to the model rather than a constraint (a `maxLength: 3` produced a
1302-character field in the S6 live round). The same transform also marks every declared property `required` and sets
`additionalProperties: false`, so a property the author left optional is not optional in practice. Validate a value
bound downstream, in an edge condition or the consuming node, and never in the schema alone. See
`docs/agent-knowledge.md` §3 for the evidence and the `--verbose` recipe that shows the compiled grammar.

A node declaring a response schema must come back a JSON **object**. A parse failure fails `NodeFailed` — the
retryable class, since a re-ask under the same grammar can land where one attempt did not — naming the finish reason,
because a truncated answer is still `Completed` and `length` is the common cause. There is deliberately no salvage
path: grammar-constrained output carries no fences, and stripping some would quietly mask a broken grammar. See §8
for what a response schema costs at the llama.cpp grammar layer.

**An Agent node is node-local only.** If the node's effective model resolves to a cloud model the node run is
refused `ValidationFailed` before any capacity is reserved: an unattended run must never hand the operator's
content to a cloud provider. Approval-required tools are likewise stripped from the offer, and the turn runs
with `IsUnattended` set — the same posture a scheduled saved-agent run holds.

The runner's own watchdog reports a timeout as a failed terminal, which is why the executor classes it `Timeout`
rather than `NodeFailed` — a node that ran out of time deserves a different answer from one whose provider said no.
Provider errors never reach the row: the reason says "see the node logs" and the detail stays there.

### 4.3 `Tool`

One built-in tool call, on its own lane, through `IToolInvocationService` (`GraphWorkflowToolExecutor`).

| Config member | Meaning |
|---|---|
| `toolName` | **Required.** |
| `arguments` | Optional object of literal arguments. |
| `argumentBindings` | Optional map of argument name → dot path, resolved against the node's input document. A binding **wins** over a literal of the same name. |

Output: `{ "result": … }` — embedded as JSON when the tool answered with an object or an array, and as a string
otherwise. That one try-parse is what lets a downstream `Condition`, which passes its predecessor's output through
verbatim, dot-path into a structured answer. Stated rather than hidden: plain text that happens to be a JSON object
is embedded as JSON, and the tools that do that mean it.

**A Tool node passes two gates, not one** (`GraphWorkflowToolGate`, ruling D6 and
[ADR 0006](../adr/0006-agentic-trust-mcp-key-scopes-and-auto-approval.md)):

1. the tool must be a **built-in** tool in the `ToolCategory.ReadLocal` envelope, and
2. the **composed** approval policy must answer that it needs no approval.

Both are asked of the same catalog at **definition save** and again at **run start**, and the run-start answer wins.
A tool that was read-only when the graph was saved does not execute after a policy tightened. A tool outside the
envelope is an **error**, never a warning: a workflow node runs unattended, so a write, execute or approval-gated
tool has nobody to ask. Errors are keyed by node key, so the editor draws them on the offending node.

`graph-workflows/tools` is the picker's feed, filtered server-side by the same service the runtime invokes through,
so the picker cannot offer a name the run would then refuse. A missing binding path fails the node
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
| `allowedDecisions` | **Required**, non-empty, distinct, from `Approve` / `Reject`. Empty would be a question nobody could answer. |
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

Output: `{ "outcome": …, "result": … }` — the resolved path, or the whole input document when the author named none.
A path the document does not carry resolves to `null`; failing the node instead would end a run that did all of its
work over a projection nobody reads.

An `End` node is a terminal node by construction (nothing may leave it), and a run is `Completed` only once one of
them **succeeded**.

---

## 5. API and hub

Full route table and hub inventory: [API & Hubs](09-api-and-hubs.md). `LocalApiRoutes.GraphWorkflows` is the family;
the whole surface, hub path included, sits behind request-path middleware in `Program.cs` that answers **404** when
`GraphWorkflows:Enabled` is false — ahead of the security middleware, so the switch cannot be probed by status code.
Every route is Operator-gated.

| Route | Notes |
|---|---|
| `graph-workflows/definitions` | GET lists **without** the graph blob (it is the encrypted column); POST creates. |
| `graph-workflows/definitions/{definitionId}` | GET / PUT with the version it was edited from / DELETE, which 409s while a live run pins the definition. |
| `graph-workflows/definitions/validate` | POST a graph and get its errors **and warnings** back without saving. The editor asks the runtime's own parser. `valid` is still zero ERRORS: a graph that only warns passes here and saves. |
| `graph-workflows/tools` | The Tool node picker's feed (§4.3). |
| `graph-workflows/definitions/{definitionId}/runs` | POST start → 202 with the run id; `requestId` is the idempotency key. |
| `graph-workflows/runs` | The run list, newest first, `?status=&limit=` with `limit` required and capped at 200. |
| `graph-workflows/runs/{runId}` | One run, its node-run **summaries**, the run's own resolved `output`, and the **graph this run pinned at start**. No node-run documents — those are a per-node read. |
| `graph-workflows/runs/{runId}/cancel` | 202. Live node runs drain first, so the run reads `Cancelling`. A repeat cancel is an idempotent 202. |
| `graph-workflows/runs/{runId}/nodes/{nodeKey}` | One node run in full, input and output documents included. |
| `graph-workflows/runs/{runId}/nodes/{nodeKey}/decide` | Answers a pause (§4.6). |
| `graph-workflows/runs/{runId}/events` | The event log, paged from an **exclusive** `afterSeq`, capped at `EventReplayLimit` — which the response reports rather than leaving a client to infer it from a full page. |

Four routes cap the request body at **1 MiB** (`GraphWorkflowRequestSizeLimit`): create, update and validate, which
carry a graph, and start-run, which carries an input document. Without it they would inherit Kestrel's 30 MB default
and a body that size would be parsed, walked and hashed before the node cap could refuse it. A name is capped at 200
characters, a description at 1024.

**The run detail carries the run's own graph.** `GraphWorkflowRunResponse` is `(Run, NodeRuns, Output, Graph)`.
`Graph` is the copy this run **pinned when it started**, not the definition's current one, in the same wire shape
`GraphWorkflowDefinitionResponse.Graph` carries — so a client parses a definition and a run with one piece of code.
It sits on the run DETAIL and deliberately not on the run summary: a list must not carry one graph per row, and a
graph may hold up to a mebibyte. `output` is the run's resolved result, written once by the succeeded `End` node at
terminalization; the per-node input and output documents remain a separate read. Without the pinned graph a run view
drew the definition it names, which is the wrong graph for every run started before an edit — the whole reason a run
pins a copy at all.

A pinned graph that will not deserialize **throws**, where a node-run document reads as null. The difference is what
each blob is: a node-run document is written by the runtime and a broken one is worth reading a page about, while a
graph was parsed before it was ever stored, so no supported route reaches a corrupt one. An empty canvas drawn beside
a real `nodeCount` would report the corruption as a graph nobody drew.

Validation errors are keyed. `GraphWorkflowValidationException` carries a `GraphWorkflowValidationResult` — a list of
`(key, message)` pairs where the key is the node or edge, or null for a failure about the document as a whole — and
the endpoints replay them one by one instead of collapsing them into a sentence. It is therefore **kept out of**
`DomainValidationExceptionHandler`, which maps single-message validation exceptions globally.

Warnings travel on the validate response as a second list of the same `(key, message)` shape. They are a separate
`Warnings` member on `GraphWorkflowValidationResult` — never mixed into `Errors`, which is what the exception path and
`valid` read — so nothing that refuses on the errors has to filter them out, and a client that ignores the member
behaves exactly as it did before it existed.

### The hub

`GraphWorkflowRunHub` at `/api/local/v1/graph-workflows/hub`. `SubscribeRun(runId, afterSeq)` joins the per-run group
`graph-workflow-run-{runId:N}` **before** reading the replay, so a change published between the read and the join
cannot reach nobody; the overlap that creates is harmless, because every push is idempotent and keyed by sequence.
The snapshot carries the run status, the queued / running / pending-decision counts, the watermark, up to
`EventReplayLimit` events, and a `replayTruncated` flag read from one row past the limit rather than inferred from a
full page. There is **no in-memory buffer** — the store is the replay authority — and a disconnect cancels nothing,
because a run outlives the tab.

The pushed event is `graphWorkflowChanged`, carrying `(runId, seq, kind)` and **no content at all**: the subscriber
re-reads the named feed from its own watermark, so a dropped push degrades to a late read rather than to a wrong
render. `kind` is **lowercase on the wire** — `run`, `node`, `gate` — written as literals in
`GraphWorkflowEventPublisher.ToWireKind` and asserted literally on both sides. There is no `event` kind: every kind
moves the append-only event feed, so the client invalidates it unconditionally.

The publisher is a **store decorator** (`PublishingGraphWorkflowStore`), so exactly one ping is emitted per committed
mutation that has a subscriber, carrying the sequence that commit allocated — and every published transition carries a
**fresh, increasing** sequence. Three writes deliberately publish nothing, because nothing is subscribed to them: the
definition writes (`CreateDefinitionAsync`, `UpdateDefinitionAsync`, `DeleteDefinitionAsync`), `StartRunAsync`, and
the startup reconciler `ReconcileNonTerminalNodeRunsAsync`. [API & Hubs](09-api-and-hubs.md) states the same. A publisher failure is logged and never fails the write that already committed. `Client.Application`
depends only on `IGraphWorkflowEventPublisher`; the host swaps the hub-backed implementation in over a registered
no-op, so a host without the hub stays resolvable.

The event vocabulary is the closed sixteen-token `GraphWorkflowEventTypes` catalog — `run.created`, `run.started`,
`run.waiting`, `run.completed`, `run.failed`, `run.cancelled`, `node.queued`, `node.started`, `node.completed`,
`node.failed`, `node.skipped`, `node.cancelled`, `node.interrupted`, `node.retried`, `gate.requested`,
`gate.decided`. The feed is append-only and durable, so a token written once is a token every later reader must
understand: extend it by amendment, never silently. Event details are small structured payloads — a failure summary,
a decision outcome — and never a transcript.

---

## 6. Frontend

`Client.React/src/features/graphWorkflows/` — see [React Client](10-react-client.md) for where it sits among the
feature folders. One route, `/graph-workflows`, with all four selections (`definitionId`, `runId`, `nodeKey`, `tab`)
as **search params** rather than path segments, so every view is linkable and a reload lands back on it. The route
component is a thin adapter; `GraphWorkflowsPage` itself is router-free and is rendered directly in unit tests.

| Directory | Holds |
|---|---|
| `models/` | `GraphWorkflowModels.ts` (the one file naming generated DTOs, plus the closed vocabularies and narrowers), `GraphWorkflowCanvasModels.ts` (the discriminated node union and the `graphToCanvas` / `canvasToGraph` round trip), `GraphWorkflowLayout.ts`, `GraphWorkflowValidation.ts` (the client mirror of the graph rules), `GraphWorkflowRunGraph.ts`. |
| `queries/` | Every read and mutation over the generated adapters, including the forward-paged events feed. |
| `hooks/` | `useGraphWorkflowEditor` (controlled React Flow state, per-handle connect prefill, refusal of a second unconditional edge, and the `context` edge added around a Pause on connect — §4.6) and `useGraphWorkflowRunHub`. |
| `components/` | Editor: the per-kind node cards, the canvas with its palette and Auto-arrange, the validation strip, the node and edge config panels, the definition list and meta dialog. Run view: the status badge, the read-only run graph, the node-run table, the run list, the events tab, the node panel and the decision panel. |
| `pages/` | `GraphWorkflowsPage` — editor mode without a `runId`, run mode with one. |
| `api/` | `GraphWorkflowConflict.ts`, which reads the three `NodeConflictProblemType` members by name. |

**The editor** mirrors the server's rules rather than inventing its own, and the mirror is a mirror on purpose: the
authoritative answer comes from `graph-workflows/definitions/validate`, which runs the parser a run would run, and
`serverErrorsToIssues` maps its keyed errors back onto the canvas elements. The page flow is Validate → Save (server
validate first; a 409 reloads) → Start. The server's **warnings** arrive through `serverWarningsToIssues` as
issues with the rule `serverWarned` and reach the strip through its own `warnings` prop; they render in their own alert
below the errors, in a different colour and under a title that asks rather than refuses, and never join the list the
save gate, the red chips and the config panels read, because a warning is not a reason to save less. Every client rule mirrors a rule that REFUSES a save, so the client raises none
of them.

Two round-trip rules the editor has to hold because the server does. Every JSON-shaped config field is written back as
JSON text with a **string quoted**, since that text is exactly what a save parses again — an unquoted string read as
the invalid-JSON complaint on a `defaultInput` the server had accepted. And the client's dot-path mirror refuses an
EMPTY segment the way `GraphWorkflowTokens.IsDotPath` does, so `a..b` cannot read green in the drawer and then 400
on save.

`useUnsavedChangesGuard` is set with `allowSameRoute`, so writing a search param — selecting a node or a tab — does
not trigger the leave prompt, while a real route change still does.

Nodes are laid out left-to-right by `layoutGraphWorkflow`, a dependency-free layered DAG layout (longest-path ranking
in Kahn order, one barycenter pass, ties broken by node key). It is used for three things: a node that arrives
without a `position`, the editor's Auto-arrange, and the run view's nodes-only fallback for a response that carries
no graph at all. **A definition whose nodes carry no positions opens laid out and dirty** — the layout is a proposal
the operator has not saved yet. That is by design, and §9 is why it matters.

**The run view** is read-only, and it draws the graph the run **pinned at start** — the one `GET runs/{runId}`
carries (§5) — with each node run's state overlaid on it. A definition edited since the run started is then worth
**saying** and nothing more: an informational notice that this is the graph the run itself ran on, never a reason to
draw less. Nodes only, auto-laid-out, is what remains for a response that carries **no** graph and whose hash
disagrees with the definition on screen, because drawing today's edges over an older run would be a lie about routing.
The Pause decision panel reads its prompt, its allowed answers and `requireComment` off the pinned graph for the same
reason: a definition edited since could offer a decision this run's gate would refuse. The view also lists the node
runs, shows a selected node's input, output and error documents, and renders the event trail. The events feed pages
**forward** from `afterSeq: 0`, so `replayTruncated` means the **newest** events are missing, and the banner says
exactly that.

The graph → canvas conversion is **memoised on the graph object**. `graphToCanvas` parses the whole document and lays
out every node that carries no stored position, while the run hub invalidates the run detail on every event — so one
node transition would otherwise re-parse and re-rank up to a mebibyte of graph to redraw a single badge. The cache is
a `WeakMap` keyed on the graph object itself: the conversion is a pure function of that document, TanStack's
structural sharing hands back the same object while its JSON is unchanged, and an entry dies with the graph that
keyed it. Callers treat the cached canvas as frozen and copy every node and edge they annotate.

`useGraphWorkflowRunHub` degrades rather than fails. On a subscribe it re-sends its watermark as `afterSeq`; the
store serves a client that has been away for days, so there is no buffer to roll over. While the hub is unavailable
it hands the page a poll interval of 3 s, cleared only on a **successful** subscribe. Node-run invalidation is keyed
on the **run**, not on the selected node, so clicking through the node table does not tear down the subscription.

The feature ships **on** and is a top-level navigation entry. Its capability flag, `nodeCapabilities.graphWorkflows`,
is declared in `src/capabilities/NodeCapabilities.ts` with the rest; the route redirects home when it is off.

---

## 7. Testing

See [Writing Tests](17-writing-tests.md) for which project a new test belongs in and
[Testing & Validation](13-testing-and-validation.md) for how to run them.

| Layer | Where |
|---|---|
| Parser, conditions, documents, state machine, deadline, dispatcher, executors, run service, options | `XE-Local-AI-Engine.Tests/GraphWorkflows/` |
| Endpoints | `XE-Local-AI-Engine.Tests/Endpoints/GraphWorkflows/V1/` |
| Hub and event publisher | `XE-Local-AI-Engine.Tests/Hubs/GraphWorkflow*Tests.cs` |
| Store, encryption, purge coverage | `XE-Local-AI-Engine.Client.Persistence.Tests/GraphWorkflows/` |
| React components, hooks, models, queries | colocated `*.test.ts(x)` under `features/graphWorkflows/` |
| Browser end-to-end | `XE-Local-AI-Engine.Tests.E2ETests/Tests/GraphWorkflowE2ETests.cs` |

Two suites are worth naming because they are the ones that catch a wiring break the unit tests cannot:

- **`GraphWorkflowEndToEndTests`** and **`GraphWorkflowPauseToolEndToEndTests`** drive the wired host over REST and a
  real SignalR `HubConnection`, with the model runtime replaced by `Testing.FakeOllama`. The pause/tool suite asserts
  the `gate` push twice, which is what proves the publisher decorator and the lowercase wire kind agree with the
  client.
- The **E2E** class drives the real browser through the whole loop — create a definition, drop nodes from the
  palette, wire and configure them, validate, save, start a run, answer the pause, read the event tab.

`GraphWorkflowHarness` and the `*HostFixture` types are the shared seams; `GraphWorkflowGraphs` holds the reusable
graphs. On the client, `test/GraphWorkflowFixtures.ts` is the one fixture file, and `eightNodeGraph` is the graph of
§2.5.

---

## 8. Limits and options

`GraphWorkflowOptions`, bound from the `GraphWorkflows` configuration section. No `appsettings*.json` carries the
section — the defaults below are the shipped values, and an operator overrides one through configuration or an
environment variable such as `GraphWorkflows__MaxConcurrentRuns`.

| Option | Default | What it bounds |
|---|---|---|
| `Enabled` | `true` | The whole surface. False makes every route and the hub answer 404 through the request-path middleware, while registration stays intact so a disabled node answers legibly instead of 500-ing out of an empty container. |
| `MaxNodesPerDefinition` | 200 | Nodes in one definition, enforced when it is **validated** rather than when it runs, and checked against the declared node count **before** the graph is parsed (§2.4). A run that re-parses an already-stored graph is deliberately uncapped. |
| `MaxNodeRunsPerRun` | 200 | Node runs one run may instantiate. Never below `MaxNodesPerDefinition` — a run that could not instantiate the definition it started from would fail halfway through a graph the operator was allowed to save. |
| `MaxTotalAttempts` | 50 | Every attempt one run may spend across all its nodes. The guard against a retry storm. |
| `DefaultNodeTimeoutSeconds` | 600 | One node run's attempt, when its node names no `timeoutSeconds`. Unlike Dev Workflows, a node that declares nothing still has a deadline. |
| `MaxOutputJsonBytes` | 262 144 | One node run's composed output document, in UTF-8 bytes, checked before it is encrypted and stored. |
| `DispatchIntervalMilliseconds` | 500 | The sweep cadence, independent of the change signals the dispatcher also listens for. Floored at 100 ms. |
| `MaxConcurrentRuns` | 4 | Live runs at once, and the size of both the Agent and Tool in-flight lanes. Runs above the cap **wait**; they are not refused. |
| `MaxRunInputBytes` | 65 536 | A run-start input document, checked in `GraphWorkflowRunService.StartAsync` (§3.1) rather than at the endpoint, so every caller of the service is held to it. Also the budget for the inlined `upstream` map in an Agent prompt. |
| `EventReplayLimit` | 200 | Events one replay may return, hub snapshot and events route alike. Ceiling 1000 — one replay is one response body. |

`GraphWorkflowOptionsValidator` checks at startup what the data annotations cannot: a semantic floor under each
budget (a `MaxNodesPerDefinition` of 1 passes `[Range(1, …)]` and still admits no graph, since every graph carries a
Start and an End), the `MaxNodeRunsPerRun ≥ MaxNodesPerDefinition` relation, and the replay ceiling. An operator meets
these at boot rather than once per node run.

Two limits that are **not** options, because they are not runtime budgets: the 1 MiB request-body cap on the four
routes that carry one (create, update, validate and start-run), and the 200-row cap on a run list page.

One ceiling that lives outside this module: an `Agent` node's `responseJsonSchema` goes down the same llama.cpp GBNF
path as a tool schema, which has an empirical combined repetition bound
(`LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound`). Keep a response schema flat. A value bound written
into that schema is not a limit either — see §4.2. Note that the sanitiser named there covers only **tool** schemas; a
response schema has no equivalent pass. See
[Local Runtime & Providers](03-local-runtime-and-providers.md) and `docs/agent-knowledge.md` §3.

---

## 9. The Open Canvas import

Open Canvas (the Preview workflow builder) was removed. Its saved canvases are **converted into Graph Workflow
definitions once, automatically, on the first start of the build that removed it.** There is no button, no prompt and
no opt-out, and the conversion **cannot be undone**.

**Back up the node's data directory before upgrading a node that has canvases you care about.** That is not a
formality: see the failure posture below.

### 9.1 Why it is split around the migration

An EF migration cannot decrypt `graph_json` — it has no node key, and the column is AEAD ciphertext — so the
conversion has to run in application code. But the `DropCanvasWorkflows` migration removes `canvas_workflows` during
`ApplyNodeChatMigrationsAsync`, which runs before any hosted service starts. A single post-migration importer would
therefore always find the table gone.

So the step is split, in `Program.cs`:

```
var pending = await ReadPendingCanvasWorkflowsAsync(app.Services);   // read + decrypt, BEFORE migrations
await ApplyNodeChatMigrationsAsync(app.Services);                    // backs up, then runs DropCanvasWorkflows
await ImportCanvasWorkflowsAsync(app.Services, pending);             // write, IMMEDIATELY after that pass
await ApplyNodeIdentityMigrationsAsync(app.Services);                // a different database; runs after the write
```

The reader guards on `sqlite_master`, so it is a no-op when the table is absent — which is a fresh install, and every
start after the first. **That absence is the idempotency mechanism.** There is no marker table, no flag column and no
provenance field to go stale.

The import runs regardless of `GraphWorkflows:Enabled`, deliberately: an operator who never turns the feature on must
not silently lose their canvases. Every row is read — there is **no cap** — because a cap plus an unconditional drop
in the same build would destroy everything past it as its *normal* outcome.

### 9.2 The mapping

| Open Canvas node | Graph Workflow node |
|---|---|
| `Start` | `Start`, with the canvas `StartText` as `config.defaultInput = { "text": <StartText> }` (null when empty) — an object, because the editor renders a stored default as JSON text and a bare string would never re-parse. |
| `Agent` | `Agent` with `maxAttempts: 1`, `instructions` / `model` / `reasoningEffort` carried over, `agentDefinitionId: null`, `responseJsonSchema: null`, `includeUpstreamOutputs: true`. |
| `Debug` | **Elided.** Every `X → Debug` and `Debug → Y` collapses to `X → Y`. A Debug node was a side-event tap that forwarded its input unchanged, so removing it preserves the run's meaning exactly. |
| `Pause` | `Pause` with `prompt: "Approve and continue?"`, `allowedDecisions: ["Approve"]`, `requireComment: false`; its single out-edge gains `label: "approved"` and `condition: { "path": "output.decision", "op": "Eq", "value": "Approve" }`. Open Canvas's `Continue` was a resume rather than a decision, so `Approve` alone is the faithful translation — and one allowed decision with one matching out-edge satisfies the Pause pre-flight rule of §2.4. **Plus one context edge `X → Y`**, unconditional and labelled `context`, where `X` is the pause's unique nearest non-`Pause` ancestor along the (Debug-elided) chain and `Y` each **starved** successor of it — see below. |
| `End` | `End` with `outcome: "completed"`, `resultPath: null`. |
| `ModelProfile` (on an Agent) | **Dropped.** An Agent node's config has no profile member. A non-null value records a reason naming the canvas id and the **node key**, never the value. That reason reaches the log as a per-change Warning only when the definition imported cleanly; on the needs-attention path it is folded into the description instead (§9.3). |

Node keys are the Open Canvas ids sanitized to `[A-Za-z0-9_-]{1,64}`; an unmappable or duplicate key becomes
`n{index}`, and a `sourceId → key` map rewires the edges. Each emitted edge gets a minted key `e{index}`. Node and
edge keys share one namespace, so `CanvasWorkflowImport.MintKey` appends `_2`, `_3` and on when the fallback is
itself taken — the key is unique before it is pretty.
The definition takes the canvas name, truncated to **255** characters (`CanvasWorkflowImport.MaxNameLength`, which
is the `name` column's own bound — `GraphWorkflowDefinitionConfiguration` declares `HasMaxLength(255)`), and the
description `"Imported from Open Canvas (canvas workflow {id})."` — provenance without a schema change. The create and
update validators cap a name at **200** (`GraphWorkflowRequestLimits.MaxNameLength`), so an imported name of 201 to
255 characters lands in the database but has to be shortened before the definition can be saved again.

**The importer emits no positions.** `position` is optional and the editor already lays out a node that arrives
without one, so generating a second layout rule here would leave one of the two dead. The practical consequence:
**an imported definition opens laid out and unsaved.** The canvas is dirty the moment you open it, and the layout is
persisted when you save. That is expected, not a bug.

**The context edge around a `Pause`.** Open Canvas's `Pause` was a pass-through resume: its post-adapter forwarded
the answer it was waiting on unchanged. A Graph Workflow `Pause` writes a decision document of its own —
`{decision, comment, payload}` — and a node's `input` is its ONE satisfied predecessor's output document, becoming
the `upstream` map only when there are several (§3.2). Mapped one-for-one, `Agent A → Pause → Agent B` therefore
hands B the approval metadata and never A's answer, and a `Pause` before an `End` loses the result the same way.
That was observed live in the S4 round, not reasoned about.

So the importer emits one extra unconditional edge from the pause's nearest non-`Pause` ancestor `X` to each
**starved** successor `Y`. `Y` keeps the default `All` join policy, so it is admitted only once **both** the content
edge and the pause's own `approved` edge are satisfied — never ahead of the approval — and with two satisfied
predecessors its `input` is the `upstream` map `{ "<X>": …, "<P>": … }`. An imported Agent carries
`includeUpstreamOutputs: true` and so sees `X`'s text; an imported End carries `resultPath: null` and so keeps both
documents. The rule applies to every pause and walks back through consecutive ones, so `A → P1 → P2 → B` gains both
`A → P2` and `A → B`; a successor several pauses reach is judged **once**, over the union of what all of them are fed
by. `X` may be the `Start` node, whose output is the run's own input, which is exactly the content the pause
interrupted.

The decision is keyed on the **successor**, and the guards are the validator's own
(`GraphWorkflowGraph.PauseContextWarnings`), read off the mapped node's `joinPolicy` and kind rather than off a canvas
kind — an importer that advised an edge the validator refuses would produce a definition nobody can save again. A
successor is owed an edge only when it is starved (every one of its inbound edges leaves a Pause; one something else
already feeds loses nothing), its `joinPolicy` is not `Any` (of any kind — an unconditional content edge would admit
an `Any` node ahead of every approval, including when all of them were rejected), and its pauses' nearest non-`Pause`
ancestor is **unique** and is not a `Condition` (two candidates are mutually exclusive branches, and an edge from each
would hang an `All` successor on the branch never taken; a `Condition` would gain a second unconditional out-edge,
which §2.4 refuses). Three more cases add nothing: a pause with no successor (already an `IMPORT NEEDS ATTENTION:`
graph, §9.3), a pair the canvas already wires — a second unconditional edge over one pair is a validation error
(§2.4) — and a self-loop.

Every guard but the starvation test is **unreachable for a real import**: an Open Canvas graph carries no `Condition`,
no `Join` and no join policy, and every stored shape is linear. They are stated in code anyway because the rule, not
today's canvas vocabulary, is what the next node kind has to keep holding.

The editor offers the same edge to an **author**, on the connect gesture (§4.6), under the same three guards. The one
difference is when it runs: the editor runs only on that gesture, so an author who deletes the edge keeps it deleted,
while the importer runs once over a whole canvas.

`maxAttempts: 1` on an imported Agent is deliberately below the default of 3. An import is conservative: re-running
somebody's agent turn twice more, on a graph they have not looked at since it changed shape, is not a decision this
conversion gets to make for them.

### 9.3 `IMPORT NEEDS ATTENTION:`

The mapper is **total** — it always returns a document and never refuses a graph. Whatever it could not translate
faithfully becomes a reason.

A converted graph then goes through `IGraphWorkflowDefinitionService`, which owns the parse, the node cap and the
hash and count. If that validation refuses it, the definition is **saved anyway**, through `IGraphWorkflowStore` and
past the validator, with its description prefixed:

```
IMPORT NEEDS ATTENTION: <reason>
```

Such a definition **cannot be run until an operator opens it in the editor and fixes what the validation strip
names.** Nothing is discarded for being invalid. A Debug node with no successor, a shape the new validator refuses,
an unknown kind — all of them arrive as a definition you can see and edit, rather than as an absence you have to
notice.

Two causes skip a row in the **read** half, both logged at **Error** and counted as failed: the blob does not
decrypt, or it does not parse as JSON at all. A blob that does not parse could never have been saved through the old
endpoint. That log line carries the canvas id and the exception type only — not the name, which the read never
decrypted a reason for, and not a reason.

`{Failed}` in the summary is not the read half alone. The **write** half counts a row there too, also at **Error**,
whenever the canvas could not be stored at all: any non-validation exception out of `CreateAsync`, the unvalidated
store write itself throwing ("could not be stored and is lost"), or the mapping throwing before either is reached.
A validation refusal is *not* one of these — that is the needs-attention path below, and the row survives.

**Graph content is never logged.** Instructions and start text are exactly the payload the column is encrypted to
protect. The reader logs entry and per-row failures; the writer logs one line per clean import. The summary an
operator must not miss is logged at **Warning**, because this is an irreversible one-shot they had no chance to
decline:

```
Open Canvas one-shot import complete: {Imported} imported, {NeedsAttention} need attention, {Failed} failed.
Open Canvas has been removed; canvas_workflows is dropped.
```

One further **Warning** line follows per needs-attention canvas, naming the id, the name and the reason. A **failed**
canvas gets an **Error** line instead, and it names no reason: the read-half line carries the id and the exception
type, the write-half lines the id, the name and the exception type. A cleanly imported canvas whose mapping still
changed something gets one Warning per change.

### 9.4 The one real risk, stated plainly

`DropCanvasWorkflows` runs during the migration pass **regardless of whether the write half later succeeds**. If the
process dies between the migration and the write, or the write throws, the canvases are gone from the live database.

The only recovery is the **best-effort** backup `INodeDbBackupService.BackupBeforeMigrationAsync()` takes immediately
before migrations run. Best-effort means what it says: a failure to take it is logged and swallowed. It is a
mitigation, not a guarantee.

No plaintext export is written to hedge this, and that is a deliberate trade rather than an oversight: the graph blob
carries the operator's agent instructions, which is exactly what the column is encrypted to protect, and dropping a
decrypted copy on disk would swap a small durability risk for a standing privacy regression.

**So: copy the SQLite data directory before you upgrade.** That is the belt-and-braces answer, and it is the
operator's to take.

---

## Related pages

- [Architecture Overview](01-architecture-overview.md) — where the module sits in the host.
- [API & Hubs](09-api-and-hubs.md) — the route family, the hub, and the event contract.
- [Data & Persistence](08-data-and-persistence.md) — the four tables, their encrypted columns and AAD binding.
- [React Client](10-react-client.md) — the `graphWorkflows` feature among the others.
- [Agent Mode](04-agent-mode.md) — the invocation stack an `Agent` node runs on, and the tool catalog a `Tool` node
  is filtered against.
- [Security & Privacy](12-security-and-privacy.md) — the approval boundary the Tool gate tightens on.
- [Testing & Validation](13-testing-and-validation.md) and [Writing Tests](17-writing-tests.md).
- [ADR 0001](../adr/0001-development-mode-restart-recovery.md) — the never-resume-a-stream restart posture §3.5
  applies.
- [ADR 0006](../adr/0006-agentic-trust-mcp-key-scopes-and-auto-approval.md) — the tool trust boundary §4.3 rests on.
