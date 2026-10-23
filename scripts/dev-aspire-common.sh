#!/usr/bin/env bash

# Shared, side-effect-free helpers for the worktree-scoped Aspire scripts.

set -uo pipefail

DEV_SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
# The checkout is the directory above this script, by construction (scripts/ sits at the repo root). Never ask
# git: `git -C` honours an inherited GIT_DIR or GIT_WORK_TREE (a private index, another checkout), would answer
# with THAT repository, and dev-stop.sh would then target another checkout's AppHost, the cross-worktree stop this
# script exists to prevent.
DEV_PROJECT_ROOT="$(cd "${DEV_SCRIPT_DIR}/.." && pwd -P)"
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
# Only a LIST of application objects is a valid answer. Any other valid JSON ({}, null, a string, a list holding
# non-objects) is a malformed query, status 4, never "no instance" (3): dev-stop.sh treats 3 as proven absence and
# would exit 0 with the stack still running, or confirm a teardown that never happened.
if not isinstance(apps, list) or not all(isinstance(app, dict) for app in apps):
    raise SystemExit(4)
for app in apps:
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

# The checkout's current loopback URLs for tools that cannot guess the per-start port: written by dev-start.sh once the
# host is up, removed by dev-stop.sh, reported (and flagged stale) by dev-status.sh. It lives in the .tmp/ of the
# checkout that owns the AppHost and never holds a token. XE_DEV_ENDPOINTS_FILE overrides the path (tests).
DEV_ENDPOINTS_FILE="${XE_DEV_ENDPOINTS_FILE:-$(dirname "$(dirname "${DEV_APPHOST}")")/.tmp/dev-endpoints.json}"

# Reads `dev-status.sh --json` on stdin and writes DEV_ENDPOINTS_FILE atomically. Only loopback URLs are kept; returns 1
# without writing when there is no loopback `app` URL or no AppHost PID.
dev_write_endpoints() {
  DEV_ENDPOINTS_FILE="${DEV_ENDPOINTS_FILE}" DEV_CHECKOUT="$(dirname "$(dirname "${DEV_APPHOST}")")" python3 -c '
import datetime, ipaddress, json, os, sys
from urllib.parse import urlsplit, urlunsplit

def loopback(url):
    try:
        parts = urlsplit(str(url))
        host = parts.hostname or ""
        if host != "localhost" and not ipaddress.ip_address(host).is_loopback:
            return None
    except ValueError:
        return None
    return urlunsplit((parts.scheme, parts.netloc, parts.path, "", ""))

try:
    status = json.load(sys.stdin)
except json.JSONDecodeError:
    raise SystemExit(1)
urls = [u for r in status.get("resources", []) if r.get("name") == "app"
        for u in (loopback(x.get("url")) for x in r.get("urls", [])) if u]
app_url = next((u for u in urls if u.startswith("https:")), urls[0] if urls else None)
pid = status.get("pid")
if not app_url or not isinstance(pid, int) or pid <= 1:
    raise SystemExit(1)
path = os.environ["DEV_ENDPOINTS_FILE"]
os.makedirs(os.path.dirname(path), exist_ok=True)
document = {
    "appUrl": app_url,
    "dashboardUrl": loopback(status.get("dashboardUrl") or ""),
    "pid": pid,
    "startedAt": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    "worktree": os.environ["DEV_CHECKOUT"],
}
with open(path + ".tmp", "w", encoding="utf-8") as handle:
    json.dump(document, handle, indent=2)
    handle.write("\n")
os.replace(path + ".tmp", path)
'
}

# Prints `null` when DEV_ENDPOINTS_FILE is absent, else {"path", "pid", "stale"}: stale when the recorded PID is not a
# live process (a crashed host or a stop that bypassed dev-stop.sh) or the file is unreadable.
dev_endpoints_state_json() {
  DEV_ENDPOINTS_FILE="${DEV_ENDPOINTS_FILE}" python3 -c '
import json, os
path = os.environ["DEV_ENDPOINTS_FILE"]
if not os.path.exists(path):
    print("null")
    raise SystemExit
try:
    with open(path, encoding="utf-8") as handle:
        pid = json.load(handle).get("pid")
except (OSError, ValueError, AttributeError):
    pid = None
alive = False
if isinstance(pid, int) and pid > 1:
    try:
        os.kill(pid, 0)
        alive = True
    except PermissionError:
        alive = True
    except (ProcessLookupError, OverflowError):
        pass
print(json.dumps({"path": path, "pid": pid, "stale": not alive}))
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
