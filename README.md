# XE Local AI Engine

XE Local AI Engine is the node-side runtime for running local AI workloads while preserving the existing C0re platform contract. The Node Web Server hosts the React management UI, owns the platform
`WorkerHub` connection, and runs the local model runtime in-process via the llama.cpp supervisor.

The repository is being prepared for an RC release. Release documentation and validation evidence live in this repo and must stay current with runtime behavior.

> **RC branch:** `feature/agent-mode-foundation` is the RC1 branch (decision D4, locked 2026-06-10). Merge into `develop` is post-RC. The `develop` branch is 208 commits behind and is not the active development line until after RC1 ships.

## What ships from this repo

- **Node Web Server** (`XE-Local-AI-Engine.Client`) — serves the React UI, local APIs under `/api/local/v1`, local SignalR hubs, SQLite-backed chat state, and the existing platform `WorkerHub`
  connection.
- **React management UI** (`XE-Local-AI-Engine.Client.React`) — node-local browser UI for chat, settings, runtime status, logs, and models.
- **Providers and agents** — local provider abstractions, llama.cpp in-app runtime (primary/default), Ollama provider (opt-in secondary), and shared agent execution loop.
- **Scheduler** — Quartz.NET-backed job scheduler with job definitions, run history, cancellation, and live run updates over a local SignalR hub (`Services/Scheduler`, `src/features/scheduler`).
- **Model-fit** — on-demand model recommendation and benchmark runs against a digest-pinned, approved utility image, exposed as cache-only reads plus a scheduler-driven refresh (`Services/ModelFit`,
  `src/features/model-fit`).
- **Agent mode** — per-agent definitions plus a governed playbook: manual and analysis-proposed actions, an offline eval gate over golden conversations, relevance-gated action retrieval, and cohort
  monitoring (`Services/{Agents,Eval,Insights,Monitoring}`, `XE-Local-AI-Engine.AI.Agent`, `src/features/agents`).
- **MCP tool extensibility** — registered MCP servers whose live tool snapshots are offered to agents through the local tool registry (`Services/Mcp`, `src/features/mcp`).
- **Tests and fixtures** — backend/client persistence tests, integration-style tests, E2E harness, and FakeOllama in-process test server.

## Architecture rules

- Only the Node Web Server talks to the C0re platform over `WorkerHub`.
- Worker credentials, cloud-provider credentials, and external endpoint tokens stay local and must not be returned to the browser or written to logs/transcripts.
- Local admin endpoints must be loopback/local-only, authenticated, strict about `Host`/`Origin`, and secret-redacted.
- Any future installer or packaging effort must not create background autostart behavior unless a new approved plan changes that contract.

## Documentation map

The contributor deep-dive lives in the **[Developer Wiki](docs/wiki/Home.md)** — code-grounded
pages covering architecture, every project, the local llama.cpp runtime and providers, agent mode,
chat, scheduler, model-fit, data/persistence, the API surface, the React client, hosting/deployment,
security/privacy, and testing. Start at [`docs/wiki/Home.md`](docs/wiki/Home.md).

Supporting notes:

- [AI runtime developer notes](docs/ai-runtime.md) — narrow AI-seam maintenance rules (see the wiki for the full runtime architecture).
- [Backend commentary map](docs/backend-commentary-map.md)

Component-specific notes:

- [Node Web Server README](XE-Local-AI-Engine.Client/README.md)
- [React Client README](XE-Local-AI-Engine.Client.React/README.md)

## Local development

### Prerequisites

- .NET SDK from [`global.json`](global.json)
- Node.js compatible with `XE-Local-AI-Engine.Client.React/package.json`
- pnpm via Corepack or a local install
- A GPU with current drivers is optional; the app self-provisions its llama.cpp runtime and GGUF models at first run. (Docker and Ollama are **not** required — Docker was removed in the 2026-06-17 runtime re-architecture and llama.cpp is the local runtime; see [docs/wiki/03-local-runtime-and-providers.md](docs/wiki/03-local-runtime-and-providers.md).)

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

## Self-contained desktop run

The host can be published as a **single self-contained executable** (the .NET runtime is bundled — no prerequisite
install) that runs as a "double-click" desktop app: a console window opens showing live logs, the default browser opens
on the running site, and **closing the console window shuts the whole app down** — including the spawned `llama-server`
child, so there is no orphan process. Closing the browser does *not* stop the app.

Desktop mode is **opt-in** via the launcher (env `XE_LAUNCH_MODE=desktop` or the `--desktop` flag); headless, Aspire,
and CI runs are unaffected. In desktop mode the host binds **HTTP on a free loopback port** (`127.0.0.1`) and skips the
HTTPS-redirect/HSTS pipeline (traffic never leaves the loopback adapter).

### Publish

```bash
# Linux
dotnet publish XE-Local-AI-Engine.Client -c Release -r linux-x64 -p:PublishProfile=linux-x64
# Windows
dotnet publish XE-Local-AI-Engine.Client -c Release -r win-x64 -p:PublishProfile=win-x64
```

The profiles set `SelfContained`, `PublishSingleFile`, and `IncludeNativeLibrariesForSelfExtract=true` (so `e_sqlite3`
and libsodium extract and load from the bundle); trimming stays **off** (EF Core / Serilog / FastEndpoints / MEAI are
reflection-heavy). Output lands in `XE-Local-AI-Engine.Client/bin/Release/net10.0/<rid>/publish/`.

### Run

Copy the matching launcher from [`publish/`](publish/) next to the published binary and start it:

- **Linux:** `publish/linux/run-xe-local-ai-engine.sh` — `exec`s the binary in the foreground so the terminal owns it
  (terminal close → `SIGHUP` → graceful shutdown).
- **Windows:** `publish/windows/run-xe-local-ai-engine.cmd` — runs the exe in the current console window so closing that
  window fires `CTRL_CLOSE_EVENT` → graceful shutdown (the Job Object is the hard-kill safety net).

See [`publish/README.md`](publish/README.md) for the expected layout. **Run one instance at a time** against the same
user-data directory — a second instance races on the SQLite database.

> The no-orphan guarantee (terminal/console close reaps `llama-server`) and the Windows Job Object path are verified on
> real desktops with a model loaded; they cannot be exercised in WSL2/CI.

## RC readiness status

Do not mark release or documentation work complete until matching validation evidence is available.

Required evidence includes:

- restore/build/test transcripts
- generated schema/sample manifest validation
- digest-pinned runtime images and package checksums
- runtime smoke-test transcript

Standalone OS-package distribution (MSI/deb/rpm) is deferred: under the runtime re-architecture the app self-provisions its llama.cpp runtime and GGUF models at first run, so there is no installer bundle to validate. A future packaging effort would be its own plan.
