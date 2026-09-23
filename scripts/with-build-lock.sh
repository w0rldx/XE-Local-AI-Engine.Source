#!/usr/bin/env bash
# with-build-lock.sh — run a command holding the repo-wide, cross-process build lock.
#
# Why this exists
#   `dotnet test --no-build` reads the assemblies in bin/. When a second process runs `dotnet build`
#   while those tests are executing, it overwrites the assemblies mid-run and the test host reports
#   PHANTOM failures — observed on this repo as `failed: 97` and `failed: 1` on runs that were clean
#   on re-run, and as `FileNotFoundException: Microsoft.AspNetCore.SignalR.Client.Core` in E2E, each
#   with DLL mtimes falling inside the run window. A contaminated run is indistinguishable from a
#   real regression, and this suite is the last gate before a release is cut — CI runs the same
#   suite, but only after the contaminated result has already been believed locally.
#
#   The only lock that existed was a SemaphoreSlim inside XEReactClientFixture — in-process, and
#   therefore invisible to another agent's shell. This is the cross-process equivalent: an exclusive
#   flock on a repo-local file, in the same spirit as the product's own
#   XE-Local-AI-Engine.Client/Hosting/SingleInstanceLease.cs.
#
#   This is PREVENTION and it is only half the story: it can only serialize commands that opt in.
#   A bare `dotnet build` in another terminal bypasses it entirely. That is what
#   scripts/assembly-guard.sh (DETECTION) is for — the two layers are independent by design.
#
# SCOPE: ONE LOCK FOR THE WHOLE REPOSITORY, WORKTREES INCLUDED
#   "Repo-wide" above is literal, and it takes deliberate work to be true. The obvious way to find
#   the repo root — `git rev-parse --show-toplevel` — returns the LINKED WORKTREE's own path when
#   you are inside one, so every worktree would get its own .tmp/build.lock and parallel lanes would
#   not serialize against each other at all. This script therefore resolves the root through
#   `--git-common-dir`, which points at the MAIN checkout's .git from inside every worktree.
#
#   That is not the shape you would guess from the corruption story above. Each worktree has its own
#   bin/ and obj/, so one worktree's build cannot rewrite another's assemblies, and assembly safety
#   alone would be satisfied by a per-worktree lock. The reason the lock is shared anyway is the
#   MACHINE: several gates at once, each running JOBS test hosts (about 1.5 GB per batch, more for the
#   Persistence lane; the constants live in scripts/lib/test-sizing.sh), will exhaust RAM, and a run that
#   dies to the OOM killer — or merely swaps through a timing-sensitive test — is contaminated in a
#   way the assembly guard cannot see. Cross-worktree builds therefore serialize BY DESIGN.
#
#   Three sibling scripts already resolved the lock this way — run-agent-framework-validation.sh,
#   run-agent-framework-hardware-compat.sh and capture-agent-framework-dependencies.sh all compute
#   SHARED_REPO_ROOT from --git-common-dir. This script was the odd one out, so the four disagreed
#   about where the lock file lives; they now agree.
#
#   To get per-worktree parallelism back on purpose, point each lane at its own file:
#
#     export BUILD_LOCK_FILE="$(git rev-parse --show-toplevel)/.tmp/build.lock"
#
#   BUILD_LOCK_FILE (and --lock-file) remain the override and are unchanged by any of this.
#
# THE FD-INHERITANCE TRAP (this bit us once already)
#   flock's lock lives on an open file descriptor, and file descriptors are INHERITED across fork
#   and exec. `dotnet build` leaves MSBuild node-reuse daemons and VBCSCompiler running for ~15
#   minutes after it exits; if they inherit the lock fd they keep the lock held while idle and every
#   other agent starves. Both `flock <file> <command>` and a plain `exec 9>lock` suffer from this.
#   The fix here is to close the lock fd in the child (`"$@" 9>&-`): the wrapper shell holds the
#   lock, nothing it spawns can, and the lock is released the moment the wrapper exits — even on a
#   crash, because the kernel closes the fd. Node reuse and shared compilation stay ENABLED, so
#   there is no build-speed cost (the previously used workaround was
#   `dotnet build-server shutdown` + `/nodeReuse:false -p:UseSharedCompilation=false`, which is slow).
#
# Usage:
#   scripts/with-build-lock.sh [options] [--] <command> [args...]
#
# Options:
#   --timeout <seconds>   Max time to wait for the lock (default: ${BUILD_LOCK_TIMEOUT:-3600}).
#                         A full Release build + solution test run legitimately takes many minutes,
#                         so the default is deliberately generous. It is bounded, never infinite.
#   --lock-file <path>    Lock file to use (default: the SHARED .tmp/build.lock in the main
#                         checkout, which is gitignored — see SCOPE above).
#   --help                Show this message.
#
# Env knobs:
#   BUILD_LOCK_TIMEOUT    Same as --timeout.
#   BUILD_LOCK_FILE       Same as --lock-file. The supported way to opt OUT of the shared lock.
#   XE_BUILD_LOCK_HELD    Set BY this script for the command it runs. If it already names the same
#                         lock file, the wrapper is a pass-through instead of deadlocking on itself.
#                         Do not set it by hand — doing so disables locking for that subtree.
#
# Visibility (who holds it, who waits)
#   The holder writes <lock>.owner (pid= started= cwd= worktree= cmd=) and truncates it on exit. A
#   process that has to wait first registers <lock>.waiters/<pid> (pid= since= cwd= worktree= cmd=),
#   removed on acquisition or exit; records of dead waiters are pruned whenever the wrapper starts.
#   While waiting it prints one line a minute:
#     [build-lock] waiting 03:00/60:00 — held by pid=… (<worktree>, <age>) <cmd>; N waiters ahead
#   and on timeout the holder's age and progress. scripts/build-lock-status.sh [--json] shows the
#   same picture on demand, read-only.
#
# Re-entrancy
#   Nesting is safe: an inner wrapper sees XE_BUILD_LOCK_HELD matching its lock file and exec's the
#   command directly. That keeps composed scripts (a wrapper calling run-tests-memory-safe.sh) from
#   deadlocking. The corollary is that a wrapped command which itself forks PARALLEL work is
#   NOT serialized internally — the lock cannot subdivide a critical section someone else created.
#   Do not wrap a runner that already takes this lock over its own dotnet trees.
#
# Exit codes:
#   0-N  — the wrapped command's own exit status (passed through unchanged)
#   69   — could not acquire the lock within the timeout (EX_UNAVAILABLE); nothing was run
#   2    — usage error
set -uo pipefail

