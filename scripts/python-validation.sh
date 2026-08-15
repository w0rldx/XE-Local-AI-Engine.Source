#!/usr/bin/env bash
# python-validation.sh - repo Python quality gate (ruff / pyrefly / pytest / bandit)
#
# Usage:
#   scripts/python-validation.sh --scope <scope> [--base <branch>] [--serial]
#
# Scopes:
#   deps      uv sync --locked --all-groups   (root pyproject.toml — the DEV tooling venv, never the training runtime)
#   style     ruff format --check + ruff check
#   types     pyrefly check
#   tests     pytest with the coverage options from pyproject.toml
#   security  bandit over tools/training and scripts
#   changed   auto-detect scope from git diff against --base (default develop)
#   full      deps, then style/types/tests/security in parallel
#
# Behavior:
#   - Independent trees run in parallel by default (--serial disables that).
#   - Per-tree output goes to .tmp/validate-logs/<tree>-<timestamp>.log; the last 80 lines of every failing log are printed.
#   - Exit 1 if any tree fails, 2 if uv is missing.
#
# The tooling config lives in the root pyproject.toml. tools/training/pyproject.toml + uv.lock are the SHIPPED training
# runtime manifest (ADR 0005) and are deliberately not touched by this script.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
LOG_DIR="${PROJECT_ROOT}/.tmp/validate-logs"
TS="$(date +%Y%m%d-%H%M%S)"
mkdir -p "${LOG_DIR}"

PARALLEL="true"
LOG_RETENTION="${VALIDATE_LOG_RETENTION:-10}"
PY_ROOTS=(tools/training scripts)

log() { echo "[python-validate] $*"; }

command -v uv >/dev/null 2>&1 || {
  echo "[python-validate] FAIL: uv is required but was not found on PATH" >&2
  exit 2
}

_rotate_logs() {
  local tree
  for tree in deps style types tests security; do
    find "${LOG_DIR}" -maxdepth 1 -type f -name "${tree}-*.log" -printf '%T@ %p\n' 2>/dev/null \
      | sort -nr \
      | tail -n +"$((LOG_RETENTION + 1))" \
      | awk '{ $1=""; sub(/^ /,""); print }' \
      | while IFS= read -r old; do rm -f -- "${old}"; done
  done
}
_rotate_logs

_run_tree() {
  local name="$1" logfile="$2" body="$3"
  log "START ${name} -> ${logfile}"
  (
    set -e
    cd "${PROJECT_ROOT}"
    echo "[${name}] start: $(date -Iseconds)"
    "${body}"
    echo "[${name}] done: $(date -Iseconds)"
  ) >"${logfile}" 2>&1
}

_tree_deps() { uv sync --locked --all-groups; }
_tree_style() {
  uv run ruff format --check "${PY_ROOTS[@]}"
  uv run ruff check "${PY_ROOTS[@]}"
}
_tree_types() { uv run pyrefly check; }
_tree_tests() { uv run pytest; }
_tree_security() { uv run bandit -c pyproject.toml -r "${PY_ROOTS[@]}"; }

declare -A RESULTS
RESULT_ORDER=()

_record() {
  RESULTS["$1"]="$2:$3"
  RESULT_ORDER+=("$1")
}

_report() {
  local any_fail=0
  echo
  log "=== Results ==="
  local name
  for name in "${RESULT_ORDER[@]}"; do
    local entry="${RESULTS[$name]}"
    local status="${entry%%:*}"
    local logfile="${entry#*:}"
    if [[ "${status}" == "0" ]]; then
      log "PASS ${name} (log: ${logfile})"
    else
      any_fail=1
      log "FAIL ${name} (status=${status}, log: ${logfile})"
      echo "----- last 80 lines of ${logfile} -----"
      tail -n 80 "${logfile}" || true
      echo "----- end ${name} log -----"
    fi
  done
  return "${any_fail}"
}

_reset_results() {
  RESULTS=()
  RESULT_ORDER=()
}

