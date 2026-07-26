# Agent Framework 1.15 completion evidence

This document closes the reproducibility and hardware-lane gaps in
`Plans/2026-07-26-agent-framework-1.15-upgrade-and-harness-assessment-plan.md`.
The deterministic compatibility tests remain the permanent release gate. The real
llama-server lane below is explicitly opt-in and hardware-dependent.

## Pre-upgrade Lane 0 proof

The expanded deterministic suite was committed before the MAF pin change, but the
original implementation history did not retain a test log from that interval. To
close the behavioral evidence gap without rewriting history, the exact immediate
pre-upgrade tree was checked out at
`d868e335cb4f7bc9137043919a1fdef3fb2330c9` and validated retrospectively:

- MAF `1.13.0`, MEAI `10.7.0`, OpenAI `2.11.0`, and MCP `1.4.0`;
- Release restore and build: 0 warnings, 0 errors;
- all 198 `XE-Local-AI-Engine.AI.Agent.Tests` tests passed;
- the assembly guard confirmed the test binaries did not change during execution.

The discovery log includes the expanded streaming approval matrix, reverse-ordered
mixed approval responses, cancellation before the first streaming update,
instructions-once containment, workflow/handoff, tool-contract, and provider-budget
tests. The sanitized logs and integrity manifest are under
`docs/agent-framework/evidence/baseline/`.

This proves the pre-upgrade code and pins satisfy Lane 0. It does not pretend the
proof was captured before the package commit; the timestamp and retrospective
purpose remain explicit in the manifest.

## Reproducible dependency graphs

Run:

```bash
scripts/capture-agent-framework-dependencies.sh \
  --baseline-ref e67d6697 \
  --current-ref HEAD
```

The script creates isolated detached worktrees, restores both `Release` and `Debug`,
and records:

- exact centrally declared pins;
- exact direct and transitive resolved graphs;
- vulnerable and deprecated package status;
- current/latest status, including prereleases;
- a SHA-256 manifest over every sanitized artifact.

Output lives under `docs/agent-framework/evidence/dependencies/`. Absolute checkout
paths are replaced with `$REPO`; package ids and versions are preserved verbatim.
Debug is captured independently because the Hosting, Hosting.OpenAI, and DevUI
packages are conditional on `$(Configuration) == Debug`.

Captured on 2026-07-27 local time (2026-07-26 UTC):

- baseline `e67d669709169fefc4cffab444af66654526a8d8`;
- combined six-plan source
  `4d84956ca04119f27139e3af9e1f9f42349799c0`;
- all 14 changed central pins are recorded in the two `central-pins.tsv` files;
- Release resolves MAF `1.15.0`, MEAI `10.8.1`, and MCP `1.4.1` without
  resolving the Debug-only Hosting/Hosting.OpenAI/DevUI packages;
- Debug additionally resolves Hosting and DevUI
  `1.15.0-preview.260722.1` and Hosting.OpenAI
  `1.15.0-alpha.260722.1`;
- no vulnerable packages were reported in either configuration;
- the existing transitive `SQLitePCLRaw.lib.e_sqlite3` `3.50.3` legacy
  deprecation remains present in both baselines and is unrelated to this upgrade.

## Real llama-server compatibility lane

Run:

```bash
scripts/run-agent-framework-hardware-compat.sh \
  --model /absolute/path/to/Qwen2.5-0.5B-Instruct-Q4_K_M.gguf \
  --server /absolute/path/to/llama-server \
  --variant cuda
```

`AgentFrameworkHardwareCompatibilityTests.ProductionInvocationRunner_CompletesThroughLlamaServerMeaiAndMaf`
uses a fixed on-disk GGUF store but otherwise resolves the production stack:

1. `LocalModelProviderResolver`
2. `LlamaServerLocalModelProvider`
3. the llama-server OpenAI-compatible endpoint through the MEAI OpenAI adapter
4. the production MAF `InvocationAgentFactory`
5. the production `InvocationRunner`

The generated JSON records only commit/type identities, backend, file SHA-256/size,
and response SHA-256/length. It contains no machine-local paths or model output.
This lane is not a release gate because arbitrary release machines do not have the
required runtime/model/hardware.

The opt-in lane passed on 2026-07-27 local time (2026-07-26 UTC) against
`0a39b49363d6d89547410b5f9b547b25c5cf3bcc` using CUDA with:

- model SHA-256
  `6eb923e7d26e9cea28811e1a8e852009b21242fb157b26149d3b188f3a8c8653`
  (`397808192` bytes);
- llama-server SHA-256
  `78cd370e18a911b284c0f732e40ada20a1b3c8fc8ba40f2aabb0e8d7d800a3cc`;
