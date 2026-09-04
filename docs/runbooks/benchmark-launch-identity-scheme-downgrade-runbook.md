# Downgrading past a launch-identity scheme change — operator runbook

**Audience:** an operator deploying a build OLDER than the one currently installed, when the two compute benchmark
launch identities under different schemes.
**Feature:** P-C5/S2 — `LlamaServerLaunchProjection.IdentitySchemeVersion`, migration `AddAiTrendsWave`.

---

## Why this exists

A benchmark run freezes its **intended** launch identity when it is queued, and writes its **effective** launch
identity later, when it actually launches. Both are SHA-256 hashes of the canonical launch projection, so adding a
member to that projection changes every hash the type produces. The two ends of one run are therefore only comparable
when they were computed under the same **identity scheme**.

Rolling forward needs no procedure. Every launch-bearing executor calls
`BenchmarkLaunchIdentityScheme.RequireCurrent` before it launches anything, and a row frozen under a different scheme
is failed with the stable reason token `launch-identity-scheme-superseded`, having written no effective identity.

**Rolling back is different.** A stored scheme of `2` on a build that computes scheme `1` is refused by the same guard
— but only while the column still exists. Revert the projection and run the migration's `Down` while scheme-2 work is
still queued and the old build reads those rows as scheme 1 (a dropped column is indistinguishable from a pre-scheme
freeze), executes them, and files a v2 intended hash beside a v1 effective hash. That is precisely the cross-scheme
comparison the scheme exists to prevent, and no later build can tell the two apart.

So the downgrade is a **procedure, run in this order**, not a revert.

---

## The procedure

### 1. Quiesce the benchmark queue consumer

`BenchmarkQueueHostedService` is a plain `BackgroundService` with **no runtime stop switch**, and since the profiling
lease landed its poller swallows a failed claim rather than ending `ExecuteAsync` — so a transient database error will
not stop it for you either. **Quiescing means stopping the node.**

Then let in-flight work reach a terminal state. A claimed row can now sit in a bounded profiling-refusal retry loop, so
"reach a terminal state" is bounded by one `BenchmarkWaitBudget`, not by the spawn alone.

### 2. Drain every non-terminal launch-bearing row under scheme 2

Across all three tables. Fail each with `launch-identity-scheme-superseded`, or requeue it for a re-freeze on the older
build once that build is running.

| table | non-terminal statuses to drain | scheme column |
|---|---|---|
| `benchmark_runs` | `PrimaryStatus` in `Queued`, `Running`, `CancelRequested` | `primary_launch_identity_scheme` |
| `benchmark_judge_attempts` | `Status` in `Queued`, `Running` | `launch_identity_scheme` |
| `benchmark_comparisons` | `Status` in `Queued`, `Running` | `launch_identity_scheme` |

Fidelity work is exempt for the same reason it is exempt at cutover: it runs `llama-perplexity` with no llama-server
and therefore has no launch identity at all.

### 3. Verify none remain — this is the gate

One query per table, each of which must return zero. **Do not proceed on a non-zero count.**

```sql
SELECT COUNT(*) FROM benchmark_runs
 WHERE primary_launch_identity_scheme = 2
   AND primary_status IN ('Queued', 'Running', 'CancelRequested');

SELECT COUNT(*) FROM benchmark_judge_attempts
 WHERE launch_identity_scheme = 2 AND status IN ('Queued', 'Running');

SELECT COUNT(*) FROM benchmark_comparisons
 WHERE launch_identity_scheme = 2 AND status IN ('Queued', 'Running');
```

Statuses are persisted as their enum NAMES, not as ordinals (`HasConversion<string>()` in each entity
configuration), so the literals above are the values actually in the column. The names come from
`BenchmarkPrimaryStatus` and `BenchmarkJudgeAttemptStatus`
(`XE-Local-AI-Engine.Client.Persistence/Entities/BenchmarkEnums.cs`) — read them there rather than from memory.

### 4. Only then deploy the older build and run the migration's `Down`

The three scheme columns ride `AddAiTrendsWave`, the ONE cumulative migration the AI-trends wave landed as, so its
`Down` drops all twenty of that wave's columns across five tables — not only these three. Rolling back past it takes
the agent-execution-log, agent-definition and dev-workflow-node-run telemetry with it; nothing older is touched.
Rows that already executed keep both of their hashes under one scheme and compare exactly as they always did.

---

## What you do NOT need to do

- **Nothing has to be drained rolling forward.** The guard runs per item, at the top of each executor, and fails a
  straddling row before it can lease a model or spawn a process.
- **Runs that already completed are untouched** in either direction. Their two hashes were computed under one scheme.
- **No receipt has to be rewritten.** The effective side is versioned by `LlamaServerLaunchReceipt.ReceiptVersion`,
  which moved to `2` in the same change as the scheme, so a pre-slice and a post-slice identity are distinguishable
  rather than merely unequal.
