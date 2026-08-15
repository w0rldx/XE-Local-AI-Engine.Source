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
#   --timeout <seconds>   Max time to wait for the lock (default: ${BUILD_LOCK_TIMEOUT:-1800}).
#                         A full Release build + solution test run legitimately takes many minutes,
#                         so the default is deliberately generous. It is bounded, never infinite.
#   --lock-file <path>    Lock file to use (default: .tmp/build.lock, which is gitignored).
#   --help                Show this message.
#
# Env knobs:
#   BUILD_LOCK_TIMEOUT    Same as --timeout.
#   BUILD_LOCK_FILE       Same as --lock-file.
#   XE_BUILD_LOCK_HELD    Set BY this script for the command it runs. If it already names the same
#                         lock file, the wrapper is a pass-through instead of deadlocking on itself.
#                         Do not set it by hand — doing so disables locking for that subtree.
#
# Re-entrancy
#   Nesting is safe: an inner wrapper sees XE_BUILD_LOCK_HELD matching its lock file and exec's the
#   command directly. That keeps composed scripts (project-validate.sh -> run-tests-memory-safe.sh)
#   from deadlocking. The corollary is that a wrapped command which itself forks PARALLEL work is
#   NOT serialized internally — the lock cannot subdivide a critical section someone else created.
#   Do not wrap .opencode/scripts/project-validate.sh as a whole; it locks its own dotnet trees.
#
# Exit codes:
#   0-N  — the wrapped command's own exit status (passed through unchanged)
#   69   — could not acquire the lock within the timeout (EX_UNAVAILABLE); nothing was run
#   2    — usage error
set -uo pipefail

PROJECT_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || (cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd))"

LOCK_FILE="${BUILD_LOCK_FILE:-${PROJECT_ROOT}/.tmp/build.lock}"
TIMEOUT="${BUILD_LOCK_TIMEOUT:-1800}"

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

# Append, never truncate: a waiting process opens this file BEFORE it holds the lock, and `>` would
# blow away the holder's data at open time.
exec 9>>"${LOCK_FILE}" || die "could not open the lock file ${LOCK_FILE}"

if ! flock -n "${LOCK_FD}"; then
  log "waiting up to ${TIMEOUT}s for the build lock — held by: $(describe_owner)"
  if ! flock -w "${TIMEOUT}" "${LOCK_FD}"; then
    echo "[build-lock] FAIL: could not acquire ${LOCK_FILE} within ${TIMEOUT}s." >&2
    echo "[build-lock]   Current holder: $(describe_owner)" >&2
    echo "[build-lock]   Nothing was run. Wait for that build/test to finish, or re-run with" >&2
    echo "[build-lock]   --timeout <seconds> if it is legitimately slower than ${TIMEOUT}s." >&2
    exit 69
  fi
fi

printf 'pid=%s started=%s cmd=%s\n' "$$" "$(date -Iseconds)" "$*" >"${OWNER_FILE}" 2>/dev/null || true
# Truncate rather than delete: the next waiter's `describe_owner` should read "unknown", not the
# stale record of a process that has already finished.
trap ': >"${OWNER_FILE}"' EXIT

# 9>&- is the whole point — see THE FD-INHERITANCE TRAP above. Without it, MSBuild's node-reuse
# daemons keep the lock alive for ~15 idle minutes and every other agent starves.
XE_BUILD_LOCK_HELD="${LOCK_FILE}" "$@" 9>&-
exit $?