# --git-common-dir, NOT --show-toplevel: see "SCOPE" above for why the lock is shared. Two mechanics
# worth knowing before editing this. The query is anchored with `git -C` at THIS SCRIPT's directory,
# never the caller's CWD: from a CWD outside any repository the query would fail and fall through to
# the else-arm, which inside a linked worktree resolves to that worktree's own root — a silently
# unshared lock, exactly the bug this resolution exists to prevent; from a CWD inside a DIFFERENT
# repository the lock would land under that repository instead. git prints the common dir relative
# to the -C directory when it is inside it, so a relative answer is joined back onto that same
# directory before realpath sees it. The else-arm is the pre-existing fallback for a source tree
# with no git metadata; keep it.
# The resolution itself lives in scripts/lib/build-lock-common.sh, shared with build-lock-status.sh.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# A copy of this script needs lib/ beside it; say so rather than fail later on a missing function.
[[ -r "${SCRIPT_DIR}/lib/build-lock-common.sh" ]] || {
  echo "[build-lock] missing ${SCRIPT_DIR}/lib/build-lock-common.sh — copy scripts/lib/ along with this script." >&2
  exit 2
}
# shellcheck source=scripts/lib/build-lock-common.sh
source "${SCRIPT_DIR}/lib/build-lock-common.sh"

