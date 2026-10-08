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
# The lab-data scan reads the real main checkout's .tmp; it is exercised on a fake checkout at the end.
export XE_DEV_STATUS_SKIP_LAB_SCAN=1

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

# Survivor grace: the fake /proc anchors the AppHost PID (this shell) and links each survivor's stat and
# cmdline to its real /proc entry, so a survivor drops out of the snapshot's identity check once it exits.
mkdir -p "${XE_ASPIRE_PROC_ROOT}/$$"
printf '%s (bash) S 1 %s %s 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 1000\n' "$$" "$$" "$$" >"${XE_ASPIRE_PROC_ROOT}/$$/stat"
printf 'bash' >"${XE_ASPIRE_PROC_ROOT}/$$/cmdline"
link_survivor() {
  local pid="$1"
  for _ in $(seq 1 50); do
    [[ "$(cat "/proc/${pid}/cmdline" 2>/dev/null | tr '\0' ' ')" == sleep* ]] && break
    sleep 0.1
  done
  mkdir -p "${XE_ASPIRE_PROC_ROOT}/${pid}"
  ln -s "/proc/${pid}/stat" "${XE_ASPIRE_PROC_ROOT}/${pid}/stat"
  ln -s "/proc/${pid}/cmdline" "${XE_ASPIRE_PROC_ROOT}/${pid}/cmdline"
}
now_ms() { date +%s%3N; }

sleep 60 &
draining_pid=$!
link_survivor "${draining_pid}"
: >"${FAKE_ASPIRE_STATE}"
started_ms="$(now_ms)"
grace_output="$(XE_DEV_STOP_GRACE_SECONDS=20 "${SCRIPT_DIR}/dev-stop.sh")"
elapsed_ms=$(( $(now_ms) - started_ms ))
[[ "${grace_output}" == *"SIGTERM scoped survivors: ${draining_pid}"* ]]
assert_not_contains "${grace_output}" "SIGKILL"
# A survivor that exits on SIGTERM ends the wait early, well inside the 20 s budget.
(( elapsed_ms < 4000 )) || { echo "FAIL: grace wait did not exit early (${elapsed_ms} ms)" >&2; exit 1; }
assert_process_gone "${draining_pid}"
rm -rf -- "${XE_ASPIRE_PROC_ROOT:?}/${draining_pid}"

( trap '' TERM; exec sleep 60 ) &
stubborn_pid=$!
link_survivor "${stubborn_pid}"
: >"${FAKE_ASPIRE_STATE}"
started_ms="$(now_ms)"
grace_output="$(XE_DEV_STOP_GRACE_SECONDS=1 "${SCRIPT_DIR}/dev-stop.sh")"
elapsed_ms=$(( $(now_ms) - started_ms ))
[[ "${grace_output}" == *"Grace period of 1s elapsed; SIGKILL scoped survivors: ${stubborn_pid}"* ]]
(( elapsed_ms >= 1000 )) || { echo "FAIL: SIGKILL came before the grace budget (${elapsed_ms} ms)" >&2; exit 1; }
assert_process_gone "${stubborn_pid}"
rm -rf -- "${XE_ASPIRE_PROC_ROOT:?}/${stubborn_pid}" "${XE_ASPIRE_PROC_ROOT:?}/$$"

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

# The SPA bundle the app origin serves is the one last built into the Client's wwwroot. dev-status derives both
# paths from the AppHost it reports on, so a fake checkout with dated files drives the staleness warning: a JSON
# field (the output stays valid JSON) and one table line, never a failure.
SPA_ROOT="${TEMP_ROOT}/spa-root"
SPA_APPHOST="${SPA_ROOT}/XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj"
SPA_BUNDLE="${SPA_ROOT}/XE-Local-AI-Engine.Client/wwwroot/index.html"
SPA_SRC="${SPA_ROOT}/XE-Local-AI-Engine.Client.React/src"
mkdir -p "$(dirname "${SPA_APPHOST}")" "$(dirname "${SPA_BUNDLE}")" "${SPA_SRC}/test"
: >"${SPA_APPHOST}"
printf 'app\n' >"${SPA_SRC}/App.tsx"
touch -d '2026-01-02' "${SPA_SRC}/App.tsx"
printf 'bundle\n' >"${SPA_BUNDLE}"
touch -d '2026-01-01' "${SPA_BUNDLE}"
: >"${FAKE_ASPIRE_STATE}"
spa_status() { FAKE_APPHOST="${SPA_APPHOST}" XE_ASPIRE_APPHOST="${SPA_APPHOST}" "${SCRIPT_DIR}/dev-status.sh" "$@"; }
spa_warning() { python3 -c 'import json, sys; print(json.load(sys.stdin)["spaBundleWarning"] or "")' <<<"$1"; }

