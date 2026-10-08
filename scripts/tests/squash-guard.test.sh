#!/usr/bin/env bash
# Contract test for scripts/squash-guard.sh. Each case builds its own throwaway repository (develop in the
# main checkout, the branch in a linked worktree, a synthetic gate-history line) and asserts the exact exit
# code. Nothing here touches this checkout's git state.

set -uo pipefail

ROOT="$(cd "$(dirname "$(realpath "${BASH_SOURCE[0]}")")/../.." && pwd)"
GUARD="$ROOT/scripts/squash-guard.sh"
TMP="$(realpath "$(mktemp -d)")"
trap 'rm -rf "$TMP"' EXIT

# Every git call, the guard's included, runs with the caller's repository selectors and config removed and
# discovery bounded by $TMP, so a fixture can never reach the enclosing repository.
SCRUB=()
for var in $(compgen -v GIT_); do SCRUB+=(-u "$var"); done
SCRUB+=(GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1 GIT_CEILING_DIRECTORIES="$TMP")
fg() { env "${SCRUB[@]}" git "$@"; }

failures=0
check() {
  local name="$1" want="$2" got="$3"
  if [[ "$got" -eq "$want" ]]; then
    echo "PASS $name (exit $got)"
  else
    echo "FAIL $name: exit $got, wanted $want"
    sed 's/^/    /' "$TMP/out"
    failures=$((failures + 1))
  fi
}

# Sets MAIN, WT, TIP, LOG for a fresh fixture whose branch is <branch> (default feature) and whose gate log
# holds one green line for the branch tip.
fixture() {
  local dir="$TMP/$1" branch="${2:-feature}"
  MAIN="$dir/main" WT="$dir/wt" LOG="$dir/main/.tmp/gate-history.log"
  mkdir -p "$MAIN"
  fg -C "$MAIN" init -q -b develop
  [[ "$(fg -C "$MAIN" rev-parse --show-toplevel)" == "$MAIN" ]] \
    || { echo "REFUSING: fixture $MAIN does not resolve to itself" >&2; exit 1; }
  fg -C "$MAIN" config user.email fixture@example.invalid
  fg -C "$MAIN" config user.name "Contract Fixture"
  # .tmp/ is ignored as in the real repository; the gate log lives there.
  printf '.tmp/\n' >"$MAIN/.gitignore"
  printf 'readme\n' >"$MAIN/README.md"
  fg -C "$MAIN" add .gitignore README.md
  fg -C "$MAIN" commit -q -m base
  fg -C "$MAIN" worktree add -q -b "$branch" "$WT"
  printf 'change\n' >"$WT/change.txt"
  fg -C "$WT" add change.txt
  fg -C "$WT" commit -q -m change
  TIP="$(fg -C "$WT" rev-parse HEAD)"
  mkdir -p "$MAIN/.tmp"
  log_line "$TIP" 120/0/3 0 >"$LOG"
}

# <tip> <tests p/f/s> <exit> [scope, default full]: one line in the shared tab-separated format.
log_line() {
  printf '2026-10-08T10:00:00Z\t%s\t%s\tTests=%s\tPersistence=40/0/0\tAgent=30/0/0\tContracts=skipped\tscope=%s\twall=900\texit=%s\n' \
    "$WT" "$1" "$2" "${4:-full}" "$3"
}

guard() {
  env "${SCRUB[@]}" "$GUARD" "$@" >"$TMP/out" 2>&1
  status=$?
}

# (a) happy path, with the default and with an explicit --gate-log
fixture a
guard feature --repo "$MAIN"
check "a happy path" 0 "$status"
grep -q "^OK: feature @ $TIP gated 2026-10-08T10:00:00Z, ancestor of develop, trees clean$" "$TMP/out" \
  || { echo "FAIL a: OK line missing"; cat "$TMP/out"; failures=$((failures + 1)); }
grep -q 'merge --squash refs/heads/feature' "$TMP/out" \
  || { echo "FAIL a: squash command not printed"; failures=$((failures + 1)); }
