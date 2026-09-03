# Benchmarks — Task Suites, Frozen Runs, Discriminating Scores

> Reviewed: 2026-08-26 · Code-grounded.

The **benchmark** module measures a frozen suite of questions against many local models and ranks the results. Quant fidelity, server-side verifiers and pairwise judging make the scores discriminating; task suites make the module a real harness: a project asks **N questions**, a launch fans out over them, the unit that ranks is a **combination of model settings** rather than a single run, and the difference between two combinations comes with an interval.

Four rules govern everything below and are worth stating before the mechanics:

- **Quality-only ranking.** Throughput, perplexity, KL divergence, top-token agreement and NIAH recall are **display axes**. None of them ever reaches `Rank` (`BenchmarkStore`, `LoadRankingAsync`/`ComputeQuality`).
- **A number is shown only while it is comparable.** Every display axis carries its own comparability key, and a figure whose key no longer matches the project's current one is **withheld**, not flagged. A number a reader can still see is a number they will still compare.
- **A partial measurement is not a small measurement.** A cell missing one item, a cell measured under a different item set, and a verifier that could not run are all *unranked with a reason* — never scored low.
- **Identity is stamped at freeze, never re-derived at read time.** What a run was asked, and what the whole question set was when it was asked, are copied onto the run. A read compares the copies against what the project holds now.

---

## 1. The model — project, item, cell, run

| Thing | What it is |
|---|---|
| **Project** | The frozen measurement: 1..N task items, a context window, an agent, a judge policy, fidelity settings |
| **Task item** (`BenchmarkTaskItem`) | One question. Kind `prompt`, or the generator `niah` and its generated `niahCase` leaves |
| **Cell** | One *combination of settings* measured over the whole item suite — one model × one KV-cache type × one repeat. **The cell is the unit that ranks.** The UI calls it a *combination* |
| **Run** (`BenchmarkRun`) | One model answering one item once, under a byte-identical runtime snapshot |
| **Judging** | A pointwise attempt, a pairwise comparison, or no model at all (§5.2) |

A cell's score is the **mean of its runs' quality scores over the project's scorable items**; a run's own `qualityScore` stays its own and is still shown beside it.

A single-item project is the degenerate case and behaves exactly as it always did: one run per cell, every number identical to the pre-suite build. The migration test compares the project and run rows byte-for-byte across the schema change and asserts no item row is invented.

---

## 2. Task items — the questions a project asks

- An item carries a **kind**, a **revision**, a plaintext **input hash**, a `countsTowardScore` flag, and four encrypted payloads: prompt, reference answer, per-criterion verifier override, generator config. Each payload has its own AAD column name — a verifier config holds expected answers and must never be servable as the prompt.
- **The store owns identity.** Index, revision, input hash and the project's item-set hash are computed on every write (`BenchmarkTaskItemHashing`, in Persistence); the service and the endpoints cannot name them. A client that could would be able to present an answer to an old question as an answer to the current one.
- `input_hash` = `v1:` + SHA-256 over `(kind, revision, prompt, reference, verifier, generator)`. `benchmark_projects.task_item_set_hash` = `v1:` + SHA-256 over the **leaf** items ordered by their immutable **`Id`, not by `Index`** — so **adding or deleting** an item moves it and **reordering does not**. A cosmetic drag-and-drop must not unrank a completed suite. A reorder therefore bumps no revision, moves no hash and resets no cohort; it is a two-pass renumber (into a disjoint index range, then into place) because the unique `(project_id, index)` index is enforced per statement.
- **A moved set hash resets the rank cohort**, through the same `ResetCurrentCohortAsync` path a judge-policy activation uses, and bumps the project version. The project score is a mean over the item set, so a different set is a different score.
- **Item writes are refused while any of the project's work is Queued or Running.** That is the primary guard; the run-level staleness stamps (§4.2) are the safety net for completed history. An item's **kind** cannot be changed in place — that is a different item wearing the old identity, so it is delete-and-create, where the set hash moves for it.
- **Creation is atomic.** `IBenchmarkStore.CreateProjectAsync` takes the initial item set and writes it in the project's own transaction, so a project never exists without a question to ask. `GetOrCreateItemsAsync` survives **only** as the legacy backfill for projects created before task items existed, and exactly one endpoint may call it (`GET …/items`) — a migration cannot do it instead, because it runs without the node encryption key and `prompt_json` is AAD-bound to its own item's id. It deliberately leaves the project's set hash **null**: materializing item 0 changes nothing about what the project asks, so it must not move the value every historical run is compared against.
- **An absent optional payload is NULL, never an empty blob**, normalized in the store. The C# trap that produced empty blobs: `cond ? null : someByteArray` assigned to a `ReadOnlyMemory<byte>?` has natural type `byte[]?`, and a null array converts to an **empty** `ReadOnlyMemory`, not to a null nullable. Cast the non-null arm to `(ReadOnlyMemory<byte>?)`. The payload bytes are inside the item's input hash, where an empty-vs-absent difference makes an untouched item look edited.
- Cap: **20 leaf items** (`BenchmarkTaskItemService.MaxTaskItems`), counted over leaves so a generator's cases each count.

Routes: `GET/POST benchmarks/projects/{projectId}/items`, `PUT/DELETE …/items/{itemId}`, `PUT …/items/order`. The project detail carries `taskItems[]` and `taskItemSetHash`.

### 2.1 Long-context probes — `niah` and `niahCase`

A `niah` item is a **generator, never a run target**. It expands into one `niahCase` child per (length × depth) pair **when the item is written**, and each case is an ordinary task item with its own id, index, revision and input hash. That is the whole design: cell completeness, the item caps, the run cap, the staleness exclusions, the paired bootstrap's shared-item set and the export all reach a case without any of them knowing what NIAH is.

A case generated during a *freeze* would have none of that: nothing to stamp in `task_item_id`, nothing to hash into `task_input_hash`, nothing for the caps to count, and no way for the ranking read to know how many probes a cell owed.

Generator config, on the `niah` item:

```json
{ "contextTokens": [8192, 32768], "needleDepthPercent": [10, 50, 90],
  "needleTemplate": "The secret passcode for {city} is {code}.",
  "questionTemplate": "What is the secret passcode for {city}?",
  "criterionId": "recall", "seed": 0, "countsTowardScore": false }
```

