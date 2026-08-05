# XE Local AI Engine

XE Local AI Engine is the node-side runtime for running local AI workloads while preserving the existing C0re platform contract. The Node Web Server hosts the React management UI, owns the platform
`WorkerHub` connection, and supervises node-owned `llama-server` host child processes for local inference.

The repository is being prepared for an RC release. Release documentation and validation evidence live in this repo and must stay current with runtime behavior.

> **Just want to install and use the app?** Start with the **[User Guide](docs/user-guide/README.md)** — download,
> install (Windows & Linux), first run, troubleshooting, and privacy, all in plain language. App downloads are on the
> [Releases](../../releases/latest) page.

## What ships from this repo

- **Node Web Server** (`XE-Local-AI-Engine.Client`) — serves the React UI, local APIs under `/api/local/v1`, local SignalR hubs, SQLite-backed chat state, and the existing platform `WorkerHub`
  connection.
- **React management UI** (`XE-Local-AI-Engine.Client.React`) — node-local browser UI for chat, settings, runtime status, logs, and models.
- **Providers and agents** — local provider abstractions, the supervised llama.cpp host runtime (primary/default), Ollama provider (opt-in secondary), and shared agent execution loop.
- **Scheduler** — Quartz.NET-backed job scheduler with job definitions, run history, cancellation, and live run updates over a local SignalR hub (`Services/Scheduler`, `src/features/scheduler`).
- **Model-fit / Model Advisor** — box-aware GGUF recommendation: profiles the local hardware (RAM / VRAM / GPU vendor), discovers candidate GGUF repos on Hugging Face, estimates each model's memory footprint with a
  pure, in-process (I/O-free) formula, and ranks the ones that fit. Exposed as cache-only reads plus a scheduler-driven refresh — the advisor is estimator-only and never spawns a process, so there is no container
  or benchmark image anywhere in this path (`Services/ModelFit`, `src/features/model-fit`). See [docs/wiki/07-model-fit.md](docs/wiki/07-model-fit.md).
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
  `Development:Enabled=false` as an emergency switch when that execution posture is not intended. MXC remains future provider work.
  [ADR 0004](docs/adr/0004-development-mode-container-execution-docker-stopgap.md) (accepted 2026-07-29) moves this feature's execution onto a **Docker container provider**, behind the same provider seam and as an
  interim step ahead of MXC. That provider has **shipped and is opt-in**: set `Development:Sandbox:Provider=docker` to select it. Leave it unset — as the shipped configuration does — and Development Mode keeps running on
  the process provider exactly as described above. On a node that *does* select it, **a running Docker daemon is a hard requirement** — there is deliberately no unisolated fallback, so a machine without one gets no
  Development Mode rather than a quietly weaker one. Docker stays scoped to this feature: chat, embeddings, model acquisition and image generation never require it. See
  [Development Mode container implementation status](docs/roadmaps/development-mode-container-status.md) for the maintained record of what is implemented.
- **MCP tool extensibility** — registered MCP servers whose live tool snapshots are offered to agents through the local tool registry (`Services/Mcp`, `src/features/mcp`).
- **Tests and fixtures** — backend/client persistence tests, integration-style tests, E2E harness, and FakeOllama in-process test server.

## Architecture rules

- Only the Node Web Server talks to the C0re platform over `WorkerHub`.
- Worker credentials, cloud-provider credentials, and external endpoint tokens stay local and must not be returned to the browser or written to logs/transcripts.
- Local admin endpoints must be loopback/local-only, authenticated, strict about `Host`/`Origin`, and secret-redacted.
- Any future installer or packaging effort must not create background autostart behavior unless a new approved plan changes that contract.

## Documentation map

**Using the app (non-developers):** the **[User Guide](docs/user-guide/README.md)** covers download, install,
first run, troubleshooting, privacy, and a plain-language glossary.

