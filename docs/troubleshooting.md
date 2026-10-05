# Troubleshooting (testers)

Quick fixes for the most common issues when running the desktop build of XE Local AI Engine. If none of these help, export a diagnostics snapshot (see [Reporting a problem](#reporting-a-problem)) and send it back.

## Where your data lives

Everything the app creates — database, keys, settings, downloaded model runtimes, and models — lives in one per-user directory:

| OS | Data directory |
|----|----------------|
| Windows | `%LOCALAPPDATA%\XE-Local-AI-Engine` |
| Linux | `~/.local/share/XE-Local-AI-Engine` (or `$XDG_DATA_HOME/XE-Local-AI-Engine`) |

Inside it you'll find `node.sqlite` (your chats/settings), `node.key` (the encryption key, itself locked by your admin password and recovery code), `models/` (downloaded models), `llama.cpp/`, `stable-diffusion.cpp/` and `whisper.cpp/` (downloaded runtimes), and `logs/` (log files).

> **Always stop the app before editing or deleting anything in this folder.** Quit it first (**Quit XE** in the tray menu, or close the window and choose to quit).

## The app shows "Unlock this node"

This is expected on **every** start, including the restart after an update. `node.key` no longer holds the encryption key in plain form: it is wrapped by your admin password and by the one-time recovery code shown at setup. Until someone unlocks it, the engine serves only the unlock page; scheduled jobs do not run, and every other local API route, MCP included, answers `503`.

- **You know the password:** enter it and click **Unlock**.
- **You forgot the password:** click **"Forgot your password? Use your recovery code"**, enter the code and a new password. From the command line instead (app stopped), pipe the code in: `--reset-admin-password '<new password>' --recovery-code-stdin`. Without the code, or with a wrong one, the command exits `5` and changes nothing. The recovery code itself stays the same. Full steps: [FAQ — I forgot my password](user-guide/docs/faq.md#i-forgot-my-password).
- **You lost both the password and the recovery code:** the data cannot be recovered. The only way forward is a fresh start (delete `node.sqlite` and `node.key` together, see below).
- **Headless or scripted start:** pass the password in `XE_ADMIN_PASSWORD` or on stdin with `--admin-password-stdin`, and the engine unlocks without the page. A wrong password exits `5`. See the [Agentic Support guide](agentic-support/agent-install.md).
- **An older build says `node.key` is corrupt:** builds from before the unlock page cannot read the protected key. Do **not** delete `node.key`; go back to the newer build or restore a complete backup.

## A model won't load (out of memory / VRAM)

A model that is too large for your GPU's VRAM (or your RAM in CPU mode) fails to load.

- **Pick a smaller model or a smaller quant.** Open **Models → Recommendations** in the app's left sidebar (the model advisor) — it profiles your hardware and lists models (and quant levels) that actually fit, with a "recommended" pick. Prefer a lower quant (e.g. `Q4_K_M`) of the same model before dropping to a smaller model.
- **Free up VRAM.** If you have an image model and a chat model loaded at once, VRAM can run out. Eject the model you're not using (Loaded Models), or close other GPU apps.
- **Only one image daemon loads at a time** by design (image generation is VRAM-heavy). If image generation and chat compete, generate images while no large chat model is loaded.

## GPU not detected — running on CPU

If responses are very slow, the app may be running on CPU. The app uses your GPU only when it can detect it reliably:

- **NVIDIA:** make sure current NVIDIA drivers are installed (the app probes `nvidia-smi`). No drivers → CPU mode.
- **AMD / Intel GPUs on Windows:** these now run GPU-accelerated inference via Vulkan (fixed in rc.5.0 — older builds silently fell back to CPU and said nothing). VRAM still can't be measured reliably on these cards, so **Models → Recommendations** sizing falls back to your system RAM instead, which is less precise. The CPU-fallback alert also isn't reachable on these cards yet, so the app can't reliably warn you if inference does end up on CPU — watch response speed as your signal instead.
- **Non-NVIDIA GPUs on Linux:** VRAM still can't be measured reliably, so the app **falls back to CPU** even though the GPU exists. This is expected in this release. CPU mode works — it's just slower — so pick a smaller model (see above).

On Linux with an NVIDIA card, upstream publishes no prebuilt CUDA `llama-server`, so an operator who wants the CUDA path instead of the Vulkan default can supply their own binary — see the [Linux CUDA bring-your-own llama-server runbook](runbooks/linux-cuda-override-operator-runbook.md).

## Port conflicts

In desktop mode the app binds an automatically chosen free loopback port (`127.0.0.1`) and opens your browser at it. The model runtimes use their own private loopback ranges (llama.cpp `18100–18199`, image `sd-server` `18200–18299`, transcription `whisper-server` `18300–18399`).

- If the browser doesn't open or the page won't connect, check the console/terminal — it prints the exact URL. Open it manually.
- **Run only ONE instance at a time** against the same data directory. A second instance races on the database and can corrupt it.
- The app remembers its last port in `desktop-port.txt` under the data dir and reuses it when free. If that becomes a problem, stop the app and delete `desktop-port.txt`; the next launch picks a fresh port.

## Where the logs are

- **Log files** are written to a `logs/` folder under your data dir (see the table above): the engine's daily `xe-node-<date>.log`, plus `desktop.log` (the app window) and, on Windows, `launcher.log`. They survive the app closing, so you can attach them to a bug report.
- **Open logs folder** in the tray menu, or the same button on the error screen shown when the app cannot start, opens that folder in your file manager (it is created if it does not exist yet).
- The desktop app opens **no console window**, so there is no live log by default. To watch one, start the app with `--debug` (below).

### Debug mode (`--debug`)

Start the app from a terminal with `--debug` added: on Windows, `XE-Local-AI-Engine.exe --debug` in the top-level portable folder; on Linux, the AppImage followed by `--debug`. Then:

- the engine starts at **Debug** log level and its log lines stream into that terminal, as well as into the log files;
- the app window allows the WebView developer tools;
- the terminal stays tied to the app: **closing it quits the app**, so quit from the app instead when you are done.

Log streaming applies only when this launch starts the engine. If an engine is already running for the data directory, or another app window already owns it, the terminal prints one line saying the logs are not streamed and naming the logs folder; read the files there instead.

## Reset the database (start clean)

If the app's chat/settings state is corrupted or you want a clean slate:

1. **Stop the app** (quit it from the tray menu or the close dialog).
2. Delete `node.sqlite` from your data dir, and `node.key` with it. If `node.key` stays, the next start still asks for the old admin password on the unlock page.
3. Restart. The app recreates an empty database on next launch and runs setup again.

This wipes chats, agents, scheduler jobs, and settings, but **keeps** your downloaded models and runtimes.

> **Do not delete `node.key`** unless you are also deleting `node.sqlite`. The database file itself is ordinary SQLite, but the sensitive **columns** inside it are encrypted with the key that `node.key` protects (per-column AES-256-GCM) — removing the key without the database leaves those columns permanently unreadable. If you delete `node.sqlite`, deleting `node.key` too is fine: setup runs again, creates a fresh key and shows a new recovery code.

## Fully remove the app

1. Quit the app and confirm its window and tray icon are gone.
2. Remove the application files:
   - **Windows:** delete the extracted Velopack portable directory.
   - **Linux:** delete the AppImage.
3. To remove all user data too, delete the platform data directory from the table above.

Deleting the application files does not delete the separate user-data directory. Back it up before removal if you may
need chats, settings, models, or keys later.

## Reporting a problem

The best bug report is an in-app diagnostics snapshot:

1. In the app, open **Diagnostics** and use **"Report a problem"** to export a snapshot (it captures recent activity, network calls, and errors, with secrets redacted).
2. Send the exported snapshot back, along with **what you did**, any errors you saw, and if the app would not start, the files from **Open logs folder** (or the terminal output of a `--debug` run).

---

See also: [Velopack release / install guide](velopack-release-install-guide.md) · Developer wiki [Hosting & Deployment](wiki/11-hosting-and-deployment.md), [Local Runtime & Providers](wiki/03-local-runtime-and-providers.md), [Model-fit / Advisor](wiki/07-model-fit.md).
