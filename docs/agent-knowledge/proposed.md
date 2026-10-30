# Proposed entries

Pending rules awaiting operator approval. Not required reading: nothing here is an active rule yet.
Add an entry in the normal format (`### <heading>`, then `**Rule:** … **Prevents:** … **Authority:** …`).
Once the operator approves it, move the entry to its topic file and delete it here; a rejected entry is deleted.

### Development behind the chain: the package folder keeps its host path, build servers are off

**Rule:** coder and validation sandboxes run in the bubblewrap chain where advertised, the trusted workspace as the jail and engine mounts as binds after `/work`. Bind the package folder at its HOST path: the warm restore runs outside the chain (it needs egress) and writes that path into `obj/`. Set `UseSharedCompilation=false` behind the chain: a compiler server in a fresh namespace holds stdout and the command hangs to its timeout. A `NuGet.Config` naming a host-only local source fails restore there (`NU1301`). **Prevents:** a `--no-restore` build missing the warmed packages, validation timing out after a green build. **Authority:** `DevelopmentWorkspaceProvider.BuildMounts`, `DevelopmentWorkspaceTools.BuildEnvironment`, `DevelopmentMountBrokerTests`.

### A new project's publish profile is ignored until `.gitignore` un-ignores its folder

**Rule:** after adding `Properties/PublishProfiles/*.pubxml` to a new project, add
`!/<project>/Properties/PublishProfiles/*.pubxml` beside the existing un-ignore lines in `.gitignore` and confirm with
`git ls-files <project>/Properties` before any packaging step or test names the profile. **Prevents:** a profile that
exists in the worktree, passes `RuntimeLicensePackagingTests` and local publishes, but is in no commit, so CI and a
clean checkout publish without the RID, self-contained and single-file settings (the `xe` client, 2026-10-17; caught by
review, not by a gate). **Authority:** the `*.pubxml` rule and its per-project exceptions in `.gitignore`.

### A `Directory.Packages.props` or `nuget-license-overrides.json` change also runs `pnpm run licenses:check`

