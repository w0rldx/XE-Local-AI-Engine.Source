# Troubleshooting (operators and developers)

> **End users: start with the [FAQ & troubleshooting](user-guide/docs/faq.md).** It covers models that won't
> load, GPU detection, ports, the unlock page, resets, SmartScreen and update problems. How to send a report
> with logs is in [Giving feedback](user-guide/docs/feedback.md).

This page holds what an operator or developer needs beyond that: the data directory layout, logging switches, the
support bundle, unattended starts and the dev host.

## Data directory layout

Everything the app creates lives in one per-user directory:

| OS | Data directory |
|----|----------------|
| Windows | `%LOCALAPPDATA%\XE-Local-AI-Engine` |
| Linux | `~/.local/share/XE-Local-AI-Engine` (or `$XDG_DATA_HOME/XE-Local-AI-Engine`) |

An absolute `XE_DATA_DIR` overrides it. Inside you'll find:

- `node.sqlite`: chats, agents, settings and every other stored record.
- `node.key`: the encryption key, wrapped by the admin password and by the one-time recovery code (ADR 0018).
- `models/`: downloaded models. `llama.cpp/`, `stable-diffusion.cpp/` and `whisper.cpp/`: downloaded runtimes.
- `logs/`: log files (see below).
- `*.enc` files: encrypted credential stores, such as cloud and external-provider credentials.

**Always stop the app before editing or deleting anything here.** The database file is ordinary SQLite, but its
sensitive **columns** are encrypted per column (AES-256-GCM) with the key that `node.key` protects. Removing
`node.key` without `node.sqlite` leaves those columns permanently unreadable; deleting both together is a clean
reset, and setup then creates a fresh key and shows a new recovery code. The user-level reset steps are in the
[FAQ](user-guide/docs/faq.md#how-do-i-completely-reset-the-app).

Run only **one** instance per data directory. The engine holds a single-instance lease and a second engine exits;
two processes racing on one database can corrupt it.

## Locked node and unattended starts

Every start is locked until the admin password unlocks `node.key`. While locked, the engine serves only the unlock
page and `/health/ready`; scheduled jobs do not run, and every other local API route, MCP included, answers `503`
("Vault locked").

- **Headless or scripted start:** pass the password in `XE_ADMIN_PASSWORD` or on stdin with
  `--admin-password-stdin`, and the engine unlocks without the page. A wrong password exits `5`. See the
  [Agentic Support guide](agentic-support/agent-install.md).
- **Command-line password reset** (app stopped): `--reset-admin-password '<new password>' --recovery-code-stdin`, with
  the recovery code piped in. A missing or wrong code exits `5` and changes nothing. A successful reset prints a new
  `XE_RECOVERY_CODE=` line and retires the old code. On Windows run it through
  `current\XE-Local-AI-Engine.WindowsLauncher.exe`. The user-facing steps are in the
  [FAQ](user-guide/docs/faq.md#i-forgot-my-password).

## Readiness and port discovery

- The desktop engine binds an automatically chosen free loopback port and remembers it in `desktop-port.txt` in the
  data directory; it reuses that port when free. Deleting the file (app stopped) makes the next launch pick a fresh
  one.
- A local serve mode, `--mcp-only` included, prints one `XE_READY=1 … XE_URL=… XE_MCP_URL=…` line and writes
  `<data-dir>/ready.json`, the canonical discovery record. `--status --json` reports `running`, the URLs and the
  vault state, and exits `0` only when the engine is running. Details:
  [connect an MCP client](runbooks/connect-an-mcp-client-runbook.md) and the
  [Agentic Support guide](agentic-support/agent-install.md).
- Model runtimes use private loopback ranges: llama.cpp `18100–18199`, `sd-server` `18200–18299`, `whisper-server`
  `18300–18399`.

## Logs and debug mode

- **Files** live in `logs/` under the data directory: the engine's daily `xe-node-<date>.log` (rolls at 50 MB, keeps
  7 files), plus `desktop.log` (the app window), `startup-crash.log` and, on Windows, `launcher.log`. They survive the
  app closing. **Open logs folder** in the tray menu or on the startup-error screen opens the directory.
- **Verbose logging without a restart:** on the **Diagnostics** page, turn on **Verbose logging until restart**. The
  node log gets Debug detail until the engine next restarts.
- **`--debug`:** a desktop launch shows no console. Start it from a terminal with `--debug` (on Windows,
  `XE-Local-AI-Engine.exe --debug` in the top-level portable folder; on Linux, the AppImage followed by `--debug`).
  The engine then runs at **Debug** level and streams its lines into that terminal as well as the log files, and the
  app window allows the WebView developer tools. Closing that terminal quits the app. Streaming applies only when
  this launch starts the engine: if an engine is already running for the data directory, the terminal prints one
  line naming the logs folder instead.

## Support bundle

The **Export** button on the **Diagnostics** page downloads one zip: the browser snapshot plus the node's support
bundle (`GET diagnostics/support-bundle`, Operator policy). The bundle holds a manifest, node info, the tails of the
newest node logs and the auxiliary logs, and the recent output of the llama-server, sd-server and whisper-server
processes. Paths, e-mail addresses and tokens are scrubbed; prompt text and model or file names can remain, so review
it before attaching it to a public issue. Redaction rules: [Security & Privacy](wiki/12-security-and-privacy.md#support-bundle-redaction).

## GPU paths for operators

User-level GPU symptoms are in the [FAQ](user-guide/docs/faq.md#replies-are-extremely-slow). On Linux with an NVIDIA
card, upstream publishes no prebuilt CUDA `llama-server`, so an operator who wants CUDA instead of the Vulkan default
can supply their own binary: [Linux CUDA bring-your-own llama-server runbook](runbooks/linux-cuda-override-operator-runbook.md).

## Dev host

A development checkout runs through the Aspire AppHost, not the packaged launcher: `scripts/dev-start.sh`,
`scripts/dev-status.sh` (the port changes on every restart) and `scripts/dev-stop.sh`, the only sanctioned stop path.
Its data directory is `XE-Local-AI-Engine.AppHost/.data`, not the per-user directory above. Never stop instances by
process name; kill by PID. Details: `scripts/README-dev-stop.md`.

## Reporting a problem

Attach the **Diagnostics → Export** zip, and if the app would not start, the files from **Open logs folder** or the
terminal output of a `--debug` run. Say what you did and what you saw. The user-facing version of this is
[Giving feedback](user-guide/docs/feedback.md).

---

See also: [Velopack release guide](velopack-release-install-guide.md) · Developer wiki
[Hosting & Deployment](wiki/11-hosting-and-deployment.md), [Local Runtime & Providers](wiki/03-local-runtime-and-providers.md),
[Model-fit / Advisor](wiki/07-model-fit.md).
