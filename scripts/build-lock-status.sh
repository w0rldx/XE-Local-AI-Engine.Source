#!/usr/bin/env bash
# build-lock-status.sh — who holds the shared build lock, for how long, how far along, and who waits.
#
# Read-only: it never takes the lock and never writes next to it (dead waiter records are skipped
# here and pruned by scripts/with-build-lock.sh). "Held" is judged from the owner record plus
# `kill -0` on its pid, so a record whose pid is dead is reported as stale — the lock is free.
#
# Usage:
#   scripts/build-lock-status.sh [--json] [--lock-file <path>]
#
# The lock path resolves exactly as in scripts/with-build-lock.sh (BUILD_LOCK_FILE, else the shared
# .tmp/build.lock of the main checkout). Progress is shown when the holder runs
# run-backend-tests.sh or run-tests-memory-safe.sh: the last `>> ` line and the newest batch line of
# the newest gate.log that has any, under <holder cwd>/.tmp/backend-test-results/.
#
# Exit codes: 0 — status printed (locked or free); 2 — usage error.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# A copy of this script needs lib/ beside it; say so rather than fail later on a missing function.
[[ -r "${SCRIPT_DIR}/lib/build-lock-common.sh" ]] || {
  echo "[build-lock-status] missing ${SCRIPT_DIR}/lib/build-lock-common.sh — copy scripts/lib/ along with this script." >&2
  exit 2
}
# shellcheck source=scripts/lib/build-lock-common.sh
source "${SCRIPT_DIR}/lib/build-lock-common.sh"

FORMAT=human
LOCK_FILE="${BUILD_LOCK_FILE:-}"
usage() { echo "Usage: scripts/build-lock-status.sh [--json] [--lock-file <path>]"; }
while [[ $# -gt 0 ]]; do
  case "$1" in
    --json)      FORMAT=json; shift ;;
    --lock-file) LOCK_FILE="${2:-}"; [[ -n "${LOCK_FILE}" ]] || { usage >&2; exit 2; }; shift 2 ;;
    --help|-h)   usage; exit 0 ;;
    *)           usage >&2; exit 2 ;;
  esac
done
[[ -n "${LOCK_FILE}" ]] || LOCK_FILE="$(build_lock_shared_path "${SCRIPT_DIR}")"
# Same canonicalisation as the wrapper, without creating anything.
[[ -d "$(dirname "${LOCK_FILE}")" ]] && LOCK_FILE="$(cd "$(dirname "${LOCK_FILE}")" && pwd)/$(basename "${LOCK_FILE}")"

# --- holder ---
record="$(head -n1 "${LOCK_FILE}.owner" 2>/dev/null)"
h_pid="$(build_lock_field "${record}" pid)"
h_alive=no; stale=false; held=false
if [[ -n "${h_pid}" ]]; then
  if build_lock_alive "${h_pid}"; then h_alive=yes; held=true; else stale=true; fi
fi
h_cmd="$(build_lock_field "${record}" cmd)"
h_cwd="$(build_lock_field "${record}" cwd)"
# Records written before cwd= existed: ask the live process instead.
[[ -z "${h_cwd}" && "${h_alive}" == yes ]] && h_cwd="$(readlink "/proc/${h_pid}/cwd" 2>/dev/null)"
h_worktree="$(build_lock_field "${record}" worktree)"
[[ -z "${h_worktree}" && -n "${h_cwd}" ]] && h_worktree="$(build_lock_worktree "${h_cwd}")"
h_age_s="$(build_lock_age_s "$(build_lock_field "${record}" started)")"

# --- progress (gate or batched runner only) ---
p_known=false; p_step=""; p_batch=""; p_log=""
if ${held} && [[ "${h_cmd}" =~ run-backend-tests\.sh|run-tests-memory-safe\.sh ]] && [[ -n "${h_cwd}" ]]; then
  # Newest gate.log that carries progress lines: sibling lanes log concurrently and write neither
  # `>> ` nor batch lines, so the newest file alone is often the wrong one. A half-written last
  # line is harmless: it simply fails to match.
  while IFS= read -r log; do
    p_step="$(grep -a '^>> ' "${log}" 2>/dev/null | tail -n1)"
    p_batch="$(grep -aE ' pass=[0-9]+ +fail=[0-9]+' "${log}" 2>/dev/null | tail -n1 | sed -E 's/^ +//; s/ +/ /g')"
    if [[ -n "${p_step}${p_batch}" ]]; then p_known=true; p_log="${log}"; break; fi
  done < <(find "${h_cwd}/.tmp/backend-test-results" -maxdepth 2 -name gate.log -printf '%T@ %p\n' 2>/dev/null \
             | sort -rn | cut -d' ' -f2-)
fi

