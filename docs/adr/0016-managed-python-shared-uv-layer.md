# ADR 0016: Managed Python is one shared uv layer in its own provider project, with one pinned uv and one XE-owned toolchain store

- **Status:** Accepted — by the repository owner (`w0rldx`) on 2026-09-27.
- **Date:** 2026-09-27
- **Scope:** How the engine acquires uv, where uv, its managed CPython installs and its cache live on disk, how a
  uv-managed feature environment is identified, and which project owns that machinery. It changes nothing about what
  Training or the compute tool (`run_python`) do, and nothing about the sandbox or any execution backend.
- **Authority:** Operator decisions D1–D6 of the 2026-09-27 managed-Python plan (post-1.0; uv is not bundled; a new
  `Providers.Python` project; a Windows toolchain layer after the project split; the Node settings status card), plus
  the routine calls recorded there (feature environment roots stay put, one CPython minor, no automatic pruning,
  `UV_PYTHON_INSTALL_REGISTRY=0` everywhere, the inherited `%LOCALAPPDATA%` ACL on Windows).
- **Amends:** [ADR 0005](0005-training-runtime-python-exclusivity-and-project-placement.md) Decision §3 only — see
  [What this amends, and what it deliberately does not](#what-this-amends-and-what-it-deliberately-does-not).

## Context

Two features run uv-managed Python: Training (ADR 0005) and the sandboxed compute tool
(`ComputePythonEnvironment`, [19-compute-tools.md](../wiki/19-compute-tools.md)). They share **code** but duplicate
**artifacts**. The compute tool provisions its numpy/scipy/sympy venv through Training's public `UvBinaryAcquirer`,
`ITrainingProcessRunner` / `LinuxTrainingProcessRunner` and `TrainingRuntimeEnvironment.BuildUvEnvironment`, with the
uv pin in `TrainingRuntimePins`; yet `TrainingRuntimeLayout` roots at `RuntimeCacheDirectory.Resolve()/training-runtime`
and `ComputePythonEnvironment.DefaultCacheRoot` at `.../compute-runtime`, and each gets its own `uv/<version>`,
`pythons` and `uv-cache`. Both lockfiles already declare `requires-python = ">=3.13,<3.14"`, so the two CPython
installs are the same minor twice.

`Client.Application` referencing `Providers.Training` is legal — it references every provider by design — so this is
not a layering violation. It is an ownership smell: ADR 0005 §3 scoped `Providers.Training` to "only uv/venv/subprocess
mechanics" *for training*, and the compute tool now depends on a training project for a pin, a downloader, a runner and
an exception type (`TrainingRuntimeException`) that are not about training. A uv bump is a Training change that
the compute tool silently inherits on its next provision; a Windows uv path would have to be added to a project whose profile will never
run on Windows.

A shared leaf has precedent: `Providers.OpenAICompatible.Core` exists once, referenced by two providers, as the one
reviewed sibling exception in [02-project-layout.md](../wiki/02-project-layout.md).

## Decision

### 1. A leaf-like `Providers.Python` owns the generic uv mechanics

`XE-Local-AI-Engine.Providers.Python` (namespace `XE_Local_AI_Engine.Providers.Python`) references
`Providers.Abstractions` only — for `SetsidLocator` and `RuntimeCacheDirectory` — so unlike the reference-free
`OpenAICompatible.Core` it is leaf-*like*, not a leaf. It is consumed by `Providers.Training` (a reviewed sibling edge,
the second after `OpenAICompatible.Core`) and by `Client.Application`. It owns: uv acquisition (`UvBinaryAcquirer`),
the uv pin (`ManagedPythonPins`), the uv environment allowlist (`ManagedPythonEnvironment`), the scrubbed, tree-killed
spawn (`IPythonToolRunner` / `LinuxPythonToolRunner` with its libc process-group handle) and `ManagedPythonException`,
which keeps `TrainingRuntimeException`'s user-safe-message contract with neutral wording.

It owns no feature semantics. Training keeps its probe and `ProbeContractVersion`, its train/export environments, the
spawner, inspector and process handles, `TrainingRuntimeService`, `InstalledTrainingRuntimeStore`, its layout and GPU
prerequisites. Compute keeps `ComputePythonEnvironment`. Feature catch sites accept both exception types.

### 2. uv is pinned by XE and downloaded at run time, never bundled

The engine pins one uv version and its per-asset SHA-256 (from the upstream `.sha256`), downloads it on first use and
verifies the digest before extracting. It never tracks upstream automatically; a bump is a deliberate commit. uv is
**not** shipped in release artifacts, so `NOTICE` §3 ("obtained on the user's machine") is unchanged: bundling would
remove one download, not the failure domain, because the first provision still fetches CPython and wheels over the
same network.

### 3. One shared toolchain store; feature environments stay in their roots

uv, the managed CPython installs (`UV_PYTHON_INSTALL_DIR`) and the uv cache (`UV_CACHE_DIR`) move to one store under
`RuntimeCacheDirectory.Resolve()`. Feature environments stay where they are (`training-runtime/`, `compute-runtime/`);
no layout change forces the multi-GB Training environment to reinstall. Both lockfiles keep
`requires-python = ">=3.13,<3.14"` so one CPython minor serves both.

- **No automatic pruning.** The store is machine-global — shared by every node profile and dev checkout on the account —
  and a venv's `pyvenv.cfg` `home` points into it, so no host can know what another host still uses. Toolchain removal
  goes only through uv's own commands (`uv python uninstall`, `uv cache clean`), never a directory delete.
- **Layout** (`ManagedPythonToolchain`): `python/uv/<version>/…`, `python/pythons/` (`UV_PYTHON_INSTALL_DIR`),
  `python/cache/` (`UV_CACHE_DIR`) under `RuntimeCacheDirectory.Resolve()`.
- **Concurrency** relies on uv's documented cache locking and its install-dir `.lock`; no in-process semaphore, which
  could not serialize across hosts. The engine's own steps are made cross-process safe with exclusive `flock(2)` lock
  files instead: `UvBinaryAcquirer` holds `python/uv/.acquire.lock` around its check → rename-aside → move and re-checks
  inside it (a waiter adopts the winner's tree); its lock-free fallback, for an older build on the same store, adopts a
  complete `uv/<version>` and only renames aside and deletes one without the executable. The compute provision holds
  `compute-runtime/.provision.lock` (below). A lock file is never deleted while hosts run — a new inode would give two
  holders; only the uninstaller removes it.
- **`UV_LINK_MODE=copy` for the compute sync.** `ComputePythonEnvironment` strips write bits from the venv
  (`SetTreeWritable`); under uv's default hardlink mode those inodes would be shared with the cache and Training's venv.
  The lockdown also skips symlinks, since `chmod` follows `bin/python` into the shared CPython store.
- The compute sandbox binds the venv and the shared CPython root read-only; it never binds the store above them. The
  jail therefore sees every managed interpreter in the store, including ones other features or checkouts installed.
- **Migration.** Compute records the store's `pythons` path beside the lockfile digest; a mismatch rebuilds through a
  **staged swap** like Training's: `uv sync` into a fresh `compute-runtime/venv.staging/` (never over the old `.venv`,
  whose `pyvenv.cfg` `home` and `bin/python` link would keep naming the old root), an import check of the staged
  interpreter (`python -I -c "import numpy, scipy, sympy"`), then `venv/` → `venv.backup/`, `venv.staging/` → `venv/`,
  an atomic record replace, and the backup deleted. The adopted path stays `venv/.venv`; a uv venv survives the rename
  because `pyvenv.cfg` names the store by absolute path and `sys.prefix` follows the interpreter (proven live). Any
  failure before the swap leaves the old venv in service: when it still has a record and a resolvable interpreter it is
  served (cached until a restart or repair retries) and status reads `Ready` with the failure as its reason, Training's
  precedent; without one the state is `Failed` as before. A crash between the renames is recovered under the
  provision lock at the next provision (a lone `venv.backup/` is renamed back). Repair uses the same path, so a failed
  repair keeps the old venv. The legacy `compute-runtime/{uv,pythons,uv-cache}` go only after a successful adopt.
  Ceiling: a venv of the pre-identity build (`installed-compute-lock.sha256`) has no record to serve it from, so an
  offline first start after that upgrade reports `Failed`, but keeps the old tree for the next online attempt.
  Console-script shebangs in `venv/.venv/bin` still name the staging path after the rename; nothing uses them, since `run_python` execs `python -I -`.
  The disk layout (paths, record, identity, swap, recovery) lives in `ComputeRuntimeDirectory`; the gate, lease,
  cached runtime and status mapping stay in `ComputePythonEnvironment`. Training never force-reinstalls: its legacy dirs go only once the
  active venv's `pyvenv.cfg` `home` is under the store — after `Ready`, outside the adopt rollback, re-checked at
  startup and at the next install — or on Remove.

**The cold-start race M2 fixed.** Several `ComputePythonEnvironment` instances provisioning one empty root (the live
suites do this; so do two hosts) failed 1–3 of 31 live tests with "The pinned compute runtime could not be provisioned
on this node." The inner exception was `ManagedPythonException: The uv archive did not contain the expected
executable.`: each acquirer extracted to a private staging dir, then *deleted* whatever `uv/<version>` existed and
moved its own in, so a winner's tree vanished between its move and its `File.Exists` check (and from under any caller
already handed the path). A later review found the same shape one step earlier — two acquirers over a *broken*
`uv/<version>`, the second renaming aside the good tree the first had just landed — so the acquirer now serializes
under its own file lock and re-checks inside it; the compute file lock closes the remaining host-side window
(one provision's final read-only lockdown and `.work` delete running under another's live `uv sync`).

### 4. Every feature environment has an explicit identity

Identity is: profile id, Python minor, lockfile SHA-256, profile revision, RID, plus the probe contract version for
Training. A mismatch means the environment needs an update. The uv version is recorded but **informational only**: a uv
bump must not flag a working multi-GB environment. Status states are `NotProvisioned`, `Provisioning`, `Ready`,
`UpdateRequired`, `RepairRequired`, `Failed` and `Unsupported`, each with a reason. Reading status never provisions.

**As implemented (M3).** `ManagedPythonEnvironmentIdentity` and the status types live in `Client.Application`
(`Services/ManagedPython/`), not in `Providers.Python`: only the status service compares identities, and Training
contributes only `TrainingRuntimeStatus.ShippedLockfileSha256`. Compute persists the identity plus the store path in
`installed-compute-runtime.json`; Training derives it from `installed-training-runtime.json` unchanged, with a profile
revision that is not recorded there and so is `1` on both sides until its profile changes shape. The lockfile digest
never reaches the wire; a differing lockfile is reported as the mismatch `lockfile`.

**Deviations from the original design.** The status carries no uv "source" field: uv has exactly one source, the pinned
download. CPython installs come from a listing of `python/pythons` (alias symlinks and uv's dot-entries skipped), not
`uv python list --only-installed`, because reading status must never spawn uv. A build whose shipped lockfile is
missing reports Training as not comparable (`Ready`, the installed runtime keeps working) but Compute as `Failed`,
because Compute provisions lazily from that lockfile and cannot run without it.

**Execution lease.** `run_python` holds an in-process execution lease on the Compute environment from before it asks
for the runtime until its jail is gone; remove and repair answer `busy` while any is held. The ceiling is the host: a
`run_python` on another host or checkout sharing `compute-runtime/` is invisible, so a remove there can still delete the
venv under a running script (the next call re-provisions, since a cached runtime whose interpreter is gone is dropped).

**No "remove toolchain" action.** The M3 plan offered one (via `uv python uninstall` / `uv cache clean`, only while no
environment on this host is `Ready`). It is dropped deliberately: the store is machine-global, and a host cannot see
the environments other node profiles and checkouts built on it, so "nothing here is `Ready`" does not mean "nothing
uses it". Toolchain removal stays with the uninstaller and uv's own commands. The status reports the toolchain
read-only: the pinned uv version, whether it is present, and the CPython install directories.

### 5. Windows toolchain

`UV_PYTHON_INSTALL_REGISTRY=0` is set on every platform (a no-op off Windows), so a CPython install never writes a PEP 514
registry key. The Windows toolchain layer (per-RID pin matrix, zip extraction, a Windows runner using
`Kill(entireProcessTree: true)`) may land ahead of any Windows consumer, proven by a Windows-only test. On Windows the
store inherits the per-user `%LOCALAPPDATA%` ACL; no explicit owner-only ACL code is written unless requested. Training
stays Linux-only by profile; the compute tool on Windows waits on Windows containment.

As implemented (M4): `ManagedPythonPins` resolves a `ManagedPythonUvAsset` per RID (`linux-x64` tarball, `win-x64` zip
with a flat `uv.exe` layout, one uv version) and refuses any other platform with a user-safe `ManagedPythonException`;
`UvBinaryAcquirer` extracts the zip entry by entry and rejects the whole archive if any entry resolves outside its
staging directory; `PythonToolRunner.ForCurrentPlatform()` picks `WindowsPythonToolRunner` on Windows. No Job Object:
uv and the venv interpreter are engine-chosen binaries, so the tree kill is enough. On Windows the uv environment adds
`SystemRoot`, `SystemDrive`, `windir` and `PATHEXT` to the allowlist and maps the isolated home and temp directories onto
`USERPROFILE`, `TEMP` and `TMP`. Long paths: .NET handles them natively, but uv, CPython or a wheel's build step may
not, so the uv acquisition every provision starts with refuses a store root over 100 characters when
`HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled` is not 1
(`ManagedPythonToolchain.EnsureWindowsPathBudget`). Known limits on Windows:

- **Wheels only.** The scrub passes no MSVC discovery variables (`VSINSTALLDIR`, `INCLUDE`, `LIB`, …), so a profile that
  needs a source build fails; every profile must lock to wheels.
- **Known-Folder callers still reach the real profile.** `USERPROFILE`/`TEMP`/`TMP` redirect environment-based lookups
  only; code that calls `SHGetKnownFolderPath` (uv's own default dirs, which the `UV_*` variables override) still
  resolves the operator's folders.
- **`BuildAllowlisted`-only runs** (the Training probe/train/export and the compute script) get no `TEMP`/`TMP`/
  `USERPROFILE` today; they need them the day a feature is enabled on Windows (M5).

Windows proof owed: an operator run of `ManagedPythonWindowsLiveTests` (it skips, with a reason, on Linux).

### 6. Agents never call uv or pip

The layer serves engine-authored, locked profiles only. No agent tool installs packages, runs uv or pip, or names a
profile; a new profile is a reviewed source change with its own lockfile.

### Where each part lands

All six decisions are accepted now. They land in slices of the 2026-09-27 managed-Python plan, and this record claims
nothing beyond what has shipped:

| Part | Slice |
|---|---|
| §1 project, move, exception contract; §2 uv 0.12.5 → 0.12.19; `UV_PYTHON_INSTALL_REGISTRY=0` in `ManagedPythonEnvironment.BuildUvEnvironment` | M1 — implemented on `feature/managed-python-m1` |
| §3 shared store, `UV_LINK_MODE=copy`, Training and Compute migration of legacy toolchain dirs, the cold-start race fix | M2 — implemented on `feature/managed-python-m2` |
| §4 identity, `GET /api/local/v1/python/status`, Compute repair/remove | M3 backend — implemented on `feature/managed-python-m3` |
| §4 the Node settings card | M3 frontend |
| §5 the Windows toolchain layer and its Windows-only test | M4 — implemented on `feature/managed-python-m4`; Windows run owed |
| Compute profile on Windows | M5, blocked on the execution-runtime plan's Windows containment |

§6 already holds (no agent tool reaches uv or pip) and stays a review rule.

## What this amends, and what it deliberately does not

**Amended — ADR 0005 Decision §3.** `Providers.Training` still owns only uv/venv/subprocess mechanics for Training, but
the *generic* part of those mechanics — acquiring uv, the uv environment allowlist, the scrubbed spawn — now lives in
`Providers.Python` and is shared, rather than being Training types that another feature borrows. `Providers.Training`
references `Providers.Python` in addition to `Providers.Abstractions`, and `LayerDependencyTests` registers the edge.

**Unamended — ADR 0005 §1 and §2.** Training semantics stay in Python behind the stdio protocol; the system interpreter
is never used; a run still holds the node exclusively.

**Unamended — ADR 0007.** The compute tool remains a sandbox consumer that declares filesystem isolation and fails
closed without it. This record adds no backend and changes no containment.

## Non-goals

A sandbox or containment change; Training on Windows; a generic runtime-acquisition hub; agent-controlled packages; a
plugin system for future profiles; repository build scripts; auto-tracking upstream uv; bundling uv.

## Consequences

- **One pin, one bump.** A uv bump is a `Providers.Python` change that both features pick up; it no longer reads as a
  Training change.
- **One more reviewed sibling edge.** `Training → Python` joins `LlamaServer/OpenAICompat → OpenAICompatible.Core`. The
  pressure to put a training or compute concept into `Providers.Python` is a review responsibility; `LayerDependencyTests`
  catches only illegal references.
- **Disk is reclaimed once, then never automatically.** One CPython minor and one cache replace two, but nothing prunes
  the shared store; an operator who wants the space back uses the uninstaller or uv itself.
- **The migration is the risk.** Relocating CPython moves a compute sandbox bind mount, and the Training legacy
  toolchain may be deleted only after a successful reinstall or on remove, never inside the adopt/rollback boundary.
- **Windows code without a product consumer.** Until the compute profile runs on Windows, the Windows toolchain layer's
  only evidence is its test class; it is kept deliberately small.
