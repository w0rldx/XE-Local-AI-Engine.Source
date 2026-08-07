# Runbook — connect an external MCP client to this node

> Audience: an operator who wants Codex, Claude Code, or another MCP client to delegate work to a
> local model running on this node.

This is the **inbound** direction: the node acts as an MCP *server*. It is independent of the
`mcp/servers` surface, where the node acts as an MCP *client* for third-party servers.

---

## 1. What the node exposes

The inbound server exposes exactly eight tools:

| Tool | Purpose |
|---|---|
| `list_agents` | List saved agents by id, name, and description. |
| `list_models` | List locally installed models that are currently available. |
| `run_agent` | Run one task synchronously and return its bounded result. This is the compatibility path for clients that expect one call to hold the connection open. |
| `list_workspaces` | List operator-authorized read-only workspaces as opaque ids and aliases. Host paths are never returned. |
| `start_agent_run` | Accept a durable background run and return its lifecycle record immediately. |
| `get_agent_run` | Poll one background run by `request_id` and return its bounded result when available. |
| `cancel_agent_run` | Durably request cancellation by `request_id`. |
| `list_agent_runs` | List bounded, content-free lifecycle metadata, optionally filtered by status. |

### Synchronous compatibility or durable background work

- Use `run_agent` for a short, synchronous delegation. The MCP request remains open, and the client
  can cancel the work by cancelling that request.
- Prefer `start_agent_run` for long work. It returns `accepted` quickly; poll with `get_agent_run`, or
  continue other work and poll later. The node keys the run by a caller-supplied UUID in its durable
  ledger, not by an HTTP connection or MCP client session.
- A client disconnect does not cancel a background run. A later Codex or Claude Code connection can
  use the same `request_id` to read or cancel it. After a node restart, queued records remain
  recoverable; work that was already executing is reported as `interrupted` rather than silently
  replayed.

`start_agent_run` requires a canonical hyphenated UUID in `request_id` and exactly one of `agent` or
`model`. Generate a fresh UUID for each distinct request. Repeating the same UUID with the same
request is idempotent and returns `existing`; repeating it with different request data returns
`request_id_conflict`.

A typical client workflow is:

1. Call `list_agents` and `list_models`.
2. If the task needs repository context, call `list_workspaces` and select an opaque workspace id.
3. Call `start_agent_run` with a fresh `request_id`.
4. Poll `get_agent_run` until the status is terminal: `succeeded`, `failed`, `cancelled`, or
   `interrupted`.
5. Call `cancel_agent_run` only when the work is no longer needed.

The result is limited to 24,000 characters and carries `result_truncated: true` when clipped. Task,
instruction, and result payloads are encrypted in the node ledger. Result payloads expire 24 hours
after terminalization; the keyed request identity remains so a reused UUID cannot silently become a
different request.

Expected lifecycle outcomes are ordinary structured tool results:

| Tool | Stable `status` values |
|---|---|
| `start_agent_run` | `accepted`, `existing`, `result_expired`, `conflict`, `capacity`, or `rejected`. Inspect `failure_code`; `conflict` uses `request_id_conflict`, and `capacity` uses `capacity_exceeded`. |
| `get_agent_run` | `queued`, `running`, `succeeded`, `failed`, `cancelled`, `interrupted`, `result_expired`, `not_found`, or `invalid_request`. |
| `cancel_agent_run` | `requested`, `already`, `terminal`, `not_found`, or `conflict`. Lifecycle races do not become protocol errors. |
| `list_agent_runs` | `ok`, or `invalid_status` when the filter is not one of the documented lifecycle states. |

### Read-only execution boundary

The inbound surface does not expose state-changing node tools.

- Bare models and general saved agents run **tool-less**. Their saved skills and ordinary tool
  configuration do not enter the inbound execution binding.
- Only the seeded **Coder (read-only)** agent can receive workspace tools. Its binding is restricted
  to exactly `list_files`, `read_file`, and `search_text`; it cannot edit files or run commands.
- The Coder requires `model_override` with `start_agent_run` (`modelOverride` with the synchronous
  `run_agent`) because the seeded agent does not pin a model. It also requires a `workspace_id` from
  `list_workspaces`.
- The operator creates or revokes workspace access in Node Settings → **MCP workspace access**. MCP
  clients receive only the alias and opaque id, never the trusted host path.

