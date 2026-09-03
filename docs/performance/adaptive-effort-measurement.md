# Adaptive `auto` reasoning effort — measurement recipe

What `auto` costs and what it buys, per tier, from the durable run-envelope rows the node already writes. No
instrumentation to enable and no separate capture step: every chat turn writes one `agent_execution_logs` row at
terminalization, and a turn authored with reasoning effort `auto` additionally stamps the tier it was dispatched to.

> **Merge note.** P-C1 owns the combined measurement recipe under `docs/`. When both slices are on one branch, this
> page becomes a section of that file rather than a page of its own; nothing in the SQL changes.

## The two columns

| Column | Value | Null when |
|---|---|---|
| `dispatched_tier` | `fast`, `normal` or `deep` | the turn authored a concrete effort, or the row predates the `AddAgentExecutionLogDispatchedTier` migration |
| `authored_effort` | `auto` | same |

Both are category labels. Neither carries message content, a model's output, or any signal the dispatcher read to
reach its decision. Retention is the whole-table `RetentionDays` sweep (30 days by default), so no separate pruning
applies.

## Tokens and latency by tier

`schema_version >= 5` is the filter that admits only rows written by a build that HAS these columns. A v4 row is not
"a turn that did not dispatch", it is a turn from before the field existed, and counting it as the former would
inflate the baseline arm with turns nobody can classify.

```sql
SELECT dispatched_tier,
       COUNT(*)                AS runs,
       AVG(latency_ms)         AS avg_latency_ms,
       AVG(prompt_tokens)      AS avg_prompt_tokens,
       AVG(completion_tokens)  AS avg_completion_tokens,
       AVG(reasoning_tokens)   AS avg_reasoning_tokens
FROM agent_execution_logs
WHERE record_kind = 1 AND schema_version >= 5
GROUP BY dispatched_tier;
```

The `dispatched_tier IS NULL` group of that result is the comparison arm: the same build, the same population, turns
that authored a concrete effort. Restrict both arms to one `model_name` before reading anything into the difference —
a FAST turn may have run on a different (smaller) model than the one the conversation was opened with, and
`model_name` on the envelope row is the model that actually served the turn.

## Before and after, on one model

```sql
SELECT CASE WHEN authored_effort IS NULL THEN 'authored effort' ELSE 'auto' END AS arm,
       COUNT(*)                AS runs,
       AVG(latency_ms)         AS avg_latency_ms,
       AVG(completion_tokens)  AS avg_completion_tokens,
       AVG(reasoning_tokens)   AS avg_reasoning_tokens
FROM agent_execution_logs
WHERE record_kind = 1 AND schema_version >= 5 AND model_name = :model
GROUP BY arm;
```

## What these numbers are not

They are an observation of live traffic, not an experiment. The two arms are not randomised: a user who picks `auto`
and one who picks `high` are asking different questions, and the tier itself is chosen FROM the question's shape. A
tier difference in average reasoning tokens is therefore evidence that the ladder is doing something, not evidence
that it is doing the right thing. Answer quality is a separate measurement — the golden sweep described in the
slice's plan, section 8 — and the swap path's real behaviour is covered by the live observations, not by this query.
