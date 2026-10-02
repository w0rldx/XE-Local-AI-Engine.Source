# Velopack release, install, and update guide

> Last reviewed: 2026-10-02 against Velopack 1.2.0, `.github/workflows/release.yml`, `.github/workflows/dev-build.yml`, the publish profiles, the app-update channel policy, and the node vault (ADR 0018).

XE Local AI Engine distributes official binaries as **Velopack-managed portable applications** for Windows x64 and
Linux x64. There is no OS installer.

For the maintainer workflow, see [`publish/README.md`](../publish/README.md). For end-user steps, see the
[`docs/user-guide`](user-guide/README.md).

## Release assets

| Platform | Download | What the release does not contain |
|---|---|---|
| Windows x64 | `XE-Local-AI-Engine-win-Portable.zip` | Framework-dependent; ASP.NET Core Runtime 10.0.12+ x64 required; no installer |
| Linux x64 | The `.AppImage` asset | No Linux portable ZIP, DEB, RPM, or install script |

Velopack feed indexes and full/delta packages are published beside the user-facing artifacts. Installed applications
consume those files; users do not open them manually.

Every official release also publishes:

- `CHECKSUMS.sha256`
- `RELEASE-MANIFEST.json`
- `RELEASE.spdx.json`

The payloads carry their own SPDX manifest and license disclosures.

## Windows install

1. Download `XE-Local-AI-Engine-win-Portable.zip` and `CHECKSUMS.sha256` from the same release.
2. Verify the ZIP's SHA-256 value against `CHECKSUMS.sha256`.
3. Extract the ZIP fully to a **writable local directory** such as `%LOCALAPPDATA%\Programs\XE-Local-AI-Engine` or
   `C:\Users\<you>\Apps\XE-Local-AI-Engine`.
4. Run the top-level `XE-Local-AI-Engine.exe` beside the `current` directory. Do not launch the application binary
   inside `current` directly.

The Windows package does not bundle .NET. Install the x64 ASP.NET Core Runtime 10.0.12 or a newer .NET 10 servicing
patch first. The top-level Velopack entry launches the C# apphost in `current`; Microsoft's apphost reports a missing
base runtime, and the launcher reports an absent/outdated ASP.NET Core runtime with the official download URL.

Keep the extracted directory writable. The official portable bundle is Velopack-managed and updates in place. Avoid
`Program Files`, read-only media, network shares, synchronized folders, and running from inside the ZIP preview.

Because the binaries are not yet signed, Windows may show browser reputation warnings and SmartScreen's **Unknown
publisher** prompt. Verify the checksum first, then use **More info → Run anyway** only if you trust the verified
release. Signing is planned when a certificate is available.

## Linux install

1. Download the `.AppImage` asset and `CHECKSUMS.sha256` from the same release.
2. Verify the AppImage's SHA-256 value against `CHECKSUMS.sha256`.
3. Move it to a writable local directory such as `~/Applications`.
4. Mark it executable and run it:

   ```bash
   chmod +x ./XE-Local-AI-Engine*.AppImage
   ./XE-Local-AI-Engine*.AppImage
   ```

The AppImage is the application; do not extract it as a ZIP. Its Velopack updater replaces that AppImage when applying
an update. If the file lives in a privileged directory, Velopack may need `pkexec` to replace it. Keeping it under
your home directory avoids that elevation path.

Linux has no direct SmartScreen equivalent, but a desktop environment, browser, mount policy, or endpoint-security
tool may block a newly downloaded unsigned executable. Verify the checksum, ensure the filesystem allows execution,
and explicitly trust the file only if you accept the current unsigned-binary risk.

## Anonymous updates

Official update checks are anonymous. The release repository is public, so the updater supplies no GitHub access
token. Users do not need a GitHub device login, repository invitation, or GitHub account.

Release selection and platform selection are independent:

| Selector | Values | Purpose |
|---|---|---|
| Update channel (chosen in the app, About page) | Stable, Preview, Development | Stable sees released versions only; Preview also sees release candidates; Development also sees daily Development builds |
| Velopack OS feed | `win`, `linux`; plus `win-dev`, `linux-dev` for Development | Selects packages compatible with the installed operating system |

The channel is node state and is sent with every check. The packaging flavour baked into a build (`main`, `tester`,
`dev`) is internal and only sets the default channel (Stable, Preview, Development) until the user picks one.
Development builds are GitHub prereleases tagged `dev/<version>` that carry only the `-dev` feeds, so Stable and
Preview reads never see them. No channel offers a lower version: moving from Development back to Stable means
installing a Stable artifact manually.

The OS feed is recorded in Velopack package metadata. No channel allows Windows to consume Linux packages or vice
versa.

### Applying an update

- **Windows:** the app downloads and applies the matching Velopack release in the extracted portable directory, then
  restarts through the top-level launcher.
- **Linux:** the app downloads the matching Velopack release and replaces the running AppImage. `pkexec` may be needed
  only when the AppImage's directory is not writable by the current user.

The per-user data directory is separate from the application files, so normal updates do not replace chats, settings,
models, or local runtime downloads.

The restart is a fresh, locked start: the app shows the **Unlock this node** page until the admin password is
entered (ADR 0018). A headless install restarted without `XE_ADMIN_PASSWORD` or `--admin-password-stdin` serves that
page and answers `503` on every other local API route until someone unlocks it in a browser.

## Release integrity and publication

The release workflow is tag-bound and fail-closed:

1. It reuses the full build-and-test workflow on the tagged source.
2. It rejects a tag/version/source-commit mismatch.
3. Windows and Linux matrix jobs build and retain assets but do not publish.
4. One serialized preparation job, protected by the `open-source-release` environment, creates a draft, merges both
   Velopack channels, and downloads the draft assets for remote-byte verification.
5. That job derives the detached SPDX envelope, release manifest, and checksums from those verified remote bytes, then
   verifies the complete draft again.
6. A separately approved protected job verifies release authority and the dated unsigned-risk decision, re-verifies
   the exact prepared draft, and publishes it without replacing assets.
7. The protected job then checks the public release and both feeds anonymously.

This shape prevents matrix publication races and binds the public assets to the immutable tag and source commit.

## Signing status

The release manifest records `signing.state` as `unsigned`. No signing certificate currently exists; acquiring one and
signing future artifacts is planned. Checksums and SPDX documents help users and maintainers verify identity and
contents, but they do not provide publisher authentication equivalent to a trusted code signature.

## Rolling back

Back up the full per-user data directory before running an older application version. Database migrations are
forward-only, so an older binary may not understand data already migrated by a newer one.

Close the app, download and verify the older platform artifact, then run it from a separate writable location. If the
older build cannot open the migrated data, stop it and restore the complete pre-update backup or return to the newer
build. Do not delete or hand-edit `node.sqlite` as a rollback technique.

A build from before the node vault cannot read the password-wrapped `node.key` and refuses to start, calling the key
file corrupt. Do not delete `node.key` in response; restore the complete pre-update backup instead.

## Historical scripts and releases

Releases through `0.1.0-rc.5.1` used earlier manual and tester-repository flows. The associated
`publish/package-tester-win.ps1` and `publish/package-rc.sh` scripts are retained as deprecated, reference-only
material. They are not fallbacks for the official workflow, and their authentication, repository, draft-publication,
or Linux-ZIP behavior must not be applied to current releases.
