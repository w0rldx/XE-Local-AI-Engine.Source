#!/usr/bin/env bash
# run-gpu-smoke-local.sh — OPT-IN live GPU smoke for a real, locally started node.
#
# Why this exists
#   A one-off live AI evaluation cost most of its effort DISCOVERING a sequence, not running it.
#   Re-running it is cheap. This script preserves that sequence so the defects it found cannot
#   silently come back.
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
#   0   — every expected step ran AND passed
#   1   — the run was JUDGED and did not earn a pass: an assertion failed, or a step produced no
#         verdict. A `1` ALWAYS comes with a `=== Summary ===` block naming each step's verdict.
#   2   — a prerequisite is missing / usage error (nothing was run)
#   3   — an AppHost for this worktree is already running; refusing to reuse or stop it
#   4   — could not establish whether an AppHost is running; refusing to start one
#   5   — INFRASTRUCTURE: the run aborted before any step could be judged (the AppHost failed to
#         build, start, or become healthy, the base URL could not be discovered, or authentication
#         failed). No summary is printed. This is deliberately NOT 1.
#   75  — CONTAMINATED: the build output changed mid-run; the result is void, re-run it
#   130 — interrupted (Ctrl-C). The AppHost is still torn down; the result is incomplete, not red.
#
# Why 5 exists, since this is the script a pre-RC checklist keys on: "the GPU did not do the work"
# is a product defect that should block an RC, while "the AppHost never came up on this laptop" is
# local infrastructure that blocks nothing. Both used to exit 1, indistinguishable to any caller,
# with the only discriminator (whether a summary was printed) undocumented and unkeyable. A
# wrapper can now treat 1 as "product says no" and 5 as "fix your machine and re-run".
#
# 1 remains deliberately broad WITHIN judged runs: an assertion failure and a vacuous step both
# mean "this run did not earn a pass" and both need the same response — read the summary.
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
    ledger_contains "${step}" "${LEDGER_EXPECTED[@]}" && continue
    # An opt-IN step that was simply not requested (6-image) is different from a normally-required
    # step the operator explicitly switched OFF (5-tool-calling via --no-tools). Both are honest
    # zeroes, but the second means a feature this smoke normally guarantees went UNVERIFIED, and
    # the summary must not let that read as routine.
    if [[ "${step}" == "5-tool-calling" ]]; then
      echo "  SKIPPED    ${step}  <- DISABLED BY --no-tools; tool calling was NOT verified" >&2
    else
      echo "  SKIPPED    ${step}  (opt-in; not requested)"
    fi
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

