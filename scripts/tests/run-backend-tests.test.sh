#!/usr/bin/env bash
# Contract test for scripts/run-backend-tests.sh against a fake repository: nothing here builds or
# runs a real test project. `dotnet`, the build lock, the assembly guard and the memory-safe runner
# are all stubs that record their arguments.

set -euo pipefail

# From this file's location, not `git rev-parse`: an exported GIT_DIR would answer for another repository.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
FAKE="$TMP/repo"
mkdir -p "$FAKE/scripts/lib" "$FAKE/bin"
cp "$ROOT/scripts/run-backend-tests.sh" "$FAKE/scripts/"
cp "$ROOT"/scripts/lib/*.sh "$FAKE/scripts/lib/"
# Pinned free RAM: the default widths asserted below must not depend on how loaded this machine is.
printf 'MemAvailable:   67108864 kB\n' >"$TMP/meminfo-64g"
printf 'MemAvailable:    6291456 kB\n' >"$TMP/meminfo-6g"
export XE_SIZING_MEMINFO="$TMP/meminfo-64g"

write_solution() {
  {
    echo '<Solution>'
    local project
    for project in "$@"; do printf '    <Project Path="%s/%s.csproj"/>\n' "$project" "$project"; done
    echo '</Solution>'
  } >"$FAKE/XE-Local-AI-Engine.slnx"
}

ALL_PROJECTS=(
  XE-Local-AI-Engine.Tests
  XE-Local-AI-Engine.AI.Agent.Tests
  XE-Local-AI-Engine.Client.Persistence.Tests
  XE-Local-AI-Engine.Tests.E2ETests
)
write_solution "${ALL_PROJECTS[@]}"

cat >"$FAKE/scripts/with-build-lock.sh" <<'EOF'
#!/usr/bin/env bash
shift
export XE_BUILD_LOCK_HELD=1
exec "$@"
EOF

# `guard --test-bins -- <cmd>`: runs the command, then reports FAKE_GUARD_EXIT instead of its status.
cat >"$FAKE/scripts/assembly-guard.sh" <<'EOF'
#!/usr/bin/env bash
echo "guard $*" >>"$FAKE_LOG"
while (($#)); do [[ "$1" == "--" ]] && { shift; break; }; shift; done
"$@"
status=$?
[[ -n "${FAKE_GUARD_EXIT:-}" ]] && exit "$FAKE_GUARD_EXIT"
exit "$status"
EOF

cat >"$FAKE/scripts/run-tests-memory-safe.sh" <<'EOF'
#!/usr/bin/env bash
echo "runner NO_BUILD=${NO_BUILD:-} COVERAGE_DIR=${COVERAGE_DIR:-} JOBS=${JOBS:-} PAR=${PAR:-}" >>"$FAKE_LOG"
if [[ -n "${FAKE_RECORD_ORDER:-}" ]]; then
  echo runner-start >>"$FAKE_LOG"
  echo runner-end >>"$FAKE_LOG"
fi
# Slow AND deliberately TERM-resistant: only the KILL escalation can end this one. An ignored
# disposition is inherited, so its sleep resists TERM too.
if [[ -n "${FAKE_RUNNER_SLEEP:-}" ]]; then
  trap '' TERM
  echo "$$" >>"$FAKE_PIDS"
  sleep "$FAKE_RUNNER_SLEEP" & echo "$!" >>"$FAKE_PIDS"
  wait
  exit 0
fi
echo "TOTAL: pass=7 fail=0 peakRSS(any batch)=1MB"
echo "ALL NAMESPACE BATCHES GREEN"
exit "${FAKE_RUNNER_EXIT:-0}"
EOF

# Stands in for the whole `dotnet` CLI: only `test` is interesting, the rest just has to succeed.
cat >"$FAKE/bin/dotnet" <<'EOF'
#!/usr/bin/env bash
if [[ "$1" == "build" ]]; then
  echo "build $2" >>"$FAKE_LOG"
  exit "${FAKE_BUILD_EXIT:-0}"
fi
[[ "$1" == "test" ]] || exit 0
project="$2"; max=""; results=""; coverage=""
while (($#)); do
  case "$1" in
    --maximum-parallel-tests) max="$2"; shift 2 ;;
    --results-directory) results="$2"; shift 2 ;;
    --coverage) coverage=1; shift ;;
    *) shift ;;
  esac
done
echo "test project=$project max=$max results=$results coverage=$coverage" >>"$FAKE_LOG"
if [[ -n "${FAKE_RECORD_ORDER:-}" ]]; then
  echo "test-start $project" >>"$FAKE_LOG"
  echo "test-end $project" >>"$FAKE_LOG"
fi
# Slow mode for the cancellation case: register this process AND a grandchild, then block. The
# grandchild is the point — it proves the signal reached the whole process group, not just the lane.
if [[ -n "${FAKE_SLEEP:-}" ]]; then
  echo "$$" >>"$FAKE_PIDS"
  sleep "$FAKE_SLEEP" & echo "$!" >>"$FAKE_PIDS"
  wait
  exit 0
fi
[[ -n "${FAKE_NO_SUMMARY:-}" ]] && exit 1
if [[ -n "${FAKE_ALL_SKIPPED:-}" ]]; then
  printf 'Test run summary: Passed!\n  total: 3\n  failed: 0\n  succeeded: 0\n  skipped: 3\n'
  exit 0
fi
cat <<'SUMMARY'
Test run summary: Passed!
  total: 3
  failed: 0
  succeeded: 3
  skipped: 0
SUMMARY
exit "${FAKE_TEST_EXIT:-0}"
EOF
# Records the environment it inherited, so the scrub of the gate's own knobs is observable.
cat >"$FAKE/scripts/run-release-contract-tests.sh" <<'EOF'
#!/usr/bin/env bash
env >"${FAKE_LOG}.contract-env"
echo "contract JOBS=${JOBS:-} COVERAGE_DIR=${COVERAGE_DIR:-} XE_TEST_WIDTH_DEFAULT=${XE_TEST_WIDTH_DEFAULT:-} TEST_GROUPS=${TEST_GROUPS:-} TEST_SHARD=${TEST_SHARD:-}" >>"$FAKE_LOG"
echo "[release-contract] scripts/tests/fake.test.sh"
if [[ -n "${FAKE_CONTRACT_EXIT:-}" ]]; then
  echo "ERROR: fake contract failure" >&2
  exit "$FAKE_CONTRACT_EXIT"
fi
echo "[release-contract] 5/5 test files passed"
echo "run-release-contract-tests.sh: PASS"
EOF

# The gate's four diff questions are answered from FAKE_GIT_*: FAKE_GIT_REF names the one ref that
# exists (default develop; "none" for neither), FAKE_GIT_NO_MERGE_BASE stands in for a shallow clone,
# FAKE_GIT_DIFF / FAKE_GIT_UNTRACKED are the changed and untracked paths, filtered by any pathspec
# after `--` the way git filters them. Anything else — the lock wrapper's `git -C … rev-parse` — goes
# to the real git. Path quoting is NOT faked: the real-git case below covers it.
export REAL_GIT
REAL_GIT="$(command -v git)"
REAL_PATH="$PATH"
cat >"$FAKE/bin/git" <<'EOF'
#!/usr/bin/env bash
list_paths() {
  local list="$1" line spec; shift
  local -a specs=()
  while (($#)); do [[ "$1" == "--" ]] && { shift; specs=("$@"); break; }; shift; done
  while IFS= read -r line; do
    [[ -n "$line" ]] || continue
    (( ${#specs[@]} )) || { printf '%s\n' "$line"; continue; }
    for spec in "${specs[@]}"; do [[ "$line" == "$spec"/* ]] && { printf '%s\n' "$line"; break; }; done
  done <<<"$list"
}
case "$1" in
  rev-parse)  [[ "$2 $3" == "--verify -q" ]] || exec "$REAL_GIT" "$@"
              [[ "$4" == "${FAKE_GIT_REF:-develop}^{commit}" ]] ;;
  merge-base) [[ -z "${FAKE_GIT_NO_MERGE_BASE:-}" ]] && echo 0123abc ;;
  diff)       list_paths "${FAKE_GIT_DIFF:-}" "$@" ;;
  ls-files)   list_paths "${FAKE_GIT_UNTRACKED:-}" "$@" ;;
  *)          exec "$REAL_GIT" "$@" ;;
esac
EOF
chmod +x "$FAKE/scripts/with-build-lock.sh" "$FAKE/scripts/assembly-guard.sh" \
  "$FAKE/scripts/run-tests-memory-safe.sh" "$FAKE/scripts/run-release-contract-tests.sh" \
  "$FAKE/bin/dotnet" "$FAKE/bin/git"
export PATH="$FAKE/bin:$PATH"

# Runs the gate with NO_BUILD=1 (the stub dotnet builds nothing) and captures output + status.
# Arguments after the case name are env assignments; GATE_ARGS carries the script's own flags.
# GITHUB_ACTIONS, CI and XE_GATE_CONTRACT_TESTS are blanked so the contract-lane decision does not
# depend on where this file runs (a runner sets GITHUB_ACTIONS=true and CI=true); a case that wants
# them passes them after `env`.
GATE_ARGS=()
run_gate() {
  local name="$1"; shift
  : >"$TMP/$name.log"
  set +e
  output="$(FAKE_LOG="$TMP/$name.log" NO_BUILD=1 GITHUB_ACTIONS='' CI='' XE_GATE_CONTRACT_TESTS='' "$@" \
    "$FAKE/scripts/run-backend-tests.sh" ${GATE_ARGS[@]+"${GATE_ARGS[@]}"} 2>&1)"
  status=$?
  set -e
}

# `! grep` would silently skip errexit, so a miss has to be reported explicitly.
refute_grep() {
  if grep "$@" >/dev/null; then
    echo "unexpected match: grep $*" >&2
    exit 1
  fi
}

# --- enrolment: E2E excluded, the batched module goes to the runner, the rest to `dotnet test` ---
run_gate enrol env
# With a message, because a bare `[[ ]]` under `set -e` fails this file silently — and the gate's
# own output is the only thing that says why.
[[ "$status" -eq 0 ]] || { echo "enrolment run exited $status" >&2; printf '%s\n' "$output" >&2; exit 1; }
grep -Fq 'BACKEND GATE GREEN' <<<"$output"
grep -Fq 'Enrolled test project: XE-Local-AI-Engine.AI.Agent.Tests/' <<<"$output"
refute_grep -F 'E2ETests' <<<"$output"
refute_grep -F 'E2ETests' "$TMP/enrol.log"
grep -Fq 'runner NO_BUILD=1 COVERAGE_DIR=' "$TMP/enrol.log"
# One `dotnet test` per sibling, none for the batched module.
[[ "$(grep -c '^test project=' "$TMP/enrol.log")" -eq 2 ]]
refute_grep '^test project=XE-Local-AI-Engine.Tests/' "$TMP/enrol.log"
# Siblings are guarded and each gets its own results directory.
[[ "$(grep -c '^guard guard --test-bins --' "$TMP/enrol.log")" -eq 2 ]]
grep -q 'results=.*/XE-Local-AI-Engine.AI.Agent.Tests ' "$TMP/enrol.log"
grep -q 'results=.*/XE-Local-AI-Engine.Client.Persistence.Tests ' "$TMP/enrol.log"

