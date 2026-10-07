# Admin API surface

> Reference page for [Security & Privacy](../12-security-and-privacy.md) §3 · Reviewed: 2026-10-07 · Code-grounded.

It lists the mechanisms behind the local admin API: the Host/Origin gate, browser sessions and authorization, the MCP and model-proxy keys, the container bridge and Codex OAuth.

## 3.1 Loopback peer + Host + Origin middleware (`LocalApiSecurityMiddleware`)

`LocalApiSecurityMiddleware` (`XE-Local-AI-Engine.Client/Endpoints/Common/LocalApiSecurityMiddleware.cs`, registered in `Program.cs` via `app.UseMiddleware<LocalApiSecurityMiddleware>()`) rejects any `/api/local/v1` request whose transport peer is non-loopback **or** whose `Host`/`Origin` is not loopback, returning **403** before routing:

```csharp
// LocalApiSecurityMiddleware.InvokeAsync()
if (IsLocalApiRequest(context.Request.Path)
    && (!IsLoopbackPeer(context.Connection.RemoteIpAddress)
        || !IsAllowedHost(context.Request.Host.Host)
        || !IsAllowedOrigin(context.Request)))
{
    context.Response.StatusCode = StatusCodes.Status403Forbidden;
    return;
}
```

- **Loopback peer check** is the authoritative transport-level gate: `context.Connection.RemoteIpAddress` is the address of the socket peer — the machine that opened the TCP connection to Kestrel — so a routable caller is rejected even if it forges a loopback `Host`/`Origin`. A **null** peer address means the request never traversed the network stack (the in-process/in-memory test host and in-process health probes present no peer) and is treated as loopback-equivalent; only a concrete non-loopback address is rejected (the `IsLoopbackPeer` branch in `LocalApiSecurityMiddleware.cs`).
- **Allowed hosts** are exactly `localhost`, `127.0.0.1`, `::1` (case-insensitive; IPv6 brackets normalized off).
- **Origin check** is fail-closed: an absent `Origin` is permitted (same-origin navigation), but any *present* `Origin` must parse, be a loopback host, and match the request's scheme + host + port exactly. A non-loopback or mismatched origin is rejected.
- Ordering matters: the middleware runs *before* `UseRouting`/`UseAuthentication`/`UseAuthorization` in `Program.cs`, so a non-local caller is rejected before it can reach an endpoint at all.
- Because it runs before authorization and does not care whether a route is anonymous, **every** operation under `/api/local/v1/` can genuinely return this 403 — the four anonymous auth routes (login, setup, status, refresh) included — which is why the OpenAPI document declares a `403` on all of them and `OpenApiDocumentTests` pins that.

> **Reverse proxies / headless deployment are unsupported.** The peer check reads the socket peer, and no forwarded-headers middleware is registered, so `X-Forwarded-For` is never honoured. A reverse proxy on the **same host** would appear as a loopback peer on every forwarded request and defeat the peer gate — this is by design: the app is single-user, same-machine only. Putting `/api/local/v1` behind a proxy or exposing it beyond the local machine is out of scope and not a supported configuration.

## 3.1a The inbound MCP endpoint sits inside the gate — deliberately

`MapMcp` mounts this node's own MCP server at `/api/local/v1/mcp/server` (`Program.cs`, beside the
`MapHub` calls). The path is not cosmetic: `IsLocalApiRequest` matches on the `/api/local/v1` prefix
**alone**, so an MCP endpoint mounted at a bare `/mcp` would be reachable without any of §3.1's peer,
Host or Origin checks, leaving the bearer key as the only control. Keep it inside the prefix.

Measured 2026-08-03 on WSL2 (NAT networking, `localhostForwarding=true`): a client on the **Windows**
host connecting to a node inside WSL presents peer `127.0.0.1` and Host `127.0.0.1`/`localhost`, so
all three checks pass unchanged and no relaxation is needed for that topology. Not re-verified under
WSL mirrored networking.


## 3.2 Authentication & authorization

