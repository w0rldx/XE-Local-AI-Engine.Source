#!/usr/bin/env bash
# squash-guard.sh — the checks the coordinator runs on develop immediately before `git merge --squash <branch>`.
#
# It replaces the hand-run trio (clean tree, on develop, develop is an ancestor of the branch) and adds the
# one nobody could remember: the branch tip is the tip the backend gate last passed on, and the tip every
# required lane (scripts/run-gate-lane.sh) last ran green on. It NEVER merges; on success it prints the squash
# commands to run next.
#
# Usage:
#   scripts/squash-guard.sh <branch> [--repo <path>] [--gate-log <path>]
#
# --repo      any checkout of the repository (default: the checkout this script lives in). The checks run
#             against its MAIN checkout, the directory holding the shared .git.
# --gate-log  default <main checkout>/.tmp/gate-history.log, one tab-separated line per gate run written by
#             scripts/run-backend-tests.sh: <time> <worktree> <tip> Tests=p/f/s ... scope=<scope> ... exit=<code>.
#             scope is full, no-build, shard, siblings-only or contracts-skipped; only full is accepted.
#             A tip suffixed +dirty or +moved never equals a branch tip, so it never authorises a squash.
#             The backend row is the newest row ending exit=0 with no lane= field. Lane rows are
#             <time> <worktree> <tip> lane=<name> wall=<s> lane-exit=<code> (scripts/run-gate-lane.sh).
#
# Required lanes: openapi always; frontend unless every path of the branch diff against its merge base with
# develop (renames read as a deletion plus an addition) is under docs/ or tools/training/ or ends in .md:
# acceptance reads far beyond the SPA (the backend package graph through licenses:check, the CI workflow's
# Node major, the OpenAPI breaking check). Every lane is required when the merge base or the diff cannot be read. Per
# required lane the NEWEST row for the worktree counts: it must hold the unsuffixed branch tip and lane-exit=0.
#
# Last, scripts/ratchet-baseline-check.py compares every shrink-only allowlist and baseline at the branch tip with
# the merge base: an added key or a raised limit fails 21, and so does a comparison that cannot run. The operator
# accepts a reviewed widening by setting XE_RATCHET_WIDEN_REASON=<why> for this run; the reason is printed. A list new
# on the branch is allowed once and printed as NEW, also when this develop copy of the checker cannot parse it.
#
# Exit codes (each failure also prints its numbered reason on stderr):
#   0  all checks passed            10 main checkout has changes (untracked included)
#   2  usage or setup error, a      11 main checkout HEAD is not develop
#      worktree path with a tab or newline, or a failed git status (never read as clean)
#                                   12 <branch> does not exist
#                                   13 develop is not an ancestor of <branch> (develop moved: absorb it, re-gate)
#                                   14 <branch> is not checked out in any worktree
#                                   15 the branch worktree has changes (untracked included)
#                                   16 no gated run recorded: no gate log, or no exit=0 line for that worktree
#                                   17 the last exit=0 run gated a different tip than the branch tip
#                                   18 the last exit=0 run recorded failed tests (Tests=p/f/s with f > 0)
#                                   19 the last exit=0 run was partial (scope= other than full, such
#                                      as contracts-skipped: the release contract lane was bypassed)
#                                   20 a required lane's newest row for that worktree is missing, red,
#                                      for another tip, or suffixed +dirty/+moved
#                                   21 a shrink-only allowlist or baseline widened against the merge base,
#                                      or that comparison could not run
set -uo pipefail

SCRIPT_DIR="$(dirname "$(realpath "${BASH_SOURCE[0]}")")"
# shellcheck source=scripts/lib/git-env.sh
source "${SCRIPT_DIR}/lib/git-env.sh" \
  || { echo "ERROR: cannot load ${SCRIPT_DIR}/lib/git-env.sh — copy scripts/lib/ along with this script." >&2; exit 2; }
# A caller's GIT_DIR/GIT_WORK_TREE/GIT_INDEX_FILE must not make this vouch for another repository.
xe_drop_git_repo_env

