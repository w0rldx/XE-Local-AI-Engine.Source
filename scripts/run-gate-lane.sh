#!/usr/bin/env bash
# run-gate-lane.sh — Run one gate lane beside the backend gate, under the build lock, and record it in the gate history.
#
# Usage:
#   scripts/run-gate-lane.sh <lane>
#
# Lanes (fixed command, started from the root of the checkout this script lives in):
#   frontend   pnpm -C <repo>/XE-Local-AI-Engine.Client.React run acceptance
#   openapi    <repo>/scripts/openapi-live-check.sh (compare mode: the committed client against a live host)
# The lane name is the only argument. Any other argument, an unknown lane, or one of these variables set in the
# environment (even empty) exits 2 and records nothing, because each skips a check or changes what runs:
#   OPENAPI_LIVE_SKIP_BUILD, OPENAPI_LIVE_SCRIPT, OPENAPI_LIVE_CLIENT_RELEASE_ROOT (openapi-live-check.sh),
#   XE_ALLOW_NODE_DRIFT (CheckNodeMajor.mjs), XE_I18N_MISSING_KEYS (CheckI18nDefaults.mjs),
#   BUILD_LOCK_FILE and, on entry, XE_BUILD_LOCK_HELD (with-build-lock.sh: another lock, or none, would let the
#   lane run beside a backend gate). OPENAPI_LIVE_TIMEOUT_SECONDS stays allowed.
#
# Locking: the script re-runs itself once through scripts/with-build-lock.sh; the live check's own lock
# re-entry passes through for the same lock file. vitest's timing assertions fail beside a backend gate.
#
# Gate history
#   Every run past the argument checks appends ONE tab-separated line to the MAIN checkout's
#   .tmp/gate-history.log, the file scripts/run-backend-tests.sh writes, resolved the same way:
#     <ISO time> <worktree real path or -> <tip> lane=<name> wall=<seconds> lane-exit=<code>
#   The last field is lane-exit=, never exit=, so a reader that takes the newest row ending exit=0 as the
#   backend row never selects a lane row. tip is HEAD once the lock is held ("-" outside git), suffixed
#   "+dirty" when the tree had changes (untracked included) then or at exit and "+moved" when HEAD differed at exit; a
#   suffixed tip certifies nothing. wall counts from the lock; for 69 it is the time spent waiting and tip is
#   HEAD at exit. 69 (lock not acquired) and 75 (contaminated) are recorded and certify nothing.
#   scripts/squash-guard.sh reads the rows.
#
# Exit codes: the lane's own; 2 usage, a refused variable or a setup error; 69 the build lock was not acquired.
set -uo pipefail

SELF="$(realpath "${BASH_SOURCE[0]}")"
REPO="$(cd "$(dirname "$SELF")/.." && pwd)"
# shellcheck source=scripts/lib/git-env.sh
source "$REPO/scripts/lib/git-env.sh" \
  || { echo "ERROR: cannot load $REPO/scripts/lib/git-env.sh — copy scripts/lib/ along with this script." >&2; exit 2; }
xe_drop_git_repo_env

usage() { sed -n '2,/^set -uo/p' "$SELF" | sed 's/^# \{0,1\}//; $d'; }

[[ $# -eq 1 ]] || { usage >&2; exit 2; }
lane="$1"
case "$lane" in
  frontend) command=(pnpm -C "$REPO/XE-Local-AI-Engine.Client.React" run acceptance) ;;
  openapi)  command=("$REPO/scripts/openapi-live-check.sh") ;;
  --help|-h) usage; exit 0 ;;
  *) echo "run-gate-lane: unknown lane '$lane' (frontend or openapi)." >&2; exit 2 ;;
esac
refused=(OPENAPI_LIVE_SKIP_BUILD OPENAPI_LIVE_SCRIPT OPENAPI_LIVE_CLIENT_RELEASE_ROOT XE_ALLOW_NODE_DRIFT
  XE_I18N_MISSING_KEYS BUILD_LOCK_FILE)
# The re-run under the lock carries the state file and the lock marker the wrapper set.
[[ -n "${XE_GATE_LANE_STATE:-}" ]] || refused+=(XE_BUILD_LOCK_HELD)
for var in "${refused[@]}"; do
  [[ -z "${!var+set}" ]] || { echo "run-gate-lane: $var is set; a gate lane runs its checks unchanged. Unset it." >&2; exit 2; }
done

# Under the lock: record the tip and the lock time for the unlocked instance, then run the lane.
if [[ -n "${XE_GATE_LANE_STATE:-}" ]]; then
  state="$XE_GATE_LANE_STATE"
  unset XE_GATE_LANE_STATE
  [[ -n "${XE_BUILD_LOCK_HELD:-}" && -f "$state" && -w "$state" ]] \
    || { echo "run-gate-lane: XE_GATE_LANE_STATE is set outside a run of this script under the build lock." >&2; exit 2; }
  if tip="$(git -C "$REPO" rev-parse HEAD 2>/dev/null)"; then
    status="$(git -C "$REPO" status --porcelain --untracked-files=all 2>/dev/null)" || status="unreadable"
    [[ -z "$status" ]] || tip+="+dirty"
  else
    tip="-"
  fi
  # A run whose tip was not recorded would be written as HEAD unsuffixed: refuse to run it at all.
  printf '%s %s\n' "$tip" "$EPOCHSECONDS" >"$state" || { echo "run-gate-lane: cannot write $state." >&2; exit 2; }
  cd "$REPO" || exit 2
  "${command[@]}"
  exit
fi

# shellcheck source=scripts/lib/build-lock-common.sh
source "$REPO/scripts/lib/build-lock-common.sh" || exit 2
history="$(dirname "$(build_lock_shared_path "$REPO/scripts")")/gate-history.log"
t0=$EPOCHSECONDS
state="$(mktemp)" || { echo "run-gate-lane: cannot create a state file." >&2; exit 2; }

record() {
  local rc=$? tip="" locked_t0="" now status worktree="$REPO"
  read -r tip locked_t0 <"$state" 2>/dev/null
  rm -f "$state"
  now="$(git -C "$REPO" rev-parse HEAD 2>/dev/null)" || now=""
  if [[ -z "$tip" ]]; then
    # Nothing ran under the lock: HEAD now, for information only (lane-exit is never 0 here).
    tip="${now:--}" locked_t0="$t0"
  elif [[ "${tip%%+*}" != - ]]; then
    status="$(git -C "$REPO" status --porcelain --untracked-files=all 2>/dev/null)" || status="unreadable"
    [[ -z "$status" || "$tip" == *+dirty ]] || tip+="+dirty"
    [[ "${tip%%+*}" == "$now" ]] || tip+="+moved"
  fi
  if [[ "$worktree" == *[$'\t\n']* ]]; then
    echo "WARN: worktree path holds a tab or newline; gate history records it as '-'." >&2
    worktree="-"
  fi
  mkdir -p "$(dirname "$history")" \
    && printf '%s\t%s\t%s\tlane=%s\twall=%s\tlane-exit=%s\n' \
      "$(date -Iseconds)" "$worktree" "$tip" "$lane" "$((EPOCHSECONDS - locked_t0))" "$rc" >>"$history" \
    || echo "WARN: could not append to $history" >&2
}
trap record EXIT
trap 'exit 130' INT
trap 'exit 143' TERM HUP
XE_GATE_LANE_STATE="$state" "$REPO/scripts/with-build-lock.sh" -- "$SELF" "$lane"
