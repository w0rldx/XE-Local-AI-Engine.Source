# XE Local AI Engine — distribution & release guide

This directory holds the desktop launcher scripts and the tester-bundle packager
(`package-rc.sh`). This document explains **how the version is set** and **how a
release is created** — both the Velopack self-updating release (primary) and the
manual no-install tester zip.

---

## Versioning — single source of truth

The version lives in **`Directory.Build.props`** as two MSBuild properties:

```xml
<VersionPrefix>0.1.0</VersionPrefix>
<VersionSuffix>rc.1.0</VersionSuffix>
```

These compose to a SemVer string `"<VersionPrefix>-<VersionSuffix>"` — currently
**`0.1.0-rc.1.0`** (a bare `<VersionPrefix>` with no suffix would be a stable
`0.1.0`). Any `-rc` / `-alpha` / `-beta` / `-pre` label marks the build as a
**prerelease**.

This one value propagates everywhere — there is no second place to edit:

| Consumer | How it reads the version |
|---|---|
| **About dialog** (in-app) | Vite injects `VITE_APP_VERSION` at build time via `XE-Local-AI-Engine.Client.React/vite.config.ts` → `resolveAppVersion()`, which reads `Directory.Build.props`. |
| **Tester zip filename** | `publish/package-rc.sh` → `read_version()` reads the two props → `xe-local-ai-engine-<version>-<rid>.zip`. |
| **Velopack package** | `.github/workflows/release.yml` composes `--packVersion` from the same two props (and asserts it is valid SemVer before uploading). |
| **Git tag** | Convention `vX.Y.Z[-suffix]`, e.g. `v0.1.0-rc.1.0` — must match the composed version. |

> **To cut a release: bump `VersionPrefix`/`VersionSuffix` in `Directory.Build.props`
> first, commit, then tag.** Everything downstream picks it up automatically.

---

## Creating a release

There are two distribution paths. The **Velopack release is primary** because it is
the only one that supports in-app self-update; the tester zip is a simpler manual
bundle with no self-update.

### Path A — Velopack release (primary, self-updating)

Tag-triggered CI in `.github/workflows/release.yml`. It publishes per RID, runs
`vpk pack`, and `vpk upload github`, producing the installer
(`Setup.exe` / `.AppImage`), a **portable** build, and **delta** packages — all
sharing one `releases.{rid}.json` feed per repo. Installs made from these artifacts
self-update via the in-app "update available → Update now" flow (GitHub device-flow
auth; see `Plans/2026-06-25-app-self-update-velopack-plan.md`).

**Cut a release:**

```sh
# 1. Bump the version in Directory.Build.props, commit it.
# 2. Tag with the matching vX.Y.Z[-suffix] and push the tag:
git tag v0.1.0-rc.1.0
git push origin v0.1.0-rc.1.0
```

The push of a `v*.*.*` tag triggers the workflow. It can also be run manually via
**workflow_dispatch**, which exposes an `update-channel` input:

- `main` (default) → uploads to the main release repo (`GH_RELEASE_REPO_MAIN`).
- `tester` → uploads to the separate tester repo (`GH_RELEASE_REPO_TESTER`).

A tester build can never upload to the main repo (a runtime guard step aborts if it
resolves there).

**One-time operator setup the workflow depends on** (see the plan §11):

- Repo **variables**: `GH_RELEASE_REPO_MAIN`, `GH_RELEASE_REPO_TESTER` (release-repo URLs; non-secret).
- Repo **secrets**: `GH_RELEASE_TOKEN_MAIN`, `GH_RELEASE_TOKEN_TESTER` (fine-grained PATs, `contents:write` on that one repo only).
- A protected **`release` environment** with at least one required reviewer (gates the upload).
- A GitHub App for the in-app device-flow update auth (`contents:read`, repo-scoped).

> Builds are currently **unsigned** (signing is a deferred hard gate before any
> non-tester rollout). During the unsigned window the supply-chain controls in
> `release.yml`'s header comment (tag protection, least-priv tokens, environment
> reviewers) are required.

### Path B — Tester zip (manual, no install, no self-update)

`publish/package-rc.sh` builds a self-contained zip an external tester just unzips
and runs — no installer, no Docker, no prerequisites. The version is read from
`Directory.Build.props` and baked into the filename.

```sh
publish/package-rc.sh                 # both win-x64 and linux-x64
publish/package-rc.sh --rid win-x64   # one RID
publish/package-rc.sh --skip-web      # reuse the existing React dist (skip pnpm build)
```

Output (git-ignored): `publish/dist/xe-local-ai-engine-<version>-<rid>.zip` plus a
`.sha256` sidecar. The script builds the React SPA, publishes the single-file
self-contained binary, stages it with the launcher + `READ-ME-FIRST.txt`, refuses to
ship if any per-node runtime/state file leaked in, then zips and checksums it.

> The win-x64 bundle is cross-built on Linux. **It must be smoke-tested on a real
> Windows machine before tagging an RC** — native-library self-extract, console-close
> no-orphan, and browser auto-open cannot be verified off-Windows.

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

`package-rc.sh` (Path B) and the Velopack workflow (Path A) both wrap `dotnet publish`
with the right publish profile. To publish the single-file binary by hand:

### Linux (linux-x64)

```sh
dotnet publish XE-Local-AI-Engine.Client -c Release -r linux-x64 -p:PublishProfile=linux-x64
```

Output lands in `XE-Local-AI-Engine.Client/bin/Release/net10.0/linux-x64/publish/`.
The binary is `XE-Local-AI-Engine.Client` (no extension).

### Windows (win-x64)

```powershell
dotnet publish XE-Local-AI-Engine.Client -c Release -r win-x64 -p:PublishProfile=win-x64
```

Output lands in `XE-Local-AI-Engine.Client\bin\Release\net10.0\win-x64\publish\`.
The binary is `XE-Local-AI-Engine.Client.exe`.

The publish profiles set `SelfContained=true`, `PublishSingleFile=true`,
`IncludeNativeLibrariesForSelfExtract=true`, `PublishTrimmed=false`. Pass
`-p:UpdateChannel=tester|main` to bake the Velopack update-source repo for that
flavor (default `main`).