The contributor deep-dive lives in the **[Developer Wiki](docs/wiki/Home.md)** — code-grounded
pages covering architecture, every project, the local llama.cpp runtime and providers, agent mode,
chat, scheduler, model-fit, data/persistence, the API surface, the React client, hosting/deployment,
security/privacy, and testing. Start at [`docs/wiki/Home.md`](docs/wiki/Home.md).

For a baseline-scoped external review, use the
**[Technical/Security Architecture Dossier](docs/audits/technical-security-architecture/README.md)**.
It describes the implementation at commit `7e64ed589e14eecc0e522e807d2e531a1095d19a` as reviewed on
2026-07-28. It is not a certification, compliance mapping, penetration-test report, or operating-
effectiveness assurance package; each chapter labels evidence availability and known gaps.

Supporting notes:

- [Architecture Decision Records](docs/adr/README.md) — repository design decisions and their code-level scope.
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
- A GPU with current drivers is optional; the app self-provisions its llama.cpp runtime and GGUF models at first run. (Neither Docker nor Ollama is required to build or run the engine — llama.cpp is the local runtime and inference needs only a driver; see [docs/wiki/03-local-runtime-and-providers.md](docs/wiki/03-local-runtime-and-providers.md).)
- Docker: **not required by default.** The container provider for **Development Mode only** ([ADR 0004](docs/adr/0004-development-mode-container-execution-docker-stopgap.md)) has shipped, but it is **opt-in and off unless you configure it**: it activates only when you set `Development:Sandbox:Provider=docker`, and the shipped `appsettings.json` leaves that key unset. On a node that does set it, Development Mode needs a running daemon, plus its data root inside the WSL2 filesystem on Windows; the real-daemon integration tests need one too (without a daemon they report as blocked or skipped-with-reason, never as a pass). Nothing else in the app gains a Docker dependency. See [Development Mode container implementation status](docs/roadmaps/development-mode-container-status.md).

### Common commands

From the repository root:

```bash
scripts/with-build-lock.sh -- dotnet restore XE-Local-AI-Engine.slnx
scripts/with-build-lock.sh -- dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-restore
scripts/with-build-lock.sh -- scripts/assembly-guard.sh guard --test-bins -- \
  dotnet test XE-Local-AI-Engine.slnx --configuration Release --no-build --max-parallel-test-modules 1
```

The lock prevents cooperating builds from rewriting test assemblies mid-run; the assembly guard
detects an unwrapped concurrent build. Exit `69` means the lock was not acquired and nothing ran.
Exit `75` means the result was **CONTAMINATED and void**—rerun it rather than treating it as red or
green.

For the React client:

```bash
cd XE-Local-AI-Engine.Client.React
pnpm install --frozen-lockfile
pnpm run lint
pnpm test
pnpm run build
```

