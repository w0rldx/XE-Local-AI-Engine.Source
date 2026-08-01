#!/usr/bin/env bash
# Start this checkout/worktree's AppHost in isolated mode.

set -euo pipefail

# shellcheck source=scripts/dev-aspire-common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)/dev-aspire-common.sh"

NO_BUILD=false
case "${1:-}" in
  "") ;;
  --no-build) NO_BUILD=true ;;
  --help|-h)
    cat <<'EOF'
Usage: scripts/dev-start.sh [--no-build]

Starts the current checkout/worktree's explicit AppHost with Aspire isolated
mode, randomized ports, isolated user secrets, and non-interactive output.
EOF
    exit 0
    ;;
  *) echo "dev-start: unknown argument: $1" >&2; exit 2 ;;
esac

dev_require_tools
command -v setsid >/dev/null 2>&1 || { echo "[dev-start] setsid is required for transactional startup cleanup." >&2; exit 2; }
dev_ensure_node_operator_secret || exit 2
if dev_matching_app_json >/dev/null 2>&1; then
  echo "[dev-start] This worktree's AppHost is already running." >&2
  "${DEV_SCRIPT_DIR}/dev-status.sh"
  exit 1
else
  query_status=$?
  if [[ "${query_status}" -ne 3 ]]; then
    echo "[dev-start] Could not query Aspire state safely; refusing to launch a possibly duplicate instance." >&2
    exit 4
  fi
fi

args=(start --apphost "${DEV_APPHOST}" --isolated --format Json --non-interactive --nologo)
[[ "${NO_BUILD}" == "true" ]] && args+=(--no-build)

# `--non-interactive` means Aspire can never prompt for the required `node-sqlite-key` parameter, and
# `--isolated` means it does not read the normal user-secrets store, so the value has to arrive as
# configuration. Aspire resolves the parameter from the key `Parameters:node-sqlite-key`, whose
# environment-variable form is `Parameters__node-sqlite-key`. bash cannot `export` a name containing
# dashes, and routing it through `env` would publish the secret in world-readable /proc/<pid>/cmdline,
# so python3 (already a hard requirement of these scripts) reads the owner-only key file, sets the
# variable in its own environment, and execs the CLI in place — the secret only ever exists in a
# process environment, which is readable by its own user alone.
seed_operator_secret_py='
import os, sys

with open(sys.argv[1], "r", encoding="ascii") as handle:
    os.environ[sys.argv[2]] = handle.read().strip()
os.execvp(sys.argv[3], sys.argv[3:])
'

# Aspire's JSON includes a dashboard login token. Keep all startup coordination files in one
# owner-only directory, never echo the raw output, and emit only the filtered status helper output.
private_dir="$(mktemp -d)"
chmod 700 "${private_dir}"
private_output="${private_dir}/aspire-output"
start_status_file="${private_dir}/start-status"
start_release_file="${private_dir}/release"
: >"${private_output}"
chmod 600 "${private_output}"
START_ATTEMPTED=false
START_SESSION_ID=""
START_CLI_PID=""
START_CLI_STARTTIME=""
START_PROC_ROOT="${XE_ASPIRE_START_PROC_ROOT:-/proc}"

# Called by cleanup(), which is itself invoked indirectly by the EXIT trap.
# shellcheck disable=SC2329
rollback_start_session() {
  [[ "${START_SESSION_ID}" =~ ^[0-9]+$ ]] || return 0
  if [[ ! "${START_CLI_PID}" =~ ^[0-9]+$ || ! "${START_CLI_STARTTIME}" =~ ^[0-9]+$ ]] \
    || ! python3 "${DEV_SCRIPT_DIR}/dev-stop-select.py" --proc-root "${START_PROC_ROOT}" \
      --identity-matches "${START_CLI_PID}" "${START_CLI_STARTTIME}" >/dev/null 2>&1; then
    echo "[dev-start] Startup session anchor identity changed; refusing to signal that session." >&2
    return 1
  fi
  local snapshot record pid starttime
  local -a records=() survivors=()
  snapshot="$(python3 "${DEV_SCRIPT_DIR}/dev-stop-select.py" --snapshot --proc-root "${START_PROC_ROOT}")"
  mapfile -t records < <(
    printf '%s\n' "${snapshot}" | python3 "${DEV_SCRIPT_DIR}/dev-stop-select.py" \
      --session-id "${START_SESSION_ID}"
  )
  for record in "${records[@]}"; do
    IFS=$'\t' read -r pid starttime <<<"${record}"
    python3 "${DEV_SCRIPT_DIR}/dev-stop-select.py" --proc-root "${START_PROC_ROOT}" \
      --identity-matches "${pid}" "${starttime}" >/dev/null 2>&1 \
      && kill -TERM "${pid}" 2>/dev/null || true
  done
  sleep 0.25
  for record in "${records[@]}"; do
    IFS=$'\t' read -r pid starttime <<<"${record}"
    if python3 "${DEV_SCRIPT_DIR}/dev-stop-select.py" --proc-root "${START_PROC_ROOT}" \
      --identity-matches "${pid}" "${starttime}" >/dev/null 2>&1; then
      kill -KILL "${pid}" 2>/dev/null || true
      survivors+=("${pid}:${starttime}")
    fi
  done
  for _ in $(seq 1 20); do
    local any_alive=false
    for record in "${survivors[@]}"; do
      IFS=: read -r pid starttime <<<"${record}"
      if python3 "${DEV_SCRIPT_DIR}/dev-stop-select.py" --proc-root "${START_PROC_ROOT}" \
        --identity-matches "${pid}" "${starttime}" >/dev/null 2>&1; then
        any_alive=true
      fi
    done
    [[ "${any_alive}" == "false" ]] && return 0
    sleep 0.1
  done
  for record in "${survivors[@]}"; do
    IFS=: read -r pid starttime <<<"${record}"
    if python3 "${DEV_SCRIPT_DIR}/dev-stop-select.py" --proc-root "${START_PROC_ROOT}" \
      --identity-matches "${pid}" "${starttime}" >/dev/null 2>&1; then
      return 1
    fi
  done
  return 0
}

