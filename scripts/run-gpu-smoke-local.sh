#!/usr/bin/env bash
# run-gpu-smoke-local.sh — OPT-IN live GPU smoke for a real, locally started node.
#
# Why this exists
#   The 2026-07-31 live AI evaluation (Plans/2026-07-31-live-ai-evaluation.md) cost most of its
#   effort DISCOVERING a sequence, not running it. Re-running it is cheap. This is that sequence,
#   encoded, so the defects it found cannot silently come back.
#
#   The single most valuable thing it caught was a SILENT CPU FALLBACK: a GPU-variant llama.cpp
#   runtime that enumerated no GPU devices and answered every prompt correctly, just slowly. A
#   reply proves nothing. So this script's load-bearing assertion is not "did it answer" but
#   "did the GPU actually do work" — sampled from nvidia-smi while a real turn generates.
#
#   It is OPT-IN by design. Nothing invokes it automatically. Run it by hand before cutting a
#   tester RC, or after touching the inference/runtime path.
#
# What it asserts, in order. Each step must produce a verdict; a step that does not run is a
# FAILURE, never a silent skip.
#
#   1. Runtime identity   — which llama.cpp tag/variant is installed (reported; the CAPABILITY
#                           verdict belongs to step 2, see "configuration vs outcome" below).
#   2. Device audit       — IRuntimeDeviceAudit reports a GPU backend and NOT cpuFallback.
#                           This is the check that would have caught the original silent-CPU state.
#   3. Model load + chat  — a real streamed turn over the SignalR hub returns non-empty content.
#   4. GPU actually used  — nvidia-smi utilisation crossed a threshold DURING generation AND VRAM
#                           rose above the pre-start baseline. THE load-bearing assertion.
#   5. Tool calling       — a tool was actually offered and INVOKED (tool-call-requested plus
#                           tool-call-completed events), not merely "the model replied".
#   6. Image generation   — opt-in (--images): one small job returns real PNG bytes.
#   7. Eject              — VRAM is released back toward the baseline.
#
# Configuration vs observable outcome — the lesson this script encodes
#   Do not assert that a setting is set; assert the behaviour it should produce. Two live findings
#   make this concrete, and both are load-bearing for how step 1 and step 2 are written:
#
#     * A `vulkan` runtime is nominally "GPU-capable" and still runs entirely on the CPU when no
#       Vulkan ICD is present (the exact state of this WSL2 box on 2026-07-31). A variant check
#       alone would have passed while inference was on the CPU.
#     * Conversely, with XE_LLAMACPP_SERVER_PATH pointing at a CUDA binary, the INSTALLED record
#       still reads `vulkan` while inference genuinely runs on CUDA. A variant check alone would
#       have failed a perfectly good GPU box.
#
#   Therefore the device audit (step 2), not the installed-runtime record (step 1), is the
#   authority on whether the GPU is in use. Step 1 reports identity and only fails when the
#   installed variant is CPU-only AND the audit agrees there is no GPU backend.
#
# Usage:
#   scripts/run-gpu-smoke-local.sh [options]
#
# Options:
#   --images              Also run step 6 (image generation). Off by default: it needs an image
#                         model installed and adds ~10-30s. The assertion is never weakened, only
#                         skipped wholesale — and a skipped step is reported, never counted as pass.
#   --no-tools            Skip step 5. Use ONLY when knowingly testing a node with tools disabled;
#                         the skip is reported loudly in the summary.
#   --keep-running        Do not stop the AppHost at the end (for follow-up debugging by hand).
#                         Step 7 still runs; you are then responsible for scripts/dev-stop.sh.
#   --model <name>        Force a specific chat model instead of auto-picking the smallest one.
#   --help                Show this message.
#
# Prerequisites (all checked up front, each with an actionable failure message):
#   - dotnet, python3, aspire   — the AppHost lifecycle (scripts/dev-*.sh)
#   - nvidia-smi                — the whole point; a box without it cannot run this smoke
#   - an installed chat GGUF    — discovered through the API, never a hard-coded path
#
# Env knobs:
#   XE_GPU_SMOKE_EMAIL / XE_GPU_SMOKE_PASSWORD
#                              operator credentials (default admin@localhost.test / !Demo1234567).
#                              On a fresh node the script performs first-run setup with these; on a
#                              node someone already onboarded by hand, set them to the real ones.
#   XE_GPU_SMOKE_MIN_UTIL_PERCENT     peak GPU utilisation required during generation (default 15)
#   XE_GPU_SMOKE_MIN_VRAM_RISE_MIB    VRAM rise over baseline required while loaded (default 150)
#   XE_GPU_SMOKE_EJECT_TOLERANCE_MIB  how close to baseline VRAM must return after eject (default 600)
#   XE_GPU_SMOKE_TIMEOUT_SECONDS      readiness timeout for `aspire wait app` (default 240)
#   XE_GPU_SMOKE_SAMPLE_INTERVAL      nvidia-smi sampling period in seconds (default 0.1)
#   XE_GPU_SMOKE_GPU_INDEX            which GPU to sample (default 0)
#   NO_BUILD_LOCK              do NOT take the cross-process build lock (escape hatch)
#   NO_GUARD                   skip the contamination snapshot/verify
#
#   Model discovery is deliberately NOT an env knob: the script asks the API what is installed, so
#   it works on any machine. To point a node at an existing model store, set the app's own
#   HuggingFace__ModelsDirectory before running.
#
# Exit codes:
#   0  — every expected step ran AND passed
#   1  — an assertion failed, or the run was vacuous (a step produced no verdict)
#   2  — a prerequisite is missing / usage error (nothing was run)
#   3  — an AppHost for this worktree is already running; refusing to reuse or stop it
#   4  — could not establish whether an AppHost is running; refusing to start one
#   75 — CONTAMINATED: the build output changed mid-run; the result is void, re-run it
#
# A note on nvidia-smi under WSL2
#   `nvidia-smi --query-compute-apps` reports NOTHING under WSL2 (verified 2026-07-31) — there is
#   no per-process VRAM attribution available. Only whole-device utilisation.gpu / memory.used can
#   be sampled, which is why the baseline is taken BEFORE the AppHost starts and every VRAM
#   assertion is expressed as a delta against it.

