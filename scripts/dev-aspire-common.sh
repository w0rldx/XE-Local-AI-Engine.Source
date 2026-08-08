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

# The node operator secret for this checkout. It is the 32-byte root from which the SQLite column key,
# the node JWT signing key and the Data Protection KEK are all derived, so it must never be tracked and
# must stay stable: regenerating it makes every encrypted column and every protected blob written under
# the previous key unreadable. It lives beside the AppHost's own `.data` state and is named `node.key`
# so the repo-root `.gitignore` rule and the agent-workspace sensitive-file exclusion both already cover it.
DEV_NODE_OPERATOR_SECRET_FILE="${XE_NODE_OPERATOR_SECRET_FILE:-$(dirname "${DEV_APPHOST}")/.data/node.key}"

# Creates the per-checkout operator secret on first use and validates it on every later use. Prints the
# path, never the value. Returns 2 when an existing file is unusable, because silently replacing it would
# destroy the developer's encrypted dev data.
dev_ensure_node_operator_secret() {
  local secret_dir status=0
  secret_dir="$(dirname "${DEV_NODE_OPERATOR_SECRET_FILE}")"
  mkdir -p "${secret_dir}" || return 2
  DEV_NODE_OPERATOR_SECRET_FILE="${DEV_NODE_OPERATOR_SECRET_FILE}" python3 -c '
import base64, binascii, os, sys

path = os.environ["DEV_NODE_OPERATOR_SECRET_FILE"]
if os.path.exists(path):
    try:
        with open(path, "r", encoding="ascii") as handle:
            decoded = base64.b64decode(handle.read().strip(), validate=True)
    except (binascii.Error, OSError, UnicodeDecodeError, ValueError):
        decoded = b""
    if len(decoded) != 32:
        print(
            f"[dev-aspire] {path} is not a base64-encoded 32-byte node operator secret. "
            "Delete it to mint a new one, but note that every encrypted column and protected blob "
            "written under the old secret becomes unreadable.",
            file=sys.stderr,
        )
        raise SystemExit(2)
    os.chmod(path, 0o600)
    raise SystemExit(0)

descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
with os.fdopen(descriptor, "w", encoding="ascii") as handle:
    handle.write(base64.b64encode(os.urandom(32)).decode("ascii"))
print(f"[dev-aspire] Minted a new node operator secret at {path} (owner-only, never tracked).", file=sys.stderr)
raise SystemExit(10)
' || status=$?
  case "${status}" in
    0) return 0 ;;
    10) dev_warn_orphaned_dev_data; return 0 ;;
    *) return 2 ;;
  esac
}

# A first mint on a checkout that already has encrypted dev data means that data was written under a
# different secret — for this repo, the shared default that used to sit in the AppHost's tracked
# appsettings.Development.json. Nothing can recover it, and the node crashes on the first read rather
# than degrading, so name the files that have to go.
dev_warn_orphaned_dev_data() {
  local candidate
  local -a orphans=()
  for candidate in \
    "$(dirname "${DEV_APPHOST}")/.data/node-sqlite" \
    "$(dirname "${DEV_APPHOST}")/../XE-Local-AI-Engine.Client/dp-keys"; do
    [[ -e "${candidate}" ]] && orphans+=("$(cd "$(dirname "${candidate}")" && pwd -P)/$(basename "${candidate}")")
  done
  [[ "${#orphans[@]}" -eq 0 ]] && return 0
  echo "[dev-aspire] This checkout already holds encrypted dev data written under a different operator" >&2
  echo "[dev-aspire] secret. It cannot be decrypted with the new one and the node will fail on first read" >&2
  echo "[dev-aspire] (AuthenticationTagMismatchException). Delete it to start clean:" >&2
  for candidate in "${orphans[@]}"; do
    echo "[dev-aspire]   rm -rf -- ${candidate}" >&2
  done
  echo "[dev-aspire] Encrypted *.enc credential files beside the key ring are orphaned with it." >&2
}

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
