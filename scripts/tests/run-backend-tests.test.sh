#!/usr/bin/env bash
# Contract test for scripts/run-backend-tests.sh against a fake repository: nothing here builds or
# runs a real test project. `dotnet`, the build lock, the assembly guard and the memory-safe runner
# are all stubs that record their arguments.

set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
FAKE="$TMP/repo"
mkdir -p "$FAKE/scripts" "$FAKE/bin"
cp "$ROOT/scripts/run-backend-tests.sh" "$FAKE/scripts/"

write_solution() {
  {
    echo '<Solution>'
    local project
    for project in "$@"; do printf '    <Project Path="%s/%s.csproj"/>\n' "$project" "$project"; done
    echo '</Solution>'
  } >"$FAKE/XE-Local-AI-Engine.slnx"
}

ALL_PROJECTS=(
  XE-Local-AI-Engine.Tests
  XE-Local-AI-Engine.AI.Agent.Tests
  XE-Local-AI-Engine.Client.Persistence.Tests
  XE-Local-AI-Engine.Tests.E2ETests
)
write_solution "${ALL_PROJECTS[@]}"

cat >"$FAKE/scripts/with-build-lock.sh" <<'EOF'
#!/usr/bin/env bash
shift
export XE_BUILD_LOCK_HELD=1
exec "$@"
EOF

# `guard --test-bins -- <cmd>`: runs the command, then reports FAKE_GUARD_EXIT instead of its status.
cat >"$FAKE/scripts/assembly-guard.sh" <<'EOF'
#!/usr/bin/env bash
echo "guard $*" >>"$FAKE_LOG"
while (($#)); do [[ "$1" == "--" ]] && { shift; break; }; shift; done
"$@"
status=$?
[[ -n "${FAKE_GUARD_EXIT:-}" ]] && exit "$FAKE_GUARD_EXIT"
exit "$status"
EOF

cat >"$FAKE/scripts/run-tests-memory-safe.sh" <<'EOF'
#!/usr/bin/env bash
echo "runner NO_BUILD=${NO_BUILD:-} COVERAGE_DIR=${COVERAGE_DIR:-} JOBS=${JOBS:-} PAR=${PAR:-}" >>"$FAKE_LOG"
if [[ -n "${FAKE_RECORD_ORDER:-}" ]]; then
  echo runner-start >>"$FAKE_LOG"
  echo runner-end >>"$FAKE_LOG"
fi
# Slow AND deliberately TERM-resistant: only the KILL escalation can end this one. An ignored
# disposition is inherited, so its sleep resists TERM too.
if [[ -n "${FAKE_RUNNER_SLEEP:-}" ]]; then
  trap '' TERM
  echo "$$" >>"$FAKE_PIDS"
  sleep "$FAKE_RUNNER_SLEEP" & echo "$!" >>"$FAKE_PIDS"
  wait
  exit 0
fi
echo "TOTAL: pass=7 fail=0 peakRSS(any batch)=1MB"
echo "ALL NAMESPACE BATCHES GREEN"
exit "${FAKE_RUNNER_EXIT:-0}"
EOF

# Stands in for the whole `dotnet` CLI: only `test` is interesting, the rest just has to succeed.
cat >"$FAKE/bin/dotnet" <<'EOF'
#!/usr/bin/env bash
[[ "$1" == "test" ]] || exit 0
project="$2"; max=""; results=""; coverage=""
while (($#)); do
  case "$1" in
    --maximum-parallel-tests) max="$2"; shift 2 ;;
    --results-directory) results="$2"; shift 2 ;;
    --coverage) coverage=1; shift ;;
    *) shift ;;
  esac
done
echo "test project=$project max=$max results=$results coverage=$coverage" >>"$FAKE_LOG"
if [[ -n "${FAKE_RECORD_ORDER:-}" ]]; then
  echo "test-start $project" >>"$FAKE_LOG"
  echo "test-end $project" >>"$FAKE_LOG"
