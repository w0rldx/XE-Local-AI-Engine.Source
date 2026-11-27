# Dev hosts and lab rounds

Scope: dev host lifecycle (`dev-start.sh`, `dev-stop.sh`), lab and scratch hosts, live-round data directories and model
dirs, GPU probing. Read when: starting, stopping or scripting a dev host, lab or live round. Host pipeline, auth and
HTTP clients: [dev-runtime](dev-runtime.md).

## Dev host lifecycle

### `lab-up.sh` refuses to start without the GPU runtime unless `--cpu` is explicit

**Rule:** a GPU lab round exports `XE_LLAMACPP_SERVER_PATH` (the BYO llama-server) and `XE_LLAMACPP_VARIANT` before `scripts/lab-up.sh`; the script reads both from its environment (`HOST_ENV_KEYS`), reuses a running host only when it started with the same values, and exits 2 with a one-line reason when the path is unset and `--cpu` was not passed. `--cpu` accepts whatever managed runtime the node selects for the host (the CUDA prebuilt on a Linux x64 NVIDIA host, Vulkan or CPU elsewhere); timing evidence from such a lab names which runtime served. **Prevents:** a live round whose latency figures come from a large model running on the CPU for minutes. **Authority:** `scripts/lab-up.sh` (`HOST_ENV_KEYS`, `--cpu`); `scripts/tests/lab-up.test.sh`; `AGENTS.md` ("Local runtime").

### Probe the GPU; never assume one

