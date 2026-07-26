# Local inference performance evidence workflow

This workflow implements the performance plan's Lane 0 baseline, Lane 1 empirical
fit/replay proof, and Lane 2 rebaseline contract. It produces **evidence**, not a
performance claim: an artifact is comparable only when the capture tool confirms
that the model, corpus, runtime binary, launch vector, machine, and device list are
identical.

The scripts have no third-party dependencies:

- `scripts/performance/capture_inference_evidence.py` — Linux/WSL baseline,
  rebaseline, fit/replay capture, and comparison.
- `scripts/performance/capture_windows_vram.ps1` — native-Windows idle-versus-game
  global/process-visible VRAM capture.
- `scripts/performance/tests/test_capture_inference_evidence.py` — deterministic
  self-tests with fake binaries.

Generated artifacts belong under the ignored `artifacts/` directory. Do not commit
machine names, local paths, or raw benchmark output without reviewing it.

To create a reviewable committed artifact, sanitize the raw capture with explicit
path labels. The command fails while any Linux or Windows user-home path remains:

```bash
python3 scripts/performance/capture_inference_evidence.py sanitize \
  --input artifacts/performance/baseline-cuda.raw.json \
  --output docs/performance/baselines/2026-07-26-e67d6697-cuda.json \
  --replace "$PWD/.tmp/perf-models=\$MODEL_ROOT" \
  --replace "$PWD/.tmp/llama.cpp-b9692=\$RUNTIME_ROOT" \
  --replace "/home/w0rldx/projects/XE-Local-AI-Engine/.tmp/worktrees/perf-baseline-e67d=\$BASELINE_REPO"
```

## 1. Fixed capture contract

Before a baseline, create a JSON specification conforming to
`docs/performance/schemas/inference-capture-spec.schema.json`. Every file identity
uses a SHA-256 supplied by the operator and independently verified by the tool.

The fixed model set is:

| Role | Model | Required identity |
|---|---|---|
| Chat | One dense GGUF selected once for the experiment | repository/revision, quant, file SHA-256 |
| Embedding | shipped `nomic-embed-text` default | repository/revision, quant, file SHA-256 |
| Reranker | `gpustack/bge-reranker-v2-m3-GGUF`, `Q4_K_M` | repository/revision, quant, file SHA-256 |

The corpus may be a file or directory. Directory identity is deterministic: sorted
relative paths plus each file hash. The spec must also fix:

- repository source commit and exact MAF, MEAI, and OpenAI package versions;
- runtime tag, provenance (`managed-source-build`, `pinned-prebuilt`, or `BYO`),
  backend, runtime binary hash, hashes for every auxiliary benchmark/helper binary,
  a deterministic hash manifest of every runtime-local dependency resolved by
  `ldd` (the Linux executables are dynamic loaders whose implementation lives in
  sibling `.so` files), and the recorded `--list-devices` output;
- cache state and the exact cache preparation procedure;
- warmup/repeat counts per command;
- the statistical rule. Use median plus nearest-rank p95. Lane 4 may ship only at
  **at least 20% throughput improvement and no more than 5% RAM/VRAM regression**;
- every unvalidated target, with a reason. An empty or omitted gap list is rejected.

Each benchmark command must print one JSON object to stdout containing its numeric
measurements. The capture tool retains raw stdout/stderr, elapsed time, ambient
load, free memory, global NVIDIA VRAM, exact argv, and aggregates. Keep secrets out
of argv and output.

Lane 0/2 artifacts have four explicit evidence partitions:

- `native-performance`: identical `llama-bench` chat/embedding commands. These
  isolate the fixed llama.cpp binary and must not be attributed to framework work.
- `framework-contract`: exact stable MAF approval/resume tests and invocation
  streaming/token-usage tests, run by fully-qualified test method rather than a
  growing class.
- `application-harness`: exact pre-existing chat harness tests covering tool-loop
  timing and metrics parsing. The current implementation must run the same methods;
  new role tests are additional, non-comparable Lane 3 evidence.
- `provider-contract`: exact reranker route/order/degradation tests. A pre-change
  real reranker throughput value is impossible because the shipped harness calls
  chat for that role; the artifact records that gap rather than inventing a number.

Every command declares its partition and comparability rule. Framework/application
commands use identical method filters and product inputs across e67d/current; native
commands use identical argv, model/corpus/runtime, and machine identity. Compare
within a partition only.

```bash
python3 scripts/performance/capture_inference_evidence.py baseline \
  --spec artifacts/performance/baseline-spec.json \
  --output artifacts/performance/baseline.json
```

