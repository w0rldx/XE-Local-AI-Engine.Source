#!/usr/bin/env bash
# Memory-safe runner for the XE-Local-AI-Engine.Tests module.
#
# WHY: WebApplicationFactory<Program> resolves this top-level-statement Program through
# HostFactoryResolver.HostingListener, which runs the entry point on a dedicated background thread that blocks in
# app.Run()/WaitForShutdownAsync. That thread's ExecutionContext holds an AsyncLocal -> HostingListener -> the built
# IHost, so EVERY host a test builds stays GC-rooted for the process lifetime even though TestingWebAppFactory is
# disposed (confirmed via gcroot; framework-level, .NET 10 / Mvc.Testing 10.0.9 — the product app builds exactly one
# host and is unaffected). Running the whole module in ONE process therefore accumulates ~11 MB per host-based test and
# balloons to ~3.5 GB, which thrashes a memory-tight box.
#
# This script runs the module in fresh-process batches, ONE per test namespace, normally single-threaded WITHIN the
# process. The exact local non-coverage DevWorkflows namespace is measured-safe at width 2; PAR=1 restores full
# serialization, and grouped/coverage runs stay at width 1 by default. A fresh process per namespace resets the leak
# between batches (bounding peak RSS) and — because namespaces
# are the natural test-tree partition — covers every test exactly once with no source-parsing guesswork.
# Single-threaded execution also removes the cross-test env-mutation races (XE_NODE_SQLITE_KEY set/unset) documented
# in docs/agent-knowledge.md §1. Those races are per-process, so JOBS batch processes run concurrently (see below).
#
# It is the low-risk MITIGATION, not a root-cause fix. CI (7-16 GB runners) can still run the module in one process.
# 2026-08-15 update: the leak IS fixed (TestServerWebAppFactory + shared per-class hosts, docs/agent-knowledge.md §1) and a
# one-process 8-wide run is green (5582/0) — but it measured 305 s wall / 3.9 GB peak RSS against 165 s / ~0.6 GB per
# batch here, so this runner stays the local full-run tool of record on wall time and memory, not on correctness.
#
# Build contamination
#   The batches run the test host against bin/ WITHOUT rebuilding, so a concurrent `dotnet build`
#   from any other shell rewrites the assemblies mid-run and produces phantom failures. This script
#   defends on both sides: it re-execs itself under the cross-process build lock
#   (scripts/with-build-lock.sh), and it snapshots the test output tree before the first batch and
#   re-checks it after the last, reporting exit 75 CONTAMINATED rather than a fail/pass verdict it
#   cannot stand behind. See docs/agent-knowledge.md §1.
#   It SELF-GUARDS, so do not wrap it in an outer `assembly-guard.sh guard --test-bins -- …`: the
#   build this script performs falls inside that outer window and trips exit 75 every time. If you
#   must wrap it, pass NO_BUILD=1.
#
# Coverage instrumentation is in-place — coverage mode needs one output tree PER JOB
#   Microsoft.Testing.Extensions.CodeCoverage uses STATIC instrumentation on Linux: it rewrites the
#   assemblies in the test output directory to add probes and restores them when the process exits.
#   A single process does that invisibly; JOBS processes over ONE directory race and corrupt each
#   other. Measured 2026-08-22 on this repo: 8 concurrent coverage batches over the shared bin/
#   produced 124 phantom failures and left 3 assemblies rewritten (assembly-guard exit 75); the SAME
#   8 batches without coverage were 0 failures with the guard clean.
#
#   Dynamic instrumentation would avoid the rewrite entirely and was tried first — it does NOT work
#   for this test host (measured 2026-08-23, package 18.10.0). A --coverage-settings file with
#   EnableDynamicManagedInstrumentation=True + EnableStaticManagedInstrumentation=False is ACCEPTED
#   and then collects nothing: the report is `<coverage line-rate="1"><packages /></coverage>`,
#   0 packages, versus 247018 valid lines for the identical batch under the static default. Adding
#   the CLR profiler env vars by hand (CORECLR_ENABLE_PROFILING=1, CORECLR_PROFILER=
#   {324F817A-7420-4E6D-B3C1-143FBED6D855}, CORECLR_PROFILER_PATH=<bin>/runtimes/linux-x64/native/
#   libInstrumentationEngine.so) still produced 0 packages. Do not re-try it without new evidence —
#   note that an empty report reads as line-rate="1", so the failure is silent at the report level
#   (merge-cobertura.py does catch it: zero source lines is a hard error there).
#
#   So COVERAGE_DIR gives each concurrent slot its own copy of the output tree, and the real bin/ is
#   never instrumented at all — which is also why the assembly guard stays meaningful in coverage
#   mode: it still watches the un-instrumented bin/ and would still catch a genuine concurrent
#   build. The copies sit under <proj>/bin/Release/cov-slots/<n>/ for two reasons: they must stay
#   INSIDE the repo (a Hosting test walks up from the test binary to find XE-Local-AI-Engine.Client/
#   Program.cs and fails from /tmp), and that path deliberately does not match assembly-guard's
#   --test-bins glob (*.Tests*/bin/*/net*), so a slot cannot be mistaken for contamination.
#   Cost: ~240 MB per job (JOBS copies), made with cp -a in well under a second on a warm cache.
#
# Batch-level parallelism (JOBS)
#   The normal per-process width of 1 is deliberate, but nothing requires the *processes* to run one
#   after another: every hazard the fresh-process design defends against is process-scoped (env-var
#   mutation, PATH stubs, meter/ActivityListener capture, the HostStartupLock) or already isolated
#   per host (GUID-named temp SQLite/data dirs, port-0 binds). So the batches run JOBS at a time,
#   longest-first. Only the exact ungrouped, non-coverage DevWorkflows unit uses width 2 when PAR is unset;
#   every other batch stays at width 1 by default. PROCESS-level parallelism is what this
#   module responds to; in-process width is contention-bound (measured 2026-08-22, 16-core host:
#   one process 8-wide = 11:00 wall / 10.0 GB, JOBS=4 batches = 6:02, JOBS=10 batches = 2:18 with
#   ~670 MB per batch process). Hence the default of 10 rather than one-per-core.
#   JOBS=1 PAR=1 reproduces the old fully serialized behavior exactly.
#
# Usage:
#   scripts/run-tests-memory-safe.sh                # build (Release) + run every namespace batch
#   NO_BUILD=1 scripts/run-tests-memory-safe.sh     # skip the build (bin must be current)
#   JOBS=1 PAR=1 scripts/run-tests-memory-safe.sh   # old behavior: batches and tests fully serialized
#   PAR=4 scripts/run-tests-memory-safe.sh          # allow N parallel tests per batch (faster, may reintroduce flakes)
#   COVERAGE_DIR=/tmp/cov scripts/run-tests-memory-safe.sh   # also emit per-batch Cobertura + TRX
#   TEST_GROUPS=16 TEST_SHARD=2/4 scripts/run-tests-memory-safe.sh   # run only groups 2, 6, 10, 14
#
# Env knobs:
#   JOBS            how many namespace batch PROCESSES run concurrently (default 10; 1 = sequential)
#   PAR             max parallel tests per batch (default 1 = deterministic + lowest RSS; >1 is faster but can flake).
#                   When unset, only the exact ungrouped, non-coverage DevWorkflows unit uses measured-safe width 2;
#                   set PAR=1 to disable that exception.
#   AVAIL_FLOOR     a batch aborts if available RAM drops below this many MB (default 800; with JOBS>1
#                   every batch that observes the breach kills itself — safety over completeness)
#   TEST_GROUPS     when set to N, pack the namespaces into N processes (LPT by measured weight)
#                   instead of one process per namespace, and filter each with a treenode
#                   alternation. This is the shape CI uses and the one coverage runs want — see
#                   the CPU-seconds table lower down. NOT named GROUPS: bash owns that name.
#                   In grouped mode the zero-enrolled guard is per GROUP, not per namespace, and
#                   COVERAGE_DIR reports land under <COVERAGE_DIR>/<group>/ rather than
#                   <COVERAGE_DIR>/<namespace>/. Either way COVERAGE_DIR/units.txt lists the units scheduled (written
#                   before they run; the per-unit report check + the workflow count cover the rest), and a unit that produced no usable report is reported FAILED.
#   TEST_SHARD      "i/N": run only the groups whose index g satisfies g % N == i (0 <= i < N).
#                   Requires TEST_GROUPS — the per-namespace shape has no group index to slice, and
#                   silently ignoring the knob there would ship a "shard" that ran the whole module.
#                   WHY it exists: CI gives each shard its OWN runner. One 4-vCPU runner covering
#                   the whole batched module grew past the job timeout (2026-09-06), so the workflow
#                   runs TEST_GROUPS=16 four times with TEST_SHARD=0/4 … 3/4. Striding by modulo
#                   rather than slicing a contiguous range spreads the LPT packer's heavy leading
#                   bins across the shards instead of piling them into shard 0. units.txt lists only
#                   this shard's units, so the workflow's one-report-per-unit check still holds per
#                   leg. Unset (the default) changes nothing for any other caller.
#   COVERAGE_DIR    when set, every batch additionally writes
#                   <COVERAGE_DIR>/<namespace>/coverage.cobertura.xml plus a TRX report. The reports
#                   are per-batch by construction (MTP resolves --coverage-output relative to
#                   --results-directory, so batches sharing one directory would overwrite each
#                   other); merge them with scripts/merge-cobertura.py, which unions by
#                   (filename, line) and so takes any number of reports.
#                   COSTS DISK: see "Coverage instrumentation is in-place" below — coverage mode
#                   clones the ~240 MB test output tree once per concurrent job.
#   NO_BUILD        when set, skip the Release build
#   NO_BUILD_LOCK   when set, do NOT take the cross-process build lock (escape hatch; you are then
#                   relying on the contamination DETECTION alone)
#   NO_GUARD        when set, skip the contamination snapshot/verify. Do not use this to make a
#                   contaminated run look green.
#
# Exit codes:
#   0   — every namespace batch green
#   1   — one or more batches had failures
#   69  — could not acquire the build lock (from scripts/with-build-lock.sh); nothing was run
#   75  — CONTAMINATED: the build output changed mid-run; the result is void, re-run it
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Serialize against any other build/test that goes through the wrapper. Re-exec rather than lock
# inline: the wrapper closes the lock fd in this child, so the MSBuild daemons the build below
# leaves behind cannot inherit the lock and starve everyone else (see with-build-lock.sh).
if [[ -z "${XE_BUILD_LOCK_HELD:-}" && -z "${NO_BUILD_LOCK:-}" ]]; then
  exec "$REPO/scripts/with-build-lock.sh" -- "${BASH_SOURCE[0]}" "$@"