**Rule:** never infer hardware or toolkit from docs; run `nvidia-smi`, `nvcc --version` and check the compiled
architecture. Under WDDM/WSL, VRAM pressure pages to host RAM instead of OOMing (slow but correct: useless for OOM tests or
consumer benchmarks); `nvidia-smi memory.free` (global) and llama.cpp `--list-devices` (process budget) can diverge by tens
of GiB: use global for admission/invalidation, the process budget for allocation, never reunify; `--query-compute-apps` may
list nothing while VRAM is held. **Prevents:** stale hardware claims and mis-sized admission. **Authority:** live probes.
[evidence](../agent-knowledge-evidence.md#wsl2-hardware-and-vram-readers)

### Headless WSL2/Linux dev boxes have no keyring

**Rule:** with no Secret Service daemon, Azure.Identity can wrap the MSAL cache persistence failure as
`AuthenticationFailedException`: walk `InnerException`, do not catch only `CredentialUnavailableException`. Such sessions
are memory-only and do not survive restart. **Prevents:** a cache failure surfacing as a sign-in error. **Authority:**
`EntraCachePersistenceFailure`, `EntraPersistenceFallbackCredential` and their tests.

### `dev-stop.sh` is the sanctioned stop path — but the leak it guards against no longer reproduces

**Rule:** do not remove `dev-stop.sh`'s owned-graph fallback cleanup (bound to
the exact AppHost/DCP graph, start-time checked against PID reuse, fail-closed on malformed Aspire output) and never restore
a global `llama-server` kill: plain `aspire stop` cleaned later retests, but the original orphan trigger is still unknown.
**Prevents:** removing live cleanup before a reproducer exists. **Authority:** `scripts/dev-stop.sh`,
`scripts/README-dev-stop.md`. [evidence](../agent-knowledge-evidence.md#aspire-teardown-retest)

### `dev-stop.sh` resolves the AppHost of your CURRENT DIRECTORY, and `pgrep -af` matches its own shell

**Rule:** `scripts/dev-stop.sh` acts on the worktree of the shell's cwd: `cd` there first; "no running instance" for a host
you know is up is a cwd check, not a stop. `pgrep -af llama-server` matches its own command line; use `pgrep -x llama-server`
or `ps -o pid,args -C llama-server`. **Prevents:** believing a host stopped while it holds the GPU and data dir; chasing a
phantom llama-server. **Authority:** `scripts/dev-stop.sh`.

### The node operator secret is seeded by dev-start.sh, not by any tracked file

**Rule:** seeding is in wiki 11 §1. An `AuthenticationTagMismatchException` usually means a
key/data mismatch (user-secret vs `.data/node.key`), not corruption: retry with `XE_NODE_OPERATOR_SECRET_FILE=<key>` before
deleting `.data/node-sqlite/`, `dp-keys/` or credential files. Log only the `dev-status.sh` allowlist (`aspire ps`/`describe`
expose tokens). **Prevents:** destroyed data and leaked tokens. **Authority:** `scripts/dev-start.sh`,
`dev_ensure_node_operator_secret`.

### A dev run provisions models through a worktree-private `HuggingFace__ModelsDirectory`

**Rule:** GGUF import is `IDesktopOnlyEndpoint`; never switch to `XE_LAUNCH_MODE=desktop`, which abandons the isolated DB.
Aspire dev mode skips `DesktopBootstrap`, so set `HuggingFace__ModelsDirectory` on the `dev-start.sh` invocation (any other
start falls back to `AppContext.BaseDirectory/models`): symlinks plus an `index.json` keyed `{fileName}:{quant}` with
`GgufRegistryRevision.ComputeV1`. Never mutate models from two hosts sharing one store at the same time: `index.json` is an
unlocked read-modify-write. **Prevents:** lost registry writes and a round on the wrong DB. **Authority:**
`scripts/dev-start.sh`, `AddNodeModelRuntimeExtensions` (models-directory default), `GgufRegistryRevision`.

### A feature surface answers 404 unless its flag is set at `dev-start` time

**Rule:** feature flags reach the Client only as inherited environment of the `dev-start.sh` invocation
(`WorkSessions__Enabled=true scripts/dev-start.sh`); `AppHost.cs` forwards none, and they are read
once at startup, so a change needs a restart. Graph Workflows default on; External Apps' code default is `false` (only `appsettings.json` enables it).
Each gate is middleware ahead of `LocalApiSecurityMiddleware`, so a disabled feature and a mistyped path both answer 404.
**Prevents:** a live round burned on a "wrong route". **Authority:** `FeatureSwitchMiddleware`.

### A live round in a worktree starts from a FRESH isolated DB, and a scratch host never touches the user data dir

**Rule:** never copy the OPERATOR's DB or keys into a worktree (it answers `auth/setup` with
`AlreadyInitialized`): use a fresh DB, `POST auth/setup` a throwaway admin and re-login (tokens expire ~15 min); persist
`defaultModelName` and `maxMessageRequestTimeoutSeconds=1800`. One sanctioned reuse is a snapshot that
`scripts/lab-up.sh --snapshot` made itself (throwaway admin, lab-owned `node.key` and `dp-keys`), restored only when its
migration-set key matches. A throwaway host sets `XDG_DATA_HOME` to a scratch dir that MIRRORS `~/.local/share` (symlink
each child but `XE-Local-AI-Engine`; an empty one makes Aspire exit 7), or a GPU round sources the BYO llama-server
override. `agent_execution_logs` rows terminalize asynchronously (re-read).
**Prevents:** voided rounds and a rewritten user-level `installed-runtime.json`. **Authority:** `scripts/lab-up.sh`.

### an `XDG_DATA_HOME` override for a scratch host also blinds a per-user tool-version manager

**Rule:** with a scratch `XDG_DATA_HOME`, export the tool-version manager's data-dir variable at its real location (for
mise, `MISE_DATA_DIR=$HOME/.local/share/mise`) on EVERY command of the round: start, status, `aspire logs`, stop.
Otherwise the `aspire`/`python3` shims fail; `dev-start.sh` then prints the probe's error and this remedy. The backend gate
puts the `mise bin-paths` directories before the shims itself, because the Development sandbox cannot reach a shimmed
`dotnet` (`runner_not_installed`); a direct `dotnet test` outside the gate needs the same. **Prevents:** a toolchain
failure read as a stale-AppHost or sandbox bug. **Authority:** `dev_require_runnable_aspire` in
`scripts/dev-aspire-common.sh`; the mise block in `scripts/run-backend-tests.sh`.

### two hosts on one box must never share a llama-server BINARY PATH, or one host's reaper kills the other's models

