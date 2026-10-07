# Troubleshooting

For an agent installing and operating the node over MCP. Everything user-facing (models that will not load, GPU
detection, the app window, update problems) is in the user guide's
[FAQ & troubleshooting](https://github.com/w0rldx/XE-Local-AI-Engine.Source/blob/develop/docs/user-guide/docs/faq.md).

| Symptom | Likely cause | Recovery |
|---|---|---|
| HTTP `503` "Vault locked" on MCP or any local API route; `/health/ready` still answers `200` | The node started locked and waits for the admin password. `ready.json` and `--status --json` report `vault` as `locked`. | Restart with `XE_ADMIN_PASSWORD` or `--admin-password-stdin`, or ask the operator to unlock it on the node's unlock page. |
| Engine exits `5` | The admin password does not unlock the node's key, or a password reset lacked the correct recovery code. Nothing was changed. | Obtain the original admin password from the operator. Do not delete `node.key`. |
| Engine exits `4` | Another engine already holds the data directory's single-instance lease. | Discover the running engine from `<data-dir>/ready.json` instead of starting a second one. |
| The URL or port changed | The desktop port is chosen at start and can differ between runs. | Read `XE_URL` / `XE_MCP_URL` from the ready line, or `url` / `mcpUrl` from `<data-dir>/ready.json`; never hard-code a port. |
| HTTP `401` | Missing, malformed, revoked, or rotated inbound MCP key. | Reload the current `xemcp_...` value from secret storage. Minting a replacement invalidates the old key immediately. |
| HTTP `403` or local API rejection | The caller is outside the loopback trust boundary, or supplied a disallowed `Host`/`Origin`. | Connect on loopback or through an operator-owned local tunnel. Do not widen the listener. |
| An admin tool is missing | The active key has `delegate` scope, or the client cached the old tool catalog. | Mint an `agentic` key, update the client secret, reconnect, and refresh its MCP tool cache. |
| An approval-required agentic tool fails before invocation | The strict metadata-only approval audit could not be persisted. | Inspect node logs and storage health. The failure is intentionally fail-closed; do not bypass the recorder. |
| `--status --json` exits `1` | The canonical readiness evidence does not identify a live healthy process. | Inspect `<data-dir>/ready.json`, confirm its PID is live, then restart with `--mcp-only`. |
| `run_agent` times out during first use | A cold local model is loading, or the client has a short hard timeout. | Raise the client timeout or use `start_agent_run` and poll `get_agent_run`. |
| `request_id_conflict` | A durable request UUID was reused with different inputs. | Generate a fresh UUID for the distinct request. |
| `result_expired` | The durable result payload passed its 24-hour retention window. | Start a new run; do not reuse the old request id for different work. |
| `workspace_not_authorized` | The opaque workspace id is missing, invalid, or was revoked by the operator. | Call `list_workspaces` again and use a currently authorized id. Never send a host path. |
| `workspace_busy` | Another read-only Coder operation owns the workspace lease. | Wait and retry after the active operation finishes. |
| AppImage will not mount on Linux | FUSE is unavailable. | Use the installer's extraction fallback or set `APPIMAGE_EXTRACT_AND_RUN=1`. |
| Windows reports a missing framework | ASP.NET Core Runtime 10.0.12 (or a newer .NET 10 servicing patch) is absent. | Install the x64 ASP.NET Core Runtime from Microsoft, then retry. The installer does not elevate silently. |
| Windows SmartScreen blocks launch | The current portable release is unsigned. | Verify `CHECKSUMS.sha256` and `RELEASE-MANIFEST.json`, then use the normal Windows trust prompt only if the verified publisher/source is acceptable. The installer never dismisses SmartScreen automatically. |

**Logs.** The node writes `<data-dir>/logs/xe-node-<date>.log`, plus `desktop.log`, `startup-crash.log` and, on
Windows, `launcher.log`. The data directory is `%LOCALAPPDATA%\XE-Local-AI-Engine` on Windows and
`~/.local/share/XE-Local-AI-Engine` (or `$XDG_DATA_HOME/XE-Local-AI-Engine`) on Linux, unless `XE_DATA_DIR` is set;
the ready line's `XE_DATA_DIR` names the one in use.

For deeper lifecycle details and client diagnostics, see the repository's
[inbound MCP client runbook](https://github.com/w0rldx/XE-Local-AI-Engine.Source/blob/develop/docs/runbooks/connect-an-mcp-client-runbook.md).
