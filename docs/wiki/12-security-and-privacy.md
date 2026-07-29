# Security & Privacy Model

> Baseline: `7e64ed589e14eecc0e522e807d2e531a1095d19a` · Reviewed: 2026-07-28 · Code-grounded.

This page documents the cross-cutting security and privacy controls implemented in the XE Local AI
Engine node and the invariants contributors are expected to preserve. It is code-and-test evidence
for the stated baseline, not proof of operating effectiveness, deployment configuration, compliance,
certification, or formal risk acceptance. The supported design routes platform traffic through the
node-owned `WorkerHub`, keeps secret-bearing values behind node-local stores and redaction seams,
serves management/admin APIs on loopback, encrypts selected sensitive fields at rest, routes designated
privacy-sensitive AI work to node-local models, and confines application-mediated tool file access.

If you are touching persistence, see [Data & Persistence](08-data-and-persistence.md) for the encryption schema; for endpoint/hub surface see [API & Hubs](09-api-and-hubs.md); for the node-local AI rule in agent flows see [Agent Mode](04-agent-mode.md).

---

## 1. Egress invariant: WorkerHub is the only platform channel

The node is the *single* outbound connection to the C0re platform. All platform-facing traffic flows through one SignalR `HubConnection` owned by `WorkerHubConnection` (`XE-Local-AI-Engine.Client.Application/Services/Connection/Implementation/WorkerHubConnection.cs`, contract `IWorkerHubConnection`). Nothing in the browser, in a tool, or in a provider opens its own connection to the platform.

| Concern | Owner | Evidence |
|---|---|---|
| The one platform connection | `WorkerHubConnection` | `Services/Connection/Implementation/WorkerHubConnection.cs` |
| Event/RPC handlers from platform | `WorkerHubConnection.EventHandlers.cs` | same folder |
| Heartbeat / liveness to platform | `HeartbeatBackgroundService` | `XE-Local-AI-Engine.Client/BackgroundServices/HeartbeatBackgroundService.cs` |
| Capability reporting | `CapabilityReporter` | `Services/Capabilities/Implementation/CapabilityReporter.cs` |
| Graceful drain on shutdown | `WorkerShutdownDrainService` | `Services/Shutdown/Implementation/WorkerShutdownDrainService.cs` |

**Maintainer rule:** new platform-facing behavior must go through the WorkerHub seam. Do not give the React client or a provider project a direct line to the platform — the browser talks only to the *local* node API/hubs ([API & Hubs](09-api-and-hubs.md)), and the node relays to the platform.

### No background autostart — connection is opt-in

The node does **not** silently dial the platform on process start. `AutoConnectBackgroundService` gates the auto-connect on a stored, operator-controlled flag:

```csharp
// XE-Local-AI-Engine.Client/BackgroundServices/AutoConnectBackgroundService.cs:66
if (!_tokenStore.AutoConnectOnStart)
{
    // ...does not connect
}
```

A fresh install is inert until the operator opts in. Preserve this contract: do not add a code path that connects to the platform without an explicit, persisted operator decision.

---

## 2. Secret-handling invariant — not returned to the browser or deliberately logged

The baseline implementation treats the following as node-local secrets: the **node operator secret**
(master key material), the **worker credentials / endpoint tokens** used on WorkerHub,
**cloud-provider credentials** (for example Codex OAuth), the **HMAC/JWT signing keys**, and the
**HuggingFace token**. DTOs, stores, and redactors are designed so these values are not returned across
the browser boundary or deliberately logged. That source-level design does not by itself prove the
absence of secrets from every operational log or diagnostic artifact.

### 2.1 The operator secret and derived keys

The operator secret is the root of all node key material. `NodeOperatorSecretProvider` (`Services/Persistence/Implementation/NodeOperatorSecretProvider.cs`) resolves a **32-byte** secret, in priority order, from:

1. env var `XE_NODE_SQLITE_KEY` (base64-encoded 32 bytes), or `IConfiguration[XE_NODE_SQLITE_KEY]`;
2. a raw 32-byte secret file at `/run/secrets/node-sqlite-key`;
3. Aspire parameter `Parameters:node-sqlite-key` (local dev only).

If none of those sources provides a value, startup *fails fast* with a helpful message. In the B1 Aspire development path, the tracked AppHost configuration supplies the shared default described below unless it is overridden.

