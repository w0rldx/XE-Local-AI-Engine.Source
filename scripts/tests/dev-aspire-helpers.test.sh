#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
PROJECT_ROOT="$(git -C "${SCRIPT_DIR}" rev-parse --show-toplevel)"
APPHOST="${PROJECT_ROOT}/XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj"
TEMP_ROOT="$(mktemp -d)"
trap 'rm -rf -- "${TEMP_ROOT}"' EXIT
mkdir -p "${TEMP_ROOT}/bin"

cat >"${TEMP_ROOT}/bin/aspire" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
printf '%q ' "$@" >>"${FAKE_ASPIRE_LOG}"
printf '\n' >>"${FAKE_ASPIRE_LOG}"
case "${1:-}" in
  ps)
    [[ "${FAKE_PS_MODE:-ok}" != "fail" ]] || exit 1
    [[ ! -f "${FAKE_PS_FAIL_MARKER}" ]] || exit 1
    if [[ "${FAKE_PS_MODE:-ok}" == "malformed" ]]; then printf '{not-json\n'; exit 0; fi
    if [[ -f "${FAKE_ASPIRE_STATE}" ]]; then
      printf '[{"appHostPath":"%s","appHostPid":%s,"status":"running","sdkVersion":"13.4.6","dashboardUrl":"https://localhost:12345/login?t=dashboard-secret"},{"appHostPath":"%s","appHostPid":999999,"status":"running","dashboardUrl":"https://localhost:9999/login?t=other-secret"}]\n' \
        "${FAKE_APPHOST}" "${FAKE_APPHOST_PID}" "${FAKE_OTHER_APPHOST}"
    else
      printf '[]\n'
    fi
    ;;
  start)
    sleep 0.1
    if [[ "${FAKE_START_MODE:-ok}" == "absent" || "${FAKE_START_MODE:-ok}" == "query-fail" ]]; then
      sleep 60 &
      printf '%s\n' "$!" >"${FAKE_LINGERING_PID}"
    fi
    [[ "${FAKE_START_MODE:-ok}" != "absent" ]] && : >"${FAKE_ASPIRE_STATE}"
    [[ "${FAKE_START_MODE:-ok}" != "query-fail" ]] || : >"${FAKE_PS_FAIL_MARKER}"
    [[ "${FAKE_START_MODE:-ok}" != "fail" ]] || exit 1
    printf '{"dashboardUrl":"https://localhost:12345/login?t=start-secret"}\n'
    ;;
  describe)
    [[ "${FAKE_DESCRIBE_MODE:-ok}" != "fail" ]] || exit 1
    if [[ "${FAKE_DESCRIBE_MODE:-ok}" == "malformed" ]]; then printf '{not-json\n'; exit 0; fi
    cat <<'JSON'
{"resources":[{"name":"app-random","displayName":"app","resourceType":"Project","state":"Running","healthStatus":"Healthy","environment":{"XE_NODE_SQLITE_KEY":"environment-secret"},"properties":{"resource.connectionString":"Data Source=secret"},"urls":[{"name":"https","url":"https://localhost:4567/?t=resource-secret"}]}]}
JSON
    ;;
  stop) rm -f -- "${FAKE_ASPIRE_STATE}" ;;
  *) exit 2 ;;
esac
FAKE
chmod 700 "${TEMP_ROOT}/bin/aspire"

export PATH="${TEMP_ROOT}/bin:${PATH}"
export FAKE_ASPIRE_LOG="${TEMP_ROOT}/aspire.log"
export FAKE_ASPIRE_STATE="${TEMP_ROOT}/running"
export FAKE_PS_FAIL_MARKER="${TEMP_ROOT}/ps-fail-after-start"
export FAKE_LINGERING_PID="${TEMP_ROOT}/lingering-pid"
export FAKE_APPHOST="${APPHOST}"
export FAKE_APPHOST_PID="$$"
export FAKE_OTHER_APPHOST="${PROJECT_ROOT}/../other-worktree/XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj"
export XE_ASPIRE_APPHOST="${APPHOST}"
mkdir -p "${TEMP_ROOT}/proc"
export XE_ASPIRE_PROC_ROOT="${TEMP_ROOT}/proc"

assert_not_contains() {
  local value="$1" forbidden="$2"
  [[ "${value}" != *"${forbidden}"* ]] || {
    echo "FAIL: output contained forbidden text: ${forbidden}" >&2
    exit 1
  }
}

assert_process_gone() {
  local pid="$1"
  for _ in $(seq 1 30); do
    [[ ! -e "/proc/${pid}" ]] && return 0
    [[ "$(awk '{ print $3 }' "/proc/${pid}/stat" 2>/dev/null || true)" == "Z" ]] && return 0
    sleep 0.1
  done
  echo "FAIL: lingering startup-session PID ${pid} survived cleanup" >&2
  return 1
}

