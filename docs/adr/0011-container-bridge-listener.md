# ADR 0011: An application container reaches the node's own inference surface through one guarded, non-loopback listener

- **Status:** Proposed — the repository owner records acceptance with their own name and date at merge, as ADR 0010 did.
- **Date:** 2026-09-12
- **Scope:** How a curated application container installed under [ADR 0010](0010-external-apps-container-execution.md)
  calls this node's local model server. It changes nothing about how such an application is installed, hardened,
  stored or reconciled.
- **Authority:** Operator rulings recorded with the C1 brief (`KICKOFF-FOLLOWUPS-2.md` §2) and the implementation
  rulings that closed it.
- **Amends:** nothing. It closes a gap ADR 0010 disclosed rather than solved.

## Context

ADR 0010 shipped applications that are told they can reach "services on this computer, including XE's own local
model server". On the maintainer's own box that sentence was false, and the wiki said so as a caveat rather than a
bug: under a **rootless** daemon `host.docker.internal` resolves to a gateway address on which nothing of the
host's is listening, `llama-server` binds `127.0.0.1`, and its `--host` is deliberately not overridable. A container
has its own network namespace; the host's loopback is not reachable from inside it on any daemon, and the alias that
papers over this on Docker Desktop papers over nothing on rootless Linux.

So the engine serves its whole surface — the SPA, `/api/local/v1`, the SignalR hubs, the MCP endpoint — on a
listener no application container can reach, and the one thing an application most wants from the engine is the one
thing it cannot have.

Three properties have to survive whatever fixes that.

1. **The local API stays loopback-only.** `LocalApiSecurityMiddleware` rejects a non-loopback peer before
   authentication, `LoopbackBindGuard` shuts the process down if the server bound a routable address, and the
   anonymous first-run setup endpoint lives behind both. Nothing here may weaken that.
2. **`llama-server` keeps binding loopback.** Its `--host` is not an operator knob, and making it one would expose a
   model server with no authentication of its own to the LAN.
3. **An application must not be able to use another application's access.** Applications are mutually untrusted;
   they share a host and, transitively, a daemon.

## Decision

The engine opens **one** additional Kestrel listener, the *container bridge*, on a LAN-facing host address. It is
the only non-loopback listener the product has, it serves only an OpenAI-compatible forwarding surface under
`/llm/v1/*`, and it is guarded by two independent controls in front of everything else on it.

**Where it binds.** `ContainerBridgeEndpointResolver` picks the first interface that is up, is not loopback, is not
a tunnel, has a gateway and carries IPv4. `ContainerBridge:BindAddress` overrides the detection outright and refuses
a wildcard, because the startup bind guard is handed exactly one address and a wildcard is not one address. A host
with no qualifying interface opens no bridge and logs one warning; it never fails startup.

**One bridge per machine.** The port is a fixed default and stays one: a container is handed
`XE_BRIDGE_ENDPOINT` when it is created, and has to find the same port after the node restarts, which a randomised
or persisted-per-node port cannot promise. The consequence is that a SECOND node on the same machine — another
checkout, or a desktop node beside a dev one — resolves the same address and port as the first. Because the bridge
URL is appended to the SAME bind list as the loopback listener, an unbindable bridge fails the whole host, so the
second node would lose its UI and API rather than just its bridge. `ContainerBridgeListenerProbe.IsPortAvailable`
therefore binds the exact address and port with a short-lived socket before the URL is appended, and a node that
cannot have it logs one warning and boots with its loopback listener alone. The operator gives the node that
should have a bridge a free `ContainerBridge:Port`. The same function refuses a bridge whose port is already one of
the host's own bind URLs (`CollidesWithHostingUrls`), which the socket probe cannot see because the node has not
bound its own listener yet at that point in startup.

The bridge is **IPv4-only**, in both paths: an IPv6 value in `BindAddress` is rejected rather than used, and a host
with no IPv4 address anywhere opens no bridge. The container networks the engine creates are IPv4, and the bridge is
the hop a container on one of them makes to the host. A configured address **this host does not own** is rejected
too, and for the same fail-open-on-boot reason as the rest: Kestrel cannot bind an address no interface carries and
fails the whole host when it tries, so a typo would otherwise cost the node its boot instead of its bridge.

**What a container is told.** `ContainerBridgeGrant` carries the container-facing `host:port` and the instance's own
token as one value, so neither can be injected without the other. On a Linux daemon the endpoint is the bound
address itself; on Docker Desktop it is `host.docker.internal`, because there the daemon lives in a VM whose idea of
the host's address is not this machine's. That is a claim about the address a container *dials*; whether the
connection is then admitted depends on the source address the daemon gives it, which is where rootless and rootful
differ — see the consequence below. `DeploymentPlanner` injects both as the built-ins
`XE_BRIDGE_ENDPOINT` and `XE_BRIDGE_TOKEN`, at install and again on every Start that rebuilds containers.

