# Benchmarks — Frozen Runs, Discriminating Scores, Display-Only Axes

> Baseline: `feature/benchmark-p2-discrimination` · Reviewed: 2026-08-26 · Code-grounded.

The **benchmark** module measures one frozen task against many local models and ranks the results. A *project* pins the task, the context and the agent; a *run* replays that project against one model with a byte-identical runtime snapshot; a *judge* scores the answer. P2 added the parts that make a score mean something: a quant-fidelity axis (perplexity, optional KL divergence), rubric criteria decided server-side with no model in the loop, and an opt-in pairwise judge fitted with Bradley–Terry.

Two rules govern everything below and are worth stating before the mechanics:

- **Quality-only ranking.** Throughput, perplexity, KL divergence and top-token agreement are **display axes**. None of them ever reaches `Rank` (`BenchmarkStore.cs`, `LoadRankingAsync`/`ComputeQuality`).
- **A number is shown only while it is comparable.** Every display axis carries its own comparability key, and a figure whose key no longer matches the project's current one is **withheld**, not flagged. A number a reader can still see is a number they will still compare.

---

## 1. Freeze — what a run is measured against

`BenchmarkRunFreezeService` (`XE-Local-AI-Engine.Client.Application/Services/Benchmarks/BenchmarkRunFreezeService.cs`) turns a project plus a model choice into runs that are already fully described before any of them starts:

- The task is decoded once, the agent runtime resolved once, and the llama-server probe / variant selection memoized for the whole launch through `BenchmarkFreezeScope`. A launch matrix therefore compares cells that saw one runtime, not N.
- Every run stores a **`BenchmarkRuntimeSnapshotV1`** — model identity and member hashes, resolved launch arguments, KV-cache types, sampling. The record is **frozen at schema v1 and validated by re-hashing its own serialized bytes** against an embedded `ConfigurationHash`. Adding a member to it, or to any nested record, makes every stored run fail to deserialize. Per-run facts go in **flat columns** instead — that is why `RepeatGroupId`, `RepeatIndex`, `RepeatMode` and the whole fidelity projection are columns rather than snapshot members.
- Repeats: `MaxRepeatCount = 10` (`BenchmarkRunFreezeService.cs:186`). A whole repeat group is inserted in ONE transaction by `IBenchmarkStore.StartRunsAsync`, which checks the project version once and queues the work items FIFO. Per-run CAS would leave orphan queued runs behind on a partial failure.

### 1.1 Repeat modes

`BenchmarkRepeatMode` (`XE-Local-AI-Engine.Client.Persistence/Entities/BenchmarkEnums.cs:52`):

| Mode | What varies | What it measures |
|---|---|---|
| `Throughput` (default) | nothing — temperature 0, one seed | the machine. Every repeat answers identically, so the spread is hardware noise |
| `AnswerVariance` | the seed, advanced per repeat, sampled at a temperature (default 0.7) | the model. The spread of *answers* is the measurement |

Both knobs are **sampling** and must never reach the launch arguments, or the runs of one group stop sharing a launch identity. Mode, seed and temperature are duplicated onto the run as plaintext columns because listing and CSV never decrypt a snapshot.

### 1.2 Stop reasons

`BenchmarkPrimaryStopReasons` (`XE-Local-AI-Engine.Client.Persistence/Stores/IBenchmarkStore.cs:913`) is the one shared vocabulary, with one shared pair of predicates (`IsTruncated`, `IsIncomplete`). Two of its members are **node-derived, not provider tokens**:

- `reasoning-length` — the provider said `length`, and no visible answer token was ever emitted. `IsTruncated` covers it deliberately.
- `incomplete` — a *clean* finish that answered nothing: the turn ended on an unanswered tool call, or emitted only reasoning.

Both are decided in `BenchmarkRunExecutor.ResolveStopReason` from the **coalesced** parts. A per-delta capture cannot show the shape of a turn.

Rank-exclusion precedence: **warm-up > user score > (truncated | incomplete) > judge reasons**. An operator user-score suppresses every stop-reason exclusion, because an operator who graded a run has overruled the machine.

### 1.3 Task items — the questions a project asks

A project holds **1..N `BenchmarkTaskItem` rows**, and a single item is the degenerate case: a project with one item freezes, ranks and exports exactly as it always did.

