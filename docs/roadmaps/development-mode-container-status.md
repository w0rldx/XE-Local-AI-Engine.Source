# Development Mode container implementation status

- **Decision:** [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md)
- **Delivery plan:** [`Plans/2026-07-28-dev-mode-container-sandbox-and-command-profiles-plan.md`](../../Plans/2026-07-28-dev-mode-container-sandbox-and-command-profiles-plan.md)
- **Last verified against the tree:** 2026-07-31
- **Overall state:** In progress; Docker is an opt-in Development Mode provider, not the default.

This is the living implementation-status companion to ADR 0004. The ADR records the accepted boundary and must not be updated as a progress log. Update this page when the shipped implementation state changes.

## Implemented

| Capability | Current evidence |
|---|---|
| Per-feature sandbox roles | `IAgentSandboxRuntimeProvider` and `IDevelopmentSandboxRuntimeProvider`; `SandboxProviderSelector` prevents the Docker provider from being selected for AgentHome/Coder. |
| Docker provider seam | `DockerSandboxRuntimeProvider` implements only `IDevelopmentSandboxRuntimeProvider`. |
| Provider registration and selection | `AddNodeContainerSandboxExtensions` registers the provider; `Development:Sandbox:Provider=docker` selects it. An unset Development provider follows the existing AgentHome provider. |
| Daemon preflight and attestation | `DockerDaemonPreflightService`, `DockerDaemonAttestationStore`, and their tests are present. |
| Container hardening primitives | `DockerSandboxHardening`, `DockerSandboxPaths`, and provider tests cover the implemented creation boundary. |
| Standalone managed workspace and mount brokerage | `DevelopmentWorkspaceProvider`, the Development runners, and `DevelopmentMountBrokerTests` use the Development-specific sandbox role. |

## Not yet a completed product switch

- Docker remains opt-in. The repository does not configure it as the default Development Mode provider.
- The delivery plan remains authoritative for unfinished Slice 3 work, acceptance gates, and sequencing. Do not infer completion from the presence of the provider class alone.
- ADR 0004's narrow scope remains unchanged: no Docker on the inference path or in AgentHome/Coder, no repository-supplied container configuration, and no silent unisolated fallback once Docker is the selected Development provider.

## Updating this page

1. Verify claims against symbols and tests in the current tree.
2. Record the verification date above.
3. Update the plan's sequencing/status summary to link here rather than copying a second status narrative.
4. Do not edit ADR 0004 unless the architectural decision itself is superseded or amended.
