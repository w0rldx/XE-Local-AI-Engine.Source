# Runbook — connect an external MCP client to this node

> Audience: an operator connecting Claude Code, Codex CLI, Cursor, VS Code/GitHub Copilot,
> OpenCode, or Gemini CLI to a local XE Local AI Engine node.

This is the **inbound** direction: the node is the MCP server at
`http://127.0.0.1:<port>/api/local/v1/mcp/server`. It is independent of `mcp/servers`, where the node
is an MCP client for third-party servers. The transport is Streamable HTTP; no stdio form exists.

For unattended installation and lifecycle commands, start at
[Install with an external agent](../agentic-support/agent-install.md).

## 1. Choose a key scope

The node persists one inbound key. Minting a new key rotates it immediately; there is no dual-valid
window and the plaintext is returned exactly once.

| scope | tools visible | execution boundary |
|---|---:|---|
| `delegate` | 8 | Shared discovery and agent-run tools. Ordinary saved agents/bare models remain tool-less; the seeded Coder receives exactly three read-only workspace tools. |
| `agentic` | 23 | The same 8 tools plus 15 administration tools. Trusted operator-equivalent only for this enumerated MCP surface, not an Operator JWT or arbitrary REST credential. |

An agentic root run may resolve its saved agent's complete allowed-tool set. Approval-required calls
are auto-approved only after a strict metadata-only audit write; failure to persist that audit blocks
invocation. Arguments, prompts, message content, tokens, passwords, full keys, and host paths are not
recorded. Spawned children retain ordinary curated tools and do not inherit the root elevation.

The exact surface and settings whitelist live in the
[external-agent skill tool reference](../../skills/xe-local-ai-engine/references/mcp-tools.md).

## 2. Start MCP-only mode and capture the endpoint

Start the installed application with `--mcp-only`. It suppresses browser launch but still serves the
React UI and local APIs on loopback. `--port <1-65535>` requests a stable port; failure is exit 6.

Wait for this exact, unformatted stdout contract:

```text
XE_READY=1 XE_VERSION=<semver> XE_URL=http://127.0.0.1:<port> XE_MCP_URL=<XE_URL>/api/local/v1/mcp/server XE_DATA_DIR=<path>
```

Canonical `<data-dir>/ready.json` contains `version`, `url`, `mcpUrl`, `dataDir`, `pid`, and
`startedAtUtc`. Require its PID to be live and `<url>/health/ready` to return 200. `--status --json`
provides a later one-shot check and exits 0 only for a live healthy process.

## 3. Mint or rotate the key

With the app stopped:

```bash
/path/to/XE-Local-AI-Engine.Client --mcp-key delegate
/path/to/XE-Local-AI-Engine.Client --mcp-key agentic
```

On Windows invoke `XE-Local-AI-Engine.exe` with the same arguments. The command prints exactly one
`XE_MCP_KEY=xemcp_...` line. Store the value in a secret manager and never log it.

With the app running, an Operator can instead `POST /api/local/v1/mcp/server-key` with JSON
`{"scope":"delegate"}` or `{"scope":"agentic"}`. A body-less POST remains the delegate-compatible
form. The exact route is **`mcp/server-key`**.

Use `XE_MCP_URL` for the endpoint and load the key into the client-specific secret variable below.

## 4. Configure Codex CLI

Codex reads `~/.codex/config.toml` (or trusted-project `.codex/config.toml`). Load the key into
`XE_MCP_TOKEN`:

```toml
[mcp_servers.xe-local-ai-engine]
url = "http://127.0.0.1:<port>/api/local/v1/mcp/server"
bearer_token_env_var = "XE_MCP_TOKEN"
startup_timeout_sec = 30
tool_timeout_sec = 1800
```

For a delegate key, an optional fail-closed allowlist is:

```toml
enabled_tools = [
  "list_agents", "list_models", "list_workspaces", "run_agent",
  "start_agent_run", "get_agent_run", "cancel_agent_run", "list_agent_runs",
]
```

Do not keep that eight-tool allowlist for an agentic connection unless intentionally hiding admin
tools. Verify with `codex mcp list`, `codex mcp get xe-local-ai-engine`, and `/mcp`.

