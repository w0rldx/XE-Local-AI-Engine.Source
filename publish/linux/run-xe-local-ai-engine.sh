#!/usr/bin/env bash
# The shell routes --browser, --headless and operator commands to the standalone engine.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BINARY="${SCRIPT_DIR}/XE-Local-AI-Engine.Desktop"
if [[ ! -x "${BINARY}" ]]; then
  echo "Error: native launcher is missing or not executable at '${BINARY}'. Extract the complete Linux package." >&2
  exit 1
fi
export XE_LAUNCH_MODE=desktop
exec "${BINARY}" "$@"