release_start_anchor() {
  if ! python3 "${DEV_SCRIPT_DIR}/dev-stop-select.py" --proc-root "${START_PROC_ROOT}" \
    --identity-matches "${START_CLI_PID}" "${START_CLI_STARTTIME}" >/dev/null 2>&1; then
    echo "[dev-start] Startup session anchor disappeared before commit." >&2
    return 1
  fi
  : >"${start_release_file}"
  chmod 600 "${start_release_file}"
  local wrapper_status=0
  wait "${START_CLI_PID}" || wrapper_status=$?
  return "${wrapper_status}"
}

# Invoked indirectly by the EXIT trap immediately below.
# shellcheck disable=SC2329
cleanup() {
  local status=$?
  trap - EXIT
  if [[ "${status}" -ne 0 && "${START_ATTEMPTED}" == "true" ]]; then
    local rollback_status=0 stop_status=0
    rollback_start_session || rollback_status=$?
    "${DEV_SCRIPT_DIR}/dev-stop.sh" >/dev/null 2>&1 || stop_status=$?
    if [[ "${rollback_status}" -ne 0 || "${stop_status}" -ne 0 ]]; then
      echo "[dev-start] Startup failed and cleanup could not prove complete scoped teardown." >&2
    fi
  fi
  rm -rf -- "${private_dir}"
  exit "${status}"
}
trap cleanup EXIT
trap 'exit 130' INT TERM
START_ATTEMPTED=true
# The single-quoted body is intentionally expanded only inside the session-leader wrapper.
# shellcheck disable=SC2016
setsid bash -c '
  set +e
  status_file="$1"
  release_file="$2"
  shift 2
  "$@"
  command_status=$?
  umask 077
  printf "%s\n" "${command_status}" >"${status_file}.tmp"
  mv -f -- "${status_file}.tmp" "${status_file}"
  while [[ ! -e "${release_file}" ]]; do sleep 0.05; done
  exit "${command_status}"
' _ "${start_status_file}" "${start_release_file}" \
  python3 -c "${seed_operator_secret_py}" \
  "${DEV_NODE_OPERATOR_SECRET_FILE}" "Parameters__node-sqlite-key" aspire "${args[@]}" \
  >"${private_output}" 2>&1 &
START_CLI_PID=$!
START_SESSION_ID="${START_CLI_PID}"
for _ in $(seq 1 20); do
  start_snapshot="$(python3 "${DEV_SCRIPT_DIR}/dev-stop-select.py" --snapshot --proc-root "${START_PROC_ROOT}")"
  START_CLI_STARTTIME="$(awk -F '\t' -v pid="${START_CLI_PID}" '$1 == pid { print $4; exit }' <<<"${start_snapshot}")"
  [[ "${START_CLI_STARTTIME}" =~ ^[0-9]+$ ]] && break
  sleep 0.01
done
if [[ ! "${START_CLI_STARTTIME}" =~ ^[0-9]+$ ]]; then
  echo "[dev-start] Could not establish the isolated Aspire start-session identity; aborting." >&2
  exit 1
fi
while [[ ! -s "${start_status_file}" ]]; do
  if ! python3 "${DEV_SCRIPT_DIR}/dev-stop-select.py" --proc-root "${START_PROC_ROOT}" \
    --identity-matches "${START_CLI_PID}" "${START_CLI_STARTTIME}" >/dev/null 2>&1; then
    echo "[dev-start] Aspire start-session anchor exited without publishing a result." >&2
    exit 1
  fi
  sleep 0.02
done
start_status="$(tr -d '[:space:]' <"${start_status_file}")"
[[ "${start_status}" =~ ^[0-9]+$ ]] || {
  echo "[dev-start] Aspire start-session published an invalid result." >&2
  exit 1
}
if [[ "${start_status}" -ne 0 ]]; then
  echo "[dev-start] Aspire failed to start ${DEV_APPHOST}; inspect ~/.aspire/logs for the scoped CLI log." >&2
  exit 1
fi

for _ in $(seq 1 30); do
  if dev_matching_app_json >/dev/null 2>&1; then
    if "${DEV_SCRIPT_DIR}/dev-status.sh"; then
      if ! release_start_anchor; then
        echo "[dev-start] Could not commit the validated startup session." >&2
        exit 1
      fi
      START_ATTEMPTED=false
      exit 0
    fi
  else
    query_status=$?
    if [[ "${query_status}" -ne 3 ]]; then
      echo "[dev-start] Aspire state became unreadable after launch; attempting scoped cleanup." >&2
      exit 4
    fi
  fi
  sleep 0.5
done
echo "[dev-start] Aspire returned success but the scoped AppHost was not registered." >&2
exit 1