> **Development confidentiality warning.** `secret: true` marks the Aspire parameter as sensitive for display and handling, but B1 also commits one shared development-only value in `XE-Local-AI-Engine.AppHost/appsettings.Development.json`. It is therefore a default, not a confidential or installation-unique secret: anyone with the source can derive keys for data created under that unchanged value. A confidential per-developer override is possible but is not enforced or evidenced. Packaged desktop mode is different: `DesktopBootstrap` generates and persists a per-installation `node.key`.

The secret is **never held longer than necessary**. `NodeSqliteKeyHolder` (`Services/Persistence/Implementation/NodeSqliteKeyHolder.cs`) derives the SQLite key with HKDF-SHA256 (info `c0re-node-sqlite|v1|{NodeName}`) in its constructor, then immediately zeroes the source secret with `CryptographicOperations.ZeroMemory`, and zeroes its own derived key on `Dispose`. The JWT signing key is derived separately (`NodeJwtKeyProvider`, `Services/Auth/Implementation/NodeJwtKeyProvider.cs`) so the at-rest key and the auth key are never the same bytes.

**Maintainer rules:**
- Never log, echo, or return operator-secret-derived material across any DTO.
- Keep at-rest and auth key derivations using *distinct* HKDF `info` strings (regression risk if collapsed).
- The 32-byte length is validated; don't relax it.

### 2.2 Redaction in logs, transcripts, and DTOs

Several redactors enforce "secrets never surface":

| Redactor | Purpose | Location |
|---|---|---|
| `AccessTokenQueryRedactor` | strips `access_token=` from request query strings before Serilog logs them | `Services/Auth/AccessTokenQueryRedactor.cs`, wired in `Program.cs:83` via `UseSerilogRequestLogging` |
| `MemoryProposalSecretScanner` | rejects/redacts secrets in agent-memory proposals before persistence (PEM keys, GitHub/AWS/Azure/Slack tokens, JWTs, high-entropy bearers; ReDoS-guarded with a 2s regex timeout) | `Services/AgentHome/Implementation/MemoryProposalSecretScanner.cs` |
| `McpServerConnectionManager.Redact` | clamps MCP connection failures to a generic message so a command path/URL/secret never reaches the UI | `Services/Mcp/Implementation/McpServerConnectionManager.cs:344` |
| `InvocationRunner.RedactAgentRuntimeMessage` | sanitizes agent runtime failure messages before surfacing | `Services/Invocation/Implementation/InvocationRunner.FailureClassification.cs:91` |
| `NodePatchApplyService.Redact` | redacts patch-apply output (AgentHome) | `Services/AgentHome/Implementation/NodePatchApplyService.cs:469` |

The request-logging enricher is the canonical example — it replaces the raw query with a redacted one before anything is written:

```csharp
// Program.cs:83
var redactedQuery = AccessTokenQueryRedactor.Redact(httpContext.Request.QueryString.Value);
diagnosticContext.Set("RequestPathWithRedactedQuery", $"{httpContext.Request.Path}{redactedQuery}");
diagnosticContext.Set("QueryString", redactedQuery);
```

The marker used across redactors is the literal `[REDACTED]` (and `[REDACTED:…]`-style markers in the secret scanner). The scanner's "bare high-entropy" regex is deliberately written so the `[`/`]` of an existing marker stays outside the match — a second pass never re-redacts an already-redacted span (`MemoryProposalSecretScanner.cs`).