- An item carries a **kind** (`prompt`, and the reserved generator kinds `niah`/`niahCase`), a **revision**, a plaintext **input hash**, a `countsTowardScore` flag, and four encrypted payloads: prompt, reference answer, per-criterion verifier override, generator config. Each payload has its own AAD column name — a verifier config holds expected answers and must never be servable as the prompt.
- **The store owns identity.** Index, revision, input hash and the project's item-set hash are computed on every write (`BenchmarkTaskItemHashing`); the service and the endpoints cannot name them. A client that could would be able to present an answer to an old question as an answer to the current one.
- `input_hash` = `v1:` + SHA-256 over `(kind, revision, prompt, reference, verifier, generator)`. `benchmark_projects.task_item_set_hash` = `v1:` + SHA-256 over the **leaf** items ordered by their immutable **`Id`, not by `Index`** — so **adding or deleting** an item moves it and **reordering does not**. A cosmetic drag-and-drop must not unrank a completed suite.
- A **moved set hash resets the rank cohort**, through the same path a judge-policy activation uses: the project score is a mean over the item set, so a different set is a different score. A reorder does neither.
- **Item writes are refused while any of the project's work is queued or running.** That is the primary guard; the run-level staleness stamps below are the safety net for completed history.
- **Creation is atomic.** `IBenchmarkStore.CreateProjectAsync` takes the initial item set and writes it in the project's own transaction, so a project never exists without a question to ask. `GetOrCreateItemsAsync` survives **only** as the legacy backfill for projects created before task items existed, and exactly one endpoint may call it (`GET …/items`) — a migration cannot do it instead, because it runs without the node encryption key and `prompt_json` is AAD-bound to its own item's id. It deliberately leaves the project's set hash **null**: materializing item 0 changes nothing about what the project asks, so it must not move the value every historical run is compared against.
- Cap: **20 leaf items** (`BenchmarkTaskItemService.MaxTaskItems`), counted over leaves so a generator's cases each count.

Every run is stamped at freeze with **four immutable identities** (`benchmark_runs`): `task_item_id` (which leaf it answered), `cell_key` (**NOT NULL** — which measurement cell its per-item score aggregates into), `task_input_hash` (a copy of the item's input hash: *exactly what it was asked*) and `task_item_set_hash` (a copy of the project's: *what the whole question set was when this cell was measured*). Three are NOT NULL because a missing stamp would otherwise read as "belongs with everything else": a null `cell_key` would drop every ungrouped run of a project into one anonymous bucket and average their scores together, silently. Runs frozen before the columns existed carry `run:<id>` and `v1:legacy`, and `v1:legacy` is also what they are compared **against**, so they are never read as stale.

> **Not built yet:** the freeze does not yet fan out over items, and ranking does not yet aggregate per cell — a run is still its own singleton cell. The columns exist so those land without a second migration.

---

## 2. The work queue — four kinds, one consumer

`BenchmarkWorkKind` (`BenchmarkEnums.cs:24`) is `{ Primary, Judge, Fidelity, Comparison }`. The two P2 kinds are **appended at the end**: the ordinal is persisted, so reordering would silently re-label stored rows.

`BenchmarkQueueHostedService` is a **single consumer** holding `IGpuWorkGate.TryBeginShared(GpuWorkKind.Benchmark)` across the claim and the execution. Nothing else of ours holds VRAM while a benchmark item runs, which is why a `Fidelity` item needs no gate of its own.

| Kind | Executor | Spawns |
|---|---|---|
| `Primary` | `BenchmarkRunExecutor` | llama-server, the run's frozen arguments |
| `Judge` | `BenchmarkJudgeExecutor` | llama-server for the judge model — **or nothing at all** (§4) |
| `Fidelity` | `BenchmarkFidelityExecutor` | `llama-perplexity`, no server and therefore no readiness probe |
| `Comparison` | `BenchmarkComparisonExecutor` | llama-server for the judge, one ordered pair per item |

