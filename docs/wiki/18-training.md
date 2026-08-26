# Training (QLoRA fine-tuning)

> Baseline: `65de769ded3eb6e7b59eabb5daf6a8d0b89531ba` · Reviewed: 2026-08-17 · Code-grounded.

The Training group lets an operator turn the node's own tool-calling behaviour into a **fine-tuned local model**, entirely on the box: a teacher model generates a supervised dataset, a QLoRA run trains an adapter against a downloaded Hugging Face base checkpoint, and the result is exported to GGUF, smoke-loaded, evaluated from staging, compared against its installed base, quality-decided, and only then explicitly promoted into the local model registry. Nothing leaves the node except the two explicit downloads (the Python wheel closure and the base checkpoint).

Two decisions shape everything on this page, both recorded in [ADR 0005](../adr/0005-training-runtime-python-exclusivity-and-project-placement.md):

1. **All training semantics live in Python**, inside a `uv`-managed, lockfile-pinned venv. C# owns process lifetime, phase/progress, artifact identity, persistence and policy — never optimizer math, tokenization, chat-template rendering or quantization arithmetic. The whole interface is a line-delimited JSON stdio protocol.
2. **A run holds the node exclusively.** While a training run, evaluation run or export is admitted, the node does no chat, no embeddings, no image generation and no benchmarks. That exclusivity is one lock — `IGpuWorkGate` — and taking it *is* the check.

---

## Where the code lives

| Concern | Project / path |
|---|---|
| uv/venv/subprocess mechanics only (ADR 0005 §3) | `XE-Local-AI-Engine.Providers.Training/` — `Contracts/ITrainingRuntimeService.cs`, `Implementation/TrainingRuntimeService.cs`, `Implementation/UvBinaryAcquirer.cs`, `Implementation/TrainingRuntimeLayout.cs`, `TrainingRuntimePins.cs` |
| Linux process spawn / group kill / inspect | `…/Providers.Training/Implementation/LinuxTrainingProcessSpawner.cs`, `…/LinuxTrainingProcessRunner.cs`, `…/LinuxTrainingProcessGroupHandle.cs`, `…/LinuxTrainingProcessInspector.cs` |
| The node's single GPU admission point | `XE-Local-AI-Engine.Client.Application/Services/Training/GpuWorkGate.cs` (`IGpuWorkGate`) |
| Dataset definitions, generation, review, export | `…/Services/Training/Datasets/` — `DatasetDefinitionService.cs`, `DatasetGenerationService.cs`, `DatasetGenerationExecutor.cs`, `StructuredAgentRunner.cs`, `SampleValidationPipeline.cs`, `ToolMockService.cs`, `DatasetExportService.cs` |
| Base checkpoint acquisition + licensing | `…/Services/Training/BaseArtifacts/` (`BaseArtifactService.cs`, `BaseArtifactDownloadCoordinator.cs`), `…/Services/Training/Runs/LicenseGateService.cs` |
| Run queue, executor, capacity, defaults | `…/Services/Training/Runs/` — `TrainingRunQueueHostedService.cs`, `TrainingRunExecutor.cs`, `TrainingCapacityGate.cs`, `TrainingFootprintEstimator.cs`, `TrainingOptionDefaultsCalculator.cs`, `InstalledBaseModelLinker.cs`, `TrainingRunStartupReaper.cs`, `TrainingRunWorkspace.cs` |
| Export → smoke → promote | `…/Services/Training/Export/` — `TrainingExportService.cs`, `TrainedModelSmokeGate.cs`, `ArtifactPromotionService.cs` |
| Evaluation + comparison | `…/Services/Training/Evaluation/` (`EvaluationRunService.cs`, `EvaluationRunExecutor.cs`, `EvaluationScorer.cs`), `…/Services/Training/Comparison/ComparisonReportService.cs` |
| Python runtime (the shipped manifest) | `tools/training/` — `pyproject.toml`, `uv.lock`, `probe.py`, `train.py`, `export.py`, `trainlib.py`, `exportlib.py` |
| SignalR hubs + relays | `XE-Local-AI-Engine.Client/Hubs/` — `DatasetGenerationHub.cs`, `TrainingRunHub.cs`, `TrainingRuntimeHub.cs`, plus `*HubEventRelay.cs` / `TrainingRuntimeEventPublisher.cs` |
| Local endpoints | `XE-Local-AI-Engine.Client/Endpoints/Training/` (`Definitions/`, `Datasets/`, `Mocks/`, `Runtime/`, `BaseArtifacts/`, `Runs/`, `Evaluations/`, `Comparisons/`, `Exports/`) |
| React feature | `XE-Local-AI-Engine.Client.React/src/features/training/` |

