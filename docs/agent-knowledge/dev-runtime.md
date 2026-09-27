# Dev environment and local runtime

Scope: dev hosts and live rounds, host pipeline, sandbox host side, HTTP clients, Windows. Read when: scripting a dev host
or live round, or touching `Program.cs`/Kestrel/auth, HTTP clients or a Windows-only branch. llama.cpp binaries, launch
fallback and tool-call gates: [inference-runtime](inference-runtime.md).

## Host pipeline and auth

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
`RevokedAtUtc` or re-links; the grace pair is a sibling of the head. Sessions are independent chains: only logout, password
change and reset revoke all. **Prevents:** reviving a logged-out cookie, or a login signing other clients out.
**Authority:** `NodeAuthRefreshRotationGraceTests`; migration `AllowConcurrentRefreshSessions`.
[evidence](../agent-knowledge-evidence.md#refresh-rotations-grace-window-is-discriminated-by-the-successor-link-never-by-revoked-recently-or-by-a-timestamp)

## Dev host lifecycle

### The dev environment has a CUDA GPU — probe it, never infer it

**Rule:** never infer hardware or toolkit from docs; run `nvidia-smi`, `nvcc --version` and check the compiled
architecture. Under WDDM/WSL, VRAM pressure pages to host RAM instead of OOMing (slow but correct: useless for OOM tests or
consumer benchmarks); `nvidia-smi memory.free` (global) and llama.cpp `--list-devices` (process budget) can diverge by tens
of GiB: use global for admission/invalidation, the process budget for allocation, never reunify; `--query-compute-apps` may
list nothing while VRAM is held. **Prevents:** stale hardware claims and mis-sized admission. **Authority:** live probes.
[evidence](../agent-knowledge-evidence.md#wsl2-hardware-and-vram-readers)

### Headless WSL2/Linux dev boxes have no keyring

**Rule:** with no Secret Service daemon, Azure.Identity can wrap the MSAL cache persistence failure as
`AuthenticationFailedException`: walk `InnerException`, do not catch only `CredentialUnavailableException`. Such sessions
are memory-only and do not survive restart. **Prevents:** a cache failure surfacing as a sign-in error. **Authority:**
`EntraCachePersistenceFailure`, `EntraPersistenceFallbackCredential` and their tests.

### `dev-stop.sh` is the sanctioned stop path — but the leak it guards against no longer reproduces

**Rule:** the stop rule itself is in `AGENTS.md`. Do not remove `dev-stop.sh`'s owned-graph fallback cleanup (bound to
the exact AppHost/DCP graph, start-time checked against PID reuse, fail-closed on malformed Aspire output) and never restore
a global `llama-server` kill: plain `aspire stop` cleaned later retests, but the original orphan trigger is still unknown.
**Prevents:** removing live cleanup before a reproducer exists. **Authority:** `scripts/dev-stop.sh`,
`scripts/README-dev-stop.md`. [evidence](../agent-knowledge-evidence.md#aspire-teardown-retest)

### `dev-stop.sh` resolves the AppHost of your CURRENT DIRECTORY, and `pgrep -af` matches its own shell

**Rule:** `scripts/dev-stop.sh` acts on the worktree of the shell's cwd: `cd` there first; "no running instance" for a host
you know is up is a cwd check, not a stop. `pgrep -af llama-server` matches its own command line; use `pgrep -x llama-server`
or `ps -o pid,args -C llama-server`. **Prevents:** believing a host stopped while it holds the GPU and data dir; chasing a
phantom llama-server. **Authority:** `scripts/dev-stop.sh`.

### The node operator secret is seeded by dev-start.sh, not by any tracked file

**Rule:** seeding is in wiki 11 §1. An `AuthenticationTagMismatchException` usually means a
key/data mismatch (user-secret vs `.data/node.key`), not corruption: retry with `XE_NODE_OPERATOR_SECRET_FILE=<key>` before
deleting `.data/node-sqlite/`, `dp-keys/` or credential files. Log only the `dev-status.sh` allowlist (`aspire ps`/`describe`
expose tokens). **Prevents:** destroyed data and leaked tokens. **Authority:** `scripts/dev-start.sh`,
`dev_ensure_node_operator_secret`.

### A dev run provisions models through a worktree-private `HuggingFace__ModelsDirectory`

**Rule:** GGUF import is `IDesktopOnlyEndpoint`; never switch to `XE_LAUNCH_MODE=desktop`, which abandons the isolated DB.
Aspire dev mode skips `DesktopBootstrap`, so set `HuggingFace__ModelsDirectory` on the `dev-start.sh` invocation (any other
start falls back to `AppContext.BaseDirectory/models`): symlinks plus an `index.json` keyed `{fileName}:{quant}` with
`GgufRegistryRevision.ComputeV1`. Never mutate models from two hosts sharing one store at the same time: `index.json` is an
unlocked read-modify-write. **Prevents:** lost registry writes and a round on the wrong DB. **Authority:**
`scripts/dev-start.sh`, `AddNodeModelRuntimeExtensions` (models-directory default), `GgufRegistryRevision`.

### The Dev-workflow surface answers 404 unless its flag is set at `dev-start` time

**Rule:** feature flags reach the Client only as inherited environment of the `dev-start.sh` invocation
(`DevWorkflows__Enabled=true WorkSessions__Enabled=true scripts/dev-start.sh`); `AppHost.cs` forwards none, and they are read
once at startup, so a change needs a restart. `DevWorkflowOptionsValidator` fails the host when DevWorkflows is on and
WorkSessions off. Graph Workflows default on; External Apps' code default is `false` (only `appsettings.json` enables it).
Each gate is middleware ahead of `LocalApiSecurityMiddleware`, so a disabled feature and a mistyped path both answer 404.
**Prevents:** a live round burned on a "wrong route". **Authority:** `Program.cs` (`areDevWorkflowsEnabled`,
`areGraphWorkflowsEnabled`), `DevWorkflowOptionsValidator`.

### A live round in a worktree starts from a FRESH isolated DB, and a scratch host never touches the user data dir

**Rule:** never copy the main checkout's DB or keys into a worktree (a copied DB shows as `auth/setup` answering
`AlreadyInitialized`): use a fresh DB, `POST auth/setup` a throwaway admin and re-login (tokens expire ~15 min); persist
`defaultModelName` and `maxMessageRequestTimeoutSeconds=1800`. A throwaway host sets `XDG_DATA_HOME` to a scratch dir that
MIRRORS `~/.local/share` (symlink each child but `XE-Local-AI-Engine`; an empty one makes Aspire exit 7 with "`--apphost`
... does not exist"), or a GPU round sources the BYO llama-server override. `agent_execution_logs` rows terminalize
asynchronously (re-read, never poll once); `dev_workflow_*` run ids are stored UPPER-CASE and SQLite `IN` is case-sensitive.
**Prevents:** voided rounds and a rewritten user-level `installed-runtime.json`. **Authority:** `scripts/dev-start.sh`.

### an `XDG_DATA_HOME` override for a scratch host also blinds a per-user tool-version manager

**Rule:** with a scratch `XDG_DATA_HOME`, also export the tool-version manager's own data-dir variable at the real location
(for mise, `MISE_DATA_DIR=$HOME/.local/share/mise`) on EVERY command of the round: start, status, `aspire logs`, stop.
Otherwise the `aspire`/`python3` shim fails, `dev_aspire_ps_json` discards its stderr, and `dev-start.sh` only says "Could
not query Aspire state safely"; diagnose by running `aspire ps` without discarding stderr. **Prevents:** a toolchain failure
read as a stale-AppHost problem. **Authority:** `dev_aspire_ps_json`, `dev_matching_app_json` in
`scripts/dev-aspire-common.sh`; the `query_status -ne 3` branch in `scripts/dev-start.sh`.

### two hosts on one box must never share a llama-server BINARY PATH, or one host's reaper kills the other's models

**Rule:** a live round beside another running host gets its own copy of the llama-server build (e.g.
`.tmp/<round>-data/llama-bin/`) behind `XE_LLAMACPP_SERVER_PATH`; never reuse another host's managed
`source-build/active/build/bin/llama-server`. `StaleLlamaServerReaper` claims every llama-server under its managed path
(path containment, not ancestry), so a restart of host A reaps host B's models. Diagnose by grepping the OTHER host's log for
"Reaping stale". **Prevents:** a resident model vanishing mid-round. **Authority:** `StaleLlamaServerReaper`,
`LlamaServerIdleReaper.PruneExitedProcesses`.
[evidence](../agent-knowledge-evidence.md#two-hosts-on-one-box-must-never-share-a-llama-server-binary-path-or-one-hosts-reaper-kills-the-others-models)

## Containers, sandbox and External Apps (host side)

### Locked runtime decisions — do not "helpfully" reintroduce

**Rule:** do not reintroduce: Docker in inference, model hosting/acquisition, embedding or image generation (ADR 0004;
Development Mode build/test/lint only), HostAgent or sandbox gRPC; the original `Docker.DotNet` package (keep
`Docker.DotNet.Enhanced`); repository-supplied container config (`devcontainer.json`, aliases); a global
`ISandboxRuntimeProvider` (select per feature); Ollama as default (opt-in `XE_OLLAMA_RUNTIME_ENABLED`).
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
guard greps raw text, comments included, so a banned member name in a comment fails it. `Plans/` is ignored: put a live
round's evidence in `docs/roadmaps/<feature>-status.md`, never `git add -f`. **Prevents:** false passes and lost evidence.
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

**Rule:** native probes need a per-call timeout plus an outer deadline that degrades safely; desktop mode treats absent
Ollama as expected. Port persistence, SIGHUP/`CTRL_CLOSE_EVENT` shutdown, the publish shapes (Windows needs x64 ASP.NET Core
Runtime 10.0.12+) and the off-flag invariant are in wiki 11 §5-§6. **Prevents:** hangs and unexpected desktop behavior.
**Authority:** `docs/wiki/11-hosting-and-deployment.md` §5.

### Measure free disk through `IFreeSpaceProbe`, never on `Path.GetPathRoot` of a path

**Rule:** use `DriveInfoFreeSpaceProbe`, which hands the nearest existing directory to `new DriveInfo(dir)` (right on Unix
and Windows). On Linux `GetPathRoot` is `/` for every path, gating on a filesystem the work never touches; a missing path
reads as 0. The probe throws `InvalidOperationException` when nothing exists at or above the path: each caller decides what
unmeasurable means. Test against a real second mount (`SeparateMountScratch`). **Prevents:** wrong-volume disk gates.
**Authority:** `Providers.Abstractions/DriveInfoFreeSpaceProbe.cs`, `DriveInfoFreeSpaceProbeTests`.

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

## Covered elsewhere

- Host filtering runs from an `IStartupFilter`, ahead of every middleware the composition root registers —
  `docs/wiki/11-hosting-and-deployment.md` ("The container bridge listener"): any new non-loopback listener adds its host
  names to `AllowedHosts` where it is opened (`ContainerBridgePipeline.AllowBridgeHost`).
- `FallbackPolicy` challenges every routed endpoint without auth metadata, and every request that matches no endpoint —
  `docs/wiki/09-api-and-hubs.md` ("Security middleware & auth ordering").
- The backend serves the SPA — `docs/wiki/09-api-and-hubs.md` ("Static SPA fallback"),
  `docs/wiki/11-hosting-and-deployment.md` (§3). Do not add a second Node/static server.
- stable-diffusion.cpp managed source builds — `docs/wiki/14-image-generation.md` ("Binary provisioning and managed source
  builds", "Invariants a maintainer must respect"); source builds also close stdin and never rely on a default-branch checkout.

## Stale beliefs

Superseded claims; the entries above are the active rules.

| Stale belief | Current correction |
|---|---|
| Development Mode must be unrestricted because restore needs network. | A short warm sandbox restores, then the agent-facing sandbox requests deny-egress where supported |
| WSL has no GPU (or a specific older card). | Hardware changes between verifications; always query live |
| Ollama was removed. | Only Aspire auto-orchestration was removed; Ollama remains opt-in |
| `aspire stop` necessarily leaks and only 13.5+ fixes it. | Not reproduced on tested versions; `dev-stop.sh` stays sanctioned until the trigger is known |
| Inbound MCP key is recoverable. | Only a SHA-256 digest is stored; plaintext is returned once on generation |
| Development Docker is unbuilt / now required. | It is shipped opt-in and not the default; the container status record is canonical |
| Development Mode always fails closed without isolation. | It degrades per served capability/platform; report actual posture |
| Docker is gone entirely. | ADR 0004 permits Development Mode only; inference/AgentHome/Coder constraints remain |
| Process sandbox always shares host network. | Empty netns is used where measured available; other hosts degrade |
| Hardened containers always use uid 1000 and inspect proves mapping. | Rootless requires uid 0 to map to engine user; verify outcome host-side |
| Node DB is SQLCipher and wrong key fails DB open. | SQLite is plain with per-column AEAD; DataProtection resolver enforces fail-closed key reads. |
| A node cannot start with `XE_OLLAMA_RUNTIME_ENABLED=false`. | It can; the gate-off branch registers a no-op `IModelCapabilityClient` and `IOllamaModelService` |
