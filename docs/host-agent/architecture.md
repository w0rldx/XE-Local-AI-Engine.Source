# HostAgent Architecture

The local node runtime keeps the existing platform contract intact: only the Node Web Server (`XE-Local-AI-Engine.Client`) talks to the central platform over the existing `WorkerHub` SignalR channel. HostAgent and the Tray are local substrate components only; they never connect to the platform and never read or forward cloud credentials.

## Components

| Component | Runs as | Responsibility |
| --- | --- | --- |
| Node Web Server | Container on `xe-engine-net` | Blazor Manager UI, model control plane, chat hot path, existing `WorkerHub` connection. |
| HostAgent.Windows | User-mode Windows process | WSL2 import/bootstrap/supervision, Windows-side admin API, bridge to HostAgent.Linux. |
| HostAgent.Linux | `systemd --user` unit | Rootless Docker, manifest reconcile, container lifecycle, capabilities, gRPC server. |
| Tray Launcher | Avalonia desktop tray app | User entry point, health badge, browser launch, local start/stop/restart commands. |

## Data and control paths

```text
Platform C0re.Server.*
  <== existing SignalR WorkerHub ==>
Node Web Server container
  <== gRPC + body-bound HMAC over bind-mounted Unix socket ==>
HostAgent.Linux --user unit
  <== Docker API over rootless user socket ==>
Rootless Docker containers: ollama, xe-node-web-server

Tray
  <== loopback admin HTTP + bearer token ==>
HostAgent.Windows or HostAgent.Linux
```

On Windows-managed installs, HostAgent.Windows also calls HostAgent.Linux over WSL TCP loopback with the same body-bound HMAC scheme used for the Node Web Server to HostAgent.Linux path.

## Security boundaries

- Platform credentials remain owned by the Node Web Server (`worker-credentials.enc`) and are not moved into HostAgent or Tray.
- HostAgent admin HTTP binds only to `127.0.0.1`, requires the per-process bearer token, rejects non-loopback `Host` headers, and rejects requests carrying `Origin`.
- The gRPC channel signs method name, request id, body hash, and a short time bucket with HMAC-SHA256. Replay is rejected with a per-bucket request-id cache.
- The Unix socket and HMAC secret are mounted into the Web Server container with group-scoped permissions. A non-group user must receive `Permission denied`.

## Lifecycle model

There is no service auto-start, no Run-key, no Task Scheduler entry, and no XDG autostart. The user starts the runtime by launching the Tray. `Quit Tray` exits only the tray icon; `Stop Services` performs the graceful substrate shutdown.

The bootstrap model (`qwen3:0.6b` by default) is pulled before the Node Web Server connects to `WorkerHub`. Larger models are pulled on demand and report progress through the Blazor Manager UI.

## External references used

- Avalonia desktop/tray docs: classic desktop lifetime and `TrayIcon`/`NativeMenu` patterns.
- Microsoft WSL docs: `wsl --install`, `wsl --import`, and command execution semantics.
- systemd user-unit docs: `systemctl --user` and per-user units.
- Docker rootless docs: rootless daemon under a user namespace and `XDG_RUNTIME_DIR` socket behavior.
