# Updating to a new build

Official Windows and Linux downloads are Velopack-managed portable applications. Both can update themselves from the
public GitHub release feed.

Update checks are **anonymous**. You do not need a GitHub account, device-code sign-in, access token, or repository
invitation.

## Release tracks and operating-system channels

Two independent settings select an update:

- **Main flavor:** follows stable releases.
- **Tester flavor:** follows stable releases and release candidates.
- **Windows/Linux channel:** Velopack automatically selects packages for the installed operating system.

Choosing the tester flavor does not change the operating-system channel.

## Update inside the app

1. Open the update section in the app.
2. Check for updates.
3. Download and apply the offered version.
4. Allow the app to restart when prompted.

On Windows, Velopack updates the extracted portable application and restarts through the top-level launcher.

On Linux, Velopack replaces the AppImage itself. If the AppImage is in a directory your user cannot write, the update
may require `pkexec`. Keeping it in `~/Applications` normally avoids elevation.

<details>
<summary><b>The updater says this build is not managed</b></summary>

Self-update works only from an official Velopack artifact:

- Windows: the extracted `XE-Local-AI-Engine-win-Portable.zip` bundle, launched through its top-level
  `XE-Local-AI-Engine.exe`.
- Linux: the Velopack `.AppImage`.

A raw `dotnet publish`, source build, or deprecated manual ZIP has no official Velopack installation metadata. Update
that build by replacing it manually with a verified official artifact.

</details>

## Manual replacement

Manual replacement remains available if the in-app updater cannot run.

### Windows

1. Stop the app by closing its console window.
2. Download the new `XE-Local-AI-Engine-win-Portable.zip` and `CHECKSUMS.sha256` from the
   [Releases page](https://github.com/w0rldx/XE-Local-AI-Engine.Source/releases).
3. Verify the ZIP's SHA-256 value.
4. Extract it fully to a new writable local directory.
5. Run the top-level `XE-Local-AI-Engine.exe` beside the `current` directory.

Do not overwrite files while the old version is running.

### Linux

1. Stop the app with `Ctrl+C` or by closing its terminal.
2. Download the new `.AppImage` and `CHECKSUMS.sha256` from the same release.
3. Verify the AppImage's SHA-256 value.
4. Move it to a writable local directory, run `chmod +x`, and start it.

See the full [Linux installation guide](install-linux.md).

## Your data

Application updates do not replace the separate per-user data directory. Chats, settings, downloaded models, and
managed runtimes carry forward.

Keep backups of data you care about. A binary update does not replace a backup policy.

## Going back to an older version

Back up the complete data directory first. Database migrations are forward-only, and an older binary may not
understand a database already migrated by a newer one.

Then download and verify the older platform artifact and run it from a separate location. If it cannot open the
migrated data, stop it and restore the complete pre-update backup or return to the newer version. Do not delete
`node.sqlite` as a downgrade technique.

## Signing warnings after an update

Current release artifacts are unsigned because no signing certificate exists yet. Signing is planned. Windows may
show SmartScreen again for new bytes, and Linux security tools may require you to trust a newly downloaded AppImage.
Verify the new artifact against `CHECKSUMS.sha256` before running it.

## Problems with an update

When reporting a regression, include the version that worked, the version that failed, the operating system, and the
error shown in the console.

See [Giving feedback](feedback.md).

**[← Back to the main page](../README.md)**
