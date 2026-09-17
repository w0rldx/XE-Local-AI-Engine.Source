# Writing Tests

> Reviewed: 2026-09-15 · Code-grounded.

[Testing & Validation](13-testing-and-validation.md) is the map of what exists and what counts as validated.
This page is the **authoring guide**: where a new test goes, which harness seam to use, and the traps that
make a test flaky, slow, or silently vacuous. Read it before adding a suite.

## 1. Which project does it belong in?

| The thing you are testing | Project |
|---|---|
| An endpoint, hub, hosted service, auth policy, or anything needing the wired node host | `XE-Local-AI-Engine.Tests` |
| MAF/MEAI agent runtime — chat clients, tools, invocation, evals | `XE-Local-AI-Engine.AI.Agent.Tests` |
| A [Graph Workflows](21-graph-workflows.md) parser, state-machine, document, dispatcher, executor or endpoint rule | `XE-Local-AI-Engine.Tests` (`GraphWorkflows/`, `Endpoints/GraphWorkflows/V1/`, `Hubs/`) — the module lives in `Client.Application`, not in the agent runtime |
| An EF migration, an entity store, the AEAD cipher, or the persistence contract | `XE-Local-AI-Engine.Client.Persistence.Tests` |
| A user-visible flow that must survive real routing, real SignalR, and a real browser | `XE-Local-AI-Engine.Tests.E2ETests` |
| A React component, hook, or mapper | colocated `*.test.ts(x)` next to the source in `XE-Local-AI-Engine.Client.React/src/` |
| A release/packaging shell or Python script | `scripts/tests/`, `scripts/compliance/tests/`, `scripts/performance/tests/`, `publish/tests/` |

Prefer the cheapest project that can still fail for the right reason. An endpoint's validation rules belong in
`.Tests`; the browser adds nothing and costs a frontend build.

## 1a. Test principles

Three rules hold for every test in this repo, in any language. The harness sections below exist to make them
cheap to follow, not to stand in for them.

### Independence

A test must not depend on another test having run, on execution order, or on state a previous run left behind.

- Name every row you write with a fresh `Guid`/unique id, or take exclusive access (§2 shared-host rules, §3
  `[NotInParallel]`).
- Restore every process-global mutation — env vars, static caches, ambient culture — in a `finally`/`Dispose`,
  including on failure.
- Never assert a whole-collection count or an empty state unless the class genuinely owns that state.
- Frontend: `restoreMocks`, `unstubEnvs` and `unstubGlobals` are on, and `src/test/Cleanup.ts` runs React Testing
  Library's `cleanup` after each test — a Zustand store, a `localStorage` key or a module-level `let` is still
  yours to reset in `beforeEach`.
- A loopback port obtained by binding `:0` and releasing it is a **candidate**, not a reservation — another
  process on the box can take it before your child process or server binds it. Hold the listener when the port
  is the test's target, and go through `Testing/LoopbackPort.cs` (`Reserve` + `BindWithRetryAsync`) when
  something else must bind it, retrying on the product's own in-use signal.

A test that passes only in isolation is not independent; it is broken and coincidentally green.

### Self-validating

A test ends in an explicit assertion and is binary green/red with no human step in between.

