# Aspire Development Modes

The XE AppHost can run the Node Web Server with no HostAgent, with a fast fake HostAgent rig, or with a more production-like HostAgent.Linux path. These modes are for local development and integration testing only; they do not replace MSI/deb/rpm clean-install tests.

## Modes

| Mode | Enable with | Purpose | Docker behavior |
| --- | --- | --- | --- |
| Default | no flag | Run the Node Web Server, Ollama, embeddings, and SQLite without HostAgent wiring. | Aspire-managed Ollama only. |
| Fast dev | `XE_ENABLE_HOST_AGENT_DEV=true` | Develop Blazor Manager UI and HostAgent client flows quickly. | HostAgent.Linux uses `HostAgent__Docker__UseFakeDriver=true`. |
| Runtime fidelity | `XE_ENABLE_HOST_AGENT_RUNTIME_FIDELITY=true` | Exercise the production-like socket/HMAC/startup-gate contract and real HostAgent.Linux Docker seam. | HostAgent.Linux uses the real Docker runtime client. |

If both HostAgent flags are set, runtime-fidelity mode wins.

## Launch profiles

The AppHost ships dedicated HTTPS launch profiles for IDE and `dotnet run` workflows:

| Profile | Mode |
| --- | --- |
| `https` | Default, no HostAgent wiring. |
| `https-fast-dev` | Fast dev mode with fake HostAgent Docker driver. |
| `https-runtime-fidelity` | Runtime-fidelity mode with real HostAgent.Linux Docker seam. |

Run a profile directly from `Apps/XE-Local-AI-Engine/`:

```bash
dotnet run --project XE-Local-AI-Engine.AppHost --launch-profile https-fast-dev
dotnet run --project XE-Local-AI-Engine.AppHost --launch-profile https-runtime-fidelity
```

The environment-variable examples below are equivalent and are useful when launching through the Aspire CLI.

## Fast dev mode

Use fast dev mode when you are iterating on UI, gRPC client calls, status rendering, or logs without needing rootless Docker behavior.

```bash
XE_ENABLE_HOST_AGENT_DEV=true aspire start --apphost Apps/XE-Local-AI-Engine/XE-Local-AI-Engine.AppHost
```

Fast dev mode wires:

- `xe-host-agent-linux`
- `HostAgent__Docker__UseFakeDriver=true`
- `HostAgent__Hmac__Secret` from an Aspire secret parameter
- `XE_HOST_AGENT_SOCKET` under `.data/host-agent-dev/host-agent.sock`
- Node Web Server `HostAgent__Client__*` and `HostAgent__StartupGate__*` settings

## Runtime-fidelity mode

Use runtime-fidelity mode before release work or when changing HostAgent/Node Web Server boundaries.

```bash
XE_ENABLE_HOST_AGENT_RUNTIME_FIDELITY=true aspire start --apphost Apps/XE-Local-AI-Engine/XE-Local-AI-Engine.AppHost
```

Runtime-fidelity mode keeps Aspire as the local orchestrator but makes the HostAgent path closer to production:

- HostAgent.Linux runs as an Aspire project resource.
- Node Web Server uses the same socket/HMAC configuration seam as production.
- Node Web Server startup gate is explicitly enabled and waits for HostAgent bootstrap-model readiness.
- HostAgent.Linux uses the real Docker runtime client instead of the fake driver.
- TCP is disabled; the Node Web Server path stays on the Unix socket.

Set `XE_HOST_AGENT_DOCKER_ENDPOINT` when the default rootless socket is not correct:

```bash
XE_ENABLE_HOST_AGENT_RUNTIME_FIDELITY=true \
XE_HOST_AGENT_DOCKER_ENDPOINT=unix:///run/user/1000/docker.sock \
aspire start --apphost Apps/XE-Local-AI-Engine/XE-Local-AI-Engine.AppHost
```

## What Aspire still does not model

Aspire does not launch or validate:

- HostAgent.Windows WSL import/bootstrap.
- Tray normal/log-mode lifecycle.
- MSI, deb, or rpm package behavior.
- Desktop shortcuts and autostart guards.
- Clean-install transcript acceptance criteria.

Those remain release-validation concerns documented in [Release and operations](./release-and-operations.md).

## Validation before relying on runtime-fidelity results

From `Apps/XE-Local-AI-Engine/`:

```bash
dotnet restore XE-Local-AI-Engine.slnx
dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-restore
dotnet test XE-Local-AI-Engine.slnx --configuration Release --no-build
```

If runtime-fidelity mode fails because rootless Docker is missing or unhealthy, treat it as an environment/setup blocker, not as proof that fast dev mode is broken.
