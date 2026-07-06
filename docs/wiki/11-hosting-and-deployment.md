# Hosting, AppHost & Deployment

> Last reviewed: 2026-06-27 · Code-grounded.

This page covers how the XE Local AI Engine node process is **hosted and shipped**: the Aspire AppHost used for local dev/integration, the shared `ServiceDefaults`, the configuration layers (`appsettings` + the user-editable `node-settings.json` + the encrypted `hf-token.enc`), the background hosted services that run inside the node, the self-contained single-file **desktop launcher** (`XE_LAUNCH_MODE=desktop`), the publish profiles + launchers used to produce a double-click distribution, and the RC-shipped cross-platform uninstaller scripts.

There are **two distinct ways the node runs**:

| Mode | Entry point | Used for | HTTP/HTTPS | DB + secrets source |
|------|-------------|----------|------------|---------------------|
| **Aspire dev / integration** | `XE-Local-AI-Engine.AppHost/AppHost.cs` orchestrates the `app` project | Local development, `aspire start`, CI | HTTPS (Kestrel default URLs) | Aspire parameters + env (`XE_NODE_SQLITE_KEY`, SQLite resource) |
| **Self-contained desktop** | `XE-Local-AI-Engine.Client` binary launched with `XE_LAUNCH_MODE=desktop` | Shipped single-file app a tester double-clicks | Plain HTTP on loopback `127.0.0.1:<auto-port>` | Per-user data dir; connection string + operator key synthesized at startup |

The two paths are deliberately kept **byte-behaviour-identical when the desktop flag is off** — every desktop branch in `Program.cs` is gated and skipped in Aspire/CI/headless runs.

---

## 1. Aspire AppHost (dev/integration)

`XE-Local-AI-Engine.AppHost/AppHost.cs` is a thin Aspire orchestration host (`IsAspireHost=true`). It references only the `Client` project and four Aspire hosting packages: `Aspire.Hosting.AppHost`, `Aspire.Hosting.Browsers`, `Aspire.Hosting.JavaScript`, `CommunityToolkit.Aspire.Hosting.Sqlite`.

What it wires (`AppHost.cs`):

- **`node-sqlite-key`** — a secret Aspire parameter (`builder.AddParameter("node-sqlite-key", secret: true)`).
- **`node-sqlite`** — a SQLite resource (`builder.AddSqlite(...)`) backed by a file under `.data/node-sqlite/node-chat.db`. In Development it also enables `WithSqliteWeb()` (a browser DB inspector).
- **`app`** — the node web server (`AddProject<XE_Local_AI_Engine_Client>("app", "https")`) with external HTTP endpoints, `ASPIRE_ENABLED=true`, `ASPNETCORE_ENVIRONMENT=Development`, the SQLite key piped in as `XE_NODE_SQLITE_KEY`, `NodeAuth__Jwt__*` issuer/audience, a `WithReference`/`WaitFor` dependency on the SQLite resource, and two health checks (`/health/live`, `/health/ready`). Extra dashboard URLs are surfaced: `/scalar`, `/openapi/local/v1/v1.json`, `/devui`.
- **`client-react`** — the Vite dev server (`AddViteApp(...)` with `WithPnpm()`), HTTPS endpoint on port **5175**, proxying to the `app` HTTPS endpoint via `VITE_PROXY_TARGET`, `WaitFor(app)`, and isolated Chromium browser logs.

> **No HostAgent, no Docker.** The old in-Aspire `HostAgent.Linux` (Docker) sandbox/runtime resource and the HostAgent gRPC client are **gone** — the AppHost contains an explicit comment to that effect. Inference and the AgentHome sandbox run as **host processes** now (see [Local Runtime & Providers](03-local-runtime-and-providers.md)). The **Ollama** provider still exists in the codebase but was **de-orchestrated** from the AppHost — `llama.cpp` is the dev runtime and there is no Ollama resource in `AppHost.cs`.

---

## 2. ServiceDefaults

`XE-Local-AI-Engine.ServiceDefaults/Extensions.cs` provides the `AddServiceDefaults()` / `ConfigureOpenTelemetry()` extension over `IHostApplicationBuilder`. The key seam:

```csharp
var aspireEnvironment = Environment.GetEnvironmentVariable("ASPIRE_ENABLED");
if (string.Equals(aspireEnvironment, "true", ...)) { /* OTEL + service discovery + resilience */ }
```

