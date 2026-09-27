# Backend tests

Scope: TUnit/MTP backend tests: scoping and filters, the batched runner and build lock, flakes and parallelism, temp
state and ports, host fixtures, SQLite test databases, and the browser E2E host; test principles live in
`docs/wiki/17-writing-tests.md` and gate commands in `AGENTS.md` ("Validation"). Read when: writing, scoping or
debugging a backend test, or changing `TestServerWebAppFactory`, `scripts/run-tests-memory-safe.sh`,
`scripts/test-durations.py`, the build lock, or a node-settings or cancellation path these entries name.

## Running and scoping

### Running backend tests

**Rule:** filters, `--list-tests` and exit 8 are in `AGENTS.md` ("Validation"). When batching in bash, never name a variable `GROUPS` (reserved: bash's group-id array); the runner's knob is `TEST_GROUPS`. **Prevents:** a batch loop silently reading the shell's group ids. **Authority:** `scripts/run-tests-memory-safe.sh` (`TEST_GROUPS`).

### `--list-tests` prints METHOD names only, so a union filter is confirmed one class at a time

**Rule:** `--list-tests` output is flat method names with no class or namespace, so grepping it for a `(ClassA|ClassB)` alternation finds nothing even when every class exists. Confirm a union filter with one `--list-tests` per class (`/*/*/<Class>/*`) and read each `Discovered N tests` line. **Prevents:** a misspelled class contributing nothing to a gate filter: the alternation never exits 8 while one name matches, and the run still reports `Passed!`. **Authority:** the `--list-tests` output of the test executable.

### the `Architecture` guards are a NAMESPACE, so a class-name treenode filter never reaches them

**Rule:** treenode paths are `/<assembly>/<namespace>/<class>/<method>`; run the architecture guards with `--treenode-filter '/*/*.Architecture/*/*'` (or `'/*/XE_Local_AI_Engine.Tests.Architecture/*/*'`). `'/*/*/Architecture*/*'` matches class names, runs none of them, and still exits green if another alternation matched. Confirm with `--list-tests`. **Prevents:** a scoped run claimed to include the guards (comment budget, layering, test categories) that never ran them, leaving the Release gate to red first. **Authority:** namespace `XE_Local_AI_Engine.Tests.Architecture`. [evidence](../agent-knowledge-evidence.md#the-architecture-guards-are-a-namespace-so-a-class-name-treenode-filter-never-reaches-them)

### a test that builds `Host.CreateApplicationBuilder` is `Integration`, however small

**Rule:** a DI-composition test that calls `Host.CreateApplicationBuilder()` (or `WebApplication.Create*`, `AddNodeApplication`, `AddNodeModelRuntime`) carries `[Category(TestCategories.Integration)]`, even when it only lists `builder.Services` descriptors and starts nothing. **Prevents:** a registration-order test written as `Unit` passing its own filter, then failing the gate's Architecture lane. **Authority:** `TestCategoryConventionTests` (scans `Unit` classes for those primitives); `RuntimeAuditCudaDeviceProbeCompositionTests`.

### Verify against the whole module, not just the class you touched

**Rule:** the whole-project run (`AGENTS.md` "Validation") is the only local check that unrelated host construction still works. A composition factory reading optional store or config state must null-guard it (`Load()?.X`): test substitutes return null, so every host-based test fails at startup while narrow changed-class runs stay green. **Prevents:** static caches, host state or order failing outside the edited class, unseen until a gate. **Authority:** `AGENTS.md` "Validation". [evidence](../agent-knowledge-evidence.md#verify-against-the-whole-module-not-just-the-class-you-touched)

### A relative test-project name in a worktree can resolve to a SIBLING worktree's binary

**Rule:** name the test project by its absolute `.csproj` path and prefix the run with `MSBUILDDISABLENODEREUSE=1`: a relative `dotnet test XE-Local-AI-Engine.Tests` inside a worktree once resolved, through an MSBuild worker node started under a sibling checkout, to that checkout's output and reported zero tests. **Prevents:** a green-looking run that never executed the code under the cwd. **Authority:** the node-reuse mechanism in `build-and-analyzers.md` ("A Dev-mode sandbox run leaves MSBuild worker nodes holding a dead `NUGET_PACKAGES`").

## Batched runner, build lock and measurements

### a batched-module red keeps only 3 grep'd lines, so capture the failure detail on the FIRST red

**Rule:** when `XE-Local-AI-Engine.Tests` reds inside the gate, the three `[<namespace>] …` lines printed are all that survives: `run_ns` keeps `grep -E 'failed|error' | grep -viE 'failed: 0' | head -3` of the raw output and deletes the rest, `RESULTS_DIR` is wiped on exit, and TRX exists only under `COVERAGE_DIR`. If the exception detail matters, re-run the failing namespace or class targeted (or set `COVERAGE_DIR`) before re-running the gate. **Prevents:** promising a reviewer exception text that is already gone. **Authority:** `scripts/run-tests-memory-safe.sh` (`run_ns`, the `RESULTS_DIR` trap). [evidence](../agent-knowledge-evidence.md#a-batched-module-red-keeps-only-3-grepd-lines-so-capture-the-failure-detail-on-the-first-red)

### An ungrouped local coverage run reds a namespace that covered no product code

**Rule:** with `COVERAGE_DIR` set and `TEST_GROUPS` unset, a `<namespace>(no-coverage-report)` entry means that unit exercised no product assembly, not that a test failed: check its pass/fail counts first. Do not make the runner tolerate empty reports; one can be a real gap. CI's `TEST_GROUPS` packing never hits it. **Prevents:** debugging phantom reds, or letting a real empty report pass. **Authority:** `scripts/run-tests-memory-safe.sh` per-unit verdict loop (`cov` field of each `.result`) and its header note.

### Re-measure a TRX set with the script, never quote a class or namespace count from a document

**Rule:** derive timings and counts by running `scripts/test-durations.py` over a TRX set (`--heavy` for the `HEAVY` lines the runner parses, with `--runs N` for a per-run mean; `--counts` for classes, tests, namespaces and files); cite the script, never a number in a comment or doc. **Prevents:** frozen measurements going stale; a stale `HEAVY` weight once pushed a CI shard past its timeout. **Authority:** `scripts/test-durations.py`; `scripts/tests/test_test_durations.py`. [evidence](../agent-knowledge-evidence.md#re-measure-a-trx-set-with-the-script-never-quote-a-class-or-namespace-count-from-a-document)

### run build and test inside ONE build-lock invocation in a shared worktree

**Rule:** in a worktree several workers share, run the build and the `--no-build` test executable as one locked command: `scripts/with-build-lock.sh -- sh -c '<build> && <test exe …>'`. Two locked calls release the lock between them, and another worker's build can replace the binaries in the gap. **Prevents:** grading another worker's half-finished tree as yours. **Authority:** `scripts/with-build-lock.sh`.

### A concurrent `dotnet build` corrupts a test run — and the result is then neither pass nor fail

**Rule:** the lock, the guard and exits 69/75 are in `docs/wiki/13-testing-and-validation.md`. Beyond them: a direct `dotnet test` bypasses both lock and snapshot, so wrap it or treat its result as provisional; an IDE's background build host ignores the shell lock and can rewrite assemblies under it (sizes unchanged, only mtimes move), so rerun; never wrap a whole validator in one outer lock, which turns its internally locked trees into pass-throughs. A lock helper must mark its descriptor close-on-exec before MSBuild. A wait loop on `pgrep -f "dotnet (build|test)"` matches its own shell wrapper and waits forever: match `dotnet-root/dotnet (build|test)`, or use the lock. **Prevents:** phantom reds and greens. **Authority:** `scripts/with-build-lock.sh`, `scripts/assembly-guard.sh`. [evidence](../agent-knowledge-evidence.md#buildtest-contamination-incident)

### Leftover build daemons starve the timing-sensitive tests — and the packaging gate is where you notice

**Rule:** before packaging, or diagnosing a suddenly slow timing test, run `dotnet build-server shutdown`. Compare the failure's duration with the test's budget: a failure lasting exactly the budget under load is a load signature, not a behaviour signal. **Prevents:** chasing a product defect for a starved timer. **Authority:** `AGENTS.md` "Validation" (shutdown before a gate chain). [evidence](../agent-knowledge-evidence.md#timing-test-and-build-daemon-incidents)

## Flakes and parallelism

### The full Tests module is flaky under parallelism — verify suspects in isolation

**Rule:** rerun a named failure alone, then the module; never weaken an assertion for load. `XE_NODE_SQLITE_KEY` premises conflict: `DesktopBootstrapTests` needs it unset (bare `[NotInParallel]`; never key it), `PlaybookRetrievalRankerRegistrationTests` set (keyed). Before adding a `[NotInParallel]`, find the parent-env write site: seeding host config or a child `ProcessStartInfo.Environment` is not a mutation (only `LlamaCppSourceBuildServiceTests` mutates this one). Get parallelism from separate processes, never by weakening these attributes. **Prevents:** a filtered green mistaken for the gate, and serialization added for a write that never happens. **Authority:** the classes named; `scripts/run-tests-memory-safe.sh`. [evidence](../agent-knowledge-evidence.md#the-full-tests-module-is-flaky-under-parallelism--verify-suspects-in-isolation)

### A class that only EMITS on `XE.Node` needs no guard — but only after you check every listener on its instruments

**Rule:** keep bare `[NotInParallel]` on the `NodeMeterCapture` suites (meter `XE.Node`, sometimes unfiltered) and on PATH-stub suites (no overlap with real `git`/`cmake` spawns). An `XE.Node` emitter needs no guard only if every listener on its instruments is bare-guarded or filters by instrument name; `KnowledgeIngestionDispatcherTests` asserts lower bounds behind a name switch, so never make it exact. **Prevents:** a listener reading two tests' sum. **Authority:** the `NodeMeterCapture` suites; `KnowledgeIngestionDispatcherTests`.

### A wall-clock budget sized on an idle box is a CI flake waiting to happen — use `TestBudgets.Contended`

**Rule:** use `TestBudgets.Contended` for completion windows under contention; it is a failure deadline, not a sleep. Keep a semantic timeout in the product option under test, and never add a stopwatch ceiling unless timing is the behaviour. Never widen a timeout the test expects to expire. If the operation can fault before signalling, await that task while polling; give a later cleanup post-condition its own `AssertEx.EventuallyAsync`. **Prevents:** idle-box budgets flaking in CI, and timeouts hiding the real exception. **Authority:** `TestBudgets`. [evidence](../agent-knowledge-evidence.md#a-wall-clock-budget-sized-on-an-idle-box-is-a-ci-flake-waiting-to-happen--use-testbudgetscontended)

### Adding a migration can red the module through a test that never mentions migrations

**Rule:** `NodeChatMigrationRecoveryServiceTests` applies every migration under an attempt budget that starvation cancels mid-apply (`table "ef_temp_<name>" already exists` on retry), so the class is `[NotInParallel]` and a new migration can red it. Its abandoned-lock test is meant to exhaust the budget: never raise the budget to fix it. **Prevents:** a starved apply read as a migration bug, and dead wall clock bought for nothing. **Authority:** `NodeChatMigrationRecoveryServiceTests`.

### `[NotInParallel]` is process-local, so a fixed path under the OS temp dir is shared by the runner's other processes

**Rule:** a path a test asserts the contents of must be unique per OS process (suffix `Environment.ProcessId` or a Guid where it is defined): a keyed `[NotInParallel]` binds only its own process's scheduler, and `scripts/run-tests-memory-safe.sh` runs one process per namespace concurrently with a shared `TMPDIR`. On a recurrence, record the failure and rerun that class alone; never retry the suite until green. **Prevents:** a sibling process writing into, or deleting, the directory under assertion. **Authority:** `FrameworkTempSentinel` (`TranscriptionUploadStreamingTests`). [evidence](../agent-knowledge-evidence.md#transcription-framework-temp-flake-2026-09-1314)

### `FastEndpoints.Config.SerOpts` is process-global, so a bare-context test sees whoever booted a host first

**Rule:** a test driving a handler with a bare `DefaultHttpContext` derives the FastEndpoints `errors[]` casing from `FastEndpointsProblemBody.GeneralErrorsName`, never a literal; host-based tests keep asserting `"generalErrors"`, the wire contract. `Config.SerOpts` is static and seeded by the first host that boots, so a hardcoded bare-context expectation depends on order. Also: `AssertEx.Equal`'s `message` replaces the expected/actual text. **Prevents:** an order dependency disguised as a flake. **Authority:** `XE-Local-AI-Engine.Tests/Testing/FastEndpointsProblemBody.cs`; `AssertEx.Equal`. [evidence](../agent-knowledge-evidence.md#fastendpointsconfigseropts-is-process-global-so-a-bare-context-test-sees-whoever-booted-a-host-first)

### A loopback port you bound on `:0` and released is a candidate, not a reservation

**Rule:** hold-versus-retry is in `docs/wiki/17-writing-tests.md` ("Independence"). The retry signals for `LoopbackPort.BindWithRetryAsync`: the engine CLI's exit code 6 (`Program`, the `DesktopPortStore.IsPortAvailable` branch; it never falls back to another port) and Kestrel's `IOException` → `AddressInUseException` → `SocketException` chain. Return `null` on those and let every other failure throw; never retry on time. Never "fix" a race by letting the server pick an ephemeral port when the test proves a requested port is honoured. Known debt: four `DesktopPortStoreTests` still reserve-then-release. **Prevents:** a batch taking the released candidate first. **Authority:** `XE-Local-AI-Engine.Tests/Testing/LoopbackPort.cs`. [evidence](../agent-knowledge-evidence.md#a-loopback-port-you-bound-on-0-and-released-is-a-candidate-not-a-reservation)

### Never classify a cancellation from a `CancellationToken.Register` callback

**Rule:** a registration callback may signal or kill, but never owns terminal classification or throws the final exception: callbacks race disposal and run in reverse registration order. Classify once, at mapping time, after the awaited operation observes cancellation, in priority order: deliberate cancellation recorded synchronously, then the caller/host token, then the invocation timer by elimination; feed category and metric from that one result. Registering the flag callback earlier is not a fix: downstream callbacks do not exist yet. Regression tests park the stream on a gate the test controls. **Prevents:** genuine timeouts reported as Cancelled, and host cancellation mislabelled as watchdog. **Authority:** `InvocationLifecycleTracker`.

## Fixtures, hosts and temp state

### The full Tests module balloons to ~3.5 GB — it is a framework leak, not a fixture bug

**Rule:** use `TestServerWebAppFactory`, never `WebApplicationFactory<Program>` (its `HostingListener` roots every host). Keep the per-host JSON options (MEAI's descriptor cache), `SqliteConnection.ClearAllPools()` and the Testing rate-limiter bypass: `AddRateLimiter` creates an undisposed replenishment timer per host, so `ConfigureServices` computes every permit limit outside its lambda (capturing ints, never `builder`) and relaxes them under `Testing`. Share a per-class host only for read-only or Guid-keyed tests, never for counts, global state, DI swaps, lifecycle or long-lived listeners. **Prevents:** process-lifetime host leaks and shared-host races. **Authority:** `TestServerWebAppFactory`; `ConfigureServices.cs`; `scripts/run-tests-memory-safe.sh`. [evidence](../agent-knowledge-evidence.md#the-full-tests-module-balloons-to-35-gb--it-is-a-framework-leak-not-a-fixture-bug)

### A test host owns ONE EF internal service provider; never let it use EF's static cache, never let it rebuild per scope

**Rule:** `EntityFramework:ServiceProviderCaching=false` (set by `TestServerWebAppFactory`) makes `AddNodeModelRuntime` register `NodeEfInternalServices`, one per host, which both node DbContexts pass to `UseInternalServiceProvider`; production keeps EF's cached provider. Under an internal provider EF refuses a singleton interceptor passed through the options and per-context provider-constant options such as `ConfigureWarnings`: register the interceptor in `NodeEfInternalServices` and keep `ConfigureWarnings` on the cached branch only. **Prevents:** EF's immortal per-host cache entry, and a provider rebuild per DbContext scope. **Authority:** `NodeEfInternalServices`; `AddNodeModelRuntimeExtensions.AddNodeModelRuntime`.

### Test hosts leak temp files to `Path.GetTempPath()` — keep the fixture cleanup

**Rule:** `TestServerWebAppFactory.DisposeAsync` deletes SQLite sidecars, node-data directories and temporary `wwwroot` roots, and a test that registers a temp path for cleanup deletes it whether file or directory. A killed run cannot dispose; recover with `find /tmp -maxdepth 1 \( -name 'xe-local-ai-engine-tests-*' -o -name 'xe-local-ai-engine-persistence-*' -o -name 'agenthome-proc-*' \) -exec rm -rf {} +` (never a huge shell glob: `ARG_MAX`). The SQLite templates live under `bin/`, not in that sweep. **Prevents:** a tmpfs `/tmp` filling across runs. **Authority:** `TestServerWebAppFactory.DisposeAsync`; `AgentHomeProcessWriteBackLoopTests`.

### The one temp artifact that is meant to survive: the migrated SQLite template

**Rule:** the MVID-keyed migrated templates (`TestServerWebAppFactory.BuildMigratedTemplate`, `MigratedDatabaseTemplate.BuildAsync`) survive teardown in `<test bin>/sqlite-templates/`, publish atomically, and each build sweeps other-MVID files. Delete that directory or `bin/` to force a rebuild; set `UsePreMigratedDatabase=false` only to test migrations. The runner prewarms the base tree (`prewarm_template`) before cloning coverage slots. Never move templates to a directory two worktrees share: the sweep is safe only per worktree and build output. **Prevents:** orphaned templates filling shared temp space. **Authority:** `TestServerWebAppFactoryTemplateSweepTests`; `scripts/run-tests-memory-safe.sh` (`prewarm_template`). [evidence](../agent-knowledge-evidence.md#the-one-temp-artifact-that-is-meant-to-survive-the-migrated-sqlite-template)

### A test that needs a migrated SQLite database copies the template — replaying the chain is the exception

**Rule:** the copy recipe, and when to keep the from-empty replay, are in `docs/wiki/17-writing-tests.md` ("An EF migration"); never feed `MigrationChainTests` a template. The template is a WAL file, so `MigratedDatabaseTemplate` publishes only after `PRAGMA wal_checkpoint(TRUNCATE)`, pool release and close, and refuses while a `-wal`/`-shm` exists; never "fix" that by disabling WAL. **Prevents:** a single-file copy losing rows still in the log, and a vacuous chain test. **Authority:** `MigratedDatabaseTemplate`, `MigratedDatabaseTemplateTests`; `TestServerWebAppFactory.BuildMigratedTemplate`.

### A test host whose content root is the real Client source dir must register a fake `INodeDataDirectory`

**Rule:** a fixture combining the real Client content root with redirected node data removes `INodeDataDirectory` and registers `FakeNodeDataDirectory`. **Prevents:** first-launch migration moving credential and settings files out of the checkout, and teardown deleting them. **Authority:** `ServiceProviderValidationTests.HostCreation_LeavesTheContentRootNodeSettingsWhereItIs`.

### A node-settings save path must carry the local-only members over from the stored record

**Rule:** `StoredNodeSettings` holds members the wire DTO never carries (`MachineKey`, `ToolApprovalPolicy`); persisting a fresh record built from the request erases them, and a lost `MachineKey` silently orphans every frozen inference profile. Never add `MachineKey` to a request or response DTO. **Prevents:** endless silent re-fits. **Authority:** `NodeSettingsEndpointTests`; `NodeSettingsAdministrationServiceTests`. [evidence](../agent-knowledge-evidence.md#a-node-settings-save-path-must-carry-the-local-only-members-over-from-the-stored-record)

### A node-settings save takes the machine key from the record at write time

**Rule:** `IMachineKeyProvider` can mint between a save's load and its write, so persist through `INodeSettingsStore.UpdateAsync` and take the key from the write-time record (`NodeSettingsAdministrationService.ValidateAndSaveAsync`: `settings with { MachineKey = latest.MachineKey }`); `MachineKeyProvider` mints only when `latest.MachineKey` is empty and caches the stored key. `SaveTrustedMergedAsync` still copies the key up front: that copy owns the record it validates and returns, rejection paths included. Never write with a bare `SaveAsync` built from an earlier `LoadAsync`. **Prevents:** a save overwriting a key minted since its load. **Authority:** `NodeSettingsAdministrationServiceTests`; `MachineKeyProviderTests`.

### `ExternalAppEntityConfigurationTests` pins the external-app instance column list

**Rule:** `InstanceTable_MapsEveryColumnToItsSnakeCaseName` holds every `external_app_instances` column as a literal array checked in both directions; a new `ExternalAppInstance` property extends it in the same commit as the entity, Fluent mapping and migration. **Prevents:** the whole `XE-Local-AI-Engine.Client.Persistence.Tests` project redding with what reads like an unrelated failure. **Authority:** `ExternalAppEntityConfigurationTests`.

### Never infer this node's foreign-key posture from a connection string — measure `PRAGMA foreign_keys`

**Rule:** read the posture with `PRAGMA foreign_keys`, never from a string, comment or fixture (mechanism: `docs/wiki/08-data-and-persistence.md`). Both layers are needed: the string's `ForeignKeys = true` reaches the Quartz job store, which resolves its connection by name and bypasses the pragma applier. Explicit ordered deletes stay required: `Restrict` rejects parents deleted first; tables with no declared FK (`integration_executions.session_id`, the polymorphic `training_work_items.target_id`) are reached only by the delete; a cascade or SET NULL promises no order (chunks go before sections). The FTS AFTER DELETE trigger also fires on cascade, so never hand-write a delete where a cascade is declared. **Prevents:** audits built on the wrong posture. **Authority:** `NodeSqlitePragmasTests`; `DesktopBootstrap.EnsureLocalDataConfiguration`.

### `SqliteParameter.AddWithValue(Guid)` binds a BLOB, but EF stores this schema's Guids as TEXT

**Rule:** in a raw `SqliteCommand` against an EF-created database, bind a Guid key as `guid.ToString()` or filter on a non-Guid column: `Microsoft.Data.Sqlite` binds a `Guid` as a 16-byte BLOB, EF's SQLite provider stores Guid columns as TEXT, and SQLite compares the two as unequal without an error. **Prevents:** a raw read-back matching zero rows, so the test throws for the wrong reason or asserts over nothing. **Authority:** `TranscriptionSessionStoreTests.ReadRawSegmentTextAsync` (filters on `seq`); `AddAgentDefinitionsMigrationTests` (binds `conversationId.ToString()`).

### Deleting a dead service can delete a live control's only tests — grep the test file first

**Rule:** before deleting a test file with the service it was written for, grep it for the other production symbols it exercises and re-home whatever is still live. **Prevents:** silently dropping a live control's only coverage; coverage thresholds are project-wide, so no gate notices. **Authority:** `MemoryProposalSecretScannerTests` (re-homed this way). [evidence](../agent-knowledge-evidence.md#deleting-a-dead-service-can-delete-a-live-controls-only-tests--grep-the-test-file-first)

## Browser E2E host

### a solution build overwrites the E2E test host, so the flagged build must be the LAST one before a `--no-build` E2E run

**Rule:** `XE-Local-AI-Engine.Tests.E2ETests` builds as `IsTestProject=false` / `OutputType=Library` unless `-p:RunE2ETests=true` is passed, so a plain `dotnet build XE-Local-AI-Engine.slnx` overwrites the MTP test host the flagged build produced. Repeat the flagged build immediately before a `--no-build` E2E run, or use `scripts/run-e2e-local.sh`, which orders this itself. **Prevents:** an E2E run that finds no test host, read as a tooling fault, and a zero-test run exiting 0. **Authority:** `XE-Local-AI-Engine.Tests.E2ETests/XE-Local-AI-Engine.Tests.E2ETests.csproj`.

### TUnit.Playwright caches ONE browser per worker, so per-class launch arguments are silently ignored

**Rule:** `TUnit.Playwright`'s `BrowserTest` registers its browser under one fixed service key per worker, so the first class to launch on a worker decides Chromium's command line and every later class's `BrowserTypeLaunchOptions` is discarded without a warning. A suite needing its own launch switches launches its own browser in a `[Before(Test)]` hook and drives its own page. **Prevents:** two suites that differ by a launch switch silently sharing the first one's, invisible from the test source. **Authority:** `XEFakeAudioE2ETestBase.LaunchFakeAudioBrowserAsync`; upstream `TUnit.Playwright` `BrowserTest.BrowserSetup`. [evidence](../agent-knowledge-evidence.md#tunitplaywright-caches-one-browser-per-worker-so-per-class-launch-arguments-are-silently-ignored)

### A test host can never reach `LaunchMode.Desktop` — two independent walls, not a convention

**Rule:** `Program.CreateAppCoreAsync` resolves `launchMode` only when `customization is null`; every
`TestServerWebAppFactory` host passes a `ProgramAppCustomization` and runs `Headless`. Even without that,
`VelopackInstall.IsManaged()` reads `VelopackLocator.Current`, which throws until `VelopackApp.SetLocator` runs (never in
a fixture). A test of an `IDesktopOnlyEndpoint` therefore asserts the endpoint's ABSENCE. **Prevents:** trying to opt a
test into desktop mode through args or env. **Authority:** `Program.CreateAppCoreAsync`, `VelopackInstall.IsManaged`,
`ValidateExecutableEndpointTests`.

## Covered elsewhere

- Development Mode is ON by default in the test host, so a Development-gated surface IS mapped there — `docs/wiki/17-writing-tests.md` ("Per-host knobs (there is no `WithWebHostBuilder`)"); `docs/wiki/09-api-and-hubs.md` (hub table); an absence test builds a factory with `EnableDevelopmentMode = false`, never the shared one
- A silent `return` on the wrong OS reports a green pass, not a skip — `docs/wiki/17-writing-tests.md` ("Self-validating", "A test that only runs on one OS")
- No analyzer in this stack detects an assertion-less TUnit test — `docs/wiki/17-writing-tests.md` ("Self-validating"); Sonar `S2699` recognises only MSTest, NUnit and xUnit test attributes, so it cannot fire on TUnit
- Sleep-then-assert-the-negative can only fail when the code gets slower — `docs/wiki/17-writing-tests.md` ("4. Never wait with `Task.Delay`")
- size a gate from free RAM and check the lock's status before waiting on it — `AGENTS.md` ("Validation")

## Stale beliefs

Superseded claims; the entries above are the active rules.

| Stale belief | Current correction |
|---|---|
| Bash variable `GROUPS` is available. | Bash owns it; use `TEST_GROUPS` (§1). |
| Memory-safe runner defaults to `JOBS=4`; increase in-process width. | The default tracks the box — `JOBS=16` on a host with at least 32 CPUs, `JOBS=10` below that — and process batches, not in-process width, provide the useful concurrency (§1). |
| `dotnet test` cannot discover MTP tests. | `global.json` pins MTP; `dotnet test` works (§1). |
| `if (!OperatingSystem.IsX()) return;` is an acceptable platform guard. | It reports a green pass on every platform that cannot run the test; use TUnit's `[RunOn(OS.Linux)]` / `[ExcludeOn(OS.Windows)]` or `Skip.Test` (§1). |
| Green E2E proves frontend typecheck. | E2E uses Vite-only `build:e2e`; run `pnpm run lint` (§1). |
| Browser E2E is entirely sequential. | It uses disjoint serial and pooled phases (§1). |
| TUnit alternation silently matches zero. | `(A|B)` returns the union; verify with `--list-tests` (§1). |
| SQLite foreign keys are OFF on the node connection, so cascades never fire and a store's delete order IS the referential integrity. | The node enforces them — it always did, through the bundled native default, and now says so in the connection string and in a pragma on every open. Declared cascades fire; an ordered delete is still required for `Restrict` parents, for links with no foreign key at all, and where order itself matters (§1). |
| A test fixture with foreign keys ON diverges from production and can hide a missing child delete. | Inverted: a fixture pinned to `Foreign Keys=False` is the one testing a database the node never has (§1, wiki 17). |
