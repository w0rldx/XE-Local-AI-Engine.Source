# Agent trajectory data policy

> **Status:** Draft, 2026-08-25 — awaiting operator approval. Nothing described under
> [§3](#3-what-a-content-rich-trajectory-store-would-require) exists, and nothing may be built until this document is
> accepted. The maintainer's decision (2026-08-25) is *policy first*: the metadata-only audit invariant stays in force
> meanwhile.

Scope: what this node records about an agent run, and what it would take to record more. It covers Development Mode
attempts, chat agent-mode runs, work sessions, and the compute tools. It does not cover conversation storage or the
knowledge base, which have their own retention.

## 1. What exists today: operational audit, metadata-only

`AgentExecutionLog` is content-free **by design**, and that is an invariant with a test behind it
(`AgentExecutionLog_StoresMetadataOnly_NoContent`). Its three record kinds — `AdaptiveMemoryDiagnostics`,
`ChatRunEnvelope`, `ApprovalDecision` — carry identity, timing, category, decision and outcome. ADR 0006 §7 states the
same rule for the inbound MCP surface in the positive and the negative: tool name, category, bounded key prefix, request
identity, decision, duration, audit outcome are recorded; *arguments, prompts, message content, tokens, passwords, full
keys and host paths are never recorded*.

**Outbound tool names are recorded too, and that is an extension of the rule above rather than a case of it.** ADR
0006 §7's tool-name precedent is about the *inbound* MCP surface — what a caller asked this node to run. The agent
loop also records the names of the tools it called *itself*, in two places: the per-step consumption detail on a work
session's `StepEnded` / `StepFailed` event, and `dev_workflow_node_runs.tool_names_json` on a settled workflow node
run. Three conditions bound it, all enforced in code rather than by convention. **Names only** — never an argument,
never a result, never a per-call sequence; the surrounding numbers are counts. **Capped** — the set is bounded at
sixteen distinct names inside `ProviderCallBudget` itself, re-capped when a node run unions its steps, and the
serialized column is clamped to 1024 characters with a trailing `…` element when it had to drop names, so neither a
runaway tool loop nor a long name can grow the record. **Operator-authored** — a tool name is a value this node's own
catalog and the operator's MCP registrations chose, so it is an identifier of configuration, not of a user's content.
A name still says which capability an agent reached for, which is why it is written down here rather than left to the
code comments that state the same three conditions at each carrier.

Rows are pruned by `RetentionSweeperService` against `AgentExecutionLogRetentionOptions` (`RetentionDays`, default 30,
on a `SweepInterval` cadence). The window is validated at startup, because a non-positive value would set the cutoff at
or after "now" and purge the table.

`DevelopmentEvent` records state transitions, not content. `DevelopmentArtifact` records content **hashes** —
`SubjectHash`, `ManifestHash`, `ContentHash` — plus an input-artifact DAG. Command output that is persisted as evidence
passes `DevelopmentArtifactSanitizer` first; memory proposals pass `MemoryProposalSecretScanner` before they are stored.

Two things follow that are easy to get wrong:

- **Evidence is not a trajectory.** Development Mode does persist real command output as validation evidence. That is
  bounded (`MaxCommandOutputBytes`), sanitized, scoped to one attempt, and exists to make an apply decision auditable.
  It is not a record of what the model thought, chose, or was shown.
- **The audit/training separation is structural today, not designed.** No pipeline reads `AgentExecutionLog`, and the
  training path takes operator-curated datasets through `SampleValidationPipeline` (ADR 0005: training is a uv-managed
  Python subprocess). Nothing currently *prevents* someone from wiring one to the other; the separation holds because
  the content that would make it worthwhile does not exist. This document is what turns that accident into a rule.

## 2. What does not exist: content-rich trajectory collection

There is no store of prompts, model reasoning, tool arguments, tool results, intermediate patches, or per-tool-call
sequences. The 2026-08-25 proposal's trajectory phase would introduce one. The proposal itself says the policy comes
first, and this document agrees with it.

The reason is not squeamishness about volume. A trajectory is, by construction, the highest-value artifact this product
could hold: it contains the repository's source in context, the model's tool arguments, and every byte the tools
returned — including the secrets the read guards keep out of the *engine's* tools but cannot keep out of a repository
test's stdout (see the threat model's AB2). A metadata-only audit is not merely smaller than a trajectory; it is a
different kind of object with a different blast radius.

## 3. What a content-rich trajectory store would require

Every item below is a **precondition**, not a preference. Partial implementation is not a partial version of this
policy.

1. **An explicit operator switch, off by default, node-local.** One setting that turns collection on, with no
   per-feature back doors and no implicit enabling by another feature. It must not be writable through the agentic MCP
   surface: ADR 0006 §8 states that agentic authority grants no general policy bypass, and a switch that turns on
   content capture is policy. Turning it on must state, in the UI, what will be recorded and where it will be kept.

2. **A separate store, never the audit tables.** Trajectories go to their own table and their own blob space, encrypted
   at rest through the existing `AesGcmNodeAeadCipher` / `ManagedEncryptedBlobStore` path. `AgentExecutionLog` stays
   content-free, and no read path may join the two into a single view. Reusing the audit tables would silently retire an
   invariant that has a test defending it.

3. **Its own retention, shorter by default than the audit window, and enforced by the same sweeper.** Retention is a
   number the operator can lower and a sweep that actually runs. A store with a configurable window and no sweeper is a
   store with no retention.

4. **Redaction on the way in, not on the way out.** Every captured payload passes `DevelopmentArtifactSanitizer` (host
   paths, protected roots) and `MemoryProposalSecretScanner` (credential shapes) before it is written. Redacting at read
   time means the unredacted bytes were at rest, which is the thing being avoided. Redaction is best-effort and must be
   documented as best-effort: a scanner does not find a secret it has no pattern for.

5. **No automatic path to training.** A trajectory never becomes a training sample without a separate, explicit,
   per-dataset operator action, and the resulting sample goes through the existing curation path
   (`SampleValidationPipeline`, the `TeacherSampleRecordV1` contract) exactly as any other sample does. "Collected" and
   "eligible for training" are two different states, and no background job may move a record between them.

6. **Export and delete, per unit and in bulk.** The operator can export what was collected for one run and delete it,
   and can delete everything. Delete means the rows and the blobs, and it must succeed while collection is enabled.

7. **Never leaves the node without a second, separate decision.** Trajectory content is not attached to a cloud request,
   a support bundle, a crash report, or telemetry. The existing rule stands: WorkerHub is the only platform channel, and
   this is not something it carries.

8. **Documented before it is shipped.** The invariant checklist in `docs/wiki/12-security-and-privacy.md` gains a line,
   and this document moves from Draft to Accepted with the operator's date on it.

## 4. What this policy does not decide

- Whether trajectory collection is worth building at all. That is the operator's call and this document takes no
  position on it.
- The schema, the capture points, or the storage cost. Those are design work that starts *after* approval.
- Anything about conversation retention, knowledge-base documents, or uploaded files, each of which has its own
  handling.

## 5. In force until this is accepted

- `AgentExecutionLog` and `DevelopmentEvent` stay metadata-only.
- ADR 0006 §7's negative list stays exhaustive: no arguments, prompts, message content, tokens, passwords, full keys or
  host paths.
- No new store, column, or log kind may carry model-visible content, and a change that would is a policy change, not an
  implementation detail.
