# Backend test runner

Scope: running TUnit/MTP backend tests: scoping and filters, the batched runner and build lock, measurements, flakes
and parallelism. Fixture, host, temp-state and SQLite rules are in
[backend-tests-conventions](backend-tests-conventions.md); test principles live in `docs/wiki/17-writing-tests.md`
and gate commands in `AGENTS.md` ("Validation"). Read when: scoping, running or debugging a backend test run, or
changing `scripts/run-tests-memory-safe.sh`, `scripts/run-backend-tests.sh`, `scripts/test-durations.py`, the build
lock, or the parallelism limits.

## Running and scoping

### Running backend tests

**Rule:** when batching in bash, never name a variable `GROUPS` (reserved: bash's group-id array); the runner's knob is `TEST_GROUPS`. **Prevents:** a batch loop silently reading the shell's group ids. **Authority:** `scripts/run-tests-memory-safe.sh` (`TEST_GROUPS`).

### `--list-tests` prints METHOD names only, so a union filter is confirmed one class at a time

**Rule:** `--list-tests` prints bare method names, so grepping it for a `(ClassA|ClassB)` alternation finds nothing. Confirm a union filter with one `--list-tests` per class (`/*/*/<Class>/*`) and read each `Discovered N tests` line. **Prevents:** a misspelled class silently contributing nothing: the alternation never exits 8 while one name matches. **Authority:** the `--list-tests` output of the test executable.

### the `Architecture` guards are a NAMESPACE, so a class-name treenode filter never reaches them

**Rule:** treenode paths are `/<assembly>/<namespace>/<class>/<method>`; run the guards with `--treenode-filter '/*/*.Architecture/*/*'`. `'/*/*/Architecture*/*'` matches class names, runs none of them, and still exits green if another alternation matched. Confirm with `--list-tests`. **Prevents:** a scoped run that claims guards it never ran. **Authority:** namespace `XE_Local_AI_Engine.Tests.Architecture`. [evidence](../agent-knowledge-evidence.md#the-architecture-guards-are-a-namespace-so-a-class-name-treenode-filter-never-reaches-them)

### a test that builds `Host.CreateApplicationBuilder` is `Integration`, however small

**Rule:** a DI-composition test that calls `Host.CreateApplicationBuilder()` (or `WebApplication.Create*`, `AddNodeApplication`, `AddNodeModelRuntime`) carries `[Category(TestCategories.Integration)]`, even when it only lists `builder.Services` descriptors and starts nothing. **Prevents:** a `Unit` registration-order test passing its own filter, then failing the gate's Architecture lane. **Authority:** `TestCategoryConventionTests` (scans `Unit` classes for those primitives); `RuntimeAuditCudaDeviceProbeCompositionTests`.

### Verify against the whole module, not just the class you touched

**Rule:** before hand-off, run the whole test project you changed: it is the only local check that unrelated host construction still works. A composition factory reading optional store or config state null-guards it (`Load()?.X`): test substitutes return null. **Prevents:** static caches, host state or order failing outside the edited class, unseen until a gate. **Authority:** the incidents in the evidence ledger. [evidence](../agent-knowledge-evidence.md#verify-against-the-whole-module-not-just-the-class-you-touched)

### A relative test-project name in a worktree can resolve to a SIBLING worktree's binary

**Rule:** name the test project by its absolute `.csproj` path and prefix the run with `MSBUILDDISABLENODEREUSE=1`: a relative name can resolve, through a worker node started under a sibling checkout, to that checkout's output. **Prevents:** a green-looking run that never executed the code under the cwd. **Authority:** `build-and-analyzers.md` ("A Dev-mode sandbox run leaves MSBuild worker nodes holding a dead `NUGET_PACKAGES`"). [evidence](../agent-knowledge-evidence.md#a-relative-test-project-name-in-a-worktree-can-resolve-to-a-sibling-worktrees-binary)

## Batched runner, build lock and measurements

### The build lock is not a FIFO queue

**Rule:** `scripts/with-build-lock.sh` waiters retry `flock -w` in bounded attempts, so the waiter that wakes first after a release takes the lock, not the one that waited longest. Read the waiter list from `scripts/build-lock-status.sh` before queueing, and never plan gates on arrival order. **Prevents:** a hand-over or multi-lane schedule that assumes an earlier waiter runs first and a later arrival that overtakes it mid-plan. **Authority:** `scripts/with-build-lock.sh` (waiter loop); `scripts/build-lock-status.sh`.

### a batched-module red keeps only 3 grep'd lines, so capture the failure detail on the FIRST red

**Rule:** when `XE-Local-AI-Engine.Tests` reds inside the gate, only the three printed `[<namespace>] …` lines survive. Before re-running the gate, re-run the failing namespace or class targeted (or set `COVERAGE_DIR`) if the exception detail matters. **Prevents:** promising a reviewer exception text that is already gone. **Authority:** `scripts/run-tests-memory-safe.sh` (`run_ns`, the `RESULTS_DIR` trap). [evidence](../agent-knowledge-evidence.md#a-batched-module-red-keeps-only-3-grepd-lines-so-capture-the-failure-detail-on-the-first-red)

### An ungrouped local coverage run reds a namespace that covered no product code

**Rule:** with `COVERAGE_DIR` set and `TEST_GROUPS` unset, a `<namespace>(no-coverage-report)` entry means that unit exercised no product assembly, not that a test failed: check its pass/fail counts first. Never make the runner tolerate empty reports: one can be a real gap. **Prevents:** debugging phantom reds, or letting a real empty report pass. **Authority:** `scripts/run-tests-memory-safe.sh` per-unit verdict loop (`cov` field of each `.result`) and its header note.

### Re-measure a TRX set with the script, never quote a class or namespace count from a document

**Rule:** derive timings and counts by running `scripts/test-durations.py` over a TRX set (`--heavy` for the runner's `HEAVY` lines, `--runs N` for a per-run mean, `--counts` for classes, tests, namespaces and files); cite the script, never a number in a comment or doc. **Prevents:** frozen measurements going stale. **Authority:** `scripts/test-durations.py`; `scripts/tests/test_test_durations.py`. [evidence](../agent-knowledge-evidence.md#re-measure-a-trx-set-with-the-script-never-quote-a-class-or-namespace-count-from-a-document)

### run build and test inside ONE build-lock invocation in a shared worktree

**Rule:** in a worktree several workers share, run the build and the `--no-build` test executable as one locked command: `scripts/with-build-lock.sh -- sh -c '<build> && <test exe …>'`. Two locked calls release the lock between them, and another worker's build can replace the binaries in the gap. **Prevents:** grading another worker's half-finished tree as yours. **Authority:** `scripts/with-build-lock.sh`.

### A concurrent `dotnet build` corrupts a test run — and the result is then neither pass nor fail

**Rule:** wrap a direct `dotnet test` or treat its result as provisional; rerun after an IDE background build; never wrap a whole validator in one outer lock; a lock helper marks its descriptor close-on-exec before MSBuild; never wait on `pgrep -f "dotnet (build|test)"`: match `dotnet-root/dotnet (build|test)`, or use the lock. **Prevents:** phantom reds and greens. **Authority:** `scripts/with-build-lock.sh`, `scripts/assembly-guard.sh`. [evidence](../agent-knowledge-evidence.md#buildtest-contamination-incident)

### Leftover build daemons starve the timing-sensitive tests — and the packaging gate is where you notice

**Rule:** before packaging, or diagnosing a suddenly slow timing test, run `dotnet build-server shutdown`. A failure lasting exactly the test's budget under load is a load signature, not a behaviour signal. **Prevents:** chasing a product defect for a starved timer. **Authority:** `AGENTS.md` "Validation" (shutdown before a gate chain). [evidence](../agent-knowledge-evidence.md#timing-test-and-build-daemon-incidents)

### The backend gate empties `.tmp/backend-test-results/` when it starts

**Rule:** never redirect a gate's own output into `.tmp/backend-test-results/`; read `gate.log` there (and the per-project `gate.log` beneath it) and take the exit code from the shell. **Prevents:** a wrapper log unlinked seconds into the run, leaving no failing test name. **Authority:** `scripts/run-backend-tests.sh`.

### Cancel the backend gate by its process group, never by its PID

**Rule:** stop `scripts/run-backend-tests.sh` non-interactively with `kill -TERM -- -<pgid>`; Ctrl-C in a terminal already signals the whole group. **Prevents:** orphaned lanes: a PID-only TERM kills the trap-less `scripts/with-build-lock.sh` wrapper, which releases the lock while the lanes keep running. **Authority:** `scripts/run-backend-tests.sh` header ("Cancellation").

### A `COVERAGE_DIR` gate runs the siblings unguarded

**Rule:** treat a sibling coverage run as blind to an unwrapped concurrent build: coverage rewrites each project's own assemblies, which the guard cannot tell from a foreign build, so siblings run unguarded. The lock still covers cooperating shells; the batched module keeps its guard. **Prevents:** trusting a coverage-run green as uncontaminated. **Authority:** `scripts/run-backend-tests.sh` header (coverage-mode exception).

### Skipping the build lock on a long lane voids its timings

**Rule:** `NO_BUILD_LOCK=1` on a long opt-in lane such as the model matrix is acceptable only when the lane builds nothing shared: it builds its own worktree once up front, and the assembly guard stays armed and reports the build output unchanged at the end, or the result is void. Sibling sessions keep building their own worktrees meanwhile, so timing numbers (time to first token, tokens per second) measured under that load are not comparable with an idle-machine run; say so in the evidence. **Prevents:** a run whose binaries changed underneath it being reported as green, and loaded-machine timings read as a regression or a win. **Authority:** `scripts/with-build-lock.sh`, `scripts/assembly-guard.sh`, `scripts/run-model-matrix-local.sh` header.

## Flakes and parallelism

### A bare test-host run must never rely on TUnit's default width

**Rule:** never launch `XE-Local-AI-Engine.Tests` or `Client.Persistence.Tests` bare over the whole module; use the gate, `scripts/run-tests-memory-safe.sh`, or `--maximum-parallel-tests`. TUnit's default is 4 x CPU cores and every live `TestServerWebAppFactory` host costs about 200 MB, so an uncapped run on a many-core machine reached over a hundred live hosts, tens of GB of RSS and thread-pool starvation (hundreds of `[slow] still running` lines). `AssemblyParallelLimit` (both assemblies) caps at 8 and catches a forgotten flag; a CLI width below it still narrows. **Prevents:** a scratch runner exhausting the machine and holding the build lock for an hour. **Authority:** `AssemblyParallelLimit`; `ParallelLimitConventionTests`; `scripts/run-backend-tests.sh` (width comment).

### The full Tests module is flaky under parallelism — verify suspects in isolation

**Rule:** rerun a named failure alone, then the module; never weaken an assertion or a `[NotInParallel]` for load: get parallelism from separate processes. `XE_NODE_SQLITE_KEY` premises conflict: `DesktopBootstrapTests` needs it unset (bare `[NotInParallel]`; never key it), `PlaybookRetrievalRankerRegistrationTests` set (keyed). Before adding a `[NotInParallel]`, find the parent-env write site: seeding host config or a child `ProcessStartInfo.Environment` is not a mutation. **Prevents:** a filtered green mistaken for the gate, and serialization for a write that never happens. **Authority:** the classes named. [evidence](../agent-knowledge-evidence.md#the-full-tests-module-is-flaky-under-parallelism--verify-suspects-in-isolation)

### A class that only EMITS on `XE.Node` needs no guard — but only after you check every listener on its instruments

**Rule:** keep bare `[NotInParallel]` on the `NodeMeterCapture` suites (meter `XE.Node`, sometimes unfiltered) and on PATH-stub suites (no overlap with real `git`/`cmake` spawns). An emitter needs no guard only if every listener on its instruments is bare-guarded or filters by name; `KnowledgeIngestionDispatcherTests` asserts lower bounds behind a name switch, so never make it exact. **Prevents:** a listener reading two tests' sum. **Authority:** the `NodeMeterCapture` suites; `KnowledgeIngestionDispatcherTests`.

### A wall-clock budget sized on an idle box is a CI flake waiting to happen — use `TestBudgets.Contended`

**Rule:** use `TestBudgets.Contended` for completion windows under contention (a failure deadline, not a sleep). Keep semantic timeouts in the product option under test; add no stopwatch ceiling unless timing is the behaviour; never widen a timeout the test expects to expire. If the operation can fault before signalling, await that task while polling; give a later cleanup post-condition its own `AssertEx.EventuallyAsync`. **Prevents:** CI flakes, and timeouts hiding the real exception. **Authority:** `TestBudgets`. [evidence](../agent-knowledge-evidence.md#a-wall-clock-budget-sized-on-an-idle-box-is-a-ci-flake-waiting-to-happen--use-testbudgetscontended)

### Adding a migration can red the module through a test that never mentions migrations

**Rule:** `NodeChatMigrationRecoveryServiceTests` applies every migration under an attempt budget that starvation cancels mid-apply, so the class is `[NotInParallel]` and a new migration can red it. Its abandoned-lock test is meant to exhaust the budget: never raise the budget to fix it. **Prevents:** a starved apply read as a migration bug. **Authority:** `NodeChatMigrationRecoveryServiceTests`. [evidence](../agent-knowledge-evidence.md#a-wall-clock-budget-sized-on-an-idle-box-is-a-ci-flake-waiting-to-happen--use-testbudgetscontended)

### `[NotInParallel]` is process-local, so a fixed path under the OS temp dir is shared by the runner's other processes

**Rule:** a path a test asserts the contents of is unique per OS process (suffix `Environment.ProcessId` or a Guid): the runner runs one process per namespace with a shared `TMPDIR`. On a recurrence, record the failure and rerun that class alone; never retry the suite until green. **Prevents:** a sibling process writing into, or deleting, the directory under assertion. **Authority:** `FrameworkTempSentinel` (`TranscriptionUploadStreamingTests`). [evidence](../agent-knowledge-evidence.md#transcription-framework-temp-flake-2026-09-1314)

### `FastEndpoints.Config.SerOpts` is process-global, so a bare-context test sees whoever booted a host first

**Rule:** a test driving a handler with a bare `DefaultHttpContext` derives the `errors[]` casing from `FastEndpointsProblemBody.GeneralErrorsName`, never a literal; host-based tests keep asserting `"generalErrors"`, the wire contract. Also: `AssertEx.Equal`'s `message` replaces the expected/actual text. **Prevents:** an order dependency disguised as a flake. **Authority:** `XE-Local-AI-Engine.Tests/Testing/FastEndpointsProblemBody.cs`; `AssertEx.Equal`. [evidence](../agent-knowledge-evidence.md#fastendpointsconfigseropts-is-process-global-so-a-bare-context-test-sees-whoever-booted-a-host-first)

### A loopback port you bound on `:0` and released is a candidate, not a reservation

**Rule:** hold-versus-retry is in `docs/wiki/17-writing-tests.md` ("Independence"). `LoopbackPort.BindWithRetryAsync` retries only on the engine CLI's exit code 6 (`DesktopPortStore.IsPortAvailable` branch of `Program`) and Kestrel's `IOException` → `AddressInUseException` → `SocketException` chain: return `null` on those, let every other failure throw, never retry on time. Never "fix" a race with an ephemeral port when the test proves a requested port is honoured. **Prevents:** a batch taking the released candidate first. **Authority:** `XE-Local-AI-Engine.Tests/Testing/LoopbackPort.cs`. [evidence](../agent-knowledge-evidence.md#a-loopback-port-you-bound-on-0-and-released-is-a-candidate-not-a-reservation)

### Never classify a cancellation from a `CancellationToken.Register` callback

**Rule:** a registration callback may signal or kill, but never classifies or throws the final exception. Classify once, at mapping time, after the awaited operation observes cancellation, in order: deliberate cancellation recorded synchronously, caller/host token, then the invocation timer by elimination; category and metric share that result. Regression tests park the stream on a gate the test controls. **Prevents:** genuine timeouts reported as Cancelled, and host cancellation mislabelled as watchdog. **Authority:** `InvocationLifecycleTracker`. [evidence](../agent-knowledge-evidence.md#never-classify-a-cancellation-from-a-cancellationtokenregister-callback)

