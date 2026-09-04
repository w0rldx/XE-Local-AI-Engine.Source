---
name: xe-local-ai-engine
description: >-
  Installs, sets up, starts, connects to, and delegates work to a local XE Local AI Engine node — an
  all-in-one local AI runtime (chat, agents, RAG, image generation, training) running on this same
  machine. USE FOR: running the repo-root install.sh/install.ps1 one-liners, unattended --setup,
  starting --mcp-only mode, minting an MCP key, registering this engine as an MCP server in this
  client, calling get_status/list_models/start_model_pull/set_default_model/run_agent/
  start_agent_run over MCP, or troubleshooting a stuck local install/connection. DO NOT USE FOR: tasks
  this agent's own filesystem or shell tools already do better, driving the engine's browser UI, or
  calling any REST endpoint outside the documented /api/local/v1 surface. INVOKES: the repo-root
  installer scripts, the engine binary's --setup/--mcp-only/--status flags, its inbound MCP server at
  /api/local/v1/mcp/server (xemcp_-prefixed bearer key, no OAuth), and its loopback-only REST API
  (see references/rest-api.md).
license: Apache-2.0
allowed-tools: Bash(curl:*) Bash(./install.sh:*) Bash(pwsh:*) Bash(powershell:*) Read WebFetch
metadata:
  app-version-min: "1.0.0-rc.1"
---

# XE Local AI Engine

## What this is

Use the engine's external-agent skill when you need to install, configure, start, connect to, or
delegate work to an XE Local AI Engine node on the same machine. It is different from the engine's
in-app agent skills, which are imported into saved agents and run inside the node.

Do not use this workflow when your own filesystem or shell tools are the more direct option, to
drive the browser UI, or to guess at undocumented endpoints. The `allowed-tools` frontmatter is a
client convenience that can pre-approve tool invocation; it is not a sandbox or an authorization
boundary.

## Prerequisites

| Requirement | Notes |
|---|---|
| Platform | Windows x64 or Linux x64. macOS and ARM builds are not shipped. |
| Disk | Allow roughly 5–30 GB for the application, runtime, and local models. |
| Windows | Install the ASP.NET Core Runtime 10.0.11 or a newer .NET 10 servicing patch. |
| Linux | FUSE is preferred for AppImage mounting; extraction mode is available when FUSE is unavailable. |

## Install

On Linux, run the installer through Bash:

```bash
curl -fsSL https://raw.githubusercontent.com/w0rldx/XE-Local-AI-Engine.Source/main/install.sh | bash
```

Pass installer arguments with `bash -s --`, for example:

```bash
export XE_ADMIN_EMAIL='admin@example.test'
export XE_ADMIN_PASSWORD='<secret-from-your-store>'
curl -fsSL https://raw.githubusercontent.com/w0rldx/XE-Local-AI-Engine.Source/main/install.sh | \
  bash -s -- --setup --start --install-skill
```

On Windows PowerShell:

```powershell
irm https://raw.githubusercontent.com/w0rldx/XE-Local-AI-Engine.Source/main/install.ps1 | iex
```

A piped installer cannot prompt or bind PowerShell parameters. Set `XE_SETUP=1` plus
`XE_ADMIN_EMAIL`/`XE_ADMIN_PASSWORD` for piped setup; `XE_PRE=1`, `XE_VERSION=<tag>`, and
`XE_AUTOSTART=1` are the piped equivalents of `-Pre`, `-Version`, and `-Autostart`. Download and
invoke `install.ps1` directly when parameter binding or interactive prompts are required.

The scripts install published assets: stable by default, prereleases with `--pre`/`-Pre`, or an
exact tag with `--version`/`-Version`. Until a release containing Agentic Support exists, post-install
flags can fail against an older asset even though the raw script itself is current. Every installed
artifact is checked against mandatory `CHECKSUMS.sha256`.

