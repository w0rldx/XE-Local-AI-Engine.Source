# AGENTS.md

XE Local AI Engine: a single ASP.NET Core node process (`XE-Local-AI-Engine.Client`) that serves a React SPA,
loopback-only `/api/local/v1` endpoints and SignalR hubs, persists to SQLite with per-column encryption, and
supervises `llama-server` / `sd-server` child processes for local inference. .NET 10 + Aspire, React 19 +
Vite + pnpm, Python (uv) for training tooling.

This file is instructions, not documentation. Read `docs/agent-knowledge.md` before your first non-trivial
change: it records the invariants and traps that the code does not tell you, and lists beliefs that are now
false. `docs/wiki/` is the code-grounded architecture reference; start at `docs/wiki/Home.md`.

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
- `docs/agent-knowledge.md` entries are written as rule → failure prevented → authority. Add one when you
  pay for a new trap; never quote a count or timing from a doc as current, the script's own output wins.

## Repository map

Solution: `XE-Local-AI-Engine.slnx`. Full layout and dependency rules: `docs/wiki/02-project-layout.md`.

- `XE-Local-AI-Engine.Client` — host: FastEndpoints, SignalR hubs, composition root, serves the SPA.
- `XE-Local-AI-Engine.Client.Application` — services (chat, agents, scheduler, model-fit, dev mode, training, benchmarks).
- `XE-Local-AI-Engine.Client.Persistence` — EF Core + SQLite, encrypted columns; references only `Providers.Abstractions`.
- `XE-Local-AI-Engine.AI.Agent` / `AI.Contracts` — Microsoft Agent Framework wiring / shared DTOs.
- `XE-Local-AI-Engine.Providers.*` — runtimes and model sources; each depends only on `Providers.Abstractions`
  (reviewed exception: `LlamaServer` and `OpenAICompat` also use the leaf `Providers.OpenAICompatible.Core`).
- `XE-Local-AI-Engine.AppHost` / `ServiceDefaults` — dev-only Aspire orchestration and telemetry defaults.
- `XE-Local-AI-Engine.WindowsLauncher` — Velopack entry point; starts the published host as a child process, no project refs.
- `XE-Local-AI-Engine.Client.React` — the SPA. Has its own `AGENTS.md` for frontend-only rules.
- `XE-Local-AI-Engine.Tests`, `AI.Agent.Tests`, `Client.Persistence.Tests` — TUnit; `Tests.E2ETests` — Playwright, opt-in.
- `Client.Testing` — shared host fixtures; `Testing.FakeOllama` — in-memory fake model server used by tests;
  `Testing.FakeDocker` — in-memory fake Docker Engine API so the container tests need no daemon.
- `scripts/` — dev lifecycle, validation gates, smoke runners. `publish/` — packaging. `tools/training/` — the
  shipped Python training runtime (own `pyproject.toml`; never `uv sync` inside it).

## Local runtime

```bash
scripts/dev-start.sh     # isolated Aspire AppHost for THIS checkout; seeds XE-Local-AI-Engine.AppHost/.data/node.key
scripts/dev-status.sh    # resource states + endpoint URLs (--json); the port changes on every restart
scripts/dev-stop.sh      # the only sanctioned stop path
```

Never run `aspire stop --all` or `pkill -f <substring>`: both cross worktree boundaries and kill other
checkouts' instances. Kill by PID. Run one instance per data directory. Details: `scripts/README-dev-stop.md`,
`docs/agent-knowledge.md` §2.

- Kill-by-PID applies to anything you background, load generators included: collect the PIDs (or `setsid` a
  process group), put the kill in a `trap … EXIT`, and confirm with `ps` before ending the turn. A `kill $SPIN`
  that never reached its subshells left 23 spinners burning a core each for nine hours.
- The app origin serves the SPA bundle that was last **built**, not the working tree. After a frontend change,
  validate UI on the `client-react` Vite origin (`scripts/dev-status.sh --json` lists both) or run `pnpm run build`
  before `dev-start`, and record which origin the evidence came from.
- A scratch or fresh-DB host must not touch the user-level `~/.local/share/XE-Local-AI-Engine` (it rewrites the
  shared `installed-runtime.json`): point `XDG_DATA_HOME` at a scratch dir, or source the BYO llama-server
  override before `dev-start.sh` for GPU rounds.

## Validation

A change is done when these pass. **`--configuration Release` is load-bearing**: Debug skips the analyzers
(Meziantou, BannedApiAnalyzers, `IDExxxx`, the no-bare-`TODO` rule). Iterate in Debug, finish in Release;
`XE_FULL_ANALYSIS=1` forces the analyzers in Debug. A ~1 s incremental build that compiled nothing proves
nothing; use `--no-incremental` when the evidence matters. The sharper form: a deliberate-break proof is void
unless the Release build reported `0 Error(s)` — a build the analyzers failed runs the **previous** binary and
reports the old green. Keep break scaffolding analyzer-clean and restore it through a shell trap.

