# Velopack release, install & update guide

> Last reviewed: 2026-07-24 · Grounded against `publish/package-tester-win.ps1`, `publish/package-rc.sh`,
> `XE-Local-AI-Engine.Client/Hosting/DesktopBootstrap.cs`, and the live tester releases.

How a release is actually cut, what a tester actually downloads, and how in-app updates work. If you are looking for
the flag-by-flag packager reference, that lives in [`publish/README.md`](../publish/README.md); this page covers the
distribution and update story around it.

> **Consolidated.** Source lives in `w0rldx/XE-Local-AI-Engine.Source` (public), the single home for source **and**
> releases. Both in-app update channels point at `.Source` (see "The update channel files" below), and the intended
> release path is the tag-triggered **[`.github/workflows/release.yml`](../.github/workflows/release.yml)** (§6):
> pushing a `v*` tag builds win-x64 + linux-x64 Velopack packages and publishes them to **this repo's** GitHub
> Releases using the built-in `GITHUB_TOKEN` — no separate artifact repo, no PAT. GitHub Actions must be enabled on
> the repository for it to run; that is an owner-level setting this page does not track.
>
> Most of the rest of this page — `package-tester-win.ps1`, `package-rc.sh`, `vpk upload github` against the tester
> repo, and the two-repo mechanics below — is now **deprecated, reference-only material**. Both manual packagers
> carry deprecation headers and still target the retired `w0rldx/XE-Local-AI-Engine.Tester-App` repo; they are kept
> here as the historical record of how tester RCs through `0.1.0-rc.5.0` were actually cut, and as a manual rehearsal
> path, not as the release mechanism.

---

## 1. The shape of a release, in one table

**This table describes the deprecated manual packager flow**, kept as historical/reference material. The intended
release path is the tag-triggered `.github/workflows/release.yml` (§6), which builds win-x64 + linux-x64 and
publishes to this repo's GitHub Releases.

| | |
|---|---|
| **Who built it (manual flow)** | a maintainer, by hand, on **Windows** |
| **What built it (manual flow)** | `publish/package-tester-win.ps1` — **deprecated**, reference-only |
| **Source repo** (code + `v` tags) | `w0rldx/XE-Local-AI-Engine.Source` |
| **Artifact repo (manual flow)** | `w0rldx/XE-Local-AI-Engine.Tester-App` (retired) |
| **Velopack channel (manual flow)** | `win` |
| **What a tester downloaded (manual flow)** | `XE-Local-AI-Engine-win-Portable.zip` |
| **Installer** | **none** — `vpk pack` runs with `--noInst` |

> **`release.yml` is the intended release path.** It is a tag-triggered, channel-selectable Velopack workflow that
> builds win-x64 **and** linux-x64 and publishes both to **this repo's** GitHub Releases with the built-in
> `GITHUB_TOKEN`. GitHub Actions must be enabled on the repository for it to run — that is an owner-level repository
> setting this page does not track. See §6 for the workflow's design and its validation binding to
> `build-and-test.yml`.

### The two-repo split (historical — pre-consolidation)

This subsection and the cautionary tale below describe how releases through `0.1.0-rc.5.0` were actually cut, back
when the manual packager uploaded to a separate tester repo. `release.yml` (§6) publishes to this single repo, so a
future CI-cut release has no tester-repo counterpart to reconcile against. Source and artifacts historically lived in
different repositories that shared only a version string:

- The **`v<version>` git tag goes on HEAD of the source repo** (`w0rldx/XE-Local-AI-Engine`). The packager refuses to
  upload unless HEAD carries that exact tag.
- `vpk upload github` then creates a same-named release on the **tester repo**
  (`w0rldx/XE-Local-AI-Engine.Tester-App`), whose commit history is unrelated.

So a tester release has no tag in the source repo, and the source tag never appears on the tester repo. Both are
expected — do not "fix" one to match the other.

**Tag-form convention changed mid-flight.** The seven tester releases published 2026-06-26 → 2026-07-07 carry a
**bare** tag (`0.1.0-rc.4.1`) with a `v`-prefixed release *name*. The packager now uploads with `--tag v<version>`, so
releases from `0.1.0-rc.5.0` onward are v-prefixed on both (rc.4.2 was never cut, so rc.5.0 is the first such release). Anything looking up an existing tester release must accept
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
correctly so: `vpk --merge` into a shipped feed would hand testers assets nobody smoke-tested). The release that
followed was `0.1.0-rc.5.0` (tagged `v0.1.0-rc.5.0`, published to the tester repo on 2026-08-04); `0.1.0-rc.4.2` was
never cut — the version target moved straight from the burned `0.1.0-rc.4.1` to `0.1.0-rc.5.0`.

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

