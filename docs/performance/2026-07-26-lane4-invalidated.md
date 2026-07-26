# Lane 4 scheduling experiment: invalidated pending recapture

The original source-`88bd2353` scheduling capture is retained at
`baselines/2026-07-26-88bd2353-lane4-invalidated.json`, but it is **not valid
for a shipping or no-change decision**. Review found correctness, memory, token,
and runtime-readback gaps that require a fresh grid run.

Commit `70fdcf13` initially recorded that grid as a no-change outcome. Commit
`1a809330` withdrew that conclusion after the methodology review. The retained
numbers are not a successful Lane 4 result.

## Why the prior decision is invalid

- Each cell checked only repeat determinism. Candidate responses were not bound
  to canonical outputs from the same-backend, same-role baseline, so a stable but
  different result could pass correctness.
- Embedding scenarios repeated identical strings. A response-order defect could
  therefore remain invisible.
- CUDA memory used peak host-global GPU allocation. Unrelated processes could
  change a cell's apparent regression, and CPU cells were incorrectly exposed to
  the same unrelated GPU activity.
- The maximum corpus used document tokenization as the hard gate. Reranker query
  and pair-template overhead appeared only when the request ran, and a rejecting
  baseline was still used for partial comparisons.
- Per-sequence context values were derived from requested context and parallelism
  when the server emitted no explicit slot readback.

The retained throughput and memory samples are historical diagnostics only. The
previous comparisons are nested under `invalidated_prior_decision`; the active
`decision` is `invalidated`, has no comparisons or winners, and explicitly
forbids a production tuning conclusion.

## Corrected recapture contract

The schema-2.0 runner now requires all of the following before a candidate can
qualify:

1. A canonical baseline output for every backend, role, and scenario.
2. Candidate-to-baseline semantic equivalence with `1e-5` numeric tolerance and
   preserved list/order semantics, plus independent repeat determinism.
3. Distinct deterministic embedding inputs in batch and concurrent scenarios.
4. PID-scoped CUDA residency; unrelated PIDs are ignored, CPU cells mark GPU as
   not applicable, and unavailable or zero CUDA measurements fail closed.
5. An actual role endpoint preflight (`/v1/embeddings` or `/v1/rerank`) for the
   maximum request so role-template overhead is exercised. A rejecting baseline
   makes its entire backend/role group non-comparable.
6. Explicit `llama-server` slot-context readback with a recorded
   `readback_source`; missing readback fails the cell without derivation.

A fresh llama-server grid run remains pending. The context-allocation
implementation is present, but this WSL2 host cannot satisfy the corrected CUDA
memory gate: `nvidia-smi --query-compute-apps` returns no process rows here, so a
PID-scoped CUDA residency sample is unavailable. Recapture needs an environment
that can attribute CUDA residency to the launched server, or a separately proven
equivalent collector. Until then, **no Lane 4 production scheduling change ships
and no no-change performance conclusion is claimed**.

## Coverage gaps retained for the fresh run

- Vulkan remains unvalidated because this host has no NVIDIA Vulkan ICD.
- Native Windows remains a manual evidence lane; the corrected grid has not run
  there.
- WSL2 exposes global NVIDIA memory but not the launched server's process
  residency, so CUDA cells cannot pass the corrected per-process memory gate.
- Representative 8 GB hardware is unavailable; RTX 5090 throughput under a
  synthetic memory constraint would not represent an 8 GB GPU.
- Native-Linux hard-OOM behavior cannot be inferred from WSL2/WDDM, which can
  page GPU memory instead of returning OOM.
- MoE placement was excluded from the fixed dense-model grid and remains
  unvalidated.