> **A new work kind is not additive at the lifecycle layer.** `BenchmarkStore.ClaimNextAsync` (`BenchmarkStore.cs:450`) and `RecoverRunsOnStartupAsync` (`:1747`) both used to branch "Primary, else it is a Judge", and the `else` dereferenced `JudgeAttemptId`. Either new kind reaching them threw `InvalidJudgeTransition` and stalled the single-consumer queue behind an item it could never claim. Both are **explicit four-arm switches** now, and recovery needs **two sibling pre-sweeps**: an attempt or comparison left `Running` by a killed process, whose work item a previous partial recovery already terminalized, has nothing else that can reach it.

> **A comparison claim touches neither run's version.** Every per-run kind bumps `BenchmarkRun.Version` so a poller sees that something changed; a comparison names two runs and its work item names only the canonical first, so the common bump invalidated that run's CAS token on every pairwise claim — scoring, deleting or re-measuring it returned `VersionConflict` throughout a tournament. A pairwise reader is refreshed by the fit's own publication.

---

## 3. Quant fidelity — perplexity and KL divergence (display only)

`BenchmarkFidelityExecutor` runs `llama-perplexity` against the run's **own frozen placement** (`--n-gpu-layers`, `--tensor-split`, `--override-tensor`, `--cache-type-k/-v`, `--flash-attn`) but at a **pinned 512-token window** (`BenchmarkFidelityPolicy.ContextTokens`, `BenchmarkFidelityContracts.cs:12`). The window is pinned because perplexity is only comparable at a fixed window and every published llama.cpp/Unsloth/bartowski number uses 512; the placement knobs are replayed because they are exactly what differs between the runs being compared.

Chunks: `DefaultChunks = 200`, range `50..655`. On this box, 200 chunks of the shipped wikitext-2 corpus separated `Qwen3.8-27B` Q4_K_M (**6.7977 ± 0.07405**) from UD-Q3_K_XL (**6.9497 ± 0.07550**) with non-overlapping bands; at the 50-chunk floor the errors roughly double and the two overlap.

**Write the parser against the binary, not the README.** A `--kl-divergence` run prints no `Final estimate` line at all — its perplexity is `Mean PPL(Q)` inside the statistics block; the statistics blocks separate a value from its error with `±` while the plain line uses `+/-`; and top-token agreement is printed as `Same top p`, with the word "agreement" appearing nowhere. `BenchmarkPerplexityOutputParser` holds verbatim fixtures of both shapes.

### 3.1 The KLD base-logit cache

KL divergence needs a base-model logit file, which is tens of gigabytes. `BenchmarkKldBaseCache`:

- **Named by a digest, never by the key.** A `ModelContentFingerprint` is `v1:<64 hex>`, and `:` is illegal in a Windows path — NTFS would reinterpret the tail as an alternate data stream rather than reject it, so the failure would be a silently empty file. The cache file is `digest[3..35] + ".logits"` with a plaintext `.json` sidecar so the directory stays auditable.
- **Leased with `FileMode.CreateNew` + `FileOptions.DeleteOnClose`.** That is what makes the *crash* case right: the OS drops the handle, so the next run takes over instead of waiting on a lock nobody will release. A bare "does the lock file exist" check gets exactly that case wrong.
- **Published atomically** — written to `<name>.tmp.<invocationId>`, flushed to disk, then same-directory renamed, so a partial logit file never resolves as a measurement.
- **Reserved against `IFreeSpaceProbe`** before the base phase, refusing when free space minus the estimate leaves under the headroom, naming both numbers.

Disk estimate: **measured, not derived**. A real 10-chunk base file for `Qwen3.8-27B` (`n_vocab` 151 936) was 1 266 472 900 bytes over 777 912 320 logits — **1.628 B/logit**, not the format's 2.0. `BenchmarkFidelityPolicy.KldBytesPerLogit` is 1.75; 200 chunks is ~25.3 GB actual, not the 31.1 GB an f16 assumption promises.

### 3.2 The comparability gate is the whole cache key

`BenchmarkKldCacheKey` (`BenchmarkFidelityContracts.cs:91`) hashes **five** inputs: base-model fingerprint, corpus SHA-256, context tokens, chunk count, and `KldFormatVersion`. Four of them move without the fingerprint moving, and `kld_p99` is strongly chunk-count dependent — so gating on the base model alone would show a number measured on 50 chunks of one corpus beside one measured on 200 chunks of another.