**Rule:** a live round beside another running host still gets its own copy of the MANAGED llama-server build (behind `XE_LLAMACPP_SERVER_PATH`); never point two hosts at another host's `source-build/active/build/bin/llama-server`, because the startup `StaleProcessReaper` still claims every llama-server under its managed root by path. A BYO binary outside the root is safe to share: on Linux it is reaped only through this node's spawn receipts (`runtime/llama-server/<pid>.json`), resolved by pid (/proc exe realpath + starttime), not by process name. **Prevents:** a resident model vanishing mid-round. **Authority:** `StaleProcessReaper`, `ProcessSpawnReceiptStore`, `LlamaServerProcessSupervisor.SpawnCoreAsync`.
[evidence](../agent-knowledge-evidence.md#two-hosts-on-one-box-must-never-share-a-llama-server-binary-path-or-one-hosts-reaper-kills-the-others-models)

### A backend gate in a lab worktree rewrites that host's `node-settings.json`

**Rule:** the dev host keeps node settings in `XE-Local-AI-Engine.Client/node-settings.json` (gitignored) under the content root, and the Integration test hosts started by `scripts/run-backend-tests.sh` share that root and rewrite the file with defaults. Never queue the gate in a worktree whose lab host you still need; after an unavoidable gate, re-apply the lab's settings with `PUT node-settings` and restart the host (`ToolApprovalCoordinator` reads `maxPendingToolCallAgeMinutes` once per process). Check `GET node-settings` after every `scripts/lab-up.sh`. **Prevents:** a lab host that silently comes back with default settings (custom tools gone from the offer, pending-card cap back at 10 minutes) and void evidence, with nothing in the node log saying so. **Authority:** `ToolApprovalCoordinator`; `scripts/run-backend-tests.sh`.

### A lab or scratch models dir holds hard links or copies, never symlinks

**Rule:** when seeding a throwaway models directory (`scripts/lab-up.sh` profile `modelsDir`, any `HuggingFace__ModelsDirectory` a live round points at), hard-link or copy the GGUF, its `.xe-model.json` sidecar and any projector from the user-level store: `ln <src> <dir>/`, never `ln -s`. Every installed-model member is refused when `FileInfo.LinkTarget` is set or the file is a reparse point, so a symlinked model is skipped at startup (`invalid acquisition sidecar` in the host log), `GET models` says `isAvailable=false`, and the lab stops at the model phase (exit 2, no manifest). **Prevents:** a round spending its first hour on a "model not installed" that is only a link. **Authority:** `InstalledGgufSnapshotStore` / `InstalledGgufDeletionStore` member checks, `ValidatedGgufImportSource.OpenNoFollow`; wiki 13 "Lab bootstrap for live rounds".

### `lab-up.sh` in a worktree needs the models hard-linked, projector included, and a Debug AppHost built before `--no-build`

**Rule:** before `scripts/lab-up.sh` runs from a worktree, hard-link every installed model file the round needs into that worktree's lab models dir, including the multimodal projector GGUF beside its weights, and build the AppHost once in Debug; only then is `--no-build` valid. **Prevents:** a lab that boots but reports the 27B as not installed (projector missing), or fails on a stale or absent Debug build. **Authority:** the `scripts/lab-up.sh` header; "A lab or scratch models dir holds hard links or copies, never symlinks" above.

### A CPU trace of a turn driven by `gpu-smoke-driver.py chat` mostly measures the driver

**Rule:** to profile the node's own CPU during a chat turn, drive the turn from a WebSocket or keep-alive client (the SPA), or subtract the transport frames: `scripts/gpu-smoke-driver.py chat` uses SignalR long polling with a new TLS connection per poll, about 130 requests per turn. **Prevents:** reading Kestrel TLS handshakes, `NodeSecurityStampCheck.IsCurrentAsync` and the middleware pipeline, 60–70 % of the sampled CPU in that setup, as product cost (2026-10-19). **Authority:** `command_chat` and its long-polling client in `scripts/gpu-smoke-driver.py`.

### Exit 9 naming an unknown migration after switching branches is the downgrade guard, not a broken database

**Rule:** a data directory a branch with a newer migration has run on (the AppHost data, a lab) refuses to start under an older checkout: exit 9 and a hint naming the migration. Use a fresh data directory or restore the pre-migration snapshot; never delete the history row. **Prevents:** "repairing" `__EFMigrationsHistory` by hand to get past the guard. **Authority:** `NodeDbRestoreStaging.FindUnknownAppliedMigrationAsync`, `Program.ApplyStartupMigrationsAsync`.
