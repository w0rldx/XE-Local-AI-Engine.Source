# AGENTS.md

XE Local AI Engine: a single ASP.NET Core node process (`XE-Local-AI-Engine.Client`) that serves a React SPA,
loopback-only `/api/local/v1` endpoints and SignalR hubs, persists to SQLite with per-column encryption, and
supervises `llama-server` / `sd-server` / `whisper-server` child processes for local inference. .NET 10 + Aspire, React 19 +
Vite + pnpm, Python (uv) for training tooling.

This file is instructions, not documentation. Before a non-trivial change, read the index
`docs/agent-knowledge.md` (§0 and its routing table), then the `docs/agent-knowledge/` topic files your change
touches, including their stale-beliefs tables. Follow evidence links when changing a rule or investigating the
same failure. For architecture, start at `docs/wiki/Home.md` and read the pages for the affected subsystem.

## Working rules

- Do not edit files, run mutating commands, or clean up before a plan is approved.
- On a failure, stop and report the failing command and its output before attempting a fix.
- Parallel work goes in git worktrees under `.tmp/worktrees/`; never let two tasks claim the same file.
- Agents sharing one worktree share one git index: never `git add` there. Commit through a private
  `GIT_INDEX_FILE` (`git read-tree HEAD` immediately before `write-tree`, `update-ref` in the three-argument
  form), then `git read-tree HEAD` the shared index once `git diff --cached` is empty.
- Branch from and target `develop`. Nothing lands on `develop` without the operator's explicit approval: never
  merge into, reset or "repair" `develop` mid-task; a feature branch absorbs `develop`, never the reverse. Report
  the branch and integration steps, then stop.
- Commit messages follow Conventional Commits (`fix(scope): …`), body only: no session-tracking, co-author or
  AI trailers. Never set git identity (`git -c user.*`, `git config user.*`, `GIT_AUTHOR_*`/`GIT_COMMITTER_*`);
  a plain `git commit` inherits the repo config.
- Never commit secrets or runtime state: `node.key`, `*.sqlite`, `.env`, `dp-keys/`, `*.enc`.
- The repo never depends on optional per-user agent or editor tooling: no tool names in product code, gates,
  tests or instructions; express the intent as a rule (dot-prefixed, hidden directory). `.gitignore` is the one
  exception. Shipped product surface (installers, MCP client runbooks, cloud providers) is not tooling.
- Never hand-edit generated output: the hey-api client under `src/core/api/generated/`, `routeTree.gen.ts`,
  EF migration designer files. Regenerate and commit the result.
- Cite `file` + symbol, never `file:line`, for code that is being edited (lines drift, symbols survive).
- Agents may propose updates to durable standards or project intelligence; promotion needs human approval.
- Agent-knowledge entries (topic files under `docs/agent-knowledge/`) are written as rule → failure prevented →
  authority, body <= 900 chars. Propose one in `docs/agent-knowledge/proposed.md` when you pay for a new trap;
  never quote a count or timing from a doc as current, the script's own output wins.

## Repository map

Solution `XE-Local-AI-Engine.slnx`. Every project, the workflows and the reviewed provider dependency exceptions:
`docs/wiki/02-project-layout.md`.

- `XE-Local-AI-Engine.Client` (host: FastEndpoints, SignalR hubs, serves the SPA) → `Client.Application` (services) →
  `Client.Persistence` (EF Core + SQLite, encrypted columns). `Providers.*` depend only on `Providers.Abstractions`,
  apart from the shared layers wiki 02 lists.
- `XE-Local-AI-Engine.Client.React` — the SPA, with its own `AGENTS.md` for frontend-only rules.
- `XE-Local-AI-Engine.Tests`, `AI.Agent.Tests`, `Client.Persistence.Tests` — TUnit; `Tests.E2ETests` — Playwright,
  opt-in.
- `scripts/` — dev lifecycle and gates; `publish/` — packaging; `tools/training/` — the shipped Python training
  runtime (own `pyproject.toml`; never `uv sync` inside it).

## Local runtime

```bash
scripts/dev-start.sh     # isolated Aspire AppHost for THIS checkout; seeds XE-Local-AI-Engine.AppHost/.data/node.key
scripts/dev-status.sh    # resource states + endpoint URLs (--json); the port changes on every restart
scripts/dev-stop.sh      # the only sanctioned stop path
```

Never run `aspire stop --all` or `pkill -f <substring>`: both cross worktree boundaries and kill other
checkouts' instances. Kill by PID. Run one instance per data directory. Details: `scripts/README-dev-stop.md`,
`docs/agent-knowledge.md` §2.