- one guarded test passed and the assembly guard confirmed no concurrent build
  contamination;
- `docs/agent-framework/evidence/hardware-compatibility.json` records the exact
  production resolver/provider/MEAI/MAF/runner types and a response hash without
  retaining the response text.

## Release, Debug, DevUI, and packaging validation

Run the Linux-safe evidence lane:

```bash
scripts/run-agent-framework-validation.sh --with-linux-package
```

It serializes Release and Debug restore/build, runs the Debug-only registration and
hosting smoke tests, runs the release-script static-analysis/P0 compile gate, and
builds the real portable `linux-x64` package. Logs are path-sanitized and hashed in
`docs/agent-framework/evidence/validation/manifest.json`.

The captured validation manifest tests source commit
`0a39b49363d6d89547410b5f9b547b25c5cf3bcc` and records `result: passed`:

- Release solution restore/build: passed with 0 warnings and 0 errors;
- Debug solution restore/build: passed with 0 warnings and 0 errors;
- Debug DevUI registration smoke: 1/1 passed;
- Debug DevUI hosting/route smoke: 1/1 passed;
- release-script static analysis and the `P0_SPIKE` compile gate: clean;
- real portable `linux-x64` package: built, 86 MiB, SHA-256
  `48401f15a78041523ec87c8a62bcf059232577c8a2e5e38f72a21cf59871193a`.

### Native Windows gap

The canonical tester release path is `publish/package-tester-win.ps1`; it requires a
native Windows packaging machine and is not proven by a WSL/Linux run. Before an RC,
run on Windows:

```powershell
pwsh ./publish/package-tester-win.ps1 -SkipUpload
```

Record the resulting package hashes beside the Linux evidence. Do not relabel the
Linux portable ZIP or PowerShell static analysis as a Windows packaging pass.

## Rollback

A real rollback branch was created from delivery commit `1a809330`:

- branch: `rollback/framework-prior-pins-1a809330`
- commit: `30ba0768` (`revert: restore pre-upgrade AI framework pins`)

It restores exactly the 14 central pins changed since `e67d6697` and nothing else.
The delivery branch remains upgraded.

The rollback was also cherry-picked onto the combined six-plan delivery tree at
`16917a2e` and validated independently:

- proof commit: `bc546d73`
- `Directory.Packages.props` is byte-identical to the `e67d6697` baseline
- Release restore/build: passed with 0 warnings and 0 errors
- Debug restore/build, including the prerelease Hosting/DevUI packages: passed
  with 0 warnings and 0 errors
- deterministic Agent Framework gate: 202/202 tests passed
- assembly guard: unchanged

The sanitized logs, exact pin mapping, hashes, and portable rollback patch are
under `docs/agent-framework/evidence/rollback/`.

Emergency replay when the local rollback branch/commit is available:

```bash
git cherry-pick 30ba0768
```

Portable replay from the committed evidence, without relying on that
out-of-branch Git object:

```bash
git apply --index docs/agent-framework/evidence/rollback/restore-prior-pins.patch
git commit -m "revert: restore pre-upgrade AI framework pins"
```

Validate the resulting rollback tree:

```bash
dotnet restore XE-Local-AI-Engine.slnx
dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-restore
dotnet restore XE-Local-AI-Engine.slnx -p:Configuration=Debug
dotnet build XE-Local-AI-Engine.slnx --configuration Debug --no-restore
scripts/with-build-lock.sh -- \
  scripts/assembly-guard.sh guard --test-bins -- \
  dotnet test \
    --project XE-Local-AI-Engine.AI.Agent.Tests/XE-Local-AI-Engine.AI.Agent.Tests.csproj \
    --configuration Release \
    --no-build \
    --max-parallel-test-modules 1
```

To reapply the upgrade after the rollback is no longer needed, revert the new
cherry-pick commit (not the original hash, if cherry-pick produced a different id):

```bash
git revert <rollback-commit-created-by-cherry-pick>
```

Then rerun dependency capture and the full validation lane.

## Attribution boundary

The dependency graphs and deterministic contract-test timings are framework evidence.
The native inference artifacts under `docs/performance/baselines/` compare
`e67d6697` with later commits that also contain prerequisite-system changes (runtime
capture, fit/VRAM semantics, telemetry/calibration, and launch-profile identity).
Those native throughput/TTFT/RSS/VRAM deltas **must not be attributed to the Agent
Framework or MEAI package upgrades**. They remain whole-system prerequisite
rebaselines. Only an otherwise identical framework-only source comparison could
support framework timing attribution.
