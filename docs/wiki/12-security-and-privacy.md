# Security & Privacy Model

> Reviewed: 2026-09-15 · Code-grounded.

This page documents the cross-cutting security and privacy controls implemented in the XE Local AI
Engine node and the invariants contributors are expected to preserve. It is code-and-test evidence
for the stated baseline, not proof of operating effectiveness, deployment configuration, compliance,
certification, or formal risk acceptance. The supported design routes platform traffic through the
keeps secret-bearing values behind node-local stores and redaction seams,
serves management/admin APIs on loopback, encrypts selected sensitive fields at rest, routes designated
privacy-sensitive AI work to node-local models, and confines application-mediated tool file access.

If you are touching persistence, see [Data & Persistence](08-data-and-persistence.md) for the encryption schema; for endpoint/hub surface see [API & Hubs](09-api-and-hubs.md); for the node-local AI rule in agent flows see [Agent Mode](04-agent-mode.md).

---

## 1. Egress invariant: the node has no control-plane channel

The node opens **no outbound connection to a control plane**. Every inbound surface is loopback-bound
(`/api/local/v1`, the SignalR hubs, the inbound MCP server), and nothing in the browser, in a tool or in a
provider dials out on the node's behalf.

An earlier design gave the node a single outbound `WorkerHub` SignalR connection to a central platform,
with device binding, a token refresh loop, an auto-connect hosted service, a `/health/ready` worker check,
end-to-end-encrypted invocation envelopes and a file-backed dead-letter queue. No shipped build could reach
the pairing UI (its capability flags were compile-time `false`), so the whole stack was removed rather than
maintained. Nothing reads `worker-credentials.enc`, a cert-pin file or a `dead-letter-queue` directory any
more; any such leftovers on an installed node are inert.

What still leaves the machine does so only because an operator configured a feature to fetch it:

| Egress | Owner | Operator decision that enables it |
|---|---|---|
| Model downloads | `Providers.HuggingFace` | the operator starts a download |
| Cloud chat providers | `Services/CloudProviders` | the operator stores Codex OAuth / Azure Foundry credentials |
| OpenAI-compatible providers | `Services/ExternalProviders` | the operator declares a connection and its base URL |
| Outbound MCP servers | `Services/Mcp` | the operator registers and enables a server |
| Update checks | `Services/AppUpdate` | shipped update channel |

**Maintainer rule:** do not add an outbound connection that a shipped build opens on its own. Egress belongs
to a feature an operator turned on, and it is documented on that feature's page.

---

## 2. Secret-handling invariant — not returned to the browser or deliberately logged

The baseline implementation treats the following as node-local secrets: the **node operator secret**
(master key material), the **endpoint tokens**,
**cloud-provider credentials** (for example Codex OAuth), the **HMAC/JWT signing keys**, and the
**HuggingFace token**. DTOs, stores, and redactors are designed so these values are not returned across
the browser boundary or deliberately logged. That source-level design does not by itself prove the
absence of secrets from every operational log or diagnostic artifact.

### 2.1 The operator secret and derived keys

The operator secret is the root of all node key material. `NodeOperatorSecretProvider` (`Services/Persistence/Implementation/NodeOperatorSecretProvider.cs`) resolves a **32-byte** secret, in priority order, from:

1. env var `XE_NODE_SQLITE_KEY` (base64-encoded 32 bytes), or `IConfiguration[XE_NODE_SQLITE_KEY]`;
2. a raw 32-byte secret file at `/run/secrets/node-sqlite-key`;
3. Aspire parameter `Parameters:node-sqlite-key` (local dev only).

If none of those sources provides a value, startup *fails fast* with a helpful message. In the Aspire development path the parameter is seeded per checkout by the dev scripts, as described below.

> **Development secret custody.** `secret: true` marks the Aspire parameter as sensitive for display and handling; it never made the value confidential. What did make it non-confidential was a shared development-only default committed to `XE-Local-AI-Engine.AppHost/appsettings.Development.json` — anyone with the source could derive keys for data created under it. That default is **gone**, and it is still in git history, so it must be treated as burned: any dev data written under it is public. `scripts/dev-aspire-common.sh` now mints a per-checkout, owner-only, `.gitignore`d `XE-Local-AI-Engine.AppHost/.data/node.key` (base64 32 bytes) on first use and `scripts/dev-start.sh` passes it to Aspire as `Parameters__node-sqlite-key`, so each checkout has its own secret and nothing sensitive is tracked. The secret is passed through process environments only, never a command line. Packaged desktop mode is different again: `DesktopBootstrap` generates and persists a per-installation `node.key`.
>
> **Rotating the secret destroys data.** The secret is the root of the SQLite column key, the JWT signing key and the non-Windows Data Protection KEK. A checkout that already holds dev data written under a different secret fails on the first protected read with `AuthenticationTagMismatchException` — `dev_ensure_node_operator_secret` warns and names the directories to delete when it mints a key next to pre-existing data.

The secret is **never held longer than necessary**. `NodeSqliteKeyHolder` (`Services/Persistence/Implementation/NodeSqliteKeyHolder.cs`) derives the SQLite key with HKDF-SHA256 (info `c0re-node-sqlite|v1|{NodeName}`) in its constructor, then immediately zeroes the source secret with `CryptographicOperations.ZeroMemory`, and zeroes its own derived key on `Dispose`. The JWT signing key is derived separately (`NodeJwtKeyProvider`, `Services/Auth/Implementation/NodeJwtKeyProvider.cs`) so the at-rest key and the auth key are never the same bytes.

**Three derivations, three info strings.** The one operator secret roots exactly three keys, each through the same HKDF-SHA256 / empty-salt / zero-on-dispose discipline and each with its own `info`: at-rest `c0re-node-sqlite|v1|{NodeName}`, auth `c0re-node-jwt|v1|{NodeName}`, and the Data Protection key-ring KEK `c0re-node-dpkeyring|v1|{NodeName}` (`NodeDataProtectionKeyProvider`). The info strings **MUST stay distinct**: collapsing any two would let one key's material stand in for another's, so a compromise or a rotation of one scope would silently reach the others. A missing secret throws at `NodeDataProtectionKeyProvider` construction, because the key-ring can be neither wrapped nor unwrapped without it; a *wrong* secret deliberately does not fail startup, since the node store is plain SQLite with application-level column encryption and a column only fails when it is read.

**The fail-closed key resolver is the backstop for a wrong secret.** Data Protection's default `IDefaultKeyResolver` treats a key whose `CreateEncryptor()` throws as merely *ineligible* and generates a fresh one — silently orphaning every `IDataProtector` payload under the old ring: cloud, Codex, HF, GitHub and worker OAuth tokens, and Entra caches. `NodeDataProtectionKeyRingFailClosedKeyResolver` decorates it so that failure is a **loud, fatal startup failure** naming the remediation instead. It is applied on **both** at-rest schemes — the non-Windows AES-GCM wrapper keyed from the operator secret and the Windows DPAPI wrapper — and deliberately outside that OS branch, because both wrappers fail all-or-nothing (the non-Windows KEK is derived deterministically from the one secret; the DPAPI blobs are all bound to one user profile), so one unreadable key means every key. The non-Windows form recognises only this node's own distinctive `NodeDataProtectionKeyRingDecryptionException`, so an unrelated `CreateEncryptor` failure is left to the framework rather than masked as a KEK problem.

**Maintainer rules:**
- Never log, echo, or return operator-secret-derived material across any DTO.
- Keep the at-rest, auth and key-ring derivations on *distinct* HKDF `info` strings (regression risk if collapsed).
- The 32-byte length is validated; don't relax it.

### 2.2 Redaction in logs, transcripts, and DTOs

Several redactors enforce "secrets never surface":

| Redactor | Purpose | Location |
|---|---|---|
| `AccessTokenQueryRedactor` | strips `access_token=` from request query strings before Serilog logs them | `Services/Auth/AccessTokenQueryRedactor.cs`, wired by the `UseSerilogRequestLogging` request-path projection in `Program.cs` |
| `MemoryProposalSecretScanner` | rejects/redacts secrets in agent-memory proposals before persistence (PEM keys, GitHub/AWS/Azure/Slack tokens, JWTs, high-entropy bearers; ReDoS-guarded with a 2s regex timeout) | `Services/AgentHome/Implementation/MemoryProposalSecretScanner.cs` |
| `McpServerConnectionManager.Redact` | clamps MCP connection failures to a generic message so a command path/URL/secret never reaches the UI | `McpServerConnectionManager.Redact` in `Services/Mcp/Implementation/McpServerConnectionManager.cs` |
| `InvocationRunner.RedactAgentRuntimeMessage` | sanitizes agent runtime failure messages before surfacing | `InvocationRunner.RedactAgentRuntimeMessage` in `Services/Invocation/Implementation/InvocationRunner.FailureClassification.cs` |
| `NodePatchApplyService.Redact` | redacts patch-apply output (AgentHome) | `NodePatchApplyService.Redact` in `Services/AgentHome/Implementation/NodePatchApplyService.cs` |

The request-logging enricher is the canonical example — it replaces the raw query with a redacted one before anything is written:

```csharp
// Program.cs, UseSerilogRequestLogging request-path projection
var redactedQuery = AccessTokenQueryRedactor.Redact(httpContext.Request.QueryString.Value);
diagnosticContext.Set("RequestPathWithRedactedQuery", $"{httpContext.Request.Path}{redactedQuery}");
diagnosticContext.Set("QueryString", redactedQuery);
```

The marker used across redactors is the literal `[REDACTED]` (and `[REDACTED:…]`-style markers in the secret scanner). The scanner's "bare high-entropy" regex is deliberately written so the `[`/`]` of an existing marker stays outside the match — a second pass never re-redacts an already-redacted span (`MemoryProposalSecretScanner.cs`).

**Maintainer rule:** any new field that can carry a credential, host path, command, or URL toward the browser, logs, or a saved transcript must pass through (or extend) a redactor. When in doubt, clamp to a generic reason like `McpServerConnectionManager.Redact` does.

### 2.3 Secret files and secret columns: one protector per purpose, 0600 at create, quarantine on failure

Four stores hold credential material outside the chat columns — `CloudCredentialStore`, `CodexTokenStore`,
`EntraTokenCacheStore` / `EntraAuthCodeAccountStore`, and `ExternalProviderStore` — and they share one posture.

- **One protector per purpose.** Each store derives its own Data Protection purpose, so a blob written for one store can
  never be decrypted as another's. Don't collapse two stores onto a shared purpose.
- **A secret file is created at 0600, in the same syscall that creates it.** `File.WriteAllBytesAsync` creates at the
  process umask — 0644 on a default Linux or macOS box — and narrowing it afterwards leaves a window in which another
  local user can read the file. `CloudCredentialStore.WriteProtectedPayloadAsync` therefore passes
  `FileStreamOptions.UnixCreateMode`, which closes that window. `UnixCreateMode` applies only on *create*, so
  `SecureFilePermissions.Apply` still runs afterwards to narrow a pre-existing file left at 0644. This is the same
  discipline `node.key` follows (§2.1).
- **A decryption failure quarantines the blob rather than propagating.** A row or file that no longer decrypts — a
  rotated operator secret, a corrupted write — would otherwise fail every later save's read-modify-write. The store
  moves it aside and reports the credential as *missing*, not *unreadable*: quarantined means gone.

**API keys are hashed, not KDF'd.** `IntegrationApiKeyService` (and `McpServerApiKeyService`) mint 256 bits of CSPRNG
output, persist a single **unsalted SHA-256** of the key's UTF-8 bytes, and compare in constant time. A password KDF is
deliberately not used: there is no guess space to slow down against 256 random bits, and it would add latency to every
authenticated request. The corollary is that the key itself must never be low-entropy or operator-chosen. Verification
resolves the row by its plaintext display prefix, because the digest column is encrypted at rest and cannot be queried,
and every malformed bearer value is guarded *before* it is sliced — an unguarded slice turns a one-character token into a
500 where a 401 is required, reachable by anyone who can reach the route.

---

## 3. Local admin API: loopback-only, Host/Origin-strict, authenticated, fail-closed

In supported configurations, local management/admin endpoints live under `/api/local/v1` and are
reachable only from the same machine. Several layers enforce this: a request-time peer + Host + Origin
gate, authentication/authorization, and a startup bind guard. The explicit
`Security:AllowNonLoopbackBind=true` opt-out described below weakens the bind guard and is not a
supported reverse-proxy/headless deployment mode.

### 3.1 Loopback peer + Host + Origin middleware (`LocalApiSecurityMiddleware`)

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

### 3.1a The inbound MCP endpoint sits inside the gate — deliberately

`MapMcp` mounts this node's own MCP server at `/api/local/v1/mcp/server` (`Program.cs`, beside the
`MapHub` calls). The path is not cosmetic: `IsLocalApiRequest` matches on the `/api/local/v1` prefix
**alone**, so an MCP endpoint mounted at a bare `/mcp` would be reachable without any of §3.1's peer,
Host or Origin checks, leaving the bearer key as the only control. Keep it inside the prefix.