- Assert with `AssertEx.*` (C#), `expect(…)` (Vitest), `assert`/`pytest.raises` (Python), `Should` (Pester). A test
  that only exercises code has verified nothing. `pnpm run validate` runs `CheckTestsHaveAssertions.mjs` over the
  frontend suite; no analyzer in this stack detects an assertion-less TUnit test at all, so on the backend the
  rule is enforced in review only.
- Never read a log line, console output or a report to decide pass/fail. The assertion is the result.
- Never `return` early because the OS, GPU or tool is missing — that reports a green pass. Skip visibly: TUnit's
  own `[RunOn(OS.Linux)]` / `[ExcludeOn(OS.Windows)]` (`using OS = TUnit.Core.Enums.OS;` — that namespace's
  `LogLevel` collides with the logging one) for an OS gate, `Skip.Test("<why>")` after a probe for a capability
  you cannot name up front (`Testing/SymlinkSupport.cs`, `Testing/JunctionSupport.cs`). A guard that depends on
  more than the OS keeps `Skip.Unless(...)` in the body on top of the attribute. `[RunOn]` is invisible to the
  CA1416 platform analyzer, so a method that calls a Linux-only API also carries
  `[UnsupportedOSPlatform("windows")]`. Proof: `XE-Local-AI-Engine.Tests/Testing/PlatformSkipTests.cs`.
- A test that logs a problem and stays green has the same defect as one with no assertion at all.

### Mocking policy

Substitute only at a real collaboration boundary, and take the first of these that works:

1. **The real thing**, when it is fast and deterministic — an in-memory store, a pure function, a real SQLite file.
2. **The repo's fake seam** for that boundary: `FakeOllama` for anything model-dependent,
   `RecordingHubMessageSender` for WorkerHub outbound, `Fixtures/FakeWorkerNodeFixture.cs` only when the transport
   itself is the subject, MSW handlers for frontend network calls.
3. **`Substitute.For<T>()`** — NSubstitute, never Moq or FakeItEasy, both banned — or `vi.fn()`/`vi.mock()` on the
   frontend, or stdlib `unittest.mock` in Python (not `pytest-mock`).
4. **A hand-written fake**, only when `Returns`/`Received` genuinely cannot express the behaviour. Reaching this
   rung is a signal to re-check rung 2.

`NSubstitute.Analyzers.CSharp` runs on all four test projects, so a substitute against a non-virtual member is a
build error instead of a silently passing test.

Never mock the thing the test exists to verify — a security or approval gate, the AEAD cipher, a migration's schema
change. Substituting those proves only that the test calls the mock.

## 1b. Test categories

Every test class carries **exactly one** class-level `[Category(...)]`, from three values:

| Category | What it means |
|---|---|
| `Unit` | Pure logic against in-memory collaborators: no host, no real database, no socket, no child process. A hand-written fake or an NSubstitute double is still `Unit`. |
| `Integration` | Deterministic, but boots something real in-process: a `TestServerWebAppFactory` host, a real SQLite file (including a `MigratedDatabaseTemplate` copy), a `FakeOllama`/`FakeDocker` server on a loopback socket, or a real child process. |
| `ExternalInfra` | Needs infrastructure the box may not have: a container daemon, a model runtime or live server, a GPU, a privileged sandbox binary. Opt-in through an environment variable, and **every** test in the class skips visibly when that gate is unset. Never part of the default gate. |

The line between `Unit` and `Integration` is **mechanism, not speed**: what the test starts decides its category,
not how long it takes. The gate runs `Unit` and `Integration`; the split exists so a failure's *class* is readable
from the lane that reported it.

Filter by category with TUnit's `--treenode-filter` property syntax — four path segments, then the property in
brackets (`!=` excludes; combine properties inside one bracket, `[(Category=Unit)&(Other=Value)]`):

```bash
--treenode-filter '/*/*/*/*[Category=Unit]'
--treenode-filter '/*/*/*/*[Category!=ExternalInfra]'
```

Write the constant, never the string: `[Category(TestCategories.Unit)]`. Each test project declares its own
`Categories/TestCategories.cs` and imports it globally from the project file, so the attribute needs no `using`.
One trap the compiler will not tell you about: `System.ComponentModel` declares a `CategoryAttribute` too, and a
file-scoped `using` of that namespace silently wins over TUnit's — the class then carries no category at all while
the source still reads correctly. A file that imports that namespace therefore also aliases
`using CategoryAttribute = TUnit.Core.CategoryAttribute;`.

`Architecture/TestCategoryConventionTests.cs` is the guard. It fails on a class with `[Test]` methods that
carries no category or more than one (by reflection in its own assembly, by source scan in the sibling
projects), and on a `Unit` class that reaches an Integration mechanism — the host factory, a real SQLite
connection, a fake server, a real socket or a child process — directly or through any helper. Only the primitives
are written down; the helper list is derived at run time, so a new fixture is covered the day it lands, and there
is no allowlist. The written-down list includes the product composition root (`Program.CreateAppAsync`,
`builder.AddServices`, `AddNodeApplication`, `AddNodeModelRuntime`) — `AddNodeModelRuntime` is the only place in
the whole DI path that calls `UseSqlite`, so a test that touches any of those four reaches a real SQLite database
through product code that no scan over test sources could follow.

What the guard deliberately does **not** check is that an `ExternalInfra` class is tagged as one. "Every test in
this class is gated" is not decidable from a scan: the gate is usually a private helper several call levels below
the test, and some classes are **mixed** — `WhisperRuntimeLiveSmokeTests` and `LlamaServerAdapterIntegrationTests`
each pair gated live tests with tests that run unconditionally, so they are `Integration`, not `ExternalInfra`.
Tagging a mixed class `ExternalInfra` would drop its ungated tests out of the default gate. That call stays with
the reviewer — but the half that IS decidable is enforced: a `*RealDaemonTests` class must be `ExternalInfra`,
and a `*LiveTests`/`*LiveSmokeTests` class must not be `Unit`.

### Naming

A test method name is **subject + scenario + expected outcome**, underscore-separated, matching the dominant shape
in every project: `Subject_WhenScenario_ExpectedOutcome` — `Upload_WhenOversize_Rejects`,
`Import_WhenTheSelectedFolderInputIsRejected_ReturnsBadRequest`. Drop the middle part only when the subject has a
single scenario worth naming (`MigratedSchema_MatchesWhatEnsureCreatedBuilds`). A name that states the mechanism
instead of the outcome ("…_Works", "…_Test") hides what broke when it reds.

## 2. `TestServerWebAppFactory` — the backend host fixture

`XE-Local-AI-Engine.Tests/TestServerWebAppFactory.cs` builds the real app through `Program.CreateAppAsync` and
serves it on `TestServer`. Never reintroduce `WebApplicationFactory<Program>` (docs/agent-knowledge.md §1).

### Shared per class vs one host per test

Building a host is the single most expensive thing a backend test does. **Share one per class when you can:**

```csharp
[ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
public required TestServerWebAppFactory Factory { get; init; }
```

Then a test body starts `var factory = Factory;` instead of `await using var factory = new TestServerWebAppFactory();`.
See `Integration/ApplicationStartupTests.cs` and `Endpoints/ModelFit/V1/ModelFitEndpointTests.cs`.

Share only when **all three** hold:

1. Every test either is read-only against host state, or names the rows it writes with a fresh `Guid`/unique id.
2. No test asserts a **whole-collection count** or an empty state (`items.Length == 1`, `AssertEx.Empty(store)`).
   Those assertions are what a sibling test's leftover row breaks.
3. Every test uses **identical factory configuration**.

A class may mix the two: give the shared host to the default-configuration tests and let the one test that
overrides configuration keep `await using var factory = new TestServerWebAppFactory { … }`. That is the shape of
`Chat/NodeChatEndpointTests.cs` and `Endpoints/Agents/GetAgentPlaybookMonitorEndpointTests.cs`.

Known blockers, so you do not rediscover them: skill **names carry a unique index**
(`Configurations/AgentSkillConfiguration.cs`), `/api/local/v1/auth/setup` bootstraps a **single admin per host**,
seeded agent templates are idempotent on a unique `seed_slug`, and resolving `TracerProvider` registers a
**process-global** `ActivityListener` (why `ApiFoundation/BackendTraceCorrelationTests.cs` stays per-test).
Agent-definition *names* are indexed but **not** unique, so duplicate seed names are safe.

### When a test fails condition 1 or 2, scope the read before you take a private host

A test that reads a container-singleton fake's history by POSITION — `fake.Objectives[^1]`, `fake.Created.Count`,
`fake.Calls.Single()` — fails conditions 1 and 2 on a shared host, because that history is every sibling's too. The
fix is usually not a private host: read the same value off the row the test's own run owns, and the assertion both
survives sharing and names what it is about. `DevWorkflowHarness.ReadObjectiveAsync(runId, nodeKey)` is the worked
example — it answers the objective from the node run's own session row instead of the fake's list, which turned
seventeen positional reads in `DevWorkflows/Execution/DevWorkflowAgentExecutorTests` into id-scoped ones.

**Never buy eligibility by resetting shared state.** `ClearReceivedCalls()`, clearing a fake's list, or deleting rows
between tests makes the suite order-dependent instead of independent. If no id-scoped read expresses the claim — a
host-wide switch, a signal channel that drains, an absolute row count — that test keeps its own host and says so at
the construction site.

### Per-host knobs (there is no `WithWebHostBuilder`)

| Init property | Use it for |
|---|---|
| `ConfigureAdditionalTestServices` | Swap a service: `services.RemoveAll<IFoo>(); services.AddSingleton(stub);` |
| `AdditionalConfiguration` | Last-wins overlay of configuration keys |
| `EnableDevelopmentMode` | Turn Development Mode on/off for this host |
| `EnvironmentName` | Override the host environment — the way to exercise **production-only middleware** such as the rate limiter, which the `Testing` environment skips |
| `SkipDefaultBaseUrlOverride` | Let the platform base URL be missing/invalid, to assert startup validation |

Auth helpers: `CreateNodeAccessToken()` / `AddNodeBearerToken(request)` mint an **operator** JWT;
`CreateNonOperatorAccessToken()` / `AddNonOperatorBearerToken(request)` mint an authenticated principal that
**fails** the operator policy — use that pair to prove a route is operator-gated, not merely authenticated.
If your test also persists the `node-admin-test` Identity row, seed
`TestServerWebAppFactory.NodeAdminTestSecurityStamp` verbatim or the fail-closed stamp check rejects the token.

## 3. Parallelism

TUnit runs classes — and tests within a class — in parallel.

- **Bare `[NotInParallel]` is a run-alone guard.** It means "nothing else while this runs". Do **not** give it a
  key to "make it stricter"; a key does the opposite.
- **Keyed `[NotInParallel("X")]` serializes on the shared resource `X`.** Every test that touches `X` must use
  the same key. Live examples: `[NotInParallel("XE_NODE_SQLITE_KEY")]`, `[NotInParallel("DevelopmentFeatureConfiguration")]`,
  `[NotInParallel("XE_LLAMACPP_OVERRIDE_ENV")]`. A test that touches several variables keys on all of them:
  `Hosting/EngineCommandDispatchTests.cs` keys on `XE_DATA_DIR` + `XE_ADMIN_EMAIL` + `XE_ADMIN_PASSWORD`.

**Environment variables are process-global.** A test that calls `Environment.SetEnvironmentVariable` must (a)
carry a keyed `[NotInParallel("<VARIABLE_NAME>")]` and (b) restore the previous value in a `finally`/`Dispose`,
including on failure. Leaking a variable poisons every later test in the module, in a way that reads as an
unrelated failure. See `Providers/LlamaServer/OverrideSelectorAndOptionsTests.cs`.

## 4. Never wait with `Task.Delay`

A sleep is either flaky (too short on a loaded box) or slow (too long everywhere). Use:

- a `TaskCompletionSource` the code under test completes — `Shutdown/WorkerShutdownDrainServiceTests.cs`,
  `Connection/WorkerHubConnectionSignalRIntegrationTests.cs`;
- `Microsoft.Extensions.Time.Testing.FakeTimeProvider` to advance time deterministically —
  `Capabilities/CapabilityReporterTests.cs`, `Interaction/AskUserToolHandlerTests.cs`;
- an unbounded `Channel` plus a bounded read, which is how `Fixtures/FakeWorkerNodeFixture.cs` turns "did the node
  send X?" into a wait with a real timeout and a legible `TimeoutException`.

The same holds on the frontend: `setTimeout`, or `await new Promise(r => setTimeout(r, n))`, is the identical
defect. Use `vi.useFakeTimers()` or `waitFor`.

**"X did not happen" needs the same discipline.** Sleeping N ms and then asserting nothing happened can only fail
when the code gets *slower* — a real regression that fires the event late still reads green. Instead drive the code
to an observable blocking point through a gate the test controls, assert the negative there, then release the gate
and assert the positive. `AssertEx.StaysIncompleteAsync(task, message)` is the negative, called once the code has
observably reached its gate; `AssertEx.CompletesAsync(task, TestBudgets.Contended, message)` is the positive, with
a failure deadline. Both build on `AssertEx.SettleAsync()`, the primitive that drains the scheduler — it is
deterministic only when everything in flight is a thread-pool continuation, so a real timer or real I/O between the
call and the gate still needs a "reached" signal from a fake.

A real timer is allowed only when the subject is a real OS process, or when the delay is the subject's own input.
Mark it with a `// real-timer:` comment naming why, so a later reader does not "fix" it into a fake clock.

## 5. Recipes

### Model-dependent behaviour → FakeOllama

Unless `RUN_LOCAL_INTEGRATION=true`, the fixture starts a `FakeOllamaServer` seeded with
`["qwen3.5:0.8b", "qwen3-embedding:0.6b"]` and points the provider at it. Script it through the test-control
endpoints (`POST /test/script`, `POST /test/failures`, `GET /test/requests`) or by passing `FakeOllamaOptions`
to the factory constructor. Embeddings are SHA256-seeded and therefore stable
(`Determinism/EmbeddingDeterminism.cs`). Flip `RUN_LOCAL_INTEGRATION=true` only for a deliberate fidelity run
against a real local runtime — never as a CI default.

### WorkerHub outbound behaviour → `RecordingHubMessageSender`, not a real host

`XE-Local-AI-Engine.Client.Testing`'s `RecordingHubMessageSender` decorates the real `IHubMessageSender` and
records every outbound call with a monotonic sequence number. Use it whenever the question is *what the node
sends*. Reach for `Fixtures/FakeWorkerNodeFixture.cs` — a real loopback Kestrel + SignalR host with a self-signed
certificate — only when the transport itself is the subject: negotiation, heartbeat cadence, or reconnect
(`FireTransportLevelConnectionDropAsync()` drops the transport with no close frame so `WithAutomaticReconnect`
engages; `FireConnectionDropAsync()` closes gracefully and the client deliberately does **not** reconnect).

### A local SignalR hub

Point a real `HubConnection` at the fixture's in-memory transport — no sockets:

```csharp
await using var connection = new HubConnectionBuilder()
    .WithUrl("http://localhost" + LocalApiRoutes.LocalChat.Hub, options =>
    {
        options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
        options.AccessTokenProvider = () => Task.FromResult<string?>(factory.CreateNodeAccessToken());
        options.Headers.Add("Origin", "http://localhost");
    })
    .Build();
```

The `Origin` header is not optional — the loopback Host/Origin guard rejects a hub negotiate without it, and a
foreign origin must be asserted to 403. Pattern: `Chat/NodeChatHubTests.cs`, `Endpoints/Scheduler/SchedulerHubTests.cs`.

### A hosted / background service

Do not start the whole host and hope the loop ran. Resolve the service, drive one iteration through
`Testing/BackgroundServiceTestHelper.RunExecuteAsync(service, ct)`, and cancel the token to end it. Give the class
a keyed `[NotInParallel(nameof(YourBackgroundServiceTests))]` when it touches a shared timer or connection —
`BackgroundServices/AutoConnectBackgroundServiceTests.cs` and `BackgroundServices/HeartbeatBackgroundServiceTests.cs` do.

### An EF migration

Every migration that changes table/column/index shape ships a `<MigrationName>MigrationTests.cs` in
`XE-Local-AI-Engine.Client.Persistence.Tests` (the file follows the migration name, not an `Add*` prefix).
The shape is: start from the **preceding** migration id, insert historical rows, `MigrateAsync()` to head, then
assert the resulting schema — see `AddAgentDefinitionsMigrationTests.cs`. Use the shared schema probe
`XE-Local-AI-Engine.Client.Persistence.Tests/Testing/MigrationSchemaProbe.cs` for the table/column/index queries
rather than hand-rolling `PRAGMA` SQL per file. Remember `MigrateAsync()` applies *every* later migration too, so
assert the columns you added exist — not that the table's column set is exactly yours.

**A database at head, or at migration N: copy the template.** Replaying the declared chain against an empty file
is the dominant per-test cost in this project, and almost no test needs it. `Testing/MigratedDatabaseTemplate.cs`
builds each state once per process — keyed by the migrations assembly's module version id, so a rebuild
invalidates it — and every consumer gets a `File.Copy`. Reach for it as:
`MigrationSchemaProbe.FromChatTemplateAsync(file)` / `FromChatTemplateAsync(file, predecessorId)` /
`FromIdentityTemplateAsync(file)` for a probe, or `MigratedDatabaseTemplate.CopyChatHeadAsync(path)` /
`CopyChatAtAsync(path, predecessorId)` when the suite opens its own context. A `WhenRolledBack_*` test copies the
**head** template and then runs the down migration for real; a `WhenApplied_*` test copies the **at-(N-1)**
template and then runs the tail for real. Both still exercise the migration they are named after.

A suite fixture that builds its schema with `EnsureCreatedAsync()` is the same mistake wearing a different hat — a
second definition of the schema, off the entity model. `GraphWorkflowTestFixture` copies the template instead;
the shared fixtures `DevWorkflowTestFixture`, `ExternalAppTestFixture`, `IntegrationTestFixture`,
`WorkSessionPersistenceTestSupport` and `DevelopmentPersistenceTestSupport` have not been moved, and dozens of store
suites still call `EnsureCreatedAsync()` inline — that is the remaining backlog. One test keeps `EnsureCreated` on
purpose: `AddGraphWorkflowsMigrationTests.MigratedSchema_MatchesWhatEnsureCreatedBuilds` holds its own context through
`GraphWorkflowTestFixture.CreateEnsureCreatedSchemaAsync`, because a parity test whose two sides both came from the
migrations asserts nothing. Either way the file is in WAL mode — `EnsureCreated` enables it exactly as the template
build does — and the at-rest scans stay honest because `SqliteFileProbe.ReadAllBytesAsync` closes the last
connection first, which checkpoints the log back into the main file.

Keep the from-empty replay (`MigrationSchemaProbe.MigrateChatAsync` / `MigrateIdentityAsync`) where the replay is
the thing under test, or where the assertion can see how the file was produced: a test that asserts the pending
migration set, that reads the migrator's own pre-migration backup file, that asserts a file was created, or that
asserts a journal mode or a `PRAGMA`. `MigrationChainTests` is the standing example — it asserts that the applied
set equals the declared set, which a template would make vacuous. Data seeded before migration N belongs on the
at-(N-1) template plus the real tail, never on a head copy.

A migration that converts, repairs or deletes **data** needs rows to convert, or it is only being tested as a
schema change. The probe's three-step seam is `FromChatTemplateAsync(file, predecessorId)` →
`ExecuteAsync(insert…)` → `MigrateToAsync(thisMigrationId)`: copy the at-(N-1) template, seed the historical
rows through raw SQL (the entity model describes head, not the schema those rows were valid under), then run
exactly the one migration over them for real and assert what it did. `EncryptConversationTitleMigrationTests.cs`
(titles cleared) and `RepairAndUniqueMessageSequenceMigrationTests.cs` (colliding sequences renumbered) are the
worked examples.

### React components

Render through the shared provider wrapper `src/test/RenderWithProviders.tsx` (Mantine theme, TanStack
Query, router) instead of bare `@testing-library/react` — a bare render loses the providers most components
need. Translations need no wrapper: `src/i18n.ts` is a Vitest `setupFiles` entry, so every file resolves `t()`
against the shipped `en` bundle and must assert that string rather than the in-code `defaultValue`. Network goes
through the MSW handlers in `src/test/msw/`; assert against handlers, not against a mocked
`fetch`. `src/test/PinLocale.ts` is already wired as a Vitest `setupFiles` entry, so locale is deterministic; so is
`src/test/Cleanup.ts`, which runs React Testing Library's `cleanup` after every test (Vitest does not register it
for you without `globals`). `restoreMocks`, `unstubEnvs` and `unstubGlobals` are on in `vite.config.ts`, so spies
and env stubs reset themselves — store and `localStorage` state does not. Every test needs a visible `expect(…)`:
`pnpm run validate` runs `CheckTestsHaveAssertions.mjs` and fails on a test without one.

