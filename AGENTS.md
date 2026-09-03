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
- Branch from and target `develop`. Commit messages follow Conventional Commits (`fix(scope): …`).
- Never commit secrets or runtime state: `node.key`, `*.sqlite`, `.env`, `dp-keys/`, `*.enc`.
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
- `Client.Testing` — shared host fixtures; `Testing.FakeOllama` — in-memory fake model server used by tests.
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

## Validation

A change is done when these pass. **`--configuration Release` is load-bearing**: Debug skips the analyzers
(Meziantou, BannedApiAnalyzers, `IDExxxx`, the no-bare-`TODO` rule). Iterate in Debug, finish in Release;
`XE_FULL_ANALYSIS=1` forces the analyzers in Debug. A ~1 s incremental build that compiled nothing proves
nothing; use `--no-incremental` when the evidence matters.

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

CI runs `.github/workflows/build-and-test.yml` on PRs and pushes to `develop` (four jobs: `python-quality`,
`release-contracts`, `build-and-test`, `client-react`); `release.yml` re-runs it before packaging. CI deliberately
runs backend test projects concurrently, each with its own `--results-directory` (MTP resolves `--coverage-output`
relative to it, so shared directories overwrite each other's Cobertura report); the commands above are the local
gate. Shape and rationale: `docs/wiki/13-testing-and-validation.md`, `docs/agent-knowledge.md` §1.

Opt-in live runners (nothing invokes them; ask before running, run before a tester RC):

- `scripts/run-e2e-local.sh` — Playwright; sets the mandatory `-p:RunE2ETests=true` and refuses a zero-test pass.
- `scripts/run-gpu-smoke-local.sh` — the only gate proving the GPU did the work; exit 5 = infra abort, 1 = product failed.
- `scripts/run-tool-grammar-smoke-local.sh` — after changing any tool schema or the llama.cpp pin; its failing
  negative control is the evidence, a run without it proved nothing.

## Conventions that bite

- Backend tests are TUnit on Microsoft.Testing.Platform, not xUnit. Style: `docs/wiki/17-writing-tests.md`.
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

<!-- CODEGRAPH_START -->
## CodeGraph

In repositories indexed by CodeGraph (a `.codegraph/` directory exists at the repo root), reach for it BEFORE grep/find or reading files when you need to understand or locate code:

- **MCP tool** (when available): `codegraph_explore` answers most code questions in one call — the relevant symbols' verbatim source plus the call paths between them, including dynamic-dispatch hops grep can't follow. Name a file or symbol in the query to read its current line-numbered source. If it's listed but deferred, load it by name via tool search.
- **Shell** (always works): `codegraph explore "<symbol names or question>"` prints the same output.

If there is no `.codegraph/` directory, skip CodeGraph entirely — indexing is the user's decision.
<!-- CODEGRAPH_END -->
