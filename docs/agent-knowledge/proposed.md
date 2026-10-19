# Proposed entries

Pending rules awaiting operator approval. Not required reading: nothing here is an active rule yet.
Add an entry in the normal format (`### <heading>`, then `**Rule:** … **Prevents:** … **Authority:** …`).
Once the operator approves it, move the entry to its topic file and delete it here; a rejected entry is deleted.

### A new project's publish profile is ignored until `.gitignore` un-ignores its folder

**Rule:** after adding `Properties/PublishProfiles/*.pubxml` to a new project, add
`!/<project>/Properties/PublishProfiles/*.pubxml` beside the existing un-ignore lines in `.gitignore` and confirm with
`git ls-files <project>/Properties` before any packaging step or test names the profile. **Prevents:** a profile that
exists in the worktree, passes `RuntimeLicensePackagingTests` and local publishes, but is in no commit, so CI and a
clean checkout publish without the RID, self-contained and single-file settings (the `xe` client, 2026-10-17; caught by
review, not by a gate). **Authority:** the `*.pubxml` rule and its per-project exceptions in `.gitignore`.

### A `Directory.Packages.props` addition also runs `pnpm run licenses:check`

**Rule:** after adding or bumping any `<PackageVersion>`, run `( cd XE-Local-AI-Engine.Client.React && pnpm run
licenses:generate )` and commit `src/features/about/data/third-party-licenses.generated.json` in the same commit;
`pnpm run acceptance` does not include `licenses:check`, and the routing table sends a `Directory.Packages.props`
change to build-and-analyzers, where the license rule (frontend-and-api, "`openapi:check` and `licenses:check` diff
the working tree against the INDEX") is never read. **Prevents:** a backend-only package addition that passes the
backend gate and the frontend acceptance locally and turns the CI `licenses:check` step red (the `xe` client's two
packages, 2026-10-17; caught by review). **Authority:** `licenses:check` in `XE-Local-AI-Engine.Client.React/package.json`;
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