### A test that only runs on one OS

Gate it with TUnit's `[RunOn(OS.Linux)]` / `[ExcludeOn(OS.Windows)]` (`OS` is `TUnit.Core.Enums.OS`, imported as
`using OS = TUnit.Core.Enums.OS;` because that namespace's `LogLevel` collides with the logging one), never with
`if (!OperatingSystem.IsWindows()) return;` — an early return reports a green pass on every platform that cannot
run the test. Both are `SkipAttribute` subclasses whose reason names the platform; the proof test is
`XE-Local-AI-Engine.Tests/Testing/PlatformSkipTests.cs`. A guard that depends on more than the OS keeps
`Skip.Unless(...)` in the body on top of the attribute. `[RunOn]` is invisible to the CA1416 platform analyzer, so
a method that calls a Linux-only API also carries `[UnsupportedOSPlatform("windows")]`. When the gate is a
capability you have to probe rather than name (symlinks, NTFS junctions), probe once and call `Skip.Test("<why>")`,
the shape of `Testing/SymlinkSupport.cs` and `Testing/JunctionSupport.cs`.

### Browser E2E

Pick a base class, and pick it deliberately:

- **`Common/XEPooledE2ETestBase.cs`** (group `BrowserPooled`) — the default. The test leases one of the seeded
  pool users for its duration, so several browsers run at once. Requires that the test only reads node-global
  state or writes `Guid`-named rows.