`Providers.Training/Contracts/` references `Providers.Abstractions` only — which is where `INodeDataDirectory` and `IGpuModelLoadAdmission` already live — so the reference is layering-legal by construction and is registered in the exact-match lists in `XE-Local-AI-Engine.Tests/Architecture/LayerDependencyTests.cs`.

---

## Architecture at a glance

```
Operator (React /training, /training/datasets, /training/comparisons)
        │  REST /api/local/v1/training/*        SignalR: DatasetGenerationHub · TrainingRunHub · TrainingRuntimeHub
        ▼
 ┌──────────────────────────── Client.Application/Services/Training ────────────────────────────┐
 │  DatasetGenerationHostedService ──┐                                                          │
 │  TrainingRunQueueHostedService  ──┴──▶  IGpuWorkGate  (one lock: exclusive vs shared)         │
 │                                          exclusive: training run · evaluation run · export   │
 │                                          shared:    benchmark · dataset generation · image   │
 │  TrainingRunExecutor / TrainingExportService  ── also take the llama.cpp                      │
 │                                                  runtime-mutation lease (eject-first)         │
 └───────────────────────────────────────────────┬──────────────────────────────────────────────┘
                                                 │ ITrainingProcessSpawner (Providers.Training)
                                                 ▼
                     uv-managed venv  ──spawn──▶  python train.py / export.py / probe.py
                     (Linux x64 only)             line-delimited JSON events on stdout
                                                 │
                     staged artifacts ──▶ convert_hf_to_gguf.py / convert_lora_to_gguf.py / llama-quantize
                                                 │
                     smoke/evaluation harnesses ──load──▶ transient llama-server (harness-owned load admission)
                                                 │
                     ArtifactPromotionService ──▶ the same GGUF acquisition preflight + importer every local import uses
```

---

## 1. The Python runtime (uv, pinned, machine-global)

`ITrainingRuntimeService` (`TrainingRuntimeService`) provisions the venv single-flight. It is **Linux-x64-only by gate** (`TrainingRuntimePrerequisiteKeys.Platform`), and the committed lockfile narrows resolution to the same platform via `tool.uv.environments`.

- `UvBinaryAcquirer` downloads the pinned `uv` release and verifies its SHA-256. Both live in `TrainingRuntimePins` (`UvVersion`, `UvAssetName`, `UvSha256`) — uv publishes a `.sha256` per asset, unlike llama.cpp, so a version bump re-fetches it rather than reading the Releases API.
- Install phases are `TrainingRuntimePhase`: `Idle → AcquiringUv → ProvisioningPython → InstallingPackages → Verifying → Ready` (plus `Failed`, `Removing`). A refusal to start is a `TrainingRuntimeInstallOutcome` (`AlreadyRunning`, `InsufficientDisk`, `MissingPrerequisites`), not an exception.
- `Verifying` runs `probe.py` inside the fresh venv and reads its one-line JSON handshake (`TrainingRuntimeProbeReport`). A probe whose `contractVersion` differs from `TrainingRuntimePins.ProbeContractVersion` is **rejected rather than adopted** — the scripts and the managed side are versioned together.
- Adoption is **one rollback boundary** spanning both the directory swap and the `InstalledTrainingRuntimeStore` write (`TrainingRuntimeLayout` names `active` / `.staging` / `.backup` under `{LocalApplicationData}/XE-Local-AI-Engine/training-runtime`). The backup is consumed only after both steps succeed.
- A failed reprovision that left the previous runtime intact terminalizes **`Ready`** carrying the reason in `SanitizedError`; only a failure with no surviving runtime ends `Failed`. `TrainingRunExecutor` and `TrainingExportService` gate on `Phase == Ready`.

**The shipped manifest is `tools/training/pyproject.toml` + `uv.lock` and nothing else.** The app copies exactly those two files into staging and runs `uv sync --locked` (no `--no-dev`), so a dev dependency group added there would install linters into every user's training venv. Repo-wide Python tooling lives in the **root** `pyproject.toml` instead (see §7).

---

## 2. Dataset generation

