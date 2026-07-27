# 2026-07-26 local-inference evidence summary

This summary records the evidence produced for the local-inference performance
plan. It does not broaden the claims made by the machine-readable artifacts in
`docs/performance/baselines/`.

## Captured configuration

- Baseline source: `e67d669709169fefc4cffab444af66654526a8d8` with
  MAF 1.13.0, MEAI 10.7.0, OpenAI 2.11.0, and MCP 1.4.0.
- Candidate source: `932cad7dad8f1c6ee0e715c9eb0de92f5dab9e61` with
  MAF 1.15.0, MEAI 10.8.1, OpenAI 2.12.0, and MCP 1.4.1.
- Runtime: managed source build of llama.cpp `b9692`/`f3e1828`, with the
  `llama-server`, `llama-bench`, `llama-fit-params`, and runtime-local dependency
  hashes retained in each artifact.
- Corpus: pinned `golden-v1.json`, SHA-256
  `100b6a808653f1a6c0867f5628c6d589b91537a867a7f79535d8192a0371fea5`.
- Machine: the same WSL2 host and RTX 5090 were used for the comparable baseline
  and candidate captures.
- Provenance: both historical source trees were restored and built cleanly before
  recapture. The CUDA artifacts bind each of the four framework, application, and
  provider commands to its verified Git HEAD, clean worktree, exact
  `Directory.Packages.props` hash and declared package versions, plus non-empty
  hashes for every command's relevant product, test, MAF, MEAI, OpenAI, and MCP
  assemblies. The CPU artifacts contain native-performance commands only, so their
  verified framework identity correctly records `required: false`. The current
  fail-closed comparator accepts both CPU and CUDA pairs.

## Results and gate decision

The CUDA native-performance deltas were:

- chat generation: +4.290%;
- chat prompt processing: -19.046%;
- embedding processing: -22.464%.

The CPU native-performance deltas were:

- chat generation: +1.039%;
- chat prompt processing: +3.096%;
- embedding processing: -10.082%.

These are fixed-system prerequisite-versus-candidate measurements, not
framework-only attribution. The exact framework and application contract suites
remained green; their single-run wall-time changes are retained as diagnostic
evidence, not throughput claims. The recaptured native values also demonstrate why
the approved gate must remain fail-closed: the identical llama.cpp binary showed
material negative variance under this WSL2 host's ambient GPU conditions. No
positive result reaches the plan's at-least-20% throughput gate, so this
prerequisite/framework delta authorizes no tuning claim. The first Lane 4 capture
was invalidated during review and replaced by a schema-2 recapture with canonical
baseline comparison, actual role preflights, explicit context readback, and
fail-closed process-memory evidence. The corrected grid produced zero qualifying
cells, so no production tuning ships. See `2026-07-26-lane4-no-change.md`.

## Fit/replay and VRAM semantics

The original fit artifact was invalidated and removed: its `-c 0 -ngl -1`
values are llama.cpp b9692's unresolved context/automatic-placement sentinels,
not a frozen replay. The corrected proof at source `3d889021` is
`baselines/2026-07-27-3d889021-fit-replay.json` and records:

- exact production Explore argv observed through the Aspire application:
  positive policy context `-c 32512`, `--fit on`, diagnostic `-v`, and
  `-fa on -ctk q8_0 -ctv q8_0`;
- exact helper projection of that successful vector, whose raw output was
  `-c 32512 -ngl -1`;
- normalization to concrete replay `-c 32512 -ngl -2` only because the actual
  Explore startup independently reported full `N/N` GPU offload;
- replay preservation of `q8_0/q8_0` KV cache plus flash attention, with
  byte-equivalent non-fit/non-diagnostic arguments;
- peak RSS delta: 0.017%, within the 20% proof tolerance;
- global GPU-used delta: 0%, within tolerance;
- global free VRAM: 28,427 MiB for both resident samples;
- process-budget free VRAM: 30,927 MiB.

The 2,500 MiB reader divergence is deliberately retained rather than averaged:
global free VRAM governs contention and sample invalidation, while the runtime's
process-budget reader describes its fit budget. Default verbosity emitted no
`common_params_fit_impl` detail lines; verbose mode emitted three, and the sibling
helper provided the stable machine-readable vector.

The original production-API run replayed the version-2 profile for five chat runs
at 602.15 tokens/s and froze it. That historical run sampled only after the
profiling process was resident, so its stable 2,500 MiB gap cannot prove the host
was free of ambient contention. It remains evidence for replay correctness, but
is superseded as benchmark-admission evidence.

The corrected version-3 path captures both readers after same-key eviction and
before the profiling spawn. On the final Aspire/Chrome validation run it observed
30,150,754,304 global-free bytes versus a 32,429,309,952-byte process budget:
a 2,278,555,648-byte (7.026%) material divergence. The API returned HTTP 400
before any benchmark workload, the UI kept the version-3 profile `Explored` with
Freeze disabled, and the operator-facing alert named external GPU pressure. The
sanitized proof is
`baselines/2026-07-27-e29571c4-ambient-vram-rejection.json`.

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
- A clean native-Windows idle capture is still required to produce a new
  version-3 frozen benchmark; the final WSL2 run correctly rejected the ambient
  2.12 GiB reader divergence rather than relabelling it as clean.
