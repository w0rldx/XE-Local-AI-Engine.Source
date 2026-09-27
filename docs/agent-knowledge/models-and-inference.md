# Models, inference, retrieval, training and transcription

Scope: GGUF discovery and recommendation, llama-server capacity/spawn/lifecycle, benchmarks, knowledge-base retrieval,
fine-tuning, and live transcription. Read when: changing model-fit or HuggingFace discovery, `CapacityService` or the
llama-server supervisor, any `Benchmark*` type, the knowledge base, `Services/Training`, or `Services/Transcription`.
Architecture lives in `docs/wiki/03`, `07`, `15`, `18`, `20` and `24`; this file holds only the traps those pages lack.

## Model discovery and recommendation

### Recommendation: walk the quant ladder, never pick one quant

**Rule:** both advisor lanes select through `GgufFileSelector.SelectBestFit` (ladder walk: `docs/wiki/07-model-fit.md`); never duplicate selection. MTP draft GGUFs are a distinct identity (`MTP-<quant>`), recognized only by an `MTP/` path segment or `mtp-` filename prefix, never a bare `MTP` token in a base-model name. Drafters sort last, are never advisor picks, register as Draft, stay out of chat defaults and remain selectable in the draft slot. **Prevents:** a real base model filtered as a drafter, or a drafter recommended for chat. **Authority:** `GgufFileSelector`, `QuantLadder`.

### Recommendation ranking is capability-bucketed

**Rule:** explore ranking is GiB size bucket → downloads → last modified → trusted publisher → repo ID; catalog ranking is tier → MoE verdict → quant quality → release date → ID. "Recommended" needs positive headroom and a quant at/above Q4_K_M. **Prevents:** raw size/headroom ordering regressing the explore lane to tiny models. **Authority:** `CatalogRecommendationService.BuildRecommendationsAsync`.

### The advisor is two lanes, and one may fail

**Rule:** catalog and live-HF explore lanes concatenate; a catalog failure degrades to an empty catalog lane and must not fail explore. **Prevents:** one lane's outage blanking all recommendations. **Authority:** `CatalogRecommendationService`.

### MoE models need `MoeFacts`, not naive VRAM math

**Rule:** fit MoE models through `MemoryFitEstimator` with `MoeFacts` (`ActiveParamCount`, `ExpertCount`, `ExpertUsedCount`) and prefer the curated active-parameter count; expert counts alone fall back to the deliberately conservative `DefaultExpertWeightShareFraction`. Catalog ranking use: `docs/wiki/07-model-fit.md`. **Prevents:** total-weight-only math rejecting or mis-scoring MoE models. **Authority:** `MemoryFitEstimator`, `MoeFacts`, `CatalogRecommendationService.BuildMoeFacts`.

### Multi-part GGUF shards are ONE model

**Rule:** `HuggingFaceGgufDiscovery.GroupShards` groups `<base>-00001-of-00003.gguf`, sums sizes, uses the first split, and drops the shard group when an equivalent merged file exists. Later splits are headerless and never candidates. **Prevents:** shards listed as separate models, or a header read on a headerless split. **Authority:** `HuggingFaceGgufDiscovery.GroupShards`.

### NVFP4 **GGUF** runs here, natively — NVFP4 **safetensors** never will

