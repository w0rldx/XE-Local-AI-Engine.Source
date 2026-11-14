# ADR 0024: A successful scheduled agent run stores its answer in an encrypted `scheduled` conversation

- **Status:** Accepted — operator ruling of 2026-11-13 (P3, item N18); implemented in `RunSavedAgentHandler`,
  `ScheduledJobRunStore.SweepOlderThanAsync`, `GetScheduledJobRunEndpoint` and the scheduler run detail.
- **Date:** 2026-11-13
- **Scope:** What the `run-agent` scheduler template keeps of a run's content, where it is stored, who can reach it
  and how long it lives. It changes nothing for the other templates, for chat, or for run history.
- **Amends:** the `run-agent` invariant "Same runtime, no chat transcript" in [Scheduler](../wiki/06-scheduler.md).
  That invariant is a wiki statement, not an ADR, so no ADR is amended; the wiki carries the amendment and links here.

## Context

A `run-agent` job runs a saved agent with a fixed prompt, typically on a schedule ("summarize the overnight logs").
The handler ran it headless with a throwaway conversation id and no persistence pump, so the answer was discarded:
the run history showed only "Agent run completed on model ...", and the text was reachable, briefly, only through the
Advanced invocation monitor. The scheduler's main use case produced an answer nobody could read.

Wiki 06 stated two rules that a fix collides with: the run uses no chat transcript, and run history is content-safe
(status, model, token counts and duration; never the prompt, the output, tool arguments or tool results). The second
rule protects history that is broadcast over the scheduler hub, written to logs and kept for its own retention period.
The first was a consequence of having nowhere safe to put the answer, not a goal in itself.

## Decision

1. **A successful run writes one conversation of a new kind, `scheduled`, at the run's id.** After the invocation
   completes, `RunSavedAgentHandler` creates the conversation (title = the job's display name, the agent's id) and
   writes two completed messages through the chat persistence service: the prompt as the user turn and the final
   assistant text as the answer, with the model and the agent's name. Both go through the same writer as any chat,
   so title and content are encrypted at rest under the node key with the usual AAD. Reasoning and tool parts are not
   stored. `NodeConversationKind` is a string column without a check constraint, so the kind needs no migration.
2. **Nothing is stored for a run that did not succeed.** A failed, cancelled, timed-out or interrupted run, and a run
   that ends with no answer text, writes no conversation: partial text from a run that did not finish is not an answer
   anyone asked to keep. A save that fails deletes what it began and fails the run with the fixed reason "The agent
   run completed, but its answer could not be saved." Only a succeeded run links an answer.
3. **Run history stays content-free.** The summary, progress events, error text, hub events and log lines of a run
   carry no prompt and no answer, exactly as before. The link from run to answer is the run's own id, so no run column
   or detail field records it.
4. **The answer is reachable only from its run.** The chat list, conversation search and chat export filter
   `kind = 'chat'`, so a `scheduled` conversation never appears in the sidebar, a search hit or an export file, and a
   by-id export answers 404. `GET scheduler/runs/{runId}` returns `answerConversationId` when the run succeeded and a
   `scheduled` conversation exists at its id; the run list does not. The run detail opens it in a read-only view
   inside the scheduler page, read through `GET chat/conversations/{id}`. It is not the chat page: nothing there can
   send, regenerate or edit.
5. **An answer lives as long as its run, and can be deleted sooner.** The scheduler history sweep deletes each expired
   run's answer, with its whole conversation footprint, in the same transaction as the run, so history retention
   (`SchedulerHistoryRetentionDays`) governs both. The chat retention sweep is not filtered by kind, so when chat
   retention is shorter it removes the answer first and the run detail drops the link. The operator can delete an
   answer from the viewer, which purges it at once. Deleting a job keeps its runs, as before, and so keeps their
   answers reachable and deletable.

## Consequences

- The scheduler now stores model output at rest. It is protected like chat content, and it is in a database backup
  and restore like chat content.
- The answer is held to the run's retention rather than to chat retention alone, so it can never outlive the only
  path that reaches it.
- An agent answer that should not be kept has no per-job opt-out; a job that must not store answers should not be a
  `run-agent` job. An opt-out is a later decision if it is asked for.
- A `scheduled` conversation is still a conversation, but it is read-only (amended 2026-11-13): `NodeChatMutationGuard`
  refuses every by-id write a client could aim at it (send, regenerate, revision, rename, pin, archive, plain mode,
  sampling, memory exclusion, branch, selected path, feedback, compaction, attachments) with 409
  `ReadOnlyConversation`. Delete stays open, for the run viewer and the handler's own cleanup. Work-session and
  integration transcripts are not covered by this rule.

## Rejected alternatives

- **Kind `chat`, in the sidebar.** Mixes unattended output into the operator's own chats and their search and export,
  which is the leak the `kind` column closed for work sessions and integrations.
- **The answer in run history.** Run history is broadcast and logged; putting content there undoes the content-safe
  rule instead of keeping it.
- **Embedding the chat page with a pinned scope**, as work sessions do. It keeps regenerate and edit live on every
  message, and it adds a cross-feature import the frontend's dependency baseline does not allow.
- **Chat retention only.** A run swept before its answer would leave a readable conversation with no path to it.