A run stores the digest it was measured under (`kld_base_logits_digest`); the project computes the digest its **current** settings expect (`BenchmarkEndpointSupport.ExpectedKldDigest`). The figure is served only while the two match, otherwise `kldState = kld-stale` with the three KLD numbers **withheld** (`BenchmarkEndpointMapper.ToFidelity`). Perplexity is unaffected — it carries `perplexity_corpus_id` and its window is pinned.

`BenchmarkKldCacheKey` is the **only** place that expression exists; `KldDigest_IsComputedByExactlyOneExpression` fails the build if a second file computes it.

### 3.3 Attempts are immutable; the run carries a projection

Each measurement is a `BenchmarkFidelityAttempt` row at `Sequence` 1..n. Re-measuring **inserts a new attempt**; the run's flat fidelity columns are refreshed only from a `Succeeded` attempt that is the highest-sequenced succeeded one, under a CAS. So a stale re-measurement cannot overwrite newer numbers, a failed one leaves the previous numbers alone, and "which file produced this figure" stays answerable from either end.

Fidelity is measured **once per cell**, never per repeat — perplexity is deterministic given the same weights and argv, so N repeats buy N identical numbers at N× the cost. A warm-up records `fidelity_status = 'skipped'` rather than NULL: "covered by repeat 1" and "never asked for" are different facts.

The measurement is **seeded on primary success**, in the same transaction that terminalizes the primary and seeds the judge attempt (`BenchmarkStore.MarkPrimarySucceededAsync`). Freeze writes only the `skipped` markers: a work item inserted at freeze outlives the run it belongs to, so a primary that failed or was cancelled left hours of GPU work queued against a run with no answer. One `IsFidelityMeasuredCell` predicate serves freeze, the seed and `EnqueueMissingFidelityAsync`.

The run's `fidelity_status` follows its attempt through **every** transition — `queued` on the seed, `running` on the claim, a terminal value on terminalization, and `failed` when restart recovery fails an interrupted attempt. A status left `queued` with no attempt and no work item behind it makes every API report an active measurement forever: the poller never stops and the UI keeps re-measure disabled. The NUMBERS are separate and are never touched by recovery.

The executor's **measurement watchdog (2 h, a constructor parameter) is a failure, not a cancellation**. Its linked token throws the same `OperationCanceledException` an operator's stop does, so the classification is derived at mapping time in the repo's priority order: the caller's token is not cancelled, therefore the timer is ours, and the attempt is terminalized `Failed` with a reason.

### 3.4 Settings live outside the project freeze

The five fidelity columns are written by `IBenchmarkStore.UpdateProjectFidelityAsync`, reached through **`PATCH benchmarks/projects/{projectId}/fidelity`** — the one project write that deliberately ignores the freeze. The freeze protects what the existing runs were measured *against* (task, context, agent); these settings decide what gets measured *next*, and every stored number keeps its own digest. Changing the base model or chunk count therefore makes old figures read `kld-stale` rather than wrong; nothing is deleted and no attempt is rewritten. `measureExisting` is opt-in and queues one item per succeeded, non-warm-up, first-of-repeat-group run that has none — freeze's own per-cell rule — reporting the count.

The base model's **fingerprint is never client-writable**: it is an input to the comparability digest, so the service resolves it from the eligible-model catalog (registry facts, no re-hashing) and the executor re-verifies it against a verifying lease before measuring.

---

## 4. Verifiable rubric criteria — judging with no model

A rubric criterion carries a `Kind` and a `Config` (`BenchmarkJudgeVerifierContracts.cs:12`). `llm` is the default an absent value takes, so every criterion written before P2 keeps its meaning and its stored hash.

| Kind | Decided by |
|---|---|
| `llm` | the judge model, as before |
| `exact` | normalized string equality |
| `regex` | a pattern the policy compiled |
| `jsonSchema` | a structural subset — `type`, `properties`, `required`, `items`, `enum`, `const`, `additionalProperties` |
| `mathAnswer` | the final number, extracted in a documented order |
| `constraint` | IFEval-style word counts, substrings, format |
| `pythonTests` | **running the answer's code** against the operator's hidden tests in the compute sandbox (§4.3) |

`BenchmarkJudgeVerifierConfig.Parse` (`:92`) is the **one** parser, called by the policy validator at activation *and* by `BenchmarkJudgeVerifiers` at execution. A second parser is how an activation-time check and a run-time check drift into disagreeing about the same config.

