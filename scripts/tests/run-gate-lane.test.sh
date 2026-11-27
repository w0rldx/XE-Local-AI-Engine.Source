#!/usr/bin/env bash
# Contract test for scripts/run-gate-lane.sh against a fake repository: the build lock, pnpm and
# openapi-live-check.sh are stubs that record how they were started, and every history row lands in the
# fake repository's .tmp, never in this checkout's.

set -uo pipefail

ROOT="$(cd "$(dirname "$(realpath "${BASH_SOURCE[0]}")")/../.." && pwd)"
TMP="$(realpath "$(mktemp -d)")"
trap 'rm -rf "$TMP"' EXIT

# Every git call runs with the caller's repository selectors and config removed and discovery bounded by
# $TMP, so the fixture can never reach the enclosing repository. The refused variables are cleared so the
# caller's shell does not decide a case.
SCRUB=()
for var in $(compgen -v GIT_) $(compgen -v FAKE_) XE_BUILD_LOCK_HELD XE_GATE_LANE_STATE XE_OPENAPI_LIVE_LOCKED \
  OPENAPI_LIVE_SKIP_BUILD OPENAPI_LIVE_SCRIPT OPENAPI_LIVE_CLIENT_RELEASE_ROOT OPENAPI_LIVE_TIMEOUT_SECONDS \
  XE_ALLOW_NODE_DRIFT XE_I18N_MISSING_KEYS BUILD_LOCK_FILE; do
  SCRUB+=(-u "$var")
done
SCRUB+=(GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1 GIT_CEILING_DIRECTORIES="$TMP" PATH="$TMP/bin:$PATH")
fg() { env "${SCRUB[@]}" git "$@"; }