A **dataset definition** (`TrainingDatasetDefinition`) describes what to generate; `POST training/definitions/{definitionId}/generate` creates a `TrainingDataset` and enqueues its single `DatasetGenerationWorkItem`. `DatasetGenerationHostedService` is a single-consumer durable FIFO; `DatasetGenerationExecutor` runs one dataset to a terminal state and owns durable terminalization.

- `StructuredAgentRunner` drives a local teacher model with a **JSON-schema-constrained** turn and per-turn deadline (`StructuredAgentRunner.TurnTimeout`); `HeadlessToolExecutor` + `ToolMockService` supply the mocked tool surface (`ToolMockDefinition`, verify verb at `training/mocks/{mockId}/verify`).
- `SampleValidationPipeline` validates each record against the **original** schema. v1 samples demonstrate **exactly one** tool call (`TeacherSampleRecordV1`), so a trajectory with more than one tool part is rejected.
- Samples are staged inert: `TrainingSampleReviewState` is `Pending` until an operator approves or rejects (`training/datasets/{datasetId}/samples/{sampleId}`), and provenance is `Generated` or `Manual`.
- **A dataset pins its definition body.** `training_datasets.definition_json` carries the body copied in the same transaction that read `DefinitionVersion`; the executors read that pin (`DatasetDefinitionService.ReadPinnedBody`), never the live definition row, which an operator can edit in between. The column is nullable on purpose, and a null pin is refused (`DatasetDefinitionService.UnpinnedDatasetReason`) rather than falling back.
- `DatasetExportService` writes canonical, **template-agnostic** JSONL (or a Hermes-style form) — the base model's chat template is applied inside the trainer, never at export. Rejected samples are excluded.

Generation takes a **shared** `IGpuWorkGate` admission. `DatasetGenerationService.StartAsync` refuses with a `TrainingBusy` conflict while something holds the gate exclusively, but that refusal is UX only: the queue's claim is what actually enforces exclusivity.

---

## 3. Base checkpoints and the license gate

GGUF files are **not trainable** — a run needs a Hugging Face checkpoint. `BaseArtifactService` resolves the operator-selected repository, preflights free disk with a fixed `DiskHeadroomBytes` margin on top of the manifest size (the frozen dataset copy, the run workspace and the export all land on the same volume immediately afterwards), records a `TrainingBaseArtifact` and hands the transfer to `BaseArtifactDownloadCoordinator` (`training/base-artifacts`, `.../cancel`).

`LicenseGateService` surfaces the checkpoint's licensing for the run wizard and hashes the exact text the operator was shown, so a later reword cannot be mistaken for consent to the same terms. **A repository that declares no license is not a pass** — the confirmation still happens and records that no license metadata was found.

`IInstalledBaseModelLinker` resolves the run's `LinkedInstalledModelName` from the wizard's explicit choice, else the official `<base>-GGUF` repo or the same repo id — never a display-name guess. Without that link an adapter export has no base to be smoke-tested against with `--lora`, cannot be promoted, and a comparison has no base side.

---

## 4. Training runs

`POST training/runs` only **enqueues** — `TrainingRunQueueHostedService` is a single-consumer durable FIFO whose work items pin `attempt = 1` (a CHECK constraint on `training_work_items`), so there is no retry to fall back on.

