# Velopack release, install & update guide

> Last reviewed: 2026-07-24 · Grounded against `publish/package-tester-win.ps1`, `publish/package-rc.sh`,
> `XE-Local-AI-Engine.Client/Hosting/DesktopBootstrap.cs`, and the live tester releases.

How a release is actually cut, what a tester actually downloads, and how in-app updates work. If you are looking for
the flag-by-flag packager reference, that lives in [`publish/README.md`](../publish/README.md); this page covers the
distribution and update story around it.

---

## 1. The shape of a release, in one table

| | |
|---|---|
| **Who builds it** | a maintainer, by hand, on **Windows** |
| **What builds it** | `publish/package-tester-win.ps1` — the canonical and only release path |
| **Source repo** (code + `v` tags) | `w0rldx/XE-Local-AI-Engine` |
| **Artifact repo** (releases) | `w0rldx/XE-Local-AI-Engine.Tester-App` |
| **Velopack channel** | `win` |
| **What a tester downloads** | `XE-Local-AI-Engine-win-Portable.zip` |
| **Installer** | **none** — `vpk pack` runs with `--noInst` |

> **GitHub Actions builds nothing.** `.github/workflows/release.yml` is `disabled_manually` and its only three runs
> all failed on 2026-06-27. `build-and-test.yml` is likewise disabled; `e2e.yml` was never registered. Six runs, six
> failures, zero successes, ever. Pushing a tag does **not** produce a release. See §6.

### The two-repo split

Source and artifacts live in different repositories that share only a version string:

- The **`v<version>` git tag goes on HEAD of the source repo** (`w0rldx/XE-Local-AI-Engine`). The packager refuses to
  upload unless HEAD carries that exact tag.
- `vpk upload github` then creates a same-named release on the **tester repo**
  (`w0rldx/XE-Local-AI-Engine.Tester-App`), whose commit history is unrelated.

So a tester release has no tag in the source repo, and the source tag never appears on the tester repo. Both are
expected — do not "fix" one to match the other.

**Tag-form convention changed mid-flight.** The seven tester releases published 2026-06-26 → 2026-07-07 carry a
**bare** tag (`0.1.0-rc.4.1`) with a `v`-prefixed release *name*. The packager now uploads with `--tag v<version>`, so
releases from `0.1.0-rc.4.2` onward are v-prefixed on both. Anything looking up an existing tester release must accept
**both** forms — the script's `Find-GitHubRelease` probes `v<version>`, then `<version>`, then falls back to matching
the release *name*. A lookup that probes one form only would make the already-published guard blind to the live
release and let `vpk upload --merge` push untested assets into a shipped update feed.

### Cautionary tale: how `0.1.0-rc.4.1` got burned

`0.1.0-rc.4.1` is **published on the tester repo and has no tag in the source repo.** Reconciling the two sides:

| | Source repo tags | Tester repo releases |
|---|---|---|
| `0.1.0-rc.1.0` … `0.1.0-rc.4.0` | 6 tags, all `v`-prefixed | 6 releases, matched 1:1 |
| `0.1.0-rc.4.1` | **none** | published 2026-07-07 |

So there is no commit in this repository that identifies what shipped as rc.4.1. The version string is **burned** — it
can never be reused, because the packager's already-published guard will refuse to upload over a live release (and
correctly so: `vpk --merge` into a shipped feed would hand testers assets nobody smoke-tested). The next release is
`0.1.0-rc.4.2`.

This is the exact failure mode the current gates exist to close, and it is worth understanding rather than just
working around:

- **HEAD must carry the exact `v<version>` tag before upload.** Without that, a release can be published from an
  unidentifiable working tree — which is how rc.4.1 happened.
- **The already-published guard resolves a release by both tag spellings and by name.** A guard that probed only
  `v<version>` would have missed every bare-tagged release, including the live one.
- **Never reuse a version string.** Bump instead. Reconstructing rc.4.1's contents afterwards required reading
  `git log` between `v0.1.0-rc.4.0` and the version-bump commit (`2d8a4ed0`) — a guess dressed up as a record, and
  the reason `CHANGELOG.md` flags that section as reconstructed.

---

