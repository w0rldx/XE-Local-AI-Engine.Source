# Agent execution unit cost telemetry — measurement runbook

**Audience:** anyone measuring whether a change to the agent runtime made a run cheaper, faster, or neither.
**Feature:** P-C1 — cost telemetry on agent execution units. The columns live on `dev_workflow_node_runs`; the
per-attempt history lives on the run's `node.retry.scheduled` events.

---

## Why this exists

A workflow node run is the unit a cost question is actually about: one attempt, one agent or one development task,
one settle. Before this slice the numbers existed only inside a live budget object that was disposed when the step
ended, so the only honest answer to "did that change make it cheaper" was to watch a terminal. The twelve columns
below are that answer written down, and this page is how to read them without over-claiming.

**Nothing here routes anything.** Every column is written at the transition that settles an attempt and read by
nobody but a report. A collection that fails, times out, or finds nothing leaves nulls and the run behaves
identically — which is what `DevWorkflowNodeRunTelemetryTests.Telemetry_DoesNotAlterTransitionCommands` pins.

---

## The recipe

Two arms are two runs of the same definition on the same model and machine, one per side of the change under test.

**Two sources, added — never one.** A node's cost is `final row + every retry snapshot`. The final row holds the
LAST attempt only, because the `Pending` reset clears the columns (`DevWorkflowStore.Mutations.cs:143-153`); the
earlier attempts live in the `node.retry.scheduled` detail, which carries all nine additive members. Summing the
node rows alone under-reports a fail-fail-success node by two attempts. The two never overlap, so addition is the
whole rule.

**The retry arm is NOT SQL.** `dev_workflow_run_events.detail_json` is encrypted at rest
(`NodeEncryptionSaveChangesInterceptor.cs:716-719`), so `json_extract` over it returns ciphertext. Read it through
the existing events endpoint, which returns the decrypted `DetailJson`
(`DevWorkflowRunEndpoints.cs:171-182`, route bound at `:177`; DTO `DevWorkflowContracts.cs`) — no new endpoint, no
new column.

```bash
# Retry arm: every earlier attempt of every node in the run, summed per additive member.
# Route: api/local/v1/development-workflows/runs/{runId}/events (LocalApiRoutes.cs:5,:901); page with sinceSeq.
curl -s "$NODE/api/local/v1/development-workflows/runs/$RUN_ID/events?sinceSeq=0&limit=200" \
  | jq '[.items[] | select(.eventType == "node.retry.scheduled") | (.detailJson | fromjson)]
        | {attempts: length,
           inputTokens:          (map(.inputTokens          // 0) | add),
           outputTokens:         (map(.outputTokens         // 0) | add),
           reasoningTokens:      (map(.reasoningTokens      // 0) | add),
           estimatedInputTokens: (map(.estimatedInputTokens // 0) | add),
           providerCalls:        (map(.providerCalls        // 0) | add),
           toolCalls:            (map(.toolCalls            // 0) | add),
           toolSchemaTokens:     (map(.toolSchemaTokens     // 0) | add),
           agentTurnMs:       (map(.agentTurnMs       // 0) | add),
           workSessionSteps:     (map(.workSessionSteps     // 0) | add)}'
```

Page on `sinceSeq` while `hasMore` is true (`ListDevWorkflowRunEventsResponse(Items, LastSequence, HasMore)` and
`DevWorkflowRunEventFeedRequest` in `DevWorkflowContracts.cs`, default limit 200), and add the result
member-by-member to the final-row rollup below. **Report the pair, not one of them:** `final` (what the successful
attempt cost) and `total`
(`final + retries`, what the run actually spent). A slice that improves cost by removing retries shows it only in
`total`.