fi
# Slow mode for the cancellation case: register this process AND a grandchild, then block. The
# grandchild is the point — it proves the signal reached the whole process group, not just the lane.
if [[ -n "${FAKE_SLEEP:-}" ]]; then
  echo "$$" >>"$FAKE_PIDS"
  sleep "$FAKE_SLEEP" & echo "$!" >>"$FAKE_PIDS"
  wait
  exit 0
fi
[[ -n "${FAKE_NO_SUMMARY:-}" ]] && exit 1
cat <<'SUMMARY'
Test run summary: Passed!
  total: 3
  failed: 0
  succeeded: 3
SUMMARY
exit "${FAKE_TEST_EXIT:-0}"
EOF
chmod +x "$FAKE/scripts/with-build-lock.sh" "$FAKE/scripts/assembly-guard.sh" \
  "$FAKE/scripts/run-tests-memory-safe.sh" "$FAKE/bin/dotnet"
export PATH="$FAKE/bin:$PATH"

# Runs the gate with NO_BUILD=1 (the stub dotnet builds nothing) and captures output + status.
# Arguments after the case name are env assignments; GATE_ARGS carries the script's own flags.
GATE_ARGS=()
run_gate() {
  local name="$1"; shift
  : >"$TMP/$name.log"
  set +e
  output="$(FAKE_LOG="$TMP/$name.log" NO_BUILD=1 "$@" \
    "$FAKE/scripts/run-backend-tests.sh" ${GATE_ARGS[@]+"${GATE_ARGS[@]}"} 2>&1)"
  status=$?
  set -e
}

# `! grep` would silently skip errexit, so a miss has to be reported explicitly.
refute_grep() {
  if grep "$@" >/dev/null; then
    echo "unexpected match: grep $*" >&2
    exit 1
  fi
}

# --- enrolment: E2E excluded, the batched module goes to the runner, the rest to `dotnet test` ---
run_gate enrol env
# With a message, because a bare `[[ ]]` under `set -e` fails this file silently — and the gate's
# own output is the only thing that says why.
[[ "$status" -eq 0 ]] || { echo "enrolment run exited $status" >&2; printf '%s\n' "$output" >&2; exit 1; }
grep -Fq 'BACKEND GATE GREEN' <<<"$output"
grep -Fq 'Enrolled test project: XE-Local-AI-Engine.AI.Agent.Tests/' <<<"$output"
refute_grep -F 'E2ETests' <<<"$output"
refute_grep -F 'E2ETests' "$TMP/enrol.log"
grep -Fq 'runner NO_BUILD=1 COVERAGE_DIR=' "$TMP/enrol.log"
# One `dotnet test` per sibling, none for the batched module.
[[ "$(grep -c '^test project=' "$TMP/enrol.log")" -eq 2 ]]
refute_grep '^test project=XE-Local-AI-Engine.Tests/' "$TMP/enrol.log"
# Siblings are guarded and each gets its own results directory.
[[ "$(grep -c '^guard guard --test-bins --' "$TMP/enrol.log")" -eq 2 ]]
grep -q 'results=.*/XE-Local-AI-Engine.AI.Agent.Tests ' "$TMP/enrol.log"
grep -q 'results=.*/XE-Local-AI-Engine.Client.Persistence.Tests ' "$TMP/enrol.log"

# --- the normal exit path is clean: exit 0, GREEN last, and no shell diagnostics ---
# `set -u` plus a variable the exit path reads before it is initialised would print "unbound
# variable" AFTER the green line and exit 1 — a gate that reports success and then fails.
run_gate clean-exit env
[[ "$status" -eq 0 ]] || { echo "clean exit path returned $status" >&2; printf '%s\n' "$output" >&2; exit 1; }
last_line="$(printf '%s\n' "$output" | grep -v '^[[:space:]]*$' | tail -1)"
[[ "$last_line" == "BACKEND GATE GREEN" ]] || { echo "last line was '$last_line'" >&2; exit 1; }
refute_grep -E 'unbound variable|command not found|: line [0-9]+:' <<<"$output"

