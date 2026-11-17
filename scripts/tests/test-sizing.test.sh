#!/usr/bin/env bash
# Self-check for scripts/lib/test-sizing.sh: the JOBS formula, the unknown-memory fallback and the
# exact evidence strings the gate log carries.
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
# shellcheck source=scripts/lib/test-sizing.sh
source "$ROOT/scripts/lib/test-sizing.sh"

check() {
  local name="$1" expected="$2" actual="$3"
  if [[ "$actual" != "$expected" ]]; then
    echo "FAIL $name: expected '$expected', got '$actual'" >&2
    exit 1
  fi
  echo "PASS $name"
}

check "sourcing defines the constants" "1.0 5 4" \
  "$XE_SIZING_BATCH_GB $XE_SIZING_SIBLING_RESERVE_GB $XE_SIZING_HEADROOM_GB"
check "compute_jobs 10 6 -> 1" 1 "$(xe_sizing_compute_jobs 10 6)"
check "compute_jobs 10 40 -> 10" 10 "$(xe_sizing_compute_jobs 10 40)"
check "compute_jobs 10 15 -> 6" 6 "$(xe_sizing_compute_jobs 10 15)"
check "compute_jobs 16 23 -> 14" 14 "$(xe_sizing_compute_jobs 16 23)"
check "compute_jobs 10 '' -> default" 10 "$(xe_sizing_compute_jobs 10 '')"

printf 'MemTotal:       32000000 kB\nMemAvailable:   17406362 kB\n' >"$TMP/meminfo"
check "mem_available_gb reads MemAvailable" 16.6 "$(XE_SIZING_MEMINFO="$TMP/meminfo" xe_sizing_mem_available_gb)"
check "compute_jobs reads meminfo" 7 "$(XE_SIZING_MEMINFO="$TMP/meminfo" xe_sizing_compute_jobs 10)"
check "no meminfo -> empty" "" "$(XE_SIZING_MEMINFO="$TMP/absent" xe_sizing_mem_available_gb)"
check "no meminfo -> default" 16 "$(XE_SIZING_MEMINFO="$TMP/absent" xe_sizing_compute_jobs 16)"

check "describe known" ">> Sizing: MemAvailable=16.6 GB → JOBS=7 (default 12)" "$(xe_sizing_describe 12 16.6)"
check "describe unknown" ">> Sizing: MemAvailable unknown → JOBS=10 (default)" \
  "$(XE_SIZING_MEMINFO="$TMP/absent" xe_sizing_describe 10)"

echo "test-sizing.test.sh: PASS"
