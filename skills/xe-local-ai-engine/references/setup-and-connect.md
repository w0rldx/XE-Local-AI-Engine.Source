# Set up and connect

## Install the engine's external-agent skill

Use one canonical source; do not add a symlink or duplicate inside this repository:

```bash
npx skills add w0rldx/XE-Local-AI-Engine.Source --skill xe-local-ai-engine
```

The repo-root installer also supports `--install-skill`, which atomically installs the
version-matched files to `~/.claude/skills/xe-local-ai-engine` and
`~/.agents/skills/xe-local-ai-engine`. A manual copy is acceptable when a client uses a different
skill directory.

## Choose the credential scope

| scope | visible tools | trust |
|---|---:|---|
| `delegate` | 8 | Shared discovery and agent-run tools. Ordinary saved agents are tool-less; the seeded Coder keeps only its three read-only workspace tools. |
| `agentic` | 23 | The same 8 tools plus 15 node-administration tools. Trusted operator-equivalent only for this enumerated MCP surface; not an Operator JWT or REST credential. |

Mint from the stopped engine with `--mcp-key delegate` or `--mcp-key agentic`. Minting replaces the
single existing key immediately. Capture the one `XE_MCP_KEY=` line; the plaintext cannot be read
again and must never be logged.

## Exact local CLI and readiness contracts

```text
--setup [--admin-email <email>] [--admin-password-stdin]
--mcp-key <delegate|agentic>
--status [--json]
--mcp-only [--port <1-65535>]
--desktop [--no-browser] [--port <1-65535>]
```

Automation passes the password through `XE_ADMIN_PASSWORD` or stdin, never
`--admin-password <value>`. Both local modes print exactly one raw line:

```text
XE_READY=1 XE_VERSION=<semver> XE_URL=http://127.0.0.1:<port> XE_MCP_URL=<XE_URL>/api/local/v1/mcp/server XE_DATA_DIR=<path>
```

Canonical `<data-dir>/ready.json` has `version`, `url`, `mcpUrl`, `dataDir`, `pid`, and
`startedAtUtc`. Require a live PID and HTTP 200 from `<url>/health/ready`. `--status --json` returns
`running`, nullable `version`/`url`/`mcpUrl`, `dataDir`, nullable `setupRequired`, and `installKind`
(`velopack-managed` or `unmanaged`); it exits 0 only when running, otherwise 1.

Engine codes: 0 success; 1 stopped/unexpected; 2 usage; 3 validation; 4 instance busy; 5
setup/credential failure; 6 requested port unavailable. Installer codes: 0 success; 1 generic; 2
unsupported platform/asset; 3 checksum; 4 network/release; 10 Windows runtime; 11 setup/key; 12
readiness; 13 external-agent skill; 14 autostart. Installer categories never propagate an engine
code raw.

## Capture connection values

The engine's ready line provides `XE_MCP_URL`. Store the one-time `xemcp_...` key in a secret manager
and expose it to the client as `XE_MCP_KEY` (or the client-specific environment variable below).
The endpoint uses plain HTTP on loopback:

```text
http://127.0.0.1:<port>/api/local/v1/mcp/server
```

## Claude Code

```json
{
  "mcpServers": {
    "xe-local-ai-engine": {
      "type": "http",
      "url": "${XE_MCP_URL}",
      "headers": { "Authorization": "Bearer ${XE_MCP_TOKEN}" },
      "timeout": 1800000
    }
  }
}
```

For long synchronous local-model calls, configure a generous per-server `timeout`. Prefer the
durable background lifecycle tools when the client does not need to hold one request open.

## Codex CLI

Load the key into `XE_MCP_TOKEN` and add this to `~/.codex/config.toml`:

```toml
[mcp_servers.xe-local-ai-engine]
url = "http://127.0.0.1:<port>/api/local/v1/mcp/server"
bearer_token_env_var = "XE_MCP_TOKEN"
startup_timeout_sec = 30
tool_timeout_sec = 1800
```

## Cursor

Load the key into `XE_MCP_TOKEN` and add a user-global `~/.cursor/mcp.json` or project
`.cursor/mcp.json`. Cursor expands `${env:NAME}` in server configuration strings:

```json
{
  "mcpServers": {
    "xe-local-ai-engine": {
      "url": "http://127.0.0.1:<port>/api/local/v1/mcp/server",
      "headers": { "Authorization": "Bearer ${env:XE_MCP_TOKEN}" }
    }
  }
}
```

## VS Code / GitHub Copilot

Add `.vscode/mcp.json` and let VS Code prompt/store the secret:

```json
{
  "inputs": [
    {
      "type": "promptString",
      "id": "xe-mcp-key",
      "description": "XE Local AI Engine MCP key",
      "password": true
    }
  ],
  "servers": {
    "xe-local-ai-engine": {
      "type": "http",
      "url": "http://127.0.0.1:<port>/api/local/v1/mcp/server",
      "headers": { "Authorization": "Bearer ${input:xe-mcp-key}" }
    }
  }
}
```

## OpenCode v2

Add `opencode.json`:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "servers": {
      "xe-local-ai-engine": {
        "type": "remote",
        "url": "http://127.0.0.1:<port>/api/local/v1/mcp/server",
        "oauth": false,
        "headers": { "Authorization": "Bearer {env:XE_MCP_TOKEN}" }
      }
    }
  }
}
```

## Gemini CLI

Add the server to `settings.json`. Its timeout is milliseconds:

```json
{
  "mcpServers": {
    "xe-local-ai-engine": {
      "httpUrl": "http://127.0.0.1:<port>/api/local/v1/mcp/server",
      "headers": { "Authorization": "Bearer ${XE_MCP_TOKEN}" },
      "timeout": 1800000
    }
  }
}
```

Gemini expands environment variables in settings strings. Do not set `trust: true` merely because
the engine key is agentic; client-side confirmation policy remains an independent decision.

## Trust and tunnels

The `delegate` key exposes the eight shared tools in [MCP tools](mcp-tools.md). An `agentic` key
exposes all 23 tools and is operator-equivalent only for that enumerated MCP surface; it is not an
Operator JWT and does not authorize the REST API. Approval-required root tools are auto-approved
only after a strict metadata-only audit write. Arguments, prompts, tokens, passwords, full keys, and
host paths are never audit payloads. Child agents remain curated and do not inherit agentic access.

The node binds to loopback only. If the MCP client runs elsewhere, an operator must create and own a
local tunnel whose engine-side connection terminates on loopback, for example SSH local forwarding
or a separately secured Tailscale/SSH path. Do not change the engine to listen on a routable address
or put it behind a same-host reverse proxy; both invalidate the supported peer-trust model.
