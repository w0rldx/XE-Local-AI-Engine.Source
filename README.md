# XE Local AI Engine

XE Local AI Engine is the node-side runtime for running local AI workloads while preserving the existing C0re platform contract. The Node Web Server hosts the React management UI, owns the platform `WorkerHub` connection, and coordinates local model/runtime workflows through HostAgent and Tray components.

The repository is being prepared for an RC release. Release documentation and validation evidence live in this repo and must stay current with runtime behavior.

## What ships from this repo

- **Node Web Server** (`XE-Local-AI-Engine.Client`) — serves the React UI, local APIs under `/api/local/v1`, local SignalR hubs, SQLite-backed chat state, and the existing platform `WorkerHub` connection.
- **React management UI** (`XE-Local-AI-Engine.Client.React`) — node-local browser UI for chat, settings, runtime status, logs, models, and HostAgent actions.
- **HostAgent.Windows / HostAgent.Linux** — local substrate components for Windows-managed WSL2 and Linux-native runtime management.
- **Tray Launcher** (`XE-Local-AI-Engine.Tray`) — desktop entry point, status surface, and local start/stop/restart control.
- **Providers and agents** — local provider abstractions, Ollama provider integration, and shared agent execution loop.
- **Tests and fixtures** — backend/client persistence tests, integration-style tests, E2E harness, and FakeOllama support.

## Architecture rules

- Only the Node Web Server talks to the C0re platform over `WorkerHub`.
- HostAgent and Tray are local substrate components only; they do not connect to the platform.
- Worker credentials, admin tokens, HMAC secrets, cloud-provider credentials, and external endpoint tokens stay local and must not be returned to the browser or written to logs/transcripts.
- Local admin endpoints must be loopback/local-only, authenticated, strict about `Host`/`Origin`, and secret-redacted.
- Windows and Linux installers must not create background autostart behavior unless a new approved plan changes that contract.

See [HostAgent architecture](docs/host-agent/architecture.md) for the full component and security-boundary model.

## Documentation map

Start with the HostAgent documentation index:

- [HostAgent docs](docs/host-agent/README.md)
- [Release and operations](docs/host-agent/release-and-operations.md)
- [Aspire development modes](docs/host-agent/aspire-dev.md)
- [Windows installation](docs/host-agent/install-windows.md)
- [Linux installation](docs/host-agent/install-linux.md)
- [Tray launcher](docs/host-agent/tray.md)
- [Bring your own runtime](docs/host-agent/byo.md)
- [Troubleshooting](docs/host-agent/troubleshooting.md)

Component-specific notes:

- [Node Web Server README](XE-Local-AI-Engine.Client/README.md)
- [React Client README](XE-Local-AI-Engine.Client.React/README.md)

## Local development

### Prerequisites

- .NET SDK from [`global.json`](global.json)
- Node.js compatible with `XE-Local-AI-Engine.Client.React/package.json`
- pnpm via Corepack or a local install
- Docker/rootless Docker and Ollama when exercising runtime-fidelity or release-like paths

### Common commands

From the repository root:

```bash
dotnet restore XE-Local-AI-Engine.slnx
dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-restore
dotnet test XE-Local-AI-Engine.slnx --configuration Release --no-build
```

For the React client:

```bash
cd XE-Local-AI-Engine.Client.React
pnpm install --frozen-lockfile
pnpm run lint
pnpm test
pnpm run build
```

The repository validation wrapper mirrors these commands:

```bash
bash .opencode/scripts/project-validate.sh --scope changed --serial
```

E2E validation is ask-gated because it may require browser/runtime setup:

```bash
bash .opencode/scripts/project-validate.sh --scope e2e --confirm-e2e --serial
```

## Aspire modes

Use Aspire for local development and integration checks, not as a replacement for installer clean-install tests.

```bash
dotnet run --project XE-Local-AI-Engine.AppHost --launch-profile https
dotnet run --project XE-Local-AI-Engine.AppHost --launch-profile https-fast-dev
dotnet run --project XE-Local-AI-Engine.AppHost --launch-profile https-runtime-fidelity
```

See [Aspire development modes](docs/host-agent/aspire-dev.md) for mode details and limitations.

## RC readiness status

Do not mark release or documentation work complete until matching validation evidence is available.

Required evidence includes:

- restore/build/test transcripts
- generated schema/sample manifest validation
- digest-pinned runtime images and package checksums
- Windows clean-install transcript
- Linux clean-install transcript
- runtime smoke-test transcript

Clean-install runner scripts are tracked under `ci/host-agent/`. Their transcripts remain pending until RC MSI/deb/rpm artifacts are produced and executed on clean runners.

Use [Release and operations](docs/host-agent/release-and-operations.md) as the release checklist and evidence index.