Measured 2026-08-03 on WSL2 (NAT networking, `localhostForwarding=true`): a client on the **Windows**
host connecting to a node inside WSL presents peer `127.0.0.1` and Host `127.0.0.1`/`localhost`, so
all three checks pass unchanged and no relaxation is needed for that topology. Not re-verified under
WSL mirrored networking.

#### Tool invocation writes no approval-audit row

`ToolInvocationService` is the one place a workflow node's tool call is admitted or refused, and it deliberately writes
no `approve` record. [ADR 0006](../adr/0006-agentic-trust-mcp-key-scopes-and-auto-approval.md)'s strict pre-invocation record exists for
*adapting* an approval-required function into a non-approval one for an agentic MCP root. This service refuses that
class twice and adapts nothing, so an `approve` row here would assert a decision nobody made.

### 3.2 Authentication & authorization

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
Metadata — see the [connect runbook](../runbooks/connect-an-mcp-client-runbook.md).

The singleton row also carries exactly one scope. `delegate` is the default and exposes the eight
shared `NodeAgentMcpTools`; `agentic` exposes those eight plus the admin tools of `NodeAdminMcpTools`,
enumerated in the drift-tested
[`references/mcp-tools.md`](../../skills/xe-local-ai-engine/references/mcp-tools.md). Minting
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
elevation. [ADR 0006](../adr/0006-agentic-trust-mcp-key-scopes-and-auto-approval.md) records the
decision.

#### 3.2.1 The inbound model-proxy bearer key

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
through the operator's cloud credentials. See [API & Hubs](09-api-and-hubs.md).

Local endpoints are still authenticated and policy-gated; loopback is necessary but not sufficient. `NodeAuthorizationPolicies` (`Services/Auth/NodeAuthorizationPolicies.cs`) defines the `NodeOperator` policy (claim type `role`, `Admin`), and endpoints apply it — e.g. `ListAgentExecutionLogsEndpoint.Configure()` calls `Policies(NodeAuthorizationPolicies.Operator)` (see `ListAgentExecutionLogsEndpoint.Configure()`). JWTs are signed with the separately-derived node JWT key (§2.1). Auth wiring lives in `AddNodeAuthAndConnectionExtensions`. See [API & Hubs](09-api-and-hubs.md) for the full endpoint inventory.

**The Simple / Advanced interface mode is not part of this gate.** `uiMode` in `node-settings.json` decides which
entries the SPA's navigation renders and nothing more: every route stays reachable by its own address in either mode,
no endpoint consults it, and the `Operator` policy above is what actually decides who may do what. Treating it as an
authorization boundary — hiding an endpoint's page instead of gating the endpoint — would be the mistake it is named
against. See [React Client](10-react-client.md).

### 3.3 Desktop / loopback hosting

In Desktop and McpOnly local modes the node binds plain HTTP on loopback only and bypasses the
HTTPS-redirect/HSTS branch by design; `LoopbackUrlResolver` / `DesktopLifecycle`
(`XE-Local-AI-Engine.Client/Hosting/`) resolve the remembered, requested, or free loopback URL. See
[Hosting & Deployment](11-hosting-and-deployment.md). The loopback bind plus the peer + Host/Origin
middleware together keep the admin surface off the network.

### 3.4 Startup bind guard (`LoopbackBindGuard`)

`LoopbackBindGuard` (`XE-Local-AI-Engine.Client/Hosting/LoopbackBindGuard.cs`, wired via `LoopbackBindGuard.Guard(app)` in `Program.cs`) is defense-in-depth behind the request-time middleware: instead of trusting the configured URLs, it inspects the addresses Kestrel *actually* bound (post `ApplicationStarted`, so an OS-assigned port and wildcard expansion are already resolved) and, if any is non-loopback, logs a **critical** line naming the offending address(es) and shuts the app down.

- The shutdown sets `Environment.ExitCode = 1` before calling `StopApplication()`, so a supervisor/CI treats the guarded stop as an **error** (exit code 1) rather than a clean shutdown (see the guarded-stop branch in `LoopbackBindGuard.Guard()`).
- Wildcard binds (`*`, `+`, `0.0.0.0`, `::`) are treated as non-loopback and trigger the guard; `localhost` and any loopback IP literal pass.
- **Opt-out:** setting `Security:AllowNonLoopbackBind=true` skips the guard entirely — for an operator who has secured the surface themselves. It defaults to `false`, and no supported launch needs it (desktop binds `127.0.0.1`; Aspire dev binds `localhost` and exposes externally via the DCP proxy, not the app process), so the guard is a no-op on every supported launch and only fires on a deliberately overridden routable bind.

### 3.5 The container bridge — the one deliberately non-loopback listener (`ContainerBridgePipeline`)

Everything in §3.1–§3.4 describes a node that listens on loopback and nothing else. There is exactly one exception,
and it is an exception by design rather than by oversight: an application container installed under
[ADR 0010](../adr/0010-external-apps-container-execution.md) has its own network namespace and cannot reach the
host's loopback at all, so the engine's whole surface — including the local model server — is unreachable from the
containers it hosts. [ADR 0011](../adr/0011-container-bridge-listener.md) records the decision; this is what it
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

### 3.6 The loopback OAuth callback listener (`LoopbackAuthorizationCodeListener`)

The Entra ID authorization-code sign-in needs a redirect target, and RFC 8252 §7.3 requires a native app's redirect URI
to be a **loopback** interface. `LoopbackAuthorizationCodeListener` is a one-shot `HttpListener` that is bound only when
a sign-in starts and stopped immediately after the single callback or a timeout — never left listening between sign-ins.
`Start` re-validates that the URI is an absolute http(s) URI on a loopback host even though callers must already have
passed `EntraAuthCodeDefaults.TryValidateRedirectUri`; that is defence in depth, not a duplicate.

The response page is a **fixed static string** and never reflects any query-parameter content back. An attacker who can
make the operator's browser hit this loopback port with a crafted query string must not be able to inject markup or
script into the page it returns. AAD's own `error` / `error_description` text (RFC 6749 §4.1.2.1) is truncated to a
single line before it is ever logged, and the callback page never renders it regardless.

### 3.7 Codex OAuth: token storage, refresh and redaction

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

---

## 4. Exception handling: no internal detail leakage

Unhandled exceptions are translated to RFC7807 ProblemDetails by `DefaultExceptionHandler` (`XE-Local-AI-Engine.Client/ExceptionHandling/DefaultExceptionHandler.cs`), registered via `app.UseExceptionHandler()` *before* FastEndpoints. Crucially, the exception *message* is only included in the response in development/test environments; in production the detail is a fixed `"An unexpected error occurred"`:

```csharp
// DefaultExceptionHandler.TryHandleAsync()
var isDevelopment = hostEnvironment.IsDevelopment()
                    || hostEnvironment.IsEnvironment("Testing")
                    || hostEnvironment.IsEnvironment("IntegrationTests");
var detail = isDevelopment ? exception.Message : "An unexpected error occurred";
```

The full exception is logged server-side with method, path, trace id, user id (or `anonymous`), and exception *type name* (not message-in-response). `ConflictExceptionHandler` handles the 409 domain-conflict case. **Maintainer rule:** never put `exception.Message`, stack traces, or internal identifiers into a production response body.

---

## 5. Encryption at rest

Selected chat/state fields in SQLite are encrypted at the column level. This is not SQLCipher or
whole-database encryption; structural fields and deliberately searchable data such as Knowledge Base
chunk text/FTS remain plaintext. See [Data & Persistence](08-data-and-persistence.md) for the exact
schema and migrations; the security-relevant cryptography is summarized here.

| Component | Role | Location |
|---|---|---|
| `AesGcmNodeAeadCipher` | the *only* `AesGcm` owner: AES-256-GCM, 12-byte nonce, 16-byte tag | `XE-Local-AI-Engine.Client.Persistence/Cryptography/AesGcmNodeAeadCipher.cs` |
| `INodeAeadCipher` | the AEAD seam both at-rest and streaming-envelope crypto delegate to | `.../Cryptography/INodeAeadCipher.cs` |
| `NodePayloadProtector` | at-rest column protector: random nonce per value, AAD binds `conversationId + recordId + columnName + schemaVersion` | `.../Cryptography/NodePayloadProtector.cs` |
| `NodeChatContentProtection` | versioned read-both envelope over `NodePayloadProtector` for the two columns with legacy plaintext rows (message `content` + `metadata_json`): a `0xFE 0x01` header — bytes that can never begin valid UTF-8 — marks ciphertext, so reads tell it apart from legacy plaintext without guessing | `.../Cryptography/NodeChatContentProtection.cs` |
| `NodeEncryptionSaveChangesInterceptor` | encrypts tracked payloads on `SavingChanges`, restores plaintext on the tracked entity after save | `XE-Local-AI-Engine.Client.Persistence/NodeEncryptionSaveChangesInterceptor.cs` |
| `UploadedFileBlobProtector` | at-rest protection for chat **uploaded-file blobs** stored on disk (raw bytes + extracted Markdown); re-uses `AesGcmNodeAeadCipher` + the same `nonce ‖ ciphertext ‖ tag` framing/AAD, binding each blob with a distinct column name (`file_bytes`/`file_md`) | `Client.Application/Services/DocumentIngestion/UploadedFileBlobProtector.cs` |
| `EnvelopeCryptoService` | streaming chat envelopes (chunk/completed/reasoning) with per-kind AAD (`c0re-…` info strings) | `Services/Invocation/Envelope/Implementation/EnvelopeCryptoService.cs` |

Key properties worth preserving:
- **Single AEAD owner.** `AesGcmNodeAeadCipher` is the sole place `AesGcm` is constructed and the tag size lives — both the at-rest protector and the streaming envelope route through it. Don't construct `AesGcm` elsewhere.
- **Associated data binds context.** `NodePayloadProtector.BuildAssociatedData` mixes the conversation id, record id, column name, and a schema version into the AAD, so a ciphertext can't be replayed into a different row/column. Don't drop a component from the AAD.
- **Interceptor restores plaintext post-save** so the in-memory entity stays usable after `SaveChanges` (the DB row is ciphertext, the tracked object is plaintext again).
- **Content/metadata are read-both.** Message `content` and `metadata_json` are the only encrypted columns that ever held plaintext on disk, so their reader accepts both forms and a startup migration (`NodeChatContentEncryptionBackfillService`) rewrites legacy plaintext rows into the envelope in resumable, idempotent batches. Don't remove the header check or the read-both fallback — a partially-migrated table depends on it.

### Retention (data minimization)

Conversation retention is a separate privacy control, **disabled by default** (`ChatRetentionOptions.Enabled = false`, config section `ChatRetention`): it permanently deletes whole conversations older than the configured window, so it must be explicitly opted into. When enabled, the sweep and the interactive immediate-purge both delete a conversation's **complete footprint** through one shared helper (`ConversationFootprintPurge`): every child DB row (messages, tool events, **feedback**, **uploaded-file rows**, tombstones, and **`agent_execution_logs`** rows — the run-envelope and adaptive-memory telemetry keyed by `conversation_id`, so a purge leaves no residual per-conversation run metadata) plus the conversation row, then the on-disk upload blobs. The node connection enforces foreign keys, so `messages`, `tool_events` and the uploaded-file rows would cascade from the conversation on their own. The rest of the footprint would not: feedback, tombstones, `agent_execution_logs` and the work-session and integration families are keyed by `conversation_id` (or reached through it) with **no foreign key on purpose**, so those tables only go when the purge names them. That is why the full list is explicit, and why the shared helper is the single source of truth — so the two paths can't drift. DB rows commit first, blobs are torn down after, and an orphan resweep on each pass removes any upload directory whose conversation row no longer exists (covering a crash between the commit and the blob delete).

---

## 6. Privacy-sensitive AI runs node-local only

At the baseline, agent-memory/playbook **analysis**, the playbook **eval/golden-conversation gate**,
and memory extraction are wired to **node-local models**, not a cloud provider. This is the implemented
privacy contract behind the playbook pipeline; source wiring and tests establish the path, while this
page does not claim operational network observation. See [Agent Mode](04-agent-mode.md) for the
playbook analysis/evaluation flow and the adaptive-memory extraction loop; the wiring decisions live in
`XE-Local-AI-Engine.Client.Application/Services/*` and the provider seams in
`Providers.Abstractions` (`ILocalModelProvider` / `IChatClient` / `IEmbeddingGenerator`).

**Maintainer rule:** when adding any AI step that consumes user conversation/memory content for analysis or evaluation, route it through the node-local provider path. Do not let a cloud provider (Codex OAuth, etc.) become the executor for analysis/eval. Cloud credentials themselves are local-only secrets (§2).

### Two recent subsystems have explicit egress boundaries

