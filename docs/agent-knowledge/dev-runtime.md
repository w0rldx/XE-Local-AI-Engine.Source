# Dev environment and local runtime

Scope: host pipeline, sandbox host side, HTTP clients, Windows. Read when: touching `Program.cs`/Kestrel/auth, HTTP
clients or a Windows-only branch. Starting, stopping or scripting a dev host, lab or live round: [dev-lab](dev-lab.md).
llama.cpp binaries, launch fallback and tool-call gates: [inference-runtime](inference-runtime.md).

## Host pipeline and auth

### `Referrer-Policy: no-referrer` makes the loopback gate refuse same-origin writes

**Rule:** keep the node's `Referrer-Policy` at `same-origin`. Under `no-referrer` browsers send `Origin: null` on a same-origin POST, which `LocalApiSecurityMiddleware.IsAllowedOrigin` refuses with 403. **Prevents:** a header-hardening change that breaks every SPA write while requests from non-browser clients keep working. **Authority:** `NodeResponseHeaders` (Referrer-Policy comment); `LocalApiSecurityMiddleware.IsAllowedOrigin`.

### An explicit Kestrel endpoint discards the addresses `UseUrls` was given

**Rule:** `builder.WebHost.ConfigureKestrel(o => o.Listen(...))` replaces, not adds: with non-empty `ListenOptions`,
`AddressBinder` logs "Overriding address(es)" and drops the `UseUrls` addresses. To open a second listener, read
`builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey)`, split on `;`, and `UseUrls` the union. **Prevents:** a bridge
`Listen` silently dropping the loopback listener that desktop mode and Aspire (`ASPNETCORE_URLS`) configure, taking the SPA
and `/api/local/v1` with it. **Authority:** `Program.cs` (`ServerUrlsKey` union), `ContainerBridgePipeline`.

### Refresh rotation's grace window is discriminated by the SUCCESSOR LINK, never by "revoked recently" or by a timestamp

