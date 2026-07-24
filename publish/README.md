# XE Local AI Engine — distribution & release guide

This directory holds the desktop launcher and uninstaller scripts, the canonical
Windows tester RC packager (`package-tester-win.ps1`), and the manual portable-zip
packager (`package-rc.sh`, a bash script). This document explains **how the version
is set** and **how a release is created**.

## Where releases go

Source and artifacts live in **two different repositories** that share only a version string:

| Role | Repository |
| --- | --- |
| **Source** — the code and its `v<version>` tags | `w0rldx/XE-Local-AI-Engine` |
| **Tester artifacts** — published releases + Velopack update feed | `w0rldx/XE-Local-AI-Engine.Tester-App` |

The `v<version>` git tag is created on **HEAD of the source repo**; `vpk upload github`
then creates a same-named release on the **tester repo**, whose commits are unrelated.
A tester release therefore has no tag in the source repo, and vice versa — both are expected.

**Tag form changed mid-flight.** The seven releases published 2026-06-26 → 2026-07-07 carry
a **bare** tag (`0.1.0-rc.4.1`) with a `v`-prefixed release *name*. `package-tester-win.ps1`
now uploads with `--tag v<version>`, so releases from `0.1.0-rc.4.2` onward are v-prefixed on
both. Its `Find-GitHubRelease` probes `v<version>`, then `<version>`, then the release name —
so the already-published guard sees the live release regardless of how its tag was spelled.

## Prerequisites on the packaging machine

`package-tester-win.ps1` runs on Windows and needs:

- **PowerShell 7+ (`pwsh`) — not Windows PowerShell 5.1.** The script declares
  `#Requires -Version 7.0`. It pairs `$ErrorActionPreference = "Stop"` with native-stderr
  redirection to detect a `gh` 404, which 5.1 escalates into a terminating error instead.
  Launch it from `pwsh`, not from the blue `powershell.exe` console.
- On `PATH`: **`git`**, **`pnpm`**, the **.NET SDK** per `global.json`, **`dnx`** (it runs
  `dnx vpk@1.2.0`), and an **authenticated `gh` CLI** (13 call sites —
  `gh release view`/`list`/`download`/`edit`, driving the already-published guard, the
  draft-publish hash verification, and the release-body update). A missing `gh` login does
  not fail fast: it fails partway through a full release build, after the gate suite has
  already run. Publication also needs network access to resolve the pushed source tag from
  `https://github.com/w0rldx/XE-Local-AI-Engine.git`.
- A **non-UTC machine time zone** — see the note under Path A.
- Two environment variables: `VPK_TOKEN` and `XE_TESTER_GITHUB_APP_CLIENT_ID`.

`package-rc.sh` (bash) needs **`pnpm`** and the **.NET SDK**; it uses no Velopack tooling
and needs no GitHub credentials.

---

## Versioning — single source of truth

The version lives in **`Directory.Build.props`** as two MSBuild properties:

```xml
<VersionPrefix>0.1.0</VersionPrefix>
<VersionSuffix>rc.4.2</VersionSuffix>
```

These compose to the SemVer string `"<VersionPrefix>-<VersionSuffix>"`. The values
above are an example snapshot; always read the file before packaging. A bare
`<VersionPrefix>` with no suffix is a stable release. Any `-rc` / `-alpha` /
`-beta` / `-pre` label marks the build as a **prerelease**.

This one value propagates everywhere — there is no second place to edit:

| Consumer                  | How it reads the version                                                                                                                                         |
| ------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **About dialog** (in-app) | Vite injects `VITE_APP_VERSION` at build time via `XE-Local-AI-Engine.Client.React/vite.config.ts` → `resolveAppVersion()`, which reads `Directory.Build.props`. |
| **Windows tester RC**     | `publish/package-tester-win.ps1` reads the two props, validates an optional `-Version` assertion, and passes the composed value to Velopack.                   |
| **Manual portable zip**   | `publish/package-rc.sh` → `read_version()` reads the two props → `xe-local-ai-engine-<version>-<rid>.zip`.                                                       |
| **Velopack package**      | `publish/package-tester-win.ps1` passes the composed value as `--packVersion`. (`.github/workflows/release.yml` does the same, but is disabled — see below.)     |
| **Source git tag**        | Convention `vX.Y.Z[-suffix]`, e.g. `v0.1.0-rc.4.2` — must match the composed version. Created on HEAD of `w0rldx/XE-Local-AI-Engine`.                            |
| **Tester release tag**    | Created on `w0rldx/XE-Local-AI-Engine.Tester-App` by `vpk upload github --tag v<version>`. Releases up to `0.1.0-rc.4.1` are **bare**-tagged; see above.        |