# --- width map: defaults, then per-project and global overrides ---
grep -q '^test project=XE-Local-AI-Engine.Client.Persistence.Tests/.* max=4 ' "$TMP/enrol.log"
grep -q '^test project=XE-Local-AI-Engine.AI.Agent.Tests/.* max=8 ' "$TMP/enrol.log"

run_gate widths env XE_TEST_WIDTH_Client_Persistence_Tests=2 XE_TEST_WIDTH_AI_Agent_Tests=3
[[ "$status" -eq 0 ]]
grep -q '^test project=XE-Local-AI-Engine.Client.Persistence.Tests/.* max=2 ' "$TMP/widths.log"
grep -q '^test project=XE-Local-AI-Engine.AI.Agent.Tests/.* max=3 ' "$TMP/widths.log"

run_gate width-default env XE_TEST_WIDTH_DEFAULT=2
[[ "$status" -eq 0 ]]
[[ "$(grep -c 'max=2 ' "$TMP/width-default.log")" -eq 2 ]]

run_gate width-invalid env XE_TEST_WIDTH_DEFAULT=nope
[[ "$status" -eq 2 ]]
grep -Fq 'must be a positive integer' <<<"$output"

# --- low-memory profile: conservative defaults and no overlap across any project lane ---
run_gate low-memory env XE_TEST_PROFILE=low-memory FAKE_RECORD_ORDER=1
[[ "$status" -eq 0 ]]
grep -Fq 'runner NO_BUILD=1 COVERAGE_DIR= JOBS=1 PAR=1' "$TMP/low-memory.log"
[[ "$(grep -c ' max=1 ' "$TMP/low-memory.log")" -eq 2 ]]
mapfile -t lane_receipts < <(grep -E '^(runner|test)-(start|end)' "$TMP/low-memory.log")
[[ "${#lane_receipts[@]}" -eq 6 ]]
for ((i = 0; i < 6; i += 2)); do
  [[ "${lane_receipts[i]}" == *-start* && "${lane_receipts[i+1]}" == *-end* ]]
done

# Explicit tuning still wins inside the profile.
run_gate low-memory-overrides env XE_TEST_PROFILE=low-memory JOBS=2 PAR=3 XE_TEST_WIDTH_DEFAULT=4
[[ "$status" -eq 0 ]]
grep -Fq 'runner NO_BUILD=1 COVERAGE_DIR= JOBS=2 PAR=3' "$TMP/low-memory-overrides.log"
[[ "$(grep -c ' max=4 ' "$TMP/low-memory-overrides.log")" -eq 2 ]]

run_gate low-memory-project-override env XE_TEST_PROFILE=low-memory XE_TEST_WIDTH_AI_Agent_Tests=3
[[ "$status" -eq 0 ]]
grep -q '^test project=XE-Local-AI-Engine.AI.Agent.Tests/.* max=3 ' "$TMP/low-memory-project-override.log"
grep -q '^test project=XE-Local-AI-Engine.Client.Persistence.Tests/.* max=1 ' "$TMP/low-memory-project-override.log"

# A red lane is recorded, but does not prevent the remaining project lanes from running.
run_gate low-memory-red env XE_TEST_PROFILE=low-memory FAKE_RUNNER_EXIT=1
[[ "$status" -eq 1 ]]
[[ "$(grep -c '^test project=' "$TMP/low-memory-red.log")" -eq 2 ]]

run_gate profile-invalid env XE_TEST_PROFILE=small
[[ "$status" -eq 2 ]]
grep -Fq "XE_TEST_PROFILE must be 'low-memory' or unset" <<<"$output"

