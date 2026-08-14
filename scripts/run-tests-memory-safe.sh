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
# This script runs the module in fresh-process batches, ONE per test namespace, each single-threaded WITHIN the
# process. A fresh process per namespace resets the leak between batches (bounding peak RSS) and — because namespaces
# are the natural test-tree partition — covers every test exactly once with no source-parsing guesswork.
# Single-threaded execution also removes the cross-test env-mutation races (XE_NODE_SQLITE_KEY set/unset) documented
# in docs/agent-knowledge.md §1. Those races are per-process, so JOBS batch processes run concurrently (see below).
#
# It is the low-risk MITIGATION, not a root-cause fix. CI (7-16 GB runners) can still run the module in one process.
#
# Build contamination
#   The batches run the test host against bin/ WITHOUT rebuilding, so a concurrent `dotnet build`
#   from any other shell rewrites the assemblies mid-run and produces phantom failures. This script
#   defends on both sides: it re-execs itself under the cross-process build lock
#   (scripts/with-build-lock.sh), and it snapshots the test output tree before the first batch and
#   re-checks it after the last, reporting exit 75 CONTAMINATED rather than a fail/pass verdict it
#   cannot stand behind. See docs/agent-knowledge.md §1.
#
# Batch-level parallelism (JOBS)
#   The per-process serialization above is deliberate, but nothing requires the *processes* to run one
#   after another: every hazard the fresh-process design defends against is process-scoped (env-var
#   mutation, PATH stubs, meter/ActivityListener capture, the HostStartupLock) or already isolated
#   per host (GUID-named temp SQLite/data dirs, port-0 binds). So the batches run JOBS at a time,
#   longest-first, each still single-threaded by default. Measured on the 8-core/32GB dev box:
#   sequential 649s of batches -> ~1/JOBS wall clock, floored by the largest batch (~41s).
#   JOBS=1 reproduces the old strictly-sequential behavior exactly.
#
# Usage:
#   scripts/run-tests-memory-safe.sh                # build (Release) + run every namespace batch
#   NO_BUILD=1 scripts/run-tests-memory-safe.sh     # skip the build (bin must be current)
#   JOBS=1 scripts/run-tests-memory-safe.sh         # old behavior: batches strictly sequential
#   PAR=4 scripts/run-tests-memory-safe.sh          # allow N parallel tests per batch (faster, may reintroduce flakes)
#
# Env knobs:
#   JOBS            how many namespace batch PROCESSES run concurrently (default 4; 1 = sequential)
#   PAR             max parallel tests per batch (default 1 = deterministic + lowest RSS; >1 is faster but can flake)
#   AVAIL_FLOOR     a batch aborts if available RAM drops below this many MB (default 800; with JOBS>1
#                   every batch that observes the breach kills itself — safety over completeness)
#   NO_BUILD        when set, skip the Release build
#   NO_BUILD_LOCK   when set, do NOT take the cross-process build lock (escape hatch; you are then
#                   relying on the contamination DETECTION alone)
#   NO_GUARD        when set, skip the contamination snapshot/verify. Do not use this to make a
#                   contaminated run look green.
#
# Exit codes:
#   0   — every namespace batch green
#   1   — one or more batches had failures
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
PAR="${PAR:-1}"
JOBS="${JOBS:-4}"
AVAIL_FLOOR="${AVAIL_FLOOR:-800}"

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

run_ns() {
  local ns="$1" out; out="$(mktemp)"; local t0=$EPOCHSECONDS
  "$EXE" --treenode-filter "/*/${ns}/*/*" --maximum-parallel-tests "$PAR" >"$out" 2>&1 &
  local pid=$! peak=0
  while kill -0 "$pid" 2>/dev/null; do
    local rss avail
    rss="$(ps -o rss= -p "$pid" 2>/dev/null | tr -d ' ')"
    [[ -n "$rss" && "$rss" -gt "$peak" ]] && peak="$rss"
    avail="$(free -m | awk '/^Mem:/{print $7}')"
    if [[ "${avail:-9999}" -lt "$AVAIL_FLOOR" ]]; then
      echo "   !! SAFETY-KILL $ns: avail ${avail}MB < ${AVAIL_FLOOR}MB (rss ${rss}KB)"; kill -9 "$pid" 2>/dev/null
      echo "0 1 $peak oom $((EPOCHSECONDS-t0))" >"$RESULTS_DIR/$ns.result"; rm -f "$out"; return
    fi
    sleep 1
  done
  wait "$pid"; local rc=$?
  local p f
  p="$(grep -oE 'succeeded: *[0-9]+' "$out" | grep -oE '[0-9]+' | tail -1)"; p="${p:-0}"
  f="$(grep -oE 'failed: *[0-9]+' "$out" | grep -oE '[0-9]+' | tail -1)"; f="${f:-0}"
  printf "   %-52s pass=%-4s fail=%-3s peakRSS=%sMB exit=%s dur=%ss\n" "$ns" "$p" "$f" "$((peak/1024))" "$rc" "$((EPOCHSECONDS-t0))"
  if [[ "$f" -gt 0 ]]; then grep -E 'failed|error' "$out" | grep -viE 'failed: 0' | head -3 >"$RESULTS_DIR/$ns.fails"; fi
  echo "$p $f $peak $rc $((EPOCHSECONDS-t0))" >"$RESULTS_DIR/$ns.result"
  rm -f "$out"
}