set -uo pipefail

GPU_SMOKE_SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
if ! GPU_SMOKE_PROJECT_ROOT="$(git -C "${GPU_SMOKE_SCRIPT_DIR}" rev-parse --show-toplevel 2>/dev/null)"; then
  GPU_SMOKE_PROJECT_ROOT="$(cd "${GPU_SMOKE_SCRIPT_DIR}/.." && pwd -P)"
fi

DRIVER="${GPU_SMOKE_SCRIPT_DIR}/gpu-smoke-driver.py"
SMOKE_EMAIL="${XE_GPU_SMOKE_EMAIL:-admin@localhost.test}"
SMOKE_PASSWORD="${XE_GPU_SMOKE_PASSWORD:-!Demo1234567}"
MIN_UTIL_PERCENT="${XE_GPU_SMOKE_MIN_UTIL_PERCENT:-15}"
MIN_VRAM_RISE_MIB="${XE_GPU_SMOKE_MIN_VRAM_RISE_MIB:-150}"
EJECT_TOLERANCE_MIB="${XE_GPU_SMOKE_EJECT_TOLERANCE_MIB:-600}"
READY_TIMEOUT_SECONDS="${XE_GPU_SMOKE_TIMEOUT_SECONDS:-240}"
SAMPLE_INTERVAL="${XE_GPU_SMOKE_SAMPLE_INTERVAL:-0.1}"
GPU_INDEX="${XE_GPU_SMOKE_GPU_INDEX:-0}"

log()  { echo "[gpu-smoke] $*"; }
warn() { echo "[gpu-smoke] WARN: $*" >&2; }
prereq_fail() { echo "[gpu-smoke] PREREQUISITE MISSING: $*" >&2; exit 2; }

# ---------------------------------------------------------------------------
# Step ledger — the anti-vacuous-pass mechanism.
#
# Every step declares itself expected BEFORE it runs and records a verdict when it finishes. The
# final gate asserts that every expected step recorded PASS and that at least one step ran. A step
# that dies, is skipped by accident, or silently returns early therefore fails the run instead of
# leaving a green summary behind — the same rule run-e2e-local.sh applies to a zero-test run.
# ---------------------------------------------------------------------------
LEDGER_EXPECTED=()
LEDGER_PASSED=()
LEDGER_SKIPPED=()
LEDGER_FAILED=()
FAILURES=()

ledger_expect() { LEDGER_EXPECTED+=("$1"); }
ledger_pass()   { LEDGER_PASSED+=("$1"); log "PASS  $1"; }
ledger_skip()   { LEDGER_SKIPPED+=("$1"); }

