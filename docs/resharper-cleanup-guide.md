# Running ReSharper code cleanup safely

We run JetBrains ReSharper command-line cleanup (`jb cleanupcode`) to format and tidy the codebase.
Use **this** command — not a bare `jb cleanupcode XE-Local-AI-Engine.slnx` — because the default
"Full Cleanup" profile applies two transforms that can silently break the build:

```bash
dotnet tool restore   # once, provisions the pinned jb / dotnet-ef tools
dotnet jb cleanupcode XE-Local-AI-Engine.slnx \
  --profile="BuildSafe" \
  --exclude="**/*.props;**/*.targets;**/*.csproj"
```

## Why these flags

### `--profile="BuildSafe"` — no member reordering

The team-shared `XE-Local-AI-Engine.slnx.DotSettings` defines a **BuildSafe** cleanup profile that
reformats + tidies (whitespace, usings, qualifiers, built-in type references) but **does not reorder
type members**.

ReSharper's member-layout reordering ("Reorder type members") sorts declarations by kind/visibility.
That moves a `static` field above another `static` member it reads — and C# runs static initializers
in **textual order**, so the field then initializes from a not-yet-initialized (null) sibling. This
hit `CodexModelCatalog` (a derived `HashSet` was moved above the array it is built from), producing a
`CS8604` build error and an `ArgumentNullException` at type-init. The compiler caught that one; a
similar reorder on a non-nullable/value type would be a **silent runtime bug**, so reordering is
disabled wholesale.

To add more cleanup tasks (e.g. named-argument insertion) later: open Rider → Settings → Editor →
Code Cleanup, duplicate this profile, enable the extra tasks, leave **Reorder type members OFF**, and
save to *Solution team-shared* settings so it lands back in `XE-Local-AI-Engine.slnx.DotSettings`.

### `--exclude="**/*.props;**/*.targets;**/*.csproj"` — leave build files alone

The cleanup's XML formatter pretty-prints long MSBuild element content onto its own line, e.g.:

```xml
<IsTestOrToolingProject Condition="…long…">
  true
</IsTestOrToolingProject>
```

MSBuild **preserves that surrounding whitespace**, so the property value becomes `"\n  true\n"` and a
condition like `'$(IsTestOrToolingProject)' != 'true'` silently evaluates true even for the projects it
was meant to exclude — which is how the analyzer guardrails leaked into test projects. We harden the
one current consumer with `.Trim()`, but the simplest guarantee is to keep cleanup away from build
files entirely. (Generated migrations etc. are already excluded by their own `.editorconfig`.)

## After running

Build with warnings-as-errors and run the suites before committing:

```bash
# --configuration Release is MANDATORY here, not a preference.
dotnet build XE-Local-AI-Engine.slnx --configuration Release   # must be 0 errors / 0 warnings
dotnet test  XE-Local-AI-Engine.AI.Agent.Tests/XE-Local-AI-Engine.AI.Agent.Tests.csproj
# …and the other test projects, one at a time — never a build and a test run concurrently
#   (see AGENTS.md: use scripts/with-build-lock.sh + scripts/assembly-guard.sh; exit 75 = void, re-run).
```

> **`HandoffWorkflowSpikeTests` is not part of that run, despite what older copies of this note said.**
> It is wholly inside an `#if P0_SPIKE` block and `P0_SPIKE` is defined in no `.csproj`, `.props` or
> `.targets` in the repo — so it never compiles into an ordinary build and has never run as a test. The
> only thing that touches it is `scripts/lint-release-scripts.sh`, which *build-only* compile-checks it
> behind a temporary `DefineConstants` and restores an ungated build afterwards. Do not cite it as a
> reason to serialize test projects.

> **Why Release is load-bearing for *this* gate specifically.** `Directory.Build.targets` disables analyzer
> execution in local Debug builds, and the rules it gates off are the `IDExxxx` code-style family — exactly
> what a `cleanupcode` pass rewrites (usings, type qualifiers, built-in type references). A Debug build is
> therefore **guaranteed** to under-report what the cleanup just introduced. `XE_FULL_ANALYSIS=1` is the
> alternative if you must stay in Debug.