- **Deterministic and seeded.** The haystack is drawn from the shipped wikitext-2 corpus (`tools/benchmark/corpus/`, CC BY-SA 3.0, attributed in the prompt) through a fixed SplitMix64 seeded from a SHA-256 of `(parentItemId, contextTokens, depth, seed)`. `Random` is deliberately not used: its sequence is an implementation detail the runtime has changed before, and a case whose text shifted with a .NET upgrade would move its own input hash and unrank every answer ever given to it.
- **Depth is a position in the text**, measured by weighted characters, not an index into the sentence list — wikitext sentences differ in length by an order of magnitude, so "the 50th of 100 sentences" and "halfway through the document" are different positions. The test asserts the needle's character offset is within 5 points of the requested depth.
- **Length is approximate and says so.** The haystack is built to **90 %** of the requested length and the case is labelled `NIAH ≈32k @ 50%`. `ChunkTokenApproximation` under-counts English prose, so building to the full request would overshoot the real tokenization and truncate the tail — and a needle that fell off the end measures the window, not the model. The `≈` survives into the UI. Live: the 8k probes estimated 7441–7452 tokens, the 32k probes 29545–29581.
- **A case keeps its own parameters in its `GeneratorConfigJson`.** Without them the freeze cannot re-check a probe's length without parsing a haystack back out of a prompt, and the frontend has no label. A case whose parameters cannot be **read** is refused rather than skipped — a probe nothing can vouch for must not slip past the check that stops it measuring the context window.
- **Refused twice**, and both refusals name both numbers: at expansion, while the operator is still looking at the form, and again at freeze, because the project's window is editable afterwards. `BenchmarkNiahGenerator.MinimumContextTokens` is 512 — under that there is nothing to hide a needle in and the depths stop being distinguishable.
- **Scored by `exact`, with no judge model.** Each case writes its own override of the criterion named by `criterionId` — the case supplies the expected passcode, the project's rubric supplies the kind. See §5.2's override rule.
- **`countsTowardScore` for the cases lives in the generator config, not on the item draft.** The draft's own default is `true`, right for an authored prompt and wrong for a probe, and a bool property cannot distinguish "omitted" from "explicitly true".
- **Only the generator is writable.** Editing a `niah` item deletes and regenerates its cases in one transaction, so a case never describes parameters its generator no longer has; deleting it takes the cases with it (explicitly ordered — foreign keys are off on this connection and no cascade fires). A `niahCase` cannot be created, edited or deleted on its own: written by hand it would carry a parent that does not describe it, and edited, the change would survive exactly until the next re-expansion.
- Cases count individually against `MaxTaskItems` (20 leaves) and `MaxRunsPerRequest` (100). A 2×3 probe costs **6**, not 1.

Not built, deliberately: multi-needle, retrieval-order and aggregation variants of RULER.

---

## 3. Freeze — what a launch produces

`BenchmarkRunFreezeService` turns a project plus a model choice into runs that are already fully described before any of them starts.

- The binary probe and the llama-server variant selection are memoized for the whole launch through `BenchmarkFreezeScope`, so a launch matrix compares cells that saw **one** runtime, not N. A second binary inspection could straddle a runtime swap and freeze two different launch answers into one measurement.
- **The agent runtime is resolved once per LEAF ITEM.** The task text is `IAgentDefinitionResolver`'s retrieval query, so the resolved system prompt and the skills behind it can legitimately differ per item, and the dependency set guarding the commit derives from that resolution — hence one `FreezeCommitGuard` per item (the store de-duplicates guards by reference).
- Every run stores a **`BenchmarkRuntimeSnapshotV1`** — model identity and member hashes, resolved launch arguments, KV-cache types, sampling. The record is **frozen at schema v1 and validated by re-hashing its own serialized bytes** against an embedded `ConfigurationHash`. Adding a member to it, or to any nested record, makes every stored run fail to deserialize. Per-run facts go in **flat columns** instead — that is why `RepeatGroupId`, `RepeatIndex`, `RepeatMode`, the four identity stamps and the whole fidelity projection are columns rather than snapshot members.
- **The snapshot cache is keyed `(itemId, seedValue)`, not on the seed alone.** Answer-variance repeats already require a per-seed dictionary; suite fan-out also requires the item id in that key. Without it, every item receives the *first* item's snapshot — every run answers item 0's prompt while its `task_item_id` column claims otherwise, and nothing fails loudly. `Start_WithThreeItems_FreezesThreeDistinctCoreTasks` is the guard.
- **Commands are built repeat-major, item-minor.** A partially drained queue then yields whole comparable cells rather than one item spread across every cell.
- Caps: `MaxRepeatCount = 10`, `MaxRunsPerRequest = 100` — the latter checked in a pre-flight that names the computed count. A whole repeat group is inserted in **one** transaction by `IBenchmarkStore.StartRunsAsync`, which checks the project version once and queues the work items FIFO. Per-run CAS would leave orphan queued runs behind on a partial failure.

### 3.1 Cell identity

```
cellGroupId = repeatGroupId ?? (leafItems.Count > 1 ? Guid.NewGuid() : null)
cell_key    = "cell:" + cellGroupId + ":" + (repeatIndex ?? 1)
```

`BenchmarkRunFreezeService` mints `repeatGroupId` only when `repeatCount > 1 || warmup`. **A cell exists whenever one freeze produces more than one run per model, which is not the same condition as a repeat group** — a 3-item single-repeat suite, the ordinary way an operator runs one, had NULL there, fell back to the singleton key, and produced three cells each missing two of three items: every cell `item-incomplete`, the project ranking nothing. Where a repeat group *does* exist the cell group **is** that GUID, so there is one identity and nothing to keep in sync, and `repeat_group_id`/`repeat_index` keep their exact previous semantics.

Two singleton forms exist and both rank on their own run: `cell:<runId>` (a one-item, one-repeat freeze) and `run:<id>` (backfilled by the migration onto every pre-suite run).

### 3.2 The four stamps

Every run is stamped at freeze with four immutable identities on `benchmark_runs`:

| Column | What it pins |
|---|---|
| `task_item_id` | which leaf it answered (nullable — a pre-suite run names none) |
| `cell_key` | **NOT NULL** — which measurement cell its per-item score aggregates into |
| `task_input_hash` | a copy of the item's input hash: *exactly what it was asked* |
| `task_item_set_hash` | a copy of the project's: *what the whole question set was when this cell was measured* |

Three are NOT NULL because a missing stamp would read as "belongs with everything else": a null `cell_key` would drop every ungrouped run of a project into one anonymous bucket and average their scores together, silently. The two hash columns default to `'v1:legacy'`, which is also what a legacy run is compared **against**, so it is never read as stale.

---

## 4. Ranking — the cell is the unit

`BenchmarkQualityScoreSources`: `user` > `judge` | `pairwise` > `none`. An operator override always ranks; a judge score ranks only under the project's current policy revision, in that revision's live cohort generation, with the execution key the cohort was claimed with. Anything else is honestly unranked with a reason the UI can act on (`BenchmarkRunJudgeStates`).

The whole rank read is a **flat-column scan** across four tables — nothing is decrypted to answer "is this run ranked?".

### 4.1 How a cell is scored

`BenchmarkStore.LoadRankingAsync`, in order:

1. **Warm-ups are stamped with a cell key and then dropped BEFORE grouping.** A warm-up sits at repeat index 0 and so forms its own cell, which could only ever be complete if every leaf item also got a warm-up run — and would otherwise sit in the ranked denominator forever. Stamping an identity is not a ranking decision.
2. **The set-hash check comes first** (`item-set-revised`, §4.2).
3. **A cell whose runs name no item at all** is a pre-suite cell and is ranked on its own run. Without this, materializing item 0 through the `GET …/items` backfill would turn every historical singleton into an `item-incomplete` cell and unrank a project's whole history *on a read*.
4. **A project with zero scorable leaves reports `no-score`, not `item-incomplete`.** A pure long-context probe has nothing to rank, which is not the same as a cell missing an item; the "incomplete" badge would send the operator looking for a question nobody asked.
5. **Completeness:** a cell ranks only when **every scorable item produced a rankable score**. Partial credit is refused for the reason a truncated run is already refused outright — scored on the easy items alone, a model that ran out of budget on the hard one outranks one that attempted everything.
6. **Mean** of the contributing runs' quality, rounded half-away-from-zero, matching `ComputeQuality`'s own arithmetic.
7. **Dense rank over cells:** equal scores share a position and the next distinct score is the next integer. `BenchmarkRankCohort.RankedCount`/`TotalScored` count **cells**. Every run reports its cell's rank and its cell's mean beside its own score.