Source: [official Codex MCP configuration](https://developers.openai.com/codex/mcp).

## 5. Configure Claude Code

Load `XE_MCP_URL` and `XE_MCP_TOKEN`, then add a project `.mcp.json` (or the corresponding user
configuration):

```json
{
  "mcpServers": {
    "xe-local-ai-engine": {
      "type": "http",
      "url": "${XE_MCP_URL}",
      "headers": {
        "Authorization": "Bearer ${XE_MCP_TOKEN}"
      },
      "timeout": 1800000
    }
  }
}
```

Claude Code expands environment variables in `url` and `headers`. Project-scoped servers require a
trust/approval decision. Verify with `claude mcp list`, `claude mcp get xe-local-ai-engine`, and
`/mcp`. A CLI registration with `--header` is supported but places the replacement value in argv and
static configuration; prefer the environment-expanded file.

Source: [official Claude Code MCP guide](https://code.claude.com/docs/en/mcp).

## 6. Configure Cursor

Cursor reads `.cursor/mcp.json` per project or `~/.cursor/mcp.json` globally. Load the key into
`XE_MCP_TOKEN`; Cursor expands `${env:NAME}` in server configuration strings:

```json
{
  "mcpServers": {
    "xe-local-ai-engine": {
      "url": "http://127.0.0.1:<port>/api/local/v1/mcp/server",
      "headers": {
        "Authorization": "Bearer ${env:XE_MCP_TOKEN}"
      }
    }
  }
}
```

Verify in Cursor's MCP/Available Tools UI.

Source: [official Cursor MCP documentation](https://docs.cursor.com/context/model-context-protocol).

## 7. Configure VS Code / GitHub Copilot

VS Code uses `.vscode/mcp.json` or a user-profile `mcp.json`. Use an input variable so the token is
prompted and securely stored instead of committed:

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
    "xeLocalAiEngine": {
      "type": "http",
      "url": "http://127.0.0.1:<port>/api/local/v1/mcp/server",
      "headers": {
        "Authorization": "Bearer ${input:xe-mcp-key}"
      }
    }
  }
}
```

Run **MCP: List Servers** to start, inspect, or refresh it. Reset trust after material configuration
changes. For Agent Host portability, current VS Code also documents workspace `.mcp.json` and user
`~/.copilot/mcp-config.json`.

Source: [official VS Code MCP configuration](https://code.visualstudio.com/docs/agents/reference/mcp-configuration).

## 8. Configure OpenCode v2

Load the key into `XE_MCP_TOKEN`. OpenCode v2 nests servers under `mcp.servers`, uses `{env:NAME}`
interpolation, and needs `oauth: false` for a header-only credential:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "servers": {
      "xe-local-ai-engine": {
        "type": "remote",
        "url": "http://127.0.0.1:<port>/api/local/v1/mcp/server",
        "oauth": false,
        "headers": {
          "Authorization": "Bearer {env:XE_MCP_TOKEN}"
        },
        "timeout": {
          "execution": 1800000
        }
      }
    }
  }
}
```

Source: [official OpenCode v2 MCP documentation](https://opencode.ai/v2/docs/mcp-servers).

## 9. Configure Gemini CLI

Gemini CLI reads user `~/.gemini/settings.json` or project `.gemini/settings.json`. Load the key into
`XE_MCP_TOKEN`; Gemini expands environment variables in settings strings. Its timeout is milliseconds.

```json
{
  "mcpServers": {
    "xe-local-ai-engine": {
      "httpUrl": "http://127.0.0.1:<port>/api/local/v1/mcp/server",
      "headers": {
        "Authorization": "Bearer ${XE_MCP_TOKEN}"
      },
      "timeout": 1800000
    }
  }
}
```

Do not set `trust: true` merely because the engine has an agentic key; client-side confirmation
policy remains an independent decision.
Verify with `gemini mcp list` or `/mcp`.

Source: [official Gemini CLI MCP documentation](https://geminicli.com/docs/tools/mcp-server/).

## 10. Verify the selected scope

1. Reconnect or refresh the client's MCP catalog after rotation.
2. With `delegate`, confirm exactly 8 tools and call `list_models` plus `list_agents`.
3. With `agentic`, confirm exactly 23 tools and call `get_status`.
4. Walk the core administration path: `get_runtime_status`; if needed
   `start_runtime_acquisition`/`get_runtime_acquisition`; then
   `start_model_pull`/`get_model_pull`; `set_default_model`; and finally `run_agent` or the durable
   lifecycle.
5. For durable work, generate a canonical UUID, call `start_agent_run`, and poll `get_agent_run`.
   Reusing the same UUID with different scope or inputs is a conflict, not a second run.

A durable agentic run admitted before key rotation keeps the authority captured in its durable row.
Cancel it explicitly if it should no longer execute.

## 11. Reachability and tunnels

Supported direct access is same-machine loopback only. `LocalApiSecurityMiddleware` checks the
socket peer plus loopback `Host` and same-origin `Origin`; `LoopbackBindGuard` rejects routable binds.
Do not enable `Security:AllowNonLoopbackBind`, add forwarded headers, or put a same-host reverse proxy
in front of the local API to make it remote.

For a remote client, use an operator-owned encrypted tunnel whose engine-side connection terminates
on loopback. Example SSH local forwarding from the client machine:

```bash
ssh -N -L 51234:127.0.0.1:<engine-port> <operator>@<engine-host>
```

Point the client at `http://127.0.0.1:51234/api/local/v1/mcp/server`. Secure and authorize the SSH
or Tailscale path separately; the engine still authenticates the MCP bearer key.

## 12. Troubleshooting

| symptom | meaning and action |
|---|---|
| `401` | Missing, malformed, rotated, or revoked key. Load the current key and reconnect. |
| `403` | Loopback peer/Host/Origin gate failed. Use the exact local or tunnel endpoint; do not widen the listener. |
| Only 8 tools appear | The key is `delegate`, or the client cached the previous catalog. Mint `agentic`, update the secret, reconnect, and refresh tools. |
| Agentic approval-required call fails before side effects | Strict metadata-only audit persistence failed. Repair node storage/logging health; the call is intentionally fail-closed. |
| `run_agent` times out | Raise the client timeout or use `start_agent_run` and poll. |
| `request_id_conflict` | The UUID already binds different inputs or authority. Use a fresh UUID for distinct work. |
| `result_expired` | The 24-hour durable result payload expired. Start genuinely new work with a fresh UUID. |
| `workspace_not_authorized` | Refresh `list_workspaces` and use an active opaque id only with the seeded Coder. Never send a host path. |
| `--status --json` exits 1 | PID/readiness/auth-status evidence does not describe a live healthy process. Restart MCP-only mode and inspect `ready.json`. |

Client syntax was rechecked against the linked official pages on 2026-08-22. Recheck after client
upgrades because these formats evolve independently of the engine.
