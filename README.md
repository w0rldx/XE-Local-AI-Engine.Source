# XE Local AI Engine

XE Local AI Engine is the node-side runtime for running local AI workloads while preserving the existing C0re platform contract. The Node Web Server hosts the React management UI, owns the platform
`WorkerHub` connection, and runs the local model runtime in-process via the llama.cpp supervisor.

The repository is being prepared for an RC release. Release documentation and validation evidence live in this repo and must stay current with runtime behavior.

> **RC branch:** `feature/agent-mode-foundation` is the RC1 branch (decision D4, locked 2026-06-10). Merge into `develop` is post-RC. The `develop` branch is 208 commits behind and is not the active development line until after RC1 ships.

## What ships from this repo

- **Node Web Server** (`XE-Local-AI-Engine.Client`) — serves the React UI, local APIs under `/api/local/v1`, local SignalR hubs, SQLite-backed chat state, and the existing platform `WorkerHub`
  connection.
- **React management UI** (`XE-Local-AI-Engine.Client.React`) — node-local browser UI for chat, settings, runtime status, logs, and models.
- **Providers and agents** — local provider abstractions, Ollama provider integration, and shared agent execution loop.
- **Scheduler** — Quartz.NET-backed job scheduler with job definitions, run history, cancellation, and live run updates over a local SignalR hub (`Services/Scheduler`, `src/features/scheduler`).
- **Model-fit** — on-demand model recommendation and benchmark runs against a digest-pinned, approved utility image, exposed as cache-only reads plus a scheduler-driven refresh (`Services/ModelFit`,
  `src/features/model-fit`).
- **Agent mode** — per-agent definitions plus a governed playbook: manual and analysis-proposed actions, an offline eval gate over golden conversations, relevance-gated action retrieval, and cohort
  monitoring (`Services/{Agents,Eval,Insights,Monitoring}`, `XE-Local-AI-Engine.AI.Agent`, `src/features/agents`).
- **MCP tool extensibility** — registered MCP servers whose live tool snapshots are offered to agents through the local tool registry (`Services/Mcp`, `src/features/mcp`).
- **Tests and fixtures** — backend/client persistence tests, integration-style tests, E2E harness, and FakeOllama support.

## Architecture rules

- Only the Node Web Server talks to the C0re platform over `WorkerHub`.
- Worker credentials, cloud-provider credentials, and external endpoint tokens stay local and must not be returned to the browser or written to logs/transcripts.
- Local admin endpoints must be loopback/local-only, authenticated, strict about `Host`/`Origin`, and secret-redacted.
- Any future installer or packaging effort must not create background autostart behavior unless a new approved plan changes that contract.

## Documentation map

- [AI runtime developer notes](docs/ai-runtime.md)
- [Backend commentary map](docs/backend-commentary-map.md)

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

Use Aspire for local development and integration checks.

```bash
dotnet run --project XE-Local-AI-Engine.AppHost --launch-profile https
```

## RC readiness status

Do not mark release or documentation work complete until matching validation evidence is available.

Required evidence includes:

- restore/build/test transcripts
- generated schema/sample manifest validation
- digest-pinned runtime images and package checksums
- runtime smoke-test transcript

Standalone OS-package distribution (MSI/deb/rpm) is deferred: under the runtime re-architecture the app self-provisions its llama.cpp runtime and GGUF models at first run, so there is no installer bundle to validate. A future packaging effort would be its own plan.