> **To cut a release: bump `VersionPrefix`/`VersionSuffix` in `Directory.Build.props`
> first, commit, then tag.** Everything downstream picks it up automatically.

**A version string is single-use.** `0.1.0-rc.4.1` is burned: it was published to the
tester repo on 2026-07-07 with **no matching source tag**, so no commit in this repo
identifies what shipped. The packager's already-published guard will refuse to upload over
a live release — correctly, since `vpk --merge` into a shipped feed would hand testers
assets nobody smoke-tested. Bump rather than reuse. The tag-before-upload requirement and
the both-spellings release lookup exist precisely to stop this recurring; see
[`docs/velopack-release-install-guide.md`](../docs/velopack-release-install-guide.md#cautionary-tale-how-010-rc41-got-burned).

For every release-specific version change:

1. Update `VersionPrefix` and `VersionSuffix` together in `Directory.Build.props`.
2. Update `CHANGELOG.md` for that version. **Nothing generates it** — `cliff.toml` drives
   `RELEASE_NOTES.md` only. Do not rewrite command examples merely to claim they are the
   current release.
3. Commit the version and notes.
4. Create the matching `v<version>` source tag on HEAD.
5. Run the Windows tester packager with `-Version <version>` when you want an
   additional mismatch assertion. The script still reads the version from
   `Directory.Build.props` and fails if the values differ.

---

## Creating a release

There are **three** paths in this directory and the workflows folder, and only one of
them ships anything:

| Path | What it produces | Status |
| --- | --- | --- |
| **A — `publish/package-tester-win.ps1`** (Windows, manual) | Velopack portable bundle + delta/full packages + update feed, uploaded to the tester repo | **canonical from `0.1.0-rc.4.0` onward.** Earlier RCs predate this script |
| **B — `publish/package-rc.sh`** (bash, manual) | plain self-contained portable zip, no Velopack metadata, **no self-update** | supported side path |
| **C — `.github/workflows/release.yml`** (tag-triggered CI) | — | **disabled; has never produced an artifact** |

> **Pushing a tag builds nothing.** `release.yml` is `disabled_manually` and its only
> three runs all failed on 2026-06-27. `build-and-test.yml` is likewise disabled and
> `e2e.yml` was never registered — 6 runs, 6 failures, 0 successes in the repository's
> whole history (`gh workflow list --all`, `gh run list`, verified 2026-07-24). The tag
> still matters, because Path A **requires** HEAD to carry it before uploading — but it
> is an input to the manual script, not a trigger.

### Path A — Canonical tester RC: manual Velopack build on Windows

Use `publish/package-tester-win.ps1` on Windows. It validates, builds, packs a portable
self-updating release, and uploads it to the tester repo
(`w0rldx/XE-Local-AI-Engine.Tester-App`).

```powershell
$env:VPK_TOKEN = "<fine-grained PAT, contents:write on the tester repo>"
$env:XE_TESTER_GITHUB_APP_CLIENT_ID = "<real public GitHub App client ID (Iv..., not the numeric App ID)>"
.\publish\package-tester-win.ps1
```

For a local pre-tag rehearsal:

```powershell
.\publish\package-tester-win.ps1 -SkipUpload
```

The rehearsal still requires `VPK_TOKEN`: the tester repository is private, and Velopack must
download its latest published full package before packing so it can generate the new delta. The
script downloads into an isolated seed directory, copies only that full package into a clean
versioned output directory, then follows Velopack's `download -> pack` flow without uploading.

The script enforces a clean working tree, validates that `-Version` matches
`Directory.Build.props`, and requires the exact local source tag before upload. Upload creates
or updates a **draft** release; it never publishes immediately. Smoke-test the exact
generated `Portable.zip`, retain the printed SHA-256, then publish that unchanged draft:

```powershell
.\publish\package-tester-win.ps1 -PublishDraft -ExpectedPortableSha256 <printed-sha256>
```

The publication command first verifies that the matching source tag is pushed to the canonical
source repository and resolves to the current HEAD. It then downloads all five Velopack assets
attached to the GitHub draft, verifies each against the SHA-256 manifest from the original pack,
checks that `-ExpectedPortableSha256` matches the manifest and downloaded Portable ZIP, and publishes
the existing draft without rebuilding or re-uploading.

`-PublishDraft` also requires the generated `RELEASE_NOTES.md` and
`publish/dist/XE-Local-AI-Engine-<version>-win.sha256.json` in the checkout. Both are generated,
git-ignored evidence from the packaging run, so publish from the same clean checkout and do not
delete them between draft upload and publication. If the checkout was cleaned or moved, regenerate
the notes and full five-asset digest manifest from the exact tagged pack output before publishing;
the command refuses to publish when either artifact is absent or inconsistent.

**The already-published guard resolves a release by all three spellings.**
`Find-GitHubRelease` probes `v<version>`, then bare `<version>`, then falls back to matching
the release *name* via `gh release list`. It is used at all three lookup sites — the
already-published guard, the `-PublishDraft` lookup, and the post-upload draft assertion —
and every downstream `gh release download` / `gh release edit` uses the **resolved** tag
rather than an assumed one. This is what stops `vpk --merge` pushing untested assets into a
live update feed.

It matters because historical tester releases are **bare**-tagged with `v`-prefixed *names*,
while new ones are `v`-prefixed on both; both forms must resolve indefinitely. Live-verified:
`0.1.0-rc.4.1` now resolves and the guard correctly refuses, `0.1.0-rc.4.2` is not found and
is safe to cut, and the old single-probe lookup of `v0.1.0-rc.4.1` returned null — exactly the
blindness that would have let an upload through.

**Parameters** (read from the current `param(...)` block; all optional):

| Parameter | Purpose |
| --- | --- |
| `-Version` | assertion only, **without** the leading `v`; must match `Directory.Build.props` exactly |
| `-GitHubAppClientId` | the public `Iv…` client ID; defaults to `$env:XE_TESTER_GITHUB_APP_CLIENT_ID` |
| `-TesterRepo` | tester repo URL; defaults to `https://github.com/w0rldx/XE-Local-AI-Engine.Tester-App` |
| `-SkipUpload` | pre-tag packaging rehearsal — build and pack, no upload |
| `-PublishDraft` | publish an existing draft; cannot be combined with `-SkipUpload` |
| `-ExpectedPortableSha256` | the smoke-tested Portable hash; must match the pack manifest and remote asset before all five remote asset hashes are verified |
| `-AllowUtcTestTimeZone` | accept reduced time-zone coverage on a genuinely UTC packaging machine |

**This script is the project's only enforced quality gate** — GitHub Actions enforces
nothing (see the table above). Its mandatory gates:

- **Frontend**: rejects local `.env*` and inherited `VITE_*` overrides, materializes the committed
  `.env.template` only for the build, then runs frozen install, lint, OpenAPI drift check, third-party license check,
  coverage-gated tests, production dependency audit, and production build.
- **Backend**: restore, transitive NuGet vulnerability audit, Release build, and
  solution-wide tests with serial test modules — including a **hollow-gate guard** that
  greps the output for an MTP `Passed!`/`Failed!` summary, because `dotnet test` exits 0
  when zero test projects enrol.
- **Package**: SPA presence, canonical tester channel/repository, caller-supplied
  non-placeholder GitHub App client ID, pinned git-cliff checksum, release notes, complete
  five-asset Velopack set, local digest manifest, and Velopack pack/upload result.

**The packaging machine must be in a non-UTC time zone.** The script does **not** set one —
it *requires* one, and refuses to run the backend tests otherwise. Before the test leg it
reads the local zone's current UTC offset and throws if it is `+00:00`, pointing you at
`tzutil /s "W. Europe Standard Time"`. Pass `-AllowUtcTestTimeZone` to accept the reduced
coverage instead.

The reason it can't just set one: the dormant workflow's `TZ=Europe/Berlin` is a **Unix-only**
mechanism in .NET. Windows resolves the local zone from `kernel32!GetDynamicTimeZoneInformation`
and reads no environment variable, so a `$env:TZ` line here would be a silent no-op —
non-UTC coverage would look configured while never actually applying. `tzutil /s` is the only
real forcing mechanism, and the script deliberately does not run it: it is global and needs
elevation, so changing the machine's clock is the operator's call, not the packager's.

**Artifacts.** `vpk pack` runs with `--noInst` on channel `win`, so a release carries
`XE-Local-AI-Engine-win-Portable.zip`, `-full.nupkg`, `-delta.nupkg`, `releases.win.json`
and `RELEASES` — **there is no `Setup.exe` and no `.AppImage`**. After draft upload the
script explicitly updates the GitHub release body (`vpk` sets it only when it *creates*
the release). The packaging checkout also receives
`publish/dist/XE-Local-AI-Engine-<version>-win.sha256.json`, which records the SHA-256 of all five
assets and is required to publish the draft.

**`-SkipUpload` relaxes exactly one thing: the client ID.** `VPK_TOKEN` remains mandatory because
the private tester repository supplies the previous full package used for delta generation. Every
build and test gate still runs. A *supplied* ID is always validated, rehearsal or not — a placeholder
(`REPLACE_`/`CHANGE_ME`/`TODO`) or a non-`Iv…` value (the numeric App ID is the usual
paste-o) is an error, not an absence. An *absent* ID is tolerated **only** under
`-SkipUpload`; uploading always requires a real `Iv…` ID.

A rehearsal with no ID bakes **no** client ID at all, so `AppUpdateChannelOptions.IsConfigured`
stays false and the updater ships inert rather than placeholder-configured. The publish dir is
stamped with `REHEARSAL-DO-NOT-SHIP.txt` — so the marker rides *inside* the Portable.zip — plus
warnings at start and finish. `dotnet publish` does not clean the publish dir, so a real run
deletes a stale marker left by an earlier rehearsal. `-SkipUpload` **with** a real ID produces a
normal shippable artifact and no marker.

### Path B — Tester zip (manual, no install, no self-update)

`publish/package-rc.sh` builds a self-contained manual zip an external tester unzips and
runs. It is a **bash script — you run it on Linux/WSL** — but it produces zips for
**both** `linux-x64` and `win-x64` by default, cross-building the Windows one. The version
is read from `Directory.Build.props` and baked into the filename.

```sh
publish/package-rc.sh                            # BOTH rids: linux-x64 and win-x64
publish/package-rc.sh --rid linux-x64            # one rid
publish/package-rc.sh --rid linux-x64 --skip-web # reuse the existing React dist (skip pnpm build)
```

> **The `win-x64` bundle is cross-built on Linux — smoke-test it on real Windows before
> tagging an RC.** Native-library self-extraction, console-close child cleanup, and
> browser auto-open cannot be verified off-Windows. This is why `package-tester-win.ps1`
> exists and why it is the path for anything a tester will actually receive. Note that a
> bare `--skip-web` still builds **both** RIDs — pass `--rid` too if you only wanted one.

Output (git-ignored): `publish/dist/xe-local-ai-engine-<version>-<rid>.zip` plus a
`.sha256` sidecar. The script builds the React SPA, publishes the single-file
self-contained binary, stages it with the launcher + uninstaller + `READ-ME-FIRST.txt` +
`LICENSE` and `NOTICE`, refuses to ship if any per-node runtime/state file leaked in, then
zips and checksums it.

**This zip never self-updates, for two independent reasons.** First, it carries **no
Velopack metadata** — there is no `vpk` step anywhere in the script, so there is nothing for
an updater to read. Second, it publishes with an explicit `-p:UpdateChannel=main` — the
intentionally inert channel — rather than silently inheriting the csproj default, so the
value is deliberate and enforced rather than incidental.

The script then *proves* the result: `assert_app_config_sane` hard-fails the build if the
staged config ever reads as **live** (a real repo URL plus a real `Iv…` client ID), with
"A portable zip is not a Velopack installation and must never self-update." It also rejects
`REPLACE_`/`CHANGE_ME` placeholders in every other `appsettings*.json`, allowing them only in
`appsettings.AppUpdate.json` and only because the inertness check has already proven they
leave `AppUpdateChannelOptions.IsConfigured` false and the updater honestly disabled.

See `publish/TESTER-QUICKSTART.md` for the tester-facing run instructions.

---

## Launcher file layout

The launcher scripts in this directory set `XE_LAUNCH_MODE=desktop` before starting
the binary. `package-rc.sh` copies them into the zip as the prominently named
`Start-XE-Local-AI-Engine.cmd` / `start-xe-local-ai-engine.sh`.

```
publish/linux/run-xe-local-ai-engine.sh      ← Linux launcher (source)
publish/windows/run-xe-local-ai-engine.cmd   ← Windows launcher (source)
```

---

## What `XE_LAUNCH_MODE=desktop` does

The launchers set the environment variable `XE_LAUNCH_MODE=desktop` before starting
the binary. This single flag enables all desktop-mode behaviour:

- **Loopback HTTP on an auto-selected free port** — Kestrel binds `http://127.0.0.1:<free-port>` instead of using the default URL configuration. HTTPS redirect and HSTS are bypassed (loopback HTTP is safe because traffic never leaves the loopback adapter).
- **Auto-opens the default browser** at the running URL once the host has started.
- **Console logs** — the Serilog console sink streams live log output to the terminal/console window.
- **Graceful shutdown on console close:**
  - *Linux:* closing the terminal sends `SIGHUP` to the process; the host converts it to `StopApplication()`, which triggers DI disposal including the `llama-server` child process teardown (no orphan).
  - *Windows:* closing the console window sends `CTRL_CLOSE_EVENT`; the host's `SetConsoleCtrlHandler` converts it to `StopApplication()`. The Job Object assigned to the `llama-server` child guarantees the child is killed even if the drain is cut short by the OS grace window (~5s).

Running the binary **without** this variable (e.g. in Aspire, CI, or headless) leaves behaviour byte-identical to before this feature was added: no browser launch, no loopback override, full HTTPS pipeline.

---

## Single-instance caveat

Run only **one instance** at a time against the same user-data directory. The app stores its SQLite database in `$HOME/.local/share/XE-Local-AI-Engine` (Linux) or `%LOCALAPPDATA%\XE-Local-AI-Engine` (Windows). A second instance will race on the database and may corrupt data. The auto-port selection avoids a listener-port collision but does not protect against database contention.

---

## Native dependency extraction

The single-file binary bundles native libraries (`e_sqlite3` for SQLite and the NSec/libsodium library for encryption). On first launch the .NET runtime extracts them to a per-user temporary directory and loads them from there. This extraction is automatic; no manual step is required.

---

## Appendix — manual `dotnet publish`

Both packagers (Path A and Path B) wrap `dotnet publish` with the right publish profile.
To publish the single-file binary by hand, build the React app first. The MSBuild publish target
fails when `XE-Local-AI-Engine.Client.React/dist/index.html` is absent.

From the repository root:

```sh
(
  cd XE-Local-AI-Engine.Client.React
  pnpm install --frozen-lockfile
  pnpm run build
)
```

### Linux (linux-x64)

```sh
dotnet publish XE-Local-AI-Engine.Client -c Release -r linux-x64 -p:PublishProfile=linux-x64
```

Output lands in `XE-Local-AI-Engine.Client/bin/Release/net10.0/linux-x64/publish/`.
The binary is `XE-Local-AI-Engine.Client` (no extension).

### Windows (win-x64)

```powershell
Push-Location XE-Local-AI-Engine.Client.React
pnpm install --frozen-lockfile
pnpm run build
Pop-Location

dotnet publish XE-Local-AI-Engine.Client -c Release -r win-x64 -p:PublishProfile=win-x64
```

Output lands in `XE-Local-AI-Engine.Client\bin\Release\net10.0\win-x64\publish\`.
The binary is `XE-Local-AI-Engine.Client.exe`.

The publish profiles set `SelfContained=true`, `PublishSingleFile=true`,
`IncludeNativeLibrariesForSelfExtract=true`, `PublishTrimmed=false`. Pass
`-p:UpdateChannel=tester|main` to bake the Velopack update-source repo for that
flavor (default `main`). A raw `dotnet publish` does not inject the GitHub App client
ID and is therefore not a release-ready self-update package; use
`package-tester-win.ps1` for tester RC artifacts.
