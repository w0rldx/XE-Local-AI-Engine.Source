#!/usr/bin/env bash
# run-docker-smoke-local.sh — OPT-IN real-daemon smoke for the container runtime and the sandbox.
#
# Why this exists
#   The real-daemon suites used to run automatically whenever a Docker socket happened to be
#   present, and CI forced them on with XE_REQUIRE_DOCKER_TESTS=1 plus three `docker pull` steps.
#   That made Docker Hub reachability a hard dependency of every pull request: a registry blip or a
#   rate limit turned a perfectly good branch red for reasons that had nothing to do with it.
#
#   The wire shape those suites used to be the only proof of is now covered in CI without a daemon,
#   by the production client talking to XE-Local-AI-Engine.Testing.FakeDocker over a loopback
#   socket. What a fake can never prove is what is left: that a real daemon HONOURS a flag — that
#   the capability set really is dropped, that a read-only rootfs really refuses a write, that a
#   uid really maps, that a healthcheck really reaches a verdict, that egress really is denied.
#
#   So the daemon suites became opt-in and this script is how they are run. Nothing invokes it
#   automatically. Run it by hand before cutting a tester RC, or after touching the container
#   runtime, the sandbox provider or the External Apps install path.
#
# THE SKIP IS THE FAILURE MODE — exit 0 alone proves nothing
#   All four suites skip themselves when no daemon is usable, and TUnit reports a skip as success.
#   A run that skipped everything therefore exits 0 with a green summary while having verified
#   nothing at all. This script closes that hole the same way run-e2e-local.sh and
#   run-tool-grammar-smoke-local.sh do: it exports XE_REQUIRE_DOCKER_TESTS=1 (which turns every
#   skip into a failure inside the tests), asserts a non-zero discovered test count BEFORE the run,
#   and refuses any summary reporting zero tests or a non-zero skip count.
#
# What it asserts, in order. Each step must produce a verdict; a step that does not run is a
# FAILURE, never a silent skip.
#
#   1. Prerequisites  — dotnet is present. The daemon is NOT probed here: the suites resolve their own
#                       endpoint and the CLI's context is not it (see step 1's comment).
#   2. Build          — the Tests project builds in Release. Debug skips the analyzers.
#   3. Discovery      — the treenode filter matches at least one test. A filter that matched
#                       nothing exits 8 and would otherwise read as "nothing to do".
#   4. Suites ran     — the run reported a non-zero total, and any skip names a daemon mode or image store
#                       this box is not. A "no usable daemon" failure from the suites themselves becomes an
#                       INFRASTRUCTURE abort (exit 5) rather than a product failure: nothing was judged.
#   5. Suites passed  — every executed test passed.
#
# Usage:
#   scripts/run-docker-smoke-local.sh [options]
#
# Options:
#   --help            Show this message.
#
# Env knobs:
#   DOCKER_HOST                honoured by the product's own endpoint resolver, so it selects the
#                              daemon under test exactly as it would in production.
#   XE_DOCKER_SMOKE_TIMEOUT    wall-clock budget for the test run (default 30m).
#   NO_BUILD_LOCK              do NOT take the cross-process build lock (escape hatch).
#   NO_GUARD                   skip the contamination snapshot/verify.
#
# Exit codes:
#   0   — every step ran AND passed
#   1   — the run was JUDGED and did not earn a pass: a test failed, or a step produced no verdict
#         (including "zero tests ran" and "a suite skipped itself"). Always accompanied by a
#         `=== Summary ===` block naming each step's verdict.
#   2   — usage error: an option this script does not take. Nothing was run.
#   5   — INFRASTRUCTURE abort: dotnet is missing, or the suites reported that the endpoint THEY resolved is
#         not usable. Nothing was judged, so this is deliberately NOT 1 — a box without a usable Docker has
#         not failed the product.
#   75  — CONTAMINATED: the test assemblies changed mid-run; the result is void, re-run it
#   130 — interrupted (Ctrl-C)

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
if ! PROJECT_ROOT="$(git -C "${SCRIPT_DIR}" rev-parse --show-toplevel 2>/dev/null)"; then
  PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd -P)"
fi