## 2. What a tester actually downloads

A published tester release carries exactly these assets:

| Asset | What it is |
|---|---|
| `XE-Local-AI-Engine-win-Portable.zip` | **the download** — the portable Velopack bundle |
| `XE-Local-AI-Engine-<version>-full.nupkg` | full package, consumed by the updater |
| `XE-Local-AI-Engine-<version>-delta.nupkg` | delta package, consumed by the updater |
| `releases.win.json` | the update feed for the `win` channel |
| `RELEASES` | legacy feed index |

Inside the bundle, `LICENSE` and `NOTICE` ship next to the executable — wired in as `Content`
items in `XE-Local-AI-Engine.Client.csproj`, so they land in the publish directory that **both**
packaging paths stage from. The software is **proprietary, all rights reserved**; `NOTICE` carries the
third-party attributions, and `READ-ME-FIRST.txt` states the proprietary terms and points at `LICENSE`.

**There is no `Setup.exe` and no `.AppImage`.** `vpk pack` is invoked with `--noInst`, which suppresses installer
generation entirely; the portable zip is the shipped flavor. Any instruction telling a tester to download
`XE-Local-AI-Engine-win-x64-Setup.exe` or an `.AppImage` is describing artifacts nothing in this repo builds.

**There is no Linux tester release.** The Velopack channel is `win`, and the packager publishes `win-x64` only. Linux
users build a portable zip locally with `publish/package-rc.sh --rid linux-x64`; it is not published anywhere and does
not self-update (§5).

---

## 3. First install (tester onboarding)

**Why device-flow does not gate the first install.** The in-app GitHub device-flow authenticates the user so the
update checker can reach the private tester repo — but it only runs *after* the app is installed. The bundle itself
lives in that same private repo, so the tester needs the download before the app can ask for auth.

**How a tester gets the first bundle.** A tester who is already a collaborator on
`w0rldx/XE-Local-AI-Engine.Tester-App` downloads it through their own GitHub browser session:

1. Open `https://github.com/w0rldx/XE-Local-AI-Engine.Tester-App/releases` and pick the latest release.
2. Download `XE-Local-AI-Engine-win-Portable.zip`.
3. Unzip it anywhere and run the packaged `XE-Local-AI-Engine` executable. No auth token is needed at this point, and
   there is nothing to install — a Velopack-managed portable bundle enters desktop mode on its own
   (`VelopackInstall.IsManaged()`), so no launcher script is required.
4. On first launch, sign in via the in-app GitHub device-flow so the update checker can reach the tester repo for
   subsequent updates.

After that, update checks and downloads happen entirely in-app.

**Operator note:** for very early private testing, a single one-time shared download link (GitHub's temporary asset
URL via the API, or a short-lived signed URL) is an acceptable alternative to granting collaborator access purely for
the download step.

Tester-facing run, reset, and troubleshooting instructions live in
[`publish/TESTER-QUICKSTART.md`](../publish/TESTER-QUICKSTART.md).

---

## 4. Where user data lives

The per-user data directory is resolved from `Environment.SpecialFolder.LocalApplicationData`
(`DesktopBootstrap.cs:18-19`):

| OS | Path |
|---|---|
| Windows | `%LOCALAPPDATA%\XE-Local-AI-Engine` |
| Linux | `$XDG_DATA_HOME/XE-Local-AI-Engine`, defaulting to `~/.local/share/XE-Local-AI-Engine` |

This one directory holds everything: `node.sqlite`, `node.key`, `node-settings.json`, `hf-token.enc`, the downloaded
llama.cpp / stable-diffusion.cpp binaries, the GGUF and image models, and the AgentHome workspace. Deleting it is a
full reset; the app stays installed.

It is **independent of the versioned app binaries**, so an update never touches it — which is what makes the rollback
options in §7 possible at all, subject to the schema caveat there.

---

## 5. In-app self-update, and which builds get it

Self-update is wired by `AddAppUpdateExtensions.cs` (Velopack updater + GitHub device-flow auth) and is **desktop-only**,
gated like every other desktop branch. Update-feed config lives in `appsettings.AppUpdate.{main,tester}.json`, selected
at publish time by `-p:UpdateChannel=tester|main` (default `main`).