**Maintainer rule:** any new field that can carry a credential, host path, command, or URL toward the browser, logs, or a saved transcript must pass through (or extend) a redactor. When in doubt, clamp to a generic reason like `McpServerConnectionManager.Redact` does.

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
// LocalApiSecurityMiddleware.cs:40
if (IsLocalApiRequest(context.Request.Path)
    && (!IsLoopbackPeer(context.Connection.RemoteIpAddress)
        || !IsAllowedHost(context.Request.Host.Host)
        || !IsAllowedOrigin(context.Request)))
{
    context.Response.StatusCode = StatusCodes.Status403Forbidden;
    return;
}
```

- **Loopback peer check** is the authoritative transport-level gate: `context.Connection.RemoteIpAddress` is the address of the socket peer — the machine that opened the TCP connection to Kestrel — so a routable caller is rejected even if it forges a loopback `Host`/`Origin`. A **null** peer address means the request never traversed the network stack (the in-process/in-memory test host and in-process health probes present no peer) and is treated as loopback-equivalent; only a concrete non-loopback address is rejected (`LocalApiSecurityMiddleware.cs:52`).
- **Allowed hosts** are exactly `localhost`, `127.0.0.1`, `::1` (case-insensitive; IPv6 brackets normalized off).
- **Origin check** is fail-closed: an absent `Origin` is permitted (same-origin navigation), but any *present* `Origin` must parse, be a loopback host, and match the request's scheme + host + port exactly. A non-loopback or mismatched origin is rejected.
- Ordering matters: the middleware runs *before* `UseRouting`/`UseAuthentication`/`UseAuthorization` in `Program.cs`, so a non-local caller is rejected before it can reach an endpoint at all.

> **Reverse proxies / headless deployment are unsupported.** The peer check reads the socket peer, and no forwarded-headers middleware is registered, so `X-Forwarded-For` is never honoured. A reverse proxy on the **same host** would appear as a loopback peer on every forwarded request and defeat the peer gate — this is by design: the app is single-user, same-machine only. Putting `/api/local/v1` behind a proxy or exposing it beyond the local machine is out of scope and not a supported configuration.

### 3.2 Authentication & authorization

Local endpoints are still authenticated and policy-gated; loopback is necessary but not sufficient. `NodeAuthorizationPolicies` (`Services/Auth/NodeAuthorizationPolicies.cs`) defines the `NodeOperator` policy (claim type `role`, `Admin`), and endpoints apply it — e.g. `ListAgentExecutionLogsEndpoint.Configure()` calls `Policies(NodeAuthorizationPolicies.Operator)` (`XE-Local-AI-Engine.Client/Endpoints/Agents/V1/ListAgentExecutionLogsEndpoint.cs:27`). JWTs are signed with the separately-derived node JWT key (§2.1). Auth wiring lives in `AddNodeAuthAndConnectionExtensions`. See [API & Hubs](09-api-and-hubs.md) for the full endpoint inventory.

### 3.3 Desktop / loopback hosting

In desktop mode the node binds plain HTTP on loopback only and the HTTPS-redirect/HSTS branch is bypassed by design (`if (!isDesktop)` guard in `Program.cs`); `LoopbackUrlResolver` / `DesktopLifecycle` (`XE-Local-AI-Engine.Client/Hosting/`) pick an auto-port loopback URL. See [Hosting & Deployment](11-hosting-and-deployment.md). The loopback bind plus the peer + Host/Origin middleware together keep the admin surface off the network.

### 3.4 Startup bind guard (`LoopbackBindGuard`)

`LoopbackBindGuard` (`XE-Local-AI-Engine.Client/Hosting/LoopbackBindGuard.cs`, wired via `LoopbackBindGuard.Guard(app)` in `Program.cs`) is defense-in-depth behind the request-time middleware: instead of trusting the configured URLs, it inspects the addresses Kestrel *actually* bound (post `ApplicationStarted`, so an OS-assigned port and wildcard expansion are already resolved) and, if any is non-loopback, logs a **critical** line naming the offending address(es) and shuts the app down.

- The shutdown sets `Environment.ExitCode = 1` before calling `StopApplication()`, so a supervisor/CI treats the guarded stop as an **error** (exit code 1) rather than a clean shutdown (`LoopbackBindGuard.cs:81`).
- Wildcard binds (`*`, `+`, `0.0.0.0`, `::`) are treated as non-loopback and trigger the guard; `localhost` and any loopback IP literal pass.
- **Opt-out:** setting `Security:AllowNonLoopbackBind=true` skips the guard entirely — for an operator who has secured the surface themselves. It defaults to `false`, and no supported launch needs it (desktop binds `127.0.0.1`; Aspire dev binds `localhost` and exposes externally via the DCP proxy, not the app process), so the guard is a no-op on every supported launch and only fires on a deliberately overridden routable bind.

**Maintainer rules:**
- Mount any new local-admin route under `/api/local/v1` so the middleware covers it; routes outside that prefix are *not* loopback-gated by this middleware.
- Keep the Origin check fail-closed — never widen `AllowedHosts` to a public address.
- Apply an authorization policy in addition to the loopback gate; do not rely on loopback alone.
- Do not add forwarded-headers middleware or a reverse proxy in front of this surface, and do not set `Security:AllowNonLoopbackBind` to enable a routable/headless deployment — those configurations are unsupported (§3.1).

---

## 4. Exception handling: no internal detail leakage

Unhandled exceptions are translated to RFC7807 ProblemDetails by `DefaultExceptionHandler` (`XE-Local-AI-Engine.Client/ExceptionHandling/DefaultExceptionHandler.cs`), registered via `app.UseExceptionHandler()` *before* FastEndpoints. Crucially, the exception *message* is only included in the response in development/test environments; in production the detail is a fixed `"An unexpected error occurred"`:

```csharp
// DefaultExceptionHandler.cs:18
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