- **Everything exclusive is acquired before the claim.** Two gates, in order: an exclusive `IGpuWorkGate` hold, then the llama.cpp runtime-mutation lease (`TryAcquireRuntimeMutationLeaseAsync`), which refuses while any model is loaded. A refusal simply leaves the item `Queued` for the next poll. The queue **peeks the head's kind** before acquiring (`PeekNextKindAsync`) because an attempt-pinned claim can never be handed back.
- **An evaluation run takes the first gate and does not directly take the second.** Both installed-base and staged-tuned evaluations use `ITransientLlamaServerEvaluationHarness`, which owns the runtime-mutation lease and model-load admission in the correct order. Installed models additionally hold a coordinated read snapshot through teardown, binding the composite registry identity and verified weight SHA-256/size while the harness loads the path.
- `TrainingRunExecutor` drives one claimed run from `Preparing` to terminal: reserve capacity (`TrainingCapacityGate`), verify the runtime, decrypt the frozen dataset into owner-only scratch (`TrainingRunWorkspace`), spawn `train.py`, and follow its stdio protocol. `TrainingRunStatus` is `Queued → Preparing → Training → Exporting → Smoke →` (`Succeeded` | `Failed` | `Cancelled`).
- The **launch receipt is persisted immediately after spawn, before any output is read** — that is the only window in which a host crash could otherwise strand a trainer holding the whole GPU with nothing on disk to identify it by. Only `TrainingRunStartupReaper` clears a receipt, and only after a successful kill or a proven non-match; a receipt whose inspect or kill threw is left in place and retried next startup.
- Two independent bounds sit over the stream: an **inactivity watchdog** driven by the protocol's `heartbeat` event (a trainer wedged on a CUDA call prints nothing at all), and a max-duration backstop.
- **Cancellation is cooperative.** The operator's cancel signals the process *group* with SIGTERM, `train.py` latches `should_training_stop`, finishes its step and exits with a distinct status, and the run records `Cancelled`. Only the watchdog escalates to SIGKILL.
- `TrainingFootprintEstimator` sizes one QLoRA run against the box (4-bit frozen base ≈0.6 bytes/param, bf16 LoRA weights + two 8-bit Adam moment buffers ≈4 bytes per *trainable* param, activations as a headroom term), with `TrainingOptionDefaultsCalculator` computing the wizard's hyper-parameters. `GET training/runs/defaults` returns both plus the licensing text.

### The stdio protocol (contract version 1)

`train.py` emits one JSON object per line on stdout; **every other line is ignored**, because importing unsloth and torch prints banner text the script does not control.

`handshake` · `phase` (`loading` | `tokenizing` | `training` | `saving`) · `progress` (step/totalSteps/epoch/loss/lr/vramBytes) · `heartbeat` · `artifact` · `done` · `error`. Exit status is `0` success, `3` cooperative SIGTERM stop (recorded `Cancelled`, not `Failed`), `1` any error. `export.py` uses the same protocol with phases `loading` | `merging`.

---

## 5. Export, smoke and promotion

An export takes the **same hold a run does** — exclusive `IGpuWorkGate` plus the mutation lease — acquired before anything is written, so a refusal is an immediate, harmless 409.

- `TrainingArtifactKind` is `AdapterGguf`, `MergedGguf` or `HfAdapterDir`; all are staged under the run's own directory until promotion.
- The **adapter** path has no Python step: the host runs llama.cpp's `convert_lora_to_gguf.py` straight against the trainer's adapter directory. The **merged** path runs `export.py` to produce a 16-bit HF checkpoint, then the host runs `convert_hf_to_gguf.py` and `llama-quantize` **at the pinned llama.cpp commit**, so the file a model is served from comes from the same source tree as the server that loads it (`save_pretrained_gguf` is deliberately never called).
- **The run's status is never moved by an export.** Training already terminalized the run; the export's outcome lives entirely on the artifact row (digest, smoke state, reason), with live progress on the run hub. A run that succeeded stays succeeded.
- `TrainedModelSmokeGate` is the loadability/tool-syntax gate. It answers two questions a digest cannot — does `llama-server` actually load this file, and does the model still emit a syntactically valid tool call — because fine-tuning can destroy tool-calling while leaving a file that loads perfectly. `TrainingArtifactSmokeState` is `Pending`/`Passed`/`Failed`/`Skipped`, and `Skipped` is a deliberate operator choice, not a silent pass. Smoke and evaluation each take model-load admission through their owning transient-runtime component; callers do not hold a second admission ticket.
- Promotion additionally requires a versioned quality decision over a complete base+tuned comparison with identical frozen membership and exact model/artifact identities. Aggregate and per-kind accuracy may not regress; benchmark deltas are advisory. Revalidation first replaces the promotable decision with `Pending`, then requires a fresh comparison and fresh base+tuned evaluation ids before a new decision can replace it. An Operator can override only a complete regression failure and must record an audited reason. `ArtifactPromotionService` rechecks the installed base, staged digest, and the importer's prepared SHA-256/size before committing through the same acquisition path every local GGUF import uses. Partial commits and failed persistence commits are compensated with the importer's rollback receipt.
- Ordinary deletion still goes through `ITrainingExportService.DeleteArtifactAsync`, never the store's row-only delete. Once quality evidence references an artifact/comparison, the explicit quality-discard route atomically releases the live comparison reference while retaining the encrypted decision and discard reason/time on the artifact row as an immutable audit tombstone. Staged-byte removal is idempotent: a failed cleanup leaves `DiscardCleanupPending` set so the same route can retry without rewriting the audit decision, and stale-artifact cleanup and comparison suggestions ignore tombstones.

