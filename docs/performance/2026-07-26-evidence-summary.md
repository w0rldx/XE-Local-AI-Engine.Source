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
- Workload label: the capture records the SHA-256 of pinned `golden-v1.json`
  (`100b6a808653f1a6c0867f5628c6d589b91537a867a7f79535d8192a0371fea5`)
  for provenance, but the native `llama-bench` commands did **not** consume that
  corpus. They used synthetic `-p 512 -n 128` chat and `-p 512 -n 0 -embd 1`
  embedding workloads. These artifacts therefore make no corpus-quality or
  representative-traffic claim.
- Machine: the same WSL2 host and RTX 5090 were used for the comparable baseline
  and candidate captures.
- Privacy: committed artifacts redact the stable NVIDIA GPU UUID; device model,
  driver, memory readings, and the numerical evidence remain unchanged.
- Provenance: both historical source trees were restored and built cleanly before
  recapture. The CUDA artifacts bind each of the four framework, application, and
  provider commands to its verified Git HEAD, clean worktree, exact
  `Directory.Packages.props` hash and declared package versions, plus non-empty
  hashes for every command's relevant product, test, MAF, MEAI, OpenAI, and MCP
  assemblies. The CPU artifacts contain native-performance commands only, so their
  verified framework identity correctly records `required: false`. The current
  fail-closed comparator accepts both CPU and CUDA pairs.

## Results and gate decision

The table below reports median deltas together with the observed
`(maximum - minimum) / median` spread for each baseline/candidate five-repeat
pair and the paired wall-time median delta:

| Backend | Workload metric | Throughput delta | Baseline / candidate spread | Wall-time delta |
| --- | --- | ---: | ---: | ---: |
| CUDA | chat generation | +4.290% | 4.5% / 2.4% | +0.845% |
| CUDA | chat prompt processing | -19.046% | 14.2% / 47.7% | +0.845% |
| CUDA | embedding processing | -22.464% | 6.5% / 33.6% | -0.143% |
| CPU | chat generation | +1.039% | 9.0% / 1.0% | -0.449% |
| CPU | chat prompt processing | +3.096% | 43.2% / 16.4% | -0.449% |
| CPU | embedding processing | -10.082% | 2.6% / 57.3% | +0.054% |

These are fixed-system prerequisite-versus-candidate measurements, not
framework-only attribution. No throughput delta exceeds the larger spread of its
own baseline/candidate pair, and CUDA embedding's -22.464% throughput observation
corresponds to only -0.143% wall time. The data is therefore too noisy for a
directional performance claim. The CPU runs are additionally non-transferable:
their argv pinned `-t 16` while the artifact records eight logical CPUs, a 2×
oversubscription that can explain the large spread. They must be recaptured with a
topology-appropriate thread count before use in a tuning decision.

The exact framework and application contract suites remained green; their
single-run wall-time changes are retained as diagnostic evidence, not throughput
claims. The recaptured native values demonstrate why the approved gate remains
fail-closed. No reliable positive result reaches the plan's at-least-20%
throughput gate, so this prerequisite/framework comparison authorizes no tuning
claim. The first Lane 4 capture was invalidated during review and replaced by a
schema-2 recapture with canonical baseline comparison, actual role preflights,
explicit context readback, and fail-closed process-memory evidence. The corrected
grid produced zero qualifying cells, so no production tuning ships. See
`2026-07-26-lane4-no-change.md`.

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

The version-3 path captures both readers after same-key eviction and before the
profiling spawn. Its first Aspire/Chrome validation observed 30,150,754,304
global-free bytes versus a 32,429,309,952-byte process budget: a
2,278,555,648-byte (7.026%) raw divergence. The API returned HTTP 400 before any
benchmark workload, but external review correctly identified that result as an
idle WDDM false positive. The classifier had compared the whole platform offset
to the same 512 MiB/5% thresholds used for incremental post-load growth.

The corrected pre-spawn classifier subtracts a configurable 1 GiB ambient
baseline first. For the same sample, the pressure-above-baseline is
1,204,813,824 bytes, or 3.715% of the process budget, so the benchmark is
admissible. The 22.9 GiB and 30 GiB ballast fixtures remain rejected at 74.135%
and 94.895% respectively after the allowance. Post-load pressure keeps the
original strict 512 MiB/5% growth rule. The historical sanitized artifact is
retained as the regression input at
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
- A clean native-Windows idle capture is still required to confirm the default
  ambient allowance on the primary shipping host. The WSL2 2.12 GiB sample is
  now correctly retained as idle-regression evidence rather than pressure proof.