# Resolved before the cd below, because the re-exec hands this path to another program: invoked as
# `scripts/run-docker-smoke-local.sh` from somewhere else, a relative BASH_SOURCE would stop resolving the moment
# the working directory moves.
SELF_NAME="$(basename "${BASH_SOURCE[0]}")"
readonly SELF="${SCRIPT_DIR}/${SELF_NAME}"

# EVERY later step runs from the repository root, because several of them are cwd-relative and none of them says
# so. `assembly-guard.sh --test-bins` discovers the assemblies to snapshot by walking the CURRENT directory, so a
# runner invoked by absolute path from another checkout — or from /tmp — guarded a different tree's binaries, or
# none, and still reported "result is trustworthy". `dotnet` also reads global.json and nuget.config from the
# working directory. One cd fixes the class rather than each instance.
cd "${PROJECT_ROOT}" || {
  echo "[docker-smoke] INFRASTRUCTURE: cannot enter the repository root '${PROJECT_ROOT}'." >&2
  exit 5
}

readonly TEST_PROJECT="${PROJECT_ROOT}/XE-Local-AI-Engine.Tests/XE-Local-AI-Engine.Tests.csproj"

# The four suites whose subject is a real daemon. Kept here rather than discovered, because a
# filter that silently stopped matching a renamed class is the same hollow gate as a skip.
readonly REQUIRE_VARIABLE='XE_REQUIRE_DOCKER_TESTS'

readonly SUITE_FILTER='/*/*/(ContainerRuntimeRealDaemonTests|DockerSandboxRealDaemonTests|ExternalAppRealDaemonTests|ContainerBridgeRealDaemonTests)/*'

log()  { echo "[docker-smoke] $*"; }
infra_abort() { echo "[docker-smoke] INFRASTRUCTURE: $*" >&2; exit 5; }
usage() { sed -n '2,/^set -uo/p' "${SELF}" | sed 's/^# \{0,1\}//; $d'; }

trap 'echo; log "Interrupted."; exit 130' INT

# Step ledger — the anti-vacuous-pass mechanism, same shape as run-tool-grammar-smoke-local.sh.
LEDGER_EXPECTED=()
LEDGER_PASSED=()
LEDGER_FAILED=()
FAILURES=()

ledger_expect() { LEDGER_EXPECTED+=("$1"); }
ledger_pass()   { LEDGER_PASSED+=("$1"); log "PASS  $1"; }

step_fail() {
  local step="$1"; shift
  LEDGER_FAILED+=("${step}")
  FAILURES+=("${step}: $*")
  echo "[docker-smoke] FAIL  ${step}: $*" >&2
  return 1
}

ledger_contains() {
  local needle="$1"; shift
  local item
  for item in "$@"; do
    [[ "${item}" == "${needle}" ]] && return 0
  done
  return 1
}

ledger_finalize() {
  local status=0 step
  echo
  log "=== Summary ==="
  if [[ "${#LEDGER_EXPECTED[@]}" -eq 0 ]]; then
    echo "[docker-smoke] FAIL: no steps were expected — nothing ran. This is not a pass." >&2
    return 1
  fi
  for step in "${LEDGER_EXPECTED[@]}"; do
    if ledger_contains "${step}" ${LEDGER_PASSED[@]+"${LEDGER_PASSED[@]}"}; then
      echo "  PASS       ${step}"
    elif ledger_contains "${step}" ${LEDGER_FAILED[@]+"${LEDGER_FAILED[@]}"}; then
      echo "  FAILED     ${step}" >&2
      status=1
    else
      echo "  NO VERDICT ${step}  <- the step produced no result at all; treating as failure" >&2
      status=1
    fi
  done
  if [[ "${#FAILURES[@]}" -gt 0 ]]; then
    echo >&2
    echo "[docker-smoke] ${#FAILURES[@]} failure(s):" >&2
    for step in "${FAILURES[@]}"; do echo "  - ${step}" >&2; done
    status=1
  fi
  return "${status}"
}

# The build lock cannot be inherited through an exported variable (see with-build-lock.sh), so it
# is taken by re-executing this whole script under it. This MUST happen before the option loop
# below consumes "$@" — re-exec'ing afterwards silently drops every flag the operator passed.
# Answered before the re-exec below, so asking what this script does never waits on the build lock.
for argument in "$@"; do
  case "${argument}" in
    --help|-h) usage; exit 0 ;;
    *) ;;
  esac
