#!/usr/bin/env bash
# Show a filtered, token-free view of this worktree's Aspire instance.

set -euo pipefail

# shellcheck source=scripts/dev-aspire-common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)/dev-aspire-common.sh"

FORMAT=table
case "${1:-}" in
  "") ;;
  --json) FORMAT=json ;;
  --help|-h)
    echo "Usage: scripts/dev-status.sh [--json]"
    exit 0
    ;;
  *) echo "dev-status: unknown argument: $1" >&2; exit 2 ;;
esac

dev_require_tools
if app_json="$(dev_matching_app_json)"; then
  :
else
  query_status=$?
  if [[ "${query_status}" -ne 3 ]]; then
    echo "[dev-status] Aspire state query failed or returned malformed JSON; no status inferred." >&2
    exit 4
  fi
  if [[ "${FORMAT}" == json ]]; then
    printf '{"appHostPath":"%s","status":"stopped","resources":[]}\n' "${DEV_APPHOST}"
  else
    echo "[dev-status] stopped  ${DEV_APPHOST}"
  fi
  exit 3
fi

if ! describe_json="$(timeout "${DEV_QUERY_TIMEOUT}s" aspire describe --apphost "${DEV_APPHOST}" --format Json --non-interactive --nologo 2>/dev/null)"; then
  echo "[dev-status] Aspire resource query failed; refusing to emit an incomplete status." >&2
  exit 4
fi

# The app origin serves the SPA bundle last BUILT into the Client's wwwroot (the Client build copies the React
# dist/ there, keeping its mtime), not the working tree; the client-react (Vite) origin serves src/ live. Warn,
# never fail, when src/ has changed since that build: validating on the Vite origin is legitimate. Tests are
# not in the bundle, so *.test.* files and src/test/ do not count.
spa_root="$(dirname "$(dirname "${DEV_APPHOST}")")"
spa_bundle="${spa_root}/XE-Local-AI-Engine.Client/wwwroot/index.html"
spa_rebuild="use the client-react (Vite) origin, or run pnpm run build in XE-Local-AI-Engine.Client.React and restart with scripts/dev-stop.sh and scripts/dev-start.sh"
# Nothing in here may change the exit code or suppress the status: a missing or unreadable src/ (an AppHost in
# another checkout) or bundle just means no warning. NUL-separated, so a file name with a newline cannot
# split the "<mtime> <path>" record.
spa_warning=""
newest_spa_source="$(find "${spa_root}/XE-Local-AI-Engine.Client.React/src" -type f ! -name '*.test.*' \
  ! -path '*/src/test/*' -printf '%T@ %P\0' 2>/dev/null | sort -z -n | tail -z -n 1 | tr -d '\0')" || newest_spa_source=""
if [[ "${newest_spa_source}" =~ ^[0-9]+ ]]; then
  if [[ ! -f "${spa_bundle}" ]]; then
    spa_warning="the app origin has no built SPA bundle (${spa_bundle} is missing); ${spa_rebuild}"
  elif spa_bundle_mtime="$(stat -c %Y "${spa_bundle}" 2>/dev/null)" \
      && (( spa_bundle_mtime < BASH_REMATCH[0] )); then
    spa_warning="the app origin serves a SPA bundle built before src/${newest_spa_source#* } changed; ${spa_rebuild}"
  fi
fi

SPA_WARNING="${spa_warning}" DEV_APPHOST="${DEV_APPHOST}" STATUS_FORMAT="${FORMAT}" APP_JSON="${app_json}" python3 -c '
import json, os, sys
from urllib.parse import urlsplit, urlunsplit

def safe_url(value):
    try:
        p = urlsplit(str(value))
        return urlunsplit((p.scheme, p.netloc, p.path, "", "")) if p.scheme and p.netloc else ""
    except ValueError:
        return ""

try:
    app = json.loads(os.environ["APP_JSON"])
except json.JSONDecodeError:
    raise SystemExit("filtered status failed: invalid Aspire app JSON")
try:
    detail = json.load(sys.stdin)
except json.JSONDecodeError:
    print("[dev-status] Aspire resource query returned malformed JSON; no status emitted.", file=sys.stderr)
    raise SystemExit(4)
resources = []
for item in detail.get("resources", []) if isinstance(detail, dict) else []:
    urls = []
    for entry in item.get("urls", []) or []:
        url = safe_url(entry.get("url", "")) if isinstance(entry, dict) else ""
        if url:
            urls.append({"name": entry.get("name") or entry.get("displayName") or "endpoint", "url": url})
    resources.append({
        "name": item.get("displayName") or item.get("name") or "unknown",
        "type": item.get("resourceType") or "unknown",
        "state": item.get("state") or "unknown",
        "health": item.get("healthStatus") or "unknown",
        "urls": urls,
    })
result = {
    "appHostPath": os.environ["DEV_APPHOST"],
    "pid": app.get("appHostPid"),
    "status": app.get("status") or "unknown",
    "sdkVersion": app.get("sdkVersion"),
    "dashboardUrl": safe_url(app.get("dashboardUrl", "")),
    "resources": resources,
    "spaBundleWarning": os.environ["SPA_WARNING"] or None,
}
if os.environ["STATUS_FORMAT"] == "json":
    json.dump(result, sys.stdout, indent=2)
    print()
else:
    print("[dev-status] {}  pid={}  apphost={}".format(result["status"], result["pid"], result["appHostPath"]))
    if result["dashboardUrl"]:
        print("[dev-status] dashboard={} (login token intentionally omitted)".format(result["dashboardUrl"]))
    for resource in resources:
        endpoints = ", ".join(x["url"] for x in resource["urls"])
        suffix = f"  {endpoints}" if endpoints else ""
        print("  {:<20} {:<12} health={}{}".format(resource["name"], resource["state"], resource["health"], suffix))
    if result["spaBundleWarning"]:
        print("[dev-status] WARNING: " + result["spaBundleWarning"])
' <<<"${describe_json}"