# --- coverage mode: reports per project, and the siblings run UNguarded (static instrumentation) ---
run_gate coverage env COVERAGE_DIR="$TMP/cov"
[[ "$status" -eq 0 ]]
grep -q 'results=.*/cov/XE-Local-AI-Engine.AI.Agent.Tests coverage=1' "$TMP/coverage.log"
grep -Fq 'COVERAGE_DIR=' "$TMP/coverage.log"
grep -Fq "COVERAGE_DIR=$TMP/cov/XE-Local-AI-Engine.Tests" "$TMP/coverage.log"
refute_grep '^guard ' "$TMP/coverage.log"

# --- --siblings-only: the CI leg shape, no runner lane ---
GATE_ARGS=(--siblings-only)
run_gate siblings-only env
GATE_ARGS=()
[[ "$status" -eq 0 ]]
refute_grep '^runner ' "$TMP/siblings-only.log"
[[ "$(grep -c '^test project=' "$TMP/siblings-only.log")" -eq 2 ]]

# --- hollow-gate guard: a suite that prints no MTP summary is never green ---
run_gate hollow env FAKE_NO_SUMMARY=1
[[ "$status" -eq 1 ]]
grep -Fq 'produced no test-suite summary' <<<"$output"
grep -Fq 'no-summary' <<<"$output"

# --- exit codes: a failing suite is red, contamination (75) outranks it and is neither ---
run_gate failing env FAKE_TEST_EXIT=1
[[ "$status" -eq 1 ]]
grep -Fq 'FAILED: XE-Local-AI-Engine.AI.Agent.Tests(exit=1)' <<<"$output"

run_gate contaminated env FAKE_GUARD_EXIT=75
[[ "$status" -eq 75 ]]
grep -Fq 'RESULT VOID' <<<"$output"
refute_grep -F 'BACKEND GATE GREEN' <<<"$output"

# Contamination outranks a red in the same run.
run_gate contaminated-and-red env FAKE_GUARD_EXIT=75 FAKE_RUNNER_EXIT=1
[[ "$status" -eq 75 ]]

# --- the batched module must be enrolled, or the gate refuses to run ---
write_solution XE-Local-AI-Engine.AI.Agent.Tests XE-Local-AI-Engine.Client.Persistence.Tests
run_gate no-batched env
[[ "$status" -eq 2 ]]
grep -Fq 'is not enrolled' <<<"$output"

write_solution XE-Local-AI-Engine.Tests
run_gate no-siblings env
[[ "$status" -eq 2 ]]
grep -Fq 'no sibling test projects left' <<<"$output"

write_solution XE-Local-AI-Engine.Tests.E2ETests
run_gate only-e2e env
[[ "$status" -eq 2 ]]
grep -Fq 'zero test projects discovered' <<<"$output"

# --- invocation by a relative path from a subdirectory ---
# The script cds to its own repository root, so it must resolve its own path to an absolute one
# BEFORE that: re-exec'ing the caller's spelling through the lock wrapper would otherwise look for
# `./run-backend-tests.sh` under the repo root and die there. This is still the stubbed wrapper.
write_solution "${ALL_PROJECTS[@]}"
: >"$TMP/relative.log"
set +e
output="$(cd "$FAKE/scripts" && FAKE_LOG="$TMP/relative.log" NO_BUILD=1 ./run-backend-tests.sh 2>&1)"
status=$?
set -e
[[ "$status" -eq 0 ]] || { echo "relative invocation exited $status" >&2; printf '%s\n' "$output" >&2; exit 1; }
grep -Fq 'BACKEND GATE GREEN' <<<"$output"
[[ "$(grep -c '^test project=' "$TMP/relative.log")" -eq 2 ]]

# A relative COVERAGE_DIR is resolved against the caller's directory, not the repository root.
: >"$TMP/relative-cov.log"
( cd "$TMP" && FAKE_LOG="$TMP/relative-cov.log" NO_BUILD=1 COVERAGE_DIR=relcov \
    "$FAKE/scripts/run-backend-tests.sh" --siblings-only >/dev/null 2>&1 )
grep -q "results=$TMP/relcov/XE-Local-AI-Engine.AI.Agent.Tests " "$TMP/relative-cov.log"