# Record a failure and keep going where it is safe to, so one run reports every broken step
# rather than only the first. The step name is tracked separately from the message so the summary
# can distinguish "this step ran and FAILED" from "this step produced no verdict at all" — the
# latter means something died unexpectedly and is a materially different thing to investigate.
step_fail() {
  local step="$1"; shift
  LEDGER_FAILED+=("${step}")
  FAILURES+=("${step}: $*")
  echo "[gpu-smoke] FAIL  ${step}: $*" >&2
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

# The final gate. Returns non-zero when anything failed, when an expected step produced no
# verdict, or when nothing ran at all.
ledger_finalize() {
  local status=0 step
  echo
  log "=== Summary ==="
  if [[ "${#LEDGER_EXPECTED[@]}" -eq 0 ]]; then
    echo "[gpu-smoke] FAIL: no steps were expected — nothing ran. This is not a pass." >&2
    return 1
  fi
  for step in "${LEDGER_EXPECTED[@]}"; do
    if ledger_contains "${step}" ${LEDGER_PASSED[@]+"${LEDGER_PASSED[@]}"}; then
      echo "  PASS       ${step}"
    elif ledger_contains "${step}" ${LEDGER_FAILED[@]+"${LEDGER_FAILED[@]}"}; then
      echo "  FAILED     ${step}" >&2
      status=1
    elif ledger_contains "${step}" ${LEDGER_SKIPPED[@]+"${LEDGER_SKIPPED[@]}"}; then
      echo "  SKIPPED    ${step}  <- explicitly requested; NOT a pass" >&2
      status=1
    else
      # Nothing recorded a verdict: the step died or returned early without saying so. That is a
      # different failure from an assertion failing, and is exactly the vacuous-pass hole this
      # ledger exists to close.
      echo "  NO VERDICT ${step}  <- the step produced no result at all; treating as failure" >&2
      status=1
    fi
  done
  for step in ${LEDGER_SKIPPED[@]+"${LEDGER_SKIPPED[@]}"}; do
    ledger_contains "${step}" "${LEDGER_EXPECTED[@]}" || echo "  SKIPPED    ${step}  (not required by these flags)"
  done
  if [[ "${#FAILURES[@]}" -gt 0 ]]; then
    echo >&2
    echo "[gpu-smoke] ${#FAILURES[@]} failure(s):" >&2
    for step in "${FAILURES[@]}"; do echo "  - ${step}" >&2; done
    status=1
  fi
  return "${status}"
}

# ---------------------------------------------------------------------------
# Driver record helpers. The driver emits `key<TAB>value`; the shell does the judging.
# ---------------------------------------------------------------------------
record_value() {
  local key="$1" text="$2"
  awk -F'\t' -v k="${key}" '$1 == k { sub(/^[^\t]*\t/, ""); print; exit }' <<<"${text}"
}

record_values() {
  local key="$1" text="$2"
  awk -F'\t' -v k="${key}" '$1 == k { sub(/^[^\t]*\t/, ""); print }' <<<"${text}"
}

# ---------------------------------------------------------------------------
# Assertions. Kept as pure functions of their arguments so scripts/tests/gpu-smoke.test.sh can
# drive every refuse-to-pass path with synthetic driver output and no GPU.
# ---------------------------------------------------------------------------

# Step 1. `installed` is NULLABLE and null does NOT mean "no runtime": a fresh node running the
# pinned-floor binary has no installed-runtime.json record while having a perfectly working
# binary (LlamaCppInstalledRuntimeResponse doc comment). So absence is reported, never failed.
# A CPU-only installed variant is only fatal when the audit agrees there is no GPU backend —
# a bring-your-own GPU binary (XE_LLAMACPP_SERVER_PATH) legitimately contradicts the record.
assert_runtime_identity() {
  local records="$1" audit_backend="$2"
  local installed tag variant
  installed="$(record_value installed "${records}")"
  if [[ "${installed}" != "true" ]]; then
    log "runtime: no installed-runtime record (pinned-floor binary). Capability verdict deferred to the device audit."
    return 0
  fi
  tag="$(record_value tag "${records}")"
  variant="$(record_value variant "${records}")"
  [[ -n "${tag}" ]] || { step_fail "1-runtime-identity" "the installed runtime record has no tag"; return 1; }
  [[ -n "${variant}" ]] || { step_fail "1-runtime-identity" "the installed runtime record has no variant"; return 1; }
  log "runtime: tag=${tag} variant=${variant} sourceBuild=$(record_value isSourceBuild "${records}")"
  if [[ "${variant}" == "cpu" ]]; then
    if [[ "${audit_backend}" == "cuda" || "${audit_backend}" == "vulkan" ]]; then
      warn "installed variant is cpu but the device audit reports '${audit_backend}' — an override is supplying a GPU binary."
      return 0
    fi
    step_fail "1-runtime-identity" \
      "a CPU-only llama.cpp variant is installed on a box with an NVIDIA GPU (tag ${tag}). Install a GPU-capable runtime, or set XE_LLAMACPP_SERVER_PATH + XE_LLAMACPP_VARIANT to a GPU-capable llama-server."
    return 1
  fi
  return 0
}

# Step 2. THE check that would have caught the original silent-CPU state.
# "unknown" is treated as a failure on purpose: an indeterminate probe must never read as a pass.
assert_device_audit() {
  local records="$1"
  local backend expected fallback reason remediation
  backend="$(record_value inferenceBackend "${records}")"
  expected="$(record_value gpuExpected "${records}")"
  fallback="$(record_value cpuFallback "${records}")"
  reason="$(record_value cpuFallbackReason "${records}")"
  remediation="$(record_value cpuFallbackRemediation "${records}")"

  if [[ -z "${backend}" ]]; then
    step_fail "2-device-audit" "hardware-profile returned no inferenceBackend; the audit contract changed"
    return 1
  fi
  log "audit: backend=${backend} gpuExpected=${expected} cpuFallback=${fallback} vendor=$(record_value gpuVendor "${records}")"

  if [[ "${fallback}" == "true" ]]; then
    step_fail "2-device-audit" "the node is in CPU FALLBACK — a GPU runtime is loaded but inference runs on the CPU."
    [[ -n "${reason}" ]] && echo "        reason:      ${reason}" >&2
    [[ -n "${remediation}" ]] && echo "        remediation: ${remediation}" >&2
    return 1
  fi
  case "${backend}" in
    cuda|vulkan) ;;
    cpu)
      step_fail "2-device-audit" "inferenceBackend is 'cpu' — inference is not on the GPU."
      [[ -n "${reason}" ]] && echo "        reason:      ${reason}" >&2
      [[ -n "${remediation}" ]] && echo "        remediation: ${remediation}" >&2
      return 1 ;;
    *)
      step_fail "2-device-audit" "inferenceBackend is '${backend}' — the device probe was indeterminate. An indeterminate probe is not a pass."
      return 1 ;;
  esac
  if [[ "${expected}" != "true" ]]; then
    step_fail "2-device-audit" "gpuExpected is '${expected}' — the node does not expect to use a GPU at all."
    return 1
  fi
  return 0
}