LOCK_FILE="${BUILD_LOCK_FILE:-$(build_lock_shared_path "${SCRIPT_DIR}")}"
TIMEOUT="${BUILD_LOCK_TIMEOUT:-3600}"

# Fixed fd rather than bash's `{var}>` form: the child redirection that closes it (`9>&-`) needs a
# literal number, and fd 9 is the conventional choice in flock's own documentation.
LOCK_FD=9

log()  { echo "[build-lock] $*"; }
die()  { echo "[build-lock] $*" >&2; exit 2; }

usage() {
  sed -n '2,/^set -uo/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//; $d'
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --timeout)   TIMEOUT="${2:-}"; [[ -n "${TIMEOUT}" ]] || die "--timeout needs a value"; shift 2 ;;
    --lock-file) LOCK_FILE="${2:-}"; [[ -n "${LOCK_FILE}" ]] || die "--lock-file needs a value"; shift 2 ;;
    --help|-h)   usage; exit 0 ;;
    --)          shift; break ;;
    -*)          die "Unknown option: $1" ;;
    *)           break ;;
  esac
done

[[ $# -gt 0 ]] || die "no command given. Usage: scripts/with-build-lock.sh [options] -- <command> [args...]"
[[ "${TIMEOUT}" =~ ^[0-9]+$ ]] || die "--timeout must be a whole number of seconds, got '${TIMEOUT}'"

command -v flock >/dev/null 2>&1 \
  || die "flock not on PATH (util-linux). Install it, or set XE_BUILD_LOCK_HELD=1 to run unlocked
             — but then nothing stops a concurrent build from corrupting the run."

mkdir -p "$(dirname "${LOCK_FILE}")" || die "could not create the lock directory for ${LOCK_FILE}"
# Canonicalise so the re-entrancy comparison is not defeated by a relative path or a symlinked root.
LOCK_FILE="$(cd "$(dirname "${LOCK_FILE}")" && pwd)/$(basename "${LOCK_FILE}")"
OWNER_FILE="${LOCK_FILE}.owner"
WAITER_FILE="${LOCK_FILE}.waiters/$$"

# Already inside a lock for this same file: run through. See "Re-entrancy" above.
if [[ "${XE_BUILD_LOCK_HELD:-}" == "${LOCK_FILE}" ]]; then
  exec "$@"
fi

describe_owner() {
  # Diagnostic only, and inherently racy: the holder may have released between our failed attempt
  # and this read. Never used for control flow.
  if [[ -s "${OWNER_FILE}" ]]; then
    tr -d '\n' <"${OWNER_FILE}"
  else
    echo "unknown (no owner record)"
  fi
}

# `held by pid=… (<worktree>, <age>) <cmd>` — diagnostic only, racy like describe_owner.
describe_holder() {
  local rec pid age
  rec="$(head -n1 "${OWNER_FILE}" 2>/dev/null)"
  pid="$(build_lock_field "${rec}" pid)"
  [[ -n "${pid}" ]] || { echo "held by an unknown process (no owner record)"; return; }
  age="$(build_lock_age_s "$(build_lock_field "${rec}" started)")"
  # A holder that wrote the pre-visibility record has no worktree field; print "-" rather than "".
  local worktree; worktree="$(build_lock_field "${rec}" worktree)"
  printf 'held by pid=%s (%s, %s) %s\n' "${pid}" "${worktree:--}" \
    "$(build_lock_hms "${age}")" "$(build_lock_field "${rec}" cmd)"
}

# Live waiter records registered before ours.
waiters_ahead() {
  local f since mine n=0
  mine="$(date -d "$(build_lock_field "$(cat "${WAITER_FILE}" 2>/dev/null)" since)" +%s 2>/dev/null)" || mine=0
  for f in "${LOCK_FILE}".waiters/*; do
    [[ -f "${f}" && "${f}" != "${WAITER_FILE}" ]] || continue
    build_lock_alive "${f##*/}" || continue
    since="$(date -d "$(build_lock_field "$(cat "${f}" 2>/dev/null)" since)" +%s 2>/dev/null)" || continue
    (( since < mine )) && n=$((n + 1))
  done
  echo "${n}"
}

mmss() { printf '%02d:%02d' $(( $1 / 60 )) $(( $1 % 60 )); }

# Append, never truncate: a waiting process opens this file BEFORE it holds the lock, and `>` would
# blow away the holder's data at open time.
exec 9>>"${LOCK_FILE}" || die "could not open the lock file ${LOCK_FILE}"

MY_CWD="$(pwd -P)"
MY_WORKTREE="$(build_lock_worktree "${MY_CWD}")"
build_lock_prune_waiters "${LOCK_FILE}"

if ! flock -n "${LOCK_FD}"; then
  # Register as a waiter so build-lock-status.sh and the holder's peers can see the queue. The EXIT
  # trap covers a timeout or a signal; a SIGKILLed waiter is pruned by the next wrapper start.
  mkdir -p "${LOCK_FILE}.waiters" \
    && printf 'pid=%s since=%s cwd=%s worktree=%s cmd=%s\n' "$$" "$(date -Iseconds)" "${MY_CWD}" \
         "${MY_WORKTREE}" "$*" >"${WAITER_FILE}" 2>/dev/null || true
  trap 'rm -f "${WAITER_FILE}"' EXIT
  log "waiting up to ${TIMEOUT}s for the build lock — held by: $(describe_owner)"
  # Bounded attempts instead of one long flock, so the wait reports once a minute.
  WAIT_START=${SECONDS}
  while :; do
    remaining=$(( TIMEOUT - (SECONDS - WAIT_START) ))
    if (( remaining <= 0 )); then
      holder_age="$(build_lock_age_s "$(build_lock_field "$(head -n1 "${OWNER_FILE}" 2>/dev/null)" started)")"
      echo "[build-lock] FAIL: could not acquire ${LOCK_FILE} within ${TIMEOUT}s." >&2
      echo "[build-lock]   Current holder: $(describe_owner)" >&2
      echo "[build-lock]   Holder age: $(build_lock_hms "${holder_age}")" >&2
      "${SCRIPT_DIR}/build-lock-status.sh" --lock-file "${LOCK_FILE}" 2>/dev/null \
        | grep -E '^(progress|batch):' | sed 's/^ */[build-lock]   /' >&2
      echo "[build-lock]   Nothing was run. Wait for that build/test to finish (scripts/build-lock-status.sh" >&2
      echo "[build-lock]   shows its progress), or re-run with --timeout <seconds> if it is legitimately" >&2
      echo "[build-lock]   slower than ${TIMEOUT}s." >&2
      exit 69
    fi
    flock -w $(( remaining < 60 ? remaining : 60 )) "${LOCK_FD}" && break
    (( SECONDS - WAIT_START < TIMEOUT )) \
      && log "waiting $(mmss $((SECONDS - WAIT_START)))/$(mmss "${TIMEOUT}") — $(describe_holder); $(waiters_ahead) waiters ahead"
  done
  rm -f "${WAITER_FILE}"
fi

printf 'pid=%s started=%s cwd=%s worktree=%s cmd=%s\n' "$$" "$(date -Iseconds)" "${MY_CWD}" "${MY_WORKTREE}" "$*" \
  >"${OWNER_FILE}" 2>/dev/null || true
# Truncate rather than delete: the next waiter's `describe_owner` should read "unknown", not the
# stale record of a process that has already finished.
trap ': >"${OWNER_FILE}"' EXIT

# 9>&- is the whole point — see THE FD-INHERITANCE TRAP above. Without it, MSBuild's node-reuse
# daemons keep the lock alive for ~15 idle minutes and every other agent starves.
XE_BUILD_LOCK_HELD="${LOCK_FILE}" "$@" 9>&-
exit $?
