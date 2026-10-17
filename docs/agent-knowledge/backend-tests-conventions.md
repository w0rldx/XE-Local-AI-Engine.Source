# Backend test conventions

Scope: writing TUnit/MTP backend tests: host fixtures, temp state and ports, SQLite test databases, node-settings and
cancellation test traps, and the browser E2E host. Running, scoping, the build lock and flakes are in
[backend-tests-runner](backend-tests-runner.md); test principles live in `docs/wiki/17-writing-tests.md`. Read when:
writing or debugging a backend test, or changing `TestServerWebAppFactory`, a `Testing.*` project, a test fixture or
the browser E2E host.

## Fixtures, hosts and temp state

### A test-support executable must not name its entry type `Program`

**Rule:** an executable under a `Testing.*` project names its entry class after the project (`FakeOpenAiGatewayHost`), never `Program`. The test-category scan covers the `Testing.*` projects and derives its integration markers from untagged top-level types by simple name, so a second `Program` is either ambiguous (never a marker) or, when it uses a hosting primitive, becomes a marker itself and reclassifies every test that names the node's `Program`. **Prevents:** `TestCategoryConventionTests` reclassifying unrelated tests after a fake gains an entry point. **Authority:** `TestCategoryConventionTests.DeriveMarkers`; the `FakeOpenAiGatewayHost` remark.

### The full Tests module balloons to ~3.5 GB — it is a framework leak, not a fixture bug