**Which builds self-update:**

| Build | Self-updates? |
|---|---|
| Velopack portable bundle from `package-tester-win.ps1` | **yes** |
| Zip from `package-rc.sh` | **no**, for two independent reasons — no Velopack metadata at all, *and* an explicit `-p:UpdateChannel=main` (the inert channel) that the script asserts on every build |
| Raw `dotnet publish` output run directly | **no** — no client ID is injected, so the updater is unconfigured |
| `-SkipUpload` rehearsal package with no client ID | **no** — deliberately inert, and stamped (below) |

### The channel files are deliberately asymmetric

| File | `GitHubRepositoryUrl` | `GitHubAppClientId` | Status |
|---|---|---|---|
| `appsettings.AppUpdate.tester.json` | `https://github.com/w0rldx/XE-Local-AI-Engine.Tester-App` — **real, intentional, non-secret** | **empty**, injected at packaging time | live |
| `appsettings.AppUpdate.main.json` | `REPLACE_*` placeholder | `REPLACE_*` placeholder | **intentionally inert** |

The `main` channel keeps its placeholders **on purpose**: distribution is tester-only today, and leaving `main`
unwired is an owner decision, not an oversight. `AppUpdateChannelOptions.IsConfigured` stays false, so the in-app
updater ships honestly disabled rather than placeholder-configured.

> **Do not "redact" the tester repository URL back to a placeholder.** It is public configuration, not a leaked
> secret, and blanking it silently breaks self-update for every installed tester build. Only the **client ID** is a
> packaging-time injection, and none is committed here.

### `package-rc.sh` proves its own inertness

The shell packager publishes with an **explicit** `-p:UpdateChannel=main` — deliberate and enforced, rather than
silently inheriting the csproj default — and then `assert_app_config_sane` *proves* the staged config reads as inert.
If the bundle would ship a live channel (a real repo URL plus a real `Iv…` client ID) it **fails the build** with "A
portable zip is not a Velopack installation and must never self-update." It also rejects `REPLACE_`/`CHANGE_ME`
placeholders in every *other* `appsettings*.json`, allowing them only in `appsettings.AppUpdate.json` and only because
the inertness check has already proven they leave the feature off.

### Rehearsal packages are stamped

`package-tester-win.ps1 -SkipUpload` is a pre-tag packaging rehearsal. Every build and test gate still runs; it
relaxes exactly one thing, the client ID. A **supplied** ID is always validated, rehearsal or not — a placeholder or a
non-`Iv…` value (the numeric App ID is the usual paste-o) is an error, not an absence. An **absent** ID is tolerated
only under `-SkipUpload`, and uploading always requires a real `Iv…` ID.

A rehearsal with no ID bakes **no** client ID at all, so `AppUpdateChannelOptions.IsConfigured` stays false and the
updater ships inert rather than placeholder-configured. The publish dir is stamped with `REHEARSAL-DO-NOT-SHIP.txt`,
so the marker rides *inside* the Portable.zip, plus warnings at start and finish. Because `dotnet publish` does not
clean the publish dir, a real run deletes a stale marker left by an earlier rehearsal. `-SkipUpload` **with** a real ID
produces a normal shippable artifact and no marker.

---

## 6. GitHub Actions: dormant, not the release mechanism

`.github/workflows/release.yml` describes a tag-triggered, channel-selectable Velopack release with per-repo
least-privilege tokens, a runtime guard that stops a tester build reaching the main repo, and a protected `release`
environment. **None of it has ever run successfully.**

| Workflow | Registered state | Runs |
|---|---|---|
| `build-and-test.yml` | `disabled_manually` | 3, all failed, last 2026-04-20 |
| `release.yml` | `disabled_manually` | 3, all failed, all 2026-06-27, each dead in ~40 s |
| `e2e.yml` | not a registered workflow | never ran |