Conversation retention is a separate privacy control, **disabled by default** (`ChatRetentionOptions.Enabled = false`, config section `ChatRetention`): it permanently deletes whole conversations older than the configured window, so it must be explicitly opted into. When enabled, the sweep and the interactive immediate-purge both delete a conversation's **complete footprint** through one shared helper (`ConversationFootprintPurge`): every child DB row (messages, tool events, **feedback**, **uploaded-file rows**, tombstones, and **`agent_execution_logs`** rows — the run-envelope and adaptive-memory telemetry keyed by `conversation_id`, so a purge leaves no residual per-conversation run metadata) plus the conversation row, then the on-disk upload blobs. Because `PRAGMA foreign_keys=ON` is not set on the node connection, every child table must be listed explicitly — the shared helper is the single source of truth so the two paths can't drift. DB rows commit first, blobs are torn down after, and an orphan resweep on each pass removes any upload directory whose conversation row no longer exists (covering a crash between the commit and the blob delete).

---

## 6. Privacy-sensitive AI runs node-local only

At the baseline, agent-memory/playbook **analysis**, the playbook **eval/golden-conversation gate**,
and memory extraction are wired to **node-local models**, not a cloud provider. This is the implemented
privacy contract behind the playbook pipeline; source wiring and tests establish the path, while this
page does not claim operational network observation. See [Agent Mode](04-agent-mode.md) for the
playbook P1–P5 / eval flow and the adaptive-memory extraction loop; the wiring decisions live in
`XE-Local-AI-Engine.Client.Application/Services/*` and the provider seams in
`Providers.Abstractions` (`ILocalModelProvider` / `IChatClient` / `IEmbeddingGenerator`).

**Maintainer rule:** when adding any AI step that consumes user conversation/memory content for analysis or evaluation, route it through the node-local provider path. Do not let a cloud provider (Codex OAuth, etc.) become the executor for analysis/eval. Cloud credentials themselves are local-only secrets (§2).

### Two recent subsystems have explicit egress boundaries

- **Voice / text-to-speech has client-side synthesis and model-download egress.** Kokoro / WebGPU inference executes in the React app and generated audio is not posted to the node. On first use, however, the browser fetches Kokoro model files directly from manifest-provided Hugging Face URLs (`ModelCache.ts`; `KokoroVoiceCatalog.cs`). The Web Speech fallback delegates to the browser/operating-system implementation, so this repository does not establish that fallback's network behavior. See [React Client](10-react-client.md).
- **Inference profiling / machine key is local-only, per-box.** The per-machine launch-tuning profiles ([Local Runtime & Providers](03-local-runtime-and-providers.md)) are keyed by a `MachineKeyProvider` identifier that is a **local-only random id** — never hardware-derived, and `IMachineKeyProvider` documents it must **NEVER** be emitted in telemetry, aggregates, or logs. The profiles themselves hold only structural launch args (no secrets) and never leave the node. Keep the machine key off every outbound DTO/aggregate.

---

## 7. Sandbox / process-jail for tool execution

Any node-side tool or shell execution runs inside a process jail, not against the host filesystem directly. The live provider is `ProcessSandboxRuntimeProvider` (`Services/Sandbox/Implementation/ProcessSandboxRuntimeProvider.cs`, implementing `ISandboxRuntimeProvider`; selected via `SandboxProviderSelector`). The old Docker/container sandbox runtime was removed in the 2026-06-17 runtime re-architecture — there is **no** container inference path, and this process-jail is the execution boundary for AgentHome and Coder. (Discrepancy note vs. older docs: `LocalContainerSandboxProvider` and the HostAgent layer no longer exist as live code.)

