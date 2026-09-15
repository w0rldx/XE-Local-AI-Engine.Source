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

- This is a standalone repository with remote **`public`** pointing to **`w0rldx/XE-Local-AI-Engine.Source`**. There is no `origin`, parent-repository pointer, or `w0rldx/XE-Local-AI-Engine` remote.
- Old references to `C0re.slnx`, `C0re.Client.React.Web`, `C0re.Tests.IntegrationTests`, or a C0re-parent Docker context are invalid. Current names are `XE-Local-AI-Engine.slnx`, `XE-Local-AI-Engine.Client.React/`, the three in-repo unit-test projects, `XE-Local-AI-Engine.Tests.E2ETests`, and `XE-Local-AI-Engine.AI.Contracts`.

### Cite symbols, not `file:line`, for anything under active development

**Rule:** cite `file` plus a symbol or quoted phrase. Never cite a line inside a file your change edits. Line citations remain in range after edits while pointing at the wrong code; symbol citations survive concurrent edits. See ADR 0004's anchor-maintenance note.

### Describe the environment generically — this is a public repository

**Rule:** docs, comments and test fixtures describe the environment in general terms. No installed-tool patch versions (Docker Engine, git, GPU driver, OS build number), no uid-bearing daemon socket paths or concrete `/etc/subuid` ranges, no host RAM or CPU-count inventory, and no "this box" / "on this machine" phrasing. GPU model, VRAM and driver version stay only where they are benchmark metadata that a measurement depends on (a perf table, a live-round record), never in fixtures or general prose; repo-pinned dependency versions (llama.cpp tag, .NET SDK, package pins) stay everywhere. **Prevents:** a slow re-leak of the maintainer's machine into a public repository. The 2026-09 External Apps status page re-published the daemon version, the uid socket path and the host RAM after the 2026-08-25 cleanup had already removed exactly that class of fact. **Authority:** maintainer decision 2026-09-11, reaffirming the 2026-08-25 cleanup.

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
- **Four analyzer rules are silent in Debug and `error` in Release, and each one rejects a shape a reviewer would call correct.** `S3267` refuses a `foreach` that could be a `Where`, including a character scan written as a loop on purpose; `MA0022`/`S4586` refuse a method returning a bare `null` `Task`; `S1117` refuses a local that shadows a field of the same class. There is no Debug signal for any of them, so a branch that built clean all day fails at the gate. Fix the shape or suppress it in the source with the reason — never "code around it" silently. Authority: the External Apps slices S0–S3, once each.


An incremental Release result is evidence only when projects actually compiled. Analyzer diagnostics are not replayed for skipped projects. Keep `TreatWarningsAsErrors` in Debug for compiler warnings, but do not confuse that with the analyzer wall. `XE_FULL_ANALYSIS=1` is the explicit analyzer-sensitive Debug loop. `RunAnalyzers=false` maps to csc `-skipanalyzers`; source generators still execute, so a zero-test run points to build/config/discovery, not this gate.

### Host filtering runs from an `IStartupFilter`, ahead of every middleware the composition root registers

A middleware you map first in `Program.cs` is not first. `HostFilteringMiddleware` is inserted by
`HostFilteringStartupFilter`, which `ConfigureWebDefaults` registers whenever `AllowedHosts` is in configuration —
and the shipped `appsettings.json` sets `AllowedHosts` to `localhost;127.0.0.1;[::1]`. Anything served on a
non-loopback address is therefore answered **400 Bad Request** before your branch, your guard or your route can
look at it, and the refusal is logged by nothing you own.

- **Rule:** a listener bound to anything but loopback must have its host names added to `AllowedHosts` in the same
  place the listener is opened. `ContainerBridgePipeline.AllowBridgeHost` is the shape.
- **Failure prevented:** the container bridge shipped a listener no container could reach. Every loopback test
  passed — `DefaultHttpContext` has no host filter, and an in-process `HttpClient` sends `Host: 127.0.0.1`, which is
  allow-listed. Only a real container, over a real socket, presented a Host header the filter rejected.
- **Diagnosis tell:** the request reaches the port but nothing of yours logs it. Capture the raw bytes
  (`nc -l` on the bound address) before suspecting the client, the network or the container.
- **Authority:** C1 commit 3, found by `ContainerBridgeRealDaemonTests` after commits 1 and 2 were already green.

### An explicit Kestrel endpoint discards the addresses `UseUrls` was given

`builder.WebHost.ConfigureKestrel(o => o.Listen(...))` does not *add* a listener. `AddressBinder.CreateStrategy`
picks the endpoints over the addresses whenever `KestrelServerOptions.ListenOptions` is non-empty, logs
"Overriding address(es)", and clears them.

- **Rule:** to open a second listener, append its URL to the existing ones
  (`builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey)`, split on `;`, `UseUrls` the union). Both binds must
  travel through one mechanism.
- **Failure prevented:** a `Listen` call for the container bridge would have silently dropped the loopback listener
  that desktop mode (`UseUrls($"http://{DesktopLaunch.LoopbackHost}:{port}")`) and Aspire (`ASPNETCORE_URLS`) both
  configure — leaving the bridge as the node's only listener, with the SPA and `/api/local/v1` gone.
- **Authority:** C1 commit 1; the plan specified `Listen` and it had to be deviated from.

### `ExternalAppEntityConfigurationTests` pins the external-app instance column list

`InstanceTable_MapsEveryColumnToItsSnakeCaseName` holds every column of `external_app_instances` as a literal array
and asserts the mapped set matches it **in both directions**.

- **Rule:** a new property on `ExternalAppInstance` needs that array extended in the same commit as the entity, the
  Fluent mapping and the migration.
- **Failure prevented:** the whole `XE-Local-AI-Engine.Client.Persistence.Tests` project goes red with "the entity
  gained a property nobody reviewed" — which is the gate working, not a flake. Adding the column without the list
  looks like an unrelated failure in a project you did not think you touched.
- **Authority:** C1 commit 2, adding `bridge_token`.

### A bare `TODO` in a C# comment fails the build — in **Release**

Sonar S1135 is an error under warnings-as-errors. Do not replace `TODO`/`FIXME`/`HACK`/`XXX` with another task marker. Describe the present limitation or rationale directly, and keep work tracking outside source comments. Debug may accept one of the banned markers; Release will not.

### A `[GeneratedRegex]` without `matchTimeoutMilliseconds` fails the build — in **Release** only

**Rule:** every `[GeneratedRegex]` names a `matchTimeoutMilliseconds` (copy the shape of `DeploymentPlanner.SubstitutionTokenRegex`). **Failure prevented:** Meziantou MA0009 is an error under the Release analyzer set and silent in Debug, so a Debug-green iteration loop hands the Release gate a red it never saw. **Authority:** `ContainerImageReference.BareImageIdRegex`, paid for in the External Apps follow-ups batch 3 (2026-09-12).

### A test that needs a migrated SQLite database copies the template — replaying the chain is the exception

**Rule:** in `XE-Local-AI-Engine.Client.Persistence.Tests`, a database at head or at a named migration comes from
`Testing/MigratedDatabaseTemplate.cs` (`CopyChatHeadAsync`, `CopyChatAtAsync`, `CopyIdentityHeadAsync`, or the
`MigrationSchemaProbe.From*TemplateAsync` entry points). Keep `MigrationSchemaProbe.MigrateChatAsync` /
`MigrateIdentityAsync`, which still replay from empty, only where the replay is what the test asserts: the applied
set versus the declared set, the pending-migration set, the migrator's own backup file, file creation, a journal
mode or a `PRAGMA`. A `WhenRolledBack_*` test copies the head template and runs the down migration for real; a
`WhenApplied_*` test copies the at-(N-1) template and runs the tail for real; data seeded before migration N goes
on the at-(N-1) template, never on a head copy. **Failure prevented:** every test paying a full declared-chain DDL
replay for a schema it never inspects the history of — and, on the other side, a template quietly making
`MigrationChainTests` vacuous by handing it a database it did not migrate. The template itself is a WAL database —
EF Core's `SqliteDatabaseCreator.Create` enables WAL when it creates the file, with no product pragma involved, and
`journal_mode` is a persistent file property the copy carries — so the invariant that makes a single-file copy safe
is not "never WAL" but "checkpointed, closed, no outstanding sidecar": the builder runs
`PRAGMA wal_checkpoint(TRUNCATE)`, releases the pool, closes the last connection, and then refuses to publish if a
`-wal`/`-shm` is still there, because copying the main file while a log holds committed rows loses them.
**Authority:**
`MigratedDatabaseTemplate` and `MigratedDatabaseTemplateTests`, twinned on
`XE-Local-AI-Engine.Tests/TestServerWebAppFactory.BuildMigratedTemplate`; paid for in the backend
test-performance plan, slice S1 (2026-09-13). For the current per-class cost, run the duration script over a TRX
set rather than quoting a number from here.

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

### `release.yml` is the intended release path — GitHub Actions must be enabled to run it

- `.github/workflows/release.yml` is the tag-triggered win-x64/linux-x64 Velopack path. It reuses `build-and-test.yml` as `validate`; packaging depends on validation.
- Actions enablement is external state. Check `gh workflow list --all` / `gh run list`; never quote a dated observation as current.
- `build-and-test.yml` targets `develop` and runs `python-quality`, `release-contracts`, `backend-tests`, `build-and-test`, and `client-react`. E2E is manual or label-triggered, not a blocking merge gate.
- `backend-tests` is a matrix: one `siblings` leg for every enrolled project except `XE-Local-AI-Engine.Tests`, and four `tests-<i>` legs that each run one `TEST_SHARD` of it through `scripts/run-tests-memory-safe.sh` with grouped namespace filters. Projects within a leg run concurrently with separate `--results-directory` values; the siblings use `--maximum-parallel-tests 2` (the last measured-safe width, not a limit of the dedicated runner).
- Coverage is merged by `(filename,line)` and checked against `scripts/backend-coverage-baseline.txt`. Keep discovery depth-bounded because `--report-trx` creates byte-identical attachment copies.
- `python-quality` uses the root tooling manifest, not the shipped training runtime. Never add dev tools to `tools/training/pyproject.toml`.
- `release-contracts` installs shellcheck **0.11.0** pinned and sha256-verified because `ubuntu-latest` ships 0.9.0, whose SC2317/SC2015 false positives fail the `--severity=style` pass; `scripts/lint-release-scripts.sh` refuses anything below 0.10.0 (exit 2). Do not "fix" the pin by lowering the severity.
- The Pester suite (`publish/tests/package-tester-win.Tests.ps1`) runs on every default `scripts/lint-release-scripts.sh` run (`--pester` only requests it explicitly, `--pester-only` scopes to it); a missing Pester module is a hard failure. It extracts its subjects from the real `.ps1` through the PowerShell AST rather than copying the logic, so a rename or restructure fails loudly instead of grading a stale copy.


**Hollow-gate controls:** the backend's `Passed!|Failed!` grep proves that MTP emitted a summary, not that a meaningful test succeeded. A unit where every test skipped still prints `Passed!`. `TZ=Europe/Berlin` stays set to expose non-UTC bugs. The grouped runner applies the summary guard per group, not per namespace. E2E remains a separate opt-in lane.

CI no longer sets `XE_REQUIRE_DOCKER_TESTS=1` and no longer pre-pulls any image. The four real-daemon suites (`ContainerBridgeRealDaemonTests` joined them with the container bridge) are **opt-in** and skip on the runner by design; their compensating control is that the Engine API wire shape they used to be the only proof of now runs unconditionally against `XE-Local-AI-Engine.Testing.FakeDocker`, a loopback HTTP fake the production `DockerDotNetRuntimeClient` talks to over a real socket — which needs no daemon and therefore cannot skip itself. The daemon half is gated before an RC by `scripts/run-docker-smoke-local.sh` (exit 5 = no usable daemon, 1 = product failed). This removed Docker Hub reachability as a hard dependency of the PR gate, where a registry blip or a rate limit used to turn a good branch red. **Never re-add a `docker pull` step or `XE_REQUIRE_DOCKER_TESTS` to `build-and-test.yml` to "restore" coverage: the coverage moved, it was not lost.**

Coverage instrumentation rewrites assemblies **in place**. Concurrent covered processes must use separate cloned output trees; without coverage, processes may share the built tree. `scripts/run-tests-memory-safe.sh` keeps coverage slots inside the repository because hosting tests locate the Client source by walking upward from the test binary. An empty dynamic-instrumentation report can claim `line-rate="1"`; `merge-cobertura.py` therefore treats zero source lines as an error. Keep the report-count checks as well as the percentage baseline, and raise the baseline only when coverage improves.

The architecture tests are ordinary tests inside `XE-Local-AI-Engine.Tests`; a full module run is what executes them. The deprecated manual packager is reference-only and is not the release authority.

### Keep a job literally named `build-and-test`, however the backend gate is split

**Rule:** `.github/workflows/build-and-test.yml` may spread the backend tests across as many matrix legs as it
likes, but exactly one non-matrix job must keep the id `build-and-test`, and that job is where the cross-checks and
the coverage merge live. **Failure prevented:** `develop`'s branch protection requires the literal status context
`build-and-test`; a matrix job reports as `backend-tests (siblings)` and friends, so renaming the job leaves every
PR waiting forever on a required check that no leg can ever satisfy. **Authority:** the `build-and-test` job in that
workflow — `needs: backend-tests`, `if: always()`, and a first step that fails unless the aggregate leg result is
`success`, because a *skipped* required check never resolves either.

**Rule:** `TEST_SHARD=i/N` in `scripts/run-tests-memory-safe.sh` is only accepted together with `TEST_GROUPS`, and
the shards must be proven to partition the module rather than assumed to. **Failure prevented:** the per-namespace
shape has no group index to slice, so silently ignoring the knob there would ship a "shard" that ran the whole
module on every leg; and a stride that overlaps or leaves a gap cannot be caught downstream, because
`scripts/merge-cobertura.py` unions by `(filename, line)` and merges a duplicated or a missing group to an equally
plausible percentage. **Authority:** the
`TEST_SHARD` parse in `scripts/run-tests-memory-safe.sh` (it rejects the combination before the Release build), and
the `build-and-test` job's check that the group indices across the legs' `units.txt` files, sorted, equal `0`…`GROUPS-1` exactly. A duplicate-only check is not enough: it passes a run that skipped groups.

### The packer's weights are load-bearing, and it cannot split a namespace

**Rule:** re-measure the `HEAVY` table in `scripts/run-tests-memory-safe.sh` from a green CI run's TRX artifacts
(coverage on, in-process width 1) whenever a namespace grows, and when one namespace alone outweighs a whole bin,
split it into sub-namespaces in the test project rather than re-tuning `TEST_GROUPS`. **Failure prevented:** a
namespace the packer cannot split is a hard floor on its shard's wall time. `DevWorkflows` was listed at a local
196 s while CI spent 1651 s on it; LPT therefore gave it a bin to itself, that bin landed on shard 0, and
`backend-tests (tests-0)` hit the 45-minute job timeout (run 34730991540) while the other three shards idled. The
table was also missing `GraphWorkflows` (503 s) and `Integrations` (366 s) entirely, and an unlisted namespace
weighs 1 — so the pack was balancing on numbers that described a different machine. **Authority:** the `HEAVY`
list and its `sed -nE 's/^  ([A-Za-z0-9_.]+) +# *([0-9]+)s.*/\1 \2/p'` weight parse in
`scripts/run-tests-memory-safe.sh`; the four `XE_Local_AI_Engine.Tests.DevWorkflows[.Execution|.Materialization|.Dispatch]`
entries and the folders behind them (IDE0130 makes folder = namespace mandatory); the CI runs named in the `HEAVY`
header comment (as of 2026-09-14 the per-run mean of 34861036286 and 34877183235 via
`scripts/test-durations.py --heavy --runs 2`, coverage on, JOBS=4, width 1, TEST_GROUPS=16 over four shards) and
run 34730991540 (the timeout).

### A HEAVY weight from one CI run is noise, and no table can balance the legs better than runner speed allows

**Rule:** weight the `HEAVY` table from the mean of the green runs available (`scripts/test-durations.py --heavy
--runs N` over their `tests-{0..3}` TRX artifacts), because the mean is the lowest-variance estimate of a
namespace's expected seconds; re-weight only when the namespace set changes (one added, split or gone) or a weight
is off by more than the run-to-run noise (>2x), never to chase shard balance, and score any re-weight on a run that
did not build it, knowing ~1.5 is the noise level. **Failure prevented:** (a) the single-run table measured from
34861036286 predicted a 1.05 shard max/min in-sample and realised 1.43 on the next green run (34877183235, whose
only test-code delta was a four-line cancellation fix in one Transcription test double), where
`DevWorkflows.Materialization` swung 1008s -> 649s; (b) believing the fix generalised — the two-run mean scored
1.14 and 1.12 on the runs that built it but 1.53 on the first held-out one (34882013960), because weight-balanced
shards ran at 0.77-1.17x relative speed inside that single run and per-leg runner speed dominates the table; (c)
re-weighting in the wrong place: a shard's wall is floor-bound by the solo namespace in its heaviest bin plus job
setup, so only splitting that namespace moves the slowest leg (entry above). **Authority:** runs 34861036286,
34877183235 and 34882013960, the `--runs` option in `scripts/test-durations.py` and its tests, and
[agent-knowledge-evidence.md](agent-knowledge-evidence.md) §1 (*HEAVY re-weight convergence*).

### Re-measure a TRX set with the script, never quote a class or namespace count from a document

**Rule:** derive per-class, per-test and per-namespace timings — and the counts a document would otherwise hard-code —
by running `scripts/test-durations.py` over a TRX set (`--heavy` emits the `HEAVY` lines
`scripts/run-tests-memory-safe.sh` parses — with `--runs N` when the set holds N runs of the same suite, so the
weights come out as a per-run mean; `--counts` the classes/tests/namespaces/files seen); cite the script, not a
number. **Failure prevented:** a comment in `XE-Local-AI-Engine.Tests/Diagnostics/TestServerWebAppFactoryTimingTests.cs`
carried a "61 test classes pay per host per test, 42 pay per class" split from 2026-08-23 that was already wrong by
2026-09-13, and a stale `HEAVY` weight is what pushed a CI shard past its job timeout (entry above). Both are the same
failure: a measurement frozen into prose. **Authority:** `scripts/test-durations.py` and its tests in
`scripts/tests/test_test_durations.py`, whose `--heavy` case asserts the runner's own weight regex still parses the
output.

### An ungrouped local coverage run reds a namespace that covered no product code

**Rule:** when `scripts/run-tests-memory-safe.sh` runs with `COVERAGE_DIR` set and `TEST_GROUPS` unset, read a
`<namespace>(no-coverage-report)` entry as "this unit exercised no product assembly", not as a test failure — confirm
against the batch's pass/fail counts before chasing it, and do not change the runner to tolerate an empty report.
**Failure prevented:** an hour spent debugging three phantom reds, or worse, a real empty report being made to pass.
The per-unit check requires a Cobertura report with content, so a batch whose tests only touch test-side code produces
one with none and the run exits 1 although every test passed — `Onboarding` (4/4 green), `Testing` (4/4 green) and
`Diagnostics` (0 enrolled) on 2026-09-13. CI does not hit it: its `TEST_GROUPS` packing puts those namespaces in bins
with product-touching ones, so each group's report is non-empty. **Authority:** the per-unit verdict loop in
`scripts/run-tests-memory-safe.sh` (the `cov` field of each `.result` file) and the header note above its
"Batch-level parallelism (JOBS)" section.

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

**A class that only EMITS on `XE.Node` needs no guard — but only after you check every listener on its instruments.** The bare-guarded capture suites above are not the whole population: `Knowledge/KnowledgeIngestionDispatcherTests`, `Mcp/McpToolCallTimeoutAIFunctionTests` and `Capacity/GpuModelLoadAdmissionTests` also listen on `XE.Node` with **no** `[NotInParallel]` at all. So the rule is: an emitter is safe unguarded when, for each instrument it publishes, every listener on that instrument is either bare-guarded or filters by instrument name. The failure this prevents is an emitter whose instrument an unguarded listener asserts by exact value — that listener then reads the sum of two tests and fails on a number nothing in its own file produced.

The two unguarded name-filtering listeners filter at `InstrumentPublished` (`instrument.Name == "mcp_tool_timeout_total"` / `== instrumentName`), so a foreign instrument is never even enabled. `KnowledgeIngestionDispatcherTests.EnqueueAsync_PublishesAcceptRejectCountersAndDepthGauge` is the loose one: it enables **every** `XE.Node` instrument and discards the rest in a `switch (instrument.Name)` default, asserts its counters as lower bounds, and gates its gauge on `when measurement == Capacity`. Its `listener.RecordObservableInstruments()` now fires other tests' live `McpAgentRunMetrics` observable gauges too, and is harmless only because of that name switch — do not relax it into an exact-value assertion.

Authority: `McpAgentRunCoordinatorTests` and `McpAgentRunCompactionServiceTests` each build a real `McpAgentRunMetrics` (a `Meter(NodeMetrics.MeterName)`) and carried a bare `[NotInParallel]` for it until the 2026-09-14 audit dropped both; between them they serialized the whole module for 13 fake-only tests. Their `mcp_agent_run_*` instruments collide with no name any listener above matches.

### A wall-clock budget sized on an idle box is a CI flake waiting to happen — use `TestBudgets.Contended`

Use `TestBudgets.Contended` for completion windows under module/coverage contention. Keep semantic timeouts inside the product option under test. Avoid stopwatch ceilings unless timing is the behavior.


`TestBudgets.Contended` is a **failure deadline**, not a sleep; polling/awaiting still returns immediately when the condition arrives. Do not widen a timeout that the test expects to expire. Raise a budget only when the awaited event can still occur.

If the operation can fault before publishing the awaited signal, inspect/await that task while polling so the test reports the real exception instead of timing out. If the wait observes one signal and the assertion observes a later cleanup signal, give the post-condition its own `AssertEx.EventuallyAsync`; terminal phase may be set before a reservation or activity marker is released.

**Adding a migration can red the module through a test that never mentions migrations.** `NodeChatMigrationRecoveryServiceTests` applies the WHOLE migration set against a real SQLite file under a wall-clock attempt budget, and starvation there does not read as a slow test: the attempt is cancelled MID-APPLY, leaving a half-rebuilt `ef_temp_*` table behind, and the retry dies on `table "ef_temp_<name>" already exists`. Two migrations plus five new migration tests (2026-08-25) were enough to tip it over on a 16-core box, with a different test of the class failing each run. The fix is `[NotInParallel]` on that class, not a bigger budget: its abandoned-lock test's first attempt is MEANT to exhaust the budget, so raising it buys the same increase in dead wall clock.

### `[NotInParallel]` is process-local, so a fixed path under the OS temp dir is shared by the runner's other processes

**Rule:** any file-system path a test asserts the CONTENTS of must be unique per OS process — suffix it with
`Environment.ProcessId` (or a Guid) at the single place it is defined, and do not let a keyed `[NotInParallel]` stand
in for that. The attribute is a constraint one process's own execution engine applies to the tests it scheduled; it
has no reach into a sibling process. `scripts/run-tests-memory-safe.sh` runs one fresh process per namespace with
`JOBS` of them at once and gives none of them a `TMPDIR` of its own, so every process's `Path.GetTempPath()` is the
same directory, and two classes in different namespaces sharing a key coordinate nothing.
**Prevents:** a sibling process writing into — or, at its session end, recursively DELETING — the directory this
process is asserting about. `FrameworkTempSentinel.Directory`
(`Tests/Endpoints/Transcription/TranscriptionUploadStreamingTests.cs`) pointed `ASPNETCORE_TEMP` at a fixed
`xe-local-ai-engine-tests-framework-temp`, and `ConversationUploadEndpointTests` (`Endpoints.LocalChat`, a different
namespace and so a different process) spilled a 2 MB payload into it; both classes carried a keyed `[NotInParallel]`
written to prevent exactly that overlap, which across processes it cannot do.
**Honest limit on the evidence:** this hazard is established by inspection, NOT by a reproduction. A negative
control on 2026-09-14 — the fixed shared path restored, the two spill-producing tests looped against each other in
two processes, 14 rounds, plus a full-namespace pass with four concurrent `LocalChat` sessions — stayed green every
time. So the fix removes a real hazard but is not proven to be what fixed the observed
`BufferedControlEndpoint_WhileRequestActive_DoesSpillToFrameworkTemp` flake (2 of 4 full local runs, ~33 s). That
duration matches the class's own local 30 s budget expiring plus host overhead, i.e. CPU starvation under `JOBS=10`
(the trace's H2), and `AssertEx.EventuallyAsync(… Count == 0, …)` returns the instant the directory is empty, so a
brief foreign spill cannot make it time out at all — only a deletion or a starved gate can. Both were addressed in
one commit (per-process directory + `TestBudgets.Contended`); if the flake recurs, H2 is the surviving suspect.
**Corollary for any recurrence:** record the failure and re-run that class alone before touching anything else —
never retry the suite until it comes up green, which destroys the evidence value of every "green ×3" acceptance
criterion around it. The pre-fix observation is in
[agent-knowledge-evidence.md](agent-knowledge-evidence.md) §1.
**Authority:** TUnit `NotInParallelAttribute` is enforced by the in-process scheduler;
`scripts/run-tests-memory-safe.sh` batching; trace and hypotheses in
`Plans/test-perf-2026-09-13/research/08-transcription-flake-trace.md`; fixed and controlled 2026-09-14.

### A silent `return` on the wrong OS reports a green pass, not a skip

**Rule:** gate a platform-specific test with TUnit's built-in `[RunOn(OS.Linux)]` / `[ExcludeOn(OS.Windows)]` (`OS` is `TUnit.Core.Enums.OS`, imported as `using OS = TUnit.Core.Enums.OS;` because that namespace's `LogLevel` collides with the logging one), or with `Skip.Test("<why>")` after a capability probe. Never `if (!OperatingSystem.IsWindows()) return;`. Both attributes are `SkipAttribute` subclasses whose reason names the platform; the proof test is `XE-Local-AI-Engine.Tests/Testing/PlatformSkipTests.cs`. A guard that depends on more than the OS keeps `Skip.Unless(...)` in the body on top of the attribute. `[RunOn]` is invisible to the CA1416 platform analyzer, so a method that calls a Linux-only API also carries `[UnsupportedOSPlatform("windows")]`. Prevents a suite reporting green on a platform where it verified nothing: 137 guards across 21 files were converted (107 `[RunOn(OS.Linux)]`, 28 `[ExcludeOn(OS.Windows)]`, 1 each of the inverses), concentrated in `ProcessSandboxRuntimeProviderTests`, `LlamaCppSourceBuildServiceTests`, `TrainingRuntimeServiceTests`, `SourceBuildRecoveryTests` and `CoderWorkspaceReaderTests`. Authority: test-principles audit 2026-09-05; the fallback shape for a probed capability is `Testing/SymlinkSupport.cs` / `Testing/JunctionSupport.cs`.

### No analyzer in this stack detects an assertion-less TUnit test

**Rule:** do not rely on Sonar S2699 ("Tests should include assertions") to catch a vacuous backend test; it cannot fire here at any severity. State the property in an `AssertEx`/`Assert.That` call rather than letting "nothing threw" or "nothing timed out" stand as the result; an assertion-less `[Test]` is caught by review alone. Failure prevented: a test that exercises code and checks nothing reporting green (two such tests lived in `XE-Local-AI-Engine.Client.Persistence.Tests`, `ModelRecommendationScheduleSeederTests` and `ModelCoordinationTests`, until 2026-09-05). Authority: sonar-dotnet source, `IMethodSymbol.IsTestMethod` recognises only the MSTest, NUnit and xUnit attribute lists (`SonarAnalyzer.Core/Semantics/KnownType.cs`, `.../Extensions/IMethodSymbolExtensions.cs`); `TUnit.Core.TestAttribute` is matched by full name and is in none of them. A live pilot on 2026-09-05 (assertion-less `[Test]` in Persistence.Tests, `S2699` at warning, Release build) reported nothing while a bare-TODO control on the same file fired S1135. TUnit itself ships no such analyzer (`src/TUnit.Analyzers/DiagnosticIds.cs`). The Vitest suite has the equivalent guard in `XE-Local-AI-Engine.Client.React/scripts/CheckTestsHaveAssertions.mjs`.

