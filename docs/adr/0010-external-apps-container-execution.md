# ADR 0010: User-managed application containers from the XE catalog are a separate consumer class with their own runtime layer

- **Status:** Proposed — the repository owner records acceptance with their own name and date at merge, as ADR 0007 did.
- **Date:** 2026-09-05
- **Scope:** How the engine runs curated, user-installed application containers. It changes nothing a sandbox backend
  enforces and does not touch the sandbox SPI.
- **Authority:** Operator rulings D1–D14 recorded with the External Apps brief (`00-brief.md` §2), and the
  reconciliation rounds that closed it.
- **Amends:** nothing.

## Context

The product is asked to install and run a small set of curated, containerised applications — the first is Odysseus —
so that a user gets a working application on their own machine without assembling a Compose file, a registry login and
a reverse proxy by hand. Such an application is long-lived, listens on a port, keeps data between restarts, and is
composed of several images that must find each other by name.

Two records already govern containers here, and neither covers this.
[ADR 0004](0004-development-mode-container-execution-docker-stopgap.md) scopes Docker to "Development Mode build/test/lint
execution. Nothing else," and its §5 rejects repository-supplied container configuration wholesale.
[ADR 0007](0007-sandbox-execution-substrate-and-backend-selection.md) narrows that permit further: a container backend
serves exactly one workload, and only because that workload declares an engine-approved image-backed toolchain it
cannot get from the host.

The sandbox SPI cannot express a hosted application either, and the gap is structural rather than a missing field.
`SandboxCreateRequest` has no ports, no networks, no healthcheck, no restart policy and no image pull; the Docker
hardening contract asserts zero devices, no added capabilities and a read-only root filesystem, and
`DockerSandboxHardening.FindViolations` treats every one of those as a fail-closed check on read-back. An application
that publishes a port and writes to a data directory violates that contract by construction. Widening the contract to
admit it would remove the property the contract exists to hold, and would break the guard tests
(`SandboxContractGuardTests`, `SandboxSubstrateSelectionArchitectureTests`) that make its narrowness enforceable rather
than aspirational.

So the choice is between weakening one boundary to serve two unlike consumers, or standing up a second boundary for the
new one. This record takes the second.

## Decision

1. **A user-managed application container from the XE-curated catalog is a distinct consumer class**, separate from the
   agent-execution sandbox of ADR 0004 and ADR 0007. It has its own layer, its own policy and its own tests, and it
   shares no contract with the sandbox beyond the Docker transport client.

2. **Its input is an XE-owned, versioned JSON manifest from an XE-controlled catalog** — never a repository file, never
   a Compose document, and never anything a user or an agent can write. The manifest schema is the allow-list: a field
   the schema does not define cannot reach the daemon, because nothing reads it.

3. **It runs on a new engine-owned runtime layer beside `ISandboxRuntimeProvider`, not inside it.**
   `SandboxRequirements`, `SandboxWorkloads` and `SandboxProviderSelector` are untouched, and
   `DockerSandboxRuntimeProvider` is not migrated. An architecture test asserts that no type in the External Apps
   feature reaches into Development Mode's container surface.

4. **The catalog's trust tier is "reviewed by this product's maintainers and pinned by digest."** That replaces
   ADR 0004 §5's wholesale rejection of externally-supplied container configuration for this class alone, and for no
   other. It is not signature-verified. Standing in for a signature are: HTTPS from one pinned repository, `@sha256:`
   on every image, a fixed capability allow-list, engine-owned policy defaults, and full disclosure of every elevated
   permission before install.

5. **The permissions beyond the sandbox baseline are exactly these:** a port published on `127.0.0.1`, an
   engine-created bridge network per instance, engine-owned bind storage under the node data directory, a healthcheck,
   an engine-owned restart policy, and capabilities drawn from an allow-list that is **Docker's own default set**
   (`AUDIT_WRITE, CHOWN, DAC_OVERRIDE, FOWNER, FSETID, KILL, MKNOD, NET_BIND_SERVICE, NET_RAW, SETFCAP, SETGID,
   SETPCAP, SETUID, SYS_CHROOT`). `cap_drop ALL` still applies and only declared entries are added back; anything
   outside the list is a validation error. A service therefore never exceeds what a plain `docker run` grants.
   Everything else stays forbidden: no privileged containers, no host namespaces, no socket mounts, no devices, no
   image builds, no host-file access.

6. **The container runs as the image's default user; the engine passes no `--user`.** The curated images start as root
   inside the container namespace and drop privileges through their own entrypoints (`PUID`/`PGID`, `su-exec`);
   forcing a uid breaks them, and breaks a port-80 bind. Application containers may therefore run as in-container root.
   The boundary is the container, its dropped capabilities, the engine seccomp profile, `no-new-privileges` and the
   loopback-only network — **not the uid**.