[[ "$(fg -C "$MAIN" rev-parse HEAD)" == "$(fg -C "$MAIN" rev-parse refs/heads/develop)" \
  && -z "$(fg -C "$MAIN" status --porcelain)" ]] \
  || { echo "FAIL a: the guard changed the main checkout"; failures=$((failures + 1)); }
mv "$LOG" "$TMP/a/elsewhere.log"
guard feature --repo "$WT" --gate-log "$TMP/a/elsewhere.log"
check "a happy path, --gate-log and --repo pointing at the branch worktree" 0 "$status"

# (b) dirty main tree: an untracked file counts
fixture b
printf 'x\n' >"$MAIN/stray.txt"
guard feature --repo "$MAIN"
check "b untracked file in main checkout" 10 "$status"

# (c) main HEAD is not develop
fixture c
fg -C "$MAIN" checkout -q -b other
guard feature --repo "$MAIN"
check "c main HEAD not develop" 11 "$status"

# (d) develop moved after the branch was cut
fixture d
printf 'later\n' >"$MAIN/later.txt"
fg -C "$MAIN" add later.txt
fg -C "$MAIN" commit -q -m later
guard feature --repo "$MAIN"
check "d develop moved" 13 "$status"

# (e) dirty branch worktree
fixture e
printf 'x\n' >"$WT/stray.txt"
guard feature --repo "$MAIN"
check "e untracked file in branch worktree" 15 "$status"

# (f) no gate log at all
fixture f
rm "$LOG"
guard feature --repo "$MAIN"
check "f no gate log" 16 "$status"

# (g) the green line is for an older tip; a newer RED run on the current tip does not count either
fixture g
printf 'more\n' >>"$WT/change.txt"
fg -C "$WT" commit -q -am more
log_line "$(fg -C "$WT" rev-parse HEAD)" 119/1/3 1 >>"$LOG"
guard feature --repo "$MAIN"
check "g gate line for a different tip" 17 "$status"

# (h) the newest green line recorded failed tests
fixture h
log_line "$TIP" 118/2/3 0 >>"$LOG"
guard feature --repo "$MAIN"
check "h gate line with fail > 0" 18 "$status"
# A siblings-only run writes exit=0 with an empty Tests= value: the main suite never ran, so no pass.
fixture h2
log_line "$TIP" '' 0 >>"$LOG"
guard feature --repo "$MAIN"
check "h green line with empty Tests= (siblings-only)" 18 "$status"

# (l) a partial green run (no build, a shard, siblings only) certifies nothing; neither does a line
# written before the scope field existed
for scope in no-build shard siblings-only; do
  fixture "l-$scope"
  log_line "$TIP" 120/0/3 0 "$scope" >>"$LOG"
  guard feature --repo "$MAIN"
  check "l green line with scope=$scope" 19 "$status"
  grep -Fq "recorded scope=$scope" "$TMP/out" || { echo "FAIL l: scope not named"; cat "$TMP/out"; failures=$((failures + 1)); }
done
# A gate that bypassed the release contract lane the diff needed: refused, and the message says why.
fixture l-contracts-skipped
log_line "$TIP" 120/0/3 0 contracts-skipped >>"$LOG"
guard feature --repo "$MAIN"
check "l green line with scope=contracts-skipped" 19 "$status"
if ! grep -Fq "recorded scope=contracts-skipped" "$TMP/out" || ! grep -Fq "contracts-skipped is not full" "$TMP/out"; then
  echo "FAIL l: contracts-skipped not explained"; cat "$TMP/out"; failures=$((failures + 1))
fi
fixture l-legacy
printf '2026-10-08T10:00:00Z\t%s\t%s\tTests=120/0/3\tPersistence=40/0/0\tAgent=30/0/0\tContracts=skipped\twall=900\texit=0\n' \
  "$WT" "$TIP" >>"$LOG"
guard feature --repo "$MAIN"
check "l green line without a scope field" 19 "$status"