A **display-only leaf** (`countsTowardScore = false`) is kept out of the mean by intersecting with the scorable id set — it does **not** null the run's `QualityScore`, because the recall axis reads it. **Every other quality aggregate has to apply the same intersection itself**, or it silently ranks on a NIAH recall figure (§5.4 is the one that had to).

### 4.2 Exclusion precedence

**warm-up > `item-revised` > `item-set-revised` > user score > (truncated | incomplete) > judge reasons.**

The two stale stamps sit **above** the operator's user-score override; truncation still sits below it. An operator who read a truncated answer and scored it anyway overruled the machine about a fact they could see; an operator who scored an answer to a question that has since been edited, or to one item of a suite whose membership has since changed, has not — they had no way to notice either moved. The more specific cause wins, so `item-revised` beats `item-set-revised`.

| Reason | Meaning |
|---|---|
| `warm-up` | never a contender |
| `item-revised` | the item's input hash moved: the answer is to a question the project no longer asks |
| `item-set-revised` | the project's set hash moved: the cell was scored against a suite the project no longer has |
| `item-incomplete` | at least one scorable item of this cell has no rankable answer |
| `no-score` | nothing in this project counts toward a score |
| `verifier-unavailable` | a deterministic verifier could not run on this node — the answer was never checked (§5.2) |
| `override-unmatched` | the item's verifier override names a rubric criterion that no longer exists, so nothing it asked for was applied |
| `truncated` / `incomplete` | see §7.1 |
| `pairwise-*` | see §5.3 |

**The set-hash check must run before the completeness check, and it is the only thing that catches a DELETE.** Completeness is judged against a mutable set: delete the item a cell never answered and its two survivors keep matching their own input hashes, satisfy every per-item check, and constitute a *complete* two-of-two cell whose mean is over a suite the model was never scored on. The project-level hash cannot see this because it **is** the thing that changed; only the per-run copy stamped at freeze can.

### 4.3 The cell table

`GET benchmarks/projects/{projectId}/cells` is its own route, not a shape on the run listing: a run list cannot say which items a cell is **missing**, and the absence is the answer. It also carries `scorableItemCount`, which is not derivable from the cells alone. `ListCellsAsync` skips a run present in the run query but absent from the ranking map rather than throwing, so a freeze landing between the two reads is ordinary rather than a 500.

The per-run write paths compare against `BenchmarkRunIdentity.Unstamped`: `ToRecordWithJudge` projects a row a write has just touched, and item edits are refused while work is live, so such a row cannot be stale. `GetRunAsync` and `LoadRankingAsync` do the real comparison.

---

## 5. Scoring

### 5.1 The pointwise judge

A rubric of weighted criteria, scored 0..10 each, recomputed to 0..100 by `BenchmarkJudgeScoreCalculator.Compute`. `ComputePolicyHash` hashes the **prompt version**, not the prompt text, so any wording change must bump `BenchmarkJudgePolicyVersions.PromptVersion`; reads tolerate stored older versions structurally so a project page can still load and offer a re-save, while writes, activation and execution validate strictly.

`PUT projects/{id}/judge` answers the re-judge precondition **before** building the policy, because building it takes the verifying model lease that re-hashes every member file — 57 s for a 22 GB judge. An unchanged draft is recognized without a lease by rebuilding it against the stored model identity and comparing hashes, with the model *name* compared separately. Ceiling: a judge file changed on disk under an unchanged name reads as unchanged until the policy is next actually built.

### 5.2 Verifiable criteria — judging with no model

A rubric criterion carries a `Kind` and a `Config` (`BenchmarkJudgeVerifierContracts.cs`, `BenchmarkJudgeCriterionKinds`). `llm` is the legacy-compatible default for an absent value, preserving the meaning and stored hash of criteria without those fields.

| Kind | Decided by |
|---|---|
| `llm` | the judge model |
| `exact` | normalized string equality |
| `regex` | a pattern the policy compiled |
| `jsonSchema` | a structural subset — `type`, `properties`, `required`, `items`, `enum`, `const`, `additionalProperties` |
| `mathAnswer` | the final number, extracted in a documented order |
| `constraint` | IFEval-style word counts, substrings, format |
| `pythonTests` | **running the answer's code** against the operator's hidden tests in the compute sandbox (§5.2.3) |

`BenchmarkJudgeVerifierConfig.Parse` is the **one** parser, called by the policy validator at activation *and* by `BenchmarkJudgeVerifiers` at execution. A second parser is how an activation-time check and a run-time check drift into disagreeing about the same config.

**Safety properties worth not undoing:**

- Every regex is compiled `RegexOptions.NonBacktracking`. That is the whole ReDoS answer — matching is linear in the input, and backreferences, lookaround and atomic groups are **rejected at activation** while the operator is still looking at the form. The 250 ms timeout is belt, not the mechanism. (The fenced-JSON scanner in `BenchmarkJudgeVerifiers` is hand-written for the same reason: finding a fence needs a negative lookahead.)
- The JSON-schema check **refuses every keyword outside its subset**. Accepting `minLength` and not enforcing it would ship a criterion that silently passes answers it should fail; refusing keeps "accepted" and "enforced" the same set. No JSON-Schema dependency was added.
- **A verifier that cannot run throws and fails the judging. It never scores 0.** 0 is a score an answer can genuinely earn, so spelling "unmeasurable" that way is a lie the ranking then acts on.
- `mathAnswer` extraction order is `\boxed{}` → `####` → an "answer is" phrase → the last number, most explicit first and the last occurrence of each. A model that shows its working leaves several numbers behind and the final one it wrote is not reliably the one it meant. The value keeps thousands separators through the capture and is cleaned afterwards (excluding them read `$1,234,567` as 1). `\boxed{1/2}` and `\boxed{\frac{1}{2}}` both equal `0.5`.

#### 5.2.1 Per-item overrides

At judging, a criterion's config **and** the reference answer resolve as **item override ?? policy config**. A suite whose items all share one expected answer could only ever ask one question, so this is what makes a multi-item verifiable suite possible at all — and it is what makes NIAH recall measurable.

It is deliberately **not** a policy-hash change: the override lives on the item, so it is already inside the item's input hash and the project's set hash, which is what unranks stale answers to it. Moving the policy hash would force a project-wide re-judge of items that did not change. An item deleted after its run was frozen judges under the policy's own config rather than failing the attempt — the ranking read already excludes such a run as `item-set-revised`.

#### 5.2.2 Where the pre-pass runs, and the `verified:v1` sentinel

`BenchmarkJudgeExecutor` runs the verifiers **after** the frozen judge runtime is read and **before** the model lease, the capacity admission and any spawn.

- **All criteria verifiable** → nothing is leased, admitted or spawned. `CompleteVerifiedAsync` synthesizes the result and terminalizes. Live: an all-verifiable rubric judged score 100 with **exactly one** `llama-server ready for model` line in the whole session — the primary.
- **Mixed** → the model is shown a **filtered rubric** containing only its own criteria, because `BenchmarkJudgeResultParser.ReadCriteria` rejects a reply whose criteria array does not match the rubric's count. The verified scores (10 or 0) are merged back and the 0..100 is recomputed against the **full** rubric — which also rejects a merge that does not cover it, so the merge is checked rather than trusted.

