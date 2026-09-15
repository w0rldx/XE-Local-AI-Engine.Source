# External Apps — Curated Containerised Applications

> Reviewed: 2026-09-15 · Code-grounded.

**External Apps** installs and runs a small set of curated, containerised applications on the node, so a user gets a
working application on their own machine without assembling a Compose file, a registry login and a reverse proxy by
hand. **The shipped catalog is empty**: it ships `{"applications": []}` until the XE-owned catalog repository exists,
so a node installs nothing today. The rich manifest the contract is exercised against is a test fixture,
`XE-Local-AI-Engine.Client.Testing/ExternalApps/sample-catalog-manifest.json`, which is never shipped.

The engine owns the containers: it pulls digest-pinned images, creates a private network per instance, publishes
ports on loopback only, keeps each instance's data in a directory it owns, and reconciles what it stored against
what the daemon actually holds.

The module spans the whole stack: `Client.Application/Services/Containers/` (the engine-owned container runtime
layer), `Client.Application/Services/ExternalApps/` and its `Catalog/` subfolder (the catalog, the deployment
planner, the container policy, the service pipelines, the reconciler and the observer), `Client.Persistence` (two
tables with per-column AEAD encryption), the `LocalApiRoutes.ExternalApps` route family plus `ExternalAppHub`, and
`Client.React/src/features/externalApps/`.

It is a **separate consumer class** from Development Mode's container sandbox, decided in
[ADR 0010](../adr/0010-external-apps-container-execution.md). Both talk to the same Docker daemon and share its
preflight probe and identity attestation; nothing else is shared. The sandbox SPI (`ISandboxRuntimeProvider`,
`DockerSandboxHardening`) has no ports, no networks, no healthcheck, no restart policy and no image pull, and its
hardening contract fails closed on exactly the things an installed application needs. External Apps does not widen
that contract — it stands beside it.

---

## 1. What it is, and what it is not

**What it is:**

- **Curated.** Applications come from an XE-authored catalog document, not from a user-supplied Compose file. The
  manifest schema is the allow-list: `ExternalAppCatalogValidator` rejects the whole document on any rule violation,
  so a catalog that half-parses never reaches the engine.
- **Digest-pinned.** Every service image must carry `@sha256:`. `ApplicationContainerPolicy.BuildSpecification`
  throws before anything is created if it does not.
- **Operator-gated.** `ExternalApps:Enabled` is read once at startup. The shipped `appsettings.json` sets it to
  `true`; the code default in `ExternalAppsOptions.Enabled` and `Program`'s `GetValue(…, defaultValue: false)` stay
  `false`, so a node with missing configuration fails closed.
- **One instance per application.** A second install answers 409 `ExternalAppAlreadyInstalled` (decision D13). The
  409 is the seam to remove if multi-instance is ever wanted.

**What it is not:**

- **Not on the inference path.** Docker is a soft dependency of this feature alone. Chat, embeddings, model
  acquisition and image generation never require a daemon, and with no daemon the runtime card is non-Ready and
  nothing installs — the designed degradation.
- **Not a general container host.** No user-supplied image, no user-supplied Compose, no arbitrary mount, no host
  network, no privileged container, no device passthrough.
- **Not a reverse proxy or a hosting platform.** Ports are published on `127.0.0.1` only; there is no TLS
  termination, no virtual host and no external exposure.
- **Not Development Mode.** Development Mode runs build/test/lint commands for a code work item under ADR 0004 and
  ADR 0007. Nothing there is migrated and nothing here replaces it.

---

## 2. The user model

The lifecycle as a user meets it, and what each action does.

