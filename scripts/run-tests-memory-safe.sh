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
# This script runs the module in fresh-process batches, ONE per test namespace, each single-threaded. A fresh process
# per namespace resets the leak between batches (bounding peak RSS) and — because namespaces are the natural test-tree
# partition — covers every test exactly once with no source-parsing guesswork. Single-threaded execution also removes
# the cross-test env-mutation races (XE_NODE_SQLITE_KEY set/unset) documented in docs/agent-knowledge.md §1.
#
# It is the low-risk MITIGATION, not a root-cause fix. CI (7-16 GB runners) can still run the module in one process.
#
# Usage:
#   scripts/run-tests-memory-safe.sh                # build (Release) + run every namespace batch
#   NO_BUILD=1 scripts/run-tests-memory-safe.sh     # skip the build (bin must be current)
#   PAR=4 scripts/run-tests-memory-safe.sh          # allow N parallel tests per batch (faster, may reintroduce flakes)
#
# Env knobs:
#   PAR          max parallel tests per batch (default 1 = deterministic + lowest RSS; >1 is faster but can flake)
#   AVAIL_FLOOR  abort a batch if available RAM drops below this many MB (default 800)
#   NO_BUILD     when set, skip the Release build
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJ="$REPO/XE-Local-AI-Engine.Tests"
EXE="$PROJ/bin/Release/net10.0/XE-Local-AI-Engine.Tests"
PAR="${PAR:-1}"
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

TOTAL_PASS=0; TOTAL_FAIL=0; FAILED=(); PEAK_ALL=0

run_ns() {
  local ns="$1" out; out="$(mktemp)"
  "$EXE" --treenode-filter "/*/${ns}/*/*" --maximum-parallel-tests "$PAR" >"$out" 2>&1 &
  local pid=$! peak=0
  while kill -0 "$pid" 2>/dev/null; do
    local rss avail
    rss="$(ps -o rss= -p "$pid" 2>/dev/null | tr -d ' ')"
    [[ -n "$rss" && "$rss" -gt "$peak" ]] && peak="$rss"
    avail="$(free -m | awk '/^Mem:/{print $7}')"
    if [[ "${avail:-9999}" -lt "$AVAIL_FLOOR" ]]; then
      echo "   !! SAFETY-KILL $ns: avail ${avail}MB < ${AVAIL_FLOOR}MB (rss ${rss}KB)"; kill -9 "$pid" 2>/dev/null
      FAILED+=("$ns(oom)"); rm -f "$out"; return
    fi
    sleep 1
  done
  wait "$pid"; local rc=$?
  local p f
  p="$(grep -oE 'succeeded: *[0-9]+' "$out" | grep -oE '[0-9]+' | tail -1)"; p="${p:-0}"
  f="$(grep -oE 'failed: *[0-9]+' "$out" | grep -oE '[0-9]+' | tail -1)"; f="${f:-0}"
  TOTAL_PASS=$((TOTAL_PASS+p)); TOTAL_FAIL=$((TOTAL_FAIL+f))
  [[ "$peak" -gt "$PEAK_ALL" ]] && PEAK_ALL="$peak"
  printf "   %-52s pass=%-4s fail=%-3s peakRSS=%sMB exit=%s\n" "$ns" "$p" "$f" "$((peak/1024))" "$rc"
  if [[ "$f" -gt 0 ]]; then FAILED+=("$ns"); grep -E 'failed|error' "$out" | grep -viE 'failed: 0' | head -3; fi
  rm -f "$out"
}

echo ">> Running namespace batches…"
for ns in "${NAMESPACES[@]}"; do run_ns "$ns"; done

echo "======================================================================"
echo "TOTAL: pass=$TOTAL_PASS fail=$TOTAL_FAIL  peakRSS(any batch)=$((PEAK_ALL/1024))MB"
echo "(pass may exceed the ~3343 unique tests: a few tests live in a parent namespace also matched by a child batch.)"
if [[ ${#FAILED[@]} -gt 0 ]]; then echo "FAILED namespaces: ${FAILED[*]}"; exit 1; fi
echo "ALL NAMESPACE BATCHES GREEN"
