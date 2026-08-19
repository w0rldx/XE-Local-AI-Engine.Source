# AGENTS.md

## Approval Gates

Do not edit files, run mutating commands, perform infrastructure changes, or clean up files before an approved plan.

For failures, stop and report the failing command/output before attempting fixes.

## Hard-won knowledge

Read `docs/agent-knowledge.md` before your first non-trivial change. It records the rules, invariants, and traps that are not derivable from the code — each one encodes a bug that was already paid for once (build-breaking `TODO` comments, the OpenAPI regen that silently drops endpoints, why `dev-stop.sh` is the sanctioned stop path, sandbox symlink guards, MAF constructor pitfalls). It also lists beliefs that are now false, so a half-remembered old rule can be corrected.

`docs/wiki/` is the code-grounded architecture reference.

## Validation

### What CI runs on a PR to `develop`

`.github/workflows/build-and-test.yml`, four parallel jobs (the tag-triggered `release.yml` re-runs
this whole file as its `validate` job before packaging):

- **python-quality** — `scripts/python-validation.sh --scope full --serial`: ruff (format + lint),
  pyrefly, pytest (+coverage) and bandit over `tools/training` and `scripts/**`, from the root
  `pyproject.toml` + `uv.lock` (dev tooling only). Runs the `tools/training/test_*.py` self-checks
  and the `scripts/**` unittest suites under pytest. Not the training runtime: `tools/training/
  pyproject.toml` + `uv.lock` are the shipped runtime manifest (ADR 0005) and stay untouched.
  - `python3 scripts/docs-inventory-check.py` runs in the same job: it fails when a SignalR hub, a
    `LocalApiRoutes` route family, a React `features/` directory, a numbered wiki page or a solution
    project is missing from the `docs/wiki/` page that enumerates it. Run it after adding any of those.
- **release-contracts** — `scripts/run-release-contract-tests.sh` (auto-enrolled `scripts/tests`,
  `scripts/compliance/tests`, `scripts/performance/tests`), then `scripts/lint-release-scripts.sh
  --no-behavior --bootstrap` (shellcheck, PSScriptAnalyzer, Pester, the `P0_SPIKE` compile gate).
  shellcheck is installed pinned (`v0.11.0`, sha256-verified) because `ubuntu-latest` ships 0.9.0,
  whose SC2317/SC2015 false positives fail the `--severity=style` pass; the script refuses < 0.10.0.
- **build-and-test** — Release build, `scripts/openapi-live-check.sh`, then one `dotnet test` per
  test project in the solution with Cobertura coverage; `scripts/merge-cobertura.py` enforces
  `scripts/backend-coverage-baseline.txt`. Coverage XML + TRX are uploaded as `backend-test-results`.
  `--report-trx` copies each coverage report as a TRX attachment under
  `<module>/_<machine>_<timestamp>/In/<machine>/`, so the report glob is depth-2 only.
- **client-react** — `openapi:check`, `licenses:check`, `validate` (= `lint` + `knip` +
  `signalr:check` + `depcruise`), `test:coverage:check`, `test:tooling`, `build`, `pnpm audit`.

Not gates: `e2e.yml` (manual dispatch, or a PR labelled `run-e2e`) and `pnpm run spellCheck`
(~1.7k unknown words on the current tree — a dictionary task, not a gate).

Backend:

- `dotnet restore XE-Local-AI-Engine.slnx`
- `dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-restore`
- `dotnet test XE-Local-AI-Engine.slnx --configuration Release --no-build --max-parallel-test-modules 1`

**The `--configuration Release` in those commands is load-bearing — always finish with it.** Local Debug builds skip analyzer execution (`Directory.Build.targets`; 84s → 10s on the Tests module), so SonarAnalyzer, Meziantou, BannedApiAnalyzers and the `IDExxxx` code-style rules — including the "no bare `TODO`" rule — only fire in Release. Iterate in Debug, but a change is not verified until a Release build passes, or the packaging script will reject what compiled fine for you. `XE_FULL_ANALYSIS=1` forces the full pass in Debug.

This solution-wide command is the canonical local backend gate; `README.md`, `CONTRIBUTING.md`, `XE-Local-AI-Engine.Client/README.md` and `XE-Local-AI-Engine.Client.React/AGENTS.md` all restate it and defer here. **CI runs something different on purpose:** `build-and-test` loops one `dotnet test` per test project auto-enrolled from `XE-Local-AI-Engine.slnx` (E2E excluded), with `--maximum-parallel-tests 8` and no `--max-parallel-test-modules`, because MTP resolves `--coverage-output` relative to `--results-directory` and parallel modules sharing one directory would overwrite each other's Cobertura report. `scripts/run-tests-memory-safe.sh` is the lower-memory local runner for the `XE-Local-AI-Engine.Tests` module specifically — it does not cover the other test projects, so run them too.