# Step 3 helper. Pick the smallest installed llama.cpp chat model. `/models` mixes local and
# cloud providers, so a provider filter is mandatory: picking a cloud model would produce a
# perfectly green chat step that never touched the GPU.
pick_chat_model() {
  local records="$1" forced="${2:-}"
  local line name kind capable
  if [[ -n "${forced}" ]]; then
    printf '%s\n' "${forced}"
    return 0
  fi
  while IFS= read -r line; do
    [[ -n "${line}" ]] || continue
    IFS='|' read -r name kind capable <<<"${line}"
    [[ "${kind}" == "Chat" ]] || continue
    printf '%s\n' "${name}"
    return 0
  done < <(record_values model "${records}")
  return 1
}

model_is_tool_capable() {
  local records="$1" wanted="$2" line name kind capable
  while IFS= read -r line; do
    IFS='|' read -r name kind capable <<<"${line}"
    if [[ "${name}" == "${wanted}" ]]; then
      [[ "${capable}" == "true" ]]
      return
    fi
  done < <(record_values model "${records}")
  return 1
}

assert_chat_reply() {
  local records="$1"
  local length error events
  length="$(record_value contentLength "${records}")"
  error="$(record_value error "${records}")"
  events="$(record_value events "${records}")"
  log "chat: events=${events}"
  if [[ -n "${error}" ]]; then
    step_fail "3-chat" "the turn failed: ${error}"
    return 1
  fi
  if [[ ! "${length}" =~ ^[0-9]+$ ]] || [[ "${length}" -eq 0 ]]; then
    step_fail "3-chat" "the assistant produced no content (contentLength='${length}'). A completed-but-empty turn is not a pass."
    return 1
  fi
  log "chat: reply=$(record_value content "${records}")"
  return 0
}

# Step 4. THE load-bearing assertion: a correct reply proves nothing, because CPU fallback answers
# correctly too. Both halves must hold — utilisation crossed a floor while generating, AND VRAM
# is still above the pre-start baseline with the model resident.
assert_gpu_was_used() {
  local peak_util="$1" peak_vram="$2" baseline_vram="$3" loaded_vram="$4"
  local rise=$((loaded_vram - baseline_vram))
  log "gpu: peakUtil=${peak_util}% baselineVram=${baseline_vram}MiB peakVram=${peak_vram}MiB loadedVram=${loaded_vram}MiB rise=${rise}MiB"
  local ok=0
  if [[ "${peak_util}" -lt "${MIN_UTIL_PERCENT}" ]]; then
    step_fail "4-gpu-used" \
      "peak GPU utilisation was ${peak_util}%, below the ${MIN_UTIL_PERCENT}% floor. The reply was produced without the GPU doing measurable work — this is the silent-CPU-fallback signature."
    ok=1
  fi
  if [[ "${rise}" -lt "${MIN_VRAM_RISE_MIB}" ]]; then
    step_fail "4-gpu-used" \
      "VRAM rose only ${rise}MiB over the pre-start baseline (floor ${MIN_VRAM_RISE_MIB}MiB). No model weights appear to be resident on the device."
    ok=1
  fi
  return "${ok}"
}

# Step 5. Assert a tool was OFFERED AND INVOKED. Asserting the allowlist config would have passed
# while the feature was broken (F-001/F-025: the allowlist was correct but seeded once at startup,
# so tools were silently withheld) — so only the turn's own tool-call events count.
assert_tool_call() {
  local records="$1"
  local requested completed
  requested="$(record_value toolsRequested "${records}")"
  completed="$(record_value toolsCompleted "${records}")"
  if [[ -z "${requested}" ]]; then
    step_fail "5-tool-calling" \
      "no tool was offered or invoked (no tool-call-requested event). Two gates guard this: the model must be tool-capable, and the node must have tools enabled. Note the allowlist is read at startup — a saved settings change does not apply until the node restarts."
    return 1
  fi
  if [[ -z "${completed}" ]]; then
    step_fail "5-tool-calling" "tool '${requested}' was requested but never completed."
    return 1
  fi
  log "tools: requested=[${requested}] completed=[${completed}]"
  return 0
}

assert_image_result() {
  local records="$1"
  local status png bytes error
  status="$(record_value status "${records}")"
  png="$(record_value png "${records}")"
  bytes="$(record_value bytes "${records}")"
  error="$(record_value error "${records}")"
  log "image: status=${status} bytes=${bytes} durationMs=$(record_value durationMs "${records}") size=$(record_value width "${records}")x$(record_value height "${records}")"
  if [[ -n "${error}" ]]; then
    step_fail "6-image" "the job reported an error: ${error}"
    return 1
  fi
  if [[ "${png}" != "true" ]]; then
    step_fail "6-image" "the job finished with status '${status}' but the retrieved bytes are not a PNG (${bytes} bytes)."
    return 1
  fi
  return 0
}