# Coerce a sampled value to a plain integer, defaulting to 0.
#
# Bash arithmetic treats a non-numeric word as a VARIABLE NAME, so `$(( x - 1 ))` with x="[N/A]"
# aborts the whole script under `set -u` ("unbound variable") instead of producing a verdict. The
# failure is safe (non-zero, and the EXIT trap still tears the AppHost down) but it kills the run
# with a cryptic message and no ledger, which is the opposite of what this script is for.
# nvidia-smi can legitimately emit non-numeric fields such as "[N/A]" on a degraded driver.
# 0 is the right default: it fails every floor rather than passing one.
as_int() {
  local value="${1:-}"
  if [[ "${value}" =~ ^-?[0-9]+$ ]]; then
    printf '%s\n' "${value}"
  else
    printf '0\n'
  fi
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

# Step 3 helper. Picks the SMALLEST installed llama.cpp chat model — smallest by sizeBytes, not
# merely the first row, so the choice does not depend on the API's ordering and the smoke stays
# fast enough to actually get run. A 27B where a 0.5B would do also risks VRAM pressure that
# would distort step 4's measurement.
LOCAL_MODEL_PROVIDER="llamacpp"

pick_chat_model() {
  local records="$1" forced="${2:-}"
  local line name kind capable provider size
  local best_name="" best_size=""
  if [[ -n "${forced}" ]]; then
    printf '%s\n' "${forced}"
    return 0
  fi
  while IFS= read -r line; do
    [[ -n "${line}" ]] || continue
    IFS='|' read -r name kind capable provider size <<<"${line}"
    [[ "${kind}" == "Chat" ]] || continue
    # The provider filter is what keeps this smoke honest. `/models` merges Ollama and the cloud
    # providers into the same list — Ollama sorts FIRST — so without it a node with Ollama
    # reachable would run the chat turn on Ollama while steps 1-2 audited llama.cpp, and steps 3
    # and 4 could both pass without the runtime under test ever being exercised.
    [[ "${provider}" == "${LOCAL_MODEL_PROVIDER}" ]] || continue
    size="$(as_int "${size:-0}")"
    # A missing/zero size must not win the comparison and silently become "smallest".
    if [[ -z "${best_name}" ]] || { [[ "${size}" -gt 0 ]] && { [[ "${best_size}" -eq 0 ]] || [[ "${size}" -lt "${best_size}" ]]; }; }; then
      best_name="${name}"
      best_size="${size}"
    fi
  done < <(record_values model "${records}")
  [[ -n "${best_name}" ]] || return 1
  printf '%s\n' "${best_name}"
}

model_is_tool_capable() {
  local records="$1" wanted="$2" line name kind capable provider
  while IFS= read -r line; do
    IFS='|' read -r name kind capable provider <<<"${line}"
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
#
# The VRAM half is the DISCRIMINATING one; the utilisation half is the noisier one. Measured on
# this WSL2 box: GPU 64-72% / +1199-1211 MiB versus CPU-fallback 11-14% / +0 MiB. A desktop
# compositor idling on the same GPU can drift utilisation over a 15% floor on its own, and one
# CPU-fallback run did exactly that — while the VRAM delta stayed at a flat 0 MiB and still
# failed the step. Because BOTH must pass, utilisation noise can only ever cause a false
# FAILURE, never a false pass, which is the safe direction for this script to be wrong in.
# Raise XE_GPU_SMOKE_MIN_UTIL_PERCENT on a busy desktop rather than lowering it.
assert_gpu_was_used() {
  local peak_util baseline_vram loaded_vram peak_vram util_samples
  peak_util="$(as_int "$1")"
  peak_vram="$(as_int "$2")"
  baseline_vram="$(as_int "$3")"
  loaded_vram="$(as_int "$4")"
  # Absent (older call sites / tests) means "assume utilisation was measurable".
  util_samples="$(as_int "${5:-1}")"
  local rise=$((loaded_vram - baseline_vram))
  log "gpu: peakUtil=${peak_util}% baselineVram=${baseline_vram}MiB peakVram=${peak_vram}MiB loadedVram=${loaded_vram}MiB rise=${rise}MiB utilSamples=${util_samples}"
  local ok=0
  if [[ "${util_samples}" -eq 0 ]]; then
    # NOT the same as "the GPU did nothing" — nvidia-smi never gave us a usable number (it can
    # report utilisation as "[N/A]"). Still a failure, because an unmeasurable run is not a pass,
    # but say what actually happened so nobody debugs a GPU fault that does not exist.
    step_fail "4-gpu-used" \
      "GPU utilisation was UNMEASURABLE — nvidia-smi returned no numeric utilisation sample during generation (it can report '[N/A]'). This is not evidence the GPU was idle; it is an absence of evidence, which is not a pass. Check 'nvidia-smi --query-gpu=utilization.gpu --format=csv' on this host."
    ok=1
  elif [[ "${peak_util}" -lt "${MIN_UTIL_PERCENT}" ]]; then
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

# NOTE the asymmetry with assert_gpu_was_used: that one compares LOWER bounds, so coercing an
# unreadable value to 0 fails safe. This one compares an UPPER bound, where 0 would PASS. So a
# non-numeric reading is rejected outright instead of coerced — the safe default for a
# "did it come back down?" check is "I could not tell, therefore no".
assert_vram_released() {
  local baseline="$1" after="$2"
  if [[ ! "${baseline}" =~ ^-?[0-9]+$ || ! "${after}" =~ ^-?[0-9]+$ ]]; then
    step_fail "7-eject" "VRAM readings were not numeric (baseline='${baseline}', after='${after}'); the release could not be verified."
    return 1
  fi
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

# Prints the used-VRAM reading in MiB, or exits NON-ZERO when nvidia-smi produced no usable
# number. The distinction is load-bearing and was a real fail-open: `$2+0` turns a failed read
# into a perfectly valid-looking `0`, and 0 satisfies `^[0-9]+$`, so every downstream guard
# accepted it. Step 7 compares an UPPER bound (`after - baseline <= tolerance`), so a phantom 0
# read as "all VRAM was released" and passed the step while an orphaned llama-server was still
# holding the device. Callers must therefore check the status, never just the string.
gpu_vram_now() {
  local sample; sample="$(gpu_sample_once)"
  awk -F', *' '
    NR==1 && $2 ~ /^[0-9]+$/ { print $2+0; found=1 }
    END { exit !found }
  ' <<<"${sample}"
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

# Emits "<peakUtil> <peakVram> <utilSamples> <vramSamples>".
#
# The two fields are filtered INDEPENDENTLY. nvidia-smi can report one of them as "[N/A]" while
# the other is perfectly good — under WSL2 that is a real possibility — and gating the memory
# field on the utilisation field being numeric threw away valid VRAM samples, printing
# `peakVram=0MiB` for a GPU genuinely holding gigabytes.
#
# The sample COUNTS are what let step 4 distinguish "utilisation was 0" (the GPU did nothing)
# from "utilisation was unmeasurable" (nvidia-smi told us nothing). Both fail the step — an
# unmeasurable run is never a pass — but they are different problems and must not share a
# diagnosis that sends someone hunting a GPU fault that does not exist.
gpu_sampler_peaks() {
  # A missing file must still yield zeros: awk exits non-zero without running END, and an empty
  # result would leave the step-4 peaks unset rather than zero — i.e. a sampler that collected
  # nothing could read as "no evidence" instead of failing the floors.
  if [[ ! -f "$1" ]]; then
    printf '0 0 0 0\n'
    return 0
  fi
  awk -F', *' '
    $1 ~ /^[0-9]+$/ { if ($1+0 > u) u = $1+0; un++ }
    $2 ~ /^[0-9]+$/ { if ($2+0 > m) m = $2+0; mn++ }
    END { printf "%d %d %d %d\n", u+0, m+0, un+0, mn+0 }
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

# The thresholds ARE the assertion. Validate them (a non-numeric knob would otherwise abort the
# run mid-comparison under `set -u`) and ECHO them, so a run weakened by
# XE_GPU_SMOKE_MIN_UTIL_PERCENT=0 cannot print an unqualified "PASS" with nothing on the record
# saying the floor was zero.
for knob in MIN_UTIL_PERCENT MIN_VRAM_RISE_MIB EJECT_TOLERANCE_MIB READY_TIMEOUT_SECONDS GPU_INDEX; do
  [[ "${!knob}" =~ ^[0-9]+$ ]] || prereq_fail "${knob} must be a non-negative integer, got '${!knob}'."
done
[[ "${SAMPLE_INTERVAL}" =~ ^[0-9]+(\.[0-9]+)?$ ]] || prereq_fail "XE_GPU_SMOKE_SAMPLE_INTERVAL must be numeric, got '${SAMPLE_INTERVAL}'."
log "thresholds minUtil=${MIN_UTIL_PERCENT}% minVramRise=${MIN_VRAM_RISE_MIB}MiB ejectTolerance=${EJECT_TOLERANCE_MIB}MiB"
if [[ "${MIN_UTIL_PERCENT}" -eq 0 || "${MIN_VRAM_RISE_MIB}" -eq 0 ]]; then
  warn "a step-4 floor is set to 0 — the load-bearing GPU assertion is DISABLED for this run."
fi

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
ASPIRE_APPHOST="${XE_ASPIRE_APPHOST:-${GPU_SMOKE_PROJECT_ROOT}/XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj}"

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
    # Contamination voids a pass or an assertion failure alike — both describe assemblies that
    # changed underneath the run. But it must NOT overwrite an interrupt (130) or a
    # prerequisite/lifecycle status: "re-run it" would send the operator past a Ctrl-C or past a
    # cleanup that failed to kill an orphaned llama-server, which needs different action.
    if ! "${GPU_SMOKE_PROJECT_ROOT}/scripts/assembly-guard.sh" verify "${GUARD_STATE}"; then
      case "${status}" in
        0|1) status=75 ;;
        *)   echo "[gpu-smoke] NOTE: the build output also changed during the run, but exit ${status} is the more actionable failure." >&2 ;;
      esac
    fi
  fi
  rm -rf -- "${TEMP_ROOT}"
  exit "${status}"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

# Aspire's normal start path builds the AppHost. That legitimate build must finish before the
# assembly snapshot or the smoke contaminates its own result when the guarded Debug outputs move.
log "=== Building this worktree's AppHost ==="
dotnet build "${ASPIRE_APPHOST}" --configuration Debug --nologo || {
  echo "[gpu-smoke] the AppHost Debug build failed: ${ASPIRE_APPHOST}" >&2
  echo "[gpu-smoke] Fix the build failure and re-run; no GPU behavior was judged." >&2
  exit 5
}

# The baseline MUST be sampled before the AppHost exists: every VRAM assertion is a delta against
# it, and WSL2 offers no per-process attribution to subtract afterwards.
BASELINE_VRAM="$(gpu_vram_now)" || prereq_fail \
  "nvidia-smi produced no usable VRAM reading, so no baseline exists. Every VRAM assertion in this
             smoke is a delta against it; without one, steps 4 and 7 would compare against a phantom
             zero and pass regardless of what the GPU did."
log "baseline VRAM ${BASELINE_VRAM}MiB (sampled before the AppHost starts)"

if [[ -z "${NO_GUARD:-}" && -d "${CLIENT_DEBUG_ROOT}" ]]; then
  GUARD_STATE="${TEMP_ROOT}/assembly-guard.state"
  "${GPU_SMOKE_PROJECT_ROOT}/scripts/assembly-guard.sh" snapshot "${GUARD_STATE}" --root "${CLIENT_DEBUG_ROOT}"
fi

log "=== Starting this worktree's AppHost ==="
CLEANUP_ARMED="true"
"${GPU_SMOKE_SCRIPT_DIR}/dev-start.sh" --no-build >/dev/null || {
  echo "[gpu-smoke] dev-start.sh failed. If a global instance lock is stale, remove" >&2
  echo "[gpu-smoke]   ~/.local/share/XE-Local-AI-Engine/instance.lock" >&2
  echo "[gpu-smoke] and re-run. Only one host may run at a time." >&2
  exit 5
}

timeout "$((READY_TIMEOUT_SECONDS + 15))s" aspire wait app \
  --apphost "${ASPIRE_APPHOST}" \
  --status healthy --timeout "${READY_TIMEOUT_SECONDS}" --non-interactive --nologo >/dev/null || {
  echo "[gpu-smoke] the app resource did not become healthy within ${READY_TIMEOUT_SECONDS}s." >&2
  exit 5
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
    # Only the bare origin; /scalar and /openapi share the same host:port.
    origins = [u.rstrip("/") for u in urls if u.count("/") == 2]
    for scheme in ("https://", "http://"):
        for origin in origins:
            if origin.startswith(scheme):
                print(origin)
                raise SystemExit(0)
raise SystemExit(1)
'
)"
[[ -n "${BASE_URL}" ]] || { echo "[gpu-smoke] could not discover the app base URL from dev-status.sh --json." >&2; exit 5; }
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
  exit 5
}
TOKEN="$(record_value token "${AUTH_RECORDS}")"
[[ -n "${TOKEN}" ]] || { echo "[gpu-smoke] auth produced no token." >&2; exit 5; }
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
  # A failed read here is fail-CLOSED (0 makes the rise negative, below the floor), but say so
  # explicitly rather than letting a phantom zero masquerade as a measurement.
  LOADED_VRAM="$(gpu_vram_now)" || { warn "no VRAM reading after generation; step 4 will fail on an unmeasurable value."; LOADED_VRAM=0; }
  read -r PEAK_UTIL PEAK_VRAM UTIL_SAMPLES VRAM_SAMPLES <<<"$(gpu_sampler_peaks "${SAMPLE_FILE}")"
  log "gpu: collected $(wc -l <"${SAMPLE_FILE}" 2>/dev/null || echo 0) rows at ${SAMPLE_INTERVAL}s (${UTIL_SAMPLES:-0} usable util, ${VRAM_SAMPLES:-0} usable vram)"

  if [[ -z "${CHAT_RECORDS}" ]]; then
    step_fail "3-chat" "the chat stream could not be driven."
  elif assert_chat_reply "${CHAT_RECORDS}"; then
    ledger_pass "3-chat"
  fi

  if assert_gpu_was_used "${PEAK_UTIL:-0}" "${PEAK_VRAM:-0}" "${BASELINE_VRAM}" "${LOADED_VRAM:-0}" "${UTIL_SAMPLES:-0}"; then
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
  # HTTP 200 is not success here: the endpoint reports an `outcome`, and `timed_out_still_busy`
  # means the process was LEFT RUNNING. Treating the status code as the verdict would count a
  # refused eject as a completed one.
  EJECT_RECORDS="$(drive eject --token "${TOKEN}" --model "${running_model}" --role "${running_role}" --force)" || EJECT_RECORDS=""
  EJECT_OUTCOME="$(record_value outcome "${EJECT_RECORDS}")"
  if [[ -z "${EJECT_RECORDS}" ]]; then
    warn "eject of ${running_model} reported an error"
  elif [[ "${EJECT_OUTCOME}" == *"still_busy"* || "${EJECT_OUTCOME}" == *"timed_out"* ]]; then
    warn "eject of ${running_model} returned outcome '${EJECT_OUTCOME}' — the process was left running."
  else
    log "  outcome=${EJECT_OUTCOME}"
    EJECTED_ANY="true"
  fi
done < <(record_values running "${RUNNING_RECORDS}")

if [[ "${RUN_IMAGES}" == "true" ]]; then
  drive eject-images --token "${TOKEN}" >/dev/null || warn "the image runtime eject reported an error"
fi

if [[ -z "${CHAT_MODEL}" ]]; then
  # Nothing was ever loaded, so there is nothing to prove was released. Verifying a VRAM delta
  # here would assert only that an idle box stayed idle — a PASS that means nothing.
  step_fail "7-eject" "no chat model was loaded, so VRAM release could not be exercised at all."
elif [[ "${EJECTED_ANY}" != "true" ]]; then
  step_fail "7-eject" "no model was resident to eject, yet a chat turn had just run. Either the model was never loaded on the device or the running-model view is wrong."
else
  # Bounded settle: freeing device memory is not synchronous with the API returning.
  #
  # An unreadable sample must NOT end the loop. Step 7's comparison is an UPPER bound, so any
  # phantom-low value satisfies it — that is how a failed nvidia-smi read used to certify "VRAM
  # released" while a process still held the device. An unreadable sample is therefore treated as
  # "not settled yet", and if it never becomes readable the step fails on an unmeasurable value.
  AFTER_VRAM=""
  VRAM_READABLE="false"
  for _ in $(seq 1 30); do
    if AFTER_VRAM="$(gpu_vram_now)"; then
      VRAM_READABLE="true"
      [[ $((AFTER_VRAM - BASELINE_VRAM)) -le "${EJECT_TOLERANCE_MIB}" ]] && break
    else
      VRAM_READABLE="false"
    fi
    sleep 1
  done
  if [[ "${VRAM_READABLE}" != "true" ]]; then
    step_fail "7-eject" "nvidia-smi produced no usable VRAM reading after eject, so the release could not be verified. An unmeasurable result is not a pass."
  elif assert_vram_released "${BASELINE_VRAM}" "${AFTER_VRAM}"; then
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
