#!/usr/bin/env bash
# Stop only the Aspire instance registered for this checkout/worktree's AppHost.

set -euo pipefail

# shellcheck source=scripts/dev-aspire-common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)/dev-aspire-common.sh"

DRY_RUN=false
case "${1:-}" in
  "") ;;
  --dry-run) DRY_RUN=true ;;
  --help|-h)
    cat <<'EOF'
Usage: scripts/dev-stop.sh [--dry-run]

Stops only the Aspire instance whose canonical AppHost path belongs to the
current checkout/worktree. XE_ASPIRE_APPHOST may select another explicit
AppHost. It never uses `aspire stop --all` and never scans for another
worktree's processes.
EOF
    exit 0
    ;;
  *) echo "dev-stop: unknown argument: $1" >&2; exit 2 ;;
esac

dev_require_tools
PROC_ROOT="${XE_ASPIRE_PROC_ROOT:-/proc}"
[[ -d "${PROC_ROOT}" ]] || { echo "[dev-stop] Process root not found: ${PROC_ROOT}" >&2; exit 2; }

if app_json="$(dev_matching_app_json)"; then
  :
else
  query_status=$?
  if [[ "${query_status}" -ne 3 ]]; then
    echo "[dev-stop] Aspire state query failed or returned malformed JSON; refusing an unscoped fallback." >&2
    exit 4
  fi
  echo "[dev-stop] No running instance for ${DEV_APPHOST}."
  exit 0
fi

app_pid="$(python3 -c 'import json,sys; print(json.load(sys.stdin).get("appHostPid", ""))' <<<"${app_json}")"
[[ "${app_pid}" =~ ^[0-9]+$ ]] || { echo "[dev-stop] Refusing to stop an instance without a valid AppHost PID." >&2; exit 1; }
# Snapshot ownership before asking Aspire to stop. The AppHost and DCP registry
# can disappear before detached descendants do; selection afterwards would lose
# the only trustworthy relationship evidence.
protected_csv=""
ancestor="$$"
for _ in $(seq 1 30); do
  [[ "${ancestor}" =~ ^[0-9]+$ && "${ancestor}" -gt 1 ]] || break
  protected_csv="${protected_csv:+${protected_csv},}${ancestor}"
  ancestor="$(ps -o ppid= -p "${ancestor}" 2>/dev/null | tr -d ' ')"
done
process_snapshot="$(python3 "${DEV_SCRIPT_DIR}/dev-stop-select.py" --snapshot --proc-root "${PROC_ROOT}")"
mapfile -t scoped_records < <(
  printf '%s\n' "${process_snapshot}" | python3 "${DEV_SCRIPT_DIR}/dev-stop-select.py" \
        --apphost-pid "${app_pid}" \
        --apphost-path "${DEV_APPHOST}" \
        --protected "${protected_csv}"
)
declare -a scoped_pids=()
declare -A scoped_starttimes=()
for record in "${scoped_records[@]}"; do
  IFS=$'\t' read -r pid starttime <<<"${record}"
  [[ "${pid}" =~ ^[0-9]+$ && "${starttime}" =~ ^[0-9]+$ ]] || continue
  scoped_pids+=("${pid}")
  scoped_starttimes["${pid}"]="${starttime}"
done

identity_matches() {
  local pid="$1"
  python3 "${DEV_SCRIPT_DIR}/dev-stop-select.py" --proc-root "${PROC_ROOT}" \
    --identity-matches "${pid}" "${scoped_starttimes[${pid}]}" >/dev/null 2>&1
}

echo "[dev-stop] Target AppHost: ${DEV_APPHOST} (PID ${app_pid})"
if [[ "${DRY_RUN}" == "true" ]]; then
  echo "[dev-stop] --dry-run: no stop request sent."
  exit 0
fi

# The explicit AppHost path is the isolation boundary. Never use --all here.
timeout 30s aspire stop --apphost "${DEV_APPHOST}" --non-interactive --nologo >/dev/null 2>&1 || true

registration_gone=false
for _ in $(seq 1 20); do
  if dev_matching_app_json >/dev/null 2>&1; then
    :
  else
    query_status=$?
    if [[ "${query_status}" -eq 3 ]]; then
      registration_gone=true
      break
    fi
    echo "[dev-stop] Aspire state query failed during cleanup; refusing to infer that the instance stopped." >&2
    exit 4
  fi
  sleep 0.5
done

# Aspire 13.4 can unregister the AppHost while leaving detached DCP descendants.
# Signal only snapshot-selected survivors, even when the registry entry vanished.
survivors=()
for pid in "${scoped_pids[@]}"; do
  identity_matches "${pid}" && survivors+=("${pid}") || true
done
if [[ ${#survivors[@]} -gt 0 ]]; then
  echo "[dev-stop] Aspire 13.4 fallback: SIGTERM scoped survivors: ${survivors[*]}"
  for pid in "${survivors[@]}"; do
    identity_matches "${pid}" && kill -TERM "${pid}" 2>/dev/null || true
  done
  sleep 3
  for pid in "${survivors[@]}"; do
    identity_matches "${pid}" && kill -KILL "${pid}" 2>/dev/null || true
  done
  sleep 1
  for pid in "${survivors[@]}"; do
    if identity_matches "${pid}"; then
      echo "[dev-stop] Scoped PID ${pid} survived SIGKILL; refusing to claim full teardown." >&2
      exit 1
    fi
  done
fi

if dev_matching_app_json >/dev/null 2>&1; then
  echo "[dev-stop] Instance still registered after scoped fallback; refusing a broader kill." >&2
  exit 1
else
  query_status=$?
  if [[ "${query_status}" -ne 3 ]]; then
    echo "[dev-stop] Aspire state query failed after cleanup; full teardown cannot be proven." >&2
    exit 4
  fi
fi

# Success proves teardown only for the exact registered AppHost graph selected above. Processes
# outside that graph may belong to another worktree and are deliberately neither killed nor used
# to turn a successful scoped teardown into a global-machine failure.
if [[ "${registration_gone}" == "true" && ${#survivors[@]} -eq 0 ]]; then
  echo "[dev-stop] Stopped."
else
  echo "[dev-stop] Stopped with scoped fallback."
fi