### `FastEndpoints.Config.SerOpts` is process-global, so a bare-context test sees whoever booted a host first

**Rule:** a test that drives a handler with a bare `DefaultHttpContext` must not hardcode the wire casing of a
FastEndpoints `errors[]` entry; derive it from the same global the writer reads, via
`XE-Local-AI-Engine.Tests/Testing/FastEndpointsProblemBody.GeneralErrorsName`. Host-based route tests keep asserting
the literal `"generalErrors"`, because a real host always has the policy set and that is the wire contract.
**Prevents:** an order dependency disguised as a flake. `Config.SerOpts` is a static; `Program.CreateAppAsync` seeds
it with the camelCase DI options when the first host boots (`TestServerWebAppFactory.EnsureApp`, lazily), and a
process that has not booted one yet serializes `"GeneralErrors"`. Under `--maximum-parallel-tests 1` the bare-context
test deterministically loses the race; at default parallelism it deterministically wins. A second trap found on the
same hunt: `AssertEx.Equal`'s `message` argument REPLACES the expected/actual text, so passing a type name as the
message hides the very values you need. **Authority:** `AssertEx.Equal` in `Tests/Testing/AssertEx.cs`; FastEndpoints
8.3.0 `ProblemDetails` proven standalone; ten of the twenty `ApiFoundation/` classes boot a host.

### Sleep-then-assert-the-negative can only fail when the code gets slower

**Rule:** never `Task.Delay`/`Thread.Sleep`/`setTimeout` to rule an event out. Drive the code to an observable blocking point through a gate the test controls, assert the negative there, then release the gate and assert the positive. Prevents a regression that fires the event *late* from reading green — the pass found 89 finite `Task.Delay`/`Thread.Sleep` sites in `XE-Local-AI-Engine.Tests`, removed 46 (converted through `AssertEx.SettleAsync` / `StaysIncompleteAsync` / `CompletesAsync`, new members of `XE-Local-AI-Engine.Tests/Testing/AssertEx.cs`, folded into a bounded poll, or deleted with the shape they served) and kept 43, each a bounded poll with a deadline, a positive wait with a real budget, or a `// real-timer:` site. A real timer is legitimate when the subject is a real OS process or the delay is the subject's own input; mark it with a `// real-timer:` comment naming why. Authority: test-principles audit 2026-09-05; `docs/wiki/17-writing-tests.md` §4.

### A loopback port you bound on `:0` and released is a candidate, not a reservation

**Rule:** binding a `TcpListener` on port 0 and disposing it yields a number that was free one instant ago, not a port held for you. When the port is the test's **target** — an occupied-port negative case — bind `:0` and keep the listener started for the whole test instead of releasing and re-binding the number. When a **child process or server** must bind it, drive the start through `LoopbackPort.BindWithRetryAsync` (`XE-Local-AI-Engine.Tests/Testing/LoopbackPort.cs`, linked into `XE-Local-AI-Engine.Tests.E2ETests`) and return `null` from the attempt on the product's own in-use signal so a fresh candidate is tried: the engine CLI's exit code **6** (`Program`, the `DesktopPortStore.IsPortAvailable` branch — it never falls back to another port), Kestrel's `IOException` → `Microsoft.AspNetCore.Connections.AddressInUseException` → `SocketException` chain. Never retry on time; retry on the signal, and let every other failure throw.

**Failure prevented:** the 2026-09-05 `scripts/run-tests-memory-safe.sh` flake in `EngineCliProcessTests.McpOnlyPrimaryServe_EmitsCanonicalReadinessSupportsStatusAndEnforcesPortAndLeaseExits`, where a batch spawning processes concurrently took the released candidate before the engine child bound it; the test passed on every isolated re-run. The same reserve-then-release shape existed in `HostBootSmokeE2ETests.Host_Boots_On_Real_Port_And_Health_Live_Returns_200`. Do not "fix" this by letting the server pick an ephemeral port — both tests exist to prove an explicitly requested port is honoured.

**Authority:** this fix; the Kestrel exception chain was confirmed by binding a held listener and starting a `WebApplication` on the same port under .NET 10. The in-process port-0 users that keep their listener for the test's duration (`DeferredLlamaServerEmbeddingGeneratorFailureTests`, `CodexAuthServiceTests`, `EntraAuthCodeSignInCoordinatorTests`, `LlamaGrammarLiveSmokeTests`, `XEReactClientFixture`) are not affected.

**Still carrying the shape (2026-09-06):** `DesktopPortStoreTests` reserves and releases in four tests. `ResolveBindUrl_WhenPersistedPortIsTaken_FallsBackToDynamicBind` and `IsPortAvailable_ReportsHeldAndReleasedPorts` re-bind the released number, so a lost race throws `SocketException` from `TcpListener.Start`; `ResolveBindUrl_WhenPersistedPortIsFree_RebindsThatPort` and `PersistThenResolve_RoundTripsToTheSameLoopbackUrl` require the number to still be free when `DesktopPortStore` probes it. The first two want the held-listener form (bind `:0`, read the port off the listener, never release); the last two are the candidate form and need the retry. Not converted here because they are synchronous and were outside this fix's scope.

### Leftover build daemons starve the timing-sensitive tests — and the packaging gate is where you notice

Before packaging or diagnosing a suddenly slow timing test, run `dotnet build-server shutdown` on a quiet machine. Compare failure duration with the intended timeout: a wall-clock failure under load differs from a fast semantic failure.

### A concurrent `dotnet build` corrupts a test run — and the result is then neither pass nor fail

- Never run builds and `--no-build` tests concurrently against the same `bin/` tree.
- Use `scripts/with-build-lock.sh -- <command>`; exit **69** names the lock holder.
- A wait-for-no-build loop written as `pgrep -f "dotnet (build|test)"` **matches the shell wrapper of its own backgrounded command**, whose command line contains that literal string, so it waits on itself forever. Match `pgrep -f "dotnet-root/dotnet (build|test)"` — and prefer the lock, which makes the collision impossible.
- Use `scripts/assembly-guard.sh guard --test-bins -- <test command>` around new runners. Exit **75** means `CONTAMINATED`: discard and rerun.
- Do **not** wrap `scripts/run-tests-memory-safe.sh` unless `NO_BUILD=1`; it already guards itself and its own build triggers an outer guard.
- The lock helper must mark the FD close-on-exec before MSBuild. Naive `flock <file> <command>` leaks it into build-server daemons and creates a phantom holder.


The assembly guard deliberately snapshots after a runner's own build and before testing. A direct `dotnet test` bypasses both lock and snapshot; wrap it or treat surprising results as provisional. VS Code C# Dev Kit's BuildHost is non-cooperating and can still rewrite assemblies under a held shell lock; recognize it by unchanged sizes with only mtimes moving and rerun.

Do not wrap the whole project validator in one outer lock: its internally locked trees would become pass-throughs and regain unsafe parallel build/test overlap. Prevention and detection must remain separate layers.

### After ANY failed build, rebuild to green before trusting a `--no-build` gate