---

## 6. Evaluation and comparison

An evaluation run (`training/evaluations`) scores a model against the **hold-out membership frozen by a training run**, one question per sample.

- **Both sides of a comparison take the hold-out sample ids from the same training run's freeze**, so the base and tuned models answer exactly the same questions. Deriving a fresh split per side would make the two accuracies incomparable while looking like they compared something.
- `EvaluationScorer` is deterministic — no model is consulted, so every verdict is reproducible from the persisted sample and the persisted response. Whether a sample is a no-tool sample is decided **structurally** (the frozen trajectory carries no tool part), not from its kind label. `ScoredBy` is `deterministic` in v1; `judge` is reserved.
- **Evaluation replays the run-owned immutable corpus, never live sample rows.** The encrypted frozen JSONL records its schema version and stable sample id on every row; the freeze binds its SHA-256 digest and hold-out ids. `EvaluationRunExecutor.LoadAsync` validates the freeze id, dataset-version fingerprint, digest and complete hold-out membership before constructing a model client. A later approve/reject/relabel operation can therefore change the live dataset without changing or preventing evaluation of the already-frozen run. Legacy v1 corpora remain readable through the freeze's sequence-to-id mapping.
- `TrainingEvaluationStatus` is flatter than a run's: `Queued → Running →` (`Succeeded` | `Failed` | `Cancelled`). Evaluations ride the same single-consumer queue. The transient harness binds encrypted execution provenance after validating its launch receipt and before the first verdict is persisted; `training/evaluations/{evaluationId}/resume` may continue a partial run only when the new attempt presents byte-identical provenance, so results from different runtime identities can never be merged.
- `ComparisonReportService.CreateAsync` **refuses unless both evaluation records and both `TrainingEvaluationMembershipV1` documents belong to the same training run**, and the memberships agree on `DatasetId`, `DatasetContentFingerprint` and the hold-out id set (order-insensitive); an unreadable membership is refused too. The report's run id is derived from that validated lineage rather than caller input. `training/comparisons/suggest` pre-fills a create dialog from one training run's lineage, and optional benchmark-run ids can be bound alongside the two evaluations.
- `POST training/comparisons/{comparisonId}/benchmark` (`ComparisonBenchmarkHandoffService`) turns a finished comparison into a benchmark project with its **paired base/tuned runs**, closing the gap the old deep link left — that link could only *select* runs that already existed. Both models are frozen against one project through one `BenchmarkFreezeScope` with the same KV-cache type and repeat count, so they differ in the model and nothing else. The benchmark **task is required from the operator**: a comparison's evaluation prompt scores hold-out samples and would benchmark the wrong thing. Both sides resolve to **installed** model names — the tuned side is usually a `StagedTrainingArtifact` evaluation, so it resolves through the artifact's `CommittedModelName` and is refused until promotion has registered it (which is also what stamps `LocalModelOrigin.Trained`); both sides resolving to one name is refused too, since two runs of one model are not a comparison. An existing project with the comparison's name is reused, so re-running the hand-off adds to the same rank cohort.

---

## 7. Endpoints, hubs and background services

Routes are the `LocalApiRoutes.Training` nested class in `XE-Local-AI-Engine.Client/Endpoints/Common/LocalApiRoutes.cs` — read it for the current, complete list. The families are: `training/definitions(/{id}/generate)`, `training/datasets(/{id}/samples|export|cancel)`, `training/mocks(/{id}/verify)`, `training/runtime/{status|prerequisites|install|remove}`, `training/base-artifacts(/{id}/cancel|license)`, `training/runs(/{id}/cancel)` + `training/runs/defaults`, `training/evaluations(/{id}/resume|cancel)`, `training/comparisons(/{id})` + `training/comparisons/suggest` + `training/comparisons/{id}/benchmark`, and the export/artifact set `training/runs/{runId}/exports|artifacts` + `training/artifacts/{artifactId}(/smoke|/promote|/quality|/quality/revalidation|/quality/override|/quality/discard)`. All are Operator-gated and loopback-only like the rest of `/api/local/v1` — see [API & Hubs](09-api-and-hubs.md) and [Security & Privacy](12-security-and-privacy.md).

