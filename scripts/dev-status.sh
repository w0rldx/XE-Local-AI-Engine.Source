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
DEV_APPHOST="${DEV_APPHOST}" STATUS_FORMAT="${FORMAT}" APP_JSON="${app_json}" python3 -c '
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
' <<<"${describe_json}"