# (m) a tip the gate marked dirty or moved never matches, and the message says why
for suffix in +dirty +moved +dirty+moved; do
  fixture "m$suffix"
  log_line "$TIP$suffix" 120/0/3 0 >>"$LOG"
  guard feature --repo "$MAIN"
  check "m green line with tip $suffix" 17 "$status"
  grep -Fq "recorded tip $TIP$suffix: the tree was dirty" "$TMP/out" \
    || { echo "FAIL m: suffix message missing"; cat "$TMP/out"; failures=$((failures + 1)); }
done

# (n) a branch worktree whose path holds a tab is refused as a setup error
fixture $'n\ttab'
guard feature --repo "$MAIN"
check "n tab in the worktree path" 2 "$status"
grep -Fq 'holds a tab or newline' "$TMP/out" || { echo "FAIL n: message missing"; cat "$TMP/out"; failures=$((failures + 1)); }

# (o) a git status that fails (here: an unreadable index) is never read as a clean tree
if [[ "$(id -u)" -eq 0 ]]; then
  echo "SKIP o: running as root, an unreadable index is still readable"
else
  fixture o-main
  chmod 000 "$MAIN/.git/index"
  guard feature --repo "$MAIN"
  check "o git status fails in the main checkout" 2 "$status"
  grep -Fq 'git status failed' "$TMP/out" || { echo "FAIL o: message missing"; cat "$TMP/out"; failures=$((failures + 1)); }
  fixture o-wt
  chmod 000 "$MAIN/.git/worktrees/wt/index"
  guard feature --repo "$MAIN"
  check "o git status fails in the branch worktree" 2 "$status"
  grep -Fq "git status failed in $WT" "$TMP/out" || { echo "FAIL o: worktree not named"; cat "$TMP/out"; failures=$((failures + 1)); }
fi

# (i) exported GIT_DIR/GIT_WORK_TREE at another repository change nothing, either way
mkdir -p "$TMP/decoy"
fg -C "$TMP/decoy" init -q -b main
printf 'x\n' >"$TMP/decoy/dirty.txt"
fixture i
env "${SCRUB[@]}" GIT_DIR="$TMP/decoy/.git" GIT_WORK_TREE="$TMP/decoy" "$GUARD" feature --repo "$MAIN" >"$TMP/out" 2>&1
check "i GIT_DIR/GIT_WORK_TREE at a dirty decoy, clean fixture" 0 $?
printf 'x\n' >"$MAIN/stray.txt"
mkdir -p "$TMP/clean-decoy"
fg -C "$TMP/clean-decoy" init -q -b develop
env "${SCRUB[@]}" GIT_DIR="$TMP/clean-decoy/.git" GIT_WORK_TREE="$TMP/clean-decoy" "$GUARD" feature --repo "$MAIN" >"$TMP/out" 2>&1
check "i GIT_DIR/GIT_WORK_TREE at a clean develop decoy, dirty fixture" 10 $?

# (j) a branch named like a tracked file is still read as the branch; the file alone is not a branch
fixture j README.md
guard README.md --repo "$MAIN"
check "j branch named README.md" 0 "$status"
fixture j2
guard README.md --repo "$MAIN"
check "j tracked file path that is not a branch" 12 "$status"

# missing branch, and a branch with no worktree to match a gate run against
fixture k
guard nope --repo "$MAIN"
check "k branch does not exist" 12 "$status"
fg -C "$MAIN" branch -q detached-branch
guard detached-branch --repo "$MAIN"
check "k branch not checked out in any worktree" 14 "$status"

# usage errors
guard --repo "$MAIN"
check "usage: no branch" 2 "$status"
guard 'bad..name' --repo "$MAIN"
check "usage: invalid branch name" 2 "$status"

if [[ "$failures" -ne 0 ]]; then
  echo "squash-guard.test.sh: $failures case(s) FAILED"
  exit 1
fi
echo "squash-guard.test.sh: PASS"
