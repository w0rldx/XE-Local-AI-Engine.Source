# ADR 0023: Dev Workflows converge into Graph Workflows

- **Status:** Accepted — operator decisions of 2026-11-10 (converge; questions Q1 to Q7 below, all seven
  recommendations) and 2026-11-12 (rc.3 is cut after Dev Workflows is retired). All steps of decision 11 are in
  place: the freeze, the graph primitives, the samples and live round, the retirement and the docs sweep.
- **Date:** 2026-11-12
- **Scope:** Which engine runs AI-assisted development work as a graph, what Graph Workflows gains to run it, what of
  Dev Workflows is dropped rather than ported, and the order in which Dev Workflows is retired. It changes nothing in
  Development Mode's own task machine or its apply gate.
- **Authority:** Operator decisions of 2026-11-10 and 2026-11-12. No ADR covered Dev Workflows before; its earlier
  rulings were plan rulings recorded in wiki 25 (deleted in the docs sweep) and the
  [divergence register](../wiki/22-workflow-engines-divergence-register.md).
- **Supersedes:** the 2026-09-09 ruling that convergence is deferred (register, introduction), and the register §2
  row "`DevTask` node kind": Graph Workflows gains a `DevTask` kind that re-calls Development Mode's apply path
  instead of porting it.

## Context

Two workflow engines ship. Graph Workflows (`Services/GraphWorkflows/`, [wiki 21](../wiki/21-graph-workflows.md)) is
the general operator-authored DAG engine and is on by default. Dev Workflows (`Services/DevWorkflows/`, wiki 25) was
the copy-adapted original, kept for development work, and ships off behind the `DevWorkflowsEnabled` node setting.
They share no code by ruling, so every fix to one is a decision about the other; the divergence register exists only
to track that, and it has grown to 19 compared aspects, 10 deliberate drops, 6 unintentional gaps and 6 pieces shared
across the boundary by separate rulings. Dev Workflows alone carries a route family, a hub, nine tables, a Workflow kind of work session and
its own options.

A design pass against the code found three facts that decide the shape:

- **Dev Workflows does not run the coder.** `DevWorkflowDevTaskExecutor` drives a Development Mode task through Dev
  Mode's own coder, deterministic validation and independent review until it is `AwaitingApply`, and the apply node
  calls `IDevelopmentManagementService.ApplyAsync` on the run's behalf. The hash-locked apply gate lives in
  `Services/Development` (`TrustedDevelopmentHostApplyPort`, `DevelopmentManagementService.EnsureApplyAuthorityAsync`).
  Converging moves an orchestrator; the gate stays where it is and is called the same way.
- **A run can land one task.** `TrustedDevelopmentHostApplyPort` applies only on the approved base branch at exactly
  the base commit with a clean tree, and it stages without committing. After the first task of a fan-out lands, the
  tree is dirty and every later task is refused. Decomposition fans out work the gate cannot integrate.
- **The landed tree is the judged tree.** Apply requires the task's `ExpectedResultHash`, so the tree that lands is
  the one Dev Mode validated and reviewed; a post-integration validation pass re-checks a pinned result.

## Decision

1. **Graph Workflows is the one workflow engine.** Development work becomes a graph like any other; Dev Workflows is
   retired in the steps of decision 11. It is frozen now: bug fixes only, no new features, no parity work.
2. **One new node kind, `DevTask`, with two actions.** `implement` creates or re-uses one Development Mode task in the
   run's project and waits until it is `AwaitingApply` (or has stood down); `apply` names an `implement` node and calls
   `IDevelopmentManagementService.ApplyAsync` on the run's behalf, once per attempt. Graph owns no workspace: Dev Mode
   keeps the attempt worktree and the registered repository. A run that holds a `DevTask` or a repository-reading Agent
   node binds its project through the `developmentProjectId` key of its Start input, the kind is refused when Development Mode is off, and it is refused inside a `Chat` graph.
3. **The apply gate is re-called, never ported or weakened.** Apply goes only through `ApplyAsync`, so the patch and
   manifest digests, approved-subject hash, base branch and commit, clean-tree and `ExpectedResultHash` checks are
   untouched. A new parser invariant (`DEVTASK-1`) requires every inbound edge of an `apply` node to come from a
   `Pause` node's `Approve` decision, with the named `implement` node a strict ancestor of that `Pause`, at save and at
   run start. `EnsureApplyAuthorityAsync` consults the Graph store for a live run driving the task; while both engines
   exist it consults both stores. Unattended runs (scheduler, integrations) cannot pass a `Pause`, so they cannot apply.
   The Graph check fails closed with Graph Workflows switched off: a live run cannot end then, so its task stays
   unapplicable and the refusal tells the operator to turn Graph Workflows back on to finish or cancel that run.
4. **Q1, decomposition is dropped.** No task-package materialization and no lane-internal fan-out: one `implement`
   node drives one task, and an author who wants two tasks draws two `implement` nodes, two `Pause` nodes and two
   `apply` nodes.
5. **Q2, the Development Mode reviewer is the verifier.** No separate verify agent: the request-first clause moves into
   the `implement` node's instructions, which the Dev Mode reviewer reads.
6. **Q3, rule sets are dropped.** The rule-set table, scopes and UI go; a `DevTask` node's own instructions reach Dev
   Mode through the existing `IDevelopmentStore.RecordWorkflowPolicyAsync` seam.
