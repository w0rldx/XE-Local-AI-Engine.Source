# Dev Workflows — The Development Runtime

> Reviewed: 2026-09-20 · Code-grounded.

**Dev Workflows** run an operator's work item through a graph of agent turns, sandbox command passes, coder
rounds and human gates. The engine executes a run from the database: every node run is a row, every change is
an append-only event, and the process that advances it holds no authoritative state of its own. A restart costs
at most the work that was in flight.

The module lives in `Client.Application/Services/DevWorkflows/` (the parser, the state machine, the dispatcher
and the three lanes), `Client.Persistence` (work items, definitions, rule sets, runs, node runs, events and
artifacts, with per-column AEAD encryption), the `LocalApiRoutes.DevelopmentWorkflows` endpoint family and
`DevWorkflowRunHub`. It is gated by `DevWorkflows:Enabled`, which ships **off**.

This page is the runtime reference. Two neighbours own what it deliberately does not repeat:

- **[Workflow Engines Divergence Register](22-workflow-engines-divergence-register.md)** owns every place where
  Dev Workflows and [Graph Workflows](21-graph-workflows.md) differ — vocabulary, approval model, reconciler
  behaviour, persistence shape, feature flags. Read it before concluding that one engine is missing something.
- **[API & Hubs](09-api-and-hubs.md)** owns the route family, the hub contract, and the four structural graph
  rules as the validator enforces them at save. This page covers their **runtime** halves — the moments a run
  re-asks the same question of the rows it actually landed on.

## 1. What it is, and what it is not

A **work item** is the unit an operator creates: a piece of development work, optionally bound to a Development
Mode project. A **run** executes one **definition** (a graph) against one work item. Work-item status is written
by the runtime inside the transaction that transitions a run, never by a client — a start makes the item
`Active`, a run waiting on a human makes it `Blocked`, and a *failed* run also maps to `Blocked`, because a
failed run needs attention rather than being done.

It is **not** a fork of the machinery it drives. The agent lane is a client of the work-session stepwise
executor; the implementation lane is a client of Dev Mode's coder/validate/review chain and its hash-locked
apply gate. Nothing here re-implements either, which is why the runtime holds no execution state: it asks for
the next action, reads what the subject became, and writes that onto the node run.

**No AI-authored change reaches a repository from here.** An implementation node's success *is* the task
reaching `AwaitingApply`; applying it is a later act behind a gate whose decision is recorded in the run's own
audit trail.

## 2. The graph

### 2.1 Node types

`DevWorkflowNodeType` has seven members (`Client.Persistence/Entities/DevWorkflowEnums.cs`). Start and End are
implicit — an entry node has no inbound edge, a terminal node no outbound edge — so neither is a member; a
client may draw synthetic anchors.

| Type | What it does |
| --- | --- |
| `Agent` | One work session per node-run attempt, driven by the run that owns it (§5). |
| `Tool` | A sandbox command pass — a build, a test run, a validation profile (§7). |
| `DevTask` | Drives the Development task the work item's project owns, through the coder → validate → review chain (§6). |
| `HumanGate` | A durable wait for a person. `Approve` / `Reject` / `RequestChanges` route the out-edges. |
| `Gate` | An automatic gate that records which way the run went, and on what. |
| `Parallel` / `Join` | Fan-out and fan-in. They exist as node types so the *timing* of a fan-out is visible in the event log. |

A `Gate` adds no routing power a conditional edge does not already have: its out-edges are evaluated by the
same generic edge rule as everything else, against the document it passes through, so the gate cannot decide
one thing and the edges another. What it buys is `branch` — one recorded answer to "which way did the run go,
and on what", otherwise reconstructible only by re-evaluating conditions against a document that may since have
been superseded.

### 2.2 Edges, joins and the pinned graph

Edges may carry conditions evaluated against the producing node's output document. A node's `joinPolicy`
decides what an inbound fan-in waits for: `All` waits while any inbound edge is `Pending`, and so does `Any`.

**The run's pinned graph is the single source of routing truth.** A run stores its own copy of the definition's
graph at start. A definition edited or deleted mid-run therefore cannot change what a running node dispatches
on, cannot widen what a running session may be offered, and cannot move a node's declarations under the rows
that already read them. Growing a run means rewriting *that blob*, never the definition — which is what keeps
re-running the same definition unaffected.

The graph is validated **again** at run start rather than trusted from the definition's save, because an agent
definition can be deleted in between. A run left `Pending` on a graph nothing can route would be swept forever,
so the refusal is written down rather than retried.

An inbound edge is in one of four states, and admission is a question about the **target's** inbound edges rather
than about any one source. `Pending` — the source has not settled, or has settled and this edge is still to be
judged against a live sibling; a source with no row at all is `Pending` too, because a source that has not been
materialized is a wait, not a refusal. `Satisfied` — the source succeeded and this edge's condition fired.
`Dead` — the source settled in a way this edge can never fire on, so nothing downstream of it will ever come. A
`Failed` or `Cancelled` source kills every out-edge: neither produced the output a condition would read, and
treating "no output" as a passing condition is how a run routes on evidence it never had. So does a `Succeeded`
source whose condition did not fire — the branch a gate did not take.

`Waived` is the fourth, and it exists because a **skip has two origins the row itself cannot tell apart**. One is
a person: an operator looks at a node that could never succeed — one slice of a decomposition whose file did not
exist — and decides the run should carry on without it. The other slices did their work, and skipping the join
over the one that was excused throws all of it away: a single skip on one clone's implementation would cancel a
run whose every sibling had succeeded. The other origin is a **cascade** off something dead — a failed ancestor,
or a gate branch nothing routed down — where the skip is the run being told where it may not go, and carrying on
past it would be routing on work that was never done.

The difference is read from the **graph** rather than from a column, which is why no column was added: a skip is
waived when every path back from it is itself satisfied or waived, because that is exactly the case where nothing
upstream refused it and only a person could have. One dead path behind it and it stays dead, so a not-taken
branch and a failed branch's tail keep the semantics they have always had. A waived edge is excused rather than
satisfied — it does not admit an `Any` successor, and it does not kill an `All` one — which is why a recorded
route gives it a bucket of its own instead of folding it into either half. The three buckets are the three states
an edge can be in for a terminal source, one for one.

A join's own **policy decides what a waiver means for it**. Under `All` one dead edge still skips the join and
one waived edge no longer does: a person who skips one leaf of a fan-out is saying the run should go on without
it, so the join goes on as long as something actually arrived. With *every* edge waived nothing arrived, so it
skips — and because that node's own inbound edges were all waived, its skip is waived in turn, so the cascade
runs exactly as far as the excusing does and stops at the first node a satisfied edge reaches. `All` over **zero**
inbound edges is vacuously satisfied, which is how an entry node becomes eligible at all, Start being implicit.
`Any` is untouched: waived is not satisfied, and a merge that exists to carry one live branch cannot be carried
by a branch nobody ran.

Whether a *skipped* node's own skip was a person's choice is likewise asked under **that node's** join policy,
because the policy is what decides which of its dependencies ever had to arrive. Under `All` every one did, so
the skip is a person's only when they all read satisfied or waived. Under `Any` one arriving was the whole
contract: a node admitted on one satisfied edge ran, and if it then blocked and an operator skipped it, the dead
sibling it was never waiting on did not cause that — judging it with an `All` predicate regardless is what makes
such a skip read as a cascade and take a downstream `All` join's surviving work down with it. A node with no
inbound edges is vacuously waived under either policy: an entry node has nothing upstream that could have refused
it. An `Any` node whose edges are dead and waived with none satisfied stays dead, never having had an admission
of its own to lose. A `Pending` dependency waives nothing under either policy — a source still to settle may yet
die, and answering now is answering ahead of it.

**Pending outranks Dead** under both policies, so the answer cannot depend on which branch happened to land
first. A dead inbound edge already settles what an `All` join will do, but settling it while a sibling branch is
still running skips the node, and everything after it, in front of work the run has not finished: one clone's
validate skipped, and the integration stage goes terminal with the other clone's implementation still to come.

On `feature-development-v1` the join also has the `decompose → join` edge, satisfied from the moment the
decomposition succeeded. An operator who skips **every** clone therefore still carries the join, and the
verification node runs over a task package and no implementations. That is accepted rather than overlooked:
verify is an agent asked to judge what was produced, and judging that nothing was is a better answer than a run
going terminal without saying so — and the skipped clones reach its objective by name, so it judges with facts.

**Invariant ids in refusal messages.** Four structural rules are enforced when the graph is parsed, and each names
its id in the exception it throws, so an operator who greps an id out of a refusal finds the rule.

| Id | The rule it denotes | Explained under |
| --- | --- | --- |
| `GRAPH-C4-1` | Every path reaches an end of the run. | Edges, joins and the pinned graph |
| `GRAPH-C4-2` | A node that writes outside its sandbox is reached through a human gate. | Edges, joins and the pinned graph |
| `GRAPH-C4-3` | An apply follows a validation. | Edges, joins and the pinned graph; The provenance walk in front of an apply |
| `GRAPH-C4-4` | A fix loop's per-node cap is declared only where there is a loop to bound. | The cross-node fix loop |

Read literally, **`GRAPH-C4-1`** is implied by acyclicity, a node with no out-edge being an end. The non-vacuous version is
over the edges a run can *take*: an out-edge of a human gate that is false for all three answers can never fire, so
the branch behind it is written but unreachable — `{"path":"decision","op":"eq","value":"Approved"}`, the past
participle, validates today and silently kills the branch at run time. It is checked in three steps, and the order
decides which complaint the operator gets: the dead edges are found first, then the live subgraph is asked whether
anything is stranded — the complaint that names the real damage — then a dead edge that stranded nothing is
reported on its own. A gate that owns a dead edge is named before anything else stranded behind it, since chaining
two gates strands every gate above the broken one and an ordinal tie-break would send the operator to the wrong
line. The rule is decidable for a **human** gate only: a `Gate` node's output document is whatever the node
produced, so no definition-time reading of its conditions says which will fire. It is deliberately *not* the rule
that every gate answer has somewhere to go — both seeded gates carry an `Approve` edge and nothing else, and a
rejection ending the run is the design.

**`GRAPH-C4-2`** is the structural half of the write rule. The apply rule below is strictly stronger for an apply node, so
this never weakens it; approval policy is tighten-only. What it adds is the **declared** case: an Agent node whose
author wrote `WriteExecute` into `requiredCapabilities` is taken at their word, and a run must not reach it without
an operator having been asked. The waiver is the graph's own `allowUngatedWrites` — a template saying so once and
in writing, rather than each node quietly opting itself out.

**`GRAPH-C4-3`**'s structural half is that every path into a `toolMode: Apply` node passes a `Validate` Tool node.
It is deliberately optimistic in one place: admission drops inbound edges whose source is a template key, so the
`validate(template) → join` edge that carries the property in the definition graph is not a run-time dependency.
The gap is closed operationally rather than structurally — the materialized graph carries the clones' real validate
edges, and the dispatcher re-asks the question over the rows a run actually landed (§3.6).

Both are answered by one question — has every run that reaches this node already passed a node with this property?
— as a single forward fixpoint in topological order, because two implementations of it would drift. One recurrence
and no special case: a node is assured when it has the property itself **or** when its inbound edges combine to it,
with the empty combination false, so an entry node evaluates to its own property rather than being initialised
false. Initialising it false would reject a definition whose entry *is* the gate guarding the write, or whose entry
is the validation ahead of the gate and the apply. The inclusive reading is safe because nothing can be at once the
property and the thing checked: a gate carries no effects, and a Tool node is `Validate` or `Apply`, never both.
The combination is keyed on the node's **`joinPolicy`** and never on its node type — OR under `All`, because every
inbound branch must complete and one carrying the property is enough, and AND under `Any`, because only one branch
may have run. Keying on the `Join` node type instead rejects the shipped template, whose verification node is an
`Agent` with two inbound edges.

The fixpoint walks the authored edges **plus one virtual edge** from each materializing node to its template root.
That is not an invention: after expansion the materializer wires exactly that edge for every dependency-free task,
chains dependent clones off their dependency's leaves, and wires each clone's leaf to the join — so the virtual
edge is the definition-time image of a real run-time one, which is why these invariants answer the same before and
after materialization, and why they can be checked at save while the author is still there to fix what they say.
A node may name itself as its own template root, so a self-edge is skipped; with it skipped the augmented graph is
acyclic, and that rests on two rules rather than one. A template subtree's only exit is its join, so the only way a
template root could reach its materializing node is through that join — and a join that *is* the materializing node
or one of its ancestors is refused. Without that second rule the virtual edge closes a loop the authored-edge
acyclicity check is silent about, and the topological order would not be one.

**An apply node is reached from a human gate and from nothing else** — `Y3`, the name the React development pages
use for the same rule when they say where a workflow-driven task's approval lives — and it is what the whole
integration stage rests on: no AI-authored patch reaches a real repository without an operator decision recorded in the run's own
audit trail, and a definition is the only place that can be checked before the fact — by the time an ungated apply
runs, the approval it should have waited for does not exist to be missed. It is stated as "every inbound edge comes
from a human gate" rather than "some gate lies on every path", which is weaker in the way that matters: an approval
given before the patches existed, a plan gate say, would satisfy the path reading while approving something else
entirely, and the immediate reading leaves no window between the answer and the act for a node run to change what
is being applied. The source being a gate is only half of it: all three answers leave a gate `Succeeded`, so an
unconditional gate-to-apply edge fires on a **rejection** and applies the patches the operator declined. The
condition is checked too, by asking the dispatcher's own routing what it would do with each answer. An apply node
inside a materialization template is refused outright — every child would apply the whole fan-out again, and
integration runs once, after the join.

A node's **retry target** must be one of its ancestors, or routing a failure to it would livelock the run, and it
may not be a materialization template key unless the node naming it is inside that subtree: a template is cloned
once per task and never runs under its own key, so a route to it would block on a configuration failure instead of
re-attempting anything — a fix loop that reads correctly and cannot fire. The clone-internal case stays legal
because both keys are rewritten together.

### 2.3 Seeded templates

The seeder ships definition templates idempotently on their seed slug, so nobody starts from a blank canvas.
Each template is parsed through the same validator a run start uses **before** it is written. An archived
template is never resurrected: a changed template ships under a new slug. A row whose slug matches is compared
against the shipped template and the prior revisions kept beside it, and only an untouched row is upgraded —
version is deliberately not the signal, since the seeder's own upgrade would move it and make every later
change look like an operator edit. Renaming a seeded definition is a name-only change that leaves the graph
alone, and the comparison is of the **graph** only.

Agent nodes in a template bind by **seed slug** rather than by id, because the personas they name are themselves
seeded and their ids differ per node.