| Action | What happens |
|---|---|
| **Browse** | The catalog page lists every application in the served document, with the runtime card above it. |
| **Preview permissions** | `GET …/catalog/{applicationId}/install-preview` returns the declared permissions, every variable, the resource verdict and the runtime resolution **before** anything is asked for. The install dialog discloses permissions first. |
| **Supply variables** | Required variables are prompted; `secret` variables are masked as they are typed and never ship a default. |
| **Install** | Admission is synchronous — the 202 body is the admitted row — and the pull, create and start run on the operation runner afterwards. The accepted `(manifestVersion, manifestSha256)` pair is echoed back; a catalog that moved underneath answers 409 `ExternalAppManifestChanged`. |
| **Open** | A published `ui` port composes a `http://127.0.0.1:<port><openPath>` link. |
| **Stop / Start / Restart** | Containers come down in reverse dependency order and back up in topological order. A lifecycle verb recreates nothing when the stored plan still verifies. |
| **Configure while stopped** | `PUT …/variables` is refused on a running instance (409 `ExternalAppInvalidTransition`). It sets `NeedsRecreate`; the value reaches the container on the next Start, which rebuilds from the stored variables and keeps the storage. |
| **Update** | `GET …/update-preview` shows the target version, the permissions the update **adds**, and target variables with current values masked. The update tears the instance down and rebuilds it against the target manifest, storage preserved. |
| **Cancel** | Cancels the in-flight operation; it settles to `Failed` with its **storage kept**. The one command that carries no `expectedVersion`. |
| **Reset** | *"This deletes everything the application has stored and starts it again from an empty state. Your settings are kept."* Confirm label: **Reset and delete data**. |
| **Uninstall** | *"This removes the application and permanently deletes everything it has stored. This cannot be undone."* Confirm label: **Uninstall and delete data**. Containers, network, row and the instance directory all go. |

Every mutating call but Cancel carries the row's `version` as `expectedVersion`; a stale one answers 409 rather
than acting on a row somebody else moved.

---

## 3. Architecture

Three layers, bottom up.

**`Services/Containers/` — the engine-owned runtime layer.** `IContainerRuntime` is the whole daemon surface this
feature uses: pull with progress, create, start, stop, remove, inspect, list, network create and remove, bounded log
read. `IContainerRuntimeResolver` answers *which* runtime and *is it usable*; `IContainerRuntimeFactory` builds the
client. `DockerDotNetRuntimeClient` is the only implementation, and `FakeDockerRuntimeClient` moves in lockstep with
it so the suites can drive the same guards without a daemon. The layer is deliberately inert on its own: nothing
resolves it until the External Apps service does.

**`Services/ExternalApps/Catalog/` — the catalog.** `IApplicationCatalogProvider` serves one
`ExternalAppCatalogSnapshot` from three sources in order of preference — a `Remote` document fetched from
`ExternalApps:Catalog:RefreshUrl`, a `Cache` copy persisted at `<node data>/external-apps/catalog-remote-cache.json`,
or the `Bundled` seed embedded in the application assembly. `ExternalAppCatalogValidator` runs on every one of them;
a document that fails any rule is rejected whole and the previous snapshot stands.

**`Services/ExternalApps/` — the application runtime.** `IExternalAppService` is the contract the endpoints call.
`ExternalAppOperationRunner` runs every mutating operation in its own DI scope, linked to `ApplicationStopping`, one
operation per instance at a time (`ExternalAppInstanceGate`). `DeploymentPlanner` turns a manifest plus stored
variables into a plan — token substitution, mount set, port set, topological order.
`ApplicationContainerPolicy` builds the container specification and verifies the daemon's read-back against it,
before and after start. `ExternalAppStorageLayout` is the only code that creates, materialises or deletes anything
under an instance directory. `ExternalAppStartupReconciler` is the boot pass (and the pass the runtime refresh
re-runs); `ExternalAppStateObserver` is the cheap poll that notices a container stopping behind the engine's back.

The endpoint surface is `LocalApiRoutes.ExternalApps` (20 routes plus the hub) and `ExternalAppHub`; both are
enumerated in [API & Hubs](09-api-and-hubs.md). The React feature is `externalApps`, described in
[React Client](10-react-client.md).

---

## 4. The manifest

One JSON document, `schemaVersion` 1, holding a list of `ApplicationManifest`. The engine never parses Compose.

`ApplicationManifest` carries identity and metadata (`Id`, `ManifestVersion`, `ManifestSha256`, `DisplayName`,
`Summary`, `Description`, `Homepage`, `License`, `Trust`, `TestedVersion`), a `Requires` list, `Permissions`,
`Resources`, its `Services` and its `Variables`. A service (`ApplicationService`) carries its digest-pinned image,
environment, `CapAdd`, `ReadOnlyRootFilesystem`, `Ports`, `Storage`, `Files`, `Healthcheck`, `DependsOn`,
`Entrypoint` and `Command`.

- **`requires[]`** must be a non-empty distinct subset of nine capability names: `containers`, `networks`,
  `bindStorage`, `loopbackPortPublishing`, `healthChecks`, `restartPolicies`, `logs`, `imagePull`, `gpuDevices`.
  A test asserts this list is ordinally equal to the runtime layer's own `ContainerRuntimeCapabilities.Names`, so a
  drift is a red test rather than a runtime "incompatible" answer to a valid manifest.