- **Voice / text-to-speech delegates to Web Speech.** The repository makes no voice-model request, ships no voice inference runtime, and does not post generated audio to the node. Synthesis is provided by the browser/operating-system speech implementation; its installed voices, offline support, and any service network traffic are outside repository control. See [React Client](10-react-client.md).
- **An external connection's connect-time probe refuses a redirect.** `ExternalProviderProbeService` sends its probe with automatic redirects disabled, the same class of control `CustomToolSsrfGuard` applies to a custom-tool fetch: a probe that followed a redirect would carry the operator's key to whatever host the endpoint named. The probe may fall back to the stored key when the request supplies none, so testing an existing connection does not require re-typing the secret; that fallback is exactly why the redirect refusal is load-bearing.
- **Payloads that arrive from outside are fenced before a model reads them.** An integration's caller-supplied seed, the prior outputs replayed into a caller-managed session, and a `emit_output` payload replayed on a later turn all pass through `UntrustedContentFraming` before they re-enter the model's context. The fence carries a server-secret-derived nonce the caller cannot forge, and the framing is what separates "data an external caller sent" from "an instruction the node authored".
- **Inference profiling / machine key is local-only, per-box.** The per-machine launch-tuning profiles ([Local Runtime & Providers](03-local-runtime-and-providers.md)) are keyed by a `MachineKeyProvider` identifier that is a **local-only random id** — never hardware-derived, and `IMachineKeyProvider` documents it must **NEVER** be emitted in telemetry, aggregates, or logs. The profiles themselves hold only structural launch args (no secrets) and never leave the node. Keep the machine key off every outbound DTO/aggregate.

### Custom Tools: operator-authored execution boundary

Custom Tools are deliberately more privileged than built-in jailed AgentHome tools. An operator can author either an
HTTP request or a **host program** definition, so enabling the feature is an acceptance of outbound-network or
same-user host-execution risk. The `CustomToolsEnabled` node setting is a process-wide kill switch, and a definition is
not offered unless it is also enabled and carries the server-validated danger acknowledgement. `CustomToolCatalog`
wraps every resolved definition in `ApprovalRequiredAIFunction` unconditionally. Scheduler and
spawned-child paths therefore strip or retain it as approval-gated according to their existing
curation and no stored/per-agent flag lowers that floor. A trusted agentic-scope **root** MCP run is
the deliberate exception: it may adapt the wrapper only through ADR 0006's strict audit-before-call
path. `CustomToolsEnabled` remains excluded from the agentic settings whitelist, so the MCP settings
surface cannot enable host-command authoring unattended.

**Stored and browser-visible secrets.** `custom_tools.config_json` contains HTTP header or command-environment secret
values and is encrypted at rest by `NodeEncryptionSaveChangesInterceptor` with the
`custom_tool_config_json` AAD column name. List/detail DTOs replace each secret with a mask sentinel; an update that
round-trips that sentinel resolves it against the stored record instead of clearing or disclosing the value. Tool
descriptions are also encrypted; names, kinds, modes, parameter schemas, enabled/acknowledged flags, and versions are
structural plaintext. Secret header/env values are scrubbed from model-facing errors and output.

**HTTP fetch controls.** `CustomToolSsrfGuard.ValidateRequestUrl()` accepts only HTTP(S), rejects URL userinfo,
non-canonical numeric hosts, and private/loopback/link-local/CGNAT/metadata/reserved address ranges. A model-parameterized
host requires `allowedHosts`. The named client disables redirects and uses
`CustomToolSsrfGuard.CreatePinnedConnectCallback()` to validate every DNS result and connect to the validated address,
closing the DNS-rebind re-resolution gap. Requests have a 30-second wall-clock limit, a 64 KiB response-body cap,
credential-bearing response headers are stripped, and the process-wide concurrency limiter defaults to four runs.

**Host program controls and residual risk.** `HostProcessExecutor` does **not** use the AgentHome process jail: host
access is the feature. It never invokes a shell, expands each template item to exactly one
`ProcessStartInfo.ArgumentList` element, clears the inherited worker environment, overlays only an allowlist plus the
tool's fixed environment, enforces a 1–300 second timeout (30-second default), tree-kills on cancellation/timeout, and
caps each captured stream at 64 KiB. `HostExecutableGuard.Validate()` runs both while authoring and immediately before
launch: absolute path only, no shell/interpreter/script, existing regular file, symlink/reparse rejection. The Linux
regular-file check uses `statx` with `AT_SYMLINK_NOFOLLOW`, because a raw `(FileOptions)` cast for the flag throws. That
check and the later `Process.Start()` re-resolve the same path string as two separate syscalls, so a
small check-to-`execve` TOCTOU window remains — eliminating it would need `open(O_NOFOLLOW|O_PATH)+fstat+fexecve`, so
validation and execution share one open file description — and host commands retain the signed-in user's filesystem and network
rights with no per-process CPU/memory ceiling. Approval, time/output/concurrency bounds, and explicit acknowledgement
reduce risk; they do not create OS isolation.

---

### Untrusted content is fenced with an unforgeable marker

`UntrustedContentFraming` (`XE-Local-AI-Engine.AI.Agent/Tools/UntrustedContentFraming.cs`) wraps every
model-facing string that **originates from data** — retrieved knowledge-base chunks, read documents, uploaded
attachments, and their attacker-controlled metadata such as titles, section headings and file names — in an
explicit trust boundary. The retrieved text is data to be reasoned over, **not** instructions to be followed:
a prompt-injection sentence buried in a document ("ignore previous instructions", "approve this action") must
be visibly inside the boundary rather than silently concatenated into the prompt where it reads like a system
directive. The BaseScaffold instructs the model to treat everything between the markers as data, and callers
**must** put every attacker-controlled field, body and metadata alike, inside one fence via `WrapDocument`;
nothing attacker-controlled is emitted outside it.

The begin and end markers carry a per-wrap **nonce**, 32 lowercase hex characters. Random and derived nonces
are the same width deliberately, so a budgeter measuring the empty-body wrap overhead measures the length the
real body's wrap will have. Two factories produce it, and both close a distinct gap:

- **Random, per call** (`Wrap`, `WrapDocument` without a seed) for query-dynamic results such as the knowledge
  tools, whose output is not prompt-cache-sensitive. The value is unpredictable to whoever authored the
  document, so embedded text — even a verbatim copy of the marker prefix — can never forge the closing marker
  and break out of the fence. That closes the **fixed-marker forgery** gap.
- **Derived, and bound to the fenced content** (`WrapDocument` with a `nonceSeed`) for the prefix-stable
  attachment path, where the fenced block is a stable prefix of a multi-turn prompt and llama.cpp prompt/KV
  cache prefix reuse has to be preserved. It is an HMAC-SHA256 keyed by the server-side per-conversation seed
  over the SHA-256 of the canonical fenced payload — metadata plus body — not a bare hash of the seed. Keying
  by the seed keeps the marker unforgeable from inside the body; keying the *message* over the content makes
  two different attachments in the **same** conversation get **different** closing markers, which closes the
  marker-**replay** gap: an earlier attachment's model-visible closing marker cannot be embedded in a later
  attachment's body to force a break-out, because the later fence derives its marker from the later content.
  Byte-stability survives — the same conversation plus the same attachment content derives the same marker
  across sends.

The canonical inner payload is composed **once** and the content-bound nonce derived over exactly the bytes
that will sit between the markers, so the same seed and rendered payload always yield the same nonce and any
change to that payload yields a different one.

## 7. Sandbox / process-jail for tool execution

Any node-side tool or shell execution runs inside a process jail, not against the host filesystem directly. The live provider is `ProcessSandboxRuntimeProvider` (`Services/Sandbox/Implementation/ProcessSandboxRuntimeProvider.cs`, implementing `ISandboxRuntimeProvider`; selected via `SandboxProviderSelector`). The old Docker/container sandbox runtime was removed in the 2026-06-17 runtime re-architecture — there is **no** container inference path, and this process-jail is the execution boundary for AgentHome and Coder. (Discrepancy note vs. older docs: `LocalContainerSandboxProvider` and the HostAgent layer no longer exist as live code.)

> **One scoped exception, and it does not move this boundary.** [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md) (Accepted 2026-07-29) permits Docker for **Development Mode build/test/lint execution only**, as a stopgap ahead of MXC. Provider selection is **per feature**: Development Mode gets the container provider; **AgentHome (4 injection sites) and Coder (1) stay on `ProcessSandboxRuntimeProvider`** and keep exactly the posture described below. The split is enforced by each feature's declared requirements rather than by configuration — each feature resolves a role marker (`IAgentSandboxRuntimeProvider` / `IDevelopmentSandboxRuntimeProvider`) whose declaration names a host toolchain that no container backend supplies, so it cannot be wired into the other two even by mistake (ADR 0007; see [Backend selection](#backend-selection-a-feature-declares-what-it-needs-and-never-names-a-backend) for what replaced the compile-time form of this guarantee, and what that trade costs). Two things follow for a security reader. First, hardening the process provider is *not* superseded by the container work — those two features remain on it. Second, on Linux **access to the Docker socket is root-equivalent**; the ADR records this rather than mitigating it, and the product neither requires nor provides rootless Docker. The container provider has **shipped as an opt-in Development Mode provider** and is **not the default** — `DockerSandboxRuntimeProvider` (`Name = "docker"`) is registered by `AddNodeContainerSandboxExtensions` and selected by `Development:Sandbox:Provider=docker`. The shipped `appsettings.json` sets no `Development:Sandbox` key at all, so `SandboxProviderSelector.ResolveDevelopment` falls back to the AgentHome provider (`AgentHome:Sandbox:Provider`, shipped as `process`). **The section below therefore describes the default posture, not the whole story** — on a node configured with `docker`, Development Mode runs under the container boundary instead. See [Development Mode container implementation status](../roadmaps/development-mode-container-status.md) for what is and is not implemented; it is the canonical status page, and this page does not restate it.

**What this boundary is — and is not.** It is **supervised execution**, not an OS isolation boundary. What it enforces: a working-directory jail with path-confinement and symlink-escape guards; a **scrubbed child environment** (the worker's secret-bearing environment — cloud API keys, OAuth tokens, the node SQLite key — is **not** inherited; only a fixed system/toolchain allow-list is forwarded, plus the caller's explicit variables); a per-command timeout; tree-kill teardown; and captured-output byte caps. It is **not** a hardware or kernel isolation boundary, and — read these two before relying on the list above — it is **not** a limit on WHICH executable runs, and under the isolation mode AgentHome uses (`SandboxIsolationMode.None`, its declared `IsolationFloor`) it is **not a filesystem boundary for the child at all**. The non-isolated chain is `setsid` → `systemd-run --scope --user` → `unshare --user --net` → the executable (`SandboxLaunchPlan.Create`): a process group, cgroup ceilings and an empty network namespace, and **no mount namespace**. The only filesystem-shaped control is `ProcessStartInfo.WorkingDirectory`. A model-chosen command therefore reads and writes any path the engine's own user can — including the operator's ORIGINAL registered folder, which the workspace copy protects only by convention. **AgentHome therefore runs its sandbox under `SandboxIsolationMode.Filesystem` wherever the provider advertises it, and offers `run_command` only when the handle reports that boundary was delivered** — on a host that cannot isolate (including every Windows host today) the action is withheld and the model is told why, while the read and write tools stay because they are confined by the node's own path guard rather than by the jail. Measured, not inferred: `ProcessSandboxFilesystemReachTests` writes and reads a marker outside the jail and asserts that it lands, and its sibling shows the SAME provider confining the child once `SandboxIsolationMode.Filesystem` is requested — which AgentHome does request wherever the provider advertises it (`SandboxWorkloads.AgentHome.RequestsFilesystemIsolationWhereAdvertised`), so the Development capability page reports the role's real posture. On Windows none of the three wrappers exists at all, so the child is a plain process with the host's network and no ceilings either. AgentHome's goal loop runs a **model-authored command line** (`run_command`, gated on the `run_commands` action of an approval-gated call), so the executable and its arguments are the model's; only the working directory, the environment, the timeout, the output caps and the teardown are the node's. Development Mode is the contrast: it resolves a command **id** against a closed per-repository catalogue, so the model never names a binary there. Risky execution is approval-gated upstream, but no formal acceptance of the residual host-user execution risk is established by this repository documentation. **One consequence is worth naming separately:** because the engine itself runs git over that same workspace afterwards (AgentHome's patch export, Development Mode's evidence export), a model that can write there can leave behind configuration that makes the ENGINE's git run a program — later, unprompted, and outside the approved call. That is closed by making every configuration those git invocations read node-owned immediately beforehand (`AgentHomeGitHardening`, `DevelopmentWorkspaceGitConfig`), not by the jail.