**Rule:** after adding or bumping any `<PackageVersion>`, or re-keying an entry in
`scripts/compliance/nuget-license-overrides.json` (an override IS the license the generator writes), run `( cd XE-Local-AI-Engine.Client.React && pnpm run
licenses:generate )` and commit `src/features/about/data/third-party-licenses.generated.json` in the same commit;
`pnpm run acceptance` does not include `licenses:check`, and the routing table sends a `Directory.Packages.props`
change to build-and-analyzers, where the license rule (frontend-and-api, "`openapi:check` and `licenses:check` diff
the working tree against the INDEX") is never read. **Prevents:** a backend-only package addition that passes the
backend gate and the frontend acceptance locally and turns the CI `licenses:check` step red (the `xe` client's two
packages, 2026-10-17; caught by review; the Mxc.Sdk 1.0.1 override re-key, 2026-10-21, turned develop red). **Authority:** `licenses:check` in `XE-Local-AI-Engine.Client.React/package.json`;
`.github/workflows/build-and-test.yml`.

### A test console built with `AnsiConsole.Create` opts out of Spectre's profile enrichers

**Rule:** every fake or capturing console a test builds through `AnsiConsole.Create(new AnsiConsoleSettings { … })`
sets `Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }` beside its `Interactive` and `Ansi`
settings; to reproduce a CI-only CLI test failure, run the Cli test host with `GITHUB_ACTIONS=true` (`CI=true`
alone does not trigger it). **Prevents:** Spectre's built-in GitHub enricher forcing `Interactive=false` and
`Ansi=true` after the explicit settings, so every prompt refuses to read keys and a scripted turn gated on that read
never ends (the `siblings` CI leg hung to its 45-minute timeout on the first run after the terminal CLI merge,
2026-10-17; a renderer snapshot saw dim escape codes). The local gate never sets `GITHUB_ACTIONS`, so the hang is
invisible locally. **Authority:** `GatedConsole` in `XE-Local-AI-Engine.Cli.Tests/Commands/TurnRunnerTests.cs` and
`Capture` in `XE-Local-AI-Engine.Cli.Tests/Commands/TestCli.cs` (7762bb3b5); Spectre.Console `ProfileEnrichment`.

### `python-validation.sh --scope changed` is blind to workflow, `publish/` and package-pin edits

**Rule:** `scripts/python-validation.sh --scope changed` maps only Python sources, the catalog inputs and the
Python tooling files to a run; an edit under `.github/workflows/`, `publish/`, `scripts/release/` or a version pin
that the release contracts mirror (`Directory.Packages.props`, `eng/ReleaseVersion.props`) prints "No Python
validation needed" and proves nothing. After such an edit run `scripts/run-release-contract-tests.sh` directly: it is
what the CI `release-contracts` job executes. **Prevents:** a green local Python gate followed by a red
`release-contracts` leg (the vpk pin lockstep test and the Aspire version-tuple test, 2026-10-18). **Authority:** the
`case` arms in `scope_changed` of `scripts/python-validation.sh`; the `release-contracts` job in
`.github/workflows/build-and-test.yml`.

### A dependency round ends with a dispatched `dev-build`

**Rule:** after a NuGet or packaging dependency round lands on `develop`, dispatch `dev-build`
(`gh workflow run dev-build.yml --ref develop`) and wait for its `package / build-pack` jobs on linux-x64 and win-x64
before calling the round done; `build-and-test` never packs. **Prevents:** a green `build-and-test` hiding a package
job that fails on the backend license corpus, the bundle-input evidence merge across the three single-file
publishes, or the `vpk` pin (both package jobs red after the Velopack 1.2.161 round, 2026-10-18, fixed in
97c771b97). **Authority:** `.github/workflows/dev-build.yml` and `package-velopack.yml`; the promoted
`UPSTREAM_PACKAGE_LICENSE_TEXTS` rule in ci-and-release.

### A `systemone` probability or confidence is a routing signal, not a calibrated probability

**Rule:** treat `output.probabilities`, `output.confidence`, `output.noul` and `output.score` from a `systemone`
`DecisionModel` as model-reported and uncalibrated; pick any `Gte`/`Lt` threshold on them from a measurement on your
own inputs, label the numbers so in the UI, and never present them as "the model is 99 % sure". **Prevents:** a graph
that routes, abstains or escalates on a threshold that only looked principled; upstream scales the numbers by
temperatures stored in the model file and states they are not guaranteed calibrated (llama.cpp #29818, README).
**Authority:** wiki 21 §4.9; `SystemOneDecisionProvider`.

### A decision model's whole prompt must fit one physical batch

**Rule:** the `Decision` role launches with `-b`/`-ub` equal to its context (`LlamaServerLaunchPolicyOptions.DecisionContextTokens`,
capped to the train context); never drop the pooled batch pair from that branch, and size a decision node's bound input
against `-ub`, not against the model's advertised context. **Prevents:** every decision prompt longer than llama.cpp's
default 512-token `n_ubatch` being refused (upstream README: "the whole prompt is evaluated in one batch: it must fit in
`--ubatch-size`"), which reads as a broken model rather than a launch flag. b11669 refuses an over-long prompt with HTTP
500 "input (N tokens) is too large to process" (live, 2026-10-19), not 400. **Authority:** `LlamaServerLaunchArgumentComposer`
(Decision branch), `LlamaServerLaunchProjection.From`; `SupervisorSpawnArgsTests.EnsureRunning_PooledRole_PinsPhysicalBatchToTheContext`.

### `decision.type` names a model family, not a question type

**Rule:** read `<arch>.decision.type` (`laya`, `kev`, `lev`, `openjev`) only as "this GGUF is a native decision model"
and which upstream family it is; the question type (`choice`, `noul`, `score`) is the node's `questionType`, chosen per
request. Julia-1 carries `laya` although it is not Laya. **Prevents:** gating question types on the header value,
mapping a family to a single question type, or classifying Julia-1 as "unknown family" and refusing it.
**Authority:** `GgufHeaderMetadata.DecisionType`, `LocalGgufModelKindClassifier.Classify`; the Hugging Face GGUF
summaries of `ggml-org/Julia-1-GGUF` and `ggml-org/Laya-GGUF` (both `laya`, read 2026-10-19).

### After a llama.cpp pin bump the grammar smoke's negative control must be a bound the new build still rejects

**Rule:** after bumping `LlamaCppReleasePins.PinnedTag`, run `scripts/run-tool-grammar-smoke-local.sh` against the new
binary AND the previous one; when the negative control comes back 200 on a non-reasoning model, re-measure the
per-keyword ceiling on the new build before touching `MaxGrammarRepetitionBound`, and move the control to a bound the
new build still rejects. A silently dropped upper bound makes the run inert, not green. **Prevents:** reading an
upstream behaviour change as a raised limit (b11669 degrades an upper bound above 2000 to unbounded, upstream #26613,
so the raw production offer stopped failing while `minLength` 2001 still fails; 2026-10-19). **Authority:**
`LlamaGrammarLiveSmokeTests`; wiki reference 03, "The grammar repetition bound".

### A second resident decision model evicts the embedder or the reranker

**Rule:** at the default `MaxLoadedCompanionProcesses` of 3 only one decision model is resident beside the embedder and
the reranker; a graph that uses two decision models makes each load evict the least-recently-used idle companion. Until
decision models get a cap of their own (proposed follow-up: a separate decision slot count, so companions evict only
within their role), use one decision model per node, or raise the cap. **Prevents:** knowledge search going cold or
thrashing (the V-01 shape) because a graph alternates between two decision models. **Authority:**
`LlamaServerIdleReaper.AdmitAndAllocatePortAsync`, `LlamaServerSupervisorOptions.MaxLoadedCompanionProcesses`; wiki 03,
"`ModelRole` and the external-endpoint option".
### A trace of a turn driven by `gpu-smoke-driver.py chat` mostly measures the driver

**Rule:** when the question is the node's own CPU during a chat turn, drive the turn from a WebSocket or keep-alive
client (the SPA) or subtract the transport frames; `scripts/gpu-smoke-driver.py chat` long-polls the hub with a new
TLS connection per poll, about 130 requests per turn. **Prevents:** reading Kestrel TLS handshakes,
`NodeSecurityStampCheck.IsCurrentAsync` and the middleware pipeline, 60–70 % of the node's sampled CPU in that setup,
as product cost (profiling round, 2026-10-19). **Authority:** `command_chat` in `scripts/gpu-smoke-driver.py`;
`NodeSecurityStampCheck.IsCurrentAsync`.

### A fresh conversation's first output includes llama-server's prompt-cache save

**Rule:** before blaming the harness for a first-output regression with unchanged prompt tokens, look for "prompt cache
update took N ms" in the Debug-forwarded llama-server log and subtract it. With a nonzero `--cache-ram`, llama-server
saves the previous slot state synchronously before prefill: 0.5–2.1 s for 15–20k-token states on the 27B. The time is
outside `/metrics` prompt seconds and inside the harness `FirstOutputMs`. **Prevents:** chasing a node regression that
is the host cache save (2026-10-19). **Authority:** the `--cache-ram` entry in
`docs/wiki/reference/03-llama-server-launch-flags.md`; `LlamaServerSupervisorOptions.ChatCacheRamMiB`.

### Restart-gated node settings are read once; an eject does not apply them

**Rule:** after changing `llamaChatCacheRamMiB` or any field in `restartGatedNodeSettingsFields` (including
`llamaMaxLoadedProcesses`), restart the node and confirm the live process argument (for example `--cache-ram` in
`pgrep -af llama-server`) before measuring. **Prevents:** a PUT plus eject that respawns llama-server with the old value,
because `AddNodeModelRuntimeExtensions.BuildSeededLlamaServerSupervisorOptions` reads the setting once into the
singleton `LlamaServerSupervisorOptions`; the settings UI warns, the API response does not (2026-10-19, a void lab
arm). **Authority:** `AddNodeModelRuntimeExtensions.BuildSeededLlamaServerSupervisorOptions`;
`restartGatedNodeSettingsFields` in `NodeSettingsFieldsModel.ts`.

### llama-server's host prompt cache never restores a tool-using turn

**Rule:** do not count a "found better prompt" line as a conversation restore without checking which conversation owns
the entry. A follow-up to a tool-using turn replays tool results as 2000-char excerpts while the saved state holds the
full results, so the keep ratio stays at or below 0.209, under llama-server's 0.25 gate. **Prevents:** reading a
cross-conversation partial hit, which identical first prompts across lab conversations produce, as the cache working
(2026-10-19). **Authority:** `ConversationContextBuilder.ProjectToolExchanges` and
`ConversationContextBudgetOptions.DefaultHistoricalToolResultExcerptChars`.

### A host prompt-cache entry is about twice the logged state size on hybrid models

**Rule:** size a `--cache-ram` budget from the "cache state" or "exceeds cache size limit" lines of the llama-server
log, not from the "total state size" of the save line. **Prevents:** a budget that holds half the entries expected,
because on hybrid models each entry also carries context checkpoints (a 668 MiB logged state was a 1266.8 MiB entry on
the 27B, 2026-10-19). **Authority:** `LlamaServerSupervisorOptions.ChatCacheRamMiB`; the `--cache-ram` entry in
`docs/wiki/reference/03-llama-server-launch-flags.md`.

### Tool timing is stripped by `BenchmarkOutputParts.ForJudge` before any judge payload is built

**Rule:** a benchmark output-part field that differs between two runs of the same answer (a duration, a timestamp) is
cleared in `BenchmarkOutputParts.ForJudge`, every judge-facing projection of the parts goes through it, and the field
is `[JsonIgnore(WhenWritingNull)]` so a cleared copy serializes as it did before the field existed. **Prevents:** a
judge prompt that changes on every run of an identical answer, so judging is not reproducible and a re-judge disagrees
with its own first pass. **Authority:** `BenchmarkOutputParts.ForJudge`; `BenchmarkOutputPartsTests`.

### Tool execution time is measured in the function invoker, never from the lifecycle gap

**Rule:** time a tool only in the `FunctionInvoker` set in `AgentServiceCollectionExtensions.DecorateChatClientPipeline`,
record it in `ToolExecutionTimingScope`, and read it where the completed lifecycle event is built. Never subtract the
`Requested` and `Completed` events of `InvocationRunner.StreamLoop`, and never read `ToolInvocationObservabilityChatClient`'s
`tool.duration_ms` as execution time. **Prevents:** a tool time that includes the model's streaming tail and every sibling
call of the round, so a fast tool reports seconds. **Authority:** `ToolExecutionTimingScope`; `ToolExecutionTimingTests`.

### Never build a symlink-chain fixture at the kernel's 40-link limit

**Rule:** keep a symlink-chain fixture at or below 20 links. Linux follows at most 40, and its lock-free path walk
restarts and re-counts the links already followed whenever anything on the box mounts, so a chain longer than 20 is not
reliably resolvable while other tests launch sandboxes. **Prevents:** a fixture that resolves alone and fails `stat`
with ELOOP inside a gate, as `SandboxedMcpStdioTransport.MaxLinkHops` did at 40 in the 2026-10-22 gate reds.
**Authority:** `SandboxedMcpStdioTransport.MaxLinkHops`; `SandboxedMcpStdioTransportTests`.

### A Quartz 4 scheduler a test starts must be shut down, or the test host never exits

**Rule:** a test that builds a provider with `AddNodeScheduler` registers `SchedulerShutdownGuard.AddTo` and disposes
it, so every scheduler the provider started is shut down even when the test fails before its own `Shutdown`.
Production keeps `store.UseBackgroundThreads = true` in `ConfigureStore`. **Prevents:** a Quartz 4 ADO store's misfire
handler, a foreground thread by default (3.x never was), keeping the test host alive after one failed scheduler test,
so the whole run hangs instead of reporting the red (Quartz 4.6.2 round, 2026-10-30). **Authority:**
`SchedulerShutdownGuard`; `NodeSchedulerServiceCollectionExtensions.AddNodeScheduler`.

### Backend and contract gates run on the plain login `PATH`, never with toolchain shims first

**Rule:** run `scripts/run-backend-tests.sh` and the contract lane from a shell whose `PATH` does not start with the
`mise` shims directory. Only a live dev-host shell puts the shims first, and one that also sets a scratch
`XDG_DATA_HOME` exports `MISE_DATA_DIR` back to the user-level data directory, because the scratch value moves
`mise`'s own data directory and its installed tools with it. **Prevents:** `dotnet` and `python3` resolving through a
shim the dev-workflow sandbox cannot reach, which fails the sandbox tests with `runner_not_installed`, and the contract
lane failing on an untrusted `mise.toml` (dependency majors round, 2026-10-30). **Authority:** `mise.toml`;
`DevelopmentTestResultAdapter.RunnerNotInstalled`.

### pnpm 12 writes `minimumReleaseAgeExclude` entries on its own; revert them

**Rule:** after a `pnpm add` or `pnpm update` that requests a version younger than `minimumReleaseAge` (7 days), diff
`XE-Local-AI-Engine.Client.React/pnpm-workspace.yaml` and revert any `minimumReleaseAgeExclude` entry you did not mean
to add; every entry carries a comment saying why it is exempt. Wait out the age instead, or pick the newest version
that is old enough. **Prevents:** a lockfile that silently bypasses the maturity hold for whatever the update touched
(pnpm 12.16.1, 2026-10-30). **Authority:** `minimumReleaseAge` and `minimumReleaseAgeExclude` in
`XE-Local-AI-Engine.Client.React/pnpm-workspace.yaml`.

### Ruff's `py314` target rewrites multi-type `except`; `scripts/**` keep `py313`

**Rule:** keep `per-file-target-version = { "scripts/**" = "py313" }` in the root `pyproject.toml` `[tool.ruff]` table
while any script runs under the host's `python3` rather than a uv-managed 3.14. **Prevents:** the formatter rewriting
`except (A, B):` to the PEP 758 parenthesis-free form, which is a `SyntaxError` on a 3.13 host (`dev-start.sh`,
`release.yml`) (Python 3.14 round, 2026-10-30). **Authority:** the root `pyproject.toml` `[tool.ruff]`.
