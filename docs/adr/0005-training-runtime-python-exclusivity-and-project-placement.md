# ADR 0005: Training runs in a uv-managed Python runtime, holds the node exclusively, and lands in a thin provider project

- **Status:** Accepted
- **Date:** 2026-08-15
- **Scope:** The Training group (dataset generation, fine-tuning runs, export, evaluation). Nothing else.
- **Authority:** Locked decisions 1, 13 and 14 of the 2026-08-15 training-module plan. Decision 1 was made by the maintainer on 2026-08-15; decisions 13 and 14 were closed by review evidence in the same round and confirmed by the maintainer. This record states them precisely enough to be enforced and to be revisited; it does not reopen them.
- **Relates to:** [ADR 0004](0004-development-mode-container-execution-docker-stopgap.md) — unchanged and unamended by this record. See "Relationship to ADR 0004" below.

## Context

Fine-tuning is a Python problem. The tooling that performs it — Unsloth, PEFT, `transformers`, `bitsandbytes`, and the llama.cpp conversion scripts — exists only as Python, has no maintained .NET equivalent, and changes shape release to release. Nothing about that is negotiable by architecture; the only open question was where the boundary between Python and C# falls, and what the rest of the node is allowed to do while a run is in flight.

Three facts about this repository constrain the answer.

**The box is not a training box that happens to serve inference.** It is an inference node. A single GPU carries chat, embeddings, image generation, benchmarks and model-fit probing, and every one of those paths already contends for it. The supervisor's runtime-mutation lease (`ILlamaServerProcessSupervisor.TryAcquireRuntimeMutationLeaseAsync`) and the GPU load admission semaphore (`IGpuModelLoadAdmission`) exist because that contention is real and was paid for. A training run is not another consumer of that budget — it is a multi-hour, whole-GPU tenant, which is a different kind of thing.

**The host's Python is not usable.** The host's system Python is newer than Unsloth's supported floor at the pin (3.13), and the torch wheels the Blackwell (sm_120) path needs come from the cu128/cu129 index. Depending on whatever interpreter the operator happens to have installed reproduces the toolchain problem ADR 0004 solved for Development Mode, in a feature where the failure mode is a run that trains for two hours and then cannot export.

**Project layering is enforced, not advisory.** `LayerDependencyTests` holds exact-match `ApprovedProjectReferences` and `ApprovedInternalAssemblyReferences` lists, and an unregistered project reference fails the build on the first commit that introduces it. So "where does this code live" is a decision with a test behind it, not a preference.

## Decision

### 1. The training runtime is a uv-managed Python venv, and all training semantics live in Python

`uv` provisions its own Python 3.13 interpreter and resolves a repo-committed lockfile (`tools/training/pyproject.toml` + `uv.lock`): torch from the cu128/cu129 index (cu130 is excluded — bitsandbytes ABI), the Apache-2.0 `unsloth` package and its closure, and the llama.cpp conversion dependency set (vendored `gguf-py` at the inference pin, numpy, sentencepiece, protobuf). The system interpreter is never used. No floating resolves; an upgrade is a deliberate lockfile bump behind the live gate.

**Licensing, stated because the closure is not uniformly Apache-2.0.** `unsloth`'s core is Apache-2.0, but its hard dependency `unsloth_zoo` is **LGPL-3.0-or-later** (live-verified 2026-08-15) — a separate matter from the AGPL Studio and `unsloth_cli` components this decision already excludes, which stay excluded. We consume `unsloth_zoo` unmodified, as a runtime pip dependency resolved into a venv the user provisions on their own machine: it is not vendored, not statically linked, and not distributed with the application, which is what LGPL obligations turn on. Modifying it, or shipping it inside a package, would be a new decision rather than an implementation detail.

`uv` itself is acquired as a managed binary with a pinned version and verified SHA-256 digest, following the existing runtime-acquisition precedent (`LlamaCppBinaryManager`, and the digest-from-the-Releases-API rule in `docs/agent-knowledge.md`).

**C# never expresses training semantics.** No optimizer math, no tokenization, no chat-template rendering, no quantization arithmetic in managed code. C# owns process lifetime, phase and progress, artifact identity, persistence and policy. The entire interface between the two is a structured stdio protocol — line-delimited JSON events out, a frozen configuration document in. Neither Unsloth Studio nor `unsloth_cli` is used; the scripts are ours and are pinned with the rest of the lockfile.

The corollary that matters for review: a pull request that adds a training concept to a C# type is out of contract, even when it would be convenient. The Python side is where fine-tuning knowledge is allowed to accumulate.

### 2. A training run holds the node exclusively

A run acquires two things for its whole duration:

- a process-wide training-activity hold — referred to below as the `ITrainingActivity` marker, which is a conceptual name in this ADR and not a C# type: the mechanism is `IGpuWorkGate.TryBeginExclusive(GpuWorkKind.TrainingRun)`, whose exclusive/shared admission makes this marker and every other queue's converse check one atomic decision under one lock — and
- the existing supervisor runtime-mutation lease (`TryAcquireRuntimeMutationLeaseAsync`), which already refuses while any model is loaded.