usage() { echo "Usage: scripts/squash-guard.sh <branch> [--repo <path>] [--gate-log <path>]"; }
# "<code>: <reason>": the reason goes to stderr, the code becomes the exit status.
fail() { echo "squash-guard: FAIL $1" >&2; exit "${1%%:*}"; }

branch=""
repo="${SCRIPT_DIR}/.."
gate_log=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo)     repo="${2:-}"; [[ -n "${repo}" ]] || { usage >&2; exit 2; }; shift 2 ;;
    --gate-log) gate_log="${2:-}"; [[ -n "${gate_log}" ]] || { usage >&2; exit 2; }; shift 2 ;;
    --help|-h)  usage; exit 0 ;;
    -*)         usage >&2; exit 2 ;;
    *)          [[ -z "${branch}" ]] || { usage >&2; exit 2; }; branch="$1"; shift ;;
  esac
done
[[ -n "${branch}" ]] || { usage >&2; exit 2; }
git check-ref-format --branch "${branch}" >/dev/null 2>&1 \
  || { echo "squash-guard: '${branch}' is not a valid branch name." >&2; exit 2; }
# Full ref names from here on: a branch named like a file or a tag can never be read as one.
ref="refs/heads/${branch}"

common="$(git -C "${repo}" rev-parse --path-format=absolute --git-common-dir 2>/dev/null)" \
  || { echo "squash-guard: '${repo}' is not inside a git repository." >&2; exit 2; }
main="$(dirname "$(realpath "${common}")")"
[[ -n "${gate_log}" ]] || gate_log="${main}/.tmp/gate-history.log"
g() { git -C "${main}" "$@"; }
# <dir> <code> <what>: fails <code> on changes; a status that itself fails proves nothing, so exit 2.
require_clean() {
  local out
  out="$(git -C "$1" status --porcelain --untracked-files=all)" \
    || { echo "squash-guard: git status failed in $1; cannot vouch that it is clean." >&2; exit 2; }
  [[ -z "${out}" ]] || fail "$2: $3 $1 has uncommitted or untracked changes."
}

require_clean "${main}" 10 "main checkout"
[[ "$(g symbolic-ref -q HEAD)" == refs/heads/develop ]] \
  || fail "11: main checkout HEAD is not develop."
tip="$(g rev-parse --verify --quiet "${ref}^{commit}")" \
  || fail "12: branch '${branch}' does not exist."
g merge-base --is-ancestor refs/heads/develop "${ref}" \
  || fail "13: develop is not an ancestor of '${branch}': develop moved, absorb it and gate again."

# NUL-separated records, so a path holding a newline is read whole and refused below.
worktree="" path=""
while IFS= read -r -d '' record; do
  case "${record}" in
    "worktree "*)    path="${record#worktree }" ;;
    "branch ${ref}") worktree="${path}"; break ;;
  esac
done < <(g worktree list --porcelain -z)
[[ -n "${worktree}" ]] || fail "14: '${branch}' is not checked out in any worktree, so no gate run can be matched."
if [[ "${worktree}" == *[$'\t\n']* ]]; then
  echo "squash-guard: the worktree path of '${branch}' holds a tab or newline; the gate log cannot record it. Move the worktree." >&2
  exit 2
fi
require_clean "${worktree}" 15 "branch worktree"

[[ -r "${gate_log}" ]] || fail "16: no gated run recorded: ${gate_log} does not exist."
# The newest exit=0 line for this worktree; the path is compared after resolving symlinks on both sides.
worktree_real="$(realpath "${worktree}")"
gated=""
while IFS= read -r line; do
  IFS=$'\t' read -r -a f <<<"${line}"
  [[ "${#f[@]}" -ge 4 && "${f[-1]}" == "exit=0" && "${f[3]}" != lane=* ]] || continue
  [[ "$(realpath -m "${f[1]}")" == "${worktree_real}" ]] && gated="${line}"
done <"${gate_log}"
[[ -n "${gated}" ]] || fail "16: no gated run recorded: ${gate_log} has no exit=0 line for ${worktree}."

