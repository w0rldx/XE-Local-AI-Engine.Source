#!/usr/bin/env bash
# regen-after-merge.sh — regenerate and stage the generated and sorted frontend files after a merge.
#
# Never hand-merge these (docs/wiki/13-testing-and-validation.md, "Generated and sorted files"): resolve each
# conflict by taking either side, then run this. From XE-Local-AI-Engine.Client.React it runs, in order and
# stopping at the first failure:
#   pnpm run openapi:generate    the hey-api client from the committed openapi/v1.json
#   pnpm run licenses:generate   the third-party license manifest (needs the restored dotnet tools)
#   pnpm run locales:sort        src/locales/*.json sorted by key
# and then stages exactly these paths, deletions included, and prints what is staged under them:
#   openapi/  src/core/api/generated/  src/features/about/data/third-party-licenses.generated.json  src/locales/*.json
# Nothing else is staged. A conflicted openapi/v1.json is regenerated from a live host first
# (OPENAPI_LIVE_SCRIPT=openapi scripts/openapi-live-check.sh); a dependency branch also runs
# `pnpm run dependencies:refresh`. Staging goes to the index git selects, an exported GIT_INDEX_FILE included:
# in a worktree shared with other agents, export a private index first (wiki 13, "Committing in a shared worktree").
#
# Usage:
#   scripts/regen-after-merge.sh [--repo <path>]
#
# --repo  the checkout to regenerate (default: the checkout this script lives in)
#
# Exit codes:
#   0  regenerated and staged
#   1  a generator or `git add` failed; nothing is staged when a generator fails
#   2  usage or setup error (unknown flag, no frontend directory, pnpm or git missing)
set -uo pipefail

usage() { echo "Usage: scripts/regen-after-merge.sh [--repo <path>]"; }

repo="$(dirname "$(realpath "${BASH_SOURCE[0]}")")/.."
while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo)    repo="${2:-}"; [[ -n "${repo}" ]] || { usage >&2; exit 2; }; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *)         usage >&2; exit 2 ;;
  esac
done

spa="${repo}/XE-Local-AI-Engine.Client.React"
[[ -f "${spa}/package.json" ]] || { echo "regen-after-merge: no ${spa}/package.json." >&2; exit 2; }
command -v pnpm >/dev/null 2>&1 || { echo "regen-after-merge: pnpm is not on PATH." >&2; exit 2; }
command -v git >/dev/null 2>&1 || { echo "regen-after-merge: git is not on PATH." >&2; exit 2; }

for step in openapi:generate licenses:generate locales:sort; do
  echo ">> pnpm run ${step}"
  ( cd "${spa}" && pnpm run "${step}" ) \
    || { echo "regen-after-merge: FAIL pnpm run ${step}; nothing staged." >&2; exit 1; }
done

paths=(openapi src/core/api/generated src/features/about/data/third-party-licenses.generated.json 'src/locales/*.json')
git -C "${spa}" add -A -- "${paths[@]}" || { echo "regen-after-merge: FAIL git add." >&2; exit 1; }
echo "Staged under the generated paths:"
git -C "${spa}" diff --cached --name-only --relative -- "${paths[@]}" | sed 's/^/  /'