> **One scoped exception, and it does not move this boundary.** [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md) (Accepted 2026-07-29) permits Docker for **Development Mode build/test/lint execution only**, as a stopgap ahead of MXC. Provider selection is **per feature**: Development Mode gets the container provider; **AgentHome (4 injection sites) and Coder (3) stay on `ProcessSandboxRuntimeProvider`** and keep exactly the posture described below. Two things follow for a security reader. First, hardening the process provider is *not* superseded by the container work — those two features remain on it. Second, on Linux **access to the Docker socket is root-equivalent**; the ADR records this rather than mitigating it, and the product neither requires nor provides rootless Docker. The container provider is Slice 3 of `Plans/2026-07-28-dev-mode-container-sandbox-and-command-profiles-plan.md` and is **in progress**; until it lands, Development Mode also runs on the process provider, and the section below is the whole story.

**What this boundary is — and is not.** It is **supervised execution**, not an OS isolation boundary. What it enforces: only fixed, node-authored executables run (`dotnet --version`, `git` with hooks disabled, `find`/`grep` — never a model-authored command line); a working-directory jail with path-confinement and symlink-escape guards; a **scrubbed child environment** (the worker's secret-bearing environment — cloud API keys, OAuth tokens, the node SQLite key — is **not** inherited; only a fixed system/toolchain allow-list is forwarded, plus the caller's explicit variables); a per-command timeout; tree-kill teardown; and captured-output byte caps. It is **not** a hardware or kernel isolation boundary. Risky execution is approval-gated upstream, but no formal acceptance of the residual host-user execution risk is established by this repository documentation.

**Network and resource containment are per mechanism and per host — never assume either from this page alone.** What the current host can actually deliver is *measured once at startup* into `SandboxContainment` (`Services/Sandbox/Implementation/Launch/SandboxContainment.cs`), and each mechanism is independently optional: process-group launch (`setsid`), CPU/memory/PID ceilings (`systemd-run --user`), and network isolation (`unshare` — a fresh **empty network namespace** with no route to host loopback, the LAN, or the cloud-metadata endpoint). Each is probed by really performing the operation, not by testing for the binary. Off Linux, and where every probe fails, the record is `SandboxContainment.None` and the child is a plain process with the host's network and no ceilings.

The **capability-honesty invariant** runs in both directions off that one record, which is what keeps advertisement and enforcement from drifting apart:

- `Capabilities` advertises `SupportsNetworkPolicy` / `SupportsResourceLimits` **only** where the matching mechanism is active.
- `BuildLaunchPolicy` (called by `CreateOrAttachAsync`) **fail-closed rejects** any `SandboxCreateRequest` asking for something the host cannot serve, with `SandboxCapabilityNotSupportedException`, rather than silently returning a sandbox weaker than requested. It rejects on three counts: `NetworkPolicy.Restricted` at all (no allow-list mechanism exists), a denial request when `SupportsNetworkIsolation` is false, and resource limits when `SupportsResourceLimits` is false.
- Each `…UnavailableReason` carries the measured reason, so a degraded host logs *why* and a live-gated test skips with a reason instead of passing silently.

Neither half may be softened into a silent no-op: a caller must never believe it received isolation the provider does not implement.

Two scope limits a reader must not overrun. **Egress is deny-everything or nothing** — where the mechanism is active the child gets an empty namespace, so egress is denied outright rather than filtered; `SandboxNetworkPolicy.Restricted` (an allow-list) stays unsupported and rejected. And **Development Mode does not get this** — `DevelopmentWorkspaceProvider` requests `Unrestricted` because its `dotnet restore` needs the network until the container work's restore machinery exists. Only AgentHome requests the denial, so there is no engine-wide egress posture to cite — though **Coder is covered too**, because `CoderWorkspaceReader` does not create its own sandbox: it attaches to AgentHome's via `ISandboxRuntimeProvider.ConnectAsync`, so that one policy decision covers AgentHome's 4 injection sites and Coder's 3. The request is itself **capability-gated** (`AgentHomeService.ResolveNetworkPolicy`): it asks for `None` only where the provider advertises `SupportsNetworkPolicy`, and `Unrestricted` otherwise — an unconditional request would be rejected fail-closed on any host without the mechanism. The AgentHome `policy.json` records the posture in force **at the time the agent home was initialised** (`"unrestricted"` when nothing is enforced, `"disabled"` when egress is denied); note that `EnsureBaselineFilesAsync` deliberately does **not** overwrite an existing `policy.json`, so a home created before denial shipped keeps a file reading `"unrestricted"` while the run is actually denied. That preservation is a tested contract protecting operator edits across re-init, and the drift runs in the safe direction — the file under-reports the boundary, never over-reports it — so read the provider's advertised capability, not this file, when you need the posture of a *current* run. An empty namespace is still not a kernel-hardened boundary: **strong isolation remains deferred to a future OS-isolated provider (MXC) behind this same seam**, and approval-gating upstream stays the interim control wherever a mechanism is inactive.

