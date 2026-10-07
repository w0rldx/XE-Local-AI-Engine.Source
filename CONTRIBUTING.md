# Contributing

Thanks for your interest in XE Local AI Engine. This is an early-stage, Apache-2.0 project maintained by one person, so please keep changes focused and well-described.

## Before you start

- For anything non-trivial, open an issue first to discuss the approach.
- **Security issues:** do not open a public issue — see [SECURITY.md](SECURITY.md).
- This project follows the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md).
- Read [`AGENTS.md`](AGENTS.md) for the repo's conventions and the authoritative validation commands, and [`docs/agent-knowledge.md`](docs/agent-knowledge.md) for the hard-won invariants (build/analyzer rules, runtime traps) that reading the code won't tell you.
- **Adding a language?** See [`docs/translating.md`](docs/translating.md) — translating the UI is data plus three small wiring edits, no code changes.

## Development setup

- .NET SDK per [`global.json`](global.json).
- .NET 8 runtime for the pinned SBOM and dependency-license tools.
- Node 22, the major CI uses; `pnpm run acceptance` enforces it, and the React
  [`package.json`](XE-Local-AI-Engine.Client.React/package.json) `engines` field is only the floor. pnpm through
  Corepack or a local install.
- Python 3 for repository validation and lifecycle scripts.
- The Aspire CLI for AppHost development and integration checks.
- On Linux/WSL, `setsid` (normally supplied by `util-linux`) for transactional `scripts/dev-start.sh` cleanup.

## Validating your change

A change is done when these pass. [`AGENTS.md` §Validation](AGENTS.md#validation) is authoritative; it explains the
exit codes, the low-memory profile and the coverage caveats. `--configuration Release` is load-bearing: the analyzers,
including the "no bare `TODO`" rule, only run in Release.

Backend, from the repository root:

```bash
dotnet tool restore --tool-manifest dotnet-tools.json
scripts/run-backend-tests.sh
```

Frontend, from `XE-Local-AI-Engine.Client.React/` after the tool restore above:

```bash
pnpm install --frozen-lockfile
pnpm run openapi:check
pnpm run licenses:check
pnpm run acceptance
pnpm audit --prod --audit-level=high
```

`pnpm run acceptance` first checks that you run the same Node major as CI (Node 22) and stops otherwise;
`XE_ALLOW_NODE_DRIFT=1` continues with a warning that the run is not CI evidence. On frontend dependency-update
branches, also run `pnpm run dependencies:refresh`.

Release-script changes:

```bash
scripts/lint-release-scripts.sh
```

End-to-end tests are opt-in and ask-gated: `scripts/run-e2e-local.sh`.

## Pull requests

- Branch from and target `develop`.
- Keep commits focused; write clear messages (Conventional Commits are used across the history).
- Don't commit generated output by hand (the hey-api client is generated), secrets, or runtime data.
- Fill in the pull-request template and note how you validated the change.

## License

By contributing, you agree that your contributions are licensed under the project's [Apache-2.0](LICENSE) license.
