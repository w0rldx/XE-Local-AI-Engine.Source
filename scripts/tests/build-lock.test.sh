#!/usr/bin/env bash
# Self-check for the build-lock waiter registry (scripts/with-build-lock.sh) and
# scripts/build-lock-status.sh. Everything runs against a TEMP lock file; the shared lock is never
# touched. Every background wrapper starts in its own process group (setsid) and the EXIT trap kills
# those groups by PGID: the wrapper runs its command in the foreground, so killing the wrapper's PID
# alone would orphan the command.
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
WRAPPER="$ROOT/scripts/with-build-lock.sh"
STATUS="$ROOT/scripts/build-lock-status.sh"
TMP="$(mktemp -d)"
LOCK="$TMP/build.lock"
PIDS=()
cleanup() {
  local p
  for p in ${PIDS[@]+"${PIDS[@]}"}; do kill -- "-$p" 2>/dev/null || true; done
  for p in ${PIDS[@]+"${PIDS[@]}"}; do wait "$p" 2>/dev/null || true; done
  rm -rf "$TMP"
}
trap cleanup EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }
status() { "$STATUS" --lock-file "$LOCK" "$@"; }
# grep reads a here-string, never a pipe: `grep -q` closing the pipe early fails the pipeline under
# pipefail with SIGPIPE.
# Polls instead of sleeping a fixed time: the condition, not a guess, ends the wait.
wait_for() {
  local what="$1"; shift
  # 60 s bound: a waiter has to start bash, resolve its worktree and prune before it registers, and
  # the contract runner shares the box with whatever gate is holding the real lock.
  for _ in $(seq 1 600); do "$@" && return 0; sleep 0.1; done
  fail "timed out waiting for: $what"
}
# Matches the holder's own pid: case (d) leaves a fabricated record behind, and a mere non-empty
# check would let the next waiter start before the holder has the lock.
holder_up() { [[ "$(head -c 64 "$LOCK.owner" 2>/dev/null)" == "pid=$HOLDER "* ]]; }
one_waiter() { grep -q '^waiters:   1$' <<<"$(status)"; }

# The holder's sleep is released through a fifo so the test decides when the lock frees up.
mkfifo "$TMP/release"
BUILD_LOCK_FILE="$LOCK" setsid "$WRAPPER" -- bash -c "read -r _ <'$TMP/release'" &
HOLDER=$!; PIDS+=("$HOLDER")
wait_for "holder record" holder_up

# (a) holder alive, with its worktree
out="$(status)"
grep -q "^holder:    pid=$HOLDER alive=yes worktree=$(basename "$ROOT") " <<<"$out" || fail "(a) holder line: $out"
echo "PASS (a) status shows the live holder with its worktree"

# (b) a second wrapper registers as a waiter, then times out with the holder's age
BUILD_LOCK_FILE="$LOCK" setsid "$WRAPPER" --timeout 3 -- true >"$TMP/waiter.out" 2>&1 &
WAITER=$!; PIDS+=("$WAITER")
wait_for "one waiter" one_waiter
grep -q "^  1. pid=$WAITER " <<<"$(status)" || fail "(b) waiter row missing"
set +e; wait "$WAITER"; rc=$?; set -e
[[ $rc -eq 69 ]] || { cat "$TMP/waiter.out" >&2; fail "(b) waiter exit $rc, wanted 69"; }
grep -q 'waiting up to 3s' "$TMP/waiter.out" || fail "(b) first wait line changed shape"
grep -Eq 'Holder age: [0-9]{2}:[0-9]{2}:[0-9]{2}' "$TMP/waiter.out" || fail "(b) no holder age on timeout"
[[ ! -e "$LOCK.waiters/$WAITER" ]] || fail "(b) waiter record left behind"
grep -q '^waiters:   0$' <<<"$(status)" || fail "(b) waiter still listed"
echo "PASS (b) waiter registered, exited 69 with the holder age, record removed"

# (c) holder exits: free, owner record truncated
echo go >"$TMP/release"
wait "$HOLDER"
grep -q '^holder:    free$' <<<"$(status)" || fail "(c) not free: $(status)"
[[ -e "$LOCK.owner" && ! -s "$LOCK.owner" ]] || fail "(c) owner record not truncated"
echo "PASS (c) released lock reads free, owner record truncated"

# (d) a record whose pid is dead is stale
bash -c 'exit 0' & dead=$!; wait "$dead"
printf 'pid=%s started=%s cwd=/nowhere worktree=x cmd=ghost\n' "$dead" "$(date -Iseconds)" >"$LOCK.owner"
grep -q "^holder:    stale record (pid $dead dead) — lock is free$" <<<"$(status)" || fail "(d) $(status)"
echo "PASS (d) dead owner pid reported as stale"

# (e) --json parses, including a hostile cmd, while held with a waiter queued
mkfifo "$TMP/release2"
BUILD_LOCK_FILE="$LOCK" setsid "$WRAPPER" -- bash -c "read -r _ <'$TMP/release2' # \"q\" \\ back"$'\t'"tab" &
HOLDER=$!; PIDS+=("$HOLDER")
wait_for "holder record" holder_up
BUILD_LOCK_FILE="$LOCK" setsid "$WRAPPER" --timeout 30 -- true >/dev/null 2>&1 &
WAITER=$!; PIDS+=("$WAITER")
wait_for "one waiter" one_waiter
status --json | python3 -c '
import json, sys
d = json.load(sys.stdin)
assert d["holder"]["alive"] and d["stale"] is False, d
assert len(d["waiters"]) == 1 and "\"q\" \\ back\ttab" in d["holder"]["cmd"], d
assert isinstance(d["memAvailableGb"], float), d
' || fail "(e) json"
echo go >"$TMP/release2"
wait "$HOLDER"; wait "$WAITER"
status --json | python3 -c 'import json,sys; d=json.load(sys.stdin); assert d["holder"] is None and d["waiters"] == [], d' \
  || fail "(e) free json"
echo "PASS (e) --json parses while held (escaped cmd, 1 waiter) and when free"
echo "build-lock.test.sh: PASS"
