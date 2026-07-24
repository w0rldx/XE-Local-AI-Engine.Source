# XE Local AI Engine Node Web Server

`XE-Local-AI-Engine.Client` is the node-side web server for the local AI engine. It hosts the React management UI from `XE-Local-AI-Engine.Client.React`, exposes node-local FastEndpoints APIs under
`/api/local/v1`, maps local SignalR hubs, persists node chat state in SQLite, and connects to the central platform through the existing `WorkerHub` channel.

## Current UI shape

- The React Web UI owns the web root.
- Legacy Razor component dependencies have been removed from this host.
- The SPA shell is served as a static `index.html`; browser requests authenticate with the node JWT flow.
- Cloud-provider credentials and platform worker credentials stay server-side; they are never returned to the React client or written to logs.

## Main responsibilities

- Serve the built React app from `wwwroot/index.html` and static assets from `wwwroot/assets/**`.
- Expose node JWT-authenticated APIs for chat, agents, settings, models, knowledge, logs, scheduling, and invocations.
- Stream local chat and runtime-log events over local SignalR endpoints.
- Apply SQLite migrations and recover interrupted chat messages at startup.
- Supervise the node-local llama.cpp and Stable Diffusion runtime processes. There is no HostAgent, Tray, or container-runtime project in the current architecture.
- Connect or disconnect from the platform through `WorkerHub` based on the node's configured remote opt-in state.

## Development notes

1. From the repository root, build the React client with `cd XE-Local-AI-Engine.Client.React && pnpm run build`.
2. Build this host through `XE-Local-AI-Engine.slnx`; the project copies the React `dist/` output into `wwwroot` during build.
3. Keep generated OpenAPI/React client files regenerated through their scripts instead of hand-editing generated output.
4. Preserve the local endpoint security posture: loopback/local origin, JWT authentication, strict host/origin checks, and no secret-bearing responses.

## Validation

Use the repository validation wrapper rather than ad-hoc commands:

```bash
bash .opencode/scripts/project-validate.sh --scope changed --serial
```
