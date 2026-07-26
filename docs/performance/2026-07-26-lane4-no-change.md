# Lane 4 scheduling experiment: no-change decision

Source `88bd2353` was evaluated with the managed llama.cpp b9692 CUDA build and
the pinned Nomic embedding and BGE reranker models. The machine-readable result is
`baselines/2026-07-26-88bd2353-lane4-no-change.json`.

## Bounded grid

The experiment covered 24 cells:

- roles: embedding and reranker;
- backends: CUDA and CPU (`-ngl 0`);
- parallel slots: 1, 2, and 4;
- logical batch: 512 and 1024;
- physical batch: 512 and 1024;
- requested total context: 2048 and 4096;
- per-sequence context: captured from slot startup readback (or the exact b9692
  derivation when the readback was not emitted);
- unified KV: explicit on and off.

Each usable cell ran warmup plus three measured repetitions of short, median,
maximum, large-batch, and concurrent-small corpora. The maximum Nomic document
input was constructed through `/tokenize` and read back as exactly 512 tokens
including `search_document: ` and special-token overhead. Outputs were compared
with the application harness's `1e-5` numeric tolerance.

Chat was not varied. The source commit and supervisor file hash are retained in
the artifact, so the chat launch contract remains byte-identical to the committed
`--parallel 1` policy.

## Findings

- No candidate improved median throughput by at least 20% in **all five**
  scenarios while also passing correctness and the 5% memory bounds.
- CUDA embedding `parallel=2` improved short, median, large, and concurrent
  throughput by 25–60%, but maximum-input throughput regressed 4.4% and semantic
  output equivalence failed.
- CPU embedding `parallel=2` retained semantic equivalence and substantially
  improved short/median/large/concurrent throughput, but maximum-input throughput
  improved only 6.0%, below the shipping gate.
- Four-slot embedding variants improved smaller-input occupancy but did not
  preserve semantic output equivalence; the 1024 physical-batch variant also
  regressed peak RSS by 11.0% on CUDA and 37.4% on CPU.
- Reranker adds its query/pair template after the 512-token document readback.
  Every 512-physical-batch configuration rejected the maximum request at 523
  tokens with llama.cpp's explicit “increase the physical batch size” error.
- Raising reranker physical batch to 1024 removed that rejection, but peak RSS
  regressed 65.2% on CUDA and 46.0% on CPU. The result is not comparable to the
  rejecting baseline for the maximum scenario and cannot qualify.

## Decision

**No production scheduling change ships.** There are zero qualifying cells, so
the current role launch policy remains unchanged. The maximum reranker rejection
is retained as evidence rather than converted into a tuning change: resolving it
requires a separately scoped correctness decision and cannot bypass the Lane 4
throughput and memory gates.

## Coverage gaps

- Vulkan remains unvalidated because this host has no NVIDIA Vulkan ICD.
- Native Windows remains a manual evidence lane.
- Representative 8 GB hardware was unavailable.
- Native-Linux hard-OOM behavior cannot be inferred from WSL2/WDDM.