```sql
-- Final-attempt rollup. :runIds is the arm's run id list. Add the retry arm above for the true total.
SELECT n.node_type,
       COUNT(*) AS node_runs,
       SUM(n.input_tokens)  AS input_tokens,   SUM(n.output_tokens) AS output_tokens,
       SUM(n.reasoning_tokens) AS reasoning_tokens,
       SUM(n.estimated_input_tokens) AS estimated_input_tokens,
       SUM(n.tool_schema_tokens) AS tool_schema_tokens,
       SUM(n.provider_calls) AS provider_calls, SUM(n.tool_calls) AS tool_calls,
       SUM(n.agent_turn_ms) AS agent_turn_ms,
       SUM(n.ended_at_utc - n.started_at_utc)  AS run_ms,
       SUM(n.started_at_utc - n.queued_at_utc) AS queued_ms,
       SUM((n.ended_at_utc - n.started_at_utc) - COALESCE(n.agent_turn_ms, 0)) AS outside_turn_ms
FROM dev_workflow_node_runs n
WHERE n.run_id IN (:runIds) AND n.ended_at_utc IS NOT NULL
GROUP BY n.node_type;

-- How many earlier attempts the retry arm above must have summed. Counting them here and summing their cost
-- there is the check that the two agree; SUM(attempt) beside SUM(tokens) is still forbidden. See rule 2.
SELECT COUNT(*) AS retried_attempts
FROM dev_workflow_run_events e
WHERE e.run_id IN (:runIds) AND e.event_type = 'node.retry.scheduled';

-- Per-node detail. Report medians and full spread, never a mean over a handful of rows.
SELECT n.node_key, n.served_model_name, n.attempt, n.status, n.failure_class,
       n.input_tokens, n.output_tokens, n.tool_calls, n.tool_schema_tokens,
       n.agent_turn_ms, n.ended_at_utc - n.started_at_utc AS run_ms, n.route_json
FROM dev_workflow_node_runs n WHERE n.run_id IN (:runIds) ORDER BY n.node_key, n.attempt;

-- Route stability: did the change move the run through a different path?
SELECT n.node_key, n.route_json, COUNT(*) AS times
FROM dev_workflow_node_runs n WHERE n.run_id IN (:runIds)
GROUP BY n.node_key, n.route_json ORDER BY n.node_key;

-- Cross-unit failure rollup (the AgentUnitFailureClass table as SQL; the FailureCategory and [code] arms live
-- only here — the workflow arm below is also shipped as code and reaches the node drill-down as failureClassGroup).
SELECT CASE n.failure_class
         WHEN 'ProviderError' THEN 'Provider'      WHEN 'ToolCommandFailed' THEN 'ToolOrCommand'
         WHEN 'GateRejected'  THEN 'Rejected'      WHEN 'BudgetExhausted'   THEN 'BudgetExhausted'
         WHEN 'Configuration' THEN 'Configuration' WHEN 'Policy'            THEN 'Policy'
         WHEN 'Timeout'       THEN 'Timeout'       WHEN 'Interrupted'       THEN 'Interrupted'
         WHEN 'Cancelled'     THEN 'Cancelled'     ELSE 'Internal' END AS failure_group,
       COUNT(*) AS n
FROM dev_workflow_node_runs n
WHERE n.run_id IN (:runIds) AND n.failure_class IS NOT NULL GROUP BY failure_group;
```

**Reading rules.**
1. Compare matched node keys across arms, never a fix-loop attempt against a first attempt.
2. **Columns are last-attempt only; the total is columns + snapshots.** The `Pending` reset clears the columns
   (`DevWorkflowStore.Mutations.cs:143-153`), so a node that failed twice and succeeded reports attempt 3 in its
   row and attempts 1 and 2 in two `node.retry.scheduled` details (all nine additive members on each).
   **Quote `total = final + retries`; quote `final` alone only when the question is "what does a clean pass
   cost".** The two sources cannot double-count, because a reset row is emptied before the next attempt fills it.
   **Never put `SUM(attempt)` in the same SELECT as `SUM(input_tokens)`** — it reads as a per-attempt average and
   is not one.
3. **Node-run tokens are a lower bound**, for seven separate reasons: a replayed step keeps the first attempt's
   numbers (`WorkSessionModels.cs:120-125`); some envelopes are written after the fact by crash recovery
   (`NodeChatRestartRecoveryService`); a step that spawned sub-agents ran more than one budget
   (`WorkSessionModels.cs:115-118`) whose invocations may not share the conversation; the envelope read is
   paged, so a truncated page under-counts rather than failing; **a node run abandoned while its session was
   mid-step counts only the steps that had ended**, because the consumption row is written inside `SettleStepAsync`
   and an in-flight step has none yet — which is exactly the `Blocked` and `Cancelled` case, the one where the
   biggest numbers are; **the envelope read stops after 20 pages of 200**, so a conversation past 4,000
   envelopes under-counts rather than holding a dispatcher tick open; **one retry route shares ONE collection
   budget** (`PublishingDevWorkflowStore.RouteRetryAsync` opens a single 5 s deadline before the loop over
   `command.Resets`), so every reset is offered a collection *while that budget lasts* and, once it is spent, the
   remaining resets are forwarded unenriched with no collection started at all — those attempts get no
   `node.retry.scheduled` cost vector, and the retry-arm total below under-counts by exactly them; and **at most four
   cost collections run at once per application** (`DevWorkflowNodeTelemetryCollectionPool`, a container singleton), so a
   settle arriving while four stuck collectors hold the pool is forwarded unmeasured the same way. Both losses are
   silent in the data and loud in the log — grep for `cost-collection slots are in use` and `outlived its`.