**Network and resource containment are per mechanism and per host — never assume either from this page alone.** What the current host can actually deliver is *measured once at startup* into `SandboxContainment` (`Services/Sandbox/Implementation/Launch/SandboxContainment.cs`), and each mechanism is independently optional: process-group launch (`setsid`), CPU/memory/PID ceilings (`systemd-run --user`), and network isolation (`unshare` — a fresh **empty network namespace** with no route to host loopback, the LAN, or the cloud-metadata endpoint). Each is probed by really performing the operation, not by testing for the binary. Off Linux, and where every probe fails, the record is `SandboxContainment.None` and the child is a plain process with the host's network and no ceilings.

The **capability-honesty invariant** runs in both directions off that one record, which is what keeps advertisement and enforcement from drifting apart:

- `Capabilities` advertises `SupportsNetworkPolicy` / `SupportsResourceLimits` **only** where the matching mechanism is active.
- `BuildLaunchPolicy` (called by `CreateOrAttachAsync`) **fail-closed rejects** any `SandboxCreateRequest` asking for something the host cannot serve, with `SandboxCapabilityNotSupportedException`, rather than silently returning a sandbox weaker than requested. It rejects on three counts: `NetworkPolicy.Restricted` at all (no allow-list mechanism exists), a denial request when `SupportsNetworkIsolation` is false, and resource limits when `SupportsResourceLimits` is false.
- Each `…UnavailableReason` carries the measured reason, so a degraded host logs *why* and a live-gated test skips with a reason instead of passing silently.

> **Do not read that fail-closed gate as a guarantee for Development Mode — on a default-configured node it never fires on the process provider, so Development Mode DEGRADES rather than failing closed.** The gate can only reject what is *asked for*, and by default `DevelopmentWorkspaceProvider` asks for nothing the process provider cannot serve on any host: the agent-facing sandbox's `NetworkPolicy` is **capability-gated** (`None` where the backend advertises `SupportsNetworkPolicy`, `Unrestricted` otherwise — see the egress paragraph below), its `ResourceLimits` are likewise capability-gated (`SandboxResourceCeilings` returns none where `SupportsResourceLimits` is absent), and the read-only `.git/config` and credential-shadow mounts come only from a provider that advertises `SupportsReadOnlyMounts`. **An operator can turn the degradation into a refusal for egress**, per node, with `Development:Sandbox:RequireEgressDenial` (or `AgentHome:Sandbox:RequireEgressDenial` for AgentHome, Coder and work sessions): denial then becomes a precondition and a node that cannot deny refuses to prepare, naming the key. Both default to off. The consequence is platform-dependent and must not be stated once for both:
>
> - **On Linux**, the process provider does enforce real containment where the probes succeed — process-group launch, `systemd-run --user` ceilings, and `unshare` network isolation are each independently available, and AgentHome takes the empty-namespace path.
> - **On Windows** (and any host where every probe fails), the record is `SandboxContainment.None`. Development Mode's generated source, MSBuild targets, source generators and tests then execute **as the signed-in user, with full host network access and no resource ceiling** — the supervised-execution guarantees above (fixed executables, working-directory jail, scrubbed environment, timeouts, output caps) still apply, but there is no OS-level containment underneath them. A container-configured node is the only path that changes this.

Neither half may be softened into a silent no-op: a caller must never believe it received isolation the provider does not implement.

Two scope limits a reader must not overrun. **Egress is deny-everything or nothing** — where the mechanism is active the child gets an empty namespace, so egress is denied outright rather than filtered; `SandboxNetworkPolicy.Restricted` (an allow-list) stays unsupported and rejected. **Development Mode now requests the denial too, for the sandbox the agent's work runs in** (see [Development Mode egress](#development-mode-egress-two-sandboxes-one-of-them-denied) for the two-sandbox design, the capability gate, and what it does *not* cover). Both requests are **capability-gated**, so there is still no engine-wide egress posture to cite — though **Coder is covered too**, because `CoderWorkspaceReader` does not create its own sandbox: it attaches to AgentHome's via `ISandboxRuntimeProvider.ConnectAsync`, so that one policy decision covers AgentHome's 4 injection sites and Coder's single one (three tools, one injected provider — earlier text said 3, counting the tools). The request is itself **capability-gated** (`AgentHomeService.ResolveNetworkPolicy`): it asks for `None` only where the provider advertises `SupportsNetworkPolicy`, and `Unrestricted` otherwise — an unconditional request would be rejected fail-closed on any host without the mechanism. The AgentHome `policy.json` records the posture in force **at the time the agent home was initialised** (`"unrestricted"` when nothing is enforced, `"disabled"` when egress is denied); note that `EnsureBaselineFilesAsync` deliberately does **not** overwrite an existing `policy.json`, so a home created before denial shipped keeps a file reading `"unrestricted"` while the run is actually denied. That preservation is a tested contract protecting operator edits across re-init, and the drift runs in the safe direction — the file under-reports the boundary, never over-reports it — so read the provider's advertised capability, not this file, when you need the posture of a *current* run. An empty namespace is still not a kernel-hardened boundary: **this backend does not provide strong OS isolation; MXC remains an unintegrated provider behind the same seam**, and approval-gating upstream stays the interim control wherever a mechanism is inactive.

Guards a contributor must not weaken:

- **No inherited worker environment.** `ExecuteAsync` clears the child's environment and repopulates it only from `InheritableEnvironmentAllowlist` (PATH/HOME/temp/locale/`DOTNET_*` + Windows essentials), then layers the caller's explicit `request.Environment`. Never widen this to inherit the parent environment — the worker holds secrets that must not reach a sandbox command.
- **Fail-closed capability contract.** Do not soften `CreateOrAttachAsync`'s rejection of unenforceable network/resource guarantees into a silent no-op; a caller must never believe it received isolation the provider does not implement. Equally, do not advertise a capability the startup probe did not measure as active — both halves must keep reading the same `SandboxContainment`.
- **The user-bus environment strip is load-bearing, not tidiness.** A network namespace does **not** confine UNIX sockets. `systemd-run --user` needs `XDG_RUNTIME_DIR` to reach the per-user systemd bus, and a sandboxed child that inherited it could start a unit **outside** its own scope and namespace — escaping both the resource ceiling and the egress denial. Verified live before the fix. Those variables are injected for the launch **wrapper only** and stripped by an `env -u` layer immediately before the sandboxed executable is exec'd; never let them reach the child.

- **Path confinement.** `ResolveJailPath` and `IsUnderJailRoot` reject any path that escapes the jail root before any file op happens.
- **Symlink-escape guard.** `EnsureNoSymlinkComponentsUnderJail` walks from the resolved leaf upward to the jail root and throws `UnauthorizedAccessException` on the *first* symlink component — defeating a "plant-a-symlink-after-resolve" swap (`ProcessSandboxRuntimeProvider.cs`).
- **O_NOFOLLOW file I/O.** Host file reads/writes use a libc `open()` `DllImport` with `O_NOFOLLOW` (plus `O_CLOEXEC`), because a managed `(FileOptions)` cast for `O_NOFOLLOW` throws. The kernel fails with `ELOOP` if the leaf is a symlink, closing the check-then-open (TOCTOU) race a managed `lstat`+open would leave. See `OpenNoFollow`, `ReadJailFileBytesNoFollowAsync`, `WriteJailFileNoFollowAsync` in `ProcessSandboxRuntimeProvider.cs`. A historical finding: plain `git apply` did *not* reject a `--binary` literal patch, so byte-level guards are not optional.
- **Byte caps + growth check.** `ReadHostFileUnderGuard` enforces a per-file byte cap and blocks (returns `null`) if the file *grew* after sizing — a swap-after-walk signal — rather than silently truncating.
- **Tree-kill teardown.** Killing a sandbox `TreeKill`s the process tree (`process.Kill(true)`) and best-effort deletes the jail dir, so a sandbox kill terminates every running command.

### 7.1 The isolated launch mode (`SandboxIsolationMode.Filesystem`) — opt-in, consumed by `run_python`

Everything above describes the **default** posture: a supervised child in a working-directory jail that can still *read* everything the engine's own user can read. A second, opt-in posture now exists behind the same provider — `SandboxCreateRequest.Isolation = SandboxIsolationMode.Filesystem` — in which the host filesystem is **not present in the command's mount namespace at all**.

