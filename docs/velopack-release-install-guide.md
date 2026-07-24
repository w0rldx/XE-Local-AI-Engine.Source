# Velopack release packaging: install, update, and retirement notes

This document covers the first-install bootstrap flow, the one-time migration from the old
raw-exe installer, and the retirement plan for the legacy `.ps1`/`.sh` installer CLI.

---

## 1. First-install bootstrap (tester onboarding)

**Why device-flow does NOT gate first install.**
The in-app GitHub device-flow authenticates the user so the update checker can reach the
(private) release repo. That flow only runs *after* the app is installed. The installer
itself (`Setup.exe` on Windows, `.AppImage` on Linux, or the portable `.zip`) lives in
that same private repo, which creates a chicken-and-egg: the user needs the installer to
get the app, but the app gates the auth.

**How testers get the first installer.**
A tester who is already a GitHub collaborator on the release repo downloads the initial
installer directly through their own GitHub web browser session:

1. Navigate to the tester release repo on github.com.
2. Open the latest release and download the appropriate artifact:
   - Windows: `XE-Local-AI-Engine-win-x64-Setup.exe` (or the portable `.zip`)
   - Linux: `XE-Local-AI-Engine-linux-x64.AppImage` (or the portable `.zip`)
3. Run the installer. No auth token is needed at install time.
4. On first launch, sign in via the in-app GitHub device-flow so the update checker can
   reach the release repo for subsequent updates.

Once signed in, all future update checks and downloads are handled entirely within the
app — no further manual download steps are required.

**Operator note:** for very early private testing a single one-time shared download link
(GitHub's temporary asset URL via the API, or a short-lived signed URL) is an acceptable
alternative to requiring collaborator access for the download step.

---

## 2. Raw-exe to Velopack one-time re-install

Users who installed an earlier build via the legacy `.ps1`/`.sh` installer CLI have a
plain self-contained executable (no Velopack delta channel). To move them onto the
managed update channel they must perform a one-time re-install:

1. Download the Velopack `Setup.exe` (Windows) or `.AppImage` / portable `.zip` (Linux)
   from the release repo as described in §1 above.
2. Run it. Velopack installs to its own managed location and leaves the old raw-exe in
   place — it does not auto-remove it.
3. Launch the newly installed app and sign in via device-flow.
4. Manually remove the old raw-exe and its launcher shortcut.

**Open question (must resolve before general rollout):** does the Velopack install
migrate `~/.xe-local-ai-engine/` (user data dir, local models, settings) or start clean?
This must be confirmed during the manual smoke test. It must not silently discard local
state.

---

## 3. What Velopack replaces (decision #10)

| Old artifact | Replaced by |
|---|---|
| `install.ps1` / `install.sh` (installer CLI) | `Setup.exe` (Windows) / `.AppImage` (Linux) |
| `uninstall.ps1` / `uninstall.sh` (uninstaller CLI) | Velopack's built-in uninstall hook |
| Portable `.zip` (manual extraction) | Velopack portable `.zip` (same shape, now update-aware) |

Velopack owns install, update, and uninstall for the desktop flavor. The in-app update
endpoint (`POST /app-update/apply`) triggers download + apply + relaunch via the
Velopack SDK.

**Self-update works for Velopack-managed installed and portable bundles.** Users who run
the raw self-contained publish output directly (outside a Velopack installed/portable
bundle) will not receive in-app updates. The release notes for the first Velopack release
must explain the one-time move to a Velopack artifact (§2 above).

---

## 4. Legacy installer CLI retirement plan

The existing installer and uninstaller scripts are **not deleted yet**. They are retained
while Velopack is in tester rollout. Retire them after all of the following are confirmed:

- [ ] Velopack uninstall hook achieves full teardown parity with the legacy uninstaller:
  - User data directory (`~/.xe-local-ai-engine/` on Linux, `%LOCALAPPDATA%\XE-Local-AI-Engine\` on Windows)
  - llama.cpp managed binaries (per-variant dirs under the data directory)
  - Encrypted secrets / token store files (`*.enc`)
  - Desktop shortcut / Start Menu entry (Windows) or `.desktop` file (Linux)
- [ ] Manual smoke test for the raw-exe -> Velopack migration path passes (§2 above).
- [ ] General (non-tester) rollout is approved.

Once all three gates are green, remove:
- `installer/install.ps1` and `installer/install.sh`
- `installer/uninstall.ps1` and `installer/uninstall.sh`

---

## 5. CI workflow quick-reference

Workflow: `.github/workflows/release.yml`

| Trigger | Default channel | Notes |
|---|---|---|
| `git push v*.*.*` tag | `main` | Production release repo |
| `workflow_dispatch` | selectable | Choose `main` or `tester` |

**Required secrets (per release repo):**

| Secret name | Scope |
|---|---|
| `GH_RELEASE_TOKEN_MAIN` | `contents:write` on main release repo only |
| `GH_RELEASE_TOKEN_TESTER` | `contents:write` on tester repo only |

**Required variables (repository variables, not secrets):**

| Variable name | Example value |
|---|---|
| `GH_RELEASE_REPO_MAIN` | `https://github.com/org/xe-releases` |
| `GH_RELEASE_REPO_TESTER` | `https://github.com/org/xe-releases-tester` |

The `release` GitHub environment must be created and at least one required reviewer added
before any non-tester rollout (see supply-chain hardening note in the workflow file).

---

## 6. Rolling back a bad update

If an in-app update produces a broken build, you can go back to a known-good binary version.
The per-user **data directory** (`%LOCALAPPDATA%\XE-Local-AI-Engine` on Windows,
`~/.local/share/XE-Local-AI-Engine` on Linux) is independent from the versioned app binaries,
but an older binary is not guaranteed to understand a database migrated by a newer version.
Back up the entire data directory before attempting a rollback.

**Option A — previous version already on disk (Velopack-managed installs).**
Velopack installs each version into its own versioned folder under the managed install root
(`%LOCALAPPDATA%\XE-Local-AI-Engine\app-<version>\` on Windows) and points a `current`
reference at the active one; the immediately previous version's folder is normally still
present after an update. To roll back, close the app and launch the previous
`app-<version>`'s executable directly. (This is a manual pin — the updater may re-offer the
newer version on the next check.)

**Option B — re-install an older release asset.**
Download the older version's artifact from the release repo (the previous `Setup.exe` /
`.AppImage`, or the portable `.zip`) and run it. Velopack treats it as an install of that
version; because the data dir is separate, your chats and models carry over.

**Option C — portable zip / tester zip.**
A Velopack portable tester zip is update-aware, so running an older portable can cause the
newer RC to be offered again. A legacy `package-rc.sh` tester zip has no self-update. In
either case, run the older unzipped build against the same data directory only after
considering the schema caveat below.

**Caveat — database schema is forward-only.** EF Core migrations run on first launch of a
newer build and are **not** reverted when an older build starts. Some releases may include
data repairs or destructive schema cleanup, so binary rollback does not imply database
compatibility. If the older build cannot use the migrated database, stop it and either
restore the complete pre-update data-directory backup or return to the newer binary. Do not
delete `node.sqlite` as a rollback step, and do not hand-edit the database. A deliberate
reset is data loss and belongs only in the explicit reset procedure in
[troubleshooting.md](troubleshooting.md).