done

if [[ -z "${XE_BUILD_LOCK_HELD:-}" && -z "${NO_BUILD_LOCK:-}" ]]; then
  exec "${PROJECT_ROOT}/scripts/with-build-lock.sh" -- "${SELF}" "$@"
fi

while [[ $# -gt 0 ]]; do
  case "$1" in
    --help|-h) usage; exit 0 ;;
    *) echo "[docker-smoke] Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

# Stale MSBuild worker nodes carry a deleted NUGET_PACKAGES across worktrees (NU5037 / CS0006 on a
# branch that is fine), so both are set for every dotnet invocation below.
export MSBUILDDISABLENODEREUSE=1
export NUGET_PACKAGES="${NUGET_PACKAGES:-${HOME}/.nuget/packages}"

# Step 1 — prerequisites. Only the toolchain: the DAEMON is not probed here.
#
# It used to run `docker info`, which asks whichever daemon the CLI's current context points at. The suites do
# not use the CLI: DockerDaemonEndpointResolver ignores CLI contexts entirely, and the test gates additionally
# fall back to the rootless user socket that production deliberately refuses. So a CLI probe could abort on a box
# the suites would have run on, or wave through a daemon they never touch — and reimplementing the resolver's
# rule in bash would put a second copy of that logic exactly where it can drift.
#
# The suites already resolve correctly and already refuse to skip: XE_REQUIRE_DOCKER_TESTS=1 turns "no usable
# daemon" into a named failure carrying the REQUIRED marker. Step 4 reads that marker and turns it back into this
# script's infrastructure abort, so there is one daemon-selection implementation and it is the product's.
ledger_expect "1-prerequisites"

command -v dotnet >/dev/null 2>&1 || infra_abort "dotnet is required to build and run the suites."

log "toolchain present; the daemon is selected and judged by the suites themselves"
ledger_pass "1-prerequisites"

# Step 2 — Release build. Debug skips analyzer execution, so a green Debug build is not
# verification (docs/agent-knowledge.md §1).
ledger_expect "2-build"
log "=== Building XE-Local-AI-Engine.Tests (Release) ==="
if dotnet build "${TEST_PROJECT}" --configuration Release; then
  ledger_pass "2-build"
else
  step_fail "2-build" "the Tests project did not build in Release."
  ledger_finalize
  exit 1
fi

# Step 3 — discovery. `--list-tests` is authoritative: a filter that matches nothing exits 8, and
# without this check that would reach the summary as "nothing to do" rather than as a broken gate.
ledger_expect "3-discovery"
LIST_FILE="$(mktemp -t xe-docker-smoke-list-XXXXXX.log)"
OUT_FILE="$(mktemp -t xe-docker-smoke-out-XXXXXX.log)"
trap 'rm -f "${LIST_FILE}" "${OUT_FILE}"' EXIT

log "=== Discovering the real-daemon suites ==="
# Two signals, both required. The exit code is authoritative — a treenode filter that matches nothing
# exits 8 — and the discovered count is what catches a listing that exited 0 while enumerating nothing.
dotnet test "${TEST_PROJECT}" --configuration Release --no-build --list-tests \
  --treenode-filter "${SUITE_FILTER}" >"${LIST_FILE}" 2>&1
LIST_STATUS=$?

if [[ "${LIST_STATUS}" -ne 0 ]]; then
  # Captured before any negation: `if ! cmd; then … $?` reports the status of the NEGATION, which is always 0.
  # And printed here rather than pointed at, because the EXIT trap deletes the file before anyone can read it.
  step_fail "3-discovery" "\`dotnet test --list-tests\` exited ${LIST_STATUS} for this filter; exit 8 means it
  matched nothing. This is not a pass."
  echo "[docker-smoke] --- last 20 lines of the discovery output ---" >&2
  tail -20 "${LIST_FILE}" >&2
  ledger_finalize
  exit 1
fi