stale_json="$(spa_status --json)"
[[ "$(spa_warning "${stale_json}")" == "the app origin serves a SPA bundle built before src/App.tsx changed; use the client-react (Vite) origin, or run pnpm run build"* ]]
[[ "$(spa_status)" == *"[dev-status] WARNING: the app origin serves a SPA bundle built before src/App.tsx changed"* ]]

touch -d '2026-01-03' "${SPA_BUNDLE}"
[[ -z "$(spa_warning "$(spa_status --json)")" ]]
[[ "$(spa_status)" != *"WARNING"* ]]

# Tests are not in the bundle: editing one does not make it stale.
printf 'test\n' >"${SPA_SRC}/App.test.tsx"
printf 'setup\n' >"${SPA_SRC}/test/Setup.ts"
touch -d '2026-01-05' "${SPA_SRC}/App.test.tsx" "${SPA_SRC}/test/Setup.ts"
[[ -z "$(spa_warning "$(spa_status --json)")" ]]

rm -f "${SPA_BUNDLE}"
[[ "$(spa_warning "$(spa_status --json)")" == "the app origin has no built SPA bundle (${SPA_BUNDLE} is missing);"* ]]

# The check is advisory: with no frontend src/ at all (an AppHost in a checkout without one) the status is
# still printed, the exit code is the plain one, and the JSON stays valid.
rm -rf "${SPA_SRC}"
for format in --json ""; do
  set +e
  no_src_output="$(spa_status ${format:+"${format}"} 2>&1)"
  no_src_status=$?
  set -e
  [[ "${no_src_status}" -eq 0 ]] || { echo "FAIL: dev-status ${format} exited ${no_src_status} without src/: ${no_src_output}" >&2; exit 1; }
done
[[ -z "$(spa_warning "$(spa_status --json)")" ]]
[[ "$(spa_status)" == *"health=Healthy"* ]]
rm -f "${FAKE_ASPIRE_STATE}"

