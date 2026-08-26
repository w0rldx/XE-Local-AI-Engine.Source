# XE Local AI Engine

XE Local AI Engine is the node-side runtime for running local AI workloads while preserving the existing C0re platform contract. The Node Web Server hosts the React management UI, owns the platform
`WorkerHub` connection, and supervises node-owned `llama-server` host child processes for local inference.

The current source version is `1.0.0-rc.2`, composed in `eng/ReleaseVersion.props`. Release documentation and
validation evidence live in this repository and must stay current with runtime behavior.

> **Just want to install and use the app?** Start with the **[User Guide](docs/user-guide/README.md)** — download,
> install (Windows & Linux), first run, troubleshooting, and privacy, all in plain language. App downloads are on the
> [Releases](https://github.com/w0rldx/XE-Local-AI-Engine.Source/releases) page.

Official binaries are portable-only: Windows ships a Velopack `Portable.zip` with no `Setup.exe`, and Linux ships a
Velopack AppImage rather than a ZIP. Both formats are self-updating. Release assets are currently unsigned because no
signing certificate exists; verify `CHECKSUMS.sha256` and review `RELEASE-MANIFEST.json` / `RELEASE.spdx.json` before
running them. Signing is not configured.

> **Installing on behalf of an AI agent?** An external agent (Claude Code, Codex CLI, Cursor, and
> others) can install, set up, start, and connect to this engine with no human in the browser:
>
> ```bash
> curl -fsSL https://raw.githubusercontent.com/w0rldx/XE-Local-AI-Engine.Source/main/install.sh | \
>   bash -s -- --setup --start --install-skill
> # PowerShell: set XE_ADMIN_EMAIL/XE_ADMIN_PASSWORD plus XE_SETUP=1, XE_START=1,
> # and XE_INSTALL_SKILL=1, then:
> # irm https://raw.githubusercontent.com/w0rldx/XE-Local-AI-Engine.Source/main/install.ps1 | iex
> # Piped selection flags use XE_PRE=1, XE_VERSION=<tag>, and XE_AUTOSTART=1;
> # download install.ps1 and invoke it directly to use -Pre/-Version/-Autostart.
> ```
>
> A piped Bash/PowerShell install has no usable prompt input, so set `XE_ADMIN_EMAIL` and
> `XE_ADMIN_PASSWORD` before requesting setup. For interactive prompts, download the installer and
> execute it directly in a terminal. On success it prints the
> node's ready line and a one-time `XE_MCP_KEY=` value — save it, because it is never shown again.
> Verify with `--status --json`. See the complete
> [Agentic Support install guide](docs/agentic-support/agent-install.md), the
> [six-client MCP runbook](docs/runbooks/connect-an-mcp-client-runbook.md), and the shipped
> [external-agent skill](skills/xe-local-ai-engine/SKILL.md).

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
- **Custom tools** — operator-authored HTTP fetches and direct host-program launches that can be assigned to agents. The node-wide feature switch is off by default; the built-in authoring UI initializes new definitions as disabled, while the API persists an acknowledged caller's requested enablement. Every call remains approval-wrapped: a fixed tool may reuse an explicit, version-bound session approval, while a parameterized tool re-prompts for every model-selected argument set. Authoring validates model-visible schemas, executable paths, template placeholders, and HTTP host allow-lists; secret headers and environment values are encrypted at rest and masked on reads (`Services/CustomTools`, `Endpoints/CustomTools/V1`, `src/features/customTools`).
- **Development Mode** — a default-on, node-local coding workflow with engine-owned detached Git worktrees, deterministic validation, independent review, hash-bound evidence, and explicit final host apply.
  The operator registers a trusted local Git repository once, then selects it by an opaque ID and alias; the host path stays internal to the node. The agent works in a managed worktree outside the selected source
  repository, and only a reviewed apply whose base and evidence hashes still match may change that source. Generated source, MSBuild targets, source generators, and tests execute as the host user with the host's
  filesystem and network access. The Process sandbox and Agent Home controls constrain application-mediated paths and bytes; they are not an operating-system security boundary. Set
  `Development:Enabled=false` as an emergency switch when that execution posture is not intended. MXC is not integrated, and the current provider seams do not constitute an MXC security boundary.
  [ADR 0004](docs/adr/0004-development-mode-container-execution-docker-stopgap.md) (accepted 2026-07-29) defines an opt-in **Docker container provider** behind the same provider seam as a
  bounded stopgap that leaves the MXC provider seam open. That provider has **shipped and is opt-in**: set `Development:Sandbox:Provider=docker` to select it. Leave it unset — as the shipped configuration does — and Development Mode keeps running on
  the process provider exactly as described above. On a node that *does* select it, **a running Docker daemon is a hard requirement** — there is deliberately no unisolated fallback, so a machine without one gets no
  Development Mode rather than a quietly weaker one. Docker stays scoped to this feature: chat, embeddings, model acquisition and image generation never require it. See
  [Development Mode container implementation status](docs/roadmaps/development-mode-container-status.md) for the maintained record of what is implemented.
- **Training** — node-local fine-tuning of a downloaded Hugging Face base checkpoint: dataset definition and generation, training runs, GGUF export with a smoke gate before promotion into the local model
  store, evaluation, and run-to-run comparison. Training semantics live in a `uv`-managed Python runtime provisioned from a repo-committed lockfile rather than the host interpreter, and a single node-wide
  admission gate (`IGpuWorkGate`) makes a run, an evaluation or an export an **exclusive** GPU tenant, so chat, embeddings, benchmarks, dataset generation and image jobs cannot overlap it. The nav group ships
  **on by default** (`XE-Local-AI-Engine.Providers.Training`, `Services/Training`, `Endpoints/Training`, `tools/training`, `src/features/training`). See
  [ADR 0005](docs/adr/0005-training-runtime-python-exclusivity-and-project-placement.md).
- **MCP tool extensibility** — registered MCP servers whose live tool snapshots are offered to agents through the local tool registry (`Services/Mcp`, `src/features/mcp`) — plus an inbound MCP server (`/api/local/v1/mcp/server`) and skill (`skills/xe-local-ai-engine/`) so external agents can drive this node; see [Agentic Support](#agentic-support).
- **Tests and fixtures** — backend/client persistence tests, integration-style tests, E2E harness, and FakeOllama in-process test server.

## Agentic Support

Agentic Support lets a same-machine external agent install, configure, start, and operate the node
without browser interaction:

- repo-root `install.sh` and `install.ps1` resolve stable, prerelease, or pinned GitHub releases;
  verify the mandatory `CHECKSUMS.sha256`; install atomically; and optionally run `--setup`,
  `--start`, `--autostart`, and `--install-skill`;
- `--setup`, `--mcp-key <delegate|agentic>`, and `--status --json` are one-shot engine commands;
  `--mcp-only` serves the normal local UI/API without opening a browser;
- the exact ready line plus canonical `<data-dir>/ready.json` make the dynamic loopback port and PID
  discoverable without scraping logs;
- inbound MCP uses Streamable HTTP at `/api/local/v1/mcp/server`, never stdio. A `delegate` key sees
  exactly 8 shared agent-run tools; an `agentic` key sees all 23 tools (8 shared plus 15 admin);
- `agentic` is trusted operator-equivalent only for that enumerated MCP surface. It grants no
  Operator role/JWT or arbitrary REST access. Approval-required root calls are strictly audited
  before auto-approval, while spawned children keep their ordinary curated tools;
- the listener remains loopback-only. Remote use requires an operator-owned encrypted tunnel whose
  engine-side connection terminates on loopback; a routable bind or same-host reverse proxy is not a
  supported deployment.

The external-agent skill lives once at `skills/xe-local-ai-engine/`. `--install-skill` installs the
version-matched files to the user's Claude and common agent skill roots; it does not create a second
copy inside this repository. Start with the
[Agentic Support install guide](docs/agentic-support/agent-install.md). The trust decision is recorded
in [ADR 0006](docs/adr/0006-agentic-trust-mcp-key-scopes-and-auto-approval.md).

Autostart is never enabled by default. It is an explicit `--autostart`/`-Autostart` opt-in that
registers a current-user systemd service on Linux or limited current-user Scheduled Task on Windows.

## Architecture rules

- Only the Node Web Server talks to the C0re platform over `WorkerHub`.
- Worker credentials, cloud-provider credentials, and external endpoint tokens stay local and must not be returned to the browser or written to logs/transcripts.
- Local admin endpoints must be loopback/local-only, authenticated, strict about `Host`/`Origin`, and secret-redacted.
- Installers and packaging must not create background autostart behavior unless
  explicitly opted in via `--autostart` (user-scope only); autostart is never the default.

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
2026-07-28. (That hash belongs to the pre-consolidation history and does not resolve in this repository, which is now
the single home for source and releases — see `docs/agent-knowledge.md`, "Consolidated to one repo." Read the dossier
as a dated snapshot, not as something you can `git checkout`.) It is not a certification, compliance mapping,
penetration-test report, or operating-effectiveness assurance package; each chapter labels evidence availability and
known gaps.

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
- Python 3 for repository validation and lifecycle scripts
- The Aspire CLI for AppHost development and readiness checks
- On Linux/WSL, `setsid` (normally provided by `util-linux`) for transactional `scripts/dev-start.sh` cleanup
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

That backend command is restated from [`AGENTS.md`](AGENTS.md#validation), which is authoritative for it and explains
why CI's per-project test loop differs. The lock prevents cooperating builds from rewriting test assemblies mid-run;
the assembly guard detects an unwrapped concurrent build. Exit `69` means the lock was not acquired and nothing ran.
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

## Desktop publishing

The desktop package is deliberately asymmetric. Linux remains a **self-contained single-file AppImage**. Windows is a
**framework-dependent Velopack Portable ZIP**: it contains a small C# launcher apphost plus the managed application DLL,
but no .NET runtime. Windows users install the x64 **ASP.NET Core Runtime 10.0.11 or a newer .NET 10 servicing patch**.
If the base .NET runtime is absent, Microsoft's apphost reports the missing framework; if ASP.NET Core is absent or too
old, the launcher prints the exact requirement and opens the official .NET 10 download page.

Both packages run as double-click desktop apps: a console window opens with live logs, the default browser opens on the
running site, and **closing the console window shuts the whole app down** — including the spawned `llama-server` child,
so there is no orphan process. Closing the browser does *not* stop the app.

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

# Linux: self-contained single-file payload
dotnet publish XE-Local-AI-Engine.Client -c Release -r linux-x64 -p:PublishProfile=linux-x64

# Windows: framework-dependent app plus the C# launcher, overlaid into one payload directory
WIN_OUT="$PWD/.tmp/publish/win-x64"
dotnet publish XE-Local-AI-Engine.Client -c Release -r win-x64 -p:PublishProfile=win-x64 --output "$WIN_OUT"
dotnet publish XE-Local-AI-Engine.WindowsLauncher -c Release -r win-x64 -p:PublishProfile=win-x64 --output "$WIN_OUT"
```

The Linux profile sets `SelfContained=true`, `PublishSingleFile=true`, and
`IncludeNativeLibrariesForSelfExtract=true`. The Windows application and launcher both set `SelfContained=false`; only
the launcher sets `UseAppHost=true`, producing the MIT-licensed Microsoft apphost while keeping `coreclr`, `hostfxr`,
and the runtime out of the artifact. Trimming stays **off** for the reflection-heavy application.

### Run

Start the matching published entry point:

- **Linux:** `publish/linux/run-xe-local-ai-engine.sh` — `exec`s the binary in the foreground so the terminal owns it
  (terminal close → `SIGHUP` → graceful shutdown).
- **Windows:** run `XE-Local-AI-Engine.WindowsLauncher.exe` from the combined payload. It validates the adjacent files
  and ASP.NET Core runtime, sets desktop mode, forwards Velopack arguments, launches the managed DLL, and propagates its
  exit code. `publish/windows/run-xe-local-ai-engine.cmd` is retained only for the deprecated self-contained manual flow.

See [`publish/README.md`](publish/README.md) for the expected layout. **Run one instance at a time** against the same
user-data directory — a second instance races on the SQLite database.

### Release

The tag-triggered **[`.github/workflows/release.yml`](.github/workflows/release.yml)** is the only official release
path. It rejects a tag/version/source mismatch, runs the shared validation workflow, lets the Windows and Linux matrix
jobs build and retain assets only, then splits publication into two protected write transactions. The serialized
`prepare-release-draft` job creates the draft, merges both Velopack channels, verifies the remote bytes, attaches
detached SPDX/release-manifest/checksum evidence, and re-verifies the complete remote draft after the
`open-source-release` environment authorizes repository write access. A separately approved `publish-release` job
re-verifies that same draft, promotes it without rebuilding or replacing any asset, and confirms both public feeds
anonymously.

Windows packing uses Velopack 1.2.0 with `--noInst`, producing a managed `Portable.zip` plus feed/full/delta assets and
no `Setup.exe`. Linux produces a managed AppImage. Public update checks require no GitHub device login or access token:
the `main` flavor follows stable releases, the `tester` flavor includes release candidates, and Velopack selects the
independent Windows/Linux OS channel from package metadata.

`publish/package-tester-win.ps1` and `publish/package-rc.sh` are **deprecated, reference-only** scripts. They describe
superseded manual distribution flows and are not publication alternatives. `scripts/lint-release-scripts.sh` still
analyzes them so retained reference code does not decay silently.

> **A `win-x64` zip from `package-rc.sh` is cross-built on Linux.** Smoke-test it on real Windows before handing it to
> anyone — native-library self-extraction, console-close child cleanup, and browser auto-open cannot be verified
> off-Windows. The same applies to the two desktop invariants below.

> The no-orphan design (terminal/console close reaps `llama-server`) and the Windows Job Object path require
> real-desktop verification with a model loaded; they cannot be exercised in WSL2 or on a headless runner. This
> baseline documentation review does not include or assert availability of the matching smoke-test transcript.

See [`docs/velopack-release-install-guide.md`](docs/velopack-release-install-guide.md) for the full release and
update-channel story.

## RC readiness status

Do not mark release or documentation work complete until matching validation evidence is available.
The checklist below defines required release evidence; its presence here does not assert that the
evidence was produced, retained, or made available for the documentation baseline.

Required evidence includes:

- the release workflow's (or, for a manual rehearsal, the deprecated packager's) frontend, backend, vulnerability, and package-gate transcript,
- a clean default `scripts/lint-release-scripts.sh` result, including its mandatory Pester suite,
- a non-vacuous Playwright E2E run (`scripts/run-e2e-local.sh`) with no exit-75 contamination,
- a passing live GPU smoke run (`scripts/run-gpu-smoke-local.sh`) on a GPU box — the only gate that
  proves the GPU did the work, since a CPU fallback answers correctly, just slowly; treat exit 5 as an
  infrastructure abort where nothing was judged, not a product failure,
- generated schema/sample-manifest validation, including a clean `openapi:check`,
- pinned runtime binary and package checksums,
- the matching `v<version>` source tag on the exact packaged commit,
- a real-Windows smoke-test transcript for the exact generated `Portable.zip` and a Linux AppImage smoke test,
- the generated release assets and their checksums, pushed source-tag verification, and
- confirmation that the release was published to this repository's GitHub Releases.

Run `scripts/lint-release-scripts.sh`. **The Pester suite is part of that default run, not an add-on** —
`--pester` only requests it explicitly, and a missing Pester module is a hard failure, never a silent skip
(a skipped test suite must never read as a pass). See [Testing & Validation](docs/wiki/13-testing-and-validation.md)
and [the release guide](publish/README.md) for the full sequence.

Standalone OS installers/packages (MSI/DEB/RPM) remain deferred. The official distribution is the Velopack-managed
Windows Portable ZIP and Linux AppImage.
A scripted convenience installer (`install.sh`/`install.ps1`, repo root) exists alongside these
formats for unattended and agent-driven installs; it produces the same portable artifacts above and is
not a new OS package format — see [Agentic Support](#agentic-support).

## License

XE Local AI Engine is licensed under **Apache-2.0**. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