# --- the normal exit path is clean: exit 0, GREEN last, and no shell diagnostics ---
# `set -u` plus a variable the exit path reads before it is initialised would print "unbound
# variable" AFTER the green line and exit 1 — a gate that reports success and then fails.
run_gate clean-exit env
[[ "$status" -eq 0 ]] || { echo "clean exit path returned $status" >&2; printf '%s\n' "$output" >&2; exit 1; }
last_line="$(printf '%s\n' "$output" | grep -v '^[[:space:]]*$' | tail -1)"
[[ "$last_line" == "BACKEND GATE GREEN" ]] || { echo "last line was '$last_line'" >&2; exit 1; }
refute_grep -E 'unbound variable|command not found|: line [0-9]+:' <<<"$output"

# --- width map: defaults, then per-project and global overrides ---
grep -q '^test project=XE-Local-AI-Engine.Client.Persistence.Tests/.* max=4 ' "$TMP/enrol.log"
grep -q '^test project=XE-Local-AI-Engine.AI.Agent.Tests/.* max=8 ' "$TMP/enrol.log"

run_gate widths env XE_TEST_WIDTH_Client_Persistence_Tests=2 XE_TEST_WIDTH_AI_Agent_Tests=3
[[ "$status" -eq 0 ]]
grep -q '^test project=XE-Local-AI-Engine.Client.Persistence.Tests/.* max=2 ' "$TMP/widths.log"
grep -q '^test project=XE-Local-AI-Engine.AI.Agent.Tests/.* max=3 ' "$TMP/widths.log"