7. **Q4, `Blocked` with Retry, Skip and Abandon is dropped.** A failed `implement` fails the run. The Development task
   survives; once the run is terminal it is applied from the Development page if it reached `AwaitingApply`, and a task
   that stood down on a missing validation runner is retried there ("Retry validation"). A task that stood down on its
   round cap or on exhausted recovery is not continued: the operator starts a new run, which creates a new task.
8. **Q5, the inbound MCP workflow tools are re-pointed.** `list_workflow_runs` and `get_workflow_run` keep their names
   and read Graph Workflow runs; their payload changes, a breaking change made before 1.0.
9. **Q6, an Agent node may read the bound repository unattended.** A `repository` option gives an Agent node the
   read-only repository tools over the run's project. On a cloud model it passes the same `CloudLocalDataGate` grant
   check as any cloud Agent node and is refused at start without it. The Agent node stays one `IInvocationRunner` turn;
   no Work-Session-backed Agent node is built.
10. **Q7, stored Dev Workflow data is dropped without export.** The retirement step drops the Dev Workflow tables, their
    artifact blobs and the Workflow-kind work sessions. Development tasks a run created are project tasks and stay.
11. **Order of work.** Each step ends gate-green and shippable.

    | Step | Content | Gate to the next |
    |---|---|---|
    | Freeze | This ADR; freeze banners on wiki 25 and the register | none |
    | Graph primitives | the `DevTask` kind and lane, `DEVTASK-1`, the Start `developmentProjectId` binding, the Agent `repository` option, `DevTask` resume on restart, a `development_task_id` on graph node runs, the two-store authority guard, lint, designer, React node and panels, regenerated client | deliberate-break proof that an ungated or Reject-routed `apply` is refused at save and start |
    | Samples and live round | Graph samples reproducing the intake template (classify; question or unclear ends; feature or bug goes plan, `Pause`, implement, `Pause`, apply) and research-plan-approval; a live round on both model tiers | **go/no-go**: the operator confirms before anything is deleted |
    | Retirement | Retire Dev Workflows: module, routes, hub, tables (drop migration), setting and options, Workflow-kind work sessions, SPA feature; MCP re-point; the guard reads the Graph store only | operator confirmation of the live-round result |
    | Docs sweep | wiki 25 deleted, the register shrunk to a retired-engine note, the remaining pages swept | none |

    The live round runs the scenarios of the earlier Dev Workflow rounds (no tests, a bug with tests, a feature, an
    unclear request) plus a Reject at the integration `Pause` and a host restart during `implement`. If the small tier
    cannot classify or plan in one turn, decision 9 is revisited before the retirement.
12. **rc.3 is cut after the retirement**, so it ships without Dev Workflows.

## Consequences

- One engine to fix. The register stops growing; after the docs sweep it holds only the retired-engine note and the §6 addendum,
  which concerns Development Mode, not this pair.
- About 24k lines of production C#, 16.7k of React and 27k of tests leave in the retirement, with 27 operations and one hub; the
  additions are one node kind, one lane, one parser rule, one column and one Agent option.
- Dev workflows gain what only Graph has: scheduling, integration targets, `DecisionModel`, web `Tool` nodes, actions
  and the designer. A scheduled dev graph still stops at its `Pause` before any apply.
- Lost on purpose: decomposition, the cross-node fix loop, post-integration full validation, `RequestChanges`, Skip
  and waived edges, run-level pause, rule sets and artifact promotion. A run lands at most one task per `apply` node,
  as the gate already allowed.
- A cloud `repository` Agent node admitted by the local-data grant reads raw repository files through its tools, unlike
  a Dev Mode cloud coder attempt, which gets a curated context (`DevelopmentCloudContextBuilder`); the grant is the consent.
- A restart during `implement` costs nothing, because the lane re-polls a durable Dev Mode task by its stored id; a
  restart during a research Agent turn costs one attempt, the existing Graph behaviour.
- Breaking for users of Dev Workflows: definitions, runs, work items and rule sets are not migrated, and the two MCP
  tools change payload. Acceptable before 1.0; the retirement records it in `CHANGELOG.md`.
- Residual risk is quality, not safety: one-turn Agent nodes may plan worse than multi-step work sessions on the small
  model tier, which is what the live-round go/no-go measures. The safety risk sits in the two-store guard and `DEVTASK-1`,
  each proven by a deliberate break.

## Alternatives rejected

- **Port materialization into Graph.** It breaks Graph's rule that every node of the pinned graph exists at run start,
  its node cap, run view, lint and designer, and ports the most intricate Dev Workflow code, all for a fan-out the gate
  cannot land.
- **Lane-internal fan-out** (one `implement` driving N tasks). Same apply ceiling; worth revisiting only together with
  a concurrent-patch merge, which does not exist.
- **Keep both engines.** The register's cost recurs on every fix, and the engine that needs the fixes ships off.
- **Agent nodes on Work Sessions.** The long-running, restart-sensitive work is the Dev Mode task, which is already
  durable; a second agent stack in Graph buys resumability for the short turns only.
- **Extend Graph's steer to a stuck `implement`.** The Development page already continues the task, and a steer would
  pull the chat steer and apply matrix into development graphs.
