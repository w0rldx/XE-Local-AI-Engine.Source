# XE Local AI Engine

XE Local AI Engine runs AI workloads locally on one machine. A single ASP.NET Core process serves the React management
UI, exposes loopback-only endpoints under `/api/local/v1` plus SignalR hubs, persists to SQLite with per-column
encryption, and supervises the `llama-server`, `sd-server`, and `whisper-server` child processes that do the inference.
The current source version is
`1.0.0-rc.2`, composed in [`eng/ReleaseVersion.props`](eng/ReleaseVersion.props).

> **Just want to install and use the app?** Start with the **[User Guide](docs/user-guide/README.md)** — download,
> install (Windows & Linux), first run, troubleshooting, and privacy, all in plain language. App downloads are on the
> [Releases](https://github.com/w0rldx/XE-Local-AI-Engine.Source/releases) page.

Official binaries are portable-only and self-updating: a Velopack `Portable.zip` on Windows and an AppImage on Linux.
They are unsigned, so verify `CHECKSUMS.sha256` before running them.

> **Installing on behalf of an AI agent?** An external agent can install, set up, start and connect to this engine
> without a browser. Set `XE_ADMIN_EMAIL` and `XE_ADMIN_PASSWORD` first, and save the one-time `XE_MCP_KEY=` and
> `XE_RECOVERY_CODE=` values it prints. Details: the [Agentic Support install guide](docs/agentic-support/agent-install.md).
>
> ```bash
> curl -fsSL https://raw.githubusercontent.com/w0rldx/XE-Local-AI-Engine.Source/main/install.sh | \
>   bash -s -- --setup --start --install-skill
> # PowerShell: set XE_ADMIN_EMAIL/XE_ADMIN_PASSWORD plus XE_SETUP=1, XE_START=1, XE_INSTALL_SKILL=1, then:
> # irm https://raw.githubusercontent.com/w0rldx/XE-Local-AI-Engine.Source/main/install.ps1 | iex
> ```

## What ships from this repo

| Area | Features |
| --- | --- |
| **Core** | [Chat](docs/wiki/05-chat.md) · [agents and work sessions](docs/wiki/04-agent-mode.md) · [scheduler](docs/wiki/06-scheduler.md) · [Model Advisor](docs/wiki/07-model-fit.md) · [knowledge base](docs/wiki/15-knowledge-base.md) · [image generation and editing](docs/wiki/14-image-generation.md) · [audio transcription](docs/wiki/24-audio-transcription.md) |
| **Developer** | [Development Mode](docs/adr/0004-development-mode-container-execution-docker-stopgap.md) · [Dev Workflows](docs/wiki/25-dev-workflows.md) · [Graph Workflows](docs/wiki/21-graph-workflows.md) · [compute tools](docs/wiki/19-compute-tools.md) · [training](docs/wiki/18-training.md) · [benchmarks](docs/wiki/20-benchmarks.md) · [custom tools](docs/wiki/04-agent-mode.md) · MCP client and server ([Agentic Support](#agentic-support)) · [web search and fetch](docs/adr/0017-web-access-tools-and-graph-allowlist.md) |
| **Platform** | [desktop shell](docs/adr/0013-native-desktop-shell.md) · [update channels](docs/adr/0014-update-channels-and-development-builds.md) · [vault key custody](docs/adr/0018-local-vault-passphrase-wrapped-node-key.md) · [External Apps](docs/wiki/23-external-apps.md) |
| **Runtimes** | llama.cpp by default, Ollama opt-in, stable-diffusion.cpp, whisper.cpp, or a cloud provider. See [Local runtime & providers](docs/wiki/03-local-runtime-and-providers.md). |

Feature switches are node settings, edited under **Settings → Node Settings → General → Features** and listed in
[wiki 08](docs/wiki/08-data-and-persistence.md). Dev Workflows, compute tools, custom tools and web access ship off,
and the External Apps catalog ships empty. The project layout is in [wiki 02](docs/wiki/02-project-layout.md).

## Agentic Support

A same-machine external agent can install, configure, start and operate the node through `install.sh` /
`install.ps1` and an inbound MCP server at `/api/local/v1/mcp/server`. An `agentic` MCP key is operator-equivalent
only for its enumerated MCP tools: it grants no Operator role or JWT and no arbitrary REST access. The listener stays
loopback-only. See the [install guide](docs/agentic-support/agent-install.md), the
[MCP client runbook](docs/runbooks/connect-an-mcp-client-runbook.md), the shipped
[external-agent skill](skills/xe-local-ai-engine/SKILL.md) and [ADR 0006](docs/adr/0006-agentic-trust-mcp-key-scopes-and-auto-approval.md).

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
- **[Troubleshooting](docs/troubleshooting.md)** — operator and developer page: data directory layout, logging switches, support bundle, unattended starts; end users start at the [FAQ](docs/user-guide/docs/faq.md).
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

Linux ships a self-contained AppImage and Windows a framework-dependent `Portable.zip` that needs ASP.NET Core 10.0.12+.
Both open the native desktop shell by default, without a console; the engine log is in `<data dir>/logs`. `--debug`
keeps a console streaming that log, and CLI modes (`--browser`, `--headless`, `--mcp-only`, `--status`, `--setup`, …)
keep theirs ([wiki 11 §5](docs/wiki/11-hosting-and-deployment.md)). Run one instance per user-data directory — a second
races on the SQLite database. The tag-triggered [`release.yml`](.github/workflows/release.yml) is the only release path.

Maintainer mechanics, artifact contracts and RC evidence live in [`publish/README.md`](publish/README.md); the
update-channel story in [`docs/velopack-release-install-guide.md`](docs/velopack-release-install-guide.md); the
publication gate in [`docs/release-publication-checklist.md`](docs/release-publication-checklist.md).

## License and conduct

XE Local AI Engine is licensed under **Apache-2.0**. See [LICENSE](LICENSE) and [NOTICE](NOTICE). Participation is
governed by the [Code of Conduct](CODE_OF_CONDUCT.md); contribution rules are in [CONTRIBUTING.md](CONTRIBUTING.md).
