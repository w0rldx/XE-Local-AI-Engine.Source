#!/usr/bin/env bash
# run-xe-local-ai-engine.sh — desktop launcher for XE Local AI Engine (Linux)
#
# Layout expected beside this script:
#   publish/linux/
#     run-xe-local-ai-engine.sh    ← this file
#     XE-Local-AI-Engine.Client    ← self-contained binary (dotnet publish output)
#
# What this script does:
#   1. Sets XE_LAUNCH_MODE=desktop so the host enters desktop mode (loopback
#      auto-port, browser open, SIGHUP → graceful shutdown).
#   2. Resolves its own directory robustly so the script works regardless of CWD
#      or how it was invoked (symlink, sourced path, etc.).
#   3. exec's the binary in the FOREGROUND so the terminal owns the process.
#      Closing the terminal sends SIGHUP to this process group; the host's
#      PosixSignalRegistration.SIGHUP handler converts it to StopApplication(),
#      which triggers graceful DI disposal including llama-server child teardown
#      (no orphan process).
#
# Do NOT set OTEL_EXPORTER_OTLP_ENDPOINT here — the OTEL exporter is a no-op
# unless that variable is set, and desktop users do not want Aspire telemetry.
#
# Single-instance note: only one instance at a time should be started against
# the same user-data directory ($HOME/.local/share/XE-Local-AI-Engine). Running
# a second instance will race on the SQLite database and may corrupt data.

set -euo pipefail

# Resolve the directory that contains this script, following symlinks.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

BINARY="${SCRIPT_DIR}/XE-Local-AI-Engine.Client"

if [[ ! -f "${BINARY}" ]]; then
  echo "Error: binary not found at '${BINARY}'" >&2
  echo "Publish the app first:" >&2
  echo "  dotnet publish XE-Local-AI-Engine.Client -c Release -r linux-x64 -p:PublishProfile=linux-x64" >&2
  echo "Then copy or symlink the published binary next to this script." >&2
  exit 1
fi

if [[ ! -x "${BINARY}" ]]; then
  echo "Error: binary '${BINARY}' is not executable." >&2
  echo "Run: chmod +x '${BINARY}'" >&2
  exit 1
fi

export XE_LAUNCH_MODE=desktop

exec "${BINARY}"