Inside the bundle, `LICENSE` and `NOTICE` ship inside the `current` folder — wired in as `Content`
items in `XE-Local-AI-Engine.Client.csproj`, so they land in the publish directory that **both**
packaging paths stage from. The software is licensed under **Apache-2.0**; `LICENSE` states
the terms and `NOTICE` carries the third-party attributions. There is no `READ-ME-FIRST.txt` in this
bundle — that file is generated only by the separate `publish/package-rc.sh` Linux/manual packaging
path, not by the Velopack `vpk pack` path this guide describes.

**There is no `Setup.exe` and no `.AppImage`.** `vpk pack` is invoked with `--noInst`, which suppresses installer
generation entirely; the portable zip is the shipped flavor. Any instruction telling a tester to download
`XE-Local-AI-Engine-win-x64-Setup.exe` or an `.AppImage` is describing artifacts nothing in this repo builds.

**There is no Linux tester release.** The Velopack channel is `win`, and the packager publishes `win-x64` only. Linux
users build a portable zip locally with `publish/package-rc.sh --rid linux-x64`; it is not published anywhere and does
not self-update (§5).

---

## 3. First install (tester onboarding)

**This section describes the historical, pre-consolidation flow** (manual packager uploading to the now-retired
tester repo). Releases published via `release.yml` land on this repo's public GitHub Releases page instead, so a
tester downloads from there directly with no collaborator access or device-flow needed for the download step itself.

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

### The update channel files

| File | `GitHubRepositoryUrl` | `GitHubAppClientId` | Status |
|---|---|---|---|
| `appsettings.AppUpdate.tester.json` | `https://github.com/w0rldx/XE-Local-AI-Engine.Source` — **real, intentional, non-secret** | **empty** (public repo needs no app auth) | live |
| `appsettings.AppUpdate.main.json` | `https://github.com/w0rldx/XE-Local-AI-Engine.Source` — **real, intentional, non-secret** | **empty** | live |

Both channels now point at the public source repo `.Source` (the `main` channel was previously inert with `REPLACE_*`
placeholders — it is now wired as part of the consolidation). Because `.Source` is public, `AppUpdateChannelOptions`
resolves configured with no GitHub App client ID, and none is committed.

> **Do not "redact" these repository URLs back to placeholders.** They are public configuration, not leaked secrets,
> and blanking them silently breaks self-update for installed builds.

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

## 6. GitHub Actions: the intended release path

`.github/workflows/release.yml` is the intended, tag-triggered release mechanism. It publishes to **this repo**
(`w0rldx/XE-Local-AI-Engine.Source`, `github.repository`) using the built-in `GITHUB_TOKEN` — no PAT, no separate
per-repo tokens or artifact repo. Triggers: a pushed `v*` tag (the normal path), or `workflow_dispatch` as a manual
fallback that lets you pick the tag/ref from the Actions UI.

**GitHub Actions must be enabled on the repository for this workflow to run.** Whether it currently is enabled is an
owner-level repository setting, not something this documentation tracks or asserts.

Job shape:

- **`validate`** — calls `build-and-test.yml` as a local reusable workflow so the exact tagged commit re-runs the full
  build + backend/frontend gates before anything is packed. `version` and `release` both `needs: validate`, so a
  failing gate blocks the release (fail-closed).
- **`version`** — composes the pack version from `Directory.Build.props`, asserts it is valid SemVer, and generates
  the changelog once with a **checksum-pinned git-cliff** (shared across the RID matrix rather than regenerated per
  leg).
- **`release`** — a matrix with one leg per RID (`win-x64` on `windows-latest`, `linux-x64` on `ubuntu-latest`; the
  Velopack channels are `win` and `linux`). Each leg builds the SPA, runs `dotnet publish` with
  `-p:UpdateChannel=main`, downloads the previous release for that channel (for Velopack delta packages), `vpk pack`s
  an installer-less portable + delta bundle, and `vpk upload github`s it to this repo's Releases — `--pre` is applied
  only on the upload step when the composed version carries an `-rc`/`-alpha`/`-beta`/`-pre` suffix, never on `vpk
  pack` (which has no `--pre` flag in 1.2.0).

Every action in the workflow is pinned to a full commit SHA (with the version tag in a trailing comment), so a moved
upstream tag cannot swap an action mid-release.

> Builds are **unsigned**. A signing follow-up (win-x64 via `--signParams`, linux-x64 via `--signEntitlements`) is
> deferred per "bind + pin now, sign later" — see the workflow's header comment. Signing remains a hard gate before
> any non-tester rollout.

The deprecated manual packagers (`publish/package-tester-win.ps1`, `publish/package-rc.sh`) remain useful as a local
rehearsal or static-analysis target (`scripts/lint-release-scripts.sh`), but they are not the release mechanism.

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
— from this repo's GitHub Releases for a CI-cut release, or from the tester repo for a release published under the
historical manual flow — and run it. Because the data dir is separate, chats and models carry over.

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
