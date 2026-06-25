# XE Local AI Engine — tester quickstart

A self-contained desktop build for external testers. No install, no Docker, no Ollama, no
prerequisites — one folder, one launcher. The app runs entirely on your machine.

## Get a build

A maintainer produces the bundle with:

```bash
publish/package-rc.sh                 # both win-x64 and linux-x64
publish/package-rc.sh --rid win-x64   # one platform
```

Output (git-ignored): `publish/dist/xe-local-ai-engine-<version>-<rid>.zip` plus a
`.sha256` sidecar. Send the matching zip to the tester.

> The win-x64 bundle is cross-built on Linux. **It must be smoke-tested on a real Windows
> machine before tagging an RC** (native-library self-extract, console-close no-orphan, and
> browser auto-open cannot be verified off-Windows).

## Run it (tester)

**Windows**
1. Unzip anywhere.
2. Double-click **`Start-XE-Local-AI-Engine.cmd`**.
   Do **not** double-click `XE-Local-AI-Engine.Client.exe` directly — without the launcher
   it does not enter desktop mode (no loopback/browser-open).

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

## Report a problem

Send back: what you did, the relevant console log lines, and any in-browser error message.
