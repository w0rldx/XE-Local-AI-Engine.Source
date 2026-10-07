# Security & Privacy Model

> Reviewed: 2026-10-07 · Code-grounded.

**What this page covers.** The node's cross-cutting security and privacy controls and the invariants contributors must keep: no control-plane egress, no secret returned to the browser or deliberately logged, a loopback-only authenticated admin API, selected fields encrypted at rest, privacy-sensitive AI runs kept node-local, and tool execution inside a process jail. That jail is supervised execution, not an OS isolation boundary. This page is code-and-test evidence, not a compliance claim.

**Read this if you are** touching egress, secrets, authentication, the Host/Origin checks or the sandbox, or reviewing a change for security impact. **Skip to** [Invariant checklist](#invariant-checklist-for-reviewers) for the review list; **reference detail** is in [Admin API surface](reference/12-admin-api-surface.md) (§3 mechanisms: the Host/Origin gate, sessions and authorization, the MCP and model-proxy keys, the container bridge, Codex OAuth) and [Sandbox modes and options](reference/12-sandbox-modes-and-options.md) (§7: the isolated launch mode, MCP trust tiers, the External Apps container policy, the seccomp profile, the Development Mode controls, backend selection and the host patch-apply guards); **related pages:** [08](08-data-and-persistence.md), [09](09-api-and-hubs.md), [04](04-agent-mode.md).

## Contents

- [1. Egress invariant: the node has no control-plane channel](#1-egress-invariant-the-node-has-no-control-plane-channel)
- [2. Secret-handling invariant — not returned to the browser or deliberately logged](#2-secret-handling-invariant--not-returned-to-the-browser-or-deliberately-logged)
- [3. Local admin API: loopback-only, Host/Origin-strict, authenticated, fail-closed](#3-local-admin-api-loopback-only-hostorigin-strict-authenticated-fail-closed)
- [4. Exception handling: no internal detail leakage](#4-exception-handling-no-internal-detail-leakage)
- [5. Encryption at rest](#5-encryption-at-rest)
- [6. Privacy-sensitive AI runs node-local only](#6-privacy-sensitive-ai-runs-node-local-only)
- [7. Sandbox / process-jail for tool execution](#7-sandbox--process-jail-for-tool-execution)
- [8. Compile-time guardrails (BannedSymbols.txt)](#8-compile-time-guardrails-bannedsymbolstxt)
- [Invariant checklist (for reviewers)](#invariant-checklist-for-reviewers)
- [Related pages](#related-pages)

This page documents the cross-cutting security and privacy controls implemented in the XE Local AI
Engine node and the invariants contributors are expected to preserve. It is code-and-test evidence
for the stated baseline, not proof of operating effectiveness, deployment configuration, compliance,
certification, or formal risk acceptance. The supported design keeps secret-bearing values behind node-local stores and redaction seams,
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
| Web search and page fetches | `Services/WebAccess` | the operator turns on **Web access** in Node settings (off by default) |

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

> **Development secret custody.** `secret: true` marks the Aspire parameter as sensitive for display and handling; it never made the value confidential. What did make it non-confidential was a shared development-only default committed to `XE-Local-AI-Engine.AppHost/appsettings.Development.json` — anyone with the source could derive keys for data created under it. That default is **gone**, and it is still in git history, so it must be treated as burned: any dev data written under it is public. `scripts/dev-aspire-common.sh` now mints a per-checkout, owner-only, `.gitignore`d `XE-Local-AI-Engine.AppHost/.data/node.key` (base64 32 bytes) on first use and `scripts/dev-start.sh` passes it to Aspire as `Parameters__node-sqlite-key`, so each checkout has its own secret and nothing sensitive is tracked. The secret is passed through process environments only, never a command line. Packaged local modes are different again: `DesktopBootstrap` persists a per-installation `node.key`, which is the passphrase-wrapped vault described next.
>
> **Rotating the secret destroys data.** The secret is the root of the SQLite column key, the JWT signing key and the non-Windows Data Protection KEK. A checkout that already holds dev data written under a different secret fails on the first protected read with `AuthenticationTagMismatchException` — `dev_ensure_node_operator_secret` warns and names the directories to delete when it mints a key next to pre-existing data.

**Packaged local modes: the `node.key` vault ([ADR 0018](../adr/0018-local-vault-passphrase-wrapped-node-key.md), Accepted).** When none of the three sources above supplies a secret, `DesktopBootstrap` takes custody of a persisted `node.key` in the data directory. That file is a v2 JSON vault (`{"magic":"xe-vault","v":2,…}`, codec `VaultFileCodec` in `Client.Application/Services/Vault`) and **never contains the raw secret**: the secret is a random master key wrapped twice with AES-256-GCM, once under a KEK from PBKDF2-SHA512 over the admin password (iteration count and salt stored in the file) and once under an HKDF-SHA256 KEK from a one-time recovery code (25 CSPRNG bytes shown as 8 groups of 5 base32 characters, never stored). The three derivations below are unchanged and still root in the master key's bytes, so migrating a legacy key re-encrypts nothing.

- **States** (`auth/status` field `vault`): `pending` (no vault file yet, or a legacy raw/DPAPI key: the host runs unlocked and the SPA forces first-run setup or the "confirm your password" step that wraps the legacy key), `locked` (v2 file, secret not unwrapped) and `unlocked` (v2 file unwrapped, or an operator-supplied secret, which leaves the vault untouched).
- **Locked means the whole host waits.** A v2 file starts a pre-host (`Hosting/Vault/VaultUnlockHost`) on the real origin that serves the SPA, `/health/ready`, `auth/status` and the two anonymous loopback-only unlock routes; every other `/api/local/v1` route answers 503. The scheduler, inbound MCP, integration API, retention and recovery services do not exist until the real host is built with the unwrapped secret as an in-memory configuration value. Unlock attempts share one fixed-window limiter (5 per 5 minutes) and a failure never answers in under 250 ms.
- **One password, two stores.** The vault passphrase is the admin login password; setup, change-password and reset update the Identity hash and the vault wrap together and restore the file if Identity refuses. `--reset-admin-password` requires the recovery code when a v2 file exists.
- **The Windows DPAPI layer on `node.key` is gone** for v2 (a legacy DPAPI blob is unwrapped once, for the migration). On Linux the file is still created `0600`, but the wrap, not the mode, is the protection. The Windows Data Protection key ring keeps DPAPI.
- **Out of scope:** operator-supplied secrets (unwrapped by design), and a same-user process reading the unwrapped secret from memory while the node is unlocked. Losing both the password and the recovery code loses the data.

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
| `AccessTokenQueryRedactor` | strips `access_token=` from request query strings before Serilog logs them | `Services/Auth/AccessTokenQueryRedactor.cs`, wired by the `UseSerilogRequestLogging` request-path projection in `Program.Logging.cs` (`ConfigureRequestLogging`) |
| `MemoryProposalSecretScanner` | rejects/redacts secrets in agent-memory proposals before persistence (PEM keys, GitHub/AWS/Azure/Slack tokens, JWTs, high-entropy bearers; ReDoS-guarded with a 2s regex timeout) | `Services/AgentHome/Implementation/MemoryProposalSecretScanner.cs` |
| `McpServerConnectionManager.SafeMessage` | clamps MCP connection failures, sandbox refusals included, to one fixed message per failure reason so a command path/URL/protected host path/secret never reaches the UI; the exception is logged at Warning. Two reasons show text written for the operator instead: a Sandboxed command missing from the jail `PATH` (`ServerNotFound`, the message names the jail `PATH`) and a server that exited during or after startup (`ServerStartupFailed`/`ServerExited`, with its `stderr` tail). That text is scrubbed of the registration's own environment, header and argument values of 8 or more characters (`SecretValueRedactor`), and the sandbox already redacts environment values as it captures the tail (`SandboxStderrTail`); host paths a server prints are **not** stripped | `McpServerConnectionManager.SafeMessage` / `DescribeFailure` in `Services/Mcp/Implementation/McpServerConnectionManager.cs` |
| `InvocationFailureClassifier.RedactAgentRuntimeMessage` | sanitizes agent runtime failure messages before surfacing | `InvocationFailureClassifier.RedactAgentRuntimeMessage` (private) in `Services/Invocation/Implementation/InvocationFailureClassifier.cs` |
| `NodePatchApplyService.Redact` | redacts patch-apply output (AgentHome) | `NodePatchApplyService.Redact` in `Services/AgentHome/Implementation/NodePatchApplyService.cs` |

The request-logging enricher is the canonical example. It replaces the raw query with a redacted one and sanitizes the method and path before anything is written:

```csharp
// Program.Logging.cs, ConfigureRequestLogging (EnrichDiagnosticContext)
var redactedQuery = AccessTokenQueryRedactor.Redact(httpContext.Request.QueryString.Value);
var path = RequestLogSanitizer.Sanitize(httpContext.Request.Path.Value);
diagnosticContext.Set("RequestPathWithRedactedQuery", $"{path}{redactedQuery}");
diagnosticContext.Set("QueryString", redactedQuery);
```

The marker used across redactors is the literal `[REDACTED]` (and `[REDACTED:…]`-style markers in the secret scanner). The scanner's "bare high-entropy" regex is deliberately written so the `[`/`]` of an existing marker stays outside the match — a second pass never re-redacts an already-redacted span (`MemoryProposalSecretScanner.cs`).

**Maintainer rule:** any new field that can carry a credential, host path, command, or URL toward the browser, logs, or a saved transcript must pass through (or extend) a redactor. When in doubt, clamp to a generic reason like `McpServerConnectionManager.SafeMessage` does.

### 2.3 Secret files and secret columns: one protector per purpose, 0600 at create, quarantine on failure

Four stores hold credential material outside the chat columns — `CloudCredentialStore`, `CodexTokenStore`,
`EntraTokenCacheStore` / `EntraAuthCodeAccountStore`, and `ExternalProviderStore` — and they share one posture.

- **One protector per purpose.** Each store derives its own Data Protection purpose, so a blob written for one store can
  never be decrypted as another's. Don't collapse two stores onto a shared purpose.
- **A secret file is created at 0600, in the same syscall that creates it, and replaced atomically.** `File.WriteAllBytesAsync`
  creates at the process umask — 0644 on a default Linux or macOS box — and narrowing it afterwards leaves a window in
  which another local user can read the file. `SecureFilePermissions.WriteAllBytesAtomicAsync` writes a temp sibling
  created with `FileStreamOptions.UnixCreateMode` 0600, applies `SecureFilePermissions.Apply` (the Windows ACL) to it,
  then renames it over the target, so a torn or cancelled write leaves the previous credential intact instead of a blob
  that no longer decrypts (which the store would quarantine as *missing*). `CloudCredentialStore`, `CodexTokenStore`,
  `EntraTokenCacheStore`, `EntraAuthCodeAccountStore` and `HfTokenStore` write through it; `ExternalProviderStore`
  still creates in place with `UnixCreateMode` and narrows afterwards. This is the same discipline `node.key` follows (§2.1).
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

`LocalApiSecurityMiddleware` answers **403** before routing to any `/api/local/v1` request whose socket peer is non-loopback or whose `Host`/`Origin` is not loopback, the anonymous auth routes included, and its Origin check is fail-closed. See [the reference page](reference/12-admin-api-surface.md#31-loopback-peer--host--origin-middleware-localapisecuritymiddleware) for the check, the allowed hosts, the null-peer rule and the middleware order.

> **Reverse proxies / headless deployment are unsupported.** The peer check reads the socket peer, and no forwarded-headers middleware is registered, so `X-Forwarded-For` is never honoured. A reverse proxy on the **same host** would appear as a loopback peer on every forwarded request and defeat the peer gate — this is by design: the app is single-user, same-machine only. Putting `/api/local/v1` behind a proxy or exposing it beyond the local machine is out of scope and not a supported configuration.

### 3.1a The inbound MCP endpoint sits inside the gate — deliberately

`MapMcp` mounts this node's own MCP server at `/api/local/v1/mcp/server` (`Program.cs`, beside the
`MapHub` calls). The path is not cosmetic: `IsLocalApiRequest` matches on the `/api/local/v1` prefix
**alone**, so an MCP endpoint mounted at a bare `/mcp` would be reachable without any of §3.1's peer,
Host or Origin checks, leaving the bearer key as the only control. Keep it inside the prefix.

See [the reference page](reference/12-admin-api-surface.md#31a-the-inbound-mcp-endpoint-sits-inside-the-gate--deliberately) for the WSL2 topology measurement.

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

Authorization is deny-by-default in two layers: a global FastEndpoints configurator applies `NodeOperator` to every endpoint that did not opt out with `AllowAnonymous()`, the four pre-authentication `auth/*` routes are the entire anonymous set, and `AuthorizationOptions.FallbackPolicy` requires a JWT on every other routed surface. The MCP key is a 256-bit secret stored as a one-way SHA-256 digest, returned once, and a node with no key generated authenticates nobody. See [the reference page](reference/12-admin-api-surface.md#32-authentication--authorization) for browser sessions and the 10-second refresh reuse grace with its residual, the canary test, the MCP key's storage and scopes, and agentic-scope audit.

#### 3.2.1 The inbound model-proxy bearer key

The OpenAI-compatible passthrough (`proxy/v1/{chat/completions,embeddings,models}`) authenticates with one operator-generated `xeprx_` bearer key handled the same way as the MCP key. See [the reference page](reference/12-admin-api-surface.md#321-the-inbound-model-proxy-bearer-key) for its storage, rotation, comparison and rate limit.

**The bearer key is not the only gate, and that is load-bearing.** The passthrough is hand-mapped
*inside* the `/api/local/v1` prefix precisely so `LocalApiSecurityMiddleware`'s loopback-peer + Host +
Origin check has already rejected any non-loopback caller before the handler runs. An external tool
therefore has to be on this host (or reach it through the operator's own tunnel) — mounting these routes
outside the prefix would silently remove that layer and leave the key as the only control. `proxy/key`
itself is an ordinary Operator-gated FastEndpoints family, so key management stays on the browser's
JWT posture. Requests are forwarded verbatim to the resolved `llama-server` child and never route
through the operator's cloud credentials. See [API & Hubs](09-api-and-hubs.md).

Local endpoints are still authenticated and policy-gated; loopback is necessary but not sufficient. `NodeAuthorizationPolicies` (`Services/Auth/NodeAuthorizationPolicies.cs`) defines the `NodeOperator` policy (claim type `role`, `Admin`), and endpoints apply it — e.g. `ListAgentExecutionLogsEndpoint.Configure()` calls `Policies(NodeAuthorizationPolicies.Operator)` (see `ListAgentExecutionLogsEndpoint.Configure()`). JWTs are signed with the separately-derived node JWT key (§2.1). Auth wiring lives in `AddNodeAuthExtensions`. See [API & Hubs](09-api-and-hubs.md) for the full endpoint inventory.

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

The container bridge is the one deliberately non-loopback listener ([ADR 0011](../adr/0011-container-bridge-listener.md)). It serves three routes on one LAN-facing IPv4 address, refuses a peer that is not one of this computer's own addresses, requires the per-instance bearer token on every route, and is off unless External Apps is on. `llama-server` still binds `127.0.0.1`, and the §3.4 guard still stops any other routable bind. The bridge is not rate-limited, and only a rootless Linux daemon is validated: under a rootful daemon it fails closed. See [the reference page](reference/12-admin-api-surface.md#35-the-container-bridge--the-one-deliberately-non-loopback-listener-containerbridgepipeline) for the full posture and the bridge token's shape.

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

`CodexTokenStore` keeps Codex tokens under a dedicated protector purpose in a separate `.enc` file. `CodexAuthHandler` strips the SDK's dummy key, injects the Codex header contract and refreshes single-flight on a 401. Token values, authorization headers and the dummy key are never logged, and a logged error body is bounded and redacted. The JWT payload is decoded without verifying the signature and used only as advisory metadata. **Do not repurpose these claims for access control without first verifying the signature against OpenAI's JWKS.** See [the reference page](reference/12-admin-api-surface.md#37-codex-oauth-token-storage-refresh-and-redaction) for the header contract, the refresh, the error-body logging rules and the login coordinator.

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

**Which handlers log.** `DefaultExceptionHandler` logs the unhandled case at Error with method, path, trace id, span id, request id, user id and the exception type. The conflict, domain-validation, development-conflict and request-body-too-large handlers write their own line. The benchmark, GGUF download and import, training, container-runtime and selected-folder handlers go through one helper, `ExceptionHandlerLog.Log`: Warning for a 4xx, Error for a 5xx, with the exception attached. The NotFound handlers stay silent on purpose, because a 404 is an expected answer and the request-completion line already records it.

**`traceId` is the W3C trace id everywhere.** Every exception handler that logs uses the W3C trace id (`ProblemDetailsExtensions.ResolveTraceId`, never the connection id): the line's `{TraceId}` equals the problem body's `traceId` and the `[trace:…]` field of the file log. FastEndpoints' own validator 400s do too, because `FastEndpointsProblemWriter.Build` is the host's `ResponseBuilder`. The id a user reads out of an error therefore matches the `[trace:…]` field of the node log line.

### Verbose logging and the silent-catch policy

Turning on verbose logging (`diagnostics/log-level`, Operator policy) widens what the node log records: Debug detail, including for third-party categories without a configured override. Scrubbing is unchanged, so the bundle's `SupportBundleScrubber` still runs over every log entry, but more text means more for the user to skim before attaching. The bundle's `node-info.json` records `verboseLogging`, so a maintainer can see that a log was captured in verbose mode. The switch never outlives the process.

Silent catches follow one policy, kept here because no conventions page owns it: every unfiltered `catch` or `catch (Exception)` carries either a log line or a one-line `// swallowed:` reason. The 2026-10-02 review covered 58 sites, added a log line at 2 and a reason at 5, and found the rest already documented or not a swallow. Narrowing the filter was rejected everywhere, because each candidate was a native callback that must not unwind, a security path that must fail closed, or a `Try*` helper whose callers rely on it never throwing.

### Support bundle redaction

The support bundle (`diagnostics/support-bundle`, see [Hosting & Deployment](11-hosting-and-deployment.md)) is meant to be attached to a public issue, so `SupportBundleScrubber` runs over every log and `processes/*` entry, line by line with 1 s regex timeouts (a line that times out is dropped whole), and over the free-text values of `node-info.json` (warnings, CPU model, settings values) before that file is serialized. It is idempotent. Dense-token masking skips hex-only runs (trace ids, SHAs), compact timestamps (`20260913T134250067Z`), semantic versions with build metadata and anything directly under a `~`/`<data>` prefix, so those stay readable.

| Scrubbed | Becomes |
|---|---|
| The user's home directory and the node data root | `~` and `<data>` |
| Any other absolute path | its leaf name (`AbsolutePathSanitizer`) |
| E-mail addresses | `[redacted-email]` |
| JWTs, `Bearer` values, prefixed keys (`sk-`, GitHub, `hf_`, AWS `AKIA`, Slack `xox*-`, Google `AIza`, Azure `AccountKey=`), the value of a `password=`/`api_key:`-style assignment, and 20+ character runs with a 16+ character segment carrying 4+ digits | `[redacted-token]` |

Not scrubbed, on purpose or because no pattern can tell:

- A bare user or host name that appears outside a path.
- Hex-only secrets. Hex runs are kept so trace ids, GUIDs and hashes stay readable.
- Prompt text, file names and other content a child process prints. The sd-server and whisper-server tails are in the bundle too.
- Model names and log category names.

**Review the zip before attaching it to a public issue.** The scrubber narrows what leaks; it does not certify the file. Nothing is uploaded by the app: the export writes one zip to the user's disk and "Open GitHub issue" only opens a prefilled form.


---

## 5. Encryption at rest

Selected chat/state fields in SQLite are encrypted at the column level. This is not SQLCipher or
whole-database encryption; structural fields and deliberately searchable data such as Knowledge Base
chunk text/FTS remain plaintext. See [Data & Persistence](08-data-and-persistence.md) for the exact
schema and migrations; the security-relevant cryptography is summarized here.

| Component | Role | Location |
|---|---|---|
| `AesGcmNodeAeadCipher` | the *only* `AesGcm` owner: AES-256-GCM, 12-byte nonce, 16-byte tag | `XE-Local-AI-Engine.Client.Persistence/Cryptography/AesGcmNodeAeadCipher.cs` |
| `VaultFileCodec` | the v2 `node.key` vault codec: wraps the master key under a PBKDF2-SHA512 password KEK and an HKDF recovery-code KEK, both through `AesGcmNodeAeadCipher` with distinct AAD per wrap; the only reader and writer of the v2 format | `XE-Local-AI-Engine.Client.Application/Services/Vault/VaultFileCodec.cs` |
| `INodeAeadCipher` | the AEAD seam every at-rest protector and the vault codec delegate to | `.../Cryptography/INodeAeadCipher.cs` |
| `NodePayloadProtector` | at-rest column protector: random nonce per value, AAD binds `conversationId + recordId + columnName + schemaVersion` | `.../Cryptography/NodePayloadProtector.cs` |
| `NodeChatContentProtection` | versioned read-both envelope over `NodePayloadProtector` for the two columns with legacy plaintext rows (message `content` + `metadata_json`): a `0xFE 0x01` header — bytes that can never begin valid UTF-8 — marks ciphertext, so reads tell it apart from legacy plaintext without guessing | `.../Cryptography/NodeChatContentProtection.cs` |
| `NodeEncryptionSaveChangesInterceptor` | encrypts tracked payloads on `SavingChanges`, restores plaintext on the tracked entity after save | `XE-Local-AI-Engine.Client.Persistence/NodeEncryptionSaveChangesInterceptor.cs` |
| `UploadedFileBlobProtector` | at-rest protection for chat **uploaded-file blobs** stored on disk (raw bytes + extracted Markdown); re-uses `AesGcmNodeAeadCipher` + the same `nonce ‖ ciphertext ‖ tag` framing/AAD, binding each blob with a distinct column name (`file_bytes`/`file_md`) | `Client.Application/Services/DocumentIngestion/Implementation/UploadedFileBlobProtector.cs` |

Key properties worth preserving:
- **Single AEAD owner.** `AesGcmNodeAeadCipher` is the sole place `AesGcm` is constructed and the tag size lives — the column protector, the blob protectors and the vault codec all route through it. Don't construct `AesGcm` elsewhere.
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

**Maintainer rule:** when adding any AI step that consumes user conversation/memory content for analysis or evaluation, route it through the node-local provider path. Do not let a cloud provider (Codex OAuth, etc.) become the executor for analysis/eval. Cloud credentials themselves are local-only secrets (§2). The three background model pickers never inherit a non-local default chat model, and each step checks its effective model through `BackgroundModelLocalityGuard` before resolving a provider, skipping the run with a warning when it is not node-local.

### Two recent subsystems have explicit egress boundaries

- **Voice / text-to-speech delegates to Web Speech.** The repository makes no voice-model request, ships no voice inference runtime, and does not post generated audio to the node. Synthesis is provided by the browser/operating-system speech implementation; its installed voices, offline support, and any service network traffic are outside repository control. See [React Client](10-react-client.md).
- **An external connection's connect-time probe refuses a redirect.** `ExternalProviderProbeService` sends its probe with automatic redirects disabled, the same class of control `CustomToolSsrfGuard` applies to a custom-tool fetch: a probe that followed a redirect would carry the operator's key to whatever host the endpoint named. The probe may fall back to the stored key when the request supplies none, so testing an existing connection does not require re-typing the secret; that fallback is exactly why the redirect refusal is load-bearing.
- **External connections are HTTPS by default.** A plain-http base URL on a host that is not loopback is refused at save and at probe unless the operator ticks the connection's `AllowInsecureHttp` opt-in (`ExternalProviderTransportPolicy`); a LAN or VPN segment is exactly where a plaintext Bearer key and the prompts are sniffable, and a hostname's private-ness cannot be trusted without DNS. Connections saved over http before the rule keep working and are badged in the settings list, so an upgrade breaks nothing and the next save makes the choice explicit.
- **One trust authority decides what leaves the node.** Every gate that withholds node-local data or execution from a model (knowledge and workspace tools, web, MCP and custom tools, `run_python`, AgentHome, sub-agent spawn, unattended runs, the cloud-model cue) asks `IModelTrustResolver`, directly or through `ModelCapabilitySnapshot.IsCloud` (the same answer collapsed to a bool, used by the unattended-run gates), which fails closed: a Codex id is cloud even with no session, and an unreadable external registration or a routing-snapshot fault is `Unresolved`, treated as cloud. Routing (which provider serves a send) stays on the cloud factory; see [Local Runtime & Providers](03-local-runtime-and-providers.md) §6 "Trust is not routing".
- **The operator decides per function what a cloud model may do.** The gates a cloud (or `Unresolved`) model meets fall into two classes. *Content-leak* gates decide which node-local content, or which outbound reach carrying it, a remote model gets; the operator may open them. *Remote-execution* gates decide whether a remote model directs execution on this machine; they are hard invariants with no setting: `run_python`, `run_in_agent_home`, `Command` custom tools, Development Mode, training, benchmarks, the adaptive-effort fast-model swap, and the inbound MCP model selection (an installed GGUF only). The **Cloud models** card in **Node Settings → Privacy** holds five switches, all off by default and read on the next turn or run, each opening one group of content-leak gates:
  - `AllowCloudModelAccess` ("Let cloud models read local data"): knowledge-base tools, workspace file tools, attachments, playbook memory, tool history and work-session repointing. `KnowledgeBase:AllowCloudModelAccess` only seeds it.
  - `AllowCloudModelUnattendedRuns` ("Let cloud models run unattended"): scheduled `run-agent` jobs, graph Agent, LLM Call and DecisionModel nodes, and integration triggers. Approval-required tools are still stripped (scheduler, graph) or refused at call time (integrations), and the other four switches still decide which tools such a run is offered. With the switch on, an integration trigger hands its external payload to the cloud model.
  - `AllowCloudModelWebTools` ("Let cloud models use web tools"): `web_search`, `web_fetch` and `HttpFetch` custom tools, all of them only while the Web access setting is on as well; web requests keep their consent flow and `HttpFetch` tools their approval policy.
  - `AllowCloudModelMcpTools` ("Let cloud models use MCP tools"): outbound MCP tools. Before this switch MCP tools reached a cloud model with no locality gate; they are now withheld until it is on.
  - `AllowCloudModelSubAgents` ("Let cloud models delegate to sub-agents"): `spawn_subagent` in the offer and the spawn service's own guard. A child keeps its own model's gates, and the cloud sub-agents per run cap still applies.

  The offer is the only gate for a tool: no tool executor re-checks locality, so opening an offer branch opens that tool end to end.
- **Payloads that arrive from outside are fenced before a model reads them.** An integration's caller-supplied seed, the prior outputs replayed into a caller-managed session, and a `emit_output` payload replayed on a later turn all pass through `UntrustedContentFraming` before they re-enter the model's context. The fence carries a server-secret-derived nonce the caller cannot forge, and the framing is what separates "data an external caller sent" from "an instruction the node authored".
- **Playbook memory follows the knowledge egress gate.** Enabled playbook actions are learned from conversations, so the resolvers fold them into an agent's prompt only for a node-local effective model (per participant in an orchestration) unless the `AllowCloudModelAccess` node setting is on (**Node Settings → Privacy**, read per turn; `KnowledgeBase:AllowCloudModelAccess` only seeds it); otherwise the base instructions go out alone.
- **Inference profiling / machine key is local-only, per-machine.** The per-machine launch-tuning profiles ([Local Runtime & Providers](03-local-runtime-and-providers.md)) are keyed by a `MachineKeyProvider` identifier that is a **local-only random id** — never hardware-derived, and `IMachineKeyProvider` documents it must **NEVER** be emitted in telemetry, aggregates, or logs. The profiles themselves hold only structural launch args (no secrets) and never leave the node. Keep the machine key off every outbound DTO/aggregate.

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
tool's fixed environment, enforces a 1-second-to-ceiling timeout (30-second default; the ceiling is the
`CustomToolMaxTimeoutSeconds` node setting, default 300 s, bounds 30–3600 s), tree-kills on cancellation/timeout, and
caps each captured stream at 64 KiB. `HostExecutableGuard.Validate()` runs both while authoring and immediately before
launch: absolute path only, no shell/interpreter/script, existing regular file, symlink/reparse rejection. The Linux
regular-file check uses `statx` with `AT_SYMLINK_NOFOLLOW`, because a raw `(FileOptions)` cast for the flag throws. That
check and the later `Process.Start()` re-resolve the same path string as two separate syscalls, so a
small check-to-`execve` TOCTOU window remains — eliminating it would need `open(O_NOFOLLOW|O_PATH)+fstat+fexecve`, so
validation and execution share one open file description — and host commands retain the signed-in user's filesystem and network
rights with no per-process CPU/memory ceiling. Approval, time/output/concurrency bounds, and explicit acknowledgement
reduce risk; they do not create OS isolation.

### Web access tools: `web_search` and `web_fetch`

Both tools are `ToolCategory.Network` and exist only while the `WebAccessEnabled` node setting is on (default off);
`WebFetchService` and `WebSearchService` re-read the switch on every call, and the review gate re-reads it before an
accepted (or auto-accepted) result is stored, so turning it off refuses in-flight offers and open review cards too.
The switch is independent of the external-access presets: choosing `offline` does not turn Web access off. ADR 0017
is the decision record.

- **Fetch boundary.** Every request and every redirect hop goes through `CustomToolSsrfGuard.ValidateRequestUrl`
  (open-host form, full private-address deny list) and the pinned connect callback on the `xe-web-fetch` client: GET
  only, no proxy, no cookies, manual redirects (at most five) under a per-call time budget (`WebFetchTimeoutSeconds`
  node setting, default 20 s, bounds 5–120 s), a 2 MiB decompressed body
  cap, and an HTML/XHTML/plain text/Markdown/JSON content-type allowlist. HTML is reduced to its main content by
  SmartReader; the text is capped at `WebFetchMaxContentChars` characters (node setting, default 12 000, bounds
  1000–100 000).
- **Search backends.** DuckDuckGo's HTML endpoint (best effort, unofficial; a block or rate limit returns a structured
  "unavailable" result) through the same guarded client, or the operator's SearXNG URL. The SearXNG client
  (`xe-web-search-searxng`) is not address-guarded because the operator typed the URL and it is often local; it is
  bound to that base URL and the model controls only the query string.
- **Untrusted output.** Page text, title and final URL, and every search result's title, URL and snippet, reach the
  model inside `UntrustedContentFraming` fences with the untrusted trust label. Bodies are never logged; URLs and queries
  only at Debug, because a query string is what an injected page would use to exfiltrate. When an OTLP exporter is
  configured (development or opt-in telemetry), the HttpClient spans carry each fetched URL as `url.full`.
- **Request consent (exfiltration gate).** Before any outbound request the turn parks on a consent card showing the exact
  URL (`web_fetch`) or query (`web_search`); Deny sends nothing and the model gets a decline note. Consent is per request
  (no session or domain scope) and covers the initial URL only: redirect hops stay server-side, SSRF-checked per hop (max 5),
  and the review shows the final URL. Parallel web calls get one card each, sequentially. Audit rows:
  `web-request approve|deny|timeout`.
- **Result review (prompt-injection gate).** After an allowed request, retrieved content enters the model's context only
  after the user accepted it on a review card; a rejection hands the model a decline note. The gate reuses the approval pause
  (`ToolApprovalCoordinator.RequestWebReviewAsync`): the host retrieves, then parks the turn with a preview, and the
  tool delegate only returns what the review stored in a per-invocation scope — any other caller gets a refusal.
  A per-conversation **auto-accept** mode skips both the consent and the review card (its notice says requests go out
  unconfirmed); the first time a user enables it they acknowledge a risk notice (stored in their tutorial state). Unattended runs never fetch; orchestration participants, agentic
  MCP scope and workflow-owned work sessions (no operator to review) are not offered the tools.
- **Graphs.** Agent nodes are never offered either tool. A Tool node may run `web_fetch` only against its own
  allowlist of URL prefixes (path-segment boundary; private addresses stay blocked even when listed), which is the
  consent that replaces the review for those URLs.

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

> **One scoped exception, and it does not move this boundary.** [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md) permits an opt-in Docker provider for Development Mode execution only (scope and shipping state: [Architecture Overview](01-architecture-overview.md#what-the-system-is)); AgentHome and Coder stay on `ProcessSandboxRuntimeProvider`, so hardening it is *not* superseded by the container work. The split is enforced by each feature's declared requirements rather than by configuration: each feature resolves its own role marker (`IAgentSandboxRuntimeProvider` / `IDevelopmentSandboxRuntimeProvider`), and the AgentHome and Coder declaration names a host toolchain that no container backend supplies, so they cannot be wired to the container provider even by mistake (ADR 0007; see [Backend selection](#backend-selection-a-feature-declares-what-it-needs-and-never-names-a-backend)). On Linux, **access to the Docker socket is root-equivalent**; the ADR records this rather than mitigating it. The shipped config sets no `Development:Sandbox` key, so `SandboxProviderSelector.ResolveDevelopment` falls back to the AgentHome provider and **the section below describes the default posture**; a node set to `Development:Sandbox:Provider=docker` runs Development Mode under the container boundary instead.

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

`SandboxIsolationMode.Filesystem` is an opt-in posture in which the host filesystem is not present in the command's mount namespace. `run_python` and a `Sandboxed` stdio MCP server request it and refuse to run where the provider does not advertise `SupportsFilesystemIsolation`; AgentHome requests it wherever the provider advertises it (see above). See [the reference page](reference/12-sandbox-modes-and-options.md#71-the-isolated-launch-mode-sandboxisolationmodefilesystem--opt-in-consumed-by-run_python) for the chain, the descriptor-based binds, the startup probe, cgroup termination and `run_python`'s bind set.

**What it is not.** It is a namespace boundary, not a kernel-hardened one — no seccomp filter, no LSM profile, no user-namespace-free design; a kernel LPE is out of scope for it exactly as it is for the default posture, and strong isolation remains MXC's job behind this same seam. There is no read-only *mount* capability (`SupportsReadOnlyMounts` stays off; `ReadOnlyTrees` is the isolated-mode surface, and a tree under a mount point the chain owns — `/usr`, `/dev`, `/proc`, `/work`, `/tmp`, the legacy roots — is **rejected** rather than mounted and silently shadowed). Isolation and a trusted host workspace are refused together: an isolated jail is tightened to 0700 and unreachable at its host path, which is the opposite of what a preserved checkout is for. And the jail-disk watchdog underneath it is unchanged — a best-effort visible-file occupancy check sampled every two seconds, which an unlink-then-write loop bypasses entirely. It is not a quota, and nothing here should be read as one; the current provider has neither a project quota nor a size-bounded mount.

The same no-follow / byte-recheck philosophy appears in AgentHome host-path safety (`Services/Workspace/Implementation/HostPathSafety.cs`: `TryResolveReparseWithinRoot`, `IsReparsePoint`, `IsPathWithinRoot`) and `HostGitRunner`. Reuse these utilities rather than re-implementing path validation.

### 7.2 Outbound MCP servers run under a declared trust tier

Every outbound stdio MCP server carries a trust tier. `Sandboxed` is the default, including for every existing registration, and runs under the §7.1 chain; a host that cannot serve it refuses the connection rather than degrading. `PrivilegedHost` is the old host launch, kept only as a per-server operator grant, and `BuiltInTrusted` is unreachable from the API. Every MCP tool of every tier stays approval-required. See [the reference page](reference/12-sandbox-modes-and-options.md#72-outbound-mcp-servers-run-under-a-declared-trust-tier) for bound-tree rules, failure reasons and secret masking. The rationale is [docs/security/mcp-trust-tiers.md](../security/mcp-trust-tiers.md).

### 7.3 External Apps run under an engine-owned container policy

One engine-owned policy builds every External Apps container and is re-verified against the daemon's read-back before and after start: `cap_drop ALL`, `no-new-privileges`, the seccomp profile, loopback-only publishing, a digest-pinned image and the declared mount set. See [the reference page](reference/12-sandbox-modes-and-options.md#73-external-apps-run-under-an-engine-owned-container-policy) for the full policy, the storage helper, secret masking, ownership labels and catalog trust. What it deliberately does not enforce:

- **Containers may run as in-container root, deliberately.** No `--user` is passed: the curated images drop
  privileges through their own entrypoints, and forcing a uid breaks that and a port-80 bind. The boundary is the
  container, the dropped capabilities, seccomp, `no-new-privileges` and the loopback network — **not** the uid.
  `capAdd` is bounded by Docker's own default 14, so a service can never exceed an unhardened `docker run`.
- **V1 enforces no outbound network restriction.** `permissions.internet` is always true and
  `permissions.localNetwork` is a **disclosure** on the install panel, not a control. Nothing denies either. Read the
  panel as what the application may do, never as an enforced boundary. There is likewise **no memory and no CPU
  ceiling**: the manifest's memory figures gate admission only.
- **The kill switch is a surface switch, not a stop button.** `ExternalApps:Enabled=false` 404s every route and the
  hub negotiate at the request-path middleware, ahead of the security middleware, so the switch cannot be probed by
  status code. It does not stop running containers and does not hide the navigation group, which is compile-time.
  The safe order is **stop or uninstall every instance, then disable**.

### 7.4 The seccomp profile every sandbox container carries

`DockerSeccompProfile` passes Docker's own default profile explicitly on every container create, because a container created without one reads back exactly like one with seccomp disabled, so only an explicit profile lets the fail-closed read-back tell them apart. See [the reference page](reference/12-sandbox-modes-and-options.md#74-the-seccomp-profile-every-sandbox-container-carries) for its provenance and why it is embedded.

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

Development Mode prepares two sandboxes: a warm restore with egress that runs once, from a clean base commit, and an agent-facing sandbox that requests `SandboxNetworkPolicy.None` wherever the backend advertises it. A dependency-manifest change fails validation. See [the reference page](reference/12-sandbox-modes-and-options.md#development-mode-egress-two-sandboxes-one-of-them-denied) for the warm-restore gate, the manifest set and the round budget.

> **The Option-B caveat, stated plainly.** A backend fails a confinement request it cannot honour *closed*. An
> unconditional `None` would therefore not harden Development Mode on Windows — or on any Linux host whose
> `unshare` probe failed — it would remove Development Mode from those nodes, because the shipped configuration
> resolves them to the process backend. On such a node **the attempt still has full host network access**, and
> the abuse case above is still live there. What makes that acceptable rather than silent is that the
> Development status surface reports the posture the provider actually **served**, not the one that was
> requested. Mandatory denial on every node is not part of the current cross-platform contract.

#### Committed credentials in the clone

Every prepare detects committed credentials with `ISensitiveFileExclusionService.IsSecret` and records them; only a backend with read-only mounts also shadows them. See [the reference page](reference/12-sandbox-modes-and-options.md#committed-credentials-in-the-clone) for detection, shadowing and the container-backed decision record.

> **On the process backend — today's default — only detection applies.** It has no mount layer, so nothing is
> shadowed and the recorded event is the whole control: the engine can see the committed credential but cannot
> stop the repository's own build or tests from reading it. Do not read this section as parity between the two
> backends.

For the opt-in container-backed provider ([ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md)):

- **On Linux, Docker-socket access is root-equivalent.** The ADR documents this rather than mitigating it. Rootless
  Docker is the operator's option; the product neither depends on it nor claims it. Do not describe the container
  provider as removing host-user risk.
- **A pinned image digest pins bytes, not hermeticity.** Mounts, runtime state, host kernel, platform, dependency
  resolution and network inputs all stay variable. Do not describe digest pinning as reproducibility.

#### The managed workspace's Git configuration is engine-owned

The engine rewrites the managed workspace's `.git/config` to a minimal allow-listed one immediately before its first host-side Git command, because a repository-defined filter driver or `core.fsmonitor` would otherwise run on the host. See [the reference page](reference/12-sandbox-modes-and-options.md#the-managed-workspaces-git-configuration-is-engine-owned).

##### The whitespace policy is derived from the index

The whitespace exemption is granted per path, only to paths whose index content is CRLF, through an engine-written `.git/info/attributes` a repository cannot override. See [the reference page](reference/12-sandbox-modes-and-options.md#the-whitespace-policy-is-derived-from-the-index).

#### The per-task CLI environment, and the PATH it must not leak into

Every sandboxed Development command runs with per-task `HOME`, `TMPDIR`, `NUGET_PACKAGES` and `DOTNET_CLI_HOME`, plus `MSBUILDDISABLENODEREUSE=1` and `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0` so per-task state cannot escape the task. See [the reference page](reference/12-sandbox-modes-and-options.md#the-per-task-cli-environment-and-the-path-it-must-not-leak-into) for the measured failures behind each.

> **`DOTNET_SKIP_FIRST_TIME_EXPERIENCE` is not an alternative** — it is a no-op in .NET 10. This is the obvious fix
> a reader will reach for; it does nothing.

#### The test-write policy's protected-path set

`DevelopmentCommandProfileCatalog.DefaultProtectedPaths` names the paths an agent may create but not modify, delete or rename; the set is code-owned and versioned, never configuration. See [the reference page](reference/12-sandbox-modes-and-options.md#the-test-write-policys-protected-path-set) for each pattern and why two were left out.

#### The workspace surveys are managed code

`WorkspaceFileScanner` implements `list_files` and `search_text` in managed code, never follows or emits a symbolic link, and applies one suppression predicate at both the prune and the emit step. See [the reference page](reference/12-sandbox-modes-and-options.md#the-workspace-surveys-are-managed-code).

- **That predicate must gate reads on `ISensitiveFileExclusionService.IsSecret`**, never on the broader `IsExcluded`
  copy filter. Conflating them refuses `obj/`, which an agent legitimately reads after a failed build, while protecting
  nothing — build output is not a credential.

### Backend selection: a feature declares what it needs, and never names a backend

Each workload declares its requirements in `SandboxWorkloads` and `SandboxProviderSelector` resolves the minimal-satisfying backend, or throws `SandboxCapabilityNotSupportedException` naming the unmet axis, with no fallback and no downgrade ([ADR 0007](../adr/0007-sandbox-execution-substrate-and-backend-selection.md)). See [the reference page](reference/12-sandbox-modes-and-options.md#backend-selection-a-feature-declares-what-it-needs-and-never-names-a-backend) for the isolation floor, the isolation panel's served-posture rule, the ceiling profiles and the operator keys.

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

### Chat attachments are staged *into* the jail, not read from the host

When a chat agent-mode turn needs to read a conversation's uploaded files, `IConversationSandboxStager` (`Services/AgentHome/IConversationSandboxStager.cs`) re-stages the **existing** node sandbox so it holds **only** that conversation's extracted attachments under the workspace `attachments/` alias (the sandbox is recreated first, so it never carries another conversation's residue). The agent then reaches them with the same jailed `list_files`/`read_file`/`search_text` tools — meaning every read still passes through the §7 path-confinement, symlink-escape, and `O_NOFOLLOW`/byte-cap guards above; staging adds no host-filesystem read path that bypasses the jail. Attachments may contain secrets or confidential content: their stored bytes are encrypted at rest by `UploadedFileBlobProtector` (§5), but extracted content exists as plaintext while decrypted and staged for use, and no secret scan occurs before staging. `MemoryProposalSecretScanner` applies only to a later memory proposal before that proposal is persisted. Don't add a staging path that writes outside the workspace root or skips the recreate-before-stage step.

### Landing a patch on the host is the operator's act, never the model's

The one AgentHome path that writes **outside** the jail is `INodePatchApplyService`, which applies a run's exported `changes.patch` onto the real selected folders. It is reachable only from `NodeOperator`-gated endpoints, binds the apply to the previewed patch hash, and refuses traversal, `.git` writes, gitlinks and symlinks by name. See [the reference page](reference/12-sandbox-modes-and-options.md#landing-a-patch-on-the-host-is-the-operators-act-never-the-models) for every guard and where it is proved.


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

### Reference pages

- [Admin API surface](reference/12-admin-api-surface.md) — §3 mechanisms: Host/Origin gate, sessions and authorization, MCP and model-proxy keys, container bridge, Codex OAuth
- [Sandbox modes and options](reference/12-sandbox-modes-and-options.md) — §7 detail: isolated launch mode, MCP trust tiers, External Apps container policy, seccomp profile, Development Mode controls, backend selection, patch-apply guards
