# Proposed entries

Pending rules awaiting operator approval. Not required reading: nothing here is an active rule yet.
Add an entry in the normal format (`### <heading>`, then `**Rule:** … **Prevents:** … **Authority:** …`).
Once the operator approves it, move the entry to its topic file and delete it here; a rejected entry is deleted.

### MXC ProcessContainer: host prep, one command line, a verbatim environment

**Rule:** treat an MXC `Probe` warning naming `wxc-host-prep` as "unavailable", never as a warning: the one-time `prepare-system-drive` and the per-boot `prepare-null-device` are admin steps the product never runs, and without them children fail to start. `ContainerRequest.Command` is one command LINE, so quote argv with `WindowsCommandLine.Join` (CommandLineToArgvW rules), never `string.Join(" ")`. A non-null `Environment` is used verbatim: pass `SystemRoot`, `windir`, `ComSpec`, `PATHEXT`, `PATH`, a jail `TEMP` and a jail `LOCALAPPDATA` (the SDK refuses an environment without it; first `windows-sandbox` CI run, 2026-10-08), or the launch fails. **Prevents:** a host advertising a boundary whose every launch dies, argv split differently inside the container, and a child that cannot find `cmd.exe`. **Authority:** ADR 0019; `HostSandboxContainmentProbe.MeasureWindows`, `WindowsCommandLine`, `SandboxLauncher.BuildAppContainerEnvironment`.

### Cloud-model tool gates live only in the offer (target: agents-and-sandbox.md)

**Rule:** a tool's cloud-model gate is the `LocalToolOfferProvider` offer branch (plus the `SubAgentSpawnService` seam guard for spawn); no executor (`WebFetchService`, `WebSearchService`, `CustomToolCatalog`, `McpToolRegistry`, `InvocationToolResolver`) re-checks locality. A new tool class that must stay off cloud models needs its own offer branch keyed on `IModelTrustResolver` (Unresolved counts as cloud) and, if it is opened by a Privacy switch, the sync getter read on the same path. Unattended callers must pass the real cloud flag into the offer, never a hard-coded `false`. **Prevents:** a tool reaching cloud models ungated, as MCP tools did before `AllowCloudModelMcpTools`, and a cloud unattended run getting the local-data offer. **Authority:** `LocalToolOfferProviderTests`, `SubAgentSpawnServiceTests`, `GraphWorkflowAgentExecutorTests`.

### A lab or scratch models dir holds hard links or copies, never symlinks (target: dev-runtime.md)

**Rule:** when seeding a throwaway models directory (`scripts/lab-up.sh` profile `modelsDir`, any `HuggingFace__ModelsDirectory` a live round points at), hard-link or copy the GGUF, its `.xe-model.json` sidecar and any projector from the user-level store: `ln <src> <dir>/`, never `ln -s`. Every installed-model member is refused when `FileInfo.LinkTarget` is set or the file is a reparse point, so a symlinked model is skipped at startup (`invalid acquisition sidecar` in the host log), `GET models` says `isAvailable=false`, and the lab stops at the model phase (exit 2, no manifest). **Prevents:** a round spending its first hour on a "model not installed" that is only a link. **Authority:** `InstalledGgufSnapshotStore` / `InstalledGgufDeletionStore` member checks, `ValidatedGgufImportSource.OpenNoFollow`; wiki 13 "Lab bootstrap for live rounds".

### A skipped `git read-tree HEAD` leaves the shared index stale, not dirty (target: wiki 13 "Committing in a shared worktree")

**Rule:** the last line of the private-index recipe, `git read-tree HEAD`, is what resyncs the shared index; a commit that skips it leaves the shared index at the previous tree, so the next committer's `git diff --cached --quiet` guard fires although nobody staged anything. Before reporting "staged changes", run `git diff --cached --quiet <previous HEAD>`: exit 0 means the index is only stale, and `git read-tree HEAD` clears it. **Prevents:** a worker halting a round on a phantom staged change left by the previous committer. **Authority:** wiki 13 "Committing in a shared worktree"; sandbox-security-profile round, 2026-10-08.

### TUnit `[Arguments]` cannot read a `private const` of the test class (target: backend-tests.md)

**Rule:** a constant referenced from `[Arguments(...)]` is re-emitted by TUnit's source generator in a generated class, so a `private const` on the test class fails the Release build with CS0122 inside the generated source while the IDE shows the test file clean. Declare such constants `internal const`. **Prevents:** a red Release build whose error points at a generated file nobody wrote. **Authority:** `SandboxSecurityProfilePolicyTests` capability constants; sandbox-security-profile round, 2026-10-08.

### A new `//` line under a 2-line comment makes a 3-line run and fails the comment ratchet (target: build-and-analyzers.md)

**Rule:** `CommentBudgetConventionTests` counts consecutive own-line `//` lines as one run and fails any run over 2 lines in a file whose allowlist line does not already carry that count. Adding one `//` line directly below an existing two-line comment is the usual way to create one: merge it into the existing lines or move it next to the code it explains, and run the `CommentBudgetConventionTests` class before hand-off whenever a change adds comments. **Prevents:** a red Architecture lane (and fail-fast gate stop) from a one-line comment edit. **Authority:** `CommentBudgetConventionTests` run shape (`RunLineBudget`); wiki 16 "Comment budget".

### `NodeSettingsEndpointTests.Persisted(mutate)` replays the mutation on a FRESH record (target: backend-tests.md)

**Rule:** `Persisted(mutate)` applies the save endpoint's mutation to a fresh `StoredNodeSettings`, and the mutation returns `latest` unchanged when the stored record differs from the one it validated. A round-trip test seeded with a non-default record therefore passes its endpoint half and fails its persisted half with the field at its default. Start such a test from `NewSettingsStore()`, or pass the same seeded record to `Persisted(mutate, stored)`. **Prevents:** chasing a "setting not saved" failure that is only the fixture's fresh record. **Authority:** `NodeSettingsEndpointTests.Persisted`, `NewSettingsStore`.

### A sync `INodeRuntimeSettings` getter is a Release error at every async call site (target: build-and-analyzers.md, extend the MA0042/MA0045 entry)

**Rule:** `INodeRuntimeSettings` exposes sync and async twins (`GetDefaultModelName()` / `GetDefaultModelNameAsync()`); CA1849, MA0042 and S6966 reject the sync twin inside any async method in Release, so a new setting whose consumers are all async gets only `Get<Name>Async` and no sync twin. Add the sync getter only for a caller that has no async context (a sync offer branch, a constructor), and never "for convenience". **Prevents:** a sync getter added for one test or caller that reds every async consumer in the Release build. **Authority:** `INodeRuntimeSettings`; `.editorconfig` Meziantou block; `SandboxSecurityProfilePolicy` consumers (async only).

### A constructor that newly requires a service breaks hand-built test containers the focused lanes never run (target: backend-tests.md)

**Rule:** some tests build a real `ServiceCollection` and resolve the type under test through it (`McpServerConnectionManagerDiTests`, `DevelopmentValidationReviewAndApplyTests`); a constructor that gains a required dependency compiles, passes the class's own tests (they use the builder or a fake) and fails only those DI tests at resolve time. When a constructor gains a parameter, grep the test tree for `AddSingleton<I...>` registrations of the type and run those classes, or run the full gate before hand-off; the touched-classes filter does not catch it. **Prevents:** a green focused lane followed by a red full gate on a `ServiceProvider` resolve. **Authority:** `McpServerConnectionManagerDiTests`, `DevelopmentValidationReviewAndApplyTests`; sandbox-security-profile round, 2026-10-09.
