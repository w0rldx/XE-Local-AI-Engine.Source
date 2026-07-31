#!/usr/bin/env bash
# Bounded live readiness smoke for this worktree's isolated Aspire AppHost.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
TIMEOUT_SECONDS="${XE_ASPIRE_SMOKE_TIMEOUT_SECONDS:-180}"
CLEANUP_ARMED=false

cleanup() {
  local status=$?
  trap - EXIT
  if [[ "${CLEANUP_ARMED}" == "true" ]]; then
    "${SCRIPT_DIR}/dev-stop.sh" || {
      echo "[aspire-smoke] Cleanup failed for this worktree's AppHost." >&2
      [[ "${status}" -ne 0 ]] || status=1
    }
  fi
  exit "${status}"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

if "${SCRIPT_DIR}/dev-status.sh" >/dev/null 2>&1; then
  echo "[aspire-smoke] Refusing to reuse or stop an existing instance. Stop it first." >&2
  exit 2
else
  status_result=$?
  if [[ "${status_result}" -ne 3 ]]; then
    echo "[aspire-smoke] Could not establish that this AppHost is stopped; refusing to start." >&2
    exit 4
  fi
fi

CLEANUP_ARMED=true
"${SCRIPT_DIR}/dev-start.sh"

# Use Aspire's resource readiness contract rather than an ad-hoc HTTP poll.
timeout "$((TIMEOUT_SECONDS + 10))s" aspire wait app \
  --apphost "${XE_ASPIRE_APPHOST:-${SCRIPT_DIR}/../XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj}" \
  --status healthy --timeout "${TIMEOUT_SECONDS}" --non-interactive --nologo

"${SCRIPT_DIR}/dev-status.sh"
echo "[aspire-smoke] PASS: app reached healthy state within ${TIMEOUT_SECONDS}s."