## 2. Generate the key

Open Node Settings → **MCP server** → **Generate key**. The key looks like `xemcp_…`.

Copy it immediately. The node stores only a one-way SHA-256 hash, so it cannot show or recover the
key later. Regenerating replaces the key immediately; revoking it closes the inbound surface.

Load the key into an environment variable named `XE_ENGINE_MCP_TOKEN` through your shell's secret
manager or credential loader. Do not put the key directly in a command, configuration committed to
source control, or shell history.

The settings page also shows the current endpoint, for example:

```text
http://127.0.0.1:<port>/api/local/v1/mcp/server
```

## 3. Configure Codex

Codex CLI, the Codex IDE extension, and the ChatGPT desktop app share MCP configuration. Codex reads
user configuration from `~/.codex/config.toml`, or project configuration from `.codex/config.toml`
after the project is trusted. The option names below match the current
[official Codex MCP configuration](https://developers.openai.com/codex/mcp).

```toml
[mcp_servers.xe-engine]
url = "http://127.0.0.1:<port>/api/local/v1/mcp/server"
bearer_token_env_var = "XE_ENGINE_MCP_TOKEN"
startup_timeout_sec = 30
tool_timeout_sec = 1800
enabled_tools = [
  "list_agents",
  "list_models",
  "run_agent",
  "list_workspaces",
  "start_agent_run",
  "get_agent_run",
  "cancel_agent_run",
  "list_agent_runs",
]
```

`tool_timeout_sec = 1800` preserves the synchronous `run_agent` compatibility path for slower local
models. The background lifecycle tools normally return quickly. `enabled_tools` is optional, but
keeping the explicit allowlist makes unexpected future tools fail closed for this client.

The equivalent initial CLI registration is:

```bash
codex mcp add xe-engine \
  --url "http://127.0.0.1:<port>/api/local/v1/mcp/server" \
  --bearer-token-env-var XE_ENGINE_MCP_TOKEN
```

Then add the timeout and optional tool allowlist to `config.toml`. Verify the registration with:

```bash
codex mcp list
codex mcp get xe-engine
```

In the Codex terminal UI, use `/mcp` to inspect the active connection. OpenAI documents the shared
configuration, CLI management commands, TUI command, token environment variable, timeouts, and tool
allowlists on the [Codex MCP page](https://developers.openai.com/codex/mcp).

## 4. Configure Claude Code

Use a project-scoped `.mcp.json` so the endpoint contract can be shared without storing the bearer
token. Claude Code expands environment variables in both `url` and `headers`; its per-server
`timeout` is milliseconds. These behaviors are documented in the
[official Claude Code MCP guide](https://code.claude.com/docs/en/mcp).

Set `XE_ENGINE_MCP_URL` to the endpoint shown in Node Settings and load
`XE_ENGINE_MCP_TOKEN` through your secret manager. Then add this file at the project root:

```json
{
  "mcpServers": {
    "xe-engine": {
      "type": "http",
      "url": "${XE_ENGINE_MCP_URL}",
      "headers": {
        "Authorization": "Bearer ${XE_ENGINE_MCP_TOKEN}"
      },
      "timeout": 1800000
    }
  }
}
```

Claude Code asks you to trust the project and approve project-scoped MCP servers before connecting.
Run Claude Code interactively, review both prompts, and then verify:

```bash
claude mcp list
claude mcp get xe-engine
```

Use `/mcp` inside Claude Code to inspect the live connection. Anthropic documents project trust,
approval status, `claude mcp list`, and `claude mcp get` in its
[MCP server management guidance](https://code.claude.com/docs/en/mcp#managing-your-servers).

### Static-header compatibility form

Claude Code also accepts a static header from the CLI:

```bash
claude mcp add --transport http --scope local xe-engine "$XE_ENGINE_MCP_URL" \
  --header "Authorization: Bearer <redacted-token>"
```

This form is compatibility-only. Replacing the placeholder puts the token in the process arguments
and stores it as a static header; typing it directly also exposes it to shell history. Prefer the
environment-expanded `.mcp.json` configuration above. The CLI shape is documented by Anthropic's
[HTTP MCP configuration reference](https://code.claude.com/docs/en/mcp#add-a-remote-http-server).

### Claude timeouts and backgrounding

Claude Code treats the per-server `timeout` as a hard wall-clock limit. Server progress does not
extend it. Current Claude Code can move a long main-conversation MCP call into a **client-side**
background task after two minutes, but the same client timeout still applies. See Anthropic's
[timeout and automatic-backgrounding behavior](https://code.claude.com/docs/en/mcp#automatic-backgrounding-of-long-tool-calls).

That client backgrounding is not durable server work. If Claude Code exits, only a run accepted by
`start_agent_run` remains independently addressable in the node ledger. Use `get_agent_run` from a
later client session to continue the workflow.

## 5. Verify delegation

In Codex or Claude Code:

1. Confirm `/mcp` shows `xe-engine` connected with eight tools.
2. Ask the client to call `list_models` and `list_agents`.
3. Start a small background run with a fresh UUID.
4. Poll it with `get_agent_run` until it reaches a terminal state.
5. For repository inspection, authorize a folder in Node Settings → **MCP workspace access**, call
   `list_workspaces`, and delegate to **Coder (read-only)** with a locally installed quantized model
   as `model_override`.

Use `list_models` as the authority for the model identifier; do not assume a downloaded repository
name is the installed model id.

## 6. Where it can be reached from

**Same machine only.** The endpoint lives under `/api/local/v1`, so `LocalApiSecurityMiddleware`
rejects requests whose transport peer is not loopback, whose `Host` is not
`localhost`/`127.0.0.1`/`::1`, or whose `Origin` is not same-origin loopback. `LoopbackBindGuard`
also stops the process if Kestrel binds a routable address. See
[Security & Privacy](../wiki/12-security-and-privacy.md) §3.

**WSL2 note (measured 2026-08-03, NAT mode with `localhostForwarding=true`):** a client running on
Windows can reach a node running inside WSL through `localhost`. This topology has not been
re-verified under WSL mirrored networking mode.

## 7. Troubleshooting

| Symptom or outcome | Meaning and action |
|---|---|
| `401` | The bearer key is missing, wrong, regenerated, or revoked. Load the current key into the configured environment variable and restart the client. |
| `403` | The request did not satisfy the loopback gate. Use the exact loopback endpoint shown in Node Settings, not a LAN address or hostname. |
| Codex aborts `run_agent` after 60 seconds | Set `tool_timeout_sec` high enough for the selected local model, or use the durable lifecycle tools. |
| Claude reports a hard timeout | Raise the server's `.mcp.json` `timeout`, or use `start_agent_run` so only the short admission call is client-bound. |
| `request_id_conflict` | The UUID already identifies different request data. Do not overwrite it; generate a fresh UUID for the distinct request. |
| `result_expired` | The run identity still exists, but its encrypted payload passed the 24-hour retention window and was compacted. The result cannot be recovered; start genuinely new work with a fresh UUID. |
| `workspace_not_authorized` | The opaque id is invalid, revoked, or was supplied to something other than the seeded Coder. Call `list_workspaces` again and use an active id only with **Coder (read-only)**. |
| `workspace_busy` | Another operation holds the node's single workspace-execution lease. Wait for it to finish, then start a new run with a fresh UUID. The failed request remains terminal and idempotent. |
| `cancel_agent_run` returns `terminal` | The run already finished. This is an expected stable outcome, not a protocol error; inspect it with `get_agent_run`. |
| `cancel_agent_run` returns `already` | A cancellation marker was already recorded. Continue polling until the run becomes terminal. |
| `run_agent` returns `Cannot run: …` | The synchronous compatibility path rejected an expected condition such as invalid binding, unavailable model, capacity, or workspace access. The message is a normal tool result. |

## 8. Protocol and durability notes

The endpoint uses authenticated Streamable HTTP behind the existing local-API loopback gate. The
pre-shared bearer key is the authentication mechanism for this local surface.

Background durability is an **application-level node contract** implemented by
`start_agent_run`/`get_agent_run`/`cancel_agent_run`/`list_agent_runs`. It does not depend on an MCP
connection staying open, and it does not currently advertise a protocol-level task extension. A
protocol-task adapter can be considered later; clients should use these lifecycle tools today.

The client commands and configuration fields in this runbook were verified with Codex CLI 0.146.1
and Claude Code 2.1.223 on 2026-08-06. Recheck the linked official documentation after upgrading
either client.
