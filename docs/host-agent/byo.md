# Bring Your Own Runtime

Bring-your-own (BYO) mode lets an operator use an existing OpenAI-compatible/Ollama endpoint instead of the managed rootless-Docker runtime.

## What changes in BYO mode

- HostAgent.Linux runs in `custom` lifecycle mode.
- HostAgent does not start, stop, or reconcile the Ollama container.
- The Node Web Server points `OLLAMA_BASE_URL` at the operator-provided endpoint.
- The Blazor Manager UI displays `runtimeLifecycle: external`.
- The existing `WorkerHub` connection remains unchanged.

## What does not change

- Platform code and platform DTOs remain untouched.
- Platform credentials remain in the Node Web Server only.
- HostAgent and Tray still do not connect to the platform.
- Tray still controls the local HostAgent process/unit, but service lifecycle actions must clearly show that the model endpoint is external.

## Configuration checklist

1. Configure `host-agent.json` for `custom` mode.
2. Set the external Ollama/OpenAI-compatible base URL for the Node Web Server.
3. Validate that the endpoint is reachable from the Node Web Server environment.
4. Validate that requested models are present or that on-demand pull is intentionally disabled/not applicable.
5. Confirm the Blazor Manager UI shows external lifecycle state.

## Operator expectations

BYO shifts model runtime health, model availability, GPU configuration, and endpoint authentication to the operator. HostAgent can validate reachability and surface status, but it does not own external process lifecycle.

## Security notes

- Do not copy platform worker credentials into the BYO runtime.
- Do not log external endpoint bearer tokens.
- Keep local admin endpoints loopback-only and token-authenticated even in BYO mode.