fi
PROJ="$REPO/XE-Local-AI-Engine.Tests"
EXE="$PROJ/bin/Release/net10.0/XE-Local-AI-Engine.Tests"
PAR_EXPLICIT="${PAR:+1}"
PAR="${PAR:-1}"
JOBS="${JOBS:-10}"
AVAIL_FLOOR="${AVAIL_FLOOR:-800}"

# Parsed here rather than next to the packer so a malformed value fails before the Release build
# instead of after it. SHARD_COUNT stays empty when the knob is unset, and that emptiness is what
# every later shard check keys on.
SHARD_INDEX=""
SHARD_COUNT=""
if [[ -n "${TEST_SHARD:-}" ]]; then
  if [[ -z "${TEST_GROUPS:-}" ]]; then
    echo "ERROR: TEST_SHARD='${TEST_SHARD}' needs TEST_GROUPS — without groups there is nothing to shard." >&2
    exit 1
  fi
  if [[ ! "$TEST_SHARD" =~ ^(0|[1-9][0-9]*)/[1-9][0-9]*$ ]]; then
    echo "ERROR: TEST_SHARD must be 'i/N' (N >= 1, 0 <= i < N), got '$TEST_SHARD'." >&2
    exit 1
  fi
  SHARD_INDEX="${TEST_SHARD%%/*}"
  SHARD_COUNT="${TEST_SHARD##*/}"
  if (( SHARD_INDEX >= SHARD_COUNT )); then
    echo "ERROR: TEST_SHARD index must be below the shard count, got '$TEST_SHARD'." >&2
    exit 1
  fi