A build that fails in project B leaves B's output directory untouched, including B's copies of dependencies that did compile, so a `--no-build` test run loads a pre-change copy of a product assembly that itself built fresh. Prevents grading old code as new (2026-09-04: three real passes read as failures in a shared worktree after another agent's compile error). Authority: reproduced from scratch with a two-project solution; `scripts/assembly-guard.sh` cannot see it (it compares output before/after a run, not output already stale at start).

### PROPOSED (awaiting operator approval): a solution build overwrites the E2E test host, so the flagged build must be the LAST one before a `--no-build` E2E run

**Rule:** `XE-Local-AI-Engine.Tests.E2ETests` compiles as `IsTestProject=false` / `OutputType=Library` unless
`-p:RunE2ETests=true` is passed, so a plain `dotnet build XE-Local-AI-Engine.slnx` rebuilds it as a library and
**overwrites** the Microsoft.Testing.Platform app the flagged build produced. In a gate chain that runs the backend
build and then a `--no-build` E2E run, repeat the flagged build in between; `scripts/run-e2e-local.sh` already
orders this itself. **Prevents:** a `--no-build` E2E invocation that finds no test host at all, read as a tooling
fault rather than as build ordering — and the neighbouring trap of a zero-test run exiting **0**. **Authority:**
`XE-Local-AI-Engine.Tests.E2ETests/…csproj`; `docs/wiki/13-testing-and-validation.md`; found while running C5's gate
chain (`Plans/audio-transcription-2026-09-11/progress/S4-notes.md`, "Gate ordering trap found here").

### PROPOSED (awaiting operator approval): TUnit.Playwright caches ONE browser per worker, so per-class launch arguments are silently ignored

**Rule:** `TUnit.Playwright`'s `BrowserTest` registers its browser under a fixed service key for the whole worker
(`WorkerAwareTest.RegisterService("Browser", …)`), so the FIRST test class to launch on a worker decides the
Chromium command line and every later class's `BrowserTypeLaunchOptions` is discarded without a warning. A suite
that needs its own switches must launch its own browser in a `[Before(Test)]` hook and drive its own page.
**Prevents:** two suites that differ only by a launch switch silently sharing the first one's switch. Measured over
three serial runs of the two fake-audio transcription suites: whichever class ran second was fed the other's audio —
the speech fixture correlated 0.951 when it ran first and 0 when it ran second, and the 440 Hz control 0.76 when
first and 0.99 when second, i.e. the negative control was measuring the positive case's WAV. The failure is
invisible from the test source, which reads as if each class had its own file. **Authority:** upstream
`TUnit.Playwright` 1.65.68 `BrowserTest.BrowserSetup`; `XE-Local-AI-Engine.Tests.E2ETests/Common/XEFakeAudioE2ETestBase.cs`
(`LaunchFakeAudioBrowserAsync`); `Plans/audio-transcription-2026-09-11/progress/S4-notes.md` §C5b.

### A full test-suite run can poison its own worktree's generated NuGet props

A fixture that restores under an isolated `HOME` rewrites `obj/*.nuget.g.props` with a since-deleted package root; the next Release build fails `CS0006` on every analyzer assembly. It looks exactly like the MSBuild node-reuse trap, but the cure is `dotnet restore` with `NUGET_PACKAGES` pinned to the real store, not a build-server shutdown. Authority: reproduced in the S5 worktree 2026-09-04.

### A Dev-mode sandbox run leaves MSBuild worker nodes holding a dead `NUGET_PACKAGES`

Development Mode gives each sandboxed task its own `NUGET_PACKAGES` under the task's runtime directory. On the process provider those `dotnet` children are host processes, and MSBuild's reusable worker nodes (`MSBuild.dll /nodemode:1`) outlive them with that per-task path still in their environment. A later `dotnet restore` anywhere on the same host can attach to one and write the by-then-deleted path into `obj/*.dgspec.json`, after which the build fails naming a `/tmp/xe-…/nuget` directory nothing asked for: **NU5037** during the graph-workflows S0 merge, **CS0006** in the session after it.

- Recover with `dotnet build-server shutdown`, then `MSBUILDDISABLENODEREUSE=1 NUGET_PACKAGES=$HOME/.nuget/packages dotnet restore --force`.
- Prefix long-lived agent shells with the same two variables; the dead path is inherited, not typed.
- Distinct from the props-poisoning entry above (same `CS0006` face, dead path in `obj/*.nuget.g.props` and no live worker): here the path is carried by a live `MSBuild.dll /nodemode:1` process, so a plain re-restore is re-poisoned until node reuse is off.
- The writer was `DevelopmentWorkspaceTools.BuildEnvironment`, which now sets `MSBUILDDISABLENODEREUSE=1` alongside the per-task `NUGET_PACKAGES`. `DevelopmentMountBrokerTests` asserts it.

### A relative test-project name in a worktree can resolve to a SIBLING worktree's binary

`dotnet test XE-Local-AI-Engine.Tests` run from inside a worktree resolved to another worktree's build output and
reported **zero tests** — a green-looking run that verified nothing, and never the code under the cwd. Name the
project by its **absolute `.csproj` path** and prefix the run with `MSBUILDDISABLENODEREUSE=1`; a live
`MSBuild.dll /nodemode:1` worker started under a sibling checkout is how the wrong path gets in (the entry above is
the same mechanism with a `NUGET_PACKAGES` payload). Zero tests is never a pass — see the treenode-filter rule.
Authority: S6 lane D, 2026-09-07.

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
- `AddRateLimiter` creates an undisposed replenishment timer per host. `ConfigureServices` computes every permit limit *outside* the `AddRateLimiter` lambda so its closure captures ints rather than `builder`, and raises them under `builder.Environment.IsEnvironment("Testing")` (auth 10 → 10,000/min) so a test host neither roots the disposed host graph nor throttles the single loopback partition every integration test shares.
- Avoid per-test providers that create long-lived MCP SDK registries/clients. Use the keyed/shared fixture pattern.
- `scripts/run-tests-memory-safe.sh` is the whole-module runner. A low `DOTNET_GCHeapHardLimit` is not a valid leak verdict; size-cap tests allocate large payloads.


`TestServerWebAppFactory` is more than a rename. The replacement closed four process-lifetime roots: a per-host MEAI function-descriptor cache key, rate-limiter timers/closures in Testing, EF service-provider caching for per-host connection strings, and SQLite pool groups. Keep the per-host JSON options, Testing rate-limiter bypass, `EntityFramework:ServiceProviderCaching=false` fixture setting, and `SqliteConnection.ClearAllPools()` teardown. Reintroducing `WebApplicationFactory<Program>` restores the top-level-entry-point `HostingListener` root.

**Shared per-class host eligibility is narrow:** TUnit runs those tests concurrently against one host and one SQLite database. Share only when tests are read-only or write exclusively Guid-named entities. Do **not** share a class that:

- asserts list counts, empty/global state, or shared-substitute `Received`/`DidNotReceive` calls;
- swaps DI/configuration per test;
- tests fixture lifecycle/startup/shutdown behavior;
- leaves process-global listeners or other side effects alive across test boundaries.

`BackendTraceCorrelationTests` proved `[NotInParallel]` is insufficient when a shared host itself outlives each test's `ActivityListener`. `ClassDataSource<T>` also needs a true parameterless constructor. Per-test fixture customizations use `ConfigureAdditionalTestServices`, `AdditionalConfiguration`, `EnableDevelopmentMode`, and related init properties; there is no `WithWebHostBuilder`.

`run-tests-memory-safe.sh` partitions by namespace into fresh processes, schedules longest first, refuses an empty unit list, and uses `TEST_GROUPS` for coverage grouping. Every batch runs at width 1 unless `PAR=N` widens it, and `PAR=N` can reintroduce in-process races. `JOBS` defaults to 16 on a host with at least 32 CPUs and 10 below that — two measured points, not a formula — and `JOBS=1` restores strict batch sequencing. The runner self-guards—an outer assembly guard sees its own build as contamination unless `NO_BUILD=1`. A one-process full run may be trustworthy on a roomy machine, but the batch runner remains the local wall-time/memory tool of record.

### Test hosts leak temp files to `Path.GetTempPath()` — keep the fixture cleanup

`TestServerWebAppFactory.DisposeAsync` must delete SQLite sidecars, node-data directories, and temporary `wwwroot` roots. A killed run cannot dispose; recover with:

```bash
find /tmp -maxdepth 1 -name 'xe-local-ai-engine-tests-*' -exec rm -rf {} +
```

Do not use a huge shell glob; it can hit `ARG_MAX`.

### A test host whose content root is the real Client source dir must register a fake `INodeDataDirectory`

Any fixture combining the real Client content root with redirected node data must remove `INodeDataDirectory` and register `FakeNodeDataDirectory`. Otherwise first-launch migration moves credential/settings files out of the checkout and teardown deletes them. `ServiceProviderValidationTests.HostCreation_LeavesTheContentRootNodeSettingsWhereItIs` is the guard.

### A node-settings save path must carry the local-only members over from the stored record

`StoredNodeSettings` holds members the wire DTO deliberately never carries: `MachineKey` (minted node-side by
`IMachineKeyProvider`, never sent to the client) and `ToolApprovalPolicy` (no operator field yet). A save that maps a
request onto a fresh record and persists it verbatim erases them. Dropping `MachineKey` is silent: the next start mints
a fresh GUID, and because inference profiles are keyed by machine key, every frozen profile on the box is orphaned —
the resolver never finds one, each model re-fits from scratch forever, the rows still read `Frozen` through the
profiles endpoint, and nothing is logged.

Carrying the value over at the *start* of the save is not enough, because the load and the write are far apart:
`IMachineKeyProvider` mints on the same node and can land between them, and this file is written WHOLE, so a save that
re-applies the key it LOADED overwrites one minted since. Both paths therefore persist through
`INodeSettingsStore.UpdateAsync`, whose mutation runs under the store's own lock and reads the record as it is at write
time — `NodeSettingsAdministrationService.ValidateAndSaveAsync` saves `settings with { MachineKey = latest.MachineKey }`
(covering every caller, including `ApplyAgenticPatchAsync`, which was already safe against plain erasure because it
starts from `current with {…}`), and `MachineKeyProvider` mints as
`latest.MachineKey is empty ? latest with { MachineKey = fresh } : latest`, caching the key the store actually holds
rather than the one it generated. `SaveTrustedMergedAsync` still carries the key over up front as well: that copy owns
the record the call VALIDATES and returns, including on the rejection paths that never reach a write. Guards:
`NodeSettingsAdministrationServiceTests.SaveTrustedMerged_WhenTheIncomingRecordHasNoMachineKey_PreservesTheStoredOne`
and `…_WhenTheMachineKeyIsMintedBetweenTheLoadAndTheWrite_PersistsTheMintedOne`,
`NodeSettingsEndpointTests.SaveNodeSettings_WhenValid_PreservesTheStoredMachineKey`, and
`MachineKeyProviderTests.MachineKey_WhenTwoProvidersMintConcurrently_AgreeOnTheOneStoredKey`. Authority: live evidence,
fu-b round 2a, 2026-09-05. Do not "fix" this by adding `MachineKey` to the request or response DTO, and do not write
this file with a bare `SaveAsync` composed from an earlier `LoadAsync`.

### The one temp artifact that is meant to survive: the migrated SQLite template

The MVID-keyed `/tmp/xe-local-ai-engine-tests-template-*.sqlite` cache intentionally survives teardown. It is atomically published and invalidates on migration/seed assembly rebuild. Delete it to force recreation. Set `UsePreMigratedDatabase=false` only when testing migrations themselves.

### Layering is mechanically frozen

- `LayerDependencyTests` enforces provider/contract direction.
- IDE0130 requires namespaces to follow folders. Endpoint DTOs deliberately keep the flat `…{Area}.V1` namespace because NSwag schema IDs include it; adding `.Dtos` churns generated API types. Mappers/validators follow their folders. See `docs/wiki/16-code-conventions.md`.

### OpenAPI → hey-api is the sole REST data layer for React

- React REST uses `src/core/api/generated/`; never hand-edit it.
- `pnpm openapi:check` only checks against checked-in `openapi/v1.json`; it cannot discover a new endpoint/field. For contract changes, fetch a live desktop spec, ensure no paths disappeared, update the spec, then regenerate.
- **Use `XE_LAUNCH_MODE=desktop`** or every `IDesktopOnlyEndpoint` disappears. Ports are random; set `OPENAPI_SPEC_URL=<host>/openapi/local/v1/v1.json`.
- A Release-built host started by hand defaults to the `Production` environment, and `Program` maps the OpenAPI document only when `!IsProduction()`, so the regen fetch answers 404 on every path. Set `ASPNETCORE_ENVIRONMENT=Development` on the throwaway regen host (the Aspire dev host already does). Authority: S2 regen 2026-09-06, one wasted host start.
- For mise-managed toolchains:

  ```bash
  MISE_TRUSTED_CONFIG_PATHS=~/.config/mise/config.toml \
  MISE_DATA_DIR=~/.local/share/mise \
  scripts/openapi-live-check.sh
  ```

  For `openapi-live-check.sh` this is now belt-and-braces rather than required: the script forwards both variables when
  the caller exports them and falls back to the real user paths when it does not, and it pins
  `ASPNETCORE_ENVIRONMENT=Development` for the host it starts, while keeping the `HOME` isolation the desktop port
  contract depends on. Exporting them yourself is still the safe habit, because it is what every other script on this
  list needs.
- **`scripts/tests/install.test.sh` isolates `HOME`, which hides mise's trust store from the installer it runs.**
  Every mise shim on PATH then aborts, `python3` among them, and the installer used to report `one safe
  skills/xe-local-ai-engine tree` for a toolchain failure. The test now forwards `MISE_TRUSTED_CONFIG_PATHS` and
  `MISE_DATA_DIR` into each isolated-`HOME` invocation using the `openapi-live-check.sh` shape, so no export is needed,
  and `install_skill_tree` runs `python3 -c ''` rather than only locating it, so an interpreter that aborts says so.
  Prevents: reading a toolchain trust failure as a corrupt source archive. Authority: S7 lane A, 2026-09-07,
  reproduced both ways.
- The same two variables are needed when you start the regen host BY HAND with an isolated `HOME`. Isolating `HOME` hides the trusted mise config, so mise refuses the toolchain and the host exits with a trust error **before** it ever reaches OpenAPI readiness — which reads as "the spec endpoint is broken", not as a toolchain problem. Point both at the real user paths. Authority: S5 lane A regen, 2026-09-07.
- **A desktop-mode host ignores `--urls`.** `DesktopPortStore.ResolveBindUrl` owns the bind address in that launch mode, so the port you passed is not the port it listens on. Read the real one from `desktop-port.txt` under the isolated data directory (`scripts/openapi-live-check.sh` does exactly that) and build `OPENAPI_SPEC_URL` from it. Prevents: fetching a spec from a port nothing is bound to and concluding the document is gone.
- **A flag-gated route family is absent from the spec unless its flag is set on the regen host.** The kill switches are request-path middleware, so a disabled family 404s and NSwag emits nothing for it; the regen then silently DELETES the whole family from the committed document. Put the flag on the regen host's own invocation (`ExternalApps__Enabled=true`, the same shape the Dev-workflow entry uses) whenever the feature you changed is one of them. Prevents: a regen that reads as a clean diff while removing every route of the feature you just added. Authority: the S3 External Apps regen, 2026-09-11.
- **FastEndpoints binds an unannotated request member from the query anyway, so endpoint tests cannot see a wrong binding.** A member with no `[QueryParam]` on a bodyless verb is documented as a `requestBody` in the spec while the endpoint reads the query. Every endpoint test passes — they build their own request — and only the generated client, which follows the document, sends the value where nothing looks for it. Annotate the member and assert the parameter's location in `OpenApiDocumentTests`. Prevents: a 400 that reproduces only through the generated client. Authority: `UninstallExternalAppRequest.ExpectedVersion`, found by the S4 executor after S3's regen had landed.
- **A schema that gains a `$ref` reorders NSwag's component output.** The referenced schema is emitted where it is first needed, so a spec diff can show several schemas removed at one offset and re-added at another with identical bodies. That is reordering, not loss. Verify it by comparing the PATH SETS (removals must be zero) rather than by reading the diff hunks, which is what `openapi-live-check.sh` reports. Prevents: reverting a correct regen because the diff looked destructive. Authority: the S5 regen that added `graph` to `GraphWorkflowRunResponse` — the live and committed path sets matched exactly while two graph-workflow schemas moved.

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
- `pnpm run lint` runs `biome format src scripts` as a gate (since 2026-09-09). `biome.json` excludes `**/.omc/**`,
  `src/routeTree.gen.ts` and `src/core/api/generated/**`, and Biome's CSS formatter is off (stylelint owns CSS). If
  the format gate fails on a file you did not write, look for gitignored runtime state under `src/` before touching
  source; runtime state lands in `<cwd>/.omc` whenever a tool runs from the React dir.
- react-doctor config is `doctor.config.jsonc`; `deslop` ignores need the `deslop/` prefix.

### Frontend tests: an `await import()` inside `it()` is charged to `testTimeout`

Cold dynamic component imports inside a test count against timeout and become coverage-only flakes. Keep `testTimeout=20s`. Prefer static imports and store setters; use `vi.resetModules()` plus dynamic import only when module-init hydration is under test.

### React Testing Library's `cleanup` is not automatic under Vitest

**Rule:** rely on `src/test/Cleanup.ts` — wired as a `setupFiles` entry in `vite.config.ts` — to unmount after each test; never assume RTL registers it for you. Prevents components mounted by an earlier test surviving into the next one, where they break `getBy*` with duplicate matches or leave a stale hub subscription running. RTL self-registers `afterEach(cleanup)` only when it detects global test hooks, and this project deliberately does not set `globals` in `vite.config.ts`. Authority: `vite.config.ts` `setupFiles`, `src/test/Cleanup.ts`; test-principles audit 2026-09-05.

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

### Review tooling that auto-detects the default branch fails here — pass the base explicitly

The remote here is `public`, not `origin` (§0), so any review or diff tool that infers the default branch from
`origin` reports that it cannot detect one. Pass the base branch explicitly (`--base develop` or the tool's
equivalent); do not add an `origin` remote alias to work around it. Operator ruling 2026-09-05.

---

## 2. Dev environment & local runtime

### The dev environment has a CUDA GPU — probe it, never infer it

Never infer the current hardware from this document. Run `nvidia-smi`, `nvcc --version`, and inspect the compiled architecture. The GPU is Blackwell-class (sm_120) as of the last verification, but hardware and toolkit versions have already changed once and will again.

WDDM/WSL traps:

- VRAM pressure may page to host RAM instead of OOM, producing correct but much slower inference. Do not use pressure here to validate OOM recovery or quote local benchmarks as consumer figures.
- `nvidia-smi memory.free` is global; llama.cpp `--list-devices` uses a process residency budget. They can diverge by tens of GiB. Use global availability for admission/invalidation and the process budget for process allocation; never reunify them.
- `nvidia-smi --query-compute-apps` may report no processes under WSL even while VRAM is occupied; per-process attribution is unavailable.

### Headless WSL2/Linux dev boxes have no keyring

With no Secret Service daemon, Azure.Identity can wrap MSAL cache persistence failure as `AuthenticationFailedException`. Walk `InnerException`; do not catch only `CredentialUnavailableException`. Such sessions are memory-only and do not survive restart.

### `dev-stop.sh` is the sanctioned stop path — but the leak it guards against no longer reproduces

Use `scripts/dev-start.sh`, `scripts/dev-status.sh`, and `scripts/dev-stop.sh`; never `aspire stop --all` during parallel development. `dev-stop.sh` binds ownership to the exact AppHost/DCP graph, records process start times to defeat PID reuse, fails closed on malformed Aspire output, and never restores a global `llama-server` kill.

Plain `aspire stop` cleaned the tested stacks in later measurements, but the original orphan trigger remains unknown and the fallback still performs real cleanup. Do not remove it until a reproducer identifies the trigger. See [evidence §2](agent-knowledge-evidence.md#2-local-runtime-evidence).

### `dev-stop.sh` resolves the AppHost of your CURRENT DIRECTORY, and `pgrep -af` matches its own shell

**Rule:** `scripts/dev-stop.sh` acts on the worktree the shell is standing in, not on the host you were last talking to. `cd` into the worktree first. A "no running instance" answer when you know a host is up is a **cwd check, not a stop** — it resolved a sibling checkout and killed nothing there. Second half of the same trap: `pgrep -af llama-server` matches its own shell's command line and reports a process that is only the search for it. Use `pgrep -x llama-server` or `ps -o pid,args -C llama-server`. **Prevents:** believing a host is stopped while it holds the GPU and the data directory, and chasing a llama-server that never existed. **Authority:** the S7 live round, 2026-09-07, where a drifted cwd made `dev-stop.sh` report no instance against a running gw-s7 host.

### The node operator secret is seeded by dev-start.sh, not by any tracked file

- `node-sqlite-key` is required. Never add a tracked default.
- `dev-start.sh` mints `.data/node.key` and passes **`Parameters__node-sqlite-key`** through the environment using Python because bash cannot export dashed names. Never put the secret in command-line args or `/proc/<pid>/cmdline`.
- A key/data mismatch surfaces as `AuthenticationTagMismatchException`, often looking like corruption. Before deleting data, retry with `XE_NODE_OPERATOR_SECRET_FILE=/path/to/key ./scripts/dev-start.sh`.
- Bare IDE/Aspire starts use interactive parameters or `dotnet user-secrets set "Parameters:node-sqlite-key" …`.
- Reuse populated model storage through `HuggingFace__ModelsDirectory`.
- **GGUF import is desktop-only; provision a dev-run FAST/extra model via `HuggingFace__ModelsDirectory`
  instead.** `PreviewGgufImportEndpoint`/`StartGgufImportEndpoint` (`XE-Local-AI-Engine.Client/Endpoints/ModelFit/V1/`)
  carry `IDesktopOnlyEndpoint` and are unreachable (404/405) outside `XE_LAUNCH_MODE=desktop`, and that flag is not
  a workaround here — it redirects the host onto the user-level data directory and abandons the isolated Aspire DB
  (`Program.cs`, `needsLocalData` branch). Point `HuggingFace__ModelsDirectory`/`HuggingFace:ModelsDirectory` at a
  worktree-private directory instead: hand-author its `index.json` (entries keyed `{fileName}:{quant}`, a
  `RegistryRevision` computed with `GgufRegistryRevision.ComputeV1`, `Providers.Abstractions/Gguf/`) alongside symlinks to the real `.gguf` files,
  so multiple models can coexist under chosen ids without a copy — this recipe is live-proven. A lighter path read
  from code but **not** live-verified: `GgufModelRegistry.LoadEntriesAsync` (`Providers.HuggingFace/Implementation/`)
  rescans the directory whenever `index.json` is missing/empty/corrupt and auto-registers any `.gguf` file whose
  name `GgufQuantParser.TryParse` (`Providers.Abstractions/Gguf/`) recognises a quant token in. Prevents reaching
  for `XE_LAUNCH_MODE=desktop` to import a FAST model and losing the isolated dev DB as a result.
- Aspire JSON is sensitive. `aspire ps` exposes the dashboard token and `aspire describe` may expose environment/connection data. Log only the `dev-status.sh` allowlist.
- Startup reapers clean owned stale llama/image servers. Never `pkill -f` a substring that also appears in the caller command line; kill by PID.


A user-secret key and `.data/node.key` can disagree. `dev-start.sh` always supplies the file, so data written under the old user secret fails later protected reads without naming the cause. Prefer `XE_NODE_OPERATOR_SECRET_FILE` pointing to the correct historical key; deleting `.data/node-sqlite/`, `dp-keys/`, and encrypted credential files is destructive fallback. `dev_ensure_node_operator_secret` warns when it mints a key beside existing data.

### The Dev-workflow surface answers 404 unless its flag is set at `dev-start` time

- Start an isolated host that actually serves it by putting the flags in the shell environment of the start script:

  ```bash
  DevWorkflows__Enabled=true WorkSessions__Enabled=true scripts/dev-start.sh
  ```

- **Graph Workflows no longer need a flag**: `GraphWorkflowOptions.Enabled` and the `Program.cs` middleware default both ship `true`. Setting `GraphWorkflows__Enabled=false` still turns the whole prefix into a 404, and a live round that wants it off must set that explicitly.
- **External Apps is the third gate and it is asymmetric**: `appsettings.json` ships `ExternalApps:Enabled=true` while the CODE defaults (`ExternalAppsOptions.Enabled`, `Program.cs`'s `GetValue(…, defaultValue: false)`) stay `false`, so a node with missing configuration fails closed. A round on a tree from before that flip must put `ExternalApps__Enabled=true` on the `dev-start.sh` invocation itself, exactly like the two above. Turning it off does **not** stop running application containers and does **not** hide the navigation group — `nodeCapabilities.externalApps` is compile-time — so the rollback order is stop or uninstall every instance, then disable.

- `AppHost.cs` forwards no such variable to the `app` resource; the flags reach the Client process as inherited process environment through `aspire` and DCP. They must therefore be on the `dev-start.sh` invocation itself, and they are read once at startup (`Program.cs`, `areDevWorkflowsEnabled`/`areGraphWorkflowsEnabled`), so changing one needs a restart.
- `DevWorkflowOptions.Section` defaults to disabled and `GraphWorkflowOptions.Section` defaults to ENABLED; only `WorkSessions:Enabled` ships `true` in `appsettings.json`, and no `GraphWorkflows` section exists in any `appsettings*.json` (it binds to the property defaults). One pair is enforced at startup: `DevWorkflowOptionsValidator` fails the host when DevWorkflows is on with WorkSessions off, because every workflow agent node runs as a work session. GraphWorkflows carries no such coupling — `GraphWorkflowOptionsValidator` checks only its own budgets — so it starts on its own.
- Prevents burning a live round on a "wrong route": each gate is a request-path middleware registered ahead of `LocalApiSecurityMiddleware` in `Program.cs`, deliberately so the switch cannot be probed by status code — which also means a disabled feature and a mistyped path are indistinguishable from the response alone.

### A live round in a worktree starts from a FRESH isolated DB, and a scratch host never touches the user data dir

**Rule:** never copy the main checkout's dev DB or keys into a worktree; it holds only the operator's real account, so
the demo admin login 401s and `POST auth/setup` short-circuits with `AlreadyInitialized`. Use a fresh isolated DB per
worktree, `POST auth/setup` a throwaway admin, then log in; access tokens expire after ~15 min, so a long seed driver
re-logs in. Persist `defaultModelName` and `maxMessageRequestTimeoutSeconds=1800` in node settings. The GGUF import
endpoints are `IDesktopOnlyEndpoint` and unmapped in a dev run, and desktop mode would resolve the USER data dir and
destroy the isolation — so extra models come from a worktree-private models dir (symlinks + a generated `index.json`),
never by writing into the shared one. Point `XDG_DATA_HOME` at a scratch dir for any throwaway host, or source the BYO
llama-server override (§ llama.cpp binaries) before `dev-start.sh` for a GPU round, so the instance never rewrites the
user-level `installed-runtime.json`. **That scratch `XDG_DATA_HOME` must MIRROR the real one, not be empty**: mise
installs its toolchains under it, so a bare directory breaks the `dotnet` shim and Aspire exits 7 reporting "the
`--apphost` option specified a project that does not exist" — a toolchain failure that reads as a missing project.
Build it as one symlink per child of `~/.local/share`, omitting only `XE-Local-AI-Engine`. With mise 2026.8 the symlink alone is no longer enough: the shims still report "aspire is not a valid shim" and `dev-start.sh` refuses with "Could not query Aspire state safely", so also pin `MISE_DATA_DIR=$HOME/.local/share/mise` on the same invocation (paid for in the External Apps follow-ups batch 3, 2026-09-12). **And in Aspire dev mode
`DesktopBootstrap` does not run**, so `HuggingFace:ModelsDirectory` falls back to `AppContext.BaseDirectory/models`
and an isolated node lists no models at all; set `HuggingFace__ModelsDirectory` on the same invocation whenever the
round needs a model. Two data traps: `agent_execution_logs` envelope rows terminalize asynchronously
(observed ~45 min later) — read the table, do not poll once; and `dev_workflow_*` run ids are stored UPPER-CASE while
SQLite text `IN` is case-sensitive, so a lower-case id returns zero rows silently. **Prevents:** a wasted or voided
live round on every item above — each one cost one during the AI-trends wave — and, before `75e0f7b60`, a fresh-DB
start deleting the operator's managed CUDA build through the shared runtime record (the product now never deletes a
build the record does not name, so the remaining stake is isolation, not data loss). **Authority:** the AI-trends
live rounds 2026-09-03/04; `LlamaCppSourceBuildService.ReconcileActiveAndBackupAsync`; `DesktopBootstrap` data-dir
resolution.

### Locked runtime decisions — do not "helpfully" reintroduce

- **Docker is off inference.** ADR 0004 permits it only for Development Mode build/test/lint. No Docker in model hosting/acquisition, embedding, image generation, or chat; HostAgent and sandbox gRPC stay deleted. Current implementation status belongs in `docs/roadmaps/development-mode-container-status.md`.
- The dependency is **`Docker.DotNet.Enhanced`** although its namespace/assembly are `Docker.DotNet`. Do not replace it with the obsolete original or pin transport packages separately.
- Rootless container uid 0 maps to the engine user; uid 1000 maps into subuids and may lose workspace access. Resolve the identity that maps to the engine's host uid and never host uid 0. `inspect` only echoes the requested uid, so verify by writing inside and statting the file host-side without following links. Uid-0 validity is daemon-dependent and cannot be rejected at options-validation time.
- A visible but inaccessible rootful socket must not silently redirect product selection to a rootless daemon. Real-daemon tests may probe the user socket only as test fallback; they run **only** under `XE_REQUIRE_DOCKER_TESTS=1` (`scripts/run-docker-smoke-local.sh`), where an unusable daemon is a failure rather than a skip.
- A read-only-rootfs .NET container needs a **64 MB `/tmp` tmpfs** with `noexec,nosuid,nodev`. `/tmp/.dotnet` is too narrow; named mutex setup needs the writable parent and ignores `TMPDIR`/`TMP`/`TEMP`. Verify flags as tokens, not substrings.
- An agent-writable `.git/config` and in-tree `.gitattributes` can execute host-side `core.fsmonitor` and clean filters. Keep hardened command-line `-c` pins, mount `.git/config` read-only in containers, and rewrite it to a minimal safe config before host git. Preserve format/filemode/bare/extensions keys; do not recreate `origin`.
- Process sandbox containment is measured per mechanism (`setsid`, `systemd-run`, `unshare`) and may degrade to none. Advertise only served capabilities and fail closed when a caller asks for unsupported confinement. Never claim flat network isolation across hosts.
- Network policy is deny-all where supported; restricted allowlists remain unsupported. AgentHome requests denial only when advertised, and Coder attaches to the same sandbox.
- Development Mode uses two sandboxes: a short-lived unrestricted `development-warm` sandbox for frozen restore, killed before return, then an agent-facing deny-egress sandbox where supported. Check which request you are reading. Dependency-manifest changes are retryable verdicts; test deletion remains a security exception.
- Tracked credentials are shadowed with engine-generated empty read-only files when the provider supports it; never delete them from the worktree. The process-provider fallback only records detection.
- Strip `XDG_RUNTIME_DIR` before executing sandboxed code. Network namespaces do not isolate Unix sockets; otherwise a child can escape via the user systemd bus.
- Provider selection is per feature through `IAgentSandboxRuntimeProvider` / `IDevelopmentSandboxRuntimeProvider`, not global `ISandboxRuntimeProvider` registration. Development may use Docker; AgentHome/Coder stay process-backed. MXC remains the long-term seam.
- Ollama remains a gated, opt-in secondary provider (`XE_OLLAMA_RUNTIME_ENABLED`). It was removed only from Aspire orchestration; llama.cpp is the default, not the only runtime.


**Docker permission boundary:** on Linux, access to the Docker socket is root-equivalent. ADR 0004 documents that risk; the product neither assumes nor provisions rootless Docker. Repository-supplied container configuration is rejected wholesale—especially `devcontainer.json` and aliases—because the repository is agent-writable and therefore untrusted input. Only engine-approved images/profiles may define the container. Widening the accepted configuration surface is an operator/security decision, not plumbing. **There is now a second consumer class** — External Apps, [ADR 0010](adr/0010-external-apps-container-execution.md) — which uses the same socket for user-installed application containers under its own policy. It does not widen the grant, and the sandbox SPI is untouched; see the block below and `docs/wiki/23-external-apps.md`.

### External Apps: container-written storage, the reserved variable prefix, and the evidence home

**Rule: a container-written directory cannot be deleted by the engine under a rootless daemon, and `rm -rf` exiting 0
is not proof that it was.** An image that runs as its own non-root in-container user (searxng's uid 977) leaves
`0700` directories owned by a HOST uid inside the operator's `100000:65536` subuid range — not the engine's uid. The
engine cannot traverse them, let alone unlink them. Deleting them needs in-container root **with
`CAP_DAC_OVERRIDE`**, which means the helper container that does it must keep Docker's default capability set:
`cap_drop ALL`, correct for every application container, silently breaks this one — the delete exits 0 having removed
nothing but one entry. Always verify a delete by counting the entries left, never by the exit code.
**Prevents:** an uninstall that tells the operator it "permanently deletes everything it has stored" and leaves the
data on disk, and a reset that fails with `StorageError`. **Authority:** S5 live step 36, failing then passing across
`e8c5e64eb`; `ExternalAppService.Pipeline.BuildStorageHelper` is the shape that works.

**Rule: `XE_` is a reserved variable prefix and the engine refuses it, so a live probe named `XE_…` never reaches a
container.** The refusal is a validation error the catalog path swallows into a fallback-to-cache, so a control that
asserts nothing off the engine reads as a pass. Name probes without the prefix, and assert the served manifest's
fingerprint off the engine before concluding anything from a catalog mutation. **Prevents:** a live control that
proves the opposite of what it claims. **Authority:** S5 live step 30, caught only by the assert-off-the-engine rule.

**Rule: `$` in a .NET regex also matches immediately before a trailing `\n`, so `^…$` accepts a value with a terminal
line feed.** Use `\A…\z` for anything that validates a whole string — an id, a service name, a digest-pinned image
reference, a hex hash. Keep `^…$` only for a scanner. The human-readable error message may still spell the rule as
`^…$`; that is the shape, not the pattern. **Prevents:** `image@sha256:<64 hex>\n` passing a digest-pin check.
**Authority:** the S1 catalog validator, five regexes, found in review.

**Rule: the External Apps architecture guard greps the directory's RAW TEXT, comments included.** A banned Development
Mode member name written in a comment under `Services/ExternalApps/**` fails the guard exactly like a call would,
which is why the state observer is an `IHostedService` with its own loop rather than a `BackgroundService` — the base
class's entry point carries one of the banned names. **Prevents:** a guard failure that looks like a false positive
and gets suppressed. **Authority:** `ExternalAppStateObserver`, S2.

**Rule: `Plans/` is `*`-ignored, so a live round's evidence cannot be committed where the plan puts it.** Decide the
durable home before the round ends: a `docs/roadmaps/<feature>-status.md` page is not ignored and is where the step
table, the NOT-OBSERVED reasons and the measurements belong. `git add -f` is not the answer — the ignore rule exists
so machine-specific working notes do not become repository authorities. **Prevents:** a finished round whose only
record disappears with the worktree. **Authority:** S5's unmade evidence commit;
`docs/roadmaps/external-apps-status.md` is the record that replaced it.

**Containment is requested and served per capability.** `SandboxContainment` is measured by doing the operation, not locating a binary. `Capabilities` advertises only mechanisms that succeeded, and `BuildLaunchPolicy` rejects requested-but-unserved controls. Do not soften either half. `SandboxNetworkPolicy.Restricted` remains unsupported; `None` means an empty namespace where served. AgentHome/Coder capability-gate the request so Windows and degraded hosts remain usable without claiming isolation.

`policy.json` is an initialization record, **not current-run authority**. `EnsureBaselineFilesAsync` preserves an existing operator-edited file, so a home created before deny-egress may still say `unrestricted` while current runs are denied. This is safe-direction under-reporting. Determine current posture from provider capabilities/status, never the baseline file.

**Development warm sandbox preconditions:** `DevelopmentWorkspaceProvider.PrepareAsync` runs only the frozen profile's restore in `development-warm`, and only when there is a clean tracked tree at the base commit (`git status --porcelain --untracked-files=no`). It runs once per `BaseCommit`, recorded in never-mounted `workspace.json`, then the sandbox is killed before the agent-facing sandbox starts. Do not warm a dirty tree or mount that metadata into the agent sandbox. A Windows/no-`unshare` backend may serve unrestricted agent execution; report the served posture from Development status rather than the requested policy.

**Credential shadowing and git hardening are outcome boundaries:** shadow only tracked credentials, never mutate/delete the worktree; the clone does not carry untracked files. Host git rewrites `.git/config` before **each** provider-independent call, while the container mounts it read-only. Minimal config preserves repository format and `extensions.*`; an empty file can make newer-format repositories unreadable. Hardened command flags remain even when a current command appears not to use a key because another subcommand can activate it.

A dependency-manifest change returns the retryable `dependency_manifest_changed` verdict; test deletion remains a security exception. The different shapes let the engine distinguish a legitimate unsupported task from an attack. Keep the manifest glob/version policy centralized in `DevelopmentCommandProfileCatalog`.

### llama.cpp binaries

- `LlamaCppReleasePins.PinnedTag` is the offline fallback floor, not the live recommendation. Re-check archive layout on pin changes.
- On any pin or BYO-build change, re-run `scripts/run-tool-grammar-smoke-local.sh` (§2). The unsanitized negative control must still return the grammar 400; a 200 there means llama.cpp moved its repetition ceiling and `LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound` must be re-measured. A run without a failing control proved nothing.
- Upstream ships no Linux CUDA prebuilt. Linux NVIDIA defaults to Vulkan; use `XE_LLAMACPP_SERVER_PATH` + `XE_LLAMACPP_VARIANT` or the managed source build for CUDA.
- A GPU variant may list zero devices and run on CPU. `IRuntimeDeviceAudit` degrades the effective profile only on determinate fallback; timeouts/failures are unknown and uncached. Never cache indeterminate probe results.
- Verify GitHub assets with the Releases API `digest: "sha256:…"`; there are no sidecar checksum files. Search recursively for the executable because archive layout varies.
- GPU offload has one owner per mode: GPU explore emits `--fit on --metrics` and no explicit placement; replay emits frozen `-ngl/-ts/-ot` and no `--fit`; CPU emits neither. Validate actual GPU work with the smoke.
- `LlamaCppSourceBuildRequestValidation.Normalize` must be idempotent: `Normalize(Normalize(x)) == Normalize(x)`.
- Helper tools (`llama-quantize`, `llama-perplexity`) are resolved **by name beside the resolved `llama-server`** (`LlamaCppToolBinaries`, surfaced as `LlamaBinary.QuantizerExecutablePath`/`PerplexityExecutablePath`, evaluated on read). Presence is never a precondition for adoption — a runtime missing one still serves; the caller that needs it fails specifically. The prebuilt archives carry `llama-perplexity` but no `llama-quantize`. **Both source-build paths only gained `llama-perplexity` when their cmake `--target` lists were widened (`CudaBuildService`, `LlamaCppSourceBuildService`) — any tree built before that, including a BYO `~/cuda-llama/<tag>` tree, has `llama-server` and nothing else, so benchmark fidelity there must rebuild the runtime or point at a prebuilt.** (`CudaBuildService` never built `llama-quantize` either.)
- **`llama-fit-params` is resolved the same way (`LlamaFitParamsProcessRunner`: `Path.Combine(serverSpec.WorkingDirectory, "llama-fit-params")`), and a BYO build that only built the `llama-server` target — a BYO `~/cuda-llama/<tag>` tree included — has no inference profiles at all: a manual Explore returns 400 "llama-fit-params did not produce a concrete replayable context and GPU placement", so nothing can be benchmarked or frozen and D13 staleness cannot be proven live.** The app degrades correctly (a warning names the missing capability, the spawn stays auto-fit). Before an inference-profile live round, check `ls "$(dirname "$XE_LLAMACPP_SERVER_PATH")/llama-fit-params"`. **Operator ruling (2026-09-05): the shared BYO build tree stays as-is — no `--target llama-fit-params` added to the build script — because the launch-policy fingerprint hashes the selected `llama-server` plus its sibling shared libraries (`RuntimeBundleIdentityCalculator.IsRuntimeBundleFile`: the executable itself, and any name ending `.dll`/`.dylib` or containing `.so`), and the target drops `libllama-fit-params-impl.so` into that directory — so the fingerprint changes once and every frozen profile on every checkout using it goes Stale.** The scratch-copy procedure is therefore the supported path for any such round, not a workaround: build the target in the shared tree, take an md5 manifest of `bin/` before, MOVE `llama-fit-params` + `libllama-fit-params-impl.so` into a `cp -a` scratch copy of `bin/`, verify the shared manifest is byte-identical after, and point `XE_LLAMACPP_SERVER_PATH` at the scratch copy for the round — it dies with the session, so each round rebuilds it. Incident: `docs/agent-knowledge-evidence.md` §2.
- Emit `reasoning_budget_tokens` only when the template provides a think-end marker. At the pinned build, higher `--log-verbosity` is more verbose; use `-lv 5` for reasoning-budget traces. Empty logs at `-lv 1` are a threshold symptom.


`IRuntimeDeviceAudit` and its probe cache are keyed by binary path + mtime. A timed-out/failed probe is `unknown`, never `no GPU`, and is not cached; otherwise a transient failure would permanently force CPU sizing. A determinate `cpuFallback` degrades only the **effective** profile to RAM sizing while the hardware endpoint still returns raw hardware plus audit details. Compute the audit lazily and outside per-spawn hot paths.

**Exact launch ownership:**

| Mode | Required placement/offload args |
|---|---|
| GPU explore | `--fit on --metrics`, explicit `-c`, optimized KV/FA; **no** `-ngl/-ts/-ot` |
| Frozen replay | stored `-ngl/-ts/-ot` verbatim; **no** `--fit` |
| CPU | explicit `-c`, `-t/-tb`; no fit or GPU placement |

A missing `-ngl` is correct in explore and a defect in replay. `BuildLaunchSpec` receives the central `LlamaServerLaunchPlan`; do not scatter defaults back into supervisors/call sites. `--fit` and explicit placement cannot coexist, while `-c/-fa/-ctk/-ctv` may coexist with fit.

**Optimized launch fallback:** GPU normal launch tries `-fa on -ctk q8_0 -ctv q8_0`. On readiness failure it retries **once** without explicit KV types and with `-fa auto`. Persist `llama-launch-fallback.json` per (backend, KV type) only if the safe retry succeeds; a broken model must not poison later launches. Legacy un-keyed entries (bare backend names) are ignored and dropped from the file on the first read. The file is USER-level and shared by every node process, so a write re-reads and merges under an OS lock on the sibling `llama-launch-fallback.json.lock` (the lock never sits on the state file itself, or the atomic replace over it would fail on Windows); a lock this process cannot take degrades to the in-process lock, where a concurrent sibling write can still be lost. Frozen profiles bypass fallback. Capacity/advisor estimates stay f16-conservative because the safe path may run.

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

A Graph Workflow Agent node's `responseJsonSchema` goes down the same grammar path, so keep it flat — and its
**structure IS enforced**. llama-server compiles the schema into a real GBNF grammar and applies it from generation
start: proven live 2026-09-07, when a `zqx-alpha`/`zqx-beta` enum held against a prompt that explicitly forbade JSON
and both tokens, and confirmed directly by the compiled grammar captured verbatim from a `--verbose` llama-server.
`enum`, `required`, the object shape and the types are all carried. The compiled `root` rule leaves the model's
`<think>` block **optional and unconstrained** and forces the schema only on what follows — captured verbatim as
`"<|im_start|>assistant\n" space ("<think>" "\n"? until-13 "\n"? "</think>" "\n"? "\n"?)? space (...)`, where the
whole think group is `?` and `until-13` is a wildcard-until-`</think>` rule — so the grammar is reasoning-aware by
construction rather than by disabling reasoning. The same round confirmed it holds at `reasoningEffort` `none` and
`high` alike. This replaces the earlier "unproven" reading, which had only a positive control. **Authority:**
`DeferredLlamaServerStructuredOutputTests`; live round 2026-09-07, grammar read off a `--verbose` llama-server.

**But every value bound is gone before the request leaves the .NET process, and nothing warns you.** The
`Microsoft.Extensions.AI.OpenAI` 10.9.0 adapter runs an **unconditional** strict-schema transform
(`OpenAIClientExtensions.StrictSchemaTransformCache`, applied in `OpenAIChatClient.ToOpenAIChatResponseFormat`) with
**no opt-out** — the `strict` AdditionalProperties key only sets the wire flag and does not gate the rewrite. It
relocates the value-constraint keywords into the schema's `description` **string**, one `"<keyword>: <value>"` line
each, which is advisory prose to the model and not a machine-checkable constraint. Live evidence: a `maxLength: 3`
schema reached llama-server as `{"description":"maxLength: 3","type":"string"}` and produced a **1302-character**
field, while an `enum` in the same round arrived untouched.

The relocated set is 22 keywords, in the order the source declares them: `contentEncoding`, `contentMediaType`,
`not`, `minLength`, `maxLength`, `pattern`, `format`, `minimum`, `maximum`, `multipleOf`, `patternProperties`,
`minItems`, `maxItems`, `unevaluatedProperties`, `propertyNames`, `minProperties`, `maxProperties`,
`unevaluatedItems`, `contains`, `minContains`, `maxContains`, `uniqueItems`. `default` goes the same way through a
separate option (`MoveDefaultKeywordToDescription`, rendered `"Default value: …"`). `exclusiveMinimum` and
`exclusiveMaximum` are **not** in the set — they survive. Authority: `unsupportedProperties` in
`src/Libraries/Microsoft.Extensions.AI.OpenAI/OpenAIClientExtensions.cs` at `dotnet/extensions` tag `v10.9.0`;
re-read that list on a package bump rather than trusting this copy. Do not dump
`Microsoft.Extensions.AI.Abstractions.dll` looking for it — that assembly carries an unrelated format vocabulary that
reads convincingly like the same table and is not.

Two more effects of the same pass. Every declared property is made `required`, so a property you left optional is not
optional. And `additionalProperties: false` is injected only into an object that has `properties` and **omits** the
key: an object that sets `additionalProperties` explicitly, `true` or a schema, is left open as written. The walk
descends `properties`, `items`, `additionalProperties`, `not`, `anyOf`, `oneOf` and `allOf` and nothing else, so a
constraint parked under `$defs`, `definitions` or `prefixItems` is **not** relocated and a `$ref` target is untouched
— which is a reach to know, not a workaround to rely on.

**Tool** schemas are not transformed by default, which is why XE carries its own
`LlamaGrammarToolSchemaCompatibility` sanitiser for that path.

**Since S7 none of the above describes the llama.cpp lane any more.**
`DeferredLlamaServerChatClient.ApplyResponseSchemaPassthrough` writes the AUTHORED schema onto the outbound
`ChatCompletionOptions` at `$.response_format.json_schema.schema`, and `OpenAIChatClient.ToOpenAIOptions` applies its
own members with `??=` — its last line being `result.ResponseFormat ??= ToOpenAIChatResponseFormat(...)` — so a
response format already set by the factory wins and the strict transform is never invoked at all. Every keyword
arrives as the author wrote it: bounds, `pattern`, `format`, the author's own `required` list, and no injected
`additionalProperties: false`. `ChatOptions.ResponseFormat` is deliberately left set, because it is what the
MEAI-level consumers above the client read; it no longer decides what goes on the wire. `strict` is sent **false**:
llama-server reads only `json_schema.schema` and ignores the sibling `name`/`description`/`strict`, and claiming
OpenAI structured-output strictness would be a guarantee llama.cpp does not make.

One thing is still removed on that lane, on purpose: a repetition bound above
`LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound` (1024), because llama.cpp's GBNF converter refuses to
compile it — HTTP 400 `Failed to initialize samplers`, the turn never reaches inference. The strict transform used to
prevent that by accident; the response schema now runs through the same `Sanitize` pass the tools array does, and only
where it must. A schema whose bounds are all in range travels verbatim.

The rewrite still applies in full to **every other provider**. Do not rely on a value bound in a response schema on a
cloud runtime; validate it yourself downstream.

Two things that will waste your time here. `InvocationAgentFactoryTests.CreateAsync_WithAResponseJsonSchema_ConstrainsTheTurnToIt`
is **not** evidence against the above and structurally cannot catch it: it asserts on the `ChatOptions` the factory
built, which is upstream of the adapter that does the rewriting. Catching it needs an observation of the wire body or
of the compiled grammar. And the XE Debug log never shows the grammar at any Serilog level —
`Serilog__MinimumLevel__Default=Debug` greps **zero** hits for `grammar`, `json_schema`, `response_format` or `gbnf`.
What prints both the compiled GBNF and the schema llama-server actually received is llama-server's own `--verbose`,
set through `PUT models/{modelName}/launch-args` (`rawArguments`) followed by `POST models/{modelName}/unload`.
**No host restart**, and no 500 — the S6-era note recording a connection refused to 11434 for a llama.cpp-provider
model is retired. See the entry below for what that route now does.


`LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound` is empirical for the **whole production offer**, not an upstream constant or per-field limit. Third-party MCP schemas make this an open boundary. If sanitization still fails, translate it to `FailureCategory.ModelCapabilityUnsupported`; do not surface the raw sampler error as a model defect. The live smoke's unsanitized negative control is load-bearing: a 200 means either a reasoning template skipped GBNF or upstream changed the limit.


Schema bounds are advisory to the model but handler validation is authoritative. Removing a large `maxLength` from the production schema would also weaken `ToolArgumentRepairAIFunction` and lie to non-llama providers. The compatibility projection strips only the offending wire keyword for llama.cpp. `ChatLocalToolsE2ETests` uses scripted FakeOllama and cannot compile GBNF; unit tests enforce the measured constant but cannot detect an upstream limit change.

### A second patch onto `ChatOptions.RawRepresentationFactory` must compose the first

**Rule:** the slot is single. The response-schema passthrough, the thinking switch, the reasoning budget and the
sampling passthrough all write the same request body, and a factory that assigns instead of chaining silently discards
whatever the previous one wrote — no compile error, no runtime signal, just a body missing a field the caller believes
it sent. Always go through `OpenAICompatibleRequestBody.Chain`. **Prevents:** `chat_template_kwargs` or the raw schema
vanishing from the wire on exactly the Constrained-mode teacher turn that needs both. **Authority:**
`DeferredLlamaServerStructuredOutputTests.ResponseFormatAndThinkingSwitch_BothReachWire`, which captures one body
carrying both.

### OllamaSharp wraps HTTP **400 only** into `OllamaException` — every other status is a bare `HttpRequestException`

**Rule:** on the pinned OllamaSharp 5.4.30, `EnsureSuccessStatusCodeAsync` parses the response body into an `OllamaException` on the `(int)response.StatusCode == 400` branch alone; every other failure status falls through to `HttpResponseMessage.EnsureSuccessStatusCode()` and surfaces as an `HttpRequestException` with `StatusCode` set. Catch on the status, never on a message substring. **Prevents:** dead code that reads as a handled case — a `catch (OllamaException)` filtered on `"not found"` for a 404 never runs, so the failure it claims to absorb propagates instead, and the next reader trusts the comment rather than the branch. **Authority:** `ilspycmd` of `OllamaApiClient.EnsureSuccessStatusCodeAsync` in `~/.nuget/packages/ollamasharp/5.4.30`; found by a second-pass review of the S7 unload change.

### Unloading a model asks BOTH local runtimes, because residency is not the provider map

**Rule:** unloading evicts a model from wherever it is resident, and both local runtimes are asked because the node cannot know which one holds it. The llama-server supervisor is asked first, gracefully and per `ModelRole`, so an in-flight turn drains rather than being killed; stopping the child process is both what frees its VRAM and how edited launch arguments take effect, since the next request respawns it. The Ollama daemon is asked second whenever the optional runtime is enabled (`OllamaRuntimeGate.RuntimeEnabledConfigurationKey`), and an unreachable daemon or a model it has never heard of both count as success, each absorbed at exactly one layer: `OllamaModelUnloader.UnloadAsync` swallows an `HttpRequestException` whose `StatusCode` is `NotFound`, which is Ollama's answer for a model it does not know, and `UnloadFromOllamaAsync` tolerates only `StatusCode is null`, a refused connection with no daemon behind it, logged Debug. Any other Ollama failure carries a status and propagates to the operator. **Prevents:** consulting the per-model provider map, which records where a model would be *served*, not where a process currently holds it — routing on it sent an Ollama-resident, unmapped model to three no-op llama-server ejects and left it loaded. `Unloaded` is false only when a llama-server role reported `TimedOutStillBusy`. **Authority:** `UnloadLocalModelEndpoint.HandleAsync` / `EjectEveryRoleAsync` / `UnloadFromOllamaAsync`, `OllamaModelUnloader.UnloadAsync`, and `LoadedModelsPageE2ETests`, which caught the regression.

### The Ollama gate-off branch registers no-op `IModelCapabilityClient` and `IOllamaModelService`, so the node still boots

**Rule:** `AddNodeModelRuntimeExtensions.AddOllamaRuntime` owns BOTH branches of the gate, and the gate-off branch registers a no-op for every service whose Ollama-side dependency lives inside the `AddOllamaLocalModelProvider` call that branch skips. There are two. `UnavailableModelCapabilityClient` (`Client.Application`, `Services/Capabilities/Implementation/`) supplies `IModelCapabilityClient`, whose only other registration is in that call; without it `ModelCapabilityProber` cannot be activated. `UnavailableOllamaModelService` (`Services/Chat/Implementation/`) supplies `IOllamaModelService`, which used to be registered unconditionally in `AddNodeWorkerInfrastructureExtensions` even though `OllamaModelService` takes an `IOllamaApiClient` that only exists on the gate-on side — so a gate-off Development host failed `ValidateOnBuild` outright, and any other host threw at the first resolve of `LocalModelCatalogService`, `CapacityService`, `ModelClassificationService`, `ModelFitQueryService`, `OllamaProviderMapBackfillCoordinator`, `GetRunningLocalModelsEndpoint`, `GetLocalModelDetailsEndpoint` or `UnloadLocalModelEndpoint`. The real service is now registered on the gate-on branch beside the provider, so it can never outlive the client it needs.

Both no-ops answer as an absent daemon, not as an error, because that is the shape every consumer already handles. The capability client reports unreachable, no version, empty installed and running inventories and a null context length, so a node started with `XE_OLLAMA_RUNTIME_ENABLED=false` reports exactly what a desktop without an Ollama daemon reports: `OllamaReachable=false`, the `ollama-unreachable` diagnostic, `ManagementMode=unknown`, and the configured-model fallbacks as the installed set. The model service answers its two list probes empty and `IsAvailableAsync` false, and throws a bare `HttpRequestException` with a null `StatusCode` from the members that need a live daemon — the exact transport failure a refused connection produces, which is what those consumers' catches already map to "Ollama not reachable". One nuance on the capability side: `InstalledModelInventoryResult.OllamaQuerySucceeded` is `true` under the no-op, because an empty list is a successful query, whereas a dead daemon throws and yields `false`; `CapabilityReportComposer` ANDs it with the runtime status, so the reported reachability is unchanged either way.

**A gate-off pin routed through `TestServerWebAppFactory` proves nothing about an Ollama-only registration.** The fixture registers a fake `IOllamaApiClient` unconditionally, so the in-process host is never in production shape for this question — which is why the first pass closed the capability client and left the model service open. The production-shape pin is `ServiceProviderValidationTests`, whose `ValidatedHost` builds the real composition root with `ValidateOnBuild` and no fakes. **Prevents:** an operator who merely opted out of a SECONDARY runtime finding the host unbuildable; a test that disables the gate having to substitute the client itself (`RunningLocalModelEndpointTests` carried that workaround and no longer needs it); and a green fixture-based test read as proof that the whole gate-off path composes. **Authority:** `ServiceProviderValidationTests.CompositionRoot_WithTheOllamaRuntimeGateOff_BuildsWithScopeAndBuildValidationEnabled`, `OllamaRuntimeGateStartupTests.Host_WithTheOllamaRuntimeGateOff_BuildsAndReportsTheRuntimeUnreachable` and `OllamaRuntimeGateStartupTests.UnavailableOllamaModelService_ListsNothingAndFailsTransportsLikeAnAbsentDaemon`.

Latent and deliberately not fixed here: `OllamaProviderMapBackfillCoordinator.ListInstalledNamesAsync` is called once outside any `try` and once inside one whose only non-cancellation catch is `InvalidOperationException or IOException`, so a real dead daemon's `HttpRequestException` from that list probe escapes the backfill. The gate-off path does not reach it, because the no-op answers the probe with an empty list rather than throwing.

### A work session is chat turns in a loop, and it takes the node's only invocation slot

- Drive `INodeChatStreamService.SendMessageAsync`, not `IInvocationRunner`, so conversation persistence, approvals, resume, and terminalization remain intact.
- Pause/cancel/deadline must call `INodeChatStreamCancellationRegistry.TryCancel`; merely stopping enumeration leaves the run and node-wide slot alive. Persist terminal writes with `CancellationToken.None`.
- `MaxConcurrentSessions` is admission only; `WorkerEventDispatcher` still serializes invocation node-wide. Bound parks with `MaxParkedSeconds`.
- Before each step, `ConversationStepContextBound` projects history and force-compacts over `StepContextBudgetTokens`; checkpoint compaction is too late and keeps too much.
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

NVFP4 GGUF is recognized/ranked and supported by the pinned llama.cpp; compressed-tensors/ModelOpt NVFP4 safetensors cannot be converted by the current script. Check the container before claiming the format is unsupported. Unknown quant tokens make files disappear, not merely misprice, so add new native formats to parsing/usability together. Use real blob size when available; the 4.25 bpw estimate is empirical.

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


**Required-context sticky hazard:** `_adjustedAllocations` is process-lifetime, monotonically downward, and is not cleared on eject/reservation release. If admission down-tiers below a caller's `RequiredContextTokens`, `TryCommitAdmissionFootprint` pins the smaller context and every later required-window admission fails until app restart, often through the misleading generic “footprint could not be determined” path. Therefore `CapacityService.DecideAsync` rejects instead of down-tiering below a named requirement. The adjustment remains process-scoped, and unknown-footprint failures still share the generic error path.

**Shared benchmark admission:** both `BenchmarkRunExecutor` and `BenchmarkJudgeExecutor` call `BenchmarkCapacityAdmission.AdmitAsync`; neither implements a private retry loop. Default is 24 retries × 5 seconds because the preceding FIFO phase may still be releasing VRAM. Every direct executor unit test injects `new BenchmarkAdmissionRetry(MaxRetries: 0, TimeSpan.Zero)` or a rejection test sleeps two minutes. Re-evaluate capacity each attempt; do not reserve speculative bytes between retries.


The four context values must stay separately named through APIs and DTOs: launched allocation (`-c`), request budget/limit, train-context maximum, and frozen replay override. A request can reduce but cannot enlarge an existing process. Frozen/deterministic allocation never silently mutates after failure; only automatic hardware selection may OOM down-tier, and only for classified startup OOM, at most twice per allocation identity.

### Benchmark cache evidence comes from response timings

**Rule:** llama-server `prompt_n` is evaluated-only and `cache_n` is reused; derive cache reuse as `cache_n / (cache_n + prompt_n)`, never from cold/warm `prompt_tokens_total` deltas.

**Failure prevented:** comparing evaluated-token counts from different prompts falsely reporting no cache reuse when the warm request reused prompt tokens.

**Authority:** pinned llama.cpp commit `8f4646a63ee29f2e0ab971b0290b141938769762` [server-context.cpp](https://github.com/ggml-org/llama.cpp/blob/8f4646a63ee29f2e0ab971b0290b141938769762/tools/server/server-context.cpp) and [server-schema.cpp](https://github.com/ggml-org/llama.cpp/blob/8f4646a63ee29f2e0ab971b0290b141938769762/tools/server/server-schema.cpp); local `LlamaServerGenerationTimings.TryRead`, `InferenceBenchmarkHarness.StreamStageAsync`, and `InferenceBenchmarkHarness.DeriveCacheHitRate`.

### llama-server node settings are captured once per boot — a `PUT` after that changes nothing until a restart

**Rule:** the node settings that feed `llama-server` — `speculativeMode`, `chatCacheReuse`, `kvCacheType` — are captured
when the `LlamaServerSupervisorOptions` singleton is **first resolved** (a lazy factory; the exact moment is not a
reliable "first spawn" marker). A `PUT node-settings` issued after that point in a boot does not reach a later spawn. Restart with
`scripts/dev-stop.sh` then `scripts/dev-start.sh`, and read the values back with a `GET` before measuring anything.

**Failure prevented:** a live A/B that flips one of those knobs between cells inside a single boot, measures no
difference, and concludes "the knob does nothing" — when the second cell ran the first cell's value. Equally: a boot
that silently inherits the previous boot's value because it was never set explicitly.

**Authority:** `BuildSeededLlamaServerSupervisorOptions` and its `AddSingleton(sp => Build…(sp))` lazy-factory
registration in `AddNodeModelRuntimeExtensions`.

### Benchmark launch evidence (KV-cache type feature, 2026-08-16)

- **`LlamaServerLaunchProjection` is the single argv projection**, and `From(...)` is the sole renderer of context/placement/thread argv and of the `ComputeIdentity()` input. Its member order and names are PERSISTED identity — do not change either without an intentional migration.
- Launch receipts are post-readiness, non-throwing, and free of paths/hosts/ports. The receipt reads the projection back from the FINAL argv (last-wins parsing), so a capability gate's omission is observable evidence; freeze-time intent stays separate. Add receipt facts freely as long as they stay outside the persisted projection identity. Aux assets record booleans only — never paths, digests or implied rankability.
- Benchmark placement capture emits `-lv`; serving logs still demote afterwards. `LlamaServerPlacementOutcome.None` means 0/N GPU layers, was APPENDED to preserve ordinal history, and maps to telemetry `none` / benchmark backend `cpu-fallback`.
- `MarkPrimaryLaunchReadyAsync` / `MarkJudgeLaunchReadyAsync` are insert-if-null and accept Running at the claimed version or Cancelled at claimed+1, because terminal cancellation increments exactly once. They use `CancellationToken.None`, log failure, and never change status/version or fail a measurement.
- **A replay uses ONE strict launch candidate** — no safe-KV fallback and no write to the fallback store. Unsupported explicit KV or a CPU-incompatible quantization returns `BenchmarkUnsupportedKvCacheTypeException` / 422. Capacity sizes from the FROZEN runtime context, not the requested sampling context; non-benchmark paths stay f16-conservative. K and V cache types must be ordinal-identical for the fused path.
- **`BenchmarkCanonicalJson` uses `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`** before hashing receipts and environment. Integer ordinals would let an enum insertion silently reinterpret stored evidence.
- **Read stored benchmark blobs with `BenchmarkExecutionSerialization`**, the writer's Web/camelCase options. Default serializer options silently produce ZEROED DTOs rather than throwing.
- **Judge response schemas are bound-free** (`min/maxLength`, `pattern`, item bounds absent) and travel `RuntimePackage.ResponseJsonSchema` → `InvocationAgentDefinition.ResponseJsonSchema` → `ChatOptions.ResponseFormat` — not as a tool, so they bypass tool-schema sanitization. Keep the field null on non-judge requests and OUTSIDE the runtime config hash: it is a per-send decoding constraint.
- **Read llama-server final-chunk timings through catch-based `JsonPatch` field reads.** `Contains` is reliable only for the outer object — a nested value may be readable while it is false — so catch the missing-field `KeyNotFoundException`. Sum EVERY provider request in a turn, including tool and approval loops, and persist `segment_count`. Tokens/sec is decode speed when timings exist, and display-only.
- `RuntimeBundleIdentityCalculator` and its framed hash are shared and persisted; changing the framing invalidates every launch fingerprint.
- **After an EF migration remove/re-add, compare the regenerated migration and snapshot with the immediately preceding designer, and run the migration-chain tests.** Up-only `MigrateAsync` is insufficient when branch timestamps interleave: SQLite down migrations rebuild tables from each migration's own target model and can delete sibling columns. Consolidate unshipped migrations at the merged tail and run the full Persistence project, rollback included.


### Benchmark readiness (repeat groups, launch matrix, export — 2026-08-17; optimization pass 2026-08-25)

- Prompt text is not hashed; every judge-system-prompt wording change bumps `BenchmarkJudgePolicyVersions.PromptVersion`. Reads tolerate old versions; writes/execution validate strictly and UI surfaces outdated status.
- Start a repeat group with one transactional `StartRunsAsync` call and one project-version check: it inserts the whole group, increments the version by N and queues the work items FIFO. Per-run CAS partially creates invisible orphan queued runs. `FreezeCommitGuard` evaluates once per distinct instance inside that transaction.
- Eligible-model listing trusts recorded registry facts without hashing, for responsiveness; `BenchmarkRunFreezeService` re-hashes every member before committing a run. Both map `InstalledGgufSnapshotException` to the declared 422 eligibility error, so one bad model in a launch matrix does not 500 and abort the later cells.
- Rank exclusion is projected onto EVERY API path that returns runs, not only the ranking query, and `TotalScored` uses the same rankable verdict for its denominator so a permanently truncated warm-up does not leave the UI waiting forever. Truncation recognition is shared between the ranking read and judge execution: adding a stop-reason token to one copy only produces inconsistent verdicts. Task-suite exclusions extend the precedence; see the task-suite section.
- Freeze `invocation_timeout_seconds` onto the run; the run copy is authoritative. Legacy null defaults to 900 s.
- Sum timings across every request in the turn and store `segment_count`. Keep server-side list projection in sync with single reads/CSV/repeats.
- Legacy registry SHA text may be uppercase — compare case-insensitively. An unmapped provider defaults to `llamacpp`.
- Benchmark-readiness schema is the consolidated `AddBenchmarkReadiness` migration; the five branch-local feature migrations were deleted, so do not search for their names.
- CSV fields beginning with formula-significant characters (`= + - @ tab CR`) receive an apostrophe inside quotes.
- A member added to `BenchmarkSamplingSnapshotV1` must be nullable and `[JsonIgnore(WhenWritingNull)]`. The payload is validated by re-hashing it against its embedded `ConfigurationHash`, so a member that emits `null` changes the bytes of every row frozen before it existed and each of those runs stops replaying. `BenchmarkRuntimeSnapshotV1CompatibilityTests` holds the literal v1 payload and is the guard.
- Two stop reasons are node-DERIVED, not provider tokens: `incomplete` (a clean finish that answered nothing — ended on an unanswered tool call, or emitted only reasoning) and `reasoning-length` (`length` with no visible answer token). Both are decided in `BenchmarkRunExecutor.ResolveStopReason` from the COALESCED parts; a per-delta capture cannot show the shape of a turn. `IsTruncated` covers `reasoning-length` deliberately. Precedence is warm-up > user score > (truncated | incomplete) > judge reasons.
- `BenchmarkFreezeScope` memoizes the llama-server probe, the chosen variant, and the verified model lease for ONE launch request; the batch endpoint threads one through every cell, the single-run endpoint passes none. Holding the leases also keeps a matrix comparable. Under it `InstalledGgufSnapshotStore` memoizes each member's SHA-256 by (absolute path, length, last-write UTC); a member rewritten with both preserved is not re-detected for the process lifetime.
- `StartBenchmarkRunBatchEndpoint.RequestTimeBudget` (45 s) is checked BETWEEN cells, never inside one, so a started cell runs to completion and no cell is half-frozen; every remaining cell returns named with code `BatchTimeBudget` plus the project version to resubmit against. It reads the injected `TimeProvider` — a test swapping the host's must start it at `DateTimeOffset.UtcNow`, because the host mints that request's bearer token off the same clock.
- `BenchmarkRepeatMode.AnswerVariance` advances the seed by repeat index off one base and samples at a temperature (default 0.7); `Throughput` stays the default and byte-identical to what it always was. Both knobs are SAMPLING and must never reach the launch arguments, or the runs of one group stop sharing a launch identity. Mode, seed and temperature are duplicated onto the run as plaintext columns because listing and CSV never decrypt a snapshot.
- The event buffer keeps a tombstone per terminal run — an emptied entry still turns a late `Subscribe` replay into a reset rather than silence. Past `MaxRetainedTerminalRuns` (256) the oldest drop and the hub resets off the persisted `LastStreamSequence` instead. Queue membership is its OWN flag: a run is evicted once per terminal PHASE and has two, so keying the queue off the eviction flag enqueued each run twice and halved the effective cap.
- `PUT projects/{id}/judge` answers the re-judge precondition BEFORE building the policy, because building it takes the VERIFYING model lease that re-hashes every member file — 57 s for a 22 GB judge. An unchanged draft is recognized without a lease by rebuilding it against the STORED model identity and comparing `ComputePolicyHash`, with the model NAME compared separately. Ceiling: a judge file changed on disk under an unchanged name reads as unchanged until the policy is next actually built.
- `samplingTemperature` is `double` end to end — both start-request DTOs, `BenchmarkSamplingSnapshotV1.Temperature`, and the freeze service's constants. A float widened into the already-`double` run column recorded 0.699999988079071 for 0.7. The sampler narrows back to float at `SamplingOptions`, which is what the wire carries; serialized snapshot bytes are unchanged, so nothing re-hashes.


### Benchmark quant fidelity — perplexity and KL divergence (2026-08-26)

- **`llama-perplexity`'s real output disagrees with its README in three ways, and each reads as "unparseable" rather than as an error.** Captured from b10201: a `--kl-divergence` run prints **no `Final estimate` line at all** — its perplexity is `Mean PPL(Q)` inside the statistics block; the statistics blocks separate a value from its error with `±` (U+00B1) while the plain perplexity line uses `+/-`; and top-token agreement is printed as **`Same top p`**, with the word "agreement" appearing nowhere. `BenchmarkPerplexityOutputParser` holds verbatim fixtures of both shapes. Write a parser against the binary, never against the docs.
- Perplexity is measured at a **fixed 512-token window**, never the project's context. Everything else about the run's placement (`--n-gpu-layers`, `--tensor-split`, `--override-tensor`, `--cache-type-k/-v`, `--flash-attn`) IS replayed — that is what differs between the runs being compared. b10201's `llama-perplexity` **does** accept `-ctk`/`-ctv`, so a KV-quantized run is measured under its own KV and needs no f16 fallback.
- **The KLD disk estimate is measured, not derived.** A real 10-chunk base file for Qwen3.8-27B (`n_vocab` 151 936) was 1 266 472 900 bytes over 777 912 320 logits — **1.628 B/logit**, not the format's 2.0. `BenchmarkFidelityPolicy.KldBytesPerLogit` is 1.75 (headroom over one measurement); 200 chunks is ~25.3 GB actual, not the 31.1 GB an f16 assumption promises.
- **A `v1:<hex>` fingerprint is not a filename.** `:` is illegal in a Windows path, and NTFS reinterprets the tail as an alternate data stream rather than rejecting it — the failure would be a silently empty file. The base-logit cache is named by the first 32 hex of a digest OF the whole cache key, with a plaintext `.json` sidecar so the directory stays human-readable.
- **The KLD comparability gate is the whole cache key, not the base model's fingerprint.** Four of the key's five inputs (corpus sha, context tokens, chunk count, format version) move without the fingerprint moving, and `kld_p99` is strongly chunk-count dependent. `BenchmarkKldCacheKey` is the ONLY place that expression exists — `KldDigest_IsComputedByExactlyOneExpression` fails the build if a second file computes it. A stale figure is **withheld** by the API, not sent flagged: a number a reader can still see is a number they will still compare.
- The cache lease is `FileMode.CreateNew` + `FileOptions.DeleteOnClose`. That is what makes the CRASH case right — the OS drops the handle, so the next run takes over instead of waiting on a lock nobody will release. A bare "does the lock file exist" check gets exactly that case wrong. The write goes to `<name>.tmp.<invocationId>` and lands by same-directory rename, so a partial logit file never resolves as a measurement.
- **A new `BenchmarkWorkKind` is not additive at the lifecycle layer.** `ClaimNextAsync` and `RecoverRunsOnStartupAsync` both branched "Primary, else it is a Judge" and the else dereferenced `JudgeAttemptId`; either new kind reaching them threw `InvalidJudgeTransition` and stalled the single-consumer queue behind an item it could never claim. Both are four-arm switches now, and recovery needs **two sibling pre-sweeps** — an attempt or comparison left `Running` by a killed process whose work item a previous partial recovery already terminalized has nothing else that can reach it.
- **EF cannot express the `COALESCE(task_case_id, x'00')` unique indexes.** `HasIndex().HasFilter()` takes columns, not expressions, so it emits an index on the bare nullable column — and SQLite lets a unique index repeat NULLs, which is the hole the COALESCE closes. The three are raw `migrationBuilder.Sql(...)` and are deliberately absent from `OnModelCreating`; the migration test reads them back from `sqlite_master`, because an assertion through the EF model passes against exactly the index that is wrong.
- The work-item CHECK rewrite is a SQLite **table rebuild** (SQLite cannot ALTER a constraint). EF's `DropCheckConstraint`/`AddCheckConstraint` do produce one, and it carries `queue_sequence` values through — asserted, because that column is a FIFO position and renumbering it silently reorders pending work.
- Fidelity is measured **once per cell**, never per repeat: perplexity is deterministic given the same weights and argv, so N repeats buy N identical numbers at N× the cost. A warm-up records `fidelity_status = 'skipped'` rather than NULL — "covered by repeat 1" and "never asked for" are different facts.
- **Fidelity is seeded on primary SUCCESS, never at freeze — same rule as the judge attempt.** Freeze inserted the attempt and its work item beside the run, so a primary that then failed or was cancelled left hours of GPU work queued against a run with no answer, and the single-consumer queue dutifully measured a corpse. `MarkPrimarySucceededAsync` seeds it inside the transaction that terminalizes the primary; freeze keeps writing only the `skipped` marker for the cells it will never measure, and the per-cell predicate is ONE `IsFidelityMeasuredCell` shared by freeze, the seed and `EnqueueMissingFidelityAsync`.
- **The run's `fidelity_status` projection must follow its attempt through EVERY transition, not only the terminal ones.** Claiming updated only the attempt, so a measurement that runs for hours read `queued` on every API and every UI poll; crash recovery failed the attempt and the work item and left the projection `queued` with nothing behind it, so the run reported an active measurement forever, polling never stopped and re-measure stayed disabled. Recovery writes the status in the sibling attempt SWEEP rather than in the work-item pass — that is the one place every interrupted attempt passes through, including one a previous partial recovery orphaned. The NUMBERS stay put either way: the projection still points at the last attempt that actually succeeded.
- **The 2 h measurement watchdog is a FAILED measurement, not a cancellation.** Its linked token throws the same `OperationCanceledException` an operator's stop does, and the executor's cancel arm recorded it with no reason at all. Both perplexity passes run through one helper that owns the watchdog and classifies at mapping time in the repo's priority order (see the `CancellationToken.Register` rule above): the caller's token is not cancelled, so by elimination the timer is ours. The timeout is a constructor parameter with a default, which is how a test hands it a process that never returns.
- Live on a dev host (RTX 5090, b10201 CUDA, the executor's own argv: `-c 512 --chunks 200 --n-gpu-layers 99 --flash-attn off`): `unsloth/Qwen3.8-27B-GGUF` Q4_K_M **6.7977 ± 0.07405** vs UD-Q3_K_XL **6.9497 ± 0.07550** — separated, with the bands ending 0.002 apart and NOT overlapping. At the 50-chunk floor the errors roughly double and the two would overlap, which is why the PPL default is 200 even though KLD's is lower. (`-ngl 999` with flash-attn on auto gives 6.7983 / 6.9513 — the placement knobs move the number in the fourth decimal, which is exactly why they are replayed rather than assumed.)
- An existing `~/cuda-llama/<tag>/build/bin` may predate the fidelity executable, so `llama-perplexity` is added incrementally without rebuilding anything else: `source ~/cuda-llama/env.sh && cmake --build ~/cuda-llama/<tag>/build --config Release --target llama-perplexity -j$(nproc)`.
- **A KLD measurement runs TWO models in sequence, so it takes two sequential capacity reservations.** One reservation sized from `snapshot.PrimaryModel` — the quant — was held across `PrepareKldAsync`, which loads the BASE model, routinely the larger of the two: an over-admission that OOMs on exactly the box where the base is big. `PrepareKldAsync` now admits the base itself (`PublishLaunchAdmission: false`) and releases when it returns, after the logit file is published; the quant pass is admitted afterwards. The early returns — a published base file, or a lease another process holds — load nothing and therefore reserve nothing.
- **A fidelity work item pins `attempt = 1`, so "failed" is literally terminal and there is no retry behind a message that promises one.** A base-logit lease held by another process used to throw `BenchmarkExecutionException` → `MarkFidelityFailedAsync`, losing the measurement until someone re-measured by hand. It now waits for the other writer's file on the `BenchmarkAdmissionRetry` cadence (the same shape a capacity rejection waits on — a transient blocker somebody else is clearing) and then calls `IBenchmarkStore.RequeueFidelityAsync`, which returns work item AND attempt to `Queued` with the reason attached. The wait is also the rate limiter: a requeued item cannot spin faster than one pass per budget even when it is alone in the queue. The requeue is a no-op once the attempt is terminal.
- **`AppendFidelityWorkAsync` owns the run's fidelity projection; a caller must not write it again.** The `measureExisting` loop set `FidelityStatus` back to null right after the helper had set it to `queued`, so every cell it queued read as "fidelity was never asked for" until terminalization — the exact distinction `skipped` versus NULL exists to preserve elsewhere in the same method.

### Benchmark verifiable rubric criteria (2026-08-26)

- **A rubric criterion carries a `Kind` and a `Config`, and both are inside `ComputePolicyHash`.** `llm` is the legacy-compatible default for an absent value, preserving the meaning and stored hash of criteria without those fields. The verifiable kinds — `exact`, `regex`, `jsonSchema`, `mathAnswer`, `constraint` — are decided by `BenchmarkJudgeVerifiers` with no model in the loop. `pythonTests` is the sixth verifiable kind and the only one that EXECUTES anything — it is also the only one that can be unscorable rather than merely failed, so it is routed away from the pure `BenchmarkJudgeVerifiers` by `IsExecutionVerified`.
- **`verified:v1` is safe only because the mode, every criterion kind and every criterion config are in the policy hash.** A judging whose whole rubric was verified server-side spawns nothing, so it has no runtime to key on — and having none is NOT `execution-identity-incomplete`, which would unrank it forever. The constant key works because one policy revision is provably one rubric composition. Move any of those three out of the hash and the sentinel starts merging attempts that were graded differently. `MarkJudgeSucceededAsync` applies it with `??=`, so a measured key written at launch is never overwritten.
- **A mixed rubric must be parsed against a FILTERED rubric.** `BenchmarkJudgeResultParser.ReadCriteria` rejects a reply whose criteria array does not match the rubric's count exactly, so handing the model the full rubric and expecting it to skip the verified ones fails every judging. The model sees only its own criteria; the verified scores (10 or 0) are merged back and `BenchmarkJudgeScoreCalculator.Compute` recomputes the 0..100 against the full rubric — which also rejects a merge that does not cover it, so the merge is checked rather than trusted.
- **Adding a member to `BenchmarkJudgeRubricCriterionV1` changes the judge's PROMPT unless you stop it.** `BuildUserPayloadJson` serializes the rubric with `DefaultIgnoreCondition.Never`, so `kind`/`config` would appear in the payload of every judging — including a re-judge under a revision stored before they existed, which is a different question asked with no version moving. The builder strips both; the model never sees a verifiable criterion anyway.
- **Every regex a policy carries is compiled `RegexOptions.NonBacktracking`, and a pattern it refuses to compile is refused at activation.** That is the whole ReDoS answer: matching is linear in the input, and backreferences, lookaround and atomic groups are rejected while the operator is still looking at the form. Do not add a backtracking fallback — the timeout is belt, not the mechanism. Same reason the fenced-JSON scanner in `BenchmarkJudgeVerifiers` is hand-written: finding a fence needs a negative lookahead.
- **The JSON-schema check enforces a subset (`type`, `properties`, `required`, `items`, `enum`, `const`, `additionalProperties`) and REFUSES every other keyword at activation.** Accepting `minLength` and not enforcing it would ship a criterion that silently passes answers it should fail; the refusal keeps "accepted" and "enforced" the same set. No JSON-Schema dependency is used.
- **A verifier that cannot run throws and fails the judging; it never scores 0.** 0 is a score an answer can genuinely earn, so spelling "unmeasurable" that way is a lie the ranking then acts on.
- `mathAnswer` extraction order is `\boxed{}` → `####` → an "answer is" phrase → the last number, most explicit first, and the last occurrence of each. A model that shows its working leaves several numbers behind and the final one it wrote is not reliably the one it meant. The value keeps commas (thousands separators) and is cleaned afterwards; excluding them from the capture read `$1,234,567` as 1. `\boxed{1/2}` and `\boxed{\frac{1}{2}}` both equal `0.5`.
- Live run: an all-verifiable rubric (mathAnswer + regex + exact) on Qwen3.8-27B Q4_K_M judged **score 100**, `executionKey verified:v1`, with **exactly one** `llama-server ready for model` line in the whole session — the primary — and zero mentions of the judge model. A mixed rubric with a Qwen3-32B Q5_K_M judge merged the model's clarity 10 and the verifier's 10 to **100**, and the same pair with a deliberately wrong expected value to **50** — `(50·10·10 + 50·0·10)/100`, the weighted merge asserted end to end.

### Benchmark pairwise judging and Bradley-Terry (2026-08-26)

- **A pairwise score lives in ONE `BenchmarkPairwiseFit` row and nowhere else.** `BenchmarkRun` carries no pairwise columns and an architecture test keeps it that way. Publishing a fit by writing a score onto each run let a crash between run four and run five leave a ranking blended from two fits, every row internally consistent and the ordering wrong, with nothing able to detect it. Publication is one transaction — deactivate the scope's active row, insert the new one — and `ux_benchmark_pairwise_fits_active` makes two active fits per `(revision, generation, case)` unrepresentable rather than merely unlikely.
- **Staleness is ONE integer, and the ranking read opens no comparison row.** `BenchmarkJudgePolicyRevision.ComparisonSetVersion` is bumped in the same transaction that inserts a comparison or terminalizes one — the only two ways the fitted set can change — and a fit stamps the value it was fit at. It is strictly stronger than hashing the verdicts: a cancel-then-re-enqueue that lands on identical verdicts still bumps it, so a fit over a REBUILT set is stale. `FittedSetJson` stays as the audit record of which verdicts were used; the hash was never what made that answerable.
- **`ComparisonSetVersion > 0` on the current revision is also how the read knows the project judges pairwise.** It leaves zero on the revision's first comparison and never returns, and switching modes changes the policy hash, which mints a different revision starting at zero again. The mode itself lives inside the encrypted policy blob, and decrypting one per page fetch to answer "which vocabulary does this project rank in" was not worth it. Consequence worth knowing: a pairwise project with fewer than two eligible runs never enqueues a comparison, so it reads on the POINTWISE path until it has a pair.
- **`pairwise_fit_key` covers the judge execution identity, not just what was asked.** Revision, generation, policy hash, both pairwise versions, the case AND the cohort's promoted `ReferenceExecutionKey`. A cohort's verdicts can come from two judge runtimes (a llama.cpp upgrade, a different quant of the judge, a KV type changed mid-cohort), and pointwise already refuses to rank across exactly that boundary. Before fitting, the fitter requires a promoted reference key and requires EVERY fitted comparison to carry it; a single mismatch refuses the whole fit and **no partial fit over the matching subset is attempted**, because dropping comparisons changes the comparison graph and can disconnect it. Recovery is the existing mechanism: re-judge, which bumps the generation and re-enqueues.
- **A comparison claim must not touch either run's version.** Every per-run work kind bumps `BenchmarkRun.Version` so a poller sees that something changed, and the comparison arm of `ClaimNextAsync` fell through to that common bump. A comparison names two runs and its work item names only the canonical first, so every pairwise claim silently invalidated that run's CAS token — scoring, deleting or re-measuring it returned `VersionConflict` throughout a tournament, while the other run of the pair never heard about the claim anyway. The fit's own publication is what refreshes a pairwise reader.
- **A refusal is PUBLISHED as a fit row with the reason and no scores.** A fit that simply fails to appear is indistinguishable, on the read path, from a cohort still judging — and telling those apart without re-reading every verdict per page is the whole point of the one-row design. So `ScoresJson` carries one entry per ELIGIBLE run (not per fitted run) with an optional `pairwise-*` reason, and a whole-fit refusal puts the same reason on every entry. Note the shipped CHECK requires `iterations > 0 AND bootstrap_replicates > 0`, so a refusal records the sweep budget it exhausted rather than a zero the row cannot hold.
- **A comparison claims the cohort's reference execution key on its first success.** A pairwise cohort has no judge attempts to claim it, so without this every fit over it refuses as `pairwise-execution-identity-incomplete` and nothing is ever rankable. It happens inside the same transaction as the terminalization, exactly as `MarkJudgeSucceededAsync` does it.
- **Bradley-Terry is regularized with a symmetric alpha = 0.5 pseudo-count per COMPARED pair.** Complete separation — a run that wins everything, or loses everything — is routine with three runs and one clear winner, and the unregularized MLE does not exist there at all. With the prior every participating run has a win total above zero, so no `log(0)` and no division by zero is reachable. Ties split 0.5/0.5; Rao-Kupper is declined on purpose (a third parameter from far fewer observations than a 12-run cohort produces). The MM sweep converges below 1e-10 on log-strengths, capped at 500, and **hitting the cap refuses the fit** rather than publishing a half-converged number. Measured: a four-run fixture converges in 62 sweeps; an earlier estimate below 60 was not the observed result.
- **The mapping is `round(100 * sigma(theta_i - mean theta))`, not `100 * p / max(p)`.** The latter pins the winner at 100 forever and reintroduces exactly the saturation the pairwise mode exists to remove. A two-run cohort where A wins both orders is 69 / 31 — hand-computable from the prior, and pinned as a fixture.
- **The bootstrap resamples whole UNORDERED PAIRS, never individual verdicts.** The two presentation orders of one pair are the same observation measured twice to cancel position bias; splitting them across replicates puts that bias straight back into the interval and understates it. A run absent from a replicate contributes nothing (not a 0, not a 50), and one appearing in fewer than 200 of 1000 replicates gets NO interval rather than a fragile one. Seeded at 0, so two runs over the same verdicts report the same interval.
- **Only the largest connected component of the comparison graph is published**; runs in the others read `pairwise-insufficient`. Strengths from two components are not on one scale and averaging them invents an ordering the verdicts never established.
- **Each answer is bounded to HALF the judge window, and the cut is recorded.** `BenchmarkOutputParts.ForJudge` already halves internally, so the pairwise call passes `window / 2`; truncation is read off the marker it appends rather than re-derived from lengths, so the flag and the text the judge saw cannot disagree. A long answer is therefore cut harder here than pointwise — itself a bias — so a cohort with more than 20 % of verdicts carrying a truncated side refuses to aggregate.
- **The swap bookkeeping lives in exactly one place.** `BenchmarkPairwiseResultParser.ToCanonicalVerdict`: with `Order = 1` the canonical B was shown first, so a verdict of `a` means B won. Getting it backwards inverts exactly half of every cohort's verdicts, which is precisely the half the swap exists to produce.
- **`EnsurePairsAsync` is called from exactly three places** — judge-policy activation, a primary's success terminalization, and startup reconciliation — and it is transactional, idempotent and unique-violation-tolerant. A terminal-FAILED slot is free again (that is what the status-filtered live-slot index is for) and its retry is a new row at `attempt_sequence + 1`. The cap is 12 eligible runs, i.e. 132 judge calls; past it nothing new is paired and the excess runs read `pairwise-cap`, because a sampled sub-tournament would be a silently biased one.
- The work item of a comparison names `RunAId`, because a comparison names TWO runs and "the run's comparison work item" is not a well-formed lookup. Every comparison lifecycle call is keyed by queue sequence for that reason.
- **A comparison references TWO runs and foreign keys are OFF, so anything that touches one run must ask the comparison rows themselves.** `DeleteRunAsync` guarded and deleted by `BenchmarkWorkItem.RunId` alone, which is the canonical FIRST run: deleting the B side of a live pair walked past the active-work guard entirely, and deleting either participant left comparison rows naming a run that no longer exists plus a published fit ranking it — with no FK to complain. The guard and the delete both go through `BenchmarkComparisons` on `RunAId == id || RunBId == id` now, and the delete bumps each affected revision's `ComparisonSetVersion` (which makes a surviving fit read stale) *and* deactivates the project's active fits (which makes the next planner pass re-fit the cohort that is actually left). Every run-cleanup path must preserve both halves. The same delete had also never removed `benchmark_fidelity_attempts`, which carry an encrypted receipt — with FKs off that is a leak, not untidiness, so the explicit order is now comparisons → work items → judge attempts → fidelity attempts → run.
- **Live on a dev host (RTX 5090, b10201 CUDA, Qwen3-32B Q5_K_M judge at ctx 8192, four quants of Qwen3.8-27B): the pointwise judge scored ALL FOUR 100 and dense-ranked them all rank 1 — a four-way tie and zero discrimination — while the pairwise fit over the same four answers produced UD-Q3_K_XL 69 [56, 90], Q5_K_M 60 [46, 77], Q6_K 51 [23, 70], Q4_K_M 23 [9, 34].** A strict total order where pointwise had none; the ordering differs on every pair, not just one. Q4_K_M won zero of its six comparisons and still scored a finite 23 — that is the alpha = 0.5 prior, observed rather than argued. The fit converged in 42 sweeps; every run appeared in 974-990 of 1000 bootstrap replicates.
- **The position swap earns its cost on real answers.** Two of the six pairs split 1-1 across the two presentation orders (Q5_K_M vs UD-Q3_K_XL, Q5_K_M vs Q6_K): shown one way the judge preferred one answer, shown the other way it preferred the other. Judging each pair once would have recorded whichever order happened to run.
- **Measured: 28.1-35.3 s per comparison (mean ~30.7 s), 12 calls, ~369 s of judge time.** The pre-flight estimate said 366.9 s from the project's own median judge attempt before a single comparison ran. Budget accordingly: a 12-run cohort is 132 calls, roughly 68 minutes at this rate.
- **Kill/restart, verified live mid-cohort.** With four comparisons succeeded and one Running, `scripts/dev-stop.sh` left exactly that row `Running` with its work item `Running` — the state that stalls the single-consumer queue forever. After `dev-start.sh`: the interrupted comparison terminalized `Failed`, `ReconcilePairwiseAsync` re-enqueued that same slot at `attempt_sequence = 2` (13 comparison rows for a 12-slot cohort), the queue resumed, and the cohort finished and published. A re-judge then bumped the generation, re-enqueued all 12, and published a second fit — with exactly ONE active row per generation and the previous one kept on disk as history, no longer in scope.
- **NOT verified live: a kill timed inside the publication itself.** It is a single sub-second transaction, so landing a kill in that window is not reproducible by hand. What is verified is the invariant that makes the window harmless: `ux_benchmark_pairwise_fits_active` is a filtered UNIQUE index, so two active fits in one scope cannot exist whatever the publisher does, and the transaction either commits both the deactivate and the insert or neither.
- Watch for this when driving a live cohort: `rankExclusionReason` is a TOP-LEVEL field of the run DTO, not a member of its `judge` object. Reading it off `judge` returns null for every run and makes a working exclusion look broken.
- **A pairwise cohort holds comparisons and NO pointwise attempts.** Activation and project re-judge both ran before the planner enqueued a single comparison, and both handed the store a cohort seed carrying a resolved judge runtime — so `EnqueueCohortAttemptsAsync` queued one pointwise judging of every succeeded run, and the cohort then held both kinds of judging of the same runs at once. `BenchmarkJudgeAttemptSeed.SeedPointwiseAttempts` is what the two modes differ by; `BenchmarkProjectService.BuildCohortSeedAsync` clears it for a pairwise policy and skips resolving the runtime at all (the planner resolves it once for the cohort, and the verifying lease re-hashes every member file). The seed is still passed, because it is what pins `ExpectedJudgePolicyRevisionId` and rolls a straddled re-judge back. Switching BACK to pointwise mints a different revision — the mode is inside the policy hash — so attempts are seeded again and `EnsurePairsAsync` is a no-op for it. (An earlier note here said the extra attempts were left in deliberately, to make a pointwise-vs-pairwise comparison possible on one project; that was measurement scaffolding, and it is gone.)

### Benchmark export (2026-08-26)

- **CSV columns are APPENDED, never inserted.** The export is flat *because* consumers read it by column index; inserting a column silently turns a sampling seed into a token count and nothing errors. `BenchmarkExportProjection.SchemaVersion` is bumped with every column change (now 3), and the snapshot test pins the whole header line plus one whole row — including the trailing empty cells, because a SHORT row is what actually breaks an index reader.
- **The export must apply the SAME comparability gate as the live read, and it did not.** `ToDetail` takes the expected KLD digest as an optional argument, so the export called it without one, a null expected digest matched nothing, and every exported KLD figure came out `kldState=stale` with its numbers nulled — a download that silently disagreed with the page it was downloaded from. Any new caller of `ToDetail`/`ToSummary`/`ToFidelity` has to pass `BenchmarkEndpointSupport.ExpectedKldDigest(project)`.
- **Fidelity numbers need more decimals than throughput does.** The CSV's `Rate` helper formats `0.###`, which rounds a perplexity of 6.7977 to 6.798 — and the measured Q4_K_M/UD-Q3_K_XL gap is 6.7977 vs 6.9497 at standard errors near 0.074, so three decimals discards exactly the digits that decide whether two quants separate. Fidelity uses a six-decimal formatter.
- **A withheld figure still exports its digest.** The three KLD cells go empty on a stale row, but `kldBaseLogitsDigest` is written anyway: it is the evidence for the withholding, and a reader comparing it against the project's current digest can see what moved.
- **A fit is exported as ONE object, not smeared over the runs.** `BenchmarkPairwiseFit` is a single immutable row whose `FitKey` covers the whole comparison set; per-run strengths stay on the run rows where every other score already is. The CSV, having nowhere else to put it, repeats the fit key on every row of the cohort so a filtered subset does not lose which fit its numbers came from.

### Benchmark task items (2026-08-26)

- **A project holds 1..N task items and a single item is the degenerate case.** Everything is additive on the one-item path: a pre-existing project freezes, ranks and exports exactly as before. The migration test that proves it compares the project and run rows byte-for-byte across the migration.
- **A project's items are created in the SAME transaction as the project.** `IBenchmarkStore.CreateProjectAsync` takes the initial item set; `BenchmarkProjectService.CreateAsync` always passes item 0. `GetOrCreateItemsAsync` survives ONLY as the legacy backfill, and exactly ONE endpoint may call it (`GET benchmarks/projects/{id}/items`) — every other project read lists rather than get-or-creates, so a page refresh cannot race two item-0 rows into existence. A migration cannot do the backfill at all: it runs without the node encryption key and `prompt_json` is a required encrypted blob AAD-bound to its own item's id.
- **The lazy backfill deliberately leaves `benchmark_projects.task_item_set_hash` NULL.** Materializing item 0 changes nothing about what the project asks; moving the hash that every historical run is compared against would unrank a whole project's history for a bookkeeping write. It moves on the first real item edit, where unranking is the correct answer.
- **`cell_key`, `task_input_hash` and `task_item_set_hash` are NOT NULL on `benchmark_runs`.** A nullable `cell_key` puts every ungrouped run of a project into one anonymous bucket and averages their scores together, silently. The migration backfills existing rows to `'run:' || id` — a derived, plaintext value, which is the ONLY kind of backfill a migration may do — so every legacy run is its own singleton cell and ranks exactly as before. The two hash columns default to `'v1:legacy'`, which is also what a legacy run is compared AGAINST on both axes, so it is never `item-revised` or `item-set-revised`.
- **The item-set hash is `Id`-ordered, not `Index`-ordered.** Adding or deleting an item changes which questions the project asks and must move it; reordering changes no question and must not, or a cosmetic drag-and-drop unranks a completed suite. Consequently a reorder bumps no revision, moves no hash and resets no cohort — it is a two-pass renumber (into a disjoint index range, then into place) because the unique `(project_id, index)` index is enforced per statement.
- **A moved set hash resets the rank cohort**, through the same `ResetCurrentCohortAsync` path a judge-policy activation uses, and bumps the project version. The project score is a mean over the item set, so a different set is a different score.
- **Item writes are refused while any of the project's work is Queued/Running.** That is the primary guard; the run stamps are the safety net for completed history. An item's kind cannot be changed in place either — that is a different item wearing the old identity, so it is delete-and-create, where the set hash moves for it.
- **An absent optional payload is NULL, never an empty blob**, normalized in the store. The C# trap that produced empty blobs: `cond ? null : someByteArray` assigned to a `ReadOnlyMemory<byte>?` has natural type `byte[]?`, and a null array converts to an EMPTY `ReadOnlyMemory`, not to a null nullable — so the omitted payload arrived as present-and-empty. Cast the non-null arm to `(ReadOnlyMemory<byte>?)`. It matters because the payload bytes are inside the item's input hash, where an empty-vs-absent difference makes an untouched item look edited.
- **`BenchmarkTaskItemHashing` is in Persistence and does NOT use `BenchmarkCanonicalJson`** — that lives in the application layer, which Persistence cannot reference. It is a length-prefixed `IncrementalHash` feed with unit/record separators, the same shape as `TrainingDatasetStore`'s content fingerprint. Length prefixes are load-bearing: without them a payload containing a separator byte could be read as a different arrangement of fields.
- **`benchmark_task_items.index` is a SQLite keyword**, so its CHECK constraint quotes it (`"index" >= 0`). EF quotes it in generated SQL; hand-written test SQL must too.
- The kind CHECK admits the whole vocabulary (`prompt`, `niah`, `niahCase`) even though only `prompt` can be written today — a CHECK rewrite is a SQLite table rebuild, and admitting the vocabulary up front costs nothing while the SERVICE is what refuses the kinds this build cannot execute.
- **A shared worktree means a shared git index, and `pnpm run openapi:check` diffs the worktree against the INDEX.** After committing through a private `GIT_INDEX_FILE`, HEAD moves but the shared index does not, so the check reports drift on a tree that exactly matches HEAD. Fast-forward the shared index (`git read-tree HEAD`) after such a commit, but only after confirming `git diff --cached <old-head>` is empty so a teammate's staged work is not discarded.

### Benchmark task suites: freeze fan-out and cell ranking (2026-08-26)

- **A freeze fans out over LEAF task items, and the snapshot cache is keyed on `(itemId, seedValue)` — not on the seed alone.** Answer-variance repeats already require a per-seed dictionary; suite fan-out also requires the item id in that key. Without it, every item receives the FIRST item's serialized snapshot: every run answers item 0's prompt while its `task_item_id` column claims otherwise, and nothing fails loudly. `Start_WithThreeItems_FreezesThreeDistinctCoreTasks` is the guard.
- **The agent runtime is resolved ONCE PER ITEM; the binary probe and the variant selection stay once per freeze.** The task text is `IAgentDefinitionResolver.ResolveAsync`'s `retrievalQuery`, so the resolved system prompt and the skills behind it can differ per item, and the dependency set that guards the commit is derived from that resolution — hence one `FreezeCommitGuard` per item (the store de-duplicates guards by reference). A second binary inspection could straddle a runtime swap and freeze two different launch answers into one measurement.
- **A CELL exists whenever one freeze produces more than one run per model, which is NOT the same condition as a repeat group.** `BenchmarkRunFreezeService` mints `repeatGroupId` only when `repeatCount > 1 || warmup`, so a 3-item single-repeat suite — the ordinary way an operator runs one — had NULL in that column, fell back to the singleton `cell:<runId>`, and produced three cells each missing two of three items: every cell `item-incomplete`, the project ranking nothing. The rule is `cellGroupId = repeatGroupId ?? (leafItems.Count > 1 ? Guid.NewGuid() : null)` and `cell_key = 'cell:' || cellGroupId || ':' || (repeatIndex ?? 1)`. Where a repeat group exists the cell group IS that GUID, so there is one identity and nothing to keep in sync, and `repeat_group_id` / `repeat_index` keep their exact previous semantics — assert they stay NULL on a multi-item single-repeat freeze.
- **Commands are built repeat-major, item-minor.** A partially drained queue then yields whole comparable cells rather than one item spread across every cell.
- **Warm-ups are stamped with a cell key and then dropped BEFORE grouping.** A warm-up sits at repeat index 0 and so forms its own cell, which could only ever be complete if every leaf item also got a warm-up run — and would otherwise sit in the ranked denominator forever. Stamping an identity is not a ranking decision; the ranking read is where the exclusion belongs, exactly as it already is at run granularity.
- **Fidelity is queued once per CELL, not once per run.** Perplexity and KL divergence measure the model file against a corpus, not the task, so before this every item of a cell would have queued an identical measurement — N times the GPU hours and, for KLD, N times ~25 GB of base logits, to produce N copies of one number. The rule now has an item half (lowest `task_item_index` in the cell) as well as the repeat half, and it is expressed THREE times that must stay in step: over the in-memory batch in `StartRunsAsync` (those rows are not saved yet), as `IsFidelityMeasuredCellAsync` on primary success, and as the EF predicate in `EnqueueMissingFidelityAsync`.
- **The unit that ranks is a cell, and a cell ranks only when every scorable item in it produced a rankable score.** Partial credit is refused for the reason a truncated run is already refused outright: scored on the easy items alone, a model that ran out of budget on the hard one outranks one that attempted everything. `BenchmarkRankCohort.RankedCount`/`TotalScored` count CELLS. A single-item project has one run per cell and every number is identical to what it always was.
- **`item-revised` and `item-set-revised` sit ABOVE the operator's user-score override; truncation still sits below it.** `BenchmarkStore.ApplyRunExclusions` had one line where `userScore is not null` suppressed every stop-reason exclusion. That is right for truncation — an operator who read a truncated answer and scored it anyway overruled the machine about a fact they could see — and wrong for a revised item or a revised item SET, which the operator has no way to notice. Precedence is now warm-up > item-revised > item-set-revised > user score > truncated/incomplete > judge state.
- **The set-hash check runs BEFORE the completeness check, and it is the only thing that catches a DELETE.** Completeness is judged against a mutable set: delete the item a cell never answered and its two survivors keep matching their own input hashes, satisfy every per-item check, and constitute a *complete* two-of-two cell whose mean is over a suite the model was never scored on. The project-level hash cannot see this because it IS the thing that changed; only the per-run copy stamped at freeze can. Asking "does this cell hold every current item" of a cell frozen under a different set is the bug.
- **A cell whose runs name NO task item is a pre-suite cell and is ranked on its own run.** Otherwise materializing item 0 for a legacy project — which the `GET …/items` backfill does on first touch — would turn every historical singleton into an `item-incomplete` cell and unrank the project's whole history on a read.
- **`GET benchmarks/projects/{projectId}/cells` is its own route, not a shape on the run listing.** A run list cannot say which items a cell is MISSING, and the absence is the answer; the response also carries `scorableItemCount`, which is not derivable from the cells alone. Export schema is **4**: `taskItems[]` and `cells[]` as top-level sections, and `taskItemId`/`taskItemIndex`/`cellKey`/`taskInputHash`/`taskItemSetHash`/`cellQuality` APPENDED to the CSV. `taskItemIndex` goes through the formula guard even though it is numeric, because a leading `-` is what a spreadsheet evaluates.
- **A run's `rank` is now its CELL's rank**, and `cellQuality` is the cell's mean beside the run's own `qualityScore`. The JSON export re-reads each run through `GetRunAsync`, which computes neither, so both have to be re-attached from the ranked summary — the same trap that already applied to `Rank`.

### Benchmark long-context probes (2026-08-26)

- **An item's verifier override is validated against the CURRENT judge rubric at write time, and again at judging time.** The override is applied by matching its criterion id against the rubric, so an id the rubric does not have applies NOTHING and the item is graded under the policy's own configuration — against another item's expected answer, producing a plausible score for a question nobody asked. `BenchmarkTaskItemService.EnsureOverridesFitRubric` refuses the write (the id must exist and `BenchmarkJudgeVerifierConfig.Parse` must accept the config for that criterion's KIND, so an `exact` blob on an `llm` criterion is refused too), and `BenchmarkProjectService.UpdateJudgePolicyAsync` re-runs it over every stored item before activating a rubric — refusing the rubric change rather than marking the items revised, because a stranded override is not a stale answer, it is a question with no expected answer, and unranking it quietly hides an edit the operator can still take back. A judging that still meets one (the item predates the rubric edit) fails with the `override-unmatched: ` prefix, which `BenchmarkStore.RankExclusionReason` turns into the `override-unmatched` exclusion — never a score.
- **`DeleteProjectAsync` deletes task items and pairwise fits explicitly.** Foreign keys are OFF, so the ordered delete IS the referential integrity and a table left out of it does not error — its rows simply outlive the project. The delete refuses a project that still has runs, so every run-scoped child is already gone; task items (encrypted prompts, reference answers and verifier overrides) and pairwise fits (run deletion only DEACTIVATES them) are the two that are scoped to the project itself.
- **A NIAH probe expands into child task items when the ITEM is written, not when a freeze runs.** A case generated during a freeze has no durable identity: nothing to stamp in `task_item_id`, nothing to hash into `task_input_hash`, nothing for `MaxTaskItems`/`MaxRunsPerRequest` to count, and no way for the ranking read to know how many probes a cell owed. Written as rows, a case is an ordinary `BenchmarkTaskItem` — so cell completeness, the caps, the staleness exclusions and the export all reach it with no NIAH-specific code anywhere in them. `IBenchmarkStore.CreateTaskItemAsync`/`UpdateTaskItemAsync` take the children and write them in the generator's own transaction.
- **The generator's id is minted in the SERVICE, not the store.** Every case is derived from `(parentItemId, contextTokens, depth, seed)`, so the id has to exist before the expansion that the store is handed.
- **`Random` is banned in the generator; it uses a SplitMix64 seeded from a SHA-256 of the case's parameters.** `Random`'s sequence is an implementation detail the runtime has changed before, and a case whose haystack shifted with a .NET upgrade would move its own `InputHash` — reading every answer ever given to it as an answer to a question that no longer exists.
- **The needle is placed by WEIGHT through the text, not by index into the sentence list.** wikitext sentences differ in length by an order of magnitude, so "the 50th of 100 sentences" and "halfway through the document" are different positions, and the depth axis is about the second. The test asserts the needle's character offset is within 5 points of the requested depth.
- **The haystack is built to 90% of the requested length and the case is labelled `NIAH ≈8k @ 50%`.** `ChunkTokenApproximation` under-counts English prose (the same 5–36% recorded for markdown above), so building to the full request overshoots the real tokenization and truncates the tail inside the model's window — and a needle that fell off the end measures the window, not the model. The `≈` has to survive into the UI: a probe that silently ran at 26k instead of 32k is worse than one labelled approximate.
- **A case keeps its own parameters in its `GeneratorConfigJson`.** Without them the freeze cannot re-check the probe's length without parsing a haystack back out of a prompt, and the FE has no label. A case whose parameters cannot be READ is refused rather than skipped — a probe nothing can vouch for must not slip past the length check.
- **The length refusal is taken twice, and both name both numbers.** At expansion the operator learns while still looking at the form; at freeze it is taken again, because a project's `ContextTokens` is editable after the cases exist.
- **The judge resolves each criterion's verifier config and the reference answer as `item override ?? policy config`.** Recall is unmeasurable without that rule: a suite whose items all shared one expected answer could only ask one question. It is deliberately NOT a policy-hash change: the override lives on the item, so it is already inside the item's `InputHash` and the project's set hash — which is what unranks stale answers to it — and moving the policy hash would force a project-wide re-judge of items that did not change. An item deleted after its run was frozen judges under the policy's own config rather than failing; the ranking read already excludes such a run as `item-set-revised`.
- **A project whose leaves are ALL `CountsTowardScore = false` reports `no-score` on its cells, not `item-incomplete`.** `LoadRankingAsync` computed `complete = scorableItemIds.Count > 0 && …`, so a pure recall project fell into the incomplete branch and the badge told the operator to re-run an item nobody had asked for. Recall itself is read off each case run's own `qualityScore`; the cases are scored normally and simply do not enter the cell mean.
- **`CountsTowardScore` for the cases lives in the GENERATOR CONFIG, not on the item draft.** The draft's own default is `true`, which is right for an authored prompt and wrong for a probe, and a bool property cannot tell "omitted" from "explicitly true".
- **A `niahCase` cannot be created, edited or deleted on its own.** Created by hand it would carry a parent that does not describe it; edited, the change survives exactly until the next re-expansion. The generator is the only writer.

### Benchmark paired-difference intervals (2026-08-26)

- **The resampling unit is the ITEM, drawn with BOTH cells' scores for it.** `BenchmarkPairedBootstrap.Estimate` takes two aligned quality vectors and bootstraps the mean of `a[k] - b[k]`; it never resamples the two cells independently. Independent resampling throws away the pairing and re-inflates the interval by exactly the between-item variance a task suite exists to hold constant — two cells that alternate wins item by item and two cells six points apart on every item would come out looking alike. The unit tests pin that difference directly.
- **Below three shared items there is NO interval, and the absence is the contract.** The endpoint omits the whole `pairedDeltas` entry rather than emitting nulls or a zero, because a delta of 0 with no interval is indistinguishable from a measured tie. The client renders "too few shared items" from the missing entry and "not separated by this suite" from `separated: false`; it never re-derives either from the bounds.
- **A display-only leaf keeps its own score and must be filtered out by hand.** `LoadRankingAsync` excludes a `CountsTowardScore=false` item from the cell MEAN by intersecting with the scorable id set — it does NOT null the run's `QualityScore`, because the recall axis reads it. Anything else that aggregates quality has to apply the same intersection itself or it silently ranks on a NIAH recall figure. The compare read does it by loading the item rows; the cell table cannot answer the question.
- **A shared item needs a RANKABLE score on both sides.** The compare read walks the ranked cell table and keeps an item only when both cells carry a non-null `qualityScore` for it — so `item-revised`, `item-set-revised`, truncated and unjudged runs take their item out of the comparison instead of into it with a guessed number. A whole cell excluded as `item-set-revised` therefore shares nothing with anybody and produces no delta at all, which is the intended refusal: it was measured against a suite the project no longer has.
- **Shared items are ordered before they are drawn.** The bootstrap draws by index, so a non-deterministic shared-item order would make the seeded interval irreproducible even though the multiset is unchanged. `SharedQuality` orders by task-item index then item id.
- **Seed 0 and nearest-rank percentiles, matching `BenchmarkBradleyTerry`.** Two intervals shown side by side in one view must agree about what a percentile is; the S2245 suppression above the `new Random(0)` says why a fixed seed is the point here rather than a smell.
- **`GET benchmarks/projects/{projectId}/compare` is a read-time projection over `ListCellsAsync`, not a new store path.** Nothing about a comparison is stored, `IBenchmarkStore` gained no member, and the delta is always recomputed from the scores the project holds now. Two to six distinct cell keys; an unknown key is a 400 that NAMES the key, because an operator comparing a cell a re-freeze replaced needs to know which one is gone.

### Benchmark scheduled matrices and the training hand-off (2026-08-26)

- **An unattended fire must not inherit an interactive request's time budget.** `StartBenchmarkRunBatchEndpoint` stops freezing cells after 45 s because it is holding an HTTP connection open. `RunBenchmarkBatchHandler` copied that number and the live gate caught it: a 2 x 2 matrix on Qwen3.8-27B enqueued 3 of 4 cells, because the freeze verifies each model's GGUF by digest and a cold cell costs ~18 s in that run. The handler's budget is now **45 s per cell**, so it scales with the matrix; the scheduler's own max-runtime is the outer bound. Reusing the endpoint constant in the unattended handler reproduces the same bug.
- **A scheduled matrix ENQUEUES and returns.** It never awaits the runs — the single-consumer `BenchmarkQueueHostedService` drains them — so the template carries no `DefaultMaxRuntimeSeconds` and a fire that queues eight GPU-hours still finishes in a minute.
- **A fire that finds queued/running WORK on the project SKIPS, and a skip is a SUCCESS.** A nightly matrix landing on the previous night's leftovers would measure the same project twice. Reporting the skip as a failure would train an operator to ignore a red schedule; `SchedulerMisfirePolicy.SkipMissed` covers the other half (a node that was off when the trigger was due).
- **The busy guard counts WORK ITEMS, not run statuses.** Judging, fidelity and pairwise comparison work outlives the run it belongs to — it is seeded on primary SUCCESS — so a matrix whose every run reads `Succeeded` can still be holding the single-consumer queue and the GPU for hours. Counting `PrimaryStatus` called that project idle and the next fire enqueued a second matrix on top of it. `IBenchmarkStore.CountActiveWorkAsync(projectId)` returns Queued|Running items per kind (joined to the project through `BenchmarkRun`, because work items carry only a run id and foreign keys are off), and the skip summary names the kinds — "still busy" and "still busy JUDGING" are different operator actions.
- **`AllowAgentCreation: false` on this template, deliberately.** An AI agent may schedule a saved-agent run (`run-agent` opts in); it may not schedule GPU-hours.
- **The scheduler's template catalog is fully data-driven, so a new template needs NO frontend change.** `GET scheduler/templates` serves the registered descriptors and `ScheduledJobForm` builds the picker, the schedule-kind list, the misfire default, the max-runtime prefill and the parameters textarea from that response — including `defaultParameters`, which is why a template should publish a filled-in parameter skeleton. The only hardcoded template id in `features/scheduler` is `run-agent`, and only for one help sentence about its derived ceiling.
- **Progress events are not readable over REST; the run row's `summary` is.** `ScheduledJobExecutionContext.ReportProgressAsync` writes `scheduled_job_run_events`, and no endpoint returns them (`GET scheduler/runs/{id}` returns the run row only). A handler that wants its outcome visible sets `ScheduledJobExecutionContext.Summary`, which `SchedulerDispatchExecutor` persists onto the run on success (handlers that set nothing keep the generic `"Completed."`) — without it a real fire and a busy-skip are both `Succeeded` / `Completed.` and only `durationMs` tells them apart. Same content rules as a progress message: the column is plaintext-structural, so counts, ids and operator-supplied names only.
- **A manual "Run now" is only distinguishable from a cron fire by a marker on the firing trigger.** Both land in the same Quartz `IJob`, so `TriggerNowAsync` stamps `SchedulerJobKeys.ManualFireKey` on the fire's `JobDataMap` and `SchedulerDispatchJobRunner` turns that into `ScheduledRunTrigger.Manual` for the run row. It is deliberately not a parameter-override key — the dispatcher's whitelist never sees it.
- **The training → benchmark hand-off resolves INSTALLED model names, not evaluation model names.** A comparison's tuned side is usually a `StagedTrainingArtifact` evaluation whose `ModelName` is a file name the harness cannot launch. The installed name is the artifact's `CommittedModelName`, which only exists after `ArtifactPromotionService` registers it (stamping `LocalModelOrigin.Trained`); until then the hand-off refuses with that reason instead of failing inside the freeze. Both sides resolving to one name is also refused — two runs of one model are not a comparison.
- **A freeze DECIDES and a commit WRITES, and the training hand-off needs both halves separately.** `IBenchmarkRunFreezeService.FreezeAsync` returns a `BenchmarkFrozenRunPlan` — verified lease, eligibility, agent resolution, project version, run-count ceiling, all already checked, nothing persisted — and `CommitAsync` inserts whole plans in ONE `StartRunsAsync` under one compare-and-swap. `StartAsync` is still the two back to back and is what every single launch and every matrix cell uses. The hand-off freezes base AND tuned before committing either: committing the base group first meant a tuned side that failed verification left an hour of GPU work queued while the caller got an error carrying no run ids, so the only retry available queued a SECOND base group. Every plan of one commit must name the same project at the same expected version — nothing is written between them, so the two sides present the SAME version, not a chained one.
- **A benchmark project NAME is not an identity, so the hand-off's reuse key is every field it freezes against.** Names are not unique and a project carries no comparison id; matching on the name alone reused whatever project happened to be called "Nightly tune" and benchmarked both models against ITS task, context window and agent instead of the operator's — silently, because nothing about the hand-off's own request was checked against the project it reused. Reuse now requires name + core task (compared as the stored JSON bytes, never decoded — a legacy payload would throw) + context tokens + agent definition; anything else creates a new project under a suffixed name (`Nightly tune (2)`). The JUDGE is deliberately excluded: the hand-off never sets one, and enabling judging afterwards scores both sides equally rather than making it a different benchmark.
- **A bare `KeyNotFoundException` from the freeze escapes as a 500.** `BenchmarkEndpointSupport.Classify` maps it to 404 but `IsHandled` deliberately excludes it, so the global `BenchmarkExceptionHandler` never sees it. Every caller of `IBenchmarkRunFreezeService.StartAsync` must either catch it at the endpoint (which costs an `EndpointExceptionMappingSourceGuardTests` allowlist entry) or translate it in the service into a benchmark exception — the hand-off does the latter, which also lets it name the model.

### Benchmark frontend for task suites (2026-08-26)

- **The UI's word for a cell is "combination".** "Cell" is a persistence term; an operator is looking at a model x KV-type grid. Every user-facing string, including the exclusion badges, says combination — and the backend reason codes (`item-incomplete`, `item-set-revised`) still appear verbatim in the copy, because that is what the API returns and what a support question will quote.
- **Every destructive item edit states its cost BEFORE the operator commits.** Saving an edit says the item bumps to r*N* and every run of r*N-1* becomes `item-revised`; deleting says every already-measured combination becomes `item-set-revised` and that a partial cell never becomes complete by losing the question it missed; reordering says explicitly that NOTHING changes. The third sentence is the one that has to be there: a UI silent about reordering trains an operator to fear it.
- **The launch estimate is runs, not time, unless the project has history.** `benchmarkRunEstimate` multiplies cells x leaf items x (repeats + warm-up); `estimatedMs` stays null when no completed run exists to extrapolate from — omitted rather than guessed — and is formatted coarsely (`1h 12m`) when it does. Warm-ups are excluded from the median because they are the slow launch the repeats after them are measured without. Over `MaxRunsPerRequest` the copy is a REFUSAL, not a warning: the node refuses the whole freeze, not the excess.
- **A missing `pairedDeltas` entry renders "too few shared items"; `separated: false` renders "not separated by this suite".** Neither is re-derived from the bounds, which is the whole point of the endpoint omitting the entry (see the paired-difference section).
- **`no-score` needs its own copy and its own absence of an action.** The per-cell "run this item again" chip was wrong on a pure long-context probe: there is no missing item. The ranking guard requires no action for this state.
- **The frontend mirrors backend constants and must be changed with them.** `benchmarkTaskItemLimits` (20 leaves, 100 runs per request), `benchmarkNiahLimits` (512-token probe floor, 20 cases), `benchmarkPythonTestsLimits` (4 000-char test code, 16 exports, 600 s) and `maxVerifierPatternLength` (512) are copies of `BenchmarkTaskItemService`, `BenchmarkNiahGenerator`, `BenchmarkPythonTestsHarness` and `BenchmarkJudgeVerifierConfig`. Drift makes a form that accepts what the node refuses.
- **The scheduler needed no frontend change at all** — its template catalog is data-driven (see the scheduled-matrices section). The benchmark suite needed a large one, because none of task items, cells or paired deltas is expressible in an existing shape.


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
- Skills are the one place the child's approval surface is NOT strictly narrower: `SubAgentSpawnService.ChildConfiguration.AttachSkillsProvider` builds its `AgentSkillsProvider` with `DisableLoadSkillApproval` and `DisableReadSkillResourceApproval` true, because those tools arrive through `AIContextProviders`, bypass `CurateChildTools`, and would otherwise be handed to a child that can never get them approved. `run_skill_script` keeps its gate on every path, including this one.
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

### A pending chat approval does not survive a restart, and that is the accepted behaviour

`PendingToolCallRegistry` is one per-process dictionary of `TaskCompletionSource`s, so a restart drops every parked approval, and there is no approval-specific reconciler. The general sweep `NodeChatRestartRecoveryService.RecoverInterruptedMessagesAsync` forces every assistant row in `Pending`, `Queued` or `Streaming` to `Interrupted` and backfills the missing run-envelope audit row, so a parked turn surfaces as interrupted and the operator re-sends it. Operator ruling 2026-09-09: accepted product behaviour for chat, not unpaid debt. Graph Workflows is the deliberate exception — `GraphWorkflowStartupReconciler` leaves `WaitingForApproval` node runs untouched because that wait is persisted, and `IGraphWorkflowRunService.DecideAsync` resolves it idempotently over REST. Prevents filing the chat behaviour as a bug, and prevents copying the graph engine's durable wait into chat without a ruling. Authority: `PendingToolCallRegistry`, `NodeChatRestartRecoveryService`, `GraphWorkflowStartupReconciler`; ledger D5.

### A tool handler that throws does NOT end the turn on Microsoft.Extensions.AI 10.9.0

The loop runs a further provider round; what the throw destroys is the handler's sentence, replaced by the pipeline's fixed `Error: Function failed.` (`IncludeDetailedErrors` is left `false`), so the model retries blind. Prevents the wrong inference that returning instead of throwing is about turn lifetime. Authority: measured with a standalone probe against the pinned package; `EmitOutputToolHandler.ExecuteAsync` carried the wrong claim until 4d7ee3ea.

### Tool invocation is sequential and blind on purpose, and both flags are pinned in code

`AgentServiceCollectionExtensions`'s `UseFunctionInvocation` callback sets `AllowConcurrentInvocation = false` and `IncludeDetailedErrors = false` explicitly rather than leaving either at its default, next to `MaximumConsecutiveErrorsPerRequest = 3` and `TerminateOnUnknownCalls = false`. Sequential is right here: the node owns one invocation slot, and `ToolArgumentRepairScope`, `ToolResultBudgetScope` and `ProviderCallBudget.Current` are `AsyncLocal` per-request counters — as is `ToolInvocationObservabilityChatClient`'s `CallId` pairing — none of which has been shown safe under tool calls running concurrently inside one round. Detailed errors stay off on privacy grounds; the model retries against the pipeline's fixed `Error: Function failed.`. Prevents reading the unset defaults as an oversight and flipping them for latency, and prevents a bump silently changing either. Authority: `AgentServiceCollectionExtensions`, pinned by `AgentToolPipelinePolicyTests.MultipleFunctions_AreInvokedSequentially` and `HandlerExceptions_AbortAtPinnedConsecutiveErrorLimit`; enabling concurrency needs each `AsyncLocal` scope audited and a measurement, not a flag flip.

### A blank tool-call id is the only id-less shape Microsoft.Extensions.AI 10.9.0 can hand you

Both `FunctionCallContent` and `FunctionResultContent` reject a null `CallId` in their constructor with `ArgumentNullException`, and `CallId` is read-only, so a provider that omits the id can only stream the empty string — a null guard on a call id there is unreachable code. `InvocationRunner.RunSingleAgentAsync` therefore mints invocation-local surrogate ids for a blank id: the bare tool name for the first call, `<name>#N` for an overlapping or later call of the same name. Prevents reasoning about `callId ?? name` fallbacks that can never fire, and prevents adding null guards that cannot be reached. Authority: a standalone probe against the pinned package during the S6 first-pass review fixes (2026-09-04), pinned by `InvocationRunnerTests.RunAsync_WhenTheProviderStreamsTwoSameNameCallsWithABlankCallId_PairsEachResultWithItsOwnCall`.

### Persisted chat parts are a render/reload record, not model context

A persisted `NodeChatMessagePart` reaches the model only through `ConversationContextBuilder.Build(includeToolHistory: true)` → `ConversationMessageDto.ToolExchanges` → `InvocationRunner.BuildChatMessages`, and only the integration execution coordinator asks for it, for a `CallerManaged` session — and only off the FULL read (`GetConversationAsync`): `GetConversationForTurnAsync` blanks `metadata_json` for every non-user row the compaction synopsis covers, which is exactly where the parts live, so a replay fed by the capped turn read is silently empty once the session compacts (`NodeChatTurnReadCapTests`). Chat has written those parts since long before any of this and never replayed one. Prevents assuming a continued turn "sees" its own tool calls because the SPA renders them — the S6 kickoff assumed chat already replayed parts, and it never did, so half of D-7 was new code rather than plumbing. Authority: `IntegrationContinuationTests.ACallerManagedContinuationReplaysTheCallItsResultAndThenTheTurnsText`, this pass 2026-09-04.

### MAF traps

- `ChatClientAgentOptions` has no `Instructions`; use `ChatOptions.Instructions`.
- Positional constructor order is `(chatClient, instructions, name, description, …)`, **verified at 1.20.0** — it moved once under a bump (`8459cb962`), so named arguments pin it. Instructions are delivered exactly once through the leading system seed; pass the ctor instruction argument null. Skill preambles mean tests assert containment, not exact equality.
- Fakes must inspect both messages and `options.Instructions`.
- At the current pin approval types are `ToolApprovalRequestContent` / `ToolApprovalResponseContent`; wrapping the function is the gate. Middleware alone does not gate a plain tool.
- `AgentSkillsProvider` needs scoped `MAAI001` suppression.
- Never log raw tool arguments/results; record length + SHA-256 12-hex prefix.
- Sessionless runs (`session: null` everywhere) are a product decision, not a bug workaround. Live spike at 1.20.0 (2026-09-09, Qwen3.8-27B): `SerializeSessionAsync`/`DeserializeSessionAsync` round-trip a pending approval cleanly, but resume accepts a forged `FunctionApprovalResponseContent` silently and the blob replays an approved tool once per resume with no consumption ledger. `ApprovalResponseValidatingAgent` stays; do not re-open on "the serialization bug is fixed" — the defect is the missing consumption ledger, not the round-trip. Authority: `ApprovalResponseValidatingAgent`, pinned by `FrameworkApprovalGateTests` and `BudgetedApprovalReplayTests`; live spike 2026-09-09.
- Workflows builder API: an `Executor<TInput>` cannot yield a workflow output until `ConfigureProtocol` declares it via `YieldsOutputTypes` (the runtime rejects the yield with "Expecting one of []"), and `Run.NewEvents` drains on read, so enumerate it once per checkpoint. Pinned by `WorkflowBuilderProbeTests`.
- `HarnessAgent` no longer exists as a type at 1.20.0; it is decomposed into `FileMemoryProvider` + `FileSystemAgentFileStore`/`InMemoryAgentFileStore` (explicit stores, no implicit cwd writes) and the seven `file_memory_*` tools, which are still built ungated. Wrap them in `ApprovalRequiredAIFunction` before ever wiring the provider. Authority: desk read of the pinned `Microsoft.Agents.AI` 1.20.0 assembly (`Directory.Packages.props`), 2026-09-09. No in-repo pin exists because nothing in this tree references those types — that absence *is* the state being recorded; re-read the assembly before relying on it after any bump of that package cohort.


Instructions are delivered exactly once through the leading System seed used by this application; pass null for the positional constructor's instruction argument. `AgentSkillsProvider` may prepend text, so “once” tests assert the intended instruction occurs once rather than requiring the entire System message to equal it. Approval middleware does not transform a plain function into an approval-required one; the function must be wrapped. Telemetry records no raw tool args/results at Information level.

### The four `gen_ai.tool.*` attribute names track MEAI, and two of them carry a digest rather than the payload

`ToolInvocationObservabilityChatClient` tags its two tool-call spans with `gen_ai.tool.call.id`, `gen_ai.tool.name`, `gen_ai.tool.call.arguments` and `gen_ai.tool.call.result`. Neither span sets `gen_ai.operation.name`: MEAI's function-invocation hop (`FunctionInvokingChatClient`) already owns the conventional `execute_tool` span for every call, on the pipeline's `OpenTelemetryChatClient` source, so the repo's pair is its own request/completion observation correlated to it by `gen_ai.tool.call.id` — claiming the value again would have a convention-aware backend count every tool execution twice. Two of the four names bend their own convention on purpose. `gen_ai.tool.call.arguments`/`.result` are defined by the registry as the actual payloads; here they carry the redaction digest instead (serialized UTF-8 byte length plus a 12-hex SHA-256 prefix, per the MAF-traps rule above), because tool arguments and results are the node's most sensitive strings. The live OTel semantic-convention registry marks all four names **Deprecated**, and they are kept anyway: they are what Microsoft.Extensions.AI 10.9.0 itself emits, so tracking MEAI keeps this node's spans on the same shape as every MEAI-emitted span in the same trace. `tool.outcome` stays a custom tag rather than an `Activity` span status because existing assertions and consumers read the tag; setting a status is an addition to make deliberately, not a rename. Prevents a convention-aware reader "fixing" the digest attributes back into carrying raw payloads (a content leak), and prevents a future pass renaming away from MEAI's names on the strength of the registry's Deprecated flag alone. Authority: the four names were read off Microsoft.Extensions.AI 10.9.0's own attribute constants and cross-checked against the live semantic-convention registry's status on 2026-09-09; pinned by `ToolInvocationObservabilityChatClientTests.GetStreamingResponseAsync_ConventionPayloadAttributes_CarryHashesNotRawContent`, `GetStreamingResponseAsync_NeitherSpan_CarriesTheExecuteToolOperationName`, and the whole-pipeline `AgentToolPipelinePolicyTests.ToolCall_ClaimsTheExecuteToolOperationNameExactlyOnceAcrossTheWholePipeline`. A separate MEAI leak sits on the failure path: `OpenTelemetryLog.RecordOperationError` copies `error.Message` into the gen_ai span's status description regardless of `EnableSensitiveData`, and a provider exception message embeds the request text or the raw HTTP response body, so `GenAiErrorDescriptionRedactionProcessor` in `ServiceDefaults` (registered right after `GenAiCancellationStatusProcessor`) rewrites that description down to the `error.type` value at export and keeps every failure path metadata-only; pinned by `GenAiErrorDescriptionRedactionProcessorTests`, whose two end-to-end tests assert the leak through the real chat and embedding hops before asserting the redaction.

### A MAF-trio bump above patch level is not merged on a green build alone

`Microsoft.Agents.AI`, `Microsoft.Agents.AI.Abstractions` and `Microsoft.Agents.AI.Workflows` move as one cohort (`Directory.Packages.props`). A patch bump may land on CI alone. **Any minor bump of the trio, and any bump at all that moves `Microsoft.Agents.AI.Workflows`, requires all three of the following before merge:** run `HandoffWorkflowSpikeTests`, `FrameworkApprovalGateTests` and `AgentSkillsProviderContractTests` and name the result; re-run the O1 workflow-backbone axes and re-record the verdict against the new version; re-read the MAF traps above and correct anything the bump made pin-wrong. Prevents the failure already in the tree — `8459cb962` fixed a `ChatClientAgent` positional-argument order that changed under an unaccompanied Dependabot merge — and prevents O1's premise going stale unnoticed, which is exactly what 1.17.0 → 1.20.0 did. Authority: ledger D13, operator ruling 2026-09-09.

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

### A live-audio hub method's size cap is application-level, not the SignalR default

**Rule:** the node's SignalR `MaximumReceiveMessageSize` is 512 KB (`ConfigureServices.cs`), not the framework's
32 KB default, so a per-frame bound must be asserted inside the hub method itself (`TranscriptionHub.MaxFrameBytes`),
and its test must be a unit test of that method, not of the transport. **Prevents:** an oversized frame accepted
silently, because the transport-level 32 KB default that would otherwise have rejected it was already raised to
512 KB for the whole node. **Authority:** S3 plan §1a.2; `TranscriptionHubTests`.


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


### `ExecuteDetailedAsync` is the single compute execution boundary

`IComputeToolGateway.ExecuteDetailedAsync(request, requireResourceLimits, ct)` performs, in order: the `Compute:Enabled` kill switch, `ComputeRunToolRequestValidator`, the filesystem-isolation refusal, an optional resource-ceiling refusal, and the jail-root refusal. `ExecuteAsync` is that method with `requireResourceLimits: false` plus the model-facing formatter — one execution path, two projections. The flag and the validation used to live in `RunPythonToolHandler`, which made them properties of ONE caller: any second caller of the gateway executed model-authored code on a node that had never opted in, with no bound on the script. Exactly one execution path reads the switch; `ExecuteDetailedAsync_IsTheOnlyExecutionPathThatReadsComputeEnabled` pins it, with the two non-execution readers (the mathematician persona seeder, and AgentHome's use of `ComputeOptions` for ceiling defaults) allow-listed by name.

`requireResourceLimits` exists because `SandboxResourceCeilings.Resolve` returns `null` when the backend cannot impose ceilings, so `run_python` runs unbounded on a host with no working systemd user scope. That is acceptable for a tool a human approves call by call and NOT for operator code executed unattended — so `run_python` passes `false` and is byte-identical to before, while the benchmark verifier passes `true` and is refused there. `RunPython_WithoutResourceLimits_StillRuns_BehaviourUnchanged` is the test that would be inverted if that decision is ever revisited.

### `pythonTests`: the process computing the verdict must never execute the graded code

A namespace is not a trust boundary. A "trusted harness" running the candidate in the same interpreter is defeated in a dozen lines: walk the ancestor frames for the nonce, write a passing marker to `sys.__stdout__`, `os._exit(0)` before the real harness prints. Variants need no frame walk at all — patch `builtins.print`, patch `json.dumps`, replace `unittest` in `sys.modules`, or monkey-patch the operator's assertion helpers.

The shipped design is two processes in one bwrap jail. A trusted PARENT (the gateway's `python -I -` on stdin) holds the nonce and the operator's `testCode`, calls `prctl(PR_SET_DUMPABLE, 0)` **before** spawning, runs the tests itself against a proxy, and prints exactly one nonce-marked verdict line. An untrusted CHILD `exec`s the candidate and answers JSON `call`/`eval` requests over its own inherited pipes. The child holds no nonce, no test source and no handle to the sandbox's stdout, and cannot ptrace the parent. The parent is PID 1 of the jail's PID namespace, so killing it tears the namespace down and yields no stdout at all.

- **Everything variable crosses as base64.** A candidate carrying triple quotes, a trailing backslash or a NUL would otherwise escape its literal and land as code in the trusted parent — the whole threat model reopened by a quoting bug. The candidate is decoded only inside the child.
- **A child-named exception is re-raised only when `getattr(builtins, name)` is a `type` subclassing `Exception`** — never `BaseException`, never `sys.modules`, never `eval`. So a child naming `SystemExit` produces an ordinary test failure, not an injected escape. The test phase is wrapped in `except BaseException` with the marker printed from `finally`: no path through the parent skips the verdict line.
- **Verdict table, no fail-open direction:** one marker with `failed == 0 && collected > 0` scores; one marker otherwise scores 0; **zero markers score 0**, because denying a verdict is a failure; two or more score 0 and are logged as forged.
- **Unscorable is not zero.** A refusal (`compute-disabled`, `invalid-request`, `no-isolation`, `no-resource-limits`, `no-jail-root`, a `ComputeEnvironmentException`, or a failed `PR_SET_DUMPABLE`) fails the judging with the `verifier-unavailable: ` prefix, which `BenchmarkStore.RankExclusionReason` turns into the `verifier-unavailable` exclusion. A TIMEOUT is the opposite: the code ran and did not finish, which is a real result about the code, so it scores 0.
- **Counts are best-effort by design.** `unittest.TestCase` subclasses in the parent's test namespace are run with a `TextTestRunner` over a `StringIO` and read as OBJECTS; a bare script with no runner is one implicit case. No framework output is ever parsed.
- **The templates are minified before composition** (blank lines and whole-line comments dropped), because the composed parent must fit inside `ComputeToolDefinition.CodeMaxLength` alongside the candidate and the tests. That is sound only while neither template carries a multi-line string literal, which a test pins.
- **The cost, not hidden:** arguments and returns must be JSON-serializable and exceptions match by name. `pyeval` runs arbitrary setup in the child so only the final expression crosses. Making the tests expressive by running them beside the candidate is the design this rejects, not a refactor.
- **The adversarial suite only means something against a real process.** A gateway substitute returns whatever the test author decided a sandbox returns, so it proves the parser, never the boundary. The unit rows run the real generated programs through a local interpreter; `BenchmarkPythonTestsLiveTests` runs the same candidates in the real jail behind `XE_COMPUTE_LIVE=1`.

**`run_python` refuses before provisioning.** `ComputeToolGateway.ExecuteDetailedAsync` checks `SupportsFilesystemIsolation` immediately after the kill switch and argument validation, before interpreter provisioning, node identity reads, or jail creation. Unsupported hosts receive a refusal and do not download/unpack the managed Python closure. Every call requests `SandboxIsolationMode.Filesystem`; unlike AgentHome, Coder, and Development Mode, compute has no degraded host-filesystem-visible fallback.

The venv requires two read-only sources: its own tree and uv's `pythons` root, because `bin/python` points through a version alias outside the venv. Execute that symlink—not its realpath—so `sys.prefix` retains site-packages. Bind only `pythons`, never the broader uv cache. Descriptor resolution rejects a symlink in **any** bind-source component; `Path.GetFullPath` alone is not canonicalization.

Caller env uses `/work`, `/work/home`, and `/tmp`. Caller variables are emitted last and therefore override chain defaults, so never pass host jail paths or repeat thread variables derived from `SandboxCreateRequest.ThreadLimit`. The sealed synthetic `/etc` memfd is rewound and non-CLOEXEC. Kill the transient scope cgroup, not only PID/PGID trees; a nested session can escape both process-tree mechanisms.

Bubblewrap's filesystem capability and non-isolated `unshare` capability are distinct. `run_python` asks for filesystem isolation and relies on bwrap's own `--unshare-net`; refusing because the separate `SupportsNetworkPolicy` probe failed creates a false negative. Conversely, a host lacking filesystem isolation is refused even if it can create a network namespace because the tool promises both host-filesystem absence and no egress.

---

### A second copy of the tool-invocation logic drifts, and the drift looks like a product bug

**Rule:** anything that executes a tool for real calls `IToolInvocationService.InvokeAsync`; a caller may add what is
its own (an offer to compose, an envelope to shape) but never its own resolve/validate/call/classify chain.
**Prevents:** the two drifts the training path had accumulated since S2 promoted that seam out of it —
`HeadlessToolExecutor` never consulted `IClientLocalToolRegistry`, so `read_file` and its five worker-owned siblings
**failed** for a teacher turn that asked for them; and a repair envelope from `ToolArgumentRepairAIFunction` came back
as a **successful sample** carrying model-repair guidance as its tool result, poisoning the dataset quietly.
**Authority:** `HeadlessToolExecutor` and `HeadlessToolExecutorTests`; `ToolInvocationService.TryAdmit` is the same
seam the Graph Workflow Tool gate reads.

### Graph Workflows: `Parallel` and `Join` are labels, not semantics

**Rule:** fan-out is any node with more than one satisfied outbound edge, and fan-in is the `joinPolicy` carried by **every** node. A `Parallel` or `Join` node is cosmetic — it is an inline kind that writes two rows, which is what makes the timing of a fan-out visible in the event log, and nothing more. **Prevents:** wiring a "real" parallel executor, or assuming a `Join` node is what makes a join happen — and its mirror image, giving an ordinary node two inbound edges and being surprised it waits for both. **Authority:** `GraphWorkflowStateMachine.Admission` reads `node.JoinPolicy` whatever the kind; `GraphWorkflowGraph.ValidateNode` refuses `Any` on any node with fewer than two inbound edges; `DevWorkflowStateMachine` holds the same rule.

`All` over ZERO inbound edges is vacuously satisfied, and that is load-bearing rather than pedantic: it is how the `Start` node becomes eligible at all. `Pending` outranks `Dead` under both policies, so a dead branch never skips a node in front of a sibling branch the run has not finished.

### Graph Workflows: an edge condition reads the SOURCE node's output, never the target's input

**Rule:** `{ path, op, value }` on an edge is evaluated against the source node's stored output document, so a path is written relative to that envelope (`output.…`, `status`, `attempt`, `branch`). **Prevents:** writing a condition against the merged upstream document the target will be handed and getting a silent `false` — evaluation **fails closed**, so a path the output does not carry answers false for every operator except `NotExists`, and the run hangs with nothing in the log to explain it. **Authority:** `GraphWorkflowStateMachine.EdgeState` → `GraphWorkflowCondition.Evaluate`; `GraphWorkflowDocuments.Compose` is the single writer of the envelope being read.

This is also why `Condition` and `Parallel` nodes pass their predecessor's `output` through verbatim (`GraphWorkflowDocuments.PassThroughOutput`): without it a Condition's own out-edges would inspect `{}` and never fire.

### Graph Workflows: `Pause` parks a node run in `WaitingForApproval`, and there is no `Blocked` state in v1

**Rule:** a `Pause` node run sits in `WaitingForApproval` until a person answers through `DecideAsync`. Both `Approve` and `Reject` **succeed** the node run — the answer is its output, and routing on it is the edges' job — so a rejection reaches the run through an out-edge, never through a node failure. **Prevents:** copying the Dev Workflow module's retries-exhausted `Blocked` state, which has no meaning without retry routing, and expecting a rejection to fail the node. **Authority:** `GraphWorkflowNodeRunStatus` (no `Blocked` member), `GraphWorkflowStateMachine.TargetFor` and `IsLegal`, `docs/wiki/21-graph-workflows.md` §4.6.

`WaitingForApproval → Skipped` is deliberately not a legal transition: skipping an open pause would be an operator walking past a decision instead of giving one. The startup reconciler leaves `WaitingForApproval` rows alone for the same reason — a durable human wait is not in-flight work, and taking it would destroy every pause on the node on every boot.

### Graph Workflows: a `Tool` node passes TWO gates, and the run-start check is the one that wins

**Rule:** a Tool node may run only a **built-in** tool that is `ToolCategory.ReadLocal` **and** whose composed effective approval is `false`. Both are asked of the same catalog when the definition is saved AND again when a run starts. **Prevents:** a tool that was read-only when the graph was saved executing unattended after a node policy tightened — and the mirror error of treating an out-of-envelope tool as a warning, when it is an error keyed to the offending node. **Authority:** `ToolInvocationService.TryAdmit` (gate 1 risk class, gate 2 `IToolApprovalPolicy.RequiresApproval`, plus the structural `ApprovalRequiredAIFunction` floor), `GraphWorkflowToolGate.ErrorsAsync`, `GraphWorkflowRunService.EnsureToolNodesAreRunnableAsync`; ADR 0006.

`graph-workflows/tools` is filtered server-side through the same `TryAdmit`, so the picker cannot offer a name the run would refuse. A graph with no Tool node never reads the catalog at all.

### Graph Workflows: a node's `input` is its ONE satisfied predecessor's output, so a node inserted mid-chain REPLACES the content

**Rule:** `input` is the single satisfied predecessor's output document, and becomes the `upstream` map only when several predecessors are satisfied. A `Pause` writes its own document (`{decision, comment, payload}`), so `A → Pause → B` hands B the approval metadata and never A's answer, even with `includeUpstreamOutputs: true`; a `Pause` before `End` loses the result the same way. Only `Condition` and `Parallel` pass their input through. To carry content past a node that does not forward it, wire a second edge from the content's source to the consumer — the consumer's default `All` join still waits for the approval and its `input` becomes the `upstream` map with both. **Prevents:** authoring or importing a chain whose downstream Agent silently works on the wrong document (the S4 live round watched agent-2 spend three chat and three embedding calls hunting for a haiku that was never in its input). **Authority:** `GraphWorkflowDocuments.ComposeInput`, `GraphWorkflowInlineExecutor.Upstream`, `GraphWorkflowDocuments.PauseOutput`; the Open Canvas importer's `CanvasWorkflowImport.AddPauseContextEdges` is the worked example, pinned by `CanvasWorkflowImportRunTests`.

An author no longer has to know this unaided, and the two halves are worth knowing separately. The EDITOR adds the edge for you (`pauseContextEdges`), but **only on the connect gesture** — never on render or validate — so an edge you delete stays deleted; wiring out of a Pause considers only the node just connected, wiring into one considers every successor reachable through consecutive pauses; the editor judges each successor exactly as the validator and the importer do (starved only when every inbound edge leaves a Pause). The VALIDATOR raises a non-blocking warning on the starved node (`GraphWorkflowGraph.Warnings`, surfaced as `warnings` on the validate response): `valid` is still zero ERRORS, so a warned graph saves and runs. All three use the same rule — the pause's nearest non-`Pause` ancestor, walking back through consecutive pauses, `Start` included — and all three **skip a `Condition` ancestor**, because the edge would be that node's second unconditional out-edge, which the parser refuses. Two more guards, found by review: nothing is added when the ancestor is **not unique** (two exclusive branches into an `All` successor skip it the moment the untaken branch is dead), and nothing touches a successor whose **`joinPolicy` is `Any`** — it is a property of every node kind, not only `Join`, and an unconditional content edge would admit it with every approval rejected, so the warning is not raised there and neither the editor nor the importer adds an edge. The importer reads a successor's join policy and kind off the node **it just emitted**, never off the canvas kind, which is what keeps its answer identical to the validator's; every guard but the starvation test is unreachable for a real Open Canvas graph and is stated anyway, because the rule and not today's vocabulary is what the next node kind must keep holding. Advice that turns a warning into an error is worse than the generic sentence, so the warning falls back to "a node before the pause" for a Condition or non-unique ancestor.

### Graph Workflows: a cap has to be enforced BEFORE the work it bounds, not after it

**Rule:** `GraphWorkflowGraph.Parse(graphJson, maxNodes)` refuses on the **declared length of the `nodes` array**, before a node is read or an edge walked, and `EnsureAcyclic` walks on an explicit `Stack<(string, int)>` rather than recursively. **Prevents:** a request under the 1 MiB body cap carrying a chain of thousands of minimal nodes, whose recursive cycle walk spends one stack frame per node and overflows the thread-pool thread's stack — a **process kill** no `catch` ever sees, on a request the cap was supposed to have refused. The old order compared the count against the *parsed* graph, so it bounded nothing about the parse that produced it. **Authority:** `GraphWorkflowGraph.Parse` / `ParseNodes` / `EnsureAcyclic`, `GraphWorkflowGraphContract.ValidateAndCountNodes`, `GraphWorkflowGraphContractTests` and `GraphWorkflowGraphTests`.

The message and the exception type did not change, so the endpoint contract and the editor's mapping stand. The callers that re-parse an already **stored** graph — `GraphWorkflowRunService`, `GraphWorkflowDispatcher`, `HasRejectBranch`, `ToolNodeNames` — stay uncapped **on purpose**: that graph was capped when it was saved, and a cap an operator lowered afterwards would make a live run unroutable rather than merely unsaveable. The iterative walk is what makes those uncapped paths safe. Threading the path through it also recovered what the copy from Dev Workflows had lost: the refusal now names every node on the cycle in walk order (`a -> b -> c -> a`), not just the node it came back to.

**Dev Workflows had the same hole, and it is now closed too** (S7, operator ruling R7-3 re-opening what S6 recorded as a finding only). `DevWorkflowOptions.MaxNodesPerDefinition` defaults to **500** — higher than the Graph cap, because a development workflow decomposes and one materialization expands a template subtree up to twenty times — and is held to `[Range(1, 10_000)]` like every other budget in that section. `DevWorkflowGraphContract.ValidateAndCountNodes(graphJson, maxNodes)` reads the option and hands it to `DevWorkflowGraph.Parse(graphJson, maxNodes)`, which refuses on the **declared length of the `nodes` array** before a node is read; `maxNodes` defaults to `int.MaxValue`, so the read paths that re-parse a stored graph omit it and stay uncapped — `DevWorkflowDefinitionSeeder` takes that default too, so the cap binds definitions written through the API and not the shipped seeds (11 nodes at their largest today), which is accepted rather than overlooked, for the same reason the Graph side keeps its uncapped re-parses. `DevWorkflowRequestSizeLimit` caps the body at **2 MiB** on the two definition-carrying routes, create and update — there is no Dev validate route — and both routes declare `ProducesProblem(StatusCodes.Status413PayloadTooLarge)`, so the generated hey-api client carries the refusal — which both halves that can refuse, Kestrel mid-read and the endpoint's own Content-Length exit, answer in that one declared shape since S8 (§5). The two Graph numbers are unchanged: **200 nodes, 1 MiB**. Both walks are explicit-stack like the Graph module's: `DevWorkflowGraph.EnsureAcyclic` and `AncestorsFirst` each run on a `Stack<(string NodeKey, int EdgeIndex)>` rather than a recursive local function, and a chain long enough to have overflowed the old recursion is pinned by a test per call shape (`DevWorkflowGraphTests.Parse_WithAChainDeeperThanTheStackWouldCarry_IsWalkedWithoutRecursion`).

### Graph Workflows: the response-schema warning mirrors a THIRD-PARTY transform, so it drifts on a package bump

**Rule:** `GraphWorkflowGraph.ResponseSchemaWarnings` warns, non-blocking, on an Agent node whose `responseJsonSchema` carries a relocated keyword, leaves a declared property out of `required`, or declares `properties` while omitting `additionalProperties`. Its `DroppedSchemaKeywords` and its walk are a hand-copy of `Microsoft.Extensions.AI.OpenAI`'s strict-schema transform (§2). **Prevents:** an author trusting a bound the grammar never enforces — and, on the other side, a **false** warning: the walk must descend exactly what the transform descends (`properties`, `additionalProperties`, `items`, `anyOf`, `oneOf`, `allOf`, and nothing else), or a constraint parked under `$defs`, `definitions` or `prefixItems` gets flagged when the transform never touches it either. **Authority:** `GraphWorkflowGraph.ResponseSchemaWarnings` / `DescribeSchema` / `NestedSchemaMembers`, `GraphWorkflowGraphTests`, `GraphWorkflowValidateEndpointTests`.

Re-read the adapter's list on every `Microsoft.Extensions.AI.OpenAI` bump: this copy cannot notice that the original changed, and a stale copy is a warning that lies in whichever direction the upstream moved. `additionalProperties` is the subtle member — only the **absent** case is warned about, because that is the only case the transform rewrites; an explicit `true` stays open and is silent. A `Start` node's `inputSchema` is deliberately never warned about, since nothing compiles it into a grammar. Both name lists cap at three before counting the rest, and the whole message goes through `GraphWorkflowStateMachine.Bounded`. Adding a warning kind needed no DTO, OpenAPI or SPA change: `Warnings` is a concatenation and the validation strip renders kinds generically.

Since S7 the parser still raises it for every Agent node, and `GraphWorkflowDefinitionService.WarningsForRuntimeAsync` drops the ones whose pinned `model` resolves through `ILocalModelProviderResolver` to the llama-server provider, because that lane no longer suffers the rewrite at all (§2, `ApplyResponseSchemaPassthrough`). The narrowing is at the service on purpose: the parser has no route to the model-to-provider map. It filters per **warning**, not per node, so a node that also earned the pause-context warning keeps that one, and a node with **no** model pin keeps its schema warning, since what an unpinned node inherits is decided at run start.

### Graph Workflows: `GraphWorkflowCondition.Order` and `DevWorkflowCondition.Order` are a knowingly duplicated pair

**Rule:** the two modules carry two copies of the same relational-comparison ladder, and a change to either copy is a change to **both** — edit both files and run both `GraphWorkflowConditionTests` and `DevWorkflowConditionTests`. There is no shared evaluator and no `GraphCore` library to move one into: the S7 graph-core extraction was assessed and **shelved**, so the duplication is the standing decision rather than debt awaiting a refactor. **Prevents:** fixing a comparison bug in one module and shipping the other module still wrong, with a green suite in both — each copy has its own tests, so neither notices the other drifted. **Authority:** operator ruling R7-1, 2026-09-07, extraction plan §6.2; the `Mirrors GraphWorkflowCondition.Order` comment in `DevWorkflowCondition`.

### Dev Workflows stays source-plus-agent-knowledge only — there is no wiki page for it

**Rule:** do not write one, and do not re-open the question unless a slice touches the module materially. What is durable about Dev Workflows lives in §4 here and in the XML docs on `DevWorkflowGraph`, `DevWorkflowStateMachine` and `DevWorkflowOptions`. `docs/wiki/22-workflow-engines-divergence-register.md` is not that page — it records where the two engines differ, not how either one works. **Prevents:** a second architecture reference that describes a module nobody is currently changing, drifting silently against the copy in the code, and paying `docs-inventory-check.py` upkeep for it. **Authority:** operator ruling R7-4, 2026-09-07.

### A fix in one workflow engine is not automatically engine-local

**Rule:** before changing behaviour in `Services/DevWorkflows/` or `Services/GraphWorkflows/`, read `docs/wiki/22-workflow-engines-divergence-register.md` for the sibling engine's equivalent. The two were copy-adapted, not shared, so most differences are deliberate — but the register also tracks unintentional gaps nobody has examined, and the register, not the reader's assumption, says which is which. **Prevents:** fixing a restart-recovery, approval or retry defect in one engine and leaving the same defect in the other, on the belief that the divergence was already a conscious choice. **Authority:** `docs/wiki/22-workflow-engines-divergence-register.md`, which records each divergence and its ruling; operator ruling D10, 2026-09-09 (write the register, defer convergence).

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

### An EF unmapped-type raw SQL query must ALIAS every column, or it binds nothing

**Rule:** `SqlQueryRaw<T>` on a type EF does not map matches result columns to PROPERTY names, so a snake_case column never binds to a PascalCase property. Write `SELECT graph_json AS GraphJson, created_at_utc AS CreatedAtUtc …`, one alias per projected column, every time. **Prevents:** a read that throws at run time rather than at compile time — and, where the caller swallows the exception so a failed read cannot block startup, a silent zero-row answer that looks exactly like "there was nothing to read". The one-shot Open Canvas import is the case that made this expensive: the read runs immediately before `DropCanvasWorkflows`, so an unaliased column would have destroyed every canvas behind one log line. **Authority:** `CanvasWorkflowImport.ReadAsync` and the older `NodeChatTitleEncryptionBackfillService`, which aliases every column for the same reason.

Corollary for any swallowing caller: log a read failure at **Error** with the exception type. "The read threw" and "there were no rows" must never look alike in the log.

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

### Single-message validation exceptions are mapped globally to 400 — don't re-add per-endpoint catches

`DomainValidationExceptionHandler` maps the single-message validation exceptions to the FastEndpoints 400 shape; the type list in its own `is not (…)` pattern is the current inventory, never a name or a count quoted from here. Add new single-message validation there. Keep multi-error `GraphWorkflowValidationException` local, and keep out any type whose status is not 400 at EVERY endpoint that raises it — `DevelopmentWorkspaceSecurityException` is the worked example (409 on patch/next-action, 400 on register/create/reconnect). The aggregate `SelectedFolderValidationException` stays out for the same reason but is no longer endpoint-local: it has its own `SelectedFolderExceptionHandler`, which splits the family by type (404 / 409 / 400) in one place.

### `DevelopmentWorkspaceSecurityException`: 400 where the request carried the value, 409 where persisted state blocks it

Caller-supplied folder defects are 400. Persisted project/trust/identity/apply state blocking a valid request is 409 through `DevelopmentRepositoryStateConflictException`. The derived type preserves existing base catches. Mixed-surface reconnect distinguishes the two.

### Knowledge repository import: only `…ImportRejectedException` is a 400

Caller-fixable bound/unavailable failures use `KnowledgeRepositoryImportRejectedException`. Host/index/read/race failures use `KnowledgeRepositoryReadException` and remain server errors. Never widen the endpoint catch to `InvalidOperationException`.


`DevelopmentRepositoryStateConflictException` derives from `DevelopmentWorkspaceSecurityException` so availability probes, summary degradation, and attempt reason handling keep their base catches. Reconnect is the mixed endpoint: a bad newly selected folder is 400, while stale persisted trust/repository identity is 409. Status follows the request surface, not the shared guard throw site.


`DomainValidationExceptionHandler` emits the same FastEndpoints error shape as the removed local catches: `errors[{name:"generalErrors",reason}]`, matching detail, request path, and trace ID. `GraphWorkflowValidationException` remains local because it carries a `GraphWorkflowValidationResult` — a LIST of `(key, message)` pairs, keyed to the node or edge each failure belongs to (null for a whole-document failure) — and the four Graph Workflow endpoints replay them one by one so the editor can draw each on its own element; collapsing them into the global handler's single sentence would lose the keys the canvas renders. `SelectedFolderValidationException` mixes 400/404/409, so it is split by `SelectedFolderExceptionHandler` rather than folded into the single-message 400. `SlashCommandConflictException` stays 409 and stays local: it is built as `Results.Problem(409, title: message)`, and `ConflictExceptionHandler`'s envelope puts the message in `detail` with `title` "Conflict" plus a `conflictType` member — moving it is a wire change needing a regenerated client, not a relocation.

### Before deleting a `.Produces<T>(status)`, trace every exception family the route can raise

**Rule:** a never-constructed `*BlockedResponse` DTO can go, but its `.Produces<>(409)` only goes once you have
grepped the route's whole call chain for every exception type and checked which handlers `ConfigureServices`
registers for them; then pin the declared statuses in `OpenApiDocumentTests` with its `AssertResponses(paths, path,
verb, [...])` idiom. **Prevents:** turning a wrongly-typed declaration into an undeclared one, which is worse — no
typed SPA handler can be written for a status the spec does not mention. On `CreateTrainingRunEndpoint`
`TrainingRunBlockedResponse` was dead, so the DTO and the 409 went together; but `TrainingRunStore.CreateAndEnqueueAsync`
still throws `TrainingConflictException`, which the global `TrainingExceptionHandler` maps to a `TrainingErrorResponse`
409. ADR 0009's prose ("the route answers with a 400, so the 409 could not occur") was false before the change and was
believed by the implementer, the brief and the diff review; an independent review in a separate lane caught it.
`pnpm run openapi:check` structurally cannot: it regenerates the client FROM the committed spec and agrees with a
wrong spec happily, and behaviour tests pin the throw and the envelope, not the declaration. **Authority:**
`TrainingExceptionHandler`, `OpenApiDocumentTests`; 2026-09-10 cleanup review.

### A declared 413 is thrown by Kestrel inside model binding, so it needs a handler, not an early exit

**Rule:** `IRequestSizeLimitMetadata` on a route is enforced by Kestrel **as it reads the body**, which happens inside FastEndpoints model binding — before `HandleAsync` runs. The refusal is a `BadHttpRequestException` carrying status 413, so the handler-side `IsOversized` Content-Length check never fires on a real connection; it only covers a client that declares its size honestly. `RequestBodyTooLargeExceptionHandler` maps that exception to the declared problem body, discriminating on the **status** rather than the type, because `BadHttpRequestException` is also how the host reports a malformed request line, a bad chunk and an over-long header — all 400s belonging to other handlers. It is registered immediately before `DefaultExceptionHandler` (order pinned by `ExceptionHandlerRegistrationOrderTests`). **Prevents:** shipping a route that declares 413, answers 500 on the only refusal path a real connection takes, and passes every in-process test — the in-memory host does not honour the metadata, so nothing below a real Kestrel can see it. Until S7 both capped families did exactly that, the graph-workflow four at 1 MiB and the development-workflow two at 2 MiB. **Authority:** `RequestBodyTooLargeExceptionHandler`, its registration in `ConfigureServices`, `RequestBodyTooLargeExceptionHandlerTests` and `ExceptionHandlerRegistrationOrderTests`; the path itself is only observable in `RequestBodyLimitE2ETests` (opt-in E2E project), and the S7 live round, 2026-09-07 §C, is what found it.

**Both refusal paths now write the SAME answer — status, headers and body (S8).** `RequestBodyTooLargeProblem.WriteAsync(HttpContext, detail, ct)` (`Client/ExceptionHandling/`) is the single writer: it sets the 413, the `application/problem+json; charset=utf-8` content type and the body, the exception handler awaits it when Kestrel refuses mid-read, and `Result(detail)` returns a private `IResult` whose `ExecuteAsync` calls the same method for the `Send.ResultAsync` the endpoint's own Content-Length exit makes. `Results.Problem` was dropped on purpose: it writes the bare media type and serializes the body itself, which makes it a second emitter of this answer. Only `detail` differs — the host can say nothing but "too large", an endpoint names the cap it enforces. The size-limit types lost their error sink with it: `GraphWorkflowRequestSizeLimit.RefuseIfOversized(HttpRequest, IValidationErrors)` and the Dev equivalent are now `IsOversized(HttpRequest)` beside an `OversizedDetail` string, with the Content-Length semantics unchanged — an absent Content-Length is still not a refusal. **Prevents:** one declared status answered in two shapes. The six capped routes declare `ProducesProblem(413)`, the ASP.NET shape, while the endpoint half sent FastEndpoints' `errors[]` body through `Send.ErrorsAsync(413)` — a contract lie the generated hey-api client encodes, and one that asserting the status alone cannot see. The OpenAPI document did not change, because the declaration never moved: this is a body fix, not a contract change (live regen plus `openapi:check` gave a zero diff). The 400 paths on these routes are untouched and correct — they write FastEndpoints' `errors[]` body and declare FastEndpoints' `ProducesProblemDetails(400)`, the pairing `ProblemDetailsProducesExtensions` documents; S7's reading that some of them declare the ASP.NET shape on 400 was not true at the tip. **Authority:** `RequestBodyTooLargeProblem`, and `RequestBodyTooLargeAssert.DeclaredProblemShapeAsync` (`Tests/Testing`), which compares the whole content-type string rather than the media type alone and is what the shape is asserted with in `GraphWorkflowDefinitionEndpointTests.GraphRoute_WithABodyOverTheCap_Returns413AndNeverReachesTheStore`, `GraphWorkflowRunEndpointTests.StartRun_WithABodyOverTheCap_Returns413InTheDeclaredShape` (start-run had no 413 test before) and `DevWorkflowEndpointTests.DefinitionRoute_WithABodyOverTheCap_Returns413AndNeverReachesTheStore`, plus `RequestBodyTooLargeExceptionHandlerTests` for the host half and `RequestBodyTooLargeProblemTests.Result_WhenExecuted_WritesTheSameAnswerTheHostRefusalWrites`, which pins the endpoint half against a bare `DefaultHttpContext`.

### Client conventions

- Use `DialogShell` for modals.
- Chat attachment capability flags are static in `NodeCapabilities.ts`, not backend-composed.
- Bounded Mantine inputs that persist “unset” need a post-mount ready guard; Mantine can fire a default/min `onChange` on mount.
- Code/text editing uses shared Monaco `CodeEditor`. Import `editor.api` + selected grammars, never `editor.main`. Keep the manual chunk name aligned with `lazyEditorJavaScriptBytes` budget. Chat streaming stays on lightweight `CodeBlock`.


Monaco stays behind shared `CodeEditor`: import `editor.api` and chosen Monarch grammars, never `editor.main`. The manual Vite chunk name is part of bundle-budget classification; changing it without the `lazyEditorJavaScriptBytes` matcher moves several MiB into the app budget. Vite `?worker` keeps the editor offline. Chat `CodeBlock` remains lightweight because it rerenders per streamed token.


A bounded Mantine `NumberInput`/`Slider` that distinguishes “unset” from override needs a post-mount `ready` guard before persistence. Mantine can emit min/default on mount and overwrite a deliberate null. Capability flags for file/image chat input remain static client constants; do not wait for a backend capabilities endpoint that is not part of this contract.

### A test asserts the bundle string, never an in-code `defaultValue`

**Rule:** i18n is initialised ONCE for the whole suite — `src/i18n.ts` is the first vitest `setupFiles` entry, and
`i18n.ts` calls `.use(initReactI18next)`, which registers the instance as react-i18next's default. So `useTranslation()`
resolves against the real `en` bundle in every test file, with no provider in the tree and regardless of which wrapper
the file renders through. Assert the string the operator reads; an assertion pinned to the in-code `defaultValue` is
asserting the fallback, not the product. `scripts/CheckI18nDefaults.mjs` fails lint on a `t()` call whose default has
drifted from `en.json` or is missing, which is the other half of the same guarantee. A file that
`vi.mock("react-i18next")`s wholesale may use `renderWithProviders` (the mock applies to the components' imports; the
instance initialised in setup is untouched), but it then renders its own defaults again and gives up this coverage.
**Prevents:** a green test over a string the app renders differently — a stale default, a raw key with no default at
all, or a literal `Step {{index}} of {{count}}` that real i18next interpolates. Before the suite-wide init, 76 files
hand-rolled a wrapper and never saw the bundle. Side effect to know: `i18next-browser-languagedetector` writes
`i18nextLng` into localStorage during `init()`; the cache is NOT disabled because production seeds
`UserLanguageStore` from that key, so a test asserting an empty localStorage clears it itself. Related: the
`"New conversation"` literal in `Chat.tsx` must NOT be translated; it is persisted and compared by exact string, so
localising it breaks the untitled-conversation check across a language switch until the contract carries an
`isUntitled` flag. **Authority:** the `setupFiles` comment in `vite.config.ts` and `RenderWithProviders.tsx`'s header;
the measured suite run that turned 7 hidden defects green-to-red, 2026-09-13.

### A date goes through `formatTimestamp` or `formatTime`, never through a bare `toLocaleString()`

**Rule:** `formatTimestamp` and its time-only sibling `formatTime` (`core/formatting/TimeFormatting.ts`) format an instant in the ACTIVE i18next language, and every date or clock rendering goes through one of them. Both take `number | string | null | undefined`, because half the wire carries epoch millis and half an ISO string and most generated timestamp fields are optional. **Prevents:** a session switched to German rendering German labels beside US-ordered dates — `toLocaleString()` reads the machine's regional setting and knows nothing about i18next — and the literal "Invalid Date" landing in a table row, which the helper answers as a dash. **Authority:** `formatTimestamp` and `TimeFormatting.test.ts`; it is also why `core` may import i18next directly, the same reason `ApiErrorMessage` and `Toast` do.

An uninitialised i18next answers `undefined`, which is exactly the argument meaning "the environment's default", so a test or an early render behaves as it always did. A hand-edited `i18nextLng` holding a malformed tag (`en_US`) throws a `RangeError` **per row**, so the call is wrapped and falls back to the default. The rule is now repo-wide: every bare `toLocaleString()`/`toLocaleTimeString()` date site imports a helper, the local copies in agents, benchmarks, knowledge, model-fit, diagnostics and `GgufFormatters.ts` are gone, and the `features/devWorkflows` sites are in scope too under an explicit operator ruling. The known remainder is the handful of sites that pass explicit `toLocaleDateString`/`toLocaleTimeString` **options** — absorbing them needs an options parameter, which is a separate decision, so they still follow the browser locale. Grep for `toLocale` under `src/` to see the current list rather than trusting one written here. Never date an absent instant with `?? 0`: that renders 1970 as if it were data, and the helper's dash is the honest answer.

### A `DialogShell` opened over another `DialogShell` needs `raised`, never a z-index literal

**Rule:** a dialog that stacks over another one passes `raised` to `DialogShell`; the component owns the single raised layer. **Prevents:** the confirmation rendering UNDERNEATH the editor it is confirming. Mantine portals every modal into one shared node, so two dialogs at the same z-index are ordered by insertion and the one underneath wins whenever it was remounted last — a `key` on the outer editor is enough to cause it, which is how the benchmark re-judge confirmation disappeared. **Authority:** `DialogShell` and `DialogShell.test.tsx`; `ConfirmProvider` and `BenchmarkConfirmationDialog` are the two call sites.

Fixing it at a call site with a z-index number leaves the next stacked dialog to rediscover the trap, and two call sites free to pick different numbers.

### A workflow hub's `kind` is LOWERCASE on the wire and is asserted literally on both sides

**Rule:** `graphWorkflowChanged` carries `kind` as one of the literals `run`, `node`, `gate`, written out by hand in `GraphWorkflowEventPublisher.ToWireKind` rather than derived from the enum name, and matched against the same literals in `useGraphWorkflowRunHub`. **Prevents:** a casing slip that silently stops every query invalidation — the client's `switch` matches no arm, nothing refetches, and there is no error anywhere. **Authority:** `GraphWorkflowEventPublisher.ToWireKind`, the `[Arguments(GraphWorkflowChangeKind.Run, "run")]` cases in `GraphWorkflowEventPublisherTests`, and the literal array in `useGraphWorkflowRunHub.test.tsx`. Same trap as `DevWorkflowEventPublisher`.

Renaming an enum member must not be able to change a wire contract by accident, which is exactly why the mapping is written out. The payload is otherwise content-free — `(runId, seq, kind)` — so the client re-reads the named feed from its own watermark and a dropped push degrades to a late read.

### hey-api's generated `queryFn` builds its request from `queryKey[0]`, not from the options it closed over

Re-using a generated `*Options()` adapter for a different page inside a hand-written `queryFn` must pass that page's own `queryKey` in the context (`page.queryFn({ ...context, queryKey: page.queryKey })`), or every call re-requests the first page and a watermark loop never advances. Authority: `useIntegrationExecutionEvents` rework, pinned by `useIntegrationExecutions.test.tsx` ("calls the generated adapter once per page…").

### Races and flashes

- Auto-advance only after observing unmet → met; do not skip content already satisfied on arrival.
- Globally mounted TanStack queries require access-token `enabled` gates.
- react-joyride v3 controlled completion is final `STEP_AFTER` + `NEXT`, not `STATUS.FINISHED`.
- Add every SignalR hub to `config/signalr-proxy-paths.json`; generic proxy fallback can wedge all websockets.
- Push-only terminal reconciliation explicitly invalidates affected queries.
- Add every `InvocationState` field to `InvocationState.Clone()`; cloned snapshots, not the live object, reach persistence/UI.


SignalR hubs are listed in `config/signalr-proxy-paths.json`; add the path there rather than another inline proxy entry. A missing websocket route can wedge Vite's generic `/api` proxy and break existing hubs. Push-only terminal handlers invalidate queries explicitly. `InvocationState.Clone()` is the single deep-copy boundary used by dispatcher/resume registry; any omitted field can look correct live and persist null.

### `signalr:check` only diffs hub route strings, never method signatures

**Rule:** `pnpm run signalr:check` compares `MapHub<T>(route)` path strings against
`config/signalr-proxy-paths.json` only; it does not diff hub method signatures. **Prevents:** adding a hub path
without the JSON entry fails it AND silently breaks that hub's WebSocket upgrade in `vite dev` — it works on the
app origin and is dead on the Vite origin, with no error from the script naming why. **Authority:**
`CheckSignalrProxySync.mjs`; the `vite.config.ts` proxy comment.


Auto-advance tracks an explicit armed state: reset on step change, arm only after observing unmet, and advance only after a later met observation. This prevents returning users from flashing through content already satisfied. Globally mounted queries remain disabled until an access token exists; otherwise the pre-login 401 can remain cached after authentication. Controlled react-joyride v3 completes on final `STEP_AFTER`/`NEXT`, not `STATUS.FINISHED`.


### A gated-off Ollama runtime is a UI state, not an empty list

**Rule:** with `XE_OLLAMA_RUNTIME_ENABLED=false` the no-op runtime answers "nothing there" — available, empty list (§2) — which renders as an ordinary idle daemon, so both surfaces branch on the `ollamaConfigured` flag instead. The loaded-models page renders a dedicated dimmed `EmptyState` (`data-testid="loaded-models-runtime-disabled"`, key `pages.loadedModels.ollama.disabled`) and guards its three existing branches with `ollamaConfigured` so all four stay mutually exclusive; it reads the flag straight off the running-models snapshot it already holds. On node settings the *page* calls `useOllamaRuntimeConfigured` (in `src/core/runtime/hooks/`) and threads `ollamaRuntimeDisabled` down through `NodeSettingsFieldsCard` to `NodeSettingsRuntimeCard`, which stays presentational and replaces the Ollama endpoint input with a dimmed line (`node-settings-ollama-disabled`, key `pages.nodeSettings.fields.ollamaEndpoint.disabled`). The hook is its own query over the same running-models endpoint at infinite `staleTime`, and lives in `core/` rather than being imported from the loaded-models feature, because dependency-cruiser's `no-cross-feature` rule forbids that edge and the fingerprint baseline is no-growth. Both callers FAIL OPEN: only a definite `false` changes anything, so a loading or failed probe leaves today's UI intact. Only the input is hidden, never the form model, so the stored endpoint still round-trips on save. **Prevents:** an operator hunting for a daemon this node will never start — and, by keeping the call at the page, a card that fetches. Only `NodeSettings.test.tsx` still stubs the hook, because the probe reaches the generated SDK whose axios interceptors import the app router; the two card tests pass the boolean and mock nothing. **Authority:** `LoadedModelsPage.ollama.test.tsx`, `NodeSettingsRuntimeCard.test.tsx` and `NodeSettings.test.tsx`.
### PROPOSED (awaiting operator approval): an AudioWorklet is ONE self-contained `.js` file imported with `?url`

**Rule:** a worklet processor ships as a single plain `.js` file with **no `import` statement of any kind**, pulled in
for its URL alone (`import workletUrl from "…/Pcm16DownsamplerWorklet.js?url"` in `PcmCapture.ts`). Vite's `?url` goes
through the asset plugin, which emits the file's raw bytes and does **not** traverse its module graph, so a `.ts` or
`.js` sibling would ship as an unresolved specifier with nothing emitted beside it. Keep the class **and** its
`registerProcessor` call inside `typeof AudioWorkletProcessor !== "undefined" && typeof registerProcessor === "function"`
— `extends AudioWorkletProcessor` is evaluated when the class declaration executes, so a class at module scope throws
`ReferenceError` under vitest before any registration guard could run; with the guard, a test can import the shipped
file and exercise the real arithmetic. Verify on the emitted asset, not the source: `grep -c "import" dist/assets/*Worklet*.js`
must be **0**, which is also why the file's own prose must avoid that word. **Prevents:** `registerProcessor` never
running, `AudioWorkletNode` construction throwing, and the whole capture path failing in a browser as a generic
"worklet-failed" while `tsc`, biome and `vite build` are all green — nothing in the gate chain instantiates a worklet.
**Authority:** S4 plan §2.3 (R37/R37a); `capture/Pcm16DownsamplerWorklet.js`; `capture/Downsample.test.ts`; the C2 and
C4 dist greps in `Plans/audio-transcription-2026-09-11/progress/S4-notes.md`.

### PROPOSED (awaiting operator approval): a worklet node needs a path to the destination, and a pre-gesture context starts suspended

**Rule:** connect the processor through to the context's destination — `source → worklet → zero-gain → destination`
with `gain.value = 0` — instead of constructing it with `numberOfOutputs: 0`. Then, because an `AudioContext` created
before the page's first user gesture starts *suspended* under the autoplay policy, `await context.resume()` when
`context.state === "suspended"` (a no-op on a running context). **Prevents:** two independent silent failures that
look identical from the UI — a node nothing pulls, and a suspended graph — where capture reports "capturing", no frame
is ever posted, no partial appears and no error is raised anywhere. **Authority:** `capture/PcmCapture.ts`
(`startPcmCapture`); S4 plan §2.3 step 5; commit `ae37b424b`.

### PROPOSED (awaiting operator approval): `CheckDependencyBaseline.mjs` fails on a new `warn` fingerprint, so a `no-orphans` warning is a gate failure

**Rule:** `evaluateDependencyBaseline`'s `additions` fail the run for **any** new violation fingerprint, `severity: "warn"`
included, so a file depcruise reports as an orphan reds `pnpm run validate` exactly like an error would. When a new
`.js` under `src/features/` has no incoming edge the cruise can see, the sanctioned fix is **one targeted `pathNot`
entry on the `no-orphans` rule** in `.dependency-cruiser.cjs` — a statement about what counts as a legitimate orphan,
the same category as the existing `.d.ts` and generated-client entries — and **never** a new fingerprint in
`config/dependency-baseline.json`. Note that a test file's import is not an edge: `options.exclude.path` ends in
`\.test\.(ts|tsx)$`, so the cruise never sees it. In S4 the rule did not fire, because depcruise resolves
`./Pcm16DownsamplerWorklet.js?url` after stripping the query and the import in `PcmCapture.ts` is a real incoming edge;
`.dependency-cruiser.cjs` was therefore left untouched. Biome does see the file, and worklet globals need a
`**/*Worklet.js` `javascript.globals` override in `biome.json` that must stay **last**: a later override's `globals`
list replaces an earlier one's, and the existing `**/*.js` override sets `globals: []`. **Prevents:** a green-looking
"only warnings" depcruise run that is actually a failed gate, a baseline widened instead of a rule exception written,
and a worklet file that cannot pass `lint/correctness/noUndeclaredVariables` on `AudioWorkletProcessor` /
`registerProcessor` / `sampleRate`. **Authority:** `scripts/CheckDependencyBaseline.mjs`; `.dependency-cruiser.cjs`;
`biome.json`'s last override; S4 plan §1a.3 and the C2 gate output in
`Plans/audio-transcription-2026-09-11/progress/S4-notes.md`.

---

## 6. Deliberately NOT built

Do not assume these exist or “restore” retired designs.

- **Turn-level budgeting and cross-turn compaction are two mechanisms; do not conflate them.** Turn-level and per-round budgeting (`ConversationContextBudgeter`, `ProviderCallBudgetChatClient`) excerpt/drop content; protected reasoning stripping ships on, protected tool-result excerpting is opt-in, rewrites preserve message metadata. Cross-turn LLM compaction is a separate mechanism (`ConversationSummarizer` + `ConversationCompactionService`), triggered manually via `POST chat/conversations/{id}/compact` (`LocalApiRoutes.CompactConversation`) and automatically by `ConversationStepContextBound` in work sessions; folds run on a node-local model only. Prevents an agent "restoring" a summarizer that already exists or routing a fold through the shared cloud-capable client. Authority: `ConversationSummarizerBoundaryTests`, `ConversationCompactionServiceTests`. One trap the 2026-09-05 live round paid for: a **single** fold can generate for a full hour and trip `LlamaServerSupervisorOptions.HttpNetworkTimeout` (`DefaultHttpNetworkTimeoutSeconds` 3600 — a per-network-operation deadline, not a cumulative one), surfacing as an unhandled 500 out of `CompactNodeChatConversationEndpoint`. The abort is clean: all three `conversations` compaction columns stay NULL, so coverage never advances over an un-summarised span. Do not read that 500 as eight folds exhausting a budget.
- **Restart reconciles but does not auto-resume.** Startup marks non-terminal chat rows Interrupted with durable envelopes and stale scheduled runs Failed. `InvocationResumeRegistry` is browser-reconnect memory only. Manual Regenerate is the recovery.
- **No mid-stream retry.** Pre-first-token retry and circuit breaker exist.
- **Orchestration is handoff-only.** `AgentWorkflowBuilder.CreateGroupChatBuilderWith` and the Magentic, sequential and concurrent members of that family are deferred, not missing: the 2026-05-27 agent-mode loop architecture plan scoped v1 to handoff, and the deferral was re-confirmed on 2026-09-09 — not adopted, no current demand. Prevents each fresh assessment re-discovering it as an unstated gap. Authority: `OrchestrationAgentFactory` is the only production caller of `AgentWorkflowBuilder` and calls `CreateHandoffBuilderWith` alone; `HandoffWorkflowSpikeTests` is the only coverage of that family; operator ruling 2026-09-09 (D9).
- **Approval/HITL and tighten-only policy are live.** MCP/default high-risk tools are structurally approval-required; node policy can only add approval. Category-wide loosening is not built. Decisions are audited.
- **Scheduler can run a saved local single agent.** It strips approval-required tools, uses capacity/GPU admission, and writes a content-safe history summary. It does not write a chat conversation or compile orchestration.
- **Sandbox providers:** fake, process, and opt-in Development Docker exist. OpenSandbox is not built; consult ADR 0004 and `docs/roadmaps/development-mode-container-status.md` for current provider coverage.
- **Playbook retrieval defaults to embeddings**, with lexical fallback. Adaptive memory is per-agent; no node-wide/cross-agent sharing.
- **No RAG over chat attachments.** V1 uses file tools or capped inline text; no image/OCR ingestion.
- **STT ships; Kokoro does not.** Speech-to-text is whisper.cpp behind `IWhisperTranscriber`, with batch upload, live sessions and browser capture (`docs/wiki/24-audio-transcription.md`). TTS is still browser Web Speech, and Kokoro is not shipped.
- Desktop-only ThemeConfigurator is outside the mobile-responsive scope. Open Canvas used to carry the same exclusion and is gone — see the stale-beliefs table.
- **Open Canvas (Preview) is removed and is not coming back.** Graph Workflows replaced it; saved canvases were converted once at startup. Do not "restore" `PreviewWorkflow*`, `features/preview/`, the `preview/*` routes or the `canvas_workflows` table. Prevents: re-adding a retired surface while chasing a dangling reference. Authority: `docs/wiki/21-graph-workflows.md` §9.


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
| There is no speech-to-text in this repo. | whisper.cpp ships: batch upload, live sessions over `TranscriptionHub`, and browser capture in the SPA (§6). |
| Open Canvas / Preview is the visual workflow builder. | Removed in favour of Graph Workflows; saved canvases were imported once at startup (§4). |
| `nodeCapabilities.preview` gates a route. | The flag, the route and the feature are gone; `graphWorkflows` is the successor and is on by default (§5). |
| Desktop-only ThemeConfigurator/Open Canvas are outside the mobile-responsive scope. | Open Canvas is gone; only ThemeConfigurator carries that exclusion (§6). |
| Context management is truncation only; no cross-turn LLM summary exists. | `ConversationSummarizer` + `ConversationCompactionService` fold older turns into a synopsis on a node-local model, manually via `POST chat/conversations/{id}/compact` and automatically in work sessions (§6). |
| CI runs test projects sequentially. | Projects run concurrently with separate result directories; the main Tests module uses grouped batch runner (§1). |
| Memory-safe runner defaults to `JOBS=4`; increase in-process width. | The default tracks the box — `JOBS=16` on a host with at least 32 CPUs, `JOBS=10` below that — and process batches, not in-process width, provide the useful concurrency (§1). |
| Coverage should use one process per namespace. | Coverage instrumentation makes that prohibitively expensive; group instead. A local run uses `TEST_GROUPS=$(nproc)`; CI uses `TEST_GROUPS=16` split across 4 `TEST_SHARD` legs (§1). |
| Development Mode must be unrestricted because restore needs network. | A short warm sandbox restores, then the agent-facing sandbox requests deny-egress where supported (§2). |
| WSL has no GPU (or a specific older card). | Hardware changes between verifications; always query live (§2). |
| llama.cpp native MCP should replace the app client. | Its MCP proxy is for llama-server's browser UI; keep the .NET MCP integration and approval boundary (§3). |
| Ollama was removed. | Only Aspire auto-orchestration was removed; Ollama remains opt-in (§2). |
| `aspire stop` necessarily leaks and only 13.5+ fixes it. | Leak did not reproduce on tested versions, but `dev-stop.sh` remains sanctioned until the trigger is understood (§2). |
| NVFP4 is unsupported. | NVFP4 GGUF works; NVFP4 safetensors conversion does not (§3). |
| Inbound MCP key is recoverable. | Only a SHA-256 digest is stored; plaintext is returned once on generation (§2). |
| `dotnet test` cannot discover MTP tests. | `global.json` pins MTP; `dotnet test` works (§1). |
| Any build runs analyzers. | Analyzer wall is Release-only locally (§1). |
| `RunAnalyzers=false` disables generators. | It skips diagnostic analyzers only (§1). |
| `if (!OperatingSystem.IsX()) return;` is an acceptable platform guard. | It reports a green pass on every platform that cannot run the test; use TUnit's `[RunOn(OS.Linux)]` / `[ExcludeOn(OS.Windows)]` or `Skip.Test` (§1). |
| Green E2E proves frontend typecheck. | E2E uses Vite-only `build:e2e`; run `pnpm run lint` (§1). |
| Browser E2E is entirely sequential. | It uses disjoint serial and pooled phases (§1). |
| TUnit alternation silently matches zero. | `(A|B)` returns the union; verify with `--list-tests` (§1). |
| Harmony reasoning must remain undetected. | It is detected as distinct `native_reasoning`, mutually exclusive with graded thinking (§2). |
| Development Docker is unbuilt / now required. | It is shipped opt-in and not the default; the container status record is canonical (§2). |
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
| A benchmark project asks one question. | A project holds 1..N task items; a single item is the degenerate case (§3). |
| A run is the unit that ranks. | The CELL ranks — one model x KV type x repeat over the whole item suite — and a run reports its cell's rank (§3). |
| A cell exists only when there is a repeat group. | A cell exists whenever one freeze produces more than one run per model; `cellGroupId = repeatGroupId ?? (leafItems > 1 ? new : null)` (§3). |
| Repeats are the only reason to launch more than one run per model. | A suite fans out one run per leaf item, and the probe cases of a NIAH generator each count (§3). |
| A user score rescues any excluded run. | It sits below `item-revised` and `item-set-revised`: an operator cannot notice that a question or the suite around it moved (§3). |
| Fidelity is measured once per repeat group. | Once per CELL — the item half of the rule is the lowest `task_item_index`, and it is expressed in three places that must stay in step (§3). |
| A verifiable criterion is decided by the policy config alone. | The judge resolves `item override ?? policy config` for both the verifier config and the reference answer (§3). |
| A verifier that cannot run scores 0. | It fails the judging and the run is excluded as `verifier-unavailable`; only a TIMEOUT is a real 0 (§3, §4). |
| The eligible-model listing re-hashes GGUF members. | Listing trusts recorded registry facts; only the freeze re-hashes (§3). |
| Export schema is 3. | 4 since task suites — `taskItems[]`, `cells[]` and six appended CSV columns (§3). |
| The graph-workflow run GET carries no graph, so a run view draws the definition's. | `GraphWorkflowRunResponse` carries the run's PINNED graph; the run view draws that and a definition edited since is only a notice (§4, wiki 21 §5). |
| A run view falls back to nodes-only whenever `run.graphHash` and `definition.graphHash` disagree. | Only a response carrying no graph at all falls back; a hash mismatch alone is informational (§4). |
| An authored `Agent → Pause → Agent` chain has no affordance and only the importer adds the context edge. | The editor adds it on the connect gesture and the validator warns about the starved node (§4). |
| `WorkSessionAgentSeeder` allow-lists the clock tool as `get_current_time`. | The registry derives the name from the method: it is `GetCurrentTime` (§4, wiki 19). |
| Whether an Agent node's `responseJsonSchema` is enforced by a grammar, or merely obeyed by the model, is unproven. | Enforced from generation start, proven live with a negative control and the compiled GBNF captured verbatim (§2). |
| A `maxLength` in a response schema never reaches llama.cpp, because the MEAI OpenAI strict-schema transform rewrote it into a `description` first. | True until S7 and still true of every other provider. On llama-server the schema is now written onto the request body by `DeferredLlamaServerChatClient.ApplyResponseSchemaPassthrough` and the transform is never invoked, so the bound is compiled into the grammar and enforced (§2). |
| The Open Canvas importer's pause context edge follows a looser rule than the editor and the validator. | All three apply the same rule and the same three guards; only WHEN they run differs (§4, wiki 21 §9.2). |
| A node cannot start with `XE_OLLAMA_RUNTIME_ENABLED=false`. | It can since S8; the gate-off branch registers a no-op `IModelCapabilityClient` AND a no-op `IOllamaModelService` (§2). |
| `MaxNodesPerDefinition` is applied after the parse, so the parser needs no cap. | `GraphWorkflowGraph.Parse` takes the cap and refuses on the declared node count before anything is read (§4, wiki 21 §2.4). `DevWorkflowGraph.Parse` does the same since S7, at 500 nodes. |
| Dev Workflows accepts a definition of any size, and the node cap there is a known-unfixed finding. | Fixed in S7: `DevWorkflowOptions.MaxNodesPerDefinition` (500) is enforced inside the parse and `DevWorkflowRequestSizeLimit` caps create and update at 2 MiB (§4). |
| A capped route's 413 comes in FastEndpoints' `errors[]` shape when the endpoint refuses it itself. | Both the host's and the endpoint's refusal write the same ASP.NET `ProblemDetails` body via `RequestBodyTooLargeProblem` (§5). |
| Date sites outside `formatTimestamp` were deliberately left on the browser locale. | No site is. S7-C moved the last four — the chat clock, the conversation-list day, the model-fit catalog release date and the usage dashboard's day label — onto `formatTimestamp`/`formatTime` through their optional `Intl.DateTimeFormatOptions` parameter (§5, wiki 10). |
