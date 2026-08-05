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

It serializes Release and Debug restore/build, runs the permanent Agent Framework
suite plus the llama-server adapter-policy/architecture scope, runs the Debug-only
registration and route-registration contract tests, runs the release-script
static-analysis/P0 compile gate, and can optionally build the real portable
`linux-x64` package. Logs are path-sanitized and hashed in
`docs/agent-framework/evidence/validation/manifest.json`.

The refreshed validation manifest records both the worktree's current `HEAD` and a Git
tree identity for the exact tracked source under test. The generated validation-evidence
directory is excluded from that tree to avoid a self-referential hash, so an uncommitted
review-correction worktree remains reproducibly bound to its tested source. Nonignored
untracked files outside that evidence directory and `.tmp` fail the lane with exit 75
instead of being silently omitted from the identity. The manifest records `result: passed`:

- Release solution restore/build: passed with 0 warnings and 0 errors;
- permanent Release Agent Framework compatibility gate: 208/208 passed, with
  unchanged test assemblies;
- Release llama-server adapter-policy and architecture dependency scope: 11 passed,
  2 live llama-server round trips skipped by their explicit opt-in environment gate,
  with unchanged test assemblies;
- Debug solution restore/build: passed with 0 warnings and 0 errors;
- Debug DevUI DI-registration contract: 1/1 passed;
- Debug DevUI endpoint-mapping contract: 1/1 passed;
- release-script static analysis and the `P0_SPIKE` compile gate: clean;
- the portable `linux-x64` package was not rebuilt in this refresh
  (`linuxPortablePackageIncluded: false`).

The two Debug checks prove service construction and endpoint mapping only. They are
not a live browser, live HTTP, or interactive DevUI proof.

### Approval replay hardening

The sessionless invocation runner replays approval history with `session: null`.
MAF 1.15's session-backed `ApprovalResponseBindingChatClient` therefore becomes a
no-op on that path, while MEAI's function-invocation client executes the tool call
carried by an approved `ToolApprovalResponseContent`. A caller that could substitute
that tool call while retaining the request id could change its name or arguments
after approval.

`ApprovalResponseValidatingAgent` now snapshots each request as the inner agent
surfaces it, before returning it to the caller. On resume it validates both the
replayed request and response against that trusted per-invocation snapshot before
MAF transforms the history. The original call id, tool type/name, and JSON-equivalent
arguments must all match. The validator atomically reserves each pending response before
calling the inner agent. A concurrent resume therefore loses the reservation race and
fails before tool execution; a sequential exact replay sees a consumed response and also
fails. Cancellation, exceptions, or abandoned streaming enumeration leave the reservation
consumed because the tool outcome is uncertain. Caller replay is transport, not authority:
unmatched, duplicate, substituted-response, or jointly substituted request-and-response
payloads fail closed before tool execution. The permanent suite covers approve, reject,
reverse-ordered parallel decisions, cancellation, response-only tampering, joint
request/response tampering, sequential replay, barrier-controlled concurrent replay,
unmatched ids, and duplicate responses.

### Native Windows gap

The release path is the tag-triggered `.github/workflows/release.yml`, which builds and packages on
GitHub-hosted `windows-latest`/`ubuntu-latest` runners. `publish/package-tester-win.ps1` is the deprecated,
reference-only manual packager; it requires a native Windows packaging machine and is not proven by a WSL/Linux run.
As a manual rehearsal before an RC, it can still be run on Windows:

```powershell
pwsh ./publish/package-tester-win.ps1 -SkipUpload
```

Record the resulting package hashes beside the Linux evidence. Do not relabel the
Linux portable ZIP or PowerShell static analysis as a Windows packaging pass.

## Rollback

The canonical rollback mechanism is the committed portable patch:

```bash
git apply --check docs/agent-framework/evidence/rollback/restore-prior-pins.patch
git apply --index docs/agent-framework/evidence/rollback/restore-prior-pins.patch
git diff --cached -- Directory.Packages.props
git commit -m "revert: restore pre-upgrade AI framework pins"
```

This restores exactly the 14 central pins changed since `e67d6697` and does not
depend on an out-of-branch Git object. Its SHA-256 is recorded as
`canonicalReplay.sha256` in
`docs/agent-framework/evidence/rollback/manifest.json`.

Historical validation remains useful proof, but is not the replay dependency. The
rollback was applied to the combined six-plan delivery tree at `16917a2e` and
validated independently:

- proof commit: `bc546d73`
- `Directory.Packages.props` is byte-identical to the `e67d6697` baseline
- Release restore/build: passed with 0 warnings and 0 errors
- Debug restore/build, including the prerelease Hosting/DevUI packages: passed
  with 0 warnings and 0 errors
- deterministic Agent Framework gate: 202/202 tests passed
- assembly guard: unchanged

The sanitized logs, exact pin mapping, hashes, and portable rollback patch are
under `docs/agent-framework/evidence/rollback/`.

Before squashing or garbage-collecting the implementation history, preserve the
historical proof object with an annotated local tag:

```bash
git cat-file -e bc546d73^{commit}
git tag -a agent-framework-rollback-proof-bc546d73 bc546d73 \
  -m "Preserve validated Agent Framework prior-pin rollback proof"
```

This only creates a local tag. Push it only after a human chooses the remote tag
namespace and retention policy. The historical source commit `30ba0768` may still
be inspected when present, but rollback instructions do not require cherry-picking
it.

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
rollback commit:

```bash
git revert <rollback-commit>
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
