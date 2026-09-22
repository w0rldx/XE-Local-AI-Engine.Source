# XE Local AI Engine

XE Local AI Engine runs AI workloads locally on one machine. A single ASP.NET Core process serves the React management
UI, exposes loopback-only endpoints under `/api/local/v1` plus SignalR hubs, persists to SQLite with per-column
encryption, and supervises the `llama-server`, `sd-server`, and `whisper-server` child processes that do the inference.
The current source version is
`1.0.0-rc.2`, composed in [`eng/ReleaseVersion.props`](eng/ReleaseVersion.props).

> **Just want to install and use the app?** Start with the **[User Guide](docs/user-guide/README.md)** — download,
> install (Windows & Linux), first run, troubleshooting, and privacy, all in plain language. App downloads are on the
> [Releases](https://github.com/w0rldx/XE-Local-AI-Engine.Source/releases) page.

Official binaries are portable-only: Windows ships a Velopack `Portable.zip` with no `Setup.exe`, and Linux ships a
Velopack AppImage. Both formats self-update. Release assets are unsigned because no signing certificate exists — verify
`CHECKSUMS.sha256` and review `RELEASE-MANIFEST.json` / `RELEASE.spdx.json` before running them.

> **Installing on behalf of an AI agent?** An external agent (Claude Code, Codex CLI, Cursor, and others) can install,
> set up, start, and connect to this engine with no human in the browser:
>
> ```bash
> curl -fsSL https://raw.githubusercontent.com/w0rldx/XE-Local-AI-Engine.Source/main/install.sh | \
>   bash -s -- --setup --start --install-skill
> # PowerShell: set XE_ADMIN_EMAIL/XE_ADMIN_PASSWORD plus XE_SETUP=1, XE_START=1, XE_INSTALL_SKILL=1, then:
> # irm https://raw.githubusercontent.com/w0rldx/XE-Local-AI-Engine.Source/main/install.ps1 | iex
> ```
>
> A piped install has no usable prompt input, so set `XE_ADMIN_EMAIL` and `XE_ADMIN_PASSWORD` before requesting setup.
> On success it prints the node's ready line and a one-time `XE_MCP_KEY=` value — save it, it is never shown again.
> See the [Agentic Support install guide](docs/agentic-support/agent-install.md), the
> [MCP client runbook](docs/runbooks/connect-an-mcp-client-runbook.md), and the shipped
> [external-agent skill](skills/xe-local-ai-engine/SKILL.md).

## What ships from this repo

- **Node Web Server** (`XE-Local-AI-Engine.Client`) — the host process: FastEndpoints, the SignalR hubs, the
  composition root and the SPA. See [API & hubs](docs/wiki/09-api-and-hubs.md).
- **React management UI** (`XE-Local-AI-Engine.Client.React`) — the node-local browser UI for every feature below.
  See [React client](docs/wiki/10-react-client.md).
- **Runtimes and providers** — llama.cpp is the default local runtime, supervised as `llama-server` children on a
  loopback port range; Ollama is an opt-in secondary. The node can acquire prebuilt binaries, build llama.cpp and
  stable-diffusion.cpp from source in-app, or route turns to a cloud provider. See
  [Local runtime & providers](docs/wiki/03-local-runtime-and-providers.md).
- **Chat** — streaming conversations with per-turn model and agent resolution, ordered reasoning/tool/answer parts,
  attachments, optional knowledge-base grounding, and content encrypted at rest. See [Chat](docs/wiki/05-chat.md).
- **Agent mode and work sessions** — per-agent definitions, a governed action playbook with an offline eval gate, and
  durable work sessions that survive a restart. See [Agent mode](docs/wiki/04-agent-mode.md).
- **Scheduler** — an in-process Quartz.NET scheduler for recurring, one-shot, and manual jobs, with run history,
  cancellation, and live updates. See [Scheduler](docs/wiki/06-scheduler.md).
- **Model-fit / Model Advisor** — profiles the local hardware, discovers candidate GGUF repos, estimates each model's
  memory footprint with an I/O-free formula, and ranks what fits. See [Model fit](docs/wiki/07-model-fit.md).
- **Image generation** — local text-to-image through stable-diffusion.cpp: one resident `sd-server` daemon per model,
  one job at a time, images encrypted at rest. See [Image generation](docs/wiki/14-image-generation.md).
- **Knowledge Base / RAG** — offline document ingestion and local embedding, retrieved by hybrid lexical plus semantic
  search with an optional reranker, offered to agents as a tool. See [Knowledge base](docs/wiki/15-knowledge-base.md).
- **Custom tools** — operator-authored HTTP fetches and host-program launches assignable to agents. The node-wide
  switch is **off by default**, every call is approval-wrapped, and secrets are encrypted at rest and masked on reads.
- **Development Mode** — a default-on, node-local coding workflow: engine-owned detached Git worktrees, deterministic
  validation, independent review, and an explicit hash-bound apply to the registered source repository. Generated code
  runs as the host user, so the process sandbox is a containment aid, not an OS security boundary. An opt-in Docker
  provider behind the same seam activates only on `Development:Sandbox:Provider=docker`, which the shipped
  configuration does not set. See [ADR 0004](docs/adr/0004-development-mode-container-execution-docker-stopgap.md) and
  its [status record](docs/roadmaps/development-mode-container-status.md).
- **Dev Workflows** — Dev Mode's graph runtime for multi-step coding runs, with pause, human gates and a bounded
  cross-node fix loop. **Off by default**. See [the divergence register](docs/wiki/22-workflow-engines-divergence-register.md).
- **Graph Workflows** — the general-purpose operator-authored DAG engine (agent turns, tool calls, conditions, human
  pauses), executed from the database so a restart loses only in-flight work. See [the wiki](docs/wiki/21-graph-workflows.md).
- **Compute tools** — a sandboxed, offline `run_python` tool with no network, host filesystem, or conversation access.
  **Off by default** (`Compute:Enabled`) and profile-opt-in even when on. See [Compute tools](docs/wiki/19-compute-tools.md).
- **Training** — node-local fine-tuning: dataset generation, runs, GGUF export behind a smoke gate, evaluation and
  comparison, in a `uv`-managed Python runtime holding an exclusive node-wide GPU admission gate. See
  [Training](docs/wiki/18-training.md) and [ADR 0005](docs/adr/0005-training-runtime-python-exclusivity-and-project-placement.md).
- **Benchmarks** — frozen task suites run across many models and settings combinations, ranked on quality only, with
  every display axis withheld once it stops being comparable. See [Benchmarks](docs/wiki/20-benchmarks.md).
- **External Apps** — curated containerised applications installed from an XE-authored catalog, pinned by digest and
  run under an engine-owned policy with loopback-only published ports. A Docker daemon is required here and nowhere
  else, and the shipped catalog is **empty today**, so a node installs nothing until one is published to it. See
  [the wiki](docs/wiki/23-external-apps.md), [ADR 0010](docs/adr/0010-external-apps-container-execution.md) and its
  [status record](docs/roadmaps/external-apps-status.md).
- **MCP** — outbound: registered MCP servers whose live tool snapshots reach agents through the local tool registry.
  Inbound: a server at `/api/local/v1/mcp/server` plus a shipped skill, so an external agent can drive this node
  ([Agentic Support](#agentic-support)).
- **Audio transcription** — local speech-to-text through whisper.cpp, supervised as a `whisper-server` child process
  the way the other runtimes are. Audio is never persisted; transcript rows are encrypted. See
  [Audio transcription](docs/wiki/24-audio-transcription.md).
- **Tests and fixtures** — backend, persistence and agent test projects, shared host fixtures, an opt-in Playwright
  E2E harness, and in-memory fakes for Ollama and the Docker Engine API. See [Testing](docs/wiki/13-testing-and-validation.md).

## Agentic Support

Agentic Support lets a same-machine external agent install, configure, start and operate the node without a browser:

- repo-root `install.sh` and `install.ps1` resolve stable, prerelease, or pinned GitHub releases; verify the mandatory
  `CHECKSUMS.sha256`; install atomically; and optionally run `--setup`, `--start`, `--autostart`, `--install-skill`;
- `--setup`, `--mcp-key <delegate|agentic>`, and `--status --json` are one-shot engine commands; `--mcp-only` serves
  the normal local UI and API without opening a browser;
- the exact ready line plus a canonical `<data-dir>/ready.json` make the dynamic loopback port and PID discoverable
  without scraping logs;
- inbound MCP uses Streamable HTTP at `/api/local/v1/mcp/server`, never stdio. A `delegate` key sees the 8 shared
  agent-run tools; an `agentic` key additionally sees the admin tools enumerated in
  [`skills/xe-local-ai-engine/references/mcp-tools.md`](skills/xe-local-ai-engine/references/mcp-tools.md);
- `agentic` is trusted operator-equivalent only for that enumerated MCP surface. It grants no Operator role or JWT and
  no arbitrary REST access. Approval-required root calls are strictly audited before auto-approval, while spawned
  children keep their ordinary curated tools;
- the listener remains loopback-only. Remote use requires an operator-owned encrypted tunnel whose engine-side
  connection terminates on loopback; a routable bind or same-host reverse proxy is not a supported deployment.

The external-agent skill lives once at `skills/xe-local-ai-engine/`; `--install-skill` installs the version-matched
files to the user's agent skill roots rather than copying them into this repository. The trust decision is recorded in
[ADR 0006](docs/adr/0006-agentic-trust-mcp-key-scopes-and-auto-approval.md). Autostart is never on by default: it is an
explicit `--autostart`/`-Autostart` opt-in registering a current-user systemd service or Scheduled Task.

## Architecture rules

- The node opens no outbound control-plane connection; every egress traces to a feature an operator turned on.
- Cloud-provider credentials and external endpoint tokens stay local and must not be returned to
  the browser or written to logs/transcripts.
- Local admin endpoints must be loopback/local-only, authenticated, strict about `Host`/`Origin`, and secret-redacted.
- Installers and packaging must not create background autostart behavior unless explicitly opted in via `--autostart`
  (user-scope only); autostart is never the default.

## Documentation map

- **[User Guide](docs/user-guide/README.md)** — install, first run, troubleshooting and privacy, for non-developers.
- **[Developer Wiki](docs/wiki/Home.md)** — the code-grounded architecture reference. Start at `Home.md`.
- **[AGENTS.md](AGENTS.md)** — contributor rules, repository map, and the authoritative validation gate set;
  **[CONTRIBUTING.md](CONTRIBUTING.md)** — how to propose a change and what a PR must state.
- **[Architecture Decision Records](docs/adr/README.md)** — design decisions and their code-level scope.
- **[AI runtime developer notes](docs/ai-runtime.md)** — narrow AI-seam maintenance rules.
- **[Troubleshooting](docs/troubleshooting.md)** — symptom-first fixes for a node that will not start or behave.
- **[Audit index](docs/audits/README.md)** — the dated audits and reviews kept in this repository.
- **[Technical/Security Architecture Dossier](docs/audits/technical-security-architecture/README.md)** — a
  baseline-scoped external review of the implementation as of 2026-07-28. Its baseline hash belongs to the
  pre-consolidation history and does not resolve here, so read it as a dated snapshot, not a commit to check out.
- Component notes: [Node Web Server](XE-Local-AI-Engine.Client/README.md) and
  [React Client](XE-Local-AI-Engine.Client.React/README.md).

## Local development

Prerequisites: the .NET SDK pinned in [`global.json`](global.json); Node.js matching
`XE-Local-AI-Engine.Client.React/package.json`; pnpm; Python 3 with `uv`; the Aspire CLI; and on Linux/WSL `setsid`. A
GPU is optional and Docker is not required by default — the app self-provisions llama.cpp and GGUF models on first run.

```bash
scripts/dev-start.sh     # start the isolated Aspire AppHost for this checkout
scripts/dev-status.sh    # resource states and endpoint URLs (--json); the port changes on every restart
scripts/dev-stop.sh      # the only sanctioned stop path — never `aspire stop --all`
```

These wrappers keep parallel checkouts from killing each other's instances; the cleanup contract is in
[`scripts/README-dev-stop.md`](scripts/README-dev-stop.md). `dev-start.sh` also mints and reuses a per-checkout,
never-tracked `XE-Local-AI-Engine.AppHost/.data/node.key`, so encrypted dev data stays readable. If it mints a key
next to data written under a different secret, it says so and names what to delete.

```bash
scripts/run-backend-tests.sh              # the backend gate: one Release build, every enrolled test project

cd XE-Local-AI-Engine.Client.React
pnpm run acceptance                       # validate + coverage thresholds + tooling tests + production bundle
```

[AGENTS.md §Validation](AGENTS.md#validation) is authoritative for the full gate set, including analyzer requirements,
`pnpm run openapi:check` after a backend contract change, the Python scope, and the opt-in live runners.

## Publishing and releases

The desktop package is deliberately asymmetric: Linux ships a self-contained single-file AppImage, Windows a
framework-dependent Velopack `Portable.zip` needing the x64 ASP.NET Core Runtime 10.0.11 or a newer .NET 10 servicing
patch. Desktop mode is opt-in through the launcher (`XE_LAUNCH_MODE=desktop` or `--desktop`); headless, Aspire and CI
runs are unaffected. A console window opens with live logs, and closing it shuts the whole app down including the
supervised child processes. Run one instance per user-data directory — a second races on the SQLite database. The
tag-triggered [`release.yml`](.github/workflows/release.yml) is the only official release path.

Maintainer mechanics, artifact contracts and RC evidence live in [`publish/README.md`](publish/README.md); the
update-channel story in [`docs/velopack-release-install-guide.md`](docs/velopack-release-install-guide.md); the
publication gate in [`docs/release-publication-checklist.md`](docs/release-publication-checklist.md).

## License

XE Local AI Engine is licensed under **Apache-2.0**. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