`npx skills add w0rldx/XE-Local-AI-Engine.Source --skill xe-local-ai-engine` installs only the
engine's external-agent skill; it does not install the engine. See
[setup and connection reference](references/setup-and-connect.md) for install locations and client
configuration.

## Set up unattended

Keep the administrator password out of process arguments. Set `XE_ADMIN_EMAIL` and
`XE_ADMIN_PASSWORD`, then invoke the installed engine with `--setup`. If the integration requires
stdin instead, use `--admin-password-stdin`; never put a real password in a committed command or a
literal `--admin-password <value>` argument.

When the node is already running, the loopback REST alternative is `POST auth/setup`, followed by
`POST auth/login` and `POST mcp/server-key`. The MCP key is returned once and cannot be recovered
later. Store it in a secret manager. See [REST API reference](references/rest-api.md).

## Start

Use `--mcp-only` for local operation without automatically opening a browser. The React UI remains
available at the announced URL. `--no-browser` provides the same browser suppression for a normal
desktop launch.

Wait for the single readiness line:

```text
XE_READY=1 XE_VERSION=<semver> XE_URL=http://127.0.0.1:<port> XE_MCP_URL=<XE_URL>/api/local/v1/mcp/server XE_DATA_DIR=<path>
```

For a later machine-readable check, use `--status --json`. The inbound MCP server remains
loopback-only; remote access requires an operator-owned tunnel.

## Connect

Authenticate the Streamable HTTP endpoint with `Authorization: Bearer <xemcp_...>`. Never commit the
real key. Client-specific examples for Claude Code, Codex CLI, Cursor, VS Code/GitHub Copilot,
OpenCode, and Gemini CLI are in [setup and connection reference](references/setup-and-connect.md).

Mint `delegate` for the eight shared delegation tools, or `agentic` for all 23 tools (the eight
shared tools plus 15 administration tools). The agentic credential is trusted operator-equivalent
only for that enumerated inbound MCP surface: it is not an Operator JWT, grants no arbitrary REST
access, and does not relax the loopback listener. It auto-approves approval-required tools for the
root saved-agent run only after a strict metadata-only audit write; audit failure blocks invocation.
Spawned children retain the ordinary curated tool surface and do not inherit this elevation.

## Delegate work well

1. Call `list_agents` and `list_models` to select a saved persona or installed local model.
2. For repository context, call `list_workspaces` and pass only its opaque workspace id. The seeded
   Coder is read-only and never receives the host path.
3. Use `run_agent` for bounded synchronous work. A cold local-model load can take several minutes,
   so configure a generous client timeout.
4. Prefer `start_agent_run` for durable background work. Generate a fresh UUID `request_id`, then
   poll with `get_agent_run`; use `cancel_agent_run` only when the work is no longer needed.
5. Use `list_agent_runs` to recover lifecycle metadata after a disconnect or restart.

For an agentic key, begin with `get_status`. Acquire a runtime with
`start_runtime_acquisition`/`get_runtime_acquisition` when needed; acquire a model with
`start_model_pull`/`get_model_pull`, then `set_default_model` before the first run. Do not enable
custom tools through settings: `CustomToolsEnabled` is intentionally outside the 17-field whitelist.

Results are capped at 24,000 characters and report `result_truncated` when clipped. Durable result
payloads expire after 24 hours. The complete current tool contract is in
[MCP tools reference](references/mcp-tools.md).

## Troubleshooting

Authentication failures, timeouts, lifecycle conflicts, workspace errors, AppImage/FUSE issues,
and Windows runtime prerequisites are covered in the
[troubleshooting reference](references/troubleshooting.md).

## Reference index

- [Setup and connect](references/setup-and-connect.md) — install and MCP client configuration.
- [MCP tools](references/mcp-tools.md) — exact registered inbound tool names and usage patterns.
- [REST API](references/rest-api.md) — authentication boundaries and the committed OpenAPI contract.
- [Troubleshooting](references/troubleshooting.md) — stable symptoms, causes, and recoveries.