assert_vram_released() {
  local baseline="$1" after="$2"
  local excess=$((after - baseline))
  log "eject: baselineVram=${baseline}MiB afterEject=${after}MiB excess=${excess}MiB (tolerance ${EJECT_TOLERANCE_MIB}MiB)"
  if [[ "${excess}" -gt "${EJECT_TOLERANCE_MIB}" ]]; then
    step_fail "7-eject" \
      "VRAM stayed ${excess}MiB above the baseline after eject (tolerance ${EJECT_TOLERANCE_MIB}MiB). A process is still holding device memory — this is the orphaned-llama-server signature."
    return 1
  fi
  return 0
}

# ---------------------------------------------------------------------------
# nvidia-smi sampling.
# ---------------------------------------------------------------------------
gpu_sample_once() {
  nvidia-smi --id="${GPU_INDEX}" --query-gpu=utilization.gpu,memory.used \
    --format=csv,noheader,nounits 2>/dev/null | head -n 1
}

gpu_vram_now() {
  local sample; sample="$(gpu_sample_once)"
  awk -F', *' 'NR==1 { printf "%d\n", $2+0 }' <<<"${sample}"
}

GPU_SAMPLER_PID=""
gpu_sampler_start() {
  : >"$1"
  (
    while :; do
      gpu_sample_once >>"$1"
      sleep "${SAMPLE_INTERVAL}"
    done
  ) &
  GPU_SAMPLER_PID=$!
}

gpu_sampler_stop() {
  if [[ -n "${GPU_SAMPLER_PID}" ]]; then
    kill "${GPU_SAMPLER_PID}" 2>/dev/null
    wait "${GPU_SAMPLER_PID}" 2>/dev/null
    GPU_SAMPLER_PID=""
  fi
}

# Emits "<peakUtil> <peakVram>". A sample file with no usable rows yields "0 0", which fails the
# step-4 floors rather than being mistaken for a pass.
gpu_sampler_peaks() {
  # A missing file must still yield "0 0": awk exits non-zero without running END, and an empty
  # result would leave the step-4 peaks unset rather than zero — i.e. a sampler that collected
  # nothing could read as "no evidence" instead of failing the floors.
  if [[ ! -f "$1" ]]; then
    printf '0 0\n'
    return 0
  fi
  awk -F', *' '
    $1 ~ /^[0-9]+$/ { if ($1+0 > u) u = $1+0; if ($2+0 > m) m = $2+0 }
    END { printf "%d %d\n", u+0, m+0 }
  ' "$1" 2>/dev/null
}

# Sourcing guard for scripts/tests/gpu-smoke.test.sh: define the functions above, run nothing.
# `return` succeeds when sourced and fails when executed, so the `exit` is the executed-directly
# path rather than dead code.
# shellcheck disable=SC2317
if [[ -n "${XE_GPU_SMOKE_LIB_ONLY:-}" ]]; then
  return 0 2>/dev/null || exit 0
fi

# ---------------------------------------------------------------------------
# From here down: the actual run.
# ---------------------------------------------------------------------------

# Serialize against any other build/test before anything is built. The wrapper closes the lock fd
# in this child so MSBuild daemons cannot inherit it (see with-build-lock.sh).
if [[ -z "${XE_BUILD_LOCK_HELD:-}" && -z "${NO_BUILD_LOCK:-}" ]]; then
  exec "${GPU_SMOKE_PROJECT_ROOT}/scripts/with-build-lock.sh" -- "${BASH_SOURCE[0]}" "$@"
fi

RUN_IMAGES="false"
RUN_TOOLS="true"
KEEP_RUNNING="false"
FORCED_MODEL=""

usage() { sed -n '2,/^set -uo/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//; $d'; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --images)       RUN_IMAGES="true"; shift ;;
    --no-tools)     RUN_TOOLS="false"; shift ;;
    --keep-running) KEEP_RUNNING="true"; shift ;;
    --model)        FORCED_MODEL="${2:-}"; [[ -n "${FORCED_MODEL}" ]] || prereq_fail "--model needs a value"; shift 2 ;;
    --help|-h)      usage; exit 0 ;;
    *)              echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

log "=== Preflight ==="
for tool in dotnet python3 aspire nvidia-smi awk; do
  command -v "${tool}" >/dev/null 2>&1 || prereq_fail "${tool} is not on PATH."
done
[[ -f "${DRIVER}" ]] || prereq_fail "driver not found at ${DRIVER}"

# A box with no NVIDIA GPU cannot produce any evidence this smoke needs. Refuse rather than
# "pass" a run whose central assertion is unmeasurable.
GPU_NAME="$(nvidia-smi --id="${GPU_INDEX}" --query-gpu=name --format=csv,noheader 2>/dev/null | head -n 1)"
[[ -n "${GPU_NAME}" ]] || prereq_fail \
  "nvidia-smi reported no GPU at index ${GPU_INDEX}. This smoke exists to prove the GPU is used;
             on a box without one there is nothing to prove and a pass would be meaningless."
