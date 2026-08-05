# Contributing

Thanks for your interest in XE Local AI Engine. This is an early-stage, Apache-2.0 project maintained by one person, so please keep changes focused and well-described.

## Before you start

- For anything non-trivial, open an issue first to discuss the approach.
- **Security issues:** do not open a public issue — see [SECURITY.md](SECURITY.md).
- Read [`AGENTS.md`](AGENTS.md) for the repo's conventions and the authoritative validation commands, and [`docs/agent-knowledge.md`](docs/agent-knowledge.md) for the hard-won invariants (build/analyzer rules, runtime traps) that reading the code won't tell you.

## Development setup

- .NET SDK per [`global.json`](global.json).
- Node + [pnpm](https://pnpm.io/) for the React client (`XE-Local-AI-Engine.Client.React/`).

## Validating your change

These are the real gates — a change isn't done until they pass. **The `--configuration Release` is load-bearing:** analyzers (including the "no bare `TODO`" rule) only run in Release.

Backend:

```bash
dotnet restore XE-Local-AI-Engine.slnx
dotnet build   XE-Local-AI-Engine.slnx --configuration Release --no-restore
dotnet test    XE-Local-AI-Engine.slnx --configuration Release --no-build --max-parallel-test-modules 1
```

`scripts/run-tests-memory-safe.sh` wraps the heavy test run and guards against build/test contamination.

Frontend (from `XE-Local-AI-Engine.Client.React/`):

```bash
pnpm install
pnpm run lint      # the only frontend typecheck
pnpm test
pnpm run build
```

After any backend contract change, regenerate and check the API client:

```bash
pnpm openapi:check
```

End-to-end tests are opt-in and ask-gated: `scripts/run-e2e-local.sh`.

## Pull requests

- Branch from and target `develop`.
- Keep commits focused; write clear messages (Conventional Commits are used across the history).
- Don't commit generated output by hand (the hey-api client is generated), secrets, or runtime data.
- Fill in the pull-request template and note how you validated the change.

## License

By contributing, you agree that your contributions are licensed under the project's [Apache-2.0](LICENSE) license.