- **Variable types** are `string`, `secret`, `integer`, `boolean` and `enum`. A `secret` never ships a default
  (decision D11). The reserved prefix `XE_` is refused for any declared variable name.
- **Substitution.** An environment value may reference `${NAME}` for a declared variable or one of four built-ins:
  `XE_UID`, `XE_GID`, `XE_INSTANCE_ID` and the `XE_UI_HOST_PORT_<service>` family. A malformed token or an
  undeclared name is a validation error, never a silent empty string.
- **Assets.** `files[]` entries are inlined base64 bodies with their own sha256, capped at 64 KiB each, materialised
  read-only into the instance's `files/` tree. Their bodies never cross the wire on any catalog route.
- **The fingerprint binds acceptance.** `ExternalAppManifestFingerprint.Compute` is the lowercase-hex SHA-256 of the
  manifest's canonical JSON form — web naming, compact, nulls written, `manifestSha256` removed, every object's
  properties sorted ordinal at every level, arrays in order, UTF-8 without BOM or trailing newline. The canonical
  form is fixed because two languages compute it: this type and the Python converter. Install and update echo the
  `(manifestVersion, manifestSha256)` pair the operator was shown, so a catalog that changed underneath is a 409
  rather than an install of something nobody read.

---

## 5. Security posture

`ApplicationContainerPolicy.BuildSpecification` is the single place a container's security shape is decided, and
`FindViolations` re-reads the daemon's own view against it both before and after start. A mismatch is a
`PolicyViolation` failure, not a warning.

| Field | Value | Verified on read-back |
|---|---|---|
| `User` | not set — the image's default user applies | identity proven by a write probe, not by inspect |
| `CapabilitiesToDrop` | `["ALL"]` | yes |
| `CapabilitiesToAdd` | only what the manifest lists, bounded by Docker's own default 14 | yes |
| `SecurityOptions` | `no-new-privileges:true` + the repo's seccomp profile | yes |
| `Privileged` | false | yes |
| `PidMode` / `IpcMode` / `UtsMode` | not host | yes |
| `NetworkMode` | the instance's own `xe-app-<instanceId:N>` network | yes |
| Published ports | `127.0.0.1` only, and only ports the manifest declares | before and after start |
| Mounts | exactly the plan's set, nothing undeclared | yes |
| `ReadOnlyRootFilesystem` | whatever the service declares | yes |
| `RestartMode` | `unless-stopped`, set once at create and never rewritten | yes |
| `MemoryBytes` / `NanoCpus` | 0 — no ceiling | yes (a ceiling is a violation) |
| `PidsLimit` | the manifest's figure | yes |

Five facts a reader should not have to infer.

**(a) V1 enforces no outbound network restriction.** `permissions.internet` is always `true` and
`permissions.localNetwork` is a **disclosure**, not a control. The install panel says so in the product's own words
— "Internet access — yes", "Local network — this application can reach other devices on your network. It reaches
XE's local model server only through the engine's container bridge, using a token issued to this application." —
and nothing in the engine denies the LAN half. Do not read the panel as an enforcement boundary for egress. Real
egress enforcement is a deliberate follow-up.

*The model-server hop is the half that IS controlled, and it did not exist until the container bridge.* A container
has its own network namespace, so the host's loopback — where `llama-server` binds, with `--host` deliberately not
overridable — is unreachable from inside it on every daemon; on a rootless daemon `host.docker.internal` resolves to
a gateway address on which nothing of the host's listens, so the alias that papers over this on Docker Desktop
papers over nothing there. The engine now opens one guarded, non-loopback listener for exactly this hop
([ADR 0011](../adr/0011-container-bridge-listener.md), [Wiki 12 §3.5](12-security-and-privacy.md)): a same-host peer
guard, then a mandatory per-instance bearer token, then `/llm/v1/*` forwarded to the same model proxy the loopback
surface uses. The container is told where and with what through the built-ins `XE_BRIDGE_ENDPOINT` and
`XE_BRIDGE_TOKEN`. An application that *discovers* model hosts rather than being configured with one cannot use it
unattended — the token is required on every route and discovery probes are unauthenticated — which is why the
odysseus sample surfaces the pair as `XE_MODEL_BRIDGE_URL` / `XE_MODEL_BRIDGE_TOKEN` for the user to register once.
**Verified live on a rootless daemon** by `ContainerBridgeRealDaemonTests`. That is the whole of what is validated:
on a **rootful** daemon a container dialling one of the host's own addresses is not source-translated, so the peer
guard sees the container's own `172.x.y.z`, refuses it with 403 and the bridge is dark — expected, unproven in
either direction, and recorded in ADR 0011 with the intended remedy (admitting engine-created network subnets).
Docker Desktop is unit-tested only. The bridge is also IPv4-only, so a host with no IPv4 address opens none, and
there is **one bridge per machine**: the port is a fixed default so a container finds the same endpoint after a
restart, so a second node on the same box (another checkout, or a desktop node beside a dev one) finds the address
and port taken, logs a warning and boots without a bridge rather than failing to start. Give the node that should
have one a free `ContainerBridge:Port`.

