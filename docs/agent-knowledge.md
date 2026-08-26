# Agent Knowledge Base

Hard-won rules, invariants, and traps for this repository. `docs/wiki/` explains how the system is built; this file records failures that are easy to repeat even after reading the code.

**Required reading:** read the relevant numbered section before a non-trivial change. Keep the numbered sections and named headings stable; code comments and runbooks cite them.

**Evidence:** measurements, dated investigations, and volatile environment observations live in [agent-knowledge-evidence.md](agent-knowledge-evidence.md). Follow that link when changing a rule or diagnosing the same failure.

**Maintenance:** write **Rule → failure prevented → authority/test**. Cite a file and symbol, not a mutable line number. Keep obsolete claims in [Stale beliefs corrected](#stale-beliefs-corrected). Treat this file as a floor, not proof that no other invariant exists.

## Navigation

- [§0 Repository orientation](#0-repo-orientation)
- [§1 Build, test, CI, packaging](#1-build-test-ci-packaging)
- [§2 Development environment and local runtime](#2-dev-environment--local-runtime)
- [§3 Models, inference, retrieval](#3-models-inference-retrieval)
- [§4 Agent Mode, MAF, sandbox, cloud](#4-agent-mode-maf-sandbox-cloud-providers)
- [§5 Frontend, chat UX, API boundary](#5-frontend-chat-ux-api-boundary)
- [§6 Deliberately not built](#6-deliberately-not-built)
- [§7 Agentic support / MCP-only mode](#7-agentic-support--mcp-only-mode)
- [Stale beliefs corrected](#stale-beliefs-corrected)

---

## 0. Repo orientation

- This is the standalone repository at `~/projects/XE-Local-AI-Engine`, with remote **`public`** pointing to **`w0rldx/XE-Local-AI-Engine.Source`**. There is no `origin`, parent-repository pointer, or `w0rldx/XE-Local-AI-Engine` remote.
- Old references to `C0re.slnx`, `C0re.Client.React.Web`, `C0re.Tests.IntegrationTests`, or a C0re-parent Docker context are invalid. Current names are `XE-Local-AI-Engine.slnx`, `XE-Local-AI-Engine.Client.React/`, the three in-repo unit-test projects, `XE-Local-AI-Engine.Tests.E2ETests`, and `XE-Local-AI-Engine.AI.Contracts`.

### Cite symbols, not `file:line`, for anything under active development

**Rule:** cite `file` plus a symbol or quoted phrase. Never cite a line inside a file your change edits. Line citations remain in range after edits while pointing at the wrong code; symbol citations survive concurrent edits. See ADR 0004's anchor-maintenance note.

---

## 1. Build, test, CI, packaging

`AGENTS.md` is authoritative for current validation commands and CI shape. The rules below explain the traps behind it.

### Always finish with a Release build — a green Debug build is not verification

- Local Debug builds disable diagnostic analyzers unless `CI` or `XE_FULL_ANALYSIS` is set. Meziantou, BannedApiAnalyzers, and `IDExxxx` enforcement therefore require Release.
- Finish backend work with a real Release compile:

  ```bash
  dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-restore
  ```

- A ~1-second incremental build with `0 Warning(s)` may have skipped every project and replayed no diagnostics. When evidence matters, force compilation:

  ```bash
  dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-incremental
  ```

- `dotnet run --no-build` defaults to Debug. Pass `--configuration Release`, and inspect the `total:` line; **zero tests is never a pass**.
- Keep the analyzer gate in `Directory.Build.targets`, not `.props`: `Configuration` is not defaulted when `.props` is imported. Verify with `dotnet msbuild <proj> -getProperty:RunAnalyzers -p:Configuration=…`.
- `RunAnalyzers=false` skips diagnostics, not source generators; do not treat it as a TUnit-discovery switch.


An incremental Release result is evidence only when projects actually compiled. Analyzer diagnostics are not replayed for skipped projects. Keep `TreatWarningsAsErrors` in Debug for compiler warnings, but do not confuse that with the analyzer wall. `XE_FULL_ANALYSIS=1` is the explicit analyzer-sensitive Debug loop. `RunAnalyzers=false` maps to csc `-skipanalyzers`; source generators still execute, so a zero-test run points to build/config/discovery, not this gate.

### A bare `TODO` in a C# comment fails the build — in **Release**

Sonar S1135 is an error under warnings-as-errors. Use `follow-up:` or `Not yet implemented:` instead of `TODO`/`FIXME`/`HACK`/`XXX`. Debug may accept the comment; Release will not.

### Running backend tests

- Tests use TUnit on Microsoft.Testing.Platform. Use `--treenode-filter`, not `--filter`:

  ```bash
  dotnet test XE-Local-AI-Engine.Tests/XE-Local-AI-Engine.Tests.csproj \
    --configuration Release --no-build \
    --treenode-filter "/*/*/CapabilityReporterTests/*"
  ```

- Wildcards and alternation work: `/*/*/(ClassA|ClassB)/*`. `--list-tests` is authoritative. A no-match filter exits **8** (`Zero tests ran`).
- Never use bash's reserved `GROUPS` variable for batching; use `TEST_GROUPS`.

### The GPU smoke is the only gate that proves the GPU did the work — and its exit codes are a taxonomy, not a scale

Run `scripts/run-gpu-smoke-local.sh` before a tester RC or after changing inference/runtime behavior. A correct answer does not distinguish GPU execution from CPU fallback; require utilization plus a VRAM rise from a pre-start baseline.

- **`1`**: a product step was judged and failed; `=== Summary ===` names every verdict.
- **`5`**: infrastructure abort; nothing was judged and no summary prints.
- **`2`**: missing prerequisite, including no NVIDIA GPU.
- **`3`**: another instance is running. **`4`**: state could not be determined.
- **`75`**: contamination; the result is void and must be rerun. **`130`**: interrupted.

Every step must record a verdict. `IRuntimeDeviceAudit`, not the installed variant record, is authoritative for effective GPU/CPU execution.


Installed runtime metadata and effective execution disagree in both directions: a Vulkan record without an ICD can run CPU, while a CUDA path override can run GPU under a stale Vulkan record. `IRuntimeDeviceAudit` combines selected binary, hardware, and `--list-devices`; the smoke confirms outcome during generation. A failed/timeout audit is unknown and must not trigger the CPU-fallback alarm or persist as determinate state.

### The `.opencode/` agent eval foundation

`.opencode/` is internal-only, gitignored tooling and is absent from a public clone. Where available, behavioral rules use compliant/negative scenario triples so the negative case must fail. Do not document its commands as public-repository validation.

### `release.yml` is the intended release path — GitHub Actions must be enabled to run it

- `.github/workflows/release.yml` is the tag-triggered win-x64/linux-x64 Velopack path. It reuses `build-and-test.yml` as `validate`; packaging depends on validation.
- Actions enablement is external state. Check `gh workflow list --all` / `gh run list`; never quote a dated observation as current.
- `build-and-test.yml` targets `develop` and runs `python-quality`, `release-contracts`, `build-and-test`, and `client-react`. E2E is manual or label-triggered, not a blocking merge gate.
- Backend projects run concurrently with separate `--results-directory` values. `XE-Local-AI-Engine.Tests` uses `scripts/run-tests-memory-safe.sh` with grouped namespace filters; other projects use `--maximum-parallel-tests 2`.
- Coverage is merged by `(filename,line)` and checked against `scripts/backend-coverage-baseline.txt`. Keep discovery depth-bounded because `--report-trx` creates byte-identical attachment copies.
- `python-quality` uses the root tooling manifest, not the shipped training runtime. Never add dev tools to `tools/training/pyproject.toml`.


**Hollow-gate controls:** the backend's `Passed!|Failed!` grep proves that MTP emitted a summary, not that a meaningful test succeeded. A unit where every test skipped still prints `Passed!`. CI therefore sets `XE_REQUIRE_DOCKER_TESTS=1` so `DockerSandboxRealDaemonTests` cannot silently skip, and `TZ=Europe/Berlin` stays set to expose non-UTC bugs. The grouped runner applies the summary guard per group, not per namespace. E2E remains a separate opt-in lane.

Coverage instrumentation rewrites assemblies **in place**. Concurrent covered processes must use separate cloned output trees; without coverage, processes may share the built tree. `scripts/run-tests-memory-safe.sh` keeps coverage slots inside the repository because hosting tests locate the Client source by walking upward from the test binary. An empty dynamic-instrumentation report can claim `line-rate="1"`; `merge-cobertura.py` therefore treats zero source lines as an error. Keep the report-count checks as well as the percentage baseline, and raise the baseline only when coverage improves.

The architecture tests are ordinary tests inside `XE-Local-AI-Engine.Tests`; a full module run is what executes them. The deprecated manual packager is reference-only and is not the release authority.

### Add a hub, a route family, a React feature or a project — and name it in the wiki, or `python-quality` goes red

After adding a SignalR hub, `LocalApiRoutes` family, React `features/` directory, numbered wiki page, or solution project, update the corresponding wiki inventory and run:

```bash
python3 scripts/docs-inventory-check.py
```

The checker parses canonical definitions, not comments; keep experimental examples out of production enum/route declarations.


`docs-inventory-check.py --verbose` exits 2 when an inventory cannot run, and each extracted inventory must be non-empty so a moved path/failed regex cannot pass vacuously. Its tests remain `unittest`: `scripts/run-release-contract-tests.sh` executes each `scripts/tests/test_*.py` directly and expects a non-vacuous `Ran N tests`/`OK`; bare pytest functions may pass `python-quality` while doing nothing in release-contracts.

### Verify against the whole module, not just the class you touched

A filtered test proves the target behavior only. Before handoff, run the whole changed test project/module because static caches, host state, and collection order create failures outside the edited class.


**Whole-module DI guard:** a singleton factory once called `INodeSettingsStore.Load().X` without accepting the test substitute's null result. Every host-based test failed at startup while narrow changed-class runs stayed green across four merges. A composition factory reading optional store/config state must null-guard it (`Load()?.X`). The full module is the only local check that proves unrelated host construction still works.

### The full Tests module is flaky under parallelism — verify suspects in isolation

Rerun a named failure alone, then the module. Timing-sensitive tests must use contended budgets and separate availability from behavior. Do not weaken assertions merely because the full module is loaded; a filtered green diagnoses the failure but is not the final gate.


**Conflicting `XE_NODE_SQLITE_KEY` premises:** `DesktopBootstrapTests.WhenNeitherEnvSet_*` requires the parent variable **unset**; `PlaybookRetrievalRankerRegistrationTests` requires it **set** to base64 for 32 bytes. No one ambient value satisfies the module. The registration tests use keyed `[NotInParallel("XE_NODE_SQLITE_KEY")]`; `DesktopBootstrapTests` uses a stronger bare `[NotInParallel]`. Do not replace the bare attribute with a key.

Only `CudaBuildServiceTests` and `LlamaCppSourceBuildServiceTests` mutate that parent environment variable, and both are exclusive. Other matches seed a host configuration dictionary, set a **child** `ProcessStartInfo.Environment`, or mention the name in comments; they do not mutate the parent. Audit the write site before adding serialization.

**Bare `[NotInParallel]` means module-wide exclusivity.** Preserve it for:

- `NodeMeterCapture` suites in Telemetry/Invocation/Mcp/Agents: they listen by meter name (`XE.Node`), sometimes without an instrument filter, so any product-emitting test contaminates the capture window. A keyed group cannot mean “nothing else emits.”
- PATH-stub suites such as CUDA/source-build tests: they must not overlap any test spawning real `git`, `cmake`, or related tools, not merely other stub suites.

Get parallelism from separate processes/modules rather than weakening these attributes.

### A wall-clock budget sized on an idle box is a CI flake waiting to happen — use `TestBudgets.Contended`

Use `TestBudgets.Contended` for completion windows under module/coverage contention. Keep semantic timeouts inside the product option under test. Avoid stopwatch ceilings unless timing is the behavior.


`TestBudgets.Contended` is a **failure deadline**, not a sleep; polling/awaiting still returns immediately when the condition arrives. Do not widen a timeout that the test expects to expire. Raise a budget only when the awaited event can still occur.

If the operation can fault before publishing the awaited signal, inspect/await that task while polling so the test reports the real exception instead of timing out. If the wait observes one signal and the assertion observes a later cleanup signal, give the post-condition its own `AssertEx.EventuallyAsync`; terminal phase may be set before a reservation or activity marker is released.

**Adding a migration can red the module through a test that never mentions migrations.** `NodeChatMigrationRecoveryServiceTests` applies the WHOLE migration set against a real SQLite file under a wall-clock attempt budget, and starvation there does not read as a slow test: the attempt is cancelled MID-APPLY, leaving a half-rebuilt `ef_temp_*` table behind, and the retry dies on `table "ef_temp_<name>" already exists`. Two migrations plus five new migration tests (2026-08-25) were enough to tip it over on a 16-core box, with a different test of the class failing each run. The fix is `[NotInParallel]` on that class, not a bigger budget: its abandoned-lock test's first attempt is MEANT to exhaust the budget, so raising it buys the same increase in dead wall clock.

### Leftover build daemons starve the timing-sensitive tests — and the packaging gate is where you notice

Before packaging or diagnosing a suddenly slow timing test, run `dotnet build-server shutdown` on a quiet machine. Compare failure duration with the intended timeout: a wall-clock failure under load differs from a fast semantic failure.

### A concurrent `dotnet build` corrupts a test run — and the result is then neither pass nor fail

- Never run builds and `--no-build` tests concurrently against the same `bin/` tree.
- Use `scripts/with-build-lock.sh -- <command>`; exit **69** names the lock holder.
- Use `scripts/assembly-guard.sh guard --test-bins -- <test command>` around new runners. Exit **75** means `CONTAMINATED`: discard and rerun.
- Do **not** wrap `scripts/run-tests-memory-safe.sh` unless `NO_BUILD=1`; it already guards itself and its own build triggers an outer guard.
- The lock helper must mark the FD close-on-exec before MSBuild. Naive `flock <file> <command>` leaks it into build-server daemons and creates a phantom holder.


The assembly guard deliberately snapshots after a runner's own build and before testing. A direct `dotnet test` bypasses both lock and snapshot; wrap it or treat surprising results as provisional. VS Code C# Dev Kit's BuildHost is non-cooperating and can still rewrite assemblies under a held shell lock; recognize it by unchanged sizes with only mtimes moving and rerun.

Do not wrap the whole project validator in one outer lock: its internally locked trees would become pass-throughs and regain unsafe parallel build/test overlap. Prevention and detection must remain separate layers.

### Never classify a cancellation from a `CancellationToken.Register` callback

Registration callbacks race disposal and execute synchronously. They may signal/kill, but must not own terminal classification or throw the final exception. Classify after the awaited operation observes authoritative cancellation state.

Cancellation callbacks run in reverse registration order. The watchdog flag callback was registered first, so later stream callbacks could release the operation and let failure mapping run before the flag was set; genuine timeouts became Cancelled. The same flag also mislabeled host/disconnect cancellation as watchdog when it won. Derive one classification at mapping time in priority order: deliberate cancellation recorded synchronously, caller/host token, then the invocation source's timer by elimination. Feed both failure category and metric from that single result.

A deterministic ordering test parks the stream on an external gate, registers a later callback that releases it, and blocks the cancellation callback chain until the result is observed. Registering “earlier” is not a fix because callbacks already owned by downstream code do not exist yet. Keep the regression test deterministic rather than load-dependent: the earlier race reproduced only intermittently under the full suite and looked like a flaky metric assertion, not product misclassification.

### Code behind `#if P0_SPIKE` escapes the analyzer wall, and the constant REPLACES the defaults

- Excluded code can rot outside analyzers. `scripts/lint-release-scripts.sh` build-checks `P0_SPIKE`; keep that gate.
- `DefineConstants=P0_SPIKE` **replaces** defaults such as `TRACE`. Append evaluated defaults or rebuild normally afterward; otherwise gated assemblies remain in `bin/` and contaminate later `--no-build` tests.


For an automated spike compile, read the existing `DefineConstants`, append `P0_SPIKE`, and encode semicolons as `%3B`; a command-line `$(DefineConstants)` is not recursively expanded. Verify the effective property with `-getProperty:DefineConstants`. Do not redirect `BaseIntermediateOutputPath` globally—it propagates through project references and can duplicate generated assembly attributes. Rebuild ungated afterward. Under `set -o pipefail`, avoid `strings | grep -q` for marker checks because the expected match can SIGPIPE `strings` and return 141.

### The full Tests module balloons to ~3.5 GB — it is a framework leak, not a fixture bug

- Use `TestServerWebAppFactory`, not `WebApplicationFactory<Program>`; entry-point resolution roots host graphs for process lifetime.
- `AddRateLimiter` creates an undisposed replenishment timer per host. Test composition uses `NoOpRateLimiterOptionsProvider`; real rate-limit tests opt into one shared host.
- Avoid per-test providers that create long-lived MCP SDK registries/clients. Use the keyed/shared fixture pattern.
- `scripts/run-tests-memory-safe.sh` is the whole-module runner. A low `DOTNET_GCHeapHardLimit` is not a valid leak verdict; size-cap tests allocate large payloads.


`TestServerWebAppFactory` is more than a rename. The replacement closed four process-lifetime roots: a per-host MEAI function-descriptor cache key, rate-limiter timers/closures in Testing, EF service-provider caching for per-host connection strings, and SQLite pool groups. Keep the per-host JSON options, Testing rate-limiter bypass, `EntityFramework:ServiceProviderCaching=false` fixture setting, and `SqliteConnection.ClearAllPools()` teardown. Reintroducing `WebApplicationFactory<Program>` restores the top-level-entry-point `HostingListener` root.

**Shared per-class host eligibility is narrow:** TUnit runs those tests concurrently against one host and one SQLite database. Share only when tests are read-only or write exclusively Guid-named entities. Do **not** share a class that:

- asserts list counts, empty/global state, or shared-substitute `Received`/`DidNotReceive` calls;
- swaps DI/configuration per test;
- tests fixture lifecycle/startup/shutdown behavior;
- leaves process-global listeners or other side effects alive across test boundaries.

`BackendTraceCorrelationTests` proved `[NotInParallel]` is insufficient when a shared host itself outlives each test's `ActivityListener`. `ClassDataSource<T>` also needs a true parameterless constructor. Per-test fixture customizations use `ConfigureAdditionalTestServices`, `AdditionalConfiguration`, `EnableDevelopmentMode`, and related init properties; there is no `WithWebHostBuilder`.

`run-tests-memory-safe.sh` partitions by namespace into fresh processes, schedules longest first, refuses an empty unit list, and uses `TEST_GROUPS` for coverage grouping. `JOBS=1` restores strict batch sequencing; `PAR=N` reintroduces in-process races. The runner self-guards—an outer assembly guard sees its own build as contamination unless `NO_BUILD=1`. A one-process full run may be trustworthy on a roomy machine, but the batch runner remains the local wall-time/memory tool of record.

### Test hosts leak temp files to `Path.GetTempPath()` — keep the fixture cleanup

`TestServerWebAppFactory.DisposeAsync` must delete SQLite sidecars, node-data directories, and temporary `wwwroot` roots. A killed run cannot dispose; recover with:

```bash
find /tmp -maxdepth 1 -name 'xe-local-ai-engine-tests-*' -exec rm -rf {} +
```

Do not use a huge shell glob; it can hit `ARG_MAX`.

### A test host whose content root is the real Client source dir must register a fake `INodeDataDirectory`

Any fixture combining the real Client content root with redirected node data must remove `INodeDataDirectory` and register `FakeNodeDataDirectory`. Otherwise first-launch migration moves credential/settings files out of the checkout and teardown deletes them. `ServiceProviderValidationTests.HostCreation_LeavesTheContentRootNodeSettingsWhereItIs` is the guard.

### The one temp artifact that is meant to survive: the migrated SQLite template

The MVID-keyed `/tmp/xe-local-ai-engine-tests-template-*.sqlite` cache intentionally survives teardown. It is atomically published and invalidates on migration/seed assembly rebuild. Delete it to force recreation. Set `UsePreMigratedDatabase=false` only when testing migrations themselves.

### Layering is mechanically frozen

- `LayerDependencyTests` enforces provider/contract direction.
- IDE0130 requires namespaces to follow folders. Endpoint DTOs deliberately keep the flat `…{Area}.V1` namespace because NSwag schema IDs include it; adding `.Dtos` churns generated API types. Mappers/validators follow their folders. See `docs/wiki/16-code-conventions.md`.

### OpenAPI → hey-api is the sole REST data layer for React

- React REST uses `src/core/api/generated/`; never hand-edit it.
- `pnpm openapi:check` only checks against checked-in `openapi/v1.json`; it cannot discover a new endpoint/field. For contract changes, fetch a live desktop spec, ensure no paths disappeared, update the spec, then regenerate.
- **Use `XE_LAUNCH_MODE=desktop`** or every `IDesktopOnlyEndpoint` disappears. Ports are random; set `OPENAPI_SPEC_URL=<host>/openapi/local/v1/v1.json`.
- For mise-managed toolchains:

  ```bash
  MISE_TRUSTED_CONFIG_PATHS=~/.config/mise/config.toml \
  MISE_DATA_DIR=~/.local/share/mise \
  scripts/openapi-live-check.sh
  ```

- Gate behavior, not discovery, when services register independently of the flag. Work Sessions keep routes/hub mapped and return 404 ahead of auth when disabled.
- hey-api query keys are single-element object arrays; invalidate by partial-object match, not `.slice()`.


OpenAPI feature flags must not make the document depend on the node used for regeneration. If services register unconditionally, keep endpoints discoverable and reject disabled behavior on the request path. Development Mode is the exception because its services are absent when off; Work Sessions demonstrate always-discovered routes/hub plus a pre-auth 404 middleware. When fetching a live spec, compare paths and require removals = 0 before replacing the checked-in document.

The live checker isolates HOME for the desktop host. Preserve the two mise variables exactly; a missing trusted config/tool install aborts before OpenAPI readiness and masquerades as contract drift. Generated TanStack keys are arrays containing one parameter object, so partial-object invalidation is the stable match across optional fields.


The generated client is the only REST layer; direct Axios is reserved for boundaries the generator cannot currently express correctly (notably multipart under the global JSON header). New backend paths require a live-spec fetch before `openapi:check`, and generated output is committed with the contract. A non-desktop fetch is explicitly incomplete because it removes app-update, GitHub auth, and image routes.

### Browser E2E runs as two ordered parallel groups, not one sequential queue

- `BrowserSerial`: canonical admin, limit 1, session-global state.
- `BrowserPooled`: distinct leased users, limit 4.
- Distinct order values create non-overlapping phases; never depend on which runs first.
- Keep exactly one `SetupCompleted=true` user because form login has no email. Pooled users authenticate through explicit-email API login.
- Never restore group/limiter attributes on `XEE2ETestBase`.


The phase split is by **state ownership**, not test duration. Keep tests mutating `WorkerEventDispatcher.CurrentInvocation`, `FakeOllamaState`, tutorial state, scheduler-global jobs, or node-wide emptiness serial. A pooled user is leased from a bounded channel and returned in `[After(Test)]`. `Context.APIRequest` shares the browser context cookie jar, so explicit-email API login establishes `node_rt` before navigation.

Distinct `[ParallelGroup]` Order values prove non-overlap but not execution direction. Never seed dependencies from one phase into the other, and never add group/limiter attributes to the common base because derived classes would inherit conflicting copies.

### Frontend lint

- `pnpm run lint` is the frontend typecheck. E2E uses `build:e2e` (Vite only), so E2E green does not prove TypeScript correctness.
- Lint does not run Biome formatting. Do not format whole directories to repair one file.
- react-doctor config is `doctor.config.jsonc`; `deslop` ignores need the `deslop/` prefix.

### Frontend tests: an `await import()` inside `it()` is charged to `testTimeout`

Cold dynamic component imports inside a test count against timeout and become coverage-only flakes. Keep `testTimeout=20s`. Prefer static imports and store setters; use `vi.resetModules()` plus dynamic import only when module-init hydration is under test.

### Packaging (Velopack)

- `vpk pack` has no `--pre`; prerelease state belongs in `--packVersion`. `--pre` is only for `vpk upload github`.
- Build React before `dotnet publish`; `GuardNodeReactBuildPresentOnPublish` rejects a webless publish.
- `release.yml` is official. `package-tester-win.ps1` and `package-rc.sh` are deprecated/reference-only.
- Package on a quiet machine after `dotnet build-server shutdown`.
- **Consolidated to one repo.** Source and releases live in `w0rldx/XE-Local-AI-Engine.Source`; the tester repository is retired. Historical tags may be bare or `v`-prefixed; lookup must support both. The deprecated script's retired-repo target is historical, not pending migration.
- Both update channels target this repository anonymously. Do not restore client-id/device-flow requirements or redact the URL.
- Changelog path: `cliff.toml` → `RELEASE_NOTES.md` → `vpk pack --releaseNotes`. Root `CHANGELOG.md` is hand-maintained.


Official packaging validates the selected update-policy file, not only publish exit code. `CopyToPublishDirectory="Always"` may leave a previously disturbed destination missing until the **source** timestamp changes. Both official and reference packagers must refuse a package with missing/wrong update channel. Historical manual tag lookup supports bare and `v` forms, but the public updater itself is anonymous and targets the consolidated source repository.

### The backend serves the SPA

One Kestrel process serves API and UI via `UseStaticFiles` and `MapFallbackToFile("index.html")`. Do not add a second bundled Node/static server.

---

## 2. Dev environment & local runtime

### This WSL2 box HAS a GPU

Never infer the current hardware from this document. Run `nvidia-smi`, `nvcc --version`, and inspect the compiled architecture. The last verified box was RTX 5090/32 GB/sm_120 with 8 WSL CPUs and ~31 GiB RAM, but hardware/toolkit versions have already changed.

WDDM/WSL traps:

- VRAM pressure may page to host RAM instead of OOM, producing correct but much slower inference. Do not use pressure here to validate OOM recovery or quote local benchmarks as consumer figures.
- `nvidia-smi memory.free` is global; llama.cpp `--list-devices` uses a process residency budget. They can diverge by tens of GiB. Use global availability for admission/invalidation and the process budget for process allocation; never reunify them.
- `nvidia-smi --query-compute-apps` may report no processes under WSL even while VRAM is occupied; per-process attribution is unavailable.

### This WSL2 box has no keyring

With no Secret Service daemon, Azure.Identity can wrap MSAL cache persistence failure as `AuthenticationFailedException`. Walk `InnerException`; do not catch only `CredentialUnavailableException`. Such sessions are memory-only and do not survive restart.

### `dev-stop.sh` is the sanctioned stop path — but the leak it guards against no longer reproduces

Use `scripts/dev-start.sh`, `scripts/dev-status.sh`, and `scripts/dev-stop.sh`; never `aspire stop --all` during parallel development. `dev-stop.sh` binds ownership to the exact AppHost/DCP graph, records process start times to defeat PID reuse, fails closed on malformed Aspire output, and never restores a global `llama-server` kill.

Plain `aspire stop` cleaned the tested stacks in later measurements, but the original orphan trigger remains unknown and the fallback still performs real cleanup. Do not remove it until a reproducer identifies the trigger. See [evidence §2](agent-knowledge-evidence.md#2-local-runtime-evidence).

### The node operator secret is seeded by dev-start.sh, not by any tracked file

- `node-sqlite-key` is required. Never add a tracked default.
- `dev-start.sh` mints `.data/node.key` and passes **`Parameters__node-sqlite-key`** through the environment using Python because bash cannot export dashed names. Never put the secret in command-line args or `/proc/<pid>/cmdline`.
- A key/data mismatch surfaces as `AuthenticationTagMismatchException`, often looking like corruption. Before deleting data, retry with `XE_NODE_OPERATOR_SECRET_FILE=/path/to/key ./scripts/dev-start.sh`.
- Bare IDE/Aspire starts use interactive parameters or `dotnet user-secrets set "Parameters:node-sqlite-key" …`.
- Reuse populated model storage through `HuggingFace__ModelsDirectory`.
- Aspire JSON is sensitive. `aspire ps` exposes the dashboard token and `aspire describe` may expose environment/connection data. Log only the `dev-status.sh` allowlist.
- Startup reapers clean owned stale llama/image servers. Never `pkill -f` a substring that also appears in the caller command line; kill by PID.


A user-secret key and `.data/node.key` can disagree. `dev-start.sh` always supplies the file, so data written under the old user secret fails later protected reads without naming the cause. Prefer `XE_NODE_OPERATOR_SECRET_FILE` pointing to the correct historical key; deleting `.data/node-sqlite/`, `dp-keys/`, and encrypted credential files is destructive fallback. `dev_ensure_node_operator_secret` warns when it mints a key beside existing data.

### Locked runtime decisions — do not "helpfully" reintroduce

- **Docker is off inference.** ADR 0004 permits it only for Development Mode build/test/lint. No Docker in model hosting/acquisition, embedding, image generation, or chat; HostAgent and sandbox gRPC stay deleted. Current implementation status belongs in `docs/roadmaps/development-mode-container-status.md`.
- The dependency is **`Docker.DotNet.Enhanced`** although its namespace/assembly are `Docker.DotNet`. Do not replace it with the obsolete original or pin transport packages separately.
- Rootless container uid 0 maps to the engine user; uid 1000 maps into subuids and may lose workspace access. Resolve the identity that maps to the engine's host uid and never host uid 0. `inspect` only echoes the requested uid, so verify by writing inside and statting the file host-side without following links. Uid-0 validity is daemon-dependent and cannot be rejected at options-validation time.
- A visible but inaccessible rootful socket must not silently redirect product selection to a rootless daemon. Real-daemon tests may probe the user socket only as test fallback; set `XE_REQUIRE_DOCKER_TESTS=1` to turn a skip into failure.
- A read-only-rootfs .NET container needs a **64 MB `/tmp` tmpfs** with `noexec,nosuid,nodev`. `/tmp/.dotnet` is too narrow; named mutex setup needs the writable parent and ignores `TMPDIR`/`TMP`/`TEMP`. Verify flags as tokens, not substrings.
- An agent-writable `.git/config` and in-tree `.gitattributes` can execute host-side `core.fsmonitor` and clean filters. Keep hardened command-line `-c` pins, mount `.git/config` read-only in containers, and rewrite it to a minimal safe config before host git. Preserve format/filemode/bare/extensions keys; do not recreate `origin`.
- Process sandbox containment is measured per mechanism (`setsid`, `systemd-run`, `unshare`) and may degrade to none. Advertise only served capabilities and fail closed when a caller asks for unsupported confinement. Never claim flat network isolation across hosts.
- Network policy is deny-all where supported; restricted allowlists remain unsupported. AgentHome requests denial only when advertised, and Coder attaches to the same sandbox.
- Development Mode uses two sandboxes: a short-lived unrestricted `development-warm` sandbox for frozen restore, killed before return, then an agent-facing deny-egress sandbox where supported. Check which request you are reading. Dependency-manifest changes are retryable verdicts; test deletion remains a security exception.
- Tracked credentials are shadowed with engine-generated empty read-only files when the provider supports it; never delete them from the worktree. The process-provider fallback only records detection.
- Strip `XDG_RUNTIME_DIR` before executing sandboxed code. Network namespaces do not isolate Unix sockets; otherwise a child can escape via the user systemd bus.
- Provider selection is per feature through `IAgentSandboxRuntimeProvider` / `IDevelopmentSandboxRuntimeProvider`, not global `ISandboxRuntimeProvider` registration. Development may use Docker; AgentHome/Coder stay process-backed. MXC remains the long-term seam.
- Ollama remains a gated, opt-in secondary provider (`XE_OLLAMA_RUNTIME_ENABLED`). It was removed only from Aspire orchestration; llama.cpp is the default, not the only runtime.


**Docker permission boundary:** on Linux, access to the Docker socket is root-equivalent. ADR 0004 documents that risk; the product neither assumes nor provisions rootless Docker. Repository-supplied container configuration is rejected wholesale—especially `devcontainer.json` and aliases—because the repository is agent-writable and therefore untrusted input. Only engine-approved images/profiles may define the container. Widening the accepted configuration surface is an operator/security decision, not plumbing.

**Containment is requested and served per capability.** `SandboxContainment` is measured by doing the operation, not locating a binary. `Capabilities` advertises only mechanisms that succeeded, and `BuildLaunchPolicy` rejects requested-but-unserved controls. Do not soften either half. `SandboxNetworkPolicy.Restricted` remains unsupported; `None` means an empty namespace where served. AgentHome/Coder capability-gate the request so Windows and degraded hosts remain usable without claiming isolation.

`policy.json` is an initialization record, **not current-run authority**. `EnsureBaselineFilesAsync` preserves an existing operator-edited file, so a home created before deny-egress may still say `unrestricted` while current runs are denied. This is safe-direction under-reporting. Determine current posture from provider capabilities/status, never the baseline file.

**Development warm sandbox preconditions:** `DevelopmentWorkspaceProvider.PrepareAsync` runs only the frozen profile's restore in `development-warm`, and only when there is a clean tracked tree at the base commit (`git status --porcelain --untracked-files=no`). It runs once per `BaseCommit`, recorded in never-mounted `workspace.json`, then the sandbox is killed before the agent-facing sandbox starts. Do not warm a dirty tree or mount that metadata into the agent sandbox. A Windows/no-`unshare` backend may serve unrestricted agent execution; report the served posture from Development status rather than the requested policy.

**Credential shadowing and git hardening are outcome boundaries:** shadow only tracked credentials, never mutate/delete the worktree; the clone does not carry untracked files. Host git rewrites `.git/config` before **each** provider-independent call, while the container mounts it read-only. Minimal config preserves repository format and `extensions.*`; an empty file can make newer-format repositories unreadable. Hardened command flags remain even when a current command appears not to use a key because a future subcommand can activate it.

A dependency-manifest change returns the retryable `dependency_manifest_changed` verdict; test deletion remains a security exception. The different shapes let the engine distinguish a legitimate unsupported task from an attack. Keep the manifest glob/version policy centralized in `DevelopmentCommandProfileCatalog`.

### llama.cpp binaries

- `LlamaCppReleasePins.PinnedTag` is the offline fallback floor, not the live recommendation. Re-check archive layout on pin changes.
- Upstream ships no Linux CUDA prebuilt. Linux NVIDIA defaults to Vulkan; use `XE_LLAMACPP_SERVER_PATH` + `XE_LLAMACPP_VARIANT` or the managed source build for CUDA.
- A GPU variant may list zero devices and run on CPU. `IRuntimeDeviceAudit` degrades the effective profile only on determinate fallback; timeouts/failures are unknown and uncached. Never cache indeterminate probe results.
- Verify GitHub assets with the Releases API `digest: "sha256:…"`; there are no sidecar checksum files. Search recursively for the executable because archive layout varies.
- GPU offload has one owner per mode: GPU explore emits `--fit on --metrics` and no explicit placement; replay emits frozen `-ngl/-ts/-ot` and no `--fit`; CPU emits neither. Validate actual GPU work with the smoke.
- `LlamaCppSourceBuildRequestValidation.Normalize` must be idempotent: `Normalize(Normalize(x)) == Normalize(x)`.
- Helper tools (`llama-quantize`, `llama-perplexity`) are resolved **by name beside the resolved `llama-server`** (`LlamaCppToolBinaries`, surfaced as `LlamaBinary.QuantizerExecutablePath`/`PerplexityExecutablePath`, evaluated on read). Presence is never a precondition for adoption — a runtime missing one still serves; the caller that needs it fails specifically. The prebuilt archives carry `llama-perplexity` but no `llama-quantize`. **Both source-build paths only gained `llama-perplexity` when their cmake `--target` lists were widened (`CudaBuildService`, `LlamaCppSourceBuildService`) — any tree built before that, including the box's own `~/cuda-llama/b10201`, has `llama-server` and nothing else, so benchmark fidelity there must rebuild the runtime or point at a prebuilt.** (`CudaBuildService` never built `llama-quantize` either.)
- Emit `reasoning_budget_tokens` only when the template provides a think-end marker. At the pinned build, higher `--log-verbosity` is more verbose; use `-lv 5` for reasoning-budget traces. Empty logs at `-lv 1` are a threshold symptom.


`IRuntimeDeviceAudit` and its probe cache are keyed by binary path + mtime. A timed-out/failed probe is `unknown`, never `no GPU`, and is not cached; otherwise a transient failure would permanently force CPU sizing. A determinate `cpuFallback` degrades only the **effective** profile to RAM sizing while the hardware endpoint still returns raw hardware plus audit details. Compute the audit lazily and outside per-spawn hot paths.

**Exact launch ownership:**

| Mode | Required placement/offload args |
|---|---|
| GPU explore | `--fit on --metrics`, explicit `-c`, optimized KV/FA; **no** `-ngl/-ts/-ot` |
| Frozen replay | stored `-ngl/-ts/-ot` verbatim; **no** `--fit` |
| CPU | explicit `-c`, `-t/-tb`; no fit or GPU placement |

A missing `-ngl` is correct in explore and a defect in replay. `BuildLaunchSpec` receives the central `LlamaServerLaunchPlan`; do not scatter defaults back into supervisors/call sites. `--fit` and explicit placement cannot coexist, while `-c/-fa/-ctk/-ctv` may coexist with fit.

**Optimized launch fallback:** GPU normal launch tries `-fa on -ctk q8_0 -ctv q8_0`. On readiness failure it retries **once** without explicit KV types and with `-fa auto`. Persist `llama-launch-fallback.json` per backend only if the safe retry succeeds; a broken model must not poison future launches. Frozen profiles bypass fallback. Capacity/advisor estimates stay f16-conservative because the safe path may run.

Every normal spawn pins `--no-warmup --parallel 1`; default parallelism multiplies KV reservations and warmup can consume the readiness budget. Every role receives explicit context. CPU never replays a GPU profile. After readiness, read `/props.default_generation_settings.n_ctx`, store it on the running process, and feed it to both `TurnPolicy.ContextCapacityTokens` and inner `num_ctx`; a per-send override still wins, while unknown providers use 8192. Do not substitute train context or requested context for the launched/effective process window.

Managed source builds must retain `llama-fit-params`; normal startup logs do not reliably emit the machine-readable fit payload. Missing/incomplete helper output stays conservative/Explored. Never enable `--ui-mcp-proxy`; the product integrates MCP above llama-server.

### stable-diffusion.cpp managed source builds

- Eject before build/remove; activity returns `409 runtime-busy` and must not be bypassed.
- An installed managed-runtime record is authoritative, including invalid tombstones. Recovery is explicit remove/rebuild; keep eject/remove reachable even when Development Mode is disabled.
- Never substitute CPU bytes for a requested GPU backend.
- Source builds inherit only the scrubbed allowlist and isolated Git home; disable prompts, close stdin, fetch explicit revisions by SHA, and do not rely on default-branch checkout.


Stable Diffusion's installed record is a fail-closed tombstone as well as a success record. Path/SHA/backend/permission drift must not silently fall back to a prebuilt that contradicts the UI. Clear in-memory availability only to stop advertising dead bytes; require explicit remove/rebuild for recovery. Mutation subprocesses use explicit SHA fetch, isolated Git home, closed stdin, disabled prompts, and scrubbed compiler/loader/credential environment.

### Per-node state must never be written to the install directory

Route per-node state through `INodeDataDirectory`; packaged install directories may be unwritable/replaced. A runtime path under the project root must be ignored **and** excluded from MSBuild globs (`Compile`, `Content`, `None`, `EmbeddedResource`) in the same change. `.gitignore` does not stop the Web SDK from compiling or publishing model-written files. Run `git status --short` after the first local feature run.


The Web SDK globs project content independently of Git. A runtime directory under the Client tree needs four exclusions (`Compile`, `Content`, `None`, `EmbeddedResource`) as applicable, not only `.gitignore`. Otherwise untrusted Development workspace C# can compile into the host, and generated images/settings can ship in installers. `INodeDataDirectory` is the preferred fix because it moves state outside both Git and publish globs.


The build-configuration barrier sits **one directory above** each workspace. Putting empty `Directory.Build.*` files inside the repository makes them appear in `git status`, changes the attempt manifest/subject hash, and can be applied back to the operator repository. A repository's own file still wins because the MSBuild upward walk stops there.

### A Development Mode workspace inherits MSBuild config from ABOVE the node data directory

`Directory.Build.props`, `.targets`, and `.Packages.props` walk upward. `DevelopmentWorkspaceProvider.EnsureBuildConfigurationBarrier` writes empty barriers **one level above** the workspace. Keep them outside the workspace so patch evidence stays clean. A container mount already forms the boundary; a green Docker test does not prove the process provider.

### The Development attempt budgets: `MaxOutputTokens` is per CALL, reported usage is per ATTEMPT

Never compare cumulative attempt output with a per-provider-call ceiling. All roles use `DevelopmentAttemptOutputBudget.Accept`; extend that helper for new roles.

### A Development attempt failure must carry a code, like the validation gate's does

Engine-authored failure detail must use `DevelopmentAttemptEvidenceException` (stable code + clamped reason). Arbitrary exceptions remain generic so model text/host paths do not enter operator records. Failed-attempt writes currently remain in the task workspace; the next attempt inherits them—an unresolved lifecycle limitation, not permission to expose raw exceptions.


`DevelopmentAttemptEvidenceException` reasons are limited to the 1,024-character `terminal_reason` column. Engine-authored code/detail may be surfaced; arbitrary exception messages remain the fixed generic sentence because they can contain model output and absolute host paths. Evidence persists only after an attempt passes its checks. The workspace remains per-task, so failed-attempt writes carry into the next attempt's changed-file manifest; the reason explains this, while rollback-vs-per-attempt-branch remains unresolved.

### Serilog silently severs OTLP log export — `writeToProviders` must stay `true`

`AddSerilog(..., writeToProviders: true)` is load-bearing: otherwise logs stop before the OpenTelemetry provider while traces/metrics remain healthy. The Aspire resource name is `app`. `SerilogProviderForwardingTests` asserts that a second provider receives events; preserve the observable test rather than only the flag.


When diagnosing “traces but no logs,” first prove `OTEL_EXPORTER_OTLP_ENDPOINT` exists on the `app` process. Kestrel/Hosting emit startup `ILogger` records unconditionally, so no startup logs with healthy traces indicates provider forwarding, not idle workload. `Program` clears providers before `AddServiceDefaults`, so `writeToProviders:true` does not duplicate a second console logger.

### Other silent-failure traps

- Native probes need a per-call timeout plus an outer deadline that degrades safely.
- Desktop mode treats absent Ollama as expected; gate or tolerate connection refusal.
- Persist the desktop loopback port so origin-scoped `localStorage` survives relaunch.
- Desktop shutdown needs explicit SIGHUP and Windows `CTRL_CLOSE_EVENT` handling.
- Linux publish is self-contained single-file. Windows is framework-dependent, requires x64 ASP.NET Core Runtime 10.0.11+, and must not ship runtime binaries/licenses. Client trimming remains off.
- Desktop behavior is opt-in via `XE_LAUNCH_MODE=desktop`; off-flag behavior must remain unchanged.

### The node is an MCP server too, and four things about it will bite you

- `[McpServerTool]` parameters are required unless they have defaults; nullable alone is insufficient. Injected parameters precede optional ones because they cannot have defaults.
- Mount MCP inside `/api/local/v1` so `LocalApiSecurityMiddleware` applies.
- The `McpServer` policy names only `McpApiKey`; an operator JWT must not authorize inbound MCP.
- Store only the SHA-256 digest. Plaintext is returned once by generate; GET uses a status DTO with no key. Keep AEAD integrity protection and both persistence interceptor branches.
- The inbound surface and outbound `mcp/servers` registrations are different directions. HTTP transports require both http/https and exact configured loopback hosts; do not broaden to generic `IPAddress.IsLoopback`.


The inbound MCP key digest remains AEAD-encrypted for integrity, even though secrecy of a one-way digest is not the goal: a database writer must not be able to substitute a digest with a known preimage. Keep both read/write interceptor branches for `mcp_api_key_hash`; removing only one breaks table materialization. The generate endpoint returns `GeneratedMcpServerApiKeyResponse`; status returns a type with no key field, so the type boundary—not a comment—prevents accidental recovery semantics.

### Tool calling has FIVE independent gates, and the UI shows only one of them

Walk these in order:

| # | Gate | Meaning |
|---|---|---|
| 1 | `request.UseLocalTools` | per-message toggle |
| 2 | `enableTools` | node setting |
| 3 | `resolution.SupportsTools` | model template capability; the `TOOLS` chip |
| 4 | `AgentHome:ToolCapableModels` | operator allow-list |
| 5 | agent `AllowedToolNames` | offered ∩ allowed; Default Assistant has none |

Gate 3 is capability; gate 4 is permission. Installed capable models are auto-registered, but non-advertising/Ollama names may still need gate 4. Matching/casing remains exact at the offer boundary. Partial failure is possible: the small production arithmetic/time tools can work while coder, KB, sub-agent, and MCP tools are filtered.


Gate 4 is read live through `CachedNodeSettingsStore`; do not capture it at DI composition. Auto-registration unions a newly installed template-capable descriptor into persisted settings using the descriptor's casing. The persisted setting replaces shipped defaults, so exact offer matching and registrar case behavior must remain aligned. Gate 5 remains independent: Default Assistant intentionally grants no tools; use a suitable bound agent for positive-path tests.

### Passing all five gates is still not enough — llama.cpp must be able to COMPILE the tool schemas

llama-server compiles the whole tools array to GBNF and has a combined repetition ceiling. `LlamaGrammarToolSchemaCompatibility` sanitizes only the llama.cpp wire; handler validation and other providers retain full schemas. Do not weaken domain constants to fit the grammar.

Use `scripts/run-tool-grammar-smoke-local.sh` with a **non-reasoning, tool-capable** GGUF. The sanitized offer must return 200 and the unsanitized negative control must still return the grammar 400. If the control returns 200, the smoke is inert (reasoning model) or llama.cpp changed its limit; re-measure `MaxGrammarRepetitionBound`. FakeOllama E2E cannot validate this.


`LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound` is empirical for the **whole production offer**, not an upstream constant or per-field limit. Third-party MCP schemas make this an open boundary. If sanitization still fails, translate it to `FailureCategory.ModelCapabilityUnsupported`; do not surface the raw sampler error as a model defect. The live smoke's unsanitized negative control is load-bearing: a 200 means either a reasoning template skipped GBNF or upstream changed the limit.


Schema bounds are advisory to the model but handler validation is authoritative. Removing a large `maxLength` from the production schema would also weaken `ToolArgumentRepairAIFunction` and lie to non-llama providers. The compatibility projection strips only the offending wire keyword for llama.cpp. `ChatLocalToolsE2ETests` uses scripted FakeOllama and cannot compile GBNF; unit tests enforce the measured constant but cannot detect an upstream limit change.

### A work session is chat turns in a loop, and it takes the node's only invocation slot

- Drive `INodeChatStreamService.SendMessageAsync`, not `IInvocationRunner`, so conversation persistence, approvals, resume, and terminalization remain intact.
- Pause/cancel/deadline must call `INodeChatStreamCancellationRegistry.TryCancel`; merely stopping enumeration leaves the run and node-wide slot alive. Persist terminal writes with `CancellationToken.None`.
- `MaxConcurrentSessions` is admission only; `WorkerEventDispatcher` still serializes invocation node-wide. Bound parks with `MaxParkedSeconds`.
- Before each step, `WorkSessionStepContextBound` projects history and force-compacts over `StepContextBudgetTokens`; checkpoint compaction is too late and keeps too much.
- A step's inner tool loop resends earlier results/reasoning. Cap with `MaxProviderCallsPerStep`; hitting it ends the **step** as outcome `ProviderCallBudget`, not the session.
- `StepEnded`/`StepFailed` detail carries content-free totals from the caller-seeded `ProviderCallCapScope`. Do not read the run's inner `AsyncLocal` after enumeration or record the last-round `UsageSnapshot` as a turn total. Cancelled steps omit counts because unwind races them.
- `ToolResultBudgetScope` is ambient, tighten-only, and must be seeded before enumeration. All ClientLocal/Custom/MCP tools wrap through `BudgetedToolResultAIFunction`.
- Both context budgeters use calibrated estimates for the **upcoming** model. Calibration may tighten, never widen. Observed usage is written at the provider-round boundary where estimate and report describe the same request.
- Historical `TextReasoningContent` is dropped by Chat Completions adapters but replayed by Responses/Codex. Keep conservative counting; do not add provider-specific suppression.
- Use a new DI scope per store write; tool handlers mutate the session mid-turn. Checkpoint operation IDs are unique per checkpoint, not per step.
- Session tool authority resolves from ambient conversation-to-session context, never a tool argument.


**Step persistence and accounting:** every completed or call-budget-ended step writes `StepEnded`; faults write `StepFailed`; cancellations intentionally omit usage because the runner can still be unwinding. `DetailJson` totals come from the caller-created `ProviderCallCapScope`, which the inner send path registers into by reference. An `AsyncLocal` assigned inside the send does not flow back to its caller, so reading `ProviderCallBudget.Current` afterward returns null. `InvocationRunner.UsageSnapshot` is the **last provider round**, not a cumulative turn total; never put it beside step totals as though they reconcile.

The call ceiling ends the current step with `ProviderCallBudget` and settles as completed, preserving tool writes and the next state block. Turning it into `StepFailed` destroys a session for hitting its own safety rail. Move the ceiling only from recorded step rows, and remember reasoning replay is not bounded by tool-result arithmetic.

**Estimate calibration:** `ProviderCallBudgetChatClient` is the only seam holding the estimate and provider-reported prompt count for the same round. Write calibration there. Store below-neutral observations so an old tightening can clear, but apply calibration tighten-only. `options.ModelId` is intentionally populated by `InvocationAgentFactory` and is the shared key for routing, `/tokenize`, and observed usage; do not add a fallback model guess.

**Compaction and replay:** force compaction occurs before the upcoming turn, using that upcoming model's calibration. The previous assistant model is fallback only when the new agent/model cannot be resolved. State tools rebuild authoritative session state from the database, so the keep window can be two. The ordinary checkpoint happens after a step and keeps eight messages; it cannot replace the pre-step bound.

Every store write uses a fresh DI scope because state tools update the session row mid-turn and a long-lived DbContext holds a stale row version. Checkpoint operation IDs are unique per checkpoint phase: park-timeout and pause can share a step number and must not deduplicate each other.


`ToolResultBudgetScope` is tighten-only against the node-wide ceiling captured when ClientLocal, Custom, and MCP registries are constructed. All three sources wrap through `BudgetedToolResultAIFunction`; do not re-read options independently or add a source-specific bypass. Seed the ambient scope before `await foreach` begins, because the send path captures execution context when enumeration starts.

The two context budgeters intentionally over-count historical reasoning for Chat Completions: that adapter drops `TextReasoningContent`, while Responses/Codex must replay encrypted reasoning under `store=false`. A provider-specific “optimization” that stops counting it can under-budget after a configuration change. Keep the safe over-count.

Session tools derive session ID from `AgentRunConversationContext`'s conversation ID plus a store lookup, never from model-supplied arguments. That is the authority boundary that makes state tools inert in ordinary chat. The profile opt-in controls offering, not identity.

### Capability detection is a substring scan of the chat template — know what it can and cannot see

- Graded thinking and `native_reasoning` are separate, mutually exclusive capabilities. Harmony markers (`<|channel|>analysis`, `reasoning_effort`) must not enter the graded branch or receive unsupported `think` kwargs.
- The tools detector includes the bare word `tools`, including comments/dead branches; a `TOOLS` chip is weak evidence and does not satisfy gate 4 or 5.
- Work-session create/repoint checks gates 3 and 4. A gate-4 failure at step start pauses via `StepEnded/ToolGate`, not `Failed`, so the operator can repair and resume. Gate 4 stays live per offer; do not cache it in a constructor.


A work session refuses tool-gate failures at the boundary without making them irrecoverable. Create/repoint checks template capability and operator allow-list; before each send, the supervisor re-checks the live allow-list only (the offer still enforces it). Refusal checkpoints and transitions to `Paused` with a distinct `ToolGate` phase so repoint/fix/resume remains possible. A deleted definition is not judged, and a transient store failure logs and proceeds because the actual offer still fails closed.

### Windows is a shipping target, and an inline `OperatingSystem.IsWindows()` is how its branches go untested

Inject/parameterize platform decisions and unit-test both branches. Prefer managed implementations that delete the branch. Keep these Windows facts:

- `find.exe` is the DOS tool, GNU `grep` is absent, and an RC cannot require Git for Windows.
- `wmic` is optional/deprecated; use absolute in-box Windows PowerShell with `Get-CimInstance`, retaining wmic only as last candidate.
- `git diff --check` needs per-path CRLF policy from `git ls-files --eol`; do not globally waive whitespace.
- DPAPI key resolution must fail closed; stock DataProtection silently creates a new key on unreadable rings.
- Coder surveys remain provider operations because only the provider maps sandbox paths safely; default methods throw rather than return a misleading empty listing.
- Use the Windows RC runbook for evidence that Linux cannot produce.


Platform abstraction shapes to copy are `ProcessGpuVendorProbe.ProbePlatform`, `IHardwareProbeEnvironment.IsWindows`, and `NodeDataProtectionKeyRingFailClosed.ResolverFactoryFor(bool)`. A Linux-only test that skips elsewhere is not Windows evidence.

`git diff --check` cannot simply be deleted for CRLF repositories. Derive `cr-at-eol` per path from `git ls-files --eol`'s index (`i/`) state and write workspace-local `.git/info/attributes`. For Windows hardware inventory, call absolute in-box Windows PowerShell and `Get-CimInstance`; retain `wmic` only as a last immediate-failure candidate. DPAPI failures surface as `CryptographicException`, so fail-closed classification differs from the non-Windows scheme.

Development surveys know the host workspace and call managed `WorkspaceFileScanner`. Coder surveys remain `ISandboxRuntimeProvider.ListFilesAsync/SearchTextAsync`; exposing the jail's host path to “share” implementations bypasses provider confinement. Default interface methods throw so an unsupported provider cannot report a misleading empty workspace.

### Measured on a real Windows 11 box, 2026-08-03 — five traps that make a Windows run lie to you

- Compare `(cmd /c "echo %PATH%").Length` with `$env:PATH.Length`; a bloated persisted PATH can appear empty to `cmd.exe` and make timeout tests fail instantly. Set `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0` in isolated CLI homes.
- Check `git diff --stat` and `git ls-files --eol <path>`; Windows editors can rewrite whole LF files to CRLF.
- Launch pnpm through `cmd.exe /c` under `UseShellExecute=false`; `CreateProcessW` cannot execute `.CMD` shims directly.
- Deprecated `package-tester-win.ps1 -SkipUpload` still needs `VPK_TOKEN` for delta inputs and is not credential-free.
- The private tester GitHub App/device-flow requirements are retired and must not return to the public updater.
- Verify packaged policy files explicitly; `CopyToPublishDirectory=Always` can leave a disturbed destination missing until the source timestamp changes.
- Use junction tests for directory reparse guards; symlink tests may skip without Developer Mode. Junctions cannot prove a file-swap guard.

---

## 3. Models, inference, retrieval

### Recommendation: walk the quant ladder, never pick one quant

Both advisor lanes use `GgufFileSelector.SelectBestFit`: rank all files by `QuantLadder`, choose the best quant at/below the ceiling that fits, then descend toward the floor. Do not duplicate selection in either lane.

MTP draft files are a distinct identity (`MTP-<quant>`), not filtered-away base variants. Recognize only an `MTP/` path segment or `mtp-` filename prefix; do not match a bare `MTP` in a real base-model name. Drafters sort last, are never advisor picks, register as Draft, stay out of chat defaults, and remain selectable in the draft slot.

### Recommendation ranking is capability-bucketed

Explore ranking is GiB size bucket → downloads → last modified → trusted publisher → repo ID; raw size/headroom ordering regresses to tiny models. Catalog ranking is tier → MoE verdict → quant quality → release date → ID. “Recommended” requires positive headroom and quant at/above Q4_K_M.

### The advisor is two lanes, and one may fail

Catalog and live-HF explore lanes concatenate. A catalog failure degrades to empty and must not fail explore.

### MoE models need `MoeFacts`, not naive VRAM math

Use `MemoryFitEstimator.MoeFacts(ActiveParamCount, ExpertCount, ExpertUsedCount)` and prefer curated active parameters. Total-weight-only math rejects/mis-scores MoE models; expert count alone uses the deliberate conservative share.

### Multi-part GGUF shards are ONE model

`HuggingFaceGgufDiscovery.GroupShards` groups `<base>-00001-of-00003.gguf`, sums sizes, uses the first split, and drops a shard group if an equivalent merged file exists. Later splits are headerless and never candidates.

### NVFP4 **GGUF** runs here, natively — NVFP4 **safetensors** never will

NVFP4 GGUF is recognized/ranked and supported by the pinned llama.cpp; compressed-tensors/ModelOpt NVFP4 safetensors cannot be converted by the current script. Check the container before claiming the format is unsupported. Unknown quant tokens make files disappear, not merely misprice, so add future native formats to parsing/usability together. Use real blob size when available; the 4.25 bpw estimate is empirical.

### GGUF filenames from a repo are untrusted input

Require `GgufFilePath.IsSafeRelativePath` and `ResolveContainedPath` immediately before open. Reject roots and `.`/`..` segments; never trust Hub sibling names.

### HuggingFace API facts that bite

Use `filter=gguf`, not `library=gguf`. `gated` is a string union. Request `?blobs=true` for sizes. .NET strips `Authorization` on cross-host CDN redirects. Filter `mmproj*.gguf` companions everywhere.

### Model kind classification

`ModelKind` includes Reranker. Check reranker names before embedding names. Capability classification cache is digest-keyed; an Ollama capability change without digest change remains cached.

### Local-default chat resolution must stay Ollama-blind

Resolve only installed llama.cpp GGUF Chat models from persisted classification. Never live-probe Ollama on the send hot path; no chat model is `ModelNotInstalled`, not provider-unreachable.

### Reasoning ("think") has counter-intuitive Ollama semantics

For models without Ollama's `thinking` capability, `think:true`/levels 400, `think:false` suppresses template-default reasoning, and omission preserves it. Therefore requested reasoning → omit; off/unspecified → false. Thinking-capable models receive false/levels.

Keep `InvocationAgentFactory` and `ParticipantReasoningOptions` behavior aligned. A new effort value also updates both factories, `ReasoningEffortNormalizer`, `RuntimePackageValidator`, and `RuntimePackageConfigHash`.

### Capacity gate: dispose the reservation

Only local `CapacityDecision.Allow` carries an `IDisposable` reservation. Dispose it when the child exits or pending bytes leak and later spawns reject. Warm `IRuntimeDeviceAudit` before entering the ledger decision gate.

### GPU-load admission gate (AUD4-06)

One process-wide `IGpuModelLoadAdmission` serializes GPU model/image loads. Acquire inside detached spawn after backend selection; CPU and warm reuse bypass it. Release on readiness/failure. Timeout is typed/non-retryable. Never hold the capacity ledger gate while waiting for this gate, and never keep it through a benchmark body. Provider projects register no-op floors; the composition root installs one shared real singleton.


`IGpuModelLoadAdmission` is acquired only for selected GPU backends inside detached spawn. Ticket lifetime ends at readiness/failure, not benchmark execution. Capacity admission releases its ledger decision gate before spawn waits; `RunExclusiveProfilingAsync` may hold a per-key ensure gate but never the GPU-load gate during the benchmark body. Provider-only hosts get `NoOpGpuModelLoadAdmission`; application composition registers one real last-wins singleton shared by llama and image supervisors. Metrics distinguish wait time, timeout count, active, and waiting.

### llama-server spawn invariants

- `--fit on` and placement (`-ngl/-ts/-ot`) are mutually exclusive. `-c/-fa/-ctk/-ctv` are not placement.
- Recover fitted placement only through `llama-fit-params`; missing/incomplete evidence stays `Explored`. Managed source builds retain both executables.
- Never enable `--ui-mcp-proxy`.
- Every spawn pins `--no-warmup --parallel 1`.
- Central policy precedence: frozen replay > explicit per-send config > role defaults. Profiling passes no policy.
- Every normal spawn emits `-c`. Shipping chat uses stable capacity tiers; pooled roles use 2048; CPU also gets `-c` and CPU thread flags, never GPU placement.
- GPU default is `-fa on -ctk q8_0 -ctv q8_0`, with one safe fallback and a backend-specific persistence record only after fallback succeeds. Estimators remain f16-conservative because fallback may activate.
- Read effective `n_ctx` from `/props` after readiness and feed both outer and inner budgeters. Per-send override wins; unknown providers fall back to 8192.
- Rerank and embedding need separate processes for the same model. Rerank failures degrade to RRF order, matching scores by returned index.


**Policy and argument precedence:** `LlamaServerLaunchPolicy` owns the launch plan. Frozen inference-profile replay is verbatim and cannot be overridden by per-send/node defaults; explicit per-send options outrank role defaults only for non-frozen launches. Operator profiling passes a null plan so experimental args are not rewritten. CPU threads are estimated from physical cores (logical/2 when SMT assumed) minus host reserve; GPU launches emit no CPU-thread override under the current policy.

**Context tiers and role split:** the composed app chooses chat context from stable tiers `65536/32768/16384/8192/4096/2048`, capped/aligned by train context; embedding/reranking use 2048. `ChatContextTokens=16384` is only a provider-only composition fallback, not the shipping fixed window. A reranker is a distinct `--rerank --pooling rank` server from the same model's `--embeddings --pooling mean` server, and rerank scores map by returned index, never response order.

**KV accounting split:** quantized KV may reduce actual GPU use, but automatic context tier selection remains fp16-conservative. Benchmark requests may name a frozen KV type for ledger sizing only after the tier was chosen under fp16; unknown KV tokens degrade to fp16. The default allocation-cache key must remain byte-identical when KV type is null so chat does not fork into a new identity.

### llama-server readiness, load lifetime, and eject (Audit-4)

- Warm local models before arming the stream-idle watchdog. Cloud/Ollama warm is no-op. Add new `InvocationState` fields to `Clone()`.
- Readiness timeout is model-size-aware. Retry a live-but-slow timeout only by `MaxReadinessTimeoutRetries`; process exit is deterministic/non-retryable.
- Spawn is per-key single-flight and detached from the first caller's cancellation under shutdown authority.
- Operator eject marks evicting, drains leases, and returns `Ejected`, `TimedOutStillBusy`, `ForcedWhileBusy`, or `NotRunning`. Clear the mark on every non-teardown exit, including request cancellation. New sends refused during drain throw `LlamaServerModelEjectedException`; internal `EvictAsync` remains immediate.
- Reapers/LRU never kill an active inference lease. Provider unload evicts all roles; chat warm/runtime-info remain chat-only.
- Readiness probes use a dedicated resilience-free `HttpClient` with ~1-second attempt bounds; shared resilience delays detection.


**Readiness retry is not process restart:** `ResolveReadinessTimeout(modelBytes)` derives a capped size-aware deadline. If the process remains alive but slow, retry readiness at most `MaxReadinessTimeoutRetries` (default 1), not `MaxRestartAttempts`. A process that exits during load is deterministic and non-retryable. The detached `_inflightSpawns` task runs under shutdown authority; a caller cancellation abandons only its `WaitAsync`, preserving the single-flight warm load for other callers.

**Eject/lease tri-state:** `TryAcquireInferenceLease` returns granted, `refused-evicting`, or `refused-absent`. Absence may proceed to the normal ensure/spawn path; evicting must fail immediately as `LlamaServerModelEjectedException`, otherwise the request slips under the drain, is killed, and self-heals by respawning the model the operator just ejected. A force-ejected active request maps to Cancelled/operator-ejected, not provider failure. The eject mark is cleared on every path where teardown did not complete—including cancellation of the eject HTTP request mid-drain—or every later lease remains refused forever. Idle/cap reapers never kill an active lease.

Provider unload calls `EvictAllRolesAsync` over `Enum.GetValues<ModelRole>()`; interactive warm/runtime-info remains chat-only. Readiness/liveness probes use a dedicated resilience-free `HttpClient`; app-wide retry handlers turn a one-second probe into multi-second uncertainty.

### Context allocation is a stable process decision, not a live-memory sample

- Distinguish process `-c`, per-request limit, train-context ceiling, and frozen replay.
- Choose automatic tiers from stable GPU+RAM allocation evidence; global-free VRAM is admission/invalidation, process-budget VRAM is allocation.
- Precedence: frozen > deterministic override > automatic. Only classified startup OOM may down-tier automatic allocation, at most twice.
- A caller-named required context must reject rather than down-tier below the requirement. Process-lifetime sticky adjustments can otherwise permanently poison later admissions; reservation-scoped adjustment remains open work.
- Benchmarks wait for capacity via `BenchmarkCapacityAdmission` (24×5 s default). Direct executor tests pass zero retries or sleep two minutes.
- Launch-policy fingerprint includes stable launch identity, never live free VRAM.


**Required-context sticky hazard:** `_adjustedAllocations` is process-lifetime, monotonically downward, and is not cleared on eject/reservation release. If admission down-tiers below a caller's `RequiredContextTokens`, `TryCommitAdmissionFootprint` pins the smaller context and every later required-window admission fails until app restart, often through the misleading generic “footprint could not be determined” path. Therefore `CapacityService.DecideAsync` rejects instead of down-tiering below a named requirement. Scoping adjustments to reservation lifetime and discriminating unknown-footprint causes remain follow-ups.

**Shared benchmark admission:** both `BenchmarkRunExecutor` and `BenchmarkJudgeExecutor` call `BenchmarkCapacityAdmission.AdmitAsync`; neither implements a private retry loop. Default is 24 retries × 5 seconds because the preceding FIFO phase may still be releasing VRAM. Every direct executor unit test injects `new BenchmarkAdmissionRetry(MaxRetries: 0, TimeSpan.Zero)` or a rejection test sleeps two minutes. Re-evaluate capacity each attempt; do not reserve speculative bytes between retries.


The four context values must stay separately named through APIs and DTOs: launched allocation (`-c`), request budget/limit, train-context maximum, and frozen replay override. A request can reduce but cannot enlarge an existing process. Frozen/deterministic allocation never silently mutates after failure; only automatic hardware selection may OOM down-tier, and only for classified startup OOM, at most twice per allocation identity.

### Benchmark launch evidence (KV-cache type feature, 2026-08-16)

- Benchmark placement capture emits `-lv`; serving logs still demote afterward.
- `LlamaServerPlacementOutcome.None` means 0/N GPU layers and remains appended in enum order.
- Benchmark launch receipts are post-readiness, non-throwing, path/host/port-free. `LlamaServerLaunchProjection` is the single argv projection and its member order/name is persisted identity; do not change without intentional migration.
- `BenchmarkCanonicalJson` keeps camelCase string enums.
- Launch-ready checkpoint is insert-if-null and accepts Running/current version or Cancelled/current+1. It never changes status/version and never fails a measurement.
- Replay uses one strict launch candidate—no safe KV fallback or fallback-store write. Unsupported explicit KV returns 422. Capacity sizes frozen runtime context/KV; non-benchmark remains f16-conservative.
- Receipt projection is parsed from final argv; omitted optional flags are explicit receipt evidence. Aux assets record booleans only, never paths/digests or implied rankability.
- K/V KV types must be ordinal-identical.
- After EF migration remove/re-add, compare with the preceding designer/snapshot and run migration-chain tests. Interleaved branch timestamps may pass up and destroy columns down; consolidate unshipped migrations at the chain tail and run the full persistence suite.
- Read stored benchmark blobs with `BenchmarkExecutionSerialization`, the writer's Web/camelCase options.
- Judge response schemas are bound-free (`min/maxLength`, `pattern`, item bounds absent) and travel through `ResponseJsonSchema`, not tools. Keep null/non-judge behavior and config hash unchanged.
- Read llama-server final-chunk timings via catch-based `JsonPatch` field reads. `Contains` may be false while `Get*` succeeds. Tokens/sec is decode speed when timings exist and display-only.
- `RuntimeBundleIdentityCalculator` and its framed hash are shared/persisted; changing framing invalidates launch fingerprints.


**Receipt identity and checkpoints:** `LlamaServerLaunchProjection.From(...)` is the sole renderer for context/placement/thread argv and `ComputeIdentity()` input. The receipt reads the projection back from final argv (last-wins parsing) so capability-gate omissions are observable; freeze-time intent stays separate. Add receipt facts freely only when they are outside the persisted projection identity. `MarkPrimaryLaunchReadyAsync` / `MarkJudgeLaunchReadyAsync` are insert-if-null and accept Running at claimed version or Cancelled at claimed+1 because terminal cancellation increments exactly once. They use `CancellationToken.None`, log failure, and never fail the measurement.

**Strict benchmark launch:** a replay uses one candidate and never writes the normal safe-KV fallback store. Unsupported explicit KV or CPU-incompatible quantization returns `BenchmarkUnsupportedKvCacheTypeException` / 422. Capacity uses the frozen runtime context, not requested sampling context. K and V cache types must be ordinal-identical for the fused path.

**Persistence/migration traps:** benchmark JSON readers use `BenchmarkExecutionSerialization`, the same Web/camelCase options as writers; default serializer options silently produce zeroed DTOs rather than throwing. After `dotnet ef migrations remove`, compare the regenerated migration/snapshot with the immediately preceding designer. Up-only `MigrateAsync` is insufficient when branch timestamps interleave: SQLite down migrations rebuild tables from each migration's own target model and can delete sibling columns. Consolidate unshipped migrations at the merged tail and run the full Persistence test project, including rollback.

**Timings:** final streaming chunks contain llama-server `timings`. `JsonPatch.Contains` is reliable only for the outer object; nested values may be readable while `Contains` is false. Catch missing-field `KeyNotFoundException`. Sum every provider request in a turn—including tool and approval loops—and persist `segment_count`; throughput is display evidence, not rank input.


`BenchmarkCanonicalJson` uses `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` before hashing receipts/environment. Integer ordinals allow enum insertion to silently reinterpret stored evidence. `LlamaServerPlacementOutcome.None` was appended to preserve ordinal history and maps to telemetry `none` / benchmark backend `cpu-fallback`.

Judge constrained output travels through `RuntimePackage.ResponseJsonSchema` → `InvocationAgentDefinition.ResponseJsonSchema` → `ChatOptions.ResponseFormat`; it is not a tool and therefore bypasses tool-schema sanitization. The caller keeps the schema free of repetition bounds. Keep the field null on non-judge requests and outside runtime config hash because it is a per-send decoding constraint.

### Benchmark readiness (repeat groups, launch matrix, export — 2026-08-17; optimization pass 2026-08-25)

- Prompt text is not hashed; every judge-system-prompt wording change bumps `BenchmarkJudgePolicyVersions.PromptVersion`. Reads tolerate old versions; writes/execution validate strictly and UI surfaces outdated status.
- Start a repeat group with one transactional `StartRunsAsync` call and one project-version check; per-run insertion creates orphan partial groups.
- Listing trusts recorded model facts for speed; freeze hashes them. Map snapshot errors to 422 eligibility errors.
- Rank exclusion precedence is warm-up > user score > truncation > judge reasons, applied on every returned run. Use the shared rankable verdict for denominators.
- Freeze `invocation_timeout_seconds` onto the run; the run copy is authoritative. Legacy null defaults to 900 s.
- Sum timings across every request in the turn and store `segment_count`. Keep server-side list projection in sync with single reads/CSV/repeats.
- Compare legacy registry SHA case-insensitively. An unmapped provider defaults to `llamacpp`.
- Benchmark-readiness schema is the consolidated `AddBenchmarkReadiness` migration.
- CSV fields beginning with formula-significant characters (`= + - @ tab CR`) receive an apostrophe inside quotes.
- A member added to `BenchmarkSamplingSnapshotV1` must be nullable and `[JsonIgnore(WhenWritingNull)]`. The payload is validated by re-hashing it against its embedded `ConfigurationHash`, so a member that emits `null` changes the bytes of every row frozen before it existed and each of those runs stops replaying. `BenchmarkRuntimeSnapshotV1CompatibilityTests` holds the literal v1 payload and is the guard.
- Two stop reasons are node-DERIVED, not provider tokens: `incomplete` (a clean finish that answered nothing — ended on an unanswered tool call, or emitted only reasoning) and `reasoning-length` (`length` with no visible answer token). Both are decided in `BenchmarkRunExecutor.ResolveStopReason` from the COALESCED parts; a per-delta capture cannot show the shape of a turn. `IsTruncated` covers `reasoning-length` deliberately. Precedence is warm-up > user score > (truncated | incomplete) > judge reasons.
- `BenchmarkFreezeScope` memoizes the llama-server probe, the chosen variant, and the verified model lease for ONE launch request; the batch endpoint threads one through every cell, the single-run endpoint passes none. Holding the leases also keeps a matrix comparable. Under it `InstalledGgufSnapshotStore` memoizes each member's SHA-256 by (absolute path, length, last-write UTC); a member rewritten with both preserved is not re-detected for the process lifetime.
- `StartBenchmarkRunBatchEndpoint.RequestTimeBudget` (45 s) is checked BETWEEN cells, never inside one, so a started cell runs to completion and no cell is half-frozen; every remaining cell returns named with code `BatchTimeBudget` plus the project version to resubmit against. It reads the injected `TimeProvider` — a test swapping the host's must start it at `DateTimeOffset.UtcNow`, because the host mints that request's bearer token off the same clock.
- `BenchmarkRepeatMode.AnswerVariance` advances the seed by repeat index off one base and samples at a temperature (default 0.7); `Throughput` stays the default and byte-identical to what it always was. Both knobs are SAMPLING and must never reach the launch arguments, or the runs of one group stop sharing a launch identity. Mode, seed and temperature are duplicated onto the run as plaintext columns because listing and CSV never decrypt a snapshot.
- The event buffer keeps a tombstone per terminal run — an emptied entry still turns a late `Subscribe` replay into a reset rather than silence. Past `MaxRetainedTerminalRuns` (256) the oldest drop and the hub resets off the persisted `LastStreamSequence` instead. Queue membership is its OWN flag: a run is evicted once per terminal PHASE and has two, so keying the queue off the eviction flag enqueued each run twice and halved the effective cap.
- `PUT projects/{id}/judge` answers the re-judge precondition BEFORE building the policy, because building it takes the VERIFYING model lease that re-hashes every member file — 57 s for a 22 GB judge. An unchanged draft is recognized without a lease by rebuilding it against the STORED model identity and comparing `ComputePolicyHash`, with the model NAME compared separately. Ceiling: a judge file changed on disk under an unchanged name reads as unchanged until the policy is next actually built.
- `samplingTemperature` is `double` end to end — both start-request DTOs, `BenchmarkSamplingSnapshotV1.Temperature`, and the freeze service's constants. A float widened into the already-`double` run column recorded 0.699999988079071 for 0.7. The sampler narrows back to float at `SamplingOptions`, which is what the wire carries; serialized snapshot bytes are unchanged, so nothing re-hashes.


### Benchmark quant fidelity — perplexity and KL divergence (P2 S1, 2026-08-26)

- **`llama-perplexity`'s real output disagrees with its README in three ways, and each reads as "unparseable" rather than as an error.** Captured from b10201: a `--kl-divergence` run prints **no `Final estimate` line at all** — its perplexity is `Mean PPL(Q)` inside the statistics block; the statistics blocks separate a value from its error with `±` (U+00B1) while the plain perplexity line uses `+/-`; and top-token agreement is printed as **`Same top p`**, with the word "agreement" appearing nowhere. `BenchmarkPerplexityOutputParser` holds verbatim fixtures of both shapes. Write a parser against the binary, never against the docs.
- Perplexity is measured at a **fixed 512-token window**, never the project's context. Everything else about the run's placement (`--n-gpu-layers`, `--tensor-split`, `--override-tensor`, `--cache-type-k/-v`, `--flash-attn`) IS replayed — that is what differs between the runs being compared. b10201's `llama-perplexity` **does** accept `-ctk`/`-ctv`, so a KV-quantized run is measured under its own KV; the plan's f16 fallback was not needed.
- **The KLD disk estimate is measured, not derived.** A real 10-chunk base file for Qwen3.8-27B (`n_vocab` 151 936) was 1 266 472 900 bytes over 777 912 320 logits — **1.628 B/logit**, not the format's 2.0. `BenchmarkFidelityPolicy.KldBytesPerLogit` is 1.75 (headroom over one measurement); 200 chunks is ~25.3 GB actual, not the 31.1 GB an f16 assumption promises.
- **A `v1:<hex>` fingerprint is not a filename.** `:` is illegal in a Windows path, and NTFS reinterprets the tail as an alternate data stream rather than rejecting it — the failure would be a silently empty file. The base-logit cache is named by the first 32 hex of a digest OF the whole cache key, with a plaintext `.json` sidecar so the directory stays human-readable.
- **The KLD comparability gate is the whole cache key, not the base model's fingerprint.** Four of the key's five inputs (corpus sha, context tokens, chunk count, format version) move without the fingerprint moving, and `kld_p99` is strongly chunk-count dependent. `BenchmarkKldCacheKey` is the ONLY place that expression exists — `KldDigest_IsComputedByExactlyOneExpression` fails the build if a second file computes it. A stale figure is **withheld** by the API, not sent flagged: a number a reader can still see is a number they will still compare.
- The cache lease is `FileMode.CreateNew` + `FileOptions.DeleteOnClose`. That is what makes the CRASH case right — the OS drops the handle, so the next run takes over instead of waiting on a lock nobody will release. A bare "does the lock file exist" check gets exactly that case wrong. The write goes to `<name>.tmp.<invocationId>` and lands by same-directory rename, so a partial logit file never resolves as a measurement.
- **A new `BenchmarkWorkKind` is not additive at the lifecycle layer.** `ClaimNextAsync` and `RecoverRunsOnStartupAsync` both branched "Primary, else it is a Judge" and the else dereferenced `JudgeAttemptId`; either new kind reaching them threw `InvalidJudgeTransition` and stalled the single-consumer queue behind an item it could never claim. Both are four-arm switches now, and recovery needs **two sibling pre-sweeps** — an attempt or comparison left `Running` by a killed process whose work item a previous partial recovery already terminalized has nothing else that can reach it.
- **EF cannot express the `COALESCE(task_case_id, x'00')` unique indexes.** `HasIndex().HasFilter()` takes columns, not expressions, so it emits an index on the bare nullable column — and SQLite lets a unique index repeat NULLs, which is the hole the COALESCE closes. The three are raw `migrationBuilder.Sql(...)` and are deliberately absent from `OnModelCreating`; the migration test reads them back from `sqlite_master`, because an assertion through the EF model passes against exactly the index that is wrong.
- The work-item CHECK rewrite is a SQLite **table rebuild** (SQLite cannot ALTER a constraint). EF's `DropCheckConstraint`/`AddCheckConstraint` do produce one, and it carries `queue_sequence` values through — asserted, because that column is a FIFO position and renumbering it silently reorders pending work.
- Fidelity is measured **once per cell**, never per repeat: perplexity is deterministic given the same weights and argv, so N repeats buy N identical numbers at N× the cost. A warm-up records `fidelity_status = 'skipped'` rather than NULL — "covered by repeat 1" and "never asked for" are different facts.
- Live on this box (RTX 5090, b10201 CUDA, the executor's own argv: `-c 512 --chunks 200 --n-gpu-layers 99 --flash-attn off`): `unsloth/Qwen3.8-27B-GGUF` Q4_K_M **6.7977 ± 0.07405** vs UD-Q3_K_XL **6.9497 ± 0.07550** — separated, with the bands ending 0.002 apart and NOT overlapping. At the 50-chunk floor the errors roughly double and the two would overlap, which is why the PPL default is 200 even though KLD's is lower. (`-ngl 999` with flash-attn on auto gives 6.7983 / 6.9513 — the placement knobs move the number in the fourth decimal, which is exactly why they are replayed rather than assumed.)
- `~/cuda-llama/<tag>/build/bin` predates S0, so `llama-perplexity` is added incrementally without rebuilding anything else: `source ~/cuda-llama/env.sh && cmake --build ~/cuda-llama/<tag>/build --config Release --target llama-perplexity -j$(nproc)`.

### Benchmark verifiable rubric criteria (P2 S2, 2026-08-26)

- **A rubric criterion carries a `Kind` and a `Config`, and both are inside `ComputePolicyHash`.** `llm` is the default an absent value takes, so every criterion written before P2 keeps its meaning and its stored hash. The verifiable kinds — `exact`, `regex`, `jsonSchema`, `mathAnswer`, `constraint` — are decided by `BenchmarkJudgeVerifiers` with no model in the loop. `pythonTests` is named in `BenchmarkJudgeCriterionKinds` but refused at activation; it is P3's.
- **`verified:v1` is safe only because the mode, every criterion kind and every criterion config are in the policy hash.** A judging whose whole rubric was verified server-side spawns nothing, so it has no runtime to key on — and having none is NOT `execution-identity-incomplete`, which would unrank it forever. The constant key works because one policy revision is provably one rubric composition. Move any of those three out of the hash and the sentinel starts merging attempts that were graded differently. `MarkJudgeSucceededAsync` applies it with `??=`, so a measured key written at launch is never overwritten.
- **A mixed rubric must be parsed against a FILTERED rubric.** `BenchmarkJudgeResultParser.ReadCriteria` rejects a reply whose criteria array does not match the rubric's count exactly, so handing the model the full rubric and expecting it to skip the verified ones fails every judging. The model sees only its own criteria; the verified scores (10 or 0) are merged back and `BenchmarkJudgeScoreCalculator.Compute` recomputes the 0..100 against the full rubric — which also rejects a merge that does not cover it, so the merge is checked rather than trusted.
- **Adding a member to `BenchmarkJudgeRubricCriterionV1` changes the judge's PROMPT unless you stop it.** `BuildUserPayloadJson` serializes the rubric with `DefaultIgnoreCondition.Never`, so `kind`/`config` would appear in the payload of every judging — including a re-judge under a revision stored before they existed, which is a different question asked with no version moving. The builder strips both; the model never sees a verifiable criterion anyway.
- **Every regex a policy carries is compiled `RegexOptions.NonBacktracking`, and a pattern it refuses to compile is refused at activation.** That is the whole ReDoS answer: matching is linear in the input, and backreferences, lookaround and atomic groups are rejected while the operator is still looking at the form. Do not add a backtracking fallback — the timeout is belt, not the mechanism. Same reason the fenced-JSON scanner in `BenchmarkJudgeVerifiers` is hand-written: finding a fence needs a negative lookahead.
- **The JSON-schema check enforces a subset (`type`, `properties`, `required`, `items`, `enum`, `const`, `additionalProperties`) and REFUSES every other keyword at activation.** Accepting `minLength` and not enforcing it would ship a criterion that silently passes answers it should fail; the refusal keeps "accepted" and "enforced" the same set. No JSON-Schema dependency was added (plan §7.0).
- **A verifier that cannot run throws and fails the judging; it never scores 0.** 0 is a score an answer can genuinely earn, so spelling "unmeasurable" that way is a lie the ranking then acts on.
- `mathAnswer` extraction order is `\boxed{}` → `####` → an "answer is" phrase → the last number, most explicit first, and the last occurrence of each. A model that shows its working leaves several numbers behind and the final one it wrote is not reliably the one it meant. The value keeps commas (thousands separators) and is cleaned afterwards; excluding them from the capture read `$1,234,567` as 1. `\boxed{1/2}` and `\boxed{\frac{1}{2}}` both equal `0.5`.
- Live on this box: an all-verifiable rubric (mathAnswer + regex + exact) on Qwen3.8-27B Q4_K_M judged **score 100**, `executionKey verified:v1`, with **exactly one** `llama-server ready for model` line in the whole session — the primary — and zero mentions of the judge model. A mixed rubric with a Qwen3-32B Q5_K_M judge merged the model's clarity 10 and the verifier's 10 to **100**, and the same pair with a deliberately wrong expected value to **50** — `(50·10·10 + 50·0·10)/100`, the weighted merge asserted end to end.

### Benchmark pairwise judging and Bradley-Terry (P2 S3, 2026-08-26)

- **A pairwise score lives in ONE `BenchmarkPairwiseFit` row and nowhere else.** `BenchmarkRun` carries no pairwise columns and an architecture test keeps it that way. Publishing a fit by writing a score onto each run let a crash between run four and run five leave a ranking blended from two fits, every row internally consistent and the ordering wrong, with nothing able to detect it. Publication is one transaction — deactivate the scope's active row, insert the new one — and `ux_benchmark_pairwise_fits_active` makes two active fits per `(revision, generation, case)` unrepresentable rather than merely unlikely.
- **Staleness is ONE integer, and the ranking read opens no comparison row.** `BenchmarkJudgePolicyRevision.ComparisonSetVersion` is bumped in the same transaction that inserts a comparison or terminalizes one — the only two ways the fitted set can change — and a fit stamps the value it was fit at. It is strictly stronger than hashing the verdicts: a cancel-then-re-enqueue that lands on identical verdicts still bumps it, so a fit over a REBUILT set is stale. `FittedSetJson` stays as the audit record of which verdicts were used; the hash was never what made that answerable.
- **`ComparisonSetVersion > 0` on the current revision is also how the read knows the project judges pairwise.** It leaves zero on the revision's first comparison and never returns, and switching modes changes the policy hash, which mints a different revision starting at zero again. The mode itself lives inside the encrypted policy blob, and decrypting one per page fetch to answer "which vocabulary does this project rank in" was not worth it. Consequence worth knowing: a pairwise project with fewer than two eligible runs never enqueues a comparison, so it reads on the POINTWISE path until it has a pair.
- **`pairwise_fit_key` covers the judge execution identity, not just what was asked.** Revision, generation, policy hash, both pairwise versions, the case AND the cohort's promoted `ReferenceExecutionKey`. A cohort's verdicts can come from two judge runtimes (a llama.cpp upgrade, a different quant of the judge, a KV type changed mid-cohort), and pointwise already refuses to rank across exactly that boundary. Before fitting, the fitter requires a promoted reference key and requires EVERY fitted comparison to carry it; a single mismatch refuses the whole fit and **no partial fit over the matching subset is attempted**, because dropping comparisons changes the comparison graph and can disconnect it. Recovery is the existing mechanism: re-judge, which bumps the generation and re-enqueues.
- **A refusal is PUBLISHED as a fit row with the reason and no scores.** A fit that simply fails to appear is indistinguishable, on the read path, from a cohort still judging — and telling those apart without re-reading every verdict per page is the whole point of the one-row design. So `ScoresJson` carries one entry per ELIGIBLE run (not per fitted run) with an optional `pairwise-*` reason, and a whole-fit refusal puts the same reason on every entry. Note the shipped CHECK requires `iterations > 0 AND bootstrap_replicates > 0`, so a refusal records the sweep budget it exhausted rather than a zero the row cannot hold.
- **A comparison claims the cohort's reference execution key on its first success.** A pairwise cohort has no judge attempts to claim it, so without this every fit over it refuses as `pairwise-execution-identity-incomplete` and nothing is ever rankable. It happens inside the same transaction as the terminalization, exactly as `MarkJudgeSucceededAsync` does it.
- **Bradley-Terry is regularized with a symmetric alpha = 0.5 pseudo-count per COMPARED pair.** Complete separation — a run that wins everything, or loses everything — is routine with three runs and one clear winner, and the unregularized MLE does not exist there at all. With the prior every participating run has a win total above zero, so no `log(0)` and no division by zero is reachable. Ties split 0.5/0.5; Rao-Kupper is declined on purpose (a third parameter from far fewer observations than a 12-run cohort produces). The MM sweep converges below 1e-10 on log-strengths, capped at 500, and **hitting the cap refuses the fit** rather than publishing a half-converged number. Measured: a four-run fixture converges in 62 sweeps, so the plan's "under 60" was an estimate taken before the fit ran.
- **The mapping is `round(100 * sigma(theta_i - mean theta))`, not `100 * p / max(p)`.** The latter pins the winner at 100 forever and reintroduces exactly the saturation the pairwise mode exists to remove. A two-run cohort where A wins both orders is 69 / 31 — hand-computable from the prior, and pinned as a fixture.
- **The bootstrap resamples whole UNORDERED PAIRS, never individual verdicts.** The two presentation orders of one pair are the same observation measured twice to cancel position bias; splitting them across replicates puts that bias straight back into the interval and understates it. A run absent from a replicate contributes nothing (not a 0, not a 50), and one appearing in fewer than 200 of 1000 replicates gets NO interval rather than a fragile one. Seeded at 0, so two runs over the same verdicts report the same interval.
- **Only the largest connected component of the comparison graph is published**; runs in the others read `pairwise-insufficient`. Strengths from two components are not on one scale and averaging them invents an ordering the verdicts never established.
- **Each answer is bounded to HALF the judge window, and the cut is recorded.** `BenchmarkOutputParts.ForJudge` already halves internally, so the pairwise call passes `window / 2`; truncation is read off the marker it appends rather than re-derived from lengths, so the flag and the text the judge saw cannot disagree. A long answer is therefore cut harder here than pointwise — itself a bias — so a cohort with more than 20 % of verdicts carrying a truncated side refuses to aggregate.
- **The swap bookkeeping lives in exactly one place.** `BenchmarkPairwiseResultParser.ToCanonicalVerdict`: with `Order = 1` the canonical B was shown first, so a verdict of `a` means B won. Getting it backwards inverts exactly half of every cohort's verdicts, which is precisely the half the swap exists to produce.
- **`EnsurePairsAsync` is called from exactly three places** — judge-policy activation, a primary's success terminalization, and startup reconciliation — and it is transactional, idempotent and unique-violation-tolerant. A terminal-FAILED slot is free again (that is what the status-filtered live-slot index is for) and its retry is a new row at `attempt_sequence + 1`. The cap is 12 eligible runs, i.e. 132 judge calls; past it nothing new is paired and the excess runs read `pairwise-cap`, because a sampled sub-tournament would be a silently biased one.
- The work item of a comparison names `RunAId`, because a comparison names TWO runs and "the run's comparison work item" is not a well-formed lookup. Every comparison lifecycle call is keyed by queue sequence for that reason.

**Repeat/version atomicity:** `IBenchmarkStore.StartRunsAsync` inserts a whole repeat group in one transaction, checks project version once, increments by N, and queues FIFO work items. Per-run CAS can partially create invisible/orphan queued runs. `FreezeCommitGuard` evaluates once per distinct instance inside the transaction.

**Judge policy version:** `ComputePolicyHash` hashes prompt version, not prompt text. Any wording change bumps `BenchmarkJudgePolicyVersions.PromptVersion`. Reads validate stored older versions structurally so the project page can load and offer re-save; writes/activation/execution validate the current version strictly. Otherwise a prompt change mixes incomparable judgments or makes old projects unreadable.

**Ranking facts:** apply exclusion precedence (warm-up, user score, truncation, judge reason) on single-run reads, lists, and ranking. Use the shared rankable verdict for cohort denominator. A truncated run with operator score remains rankable. Project and run both store invocation timeout; execution uses the frozen run value and legacy null means 900 seconds.

**Registry/export contracts:** legacy SHA text may be uppercase; compare case-insensitively. Missing provider-map entry means `llamacpp`. CSV output guards spreadsheet formula prefixes (`=`, `+`, `-`, `@`, tab, CR) with a leading apostrophe inside quotes. The five readiness feature migrations were consolidated into `AddBenchmarkReadiness`; do not search for the deleted branch-local names.


Eligible-model listing reads recorded registry facts without hashing for responsiveness; `BenchmarkRunFreezeService` re-hashes every member before committing a run. Both map `InstalledGgufSnapshotException` to the declared 422 eligibility error so one bad model in a launch matrix does not 500 and abort later cells.

Rank exclusion is projected onto every API path returning runs, not only the ranking query. `TotalScored` uses the same rankable result so a permanently truncated warm-up does not leave the UI waiting forever. Truncation recognition is shared between ranking and judge execution; adding a stop-reason token to one copy only creates inconsistent verdicts.

### Knowledge base / RAG

- SQLite foreign keys are not enabled; cascades do not fire. Explicitly delete vectors → chunks (FTS trigger) → sections → document → file. EF graph tests can false-pass.
- Vector search is managed brute-force cosine by design; sqlite-vec was slower through 100k rows.
- Ingest/query share one `EmbeddingModelResolver` result. Reset staleness only when `IsConfident`.
- Vector identity is model + transform + width. Both paths apply `KnowledgeEmbeddingVectorPolicy`; rows, filters, stale checks, and RAM cache use the canonical identity. Rollback: switch to Native, fully reindex, verify no stale docs, then downgrade binary.
- Retrieval is BM25 ∪ cosine → RRF(k=60) → optional rerank; failures preserve RRF.
- Query embeddings from sensitive text are RAM-only, bounded, keyed by identity + query hash.
- **A pooled (embedding/rerank) forward pass must fit in ONE physical micro-batch.** Emit `-b/-ub = effective context` for Embedding/Reranker only; default 512 is too small for ordinary chunks. llama.cpp clamps to context. The chars/4 estimator remains optimistic for markdown.


**Delete/reindex ordering is explicit because SQLite foreign keys are off:** vectors → chunks (so the FTS sync trigger runs) → sections → document → file. A test that deletes an attached EF graph can false-pass without enabling the same pragma behavior as production.

`EmbeddingModelResolution.IsConfident` gates corpus-wide stale/reset decisions. A transient provider failure must never reinterpret a healthy corpus under a fallback model. Canonical vector identity includes model, transform, and width; the RAM query cache also includes identity + query hash and never persists sensitive query vectors. For vector-mode rollback, set Native, fully reindex, verify no stale documents, **then** deploy an older binary; reversing that order lets older code misread transformed rows.


A pooled embedding/rerank input must fit one `n_ubatch`; llama-server returns 500 rather than splitting. `AppendPooledForwardPassBatchArgs` emits both `-b` and `-ub` equal to effective context for pooled roles only. Over-request is safe because llama.cpp clamps to context. Chat remains excluded because causal decode splits and `--fit` owns batching. `ChunkTokenApproximation` at chars/4 under-counts markdown in observed cases, so do not rely on the small reserve as a proof that a chunk fits.

### Training module (fine-tuning) — live-gate rules, 2026-08-15

- Structured output uses existing MEAI JSON schema. Strict transform makes every property required; interpret no-tool sentinels at `TeacherSampleRecordV1.DemonstratesToolCall` and validate against the original schema.
- Bound every queued model turn (`StructuredAgentRunner.TurnTimeout` and evaluation). A timeout fails one sample, never wedges the queue.
- Python quality belongs in root `pyproject.toml`; `tools/training/pyproject.toml` + lock are shipped runtime. Keep caches/venvs out of the shipped Content glob.
- `str(EOFError())` is empty. Error reasons fall back to exception type/exit code.
- `datasets.map` remains `dataset_num_proc=1`; sandboxed CUDA-initialized processes cannot safely fork.
- Durable rejection counts need durable/logged sanitized reasons; the transient hub buffer is insufficient.
- Preserve uv platform/index constraints and scan stdout for JSON lines because unsloth banners make it dirty.
- Adding `LocalModelOrigin` changes converter, EF pair, CHECK constraint, and registry serialization. New registry fields also update `InstalledGgufRegistryValue` or rollback CAS fails.
- Nullable blobs use `OptionalBlob`; implicit `ReadOnlyMemory<byte>?` conversion can turn database NULL into non-null empty.
- Training routes are flat siblings (`training.index`, `.datasets`, `.comparisons`) and each has its own capability `beforeLoad`. A parent needs `<Outlet>`; route-map tests cover additions.
- llama.cpp “placed layers” text is a fit plan, not GPU audit. Use CUDA override and `IRuntimeDeviceAudit` for live GPU gates.
- GPU exclusivity is `IGpuWorkGate`: loads take exclusive/shared as designed. Taking the gate is the check—never check then act. Evaluation must not take a mutation lease that blocks its own load.
- Convert scripts require the `conversion/` package; re-check imports on every llama.cpp bump.
- Link every run to an installed base GGUF through `IInstalledBaseModelLinker`; never display-name guess.
- Merged export on prebuilt runtime supports F16 only because archives omit `llama-quantize`; quantized merge requires source-built runtime.
- Build daemons often cause RAM refusal; run `dotnet build-server shutdown` before diagnosing capacity.
- Dataset records pin definition JSON transactionally; legacy null pins refuse. V1 samples are exactly one tool call, and evaluation/scoring reject multi-call consistently.
- Comparisons require equal dataset ID, content fingerprint, and order-insensitive hold-out ID set; unreadable membership refuses.
- Revision-pinned Hub lookups use the revision-aware overload and escaped path segment/cache key.
- Queued training behind a warm model must surface waiting/admitted state; eject releases it.
- Route-bound IDs in body DTOs cannot be `required`; FastEndpoints validates body before route binding.
- Launch receipts survive startup recovery and are cleared only by the reaper after kill/non-match proof, per receipt, from an unpaged query. Normal completion clears transactionally.
- Training runtime adoption is one rollback boundary across directory swap + state write, using non-cancellable rollback. Failed reprovision with the prior runtime intact remains `Ready` with sanitized error.
- `training` navigation is compile-time UI visibility, not a server kill switch.
- Evaluation reads live sample rows but refuses fingerprint drift at create and claim/load; keep missing-sample verdict too.
- Artifact deletion goes through `ITrainingExportService.DeleteArtifactAsync`: delete row first, then best-effort contained disk cleanup. Export stale-attempt sweep runs before writing deterministic output.


**Pinned dataset and sample boundaries:** `training_datasets.definition_json` is copied in the same transaction that reads `DefinitionVersion`; generation and evaluation read `DatasetDefinitionService.ReadPinnedBody`, never the mutable definition row. The column is nullable so legacy rows do not materialize an undecryptable empty default; null refuses with `UnpinnedDatasetReason` rather than falling back live.

V1 `TeacherSampleRecordV1` represents **exactly one tool call**. `SampleValidationPipeline` rejects multiple tool parts and `EvaluationScorer.RejectMultiCall` produces a deterministic verdict. Supporting multi-call requires widening `EvaluationExpectation` and scoring, not merely allowing another generated part.

**Launch receipt recovery:** `TrainingRunStartupReaper` enumerates `ITrainingRunStore.ListLaunchReceiptsAsync` **unpaged**; a paged recent-runs query can skip an older still-live trainer. Startup reconciliation may terminalize the run but does not clear its receipt. Only the reaper clears after a successful kill or proven identity non-match. Inspect/kill exceptions leave the receipt for the next startup, and one bad receipt must not abort the rest. Normal completion clears it transactionally.

**Runtime adoption:** directory swap and installed-state write are one rollback boundary. Consume backup only after both succeed; restore directory and state with `CancellationToken.None` even if installation was cancelled. A failed reprovision that leaves the old runtime usable reports `Ready` plus `SanitizedError`; only no surviving runtime reports Failed.

**Dataset/evaluation consistency:** comparison membership requires dataset ID, content fingerprint, and order-insensitive hold-out ID set; run ID is not identity. Evaluation reads live sample rows but checks the current fingerprint at create and claim/load, refusing drift before consulting the model. Keep the per-ID missing-sample check too. Artifact deletion goes through `ITrainingExportService`: row first, then best-effort disk deletion only inside the staged run directory; replacement sweep occurs before deterministic output is written.


**Structured teacher contract:** the MEAI adapter already sends JSON schema and llama-server's strict transform promotes all properties to required. Interpret no-tool sentinels at `TeacherSampleRecordV1.DemonstratesToolCall`, then validate the resulting record against the **original** schema. `DeferredLlamaServerStructuredOutputTests` pins the wire behavior; an adapter upgrade failure is a release blocker.

**Per-turn liveness:** every dataset/evaluation model turn has its own deadline; the provider SDK's multi-minute network timeout is not queue liveness. An overrun records one sample failure. `SFTConfig(dataset_num_proc=1)` remains because CUDA-initialized sandbox processes cannot safely fork the datasets worker manager.

**Runtime manifest split:** root `pyproject.toml`/lock owns ruff, pyrefly, pytest, and bandit. `tools/training/pyproject.toml`/lock is copied to users and `uv sync --locked`; do not add dev tooling there. Parsers scan stdout for JSON records because unsloth banners precede protocol output. Exception text uses type fallback when `str(exception)` is empty.

**Persistence fan-out:** a new `LocalModelOrigin` updates its JSON converter, benchmark EF pair, table CHECK constraint, and `GgufRegistryRevision.SerializeOrigin`. A new `GgufModelRegistryEntry` field also updates `InstalledGgufRegistryValue` or deletion rollback recomputes a different CAS revision. Nullable encrypted blobs go through `OptionalBlob`; implicit nullable `ReadOnlyMemory<byte>` conversion can turn database NULL into present-empty.

**Training routing/exclusivity:** routes remain flat `training.index`, `training.datasets`, and `training.comparisons`; each has its own capability `beforeLoad`. A parent route without `<Outlet>` swallows children. Training/evaluation/model-loading exclusivity uses `IGpuWorkGate`; taking the gate is the check. Training holds the mutation lease where appropriate, but evaluation must not take a lease that blocks its own inference load. Queue claims are attempt-pinned and cannot be returned.

**Hub/filesystem recovery:** rejection reasons must outlive the transient hub buffer through sanitized logging/state. Conversion provisioning includes the pinned scripts' `conversion/` package. Link adapters to an installed base model by explicit choice or repository identity, never display name. Prebuilt runtime supports F16 merge but lacks `llama-quantize`; quantized export requires source build.

**API/revision boundaries:** pinned Hub discovery uses the revision-aware overload, encodes the revision as one escaped path segment, and keys cache by revision. Route-bound IDs in body DTOs are not `required` because body validation precedes route binding. A queued run waiting behind a resident model logs waiting→admitted; eject releases it.


A training run links to its installed base GGUF before adapter export/smoke/promotion. `IInstalledBaseModelLinker` uses explicit wizard selection, official `<base>-GGUF`, or same repository ID—never display-name similarity. Comparison membership deliberately excludes TrainingRunId because the frozen dataset identity and hold-out set, not the path used to reach them, determine comparability.

The training navigation flag is compile-time UI visibility only; endpoints remain registered and Operator-gated. Evaluation uses the current sample rows because frozen JSONL has no sample IDs, but refuses fingerprint drift both at request time and after queue claim so edits between create and execution cannot change the question silently.

---

## 4. Agent Mode, MAF, sandbox, cloud providers

### Sandbox: the two guards are mandatory *together*

Every sandbox read/survey uses both:

1. `ResolveJailPath` for normalized prefix containment.
2. `EnsureNoSymlinkComponentsUnderJail` before open.

Prefix checks do not resolve planted symlinks. Host reads then use raw `open(O_RDONLY|O_NOFOLLOW|O_CLOEXEC)`; do not cast `O_NOFOLLOW` into `FileOptions`. After sizing/reading, probe one extra byte to reject a file that grew and avoid truncated copies. Coder list/search are provider operations so they share these guards and work on Windows; never route surveys back through `ExecuteAsync`.


`ResolveJailPath` prevents lexical escape only. `EnsureNoSymlinkComponentsUnderJail` runs immediately before every read/write/list/search so a command-planted link cannot redirect an otherwise-contained path. The leaf is opened with raw `open(O_NOFOLLOW)` and errno 40/ELOOP is a symlink refusal. Size, read exactly that many bytes, then probe one more byte: growth after sizing rejects the whole copy instead of returning a torn prefix. Survey operations belong on `ISandboxRuntimeProvider` so callers cannot accidentally recreate weaker confinement in command arguments.

### Dev Mode's file guard has TWO predicates on purpose — do not merge them

- `IsSecret(name)` gates reads.
- `IsExcluded(name,isDir)` gates copies and adds build/cache directories.

Using copy exclusions for reads hides diagnostic files such as `obj/project.assets.json`. Credential patterns intentionally omit private-registry config and test certificates from copies. Execution can still print secrets through repository commands; the read/copy guards do not claim to close that channel. Rotate dev databases written under the removed tracked key.


The accepted trade is intentionally conservative: `.npmrc`, `.env.*` (including `.env.example`), and certificate/key patterns are treated as credentials for read/copy policy, so private-registry restores or certificate fixtures may be unavailable inside copied homes. Do not loosen them as a convenience fix. This does **not** prevent a repository build/test from printing a secret to captured stdout; execution is a separate, explicitly unclosed channel.

### Sub-agent spawn: depth cap is structural first

- Strip `spawn_subagent` from child tools unconditionally; the depth runtime check is defense-in-depth.
- Strip every `ApprovalRequiredAIFunction`; a child-as-tool has no approval round trip. Drop wrappers, never unwrap them into auto-execution.
- Bind the child model via construction-time `ChatOptions.ModelId` or it falls back to node default.
- Resolve one `ResolvedAgentRuntime` and carry prompt, reasoning, skills, and curated tools together. Bake reasoning and `AgentSkillsProvider` into construction because `AsAIFunction` receives no per-run options.
- Child restrictions are deliberate: no spawn and no adaptive-memory extraction. Anonymous model-id spawn remains raw/tool-less.
- `AIAgent.AsAIFunction()` forwards cancellation and its generated input is `query`, not `task`.


A bound child consumes one resolved runtime unit: `ResolvedSystemPrompt` (base scaffold + persona + injected memory), `ReasoningEffort`, skills, and curated tools. Reasoning uses `ParticipantReasoningOptions.Build(effort, supportsThinking)` against the **child model's** capability, and skills use an `AgentSkillsProvider` on the child agent. A child has no per-run options, so construction-time binding is mandatory. Memory injection is inherited; post-run memory extraction is intentionally disabled. Anonymous model-id spawn remains raw instructions and no tools.

### Tool-approval policy: the enforcement seams are plural

Apply tighten-only policy projection at every offer seam: bound agent, orchestration participant, mode-off Default Assistant, and deleted-agent fallback in send/regenerate. A missing projection silently bypasses policy. Registry pre-wrap + add-only resolver remain the structural floor; policy only ORs more approval and Unknown fails closed.

`SessionApprovalEligibility` separately controls whether approval may be remembered. Only `load_skill`, `read_skill_resource`, and Fixed custom tools qualify; script execution and parameterized tools do not. Catalog `sessionScopeEligible` is an upper bound; runtime further binds skill/resource/version/import status. Keep runner and catalog callers on the shared helper.

Approval audit rows overload columns in `agent_execution_logs`; every query/aggregate must filter `record_kind`.


The known approval-projection seams are `AgentDefinitionResolver.ProjectAllowedTools` (including its mode-off early return), `OrchestrationResolver.ProjectAllowedTools`, and the `resolved == null` fallback in send/regenerate after an agent definition was deleted. Each projects the catalog flag through `IToolApprovalPolicy`; `InvocationToolResolver` only adds wrappers and never removes the registration-time floor.

Session-scope approval is identity-specific, not category-specific. `load_skill` and `read_skill_resource` require a packaged, non-imported named skill/resource; Fixed custom tools bind the memo to tool version. `run_skill_script` and Parameterized custom tools never qualify. Updating a skill/tool version forces a new prompt. The catalog's `sessionScopeEligible` is only an upper bound because it cannot see call arguments.

`agent_execution_logs` stores chat runs, usage records, and approval decisions in the same table with overloaded columns. Every new SELECT/aggregate filters `record_kind`; otherwise approval source/category fields are misread as model/provider usage.

### A tool handler can NEVER block waiting for a human

The 60-second stream-idle watchdog covers time inside tool handlers. Human waits must end the streamed segment through `ApprovalRequiredAIFunction` and resume in the runner's approval loop. `ask_user` approval is structural; removing it breaks the feature. Children/schedulers/delegate MCP strip such tools. Agentic-scope inbound root runs may auto-answer only through ADR 0006's audit-before-invocation path.


`StreamIdleWatchdog.WithIdleTimeout` wraps the whole streaming call, including function execution. The human wait must occur after a `ToolApprovalRequestContent` terminates that segment. Delegate-scope inbound MCP, schedulers, and children strip approval-required tools because they cannot answer. Agentic-scope root inbound execution is the only auto-approval exception and requires the audit record to succeed **before** invocation.

### MAF traps

- `ChatClientAgentOptions` has no `Instructions`; use `ChatOptions.Instructions`.
- Positional constructor order is `(chatClient, instructions, name, description, …)`. Instructions are delivered exactly once through the leading system seed; pass the ctor instruction argument null. Skill preambles mean tests assert containment, not exact equality.
- Fakes must inspect both messages and `options.Instructions`.
- At the current pin approval types are `ToolApprovalRequestContent` / `ToolApprovalResponseContent`; wrapping the function is the gate. Middleware alone does not gate a plain tool.
- `AgentSkillsProvider` needs scoped `MAAI001` suppression.
- Never log raw tool arguments/results; record length + SHA-256 12-hex prefix.


Instructions are delivered exactly once through the leading System seed used by this application; pass null for the positional constructor's instruction argument. `AgentSkillsProvider` may prepend text, so “once” tests assert the intended instruction occurs once rather than requiring the entire System message to equal it. Approval middleware does not transform a plain function into an approval-required one; the function must be wrapped. Telemetry records no raw tool args/results at Information level.

### Cloud providers

**Codex**

- Strip System messages and fold text into `ChatOptions.Instructions` only at the Codex wrapper; backend rejects System role.
- Overwrite inbound `ModelId` with the resolved Codex ID and clear incompatible output settings. Never send a local model name to a cloud provider.
- Reasoning effort uses the Codex-only side channel, not Ollama `think`; UI Highest degrades to SDK High.
- Enforce `store=false` unconditionally. Preserve encrypted reasoning and function-call raw items across tool rounds; MEAI already replays them.

**Azure Foundry / Entra**

- Route per request by `ChatOptions.ModelId`: explicit Azure deployment > Codex session > blank/default > unknown/local. Never select only from configured connection presence.
- APIM hosts require explicit additional allowed suffixes.
- Construct `OpenAIClient(AuthenticationPolicy, options)` with bearer auth; a per-call policy is overwritten later by API-key auth. Validate final wire headers with a capturing transport, not DI inspection.
- App-only tokens carry `roles`, not `scp`. Delegated auth uses MSAL confidential-client auth code flow, not `AuthorizationCodeCredential`.
- Walk inner exceptions for AADSTS codes; outer `AuthenticationFailedException.Message` is generic.

**MCP transport and skill import**

- HTTP MCP needs http/https plus exact configured loopback host at connect time.
- `GitHubSkillArchiveDownloader`'s real HEAD.zip redirect remains a pre-RC manual check; unit tests fake the redirect and do not prove GitHub's current response.


**Exact MCP transport predicates:** connection-time validation requires both `IsHttpScheme` (**only** `http`/`https`, so `file:` and `ftp:` fail even for loopback) and `IsLoopbackHost` (exact ordinal match in `McpOptions.HttpLoopbackHosts`). Keep both as defense in depth over CRUD validation. Do not replace the configured host list with `IPAddress.IsLoopback`; the strict boundary rejects metadata addresses, userinfo tricks, rebinding suffixes, and alternate IPv6 spellings not explicitly allowed.

The inbound `McpServer` authentication policy lists only `McpApiKey`; JWT remains the application default but must not drive this surface. `LocalApiSecurityMiddleware` is prefix-based, so `MapMcp` stays inside `/api/local/v1`. Optional tool parameters need C# defaults—nullable annotations do not remove them from the SDK schema's required array.


Codex `store=false` is enforced at the wrapper boundary regardless of caller options. When tools and reasoning are present, preserve `ResponseItem` raw representations so encrypted reasoning and function-call items replay exactly. Azure model routing receives per-send ModelId; unknown IDs deliberately fall back local. Do not turn connection presence into provider selection.

The Azure bearer policy regression is pinned by a request-capturing transport that inspects final headers after the assembled pipeline. Construction/DI tests cannot see later policy overwrite. The host suffix allowlist blocks APIM unless the operator adds its suffix; do not weaken it while fixing authentication.

### SignalR does not replay to late joiners

For push hubs: assign per-run monotonic `Seq`; buffer outside the live-run dictionary through a short retention window; join the group before replay; dedupe client-side with high-water mark + gaps. Publish terminal directly from cancel; a model call that never unwinds cannot be allowed to leave UI running forever.


The replay buffer must outlive the live-run dictionary because a fast run can finish before HTTP returns the ID needed to subscribe. Join first, then replay to the caller; sequence/high-water/gap handling deduplicates replay racing live publication. Keep the buffer bounded and evict after the replay-retention window. Cancel publishes a terminal immediately rather than waiting for an unresponsive model call to unwind.

### Chat message status is a table-enforced state machine

`NodeChatMessageTransitions` is the sole allowed-source table. Every correlated update enforces `status IN (...)` atomically; never read then write.

- Cancel/flush/recovery only from non-terminal.
- Queued from pending; streaming from pending/queued. If mark fails, return the persisted terminal event and do not invoke the model.
- `Interrupted` terminalizes only non-terminal. Completed/failed/cancelled may supersede an optimistic Cancelled marker; no other terminal rewrite is legal.
- Build run envelope and SSE terminal from the persisted winning status, never the requested status.


The transition table applies inside one SQL UPDATE (`AND status IN (...)`). Queued may follow pending; streaming may follow pending (platform) or queued (local). If a lifecycle mark loses to cancellation, send/regenerate returns the persisted terminal row and aborts before model execution. Terminalization derives sources from the **target**: Interrupted never overwrites Cancelled, while a true completed/failed/cancelled outcome may supersede the optimistic Cancelled marker. Ledger and SSE use `persisted.Status`, not the requested transition.

### The bubblewrap-isolated sandbox mode: seven traps, all paid for once

- Reject read-only bind sources under chain-owned `/tmp` or `/work`; later mounts shadow them silently. Also reject special `/usr`, `/dev`, `/proc` conflicts rather than reordering.
- `/proc` may answer ENOENT; use `/dev` EROFS as the read-only control.
- Bind descriptors must remain non-CLOEXEC through setsid/systemd-run/bwrap.
- Symlink mode bits are meaningless; follow to canonical target before trust checks.
- Ownership tests must not place fixtures under world-writable `/tmp`, which trips a different guard first.
- Tighten jail/home/tmp to 0700 explicitly.
- Live tests skip only when trusted bwrap is absent; a present bwrap that cannot isolate is a failure, not a skip.


Trust/read-only controls are intentionally outcome-tested. A bind beneath chain-owned `/tmp` or `/work` is rejected rather than silently shadowed by later mounts. Trust checks follow symlinks before applying mode/ownership (Linux symlink bits are normally 0777). The jail/home/tmp are chmod 0700 explicitly. A live gate skips only when no trusted bwrap exists; a present bwrap that fails the isolation control is a product failure.

### Wiring a caller onto the isolated mode: five things the generic layer will not tell you

- Bind the venv **and** uv `pythons` root read-only; venv Python points through a version alias. Do not bind the broader compute cache.
- Execute the venv symlink, not its realpath, or `sys.prefix` loses site-packages.
- Descriptor `RESOLVE_NO_SYMLINKS` checks every path component; `Path.GetFullPath` is insufficient. Report the offending component.
- Environment paths are sandbox paths (`/work`, `/work/home`, `/tmp`), not host jail paths.
- Thread variables come only from `SandboxCreateRequest.ThreadLimit`; caller env emitted later would override them.
- Isolation uses bwrap's network namespace; do not gate it on the non-isolated `SupportsNetworkPolicy` probe.
- Synthetic `/etc` uses rewound, non-CLOEXEC sealed memfd. Kill authority is the transient scope cgroup because PID/PGID tree kills are incomplete inside PID namespaces.


**`run_python` refuses before provisioning.** `ComputeToolGateway.ExecuteAsync` checks `SupportsFilesystemIsolation` immediately after argument validation, before interpreter provisioning, node identity reads, or jail creation. Unsupported hosts receive a refusal and do not download/unpack the managed Python closure. Every call requests `SandboxIsolationMode.Filesystem`; unlike AgentHome, Coder, and Development Mode, compute has no degraded host-filesystem-visible fallback.

The venv requires two read-only sources: its own tree and uv's `pythons` root, because `bin/python` points through a version alias outside the venv. Execute that symlink—not its realpath—so `sys.prefix` retains site-packages. Bind only `pythons`, never the broader uv cache. Descriptor resolution rejects a symlink in **any** bind-source component; `Path.GetFullPath` alone is not canonicalization.

Caller env uses `/work`, `/work/home`, and `/tmp`. Caller variables are emitted last and therefore override chain defaults, so never pass host jail paths or repeat thread variables derived from `SandboxCreateRequest.ThreadLimit`. The sealed synthetic `/etc` memfd is rewound and non-CLOEXEC. Kill the transient scope cgroup, not only PID/PGID trees; a nested session can escape both process-tree mechanisms.

Bubblewrap's filesystem capability and non-isolated `unshare` capability are distinct. `run_python` asks for filesystem isolation and relies on bwrap's own `--unshare-net`; refusing because the separate `SupportsNetworkPolicy` probe failed creates a false negative. Conversely, a host lacking filesystem isolation is refused even if it can create a network namespace because the tool promises both host-filesystem absence and no egress.

---

## 5. Frontend, chat UX, API boundary

### Chat rendering contract

Render one ordered `parts[]` sequence with shared `buildMessageParts()` for live and reloaded messages. Do not flatten reasoning or split streaming/final tool components. Use one state-driven `ToolCallCard` and shared `CodeBlock`. Additive turn metadata stays in `metadata_json`; setting precedence is request → conversation → default.

### Error surfacing

- A failed assistant turn shows exactly one inline red Alert from `hasText(message.error)`, independent of partial content.
- Toast = transient mutation result. Inline Alert = query error, status, empty guidance, or form validation.
- Keep i18next `escapeValue:false`; JSX already escapes and double-escaping shows entities.
- API errors use `apiErrorMessage(error, localizedFallback)`, not dead Axios response access after the interceptor replaces the error.

### API-boundary traps

- Body-less route-only FastEndpoints POSTs need `Description(x => x.Accepts<TRequest>())` or generated no-content calls get 415.
- Multipart DTOs need typed `IFormFile? File` so OpenAPI emits multipart. Global Axios JSON headers defeat serializers; post `FormData` directly with multipart headers.
- `ApiError` fallback order is `detail → message → title → ""`; typed domain bodies may lack ProblemDetails detail.
- Decode route-segment model names with `ModelRouteName.Decode` / `Uri.UnescapeDataString`; Kestrel preserves `%2F` and `WebUtility.UrlDecode` corrupts `+`.
- Normalize ordinary OpenAPI int64 to number at `FetchOpenapi.mjs`; precision-sensitive seeds are strings. Never hand-edit generated Zod.


The Axios interceptor replaces the original Axios error. Components therefore use `ApiError` helpers and cannot recover domain messages through `error.response.data`. Typed `{reason,message}` 409 bodies and ProblemDetails share the same message fallback. For multipart, a typed nullable file exists to make OpenAPI advertise `multipart/form-data`; the endpoint may still fall back to `Files[0]`, but the client must bypass the global JSON default with direct FormData post.


OpenAPI int64 normalization belongs at spec materialization. Ordinary timestamps/durations/counts become Zod numbers; seeds remain strings because they can exceed JavaScript's safe integer. Correct the endpoint/spec seam and regenerate—editing `zod.gen.ts` yields a type/runtime mismatch on the next generation.

### Endpoint exception handling is mature — don't mass-remove catches

Most endpoint catches map domain status or deliberate poll degradation. Before removing one, wire the global handler and declared schema. An operator 401 may log them out; worker-token conflict is 409.

Typed conflicts go through `ConflictExceptionHandler` and `ConflictProblemDetails`; add switch arm + enum + `ProducesConflictProblemDetails()`. Preserve `application/problem+json` by using the content-type overload of `WriteAsJsonAsync`.

Declare the body actually emitted:

- FastEndpoints `AddError`/`Send.ErrorsAsync` or `DomainValidationExceptionHandler` → `ProducesProblemDetails`.
- conflict handler → `ProducesConflictProblemDetails`.
- ASP.NET `Results.Problem` with extensions → `ProducesProblem`.

If one status emits multiple shapes, declare the permissive ASP.NET shape. SignalR equivalents use a `HubException` message beginning with the conflict token.


There are three problem schemas and one status may need the permissive one:

1. FastEndpoints validation (`AddError` / global domain-validation handler) → FastEndpoints `ProducesProblemDetails` with `errors[]` and closed additional properties.
2. Typed conflicts → `ProducesConflictProblemDetails`, including `conflictType`, trace ID, and null-omitted typed extras.
3. `Results.Problem` / ASP.NET ProblemDetails extensions → `ProducesProblem`, which allows extension members.

`WriteAsJsonAsync(value, ct)` overwrites Content-Type with `application/json`; use the overload that preserves `application/problem+json`. Over SignalR, encode the conflict type at the start of `HubException.Message` because that string is the only detail forwarded.

### Seven validation exceptions are mapped globally to 400 — don't re-add per-endpoint catches

`DomainValidationExceptionHandler` maps ScheduledJob, CustomTool, McpServer, SlashCommand, PlaybookAction, AgentDefinition, and AgentSkill validation exceptions to the FastEndpoints 400 shape. Add new single-message validation there. Keep multi-error `PreviewWorkflowValidationException`, aggregate `SelectedFolderValidationException`, and conflict exceptions local.

### `DevelopmentWorkspaceSecurityException`: 400 where the request carried the value, 409 where persisted state blocks it

Caller-supplied folder defects are 400. Persisted project/trust/identity/apply state blocking a valid request is 409 through `DevelopmentRepositoryStateConflictException`. The derived type preserves existing base catches. Mixed-surface reconnect distinguishes the two.

### Knowledge repository import: only `…ImportRejectedException` is a 400

Caller-fixable bound/unavailable failures use `KnowledgeRepositoryImportRejectedException`. Host/index/read/race failures use `KnowledgeRepositoryReadException` and remain server errors. Never widen the endpoint catch to `InvalidOperationException`.


`DevelopmentRepositoryStateConflictException` derives from `DevelopmentWorkspaceSecurityException` so availability probes, summary degradation, and attempt reason handling keep their base catches. Reconnect is the mixed endpoint: a bad newly selected folder is 400, while stale persisted trust/repository identity is 409. Status follows the request surface, not the shared guard throw site.


`DomainValidationExceptionHandler` emits the same FastEndpoints error shape as the removed local catches: `errors[{name:"generalErrors",reason}]`, matching detail, request path, and trace ID. `PreviewWorkflowValidationException` remains local because it contains multiple errors; `SelectedFolderValidationException` mixes 400/404/409 and must be split before global mapping. `SlashCommandConflictException` stays 409.

### Client conventions

- Use `DialogShell` for modals.
- Chat attachment capability flags are static in `NodeCapabilities.ts`, not backend-composed.
- Bounded Mantine inputs that persist “unset” need a post-mount ready guard; Mantine can fire a default/min `onChange` on mount.
- Code/text editing uses shared Monaco `CodeEditor`. Import `editor.api` + selected grammars, never `editor.main`. Keep the manual chunk name aligned with `lazyEditorJavaScriptBytes` budget. Chat streaming stays on lightweight `CodeBlock`.


Monaco stays behind shared `CodeEditor`: import `editor.api` and chosen Monarch grammars, never `editor.main`. The manual Vite chunk name is part of bundle-budget classification; changing it without the `lazyEditorJavaScriptBytes` matcher moves several MiB into the app budget. Vite `?worker` keeps the editor offline. Chat `CodeBlock` remains lightweight because it rerenders per streamed token.


A bounded Mantine `NumberInput`/`Slider` that distinguishes “unset” from override needs a post-mount `ready` guard before persistence. Mantine can emit min/default on mount and overwrite a deliberate null. Capability flags for file/image chat input remain static client constants; do not wait for a backend capabilities endpoint that is not part of this contract.

### Races and flashes

- Auto-advance only after observing unmet → met; do not skip content already satisfied on arrival.
- Globally mounted TanStack queries require access-token `enabled` gates.
- react-joyride v3 controlled completion is final `STEP_AFTER` + `NEXT`, not `STATUS.FINISHED`.
- Add every SignalR hub to `config/signalr-proxy-paths.json`; generic proxy fallback can wedge all websockets.
- Push-only terminal reconciliation explicitly invalidates affected queries.
- Add every `InvocationState` field to `InvocationState.Clone()`; cloned snapshots, not the live object, reach persistence/UI.


SignalR hubs are listed in `config/signalr-proxy-paths.json`; add the path there rather than another inline proxy entry. A missing websocket route can wedge Vite's generic `/api` proxy and break existing hubs. Push-only terminal handlers invalidate queries explicitly. `InvocationState.Clone()` is the single deep-copy boundary used by dispatcher/resume registry; any omitted field can look correct live and persist null.


Auto-advance tracks an explicit armed state: reset on step change, arm only after observing unmet, and advance only after a later met observation. This prevents returning users from flashing through content already satisfied. Globally mounted queries remain disabled until an access token exists; otherwise the pre-login 401 can remain cached after authentication. Controlled react-joyride v3 completes on final `STEP_AFTER`/`NEXT`, not `STATUS.FINISHED`.

---

## 6. Deliberately NOT built

Do not assume these exist or “restore” retired designs.

- **Context management is truncation, not summarization.** `ConversationContextBudgeter` (turn-level) plus `ProviderCallBudgetChatClient` (every provider round) excerpt/drop content. Protected reasoning stripping ships on; protected tool-result excerpting is opt-in. Rewrites preserve message metadata. No cross-turn LLM summary/prefix strategy exists.
- **Restart reconciles but does not auto-resume.** Startup marks non-terminal chat rows Interrupted with durable envelopes and stale scheduled runs Failed. `InvocationResumeRegistry` is browser-reconnect memory only. Manual Regenerate is the recovery.
- **No mid-stream retry.** Pre-first-token retry and circuit breaker exist.
- **Approval/HITL and tighten-only policy are live.** MCP/default high-risk tools are structurally approval-required; node policy can only add approval. Category-wide loosening is not built. Decisions are audited.
- **Scheduler can run a saved local single agent.** It strips approval-required tools, uses capacity/GPU admission, and writes a content-safe history summary. It does not write a chat conversation or compile orchestration.
- **Sandbox providers:** fake, process, and opt-in Development Docker exist. OpenSandbox is not built; consult ADR 0004 and the container-status roadmap.
- **Playbook retrieval defaults to embeddings**, with lexical fallback. Adaptive memory is per-agent; no node-wide/cross-agent sharing.
- **No RAG over chat attachments.** V1 uses file tools or capped inline text; no image/OCR ingestion.
- **No STT.** TTS uses browser Web Speech; Kokoro is not shipped.
- Desktop-only ThemeConfigurator/Open Canvas are outside the mobile-responsive scope.


The outer budgeter protects system messages plus recent turns, then excerpts old tool results, drops old whole turns/approval groups, and—only if still over—may strip reasoning inside the protected window; protected tool-result excerpting is opt-in. It never mutates the last message. The inner budgeter repeats protection for every provider/tool-loop/participant round. Rebuilt messages preserve ID, author, creation time, raw representation, and additional properties; role+content reconstruction loses resume/provider identity.

Restart recovery order matters: reconcile non-terminal chat rows to Interrupted and write durable content-free run envelopes before serving; mark stale queued/running scheduler rows Failed. Do not auto-dispatch: that would retry billed/private work after restart. `InvocationResumeRegistry` is intentionally process memory for browser reconnect only.

The node approval policy is tighten-only OR over catalog/category/agent requirements; Unknown fails closed. Category-wide loosening is deliberately absent. Scheduled saved-agent runs are node-local, single-agent, capacity/admission-gated, and strip approval tools; they write a safe summary, not a visible conversation or orchestration result.

Agent Skills resources/assets are live and encrypted with AAD bound to both skill ID and resource name; scripts are refused. Names use MAF's `AgentSkillFrontmatter.ValidateName`, including the consecutive-hyphen rule. Children waive approval only for `load_skill`/`read_skill_resource` after the parent approves spawn; `run_skill_script` remains approval-required and is therefore stripped.


**Approval and scheduler boundaries:** MCP tools default approval-required, and `run_in_agent_home` is gated/inactive. The node policy cannot waive those structural defaults. Approval decisions write metadata-only audit rows and a metric. Scheduled saved agents reject cloud/remote effective models before execution, dispose capacity reservations after each fire, strip HITL tools, and never compile an orchestration.

**Sandbox status:** fake is the CI floor; process is the normal AgentHome/Coder provider; Development Docker is opt-in. OpenSandbox/MXC is not silently selected and the existing SPI remains because Docker is interim, not proof of a hard security boundary.

**Memory boundaries:** playbook injection is embedding-ranked with lexical fallback and rides the resolved prompt so resume config hashing stays stable. Adaptive extraction is per-agent only. Attachments are not indexed into RAG; image/OCR and STT remain unbuilt. Do not describe browser Web Speech TTS as a shipped local speech model.

---

## 7. Agentic support / MCP-only mode

- **`--mcp-only` is a local mode, not a second host.** Treat `LaunchMode.McpOnly` like desktop for desktop-only endpoints, data, provisioning, lease, loopback security, and shutdown.
- **Ready output is a stable raw contract.** `DesktopLifecycle` emits one `XE_READY=1` line in exact key order and canonical `ready.json`; do not wrap/reformat it.
- **`delegate` and `agentic` share one key row but differ in authority.** Mint atomically replaces key/scope; there is no dual-valid window or per-call scope negotiation. Durable runs retain captured authority across restart/key rotation.
- **Agentic authority is explicit.** It travels through `McpInboundExecutionContext`, fingerprints, admission, and durable execution—never `AsyncLocal`. It grants only enumerated inbound MCP authority, not Operator JWT/REST. Root approval-required calls need a successful metadata-only audit write before invocation; child curation is unchanged. See ADR 0006.
- **One skill source:** `skills/xe-local-ai-engine/`. Do not duplicate/symlink into `.claude` or `.agents`; installers copy the versioned tree.
- **MCP reference is executable documentation.** `McpToolsReferenceDriftTests` reflects tool sets/counts/scopes and compares the first columns of `skills/xe-local-ai-engine/references/mcp-tools.md`. Update code and table together.


`LaunchMode.McpOnly` participates in the same loopback middleware and local-data/provisioning/lease/shutdown behavior as Desktop; a check for Desktop alone drops endpoints in the external-agent launch. The raw ready line is consumed by installers by prefix and exact key order, so even harmless structured logging is a breaking protocol change.

A key mint replaces delegate/agentic scope atomically. Persisted agentic runs capture authority in request/binding fingerprints and retain it over restart/key rotation; do not infer it later from the current key row. Root authority is enumerated MCP capability only—never an Operator role, JWT, REST bypass, or ambient `AsyncLocal`. Audit failure blocks the root approval-required call.

The MCP reference drift test compares reflected tool names/scopes/counts with the markdown table. Tool rename or scope move updates both in one change. The external skill source is one real directory; symlink/duplicate copies break Windows archives and drift.

---

## Stale beliefs corrected

These are intentionally terse. Follow the linked/current section for the active rule.

| Stale belief | Current correction |
|---|---|
| Bash variable `GROUPS` is available. | Bash owns it; use `TEST_GROUPS` (§1). |
| CI runs test projects sequentially. | Projects run concurrently with separate result directories; the main Tests module uses grouped batch runner (§1). |
| Memory-safe runner defaults to `JOBS=4`; increase in-process width. | Default is `JOBS=10`; process batches, not width, provide the useful concurrency (§1). |
| Coverage should use one process per namespace. | Coverage instrumentation makes that prohibitively expensive; use `TEST_GROUPS=$(nproc)` groups (§1). |
| Development Mode must be unrestricted because restore needs network. | A short warm sandbox restores, then the agent-facing sandbox requests deny-egress where supported (§2). |
| WSL has no GPU / is an RTX 4080. | Hardware changed to RTX 5090 at last verification; always query live (§2). |
| llama.cpp native MCP should replace the app client. | Its MCP proxy is for llama-server's browser UI; keep the .NET MCP integration and approval boundary (§3). |
| Ollama was removed. | Only Aspire auto-orchestration was removed; Ollama remains opt-in (§2). |
| `aspire stop` necessarily leaks and only 13.5+ fixes it. | Leak did not reproduce on tested versions, but `dev-stop.sh` remains sanctioned until the trigger is understood (§2). |
| NVFP4 is unsupported. | NVFP4 GGUF works; NVFP4 safetensors conversion does not (§3). |
| Inbound MCP key is recoverable. | Only a SHA-256 digest is stored; plaintext is returned once on generation (§2). |
| `dotnet test` cannot discover MTP tests. | `global.json` pins MTP; `dotnet test` works (§1). |
| Any build runs analyzers. | Analyzer wall is Release-only locally (§1). |
| `RunAnalyzers=false` disables generators. | It skips diagnostic analyzers only (§1). |
| Green E2E proves frontend typecheck. | E2E uses Vite-only `build:e2e`; run `pnpm run lint` (§1). |
| Browser E2E is entirely sequential. | It uses disjoint serial and pooled phases (§1). |
| TUnit alternation silently matches zero. | `(A|B)` returns the union; verify with `--list-tests` (§1). |
| Harmony reasoning must remain undetected. | It is detected as distinct `native_reasoning`, mutually exclusive with graded thinking (§2). |
| Development Docker is unbuilt / now required. | It is shipped opt-in and not the default; status roadmap is canonical (§2). |
| Development Mode always fails closed without isolation. | It degrades per served capability/platform; report actual posture (§2). |
| Both build-and-test and E2E are blocking PR gates. | Main CI targets develop; E2E is manual/label-triggered (§1). |
| `release.yml` passes `--pre` to `vpk pack`. | `--pre` is only for upload; packing uses SemVer suffix (§1). |
| Docker is gone entirely. | ADR 0004 permits Development Mode only; inference/AgentHome/Coder constraints remain (§2). |
| Process sandbox always shares host network. | Empty netns is used where measured available; other hosts degrade (§2). |
| Hardened containers always use uid 1000 and inspect proves mapping. | Rootless requires uid 0 to map to engine user; verify outcome host-side (§2). |
| TOCTOU guards live in a Docker provider. | They live in `ProcessSandboxRuntimeProvider` and apply to provider operations (§4). |
| Modern `git apply` rejects `--binary` patches. | It applies them; never depend on rejection for security. |
| Advisor runs an llmfit container/HostAgent. | It uses in-process fit estimation and live HF discovery (§3). |
| Recommendation is raw-size descending. | Explore ranking is capability-bucketed (§3). |
| `ModelKind` has only Unknown/Chat/Embedding. | Reranker is a fourth kind and is checked first (§3). |
| CUDA/TUnit remembered minor versions are pins. | They are volatile; query tools and `Directory.Packages.props`. |
| Conversation history always replays verbatim. | Two budgeters excerpt/drop history (§6). |
| Protected recent turns are immutable. | Late budget passes may strip reasoning; protected tool-result excerpting is opt-in (§6). |
| Playbook retrieval is lexical by design. | Embedding ranker is default; lexical is fallback (§6). |
| Node DB is SQLCipher and wrong key fails DB open. | SQLite is plain with per-column AEAD; DataProtection resolver enforces fail-closed key reads. |
| Skill names only reject edge hyphens. | MAF validation also rejects consecutive hyphens; use its validator. |
| Skills are instructions-only. | Resources/assets are persisted with skill-bound AAD; scripts remain refused. |
| Assigned skills automatically work in children. | Skill read/load approval is waived only for children; script execution remains approval-required (§4). |
| `run_python` can see the host filesystem. | It requires filesystem-isolated bubblewrap and refuses otherwise; other sandboxes are unchanged (§4). |
| Compute venv chmod can undo read-only mode. | Inside the namespace the read-only bind makes chmod/write fail. |
| Compute egress is gated by `SupportsNetworkPolicy`. | Filesystem isolation/bwrap owns the network namespace; use that boundary (§4). |
