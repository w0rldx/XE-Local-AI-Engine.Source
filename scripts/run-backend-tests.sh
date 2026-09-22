#!/usr/bin/env bash
# run-backend-tests.sh — THE local backend gate: one build, then every enrolled test project.
#
# What it replaces
#   The documented gate used to be a solution-wide `dotnet test XE-Local-AI-Engine.slnx
#   --max-parallel-test-modules 1`, which serialized the three test modules because MTP's default
#   module width is Environment.ProcessorCount and XE-Local-AI-Engine.Client.Persistence.Tests at
#   TUnit's default in-process width peaked at 11.6 GB RSS. Serializing three modules to survive one
#   unpinned module is the wrong trade: this script pins a per-project width instead and then runs
#   the modules CONCURRENTLY — the shape .github/workflows/build-and-test.yml already used for its
#   `siblings` leg, which now calls this script rather than carrying a second copy of it.
#
# The two lanes
#   * XE-Local-AI-Engine.Tests goes through scripts/run-tests-memory-safe.sh (namespace batches,
#     JOBS processes, PAR tests per process). It is the tool of record for that module on wall clock
#     and memory; `dotnet test` over it in one process is neither.
#   * Every OTHER test project enrolled from XE-Local-AI-Engine.slnx runs as a plain `dotnet test`
#     --no-build at its pinned width, each with its OWN --results-directory (MTP resolves
#     --coverage-output relative to it, so projects sharing one directory overwrite each other's
#     Cobertura report — that collision was the only reason the projects were ever serial).
#   Both lanes run at the same time; every PID is waited on individually.
#
# Locking — ONE lock for the whole gate, taken here
#   scripts/with-build-lock.sh cannot subdivide a critical section (see its "Re-entrancy" note), and
#   run-tests-memory-safe.sh takes the lock for its ENTIRE run, not just its build. So the siblings
#   could not take it too — they would block behind the runner until it finished and the two lanes
#   would be serial again. This script therefore re-execs ITSELF under the wrapper, exactly once, and
#   everything below runs inside that one lock: the runner sees XE_BUILD_LOCK_HELD and passes
#   through, and the sibling `dotnet test` processes are deliberately unwrapped. The lock's purpose
#   is unchanged — no OTHER shell builds or tests while this gate runs — and the parallelism inside
#   the lock is this script's own, which is the one thing the lock was never meant to police.
#
# Contamination guard
#   Each sibling runs under `scripts/assembly-guard.sh guard --test-bins`, which reports exit 75
#   (CONTAMINATED, result void, re-run) when a foreign build rewrites the assemblies mid-run. The
#   runner self-guards its own output tree, which is why it is invoked with NO_BUILD=1 and is NOT
#   wrapped in an outer guard (its own build would otherwise fall inside that window and trip 75
#   every time).
#   EXCEPTION — coverage mode: Microsoft.Testing.Extensions.CodeCoverage instruments STATICALLY on
#   Linux, rewriting the assemblies in the project's own output tree and restoring them at exit, so
#   the guard would report every coverage run as contaminated. With COVERAGE_DIR set the siblings
#   therefore run unguarded, which is also what CI did before this script existed (CI gives each leg
#   a dedicated runner, so there is no foreign build to catch). The consequence is real and local: in
#   a sibling coverage run an unwrapped concurrent build is NOT detected, so such a run has weaker
#   contamination detection than the plain gate. The build lock still covers every cooperating shell,
#   and the batched module's lane keeps its own guard.
#
# Cancellation
#   In a terminal, Ctrl-C just works: the signal goes to the whole foreground process group, which is
#   the lock wrapper, this script and everything they have not put in a group of their own.
#   Non-interactively, signal this script's PROCESS GROUP, not its PID:
#
#     kill -TERM -- -"$(ps -o pgid= -p <pid> | tr -d ' ')"
#
#   A TERM to the PID alone hits scripts/with-build-lock.sh, which has no trap: it dies, releases the
#   lock, and leaves this script and its lanes running unsupervised. The wrapper is deliberately left
#   that way — it runs its command in the FOREGROUND, and six other scripts depend on that shape;
#   making it asynchronous so it could forward signals would silently set SIGINT and SIGQUIT to
#   ignored in every command it wraps, which is the POSIX rule for asynchronous commands.
#
#   However the signal arrives, the handler here terminates each lane's process GROUP, waits out a
#   bounded grace period, escalates survivors to KILL and reaps them before this script exits. The
#   lock itself belongs to the wrapper, so it is released when the wrapper exits, which in a
#   group-wide signal is not ordered after the reap. That is accepted: a cancelled run has no verdict
#   to protect, and a dying test host only READS the assemblies a new lock holder might rewrite —
#   except under COVERAGE_DIR, where a killed host never restores the ones its instrumentation
#   rewrote, so a cancelled coverage run leaves that project's output tree to the next build.
#
# Usage:
#   scripts/run-backend-tests.sh                     # build Release, then both lanes
#   XE_TEST_PROFILE=low-memory scripts/run-backend-tests.sh # serialize test lanes; width 1 unless overridden
#   NO_BUILD=1 scripts/run-backend-tests.sh          # skip the build (bin must be current)
#   scripts/run-backend-tests.sh --siblings-only     # skip the batched module (the CI `siblings` leg)
#   COVERAGE_DIR=/tmp/cov scripts/run-backend-tests.sh   # + Cobertura/TRX per project, unguarded
#
# Env knobs:
#   XE_TEST_PROFILE   unset: measured parallel defaults below; low-memory: default JOBS=1, PAR=1
#                     and sibling width 1, and run every project lane sequentially. Explicit JOBS,
#                     PAR and XE_TEST_WIDTH_* values still win.
#   NO_BUILD          skip the Release build
#   NO_BUILD_LOCK     do NOT take the cross-process build lock (escape hatch; detection only)
#   COVERAGE_DIR      per-project Cobertura + TRX land under <COVERAGE_DIR>/<module>/, and the batched
#                     module gets <COVERAGE_DIR>/XE-Local-AI-Engine.Tests/<unit>/ from the runner. A
#                     relative path is resolved against the directory you ran the script from.
#                     Unset: no coverage anywhere and TRX for the SIBLINGS ONLY — the batched runner
#                     is given no reporting arguments without COVERAGE_DIR — under
#                     .tmp/backend-test-results/, which this script clears first. Each lane's full
#                     console output is kept beside its reports as gate.log either way, and printed
#                     only when that lane fails.
#   JOBS, PAR         passed through to scripts/run-tests-memory-safe.sh
#   XE_GATE_CANCEL_GRACE  seconds a lane may take to wind down after TERM before it is killed
#                     outright (default 15; a whole number, 0 to skip straight to KILL)
#   XE_TEST_WIDTH_DEFAULT             --maximum-parallel-tests for a sibling (default 8)
#   XE_TEST_WIDTH_<Project>           per-project override; <Project> is the project name without the
#                                     XE-Local-AI-Engine. prefix, with . and - replaced by _
#                                     (XE_TEST_WIDTH_Client_Persistence_Tests, XE_TEST_WIDTH_AI_Agent_Tests).
#                                     Client.Persistence.Tests defaults to 4: measured fastest on a
#                                     32-core box (102 s / 2.7 GB) against a much larger default width.
#
# Exit codes:
#   0    — every project green
#   1    — one or more projects failed, or produced no MTP run summary (hollow gate)
#   2    — usage error / nothing enrolled
#   69   — could not acquire the build lock (from with-build-lock.sh); nothing was run
#   75   — CONTAMINATED: assemblies changed under a run; the result is void, re-run it. Never a
#          red and never a green.
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Resolved BEFORE the cd below, because $BASH_SOURCE is whatever spelling the caller used: after the
# cd, re-exec'ing a relative one would look for it under the repo root and die there. Every relative
# invocation — `./run-backend-tests.sh` from scripts/, or any path from another directory — depends
# on this line.
SELF="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"