A node without a bridge cannot plan a manifest that reads either built-in, and that is answered at **admission**
rather than discovered in the pipeline: `DeploymentPlanner.RequiresBridge` reads the same substitution surface and
the same token regex `Plan` does, and `ExternalAppService.BridgeUnavailableFor` pairs it with whether this node
opened a bridge. Both previews then report `BridgeUnavailable` and both commands refuse with it — so the catalog
page does not offer an install that would fail after the row exists, and the update dialog does not offer an update
that would fail after a working version had been stopped. `AdmitAsync` asks the same predicate for **Start** and
**Restart**, against the INSTALLED manifest snapshot rather than the catalog entry, after the transition table and
before the row's compare-and-swap: a bridge that closed after the install — a configuration change, a port
collision at boot — is refused with the same 400 wording instead of failing inside the pipeline and settling the
instance `Failed`. Stop, Reset, Uninstall, Cancel and Configure are deliberately not gated, so an operator whose
node lost its bridge can still shut the application down and clear it.

**(b) Containers may run as in-container root.** The engine passes no `--user`, deliberately: the curated images
start as root and drop privileges through their own entrypoints, and forcing a uid breaks that and breaks a port-80
bind. The boundary is the container, `cap_drop ALL`, seccomp, `no-new-privileges` and the loopback-only network —
not the uid.

**(c) `XE_UID` / `XE_GID` are daemon-resolved.** `ExternalAppContainerIdentity` answers `0:0` on a rootless daemon,
the engine's euid/egid on a rootful one, and `1000:1000` on Docker Desktop; `ExternalApps:ContainerIdentity`
overrides it as `uid:gid` for a daemon neither rule describes. Because `inspect` cannot show a mapping, **every
install runs a write probe** through the engine-created mount and fails the install if it cannot write.

**(d) The `capAdd` ceiling is Docker's own default set of 14** — `AUDIT_WRITE`, `CHOWN`, `DAC_OVERRIDE`, `FOWNER`,
`FSETID`, `KILL`, `MKNOD`, `NET_BIND_SERVICE`, `NET_RAW`, `SETFCAP`, `SETGID`, `SETPCAP`, `SETUID`, `SYS_CHROOT`.
With `cap_drop ALL` applied first, a service can never exceed an unhardened `docker run`. Anything outside the table
is a validation error in the catalog and a `ContainerPolicyException` at build time.

**(e) V1 sets no memory and no CPU ceiling.** The manifest's `minimumMemoryMb` and `recommendedMemoryMb` gate
**admission** only — `ExternalAppResourceGate` refuses an install on a box with too little free memory or disk — and
`pidsLimit` is the one per-container limit that is actually imposed, as a fork-bomb guard. A runaway application can
therefore consume as much memory and CPU as the host will give it, and the operator's remedy is Stop, not a cgroup.

**The daemon is shared.** Ownership is never inferred from the container name: `xe-app-<instanceId:N>` would repeat
across two XE installations pointed at the same daemon. Every container and network carries four labels —
`com.xe-local-ai-engine.owner=external-apps`, the per-installation `…install` id, `…external-app.instance` and
`…external-app.service` — and the reconciler counts owner-labelled containers whose install label is missing or
different as **`foreignInstallContainers`**. It reports them and **never removes them**.