- Live rounds: `scripts/lab-up.sh` brings a lab node to the model-ready handoff (before any LLM turn), skipping satisfied
  phases; `scripts/lab-api.sh` makes the logged calls. Wiki 13 "Lab bootstrap for live rounds".
- Kill-by-PID applies to anything you background, load generators included: collect the PIDs (or `setsid` a
  process group), put the kill in a `trap … EXIT`, and confirm with `ps` before ending the turn.
- The app origin serves the SPA bundle that was last **built**, not the working tree. After a frontend change,
  validate UI on the `client-react` Vite origin (`scripts/dev-status.sh --json` lists both) or run `pnpm run build`
  before `dev-start`, and record which origin the evidence came from.
- A scratch or fresh-DB host must not touch the user-level `~/.local/share/XE-Local-AI-Engine` (it rewrites the
  shared `installed-runtime.json`): point `XDG_DATA_HOME` at a scratch dir, or source the BYO llama-server
  override before `dev-start.sh` for GPU rounds.

## Validation

A change is done when these pass. Why each rule exists: `docs/wiki/13-testing-and-validation.md` and
`docs/agent-knowledge.md` §1.

- **`--configuration Release` is load-bearing**: Debug skips the analyzers (Meziantou, BannedApiAnalyzers, `IDExxxx`,
  the no-bare-`TODO` rule). Iterate in Debug, finish in Release; `XE_FULL_ANALYSIS=1` forces them in Debug.
- Use `--no-incremental` when the evidence matters. A deliberate-break proof is void unless the Release build
  reported `0 Error(s)`; keep break scaffolding analyzer-clean and restore it through a shell trap.
- Before a gate chain: `dotnet build-server shutdown`, then export `MSBUILDDISABLENODEREUSE=1` and
  `NUGET_PACKAGES=$HOME/.nuget/packages` for every `dotnet build`/`dotnet test` (the gate does both itself).
- Never queue a gate on a worktree a writer is still editing; no guard catches source edits.

```bash
dotnet tool restore --tool-manifest dotnet-tools.json
scripts/run-backend-tests.sh   # THE backend gate: one Release build, then every enrolled test project
```

- `NO_BUILD=1` skips the build, `--siblings-only` the batched `XE-Local-AI-Engine.Tests`; `COVERAGE_DIR` adds
  Cobertura + TRX; `XE_TEST_WIDTH_DEFAULT`/`XE_TEST_WIDTH_<Project>` set `--maximum-parallel-tests`. CI's `siblings`
  leg calls the same script. It also runs `scripts/run-release-contract-tests.sh` when `scripts/`, `publish/` or
  `.github/workflows/` differ from the merge-base with `develop` (`XE_GATE_CONTRACT_TESTS=run|skip` overrides).
- It sizes `JOBS` from free RAM (`>> Sizing:` line). `XE_TEST_PROFILE=low-memory` (lanes serial; `JOBS`, `PAR`,
  `XE_TEST_WIDTH_DEFAULT` = 1) is only for a memory-constrained machine.
- Never overlap a build with a `--no-build` test run. Exit **69** = lock not acquired, nothing ran; **75** =
  CONTAMINATED, void, re-run (not a red). Check `scripts/build-lock-status.sh` (`--json`) before queueing.
- Cancel by **process group** (`kill -TERM -- -<pgid>`), not PID. **With `COVERAGE_DIR` the siblings run UNGUARDED.**
- Scope with `--treenode-filter '/*/*/(ClassA|ClassB)/*'`, never `--filter`; `--list-tests` is authoritative; a
  no-match filter exits 8. Zero tests is never a pass. Verify the whole changed test project before hand-off.
- `scripts/run-tests-memory-safe.sh` runs only `XE-Local-AI-Engine.Tests` (locked, guarded) for iterating; never wrap
  it in the guard without `NO_BUILD=1` (every run reports 75).

Frontend, from `XE-Local-AI-Engine.Client.React/`:

```bash
pnpm install --frozen-lockfile
pnpm run acceptance   # Node-major check, validate, coverage thresholds, tooling tests, production bundle
```

- `acceptance` first runs `scripts/CheckNodeMajor.mjs`: a Node major other than CI's (22) fails;
  `XE_ALLOW_NODE_DRIFT=1` continues with a not-CI-evidence warning. Standalone `pnpm run build` still runs the full
  lint chain; `build:bundle` alone is not a gate.