fi

# The module has a CONFLICTING env premise (docs/agent-knowledge.md §1): some tests require XE_NODE_SQLITE_KEY UNSET,
# one requires it SET. Never export it here — host tests inject it via in-memory config, and namespace batching keeps
# the conflicting classes (DesktopBootstrapTests in .Hosting vs the Playbook ones in .Agents) in separate processes.
unset XE_NODE_SQLITE_KEY

if [[ -z "${NO_BUILD:-}" ]]; then
  echo ">> Building $PROJ (Release)…"
  dotnet build "$PROJ/XE-Local-AI-Engine.Tests.csproj" -c Release -v q || { echo "BUILD FAILED"; exit 1; }
fi
[[ -x "$EXE" ]] || { echo "test host not found at $EXE (build first)"; exit 1; }

echo ">> Enumerating test namespaces…"
mapfile -t NAMESPACES < <(grep -rlE '\[Test' "$PROJ" 2>/dev/null | grep -v '/bin/\|/obj/' | grep '\.cs$' \
  | xargs grep -h '^namespace ' 2>/dev/null | sed 's/namespace //; s/;.*//' | sort -u)
echo "   ${#NAMESPACES[@]} namespaces; PAR=$PAR"

# Batches run as background subshells, so results go through files, not shell globals.
RESULTS_DIR="$(mktemp -d)"