**Secrets.** A `secret` variable is the user's own credential. Values live in the single AEAD-encrypted column
`external_app_instance_variables_json`, bound by AAD to the row's own id. They are masked on the way out by
`ExternalAppVariableMask` and kept on the sentinel on the way in, so a save that echoes the mask preserves the
stored value. Any other value is written, the empty string included, so clearing a stored secret is a deliberate act
in the UI: the shared `StoredSecretInput` sends the sentinel back for an emptied box and only its explicit Clear
control sends the empty string. `ApplicationManifest` suppresses its own record `PrintMembers`, so a stray
structured log of a snapshot cannot print the catalog or a variable default. `FailureSummary` is content-free by
contract: category prose, a service name, and the resource gate's requested-versus-available figures — never a
variable value and never a daemon message.

**The socket is root-equivalent.** Anything that can reach the Docker socket can reach the host. That is the whole
subject of [ADR 0010](../adr/0010-external-apps-container-execution.md), and this feature is its **second**
consumer class after Development Mode's sandbox. It does not widen the grant; it uses the same socket for a
different workload under its own policy.

**Catalog trust is XE-curated + HTTPS + digests.** There is **no signing in V1**. A configured
`ExternalApps:Catalog:RefreshUrl` must be `https://`, with plain `http://` accepted only for `127.0.0.1`, `::1` and
`localhost`; anything else logs one Error and serves bundled-only. Redirects are not followed.

---

## 6. Lifecycle and restore semantics

`ExternalAppInstanceStatus` has ten members. Six are **transient** — `Installing`, `Starting`, `Stopping`,
`Updating`, `Resetting`, `Uninstalling` — and accept no command but Cancel. The four settled ones are `Stopped`,
`Running`, `Failed` and `StoppedUnexpectedly`. `ExternalAppDesiredState` is separate and holds what the user last
asked for, independent of where the instance currently is.

**The restart policy is created once and never rewritten.** Every container is created `unless-stopped`, in both
desired states, and no lifecycle verb touches it afterwards (proven live across a Stop/Start pair, container ids
unchanged). `unless-stopped` is what keeps a container an operator stopped from coming back on a daemon restart,
while one that was running does come back.

**The boot pass.** `ExternalAppStartupReconciler` runs at startup and again on `POST …/runtime/refresh`, with one
verdict per row:

1. A row in a **transient** status is an operation the host died inside. An interrupted uninstall is completed in
   the pipeline's order; every other transient status settles to `Failed` and is never resumed — safe only because
   the storage is kept, so the user's next action is an ordinary reset or uninstall.
2. A **settled** row is judged on its desired state and what the daemon actually holds, never on the status it was
   left with. An instance whose containers survived a crash is adopted — after verification — and gets a
   `RestoredOnBoot` event.
3. Anything the daemon holds **under this installation** that no row claims is an orphan and is removed with its
   network. Containers under a *different* install label are counted, never touched.

Two things it deliberately does not do: it never touches an instance a live operation holds, and it writes nothing
at all when no runtime is ready — rewriting every row to `Failed` would destroy the evidence the next pass needs.

**The observer.** `ExternalAppStateObserver` polls every `ExternalApps:ObserverIntervalSeconds` (default 15), one
scope and one detailed list call per tick, and moves a running instance whose container is missing or listed
non-running to `StoppedUnexpectedly`. It starts, creates and removes nothing, and skips any instance with a live
operation. The detailed listing is load-bearing: `docker stop` leaves the container *listed*, so an id-only poll
would see no change.

**Applications keep serving while XE is not running.** Stopping the engine does not stop the containers; they are
long-lived daemon-managed containers, which is the point. Two consequences follow for the kill switch:

- Setting `ExternalApps__Enabled=false` and restarting makes every route and the hub negotiate answer **404**
  again, at the request-path middleware ahead of the security middleware, so the switch cannot be probed by status
  code. It does **not** stop anything on the daemon: instances keep running under `unless-stopped` with no UI left
  to stop them. **The order is therefore stop (or uninstall) every instance, then disable.**
- The SPA does **not** hide the navigation group. `nodeCapabilities.externalApps` is compile-time; the group stays
  visible and its pages render an error state against the 404s. Hiding it needs a **build**.

---

## 7. Runtime selection

The node setting is `containerRuntimeSelection`, a **string** on the wire — the enum stays internal, so a JSON
number never binds. Values are accepted case-insensitively against an allow-list and read back lower-case as
`auto` or `docker`; an unknown name answers 400. `ExternalAppInstance.RuntimeOverride` exists as a per-instance
override column and **V1 never populates it** — the omission is a decision, not an oversight.