A judging that spawned nothing has no runtime to key on. Having *none* is not `execution-identity-incomplete`, which would unrank it forever, so such an attempt takes the constant `BenchmarkJudgeExecutionKey.VerifiedSentinel` = `verified:v1`.

**This is safe only because the judging `Mode` and every criterion's `Kind`/`Config` are inside `ComputePolicyHash`.** That is what makes one policy revision provably one rubric composition, so a constant key cannot merge attempts that were graded differently. Move any of the three out and the sentinel starts joining unlike things. `MarkJudgeSucceededAsync` applies it with `??=`, so a measured key written at launch is never overwritten.

> Adding a member to `BenchmarkJudgeRubricCriterionV1` changes the judge's **prompt** unless you stop it: the payload builder serializes the rubric with `DefaultIgnoreCondition.Never`. `BuildUserPayloadJson` strips `kind`/`config`, so a re-judge under a legacy revision stored without those fields asks byte-identically the same question. The model never sees a verifiable criterion anyway.

#### 5.2.3 `pythonTests` — execution scoring

The only kind that RUNS anything, and the only one that can be *unscorable* rather than merely failed. A judge model scoring code it never ran was the failure this replaces.

```json
{ "testCode": "assert solve(10) == 20", "exports": ["solve"], "timeoutSeconds": 30, "extract": "firstPythonFence" }
```

Caps: `testCode` ≤ 4 000 chars (at **activation**), ≤ 16 exports, timeout 1..600 s. The candidate is bounded dynamically by what is left inside `ComputeToolDefinition.CodeMaxLength`; an over-long extracted solution is a candidate failure naming both numbers, not a sandbox refusal.

The candidate is extracted in a documented order — a `python`-tagged fence, then any fence, then the whole trimmed text — and the extracted text is stored in the attempt's verifier evidence, so a wrong extraction is visible rather than silent.

