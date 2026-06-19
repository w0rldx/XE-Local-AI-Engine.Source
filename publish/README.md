# XE Local AI Engine — self-contained desktop distribution

This directory contains OS-specific launcher scripts for the self-contained single-file builds.

---

## File layout after publishing

### Linux

```
publish/linux/
  run-xe-local-ai-engine.sh      ← launcher (this repo)
  XE-Local-AI-Engine.Client      ← published binary (copy here after dotnet publish)
```

### Windows

```
publish\windows\
  run-xe-local-ai-engine.cmd     ← launcher (this repo)
  XE-Local-AI-Engine.Client.exe  ← published binary (copy here after dotnet publish)
```

---

## How to publish

### Linux (linux-x64)

```sh
dotnet publish XE-Local-AI-Engine.Client \
  -c Release \
  -r linux-x64 \
  -p:PublishProfile=linux-x64
```

Output lands in `XE-Local-AI-Engine.Client/bin/Release/net10.0/linux-x64/publish/`.
Copy `XE-Local-AI-Engine.Client` (the binary — no extension) next to `publish/linux/run-xe-local-ai-engine.sh`.

Alternatively, using explicit MSBuild properties (equivalent to the profile):

```sh
dotnet publish XE-Local-AI-Engine.Client \
  -c Release \
  -r linux-x64 \
  -p:SelfContained=true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishTrimmed=false
```

### Windows (win-x64)

```powershell
dotnet publish XE-Local-AI-Engine.Client `
  -c Release `
  -r win-x64 `
  -p:PublishProfile=win-x64
```

Output lands in `XE-Local-AI-Engine.Client\bin\Release\net10.0\win-x64\publish\`.
Copy `XE-Local-AI-Engine.Client.exe` next to `publish\windows\run-xe-local-ai-engine.cmd`.

---

## Starting the app

**Linux:** open a terminal in `publish/linux/` and run:

```sh
./run-xe-local-ai-engine.sh
```

**Windows:** double-click `publish\windows\run-xe-local-ai-engine.cmd`, or open a Command Prompt in that directory and run it. Do not use PowerShell's `Start-Process` or the `start` command — the binary must run in the current console window for the shutdown signal to be delivered correctly (see below).

---

## What `XE_LAUNCH_MODE=desktop` does

The launchers set the environment variable `XE_LAUNCH_MODE=desktop` before starting the binary. This single flag enables all desktop-mode behaviour:

- **Loopback HTTP on an auto-selected free port** — Kestrel binds `http://127.0.0.1:<free-port>` instead of using the default URL configuration. HTTPS redirect and HSTS are bypassed (loopback HTTP is safe because traffic never leaves the loopback adapter).
- **Auto-opens the default browser** at the running URL once the host has started.
- **Console logs** — the Serilog console sink streams live log output to the terminal/console window.
- **Graceful shutdown on console close:**
  - *Linux:* closing the terminal sends `SIGHUP` to the process; the host converts it to `StopApplication()`, which triggers DI disposal including the `llama-server` child process teardown (no orphan).
  - *Windows:* closing the console window sends `CTRL_CLOSE_EVENT`; the host's `SetConsoleCtrlHandler` converts it to `StopApplication()`. The Job Object assigned to the `llama-server` child guarantees the child is killed even if the drain is cut short by the OS grace window (~5s).

Running the binary **without** this variable (e.g. in Aspire, CI, or headless) leaves behaviour byte-identical to before this feature was added: no browser launch, no loopback override, full HTTPS pipeline.

---

## Single-instance caveat

Run only **one instance** at a time against the same user-data directory. The app stores its SQLite database in `$HOME/.local/share/XE-Local-AI-Engine` (Linux) or `%LOCALAPPDATA%\XE-Local-AI-Engine` (Windows). A second instance will race on the database and may corrupt data. The auto-port selection avoids a listener-port collision but does not protect against database contention.

---

## Native dependency extraction

The single-file binary bundles native libraries (`e_sqlite3` for SQLite and the NSec/libsodium library for encryption). On first launch the .NET runtime extracts them to a per-user temporary directory and loads them from there. This extraction is automatic; no manual step is required.