_run_group() {
  local pids=() names=() logs=()
  while [[ $# -gt 0 ]]; do
    local name="$1" body="$2"
    shift 2
    local logfile="${LOG_DIR}/${name}-${TS}.log"
    names+=("${name}")
    logs+=("${logfile}")
    if [[ "${PARALLEL}" == "true" ]]; then
      _run_tree "${name}" "${logfile}" "${body}" &
      pids+=("$!")
    else
      _run_tree "${name}" "${logfile}" "${body}"
      _record "${name}" "$?" "${logfile}"
    fi
  done

  if [[ "${PARALLEL}" == "true" ]]; then
    local i=0
    local status=0
    for pid in "${pids[@]}"; do
      wait "${pid}" || status=$?
      _record "${names[$i]}" "${status}" "${logs[$i]}"
      status=0
      i=$((i + 1))
    done
  fi
}

scope_single() {
  local name="$1" body="$2"
  _reset_results
  _run_group "${name}" "${body}"
  _report
}

scope_full() {
  log "=== Full Python validation ==="

  _reset_results
  _run_group "deps" _tree_deps
  _report || return $?

  _reset_results
  _run_group \
    "style" _tree_style \
    "types" _tree_types \
    "tests" _tree_tests \
    "security" _tree_security
  _report
}

scope_changed() {
  local base="${1:-develop}"
  log "=== Changed-scope validation (base: ${base}) ==="

  # Resolve the base as a local ref, then origin/<base>, then any rev (tag/SHA); refuse to guess.
  local base_ref="" candidate
  for candidate in "refs/heads/${base}" "refs/remotes/origin/${base}" "${base}"; do
    if git -C "${PROJECT_ROOT}" rev-parse --verify --quiet "${candidate}^{commit}" >/dev/null 2>&1; then
      base_ref="${candidate}"
      break
    fi
  done
  if [[ -z "${base_ref}" ]]; then
    echo "[python-validate] FAIL: cannot resolve --base '${base}' (tried the local branch, origin/${base}, and a rev)" >&2
    return 1
  fi
  local merge_base
  merge_base="$(git -C "${PROJECT_ROOT}" merge-base "${base_ref}" HEAD)" || {
    echo "[python-validate] FAIL: no merge base between ${base_ref} and HEAD" >&2
    return 1
  }

  # Committed + staged + unstaged changes since the merge base, plus untracked files, so a pre-commit
  # run sees the same set a post-commit run would.
  local changed_files
  changed_files="$(
    {
      git -C "${PROJECT_ROOT}" diff --name-only "${merge_base}"
      git -C "${PROJECT_ROOT}" ls-files --others --exclude-standard
    } | LC_ALL=C sort -u
  )"

  if [[ -z "${changed_files}" ]]; then
    log "No changed files detected."
    return 0
  fi

  log "Changed files detected:"
  while IFS= read -r file; do echo "  ${file}"; done <<< "${changed_files}"

  local run_full=false run_py=false
  while IFS= read -r file; do
    case "${file}" in
      pyproject.toml|uv.lock|scripts/python-validation.sh|.gitignore) run_full=true ;;
      tools/training/*.py|scripts/*.py) run_py=true ;;  # bash case globs cross "/" — this covers nested dirs
    esac
  done <<< "${changed_files}"

  if [[ "${run_full}" == "true" ]]; then
    scope_full
    return $?
  fi

  if [[ "${run_py}" != "true" ]]; then
    log "No Python validation needed."
    return 0
  fi

  _reset_results
  _run_group \
    "style" _tree_style \
    "types" _tree_types \
    "tests" _tree_tests \
    "security" _tree_security
  _report
}

usage() {
  cat <<USAGE
Usage: $(basename "$0") --scope <scope> [--base <branch>] [--serial]

Scopes:
  deps      uv sync --locked --all-groups (root dev tooling venv)
  style     ruff format --check + ruff check
  types     pyrefly check
  tests     pytest with coverage from pyproject.toml
  security  bandit over tools/training and scripts
  changed   auto-detect scope from git diff (--base defaults to develop)
  full      deps, then style/types/tests/security

Options:
  --base <branch>  Branch/ref to diff against for changed scope (default: develop)
  --serial         Disable parallel execution
  --help           Show this message

Logs: ${LOG_DIR}/<tree>-<timestamp>.log
USAGE
}

SCOPE=""
BASE_BRANCH="develop"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --scope|--base)
      if [[ $# -lt 2 || "${2}" == --* ]]; then
        echo "Error: $1 requires a value" >&2
        usage >&2
        exit 1
      fi
      if [[ "$1" == "--scope" ]]; then SCOPE="$2"; else BASE_BRANCH="$2"; fi
      shift 2
      ;;
    --serial) PARALLEL="false"; shift ;;
    --help|-h) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 1 ;;
  esac
done

if [[ -z "${SCOPE}" ]]; then
  echo "Error: --scope is required" >&2
  usage >&2
  exit 1
fi

case "${SCOPE}" in
  deps) scope_single "deps" _tree_deps ;;
  style) scope_single "style" _tree_style ;;
  types) scope_single "types" _tree_types ;;
  tests) scope_single "tests" _tree_tests ;;
  security) scope_single "security" _tree_security ;;
  changed) scope_changed "${BASE_BRANCH}" ;;
  full) scope_full ;;
  *) echo "Unknown scope: ${SCOPE}" >&2; usage >&2; exit 1 ;;
esac
