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
BASELINE_WORKTREE=/absolute/path/to/a/clean/e67d6697-worktree
# The runtime dir is named for whichever llama.cpp tag the capture ran on. Resolve
# it from the source of truth rather than pasting a literal, which goes stale.
LLAMACPP_TAG=$(grep -oP 'PinnedTag\s*=\s*"\K[^"]+' \
  XE-Local-AI-Engine.Providers.LlamaServer/LlamaCppReleasePins.cs)

python3 scripts/performance/capture_inference_evidence.py sanitize \
  --input artifacts/performance/baseline-cuda.raw.json \
  --output docs/performance/baselines/2026-07-26-e67d6697-cuda.json \
  --replace "$PWD/.tmp/perf-models=\$MODEL_ROOT" \
  --replace "$PWD/.tmp/llama.cpp-$LLAMACPP_TAG=\$RUNTIME_ROOT" \
  --replace "$BASELINE_WORKTREE=\$BASELINE_REPO"
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
- the SHA-256 of `Directory.Packages.props` relative to each framework/application
  command's Git root, plus the relevant built assembly SHA-256 identities on each
  command. Before the first measured process starts, the tool proves that every
  such command runs from a clean Git worktree at the declared source commit, that
  the central pins file matches its declared hash and package versions, and that
  the declared assemblies match the bytes on disk. The verified command-tree and
  assembly identities are persisted under `verified_identity.framework`;
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
within a partition only. Framework/application/provider commands must also declare
`cwd` and a non-empty `framework_assemblies` identity array. `cwd` may deliberately
point at a clean historical worktree for baseline capture; it is verified against
`framework.source_commit` rather than forced to the capture script's own checkout.

```bash
python3 scripts/performance/capture_inference_evidence.py baseline \
  --spec artifacts/performance/baseline-spec.json \
  --output artifacts/performance/baseline.json
```

For every framework servicing branch, copy the last compatible framework/application
spec, change `capture_id`, `phase`, and the `framework` source/package identity to
the exact landed MAF, MEAI, OpenAI, and MCP versions, then refresh every declared
assembly SHA-256. Run it on the same machine with the same models, corpus, runtime
binary, backend, cache procedure, commands, warmups, repeats, and ambient-load
policy. Historical artifacts stay immutable; a servicing branch produces a new
candidate artifact rather than rewriting the prior capture.

```bash
python3 scripts/performance/capture_inference_evidence.py baseline \
  --spec artifacts/performance/rebaseline-spec.json \
  --output artifacts/performance/rebaseline.json

python3 scripts/performance/capture_inference_evidence.py compare \
  --baseline artifacts/performance/baseline.json \
  --candidate artifacts/performance/rebaseline.json \
  --output artifacts/performance/framework-delta.json
```

The comparison fails closed if an immutable identity or argv differs, or if either
artifact's declared framework fields no longer match its persisted verified
framework identity. Expected framework/tree/assembly differences are retained
explicitly in the comparison as the baseline and candidate identities rather than
being mistaken for immutable runtime/model differences. Review
token accounting, streaming cadence, tool-loop latency, TTFT, and output-quality
evidence before attributing any delta to llama.cpp settings.

## 2. Exact fit acquisition and replay proof

Create a fit specification conforming to
`docs/performance/schemas/fit-replay-capture-spec.schema.json`. Use the exact same
model and non-fit launch arguments in all vectors.

The five required commands are deliberately explicit:

1. `default_verbosity`: verified `llama-server`, with `--fit`, using the production
   profiling vector with its one diagnostic `-v`/`--verbose` flag removed;
2. `verbose`: byte-identical argv plus exactly one `-v` (or `--verbose`); this
   must also be the exact production Explore profiling vector;
3. `fit_params`: verified sibling `llama-fit-params`, which must exit successfully
   and emit a deterministic line starting with `-c`; its argv must exactly equal
   the production `LlamaFitParamsProcessRunner` projection of the Explore vector.
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

- verbose and Explore use the same production profiling vector, including the
  policy's positive context and diagnostic verbosity required to prove
  full-offload placement; default-verbosity differs by that one diagnostic flag
  only;
- explore contains `--fit`; replay does not;
- non-fit arguments are byte-equal after removing fit/placement semantics and
  diagnostic acquisition flags (`-v` and `--metrics`; the latter is separately
  required exactly once in both profiling vectors because replay appends it
  after its role flags);
- GPU KV-cache and flash-attention policy is equal across Explore and replay:
  matching `-ctk/-ctv` values plus flash attention `on`, or all three absent for
  the successful safe-fallback candidate;
- replay contains exactly the `-c`, `-ngl`, `-ts`, `-ot`, `-ctk`, and `-ctv`
  values emitted by `llama-fit-params` (when present);
- helper `-ngl -1` (automatic placement) is normalized to replay `-ngl -2`
  (explicit all layers) only when the **actual production Explore command's**
  startup output records `offloaded N/N layers to GPU`; the earlier independent
  verbose probe cannot authorize normalization for a different Explore run;
  otherwise the proof fails rather than recording automatic placement as frozen;
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
- binary filename/hash, driver, OS/processor, scenario, interval, and raw
  unparsed output.

Future captures deliberately omit GPU UUIDs, machine names, and absolute user
paths. Native probes are time-bounded; their exit code, timeout state, and
sanitized output remain available even when the command fails.
The Windows capture fails closed when no global GPU row can be parsed; an empty
global sample is never valid VRAM evidence.
The Python runner starts every command in a dedicated process group and kills
that entire group during bounded cleanup. The Windows runner likewise terminates
the native process tree; both report cleanup failure instead of waiting
indefinitely.