Three authentication schemes are registered. **JWT bearer** is the default and gates everything the
browser touches via the `NodeOperator` policy. **`McpApiKey`** is a second scheme used by exactly one
endpoint — the inbound MCP transport — via the `McpServer` policy, which lists only that scheme.
**`LocalModelProxyApiKey`** is a third, applied only by the `LocalModelProxy` policy on the inbound
OpenAI-compatible model proxy (§3.2.1). Each policy names exactly one scheme, so no principal can
impersonate another: an operator JWT does not open the MCP endpoint or the proxy passthrough, and
neither key opens the key-management endpoints (or anything else). Both directions are asserted for
MCP in `XE-Local-AI-Engine.Tests/Mcp/McpServerInboundAuthTests.cs`.

The browser's session is a short-lived access token held **in memory only** plus an HttpOnly, Secure,
SameSite=Strict refresh cookie, so every document load calls `auth/refresh`. **Sessions are independent**: each
sign-in starts its own refresh-token chain and revokes nothing, so signing in on a second browser or client never
signs the first one out. `NodeAuthService.RefreshAsync` rotates the presented token single-use — that token alone is
revoked and linked to its successor inside one serializable transaction; other sessions are untouched. Logout
(`RevokeRefreshTokensAsync`), `ChangePasswordAsync` and `ResetAdminPasswordAsync` still revoke **every** session of
the user. A failed refresh (missing, expired or revoked cookie) answers a bodyless 401 and clears the cookie.

Rotation carries a **10-second reuse grace for rotation alone**: a token that rotation replaced still buys a
pair until the window closes, because a reload racing its own in-flight refresh (or a second tab sharing the cookie)
otherwise loses, and the loser's 401 clears the cookie and signs a blameless operator out. The grace never re-opens a
session an operator closed. The discriminator is the successor link (`replaced_by_token_id`), which only rotation
writes: the grace follows it to the chain's head and requires that head to be live and unexpired. The revoke-all
paths write no link and revoke the head, so a logged-out cookie never qualifies — and because the link is per
chain, another session's live token can never vouch for it. Presenting a rotated token again does **not** re-stamp
or re-link it, so the window is measured from the original rotation and cannot be walked forward. Expiry is checked
first and is never graced. `XE-Local-AI-Engine.Tests/Auth/NodeAuthRefreshRotationGraceTests.cs` walks each revoke
path and the concurrent-session cases.