# MTP prints "Discovered N tests in assembly - <path>" and then the bare method names, so the count comes
# from that line rather than from matching class names, which the listing never prints.
DISCOVERED="$(sed -n 's/^Discovered \([0-9][0-9]*\) tests\? in assembly.*/\1/p' "${LIST_FILE}" | head -1)"
if [[ -z "${DISCOVERED}" || "${DISCOVERED}" -eq 0 ]]; then
  step_fail "3-discovery" "the listing reported no discovered tests. Either the four suites were renamed or
  ${SUITE_FILTER} is wrong. This is not a pass."
  echo "[docker-smoke] --- last 20 lines of the discovery output ---" >&2
  tail -20 "${LIST_FILE}" >&2
  ledger_finalize
  exit 1
fi
log "discovered ${DISCOVERED} test(s) across the four real-daemon suites"
ledger_pass "3-discovery"

# Which skips this run may contain — a SET of recognised reasons, not one phrase.
#
# With XE_REQUIRE_DOCKER_TESTS=1 a daemon-unavailable skip is already a failure inside the tests, so nothing
# can reach here by being unable to find Docker. What can reach here is an assertion that is only meaningful
# against a daemon mode or an image store this box is not, and there are two shapes of that:
#
#   * A PAIR, where the other half runs instead — the rootless and rootful identity-mapping tests. Exactly one
#     applies to any daemon and the inapplicable half names its counterpart.
#   * A LONE assertion with no counterpart at all — the inverted-identity refusal, which is only reachable on a
#     rootless daemon. On a box where it does not apply the assertion is simply never made, and nothing else
#     makes it. (The volume-declaring fixture used to belong here too. It no longer skips: it falls back to the
#     bare image id, which the runtime's guard accepts, so the assertion is makeable on every image store.)
#
# Refusing either would fail this runner on a perfectly good box; accepting any skip at all would reopen the
# hollow-gate hole it exists to close. So the rule is: every skipped test must carry one of the two phrases
# below, and the number that do must equal the number that skipped. A skip for any other reason fails the run.
readonly RECOGNISED_SKIP_REASONS=(
  'counterpart in this class covers that host'
  'no counterpart exists for this host'
)

skips_are_all_recognised() {
  local skipped recognised=0 reason

  skipped="$(sed -n 's/^[[:space:]]*skipped:[[:space:]]*\([0-9][0-9]*\).*/\1/p' "${OUT_FILE}" | tail -1)"
  if [[ -z "${skipped}" ]]; then
    # No summary line was parsed at all. Distinct from "a suite skipped itself", and the caller says so.
    return 2
  fi

  for reason in "${RECOGNISED_SKIP_REASONS[@]}"; do
    recognised=$((recognised + $(grep -cF "${reason}" "${OUT_FILE}" || true)))
  done

  [[ "${recognised}" -eq "${skipped}" ]]
}

# Steps 4 and 5 — run them. XE_REQUIRE_DOCKER_TESTS=1 is what makes this a gate rather than a
# suggestion: inside the suites it turns every "no usable daemon" skip into a failure.
ledger_expect "4-suites-ran"
ledger_expect "5-suites-passed"

if [[ -z "${NO_GUARD:-}" ]]; then
  RUNNER=("${PROJECT_ROOT}/scripts/assembly-guard.sh" guard --test-bins --)
else
  RUNNER=()
fi

log "=== Running the real-daemon suites ==="
env "${REQUIRE_VARIABLE}=1" \
  ${RUNNER[@]+"${RUNNER[@]}"} timeout --signal=TERM --kill-after=60s "${XE_DOCKER_SMOKE_TIMEOUT:-30m}" \
  dotnet test "${TEST_PROJECT}" --configuration Release --no-build --max-parallel-test-modules 1 \
    --treenode-filter "${SUITE_FILTER}" 2>&1 | tee "${OUT_FILE}"
TEST_STATUS="${PIPESTATUS[0]}"

# Contamination is diagnosed FIRST: a run whose assemblies were rewritten underneath it can fail in
# any shape at all, including a vacuous zero-test summary, and every explanation below would be wrong.
if [[ "${TEST_STATUS}" -eq 75 ]]; then
  log "CONTAMINATED: the test assemblies changed mid-run. This result is VOID, not red — re-run it."
  exit 75
fi