**Rule:** `NodeAuthService.RefreshAsync` honours a rotated-away token for ten seconds only by following
`replaced_by_token_id` (written only by `INodeIdentityStore.RotateAsync`) to a live, unexpired chain head. Never key it on
`revoked_at_utc` (every revoke path writes it) or a user + instant match (crosses sessions). The grace path never re-stamps
`RevokedAtUtc` or re-links; the grace pair is a sibling of the head. Sessions are independent chains: logout and a session revoke end
one session; sign out everywhere, password change and reset revoke all. **Prevents:** reviving a logged-out cookie, or a login signing other clients out.
**Authority:** `NodeAuthRefreshRotationGraceTests`; migration `AllowConcurrentRefreshSessions`.
[evidence](../agent-knowledge-evidence.md#refresh-rotations-grace-window-is-discriminated-by-the-successor-link-never-by-revoked-recently-or-by-a-timestamp)

### A local-mode host with a v2 `node.key` starts locked: tests and scripts that spawn the real host must unlock it

**Rule:** a desktop or `XE_DATA_DIR` host whose `node.key` is a v2 vault serves only the unlock pre-host until the admin password is given, so any test or script that spawns the real host against such a data directory must pass `XE_ADMIN_PASSWORD` (or `--admin-password-stdin`) for a one-shot, supply an operator secret (`XE_NODE_SQLITE_KEY`) to bypass the vault, or unlock over `auth/vault/unlock`. **Prevents:** a spawned host that reports `XE_READY` and a healthy `auth/status` but never serves the real API, so the test or shell hangs on the unlock page until the 2-minute shell start deadline. `--mcp-key` and `--setup` on a locked vault exit 5 without a password. **Authority:** ADR 0018; `Program.Vault.cs` `UnlockVaultAsync`; `EngineCliProcessTests` locked-vault flows.

### A new pre-host file read must skip the test content root

**Rule:** a read of node state before the host is built takes the same `customization is null` guard `Program.cs` passes to `NodeStartupSettings.Read` (`includeLegacyContentRoot`): a test host's content root is the developer's source directory, so a legacy-path fallback there reads whatever file a dev run left behind. **Prevents:** test hosts whose registrations depend on a stray `node-settings.json` in `XE-Local-AI-Engine.Client/`, green on CI and red on one machine. **Authority:** `NodeStartupSettings.Read`, `Program.cs` (`includeLegacyContentRoot: customization is null`).

### A host that dies at startup may be a stale key ring, not the change

**Rule:** when a dev host or opt-in runner dies at startup, read the node log for "encrypted key-ring element could not be decrypted with the current node operator secret" before suspecting the change. A checkout's `XE-Local-AI-Engine.Client/dp-keys` ring can be encrypted under an older operator secret than the current `XE-Local-AI-Engine.AppHost/.data/node.key`, and then every start stops in `NodeKeyCheckStartupService`. Run node-based lanes from a fresh worktree, or park the stale ring with `XE-Local-AI-Engine.AppHost/.data/node-sqlite` and `XE-Local-AI-Engine.Client/node-settings.json` (move them, never delete without the operator); `node.key` stays. **Prevents:** chasing a runner's bare "app resource failed to start" (exit 5) as a product regression. **Authority:** `NodeKeyCheckStartupService`, `NodeDataProtectionKeyRingDecryptor`; `scripts/run-gpu-smoke-local.sh` exit codes.

## Containers, sandbox and External Apps (host side)

### Locked runtime decisions — do not "helpfully" reintroduce

**Rule:** do not reintroduce: Docker in inference, model hosting/acquisition, embedding or image generation (ADR 0004;
Development Mode build/test/lint only), HostAgent or sandbox gRPC; the original `Docker.DotNet` package (keep
`Docker.DotNet.Enhanced`); repository-supplied container config (`devcontainer.json`, aliases); a global
`ISandboxRuntimeProvider` (select per feature).
A read-only-rootfs .NET container needs a 64 MB `/tmp` tmpfs `noexec,nosuid,nodev` (check flags as tokens). Rootless uid
mapping is proven by writing and statting host-side, never by `inspect`. **Prevents:** re-opened security decisions.
**Authority:** ADR 0004, `docs/wiki/12-security-and-privacy.md` §7. [evidence](../agent-knowledge-evidence.md#tmp-tmpfs)

### Locked sandbox never-clauses: daemon selection, credentials and containment

**Rule:** a visible but inaccessible rootful socket never silently switches product selection to a rootless daemon:
`DockerDaemonEndpointResolver` takes the per-user socket only when the system socket is absent, and only real-daemon tests
(`XE_REQUIRE_DOCKER_TESTS=1`, where an unusable daemon fails) may fall back to it. Never delete tracked credentials (shadow
them); when rewriting `.git/config` keep the preserved core/extensions keys and never recreate `origin`; never pin Docker
transport packages separately. Advertise only served containment, fail closed on unsupported confinement, never claim flat
network isolation across hosts; strip `XDG_RUNTIME_DIR` before sandboxed code. **Prevents:** silent daemon substitution and
weakened containment. **Authority:** `DockerDaemonEndpointResolverTests`, `DevelopmentWorkspaceGitConfig`, ADR 0004.

### External Apps: container-written storage, the reserved variable prefix, and the evidence home

**Rule:** storage deletion and the `XE_` prefix are in wiki 23; verify a delete by counting what is
left, never by `rm` exit 0, and assert a probe off the engine (an `XE_…` probe is refused and the catalog path falls back to
cache). Validate a whole string with `\A…\z`, not `^…$` (`$` matches before a trailing `\n`). The External Apps architecture
guard greps raw text, comments included, so a banned member name in a comment fails it. The git-ignored plan folder is not durable: put a
live round's evidence in `docs/roadmaps/<feature>-status.md`, never `git add -f`. **Prevents:** false passes and lost evidence.
**Authority:** `ExternalAppService.Pipeline.BuildStorageHelper`, `ExternalAppStateObserver`,
`docs/roadmaps/external-apps-status.md`.

### Per-node state must never be written to the install directory

**Rule:** route per-node state through `INodeDataDirectory`; install directories may be unwritable or replaced. A runtime
path under the project tree needs `.gitignore` AND exclusion from the MSBuild globs (`Compile`, `Content`, `None`,
`EmbeddedResource`) in the same change; `.gitignore` does not stop the Web SDK compiling or publishing it. Run
`git status --short` after the first local feature run. **Prevents:** workspace C# compiling into the host, generated files
shipping in installers. **Authority:** `INodeDataDirectory`.

### A Development Mode workspace inherits MSBuild config from ABOVE the node data directory

**Rule:** `Directory.Build.props`/`.targets`/`.Packages.props` walk upward, so
`DevelopmentWorkspaceProvider.EnsureBuildConfigurationBarrier` writes empty barriers one level ABOVE the workspace. Never put
them inside (they show in `git status`, change the subject hash, can land in the operator repo); a repo's own file still
wins. A green Docker test does not prove the process provider. **Prevents:** inherited build config and dirty patch
evidence. **Authority:** `DevelopmentWorkspaceProvider.EnsureBuildConfigurationBarrier`.

### The Development attempt budgets: `MaxOutputTokens` is per CALL, reported usage is per ATTEMPT

**Rule:** never compare cumulative attempt output with a per-provider-call ceiling. All roles use
`DevelopmentAttemptOutputBudget.Accept`; extend that helper for new roles. **Prevents:** false budget failures.
**Authority:** `DevelopmentAttemptOutputBudget`.

### A Development attempt failure must carry a code, like the validation gate's does

**Rule:** engine-authored failure detail throws `DevelopmentAttemptEvidenceException` (stable code, reason clamped to the
1,024-char `terminal_reason`); arbitrary exceptions stay the fixed generic sentence because they may carry model output or
host paths. Evidence persists only after checks pass; failed-attempt writes still carry into the next attempt (unresolved,
not a licence to expose raw exceptions). **Prevents:** leaking model text/paths into operator records. **Authority:**
`DevelopmentAttemptEvidenceException`.

## Host services, HTTP clients and proxy

### Serilog silently severs OTLP log export — `writeToProviders` must stay `true`

**Rule:** the flag and why are in wiki 11 ("Logging and the Data Protection key-ring at
registration time"). For "traces but no logs", first prove `OTEL_EXPORTER_OTLP_ENDPOINT` is set on the `app` process:
startup `ILogger` records always exist, so their absence means provider forwarding, not an idle workload. Keep the observable
test, not just the flag. **Prevents:** chasing an idle-workload theory. **Authority:** `SerilogProviderForwardingTests`.

### An in-process waiter wakes on a signal; its timer is only a slow safety sweep

**Rule:** a dispatcher or worker waiting for state this process writes (a queued row, a lane landing, a lease release)
waits on a signal raised after the write commits, with its interval as a fallback of seconds, never a sub-second
database poll. The permit or one-slot channel must survive the gap between the empty read and the wait. Poll only what
offers no event: a child's HTTP readiness, a sampled value, a third-party API. UI half: frontend-and-api, "Push first".
**Prevents:** an idle node querying SQLite several times a second for queued MCP runs and live graph runs.
**Authority:** `McpAgentRunWakeSignal`, `GraphWorkflowSweepWake`, `McpAgentRunOptions.PollIntervalMilliseconds`,
`GraphWorkflowOptions.DispatchIntervalMilliseconds`; operator decision 2026-10-06.

### The node is an MCP server too, and four things about it will bite you

**Rule:** mount, `McpApiKey` policy and digest storage are in wiki 12 (§3.1a, §3.2).
`[McpServerTool]` parameters are required unless they have a default (nullable is not enough), and injected parameters
precede optional ones. Keep both read/write interceptor branches for `mcp_api_key_hash`, and the status DTO without a key
field (`GeneratedMcpServerApiKeyResponse` only from generate). Outbound `mcp/servers` HTTP transports require exact
configured loopback hosts, never `IPAddress.IsLoopback`. **Prevents:** unexpected required tool args, broken table
materialization, key recovery. **Authority:** `GenerateMcpServerApiKeyEndpoint`, `McpApiKeyAuthenticationHandler`.

### The raw-model proxy's post-header failure contract: an event stream gets a terminal frame, anything else is aborted

**Rule:** once `LocalModelProxyForwarder` wrote the upstream status, no failure may become a 503. For the "child is gone"
family (`LlamaServerConnectionFailure.IsServerGone`) `PumpWithIdleDeadlineAsync` ends the exchange: an event stream gets one
OpenAI error frame (the `WriteErrorAsync` `message`/`type`/`code` shape) then `data: [DONE]` (never a `finish_reason:
"stop"`), any other body is aborted; the terminal write is guarded against a gone caller. The forwarding client carries no resilience pipeline (`AddNodeModelProxy` strips it).
**Prevents:** a forced eject surfacing as an unhandled ERROR and an SSE stream stopping mid-token. **Authority:**
`LocalModelProxyForwarderTests`, `LocalModelProxyResilienceTests`, `DeferredLlamaServerChatClientServerGoneTests`.

### Every factory client is born with Aspire's resilience pipeline; a non-idempotent or long request must strip it

**Rule:** under Aspire, `ServiceDefaults` adds `AddStandardResilienceHandler()` (retries all methods, short attempt
timeout) to every `IHttpClientFactory` client. A non-idempotent (inference/rerank POST, OAuth grant, job submit) or long
request calls `RemoveAllResilienceHandlers()` under a reasoned `#pragma warning disable EXTEXP0001` AND sets
`HttpClient.Timeout` above the per-call budget (infinite only if every call site owns a linked-token deadline). Idempotent
GETs keep it, with a comment; `new HttpClient` transports never get it. Guard tests register it BEFORE production
and assert the chain. **Prevents:** replayed POSTs, `invalid_grant`, escaping `TimeoutRejectedException`. **Authority:** `LocalModelProxyResilienceTests`,
`LlamaServerRerankerResilienceTests`, `CodexOAuthTokenEndpointResilienceTests`, `WhisperRuntimeHttpTimeoutTests`,
`StableDiffusionCppRuntimeResilienceTests`.

### Other silent-failure traps

**Rule:** native probes need a per-call timeout plus an outer deadline that degrades safely. **Prevents:** hangs and
unexpected desktop behavior.
**Authority:** `docs/wiki/11-hosting-and-deployment.md` §5.

### Measure free disk through `IFreeSpaceProbe`, never on `Path.GetPathRoot` of a path

**Rule:** use `DriveInfoFreeSpaceProbe`, which hands the nearest existing directory to `new DriveInfo(dir)` (right on Unix
and Windows). On Linux `GetPathRoot` is `/` for every path, gating on a filesystem the work never touches; a missing path
reads as 0. The probe throws `InvalidOperationException` when nothing exists at or above the path: each caller decides what
unmeasurable means. Test against a real second mount (`SeparateMountScratch`). **Prevents:** wrong-volume disk gates.
**Authority:** `Providers.Abstractions/DriveInfoFreeSpaceProbe.cs`, `DriveInfoFreeSpaceProbeTests`.

### An ArgumentOutOfRangeException from Microsoft.Data.Sqlite on constant SQL is a masked native prepare failure

**Rule:** treat it as SQLITE_MISUSE/NOMEM leaving the tail pointer unset, not as a caller bug. Read the NodeSqliteDiagnostics Warning lines (sqlite3_config_log result code, the pragma's extended result code) before changing pooling or adding locks. Tests that attach a logger match on a unique marker: the native log hook is process-global and every attached logger sees other tests' messages. **Prevents:** chasing a phantom argument bug, and flaky counts from cross-test SQLite messages. **Authority:** `NodeSqlitePragmas` transient open failure; `NodeSqliteDiagnosticsTests`; wiki 08 "Connection pragmas".

### Never open the node database on a shared cache: busy_timeout does not cover its table locks

**Rule:** the node database runs on a private cache; `AddNodeModelRuntime` strips Aspire's `Cache=Shared` through `NodeSqlitePragmas.WithPrivateCache`. Read extended code 262 as SQLITE_LOCKED_SHAREDCACHE: on a shared cache one connection's uncommitted DDL blocks every other connection's next prepare. The buffer address decides the form: an `ArgumentOutOfRangeException` from SQLitePCL, or SQLite error 6 once the driver's retry hits its command timeout. A test of it accepts both and sets a short `DefaultTimeout`. **Prevents:** the readiness probe's rolled-back `CREATE TABLE` failing an identity lookup with 500 and dropping a SignalR long-poll stream; a repro test that flakes on the 60 s form. **Authority:** `NodeSqlitePragmasTests` (`SharedCache_*`, `WithPrivateCache_*`); 2026-10-03.

### A stable-diffusion.cpp source build closes stdin on every child

**Rule:** every git and CMake child of a managed stable-diffusion.cpp source build redirects stdin and closes it at once
(the rest of the hardening: wiki 14, "Binary provisioning and managed source builds"). **Prevents:** a build step
blocking forever on an interactive prompt. **Authority:** `SourceProcessHardening.CloseStandardInput`.

## Windows

### Windows is a shipping target, and an inline `OperatingSystem.IsWindows()` is how its branches go untested

**Rule:** inject platform decisions and test both branches (`ProcessGpuVendorProbe.ProbePlatform`,
`IHardwareProbeEnvironment.IsWindows`, `NodeDataProtectionKeyRingFailClosed.ResolverFactoryFor(bool)`); prefer managed code
that deletes the branch. Windows: `find.exe` is DOS, no GNU `grep`, no Git for Windows required; use absolute in-box
PowerShell `Get-CimInstance` (`wmic` last only); derive `git diff --check` CRLF policy per path from `git ls-files --eol`;
DPAPI key resolution fails closed; Coder surveys stay `ISandboxRuntimeProvider.ListFilesAsync`/`SearchTextAsync` (never
share the managed scanner through the jail's host path; the default methods throw, never return an empty listing). A Linux test that skips elsewhere is not Windows
evidence. **Prevents:** untested Windows branches. **Authority:** the named types; Windows RC runbook.

### Measured on a real Windows 11 box, 2026-08-03 — five traps that make a Windows run lie to you

**Rule:** compare `(cmd /c "echo %PATH%").Length` with `$env:PATH.Length` (a bloated PATH reads empty to `cmd.exe`) and set
`DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0` in isolated CLI homes; check `git ls-files --eol` for editor CRLF rewrites; launch pnpm
via `cmd.exe /c` under `UseShellExecute=false`; the deprecated `package-tester-win.ps1 -SkipUpload` still needs `VPK_TOKEN`;
verify packaged policy files explicitly; use junction tests for directory reparse guards (symlink tests skip without
Developer Mode); a junction cannot prove a file-swap guard.
**Prevents:** instant timeout-test failures and false greens. **Authority:** Windows RC runbook.
[evidence](../agent-knowledge-evidence.md#windows-live-verification)

## Stale beliefs

Superseded claims; the entries above are the active rules.

| Stale belief | Current correction |
|---|---|
| Development Mode must be unrestricted because restore needs network. | A short warm sandbox restores, then the agent-facing sandbox requests deny-egress where supported |
| Ollama remains an opt-in secondary runtime. | It was removed: llama-server and the `ext:` OpenAI-compatible provider remain, and `XE_OLLAMA_*` and `Ollama:*` configuration is ignored |
| Inbound MCP key is recoverable. | Only a SHA-256 digest is stored; plaintext is returned once on generation |
| Development Docker is unbuilt / now required. | It is shipped opt-in and not the default; the container status record is canonical |
| Development Mode always fails closed without isolation. | It degrades per served capability/platform; report actual posture |
| Docker is gone entirely. | ADR 0004 permits Development Mode only; inference/AgentHome/Coder constraints remain |
| Process sandbox always shares host network. | Empty netns is used where measured available; other hosts degrade |
| Hardened containers always use uid 1000 and inspect proves mapping. | Rootless requires uid 0 to map to engine user; verify outcome host-side |
| Exit 9 at startup means the node database is broken. | It is the downgrade guard: a newer branch migrated that data directory |
| Node DB is SQLCipher and wrong key fails DB open. | SQLite is plain with per-column AEAD; DataProtection resolver enforces fail-closed key reads. |