Guards a contributor must not weaken:

- **No inherited worker environment.** `ExecuteAsync` clears the child's environment and repopulates it only from `InheritableEnvironmentAllowlist` (PATH/HOME/temp/locale/`DOTNET_*` + Windows essentials), then layers the caller's explicit `request.Environment`. Never widen this to inherit the parent environment — the worker holds secrets that must not reach a sandbox command.
- **Fail-closed capability contract.** Do not soften `CreateOrAttachAsync`'s rejection of unenforceable network/resource guarantees into a silent no-op; a caller must never believe it received isolation the provider does not implement. Equally, do not advertise a capability the startup probe did not measure as active — both halves must keep reading the same `SandboxContainment`.
- **The user-bus environment strip is load-bearing, not tidiness.** A network namespace does **not** confine UNIX sockets. `systemd-run --user` needs `XDG_RUNTIME_DIR` to reach the per-user systemd bus, and a sandboxed child that inherited it could start a unit **outside** its own scope and namespace — escaping both the resource ceiling and the egress denial. Verified live before the fix. Those variables are injected for the launch **wrapper only** and stripped by an `env -u` layer immediately before the sandboxed executable is exec'd; never let them reach the child.

- **Path confinement.** `ResolveJailPath` and `IsUnderJailRoot` reject any path that escapes the jail root before any file op happens.
- **Symlink-escape guard.** `EnsureNoSymlinkComponentsUnderJail` walks from the resolved leaf upward to the jail root and throws `UnauthorizedAccessException` on the *first* symlink component — defeating a "plant-a-symlink-after-resolve" swap (`ProcessSandboxRuntimeProvider.cs`).
- **O_NOFOLLOW file I/O.** Host file reads/writes use a libc `open()` `DllImport` with `O_NOFOLLOW` (plus `O_CLOEXEC`), because a managed `(FileOptions)` cast for `O_NOFOLLOW` throws. The kernel fails with `ELOOP` if the leaf is a symlink, closing the check-then-open (TOCTOU) race a managed `lstat`+open would leave. See `OpenNoFollow`, `ReadJailFileBytesNoFollowAsync`, `WriteJailFileNoFollowAsync` in `ProcessSandboxRuntimeProvider.cs`. A historical finding: plain `git apply` did *not* reject a `--binary` literal patch, so byte-level guards are not optional.
- **Byte caps + growth check.** `ReadHostFileUnderGuard` enforces a per-file byte cap and blocks (returns `null`) if the file *grew* after sizing — a swap-after-walk signal — rather than silently truncating.
- **Tree-kill teardown.** Killing a sandbox `TreeKill`s the process tree (`process.Kill(true)`) and best-effort deletes the jail dir, so a sandbox kill terminates every running command.

The same no-follow / byte-recheck philosophy appears in AgentHome host-path safety (`Services/Workspace/Implementation/HostPathSafety.cs`: `TryResolveReparseWithinRoot`, `IsReparsePoint`, `IsPathWithinRoot`) and `HostGitRunner`. Reuse these utilities rather than re-implementing path validation.

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
and tests run as the host user and have that user's host filesystem and network access. The Process sandbox and
Agent Home are application-level path, byte, environment, and lifecycle controls; neither is an OS security
boundary. Do not describe the selected folder as a kernel-enforced filesystem allow-list.

MXC remains future provider work behind the existing sandbox/workspace seams. It is not integrated today, and no
current MXC profile should be documented as a security boundary. Any future provider must implement and
independently validate the isolation guarantees it advertises.

**Container-backed Development Mode execution is decided but not yet shipped.**
[ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md) (Accepted 2026-07-29) approves a
Docker-backed provider behind the same `ISandboxRuntimeProvider` seam for Development Mode build/test/lint
execution — as a **stopgap ahead of MXC**, which stays the long-term hard-isolation seam. The paragraphs above
describe what executes today and remain accurate until Slice 3 of
`Plans/2026-07-28-dev-mode-container-sandbox-and-command-profiles-plan.md` lands. What the decision already fixes,
and what a reviewer should hold it to:

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