run_gate width-default env XE_TEST_WIDTH_DEFAULT=2
[[ "$status" -eq 0 ]]
[[ "$(grep -c 'max=2 ' "$TMP/width-default.log")" -eq 2 ]]

run_gate width-invalid env XE_TEST_WIDTH_DEFAULT=nope
[[ "$status" -eq 2 ]]
grep -Fq 'must be a positive integer' <<<"$output"

# --- low-memory profile: conservative defaults and no overlap across any project lane ---
run_gate low-memory env XE_TEST_PROFILE=low-memory FAKE_RECORD_ORDER=1
[[ "$status" -eq 0 ]]
grep -Fq 'runner NO_BUILD=1 COVERAGE_DIR= JOBS=1 PAR=1' "$TMP/low-memory.log"
[[ "$(grep -c ' max=1 ' "$TMP/low-memory.log")" -eq 2 ]]
mapfile -t lane_receipts < <(grep -E '^(runner|test)-(start|end)' "$TMP/low-memory.log")
[[ "${#lane_receipts[@]}" -eq 6 ]]
for ((i = 0; i < 6; i += 2)); do
  [[ "${lane_receipts[i]}" == *-start* && "${lane_receipts[i+1]}" == *-end* ]]
done

# Explicit tuning still wins inside the profile.
run_gate low-memory-overrides env XE_TEST_PROFILE=low-memory JOBS=2 PAR=3 XE_TEST_WIDTH_DEFAULT=4
[[ "$status" -eq 0 ]]
grep -Fq 'runner NO_BUILD=1 COVERAGE_DIR= JOBS=2 PAR=3' "$TMP/low-memory-overrides.log"
[[ "$(grep -c ' max=4 ' "$TMP/low-memory-overrides.log")" -eq 2 ]]

run_gate low-memory-project-override env XE_TEST_PROFILE=low-memory XE_TEST_WIDTH_AI_Agent_Tests=3
[[ "$status" -eq 0 ]]
grep -q '^test project=XE-Local-AI-Engine.AI.Agent.Tests/.* max=3 ' "$TMP/low-memory-project-override.log"
grep -q '^test project=XE-Local-AI-Engine.Client.Persistence.Tests/.* max=1 ' "$TMP/low-memory-project-override.log"

# A red lane is recorded, but does not prevent the remaining project lanes from running.
run_gate low-memory-red env XE_TEST_PROFILE=low-memory FAKE_RUNNER_EXIT=1
[[ "$status" -eq 1 ]]
[[ "$(grep -c '^test project=' "$TMP/low-memory-red.log")" -eq 2 ]]

# --- RAM sizing (no profile): JOBS computed once and exported; at JOBS=1 widths drop, lanes stay concurrent ---
run_gate sizing-line env
grep -Fq '>> Sizing: MemAvailable=64.0 GB' <<<"$output"

run_gate sizing-low env XE_SIZING_MEMINFO="$TMP/meminfo-6g" FAKE_RECORD_ORDER=1
[[ "$status" -eq 0 ]] || { echo "sizing-low exited $status" >&2; printf '%s\n' "$output" >&2; exit 1; }
grep -Fq '>> Sizing: MemAvailable=6.0 GB → JOBS=1' <<<"$output"
grep -Fq 'runner NO_BUILD=1 COVERAGE_DIR= JOBS=1 PAR=' "$TMP/sizing-low.log"
[[ "$(grep -c ' max=1 ' "$TMP/sizing-low.log")" -eq 2 ]]

run_gate sizing-explicit env XE_SIZING_MEMINFO="$TMP/meminfo-6g" JOBS=3
[[ "$status" -eq 0 ]]
refute_grep -F '>> Sizing:' <<<"$output"
grep -Fq 'runner NO_BUILD=1 COVERAGE_DIR= JOBS=3 PAR=' "$TMP/sizing-explicit.log"
grep -q '^test project=XE-Local-AI-Engine.AI.Agent.Tests/.* max=8 ' "$TMP/sizing-explicit.log"

run_gate profile-invalid env XE_TEST_PROFILE=small
[[ "$status" -eq 2 ]]
grep -Fq "XE_TEST_PROFILE must be 'low-memory' or unset" <<<"$output"

# --- coverage mode: reports per project, and the siblings run UNguarded (static instrumentation) ---
run_gate coverage env COVERAGE_DIR="$TMP/cov"
[[ "$status" -eq 0 ]]
grep -q 'results=.*/cov/XE-Local-AI-Engine.AI.Agent.Tests coverage=1' "$TMP/coverage.log"
grep -Fq 'COVERAGE_DIR=' "$TMP/coverage.log"
grep -Fq "COVERAGE_DIR=$TMP/cov/XE-Local-AI-Engine.Tests" "$TMP/coverage.log"
refute_grep '^guard ' "$TMP/coverage.log"

