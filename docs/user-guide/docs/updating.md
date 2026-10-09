# Updating to a new build

Official Windows and Linux downloads are Velopack-managed portable applications. Both can update themselves from the
public GitHub release feed.

Update checks are **anonymous**. You do not need a GitHub account, device-code sign-in, access token, or repository
invitation.

## Choose an update channel

On the **About** page, **Update channel** decides which builds the app offers you:

- **Stable — Recommended:** released versions only. The right choice for everyday use.
- **Preview:** release candidates as well as released versions. Newer, and tested less.
- **Development:** daily builds from the development branch, on top of everything Preview offers. Less stable
  than Preview and not tested for release — expect defects.

Changing the channel only changes what the app looks for; nothing is installed until you choose to update. The
app never offers an older version than the one you run, so leaving **Development** for **Stable** means installing
a Stable build manually (see [Manual replacement](#manual-replacement)).

Until you pick one, the app follows the channel its download was built for. Windows or Linux packages are
selected automatically for the installed operating system; the channel does not change that.

## Update inside the app

1. Open the **About** page and find **Updates**.
2. Click **Check for updates**.
3. If a version is offered, click **Update now**.
4. The app shows **Restarting…** and comes back on the new version.

On Windows, Velopack updates the extracted portable application and restarts through the top-level launcher.

On Linux, Velopack replaces the AppImage itself. If the AppImage is in a directory your user cannot write, the update
may require `pkexec`. Keeping it in `~/Applications` normally avoids elevation.

After the restart the app shows **"Unlock this node"**, as on every start: enter your admin password to continue.
Updating from a build that did not yet have the unlock page? Once you are signed in, the app asks you to confirm
your password once (**"Protect this node's data"**) and then shows your one-time **recovery code**. Save it — see
[Your first run](first-run.md#save-your-recovery-code).

<details>
<summary><b>The app says automatic updates aren't available in this build</b></summary>

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

1. Stop the app with **Quit XE** in the tray menu.
2. Download the new `XE-Local-AI-Engine-win-Portable.zip` and `CHECKSUMS.sha256` from the
   [Releases page](https://github.com/w0rldx/XE-Local-AI-Engine.Source/releases).
3. Verify the ZIP's SHA-256 value. → [How](download-from-github.md#step-4--verify-sha-256)
4. Extract it fully to a new writable local directory.
5. Run the top-level `XE-Local-AI-Engine.exe` beside the `current` directory.

Do not overwrite files while the old version is running.

### Linux

1. Stop the app by closing its window and confirming that you want to quit.
2. Download the new `.AppImage` and `CHECKSUMS.sha256` from the same release.
3. Verify the AppImage's SHA-256 value.
4. Move it to a writable local directory, run `chmod +x`, and start it.

See the full [Linux installation guide](install-linux.md).

## Your data

Application updates do not replace the separate per-user data directory. Chats, settings, downloaded models, and
managed runtimes carry forward.

Keep backups of data you care about. A binary update does not replace a backup policy.

### Restoring the automatic pre-update snapshot

When an update brings database changes, the app first copies `node.sqlite` to
`<data-directory>/backups/node-chat-<timestamp>.sqlite` and keeps the newest few copies. The copy is skipped, with a
warning in the log, when the disk has too little free space or the copy takes too long. If the app then refuses to start
because its database could not be updated, or because the database is damaged, the error names the newest snapshot. To
restore it:

1. Close the app completely, including the tray icon.
2. Rename `node.sqlite` in the data directory to `node.sqlite.broken`. Delete `node.sqlite-wal` and `node.sqlite-shm`
   if they exist.
3. Copy the newest `backups/node-chat-<timestamp>.sqlite` to the data directory and rename it to `node.sqlite`.
4. Start the app again. Keep `node.key` where it is: the snapshot needs the same key.

Anything you did after the snapshot was taken is not in it. If the update itself is the problem, the restored database
is migrated again on the next start, so report the failure before retrying.

## Going back to an older version

Back up the complete data directory first. Database migrations are normally forward-only, and an older binary may not
understand a database already migrated by a newer one.

### Restoring a database snapshot

The node keeps snapshots of its database in the `backups` folder of the data directory. One is taken automatically
before an update changes the database, and you can take one at any time. Both live on the **Diagnostics** page, in the
**Database snapshots** card. The card lists every snapshot with its size and time, and says whether this start took an
automatic backup (and why not, when it was skipped or failed).

- **Take a snapshot:** click **Take snapshot**. If another backup is running or the disk is too full, the card says so.
- **Restore a snapshot:** click **Restore** on its row and confirm. The node checks the file first and refuses one that
  is damaged or comes from a newer version of the app. While a backup is running, try again once it has finished.

After you confirm, the node stops and does not start again by itself; on the desktop app the window closes. Start the
app again: before it opens anything, it puts the snapshot in place of `node.sqlite` and keeps the database it replaced
next to it as `node.sqlite.prerestore-<time>`.

Keep in mind:

- A snapshot holds the **database only**. `node.key`, `node-settings.json`, the encrypted credential files and files
  stored on disk (images, uploads, models) are not part of it and are not changed by a restore.
- A snapshot opens only with the **same `node.key`** that was in use when it was taken.
- Everything written after the snapshot was taken is not in the restored database; it stays in the `prerestore` copy.
- If the restore cannot be applied, the app does not start (exit code 9) and says why. Fix the cause and start it
  again, or delete `backups/restore-pending.json` in the data directory to cancel the restore.

For scripted use, an administrator can do the same through the node's local API: `GET` and `POST node/backups` list
and take snapshots, and `POST node/backups/<name>/restore` restores one.

### Knowledge collection downgrade preflight

Builds that include knowledge collections permit the same document content in different collections or repository
provenances. The older schema required every document content hash to be globally unique, so not every upgraded data set
can be represented by that schema. The migration itself remains fail-fast: it refuses the downgrade transaction before
dropping any columns when duplicate hashes exist. It never chooses a document to delete or merge.

With the app stopped, use the newer binary to inspect the on-disk database **before** attempting a downgrade:

```text
XE-Local-AI-Engine --knowledge-downgrade-preflight
```

The command runs before startup migrations and does not start the web server. It reports:

- whether the collection migration is present;
- compatibility with the legacy global uniqueness rule;
- conflict-group, conflicting-document, and minimum-removal counts; and
- deterministic opaque document identifiers grouped as `conflict-000001`, etc.

It never prints document content, filenames, paths, source identifiers, collection identifiers, or content hashes. Exit
code `0` means compatible, `3` means conflicts block the downgrade, and `1` means the check itself failed.

Create an explicit consistent SQLite backup before any downgrade attempt:

```text
XE-Local-AI-Engine --knowledge-downgrade-export
```

This command includes the same preflight and writes a `VACUUM INTO` snapshot under
`<data-directory>/backups/knowledge-downgrade/`. The artifact path, byte count, and SHA-256 are printed. The filename is
generated internally, an existing file is never overwritten, and symlink/reparse-point backup directories are rejected.
The export is still created when conflicts exist so the operator has a recovery artifact; the command then exits `3` to
make clear that the downgrade remains blocked.

Conflict resolution is deliberately manual and must happen in the newer version. Exporting does not remove, merge, or
rewrite any knowledge document. Re-run the preflight after operator-directed cleanup and proceed only after it exits `0`.

Then download and verify the older platform artifact and run it from a separate location. If it cannot open the
migrated data, stop it and restore the complete pre-update backup or return to the newer version. Do not delete
`node.sqlite` as a downgrade technique.

Builds from before the unlock page cannot read the password-protected `node.key` and refuse to start, reporting
the key file as corrupt. **Do not delete `node.key` because of that message** — without it your encrypted data is
lost. Restore the complete pre-update backup instead.

### When the node refuses to start

A node that cannot start safely stops with an exit code and logs why; codes 5, 8, 9 and 10 also print a hint on standard
error, which the desktop app shows. Take the next step for the code you see, and keep the complete data directory intact until
the node runs again.

| Exit code | Meaning | Next step |
|---|---|---|
| `5` | The password-protected node key needs a secret, and the one supplied is missing or wrong. | Start again with the correct admin password (environment variable or `--admin-password-stdin`), or pipe the recovery code shown at setup when you are resetting the password. |
| `6` | The port you asked for with `--port` is already in use. The node does not fall back to another port. | Stop whatever holds the port, or start without `--port` or with a different one. |
| `7` | The unlock page's port stayed taken after the unlock, so the node could not start on a new address. | Close the program that took the port and start the node again. |
| `8` | The node key does not match the database, or the key file for the database is missing. | Do not delete `node.key` or the database. Restore the matching pair from your backup, as above. |
| `9` | The database migration failed. | Follow the restore hint the node prints: it names the newest pre-migration snapshot and the files to delete and copy, or tells you to move a damaged database aside. Then report the failure before retrying. |
| `10` | The data directory is unusable: it cannot be created or written, or its `node-settings.json` is present but cannot be read. | Fix the ownership or permissions of the folder the message names. For `node-settings.json`, repair the file or move it aside; moving it aside resets the node's settings, not your data. The maintenance commands, including the admin password reset, also stop with `10` while `node-settings.json` cannot be read. |

## Signing warnings after an update

Release files are not code-signed yet, so Windows may show the SmartScreen warning again for a newly
downloaded version, and Linux security tools may ask you to trust the new AppImage. Verify the new file
against `CHECKSUMS.sha256` before running it.
→ [The SmartScreen warning](install-windows.md#the-windows-smartscreen-warning)

## Problems with an update

When reporting a regression, include the version that worked, the version that failed, the operating system, and the
error you saw. The [FAQ](faq.md#updating) covers the common update questions.

See [Giving feedback](feedback.md).

**[← Back to the main page](../README.md)**
