# ADR 0022: A designer model proposes whole graphs for the operator to accept; it never saves, runs or opts in

- **Status:** Accepted — operator decisions of 2026-10-28 (plan approval); the design is implemented on the
  `feat/workflow-designer` branch (`GraphWorkflowDesignerService`, `RunGraphWorkflowDesignerTurnEndpoint`,
  `GraphWorkflowSemanticLint`, the Assistant tab of the graph workflow editor).
- **Date:** 2026-10-28
- **Scope:** How a model may help an operator author a Graph Workflow: what it may produce, what checks a proposal
  passes before the operator sees it, which models may be asked, and what is stored. It changes nothing for saved
  definitions, runs, chat, agents or Dev Workflows.
- **Authority:** Operator decisions of 2026-10-28, recorded in `Plans/workflow-designer-2026-10-28/` (the plan
  directory is git-ignored; it is the record of the decisions, not a published document): local and cloud models
  are both offered; there is no Manual/AI-Assisted authoring mode; the cloud gate is `CloudLocalDataGate`; this ADR
  is written because a documented invariant changes.
- **Amends:** the "Operator-authored" invariant of [Graph Workflows](../wiki/21-graph-workflows.md) §1 ("Nothing
  generates one, and no agent may edit one"). That invariant is a wiki statement, not an ADR, so no ADR is amended;
  the wiki carries the amendment and links here.

## Context

Wiki 21 §1 states that a definition is a graph the operator drew: nothing generates one and no agent edits one. The
rule exists because a definition is executable. A saved graph runs unattended, and with `AllowsActions` (ADR 0021) it
may perform real side effects, so authorship is a trust boundary. What the rule protects is that only the operator's
save changes a definition, not that the operator types every node.

A spike (`Plans/workflow-designer-2026-10-28/`) measured what a model can do against the real validator:

- **Grammar key order.** llama.cpp emits required properties before optional ones, so a response schema with
  optional members ahead of required ones forces the wrong order. Every member must be required and nullable, in
  canonical order.
- **Thinking cost.** With thinking on a turn takes 30 to 130 s; with it off, a median of about 15 s.
- **Validator-valid is not runnable.** With thinking off, three of ten graphs that passed the parser would not run: an
  `All` join behind mutually exclusive branches (always skipped) and an `upstream.<key>` binding to a node that is not
  an immediate predecessor (missing at run time). Nothing in the parser checked either; wiki 21 §4.2a said bindings
  are not path-analysed.
- **Calls must go through the node.** A direct call to the runtime child is invisible to idle tracking, and the node
  evicted the model mid-turn.

## Decision

1. **The designer proposes; the operator decides.** A turn returns a reply and, when a change was asked for, a whole
   proposed graph. The designer never saves, never starts a run and never sets or changes `AllowsActions` or
   `TriggerSources`. The editor shows the proposal as a diff over the working canvas; accepting replaces the working
   canvas only, leaves the saved baseline alone (the editor is dirty) and is one undo from the previous canvas. The
   route writes nothing. Only the operator's save changes a definition.
2. **Whole graph, not patch operations.** The model returns the complete graph in the wire shape the save route
   already takes, validated by the runtime's own parser. The change list shown to the operator is computed by the node
   on the parsed typed graphs, so a member equal to its parser default and a moved node are no change.
3. **Validation is the real one.** A proposal passes `GraphWorkflowGraphContract` and the tool gate under the
   definition's real `AllowsActions` (false when there is no definition), so a proposal cannot name an action tool the
   definition has not been opted in for. Errors are fed back to the model up to two times; a change turn that returned
   no proposal is nudged once; a proposal still invalid is returned marked invalid and cannot be accepted.
4. **Semantic lint L1 to L3 joins the validator as warnings, for everyone.** L1: an `All` join fed only by mutually
   exclusive branches of one Condition or DecisionModel. L2: an `upstream.<key>` binding to a node that is not an
   immediate predecessor. L3: an `input.<segment>` binding whose segment is not a predecessor key on a node where several
   predecessors can succeed together, an `All` join or an `Any` join not fed only by exclusive branches (there `input`
   is the predecessor map). They ride the existing warnings list, so the validate
   route and the save report them for hand-edited graphs too, and they never block. The designer loop treats every
   finding as must-fix.
5. **The response schema is required and nullable.** The envelope is `{reply, proposal}`, each required and nullable,
   in canonical order. The node builds the schema itself and it contains no boolean subschema, which llama.cpp refuses:
   a node's `config` is an object or null and a condition `value` a scalar or null. A provider that refuses or fails the
   request is a typed 502 `ProviderFailed` carrying its HTTP status only.
6. **Thinking is off.** The llama.cpp thinking-off marker is set for the designer call. The semantic lint and the
   retries carry quality; a reasoning budget is a later, measured change.
7. **No tool use.** The designer model is given no tools. It reads a prompt (the contract, the per-kind config members,
   the non-action tool catalog, installed models and agents, the working graph) and writes JSON.
8. **Cloud models only through `CloudLocalDataGate`.** The workflow and the conversation are node-local data. A model
   that leaves the node is admitted only when its connection grants `LocalData` or the `AllowCloudModelAccess` node
   setting is on; otherwise the route answers a typed 403 `CloudLocalDataWithheld` naming the grant. There is no silent
   fallback to another model and no per-send consent.
9. **Local models run Transient, through the node.** A node-local model resolves through the drafting eligibility rule,
   the provider resolver and `ResidencyIntent.Transient`, never a direct call to the runtime child, so idle tracking
   sees the call and the model is unloaded soon after.
10. **The drafting admission gate is reused.** A turn takes the single draft slot; while a run or a draft holds the
    node the answer is 409 `NodeBusy`. Turns never queue.
11. **The chat lives in the client's memory only.** The client sends the conversation whole on every turn; the node
    stores nothing, logs no model text and keeps no designer conversation entity.

## Consequences

- The authorship boundary is unchanged in substance: nothing becomes a definition without the operator's accept and
  save, and nothing a model writes can raise `AllowsActions`. A proposal that names an action tool in a definition not
  opted in is an invalid proposal, not a saved one.
- Hand-edited graphs gain three warnings. They are non-blocking, so no saved graph becomes unsavable; L1 is narrow
  (exclusive branches of one decision node only) to keep false positives rare.
- A turn costs up to three model calls and the turn holds the draft slot, so chat and runs get 409 while it works, and
  the designer gets 409 while they work. The prompt carries the contract plus the graph (about 5 to 6k tokens for the
  live round's small graphs), so prompt caching keeps repeats cheap and a 200-node graph is expensive.
- A cloud designer sends the workflow and the conversation off the node, which is why the gate applies; an operator
  who has granted neither switch can use local models only.
- A reload or New chat loses the conversation by design. Persisted designer chats and run diagnosis are later work and
  need their own decision, including the same cloud gate for run documents.

## Alternatives rejected

- **Patch operations (add, remove, set).** Each operation must be validated against the graph it assumes, ordering
  and key drift produce partial applies, and the model has to track state. A whole graph is validated once by the same
  parser a save uses and diffed by the node.
- **Per-send consent for cloud models.** A new dialog, a new audit row and a rule that differs from every other place
  local data leaves the node. `CloudLocalDataGate` is the existing single rule, with a grant that names itself.
- **A Manual / AI-Assisted mode flag on the definition.** The tab is offered on every workflow and a proposal changes
  nothing until accepted, so a mode adds a stored column and a decision for the operator without adding safety.
- **A built-in agent entity for the designer.** An agent definition brings tools, memory, a persona and a place in the
  agent catalog; the designer needs none of it, and a visible entity would invite binding it into graphs.
- **Thinking on.** It costs 30 to 130 s per attempt for a task the semantic lint and two retries already catch; the
  remaining failures were the lint's rules, not reasoning depth.