# Lab data under the MAIN checkout's .tmp/: a fake checkout with a linked worktree under .tmp/worktrees runs the
# worktree's copy of dev-status, which must still list the main checkout's .tmp and every linked worktree's
# .tmp/lab. A fake du reports each directory's
# size and newest date from marker files; a `.hang` marker makes it hang until the scan timeout.
LAB_MAIN="${TEMP_ROOT}/lab-main"
LAB_WT="${LAB_MAIN}/.tmp/worktrees/wt"
git init -q "${LAB_MAIN}"
git -C "${LAB_MAIN}" worktree add -q --orphan -b lab-wt "${LAB_WT}" 2>/dev/null
mkdir -p "${LAB_WT}/scripts/lib" "${LAB_WT}/XE-Local-AI-Engine.AppHost" "${TEMP_ROOT}/du-bin"
cp "${SCRIPT_DIR}/dev-status.sh" "${SCRIPT_DIR}/dev-aspire-common.sh" "${LAB_WT}/scripts/"
cp "${SCRIPT_DIR}/lib/"*.sh "${LAB_WT}/scripts/lib/"
LAB_APPHOST="${LAB_WT}/XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj"
: >"${LAB_APPHOST}"
cat >"${TEMP_ROOT}/du-bin/du" <<'FAKE'
#!/usr/bin/env bash
dir="${*: -1}"
[[ ! -f "${dir}/.hang" ]] || exec sleep 30  # real-timer: stands in for a walk the scan timeout must cut short
printf '%s\t%s\t%s\n' "$(cat "${dir}/.gb" 2>/dev/null || echo 1)" "$(cat "${dir}/.newest" 2>/dev/null || echo 2026-01-01)" "${dir}"
FAKE
chmod 700 "${TEMP_ROOT}/du-bin/du"
lab_dir() {  # [LAB_IN=checkout] lab_dir NAME GB [OWNER-lines]
  local tmp="${LAB_IN:-${LAB_MAIN}}/.tmp"
  mkdir -p "${tmp}/$1"
  echo "$2" >"${tmp}/$1/.gb"
  echo 2026-10-01 >"${tmp}/$1/.newest"
  [[ -z "${3:-}" ]] || printf '%b' "$3" >"${tmp}/$1/OWNER"
}
# lab-up.sh creates a lab under the checkout it runs from: a second linked worktree outside the main .tmp holds labs.
LAB_WT2="${TEMP_ROOT}/lab-wt2"
git -C "${LAB_MAIN}" worktree add -q --orphan -b lab-wt2 "${LAB_WT2}" 2>/dev/null
LAB_IN="${LAB_WT2}" lab_dir lab/wt-owned 6 'plan=plan-c\ndelete_after=2999-01-01\n'
LAB_IN="${LAB_WT2}" lab_dir lab/wt-unowned 7
LAB_IN="${LAB_WT2}" lab_dir lab/wt-small 1
lab_dir owned-lab 5 'plan=plan-a\ncreated=2026-10-01T00:00:00Z\ndelete_after=2999-01-01\n'
lab_dir unowned-data 3
lab_dir expired-lab 2 'plan=old-plan\ndelete_after=2000-01-01\n'
lab_dir small-dir 1 'plan=plan-s\ndelete_after=2999-01-01\n'
lab_dir lab/default 4 'plan=plan-b\ndelete_after=2999-01-01\n'
lab_dir logs 9
lab_dir slow-dir 1
: >"${LAB_MAIN}/.tmp/slow-dir/.hang"
lab_status() {
  env XE_DEV_STATUS_SKIP_LAB_SCAN="${LAB_SKIP:-0}" PATH="${TEMP_ROOT}/du-bin:${PATH}" XE_DEV_STATUS_LAB_SCAN_TIMEOUT_SECONDS=1 \
    FAKE_APPHOST="${LAB_APPHOST}" XE_ASPIRE_APPHOST="${LAB_APPHOST}" "${LAB_WT}/scripts/dev-status.sh" "$@"
}
set +e
lab_table="$(lab_status)"
lab_table_status=$?
lab_json="$(lab_status --json)"
set -e
[[ "${lab_table_status}" -eq 3 ]] || { echo "FAIL: stopped dev-status with lab scan exited ${lab_table_status}" >&2; exit 1; }
expect_line() {
  grep -Eq -- "$1" <<<"${lab_table}" || { echo "FAIL: lab table lacks /$1/:" >&2; echo "${lab_table}" >&2; exit 1; }
}
expect_line "^\[dev-status\] Lab data under ${LAB_MAIN}/\.tmp/ and linked worktrees' \.tmp/lab/ \(over 1 GB\):$"
expect_line '^  owned-lab +5G  newest=2026-10-01  plan=plan-a delete_after=2999-01-01$'
expect_line '^  unowned-data +3G  newest=2026-10-01  UNOWNED$'
expect_line '^  expired-lab +2G  newest=2026-10-01  plan=old-plan delete_after=2000-01-01 EXPIRED$'
expect_line '^  lab/default +4G  newest=2026-10-01  plan=plan-b delete_after=2999-01-01$'
expect_line '^  slow-dir +\?G  newest=\?  UNOWNED$'
expect_line '^  lab-wt2/lab/wt-owned +6G  newest=2026-10-01  plan=plan-c delete_after=2999-01-01$'
expect_line '^  lab-wt2/lab/wt-unowned +7G  newest=2026-10-01  UNOWNED$'
lab_table_rows="$(grep '^  ' <<<"${lab_table}")"
assert_not_contains "${lab_table_rows}" "small-dir"
assert_not_contains "${lab_table_rows}" "wt-small"
assert_not_contains "${lab_table_rows}" "logs"
assert_not_contains "${lab_table_rows}" "worktrees"
[[ "$(grep -c '^  ' <<<"${lab_table}")" -eq 7 ]] || { echo "FAIL: expected 7 lab rows: ${lab_table}" >&2; exit 1; }
lab_rows() {
  python3 -c '
import json, sys
for row in json.load(sys.stdin)["labData"]:
    print(row["checkout"], row["name"], row["sizeGb"], row["newest"], row["plan"], row["deleteAfter"], row["status"])
' | sort
}
expected_rows="$(printf '%s\n' 'main expired-lab 2 2026-10-01 old-plan 2000-01-01 expired' \
  'main lab/default 4 2026-10-01 plan-b 2999-01-01 owned' 'main owned-lab 5 2026-10-01 plan-a 2999-01-01 owned' \
  'main slow-dir None None None None unowned' 'main unowned-data 3 2026-10-01 None None unowned' \
  'lab-wt2 lab/wt-owned 6 2026-10-01 plan-c 2999-01-01 owned' \
  'lab-wt2 lab/wt-unowned 7 2026-10-01 None None unowned' | sort)"
