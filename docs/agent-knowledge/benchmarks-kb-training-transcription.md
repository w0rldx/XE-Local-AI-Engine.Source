# Benchmarks, knowledge base, training and transcription

Scope: benchmarks, knowledge-base retrieval, fine-tuning and live transcription. Read when: changing any `Benchmark*`
type, the knowledge base, `Services/Training` or `Services/Transcription`. Architecture lives in `docs/wiki/15`, `18`,
`20` and `24`; this file holds only the traps those pages lack. Model fit, capacity and spawn:
[model-fit-and-discovery](model-fit-and-discovery.md).

## Benchmarks

`docs/wiki/20-benchmarks.md` is the authority; the entries below keep only facts that page lacks.

### Benchmark cache evidence comes from response timings

**Rule:** llama-server `prompt_n` counts evaluated tokens only and `cache_n` reused ones; derive reuse as `cache_n / (cache_n + prompt_n)`, never from cold/warm `prompt_tokens_total` deltas. **Prevents:** reporting no cache reuse when the warm request reused the prompt. **Authority:** `LlamaServerGenerationTimings.TryRead`, `InferenceBenchmarkHarness.StreamStageAsync` / `DeriveCacheHitRate`; pinned llama.cpp `tools/server/server-context.cpp`.

### Benchmark launch evidence (KV-cache type feature, 2026-08-16)

