# Runbook — connect an external MCP client to this node

> Audience: an operator who wants Claude Code (or another MCP client) to hand work to the local model
> running on this node.

This is the **inbound** direction: the node acts as an MCP *server*. It is the mirror image of the
`mcp/servers` surface, which is the node acting as an MCP *client* against third-party servers. The
two are independent — configuring one does nothing to the other.

---

## 1. What the node exposes

| Tool | What it does |
|---|---|
| `list_agents` | Lists the saved agents (personas) on this node — id, name, description. |
| `list_models` | Lists the locally installed models a run can bind to directly. |
| `run_agent` | Runs a task on the local model and returns the result. Takes `task`, plus **exactly one** of `agent` (a saved agent's id or name) or `model`, and an optional `instructions` override. |

The surface is **read-only**: nothing here writes node state. An MCP client has no human-in-the-loop
route to answer a tool-approval prompt, so approval-gated tools are not reachable through it — the
same rule unattended scheduled runs follow.

## 2. Generate the key

Node Settings → **MCP server** → *Generate key*. The key looks like `xemcp_…`.

It is stored encrypted in the node database and can be re-read from that page, so you can copy it
again later without invalidating clients that already hold it. **Regenerating replaces it
immediately** — every client configured with the old value stops working at once. Revoking removes it
and closes the inbound surface entirely.

## 3. Point Claude Code at it

The same page shows the endpoint URL. It looks like:

```
http://127.0.0.1:<port>/api/local/v1/mcp/server
```

```bash
claude mcp add --transport http xe-engine \
  "http://127.0.0.1:<port>/api/local/v1/mcp/server" \
  --header "Authorization: Bearer xemcp_<your-key>"
```

Or in `.mcp.json`:

```json
{
  "mcpServers": {
    "xe-engine": {
      "type": "http",
      "url": "http://127.0.0.1:<port>/api/local/v1/mcp/server",
      "headers": { "Authorization": "Bearer xemcp_<your-key>" },
      "timeout": 1800000
    }
  }
}
```

### That `timeout` is not optional in practice

Claude Code applies three separate limits to an HTTP MCP server, and a local model will hit all
three unless you raise them:

| Limit | Default | Why it bites |
|---|---|---|
| Time to **first response byte** | **60 s** | A cold GGUF load routinely exceeds this. |
| Idle window (no response *and* no progress notification) | **5 min** | A long generation looks hung without progress. |
| Wall-clock per tool call | per-server `timeout`, else ~28 h | — |

Setting the per-server `timeout` (milliseconds) raises the first-byte timer to the same value and
acts as a floor on the idle window. `1800000` (30 min) is a sane starting point for a large local
model; lower it if your models are small.

`run_agent` emits progress notifications as it admits and runs the task, which is what keeps the idle
window alive during a long generation. Progress does **not** extend the wall-clock limit.

Output is capped at 24 000 characters and marked when truncated, so it stays inside Claude Code's
~25 000-token MCP output cap rather than being cut off by the client without explanation.

## 4. Verify

```
/mcp
```

in Claude Code should list `xe-engine` as connected with three tools. Then ask it to use one, e.g.
*"use the xe-engine MCP server to list the agents on my local node"*.

## 5. Where it can be reached from

**Same machine only.** The endpoint lives under `/api/local/v1`, so `LocalApiSecurityMiddleware`
rejects any request whose transport peer is not loopback, whose `Host` is not
`localhost`/`127.0.0.1`/`::1`, or whose `Origin` (when present) is not same-origin loopback. The
`LoopbackBindGuard` additionally stops the process outright if Kestrel ever binds a routable address.
This is a deliberate product invariant, not a default to be relaxed — see
[Security & Privacy](../wiki/12-security-and-privacy.md) §3.

**WSL2 note (measured 2026-08-03, NAT mode with `localhostForwarding=true`):** Claude Code running on
*Windows* against a node running *inside WSL* works unchanged. WSL relays the connection as loopback
inside the VM, so the peer address the node sees is `127.0.0.1` and all three checks pass. No
configuration change is needed for that topology. Not re-verified under WSL **mirrored** networking
mode.

## 6. Troubleshooting

| Symptom | Cause |
|---|---|
| `401` | No key generated, wrong key, or the key was regenerated/revoked. Re-copy from Node Settings. |
| `403` | The request did not look loopback — check you used `127.0.0.1`/`localhost` and not a LAN address or hostname. |
| Tool call aborts after ~60 s | No per-server `timeout` set. See §3. |
| Tool call aborts after ~5 min | Same fix; the idle window is floored by `timeout`. |
| `run_agent` returns "Cannot run: …" | An expected, sanitized rejection (no model fits, node busy, agent not found). It is a normal tool result, not a protocol error. |

## 7. Protocol notes

The node implements MCP specification revision **2026-07-28** via C# SDK 2.0.0, in **stateless** mode
(the revision removed protocol-level sessions and the `initialize` handshake). Authorization is
**optional** in that revision; this node deliberately does not implement the OAuth 2.1 profile and
therefore advertises no Protected Resource Metadata — the pre-shared bearer key is the whole
authentication story, backed by the loopback gate described in §5. Not advertising PRM is also what
keeps Claude Code from discarding your configured `Authorization` header and attempting OAuth
discovery instead (`anthropics/claude-code#59467`).

The revision's transport security requirements are met by the existing local-API gate rather than by
anything MCP-specific: servers **MUST** validate `Origin` and answer `403` when it is present and
invalid, **SHOULD** bind only to loopback when running locally, and **SHOULD** authenticate every
connection. `LocalApiSecurityMiddleware`, `LoopbackBindGuard` and the bearer key cover all three.

**On long-running work.** Today `run_agent` holds the request open and reports progress. The official
alternative for genuinely long calls is the `io.modelcontextprotocol/tasks` extension (SEP-2663,
NuGet `ModelContextProtocol.Extensions.Tasks` 2.0.0): the server returns a task handle and the client
polls `tasks/get`. If that is ever adopted here, note that the two mechanisms are **mutually
exclusive** — the SEP states `notifications/progress` MUST NOT be sent for a task — so adopting tasks
means *replacing* the progress reporting on that path, not adding to it. The tool's own arguments and
return shape would not change.
