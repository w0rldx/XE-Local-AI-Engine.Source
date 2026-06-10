# XE Local AI Engine — RC1 Tester Quick-Start

Thank you for testing XE Local AI Engine RC1.  This guide gets you from the zip you received
to a running engine in one session.  Read it fully before you start.

---

## Prerequisites

Before running the installer, confirm all of the following:

| Requirement | Detail |
|---|---|
| **Windows 11** | Build 22000 or later (Settings → System → About → OS Build).  Windows 10 is not supported. |
| **Administrator rights** | The installer must run elevated.  UAC will prompt once at launch — this is expected. |
| **Internet connection** | Required to pull the `qwen3:0.6b` bootstrap model (~400 MB) during install.  Wi-Fi or wired. |
| **Free disk space** | The installer checks the exact requirement from the bundle (`bundle-metadata.json` → `minimumFreeDiskBytes`).  As a rough guide, plan for at least 10 GB free on the install drive (C:) to cover the WSL distro, container image, and model data. |
| **WSL2 capability** | Most Windows 11 machines already have it.  If not, the installer enables it and tells you to reboot once. |

> The installer does **not** require Docker Desktop, Visual Studio, or any other software to be
> pre-installed.  Everything is self-contained.

---

## Step 1 — Unblock the installer

Windows marks files downloaded from the internet as untrusted (`Zone.Identifier`).  You must
unblock `xe-installer.exe` before running it, or the operating system will silently block it.

**Option A — PowerShell (recommended):**

Open a PowerShell window in the folder where you unzipped the bundle and run:

```powershell
Unblock-File .\xe-installer.exe
```

**Option B — File Explorer:**

Right-click `xe-installer.exe` → Properties → tick **Unblock** at the bottom → OK.

> **SmartScreen warning:** because this RC build is unsigned, Windows SmartScreen may show an
> "unrecognized app" dialog.  Click **More info** then **Run anyway** to proceed.
> Code signing is planned for RC2/GA.

---

## Step 2 — Install

Open an **elevated** (Administrator) PowerShell or Command Prompt in the bundle folder, then run:

```powershell
.\xe-installer.exe install --bundle .
```

The installer prints progress as it works through these phases:

1. Verifies payload checksums (`SHA256SUMS`).
2. Enables WSL2 if needed (may require a reboot — see below).
3. Imports the `xe-engine-runtime` WSL2 distro.
4. Loads the app container image into the distro (reads the bundled tar via the WSL `/mnt/c` mount).
5. Writes the runtime configuration and admin token.
6. Installs the HostAgent and Tray (copies binaries + creates shortcuts).
7. Pulls the `qwen3:0.6b` model via Ollama (online — requires internet).
8. Verifies the engine is reachable and writes the install manifest.

**Expected duration:** approximately 5–15 minutes depending on internet speed and disk performance.

---

## Reboot notice (WSL2 first-time setup)

If WSL2 is not already enabled on your machine, the installer will enable it and then print:

```
WSL2 enabled. A one-time reboot is required.
After rebooting, run xe-installer install --bundle <this-folder> again — it resumes where it left off.
Keep this folder in place across the reboot (do not move or delete it).
```

**RC1 does not resume automatically.** After rebooting, you must run the install command again
yourself.  The installer detects the reboot state and picks up where it left off — you will not
redo any already-completed steps.

> Keep the unzipped bundle folder in its current location across the reboot.  Do not run the
> installer from a Downloads folder that Windows clears on restart, or a removable drive.

---

## Step 3 — Verify the engine is running

After the install completes, open a browser and navigate to:

```
http://localhost:5173
```

You should see the XE Local AI Engine chat interface.  Start a chat — the first response may
take 10–30 seconds as the model loads.  Confirm you receive an answer from `qwen3:0.6b`.

You can also check the install state at any time:

```powershell
.\xe-installer.exe status
```

---

## Verbs and flags reference

| Command | Description |
|---|---|
| `xe-installer install --bundle <dir>` | Fresh install from the given bundle directory. |
| `xe-installer status` | Print current install phase and reboot-pending flag.  No changes made. |
| `xe-installer reset --bundle <dir>` | Full teardown then fresh install from the bundle (supported upgrade path for RC1). |
| `xe-installer remove` | Remove everything the installer created.  Prompts for typed confirmation. |
| `xe-installer remove --yes` | Remove without interactive confirmation (use with care). |
| `xe-installer remove --keep-models` | Remove everything except the downloaded Ollama model data. |
| `xe-installer install --bundle <dir> --dry-run` | Show what would be done without making changes. |

---

## Troubleshooting

**"Already installed — use reset":** the installer detected a previous install.  Run
`xe-installer reset --bundle .` to tear down and reinstall, or `xe-installer remove` to
remove only.

**"Distro xe-engine-runtime exists but no install manifest":** a previous install was
interrupted before it completed.  Run `xe-installer remove` to clean up, then reinstall.

**Port 5173 or 11434 already in use:** another application is using a port the engine needs.
Stop that application and re-run `xe-installer install`.

**SmartScreen blocks the exe:** see Step 1 above — unblock the file first.

**WSL2 install fails:** ensure Hyper-V / Virtual Machine Platform is enabled in Windows
Features (Settings → Optional Features → More Windows features → Virtual Machine Platform).

---

## About SHA256SUMS

The bundle includes a `SHA256SUMS` file.  The installer verifies every payload file against
this list before making any changes.  **This guards against accidental corruption during
download or extraction — it is not an anti-tamper guarantee.**  An attacker who can modify
the payload files can also modify `SHA256SUMS`.  Payload signing (signed manifest + signed
exe) is planned for RC2/GA.

---

## Feedback

Please capture your install session transcript (copy the terminal output) and include:

- Your Windows 11 build number (Settings → System → About → OS Build).
- Whether a WSL2 reboot was required.
- Any error messages, with the full text.
- The output of `xe-installer status` after install.
- A screenshot or copy of the first chat response.

Send findings to the project contact.  Thank you for your help.