Aspire-specific concerns (OpenTelemetry metrics/traces, service discovery, standard resilience HTTP handler) are **only** turned on when `ASPIRE_ENABLED=true` (which the AppHost sets). A desktop/headless run leaves them off. OTLP export is itself gated again on `OTEL_EXPORTER_OTLP_ENDPOINT` being set — so even under Aspire, telemetry export is a no-op unless an endpoint is configured. Tracing sources include `XE.LocalAiEngine.AI.Agent`, `Microsoft.Agents.AI*`, `Microsoft.Extensions.AI*`; the metrics meter `XE.Node` is added by literal string (ServiceDefaults cannot reference the Client project).

---

## 3. The node host pipeline (`Program.cs`)

`XE-Local-AI-Engine.Client/Program.cs` builds a `WebApplication`. Startup order matters:

1. **Resolve desktop mode early** — `var isDesktop = DesktopLaunch.IsDesktopMode(args);`. If desktop, it (a) binds loopback (`builder.WebHost.UseUrls(DesktopLaunch.LoopbackBindUrl)` = `http://127.0.0.1:0`) and (b) synthesizes config via `DesktopBootstrap.EnsureLocalDataConfiguration(builder.Configuration)` **before** `AddServices` reads configuration.
2. `AddServiceDefaults()` then `AddServices(builder.Configuration)`.
3. DevUI / OpenAI-compatible Responses+Conversations services — **Development only**.
4. After `Build()`: apply node-chat + node-identity EF migrations, recover interrupted chat messages, reconcile stale scheduled runs, eagerly activate the invocation-resume registry, and register the **worker shutdown drain** on `ApplicationStopping`.
5. Pipeline: Serilog request logging (with access-token query redaction), `UseExceptionHandler` (RFC7807), **HTTPS redirect + HSTS bypassed in desktop mode**, antiforgery, static files, health checks, `LocalApiSecurityMiddleware`, routing, rate limiter, auth, FastEndpoints (route prefix `LocalApiRoutes.Prefix`), SignalR hubs (`LocalChatHub`, `SchedulerHub`, `PreviewWorkflowHub`, all `RequireAuthorization(Operator)`), Scalar/Swagger (non-Production), DevUI (Development), and `MapFallbackToFile("index.html")` for the SPA.
6. **Desktop only**: `ActivateDesktopLifecycle(app)` installs the console-close → graceful-stop triggers and the on-started browser launch.

See [API & Hubs](09-api-and-hubs.md) for endpoint/hub detail and [Security & Privacy](12-security-and-privacy.md) for the loopback / `LocalApiSecurityMiddleware` invariants.

### Background (hosted) services

Registered via `AddHostedService<>` in `XE-Local-AI-Engine.Client/ConfigureServices.cs`. These are the always-on workers inside the node process:

| Service | Role |
|---------|------|
| `HeartbeatBackgroundService` | platform WorkerHub heartbeat |
| `AutoConnectBackgroundService` | establishes/maintains the single WorkerHub connection |
| `RetentionSweeperService`, `SchedulerHistoryRetentionService`, `AgentExecutionLogRetentionService` | data retention sweeps |
| `SchedulerJobDetailReconciliationService` | reconcile Quartz scheduler job detail (see [Scheduler](06-scheduler.md)) |
| `ModelRecommendationScheduleSeeder` | seeds the model-fit recommendation schedule (see [Model-Fit](07-model-fit.md)) |
| `DefaultAgentSeeder`, `CoderAgentSeeder` | seed built-in agent definitions (see [Agent Mode](04-agent-mode.md)) |
| `ToolCallCleanupService` | clears stale tool-call state |
| `NodeChatTitleEncryptionBackfillService`, `OllamaProviderMapBackfillService` | one-shot data backfills |
| `FirstRunModelProvisioningService` | desktop first-run GGUF starter-model download |
| `LlamaCppUpdateCheckService` | periodic llama.cpp runtime update check (see [Local Runtime & Providers](03-local-runtime-and-providers.md)) |

---

## 4. Configuration layering

Configuration resolves through several layers (later wins where noted):