### Chat attachments are staged *into* the jail, not read from the host

When a chat agent-mode turn needs to read a conversation's uploaded files, `IConversationSandboxStager` (`Services/AgentHome/IConversationSandboxStager.cs`) re-stages the **existing** node sandbox so it holds **only** that conversation's extracted attachments under the workspace `attachments/` alias (the sandbox is recreated first, so it never carries another conversation's residue). The agent then reaches them with the same jailed `list_files`/`read_file`/`search_text` tools — meaning every read still passes through the §7 path-confinement, symlink-escape, and `O_NOFOLLOW`/byte-cap guards above; staging adds no host-filesystem read path that bypasses the jail. Attachments may contain secrets or confidential content: their stored bytes are encrypted at rest by `UploadedFileBlobProtector` (§5), but extracted content exists as plaintext while decrypted and staged for use, and no secret scan occurs before staging. `MemoryProposalSecretScanner` applies only to a later memory proposal before that proposal is persisted. Don't add a staging path that writes outside the workspace root or skips the recreate-before-stage step.

---

## 8. Compile-time guardrails (`BannedSymbols.txt`)

The repo enforces a "banned API" wall via `Microsoft.CodeAnalysis.BannedApiAnalyzers` (RS0030), promoted to a build *error* by repo-wide `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`. `BannedSymbols.txt` (repo root) applies to production projects only (test/tooling exempt via `IsTestOrToolingProject` in `Directory.Build.props`). Current bans:

| Banned | Use instead |
|---|---|
| `DateTime.Now` / `DateTime.UtcNow` / `DateTimeOffset.Now` | inject `TimeProvider`, call `GetUtcNow()` / `GetLocalNow()` |
| `Thread.Sleep(...)` | `await Task.Delay(..., cancellationToken)` |
| `GC.Collect(...)` | let the runtime manage GC |

The file documents its own scope: it is the "safe set" — APIs with zero current production usage — so the wall blocks *new* occurrences while keeping the build green. (`DateTimeOffset.UtcNow` and sync-over-async `.Result`/`.Wait()`/`.GetAwaiter().GetResult()` are explicitly *not yet* banned, pending a dedicated refactor.) Separately, a literal `TODO`/`FIXME`/`HACK`/`XXX` in a comment fails the build (Sonar S1135 = error) — phrase deferred work as "… follow-up:".

**Maintainer rule:** don't suppress RS0030 to land a banned call; fix the call site.

---

## Invariant checklist (for reviewers)

- [ ] Platform traffic goes only through `WorkerHubConnection`; nothing else dials the platform.
- [ ] No code path connects on startup without the `AutoConnectOnStart` opt-in.
- [ ] No secret (operator secret, worker/endpoint token, cloud cred, HMAC/JWT key, HF token) is returned to the browser or logged; new credential-bearing fields pass a redactor.
- [ ] New local-admin routes live under `/api/local/v1`, keep the Host/Origin gate fail-closed, and apply an authorization policy.
- [ ] Production error responses carry no internal detail (message/stack/ids).
- [ ] At-rest crypto routes through `AesGcmNodeAeadCipher`; AAD context components are preserved.
- [ ] Analysis/eval/extraction AI runs node-local only.
- [ ] Tool execution stays inside the jail with symlink + O_NOFOLLOW + byte-cap guards.
- [ ] No banned API (RS0030) and no literal TODO/FIXME comment.

---

## Related pages

- [Architecture Overview](01-architecture-overview.md)
- [Local Runtime & Providers](03-local-runtime-and-providers.md)
- [Agent Mode](04-agent-mode.md) — node-local analysis/eval rule, sandboxed tool execution
- [Data & Persistence](08-data-and-persistence.md) — at-rest encryption schema & interceptor
- [API & Hubs](09-api-and-hubs.md) — `/api/local/v1` surface, auth policies, local hubs
- [Hosting & Deployment](11-hosting-and-deployment.md) — loopback/desktop binding, no-autostart
- [Testing & Validation](13-testing-and-validation.md) — persistence-encryption & loopback tests
- [Technical/Security Architecture Dossier](../audits/technical-security-architecture/README.md) — baseline auditor narrative, evidence states, and residual-risk limitations