**Two processes in one jail.** A trusted **parent** (the gateway's `python -I -` on stdin) holds the nonce and the operator's `testCode`, calls `prctl(PR_SET_DUMPABLE, 0)` **before** spawning, runs the tests itself against a proxy, and prints exactly one nonce-marked verdict line. An untrusted **child** `exec`s the candidate and answers JSON `call`/`eval` requests over its own inherited pipes. Both are inside the same per-invocation bwrap jail; the new boundary is a *process* boundary inside it. The parent is PID 1 of the jail's PID namespace, so killing it tears the namespace down and yields no stdout at all.

Why not one interpreter with a "trusted harness" namespace: **a namespace is not a trust boundary against code in the same address space.** That design was defeated by walking the ancestor frames for the nonce, writing a passing marker to `sys.__stdout__` and calling `os._exit(0)` before the real harness printed — and by variants needing no frame walk at all (patch `builtins.print`, patch `json.dumps`, replace `unittest` in `sys.modules`).

| Markers on the sandbox's stdout | Verdict |
|---|---|
| exactly 1, `failed == 0 && collected > 0` | **10** |
| exactly 1, otherwise | **0** |
| **0** | **0** — the parent never reached its final line. Denying a verdict is a failure |
| **≥ 2** | **0**, logged as forged |

- **Everything variable crosses as base64**, decoded only inside the child. A candidate carrying triple quotes, a trailing backslash or a NUL would otherwise escape its literal and land as code in the trusted parent.
- The operator's tests reach the candidate only through a proxy: `candidate.solve(10)`, the `exports` shorthand `solve(10)`, `pycall(name, args, kwargs)`, or `pyeval(src)` — which runs arbitrary setup *in the child* and crosses only the final expression's value. The cost is stated rather than hidden: **arguments and returns must be JSON-serializable and exceptions match by name.** A child-named exception is re-raised only when `getattr(builtins, name)` is a `type` subclassing `Exception` — so a child naming `SystemExit` gets an ordinary test failure, not an injected escape. The test phase is wrapped in `except BaseException` with the marker printed from `finally`.
- `collected`/`passed`/`failed` are best-effort evidence, not the verdict: `unittest.TestCase` subclasses in the parent's namespace are run with a `TextTestRunner` and read as **objects**; a bare test script is one implicit case. No framework output is ever parsed. Scoring is binary either way.
- **The templates are minified before composition** (blank lines and whole-line comments dropped), because the composed parent must fit `CodeMaxLength` alongside the candidate and the tests. Sound only while neither template holds a multi-line string literal — pinned by `HarnessTemplatesCarryNoMultiLineStringLiteral`.
- **The adversarial suite only means something against a real process.** A gateway substitute returns whatever the test author decided a sandbox returns, so it proves the parser and never the boundary. 30 unit rows run the real generated parent and child through a local interpreter; `BenchmarkPythonTestsLiveTests` runs the same candidates in the real bwrap jail behind `XE_COMPUTE_LIVE=1`.

**Unscorable vs. zero.** The verifier calls `ExecuteDetailedAsync(request, requireResourceLimits: true, ct)` — stricter than the `run_python` tool, which passes `false`, because this is operator code executed unattended. Every refusal (`compute-disabled`, `invalid-request`, `no-isolation`, `no-resource-limits`, `no-jail-root`, `environment-unavailable`, `containment-unavailable`, or a failed `PR_SET_DUMPABLE`) fails the judging with the `verifier-unavailable: ` prefix, which `BenchmarkStore.RankExclusionReason` turns into the `verifier-unavailable` exclusion — telling the operator to fix the node. A **timeout** is the opposite: the code ran and did not finish, which is a real result about the code, so it scores 0.

The `codeExecution` rubric preset (`GET benchmarks/rubric-presets`) ships this as one criterion at full weight, with the test code and exported names as placeholders to edit. The compute boundary itself — the bwrap jail, the venv, the isolation refusals — is [Compute Tools](19-compute-tools.md).

### 5.3 Pairwise judging and Bradley–Terry

Opt-in per project via the policy's `Mode`. Pointwise stays the default.

`BenchmarkPairwisePolicy`: `MaximumRuns = 12` per cohort (12·11 = **132** judge calls), a UI warning from `WarnAtRuns = 8`, and `MaximumTruncatedShare = 0.20` — a cohort in which more than a fifth of verdicts had a truncated side refuses to aggregate rather than publishing a biased score.

- **Pairs form within a task case, never across one.** `BenchmarkPairwisePlanner.EnsurePairsAsync` groups eligible runs by `(TaskCaseId, TaskInputHash)` and pairs only inside a group: "which answer is better" is meaningless when the two answers are to different questions. The grouping is a natural no-op for a single-case project and load-bearing for a suite.
- Both presentation orders of every unordered pair are enqueued; position swap is what removes position bias, and the swap bookkeeping lives in exactly one place (`BenchmarkPairwiseResultParser.ToCanonicalVerdict`). Getting it backwards inverts exactly half of every cohort's verdicts. Live, two of six pairs split 1–1 across the orders.
- Creation is **one transaction, idempotent** — a `UNIQUE` violation from a concurrent caller is swallowed, re-read and returned. `ReconcilePairwiseAsync` re-runs it at startup, healing a crash between "a primary succeeded" and "its pairs were enqueued".

`BenchmarkBradleyTerry` is ~a dozen lines of MM iteration and one constant, with no dependency:

| Constant | Value | Why |
|---|---|---|
| `PriorPseudoCount` | `0.5` | a symmetric per-pair prior. Without it a run that wins everything has no finite MLE; with it every weight is positive and the fit is unique. A real thumb on the scale for a tiny cohort, and the intended direction — a 6-verdict cohort *should not* report 0 and 100 |
| `MaximumIterations` | `500` | sweeps cap. **Non-convergence refuses the fit** (`pairwise-unfitted`) rather than publishing a half-fit number |
| `ConvergenceTolerance` | `1e-10` | on log-strengths |
| `DefaultReplicates` | `1000` | cluster bootstrap, resampling **unordered pairs** so a swap pair is never split |
| `MinimumVerdicts` | `2` | below it a run reports `pairwise-insufficient` |

The mapping is `round(100 · σ(θᵢ − mean θ))`, not `100 · p / max(p)` — the latter pins the winner at 100 forever and reintroduces exactly the saturation pairwise exists to remove. Only the largest connected component of the comparison graph is published; strengths from two components are not on one scale.

**A fit is one immutable row with one active pointer**, not N per-run writes. At most one `BenchmarkPairwiseFit` per `(revision, generation, case)` is active, enforced by a filtered unique index, and publication is one transaction — so a torn publication (a ranking blended from two fits, every row internally consistent and the ordering wrong) is not a reachable state. A **refusal is published as a fit row** with the reason and no scores, because a fit that simply fails to appear is indistinguishable from a cohort still judging.

Ranking reads the scores out of the active fit while its `FitKey` still matches. The key covers policy revision, cohort generation, policy hash, both pairwise versions, the task case **and the judge execution identity** — a generation counter alone cannot tell a reader whether the fit behind a stored score used the same verdicts, prompt, case or judge runtime.

Measured on this box: the pointwise judge scored **all four quants 100** and dense-ranked them all rank 1 — zero discrimination — while the pairwise fit over the same four answers produced UD-Q3_K_XL 69 [56, 90], Q5_K_M 60 [46, 77], Q6_K 51 [23, 70], Q4_K_M 23 [9, 34]. ~30.7 s per comparison; budget a 12-run cohort at roughly 68 minutes.

### 5.4 Comparing two cells — the paired-difference interval

`GET benchmarks/projects/{projectId}/compare?cellKeys=…&cellKeys=…` (Operator, 2..6 distinct keys) returns the named cells in the shape `GET …/cells` already serves, plus one `pairedDeltas` entry per unordered pair. The difference is a **read-time projection** — nothing about a comparison is stored, `IBenchmarkStore` gained no member for it, and the number is always recomputed from the scores the project holds now.

- **The resampling unit is the item, drawn with both cells' scores for it** (`BenchmarkPairedBootstrap.Estimate`): `δₖ = qualityA(k) − qualityB(k)` over the shared items, B = 2000 replicates, percentile 2.5 / 97.5 by nearest rank, seeded at 0 like the Bradley–Terry bootstrap. Resampling the two cells independently would discard the pairing and re-inflate the interval by exactly the between-item variance a task suite exists to hold constant — two cells that alternate wins item by item and two cells six points apart on every item would come out looking alike.
- **Shared items are ordered (task-item index, then item id) before the draw.** The bootstrap draws by index, so an unordered shared set would make a seeded interval irreproducible across reads even though the multiset it samples from is unchanged.
- **A shared item needs a rankable score on both sides.** `item-revised`, `item-set-revised`, truncated and unjudged runs carry a null quality and take their item *out* of the comparison rather than into it with a guessed number — so a cell excluded wholesale shares nothing with anybody and yields no delta at all. One revised item costs one item, not the whole comparison.
- **A leaf with `countsTowardScore = false` is left out of the delta**, exactly as it is left out of the cell mean — the endpoint loads the item rows and intersects, because §4.1's rule does not null the run's score. This was a live bug until it was fixed, and it was latent until NIAH made such a leaf reachable. `sharedItemCount` counts scoring items only.
- **Below three shared items (`MinimumSharedItems`) there is no entry, not a zero.** A delta of 0 with no interval is indistinguishable from a measured tie, so the absence is the contract: the client renders "too few shared items" from the missing entry and **"not separated by this suite"** from `separated: false`, and re-derives neither from the bounds. `Separated` is false exactly when 0 lies inside the interval — an interval that merely touches zero is not a separation.
- **A duplicate `cellKeys` value is a 400.** A cell against itself is a delta of exactly 0 with a zero-width interval — a true statement that reads as a finding. An unknown key is a 400 that **names** the key.
- Quality only, like every other ranking read.

### 5.5 The recall axis

NIAH cases are **excluded from the project mean by default** (`countsTowardScore: false`). Recall is a capability, not quality: averaging 0-or-10 needle recall into a rubric mean says a model that missed the needle wrote a worse answer. The cases are still scored and still shown — **each case run's own `qualityScore` is the recall axis**, and the cell table reports all of a cell's runs beside a mean they did not enter.

Live: a 7-leaf project (one authored prompt + six cases) froze 7 runs into **one** cell; all succeeded, recall 6/6 on Qwen3.8-27B Q4_K_M, `scorableItemCount` **1**, the cell's quality **100** = the authored item's score alone, rank 1. Without the per-item override (§5.2.1) every case would have been graded against the policy's placeholder and scored 0.

---

## 6. Quant fidelity — perplexity and KL divergence (display only)

`BenchmarkFidelityExecutor` runs `llama-perplexity` against the run's **own frozen placement** (`--n-gpu-layers`, `--tensor-split`, `--override-tensor`, `--cache-type-k/-v`, `--flash-attn`) but at a **pinned 512-token window** (`BenchmarkFidelityPolicy.ContextTokens`). The window is pinned because perplexity is only comparable at a fixed window and every published llama.cpp/Unsloth/bartowski number uses 512; the placement knobs are replayed because they are exactly what differs between the runs being compared.

Chunks: `DefaultChunks = 200`, range `50..655`. On this box, 200 chunks of the shipped wikitext-2 corpus separated `Qwen3.8-27B` Q4_K_M (**6.7977 ± 0.07405**) from UD-Q3_K_XL (**6.9497 ± 0.07550**) with non-overlapping bands; at the 50-chunk floor the errors roughly double and the two overlap.

**Write the parser against the binary, not the README.** A `--kl-divergence` run prints no `Final estimate` line at all — its perplexity is `Mean PPL(Q)` inside the statistics block; the statistics blocks separate a value from its error with `±` while the plain line uses `+/-`; and top-token agreement is printed as `Same top p`, with the word "agreement" appearing nowhere. `BenchmarkPerplexityOutputParser` holds verbatim fixtures of both shapes.

### 6.1 The KLD base-logit cache

KL divergence needs a base-model logit file, which is tens of gigabytes. `BenchmarkKldBaseCache`:

- **Named by a digest, never by the key.** A `ModelContentFingerprint` is `v1:<64 hex>`, and `:` is illegal in a Windows path — NTFS would reinterpret the tail as an alternate data stream rather than reject it, so the failure would be a silently empty file. The cache file is `digest[3..35] + ".logits"` with a plaintext `.json` sidecar so the directory stays auditable.
- **Leased with `FileMode.CreateNew` + `FileOptions.DeleteOnClose`.** That is what makes the *crash* case right: the OS drops the handle, so the next run takes over instead of waiting on a lock nobody will release. A bare "does the lock file exist" check gets exactly that case wrong.
- **Published atomically** — written to `<name>.tmp.<invocationId>`, flushed, then same-directory renamed, so a partial logit file never resolves as a measurement.
- **Reserved against `IFreeSpaceProbe`** before the base phase, refusing when free space minus the estimate leaves under the headroom, naming both numbers.
- **A KLD measurement runs two models in sequence, so it takes two sequential capacity reservations.** One reservation sized from the quant held across the base phase — routinely the larger model — is an over-admission that OOMs on exactly the box where the base is big.

Disk estimate: **measured, not derived**. A real 10-chunk base file for `Qwen3.8-27B` (`n_vocab` 151 936) was 1 266 472 900 bytes over 777 912 320 logits — **1.628 B/logit**, not the format's 2.0. `BenchmarkFidelityPolicy.KldBytesPerLogit` is 1.75; 200 chunks is ~25.3 GB actual, not the 31.1 GB an f16 assumption promises.

### 6.2 The comparability gate is the whole cache key

`BenchmarkKldCacheKey` hashes **five** inputs: base-model fingerprint, corpus SHA-256, context tokens, chunk count, and `KldFormatVersion`. Four of them move without the fingerprint moving, and `kld_p99` is strongly chunk-count dependent — so gating on the base model alone would show a number measured on 50 chunks of one corpus beside one measured on 200 chunks of another.

A run stores the digest it was measured under (`kld_base_logits_digest`); the project computes the digest its **current** settings expect (`BenchmarkEndpointSupport.ExpectedKldDigest`). The figure is served only while the two match, otherwise `kldState = kld-stale` with the three KLD numbers **withheld**. Perplexity is unaffected — it carries `perplexity_corpus_id` and its window is pinned.

`BenchmarkKldCacheKey` is the **only** place that expression exists; `KldDigest_IsComputedByExactlyOneExpression` fails the build if a second file computes it.

### 6.3 Attempts are immutable; the run carries a projection

Each measurement is a `BenchmarkFidelityAttempt` row at `Sequence` 1..n. Re-measuring **inserts a new attempt**; the run's flat fidelity columns are refreshed only from a `Succeeded` attempt that is the highest-sequenced succeeded one, under a CAS. So a stale re-measurement cannot overwrite newer numbers, a failed one leaves the previous numbers alone, and "which file produced this figure" stays answerable from either end.

**Fidelity is measured once per CELL, never per repeat and never per item.** Perplexity and KL divergence measure the model file against a corpus, not the task — so with three items per cell the pre-suite rule (`!IsWarmup && RepeatIndex is null or 1`) queued three identical measurements, three times the GPU hours and, for KLD, three times ~25 GB of base logits, for three copies of one number. The rule gained an item half (**lowest `task_item_index` in the cell**) and is expressed **three times that must stay in step**: over the in-memory batch in `StartRunsAsync`, as `IsFidelityMeasuredCellAsync` on primary success, and as the EF predicate in `EnqueueMissingFidelityAsync`. A warm-up records `fidelity_status = 'skipped'` rather than NULL: "covered by repeat 1" and "never asked for" are different facts.

The measurement is **seeded on primary success**, in the same transaction that terminalizes the primary and seeds the judge attempt. Freeze writes only the `skipped` markers: a work item inserted at freeze outlives the run it belongs to, so a primary that failed or was cancelled left hours of GPU work queued against a run with no answer.

The run's `fidelity_status` follows its attempt through **every** transition — `queued` on the seed, `running` on the claim, a terminal value on terminalization, and `failed` when restart recovery fails an interrupted attempt. A status left `queued` with no attempt and no work item behind it makes every API report an active measurement forever. The NUMBERS are separate and are never touched by recovery. `AppendFidelityWorkAsync` owns that projection; a caller must not write it again.

The executor's **measurement watchdog (2 h, a constructor parameter) is a failure, not a cancellation**. Its linked token throws the same `OperationCanceledException` an operator's stop does, so the classification is derived at mapping time in the repo's priority order: the caller's token is not cancelled, therefore the timer is ours.

### 6.4 Settings live outside the project freeze

The five fidelity columns are written through **`PATCH benchmarks/projects/{projectId}/fidelity`** — the one project write that deliberately ignores the freeze. The freeze protects what the existing runs were measured *against* (task, context, agent); these settings decide what gets measured *next*, and every stored number keeps its own digest. Changing the base model or chunk count therefore makes old figures read `kld-stale` rather than wrong; nothing is deleted and no attempt is rewritten. `measureExisting` is opt-in and queues one item per succeeded, non-warm-up, first-of-cell run that has none, reporting the count.

The base model's **fingerprint is never client-writable**: it is an input to the comparability digest, so the service resolves it from the eligible-model catalog (registry facts, no re-hashing) and the executor re-verifies it against a verifying lease before measuring.

---

## 7. Lifecycle — the work queue, stop reasons, recovery

`BenchmarkWorkKind` is `{ Primary, Judge, Fidelity, Comparison }`. `Fidelity` and `Comparison` are **appended at the end**: the ordinal is persisted, so reordering would silently re-label stored rows.

`BenchmarkQueueHostedService` is a **single consumer** holding `IGpuWorkGate.TryBeginShared(GpuWorkKind.Benchmark)` across the claim and the execution. Nothing else of ours holds VRAM while a benchmark item runs, which is why a `Fidelity` item needs no gate of its own.

| Kind | Executor | Spawns |
|---|---|---|
| `Primary` | `BenchmarkRunExecutor` | llama-server, the run's frozen arguments |
| `Judge` | `BenchmarkJudgeExecutor` | llama-server for the judge model — **or nothing at all** (§5.2.2) |
| `Fidelity` | `BenchmarkFidelityExecutor` | `llama-perplexity`, no server and therefore no readiness probe |
| `Comparison` | `BenchmarkComparisonExecutor` | llama-server for the judge, one ordered pair per item |

> **A new work kind is not additive at the lifecycle layer.** `BenchmarkStore.ClaimNextAsync` and `RecoverRunsOnStartupAsync` both used to branch "Primary, else it is a Judge", and the `else` dereferenced `JudgeAttemptId`. Either new kind reaching them threw `InvalidJudgeTransition` and stalled the single-consumer queue behind an item it could never claim. Both are **explicit four-arm switches** now, and recovery needs **two sibling pre-sweeps**: an attempt or comparison left `Running` by a killed process, whose work item a previous partial recovery already terminalized, has nothing else that can reach it.

> **A comparison claim touches neither run's version.** Every per-run kind bumps `BenchmarkRun.Version` so a poller sees that something changed; a comparison names two runs and its work item names only the canonical first, so the common bump invalidated that run's CAS token on every pairwise claim — scoring, deleting or re-measuring it returned `VersionConflict` throughout a tournament. A pairwise reader is refreshed by the fit's own publication.

> **A comparison references two runs and foreign keys are OFF.** `DeleteRunAsync` guards and deletes through `BenchmarkComparisons` on `RunAId == id || RunBId == id`, bumps each affected revision's `ComparisonSetVersion` and deactivates the project's active fits. The delete order is comparisons → work items → judge attempts → fidelity attempts → run; fidelity attempts carry an encrypted receipt, so skipping them is a leak, not untidiness.

### 7.1 Repeat modes and stop reasons

`BenchmarkRepeatMode`:

| Mode | What varies | What it measures |
|---|---|---|
| `Throughput` (default) | nothing — temperature 0, one seed | the machine. Every repeat answers identically, so the spread is hardware noise |
| `AnswerVariance` | the seed, advanced per repeat, sampled at a temperature (default 0.7) | the model. The spread of *answers* is the measurement |

Both knobs are **sampling** and must never reach the launch arguments, or the runs of one group stop sharing a launch identity. Mode, seed and temperature are duplicated onto the run as plaintext columns because listing and CSV never decrypt a snapshot. `samplingTemperature` is `double` end to end — a float widened into the already-`double` column recorded 0.699999988079071 for 0.7.

`BenchmarkPrimaryStopReasons` is the one shared vocabulary, with one shared pair of predicates (`IsTruncated`, `IsIncomplete`). Two of its members are **node-derived, not provider tokens**:

- `reasoning-length` — the provider said `length`, and no visible answer token was ever emitted. `IsTruncated` covers it deliberately.
- `incomplete` — a *clean* finish that answered nothing: the turn ended on an unanswered tool call, or emitted only reasoning.

Both are decided in `BenchmarkRunExecutor.ResolveStopReason` from the **coalesced** parts. A per-delta capture cannot show the shape of a turn.

The event buffer keeps a tombstone per terminal run — an emptied entry still turns a late `Subscribe` replay into a reset rather than silence. Past `MaxRetainedTerminalRuns` (256) the oldest drop and the hub resets off the persisted `LastStreamSequence`. Queue membership is its **own** flag: a run is evicted once per terminal *phase* and has two, so keying the queue off the eviction flag enqueued each run twice.

---

## 8. Scheduling and hand-off

**`RunBenchmarkBatchHandler`** (template id `run-benchmark-batch`) freezes a whole model × KV-cache matrix against one project on a Quartz schedule, so an overnight matrix is a schedule rather than a foreground wait. Parameters `projectId`, `models` (1..10), `kvCacheTypes` (1..4), `repeatCount` (1..10), `warmup`; the expanded matrix is capped at 50 cells.

- **It enqueues and returns** — the single-consumer queue drains the runs, so the descriptor carries no `DefaultMaxRuntimeSeconds`.
- **A fire that finds queued/running WORK of ANY kind on the project SKIPS, and a skip is a SUCCESS.** A nightly matrix landing on the previous night's leftovers would measure the same project twice; reporting the skip as a failure would train an operator to ignore a red schedule. The guard asks `IBenchmarkStore.CountActiveWorkAsync` — work items, not run statuses, because judge, fidelity and pairwise work outlives the runs it belongs to — and the skip summary names which kinds are still busy.
- **The freeze budget is 45 s per CELL, not per fire.** The interactive batch endpoint's flat 45 s exists because it holds an HTTP connection; a fire holds nothing. Copying the constant truncated a 4-cell matrix live, because the freeze verifies each model's GGUF by digest at ~18 s per cold cell.
- **`AllowAgentCreation: false`, deliberately.** An AI agent may schedule a saved-agent run; it may not schedule GPU-hours.
- **A new template needs no frontend change** — `GET scheduler/templates` is what `ScheduledJobForm` builds itself from, including `defaultParameters`.

**`POST training/comparisons/{comparisonId}/benchmark`** (`ComparisonBenchmarkHandoffService`) turns a finished training comparison into a benchmark project with its paired base/tuned runs, closing the gap the old deep link left — that link could only *select* runs that already existed. Both models are frozen against one project through one `BenchmarkFreezeScope` with the same KV-cache type and repeat count, so they differ in the model and nothing else. The task is **required from the operator**: a comparison's evaluation prompt scores hold-out samples and would benchmark the wrong thing. Both sides resolve to **installed** model names (the tuned side through the artifact's `CommittedModelName`, which only exists after promotion), and both sides resolving to one name is refused.