7. **The daemon-resolved host identity reaches the manifest as `XE_UID`/`XE_GID`:** `0:0` under a rootless daemon, the
   engine's euid/egid on a rootful Linux daemon, `1000:1000` on Windows or macOS Docker Desktop, with an operator
   override winning over all three. The rootless claim is a premise, not a construction, so every install runs a
   write-probe through the first started container's data mount and fails, naming the daemon mode, when the premise
   does not hold.

8. **Nothing is silently approved.** Every elevated permission is declared and shown before install, and a permission
   that widens on update needs an explicit acknowledgement. This is ADR 0006's rule applied to this consumer class.

## What this amends, and what it deliberately does not

**Unamended — ADR 0004 §2.** No Docker on the inference path. Model hosting, acquisition, embedding, image generation
and every chat-path provider stay container-free, and this class adds no consumer there.

**Unamended — ADR 0004 §3.** MXC remains the long-term hard-isolation seam. This record adopts no new isolation
technology; it adds a consumer to the one that is already present.

**Unamended — ADR 0004 §4.** The process sandbox and its hardening plan are untouched.

**Amended for this class only — ADR 0004 §5.** Repository-supplied container configuration stays rejected wholesale.
What this record permits is not repository-supplied: it is an XE-owned manifest from an XE-controlled catalog,
reviewed by the maintainers and pinned by digest, and it reaches the daemon only through a schema that is itself the
allow-list. Development Mode's own rule is unchanged.

**Unamended — ADR 0007.** The requirements/selector vocabulary is neither changed nor used. This layer does not
register an `ISandboxRuntimeProvider`, declares no `SandboxWorkloads` entry, and is invisible to
`SandboxProviderSelector`.

**Unamended — ADR 0006.** Its disclosure-and-acknowledgement rule is applied here, not modified.

## Non-goals

- **A remote daemon.** V1 accepts `unix://` and `npipe://` only. A `tcp://` daemon hosts the user's data on another
  machine, where loopback publishing and an engine-created bind mount mean something else entirely, and it would
  receive the deployment's secrets before the write-probe could refuse it.
- **Enforcing outbound network restrictions.** Each instance gets its own engine-created bridge network and nothing is
  reachable from off-box, but a container may still reach the LAN and the internet. `Internal = true` would break the
  very applications this catalog ships: they update themselves, fetch models, and call third-party APIs. So V1 states
  this as a **limitation with disclosure, not enforcement** — the permissions panel says what an application can
  reach, and never claims a denial the engine does not perform. An internal network plus a declared egress allow-list
  is a deliberately deferred post-V1 decision.
- **GPU devices.** `permissions.gpu: required` fails install; no device request is ever built.
- **Compose parsing in the engine**, in any form, including `build:`.
- **A second runtime provider.** Podman is a future provider behind the same interface; nothing here adopts one.
- **A general container administration surface**, user-authored definitions, LAN or public exposure, catalog signing,
  or agent and MCP access to any of this.

## Consequences

Stated honestly, including the ones that are costs.

- **Docker socket access is root-equivalent on Linux.** ADR 0004 documented rather than mitigated this, and the posture
  is inherited unchanged. This record widens what that socket is used for; it does not widen what the socket grants.

- **Application images are large and pulled over the network.** First install is slow and can fail offline. That
  failure is named `ImagePullFailed` and surfaced, rather than hidden behind a generic error.

- **Application containers may run as in-container root.** Decision §6 is a deliberate reduction relative to the
  Development Mode sandbox, which pins a non-root uid. What replaces it is the rest of the boundary — dropped
  capabilities, seccomp, `no-new-privileges`, no devices, and a network reachable only from loopback — and the fact
  that the images are curated and digest-pinned rather than user-supplied.

- **Under a rootless daemon the in-container uid maps to a host subuid**, so in-container root is the identity that can
  write an engine-created bind mount. The layer reports rootlessness, and the per-install write-probe proves the
  mapping instead of assuming it. Windows and macOS take `1000:1000` and are a **recorded pre-release follow-up**, not
  a gate on the Linux validation round.

- **Applications keep running while XE is stopped.** The restart policy is `unless-stopped`, created once and never
  changed, and it belongs to the daemon. A container the user stopped stays stopped across a daemon restart, and a
  boot reconcile re-establishes the engine's view of what is running. A user who wants an application off must stop the
  application, not the engine.

- **A substituted daemon now substitutes the host of the user's persistent application data**, not only of a build. The
  trust-on-first-use attestation is therefore reused unchanged: a single unkeyed pin shared with Development Mode. With
  both endpoint settings unset — the shipped default — both consumers reach the same daemon. Divergent explicit
  endpoints produce a confirmation ping-pong between the two consumers, which is an annoyance rather than a bypass;
  keying the pin by endpoint is the fix if that is ever supported.

- **The lying fake and a real-daemon suite are required by this record, not optional.** A read-back verification whose
  failure branch is unreachable in tests is a verification on paper only, and a daemon cannot be asked on demand to
  drop a setting it was given.

- **Revisiting is expected.** An egress allow-list, a second runtime provider, per-service uid pinning and catalog
  signing are each a new operator decision, not an edit to this record.