# --- --siblings-only: the CI leg shape, no runner lane ---
GATE_ARGS=(--siblings-only)
run_gate siblings-only env
GATE_ARGS=()
[[ "$status" -eq 0 ]]
refute_grep '^runner ' "$TMP/siblings-only.log"
[[ "$(grep -c '^test project=' "$TMP/siblings-only.log")" -eq 2 ]]

# --- release contract tests: a lane when scripts/, publish/ or .github/workflows/ changed ---
# Nothing changed: one line says why, and the runner is never called.
run_gate contract-untouched env FAKE_GIT_DIFF=$'README.md\nXE-Local-AI-Engine.Client/Program.cs\n.github/dependabot.yml\nscriptsx/a.sh\n'
[[ "$status" -eq 0 ]] || { echo "contract-untouched exited $status" >&2; printf '%s\n' "$output" >&2; exit 1; }
grep -Fq '>> Release contract tests: skipped (nothing under scripts/, publish/ or .github/workflows/ changed since the merge-base with develop; XE_GATE_CONTRACT_TESTS=run forces them)' <<<"$output"
refute_grep '^contract ' "$TMP/contract-untouched.log"

# An untracked script counts, and the lane runs with the gate's own knobs scrubbed from its env.
# TEST_GROUPS/TEST_SHARD are the batched runner's knobs: inherited, they make fixture-based contract
# tests select nothing.
run_gate contract-untracked env FAKE_GIT_UNTRACKED=$'scripts/new.sh\n' XE_TEST_WIDTH_DEFAULT=2 \
  TEST_GROUPS=16 TEST_SHARD=1/4
[[ "$status" -eq 0 ]] || { echo "contract-untracked exited $status" >&2; printf '%s\n' "$output" >&2; exit 1; }
grep -Fq '>> Release contract tests: will run (scripts/new.sh changed since the merge-base with develop)' <<<"$output"
grep -Fxq 'contract JOBS= COVERAGE_DIR= XE_TEST_WIDTH_DEFAULT= TEST_GROUPS= TEST_SHARD=' "$TMP/contract-untracked.log" \
  || { echo "contract lane env not scrubbed: $(grep '^contract ' "$TMP/contract-untracked.log")" >&2; exit 1; }
grep -Eq '^release-contract-tests +5 +0 +0 +[0-9]+s +0$' <<<"$output"
grep -Fq 'BACKEND GATE GREEN' <<<"$output"

