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
| 16 | A per-feature 400 `reason` handler | Deferred, not dropped: the validation family answers with the generic problem body, and the typed `reason` member exists on the service contract only. It moves the wire contract on every External Apps 400, so it must not ride along with an unrelated change. |
| 17 | Narrower `capAdd` for three services | Three of the four services carry Docker's full default set because upstream declares `cap_add` only where it wants something narrower. Each should be run down to what it needs. |

## Updating this page

1. Verify claims against symbols and tests in the current tree; re-run the live controls that back a claim you
   change rather than editing the sentence.
2. Record the verification date above, and say which box and daemon a measurement came from.
3. Do not edit ADR 0010 unless the architectural decision itself is superseded or amended.