Three hubs, all `[Authorize(… Policy = Operator)]` and mapped unconditionally in `Program.cs`:

| Hub | Path constant | Shape |
|---|---|---|
| `DatasetGenerationHub` | `Training.DatasetGenerationHub` | `Subscribe(datasetId, afterSeq)` joins the group **then** replays, closing the subscribe-after-publish race; events `datasetGeneration.event` / `datasetGeneration.replayReset` |
| `TrainingRunHub` | `Training.RunHub` | Same join-then-replay shape per run; events `trainingRun.event` / `trainingRun.replayReset`. Evaluation progress rides this hub too |
| `TrainingRuntimeHub` | `Training.RuntimeHub` | Deliberately empty class — one machine-global runtime, so there is nothing to subscribe to. Event `trainingRuntime.statusChanged` (`TrainingRuntimeHubEvents`), carrying only the lines appended since the last push plus their start sequence |

Hosted services (`AddNodeTrainingDatasetExtensions` / `AddNodeTrainingRunExtensions`, plus the relays in `ConfigureServices`): `DatasetGenerationHostedService`, `TrainingRunQueueHostedService`, `TrainingRunStartupReaper`, `DatasetGenerationHubEventRelay`, `TrainingRunHubEventRelay`. See [Hosting & Deployment](11-hosting-and-deployment.md).

---

## 8. Persistence

Entities are in `Client.Persistence/Entities/`, mapped by the matching `Configurations/*Configuration.cs`, and reached only through stores:

`TrainingDatasetDefinition` (`training_dataset_definitions`) · `TrainingDataset` (`training_datasets`) · `TrainingDatasetSample` (`training_dataset_samples`) · `ToolMockDefinition` (`tool_mock_definitions`) · `DatasetGenerationWorkItem` (`dataset_generation_work_items`) · `TrainingBaseArtifact` (`training_base_artifacts`) · `TrainingRun` (`training_runs`) · `TrainingWorkItem` (`training_work_items`) · `TrainingArtifact` (`training_artifacts`) · `TrainingEvaluationRun` (`training_evaluation_runs`) · `TrainingComparisonReport` (`training_comparison_reports`).

Training artifact paths, digests, smoke state and discard metadata are structural/operator-facing plaintext. The artifact's quality-decision document is separately registered with the encryption interceptors, alongside definition bodies, dataset/sample payloads, mock configuration, run configuration, evaluation results and comparison reports. These documents are **encrypted at rest** with column-specific AAD; dataset samples take the skill-resource treatment (the owning dataset id is the AAD's record component). Migrations are the `AddTraining*` set in `Client.Persistence/Migrations/` — see [Data & Persistence](08-data-and-persistence.md) for the schema conventions and the migration inventory.

---

## 9. React feature

`src/features/training/` (`components/`, `hooks/`, `models/`, `pages/`, `queries/`) renders three pages — `TrainingPage`, `DatasetsPage`, `ComparisonsPage` — behind three **sibling** routes in `src/routes/_layout/`: `training.index.tsx`, `training.datasets.tsx`, `training.comparisons.tsx`. TanStack file routes nest by filename, so these are deliberately siblings rather than children of a `training.tsx` parent, and **each carries its own `beforeLoad` capability gate**.

The nav flag is `training` in `src/capabilities/NodeCapabilities.ts` — compile-time, and currently **`true`**. Endpoints ship registered and Operator-gated regardless of the flag; while it is off, direct URLs redirect home. `NodeCapabilities.test.ts` pins the whole route map, so a new route must be added there too.

The three hooks (`useDatasetGenerationHub`, `useTrainingRunHub`, `useTrainingRuntimeHub`) follow the standard client conventions in [React Client](10-react-client.md). Strings live under the `training` top-level section of `src/locales/en.json` (and `de.json`), pinned by `src/features/training/I18nParity.test.ts` — see [Translating](../translating.md).

---

## 10. Tests and validation