# --- waiters, oldest first (dead records skipped, not deleted) ---
waiters=()
for f in "${LOCK_FILE}".waiters/*; do
  [[ -f "${f}" ]] || continue
  build_lock_alive "${f##*/}" || continue
  w="$(head -n1 "${f}" 2>/dev/null)"
  s="$(date -d "$(build_lock_field "${w}" since)" +%s 2>/dev/null)" || s=0
  waiters+=("${s}"$'\t'"${w}")
done
if [[ ${#waiters[@]} -gt 0 ]]; then mapfile -t waiters < <(printf '%s\n' "${waiters[@]}" | sort -n); fi

mem_gb="$(awk '/^MemAvailable:/ { printf "%.1f", $2 / 1048576 }' /proc/meminfo 2>/dev/null)"

if [[ "${FORMAT}" == json ]]; then
  # No jq on the contract: escape by hand. Backslash first, then quote, then the control characters
  # a command line can realistically carry.
  js() {
    local v="$1"
    v="${v//\\/\\\\}"; v="${v//\"/\\\"}"; v="${v//$'\t'/\\t}"; v="${v//$'\n'/\\n}"; v="${v//$'\r'/\\r}"
    printf '"%s"' "${v//[[:cntrl:]]/}"
  }
  num() { [[ "$1" =~ ^[0-9]+(\.[0-9]+)?$ ]] && printf '%s' "$1" || printf 'null'; }
  printf '{"lock":%s,' "$(js "${LOCK_FILE}")"
  if ${held}; then
    printf '"holder":{"pid":%s,"alive":true,"worktree":%s,"cwd":%s,"ageSeconds":%s,"age":%s,"cmd":%s},' \
      "$(num "${h_pid}")" "$(js "${h_worktree}")" "$(js "${h_cwd}")" "$(num "${h_age_s}")" \
      "$(js "$(build_lock_hms "${h_age_s}")")" "$(js "${h_cmd}")"
  else
    printf '"holder":null,'
  fi
  printf '"stale":%s,' "${stale}"
  if ${stale}; then printf '"stalePid":%s,' "$(num "${h_pid}")"; fi
  printf '"waiters":['
  sep=""
  for e in ${waiters[@]+"${waiters[@]}"}; do
    w="${e#*$'\t'}"; a="$(build_lock_age_s "$(build_lock_field "${w}" since)")"
    printf '%s{"pid":%s,"since":%s,"ageSeconds":%s,"worktree":%s,"cwd":%s,"cmd":%s}' "${sep}" \
      "$(num "$(build_lock_field "${w}" pid)")" "$(js "$(build_lock_field "${w}" since)")" "$(num "${a}")" \
      "$(js "$(build_lock_field "${w}" worktree)")" "$(js "$(build_lock_field "${w}" cwd)")" \
      "$(js "$(build_lock_field "${w}" cmd)")"
    sep=","
  done
  printf '],'
  if ${held} && [[ "${h_cmd}" =~ run-backend-tests\.sh|run-tests-memory-safe\.sh ]]; then
    printf '"progress":{"known":%s,"step":%s,"lastBatch":%s,"log":%s},' "${p_known}" \
      "$(js "${p_step}")" "$(js "${p_batch}")" "$(js "${p_log}")"
  else
    printf '"progress":null,'
  fi
  printf '"memAvailableGb":%s}\n' "$(num "${mem_gb}")"
  exit 0
fi

echo "lock:      ${LOCK_FILE}"
if ${held}; then
  echo "holder:    pid=${h_pid} alive=yes worktree=${h_worktree:--} age=$(build_lock_hms "${h_age_s}")"
  echo "cmd:       ${h_cmd}"
  if [[ "${h_cmd}" =~ run-backend-tests\.sh|run-tests-memory-safe\.sh ]]; then
    if ${p_known}; then
      echo "progress:  ${p_step:-(no >> line yet)}"
      echo "batch:     ${p_batch:-(none yet)}"
      echo "log:       ${p_log}"
    else
      echo "progress:  unknown (no gate.log with progress lines under ${h_cwd:-?}/.tmp/backend-test-results yet)"
    fi
  fi
elif ${stale}; then
  echo "holder:    stale record (pid ${h_pid} dead) — lock is free"
else
  echo "holder:    free"
fi
echo "waiters:   ${#waiters[@]}"
i=0
for e in ${waiters[@]+"${waiters[@]}"}; do
  i=$((i + 1)); w="${e#*$'\t'}"
  printf '  %d. pid=%s worktree=%s waiting=%s cmd=%s\n' "${i}" "$(build_lock_field "${w}" pid)" \
    "$(build_lock_field "${w}" worktree)" "$(build_lock_hms "$(build_lock_age_s "$(build_lock_field "${w}" since)")")" \
    "$(build_lock_field "${w}" cmd)"
done
echo "memAvailable: ${mem_gb:-?} GB"
