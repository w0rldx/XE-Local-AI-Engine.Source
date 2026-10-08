# AGENTS.md

XE Local AI Engine: one ASP.NET Core node (`XE-Local-AI-Engine.Client`) serving a React SPA, loopback `/api/local/v1`
endpoints and SignalR hubs, SQLite with encrypted columns, and supervised `llama-server`/`sd-server`/`whisper-server`
children. .NET 10 + Aspire, React 19 + Vite + pnpm, Python (uv) for training.

Instructions, not documentation: each rule has one home, and this file points at it. Before a non-trivial change,
read `docs/agent-knowledge.md` (§0 and its routing table), then the topic files your change touches, including their
stale-beliefs tables. Architecture: start at `docs/wiki/Home.md`.

## Working rules

- Do not edit files, run mutating commands, or clean up before the operator approves a plan. Read-only
  investigation, files under gitignored plan folders, and edits the operator asked for directly are exempt.
- On a failure, stop and report the failing command and its output before attempting a fix.
- Parallel work goes in git worktrees under `.tmp/worktrees/`; never let two tasks claim the same file.
- Agents sharing one worktree share one git index: never `git add` there. Commit through a private index:
  wiki 13, "Committing in a shared worktree".
- Branch from and target `develop`. Nothing lands on `develop` without the operator's explicit approval: never
  merge into, reset or "repair" `develop` mid-task; a feature branch absorbs `develop`, never the reverse. Once the
  operator approves the squash, absorb `develop` into the branch once, run the backend gate once on that tip, then run
  `scripts/squash-guard.sh <branch>` from the main checkout and squash only on its `OK` line.
- Commit messages follow Conventional Commits (`fix(scope): …`), body only: no session-tracking, co-author or
  AI trailers. Never set git identity (`git -c user.*`, `git config user.*`, `GIT_AUTHOR_*`/`GIT_COMMITTER_*`);
  a plain `git commit` inherits the repo config.
- Never commit secrets or runtime state (`node.key`, `*.sqlite`, `.env`, `dp-keys/`, `*.enc`). Never name optional
  per-user agent or editor tooling in product code, gates, tests or instructions: state what it would enforce as a
  repo rule (`.gitignore` excepted; shipped installers and MCP client runbooks are product, not tooling).
  Cite code as file + symbol, never `file:line`. Describe the environment generically, never the host machine.
  `scripts/docs-inventory-check.py` rejects `file:line` citations, tracked secrets, tool names and host-specific phrasing.
- Never hand-edit generated output: the hey-api client under `src/core/api/generated/`, `routeTree.gen.ts`,
  EF migration designer files. Regenerate and commit the result.
- Propose a new trap in `docs/agent-knowledge/proposed.md` (format: its index, "Maintenance"); promotion needs
  operator approval. Never quote a count or timing from a doc as current: the script's own output wins.

## Repository map

Solution `XE-Local-AI-Engine.slnx`; every project, the workflows and the dependency exceptions:
`docs/wiki/02-project-layout.md`. Host `XE-Local-AI-Engine.Client` → `Client.Application` (services) →
`Client.Persistence` (EF Core + SQLite). `XE-Local-AI-Engine.Client.React` is the SPA, with its own `AGENTS.md`.
Tests: TUnit projects plus opt-in Playwright E2E (wiki 13). `scripts/` holds the dev lifecycle and gates; each
script's header documents its flags and exit codes. `tools/training/` is the shipped Python training runtime: never
`uv sync` inside it.

## Local runtime

`scripts/dev-start.sh`, `dev-status.sh` (the port changes on every restart) and `dev-stop.sh` (the only sanctioned
stop path) run an isolated AppHost for THIS checkout: `scripts/README-dev-stop.md`.

- Never run `aspire stop --all` or `pkill -f <substring>`: both cross worktree boundaries. Kill by PID, including
  anything you background: collect the PIDs (or `setsid` a process group), put the kill in a `trap … EXIT`, and
  confirm with `ps` before ending the turn. Run one instance per data directory.
- Live rounds: `scripts/lab-up.sh` brings a lab node to the model-ready handoff and `scripts/lab-api.sh` makes the
  logged calls (wiki 13, "Lab bootstrap for live rounds"); pass `--plan <plan-name>` so the lab's OWNER file names its
  plan (`dev-status.sh` lists unowned and expired lab data).
- The app origin serves the SPA bundle that was last **built**. After a frontend change, validate UI on the
  `client-react` Vite origin or run `pnpm run build` before `dev-start`, and record which origin the evidence came from.
- A scratch or fresh-DB host must not touch the user-level `~/.local/share/XE-Local-AI-Engine`: point
  `XDG_DATA_HOME` at a scratch dir, or source the BYO llama-server override before `dev-start.sh` for GPU rounds.