**Who may connect.** `ContainerBridgePeerGuardMiddleware` runs first on the bridge branch and refuses, with 403,
any peer that is not one of this computer's own addresses. `ContainerBridgeAddressWatcher` keeps that set current on
a timer, because a container daemon creates and destroys bridge interfaces while the node runs and a set frozen at
boot would refuse a container that came up on an interface created later.

**Who may call.** `ContainerBridgeTokenMiddleware` then requires the per-instance bearer token on **every** bridge
route. The peer guard admits any container on an engine-owned network; this is what stops one of them using
another's bridge. The token is `<instance id>.<256 bits of CSPRNG>`, so verification is one keyed row read rather
than a comparison against every installed application's secret, and the whole token is compared in constant time so
an id rewritten to name another instance cannot match that instance's row. Every refusal is byte-identical: a caller
able to tell "no such instance" from "wrong secret" could enumerate this node's installs.

**What it serves.** `POST /llm/v1/chat/completions`, `POST /llm/v1/embeddings` and `GET /llm/v1/models`, mapped onto
the **same** `LocalModelProxyForwarder` the loopback model proxy uses — not a second copy of it. The branch is the
first thing in the application pipeline and its predicate is the connection's whole local end — address AND port,
through `ResolvedContainerBridgeEndpoint.Matches` — so nothing else the host serves is reachable on the bridge
listener, and nothing mapped inside it is reachable on the loopback one. The address half is not decoration: a
desktop launch given `--port 18790` would put the node's own listener on the bridge's port, and a port-only
predicate would route every SPA and API request into the bridge branch. Such a node opens no bridge at all, and
the peer guard reads the same predicate, so the branch and the guard cannot disagree about which listener a
connection arrived on.

**Two existing guards had to be told about it.** `LoopbackBindGuard` takes an allow-list of expected non-loopback
binds and is handed exactly the bridge's own listener URL, so it still fails closed on every other routable bind —
which the existing `Security:AllowNonLoopbackBind` flag could not do, since that flag would also silence the guard
for `/api/local/v1` going routable by operator error. And `AllowedHosts` is widened with the bridge's host names,
because `HostFilteringMiddleware` is installed by an `IStartupFilter` and therefore runs ahead of every middleware
the composition root registers: the shipped `localhost;127.0.0.1;[::1]` answered a container's
`Host: <lan-address>:18790` with 400 before the bridge branch could see it. Host filtering is per host and not per
endpoint, so the widening reaches the loopback listener too; that costs nothing the node relied on, because
`LocalApiSecurityMiddleware` checks Host and Origin against its own list and rejects a non-loopback peer outright,
and neither check reads this setting.

## Alternatives rejected

**Bind `llama-server` to a routable address.** It has no authentication of its own, so this publishes an
unauthenticated model server to the LAN. Rejected outright.

**Put the bridge inside `/api/local/v1`.** `LocalApiSecurityMiddleware` would reject exactly the traffic the bridge
exists to accept. The model proxy's own authentication handler carries a comment warning against mounting
proxy-style endpoints outside that prefix; the bridge is the deliberate exception, for the opposite reason the
warning exists, and it compensates with a peer guard at least as strict about what it admits.

**A Unix domain socket bind-mounted into each container.** It keeps the host surface loopback-only and is the
tidiest answer on rootful Linux. It is not portable: Docker Desktop's containers are in a VM with no access to a
host socket, and an application that speaks an OpenAI base URL cannot dial a socket path without an in-container
shim the catalog would then have to ship and maintain per image.

**A per-instance sidecar container proxying to the host.** It moves the same reachability problem into a second
container without solving it: the sidecar still has to reach the host from a container namespace. It also doubles
the container count of every install and adds an image to pin, pull and audit.

**`--add-host host.docker.internal:host-gateway` and nothing else.** This is what the catalog shipped, and it is
precisely what does not work on a rootless daemon. Removing that block from the odysseus sample is part of this
decision, not an unrelated cleanup.

## Consequences

**The panel copy changes.** "This application can reach services on this computer" became a sentence that names the
bridge and the token, because the old one was false on the platform the maintainer develops on.

**Odysseus cannot use the bridge automatically.** It probes auto-discovered model hosts *unauthenticated*, and
attaches a key only to endpoints a user registers by hand. The token is mandatory on every bridge route and is not
being made optional for discovery — an unauthenticated route is a route any container on the network may call. So
the sample exposes the bridge as two inspectable environment values, `XE_MODEL_BRIDGE_URL` and
`XE_MODEL_BRIDGE_TOKEN`, and the user registers them inside Odysseus once. This is a real limitation of this design
for applications that discover rather than configure, and it is accepted rather than designed around.

