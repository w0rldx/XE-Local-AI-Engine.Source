# HostAgent Troubleshooting

Use this guide to triage install, launch, and runtime issues without exposing secrets.

## First facts to collect

- OS and install mode: Windows managed, Linux native, or BYO.
- Desired state from the Tray/admin API.
- HostAgent state and health summary.
- Recent HostAgent logs with tokens redacted.
- Whether the Node Web Server has connected to `WorkerHub`.
- Whether `bootstrapModelReady` is true.

## Common symptoms

### Tray is red

Likely causes:

- HostAgent process/unit is not running.
- Runtime metadata points at a stale PID.
- Admin token rotated and the Tray needs to re-read it.
- Admin API is not bound to loopback or rejects the request.

Actions:

1. Relaunch the Tray once to force re-attach logic.
2. Check HostAgent logs.
3. Verify the admin API listens on `127.0.0.1` only.
4. Verify secure-store/token file permissions.

### Tray is yellow for a long time

Likely causes:

- Bootstrap model pull is still in progress.
- Ollama is healthy but the Web Server is not yet ready.
- Rootless Docker is starting or recovering.

Actions:

1. Open the React Web UI if available.
2. Check model pull progress.
3. Inspect HostAgent.Linux logs and rootless Docker status.

### WorkerHub never connects

Likely causes:

- `bootstrapModelReady=false` so the startup gate is correctly waiting.
- Web Server cannot read the HostAgent socket or HMAC secret.
- Existing `worker-credentials.enc` is unavailable or invalid.

Actions:

1. Verify HostAgent status reports bootstrap readiness.
2. Verify Unix socket permissions and shared-group membership.
3. Verify the Node Web Server environment has `XE_HOST_AGENT_SOCKET` and `XE_HOST_AGENT_HMAC_SECRET_FILE`.
4. Do not move or expose platform worker credentials during triage.

### Windows WSL bootstrap fails

Likely causes:

- WSL is unavailable or blocked by policy.
- Rootfs SHA-256 mismatch.
- Bootstrap script failed before writing a phase exit file.
- systemd readiness check failed after `wsl --terminate`.

Actions:

1. Capture `wsl --status` output.
2. Inspect `bootstrap.exit.json` via the allowlisted `xe-host-agent-ctl read-phase-exit bootstrap` path.
3. Reinstall as admin if the WSL feature was never enabled.
4. Rollback with the HostAgent-managed unregister path only; scripts must not call `wsl --terminate` themselves.

### Linux native unit will not start

Likely causes:

- Tray was launched under `sudo` or root.
- User systemd manager is unavailable.
- Rootless Docker prerequisites are missing.
- `XDG_RUNTIME_DIR` is unset or points to the wrong user.

Actions:

1. Relaunch as the desktop user.
2. Run `systemctl --user status xe-host-agent.service` as that same user.
3. Verify `XDG_RUNTIME_DIR` and rootless Docker setup output.
4. Confirm linger was not enabled intentionally; logoff stops the runtime by design.

### Stop Services causes invocation failures

Expected behavior is graceful drain: Stop Services sends SIGTERM, waits up to the drain window for in-flight WorkerHub invocations, flushes completion/failure envelopes, and then stops the connection cleanly.

If clean stop writes new DLQ entries:

1. Confirm the Web Server graceful shutdown hook is registered.
2. Confirm no new invocations are accepted after shutdown begins.
3. Confirm the drain window was not exceeded.
4. Treat this as a regression in the Epic E4/C7 contract.

## Secret-handling rules

- Never paste admin tokens, HMAC secrets, or worker credentials into logs, tickets, or docs.
- Redact bearer values and secret file contents.
- Prefer status summaries and token generation IDs over raw token values.
