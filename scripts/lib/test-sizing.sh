# shellcheck shell=bash
# test-sizing.sh — sourced by scripts/run-backend-tests.sh and scripts/run-tests-memory-safe.sh, never
# executed. Sizes the batched module's JOBS from the RAM that is free NOW, so a gate started on a box
# already loaded by other worktrees shrinks instead of swapping, and one started on an idle box keeps
# the measured default. XE_TEST_PROFILE=low-memory and an explicit JOBS bypass it entirely.
#
#   JOBS = clamp(1, default, floor((MemAvailable − SIBLING_RESERVE − HEADROOM) / BATCH))
#
# Constants (GB), from measurements, not a model:
#   BATCH 1.0            2026-11-17, 16 vCPU / 32 GB, full gate shape: the summed host-tree RSS of all
#                        live batches peaked at 0.8 GB per batch at JOBS 10 and 12 (3 runs each, siblings
#                        included in the total); the old 1.5 came from a probe that later read only the
#                        MTP controller, so it can no longer be re-checked from gate output
#   SIBLING_RESERVE 5    the concurrent sibling lanes: Client.Persistence.Tests 3.2 GB at its pinned
#                        width, plus AI.Agent.Tests
#   HEADROOM 4           the OS, the IDE and whatever else the host runs
# Fixed per-batch GB; re-measure with scripts/test-durations.py if batches change
#
# Test seam: XE_SIZING_MEMINFO names the meminfo file (default /proc/meminfo); a missing file reads
# as unknown memory, which is also what macOS gets.

XE_SIZING_BATCH_GB=1.0
XE_SIZING_SIBLING_RESERVE_GB=5
XE_SIZING_HEADROOM_GB=4

# The measured JOBS defaults (see the runner header): 16 on a >= 32-CPU host, 12 on 16-31, 10 below that.
xe_sizing_default_jobs() {
  local nproc
  nproc="$(nproc 2>/dev/null || echo 4)"
  echo $(( nproc >= 32 ? 16 : nproc >= 16 ? 12 : 10 ))
}

# MemAvailable in GB with one decimal; prints nothing where the file or the field is missing.
xe_sizing_mem_available_gb() {
  local meminfo="${XE_SIZING_MEMINFO:-/proc/meminfo}"
  [[ -r "$meminfo" ]] || return 0
  awk '/^MemAvailable:/ { printf "%.1f\n", $2 / 1048576; exit }' "$meminfo"
}

# xe_sizing_compute_jobs <default_jobs> [<mem_available_gb>]
# With one argument the memory is read here; an explicit empty or non-numeric second argument means
# unknown, and unknown memory yields the default unchanged.
xe_sizing_compute_jobs() {
  local default="$1" mem
  if (( $# >= 2 )); then mem="$2"; else mem="$(xe_sizing_mem_available_gb)"; fi
  if [[ ! "$mem" =~ ^[0-9]+(\.[0-9]+)?$ ]]; then
    echo "$default"
    return 0
  fi
  awk -v d="$default" -v m="$mem" -v r="$XE_SIZING_SIBLING_RESERVE_GB" \
      -v h="$XE_SIZING_HEADROOM_GB" -v b="$XE_SIZING_BATCH_GB" \
      'BEGIN { j = int((m - r - h) / b); if (j < 1) j = 1; if (j > d) j = d; print j }'
}

# xe_sizing_describe <default_jobs> [<mem_available_gb>] — the one-line evidence for the gate log.
xe_sizing_describe() {
  local default="$1" mem jobs
  if (( $# >= 2 )); then mem="$2"; else mem="$(xe_sizing_mem_available_gb)"; fi
  jobs="$(xe_sizing_compute_jobs "$default" "$mem")"
  if [[ "$mem" =~ ^[0-9]+(\.[0-9]+)?$ ]]; then
    echo ">> Sizing: MemAvailable=$mem GB → JOBS=$jobs (default $default)"
  else
    echo ">> Sizing: MemAvailable unknown → JOBS=$jobs (default)"
  fi
}