**Rule:** `MarkPrimaryLaunchReadyAsync`/`MarkJudgeLaunchReadyAsync` are insert-if-null, accept Running at the claimed version or Cancelled at claimed+1, use `CancellationToken.None`, log failure, and never change status/version or fail a measurement. A replay uses ONE strict launch candidate: no safe-KV fallback, no fallback-store write; unsupported KV → `BenchmarkUnsupportedKvCacheTypeException`/422; capacity sizes from the frozen context; K and V types identical. Judge response schemas are bound-free and ride `ChatOptions.ResponseFormat`, not a tool; null on non-judge requests and outside the config hash. Read timings with catch-based `JsonPatch` reads, sum every request of a turn, persist `segment_count`. `RuntimeBundleIdentityCalculator` framing is persisted. **Authority:** the symbols named. [evidence](../agent-knowledge-evidence.md#benchmark-evidence-incidents)

### Benchmark launch evidence: a launch receipt carries nothing addressable

**Rule:** `LlamaServerLaunchReceipt` is assembled post-readiness, non-throwing, and holds no model or executable path, host or port (the executable is identified by digest and version). `LlamaServerLaunchAuxAssets` records booleans only, never paths or digests. New receipt facts may be added freely as long as they stay outside the persisted `LlamaServerLaunchProjection` identity. **Prevents:** a persisted, displayed receipt leaking local paths or endpoints; a new fact silently changing run comparability. **Authority:** `LlamaServerLaunchReceipt`, `LlamaServerLaunchAuxAssets`, `LlamaServerLaunchProjection`.

### Benchmark readiness (repeat groups, launch matrix, export — 2026-08-17; optimization pass 2026-08-25)

**Rule:** eligible-model listing and `BenchmarkRunFreezeService` both map `InstalledGgufSnapshotException` to the declared 422, so one bad model does not 500 a matrix. Rank exclusion is projected onto EVERY API path returning runs; truncation recognition is shared between ranking and judge execution. `invocation_timeout_seconds` is frozen on the run (legacy null = 900 s). Compare legacy registry SHA case-insensitively; an unmapped provider defaults to `llamacpp`. CSV fields starting with `= + - @` tab or CR get an apostrophe. `InstalledGgufSnapshotStore` memoizes SHA-256 by (path, length, mtime). `BenchmarkRunBatchService.RequestTimeBudget` (45 s) is checked between cells, returning `BatchTimeBudget`; a test swapping `TimeProvider` starts it at `DateTimeOffset.UtcNow` (the host's token clock). **Authority:** the symbols named.

### Benchmark quant fidelity — perplexity and KL divergence (2026-08-26)

**Rule:** the work-item CHECK rewrite is a SQLite table rebuild; `DropCheckConstraint`/`AddCheckConstraint` must carry `queue_sequence` (the FIFO position) through, asserted in the migration test. A fidelity work item pins `attempt = 1`: a base-logit lease held by another process waits on the `BenchmarkAdmissionRetry` cadence and then `IBenchmarkStore.RequeueFidelityAsync` returns item and attempt to Queued; never fail it. Replayed placement flags include `-ctk`/`-ctv`, so a KV-quantized run is measured under its own KV. **Prevents:** reordered pending work; a lost measurement behind a message promising a retry. **Authority:** `BenchmarkFidelityExecutor`, `BenchmarkWorkKindLifecycleTests`. [evidence](../agent-knowledge-evidence.md#benchmark-quant-fidelity--perplexity-and-kl-divergence-2026-08-26)

### Benchmark pairwise judging and Bradley-Terry (2026-08-26)

**Rule:** `ComparisonSetVersion` bumps in the transaction that inserts or terminalizes a comparison; a fit stamps it, and `> 0` is how the read knows a project judges pairwise. The fitter requires a promoted `ReferenceExecutionKey` carried by EVERY comparison; one mismatch refuses the fit, never a partial fit. A comparison claims the cohort's reference key on its first success. `BenchmarkProjectService.BuildCohortSeedAsync` clears `SeedPointwiseAttempts` for a pairwise policy. A failed comparison slot retries as a new row at `attempt_sequence + 1`. **Prevents:** unrankable or mixed-runtime fits; pointwise attempts in a pairwise cohort. **Authority:** `BenchmarkPairwiseFitter`, `BenchmarkPairwisePlanner`. [evidence](../agent-knowledge-evidence.md#benchmark-pairwise-judging-and-bradley-terry-2026-08-26)

### Benchmark task items (2026-08-26)

**Rule:** `BenchmarkTaskItemHashing` lives in Persistence and cannot use `BenchmarkCanonicalJson` (application layer); it is a length-prefixed `IncrementalHash` feed with unit/record separators. Keep the length prefixes. A migration may backfill only derived plaintext (`cell_key` = `'run:' || id`). The kind CHECK admits the whole vocabulary up front; the service refuses unexecutable kinds, because a CHECK rewrite is a table rebuild. **Prevents:** a payload holding a separator byte hashing as a different field arrangement. **Authority:** `BenchmarkTaskItemHashing`.

### Benchmark long-context probes (2026-08-26)

**Rule:** a per-item verifier override is validated against the CURRENT rubric at write (`BenchmarkTaskItemService.EnsureOverridesFitRubric`: the criterion id exists and `BenchmarkJudgeVerifierConfig.Parse` accepts the config for that criterion's kind), and `BenchmarkProjectService.UpdateJudgePolicyAsync` re-runs it over every item, refusing a rubric change that would strand an override. A judging that still meets one fails with the `override-unmatched: ` prefix. The NIAH generator id is minted in the service, not the store. **Prevents:** an item graded against another item's expected answer. **Authority:** the symbols named.

### Benchmark scheduled matrices and the training hand-off (2026-08-26)

**Rule:** scheduler progress events (`scheduled_job_run_events`) are not readable over REST; a handler that wants its outcome visible sets `ScheduledJobExecutionContext.Summary` (plaintext-structural: counts, ids, operator names only), else a real fire and a busy-skip both read `Completed.`. A manual "Run now" is marked by `SchedulerJobKeys.ManualFireKey` on the fire's `JobDataMap`, which `SchedulerDispatchJobRunner` maps to `ScheduledRunTrigger.Manual`; it is not a parameter-override key. **Prevents:** indistinguishable scheduler run rows. **Authority:** the symbols named; `RunBenchmarkBatchHandler`.

## Knowledge base

### Knowledge base / RAG

**Rule:** delete order and invariants: `docs/wiki/15-knowledge-base.md`; pooled `-b/-ub`: `docs/wiki/03-local-runtime-and-providers.md`. Not stated there: vector search is managed brute-force cosine by design; ingest and query share one `EmbeddingModelResolver` result, and corpus-wide stale/reset decisions run only when `EmbeddingModelResolution.IsConfident`; retrieval is BM25 ∪ cosine → RRF(k=60) → optional rerank; query embeddings of sensitive text are RAM-only, bounded, keyed by vector identity + query hash. **Prevents:** a transient provider failure reinterpreting a healthy corpus under a fallback model; persisted sensitive vectors; a delete test on an attached EF graph can false-pass, so assert through `KnowledgeDocumentPurgeService`. **Authority:** the symbols named. [evidence](../agent-knowledge-evidence.md#knowledge-retrieval-evidence)

## Training

### Training module (fine-tuning) — live-gate rules, 2026-08-15

**Rule:** `training_datasets.definition_json` is pinned in the transaction that reads `DefinitionVersion`; generation/evaluation read `DatasetDefinitionService.ReadPinnedBody`, and a null pin refuses with `UnpinnedDatasetReason`. V1 `TeacherSampleRecordV1` is exactly one tool call (`SampleValidationPipeline`, `EvaluationScorer.RejectMultiCall`). Comparison identity is dataset ID + content fingerprint + order-insensitive hold-out set, never run ID. Evaluation refuses fingerprint drift at create and at claim/load and keeps the per-ID missing-sample check. Link runs via `IInstalledBaseModelLinker`, never by display name. Delete artifacts through `ITrainingExportService.DeleteArtifactAsync`. **Authority:** the symbols named. [evidence](../agent-knowledge-evidence.md#training-live-gate-incidents)

### Training: launch receipts, runtime adoption and teacher turns

**Rule:** `TrainingRunStartupReaper` reads `ListLaunchReceiptsAsync` unpaged; only the reaper clears a receipt, after a kill or proven non-match, one receipt at a time; normal completion clears transactionally. Runtime directory swap + state write are one rollback boundary, restored with `CancellationToken.None`; a failed reprovision with the old runtime intact stays Ready with a sanitized error. Structured teacher output: interpret no-tool sentinels at `TeacherSampleRecordV1.DemonstratesToolCall`, then validate against the ORIGINAL schema (strict transform makes all properties required). Every model turn has its own deadline (`StructuredAgentRunner.TurnTimeout`); keep `dataset_num_proc=1`. Python parsers scan stdout for JSON lines; fall back to type/exit code when `str(e)` is empty. **Authority:** the symbols named; `DeferredLlamaServerStructuredOutputTests`.

### Training: persistence fan-out and routing

**Rule:** a new `LocalModelOrigin` updates its JSON converter, the EF pair, the table CHECK and `GgufRegistryRevision.SerializeOrigin`; a new registry-entry field also updates `InstalledGgufRegistryValue` or rollback CAS fails. Nullable encrypted blobs go through `OptionalBlob`. Evaluation never takes a mutation lease that blocks its own load. Queue claims are attempt-pinned and cannot be returned: acquire exclusivity before the claim. Durable rejection reasons outlive the hub buffer. Revision-pinned Hub lookups: revision-aware overload, one escaped path segment, revision in the cache key. **Authority:** the symbols named; `TrainingRunQueueHostedService`, `TrainingStoreNullBlobTests`.

### Training: routes stay flat siblings

**Rule:** `training.index`, `training.datasets` and `training.comparisons` are flat sibling routes, each with its own capability `beforeLoad`; never nest them under a parent, because a parent route without `<Outlet>` swallows its children. The navigation flag is UI-only; the endpoints stay Operator-gated. **Prevents:** child pages that route but render nothing; a hidden nav item mistaken for access control. **Authority:** `XE-Local-AI-Engine.Client.React/src/routes/_layout/training.*.tsx`.

## Live transcription

### NAudio's `CaptureAsync` starts the client itself and silently drops Silent packets

**Rule:** `WasapiRecorder.CaptureAsync(ct)` throws "Already recording" unless capture is `Stopped`, initialises and starts the audio client itself and stops/resets it in its own `finally`: a `StartRecording()` before the loop throws, a `StopRecording()` after it is a no-op, and `await using` is the whole teardown. Silent-packet drop and the lagging audio clock: `docs/wiki/24-audio-transcription.md` ("Silence never arrives…"). **Prevents:** an "Already recording" crash on the first capture written from the obvious lifecycle. **Authority:** upstream `NAudio` 3.1.0 `WasapiRecorder`; `WindowsProcessAudioCaptureSource`. [evidence](../agent-knowledge-evidence.md#naudios-captureasync-starts-the-client-itself-and-silently-drops-silent-packets)

### never publish an advisory hub event under a live session's commit gate

**Rule:** the commit gate in `LiveTranscriptionSessionRegistry` serializes seq allocation, persistence and publication; progress and other advisory pushes use their own `ProgressGate`, closed just before the terminal status push. **Prevents:** a lane with nothing to commit waiting behind a sibling's blocked persistence. **Authority:** `LiveTranscriptionSessionRegistry.PublishCatchUpAsync`/`CloseProgressAsync`; `LiveTranscriptionSessionRegistryTests.Commit_WithBlockedSeqOnePersistence_DoesNotPublishSeqTwoFirst`.

### "still producing rows" is `IsRegistered`, not `IsLive`

**Rule:** a guard that must refuse while a live session can still commit rows asks `ILiveTranscriptionSessionRegistry.IsRegistered` plus the persisted `Transcribing` status. `IsLive` turns false when admission closes (Stop), but the session drains and commits until finalization unregisters it, possibly for minutes. **Prevents:** a transcript edit accepted during the drain, then raced by a late commit. **Authority:** `TranscriptionService.UpdateSegmentTextAsync`, `TranscriptSegmentEditServiceTests`, `UpdateTranscriptSegmentEndpoint` (409 `session-transcribing`).

## Stale beliefs

Superseded claims; the entries above are the active rules.

| Stale belief | Current correction |
|---|---|
| There is no speech-to-text in this repo. | whisper.cpp ships: batch upload, live sessions over `TranscriptionHub`, and browser capture in the SPA (§6). |
| A benchmark project asks one question. | A project holds 1..N task items; a single item is the degenerate case (§3). |
| A run is the unit that ranks. | The CELL ranks — one model x KV type x repeat over the whole item suite — and a run reports its cell's rank (§3). |
| A cell exists only when there is a repeat group. | A cell exists whenever one freeze produces more than one run per model; `cellGroupId = repeatGroupId ?? (leafItems > 1 ? new : null)` (§3). |
| Repeats are the only reason to launch more than one run per model. | A suite fans out one run per leaf item, and the probe cases of a NIAH generator each count (§3). |
| A user score rescues any excluded run. | It sits below `item-revised` and `item-set-revised`: an operator cannot notice that a question or the suite around it moved (§3). |
| Fidelity is measured once per repeat group. | Once per CELL — the item half of the rule is the lowest `task_item_index`, and it is expressed in three places that must stay in step (§3). |
| A verifiable criterion is decided by the policy config alone. | The judge resolves `item override ?? policy config` for both the verifier config and the reference answer (§3). |
| A verifier that cannot run scores 0. | It fails the judging and the run is excluded as `verifier-unavailable`; only a TIMEOUT is a real 0 (§3, §4). |
| The eligible-model listing re-hashes GGUF members. | Listing trusts recorded registry facts; only the freeze re-hashes (§3). |