log "gpu        ${GPU_NAME}"
log "dotnet     $(dotnet --version)"

# Refuse to start unless we can PROVE nothing is running — the same rule aspire-readiness-smoke.sh
# applies. Reusing someone else's instance would mean a meaningless VRAM baseline; guessing would
# mean a run whose result cannot be trusted either way.
if "${GPU_SMOKE_SCRIPT_DIR}/dev-status.sh" >/dev/null 2>&1; then
  echo "[gpu-smoke] An AppHost for this worktree is already running. Refusing to reuse or stop it:" >&2
  echo "[gpu-smoke] the VRAM baseline must be taken on a clean box or step 4/7 mean nothing." >&2
  echo "[gpu-smoke] Stop it first with scripts/dev-stop.sh." >&2
  exit 3
else
  status_result=$?
  if [[ "${status_result}" -ne 3 ]]; then
    echo "[gpu-smoke] Could not establish whether this AppHost is stopped; refusing to start one." >&2
    exit 4
  fi
fi

TEMP_ROOT="$(mktemp -d)"
chmod 700 "${TEMP_ROOT}"
SAMPLE_FILE="${TEMP_ROOT}/gpu-samples"
GUARD_STATE=""
CLEANUP_ARMED="false"
CLIENT_DEBUG_ROOT="${GPU_SMOKE_PROJECT_ROOT}/XE-Local-AI-Engine.Client/bin/Debug"

# Invoked indirectly by the EXIT trap immediately below.
# shellcheck disable=SC2329
cleanup() {
  local status=$?
  trap - EXIT
  gpu_sampler_stop
  if [[ "${CLEANUP_ARMED}" == "true" && "${KEEP_RUNNING}" != "true" ]]; then
    # NEVER `aspire stop` — it is a no-op on this stack and leaves an orphaned llama-server
    # holding a port and VRAM. dev-stop.sh is worktree-scoped.
    "${GPU_SMOKE_SCRIPT_DIR}/dev-stop.sh" >/dev/null 2>&1 || {
      echo "[gpu-smoke] Cleanup failed; an AppHost or llama-server may still hold VRAM." >&2
      [[ "${status}" -ne 0 ]] || status=1
    }
  elif [[ "${CLEANUP_ARMED}" == "true" ]]; then
    log "--keep-running: the AppHost is still up. Stop it with scripts/dev-stop.sh."
  fi
  if [[ -n "${GUARD_STATE}" ]]; then
    "${GPU_SMOKE_PROJECT_ROOT}/scripts/assembly-guard.sh" verify "${GUARD_STATE}" || status=75
  fi
  rm -rf -- "${TEMP_ROOT}"
  exit "${status}"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

# The baseline MUST be sampled before the AppHost exists: every VRAM assertion is a delta against
# it, and WSL2 offers no per-process attribution to subtract afterwards.
BASELINE_VRAM="$(gpu_vram_now)"
[[ "${BASELINE_VRAM}" =~ ^[0-9]+$ ]] || prereq_fail "could not read a VRAM baseline from nvidia-smi."
log "baseline VRAM ${BASELINE_VRAM}MiB (sampled before the AppHost starts)"

if [[ -z "${NO_GUARD:-}" && -d "${CLIENT_DEBUG_ROOT}" ]]; then
  GUARD_STATE="${TEMP_ROOT}/assembly-guard.state"
  "${GPU_SMOKE_PROJECT_ROOT}/scripts/assembly-guard.sh" snapshot "${GUARD_STATE}" --root "${CLIENT_DEBUG_ROOT}"
fi

log "=== Starting this worktree's AppHost ==="
CLEANUP_ARMED="true"
"${GPU_SMOKE_SCRIPT_DIR}/dev-start.sh" >/dev/null || {
  echo "[gpu-smoke] dev-start.sh failed. If a global instance lock is stale, remove" >&2
  echo "[gpu-smoke]   ~/.local/share/XE-Local-AI-Engine/instance.lock" >&2
  echo "[gpu-smoke] and re-run. Only one host may run at a time." >&2
  exit 1
}

timeout "$((READY_TIMEOUT_SECONDS + 15))s" aspire wait app \
  --apphost "${XE_ASPIRE_APPHOST:-${GPU_SMOKE_PROJECT_ROOT}/XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj}" \
  --status healthy --timeout "${READY_TIMEOUT_SECONDS}" --non-interactive --nologo >/dev/null || {
  echo "[gpu-smoke] the app resource did not become healthy within ${READY_TIMEOUT_SECONDS}s." >&2
  exit 1
}

# Discover the port. It changes on EVERY restart, so it is read from Aspire's own resource list
# and never hard-coded. Prefer https: the http endpoint 307-redirects to it.
BASE_URL="$(
  "${GPU_SMOKE_SCRIPT_DIR}/dev-status.sh" --json | python3 -c '
import json, sys
try:
    status = json.load(sys.stdin)
except json.JSONDecodeError:
    raise SystemExit(1)
for resource in status.get("resources", []):
    if resource.get("name") != "app":
        continue
    urls = [u.get("url", "") for u in resource.get("urls", [])]
    # Only the bare origin; /scalar, /openapi and /devui share the same host:port.
    origins = [u.rstrip("/") for u in urls if u.count("/") == 2]
    for scheme in ("https://", "http://"):
        for origin in origins:
            if origin.startswith(scheme):
                print(origin)
                raise SystemExit(0)
raise SystemExit(1)
'
)"
[[ -n "${BASE_URL}" ]] || { echo "[gpu-smoke] could not discover the app base URL from dev-status.sh --json." >&2; exit 1; }
log "base URL   ${BASE_URL}"

