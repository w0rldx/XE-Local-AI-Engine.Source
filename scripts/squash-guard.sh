#!/usr/bin/env bash
# squash-guard.sh — the checks the coordinator runs on develop immediately before `git merge --squash <branch>`.
#
# It replaces the hand-run trio (clean tree, on develop, develop is an ancestor of the branch) and adds the
# one nobody could remember: the branch tip is the tip the backend gate last passed on. It NEVER merges; on
# success it prints the squash commands to run next.
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
  [[ "${#f[@]}" -ge 4 && "${f[-1]}" == "exit=0" ]] || continue
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

echo "OK: ${branch} @ ${tip} gated ${f[0]}, ancestor of develop, trees clean"
echo "Next (not run by this script):"
printf '  git -C %q merge --squash %q\n' "${main}" "${ref}"
printf '  git -C %q commit\n' "${main}"