**Safety properties worth not undoing:**

- Every regex is compiled `RegexOptions.NonBacktracking`. That is the whole ReDoS answer — matching is linear in the input, and backreferences, lookaround and atomic groups are **rejected at activation** while the operator is still looking at the form. The 250 ms timeout is belt, not the mechanism. (The fenced-JSON scanner in `BenchmarkJudgeVerifiers` is hand-written for the same reason: finding a fence needs a negative lookahead.)
- The JSON-schema check **refuses every keyword outside its subset**. Accepting `minLength` and not enforcing it would ship a criterion that silently passes answers it should fail; refusing keeps "accepted" and "enforced" the same set. No JSON-Schema dependency was added.
- **A verifier that cannot run throws and fails the judging. It never scores 0.** 0 is a score an answer can genuinely earn, so spelling "unmeasurable" that way is a lie the ranking then acts on.
- `mathAnswer` extraction order is `\boxed{}` → `####` → an "answer is" phrase → the last number, most explicit first and the last occurrence of each. A model that shows its working leaves several numbers behind and the final one it wrote is not reliably the one it meant. `\boxed{1/2}` and `\boxed{\frac{1}{2}}` both equal `0.5`.

### 4.1 Where the pre-pass runs, and what it skips

`BenchmarkJudgeExecutor` runs the verifiers **after** the frozen judge runtime is read and **before** the model lease, the capacity admission and any spawn (`BenchmarkJudgeExecutor.cs:112`).

- **All criteria verifiable** → nothing is leased, admitted or spawned. `CompleteVerifiedAsync` (`:118`) synthesizes the result and terminalizes. Live: an all-verifiable rubric judged score 100 with **exactly one** `llama-server ready for model` line in the whole session — the primary.
- **Mixed** → the model is shown a **filtered rubric** containing only its own criteria, because `BenchmarkJudgeResultParser.ReadCriteria` rejects a reply whose criteria array does not match the rubric's count. The verified scores (10 or 0) are merged back and `BenchmarkJudgeScoreCalculator.Compute` recomputes the 0..100 against the **full** rubric — which also rejects a merge that does not cover it, so the merge is checked rather than trusted.

### 4.2 The `verified:v1` sentinel

A judging that spawned nothing has no runtime to key on. Having *none* is not `execution-identity-incomplete`, which would unrank it forever, so such an attempt takes the constant `BenchmarkJudgeExecutionKey.VerifiedSentinel` (`BenchmarkJudgeExecutionIdentity.cs:102`).

**This is safe only because the judging `Mode` and every criterion's `Kind`/`Config` are inside `ComputePolicyHash`.** That is what makes one policy revision provably one rubric composition, so a constant key cannot merge attempts that were graded differently. Move any of the three out of the policy hash and the sentinel starts joining unlike things. `MarkJudgeSucceededAsync` applies it with `??=`, so a measured key written at launch is never overwritten.

> Adding a member to `BenchmarkJudgeRubricCriterionV1` changes the judge's **prompt** unless you stop it: the payload builder serializes the rubric with `DefaultIgnoreCondition.Never`. `BuildUserPayloadJson` strips `kind`/`config`, so a re-judge under a revision stored before P2 asks byte-identically the same question. The model never sees a verifiable criterion anyway.

### 4.3 `pythonTests` — execution scoring

The only kind that RUNS anything, and the only one that can be *unscorable* rather than merely failed. A judge model scoring code it never ran was the failure this replaces.

```json
{ "testCode": "assert solve(10) == 20", "exports": ["solve"], "timeoutSeconds": 30, "extract": "firstPythonFence" }
```

The candidate is extracted from the answer in a documented order — a `python`-tagged fence, then any fence, then the whole trimmed text — and the extracted text is stored in the attempt's verifier evidence, so a wrong extraction is visible rather than silent.

