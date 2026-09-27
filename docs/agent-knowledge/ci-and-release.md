# CI and release

Scope: CI legs, shard packing, coverage, docs-inventory, GPU smoke, Velopack packaging/release, git/review traps.
Read when: editing `.github/workflows/**`, `HEAVY`/sharding, coverage merge, `publish/**`, `scripts/release/**`,
`cliff.toml` or Velopack bootstrap code, or committing/reviewing in a shared worktree.

## CI and gates

### The GPU smoke is the only gate that proves the GPU did the work — and its exit codes are a taxonomy, not a scale

**Rule:** run `scripts/run-gpu-smoke-local.sh` before a tester RC or after inference/runtime changes; a correct answer
does not prove GPU execution, so require utilization plus a VRAM rise from a pre-start baseline.
Exit codes: `1` a product step failed (every step records a verdict; `=== Summary ===` names them), `5` infra abort (nothing judged), `2` missing prerequisite, `3` another instance running, `4` state undetermined, `75`
contaminated (void), `130` interrupted. Effective GPU/CPU execution comes from `IRuntimeDeviceAudit` (binary +
hardware + `--list-devices`), never the installed variant record; a failed/timed-out audit is unknown, never a CPU-fallback
alarm. **Prevents:** a CPU-fallback run passing as GPU. **Authority:** `scripts/run-gpu-smoke-local.sh`,
`scripts/tests/gpu-smoke.test.sh`, `IRuntimeDeviceAudit`. [evidence](../agent-knowledge-evidence.md#gpu-smoke-evidence)

### `release.yml` is the intended release path — GitHub Actions must be enabled to run it

**Rule:** Actions enablement is external state: check `gh workflow list --all` / `gh run list`, never quote an old
result. Never lower the shellcheck pin (0.11.0) or lint severity: `ubuntu-latest`'s 0.9.0 false positives fail
`--severity=style`. Never re-add `docker pull` or `XE_REQUIRE_DOCKER_TESTS=1` to `build-and-test.yml`: the wire shape
runs against `Testing.FakeDocker`, the daemon half in `scripts/run-docker-smoke-local.sh`. `python-quality` uses the root
tooling manifest: never add dev tools to `tools/training/pyproject.toml`. **Prevents:** registry blips reddening PRs.
**Authority:** `build-and-test.yml`, `scripts/lint-release-scripts.sh`.
[evidence](../agent-knowledge-evidence.md#packaging-and-repository-consolidation)

### Coverage and summary checks can pass hollow

**Rule:** coverage rewrites assemblies in place, so concurrent covered processes need separate cloned output trees. An
empty report can claim `line-rate="1"`, so `scripts/merge-cobertura.py` fails on zero source lines; keep the report-count
checks beside `scripts/backend-coverage-baseline.txt` and raise that baseline only when coverage improves. The
`Passed!|Failed!` grep proves only that MTP printed a summary: a unit where every test skipped still prints `Passed!`.
**Prevents:** hollow coverage and a green gate that ran nothing. **Authority:** `scripts/merge-cobertura.py`,
`build-and-test.yml` (`--minimum-file scripts/backend-coverage-baseline.txt`).

### The packer's weights are load-bearing, and it cannot split a namespace

**Rule:** re-measure the `HEAVY` table in `scripts/run-tests-memory-safe.sh` from a green CI run's TRX artifacts
(`scripts/test-durations.py --heavy --runs N`) whenever a namespace grows; an unlisted namespace weighs 1. When one
namespace alone outweighs a bin, split it into sub-namespaces (folder = namespace, IDE0130) instead of re-tuning
`TEST_GROUPS`. **Prevents:** a solo namespace flooring its shard's wall time past the job timeout. **Authority:** the
`HEAVY` list and its weight-parse `sed` in `scripts/run-tests-memory-safe.sh`; the source named in its header comment.
[evidence](../agent-knowledge-evidence.md#the-packers-weights-are-load-bearing-and-it-cannot-split-a-namespace)

### A HEAVY weight from one CI run is noise, and no table can balance the legs better than runner speed allows

**Rule:** weight `HEAVY` from the mean of the available green runs (`--runs N`); re-weight only when the namespace set
changes or a weight is off by more than ~2x, never to chase shard balance, and score a re-weight on a run that did not
build it (max/min ~1.5 is noise). Only splitting the solo namespace in the heaviest bin moves the slowest leg.
**Prevents:** re-weighting that balances its own run and no other. **Authority:** `scripts/test-durations.py` `--runs`
and `scripts/tests/test_test_durations.py`. [evidence](../agent-knowledge-evidence.md#heavy-re-weight-convergence-2026-09-14)

### Add a hub, a route family, a React feature or a project — and name it in the wiki, or `python-quality` goes red

**Rule:** the trigger and command are in `AGENTS.md`. Beyond that: the checker parses canonical definitions, not
comments, so keep examples out of production enum/route declarations; `--verbose` exits 2 when an inventory cannot run and
every inventory must be non-empty. Its tests stay `unittest`: `scripts/run-release-contract-tests.sh` runs each
`scripts/tests/test_*.py` directly and expects `Ran N tests`/`OK`, so bare pytest functions do nothing there.
**Prevents:** a vacuous inventory or test pass. **Authority:** `scripts/docs-inventory-check.py`,
`scripts/run-release-contract-tests.sh`.

### Keep a job literally named `build-and-test`, however the backend gate is split

**Rule:** branch protection requires `build-and-test` (shape: `docs/wiki/13-testing-and-validation.md`, "What
`build-and-test.yml` and `e2e.yml` describe"). It keeps `needs: backend-tests` and `if: always()`, and its first step fails
unless `needs.backend-tests.result == 'success'`. `TEST_SHARD` without `TEST_GROUPS` is refused by
`scripts/run-tests-memory-safe.sh`. **Prevents:** a failed leg leaving the required check skipped, which never resolves.
**Authority:** `.github/workflows/build-and-test.yml` (`build-and-test` job).

## Packaging and release

### Packaging (Velopack)

**Rule:** package on a quiet machine after `dotnet build-server shutdown`. `.github/workflows/package-velopack.yml` is the
only packaging job (`release.yml`/`dev-build.yml` differ by inputs); never copy a step into a caller. A `dev/`
tag must never steer official notes: keep `generate-release-notes.sh`'s HEAD probe `--match 'v*'` and `cliff.toml`'s
`tag_pattern` anchored (`^v[0-9]`, an unanchored REGEX, not a glob) plus `ignore_tags = "^dev/"` (folds, unlike
`skip_tags`). Packagers validate the update-policy file and refuse a wrong channel (`CopyToPublishDirectory="Always"` can leave it
missing until the source timestamp changes).
**Prevents:** drifting packaging copies; Development snapshots truncating release notes. **Authority:**
`scripts/tests/release-workflow-contract.test.py`, `scripts/tests/test_generate_release_notes.py` (`CliffTagBoundaryTests`).

### Velopack version pins move together

**Rule:** bump Velopack in one commit across `VPK_VERSION` in `release.yml` and `dev-build.yml`, the `vpk-version` input
default in `package-velopack.yml`, and the `Velopack` pin in `Directory.Packages.props`. **Prevents:** packing with one vpk
while the app bootstraps another. **Authority:** `scripts/tests/release-workflow-contract.test.py`
(`test_vpk_version_pin_is_consistent_across_every_release_surface`,
`test_both_release_paths_call_the_shared_packaging_workflow`).

### A Development build's version extends the anchor with DOTS ONLY — never a second hyphen

**Rule:** extend the anchor's prerelease label with dots (`1.0.0-rc.2` -> `1.0.0-rc.2.dev.<yyyymmdd>.<n>`); after 1.0.0
bump the patch (`1.0.1-dev.<yyyymmdd>.<n>`). The anchor is the newest `v*` tag reachable from the commit, never
`eng/ReleaseVersion.props`. **Prevents:** Velopack's `SemanticVersion` splitting at the first hyphen and ranking
`1.0.0-rc.2-dev.1` above `1.0.0-rc.9` (stranding Development testers), or `1.0.0-dev.*` sorting below the RCs.
**Authority:** `scripts/release/dev-build-identity.py` `compose_dev_version`; `scripts/tests/test_dev_build_identity.py`
`test_no_composed_version_ever_contains_two_hyphens`; Velopack 1.2.0 `SemanticVersion`.

### Development releases are capped at 30 because the stock GitHub source reads only the 10 newest releases

**Rule:** one GitHub release per Development snapshot, pruned by `dev-build.yml`'s `prune` job to the newest 30; never an
uncapped stream, never merged into a `win`/`linux` release. **Prevents:** Velopack's `GithubSource.GetReleases`
(`per_page=10`, prerelease filter applied after truncation) evicting every stable/RC release so `CheckForUpdatesAsync`
silently returns `null`. The cap is damage control; the fix is the app's paginating source. **Authority:**
`XE-Local-AI-Engine.Client/Services/AppUpdate/PaginatingGithubSource.cs`; `scripts/release/dev-build-identity.py`
(`DEFAULT_KEEP`, `select_prune`); ADR 0014 D2/D3.

### Deleting a superseded Development release is safe; deleting its tag is not

**Rule:** prune with `gh release delete --yes`, never `--cleanup-tag`; a `dev/<version>` tag outlives its release forever.
**Prevents:** a reported Development version with no commit behind it, and a broken previous-Development lookup that
rebuilds the same commit or misnumbers the counter (a delta-chain gap is harmless: Velopack falls back to a full package).
**Authority:** `.github/workflows/dev-build.yml` `prune` and its three non-`dev/` guards (`jq` filter, `case` guard,
`select_prune`); `scripts/tests/release-workflow-contract.test.py` `test_dev_build_prune_never_deletes_tags_or_official_releases`.

### Only the packaged main executable calls `VelopackApp.Build().Run()` — a second process of the same install must not

**Rule:** inside an installed app `Run()` deletes superseded `.nupkg`s and, with a staged newer package, spawns
`Update.exe apply --waitPid <this pid>` and exits. From a secondary process that pid is wrong, the update rewrites the
install under the live main executable, and the once-only guard is per-process. On Windows only
`WindowsLauncher` (the packed `--mainExe`) calls it; on Linux the Desktop shell is the main executable
and calls it (behind `OperatingSystem.IsLinux()`) first in a synchronous `Main`, outside every `try`. A supervised engine points Velopack at its supervisor instead.
**Prevents:** two processes racing one update. **Authority:** `XE-Local-AI-Engine.Desktop/Program.cs`,
`XE-Local-AI-Engine.WindowsLauncher/Program.cs`, `FrameworkDependentVelopackBootstrap.CreateSupervisedProcess`, the
`main-exe` matrix in `.github/workflows/package-velopack.yml`.

## Git and review

### Review tooling that auto-detects the default branch fails here — pass the base explicitly

**Rule:** pass the review base explicitly (`--base develop` or the tool's equivalent), using the remotes discovered in §0
when a remote-qualified ref is needed. Do not add a remote alias to satisfy auto-detection. **Prevents:** a missing or
incorrect review base when checkout remote names differ. **Authority:** `AGENTS.md` branch policy.

### a path-scoped `git commit -- <paths>` silently skips an UNTRACKED file among those paths

**Rule:** `git commit -- <path>...` commits only tracked changes and ignores a named untracked file without warning. In a
shared worktree commit new files through the `AGENTS.md` private-index recipe (`GIT_INDEX_FILE` + `read-tree` /
`update-ref`), or check `git status --short -- <paths>` for `??` before and after. **Prevents:** a "complete" commit missing
its new test or source file, and the fix-up commit that follows. **Authority:** `git help commit` ("only from the named
paths ... already known to Git").

## Stale beliefs

Superseded claims; the entries above are the active rules.

| Stale belief | Current correction |
|---|---|
| CI runs test projects sequentially. | Projects run concurrently with separate result directories; the main Tests module uses grouped batch runner (§1). |
| Coverage should use one process per namespace. | Coverage instrumentation makes that prohibitively expensive; group instead. A local run uses `TEST_GROUPS=$(nproc)`; CI uses `TEST_GROUPS=16` split across 4 `TEST_SHARD` legs (§1). |
| Both build-and-test and E2E are blocking PR gates. | Main CI targets develop; E2E is manual/label-triggered (§1). |
| `release.yml` passes `--pre` to `vpk pack`. | `--pre` is only for upload; packing uses SemVer suffix (§1). |