The project is reused only when **every benchmark-defining field matches** — name, core task, context tokens and agent definition — because names are not unique and a project holds no comparison id; a same-named project holding a different question gets a suffixed sibling (`Nightly tune (2)`) rather than someone else's runs. Both sides are **frozen before either is written**: `IBenchmarkRunFreezeService.FreezeAsync` decides one model's runs without persisting them and `CommitAsync` inserts whole plans in one all-or-nothing `StartRunsAsync`, so a tuned side that fails verification leaves nothing queued and a retry cannot duplicate the base group. The response names each side's runs (`baseRunIds`, `tunedRunIds`) rather than one flat list.

> A bare `KeyNotFoundException` from the freeze escapes as a **500**: `BenchmarkEndpointSupport.Classify` maps it to 404 but `IsHandled` deliberately excludes it, so the global handler never sees it. Every caller of `IBenchmarkRunFreezeService.StartAsync` must catch it at the endpoint (costing an `EndpointExceptionMappingSourceGuardTests` allowlist entry) or translate it in the service — the hand-off does the latter.

Full detail: [Scheduler](06-scheduler.md) and [Training](18-training.md).

---

## 9. Export

`GET benchmarks/projects/{projectId}/export` (JSON) and `.../export.csv`, both Operator-gated. `BenchmarkExportProjection.SchemaVersion` is **4**.