## Validation

A change is done when these pass. Why each rule exists: `docs/wiki/13-testing-and-validation.md`.

```bash
dotnet tool restore --tool-manifest dotnet-tools.json
scripts/run-backend-tests.sh   # THE backend gate: one Release build, then every enrolled test project
```

- Finish in `--configuration Release`: Debug skips the analyzers. Evidence you report (a deliberate-break proof, a
  before/after comparison) comes from a `--no-incremental` Release build that reported `0 Error(s)`; restore break
  scaffolding through a shell trap.
- `dotnet build-server shutdown`, `MSBUILDDISABLENODEREUSE=1` and `NUGET_PACKAGES=$HOME/.nuget/packages` are only for
  direct `dotnet build`/`dotnet test` invocations; `scripts/run-backend-tests.sh` does it itself.
- Never queue a gate on a worktree a writer is still editing; never overlap a build with a `--no-build` test run.
  Check `scripts/build-lock-status.sh` before queueing. Exit 69 = lock not acquired, nothing ran; 75 =
  CONTAMINATED, void, re-run (not a red).
- The backend gate runs the Architecture namespace alone right after the Release build; red there stops the gate
  (`FAIL-FAST`, exit 1) before the long lanes; `XE_GATE_FAIL_FAST=0` skips it. Every gate run appends one line to the
  main checkout's `.tmp/gate-history.log`; a failing test listed in `scripts/known-flakes.txt` is annotated, never skipped.
- Scope with `--treenode-filter '/*/*/(ClassA|ClassB)/*'`, never `--filter`; `--list-tests` is authoritative, a
  no-match filter exits 8, and zero tests is never a pass. Before hand-off run the whole changed test project.
- Cross-cutting triggers: a startup, DI or `Program.cs` change runs the whole `Integration` namespace; a new or renamed
  stored node setting runs `SettingsReview_CoversEveryStoredField_ExactlyOnce`; a `Client.Persistence.Tests` fixture
  change runs that whole project.

Frontend, from `XE-Local-AI-Engine.Client.React/` (details in its `AGENTS.md`):

```bash
pnpm install --frozen-lockfile
../scripts/with-build-lock.sh -- pnpm run acceptance   # vitest timing assertions fail beside a backend gate
```

- `acceptance` first runs `XE-Local-AI-Engine.Client.React/scripts/CheckNodeMajor.mjs`, which fails on a Node major
  other than CI's. After a backend contract change run `pnpm run openapi:check` and commit the regenerated client.

Other gates:

- Python (`tools/training`, `scripts/**`): `scripts/python-validation.sh --scope changed`.
- After adding a SignalR hub, `LocalApiRoutes` family, React `features/` dir, wiki page or project: run
  `python3 scripts/docs-inventory-check.py` and name it in the wiki page that enumerates it.
- `publish/**`, `scripts/release/**`: `scripts/lint-release-scripts.sh`.
- CI: `.github/workflows/build-and-test.yml`; its `build-and-test` job is the required check (shape: wiki 13).

Opt-in live runners in `scripts/`: `run-e2e-local.sh`, `run-gpu-smoke-local.sh`, `run-tool-grammar-smoke-local.sh`,
`run-retrieval-eval-local.sh`, `run-docker-smoke-local.sh`, `run-model-matrix-local.sh`. Nothing invokes them. Ask
before running one unless the task targets what it checks; the operator decides which run before a tester RC (wiki
13, "RC evidence requirements").

- The GPU smoke is the only gate proving the GPU did the work.
- Run the tool-grammar smoke after a tool-schema or llama.cpp-pin change; without its failing negative control the
  run proved nothing.
- The model matrix is slow: run it once, last, only for inference, thinking, tool offering, window budgeting or
  model-fit changes; never while iterating.

## Conventions that bite

- Backend tests are TUnit on Microsoft.Testing.Platform, not xUnit. Every test is independent and self-validating
  (skip visibly with a reason, never a silent `return`); never sleep to wait for or rule out an event; mock in the
  order real thing, repo fake seam, `Substitute.For<T>()`/`vi.fn()`, hand-written fake, and never mock the gate,
  cipher or migration under test. Rules and style: `docs/wiki/17-writing-tests.md`.
- Code conventions: `docs/wiki/16-code-conventions.md`; Release analyzers and Architecture tests enforce most of them.

## Where to look

- `docs/wiki/` — architecture; `docs/adr/` — decisions; `docs/roadmaps/` — status records.
- `CONTRIBUTING.md`, `.github/PULL_REQUEST_TEMPLATE.md` — what a PR must state.