`ContainerRuntimeStatus` has seven members: `Ready`, `DaemonUnreachable`, `PermissionDenied`, `ApiVersionTooOld`,
`DaemonIdentityChanged`, `NotConfigured` (unreachable in V1, kept for parity with the preflight enum) and
`ProbeFailed`. The runtime card distinguishes **available** (a daemon answered) from **ready** (it is also the one
this node approved), which is why `DaemonIdentityChanged` reads as available-but-not-ready rather than as an
outage.

**The daemon identity pin.** The first successful probe pins the daemon's id, shared with Development Mode's
attestation store. A daemon whose id no longer matches is refused until an operator acknowledges it:
`POST …/runtime/refresh` carries `acknowledgeDaemonId`, which must equal the id **currently observed**. A wrong or
stale id answers 400. It is a POST precisely so a refresh, a prefetch or a health check cannot approve whatever
daemon happens to be answering. Only local transports are accepted, and an endpoint carrying URI user information
is refused before it is probed or logged.

---

## 8. Diagnostics

**The runtime card** (`RuntimeStatusCard`, catalog page) is the one place the container runtime is named: the
resolved provider, the status, the capability set, the observed and pinned daemon, the operator confirmation when
one is required, and the foreign-container count. That count is read from the **refresh response**:
`GET …/runtime` reports `foreignInstallContainers: 0` on purpose, because only the refresh reconciles and a GET
reporting a cached count as a fresh observation would claim a foreign container is present when it is not.

**The event feed.** `external_app_instance_events` is append-only and the sequence is minted inside the same
transaction as the status change, so the feed has no holes and no reservations. `GET …/events` pages by an
**exclusive** `afterSequence` lower bound, ascending. `ExternalAppHub.Subscribe(instanceId, afterSequence)` joins
the per-instance group and then reads the replay from the same store, so the database is the replay authority; the
`externalAppChanged` push is a notification, never a payload — the client re-reads from its own watermark, so a
dropped push is a late read rather than a wrong render. `externalAppPullProgress` is the one message that *is* a
payload, because there is no row behind it to re-read.

**Logs.** `GET …/logs?service=&tail=` reads a bounded tail from the daemon and persists nothing. A `tail` above
`ExternalApps:MaxLogTailLines` (default 2000) is **rejected**, never clamped — a silently clamped `tail=100000`
reads as a truncated log. The text is the application's own container output and is **unmasked by design**: it is
not an engine-owned value, and masking somebody else's log would be a lie about what the container printed.

**Failure categories.** `ExternalAppFailureCategory` is a closed vocabulary keyed on the failing *phase*, not on
daemon prose.

| Category | What a user should do |
|---|---|
| `RuntimeUnavailable` | Start the container runtime, then use Check again on the runtime card. |
| `RuntimeIncompatible` | The daemon is too old or lacks a capability the application needs; upgrade it. |
| `GpuNotSupported` | Nothing — XE does not offer a GPU to applications yet. |
| `InsufficientMemory` / `InsufficientDisk` | Free memory or disk and retry; the summary carries the two figures. |
| `ImagePullFailed` | Check connectivity and retry; the digest may also have been withdrawn upstream. |
| `ConfigurationMissing` | Supply the missing variable in Configure, then Start. |
| `PolicyViolation` | Report it: the daemon applied something the engine refuses. Not user-repairable. |
| `PortUnavailable` | Free the port or retry — the allocator will take an ephemeral one. |
| `HealthCheckFailed` | Read the container logs; the application itself did not come up. |
| `StoppedUnexpectedly` | Start it again; something stopped the container outside XE. |
| `StorageError` | Check the node data directory's permissions and free space. |
| `Unknown` | Read the logs. A cancelled operation currently lands here (see below). |

Two known rough edges, both recorded rather than fixed: a **cancel** settles with `Unknown`, so the panel says XE
could not identify the problem above the cancellation sentence — a `Cancelled` member would ripple through
persistence, the DTO, the SPA union and both locales — and `ExternalAppHub.Subscribe` logs at Error for an instance
that is already gone, which a browser tab left open on a just-uninstalled instance produces routinely.

**Windows and macOS were not live-validated.** The identity built-ins resolve to `1000:1000` under Docker Desktop
and the write probe is the only proof the mount is writable there. That is a pre-release follow-up, not a claim
this page makes.