| Version | Added |
|---|---|
| 2 | `repeatGroups`, `llamaBench` |
| 3 | a fidelity block on every run, and the pairwise fit |
| 4 | task suites — `taskItems[]` and `cells[]` as top-level sections, and `taskItemId`/`taskItemIndex`/`cellKey`/`taskInputHash`/`taskItemSetHash`/`cellQuality` on every run row |

- **JSON** reuses `BenchmarkRunDetailResponse` verbatim — the same shape `GET benchmarks/runs/{runId}` returns — so an export and a live read of one run can never disagree. `pairwiseFit` is ONE object: a fit is a single immutable row whose key covers the whole comparison set, so smearing it over the runs would say something untrue.
- **CSV** is flat and its columns are **appended, never inserted**. Consumers read this export by column index, and an inserted column silently turns a sampling seed into a token count. The snapshot test pins the whole header line plus one whole row, *including* the trailing empty cells — a short row is what actually breaks an index reader. `taskItemIndex` goes through the formula guard even though it is numeric, because a leading `-` is what a spreadsheet evaluates. The CSV repeats the pairwise fit key on every row of the cohort, having nowhere else to put it.
- **A run's `rank` is its CELL's rank**, and `cellQuality` is the cell's mean. The JSON export re-reads each run through `GetRunAsync`, which computes neither, so both must be re-attached from the ranked summary.
- The export applies the **same** KLD comparability gate as the live read: a stale figure exports as `kldState=kld-stale` with its three cells empty and its digest still written, because the digest is the evidence for the withholding. Any new caller of `ToDetail`/`ToSummary`/`ToFidelity` has to pass `BenchmarkEndpointSupport.ExpectedKldDigest(project)`.
- Fidelity numbers use a six-decimal formatter, not the throughput formatter's three: the measured Q4_K_M/UD-Q3_K_XL gap is 6.7977 vs 6.9497 at standard errors near 0.074, so three decimals throws away exactly the digits that decide whether two quants separate.

---

## 10. The frontend

`XE-Local-AI-Engine.Client.React/src/features/benchmarks/`. The UI's word for a cell is **combination** — "cell" is a persistence term and means nothing to an operator looking at a model × KV-type grid.