- **Backend integration** (`XE-Local-AI-Engine.Tests/`): `Training/` (dataset generation, queue, cancel, `GpuWorkGateTests`, headless tool executor, `Evaluation/`, `Export/`), `Endpoints/Training/V1/`, `Providers/Training/` (runtime service, prerequisite probe, probe parser, `UvBinaryAcquirerTests`, base-artifact/checkpoint stores).
- **Persistence** (`XE-Local-AI-Engine.Client.Persistence.Tests/Training/`): the five `AddTraining*MigrationTests`, the store tests, and the encryption tests (`TrainingEncryptionTests`, `TrainingRunEncryptionTests`, `TrainingEvaluationEncryptionTests`, `TrainingStoreNullBlobTests`).
- **Frontend** (Vitest): `features/training/**/*.test.ts(x)` — model parsers, the definition-editor dialog, and `I18nParity.test.ts`.
- **Python**: `tools/training/test_trainlib.py` and `test_exportlib.py`, run by `scripts/python-validation.sh` and the CI `python-quality` job (ruff / pyrefly / pytest / bandit) from the **root** `pyproject.toml`.
- **Tracked E2E** (`XE-Local-AI-Engine.Tests.E2ETests/Tests/TrainingLifecycleE2ETests.cs`): the opt-in E2E host drives authenticated lifecycle routes and the production training executor, export pipeline, evaluation service/executor, comparison/quality policies and promotion guards over the real immutable freeze writer and encrypted SQLite stores. Deterministic seams replace only external Python/llama.cpp execution, installed-model lookup and final registry I/O; launch evidence uses the production invariant that executable and manifest SHA-256 identities match. A nine-stage verdict ledger and negative control refuse a skipped lifecycle phase. This proves orchestration and durable evidence, **not real GPU execution** — the manual live GPU gate remains load-bearing for that outcome. See [Testing & Validation](13-testing-and-validation.md).

---

## Constraints and traps a maintainer must respect

1. **A GGUF is not trainable.** Training needs an HF checkpoint; that is what `training/base-artifacts` exists for.
2. **The gate is one lock, and taking it IS the check.** Never read `IGpuWorkGate.ExclusiveKind` and then act on the answer — that property is for UX refusals only. Queued-but-unclaimed work does not block a run; only work that *holds* the gate does.
3. **Training is unavailable without a successful uv provision, and there is no fallback** — no degraded CPU path, no system interpreter. A failed install must name the failing step.
4. **Never add a dev dependency group to `tools/training/pyproject.toml`.** That file plus `uv.lock` is the shipped runtime manifest; repo tooling belongs in the root `pyproject.toml`.
5. **A run must be linked to its installed base GGUF**, or adapters are dead ends (no smoke, no promotion, no base side for a comparison).
6. **Merged export on a prebuilt runtime stops at the quantizer, by design.** Upstream ships no `llama-quantize` in release archives; only an in-app source build has one. The refusal says "build the runtime from source, or export at F16" — F16 merged export needs no quantizer.
7. **Any llama.cpp pin bump must re-check the convert scripts' top-level imports.** At the current pin `convert_hf_to_gguf.py`/`convert_lora_to_gguf.py` import a `conversion/` package, so provisioning only the two scripts plus `gguf-py` fails with `ModuleNotFoundError`.
8. **A route-bound id in a body DTO can never be `required`.** FastEndpoints deserializes the body before binding route values, so a `required` route id turns every generated-client delete into a 400.
9. **"placed all N layers on the GPU" is the fit-plan, not the audit.** For any live GPU gate use the CUDA `llama-server` override; `IRuntimeDeviceAudit` is the authority on what actually ran on the GPU.
10. **A queued run behind a warm model waits on the eject-first rule.** Dataset generation leaves its teacher resident; `POST model-fit/running/eject` starts the queued run immediately, and the queue logs the waiting→admitted transition once.

---

## Related pages

- [Local Runtime & Providers](03-local-runtime-and-providers.md) — the llama.cpp supervisor, the mutation lease, and in-app source builds (which own `llama-quantize`).
- [Model Fit](07-model-fit.md) — the GGUF catalog, import and acquisition path a promoted artifact lands in.
- [Data & Persistence](08-data-and-persistence.md) — encryption interceptor, AAD conventions, migration inventory.
- [API & Hubs](09-api-and-hubs.md) — `/api/local/v1` mapping, hub inventory, OpenAPI → hey-api regen.
- [React Client](10-react-client.md) — TanStack Query + SignalR conventions used by this feature.
- [Hosting & Deployment](11-hosting-and-deployment.md) — hosted-service inventory and process reaping.
- [Security & Privacy](12-security-and-privacy.md) — Operator gating, loopback-only surface, encryption at rest.
- [Testing & Validation](13-testing-and-validation.md) · [ADR 0005](../adr/0005-training-runtime-python-exclusivity-and-project-placement.md)