---

## 9. Storage, and the helper container

One directory per instance, under the node data directory (or `ExternalApps:InstanceRoot`):

```
<root>/external-apps/instances/<instanceId:N>/
  volumes/<service>/<storage-name>/    writable, bind-mounted
  files/<service>/<name>               read-only assets materialised from the manifest
```

Both halves are namespaced **by service**, because `storage[].name` is unique only within a service — an application
can declare `data` on two of them, as the sample manifest fixture does — so a flat layout would bind one host
directory into two containers holding different data. Directories are created `0700`. Lexical confinement is not
confinement: every component from `external-apps` down is checked for a symlink before it is created, written or
handed out as a bind source, and every write goes through a temp file opened `CreateNew` rather than an in-place
overwrite.

**Why an engine-owned helper container deletes the contents.** An application's in-container user is not the engine.
Under a rootless daemon a service that creates `0700` directories as, say, uid 977 leaves them owned by a host uid
inside the operator's subuid range — a uid the engine can neither traverse nor unlink. Reset and uninstall therefore
run a short-lived helper: the digest-pinned `ExternalApps:StorageHelperImage` (BusyBox by default, and the validator
refuses anything not pinned with `@sha256:`), named `xe-app-<instanceId:N>-storage-helper`, carrying the instance's
three labels plus `…external-app.helper=storage-wipe` and **no service label**, so nothing that keys containers by
service can mistake it for one of the application's own. It gets exactly one bind mount — that instance's `volumes`
directory — a read-only root filesystem, no network, no environment, `no-new-privileges`, seccomp, a 64-process
limit, and it is removed in a `finally` within the same operation.

It is the one container here that keeps **Docker's own default capability set** rather than `cap_drop ALL`.
`CAP_DAC_OVERRIDE` is in that set and is the point: without it, in-container root cannot traverse the `0700`
directories it exists to remove, and `rm` exits 0 having deleted nothing. The engine removes the emptied tree
host-side afterwards and counts what is left rather than trusting the exit code.

---

## 10. Catalog authoring

`catalog/external-apps/` is a non-project authoring folder, not a `.csproj`. Per application it holds the
byte-verbatim upstream `compose.yml` pinned to a commit, a `variables.json` classifying every upstream environment
name, a `manifest.overrides.json` with metadata, resolved digests and their provenance, and any catalog-shipped
asset under `files/`. `tools/build_catalog.py` converts them offline into `dist/applications.json`, which is copied
byte-for-byte to the embedded seed under `Services/ExternalApps/Catalog/`; a test asserts the two are identical, so
hand-editing either is a red build. The converter fails on an unclassified environment key, an unclaimed compose
mount or port, and on a claimed mount or port the compose no longer declares — so an upstream bump turns the build
red instead of silently drifting.

A digest is resolved once, recorded with its tag, the command and the date, and pinned. The full authoring
procedure is `catalog/external-apps/README.md`; this page does not restate it.

**Distribution.** The shipped `ExternalApps:Catalog:RefreshUrl` is **empty**, so a node serves the embedded seed
until an operator configures a URL. The planned XE-owned catalog repository does not exist yet.

---

## Related pages

- [Architecture Overview](01-architecture-overview.md) — where the module sits in the host.
- [API & Hubs](09-api-and-hubs.md) — the `external-apps/*` route family, `ExternalAppHub` and the event contract.
- [React Client](10-react-client.md) — the `externalApps` feature.
- [Data & Persistence](08-data-and-persistence.md) — the two tables and the encrypted variables column.
- [Security & Privacy](12-security-and-privacy.md) — §7 for the container policy in the security narrative.
- [Testing & Validation](13-testing-and-validation.md) — the fake Docker server that covers the wire shape in CI, and the opt-in real-daemon suites behind `XE_REQUIRE_DOCKER_TESTS=1` / `scripts/run-docker-smoke-local.sh`.
- [Project Layout](02-project-layout.md) — `catalog/external-apps/` as a non-project folder.
- [ADR 0010](../adr/0010-external-apps-container-execution.md) — the decision this module implements.
- [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md) and
  [ADR 0007](../adr/0007-sandbox-execution-substrate-and-backend-selection.md) — the sandbox records this one stands
  beside rather than amends.
- [External Apps implementation status](../roadmaps/external-apps-status.md) — the living record of what is built
  and what is deferred.