# --- cancellation: signalling the gate's PROCESS GROUP must leave nothing running ---
# This case uses the REAL scripts/with-build-lock.sh. The wrapper runs its command in the foreground
# and has no traps, so a signal aimed at its PID alone would kill it and orphan the gate: the
# documented way to cancel a non-interactive run is to signal the process group, which is what a
# terminal's Ctrl-C does natively. `set -m` here is what gives the gate a group of its own to signal.
write_solution "${ALL_PROJECTS[@]}"
cp "$ROOT/scripts/with-build-lock.sh" "$FAKE/scripts/with-build-lock.sh"
LOCK="$TMP/build.lock"
PIDS_FILE="$TMP/lane-pids"
: >"$PIDS_FILE"
: >"$TMP/cancel.log"

# Kills anything this case leaves behind, on every exit path, before the temp dir goes away.
# Always succeeds: it is called on the success path too, and every kill in it is expected to find
# nothing once the gate has cleaned up after itself.
cancel_case_cleanup() {
  local pid
  # `|| true` on every kill: under `set -e` a kill that finds nothing already gone would end the
  # whole test file here, which is the one place it must not.
  while read -r pid; do kill -KILL "$pid" 2>/dev/null || true; done <"$PIDS_FILE"
  kill -KILL -- "-$PUBLIC" 2>/dev/null || true
  return 0
}

# The serialized profile waits inside the launch loop. Cancellation must terminate that active
# lane and exit without launching either later sibling.
: >"$PIDS_FILE"
: >"$TMP/low-cancel.log"
set -m
FAKE_LOG="$TMP/low-cancel.log" FAKE_PIDS="$PIDS_FILE" FAKE_RUNNER_SLEEP=120 NO_BUILD=1 \
  XE_TEST_PROFILE=low-memory BUILD_LOCK_FILE="$LOCK" XE_GATE_CANCEL_GRACE=2 \
  "$FAKE/scripts/run-backend-tests.sh" >"$TMP/low-cancel.out" 2>&1 &
PUBLIC=$!
set +m
deadline=$((SECONDS + 60))
while [[ "$(wc -l <"$PIDS_FILE")" -lt 2 ]]; do
  (( SECONDS <= deadline )) || { echo "serialized lane never started" >&2; cancel_case_cleanup; exit 1; }
  # real-timer: the subject is a real OS process group registering its child PIDs.
  sleep 0.2
done
mapfile -t LOW_LANE_PROCS <"$PIDS_FILE"
kill -TERM -- "-$PUBLIC"
set +e
wait "$PUBLIC"
low_cancel_status=$?
set -e
[[ "$low_cancel_status" -eq 143 ]] || { echo "low-memory cancel status was $low_cancel_status" >&2; cancel_case_cleanup; exit 1; }
refute_grep '^test project=' "$TMP/low-cancel.log"
deadline=$((SECONDS + 60))
while :; do
  survivors=()
  for pid in "${LOW_LANE_PROCS[@]}"; do kill -0 "$pid" 2>/dev/null && survivors+=("$pid"); done
  [[ "${#survivors[@]}" -eq 0 ]] && break
  (( SECONDS <= deadline )) || { echo "serialized lane survived cancellation: ${survivors[*]}" >&2; cancel_case_cleanup; exit 1; }
  # real-timer: the assertion waits for real OS processes to disappear after KILL escalation.
  sleep 0.5
done

# The parallel case below owns a fresh receipt set. Keeping the serialized case's two dead PIDs
# would let its six-PID readiness check pass after only four of the six new processes registered.
: >"$PIDS_FILE"

set -m
FAKE_LOG="$TMP/cancel.log" FAKE_PIDS="$PIDS_FILE" FAKE_SLEEP=120 FAKE_RUNNER_SLEEP=120 NO_BUILD=1 \
  BUILD_LOCK_FILE="$LOCK" XE_GATE_CANCEL_GRACE=2 \
  "$FAKE/scripts/run-backend-tests.sh" >"$TMP/cancel.out" 2>&1 &
