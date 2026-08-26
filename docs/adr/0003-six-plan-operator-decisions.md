# ADR 0003: Six-plan implementation scope and hardware evidence decisions

- **Status:** Accepted
- **Date:** 2026-07-26
- **Scope:** The six plans implemented in the 2026-07-26 batch
- **Authority:** Human operator responses in the implementation handoff

## Context

Three scope choices and one embedding-shape choice blocked implementation of the 2026-07-26 plan set. The operator
answered the structured handoff before implementation:

| Prompt | Operator response |
|---|---|
| Performance optimization scope | `honor_gates` |
| VRAM-reader design | `separate_and_fix` |
| Unavailable hardware completion | `scripts_and_gaps` |
| Nomic Matryoshka output dimension | `512` |

The durable plan text that records these choices was committed after some implementation commits. Commit order therefore
does not establish when the authority was given; this ADR records the operator's earlier handoff answers so the branch
does not rely on implementer-authored status text as its only audit trail.

## Decision

1. Performance candidates ship only when their predeclared correctness and gain gates pass. A no-change result is a valid
   completed outcome; measurements are not rounded into a winner.
2. Machine-global free VRAM and llama.cpp's process-visible budget remain separate facts. Benchmark admission uses the
   global figure for contention, records both figures, distinguishes the WDDM ambient offset from divergence growth, and
   rejects material pressure unless the operator explicitly overrides the pre-spawn gate for that request.
3. Hardware lanes unavailable on the implementation host are completed as copy-pasteable scripts, schemas, and explicit
   validation gaps. They are not replaced by fabricated, mocked, or relabelled hardware evidence.
4. Nomic embeddings are stored at 512 dimensions using the model's supported Matryoshka truncation path. Legacy vector
   rows remain structurally excluded until reindexed.

## Consequences

- The `separate_and_fix` behavior changes are authorized; they do not need to be reverted to the plan's former open state.
- Windows, constrained-VRAM, Vulkan, and other unavailable hardware results remain gaps until real artifacts are returned.
- Checked implementation gates in the plans are evidence produced by the implementation team, not independent reviewer
  approval. Merge-review acceptance remains a separate external action.
- Any future change to these four choices requires a new operator decision rather than editing this record in place.