**Rule:** use `TestServerWebAppFactory`, never `WebApplicationFactory<Program>`. Keep the per-host JSON options, `SqliteConnection.ClearAllPools()` and the Testing rate-limiter bypass: `ConfigureServices` computes every permit limit outside its lambda (capturing ints, never `builder`) and relaxes them under `Testing`. Share a per-class host only for read-only or Guid-keyed tests, never for counts, global state, DI swaps, lifecycle or long-lived listeners. **Prevents:** process-lifetime host leaks and shared-host races. **Authority:** `TestServerWebAppFactory`; `ConfigureServices.cs`. [evidence](../agent-knowledge-evidence.md#the-full-tests-module-balloons-to-35-gb--it-is-a-framework-leak-not-a-fixture-bug)

### A test host owns ONE EF internal service provider; never let it use EF's static cache, never let it rebuild per scope

**Rule:** `EntityFramework:ServiceProviderCaching=false` (set by `TestServerWebAppFactory`) makes `AddNodeModelRuntime` register one `NodeEfInternalServices` per host for both node DbContexts' `UseInternalServiceProvider`; production keeps EF's cached provider. Register interceptors in `NodeEfInternalServices` and keep `ConfigureWarnings` on the cached branch only: EF refuses both through the options under an internal provider. **Prevents:** an immortal per-host EF cache entry, or a rebuild per DbContext scope. **Authority:** `NodeEfInternalServices`; `AddNodeModelRuntimeExtensions.AddNodeModelRuntime`. [evidence](../agent-knowledge-evidence.md#the-full-tests-module-balloons-to-35-gb--it-is-a-framework-leak-not-a-fixture-bug)

### Test hosts leak temp files to `Path.GetTempPath()` — keep the fixture cleanup

**Rule:** keep `TestServerWebAppFactory.DisposeAsync` deleting SQLite sidecars, node-data and temporary `wwwroot` directories, and delete a registered temp path whether file or directory. A killed run cannot dispose; recover with `find /tmp -maxdepth 1 \( -name 'xe-local-ai-engine-tests-*' -o -name 'xe-local-ai-engine-persistence-*' -o -name 'agenthome-proc-*' \) -exec rm -rf {} +` (never a huge shell glob: `ARG_MAX`). The SQLite templates live under `bin/`, not in that sweep. **Prevents:** a tmpfs `/tmp` filling across runs. **Authority:** `TestServerWebAppFactory.DisposeAsync`; `AgentHomeProcessWriteBackLoopTests`.

### The one temp artifact that is meant to survive: the migrated SQLite template

**Rule:** the MVID-keyed migrated templates (`TestServerWebAppFactory.BuildMigratedTemplate`, `MigratedDatabaseTemplate.BuildAsync`) survive teardown in `<test bin>/sqlite-templates/` and each build sweeps other-MVID files. Delete it (or `bin/`) to rebuild; set `UsePreMigratedDatabase=false` only to test migrations. Never move templates to a directory two worktrees share: the sweep is safe only per worktree and build output. **Prevents:** orphaned templates filling shared temp space. **Authority:** `TestServerWebAppFactoryTemplateSweepTests`; `scripts/run-tests-memory-safe.sh` (`prewarm_template`). [evidence](../agent-knowledge-evidence.md#the-one-temp-artifact-that-is-meant-to-survive-the-migrated-sqlite-template)

### A test that needs a migrated SQLite database copies the template — replaying the chain is the exception

**Rule:** the copy recipe, and when to keep the from-empty replay, are in `docs/wiki/17-writing-tests.md` ("An EF migration"); never feed `MigrationChainTests` a template. Never "fix" `MigratedDatabaseTemplate` refusing to publish while a `-wal`/`-shm` exists by disabling WAL. **Prevents:** a single-file copy losing rows still in the log, and a vacuous chain test. **Authority:** `MigratedDatabaseTemplate`, `MigratedDatabaseTemplateTests`; `TestServerWebAppFactory.BuildMigratedTemplate`.

### A test host whose content root is the real Client source dir must register a fake `INodeDataDirectory`

**Rule:** a fixture combining the real Client content root with redirected node data removes `INodeDataDirectory` and registers `FakeNodeDataDirectory`. **Prevents:** first-launch migration moving credential and settings files out of the checkout, and teardown deleting them. **Authority:** `ServiceProviderValidationTests.HostCreation_LeavesTheContentRootNodeSettingsWhereItIs`.

### A node-settings save path must carry the local-only members over from the stored record

**Rule:** `StoredNodeSettings` holds members the wire DTO never carries (`MachineKey`, `ToolApprovalPolicy`); persisting a fresh record built from the request erases them. Never add `MachineKey` to a request or response DTO. **Prevents:** a lost `MachineKey` silently orphaning every frozen inference profile. **Authority:** `NodeSettingsEndpointTests`; `NodeSettingsAdministrationServiceTests`. [evidence](../agent-knowledge-evidence.md#a-node-settings-save-path-must-carry-the-local-only-members-over-from-the-stored-record)

### A node-settings save takes the machine key from the record at write time

**Rule:** persist through `INodeSettingsStore.UpdateAsync` and take the key from the write-time record (`settings with { MachineKey = latest.MachineKey }` in `NodeSettingsAdministrationService.ValidateAndSaveAsync`): `IMachineKeyProvider` can mint between a save's load and its write. Never write with a bare `SaveAsync` built from an earlier `LoadAsync`. **Prevents:** a save overwriting a key minted since its load. **Authority:** `NodeSettingsAdministrationServiceTests`; `MachineKeyProviderTests`. [evidence](../agent-knowledge-evidence.md#a-node-settings-save-path-must-carry-the-local-only-members-over-from-the-stored-record)

### `ExternalAppEntityConfigurationTests` pins the external-app instance column list

**Rule:** `InstanceTable_MapsEveryColumnToItsSnakeCaseName` holds every `external_app_instances` column as a literal array checked in both directions; a new `ExternalAppInstance` property extends it in the same commit as the entity, Fluent mapping and migration. **Prevents:** the whole `XE-Local-AI-Engine.Client.Persistence.Tests` project redding with what reads like an unrelated failure. **Authority:** `ExternalAppEntityConfigurationTests`.

### Never infer this node's foreign-key posture from a connection string — measure `PRAGMA foreign_keys`

**Rule:** read the posture with `PRAGMA foreign_keys`, never from a string, comment or fixture (mechanism: `docs/wiki/08-data-and-persistence.md`); keep both layers, the string's `ForeignKeys = true` and the pragma applier. Explicit ordered deletes stay required: `Restrict` rejects parents deleted first; tables with no declared FK (`integration_executions.session_id`, `training_work_items.target_id`) need the delete; a cascade or SET NULL promises no order. Never hand-write a delete where a cascade is declared. **Prevents:** audits built on the wrong posture. **Authority:** `NodeSqlitePragmasTests`; `DesktopBootstrap.EnsureLocalDataConfiguration`. [evidence](../agent-knowledge-evidence.md#never-infer-this-nodes-foreign-key-posture-from-a-connection-string--measure-pragma-foreign_keys)

### `SqliteParameter.AddWithValue(Guid)` binds a BLOB, but EF stores this schema's Guids as TEXT

**Rule:** in a raw `SqliteCommand` against an EF-created database, bind a Guid key as `guid.ToString()` or filter on a non-Guid column; SQLite silently compares BLOB and TEXT as unequal. **Prevents:** a raw read-back matching zero rows, so the test asserts over nothing. **Authority:** `TranscriptionSessionStoreTests.ReadRawSegmentTextAsync` (filters on `seq`); `AddAgentDefinitionsMigrationTests` (binds `conversationId.ToString()`).

### Deleting a dead service can delete a live control's only tests — grep the test file first

**Rule:** before deleting a test file with the service it was written for, grep it for the other production symbols it exercises and re-home whatever is still live. **Prevents:** silently dropping a live control's only coverage; coverage thresholds are project-wide, so no gate notices. **Authority:** `MemoryProposalSecretScannerTests` (re-homed this way). [evidence](../agent-knowledge-evidence.md#deleting-a-dead-service-can-delete-a-live-controls-only-tests--grep-the-test-file-first)

### A tamper test on an acquisition sidecar must keep it self-consistent, or the registry repairs it first

**Rule:** `GgufModelRegistry.ListAllAsync` rewrites a shape-invalid or revision-inconsistent sidecar from the manifest before `InstalledGgufSnapshotStore` sees it, so a digest-only tamper never reaches the store's check; recompute `WeightMemberFingerprint`, `RegistrySourceRevision` and `RegistryRevision` when tampering. **Prevents:** a green "rejects a wrong sidecar" test that never exercised the rejection. **Authority:** `InstalledGgufSnapshotStoreTests` (SeedAcquiredAsync); open-items O2, 2026-09-28.

### `StubNodeRuntimeSettings` feature switches default ON: disable a feature in the stub, not only in options

**Rule:** a test that needs a feature off sets it on the stub (`With…Enabled(false)`) or in the stored settings: the stub's switch getters return `true` by default and consumers read the accessor, never `IOptions<T>.Enabled`. Bound any loop the test drives with a `CancellationToken` that fires. **Prevents:** an options-only disable running the enabled path, and a timer loop under `CancellationToken.None` with an unadvanced `FakeTimeProvider` hanging the gate batch silently. **Authority:** `AgentHomeRunRetentionServiceTests`, `SchedulerHistoryRetentionServiceTests`.

### Development Mode is ON by default in the test host, so a Development-gated surface IS mapped there

**Rule:** a test asserting that a Development-gated endpoint or hub is ABSENT builds its own factory with `EnableDevelopmentMode = false`, never the shared host, where the surface is mapped. **Prevents:** an absence test that fails against the shared host, or is "fixed" by weakening its assertion. **Authority:** `TestServerWebAppFactory.EnableDevelopmentMode`; `docs/wiki/17-writing-tests.md` ("Per-host knobs").

### TUnit `[Arguments]` cannot read a `private const` of the test class
**Rule:** a constant referenced from `[Arguments(...)]` is re-emitted by TUnit's source generator in a generated class, so a `private const` on the test class fails the Release build with CS0122 inside the generated source while the IDE shows the test file clean. Declare such constants `internal const`. **Prevents:** a red Release build whose error points at a generated file nobody wrote. **Authority:** `SandboxSecurityProfilePolicyTests` capability constants; sandbox-security-profile round, 2026-10-08.

### `NodeSettingsEndpointTests.Persisted(mutate)` replays the mutation on a FRESH record
**Rule:** `Persisted(mutate)` applies the save endpoint's mutation to a fresh `StoredNodeSettings`, and the mutation returns `latest` unchanged when the stored record differs from the one it validated. A round-trip test seeded with a non-default record therefore passes its endpoint half and fails its persisted half with the field at its default. Start such a test from `NewSettingsStore()`, or pass the same seeded record to `Persisted(mutate, stored)`. **Prevents:** chasing a "setting not saved" failure that is only the fixture's fresh record. **Authority:** `NodeSettingsEndpointTests.Persisted`, `NewSettingsStore`.

### A constructor that newly requires a service breaks hand-built test containers the focused lanes never run
**Rule:** some tests build a real `ServiceCollection` and resolve the type under test through it (`McpServerConnectionManagerDiTests`, `DevelopmentValidationReviewAndApplyTests`); a constructor that gains a required dependency compiles, passes the class's own tests (they use the builder or a fake) and fails only those DI tests at resolve time. When a constructor gains a parameter, grep the test tree for `AddSingleton<I...>` registrations of the type and run those classes, or run the full gate before hand-off; the touched-classes filter does not catch it. **Prevents:** a green focused lane followed by a red full gate on a `ServiceProvider` resolve. **Authority:** `McpServerConnectionManagerDiTests`, `DevelopmentValidationReviewAndApplyTests`; sandbox-security-profile round, 2026-10-09.

### A substituted trust resolver answers null grants

**Rule:** an `IModelTrustResolver` built with `Substitute.For<T>()` returns null from `ResolveCloudGrantsAsync` and `ClassifyCloudGrants` unless configured. Any test whose trust answer is not `Local` must stub both to `ExternalProviderCloudGrants.None` (or use `FakeModelTrustResolver`, which mirrors the real rule), or the gate under test dies with a `NullReferenceException` instead of withholding. **Prevents:** a cloud-path test that crashes before it reaches the gate it pins. **Authority:** `GraphWorkflowAgentHostFixture`, `GraphWorkflowResponseSchemaWarningTests.BuildService`, `ModelTrustResolverTests.FakeModelTrustResolver_AnswersGrantsLikeTheRealResolver`.

## Browser E2E host

### a solution build overwrites the E2E test host, so the flagged build must be the LAST one before a `--no-build` E2E run

**Rule:** without `-p:RunE2ETests=true`, `XE-Local-AI-Engine.Tests.E2ETests` builds as a library, so a plain solution build overwrites the MTP test host the flagged build produced. Repeat the flagged build immediately before a `--no-build` E2E run, or use `scripts/run-e2e-local.sh`, which orders this itself. **Prevents:** an E2E run that finds no test host, read as a tooling fault, and a zero-test run exiting 0. **Authority:** `XE-Local-AI-Engine.Tests.E2ETests/XE-Local-AI-Engine.Tests.E2ETests.csproj`.

### TUnit.Playwright caches ONE browser per worker, so per-class launch arguments are silently ignored

**Rule:** the first class to launch on a worker decides Chromium's command line; every later class's `BrowserTypeLaunchOptions` is discarded without a warning. A suite needing its own launch switches launches its own browser in a `[Before(Test)]` hook and drives its own page. **Prevents:** two suites that differ by a launch switch silently sharing the first one's. **Authority:** `XEFakeAudioE2ETestBase.LaunchFakeAudioBrowserAsync`; upstream `BrowserTest.BrowserSetup`. [evidence](../agent-knowledge-evidence.md#tunitplaywright-caches-one-browser-per-worker-so-per-class-launch-arguments-are-silently-ignored)

### A test host can never reach `LaunchMode.Desktop` — two independent walls, not a convention

**Rule:** a test of an `IDesktopOnlyEndpoint` asserts the endpoint's ABSENCE. `Program.CreateAppCoreAsync` resolves `launchMode` only when `customization is null`, and every `TestServerWebAppFactory` host passes one and runs `Headless`; separately, `VelopackInstall.IsManaged()` throws until `VelopackApp.SetLocator` runs, never in a fixture. **Prevents:** opting a test into desktop mode through args or env. **Authority:** `Program.CreateAppCoreAsync`, `VelopackInstall.IsManaged`, `ValidateExecutableEndpointTests`.

### An E2E run rewrites the checkout's node settings

**Rule:** prepare a model-matrix or lab node only AFTER any Playwright E2E run in the same checkout, or run the two in different worktrees. The E2E suite's in-process host uses the checkout's Client project directory as content root, which `NodeDataDirectory` treats as the node root, so it rewrites `XE-Local-AI-Engine.Client/node-settings.json`: an `offline` external-access profile prepared for the matrix was gone after an E2E run and `ExternalAccessProfileBackfillService` stamped `recommended`. The mechanism is unconfirmed; the effect is confirmed. **Prevents:** `scripts/run-model-matrix-local.sh` exiting 2 on its prerequisite "the node's external-access profile must be `offline` … it is 'recommended'" after an unrelated E2E run. **Authority:** `NodeDataDirectory`, `ExternalAccessProfileBackfillService`, the matrix runner's prerequisite check.

## Stale beliefs

Superseded claims; the entries above are the active rules.

| Stale belief | Current correction |
|---|---|
| Browser E2E is entirely sequential. | It uses disjoint serial and pooled phases (§1). |
| SQLite foreign keys are OFF on the node connection, so cascades never fire and a store's delete order IS the referential integrity. | The node enforces them — it always did, through the bundled native default, and now says so in the connection string and in a pragma on every open. Declared cascades fire; an ordered delete is still required for `Restrict` parents, for links with no foreign key at all, and where order itself matters (§1). |
| A test fixture with foreign keys ON diverges from production and can hide a missing child delete. | Inverted: a fixture pinned to `Foreign Keys=False` is the one testing a database the node never has (§1, wiki 17). |
