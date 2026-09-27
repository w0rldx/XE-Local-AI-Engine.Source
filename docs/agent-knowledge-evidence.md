# Agent Knowledge Evidence Ledger

This companion preserves dated measurements, incident summaries, and provenance behind the rules in [the index](agent-knowledge.md) and its topic files under [`agent-knowledge/`](agent-knowledge/). It is **not required reading** for ordinary work and is not the authority for current versions, hardware, workflow status, or command syntax. Re-run volatile probes before relying on them.

Use this file when:

- changing or deleting a rule in `agent-knowledge.md` or a topic file under `agent-knowledge/`;
- reproducing the same failure mode;
- deciding whether a historical workaround is still necessary;
- distinguishing measured evidence from a design inference.

## Provenance and scope

The original knowledge base grew from roughly 135 session-memory notes (June–July 2026), a partial review of about 30 recent commits on 2026-08-01, an August-note pass on 2026-08-07, and a freshness pass on 2026-08-17. It was never a systematic history review. Absence from either document is not evidence that no invariant exists.

The 2026-08-25 compaction retained the actionable rule in the main file and moved these categories here:

- dated machine/tool versions and external GitHub state;
- A/B timings, memory/RSS, throughput, and concurrency measurements;
- long incident narratives and superseded implementation chronology;
- live-model/model-server observations used to validate a rule;
- provenance of workarounds whose current code/test is the primary authority.

## 0. Documentation evidence

### Why symbol citations replaced line citations

A maintenance pass checked about 84 anchors. Five drifted in one run, all in concurrently edited files; untouched files did not drift. One citation remained in range but pointed to a different symbol, one had crossed into the wrong file, and one drifted twice during the same run. Symbol names used by the same documents remained stable. ADR 0004 records the same experience after `ProcessSandboxRuntimeProvider` documentation moved.

## 1. Build, test, CI, and packaging evidence

### Analyzer gate measurements

A full Debug rebuild of `XE-Local-AI-Engine.Tests` measured roughly 84 seconds with analyzers and 10 seconds without. That cost drove the local Debug gate in `Directory.Build.targets`; all authoritative gates still use Release. An incremental Release build can finish in about one second because MSBuild skips unchanged projects, and skipped analyzers do not replay diagnostics.

A Debug build with `RunAnalyzers=false` still discovered 209 tests in `XE-Local-AI-Engine.AI.Agent.Tests`, confirming source generators continued to run at the then-current pin.

### Filter behavior

Measured with TUnit/MTP on 2026-07-24:

- `QuantLadderTests`: 9 tests;
- `DesktopPortStoreTests`: 6 tests;
- `(QuantLadderTests|DesktopPortStoreTests)`: 15 tests, exactly the union;
- a filter with the wrong depth: exit 8, `Zero tests ran`.

Counts and package versions are volatile; `--list-tests` remains authoritative.

### GPU smoke evidence

On the same model and script, the GPU run peaked near 72% utilization with a 1,199 MiB VRAM rise; CPU fallback peaked near 11% with no VRAM rise and produced the same correct answer. This is why answer correctness cannot validate GPU execution.

The smoke's refuse-to-pass logic is tested without a GPU by `scripts/tests/gpu-smoke.test.sh`. Trust the script's printed check count rather than a count copied into documentation.

### CI batching and coverage

The Tests module is unusually sensitive to how coverage is parallelized:

| Shape | Approximate result on the measured box |
|---|---:|
| one process, eight-wide | 11:00 wall, ~10 GB |
| `JOBS=4` batches | 6:02 wall |
| `JOBS=10` batches | 2:18 wall, ~670 MB per batch |
| coverage, 98 namespace processes | 1,991 CPU-s, 7:07 wall |
| coverage, 8 groups | 830 CPU-s, 3:12 wall |
| coverage, 4 groups | 684 CPU-s, 2:43 wall |
| coverage, one process/width 4 | 677 CPU-s, 7:44 wall |

One-process-per-namespace was fast without coverage but paid static instrumentation of the roughly 240 MB output tree per process with coverage. CI run 32609813981 remained green but took 25.5 minutes versus the 22.5 minutes it replaced and contributed to a sibling timeout. This led to grouped alternation filters (`TEST_GROUPS=$(nproc)`) rather than one process per namespace.

MTP resolves coverage output relative to each results directory. Concurrent projects sharing one directory overwrite reports. `--report-trx` also copied `coverage.cobertura.xml` into the TRX attachment tree; recursive discovery double-counted identical files, so CI uses bounded-depth searches before `merge-cobertura.py` deduplicates source lines.

### HEAVY re-weight convergence (2026-09-14)