4. `estimated_input_tokens` is a character-profile estimate (`WorkSessionModels.cs:129`); quote it only where
   `input_tokens` is null.
5. `provider_calls` is a ratio against its cap only while one budget was attached (`WorkSessionModels.cs:115-118`).
6. **`route_json.satisfied` means "this out-edge was satisfied", never "the successor ran"** — an `All` join can
   still skip on a dead sibling edge and an `Any` join can admit on one (`DevWorkflowStateMachine.cs:186-192`). For
   `Gate` nodes, `output_json.branch` is authoritative (written at `DevWorkflowDispatcher.cs:985-994`). The same
   route reaches an operator on the node drill-down as `route`, with `truncated` saying whether keys were dropped.
   **The document has three buckets, not two: `satisfied`, `dead` and `waived`.** A `waived` out-edge belongs to a
   node run whose own skip the state machine excused — an operator's skip rather than one that cascaded off
   something dead — and it is neither of the others, so do not fold it into either when you read or aggregate. It
   does not admit an `Any` successor the way a satisfied edge does, and it does not kill an `All` one the way a dead
   edge does; a downstream `All` join carries on past it as long as a sibling arrived. The buckets are the three
   states `DevWorkflowStateMachine.EdgeState` can answer for a terminal source, one for one, which is what stops the
   recorded route and the routing that actually happened from answering differently. A row written before this
   bucket existed simply omits `waived`, which reads back as the empty list it means.
7. `tool_schema_tokens` is schema tokens **shipped across rounds**, not schema size. A tool-schema *budget* would
   be the size; this column is what those schemas cost to send, summed over every round of the attempt.
8. **The recipe cannot be re-run over runs older than the envelope retention window** (30 days,
   `AgentExecutionLogRetentionOptions.RetentionDays`): `dev_workflow_node_runs` outlives `agent_execution_logs`, so
   a collector bug is unrecoverable for old runs. Snapshot query output per arm at measurement time.
9. N per arm and machine state (`source ~/cuda-llama/env.sh`, fresh DB) go in the report beside the numbers.
10. **`tool_names_json` is agent-path only.** It is populated from the work-session step rows, so it is null
    on a DevTask node run and on every Tool/Gate/Parallel/Join row. A null means "no step rows to read", never "this
    node called no tools" — use `tool_calls` for that question. The names are also **bounded twice at the source**:
    sixteen distinct names per attempt, each clamped to 128 characters with a trailing `…`, and a name the model
    emitted that matched no offered tool is not recorded at all. So this column answers "which offered tools did
    this attempt reach for", never "what did the model type".
11. **`agent_turn_ms` is WHOLE-turn time, not provider time.** It sums each chat-run envelope's own duration
    (`agent_execution_logs.duration_ms`), and an envelope spans the provider rounds AND the tool loop between them.
    Nothing persisted separates the two, so `run_ms - agent_turn_ms` is time spent **outside** the turns — queueing
    after the node started, the settle itself — and is **not** tool time. Do not present it as a provider-versus-tools
    split; the node drill-down does not either, where the two rows read "Agent turns" and "Outside the turns".

**Defaults by question.** *Did model routing change what it cost?* — tokens and `run_ms` grouped by
`served_model_name`. *Did tool-schema filtering pay for itself?* — `tool_schema_tokens` per node run, with
`tool_calls` beside it as the "did filtering break tool use" guard. *Did the run take a different path?* — the route
query. *Did a serving change move latency?* — `agent_turn_ms` at fixed token counts, which moves with the tool loop
as well as with the provider (rule 11).