set +e
stopped_output="$("${SCRIPT_DIR}/dev-status.sh" --json 2>&1)"
stopped_status=$?
set -e
[[ "${stopped_status}" -eq 3 && "${stopped_output}" == *'"status":"stopped"'* ]]

start_output="$("${SCRIPT_DIR}/dev-start.sh")"
assert_not_contains "${start_output}" "start-secret"
grep -q -- '--isolated' "${FAKE_ASPIRE_LOG}"
grep -Fq -- "--apphost ${APPHOST}" "${FAKE_ASPIRE_LOG}"

status_output="$("${SCRIPT_DIR}/dev-status.sh" --json)"
[[ "${status_output}" == *'"health": "Healthy"'* ]]
assert_not_contains "${status_output}" "dashboard-secret"
assert_not_contains "${status_output}" "resource-secret"
assert_not_contains "${status_output}" "environment-secret"
assert_not_contains "${status_output}" "connectionString"
assert_not_contains "${status_output}" "other-worktree"
assert_not_contains "${status_output}" "other-secret"

"${SCRIPT_DIR}/dev-stop.sh" --dry-run >/dev/null
[[ -f "${FAKE_ASPIRE_STATE}" ]]
"${SCRIPT_DIR}/dev-stop.sh" >/dev/null
[[ ! -f "${FAKE_ASPIRE_STATE}" ]]
grep -Fq -- "stop --apphost ${APPHOST} --non-interactive --nologo" "${FAKE_ASPIRE_LOG}"

export FAKE_PS_MODE=fail
start_count_before="$(grep -c '^start ' "${FAKE_ASPIRE_LOG}" || true)"
set +e
query_failure="$("${SCRIPT_DIR}/dev-start.sh" 2>&1)"
query_failure_status=$?
set -e
[[ "${query_failure_status}" -eq 4 ]]
[[ "${query_failure}" == *"refusing to launch"* ]]
[[ "$(grep -c '^start ' "${FAKE_ASPIRE_LOG}" || true)" -eq "${start_count_before}" ]]

set +e
smoke_failure="$("${SCRIPT_DIR}/aspire-readiness-smoke.sh" 2>&1)"
smoke_failure_status=$?
set -e
[[ "${smoke_failure_status}" -eq 4 ]]
[[ "${smoke_failure}" == *"refusing to start"* ]]
unset FAKE_PS_MODE

: >"${FAKE_ASPIRE_STATE}"
export FAKE_DESCRIBE_MODE=malformed
set +e
describe_failure="$("${SCRIPT_DIR}/dev-status.sh" --json 2>&1)"
describe_failure_status=$?
set -e
[[ "${describe_failure_status}" -eq 4 ]]
[[ "${describe_failure}" == *"malformed JSON"* ]]
assert_not_contains "${describe_failure}" '"resources": []'
unset FAKE_DESCRIBE_MODE
"${SCRIPT_DIR}/dev-stop.sh" >/dev/null

export FAKE_START_MODE=fail
set +e
partial_failure="$("${SCRIPT_DIR}/dev-start.sh" 2>&1)"
partial_failure_status=$?
set -e
[[ "${partial_failure_status}" -eq 1 ]]
[[ "${partial_failure}" == *"Aspire failed to start"* ]]
assert_not_contains "${partial_failure}" "dashboard-token-secret"
[[ ! -f "${FAKE_ASPIRE_STATE}" ]]
unset FAKE_START_MODE

export FAKE_START_MODE=absent
set +e
absent_failure="$("${SCRIPT_DIR}/dev-start.sh" 2>&1)"
absent_failure_status=$?
set -e
[[ "${absent_failure_status}" -eq 1 ]]
[[ "${absent_failure}" == *"was not registered"* ]]
assert_process_gone "$(cat "${FAKE_LINGERING_PID}")"
unset FAKE_START_MODE

export FAKE_START_MODE=query-fail
set +e
unreadable_failure="$("${SCRIPT_DIR}/dev-start.sh" 2>&1)"
unreadable_failure_status=$?
set -e
[[ "${unreadable_failure_status}" -eq 4 ]]
[[ "${unreadable_failure}" == *"state became unreadable"* ]]
assert_process_gone "$(cat "${FAKE_LINGERING_PID}")"
rm -f "${FAKE_ASPIRE_STATE}" "${FAKE_PS_FAIL_MARKER}"
unset FAKE_START_MODE

grep -Fq "trap 'exit 130' INT TERM" "${SCRIPT_DIR}/dev-start.sh"
grep -Fq "trap 'exit 130' INT TERM" "${SCRIPT_DIR}/aspire-readiness-smoke.sh"
grep -Fq "Startup session anchor identity changed; refusing to signal that session." "${SCRIPT_DIR}/dev-start.sh"
grep -Fq "chmod 700 \"\${private_dir}\"" "${SCRIPT_DIR}/dev-start.sh"
grep -Fq "rm -rf -- \"\${private_dir}\"" "${SCRIPT_DIR}/dev-start.sh"

echo "dev-aspire-helpers.test.sh: PASS"