That second acquisition gives training the same eject-first user experience the llama.cpp source build already has: a run cannot start while a model is resident, and the refusal surfaces as the established `runtime-busy` outcome rather than as a new failure vocabulary. Dataset generation, benchmark runs and image jobs take a shared admission on the same gate before starting (`IGpuWorkGate.TryBeginShared` with `GpuWorkKind.DatasetGeneration`, `GpuWorkKind.Benchmark`, `GpuWorkKind.ImageJob`), which is refused while an exclusive holder owns the node, and conversely a run start refuses while any of them is active.

**`IGpuModelLoadAdmission` is held only for brief load windows** — the staged-artifact smoke gate, and nothing else. It is never held across a run. This is the sharpest edge in the decision and it is deliberate: that semaphore serializes every model load node-wide, so holding it for hours would not merely queue other work, it would convert every waiting chat and image load into a `GpuModelLoadAdmissionTimeoutException`. Exclusivity is expressed by the training marker plus the mutation lease; the load-admission semaphore keeps meaning what it already means.

### 3. Training lands in a thin `XE-Local-AI-Engine.Providers.Training` project

A new project owns **only** uv/venv/subprocess mechanics — provisioning the environment, launching the trainer, framing the stdio protocol, watchdog and process-receipt handling. Its contracts live in `Providers.Training/Contracts/` and reference `Providers.Abstractions` only, which is where `INodeDataDirectory` and `IGpuModelLoadAdmission` already live, so the reference is layering-legal by construction.

Everything else — the durable queue, EF entities and migrations, capacity accounting, orchestration, endpoints — lives in `Client.Application/Services/Training/`, alongside the benchmark module it structurally mirrors.

Registering the new project in both `LayerDependencyTests` exact-match lists is part of the first commit that references it, not a follow-up.

## Consequences

Stated honestly, including the ones that are costs.

- **Training is unavailable without a successful uv provision, and there is no fallback.** No degraded CPU path, no "use whatever Python is on the box". A failed environment install means no Training feature, and it must fail with an actionable message naming the failing step rather than a generic error. This is the same posture ADR 0004 took for Development Mode, for the same reason: an isolation-or-toolchain guarantee that silently degrades is one nobody can reason about from the outside.

- **First use is a large, slow, network-dependent download.** The torch/CUDA wheel closure is multi-gigabyte. It is one explicit, user-initiated step — the only network step in the feature besides the base-checkpoint download — and it must be resumable and honestly reported, not hidden behind a spinner.

- **The lockfile is a maintenance obligation with a hardware coupling.** Pinning torch to the cu128/cu129 index ties the feature to a CUDA generation. When the box or the driver moves, the pin is a deliberate bump with a live re-verification, not a resolver's choice. Excluding cu130 for a bitsandbytes ABI reason is exactly the kind of fact that is invisible in the diff and expensive to rediscover.

- **Exclusivity is a real product limitation, not an implementation detail.** While a run is active the node does no chat, no embeddings, no image generation, no benchmarks. Runs are hours long. This must be visible in the UI before the run starts, and the refusals in both directions must say which activity is holding the node.

- **Debugging crosses a language boundary.** A failure inside `train.py` reaches the operator only through whatever the stdio protocol carries. That makes the protocol's error and log surface load-bearing: an unstructured traceback that never reaches persistence is an unsupportable feature. The subprocess precedent (`StreamingProcessRunner`) supplies the launch shape but has no stall detection and persists no recoverable process identity, so the training lane adds an inactivity watchdog driven by protocol heartbeats and persisted launch receipts.

- **Two projects means a boundary that will be pushed on.** The pressure to put "just one" queue concern into `Providers.Training`, or one subprocess detail into `Client.Application`, is constant and each individual instance looks harmless. The `LayerDependencyTests` lists catch illegal *references*; they do not catch a misplaced class within a legal reference. That one is a review responsibility.

- **Revisiting this is expected.** If a maintained .NET fine-tuning path appears, or if the node grows a second GPU that makes whole-node exclusivity unnecessary, decisions 1 and 13 respectively should be superseded rather than quietly widened.

## Relationship to ADR 0004

ADR 0004 is unchanged. It permits Docker for Development Mode build/test/lint execution only, and its second boundary — **no Docker on the inference path** — stands in full.

This record does not touch that boundary. The training runtime is a **host process**, exactly like `llama-server`: a uv-managed venv and a supervised Python subprocess, with no daemon, no container, and no image. Training therefore neither exercises ADR 0004's permission nor tests its prohibition.

The two records share one piece of reasoning, which is why the relationship is worth stating rather than leaving implicit: ADR 0004 concluded that confinement does not supply a toolchain, and reached for a container because Development Mode must run *arbitrary user repositories* against SDKs the host may not have. Training's toolchain problem is narrower — one pinned Python closure that we author and lock — so it is solved with a pinned interpreter and a lockfile instead of an image, at a fraction of the operational cost and without adding a daemon requirement to a feature on the GPU path.
