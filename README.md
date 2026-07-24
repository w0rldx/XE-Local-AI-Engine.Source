# XE Local AI Engine

XE Local AI Engine is the node-side runtime for running local AI workloads while preserving the existing C0re platform contract. The Node Web Server hosts the React management UI, owns the platform
`WorkerHub` connection, and runs the local model runtime in-process via the llama.cpp supervisor.

The repository is being prepared for an RC release. Release documentation and validation evidence live in this repo and must stay current with runtime behavior.

## What ships from this repo

- **Node Web Server** (`XE-Local-AI-Engine.Client`) — serves the React UI, local APIs under `/api/local/v1`, local SignalR hubs, SQLite-backed chat state, and the existing platform `WorkerHub`
  connection.
- **React management UI** (`XE-Local-AI-Engine.Client.React`) — node-local browser UI for chat, settings, runtime status, logs, and models.
- **Providers and agents** — local provider abstractions, llama.cpp in-app runtime (primary/default), Ollama provider (opt-in secondary), and shared agent execution loop.
- **Scheduler** — Quartz.NET-backed job scheduler with job definitions, run history, cancellation, and live run updates over a local SignalR hub (`Services/Scheduler`, `src/features/scheduler`).
- **Model-fit / Model Advisor** — box-aware GGUF recommendation: profiles the local hardware (RAM / VRAM / GPU vendor), discovers candidate GGUF repos on Hugging Face, estimates each model's memory footprint with a
  pure, in-process (I/O-free) formula, and ranks the ones that fit. Exposed as cache-only reads plus a scheduler-driven refresh — there is no container or benchmark image (Docker was removed in the 2026-06-17
  runtime re-architecture) (`Services/ModelFit`, `src/features/model-fit`). See [docs/wiki/07-model-fit.md](docs/wiki/07-model-fit.md).
- **Image generation** — local text-to-image via **stable-diffusion.cpp**: the node supervises a resident `sd-server` child process (one daemon per model, readiness-gated, idle-evicted on its own loopback port
  range), serializes generation to one job at a time with queue/cancel, and persists produced images encrypted-at-rest. Ships **enabled by default** (`Services/Images`, `Providers.StableDiffusionCpp`,
  `src/features/images`). See [docs/wiki/14-image-generation.md](docs/wiki/14-image-generation.md).
- **Knowledge Base / RAG** — fully offline document knowledge base: upload documents (`.txt`/`.md`/`.pdf`/`.docx` and other plaintext types), which are chunked, embedded with a local embedding model, and indexed
  into local SQLite with **selective encryption** — source document blobs and display names are encrypted at rest, while the extracted chunk text and its FTS search index are stored unencrypted locally. Retrieval is a **hybrid search** — lexical FTS5 + semantic vector arms fused with Reciprocal Rank Fusion, with an optional local cross-encoder reranker — surfaced to agents as a tool
  (`Services/Knowledge`, `Endpoints/Knowledge/V1`, `src/features/knowledge`). See [docs/wiki/15-knowledge-base.md](docs/wiki/15-knowledge-base.md).
- **Agent mode** — per-agent definitions plus a governed playbook: manual and analysis-proposed actions, an offline eval gate over golden conversations, relevance-gated action retrieval, and cohort
  monitoring (`Services/{Agents,Eval,Insights,Monitoring}`, `XE-Local-AI-Engine.AI.Agent`, `src/features/agents`).
- **Development Mode** — a default-on, node-local coding workflow with engine-owned detached Git worktrees, deterministic validation, independent review, hash-bound evidence, and explicit final host apply.
  The operator registers a trusted local Git repository once, then selects it by an opaque ID and alias; the host path stays internal to the node. The agent works in a managed worktree outside the selected source
  repository, and only a reviewed apply whose base and evidence hashes still match may change that source. Generated source, MSBuild targets, source generators, and tests execute as the host user with the host's
  filesystem and network access. The Process sandbox and Agent Home controls constrain application-mediated paths and bytes; they are not an operating-system security boundary. Set
  `Development:Enabled=false` as an emergency switch when that execution posture is not intended. MXC and devcontainer-backed isolation remain future provider work.
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
dotnet test XE-Local-AI-Engine.slnx --configuration Release --no-build --max-parallel-test-modules 1
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
bash .opencode/scripts/project-validate.sh --scope changed --base develop --serial
```

> **Pass `--base develop`.** `--scope changed` defaults to `--base main` (`project-validate.sh:340`), but this repository
> has **no `main` branch** — the default branch is `develop`. With the default the script silently falls back to
> `git diff HEAD~1` (`project-validate.sh:344-348`), so it validates a *single commit* instead of your whole branch and
> reports green while never touching most of your changes.

E2E validation is ask-gated because it may require browser/runtime setup:

```bash
bash .opencode/scripts/project-validate.sh --scope e2e --confirm-e2e --serial
```

## Aspire modes

Use Aspire for local development and integration checks.

```bash
aspire run --apphost XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj
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

For a Windows tester RC, use [`publish/package-tester-win.ps1`](publish/package-tester-win.ps1) — run on Windows, it is
the canonical build, validation, packaging, and tester-upload path, and every published tester RC came from it.
`publish/package-rc.sh` is a separate, much simpler manual portable-zip packager: a bash script you run on Linux/WSL,
producing a plain self-contained zip with **no Velopack metadata and therefore no self-update**. It builds **both**
`linux-x64` and `win-x64` by default (`--rid <rid>` for one).

> **A `win-x64` zip from `package-rc.sh` is cross-built on Linux.** Smoke-test it on real Windows before handing it to
> anyone — native-library self-extraction, console-close child cleanup, and browser auto-open cannot be verified
> off-Windows. The same applies to the two desktop invariants below.

> The no-orphan guarantee (terminal/console close reaps `llama-server`) and the Windows Job Object path are verified on
> real desktops with a model loaded; they cannot be exercised in WSL2 or on a headless runner.

## RC readiness status

Do not mark release or documentation work complete until matching validation evidence is available.

Required evidence includes:

- restore/build/test transcripts
- generated schema/sample manifest validation
- pinned runtime binary and package checksums
- runtime smoke-test transcript

Standalone OS-package distribution (MSI/deb/rpm) is deferred: under the runtime re-architecture the app self-provisions its llama.cpp runtime and GGUF models at first run, so there is no installer bundle to validate. A future packaging effort would be its own plan.