- **`Common/XESerialE2ETestBase.cs`** (group `BrowserSerial`) — for tests that mutate session-global state (the
  `WorkerEventDispatcher.CurrentInvocation` slot, FakeOllama scripts/models, the admin's tutorial row) or assert a
  node-wide empty state. Runs one at a time as the canonical admin.

The two groups run as disjoint phases, but **which phase runs first is not guaranteed** — never write a test that
depends on the other group having run. Selectors are **testid-first** (`Page.GetByTestId("agent-create-button")`);
note Mantine puts `data-testid` on the `<input>` for `TextInput` but on the **wrapper** for `Textarea`, so a
textarea needs `[data-testid="…"] textarea`. Traces are captured and written to `test-results/traces/*.zip`
**only on failure** — that zip is the first thing to open when CI reds.

## 6. Running what you changed

```bash
# Backend — one class, or an alternation of several
scripts/with-build-lock.sh -- dotnet build XE-Local-AI-Engine.Tests/XE-Local-AI-Engine.Tests.csproj -c Release
scripts/with-build-lock.sh -- scripts/assembly-guard.sh guard --test-bins -- \
  dotnet test XE-Local-AI-Engine.Tests/XE-Local-AI-Engine.Tests.csproj -c Release --no-build \
    --treenode-filter '/*/*/(FooTests|BarTests)/*'

# Frontend
cd XE-Local-AI-Engine.Client.React && pnpm test -- src/features/agents

# E2E
scripts/run-e2e-local.sh --filter '/*/*/AgentsPageE2ETests/*'      # --list enumerates without running
```

- `--treenode-filter`, **never** VSTest's `--filter`. Wildcards and `(A|B)` alternation both work.
  A filter that matches nothing exits **8**; a zero-test run is not a pass.
- Wrap every build and every test run in `scripts/with-build-lock.sh`, and **never** build while a test run is in
  flight — the box is shared. Exit `75` means the result is void; rerun.
- Iterate in Debug if you like, but **finish with a Release build** of the solution. Debug skips the analyzers
  entirely, so a green Debug build has verified none of the static-analysis wall.
- A bare `TODO`/`FIXME` in a C# comment **fails the Release build** (Sonar S1135 + warnings-as-errors). Describe
  the present limitation or rationale directly without `TODO`/`FIXME` or task markers.

## Related pages

- [Testing & Validation](13-testing-and-validation.md) — topology, validation commands, CI gates, RC evidence
- [Code Organization Conventions](16-code-conventions.md) — where a file goes
- [API & Hubs](09-api-and-hubs.md) — the endpoint/hub surface under test
- [Data & Persistence](08-data-and-persistence.md) — entities and the migration timeline
- [React Client](10-react-client.md) — the frontend under test
- [Home](Home.md)
