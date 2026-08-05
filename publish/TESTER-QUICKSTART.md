# XE Local AI Engine — tester quickstart (superseded)

> **Superseded.** End-user install/run guidance now lives in the [User Guide](../docs/user-guide/README.md),
> and app downloads are on this repo's
> [Releases](https://github.com/w0rldx/XE-Local-AI-Engine.Source/releases/latest) page. This file describes
> the retired private tester-repo flow and is kept for reference only.

A self-contained desktop build. No .NET runtime, Docker, Ollama, or installer is required—extract one
portable folder and run it locally. Internet access is needed for the initial runtime/model download and
update checks.

## Get a Windows tester build

A maintainer runs the canonical Windows RC packager from a clean, tagged checkout on Windows:

```powershell
$env:VPK_TOKEN = "<fine-grained tester-repo token>"
$env:XE_TESTER_GITHUB_APP_CLIENT_ID = "<real GitHub App client ID (Iv..., not the numeric App ID)>"
.\publish\package-tester-win.ps1
```

The script reads the release version from `Directory.Build.props`, rejects local Vite `.env*` /
inherited `VITE_*` overrides, runs all frontend and backend release
gates (including OpenAPI/license/coverage/dependency audits), publishes `win-x64`, validates the staged
tester update config, builds the Velopack portable package, and uploads it to the tester release repo —
**`w0rldx/XE-Local-AI-Engine.Tester-App`**, a repository separate from the source repo
(`w0rldx/XE-Local-AI-Engine`). The `v<version>` git tag goes on HEAD of the *source* repo; the release
is created on the *tester* repo. The upload remains a draft. Smoke-test the exact generated
`Portable.zip`, then publish that unchanged draft using the printed hash:

```powershell
.\publish\package-tester-win.ps1 -PublishDraft -ExpectedPortableSha256 <printed-sha256>
```

Publication verifies the pushed canonical source tag resolves to HEAD, downloads the complete
five-file Velopack asset set from the draft, and checks every file against the digest manifest from
the original pack. It also confirms the smoke-tested Portable hash. It does not trust a local copy,
and the upload step refuses to merge into an already-published release.

Keep the packaging checkout intact between upload and publication:
`RELEASE_NOTES.md` and `publish/dist/XE-Local-AI-Engine-<version>-win.sha256.json` are generated during
packaging, are git-ignored, and are required by `-PublishDraft`. If either is removed, regenerate it
from the exact tagged pack output before publishing.

Run it from **PowerShell 7+ (`pwsh`)** — the script declares `#Requires -Version 7.0` and will not run under
Windows PowerShell 5.1. The packaging machine also needs a **non-UTC time zone** (the script checks, and points at
`tzutil /s`).

Use `-SkipUpload` for a pre-tag packaging rehearsal: every build and test gate still runs, and it relaxes only the
client-ID requirement. `VPK_TOKEN` is still required because the private tester repository supplies the previous
full package used to generate the rehearsal delta. The script downloads that package into an isolated seed directory
and packs into a clean versioned output directory without uploading. An ID-less rehearsal bakes no client ID, ships an inert updater, and is stamped
`REHEARSAL-DO-NOT-SHIP.txt` inside the zip — never hand one to a tester. The client ID is public configuration
supplied at packaging time; do not commit a guessed value or placeholder.

For a Linux portable zip, use `publish/package-rc.sh --rid linux-x64` (run it on Linux/WSL). That zip is
**not** published anywhere and does **not** self-update — it ships the intentionally inert `main` update
channel. Only the Windows Velopack bundle above has a live update feed.

> **Smoke-test every Windows RC on real Windows before publishing it.** Native-library
> self-extraction, console-close child cleanup, and browser auto-open cannot be verified off-Windows.

## Run it (tester)

**Windows**
1. Sign in to GitHub with an account that can access the private tester repository. From the latest
   release at `https://github.com/w0rldx/XE-Local-AI-Engine.Tester-App/releases`,
   download `XE-Local-AI-Engine-win-Portable.zip` and unzip it. (There is no `Setup.exe` — the
   packager runs `vpk pack --noInst`, so the portable bundle is the shipped flavor. The `.nupkg`
   files and `releases.win.json` next to it are for the in-app updater, not for you.)
2. Start the packaged XE Local AI Engine executable. Velopack-managed portable builds enter
   desktop mode automatically — no launcher script needed.

**Linux**
1. Unzip anywhere.
2. `./start-xe-local-ai-engine.sh` from a terminal in that folder.
   Do **not** run the bare `XE-Local-AI-Engine.Client` binary directly.

Either way: a console/terminal opens with live logs and your default browser opens the app
on a local loopback URL (`http://127.0.0.1:<port>/`). If the browser does not open, the URL
is printed in the console — paste it manually.

**This software is licensed under Apache-2.0**; `LICENSE` states the terms and
`NOTICE` carries the third-party attributions.

- **Windows:** `LICENSE` and `NOTICE` ship inside the `current` folder, not alongside the
  top-level `XE-Local-AI-Engine.exe` you launch.
- **Linux:** `LICENSE`, `NOTICE`, and a `READ-ME-FIRST.txt` one-screen quickstart ship
  directly alongside the executable and `start-xe-local-ai-engine.sh`.

## What to expect on first run

- The app self-provisions a **llama.cpp runtime** and downloads a **~400 MB starter model**
  (`Qwen2.5-0.5B-Instruct`, Q4_K_M) from Hugging Face. This takes a few minutes, is mostly
  silent, requires internet access, and only happens once — watch the console.
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
