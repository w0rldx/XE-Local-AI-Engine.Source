# shellcheck shell=bash
# build-lock-common.sh — sourced by scripts/with-build-lock.sh and scripts/build-lock-status.sh so the
# two can never disagree about where the shared lock lives or how its records are read.
#
# Records are one line of `key=value` fields separated by single spaces, `cmd=` always last:
#   <lock>.owner            pid= started= cwd= worktree= cmd=
#   <lock>.waiters/<pid>    pid= since=   cwd= worktree= cmd=

# Every git query below goes through xe_git: an inherited GIT_DIR would otherwise put the lock under
# ANOTHER repository while this checkout builds, and `git -C` does not override it.
# Mandatory, and fatal to the sourcing script: without xe_git every discovery below would fail and fall
# back to a checkout-local lock, silently unsharing it in a linked worktree. `exit`, not `return`: a
# sourcer that ignored a non-zero source status would carry on without it.
BUILD_LOCK_GIT_ENV="$(dirname "$(realpath "${BASH_SOURCE[0]}")")/git-env.sh"
# shellcheck source=scripts/lib/git-env.sh
source "${BUILD_LOCK_GIT_ENV}" || {
  echo "[build-lock] ERROR: cannot load ${BUILD_LOCK_GIT_ENV} — copy scripts/lib/ along with this script." >&2
  exit 2
}

# The shared lock path, resolved from <script dir> — see "SCOPE" in scripts/with-build-lock.sh for
# why this goes through --git-common-dir and is anchored at the script, never the caller's CWD. git
# prints the common dir relative to the -C directory when it is inside it, so a relative answer is
# joined back onto that directory before realpath sees it. The else-arm is the fallback for a source
# tree with no git metadata; keep it.
build_lock_shared_path() {
  local dir="$1" common root
  if common="$(xe_git -C "${dir}" rev-parse --git-common-dir 2>/dev/null)" && [[ -n "${common}" ]]; then
    [[ "${common}" == /* ]] || common="${dir}/${common}"
    root="$(dirname "$(realpath "${common}")")"
  else
    root="$(dirname "${dir}")"
  fi
  printf '%s/.tmp/build.lock\n' "${root}"
}

# Basename of the checkout (main or linked worktree) that contains <dir>, or "-".
build_lock_worktree() {
  local top
  if top="$(xe_git -C "$1" rev-parse --show-toplevel 2>/dev/null)" && [[ -n "${top}" ]]; then
    basename "${top}"
  else
    echo "-"
  fi
}

# Value of <key> in a record line. Every field but cmd ends at the next ` <known key>=`, so a path
# with spaces survives; cmd takes the rest of the line.
build_lock_field() {
  local line=" $1" key="$2" value
  [[ "${line}" == *" ${key}="* ]] || return 0
  value="${line#*" ${key}="}"
  if [[ "${key}" != cmd ]]; then
    value="$(sed -E 's/ (pid|started|since|cwd|worktree|cmd)=.*//' <<<"${value}")"
  fi
  printf '%s\n' "${value}"
}

build_lock_alive() { [[ "$1" =~ ^[0-9]+$ ]] && kill -0 "$1" 2>/dev/null; }

# Seconds since an ISO-8601 timestamp, or empty when it does not parse.
build_lock_age_s() {
  local t
  t="$(date -d "$1" +%s 2>/dev/null)" || return 0
  echo $(( $(date +%s) - t ))
}

build_lock_hms() {
  [[ "$1" =~ ^[0-9]+$ ]] || { echo "?"; return; }
  printf '%02d:%02d:%02d\n' $(( $1 / 3600 )) $(( $1 % 3600 / 60 )) $(( $1 % 60 ))
}

# Removes waiter records whose process is gone (a SIGKILLed waiter never runs its EXIT trap).
build_lock_prune_waiters() {
  local f
  for f in "$1".waiters/*; do
    [[ -f "${f}" ]] || continue
    build_lock_alive "${f##*/}" || rm -f "${f}"
  done
}
