# Troubleshooting (testers)

Quick fixes for the most common issues when running the desktop build of XE Local AI Engine. If none of these help, export a diagnostics snapshot (see [Reporting a problem](#reporting-a-problem)) and send it back.

## Where your data lives

Everything the app creates — database, keys, settings, downloaded model runtimes, and models — lives in one per-user directory:

| OS | Data directory |
|----|----------------|
| Windows | `%LOCALAPPDATA%\XE-Local-AI-Engine` |
| Linux | `~/.local/share/XE-Local-AI-Engine` (or `$XDG_DATA_HOME/XE-Local-AI-Engine`) |

Inside it you'll find `node.sqlite` (your chats/settings), `node.key` (the encryption key), `models/` (downloaded models), `llama.cpp/` and `stable-diffusion.cpp/` (downloaded runtimes), and `logs/` (log files).

> **Always stop the app before editing or deleting anything in this folder.** Close the console/terminal window first.

## A model won't load (out of memory / VRAM)

A model that is too large for your GPU's VRAM (or your RAM in CPU mode) fails to load.

- **Pick a smaller model or a smaller quant.** Open **Model Advisor / Model-fit** in the app — it profiles your hardware and lists models (and quant levels) that actually fit, with a "recommended" pick. Prefer a lower quant (e.g. `Q4_K_M`) of the same model before dropping to a smaller model.
- **Free up VRAM.** If you have an image model and a chat model loaded at once, VRAM can run out. Eject the model you're not using (Loaded Models), or close other GPU apps.
- **Only one image daemon loads at a time** by design (image generation is VRAM-heavy). If image generation and chat compete, generate images while no large chat model is loaded.

## GPU not detected — running on CPU

If responses are very slow, the app may be running on CPU. The app uses your GPU only when it can detect it reliably:

- **NVIDIA:** make sure current NVIDIA drivers are installed (the app probes `nvidia-smi`). No drivers → CPU mode.
- **AMD / Intel GPUs on Windows, and non-NVIDIA GPUs on Linux:** VRAM can't be measured reliably, so the app **falls back to CPU** even though the GPU exists. This is expected in this release. CPU mode works — it's just slower — so pick a smaller model (see above).

## Port conflicts

In desktop mode the app binds an automatically chosen free loopback port (`127.0.0.1`) and opens your browser at it. The model runtimes use their own private loopback ranges (llama.cpp `18100–18199`, image `sd-server` `18200–18299`).

- If the browser doesn't open or the page won't connect, check the console/terminal — it prints the exact URL. Open it manually.
- **Run only ONE instance at a time** against the same data directory. A second instance races on the database and can corrupt it.
- The app remembers its last port in `desktop-port.txt` under the data dir and reuses it when free. If that becomes a problem, stop the app and delete `desktop-port.txt`; the next launch picks a fresh port.

## Where the logs are

- **Live logs** stream in the console (Windows) / terminal (Linux) window the launcher opened — watch it while the app runs.
- **Persisted logs** are written to a `logs/` folder under your data dir (see the table above), so you can attach them to a bug report even after the window is closed.

## Reset the database (start clean)

If the app's chat/settings state is corrupted or you want a clean slate:

1. **Stop the app** (close the console/terminal window).
2. Delete `node.sqlite` from your data dir.
3. Restart. The app recreates an empty database on next launch.

This wipes chats, agents, scheduler jobs, and settings, but **keeps** your downloaded models and runtimes.

> **Do not delete `node.key`** unless you are also deleting `node.sqlite`. The database file itself is ordinary SQLite, but the sensitive **columns** inside it are encrypted with that key (per-column AES-256-GCM) — removing the key without the database leaves those columns permanently unreadable. If you delete `node.sqlite`, deleting `node.key` too is fine (a fresh key is generated).

## Fully remove the app

Use the uninstaller shipped in the zip (it stops the app + model engine, then deletes your data dir after you confirm):

- **Windows:** right-click `uninstall-xe-local-ai-engine.ps1` → **Run with PowerShell**.
- **Linux:** from a terminal in the unzipped folder, run `./uninstall-xe-local-ai-engine.sh`.

Then delete the unzipped app folder. To preview without deleting, pass `--dry-run` (Linux) / `-DryRun` (Windows). To keep your data, pass `--keep-data` / `-KeepData`.

## Reporting a problem

The best bug report is an in-app diagnostics snapshot:

1. In the app, open **Diagnostics** and use **"Report a problem"** to export a snapshot (it captures recent activity, network calls, and errors, with secrets redacted).
2. Send the exported snapshot back, along with **what you did**, and any **console/terminal log lines** and browser errors you saw.

---

See also: [Velopack release / install guide](velopack-release-install-guide.md) · Developer wiki [Hosting & Deployment](wiki/11-hosting-and-deployment.md), [Local Runtime & Providers](wiki/03-local-runtime-and-providers.md), [Model-fit / Advisor](wiki/07-model-fit.md).