- `pnpm run lint` is the typecheck; E2E's `build:e2e` is a bare `vite build`, so green E2E does not prove types.
- After a backend contract change: `pnpm run openapi:check` (regenerates the hey-api client, fails on drift), commit
  the output. `scripts/openapi-live-check.sh` builds Release first unless `OPENAPI_LIVE_SKIP_BUILD=1`.
  `pnpm run licenses:check` after a dependency change.

Other gates:

- Python (`tools/training`, `scripts/**`): `scripts/python-validation.sh --scope changed` (or `full`). Needs `uv`.
- After adding a SignalR hub, `LocalApiRoutes` family, React `features/` dir, wiki page or project: run
  `python3 scripts/docs-inventory-check.py` and name it in the wiki page that enumerates it. The same gate fails on a
  broken relative Markdown link or `#anchor` in any tracked `.md`.
- `publish/**`, `scripts/release/**`: `scripts/lint-release-scripts.sh` (shellcheck ≥ 0.10, PSScriptAnalyzer, Pester;
  a missing tool fails, never skips).
- CI: `.github/workflows/build-and-test.yml` (`python-quality`, `release-contracts`, five-leg `backend-tests`,
  `build-and-test` = the required check, `client-react`); `windows-tests.yml` is advisory.

Opt-in live runners (nothing invokes them; ask before running, run before a tester RC):

- `scripts/run-e2e-local.sh` — Playwright; sets the mandatory `-p:RunE2ETests=true`, refuses a zero-test pass.
- `scripts/run-gpu-smoke-local.sh` — the only gate proving the GPU did the work. Exit 5 = infra abort, 1 = product
  failed.
- `scripts/run-tool-grammar-smoke-local.sh` — after a tool-schema or llama.cpp-pin change; without its failing
  negative control the run proved nothing.
- `scripts/run-retrieval-eval-local.sh` — real-model retrieval eval. Exit 1 = no completed JSON, an INVALID config or
  a failed negative control; 5 = a server never came up.
- `scripts/run-docker-smoke-local.sh` — real-daemon container suites (`XE_REQUIRE_DOCKER_TESTS=1`). Exit 5 = no usable
  daemon, 1 = product failed.
- `scripts/run-model-matrix-local.sh` — pinned-model checks on a live node (`--tier fast|extended|rc`, `--download`).
  Exit 1 = a hard check failed, 2 = model missing/hash mismatch/node not set up, 5 = infra abort. Slow: run it once,
  last, only for inference, thinking, tool offering, window budgeting or model-fit changes; never while iterating.

## Conventions that bite

- Backend tests are TUnit on Microsoft.Testing.Platform, not xUnit. Style: `docs/wiki/17-writing-tests.md`.
- Every test is independent and self-validating: no assertion, or a silent `return` on an unsupported OS, has
  verified nothing — skip visibly with a reason. Principles: `docs/wiki/17-writing-tests.md` §1a.
- Never sleep to wait for, or to rule out, an event in a test; use a gate the test controls, `FakeTimeProvider`, or
  `AssertEx.EventuallyAsync`. A real timer needs a `// real-timer:` comment saying why.
- Mock in this order: the real thing, the repo's fake seam (`FakeOllama`, `RecordingHubMessageSender`, MSW), then
  `Substitute.For<T>()`/`vi.fn()`, then a hand-written fake. Never mock the gate, cipher or migration under test.
- FastEndpoints, one endpoint per file, calling `Client.Application` services directly; no MediatR/CQRS.
  DTOs, mappers and validators fold into `V1/{Dtos,Mappers,Validators}/`; `Dtos/` keeps a flat namespace.
- `using` directives go inside the file-scoped namespace; subfolder namespaces must nest (IDE0130 is an error).
- A bare `TODO`/`FIXME`/`HACK` in a C# comment fails the Release build.
- Frontend: feature folders under `src/features/`, TanStack Query for server state, Zustand only for UI state,
  manual Mantine forms (no form library), user-facing strings through react-i18next.
- Full list: `docs/wiki/16-code-conventions.md`.

## Where to look

- `docs/agent-knowledge.md` — index of hard-won rules (§0 orientation, routing table, §6 not built); topic files
  in `docs/agent-knowledge/` by area (§1 build/test/CI, §2 runtime, §3 models, §4 agents, §5 frontend).
- `docs/wiki/` — architecture; `docs/adr/` — decisions; `docs/roadmaps/` — status records.
- `CONTRIBUTING.md`, `.github/PULL_REQUEST_TEMPLATE.md` — what a PR must state.