**Rule:** NVFP4 GGUF is recognized, ranked and run by the pinned llama.cpp; compressed-tensors/ModelOpt NVFP4 safetensors cannot be converted by the current script. Check the container before calling the format unsupported. An unknown quant token makes a file disappear, not merely misprice, so add a new native format to parsing and usability together. Use the real blob size when available; the 4.25 bpw estimate is empirical. **Prevents:** a supported format declared unsupported, or silently dropped. **Authority:** `QuantLadder`. [evidence](../agent-knowledge-evidence.md#nvfp4-live-run)

### GGUF filenames from a repo are untrusted input

**Rule:** call `GgufFilePath.IsSafeRelativePath` and `ResolveContainedPath` immediately before opening; reject roots and `.`/`..` segments; never trust Hub sibling names. **Prevents:** path traversal out of the model store. **Authority:** `GgufFilePath`, `GgufFilePathTests`.

### HuggingFace API facts that bite

**Rule:** use `filter=gguf`, not `library=gguf`; `gated` is a string union, not a bool; request `?blobs=true` for sizes; .NET strips `Authorization` on a cross-host CDN redirect; filter `mmproj*.gguf` companions everywhere. **Prevents:** empty listings, parse failures, missing sizes, gated downloads failing after redirect, projectors offered as models. **Authority:** `HuggingFaceGgufDiscovery`.

### Model kind classification

**Rule:** `ModelKind` includes Reranker; test reranker names before embedding names. The capability-classification cache is digest-keyed, so an Ollama capability change without a digest change stays cached. **Prevents:** rerankers classified as embedding models; expecting a re-probe that never happens. **Authority:** `ModelKind` (`Client.Persistence`).

### Local-default chat resolution must stay Ollama-blind

**Rule:** resolve only installed llama.cpp GGUF Chat models from persisted classification; never live-probe Ollama on the send hot path. No chat model is `ModelNotInstalled`, not provider-unreachable. **Prevents:** send latency and a misleading error. **Authority:** `LocalDefaultChatModelResolver`, `NoChatModelInstalledException`.

### Reasoning ("think") has counter-intuitive Ollama semantics

**Rule:** for a model without Ollama's `thinking` capability, `think:true`/levels answer 400, `think:false` suppresses template-default reasoning, and omission preserves it: requested reasoning → omit; off/unspecified → false. Thinking-capable models receive false/levels. Keep `InvocationAgentFactory` and `ParticipantReasoningOptions` aligned; a new effort value also updates both factories, `ReasoningEffortNormalizer`, `RuntimePackageValidator` and `RuntimePackageConfigHash`. **Prevents:** 400s on non-thinking models and reasoning silently on or off. **Authority:** the symbols named.

## Capacity, spawn and process lifecycle

### Capacity gate: dispose the reservation

**Rule:** only a local `CapacityDecision.Allow` carries an `IDisposable` reservation; dispose it when the child exits. Warm `IRuntimeDeviceAudit` before entering the ledger decision gate. **Prevents:** leaked pending bytes that make later spawns reject. **Authority:** `CapacityService`.

### llama-server spawn invariants

**Rule:** the flag set, policy precedence, context tiers and KV defaults are in `docs/wiki/03-local-runtime-and-providers.md` ("Per-role launch flags…", "Launch-policy defaults…", "Spawn attempt sequencing…"); fitted GPU placement comes only from `llama-fit-params` ("`ExploreAsync`"). Not stated there: never enable `--ui-mcp-proxy` (MCP sits above llama-server); rerank scores map by the returned index, never response order, and a rerank failure degrades to RRF order; an unknown KV token degrades to fp16, and the default allocation-cache key must stay byte-identical when KV type is null so chat keeps its identity; read effective `n_ctx` from `/props` after readiness and feed both budgeters (unknown providers fall back to 8192). **Prevents:** a forked allocation identity; misattributed rerank scores. **Authority:** `LlamaServerLaunchPolicy`, `LlamaServerProps.ReadAsync`.

### llama-server readiness, load lifetime, and eject (Audit-4)

**Rule:** readiness, retry classes and reaper rules: `docs/wiki/03-local-runtime-and-providers.md` ("Health probe…", "Reuse-path liveness…", "Eviction & reaper"). Eject tri-state: `TryAcquireInferenceLease` is granted, refused-evicting or refused-absent. Absent may ensure/spawn; evicting must fail at once with `LlamaServerModelEjectedException`. Clear the eject mark on every path where teardown did not complete, including cancellation of the eject request mid-drain. Add new `InvocationState` fields to `Clone()`; warm local models before arming the stream-idle watchdog. **Prevents:** a request slipping under the drain and respawning the model the operator just ejected; every later lease refused forever. **Authority:** `LlamaServerProcessSupervisor`, `SupervisorLifecycleTests`.

### Context allocation is a stable process decision, not a live-memory sample

**Rule:** tier mechanics: `docs/wiki/03-local-runtime-and-providers.md` ("2.8 Process context allocation…"). `CapacityService.DecideAsync` must reject, never down-tier, below a caller's `RequiredContextTokens`: `_adjustedAllocations` is process-lifetime, monotonically downward and never cleared on eject, so one down-tier pinned by `TryCommitAdmissionFootprint` fails every later required-window admission until restart. Benchmark executors share `BenchmarkCapacityAdmission.AdmitAsync` (24 × 5 s default); a direct executor test injects `new BenchmarkAdmissionRetry(MaxRetries: 0, TimeSpan.Zero)`. **Prevents:** a sticky poisoned admission; two-minute test sleeps. **Authority:** the symbols named. [evidence](../agent-knowledge-evidence.md#context-and-admission-latch)

### Context allocation precedence: frozen, then deterministic override, then automatic

**Rule:** `ProcessContextAllocationResolver` resolves `FrozenProfile` > `DeterministicOverride` > `HardwareTier`. Only a `HardwareTier` allocation down-tiers (`TryDownTierForAdmission`, or `TryDownTierAfterOutOfMemory` at most `MaximumAutomaticDownTiers` (2) times per key); frozen and override allocations never mutate after a failure. A request can shrink an existing process's window, never enlarge it. The launch-policy fingerprint and tier decision never include live free VRAM, and admission retries re-evaluate capacity without reserving speculative bytes. Keep four values separately named through APIs and DTOs: launched `-c`, request budget, train-context ceiling, frozen replay override. **Prevents:** a frozen replay silently measuring a different window; one context name meaning four things. **Authority:** `ProcessContextAllocationResolver`, `ProcessContextAllocationSource`.

### llama-server node settings are captured once per boot — a `PUT` after that changes nothing until a restart

**Rule:** `speculativeMode` and `chatCacheReuse` are captured when the `LlamaServerSupervisorOptions` singleton is first resolved, and `kvCacheType` when `LlamaServerLaunchPolicyOptions` is (both lazy factories, not a reliable "first spawn" marker). A `PUT node-settings` after that does not reach a later spawn: restart with `scripts/dev-stop.sh` + `scripts/dev-start.sh` and read the values back with a `GET` before measuring. **Prevents:** an in-boot A/B concluding "the knob does nothing". **Authority:** `AddNodeModelRuntimeExtensions.BuildSeededLlamaServerSupervisorOptions`, `BuildSeededLlamaServerLaunchPolicyOptions`. [evidence](../agent-knowledge-evidence.md#llama-server-node-settings-are-captured-once-per-boot--a-put-after-that-changes-nothing-until-a-restart)

### a callback that runs under the supervisor's per-key gate must never re-enter the gated path for that key

**Rule:** `RunExclusiveProfilingCoreAsync` holds the non-reentrant `_ensureGates[key]` across its `body` (for a benchmark, the whole invocation pipeline). A guard excluding a profiling-owned process must answer EVERY same-key call the body reaches (`EnsureRunningAsync`, `GetRuntimeInfo`) from the pinned process via an identity-matched bypass (an `AsyncLocal` marker naming the pinned `ProcessKey` and INSTANCE, cleared before the body unwinds). A marker whose process is gone fails with the classified error, never falls through to `DecideEnsureAsync`; a mocked runner hides it. **Prevents:** local benchmarks hanging to timeout or failing `EffectiveContextUnavailable`. **Authority:** `SupervisorProfilingReentrancyTests`, `SupervisorProfilingTests`. [evidence](../agent-knowledge-evidence.md#a-callback-that-runs-under-the-supervisors-per-key-gate-must-never-re-enter-the-gated-path-for-that-key)

### a provider's `TryAddSingleton(new XOptions())` binds NOTHING; prove a config key end to end before measuring with it

**Rule:** a provider module registering `services.TryAddSingleton(new XOptions())` gives the host a bare default; the class's `SectionName` is only a promise the host keeps by binding the section before registering the module. Before a live round relies on a config/env knob, prove it reached the process (`ps -o args` for a launch flag, else a log line). A unit test handing the options object to the consumer proves the consumer, not the binding. **Prevents:** a void live round measuring defaults. **Authority:** `AddNodeImagesExtensions.BindStableDiffusionRuntimeOptions`, `StableDiffusionRuntimeOptionsBindingTests`. [evidence](../agent-knowledge-evidence.md#a-providers-tryaddsingletonnew-xoptions-binds-nothing-prove-a-config-key-end-to-end-before-measuring-with-it)

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

**Rule:** (rules already in `docs/wiki/18-training.md` "Constraints and traps" are not repeated.) `training_datasets.definition_json` is pinned in the transaction that reads `DefinitionVersion`; generation/evaluation read `DatasetDefinitionService.ReadPinnedBody`, and a null pin refuses with `UnpinnedDatasetReason`. V1 `TeacherSampleRecordV1` is exactly one tool call (`SampleValidationPipeline`, `EvaluationScorer.RejectMultiCall`). Comparison identity is dataset ID + content fingerprint + order-insensitive hold-out set, never run ID. Evaluation refuses fingerprint drift at create and at claim/load and keeps the per-ID missing-sample check. Link runs via `IInstalledBaseModelLinker`, never by display name. Delete artifacts through `ITrainingExportService.DeleteArtifactAsync`. **Authority:** the symbols named. [evidence](../agent-knowledge-evidence.md#training-live-gate-incidents)

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

## Layering

### a reverse provider→application `ProjectReference` fails at RESTORE, not in an architecture test

**Rule:** a break-proof adding a `ProjectReference` back up the layering graph never reaches a test: `dotnet restore XE-Local-AI-Engine.slnx` fails first with `MSB4006` circular dependency (`_GenerateRestoreProjectPathWalk`), naming the mutated csproj. Report that restore failure as the proof. **Prevents:** concluding the architecture test is broken because it never ran. **Authority:** the `MSB4006` diagnostic restore prints. [evidence](../agent-knowledge-evidence.md#a-reverse-providerapplication-projectreference-fails-at-restore-not-in-an-architecture-test)

## Covered elsewhere

- GPU-load admission gate (AUD4-06) — `docs/wiki/03-local-runtime-and-providers.md` ("Spawn attempt sequencing, startup capture and one-shot fallbacks", "GPU-load admission (AUD4-06)" paragraph); never hold the GPU-load gate across a `RunExclusiveProfilingAsync`/`RunExclusiveBenchmarkAsync` body, only the per-key ensure gate
- A GPU-vendor check is not a device check: pick a CUDA prebuilt only after `ICudaDeviceProbe` agrees — `docs/wiki/03-local-runtime-and-providers.md` ("Device audit (AUD4-03)" paragraph), `docs/wiki/24-audio-transcription.md`
- `nvcuda.dll` present is not a CUDA device; peek the cached llama.cpp audit, never probe on a spawn path — `docs/wiki/03-local-runtime-and-providers.md` ("Device audit (AUD4-03)" paragraph)
- Benchmark verifiable rubric criteria (2026-08-26) — `docs/wiki/20-benchmarks.md` ("5.2 Verifiable criteria — judging with no model")
- Benchmark export (2026-08-26) — `docs/wiki/20-benchmarks.md` ("9. Export")
- Benchmark task suites: freeze fan-out and cell ranking (2026-08-26) — `docs/wiki/20-benchmarks.md` ("3. Freeze", "4. Ranking — the cell is the unit")
- Benchmark paired-difference intervals (2026-08-26) — `docs/wiki/20-benchmarks.md` ("5.4 Comparing two cells — the paired-difference interval")
- Benchmark frontend for task suites (2026-08-26) — `docs/wiki/20-benchmarks.md` ("10. The frontend")
- live transcription never ends a session for being slow; only a memory cap does, gracefully — `docs/wiki/24-audio-transcription.md` ("The segmenter", "The registry", `Transcription:MaxBufferedAudioMb`)
- OllamaSharp types live only in `Providers.Ollama`, and a `PackageReference` allowlist is what enforces it — `docs/wiki/02-project-layout.md`, `docs/wiki/16-code-conventions.md`; enforced by `LayerDependencyTests.ProductionProjects_HaveOnlyTheApprovedPackageReferences`

## Stale beliefs

Superseded claims; the entries above are the active rules.

| Stale belief | Current correction |
|---|---|
| There is no speech-to-text in this repo. | whisper.cpp ships: batch upload, live sessions over `TranscriptionHub`, and browser capture in the SPA (§6). |
| llama.cpp native MCP should replace the app client. | Its MCP proxy is for llama-server's browser UI; keep the .NET MCP integration and approval boundary (§3). |
| NVFP4 is unsupported. | NVFP4 GGUF works; NVFP4 safetensors conversion does not (§3). |
| Advisor runs an llmfit container/HostAgent. | It uses in-process fit estimation and live HF discovery (§3). |
| Recommendation is raw-size descending. | Explore ranking is capability-bucketed (§3). |
| `ModelKind` has only Unknown/Chat/Embedding. | Reranker is a fourth kind and is checked first (§3). |
| A benchmark project asks one question. | A project holds 1..N task items; a single item is the degenerate case (§3). |
| A run is the unit that ranks. | The CELL ranks — one model x KV type x repeat over the whole item suite — and a run reports its cell's rank (§3). |
| A cell exists only when there is a repeat group. | A cell exists whenever one freeze produces more than one run per model; `cellGroupId = repeatGroupId ?? (leafItems > 1 ? new : null)` (§3). |
| Repeats are the only reason to launch more than one run per model. | A suite fans out one run per leaf item, and the probe cases of a NIAH generator each count (§3). |
| A user score rescues any excluded run. | It sits below `item-revised` and `item-set-revised`: an operator cannot notice that a question or the suite around it moved (§3). |
| Fidelity is measured once per repeat group. | Once per CELL — the item half of the rule is the lowest `task_item_index`, and it is expressed in three places that must stay in step (§3). |
| A verifiable criterion is decided by the policy config alone. | The judge resolves `item override ?? policy config` for both the verifier config and the reference answer (§3). |
| A verifier that cannot run scores 0. | It fails the judging and the run is excluded as `verifier-unavailable`; only a TIMEOUT is a real 0 (§3, §4). |
| The eligible-model listing re-hashes GGUF members. | Listing trusts recorded registry facts; only the freeze re-hashes (§3). |
| Export schema is 3. | 4 since task suites — `taskItems[]`, `cells[]` and six appended CSV columns (§3). |