IFS=$'\t' read -r -a f <<<"${gated}"
if [[ "${f[2]}" == *+* ]]; then
  fail "17: last green gate of ${worktree} recorded tip ${f[2]}: the tree was dirty when the gate started or HEAD moved during it, so it certifies no commit. Commit, then gate this tip (${tip})."
fi
[[ "${f[2]}" == "${tip}" ]] \
  || fail "17: last green gate of ${worktree} ran on ${f[2]}, but '${branch}' is at ${tip}: gate this tip."
tests="" scope=""
for field in "${f[@]}"; do
  [[ "${field}" == Tests=* ]] && tests="${field#Tests=}"
  [[ "${field}" == scope=* ]] && scope="${field#scope=}"
done
IFS=/ read -r _ failed _ <<<"${tests}"
[[ "${failed}" == 0 ]] || fail "18: last green gate of ${worktree} recorded Tests=${tests:-<missing>}, failures must be 0."
[[ "${scope}" == full ]] \
  || fail "19: last green gate of ${worktree} recorded scope=${scope:-<missing>}: only a full run (built, unsharded, every lane, release contract tests not bypassed; contracts-skipped is not full) certifies a squash."

gated_at="${f[0]}"

required=(openapi)
if base="$(g merge-base refs/heads/develop "${ref}" 2>/dev/null)"; then
  # Pathspec * crosses directories, so *.md excludes Markdown at any depth.
  g diff --quiet --no-renames "${base}" "${ref}" -- . ':(exclude)docs/' ':(exclude)*.md' \
    ':(exclude)tools/training/' >/dev/null 2>&1
  case $? in
    0) ;;
    1) required+=(frontend) ;;
    *) required+=(frontend); echo "squash-guard: the branch diff could not be read; every lane is required." >&2 ;;
  esac
else
  required+=(frontend)
  echo "squash-guard: no merge base of '${branch}' with develop; every lane is required." >&2
fi
lanes_ok=""
for lane in "${required[@]}"; do
  row=""
  while IFS= read -r line; do
    IFS=$'\t' read -r -a f <<<"${line}"
    [[ "${#f[@]}" -ge 6 && "${f[3]}" == "lane=${lane}" && "${f[-1]}" == lane-exit=* ]] || continue
    [[ "$(realpath -m "${f[1]}")" == "${worktree_real}" ]] && row="${line}"
  done <"${gate_log}"
  [[ -n "${row}" ]] \
    || fail "20: no ${lane} lane run recorded for ${worktree}: run scripts/run-gate-lane.sh ${lane} on ${tip}."
  IFS=$'\t' read -r -a f <<<"${row}"
  if [[ "${f[2]}" == *+* ]]; then
    fail "20: last ${lane} lane run of ${worktree} recorded tip ${f[2]}: the tree was dirty when it started or HEAD moved during it, so it certifies no commit. Run it on ${tip}."
  fi
  [[ "${f[2]}" == "${tip}" ]] \
    || fail "20: last ${lane} lane run of ${worktree} ran on ${f[2]}, but '${branch}' is at ${tip}: run scripts/run-gate-lane.sh ${lane}."
  [[ "${f[-1]}" == lane-exit=0 ]] \
    || fail "20: last ${lane} lane run of ${worktree} on ${tip} recorded ${f[-1]}: it must be green."
  lanes_ok+="${lanes_ok:+, }${lane} ${f[0]}"
done

python3 "${SCRIPT_DIR}/ratchet-baseline-check.py" "${ref}" --repo "${main}" --base refs/heads/develop \
  || fail "21: '${branch}' widens a shrink-only allowlist or baseline, or the comparison could not run (above)."

echo "OK: ${branch} @ ${tip} gated ${gated_at}, ancestor of develop, trees clean"
echo "Lanes green on this tip: ${lanes_ok}"
echo "Next (not run by this script):"
printf '  git -C %q merge --squash %q\n' "${main}" "${ref}"
printf '  git -C %q commit\n' "${main}"