After any backend contract change, run `pnpm openapi:check` from `XE-Local-AI-Engine.Client.React/` — it
regenerates the hey-api client and fails on drift. See [AGENTS.md](AGENTS.md#validation) for the full
validation command set, including analyzer requirements, build-lock/assembly-guard usage, and the
backend/frontend test suites.

E2E validation is ask-gated because it may require browser/runtime setup:

```bash
scripts/run-e2e-local.sh
```

## Aspire modes

Use Aspire for local development and integration checks.

```bash
scripts/dev-start.sh          # always --isolated and scoped to this worktree's AppHost
scripts/dev-status.sh         # filtered status; secrets and dashboard tokens are omitted
scripts/dev-stop.sh           # stops only this worktree's registered AppHost

# Bounded integration smoke; refuses to reuse an existing instance and always cleans up its own.
scripts/aspire-readiness-smoke.sh
```

These wrappers make parallel worktrees safe. Do not use `aspire stop --all`; it crosses checkout
boundaries. See [`scripts/README-dev-stop.md`](scripts/README-dev-stop.md) for the Aspire 13.4
fallback and cleanup contract.

`dev-start.sh` also owns the node operator secret. On first use it mints a per-checkout, owner-only
`XE-Local-AI-Engine.AppHost/.data/node.key` (never tracked) and passes it to Aspire's required
`node-sqlite-key` parameter; later runs reuse it, so encrypted dev data stays readable. If it mints a
key next to dev data written under a *different* secret, it says so and names what to delete — that
data cannot be decrypted and the node will otherwise crash on the first read.

## Self-contained desktop run

The host can be published as a **single self-contained executable** (the .NET runtime is bundled — no prerequisite
install) that runs as a "double-click" desktop app: a console window opens showing live logs, the default browser opens
on the running site, and **closing the console window shuts the whole app down** — including the spawned `llama-server`
child, so there is no orphan process. Closing the browser does *not* stop the app.

Desktop mode is **opt-in** via the launcher (env `XE_LAUNCH_MODE=desktop` or the `--desktop` flag); headless, Aspire,
and CI runs are unaffected. In desktop mode the host binds **HTTP on a free loopback port** (`127.0.0.1`) and skips the
HTTPS-redirect/HSTS pipeline (traffic never leaves the loopback adapter).

### Publish

Build the React app first; the publish target rejects a missing `dist/index.html`:

```bash
# Web assets (once before either RID)
(
  cd XE-Local-AI-Engine.Client.React
  pnpm install --frozen-lockfile
  pnpm run build
)

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
the canonical build, validation, packaging, and tester-upload path. It became canonical in `0.1.0-rc.4.0`;
earlier tester releases predate the script and must not be treated as evidence that they passed its current gates.
`publish/package-rc.sh` is a separate, much simpler manual portable-zip packager: a bash script you run on Linux/WSL,
producing a plain self-contained zip with **no Velopack metadata and therefore no self-update**. It builds **both**
`linux-x64` and `win-x64` by default (`--rid <rid>` for one).

> **A `win-x64` zip from `package-rc.sh` is cross-built on Linux.** Smoke-test it on real Windows before handing it to
> anyone — native-library self-extraction, console-close child cleanup, and browser auto-open cannot be verified
> off-Windows. The same applies to the two desktop invariants below.

> The no-orphan design (terminal/console close reaps `llama-server`) and the Windows Job Object path require
> real-desktop verification with a model loaded; they cannot be exercised in WSL2 or on a headless runner. This
> baseline documentation review does not include or assert availability of the matching smoke-test transcript.

## RC readiness status

Do not mark release or documentation work complete until matching validation evidence is available.
The checklist below defines required release evidence; its presence here does not assert that the
evidence was produced, retained, or made available for the documentation baseline.

Required evidence includes:

- the canonical packager's frontend, backend, vulnerability, and package-gate transcript,
- a clean default `scripts/lint-release-scripts.sh` result, including its mandatory Pester suite,
- a non-vacuous Playwright E2E run (`scripts/run-e2e-local.sh`) with no exit-75 contamination,
- a passing live GPU smoke run (`scripts/run-gpu-smoke-local.sh`) on a GPU box — the only gate that
  proves the GPU did the work, since a CPU fallback answers correctly, just slowly; treat exit 5 as an
  infrastructure abort where nothing was judged, not a product failure,
- generated schema/sample-manifest validation, including a clean `openapi:check`,
- pinned runtime binary and package checksums,
- the matching `v<version>` source tag on the exact packaged commit,
- a real-Windows smoke-test transcript for the exact generated `Portable.zip`,
- the generated five-asset SHA-256 manifest, printed Portable hash, pushed source-tag verification,
  and successful verification of all five remote assets during `-PublishDraft`, and
- confirmation that the unchanged draft was published in the tester repository.

Run `scripts/lint-release-scripts.sh`. **The Pester suite is part of that default run, not an add-on** —
`--pester` only requests it explicitly, and a missing Pester module is a hard failure, never a silent skip
(a skipped test suite must never read as a pass). See [Testing & Validation](docs/wiki/13-testing-and-validation.md)
and [the release guide](publish/README.md) for the full sequence.

Standalone OS-package distribution (MSI/deb/rpm) is deferred: under the runtime re-architecture the app self-provisions its llama.cpp runtime and GGUF models at first run, so there is no installer bundle to validate. A future packaging effort would be its own plan.