PUBLIC=$!
set +m

# Three lanes, each registering itself and a grandchild.
deadline=$((SECONDS + 60))
while [[ "$(wc -l <"$PIDS_FILE")" -lt 6 ]]; do
  if (( SECONDS > deadline )); then
    echo "lanes never started: $(cat "$PIDS_FILE")" >&2
    cancel_case_cleanup
    exit 1
  fi
  sleep 0.2
done
mapfile -t LANE_PROCS <"$PIDS_FILE"

kill -TERM -- "-$PUBLIC"
set +e
wait "$PUBLIC"
cancel_status=$?
set -e
[[ "$cancel_status" -eq 143 ]] || {
  echo "cancel status was $cancel_status, wanted 143" >&2; cancel_case_cleanup; exit 1; }
refute_grep -F 'BACKEND GATE GREEN' "$TMP/cancel.out"
refute_grep -E 'unbound variable|command not found|: line [0-9]+:' "$TMP/cancel.out"

# The gate is still winding its lanes down when the wrapper's status arrives, and one lane ignores
# TERM outright, so give the grace period and the KILL escalation time to land — bounded, then fail.
deadline=$((SECONDS + 60))
while :; do
  survivors=()
  for pid in "${LANE_PROCS[@]}"; do
    kill -0 "$pid" 2>/dev/null && survivors+=("$pid")
  done
  [[ "${#survivors[@]}" -eq 0 ]] && break
  if (( SECONDS > deadline )); then
    echo "lane processes survived cancellation: ${survivors[*]}" >&2
    cancel_case_cleanup
    exit 1
  fi
  sleep 0.5
done
# Nothing may still hold the lock once the run is over.
flock -n "$LOCK" -c true || { echo "build lock still held after cancellation" >&2; exit 1; }

# --- a lane that had already finished CONTAMINATED outranks the cancellation status ---
# NO_BUILD_LOCK=1 so the gate itself is the process we signal: through the wrapper, a group signal
# kills that trapless foreground process first and we would read ITS 143, never the gate's verdict.
# The batched lane exits 75 immediately (it does no work at all), so its result is on disk well
# before the two sibling lanes have finished registering, which is what this case waits for.
write_solution "${ALL_PROJECTS[@]}"
: >"$PIDS_FILE"
: >"$TMP/promote.log"
set -m
FAKE_LOG="$TMP/promote.log" FAKE_PIDS="$PIDS_FILE" FAKE_SLEEP=120 FAKE_RUNNER_EXIT=75 \
  NO_BUILD=1 NO_BUILD_LOCK=1 XE_GATE_CANCEL_GRACE=2 \
  "$FAKE/scripts/run-backend-tests.sh" >"$TMP/promote.out" 2>&1 &
PUBLIC=$!
set +m

deadline=$((SECONDS + 60))
while [[ "$(wc -l <"$PIDS_FILE")" -lt 4 ]]; do
  if (( SECONDS > deadline )); then
    echo "sibling lanes never started: $(cat "$PIDS_FILE")" >&2
    cancel_case_cleanup
    exit 1
  fi
  sleep 0.2
done

kill -TERM -- "-$PUBLIC"
set +e
wait "$PUBLIC"
promote_status=$?
set -e
# 75, not 143: the assemblies moved under a lane, and "re-run me" outranks "you cancelled me".
[[ "$promote_status" -eq 75 ]] || {
  echo "cancelled run with a contaminated lane exited $promote_status, wanted 75" >&2
  cat "$TMP/promote.out" >&2
  cancel_case_cleanup
  exit 1
}
cancel_case_cleanup

# NOT tested, by construction: the LAUNCHING branch of request_cancel, which covers a signal landing
# between `lane &` and the `$!` that records it. That window is microseconds wide and cannot be hit
# deterministically from outside the script, so it is correctness by construction, not by test.

echo "run-backend-tests.test.sh: PASS"
