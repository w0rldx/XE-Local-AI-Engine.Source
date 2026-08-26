# Writing Tests

> Reviewed: 2026-08-15 · Code-grounded.

[Testing & Validation](13-testing-and-validation.md) is the map of what exists and what counts as validated.
This page is the **authoring guide**: where a new test goes, which harness seam to use, and the traps that
make a test flaky, slow, or silently vacuous. Read it before adding a suite.

## 1. Which project does it belong in?

| The thing you are testing | Project |
|---|---|
| An endpoint, hub, hosted service, auth policy, or anything needing the wired node host | `XE-Local-AI-Engine.Tests` |
| MAF/MEAI agent runtime — chat clients, tools, invocation, evals, preview workflows | `XE-Local-AI-Engine.AI.Agent.Tests` |
| An EF migration, an entity store, the AEAD cipher, or the persistence contract | `XE-Local-AI-Engine.Client.Persistence.Tests` |
| A user-visible flow that must survive real routing, real SignalR, and a real browser | `XE-Local-AI-Engine.Tests.E2ETests` |
| A React component, hook, or mapper | colocated `*.test.ts(x)` next to the source in `XE-Local-AI-Engine.Client.React/src/` |
| A release/packaging shell or Python script | `scripts/tests/`, `scripts/compliance/tests/`, `scripts/performance/tests/`, `publish/tests/` |

Prefer the cheapest project that can still fail for the right reason. An endpoint's validation rules belong in
`.Tests`; the browser adds nothing and costs a frontend build.

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
  `[NotInParallel("XE_LLAMACPP_OVERRIDE_ENV")]`.

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
`Connection/AutoConnectBackgroundServiceTests.cs` and `Connection/HeartbeatBackgroundServiceTests.cs` do.

### An EF migration

Every migration that changes table/column/index shape ships a `<MigrationName>MigrationTests.cs` in
`XE-Local-AI-Engine.Client.Persistence.Tests` (the file follows the migration name, not an `Add*` prefix).
The shape is: migrate to the **preceding** migration id, insert historical rows, `MigrateAsync()` to head, then
assert the resulting schema — see `AddAgentDefinitionsMigrationTests.cs`. Use the shared schema probe
`XE-Local-AI-Engine.Client.Persistence.Tests/Testing/MigrationSchemaProbe.cs` for the table/column/index queries
rather than hand-rolling `PRAGMA` SQL per file. Remember `MigrateAsync()` applies *every* later migration too, so
assert the columns you added exist — not that the table's column set is exactly yours.

A migration that converts, repairs or deletes **data** needs rows to convert, or it is only being tested as a
schema change. The probe's three-step seam is `MigrateChatAsync(file, predecessorId)` → `ExecuteAsync(insert…)`
→ `MigrateToAsync(thisMigrationId)`: seed the historical rows through raw SQL (the entity model describes head,
not the schema those rows were valid under), then run exactly the one migration over them and assert what it
did. `EncryptConversationTitleMigrationTests.cs` (titles cleared) and
`RepairAndUniqueMessageSequenceMigrationTests.cs` (colliding sequences renumbered) are the worked examples.

### React components

Render through the shared provider wrapper `src/test/RenderWithProviders.tsx` (Mantine theme, i18n, TanStack
Query, router) instead of bare `@testing-library/react` — a bare render loses the providers most components
need. Network goes through the MSW handlers in `src/test/msw/`; assert against handlers, not against a mocked
`fetch`. `src/test/PinLocale.ts` is already wired as the Vitest `setupFiles` entry, so locale is deterministic.

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
