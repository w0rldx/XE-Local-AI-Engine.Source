# Contributing

Thanks for your interest in XE Local AI Engine. This is an early-stage, Apache-2.0 project maintained by one person, so please keep changes focused and well-described.

## Before you start

- For anything non-trivial, open an issue first to discuss the approach.
- **Security issues:** do not open a public issue — see [SECURITY.md](SECURITY.md).
- Read [`AGENTS.md`](AGENTS.md) for the repo's conventions and the authoritative validation commands, and [`docs/agent-knowledge.md`](docs/agent-knowledge.md) for the hard-won invariants (build/analyzer rules, runtime traps) that reading the code won't tell you.
- **Adding a language?** See [`docs/translating.md`](docs/translating.md) — translating the UI is data plus three small wiring edits, no code changes.

## Development setup

- .NET SDK per [`global.json`](global.json).
- .NET 8 runtime for the pinned SBOM and dependency-license tools.
- Node compatible with the React [`package.json`](XE-Local-AI-Engine.Client.React/package.json) and pnpm through
  Corepack or a local install.
- Python 3 for repository validation and lifecycle scripts.
- The Aspire CLI for AppHost development and integration checks.
- On Linux/WSL, `setsid` (normally supplied by `util-linux`) for transactional `scripts/dev-start.sh` cleanup.

## Validating your change

These are the real gates — a change isn't done until they pass. **The `--configuration Release` is load-bearing:** analyzers (including the "no bare `TODO`" rule) only run in Release.

Backend:

```bash
scripts/with-build-lock.sh -- dotnet restore XE-Local-AI-Engine.slnx
dotnet tool restore --tool-manifest dotnet-tools.json
scripts/with-build-lock.sh -- dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-restore
scripts/with-build-lock.sh -- scripts/assembly-guard.sh guard --test-bins -- \
  dotnet test XE-Local-AI-Engine.slnx --configuration Release --no-build --max-parallel-test-modules 1
```

That backend command set is restated from [`AGENTS.md`](AGENTS.md#validation), which is authoritative for it. Exit `69`
means the build lock was not acquired. Exit `75` means the test result was contaminated and is void; rerun it.
`scripts/run-tests-memory-safe.sh` is the lower-memory alternative for the `XE-Local-AI-Engine.Tests` module; it does not
cover the other test projects, so run those too.

Frontend CI gates (run `dotnet tool restore --tool-manifest dotnet-tools.json` once from the repository root, then run
these commands from `XE-Local-AI-Engine.Client.React/`):

```bash
pnpm install --frozen-lockfile
pnpm run openapi:check
pnpm run licenses:check
pnpm run lint
pnpm run test:coverage:check
pnpm run build
pnpm audit --prod --audit-level=high
```

`pnpm run lint` is the frontend typecheck. `openapi:check` validates the generated client against the committed spec;
after a backend contract change, follow the live-spec regeneration rules in [`AGENTS.md`](AGENTS.md#validation) before
trusting that drift check.

For frontend dependency-update branches, run `pnpm run dependencies:refresh` from
`XE-Local-AI-Engine.Client.React/`. It performs the frozen install first, then collects OpenAPI, generated-license,
validation, and production-build results and reports regenerated tracked files that belong in the same change. A
failed frozen install skips every generator so stale `node_modules` content cannot produce commit advice. Any
curated license override still requires human verification of its exact evidence, upstream source/tag, and SHA-256.

Release-script changes must also pass:

```bash
scripts/lint-release-scripts.sh
```

That default run includes the Pester suite and fails if required linters are missing. End-to-end tests remain opt-in and
ask-gated: `scripts/run-e2e-local.sh`.

## Pull requests

- Branch from and target `develop`.
- Keep commits focused; write clear messages (Conventional Commits are used across the history).
- Don't commit generated output by hand (the hey-api client is generated), secrets, or runtime data.
- Fill in the pull-request template and note how you validated the change.

## License

By contributing, you agree that your contributions are licensed under the project's [Apache-2.0](LICENSE) license.