[[ "$(lab_rows <<<"${lab_json}")" == "${expected_rows}" ]] \
  || { echo "FAIL: stopped --json labData: ${lab_json}" >&2; exit 1; }
: >"${FAKE_ASPIRE_STATE}"
[[ "$(lab_status --json | lab_rows)" == "${expected_rows}" ]] \
  || { echo "FAIL: running --json lacks the labData rows" >&2; exit 1; }
[[ "$(lab_status)" == *"health=Healthy"*"  unowned-data "* ]] || { echo "FAIL: running table lacks the lab section" >&2; exit 1; }
rm -f "${FAKE_ASPIRE_STATE}"
skipped_json="$(LAB_SKIP=1 lab_status --json || true)"
[[ "${skipped_json}" == *'"labData":null'* ]] || { echo "FAIL: skip variable did not skip: ${skipped_json}" >&2; exit 1; }
assert_not_contains "$(LAB_SKIP=1 lab_status || true)" "Lab data"
# Porcelain output quotes a worktree path holding a tab; the scan names that worktree on stderr, never hides it.
LAB_WT_TAB="${TEMP_ROOT}/lab"$'\t'"tab"
git -C "${LAB_MAIN}" worktree add -q --orphan -b lab-wt-tab "${LAB_WT_TAB}" 2>/dev/null
LAB_IN="${LAB_WT_TAB}" lab_dir lab/tab-lab 8
tab_stderr="$(lab_status --json 2>&1 >"${TEMP_ROOT}/tab-lab.json" || true)"
[[ "${tab_stderr}" == *"WARN: worktree path needs quoting, skipped: "*"lab"*"tab"* ]] \
  || { echo "FAIL: a tab-holding worktree path was skipped silently: ${tab_stderr}" >&2; exit 1; }
[[ "$(lab_rows <"${TEMP_ROOT}/tab-lab.json")" == "${expected_rows}" ]] \
  || { echo "FAIL: the tab-holding worktree changed the other lab rows" >&2; exit 1; }
git -C "${LAB_MAIN}" worktree remove --force "${LAB_WT_TAB}"

grep -Fq "trap 'exit 130' INT TERM" "${SCRIPT_DIR}/dev-start.sh"
grep -Fq "trap 'exit 130' INT TERM" "${SCRIPT_DIR}/aspire-readiness-smoke.sh"
grep -Fq "Startup session anchor identity changed; refusing to signal that session." "${SCRIPT_DIR}/dev-start.sh"
grep -Fq "chmod 700 \"\${private_dir}\"" "${SCRIPT_DIR}/dev-start.sh"
grep -Fq "rm -rf -- \"\${private_dir}\"" "${SCRIPT_DIR}/dev-start.sh"

echo "dev-aspire-helpers.test.sh: PASS"