usage() { sed -n '2,/^set -uo/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//; $d'; }

SIBLINGS_ONLY=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --siblings-only) SIBLINGS_ONLY=1; shift ;;
    --help|-h)       usage; exit 0 ;;
    *) echo "ERROR: unknown argument '$1' (see --help)." >&2; exit 2 ;;
  esac
done

# Resolve a caller-relative COVERAGE_DIR while the caller's directory is still current, then work
# from THIS checkout: `dotnet test` is handed a solution-relative project path and assembly-guard
# derives its roots from the current directory, so running this script from another checkout would
# otherwise build here and test there — and report that mixture as green.
if [[ -n "${COVERAGE_DIR:-}" && "$COVERAGE_DIR" != /* ]]; then
  COVERAGE_DIR="$PWD/$COVERAGE_DIR"
  export COVERAGE_DIR
fi
cd "$REPO" || { echo "ERROR: cannot enter the repository root $REPO." >&2; exit 2; }

LOW_MEMORY=""
case "${XE_TEST_PROFILE:-}" in
  "") ;;
  low-memory)
    LOW_MEMORY=1
    export JOBS="${JOBS:-1}"
    export PAR="${PAR:-1}"
    export XE_TEST_WIDTH_DEFAULT="${XE_TEST_WIDTH_DEFAULT:-1}"
    ;;
  *) echo "ERROR: XE_TEST_PROFILE must be 'low-memory' or unset, got '${XE_TEST_PROFILE}'." >&2; exit 2 ;;
esac

# Take the lock once, for the whole gate — see "Locking" above. Re-exec rather than lock inline so
# the wrapper can close the lock fd in this child: MSBuild's node-reuse daemons would otherwise
# inherit it and hold it idle for ~15 minutes (with-build-lock.sh, "THE FD-INHERITANCE TRAP").
# The guard is OUR OWN marker, not XE_BUILD_LOCK_HELD: that variable names one specific lock file,
# and deciding whether it is the lock we want is the wrapper's job — it exec's straight through for
# the same file and acquires properly for a different one. Reading it here would skip a lock the
# wrapper would have taken, and a marker inherited from a dead holder would skip locking entirely.
if [[ -z "${XE_BACKEND_GATE_LOCKED:-}" && -z "${NO_BUILD_LOCK:-}" ]]; then
  export XE_BACKEND_GATE_LOCKED=1
  exec "$REPO/scripts/with-build-lock.sh" -- "$SELF" ${SIBLINGS_ONLY:+--siblings-only}
fi

SLN="$REPO/XE-Local-AI-Engine.slnx"
BATCHED_MODULE="XE-Local-AI-Engine.Tests"
RESULTS_ROOT="${COVERAGE_DIR:-$REPO/.tmp/backend-test-results}"

# Stale MSBuild worker nodes carry a deleted NUGET_PACKAGES across worktrees (NU5037 / CS0006 on a
# branch that is fine) — AGENTS.md "Validation". Exported for the build AND for every `dotnet test`,
# which still evaluates the project.
export MSBUILDDISABLENODEREUSE=1
export NUGET_PACKAGES="${NUGET_PACKAGES:-$HOME/.nuget/packages}"

# Auto-enrolment from the solution, same rule as the workflow: every *.Tests[.X] project except the
# E2E suite (Playwright browsers + an SPA build; it has its own opt-in runner).
mapfile -t TEST_PROJECTS < <(
  grep -oE 'Project Path="[^"]*"' "$SLN" \
    | sed -E 's/Project Path="([^"]*)"/\1/' \
    | grep -E '\.Tests(\.[A-Za-z0-9]+)*\.csproj$' \
    | grep -v 'E2ETests' \
    | sort)
if [[ "${#TEST_PROJECTS[@]}" -eq 0 ]]; then
  echo "ERROR: zero test projects discovered in $SLN." >&2
  exit 2
fi

declare -A MODULE_PROJECTS=()
for project in "${TEST_PROJECTS[@]}"; do
  MODULE_PROJECTS["$(basename "$project" .csproj)"]="$project"
done
# The batched module is this gate's first lane, so its disappearance from the solution must be loud:
# otherwise it would silently become one more plain `dotnet test` in the sibling loop.
if [[ -z "${MODULE_PROJECTS[$BATCHED_MODULE]:-}" ]]; then
  echo "ERROR: $BATCHED_MODULE is not enrolled from $SLN." >&2
  exit 2
fi
unset 'MODULE_PROJECTS[$BATCHED_MODULE]'
if [[ "${#MODULE_PROJECTS[@]}" -eq 0 ]]; then
  echo "ERROR: no sibling test projects left after excluding $BATCHED_MODULE." >&2
  exit 2
fi
printf 'Enrolled test project: %s\n' "${TEST_PROJECTS[@]}"

# Per-project in-process width. The default is deliberately NOT TUnit's (which is thread-pool driven
# and unbounded): Client.Persistence.Tests at that width measured 6:08 wall / 11.6 GB RSS, against
# 102 s / 2.7 GB at width 4.
width_for() {
  local module="$1" key var value
  key="${module#XE-Local-AI-Engine.}"; key="${key//[.-]/_}"
  var="XE_TEST_WIDTH_${key}"
  value="${!var:-}"
  if [[ -z "$value" ]]; then
    case "$module" in
      *.Client.Persistence.Tests) value="${XE_TEST_WIDTH_DEFAULT:-4}" ;;
      *)                          value="${XE_TEST_WIDTH_DEFAULT:-8}" ;;
    esac
  fi
  if [[ ! "$value" =~ ^[1-9][0-9]*$ ]]; then
    echo "ERROR: width for $module must be a positive integer, got '$value' (from \$$var or \$XE_TEST_WIDTH_DEFAULT)." >&2
    return 2
  fi
  printf '%s' "$value"
}

if [[ -z "${NO_BUILD:-}" ]]; then
  echo ">> Building the solution (Release)…"
  dotnet build-server shutdown >/dev/null 2>&1 || true
  if ! dotnet build "$SLN" --configuration Release; then
    echo "BUILD FAILED — nothing was run." >&2
    exit 1
  fi
fi

# Own directory, so clearing it cannot touch a caller's coverage tree.
[[ -n "${COVERAGE_DIR:-}" ]] || rm -rf "$RESULTS_ROOT"
mkdir -p "$RESULTS_ROOT"

# Lanes are background processes, so results travel through files, not shell globals.
LANE_DIR="$(mktemp -d)"
trap 'rm -rf "$LANE_DIR"' EXIT
declare -A LANE_PIDS=()
LANES=()
# Non-zero once a signal has asked for cancellation; 1 while a lane launch is mid-flight.
CANCEL_STATUS=0
LAUNCHING=0
# How long a lane may take to wind down after TERM before it is killed outright. Validated HERE,
# not where it is used: a non-integer would otherwise abort the shell inside the arithmetic in
# cancel_lanes — after the TERM but before the KILL and the reap, which is the one moment this
# script must not die in.
CANCEL_GRACE_SECONDS="${XE_GATE_CANCEL_GRACE:-15}"
if [[ ! "$CANCEL_GRACE_SECONDS" =~ ^(0|[1-9][0-9]*)$ ]]; then
  echo "ERROR: XE_GATE_CANCEL_GRACE must be a whole number of seconds, got '$CANCEL_GRACE_SECONDS'." >&2
  exit 2
fi

# Cancellation, by PID and PGID only — a pattern kill crosses worktree boundaries and takes out
# another checkout's run (AGENTS.md "Local runtime"). Each lane is its own process GROUP (see the
# `set -m` at the launch loop), so one signal reaches the lane subshell, the assembly guard, the test
# host it spawned and anything the host spawned in turn, including processes created after the
# signal was decided on — which a walk over a PID tree cannot promise.
cancel_lanes() {
  local pid deadline
  (( ${#LANE_PIDS[@]} )) || return 0
  # Group first, lane PID as the floor: if job control were ever lost the group would not exist, and
  # a cancellation that signalled nothing would hang this function in the reap below instead of
  # ending the run.
  for pid in "${LANE_PIDS[@]}"; do
    kill -TERM -- "-$pid" 2>/dev/null || kill -TERM "$pid" 2>/dev/null
  done
  # Bounded grace: a test host that flushes reports on TERM deserves the chance, a host that ignores
  # it does not get to outlive the gate.
  deadline=$((EPOCHSECONDS + CANCEL_GRACE_SECONDS))
  for pid in "${LANE_PIDS[@]}"; do
    while kill -0 "$pid" 2>/dev/null && (( EPOCHSECONDS < deadline )); do sleep 0.5; done
  done
  for pid in "${LANE_PIDS[@]}"; do
    kill -KILL -- "-$pid" 2>/dev/null || kill -KILL "$pid" 2>/dev/null
  done
  # Reap before returning: nothing this script reports may be written by a process it no longer
  # waits for, and the result files read below are only complete once the lanes are gone.
  for pid in "${LANE_PIDS[@]}"; do wait "$pid" 2>/dev/null; done
}

# Cancellation is requested by a signal and COMPLETED here, so that a lane started microseconds
# before the signal is still cancelled: see LAUNCHING below.
finish_cancel() {
  local module rc status="$CANCEL_STATUS"
  cancel_lanes
  # A lane that had already finished CONTAMINATED outranks the cancellation status. The assemblies
  # moved under that lane, and "re-run me" is the only honest verdict — losing it behind a 130 would
  # let the next run be judged against a tree that had changed.
  for module in "${LANES[@]}"; do
    [[ -f "$LANE_DIR/$module.result" ]] || continue
    read -r _ _ rc _ <"$LANE_DIR/$module.result"
    [[ "$rc" == 75 ]] && status=75
  done
  rm -rf "$LANE_DIR"
  exit "$status"
}

# A signal that lands between `lane &` and the `$!` that records its PID would otherwise leave that
# lane unregistered — and so uncancelled, running on with nothing waiting for it. The handler
# therefore only RECORDS the request while a launch is in flight; the launch loop completes the
# registration and then cancels.
request_cancel() {
  CANCEL_STATUS="$1"
  (( LAUNCHING )) && return 0
  finish_cancel
}
# Every variable these handlers read — CANCEL_STATUS, LAUNCHING, LANES, LANE_PIDS, LANE_DIR,
# CANCEL_GRACE_SECONDS — is initialised above, before the first trap can fire. Under `set -u` an
# uninitialised one would turn any cancellation into an "unbound variable" error instead.
trap 'request_cancel 130' INT
trap 'request_cancel 143' TERM HUP

# "<pass> <fail> <exit> <seconds>" per lane. exit 75 = contaminated, 90 = no MTP run summary.
run_sibling() {
  local module="$1" project="$2" width="$3"
  local log="$RESULTS_ROOT/$module/gate.log" t0=$EPOCHSECONDS
  local -a report_args=(--report-trx --results-directory "$RESULTS_ROOT/$module")
  local -a guard=("$REPO/scripts/assembly-guard.sh" guard --test-bins --)
  if [[ -n "${COVERAGE_DIR:-}" ]]; then
    report_args=(--coverage --coverage-output coverage.cobertura.xml --coverage-output-format cobertura
                 "${report_args[@]}")
    guard=()  # static instrumentation rewrites this project's own bin — see the header.
  fi
  mkdir -p "$RESULTS_ROOT/$module"
  # env -u: the lock marker is for the nested memory-safe runner, which must not re-take the lock we
  # already hold. A leaf `dotnet test` has no such re-entry to suppress, and leaving the marker in
  # its environment would let anything it spawns believe it holds a lock that it does not.
  env -u XE_BUILD_LOCK_HELD "${guard[@]}" dotnet test "$project" --configuration Release --no-build \
    --maximum-parallel-tests "$width" "${report_args[@]}" >"$log" 2>&1
  local rc=$? p f
  p="$(grep -oE 'succeeded: *[0-9]+' "$log" | grep -oE '[0-9]+' | tail -1)"; p="${p:-0}"
  f="$(grep -oE 'failed: *[0-9]+' "$log" | grep -oE '[0-9]+' | tail -1)"; f="${f:-0}"
  # Hollow-gate guard: MTP always prints a "Passed!"/"Failed!" run summary for a suite that actually
  # ran. A suite that enrolled nothing prints none and still exits 0 — never count that as green.
  if [[ "$rc" != 75 ]] && ! grep -qE 'Passed!|Failed!' "$log"; then
    echo "ERROR: $module produced no test-suite summary — nothing ran." \
         "Check project names or IsTestingPlatformApplication." >&2
    rc=90
  fi
  echo "$p $f $rc $((EPOCHSECONDS-t0))" >"$LANE_DIR/$module.result"
}

run_batched_module() {
  local module="$BATCHED_MODULE" log="$RESULTS_ROOT/$BATCHED_MODULE/gate.log" t0=$EPOCHSECONDS
  mkdir -p "$RESULTS_ROOT/$module"
  # NO_BUILD: this script already built. The runner self-locks (pass-through here) and self-guards,
  # so it must not be wrapped in an outer guard.
  local -a lane_env=(NO_BUILD=1)
  [[ -n "${COVERAGE_DIR:-}" ]] && lane_env+=("COVERAGE_DIR=$COVERAGE_DIR/$module")
  env "${lane_env[@]}" "$REPO/scripts/run-tests-memory-safe.sh" >"$log" 2>&1
  local rc=$? p f
  p="$(grep -oE 'TOTAL: pass=[0-9]+' "$log" | grep -oE '[0-9]+' | tail -1)"; p="${p:-0}"
  f="$(grep -oE 'fail=[0-9]+' "$log" | grep -oE '[0-9]+' | tail -1)"; f="${f:-0}"
  echo "$p $f $rc $((EPOCHSECONDS-t0))" >"$LANE_DIR/$module.result"
}

# Widths are resolved BEFORE any lane starts: a bad value must fail the gate without first spending
# a test run on the other lane.
declare -A MODULE_WIDTHS=()
for module in "${!MODULE_PROJECTS[@]}"; do
  MODULE_WIDTHS["$module"]="$(width_for "$module")" || exit 2
done

# Job control ON for the launches: it puts every background lane in its own process group, which is
# what makes cancellation reach the whole lane instead of only its subshell. It is switched off again
# straight after — `wait` and the summary want the plain non-interactive behaviour.
#
# Every lane therefore takes `</dev/null` explicitly. Job control restores what bash otherwise
# removes — an async command's stdin is /dev/null only while job control is OFF — and a background
# process group that reads the controlling terminal is stopped with SIGTTIN. A stopped child never
# satisfies `wait`, so the gate would hang there, holding the build lock, until someone killed it by
# hand. Job control also un-ignores SIGINT and SIGQUIT for these lanes, which bash sets to ignored in
# an asynchronous command: without it a lane could not be interrupted at all.
set -m
if [[ -z "$SIBLINGS_ONLY" ]]; then
  echo ">> Lane: $BATCHED_MODULE through scripts/run-tests-memory-safe.sh"
  LANES+=("$BATCHED_MODULE")
  LAUNCHING=1
  run_batched_module </dev/null &
  LANE_PIDS["$BATCHED_MODULE"]=$!
  LAUNCHING=0
  (( CANCEL_STATUS )) && finish_cancel
  [[ -z "$LOW_MEMORY" ]] || wait "${LANE_PIDS[$BATCHED_MODULE]}"
fi
for module in "${!MODULE_PROJECTS[@]}"; do
  echo ">> Lane: $module at --maximum-parallel-tests ${MODULE_WIDTHS[$module]}"
  LANES+=("$module")
  LAUNCHING=1
  run_sibling "$module" "${MODULE_PROJECTS[$module]}" "${MODULE_WIDTHS[$module]}" </dev/null &
  LANE_PIDS["$module"]=$!
  LAUNCHING=0
  (( CANCEL_STATUS )) && finish_cancel
  [[ -z "$LOW_MEMORY" ]] || wait "${LANE_PIDS[$module]}"
done
set +m

for module in "${LANES[@]}"; do wait "${LANE_PIDS[$module]}"; done

CONTAMINATED=0
FAILED=()
echo "======================================================================"
printf '%-46s %7s %7s %8s %6s\n' "PROJECT" "PASS" "FAIL" "WALL" "EXIT"
for module in "${LANES[@]}"; do
  rfile="$LANE_DIR/$module.result"
  if [[ ! -f "$rfile" ]]; then
    printf '%-46s %7s %7s %8s %6s\n' "$module" "?" "?" "?" "no-result"
    FAILED+=("$module(no-result)")
    continue
  fi
  read -r p f rc dur <"$rfile"
  printf '%-46s %7s %7s %7ss %6s\n' "$module" "$p" "$f" "$dur" "$rc"
  case "$rc" in
    0)  ;;
    75) CONTAMINATED=1 ;;
    90) FAILED+=("$module(no-summary)") ;;
    *)  FAILED+=("$module(exit=$rc)") ;;
  esac
done
echo "----------------------------------------------------------------------"
echo "Reports: $RESULTS_ROOT"

if [[ "$CONTAMINATED" == 1 ]]; then
  echo "RESULT VOID: assemblies changed under a run (assembly-guard exit 75). Re-run the gate;" \
       "this is neither a pass nor a failure." >&2
  exit 75
fi
if [[ "${#FAILED[@]}" -gt 0 ]]; then
  # The whole log, not a tail: a lane's output is the only evidence of WHICH test failed, and it is
  # buffered rather than streamed because two lanes writing to one terminal interleave unreadably.
  for module in "${FAILED[@]}"; do
    echo "FAILED: $module" >&2
    lane_log="$RESULTS_ROOT/${module%%(*}/gate.log"
    [[ -f "$lane_log" ]] && sed "s/^/[${module%%(*}] /" "$lane_log" >&2
  done
  exit 1
fi
echo "BACKEND GATE GREEN"