Backend tests are TUnit on Microsoft.Testing.Platform — the pinned version is in `Directory.Packages.props`. To scope a run, use `--treenode-filter` (not `--filter`). Alternation works: `/*/*/(QuantLadderTests|DesktopPortStoreTests)/*` discovers exactly the union of the two classes' tests. `--list-tests` honors the filter and is authoritative for the current count; don't trust a count written down here.

Never run a build and a test run concurrently — `dotnet test --no-build` reads `bin/`, and a build in another process rewrites those assemblies mid-run and produces phantom failures (or a phantom green). Two guards exist, and both are already wired into `scripts/run-tests-memory-safe.sh` and `scripts/run-e2e-local.sh`:

- `scripts/with-build-lock.sh -- <command>` — cross-process `flock` so cooperating shells serialize. Bounded wait; exit 69 names the holder.
- `scripts/assembly-guard.sh guard --test-bins -- <test command>` — snapshots the test assemblies around the run and reports **exit 75, CONTAMINATED, re-run required** if they changed, instead of reporting test failures. Wrap any new test runner in it.

**Exit 75 from any test script means the result is void, not red.** Re-run it. See `docs/agent-knowledge.md` §1 for the evidence and the file-descriptor trap that makes the naive `flock <file> <command>` form leak the lock to MSBuild's daemons.

Frontend (from `XE-Local-AI-Engine.Client.React/`):

- `pnpm install`
- `pnpm run lint`
- `pnpm test`
- `pnpm run build`

After any backend contract change, run `pnpm openapi:check` — it regenerates the hey-api client and fails on drift. Commit regenerated output with the change; never hand-edit generated files.

E2E is ask-gated unless the task specifically targets E2E behavior; it runs in its own lane and is excluded from solution-wide `dotnet test`.

- `scripts/run-e2e-local.sh` — opt-in local runner for the Playwright suite. Nothing invokes it automatically; run it by hand before cutting a tester RC. It sets the mandatory `-p:RunE2ETests=true` (without it the E2E csproj demotes itself to a library and the run passes vacuously with zero tests), installs Playwright browsers, and refuses to report a zero-test run as a pass. `--filter '/*/*/HostBootSmokeE2ETests/*'` scopes it; `--list` is authoritative for the current test count. Note the fixture runs `pnpm run build:e2e` (a bare `vite build`) — it deliberately does **not** typecheck or lint, because `pnpm run lint` already does and paying for it twice cost 20-45s per run. The consequence: a frontend type error no longer surfaces at fixture init, so run `pnpm run lint` yourself before trusting a green E2E pass.

- `scripts/run-gpu-smoke-local.sh` — opt-in **live GPU smoke** against a real, locally started node. Nothing invokes it automatically; run it by hand before cutting a tester RC or after touching the inference/runtime path. It owns the AppHost lifecycle (`dev-start.sh` → `aspire wait app` → `dev-stop.sh`), discovers the port from `dev-status.sh --json` (it changes on every restart), and asserts, in order: the installed llama.cpp identity, the `IRuntimeDeviceAudit` verdict, a real streamed chat turn, **that the GPU actually did the work** (nvidia-smi utilisation during generation + a VRAM rise over a baseline sampled before the host starts), a real tool call, optionally image generation (`--images`), and that eject returns VRAM to baseline. Every step must record a verdict — a step that is skipped or produces no result fails the run, so "nothing ran" can never read as green. Exit 1 always comes with a `=== Summary ===` naming each step's verdict; **exit 5 is an infrastructure abort** (AppHost failed to start or never became healthy, base URL undiscoverable, auth failed) where nothing was judged and no summary is printed — so a pre-RC wrapper can treat 1 as "product says no" and 5 as "fix the machine and re-run". Exit 3 means an instance is already running, 4 that it could not tell, 2 a missing prerequisite (including "this box has no NVIDIA GPU"), 75 contamination, 130 interrupted.
  - **Why the GPU assertion is the load-bearing one:** a correct reply proves nothing — CPU fallback answers correctly, just slowly. Measured on this box, same model and script: GPU peak 72% / +1199 MiB VRAM versus CPU-fallback 11% / +0 MiB, with an identical, correct answer both times.
  - **Configuration is not outcome.** The *installed* runtime record and the *effective* backend disagree in both directions: a `vulkan` install with no Vulkan ICD runs entirely on the CPU, and an `XE_LLAMACPP_SERVER_PATH` override runs on CUDA while the record still says `vulkan`. The device audit, not the variant field, is the authority.
  - Logic is tested without a GPU by `scripts/tests/gpu-smoke.test.sh` (96 checks), which drives every refuse-to-pass path directly. The script prints its own `N checks passed` line — trust that over any number quoted here, and note it treats a zero-check run as a failure rather than a pass.