1. **`appsettings.json` + `appsettings.Development.json`** (in `XE-Local-AI-Engine.Client/`) — static defaults shipped with the binary.
2. **Environment / Aspire parameters** — e.g. `XE_NODE_SQLITE_KEY`, `NodeAuth__Jwt__*`, the node-sqlite connection string. In Aspire these come from `AppHost.cs`.
3. **Desktop in-memory overrides** (`DesktopBootstrap`, desktop mode only — added last so they intentionally win over `appsettings`, but only reached behind the desktop flag). See §5.
4. **`node-settings.json`** — a **user-editable, cached** settings file (not env/appsettings). `NodeSettingsStore` (`Client.Application/Services/NodeSettings/Implementation/NodeSettingsStore.cs`) reads/writes `node-settings.json` under the node data directory, with both an async and a sync (startup/DI factory) load path, tolerant JSON deserialize, and a `SemaphoreSlim` write lock. The shape is `StoredNodeSettings`. This is the runtime-editable settings store that supersedes baking values only into `appsettings`.
5. **`hf-token.enc`** — the optional Hugging Face access token, encrypted at rest. `HfTokenStore` (`Client.Application/Services/HuggingFace/HfTokenStore.cs`) uses an `IDataProtector` (`WorkerNode.HfTokenStore.v1`) to write `hf-token.enc` under the node data dir. The token is exposed **only** to the download client, **never** logged, never put in exceptions, never indexed — the same `IDataProtector` pattern as the cloud credential / worker token stores. See [Security & Privacy](12-security-and-privacy.md).

All per-node runtime artifacts (settings, encrypted credential stores, cert pins, the AgentHome workspace, the hardware-profile cache, the GGUF model cache) live under the **node data directory** (`INodeDataDirectory`), which defaults to `ContentRootPath` but is redirected to a per-user data dir in desktop mode (§5). See [Data & Persistence](08-data-and-persistence.md).

---

## 5. Self-contained single-file desktop launcher

Desktop mode turns the same binary into a double-click app. It is **opt-in** and resolved by `DesktopLaunch.IsDesktopMode(args, VelopackInstall.IsManaged())` (`Client/Hosting/DesktopLaunch.cs`, `Program.cs:49`) from any of three signals: env `XE_LAUNCH_MODE=desktop`, CLI `--desktop`, **or** running from a **Velopack-managed install** (`VelopackInstall.IsManaged()` — installer or portable flavor). The managed-install signal exists because the Velopack stub launches the bare exe with no env/arg, yet the packaged app *is* the desktop flavor (its in-app updater is desktop-only); `VelopackApp.Build().Run()` (`Program.cs:29`) establishes the locator `IsManaged()` reads. With **none** of the three signals present — Aspire, CI, and headless runs are not Velopack installs and set no env/arg — every desktop branch is skipped and behaviour is byte-identical.

```
 launcher script sets XE_LAUNCH_MODE=desktop
            │
            ▼
 Program.cs: isDesktop = true
   ├─ Kestrel binds DesktopPortStore.ResolveBindUrl(dir)   (remembered port, else 127.0.0.1:0)
   ├─ DesktopBootstrap.EnsureLocalDataConfiguration(config)
   │     • NodeData:Directory   → %LOCALAPPDATA%/XE-Local-AI-Engine  (or $XDG_DATA_HOME)
   │     • ConnectionStrings:node-sqlite → Data Source=<dir>/node.sqlite   (if absent)
   │     • operator secret      → generated once, persisted to <dir>/node.key  (if absent)
   │     • HuggingFace:ModelsDirectory → <dir>/models                  (if absent)
   │     • Agent:LocalChat:DefaultModel → FirstRunModel repo:quant     (if configured)
   │     (each key filled ONLY when not already supplied → env/Aspire always wins)
   │
   ├─ HTTPS redirect + HSTS bypassed  (loopback HTTP is safe)
   │
   └─ ActivateDesktopLifecycle(app)  (DesktopLifecycle)
         ├─ on ApplicationStarted → resolve bound URL → open default browser
         └─ console-close → graceful StopApplication() (→ llama-server child reaped)
```

### Loopback auto-port + persisted port + browser open