MAIN="$TMP/main" WT="$TMP/wt" LOG="$TMP/calls.log" HISTORY="$TMP/main/.tmp/gate-history.log"
mkdir -p "$MAIN/scripts/lib" "$MAIN/XE-Local-AI-Engine.Client.React" "$TMP/bin"
cp "$ROOT/scripts/run-gate-lane.sh" "$MAIN/scripts/"
cp "$ROOT"/scripts/lib/*.sh "$MAIN/scripts/lib/"
# The lock stub records the call, fails with FAKE_LOCK_EXIT like a lock that was not acquired, else marks
# the lock held as the real wrapper does and runs the command.
cat >"$MAIN/scripts/with-build-lock.sh" <<'STUB'
#!/usr/bin/env bash
printf 'lock %s\n' "$*" >>"$FAKE_LOG"
[[ -z "${FAKE_LOCK_EXIT:-}" ]] || exit "$FAKE_LOCK_EXIT"
[[ "$1" == -- ]] && shift
export XE_BUILD_LOCK_HELD="$0.lock"
exec "$@"
STUB
# One lane stub for pnpm and openapi-live-check.sh: its argv, working directory, lock marker and the one
# allowed variable; FAKE_MOVE_HEAD commits in that checkout, FAKE_DIRTY creates that file, FAKE_LANE_EXIT is its
# exit code.
cat >"$TMP/bin/pnpm" <<'STUB'
#!/usr/bin/env bash
printf 'lane %s argv=[%s] cwd=%s held=%s timeout=%s\n' "$(basename "$0")" "$*" "$PWD" "${XE_BUILD_LOCK_HELD:+yes}" \
  "${OPENAPI_LIVE_TIMEOUT_SECONDS:-}" >>"$FAKE_LOG"
[[ -z "${FAKE_MOVE_HEAD:-}" ]] || git -C "$FAKE_MOVE_HEAD" commit -q --allow-empty -m moved
[[ -z "${FAKE_DIRTY:-}" ]] || touch "$FAKE_DIRTY"
exit "${FAKE_LANE_EXIT:-0}"
STUB
cp "$TMP/bin/pnpm" "$MAIN/scripts/openapi-live-check.sh"
chmod +x "$MAIN/scripts/with-build-lock.sh" "$MAIN/scripts/openapi-live-check.sh" "$TMP/bin/pnpm"
printf '.tmp/\n' >"$MAIN/.gitignore"
touch "$MAIN/XE-Local-AI-Engine.Client.React/.keep"
fg -C "$MAIN" init -q -b develop
fg -C "$MAIN" config user.email fixture@example.invalid
fg -C "$MAIN" config user.name "Contract Fixture"
fg -C "$MAIN" add -A
fg -C "$MAIN" commit -q -m base
fg -C "$MAIN" worktree add -q -b feature "$WT"

failures=0
fail() { echo "FAIL $1"; sed 's/^/    /' "$TMP/out"; failures=$((failures + 1)); }
rows() { if [[ -f "$HISTORY" ]]; then wc -l <"$HISTORY"; else echo 0; fi; }
# <checkout> [env assignments...] -- <args...>: runs that checkout's copy; sets status.
run_lane() {
  local dir="$1"; shift
  local -a envs=()
  while [[ $# -gt 0 && "$1" != -- ]]; do envs+=("$1"); shift; done
  shift
  : >"$LOG"
  env "${SCRUB[@]}" FAKE_LOG="$LOG" ${envs[@]+"${envs[@]}"} "$dir/scripts/run-gate-lane.sh" "$@" >"$TMP/out" 2>&1
  status=$?
}
# <name> <want exit> <want rows>: exit code, row count, and the lane stub not started when no row is wanted.
expect() {
  [[ "$status" -eq "$2" ]] || fail "$1: exit $status, wanted $2"
  [[ "$(rows)" -eq "$3" ]] || fail "$1: $(rows) history rows, wanted $3"
}
# <name> <worktree> <tip> <lane> <lane-exit>: the newest row, field by field (time and wall by shape).
expect_row() {
  local -a f
  IFS=$'\t' read -r -a f < <(tail -1 "$HISTORY")
  [[ "${#f[@]}" -eq 6 && "${f[0]}" =~ ^20[0-9]{2}-[0-9]{2}-[0-9]{2}T && "${f[1]}" == "$2" && "${f[2]}" == "$3" \
    && "${f[3]}" == "lane=$4" && "${f[4]}" =~ ^wall=[0-9]+$ && "${f[5]}" == "lane-exit=$5" ]] \
    || fail "$1: row '$(tail -1 "$HISTORY")', wanted $2 $3 lane=$4 lane-exit=$5"
  # Never an exit= field: a reader taking the newest row ending exit=0 as the backend row must skip it.
  [[ "$(tail -1 "$HISTORY")" != *$'\t'exit=* ]] || fail "$1: lane row carries an exit= field"
}
TIP="$(fg -C "$MAIN" rev-parse HEAD)"

# Usage: no lane, a second argument, an unknown lane. Nothing runs, nothing is recorded.
for args in "" "frontend extra" "backend"; do
  # shellcheck disable=SC2086
  run_lane "$MAIN" -- $args
  expect "usage '$args'" 2 0
  [[ ! -s "$LOG" ]] || fail "usage '$args': something ran"
done

# Each override that skips a check or changes what runs is refused, even empty. A lock marker on entry would
# pass the wrapper through unlocked; the re-run under the stub lock (every case below) still carries one.
for var in OPENAPI_LIVE_SKIP_BUILD OPENAPI_LIVE_SCRIPT OPENAPI_LIVE_CLIENT_RELEASE_ROOT XE_ALLOW_NODE_DRIFT \
  XE_I18N_MISSING_KEYS BUILD_LOCK_FILE XE_BUILD_LOCK_HELD; do
  for value in 1 ""; do
    run_lane "$MAIN" "$var=$value" -- openapi
    expect "refused $var='$value'" 2 0
    grep -Fq "$var is set" "$TMP/out" || fail "refused $var: variable not named"
    [[ ! -s "$LOG" ]] || fail "refused $var: something ran"
  done
done

# frontend: the fixed pnpm command from the repository root, inside the lock; one green row.
run_lane "$MAIN" -- frontend
expect "frontend green" 0 1
expect_row "frontend green" "$MAIN" "$TIP" frontend 0
grep -Fxq "lane pnpm argv=[-C $MAIN/XE-Local-AI-Engine.Client.React run acceptance] cwd=$MAIN held=yes timeout=" "$LOG" \
  || fail "frontend: pnpm not started as fixed"
grep -Fxq "lock -- $MAIN/scripts/run-gate-lane.sh frontend" "$LOG" || fail "frontend: lock not taken once around the lane"

# openapi: the live check with no arguments (compare mode); the timeout variable passes through.
run_lane "$MAIN" OPENAPI_LIVE_TIMEOUT_SECONDS=300 -- openapi
expect "openapi green" 0 2
expect_row "openapi green" "$MAIN" "$TIP" openapi 0
grep -Fxq "lane openapi-live-check.sh argv=[] cwd=$MAIN held=yes timeout=300" "$LOG" || fail "openapi: check not started as fixed"

# The lane's exit code is the script's and the row's.
run_lane "$MAIN" FAKE_LANE_EXIT=3 -- frontend
expect "lane red" 3 3
expect_row "lane red" "$MAIN" "$TIP" frontend 3

# A dirty tree when the lock was taken, and HEAD moving during the run, suffix the tip.
printf 'x\n' >"$MAIN/stray.txt"
run_lane "$MAIN" -- frontend
expect "dirty" 0 4
expect_row "dirty" "$MAIN" "$TIP+dirty" frontend 0
rm "$MAIN/stray.txt"
run_lane "$MAIN" FAKE_MOVE_HEAD="$MAIN" -- openapi
expect "moved" 0 5
expect_row "moved" "$MAIN" "$TIP+moved" openapi 0
TIP="$(fg -C "$MAIN" rev-parse HEAD)"

# The lock not acquired: 69, recorded, the lane never started.
run_lane "$MAIN" FAKE_LOCK_EXIT=69 -- frontend
expect "lock timeout" 69 6
expect_row "lock timeout" "$MAIN" "$TIP" frontend 69
grep -q '^lane ' "$LOG" && fail "lock timeout: the lane ran"

# From a linked worktree the row names that worktree and goes to the MAIN checkout's history.
WT_TIP="$(fg -C "$WT" rev-parse HEAD)"
run_lane "$WT" -- frontend
expect "linked worktree" 0 7
expect_row "linked worktree" "$WT" "$WT_TIP" frontend 0
[[ ! -e "$WT/.tmp/gate-history.log" ]] || fail "linked worktree: a history file in the worktree"

# A state variable inherited from outside a locked run never runs the lane without the lock.
run_lane "$MAIN" XE_GATE_LANE_STATE="$TMP/foreign-state" -- frontend
expect "inherited state" 2 7
grep -q '^lane ' "$LOG" && fail "inherited state: the lane ran"

# A tree the lane left dirty suffixes the tip too.
run_lane "$MAIN" FAKE_DIRTY="$MAIN/left-behind.txt" -- frontend
expect "dirty at exit" 0 8
expect_row "dirty at exit" "$MAIN" "$TIP+dirty" frontend 0
rm -f "$MAIN/left-behind.txt"

if [[ "$failures" -ne 0 ]]; then
  echo "run-gate-lane.test.sh: $failures check(s) FAILED"
  exit 1
fi
echo "run-gate-lane.test.sh: PASS"