- `scripts/run-tool-grammar-smoke-local.sh` — opt-in **live tool-schema grammar smoke** against a real `llama-server`. Nothing invokes it automatically; run it after changing any tool's `ParameterSchema`, after adding a tool, or when bumping the llama.cpp pin. It exists because llama-server compiles the whole `tools` array into one GBNF grammar before sampling and rejects over-large repetition bounds with HTTP 400 `Failed to initialize samplers: failed to parse grammar` — a P1 that shipped once, and that **no other suite can catch**: `ChatLocalToolsE2ETests` runs against FakeOllama (no chat template, no grammar), and the `LlamaGrammarToolSchemaCompatibilityTests` unit tests measure against a hand-measured constant rather than the real converter.
  - **The negative control is the load-bearing assertion.** The script POSTs the real production offer twice: sanitised (must be 200) and unsanitised (must still be the grammar 400). A 200 on the unsanitised body means the run proved nothing — either the model is a *reasoning* model, whose template never enters the constrained branch, or llama.cpp raised its limits and `LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound` must be re-measured. A "pass" without that failing control is not evidence.
  - It therefore needs a **non-reasoning, tool-capable** GGUF; a reasoning model makes the smoke inert rather than red, which is exactly why the control is checked explicitly.

Python (`tools/training`, `scripts/**`):

- `scripts/python-validation.sh --scope <deps|style|types|tests|security|changed|full>` — the same
  gate CI runs, from the root `pyproject.toml` (ruff `E,F,I,B,UP,SIM,N,S` @120 cols, pyrefly,
  pytest with `filterwarnings=error`, bandit). Needs `uv` on PATH; `--scope deps` (or `full`) syncs
  the dev venv into `./.venv`. `--scope changed` diffs against `develop` and runs only what the
  changed files need. `uv run ruff format .` fixes formatting; `uv run ruff check --fix .` the
  autofixable rules. Do NOT run `uv sync` inside `tools/training/` — that resolves the multi-GB
  training runtime, not the tooling. The heavy runtime imports (`torch`, `unsloth`, ...) are typed
  as `Any` in pyrefly (`replace-imports-with-any`) because they only exist in the provisioned venv.

Release-critical scripts:

- `scripts/lint-release-scripts.sh` — shellcheck + PSScriptAnalyzer over `publish/package-tester-win.ps1`, `publish/package-rc.sh`, and the other packaging scripts. The release path is the tag-triggered `.github/workflows/release.yml` (publishes to this repo's GitHub Releases; GitHub Actions must be enabled on the repository for it to run). `package-tester-win.ps1` and `package-rc.sh` are deprecated/reference-only manual packagers, but this script still gives them static analysis of their own. A missing linter exits 2 rather than passing silently. It also build-only compile-checks the `#if P0_SPIKE` code in `XE-Local-AI-Engine.AI.Agent.Tests` (never runs it) and restores an ungated build afterwards — see `docs/agent-knowledge.md` for the gate rationale and the `DefineConstants` replacement trap.
- `publish/tests/package-tester-win.Tests.ps1` — Pester coverage (49 tests) for the packaging script's pure logic: the NuGet vulnerability-JSON parsing, `Get-ProjectVersion`, the SemVer gate, the GitHub-App client-ID predicate, and `Find-GitHubRelease`'s both-tag-form resolution. **It runs by default** — `scripts/lint-release-scripts.sh` includes it, and `--pester` merely requests it explicitly (`--pester-only` scopes to it). A missing Pester module is a **hard failure**, not a silent skip, because a skipped suite must never read as a pass. The tests extract their subjects from the real `.ps1` via the PowerShell AST rather than copying its logic, so a rename or restructure fails them loudly instead of leaving them grading a stale copy.

## Parallel Work

For parallel implementation tasks, prefer git worktrees under `.tmp/worktrees/` and avoid multiple tasks claiming the same file unless explicitly approved.

## Durable Memory

Agents may propose updates to project intelligence or standards, but durable context promotion requires human architect approval.

<!-- CODEGRAPH_START -->
## CodeGraph

In repositories indexed by CodeGraph (a `.codegraph/` directory exists at the repo root), reach for it BEFORE grep/find or reading files when you need to understand or locate code:

- **MCP tool** (when available): `codegraph_explore` answers most code questions in one call — the relevant symbols' verbatim source plus the call paths between them, including dynamic-dispatch hops grep can't follow. Name a file or symbol in the query to read its current line-numbered source. If it's listed but deferred, load it by name via tool search.
- **Shell** (always works): `codegraph explore "<symbol names or question>"` prints the same output.

If there is no `.codegraph/` directory, skip CodeGraph entirely — indexing is the user's decision.
<!-- CODEGRAPH_END -->
