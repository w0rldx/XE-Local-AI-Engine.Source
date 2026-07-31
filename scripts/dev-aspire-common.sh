#!/usr/bin/env bash

# Shared, side-effect-free helpers for the worktree-scoped Aspire scripts.

set -uo pipefail

DEV_SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
if ! DEV_PROJECT_ROOT="$(git -C "${DEV_SCRIPT_DIR}" rev-parse --show-toplevel 2>/dev/null)"; then
  DEV_PROJECT_ROOT="$(cd "${DEV_SCRIPT_DIR}/.." && pwd -P)"
fi
DEV_APPHOST="${XE_ASPIRE_APPHOST:-${DEV_PROJECT_ROOT}/XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj}"
DEV_QUERY_TIMEOUT="${XE_ASPIRE_QUERY_TIMEOUT_SECONDS:-15}"

if command -v realpath >/dev/null 2>&1; then
  DEV_APPHOST="$(realpath -m "${DEV_APPHOST}")"
else
  DEV_APPHOST="$(cd "$(dirname "${DEV_APPHOST}")" && pwd -P)/$(basename "${DEV_APPHOST}")"
fi

dev_require_tools() {
  command -v aspire >/dev/null 2>&1 || {
    echo "[dev-aspire] Aspire CLI is required (https://aspire.dev/get-started/install-cli/)." >&2
    return 2
  }
  command -v python3 >/dev/null 2>&1 || {
    echo "[dev-aspire] python3 is required to parse and redact Aspire JSON output." >&2
    return 2
  }
  [[ -f "${DEV_APPHOST}" ]] || {
    echo "[dev-aspire] AppHost not found: ${DEV_APPHOST}" >&2
    return 2
  }
}

dev_aspire_ps_json() {
  timeout "${DEV_QUERY_TIMEOUT}s" aspire ps --format Json --non-interactive --nologo 2>/dev/null
}

dev_matching_app_json() {
  local raw
  raw="$(dev_aspire_ps_json)" || return 4
  DEV_APPHOST="${DEV_APPHOST}" python3 -c '
import json, os, sys
from urllib.parse import urlsplit, urlunsplit
target = os.path.realpath(os.environ["DEV_APPHOST"])
try:
    apps = json.load(sys.stdin)
except (json.JSONDecodeError, TypeError):
    raise SystemExit(4)
for app in apps if isinstance(apps, list) else []:
    path = app.get("appHostPath") or app.get("AppHostPath")
    if path and os.path.realpath(path) == target:
        dashboard = app.get("dashboardUrl") or ""
        try:
            parts = urlsplit(dashboard)
            dashboard = urlunsplit((parts.scheme, parts.netloc, parts.path, "", ""))
        except ValueError:
            dashboard = ""
        json.dump({
            "appHostPath": target,
            "appHostPid": app.get("appHostPid") or app.get("AppHostPid"),
            "status": app.get("status") or app.get("Status") or "unknown",
            "sdkVersion": app.get("sdkVersion") or app.get("SdkVersion"),
            "dashboardUrl": dashboard,
        }, sys.stdout)
        raise SystemExit(0)
raise SystemExit(3)
' <<<"${raw}"
}

dev_matching_app_pid() {
  dev_matching_app_json | python3 -c '
import json, sys
try:
    app = json.load(sys.stdin)
except (json.JSONDecodeError, TypeError):
    raise SystemExit(1)
pid = app.get("appHostPid") or app.get("AppHostPid")
if isinstance(pid, int) and pid > 1:
    print(pid)
else:
    raise SystemExit(1)
'
}

dev_safe_url() {
  python3 -c '
import sys
from urllib.parse import urlsplit, urlunsplit
value = sys.stdin.read().strip()
parts = urlsplit(value)
print(urlunsplit((parts.scheme, parts.netloc, parts.path, "", "")) if parts.scheme and parts.netloc else "")
'
}
