# Lane 4 scheduling experiment: corrected no-change decision

> **Historical baseline — do not update the identities below.** The llama.cpp
> tag, model hashes and binary SHA-256 recorded here describe the runtime this
> experiment actually ran on. They are evidence, not configuration. The engine's
> live pin is `LlamaCppReleasePins.PinnedTag` and has since moved past this tag.

Source `88bd2353` was recaptured with the schema-2 scheduling harness, managed
llama.cpp b9692 CUDA build, pinned Nomic embedding model, and pinned BGE
reranker. The sanitized machine-readable result is
`baselines/2026-07-26-88bd2353-lane4-no-change.json`.

## Fixed identities

- llama-server SHA-256:
  `78cd370e18a911b284c0f732e40ada20a1b3c8fc8ba40f2aabb0e8d7d800a3cc`;
- Nomic model SHA-256:
  `d4e388894e09cf3816e8b0896d81d265b55e7a9fff9ab03fe8bf4ef5e11295ac`;
- BGE reranker SHA-256:
  `e186a244ed455b4ab66ec64339ce7427a6ae13f5c0b5e544de96e50f0f8b3673`;
- host GPU: NVIDIA GeForce RTX 5090 with 32,607 MiB reported memory;
- repetitions: one warmup plus three measured repetitions per scenario.

The source commit and supervisor file hash remain recorded, and no chat launch
setting or production policy was changed.

## Bounded grid and hard gates

The experiment covered 24 serialized cells:

- roles: embedding and reranker;
- backends: CUDA and CPU (`-ngl 0`);
- parallel slots: 1, 2, and 4;
- logical batch: 512 and 1024;
- physical batch: 512 and 1024;
- requested total context: 2048 and 4096;
- unified KV: explicit on and off;
- scenarios: short, median, maximum, large batch, and concurrent small batch.

Every backend/role baseline passed its actual role endpoint preflight:
`/v1/embeddings` for the 512-token prefixed Nomic input and `/v1/rerank` for
the 501-token document plus the real query/pair template. No rejecting baseline
was compared. Every cell recorded `llama-server` slot initialization as its
explicit `readback_source`; the observed per-sequence contexts were 2048 for
the baseline, 1024 for two-slot cells, 512 for non-unified four-slot/2048
cells, 2048 for unified four-slot/2048 cells, and 1024 for four-slot/4096
cells.

Each candidate was compared with canonical output from the same backend, role,
and scenario using `1e-5` numeric tolerance with list order preserved. Repeat
determinism remained a separate gate. Distinct deterministic embedding inputs
made response reordering observable.

## Findings

- All four baselines passed actual request preflight, explicit context readback,
  and repeat determinism.
- Every scheduling candidate failed canonical semantic equivalence in at least
  one required scenario. Embedding candidates diverged for short, median,
  large, and concurrent scenarios. Reranker candidates diverged for short and
  concurrent scenarios; the 1024-physical-batch variant also diverged for
  median and large scenarios.
- Because semantic equivalence is a hard gate, affected throughput deltas are
  intentionally non-comparable rather than being used to justify a change.
- The 1024-physical-batch variants also exceeded the 5% RSS bound: 14.0% CUDA
  and 12.4% CPU for embedding, and 38.1% CUDA and 24.7% CPU for reranking.
- CPU cells correctly record GPU memory as `not_applicable` and do not inspect
  unrelated GPU activity.
- WSL2's NVIDIA compute-process query returned explicit zero PID residency for
  every CUDA cell despite successful CUDA inference. The harness records
  `status=zero` and fails CUDA memory comparison closed; it does not substitute
  host-global GPU usage. This is an evidence limitation, but it does not affect
  the decision because every CUDA candidate independently failed semantic
  equivalence.

## Decision

**No production scheduling change ships.** There are zero qualifying cells.
The decision follows the corrected gates rather than the invalidated earlier
capture: actual role preflight, explicit runtime context readback, repeat
determinism, canonical baseline semantic equivalence, at least 20% improvement
in all five scenarios, and no more than 5% RSS/CUDA process-residency
regression.

## Coverage gaps

- PID-scoped CUDA residency is not observable through `nvidia-smi` on this WSL2
  host; zero is explicit and fail-closed.
- Vulkan remains unvalidated because this host has no NVIDIA Vulkan ICD.
- Native Windows remains a manual evidence lane.
- Representative 8 GB hardware is unavailable.
- Native-Linux hard-OOM behavior cannot be inferred from WSL2/WDDM.
- MoE placement was excluded from the fixed dense-model grid.
