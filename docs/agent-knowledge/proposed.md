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

### A host that dies at startup may be a stale key ring, not the change (target: dev-runtime.md)

**Rule:** when a dev host or opt-in runner dies at startup, read the node log for "encrypted key-ring element could not be decrypted with the current node operator secret" before suspecting the change. A checkout's `XE-Local-AI-Engine.Client/dp-keys` ring can be encrypted under an older operator secret than the current `XE-Local-AI-Engine.AppHost/.data/node.key`, and then every start stops in `NodeKeyCheckStartupService`. Run node-based lanes from a fresh worktree, or park the stale ring with `XE-Local-AI-Engine.AppHost/.data/node-sqlite` and `XE-Local-AI-Engine.Client/node-settings.json` (move them, never delete without the operator); `node.key` stays. **Prevents:** chasing a runner's bare "app resource failed to start" (exit 5) as a product regression. **Authority:** `NodeKeyCheckStartupService`, `NodeDataProtectionKeyRingDecryptor`; `scripts/run-gpu-smoke-local.sh` exit codes.

### An E2E run rewrites the checkout's node settings (target: backend-tests.md)

**Rule:** prepare a model-matrix or lab node only AFTER any Playwright E2E run in the same checkout, or run the two in different worktrees. The E2E suite's in-process host uses the checkout's Client project directory as content root, which `NodeDataDirectory` treats as the node root, so it rewrites `XE-Local-AI-Engine.Client/node-settings.json`: an `offline` external-access profile prepared for the matrix was gone after an E2E run and `ExternalAccessProfileBackfillService` stamped `recommended`. The mechanism is unconfirmed; the effect is confirmed. **Prevents:** `scripts/run-model-matrix-local.sh` exiting 2 on its prerequisite "the node's external-access profile must be `offline` … it is 'recommended'" after an unrelated E2E run. **Authority:** `NodeDataDirectory`, `ExternalAccessProfileBackfillService`, the matrix runner's prerequisite check.

### Skipping the build lock on a long lane voids its timings (target: build-and-analyzers.md)

**Rule:** `NO_BUILD_LOCK=1` on a long opt-in lane such as the model matrix is acceptable only when the lane builds nothing shared: it builds its own worktree once up front, and the assembly guard stays armed and reports the build output unchanged at the end, or the result is void. Sibling sessions keep building their own worktrees meanwhile, so timing numbers (time to first token, tokens per second) measured under that load are not comparable with an idle-machine run; say so in the evidence. **Prevents:** a run whose binaries changed underneath it being reported as green, and loaded-machine timings read as a regression or a win. **Authority:** `scripts/with-build-lock.sh`, `scripts/assembly-guard.sh`, `scripts/run-model-matrix-local.sh` header.

### A stale credential helper hangs the GitHub CLI (target: ci-and-release.md)

**Rule:** run `gh` alone and under `timeout`, never inside a chained evidence command. The GitHub CLI can hang indefinitely on a stale credential helper, and a chain containing it then never returns. A hang is never a pass. **Prevents:** a stuck `gh` call read as a hung gate, or a chain that never finished reported as evidence. **Authority:** operator experience in two rounds, 2026-10-07 and 2026-10-09.