The grace pair is a **sibling** of the chain's head, not its replacement, because only the token hash is stored and
the winner's token cannot be handed back: the winner's token stays live, so whichever `Set-Cookie` a browser keeps
when the two responses arrive out of order still works, and a third presenter inside the window is served too. The
cost is one extra live token per graced race, which ends at expiry or logout, and a copy of the cookie captured
inside the window can mint pairs until it closes (bounded by the auth endpoints' rate limit). Each such pair is an
independent session with the full refresh lifetime, so a cookie stolen inside the window is no longer revealed by
the other client being signed out: only logout, a password change or a reset ends it.

Authorization is **deny-by-default, in two layers**. At the FastEndpoints layer a global configurator applies
the `NodeOperator` policy to every discovered endpoint whether or not that endpoint's own `Configure()` asked
for it, so a forgotten `Policies()` call cannot ship an anonymous route; an endpoint that deliberately opted
out with `AllowAnonymous()` still wins, and the four pre-authentication endpoints under `Endpoints/Auth/V1/` (status, setup, login, refresh)
(`auth/status`, `auth/setup`, `auth/login`, `auth/refresh`) are the entire anonymous set. Behind it,
`AuthorizationOptions.FallbackPolicy` requires JWT bearer and an authenticated user for every routed surface
carrying no authorization metadata of its own, and it is evaluated even when routing matches no endpoint at
all, so a path this node does not serve answers an anonymous caller **401** rather than disclosing a 404 or
a 405. The surfaces that must stay reachable without a token say so explicitly: both health probes and the SPA
fallback that serves the login page. The dev-only OpenAPI document cannot, because it is raw middleware with no
endpoint to attach `AllowAnonymous` to, and is instead served ahead of `UseAuthentication()`.

`EndpointAuthorizationPolicyTests` locks both layers in, reading the effective route metadata rather than
FastEndpoints' own bookkeeping: every endpoint resolves to `NodeOperator` or appears in the pre-authentication
allowlist, every SignalR hub route requires `NodeOperator`, and the hand-mapped minimal APIs and the inbound
MCP route carry their named policies. Its teeth are a permanent canary endpoint,
`GET diagnostics/configurator-canary-probe`, which calls neither `Policies()` nor `AllowAnonymous()` and is
therefore protected by the global configurator alone. Deleting that one line fails the test by name, while
every endpoint carrying its own redundant `Policies()` call would notice nothing. The canary answers 204 to an
operator and 401 to anyone else, and is excluded from the OpenAPI document. The residual is that
`FallbackPolicy` asks only for a valid JWT, not the `Admin` role `NodeOperator` requires: it is defense in
depth behind the first layer, not an equal substitute, which is why the canary rather than the fallback is
what the guard watches.

The MCP credential is a single 256-bit `xemcp_`-prefixed secret stored as a **one-way SHA-256 digest**
in the node database. The plaintext is returned exactly once — in the response to the generate call —
and is unrecoverable afterwards: `GET` returns only the prefix and timestamps, and the response type
has no key field at all, so the guarantee is enforced by the contract rather than by convention. A
lost key therefore cannot be recovered; the remedy is to regenerate and reconfigure every client.
A plain digest rather than a password KDF is deliberate: the input is 256 bits of CSPRNG output, so
PBKDF2/Argon2 would buy no guessing resistance and would tax every authenticated request, and no salt
is needed for a single high-entropy secret. The digest is *additionally* encrypted at rest, now for
**integrity rather than confidentiality** — hashing already defends a database read, but a bare hash
column would let anyone who can write the database file substitute a digest whose preimage they know
and take over an agent-execution surface; the AAD-bound AEAD (`mcp_api_key_hash`) is what makes that
substitution fail. (`node-settings.json` was rejected as a home because it is plaintext and carries no
restrictive ACL on Windows.) Generating replaces the previous key with no window in which both
authenticate, comparison hashes the presented value and runs
`CryptographicOperations.FixedTimeEquals` over the **digest bytes** (a short-circuiting compare is a
byte-at-a-time oracle over loopback), and a node with no key generated authenticates nobody. Spec revision 2026-07-28 makes authorization **OPTIONAL** for MCP
implementations, so this node implements no OAuth profile and advertises no Protected Resource
Metadata — see the [connect runbook](../../runbooks/connect-an-mcp-client-runbook.md).

The singleton row also carries exactly one scope. `delegate` is the default and exposes the eight
shared `NodeAgentMcpTools`; `agentic` exposes those eight plus the admin tools of `NodeAdminMcpTools`,
enumerated in the drift-tested
[`references/mcp-tools.md`](../../../skills/xe-local-ai-engine/references/mcp-tools.md). Minting
either scope rotates the row atomically. Authentication places `xe:mcp_scope` and a bounded key
prefix in claims; SDK authorization filters remove unauthorized tools from discovery and reject
direct calls. Agentic is operator-equivalent only for that enumerated MCP tool surface: it grants
no Operator role/JWT, REST access, routable listener, or general policy bypass.

For saved-agent execution, authority is explicit rather than ambient and is fingerprinted/persisted
with durable requests. A run admitted before rotation deliberately retains its captured authority
across disconnect, restart, and rotation. Agentic root execution may unwrap approval-required tools
from the saved agent's complete allowed set, but a strict recorder persists a metadata-only
`ApprovalDecision` with source `mcp-agentic:<bounded-prefix>` before the inner function runs. Audit
failure blocks invocation. Arguments, prompts, message content, tokens, passwords, full keys, and
host paths are never recorded. Spawned children retain ordinary curation and do not inherit agentic
elevation. [ADR 0006](../../adr/0006-agentic-trust-mcp-key-scopes-and-auto-approval.md) records the
decision.

### 3.2.1 The inbound model-proxy bearer key

The node exposes an **OpenAI-compatible passthrough** (`proxy/v1/{chat/completions,embeddings,models}`)
so an external tool — LiteLLM, Continue, a Hermes-style agent — can point its `base_url` at this node
and use the locally loaded model. The credential is a single operator-generated bearer key, deliberately
chosen because a static `Authorization: Bearer …` header *is* the OpenAI wire convention and is what
those clients already speak.

Its handling mirrors the MCP key rather than inventing a second posture
(`LocalModelProxyApiKeyService`, `LocalModelProxyApiKeyAuthenticationHandler`):

- **One key, 256 bits of CSPRNG output**, Base64Url-encoded behind an `xeprx_` scheme prefix so it
  survives a shell argument, a JSON config file and an HTTP header untouched.
- **Stored as a one-way SHA-256 digest**, and that digest is *additionally* AEAD-encrypted at rest under
  its own AAD column (`local_model_proxy_api_key_hash`) — for **integrity, not confidentiality**: a bare
  hash column would let anyone who can write the database file substitute a digest whose preimage they
  chose and take over the proxy surface. A plain digest rather than a password KDF is the same
  deliberate call made for MCP: 256 bits of entropy has no guess space to slow down.
- **The plaintext is returned exactly once**, from `POST proxy/key`. `GET proxy/key` returns only the
  display prefix, timestamps and last-used marker; the response type has no key field at all.
  Generating replaces the previous key with no window in which both authenticate, and `DELETE` revokes.
- **A node with no key generated authenticates nobody** — an ungenerated credential fails closed rather
  than reading as "no authentication required".
- Comparison hashes the presented value and runs `CryptographicOperations.FixedTimeEquals` over the
  **digest bytes**, because a short-circuiting compare is a byte-at-a-time oracle over loopback.
- The three passthrough routes carry their own fixed-window rate-limit policy
  (`NodeAuthRateLimits.LocalModelProxyPolicy`).

**The bearer key is not the only gate, and that is load-bearing.** The passthrough is hand-mapped
*inside* the `/api/local/v1` prefix precisely so `LocalApiSecurityMiddleware`'s loopback-peer + Host +
Origin check has already rejected any non-loopback caller before the handler runs. An external tool
therefore has to be on this host (or reach it through the operator's own tunnel) — mounting these routes
outside the prefix would silently remove that layer and leave the key as the only control. `proxy/key`
itself is an ordinary Operator-gated FastEndpoints family, so key management stays on the browser's
JWT posture. Requests are forwarded verbatim to the resolved `llama-server` child and never route
through the operator's cloud credentials. See [API & Hubs](../09-api-and-hubs.md).

Local endpoints are still authenticated and policy-gated; loopback is necessary but not sufficient. `NodeAuthorizationPolicies` (`Services/Auth/NodeAuthorizationPolicies.cs`) defines the `NodeOperator` policy (claim type `role`, `Admin`), and endpoints apply it — e.g. `ListAgentExecutionLogsEndpoint.Configure()` calls `Policies(NodeAuthorizationPolicies.Operator)` (see `ListAgentExecutionLogsEndpoint.Configure()`). JWTs are signed with the separately-derived node JWT key (§2.1). Auth wiring lives in `AddNodeAuthExtensions`. See [API & Hubs](../09-api-and-hubs.md) for the full endpoint inventory.

**The Simple / Advanced interface mode is not part of this gate.** `uiMode` in `node-settings.json` decides which
entries the SPA's navigation renders and nothing more: every route stays reachable by its own address in either mode,
no endpoint consults it, and the `Operator` policy above is what actually decides who may do what. Treating it as an
authorization boundary — hiding an endpoint's page instead of gating the endpoint — would be the mistake it is named
against. See [React Client](../10-react-client.md).

## 3.5 The container bridge — the one deliberately non-loopback listener (`ContainerBridgePipeline`)

Everything in §3.1–§3.4 describes a node that listens on loopback and nothing else. There is exactly one exception,
and it is an exception by design rather than by oversight: an application container installed under
[ADR 0010](../../adr/0010-external-apps-container-execution.md) has its own network namespace and cannot reach the
host's loopback at all, so the engine's whole surface — including the local model server — is unreachable from the
containers it hosts. [ADR 0011](../../adr/0011-container-bridge-listener.md) records the decision; this is what it
means for the security posture.

- **It is one listener, on one address, serving three routes.** `ContainerBridgeEndpointResolver` picks a
  LAN-facing IPv4 address (or takes `ContainerBridge:BindAddress`, which refuses a wildcard), and
  `ContainerBridgePipeline.Map` branches that listener out **first** in the pipeline, matching the connection's
  whole local end (address and port) so a node whose own listener shares the bridge's port cannot have its traffic
  claimed by the branch. Nothing else the host
  serves — the SPA, `/api/local/v1`, the hubs, the MCP endpoint, the health checks — is reachable on it, and
  nothing mapped inside it is reachable on the loopback listener. The branch predicate is the socket's local port,
  which is the one fact a caller cannot forge. A node whose configured bridge port is already one of its own bind
  URLs, or whose bridge address and port are already held by another node on the machine, opens no bridge and boots
  with its loopback listener alone.
- **Two independent controls run before any route.** `ContainerBridgePeerGuardMiddleware` refuses, with 403, a peer
  that is not one of this computer's own addresses — the compensating control for the loopback-peer check that
  cannot apply here, since accepting a non-loopback peer is the bridge's entire purpose. Then
  `ContainerBridgeTokenMiddleware` requires the per-instance bearer token on **every** route: the peer guard admits
  any container on an engine-owned network, so the token is what stops one application using another's bridge.
  Every refusal is byte-identical, so a caller cannot learn which instance ids exist.
- **`llama-server` still binds `127.0.0.1`.** The bridge forwards to it through the same
  `LocalModelProxyForwarder` the loopback model proxy uses. No model server is published.
- **The guard in §3.4 still fails closed.** It is handed the bridge's own listener URL as an expected non-loopback
  bind and subtracts exactly that; any *other* routable bind still stops the process. This is deliberately not
  `Security:AllowNonLoopbackBind`, which would silence the guard for `/api/local/v1` going routable too.
- **`AllowedHosts` is widened with the bridge's host names**, because `HostFilteringMiddleware` is installed by an
  `IStartupFilter` and runs ahead of every middleware the composition root registers — without it a container's
  `Host: <lan-address>:18790` is answered 400 before the bridge branch exists. Host filtering is per host, not per
  endpoint, so this reaches the loopback listener too; it costs nothing, because `LocalApiSecurityMiddleware`
  checks Host and Origin against its **own** list and rejects a non-loopback peer outright, and neither check reads
  this setting. The maintainer rule below is about that own list, not about this one.
- **It is off unless External Apps is on.** `ContainerBridge:Enabled` is `false` in code and `true` in the shipped
  `appsettings.json`, and the listener opens only when both flags are true.
- **IPv4 only, and only an address this host owns.** An IPv6 `BindAddress` is rejected rather than used, and a host
  with no IPv4 address opens no bridge; the container networks the engine creates are IPv4. A configured address no
  interface carries is rejected as well, so a typo costs the node its bridge and not its boot — Kestrel fails the
  whole host on a bind it cannot satisfy.
- **Only a rootless Linux daemon is validated.** Rootless Docker source-translates a container's traffic, so it
  reaches the bridge from one of the host's own addresses and the peer guard admits it. A **rootful** daemon does
  not: traffic to a local address never passes POSTROUTING, so it is never masqueraded and arrives with the
  container's own address, which the guard refuses with 403. The bridge is therefore expected to be dark there. It
  fails closed — this is a feature that does not work, not a hole — and the peer guard's warning names the
  hypothesis when the refused peer is private or link-local. ADR 0011 records the remedy: admit the subnets of
  networks the engine created, still behind the per-instance token.
- **It is not rate-limited.** `UseRateLimiter` sits below the branch, so bridge traffic bypasses it. Accepted for
  V1 — the token is mandatory and per-instance, and the forwarder's inference lease and idle watchdog bound each
  request — with the observable failure mode recorded in ADR 0011: a container thrashing model loads.

---

**The bridge token's shape is a credential plus a lookup key.** `ContainerBridgeToken` mints
`<instance id, "N" format>.<base64url of 32 CSPRNG bytes>`. The instance id travels **in the clear, in front**, so
verification is a keyed row read rather than a scan of every installed application's secret. That is not a weakening:
the id is not the credential, the 256 bits behind the separator are, and a scan would compare the presented secret
against rows it was never meant for. Base64url — no padding, no `+`, `/` or `=` — so the token survives an environment
variable, a container's own config file and an HTTP header untouched, and all three are on the path to the application.
`ContainerBridge:BindAddress` refusing a wildcard is the bridge's own half of the §3.4 startup bind guard: the guard
treats a wildcard bind as non-loopback and shuts the node down, so a bridge that accepted one would either be killed at
startup or, worse, expand to an address the operator never chose.

**Maintainer rules:**
- Mount any new local-admin route under `/api/local/v1` so the middleware covers it; routes outside that prefix are *not* loopback-gated by this middleware. The container bridge (§3.5) is the one reviewed exception, and it carries its own peer guard and token gate in place of this middleware.
- Keep the Origin check fail-closed — never widen `AllowedHosts` to a public address.
- Apply an authorization policy in addition to the loopback gate; do not rely on loopback alone.
- Do not add forwarded-headers middleware or a reverse proxy in front of this surface, and do not set `Security:AllowNonLoopbackBind` to enable a routable/headless deployment — those configurations are unsupported (§3.1).

## 3.7 Codex OAuth: token storage, refresh and redaction

`CodexTokenStore` mirrors `CloudCredentialStore` — DataProtection at rest, user-only file permissions through
`SecureFilePermissions` — but uses a **dedicated protector purpose** and a **separate `.enc` file**, so it cannot
collide with the API-key-shaped cloud credential store. It never logs token values.

`CodexAuthHandler` is the `DelegatingHandler` that owns auth on the SSE Responses path, and it does three things in
order:

1. **Strips** any `Authorization` the OpenAI SDK added from its dummy `"unused"` key, so that value never reaches the
   wire.
2. **Injects** the Codex header contract for the SSE path: the real bearer `Authorization`, `chatgpt-account-id`,
   `originator` and `User-Agent` — and *not* the WebSocket-only `OpenAI-Beta`.
3. On a **401**, performs a **single-flight refresh** — one gate, with concurrent 401s awaiting the same refresh under
   a double-checked expiry — and retries the request exactly once. A sent `HttpRequestMessage` cannot be reused, so the
   retry goes out on a fresh clone whose content is buffered to be re-readable.

It never logs token values, authorization headers or the dummy key. `CodexHeaders` is the single source of truth for
that contract and the wire-contract test binds to its constants. The account-id header is `chatgpt-account-id` (as the
Codex CLI sends it) and **not** `openai-`-prefixed, because the prefix may break account-scoped auth; this was verified
against the Codex CLI source path `codex-rs/core/src/client.rs`, where the v0 SSE Responses path sends the always-on
auth headers plus the minimal HTTP/SSE subset. The WebSocket-only `OpenAI-Beta: responses_websockets` header is
intentionally neither defined nor sent.

**Diagnostic error bodies are logged, under four constraints.** On a non-success response the handler logs the server's
error body so the node host log shows why the call was rejected. The body is buffered with `LoadIntoBufferAsync` first,
so reading it does not consume the content for the OpenAI SDK — only a bounded prefix is read from the buffered,
seekable stream and the stream is rewound, leaving the SDK to surface the same error to the caller. It is gated to
**failure statuses only**: a success response carries the live SSE stream and must not be read there. At most
`MaxLoggedBodyBytes` are logged, with the total body length reported separately, and the excerpt is stripped of control
characters so a server-controlled body cannot forge log lines. Only the body excerpt and the status are logged; request
headers are never touched, and the server's error JSON never echoes the bearer token or account id.

On top of that the excerpt is **redacted** before it reaches the log: user emails, JWT-shaped material, and any long
high-entropy token-like run — 20 or more characters from the base64/hex alphabet carrying **both** a letter and a
digit, so readable identifiers such as `invalid_request_error` survive intact. A pathological body that stalls a
pattern is dropped wholesale rather than logged unredacted.

**The JWT payload is base64url-decoded without verifying the signature.** That is intentional and safe here: the access
token arrives over TLS directly from the OpenAI token endpoint, and the decoded claims — `chatgpt_account_id` and
`exp` — are used **only** as advisory metadata, the account id becoming a request header and the expiry driving
proactive refresh. Neither is ever an authorization input or a trust decision on this node. **Do not repurpose these
claims for access control without first verifying the signature against OpenAI's JWKS.** When a token carries no usable
`exp` claim, the store falls back to a conservative 50-minute expiry.

`CodexLoginCoordinator` owns the pending-login lifecycle so the Operator endpoints can start a loopback PKCE login,
return the authorize URL immediately, and poll status until it completes. A second `Start` **supersedes** any in-flight
login: the prior attempt is cancelled and its loopback listener freed, so the new login can re-bind the callback port.
It takes the auth service as a `Lazy<T>` so the auth `HttpClient` is built on first `Start` rather than when the
singleton is constructed, which keeps endpoint instantiation at host startup from eagerly materializing it. It never
logs token material. The authorize request carries `originator`, `id_token_add_organizations` and
`codex_cli_simplified_flow` — verified against the working opencode reference client — to identify the client family,
ask the issuer to embed the org/account id in the `id_token` so the subscription path can resolve
`chatgpt-account-id`, and opt into the simplified Codex CLI flow.
