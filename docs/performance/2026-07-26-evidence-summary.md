# 2026-07-26 local-inference evidence summary

This summary records the evidence produced for the local-inference performance
plan. It does not broaden the claims made by the machine-readable artifacts in
`docs/performance/baselines/`.

## Captured configuration

- Baseline source: `e67d6697` with MAF 1.13.0, MEAI 10.7.0, OpenAI 2.11.0,
  and MCP 1.4.0.
- Candidate source: `932cad7d` with MAF 1.15.0, MEAI 10.8.1, OpenAI 2.12.0,
  and MCP 1.4.1.
- Runtime: managed source build of llama.cpp `b9692`/`f3e1828`, with the
  `llama-server`, `llama-bench`, `llama-fit-params`, and runtime-local dependency
  hashes retained in each artifact.
- Corpus: pinned `golden-v1.json`, SHA-256
  `100b6a808653f1a6c0867f5628c6d589b91537a867a7f79535d8192a0371fea5`.
- Machine: the same WSL2 host and RTX 5090 were used for the comparable baseline
  and candidate captures.

## Results and gate decision

The CUDA native-performance deltas were:

- chat generation: -2.383%;
- chat prompt processing: +3.187%;
- embedding processing: +0.028%.

The CPU native-performance deltas were:

- chat generation: +1.164%;
- chat prompt processing: +9.866%;
- embedding processing: -6.311%.

These are fixed-system prerequisite-versus-candidate measurements, not
framework-only attribution. The exact framework and application contract suites
remained green; their single-run wall-time changes are retained as diagnostic
evidence, not throughput claims. No result reaches the plan's at-least-20%
throughput gate, so no Lane 4 optimization grid is authorized by this evidence.

## Fit/replay and VRAM semantics

The successful CUDA fit proof records:

- helper output and replay placement: `-c 0 -ngl -1`;
- byte-equivalent non-fit explore/replay arguments;
- peak RSS delta: 0.013%, within the 20% proof tolerance;
- global GPU-used delta: 0%, within tolerance;
- global free VRAM: 28,980 MiB for both resident samples;
- process-budget free VRAM: 30,927 MiB.

The 1,947 MiB reader divergence is deliberately retained rather than averaged:
global free VRAM governs contention and sample invalidation, while the runtime's
process-budget reader describes its fit budget. Default verbosity emitted no
`common_params_fit_impl` detail lines; verbose mode emitted three, and the sibling
helper provided the stable replay vector. One clean capture attempt was discarded
because its explore/replay peak RSS delta exceeded 20%; the successful rerun did
not relax the gate.

## Explicit hardware and distribution gaps

- Pinned-prebuilt helper availability remains unvalidated.
- BYO runtime support remains capability-dependent; a missing sibling helper is
  reported as unsupported rather than replaced by log scraping.
- Native-Windows idle/game VRAM evidence requires the supplied manual PowerShell
  capture.
- Vulkan is unvalidated because this host has no working NVIDIA Vulkan ICD.
- Native-Linux OOM behavior cannot be inferred from WSL2/WDDM.
- 8 GB/constrained-VRAM behavior needs representative hardware or a proven
  process-budget constraint.
- MoE placement is outside the fixed dense-model experiment and remains excluded.