# Was EVERY failure an infrastructure one, or only some of them?
#
# One marker anywhere used to mean exit 5 for the whole run, so a box where one suite could not reach its
# prerequisite AND another suite failed a real assertion reported "infrastructure" and the product failure went
# unmentioned. So the failures are classified one at a time: MTP prints a `failed <name>` line per failed test
# and then that test's message, so a failure counts as infrastructure only when ITS OWN block carries the marker.
#
# Exit 5 needs unanimity AND agreement: every failure marked, and the per-test count equal to the summary's own
# `failed:` figure. Any disagreement means this parsing has drifted from MTP's output, and the safe direction is
# to report a product failure — the whole point of this classification is that hiding one is the bad outcome.
classify_failures() {
  awk -v marker="REQUIRED — ${REQUIRE_VARIABLE}=1" '
    /^[[:space:]]*failed / { failed++; inblock = 1; counted = 0; next }
    /^[[:space:]]*(passed|skipped) / { inblock = 0; next }
    inblock && index($0, marker) && !counted { infrastructure++; counted = 1 }
    END { printf "%d %d\n", failed + 0, infrastructure + 0 }
  ' "${OUT_FILE}"
}

SUMMARY_FAILED="$(sed -n 's/^[[:space:]]*failed:[[:space:]]*\([0-9][0-9]*\).*/\1/p' "${OUT_FILE}" | tail -1)"
read -r COUNTED_FAILED COUNTED_INFRASTRUCTURE <<<"$(classify_failures)"

if [[ "${SUMMARY_FAILED:-0}" -gt 0 ]]; then
  log "failures: ${SUMMARY_FAILED} reported, ${COUNTED_FAILED} classified, ${COUNTED_INFRASTRUCTURE} of them infrastructure"

  if [[ "${COUNTED_FAILED}" -eq "${SUMMARY_FAILED}" && "${COUNTED_INFRASTRUCTURE}" -eq "${COUNTED_FAILED}" ]]; then
    echo "[docker-smoke] --- the suites own reason ---" >&2
    grep -F "REQUIRED — ${REQUIRE_VARIABLE}=1" "${OUT_FILE}" | head -1 >&2
    infra_abort "every failure was the suites reporting that the daemon endpoint THEY resolved is not usable. The
  reason above is theirs, and it names the endpoint and how it was found; this script does not second-guess it by
  probing a different daemon."
  fi

  if [[ "${COUNTED_INFRASTRUCTURE}" -gt 0 ]]; then
    log "NOT an infrastructure abort: ${COUNTED_INFRASTRUCTURE} of ${SUMMARY_FAILED} failure(s) carry the"
    log "prerequisite marker and the rest do not, so at least one is the product. Exiting 1, not 5."
  fi
fi

if grep -qiE 'total:[[:space:]]*0([^0-9]|$)|zero tests ran' "${OUT_FILE}"; then
  step_fail "4-suites-ran" "the runner executed ZERO tests. This is not a pass."
elif ! grep -qiE 'total:[[:space:]]*[1-9]' "${OUT_FILE}"; then
  step_fail "4-suites-ran" "no MTP test summary was found — the module did not run as a test app. This is not a pass."
else
  skips_are_all_recognised
  case "$?" in
    0) ledger_pass "4-suites-ran" ;;
    2) step_fail "4-suites-ran" "no skip count could be read out of the run summary, so this runner cannot tell a
  clean run from one that skipped everything. Read the output above." ;;
    *) step_fail "4-suites-ran" "a suite SKIPPED itself for a reason this runner does not recognise, even under
  XE_REQUIRE_DOCKER_TESTS=1. Recognised reasons name a daemon mode or an image store this box is not; read the
  skip above. An opt-in run that skipped what it was asked to prove is not a pass." ;;
  esac
fi

if [[ "${TEST_STATUS}" -eq 0 ]]; then
  ledger_pass "5-suites-passed"
else
  step_fail "5-suites-passed" "the test run exited ${TEST_STATUS}. Read the assertion messages above."
fi

grep -E 'total:|failed:|succeeded:|skipped:' "${OUT_FILE}" | tail -5 || true

if ledger_finalize; then
  echo
  log "Real-daemon Docker smoke PASSED."
  exit 0
fi
exit 1