# Slot pool for coverage mode (see "Coverage instrumentation is in-place" above). A slot is claimed
# with mkdir, which is atomic across the batch subshells; at most JOBS batches run at once, so a
# free slot always exists and the wait loop never actually spins.
SLOT_ROOT=""
SLOT_LOCKS=""
acquire_slot() {
  local i
  while :; do
    for ((i = 0; i < JOBS; i++)); do
      if mkdir "$SLOT_LOCKS/$i" 2>/dev/null; then printf '%s' "$i"; return; fi
    done
    sleep 0.2
  done
}
release_slot() { [[ -n "${1:-}" ]] && rmdir "$SLOT_LOCKS/$1" 2>/dev/null; return 0; }

run_ns() {
  local ns="$1" filter="$2" out; out="$(mktemp)"; local t0=$EPOCHSECONDS
  local unit_par="$PAR"
  [[ -z "$PAR_EXPLICIT" && -z "${COVERAGE_DIR:-}" \
    && "$ns" == "XE_Local_AI_Engine.Tests.DevWorkflows" ]] && unit_par=2
  # Coverage/TRX is opt-in: one results directory per batch, because MTP resolves --coverage-output
  # relative to --results-directory and batches sharing one would overwrite each other's report.
  local exe="$EXE" slot=""
  local -a report_args=()
  if [[ -n "${COVERAGE_DIR:-}" ]]; then
    report_args=(--coverage --coverage-output coverage.cobertura.xml --coverage-output-format cobertura
                 --report-trx --results-directory "$COVERAGE_DIR/$ns")
    if [[ -n "$SLOT_ROOT" ]]; then
      slot="$(acquire_slot)"; exe="$SLOT_ROOT/$slot/$(basename "$EXE")"
    fi
  fi
  TUNIT_DISABLE_HTML_REPORTER=1 "$exe" --treenode-filter "$filter" --maximum-parallel-tests "$unit_par" \
    "${report_args[@]}" >"$out" 2>&1 &
  local pid=$! peak=0
  while kill -0 "$pid" 2>/dev/null; do
    local rss avail
    rss="$(ps -o rss= -p "$pid" 2>/dev/null | tr -d ' ')"
    [[ -n "$rss" && "$rss" -gt "$peak" ]] && peak="$rss"
    avail="$(free -m | awk '/^Mem:/{print $7}')"
    if [[ "${avail:-9999}" -lt "$AVAIL_FLOOR" ]]; then
      echo "   !! SAFETY-KILL $ns: avail ${avail}MB < ${AVAIL_FLOOR}MB (rss ${rss}KB)"; kill -9 "$pid" 2>/dev/null
      echo "0 1 $peak oom $((EPOCHSECONDS-t0)) 0 0" >"$RESULTS_DIR/$ns.result"; rm -f "$out"
      release_slot "$slot"; return
    fi
    sleep 1
  done
  wait "$pid"; local rc=$?
  local p f
  p="$(grep -oE 'succeeded: *[0-9]+' "$out" | grep -oE '[0-9]+' | tail -1)"; p="${p:-0}"
  f="$(grep -oE 'failed: *[0-9]+' "$out" | grep -oE '[0-9]+' | tail -1)"; f="${f:-0}"
  # Hollow-gate guard, same semantics as the CI loop: MTP always prints a "Passed!"/"Failed!" run
  # summary for a batch that actually ran. A batch that enrolled nothing — or died before the
  # summary — prints none, and must never be counted as green just because pass=0 fail=0.
  # Two limits worth knowing: it fires per UNIT, so under TEST_GROUPS that is per group and not per
  # namespace; and an all-SKIPPED unit does print "Passed!" and reads green either way (the
  # compensating control there is XE_REQUIRE_DOCKER_TESTS=1).
  local sum=0; grep -qE 'Test run summary: (Passed|Failed)!' "$out" && sum=1
  printf "   %-52s pass=%-4s fail=%-3s peakRSS=%sMB exit=%s dur=%ss\n" "$ns" "$p" "$f" "$((peak/1024))" "$rc" "$((EPOCHSECONDS-t0))"
  if [[ "$f" -gt 0 ]]; then grep -E 'failed|error' "$out" | grep -viE 'failed: 0' | head -3 >"$RESULTS_DIR/$ns.fails"; fi
  rm -f "$out"
  # A unit whose collector wrote no report (or an empty one) would otherwise be merged as silently
  # incomplete coverage: the tests pass, the percentage quietly drops. The marker is `<class`, NOT
  # `<package`: an empty report is `<packages />`, which contains `<package` as a substring and
  # would pass (measured — that is exactly what dynamic instrumentation emits, see the header).
  local cov=1
  if [[ -n "${COVERAGE_DIR:-}" ]]; then
    grep -q '<class' "$COVERAGE_DIR/$ns/coverage.cobertura.xml" 2>/dev/null || cov=0
    # --report-trx copies coverage.cobertura.xml into the TRX attachment tree byte for byte. At
    # ~40 MB a unit that is GBs of pure duplication across the module — and it is the copy that
    # makes a recursive report search count every report twice (the miscount that kept develop red
    # once).
    rm -f "$COVERAGE_DIR/$ns"/_*/In/*/coverage.cobertura.xml
  fi
  echo "$p $f $peak $rc $((EPOCHSECONDS-t0)) $sum $cov" >"$RESULTS_DIR/$ns.result"
  release_slot "$slot"
}

# Contamination snapshot goes HERE — after our own build, before the first batch — so the build
# this script just performed is outside the window and cannot be mistaken for interference.
GUARD_STATE=""
trap 'rm -rf "$RESULTS_DIR" ${GUARD_STATE:+"$GUARD_STATE"} ${SLOT_ROOT:+"$SLOT_ROOT"} ${SLOT_LOCKS:+"$SLOT_LOCKS"}' EXIT
if [[ -z "${NO_GUARD:-}" ]]; then
  mkdir -p "$REPO/.tmp"
  GUARD_STATE="$(mktemp "$REPO/.tmp/assembly-guard-memsafe-XXXXXX.state")"
  "$REPO/scripts/assembly-guard.sh" snapshot "$GUARD_STATE" --root "$(dirname "$EXE")"
fi

# One instrumentable copy of the output tree per concurrent job — see the header. Copies only, so
# the guarded bin/ itself is never handed to an instrumenting process.
if [[ -n "${COVERAGE_DIR:-}" ]]; then
  SLOT_ROOT="$(dirname "$(dirname "$EXE")")/cov-slots"
  SLOT_LOCKS="$(mktemp -d)"
  rm -rf "$SLOT_ROOT"; mkdir -p "$SLOT_ROOT"
  echo ">> Coverage mode: cloning the test output tree into $JOBS slot(s) under $SLOT_ROOT…"
  for ((i = 0; i < JOBS; i++)); do cp -a "$(dirname "$EXE")" "$SLOT_ROOT/$i" & done
  wait
fi

# Longest-first (LPT) so the parallel schedule doesn't end on a 90s batch started last. Every
# namespace that took >= 10s is listed, descending; the trailing comment is that measurement.
# Weights measured 2026-08-29 on a 16-core host as max(run1, run2) of two default-JOBS runs;
# harmless if stale — unlisted namespaces follow alphabetically. Re-generate from a run's `dur=`
# column when the tail stops shrinking — and note only the PER-NAMESPACE shape yields a
# per-namespace `dur=`, so regenerate from a run with TEST_GROUPS unset. These weights are also what
# the TEST_GROUPS packer bins on.
# The cut-off is 10s and not 20s because everything unlisted weighs 1 regardless of its real cost:
# at a 15s cut-off the 71 remaining namespaces hid 248s of work from the packer and TEST_GROUPS=4
# came out worse than the stale table it replaced (486s vs 479s of true load on the fullest bin).
# Listing down to 10s brings that to 439s; going below 10s buys another ~13s for 11 more entries.
HEAVY=(
  XE_Local_AI_Engine.Tests.DevWorkflows                 # 196s (single sample, 2026-08-29 merged-tree run)
  XE_Local_AI_Engine.Tests.Endpoints.Benchmarks.V1      # 118s
  XE_Local_AI_Engine.Tests.WorkSessions                 # 108s
  XE_Local_AI_Engine.Tests.Endpoints.Training.V1        # 88s
  XE_Local_AI_Engine.Tests.Endpoints.DevelopmentWorkflows.V1 # 74s (single sample, 2026-08-29 merged-tree run)
  XE_Local_AI_Engine.Tests.Development                  # 70s
  XE_Local_AI_Engine.Tests.Endpoints.WorkSessions.V1    # 65s
  XE_Local_AI_Engine.Tests.Mcp                          # 65s
  XE_Local_AI_Engine.Tests.Endpoints.LocalModels        # 57s
  XE_Local_AI_Engine.Tests.Hosting                      # 56s
  XE_Local_AI_Engine.Tests.Endpoints.Images             # 52s
  XE_Local_AI_Engine.Tests.Endpoints.Agents             # 50s
  XE_Local_AI_Engine.Tests.Endpoints.Development.V1     # 50s
  XE_Local_AI_Engine.Tests.Chat                         # 44s
  XE_Local_AI_Engine.Tests.Endpoints.Knowledge          # 41s
  XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1        # 41s
  XE_Local_AI_Engine.Tests.CloudSettings                # 38s
  XE_Local_AI_Engine.Tests.Providers.LlamaServer        # 37s
  XE_Local_AI_Engine.Tests.Endpoints.Drafting           # 35s
  XE_Local_AI_Engine.Tests.Auth                         # 31s
  XE_Local_AI_Engine.Tests.Endpoints.Development        # 31s
  XE_Local_AI_Engine.Tests.Sandbox                      # 30s
  XE_Local_AI_Engine.Tests.NodeSettings                 # 28s
  XE_Local_AI_Engine.Tests.ApiFoundation                # 27s
  XE_Local_AI_Engine.Tests.Automation                   # 27s
  XE_Local_AI_Engine.Tests.PreviewWorkflows             # 26s
  XE_Local_AI_Engine.Tests.Endpoints.LocalChat          # 25s
  XE_Local_AI_Engine.Tests.Endpoints.ExternalProviders  # 24s
  XE_Local_AI_Engine.Tests.Endpoints.NodeBinding.V1     # 24s
  XE_Local_AI_Engine.Tests.Endpoints.Skills             # 24s
  XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp # 24s
  XE_Local_AI_Engine.Tests.Agents                       # 22s
  XE_Local_AI_Engine.Tests.Endpoints.CustomTools.V1     # 20s
  XE_Local_AI_Engine.Tests.Endpoints.Workspaces         # 20s
  XE_Local_AI_Engine.Tests.Proxy                        # 17s
  XE_Local_AI_Engine.Tests.Connection                   # 16s
  XE_Local_AI_Engine.Tests.Invocation                   # 16s
  XE_Local_AI_Engine.Tests.ContainerSandbox             # 14s
  XE_Local_AI_Engine.Tests.Endpoints.Cloud.Codex        # 13s
  XE_Local_AI_Engine.Tests.Auth.Integration             # 12s
  XE_Local_AI_Engine.Tests.Benchmarks                   # 11s
  XE_Local_AI_Engine.Tests.Endpoints.TutorialState      # 11s
  XE_Local_AI_Engine.Tests.BackgroundServices           # 10s
  XE_Local_AI_Engine.Tests.Endpoints.Invocations        # 10s
  XE_Local_AI_Engine.Tests.Memory                       # 10s
)
declare -A IS_HEAVY=()
ORDERED=()
for h in "${HEAVY[@]}"; do
  for ns in "${NAMESPACES[@]}"; do [[ "$ns" == "$h" ]] && { ORDERED+=("$h"); IS_HEAVY[$h]=1; }; done
done
for ns in "${NAMESPACES[@]}"; do [[ -z "${IS_HEAVY[$ns]:-}" ]] && ORDERED+=("$ns"); done

# TEST_GROUPS: pack the namespaces into N processes instead of one per namespace. WHY it exists:
# with coverage on, per-namespace batching pays the static-instrumentation start-up cost ~98 times,
# and that cost dominates on a slow runner. Measured 2026-08-23 on the 16-core box, coverage on,
# JOBS=4:
#   98 batches            1991 CPU-s   7:07 wall   868 MB/proc
#   TEST_GROUPS=8          830 CPU-s   3:12 wall  1557 MB/proc
#   TEST_GROUPS=4          684 CPU-s   2:43 wall  1532 MB/proc   <- CI uses this shape
#   one process, width 4   677 CPU-s   7:44 wall  7719 MB (one)
# TEST_GROUPS=4 costs the same CPU as a single process (the floor) while actually using the cores:
# 4.2x parallelism against 1.46x, because per-test host builds serialize behind the static
# HostStartupLock and width inside one process cannot escape it.
#
# The knob is TEST_GROUPS and NOT `GROUPS`: bash pre-populates GROUPS with the caller's group ids,
# so `${GROUPS:-}` is never empty (it reads 1000 here) and a `-n` test on it is always true. That
# bug made the per-namespace default unreachable on every machine, and as root — gid 0 — it built
# ZERO units and still reported "ALL NAMESPACE BATCHES GREEN". Hence the empty-unit guard below.
UNITS=()
if [[ -n "${TEST_GROUPS:-}" ]]; then
  # Validate before the packer: under `set -u` a non-numeric or zero value otherwise dies with an
  # unbound-variable trace instead of saying what is wrong.
  if [[ ! "$TEST_GROUPS" =~ ^[1-9][0-9]*$ ]]; then
    echo "ERROR: TEST_GROUPS must be a positive integer, got '$TEST_GROUPS'." >&2
    exit 1
  fi
  # Weights come from the `# NNs` comments on the HEAVY entries above — the same measurements that
  # order the list. Anything unlisted is short by construction, so it weighs 1.
  declare -A WEIGHT=()
  while read -r wns wsec; do WEIGHT[$wns]="$wsec"; done < <(
    sed -nE 's/^  ([A-Za-z0-9_.]+) +# *([0-9]+)s.*/\1 \2/p' "${BASH_SOURCE[0]}")
  (( ${#WEIGHT[@]} )) || echo "WARN: no weights parsed from the HEAVY list — packing degenerates to round-robin" >&2
  declare -a BIN_NS=() BIN_LOAD=() BIN_COUNT=() BIN_HEAD=()
  for ((g = 0; g < TEST_GROUPS; g++)); do BIN_NS[g]=""; BIN_LOAD[g]=0; BIN_COUNT[g]=0; BIN_HEAD[g]=""; done
  # LPT: ORDERED is already longest-first, so appending each to the lightest bin is the greedy pack.
  for ns in "${ORDERED[@]}"; do
    light=0
    for ((g = 1; g < TEST_GROUPS; g++)); do (( BIN_LOAD[g] < BIN_LOAD[light] )) && light=$g; done
    BIN_NS[light]="${BIN_NS[light]:+${BIN_NS[light]}|}$ns"
    BIN_LOAD[light]=$(( BIN_LOAD[light] + ${WEIGHT[$ns]:-1} ))
    BIN_COUNT[light]=$(( BIN_COUNT[light] + 1 ))
    # First in wins: ORDERED is longest-first, so the first namespace a bin gets is its heaviest.
    [[ -z "${BIN_HEAD[light]}" ]] && BIN_HEAD[light]="${ns#XE_Local_AI_Engine.Tests.}"
  done
  # Every shard packs the SAME bins from the same weights — the pack is deterministic — and then
  # keeps only its own stride. That is why the shards partition the module exactly once with no
  # cross-runner coordination.
  shard_groups=()
  for ((g = 0; g < TEST_GROUPS; g++)); do
    [[ -z "${BIN_NS[g]}" ]] && continue
    if [[ -n "$SHARD_COUNT" ]]; then
      (( g % SHARD_COUNT == SHARD_INDEX )) || continue
      shard_groups+=("$g")
    fi
    # Name the unit after its heaviest member so a FAILED line says something on its own.
    UNITS+=("group${g}[${BIN_HEAD[g]}+$(( BIN_COUNT[g] - 1 ))]"$'\t'"/*/(${BIN_NS[g]})/*/*")
    echo "   group$g: weight=${BIN_LOAD[g]}s count=${BIN_COUNT[g]} => ${BIN_NS[g]//|/ }"
  done
  if [[ -n "$SHARD_COUNT" ]]; then
    echo ">> Shard $SHARD_INDEX/$SHARD_COUNT: running groups ${shard_groups[*]:-none} of $TEST_GROUPS."
  fi
  echo ">> Running ${#UNITS[@]} namespace groups (JOBS=$JOBS${COVERAGE_DIR:+, coverage+TRX -> $COVERAGE_DIR/<group>})…"
else
  for ns in "${ORDERED[@]}"; do UNITS+=("$ns"$'\t'"/*/${ns}/*/*"); done
  echo ">> Running namespace batches (JOBS=$JOBS${COVERAGE_DIR:+, coverage+TRX -> $COVERAGE_DIR/<namespace>})…"
fi
# A run that enrolled nothing must never reach the green summary — see the TEST_GROUPS note above.
if [[ ${#UNITS[@]} -eq 0 ]]; then
  echo "ERROR: no test units to run (TEST_GROUPS=${TEST_GROUPS:-unset}" \
       "TEST_SHARD=${TEST_SHARD:-unset} selected no groups)." >&2
  exit 1
fi

# The unit list is the authoritative count for anything downstream that has to check "one report
# per unit" — the workflow cannot infer it from nproc, since a group can be dropped when empty.
if [[ -n "${COVERAGE_DIR:-}" ]]; then
  mkdir -p "$COVERAGE_DIR"
  printf '%s\n' "${UNITS[@]%%$'\t'*}" >"$COVERAGE_DIR/units.txt"
fi

running=0
for unit in "${UNITS[@]}"; do
  run_ns "${unit%%$'\t'*}" "${unit#*$'\t'}" &
  running=$((running+1))
  if (( running >= JOBS )); then wait -n; running=$((running-1)); fi
done
wait

TOTAL_PASS=0; TOTAL_FAIL=0; FAILED=(); PEAK_ALL=0
for unit in "${UNITS[@]}"; do
  ns="${unit%%$'\t'*}"
  rfile="$RESULTS_DIR/$ns.result"
  if [[ ! -f "$rfile" ]]; then FAILED+=("$ns(no-result)"); continue; fi
  read -r p f peak rc _dur sum cov <"$rfile"
  TOTAL_PASS=$((TOTAL_PASS+p)); TOTAL_FAIL=$((TOTAL_FAIL+f))
  [[ "$peak" -gt "$PEAK_ALL" ]] && PEAK_ALL="$peak"
  if [[ "$rc" == "oom" ]]; then
    FAILED+=("$ns(oom)")
  elif [[ "$f" -gt 0 ]]; then
    FAILED+=("$ns")
    [[ -f "$RESULTS_DIR/$ns.fails" ]] && sed "s/^/   [$ns] /" "$RESULTS_DIR/$ns.fails"
  elif [[ "$sum" != 1 ]]; then
    # No MTP run summary: the batch enrolled nothing or died before reporting. Never green.
    FAILED+=("$ns(no-summary,exit=$rc)")
  elif [[ "$rc" != 0 ]]; then
    # A reporter/process error after partial successes must not read as green.
    FAILED+=("$ns(exit=$rc)")
  elif [[ "$cov" != 1 ]]; then
    FAILED+=("$ns(no-coverage-report)")
  fi
done

# Verify BEFORE the verdict: if the assemblies moved, there is no verdict to report.
if [[ -n "$GUARD_STATE" ]]; then
  if ! "$REPO/scripts/assembly-guard.sh" verify "$GUARD_STATE"; then
    echo "======================================================================"
    echo "RESULT VOID (pass=$TOTAL_PASS fail=$TOTAL_FAIL was measured against assemblies that changed)"
    exit 75
  fi
fi

echo "======================================================================"
echo "TOTAL: pass=$TOTAL_PASS fail=$TOTAL_FAIL  peakRSS(any batch)=$((PEAK_ALL/1024))MB"
echo "(pass may exceed the ~4.9k unique tests: a few tests live in a parent namespace also matched by a child batch.)"
if [[ ${#FAILED[@]} -gt 0 ]]; then echo "FAILED namespaces: ${FAILED[*]}"; exit 1; fi
echo "ALL NAMESPACE BATCHES GREEN"