drive() { python3 "${DRIVER}" --base-url "${BASE_URL}" "$@"; }

# --- authentication ---------------------------------------------------------
# Every node-local route is Operator-policy gated. A fresh node has NO seeded operator: it reports
# setupRequired=true and a bare login 401s, which is why the driver runs first-run setup first.
log "=== Authenticating ==="
AUTH_RECORDS="$(drive auth --email "${SMOKE_EMAIL}" --password "${SMOKE_PASSWORD}")" || {
  echo "[gpu-smoke] could not obtain an operator token." >&2
  echo "[gpu-smoke] If this node was already onboarded by hand, its password is not the default;" >&2
  echo "[gpu-smoke] set XE_GPU_SMOKE_EMAIL / XE_GPU_SMOKE_PASSWORD to the real credentials." >&2
  exit 1
}
TOKEN="$(record_value token "${AUTH_RECORDS}")"
[[ -n "${TOKEN}" ]] || { echo "[gpu-smoke] auth produced no token." >&2; exit 1; }
log "auth       ok (first-run setup performed: $(record_value setupPerformed "${AUTH_RECORDS}"))"

# --- step 2 first: its verdict is an input to step 1 ------------------------
log "=== Step 2: device audit ==="
ledger_expect "2-device-audit"
AUDIT_RECORDS="$(drive audit --token "${TOKEN}")" || AUDIT_RECORDS=""
AUDIT_BACKEND="$(record_value inferenceBackend "${AUDIT_RECORDS}")"
if [[ -z "${AUDIT_RECORDS}" ]]; then
  step_fail "2-device-audit" "the hardware-profile request failed; no audit verdict could be read."
elif assert_device_audit "${AUDIT_RECORDS}"; then
  ledger_pass "2-device-audit"
fi

log "=== Step 1: runtime identity ==="
ledger_expect "1-runtime-identity"
RUNTIME_RECORDS="$(drive runtime --token "${TOKEN}")" || RUNTIME_RECORDS=""
if [[ -z "${RUNTIME_RECORDS}" ]]; then
  step_fail "1-runtime-identity" "the llamacpp/runtime request failed."
elif assert_runtime_identity "${RUNTIME_RECORDS}" "${AUDIT_BACKEND}"; then
  ledger_pass "1-runtime-identity"
fi

# --- step 3 + 4: one generation, two assertions ------------------------------
log "=== Step 3: model load + chat  /  Step 4: GPU actually used ==="
ledger_expect "3-chat"
ledger_expect "4-gpu-used"
MODEL_RECORDS="$(drive models --token "${TOKEN}")" || MODEL_RECORDS=""
CHAT_MODEL="$(pick_chat_model "${MODEL_RECORDS}" "${FORCED_MODEL}")" || CHAT_MODEL=""
if [[ -z "${CHAT_MODEL}" ]]; then
  step_fail "3-chat" "no installed llama.cpp chat model was found. Install a chat GGUF before running this smoke; a run with nothing to load is not a pass."
  step_fail "4-gpu-used" "skipped because no chat model was available."
else
  log "model      ${CHAT_MODEL}"
  # A deliberately long-running prompt. A 0.5B model on a fast GPU finishes a short answer inside
  # a single sampling interval, so the utilisation peak could be missed entirely and step 4 would
  # fail for a measurement reason rather than a real one.
  gpu_sampler_start "${SAMPLE_FILE}"
  CHAT_RECORDS="$(drive chat --token "${TOKEN}" --model "${CHAT_MODEL}" \
    --prompt 'Count from 1 to 60, writing each number as a word on its own line. Do not stop early.')" || CHAT_RECORDS=""
  gpu_sampler_stop
  LOADED_VRAM="$(gpu_vram_now)"
  read -r PEAK_UTIL PEAK_VRAM <<<"$(gpu_sampler_peaks "${SAMPLE_FILE}")"
  log "gpu: collected $(wc -l <"${SAMPLE_FILE}" 2>/dev/null || echo 0) samples at ${SAMPLE_INTERVAL}s"

  if [[ -z "${CHAT_RECORDS}" ]]; then
    step_fail "3-chat" "the chat stream could not be driven."
  elif assert_chat_reply "${CHAT_RECORDS}"; then
    ledger_pass "3-chat"
  fi

  if assert_gpu_was_used "${PEAK_UTIL:-0}" "${PEAK_VRAM:-0}" "${BASELINE_VRAM}" "${LOADED_VRAM:-0}"; then
    ledger_pass "4-gpu-used"
  fi