Two templates ship. `research-plan-approval` is strictly linear, needs no repository, and ends on the approval that
is the point of it. `feature-development-v1` is research and a plan a human approves, a decomposition that expands
into one implementation-and-validation subtree per task, and an integration stage that applies nothing until a
second human gate says so. Both its gates route on the approve decision and have **no other branch**, which is what
makes a refusal end the run rather than continue past it — for the integration gate that is the rule that an apply
node is reached from a human gate and from nothing else (`GRAPH-C4-2`'s stronger sibling, §2.2). Its implementation node declares a timeout of its
own because nothing else bounds it: Development Mode bounds each attempt and each review round, but not attempts
back to back.

**Two of `feature-development-v1`'s edges are load-bearing for evidence rather than for routing.** Because a
producing *type* stops the upstream-artifact walk on its own path (§3.7), the join's inbound edge from the
unmaterialized `validate` template node contributes nothing, and `decompose → join` is what puts the task package
on a path back from the join at all (§9.3). That edge alone does not carry the approved plan — the walk stops at
`decompose`, so `plan.md` is one producer further back — which is what `planapproval → verify` is for, conditioned
on the approval like the decomposition's, so a declined plan kills both paths into the verification rather than
leaving it half-fed. It costs no routing: the gate has long since settled by the time the join lets anything
through.

**The second fix loop is what makes staleness reachable at all.** `validate`'s own retry target fires before
anything downstream of it exists, so nothing has yet recorded consuming the work it replaces and the stale mark
finds no dependents. `fullvalidate`'s target routes a failure of the *integrated* result back to `verify`, by which
point the integration gate has been handed the verification report as its evidence and `integrate` has consumed it
too — so the re-run supersedes it, and the apply report and the full check's own report are flagged as written from
a version that no longer exists.

**It targets `verify` and not `implement`, and that is not a preference.** `implement` is the materialization
*template* node, which the parser refuses as a retry target from outside its subtree (§2.2). Targeting `verify` is
also the safe direction: the implementations are not reset, their tasks stay completed, and the apply node's re-run
short-circuits per task rather than applying a patch twice.

**A v1 ceiling.** What a re-run does *not* flag is `validate`'s own report. Only an agent node's promotion and a
tool node's report write a workflow artifact, and `implement` is a `DevTask` — the patch it produces is a
Development Mode artifact in another store, which the artifact-use table cannot name. So `validate` records
consuming nothing and there is nothing for a re-run to supersede. Closing that means promoting the approved patch
into the run's own artifacts, which is a producer this lane does not have yet.

### 2.4 Vocabularies

Four closed vocabularies live in `DevWorkflowVocabularies`, and they are deliberately separate from each other:

- **Failure classes** (`failure_class`, PascalCase) — why a node run failed: `ProviderError`, `Timeout`,
  `Interrupted`, `ToolCommandFailed`, `Internal`, `Configuration`, `Policy`, `BudgetExhausted`, `Cancelled`,
  `GateRejected`, `ObjectiveNotMet`.
- **Event outcomes** (lowercase verbs, ≤ 64 chars) — how one event ended. There is deliberately **no** token
  for a refused admission: a lane that will not take a node run yet is queueing, not failing, and it produces a
  `Queued` row with a reason rather than an event.
- **Queue reasons** (lowercase-hyphenated, displayed verbatim) — `awaiting-agent-slot`,
  `awaiting-sandbox-slot`, `awaiting-dependency`. The whole point of `Queued` and `Running` being separate
  states is that the UI can say *which* of these it is.
- **Node output status** — what the node produced, which a definition author's `status eq "succeeded"`
  condition reads. It is not the event-outcome vocabulary even where two strings coincide: sharing the
  constants would make a later change to either silently reach into the other.

Collapsing failure classes into outcomes is what once produced tokens that belonged to neither.

Three failure classes are **not retryable**, and each on evidence rather than by policy: `Configuration` (a
missing agent, a model that cannot call tools, a repo-bound node on a work item with no project — a retry
produces the same answer), a manifest check that hard-fails with zero commands run and no egress to re-resolve,
and `ObjectiveNotMet` (a session that gave up honestly gives up again on an identical second run; what changes
the answer is the operator's retry reason). All three go straight to a human.

## 3. The dispatcher

`DevWorkflowDispatcher` is the runtime's one loop, and its central invariant is:

> **Every node-run status write happens inside a serialized `AdvanceOnceAsync` call.**

Lane work, where it exists, only produces a pollable result and never transitions a row itself. The only other
writer to a run is the human-decision path, which is what the store's `Any` version sentinel exists for.

Advancement is a pure database decision and takes microseconds, so one loop for every run is enough and gives
one place where graph invariants are decided. Nothing in it assumes it is alone; the seam, if the run count
ever justifies one, is to partition by run id. The parsed-graph cache is a cost optimisation and nothing else —
one entry per run, replaced when the revision moves, dropped when the run terminalizes, bounded by the
concurrent-run cap, with no eviction policy because it needs none.

The production loop is a thin wrapper around `AdvanceOnceAsync`, which is a design requirement rather than an
afterthought: no test ever has to wait on a timer or race a background task.

### 3.1 The order of a tick

`DevWorkflowDispatcher.AdvanceCoreAsync` reads the run first and answers immediately for three statuses: a
terminal run is forgotten, a `Paused` one does nothing (a decision recorded during the pause is a durable row the
first tick after the resume settles), and a `Pending` one goes to run start instead. Then it resolves the pinned
graph — a running run's graph parsed once already, so a parse failure here means the blob changed underneath it,
and the run is failed as unroutable rather than re-thrown on every sweep. What follows is four steps:

1. **Poll the lanes first**, before anything reads the node runs. A session that finished between ticks has to
   be seen as finished, or the run judges its whole graph against a row that is only still `Running` because
   nothing asked. Superseded tool passes are forgotten before anything is read off a lane. A `Tool` row still
   reading `Queued` is polled too: the `Running` write can fail after the slot and the registry entry were
   taken, and outside a drain the next admission repairs that — but a drain admits nothing, so without the poll
   the run waits on a row nothing would move again. **Deadline expiry lives here**, per row and as a fallback:
   only a row whose own lane poll answered nothing is expired (§3.3). A pass that landed inside its budget is
   settled off what it actually came to — including a sandbox timeout, which arrives with the evidence gathered
   before the clock ran out — and expiring it as well would overwrite that answer with a coarser one. There is no
   separate pass over every row.
2. **Settle recorded decisions.** A recorded decision is the durable half of a human act; turning it into a
   transition here rather than at admission is what lets a decision taken during a pause apply on the first tick
   after the resume. Two branches end the tick from here: a **gate rejection** the settle surfaced moves the run
   to `Cancelling` and returns, unless an in-flight cancel already supersedes it (§3.5), and a run that is
   already `Pausing` or `Cancelling` **drains** and returns (§3.4).
3. **Materialize a settled decomposition** — and if anything was materialized the tick *ends* there (§9),
   because what follows would judge node runs against the graph this call just replaced.
4. **Admit what is ready**, then recompute the run status.

One poll can move rows that are not its own: a failure routed to an upstream node resets that node's whole
subtree, and the rest of the list is then a picture of a graph that has changed underneath it. So once anything
has been written the rows are re-read, and a row this lane no longer owns is left alone — settling it would
write an answer about the round the run has just decided to do again.

Every node-run transition bumps the run version, so the top-of-tick version is stale by the time a drain or a
recomputation writes; re-reading narrows the window to what the check is actually for — a human decision or a
lifecycle command landing between the read and the write, which must win rather than be overwritten by a status
move. A decision the node run's status forbids (an `Approve` recorded against a `Blocked` row) is a durable row
re-read on every tick; left to throw it would wedge the whole run, siblings included, so it is recorded against
its own node run and the tick carries on.

### 3.2 Concurrency

`MaxConcurrentRuns` counts the runs a node is actually driving: `Running` and the two drains, and deliberately
nothing else. A `Paused` run is not being advanced and a `WaitingForApproval` one is waiting on a person who may
take days — counting either would let one unanswered gate stop every other run, which is the cap protecting
nothing at the cost of the throughput it exists to manage. The counts are read as summaries, one row more than
the cap per status, because a count must not decrypt a graph blob per live run.

A run above the cap is **waiting, not refused**: it keeps its rows and its place and the next sweep offers it
again. The sweep's own page size is deliberately *not* `MaxConcurrentRuns`: using an admission cap as a page
size orders live runs by creation date and then silently stops sweeping everything past it — starting with the
oldest stuck run, which is exactly the one a sweep exists to rescue.

### 3.3 Deadlines

A node run's deadline is derived from the **row** — its `StartedAtUtc` plus the node's declared timeout — and
never held in memory, on every tick. A deadline a process owns dies with that process, and the node run it was
bounding would run until something else noticed.

A grace period sits past the deadline before the dispatcher ends the row itself. The sandbox lane bounds its
own pass by the same node timeout counted from a moment *earlier* — the row is written `Running` after the pass
is started — and then needs a moment to sanitize its evidence and compose a report. Ending the row the instant
the number is reached would race that better answer and sometimes win by milliseconds, throwing the evidence
away for nothing. Only a row whose lane had nothing to say is expired here: a pass that landed inside its budget
is settled off what it actually came to, including a sandbox timeout that arrives with the evidence gathered
before the clock ran out.

A node that declares no timeout never expires, deliberately: the sandbox node types default to the Development
attempt budget, which the lane already applies to the work it can see, and a second number derived up here could
only disagree with it. What the deadline adds is the bound nothing else has — an agent session that never lands,
and a sandbox pass that stops answering its own budget.

Restart recovery is not the deadline's story: the startup reconciler collapses every in-flight sandbox row to
`Pending` before the first tick, so a row that survived a restart carries no start instant to expire. Nor is a
pause a special case, which is the payoff for deriving it from the row: the store clears `StartedAtUtc` whenever
a row goes back to `Pending`, and a run only reaches `Paused` once nothing under it is `Queued` or `Running`, so
a paused run holds no deadline that could expire while nobody is working. The resume's re-admission stamps the
fresh instant the next attempt counts from.

Where an expiry *leads* — another attempt, the node that produced what this one was judging, or a human — is the
retry policy's answer, the same as for every other retryable failure class. The sandbox registry entry is
dropped **before** the row is settled, and dropped rather than merely stopped: a re-attempt lands the row on a
new attempt inside the same call, and this tick's admission would otherwise find the registry still holding the
pass that ran out of time, leaving the fresh attempt's pass running with nothing to poll it.

### 3.4 Pausing and cancelling

Every lifecycle command is fire-and-forget: the endpoint commits an intent and returns, which is why the run
status set carries `Pausing` and `Cancelling` — the UI can say "cancelling" honestly rather than claim one that
has not landed.

A drain **asks** a live node run to stop; it does not settle it. A `Running` row belongs to an executor, and
only the executor knows what stopping it costs, so the next tick's poll writes the terminal off what actually
happened. Rows no lane owns are settled by the drain itself, because for them there is nothing to ask. The stop
is counted as work so the next tick comes immediately rather than a sweep later, and the rows are re-read
afterwards: judging "is anything still live" off the snapshot taken before the stops would cost a whole extra
tick for a drain that is in fact finished.

What each lane does with a drain:

- **Agent** — a cancel asks the session to stop and the next poll settles the row; a pause checkpoints and
  parks, and the row collapses to `Pending` rather than to a terminal. A `Pending` row with its session still
  attached is exactly what the resume re-admits: it continues the paused session instead of starting over.
- **Tool** — a pause lets a build finish. It holds no model slot, it cannot be resumed halfway, and killing it
  would throw away minutes of work to save seconds, so the run stays `Pausing` until the poll settles the row.
- **DevTask** — a cancel asks the attempt to stop and the next poll settles on what it did; a pause leaves the
  attempt to finish and parks the row where the resume re-drives the task from. A coder attempt is a model
  conversation with a workspace behind it and cannot be resumed halfway.

A pause keeps the durable human waits and the not-yet-admitted rows exactly where they are. The one thing it
*does* move is a `Queued` row back to `Pending`: it is queued for a slot nothing will hand out while the run is
draining, so leaving it would pin `Pausing` for as long as the lane stayed busy. That is the same collapse the
startup reconciler performs, for the same reason. A paused run also keeps its promised re-attempts — it is
coming back, and a resume that skipped every cushion a definition asked for would be the pause spending them.

Every terminal is reached through a drain or through the "nothing is live" recomputation. There is no path that
writes one directly, because doing so would strand the run's live node runs under a run no tick looks at again.

### 3.5 A stranded gate ends the run

A gate answered in a way no out-edge accepts ends the run. Reading it as `Completed` (every downstream skipped)
or as `Failed` (nothing failed) would both lie, so it goes through the drain like every other terminal and live
siblings settle and release what they hold instead of being orphaned.

Only a **human** gate can strand a run this way: every other node's dead out-edges skip their targets, which is
a route rather than a dead end. A gate with *no* out-edges counts, and that case is the shipped
"Research → Plan → Approval" shape rather than a corner — rejecting its terminal approval must not read as the
run having succeeded. `Approve` is exempt **only** there; at a gate that *has* branches, an `Approve` none of
them accepts is as stranded as any other answer, and completing it through skipped downstream would be the same
lie in the other direction.

A `Pausing` run must still take the rejection branch: the gate is already `Succeeded` by the time the pause
settles, so nothing would re-detect the rejection and the run would resume and complete — the exact lie the rule
exists to prevent. Only an in-flight cancel supersedes it.

### 3.6 The provenance walk in front of an apply

This is the runtime half of **`GRAPH-C4-3`**, whose structural half the parser enforces (§2.2).

An apply node's structural requirement (the apply-follows-a-validation rule, [API & Hubs](09-api-and-hubs.md)) is re-asked at
runtime, of the rows a run actually landed rather than of every structural ancestor: does the apply node's
provenance contain a `Tool`/`Validate` node whose row **succeeded**? It is asked **before** the consumption
record is written, so a blocked apply does not first record that it consumed inputs it never read, and a refusal
is `Policy` rather than a failure — nothing broke, and the answer is a person's.

The walk is one backward pass over the inbound edges whose state is in the provenance edge-state set, which is
what makes the rule the smaller one. A branch that did not run drops out with no special case: a `Failed` or
`Cancelled` source, and a `Skipped` one with anything dead behind it, kills every out-edge, so an `Any`
convergence whose other branch carried its own validation does not block the apply on work that was correctly
not done.

`Dead` and `Pending` are the two edge states that must **never** be in that set — a branch that did not run, or
has not run yet, carries no provenance. Every other state belongs in it, `Waived` included: an operator's skip
is waived precisely when everything behind it was satisfied or waived in turn, so the rows further back did run
and the walk has to reach them. Leaving `Waived` out is what would block `integrate` on the shipped template the
moment someone skips `verify`. A materialization test *enforces* this rather than proving it: it demands an
entry for every state outside those two, so a new edge state cannot be added without this walk being told about
it, and a future state meaning "undecided in some new way" is a third exclusion for that test to name rather
than an entry in the set.

Testing the candidate row for `Succeeded` separately is **not** redundant with the edge state: a `Satisfied`
edge does imply a succeeded source, but a `Waived` one does not — the rows *behind* a waived edge succeeded
while the waived node itself was skipped. Crossing the edge and still refusing to count a non-succeeded
validation is the pair that stays correct either way, and it is what makes an operator's skip of the validation
node itself still block the apply. An unmaterialized template key reads `Pending` and falls out exactly as
admission's own template filter drops it; the zero-task decomposition's no-op verdict row puts it back in (§9.3).

### 3.7 Consumption records

An inline node goes `Pending` → `Running` → `Succeeded`, skipping `Queued`: it waits for no slot, and the three
queue-reason tokens all name something real to be waiting for, so a `Queued` row with none of them would be the
row lying about why it is not running. It still costs two event rows, which is what makes the timing of a
fan-out visible.

Consumption of upstream artifacts is recorded by the dispatcher, once per attempt, on the first tick that admits
the node — a re-attempt is a new use of whatever version is current, and a tick that only finds the lane full
must not record a second. The sandbox lane cannot record it itself: it reads its inputs through a prepared
workspace and through Dev Mode, neither of which the store can see, so without the dispatcher's record a `Tool`
node would consume everything and record nothing, and the whole "stale because" link would be dead on every
graph whose fix loop reaches past a check. A gate records it too — without it the approval panel renders a
prompt and three buttons over nothing, and the operator approves a plan they cannot see.

The record is written when the node run is **handed** the artifacts rather than derived by walking the graph on
read: it is what the gate panel renders as its evidence list, what staleness propagation reads when an upstream
artifact is superseded, and what makes "this decision was taken on *that* plan" answerable a month afterwards.
A read-time walk can reconstruct none of it, because by then the graph may have been rewritten.

A **skipped** producer contributes no artifact, and the consumer is told which one it was, off the node-run rows.
Until a person could excuse one slice of a decomposition and let the rest reach the verification node, that
absence was invisible by construction — the consumer simply received one document fewer than the fan-out was wide
and had no way to know it, and judged a partial result as if it were the whole one. It is read off the rows
rather than off a synthetic artifact because an artifact snapshot is backed by blob storage and recorded as
consumed, and inventing one for work that never happened would put a document in the audit that no node produced.

Superseding is **mark-only**: a node run that consumed the version another node run just replaced is flagged and
a human decides what to do about it. Nothing is regenerated and the superseded bytes stay — versioning is the
point.

## 4. Human decisions

One decision surface answers both halves of the same human act: a gate's `Approve` / `Reject` / `RequestChanges`
and a stuck node's `Retry` / `Skip` / `Abandon`. They share a table and an endpoint because they are the same
thing, a person unblocking a node run.

**What an operator says travels with the attempt their decision starts**, not only into the decision row a panel
lists. The comment is merged into the node run's inputs exactly as a routed failure is, because that document is
where both lanes already read the next attempt's brief from — the agent objective composes it, and the
implementation lane turns it into the coder's change request. It is bounded like every other decision comment:
the text is free-form, the `terminal_reason` column holds 1024, and a prompt is the wrong place to discover
that. A decision that could not be applied because someone was verbose is the wrong way for a run to stop.

**Every `Retry` goes through the merge, including one typed with nothing in the box.** The merge is also what
*drops* an earlier retry's reason, so a silent `Retry` that skipped it would leave the previous operator's
sentence on the row for a try they said nothing about. The attempt the decision bought is written even when
nothing was typed: without it the members would be read again by every later automatic re-attempt, quoting a
person who said nothing about that try — and a lane that acts on the *retry* rather than on the sentence could
not tell a person's re-attempt from the policy's.

**Every `Retry` widens the node's attempt cap by one**, not only a `Retry` at the cap. So a `Retry` at attempt 1
of 3 leaves a node that can now reach 4 on its own, and that fourth try is an ordinary automatic re-attempt: the
operator's reason is scoped to the one attempt their decision started, so the attempt they bought carries it and
nothing after it does. What still bounds all of this is the run-wide `MaxTotalAttempts`, which counts an
operator's re-attempt and an automatic one alike and which no widening touches.

A retry always releases the node run's work session, agent node or not (§8.1). An answered node run may also be
the last thing the work item was blocked on, and the run status often does not move when it settles — so the
work-item release travels with the answer, for the same reason blocking it does.

A `Skip` is the one terminal that most needs a reason: an `All` join carries on past a skipped leaf, so the node
downstream is handed the skip as evidence and has only the terminal reason to say **why** the work it was
expecting is not there. The operator's own words are the whole of that why, so they travel — bounded.

## 5. The agent lane

`DevWorkflowAgentExecutor` runs one work session per agent node-run attempt. The runtime is a **client** of the
work-session machinery, not a fork of it: the stepwise executor, the checkpoints, the transcript and the
pause/restart/resume are one level down and already proven. What the lane adds is the four things a graph needs
from them — an objective composed from the node's inputs, an admission that queues honestly when the node's one
invocation slot is taken, a poll that turns a session status into a node-run status, and the promotion of what
the session produced into the run's own audit.

It writes node-run transitions itself, from inside the dispatcher's serialized tick, which is what keeps the
"every node-run status write happens inside `AdvanceOnceAsync`" invariant true. It starts no task of its own:
the work session is already detached, so there is nothing here to detach.

### 5.1 Admission

The row goes to `Queued` **first, always**, even when a slot is free a line later. It costs one event and it is
what makes the queue honest: three parallel agent nodes on a one-slot node are `Running, Queued, Queued`, and a
reader has to be able to *see* that rather than infer it. Losing the admission race between the capacity read
and the start leaves the row `Queued` with its reason and holding the session it already owns, so the next tick
starts that one rather than creating a second.

The graph node's model and effort travel on **every** drive, not only the create: the work-session layer holds
them for the run it is driving rather than storing them, so a resume after a restart is what puts them back.

Until the attach commits, nothing points at a created session: the next tick creates another, a work-item delete
cannot find it, and the external lifecycle refuses a workflow-kind session to every other caller. The create is
therefore undone on a failed attach rather than left for the startup sweep, and the original failure is what
propagates — the compensation is not the story. Ownership is re-read across **all** node runs rather than
assumed from the failure, and both directions matter: an attach can commit and still throw on the way back, and
an attach can fail precisely *because* another node run already owns that session. Deleting in either case would
take a transcript out from under a row that points at it. The cleanup runs without a cancellation token, because
it is the cleanup for a call that may itself have been cancelled, and it reports rather than rethrows: the
caller's failure is the one worth surfacing, and a session left behind is what the startup sweep is for.

### 5.2 Polling

The session status is the only authority: the lane never remembers what it dispatched, which is exactly why a
restart costs nothing but the poll that follows it. The poll runs in every non-terminal run status including the
two drains — a session asked to stop settles here, which is how the drain learns it may finish.

A session that landed while the host was down needs no re-running: the row is settled off the session's own
answer, which is what the lost tick would have written. A retry does not come through that path, because it
releases its session first, precisely so it cannot.

A retry always gets a **new** session — resuming the one that just failed resumes its poisoned context — but a
session that is merely `Draft`, `Paused` or `Interrupted` is the crash window between the attach and the start,
or a pause, and reusing it is what keeps a restart from stranding a conversation nobody will ever drive. A
resume is never attempted while the run is draining: under a pause the session was paused *on purpose* a moment
ago, and resuming it would undo the operator's command with the run still reading `Pausing`.

**Parking is routine, not a fault.** A work session pauses on its own step budget and a workflow node routinely
needs more steps than one run allows, so `MaxSessionResumesPerNodeRun` is a budget rather than a failure count:
exhausting it asks a human rather than failing the node, because the work so far is on the session and a person
decides whether it needs more. A resume is recorded **after** it lands and keyed by the resume index, so a
replayed tick cannot spend the budget twice; the attach event is also the per-attempt history the single-row
node-run schema does not keep. When the node's slot is held by another session the row stays `Running` — it has
not stopped working, it is waiting for its own continuation — and the next tick asks again.

### 5.3 What a completed session actually produced

A session completes for two different reasons and its status cannot tell them apart: the objective was met, or
the step could not meet it and closed anyway because nothing else lets a step end. **Reading every completion as
a success is the regression this reading exists to stop** — a verify node can write that it had *not* signed the
work off while the run still went green.

Two honest signals a stuck session leaves are read instead: a plan task it moved to `Blocked`, and the
`objectiveMet: false` it may declare on `complete_work_session`. Either stands the row down for a human, who
answers on the intervention gate that already exists — `Retry` re-attempts on a fresh session carrying their
reason, `Skip` routes around.

The blocked plan is read first because it is the thing the step prompt asks a stuck session for by name, and it
says **which** piece of work stalled, which the declaration alone does not. The store's own ordering is kept
rather than re-sorted: tasks are ordered by creation step so a task an update re-stamped does not jump the page,
and re-sorting would name the three least recently touched tasks instead.

The declaration is read off the completion **event** rather than the session row, because the row keeps only a
status — and the event is the same row, holding the same detail record, that the work-session supervisor reads
to close the session at all. A completion recorded before the argument existed carries no member, which reads as
met: an upgrade cannot retroactively block a node run that already finished. Unreadable detail is not evidence
of a failure either — the supervisor already completed the session on it, and inventing a block from a parse
error would strand a node run a person then has to un-stick by hand. The detail is read case-**insensitively**,
deliberately: the handler writes it with bare defaults today, and a later tidy-up onto the shared web options
would silently rename the members, after which a case-sensitive read binds nothing and every declared unmet
objective reads as met, with no exception and no log line to find it by.

The **last** completion request answers, which pairs with the **first** one winning inside a single step:
`complete_work_session`'s operation id is derived from the step, so a second call in the same step replays the
first through the store's dedupe and writes nothing, while each later step declares its own row. Together that
means a session cannot double-append a declaration after a lost response, and a session that kept working after
declaring is judged on what it said last.

A blocked outcome is **promoted exactly as a success would be**, because the status carries the honesty and
nothing downstream reads a `Blocked` row as a deliverable. Without the promotion the operator choosing `Retry`,
`Skip` or `Abandon` decides on the terminal reason alone, with the report that explains the block unreadable on
the session — and a `Retry` clears the node run's session pointer, so the run's own artifact table is the only
place these survive the decision. Evidence is written first and status last, the same order the work-session
loop uses one level down: a crash in that window re-derives the same answer, because the promotion is keyed and
the poll runs again.

The lane is not filtered by session kind: every session it drives was created through the workflow-owned
lifecycle and is of the workflow kind by construction, so a kind check would be a condition that cannot be false.

### 5.4 The write declaration at runtime

The structural half of the write-declaration rule would be inert on its own: it can only bite on an apply node, which the
parser already requires a human gate in front of, and on a declaration an author volunteered. What a node may
actually do is decided when the binding is resolved, so the question is asked there — twice.

- **Before the session is created**, where the refusal costs nothing and reads as configuration rather than as a
  stopped session. A node that already declares `WriteExecute` is not asked at all: its declaration has dragged
  it into the structural rule's gate requirement at save, which is the stronger and earlier answer.
- **On every turn the session takes**, because what the early check judges is mutable. The node's declaration
  and the template's waiver are re-supplied off the run's **pinned** graph on every start and resume, so nothing
  an operator edits mid-run can widen what a running session is allowed to be offered.

A turn refused this way stops the session, and the row the session wrote is what turns that stop into the lane's
own refusal — read back from the **record** of the refusal, never re-decided. Asking the guard again would
answer about the definition as it stands *now*: an operator who restored or narrowed it between the refusal and
the poll would send the node run down the retryable provider-failure path, losing the `Policy` class for a
refusal that really happened. A historical cause is read, not recomputed. The node's declaration is still
consulted first off the pinned graph, so an ordinary provider failure costs nothing but a dictionary miss. The
answer is read off the step row the supervisor wrote at the moment of the refusal — the outcome tag is the
stable code and its detail is the sentence — and the **last** such row wins, for the same reason a terminal
reason does: it is the one the session settled on.

The runtime refusal is `Policy`, not `Configuration`: nothing is misconfigured — the node's agent may do more
than the definition admits to, and only a person can say whether that is meant. Policy blocks for a human rather
than failing the run, and a retry would produce the same answer.

A node run whose key is not in the run's graph reads as "no override" rather than failing a resume over a label.

### 5.5 Composing the objective

The objective is assembled in a fixed order, and the order is the point.

| Phase | Bounded? | Why it sits where it does |
| --- | --- | --- |
| The node's own instructions | No | A node whose instructions exceed the work-session limit is the pre-existing refusal; silently trimming what an author wrote would be a worse answer than the block. |
| The operator's request | No | With the instructions, what a node cannot do without. |
| The decomposition contract (§6.4) | No | Lands before the policy phase reads the running length, so policy gets the room it leaves. |
| The scoped rule sets | Yes | Policy that pushed the objective over the limit would crowd out the request it is supposed to govern. |
| Upstream artifacts | Yes | The bounded phase (§5.6). |
| The skipped-step lines | Yes | Under the same overall bound, spending whatever the artifacts left. |

The whole composition holds itself to a ceiling **below** the work-session layer's own objective limit, which is
private to that class. The work-session layer **refuses** an over-long objective rather than trimming it, and
that refusal reaches the lane as a validation failure that blocks the node run for a human — so an objective
this lane composes must never approach it. The margin absorbs a modest reduction there without the lane
noticing, and a test fails if the two ever cross.

The rule sets are read straight from what the node run **recorded** — id, name, hash and the text itself — and
never from the rule set as it stands now. Editing or deleting a rule set mid-run is allowed, so a dispatch-time
read would hand the agent a document the audit does not name, or nothing at all. The fair share, the visible
truncation marker and the dropped-with-a-warning are `DevWorkflowPolicyText`'s, shared with the implementation
lane so the two cannot drift into rendering the same recorded rule sets differently.

**The operator's retry reason is lifted out of the generic member list and given its own sentence.** A person
who retried this step and said why is not one more anonymous input line, and read as one it is the line a model
is most likely to skim past. It is scoped to the attempt their decision started, so a later automatic
re-attempt composes an objective with no complaint in it at all.

**What did *not* arrive belongs in the same section as what did.** An `All` join carries on past a leaf a person
skipped, so a node can be handed four implementations where the fan-out was five wide — and with nothing saying
so it would judge the four as if they were the whole job. The skipped-step lines are composed **first** though
they are appended last, one line per skipped step carrying the reason the row kept (for an operator's decision,
their own words), with the heading folded into the first line so a budget that runs out leaves no heading
promising steps it could not name. Their room is then held **back** from what the artifacts apportion: the share
below hands the bodies every remaining character, so one long document would truncate to the ceiling and leave
the lines nothing to fit into. The reserve is capped at half the room so it cannot invert the problem, a wide
fan-out of skipped branches crowding out the branches that did produce.

### 5.6 Upstream artifacts in the objective

Each upstream artifact is rendered with its **contents**, not merely its name and id. A reference alone is
useless to a node that has no way to dereference it: a plan node told to turn `research.md` into a plan, handed
only `Report 'research.md' (version 1, id …)`, would invent one. The bytes travel in the objective because that
is the one channel the agent lane has. What each attempt was given is recorded as consumed in the same breath,
so the audit says what the attempt was handed rather than what a later read can guess.

Everything still to be written shares the room left equally — the room left *below* the reserve, not below the
limit — so a long first document cannot crowd out the ones after it and a short one hands its slack on. The
share covers the artifact's own header and marker as well as its body, and the bound is enforced on the
**finished** block, with header, body, whichever marker was rendered and the newlines all counted. Nothing
reaches the objective except through that check, so no reference-only line, marker or rounding can push it past
the ceiling. A block that will not fit falls back to its header alone — the agent still learns the artifact
exists, which is the half that matters most — and one with no room even for that is dropped.

Text is decided from the **declared** media type rather than by sniffing bytes, and only once the blob store has
verified the artifact row's own digest and size. An artifact whose bytes no longer match what produced them is
handed over as a reference and a warning: silently injecting unverified content is how a tampered file ends up
being reasoned about as if it were the research.

The read is gated on the row's recorded size **before** the read, because reading is what costs: the blob store
materialises the whole blob and the decode allocates a UTF-16 copy of it, and an artifact may be as large as
`MaxArtifactBytes` allows, so a fan-in would allocate hundreds of megabytes to keep a few thousand characters.
The ceiling for a *readable* artifact sits two orders of magnitude above anything the objective could use and
two below the maximum an artifact is allowed to be. The trade is that a huge document loses even its prefix,
which is the right way round: the first few thousand characters of a 64 MiB file were never grounding, and the
marker says so.

## 6. The implementation lane

`DevWorkflowDevTaskExecutor` drives the Development task the work item's project owns, through the chain that
already exists — coder attempt, deterministic validation, independent review — and succeeds when that task
reaches `AwaitingApply`.

It is a **client** of Dev Mode, not a fork. The hash-locked apply gate is the concept's integration and
independent verification, and it is built on the task and attempt rows; re-implementing any of it here would
either rebuild that chain or weaken it. So the lane holds no execution state of its own: it asks for the next
action, reads what the task became, and writes that onto the node run.

There is **no `Queued` hop**: this lane hands out no slots, and a `Queued` row would have to name a queue reason
for a queue that does not exist. What bounds the work is `MaxConcurrentRuns` — Dev Mode's own one-active-attempt
rule is per *task*, so four runs on four projects legitimately drive four attempts at once — and each of those
attempts carries the Development attempt-duration budget of its own.

### 6.1 Which task a node run drives

**Decided once and then remembered.** A node run that already names a task drives that one; the pointer comes
first and is never second-guessed. It survives a reset by design (a previous attempt's task is that attempt's
evidence), so re-resolving would let a re-attempt walk away from work already under way. That is Dev Mode's own
rework loop, and it leaves the per-round evidence where the rest of it lives.

A **materialized child** gets a task of its own in the same project, because it implements its own slice and two
children sharing one task would overwrite each other's work. Creating it is keyed on the run and node key
*without* the attempt — the task belongs to the node for the life of the run — so a crash between the create and
the pointer write is answered by the same task on re-dispatch rather than by a second one nothing points at.
The child inherits the project's first (operator-authored) task's **acceptance criteria and review budget**,
which carry the standard the project's work is judged against. What it must **not** inherit is the
**requirements**: that would hand every child the whole feature, N times over, each looking like a legitimately
configured task while doing it.

Anything else — the ordinary undecomposed graph — drives the project's existing task rather than creating one.

A brief a node cannot be given a task from, or a project that is gone, are both **decided facts** and stand the
node down for a human. An escape there would be the worst failure mode available: the row would still be
`Pending`, so no deadline could fire on it and the sweep would re-dispatch it every tick forever. Both messages
are the engine's own text about its own rows — no host path, no model output. A Dev Mode *concurrency*
exception deliberately does not land there: a lost ledger race is transient, and the next sweep is the right
answer to it.

### 6.2 The child brief

The brief is the seam decomposition writes through.

- `requirements` is **mandatory**. An input that is absent, unparseable, shaped for something else, or blank
  stands the node down for a human. The only thing in this repository that writes these rows is decomposition,
  so a missing brief is a bug in the thing that materialized the child — and inheriting the parent's
  requirements would hide it behind N children each implementing the entire feature.
- `title` falls back to the node's own label, and the acceptance criteria to the project's first task: neither
  says what to build, and the project's standard of done is the right one for a slice of it.
- The allowed-paths guidance is **folded into the requirements** rather than carried as a field of its own,
  because `requirements` is the whole of what the lane renders to the coder. A second field would have to be
  threaded through the brief, the executor's own reader and the Development task before it reached a prompt, to
  say what a sentence says here. It is guidance, not a boundary: nothing refuses a coder for touching a file it
  does not name.

### 6.3 Policy injection

Which rule sets apply is stated once, in `DevWorkflowRulePolicyResolver`. Each of the two axes — `projectIds` and
`nodeTypes` — matches when it is **empty**, and otherwise by exact case-insensitive membership; both must match;
every match is applied, ordered by name. There are no globs, no precedence and no conflict resolution: a rule set
either applies or it does not, and two that both apply are both injected. A scope column that cannot be read at
all matches **nothing** — the endpoints are its only validating writer, so an unreadable one is a hand-edited row
and "applies to every node on this box" is the dangerous reading. A read model renders the same row as empty axes
instead, because a management page that cannot load the row is a page nobody can use to fix it.

Resolution is **recorded** on every node-run type, agent or not, so `appliedRuleSets` is an honest answer whichever
node is asked. Two lanes inject the bodies: an Agent node's objective and a DevTask node's coder and reviewer
prompts. A Tool node runs a command profile with no prose channel to put a body in, and a HumanGate asks a person;
both still record, and injecting there is additive whenever those lanes grow somewhere to put it. The body is
**snapshotted** for the node types that inject it, and only those. Re-reading the rule set at dispatch would let an
edit landing between materialization and dispatch hand the agent one text while the audit permanently claimed
another, and a delete leave nothing to inject at all; on every other node type the text would be a copy nothing
reads, decrypted into each node-run snapshot on every list. Those rows record the id, the name and the hash, which
is all their audit needs. The hash keeps the audit truthful after the rule set is edited or deleted, and the
snapshotted body is what the node was actually given, so the two can never tell different stories. Neither body
reaches the wire: the node-run response carries ids, names and hashes, and a reader who wants the text asks the
rule set for it.

The rule sets the node run **recorded** are put where Dev Mode's own coder and reviewer prompts read them — the
same event-derived route the previous round's feedback travels, so it costs no column and no migration. It is
written on **every** dispatch, not only the first bind: the operation id is keyed to this node-run *attempt*, so
a replayed tick meets the store's idempotency, while a fix loop that routes the node run back around re-applies
the policy the settle cleared. The write is inside the dispatch `try`, because a task deleted between the
resolve and it is the same decided fact the catch stands the node down for.

Nothing applied records an **empty** resolution rather than nothing at all — and so does a resolution whose
bodies are all missing or all too long to fit, the render being what decides and what logs each section it
dropped. Writing nothing was the leak: the snapshot answers off the latest row, so a workflow that resolved no
policy would leave the *previous* one governing rounds it never applied to.

The injection is **revoked** when the node run stops driving the task, so what governed the workflow's rounds
does not go on governing the operator's own later ones. The revoke is called from the shared writers that
settle, block and cancel a node run rather than from their call sites: every terminal path has to revoke, and
naming them one by one is how the attempt-cancelled settle, the stand-downs and the run cancel are missed. It is
deliberately **over-eager** — a failure the retry policy re-attempts increments the attempt, so the re-dispatch
derives a new operation id and records the policy again. A **pause** is the one stop that does not: it parks the
row at the same attempt, whose operation id is already written, which is why only the cancelling half of the
stop clears. It is best-effort by construction: a node run that never bound a task, and a node whose Development
Mode is switched off, have no injection to revoke.

### 6.4 The decomposition contract

`DevWorkflowDecompositionContract` is appended to a node's objective whenever its materialization template
carries a `DevTask` anywhere in the subtree — straight after the node's own instructions and before anything the
operator configured, the same standing as the instructions, because a task written against the wrong
capabilities is worse than one written against no policy.

These are not planning preferences; they are the implementation lane's own contract. A Development attempt has
to export a **non-empty** patch to finish, and it is refused outright for touching a test file that existed at
the base commit. A decomposition that does not know either fact writes slices nobody can complete: each is
refused, re-attempted and refused again until it blocks the run in front of a human, burning the task's attempts
on "survey the code" and "add the test to the existing file".

It is owned in code rather than baked into the seeded template because it describes the **lane**, not one
template's strategy: every node whose template expands into that lane, in every graph an operator writes, needs
it, and a copy in each seeded string would drift from the code that enforces it and cost a seeder revision every
time it were reworded.

It is **not** on every materializing node: a template of `Agent` and `Tool` nodes produces no coder attempt, so
its decomposition would be told to make every task export a patch and add a new test file when nothing there
asks for either. The predicate is `DevWorkflowGraph.TemplateSubtreeHasDevTask` — the same one the materializer
refuses a task package by, so what a decomposition is told matches what it is judged by.

### 6.5 Reading the task back

Each tick asks the chain for its next action under an operation id derived from what the tick **observed** — the
task's status and how many attempts it has — so a tick replayed after a crash asks for the same action rather
than a second one, and a tick that genuinely finds a new state asks for the next.

| What the tick finds | What the node run becomes |
| --- | --- |
| `AwaitingApply` | **`Succeeded`** — an independent reviewer approved the exact subject, and applying it is a later act behind a gate this run records. A task somebody has already applied is the same answer arriving later. A re-attempt with no routed failure behind it succeeds immediately, which is the honest answer: nothing has said the implementation is wrong, and the claim "this task is implemented and waiting to be applied" is still true. |
| `AwaitingApply` **with an unconsumed routed failure** | A rework round instead (§8.2). |
| An attempt this node-run attempt started, still running | Stays `Running`. The chain drives its own attempt to completion without being ticked. |
| A stage boundary | Counted as **work** even though it writes no row of this module's: the chain runs one attempt per request, so the tick that asks for the next one has moved the run on, and saying otherwise would leave the whole implementation waiting a sweep interval per stage. |
| An attempt that failed, was interrupted or was cancelled | The retry policy decides — another attempt here, the node that produced what it was implementing, or a human. |
| Review rounds run out, or an operator stood the task down | **Blocked for a human.** Another node-run attempt would re-drive a task that is not going anywhere. |
| A workspace policy refusing the attempt's diff | **Blocked for a human.** That is not the provider failing: it is the engine declining work on evidence, so it goes straight to a person instead of spending three more attempts to be refused identically. |
| Nothing to do, and the task has not moved | **Blocked for a human** (§6.6). |

Only the attempts **this** node-run attempt is answerable for are read. A failed attempt from a previous round
is still on the task — it is that round's evidence — and reading it as this round's answer would settle every
re-attempt off the failure that caused it, spending the node's whole budget without ever asking the chain to try
again. Both instants come from the same clock and the row's is taken before anything is started, so the
comparison is a fact about ordering rather than a guess. The dispatch path carries that instant: the row the
call goes on to judge attempts against is composed during the dispatch, and a snapshot still holding the
pre-dispatch null would make the comparison vacuous.

A cancelled attempt deliberately does **not** touch the node run: it is still winding down, and only the next
tick's poll knows whether it stopped or finished inside the window. The one caller that does write a terminal
off a cancel is the node deadline, which writes its own, because the clock is the reason and the attempt's
cancellation is only the consequence.

### 6.6 Telling "stuck" from "busy"

"No next action" does not mean stuck. An attempt row is not the only way Dev Mode is busy: **deterministic
validation is a phase its own supervisor drives with no attempt row at all** — it runs the project's command
profile and then moves the task on to `InReview` or `ChangesRequested` — so a tick landing inside that window is
told there is no next action, which is true and is not a fault. Without a guard the lane stands tasks down
moments after validation starts, with a *succeeded* coder attempt on them and Dev Mode calmly finishing.

The **version** is what makes the guard general rather than a patch for one status. Naming `Validation` alone
loses the same race one hop later: the supervisor can finish and move the task to `InReview` between the ask and
the read, and a task that *moved* since the snapshot the tick opened with is working, whatever it moved to. Only
a task sitting exactly where the tick found it, with no attempt and no next action, is genuinely stuck — and
that is the one that blocks.

Re-reading also tells apart "the task has no next action" from "something else started one" without matching on
a message, because the Development views can drive the same task: if an attempt is running now, the run is not
stuck, it is simply not this tick's to advance.

A version conflict from the chain means something else moved the task between the tick's read and its ask — an
operator in the Development views, or a sibling tick. Nothing is owed and the operation id is still unwritten:
the next tick reads what the task became and asks again if that is still the right thing to do. **Only** the
concurrency case is swallowed: the transition check runs the version *before* legality, so a task whose status
moved always surfaces as a concurrency clash rather than as an invalid transition — which means an invalid
transition is a broken invariant rather than a race. Swallowing it would start the coder round unbriefed and
call that success; propagating it stalls the node run loudly, and the dispatcher logs it and re-derives from
unchanged rows next tick.

An unexpected exception's **message is not surfaced**: it is the one string on this path nothing has sanitized,
and it can carry a host path or a fragment of a prompt.

### 6.7 Operator retry reasons

When a person retries a `DevTask` node run and says why, the sentence has to reach the coder about to redo the
round, so it travels the route a routed rejection travels: a task's own change request is the one channel Dev Mode
composes a coder prompt out of. It is marked **operator-directed**, which lets the coder and reviewer prompts rank
a person's sentence above the task's immutable requirements and above a reviewer's feedback. The node run is
**not** settled — the next poll finds the task at `ChangesRequested`, where the ordinary next-action path starts
the round it was going to start anyway.

There are two shapes of the same act:

- From **`InProgress` with a coder attempt that did not succeed**, the next action is a coder round either way,
  so the change request adds the sentence without changing what the retry does. This case reads the **sentence**.
- From **`Blocked` at the round cap**, it does more: it **widens the cap by one**, turning the retry from a no-op
  into a round. That widening is the single documented exception to a Dev Mode task being immutable, bought one
  round at a time by the same click that already widens the node's own attempt cap. This case reads the **act**,
  not the sentence, so a silent `Retry` buys the round exactly as a spoken one does and the operator row it writes
  carries no reason. Without it, a retry on a node blocked at "all rounds used" re-dispatches, re-blocks within
  seconds, spends one of the node's own attempts and never builds a coder prompt, leaving the operator's sentence
  stored, shown in the panel, and unreachable by any model.

The cap widening is also **the one task-status hop nothing else records**. Dev Mode's management service logs
the hops *it* decides, and this edge is bought by an operator's `Retry` in the workflow lane instead, so a live
round could otherwise see the cap widen and the task move with no line saying either happened. The lane logs it
with the same literal "task status" phrase, so the one grep that finds every hop still finds this one.

#### Where an operator's sentence deliberately does not reach a coder

Every other task status is a **known hole** — the retry still happens, the operator's sentence simply does not
reach a coder — and each is deliberate:

| Task status | Why no change request is written |
| --- | --- |
| `InProgress` whose last coder attempt **succeeded** | It is on its way to deterministic validation; asking for changes would throw that round and its evidence away. |
| `Ready`, `Planned` | No round to brief — nothing has been implemented yet. |
| `ChangesRequested` | Already carries the verdict that asked for the round — a reviewer's feedback, or the deterministic gate's failure — and overwriting it would replace the very thing the round exists to answer. |
| `InReview` | The ask would **change** its next action, from the re-review that is the right answer to a reviewer that failed into a coder round nobody asked for. |
| `AwaitingApply` | Never reaches here: the node run is settled `Succeeded` first, because an approved implementation waiting to be applied is still a true claim. |
| `Blocked` for any reason **other** than the round cap (an operator stood it down, the repository is gone) | The widening buys a round, and a task blocked on something a round cannot fix would spend it re-earning the same block. |
| `Validation`, `Completed`, `Cancelled` | `Validation` is Dev Mode's own supervisor window and holds no attempt; the other two are settled above. None of the three is a round anything can brief. |

#### Two rules that hold the whole thing together

**Nothing has to withdraw an instruction, and that is why no branch tries.** Every writer that increments the
attempt targets a `Pending` row, and the `Pending → Running` dispatch records a fresh policy row **before** the
retry reason is read — so by the first tick of a new attempt the store's boundary already sits above every
instruction an earlier attempt wrote, and an empty `Retry` has retracted the last one without anybody writing a
retraction. Within one attempt the reason cannot change, because it is keyed to the attempt on the way in. A
silent `Retry` writes an operator row with **no** reason, which is already the retraction: the round it buys is
told nothing rather than told the last person's sentence.

**One ask per (node run, attempt), and the ledger is what enforces it.** The reason stays on the inputs for the
life of the attempt, and the round asked for walks the task back through `InProgress` without starting an attempt
this node run is answerable for — so without the operation id the tick would ask again and loop the task back to
`ChangesRequested` for as long as the poll continues.

### 6.8 The node's output document

An implementation node writes the slice of the shared output document every executor writes: the **verdict** a
conditional edge routes on, and the **task** a reader drills into. No patch and no evidence — those live on the
Development task, which is exactly why the node run names it.

## 7. The tool lane

A `Tool` node's sandbox work is detached, and the invariant the whole runtime rests on is that only the
dispatcher's serialized tick writes a node-run status. So what runs out of band answers with a **pure value**
and the tick decides what it means. In that result the failure class is null exactly when the pass passed: a
failing verdict from the commands themselves is `ToolCommandFailed`, which is the fix loop's fuel rather than an
error, while the other classes mean the pass never got as far as a verdict.

`MaxParallelToolNodes` bounds how many `Tool` and `DevTask` node runs may hold a sandbox at once. The value
exists because the attempt supervisor the lane copies has no cap at all, so a workflow fanning out eight
validation nodes would otherwise start eight builds.

The command interface is the second and last interface-for-one-implementation in this runtime, for the same
reason as the agent session seam: it is the only way to exercise the graph without provisioning a real
repository and running a real build, and the harness that makes every other test fast depends on being able to
script it. Everything around it — the lane, the rows, the report artifact — is a concrete type.

The lane is a **singleton**, which is why it is a class of its own rather than a method on the dispatcher: the slot
count and the in-flight registry outlive a tick and a scope, and a second instance would hand out the same slots
twice. The dispatcher resolves it once and asks it three questions — dispatch this, has this landed, stop this —
exactly as it asks the agent lane. A slot is taken **before** the row moves to `Running` and released in the
detached task's `finally`, the shape work-session admission uses, so the cap holds across concurrent admissions
rather than merely looking like it does. The row still goes to `Queued` first even when a slot is free a line
later: three validation nodes on a two-slot lane are `Running, Running, Queued`, and a reader has to be able to
see that rather than infer it from timing. The integration variant takes the same slot as a validation pass, going
through the same workspace machinery against the same repository — the resource the count bounds — and that is the
one difference from the `DevTask` lane, which takes no slot, driving a Development attempt with a bound of its own.

**A stop is asked, not settled.** A build asked to stop is still winding down, and only the next tick's poll knows
whether it landed cancelled or finished inside the window; settling it here would also hold the advance gate, and
with it every other run, for as long as the stop took. A pass that has *already* been asked answers that there was
nothing to ask, and that matters: the registry entry lives until a poll sees the pass land, so a cancelling drain
reaches the stop every tick until then, the caller counts a yes as a written transition, and the dispatcher
re-signals itself after any productive tick — so answering yes each time spins the drain for as long as the
commands take, and a build's worth of ticks lands on whatever else shares the thread pool.

**Superseded passes are forgotten** once a tick, before anything is polled, because a reset reaches rows this lane
is driving without coming through it: the node that routes a failure need not be the node whose pass is in flight.
Otherwise the registry would claim to be driving a row that has been re-attempted, and the answer in hand would
eventually be settled onto an attempt it never ran. Removing the entry is the load-bearing half, not the cancel —
a row settled with its entry left behind would refuse the next attempt's admission its place in the registry, and
that attempt's pass would run with nothing polling it. The result is thrown away deliberately, describing work the
run has decided to do again, and the pass is left to unwind on its own, releasing its slot in its own `finally`;
waiting for that here would hold the advance gate for as long as a build takes to notice it has been cancelled.

A result is **consumed only once the settle has committed**. Doing it first would spend the result on a write that
may throw — an over-budget blob, a lost version race — and the next poll would then find no entry and record "the
host stopped" about a pass that finished perfectly. The settle is idempotent, so a replayed poll re-derives the
same answer instead. What a stopped pass is told to have been doing is said in the **node's own terms**: this lane
runs two kinds of work, and "validation commands stopped" is the wrong account of the node that puts approved
patches into a repository, the one node where what was and was not done matters most to whoever reads the row.

### 7.1 The integration half

An `Apply` Tool node hands the tasks this run implemented to Development Mode's own hash-locked apply gate, one
after another, and reports what that gate made of each. It adds **no apply mechanics**: the call it makes is the
call the Development apply endpoint makes, so the evidence chain — independently approved subject, exact patch and
manifest digests, host state inspected before and after — is the one that was already there. What is new is only
*when* it runs: downstream of a human gate the graph is required to place in front of it (§2.2), never on the
implementation node's own success.

It is **sequential and stops at the first refusal**. The gate's single-writer discipline is per *task*, so N tasks
are N calls, and once one has been refused the repository is not in the state the next patch was approved against,
so continuing would apply patches to a tree nobody judged. No tasks at all is a **pass**, not a refusal: a
decomposition may legitimately answer that no follow-up work is needed, and the report says so rather than leaving
a reader to infer it from an empty list. What this counts as one "command" is one task's apply — the counts are
what a conditional edge routes on and what a fix-loop objective quotes, and for this node the unit of work is a
task rather than a shell command.

Each task's apply is keyed on the run, the node, the **attempt** and the task, so a retry *asks again*. Applying
twice is prevented by the task's own state instead: the completed short-circuit, which is what a landed apply
leaves the task in, and Development's own idempotent arm, which recognises an exact approved result already in the
repository. That is what makes the operator's retry work — a node standing `Blocked` because the gate declined a
patch is retryable by hand, and a key that ignored the attempt would hand that retry the recorded refusal without
asking the repository anything.

A refusal at the base check is the gate **declining on evidence** rather than an error, and it is what the second
patch of one fan-out finds, the first one's applied change sitting in the tree it was approved against. Concurrent-
patch merge is deferred, and this is where that boundary shows up at runtime, legibly, rather than as a patch
applied onto a tree nobody judged. Where the evidence chain itself refuses — the approved subject is no longer the
current one, the review is not the one that approved it, or the repository's trust has lapsed — asking again
answers none of them, so it goes straight to a human. A task the gate has **already** stood down gets the lane's
own sentence in front of Development's, whose answer on the next attempt is about a *precondition* rather than a
cause ("patch preview requires an independently approved task awaiting explicit apply" is true, names no cause,
and reads as a different and less serious problem than the one being retried). The sanitized original is kept
after it, being what the Development view says about the same task, and the cause is read off the task rather than
assumed: a declined apply is the usual way one gets blocked but not the only one, and Development already recorded
which. That reason is sanitized again here, because a second reader is a second exposure.

Which tasks a node integrates is bound to the **node runs**, not to the project's task list — the project also
holds the operator's own task and whatever earlier runs left there, and a run may only integrate what *it*
implemented — and to this node's own **ancestry**, not to the run, because a run may carry more than one gated
apply lane. The gate in front of this node displayed the work on the branch that reaches it, so applying a
succeeded task from a parallel branch would be this node landing a patch its approval never showed anyone. It is
resolved over the run's pinned graph, the same revision the tick routed on, so a materialization's clones are in
it and "upstream" is exactly the set the approval covers. A node run names its task exactly when the
implementation lane bound it, the materialized children of one decomposition carry their 1-based index, and a
re-attempt keeps the same pointer, so distinct task ids in index order is the whole enumeration.

A **cancellation between two patches** is answered as a result rather than by letting the token throw: one patch
may already be in the repository, and the sentence saying which is on a report the throwing path never writes.
Tasks the sequence never reached are named in that report too, because one listing two of four tasks would read as
a run that implemented two; they carry no title, reading one being a store round-trip on a token already
cancelled. A task **title** is model text arriving through a task package, and it reaches an operator in both the
terminal reason and every report entry, so it is sanitized like the lane's exception messages; a title the
sanitizer refuses — one carrying credential-like material it will not redact — is replaced by the task's own id
rather than allowed to escape as a second exception.

The apply report is deliberately **not** the validation report shape: that document describes commands run against
a workspace, and filling its command list with task applies would be a report claiming evidence it does not have.
It is written under the ordinary `Report` kind for the same reason. The node's detail becomes the row's
`terminal_reason`, whose composed refusals are additive — a lead sentence, a model title, a stored blocked reason
and an exception message — so it is capped in the one place every detail passes through, lead kept and tail cut,
because SQLite enforces no declared length and an over-long one would break the contract silently.

### 7.2 The patch overlay

A Tool node runs the substrate **below** the Development validation runner rather than that runner, which is welded
to the task machine: it starts a validation transition on a task row, reads that task's last succeeded coder
attempt, and finalizes by writing a task status, and a workflow Tool node-run has none of those rows. What it does
share is everything that matters — the same workspace provider, the same command profile, the same sanitizer and
the same verdict — so the gate a workflow node applies is the gate Development Mode applies, not a second one that
drifted. The one thing that had to move is where committed credentials are reported: that write resolved the
project from a task row, so it goes through a secrets sink, and this lane hands the provider one that simply
collects them for the tick to record. The provider itself is constructed with that sink rather than resolved, and
everything else it needs still comes from the container, so the wiring cannot drift from the registered one.

The node puts the implementation's **own work** into the freshly prepared workspace before anything judges it. An
implementation node runs through Development Mode, which leaves its work as a *staged* patch in the attempt's own
worktree and never touches the base branch — so a validation node that cloned the base branch and ran the commands
would judge the committed base and report green about a tree the change is not in, voiding the per-slice quality
gate and leaving the fix loop unable to fire on a real implementation failure.

The bytes are the approved patch artifact's, read through the same immutable hash-and-byte-count verification the
trusted host apply port reads them through and bound to the task's own approved-subject hash; the apply is
`git apply --index` under the hardened argument vector, inside the **sandbox** workspace only. A `DevTask`'s write
is sandbox-scoped by ruling **D8** — its patch reaches a real repository only through an apply node. The operator's
registered repository is never written to, no trust decision is re-asserted, and a patch that does not verify or
does not apply **refuses** the node rather than silently validating the base underneath it. Every anomaly on this
path refuses rather than falling back to the base: a validation node runs only after its implementation succeeded,
so an upstream implementation with no approved patch, a task carrying no approved subject, or a graph offering more
than one implementation are all states this node cannot judge, and a green report over the base is exactly the
silent lie the overlay exists to remove. The honest base-validation path is the one with no upstream implementation
to judge — a Tool node whose branch reaches no `DevTask`, or one past an apply, whose base already *is* the applied
work.

Which implementations a validation node judges is resolved from the **graph**, not from whether the node run
happens to be a materialization clone. For a clone it is the `DevTask` rows of the same clone group — same origin
node run, same 1-based index — that this node sits downstream of, derived from the graph and the rows rather than
from node keys, a clone's key being the template's key with a suffix the decomposing agent chose. For a node run
that is not a clone it is the nearest `DevTask` ancestors by graph edges, which is the same question asked of an
undecomposed graph: a plain `implement → validate` pair belongs to no clone group, and answering "no
implementation" there made the one node whose job is to judge the patch judge the committed base instead. The walk
stops at an `Apply` Tool node and never looks past one, because past an apply the work *is* the committed base —
so a full validation after an integration keeps validating that base, the ceiling recorded for it, instead of
re-overlaying patches the apply already landed. It answers the whole set rather than a first match, more than one
implementation feeding one validation being a fault to refuse rather than something to pick alphabetically;
merging a multi-child fan-out into one workspace stays a ceiling.

The execution snapshot a Tool node-run stands in for an attempt with carries **both isolation keys from the
attempt**, not from the node run. The provider partitions its worktree by the task id and reuses a preserved one
whole, including the base commit recorded in its manifest, so a node run whose identity was constant across
attempts would have its second attempt re-validate the first attempt's commit in the first attempt's tree and
report green or red about a state nothing is in any more. Deriving the id from the attempt is what makes a
re-attempt a real second try: the directory does not exist, so the base branch is resolved again and the workspace
is built from it. The derivation is deterministic and the same one the tick's idempotency keys use, so a poll
replayed after a crash prepares the workspace this attempt already has rather than a second one beside it.

A pass is bounded by the node's budget, the project's and the hard attempt cap, **whichever is smallest** — the cap
is the outer bound it claims to be, so a node asking for more gets less. That bounds the *pass*; the row's own
deadline bounds the *node run*, and the two cannot disagree about the node's number, because the pass takes the
smaller of it and the sandbox's and counts from the earlier instant. So a pass answers with its evidence before the
dispatcher would have to end the row without any. The commands that did finish are still evidence, and the report
says which of them passed before the clock ran out: that is what tells an operator whether the node is slow or
stuck, and it is the same artifact every other outcome leaves. The verdict is evaluated against the list this node
**actually ran**, its first rule being that every declared command produced evidence — a node narrowing the
profile's list would otherwise fail that rule by construction, while a timed-out pass fails it for a real reason,
the commands it never reached, so the report names the missing evidence and the row names the clock.

The report bytes are bounded so the artifact store can always take them. Each command's captured output is already
capped, but a multi-command profile can carry several of those caps and the artifact limit sits below that, so a
document that will not fit keeps every command's identity, exit code, duration and test result and gives up only
the captured text, with a line saying so: an evidence record that names what failed beats an artifact write that
throws and leaves the node run with no evidence at all. The report is deliberately not the Development validation
report — that record's subject, manifest and expected-result hashes describe a coder attempt's patch, and filling
three hash fields with placeholders would claim evidence it does not have.

## 8. The retry policy

`DevWorkflowRetryPolicy` is where a failed node run's next move is decided: re-attempt it, re-run the upstream
node that produced what it was judging, or stand it down for a human. It is one class rather than a branch in
each executor, because the agent lane and the sandbox lane must answer this question identically — a build
failing three times and an agent failing three times differ in what produced the failure and in nothing else —
and because the cross-node fix loop reaches rows neither lane owns. Every write it makes goes through the store
inside the dispatcher's serialized tick, exactly as the executors' own settles do.

A **cancelling** run is exempt: it has been told to stop, and re-attempting anything under it would be the
runtime resurrecting work an operator asked it to abandon, so such a failure settles `Failed` and the drain takes
it from there. A **pausing** run is *not* exempt — a re-attempt lands the row at `Pending`, which is exactly
where the pause drain parks work anyway.

`Internal` is retryable exactly once: an executor that threw something nobody predicted may have hit a
transient, but a second identical throw is a defect, and spending a node's whole attempt budget on it only delays
the human who has to read the log. A **decomposing** node's `Configuration` failure is retryable once for the
same reason, and is the one named exception to `Configuration` being non-retryable: the thing that wrote the
unusable task package is the thing that can rewrite it, and the re-attempt carries the complaint into its
objective. That is scoped to the node rather than to the failure because the failure class is all a lane hands
over; the cost of the wider reading is one spent attempt on a decomposing node misconfigured some other way, and
the answer after it is the same human.

### 8.1 Same-node re-attempt

The next attempt is told what the last one came to, or the agent composes a byte-identical objective and does
the same thing again. That is read off the failure in hand rather than off the row the `Pending` write is about
to clear, and the merge strips any earlier prior failure, so rounds replace rather than nest.

A same-node retry writes **no** `priorFailureNode`. That key names the *other* node whose verdict routed the run
back, which only a route has. Writing this node's own key there makes a same-node retry indistinguishable from a
cross-node rejection, and the implementation lane reads it as "a downstream node rejected this implementation" —
after which it re-asks an **already-approved** task to be implemented again, quoting a verdict nothing reached,
until the task's review rounds run out and its approved patch is discarded. An earlier genuine route's key is
left in place: a transient retry in the middle of a fix loop must not lose the rework the loop asked for, and
overwriting the prior failure there would leave the routed node's name with this node's own count-less output
under it.

`ClearWorkSession` travels with **every** re-attempt, agent node or not: a retry that resumed the session that
just failed would resume the context that failed with it, and the release is also what stops the fresh attempt
being settled straight back off the old session's answer. The store clears the failure fields, so the event the
re-attempt writes is the only record of what is being re-attempted — which is why it carries its own detail
rather than the reason.

The run-wide budget rides **with** the write, so the store re-checks it under the writer lock rather than
trusting the caller's earlier read (`FU3-4`). It is inert on a reset inside a route, which admits the whole
cascade once against the route's own transaction instead of each reset against this one.

Delays are a **cushion, never a bound**: the bounds are the row's `Attempt` and the run's total, both durable.
The map of "when a re-attempt may be admitted" is in memory, so a restart re-admits immediately, and that is the
answer rather than a gap in it — re-admitting early can only shorten a wait, in the one situation that has
already cost more wall-clock than any delay a definition would ask for. The durable record exists either way:
the scheduled-retry event carries the delay instant, so the log says what was promised even though nothing
re-arms it. An entry is written for **every** re-attempt including the ones that ask for no delay, so a fix-loop
reset of a row that was already waiting on a clock does not inherit the previous attempt's, and so the map does
not accumulate an entry per delayed retry for the life of the process. A run's entries are dropped wherever the
dispatcher forgets its parsed graph, and for the same reason — except a **paused** run, which is coming back and
whose cushions still stand.

### 8.2 The cross-node fix loop

The node that failed is not the node that is re-run. The named upstream **target** re-runs with this failure in
its inputs, and every node run downstream of it re-runs with it — including the one that failed, which is a
descendant by the ancestry rule the graph validates at parse.

The reset set is **all** descendants rather than the path back to the failure, because a `Succeeded` sibling
holds an answer about an implementation that no longer exists. Leaving it would be a stale result presented as a
current one, and a re-run that was not needed is the cheaper mistake.

The route's **identity** is the failing node's key plus that node run's attempt — the attempt that produced the
verdict and wrote the report behind it. The target's attempt is the wrong key because it *moves* while the same
rejection is still outstanding: a transient failure between the change request and the round it asked for spends
one of the target's attempts, and the next arrival back at `AwaitingApply` then looks for an id nothing wrote,
answers the rejection twice, and runs a second coder round against work a reviewer had already approved. The
failing node's key rides in the operation-id phase, so two checks that both route to one target are two routes
and get one ask each. A payload written before the attempt was part of the key keeps the id it was written
under, because changing the key of a route already in flight would ask for its round a second time.

A node's own fix loop is bounded by what the definition said — **`GRAPH-C4-4`** — and absent means **no cap**
(ruling D9): a parse-time default would silently tighten routing on every already-stored definition at run start. The row's `Attempt` is
the right base and needs no column of its own — a node with a retry target never takes the same-node path, and
each route re-attempts the whole descendant set including this node. An operator `Retry` raises the same counter
and is bounded only by the run-wide budget, so it is subtracted. The count over-attributes when two nodes route
to one target and the reset bumps both rows, which errs toward blocking — the direction every budget here errs.

Ordering, and why it is what it is:

1. **Compose the moves before anything is touched**, so an illegal move is refused while the run still stands
   where it did. The target is built **last** so the event log reads decision, then the answers being discarded,
   then the node being re-run — the order a person reconstructs the round in. Only the node that failed is
   stamped failed; the rest are being re-run because the answer they gave is about to describe something that no
   longer exists, and stamping them "failed" would say they broke.
2. **Check the run budget for the whole cascade**, not just the target's own attempt. Admitting a fan-out one
   attempt at a time is how a run spends more re-attempts than it allows by the width of its graph.
3. **Quiesce every row the route supersedes before the transaction opens.** Stopping a live session is not
   something a rollback can undo, so it cannot sit inside the write — and a lane still driving a row the reset is
   about to take would otherwise settle it back off the answer being discarded. The target is almost always
   `Succeeded` by then, but an `Any` join lets a descendant run on a sibling branch while the target is still
   working. Without this a fix loop orphans live work rather than replacing it: an agent row's re-attempt clears
   its session pointer, so the session keeps the node's one invocation slot with nothing pointing at it and the
   fresh attempt queues behind the very session it supersedes; a dev-task row leaves an attempt holding Dev
   Mode's one-active-attempt rule the same way. Sessions are **asked** to stop, never deleted — a superseded
   session ran, so it is audit evidence the event log still names. The tool lane **discards** instead, because a
   registry entry left behind would refuse the next attempt its place and leave that attempt's pass with nothing
   polling it.
4. **Re-ask the budget immediately before anything is stopped.** The first check ran before the moves were
   composed and the rows read; a human `Retry` committing since then makes the route unaffordable, and a
   transactional refusal arrives too late to give the quiesced lanes their work back. The store stays the
   authority — this only narrows the window.
5. **One transaction for the routing event and every reset under it.** A row at a time leaves a crash window in
   which the failed check is `Pending` again while the verification and gate approval beside it still read
   `Succeeded`, which nothing reconciles — startup recovery judges only rows left `Queued` or `Running` — so the
   run would repeat the check and complete on evidence about an implementation that no longer existed. All or
   nothing means a crash leaves the failure recorded, which the next sweep re-routes.
6. **On a lost race, ask exactly once more**, then stop. The lanes are already stopped when the write is
   attempted and a clash rolls it back whole; leaving it to the next sweep would leave cancelled attempts with
   nothing reset, and a cancelled `DevTask` attempt reads as a cancellation rather than a round to redo, so the
   fix loop would come back as a cancelled run. The **same** command is re-sent rather than one re-derived from
   re-read rows: every part already carries the `Any` version sentinel, so a re-read could only change the reset
   set, and a row that moved in after the snapshot is the race the first attempt runs anyway. Re-sending unchanged
   also makes the operation id do its job — a first attempt that committed and lost only its answer is a replay.
   Twice and no further: a third ask is a writer that is not going away, and the failure is still recorded for the
   next sweep.

A budget refusal that gets past **both** pre-checks means a human `Retry` committed inside the transaction's own
window. It logs at warning rather than information, because the lanes are already stopped and the refusal does
not reset them: unlike a concurrency clash, it has no next route to redo them, so the rows it names are the ones
a human has to look at.

### 8.3 Rework instead of a no-op success

When a routed failure arrives back at a task that is already approved, settling the node `Succeeded` would make
the routed re-attempt a no-op: the loop routes, re-succeeds in the same tick, and spends the target's whole
attempt budget in seconds without ever asking for a different patch. So the node asks Dev Mode for **rework**
instead, with the routed node's own validation report as the review evidence.

Whether the rejection has already been answered cannot be read off the input — the routed failure stays on the
node run's inputs for the life of the attempt and nothing clears it. It is read off the **ledger**: the change
request is written under an operation keyed on the *route*, so that operation existing **is** the record that
this rejection has had its one ask. Without it, the second round's arrival back at `AwaitingApply` would ask
again, be answered by the memoized operation, move nothing, and leave the node run `Running` for as long as the
dispatcher kept polling it.

A workflow-driven change request **does not consume a review round**. The transition bumps the round counter
only on the `InReview` hop and refuses it past the maximum, and this transition never enters review, so charging
it one would take a round away from work nothing has judged yet. (Dev Mode's own validation *does* charge one on
its failure hop, because a failed deterministic gate **is** a judgement on the round, and that budget is the only
thing bounding the rework loop; this ask judges nothing.) What it must not do is ask for a round that cannot
finish: a task that has spent them all would run a whole coder attempt and be stood down before it could reach a
review, for a reason nobody can act on. So the node stands down instead, while the reason is still legible.

**A reason with no evidence behind it is a sentence, not a verdict, and nothing may spend a coder round on it.**
Evidence is a readable validation report, or a routed payload carrying a command or test that really ran. With
neither, asking for a round is how a coder is told to redo approved work for no stated reason, and *succeeding*
instead would send the run back round the loop to re-fail the same check, spending the budget on having tried
nothing — so the node stands down where a human can read why, with the approved task untouched behind it.

The evidence quoted is the **report**, not the routed summary, which carries only counts: a coder told "1 of 4
commands failed" has been told nothing it can act on. It is correlated to the **attempt**, not merely to the node
key, because a later attempt can refuse before running anything — a missing command profile, a workspace it could
not prepare — and write no report at all, leaving the latest report for the key describing an implementation that
has since been rewritten. Quoting that would make the reason look evidenced and ask a coder to fix output nothing
just produced; refusing it drops through to the counts, which for a check that ran nothing say so.

When nothing ran there is nothing to count, and the lane authors no count sentence: a sentence like "0 of 0
commands failed, 0 tests failed" says less than the generic line while sounding like a measurement. The counts
go through the same bound and the same sanitizer as the report does, because the node key interpolated into them
comes from a stored graph definition, which is authored text like any other. A report whose shape this build
cannot read, or whose bytes the filesystem will not hand over, falls back to the counts — the blob store answers
a missing or tampered blob with a status, but a disk or permission fault still throws, and letting it escape
would fail the tick and re-throw on every sweep after it; the counts are authored from numbers, so they are
always the safe answer.

How much of the routed node's validation report a change request may carry is bounded at the same size as the
change-request reason itself and as a rule set's own body cap, and for the same reason: the prompt these
sections land in already carries the task's title, requirements and acceptance criteria uncapped, so policy gets
a bounded share of it rather than the room it would like. Past the bound the sections are truncated **visibly**,
never silently dropped.

## 9. Decomposition and materialization

A decomposing node produces a **task package** artifact, and `DevWorkflowMaterializer` expands the graph's
materialization template once per task in it.

### 9.1 The transaction

Growing a run means rewriting the run's **pinned graph** blob, never the definition. The rows and the rewrite
therefore commit **together**: a rewrite without matching rows leaves the dispatcher waiting on nodes it has no
row for, which *hangs* rather than fails.

Materialization is the last thing a tick does **before admission** (§3.1), and when it expands anything the tick
returns immediately, because everything downstream would otherwise be judging the graph the call has just
replaced. A tick that expanded nothing carries on to admission in the same call. The next tick re-parses on the
bumped revision and admits what the expansion created. The dispatcher re-reads the rows it already read only if
a decision moved one: the decomposition it is looking for has to be `Succeeded`, and a tick that settled nothing
cannot have changed which rows are.

Task creation is deliberately **not** here: a materialized child's Development task is created by the
implementation lane at first dispatch, so this transaction stays rows-and-graph and a crash between the two
leaves nothing half-created outside the run.

The expansion is keyed under a synthetic attempt rather than a real one — real attempts start at one — because a
decomposition expands **once** for the life of the run: a second decomposition after a plan revision is named
v2, and keying by attempt would quietly make the fix loop do it. The commit marker is asked for **by id** rather
than counted off the rows: it is the one answer that covers both a run that grew children and one whose
decomposition legitimately produced no work at all, and it survives the fix loop re-running the node.

The producer's own recorded route is **re-taken** against the graph the expansion writes, inside the same
transaction as the rewrite. Its route was recorded when the node settled, before the clone-root edges existed,
so left alone the persisted document lists the authored join edge and omits every root the next tick actually
admits — a recorded route disagreeing with the routing that happened. There is no gate answer to record: a node
carrying a materialization is never a `HumanGate`.

Rule sets are read once for the expansion, after the decision to expand has been made: every clone's resolution
comes off the same list, and a tick that expands nothing never touches the table. A clone inherits the
producer's project, so it resolves against the same project axis its parent did — and against its **own** node
type, which is what makes a rule set scoped to `Tool` nodes reach a materialized `Tool` clone.

### 9.2 The task-package schema and its refusals

The package is an array of `{ id, title, goal, allowedPaths[], dependsOn[], acceptanceCriteria[] }`, at the root
or under a `tasks` property — a model writing an object around its list is the commonest shape of the same
answer, and refusing it would spend a whole re-attempt on punctuation.

A JSON `null` in the array deserializes to a null **element** despite the non-nullable annotation, and every
reader below dereferences the entry. It is refused at the **parse boundary** rather than in one of them, because
that is the one place that makes the list element-non-null by construction: reaching any reader with the hole
throws out of the tick, and a tick that throws over a decomposition that has already **succeeded** re-throws on
every tick after it. That is the wedge this module refuses to have; a stand-down is a refusal the node can be
told about instead.

A package the materializer cannot use stands the decomposition down **through the ordinary retry policy**, so
the node's first answer to malformed output is another attempt carrying the schema error in its objective — the
cheapest correction loop available, since the thing that wrote the document is the thing that can fix it — and
the answer when that is spent is a human. All refusals are answered the same way, so what matters is that the
sentence is specific enough for a model (and a human reading the same field) to act on.

The refusals:

| Refusal | Why |
| --- | --- |
| A task that names no `changes`, when the template carries a `DevTask` | There a task becomes a Development coder attempt, and that attempt cannot finish without exporting a **non-empty** patch: a slice with nothing to change — a survey, a style profile, a verification — is refused, re-attempted, refused again and then blocks the run in front of a human. `changes` is the one signal the package carries that there *is* something to change, so a package naming none is handed straight back while it is still cheap to fix. A template with no `DevTask` keeps the older contract: its clones are ordinary sessions with no patch to export. |
| An `allowedPaths` entry that is absolute, climbs out of the workspace, or sits under protected Git state | The workspace confinement the coder's own tools enforce, asked here instead: those are files the coder would be refused for touching, so the decomposition is told now rather than three attempts later. |
| A package that **depends** on `allowedPaths` for isolation | It is not enforced anywhere: the child brief carries the title, the requirements and the acceptance criteria, and Dev Mode's workspace policy has no per-task path restriction to hand it to. A decomposition leaning on it for parallel-child isolation would get none, silently, so it is refused loudly instead. The field stays in the schema and on the stored artifact — this refuses a package that depends on it, not one that mentions it. |
| A clone key that collides | `"{nodeKey}#{taskId}"` is not injective — a template carrying both `a` and `a#b` generates `a#b#c` for task `b#c` and again for task `c` — and the store's unique `(run_id, node_key)` answers that with a refused insert, which throws out of the tick instead of standing the decomposition down. Every clone key the package *would* take is therefore checked as the loop goes, against **every** node of the subtree rather than just its root: a graph that happens to declare a node named like one of the clones collides just as hard. |

The task-package read is **deliberately not attempt-scoped**, and this is the one place that reading is right.
Artifacts are run-scoped, so a re-attempt that saved nothing leaves the first attempt's package the newest — and
judging the second attempt on it is correct here, because the package **is** the node's output: an attempt that
produced no new one has not corrected anything, and the second refusal is what stands the node down for a human.
(A node *panel* must take the opposite reading for the same reason: there the question is "what did this attempt
do", here it is "is there a usable package on this run yet".)

A stand-down does two things to a row that had **succeeded**, both deliberate: its end instant is the success's
and is left alone — the agent really did finish then, and re-stamping it would date the node's work to the
moment its output was judged — and its output document is **replaced** by the refusal, losing the document the
node panel had been rendering. That is accepted, because the panel's job is to explain the row's current state,
that state is blocked, and the reason it is blocked is the more useful of the two answers. The promoted artifact
still holds what the node actually produced.

### 9.3 The clones, the join, and the zero-task case

Each clone is the template node's **own JSON** with its key — and any retry target — rewritten. The rewrite
works on the stored JSON rather than on the parsed projection, because the projection keeps only what the
runtime routes on and re-serialising it would silently drop every authoring field the editor put there.

Whether the template carries a `DevTask` **anywhere** decides whether a task has to name the files it changes:
that is the node type whose clone becomes a coder attempt, so the contract is the attempt's (§6.4). The whole
subtree rather than its root, a custom template being free to root itself in an `Agent` that briefs a `DevTask`
below it, and it is read once, because it cannot differ between tasks of one package.

The join edge is computed from the template's **own** edges: a leaf of the template is what the join is actually
waiting for, so a template that already names the join keeps that edge and one that names nothing gets it —
either way the join waits for every task's last node rather than firing while they run.

**The decomposition's own edge into the join is kept.** Removing it looks safe — a join left waiting on an
already-`Succeeded` node would seem to fire the moment the decomposition landed — but admission does not do
that: `All` waits while any inbound edge is `Pending`, and so does `Any`, so the clones' fresh edges hold the
join exactly as they did before. What removing it *does* do is take the decomposition off every path back from
the join, and upstream artifact resolution walks those paths: the node behind the join is then left inheriting
the clones' validation reports and nothing else, so a verification agent judges the feature without the task
package it was decomposed into.

An `Any` join under a materialization is left to the author: one task expanding into an `Any` join gives it a
single live inbound edge, which parse refuses — the run then **fails as unroutable** with that sentence rather
than hanging, and the shipped templates all join with `All`. The upgrade path, if a template ever wants it, is
to relax the two-edge rule for a join a materialization names, since its real width is only known once the
package is read.

**A decomposition that legitimately produces no tasks** (ruling D12) leaves the graph exactly as it is — the join keeps its
edge from this node and fires on it — and writes the commit marker plus **one already-succeeded row per
validation node in the template subtree**. Without those rows an apply downstream would be blocked by the
runtime half of `GRAPH-C4-3` (§3.6): it asks whether a `Tool`/`Validate` node succeeded on the path this run
took, an unmaterialized template key has no row at all, and "there was nothing to validate" would read as
"nothing validated it". One row per such node rather than an arbitrary pick, so a template carrying two checks
shows both as not-applicable rather than one as missing. A subtree with **no** validation node writes no row at
all — there is nothing to stand for, and the apply's proof is carried by another branch whose validation has a
real row.

Such a row can never make a run look finished, because **a template's validation node must have an out-edge** —
`GRAPH-C4-1`'s fourth step — and the parser refuses one that has not. `TerminalNodeKeys` is every node with no out-edge, template keys included,
which is normally moot since a template never gets a node run; the zero-task row is the exception that falsifies
that premise, a template leaf taking a `Succeeded` row at a key the completion predicate reads, so the run would
report `Completed` though its real tail never ran. Refusing the shape at save is cheaper than teaching two runtime
rules about each other. The rule is scoped to the nodes that row is written **for**, so an edge-less `DevTask` or
`Agent` template stays rowless and can neither satisfy nor block the predicate; widening it to every node type
would refuse shapes that are legal and harmless, the baseline decomposition template among them. "Has an out-edge"
implies "reaches the join", the subtree pulling every non-join edge target back into itself — an argument leaning
on acyclicity, which this check runs *before* the cycle check proves. Nothing unsound follows: a cyclic graph is
refused either way and reads this complaint rather than the cycle one.

That row's document says `status: succeeded`, which is the routing vocabulary's own token, so a conditional
out-edge on the template's validation node fires exactly as a real pass would; a separate `verdict` member is
what keeps a reader from taking it for one. The rows and the marker go under the **same** operation id the
marker would have taken, so the replay guard is unchanged and there is no window in which one exists without the
other, and the marker's event names a node materialization rather than a graph change — the honest token, since
no graph changed and a consumer that refetched on a graph token alone would fetch the same revision back.

## 10. Restart recovery

`DevWorkflowStartupReconciler` runs at boot, registered **after** the work-session reconciler and **before** the
dispatcher, and both halves of that matter: a node run that resumes must not find its session still holding a
half-written turn, and the dispatcher must not start admitting rows this has not judged yet.

It deliberately touches **no run row**. Runs auto-resume: a workflow run legitimately spans days, and requiring
an operator to restart every one of them after an engine restart would defeat the durability the feature exists
to provide.

**Exactly once survives a crash during recovery**, because the collapse and every verdict that follows from it
commit together: the reconciler reads the interrupted rows, decides what each one costs, and hands those
decisions to the store to apply inside the one transaction that collapses them. A host that dies before that
commit leaves the rows as it found them and the next boot judges them again from the same evidence; one that
dies after it finds nothing left to judge. Neither can spend a second attempt on one interruption.

### 10.1 The pass loop

Read, decide, write once — and only the rows that are still what they were when they were judged. A pass that
finds rows it cannot judge (a second process moved one, or stranded one after the read) leaves those alone and
goes round again, because collapsing an unjudged row would strand it at `Pending` with nothing left to decide
what re-running it costs.

The **last** pass settles what it could not judge instead of walking away from it, because nothing downstream
picks those rows up: the dispatcher admits `Pending` rows and follows `Running` *agent* ones, so a stranded
`Tool` row left behind wedges its run for good — "the next boot" is not something anybody schedules. That
settlement is decided against the live row inside the transaction, so the drift that caused it cannot reach it.

Two counters bound the loop, and they are different on purpose:

- **Passes** — how many times recovery re-reads and re-judges before the last pass settles whatever is left.
  Bounded rather than open-ended: a writer that keeps moving these rows is one this cannot outrace, and a
  startup that spins on it never reaches the dispatcher.
- **Attempts** — how many times the loop may run in total, counting the passes a concurrent operator `Retry`
  refused. A refused pass writes nothing at all — the collapse rolls back whole — so counting it as a *pass*
  would spend one on a judgement that was never applied, and a refusal landing on the **last** pass would throw
  the settling pass away with it, leaving rows `Queued`/`Running`, which is precisely the pair of states nothing
  downstream recovers. The attempt counter is a cap rather than an unbounded retry because a writer that refuses
  every pass is one this cannot outrace either.

When the attempt cap stops the loop, every run whose rows are still `Queued` or `Running` is **named in the
log** rather than left to be found by whoever notices the run has stopped moving. Rows stranded *after* the
settling pass looked are left alone: they are that writer's rows, whatever is writing them is running, and
blocking another writer's live work would be the worse mistake.

### 10.2 What re-running an interrupted row costs

The store collapses every stranded row to `Pending` without touching `Attempt`, which is right for the two cases
that dominate — an agent whose session survives the restart on its own checkpoint, and a queued row that was
never dispatched. Neither is a failure, so neither is an attempt. What the store cannot know is what the
reconciler decides: whether re-running each interrupted node run costs an attempt, whether it can be re-run at
all, and whether its run has any re-attempts left to spend on it.

- A **sandbox** row costs a real attempt: the process is gone and its workspace may be half-prepared, so the
  re-run is a genuine second attempt and has to count against the node's budget.
- A row already **at its own cap** is not a candidate for the run's budget at all — recovery increments the
  attempt of every row it admits, and this one has no attempt left to be given. It is blocked with its **own**
  reason, so nobody reads it as the run-wide budget. (The live path refuses the same row before every automatic
  re-attempt; recovery would otherwise bypass that check and reset a 3-of-3 row to `Pending` at 4.)
- A row whose **session was deleted** out from under the run goes to a human with the reason on the row:
  nothing can resume it, and a retry would only create a second session for work whose transcript is gone.

The run's budget counts its **re**-attempts — the sum of `Attempt − 1` over its node runs — so a graph that has
merely started has spent none of it however many nodes it declares. Restating that sum anywhere else is how
recovery came to hand an interrupted row a slot a recorded-but-unapplied `Retry` had already reserved; there is
**one** definition with three callers — this policy, the reconciler, and, in its own SQL, the store's
transactional admission.

Recovery therefore keeps **two counts** (`FU3-4` race B). `spent` is what the run has actually made, which the
trailing sweep decides on. `promised` adds the reservations: a `Retry` recorded before the crash and never
applied has spent no attempt a sum over `Attempt` can see, but the dispatcher turns it into one on its first
tick. **Only the
admission decision uses `promised`** — widening the trailing sweep with it would block rows that cost nothing,
so one unapplied `Retry` sitting on the last slot would send every interrupted agent row to a human at boot for
a slot none of them wanted.

What is left is handed out to the interrupted sandbox node runs in **node-key order**, so the same boot always
admits the same rows. Several interrupted siblings each taking one would spend more than the run allows by the
width of its fan-out, which is precisely the restart loop this budget exists to stop. The ones the budget does
not reach are blocked **without** an attempt: an attempt recorded there would be one the run never had, and the
row would read as having tried again when it never did. A row already at its **own** cap is no candidate either
(`FU3-4`): recovery increments the attempt of every row it admits, and that one has none left to give, so it is
blocked with its own reason rather than the run-wide budget's. The per-run node-run cap is deliberately **not**
re-checked: it is enforced where the rows are created, and a run that somehow held more than it allows cannot be
repaired by blocking every node run it has — that would turn a bounded accounting error into a dead run.

The affordability check is a check-then-write like every other one, since the run service can be recording a
human `Retry` while the verdicts are being composed. The budget rides on the command so the collapse admits it
under the writer lock, and a refusal rolls the whole pass back for the loop to re-judge from the decision it did
not see.

### 10.3 The orphan sweep

The last step deletes **`Draft`** work-session rows no node run references. It runs unconditionally, unlike
everything above it: an orphan is a session no node run points at, so there is no reconciled row that could lead
to one. Each is named in the log, one line: a session lost this way is a crash that happened, and deleting it
silently would erase the evidence along with the row.

A `Draft` orphan is precisely what a host death between the session create and the attach leaves behind, and it
is unreachable by design — the owner surface refuses workflow-kind sessions to every external caller, a
work-item delete can only release what its node runs point at, and the next tick creates a fresh session rather
than finding this one. Never started, it holds no transcript to lose.

**`Draft` is load-bearing rather than tidiness.** A re-attempt clears the session pointer so the fresh attempt
cannot resume a poisoned context, which leaves the *previous* attempt's session owned by nothing as well — but
that one **ran**, and its transcript is the evidence of an attempt the event log keeps only the id of.
Auditability is this module's pillar, so a driven session is never swept. A work-item delete interrupted before
it released its sessions therefore leaves recoverable orphans rather than being mopped up here: rare,
kind-scoped, and deletable through the owner surface.

The sweep is **startup only**, and two queries — every workflow-kind session, and the ids node runs own. It is
deliberately not a per-tick sweep: a session created a millisecond ago and not yet attached is
indistinguishable from an orphan, and a sweep running concurrently with the executor would delete live work.

## 11. Artifacts

A run's artifacts are its audit and outlive the sessions that produced them. Promotion is an application-layer
composition rather than a store method, because it spans three things no store can reach at once: the work
session's bytes, the run's blob store, and the run's artifact rows. The session is execution scratch and can be
deleted with its node run, so the bytes are **copied** rather than referenced.

It is **idempotent by construction**: both the artifact id and the append's operation id are derived from
`(run, node key, attempt, artifact name)`, so a promotion replayed after a crash rewrites the same blob and the
store's query-first check returns the recorded result instead of appending a second version.

The work session has four artifact kinds; a run has ten. `Patch` maps exactly. `Report` is the session's word
for "the structured result of this work", so it — and only it — takes the **node's declared kind** when the node
declares one: that is how `TaskPackage`, `Plan` and `Specification` become reachable at all, since the session
enum has no member for any of them and inferring one from the bytes would be guessing. `Note` and `File` are the
session's scratch, and a node's declared output is not what they are. A decomposing node's declaration is what
the promotion needs: without it, the document the node's whole purpose is to hand downstream lands as an
ordinary report and the materialization finds nothing.

**Blob bytes** live under `{node data root}/dev-workflows/artifacts/{runId:N}/{artifactId:N}.blob`, encrypted
under the node key with the run and artifact ids bound into the AAD. Only the digest and size cross between the
blob and the row. Callers write the **blob before the row**: a crash between the two leaks one bounded blob,
where the other order would leave a row pointing at bytes that never existed.

## 12. Events, telemetry and reads

### 12.1 The event feed

An event push carries the allocated `sequence`, so a subscriber that replays from the watermark can never miss
the row it names. The payload is **content-free by design**: a dropped push degrades to a late read rather than
to a wrong render, because the database is the only replay authority.

Sequences are strictly increasing but **not contiguous** — the run's counter is shared with node runs and
artifacts. A stored route document is read defensively and never yields a null list, because the generated
client accepts a missing member and rejects a null one. That parsing lives beside the writers rather than in the
read model: whether an unparseable column costs one field or the whole read a 500 is a judgement about the
document, and the catch it needs is what the endpoint layer must not carry.

### 12.2 The dispatcher signal

The dispatcher listens for change signals as well as sweeping. The signal is **latency only**: a dropped signal
costs at most one sweep interval and never correctness, which is what lets the channel behind it drop writes
rather than block a committing caller.

### 12.3 Node telemetry

Telemetry is collected when a node run terminalizes, and **only from what is already persisted**. That is the
whole constraint: a node run terminalizes on a dispatcher tick, in a different DI scope from the work session
that did the work and possibly in a different process after a restart, so nothing the run held in memory is
reachable. The work session's provider-call budget in particular is gone, because its cap scope is disposed when
the step that seeded it ends.

**Metadata only, under the trajectory policy**: counts, ids, a served model name and tool **names**. No prompt,
no tool argument, no tool result, no transcript.

A collection deadline bounds the **caller's wait**, not the collection behind it. A separate flat ceiling bounds
how many collections may be in flight at once for one application: without it, a collector that blocks — or one
that never terminates — keeps a thread-pool worker and a service scope for as long as the process runs, one per
settle, and a wide retry route multiplies that by the graph's width. A settle that finds every slot taken goes
ahead **unmeasured**, which is exactly the trade the deadline already makes.

The ceiling is a container **singleton**: the store around it is scoped, so a per-instance pool would bound
nothing, and a static one would be shared by every application a single process stands up.

The collector itself is registered **unconditionally**, beside the workflow store every node-run transition goes
through, while the whole Development module — `IDevelopmentStore` included — is registered only when
`Development:Enabled` is true. The development store is therefore an *optional* dependency: a required one would
fail to resolve `IDevWorkflowStore` itself on a node with Development Mode switched off. The container fills a
defaulted parameter with null when the service is absent, which is the answer the DevTask lane gives for the same
reason, and the node run simply reports no DevTask cost. The local-model-load telemetry is optional for the same
reason — it belongs to the model-fit module, which a host composing only the workflow stack does not add — and
absent it the VRAM columns stay null, which is what they say anyway for every model the node did not load itself.

There are three shapes, and the node run decides which: a work session takes the **agent** path, a development
task the **DevTask** path, and a node run with neither — `Tool`, `Gate`, `Parallel`, `Join`, and every `Skipped`
row — has no cost rows to read and answers nothing. The agent path reads two things that do not substitute for
each other: the session's own step rows for what the loop spent, and the conversation's chat-run envelopes for
what the provider reported. The step rows exist even when no envelope was ever written, and only the envelopes
carry real provider tokens. The DevTask path counts the task's attempts, coder and reviewer alike, successful and
failed alike, windowed on the node run's own start — required rather than tidy, since a re-attempt keeps the same
development task and without the window a third attempt would re-count the first two attempts' tokens.

The collection hangs off the store **decorator** that announces every committed mutation, not off the fifteen call
sites in the runtime: a missed call site would be a pane that silently stops updating and a cost total that
silently under-reports, with no test that would notice, so wrapping the one interface they all go through makes
both impossible to forget, including from code written later. The change kind a push carries comes from the
**command** rather than from the event row, because a caller that transitions a node run into a human wait is
asking for a person, which is the one push with a consequence beyond re-rendering.

The gate on enrichment is the target **status**, not the caller: a terminal, `Blocked` or `WaitingForApproval` move
is where an attempt's spend stops changing. Those two live statuses are in the set deliberately — they are where
the most expensive node runs land, retry-exhausted, budget-exhausted, resume-exhausted, session-less, and gating on
terminality alone would leave every abandoned node run reporting nothing. The whole enrichment is contained: any
throw, any timeout, and the original command goes through. It runs before the inner store call and never inside its
transaction, so a crash mid-collect loses a measurement and nothing else.

The deadline is enforced by **abandoning the wait**, not by asking the collection to stop. Cancellation is
cooperative, so a collector that never observes its token — or a database call that does not — would otherwise hold
a terminal transition or a retry route open forever, which is a workflow that never settles rather than a
measurement that is missing. The waiter gives the thread back when the deadline fires and leaves the collection to
finish into a watcher that logs its completion once, at warning, with counts only, and drops whatever it answers.
An expired deadline is swallowed and the command goes through unenriched; a cancelled *caller* is rethrown. A
**spent** deadline schedules nothing at all: a route's remaining resets are forwarded unenriched rather than
starting work behind a boundary the caller has stopped watching. It bounds the whole **ask**, not one command — a
retry route enriches every reset it carries under one deadline, the graph's width deciding how many resets there
are, and a per-command budget would multiply by it.

Admission to the flat ceiling happens **before** scheduling, because the deadline bounds the wait and not the work
behind it; no slot free means no collection at all, the same trade an expired deadline makes. The slot comes back
when the **collector** terminates, not when the caller stops waiting, since releasing it on the abandoned wait
would let the next settle start work beside the stuck one. The collection starts on a pool thread so the boundary
holds even against a collector that blocks before its first await, at the cost of a reset being offered to the
collector eventually rather than synchronously.

Each collection reads on a **service scope it owns and disposes**. That isolation is the point: the work can
outlive the settle that started it, and the settle's next act is to write through the inner store, so a collection
reading on the mutation's own `DbContext` would be a second concurrent operation on it — a hard failure rather than
a lost measurement. Everything on that path reads; the only write is the enriched command it returns, and a late
return is dropped. The scope's own store is this same decorator around a fresh inner store, whose read methods
forward untouched; asking for the concrete inner store instead would bind the decorator to a registration rather
than to the interface it already depends on.

A route is computed from the **pre-write** row with the command's own target status and output projected onto it
first: asked of the row as it stands, every edge would answer `Pending` — the row still reads `Running`, carrying
the previous attempt's output — and the route document would be empty in every real run. That row is read for four
things only, the work session, the development task, the attempt's start and the node type, its status and output
being the previous attempt's. The run's other rows come along too, because whether a skip was waived is a walk back
over the graph rather than something one row carries, and without them a waived skip's out-edges would record as
dead. A non-terminal move gets a null route, which is honest where an empty document would not be.

A **cross-node** route is one announcement, because it is one commit: its watermark names the routing event and
every reset the same transaction wrote sits after it, so a subscriber replaying from there still sees them. Each
reset is enriched exactly as a same-node re-attempt is — this is the other write path into the node-run transition,
and a cross-node retry that skipped it would lose an attempt from every cost total — and the enrichment is
re-derived on every ask and never cached, because the fix loop re-sends the same command object after a lost
concurrency race.

The failing attempt's cost is captured onto the **retry event** before the reset that empties the row. The node-run
row keeps the last attempt only, so without that a node that failed twice and succeeded on the third try would
report one attempt of three, and every total that summed the rows would silently under-report. The event log is
where per-attempt history already lives and the retry event's own detail is the place the catalog names for it, so
no event type is added and the retry policy — a singleton, which cannot hold a scoped collector — is not touched at
all. The pre-write row is also the last place the failing attempt's work session still exists, the command clearing
it downstream inside the store's own transition. The members merged in are the telemetry record's own, minus the
five that cannot be added up: a route belongs to one settle, a served model is a name rather than a quantity, a set
of tool names does not sum, and the two VRAM figures are a *reading* of the box at one load rather than a quantity
the attempt spent, so adding two attempts' free-VRAM bytes would produce a number that describes nothing. A column
added to that record later therefore rides along automatically, or it is not additive; nothing enumerates them by
hand.

### 12.4 Operation ids

A write whose command demands idempotency carries an operation id with the **phase encoded inside it**, so the
store's two-column `(run_id, operation_id)` idempotency holds one row per phase with no phase column, and a tick
replayed after a crash short-circuits on the query-first path instead of appending a second event.

Only writes whose command demands an operation id use one. An ordinary status move does not: it passes the `Any`
version sentinel and no operation id, because it has no lost update to protect against and a replayed tick
re-derives the same answer from rows that did not change.

### 12.5 The application service seams

An endpoint is the HTTP edge and **may not take a persistence store itself**, so two read/authoring services
stand between them, adding no policy of their own: validation, size refusals, filter parsing and ownership
checks stay in the endpoints, and the store keeps deciding not-found, version conflicts and null fields.
Everything that **moves** a run — start, pause, resume, cancel, the human decision, the work-item delete —
lives on `IDevWorkflowRunService` instead, which is where the fire-and-forget and operation-id discipline
belong. The runtime drives the store directly for what no endpoint asks for.

**Replay is resolved before legality** by every lifecycle verb. A command that committed and whose answer the
client never saw is retried against a run the dispatcher has meanwhile advanced — a cancel that has since drained
to `Cancelled`, a resume whose run is now `Running` — and judging that retry against the status its own first
attempt produced would answer a conflict to a caller that did exactly the right thing. It is the replay of *that*
verb or it is not a replay at all: an operation id names one act, so the same id arriving on a different verb is a
caller bug, and answering it with the run would report a cancel as done while the run carried on. Lifecycle
commands are written against the `Any` version sentinel deliberately — an operator's intent must win over a status
move the dispatcher decided a moment earlier, the version check existing to stop the reverse.

**Deleting a work item** writes its rows first and does everything external afterwards. Past that commit the
request's cancellation token is deliberately dropped and each step is best-effort per item: the rows that named
those sessions and bytes have already committed, so cancelling undoes nothing and only stops the cleanup partway,
while throwing would report a failure for a delete that in fact succeeded and let one refused session cost the
sessions after it and every artifact directory behind them. What a failure leaves is a session or a directory
nothing points at — never a dangling reference, removable by hand through the owner surface — and the startup
sweep will not collect it later, taking only never-driven sessions.

`DevWorkflowGraphContract` is the third seam: the API layer's questions about a stored graph are answered by the
**same** parser, condition evaluator and transition table the dispatcher routes with. The parsed graph stays
internal — it is the runtime's projection, not a wire shape, and the API composes its own field-for-field mirror
from the JSON. What must not be duplicated is the *judgement*: whether a graph is routable, which answers a node
run can take, where a rejection would go, which nodes are template subtrees, what each node can change, and what
cap each declared. A second implementation of any of those drifts from the one that actually decides — an editor
computing its own effect badges would show badges that disagree with the 400 the operator gets.

Its read paths answer **empty** for a graph that cannot be parsed rather than throwing, and the waiver question
answers **null** — unknown, not "none". A run whose pinned graph is unroutable is exactly the run an operator
most needs to open: it has already been failed with the reason on the row, and refusing to render it would hide
that reason behind a 500. A missing key means the reader has nothing to compare against, not that the value is
zero. Save-time validation is the exception and throws, because a save is where a refusal belongs.

A **policy** refusal is deliberately a different exception from a validation one, because the lane maps the two
to different failure classes: `Configuration` names something an author must fix, and a policy refusal named
that way would send an operator looking for a mistake in a definition that has none. How the API layer maps
each is [API & Hubs](09-api-and-hubs.md)'s.

## 13. Limits and options

`DevWorkflowOptions`, bound from the `DevWorkflows` configuration section. `Enabled` gates **behaviour, never
registration** — the same posture work sessions hold: a disabled node has to answer legibly rather than 500 out
of an empty container.

| Option | Default | What it bounds |
| --- | --- | --- |
| `Enabled` | `false` | The whole feature's behaviour. |
| `MaxNodesPerDefinition` | 500 | One definition's nodes, at validation rather than at run. Higher than the graph-workflow cap because a development workflow decomposes: one materialization expands a template subtree many times, so a definition carries shapes a hand-drawn graph does not. |
| `MaxArtifactBytes` | 1 MiB | One artifact's bytes, enforced by the blob store. |
| `SweepSeconds` | 5 | How often the dispatcher sweeps every live run, independently of the change signals. |
| `MaxParallelToolNodes` | 2 | `Tool` and `DevTask` node runs holding a sandbox at once (§7). |
| `MaxSessionResumesPerNodeRun` | 4 | How often one node run may resume its parked work session (§5.2). |
| `MaxNodeRunsPerRun` | 200 | The guard against a runaway decomposition, checked at materialization and again at startup recovery. |
| `MaxTotalAttempts` | 50 | The guard against a retry storm, and the outer bound on the cross-node fix loop: without it a definition whose validation node re-attempts its implementation node could oscillate indefinitely. |
| `MaxConcurrentRuns` | 4 | Live runs. Runs above the cap **wait**; they are not refused. |
| `McpDefaultListLimit` / `McpMaxListLimit` | 20 / 50 | The read-only MCP run listing. Bounded here for the same reason the agent-run listing is: an MCP client truncates oversized tool output on its own and silently, so the server decides how much is enough. |

## Related pages

- [Workflow Engines Divergence Register](22-workflow-engines-divergence-register.md) — every difference between
  this engine and Graph Workflows, and which are deliberate.
- [Graph Workflows](21-graph-workflows.md) — the operator-authored DAG engine beside this one.
- [API & Hubs](09-api-and-hubs.md) — the `DevelopmentWorkflows` route family, `DevWorkflowRunHub`, and the four
  structural graph rules as the validator enforces them at save.
- [Agent Mode](04-agent-mode.md) — the work sessions, tool registries and agent definitions this runtime drives.
- [React Client](10-react-client.md) — the Development Workflows SPA feature area.
- [Security & Privacy](12-security-and-privacy.md) — the Development Mode execution boundary an implementation
  node runs behind.
