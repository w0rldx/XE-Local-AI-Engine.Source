# Build and analyzers

Scope: the Release-only analyzer wall, `.editorconfig` and MSBuild levers, NuGet audit, architecture-guard authoring,
DI registration order, and poisoned NuGet paths; `AGENTS.md` ("Validation") is authoritative for the commands and
these entries are the traps behind them. Read when: changing `.editorconfig`, `Directory.Build.*`,
`Directory.Packages.props`, an analyzer suppression, an `Architecture/` test or a DI module, or when Release reds
where Debug was green.

## Release-only analyzer wall

### Always finish with a Release build — a green Debug build is not verification

**Rule:** Debug skips analyzers unless `CI` or `XE_FULL_ANALYSIS` is set; the gate lives in `Directory.Build.targets`, never `.props` (`Configuration` is not defaulted there); check it with `-getProperty:RunAnalyzersDuringBuild -p:Configuration=Debug`. `RunAnalyzersDuringBuild=false` never skips source generators, so a zero-test Debug run points at build, config or discovery, not this gate. `dotnet run --no-build` defaults to Debug: pass `--configuration Release`. A deliberate break leaves every symbol used (move a call, change an argument, invert a condition), never deletes the only call. **Prevents:** a Debug-green branch failing at the gate, and a break-proof whose errored build ran the previous binary. **Authority:** `Directory.Build.targets`. [evidence](../agent-knowledge-evidence.md#always-finish-with-a-release-build--a-green-debug-build-is-not-verification)

### Four analyzer rules are silent in Debug and `error` in Release

**Rule:** `S3267` (a `foreach` that could be a `Where`), `MA0022`/`S4586` (returning a bare `null` `Task`) and `S1117` (a local shadowing a field) reject shapes a reviewer would accept: fix the shape or suppress in source with a reason. A local shadowing a primary-constructor parameter fails Release as well (`S1117` + `MA0084`): rename the local, never the parameter. **Prevents:** a branch that built clean all day in Debug failing at the gate. **Authority:** `.editorconfig` (Sonar and Meziantou rules left at their default warning), promoted to errors by `TreatWarningsAsErrors` in `Directory.Build.props`.

### A `[GeneratedRegex]` without `matchTimeoutMilliseconds` fails the build — in **Release** only

**Rule:** every `[GeneratedRegex]` names a `matchTimeoutMilliseconds` (copy `DeploymentPlanner.SubstitutionTokenRegex`). **Prevents:** Meziantou MA0009, an error in Release and silent in Debug, reaching the gate unseen. **Authority:** `ContainerImageReference.BareImageIdRegex`.

### `ArgumentNullException.ThrowIfNull` on a FIELD fails this repo's Release build (MA0015)

**Rule:** MA0015 (an error here) rejects `ArgumentNullException.ThrowIfNull(x)` when `x` is a field, so it cannot make a field count as read. In a break-proof, read the re-added field in the method body instead; a field that is assigned and never read trips the unused-member analyzer. **Prevents:** a break-proof whose Release build reports `1 Error(s)` and so runs the stale binary. **Authority:** `.editorconfig` (MA0015 in the enforced Meziantou set).

### `.Result` false-positives are why MA0042/MA0045 replace a `BannedSymbols` line

**Rule:** sync-over-async is enforced by Meziantou MA0042/MA0045, which are type-aware, never by a `BannedSymbols.txt` text ban on `.Result`, `.Wait(` or `GetAwaiter().GetResult()`: many DTOs have a `Result` property and zero-timeout `Wait(0)` admission polls are legitimate. The analyzers also catch sync I/O with an async twin inside an async method. Take site counts from a Release build, not a grep census. **Prevents:** a text ban redding every `Result`-named property. **Authority:** `.editorconfig` Meziantou block; `BannedSymbols.txt` header. [evidence](../agent-knowledge-evidence.md#result-false-positives-are-why-ma0042ma0045-replace-a-bannedsymbols-line)

### `TimeProvider` is registered once, in `Client/ConfigureServices.cs`

**Rule:** the clock enters DI once, `TryAddSingleton(TimeProvider.System)` in `XE-Local-AI-Engine.Client/ConfigureServices.cs`. Consumers take `TimeProvider` as a required constructor parameter and call `GetUtcNow()`; delete any `?? TimeProvider.System` default, `sp.GetService<TimeProvider>()` in a factory, or second registration (search `DependencyInjection/Modules/*.cs` too). A test needing a frozen clock uses `ConfigureAdditionalTestServices` (`RemoveAll<TimeProvider>()`, then add the fake); the shared fixture keeps the real clock because JWT lifetime validation ignores the DI clock. **Prevents:** a class silently reading the real clock under a test's fake. **Authority:** `ConfigureServices.cs`; `BannedSymbols.txt` (`DateTimeOffset.UtcNow`, RS0030).

### a Windows-only NuGet needs no Windows TFM — reference the LEAF package and guard with attributes

**Rule:** before adding a Windows TFM or `EnableWindowsTargeting`, check the package: a leaf such as `NAudio.Wasapi` ships plain `lib/netX.0` with an assembly-level `[SupportedOSPlatform("windows")]`; its meta-package multi-targets Windows TFMs. Satisfy CA1416 with a type-level `[SupportedOSPlatform]` plus an `OperatingSystem.IsWindows()` branch at the DI call site; a versioned annotation needs `IsWindowsVersionAtLeast(…)`. A Windows-gated test class needs `[SupportedOSPlatform("windows10.0.19041.0")]` besides `[RunOn(OS.Windows)]`. Never `#pragma warning disable CA1416`. **Prevents:** a repo-first Windows TFM, and a suppressed check letting a Windows-only call reach Linux. **Authority:** `Directory.Packages.props` (`NAudio.Wasapi`); `WindowsProcessAudioCaptureSource`, `ProcessAudioCaptureSupport`; `WindowsProcessLoopbackTests`.

## Editorconfig and MSBuild levers

### `dotnet_naming_rule.*.severity` is IDE-only — the build-time lever is the single `IDE1006` diagnostic

**Rule:** a `severity` inside a `dotnet_naming_rule` block is ignored at build time; the only build lever is `dotnet_diagnostic.IDE1006.severity`, all-or-nothing across every naming rule. It is deliberately unset, so naming is advisory; nearly all current violations are one style question (PascalCase local constants against the declared camelCase), which must be settled before promoting it. **Prevents:** "promoting" one naming rule and believing Release enforces it. **Authority:** `.editorconfig` (no `IDE1006` line); Microsoft Learn naming-rules page. [evidence](../agent-knowledge-evidence.md#dotnet_naming_ruleseverity-is-ide-only--the-build-time-lever-is-the-single-ide1006-diagnostic)

### `global.json` pins an SDK feature band, and CI resolves from the same file

**Rule:** the pin and CI resolution are described in `docs/wiki/02-project-layout.md` ("Build & package conventions"); install a `10.0.4xx` SDK (`dotnet --list-sdks`). **Prevents:** misreading an unmatched band, whose tell is every `dotnet` command failing with "A compatible .NET SDK was not found", naming the requested version and the `global.json`. **Authority:** `global.json`.

### `RunAnalyzersDuringBuild=false` keeps IDE squiggles; `RunAnalyzers=false` kills them too

**Rule:** for any Debug/Release analyzer split use `RunAnalyzersDuringBuild`, never `RunAnalyzers=false`: the master switch also clears `RunAnalyzersDuringLiveAnalysis`, so the IDE stops showing diagnostics while you edit. `tools/AgentTemplateGenerator` uses `RunAnalyzers=false` on purpose (standalone, no analyzer wall). `-getProperty:RunAnalyzersDuringLiveAnalysis` printing an empty line is correct: the default is applied downstream. **Prevents:** buying a fast Debug build by blinding the editor. **Authority:** the analyzer `PropertyGroup` comment in `Directory.Build.targets`; `tools/AgentTemplateGenerator/Directory.Build.props`.

### NuGet audit is stated explicitly, and a new advisory is meant to break the build

**Rule:** `Directory.Build.props` states `NuGetAudit`, `NuGetAuditMode=all` and `NuGetAuditLevel=low` (the SDK defaults, written for legibility). Under `TreatWarningsAsErrors` a newly disclosed advisory of any severity reds restore or build with no source change; that is by design. Bump the package or add a reviewed override for that one advisory; never lower the level or narrow the mode to `direct`. **Prevents:** silencing supply-chain advisories to get a green build. **Authority:** `Directory.Build.props` (NuGet audit block).

### Measuring an analyzer rule: drop warnings-as-errors for that build, and flip the rule under `[*.cs]`

**Rule:** to count a rule's violations, set it to `warning` inside the `[*.cs]` section (the file ends in narrow globs, the last an endpoint-DTO glob, so an appended line scopes to those alone), build Release once with `-p:TreatWarningsAsErrors=false`, and confirm every project printed a `-> *.dll` line. Install the flip behind a `trap … EXIT` restore. **Prevents:** a partial count (under warnings-as-errors the first failing project stops its dependents) or a zero from a mis-scoped flip, promoting a rule on false evidence. **Authority:** `.editorconfig` section order. [evidence](../agent-knowledge-evidence.md#measuring-an-analyzer-rule-drop-warnings-as-errors-for-that-build-and-flip-the-rule-under-cs)

### a bulk `.editorconfig` severity silences an analyzer without stopping it running

**Rule:** a section's bulk `dotnet_analyzer_diagnostic.severity = none` suppresses a rule's reporting there, but the analyzer still executes over those files; a per-ID `dotnet_diagnostic.<ID>.severity = none` in the same section (or removing the files from `<Compile>`) stops execution, because Roslyn's per-tree skip reads the per-ID key. Sonar rules opt out of Roslyn's generated-code filter, so `[**/Migrations/*.cs]` does not take migrations off the bill. Measure with `-p:ReportAnalyzer=true -v:d --no-dependencies` on the one suspect project, before and after. **Prevents:** disabling a security rule solution-wide to buy back time that scoping it to the generated section buys for free. **Authority:** `.editorconfig` `[**/Migrations/*.cs]` section. [evidence](../agent-knowledge-evidence.md#a-bulk-editorconfig-severity-silences-an-analyzer-without-stopping-it-running)

### Code behind `#if P0_SPIKE` escapes the analyzer wall, and the constant REPLACES the defaults

**Rule:** `scripts/lint-release-scripts.sh` build-checks `P0_SPIKE` code; keep that gate. `-p:DefineConstants=P0_SPIKE` replaces defaults such as `TRACE`: read the existing value, append `P0_SPIKE` with semicolons encoded as `%3B` (a command-line `$(DefineConstants)` is not expanded), verify with `-getProperty:DefineConstants`, and rebuild ungated afterwards. Never redirect `BaseIntermediateOutputPath` globally (it propagates through references and duplicates assembly attributes). Under `set -o pipefail`, `strings | grep -q` can exit 141. **Prevents:** rotting spike code, and gated assemblies left in `bin/` for a later `--no-build` run. **Authority:** `scripts/lint-release-scripts.sh` (`--no-spike`, `--spike-only`).

## Build and NuGet state

### After ANY failed build, rebuild to green before trusting a `--no-build` gate

**Rule:** after any failed build, rebuild to `0 Error(s)` before a `--no-build` run: a project that fails leaves its output directory untouched, including its copies of dependencies that did compile, so the test host loads a pre-change product assembly. The same holds for a Debug build with `XE_FULL_ANALYSIS=1`: an analyzer failure writes no new assembly, so a focused test-host run executes the previous one. **Prevents:** grading old code as new. `scripts/assembly-guard.sh` cannot see it: it compares output before and after its own run, not output already stale at the start. **Authority:** `scripts/assembly-guard.sh`; reproduced with a two-project solution.

### an analyzer error in a file the change never touched: re-run after a build-server shutdown before believing it

**Rule:** a Release analyzer error in a file your change did not touch is not a finding until it reproduces: keep the failing log, run `dotnet build-server shutdown`, and build the same tree again; never edit the flagged file on one red. A break-proof round whose build reported errors ran the previous binary, so discard and re-run it either way. **Prevents:** "fixing" correct code for a one-off red, and reporting a break-proof whose build never produced the binary under test. **Authority:** AGENTS.md "Validation" (break-proof rule). [evidence](../agent-knowledge-evidence.md#an-analyzer-error-in-a-file-the-change-never-touched-re-run-after-a-build-server-shutdown-before-believing-it)

### A full test-suite run can poison its own worktree's generated NuGet props

**Rule:** a fixture that restores under an isolated `HOME` can rewrite `obj/*.nuget.g.props` with a since-deleted package root, and the next Release build fails `CS0006` on every analyzer assembly. Cure it with `dotnet restore` with `NUGET_PACKAGES` pinned to the real store; a build-server shutdown alone does not help. **Prevents:** treating it as the node-reuse trap below, which has the same face. **Authority:** the package root recorded in `obj/*.nuget.g.props`.

### A Dev-mode sandbox run leaves MSBuild worker nodes holding a dead `NUGET_PACKAGES`

**Rule:** Development Mode gives each sandboxed task its own `NUGET_PACKAGES`; a reusable worker (`MSBuild.dll /nodemode:1`) can outlive the task with that path and write it into `obj/*.dgspec.json` on a later restore anywhere on the host (NU5037 or CS0006 naming a `/tmp/xe-…/nuget` directory). Recover with `dotnet build-server shutdown`, then `MSBUILDDISABLENODEREUSE=1 NUGET_PACKAGES=$HOME/.nuget/packages dotnet restore --force`; unlike the props trap above, a live worker re-poisons a plain re-restore. Prefix long-lived agent shells with the same two variables. **Prevents:** chasing a package path nothing asked for. **Authority:** `DevelopmentWorkspaceTools.BuildEnvironment` (sets `MSBUILDDISABLENODEREUSE=1`), asserted by `DevelopmentMountBrokerTests`. [evidence](../agent-knowledge-evidence.md#a-dev-mode-sandbox-run-leaves-msbuild-worker-nodes-holding-a-dead-nuget_packages)

## Architecture guards and refactors

### Measure a non-vacuity floor through the guard itself, never with a grep over source

**Rule:** get a scan guard's minimum-scanned-count floor by temporarily raising it to an absurd value, building Release and reading the real count from the failure message. A grep over source counts a different population (nested and local declarations) and lands above the assembly count. **Prevents:** a floor set from grep that reds the guard on a clean tree. **Authority:** `LayerDependencyTests.AssertTypesScanned`. [evidence](../agent-knowledge-evidence.md#measure-a-non-vacuity-floor-through-the-guard-itself-never-with-a-grep-over-source)

### A ratchet allowlist is keyed by a composite fully-qualified string, and the forbidden-type walk recurses

**Rule:** an allowlist key must identify the exact violation, not just its owner: a bare type key hides a second violation on a listed type, and a simple name can collide across areas. The forbidden-type walk recurses into generic arguments, array elements and `Nullable<T>` (`IOptions<CodexOptions>` depends on `CodexOptions`). Read the key shape and recursion out of the test before editing any allowlist; never assume them from a plan. **Prevents:** an allowed outer generic silently exempting the forbidden type it wraps. **Authority:** `Architecture/Support/HostDependencyRule`; the allowlists in `ThirdPartySdkBoundaryTests` and `ProviderTelemetryWrapGuardTests`.

### A reflection walk over an endpoint's dependencies sees `ILogger<TSelf>` as a self-reference

**Rule:** a recursive walk over constructor parameters must skip `ILogger<T>`'s type argument when it is the type's own (a logging category, not a dependency), while `ILogger<AnotherEndpoint>` stays a violation. **Prevents:** every endpoint that logs landing on an allowlist and freezing a false rule. **Authority:** `Architecture/Support/HostDependencyRule` (used by `EndpointDependencyTests`). [evidence](../agent-knowledge-evidence.md#a-reflection-walk-over-an-endpoints-dependencies-sees-iloggertself-as-a-self-reference)

### FastEndpoints verb names collide with `IResponseCookies.Delete`, so a route scan must match unqualified calls only

**Rule:** a route-literal scan matches `Get|Post|Put|Delete|Patch|Routes(` only when unqualified: use a negative lookbehind for an identifier character or `.`, not `\b`, which matches after a dot and so reads `response.Cookies.Delete(…)` in `NodeAuthCookie` as a verb call. Safe because `.editorconfig` sets `dotnet_style_qualification_for_method = false:error`. **Prevents:** a guard red on correct cookie code, "fixed" by an allowlist entry that also excuses real violations. **Authority:** `EndpointConventionTests`.

### Three Release-only compile traps that bite a newly written architecture test

**Rule:** new `XE-Local-AI-Engine.Tests/Architecture/` tests hit Release-only errors: `IDE0042` (deconstruct a named tuple: `var (violations, count) = Scan();`), `S3878` (no collection expression into `params`: `LoadAssemblies(a, b)`), and `ArchUnitNET.Loader.Type` colliding with `System.Type` (alias it: `using ArchLoader = ArchUnitNET.Loader.ArchLoader;`, as `PlacementConventionTests` does for `Assembly`). **Prevents:** Debug-green iterations handing the Release gate a red. **Authority:** `EndpointConventionTests` (the alias).

### The family-file exemption bought room for data, and logic classes accreted in it until the purity rule fenced them

**Rule:** a family file (`*Dtos.cs`, `*Contracts.cs`, `*ServiceModels.cs`, `*Models.cs`) may declare records, data-shaped classes and structs, enums, interfaces, delegates and framework-plumbing overrides, but no non-private, non-override method. `FilePlacementConventionTests.FamilyFiles_DeclareOnlyDataShapedTypes` has NO allowlist (a hit is always a move) and fails below its `FamilyFileFloor`. **Prevents:** logic classes accreting in contract files unseen, as `BenchmarkJudgeScoreCalculator`, `BenchmarkJudgePolicyValidator`, the pairwise payload builders and `CustomToolConfigParser` had. **Authority:** project-cleanup A2; wiki 16 "One top-level type per file".

### Moving a hosted service into an Application DI module changes its START ORDER, and one module is feature-gated

**Rule:** `IHostedService`s start in registration order and the `Client.Application` modules run at the top of `ConfigureServices`, so moving `AddHostedService<T>()` into one starts it earlier: check the service for an ordering dependency, and the destination for an early return (`AddNodeScheduler` registers nothing unless `SchedulerOptions.Enabled`). `FirstRunModelProvisioningService` takes its launch decision as `NodeLaunchContext`, so the type lives in `Client.Application`; only its registration stays at the bottom of the host's `ConfigureServices`, and moves under the same rule. **Prevents:** a "behaviour-neutral" move reordering startup or dropping a service on a feature-off node; no test asserts either. **Authority:** `NodeSchedulerServiceCollectionExtensions.AddNodeScheduler`; `AddNode*Extensions`; `ConfigureServices.cs` (`AddHostedService<FirstRunModelProvisioningService>`).

### a constructor-caller census by `grep "new X("` misses target-typed `new(...)` and NUL-bearing files

**Rule:** when a constructor signature changes, take the caller list from the Release build, not from grep. If you grep first, use `grep -a` (test files with a literal NUL in a string literal read as binary) and remember that target-typed `new(...)` and `=> new(...)` never name the class. **Prevents:** a "no other callers" claim the first Release build disproves. **Authority:** the compiler. [evidence](../agent-knowledge-evidence.md#a-constructor-caller-census-by-grep-new-x-misses-target-typed-new-and-nul-bearing-files)

### Commit before you trap-guard a mutation on a file you are still editing

**Rule:** `trap 'git checkout -- <file>' EXIT` restores the file to HEAD, not to your working tree: commit real edits to a file before installing a deliberate-break mutation on it. **Prevents:** the safety net discarding the slice's own uncommitted work along with the mutation. **Authority:** `git checkout -- <path>` semantics. [evidence](../agent-knowledge-evidence.md#commit-before-you-trap-guard-a-mutation-on-a-file-you-are-still-editing)

## Covered elsewhere

- A bare `TODO` in a C# comment fails the build — in **Release** — `AGENTS.md` ("Conventions that bite"); `docs/wiki/16-code-conventions.md` ("Comments and XML documentation")
- Layering is mechanically frozen — `docs/wiki/16-code-conventions.md` ("Subfolders under `V1/` must nest their namespace — IDE0130 is a build error", "Providers depend only on `Providers.Abstractions`")

## Stale beliefs

Superseded claims; the entries above are the active rules.

| Stale belief | Current correction |
|---|---|
| Any build runs analyzers. | Analyzer wall is Release-only locally (§1). |
| `RunAnalyzers=false` disables generators, and is the Debug gate. | The gate is `RunAnalyzersDuringBuild=false` (since 2026-09-16). It skips diagnostic analyzers only: source generators still run, and IDE live analysis stays on (§1). |
| CUDA/TUnit remembered minor versions are pins. | They are volatile; query tools and `Directory.Packages.props`. |
