# Agent Knowledge Base

Hard-won rules, invariants, and traps for this repository — the things that are **not** derivable from reading the code, because they encode a bug that was already paid for once.

**Who this is for:** any agent or engineer starting work in a fresh clone. Read this before your first non-trivial change. `docs/wiki/` tells you how the system is *built*; this file tells you how it *bites*.

**Provenance:** distilled from ~135 accumulated session-memory notes spanning 2026-06 → 2026-07, each rule re-verified against the current tree at distillation time. Rules that turned out to be obsolete are recorded in [Stale beliefs](#stale-beliefs-corrected) rather than deleted — an agent that half-remembers the old rule needs to find the correction.

**Maintaining it:** when a fix encodes a rule (not just a patch), add the rule here. Keep entries in the form *imperative rule → the concrete failure it prevents → current file path*. Items marked `(unverified)` were asserted by an older note but could not be confirmed against current code — confirm before relying on them.

---

## 0. Repo orientation

This repo is **standalone**. It was previously a submodule at `~/projects/C0re/Apps/XE-Local-AI-Engine`; it now lives at `~/projects/XE-Local-AI-Engine` with its own remote (`w0rldx/XE-Local-AI-Engine`) and no pointer back to a parent.

Any instruction referencing `C0re.slnx`, `C0re.Client.React.Web`, `C0re.Tests.IntegrationTests`, or a Docker build context rooted at the C0re parent is describing the **old** layout and is wrong today. The real names:

| Thing | Actual name |
|---|---|
| Solution | `XE-Local-AI-Engine.slnx` |
| React app | `XE-Local-AI-Engine.Client.React/` |
| Backend unit tests | `XE-Local-AI-Engine.Tests`, `.Client.Persistence.Tests`, `.AI.Agent.Tests` |
| E2E | `XE-Local-AI-Engine.Tests.E2ETests` (separate lane, see §1) |
| Shared contracts | `XE-Local-AI-Engine.AI.Contracts` (owned in-repo now) |

---

## 1. Build, test, CI, packaging

### A bare `TODO` in a C# comment fails the build

SonarAnalyzer is referenced repo-wide (`Directory.Build.props:39`) with `TreatWarningsAsErrors=true` (`Directory.Build.props:11`), which escalates **S1135** to an error for any comment containing the literal token `TODO` (same class of rule catches `FIXME`/`HACK`/`XXX`).

Phrase deferred work as `// ... follow-up:` or `// Not yet implemented:`. See the live convention at `XE-Local-AI-Engine.Providers.Capabilities/Implementation/HardwareProfiler.cs:245,262`.

### Running backend tests

The three unit-test projects are **TUnit 1.58.x on Microsoft.Testing.Platform** (`<OutputType>Exe</OutputType>`). `global.json` pins `"test": {"runner": "Microsoft.Testing.Platform"}`, which bridges MTP to `dotnet test` — so plain `dotnet test` works:

```bash
dotnet test XE-Local-AI-Engine.Tests/XE-Local-AI-Engine.Tests.csproj --no-build \
  --treenode-filter "/*/*/CapabilityReporterTests/*"
```

Use `--treenode-filter`, **not** `--filter`. Class/namespace **wildcards** work (`/*/*/*EndpointTests/*`, `/*/*NodeSettings*/*/*`), and on TUnit 1.58 **alternation also works** — `/*/*/(ClassA|ClassB)/*` returns the union. Re-verified 2026-07-24 with `--list-tests`: `QuantLadderTests` alone → 9, `DesktopPortStoreTests` alone → 6, `(QuantLadderTests|DesktopPortStoreTests)` → **15**, exit 0, listing exactly both classes' tests. (`AGENTS.md` claimed alternation "silently matches zero tests"; that claim is false — if you find it still there, it is stale.) `--list-tests` honors the filter, so you can validate a filter's match count without running it. A filter that matches nothing exits **8** (`Zero tests ran`); if you meant to match, add another `/*` — the depth is off.

### GitHub Actions are DISABLED — there is no CI gate on this repo

**Read this before trusting any sentence about "CI" in this tree.** Both registered workflows are `disabled_manually`, and **no workflow run has ever succeeded**:

| Workflow | State | Run history |
|---|---|---|
| `build-and-test.yml` | `disabled_manually` | 3 runs, **3 failures**, last attempt 2026-04-20 (on `feature/agent-support`) |
| `release.yml` | `disabled_manually` | 3 runs, **3 failures**, all 2026-06-27, each dead in ~40 s |
| `e2e.yml` | **never registered as a workflow at all** | its nightly cron has never fired |

That is 6 runs, 6 failures, 0 successes, total, ever (`gh workflow list --all`, `gh run list`; verified 2026-07-24). The YAML files are still tracked and are the design of record, so read them for *intent* — but nothing in `.github/workflows/` has ever gated a merge or produced a release artifact.

Two further reasons `build-and-test.yml` could not have gated this RC even if enabled: it triggers only on `pull_request`/`push` to `develop`/`main`, the RC branch is `feature/agent-mode-foundation`, and **`main` does not exist** in this repo.

**The only real quality gate is `publish/package-tester-win.ps1`.** It runs the frontend and backend gate set itself, and every published tester RC came from it. If you want a gate enforced, it belongs in that script — adding it to a workflow file enforces nothing today.

What the dormant `build-and-test.yml` *would* do, and why it is shaped that way (still worth preserving if it is ever re-enabled): `dotnet test XE-Local-AI-Engine.slnx --configuration Release --no-build --max-parallel-test-modules 1` against the whole solution, auto-enrolling any project ending in `.Tests` so a new test project needs no workflow edit; `--max-parallel-test-modules 1` because concurrent suites time out on shared runners; a `Passed!|Failed!` output grep as a **hollow-gate guard** against the silent "zero tests enrolled" failure mode; and `TZ=Europe/Berlin` to expose non-UTC bugs. E2E sets `<IsTestingPlatformApplication>false</IsTestingPlatformApplication>` so solution-wide `dotnet test` skips it — it needs Playwright browsers + a built SPA, and is described by the unregistered `e2e.yml`.

The **architecture tests are a real gate** — but because they are ordinary tests inside `XE-Local-AI-Engine.Tests`, so they run in any full-module run and in the packaging script's backend leg. They are enforced by the test suite, not by a PR check.

### Verify against the whole module, not just the class you touched

A targeted `--treenode-filter "/*/*/<YourClass>/*"` run is great for a fast loop, but it is **not** a merge gate. A DI-wiring regression (an unguarded `INodeSettingsStore.Load()` in a singleton factory) NRE'd every **host-based** test (`WebApplicationFactory` startup) — 14 `NodeSettingsEndpointTests` plus `GetVoiceManifest_*` — and stayed red across *four* merges because every review only ran the narrow classes it changed. Before declaring a backend change merged, run the **whole `XE-Local-AI-Engine.Tests` module** at least once. Nothing else will: there is no CI gate (above), and the packaging script's solution-wide backend leg only runs at release time — by which point a four-merge-old regression is already in the RC. A DI factory that reads a store/config at construction must null-guard it (`Load()?.X`) — a test substitute's `Load()` returns null and takes the whole host down.

### The full Tests module is flaky under parallelism — verify suspects in isolation

Running the entire `XE-Local-AI-Engine.Tests` module concurrently produces a **non-deterministic** failure count (observed 1/4/5/21) from tests that mutate **process-global** state racing each other — chiefly `DesktopBootstrapTests` (`EnsureLocalDataConfiguration_*`, which set/clear `XE_NODE_SQLITE_KEY` and write key files) and `EmbeddingPlaybookRetrievalRankerTests`. They **pass in isolation** (`--treenode-filter` per class). So a red full-module run is not automatically a real failure — re-run the named suspects alone before believing it. Related trap: the module has **conflicting env premises** — `DesktopBootstrapTests` `WhenNeitherEnvSet_*` require `XE_NODE_SQLITE_KEY` **unset**, while `PlaybookRetrievalRankerRegistrationTests.AddServices_ResolvesBoth…` requires it **set** (base64 of 32 bytes, not hex) — so you **cannot** satisfy the whole module with one ambient env var. (Hardening these with `[NotInParallel]` / per-test env save-restore is a logged follow-up.)

### A concurrent `dotnet build` corrupts a test run — and the result is then neither pass nor fail

The other reason a red run may not be real. `dotnet test --no-build` loads assemblies out of `bin/`; a `dotnet build` in **any other process** rewrites those files mid-flight and the test host reports whatever it happens to trip over. Measured on this repo on 2026-07-24, with parallel agents running: one full-suite run reported `failed: 97` (of 4225), another `failed: 1`, both **clean on re-run**; an E2E run died with `FileNotFoundException: Microsoft.AspNetCore.SignalR.Client.Core, Version=10.0.9.0`. The evidence in each case was DLL mtimes falling **inside** the run window (`Client.Application.dll` 12:35:41, `XE-Local-AI-Engine.Tests.dll` 12:38:09).

**The corruption is not biased toward red.** A deliberate reproduction — `scripts/run-tests-memory-safe.sh` with a bare `dotnet build --no-incremental` fired at it 100 s in — rewrote **32** tracked files under the running test host and the batches still totalled `pass=3223 fail=0`. A contaminated run can hand you a **green** just as easily. So the verdict to reach for is not "pass" or "fail" but **"void — re-run"**; anything else eventually gets a real regression waved away as "probably contamination".

Two independent layers now exist, and they cover different things.

**Prevention — `scripts/with-build-lock.sh -- <command>`** takes an exclusive `flock` on `.tmp/build.lock` (gitignored) so cooperating shells serialize. Bounded wait (`BUILD_LOCK_TIMEOUT`, default 1800 s), exit **69** with the holder's PID and command line if it cannot acquire. Nesting is a pass-through (`XE_BUILD_LOCK_HELD`), so composed scripts do not deadlock.

> **The fd-inheritance trap.** An `flock` lives on an open **file descriptor**, and descriptors are inherited across fork/exec. `dotnet build` leaves MSBuild node-reuse daemons and `VBCSCompiler` alive for ~15 idle minutes; if they inherit the lock fd they hold the lock **while idle** and every other agent starves. This is not theoretical — measured here: `flock .tmp/x.lock dotnet build …`, then a `flock -w 3` after it exits **times out** (15 daemons still alive), whereas the same build under `with-build-lock.sh` re-acquires **instantly**. The fix is `"$@" 9>&-` — hold the lock in the wrapper shell and close the fd in the child, so nothing it spawns can hold it. The older workaround (`dotnet build-server shutdown` + `/nodeReuse:false -p:UseSharedCompilation=false`) also works but costs build speed; do not reintroduce it. `flock <file> <command>` in its plain form has this bug — do not use it around a .NET build.

**Detection — `scripts/assembly-guard.sh`** is the layer that actually matters, because you cannot force every terminal's `dotnet build` through a wrapper. It records `(size, mtime)` for every `*.dll`/`*.exe`/`*.so`/`*.deps.json`/`*.runtimeconfig.json`/apphost under the test output trees **after** the run's own build and **before** the first test process, re-checks after the last, and on any difference reports **CONTAMINATED** with the changed files and exits **75** (`EX_TEMPFAIL`) — never as test failures, never as a pass. Snapshotting after the build is what keeps a normal `build && test` sequence from tripping it. Use `assembly-guard.sh guard --test-bins -- <test command>` for new runners.

Already wired: `scripts/run-tests-memory-safe.sh`, `scripts/run-e2e-local.sh`, and every dotnet tree in `.opencode/scripts/project-validate.sh` (which now also reports `⚠ CONTAMINATED` distinctly from `✘ FAILED` and returns 75). One consequence worth knowing: `--scope full` previously ran the backend tree and the **scripts** tree concurrently, and `lint-release-scripts.sh` rebuilds `XE-Local-AI-Engine.AI.Agent.Tests/bin/Release` — i.e. it was overwriting assemblies the backend suite was loading. Those two are now serialized, so `--scope full` is slower by the length of the scripts lint. Do **not** wrap `project-validate.sh` itself in the build lock: it locks its own trees, and an outer lock makes the inner ones pass-through, putting its parallel trees back inside one critical section.

**What is still exposed:** a human (or agent) running `dotnet test` **directly**, not through one of the wired scripts, gets neither layer — no lock, no snapshot, no contamination verdict. If you invoke the runner by hand, either put it behind `assembly-guard.sh guard --test-bins -- …` or accept that a surprising failure list needs a clean re-run before you believe it.

### Never classify a cancellation from a `CancellationToken.Register` callback

`CancellationToken` callbacks run in **reverse registration order**. Whatever registers *last* runs *first*. `InvocationRunner` registered a "this was the watchdog" flag-setter at invocation registration — the earliest registration — so every later registration (the streaming agent's own, which is what releases its `await`) fired ahead of it. The released agent then reached the failure mapping before the flag had been set, and a genuine watchdog timeout was reported as `FailureCategory.Cancelled` / metric `"shutdown"`. Under full-suite load it lost that race about **one run in two**, which is why it read as a flaky test rather than as the product bug it was.

It was wrong in both directions: the same callback set the flag for *any* non-user cancellation, so when it did win the race, host shutdown and disconnect-driven `CancelAll` were reported as `Timeout` / `"watchdog"` — contradicting `NodeChatStreamService`, which maps that exact outer-token cancellation to `Cancelled`. The `"shutdown"` bucket was only ever produced by *losing* the race.

The rule: **derive the classification from state that is already observable at mapping time**, in an explicit priority order — a deliberate cancel recorded synchronously under the lock by its own requester, then the captured caller/host token, then by elimination the invocation source's own `CancelAfter`. Only the timer cannot record itself, so it is the only thing left to inference. Resolve it **once** per cancelled turn and feed both the failure category and the metric from that one value; two independent lock acquisitions reading the same flags can disagree about a single event. Also note you cannot fix this by registering earlier — you cannot register before a callback that does not exist yet.

To *test* ordering-sensitive cancellation deterministically, park the stream on an **external** gate rather than on the token (so nothing the stream registers can win), then register a callback after the runner's that releases the stream and blocks the cancel-callback loop until the failure has been reported. `CancellationTokenSource.Cancel` runs callbacks sequentially, so anything registered earlier provably runs too late to matter. The regression pin built this way fails **5/5** against the unfixed runner, versus roughly 1-in-2 for the load-dependent version.

### Code behind `#if P0_SPIKE` escapes the analyzer wall, and the constant REPLACES the defaults

`XE-Local-AI-Engine.AI.Agent.Tests/Invocation/WorkflowToolApprovalSpikeTests.cs` is wrapped in `#if P0_SPIKE`, which is defined nowhere. The gate is **load-bearing** — the class constructs a live `OllamaApiClient`, and a default build must not carry an opt-in live probe — so do not "clean it up" by deleting the `#if`. But be aware the gated code is compiled out of every normal build, so `TreatWarningsAsErrors` never sees it and it rots silently: its sibling `HandoffWorkflowSpikeTests` needed real repair for MAF 1.8.0 → 1.13.0 shape changes that a compiling build would have surfaced immediately.

The cheap guard is a build-only compile check (`dotnet build XE-Local-AI-Engine.AI.Agent.Tests -p:DefineConstants=P0_SPIKE`, assert 0/0) — it restores the analyzer wall over the gated code without ever executing the live probe.

**Trap when you do that:** `-p:DefineConstants=P0_SPIKE` **replaces** the property, it does not append. Measured on this repo:

```
default (Release)                  -> TRACE;RELEASE
-p:DefineConstants=P0_SPIKE        -> P0_SPIKE
```

`TRACE` and `RELEASE` are silently dropped. Nothing currently depends on them so it builds clean either way, but that is luck.

Appending is fiddlier than it looks. **`-p:DefineConstants='$(DefineConstants);P0_SPIKE'` does not work** — it fails with `MSBUILD : error MSB1006: Property is not valid. / Switch: P0_SPIKE`. Two reasons: MSBuild does not recursively expand a command-line property, so `$(DefineConstants)` would never resolve against the project's own value, and the bare `;` breaks switch parsing. A shell-quoted literal `"…=TRACE;RELEASE;P0_SPIKE"` fails the same way. The working form reads the project's value first and passes it back with semicolons escaped as `%3B`:

```
-p:DefineConstants='TRACE%3BRELEASE%3BP0_SPIKE'   ->  TRACE;RELEASE;P0_SPIKE
```

Verify the *effective* value with `-getProperty:DefineConstants` rather than trusting that your quoting survived the shell.

Two further traps if you automate this. `-p:BaseIntermediateOutputPath` is **global and propagates to every `ProjectReference`**, so redirecting output to keep a spike-built binary out of `bin/` makes both obj trees emit `AssemblyInfo.cs` and the build dies with ~16 `CS0579` duplicate-attribute errors — build with the constant and then rebuild without it instead. And `strings "$dll" | grep -q MARKER` under `set -o pipefail` returns **141** (SIGPIPE: `grep -q` exits on first match and kills `strings`), so it fails precisely *when the marker is present* — count matches instead of using `grep -q`.

### The full Tests module balloons to ~3.5 GB — it is a framework leak, not a fixture bug

A one-process run of `XE-Local-AI-Engine.Tests` grows **monotonically to ~3.5 GB** RSS (single-threaded, the `*EndpointTests` subset alone reaches ~3.05 GB), which thrashes a memory-tight box. Cause, confirmed by `gcroot` on a live-heap dump: `WebApplicationFactory<Program>` resolves this **top-level-statement** `Program` through `HostFactoryResolver.HostingListener`, which runs the entry point on a **dedicated background thread that blocks in `app.Run()`/`WaitForShutdownAsync`**. That thread's `ExecutionContext` holds an `AsyncLocal → HostingListener → the built IHost`, so **every host a test builds stays GC-rooted for the whole process** even though `TestingWebAppFactory` is disposed (`await using`). ~11 MB accumulates per host-based test × ~272 of them. It is **test-only** — the product builds exactly one host and is unaffected — and **not** fixable from the fixture: disposing the factory, `ExecutionContext.SuppressFlow()` around `CreateHost`, and `IHostApplicationLifetime.StopApplication()` on dispose were all measured to change nothing (`Pooling=False` shaves only ~9%). It is a `.NET 10 / Mvc.Testing 10.0.9` framework characteristic; only ~59 classes (those using `new TestingWebAppFactory`) leak.

**Memory-safe full-module run:** `scripts/run-tests-memory-safe.sh` runs the module in **fresh-process batches, one per test namespace, single-threaded**, so the leak resets between processes and every test runs exactly once (namespaces are the natural test-tree partition — no source-parsing guesswork). Peak RSS drops from ~3.1 GB (one process) to a few hundred MB per batch, and — because each batch is a fresh single-threaded process — it also sidesteps the parallel env-mutation races above. Use it for local full runs on tight memory. A roomier machine (7–16 GB) can still run the module in one process — which is what the packaging script's backend leg does. (`PAR=N` allows N-parallel per batch for speed at the cost of reintroducing possible flakes.)

**Upstream status (researched 2026-07-19):** the canonical tracker is [dotnet/aspnetcore#48047](https://github.com/dotnet/aspnetcore/issues/48047) — open since 2023, Backlog milestone, **no fix in any released or preview version**. The one attempted runtime fix ([dotnet/runtime PR#124391](https://github.com/dotnet/runtime/pull/124391), de-static-ing the `AsyncLocal<HostingListener>`) was closed **unmerged** 2026-02 as ineffective. Verified against current `HostFactoryResolver.cs` (main + release/10.0): `_currentListener.Value = this` is set on the spawned thread and **never cleared**. Nuance from the maintainer investigation worth keeping: the root only lives **while the entry-point thread stays blocked in `app.Run()`** — in principle a disposed factory stops the host and unwinds the thread; leaks that survive dispose indicate a *secondary* ExecutionContext capture into process-lifetime state (their canonical example: `HttpClientFactory`'s `ActiveHandlerTrackingEntry` timers, [dotnet/runtime#113494](https://github.com/dotnet/runtime/issues/113494)). Our measured no-effect-on-dispose therefore suggests such a secondary root exists here; a `gcroot` pass on one disposed host would name it. **Candidate structural fix (unbuilt):** stop using `WebApplicationFactory<Program>` for host-based suites — expose a directly callable `CreateApp(customize)` from `Program.cs` and build a fixture on it + `TestServer`; no entry-point resolution ⇒ no `HostingListener`, no blocked thread, no AsyncLocal root. Until that is proven with a heap dump, the fresh-process runner stays the mitigation of record.

### Test hosts leak temp files to `Path.GetTempPath()` — keep the fixture cleanup

Every `TestingWebAppFactory` host writes a fresh SQLite DB (`xe-local-ai-engine-tests-*.sqlite` + `-wal`/`-shm`/`-journal`/`.migration.lock` sidecars), a `…-nodedata-*` directory, and a `…-wwwroot-*` fixture root under the temp dir. `DisposeAsync` deletes **all** of them (the SQLite family via a filename-prefix sweep so new sidecars are caught automatically). Do not drop that cleanup: before it existed, a full run left ~3 artifacts per host build behind, which accumulated to **tens of thousands of files (~15 GB) and filled the 16 GB tmpfs `/tmp`** on this box, breaking subsequent runs mid-flight with `ENOSPC`. A **killed** run still leaks (dispose never runs) — after aborting a run, sweep with `find /tmp -maxdepth 1 -name 'xe-local-ai-engine-tests-*' -exec rm -rf {} +` (a bare shell glob hits ARG_MAX at these counts and silently no-ops).

### Layering is mechanically frozen

`XE-Local-AI-Engine.Tests/Architecture/LayerDependencyTests.cs` uses NetArchTest to freeze dependency direction (providers → Abstractions only; Contracts/Abstractions never reach back up). A structural refactor that breaks layering fails a *test*, not just review.

`.editorconfig:450` sets `dotnet_diagnostic.IDE0130.severity = error` — **namespace must match folder path**, no carve-outs remain.

### OpenAPI → hey-api is the sole REST data layer for React

The generated client at `XE-Local-AI-Engine.Client.React/src/core/api/generated/` is the only sanctioned way React talks REST. Never hand-edit generated files.

`pnpm openapi:check` regenerates and runs `git diff --exit-code` — this is the drift gate. After any backend contract change, regenerate and commit the output *with* the change.

> **Regen trap — this one is invisible.** The throwaway host used for regen **must** run with `XE_LAUNCH_MODE=desktop`, or the spec silently omits every `IDesktopOnlyEndpoint`-gated path (`app-update`, `github-auth`, image endpoints). The generated client then drops them, and you get dozens of phantom `TS2305 no exported member ...` errors. A non-desktop regen is *incomplete without saying so*. Prefer merging new/changed paths into the committed `openapi/v1.json` over overwriting it wholesale.

TanStack query keys generated by hey-api are single-element arrays `[{ _id: operationId, ... }]` — invalidate by **partial-object match**, never `.slice()`.

### Frontend lint

`pnpm run lint` = `tsc --noEmit && node scripts/CheckEventCurrentTargetInUpdaters.mjs && biome lint && stylelint`. It does **not** run `biome format`, so formatting drift won't fail lint. Do not run `biome format --write` across whole directories as a "fix" — it dirties committed files with whitespace churn unrelated to your change.

react-doctor's config must be `doctor.config.jsonc` (comments) — biome parses `.json` strictly and a `.json` with `//` comments fails lint. Its dependency rules are namespaced under the `deslop` plugin, so `ignore.rules` entries need the `deslop/` prefix or they silently no-op.

### Packaging (Velopack)

- `vpk pack` has **no `--pre` flag** — it fails with `'--pre' was not matched`. Prerelease state rides the SemVer suffix in `--packVersion`; `--pre` is only valid on `vpk upload github`. Already correctly wired in both `release.yml` and `package-tester-win.ps1` — don't "fix" it back.
- The React SPA must be built **before** `dotnet publish`. This is now a hard guard: the `GuardNodeReactBuildPresentOnPublish` MSBuild target errors a webless publish with a "run pnpm build" message, instead of shipping a blank app.
- **There are three release paths, and only one is real.** `publish/README.md` documents all three: (1) `publish/package-tester-win.ps1` — the **canonical** path, a manual Velopack build+pack+upload run on Windows, and the source of every published tester RC; (2) `publish/package-rc.sh` — a manual portable zip with no Velopack metadata and therefore no self-update; (3) `.github/workflows/release.yml` — tag-triggered CI that is **disabled and has never produced an artifact** (see the CI section above). Don't describe (3) as the primary path.
- **`package-tester-win.ps1` needs PowerShell 7+ (`pwsh`) and a non-UTC machine clock — both fail the run immediately.** It declares `#Requires -Version 7.0`: it pairs `$ErrorActionPreference = "Stop"` with native-stderr redirection to detect a `gh` 404, which **Windows PowerShell 5.1 escalates into a terminating error**. Separately, it throws before the backend tests if the machine's current UTC offset is `+00:00`. It deliberately does **not** set a time zone — `$env:TZ` is a **Unix-only** mechanism in .NET (the Windows implementation reads `kernel32!GetDynamicTimeZoneInformation` and no env var), so a `$env:TZ` line would be a silent no-op that made non-UTC coverage *look* configured. `tzutil /s` is the only real forcing mechanism and is the operator's call (global, needs elevation); `-AllowUtcTestTimeZone` accepts the reduced coverage. Full prerequisite list: `pwsh` 7+, `pnpm`, the .NET SDK, `dnx`, an **authenticated** `gh`, a non-UTC zone, plus `VPK_TOKEN` and `XE_TESTER_GITHUB_APP_CLIENT_ID`. A missing `gh` login fails *partway through* a full release build, after the whole gate suite has run.
- **`-SkipUpload` is not a "skip validation" flag.** Every build and test gate still runs; it relaxes exactly one thing, the client-ID *requirement*. A supplied ID is always validated (placeholder or non-`Iv…` → error); an absent one is tolerated only here, bakes no ID at all so the updater ships inert rather than placeholder-configured, and stamps `REHEARSAL-DO-NOT-SHIP.txt` into the publish dir — which means it rides *inside* the Portable.zip. `dotnet publish` doesn't clean that dir, so a real run deletes a stale marker.
- **Two repos, one version string.** Source lives in `w0rldx/XE-Local-AI-Engine`; tester artifacts are published to a *separate* repo, `w0rldx/XE-Local-AI-Engine.Tester-App`. The `v<version>` git tag goes on HEAD of the **source** repo; `vpk upload github --tag` then creates a same-named release on the **tester** repo. Same version, different repos, different commits — don't go looking for the tester release's tag in this repo's `git tag -l`.
- **Tester-release tag convention changed.** The 7 releases published through 2026-07-07 carry **bare** tags (`0.1.0-rc.4.1`) with `v`-prefixed release *names*. The script now passes `--tag v<version>`, so new releases are v-prefixed while the old ones stay bare. Any tooling that looks up an existing tester release must handle **both** forms.
- **The `main` update channel is intentionally inert.** `appsettings.AppUpdate.main.json` keeps its `REPLACE_*` placeholders on purpose — distribution is tester-only today, and that is an owner decision, not an oversight. By contrast `appsettings.AppUpdate.tester.json` holds a **real, intentional, non-secret** `GitHubRepositoryUrl` (`https://github.com/w0rldx/XE-Local-AI-Engine.Tester-App`) and a deliberately **empty** `GitHubAppClientId` that packaging injects. Do not "redact" the tester repository URL back to a placeholder — that breaks self-update for every installed tester build.
- Changelog: `cliff.toml` → `RELEASE_NOTES.md` → `vpk pack --releaseNotes`. Notes must exist **at pack time** — there is no notes flag on `vpk upload github`. `cliff.toml` drives `RELEASE_NOTES.md` **only**; the repo-root `CHANGELOG.md` is hand-maintained Keep-a-Changelog and is not generated, which is exactly why it drifts. `(unverified)` Re-uploading assets to an existing release does not update its body; re-releasing needs `gh release delete <ver> --cleanup-tag` or `gh release edit --notes-file`.

### The backend serves the SPA

One Kestrel process serves both API and UI: `app.UseStaticFiles()` + `app.MapFallbackToFile("index.html")` (registered after endpoint mapping) in `XE-Local-AI-Engine.Client/Program.cs`. Don't stand up a second static/node server in the bundle.

---

## 2. Dev environment & local runtime

### This WSL2 box HAS a GPU

**RTX 5090, 32 GB, CUDA toolkit 13.3, compute arch sm_120 (Blackwell), driver 610.74.** Verified live 2026-07-26 via `nvidia-smi`. Any note claiming "WSL has no GPU, GPU work can't be tested here" is **wrong** — CUDA builds, VRAM offload, and `nvidia-smi`-gated paths can all be built and live-tested on this box. cmake/gcc/ninja are present, so a from-source CUDA llama.cpp build works.

**This entry said "RTX 4080, 16 GB, sm_89 (Ada)" until 2026-07-26.** The hardware changed; the doc did not. Don't trust a remembered GPU model — read `nvidia-smi` if it matters, exactly as you would for the CUDA version.

Don't hardcode the CUDA minor version — it has already drifted (13.1 → 13.3). Read `nvcc --version` if it matters. **The same applies to the compute arch**: `CudaBuildService` detects it live, but its fallback constant `DefaultCudaArchitectures = "75;86;89"` (`:26`) has **no `120`**, so any detection failure yields a build with no native Blackwell code. If a GPU build is mysteriously slow, check what arch it was actually compiled for.

Rest of the box, for sizing assumptions: **AMD Ryzen 9 9950X3D** host, **8 processors exposed to WSL**, **~31 GiB RAM in the VM**. All three are well above the stated consumer target (≈16 GB RAM, 8–16 GB VRAM), so **local benchmark numbers over-report** — never quote them as consumer-hardware figures.

**Two GPU-behaviour traps specific to WSL2/WDDM** (both measured 2026-07-26; they apply to native Windows too, since it is the same driver model):

- **VRAM exhaustion does not OOM — it silently degrades.** WDDM demand-pages GPU memory to host RAM. With ~1.2 GB truly free, `llama-server -ngl 99` loaded and served at **161.7 tok/s** versus **698.4 tok/s** unloaded — a **4.3× slowdown with zero errors**. So (a) OOM-recovery paths cannot be exercised by VRAM pressure here, and (b) any benchmark taken while something else holds VRAM silently reports paged numbers that do not transfer. Don't deliberately drive true OOM either — WSL2 GPU OOM has been reported to kernel-panic Hyper-V and BSOD the host.
- **The two free-VRAM readers disagree under pressure.** `nvidia-smi memory.free` (→ `HardwareProfiler` → `CapacityService`) reports the true global figure; `llama-server --list-devices` (→ `LlamaListDevicesVramProbe` → profile invalidation, benchmark metrics) is built on `cudaMemGetInfo`, which on WDDM reports the **calling process's residency budget**. Measured divergence with another process holding VRAM: **492 MiB vs 29697 MiB**. See `Plans/2026-07-26-vram-reader-divergence-defect.md`.

**`nvidia-smi --query-compute-apps` returns an empty list under WSL** even when a process is holding tens of GB. Per-process VRAM attribution is unavailable here; anything relying on it is untestable on this box.

### This WSL2 box has no keyring

No Secret Service daemon (`org.freedesktop.secrets was not provided by any .service files`). MSAL/Azure.Identity token-cache persistence throws `MsalCachePersistenceException`, which **Azure.Identity re-wraps as `AuthenticationFailedException`** — so a handler catching only `CredentialUnavailableException` never sees it. When touching Entra/Azure auth here, walk the `InnerException` chain. Consequence: such sign-ins are in-memory-only on this box and don't survive restart.

### `aspire stop` is a no-op — do not trust it

Every Aspire resource runs as a DCP-owned process in its own process group, detached from the AppHost/CLI's process tree, so `aspire stop`'s tree-kill can't reach it (upstream Aspire CLI bug; fixed only in 13.5+, still preview). A killed session therefore leaves an **orphaned `llama-server` holding its port and GPU VRAM** — it runs under `setsid`, so a parent SIGKILL doesn't touch it.

**Use `scripts/dev-stop.sh`** to bring the stack fully down. It targets only same-session/AppHost-descendant processes plus `llama-server` matched under the app's own binaries root, so it won't kill an unrelated Ollama.

A startup reaper (`StaleLlamaServerReaper`, in `XE-Local-AI-Engine.Providers.LlamaServer/Implementation/`) also kills leftovers under the managed binaries root on next launch, so an orphan won't block a restart. `StaleImageServerReaper` does the same for image-gen.

> **Harness gotcha:** never `pkill -f <substring>` where the substring also matches your own command line — the `pkill` process matches itself and dies before reaching the target. Kill by PID, or from a separate shell call.

### Locked runtime decisions — do not "helpfully" reintroduce

- **Docker is gone.** No Dockerfiles remain. Deliberate epic-level decision to drop the dependency (previously used for Ollama hosting + tool sandboxing) in favour of GPU inference with a driver-only footprint.
- **HostAgent is gone.** The gRPC HostAgent client, its "RuntimeManager" UI/hub/endpoints, and the standalone Tray app were all deleted; the Windows-elevation requirement it existed for is now served by an in-app unprivileged process supervisor (Job Object tree-kill on Windows).
  - **Don't confuse this with the worker-hub `Services/Connection/*` subsystem** (`IWorkerHubConnection`) — that's the SignalR cloud-pairing path and is unrelated. Don't delete it by name-matching "connect"/"hub".
- **Tool sandboxing is a supervised process, not a container** — `ProcessSandboxRuntimeProvider` (`ProviderName="process"`), a native process under a node-scoped jail dir. It ships **enabled by default**. It has deliberately **no network isolation** — that's an accepted, documented gap, not a bug to fix by adding Docker back.
- **Ollama was NOT removed** — it is a deliberately kept, gated, opt-in *secondary* provider (`XE_OLLAMA_RUNTIME_ENABLED` / `AddOllamaRuntime` in `AddNodeModelRuntimeExtensions.cs`), with 50+ live call sites on `IOllamaModelService`. What was removed is Ollama from *Aspire's dev orchestration* (no auto-provisioned container in dev). **llama.cpp is the default local runtime** (a supervised `llama-server` process per model, no daemon). Don't strip Ollama code paths.

### llama.cpp binaries

- `LlamaCppReleasePins.PinnedTag` is only the **offline-fallback floor**. The updater resolves a live "recommended" tag from GitHub Releases first, then a cached `installed-runtime.json`, and only then this constant. If you bump it, re-verify the archive layout for that tag.
- **Upstream ships no Linux CUDA prebuilt — Windows only.** On Linux, an NVIDIA box's `GpuVariantSelector` resolves to **Vulkan**, never CUDA. For CUDA on Linux, use either the bring-your-own-binary override (`XE_LLAMACPP_SERVER_PATH` + `XE_LLAMACPP_VARIANT`) or the in-app build-from-source feature — both exist and were live-verified against this box's GPU.
- **A GPU-variant binary can see ZERO devices and silently run on the CPU — the device audit exists to catch this (AUD4-03).** On this WSL2 box the shipped Vulkan build's `llama-server --list-devices` returns an empty list (no Vulkan ICD), so inference ran 4-thread CPU while the advisor/UI sized models to 16 GB VRAM. `IRuntimeDeviceAudit` (Application) composes the hardware profile + the selected variant + `ILlamaDeviceInventoryProbe` (a cached `--list-devices` parse, per binary path+mtime) into a `RuntimeDeviceAuditState {inferenceBackend, gpuExpected, cpuFallback, reason, remediation}`. **A failed/timed-out probe is "unknown", never "no GPU"** — it must never raise a false CPU-fallback alarm. On `cpuFallback`, `GetEffectiveProfileAsync` degrades the profile to CPU-mode (`VramKnown=false`) so the advisor + capacity gate size against RAM, not phantom VRAM; a `device_fallback` metric + a Warning fire once per binary. The audit is computed lazily on first demand and cached **only when determinate** — an indeterminate probe result ("unknown") is returned uncached so the next call re-probes (latching it would keep capacity/advisor trusting phantom VRAM until restart or a forced refresh; the probe layer likewise never caches failed probes). It is a pure function of the selected binary, so it is deliberately **not** wired per-spawn (zero warm-path cost). The hardware-profile endpoint returns the raw physical profile PLUS the audit block.
- Verify GitHub asset digests via the Releases API `digest: "sha256:..."` field. There are **no `.sha256` sidecar files** — don't go looking for them.
- Archive layout is not guaranteed to match `ServerRelativePath = build/bin/llama-server`; the resolver falls back to a recursive search by executable name. This was a real shipped bug — don't hardcode the extraction path.
- GPU offload requires `--n-gpu-layers` to actually be emitted for non-CPU variants. It was once silently missing: CUDA initialized, zero layers offloaded, model quietly ran on CPU. If you touch `LlamaServerProcessSupervisor.BuildLaunchSpec`, confirm it's still wired.
- **`LlamaCppSourceBuildRequestValidation.Normalize` must stay IDEMPOTENT — it runs at three layers.** The FluentValidation request validator, `StartLlamaCppSourceBuildEndpoint`, and `LlamaCppSourceBuildService.StartAsync` each normalized the same request. For `source=official` the first pass *writes* the server-selected `Repository`, and the strict "the official repository is selected by the server" rule then rejected that value on the second pass — so **every** official-source build answered 409 `{"reason":"prerequisites"}` from the day the feature was generalized, while custom-source builds worked fine (their normalization was already idempotent). The rule is now "reject any repository that is not the canonical one" and the endpoint no longer pre-normalizes. If you add another normalization layer, `Normalize(Normalize(x)) == Normalize(x)` must hold (`LlamaCppSourceBuildCoreTests.Normalize_*AppliedTwice_IsIdempotent`).

### stable-diffusion.cpp managed source builds

- **Eject first, then build/remove.** The image-runtime activity coordinator atomically blocks mutation while an image
  job, spawn/readiness lease, or resident `sd-server` exists. The source-build start/remove endpoints deliberately
  return `409 runtime-busy`; do not bypass the gate. Use the eject action, then retry the mutation.
- **An installed managed-runtime record is authoritative, including an invalid tombstone.** Backend mismatch, path/SHA
  drift, or unsafe permissions fail closed instead of falling back to a prebuilt. Clearing the in-memory managed signal
  prevents the selector from advertising dead bytes, but recovery still requires an explicit remove/rebuild. The
  invalid-state eject/remove UI must remain reachable even when Development Mode is disabled.
- **A requested GPU backend never substitutes the CPU prebuilt.** Binary acquisition uses
  `StableDiffusionReleasePins.ResolveExact` for CUDA/Vulkan and fails if that exact OS/arch/backend asset does not exist;
  only an explicit CPU selection uses the CPU floor. This keeps the UI/runtime backend claim aligned with the bytes
  actually launched, especially for Linux CUDA where source build is the supported lane.
- **Source-build subprocesses must use the scrubbed allowlist and isolated Git home.** Inheriting the host environment
  re-enables Git URL rewriting/hooks and leaks compiler/loader/credential variables into arbitrary upstream build
  scripts and streamed logs. Keep Git prompts disabled, close stdin, and fetch explicit revisions by SHA with protocol
  hardening; a plain clone followed by checkout cannot resolve commits outside the default branch.

### Per-node state must never be written to the install directory

Route every writer of per-node state (settings, encrypted credential stores, hardware-profile cache) through **`INodeDataDirectory`** (`XE-Local-AI-Engine.Providers.Abstractions/INodeDataDirectory.cs`), which resolves to `LocalApplicationData` in desktop mode. Writing to `ContentRootPath` breaks on a self-contained desktop build where that directory may not be writable.

> **The bug this prevents, because it's a good one:** a stale dev `node-settings.json` got committed, the Web SDK auto-globbed it into the publish output as Content, and every fresh install was silently pinned to a nonexistent default model — first-run provisioning skipped its download with no error, just a permanently empty model store. A runtime-written JSON file must never be checked in *or* published as Content.

### Other silent-failure traps

- **A native process-probe with no timeout hangs provisioning forever.** `nvidia-smi`/`wmic` GPU detection once had no deadline; a hung call stalled first-run model provisioning indefinitely with nothing logged. Any shell-out to a native diagnostic needs a per-call timeout *and* an outer deadline that degrades to a safe default (CPU variant).
- **Desktop mode must treat Ollama as absent, not error-worthy.** Any Ollama call path (`/api/show`, `IOllamaApiClient`) must be provider-gated or tolerate connection-refused gracefully. Repeated source of chat failures and noisy stack traces in desktop mode, where no Ollama daemon runs.
- **The desktop loopback port is persisted on purpose** (`DesktopPortStore`, `desktop-port.txt`) so browser-origin-scoped `localStorage` prefs survive a relaunch. Don't revert to a random port per launch.
- Desktop shutdown needs explicit **SIGHUP** (Linux) and **CTRL_CLOSE_EVENT** (Windows, via `SetConsoleCtrlHandler`, blocking ~4s for graceful `ApplicationStopped`) handlers — .NET's default ConsoleLifetime covers neither, and without them console-close orphans `llama-server` again.
- Desktop publish is self-contained single-file but **explicitly not trimmed** (`PublishTrimmed=false`) — trimming breaks EF Core / Serilog / FastEndpoints / MEAI reflection wiring.
- Desktop mode is opt-in via `XE_LAUNCH_MODE=desktop`; off-flag behaviour (headless/Aspire/CI) must stay byte-identical.

---

## 3. Models, inference, retrieval

### Recommendation: walk the quant ladder, never pick one quant

Both advisor lanes rank *every* file in a repo by `QuantLadder.QualityRank` and take the **highest-quality quant at/below the ceiling that fits**, stepping further down toward `QuantLadder.FloorRank` if nothing at ceiling fits (`ModelFitRefreshService.cs:598`, `CatalogRecommendationService.cs:215`).

The old design picked one quant (Q4_K_M else `files[0]`) and dropped the *entire repo* if it didn't fit — so big/new models whose default quant didn't fit never appeared at all, even though a Q3/Q4 variant would have run fine.

### Recommendation ranking is capability-bucketed

Explore lane orders by `EstimatedBytes / 1 GiB` **bucket** → downloads → last-modified → trusted-publisher → repoId. The bucket (rather than raw bytes) stops a trivially-larger model from always beating a newer, more popular peer.

The original bug ordered by `HeadroomBytes` descending — i.e. *smallest-fitting-model-first* — which is exactly why users only ever saw old, weak, tiny models. "Biggest that fits" was the real fix.

Catalog lane orders by tier (S<A<B) → MoE-offload verdict → quant quality → release date → id. "Recommended" = quant at/above Q4_K_M **and** positive headroom; everything else eligible is "CanRun".

### The advisor is two lanes, and one may fail

`ModelFitRefreshService.BuildRecommendationsAsync` concatenates a **catalog lane** (curated `ModelCatalogEntry`, tiers S/A/B) with an **explore lane** (live HF discovery). A catalog-lane failure is caught and degrades to empty — it must never fail the whole refresh, because the explore lane still returns useful results alone.

### MoE models need `MoeFacts`, not naive VRAM math

`MemoryFitEstimator.MoeFacts(ActiveParamCount, ExpertCount, ExpertUsedCount)`; `IsMoe` is `ExpertCount > 0`. The catalog lane prefers curated `ActiveParamsB` over GGUF header expert fields (not every quantized file retains `expert_count`). With only an expert count, `DefaultExpertWeightShareFraction = 0.85` is a deliberately conservative placeholder.

Without MoE modelling, naive total-weights-vs-VRAM math rejects or mis-scores every 2026-era MoE model (Qwen3.5-35B-A3B, gpt-oss-20b, Gemma-4 26B-A4B).

### Multi-part GGUF shards are ONE model

llama.cpp splits (`<base>-00001-of-00003.gguf`) carry a full header only on the *first* split; later splits are headerless tensor continuations and are never independently loadable. `HuggingFaceGgufDiscovery.GroupShards` collapses each group into one candidate (representative = lowest split, size = sum) and drops the group if a merged single-file variant of the same quant exists.

Without this, the advisor treated a lone 0.99 GB tail shard as its own candidate and estimated a 14B model's footprint at ~1.8 GB.

### GGUF filenames from a repo are untrusted input

`GgufFilePath.IsSafeRelativePath` rejects rooted paths and any `.`/`..` segment; `ResolveContainedPath` re-checks containment immediately before any file handle opens. Defense in depth against a compromised repo returning `../../etc/evil.gguf`.

### HuggingFace API facts that bite

Use `filter=gguf`, not `library=gguf`. `gated` is a **string union** (`false`/`null`/`"manual"`/`"auto"`), not a bool. `siblings` gives filenames only — no sizes without `?blobs=true`. .NET **strips the `Authorization` header** across the cross-host CDN redirect to `us.aws.cdn.hf.co`. `mmproj*.gguf` files are vision-projector companions and are filtered out by filename everywhere — never a model candidate.

### Model kind classification

`ModelKind` = Unknown/Chat/Embedding/**Reranker**. Classification order matters: check `IsRerankerName` **before** the embedding-name check, or `bge-reranker-v2-m3` misclassifies as Embedding.

The classification cache is **digest-keyed** — a model whose Ollama-reported capabilities change across an Ollama version bump is *not* re-probed if the digest is unchanged. Stale-capability trap.

### Local-default chat resolution must stay Ollama-blind

`LocalDefaultChatModelResolver` resolves only among installed llama.cpp GGUF chat models, reading the **persisted** `IModelClassificationStore` (not a live `/api/show` probe), excluding Embedding and Reranker kinds. No installed chat model → an explicit `ModelNotInstalled` failure, never a generic "provider unreachable".

Do **not** call `IModelClassificationService.ClassifyAsync` with `Digest=null` on this hot path — it defeats the digest cache and re-probes a possibly-dead Ollama on every send.

### Reasoning ("think") has counter-intuitive Ollama semantics

For a model **lacking** Ollama's `thinking` capability:

| You send | What happens |
|---|---|
| `think:true` or any level string | **400** |
| `think:false` | accepted, but actively **suppresses** reasoning that some GGUF chat templates emit by default |
| *omit `think` entirely* | the template's built-in reasoning runs — this is what you want |

So: non-thinking model + reasoning requested (binary `"on"` **or** graded `low/medium/high`) → **omit** `think`. Reasoning off/unspecified → `think:false`. Thinking-capable models honour `false`/`low`/`medium`/`high` directly.

This logic is **intentionally duplicated across assemblies** — `InvocationAgentFactory.cs:71` and `Invocation/Orchestration/ParticipantReasoningOptions.cs:42`. A change to one must be mirrored in the other. A *new* reasoning-effort value must be added to **both factories plus `ReasoningEffortNormalizer` plus `RuntimePackageValidator` plus `RuntimePackageConfigHash`** — four normalizer sites — or it silently round-trips to null.

### Capacity gate: dispose the reservation

`ICapacityService.DecideAsync` → `CapacityDecision(Verdict, Reason, OllamaEvictionWarning, Reservation)`, verdict ∈ {`Allow`, `QueueSameModel`, `RejectInsufficient`}.

**Only a local `Allow` carries a non-null `Reservation`** (an `IDisposable` into `PendingFootprintLedger`). The caller **must** dispose it when the spawned child exits, or reserved-bytes never comes back down and later spawns wrongly reject. Cloud `Allow`, `QueueSameModel`, and all rejects carry null.

`CapacityService` no longer reads `IHardwareProfiler` directly — it consults `IRuntimeDeviceAudit.GetEffectiveProfileAsync` (AUD4-03), so on a silent CPU fallback the byte budget uses RAM, not phantom VRAM. It **warms the audit BEFORE `EnterDecisionAsync`** so the (bounded, cached) `--list-devices` probe never runs under the ledger decision gate.

### GPU-load admission gate (AUD4-06)

`IGpuModelLoadAdmission` (interface + `NoOpGpuModelLoadAdmission` floor in `Providers.Abstractions`; real `GpuModelLoadAdmission` in Application) is a **single process-wide `SemaphoreSlim(1,1)` shared by BOTH the llama-server supervisor and the stable-diffusion.cpp image supervisor**, so two `--fit` loads never read the same free-VRAM snapshot at once (the audited oversubscription: last loader spills to CPU/crashes). Rules:

- The gate is acquired **inside `SpawnCoreAsync` / `SpawnOnceAsync`, after variant/backend selection, only for a GPU variant** (`variant != Cpu` / `binary.Backend != Cpu`). CPU loads bypass it entirely. The ticket releases on ready OR any failure via a `using` scope. Because it runs under the detached-spawn/shutdown token (not the first caller's), a caller cancelling its wait never leaves the gate held. **Reuse (warm) never touches the gate — zero warm-path latency.**
- Serialization IS the re-evaluation: the next waiter's `--fit` reads fresh free VRAM once the current load is resident. Don't invent byte-level accounting beyond `PendingFootprintLedger`.
- Bounded max-wait → a typed `GpuModelLoadAdmissionTimeoutException` (never hang a chat turn); the LLM supervisor surfaces it **non-retryable** (a `catch` before the generic one in `SpawnWithRestartAsync`). Metrics: `gpu_admission_wait_ms`, `gpu_admission_timeout_total`, plus active/waiting gauges.
- **Lock ordering (no nested-gate deadlock):** the capacity decision completes and releases the ledger decision gate BEFORE the supervisor spawn acquires the load gate (the load gate is taken later, inside the detached spawn). `DecideAsync` never holds the ledger gate while awaiting the load gate. `RunExclusiveProfilingAsync` holds the per-key ensure gate across the spawn, but the load gate is still acquired inside `SpawnCoreAsync` and released before the benchmark body runs, so a long benchmark never hogs it.
- DI: each provider registers the `NoOp` floor via `TryAddSingleton`; the composition root (`AddNodeModelRuntime`) registers the real `GpuModelLoadAdmission` via plain `AddSingleton` (last-wins), so both supervisors share ONE instance. Provider-only hosts/tests get the no-op (serialization off) — the supervisor ctors take an optional trailing `IGpuModelLoadAdmission? = null` defaulting to the no-op.

### llama-server spawn invariants

- `--fit on` and any explicit **placement** flag (`-ngl`/`-ts`/`-ot`) are **mutually exclusive per spawn** — passing both disables `--fit`. `-c`, `-fa`, `-ctk`/`-ctv` are **not** placement flags: `--fit on` respects an explicit `-c` and fits ngl/batch around it (verified against b9692), so a GPU explore spawn legitimately emits `--fit on -c <policy> -fa on -ctk q8_0 -ctv q8_0` together.
- **Do not recover fitted placement from the ordinary `llama-server` startup log.** The machine-readable `LOG_TRC` fit payload is not emitted by the default server logging configuration, so the supported acquisition path is the sibling `llama-fit-params` helper through `ILlamaFitParamsRunner`. Managed source builds must retain both executables. If the helper is absent, fails, or returns an incomplete placement, keep the profile conservative and `Explored`; never freeze a partially parsed placement.
- **Never enable `--ui-mcp-proxy` on a spawned `llama-server`.** It only supports llama-server's browser UI, which this product does not serve; the existing .NET `ModelContextProtocol` client is the integration point. Enabling an experimental CORS proxy on the loopback inference process would add attack surface without adding capability. See `Plans/2026-07-26-llamacpp-native-mcp-decision.md`.
- Every spawn must pin **`--no-warmup --parallel 1`**. Otherwise the default `n_parallel=4` reserves 4× KV cache (making `--fit on` spill weights to RAM even when the model "fits"), and the default warmup run can overrun the ready-timeout, causing a kill/respawn loop.
- **Launch args now come from a central policy, not scattered defaults** (`LlamaServerLaunchPolicy` + `LlamaServerLaunchPolicyOptions`, AUD4-02/05/17). Precedence, highest first: a **frozen inference profile replayed verbatim** (never overridden) > explicit per-send/user config > role defaults. The supervisor's `BuildLaunchSpec` takes a `LlamaServerLaunchPlan` the policy produced; operator profiling passes a **null** plan (no policy interference — the supplied args are the experiment). Do not re-scatter these decisions across other classes.
  - **Every normal spawn emits `-c` now** (the audited bug: no `-c` ⇒ `n_ctx` = full model train ctx, e.g. 262144 ⇒ ~9 GB KV). In the composed application, chat chooses the largest stable dual-axis capacity tier from **65536/32768/16384/8192/4096/2048** (then caps/aligned to the model's train context); embedding/reranker use **2048**. `ChatContextTokens=16384` remains only the provider-only fallback when the application capacity resolver is not composed, not the shipping app's fixed chat window. **The CPU variant gets `-c` too** (it previously emitted none ⇒ full-train-ctx KV in RAM) — and only `-c` + `-t`/`-tb`, never GPU placement/replay args (a frozen GPU profile does not transfer to a CPU spawn).
  - **GPU KV-cache quantization is now a global default, no longer profile-only:** a GPU build emits `-fa on -ctk q8_0 -ctv q8_0` (biggest VRAM lever on 12–24 GB GPUs). It still requires flash-attn + matching K/V types and differs per backend, so there is a **one-shot fallback**: if the optimized spawn can't reach readiness, the supervisor retries ONCE with the safe config (no `-ctk/-ctv`, `-fa auto`) and — **only when the safe retry then succeeds** (so a genuinely broken model never poisons the state) — records the fallback per backend in `llama-launch-fallback.json` under the node data dir, so later spawns skip the known-bad optimized config. Frozen profiles bypass all of this (they pin their own KV/FA). **Because the runtime is therefore not guaranteed to run q8_0, the fit/admission estimators deliberately stay on the conservative f16 assumption** — the catalog lane's q8_0 KV *advisory* already surfaces the runtime benefit; assuming q8_0 in the footprint pre-flight would over-admit and risk OOM when the fallback fires.
  - **CPU thread policy (AUD4-17):** a CPU build emits `-t`/`-tb` from a physical-core estimate (logical/2 when SMT is assumed) minus a small host reserve; a GPU build emits no `-t`. Options-bound + user-overridable (`CpuThreadCount`/`CpuThreadsBatchCount`).
  - **Effective context is read back and propagated.** After readiness the supervisor reads `/props` `default_generation_settings.n_ctx` (via the resilience-free probe client) and stores it on the running process; `ILlamaServerProcessSupervisor.GetRuntimeInfo(model, role)` / `ILocalModelProvider.GetRuntimeInfoAsync` expose it. The invocation runner resolves it ONCE per turn after the warm phase and threads it into **both** budgeters — `TurnPolicy.ContextCapacityTokens` (outer) and the `num_ctx` `AdditionalProperties` key (inner, `ProviderCallBudgetChatClient`) — so both size against the real window. A per-send `num_ctx` override still wins; an unknown window (cloud/Ollama/not-yet-started) falls back to the app default (8192). The `LocalModelDetailsResponse.EffectiveContextTokens` field surfaces it for the chat context-usage meter.
- A **reranker runs its own dedicated llama-server** (`--rerank --pooling rank`), distinct from an embedding server (`--embeddings --pooling mean`) for the same model — they cannot share a process. `IRerankerClient` degrades to null (falling back to RRF order) on any failure, and must match scores by the **returned** index, not request order.

### llama-server readiness, load lifetime, and eject (Audit-4)

- **Readiness is separated from the stream-idle watchdog.** A cold model load must happen BEFORE the streaming watchdog is armed, or a big model gets killed at the (shorter) `StreamIdleTimeoutSeconds` and can never load through chat. The invocation runner warms a local (llama.cpp) model via `ILocalModelProvider.WarmModelAsync` (`InvocationRunner.PrepareLocalRuntimeAsync`) — reporting `InvocationRuntimePhase` (PreparingRuntime → LoadingModel → Generating) — and only then streams. Cloud/Ollama warm is a no-op. A new `InvocationState.RuntimePhase` field rides both `Clone` methods (the two-Clone gotcha in §5 applies).
- **The readiness timeout is size-aware, not a constant.** The old hardcoded 120 s `ReadinessTimeout` is gone; the supervisor derives the deadline from on-disk model size via `LlamaServerSupervisorOptions.ResolveReadinessTimeout(bytes)` (base + per-GiB extension above a threshold, capped). A readiness **timeout** (process alive but slow) is retried at most `MaxReadinessTimeoutRetries` (default 1) — NOT `MaxRestartAttempts` — so a slow model no longer thrashes ~6 min of kill/reload. A process **exit** during load stays non-retryable (deterministic crash).
- **The spawn/load is DETACHED from the first caller's token.** `EnsureRunningAsync` runs the spawn as a shared, per-key detached task (`_inflightSpawns`) under the shutdown token; a caller cancelling only abandons its `WaitAsync`, the load continues and warms the model for the next send. Single-flight (one spawn per key for a concurrent burst) is preserved.
- **Operator eject is graceful by default (`EjectAsync(model, role, force, ct)`).** It marks the process evicting (no new leases), drains in-flight inference for a bounded `EjectDrainTimeout`, then tears down — returning `LlamaServerEjectOutcome` {`Ejected` | `TimedOutStillBusy` | `ForcedWhileBusy` | `NotRunning`}. A graceful eject that can't drain does **not** kill (returns `TimedOutStillBusy`); `force:true` kills anyway and marks the run operator-ejected. The evicting mark is guaranteed cleared on every exit where teardown did not complete — including an eject HTTP request **cancelled mid-drain** (a stuck mark would refuse every future lease forever). `TryAcquireInferenceLease` returns a tri-state `LlamaServerLeaseAcquisition`: granted lease / refused-evicting / refused-absent. The chat client (`DeferredLlamaServerChatClient`) holds a lease per request; a lease refused **because an eject is draining** fails the send up front as `LlamaServerModelEjectedException` (proceeding leaseless would slip under the drain, be killed mid-flight by the teardown, and self-heal-respawn the just-ejected model — leaseless proceed is only for a genuinely absent/exited process), and a force-eject drop mid-request throws the same typed exception → classified `FailureCategory.Cancelled` (truthful "ejected by operator" message), not a generic provider failure. The idle reaper and the cap-admission LRU eviction **never kill a process holding an active inference lease**, even past the idle TTL (`LastUsedUtc` is stamped per ensure/reuse, not per token, so a long generation looks idle). `EvictAsync` remains the immediate (non-draining) teardown for internal callers (profiling, provider unload). The old ModelFit eject was an unconditional tree-kill; its endpoint doc/copy was corrected.
- **Provider unload is all-role, while warm/runtime-info probes are deliberately chat-only.** `LlamaServerLocalModelProvider.UnloadModelAsync` must call `ILlamaServerProcessSupervisor.EvictAllRolesAsync`; the supervisor's authoritative helper iterates `Enum.GetValues<ModelRole>()`, so a future role cannot silently survive provider-wide teardown. Do not replace chat-only warm/runtime-info behavior with all-role iteration: those calls describe the interactive chat runtime, not the provider-wide teardown contract. The full role-decision inventory is in [`audits/2026-07-26-model-role-audit.md`](audits/2026-07-26-model-role-audit.md).
- **The readiness/liveness probe uses a dedicated, resilience-free HttpClient** (`new HttpClient`, not the app's `IHttpClientFactory`) with a ~1 s per-attempt bound. Routing it through the factory inherited the standard resilience handler's exponential retries, stretching one logical probe to ~10 s and detecting readiness up to ~5 s late. Don't re-route the probe through the shared client.

### Context allocation is a stable process decision, not a live-memory sample

- **Keep four context values distinct:** (1) the process allocation launched as `-c`; (2) the per-request budget/limit, which may reduce but cannot enlarge an already-running process; (3) the model train-context ceiling; and (4) a frozen profile's replay override. Do not substitute one for another or reintroduce a shared compile-time default.
- **Select automatic process tiers from stable dual-axis capacity:** `ProcessContextAllocation` carries both GPU and RAM footprints and covers CPU, GPU-resident, hybrid, and MoE expert-offload placement. Global-free VRAM answers whether the machine can admit work or whether a profile should be invalidated; the runtime's process-budget VRAM answers what this process can allocate. Never average or alias the two.
- **Preserve precedence:** frozen profile replay > deterministic operator override > automatic hardware tier. A frozen or deterministic allocation never shrinks itself after failure.
- **Only a classified startup OOM may down-tier automatically, and at most twice per allocation identity.** Readiness timeouts, capability/KV failures, and other startup errors are not OOM evidence. The automatic hardware tier may step down; frozen-profile and deterministic-override allocations fail without silent mutation.
- **Launch-policy fingerprint v2 describes stable launch identity, not current load.** It includes the context-allocation policy, runtime/model/role/backend identity, total and per-sequence context semantics, placement-affecting args, reserves, and conservative KV assumptions. Live global-free VRAM and other transient samples must not enter the fingerprint; they belong to admission, invalidation, and benchmark evidence.

### Knowledge base / RAG

- **FK cascades don't fire.** The node SQLite connection has no `PRAGMA foreign_keys=ON`, so `ON DELETE CASCADE` never runs. Delete/reindex paths must issue explicit ordered raw-SQL deletes (vectors → chunks [fires the FTS sync trigger] → sections → document → file). An EF-graph delete in a test will **false-pass** without exercising this.
- **Vector search is managed brute-force cosine**, not sqlite-vec — bench-confirmed faster at every corpus size up to 100k rows. sqlite-vec was deliberately deferred (its `vec0` is brute-force with no default ANN index anyway).
- **Embedding-model resolution must be shared.** `EmbeddingModelResolver` resolves configured-exact → first embedding-named installed GGUF → configured-name fallback. The **same resolved instance** must feed both ingest and query, or the vectors are incomparable. Staleness/mass-reset logic must gate on `EmbeddingModelResolution.IsConfident` — resolving during a transient provider outage must never mass-reset a healthy corpus.
- **Knowledge vector identity is model + transform + width, not the model name.** A confidently resolved `nomic-embed-text-v1.5` uses the shared `layernorm-population-eps1e-5-truncate-l2:v1` transform at width 512 by default; ingestion and query must both call `KnowledgeEmbeddingVectorPolicy` after provider generation. Documents, vector rows, search filters, catalog staleness, and the RAM query cache all key on the exact canonical identity, with `dim` as defense in depth. Other models remain native. Operational rollback is: set `KnowledgeBase:EmbeddingVectorMode=Native`, run the normal full-corpus reindex, verify no stale documents remain, **then** deploy an older binary. Rolling the binary back first leaves 512-wide rows that the old model-name-only code can misinterpret.
- **Hybrid retrieval** = FTS5 BM25 (per-token OR-quoted, not literal phrase match) ∪ vector cosine, fused by Reciprocal Rank Fusion (k=60), then optionally reranked. Every failure degrades to untouched RRF order.
- **Never persist query embeddings derived from encrypted/sensitive source text.** Playbook/KB query caches are RAM-only, bounded, and keyed by the canonical vector identity plus a query hash so native/512 or cross-model vectors can never mix.

---

## 4. Agent Mode, MAF, sandbox, cloud providers

### Sandbox: the two guards are mandatory *together*

`ResolveJailPath` (`ProcessSandboxRuntimeProvider.cs:518`) canonicalizes via `Path.GetFullPath` + prefix check — which collapses `..` but does **not** resolve symlinks. A path under the jail can still traverse a symlink planted by a command that ran with the jail as CWD. **Every read/write leg must also pass `EnsureNoSymlinkComponentsUnderJail`** (~:555) before opening.

Host-file reads use a **no-follow open**: `OpenNoFollow` (~:714) P/Invokes raw `open()` with `O_RDONLY|O_NOFOLLOW|O_CLOEXEC`. Do **not** cast `O_NOFOLLOW` to `FileOptions` and pass it to `File.OpenHandle` — the runtime validates the enum and throws `ArgumentOutOfRangeException` on *every* file, not just symlinks. On `fd < 0`, check `Marshal.GetLastPInvokeError()` (errno 40 = ELOOP = symlink leaf).

The **byte-cap re-check must cover post-sizing growth**: size a buffer from `RandomAccess.GetLength`, read exactly that many bytes, then probe one more byte at `length`. A >0 probe means the file grew after sizing — block the whole copy (return null). Never emit a torn or truncated copy.

**Known gap (accepted, Low):** coder-mode's `ExecuteAsync` (backing `list_files`/`search_text` via allow-listed `find`/`grep`) is *not* independently jailed — it relies on `WorkingDirectory` confinement, which does not re-apply the symlink guard. Not model-exploitable today (coder can't create symlinks; host→sandbox copy rejects reparse points), but it widens the moment a write-capable sandbox tool ships.

### Sub-agent spawn: depth cap is structural first

A spawned child is built with `spawn_subagent` **unconditionally stripped from its tool set** (`SubAgentSpawnService.ResolveBindingAsync` → `CurateChildTools`, `SubAgentSpawnService.cs`). The runtime guard (`SpawnContext.Current is { Depth: >= 1 } → reject`) is defense-in-depth for a misconfiguration, **not** the primary control. Never rely on the runtime check alone.

**A child is also stripped of every approval-required tool, not just `spawn_subagent`** (GPTAUD-01). `CurateChildTools` drops any `ApprovalRequiredAIFunction` from the resolved child set (and logs a Warning naming them). A child runs as an agent-as-tool via `AsAIFunction`, which invokes with **no** per-run options and has **no** HITL/approval round-trip — an approval-gated tool would surface a `ToolApprovalRequestContent` the child can never answer, silently failing every call to it. The tools are **dropped, never unwrapped to auto-execute** — unwrapping would bypass the tighten-only approval control the offer/registry/MCP policy asserted. (This bites when an MCP tool is in the child's `AllowedToolNames`: MCP ships approval-required-by-default, so it resolves wrapped and is then dropped.)

**The child must get its model bound via `ChatOptions.ModelId` at construction.** `RuntimeChatClient` routes the shared `IChatClient` to a provider **per send** off `ChatOptions.ModelId`; a null ModelId silently falls back to the node default. This was a real live bug — the child fell back to Ollama instead of its bound llama.cpp model.

**A profile-bound child consumes the COMPLETE resolved runtime as one unit** — not just its tools. `ResolveBindingAsync` resolves the `ResolvedAgentRuntime` **once** and threads `ResolvedSystemPrompt` (scaffold + persona + injected playbook memory), `ReasoningEffort`, `Skills`, **and** the curated tools into the child. Reading only `AllowedTools` was MED-002: a saved sub-agent silently ran on raw `definition.Instructions` with no scaffold/reasoning/skills — *less* grounding than the anonymous model-id-only path, which already composes the base scaffold. Because a spawned agent-as-tool never receives per-run `RunOptions` (`AsAIFunction` invokes with none), reasoning + skills must be baked into the agent at **construction** — exactly the orchestration-participant shape: reasoning rides `ChatOptions.AdditionalProperties` via **`ParticipantReasoningOptions.Build(effort, supportsThinking)`** (gated on the child model's OWN thinking capability, resolved through `IModelCapabilityResolver` — a non-thinking Ollama model 400s on `think:true`/level), and skills ride an `AgentSkillsProvider` on `ChatClientAgentOptions.AIContextProviders`. Playbook memory **injection** already lives inside `ResolvedSystemPrompt`, so the child inherits it automatically — that parity is desired.

**Deliberately restricted for a child (intentional, not oversight):** (1) `spawn_subagent` is stripped (the structural depth cap above); (2) post-run adaptive-memory **EXTRACTION** is disabled — a child mines no new playbook candidates (injection still rides its resolved prompt). Both are by design; do not "fix" them into parity. The anonymous model-id-only spawn path also stays as-is: raw request instructions, tool-less, no reasoning/skills.

`AIAgent.AsAIFunction()` is GA and **does** forward the outer `CancellationToken` — no linked-CTS workaround needed. Its generated tool input parameter is named **`"query"`**, not `"task"`.

### Tool-approval policy: the enforcement seams are plural

The tighten-only node policy (`IToolApprovalPolicy`, OPP-03 — see §6) is applied by re-projecting each offered tool's `RequiresApproval` through the policy. That projection happens at **more than one place**, and a new tool-offer path that skips it is a **silent policy bypass** (the offer flows to `InvocationToolResolver` with metadata, so it does not fail closed — it just runs at catalog defaults). The known seams that all must apply the policy: `AgentDefinitionResolver.ProjectAllowedTools` (bound-agent path **and** the mode-off Default Assistant early-return), `OrchestrationResolver.ProjectAllowedTools` (participants), and the **`resolved == null` fallback** in `NodeChatStreamService`/`NodeChatRegenerationService` (reached when a conversation's bound agent was deleted — this one was a real bypass caught only in security review). If you add another path that builds a tool offer, it must run the same compose or it is a hole. What you must **not** touch: the structural floor is the **registry pre-wrap** (`McpServerConnectionManager`, `ClientLocalToolRegistry` wrap high-risk tools as `ApprovalRequiredAIFunction` at registration) + the add-only `InvocationToolResolver`; the policy composes *on top* (OR), it never unwraps. A per-agent/policy `false` is a **no-op** by design. When no node policy is configured, the `PermissiveToolApprovalPolicy` floor is **identity**, so offers + config hash stay byte-identical (guarded by `LocalToolOfferProviderTests.ProductionCatalog_EveryOfferedTool_DeclaresANonUnknownCategory` — a production tool must never be `Unknown`, since Unknown now fails closed).

The approval **audit** row (`AgentExecutionLogRecordKind.ApprovalDecision=2`) reuses `agent_execution_logs` by **overloading** columns (toolName→`model_name`, category→`config_hash`, decision→`terminal_status`, source→`provider`). This is safe **only because every read/aggregate over that table filters by `record_kind`** — `SummarizeTokenUsageAsync`/`ListRunEnvelopes` (kind 1), `ListByAgent` (kind 0). Any new SELECT over `agent_execution_logs` that omits a `record_kind` filter will mis-read these overloaded columns (esp. `provider`, which for kind-1 means the token-usage provider). Always kind-filter.

### MAF traps

- **`ChatClientAgentOptions` has NO `Instructions` property** (MAF 1.8 → 1.13). Instructions live on `ChatOptions.Instructions`. Any snippet setting `Instructions=` on `ChatClientAgentOptions` is wrong.
- **Positional ctor order is `(chatClient, instructions, name, description, ...)`** — this has been gotten backwards in-tree at least once (name/instructions swapped). Contract: instructions are delivered **exactly once** via a leading `System`-role seed message, and the `instructions` argument itself must be null on all paths, or you double-send. Because `AgentSkillsProvider` prepends its own preamble, a "no double-send" test must assert **containment**, not exact/null equality.
- MAF delivers `ChatOptions.Instructions` at the raw `IChatClient` boundary via `options.Instructions`, **not** as an injected System message — a fake `IChatClient` in a test must check both places.
- Approval types are `ToolApprovalRequestContent`/`ToolApprovalResponseContent` in the pinned Extensions.AI. `FunctionApprovalRequestContent` (shown in current-looking official docs) **does not exist** at this pin. Gating comes from marking a tool `ApprovalRequiredAIFunction` — **not** from the `UseToolApproval` middleware wrapper (a plain tool under that middleware runs un-gated).
- Agent Skills (`AgentSkillsProvider`) are `[Experimental]` → `MAAI001`, needs scoped pragma suppression.
- Tool-call argument telemetry must **never** log raw arguments — redact to length + SHA-256 12-hex prefix. (An audit found `tool.arguments` leaking into spans at Information level.)

### Cloud providers

**Codex (ChatGPT-subscription OAuth):**
- The backend **rejects `system`-role messages outright** (`{"detail":"System messages are not allowed"}`). `CodexStoreDisabledChatClient.PrepareCodexRequest` strips every System message and folds its text into `ChatOptions.Instructions` — **Codex-side only**; local/Ollama keeps System messages.
- **A local model id must never reach the Codex wire.** The general send path sets `ChatOptions.ModelId` to the active local model, and MEAI's Responses adapter prefers the per-call ModelId over the construction-time one — so a leaked local name gets sent to Codex and 400s. `ApplyStoreDisabled` therefore **unconditionally overwrites** `result.ModelId` with the resolved Codex id (and clears `MaxOutputTokens`). Replicate this pattern for any future cloud wrapper: never trust an inbound ModelId on a boundary that pins a different provider's model set.
- Reasoning effort must **not** ride the Ollama `think` key (`minimal`/`xhigh` 400 Ollama) — it rides a Codex-only `AdditionalProperties["codex_reasoning_effort"]` side channel, because the `ChatOptions` factory is provider-blind. The OpenAI SDK has **no `XHigh`** — UI "Highest" silently degrades to `High` on the wire.
- `store=false` is a **privacy invariant enforced unconditionally** at the wrapper boundary, regardless of caller options. With tool-calling + reasoning + store=false, encrypted reasoning and prior function-call items must be replayed verbatim each round-trip — MEAI's `OpenAIResponsesChatClient` does this automatically for content whose `RawRepresentation is ResponseItem`. Don't hand-roll the replay.

**Azure Foundry / Entra:**
- **Routing is per-request and model-driven**, not connection-presence-driven. `RuntimeChatClient.ResolveActiveClient()` must receive the per-send `ChatOptions.ModelId`. Precedence: explicit Azure-deployment match > explicit Codex session > null/blank (node default) > unknown id (routes local). Getting this wrong sent Azure picks to the local llama-server with "model not installed". Any new cloud provider must participate in this same per-send resolution, not a startup-computed singleton.
- The host allowlist blocks **APIM gateways by default** (`AllowedHostSuffixes` = `.openai.azure.com` / `.services.ai.azure.com` / `.cognitiveservices.azure.com`) — an APIM host is rejected before any auth logic runs unless an operator adds `AdditionalAllowedHostSuffixes`.
- **The Azure OpenAI SDK silently overwrites the `Authorization` header** on the v1 surface, even with a per-call Entra bearer policy — the ctor credential's `ApiKeyAuthenticationPolicy` sits in a fixed per-try slot that runs *after* all per-call policies. Symptom: a cryptic `IDX12741 "JWT must have three segments"`. Fix: pass the bearer policy as the `OpenAIClient(AuthenticationPolicy, options)` **constructor argument**, not `AddPolicy`.
  - Lesson worth generalizing: a construction/DI unit test **cannot** catch pipeline-order header clobbering. Any change to pipeline policies needs an integration test that fires the assembled pipeline through a request-capturing handler and asserts the final wire headers.
- **Client-credentials (app-only) tokens carry `roles`, not `scp`** — a gateway `validate-jwt` policy checking `scp` rejects them even though auth "succeeded" locally. Fix the gateway policy or use the delegated auth-code flow (`ConfidentialClientApplication`, **not** `Azure.Identity.AuthorizationCodeCredential` — that type has no PKCE and no persistable `AuthenticationRecord`).
- **The real Azure auth error is not in `AuthenticationFailedException.Message`** (that's just "ClientSecretCredential authentication failed: ") — the AADSTS code is in the inner `MsalServiceException.Message`. Any sanitizer must walk the InnerException chain.

**MCP HTTP transport** requires `IsHttpScheme` (http/https only — blocks `ftp://127.0.0.1`, `file://…` even on a loopback host) **and** `IsLoopbackHost` (exact-string match against `McpOptions.HttpLoopbackHosts`) at connect time, as defense in depth over the CRUD-layer validation. Do **not** swap the exact allowlist for `IPAddress.IsLoopback` — the strict form was kept deliberately after proving no bypass via metadata-IP, userinfo tricks, DNS-rebinding suffixes, or expanded IPv6.

### SignalR does not replay to late joiners

This is load-bearing anywhere a hub streams run/tool events. The concrete bug: a service published `RunStarted` and began draining events to a group **before** the HTTP response carrying the runId (which the client needs in order to `Subscribe`) returned — so a fast run's events all hit an empty group, and the client saw zero output with a stuck-enabled Cancel button.

**Pattern to replicate for any push hub:** give every event a per-run monotonic `Seq`; keep a bounded per-run event buffer **outside** the live-run dictionary (it must outlive run completion, with a short eviction sweep after a replay-retention window, e.g. 60 s); on `Subscribe`, join the group **then** replay the buffer to the caller; dedupe client-side via a high-water mark + gap set so buffered and live events never double-apply.

**Related:** if "cancel" only cancels the CTS and relies on the normal drain path to publish the terminal event, a model call that never unwinds leaves the UI stuck "running" forever. **Publish the terminal event directly from the cancel handler**; the drain path should just dispose.

### Chat message status is a table-enforced state machine

Two independent writers race one assistant row: the HTTP cancel endpoint and the pump's terminalize/flush. The allowed source statuses per writer intent live in **one** table, `NodeChatMessageTransitions` (`Services/Chat/NodeChatMessageTransitions.cs`), and every correlated UPDATE enforces its set **atomically** via an `AND status IN (...)` predicate — never a read-then-write (`NodeChatMessageCommands.UpdateCorrelatedMessageAsync`). Terminal rows are otherwise immutable, with **one deliberate whitelist**: the pump's true-outcome terminalize (completed/failed/cancelled) may fire from a `Cancelled` source, so an authoritative completion supersedes an optimistic HTTP-cancel marker and a cancel-terminalize over a cancelled row is the idempotent final-content write. `Interrupted` is **not** whitelisted — it can never downgrade a user `Cancelled`. Rules:

- **Cancel / flush / recovery** may fire only from the non-terminal set (`pending`/`queued`/`streaming`). A late flush or cancel against a terminal row is an atomic no-op, not a rewrite.
- **The queued/streaming lifecycle marks are guarded too.** Queued may fire only from `pending`; streaming only from `pending` (platform path — the worker coordinator marks streaming straight off the placeholder, no queued step) or `queued` (local send/regen). This closes the reported race: a cancel landing on the `pending` placeholder **before** the cancellation registration exists can no longer be overwritten back to `queued`/`streaming`. The stream/regen services **check the mark result** — a rejected mark returns the true terminal row, so they emit that terminal SSE and abort instead of running the model into a finalized message (`NodeChatStreamService`/`NodeChatRegenerationService`, both mark sites).
- **Terminalize** derives its allowed sources from the *target* status: non-terminal for `Interrupted`, non-terminal **plus** `Cancelled` for completed/failed/cancelled. `completed`/`failed`/`interrupted` are never a legal source, so a second terminalize is a no-op.
- **The run envelope + the single SSE terminal are built from the PERSISTED winning status** (`persisted.Status`), never the requested one — because the guard may have rejected the write. The ledger can therefore never disagree with the row. Don't "simplify" the pump back to using the requested `terminalStatus`.

---

## 5. Frontend, chat UX, API boundary

### Chat rendering contract

An assistant turn renders as **one ordered `parts[]` array** (reasoning ↔ tool ↔ reasoning → answer), not fixed sections. Do not flatten reasoning into a single string — you lose the wire `sequence` and tool calls render out of order. Renderer: `src/features/chat/components/MessageParts.tsx`, fed by a pure `buildMessageParts()` shared by both the live streaming reducer and the reload-from-DB mapping.

Tool cards use **one** state-driven component (`ToolCallCard.tsx`) for requesting/waiting/received/failed. Don't reintroduce a separate "streaming" vs "final" tool component — that duality was deliberately retired.

Tool args/results render via the shared `CodeBlock` component (`src/core/ui/components/CodeBlock/`) — reuse it rather than adding another highlighter.

Turn metadata (agent name, reasoning effort, tokens/sec, tool parts, duration) rides the existing `metadata_json` blob — additive fields need **no DB migration**. Per-turn setting precedence is `request.value ?? conversation.value ?? default`.

### Error surfacing

A failed assistant turn shows **exactly one** red Alert, driven purely by `hasText(message.error)` — independent of whether partial content exists (`ChatMessage.tsx:165`). Don't duplicate the error into the streaming indicator or the body placeholder; those paths were deliberately stripped of error rendering.

**Toast vs Alert is a deliberate boundary, not an oversight.** Toast (`src/core/ui/notifications/Toast.tsx`) = page-level, transient, mutation-result. Inline `<Alert>` = query load-errors, persistent status banners, empty-state guidance, form validation. Don't migrate the latter to toast.

`i18n.ts:31` sets `interpolation: { escapeValue: false }` — **load-bearing**. Without it, i18next HTML-entity-escapes every interpolated string (e.g. a HuggingFace model id containing `/`) before it reaches JSX, which already escapes text nodes. It is safe, not an XSS reintroduction. If literal `&#x2F;` shows up in the UI again, check here first.

### API-boundary traps

- **Body-less POST endpoints 415.** Any FastEndpoints POST whose data comes only from the route (run-now / enable / cancel actions) is called by the generated client with no `Content-Type`, and FastEndpoints' default `Accepts=application/json` rejects it with **415** — surfacing as an empty, generic error toast. Fix (14 call sites already use it): `Description(x => x.Accepts<TRequest>())` in `Configure()`.
- **Multipart upload 415.** An upload request that only reads the untyped `Files` collection emits an empty OpenAPI `requestBody`, so the generated client sends JSON `{}` → 415. Fix: add a typed `IFormFile? File` to the request DTO so OpenAPI documents `multipart/form-data`, and read `req.File ?? Files[0]`. On the client, `AxiosInstance.ts` sets a global default `Content-Type: application/json` which **silently defeats** hey-api's per-call multipart serializer — uploads must call `axiosInstance.post(url, FormData, { headers: { 'Content-Type': 'multipart/form-data' } })` directly, not the generated SDK method.
- **A non-ProblemDetails error body produced an EMPTY toast.** `addApiProblemDetailsInterceptor` casts *every* non-2xx/401/429 body to `ProblemDetails` and throws `ApiError`, whose `message` was `apiProblemDetails.detail`. Endpoints that answer with a typed domain body instead (the source-build / CUDA-build 409 `{reason, message}`) have no `detail`, so `message` was `undefined` and `toast.error(undefined)` rendered a blank notification — the real reason silently discarded. `ApiError` now resolves `detail → message → title`, else `""`. Read the message via `apiErrorMessage(error, localizedFallback)` (`src/core/api/errors/ApiErrorMessage.ts`). Do **not** reach for `error.response.data.…` in a component — the interceptor has already replaced the AxiosError by then, so that branch is dead code (two build cards each carried their own dead copy of it).
- **URL-encoded slashes in path params.** hey-api encodes `/` as `%2F`, and Kestrel leaves `%2F`/`%5C` encoded by design — so a validator regex on the raw route value sees a literal `%` and rejects it. Any endpoint taking a model-name-like value as a **route segment** must decode via `ModelRouteName.Decode` (`Uri.UnescapeDataString`, deliberately not `WebUtility.UrlDecode`, which turns `+` into a space). Endpoints taking the name in a POST body don't need this.
- **int64 wire contract: `long` fields are normalized to `number`, except precision-sensitive seeds which are strings.** Raw hey-api with `validator:true` turns a C# `long`/`long?` (OpenAPI `format: int64`) into `z.coerce.bigint()` — the TS type claims `number` but the runtime value is a `bigint`, and arithmetic throws "Cannot mix BigInt and other types". The fix lives at the spec seam, not the generated client: `FetchOpenapi.mjs` normalizes int64 `format` at spec materialization so ordinary timestamps/durations/counts generate `z.number()`. Precision-sensitive long fields that can exceed 2^53 (sampling/image **seeds**) are instead carried as **strings** on the wire so no precision is lost. Never hand-edit `zod.gen.ts` — correct the contract in `FetchOpenapi.mjs` (or the endpoint's declared type) and regenerate.

### Client conventions

- Modals go through the shared **`DialogShell`** primitive — don't hand-roll a Mantine `Modal`.
- Chat capability flags (file/image attachments) are a **static client-side constant** (`NodeCapabilities.ts`), not server-composed. Don't assume a backend capability endpoint drives chat UI gating.
- Any bounded Mantine `NumberInput`/`Slider` that must distinguish "unset" from "user edited" needs a post-mount `ready` guard before wiring `onChange` to persistence — Mantine fires a **spurious `onChange` on mount** with a default/min value, silently overwriting an intentional "no override". This bit the sampling-options dialog twice.

### Races and flashes

- **Auto-advance must arm on the unmet→met transition**, not fire because the condition is already true on arrival — otherwise a returning user flashes through the step before they can read it. Pattern: `autoAdvanceArmedRef` in `OnboardingProvider.tsx` — reset to false on step change, set true only when the effect observes *unmet*, fire only when armed **and** met.
- Any **globally-mounted** TanStack Query (outside auth-gated routes) must be `enabled:`-gated on having an access token, or it fires pre-login without a bearer, 401s, and sticks in an error state that never recovers after login.
- **react-joyride v3 in controlled mode never emits `STATUS.FINISHED`** — the final Next emits `STEP_AFTER` + `action=NEXT` at the last index. A handler keyed on `FINISHED` hangs on the last step forever.
- **Every SignalR hub must be listed in `vite.config.ts`'s dev WS-proxy allowlist.** One missing hub falls through to the generic `/api` proxy and wedges Vite's *entire* WebSocket proxy — breaking hubs that *are* correctly listed.
- **Push-only (SignalR) terminal states need an explicit query invalidation in the reconcile handler** — there's no REST `onSuccess` to hang it off. Missed once for GGUF-download completion: the installed-models list only refreshed on manual reload.
- **`InvocationState` has two separate hand-rolled `Clone()` methods** (`WorkerEventDispatcher.Clone` and `InvocationResumeRegistry.Clone`), and the *cloned* snapshot — not the live mutated state — is what reaches the chat pump and persistence. **Any new `InvocationState` field must be added to both**, or it silently persists as null despite the dispatcher setting it correctly. This class of bug passes unit tests and is only caught by live verification.

---

## 6. Deliberately NOT built

Don't assume these exist; don't "restore" them.

- **Context-window management is a two-layer budgeter, not summarization.** `ConversationContextBudgeter` (`Client.Application/Services/Invocation/Context/`) is turn-grouped and two-pass — it excerpts oversized historical tool results, then drops oldest whole turns, pinning system messages plus the most recent N turns — applied at the single-agent growth points and at the orchestration seed in `InvocationRunner`. Below it, an inner `ProviderCallBudgetChatClient` re-budgets *every* raw provider round, covering the inner tool-loop rounds and MAF participant turns the outer layer doesn't see. The effective llama.cpp window (`-c`) is read back post-readiness and threaded into both budgeters. What's still true: this TRUNCATES/DROPS history (and excerpts tool results) rather than doing LLM summarization/compaction, and there is no cross-turn prompt-prefix preservation strategy.
- **Run state IS reconciled across a restart (no "stuck forever"), but a run is not auto-resumed.** Two startup reconcilers run *before* Kestrel serves (`Program.cs:198-199`): `NodeChatRestartRecoveryService` terminalizes any non-terminal assistant row → `Interrupted` and backfills a durable content-free run envelope; `IScheduledJobRunStore.MarkStaleActiveRunsAsync` moves stale `Queued`/`Running` scheduler runs → `Failed`. The run envelope (`AgentExecutionLogRecordKind.ChatRunEnvelope`) is the already-wired `ScheduledJobRunStore`-style durable ledger. What is deliberately NOT built: **auto-resume / re-dispatch** of an interrupted run (collides with "no mid-stream retry" + the cost/privacy of silently re-billing on every restart) — manual **Regenerate** covers it. `InvocationResumeRegistry` is in-memory (browser-reconnect only; empty after restart *by design*), not cross-restart resume. (Older notes saying "a mid-run restart loses it / stuck forever" are stale.)
- **No mid-stream retry** by design (a pre-first-token retry + per-model circuit breaker do exist).
- **Approval/HITL is live, and a general policy layer now exists (OPP-03).** MCP tools ship `RequiresApproval=true` by **default** (`Services/Mcp/IMcpServerConnectionManager.cs`), so `InvocationToolResolver` wraps each in `ApprovalRequiredAIFunction` and the framework surfaces a `ToolApprovalRequestContent` before it runs — the approval round-trip fires live for any node with an MCP server connected. `run_in_agent_home` is also gated (and ships inactive). **On top of the per-tool flag there is now a node-wide policy engine** (`IToolApprovalPolicy` + `PermissiveToolApprovalPolicy` NoOp floor in AI.Agent; real `NodeToolApprovalPolicy` in Application, reads `StoredNodeSettings.ToolApprovalPolicy` once at composition): tools carry a `ToolCategory` (ReadLocal/WriteExecute/Orchestration/Network/Unknown) declared at each definition site, and the policy maps `category → requiresApproval` (+ per-tool overrides). It is **strictly tighten-only** — the compose is a pure OR (`catalogDefault || nodePolicyByCategory || perAgent==true`), so a policy/per-agent value can only *add* approval, **never waive** it. Unknown category **fails closed** (requires approval). The compose is **not** cosmetic: `InvocationToolResolver` wraps on the resulting flag. Deferred (see [[Stale beliefs corrected]] follow-ups): category-level *loosening* (auto-approving a whole category unattended) is intentionally NOT built — it would require removing the structural floor. See §4 for the enforcement-seam trap. Approval decisions are audited (`AgentExecutionLogRecordKind.ApprovalDecision=2` in `agent_execution_logs`, metadata-only) + a `tool_approval_decisions_total` metric.
- **The scheduler CAN run a saved agent** (`RunSavedAgentHandler`, template id `run-agent`, OPP-02): node-local-only (a cloud/remote *effective* model is rejected up front — unattended work never leaves the box), capacity/GPU-admission-gated with the reservation disposed per fire, approval-required tools stripped (no unattended HITL), single-agent only, output recorded as a content-safe run-history summary. The `ModelRecommendationCheckHandler` is no longer the *only* handler. What is NOT built (follow-ups): writing the agent's answer into a visible chat **conversation** (summary-to-history only), and compiling a bound **orchestration** for a scheduled run (it runs single-agent).
- **No third sandbox provider.** Only `fake` (in-memory, CI default) and the process-based provider exist. The OpenSandbox self-hosted provider is planned, reviewed, and unbuilt.
- **Playbook/memory retrieval is embedding-ranked by default, lexical fallback** — the registered default `IPlaybookRetrievalRanker` is `EmbeddingPlaybookRetrievalRanker` (node-local cosine), with `LexicalPlaybookRetrievalRanker` (token-overlap) used only when `PlaybookRetrieval:EmbeddingModelName` is unset or embedding fails. Adaptive-memory injection rides the resolved system prompt (config-hash-safe) rather than a live `AIContextProvider` injection path, which would break resume-safety. Extraction-only use of `AIContextProvider` was the deliberate choice.
- **Adaptive memory is per-agent only** — scoped per `AgentDefinition`, no cross-agent or node-wide sharing.
- **No RAG over chat attachments** — v1 is file-tools-only (agent mode) or inline-text injection with a char cap (plain chat). No image/OCR ingestion.
- **No STT** — voice work is output-only (TTS). Kokoro TTS is **English-only**; all non-English speech falls back to the browser's OS voices.
- Desktop-only settings (ThemeConfigurator, Open Canvas editor) are deliberately not part of the mobile-responsive sweep.

---

## Stale beliefs corrected

Old notes (and agents who half-remember them) assert these. They are **false today**.

| Stale belief | Reality |
|---|---|
| "The WSL dev box has no GPU, so GPU work can't be tested here." | It has an **RTX 5090 (32 GB, sm_120) + CUDA 13.3**, live-verified. GPU paths are testable here. |
| "The dev box is an RTX 4080 with 16 GB (sm_89 / Ada); build CUDA with `-DCMAKE_CUDA_ARCHITECTURES=89`." | **Wrong since 2026-07-26** — it is an **RTX 5090, 32 GB, sm_120 (Blackwell)**. Read `nvidia-smi`; never hardcode the arch. `CudaBuildService`'s fallback constant still lacks `120`. |
| "llama.cpp has native MCP support now, so the app could use it." | The MCP client lives in llama-server's **web UI**, which this app never serves; the backend addition is only the experimental `--ui-mcp-proxy` CORS proxy. The .NET `ModelContextProtocol` client remains the correct integration point, and a browser-side host would move tool execution below the approval/sandbox boundary. See `Plans/2026-07-26-llamacpp-native-mcp-decision.md`. |
| "Ollama was removed from the app." | Removed only from *Aspire dev orchestration*. It remains a supported, gated, opt-in secondary provider with 50+ live call sites. llama.cpp is the *default*, not the *only*, runtime. |
| "`dotnet test` reports zero tests — run the native test-host exe instead." | Fixed by the `global.json` MTP runner pin. `dotnet test` works against the whole solution. Native exe still works, but is no longer required. |
| "`--treenode-filter` alternation `(A|B)` silently matches zero tests." | **False** on TUnit 1.58 — it returns the union (9 + 6 = 15, re-verified 2026-07-24). The claim survives in `AGENTS.md`; believe the measurement. |
| "`build-and-test.yml` and `e2e.yml` are PR gates that block merges." | **False, and always was.** Both registered workflows are `disabled_manually`, `e2e.yml` was never registered at all, and the repo has 6 total runs with 6 failures and 0 successes. The only enforced gate is `publish/package-tester-win.ps1`. |
| "`release.yml` is the release mechanism." | It is disabled and has never produced an artifact. Every published tester RC came from `publish/package-tester-win.ps1` run manually on Windows. |
| "`release.yml` passes `--pre` to `vpk pack`, breaking prerelease CI." | Already fixed in the (dormant) workflow — `--pre` is only on the `upload github` step. |
| "The TOCTOU/no-follow sandbox guards live in `LocalContainerSandboxProvider` (Docker)." | They live in **`ProcessSandboxRuntimeProvider`** (`ProviderName="process"`). Re-check `SandboxProviderSelector.Resolve` before citing a provider name. |
| "Plain `git apply` rejects a `--binary`-diffed patch." | **False** on modern git (2.43+) — it applies fine. Any security control depending on that rejection is unsound. |
| "The advisor runs an approved `llmfit` utility container over gRPC/HostAgent." | That path was built and then **fully replaced** by the in-process `MemoryFitEstimator` + live HF discovery. No Docker/HostAgent in the recommendation path; the orphaned `approved_utility_images` store was deleted and the table **dropped via migration** (BE-04). |
| "Recommendation ranks by `OrderByDescending(EstimatedBytes)`." | Superseded by **capability-bucketed** ranking (`EstimatedBytes / 1 GiB` bucket → downloads → date → trust). |
| "`ModelKind` is Unknown/Chat/Embedding." | A fourth kind, **Reranker**, exists — and the reranker name-check must run *before* the embedding check. |
| CUDA toolkit pinned at "13.1"; TUnit at "1.56.x". | Point-in-time snapshots — now 13.3 and 1.58.x. Don't build tooling against a remembered minor version. |
| "There's no context-window management — the full conversation replays verbatim every turn." | A two-layer budgeter exists: `ConversationContextBudgeter` (turn-level) plus `ProviderCallBudgetChatClient` (per-provider-round). It truncates/drops, not summarizes. |
| "Playbook/memory retrieval is lexical, token-overlap ranking by design." | The default `IPlaybookRetrievalRanker` is `EmbeddingPlaybookRetrievalRanker`; `LexicalPlaybookRetrievalRanker` is only the fallback. |
| "The node DB is SQLCipher / whole-file encrypted, so a wrong operator secret fails DB-open at startup." | **False** — it's plain SQLite (`bundle_e_sqlite3`, no `PRAGMA key`) with **per-column AES-256-GCM** AEAD (see [wiki 08](wiki/08-data-and-persistence.md)). A wrong secret does **not** fail startup. Consequently the DataProtection key-ring's fail-closed guarantee rests on `NodeDataProtectionKeyRingFailClosedKeyResolver` (BE-02) — which hard-fails on an *undecryptable encrypted* key instead of silently regenerating and orphaning cloud/worker tokens — **not** on DB-gating. |