Three green `backend-tests` runs, each 16 TRX files and 12,128 tests. A: 34861036286 (develop @3c44cd001). B: 34877183235 (develop @3d21d9e28). C: 34882013960 (PR #60, i.e. @3d21d9e28 plus this change's script and doc edits only). The test code is near-identical across all three: the only change between A and B was a four-line cancellation fix in `FakeWhisperTranscriber.TranscribeAsync` (`XE-Local-AI-Engine.Tests/Transcription/TranscriptionTestDoubles.cs`), which cannot move solo namespaces such as `DevWorkflows.Materialization`, and C adds no test code at all. Leg walls: A 20:17 / 24:49 / 22:23 / 17:36; B 16:26 / 19:30 / 20:43 / 18:20; C 12:50 / 20:50 / 17:34 / 21:59. Total test-seconds 11,462 (A), 9,980 (B), 9,631 (C). The pack simulator reproduced CI's own `groupN:` lines on A and B, 16 bins identical, so a table can be scored against a run that did not build it.

| Table | Shard max/min on A | on B | on C |
|---|---:|---:|---:|
| from run A alone (the then-current file) | 1.05 * | 1.43 | 1.14 |
| from run B alone | 1.36 | 1.11 * | 1.21 |
| per-run mean of A and B (`--heavy --runs 2`, committed) | 1.14 * | 1.12 * | **1.53** |

`*` marks a score on a run that helped build the table. Each single-run table balances its own run and no other, and the mean does not generalise either: on C, the run held out from all three tables, it scores worse than both single-run tables. Nothing here is a balance fix, because the realised balance is set by how fast each leg's runner is, not by the pack. Under the committed table every shard carries about the same weight-space load, yet C's true per-shard seconds were 1,886 / 2,890 / 2,033 / 2,821, with every bin on shards 1 and 3 slower than every bin on shards 0 and 2 across unrelated namespaces (shard 0 bins 575 / 439 / 517 / 355 s against shard 1's 819 / 696 / 667 / 708 s). Relative leg speed was 0.77 / 1.17 / 0.83 / 1.15 in C and 0.71 / 1.03 / 0.99 / 0.74 in B: about +/-20%, and it moves whole legs. Per-namespace noise rides on top — three-run max/min 1.64x for `GraphWorkflows`, 1.55x for `DevWorkflows.Materialization`, 2.02x for `Endpoints.GraphWorkflows.V1`, 2.71x for `Endpoints.Drafting` — and a namespace holding a bin alone has no neighbours to average it away.

`JOBS=4` runs a shard's four bins concurrently, so a shard's wall is at least its heaviest bin, and the four heaviest namespaces each hold a bin alone. Run-B floors were 905 s (`GraphWorkflows`), 792 s (`DevWorkflows`), 709 s (`Dispatch`) and 649 s (`Materialization`), while shard load divided by four was 522-748 s: every leg was floor-bound, not pack-bound. Setup overhead on top of the floor varied between 4.5 and 7.5 minutes. No table can pull the slowest leg below its solo namespace's own time; only splitting that namespace can.

### Build/test contamination incident

A build running beside a `--no-build` test rewrote assemblies while MTP was loading them, producing both phantom failures and phantom green results. The first lock implementation used a conventional `flock <file> <command>` form; MSBuild daemons inherited the open descriptor and kept the lock after the command returned. The current helper marks the descriptor close-on-exec and exit 69 identifies a live holder. Assembly snapshots turn concurrent mutation into exit 75 rather than test evidence.

### Test-host memory and temp artifacts

The original full Tests module grew to roughly 3.5 GB. gcroot analysis identified long-lived host graphs from entry-point resolution and recurring rate-limiter/MCP allocations, not a normal fixture-reference leak. `WebApplicationFactory<Program>` was replaced with `TestServerWebAppFactory`; safe suites share a per-class host.

Before fixture cleanup, each host left SQLite, node-data, and web-root artifacts. Tens of thousands accumulated to roughly 15 GB and filled the 16 GB `/tmp` tmpfs. A killed test process still leaks because disposal never runs.

The migrated SQLite template measured approximately:

- empty database migration: 1,181 ms per host;
- template copy then normal startup: 712 ms per host;
- `JOBS=10` full module with template: 128–164 seconds;
- same shape without: 197–208 seconds.

Two assembly MVIDs in the filename prevent reuse after migrations or identity seed code changes. Publication is an atomic same-filesystem rename.

### Transcription framework-temp flake (2026-09-13/14)

`TranscriptionUploadStreamingTests.BufferedControlEndpoint_WhileRequestActive_DoesSpillToFrameworkTemp` failed 2 of 4 full local runs of the module through `scripts/run-tests-memory-safe.sh` at `JOBS=10 PAR=1`, costing 33 seconds in each failing run — the class's own 30-second budget expiring plus host overhead. Two causes were addressed in one commit: the fixed `ASPNETCORE_TEMP` directory was made per-process, and the budget moved to `TestBudgets.Contended`. A negative control on 2026-09-14 (the shared path restored, the two spill-producing tests looped against each other in two processes for 14 rounds, plus a full-namespace pass with four concurrent `LocalChat` sessions) stayed green throughout, so the cross-process hazard is established by inspection rather than by reproduction and CPU starvation under `JOBS=10` remains the surviving suspect for the observed failures. Trace and hypotheses: `Plans/test-perf-2026-09-13/research/08-transcription-flake-trace.md`.

### Timing-test and build-daemon incidents

In the rc.4.2 manual packaging session, three of five attempts failed because lingering build daemons starved timing-sensitive tests; no corresponding product defect was found. Failure duration aligned with the timing budget. `dotnet build-server shutdown` restored the expected behavior.

### Browser and frontend timing measurements

A 69-test browser run showed no overlap between serial and pooled groups, but pooled ran before serial despite order values suggesting the reverse. Pooled: 27 tests, peak four concurrent, 95.1 seconds of test time in a 46.4-second span. Serial: 40 tests, peak one, 142.4-second span. The evidence proves disjoint phases, not direction.

In `ChatInputArea.sampling.test.tsx`, the first dynamic import took about 1,754 ms versus 91/139 ms after the component graph was warm. Under coverage across 209 files, import work exceeded Vitest's old five-second default intermittently. The 20-second timeout is for cold transform/evaluation, not permission for slow behavior.

### OpenAPI incidents

Two silent failure modes shipped:

1. `openapi:check` regenerated from the already checked-in spec, so a new endpoint was absent from both input and output and the gate passed.
2. Regeneration under a non-desktop host omitted every `IDesktopOnlyEndpoint`, causing generated exports to disappear and downstream `TS2305` failures.

A mise-managed environment created a third false failure: the live-check script isolates HOME/XDG, losing mise trust and tool installs. Pinning `MISE_TRUSTED_CONFIG_PATHS` and `MISE_DATA_DIR` preserves the backend launch.

### Packaging and repository consolidation

Manual tester releases through `0.1.0-rc.5.0` were published to a separate tester repository. Earlier releases use bare tags with v-prefixed names; later scripts used v-prefixed tags. The current `release.yml` publishes both RIDs to `w0rldx/XE-Local-AI-Engine.Source` with `GITHUB_TOKEN`; old manual scripts remain reference-only and deliberately preserve their historical repository/auth flow.

GitHub workflow state changed between checks. On 2026-07-24 workflows were reported disabled/unregistered; on 2026-08-17 `gh workflow list --all` reported build-and-test, E2E, release, and Dependabot active. Neither observation is current-state evidence.

### Always finish with a Release build — a green Debug build is not verification

The four Release-only rules (`S3267`, `MA0022`/`S4586`, `S1117`) were each paid for once in the External Apps slices
S0–S3: a branch that built clean in Debug all day failed at the Release gate. Deleting `AgentHomeToolGateway.BuildHeader`'s
only call site as a deliberate break tripped the unused-private-member analyzer, the Release build reported errors, and
the test run that followed executed the previous binary; it cost a full lock round. Moving the call instead kept the
analyzers quiet and also proved more: that the header sits at index 0 rather than merely being present.

### `dotnet_naming_rule.*.severity` is IDE-only — the build-time lever is the single `IDE1006` diagnostic

The S0 config-hygiene slice measured `IDE1006` at 225 violations on 2026-09-16 and did not promote it. 221 of the 225
were one style disagreement: local constants written PascalCase against the declared camelCase style. Source: Microsoft
Learn naming-rules page, "Severity specification within a naming rule is only respected inside development IDEs … not
respected during build".

### Measuring an analyzer rule: drop warnings-as-errors for that build, and flip the rule under `[*.cs]`

The first `IDE1006` measurement appended the flip to the end of `.editorconfig`, where the last section is an
endpoint-DTO glob, and reported zero violations; it would have promoted the rule on false evidence. Re-run under
`[*.cs]`, the same flip reported 225 (S0 config-hygiene slice, 2026-09-16).

### Commit before you trap-guard a mutation on a file you are still editing

The S1 regression-guards lane hardening the `LayerDependencyTests` floors installed its break-proof trap on the very
file it was writing and lost a cycle re-applying its edits (2026-09-16). The same loss was paid again in S6a.

### Measure a non-vacuity floor through the guard itself, never with a grep over source

Three floors written from a grep proxy sat above the real count and turned the guard red on a clean tree. Measured on
2026-09-16, the NetArchTest assembly counts for `Providers.LlamaServer`, `Providers.HuggingFace` and
`Providers.WhisperCpp` came in roughly an eighth below their grep figures (S1 regression-guards slice).

### A reflection walk over an endpoint's dependencies sees `ILogger<TSelf>` as a self-reference

On the first empty-allowlist run of `EndpointDependencyTests`, nine endpoints landed on the allowlist for the sole
offence of owning a logger, which would have frozen a false rule into the list S6 went on to empty (S1
regression-guards slice, 2026-09-16).

### a bulk `.editorconfig` severity silences an analyzer without stopping it running

The S7 analyzer-upgrade slice measured `XE-Local-AI-Engine.Client.Persistence` three ways on Sonar 10.34: as shipped,
with per-ID `none` lines added under the existing Migrations glob, and with `Migrations/**` removed from `<Compile>`.
The reporting half was proved directly: the same `FromSqlRaw` string-concatenation violation is an `S2077` build error
in ordinary Persistence source and compiles clean inside `Migrations/`. Per rule (each unset in `.editorconfig` except
`S2068`), excluding the migrations from compilation collapsed the rule's cost to near nothing and a per-ID `none` under
the Migrations glob reached almost the same floor, so the cost is the generated migrations: `S5344`, `S2077`, `S4790`,
`S2068`, `S4036`, `S5542`, `S7039`, `S2971`, `S5122`, `S3011`. Decompiled from the shipped analyzer: every Sonar rule
derives from `SonarDiagnosticAnalyzer`, which calls `ConfigureGeneratedCodeAnalysis` with the `Analyze` flag, so
Roslyn's generated-code filter never applies; each of the ten registers only node actions inside a compilation-start
action, so none is exempt from the per-tree skip (which is skipped for a rule that registers a symbol-start or
compilation-end action). Per-rule figures are in the slice's progress report. The original entry also named a committed
`spike/analyzer-*.txt` baseline; no such file exists in the repository (checked 2026-09-27), so the rule now says to
measure before and after instead.

### an analyzer error in a file the change never touched: re-run after a build-server shutdown before believing it

Observed once, in the static-quality S6i break-proof (2026-09-17): a Release build (`--no-incremental`, under the build
lock, `MSBUILDDISABLENODEREUSE=1` and `NUGET_PACKAGES` exported, in the same shell chain as an earlier build with no
`dotnet build-server shutdown` between them) reported `error S125: Remove this commented out code` at a prose comment in
`NodeChatStreamService.cs`, a file the slice never touched, and `1 Error(s)`. The identical tree then built
`0 Error(s)` twice after a shutdown. The cause was not isolated: a stale build server is the suspect because the
shutdown is what changed, but the red was not reproduced, no second variable was held, and the failing log was
overwritten before it was kept. It may equally have been analyzer nondeterminism. One round was discarded.
Source: `Plans/static-quality-enforcement-2026-09-15/progress/S6i-report.md` §8 and §10.

### A Dev-mode sandbox run leaves MSBuild worker nodes holding a dead `NUGET_PACKAGES`

The dead per-task path surfaced as NU5037 during the graph-workflows S0 merge and as CS0006 in the session after it,
each build naming a `/tmp/xe-…/nuget` directory nothing had asked for. The writer was
`DevelopmentWorkspaceTools.BuildEnvironment`, fixed by setting `MSBUILDDISABLENODEREUSE=1` alongside the per-task
`NUGET_PACKAGES`.

### a constructor-caller census by `grep "new X("` misses target-typed `new(...)` and NUL-bearing files

The static-quality S3 migration hit both blind spots: a "no callers outside the plan" census was disproved by the
first Release build (`SourceBuildRecoveryTests`, `DevelopmentWorkspaceAndCoderTests`, `LlamaGrammarToolOffer`,
`EmbeddingToolRelevanceSelectorTests`). Two test files carry a literal NUL byte inside a string literal, which plain GNU
grep classifies as binary. Source: `Plans/static-quality-enforcement-2026-09-15/progress/S3-report.md`.

### `.Result` false-positives are why MA0042/MA0045 replace a `BannedSymbols` line

A "23 known sites" sync-over-async census was mistaken for the population; the Release measurement found 26 MA0042 and
150 MA0045 sites. A text ban would also red dozens of DTO properties named `Result` and four zero-timeout `Wait(0)`
admission polls. Source: `Plans/static-quality-enforcement-2026-09-15/S4-blocking-and-cancellation-rules-plan.md`
§3a/§4 and `progress/S4-report.md`.

### the `Architecture` guards are a NAMESPACE, so a class-name treenode filter never reaches them

In the 2026-09-23 transcription-open-sessions BE-1 and BE-2 runs, a scoped run reported as "including the
Architecture namespace" used a class-name pattern and never executed the shrink-only comment-budget guard; only the
namespace filter ran the guards. Source: `Plans/transcription-open-sessions-2026-09-23/progress/`.

### Verify against the whole module, not just the class you touched

A singleton factory once called `INodeSettingsStore.Load().X` without accepting the test substitute's null result.
Every host-based test failed at startup while narrow changed-class runs stayed green across four merges.

### a batched-module red keeps only 3 grep'd lines, so capture the failure detail on the FIRST red

Paid for in static-quality S6b (2026-09-16): a one-test `System.Net.Sockets` red in `XE_Local_AI_Engine.Tests.Hosting`
kept its stack frame but not the `SocketError` value or the port, because the message line was line 4 of a `head -3`.
A second gate run recovers nothing. Source: `Plans/static-quality-enforcement-2026-09-15/progress/S6b-report.md` §6a.

### Re-measure a TRX set with the script, never quote a class or namespace count from a document

A comment in `XE-Local-AI-Engine.Tests/Diagnostics/TestServerWebAppFactoryTimingTests.cs` carried a "61 test classes
pay per host per test, 42 pay per class" split from 2026-08-23 that was already wrong by 2026-09-13. A stale `HEAVY`
weight is what pushed a CI shard past its job timeout. Both are the same failure: a measurement frozen into prose.

### The full Tests module is flaky under parallelism — verify suspects in isolation

Only `LlamaCppSourceBuildServiceTests` mutates the parent `XE_NODE_SQLITE_KEY` environment variable, and it is
exclusive; other matches seed a host configuration dictionary, set a child `ProcessStartInfo.Environment`, or mention
the name in comments. The unguarded `XE.Node` listeners at the 2026-09-14 audit were
`Knowledge/KnowledgeIngestionDispatcherTests`, `Mcp/McpToolCallTimeoutAIFunctionTests` and
`Capacity/GpuModelLoadAdmissionTests`. The two name-filtering ones filter at `InstrumentPublished`
(`instrument.Name == "mcp_tool_timeout_total"` / `== instrumentName`), so a foreign instrument is never enabled.
`KnowledgeIngestionDispatcherTests.EnqueueAsync_PublishesAcceptRejectCountersAndDepthGauge` enables every `XE.Node`
instrument, discards the rest in a `switch (instrument.Name)` default, asserts counters as lower bounds and gates its
gauge on `when measurement == Capacity`; its `listener.RecordObservableInstruments()` also fires other tests' live
`McpAgentRunMetrics` observable gauges, harmless only because of that switch. `McpAgentRunCoordinatorTests` and
`McpAgentRunCompactionServiceTests` each build a real `McpAgentRunMetrics` and carried a bare `[NotInParallel]` for it
until the 2026-09-14 audit dropped both; between them they had serialized the whole module for 13 fake-only tests.
Their `mcp_agent_run_*` instruments collide with no name any listener matches.

### A wall-clock budget sized on an idle box is a CI flake waiting to happen — use `TestBudgets.Contended`

On 2026-08-25, two new migrations plus five new migration tests were enough to tip
`NodeChatMigrationRecoveryServiceTests` over under full-module load, with a different test of the class failing each
run: the attempt was cancelled mid-apply, leaving a half-rebuilt `ef_temp_*` table, and the retry died on
`table "ef_temp_<name>" already exists`. Raising the budget would only have bought the same increase in dead wall
clock, because its abandoned-lock test's first attempt is meant to exhaust the budget.

### `FastEndpoints.Config.SerOpts` is process-global, so a bare-context test sees whoever booted a host first

`Program.CreateAppAsync` seeds `Config.SerOpts` with the camelCase DI options when the first host boots
(`TestServerWebAppFactory.EnsureApp`, lazily); a process that has not booted one serializes `"GeneralErrors"`. Under
`--maximum-parallel-tests 1` the bare-context test deterministically lost the race; at default parallelism it
deterministically won. FastEndpoints 8.3.0 `ProblemDetails` behaviour was proven standalone; ten of the twenty
`ApiFoundation/` classes boot a host.

### A loopback port you bound on `:0` and released is a candidate, not a reservation

The 2026-09-05 `scripts/run-tests-memory-safe.sh` flake in
`EngineCliProcessTests.McpOnlyPrimaryServe_EmitsCanonicalReadinessSupportsStatusAndEnforcesPortAndLeaseExits`: a batch
spawning processes concurrently took the released candidate before the engine child bound it; the test passed on every
isolated re-run. `HostBootSmokeE2ETests.Host_Boots_On_Real_Port_And_Health_Live_Returns_200` had the same
reserve-then-release shape. The Kestrel exception chain was confirmed by binding a held listener and starting a
`WebApplication` on the same port under .NET 10. In-process port-0 users that keep their listener for the test's
duration (`DeferredLlamaServerEmbeddingGeneratorFailureTests`, `CodexAuthServiceTests`,
`EntraAuthCodeSignInCoordinatorTests`, `LlamaGrammarLiveSmokeTests`, `XEReactClientFixture`) are not affected.
Still carrying the shape (recorded 2026-09-06, still present 2026-09-27): `DesktopPortStoreTests` reserves and releases
in four tests. `ResolveBindUrl_WhenPersistedPortIsTaken_FallsBackToDynamicBind` and
`IsPortAvailable_ReportsHeldAndReleasedPorts` re-bind the released number and want the held-listener form;
`ResolveBindUrl_WhenPersistedPortIsFree_RebindsThatPort` and `PersistThenResolve_RoundTripsToTheSameLoopbackUrl` need
the number free when `DesktopPortStore` probes it and want the retry form. Not converted because they are synchronous.

### The full Tests module balloons to ~3.5 GB — it is a framework leak, not a fixture bug

`TestServerWebAppFactory` closed four process-lifetime roots: a per-host MEAI function-descriptor cache key,
rate-limiter timers and closures in Testing (auth limit raised 10 to 10,000/min so a test host neither roots the
disposed graph nor throttles the single loopback partition), EF service-provider caching for per-host connection
strings, and SQLite pool groups. EF 10's cache key includes the root application service provider, so a cached test
host left an immortal ~20 MB entry, and the old `EnableServiceProviderCaching(false)` escape rebuilt EF's provider on
every DbContext scope. With the internal provider, EF refuses a singleton interceptor passed through the options and
refuses per-context differences in provider-constant options such as `ConfigureWarnings`. Measured 2026-09-25 on a
loaded machine with `/usr/bin/time` peak RSS: `Endpoints.Benchmarks.V1` at width 1 peaked at 3.3 GB cached, 674 MB
with `NodeEfInternalServices`; `DevWorkflows.Materialization` at width 1 took 463 s uncached per scope, 55 s now.
A low `DOTNET_GCHeapHardLimit` is not a valid leak verdict: size-cap tests allocate large payloads.

### A node-settings save path must carry the local-only members over from the stored record

Live evidence, fu-b round 2a (2026-09-05): a dropped `MachineKey` made the next start mint a fresh GUID; because
inference profiles are keyed by machine key, every frozen profile was orphaned, each model re-fit from scratch, the rows
still read `Frozen` through the profiles endpoint, and nothing was logged. Code shape at the fix:
`ApplyAgenticPatchAsync` was already safe against plain erasure because it starts from `current with {…}`;
`MachineKeyProvider` mints as `latest.MachineKey is empty ? latest with { MachineKey = fresh } : latest`, caching the
key the store actually holds; `SaveTrustedMergedAsync` also carries the key over up front because that copy owns the
record the call validates and returns, including on rejection paths that never write. Guards:
`SaveTrustedMerged_WhenTheIncomingRecordHasNoMachineKey_PreservesTheStoredOne`,
`…_WhenTheMachineKeyIsMintedBetweenTheLoadAndTheWrite_PersistsTheMintedOne`,
`SaveNodeSettings_WhenValid_PreservesTheStoredMachineKey`,
`MachineKey_WhenTwoProvidersMintConcurrently_AgreeOnTheOneStoredKey`.

### The one temp artifact that is meant to survive: the migrated SQLite template

Before the 2026-09-24 disk-hygiene pass the templates lived under `/tmp`, where every rebuild orphaned the whole set and
nothing could sweep it safely: 943 files, 790 MB of tmpfs across 20 builds. The template is a WAL database (EF Core's
`SqliteDatabaseCreator.Create` enables WAL), so the builder runs `PRAGMA wal_checkpoint(TRUNCATE)`, releases the pool,
closes the last connection, and refuses to publish while a `-wal`/`-shm` sidecar remains, because copying the main file
while a log holds committed rows loses them.

### Deleting a dead service can delete a live control's only tests — grep the test file first

In the 2026-09-20 AgentHome memory-proposal removal, `AgentHomeMemoryProposalServiceTests` was the sole coverage of
`MemoryProposalSecretScanner`, a security control with two unrelated live callers (`MemoryExtractionService`,
`DevelopmentArtifactSanitizer`). Deleting the file as specified would have removed that coverage with no gate saying so.

### TUnit.Playwright caches ONE browser per worker, so per-class launch arguments are silently ignored

Upstream `TUnit.Playwright` 1.65.68 `BrowserTest.BrowserSetup` registers the browser via
`WorkerAwareTest.RegisterService("Browser", …)`. Measured over three serial runs of the two fake-audio transcription
suites: whichever class ran second was fed the other's audio. The speech fixture correlated 0.951 when it ran first and
0 when second; the 440 Hz control 0.76 when first and 0.99 when second, so the negative control was measuring the
positive case's WAV.

### size a gate from free RAM and check the lock's status before waiting on it

2026-09-23: a `low-memory` gate held the shared cross-worktree build lock for over 65 minutes with 23 GB free, while two
other sessions' five-second builds each waited out the 1800 s default timeout and got exit 69. Neither waiter could see
who held the lock or for how long. This led to RAM-based `JOBS` sizing (`scripts/lib/test-sizing.sh`) and
`scripts/build-lock-status.sh`; the operative rule is in `AGENTS.md` ("Validation").

### No analyzer in this stack detects an assertion-less TUnit test

sonar-dotnet's `IMethodSymbol.IsTestMethod` recognises only the MSTest, NUnit and xUnit attribute lists
(`SonarAnalyzer.Core/Semantics/KnownType.cs`, `.../Extensions/IMethodSymbolExtensions.cs`); `TUnit.Core.TestAttribute`
is in none of them, so S2699 cannot fire at any severity. A live pilot on 2026-09-05 (assertion-less `[Test]` in
Persistence.Tests, `S2699` at warning, Release) reported nothing while a bare-TODO control on the same file fired S1135.
TUnit ships no such analyzer (`src/TUnit.Analyzers/DiagnosticIds.cs`). Two assertion-less tests
(`ModelRecommendationScheduleSeederTests`, `ModelCoordinationTests`) lived in Persistence.Tests until 2026-09-05.

### A silent `return` on the wrong OS reports a green pass, not a skip

The 2026-09-05 test-principles audit converted 137 guards across 21 files (107 `[RunOn(OS.Linux)]`, 28
`[ExcludeOn(OS.Windows)]`, one each of the inverses), concentrated in `ProcessSandboxRuntimeProviderTests`,
`LlamaCppSourceBuildServiceTests`, `TrainingRuntimeServiceTests`, `SourceBuildRecoveryTests` and
`CoderWorkspaceReaderTests`.

### Sleep-then-assert-the-negative can only fail when the code gets slower

The 2026-09-05 pass found 89 finite `Task.Delay`/`Thread.Sleep` sites in `XE-Local-AI-Engine.Tests`, removed 46
(converted through `AssertEx.SettleAsync` / `StaysIncompleteAsync` / `CompletesAsync`, folded into a bounded poll, or
deleted with the shape they served) and kept 43, each a bounded poll with a deadline, a positive wait with a real
budget, or a `// real-timer:` site.

### Never infer this node's foreign-key posture from a connection string — measure `PRAGMA foreign_keys`

The wrong belief (foreign keys off) scoped a whole audit slice on "the fixtures hide missing child deletes" and two
store bugs that did not exist. `NodeSqlitePragmasTests` has one test per layer, one through the production path, and a
pair pinning the split: a failing `foreign_keys` pragma propagates on both the sync and async paths while a failing
tuning pragma logs a warning and lets the rest apply. The per-layer tests exist because a native default masks a
deleted setting: the string test parses and never opens a connection, and the applier test opens both its connections
with `Foreign Keys=False` so only the pragma can flip them. SQLite runs the AFTER DELETE row trigger for a row an
`ON DELETE CASCADE` removed (measured on the migrated schema, pinned by a test), so `knowledge_document_chunks_ad` keeps
`chunk_fts` aligned on the cascade path.

### The GPU smoke is the only gate that proves the GPU did the work — and its exit codes are a taxonomy, not a scale

Installed runtime metadata and effective execution disagree in both directions: a Vulkan record without an ICD can run on
CPU, while a CUDA path override can run on GPU under a stale Vulkan record. `IRuntimeDeviceAudit` combines the selected
binary, the hardware and `--list-devices`; the smoke confirms the outcome during generation. A failed or timed-out audit is
unknown and must not persist as determinate state.

### The packer's weights are load-bearing, and it cannot split a namespace

`DevWorkflows` was listed at a local 196 s while CI spent 1651 s on it; LPT therefore gave it a bin to itself, that bin
landed on shard 0, and `backend-tests (tests-0)` hit the 45-minute job timeout (run 34730991540) while the other three
shards idled. The table was also missing `GraphWorkflows` (503 s) and `Integrations` (366 s) entirely, and an unlisted
namespace weighs 1, so the pack was balancing on numbers that described a different machine. The fix split `DevWorkflows`
into `DevWorkflows[.Execution|.Materialization|.Dispatch]`. Table sources named in the `HEAVY` header comment: 2026-09-14,
the per-run mean of CI runs 34861036286 and 34877183235 via `scripts/test-durations.py --heavy --runs 2` (coverage on,
JOBS=4, width 1, TEST_GROUPS=16 over four shards); 2026-09-25, CI run 36079796542, the first green run after the per-host
EF provider change, one run until the next green run is folded in with `--runs 2`.

### A HEAVY weight from one CI run is noise, and no table can balance the legs better than runner speed allows

The single-run table from 34861036286 predicted a 1.05 shard max/min in-sample and realised 1.43 on the next green run
(34877183235, whose only test-code delta was a four-line cancellation fix in one Transcription test double), where
`DevWorkflows.Materialization` swung 1008 s -> 649 s. The two-run mean scored 1.14 and 1.12 on the runs that built it but
1.53 on the first held-out one (34882013960), because weight-balanced shards ran at 0.77-1.17x relative speed inside that
single run: per-leg runner speed dominates the table.

### Packaging (Velopack)

`cliff.toml`'s `tag_pattern` has been a regex since git-cliff 1.4.0, matched with `regex::Regex::is_match`, so the
long-standing `"v?[0-9]*"` matched every refname in the repo, the empty string included. `dev/<version>` snapshots and
`codex/...` branch tags therefore counted as release boundaries, and official `--unreleased`/`--latest` notes would have
started at the newest Development build. The separate HEAD probe failure mode: a `dev/` tag on a release commit made
`git describe --exact-match --tags HEAD` succeed, so the script emitted `--latest` against the Development tag's range.
Authority at the time: git-cliff `[git]` docs for the pinned 2.13.1; fixed 2026-09-22/23. The shared packaging workflow
(one 17-step job) was extracted 2026-09-22; `test_vpk_version_pin_is_consistent_across_every_release_surface` ties the
`VPK_VERSION` in both workflows, the `vpk-version` input default and `Directory.Packages.props`'s `Velopack` pin to one
string.

### A Development build's version extends the anchor with DOTS ONLY — never a second hyphen

Verified 2026-09-22 against Velopack 1.2.0 `SemanticVersion.TryParse` / `CompareTo` (commit `f2edcbca`) plus an executed
comparison: the prerelease is split at the FIRST hyphen and any non-numeric label ranks above any numeric one.
`compose_dev_version` asserts its own output carries exactly one hyphen.

### Development releases are capped at 30 because the stock GitHub source reads only the 10 newest releases

Velopack 1.2.0 `Sources/GithubSource.cs` `GetReleases` hard-codes `per_page=10, page=1`; `Sources/GitBase.cs`
`GetReleaseFeed` applies the prerelease filter after that truncation, so 11+ Development releases evict every stable and
RC release and `CheckForUpdatesAsync` returns `null` to Stable and Preview users indefinitely (2026-09-22).

### Deleting a superseded Development release is safe; deleting its tag is not

Velopack 1.2.0's delta strategy in `UpdateManager.DownloadUpdatesAsync` falls back to a full package when the delta chain
has a gap, which is why deleting releases is harmless while deleting a tag breaks the previous-Development lookup
(2026-09-22).

### Only the packaged main executable calls `VelopackApp.Build().Run()` — a second process of the same install must not

From a Velopack 1.2.0 `VelopackApp.Run` decompile (review 2026-09-22): with no `--veloapp-*` hook arguments `Run()` is a
near no-op only in an unpackaged dev run (`CurrentlyInstalledVersion` null). Inside an installed app, when a newer staged
full package exists and auto-apply is on (the default), it spawns `Update.exe apply --waitPid <this pid>` and calls
`Environment.Exit(0)`; from a secondary process the updater then relaunches the main executable carrying the secondary's
arguments. The once-only guard is a per-process static that only logs, and the Windows locator keys on the process path,
so any executable inside the install tree looks like the main one. The failure reviewed against was a `Run()` added to
the Desktop shell racing the launcher over one Windows install.

### Review tooling that auto-detects the default branch fails here — pass the base explicitly

Operator ruling 2026-09-05 on explicit review bases.

### a path-scoped `git commit -- <paths>` silently skips an UNTRACKED file among those paths

Tester round 3 (2026-09-25) paid two fix-up commits for workstream commits that lacked a new test or source file
(`Plans/tester-round3-2026-09-25/progress/REPORT.md`, a local, git-ignored plan folder).

## 2. Local runtime evidence

### WSL2 hardware and VRAM readers

A live probe (2026-07-26) found a different GPU generation than this entry had recorded, and a different CUDA toolkit version than assumed. Notes about the dev hardware go stale silently — probe (`nvidia-smi`, `nvcc --version`, compiled arch) rather than reading a recorded inventory.

Under WDDM pressure, a model with about 1.2 GB truly free still loaded and served rather than OOMing: 161.7 tok/s versus 698.4 tok/s unloaded, a 4.3× slowdown without an error. In another run, `nvidia-smi` reported 492 MiB free while llama.cpp's process-local `cudaMemGetInfo` view reported 29,697 MiB. `nvidia-smi --query-compute-apps` remained empty while processes held large allocations.

### Aspire teardown retest

On 2026-08-19, five cycles included a real `llama-server`, Docker `sqlite-web`, Aspire hosting packages 13.4.6/13.5.0, CLI 13.4.6/13.5.0, and one SIGKILL of the CLI. Plain `aspire stop` removed the observed process/container graph and VRAM returned to baseline every time. This disproved the claim that the fix began only in 13.5+, but did not identify the original orphan trigger. Invoking `dev-stop.sh` directly on a live stack still issued 15 SIGTERMs, so the fallback remains useful.

### Rootless container identity

Measured on a rootless Docker Engine daemon with a non-default subuid range:

- container `1000:1000` mapped to a host uid near the top of that range (base + 999) and could not write the engine-owned bind mount;
- container `0:0` mapped to the invoking host user and wrote files owned by that user;
- `inspect` reported the requested identity in both cases and could not reveal the mapping.

The create-time write/stat probe verifies the outcome that configuration read-back cannot.

### `/tmp` tmpfs

With a read-only root filesystem, every `dotnet` command failed EROFS before project work because the CoreCLR named-mutex path is compiled under `/tmp/.dotnet/shm`. Redirecting `TMPDIR`, `TMP`, or `TEMP` did not change it. Mounting `/tmp/.dotnet` alone also failed because the PAL creates a sibling temp directory and renames it. A 1 GiB tmpfs under a 256 MiB cgroup OOM-killed the container near 254 MiB. Real use was about 4 KiB; the shipped limit is 64 MiB with `noexec,nosuid,nodev`.

### Host-git execution from repository configuration

On a recent Git release, repository `core.fsmonitor` executed during index refresh, and an in-tree `.gitattributes` selected a `filter.*.clean` command during `git add`. Command-line `-c core.fsmonitor=` closes the finite key; arbitrary filter driver names cannot be pre-pinned. This is why `.git/config` is mounted read-only in the container and rewritten before host-side patch-evidence git calls.

### Tool grammar ceiling

Against llama-server b10201 and a non-reasoning Qwen2.5 model:

| Keyword | Observed boundary |
|---|---|
| `maxLength` | 2,000 failed; 1,990 passed in the isolated probe |
| `minLength` / `minItems` / `maxItems` | 8,000 failed |
| regex `{0,8000}` | failed; `{0,63}` passed |
| numeric min/max | 100,000 still passed |

The ceiling combines the whole tool catalog. A production offer still failed with every `maxLength` at 2,048 and compiled at 1,024. A reasoning Qwen3.6 request passed because it never entered the constrained grammar branch, proving why the live smoke requires a non-reasoning negative control.

### Work-session context incidents

A 27B model at a 65,536-token window overflowed at step 5 before forced step-bound compaction. A separate step made 14 tool calls (10 KB searches, each result already clipped near 16,041 characters) plus 1,094 reasoning chunks; repeated in-turn replay reached 71,172 tokens. These incidents motivated both step-boundary compaction and `MaxProviderCallsPerStep`; neither replaces the other.

The option initializer for `MaxToolResultCharacters` was 8,000 while an XML comment said 16,000. At 8,000 and chars/4, one clipped result estimates near 2,000 tokens. After 0.85 safety and a 12,000-token step context, about 21 results fit in a 65,536 window **before** reasoning replay, so 21 is an upper bound, not a recommended call cap.

### Windows live verification

The first native Windows run on 2026-08-03 found:

- persisted PATH contained 153 dead temp tool entries (~27 KB); `where ping` worked while `cmd /c ping` said not found. Three cancellation tests failed in 18–81 ms because their sleep command exited instantly. Cleanup reduced PATH from 28,387 to 847 characters and all three passed.
- one-line edits rewrote LF files to CRLF, producing 104/486 changed lines.
- pnpm had `.CMD`/PowerShell shims but no executable suitable for direct `CreateProcessW` under `UseShellExecute=false`.
- `-SkipUpload` failed immediately without `VPK_TOKEN` because the deprecated packager still downloaded the previous private release.
- repeat publish could leave `appsettings.AppUpdate.json` missing until the source timestamp changed despite `CopyToPublishDirectory=Always`.
- seven symlink tests skipped without privilege; junction-based tests exercised the directory reparse guards without elevation.

### BYO CUDA runtime without `llama-fit-params` (2026-09-05)

AI-trends follow-up pass B, B4 (D13 profile-authority live proof) round 1 against a BYO `~/cuda-llama/<tag>` build: six host
restarts and about sixteen minutes produced five NOT RUN observations before the missing sibling binary explained them —
every Explore answered 400. Round 2 against a scratch copy of the same bin directory with the freshly built
`llama-fit-params` passed 6/6 in about twelve minutes; the shared directory's 25-file md5 manifest was identical before and
after, and `installed-runtime.json` kept its original mtime. The blocked round's 400 is the one this incident is named for:
`LlamaFitParamsProcessRunner` resolves the helper as a sibling of the `llama-server` it launched, so a BYO build missing it
can freeze nothing. The rule lives in `docs/agent-knowledge/inference-runtime.md` ("llama.cpp binaries"); the supported scratch-copy
procedure is recorded under "llama.cpp binaries" in §2 of this ledger.

### Host filtering runs from an `IStartupFilter`, ahead of every middleware the composition root registers

The container bridge (C1, commit 3) shipped a listener no container could reach. Every loopback test passed:
`DefaultHttpContext` has no host filter, and an in-process `HttpClient` sends `Host: 127.0.0.1`, which is allow-listed.
Only a real container over a real socket (`ContainerBridgeRealDaemonTests`, after commits 1 and 2 were already green)
presented a Host header the filter rejected with 400, logged by nothing the node owns. Diagnosis tell: the request reaches
the port but nothing of yours logs it; capture the raw bytes (`nc -l` on the bound address) before suspecting the client,
the network or the container.

### An explicit Kestrel endpoint discards the addresses `UseUrls` was given

Found in C1 commit 1: the plan specified `ConfigureKestrel(o => o.Listen(...))` for the container bridge and had to be
deviated from, because `AddressBinder.CreateStrategy` picks the endpoints over the addresses whenever
`KestrelServerOptions.ListenOptions` is non-empty, logs "Overriding address(es)", and clears them.

### `FallbackPolicy` challenges every routed endpoint without auth metadata, and every request that matches no endpoint

S2 deny-by-default slice, 2026-09-16: two `Microsoft.AspNetCore.TestHost` spikes against FastEndpoints 8.3.0, then two gate
reds (`RouteCoexistenceTests`, `ValidateExecutableEndpointTests`) that had asserted the old 404 and 405. The silent 401s
hit Aspire's `WithHttpHealthCheck` poll, the login page the SPA fallback serves, and the dev-only OpenAPI document; none
was named by a test beforehand.

### Refresh rotation's grace window is discriminated by the SUCCESSOR LINK, never by "revoked recently" or by a timestamp

Sessions became independent chains by operator decision 2026-09-23 (live-QA F-01: a "one live token per user" model
signed every other client out on each login). Before that, the grace matched "a live token of this user created at the
revocation instant", which would let another client's login or rotation on the same tick vouch for a dead chain
(`Refresh_WhenTheOwnChainIsDeadButAnotherSessionIsLive_FailsInsideTheWindow`). Because the grace pair is a sibling of the
head, out-of-order `Set-Cookie` responses no longer lose the session; the price is one extra live token until expiry
(14 days) or logout per graced race, and a copy of the cookie captured inside the window can mint pairs until it closes
(bounded by the auth rate limit). Each such pair is an independent full-lifetime session, so cookie theft inside the
window is no longer revealed by the other client being signed out. Tests written 2026-09-17, rewritten 2026-09-23.

### `dev-stop.sh` resolves the AppHost of your CURRENT DIRECTORY, and `pgrep -af` matches its own shell

S7 live round, 2026-09-07: a drifted cwd made `dev-stop.sh` report no instance against a running host of another worktree.

### The node operator secret is seeded by dev-start.sh, not by any tracked file

`dev-start.sh` always supplies `.data/node.key`, so data written under an older user-secret key fails later protected
reads without naming the cause; `dev_ensure_node_operator_secret` warns when it mints a key beside existing data. Shared
model store detail: the `index.json` lock is an in-process semaphore and the manifest write goes through a fixed
`index.json.tmp`; concurrent downloads of the same file are safe (unique `.part` names, `File.Move` without overwrite) but
the loser fails with a destination conflict. The hand-authored `index.json` + symlink recipe is live-proven. A lighter path
read from code but NOT live-verified: `GgufModelRegistry.LoadEntriesAsync` rescans the directory whenever `index.json` is
missing/empty/corrupt and auto-registers any `.gguf` whose name `GgufQuantParser.TryParse` recognises a quant token in.

### A live round in a worktree starts from a FRESH isolated DB, and a scratch host never touches the user data dir

AI-trends live rounds 2026-09-03/04: each listed trap voided or wasted a round. A bare scratch `XDG_DATA_HOME` broke the
`dotnet` shim and Aspire exited 7 reporting "the `--apphost` option specified a project that does not exist". With mise
2026.8 the mirrored-symlink directory alone stopped being enough (External Apps follow-ups batch 3, 2026-09-12). Observed
`agent_execution_logs` envelope rows terminalizing about 45 minutes later. Before `75e0f7b60`, a fresh-DB start could delete
the operator's managed CUDA build through the shared runtime record; the product now never deletes a build the record does
not name (`LlamaCppSourceBuildService.ReconcileActiveAndBackupAsync`), so the remaining stake is isolation, not data loss.

### an `XDG_DATA_HOME` override for a scratch host also blinds a per-user tool-version manager

W1 follow-up round, 2026-09-19. The shim may also silently re-install an interpreter into the scratch directory.
`dev_matching_app_json` returns 4 on the empty output.

### two hosts on one box must never share a llama-server BINARY PATH, or one host's reaper kills the other's models

2026-09-25: the tester-round-3 live host lost its resident 7B mid-round when the live-QA lab host restarted ("Reaping stale
llama-server orphan (pid ...)" in the lab's log). The losing host logged nothing (the prune path was silent until a
follow-up added a Warning), the next run spawned the node default, and the model-reuse evidence was void.

### External Apps: container-written storage, the reserved variable prefix, and the evidence home

S5 live step 36 failed then passed across `e8c5e64eb`: an image running as its own non-root user (searxng's uid 977) left
`0700` directories owned by a host uid in the operator's subuid range; the helper needs in-container root with
`CAP_DAC_OVERRIDE`, so `cap_drop ALL` silently broke it and the delete exited 0 having removed one entry. S5 live step 30:
an `XE_`-named probe was refused and the catalog path swallowed the validation error into a cache fallback, caught only by
asserting off the engine. The `\A…\z` rule came from review of the S1 catalog validator (five regexes); the
`image@sha256:<64 hex>\n` case passed `^…$`. The raw-text architecture guard is why `ExternalAppStateObserver` is an
`IHostedService` with its own loop rather than a `BackgroundService`, whose entry point carries a banned name. S5's
evidence commit was never made because `Plans/` is ignored; `docs/roadmaps/external-apps-status.md` replaced it.

### llama.cpp binaries

2026-09-18: on a fresh node, the SPA shell's `GET model-fit/hardware-profile` (mounted on every authenticated page by
`CpuFallbackBanner`) fetched a multi-hundred-MB llama.cpp runtime within a second of the operator choosing Offline /
Manual; the three `ExternalAccessGate`-protected services were correctly gated and were not the source. Owner ruling
2026-09-19: acquisition belongs only to explicit paths (ensure endpoint, spawn, first-run provisioning); any new automatic
caller of `EnsureBinaryAsync` is the same defect. Prebuilt archives carry `llama-perplexity` but no `llama-quantize`; the
source-build path only gained `llama-perplexity` when its cmake `--target` list was widened, so an older or BYO tree has
`llama-server` only. Operator ruling 2026-09-05: the shared BYO build tree stays as-is, because `--target llama-fit-params`
drops `libllama-fit-params-impl.so` beside `llama-server`, and `RuntimeBundleIdentityCalculator.IsRuntimeBundleFile` hashes
the executable plus any `.dll`/`.dylib`/`.so` sibling, so every frozen profile on every checkout using it would go Stale.
Scratch-copy procedure: build the target in the shared tree, take an md5 manifest of `bin/`, MOVE `llama-fit-params` +
`libllama-fit-params-impl.so` into a `cp -a` scratch copy, verify the shared manifest is byte-identical, point
`XE_LLAMACPP_SERVER_PATH` at the copy for the round.

### Passing all five gates is still not enough — llama.cpp must be able to COMPILE the tool schemas

Live round 2026-09-07: a Graph Workflow Agent node's `responseJsonSchema` is enforced by a real compiled GBNF grammar from
generation start (a `zqx-alpha`/`zqx-beta` enum held against a prompt forbidding JSON and both tokens). The compiled `root`
rule leaves the `<think>` block optional and unconstrained, captured verbatim from a `--verbose` llama-server as
`"<|im_start|>assistant\n" space ("<think>" "\n"? until-13 "\n"? "</think>" "\n"? "\n"?)? space (...)`, and held at
`reasoningEffort` `none` and `high`. Before S7, the `Microsoft.Extensions.AI.OpenAI` 10.9.0 strict transform relocated the
value bounds: a `maxLength: 3` schema reached llama-server as `{"description":"maxLength: 3","type":"string"}` and produced a
1302-character field, while an `enum` arrived untouched. The relocated set (source order, `unsupportedProperties` in
`OpenAIClientExtensions.cs` at `dotnet/extensions` tag `v10.9.0`): `contentEncoding`, `contentMediaType`, `not`,
`minLength`, `maxLength`, `pattern`, `format`, `minimum`, `maximum`, `multipleOf`, `patternProperties`, `minItems`,
`maxItems`, `unevaluatedProperties`, `propertyNames`, `minProperties`, `maxProperties`, `unevaluatedItems`, `contains`,
`minContains`, `maxContains`, `uniqueItems`; `default` moves via `MoveDefaultKeywordToDescription`; `exclusiveMinimum` and
`exclusiveMaximum` survive. The pass also makes every declared property `required`, injects `additionalProperties: false`
only where an object has `properties` and omits the key, and walks only `properties`, `items`, `additionalProperties`,
`not`, `anyOf`, `oneOf`, `allOf` (so `$defs`, `definitions`, `prefixItems` and `$ref` targets are untouched). Do not dump
`Microsoft.Extensions.AI.Abstractions.dll` for the list: it carries an unrelated format vocabulary that looks alike. Since
S7, `ApplyResponseSchemaPassthrough` sets `$.response_format.json_schema.schema` before `ToOpenAIOptions`' `??=`, so the
transform never runs on the llama.cpp lane and `strict` is sent false; only a repetition bound above
`MaxGrammarRepetitionBound` (1024) is still removed. `InvocationAgentFactoryTests.CreateAsync_WithAResponseJsonSchema_ConstrainsTheTurnToIt`
asserts on the factory's `ChatOptions`, upstream of the adapter, so it cannot catch a rewrite. Serilog at Debug greps zero
hits for `grammar`, `json_schema`, `response_format` or `gbnf`; only llama-server's `--verbose` shows the compiled GBNF,
with no host restart needed.

### OllamaSharp wraps HTTP **400 only** into `OllamaException` — every other status is a bare `HttpRequestException`

Found by a second-pass review of the S7 unload change, via `ilspycmd` of `OllamaApiClient.EnsureSuccessStatusCodeAsync` in
the 5.4.30 package.

### Unloading a model asks BOTH local runtimes, because residency is not the provider map

Routing on the provider map sent an Ollama-resident, unmapped model to three no-op llama-server ejects and left it loaded.
`LoadedModelsPageE2ETests` caught it through the page's Ollama eject button; that UI path was removed on 2026-09-23 by
operator decision. `Unloaded` is false only when a llama-server role reported `TimedOutStillBusy`.

### The Ollama gate-off branch registers no-op `IModelCapabilityClient` and `IOllamaModelService`, so the node still boots

Before the fix `OllamaModelService` was registered unconditionally in `AddNodeWorkerInfrastructureExtensions` although it
needs an `IOllamaApiClient` that only exists on the gate-on side, so a gate-off Development host failed `ValidateOnBuild`
and other hosts threw at the first resolve of `LocalModelCatalogService`, `CapacityService`, `ModelClassificationService`,
`ModelFitQueryService`, `OllamaProviderMapBackfillCoordinator`, `GetRunningLocalModelsEndpoint`,
`GetLocalModelDetailsEndpoint` or `UnloadLocalModelEndpoint`. Nuance: `InstalledModelInventoryResult.OllamaQuerySucceeded`
is `true` under the no-op (an empty list is a successful query); nothing reads it for reachability. Latent and not fixed:
`OllamaProviderMapBackfillCoordinator.ListInstalledNamesAsync` is called once outside any `try` and once inside one whose
only non-cancellation catch is `InvalidOperationException or IOException`, so a real dead daemon's `HttpRequestException`
escapes the backfill; the gate-off path does not reach it.

### The post-turn maintenance queue shares the chat's single llama-server slot, so its cost lands on the NEXT turn

Live round 2026-09-25 (40 turns, 13 Distill jobs, 3 automatic compactions under a `numCtx=8192` per-send override):
about 5 s after a Distill (every `DistillEveryMessages` = 6 messages) and 20-30 s while a multi-pass compaction fold runs
on a 27B Q4 model on a high-end GPU; longer on weaker hardware.

### Measure free disk through `IFreeSpaceProbe`, never on `Path.GetPathRoot` of a path

Six hand-written free-disk copies existed before they were folded into the one probe; the root-based ones were invisible
on a single-volume dev box.

## 3. Model, inference, retrieval, and training evidence

### Large-model launch warmup timing

Historical measurements on the then-current large-model workloads put cold launch plus upstream warmup at roughly 45–110 seconds. That range is model-, hardware-, and version-specific, not a current universal timing or SLA. It showed that warmup could consume or exceed even a size-aware readiness window and trigger a kill/respawn loop; normal spawns therefore retain `--no-warmup`, while any synthetic warming belongs after readiness.

### NVFP4 live run

On 2026-07-31, `tngtech/Qwen3.6-27B-NVFP4-GGUF` (18.5 GB file) loaded to a 22.7 GB VRAM peak and generated near 95% GPU utilization on sm_120 using CUDA. The pinned llama.cpp was newer than the Blackwell NVFP4 kernel merge. A same-base/repo measurement used to estimate 4.25 bpw, but cross-repo sizes varied enough that actual blob size remains preferred.

### Silent CPU fallback

The managed Vulkan binary in the WSL environment had no Vulkan ICD and `--list-devices` returned an empty list; inference still answered on four CPU threads while the installed record and UI implied GPU. Conversely, a CUDA binary selected by `XE_LLAMACPP_SERVER_PATH` used the GPU while the installed record still said Vulkan. This validated `IRuntimeDeviceAudit` as the authority.

### Context and admission latch

A judge requiring a 16,384-token window was down-tiered during one tight-VRAM admission. The adjusted allocation was process-lifetime sticky, so later requests for the required window failed eight times, 25 seconds apart, even with no resident model, until restart. The immediate guard rejects instead of down-tiering a named requirement; reservation-scoped adjustments remain follow-up work.

### Benchmark evidence incidents

- A benchmark spawn carried fit capture and therefore missed the old verbosity branch; placement facts were absent until the benchmark-policy OR condition was added.
- Judge JSON written with Web/camelCase options was once read with default options. Deserialization did not throw; it produced a zeroed record and only successful judged runs crashed the frontend schema.
- `JsonPatch.Contains("$.timings.prompt_n")` returned false on a patch where `GetInt32` returned 123. Missing timing fields are now detected by `KeyNotFoundException`, with only the outer timings object checked by `Contains`.
- EF migration removal rolled the model snapshot behind an already-shipped sibling. Re-added migration emitted duplicate columns that failed only on database update.
- Interleaved branch migrations passed migrate-up tests, then SQLite table rebuild during rollback silently dropped sibling columns. Consolidating unshipped migrations at the tail restored a reversible chain.
- Hashing every installed model for eligible-model listing took 6 minutes 34 seconds on a large corpus; listing now trusts recorded facts and freeze verifies bytes.
- A per-run repeat insertion could lose CAS halfway, return no IDs, and leave already-created queued runs. Transactional `StartRunsAsync` makes the repeat group all-or-nothing.

### Knowledge retrieval evidence

Managed cosine outperformed sqlite-vec through the measured 100k-row corpus; sqlite-vec's default `vec0` remained brute-force. For pooled llama-server roles, ordinary ~2,000-character markdown chunks measured roughly 520–680 real tokens and failed the default 512 physical micro-batch even when `-c` was larger. Raising `-b/-ub` to effective context fixed ingestion; chat was excluded because causal decode splits normally.

### Training live-gate incidents

The 2026-08-15 end-to-end training gate found issues unit suites did not expose:

- llama-server strict schema promoted optional properties to required, so a small teacher emitted `"None"` rather than omitting a no-tool field;
- one lost non-streaming response parked a queue until the OpenAI SDK's 10-minute network timeout;
- `datasets.map` worker fork failed with EOF after CUDA initialization;
- unsloth banners preceded JSON protocol output;
- NumPy lacked the required Python 3.13 wheel at the research pin, torchvision from PyPI targeted a different CUDA major, and unconstrained uv resolution pulled incompatible Darwin-only packages;
- default Vulkan claimed all layers placed while `nvidia-smi` showed no work;
- missing `conversion/` beside llama.cpp conversion scripts caused `ModuleNotFoundError`;
- prebuilt archives lacked `llama-quantize`, while an F16 merge/export/smoke/promotion succeeded;
- `VBCSCompiler` held ~3.9 GB, leaving 4 GB free against a 6 GB estimate; shutting down build servers cleared the capacity refusal;
- an unlinked adapter could neither smoke-test nor promote;
- required route IDs in body DTOs made generated-client delete calls return 400 before route binding;
- startup recovery cleared a launch receipt before proving an orphan dead, making the process unidentifiable;
- directory swap and state write were separate rollback boundaries and could lose the previous working runtime;
- direct artifact-row deletion leaked multi-gigabyte staged bytes.

### llama-server node settings are captured once per boot — a `PUT` after that changes nothing until a restart

A live A/B that flipped `speculativeMode`, `chatCacheReuse` or `kvCacheType` between cells inside a single boot
measured no difference and concluded "the knob does nothing" — the second cell ran the first cell's value, because the
options singletons had already been resolved. Equally, a boot silently inherited the previous boot's value because it
was never set explicitly.

### a callback that runs under the supervisor's per-key gate must never re-enter the gated path for that key

Every real local benchmark run sat at zero output until the 900 s invocation timeout. Commit `30a514d00` had added the
"a profiling-owned process is never handed out" exclusion for a real race (an unrelated chat handed a profiling
process that was then killed mid-generation). A live `dumpasync --coalesce` capture showed the pending chain
`RunExclusiveProfilingCoreAsync → … → EnsureRunningAsync → DecideEnsureAsync → SemaphoreSlim.WaitUntilCountOrTimeoutAsync`:
`EnsureRunningAsync` (via the provider's warm) deadlocked on the semaphore the body's own frame held. `GetRuntimeInfo`,
excluded for the same reason, returned null one step later, which a benchmark's context admission rejects as
`EffectiveContextUnavailable` — a fix for only the first would have traded a hang for a failure. No test exercised
both sides together: `BenchmarkRunExecutorTests` substitutes `IInvocationRunner` wholesale, and the supervisor's
profiling tests drive a synthetic `body` that never re-enters the supervisor. `SupervisorProfilingReentrancyTests`'
deadlock-detector tests hang (then red) against the unpatched supervisor, while
`SupervisorProfilingTests.Profiling_ConcurrentEnsureForSameKey_NeverReusesTheProfilingProcess` keeps the exclusion
honest for every other caller.

### a provider's `TryAddSingleton(new XOptions())` binds NOTHING; prove a config key end to end before measuring with it

The 2026-09-25 text-encoder round set `StableDiffusionRuntime__TextEncoderOnGpu=true`, saw it in the node's
environment, and sd-server still launched with `te=cpu`: no `StableDiffusionRuntime:*` key (port range, TTL, cap) had
ever reached the supervisor since the section was introduced, because the provider registered a bare
`new StableDiffusionRuntimeOptions()`. Phase B of the round was void until the host bound the section
(`AddNodeImagesExtensions.BindStableDiffusionRuntimeOptions`).

### a reverse provider→application `ProjectReference` fails at RESTORE, not in an architecture test

Found during the OllamaSharp boundary migration (static-quality S5): the reverse-cycle break-proof
(`Providers.Ollama` → `Client.Application`, which already references `Providers.Ollama`) failed `dotnet restore` with
`error MSB4006: There is a circular dependency in the target dependency graph involving target
"_GenerateRestoreProjectPathWalk"`, before any test ran. The guard demonstrated is the project graph itself, not
`LayerDependencyTests`.

### NAudio's `CaptureAsync` starts the client itself and silently drops Silent packets

Established by reading upstream `NAudio` 3.1.0 `src/NAudio.Wasapi/WasapiRecorder.cs` from source during the Windows
per-application capture slice (audio-transcription S5, deviations 4–5). Writing silence would need the zero-copy
`DataAvailable` event, whose `ReadOnlySpan<byte>` cannot cross an `await`; the lagging `Others`-lane clock is therefore
documented rather than fixed.

### Benchmark quant fidelity — perplexity and KL divergence (2026-08-26)

With `-ngl 999` and flash-attn on auto (instead of the executor's `--n-gpu-layers 99 --flash-attn off`), the same
Qwen3.8-27B Q4_K_M / UD-Q3_K_XL pair measured 6.7983 / 6.9513 against 6.7977 / 6.9497: the placement knobs move
perplexity in the fourth decimal, which is why they are replayed rather than assumed. Before the requeue fix, a
base-logit lease held by another process threw `BenchmarkExecutionException` → `MarkFidelityFailedAsync`, losing the
measurement until someone re-measured by hand; the wait on the admission-retry cadence also rate-limits a requeued
item that is alone in the queue.

### Benchmark pairwise judging and Bradley-Terry (2026-08-26)

Kill/restart verified live mid-cohort: with four comparisons succeeded and one Running, `scripts/dev-stop.sh` left that
row and its work item `Running`. After `dev-start.sh` the interrupted comparison terminalized `Failed`,
`ReconcilePairwiseAsync` re-enqueued the same slot at `attempt_sequence = 2` (13 comparison rows for a 12-slot
cohort), the queue resumed and the cohort published. A re-judge then bumped the generation, re-enqueued all 12 and
published a second fit, with exactly one active row per generation. NOT verified live: a kill timed inside the
sub-second publication transaction; the filtered unique index `ux_benchmark_pairwise_fits_active` is what makes that
window harmless. Earlier, activation and project re-judge queued one pointwise judging of every succeeded run in a
pairwise cohort (the seed carried a resolved judge runtime); that measurement scaffolding was removed. The four-run MM
fixture converges in 62 sweeps.

### Benchmark verifiable rubric criteria (2026-08-26)

A mixed rubric with a Qwen3-32B Q5_K_M judge merged the model's clarity 10 and the verifier's 10 to 100, and the same
pair with a deliberately wrong expected value to 50 — `(50·10·10 + 50·0·10)/100` — asserting the weighted merge end to
end.

## 4. Agent, sandbox, and cloud evidence

### Sandbox no-follow failures

`Path.GetFullPath` plus prefix containment accepted a path whose interior component was a planted symlink. Passing an `O_NOFOLLOW` numeric value as `FileOptions` caused `ArgumentOutOfRangeException` for every file; raw `open()` with errno inspection was required. A file growing after sizing produced a truncated copy until the one-byte post-read probe was added.

### Bubblewrap live controls

- Read-only trees mounted under the jail's later `/tmp` bind disappeared without an error.
- `/proc` returned ENOENT for create attempts; `/dev` returned EROFS after remount-read-only.
- non-CLOEXEC FDs survived .NET `Process.Start` through setsid → systemd-run → bwrap.
- a trust fixture under `/tmp` failed the world-writable guard before ownership, hiding mutations to the ownership check.
- breaking `--remount-ro /dev` made the capability probe fail; a skip-on-probe-failure live test then skipped the regression it was created to find. Current live tests fail if trusted bwrap exists but isolation does not.

### MAF and cloud incidents

A positional MAF constructor once swapped name/instructions. Other failures came from setting nonexistent `ChatClientAgentOptions.Instructions`, double-sending instructions, and test fakes inspecting only System messages while MAF delivered `options.Instructions`.

A local model ID leaked into the Codex request because the per-call ID outranked the client default. Azure OpenAI per-call bearer policy was overwritten by a later API-key policy and surfaced as `IDX12741` about a malformed JWT; constructor-level authentication policy fixed final wire headers. Azure app-only tokens carried `roles`, so gateways checking `scp` rejected otherwise valid authentication.

### A lexical path-containment check is one shared helper, and the root is not its own descendant

Before consolidation there were five private copies of the containment check. `Path.GetFullPath` preserves a
trailing separator, so a root spelled with one normalised to a string that starts with `root + separator` and read as
its own descendant: `IsUnderRoot("/root/", "/root")` answered `true` in all five copies, each of them standing in front
of a `Directory.Delete(recursive: true)` or a process-tree kill. The copies had also drifted: three caught only
`ArgumentException` while their own doc comments claimed parity with the two that also caught
`NotSupportedException` and `PathTooLongException`.

### `ExecuteDetailedAsync` is the single compute execution boundary

The kill switch and request validation used to live in `RunPythonToolHandler`, which made them properties of one
caller: any second caller of the gateway executed model-authored code on a node that had never opted in, with no bound
on the script. `ExecuteDetailedAsync_IsTheOnlyExecutionPathThatReadsComputeEnabled` allow-lists the two
non-execution readers of the switch by name (the mathematician persona seeder, and AgentHome's use of
`ComputeOptions` for ceiling defaults). `requireResourceLimits` exists because `SandboxResourceCeilings.Resolve`
returns `null` when the backend cannot impose ceilings, so `run_python` runs unbounded on a host with no working
systemd user scope; `run_python` stayed byte-identical after the change. `RunPython_WithoutResourceLimits_StillRuns_BehaviourUnchanged`
is the test to invert if that decision is ever revisited.

### A tool handler that throws does NOT end the turn on Microsoft.Extensions.AI 10.9.0

Measured with a standalone probe against the then-pinned Microsoft.Extensions.AI 10.9.0. `EmitOutputToolHandler.ExecuteAsync`
carried the wrong claim (that throwing ends the turn) until commit 4d7ee3ea. Not re-measured at the 10.10.0 pin.

### A blank tool-call id is the only id-less shape Microsoft.Extensions.AI 10.9.0 can hand you

Established by a standalone probe against the then-pinned package during the S6 first-pass review fixes
(2026-09-04). Not re-measured at the 10.10.0 pin.

### Persisted chat parts are a render/reload record, not model context

Chat had written `NodeChatMessagePart` rows long before the integration continuation work and never replayed one. The
S6 kickoff assumed chat already replayed parts; it did not, so half of D-7 was new code rather than plumbing
(2026-09-04 pass).

### MAF traps

- The positional `ChatClientAgent` constructor order `(chatClient, instructions, name, description, …)` was verified
  at 1.20.0; it moved once under a bump (fixed in `8459cb962`).
- Sessionless runs: live spike at 1.20.0 (2026-09-09, Qwen3.8-27B). `SerializeSessionAsync`/`DeserializeSessionAsync`
  round-trip a pending approval cleanly, but resume accepts a forged `FunctionApprovalResponseContent` silently and the
  blob replays an approved tool once per resume with no consumption ledger. Do not re-open on "the serialization bug
  is fixed": the defect is the missing consumption ledger, not the round-trip. Pinned by `FrameworkApprovalGateTests`
  and `BudgetedApprovalReplayTests`.
- `HarnessAgent` no longer exists as a type at 1.20.0; it is decomposed into `FileMemoryProvider` +
  `FileSystemAgentFileStore`/`InMemoryAgentFileStore` (explicit stores, no implicit cwd writes) and the seven
  `file_memory_*` tools, built ungated. Desk read of the pinned `Microsoft.Agents.AI` 1.20.0 assembly, 2026-09-09. No
  in-repo pin exists because nothing references those types; that absence is the recorded state. Re-read at 1.21.0
  (2026-09-17): `HarnessAgent` still absent, zero references to `AgentFileStore`, `AgentFileSkill*` or
  `FileMemoryProvider`; that release's only `Microsoft.Agents.AI` break, PR #7671, does not affect this repo.

### The four `gen_ai.tool.*` attribute names track MEAI, and two of them carry a digest rather than the payload

The four names were read off Microsoft.Extensions.AI 10.9.0's own attribute constants and cross-checked against the
live OTel semantic-convention registry's status on 2026-09-09. Setting `gen_ai.operation.name` on the repo's spans
would make a convention-aware backend count every tool execution twice, since MEAI's function-invocation hop already
emits `execute_tool` on the pipeline's `OpenTelemetryChatClient` source; the repo's pair correlates to it by
`gen_ai.tool.call.id`. `tool.outcome` stays a custom tag rather than an `Activity` span status because existing
assertions and consumers read the tag. The failure-path leak: `OpenTelemetryLog.RecordOperationError` copies
`error.Message` into the span status description regardless of `EnableSensitiveData`, and a provider exception
message embeds request text or the raw HTTP body; `GenAiErrorDescriptionRedactionProcessor` in `ServiceDefaults`
(registered right after `GenAiCancellationStatusProcessor`) fixes it. Pins:
`ToolInvocationObservabilityChatClientTests.GetStreamingResponseAsync_ConventionPayloadAttributes_CarryHashesNotRawContent`,
`GetStreamingResponseAsync_NeitherSpan_CarriesTheExecuteToolOperationName`,
`AgentToolPipelinePolicyTests.ToolCall_ClaimsTheExecuteToolOperationNameExactlyOnceAcrossTheWholePipeline`, and the
two end-to-end tests in `GenAiErrorDescriptionRedactionProcessorTests`.

### A MAF-trio bump above patch level is not merged on a green build alone

`8459cb962` fixed a `ChatClientAgent` positional-argument order that changed under an unaccompanied Dependabot merge;
O1's premise went stale unnoticed across 1.17.0 → 1.20.0. Re-run for 1.20.0 → 1.21.0 on 2026-09-17:
`HandoffWorkflowSpikeTests`, `FrameworkApprovalGateTests` and `AgentSkillsProviderContractTests` passed; an assembly
diff of the two net10.0 sets showed `Microsoft.Agents.AI.Workflows` and `.Abstractions` identical in public surface
(version strings only), with `Microsoft.Agents.AI`'s single break being PR #7671 (`file_access_read_lines`;
`AgentFileStore.SearchAsync` abstract→virtual, `AgentFileSkillResource` ctor gained an internal-typed parameter); all
four O1 workflow-backbone axes unchanged, so O1 stands at 1.21.0.

### `Microsoft.Extensions.AI.OpenAI` and the plain `OpenAI` SDK move together, and NuGet will not tell you

2026-09-17 dependency round: MEAI 10.10.0 declares `OpenAI >= 2.13.0`, so NuGet resolved 2.14.0 without a warning and
the solution compiled clean in Release. At run time `OpenAIResponsesChatClient.ToResponseTool` references
`OpenAI.Responses.GlobalMcpToolCallApprovalPolicy`, which 2.14.0 deleted, so every Responses call carrying
`ChatOptions.Tools` threw `TypeLoadException`. A test that swallowed the send exception reported only the missing
body. `CodexToolCallingWireTests.FirstTurn_WithTool_WrapperAddsEncryptedReasoningInclude_AndKeepsTool` caught it; the
round reverted `OpenAI` 2.14.0 to 2.13.0 and recorded the reason on the pin.

### A second copy of the tool-invocation logic drifts, and the drift looks like a product bug

Both drifts accumulated in the training path after S2 promoted the invocation seam out of it: `HeadlessToolExecutor`
never consulted `IClientLocalToolRegistry`, so `read_file` and its five worker-owned siblings failed for a teacher turn
that asked for them; and a repair envelope from `ToolArgumentRepairAIFunction` came back as a successful sample
carrying model-repair guidance as its tool result, poisoning the dataset quietly.

### Graph Workflows: a node's `input` is its ONE satisfied predecessor's output, so a node inserted mid-chain REPLACES the content

The S4 live round watched agent-2 spend three chat and three embedding calls hunting for a haiku that was never in its
input, because a `Pause` between it and the producer replaced the content with the approval document.

### Graph Workflows: a cap has to be enforced BEFORE the work it bounds, not after it

The old Graph order compared the node count against the parsed graph, so it bounded nothing about the parse that
produced it. Dev Workflows had the same hole; S7 closed it under operator ruling R7-3 (re-opening what S6 recorded as a
finding only). `DevWorkflowOptions.MaxNodesPerDefinition` is 500 (higher than Graph's 200 because one materialization
expands a template subtree up to twenty times), held to `[Range(1, 10_000)]`. `DevWorkflowDefinitionSeeder` takes the
uncapped default, so the cap binds API-written definitions and not the shipped seeds (11 nodes at their largest at the
time). Both Dev create/update routes declare `ProducesProblem(StatusCodes.Status413PayloadTooLarge)`.

## 5. Frontend and API evidence

### API boundary failures

- Generated no-body POSTs lacked Content-Type and received 415 from FastEndpoints.
- Untyped `Files` produced an empty OpenAPI request body; generated code sent `{}` as JSON. Global Axios JSON headers also overrode multipart serialization.
- Typed 409 bodies without `detail` became `ApiError.message = undefined`, rendering blank toasts.
- `%2F` remained encoded in Kestrel route values and failed raw validators.
- hey-api generated `z.coerce.bigint()` for C# long while TypeScript declared number; runtime arithmetic threw mixed BigInt/number errors.
- `WriteAsJsonAsync(value, ct)` overwrote a previously selected problem content type with `application/json`.
- Declaring an ASP.NET ProblemDetails body as FastEndpoints ProblemDetails made schema-validating clients reject extension properties.

### UI race failures

Mantine bounded inputs fired a mount-time `onChange` and persisted a min/default over “unset”. Globally mounted queries fired before auth, cached 401, and did not recover after login. A new SignalR hub missing from the dedicated Vite websocket list fell through the generic proxy and wedged other websocket routes. A new `InvocationState` field appeared in live dispatch but persisted null because `Clone()` did not copy it.

### OpenAPI → hey-api is the sole REST data layer for React

- A Release-built host started by hand defaults to `Production`; `Program` maps the OpenAPI document only when
  `!IsProduction()`, so the regen fetch answered 404 on every path. Cost one wasted host start in the S2 regen,
  2026-09-06.
- An isolated `HOME` hid mise's trust store: mise refused the toolchain and the host exited with a trust error before
  OpenAPI readiness, which read as "the spec endpoint is broken" (S5 lane A regen, 2026-09-07).
- `scripts/tests/install.test.sh` isolates `HOME` too; every mise shim, `python3` among them, then aborted, and the
  installer used to report `one safe skills/xe-local-ai-engine tree` for a toolchain failure. The test now forwards
  `MISE_TRUSTED_CONFIG_PATHS` and `MISE_DATA_DIR` into each isolated-`HOME` invocation using the
  `openapi-live-check.sh` shape, and `install_skill_tree` in `install.sh` runs `python3 -c ''` rather than only
  locating it, so an interpreter that aborts says so (S7 lane A, 2026-09-07, reproduced both ways).
- A flag-gated family was silently deleted from the committed document by a regen run without its flag: the S3
  External Apps regen, 2026-09-11.
- The unannotated-query-member binding (`UninstallExternalAppRequest.ExpectedVersion`) was found by the S4 executor
  after S3's regen had landed; every endpoint test passed because each builds its own request.
- `$ref` reordering: the S5 regen that added `graph` to `GraphWorkflowRunResponse` showed two graph-workflow schemas
  removed at one offset and re-added at another; the live and committed path sets matched exactly.

### Before deleting a `.Produces<T>(status)`, trace every exception family the route can raise

On `CreateTrainingRunEndpoint`, `TrainingRunBlockedResponse` was dead, so the DTO and the 409 went together; but
`TrainingRunStore.CreateAndEnqueueAsync` still throws `TrainingConflictException`, which the global
`TrainingExceptionHandler` maps to a `TrainingErrorResponse` 409. ADR 0009's prose ("the route answers with a 400, so
the 409 could not occur") was false before the change and was believed by the implementer, the brief and the diff
review; an independent review in a separate lane caught it (2026-09-10 cleanup review). `pnpm run openapi:check`
regenerates the client from the committed spec and agrees with a wrong spec; behaviour tests pin the throw and the
envelope, not the declaration.

### A declared 413 is thrown by Kestrel inside model binding, so it needs a handler, not an early exit

Until S7 both capped families answered 500 on the only refusal path a real connection takes (the graph-workflow
four at 1 MiB, the development-workflow two at 2 MiB) while passing every in-process test; the S7 live round,
2026-09-07 §C, found it. In S8 both refusal paths were unified on `RequestBodyTooLargeProblem.WriteAsync(HttpContext,
detail, ct)` (`Client/ExceptionHandling/`): it sets the 413, `application/problem+json; charset=utf-8` and the body;
the handler awaits it and `Result(detail)` returns a private `IResult` for the endpoint's `Send.ResultAsync`.
`Results.Problem` was dropped because it writes the bare media type and serializes the body itself. The size-limit
types lost their error sink: `GraphWorkflowRequestSizeLimit.RefuseIfOversized(HttpRequest, IValidationErrors)` and the
Dev equivalent became `IsOversized(HttpRequest)` beside an `OversizedDetail` string (an absent Content-Length is still
not a refusal). Before S8 the endpoint half sent FastEndpoints' `errors[]` body through `Send.ErrorsAsync(413)` while
the six routes declared `ProducesProblem(413)`. The OpenAPI document did not change (live regen plus `openapi:check`
gave a zero diff). The 400 paths on these routes write FastEndpoints' `errors[]` and declare
`ProducesProblemDetails(400)`; S7's reading that some declared the ASP.NET shape on 400 was not true at the tip.
Shape assertions: `RequestBodyTooLargeAssert.DeclaredProblemShapeAsync` (`Tests/Testing`, compares the whole
content-type string) used by `GraphWorkflowDefinitionEndpointTests.GraphRoute_WithABodyOverTheCap_Returns413AndNeverReachesTheStore`,
`GraphWorkflowRunEndpointTests.StartRun_WithABodyOverTheCap_Returns413InTheDeclaredShape` and
`DevWorkflowEndpointTests.DefinitionRoute_WithABodyOverTheCap_Returns413AndNeverReachesTheStore`; the endpoint half is
pinned by `RequestBodyTooLargeProblemTests.Result_WhenExecuted_WritesTheSameAnswerTheHostRefusalWrites`.

### An EF unmapped-type raw SQL query must ALIAS every column, or it binds nothing

The one-shot Open Canvas import made this expensive: its read runs immediately before `DropCanvasWorkflows`, and
until 2026-09-22 startup swallowed a failed read, so an unaliased column would have destroyed every canvas behind one
log line. A failed read now stops startup and the ciphertext is staged first, but the alias rule is what keeps the
read binding at all.

### A test asserts the bundle string, never an in-code `defaultValue`

Before the suite-wide i18n init, 76 files hand-rolled a wrapper and never saw the bundle; the measured suite run after
the change turned 7 hidden defects green-to-red (2026-09-13). Defect classes: a stale default, a raw key with no
default, and a literal `Step {{index}} of {{count}}` that real i18next interpolates.

### An MSW request no handler declared FAILS the test that made it — declare the route, never widen the guard

MSW's own `onUnhandledRequest: "error"` rejects the caller's `fetch`, but TanStack Query catches the rejection into
`query.error`, so a test asserting elsewhere stayed green. The first full run under the guard found 15 such calls
across nine files (2026-09-20).

### Every `MantineProvider` a test mounts carries `env="test"`

One test read a tab's content synchronously and only passed because a modal's open transition gave an in-flight
request time to land; with transitions off the click is immediate and the read had to become a `findBy*`. The
three-effect behaviour was read from the installed `@mantine/core` ESM (`OptionalPortal`, `Transition`, `TabsPanel`).

### A gated-off Ollama runtime is a UI state, not an empty list

The loaded-models page stopped showing Ollama at all on 2026-09-23. The probe hook is its own query over the
running-models endpoint at infinite `staleTime`; the dependency-cruiser fingerprint baseline is no-growth, which is
why the hook lives in `core/`.

### an AudioWorklet is ONE self-contained `.js` file imported with `?url`

`extends AudioWorkletProcessor` is evaluated when the class declaration executes, so a class at module scope throws
`ReferenceError` under vitest before any registration guard runs. Source: S4 plan §2.3 (R37/R37a) and the C2/C4 dist
greps recorded in the S4 progress notes.

### a worklet node needs a path to the destination, and a pre-gesture context starts suspended

Introduced by commit `ae37b424b` (S4 plan §2.3 step 5), replacing a `numberOfOutputs: 0` construction.

### `CheckDependencyBaseline.mjs` fails on a new `warn` fingerprint, so a `no-orphans` warning is a gate failure

In S4 the rule did not fire: depcruise resolves `./Pcm16DownsamplerWorklet.js?url` after stripping the query, so the
import in `PcmCapture.ts` is a real incoming edge and `.dependency-cruiser.cjs` was left untouched. The existing
`**/*.js` biome override sets `globals: []`, which is why the worklet override must come after it (S4 plan §1a.3).

### nothing on the axios instance's import path may import the router

The full cycle: `AxiosInstance` -> `Interceptors` -> `Router` -> the route tree -> feature routes -> `client.gen.ts`
-> `Generated.runtime.ts` -> `AxiosInstance`. With the interceptors missing, every non-2xx arrived as a plain
`AxiosError` and `readGraphWorkflowConflict` / `isNodeChatReadOnlyConflict` read nothing. It surfaced in a Vitest file
that mocked `NodeChatAdapter` (changing which module loaded first); the app's own entry order happened to avoid it.
A lazy router `import()` re-split the bundle by about +60 kB, over budget (S1 frontend report).

## Change history of this ledger

- **2026-08-25:** split evidence from the mandatory rulebook. No rule should rely on this file alone for current versions or external state.
- **2026-09-27:** the rulebook became an index plus topic files under `agent-knowledge/`; narrative removed from condensed rules was appended here under the matching area heading, existing headings unchanged.
