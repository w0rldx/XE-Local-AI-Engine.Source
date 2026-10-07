# Velopack release, install, and update guide

> Last reviewed: 2026-10-07 against Velopack 1.2.0, `.github/workflows/release.yml`, `.github/workflows/dev-build.yml`,
> `.github/workflows/package-velopack.yml`, the publish profiles, the app-update channel policy, and the node vault
> (ADR 0018).

XE Local AI Engine distributes official binaries as **Velopack-managed portable applications** for Windows x64 and
Linux x64. There is no OS installer.

This page holds the release-engineering facts: what a release contains, how its integrity is checked, how the update
feeds are laid out, and the signing status. Everything else has one home elsewhere:

| Topic | Owner |
|---|---|
| Installing, updating, switching channels and rolling back, as a user does it | [User guide: Updating](user-guide/docs/updating.md), [Installing on Windows](user-guide/docs/install-windows.md), [Installing on Linux](user-guide/docs/install-linux.md) |
| Version and tag contract, channel semantics, Development builds, the release workflow, local publish | [`publish/README.md`](../publish/README.md) |
| Operator and developer troubleshooting | [Troubleshooting](troubleshooting.md) |

## Release assets

| Platform | User-facing artifact | What the release does not contain |
|---|---|---|
| Windows x64 | `XE-Local-AI-Engine-win-Portable.zip` | Framework-dependent: needs the x64 ASP.NET Core Runtime 10.0.12+ and the WebView2 Evergreen Runtime; no installer, no `Setup.exe` |
| Linux x64 | The `.AppImage` asset | No Linux portable ZIP, DEB, RPM, or install script |

Every official release also publishes:

- `CHECKSUMS.sha256`: SHA-256 checksums generated from the verified remote release bytes.
- `RELEASE-MANIFEST.json`: the release tag, source commit, asset sizes and SHA-256 values, and signing state.
- `RELEASE.spdx.json`: a detached SPDX 2.2 release envelope.

The payloads carry their own SPDX manifest and license disclosures.

## Feed layout

Velopack feed indexes and full/delta packages are published beside the user-facing artifacts. Installed applications
consume those files; users do not open them manually.

| Feed | Published by | Read by |
|---|---|---|
| `releases.win.json`, `releases.linux.json` plus `*-full.nupkg` / `*-delta.nupkg` | Tag releases | Every channel |
| `releases.win-dev.json`, `releases.linux-dev.json` plus their packages | Development prereleases tagged `dev/<version>` only | The Development channel only |
| `RELEASES` | Tag releases, Windows feed only | Velopack's legacy Squirrel-compatible index; no Linux equivalent is published |

The Velopack channel is the OS short name plus an optional `-dev` suffix (`VPK_CHANNEL` in `package-velopack.yml`).
The OS feed is recorded in the package metadata, so no channel lets Windows consume Linux packages or the reverse.
The release workflow uploads the Windows channel first and unmerged so its legacy `RELEASES` index is the one
published, then merges the Linux channel into the same draft. Which update channel reads which feed, and how a build
picks its default channel, is the channel contract in [`publish/README.md`](../publish/README.md#update-channels).

Update checks are anonymous: the release repository is public, and the updater supplies no GitHub access token.

## Integrity contract

The release workflow is tag-bound and fail-closed. The stage-by-stage workflow lives in
[`publish/README.md`](../publish/README.md#release-workflow); the integrity guarantees it provides are:

- the public assets are bound to an immutable tag and its source commit, and a tag, version or commit mismatch is
  rejected;
- matrix jobs build and retain assets but never publish, so the two OS channels cannot race or split into two
  releases;
- the detached evidence (`RELEASE.spdx.json`, `RELEASE-MANIFEST.json`, `CHECKSUMS.sha256`) is derived from the
  **downloaded remote draft bytes**, not from local build output, and the complete draft is re-verified afterwards;
- publication re-verifies the exact prepared draft and publishes it without rebuilding, re-uploading or replacing
  assets, then checks the public release and both OS feeds anonymously.

## Update restart and the vault

The per-user data directory is separate from the application files, so an update replaces neither chats, settings,
models, nor local runtime downloads. On Linux, Velopack replaces the AppImage in place and may need `pkexec` only when
the AppImage's directory is not writable by the current user.

The restart after an update is a fresh, locked start: the app shows the **Unlock this node** page until the admin
password is entered (ADR 0018). A headless install restarted without `XE_ADMIN_PASSWORD` or `--admin-password-stdin`
serves that page and answers `503` on every other local API route until someone unlocks it. A build from before the
node vault cannot read the password-wrapped `node.key` and reports it as corrupt; the user-facing recovery is in
[Updating](user-guide/docs/updating.md#going-back-to-an-older-version).

## Signing status

The release manifest records `signing.state` as `unsigned`. No signing certificate currently exists; acquiring one and
signing future artifacts is planned. Checksums and SPDX documents help users and maintainers verify identity and
contents, but they do not provide publisher authentication equivalent to a trusted code signature. Publication is
gated on a dated, approved unsigned-risk record; that gate documents the accepted interim risk and does not make
unsigned binaries equivalent to signed ones.

What users see as a result (SmartScreen's **Unknown publisher**, browser download warnings, Linux trust prompts) and
how they verify a download is covered in the user guide:
[the SmartScreen warning](user-guide/docs/install-windows.md#the-windows-smartscreen-warning) and
[verifying SHA-256](user-guide/docs/download-from-github.md#step-4--verify-sha-256).

## Historical scripts and releases

Releases through `0.1.0-rc.5.1` used earlier manual and tester-repository flows. The associated
`publish/package-tester-win.ps1` and `publish/package-rc.sh` scripts are retained as deprecated, reference-only
material ([`publish/README.md`](../publish/README.md#legacy-manual-packagers)). They are not fallbacks for the
official workflow, and their authentication, repository, draft-publication, or Linux-ZIP behavior must not be applied
to current releases.
