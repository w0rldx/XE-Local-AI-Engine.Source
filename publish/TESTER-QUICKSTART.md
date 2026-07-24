# XE Local AI Engine — tester quickstart

A self-contained desktop build for external testers. No install, no Docker, no Ollama, no
prerequisites — extract one portable folder and run it locally.

## Get a Windows tester build

A maintainer runs the canonical Windows RC packager from a clean, tagged checkout on Windows:

```powershell
$env:VPK_TOKEN = "<fine-grained tester-repo token>"
$env:XE_TESTER_GITHUB_APP_CLIENT_ID = "<real GitHub App client ID (Iv..., not the numeric App ID)>"
.\publish\package-tester-win.ps1
```

The script reads the release version from `Directory.Build.props`, runs all frontend and backend release
gates (including OpenAPI/license/coverage/dependency audits), publishes `win-x64`, validates the staged
tester update config, builds the Velopack portable package, and uploads it to the tester release repo.
The upload remains a draft. Smoke-test the exact generated `Portable.zip`, then publish
that unchanged draft using the printed hash:

```powershell
.\publish\package-tester-win.ps1 -PublishDraft -ExpectedPortableSha256 <printed-sha256>
```

Publication downloads and hashes the Portable ZIP attached to the draft; it does not
trust a local copy. The upload step also refuses to merge into an already-published
release.

Use `-SkipUpload` for a pre-tag packaging rehearsal; validation remains mandatory. The
client ID is public configuration supplied at packaging time; do not commit a guessed
value or placeholder.

For a Linux portable zip, use `publish/package-rc.sh --rid linux-x64`.

> **Smoke-test every Windows RC on real Windows before publishing it.** Native-library
> self-extraction, console-close child cleanup, and browser auto-open cannot be verified off-Windows.

## Run it (tester)

**Windows**
1. Download the Velopack `Portable.zip` asset from the tester release and unzip it.
2. Start the packaged XE Local AI Engine executable. Velopack-managed portable builds enter
   desktop mode automatically.

**Linux**
1. Unzip anywhere.
2. `./start-xe-local-ai-engine.sh` from a terminal in that folder.
   Do **not** run the bare `XE-Local-AI-Engine.Client` binary directly.

Either way: a console/terminal opens with live logs and your default browser opens the app
on a local loopback URL (`http://127.0.0.1:<port>/`). If the browser does not open, the URL
is printed in the console — paste it manually.

## What to expect on first run

- The app self-provisions a **llama.cpp runtime** and downloads a **~400 MB starter model**
  (`Qwen2.5-0.5B-Instruct`, Q4_K_M) from Hugging Face. This takes a few minutes, is mostly
  silent, and only happens once — watch the console.
- Needs **~2 GB free disk**. A GPU is optional; CPU works (slower).
- First open: set an **admin password** to create your login.
- Data location: `%LOCALAPPDATA%\XE-Local-AI-Engine` (Windows) /
  `$HOME/.local/share/XE-Local-AI-Engine` (Linux). Deleting that folder is a full reset.

## Stop

Close the console/terminal window. That gracefully stops the app **and** the model engine
(no leftover background process). Run only **one** instance at a time.

## Reset your environment

All app state — your login, settings, conversations, downloaded models, and the inference
runtime — lives under **one** folder:

- **Windows:** `%LOCALAPPDATA%\XE-Local-AI-Engine`
- **Linux:** `$HOME/.local/share/XE-Local-AI-Engine`

**Stop the app first** (close the console/terminal — see above) before deleting anything, or
you can corrupt the database.

**Full reset** — back to a clean first run (re-set the admin password; the ~400 MB model and
the runtime re-download on next launch):

```powershell
# Windows (PowerShell)
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\XE-Local-AI-Engine"
```
```sh
# Linux
rm -rf "$HOME/.local/share/XE-Local-AI-Engine"
```

**Lightweight reset** — wipe your account/data but **keep** the downloaded model + runtime so
you don't re-download (faster):

```powershell
# Windows (PowerShell)
$d = "$env:LOCALAPPDATA\XE-Local-AI-Engine"
Remove-Item -Force "$d\node.sqlite","$d\node.key"; Remove-Item -Force "$d\*.enc"
```
```sh
# Linux
d="$HOME/.local/share/XE-Local-AI-Engine"
rm -f "$d/node.sqlite" "$d/node.key" "$d"/*.enc
```

Deleting this folder only resets your data — the app stays installed. To remove the program
itself, uninstall it (Add/Remove Programs for an installed build, or delete the folder for a
portable build), then delete the data folder above.

## Report a problem

Send back: what you did, the relevant console log lines, and any in-browser error message.