| Component / model | What it shows |
|---|---|
| `BenchmarkTaskItemEditor` | add / edit / reorder / delete items, the NIAH probe form, per-item verifier overrides |
| `BenchmarkCellsTable` | the combination table: suite mean, rank, items answered, the missing ones by name, recall |
| `BenchmarkItemBreakdown` | one combination item by item, each row linking to its run |
| `BenchmarkPairedDelta` | `A − B = δ [low, high]`, separated / not separated |
| `BenchmarkVerifierEditor` | the criterion kinds including `pythonTests` (test code, exports, timeout, extraction mode) |
| `BenchmarkRunEstimate` | what a launch is about to cost, before the operator commits |
| `BenchmarkExportButtons` | names the schema version on the buttons that produce it |

Rules the UI is built on, all of them consequences of the backend contracts above:

- **Every destructive item edit states its cost first.** Saving an edit says the item bumps to r*N* and every run of r*N−1* becomes `item-revised`; deleting says every already-measured combination becomes `item-set-revised`; reordering says explicitly that **nothing changes**, because the set is identified by its items and not their positions.
- **The launch estimate is runs, not time, unless the project has history.** `benchmarkRunEstimate` multiplies cells × leaf items × (repeats + warm-up); `estimatedMs` is null when there is no completed run to extrapolate from — omitted rather than guessed, and coarse when it exists (`1h 12m`, never seconds of an hour). Warm-ups are excluded from the median: they are the slow launch the repeats after them are measured without. Over `maxRunsPerRequest` the message is a **refusal**, not a warning, because the node refuses the whole freeze.
- **A missing `pairedDeltas` entry renders "too few shared items"; `separated: false` renders "not separated by this suite".** Neither is re-derived from the bounds (§5.4).
- **`no-score` gets its own copy**, not the `item-incomplete` action chip. Telling an operator to re-run an item on a pure-probe project sends them looking for a question nobody asked.
- Every exclusion reason has a long form (why the cell is unranked) and a short badge, plus an action where one exists — `rerun-cell`, `rerun-item`, `enable-compute`.
- The FE's mirrored limits (`benchmarkTaskItemLimits`, `benchmarkNiahLimits`, `benchmarkPythonTestsLimits`, `maxVerifierPatternLength`) are copies of backend constants and are verified against them: 20 leaves, 100 runs per request, 512-token probe floor, 4 000-char test code, 16 exports, 600 s timeout, 512-char pattern. Change one side and the other becomes a form that accepts what the node refuses.

---

## 11. Traps

- **Adding a member to `BenchmarkRuntimeSnapshotV1` (or any nested record) breaks every stored run.** The payload is validated by re-hashing it against its own embedded `ConfigurationHash`. A new member must be nullable **and** `[JsonIgnore(WhenWritingNull)]`; `BenchmarkRuntimeSnapshotV1CompatibilityTests` holds the literal v1 payload and is the guard. Per-run facts belong in flat columns.
- **Read stored benchmark blobs with `BenchmarkExecutionSerialization`**, the writer's Web/camelCase options. Default serializer options silently produce zeroed DTOs rather than throwing.
- **`BenchmarkCanonicalJson` uses `JsonStringEnumConverter(CamelCase)` before hashing.** Integer ordinals would let an enum insertion silently reinterpret stored evidence — which is also why `LlamaServerPlacementOutcome.None` was *appended*.
- **`LlamaServerLaunchProjection` is the single argv projection**, and its member order and names are persisted identity. The receipt is parsed back from final argv, so an omitted optional flag is explicit evidence. Changing the projection changes every hash it produces, so it is versioned by `IdentitySchemeVersion` and every row records the scheme it froze under; rolling *back* past a scheme change is a drain-then-migrate procedure, not a revert — see the [launch-identity scheme downgrade runbook](../runbooks/benchmark-launch-identity-scheme-downgrade-runbook.md).
- **`benchmark_task_items.index` is a SQLite keyword** — its CHECK quotes it (`"index" >= 0`). EF quotes it in generated SQL; hand-written test SQL must too.
- **EF cannot express the `COALESCE(task_case_id, x'00')` unique indexes.** `HasIndex().HasFilter()` takes columns, not expressions, and SQLite lets a unique index repeat NULLs. The three are raw `migrationBuilder.Sql(...)`, deliberately absent from `OnModelCreating`, and the migration test reads them back from `sqlite_master` — an assertion through the EF model passes against exactly the index that is wrong.
- **After `dotnet ef migrations remove`, compare the regenerated migration and snapshot with the preceding designer.** Up-only `MigrateAsync` is insufficient when branch timestamps interleave: SQLite down migrations rebuild tables from each migration's own target model and can delete sibling columns. Consolidate unshipped migrations at the merged tail and run the full Persistence project, rollback included.
- **`rankExclusionReason` is a top-level field of the run DTO**, not a member of its `judge` object. Reading it off `judge` returns null for every run and makes a working exclusion look broken.
- **A shared worktree means a shared git index**, and `pnpm run openapi:check` diffs the worktree against the **index**. After a private-index commit HEAD moves and the shared index does not, so the check reports drift on a tree that exactly matches HEAD.
- **Under Aspire the node's GGUF registry defaults to `AppContext.BaseDirectory/models`, which is empty.** A dev-stack live gate needing real models must export `HuggingFace__ModelsDirectory=$HOME/.local/share/XE-Local-AI-Engine/models` before `scripts/dev-start.sh`, or `benchmarks/eligible-models` is silently empty. A multimodal GGUF cannot be the judge even when the rubric never spawns one.
- **`pgrep -f "dotnet (build|test)"` matches its own wrapper** — a wait-for-no-build loop waits on itself forever. Use `pgrep -f "dotnet-root/dotnet (build|test)"`, and prefer `scripts/with-build-lock.sh`, which makes the collision impossible. A concurrent build swapping DLLs mid-run produces results that are neither pass nor fail.

---

## 12. Where things live

| Concern | Path |
|---|---|
| Executors, freeze, planner, fitter, bootstrap, NIAH generator, contracts | `XE-Local-AI-Engine.Client.Application/Services/Benchmarks/` |
| Scheduled matrix handler | `…Client.Application/Services/Scheduler/Handlers/RunBenchmarkBatchHandler.cs` |
| Training hand-off | `…Client.Application/Services/Training/Comparison/ComparisonBenchmarkHandoffService.cs` |
| Entities, configurations, hashing, store | `XE-Local-AI-Engine.Client.Persistence/{Entities,Configurations,Implementation,Stores}/` |
| Endpoints, DTOs, mappers | `XE-Local-AI-Engine.Client/Endpoints/Benchmarks/V1/` |
| Routes | `XE-Local-AI-Engine.Client/Endpoints/Common/LocalApiRoutes.cs` (`LocalApiRoutes.Benchmarks`; the hand-off is `LocalApiRoutes.Training.ComparisonBenchmark`) |
| React feature | `XE-Local-AI-Engine.Client.React/src/features/benchmarks/` |
| Fidelity + NIAH corpus and licence | `tools/benchmark/corpus/`, `third-party/data/` |

Related pages: [Local Runtime and Providers](03-local-runtime-and-providers.md) for llama.cpp binaries and placement, [Scheduler](06-scheduler.md) for the matrix template, [Model Fit](07-model-fit.md) for eligibility, [Data and Persistence](08-data-and-persistence.md) for the encryption interceptors every new encrypted column needs a row in, [API and Hubs](09-api-and-hubs.md) for the route surface, [Training](18-training.md) for the comparison hand-off, [Compute Tools](19-compute-tools.md) for the sandbox `pythonTests` runs in.