Before a gate chain: `dotnet build-server shutdown`, then export `MSBUILDDISABLENODEREUSE=1` and
`NUGET_PACKAGES=$HOME/.nuget/packages` for every `dotnet build`/`dotnet test` in the chain — stale MSBuild
worker nodes carry a deleted `NUGET_PACKAGES` across worktrees (NU5037 / CS0006 on a branch that is fine).
Never queue a gate on a worktree a writer is still editing; the result is void and no guard catches source edits.

Backend (repo root):

```bash
dotnet tool restore --tool-manifest dotnet-tools.json
scripts/with-build-lock.sh -- dotnet restore XE-Local-AI-Engine.slnx
scripts/with-build-lock.sh -- dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-restore
scripts/with-build-lock.sh -- scripts/assembly-guard.sh guard --test-bins -- \
  dotnet test XE-Local-AI-Engine.slnx --configuration Release --no-build --max-parallel-test-modules 1
```

- Never overlap a build with a `--no-build` test run. The lock serializes cooperating shells (exit **69** = lock
  not acquired, nothing ran); the guard detects an uncooperative build (exit **75** = CONTAMINATED, result void,
  re-run; it is not a red).
- Scope with `--treenode-filter '/*/*/(ClassA|ClassB)/*'`, never `--filter`. `--list-tests` is authoritative.
  A no-match filter exits 8. Zero tests is never a pass.
- Verify the whole changed test project before hand-off, not only the class you touched.
- `scripts/run-tests-memory-safe.sh` is the lower-memory full run of `XE-Local-AI-Engine.Tests` only; it locks
  and guards itself. Do not wrap it in the guard; if you must, pass `NO_BUILD=1` or every run reports exit 75.
  Run the other test projects separately.

Frontend (`XE-Local-AI-Engine.Client.React/`):

```bash
pnpm install --frozen-lockfile
pnpm run validate            # lint (tsc + biome + stylelint + guards) + knip + signalr:check + depcruise
pnpm run test:coverage:check # full vitest run with thresholds (pnpm test = same suite, no coverage, inner loop)
pnpm run test:tooling
pnpm run build
```

`pnpm run lint` is the typecheck; the E2E fixture's `build:e2e` is a bare `vite build`, so a green E2E run does
not prove types. After any backend contract change run `pnpm run openapi:check` (regenerates the hey-api client,
fails on drift) and commit the output. `pnpm run licenses:check` after a dependency change.

Python (`tools/training`, `scripts/**`): `scripts/python-validation.sh --scope changed` (or `full`). Needs `uv`.

Docs inventory: after adding a SignalR hub, `LocalApiRoutes` family, React `features/` dir, wiki page or
project, run `python3 scripts/docs-inventory-check.py` and name it in the wiki page that enumerates it.

Release scripts (`publish/**`, `scripts/release/**`): `scripts/lint-release-scripts.sh` (shellcheck ≥ 0.10,
PSScriptAnalyzer, Pester; a missing tool fails, never skips).

CI runs `.github/workflows/build-and-test.yml` on PRs and pushes to `develop` (five jobs: `python-quality`,
`release-contracts`, `backend-tests`, `build-and-test`, `client-react`); `release.yml` re-runs it before packaging.
`backend-tests` is a five-leg matrix — the sibling projects on one runner, four `TEST_SHARD` quarters of
`XE-Local-AI-Engine.Tests` on four more — and `build-and-test` cross-checks and merges their coverage under the job
name branch protection requires. Within a leg, projects run concurrently, each with its own `--results-directory`
(MTP resolves `--coverage-output` relative to it, so shared directories overwrite each other's Cobertura report);
the commands above are the local gate. Shape and rationale: `docs/wiki/13-testing-and-validation.md`, `docs/agent-knowledge.md` §1.

Opt-in live runners (nothing invokes them; ask before running, run before a tester RC):

- `scripts/run-e2e-local.sh` — Playwright; sets the mandatory `-p:RunE2ETests=true` and refuses a zero-test pass.
- `scripts/run-gpu-smoke-local.sh` — the only gate proving the GPU did the work; exit 5 = infra abort, 1 = product failed.
- `scripts/run-tool-grammar-smoke-local.sh` — after changing any tool schema or the llama.cpp pin; its failing
  negative control is the evidence, a run without it proved nothing.
- `scripts/run-docker-smoke-local.sh` — the four real-daemon container suites, which run ONLY under
  `XE_REQUIRE_DOCKER_TESTS=1` and skip everywhere else. CI proves the Engine API wire shape without a daemon
  against `Testing.FakeDocker`; this proves what only a daemon can. Exit 5 = no usable daemon, 1 = product failed.

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

- `docs/agent-knowledge.md` — hard-won rules by area (§1 build/test, §2 runtime, §3 models, §4 agents, §5 frontend).
- `docs/wiki/` — architecture; `docs/adr/` — decisions; `docs/roadmaps/` — status records.
- `CONTRIBUTING.md`, `.github/PULL_REQUEST_TEMPLATE.md` — what a PR must state.