**A pre-bridge instance has no token; the UPDATE mints one, and nothing else does.** The column is nullable for
exactly that row shape. There is no migration backfill — a migration that minted credentials would be a migration
writing secrets — but an update backfills: `ExternalAppService.UpdateAsync` mints a token when the row carries
none, and `IExternalAppInstanceStore.CommitUpdateAsync` writes it in the same transaction as the manifest and the
variables. That transaction is the update's recovery boundary and the containers being created are created from
the same grant, so the token the row holds and the token the application was given become true together. The
token then persists for every later Start.

Start does **not** mint, and that is not an oversight. Start may REUSE the containers it finds: a token minted
there would be one no running container was ever given, so the row would claim bridge access the application
cannot use, and the next verification plan would disagree with the environment the containers actually have. An
update has no such gap because it recreates every container by definition. The remaining case — a pre-bridge
instance on a node whose catalog offers it no newer manifest — still gets bridge access by being reinstalled.

**Only a rootless Linux daemon is validated; the bridge is expected to be dark on a rootful one.** Under rootless
Docker, RootlessKit translates a container's traffic, so it arrives at the bridge from one of the host's own
addresses and the peer guard admits it — which `ContainerBridgeRealDaemonTests` proves live. Under a **rootful**
daemon it does not: a container dialling one of the host's own addresses sends a packet whose destination is local,
which the kernel routes PREROUTING to INPUT without ever passing POSTROUTING, so the daemon's `MASQUERADE` rule
never applies and the source address stays the container's own `172.x.y.z`. The peer guard sees an address no
interface owns and answers 403 before the token gate. This is unproven on hardware in **either** direction — there
is no rootful box here — which is why it is recorded as a limitation rather than asserted either way. The guard
fails closed, so the cost is a feature that does not work, not a hole. `ContainerBridgePeerGuardMiddleware` names the
hypothesis in its warning when the refused peer is in a private or link-local range, so the next person does not
repeat the packet capture that found it.

*The intended remedy, deliberately not built here:* `ContainerBridgeAddressWatcher` also admits the subnets of the
networks the engine itself created. It is a real widening of who may connect, but every admitted caller still has to
present a per-instance token, which is the control that actually separates one application from another. It is not
built in this batch because there is no rootful host to prove it against, and a widening validated by nothing is
worse than a limitation written down.

**The bridge is deliberately not rate-limited.** `app.UseRateLimiter()` is registered far below the branch, so
bridge traffic bypasses it entirely. Accepted for V1: the token is mandatory and per-instance, and the forwarder's
inference lease and idle-read watchdog bound every request. The observable failure mode is a container driving
repeated `EnsureRunning` calls for different installed models and thrashing the model the user is actually using. A
container that wanted to hurt this box has cheaper ways, so this is recorded as a decision rather than closed.

**Docker Desktop is code-reviewed, not live-validated.** The `host.docker.internal` branch of both the endpoint
resolver and the host-name allow list has unit coverage and no live round behind it. Do not read a green suite as
Desktop parity.

**The bridge is off unless External Apps is on.** `ContainerBridge:Enabled` defaults to `false` in code and ships
`true`, the same fail-closed asymmetry `ExternalApps` carries, and the listener opens only when both are true — the
bridge exists for application containers and a node without that feature has none.

**What proves it.** `ContainerBridgeRealDaemonTests` runs a BusyBox container on an engine-created network against a
real listener on the address the production resolver chose: it fetches the model list with its token, and is refused
both without a token and with another instance's. Nothing else establishes that a rootless container's source
address passes the same-host peer guard. The host-filtering defect above was found by a **live** round against a
real node, not by that fixture: both bridge fixtures build with `WebApplication.CreateSlimBuilder()`, which installs
no host-filtering startup filter, and this test project ships no `appsettings.json`, so `AllowedHosts` is unset
there and host filtering defaults to `*`. `ContainerBridgeHostFilteringTests` is what guards the fix — it runs the
real `HostFilteringMiddleware` over the real shipped allow list in front of the real bridge branch, asserts a
container's `Host` header reaches the token gate with the widening and is answered 400 without it, and reads
`Program.cs` to assert the composition root still makes the call.

## Related

- [ADR 0010](0010-external-apps-container-execution.md) — the runtime this bridge serves.
- [Wiki 23](../wiki/23-external-apps.md) §5 — the security posture, including the caveat this closes.
- [Wiki 12](../wiki/12-security-and-privacy.md) §3.5 — the bridge beside the loopback-only invariant.
