# External Apps implementation status

- **Decision:** [ADR 0010](../adr/0010-external-apps-container-execution.md)
- **Status authority:** this living page
- **Last verified against the tree:** 2026-09-11
- **Overall state:** Implemented end to end, enabled by default, and live-validated on one rootless Linux daemon.
  Windows and macOS are not live-validated, and V1 enforces no outbound network restriction.

This is the living implementation-status companion to ADR 0010. The ADR records the accepted boundary and must not
be updated as a progress log; update this page when the shipped implementation state changes. The feature reference
is [External Apps](../wiki/23-external-apps.md).

## Implemented

| Capability | Current evidence |
|---|---|
| Engine-owned container runtime layer, separate from the sandbox SPI | `Client.Application/Services/Containers/`: `IContainerRuntime`, `IContainerRuntimeResolver`, `IContainerRuntimeFactory`, `DockerDotNetRuntimeClient`, and `FakeDockerRuntimeClient` moving in lockstep with it. `ContainerRuntimeRealDaemonTests` drives it against a real daemon. |
| Shared daemon probe and identity attestation | `DockerDaemonProbe`, extracted from `DockerDaemonPreflightService` and used by both consumer classes; `DockerDaemonEndpoint.Display`/`Redact` keep an operator-set endpoint's user information, query and fragment out of every log, pin, message and API body. |
| Catalog document, validator and provider | `ExternalAppCatalogValidator` (fail-closed, whole-document rejection), `ApplicationCatalogProvider` (bundled seed, remote refresh with ETag and TTL, owner-only cache file), `ExternalAppManifestFingerprint` binding acceptance across C# and the Python converter. |
| Catalog authoring source and converter | `catalog/external-apps/` with the upstream Compose, variable classification and override document per application; `tools/build_catalog.py` and its pytest suite; `dist/applications.json` and the byte-identical embedded seed, asserted equal by test. |
| Persistence | `ExternalAppInstance` and `ExternalAppInstanceEvent`, the `AddExternalApps` migration, and `ExternalAppInstanceStore` minting the event sequence inside the same transaction as the status change. Variables are one AEAD-encrypted column, AAD-bound to the row id. |
| Deployment planning and container policy | `DeploymentPlanner` (token grammar, mount collision rejection, topological order) and `ApplicationContainerPolicy` (the specification, and `FindViolations` verifying the daemon's read-back before and after start). |
| Install, lifecycle, update, reset, uninstall and cancel pipelines | `ExternalAppService` with its `.Install`, `.Lifecycle`, `.Update` and `.Pipeline` parts, run by `ExternalAppOperationRunner` one operation per instance, every source linked to `ApplicationStopping`. |
| Storage-wipe helper container | `ExternalAppService.Pipeline.BuildStorageHelper`: digest-pinned `ExternalApps:StorageHelperImage`, one bind mount, read-only rootfs, no network, no environment, 64-process limit, removed in a `finally`. It keeps Docker's default capability set so `CAP_DAC_OVERRIDE` can unlink subuid-owned `0700` trees. `ExternalAppStorageHelperTests`. |
| Boot reconciler and state observer | `ExternalAppStartupReconciler` (three verdicts, foreign containers counted and never removed) and `ExternalAppStateObserver` (detailed listing per tick, reports `StoppedUnexpectedly`, starts and removes nothing). |
| API, hub and kill switch | `LocalApiRoutes.ExternalApps` (20 routes), `ExternalAppHub`, and the `ExternalApps:Enabled` request-path middleware that 404s the whole family including the hub negotiate, ahead of the security middleware. |
| Node setting for runtime selection | `containerRuntimeSelection`, a case-insensitive string on the wire reading back lower-case, unknown name 400; the enum stays internal. |
| Enabled by default | `appsettings.json` carries `"ExternalApps": { "Enabled": true }` while the code defaults stay `false`, so a node with missing configuration fails closed. `ExternalAppsShippedConfigurationTests` pins both halves plus the empty catalog refresh URL; `NodeCapabilities.test.ts` pins the compile-time capability. |
| React feature | `Client.React/src/features/externalApps/`: the catalog page and its runtime card, the four-step install dialog with fingerprint-bound acceptance, the permissions panel, the variables form, the installed list, the instance detail page, the update dialog, and `useExternalAppHub`. |

## The 2026-09-11 live round

Forty-seven steps against a real daemon, in a browser, on this branch. The record below is the durable one: the
working evidence directory is a local planning artifact and is not part of the repository.

| Field | Value |
|---|---|
| Tree the round concluded on | `c3e761edb` (parts A and B ran on `beddd6bfd`+ and `f2562abed`+; the re-run added the merged storage-helper fix) |
| Daemon | a rootless Docker Engine daemon reached over its user socket, cgroup v2, runc only |
| Host | WSL2 with a 32 GiB-class NVIDIA GPU, **no** NVIDIA Container Toolkit |
| SPA origin | the Vite dev origin throughout — the Aspire app origin serves the last built bundle |
| Application under test | Odysseus, four services (odysseus, searxng, chromadb, ntfy) |

### Step results

| Steps | Area | Result |
|---|---|---|
| 1–9 | Host preflight, capability flip, build, loopback catalog server, engine start, navigation | PASS |
| 10–12 | Runtime card, daemon-identity change and acknowledgement, catalog listing and refresh | PASS |
| 13–18 | Install preview, cancel mid-pull, manifest-changed 409, install, readiness, open and sign in | PASS (13 and 16 after a fix, below) |
| 19–25 | Daemon-side projections, 95 policy assertions, container identity, storage write-through, restart policy, log bounds, secret scan | PASS |
| 26–30 | Stop/Start/Restart, configure while stopped and refusal while running, the configured value reaching the container, stale-version 409, update to a new digest with a newly required variable | PASS |
| 31 | A stopped container stays down across a daemon restart | **NOT-OBSERVED** |
| 32–35 | Applications serving with the engine down, `RestoredOnBoot`, foreign-container reporting, `StoppedUnexpectedly` through the observer | PASS (34 after a fix) |
| 36 | Reset, then uninstall | PASS on the re-run (the round's only FAIL, fixed) |
| 37 | Kill switch | PASS |
| 38–42 | Negative controls: tampered digest, `gpu: required`, insufficient memory, occupied preferred port, refresh-URL scheme | PASS |
| 43–45 | Measurements, evidence secret sweep, teardown | PASS |
| 46a | The node runs a 27B model on the GPU while the containers are up | PASS |
| 46b | An application reaching the node's model server | **NOT-OBSERVED** |
| 47 | Post-flip verification: the engine started with no `ExternalApps__Enabled` override | PASS |

Every step the plan marks **mandatory** — the runtime card and identity acknowledgement, install with the secret
prompt and cancel mid-pull, the daemon-side security assertions and the write probe, applications serving without
the engine, `RestoredOnBoot`, `StoppedUnexpectedly`, the kill switch, and the evidence secret sweep — is **PASS**.
No step is left FAIL.

### The post-flip verification

Step 47 is the leg that proves the shipped default, not an environment variable, is what turns the feature on. The
engine was restarted with **no** `ExternalApps__Enabled` and **no** catalog refresh URL, leaving only two isolation
variables that touch no External Apps configuration. Anonymous `GET external-apps/runtime` answered **401** rather
than 404 — the kill switch is request-path middleware ahead of authentication, so 401 is only reachable when the
family is live — and the same route with an operator bearer answered **200** with `status: Ready` and the pinned
daemon matching the observed one. In the browser the **External Apps** navigation group renders with both children
and the catalog page shows the runtime card as READY, against the **bundled seed**, because no refresh URL ships.

### The two NOT-OBSERVED steps

- **Step 31 — a daemon restart.** `systemctl --user restart docker` was refused by the session's permission layer,
  correctly: the daemon is shared with other checkouts whose real-daemon tests run against it, and a restart is the
  one action in the round that is not label-scoped. What was proven instead is the property underneath it — every
  container an operator stopped is `exited` with `Restarting=false` under an `unless-stopped` policy, and step 23
  showed the policy is identical before and after a Stop/Start with container ids unchanged. Not a mandatory step.
- **Step 46b — an application reaching XE's local model server.** Optional, and it produced a product-relevant
  finding rather than a gap in coverage. On this rootless daemon `host.docker.internal` resolves to an address that
  reaches nothing on the host, while `llama-server` binds `127.0.0.1` and `--host` is deliberately not overridable.
  Bridging to a non-loopback interface was refused, correctly, as it would put an unauthenticated model server on
  one. The install panel's "Local network … such as XE's local model server" sentence therefore describes a path
  that does not exist on rootless Linux; the wiki page says so.

### Fixes the round produced

| Commit | Step | What was wrong |
|---|---|---|
| `6f2dbd7ff` | 13 | Every `secret` variable rendered as a plain text box, so the admin password was printed in clear as it was typed. |
| `9bc96f3e0` | 13 | The dev-runtime `external-apps/` state directory was not ignored. |
| `70634cac6` | 16 | The shipped Odysseus entry could not install: the `searxng` image declares a second `VOLUME` the offline converter cannot see, so the post-start policy check refused an undeclared mount. All four images were audited; searxng was the only one affected. |
| `63cc5e9ea` | 34 | The runtime card never rendered `foreignInstallContainers`, so a machine holding another XE installation's containers looked identical to a clean one. The count comes from the refresh response, because the GET reports 0 on purpose. |
| `e8c5e64eb` | 36 | Reset failed and uninstall silently left data on disk under a rootless daemon: an application's own non-root user leaves `0700` directories owned by a host uid in the operator's subuid range, which the engine can neither traverse nor unlink. Fixed by deleting the volume contents from an engine-owned helper container. |
| `2ead47c45` | 36 | Test follow-up for the above: the helper's leftover count on uninstall, and the wiped subuid-owned subtree. |

A round is evidence only for the tree it ran on, so after each fix the affected steps plus the standing after-fix
set were re-run. The final re-run re-extracted every daemon-side projection and re-passed 95 of 95 policy
assertions; a negative control fell out of the ordering by accident and confirmed the projections are genuinely
re-extracted rather than carried over.

### Measurements

All measured on the host described above, on 2026-09-11. They are a snapshot, not a specification.

| Measurement | Value |
|---|---|
| Install, images already local | ~7 s |
| Update pulling one 370 MB image | 41 s |
| Reset | 11 s |
| Four containers resident at idle | ~1.28 GiB total |
| Manifest `minimumMemoryMb` | 4832 MB — an admission figure only, roughly 4x the measured idle footprint |
| Observer gap, container stopped to `StoppedUnexpectedly` | 3.19 s against a 15 s interval plus one push |
| Kill switch | 21 routes, 21 × 404, hub negotiate included |
| Frontend bundle after the feature | app 5.02 MB, lazy editor 2.97 MB |
| 27B Q4_K_XL on the GPU while the containers ran | 58.5 tok/s predicted, ≈20.3 GB VRAM |

### Evidence hygiene

The committed record carries no secret value, no raw container `Env`, `Cmd`, `Entrypoint` or `Args` object, and no
screenshot of an unmasked password field. Both password fills asserted `type="password"` programmatically before
submitting. Raw `docker inspect` output, container environments and log bodies never left the run's scratch
directory.

## Not implemented / deferred

| # | Item | Note |
|---|---|---|
| 1 | GPU passthrough | `gpu: required` is refused. Needs the NVIDIA Container Toolkit, `DeviceRequests`, the `gpuDevices` capability and a box to prove it on. |
| 2 | Outbound network restriction | V1 enforces none. Real enforcement changes the disclosure vocabulary and the panel wording, not just a flag. |
| 3 | Multi-instance | One instance per application; the 409 `ExternalAppAlreadyInstalled` is the seam to remove. |
| 4 | Podman | `IContainerRuntimeResolver` makes a second provider additive. |
| 5 | Catalog signing | V1 trusts curation plus HTTPS plus pinned image digests. |
| 6 | A second application | The real test of whether the manifest and the converter generalise. |
| 7 | An XE mirror image | The upstream GHCR images are used as published. |
| 8 | Measured resource figures in the manifest | The authored estimates are still estimates; the round's numbers above are the replacement input. |
| 9 | A published catalog repository | `ExternalApps:Catalog:RefreshUrl` ships **empty**: the planned XE catalog repository does not exist, so every node serves the embedded seed. Once published, set the URL and re-run the catalog-refresh and refresh-URL-scheme controls. |
| 10 | Windows and macOS | Not live-validated. Under Docker Desktop the identity built-ins resolve to `1000:1000` and the install write probe is the only proof the mount is writable. A pre-release follow-up. |
| 11 | A daemon restart with a stopped instance | Step 31; needs a daemon not shared with concurrent test runs. |
| 12 | Cancel reports as an unidentifiable failure | `ExternalAppFailureCategory` has no `Cancelled` member, so a cancelled operation settles `Unknown` and the panel tells the user to check the logs. Adding one ripples through persistence, the DTO, the SPA union and both locales. |
| 13 | Clearing a stored secret | The variables form renders a stored secret empty behind "leave empty to keep", so an empty save keeps the value. There is no signal for *removing* one once set. |
| 14 | The hub logs at Error for a gone instance | `ExternalAppHub.Subscribe` on a just-uninstalled instance is routine, not exceptional. |
| 15 | The detail page does not react to a completed uninstall | It stays on the instance until a manual reload. Reproduced twice, with server-side proof the row and directory were already gone. |
| 16 | A per-feature 400 `reason` handler | **Dropped** (operator ruling, 2026-09-11): an admission refusal is already a readable 400 message, and it leads with the `ExternalAppBlockedReason` name. A typed 400 member was judged not worth a new exception handler, a `Produces` declaration on ~8 routes and a client regen. The unread `Reason` member on `ExternalAppValidationException` was deleted with the ruling; only the install/update **preview** DTOs still carry a typed `blockedReason`. |
| 17 | Narrower `capAdd` for three services | Done for odysseus, chromadb and ntfy: each now carries what its entrypoint needs instead of Docker's full default set. `DAC_OVERRIDE` is kept on all three because the engine creates every bind mount `0700` and engine-owned, and on a **rootful** daemon the container's root is the host's root, still bound by those mode bits. Only a **rootless** daemon was live-tested, where the container's root maps to the engine's uid and the capability is not exercised; a rootful box is what would prove it. |
| 18 | The ntfy host-port publication | **Removed** (operator ruling, 2026-09-11). ntfy was published to `127.0.0.1:8091`; the follow-ups live round found nothing on the host reaching it. With Odysseus signed in and open, all 54 browser requests went to the Odysseus port, and the odysseus container carried no `NTFY` environment: ntfy is a server-side integration (`note_routes.py` posts a reminder to the base URL entered in Odysseus, and a phone app subscribes), so the loopback publication served only ntfy's own web UI. The service now declares no port and reaches nothing on the host; `NTFY_BASE_URL` is its own address on the instance network. Supersedes the S5 readiness reading (steps 17-18) that the browser subscribed directly. |
| 19 | The container suites need no daemon in CI | `XE-Local-AI-Engine.Testing.FakeDocker` serves the Engine API subset `DockerDotNetRuntimeClient` calls over loopback HTTP, and the production client is driven against it by `FakeDockerDaemonRouteTests`, `FakeDockerContainerRouteTests`, `FakeDockerNetworkAndExecRouteTests`, `DockerSandboxFakeServerTests` and the `FakeServer` arm of `ContainerRuntimeContractTests`. Eight ported cases and two duplicates left `ContainerRuntimeRealDaemonTests`; four left `DockerSandboxRealDaemonTests`. The rest stay real-daemon because a fake cannot falsify them — a flag actually honoured, a uid actually mapped, egress actually denied, a healthcheck reaching a verdict, and the daemon's own pull narration and exit-code status prose, which are the sentinels for Docker rewording either. All three real-daemon suites are now opt-in behind `XE_REQUIRE_DOCKER_TESTS=1`; `scripts/run-docker-smoke-local.sh` is the pre-RC gate, and `build-and-test.yml` pulls no image and sets no variable. **Deferred:** the two bidirectional exec-stream cases (`ALargePayloadDoesNotDeadlockAgainstTheChildsOwnOutput`, `WhenNoStandardInputIsSupplied_TheChildStillSeesEndOfInputRatherThanHanging`) stay real-daemon — the fake's exec stream is read-only, and modelling a genuine hijacked duplex with `CloseWrite` half-close semantics is its own slice. **Still real-daemon-only:** the anonymous-volume leak check, which needs a real `/volumes` listing. Its image is no longer pulled — `VolumeDeclaringImageFixture` builds it in the daemon from two lines of Dockerfile over the BusyBox base and removes it again, so the redis pin is gone from the tree. BusyBox remains the one pulled digest, because the pull-progress test needs real layers to count. |
| 20 | Any shipped application | **2026-09-12:** the curated entry was removed from the shipped catalog — a third-party application is not ours to ship. `catalog/external-apps/dist/applications.json` and the embedded seed are now `{"applications": []}`, so a node installs nothing until the catalog repository of item 9 exists. The manifest contract stays exercised by `XE-Local-AI-Engine.Client.Testing/ExternalApps/sample-catalog-manifest.json`, a test-only fixture the converter never produces. The 2026-09-11 live-round rows above record what that round actually installed and are left as they are. |
| 21 | A container could not reach XE's own local model server | **Closed** by the container bridge ([ADR 0011](../adr/0011-container-bridge-listener.md)). ADR 0010 shipped applications told they could reach "services on this computer, including XE's own local model server"; on a rootless daemon that was false, and item (a) of Wiki 23 §5 disclosed it as a caveat. The engine now opens one guarded non-loopback listener serving `/llm/v1/*` behind a same-host peer guard and a mandatory per-instance token, and injects `XE_BRIDGE_ENDPOINT` / `XE_BRIDGE_TOKEN` into every container. Verified live on a rootless daemon by `ContainerBridgeRealDaemonTests`; Docker Desktop is unit-tested only. Three limitations stand: an application that *discovers* model hosts cannot use the bridge unattended, because discovery probes are unauthenticated and the token is required on every route (the odysseus sample therefore surfaces the pair as `XE_MODEL_BRIDGE_URL` / `XE_MODEL_BRIDGE_TOKEN` for a one-time registration); an instance installed before the bridge carries no token and is not backfilled, so it gets bridge access by being reinstalled; and **only a rootless Linux daemon is validated** — on a rootful daemon a container's traffic to a local host address is never masqueraded, so it arrives with the container's own address and the peer guard refuses it (fails closed, so the bridge is dark rather than open). **Deferred: admit the subnets of engine-created networks in the bridge peer guard**, which is what makes a rootful daemon work; every admitted caller still presents a per-instance token. Not built here because no rootful host exists to prove it against. Rows 19 and 20 belong to the sibling batch's branches. |
| 22 | The update preview offered what the update refused | **Closed** (follow-ups kickoff 3, 2026-09-12). A manifest that reads `${XE_BRIDGE_ENDPOINT}`/`${XE_BRIDGE_TOKEN}` on a node with no open bridge previewed as updatable and the command answered 400; install was worse — it inserted the row, then failed in the pipeline and left a `Failed` instance. `DeploymentPlanner.RequiresBridge` (same token regex, same environment surface `Plan` substitutes) plus `ExternalAppService.BridgeUnavailableFor` feed a new `ExternalAppBlockedReason.BridgeUnavailable` into the shared `BlockingReason`, so both previews render it and both commands refuse before anything is written or stopped. Live: install and update dialogs both showed the reason with the action disabled on a bridge-less node. Remaining asymmetry at the time: Start/Restart had no admission check, so a bridge that closed after the install still failed inside the Start pipeline with the planner's message — closed in turn by row 26. |
| 23 | The permissions panel called a published port "Another address on this computer" | **Closed** (kickoff 3). A published port is the application reachable from this computer on a loopback port (`ApplicationContainerPolicy` binds every publication to `127.0.0.1`); "another address" describes `extraHosts`. Copy corrected in both locales; the derivation was already right and the panel has no host port to name. |
| 24 | `Stop` from `Failed` / `StoppedUnexpectedly` | **Closed** (kickoff 3). `AdmittedStatusFor` admitted it, `InstanceActions` offered Stop only while Running. Stop now follows the server rule (Restart stays Running-only); live, Stop from StoppedUnexpectedly tore the three surviving containers down. |
| 25 | The run-container digest guard refused a bare image id | **Closed** (operator ruling, kickoff 3). `ContainerImageReference.IsContentAddressed` accepts `<ref>@sha256:<64 hex>` (now anchored, one regex shared with the catalog validator) or a bare `sha256:<64 hex>` image id; the catalog validators still require the digest-pinned form, so no production caller reaches the bare-id path today. `VolumeDeclaringImageFixture` falls back to the image ID instead of skipping on a classic image store, so the real-daemon volume assertion is now made on every store. `IFreeSpaceProbe` moved to `Providers.Abstractions` in the same batch. |
| 26 | `Start`/`Restart` were admitted on a node whose bridge had closed | **Closed** (follow-ups kickoff 4, 2026-09-13). Install and update refused `BridgeUnavailable` at admission; `AdmitAsync` did not, so a bridge that closed after the install (a configuration change, a port collision at boot) admitted the command, flipped the row to `Starting`/`Stopping` and failed inside `StartInstanceAsync` on the unresolvable `${XE_BRIDGE_ENDPOINT}`/`${XE_BRIDGE_TOKEN}`, settling the instance `Failed` / `ConfigurationMissing`. `AdmitAsync` now asks `ExternalAppService.BridgeUnavailableFor` against the installed manifest snapshot for `Start` and `Restart` only — after `AdmittedStatusFor`, so an invalid transition still reads as one, and before `ApplyAsync`, so the row never leaves its status — and throws `Refuse(BridgeUnavailable, BridgeUnavailableDetail)`, the one composition and the one wording the install/update `Refuse` also uses, which a test pins as byte-identical to the install refusal. Stop, Reset, Uninstall, Cancel and Configure stay ungated on purpose: an operator must be able to shut down and clear an application on a node that lost its bridge. Out of scope and unchanged: a row with no bridge token on a node that HAS a bridge (row 21's reinstall remedy) and the boot `ExternalAppStartupReconciler`, which has its own pipeline. Live (rootless daemon, isolated node, the Odysseus document from a loopback catalog server): installed and Running with the bridge on; the node was restarted with `ContainerBridge__Enabled=false` and the same database. The boot reconciler settled the row `Failed` on its own path (`TryPlanForVerification` cannot plan the bridge token, summary "start it again to rebuild them"); Start then answered 400 `BridgeUnavailable: This application reads the node's container bridge…` as a toast from `Failed` and again from `Stopped`, the History tab recorded no `Start requested` between `Failed` and `Stop requested`, and the row's version did not move; Stop from `Failed` was admitted and settled `Stopped`; uninstall left no `xe-app` containers, networks or volumes. Restart-from-Running is unit-tested only: a bridge-less node cannot hold a Running bridge-needing instance long enough to ask it. **Open:** the reconciler's summary still advises the start it now refuses; settling that row with the planner's own bridge message instead is a small follow-up. |

## Updating this page

1. Verify claims against symbols and tests in the current tree; re-run the live controls that back a claim you
   change rather than editing the sentence.
2. Record the verification date above, and say which box and daemon a measurement came from.
3. Do not edit ADR 0010 unless the architectural decision itself is superseded or amended.