- The bind URL comes from `DesktopPortStore.ResolveBindUrl(dataDirectory)` (`Client/Hosting/DesktopPortStore.cs`, used at `Program.cs:58`), **not** a hard-coded `:0`. `DesktopPortStore` **remembers the last loopback port** in a `desktop-port.txt` file under the per-user data dir and re-binds it when it is still free; only when there is no remembered port (or it is taken/invalid) does it fall back to the dynamic `http://127.0.0.1:0`. This matters because a fresh OS-assigned port every launch changes the browser **origin** (scheme+host+port) and silently resets every `localStorage`-backed user preference between runs — pinning the port keeps preferences alive. The store writes via temp-file+move (no torn file), probes availability with a throwaway `TcpListener`, and is best-effort: any IO/parse failure resolves to a dynamic bind rather than throwing.
- Kestrel still binds loopback only. The concrete URL is known **post-bind**, so `LoopbackUrlResolver.Resolve` (`Client/Hosting/LoopbackUrlResolver.cs`) reads `IServerAddressesFeature.Addresses`, prefers an explicit `127.0.0.1`/`localhost` address, and **rewrites any wildcard host (`0.0.0.0`/`::`) back to `127.0.0.1`** so the browser never targets a routable interface.
- `DesktopLifecycle.OnApplicationStarted` resolves that URL and calls `BrowserLauncher.OpenBrowser` (`Client/Hosting/BrowserLauncher.cs`): `explorer <url>` on Windows, `xdg-open <url>` on Linux, **never via a shell** (`UseShellExecute = false`). Browser launch is strictly non-fatal — failure logs the URL and the server keeps serving.

### Persistent per-user data + operator key

`DesktopBootstrap` (`Client/Hosting/DesktopBootstrap.cs`) exists because a double-click launch supplies neither a DB connection string nor the operator secret. It targets `Environment.SpecialFolder.LocalApplicationData` (Windows `%LOCALAPPDATA%`, Linux `$XDG_DATA_HOME`/`~/.local/share`) so a single-file exe — whose `AppContext.BaseDirectory` is a volatile bundle-extraction temp — keeps its data across runs. The operator key is **generated once and persisted** to `node.key` (atomic temp-file write, `0600` on non-Windows); a torn/corrupt or wrong-length key **fails loudly** rather than regenerating (regenerating would brick the encrypted DB).

### No-orphan shutdown (the load-bearing invariant)

`DesktopLifecycle` (`Client/Hosting/DesktopLifecycle.cs`) fills the two OS gaps `ConsoleLifetime` doesn't cover, so a closed window drains gracefully and the singleton `LlamaServerProcessSupervisor` disposes & tree-kills its `llama-server` child (no orphan):

- **Linux `SIGHUP`** (terminal close) → a `PosixSignalRegistration` with `context.Cancel = true` → `StopApplication()`.
- **Windows `CTRL_CLOSE_EVENT` / logoff / shutdown** → a `SetConsoleCtrlHandler` callback (kept rooted so the GC can't reclaim the native delegate) that calls `StopApplication()` then **blocks up to ~4s** (`ConsoleCloseDrainBudget`, safely under Windows' ~5s force-kill window) for the drain.

