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
# holds one green backend line and one green openapi lane line for the branch tip. The branch commit is
# Markdown, so no fixture needs a frontend row. develop carries a frontend file, so a case can rename or delete it.
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
  mkdir -p "$MAIN/XE-Local-AI-Engine.Client.React"
  printf 'app\n' >"$MAIN/XE-Local-AI-Engine.Client.React/app.ts"
  fg -C "$MAIN" add .gitignore README.md XE-Local-AI-Engine.Client.React/app.ts
  fg -C "$MAIN" commit -q -m base
  fg -C "$MAIN" worktree add -q -b "$branch" "$WT"
  printf 'change\n' >"$WT/change.md"
  fg -C "$WT" add change.md
  fg -C "$WT" commit -q -m change
  TIP="$(fg -C "$WT" rev-parse HEAD)"
  mkdir -p "$MAIN/.tmp"
  log_line "$TIP" 120/0/3 0 >"$LOG"
  lane_line "$TIP" openapi 0 >>"$LOG"
}

# <tip> <lane> <lane exit> [worktree, default $WT]: one scripts/run-gate-lane.sh row.
lane_line() {
  printf '2026-10-08T11:00:00Z\t%s\t%s\tlane=%s\twall=60\tlane-exit=%s\n' "${4:-$WT}" "$1" "$2" "$3"
}

# Commits <change> in the branch worktree (a shell command run there) and rewrites the gate log for the new
# tip: one green backend line plus one green row per lane named after it.
branch_change() {
  local change="$1" lane; shift
  (cd "$WT" && eval "$change") && fg -C "$WT" add -A && fg -C "$WT" commit -q -m "$change"
  TIP="$(fg -C "$WT" rev-parse HEAD)"
  log_line "$TIP" 120/0/3 0 >"$LOG"
  for lane in "$@"; do lane_line "$TIP" "$lane" 0 >>"$LOG"; done
}