Verified 2026-07-24 with `gh workflow list --all` and `gh run list`. Read `release.yml` for design intent if the
workflows are ever revived — the supply-chain controls in its header comment (tag protection, least-privilege tokens,
environment reviewers) are the part worth keeping. Until then, the operator setup it describes (repo variables
`GH_RELEASE_REPO_MAIN` / `GH_RELEASE_REPO_TESTER`, secrets `GH_RELEASE_TOKEN_MAIN` / `GH_RELEASE_TOKEN_TESTER`, the
protected `release` environment) is **not** a prerequisite for shipping. The real credential set is two environment
variables on the packaging machine: `VPK_TOKEN` and `XE_TESTER_GITHUB_APP_CLIENT_ID`.

> Builds are **unsigned**. Signing remains a hard gate before any non-tester rollout.

---

## 7. Rolling back a bad update

The per-user data directory (§4) is independent of the versioned binaries, but an older binary is not guaranteed to
understand a database migrated by a newer one. **Back up the whole data directory before attempting a rollback.**

**Option A — previous version still on disk (Velopack-managed).** Velopack keeps each version in its own
`app-<version>` folder under the managed install root and points a `current` reference at the active one; the
immediately previous version's folder is normally still present after an update. Close the app and launch the previous
`app-<version>` executable directly. This is a manual pin — the updater may re-offer the newer version on the next
check.

**Option B — re-install an older release asset.** Download the older release's `XE-Local-AI-Engine-win-Portable.zip`
from the tester repo and run it. Because the data dir is separate, chats and models carry over.

**Option C — a `package-rc.sh` zip.** It has no self-update, so it will never pull itself forward; unzip and run it
against the same data directory, subject to the schema caveat below.

**Caveat — the database schema is forward-only.** EF Core migrations run on first launch of a newer build and are
**not** reverted when an older build starts. Some releases include data repairs or destructive schema cleanup, so a
binary rollback does not imply database compatibility. If the older build cannot use the migrated database, stop it
and either restore the complete pre-update data-directory backup or return to the newer binary. Do not delete
`node.sqlite` as a rollback step, and do not hand-edit the database — a deliberate reset is data loss and belongs only
in the explicit reset procedures ([`troubleshooting.md`](troubleshooting.md#reset-the-database-start-clean) for
maintainers, [`publish/TESTER-QUICKSTART.md`](../publish/TESTER-QUICKSTART.md) for testers).

---

## 8. Uninstalling

There is **no installer**, so there is nothing to uninstall in the OS sense for a portable bundle — but two scripts
ship next to the launchers to tear down the runtime state:

- `publish/windows/uninstall-xe-local-ai-engine.ps1` (PowerShell 5.1-compatible)
- `publish/linux/uninstall-xe-local-ai-engine.sh` (POSIX `sh`)

Both stop the node process and the `llama-server` / `sd-server` children it spawned — matched strictly by executable
path under the app's own data dir, so an unrelated `llama-server` (Ollama's, say) is never touched — then branch:

- **Velopack-managed install detected**: the script does **not** delete the tree. It delegates to Velopack (on Windows
  it can best-effort invoke `Update.exe --uninstall`; otherwise it points at "Apps & features") and stops.
- **Portable / manual install**: after an explicit typed confirmation (`--yes` / `-Yes` skips it), it deletes **only**
  the per-user data directory from §4.

Both refuse to run elevated or as root (a per-user data dir would resolve to the wrong profile) and support
`--dry-run` and `--keep-data`. They never touch anything outside that one directory, and they never remove the
application binaries — portable-zip users delete the unzipped folder by hand afterward.

> **There was never an `installer/install.ps1` or `installer/install.sh`.** The repo once carried a .NET installer CLI
> project at `tools/installer/`, deleted on 2026-06-19 (before the first RC tag). No install script has ever been
> tracked in this repository, and there is no `installer/` directory. Do not write a retirement plan for scripts that
> do not exist.

---

## Related

- [`publish/README.md`](../publish/README.md) — versioning, the three packaging paths, and the packager's flags
- [`publish/TESTER-QUICKSTART.md`](../publish/TESTER-QUICKSTART.md) — tester-facing run / reset instructions
- [`docs/wiki/11-hosting-and-deployment.md`](wiki/11-hosting-and-deployment.md) — publish profiles, launchers, desktop mode
- [`docs/wiki/13-testing-and-validation.md`](wiki/13-testing-and-validation.md) — where the quality gates actually run
