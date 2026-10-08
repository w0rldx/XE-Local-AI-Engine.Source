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

# Lab data under the MAIN checkout's .tmp/ (shared by every worktree, located like the build lock): every direct
# subdirectory over 1 GiB, with its owner from <dir>/OWNER (written by scripts/lab-up.sh) or UNOWNED. lab/ holds
# one lab per profile, so its children are listed instead. One `du` walk per directory gives size and newest
# change, each bounded by XE_DEV_STATUS_LAB_SCAN_TIMEOUT_SECONDS. Skipped with XE_DEV_STATUS_SKIP_LAB_SCAN=1 and
# when stdout is /dev/null (a caller that only wants the exit code). scripts/lab-up.sh creates a lab under the
# checkout it runs from, so each linked worktree's .tmp/lab/<profile> is listed too, with the worktree as checkout.
# shellcheck source=scripts/lib/build-lock-common.sh
source "${DEV_SCRIPT_DIR}/lib/build-lock-common.sh"
LAB_TMP="$(dirname "$(build_lock_shared_path "${DEV_SCRIPT_DIR}")")"
lab_scan_row() {  # lab_scan_row DIR NAME CHECKOUT TODAY
  local dir="$1" size newest plan="" delete_after="" state=UNOWNED
  # A walk that times out reports nothing: the directory is listed with an unknown size rather than hidden.
  IFS=$'\t' read -r size newest _ < <(timeout "${XE_DEV_STATUS_LAB_SCAN_TIMEOUT_SECONDS:-10}s" \
    du -s --time --time-style=+%Y-%m-%d --block-size=1G -- "${dir}" 2>/dev/null) || true
  [[ "${size:-}" =~ ^[0-9]+$ ]] || size="?"
  [[ "${size}" == "?" || "${size}" -gt 1 ]] || return 0
  if [[ -f "${dir}/OWNER" ]]; then
    plan="$(sed -n 's/^plan=//p' "${dir}/OWNER" | head -n 1)"
    delete_after="$(sed -n 's/^delete_after=//p' "${dir}/OWNER" | head -n 1)"
    state=owned
    [[ -z "${delete_after}" || ! "${delete_after}" < "$4" ]] || state=EXPIRED
  fi
  # Unit-separated: a tab is IFS whitespace, so `read` would collapse the empty plan fields of an unowned row.
  printf '%s\x1f%s\x1f%s\x1f%s\x1f%s\x1f%s\x1f%s\n' "$2" "${size}" "${newest:-?}" "${plan}" "${delete_after}" "${state}" "$3"
}
lab_scan_tsv() {
  local dir name today main worktree
  today="$(date -u +%Y-%m-%d)"
  main="$(dirname "${LAB_TMP}")"
  shopt -s nullglob
  for dir in "${LAB_TMP}"/*/ "${LAB_TMP}"/lab/*/; do
    dir="${dir%/}"
    name="${dir#"${LAB_TMP}/"}"
    case "${name}" in lab|worktrees|backend-test-results|logs|build.lock*) continue ;; esac
    lab_scan_row "${dir}" "${name}" main "${today}"
  done
  # The first porcelain entry is the main checkout, scanned above. NUL-separated records carry paths unquoted; a
  # tab or newline cannot ride in a row, so that worktree is named on stderr instead of silently hidden.
  while IFS= read -r -d '' worktree; do
    [[ "${worktree}" == "worktree "* ]] || continue
    worktree="${worktree#worktree }"
    if [[ "${worktree}" == *[$'\t\n']* ]]; then
      printf 'WARN: worktree path needs quoting, skipped: %q\n' "${worktree}" >&2
      continue
    fi
    [[ "$(realpath -- "${worktree}" 2>/dev/null)" != "$(realpath -- "${main}")" ]] || continue
    for dir in "${worktree}"/.tmp/lab/*/; do
      dir="${dir%/}"
      lab_scan_row "${dir}" "lab/${dir##*/}" "$(basename "${worktree}")" "${today}"
    done
  done < <(xe_git -C "${main}" worktree list --porcelain -z 2>/dev/null)
  shopt -u nullglob
}
print_lab_table() {
  [[ "${LAB_SCANNED}" == true ]] || return 0
  echo "[dev-status] Lab data under ${LAB_TMP}/ and linked worktrees' .tmp/lab/ (over 1 GB):"
  [[ -n "${LAB_TSV}" ]] || { echo "  (none)"; return 0; }
  local name size newest plan delete_after state checkout owner
  while IFS=$'\x1f' read -r name size newest plan delete_after state checkout; do
    [[ "${checkout}" == main ]] || name="${checkout}/${name}"
    if [[ "${state}" == UNOWNED ]]; then owner=UNOWNED; else owner="plan=${plan} delete_after=${delete_after:-?}"; fi
    [[ "${state}" != EXPIRED ]] || owner+=" EXPIRED"
    printf '  %-28s %5sG  newest=%s  %s\n' "${name}" "${size}" "${newest}" "${owner}"
  done <<<"${LAB_TSV}"
}

dev_require_tools
LAB_TSV=""
LAB_SCANNED=false
if [[ "${XE_DEV_STATUS_SKIP_LAB_SCAN:-0}" != 1 && "$(readlink "/proc/$$/fd/1" 2>/dev/null)" != /dev/null ]]; then
  LAB_TSV="$(lab_scan_tsv)"
  LAB_SCANNED=true
fi
LAB_JSON="$(LAB_SCANNED="${LAB_SCANNED}" python3 -c '
import json, os, sys
if os.environ["LAB_SCANNED"] != "true":
    print("null")
    raise SystemExit
rows = []
for line in filter(None, sys.stdin.read().splitlines()):
    name, size, newest, plan, delete_after, state, checkout = line.split("\x1f")
    rows.append({"checkout": checkout, "name": name, "sizeGb": int(size) if size.isdigit() else None,
                 "newest": None if newest == "?" else newest, "plan": plan or None,
                 "deleteAfter": delete_after or None, "status": state.lower()})
print(json.dumps(rows))
' <<<"${LAB_TSV}")"
if app_json="$(dev_matching_app_json)"; then
  :
else
  query_status=$?
  if [[ "${query_status}" -ne 3 ]]; then
    echo "[dev-status] Aspire state query failed or returned malformed JSON; no status inferred." >&2
    exit 4
  fi
  if [[ "${FORMAT}" == json ]]; then
    printf '{"appHostPath":"%s","status":"stopped","resources":[],"labData":%s}\n' "${DEV_APPHOST}" "${LAB_JSON}"
  else
    echo "[dev-status] stopped  ${DEV_APPHOST}"
    print_lab_table
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

SPA_WARNING="${spa_warning}" LAB_JSON="${LAB_JSON}" DEV_APPHOST="${DEV_APPHOST}" STATUS_FORMAT="${FORMAT}" APP_JSON="${app_json}" python3 -c '
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
    "labData": json.loads(os.environ["LAB_JSON"]),
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
[[ "${FORMAT}" == json ]] || print_lab_table