fi

# --- step 5: tool calling ----------------------------------------------------
log "=== Step 5: tool calling ==="
TOOL_RECORDS="$(drive tools --token "${TOKEN}")" || TOOL_RECORDS=""
# Reported rather than asserted: which tools the catalog actually offers is a product question,
# and printing it here settles it without this script taking a position on the answer.
log "tool catalog ($(record_value count "${TOOL_RECORDS}")): $(record_values tool "${TOOL_RECORDS}" | paste -sd, -)"

if [[ "${RUN_TOOLS}" != "true" ]]; then
  ledger_skip "5-tool-calling"
  warn "step 5 skipped by --no-tools. Tool calling was NOT verified."
elif [[ -z "${CHAT_MODEL}" ]]; then
  ledger_expect "5-tool-calling"
  step_fail "5-tool-calling" "skipped because no chat model was available."
else
  ledger_expect "5-tool-calling"
  if ! model_is_tool_capable "${MODEL_RECORDS}" "${CHAT_MODEL}"; then
    warn "the API reports '${CHAT_MODEL}' as NOT tool-capable; the turn below is expected to withhold tools."
  fi
  TOOLCALL_RECORDS="$(drive chat --token "${TOKEN}" --model "${CHAT_MODEL}" --tools \
    --title 'gpu-smoke-tools' \
    --prompt 'What is 17 multiplied by 23? Use the Calculate tool to work it out.')" || TOOLCALL_RECORDS=""
  if [[ -z "${TOOLCALL_RECORDS}" ]]; then
    step_fail "5-tool-calling" "the tool-calling turn could not be driven."
  elif assert_tool_call "${TOOLCALL_RECORDS}"; then
    ledger_pass "5-tool-calling"
  fi
fi

# --- step 6: image generation (opt-in) ---------------------------------------
if [[ "${RUN_IMAGES}" == "true" ]]; then
  log "=== Step 6: image generation ==="
  ledger_expect "6-image"
  IMAGE_MODEL_RECORDS="$(drive image-models --token "${TOKEN}")" || IMAGE_MODEL_RECORDS=""
  IMAGE_MODEL="$(record_values imageModel "${IMAGE_MODEL_RECORDS}" | head -n 1)"
  if [[ -z "${IMAGE_MODEL}" ]]; then
    step_fail "6-image" "--images was requested but no image model is installed. Download one first; a skipped generation is not a pass."
  else
    log "image model ${IMAGE_MODEL}"
    IMAGE_RECORDS="$(drive image --token "${TOKEN}" --model "${IMAGE_MODEL}")" || IMAGE_RECORDS=""
    if [[ -z "${IMAGE_RECORDS}" ]]; then
      step_fail "6-image" "the image job could not be driven."
    elif assert_image_result "${IMAGE_RECORDS}"; then
      ledger_pass "6-image"
    fi
  fi
else
  ledger_skip "6-image"
fi

# --- step 7: eject -----------------------------------------------------------
log "=== Step 7: eject releases VRAM ==="
ledger_expect "7-eject"
RUNNING_RECORDS="$(drive running --token "${TOKEN}")" || RUNNING_RECORDS=""
EJECTED_ANY="false"
while IFS= read -r entry; do
  [[ -n "${entry}" ]] || continue
  IFS='|' read -r running_model running_role <<<"${entry}"
  log "ejecting ${running_model} (role=${running_role:-none})"
  if drive eject --token "${TOKEN}" --model "${running_model}" --role "${running_role}" --force >/dev/null; then
    EJECTED_ANY="true"
  else
    warn "eject of ${running_model} reported an error"
  fi
done < <(record_values running "${RUNNING_RECORDS}")

if [[ "${RUN_IMAGES}" == "true" ]]; then
  drive eject-images --token "${TOKEN}" >/dev/null || warn "the image runtime eject reported an error"
fi

if [[ "${EJECTED_ANY}" != "true" && -n "${CHAT_MODEL}" ]]; then
  step_fail "7-eject" "no model was resident to eject, yet a chat turn had just run. Either the model was never loaded on the device or the running-model view is wrong."
else
  # Bounded settle: freeing device memory is not synchronous with the API returning.
  AFTER_VRAM="${BASELINE_VRAM}"
  for _ in $(seq 1 30); do
    AFTER_VRAM="$(gpu_vram_now)"
    [[ "${AFTER_VRAM}" =~ ^[0-9]+$ ]] || AFTER_VRAM=999999
    [[ $((AFTER_VRAM - BASELINE_VRAM)) -le "${EJECT_TOLERANCE_MIB}" ]] && break
    sleep 1
  done
  if assert_vram_released "${BASELINE_VRAM}" "${AFTER_VRAM}"; then
    ledger_pass "7-eject"
  fi
fi

# --- verdict -----------------------------------------------------------------
if ledger_finalize; then
  echo
  log "RESULT: PASS — every expected step ran and passed."
  exit 0
fi
echo
log "RESULT: FAIL — see the failures above. This is NOT a pass."
exit 1