A materially higher process-visible budget than global free VRAM is **WDDM
reader divergence**, but the raw gap is not by itself proof of external
pressure: the clean dev-box baseline is approximately 950 MiB and idle WDDM
samples can be higher. Benchmark admission first subtracts the configured
ambient baseline, then applies the pre-spawn materiality thresholds; pressure
introduced after load is judged independently as growth beyond the post-load
gap. The global reader governs benchmark contention; the process-visible reader
describes the launch process's budget.

## 4. Capture validity checklist

- No build, download, game startup, index job, or other ambient workload overlaps a
  measured repeat.
- The model/corpus/runtime SHA-256 checks pass.
- Runtime tag, binary provenance, backend, driver, and device list are present.
- Cache preparation is performed exactly as declared.
- Warmups are excluded; all repeats succeed; median and p95 are retained.
- Retained stdout/stderr are bounded and carry the byte count plus SHA-256 of
  the complete stream whenever truncation is required.
- Free global VRAM is stable and process/global divergence is not material.
- Chat, embedding, reranker, provider tests, and deterministic suites use separate
  named commands so failures cannot be averaged away.
- Framework rebaseline uses the identical capture contract.
- Every framework/application command records a clean command Git tree at the
  declared commit, a verified central-package-pins hash/version projection, and
  verified relevant assembly hashes before execution.
- Coverage gaps remain attached to every artifact.

## 5. Generic command-aggregate policy gate

`compare` remains a report-only operation. Use the separate `gate` operation when
an optimization decision needs a machine-readable pass, rejection, or
unevaluable verdict:

```bash
python3 scripts/performance/capture_inference_evidence.py gate \
  --baseline artifacts/performance/baseline.json \
  --candidate artifacts/performance/candidate.json \
  --policy docs/performance/policies/generic-inference-throughput-policy.example.json \
  --output artifacts/performance/inference-verdict.json
```

Policies conform to
`docs/performance/schemas/inference-comparison-policy.schema.json`. Version 1 has
a closed contract: `schema_version`, `policy_id`, an optional
`allowed_identity_changes` array that defaults to `[]`, and a non-empty ordered
`rules` array. `policy_id` and every rule ID, command, and metric use the
privacy-safe token pattern `^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*$`, are limited to
128 characters, and rule IDs must be unique. Each rule names an exact captured
command, aggregate metric, `median` or `p95` statistic, rule kind, and finite
non-negative percentage threshold.

The percentage delta is `((candidate / baseline) - 1) * 100`. Pass/fail boundary
comparisons use exact integer-rational cross-products derived from the
JSON-loaded decimal representations; no floating tolerance or rounded decimal
context decides a rule. The reported delta remains a JSON-native number and an
unrepresentable delta fails closed.
`minimum_improvement_percent` accepts equality or a larger delta;
`maximum_regression_percent` accepts equality or a smaller delta. Both values
must be finite and strictly greater than zero. Missing, non-numeric, non-finite,
zero, or negative values make the required rule unevaluable; they never count as
a pass or threshold rejection.

The identity policy is default-deny:

- `framework` permits only the independently verified framework declarations,
  central pins, source commits, command trees, and assembly hashes to differ.
- `runtime` permits only the independently verified runtime tag, provenance,
  backend, binary/dependency-manifest, and auxiliary-binary identities to differ.

Declaring an identity change identifies the experiment variable; it does not skip
verification. Each artifact must retain non-empty, individually hash-verified
model, corpus, and runtime dependency-manifest identities, a complete verified
framework declaration, plus complete machine and runtime-device probe identity.
When framework/application commands require framework verification,
`required: true` carries non-empty strict command-tree, central-pin, and assembly
identities. Native-only evidence uses `required: false` with exactly an empty
`command_trees` array. Models, corpus, machine, devices, the complete command-name
set, and every command argv hash remain equal.
Command-name and argv-hash equality is established before any rule evaluation; a
global mismatch marks the identity and every ordered rule unevaluable. Unknown or
duplicate allowances, an undeclared difference, an unverified identity, malformed
artifacts, or duplicate command names produce an unevaluable verdict.

The gate writes the verdict atomically after flushing and syncing a temporary file.
It contains raw-input SHA-256 hashes, the identity decision, and every rule result
in policy order. It deliberately excludes input paths, argv, stdout/stderr,
environment values, GPU UUIDs, secrets, and framework assembly paths.

Exit codes are:

- `0`: comparable, every rule evaluable, and all thresholds pass;
- `2`: malformed/incomparable evidence or policy, unverified identity, or at
  least one unevaluable required rule;
- `3`: comparable and fully evaluable, with at least one threshold rejection.

Exit `2` takes precedence over `3`. No active CI invokes this command; it is an
explicit local/release decision aid until repository authority adds external
enforcement.

### Specialized Lane 4 compound gate

The generic example above covers throughput command aggregates only. It does not
certify RAM or VRAM and does not replace
`scripts/performance/run_scheduling_grid.py`, whose specialized Lane 4 evaluator
remains authoritative for the compound gate: role-request preflight, context
readback, token/correctness checks, repeat determinism, canonical-baseline
semantic equivalence, all five corpus scenarios at least 20% faster by median,
peak process RSS regression at most 5%, and CUDA PID-scoped process-residency
regression at most 5%.

Process RSS means a harness-collected process peak/high-water value. GPU residency
means PID-scoped evidence with status `measured`. Ambient WSL/global
`nvidia-smi` free/used memory cannot substitute for either. CPU cells mark GPU
memory not applicable; unavailable or zero WSL PID residency fails closed rather
than becoming a zero-usage pass.
