# Agent Knowledge Base

Hard-won rules, invariants, and traps for this repository — the things that are **not** derivable from reading the code, because they encode a bug that was already paid for once.

**Who this is for:** any agent or engineer starting work in a fresh clone. Read this before your first non-trivial change. `docs/wiki/` tells you how the system is *built*; this file tells you how it *bites*.

**Provenance:** distilled from ~135 accumulated session-memory notes spanning 2026-06 → 2026-07, each rule re-verified against the current tree at distillation time. Rules that turned out to be obsolete are recorded in [Stale beliefs](#stale-beliefs-corrected) rather than deleted — an agent that half-remembers the old rule needs to find the correction.

**Coverage is incomplete, by construction.** The 2026-08-01 documentation pass that added the `/tmp` tmpfs, GPU-smoke, `.opencode/` eval and MTP draft-model entries derived them from the **~30 most recent commits**, not from a systematic sweep of all 614 non-merge commits since `v0.1.0-rc.4.0`. A follow-up pass on 2026-08-07 folded in the then-siloed August session notes — the pooled-embedding micro-batch limit (§3), the Dev Mode read-vs-copy secret predicates and the tool-handler idle-watchdog invariant (§4), and the endpoint-exception-handling maturity rule (§5) — each re-verified against the tree at that date. Absence of a rule here is therefore **not** evidence that no rule exists — older commits very likely encode invariants that were never distilled. Treat this file as a floor, not a ceiling, and add what you find.

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

### Cite symbols, not `file:line`, for anything under active development

**Rule (operator decision, 2026-07-30).** In plans, ADRs, wiki pages and doc comments, cite `file` plus a **symbol name or a quoted phrase**. Reserve `file:line` for stable reference material that nobody is currently editing, and **never cite a line into a file your own change is touching**.

The failure it prevents: a citation that is confidently, silently wrong.

- **Five of ~84 anchors drifted in a single run**, and every one was in a concurrently-edited file. Files nobody touched did not drift.
- **Range-checking does not catch it.** A stale cite is almost always still *in range* — the file only got longer — so it points at the wrong code and reads as verified. There is no cheap mechanical check that works.
- **One anchor had drifted to a different file entirely** (a plan cited `BuildEnvironment()` in `DevelopmentWorkspaceProvider.cs`; it is a member of `DevelopmentWorkspaceTools`). No range check could ever catch that, and a reader resolves it as "the plan is wrong about the architecture", not "wrong about the line".
- **One anchor drifted twice within one run** — corrected mid-run by the lane that owned it, stale again before that run ended.
- **Symbol names survived unchanged wherever they were used.** Not one needed correcting.

Precedent: [ADR 0004](adr/0004-development-mode-container-execution-docker-stopgap.md) applied this unilaterally in its 2026-07-29 anchor-maintenance note, after two `ProcessSandboxRuntimeProvider.cs` cites drifted when that class doc was rewritten and one came to point at a bare `</para>`.

---

## 1. Build, test, CI, packaging

### Always finish with a Release build — a green Debug build is not verification

**A Debug build does not run the analyzers.** `Directory.Build.targets` sets `RunAnalyzers=false` when `Configuration == Debug` **and** `CI` is unset **and** `XE_FULL_ANALYSIS` is unset, because analyzer execution dominated the inner dev loop: measured on a full Debug rebuild of `XE-Local-AI-Engine.Tests`, **84 s with analyzers, 10 s without**.

The whole static-analysis wall — SonarAnalyzer (including the S1135 `TODO` rule below), Meziantou, BannedApiAnalyzers, and every `IDExxxx` rule from `EnforceCodeStyleInBuild` — therefore lives **only in Release**. That is deliberate and safe *because Release is what every gate builds* (the `AGENTS.md` validation commands, `.opencode/scripts/project-validate.sh` — internal-only tooling, gitignored and absent from a public clone — and `publish/package-tester-win.ps1`, now deprecated/reference-only). It is also why the last thing you do before claiming a backend change is done must be:

```bash
dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-restore
```

Skip it and you hand off code that compiles for you and fails for the packaging script. `TreatWarningsAsErrors` still applies in Debug, so genuine *compiler* warnings still stop you — it is the analyzer diagnostics that go quiet. Set `XE_FULL_ANALYSIS=1` to force the full pass in Debug while iterating on something analyzer-sensitive.

**An *incremental* Release build that returns `0 Warning(s) / 0 Error(s)` in ~1 second is a hollow gate.** MSBuild skips projects whose inputs are unchanged, and analyzer diagnostics are **not replayed for a skipped project** — so the zeros describe the last build that actually compiled, not this one. That matters most in exactly the situation you want the number for: several agents or lanes editing one tree, where you are trying to prove *the current* state is clean. If the elapsed time is implausibly short, the run proved nothing. Force a real compile before you claim a number:

```bash
dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-incremental
```

Same failure shape as the CI "zero tests enrolled" hollow gate below: a green result that was never actually evaluated.

**`dotnet run --no-build` defaults to Debug, so a targeted test run without `--configuration Release` executes a stale Debug assembly** — which may predate the tests you are trying to run, discover **zero** of them, and report "passed". Always pass `--configuration Release` to the run command, not only to the build, and read the `total:` line: **a zero-test run is not a pass.**

Two mechanics worth not re-deriving:

- **The gate must live in `Directory.Build.targets`, not `Directory.Build.props`.** `Microsoft.Common.props` imports the `.props` file **before** it defaults `$(Configuration)`, so a Configuration condition evaluated there sees an empty string and misfires in both directions. Verify any change with `dotnet msbuild <proj> -getProperty:RunAnalyzers -p:Configuration=…` rather than by reading the XML.
- **`RunAnalyzers=false` does *not* disable source generators.** It maps to csc `-skipanalyzers`, which skips diagnostic analyzers only. Confirmed against the SDK targets (`Roslyn/Microsoft.Managed.Core.targets` `_ComputeSkipAnalyzers` → `SkipAnalyzers` on the Csc task), the csc help text, and a discovery run: a Debug build of `XE-Local-AI-Engine.AI.Agent.Tests` still lists **209 tests**. This matters because a silent zero-test run is a failure mode this repo has already paid for once — check it, don't assume it, if you gate analyzers anywhere else.

### A bare `TODO` in a C# comment fails the build — in **Release**

SonarAnalyzer is referenced repo-wide (`Directory.Build.props:39`) with `TreatWarningsAsErrors=true` (`Directory.Build.props:11`), which escalates **S1135** to an error for any comment containing the literal token `TODO` (same class of rule catches `FIXME`/`HACK`/`XXX`).

**Since 2026-07-31 this fires only in Release** — see the analyzer gate above. A local `dotnet build` with no `-c` flag will happily accept a bare `TODO` and the packaging script will then reject it, so the rule has not softened, its feedback just arrives later.

Phrase deferred work as `// ... follow-up:` or `// Not yet implemented:`. See the live convention at `XE-Local-AI-Engine.Providers.Capabilities/Implementation/HardwareProfiler.cs:245,262`.

### Running backend tests

The three unit-test projects are **TUnit 1.65.x on Microsoft.Testing.Platform** (the E2E project's `TUnit.Playwright` is still pinned 1.58.0) (`<OutputType>Exe</OutputType>`). `global.json` pins `"test": {"runner": "Microsoft.Testing.Platform"}`, which bridges MTP to `dotnet test` — so plain `dotnet test` works:

```bash
dotnet test XE-Local-AI-Engine.Tests/XE-Local-AI-Engine.Tests.csproj --no-build \
  --treenode-filter "/*/*/CapabilityReporterTests/*"
```

Use `--treenode-filter`, **not** `--filter`. Class/namespace **wildcards** work (`/*/*/*EndpointTests/*`, `/*/*NodeSettings*/*/*`), and on TUnit 1.58 **alternation also works** — `/*/*/(ClassA|ClassB)/*` returns the union. Re-verified 2026-07-24 with `--list-tests`: `QuantLadderTests` alone → 9, `DesktopPortStoreTests` alone → 6, `(QuantLadderTests|DesktopPortStoreTests)` → **15**, exit 0, listing exactly both classes' tests. (An old claim that alternation "silently matches zero tests" is false; `AGENTS.md` now carries the correct measurement.) `--list-tests` honors the filter, so you can validate a filter's match count without running it. A filter that matches nothing exits **8** (`Zero tests ran`); if you meant to match, add another `/*` — the depth is off.

### The GPU smoke is the only gate that proves the GPU did the work — and its exit codes are a taxonomy, not a scale

`scripts/run-gpu-smoke-local.sh` is opt-in and nothing invokes it automatically. Run it by hand before cutting a tester RC or after touching the inference/runtime path. It owns the AppHost lifecycle (`dev-start.sh` → `aspire wait app` → `dev-stop.sh`) and discovers the port from `dev-status.sh --json` (it changes on every restart).

**Why it exists: a correct reply proves nothing.** CPU fallback answers correctly, just slowly. Measured on this box, same model and script: **GPU peak 72% / +1199 MiB VRAM versus CPU-fallback 11% / +0 MiB, with an identical, correct answer both times.** So the load-bearing assertion is nvidia-smi utilisation during generation plus a VRAM rise over a baseline sampled *before* the host starts — not the answer.

**Exit codes distinguish "the product says no" from "nothing was judged":**

- **`1`** — a judged step failed. Always accompanied by a `=== Summary ===` naming each step's verdict.
- **`5`** — an **infrastructure abort**: the AppHost failed to start or never became healthy, the base URL was undiscoverable, or auth failed. **Nothing was judged and no summary prints.** A pre-RC wrapper must treat 1 as "product says no" and 5 as "fix the machine and re-run" — reporting a 5 as a product failure is a false red, and treating it as a pass is worse.
- `3` an instance is already running · `4` could not tell · `2` a missing prerequisite (including "this box has no NVIDIA GPU") · `75` contamination (void, re-run) · `130` interrupted.

Every step must record a verdict — a skipped or result-less step **fails** the run, so "nothing ran" can never read as green. The refuse-to-pass logic itself is tested without a GPU by `scripts/tests/gpu-smoke.test.sh` (96 checks; it also fails a zero-check run).

**Configuration is not outcome — the installed record and the effective backend disagree in BOTH directions.** Both live-confirmed at HEAD 2026-07-31:

- A `vulkan` install with **no Vulkan ICD** runs entirely on the CPU. The default managed runtime's `llama-server --list-devices` printed an **empty device list** on this box.
- An `XE_LLAMACPP_SERVER_PATH` override runs on **CUDA while the installed record still says `vulkan`**. The `~/.unsloth` CUDA binary printed `CUDA0: NVIDIA GeForce RTX 5090`.

So **`IRuntimeDeviceAudit` is the authority, never the variant field**, and never conclude a box's effective backend from its installed-runtime record.

### The `.opencode/` agent eval foundation (`98f56d42`)

`.opencode/` carries an **offline behavioral agent eval** harness — no model calls, so it runs anywhere and is deterministic. **`.opencode/` is internal-only tooling, gitignored and absent from a fresh clone of this repository** — the instructions below describe what it does for context, not a command you can run from a public checkout. Where it existed, it was run with `.opencode/scripts/run-agent-evals.ts` (the older, separate `.opencode/scripts/run-evals.ts` ran the golden-workflow/config regression suite; they are different things).

Shape: each scenario under `.opencode/evals/behavioral/<name>/` ships a **compliant** and a **negative** triple — `*.scenario.json` (the setup), `*.trajectory.jsonl` (a recorded tool-call trajectory), `*.expected.json` (the verdict) — so every rule is proven to *fail* on the negative case rather than merely passing on the happy one. The contracts and graders live in `.opencode/tools/agent-evals/` with JSON Schemas under `.opencode/evals/schemas/` and their own unit tests. Existing scenarios encode this repo's own invariants (approval-before-mutation, dedicated-worktree boundary, exit-75 contamination, forbidden network/remote effects, release-validation evidence, symlink-realpath escape) — add the scenario pair when you add a rule agents must follow.

### `release.yml` is the intended release path — GitHub Actions must be enabled to run it

**Read this before trusting any sentence about "CI" in this tree.** `.github/workflows/release.yml` is the intended,
tag-triggered release mechanism: pushing a `v*` tag builds win-x64 + linux-x64 Velopack packages, generates the
changelog via a pinned `git-cliff`, and publishes to **this repo's** GitHub Releases with the built-in `GITHUB_TOKEN`
(no PAT, no separate artifact repo). Its `validate` job calls `build-and-test.yml` as a reusable workflow, so the
exact tagged commit re-runs the full build + backend/frontend gate set before anything is packed (fail-closed —
`version` and `release` both `needs: validate`). `workflow_dispatch` is a manual fallback on both workflows.

**GitHub Actions must be enabled on the repository for either workflow to run.** That is an owner-level repository
setting, not something in the YAML — do not assert it is currently enabled or disabled without checking
`gh workflow list --all` / `gh run list` yourself; this file does not track that state.

> **Historical note (checked 2026-07-24, not recaptured since).** At that point both `build-and-test.yml` and the
> then-current `release.yml` were registered as `disabled_manually`, with 3 runs/3 failures each, and `e2e.yml` was
> not a registered workflow. `release.yml` has since been rewritten (single-repo publish, win-x64 + linux-x64 matrix,
> `validate` gating). Do not restate the old run-history numbers as current.

`build-and-test.yml` triggers on `pull_request`/`push` to `develop` (not `main` — this repo has no `main` branch) plus
`workflow_dispatch`/`workflow_call`, running `dotnet test XE-Local-AI-Engine.slnx --configuration Release --no-build --max-parallel-test-modules 2 --maximum-parallel-tests 8` against the whole solution (auto-enrolling any project ending in `.Tests`; two module processes overlap, each capped at 8 in-process — TUnit's default of ProcessorCount×4 per module is what made unbounded concurrent modules time out on shared runners), a `Passed!|Failed!` output grep as a **hollow-gate guard** against the silent "zero tests enrolled" failure mode, and `TZ=Europe/Berlin` to expose non-UTC bugs. `e2e.yml` runs on `workflow_dispatch` or a `develop` PR labelled `run-e2e` — deliberately not a blocking merge gate, since it needs Playwright browsers + a built SPA.

The deprecated manual packager, `publish/package-tester-win.ps1`, ran the same shape of frontend and backend gate set
itself and was the source of every published tester RC from `0.1.0-rc.4.0` through `0.1.0-rc.5.0`. It is
reference-only now; the release mechanism is `release.yml`.

The **architecture tests are a real gate** — but because they are ordinary tests inside `XE-Local-AI-Engine.Tests`, so they run in any full-module run and in the packaging script's backend leg. They are enforced by the test suite, not by a PR check.

### Verify against the whole module, not just the class you touched

A targeted `--treenode-filter "/*/*/<YourClass>/*"` run is great for a fast loop, but it is **not** a merge gate. A DI-wiring regression (an unguarded `INodeSettingsStore.Load()` in a singleton factory) NRE'd every **host-based** test (`WebApplicationFactory` startup) — 14 `NodeSettingsEndpointTests` plus `GetVoiceManifest_*` — and stayed red across *four* merges because every review only ran the narrow classes it changed. Before declaring a backend change merged, run the **whole `XE-Local-AI-Engine.Tests` module** at least once. Nothing else will: there is no CI gate (above), and the packaging script's solution-wide backend leg only runs at release time — by which point a four-merge-old regression is already in the RC. A DI factory that reads a store/config at construction must null-guard it (`Load()?.X`) — a test substitute's `Load()` returns null and takes the whole host down.

### The full Tests module is flaky under parallelism — verify suspects in isolation

Running the entire `XE-Local-AI-Engine.Tests` module concurrently produces a **non-deterministic** failure count (observed 1/4/5/21) from tests that mutate **process-global** state racing each other — chiefly `DesktopBootstrapTests` (`EnsureLocalDataConfiguration_*`, which set/clear `XE_NODE_SQLITE_KEY` and write key files) and `EmbeddingPlaybookRetrievalRankerTests`. They **pass in isolation** (`--treenode-filter` per class). So a red full-module run is not automatically a real failure — re-run the named suspects alone before believing it. Related trap: the module has **conflicting env premises** — `DesktopBootstrapTests` `WhenNeitherEnvSet_*` require `XE_NODE_SQLITE_KEY` **unset**, while `PlaybookRetrievalRankerRegistrationTests.AddServices_ResolvesBoth…` requires it **set** (base64 of 32 bytes, not hex) — so you **cannot** satisfy the whole module with one ambient env var. (That follow-up is done where it is safe: both named suspects now carry `[NotInParallel("XE_NODE_SQLITE_KEY")]`.)

**Do not "optimize" the remaining bare `[NotInParallel]` attributes into keyed groups.** Verified 2026-08-15: in TUnit a bare `[NotInParallel]` means *run exclusively* (a terminal serial phase after everything else), and for most of the ~28 bare sites that exclusivity is load-bearing, not laziness. The meter-capture classes (`NodeMeterCapture` copies in `Telemetry/`, `Invocation/`, `Mcp/`, `Agents/`) subscribe by **meter name** (`XE.Node`) — several with no instrument filter at all — so *any* concurrently running test that drives product code emitting on that meter (every `TestServerWebAppFactory` host, every `McpAgentRunMetrics` instantiation) contaminates the capture window. A keyed group cannot express "nothing else may emit while I listen"; only run-alone can, unless every emitter in the module were keyed too. Likewise the `PATH`-stub suites (`CudaBuildServiceTests` and friends) must not overlap *any* test that spawns a real `git`/`cmake`, not just each other. The parallelism win comes from process- and module-level parallelism instead (see the memory-safe runner's `JOBS` below and the CI `--max-parallel-test-modules 2`), which these attributes do not constrain.

### Leftover build daemons starve the timing-sensitive tests — and the packaging gate is where you notice

The third reason a red run may not be real, and the one that bites at exactly the worst moment. It is neither of the two below it: no process-global state is racing, and no assembly is being rewritten. The machine is simply **busy**, and a handful of tests spawn real child processes against fixed wall-clock budgets.

Measured on Windows 11 on 2026-08-03, cutting `0.1.0-rc.4.2`. `publish/package-tester-win.ps1` failed its backend leg with `failed: 2` of 5008:

```
failed ProcessSandboxProvider_CancelCommand_TreeKillsInFlight      (30s 437ms)
failed ProcessSandboxProvider_Execute_ByteBudget_CapsCapturedOutput (30s 077ms)
```

Both at ~30 s — the budget, not an assertion about behaviour. The machine at that moment carried **19 `dotnet` processes** (MSBuild node-reuse daemons + `VBCSCompiler`, accumulated over a long session of builds) at **34%** CPU. The same class run alone: **43 tests, 0 failed, twice**. After `dotnet build-server shutdown` — 0 stray processes, 20% load — two consecutive full packs went green at `5008/0`.

The same run had already lost an earlier attempt to the frontend equivalent: `hides the sampling options trigger when developer mode is OFF` failing at **5012 ms** against a 5 s timeout, passing 3/3 in isolation.

Three things follow:

- **Shut the daemons down before a release pack**, not after it goes red: `dotnet build-server shutdown`. Node reuse is otherwise worth keeping (see the fd-inheritance note below — the wrapper exists precisely so you do *not* have to disable it for correctness), so this is a pre-gate hygiene step, not a config change.
- **A ~30 s / ~5 s duration next to a failure is a load signature, not a behaviour signature.** Read the duration column before reading the assertion message. A test that genuinely disagrees with the product fails fast.
- **The packaging script is where this surfaces**, because it is the only thing that runs the whole frontend + backend gate set back-to-back on a machine that has just done several builds. Expect it, and budget for a re-run: three of five pack attempts in that session were lost to this, none to a real defect.

### A concurrent `dotnet build` corrupts a test run — and the result is then neither pass nor fail

The other reason a red run may not be real. `dotnet test --no-build` loads assemblies out of `bin/`; a `dotnet build` in **any other process** rewrites those files mid-flight and the test host reports whatever it happens to trip over. Measured on this repo on 2026-07-24, with parallel agents running: one full-suite run reported `failed: 97` (of 4225), another `failed: 1`, both **clean on re-run**; an E2E run died with `FileNotFoundException: Microsoft.AspNetCore.SignalR.Client.Core, Version=10.0.9.0`. The evidence in each case was DLL mtimes falling **inside** the run window (`Client.Application.dll` 12:35:41, `XE-Local-AI-Engine.Tests.dll` 12:38:09).

**The corruption is not biased toward red.** A deliberate reproduction — `scripts/run-tests-memory-safe.sh` with a bare `dotnet build --no-incremental` fired at it 100 s in — rewrote **32** tracked files under the running test host and the batches still totalled `pass=3223 fail=0`. A contaminated run can hand you a **green** just as easily. So the verdict to reach for is not "pass" or "fail" but **"void — re-run"**; anything else eventually gets a real regression waved away as "probably contamination".

Two independent layers now exist, and they cover different things.

**Prevention — `scripts/with-build-lock.sh -- <command>`** takes an exclusive `flock` on `.tmp/build.lock` (gitignored) so cooperating shells serialize. Bounded wait (`BUILD_LOCK_TIMEOUT`, default 1800 s), exit **69** with the holder's PID and command line if it cannot acquire. Nesting is a pass-through (`XE_BUILD_LOCK_HELD`), so composed scripts do not deadlock.

> **The fd-inheritance trap.** An `flock` lives on an open **file descriptor**, and descriptors are inherited across fork/exec. `dotnet build` leaves MSBuild node-reuse daemons and `VBCSCompiler` alive for ~15 idle minutes; if they inherit the lock fd they hold the lock **while idle** and every other agent starves. This is not theoretical — measured here: `flock .tmp/x.lock dotnet build …`, then a `flock -w 3` after it exits **times out** (15 daemons still alive), whereas the same build under `with-build-lock.sh` re-acquires **instantly**. The fix is `"$@" 9>&-` — hold the lock in the wrapper shell and close the fd in the child, so nothing it spawns can hold it. The older workaround (`dotnet build-server shutdown` + `/nodeReuse:false -p:UseSharedCompilation=false`) also works but costs build speed; do not reintroduce it. `flock <file> <command>` in its plain form has this bug — do not use it around a .NET build.

**Detection — `scripts/assembly-guard.sh`** is the layer that actually matters, because you cannot force every terminal's `dotnet build` through a wrapper. It records `(size, mtime)` for every `*.dll`/`*.exe`/`*.so`/`*.deps.json`/`*.runtimeconfig.json`/apphost under the test output trees **after** the run's own build and **before** the first test process, re-checks after the last, and on any difference reports **CONTAMINATED** with the changed files and exits **75** (`EX_TEMPFAIL`) — never as test failures, never as a pass. Snapshotting after the build is what keeps a normal `build && test` sequence from tripping it. Use `assembly-guard.sh guard --test-bins -- <test command>` for new runners.

Already wired: `scripts/run-tests-memory-safe.sh`, `scripts/run-e2e-local.sh`, and every dotnet tree in `.opencode/scripts/project-validate.sh` (internal-only tooling, gitignored and absent from a public clone; described here for context, not as a runnable command) (which now also reports `⚠ CONTAMINATED` distinctly from `✘ FAILED` and returns 75). One consequence worth knowing: `--scope full` previously ran the backend tree and the **scripts** tree concurrently, and `lint-release-scripts.sh` rebuilds `XE-Local-AI-Engine.AI.Agent.Tests/bin/Release` — i.e. it was overwriting assemblies the backend suite was loading. Those two are now serialized, so `--scope full` is slower by the length of the scripts lint. Do **not** wrap `project-validate.sh` itself in the build lock: it locks its own trees, and an outer lock makes the inner ones pass-through, putting its parallel trees back inside one critical section.

**What is still exposed:** a human (or agent) running `dotnet test` **directly**, not through one of the wired scripts, gets neither layer — no lock, no snapshot, no contamination verdict. If you invoke the runner by hand, either put it behind `assembly-guard.sh guard --test-bins -- …` or accept that a surprising failure list needs a clean re-run before you believe it.

**Not every exit 75 is a sibling agent lane.** The VS Code C# Dev Kit **build host** (`Microsoft.VisualStudio.ProjectSystem.Server.BuildHost.dll`) rebuilds in the background and **cannot** be serialized by `with-build-lock.sh` — it is not a cooperating shell, so wrapping build+test in one lock acquisition does not help. Recognise it by signature: a small fixed set of files under a project you did **not** touch, identical sizes, only mtimes moved. Check `ps -eo pid,args | grep BuildHost` before blaming another lane; re-running is the only fix (it typically goes clean within a couple of attempts).

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

**Memory-safe full-module run:** `scripts/run-tests-memory-safe.sh` runs the module in **fresh-process batches, one per test namespace, single-threaded within each process**, so the leak resets between processes and every test runs exactly once (namespaces are the natural test-tree partition — no source-parsing guesswork). Peak RSS drops from ~3.1 GB (one process) to a few hundred MB per batch, and — because each batch is a single-threaded process — it also sidesteps the parallel env-mutation races above. Since 2026-08-15 the batch **processes** run `JOBS` at a time (default 4, longest-first): every hazard the design defends against is process-scoped, so cross-process concurrency is safe, and the measured 649 s of sequential batches drops to roughly 1/JOBS wall clock (floored by the largest ~41 s batch). `JOBS=1` restores strict sequencing; `PAR=N` additionally allows N-parallel tests per batch at the cost of reintroducing possible in-process flakes. Use it for local full runs. A roomier machine (7–16 GB) can still run the module in one process — which is what the packaging script's backend leg does.

**Upstream status (researched 2026-07-19):** the canonical tracker is [dotnet/aspnetcore#48047](https://github.com/dotnet/aspnetcore/issues/48047) — open since 2023, Backlog milestone, **no fix in any released or preview version**. The one attempted runtime fix ([dotnet/runtime PR#124391](https://github.com/dotnet/runtime/pull/124391), de-static-ing the `AsyncLocal<HostingListener>`) was closed **unmerged** 2026-02 as ineffective. Verified against current `HostFactoryResolver.cs` (main + release/10.0): `_currentListener.Value = this` is set on the spawned thread and **never cleared**. Nuance from the maintainer investigation worth keeping: the root only lives **while the entry-point thread stays blocked in `app.Run()`** — in principle a disposed factory stops the host and unwinds the thread; leaks that survive dispose indicate a *secondary* ExecutionContext capture into process-lifetime state (their canonical example: `HttpClientFactory`'s `ActiveHandlerTrackingEntry` timers, [dotnet/runtime#113494](https://github.com/dotnet/runtime/issues/113494)). Our measured no-effect-on-dispose therefore suggests such a secondary root exists here; a `gcroot` pass on one disposed host would name it. **Candidate structural fix — BUILT and heap-dump-verified 2026-08-15.** `Program.CreateAppAsync(args, ProgramAppCustomization?)` is the directly callable app factory (the entry point calls it too), and `TestServerWebAppFactory` builds on it + TestServer. But the `HostingListener` thread was NOT the dominant root — the TestServer fixture alone changed nothing until four further roots were killed, each named by `dotnet-dump gcroot` / `dotnet-gcdump`:

1. **MEAI's static AIFunction descriptor cache** (the big one): `AIFunctionFactory` caches every reflection-built tool descriptor in a static `ConditionalWeakTable` keyed by the `JsonSerializerOptions` used at registration; the MCP SDK's static default options made the entry immortal, and each descriptor's parameter-binding delegate captures `McpServerToolCreateOptions.Services` — the host's root `IServiceProvider`. Fixed in OUR code: `AddNodeMcpServerExtensions` registers `WithTools` with a per-host copy of `McpJsonUtilities.DefaultOptions`, so the weakly-keyed entry dies with the host.
2. **RateLimitingMiddleware's undisposed `PartitionedRateLimiter`** (verified by decompiling ASP.NET Core 10 — no disposal path exists): its 100 ms `RunTimer` outlives host disposal and roots the middleware pipeline → logger → root DI scope → everything. Additionally the permit-limit locals were computed INSIDE the `AddRateLimiter` lambda, so the closure captured the whole `WebApplicationBuilder`. Fixed: limits hoisted out of the closure, and `UseRateLimiter` is skipped in the Testing environment (limits there were non-limits anyway; no test asserts 429).
3. **EF Core's static `ServiceProviderCache`**: one immortal entry per distinct `DbContextOptions` (per-host connection strings!), each strongly rooting the application ServiceProvider. Fixed via a config seam — `EntityFramework:ServiceProviderCaching` (default true, product unchanged); the test fixtures set it false.
4. **Microsoft.Data.Sqlite's static pool groups**: one per connection string, holding an open connection + prune timer. The fixtures call `SqliteConnection.ClearAllPools()` (public API) on dispose.

With all four fixed, `dotnet-gcdump` shows disposed **TestServer-fixture** hosts fully collected (2 live fixtures = the running test), and the full-suite peak batch RSS dropped 1366 → ~590 MB. The **WebApplicationFactory** fixture still leaked per host even with these fixes (measured: 15 live `TestingWebAppFactory` after ~20 disposals) — there the `HostingListener` root is real and unfixable from the fixture, so retiring `WebApplicationFactory` per class was the path to a leak-free one-process run. **That migration is complete (2026-08-15):** all 78 host-based test files run on `TestServerWebAppFactory` and `TestingWebAppFactory.cs` is deleted. Do not reintroduce `WebApplicationFactory<Program>` in `XE-Local-AI-Engine.Tests` — the E2E module's own factory is a separate, out-of-process case. The new fixture has no `WithWebHostBuilder`; per-test host tweaks go through its `ConfigureAdditionalTestServices` / `AdditionalConfiguration` init-properties. The fresh-process runner stays the mitigation of record for the module's other memory pressure.

**Shared per-class hosts (2026-08-15).** 22 read-only / Guid-isolated classes now share ONE host per class via `[ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)] public required TestServerWebAppFactory Factory { get; init; }` and `var factory = Factory;` in each test — 138 of the ~311 per-test host builds gone (measured: `SchedulerEndpointTests`, 23 tests, 6.6 s → 3.9 s; `NodeChatReadOnlyEndpointTests`, 7 tests, 11.0 s → 4.8 s). Rules before you migrate another class: (1) TUnit runs the class's tests **in parallel against that one host and one SQLite DB**, so only classes whose tests are read-only or write exclusively their own Guid-named entities and never assert global state (list counts, empty states, `Received()/DidNotReceive()` on a shared substitute, "setup succeeds") qualify — the per-class audit is in the 2026-08-15 test-suite memory note; (2) any class that swaps DI services per test, or whose test subject is the fixture lifecycle itself (`WorkerShutdownDrainServiceTests`, `ApplicationStartupTests`), can never share; (3) **a shared host also outlives each test's process-global side effects** — `BackendTraceCorrelationTests` was reverted to per-test hosts because resolving `TracerProvider` registers a process-wide `ActivityListener` that lived on into the sibling tests asserting the opposite trace-flags byte; `[NotInParallel]` does not save you there. `ClassDataSource<T>` needs a **true parameterless constructor** (TUnit0061 rejects an all-optional-parameter one), which is why the fixture declares one explicitly.

**One-process full runs are green but not better (measured 2026-08-15, this box):** `XE-Local-AI-Engine.Tests --maximum-parallel-tests 8` in one process = **5582/0, 305 s wall, 3.9 GB peak RSS**; `scripts/run-tests-memory-safe.sh` at `JOBS=4` = 165 s wall, ~0.6 GB per batch process. The one-process shape is CI's shape and is now trustworthy (no leak, no crash), so use it where a single process is required — but the batch runner stays the local full-run tool of record on wall time and memory alone. A `DOTNET_GCHeapHardLimit=1.5 GB` attempt OOM'd inside the size-cap tests (they legitimately allocate large payloads, 8 hosts live at once), so that number is not a leak verdict — do not re-run it as one.

### Test hosts leak temp files to `Path.GetTempPath()` — keep the fixture cleanup

Every `TestServerWebAppFactory` host writes a fresh SQLite DB (`xe-local-ai-engine-tests-*.sqlite` + `-wal`/`-shm`/`-journal`/`.migration.lock` sidecars), a `…-nodedata-*` directory, and a `…-wwwroot-*` fixture root under the temp dir. `DisposeAsync` deletes **all** of them (the SQLite family via a filename-prefix sweep so new sidecars are caught automatically). Do not drop that cleanup: before it existed, a full run left ~3 artifacts per host build behind, which accumulated to **tens of thousands of files (~15 GB) and filled the 16 GB tmpfs `/tmp`** on this box, breaking subsequent runs mid-flight with `ENOSPC`. A **killed** run still leaks (dispose never runs) — after aborting a run, sweep with `find /tmp -maxdepth 1 -name 'xe-local-ai-engine-tests-*' -exec rm -rf {} +` (a bare shell glob hits ARG_MAX at these counts and silently no-ops).

### Layering is mechanically frozen

`XE-Local-AI-Engine.Tests/Architecture/LayerDependencyTests.cs` uses NetArchTest to freeze dependency direction (providers → Abstractions only; Contracts/Abstractions never reach back up). A structural refactor that breaks layering fails a *test*, not just review.

`.editorconfig:450` sets `dotnet_diagnostic.IDE0130.severity = error` — **namespace must match folder path**. One deliberate carve-out: `[**/Endpoints/**/V1/Dtos/*.cs]` disables IDE0130 so endpoint DTO files can sit in a `Dtos/` subfolder while keeping the **flat** `…{Area}.V1` namespace. This is load-bearing, not laziness: FastEndpoints/NSwag builds OpenAPI **schema IDs from the full type namespace** (`XE_…EndpointsSkillsV1SkillResponse`), so nesting a `.Dtos` segment would rename ~361 schema keys and churn the entire generated hey-api client. `Mappers/` and `Validators/` subfolders are *not* serialized and nest their namespace normally (IDE0130 enforced). See `docs/wiki/16-code-conventions.md`.

### OpenAPI → hey-api is the sole REST data layer for React

The generated client at `XE-Local-AI-Engine.Client.React/src/core/api/generated/` is the only sanctioned way React talks REST. Never hand-edit generated files.

`pnpm openapi:check` regenerates and runs `git diff --exit-code` — this is the drift gate. After any backend contract change, regenerate and commit the output *with* the change.

> **`openapi:check` cannot see a NEW endpoint or field.** It regenerates the client from the **checked-in** `openapi/v1.json`, so it re-derives from a stale spec and passes even after you add a path. To catch an addition you must re-fetch the spec from a *running* host — `XE_LAUNCH_MODE=desktop` mandatory (see the regen trap below), and a desktop host binds a **random** loopback port, so override `OPENAPI_SPEC_URL=<host>/openapi/local/v1/v1.json` rather than trusting the default 50722 — then assert **paths removed = 0**.

> **Regen trap — this one is invisible.** The throwaway host used for regen **must** run with `XE_LAUNCH_MODE=desktop`, or the spec silently omits every `IDesktopOnlyEndpoint`-gated path (`app-update`, `github-auth`, image endpoints). The generated client then drops them, and you get dozens of phantom `TS2305 no exported member ...` errors. A non-desktop regen is *incomplete without saying so*. Prefer merging new/changed paths into the committed `openapi/v1.json` over overwriting it wholesale.

TanStack query keys generated by hey-api are single-element arrays `[{ _id: operationId, ... }]` — invalidate by **partial-object match**, never `.slice()`.

### Browser E2E runs as two ordered parallel groups, not one sequential queue

`BrowserParallelLimit.Limit == 1` used to apply to **every** browser test, on the reasoning that the node revokes all of a user's active refresh tokens on each login/refresh (`NodeAuthService.RevokeActiveTokensAsync`), so concurrent sessions would revoke each other's cookie. That coupling is real but **strictly per-user** — the revoke query filters `token.UserId == userId`, and Identity lockout is per-user too. Nothing in the auth model forbids concurrency between *different* users.

What does constrain the shape is the login page: `Login.tsx` has **no email field**, it posts `{ password }` only, and `ResolveLoginUserAsync(email: null)` resolves the account with `Users.SingleOrDefaultAsync(u => u.SetupCompleted)`. So there must remain **exactly one** user with `SetupCompleted = true` — seed a second and the form login throws for every test. Pooled users are therefore seeded `SetupCompleted = false` and sign in through `POST /api/local/v1/auth/login` with an explicit email (the `FindByEmailAsync` branch). `Context.APIRequest` shares the BrowserContext cookie jar, so the `node_rt` cookie lands in the context and a plain `Page.GotoAsync` boots the SPA already authenticated via session-restore.

The suite is now split by which state a test touches, with `XEE2ETestBase` reduced to an attribute-free core:

- **`XESerialE2ETestBase`** — `[ParallelGroup("BrowserSerial", Order = 0)]`, limit 1, canonical admin, UI form login. For anything that mutates session-global state (`WorkerEventDispatcher.CurrentInvocation`, `FakeOllamaState`, `SetAdminTutorialStateAsync`) or asserts a node-wide empty state.
- **`XEPooledE2ETestBase`** — `[ParallelGroup("BrowserPooled", Order = 1)]`, limit `PooledUserCount` (4), each test leasing a distinct `e2e-pool-{n}@example.test` from a pre-filled `Channel<int>` and returning it in `[After(Test)]`.

TUnit runs the two groups as **disjoint phases**, and that disjointness — not the order — is the load-bearing property: it is what makes a global mutation in the serial phase unable to race a pooled reader. `SchedulerPageE2ETests` is kept serial for exactly this reason — creating a `model-recommendation-check` job is what flips the gate `ModelRecommendationsPageE2ETests` branches on, and concurrently the job could appear between that test's enabled-state read and its assertions.

**Do not assume the phases run in ascending `Order`.** Measured on a 69-test run (TRX per-test start/end timestamps): **0** pooled/serial overlapping pairs, but the observed sequence was **pooled first (10.8 s–57.2 s), then serial (58.1 s–200.5 s)** — the reverse of the `Order` values. Distinct `Order` values are what put the groups in separate phases; the direction has not been pinned down here, so no test may depend on the other phase having already run. Concurrency evidence from the same run: pooled = 27 tests, **peak 4 concurrent**, 95.1 s of test time inside a 46.4 s span (**2.05×**); serial = 40 tests, **peak 1 concurrent**, unchanged from before.

Never put `[ParallelLimiter]` or `[ParallelGroup]` back on `XEE2ETestBase`; derived tests would carry two of each.

### Frontend lint

`pnpm run lint` = `tsc --noEmit && node scripts/CheckEventCurrentTargetInUpdaters.mjs && biome lint && stylelint`. It does **not** run `biome format`, so formatting drift won't fail lint. Do not run `biome format --write` across whole directories as a "fix" — it dirties committed files with whitespace churn unrelated to your change.

**`pnpm run lint` is now the only frontend typecheck — a green E2E run is not one.** `XEReactClientFixture` used to run `pnpm run build` (which begins with `tsc --noEmit`), so a type error blew up at fixture init. Since 2026-07-31 it runs **`build:e2e`**, a bare `vite build` — because the fixture only consumes `dist/`, never the lint output, and `project-validate.sh --scope full` was paying for the frontend lint **three times** (once as `lint`, again inside `build`, again in the fixture) at 20–45 s each. esbuild strips types without checking them, so E2E can now go green over a frontend that does not typecheck. Run `pnpm run lint` yourself before trusting one. Same principle as the Release-build rule in §1: the fast path stopped being the gate, so run the gate explicitly.

react-doctor's config must be `doctor.config.jsonc` (comments) — biome parses `.json` strictly and a `.json` with `//` comments fails lint. Its dependency rules are namespaced under the `deslop` plugin, so `ignore.rules` entries need the `deslop/` prefix or they silently no-op.

### Frontend tests: an `await import()` inside `it()` is charged to `testTimeout`

**A vitest test that dynamically imports a component graph is timing itself against module loading, not against its own assertion.** `vi.resetModules()` + `await import("…/SomeComponent")` inside a test body pays the cold transform *and* evaluation of that component's entire graph — Mantine, `@tabler/icons-react`, `react-i18next`, every child component — and vitest counts all of it against that one test's timeout. Measured on an idle Linux box, the first test in `ChatInputArea.sampling.test.tsx` took **1754 ms** against **91 ms** and **139 ms** for the two after it; the only difference is that the first one paid for the cold graph. The packaging run executes 209 files with coverage across parallel workers (its own summary reports `import 804 s` aggregate), and there that cost intermittently crossed the **5 s default** and failed `package-tester-win.ps1` at the "Frontend tests and coverage gate" step — with a timeout message naming a test whose behaviour was fine. It passed on rebuild, because the second run's caches were warm.

Two consequences:

- `testTimeout` is set to **20 s** in `vite.config.ts` (not the 5 s default). It is there for this failure mode, not for slow tests. Don't "tidy" it back down.
- **Prefer a static import and drive state through the store's setter.** The pattern only exists to re-run a `read…FromLocalStorage()` call at module-init, and the zustand stores that hydrate that way (`DeveloperModeStore`, `ChatSamplingPreferencesStore`) all expose a setter that writes through to storage. `useDeveloperModeStore.getState().actions.setDeveloperMode(true)` gets the same state with no import in the test body, and the module load moves to collection time, which no test timeout applies to. Reach for `vi.resetModules()` **only** when the hydrate-from-storage path *is* the thing under test — as in `NodeSettings.sampling.test.tsx` ("switch starts checked when developer mode was persisted on"), where re-running module init is the assertion.

### Packaging (Velopack)

- `vpk pack` has **no `--pre` flag** — it fails with `'--pre' was not matched`. Prerelease state rides the SemVer suffix in `--packVersion`; `--pre` is only valid on `vpk upload github`. Already correctly wired in both `release.yml` and `package-tester-win.ps1` — don't "fix" it back.
- The React SPA must be built **before** `dotnet publish`. This is now a hard guard: the `GuardNodeReactBuildPresentOnPublish` MSBuild target errors a webless publish with a "run pnpm build" message, instead of shipping a blank app.
- **`release.yml` is now the intended release path.** `publish/README.md` documents three packaging routes: (1) `.github/workflows/release.yml` — tag-triggered CI that builds win-x64 + linux-x64 Velopack packages and publishes them to **this repo's** GitHub Releases with the built-in `GITHUB_TOKEN`; this is the intended path, but GitHub Actions must be enabled on the repository for it to run (an owner-level setting this file does not track); (2) `publish/package-tester-win.ps1` — a manual Velopack build+pack+upload run on Windows, the source of every published tester RC from `0.1.0-rc.4.0` through `0.1.0-rc.5.0`, and now **deprecated, reference-only**; (3) `publish/package-rc.sh` — a manual portable zip with no Velopack metadata and therefore no self-update, also deprecated/reference-only. Both manual packagers still get static analysis from `scripts/lint-release-scripts.sh`, but neither is the release mechanism anymore.
- **Historical manual packager note:** `publish/package-tester-win.ps1` needs PowerShell 7+ and a non-UTC machine clock. It also preserves the retired private-repository GitHub App/device-flow path. It is deprecated, reference-only, and must not be used to produce an official public artifact. The tag-triggered workflow is the release authority; its public updater is anonymous and has no client-ID requirement.
- **Run the pack on a QUIET machine, and `dotnet build-server shutdown` first.** Three of five pack attempts in the rc.4.2 session were lost to leftover build daemons starving the timing-sensitive tests, and none to a real defect — see "Leftover build daemons starve the timing-sensitive tests" above. A red pack after a session of builds is far more likely to be that than a regression; check the failure's *duration* before its message.
- **Consolidated to one repo.** Source lives in `w0rldx/XE-Local-AI-Engine.Source` (public) — the single home for source and releases; the separate `w0rldx/XE-Local-AI-Engine.Tester-App` is retired. Historically the two shared only a version string: the `v<version>` git tag went on HEAD of the source repo while `vpk upload github --tag` created a same-named release on the tester repo, so tester releases through `0.1.0-rc.5.0` have no tag in this repo — don't look for them in `git tag -l`. The consolidation is done on the CI side: `release.yml` now publishes both RIDs to **this repo's** GitHub Releases using the built-in `GITHUB_TOKEN`. The deprecated manual packager (`package-tester-win.ps1`) still targets the retired tester repo — that is expected of reference-only tooling, not a pending migration step.
- **Tester-release tag convention changed.** The 7 releases published through 2026-07-07 carry **bare** tags (`0.1.0-rc.4.1`) with `v`-prefixed release *names*. The script now passes `--tag v<version>`, so new releases are v-prefixed while the old ones stay bare. Any tooling that looks up an existing tester release must handle **both** forms.
- **Both update channels point at this repo anonymously.** `appsettings.AppUpdate.main.json` and
  `appsettings.AppUpdate.tester.json` carry `GitHubRepositoryUrl = https://github.com/w0rldx/XE-Local-AI-Engine.Source`;
  the public updater has no client ID or device-flow dependency. Do not redact the URL back to a placeholder — that
  breaks self-update for installed builds.
- Changelog: `cliff.toml` → `RELEASE_NOTES.md` → `vpk pack --releaseNotes`. Notes must exist **at pack time** — there is no notes flag on `vpk upload github`. `cliff.toml` drives `RELEASE_NOTES.md` **only**; the repo-root `CHANGELOG.md` is hand-maintained Keep-a-Changelog and is not generated, which is exactly why it drifts. `(unverified)` Re-uploading assets to an existing release does not update its body; re-releasing needs `gh release delete <ver> --cleanup-tag` or `gh release edit --notes-file`.

### The backend serves the SPA

One Kestrel process serves both API and UI: `app.UseStaticFiles()` + `app.MapFallbackToFile("index.html")` (registered after endpoint mapping) in `XE-Local-AI-Engine.Client/Program.cs`. Don't stand up a second static/node server in the bundle.

---

## 2. Dev environment & local runtime

### This WSL2 box HAS a GPU

**RTX 5090, 32 GB, CUDA toolkit 13.3, compute arch sm_120 (Blackwell), driver 610.74.** Verified live 2026-07-26 via `nvidia-smi`. Any note claiming "WSL has no GPU, GPU work can't be tested here" is **wrong** — CUDA builds, VRAM offload, and `nvidia-smi`-gated paths can all be built and live-tested on this box. cmake/gcc/ninja are present, so a from-source CUDA llama.cpp build works.

**This entry said "RTX 4080, 16 GB, sm_89 (Ada)" until 2026-07-26.** The hardware changed; the doc did not. Don't trust a remembered GPU model — read `nvidia-smi` if it matters, exactly as you would for the CUDA version.

Don't hardcode the CUDA minor version — it has already drifted (13.1 → 13.3). Read `nvcc --version` if it matters. **The same applies to the compute arch**: source builds detect it live, while the conservative probe-failure fallback now includes `75;86;89;120`. If a GPU build is mysteriously slow, check what arch it was actually compiled for rather than assuming the fallback was used.

Rest of the box, for sizing assumptions: **AMD Ryzen 9 9950X3D** host, **8 processors exposed to WSL**, **~31 GiB RAM in the VM**. All three are well above the stated consumer target (≈16 GB RAM, 8–16 GB VRAM), so **local benchmark numbers over-report** — never quote them as consumer-hardware figures.

**Two GPU-behaviour traps specific to WSL2/WDDM** (both measured 2026-07-26; they apply to native Windows too, since it is the same driver model):

- **VRAM exhaustion does not OOM — it silently degrades.** WDDM demand-pages GPU memory to host RAM. With ~1.2 GB truly free, `llama-server -ngl 99` loaded and served at **161.7 tok/s** versus **698.4 tok/s** unloaded — a **4.3× slowdown with zero errors**. So (a) OOM-recovery paths cannot be exercised by VRAM pressure here, and (b) any benchmark taken while something else holds VRAM silently reports paged numbers that do not transfer. Don't deliberately drive true OOM either — WSL2 GPU OOM has been reported to kernel-panic Hyper-V and BSOD the host.
- **The two free-VRAM readers disagree under pressure.** `nvidia-smi memory.free` (→ `HardwareProfiler` → `CapacityService`) reports the true global figure; `llama-server --list-devices` (→ `IProcessVramBudgetProbe` / `LlamaListDevicesProcessVramBudgetProbe` → the GGUF variant recommender, `InferenceProfileService`, `InferenceBenchmarkHarness`, `ProcessContextAllocationResolver`) is built on `cudaMemGetInfo`, which on WDDM reports the **calling process's residency budget**. Measured divergence with another process holding VRAM: **492 MiB vs 29697 MiB**. See `Plans/2026-07-26-vram-reader-divergence-defect.md`. The seam name now says so, and the split was acted on: `InferenceInvalidationEvaluator` reads `HardwareProfile.AvailableVramBytes` (the global figure) and **no longer consumes the `--list-devices` number at all**. Don't reunify these two readers — they measure different things on purpose.

**`nvidia-smi --query-compute-apps` returns an empty list under WSL** even when a process is holding tens of GB. Per-process VRAM attribution is unavailable here; anything relying on it is untestable on this box.

### This WSL2 box has no keyring

No Secret Service daemon (`org.freedesktop.secrets was not provided by any .service files`). MSAL/Azure.Identity token-cache persistence throws `MsalCachePersistenceException`, which **Azure.Identity re-wraps as `AuthenticationFailedException`** — so a handler catching only `CredentialUnavailableException` never sees it. When touching Entra/Azure auth here, walk the `InnerException` chain. Consequence: such sign-ins are in-memory-only on this box and don't survive restart.

### Aspire 13.4 stop needs a worktree-scoped fallback

Every Aspire resource runs as a DCP-owned process in its own process group, detached from the AppHost/CLI's process tree, so `aspire stop`'s tree-kill can't reach it (upstream Aspire CLI bug; fixed only in 13.5+, still preview). A killed session therefore leaves an **orphaned `llama-server` holding its port and GPU VRAM** — it runs under `setsid`, so a parent SIGKILL doesn't touch it.

**Use `scripts/dev-start.sh`, `scripts/dev-status.sh`, and `scripts/dev-stop.sh`.** They bind every
operation to the canonical AppHost path for the current checkout; start always uses `--isolated`.
The stop fallback snapshots that exact AppHost, its exact-path Aspire ancestor, the DCP sibling
whose command line contains the exact `--monitor <AppHost PID>` token pair, and the complete
descendant closure before stopping. It records `/proc` start times and revalidates identity before every
signal, so separately-sessioned descendants remain attributable without treating a shared
login/session SID or a reused PID as ownership. Aspire query failures and malformed JSON fail
closed; they are never equivalent to "not running". A descendant `llama-server` is owned and
cleaned. Never restore the old global `llama-server` kill: managed binaries are shared across
worktrees, so an already-orphaned executable-path match cannot prove ownership. Processes outside
the selected graph are untouched and do not make scoped teardown fail; success proves only the
exact registration and selected graph are gone. Never use
`aspire stop --all` during parallel development.

### The node operator secret is seeded by dev-start.sh, not by any tracked file

`AppHost.cs` declares `node-sqlite-key` as a **required** secret parameter. Nothing in the repo
supplies its value — the shared default that used to sit in the AppHost's `appsettings.Development.json`
was removed because it made every dev database decryptable by anyone with the source, and it must not
come back. `dev-start.sh` mints a per-checkout `XE-Local-AI-Engine.AppHost/.data/node.key` and passes
it as the environment variable **`Parameters__node-sqlite-key`** (the env form of Aspire's
`Parameters:node-sqlite-key` config key — dashes are preserved, `__` maps to `:`). Consequences:

- **Never seed it via `env` or an `aspire -- <args>` argument.** `/proc/<pid>/cmdline` is
  world-readable; a process environment is not. `dev-start.sh` uses `python3` to set the variable and
  `execvp` the CLI precisely because bash cannot `export` a name containing dashes.
- **A checkout with dev data written under a different secret does not degrade — it crashes.** The
  first protected read throws `AuthenticationTagMismatchException` and takes the host down at startup
  (observed via `SchedulerJobDetailReconciliationService`). Delete
  `XE-Local-AI-Engine.AppHost/.data/node-sqlite/` (and, if the key ring is also orphaned,
  `XE-Local-AI-Engine.Client/dp-keys/` plus the `*.enc` files beside it). `dev_ensure_node_operator_secret`
  warns and names these paths when it mints a key next to pre-existing data.
- Starting the AppHost any other way (IDE F5, bare `aspire run`) gets Aspire's interactive parameter
  prompt instead, or use `dotnet user-secrets set "Parameters:node-sqlite-key" …`.

Aspire JSON is sensitive operational data. `aspire ps` includes the dashboard login token, while
`aspire describe` can include environment values, connection strings, and sensitive resource
properties. `scripts/dev-status.sh` deliberately projects a small allowlist and strips URL query
strings. Do not replace it with raw JSON output in logs or validation transcripts.

A startup reaper (`StaleLlamaServerReaper`, in `XE-Local-AI-Engine.Providers.LlamaServer/Implementation/`) also kills leftovers under the managed binaries root on next launch, so an orphan won't block a restart. `StaleImageServerReaper` does the same for image-gen.

> **Harness gotcha:** never `pkill -f <substring>` where the substring also matches your own command line — the `pkill` process matches itself and dies before reaching the target. Kill by PID, or from a separate shell call.

### Locked runtime decisions — do not "helpfully" reintroduce

- **Docker is off the inference path — and, since [ADR 0004](adr/0004-development-mode-container-execution-docker-stopgap.md) (Accepted 2026-07-29), permitted for Development Mode build/test/lint execution *only*.** The epic-level decision that dropped the dependency (previously Ollama hosting + tool sandboxing) in favour of GPU inference with a driver-only footprint is **narrowed, not reversed**. Still true and still not to be reintroduced: no Docker in model hosting, model acquisition, embedding, image generation, or any provider on the chat path; **HostAgent and the sandbox-gRPC transport stay deleted**. What the ADR permits is a Docker Engine API client reference (see the package-id trap below) plus a running daemon as a **hard runtime requirement for Development Mode** — no daemon means no Development Mode, with **no unisolated fallback** to the process provider by design. Two traps: on Linux, Docker-socket access is **root-equivalent** (the ADR documents this rather than mitigating it — the product neither requires nor provides rootless Docker), and repository-supplied container configuration is rejected wholesale (plan D7), because a `devcontainer.json` in a repo the agent can write is untrusted input. Widening any of that is a new operator decision, not an implementation detail.
  - **Status matters when you read the tree, and there is exactly ONE page that tracks it: [`docs/roadmaps/development-mode-container-status.md`](roadmaps/development-mode-container-status.md).** Read that before concluding anything about what is built; it is the living companion to the ADR (which records the decision and is deliberately *not* a progress log). As of 2026-07-31 the provider has **shipped as opt-in**: `DockerSandboxRuntimeProvider` (`Name = "docker"`) is registered and selected by `Development:Sandbox:Provider=docker`, while the shipped `appsettings.json` sets no `Development:Sandbox` key, so a default node still runs Development Mode on the process provider. So finding a Docker client reference is **not** a regression to rip out, and finding Development Mode on the process provider is **not** evidence the work stalled — it is the default.
  - **The package id is `Docker.DotNet.Enhanced`, and the difference is not cosmetic.** The **assembly and namespace are still `Docker.DotNet`**, so the `using` and every type name look like the original — but the pinned package is the maintained testcontainers fork (`Directory.Packages.props`, 4.3.3, MIT, real `net10.0` target, `System.Text.Json`, what Testcontainers 4.x itself depends on). The **original `Docker.DotNet` (3.125.15) was last published 2023-05-18, is netstandard-only, and drags in Newtonsoft.Json.** Because the namespace matches, "add the missing package" tooling and a from-memory `<PackageVersion Include="Docker.DotNet">` both resolve to the *wrong, unmaintained* one and compile fine. Its transports arrive transitively as `Docker.DotNet.Enhanced.{Unix,NPipe,NativeHttp,LegacyHttp}` in lock-step, so they need no entry of their own — don't "fix" a missing transport by pinning one.
  - **"Run the container non-root" is WRONG on a rootless daemon, and the read-back that would reassure you is blind to it.** A rootless daemon maps container uid 0 to the **invoking user's** host uid, and container uid `N>0` to `subuid_base + N - 1`. Measured on Engine 29.6.1 rootless (`/etc/subuid` = `…:100000:65536`): `--user 1000:1000` cannot create a file in an engine-generated workspace bind mount at all (**Permission denied** — container 1000 is host 100999), while `--user 0:0` writes files owned host-side by uid 1000, the engine's own account. The rule is therefore **"run as the identity that maps to the engine's own host uid, and never to host uid 0"** — the engine's effective uid under a rootful daemon, uid 0 under a rootless one. Rootless container-uid-0 is not host root: caps all dropped, no-new-privileges, read-only rootfs, unprivileged host account — strictly less privileged than the engine itself.
    - **The trap is the verification, not the flag.** `inspect` echoes the uid that was *asked for*; it can never report what that uid *maps to*. With `--user 1000:1000` against a rootless daemon **every hardening read-back passes** while the container cannot write a byte. So `DockerSandboxRuntimeProvider` verifies an **outcome**: after create it writes a probe file from inside the container, stats it host-side (`statx`, no symlink follow) and asserts the owner is the engine's effective uid, removing the container and failing the create otherwise. Don't replace that probe with an inspect assertion because it looks equivalent — it is exactly what the probe exists to catch.
    - **Idmapped bind mounts are not the escape hatch.** `BindOptions` in `Docker.DotNet.Enhanced` 4.3.3 exposes no idmap member, and Docker only idmaps under rootful `userns-remap` — the mode where this problem does not arise.
    - **A uid-0 rejection cannot live in `IValidateOptions<T>`.** That runs at startup with no daemon in reach, so it cannot know whether 0 means host root or the invoking user. It lives at create-time identity resolution instead. The validator keeps only the daemon-independent half: a `0` paired with a non-zero id is rejected, since that identity straddles two host accounts either way.
  - **`/tmp` must be a writable tmpfs inside the container or NO `dotnet` command runs at all (`fbe09cb7`).** With §3.8's read-only root filesystem, every `dotnet` invocation in Development Mode's validation gate failed **EROFS before touching the project** — restore, Release build and test each failed identically, and all passed with the mount (measured, rootless daemon). The cause: the CoreCLR PAL backs a **named mutex** with shared-memory files under a path it *compiles in* (`TEMP_DIRECTORY_PATH` → `/tmp/.dotnet/shm/session<N>`), and the CLI takes such a mutex on first invocation. Four non-obvious facts, each already paid for:
    - **The path cannot be relocated.** It honours neither `TMPDIR`, `TMP` nor `TEMP` — all three of which the engine already redirects to writable runtime mounts. `dotnet/runtime#49822` closed that request **by design** (a global mutex needs a location every process agrees on). `DOTNET_SKIP_FIRST_TIME_EXPERIENCE` is a no-op in .NET 10, and NuGet's `MigrationRunner` takes the mutex regardless. Don't go looking for an env var; there isn't one.
    - **Mount `/tmp`, not the narrower `/tmp/.dotnet`.** The PAL creates its directory by **mkdtemp-and-rename**, so it needs the *parent* writable. A tmpfs mounted precisely at `/tmp/.dotnet` was measured to fail with `mkdtemp(…) == nullptr; errno == EROFS`.
    - **`noexec` does NOT break .NET named mutexes** — the mutex needs writable shared memory, not executable pages. So the mount is `noexec,nosuid,nodev` and stays that way. It also does not widen the payload-landing surface: the workspace bind mount and every engine-generated runtime mount are already writable and carry **no** `noexec` (an ELF binary dropped into the workspace was measured to execute), so a noexec tmpfs is strictly *weaker* than surfaces that already exist.
    - **Size it, because tmpfs pages are charged to the container's memory cgroup.** A 1 GB tmpfs under a 256 MB limit was measured to OOM-kill the container at ~254 MB. It ships at 64 MB against a measured 4 KB of real use; `size=` is the second belt after the cgroup limit.
    - **Verify the flags, not just the mount.** The §3.8 read-back originally asserted each tmpfs was present and size-bounded but never that `noexec`/`nosuid`/`nodev` survived — so a daemon that silently dropped them produced a container that *verified*. All three are now checked, **tokenized rather than substring-matched** (`noexec` contains `exec`).
  - **A repository-local `.git/config` is arbitrary code execution against HOST-side git — reproduced twice on git 2.53.0.** `core.fsmonitor` runs as a shell command on index refresh (`status`, `reset`, `add`, `diff` all trigger it), and `filter.<driver>.clean`, selected by an **in-tree** `.gitattributes`, runs on `git add`. `DevelopmentPatchEvidenceService.ExportAsync` runs `reset` and `add -A` **on the host** against the agent-writable workspace, so this is reachable.
    - `core.fsmonitor` is **closed**: `AgentHomeGit` pins `-c core.fsmonitor=` in its hardened flag set (with `core.sshCommand=`, `core.pager=cat`, `core.editor=false`). A command-line `-c` outranks any `include.path`/`includeIf` chain, so those are covered. Don't remove a pin because the key "isn't used here" — that is what a later command changes.
    - `filter.*.clean` **cannot** be closed from the argument vector: driver key names are arbitrary, so there is no finite set to pin, and `core.attributesfile=/dev/null` disables only the **global** attributes file — an in-tree `.gitattributes` outranks it and git has no flag disabling attribute processing. The two answers are a read-only bind mount of `<workspace>/.git/config` (container side) and rewriting that file to a known-good minimal config before each host-side git call (engine side, provider-independent). **Minimal is not empty** — preserve `core.repositoryformatversion`, `core.filemode`, `core.bare` and every `extensions.*` key, or git refuses to operate on a newer-format repository; and do not reintroduce `origin`.
    - **The standalone clone (D8) OPENED this.** With the old linked worktree, `.git` was a pointer *file*, so `<jail>/.git/config` did not exist inside the jail. After the clone it is a real, agent-writable file there. `DevelopmentWorkspaceSecurity.Confine` stops the workspace *tools* naming that path; nothing stops a build or test command writing it as a side effect.
- **HostAgent is gone.** The gRPC HostAgent client, its "RuntimeManager" UI/hub/endpoints, and the standalone Tray app were all deleted; the Windows-elevation requirement it existed for is now served by an in-app unprivileged process supervisor (Job Object tree-kill on Windows).
  - **Don't confuse this with the worker-hub `Services/Connection/*` subsystem** (`IWorkerHubConnection`) — that's the SignalR cloud-pairing path and is unrelated. Don't delete it by name-matching "connect"/"hub".
- **Tool sandboxing is a supervised process, not a container** — `ProcessSandboxRuntimeProvider` (`ProviderName="process"`), a native process under a node-scoped jail dir. It ships **enabled by default**.
  - **Never state this provider's containment flatly — state it per mechanism, per host.** Containment is *measured once at startup* into `SandboxContainment` (`Services/Sandbox/Implementation/Launch/SandboxContainment.cs`), and each mechanism is **independently optional**: process-group launch (`setsid`), resource ceilings (`systemd-run --user`), and network isolation (`unshare`, a fresh empty netns with no route to host loopback, the LAN, or the cloud-metadata endpoint). Each is probed by *really doing the thing* — starting a constrained transient scope, creating a namespace — not by looking for the binary. Off Linux, and on a host where every probe fails, the record is `SandboxContainment.None` and the child is a plain process. So "the sandbox blocks the network" is **wrong as a flat claim**; the guarantee holds only where the mechanism is active, and each `…UnavailableReason` carries the measured reason a degraded host did not get it.
  - **Capability honesty runs in both directions, and one record enforces both.** `Capabilities` advertises a flag **only** when the matching mechanism in that same probe is active, and `BuildLaunchPolicy` (called by `CreateOrAttachAsync`) **fail-closed rejects** (`SandboxCapabilityNotSupportedException`) any request for something unserved, rather than returning a sandbox weaker than asked for. Both halves read the same `SandboxContainment`, which is what stops advertisement and enforcement from drifting apart. **Do not "helpfully" soften either half** — not the advertise-only-if-served rule, and not the rejection into a silent no-op. A caller must never believe it received isolation the provider did not implement.
  - **Egress: default-deny where the mechanism is active, and no allow-list anywhere.** With `unshare` available, AgentHome's child gets an **empty network namespace** — egress denied outright, not filtered. `SandboxNetworkPolicy.Restricted` (an egress allow-list) remains **unsupported and fail-closed rejected**; only the deny-everything policy is honoured. Where the mechanism is absent, the provider degrades, does **not** advertise the capability, and approval-gating upstream stays the interim control. **Strong isolation is still deferred to MXC** — an empty netns is not a kernel-hardened boundary.
    - **The request is capability-gated, and it has to be.** `AgentHomeService.ResolveNetworkPolicy` asks for `None` only when the provider advertises `SupportsNetworkPolicy`, else `Unrestricted`. Since the provider **fails closed** on a confinement request it cannot honour, an unconditional `None` would throw on every host without the mechanism — i.e. it would kill AgentHome outright on Windows. Don't "simplify" that conditional away.
    - **This covers Coder too, without Coder asking.** `CoderWorkspaceReader` creates no sandbox of its own — it attaches to AgentHome's through `ISandboxRuntimeProvider.ConnectAsync` (deliberately *not* taking the AgentHome run lock). So one policy decision covers AgentHome's 4 injection sites **and** Coder's single one, and there is no separate Coder opt-in to look for or to add. (If you have seen "Coder's 3" written down, that was counting Coder's **tool entry points** — three tools, one injected provider.)
    - **`policy.json` can under-report, by design.** It records the posture at the time the agent home was **initialised**, and `EnsureBaselineFilesAsync` deliberately does not overwrite an existing file (preserving operator edits across re-init is a pinned contract). A home created before denial shipped therefore still reads `"unrestricted"` while its runs are actually denied. The drift is safe-direction only — under-claims, never over-claims — but don't read that file to determine a *current* run's posture; read the provider's advertised capability.
  - **Development Mode is NOT on default-deny egress** — `DevelopmentWorkspaceProvider` requests `Unrestricted` (no line cite: Slice 3 is actively moving that file). Its `dotnet restore` needs the network until Slice 3's S3.6/D6 machinery exists. So the denial covers the **AgentHome sandbox only**, plus Coder by attachment; Development Mode is deliberately outside it. Don't describe an engine-wide egress posture — there isn't one.
  - **A network namespace does NOT confine UNIX sockets — this one was paid for live.** `systemd-run --user` needs `XDG_RUNTIME_DIR` to reach the per-user systemd bus, and that variable addresses a **UNIX socket**, which a netns does not cover. A child that inherited it could call `systemd-run` itself and start a unit **outside** its own scope and namespace, escaping both the ceiling and the egress denial — verified by actually doing it before the fix. The variable is therefore injected for the **wrapper only** and stripped by an `env -u` layer immediately before the sandboxed executable is exec'd. If you touch the launch wrapper, that strip is load-bearing.
  - **The provider is chosen per feature, never globally** (plan decision D2). ADR 0004 gives **Development Mode** a container provider; **AgentHome (4 injection sites) and Coder (1) stay on `ProcessSandboxRuntimeProvider`**. Do not "finish the job" by switching them — that would force a Docker requirement onto features that do not need one, which is exactly what D0's Dev-Mode-only scoping rules out. **11 injection sites in total**: AgentHome 4, Coder 1, Development 6. Older docs say 13 and "Coder 3"; that counted tool entry points rather than injected providers.
    - **The seam is types, not configuration, and that is deliberate.** `ISandboxRuntimeProvider` is **not DI-registered**; each feature resolves a role marker instead — `IAgentSandboxRuntimeProvider` or `IDevelopmentSandboxRuntimeProvider`, both empty interfaces over it. `DockerSandboxRuntimeProvider` implements **only** the Development role, so wiring a container provider into AgentHome or Coder is a **compile error**, not a rule someone has to remember. Don't "simplify" this back to one registration.
    - **`Development:Sandbox:Provider` unset means "follow the agent role"** — not "docker", and not a separate default. That fallback is what made introducing the seam a runtime no-op on every existing node, so don't give it a default of its own.
  - **The `ISandboxRuntimeProvider` seam is not closed by this.** A container provider slots in behind it as one more implementation; **MXC remains the recorded long-term hard-isolation backend** and Docker is explicitly interim. Don't remove the seam on the grounds that "isolation is solved now" — and note MXC is early-preview and per its own README not itself a security boundary, so it is defence-in-depth rather than a guarantee.
  - Because AgentHome and Coder stay put, `Plans/PLAN-sandbox-hardening-2026-07-01.md` (default-deny egress, real cgroup ceilings, orphan reaper) is **complementary and not superseded** — it is what hardens them, and the container work does not do it for them.
- **Ollama was NOT removed** — it is a deliberately kept, gated, opt-in *secondary* provider (`XE_OLLAMA_RUNTIME_ENABLED` / `AddOllamaRuntime` in `AddNodeModelRuntimeExtensions.cs`), with 50+ live call sites on `IOllamaModelService`. What was removed is Ollama from *Aspire's dev orchestration* (no auto-provisioned container in dev). **llama.cpp is the default local runtime** (a supervised `llama-server` process per model, no daemon). Don't strip Ollama code paths.

### llama.cpp binaries

- `LlamaCppReleasePins.PinnedTag` is only the **offline-fallback floor**. The updater resolves a live "recommended" tag from GitHub Releases first, then a cached `installed-runtime.json`, and only then this constant. If you bump it, re-verify the archive layout for that tag.
- **Upstream ships no Linux CUDA prebuilt — Windows only.** On Linux, an NVIDIA box's `GpuVariantSelector` resolves to **Vulkan**, never CUDA. For CUDA on Linux, use either the bring-your-own-binary override (`XE_LLAMACPP_SERVER_PATH` + `XE_LLAMACPP_VARIANT`) or the in-app build-from-source feature — both exist and were live-verified against this box's GPU.
- **A GPU-variant binary can see ZERO devices and silently run on the CPU — the device audit exists to catch this (AUD4-03).** On this WSL2 box the shipped Vulkan build's `llama-server --list-devices` returns an empty list (no Vulkan ICD), so inference ran 4-thread CPU while the advisor/UI sized models to 16 GB VRAM. `IRuntimeDeviceAudit` (Application) composes the hardware profile + the selected variant + `ILlamaDeviceInventoryProbe` (a cached `--list-devices` parse, per binary path+mtime) into a `RuntimeDeviceAuditState {inferenceBackend, gpuExpected, cpuFallback, reason, remediation}`. **A failed/timed-out probe is "unknown", never "no GPU"** — it must never raise a false CPU-fallback alarm. On `cpuFallback`, `GetEffectiveProfileAsync` degrades the profile to CPU-mode (`VramKnown=false`) so the advisor + capacity gate size against RAM, not phantom VRAM; a `device_fallback` metric + a Warning fire once per binary. The audit is computed lazily on first demand and cached **only when determinate** — an indeterminate probe result ("unknown") is returned uncached so the next call re-probes (latching it would keep capacity/advisor trusting phantom VRAM until restart or a forced refresh; the probe layer likewise never caches failed probes). It is a pure function of the selected binary, so it is deliberately **not** wired per-spawn (zero warm-path cost). The hardware-profile endpoint returns the raw physical profile PLUS the audit block.
- Verify GitHub asset digests via the Releases API `digest: "sha256:..."` field. There are **no `.sha256` sidecar files** — don't go looking for them.
- Archive layout is not guaranteed to match `ServerRelativePath = build/bin/llama-server`; the resolver falls back to a recursive search by executable name. This was a real shipped bug — don't hardcode the extraction path.
- **GPU offload must be *owned* by exactly one mechanism per spawn — and which one depends on the mode.** The original bug was that neither owned it: `--n-gpu-layers` was silently missing on a non-CPU variant, CUDA initialized, zero layers offloaded, and the model quietly ran on CPU. The rule that came out of it ("always emit `-ngl`") is **no longer the whole truth**, and applying it blindly now *breaks* placement, because passing an explicit placement flag disables `--fit` (§llama-server spawn invariants):
  - **GPU explore** (the ordinary path) emits `--fit on --metrics` and **no `-ngl`/`-ts`/`-ot` at all** — llama.cpp auto-fits placement around the explicit `-c`. Verified live 2026-07-31: the whole launch line was `--fit on --metrics -c 65536 -fa on -ctk q8_0 -ctv q8_0 --jinja --cache-reuse 256`, with the GPU at 91–95% utilisation. A missing `-ngl` here is **correct**, not the old bug.
  - **Replay** (a frozen inference profile) emits explicit `-ngl`/`-ts`/`-ot` verbatim and deliberately omits `--fit` (`BuildReplayArgs`, `LlamaServerProcessSupervisor.cs:1482`). Here a missing `-ngl` *is* the old bug.
  - **CPU** emits neither — no `--fit`, no placement args.

  So if you touch `LlamaServerProcessSupervisor.BuildLaunchSpec`, confirm each mode still emits *its own* offload mechanism, and never "restore" `-ngl` to the explore path. The observable check that actually catches the original defect is not the flag but the behaviour: GPU utilisation during generation (see the smoke script, `scripts/run-gpu-smoke-local.sh`).
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

Route every writer of per-node state (settings, encrypted credential stores, hardware-profile cache) through **`INodeDataDirectory`** (`XE-Local-AI-Engine.Providers.Abstractions/INodeDataDirectory.cs`), which resolves to `LocalApplicationData` in desktop mode. Writing to `ContentRootPath` breaks on a packaged desktop build where the portable application directory may not be writable and is replaced during updates.

> **The bug this prevents, because it's a good one:** a stale dev `node-settings.json` got committed, the Web SDK auto-globbed it into the publish output as Content, and every fresh install was silently pinned to a nonexistent default model — first-run provisioning skipped its download with no error, just a permanently empty model store.

**Generalize from that one file: ANY runtime-written path under the project root must be `.gitignore`d the moment it is created.** The Web SDK globs the project directory into publish output as Content, so "committed" and "shipped inside the installer" are the same event here — and neither is announced. This has now happened twice, so treat it as a class, not an anecdote:

| Path | What leaked / would leak |
|---|---|
| `node-settings.json` | a dev node's settings pinned every fresh install to a nonexistent model |
| `XE-Local-AI-Engine.Client/generated-images/` | a tester's generated PNGs shipped inside the installer (~4.5 MB swept in by one `git add -A`, 2026-07-31) |
| `XE-Local-AI-Engine.Client/development/` | Development Mode engine state (workspaces, task records) |

The checklist when you add a feature that writes next to the app: (1) route it through **`INodeDataDirectory`** if it is per-node state — that is the real fix, because `LocalApplicationData` is outside the glob entirely; (2) if it genuinely must live under the project root, add it to `.gitignore` **in the same commit that introduces the writer**, never later; (3) `git status --short` after the first local run of the feature — an untracked path appearing next to the app is the signal, and it is the only warning you get.

> **`.gitignore` is not enough, and Development Mode is the proof.** Ignoring a path hides it from Git; MSBuild has never read `.gitignore`. `development/workspaces/` holds **clones of registered repositories**, so after three Dev Mode tasks against a small C# repository, `dotnet build` of `XE-Local-AI-Engine.Client` failed with a wall of `CS0101`/`CS0579` — the agent's workspace was being compiled into the host application (measured 2026-07-31). The build break is the loud half; the quiet half is that **model-written C# from an untrusted repository is a source file of this application** and would be compiled into its assembly on publish. Every runtime-written directory under the project root therefore needs an explicit `<Compile Remove>`/`<Content Remove>`/`<None Remove>`/`<EmbeddedResource Remove>` in `XE-Local-AI-Engine.Client.csproj` **as well as** the `.gitignore` entry. Keep the two lists in step; they are different guarantees.

### A Development Mode workspace inherits MSBuild config from ABOVE the node data directory

`Directory.Build.props`, `Directory.Build.targets` and `Directory.Packages.props` are found by walking **up** from the project until the first hit, with no upper bound — so a managed workspace inherits whatever sits above `INodeDataDirectory.Root`. Running from a source checkout puts that root inside this repository, and a registered repository's `dotnet restore` then picked up **this** repository's Central Package Management and failed `NU1008` for a package it declares perfectly legally inline (measured 2026-07-31, process sandbox provider — the shipped default). Validation was measuring the operator's build configuration rather than the repository under test, and the coder model rationally responded by inventing `Directory.Build.props`/`Packages.props` files to satisfy a CPM rule that was never the target repository's.

`DevelopmentWorkspaceProvider.EnsureBuildConfigurationBarrier` writes empty barrier files **one level above** each workspace to terminate the walk. Two things about it are load-bearing: it must stay *outside* the workspace (a file inside shows up in `git status` and lands in the attempt's changed-file manifest, which is the evidence the apply gate is built on), and a repository that ships its own copy of one of these files is unaffected, because the walk stops at the repository's own file first.

The **container** provider never had this: its mount root *is* the workspace, so the walk already terminated at the mount boundary. Do not conclude from a green Docker run that the process provider is fine.

### The Development attempt budgets: `MaxOutputTokens` is per CALL, reported usage is per ATTEMPT

`ChatOptions.MaxOutputTokens` is what the provider enforces on **each** round of the tool loop. `updates.ToChatResponse().Usage` is the **sum over every round**. Comparing the second against the first fails any multi-round attempt whose rounds together out-talk one round's budget — a limit no single call exceeded. Measured live 2026-07-31 with `unsloth/Ornith-1.0-9B-GGUF:Q4_K_M`: 33k cumulative output tokens under a 32768 per-call budget, attempt failed, completed work discarded. The input side had always got this right (`MaxCumulativeInputTokens = maxOutputTokens * providerCalls`); the output side had not. Both roles now go through `DevelopmentAttemptOutputBudget.Accept`. If you add a third role, use it.

### A Development attempt failure must carry a code, like the validation gate's does

`DevelopmentValidationFailureCodes` gives the deterministic gate a stable code plus operator-facing detail. The **attempt** lane had neither: `SanitizedReason` collapsed every non-cancel, non-security exception into *"failed before producing valid exact evidence"*, and because evidence is only persisted **after** an attempt passes its own checks, a failed attempt also persists **zero artifacts**. Two consecutive live failures with completely different causes were reported identically, and the first of them had produced the correct fix.

Engine-authored reasons now travel in `DevelopmentAttemptEvidenceException` (code + reason, clamped to the 1024-char `terminal_reason` column) and are surfaced verbatim. The sanitization rule is unchanged — anything the engine did not author still falls through to the generic sentence. **Do not widen that to arbitrary exception messages**: model output and absolute host paths are exactly what the generic reason exists to keep out of the operator record.

Related, and still open: the managed workspace is **per task, not per attempt**, and a failed attempt's writes stay in it. The next attempt therefore inherits them and must list them in its own changed-file manifest — which is a second, self-inflicted way to fail the manifest check. The failure reason now says so; the lifecycle question (roll back, or branch per attempt) is unresolved.

### Serilog silently severs OTLP log export — `writeToProviders` must stay `true`

`AddSerilog(...)` (`XE-Local-AI-Engine.Client/ConfigureServices.cs`) defaults `writeToProviders` to **`false`**, which makes Serilog the *terminus* of the logging pipeline: events reach Serilog's own sinks (Console, rolling file) and **no other registered `ILoggerProvider`**. The OpenTelemetry logger provider wired by `ConfigureOpenTelemetry` (`ServiceDefaults/Extensions.cs`) is one of those, so with the default **every `ILogger` call dead-ends before the OTLP log exporter**.

The failure is invisible in the obvious places: traces and metrics keep flowing, because they go straight to the `TracerProvider`/`MeterProvider` and never touch `ILoggerFactory`. So the Aspire dashboard shows healthy traces and **zero structured logs**, and `aspire otel logs` returns `[]` for every resource while `aspire otel traces` is full. It reads like a collector or filter problem; it is neither.

Diagnosing it: confirm `OTEL_EXPORTER_OTLP_ENDPOINT` is on the process (`tr '\0' '\n' < /proc/<pid>/environ | grep OTEL`) — traces arriving already proves the transport works. The Aspire resource is named **`app`**, not the project name, so a resource-filtered query for `XE-Local-AI-Engine.Client` errors out and a wrong-name query looks the same as an empty one. Kestrel/Hosting emit `ILogger` records at startup unconditionally; if none of those arrive, the pipeline is severed, not idle.

`Program.cs` calls `Logging.ClearProviders()` before `AddServiceDefaults`, so OpenTelemetry is the only other provider in the chain — forwarding cannot resurrect a duplicate console logger. Guarded by `SerilogProviderForwardingTests`, which asserts the observable consequence (a second provider receives events) rather than the flag, so it survives a restructure.

### Other silent-failure traps

- **A native process-probe with no timeout hangs provisioning forever.** `nvidia-smi`/adapter-enumeration GPU detection once had no deadline; a hung call stalled first-run model provisioning indefinitely with nothing logged. Any shell-out to a native diagnostic needs a per-call timeout *and* an outer deadline that degrades to a safe default (CPU variant). See §2's Windows section for the matching trap on the other side: a probe that degrades *silently* because the tool it names does not exist.
- **Desktop mode must treat Ollama as absent, not error-worthy.** Any Ollama call path (`/api/show`, `IOllamaApiClient`) must be provider-gated or tolerate connection-refused gracefully. Repeated source of chat failures and noisy stack traces in desktop mode, where no Ollama daemon runs.
- **The desktop loopback port is persisted on purpose** (`DesktopPortStore`, `desktop-port.txt`) so browser-origin-scoped `localStorage` prefs survive a relaunch. Don't revert to a random port per launch.
- Desktop shutdown needs explicit **SIGHUP** (Linux) and **CTRL_CLOSE_EVENT** (Windows, via `SetConsoleCtrlHandler`, blocking ~4s for graceful `ApplicationStopped`) handlers — .NET's default ConsoleLifetime covers neither, and without them console-close orphans `llama-server` again.
- Desktop publishing is asymmetric: Linux remains self-contained single-file; Windows publishes the client as
  framework-dependent DLL/deps/runtimeconfig files and overlays the framework-dependent
  `XE-Local-AI-Engine.WindowsLauncher` C# apphost. Windows requires x64 ASP.NET Core Runtime 10.0.10+ and must not ship
  `coreclr.dll`, `hostfxr.dll`, `hostpolicy.dll`, or the .NET Library License. The client stays explicitly untrimmed —
  trimming breaks EF Core / Serilog / FastEndpoints / MEAI reflection wiring.
- Desktop mode is opt-in via `XE_LAUNCH_MODE=desktop`; off-flag behaviour (headless/Aspire/CI) must stay byte-identical.

### The node is an MCP server too, and four things about it will bite you

The inbound surface (`/api/local/v1/mcp/server`, C# SDK 2.0.0 / spec revision 2026-07-28) is the
mirror of the `mcp/servers` registrations. Don't confuse the directions — they share only the name.

- **An `[McpServerTool]` parameter is REQUIRED unless it has a default. Nullability is not enough.**
  `string? agent` with no `= null` is advertised in `inputSchema.required`, and the SDK's binder
  rejects the call *before* your handler runs with "the arguments dictionary is missing a value for
  the required parameter 'agent'". Measured live — `tools/list` looked perfectly correct while every
  `tools/call` that omitted the parameter failed. Because injected parameters (`IProgress<…>`,
  `CancellationToken`) carry no default, they must sit *before* the optional ones, which makes the
  signature order look wrong and invites someone to "tidy" it. Don't.
- **`LocalApiSecurityMiddleware` matches on the `/api/local/v1` prefix alone.** `MapMcp` must mount
  inside it or the loopback peer/Host/Origin gate silently does not apply to the MCP endpoint. Same
  trap for any future non-FastEndpoints route.
- **A second auth scheme needs its own policy, and the policy must list only that scheme.** The
  `McpServer` policy names `McpApiKey` and nothing else, which is what stops an operator JWT from
  driving the MCP tool surface. `AddAuthentication(JwtBearer…)` keeps JWT as the default so no
  existing endpoint changes behavior.
- **The inbound key is hashed, and the GET *cannot* return it.** The node stores a SHA-256 digest, so
  the plaintext exists only in the `POST /mcp/server/api-key` response. That is enforced by the type
  system, not a comment: `GET` answers `McpServerApiKeyStatusResponse` (no key field) and only the
  generate path answers `GeneratedMcpServerApiKeyResponse`. Don't "helpfully" add a key field to the
  status shape or re-point the generate endpoint at `ToStatus` — either silently reverts the whole
  change. The digest is *still* AEAD-encrypted at rest, but now for **integrity**: without it, a
  writer of the database file could drop in a digest whose preimage they know. Keep both interceptor
  branches (`mcp_api_key_hash`) — deleting only one makes every read of the table throw.

Two facts worth not re-deriving: the **1.4.1 → 2.0.0 SDK upgrade is a source-level no-op here** (the
client only touches `McpClient.CreateAsync`, `ListToolsAsync`, the two transports and `McpException`,
none of which changed, and none of which are the `MCP9005`-deprecated Roots/Sampling/Logging), and
spec 2026-07-28 states **"Authorization is OPTIONAL for MCP implementations"** — so the pre-shared
bearer key is a supported deviation, not a violation, provided the server advertises no Protected
Resource Metadata.

### Tool calling has FIVE independent gates, and the UI shows only one of them

Before concluding "tool calling is broken", walk all five. A live evaluation burned most of a session on this: the installed-models row advertised a **`TOOLS`** capability chip, the node's *Enable tools* switch was on, the per-message *Local tools* toggle was on — and the model still answered `NO TOOLS AVAILABLE`, because two of the five gates are invisible from the chat screen.

In evaluation order:

| # | Gate | Where | Visible to the user? |
|---|---|---|---|
| 1 | `request.UseLocalTools` | per-message toggle → `NodeChatStreamService.cs:248` | yes, in the composer |
| 2 | `enableTools` | node setting → `GetEnableToolsAsync`, same line | yes, Node Settings |
| 3 | `resolution.SupportsTools` | detected **offline from the model's chat template** by `IGgufModelCapabilityResolver` | **this is what the `TOOLS` chip shows** |
| 4 | `AgentHome:ToolCapableModels` allow-list | `LocalToolOfferProvider.IsToolCapable` | **no** — an operator allow-list, unrelated to gate 3 |
| 5 | the agent's `AllowedToolNames` | `AgentDefinitionResolver` intersects offered ∩ allowed | **no** — and the seeded **Default Assistant ships with 0 tools** |

The trap is the relationship between 3 and 4. **Gate 3 is a statement about the model; gate 4 is a statement about operator permission.** They come from entirely different sources and are free to disagree, so a `TOOLS` chip is *not* a prediction that tools will be offered — it only means the chat template supports them. A model can be genuinely tool-capable, correctly chipped, and still receive nothing because it is absent from a static allow-list that ships as
`["qwen3:8b", "bartowski/Qwen2.5-3B-Instruct-GGUF:Q4_K_M"]` — i.e. **none of the models the app's own recommender offers**.

> **Gate 4 no longer silently excludes a model you just downloaded (`4e9d22c8`) — verified live on Windows 2026-08-03.** `ToolCapableModelRegistrar` reads the template-detected `LocalModelDescriptor.IsToolCapable` and **unions the capable names into the persisted allow-list**, both per download (`RegisterIfToolCapableAsync`) and for already-installed models (`BackfillInstalledAsync`), logging `Model <name> advertises tool calling in its chat template; added it to the tool-capable model list.` Measured on a node whose data root had been wiped: after downloading two models the persisted `node-settings.json` read
> ```json
> "toolCapableModels": ["bartowski/Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M", "unsloth/Qwen3.6-27B-GGUF:Q8_0"]
> ```
> Note what that means in practice: the **persisted setting replaces the shipped defaults outright** — neither `qwen3:8b` nor `bartowski/Qwen2.5-3B-Instruct-GGUF:Q4_K_M` appears once the node has written the setting. So "add your model to `AgentHome:ToolCapableModels` before testing tools" is now **stale advice** for any model the engine itself installed. It still applies to a model whose template does not advertise tools (gate 3 false ⇒ never auto-added) and to Ollama-side names. Gate 4 remains a real, separate gate — it is just no longer the one that quietly eats a freshly downloaded model. Gates 3 and 4 are still free to disagree in the other direction, and the note below about matching being `Ordinal` at the gate but `OrdinalIgnoreCase` at registration is why the registrar stores the descriptor's own casing.

Note the failure is *partial*, which makes it harder to spot than a clean "no tools": a non-allow-listed model still receives `get_current_time` and `calculate` (the whole production `LocalAgentToolRegistry`), while the coder tools, the knowledge-base tools, `spawn_subagent` and **every MCP tool** are dropped. So "tools work" and "tools are broken" can both look true in the same conversation.

### Passing all five gates is still not enough — llama.cpp must be able to COMPILE the tool schemas

A sixth thing can fail *after* every gate above says yes, and it looks nothing like a capability problem. llama-server turns each offered tool's JSON schema into a GBNF grammar for constrained decoding, and its converter has a hard repetition ceiling. Exceed it and the turn dies in ~80 ms with a raw sampler message:

```
parse: error parsing grammar: number of repetitions exceeds sane defaults, please reduce the number of repetitions
-> HTTP 400 {"message":"Failed to initialize samplers: failed to parse grammar"}
```

**Do not read that as "the model can't do tool calling".** A capture run in 2026-08 filed it that way against the first-run 0.5B, and the diagnosis was wrong in every particular: the model tool-calls correctly, it *was* in the allow-list, and the UI toggle was right to be enabled. The defect was ours — our own tool schemas.

Measured against `llama-server` b10201 (source-build CUDA) with `bartowski/Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M`, one keyword per request:

| keyword | breaks at | safe at |
|---|---|---|
| `maxLength` | 2000 | 1990 |
| `minLength`, `minItems`, `maxItems` | 8000 | — |
| `pattern` with a `{0,8000}` quantifier | yes | `{0,63}` fine |
| integer `minimum` / `maximum` | **never** — ok at 100000 | — |

The ceiling is also **combined across the whole `tools` array** (a second error variant names "rules ... multiplied by the new repetition"), so a per-keyword threshold is not sufficient on its own: the full production offer still failed with every `maxLength` clamped to 2048 and only compiled at 1024. That is where `LlamaGrammarToolSchemaCompatibility`'s bound comes from — it is an empirical figure for *our whole catalog*, not a number from llama.cpp's source. Re-measure it if the offer grows.

Two traps worth keeping:

- **The bug hides behind reasoning models.** The same request that 400s on Qwen2.5 succeeds on Qwen3.6-27B — not because the 27B is more capable, but because it emits `reasoning_content` first, so llama.cpp never enters the constrained branch and never compiles the grammar (its server log contains zero grammar lines). Both report `chat format: peg-native`. A reasoning model is therefore **useless as a positive control** for this failure; reproduce on a non-reasoning tool-capable model.

  **No automated suite in this repo can catch a regression here, and the one that looks like it can, cannot.** `ChatLocalToolsE2ETests` drives the local-tools toggle end to end and asserts a real tool call — but its chat backend is **FakeOllama**: `ToolCapableModelName = "qwen3.5:0.8b"` is a canned fake model (`TestingWebAppFactory.cs:59`) whose tool call comes from `FakeOllamaState.ToolCallScript`. No chat template, no `json-schema-to-grammar`, no sampler. Adding a "non-reasoning model" to that suite would change nothing — there is no llama.cpp in it at all. The unit tests in `LlamaGrammarToolSchemaCompatibilityTests` close the other half (our schemas stay under the bound) but cannot notice llama.cpp *changing* the limit, because the bound is a constant we measured by hand.

  That gap is what `scripts/run-tool-grammar-smoke-local.sh` exists for: an opt-in live smoke against a real `llama-server`. Its load-bearing assertion is the **negative control** — the unsanitised offer MUST still produce the grammar 400. If it returns 200, the run proved nothing, and there are exactly two causes: the model is a reasoning model (inert smoke), or llama.cpp raised its limits and `MaxGrammarRepetitionBound` needs re-measuring. Treat a "passing" smoke without that failing control as no evidence at all.
- **Fixing it by shrinking the constants is the wrong reflex.** The bound in a tool's `ParameterSchema` is advisory to the model; the handler's own validation is authoritative, and the same schema drives `ToolArgumentRepairAIFunction`'s argument checking. Clamping `spawn_subagent`'s `task` from 8000 to 1024 would tell the model a lie and weaken validation to match it. The compatibility pass strips the offending keyword **on the llama.cpp wire only**, so Codex and Azure Foundry keep the full schema. `KnowledgeQueryLimits` already carries a scar from the other half of this — its comment records the tool schema's "former advisory 2000".

MCP servers supply third-party tool schemas the node does not control, so this is not a closed problem: the compatibility pass is the guard, and `FailureCategory.ModelCapabilityUnsupported` carries the translated message when something still slips through.

### Capability detection is a substring scan of the chat template — know what it can and cannot see

`GgufCapabilityDetector` decides a GGUF's advertised capabilities by scanning `tokenizer.chat_template` for literal substrings: tools from `tool_calls`/`tool_call`/`function_call`/`tools`, reasoning from `<think`/`enable_thinking`/`reasoning_content`. Two consequences that have already bitten:

- **A reasoning model can show no `THINKING` chip and still reason.** Measured on `unsloth/gpt-oss-20b-GGUF:Q5_K_M` (2026-07-31): its 17k-char OpenAI *harmony* template contains **zero** occurrences of all three reasoning markers, but 12 of `<|channel|>` and 4 of `reasoning_effort` — it emits reasoning on an `analysis` channel. **The detector now knows about that channel** (see the next bullet) and reports it as a *separate* `native_reasoning` capability — but it is still **not** the graded `THINKING` chip, and it never will be. The model reasons anyway, because the enforcing gate's false branch (`InvocationAgentFactory`, the `else if (IsReasoningRequested(...))` arm) deliberately **omits** the `think` field and lets the template's baked-in behaviour through. So the graded chip remains a statement about *"is a graded `think:<level>` control available"*, not about *"does this model reason"* — two questions with two answers, deliberately.
- **This was fixed by adding a DISTINCT capability, not by widening the graded marker list — and the distinction is the whole point (F-014, `a3496f60`).** `GgufCapabilityDetector` now reports a second, separate reasoning capability: `NativeReasoningCapability` (`"native_reasoning"`), detected by `NativeReasoningTemplateMarkers` (`<|channel|>analysis`, `reasoning_effort`). It is computed **mutually exclusive** with graded thinking — `!isReasoningCapable && ContainsAny(...)` — so a harmony model is *never* flipped into the graded branch. That exclusion is load-bearing, and re-check it before touching the detector: moving such a model into `if (SupportsThinking)` would write `think` and, on `none`, set `enable_thinking=false` via `chat_template_kwargs` — a kwarg the harmony template does not have (measured: 0 occurrences; it takes `reasoning_effort`), producing a graded menu whose levels do nothing **and** a broken reasoning-off path. Adding harmony markers to the *graded* list is still wrong; adding them to the native list is what already happened.
- The tools marker list includes the bare English word **`tools`**, matched anywhere in the raw template including comments and dead branches. `<think` is a tag prefix; `tools` is not. Treat a `TOOLS` chip as weaker evidence than a `THINKING` one.

Gate 5 catches the rest: the Default Assistant grants zero tools, so even a fully permitted model gets nothing on the default agent. Use the Coder (read-only) agent, or grant tools on the definition, when testing the positive path.

Historical note, so nobody re-introduces it: gate 4 used to be **captured at DI composition** and never re-read, so editing the allow-list did nothing until the node restarted — while gates that read the *same* setting (`GetToolCapableModelsEndpoint`, `OrchestrationResolver`) were already live. It is now read live per offer. Do not "optimize" it back into a constructor field — the read resolves through `CachedNodeSettingsStore`, so it is a memory-cache hit that `SaveAsync` re-primes, not a file read.

---

### Windows is a shipping target, and an inline `OperatingSystem.IsWindows()` is how its branches go untested

Nobody working in this repo has a Windows machine. A 2026-08-02 pass over the *product* paths (as opposed to the test suite's own portability, which was a separate pass) found four defects that compiled, passed every test, and were wrong or absent on Windows. Three of the four share one shape: **the OS decision was inline, so the branch that was wrong was the branch no test on this box could reach.**

The rule that came out of it: when behaviour differs by OS, make the platform a **parameter** — a constructor argument, an injected environment, a factory selector — and unit-test both branches here. `ProcessGpuVendorProbe.ProbePlatform`, `IHardwareProbeEnvironment.IsWindows` and `NodeDataProtectionKeyRingFailClosed.ResolverFactoryFor(bool)` are the shapes to copy. A test that begins `if (!OperatingSystem.IsLinux()) Skip.Test(...)` proves nothing about the platform you are shipping to; check the skip reasons before trusting a green run as Windows evidence.

Better still, where it is affordable: **delete the branch**. Development Mode's `list_files`/`search_text` used to shell out to `find` and `grep`, and now do the work in managed code on every platform (`WorkspaceFileScanner`, at `Client.Application/Services/Workspace/WorkspaceFileScanner.cs` — there is no type named `DevelopmentWorkspaceFileScanner`, despite earlier notes) — so the Linux test *is* the Windows evidence, and the engine no longer depends on which coreutils build happens to be first on `PATH`.

Four Windows facts worth not re-deriving:

- **`find.exe` exists on Windows and is not GNU find.** `C:\Windows\System32\find.exe` is the DOS tool; it rejects `-maxdepth`, `-iname` and `-prune`. `System32` normally precedes Git for Windows' `usr\bin` on `PATH`, so a bare `find` resolves to the DOS tool **even where GNU find is installed** — which makes "it works on my box with Git installed" an unreliable check. `grep` simply does not exist. **A shipped RC cannot assume Git for Windows.**
- **`wmic` is gone.** It is a deprecated Feature-on-Demand, not installed by default on current Windows 11. Anything reading `Win32_*` must go through `Get-CimInstance` (Windows PowerShell 5.1 is in-box; prefer it by its absolute `System32\WindowsPowerShell\v1.0\powershell.exe` path so a planted `powershell.exe` cannot answer). Keep `wmic` as a last candidate rather than deleting it — a missing executable fails to start immediately and costs nothing.
- **`git diff --check` fails on a repository that legitimately stores CRLF.** Git's default whitespace rules count the CR of a CRLF pair as trailing whitespace, so the Development validation gate's *first* command exits 2 on every changed line of a Windows-native repository. Neither `core.whitespace=cr-at-eol` nor deleting the check is the answer — both retire a real check on LF repositories. The policy is derived per path from `git ls-files --eol` (the **`i/`** column: with `core.autocrlf=true`, common in Git for Windows' system config, the worktree is CRLF while the blob is LF) and written to `.git/info/attributes`. Mixed repositories are the common case, not the exotic one — this repo stores 4243 files as LF and one as CRLF.
- **DPAPI failing open is invisible.** `ProtectKeysWithDpapi` plus the framework's stock `DefaultKeyResolver` means an unreadable key ring silently mints a new key and orphans every `*.enc` credential, with no exception and no log line. The fail-closed decorator is now on both branches; note its classifier necessarily differs per scheme (a DPAPI failure surfaces as `CryptographicException`, not as this node's own exception type), which is why it was inert on Windows before rather than merely unregistered.

**Both callers are fixed, but not the same way, and the difference is the interesting part.** Development Mode surveys a host worktree whose path it knows, so it calls `WorkspaceFileScanner` directly. Coder reads a jail whose root is provider-internal *by design*, so its surveys became provider OPERATIONS (`ISandboxRuntimeProvider.ListFilesAsync`/`SearchTextAsync`) sitting next to `ReadFileAsync` — only the provider knows how a sandbox path maps to bytes, and only it can apply its own confinement to that mapping. Do not "unify" these by handing Coder a host path: exposing the jail root to callers is exactly what `ResolveJailPath` exists to avoid. The contract's default implementation THROWS rather than returning an empty list, because an empty listing reads as "the workspace is empty" — a different and misleading answer.

`docs/runbooks/windows-rc-verification-runbook.md` is where each of these says what a tester should now see, and — for the parts no Linux test can reach — what they must record instead.

### Measured on a real Windows 11 box, 2026-08-03 — five traps that make a Windows run lie to you

The first session to actually execute this repo's suites on native Windows. Every item below was measured, not inferred.

- **A bloated `PATH` makes `cmd.exe` see an EMPTY `%PATH%`, and that silently breaks the product's own tests.** Found with 153 dead `%TEMP%\xe-dev-rev-*\…\dotnet\.dotnet\tools` entries (27 KB, 171 entries). `where.exe ping` resolved fine while `cmd /c ping` answered *"'ping' is not recognized"*. Three `ProcessSandboxRuntimeProviderTests` failed at **18–81 ms** because `SleepCommand` on Windows is `cmd.exe /c ping -n 31`, which exited instantly instead of sleeping 30 s — so cancel/timeout/tree-kill had nothing to kill. Stripping the dead entries took `PATH` 28 387 → 847 chars and the class went from 3 failed to **0 failed** with no other change. **Before believing any Windows test failure, check `(cmd /c "echo %PATH%").Length` against `$env:PATH.Length`.**
  - The leak's source is ours: `DevelopmentWorkspaceTools.BuildEnvironment` points `DOTNET_CLI_HOME` at a fresh per-task directory and does **not** set `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0`, so the .NET CLI's first-run registers `$DOTNET_CLI_HOME\.dotnet\tools` on the **persisted user** PATH, once per task, forever. Confirmed live: the count rose 147 → 153 during a single session. Two prefixes leak — `xe-dev-rev-*` (unit fixtures) and `xe-local-ai-engine-e2e-*` (the E2E fixture). `DOTNET_SKIP_FIRST_TIME_EXPERIENCE` is **not** the fix; it is a no-op in .NET 10 (see §1).
- **Editing a file on Windows rewrites it CRLF, and only `scripts/*` and `*.sh` are pinned.** `.gitattributes` covers 57 of 4271 tracked files; **2401 `.cs` files are stored LF and unpinned**. A one-line version bump showed as **104 changed lines**, a 16-line fixture edit as **486**. With `core.autocrlf=false` git records that as real change, so whole-file whitespace churn rides into commits unnoticed. Check `git diff --stat` against what you believe you changed, and `git ls-files --eol <path>` (the `w/` column) before committing. A repo-wide `* text=auto eol=lf` would close it.
- **`pnpm` has no `.exe` on Windows and `CreateProcessW` cannot run a `.CMD`.** pnpm installs shims (`pnpm`, `pnpm.CMD`, `pnpm.ps1`); the real binary is content-addressed in the store at a path that changes with every version. `UseShellExecute = false` + `FileName = "pnpm"` therefore throws *"The system cannot find the file specified"* on a box where `pnpm --version` works fine. Resolving the shim's full path is **not** enough — launch through `cmd.exe /c`, which also preserves stdout/stderr redirection. Fixed in `XEReactClientFixture`; the repo's JS already had the guard at `scripts/RunPackageTool.mjs`.
- **`-SkipUpload` is not credential-free.** `package-tester-win.ps1` throws without `VPK_TOKEN` even for a rehearsal — it downloads the previous release from the private tester repo to build the delta. It fails in ~1 s, before the gate suite, so it is cheap; but a runbook that says "just run `-SkipUpload`" is wrong.
- **Retired private-tester path:** the deprecated manual packager required an `Iv…` GitHub App client ID; numeric App IDs and `Ov…` OAuth App IDs produced unusable private-repository tokens. The official public updater no longer uses device flow or a client ID: it reads public GitHub releases anonymously. Preserve this note only when maintaining the deprecated private packaging reference, and do not reintroduce that authentication contract into `AppUpdateChannelOptions`.

Two more, each already actioned but worth not re-deriving:

- **`CopyToPublishDirectory="Always"` does not mean "every publish overwrites".** Measured: after the destination is disturbed, repeat `dotnet publish` runs leave `appsettings.AppUpdate.json` **missing** — silently, exit 0 — until the **source** timestamp changes. The project comment now records this limitation. The official workflow and deprecated manual packager must both reject a package when the selected update-policy file is missing or wrong; a raw manual publish is not release evidence. The channel switch (`-p:UpdateChannel=main`) does work correctly.
- **Junctions prove the link guards that symbolic links cannot.** Seven symbolic-link tests skip on a stock box (no `SeCreateSymbolicLinkPrivilege` without Developer Mode or elevation). Junctions are the same reparse point, need no privilege, and are the likelier real-world shape. `Testing/JunctionSupport.cs` plants them; the pattern now covers the scanner plus AgentHome prep, patch apply, sandbox `CopyInto` and registered-path resolution. It does **not** extend to `DevelopmentWorkspaceGitConfig.RestoreMinimal`, which swaps a *file* — a junction is a directory reparse point only, so that guard stays unproven on an unprivileged Windows box.

---

## 3. Models, inference, retrieval

### Recommendation: walk the quant ladder, never pick one quant

Both advisor lanes rank *every* file in a repo by `QuantLadder.QualityRank` and take the **highest-quality quant at/below the ceiling that fits**, stepping further down toward `QuantLadder.FloorRank` if nothing at ceiling fits (`ModelFitRefreshService.cs:598`, `CatalogRecommendationService.cs:215`).

The old design picked one quant (Q4_K_M else `files[0]`) and dropped the *entire repo* if it didn't fit — so big/new models whose default quant didn't fit never appeared at all, even though a Q3/Q4 variant would have run fine.

**Carve-out: MTP draft models are NOT ladder entries, and the fix is an identity rather than a filter (F-011, `26ef76e3`).** `unsloth/gemma-4-12b-it-GGUF` ships speculative-decoding drafters under `MTP/`, and their file names parse to the *same quant tokens* as the root weights. Discovery had one companion concept (`IsMmprojCompanion`), so every drafter entered the base model's quant list as a first-class variant — and because that list sorts ascending by size, three 0.4–0.8 GB drafters took the **top three rows**, each labelled with the base model's quant and graded "Highest quality". `Q8_0` meant either 0.4 GB or 11.8 GB depending on which row you clicked, and since the registry keys a model as `{repoId}:{quant}`, both mapped to the **same key**.

- The root cause is one assumption made in two places — "a file name that parses to a quant is a base-model chat variant". `GgufModelRegistry.Rescan` made it too, so a downloaded drafter also appeared in the installed list as an ordinary chat model.
- It is fixed as an **identity, not a filter**, because the app *has* a `Draft model (MTP)` mode — these files are meaningful and must stay downloadable. `GgufDraftModel` recognises a drafter (an `MTP/` path segment or an `mtp-` file-name prefix) and marks its quant: `Q8_0` → `MTP-Q8_0`. That one marker keeps the label unambiguous, the registry key distinct, and gives every consumer a cheap test.
- Consequences to preserve: the picker lists drafters **last**, badged "Draft model" (`isDraft` on the inspect DTO); **neither advisor lane can pick one** (`GgufVariantRecommender` and the ladder walks in `ModelFitRefreshService` / `CatalogRecommendationService`) — critical, because a drafter is simultaneously the smallest *and* the highest-quality-looking file in the repo, so it would win fit-first ordering outright; a downloaded drafter registers as `GgufRole.Draft` / `ModelKind.Draft` so chat pickers (`kind === "Chat"`) and the local-default resolver drop it; Node Settings' draft slot offers `Draft` + `Chat`.
- **Deliberately narrow** so a base model that merely *carries* MTP layers is never misread: `unsloth/Qwen3.6-27B-MTP-GGUF` (a real 21 GB chat model) matches neither rule. There are tests in both directions — don't broaden the match to the bare string `MTP`.

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

### NVFP4 **GGUF** runs here, natively — NVFP4 **safetensors** never will

Answered by research at least three separate times; the answer lives here now so it does not need re-deriving.

**It works, live-verified 2026-07-31 on this box** (`Plans/2026-07-31-live-ai-evaluation.md`, model matrix): `tngtech/Qwen3.6-27B-NVFP4-GGUF` — 18.5 GB file, 22.7 GB VRAM peak — downloaded, installed, appeared in the picker with a Reasoning badge, loaded onto the GPU and generated at **95% GPU utilisation**. Every layer of the stack lines up:

- **llama.cpp**: `GGML_TYPE_NVFP4` (type 40) merged over late-March → April 2026; the Blackwell-native tensor-core kernels landed around **b8967** (2026-04-29). `LlamaCppReleasePins.PinnedTag` is `b10201`, comfortably past it. Kernels exist for CUDA, SYCL and Vulkan.
- **Hardware**: the box is sm_120 Blackwell and the in-app source build detects `CMAKE_CUDA_ARCHITECTURES=120a` live, so this is the real tensor-core path, not just the memory saving. On sm_89 and older the GGUF still loads but collects **only** the size reduction — reported upstream gain is prefill +43–68%, token generation unchanged.
- **Our quant plumbing** (`75cc519a`): `GgufQuantParser` recognizes the `NVFP4` token; `QuantLadder` ranks it **above** MXFP4 (16-element blocks with an FP8 E4M3 scale beat MXFP4's 32-element E8M0) at `Balanced` tier and reports it via `IsNativeFormat`; `MemoryFitEstimator` prices it at 4.25 bpw.

**The failure everyone hits is the container, not the format.** An NVFP4 **safetensors** checkpoint (ModelOpt / compressed-tensors — e.g. `unsloth/Qwen3.6-27B-NVFP4`) does **not** work and no llama.cpp bump fixes it: the convert script for compressed-tensors NVFP4 is still an open upstream PR. If someone reports "NVFP4 is unreachable", check which container they tried before believing the format is at fault. Requires the CUDA binary, too — this box's *default* variant resolves to Vulkan with no ICD and silently runs on CPU (see §2 llama.cpp binaries).

Two live caveats worth keeping:

- **The 4.25 bpw price is one measurement, not theory.** It came from `s-batman/Ornith-1.0-9B-NVFP4-MTP-GGUF` shipping byte-identical MXFP4 and NVFP4 conversions of the same model from the same converter (5.45 GB each). Cross-repo NVFP4 sizes for one base model vary widely, so a fit estimate for an unfamiliar NVFP4 repo is soft — prefer the real blob size when you have it.
- **An unrecognized quant token is not merely mis-priced — it makes the file disappear.** The file fails `IsUsableGgufFile`, so a repo whose files all use that token vanishes behind "No GGUF repositories matched that search". That is exactly how NVFP4 looked unsupported before `75cc519a`: `tngtech/Qwen3.6-27B-NVFP4-GGUF` was invisible and `s-batman/Ornith-1.0-9B-NVFP4-MTP-GGUF` was visible with its NVFP4 file silently absent from the picker. Any future native format (the next MX/FP variant) will fail the same silent way until the regex learns it.

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

- **Readiness is separated from the stream-idle watchdog.** A cold model load must happen BEFORE the streaming watchdog is armed, or a big model gets killed at the (shorter) `StreamIdleTimeoutSeconds` and can never load through chat. The invocation runner warms a local (llama.cpp) model via `ILocalModelProvider.WarmModelAsync` (`InvocationRunner.PrepareLocalRuntimeAsync`) — reporting `InvocationRuntimePhase` (PreparingRuntime → LoadingModel → Generating) — and only then streams. Cloud/Ollama warm is a no-op. A new `InvocationState.RuntimePhase` field rides `InvocationState.Clone()` (the add-new-fields-to-`Clone` gotcha in §5 applies).
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
- **A pooled (embedding/rerank) forward pass must fit in ONE physical micro-batch.** llama-server is non-causal for pooled roles, so it **rejects — never splits** — any single input longer than `n_ubatch` (default **512** tokens): `500 "input (N tokens) is too large to process. increase the physical batch size (current batch size: 512)"`. The usable embedding input is therefore 512 tokens, **not** the `-c` context you ask for and **not** the window the model advertises — so ordinary ~2000-char markdown chunks (~520–680 real tokens) blow past it and *every* KB document fails on a default node. Fix in place: `LlamaServerProcessSupervisor.AppendPooledForwardPassBatchArgs` emits `-b/-ub = effective context` for Embedding + Reranker (chat is deliberately excluded — causal decode splits correctly, and `--fit` owns that trade-off). llama.cpp **clamps** `-b/-ub` down to the context, so over-requesting is a no-op, not an error, and it composes with `--fit on`. Related risk left open: the `chars/4` token estimator (`ChunkTokenApproximation`) under-counts real markdown by 5–36% despite a doc comment claiming it over-estimates (true for English prose only), and the 32-token `EmbeddingWindowReserveTokens` cannot absorb the gap.

---

## 4. Agent Mode, MAF, sandbox, cloud providers

### Sandbox: the two guards are mandatory *together*

`ResolveJailPath` (`ProcessSandboxRuntimeProvider.cs:1021`) canonicalizes via `Path.GetFullPath` + prefix check — which collapses `..` but does **not** resolve symlinks. A path under the jail can still traverse a symlink planted by a command that ran with the jail as CWD. **Every read/write leg must also pass `EnsureNoSymlinkComponentsUnderJail`** (~:555) before opening.

Host-file reads use a **no-follow open**: `OpenNoFollow` (~:714) P/Invokes raw `open()` with `O_RDONLY|O_NOFOLLOW|O_CLOEXEC`. Do **not** cast `O_NOFOLLOW` to `FileOptions` and pass it to `File.OpenHandle` — the runtime validates the enum and throws `ArgumentOutOfRangeException` on *every* file, not just symlinks. On `fd < 0`, check `Marshal.GetLastPInvokeError()` (errno 40 = ELOOP = symlink leaf).

The **byte-cap re-check must cover post-sizing growth**: size a buffer from `RandomAccess.GetLength`, read exactly that many bytes, then probe one more byte at `length`. A >0 probe means the file grew after sizing — block the whole copy (return null). Never emit a torn or truncated copy.

**Known gap (accepted, Low):** coder-mode's `ExecuteAsync` (backing `list_files`/`search_text` via allow-listed `find`/`grep`) is *not* independently jailed — it relies on `WorkingDirectory` confinement, which does not re-apply the symlink guard. Not model-exploitable today (coder can't create symlinks; host→sandbox copy rejects reparse points), but it widens the moment a write-capable sandbox tool ships.

### Dev Mode's file guard has TWO predicates on purpose — do not merge them

`ISensitiveFileExclusionService` (`Services/Workspace/ISensitiveFileExclusionService.cs`) answers two different questions, and collapsing them back into one breaks Development Mode's primary use case:

- **`IsSecret(name)` — "is this a CREDENTIAL?"** Gates every **read** path (Dev Mode `read_file`/`search_text`/`list_files`, Coder `read_file`).
- **`IsExcluded(name, isDir)` — "is this worth COPYING?"** Gates the workspace **copy** only. Its set is `SecretNames ∪ CopySkipNames`, where `CopySkipNames` = `.git bin obj node_modules dist coverage .vs .idea`.

Gating a **read** on `IsExcluded` refuses `obj/project.assets.json` to an agent diagnosing a failed restore — Dev Mode's whole point — while protecting nothing, because build output is not a credential. This conflation was shipped once and correctly rejected in review. Tripwire in place: `CoderWorkspaceReaderTests.ReadFile_WhenBinary_…` seeds `bin/data.bin` on purpose; if it ever fails with "excluded because files with that name commonly hold credentials" instead of "binary", a read path has been rewired to the copy filter.

**Accepted trades that will later look like regressions:** `.npmrc`, `*.pem`/`*.pfx`/`*.p12`, and `.env.*` (which matches `.env.example`) are in `SecretNames`, so they are dropped from the AgentHome **copy** — an in-sandbox `npm install` against a **private registry** fails to restore, and a repo's TLS test-fixture certs vanish from the sandbox. Deliberate, not bugs. **What the read guard does NOT close:** Dev Mode *executes* the repository's own build/test commands, so a test that prints `.env` puts those bytes in captured stdout, which reaches the same attempt context and cloud role route. The one-step read and the patch `rename from`/`copy from` bypass are closed; execution is not.

**Rotate any dev DB created under the old committed key.** The base64 `node-sqlite-key` was removed from `AppHost/appsettings.Development.json`, but it lives in git history — deleting the line does not make data sealed under it safe. `node.key` and `*.sqlite*` are now gitignored.

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

### A tool handler can NEVER block waiting for a human

`StreamIdleWatchdog.WithIdleTimeout` (`Services/Invocation/Resilience/StreamIdleWatchdog.cs`) wraps the whole `RunStreamingAsync` call **including the gaps where `FunctionInvokingChatClient` executes tools**, and `StreamIdleTimeoutSeconds` is **60** (`Models/TimeoutSettings.cs`). A tool that parks inside its handler waiting for an operator answer trips the idle watchdog and the turn fails. That is why `ask_user` ships **`RequiresApproval = true`** — structural, not a risk verdict: the flag makes the framework END the streamed segment and surface a `ToolApprovalRequestContent`, so the human wait happens in the runner's out-of-stream approval round-trip, not inside the stream. Flipping it to `false` does not merely skip a prompt, it breaks the feature. Free correctness side effect: the sub-agent, scheduler, and inbound-MCP paths already strip approval-required tools (see §4), so unattended callers never receive `ask_user`.

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

**Pre-RC manual check still outstanding, agent skills import:** `GitHubSkillArchiveDownloader.cs:67-68` builds the fetch URL as `https://github.com/{owner}/{repo}/archive/HEAD.zip`, expecting GitHub to answer with a 302 to `codeload.github.com`. This is **not** live-verified — `SkillImportServiceTests.cs` deliberately drives it through a fake handler that enqueues the redirect (network-free by design, matching this repo's test posture) — so the real GitHub response shape, hop count, and content type have never been observed by this code. Run one real import against a small public repo before shipping.

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

### Endpoint exception handling is mature — don't mass-remove catches

Global handling already exists: `ConflictExceptionHandler` (→ 409) + `DefaultExceptionHandler` (→ 500) in `Client/ExceptionHandling/`, plus FastEndpoints `UseProblemDetails`. There are **40+ custom domain exceptions** and **zero** `throw new Exception(` solution-wide. So a "clean up redundant endpoint try/catch" refactor is mostly wrong: most catches are **load-bearing** — they map a domain exception to a specific status, or degrade gracefully on a polled endpoint (`catch (OperationCanceledException) when (ct.IsCancellationRequested) → throw` guards). Do **not** mass-remove them. When you *do* narrow one, wire the mapping first: a catch converted to a custom exception with no handler still 500s, just more verbosely — which is churn, not a fix. And a 401 is not a free substitute for 409: a 401 on the operator's *own* request can trip the client auth-interceptor into logging them out, which is why `ConnectConnectionEndpoint` maps an expired **worker** token to 409, not 401.

### Seven validation exceptions are mapped globally to 400 — don't re-add per-endpoint catches

`DomainValidationExceptionHandler` (`Client/ExceptionHandling/`, registered after `ConflictExceptionHandler` and before `DefaultExceptionHandler`) maps `ScheduledJob`, `CustomTool`, `McpServer`, `SlashCommand`, `PlaybookAction`, `AgentDefinition` and `AgentSkill` `…ValidationException` to a 400 carrying FastEndpoints' own `ProblemDetails` shape — `errors: [{name: "generalErrors", reason: message}]`, `detail` = that reason, `instance` = request path, `traceId` = `HttpContext.TraceIdentifier` — byte-identical to the `AddError(message) + Send.ErrorsAsync()` pair it replaced (pinned by `ApiFoundation/DomainValidationExceptionHandlerTests`).
A **new single-message** validation exception belongs in that handler's type switch, not in a per-endpoint `catch`. Deliberately **not** in it: `PreviewWorkflowValidationException` (carries a *list* of errors — one failure per entry, needs explicit multi-error handling) and `SelectedFolderValidationException` (an aggregate of 400/404/409 faults — split the type first). `SlashCommandConflictException` stays a local 409 catch.

### Client conventions

- Modals go through the shared **`DialogShell`** primitive — don't hand-roll a Mantine `Modal`.
- Chat capability flags (file/image attachments) are a **static client-side constant** (`NodeCapabilities.ts`), not server-composed. Don't assume a backend capability endpoint drives chat UI gating.
- Any bounded Mantine `NumberInput`/`Slider` that must distinguish "unset" from "user edited" needs a post-mount `ready` guard before wiring `onChange` to persistence — Mantine fires a **spurious `onChange` on mount** with a default/min value, silently overwriting an intentional "no override". This bit the sampling-options dialog twice.
- **Code/text viewing and editing goes through the shared `CodeEditor`** (`src/core/ui/components/CodeEditor/`, Monaco) — don't reach for a read-only `Textarea` or a second highlighter. Two rules keep it cheap: (1) `MonacoRuntime.ts` imports **`editor.api` + hand-picked Monarch grammars, never `editor.main`** — `editor.main` drags in every language service and its worker (the JSON service alone measured +1.6 MB), and everything from `monaco-editor` is forced into one `monaco-editor-*` chunk by `vite.config.ts` `codeSplitting.groups`; (2) that chunk plus `editor.worker` are measured under **`lazyEditorJavaScriptBytes`** in `config/bundle-budget.json`, *not* the app budget — `scripts/CheckBundleBudget.mjs` matches them by chunk name, so **renaming the group or the runtime module without updating the pattern silently moves ~3 MB back into the app budget** and fails the build. Chat's `CodeBlock` (Prism-light) stays as is: it re-renders per streamed token, where a Monaco instance per fenced block would be the wrong tool. Workers are bundled through Vite `?worker` — no CDN, the app must work offline.

### Races and flashes

- **Auto-advance must arm on the unmet→met transition**, not fire because the condition is already true on arrival — otherwise a returning user flashes through the step before they can read it. Pattern: `autoAdvanceArmedRef` in `OnboardingProvider.tsx` — reset to false on step change, set true only when the effect observes *unmet*, fire only when armed **and** met.
- Any **globally-mounted** TanStack Query (outside auth-gated routes) must be `enabled:`-gated on having an access token, or it fires pre-login without a bearer, 401s, and sticks in an error state that never recovers after login.
- **react-joyride v3 in controlled mode never emits `STATUS.FINISHED`** — the final Next emits `STEP_AFTER` + `action=NEXT` at the last index. A handler keyed on `FINISHED` hangs on the last step forever.
- **Every SignalR hub must be listed in `vite.config.ts`'s dev WS-proxy allowlist.** One missing hub falls through to the generic `/api` proxy and wedges Vite's *entire* WebSocket proxy — breaking hubs that *are* correctly listed.
- **Push-only (SignalR) terminal states need an explicit query invalidation in the reconcile handler** — there's no REST `onSuccess` to hang it off. Missed once for GGUF-download completion: the installed-models list only refreshed on manual reload.
- **`InvocationState` is deep-copied by a single `InvocationState.Clone()` method** (on the type itself; both `WorkerEventDispatcher` and `InvocationResumeRegistry` call it — the two former hand-rolled copies were centralized). The *cloned* snapshot — not the live mutated state — is what reaches the chat pump and persistence. **Any new `InvocationState` field must be added to `Clone()`**, or it silently persists as null despite the dispatcher setting it correctly. This class of bug passes unit tests and is only caught by live verification.

---

## 6. Deliberately NOT built

Don't assume these exist; don't "restore" them.

- **Context-window management is a two-layer budgeter, not summarization.** `ConversationContextBudgeter` (`Client.Application/Services/Invocation/Context/`) is turn-grouped and two-pass — it excerpts oversized historical tool results, then drops oldest whole turns, pinning system messages plus the most recent N turns — applied at the single-agent growth points and at the orchestration seed in `InvocationRunner`. Below it, an inner `ProviderCallBudgetChatClient` re-budgets *every* raw provider round, covering the inner tool-loop rounds and MAF participant turns the outer layer doesn't see. The effective llama.cpp window (`-c`) is read back post-readiness and threaded into both budgeters. What's still true: this TRUNCATES/DROPS history (and excerpts tool results) rather than doing LLM summarization/compaction, and there is no cross-turn prompt-prefix preservation strategy.
- **Run state IS reconciled across a restart (no "stuck forever"), but a run is not auto-resumed.** Two startup reconcilers run *before* Kestrel serves (`Program.cs:198-199`): `NodeChatRestartRecoveryService` terminalizes any non-terminal assistant row → `Interrupted` and backfills a durable content-free run envelope; `IScheduledJobRunStore.MarkStaleActiveRunsAsync` moves stale `Queued`/`Running` scheduler runs → `Failed`. The run envelope (`AgentExecutionLogRecordKind.ChatRunEnvelope`) is the already-wired `ScheduledJobRunStore`-style durable ledger. What is deliberately NOT built: **auto-resume / re-dispatch** of an interrupted run (collides with "no mid-stream retry" + the cost/privacy of silently re-billing on every restart) — manual **Regenerate** covers it. `InvocationResumeRegistry` is in-memory (browser-reconnect only; empty after restart *by design*), not cross-restart resume. (Older notes saying "a mid-run restart loses it / stuck forever" are stale.)
- **No mid-stream retry** by design (a pre-first-token retry + per-model circuit breaker do exist).
- **Approval/HITL is live, and a general policy layer now exists (OPP-03).** MCP tools ship `RequiresApproval=true` by **default** (`Services/Mcp/IMcpServerConnectionManager.cs`), so `InvocationToolResolver` wraps each in `ApprovalRequiredAIFunction` and the framework surfaces a `ToolApprovalRequestContent` before it runs — the approval round-trip fires live for any node with an MCP server connected. `run_in_agent_home` is also gated (and ships inactive). **On top of the per-tool flag there is now a node-wide policy engine** (`IToolApprovalPolicy` + `PermissiveToolApprovalPolicy` NoOp floor in AI.Agent; real `NodeToolApprovalPolicy` in Application, reads `StoredNodeSettings.ToolApprovalPolicy` once at composition): tools carry a `ToolCategory` (ReadLocal/WriteExecute/Orchestration/Network/Unknown) declared at each definition site, and the policy maps `category → requiresApproval` (+ per-tool overrides). It is **strictly tighten-only** — the compose is a pure OR (`catalogDefault || nodePolicyByCategory || perAgent==true`), so a policy/per-agent value can only *add* approval, **never waive** it. Unknown category **fails closed** (requires approval). The compose is **not** cosmetic: `InvocationToolResolver` wraps on the resulting flag. Deferred (see [[Stale beliefs corrected]] follow-ups): category-level *loosening* (auto-approving a whole category unattended) is intentionally NOT built — it would require removing the structural floor. See §4 for the enforcement-seam trap. Approval decisions are audited (`AgentExecutionLogRecordKind.ApprovalDecision=2` in `agent_execution_logs`, metadata-only) + a `tool_approval_decisions_total` metric.
- **The scheduler CAN run a saved agent** (`RunSavedAgentHandler`, template id `run-agent`, OPP-02): node-local-only (a cloud/remote *effective* model is rejected up front — unattended work never leaves the box), capacity/GPU-admission-gated with the reservation disposed per fire, approval-required tools stripped (no unattended HITL), single-agent only, output recorded as a content-safe run-history summary. The `ModelRecommendationCheckHandler` is no longer the *only* handler. What is NOT built (follow-ups): writing the agent's answer into a visible chat **conversation** (summary-to-history only), and compiling a bound **orchestration** for a scheduled run (it runs single-agent).
- **Three sandbox providers exist; only two are ever selected by default.** `fake` (in-memory, CI default), the process-based provider, and — since 2026-07-31 — the **Docker container provider for Development Mode** (`DockerSandboxRuntimeProvider`, approved by [ADR 0004](adr/0004-development-mode-container-execution-docker-stopgap.md)). The Docker one is **opt-in**: `Development:Sandbox:Provider=docker` selects it and the shipped config leaves that key unset, so nothing reaches it unless an operator asks. Implemented-vs-not is tracked on [`docs/roadmaps/development-mode-container-status.md`](roadmaps/development-mode-container-status.md) — read that page, don't infer from the tree. Still unbuilt: the **OpenSandbox self-hosted provider** (`Plans/marker-j-open-plan.md`, planned and reviewed; **declined for Slice 3** in 2026-07-29 with recorded flip conditions, retargeted as the leading post-Docker candidate). It goes behind the existing `ISandboxRuntimeProvider` SPI.
- **Playbook/memory retrieval is embedding-ranked by default, lexical fallback** — the registered default `IPlaybookRetrievalRanker` is `EmbeddingPlaybookRetrievalRanker` (node-local cosine), with `LexicalPlaybookRetrievalRanker` (token-overlap) used only when `PlaybookRetrieval:EmbeddingModelName` is unset or embedding fails. Adaptive-memory injection rides the resolved system prompt (config-hash-safe) rather than a live `AIContextProvider` injection path, which would break resume-safety. Extraction-only use of `AIContextProvider` was the deliberate choice.
- **Adaptive memory is per-agent only** — scoped per `AgentDefinition`, no cross-agent or node-wide sharing.
- **No RAG over chat attachments** — v1 is file-tools-only (agent mode) or inline-text injection with a char cap (plain chat). No image/OCR ingestion.
- **No STT** — voice work is output-only (TTS). English and non-English playback use the browser's Web Speech API and the voices exposed by the browser/OS; Kokoro is no longer shipped.
- Desktop-only settings (ThemeConfigurator, Open Canvas editor) are deliberately not part of the mobile-responsive sweep.

---

## Stale beliefs corrected

Old notes (and agents who half-remember them) assert these. They are **false today**.

| Stale belief | Reality |
|---|---|
| "The WSL dev box has no GPU, so GPU work can't be tested here." | It has an **RTX 5090 (32 GB, sm_120) + CUDA 13.3**, live-verified. GPU paths are testable here. |
| "The dev box is an RTX 4080 with 16 GB (sm_89 / Ada); build CUDA with `-DCMAKE_CUDA_ARCHITECTURES=89`." | **Wrong since 2026-07-26** — it is an **RTX 5090, 32 GB, sm_120 (Blackwell)**. Read `nvidia-smi`; never hardcode the arch. The source-build fallback includes `120`, but live detection remains authoritative. |
| "llama.cpp has native MCP support now, so the app could use it." | The MCP client lives in llama-server's **web UI**, which this app never serves; the backend addition is only the experimental `--ui-mcp-proxy` CORS proxy. The .NET `ModelContextProtocol` client remains the correct integration point, and a browser-side host would move tool execution below the approval/sandbox boundary. See `Plans/2026-07-26-llamacpp-native-mcp-decision.md`. |
| "Ollama was removed from the app." | Removed only from *Aspire dev orchestration*. It remains a supported, gated, opt-in secondary provider with 50+ live call sites. llama.cpp is the *default*, not the *only*, runtime. |
| "NVFP4 isn't supported — llama.cpp can't load it, or it can't be used on this box." | **False since `75cc519a`, and live-proven 2026-07-31.** llama.cpp carries `GGML_TYPE_NVFP4` with sm_120 tensor-core kernels, our pin `b10201` includes them, and a 27B NVFP4 GGUF loaded and generated at 95% GPU utilisation here. The *true* limitation is narrower and is about the **container**: NVFP4 **safetensors** (compressed-tensors/ModelOpt) remain unloadable, since the upstream convert script is still an unmerged PR. Don't restate the narrow fact as the broad one — see §3. |
| "The inbound MCP key is stored reversibly on purpose, so Node Settings can re-show it and the operator can re-copy it without rotating." | **True until 2026-08-03, false now.** It is stored as a one-way SHA-256 digest. The plaintext is returned exactly once, by the generate endpoint, and is unrecoverable afterwards — `GET` has no key field at all. A lost key means regenerate + reconfigure every client. The old rationale is gone from the code, wiki and runbook; if you find prose still arguing for reversibility, it is a leftover, not a decision. |
| "`dotnet test` reports zero tests — run the native test-host exe instead." | Fixed by the `global.json` MTP runner pin. `dotnet test` works against the whole solution. Native exe still works, but is no longer required. |
| "Any build catches a bare `TODO`, a banned API, or a style violation — so a green `dotnet build` means the analyzer wall passed." | **False since 2026-07-31.** `Directory.Build.targets` disables analyzer *execution* in local Debug builds (84 s → 10 s on the Tests module). The wall is Release-only. **Finish with `dotnet build … --configuration Release`** or you will hand off code the packaging script rejects. `XE_FULL_ANALYSIS=1` forces the full pass in Debug. |
| "`RunAnalyzers=false` also turns off source generators, so gating analyzers would break TUnit discovery." | **False.** It maps to csc `-skipanalyzers`, which skips diagnostic analyzers only; generators still run. Verified — a Debug build of `XE-Local-AI-Engine.AI.Agent.Tests` still lists 209 tests. |
| "A green E2E run proves the frontend typechecks, because the fixture builds it." | **False since 2026-07-31.** `XEReactClientFixture` runs `build:e2e` (bare `vite build`), not `build`; esbuild strips types without checking them. `pnpm run lint` is the only frontend typecheck. |
| "Browser E2E runs strictly sequentially — `BrowserParallelLimit.Limit == 1` — because the node allows one refresh token per user." | **False since 2026-08-15.** The revoke is per-user, so the fix was per-test users, not sequencing. The suite is two disjoint `[ParallelGroup]` phases: `BrowserSerial` (limit 1) and `BrowserPooled` (limit 4, one leased user per test). `BrowserParallelLimit` still exists but now caps only the serial group. |
| "`--treenode-filter` alternation `(A\|B)` silently matches zero tests." | **False** on TUnit 1.58 — it returns the union (9 + 6 = 15, re-verified 2026-07-24). `AGENTS.md` now carries the correct measurement; believe it. |
| "Capability detection cannot see harmony/gpt-oss reasoning, and adding markers for it would break the reasoning-off path — so it must stay undetected." | **Overtaken 2026-07-31** by F-014 (`a3496f60`). The fix landed in the form the old note itself recommended: a **distinct** capability rather than a widened graded marker list. `GgufCapabilityDetector` reports `native_reasoning` off `NativeReasoningTemplateMarkers` (`<\|channel\|>analysis`, `reasoning_effort`), computed **mutually exclusive** with graded thinking so a harmony model never enters the `if (SupportsThinking)` branch. The hazard the old note described is real and is what that exclusion prevents — keep it. |
| "Development Mode's container provider is unbuilt / 'in progress', so Development Mode necessarily runs on the process provider." | **Shipped 2026-07-31, opt-in.** `DockerSandboxRuntimeProvider` (`Name = "docker"`) is DI-registered and selected by `Development:Sandbox:Provider=docker`. It is *not* the default — the shipped `appsettings.json` sets no `Development:Sandbox` key, so selection falls back to the AgentHome provider (`process`). Both "it hasn't shipped" and "Docker is now required" are wrong. [`docs/roadmaps/development-mode-container-status.md`](roadmaps/development-mode-container-status.md) is the canonical status page. |
| "Development Mode fails closed when the host cannot provide isolation, because `BuildLaunchPolicy` rejects unserveable requests." | **False — it DEGRADES.** The gate can only reject what is asked for, and `DevelopmentWorkspaceProvider` asks for nothing the process provider cannot serve anywhere: `NetworkPolicy.Unrestricted` (a recorded deferral — `dotnet restore` needs egress), **no** `ResourceLimits`, and the read-only `.git/config` mount only from a provider advertising `SupportsReadOnlyMounts`. So the fail-closed path never fires. On Linux the process provider does enforce real containment where the probes succeed; on **Windows** the record is `SandboxContainment.None` and repo code runs as the signed-in user with full network and no ceiling. Never state this posture without naming the platform. |
| "`build-and-test.yml` and `e2e.yml` are PR gates that block merges." | **False.** `build-and-test.yml` gates `develop`, not the RC branch; `e2e.yml` runs only on manual dispatch or a `run-e2e`-labelled PR and is deliberately non-blocking. (As of 2026-07-24, both were also registered `disabled_manually` — that specific state was not reverified since; don't restate it as current without checking.) |
| "`release.yml` passes `--pre` to `vpk pack`, breaking prerelease CI." | Already fixed in the workflow — `--pre` is only on the `upload github` step. |
| "Docker is gone from this repo entirely, so a `Docker.DotNet` reference or a daemon requirement is a regression to revert." | **Narrowed since 2026-07-29** by [ADR 0004](adr/0004-development-mode-container-execution-docker-stopgap.md): Docker is permitted for **Development Mode build/test/lint execution only**, as a stopgap ahead of MXC, and the daemon is a hard requirement there with no unisolated fallback. The rest still holds — no Docker on the inference path, in the model runtime, or in HostAgent, and no global provider switch (AgentHome and Coder stay on `ProcessSandboxRuntimeProvider` under plan D2). Read the ADR before reverting anything Docker-shaped. |
| "`ProcessSandboxRuntimeProvider` has no network isolation at all — the child always shares the host network." | **Narrowed 2026-07-29.** Where `unshare` is available, AgentHome's child now runs in an **empty network namespace** (egress denied outright) — and Coder with it, since it attaches to that same sandbox rather than creating one. But the flat opposite is just as wrong: containment is measured per mechanism into `SandboxContainment`, so off Linux or on a host that fails the probe there is still no isolation, the provider does **not** advertise the capability, and approval-gating is the interim control. **Development Mode is deliberately still `Unrestricted`** (its restore needs the network). Never state this provider's egress posture without saying *which mechanism, which host, which feature*. |
| "A hardened container must run **non-root** — set `--user 1000:1000` and read it back with `inspect`." | **False on a rootless daemon, in both halves.** Rootless maps container uid 0 to the invoking user and uid `N>0` to `subuid_base + N - 1`, so uid 1000 is host 100999 and cannot write the engine-generated workspace mount at all (measured, Engine 29.6.1). The rule is *"the identity that maps to the engine's own host uid, never to host uid 0"* — uid 0 under rootless, the engine's effective uid under rootful. And `inspect` only echoes the uid **asked for**, never what it maps to, so every read-back passes on a container that cannot write a byte. `DockerSandboxRuntimeProvider` verifies with a create-time probe file stat'd host-side instead. |
| "The TOCTOU/no-follow sandbox guards live in `LocalContainerSandboxProvider` (Docker)." | They live in **`ProcessSandboxRuntimeProvider`** (`ProviderName="process"`). Re-check which provider a feature actually resolves (`SandboxProviderSelector.ResolveAgent` / `ResolveDevelopment` — selection is per feature since 2026-07-30) before citing a provider name. `LocalContainerSandboxProvider` is the **deleted** pre-re-architecture class and is not the Dev Mode container provider ADR 0004 approves — the guards are not moving back into it. |
| "Plain `git apply` rejects a `--binary`-diffed patch." | **False** on modern git (2.43+) — it applies fine. Any security control depending on that rejection is unsound. |
| "The advisor runs an approved `llmfit` utility container over gRPC/HostAgent." | That path was built and then **fully replaced** by the in-process `MemoryFitEstimator` + live HF discovery. No Docker/HostAgent in the recommendation path; the orphaned `approved_utility_images` store was deleted and the table **dropped via migration** (BE-04). ADR 0004's Development-Mode permission does **not** reopen this — the advisor stays estimator-only and never spawns a process. |
| "Recommendation ranks by `OrderByDescending(EstimatedBytes)`." | Superseded by **capability-bucketed** ranking (`EstimatedBytes / 1 GiB` bucket → downloads → date → trust). |
| "`ModelKind` is Unknown/Chat/Embedding." | A fourth kind, **Reranker**, exists — and the reranker name-check must run *before* the embedding check. |
| CUDA toolkit pinned at "13.1"; TUnit at "1.56.x". | Point-in-time snapshots — now 13.3 and 1.58.x. Don't build tooling against a remembered minor version. |
| "There's no context-window management — the full conversation replays verbatim every turn." | A two-layer budgeter exists: `ConversationContextBudgeter` (turn-level) plus `ProviderCallBudgetChatClient` (per-provider-round). It truncates/drops, not summarizes. |
| "Playbook/memory retrieval is lexical, token-overlap ranking by design." | The default `IPlaybookRetrievalRanker` is `EmbeddingPlaybookRetrievalRanker`; `LexicalPlaybookRetrievalRanker` is only the fallback. |
| "The node DB is SQLCipher / whole-file encrypted, so a wrong operator secret fails DB-open at startup." | **False** — it's plain SQLite (`bundle_e_sqlite3`, no `PRAGMA key`) with **per-column AES-256-GCM** AEAD (see [wiki 08](wiki/08-data-and-persistence.md)). A wrong secret does **not** fail startup. Consequently the DataProtection key-ring's fail-closed guarantee rests on `NodeDataProtectionKeyRingFailClosedKeyResolver` (BE-02) — which hard-fails on an *undecryptable encrypted* key instead of silently regenerating and orphaning cloud/worker tokens — **not** on DB-gating. |
| "A skill name only needs to avoid a leading/trailing dash." | **False.** The Agent Skills specification also forbids **consecutive** hyphens, and `Microsoft.Agents.AI`'s `AgentSkillFrontmatter.ValidateName` enforces that — our own regex didn't, so `foo--bar` validated, persisted and encrypted cleanly, then **threw `ArgumentException`** out of `AgentInlineSkill`'s constructor at agent-construction time, in both `InvocationAgentFactory` and `SubAgentSpawnService`. Validation now delegates to `AgentSkillFrontmatter` directly (one authority, can't drift again), and `AgentDefinitionResolver.ProjectSkill` drops any legacy row MAF would still reject, fail-soft and logged by id only. See [wiki 04 §4.2](wiki/04-agent-mode.md). |
| "Agent Skills are instructions-only — there's no resource/attachment concept." | **False since `59e0a87d`.** An `agent_skill_resources` table holds the `references/`/`assets/` files a real skill's `SKILL.md` body links to (level-3 progressive disclosure, fetched by the model via `read_skill_resource`). Content is encrypted with the AAD bound to **both** `skill_id` and the resource name — not just the row id — because the threat is a database writer re-parenting a resource onto a different skill, not a reader. `scripts/` remains deliberately unsupported: detected at import, listed as refused, never persisted, never executed. See [wiki 04 §4.1/§4.5](wiki/04-agent-mode.md). |
| "Assigning a skill to an agent just works — load it and go." | **False on two counts.** (1) Since the `Microsoft.Agents.AI` 1.15.0 pin, `load_skill`/`read_skill_resource`/`run_skill_script` are **approval-required by default** — a regression relative to the 1.8.0 baseline this feature was first verified against, since neither construction call site set `AgentSkillsProviderOptions`. (2) Skills on a **spawned sub-agent** could never load at all: `SubAgentSpawnService.CurateChildTools` strips every approval-required tool from a child (it has no human to ask), but the skills provider rides `AIContextProviders` and bypassed that curation — so its all-gated-by-default tools reached a child with no way to answer them. Fixed by waiving `load_skill`/`read_skill_resource` approval for children only (the parent's approval of the spawn is the consent); `run_skill_script` stays gated unconditionally, everywhere. See [wiki 04 §4.3/§4.4](wiki/04-agent-mode.md). |