# The WHOLE scrub list, read from the gate itself so the list and this assertion cannot drift: every
# named knob and one probe per prefix is exported (with a value the gate accepts) and none may reach
# the lane.
gate_source="$ROOT/scripts/run-backend-tests.sh"
read -r -a SCRUB_NAMES <<<"$(sed -n '/^CONTRACT_LANE_SCRUB=(/,/^)/p' "$gate_source" | sed '1d;$d' | tr '\n' ' ')"
read -r -a SCRUB_PREFIXES <<<"$(sed -n 's/^CONTRACT_LANE_SCRUB_PREFIXES=(\(.*\))$/\1/p' "$gate_source")"
(( ${#SCRUB_NAMES[@]} >= 10 && ${#SCRUB_PREFIXES[@]} >= 3 )) \
  || { echo "scrub lists not found in $gate_source: ${SCRUB_NAMES[*]} / ${SCRUB_PREFIXES[*]}" >&2; exit 1; }
declare -A scrub_value=([XE_TEST_PROFILE]=low-memory [XE_GATE_CANCEL_GRACE]=5 [XE_GATE_CONTRACT_TESTS]=run
  [COVERAGE_DIR]="$TMP/scrub-cov" [JOBS]=2 [TEST_GROUPS]=16 [TEST_SHARD]=1/4)
scrub_env=()
scrub_check=()
for name in "${SCRUB_NAMES[@]}"; do scrub_env+=("$name=${scrub_value[$name]:-1}"); scrub_check+=("$name"); done
for prefix in "${SCRUB_PREFIXES[@]}"; do scrub_env+=("${prefix}SCRUB_PROBE=1"); scrub_check+=("${prefix}SCRUB_PROBE"); done
run_gate contract-scrub env "${scrub_env[@]}"
[[ "$status" -eq 0 ]] || { echo "contract-scrub exited $status" >&2; printf '%s\n' "$output" >&2; exit 1; }
grep -q '^FAKE_LOG=' "$TMP/contract-scrub.log.contract-env"
for name in "${scrub_check[@]}"; do
  if grep -q "^$name=" "$TMP/contract-scrub.log.contract-env"; then
    echo "contract lane inherited $name" >&2; exit 1
  fi
done
# ...and the list is COMPLETE against what the gate and its runner document as knobs: every name on a
# knob line of either usage text ("#   NAME  description", or "#   JOBS, PAR  ...") must be listed or
# covered by a prefix.
documented_knobs=()
knob_line='^#   ([A-Z][A-Z0-9_]*(, [A-Z][A-Z0-9_]*)*)  '
for documented in "$ROOT/scripts/run-backend-tests.sh" "$ROOT/scripts/run-tests-memory-safe.sh"; do
  while IFS= read -r line; do
    [[ "$line" =~ $knob_line ]] || continue
    IFS=', ' read -r -a names <<<"${BASH_REMATCH[1]}"
    documented_knobs+=("${names[@]}")
  done < <(sed -n '1,/^set -uo/p' "$documented")
done
(( ${#documented_knobs[@]} >= 15 )) || { echo "documented knobs not found: ${documented_knobs[*]}" >&2; exit 1; }
for knob in "${documented_knobs[@]}"; do
  covered=""
  for name in "${SCRUB_NAMES[@]}"; do [[ "$knob" == "$name" ]] && covered=1; done
  for prefix in "${SCRUB_PREFIXES[@]}"; do [[ "$knob" == "$prefix"* ]] && covered=1; done
  [[ -n "$covered" ]] || { echo "documented knob $knob is missing from CONTRACT_LANE_SCRUB" >&2; exit 1; }
done

# A committed or uncommitted change under publish/ or .github/workflows/ counts too.
run_gate contract-publish env FAKE_GIT_DIFF=$'README.md\npublish/package-rc.sh\n'
[[ "$status" -eq 0 ]]
grep -Fq 'will run (publish/package-rc.sh changed' <<<"$output"
run_gate contract-workflow env FAKE_GIT_DIFF=$'.github/workflows/e2e.yml\n'
grep -Fq 'will run (.github/workflows/e2e.yml changed' <<<"$output"

# A failing contract run fails the gate, appears in the table and prints its log.
run_gate contract-red env FAKE_GIT_DIFF=$'scripts/dev-start.sh\n' FAKE_CONTRACT_EXIT=1
[[ "$status" -eq 1 ]] || { echo "contract-red exited $status" >&2; printf '%s\n' "$output" >&2; exit 1; }
grep -Fq 'FAILED: release-contract-tests(exit=1)' <<<"$output"
grep -Fq '[release-contract-tests] ERROR: fake contract failure' <<<"$output"
refute_grep -F 'BACKEND GATE GREEN' <<<"$output"

# Cannot tell = run, and say why: no develop ref, or no merge-base (a shallow clone).
run_gate contract-no-ref env FAKE_GIT_REF=none
grep -Fq 'will run (no develop or origin/develop ref' <<<"$output"
grep -q '^contract ' "$TMP/contract-no-ref.log"
run_gate contract-shallow env FAKE_GIT_NO_MERGE_BASE=1
grep -Fq 'will run (no merge-base of HEAD with develop (shallow clone?)' <<<"$output"
run_gate contract-origin env FAKE_GIT_REF=origin/develop
grep -Fq 'changed since the merge-base with origin/develop;' <<<"$output"

# The override forces either way; anything else is a usage error before any build.
run_gate contract-force env XE_GATE_CONTRACT_TESTS=run
grep -Fq 'will run (forced by XE_GATE_CONTRACT_TESTS=run)' <<<"$output"
grep -q '^contract ' "$TMP/contract-force.log"
run_gate contract-skip env XE_GATE_CONTRACT_TESTS=skip FAKE_GIT_DIFF=$'scripts/dev-start.sh\n'
grep -Fq 'skipped (XE_GATE_CONTRACT_TESTS=skip;' <<<"$output"
refute_grep '^contract ' "$TMP/contract-skip.log"
run_gate contract-invalid env XE_GATE_CONTRACT_TESTS=yes NO_BUILD=
[[ "$status" -eq 2 ]]
grep -Fq "XE_GATE_CONTRACT_TESTS must be run, skip or auto, got 'yes'" <<<"$output"
refute_grep '^build ' "$TMP/contract-invalid.log"

# The CI `siblings` leg shape is unchanged: a GitHub Actions runner has its own release-contracts job.
GATE_ARGS=(--siblings-only)
run_gate contract-ci env GITHUB_ACTIONS=true CI=true COVERAGE_DIR="$TMP/ci-cov" XE_TEST_WIDTH_DEFAULT=2 \
  FAKE_GIT_DIFF=$'scripts/dev-start.sh\n'
GATE_ARGS=()
[[ "$status" -eq 0 ]]
grep -Fq 'skipped (GITHUB_ACTIONS=true; that workflow runs them in its own release-contracts job;' <<<"$output"
refute_grep '^contract ' "$TMP/contract-ci.log"
[[ "$(grep -c '^test project=' "$TMP/contract-ci.log")" -eq 2 ]]
# A developer shell that merely exports CI (any value) is not that runner: the lane still runs.
for ci_value in true false 1; do
  run_gate "contract-ci-$ci_value" env CI="$ci_value" FAKE_GIT_DIFF=$'scripts/dev-start.sh\n'
  grep -Fq '>> Release contract tests: will run (scripts/dev-start.sh changed' <<<"$output" \
    || { echo "CI=$ci_value skipped the contract lane:" >&2; printf '%s\n' "$output" >&2; exit 1; }
  grep -q '^contract ' "$TMP/contract-ci-$ci_value.log"
done
run_gate contract-gha-false env GITHUB_ACTIONS=false FAKE_GIT_DIFF=$'scripts/dev-start.sh\n'
grep -q '^contract ' "$TMP/contract-gha-false.log"

# --- release contract tests against REAL git: git quotes unusual paths ("scripts/caf\303\251.sh") ---
# A throwaway repository whose develop holds the fake gate as committed, then a feature branch. The
# fixture-local identity is the repository's own throwaway one, as in
# scripts/performance/tests/test_capture_inference_evidence.py.
#
# Isolation: git picks the repository from the environment BEFORE -C, so an exported GIT_DIR,
# GIT_WORK_TREE or GIT_INDEX_FILE (agents commit through a private GIT_INDEX_FILE) would send the
# fixture's init, config, add and commit into that repository. Every fixture git and gate call runs
# with all GIT_* variables removed and no global or system config, and nothing is written before git
# has been shown to resolve to the temporary directory.
GITFAKE="$TMP/gitrepo"
mkdir -p "$GITFAKE/bin"
cp -r "$FAKE/scripts" "$GITFAKE/"
cp "$FAKE/XE-Local-AI-Engine.slnx" "$GITFAKE/"
cp "$FAKE/bin/dotnet" "$GITFAKE/bin/"
printf '.tmp/\n' >"$GITFAKE/.gitignore"
# The REAL contract runner here, and one contract test that records which checkout it ran from: proof
# of whose tests the lane ran comes from the test itself, not from a stub that prints success.
cp "$ROOT/scripts/run-release-contract-tests.sh" "$GITFAKE/scripts/"
mkdir -p "$GITFAKE/scripts/tests"
cat >"$GITFAKE/scripts/tests/marker.test.sh" <<'EOF'
#!/usr/bin/env bash
echo "contract-marker $(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)" >>"$FAKE_LOG"
echo "marker.test.sh: PASS"
EOF
chmod +x "$GITFAKE/scripts/tests/marker.test.sh"
GIT_ENV_SCRUB=()
for var in $(compgen -v GIT_); do GIT_ENV_SCRUB+=(-u "$var"); done
GIT_ENV_SCRUB+=(GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1 GIT_CEILING_DIRECTORIES="$TMP")
fixture_git() { env "${GIT_ENV_SCRUB[@]}" "$REAL_GIT" -C "$GITFAKE" "$@"; }
fixture_git init -q -b develop
gitfake_real="$(cd "$GITFAKE" && pwd -P)"
fixture_git_dir="$(fixture_git rev-parse --absolute-git-dir)"
fixture_top="$(fixture_git rev-parse --show-toplevel)"
if [[ "$fixture_git_dir" != "$gitfake_real/.git" || "$fixture_top" != "$gitfake_real" ]]; then
  echo "REFUSING: fixture git resolves to git-dir '$fixture_git_dir', top '$fixture_top', not $gitfake_real" >&2
  exit 1
fi
fixture_git config user.email fixture@example.invalid
fixture_git config user.name "Contract Fixture"
fixture_git add -A
fixture_git commit -q -m fixture
fixture_git checkout -q -b feature
# A clean clone: the repository an inherited GIT_DIR/GIT_WORK_TREE points the gate at below. Its own
# marker test would record the decoy's path.
env "${GIT_ENV_SCRUB[@]}" "$REAL_GIT" clone -q "$GITFAKE" "$TMP/decoy"
decoy_real="$(cd "$TMP/decoy" && pwd -P)"
# Arguments are extra env assignments, applied after the scrub so a case can export git variables on purpose.
real_git_gate() {
  local name="$1"; shift
  : >"$TMP/$name.log"
  set +e
  output="$(env "${GIT_ENV_SCRUB[@]}" FAKE_LOG="$TMP/$name.log" NO_BUILD=1 GITHUB_ACTIONS='' CI='' \
    XE_GATE_CONTRACT_TESTS='' PATH="$GITFAKE/bin:$REAL_PATH" "$@" "$GITFAKE/scripts/run-backend-tests.sh" 2>&1)"
  status=$?
  set -e
}
real_git_gate real-git-clean
[[ "$status" -eq 0 ]] || { echo "real-git-clean exited $status" >&2; printf '%s\n' "$output" >&2; exit 1; }
grep -Fq '>> Release contract tests: skipped (nothing under scripts/' <<<"$output"
printf 'echo\n' >"$GITFAKE/scripts/"$'caf\xc3\xa9.sh'
real_git_gate real-git-quoted
[[ "$status" -eq 0 ]] || { echo "real-git-quoted exited $status" >&2; printf '%s\n' "$output" >&2; exit 1; }
grep -Fq '>> Release contract tests: will run (' <<<"$output" \
  || { echo "untracked non-ASCII script did not run the lane:" >&2; printf '%s\n' "$output" >&2; exit 1; }
grep -Fxq "contract-marker $gitfake_real" "$TMP/real-git-quoted.log"
# Staged, so it is in `git diff` rather than in the untracked list.
fixture_git add -A
real_git_gate real-git-staged
[[ "$status" -eq 0 ]] || { echo "real-git-staged exited $status" >&2; printf '%s\n' "$output" >&2; exit 1; }
grep -Fq '>> Release contract tests: will run (' <<<"$output"
grep -Fxq "contract-marker $gitfake_real" "$TMP/real-git-staged.log"
grep -Eq '^release-contract-tests +1 +0 +0 +[0-9]+s +0$' <<<"$output"
grep -Fq 'BACKEND GATE GREEN' <<<"$output"
# The gate decides from, AND runs the contract tests of, ITS OWN checkout: a caller's GIT_DIR,
# GIT_WORK_TREE and GIT_INDEX_FILE naming a clean sibling checkout must neither turn the staged change
# into "nothing changed" nor make the runner run the sibling's tests and report their PASS.
real_git_gate real-git-inherited GIT_DIR="$TMP/decoy/.git" GIT_WORK_TREE="$TMP/decoy" \
  GIT_INDEX_FILE="$TMP/decoy/.git/index"
grep -Fq '>> Release contract tests: will run (' <<<"$output" \
  || { echo "inherited git variables redirected the gate's diff:" >&2; printf '%s\n' "$output" >&2; exit 1; }
grep -Fxq "contract-marker $gitfake_real" "$TMP/real-git-inherited.log" \
  || { echo "the lane ran another checkout's contract tests: $(grep contract-marker "$TMP/real-git-inherited.log")" >&2; exit 1; }
refute_grep -F "contract-marker $decoy_real" "$TMP/real-git-inherited.log"

# --- hollow-gate guard: a suite that prints no MTP summary is never green ---
run_gate hollow env FAKE_NO_SUMMARY=1
[[ "$status" -eq 1 ]]
grep -Fq 'produced no test-suite summary' <<<"$output"
grep -Fq 'no-summary' <<<"$output"

# --- hollow-gate guard: a suite whose tests all skipped (succeeded 0) is never green ---
run_gate all-skipped env FAKE_ALL_SKIPPED=1
[[ "$status" -eq 1 ]] || { echo "all-skipped run exited $status" >&2; printf '%s\n' "$output" >&2; exit 1; }
grep -Fq 'HOLLOW: all 3 tests skipped in XE-Local-AI-Engine.AI.Agent.Tests' <<<"$output"
grep -Fq 'FAILED: XE-Local-AI-Engine.Client.Persistence.Tests(hollow)' <<<"$output"
grep -Fq 'Skipped (all lanes): 6' <<<"$output"
refute_grep -F 'BACKEND GATE GREEN' <<<"$output"

# --- a failed Release build exits before any lane starts ---
run_gate build-fails env NO_BUILD= FAKE_BUILD_EXIT=1
[[ "$status" -eq 1 ]] || { echo "build-failure run exited $status" >&2; printf '%s\n' "$output" >&2; exit 1; }
grep -Fq 'BUILD FAILED' <<<"$output"
grep -q '^build .*XE-Local-AI-Engine.slnx$' "$TMP/build-fails.log"
refute_grep -E '^(test project=|runner )' "$TMP/build-fails.log"
refute_grep -F 'BACKEND GATE GREEN' <<<"$output"

# --- exit codes: a failing suite is red, contamination (75) outranks it and is neither ---
run_gate failing env FAKE_TEST_EXIT=1
[[ "$status" -eq 1 ]]
grep -Fq 'FAILED: XE-Local-AI-Engine.AI.Agent.Tests(exit=1)' <<<"$output"

run_gate contaminated env FAKE_GUARD_EXIT=75
[[ "$status" -eq 75 ]]
grep -Fq 'RESULT VOID' <<<"$output"
refute_grep -F 'BACKEND GATE GREEN' <<<"$output"

# Contamination outranks a red in the same run.
run_gate contaminated-and-red env FAKE_GUARD_EXIT=75 FAKE_RUNNER_EXIT=1
[[ "$status" -eq 75 ]]

# --- the batched module must be enrolled, or the gate refuses to run ---
write_solution XE-Local-AI-Engine.AI.Agent.Tests XE-Local-AI-Engine.Client.Persistence.Tests
run_gate no-batched env
[[ "$status" -eq 2 ]]
grep -Fq 'is not enrolled' <<<"$output"

write_solution XE-Local-AI-Engine.Tests
run_gate no-siblings env
[[ "$status" -eq 2 ]]
grep -Fq 'no sibling test projects left' <<<"$output"

write_solution XE-Local-AI-Engine.Tests.E2ETests
run_gate only-e2e env
[[ "$status" -eq 2 ]]
grep -Fq 'zero test projects discovered' <<<"$output"

# --- invocation by a relative path from a subdirectory ---
# The script cds to its own repository root, so it must resolve its own path to an absolute one
# BEFORE that: re-exec'ing the caller's spelling through the lock wrapper would otherwise look for
# `./run-backend-tests.sh` under the repo root and die there. This is still the stubbed wrapper.
write_solution "${ALL_PROJECTS[@]}"
: >"$TMP/relative.log"
set +e
output="$(cd "$FAKE/scripts" && FAKE_LOG="$TMP/relative.log" NO_BUILD=1 ./run-backend-tests.sh 2>&1)"
status=$?
set -e
[[ "$status" -eq 0 ]] || { echo "relative invocation exited $status" >&2; printf '%s\n' "$output" >&2; exit 1; }
grep -Fq 'BACKEND GATE GREEN' <<<"$output"
[[ "$(grep -c '^test project=' "$TMP/relative.log")" -eq 2 ]]

# A relative COVERAGE_DIR is resolved against the caller's directory, not the repository root.
: >"$TMP/relative-cov.log"
( cd "$TMP" && FAKE_LOG="$TMP/relative-cov.log" NO_BUILD=1 COVERAGE_DIR=relcov \
    "$FAKE/scripts/run-backend-tests.sh" --siblings-only >/dev/null 2>&1 )
grep -q "results=$TMP/relcov/XE-Local-AI-Engine.AI.Agent.Tests " "$TMP/relative-cov.log"

# --- cancellation: signalling the gate's PROCESS GROUP must leave nothing running ---
# This case uses the REAL scripts/with-build-lock.sh. The wrapper runs its command in the foreground
# and has no traps, so a signal aimed at its PID alone would kill it and orphan the gate: the
# documented way to cancel a non-interactive run is to signal the process group, which is what a
# terminal's Ctrl-C does natively. `set -m` here is what gives the gate a group of its own to signal.
write_solution "${ALL_PROJECTS[@]}"
cp "$ROOT/scripts/with-build-lock.sh" "$FAKE/scripts/with-build-lock.sh"
LOCK="$TMP/build.lock"
PIDS_FILE="$TMP/lane-pids"
: >"$PIDS_FILE"
: >"$TMP/cancel.log"

# Kills anything this case leaves behind, on every exit path, before the temp dir goes away.
# Always succeeds: it is called on the success path too, and every kill in it is expected to find
# nothing once the gate has cleaned up after itself.
cancel_case_cleanup() {
  local pid
  # `|| true` on every kill: under `set -e` a kill that finds nothing already gone would end the
  # whole test file here, which is the one place it must not.
  while read -r pid; do kill -KILL "$pid" 2>/dev/null || true; done <"$PIDS_FILE"
  kill -KILL -- "-$PUBLIC" 2>/dev/null || true
  return 0
}

# The serialized profile waits inside the launch loop. Cancellation must terminate that active
# lane and exit without launching either later sibling.
: >"$PIDS_FILE"
: >"$TMP/low-cancel.log"
set -m
FAKE_LOG="$TMP/low-cancel.log" FAKE_PIDS="$PIDS_FILE" FAKE_RUNNER_SLEEP=120 NO_BUILD=1 \
  XE_TEST_PROFILE=low-memory BUILD_LOCK_FILE="$LOCK" XE_GATE_CANCEL_GRACE=2 \
  "$FAKE/scripts/run-backend-tests.sh" >"$TMP/low-cancel.out" 2>&1 &
PUBLIC=$!
set +m
deadline=$((SECONDS + 60))
while [[ "$(wc -l <"$PIDS_FILE")" -lt 2 ]]; do
  (( SECONDS <= deadline )) || { echo "serialized lane never started" >&2; cancel_case_cleanup; exit 1; }
  # real-timer: the subject is a real OS process group registering its child PIDs.
  sleep 0.2
done
mapfile -t LOW_LANE_PROCS <"$PIDS_FILE"
kill -TERM -- "-$PUBLIC"
set +e
wait "$PUBLIC"
low_cancel_status=$?
set -e
[[ "$low_cancel_status" -eq 143 ]] || { echo "low-memory cancel status was $low_cancel_status" >&2; cancel_case_cleanup; exit 1; }
refute_grep '^test project=' "$TMP/low-cancel.log"
deadline=$((SECONDS + 60))
while :; do
  survivors=()
  for pid in "${LOW_LANE_PROCS[@]}"; do kill -0 "$pid" 2>/dev/null && survivors+=("$pid"); done
  [[ "${#survivors[@]}" -eq 0 ]] && break
  (( SECONDS <= deadline )) || { echo "serialized lane survived cancellation: ${survivors[*]}" >&2; cancel_case_cleanup; exit 1; }
  # real-timer: the assertion waits for real OS processes to disappear after KILL escalation.
  sleep 0.5
done

# The parallel case below owns a fresh receipt set. Keeping the serialized case's two dead PIDs
# would let its six-PID readiness check pass after only four of the six new processes registered.
: >"$PIDS_FILE"

set -m
FAKE_LOG="$TMP/cancel.log" FAKE_PIDS="$PIDS_FILE" FAKE_SLEEP=120 FAKE_RUNNER_SLEEP=120 NO_BUILD=1 \
  BUILD_LOCK_FILE="$LOCK" XE_GATE_CANCEL_GRACE=2 \
  "$FAKE/scripts/run-backend-tests.sh" >"$TMP/cancel.out" 2>&1 &
PUBLIC=$!
set +m

# Three lanes, each registering itself and a grandchild.
deadline=$((SECONDS + 60))
while [[ "$(wc -l <"$PIDS_FILE")" -lt 6 ]]; do
  if (( SECONDS > deadline )); then
    echo "lanes never started: $(cat "$PIDS_FILE")" >&2
    cancel_case_cleanup
    exit 1
  fi
  sleep 0.2
done
mapfile -t LANE_PROCS <"$PIDS_FILE"

kill -TERM -- "-$PUBLIC"
set +e
wait "$PUBLIC"
cancel_status=$?
set -e
[[ "$cancel_status" -eq 143 ]] || {
  echo "cancel status was $cancel_status, wanted 143" >&2; cancel_case_cleanup; exit 1; }
refute_grep -F 'BACKEND GATE GREEN' "$TMP/cancel.out"
refute_grep -E 'unbound variable|command not found|: line [0-9]+:' "$TMP/cancel.out"

# The gate is still winding its lanes down when the wrapper's status arrives, and one lane ignores
# TERM outright, so give the grace period and the KILL escalation time to land — bounded, then fail.
deadline=$((SECONDS + 60))
while :; do
  survivors=()
  for pid in "${LANE_PROCS[@]}"; do
    kill -0 "$pid" 2>/dev/null && survivors+=("$pid")
  done
  [[ "${#survivors[@]}" -eq 0 ]] && break
  if (( SECONDS > deadline )); then
    echo "lane processes survived cancellation: ${survivors[*]}" >&2
    cancel_case_cleanup
    exit 1
  fi
  sleep 0.5
done
# Nothing may still hold the lock once the run is over.
flock -n "$LOCK" -c true || { echo "build lock still held after cancellation" >&2; exit 1; }

# --- a lane that had already finished CONTAMINATED outranks the cancellation status ---
# NO_BUILD_LOCK=1 so the gate itself is the process we signal: through the wrapper, a group signal
# kills that trapless foreground process first and we would read ITS 143, never the gate's verdict.
# The batched lane exits 75 immediately (it does no work at all), so its result is on disk well
# before the two sibling lanes have finished registering, which is what this case waits for.
write_solution "${ALL_PROJECTS[@]}"
: >"$PIDS_FILE"
: >"$TMP/promote.log"
set -m
FAKE_LOG="$TMP/promote.log" FAKE_PIDS="$PIDS_FILE" FAKE_SLEEP=120 FAKE_RUNNER_EXIT=75 \
  NO_BUILD=1 NO_BUILD_LOCK=1 XE_GATE_CANCEL_GRACE=2 \
  "$FAKE/scripts/run-backend-tests.sh" >"$TMP/promote.out" 2>&1 &
PUBLIC=$!
set +m

deadline=$((SECONDS + 60))
while [[ "$(wc -l <"$PIDS_FILE")" -lt 4 ]]; do
  if (( SECONDS > deadline )); then
    echo "sibling lanes never started: $(cat "$PIDS_FILE")" >&2
    cancel_case_cleanup
    exit 1
  fi
  sleep 0.2
done

kill -TERM -- "-$PUBLIC"
set +e
wait "$PUBLIC"
promote_status=$?
set -e
# 75, not 143: the assemblies moved under a lane, and "re-run me" outranks "you cancelled me".
[[ "$promote_status" -eq 75 ]] || {
  echo "cancelled run with a contaminated lane exited $promote_status, wanted 75" >&2
  cat "$TMP/promote.out" >&2
  cancel_case_cleanup
  exit 1
}
cancel_case_cleanup

# NOT tested, by construction: the LAUNCHING branch of request_cancel, which covers a signal landing
# between `lane &` and the `$!` that records it. That window is microseconds wide and cannot be hit
# deterministically from outside the script, so it is correctness by construction, not by test.

echo "run-backend-tests.test.sh: PASS"