# Contamination snapshot goes HERE — after our own build, before the first batch — so the build
# this script just performed is outside the window and cannot be mistaken for interference.
GUARD_STATE=""
trap 'rm -rf "$RESULTS_DIR" ${GUARD_STATE:+"$GUARD_STATE"}' EXIT
if [[ -z "${NO_GUARD:-}" ]]; then
  mkdir -p "$REPO/.tmp"
  GUARD_STATE="$(mktemp "$REPO/.tmp/assembly-guard-memsafe-XXXXXX.state")"
  "$REPO/scripts/assembly-guard.sh" snapshot "$GUARD_STATE" --root "$(dirname "$EXE")"
fi

# Longest-first (LPT) so the parallel schedule doesn't end on a 40s batch started last. Weights
# measured 2026-08-15 on the dev box; harmless if stale — unlisted namespaces follow alphabetically.
HEAVY=(
  XE_Local_AI_Engine.Tests.Endpoints.Agents
  XE_Local_AI_Engine.Tests.Chat
  XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1
  XE_Local_AI_Engine.Tests.Providers.LlamaServer
  XE_Local_AI_Engine.Tests.Development
  XE_Local_AI_Engine.Tests.Endpoints.Skills
  XE_Local_AI_Engine.Tests.Endpoints.LocalModels
  XE_Local_AI_Engine.Tests.ApiFoundation
  XE_Local_AI_Engine.Tests.Mcp
  XE_Local_AI_Engine.Tests.Hosting
  XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp
  XE_Local_AI_Engine.Tests.Endpoints.Images
  XE_Local_AI_Engine.Tests.Endpoints.Development.V1
  XE_Local_AI_Engine.Tests.Endpoints.Benchmarks.V1
  XE_Local_AI_Engine.Tests.CloudSettings
)
declare -A IS_HEAVY=()
ORDERED=()
for h in "${HEAVY[@]}"; do
  for ns in "${NAMESPACES[@]}"; do [[ "$ns" == "$h" ]] && { ORDERED+=("$h"); IS_HEAVY[$h]=1; }; done
done
for ns in "${NAMESPACES[@]}"; do [[ -z "${IS_HEAVY[$ns]:-}" ]] && ORDERED+=("$ns"); done

echo ">> Running namespace batches (JOBS=$JOBS)…"
running=0
for ns in "${ORDERED[@]}"; do
  run_ns "$ns" &
  running=$((running+1))
  if (( running >= JOBS )); then wait -n; running=$((running-1)); fi
done
wait

TOTAL_PASS=0; TOTAL_FAIL=0; FAILED=(); PEAK_ALL=0
for ns in "${ORDERED[@]}"; do
  rfile="$RESULTS_DIR/$ns.result"
  if [[ ! -f "$rfile" ]]; then FAILED+=("$ns(no-result)"); continue; fi
  read -r p f peak rc _dur <"$rfile"
  TOTAL_PASS=$((TOTAL_PASS+p)); TOTAL_FAIL=$((TOTAL_FAIL+f))
  [[ "$peak" -gt "$PEAK_ALL" ]] && PEAK_ALL="$peak"
  if [[ "$rc" == "oom" ]]; then
    FAILED+=("$ns(oom)")
  elif [[ "$f" -gt 0 ]]; then
    FAILED+=("$ns")
    [[ -f "$RESULTS_DIR/$ns.fails" ]] && sed "s/^/   [$ns] /" "$RESULTS_DIR/$ns.fails"
  elif [[ "$rc" != 0 && "$p" -eq 0 ]]; then
    # The batch process died without reporting a single test — a crash must not read as green.
    FAILED+=("$ns(exit=$rc)")
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