**Status: built, probed, and consumed by two callers — `run_python` and a `Sandboxed` stdio MCP server (see [§7.2](#72-outbound-mcp-servers-run-under-a-declared-trust-tier)).** `ComputeToolGateway` names `SandboxIsolationMode.Filesystem` on every invocation and **refuses the call** on a node whose provider does not advertise `SupportsFilesystemIsolation` (see [Compute Tools §2.1](19-compute-tools.md#21-execution-flow)). `SandboxedMcpStdioTransport` does the same for an MCP server and refuses the connection the same way. AgentHome, Coder and Development Mode create sandboxes without naming an isolation mode and therefore still run the byte-identical chain they always did (asserted by `SandboxFilesystemIsolationContractTests`); filesystem isolation is not part of their current boundary. Nothing on this page's default posture has moved.

**What the isolated chain is.** `setsid` → a named transient `systemd-run --user --scope` → `bwrap`, rendered by `SandboxIsolatedChain` (`Services/Sandbox/Implementation/Launch/Isolation/`). Inside it the workload sees: a read-only bind of `/usr` plus whatever legacy roots (`/bin`, `/lib64`, …) this host's layout needs to make an ELF interpreter resolve; an invented four-file `/etc` (`passwd`, `group`, `nsswitch.conf`, `hosts`) generated byte for byte into **sealed `memfd`s** rather than bound from the host, so the machine's real account database is never exposed; `/dev` and `/proc`, both remounted read-only; empty `/home`, `/run`, `/var`; any explicitly named read-only trees at their own canonical paths; and exactly one writable directory, `/work`, which is the engine's jail. `/tmp` is the jail's own subdirectory, so everything the workload writes stays inside the one tree the disk watchdog walks. The environment is `--clearenv` plus a fixed allow-list. PID, IPC, UTS and network namespaces are unshared; `--disable-userns --assert-userns-disabled` closes the nested-user-namespace route back out.

**Bind sources are file descriptors, never pathnames.** Every bind is `--bind-fd` / `--ro-bind-fd` against a descriptor the engine opened itself with `openat2(RESOLVE_BENEATH|RESOLVE_NO_SYMLINKS)`, having checked the ownership of each component *as the descriptor it just opened*. A pathname handed to `bwrap` would be re-resolved in another process at a later moment, and anything able to rename a component in between would redirect the mount; a descriptor names the inode that was already validated. There is **no pathname fallback**: a host where the descriptor chain cannot be established reports the capability as absent. The descriptors survive all three execs because they are not close-on-exec — measured on this host, which is why no `posix_spawn` shim is needed.

**Helper binaries are resolved without consulting `PATH`.** `TrustedBinaryResolver` searches only `/usr/bin`, `/bin`, `/usr/local/bin` and requires every path component, symlink targets included, to be root-owned and not group- or world-writable. The other resolver in this layer prefers `PATH` because for the resource-limit chain that is an availability question; here it is a trust question, and a workload that could plant a `bwrap` earlier on `PATH` would be choosing the program that builds its own jail.

**The capability is measured, not assumed.** `HostSandboxContainmentProbe` runs the **production chain** once against a throwaway 0700 jail and checks fifteen controls before advertising `SupportsFilesystemIsolation`: canaries under the user's home and beside the jail are invisible inside while still existing outside; the workload is pid 2 and a host pid is absent from its `/proc`; `/work` and `/tmp` are writable; `/dev` answers `EROFS` and `/proc` refuses creation while `/dev/null`, `/dev/urandom` and `/proc` reads still work; `/run` is empty and neither the user-bus nor a docker socket path exists; and a loopback connect to a **live host listener** fails inside while succeeding outside. That probe is caught **separately** from the resource-limit and network probes, so its failure withdraws only this capability. `CreateOrAttachAsync` rejects the request fail-closed (`SandboxCapabilityNotSupportedException`, carrying the measured reason) on a host that cannot deliver it, and a launch-time failure returns a non-completed result **without running the command** rather than quietly running it on the host filesystem.

**Termination is the scope's cgroup, not the process tree.** The workload's processes live in a PID namespace the engine cannot see, and the pid it holds belongs to `setsid`. The kill authority is therefore `systemctl --user kill --kill-whom=cgroup --signal=SIGKILL --wait <unit>` against the transient scope named at launch (`xe-<role>-<32 hex>.scope`), with the process-group kill kept as a fallback. It is applied at timeout, cancel (which previously had no group kill at all), sandbox kill, disposal, the disk ceiling, and caller cancellation; the unit name is recorded in the orphan marker so the next start can reap it, and a startup sweep kills engine-owned scopes no live worker still claims. That marker is written **before** the launch that creates the scope — `systemd-run` creates it as its first act, so a marker written afterwards would leave a window in which a second worker's sweep saw a live command's scope unclaimed and killed it — and the sweep additionally skips any unreferenced scope that has been active for less than 30 seconds, or whose age the user manager did not report. `RuntimeMaxSec` bounds a scope whose engine was hard-killed. A live test covers the case that motivates all of it: a **detached grandchild** started with its own `setsid`, which a tree-kill and a `kill(-pgid)` both miss.

**What it is not.** It is a namespace boundary, not a kernel-hardened one — no seccomp filter, no LSM profile, no user-namespace-free design; a kernel LPE is out of scope for it exactly as it is for the default posture, and strong isolation remains MXC's job behind this same seam. There is no read-only *mount* capability (`SupportsReadOnlyMounts` stays off; `ReadOnlyTrees` is the isolated-mode surface, and a tree under a mount point the chain owns — `/usr`, `/dev`, `/proc`, `/work`, `/tmp`, the legacy roots — is **rejected** rather than mounted and silently shadowed). Isolation and a trusted host workspace are refused together: an isolated jail is tightened to 0700 and unreachable at its host path, which is the opposite of what a preserved checkout is for. And the jail-disk watchdog underneath it is unchanged — a best-effort visible-file occupancy check sampled every two seconds, which an unlink-then-write loop bypasses entirely. It is not a quota, and nothing here should be read as one; the current provider has neither a project quota nor a size-bounded mount.

**What `run_python` binds, and why it is two trees rather than one.** The compute tool names its own `ReadOnlyTrees`: the provisioned venv (`<compute-runtime>/venv/.venv`) and the uv-managed CPython root it links into (`<compute-runtime>/pythons`). Not the compute cache root above them — that also holds the uv download cache, the digest-pinned uv binary and the lockfile state marker, and naming the parent would have handed all of it to a model-authored script for free. Not the single installed CPython version either: uv addresses the install through a version-alias symlink beside it (`cpython-3.13-…` → `cpython-3.13.15-…`) which the venv's own `bin/python` points at, so binding only the versioned directory leaves that alias resolving to nothing inside. Both are bound **at their own canonical paths**, which is what lets the venv's compiled-in absolute paths keep working, and both are read-only: a script's `os.chmod` on `site-packages` or on the interpreter now answers `EROFS` regardless of who owns the inode. The venv's cleared write bits are still applied, demoted to what they always were — defence in depth for what happens *outside* the namespace.

The same no-follow / byte-recheck philosophy appears in AgentHome host-path safety (`Services/Workspace/Implementation/HostPathSafety.cs`: `TryResolveReparseWithinRoot`, `IsReparsePoint`, `IsPathWithinRoot`) and `HostGitRunner`. Reuse these utilities rather than re-implementing path validation.

### 7.2 Outbound MCP servers run under a declared trust tier

An outbound stdio MCP server is a third-party executable the operator installed. Legacy registrations ran as plain
engine child processes with only an environment scrub between them and the machine. Each registration now carries a
**trust tier** (`McpTrustTier`), and the tier decides where its process runs. The full rationale, including why
there is no `Remote` tier and why existing rows migrated the way they did, is
[docs/security/mcp-trust-tiers.md](../security/mcp-trust-tiers.md); what a security reader needs from this page is:

- **`Sandboxed` is the default, including for every registration that already existed.** The server is launched inside
  the substrate under `SandboxWorkloads.McpStdio` — the §7.1 chain, so no host filesystem, an empty network namespace,
  a disposable jail as the working directory, and only the configured environment variables. Its own package tree (the
  resolved command's directory, and the configured working directory when there is one) is bound **read-only**; the
  jail is the only writable surface it has.
- **Neither bound tree may cover a sensitive host root.** A tree that equals or contains the home directory, a
  credential store under it (`~/.ssh`, `~/.gnupg`, `~/.aws`, `~/.azure`, `~/.config`, `~/.docker`, `~/.kube`), the
  node data directory, the engine's install directory, `/root`, `/etc`, `/var` or `/` is **refused**, naming the
  path and the tier. Subtrees of those roots stay bindable — `~/.nvm/…/bin` exposes a node install, `$HOME` exposes
  the operator — which is what keeps `npx`- and `uvx`-based servers usable at the default tier. Comparison happens
  on resolved paths at one gate both trees pass through, and the list is code-owned.
- **It fails closed, and it is visible before it fails.** A host whose backend does not advertise
  `SupportsFilesystemIsolation` — Windows, or a Linux host without bubblewrap — refuses the connection
  before a process exists, with an engine-authored reason that names the tier and is surfaced verbatim rather than
  redacted. The Development status isolation panel carries an `mcp-stdio` row that says the same thing ahead of any
  connection attempt.
- **`PrivilegedHost` is the old host launch, kept deliberately.** It is a per-server operator grant, never a fallback
  and never inferred, and its tools are offered as `ToolCategory.WriteExecute` rather than `Network` — because a
  server this node launched unconfined can write files and run commands here, and the class an operator sees and the
  node policy tightens on should say the stronger of the two.
- **`BuiltInTrusted` is engine-owned and unreachable from the API.** The CRUD surface rejects it and a schema check
  constraint bounds the column.
- **The stored environment does not come back out.** `EnvJson` was already AEAD-encrypted at rest; the response now
  returns the variable NAMES with a fixed mask in place of every value, and an update that sends the mask back keeps
  the stored secret (`McpEnvironmentMask`).

Unchanged: HTTP MCP registrations stay exact-match loopback (`McpOptions.HttpLoopbackHosts`, re-validated at connect
time), the tier is inert for them, and every MCP tool of every tier remains approval-required, pre-wrapped in
`ApprovalRequiredAIFunction`, and ineligible for a remembered session approval.

### 7.3 External Apps run under an engine-owned container policy

**External Apps** installs curated, containerised applications from an XE-authored catalog
([ADR 0010](../adr/0010-external-apps-container-execution.md), [External Apps](23-external-apps.md)). It is the
**second** consumer class of the Docker socket after §7.1's sandbox, and it does not widen that grant — the socket is
root-equivalent either way, which is the whole subject of the ADR. The sandbox SPI is untouched: an application that
publishes a port and writes to a data directory violates `DockerSandboxHardening`'s contract by construction, so
External Apps stands beside that contract with its own rather than loosening it.

What a security reader needs from this page:

- **One policy builds every container, and the daemon's read-back is verified against it.**
  `ApplicationContainerPolicy.BuildSpecification` sets `cap_drop ALL`, `no-new-privileges:true`, the repo's seccomp
  profile, the instance's own private network, an explicit mount set, `unless-stopped`, the manifest's `pidsLimit`
  and a digest-pinned image. `FindViolations` re-reads the daemon's own view **before and after start**; a
  disagreement is a `PolicyViolation` failure that stops the instance, not a warning.
- **Publishing is loopback-only.** A published port binds ordinal `127.0.0.1` — `::1` is refused — and only ports
  the manifest declares. This is checked in the specification and again on read-back.
- **Containers may run as in-container root, deliberately.** No `--user` is passed: the curated images drop
  privileges through their own entrypoints, and forcing a uid breaks that and a port-80 bind. The boundary is the
  container, the dropped capabilities, seccomp, `no-new-privileges` and the loopback network — **not** the uid.
  `capAdd` is bounded by Docker's own default 14, so a service can never exceed an unhardened `docker run`.
- **V1 enforces no outbound network restriction.** `permissions.internet` is always true and
  `permissions.localNetwork` is a **disclosure** on the install panel, not a control. Nothing denies either. Read the
  panel as what the application may do, never as an enforced boundary. There is likewise **no memory and no CPU
  ceiling**: the manifest's memory figures gate admission only.
- **The one container that keeps Docker's default capability set is engine-owned and short-lived.** Reset and
  uninstall delete an instance's volume contents from a digest-pinned helper container with a read-only root
  filesystem, no network, no environment, a 64-process limit and exactly one bind mount — that instance's volumes
  directory. It keeps the default capabilities because `CAP_DAC_OVERRIDE` is what lets in-container root unlink the
  `0700` directories an application's own non-root user left behind; under a rootless daemon those belong to a host
  uid inside the operator's subuid range that the engine cannot even traverse. With that helper, **uninstall really
  does delete everything the application stored**, on rootless and rootful daemons alike — verified live against
  exactly such a subuid-owned subtree. The engine removes the emptied tree host-side and counts what is left rather
  than trusting an exit code.
- **Secrets stay in one encrypted column and are never rendered.** A manifest `secret` variable is the user's own
  credential: values live in `external_app_instance_variables_json`, AEAD-encrypted and AAD-bound to the row's id,
  masked on the way out and preserved when the mask comes back in. `ApplicationManifest` suppresses its record
  `PrintMembers`, so a structured log of a snapshot cannot print the catalog or a variable default, and
  `FailureSummary` is content-free by contract — category prose, a service name, the resource gate's two figures,
  never a variable value and never a daemon message. Container **logs are unmasked by design**: the text is the
  application's own output, not an engine-owned value.
- **A daemon endpoint is redacted before it is logged, pinned, or returned.** `DockerDaemonEndpoint.Display` drops
  user information, the query and the fragment whole — an operator-set `DOCKER_HOST` is somewhere a token fits, and
  the paths that refuse such an endpoint would otherwise disclose it in the course of declining to use it. A pin
  written before that existed is redacted on the way back off disk, and an endpoint that carries any of the three is
  refused by name, never by quoting it.
- **Ownership is by label, and foreign containers are reported, never removed.** Container names repeat across two
  XE installations pointed at one daemon, so every container and network carries an owner label, a per-installation
  install id, an instance label and a service label. Owner-labelled containers under a *different* install id are
  counted as `foreignInstallContainers` and left alone.
- **Catalog trust is curation plus HTTPS plus digests. There is no signing in V1.** A configured refresh URL must be
  `https://`, with plain HTTP accepted only for `127.0.0.1`, `::1` and `localhost`; redirects are not followed, and a
  document that fails any validator rule is rejected whole.
- **A secret variable's value is masked on the way out, and that is what makes the at-rest AEAD mean anything.** Stored variables hold an application's admin password and API keys, AEAD-encrypted at rest in `ExternalAppInstance.VariablesJson`. There is no editing reason to read one back — the settings form needs the variable *definitions*, never the values — so every secret value leaves the node as the single `ExternalAppVariableMask.Value` sentinel, and a configure or update that sends the sentinel back means "keep what is stored". Without the mask, anything holding a session could read the plaintext and the encryption would protect only the disk. It is one symbol shared by both sides on purpose: a mask the write side did not recognise would silently store the placeholder as the password.
- **The kill switch is a surface switch, not a stop button.** `ExternalApps:Enabled=false` 404s every route and the
  hub negotiate at the request-path middleware, ahead of the security middleware, so the switch cannot be probed by
  status code. It does not stop running containers and does not hide the navigation group, which is compile-time.
  The safe order is **stop or uninstall every instance, then disable**.

### 7.4 The seccomp profile every sandbox container carries

`DockerSeccompProfile` ships Docker's own default seccomp profile as an embedded resource and passes it explicitly
on every container create. Three things about that are worth having written down, because each answers an obvious
"why not do it the simpler way".

**Provenance, in full.** The bytes in `seccomp-default.json` are copied verbatim from
`https://github.com/moby/profiles/blob/seccomp/v0.2.3/seccomp/default.json` — tag `seccomp/v0.2.3`, commit
`836ae4d37ef2ec995c77c99fc55f5b5f3af3a897`, SHA-256
`536529b665dd0972c37bfb569f5d4ac8a53592e7b00752bc39ff063ca9864c74`, fetched 2026-08-25. That module is what the
daemon itself vendors: `moby/moby`'s `vendor/modules.txt` pins `github.com/moby/profiles/seccomp v0.2.3`. So the
profile shipped here is the daemon's builtin, not a hand-written approximation.

**Why this cites `moby/profiles` and not `moby/moby`.** The profile moved out of `moby/moby`'s
`profiles/seccomp/default.json` after v28.0.x, and that path 404s on current tags. The split-out repository is the
live source; citing the daemon repository would give a reader a dead link and no way to re-verify the SHA-256.

**Why ship a copy at all, when the daemon applies this by default.** Because "by default" is not verifiable. A
container created with no `seccomp=` option reads back with `SecurityOpt: null` (measured against a current Docker
Engine), which is the *same* read-back as a daemon started with seccomp disabled entirely. Asking for the profile
explicitly is the only way the fail-closed read-back in `DockerSandboxHardening.VerifySecurityOptions` can tell a
confined container from an unconfined one.

**Why it is an embedded resource and not a file on disk.** The Engine API takes profile **content**, not a path.
The `docker` CLI reads the file named by `--security-opt seccomp=<path>` and sends its JSON; the daemon never opens
a host path on the client's behalf. Measured against a current Docker Engine, a container created with
`--security-opt seccomp=/tmp/default.json` inspects back as `seccomp={"defaultAction":…}` — the compacted JSON —
and never as the path. There is therefore nothing to materialize in the node data directory for the daemon to read.

### Development Mode source and execution boundary

Development Mode ships enabled by default. `Development:Enabled=false` is the backend emergency switch for an
operator who does not accept same-host-user code execution. Availability does not authorize a repository by
itself: the operator must register a local Git repository, and normal Development contracts refer to it only by
an opaque selected-folder ID and alias. The host path stays internal to the node and is encrypted through the
existing selected-folder persistence path.

The selected folder authorizes the source repository. The agent does not work in that source directory:
`DevelopmentWorkspaceProvider` creates an engine-owned detached Git worktree under node data and exposes that
managed worktree through the Process sandbox. Before execution, preview, and apply, the Development binding path
resolves the stored selected-folder ID, canonicalizes the Git top-level, and compares its identity hash with the
project's persisted repository identity. A moved, replaced, unavailable, or mismatched repository fails closed.
An older project without a selected-folder binding cannot execute until the operator reconnects the exact
repository identity.

Only the final reviewed apply path may change the registered source repository. The review evidence binds the
patch to its expected base and content hashes, and apply revalidates those values immediately before mutation.
Changes in the detached worktree do not bypass this gate.

These controls limit what the **application's Development tools** read, write, preview, and apply. They do not
limit what executed repository code can do. Generated source, MSBuild targets, source generators, build scripts,
and tests run as the host user and have that user's **host filesystem** access — the workload declares an isolation
floor of `None` (`SandboxWorkloads.DevelopmentModeHostToolchain`), so there is no host-filesystem boundary under
them on any backend the shipped configuration resolves. **Network access is no longer part of that sentence**: the
agent-facing sandbox asks for egress denial wherever the backend advertises it, and reaches the network
unrestricted only where it does not — see
[Development Mode egress](#development-mode-egress-two-sandboxes-one-of-them-denied) for the two-sandbox design and
the capability gate. **CPU, memory and process-count ceilings ARE requested for these commands** — since 2026-08-25
both sandboxes `DevelopmentWorkspaceProvider` creates carry the host-toolchain profile
(`SandboxWorkloads.DevelopmentModeHostToolchain.Ceilings`), applied wherever the backend advertises
`SupportsResourceLimits` and reported by the isolation panel as served. The numbers are **not** `run_python`'s, and
that split is measured rather than argued: under the compute profile's 2 CPU / 2048 MB / 64 tasks this repository's
own Release build failed outright with 15 errors ("Resource temporarily unavailable" starting `csc`, "Failed to create
CoreCLR"), and under the 2048 MB ceiling alone it was SIGKILLed mid-build printing no summary, while 8192 MB completed
it in 33.6 s. Toolchain roles therefore read `LocalContainer:ToolchainLimits`, whose unset members derive from the
host — all logical cores, 75% of physical RAM floored at 4096 MB and capped at physical RAM, and 4096 tasks — and
whose overrides are floored by `LocalContainerOptionsValidator` (memory 1024 MB, PIDs 256) because on Linux these
become `MemoryMax` with swap denied and a thread-counting `TasksMax`, where a too-small ceiling kills every attempt
rather than bounding it. The Process
sandbox and Agent Home are application-level path, byte, environment, and lifecycle controls; neither is an OS
security boundary. Do not describe the selected folder as a kernel-enforced filesystem allow-list.

MXC is not integrated through the existing sandbox or workspace seams, and no current MXC profile is a security
boundary. A provider exposed through those seams must implement and independently validate every isolation
guarantee it advertises.

#### Development Mode egress: two sandboxes, one of them denied

Before network denial was implemented, `DevelopmentWorkspaceProvider` created one sandbox with
`NetworkPolicy = Unrestricted`. That was the one live High-risk gap in this feature: a malicious package's
restore hook, an MSBuild target, or a test could read the whole clone and POST it out. It is now closed on the
axis the engine controls, in three parts, and the honest statement of what remains open matters as much as what
does not.

**One sandbox cannot have network for one command and not the next.** `SandboxNetworkPolicy` lives on
`SandboxCreateRequest` and is fixed at create; `SandboxCommandRequest` has no network field. So the design is
two sandboxes, not one sandbox with two postures.

1. **A warm restore, from the base commit, with egress.** Before the agent-facing sandbox exists, `PrepareAsync`
   creates a second short-lived sandbox (`RuntimeProfile = "development-warm"`, its own `SandboxAttachKey`, the
   same mount set) and runs exactly one command: the frozen profile's `dotnet_restore`. Then it kills it. The
   per-task `NUGET_PACKAGES` / `DOTNET_CLI_HOME` roots and the generated `obj/` trees outlive it, which is what
   lets the later `--no-restore` build and `--no-build` test work with no network at all. Running
   repository-authored MSBuild with egress is sound **here and only here**: at warm time the tree *is* the base
   commit — the operator's own repository, already trusted to the degree the whole feature trusts it — and the
   agent has written nothing. The gate is therefore not "is this code safe" but "is this tree provably still the
   base commit": a warm runs only from a worktree whose **tracked** files are clean
   (`git status --porcelain --untracked-files=no`), once per `BaseCommit`, with the result recorded in
   `workspace.json` — which lives in `RuntimePath` and is **never mounted**, so it cannot be read or forged from
   inside any sandbox. A profile with no restore command (`generic-git`) skips warming entirely.

2. **A dependency-manifest change fails validation.** `DevelopmentDependencyManifestPolicy` runs *before* the
   command loop and fails the gate with `dependency_manifest_changed` for any change to `**/*.csproj`,
   `**/Directory.Packages.props`, `**/Directory.Build.props`, `**/Directory.Build.targets`,
   `**/packages.lock.json`, `**/NuGet.config`, the npm/yarn/pnpm lockfiles, `**/Cargo.toml`, `**/Cargo.lock`,
   `**/requirements*.txt`, `**/pyproject.toml`, `**/uv.lock` or `**/poetry.lock`. Added counts as much as
   modified — a new `Directory.Packages.props` changes resolution for the whole tree. This is a **verdict, not a
   `DevelopmentWorkspaceSecurityException`**: the task moves to `ChangesRequested` carrying the reason, because
   "delete the failing test" is an attack and "add a package" is a legitimate task this version cannot serve.
   The set is code-owned and versioned with `DevelopmentCommandProfileCatalog.CurrentVersion`; a packaging system
   missing from it is a hole, not a gap in coverage. `Directory.Build.props` and `Directory.Build.targets` are build
   configuration rather than dependency manifests, but either can carry a `PackageReference`, so both are in the set
   — a different control from `EnsureBuildConfigurationBarrier`, which bounds MSBuild's upward search to
   configuration from *above* the workspace.

   That `ChangesRequested` hop is written by `DevelopmentStore.FinalizeValidationAsync` as a
   **`ValidationFinalized`** event, not a `TaskTransitioned` one — it is a status-changing event all the same, and
   an audit built from `TaskTransitioned` rows alone will not show it (wiki [08](08-data-and-persistence.md)).
   The hop also **spends a round**: `MaxReviewRounds` is the budget of attempts to get *through* the gates, so a
   failed deterministic gate costs one exactly as a reviewer rejection does, and a task that exhausts it is stood
   down at `Blocked` rather than reworked again.

   **A task's round budget is immutable except that an operator's Retry widens it by one.** That is the single
   edge out of `Blocked`, and `DevelopmentStore.TransitionTaskAsync` refuses it to any command that does not also
   widen the cap, so a task cannot be let out of `Blocked` into a round it has no budget to finish. Only
   `DevWorkflowDevTaskExecutor.CarryOperatorRetryAsync` sets it, once per node-run attempt under the retry's own
   operation id, and only for a task whose block IS the round cap — a task blocked on anything a round cannot fix
   is left where it is. Before this, a Retry on a workflow node blocked at the cap re-dispatched the node, which
   re-read a task still at its cap and stood itself down about two seconds later, spending one of the node's own
   attempts each time and never starting a coder round, so the reason typed into the retry box could not reach a
   model.

   **Known ceilings of the rework surface**, recorded so they are not re-discovered as bugs:
   an operator instruction on a task **no workflow ever drove** has no `WorkflowPolicyApplied` row to bound it and
   therefore governs every later round of that task; a **reviewer's** request for changes never reaches the task's
   `blocked_reason` column (only a gate failure or a workflow/operator transition writes it), so the overview card
   can be empty on a reviewer-driven rework; and `blocked_reason` is one last-write-wins column, so it shows the
   most recent request only — the durable event timeline is the full history, by design.

3. **The agent-facing sandbox asks for `SandboxNetworkPolicy.None`.** Capability-gated exactly as AgentHome's
   request is (`DevelopmentWorkspaceProvider.ResolveAgentFacingNetworkPolicy`): `None` where the backend
   advertises `SupportsNetworkPolicy`, `Unrestricted` where it does not.

> **The Option-B caveat, stated plainly.** A backend fails a confinement request it cannot honour *closed*. An
> unconditional `None` would therefore not harden Development Mode on Windows — or on any Linux host whose
> `unshare` probe failed — it would remove Development Mode from those nodes, because the shipped configuration
> resolves them to the process backend. On such a node **the attempt still has full host network access**, and
> the abuse case above is still live there. What makes that acceptable rather than silent is that the
> Development status surface reports the posture the provider actually **served**, not the one that was
> requested. Mandatory denial on every node is not part of the current cross-platform contract.

Two things this does *not* close, on any backend. A private feed named by the repository's own `NuGet.config` is
reached by the **warm** restore, which is correct behaviour and will read as "restore worked, build failed" if
that feed is unreachable later. And a repository whose restore is not idempotent — a hook that writes into
`obj/` differently under `--no-restore` — can warm green and build red; the synthetic fixture cannot show this.

#### Committed credentials in the clone

`CreateStandaloneWorkspaceAsync` runs `git clone`, so only **tracked** content reaches the workspace: an
untracked `.env` in the operator's repository does not ride along. The real exposure is a **committed**
credential, which is common enough to matter, and every prepare now answers for it in two parts.

Detection is unconditional. The engine asks `git ls-files` and tests every path segment against
`ISensitiveFileExclusionService.IsSecret` — the same predicate the workspace read tools and AgentHome's copy
filter use. The set is recorded in `workspace.json` and emitted as an operator-visible `DevelopmentEvent`
(`WorkspaceSecretsDetected`, idempotent per attempt). It never blocks the attempt.

Neutralization is capability-gated. Where the backend advertises `SupportsReadOnlyMounts`, each detected path is
shadowed by an **engine-generated empty read-only file mount** at that path — the mechanism `.git/config`
already uses, with `SandboxMount.TargetIsWorkspaceRelative` set because the mount *source* has to live outside
the workspace. The file on disk is never touched: deleting or emptying it would make the tree dirty against its
base commit, so `ValidatePreservedWorktreeAsync` and the `SubjectHash` would see a deletion and an apply would
delete the operator's real file. The set is capped at 32, above which the prepare fails closed rather than
shadowing some — a partial shadow reads as a control and is not one.

> **On the process backend — today's default — only detection applies.** It has no mount layer, so nothing is
> shadowed and the recorded event is the whole control: the engine can see the committed credential but cannot
> stop the repository's own build or tests from reading it. Do not read this section as parity between the two
> backends.

Accepted trade: `SecretEntryNames` includes `.env.*`, which matches `.env.example`. Shadowing it is harmless in
most repositories and confusing in a few; it is the same trade AgentHome's copy filter already makes. And a
committed test certificate a build legitimately needs will turn a green repository red — the recorded event is
what makes that diagnosable in one look.

**Container-backed Development Mode execution has shipped, opt-in and off by default.**
[ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md) (Accepted 2026-07-29) approves a
Docker-backed provider behind the same `ISandboxRuntimeProvider` seam for Development Mode build/test/lint
execution — as a **stopgap ahead of MXC**, which stays the long-term hard-isolation seam. That provider now
exists and is selectable, but nothing selects it for you: the paragraphs above describe what executes on a
**default-configured** node, and remain accurate there. Set `Development:Sandbox:Provider=docker` and Development
Mode moves to the container boundary instead. **[Development Mode container implementation status](../roadmaps/development-mode-container-status.md)
is the canonical, maintained record of what is implemented** — read it rather than inferring shipping state from
this page. What the decision fixes, and what a reviewer should hold it to:

- **A running daemon is a hard requirement for the feature, with no unisolated fallback.** No daemon means no
  Development Mode — it must fail with an actionable message rather than silently degrading to the process
  provider, so an operator can tell from the outside which posture ran.
- **Repository-supplied container configuration is rejected wholesale.** Engine-generated canonical mounts only;
  no socket or named-pipe mounts, no devices, no `--privileged`, no added capabilities, no host PID/network/IPC
  namespaces; operator-approved digest-pinned images only; no repository Dockerfile builds; no `${localEnv:*}`.
  A `devcontainer.json` in a repository the agent can write is untrusted input, and a Docker-socket mount is full
  host compromise.
- **On Linux, Docker-socket access is root-equivalent.** The ADR documents this rather than mitigating it. Rootless
  Docker is the operator's option; the product neither depends on it nor claims it. Do not describe the container
  provider as removing host-user risk.
- **A pinned image digest pins bytes, not hermeticity.** Mounts, runtime state, host kernel, platform, dependency
  resolution and network inputs all stay variable. Do not describe digest pinning as reproducibility.
- **The scope is narrow by construction, and widening it is a new operator decision**, not an implementation
  detail.

#### The managed workspace's Git configuration is engine-owned

The engine runs `git reset` and `git add -A` **on the host** against the managed worktree, so anything a repository's
own `.git/config` can make Git execute runs on the machine running the engine — `core.fsmonitor` on any index
refresh, and a `filter.<driver>.clean` selected by an in-tree `.gitattributes` on `git add`. The standalone clone
made `<workspace>/.git/config` a real, agent-writable file inside the jail, on the process provider Development runs
on by default.

`-c` pins are not enough. They close `core.fsmonitor` and outrank every include chain, but they cannot close
`filter.*.clean`: driver names are arbitrary, so there is no finite set of keys to pin, and Git has no flag that
disables attribute processing. `DevelopmentWorkspaceGitConfig.RestoreMinimalAsync` therefore **rewrites** the file to
a minimal one immediately before the first host-side Git command. A filter driver has to be *defined in config* to
run, and an in-tree `.gitattributes` naming an undefined driver is a no-op, so removing every definition closes
`filter.*.clean`, `core.fsmonitor` and any future exec-bearing key at once without enumerating key names. That is the
same property the read-only `.git/config` bind mount gets on the container side, and it is provider-independent.

Minimal is not empty. A clone of a repository using a newer format carries `extensions.*` keys Git *refuses to
operate without*, selected by `core.repositoryformatversion`, and `core.filemode` and `core.bare` describe the
repository, where a wrong value changes what a diff says. Everything else in `core` is dropped, which makes
`PreservedCoreKeys` an allow-list: a key nobody has thought of yet is dropped by default rather than surviving until
someone remembers to name it. Two things are deliberately **not** preserved — `origin`, because the clone drops it on
purpose and restoring it would undo the standalone clone's isolation, and `extensions.worktreeConfig`, because it
makes Git read a second config file (`.git/config.worktree`) the rewrite does not cover; that file is removed
alongside rather than sanitised, a standalone clone having no linked worktrees to need it.

There is no meaningful TOCTOU window: evidence export runs after the attempt has finished with no agent command in
flight, and workspace preparation runs before any command has started. Both files are deleted rather than overwritten
in place, because a command can replace one with a symbolic link and an ordinary write would then follow it out of
the workspace.

##### The whitespace policy is derived from the index

Every .NET validation profile begins with `git diff --check HEAD -- .`. Git's default rules count the CR of a CRLF
pair as trailing whitespace, so a repository that legitimately stores CRLF blobs — the norm for a Windows-native
project — reports `trailing whitespace` on every changed line and exits 2, failing the gate at command one on a
perfectly correct change. Reproduced on a current Git release: a three-line CRLF file plus one added line exits 2
under the default rules and 0 under `cr-at-eol`.

Setting `core.whitespace=cr-at-eol` is not the answer, because it is repository-wide and the answer is not. On an LF
repository where a change introduces one CRLF line — the genuine defect the check exists to catch — `cr-at-eol`
silences it too; deleting the whitespace command and setting the option globally are the same mistake in two
spellings. Whole-repository classification is not enough either, because mixed repositories are the common case
rather than the exotic one: this engine's own repository stores 4243 files as LF and exactly one as CRLF.

`DevelopmentWorkspaceWhitespacePolicy` therefore grants Git's per-path `whitespace` attribute to the paths whose
**index** content is CRLF and to nothing else; every other path keeps the full default rule set, CR included. The
index is the right signal because `core.autocrlf=true`, which Git for Windows' system config commonly sets, leaves
the worktree CRLF while the blob is LF, and `diff --check` compares against the blob — sampling the worktree would
hand `cr-at-eol` to an ordinary LF repository on every Windows box.

It is written to `.git/info/attributes` rather than into the profile's argument vector, because the profile is
snapshotted and re-derived from the code-owned catalog, so a per-repository argument would need a catalog version
bump that invalidates every stored profile. `$GIT_DIR/info/attributes` outranks an in-tree `.gitattributes`, so a
hostile repository can neither revoke the policy nor grant itself one: the file is engine-written and rewritten from
the index on every preparation. Above `MaxExplicitPaths` (2048) CRLF paths it names `*` instead of every path,
because Git walks the pattern list for every lookup, so an exhaustive list on a large all-CRLF repository is
quadratic work on every diff — and a repository with that many CRLF blobs is a CRLF repository.

**The repository's own config is not the source, and could not be.** The managed workspace is a standalone clone, and
`git clone` copies no `core.*` from the source repository — verified: a source carrying `core.whitespace=cr-at-eol`
and `core.autocrlf=input` produces a clone whose config carries neither. The minimal-config allow-list is not what
removes them; they were never there. The workspace is also deliberately more deterministic than the operator's
checkout, because commands run with `HOME` pointed at a per-task runtime directory and see no user `~/.gitconfig`.
Do not "fix" the difference by adding `whitespace` or `autocrlf` to `PreservedCoreKeys`: there is nothing to
preserve, and a key an agent-writable file supplies is exactly what that allow-list exists to refuse.

#### The per-task CLI environment, and the PATH it must not leak into

Every sandboxed Development command runs with `HOME`, `TMPDIR`, `NUGET_PACKAGES` and `DOTNET_CLI_HOME` pointed at
per-task runtime directories, expressed in the sandbox's own path namespace. Two of the variables beside them are
there to stop that per-task state escaping the task, and both were paid for.

**`MSBUILDDISABLENODEREUSE=1`.** MSBuild's reusable worker nodes (`MSBuild.dll /nodemode:1`) survive the `dotnet`
process that started them and keep the per-task `NUGET_PACKAGES` path in their environment. On the process provider
they are ordinary host processes, so a *later* restore anywhere on the same host can attach to one and write the
by-then-deleted packages path into `obj/*.dgspec.json`. Measured twice, as `NU5037` and then as `CS0006`, both
naming a temporary directory no command had asked for. One task per node, no reuse.

**`DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0`.** Without it, the .NET CLI's first-run experience appends
`$DOTNET_CLI_HOME/.dotnet/tools` to the **persisted** per-user PATH — on Windows, the `HKCU\Environment` registry
value. `DOTNET_CLI_HOME` is a fresh per-task directory, so every task leaks one more entry that outlives the
directory it names. Measured on Windows 11: **153 dead entries, 28,387 characters**, and the count still climbing
within a single session. The damage is not untidiness — `cmd.exe` silently receives an **empty** `%PATH%` once the
variable grows past its limit, so every bare-name command run through it fails. That broke three sandbox tests
whose fixture is `cmd /c ping -n 31`: `ping` could not resolve, the command exited instantly, and
cancel/timeout/tree-kill had nothing left to kill. Stripping the dead entries took PATH to 847 characters and the
same tests went green with no code change.

> **`DOTNET_SKIP_FIRST_TIME_EXPERIENCE` is not an alternative** — it is a no-op in .NET 10. This is the obvious fix
> a reader will reach for; it does nothing.

#### The test-write policy's protected-path set

`DevelopmentCommandProfileCatalog.DefaultProtectedPaths` names the paths an agent may create but may not modify,
delete or rename once they existed at the attempt's base commit. The set is grounded in the measured layout of this
repository, `XE-Framework` and the synthetic fixture rather than assumed, and each part of it answers for itself.

- **The filename rules carry most of the weight.** `*Tests.cs` matches 543 files across both real repositories with
  zero false positives.
- **The directory rules exist only to close the shared-helper hole.** Without them an agent can gut `AssertEx.cs` so
  that every assertion silently passes — a shorter path to green than deleting a test, and exactly the move the
  policy exists to stop.
- **The directory rules are scoped to `*.cs` on purpose.** Freezing whole test directories would freeze the test
  `.csproj` files too, so an agent could never add a package reference to an existing test project, which blocks the
  "implement a feature and its tests" case the policy explicitly permits.
- **`**/*.Tests/**` is never used alone**, because alone it is a trap: no directory in `XE-Framework` ends in
  `.Tests` (its projects are `XeFramework.Tests.UnitTests` and siblings), and it misses this repository's own
  `XE-Local-AI-Engine.Tests.E2ETests`. On its own it protects zero tests here.
- **Two patterns are deliberately left out, each for a measured reason.** `*Spec.cs` has three false positives
  across the two repositories (`OrchestrationSpec.cs`, `LlamaServerLaunchSpec.cs`, `ImageServerLaunchSpec.cs`) and
  zero true positives; `*Test.cs` singular matches nothing in either repository.

The set is code-owned and versioned with `DevelopmentCommandProfileCatalog.CurrentVersion`, so widening or narrowing
it is a source change plus a version bump, never configuration.

#### The workspace surveys are managed code

`WorkspaceFileScanner` implements `list_files` and `search_text` in managed code rather than shelling out to `find` and
`grep`, and that decision preserves every security property the shell-out had — strengthening one.

- **Path confinement is unchanged.** It still happens in the caller's own path guard, before anything reaches the
  scanner.
- **Symbolic links are never followed and never emitted**, exactly as `find -P … -type f` and `grep -r` behaved, and
  the scanner additionally refuses a scan root that is itself reached through a link. That refusal is the strengthening.
- **One suppression predicate, applied twice.** The caller supplies a single `isSuppressed` delegate
  (`DevelopmentWorkspaceTools.IsSuppressedFromOutput` in production) and the scanner applies it at *both* the prune step
  and the emit step, so the generator and the filter cannot drift apart.
- **That predicate must gate reads on `ISensitiveFileExclusionService.IsSecret`**, never on the broader `IsExcluded`
  copy filter. Conflating them refuses `obj/`, which an agent legitimately reads after a failed build, while protecting
  nothing — build output is not a credential.

### Backend selection: a feature declares what it needs, and never names a backend

[ADR 0007](../adr/0007-sandbox-execution-substrate-and-backend-selection.md) (Accepted 2026-08-25) changes **who
decides which of the boundaries above runs**, and changes none of them. Each workload states its requirements as an
engine-owned constant in `SandboxWorkloads` — a **toolchain source** (the host's, or a named engine-approved image),
an **isolation floor**, a **network floor**, a **persistence** need and a disk ceiling — and `SandboxProviderSelector`
resolves the **minimal-satisfying** registered backend: among those that honour every declared axis, the one with the
smallest additional privilege footprint wins (`fake` < `process` < `docker`, the last because a live daemon whose
socket is root-equivalent on Linux is additional privilege even where the container is the stronger boundary). When
none can honour the declaration the call throws `SandboxCapabilityNotSupportedException` naming the unmet axis. There
is no fallback and no downgrade.

The **isolation floor is a property, not a mechanism**: at its `Filesystem` value it asks that the host filesystem be
absent from the sandbox's view, and it is satisfied by any backend advertising `SupportsHostFilesystemBoundary` — the
bubblewrap chain (probe-exercised) and a hardened container (read-only rootfs, engine-generated mounts, no host
namespaces, all read back and fail-closed on mismatch) both qualify. That is deliberately **not** the same flag as
`SupportsFilesystemIsolation`, which means the narrower "serves `SandboxIsolationMode.Filesystem`" — a specific
create-request contract of named read-only host trees, a synthetic `/etc` and a jail-backed `/tmp`. The container
provider has the property and implements none of that contract, and still refuses the mode on a create request; a
single flag asked to mean both would either lie to `run_python` or deny a container an isolation level it genuinely
has. The isolation panel on the Development page reports the **property**, and reports it as SERVED — the role's declared
floor intersected with what the backend advertises. So a container-served role reads as having the boundary only when
its declaration asks for one, and Development Mode's does not: on this repository's shipped declarations `run_python`
is the single role whose Filesystem column can read Yes. A panel that read the capability alone claimed a boundary for
Development Mode on any Linux host with a working bubblewrap chain, which was false in the unsafe direction;
`DevelopmentContractMapper.ToIsolationSummary` owns the intersection rule and the two different "no boundary" reasons
(not requested by the role, versus requested and unavailable with the measured probe reason). The Resource-limits
column follows the same rule for the same reason: `SandboxCreateRequest.ResourceLimits` is a preference a backend may
drop, `SandboxLifecycleRegistry.BuildLaunchPolicy` applies a scope ceiling only when the request carries one, and
`SandboxRequirements.Ceilings` is where each workload states WHICH profile it asks for —
`SandboxCeilingProfile.ComputeTool` for `run_python`'s tight script-sized numbers, `HostToolchain` for every role that
runs a real compiler. `SandboxSubstrateSelectionArchitectureTests` asserts every declaration names a profile, that
exactly one is on the compute profile, and that `SandboxResourceCeilings.Resolve` hands each declaration exactly that
profile's numbers; each create site's own test asserts its request agrees with its constant.

Two consequences a security reader should hold on to.

- **The strongest guarantee in the previous design is weakened in kind, deliberately.** "Docker cannot be wired into
  AgentHome" used to be an absent `implements` clause — a compile error. It is now three mechanisms: AgentHome
  declares a host toolchain, which no container backend supplies; the isolation floor has no default, so a new
  consumer cannot inherit the weakest posture by saying nothing; and
  `SandboxSubstrateSelectionArchitectureTests` enumerates every declaration and asserts the exact backend set allowed
  to serve it. A compile error cannot be skipped and a test can. The mitigation is that the test is an enumeration
  over engine-owned constants rather than a behavioural test, so it fails deterministically and offline — but it is a
  real reduction. (In this tree `DockerSandboxRuntimeProvider` also still implements only the Development role, so the
  old compile error stands *behind* the new checks rather than in place of them.)
- **Diagnosis moved.** A feature can no longer be read off its own file. `SandboxProviderSelector` logs every
  resolution at **Information** — declaration, candidates considered, winner, and rejected candidates with reasons —
  and that log line is now the answer to "which boundary is this node actually running".

> **Operator keys changed meaning.** `AgentHome:Sandbox:Provider` and `Development:Sandbox:Provider` used to *name*
> the provider. They now **constrain the candidate set**, and the workload's declaration decides whether the named
> backend may serve it at all. On every node that ships today the outcome is identical. Where it differs it is loud,
> never quiet: a key naming a backend that cannot honour the declaration **fails closed at startup with the unmet axis
> named**, because silently reinterpreting a set key is how a hardened node becomes an unhardened one. One extra rule
> applies to Development Mode only — naming `docker` is *also* read as declaring an image-backed toolchain need (which
> is what that key always meant), and setting `Development:ContainerSandbox:Image` declares the same need without the
> key. The unset-Development-key fallback to the AgentHome key still applies, but only while no image toolchain is
> declared.

### Chat attachments are staged *into* the jail, not read from the host

When a chat agent-mode turn needs to read a conversation's uploaded files, `IConversationSandboxStager` (`Services/AgentHome/IConversationSandboxStager.cs`) re-stages the **existing** node sandbox so it holds **only** that conversation's extracted attachments under the workspace `attachments/` alias (the sandbox is recreated first, so it never carries another conversation's residue). The agent then reaches them with the same jailed `list_files`/`read_file`/`search_text` tools — meaning every read still passes through the §7 path-confinement, symlink-escape, and `O_NOFOLLOW`/byte-cap guards above; staging adds no host-filesystem read path that bypasses the jail. Attachments may contain secrets or confidential content: their stored bytes are encrypted at rest by `UploadedFileBlobProtector` (§5), but extracted content exists as plaintext while decrypted and staged for use, and no secret scan occurs before staging. `MemoryProposalSecretScanner` applies only to a later memory proposal before that proposal is persisted. Don't add a staging path that writes outside the workspace root or skips the recreate-before-stage step.

### Landing a patch on the host is the operator's act, never the model's

The one AgentHome path that writes **outside** the jail is `INodePatchApplyService`, which applies a run's exported
`changes.patch` onto the real selected folders. Its guards, and where each is proved:

| Guard | What it stops | Where |
|---|---|---|
| Operator-only reach | A model asking for host mutation. The endpoint pair is `NodeOperator`-gated on a loopback-bound process, and no `[McpServerTool]` or `IClientLocalToolHandler` names the service | `Endpoints/AgentHome/V1/*`; pinned by `HostPatchApplyReachArchitectureTests` |
| Preview→apply hash binding | A `changes.patch` replaced between the review and the approval. The apply must echo the preview's SHA-256, and the bytes are read ONCE — the bytes hashed are the bytes validated and handed to git | `NodePatchApplyService.BuildPlanAsync` |
| Body-path within-root + symlink/reparse walk | A target that escapes the selected folder, with or without git's help. Authoritative and independent of git, derived from the patch BODY lines rather than the `diff --git` header | `NodePatchApplyService.BuildAliasPlanAsync` |
| `..` segment refusal | Traversal in any path position, including a mode-only block's header path | `NodePatchApplyService.ContainsTraversal` |
| `.git` segment refusal | A write into the target checkout's own repository — hooks, and the config that DEFINES the `filter`/`textconv` programs git runs. Segment equality and case-insensitive, so `.gitattributes` and `.gitignore` stay ordinary files | `NodePatchApplyService.ContainsGitDirectory` |
| Hardened git environment | A `filter.<driver>.clean` or `diff.<name>.textconv` defined in the host's global or system configuration and selected by the target's in-tree `.gitattributes`. The `-c` pins cannot close that class (driver names are arbitrary); removing the files from git's search does | `HostGitRunner` carrying `AgentHomeGitHardening.Environment` |
| Binary reject-by-default, size bound, command timeout | An unreviewable or unbounded apply | `AgentHomeOptions.AllowBinaryPatchApply`, `GetAgentHomeMaxPatchBytesAsync`, `PatchApplyTimeoutSeconds` |
| Quoted-path decode, gitlink and symlink refusal | A block this parser does not understand being guessed at. A C-quoted path is decoded by `GitQuotedPath` — no more permissively than git's own `unquote_c_style`, because git answers a literal it cannot read by taking the raw text as the name — BEFORE the traversal, git-directory, alias and containment guards run, so a `..` or `.git` behind an octal escape faces exactly the checks its plain spelling would; a decoded name holding a control or format character, or a character this host cannot put in a file name, is refused by name. A `160000` block is a submodule pointer `git apply` answers with an empty directory; a `120000` block carries a link TARGET as its content, so applying it would create a real link on the operator's folder pointing at an absolute system path or out through `../` — the within-root guard validates the link's own path, never where it points. All three are refused by name instead of failing closed under some other rule's message. Every mode arm is matched as a whole LINE (creation, deletion, either direction of a mode pair, and the same-mode `index … <mode>` header), so a file whose CONTENT is the text of a mode line still applies | `NodePatchApplyService.ParseBlock`, `DeclaresMode` |
| The child dies with the request | A cancelled apply's `git` outliving the gate its caller released on the way out. Any cancellation kills the process tree, and the call does not return until the child is confirmed gone | `HostGitRunner.RunAsync` |
| Sub-patch written user-only, deleted on every path | A copy of the operator's source sitting world-readable in the system temp directory. The mode rides on the create, so there is no window at the process umask | `NodePatchApplyService.WriteSubPatchAsync` |
| Node-wide apply serialization | Two applies interleaving writes into a tree the other's `--check` already cleared. `git apply` is not transactional across files | the static gate in `NodePatchApplyService.ApplyApprovedAsync` |
| Host-path redaction | A rejection string or log line naming a host directory. Every path that crosses the wire is `<alias>/<relative>` | `NodePatchApplyService.Redact` (§2.2), the run log, and the endpoint DTOs |
| Named refusals stay folder-relative | A refusal echoing a model-authored path back verbatim. A refused entry is named only through the split the `Files` list already passes (alias + relative, `<alias>/<relative>`); a C-quoted path is named through the same decoder the guards use and stays unnamed only when that decoder refuses it, and a control character in a decoded name is refused and shown as a `\u{XXXX}` escape rather than carried raw into the dialog, the 409's error name or `patch_apply_rejected` | `NodePatchApplyService.TryDescribeTarget`, `Describe`, `SafeDisplayPath` |
| Dirty-target read cannot write, cannot refuse | A preview mutating the operator's repository, or a local edit silently blocking an apply. `status` refreshes and rewrites the index unless told otherwise, so it runs under `--no-optional-locks`, pathspec-scoped to the patch's own targets, and its result is advisory: outside `CanApply`, outside the hash binding, and never run at apply time. A failure reports "unknown" rather than "clean" | `NodePatchApplyService.ReadDirtyTargetsAsync` (`NodePatchApplyService.DirtyTargets.cs`) |

---

## 8. Compile-time guardrails (`BannedSymbols.txt`)

The repo enforces a "banned API" wall via `Microsoft.CodeAnalysis.BannedApiAnalyzers` (RS0030), promoted to a build *error* by repo-wide `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`. `BannedSymbols.txt` (repo root) applies to production projects only (test/tooling exempt via `IsTestOrToolingProject` in `Directory.Build.props`).

> **This wall only fires in Release.** Since 2026-07-31 `Directory.Build.targets` sets `RunAnalyzersDuringBuild=false` (property renamed 2026-09-16 so IDE live analysis stays on) for local **Debug** builds (`Configuration == Debug` and neither `CI` nor `XE_FULL_ANALYSIS` set), which maps to csc `-skipanalyzers` — so RS0030, the Sonar rules, Meziantou and the `IDExxxx` style rules do not run at all. A green local `dotnet build` is **not** evidence that this section's rules passed. Finish with `dotnet build XE-Local-AI-Engine.slnx --configuration Release`, or set `XE_FULL_ANALYSIS=1` to force the full pass in Debug. `TreatWarningsAsErrors` stays on either way, so genuine compiler warnings still fail a Debug build.

Current bans:

| Banned | Use instead |
|---|---|
| `DateTime.Now` / `DateTime.UtcNow` / `DateTimeOffset.Now` / `DateTimeOffset.UtcNow` | inject `TimeProvider`, call `GetUtcNow()` / `GetLocalNow()` |
| `Thread.Sleep(...)` | `await Task.Delay(..., cancellationToken)` |
| `GC.Collect(...)` | let the runtime manage GC |

The file documents its own scope: it is the "safe set" — APIs with zero current production usage — so the wall blocks *new* occurrences while keeping the build green. (`DateTimeOffset.UtcNow` joined the set on 2026-09-16 after every production reader was migrated to an injected `TimeProvider`; sync-over-async `.Result`/`.Wait()`/`.GetAwaiter().GetResult()` remains outside it because production call sites still use it.) Separately, a literal `TODO`/`FIXME`/`HACK`/`XXX` in a comment fails the build (Sonar S1135 = error); describe the present limitation or rationale directly without task markers. Like every rule on this page, that one is **Release-only** per the note above: a bare `TODO` compiles cleanly in a local Debug build and fails the packaging script later.

**Maintainer rule:** don't suppress RS0030 to land a banned call; fix the call site.

---

## Invariant checklist (for reviewers)

- [ ] No code path opens an outbound control-plane connection; every egress traces to an operator-enabled feature.
- [ ] No secret (operator secret, endpoint token, cloud cred, HMAC/JWT key, HF token) is returned to the browser or logged; new credential-bearing fields pass a redactor.
- [ ] New local-admin routes live under `/api/local/v1`, keep the Host/Origin gate fail-closed, and apply an authorization policy.
- [ ] Production error responses carry no internal detail (message/stack/ids).
- [ ] At-rest crypto routes through `AesGcmNodeAeadCipher`; AAD context components are preserved.
- [ ] Analysis/eval/extraction AI runs node-local only.
- [ ] Tool execution stays inside the jail with symlink + O_NOFOLLOW + byte-cap guards.
- [ ] Nothing new reaches `INodePatchApplyService` except an operator-gated surface: host patch apply is the one AgentHome path that writes outside the jail, and a model must not be able to ask for it.
- [ ] A new outbound MCP capability does not weaken the default trust tier: `Sandboxed` stays the default for stdio, `PrivilegedHost` stays a per-server operator grant, and a host that cannot serve the boundary still refuses rather than degrading.
- [ ] A new External Apps capability keeps the container policy fail-closed: `cap_drop ALL` plus a `capAdd` inside Docker's default set, `no-new-privileges` and seccomp, loopback-only publishing, a digest-pinned image, the declared mount set only, and every one of them re-verified against the daemon's read-back rather than assumed from the create call.
- [ ] No banned API (RS0030) and no literal TODO/FIXME comment.

---

## Related pages

- [Architecture Overview](01-architecture-overview.md)
- [Local Runtime & Providers](03-local-runtime-and-providers.md)
- [Agent Mode](04-agent-mode.md) — node-local analysis/eval rule, sandboxed tool execution
- [Data & Persistence](08-data-and-persistence.md) — at-rest encryption schema & interceptor
- [API & Hubs](09-api-and-hubs.md) — `/api/local/v1` surface, auth policies, local hubs
- [Hosting & Deployment](11-hosting-and-deployment.md) — loopback local modes and opt-in user autostart
- [External Apps](23-external-apps.md) — §7.3 in full: the container policy, the storage helper, and what V1 does not enforce
- [Testing & Validation](13-testing-and-validation.md) — persistence-encryption & loopback tests
- [Technical/Security Architecture Dossier](../audits/technical-security-architecture/README.md) — baseline auditor narrative, evidence states, and residual-risk limitations