# <tip> <tests p/f/s> <exit> [scope, default full]: one line in the shared tab-separated format, with
# every column scripts/run-backend-tests.sh writes today.
log_line() {
  printf '2026-10-08T10:00:00Z\t%s\t%s\tTests=%s\tPersistence=40/0/0\tAgent=30/0/0\tCli=20/0/0\tContracts=skipped\tscope=%s\twall=900\treason=ok\tskips=needs-a-gpu:3\tunreported=0\tfailed=-\tflakes=-\tratchets=DbContextUser:31,PositionalRecord:314,Tuple:281,FilePlacement:251,CommentBudget:1665\texit=%s\n' \
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
# A twelve-field row written before 2026-10-23 (no unreported=, failed=, flakes=, ratchets=) still certifies.
fixture a-twelve
printf '2026-10-08T10:00:00Z\t%s\t%s\tTests=120/0/3\tPersistence=40/0/0\tAgent=30/0/0\tContracts=skipped\tscope=full\twall=900\treason=ok\tskips=unreported:3\texit=0\n' \
  "$WT" "$TIP" >"$LOG"
lane_line "$TIP" openapi 0 >>"$LOG"
guard feature --repo "$MAIN"
check "a happy path, twelve-field row" 0 "$status"
# A sixteen-field row written before 2026-10-28 (no Cli=) still certifies.
fixture a-sixteen
printf '2026-10-08T10:00:00Z\t%s\t%s\tTests=120/0/3\tPersistence=40/0/0\tAgent=30/0/0\tContracts=skipped\tscope=full\twall=900\treason=ok\tskips=needs-a-gpu:3\tunreported=0\tfailed=-\tflakes=-\tratchets=DbContextUser:31\texit=0\n' \
  "$WT" "$TIP" >"$LOG"
lane_line "$TIP" openapi 0 >>"$LOG"
guard feature --repo "$MAIN"
check "a happy path, sixteen-field row" 0 "$status"
# A seventeen-field row written before 2026-11-26 (no lanes=) still certifies; so does one with lanes=.
fixture a-seventeen
printf '2026-10-08T10:00:00Z\t%s\t%s\tTests=120/0/3\tPersistence=40/0/0\tAgent=30/0/0\tCli=20/0/0\tContracts=skipped\tscope=full\twall=900\treason=ok\tskips=-\tunreported=0\tfailed=-\tflakes=-\tratchets=DbContextUser:31\texit=0\n' \
  "$WT" "$TIP" >"$LOG"
lane_line "$TIP" openapi 0 >>"$LOG"
guard feature --repo "$MAIN"
check "a happy path, seventeen-field row" 0 "$status"
fixture a-lanes
printf '2026-10-08T10:00:00Z\t%s\t%s\tTests=120/0/3\tPersistence=40/0/0\tAgent=30/0/0\tCli=20/0/0\tContracts=skipped\tscope=full\twall=900\treason=ok\tskips=-\tunreported=0\tfailed=-\tflakes=-\tratchets=DbContextUser:31\tlanes=Tests:400,architecture:40\texit=0\n' \
  "$WT" "$TIP" >"$LOG"
lane_line "$TIP" openapi 0 >>"$LOG"
guard feature --repo "$MAIN"
check "a happy path, row with lanes=" 0 "$status"

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
printf 'more\n' >>"$WT/change.md"
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

# (p) lane rows (scripts/run-gate-lane.sh). Both append orders certify, and the OK output names the lanes.
fixture p-lane-first
{ lane_line "$TIP" openapi 0; log_line "$TIP" 120/0/3 0; } >"$LOG"
guard feature --repo "$MAIN"
check "p lane row before the backend row" 0 "$status"
grep -Fxq "Lanes green on this tip: openapi 2026-10-08T11:00:00Z" "$TMP/out" \
  || { echo "FAIL p: lanes line missing"; cat "$TMP/out"; failures=$((failures + 1)); }
# A green lane is no backend gate.
fixture p-lane-only
lane_line "$TIP" openapi 0 >"$LOG"
guard feature --repo "$MAIN"
check "p lane row without a backend row" 16 "$status"
# openapi is required at every squash; its newest row must be green on this tip and unsuffixed.
fixture p-missing
log_line "$TIP" 120/0/3 0 >"$LOG"
guard feature --repo "$MAIN"
check "p no openapi row" 20 "$status"
grep -Fq "no openapi lane run recorded" "$TMP/out" || { echo "FAIL p: lane not named"; cat "$TMP/out"; failures=$((failures + 1)); }
fixture p-other-worktree
log_line "$TIP" 120/0/3 0 >"$LOG"
lane_line "$TIP" openapi 0 "$TMP/elsewhere" >>"$LOG"
guard feature --repo "$MAIN"
check "p openapi row of another worktree" 20 "$status"
fixture p-stale
old_tip="$TIP"
branch_change 'printf more >>change.md'
lane_line "$old_tip" openapi 0 >>"$LOG"
guard feature --repo "$MAIN"
check "p openapi row for an older tip" 20 "$status"
grep -Fq "ran on $old_tip" "$TMP/out" || { echo "FAIL p: stale tip not named"; cat "$TMP/out"; failures=$((failures + 1)); }
for suffix in +dirty +moved; do
  fixture "p$suffix"
  lane_line "$TIP$suffix" openapi 0 >>"$LOG"
  guard feature --repo "$MAIN"
  check "p newest openapi row with tip $suffix" 20 "$status"
  grep -Fq "recorded tip $TIP$suffix" "$TMP/out" || { echo "FAIL p: suffix not named"; cat "$TMP/out"; failures=$((failures + 1)); }
done
fixture p-red
lane_line "$TIP" openapi 1 >>"$LOG"
guard feature --repo "$MAIN"
check "p newest openapi row red after a green one" 20 "$status"
grep -Fq "recorded lane-exit=1" "$TMP/out" || { echo "FAIL p: red not named"; cat "$TMP/out"; failures=$((failures + 1)); }
fixture p-red-then-green
{ lane_line "$TIP" openapi 1; lane_line "$TIP" openapi 0; } >>"$LOG"
guard feature --repo "$MAIN"
check "p newest openapi row green after a red one" 0 "$status"

# (q) frontend is required unless the diff is only docs/, tools/training/ or Markdown, renames and deletions included.
frontend_changes=(
  "mkdir -p third-party/npm && printf x >third-party/npm/frontend-license-overrides.json"
  "mkdir -p .github/workflows && printf x >.github/workflows/build-and-test.yml"
  "printf x >XE-Local-AI-Engine.Client.React/new.ts"
  "printf x >Directory.Packages.props"
  "mkdir -p sub/Proj && printf x >sub/Proj/Proj.csproj"
  "mkdir -p scripts/compliance && printf x >scripts/compliance/overrides.json"
  "mkdir -p elsewhere && fg mv XE-Local-AI-Engine.Client.React/app.ts elsewhere/app.ts"
  "fg rm -q XE-Local-AI-Engine.Client.React/app.ts"
)
i=0
for change in "${frontend_changes[@]}"; do
  i=$((i + 1))
  fixture "q$i"
  branch_change "$change" openapi
  guard feature --repo "$MAIN"
  check "q frontend required, only openapi green: $change" 20 "$status"
  grep -Fq "no frontend lane run recorded" "$TMP/out" || { echo "FAIL q: frontend not named"; cat "$TMP/out"; failures=$((failures + 1)); }
  log_line "$TIP" 120/0/3 0 >"$LOG"
  lane_line "$TIP" openapi 0 >>"$LOG"
  lane_line "$TIP" frontend 0 >>"$LOG"
  guard feature --repo "$MAIN"
  check "q frontend required, both green: $change" 0 "$status"
done
# A diff of only docs/, tools/training/ and Markdown at any depth needs no frontend row.
fixture q-docs-only
branch_change "mkdir -p docs/wiki tools/training sub && printf x >docs/wiki/page.txt && printf x >tools/training/run.py && printf x >sub/notes.md" openapi
guard feature --repo "$MAIN"
check "q docs-only diff, only openapi green" 0 "$status"
# A frontend-only diff still requires openapi.
fixture q-frontend-only
branch_change "printf x >XE-Local-AI-Engine.Client.React/new.ts" frontend
guard feature --repo "$MAIN"
check "q frontend-only diff without an openapi row" 20 "$status"
grep -Fq "no openapi lane run recorded" "$TMP/out" || { echo "FAIL q: openapi not named"; cat "$TMP/out"; failures=$((failures + 1)); }
# A merge base that cannot be read requires every lane. The git stub fails only that query.
mkdir -p "$TMP/nobase-bin"
cat >"$TMP/nobase-bin/git" <<'STUB'
#!/usr/bin/env bash
for a in "$@"; do [[ "$a" == --is-ancestor ]] && exec "$REAL_GIT" "$@"; done
for a in "$@"; do [[ "$a" == merge-base ]] && exit 1; done
exec "$REAL_GIT" "$@"
STUB
chmod +x "$TMP/nobase-bin/git"
NOBASE=(REAL_GIT="$(command -v git)" PATH="$TMP/nobase-bin:$PATH")
fixture q-no-base
env "${SCRUB[@]}" "${NOBASE[@]}" "$GUARD" feature --repo "$MAIN" >"$TMP/out" 2>&1
check "q no merge base, only openapi green" 20 $?
grep -Fq "every lane is required" "$TMP/out" || { echo "FAIL q: no-base reason missing"; cat "$TMP/out"; failures=$((failures + 1)); }
lane_line "$TIP" frontend 0 >>"$LOG"
env "${SCRUB[@]}" "${NOBASE[@]}" "$GUARD" feature --repo "$MAIN" >"$TMP/out" 2>&1
# Every lane is green, but without a merge base the allowlists cannot be compared: that fails closed too.
check "q no merge base, every lane green" 21 $?
grep -Fq "ratchet-baseline-check: ERROR" "$TMP/out" || { echo "FAIL q: no-base ratchet error missing"; cat "$TMP/out"; failures=$((failures + 1)); }

# (r) a shrink-only allowlist: a new file is allowed once; widened against develop it fails 21 unless the operator
# gives a reason, which is printed.
ALLOWLIST=XE-Local-AI-Engine.Tests/Architecture/TupleAllowlist.txt
fixture r
branch_change "mkdir -p ${ALLOWLIST%/*} && printf 'a.cs|3\n' >$ALLOWLIST" openapi frontend
guard feature --repo "$MAIN"
check "r new allowlist file" 0 "$status"
grep -Fq "NEW $ALLOWLIST" "$TMP/out" || { echo "FAIL r: new file not named"; cat "$TMP/out"; failures=$((failures + 1)); }
fg -C "$MAIN" merge -q --ff-only feature
branch_change "printf 'a.cs|4\n' >$ALLOWLIST" openapi frontend
guard feature --repo "$MAIN"
check "r raised allowlist limit" 21 "$status"
grep -Fq "RAISED $ALLOWLIST: a.cs 3 -> 4" "$TMP/out" || { echo "FAIL r: raise not named"; cat "$TMP/out"; failures=$((failures + 1)); }
env "${SCRUB[@]}" XE_RATCHET_WIDEN_REASON="reviewed" "$GUARD" feature --repo "$MAIN" >"$TMP/out" 2>&1
check "r raised limit with the operator's reason" 0 $?
grep -Fq "accepted by XE_RATCHET_WIDEN_REASON: reviewed" "$TMP/out" || { echo "FAIL r: reason not printed"; cat "$TMP/out"; failures=$((failures + 1)); }

# (r) a new guard list plus its parser entry on the branch: the guard runs develop's checker, which cannot parse the
# list, and still allows it once and names it. Once the list is on develop, a checker without its entry fails 21.
NEW_LIST=XE-Local-AI-Engine.Tests/Architecture/NewGuardAllowlist.txt
fixture r-unparsed
branch_change "mkdir -p ${NEW_LIST%/*} scripts && printf 'Some.Type\n' >$NEW_LIST && printf '# parser entry\n' >scripts/ratchet-baseline-check.py" openapi frontend
guard feature --repo "$MAIN"
check "r new unparsed allowlist" 0 "$status"
grep -Fq "NEW (unparsed by this checker) $NEW_LIST" "$TMP/out" || { echo "FAIL r: unparsed new list not named"; cat "$TMP/out"; failures=$((failures + 1)); }
fg -C "$MAIN" merge -q --ff-only feature
branch_change "printf 'Other.Type\n' >>$NEW_LIST" openapi frontend
guard feature --repo "$MAIN"
check "r unparsed allowlist at the merge base" 21 "$status"

# (s) a python3 older than the checker's floor first on PATH (it reports 3.12 and, as 3.12 does, lacks
# PurePath.full_match) fails 21 with the checker's reason, never a traceback, also on a branch Biome's includes judge.
mkdir -p "$TMP/oldpy-bin" "$TMP/oldpy-site"
cat >"$TMP/oldpy-site/sitecustomize.py" <<'SITE'
import pathlib, sys
sys.version_info = (3, 12, 9, "final", 0)
for owner in pathlib.PurePosixPath.__mro__:
    if "full_match" in vars(owner):
        delattr(owner, "full_match")
SITE
printf '#!/bin/sh\nPYTHONPATH=%q exec %q "$@"\n' "$TMP/oldpy-site" "$(command -v python3)" >"$TMP/oldpy-bin/python3"
chmod +x "$TMP/oldpy-bin/python3"
fixture s
branch_change "printf '{\"overrides\":[{\"includes\":[\"src/**\"]}]}' >XE-Local-AI-Engine.Client.React/biome.json" openapi frontend
env "${SCRUB[@]}" PATH="$TMP/oldpy-bin:$PATH" "$GUARD" feature --repo "$MAIN" >"$TMP/out" 2>&1
check "s python3 older than 3.13" 21 $?
grep -Fq "ratchet-baseline-check: ERROR needs Python 3.13 or newer" "$TMP/out" \
  || { echo "FAIL s: the interpreter floor is not named"; cat "$TMP/out"; failures=$((failures + 1)); }
! grep -Fq "Traceback" "$TMP/out" || { echo "FAIL s: a traceback"; cat "$TMP/out"; failures=$((failures + 1)); }

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