**Two processes in one jail.** A trusted **parent** (the gateway's `python -I -` on stdin) holds the nonce and the operator's `testCode`, calls `prctl(PR_SET_DUMPABLE, 0)` before spawning, runs the tests itself against a proxy, and prints exactly one nonce-marked verdict line. An untrusted **child** `exec`s the candidate and answers JSON `call`/`eval` requests over its own inherited pipes. Both are inside the same per-invocation bwrap jail; the new boundary is a *process* boundary inside it.

Why not one interpreter with a "trusted harness" namespace: a namespace is not a trust boundary against code in the same address space. That design was defeated by walking the ancestor frames for the nonce, writing a passing marker to `sys.__stdout__` and calling `os._exit(0)` before the real harness printed.

| Markers on the sandbox's stdout | Verdict |
|---|---|
| exactly 1, `failed == 0 && collected > 0` | **10** |
| exactly 1, otherwise | **0** |
| **0** | **0** — the parent never reached its final line. Denying a verdict is a failure |
| **≥ 2** | **0**, logged as forged |

The operator's tests reach the candidate only through a proxy: `candidate.solve(10)`, the `exports` shorthand `solve(10)`, `pycall(name, args, kwargs)`, or `pyeval(src)` — which runs arbitrary setup *in the child* and crosses only the final expression's value. The cost is stated rather than hidden: **arguments and returns must be JSON-serializable and exceptions match by name**. A child that names `SystemExit` gets an ordinary test failure, because the parent re-raises only a `builtins` `Exception` subclass.

`collected`/`passed`/`failed` are best-effort evidence, not the verdict: `unittest.TestCase` subclasses in the parent's namespace are run with a `TextTestRunner` and read as objects; a bare test script is one implicit case. Scoring is binary either way.

**Unscorable vs. zero.** The verifier calls `ExecuteDetailedAsync(request, requireResourceLimits: true, ct)` — stricter than the `run_python` tool, which passes `false`, because this is operator code executed unattended. Every refusal (`compute-disabled`, `invalid-request`, `no-isolation`, `no-resource-limits`, `no-jail-root`, an unprovisionable interpreter, or a failed `PR_SET_DUMPABLE`) fails the judging and gives the run `rankExclusionReason = verifier-unavailable`, which tells the operator to fix the node. A **timeout** is the opposite: the code ran and did not finish, which is a real result about the code, so it scores 0.

The `codeExecution` rubric preset (`GET benchmarks/rubric-presets`) ships this as one criterion at full weight, with the test code and exported names as placeholders to edit.

---

## 5. Pairwise judging and Bradley–Terry

Opt-in per project via the policy's `Mode`. Pointwise stays the default.

`BenchmarkPairwisePolicy` (`BenchmarkPairwisePlanner.cs:6`): `MaximumRuns = 12` per cohort (12·11 = **132** judge calls), a UI warning from `WarnAtRuns = 8`, and `MaximumTruncatedShare = 0.20` — a cohort in which more than a fifth of verdicts had a truncated side refuses to aggregate rather than publishing a biased score.

- **Pairs form within a task case, never across one.** `EnsurePairsAsync` (`:115`) groups eligible runs by `(TaskCaseId, TaskInputHash)` and pairs only inside a group: "which answer is better" is meaningless when the two answers are to different questions. In P2 a project is one case, so this is a no-op today and the contract P3 widens.
- Both presentation orders of every unordered pair are enqueued. Position swap is what removes position bias.
- Creation is **one transaction, idempotent** — a `UNIQUE` violation from a concurrent caller is swallowed, re-read and returned. `ReconcilePairwiseAsync` (`:169`) re-runs it at startup, healing a crash between "a primary succeeded" and "its pairs were enqueued", which would otherwise leave a cohort permanently one comparison short.

`BenchmarkBradleyTerry` (`BenchmarkBradleyTerry.cs:51`) is ~a dozen lines of MM iteration and one constant, with no dependency:

| Constant | Value | Why |
|---|---|---|
| `PriorPseudoCount` | `0.5` | a symmetric per-pair prior. Without it a run that wins everything has no finite MLE; with it every weight is positive and the fit is unique. It is a real thumb on the scale for a tiny cohort, and that is the intended direction — a 6-verdict cohort *should not* report 0 and 100 |
| `MaximumIterations` | `500` | sweeps cap. **Non-convergence refuses the fit** (`pairwise-unfitted`) rather than publishing a half-fit number |
| `ConvergenceTolerance` | `1e-10` | on log-strengths |
| `DefaultReplicates` | `1000` | cluster bootstrap, resampling **unordered pairs** so a swap pair is never split |
| `MinimumVerdicts` | `2` | below it a run reports `pairwise-insufficient` rather than a strength |

**A fit is one immutable row with one active pointer**, not N per-run writes. `BenchmarkPairwiseFit` stores the fitted set and every score; at most one row per `(revision, generation, case)` is active, enforced by a filtered unique index, and publication is one transaction. There are no per-run pairwise columns, so a torn publication — a ranking blended from two fits, every row internally consistent and the ordering wrong — is not a reachable state.

Ranking reads the scores out of the active fit while its `FitKey` still matches. The key covers policy revision, cohort generation, policy hash, both pairwise versions, the task case **and the judge execution identity**: a generation counter cannot tell a reader whether the fit behind a stored score used the same verdicts, the same prompt, the same case or the same judge runtime. Staleness is therefore one comparison against one row.

---

## 6. Ranking and quality sources

`BenchmarkQualityScoreSources` (`IBenchmarkStore.cs:1057`): `user` > `judge` | `pairwise` > `none`. An operator override always ranks; a judge score ranks only under the project's current policy revision, in that revision's live cohort generation, with the execution key the cohort was claimed with. Anything else is honestly unranked with a reason the UI can act on (`BenchmarkRunJudgeStates`, `:964`).

The whole rank read is a **flat-column scan** across four tables — nothing is decrypted to answer "is this run ranked?".

---

## 7. Export

`GET benchmarks/projects/{projectId}/export` (JSON) and `.../export.csv`, both Operator-gated. `BenchmarkExportProjection.SchemaVersion` is **3** (`BenchmarkExportEndpoints.cs:518`) since the P2 axes joined the record.

- **JSON** reuses `BenchmarkRunDetailResponse` verbatim — the same shape `GET benchmarks/runs/{runId}` returns — so an export and a live read of one run can never disagree. It adds `pairwiseFit` as ONE object: a fit is a single immutable row whose key covers the whole comparison set, so smearing it over the runs would say something untrue. Per-run strengths stay on the run rows.
- **CSV** is flat and its columns are **appended, never inserted**. Consumers read this export by column index, and an inserted column silently turns a sampling seed into a token count. P2 appended twelve fidelity columns and five pairwise ones.
- The export applies the **same** KLD comparability gate as the live read: a stale figure exports as `kldState=kld-stale` with its three cells empty and its digest still written, because the digest is the evidence for the withholding.
- Fidelity numbers use a six-decimal formatter, not the throughput formatter's three: the measured Q4_K_M/UD-Q3_K_XL gap is 6.7977 vs 6.9497 at standard errors near 0.074, so three decimals throws away exactly the digits that decide whether two quants separate.

---

## 8. Where things live

| Concern | Path |
|---|---|
| Executors, planner, fitter, contracts | `XE-Local-AI-Engine.Client.Application/Services/Benchmarks/` |
| Entities, configurations, store | `XE-Local-AI-Engine.Client.Persistence/{Entities,Configurations,Implementation,Stores}/` |
| Endpoints, DTOs, mappers | `XE-Local-AI-Engine.Client/Endpoints/Benchmarks/V1/` |
| Routes | `XE-Local-AI-Engine.Client/Endpoints/Common/LocalApiRoutes.cs:246` (`LocalApiRoutes.Benchmarks`) |
| Task items | `…/Persistence/Entities/BenchmarkTaskItem.cs`, `…/Persistence/Implementation/BenchmarkTaskItemHashing.cs`, `…/Application/Services/Benchmarks/BenchmarkTaskItemService.cs`, `…/Client/Endpoints/Benchmarks/V1/BenchmarkTaskItemEndpoints.cs` |
| React feature | `XE-Local-AI-Engine.Client.React/src/features/benchmarks/` |
| Fidelity corpus + licence | `tools/benchmark/corpus/`, `third-party/data/` |

Related pages: [Local Runtime and Providers](03-local-runtime-and-providers.md) for llama.cpp binaries and placement, [Data and Persistence](08-data-and-persistence.md) for the encryption interceptors every new encrypted column needs a row in, [API and Hubs](09-api-and-hubs.md) for the route surface, [Model Fit](07-model-fit.md) for eligibility.