After both MAF 1.15 and the MEAI/OpenAI package closure have landed, copy the
baseline spec, change `capture_id`, `phase`, and the `framework` source/package
identity to the landed versions, and run it on the same machine with the same
models, corpus, runtime binary, backend, cache procedure, commands, warmups,
repeats, and ambient-load policy.

```bash
python3 scripts/performance/capture_inference_evidence.py baseline \
  --spec artifacts/performance/rebaseline-spec.json \
  --output artifacts/performance/rebaseline.json

python3 scripts/performance/capture_inference_evidence.py compare \
  --baseline artifacts/performance/baseline.json \
  --candidate artifacts/performance/rebaseline.json \
  --output artifacts/performance/framework-delta.json
```

The comparison fails closed if an immutable identity or argv differs. Review
token accounting, streaming cadence, tool-loop latency, TTFT, and output-quality
evidence before attributing any delta to llama.cpp settings.

## 2. Exact fit acquisition and replay proof

Create a fit specification conforming to
`docs/performance/schemas/fit-replay-capture-spec.schema.json`. Use the exact same
model and non-fit launch arguments in all vectors.

The five required commands are deliberately explicit:

1. `default_verbosity`: verified `llama-server`, with `--fit`, without `-v`;
2. `verbose`: byte-identical argv plus exactly one `-v` (or `--verbose`);
3. `fit_params`: verified sibling `llama-fit-params`, which must exit successfully
   and emit a deterministic line starting with `-c`.
4. `explore`: verified `llama-server` with the exact explore launch vector;
5. `replay`: verified `llama-server` with the exact replay launch vector.

For persistent `llama-server` commands, set `expected_timeout: true` and a timeout
long enough to capture startup. The tool terminates only the child process it
started. It never searches for or kills other llama.cpp processes.

```bash
python3 scripts/performance/capture_inference_evidence.py fit \
  --spec artifacts/performance/fit-proof-spec.json \
  --output artifacts/performance/fit-proof.json
```

The spec also supplies the **exact** application explore and replay argument arrays.
The tool proves:

- explore contains `--fit`; replay does not;
- non-fit arguments are byte-equal after removing fit/placement semantics;
- replay contains exactly the `-c`, `-ngl`, `-ts`, `-ot`, `-ctk`, and `-ctv`
  values emitted by `llama-fit-params` (when present);
- explore/replay peak process RSS is within the declared resource tolerance, with
  global VRAM sampled while each process is resident;
- default and verbose startup captures used the verified server binary;
- the helper capture used the verified sibling binary.

Availability must be captured separately for pinned prebuilt, managed source-build,
and BYO runtimes. A missing helper is an unsupported capability, never permission to
fall back to log scraping.

## 3. Native-Windows idle-versus-game VRAM capture

Run in PowerShell 7 from a native Windows checkout. First capture an idle desktop;
then start the game or other competing workload, reach a named repeatable scene,
and capture again. The script does not launch or stop the workload.

```powershell
./scripts/performance/capture_windows_vram.ps1 `
  -Scenario idle `
  -LlamaServerPath C:\llama\llama-server.exe `
  -OutputPath artifacts\vram-idle.json

./scripts/performance/capture_windows_vram.ps1 `
  -Scenario game `
  -WorkloadLabel "Game / fixed scene / graphics preset" `
  -LlamaServerPath C:\llama\llama-server.exe `
  -OutputPath artifacts\vram-game.json
```

Each sample records:

- global total/free/used VRAM and utilization from `nvidia-smi`;
- process-visible budget evidence from that exact binary's
  `llama-server --list-devices`;
- compute-app output when WDDM/NVML exposes it;
- binary hash, driver, host, scenario, interval, and raw unparsed output.

A materially higher process-visible budget than global free VRAM is **external
pressure / WDDM reader divergence**. The sample is useful defect evidence but is
invalid for throughput comparisons. The global reader governs benchmark
contention; the process-visible reader describes the launch process's budget.

## 4. Capture validity checklist

- No build, download, game startup, index job, or other ambient workload overlaps a
  measured repeat.
- The model/corpus/runtime SHA-256 checks pass.
- Runtime tag, binary provenance, backend, driver, and device list are present.
- Cache preparation is performed exactly as declared.
- Warmups are excluded; all repeats succeed; median and p95 are retained.
- Free global VRAM is stable and process/global divergence is not material.
- Chat, embedding, reranker, provider tests, and deterministic suites use separate
  named commands so failures cannot be averaged away.
- Framework rebaseline uses the identical capture contract.
- Coverage gaps remain attached to every artifact.