The **Windows Job Object is the hard-kill safety net** regardless of whether the drain completes. `WindowsJobObjectProcessHandle` (`Providers.LlamaServer/WindowsJobObjectProcessHandle.cs`) wraps the child in a job created with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`: closing the job handle (on `TreeKill`/`Dispose`) terminates the whole process tree, so no `llama-server` survives a supervisor stop or crash. (This Win32 path is `[SupportedOSPlatform("windows")]` and the source notes it MUST be verified on real Windows 11 — the WSL build can't exercise it.) See [Local Runtime & Providers](03-local-runtime-and-providers.md) for the supervisor side.

---

## 6. Publish profiles, launchers & RC packaging

### Publish profiles

`XE-Local-AI-Engine.Client/Properties/PublishProfiles/{linux-x64,win-x64}.pubxml` — both produce a **self-contained, single-file** build:

| Property | Value | Why |
|----------|-------|-----|
| `SelfContained` | `true` | no .NET runtime install required on the target |
| `PublishSingleFile` | `true` | one binary |
| `IncludeNativeLibrariesForSelfExtract` | `true` | embed `e_sqlite3` (EF Core SQLite) + libsodium/NSec native libs; extracted to a per-user temp dir on first launch |
| `PublishTrimmed` | **`false`** | EF Core / Serilog / FastEndpoints / MEAI use heavy runtime reflection; the trimmer would silently strip reachable members. Trimming stays off until every dependency is trim-annotated and a trim smoke suite exists |
| `RuntimeIdentifier` | `linux-x64` / `win-x64` | the two shipped targets |

Publish e.g. `dotnet publish XE-Local-AI-Engine.Client -c Release -r linux-x64 -p:PublishProfile=linux-x64`. Output: `XE-Local-AI-Engine.Client/bin/Release/net10.0/<rid>/publish/`.

### Launcher scripts (`publish/`)

For a **manually unzipped RC build, the bare binary does not enter desktop mode** — a launcher must set `XE_LAUNCH_MODE=desktop` (or pass `--desktop`). (A **Velopack-managed install is the exception**: `VelopackInstall.IsManaged()` flips desktop mode on automatically, so the installer/portable flavor needs no launcher — see §5.) Tracked launchers for the manual/RC-zip path:

- `publish/linux/run-xe-local-ai-engine.sh` — sets `XE_LAUNCH_MODE=desktop`, resolves its own dir (symlink-safe), and `exec`s the binary **in the foreground** so closing the terminal delivers `SIGHUP` to the process group → graceful teardown.
- `publish/windows/run-xe-local-ai-engine.cmd` — sets `XE_LAUNCH_MODE=desktop` and runs the exe **in the current console window** (no `START`/`Start-Process`); a new/detached window would break the `CTRL_CLOSE_EVENT` → graceful-shutdown chain.

Both scripts carry an explicit **single-instance caveat**: only one instance per user-data dir (the auto-port avoids a listener collision but not SQLite contention — a second instance can corrupt the DB).

### RC bundle packaging

`publish/package-rc.sh` builds the distributable per-RID zip a tester downloads: the single-file binary + `wwwroot` SPA assets + a prominently named launcher (that sets `XE_LAUNCH_MODE=desktop`) + the matching **uninstaller script** (§7) + a `READ-ME-FIRST.txt`, plus a `.sha256` sidecar. It cross-compiles both RIDs from Linux, with `--rid` and `--skip-web` flags. The Windows bundle is built on Linux but **must be smoke-tested on real Windows 11** before tagging (`publish/TESTER-QUICKSTART.md`). The React SPA is built and copied into `wwwroot`, served by ASP.NET Core via `UseStaticFiles` + `MapFallbackToFile("index.html")` (the "C0re static-files" pattern) — see [React Client](10-react-client.md).

> **Packaging fails if the SPA build is missing.** `package-rc.sh` hard-errors (`exit 1`) when `<react>/dist/index.html` is absent — both when `--skip-web` is passed without an existing dist and when a fresh build produced none (`package-rc.sh:55,61`) — so an RC zip can never ship without its frontend (a blank-page regression). The script also scans the staged bundle for leaked runtime/state files (`node-settings.json`, any `*.enc` credential, any `*.pin`) and aborts if one is found, so per-user secrets never ride along in a distributable.

### Changelog automation & release notes

Release notes are generated from conventional-commit history rather than hand-written:

- `cliff.toml` (repo root) configures **git-cliff** to render an auto-grouped changelog from the commits between the previous release tag and HEAD. `scripts/generate-release-notes.sh` (used by CI **and** manual runs) produces a `RELEASE_NOTES.md`, which is fed to **`vpk pack --releaseNotes <file>`** to embed the notes into the Velopack package; `vpk upload github` then publishes them as the GitHub release body. A repo-root `CHANGELOG.md` is the human-readable rollup.
- **Tags are standardised on a `v` prefix** going forward (e.g. `v0.1.0-rc.1.2`); `cliff.toml`'s `tag_pattern = "v?[0-9]*"` keeps the two legacy unprefixed tags (`0.1.0-rc.1.0`/`.1`) parseable.
- **`vpk pack` (1.2.0) has no `--pre` flag** — passing it fails with `'--pre' was not matched`. Prerelease state rides on the **SemVer suffix in `--packVersion`** (`0.1.0-rc.1.0` *is* a prerelease); the GitHub-release prerelease marker is set with `--pre` only on `vpk upload github` (`.github/workflows/release.yml:267-270`).

### In-app self-update (Velopack)

The desktop app can update itself: `AddAppUpdateExtensions.cs` wires a Velopack updater fed by a GitHub **device-flow** authorization (the operator copies the device code before the browser opens). The update path is **desktop-only** (gated like every other desktop branch; see the endpoint desktop-gate tests under `XE-Local-AI-Engine.Tests/AppUpdate/`). Update-feed/channel config lives in `appsettings.AppUpdate.{main,tester}.json`. See `docs/velopack-release-install-guide.md`.

---

## 7. Installers & uninstaller

**OS-native installers (MSI / deb / rpm) are deferred.** The shipped distribution vehicle is the self-contained single-file desktop build + launcher script + RC zip. The runtime is **self-provisioning** (it downloads its own llama.cpp binary and GGUF models on demand into the per-user data dir), so a heavyweight installer is not required to get a working node.

### Uninstaller scripts (RC-shipped)

A pair of **lightweight uninstaller scripts** ships in the RC zip next to the launchers, built for the *current* app shape (Velopack desktop + portable zip):

- `publish/windows/uninstall-xe-local-ai-engine.ps1` (packaged as `Uninstall-XE-Local-AI-Engine.ps1`) — PowerShell 5.1-compatible.
- `publish/linux/uninstall-xe-local-ai-engine.sh` (packaged as `uninstall-xe-local-ai-engine.sh`) — plain POSIX `sh`.

Both always **stop** the running node process and the `llama-server` / `sd-server` child runtimes it spawned — matched **strictly** by executable path under the app's own per-user data dir, mirroring `StaleLlamaServerReaper`'s own-binaries-root discrimination so an unrelated `llama-server` (e.g. Ollama's) is never touched. They then branch:

- **Velopack-managed install detected** (a `current/` dir or `Update`/`Update.exe` helper at the data-dir root — on the default Windows layout the managed install root *is* the data dir): the script **does not delete** the tree. It delegates to Velopack (on Windows it can best-effort invoke `Update.exe --uninstall`; otherwise it points at the OS "Apps & features" uninstall) and stops. This is the safety valve that prevents brute-force-deleting a live managed install.
- **Portable / manual install** (what the RC zip actually ships — no Velopack tree present): after an **explicit confirmation** (typed `y`; `--yes`/`-Yes` skips it for automation), it deletes **only** the per-user data dir (`%LOCALAPPDATA%\XE-Local-AI-Engine` / `$XDG_DATA_HOME/XE-Local-AI-Engine`) — `node.sqlite`, `node.key`, `node-settings.json`, `hf-token.enc`, the downloaded `llama.cpp`/`stable-diffusion.cpp` binaries, `models/`, and the AgentHome workspace.

Both refuse to run elevated/as-root (a per-user data dir would resolve to the wrong profile), support `--dry-run` and `--keep-data`, and never delete anything outside that exact directory. Portable-zip users delete the unzipped app folder by hand afterward.

> **Do not resurrect the old HostAgent-era uninstaller.** A prior install-type-aware teardown existed but predates the runtime re-architecture — it referenced a WSL managed distro (`wsl --unregister xe-engine-runtime`), Docker containers/volumes/network, and `HostAgent.Windows`, **all removed** when Docker/HostAgent were torn down (see [Architecture Overview](01-architecture-overview.md)). The current scripts deliberately target **only** the per-user data dir + child runtimes.

---

## Related pages

- [Architecture Overview](01-architecture-overview.md) — where hosting sits in the whole node
- [Project Layout](02-project-layout.md) — the 19 projects, incl. AppHost & ServiceDefaults
- [Local Runtime & Providers](03-local-runtime-and-providers.md) — llama.cpp supervisor, process reaping, the Job Object's counterpart
- [Data & Persistence](08-data-and-persistence.md) — node data directory, encrypted SQLite, EF migrations
- [API & Hubs](09-api-and-hubs.md) — endpoints, SignalR hubs, OpenAPI/Scalar
- [React Client](10-react-client.md) — the SPA served from `wwwroot`
- [Security & Privacy](12-security-and-privacy.md) — loopback-only, `LocalApiSecurityMiddleware`, secret stores
- [Scheduler](06-scheduler.md), [Model-Fit](07-model-fit.md), [Agent Mode](04-agent-mode.md) — features driven by the background hosted services
