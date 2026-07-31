#!/usr/bin/env bash
#
# Behavioural tests for scripts/run-gpu-smoke-local.sh.
#
# The point of the GPU smoke is that it REFUSES to report a vacuous pass. A smoke script whose
# failure paths are untested is exactly the kind of thing that reports a green nobody earned, so
# this suite drives those paths directly — and needs no GPU, no models and no running node.
#
# Two layers:
#   1. The script is sourced with XE_GPU_SMOKE_LIB_ONLY=1, which defines its assertion functions
#      and runs nothing. Each assertion is then fed synthetic driver records and checked for the
#      right verdict. This is where the "never let nothing look green" rules are proven.
#   2. End-to-end runs with fakes on PATH (nvidia-smi, aspire) prove the preflight and
#      refuse-to-start gates really exit non-zero before anything is launched.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd -P)"
SMOKE="${SCRIPT_DIR}/run-gpu-smoke-local.sh"
TEMP_ROOT="$(mktemp -d)"
trap 'rm -rf -- "${TEMP_ROOT}"' EXIT

FAILED=0
CHECKS=0

check() {
  local label="$1" expected="$2" actual="$3"
  CHECKS=$((CHECKS + 1))
  if [[ "${expected}" == "${actual}" ]]; then
    return 0
  fi
  echo "  FAIL: ${label}: expected '${expected}', got '${actual}'" >&2
  FAILED=$((FAILED + 1))
}

check_contains() {
  local label="$1" needle="$2" haystack="$3"
  CHECKS=$((CHECKS + 1))
  if [[ "${haystack}" == *"${needle}"* ]]; then
    return 0
  fi
  echo "  FAIL: ${label}: output did not contain '${needle}'" >&2
  echo "        got: ${haystack}" >&2
  FAILED=$((FAILED + 1))
}

# A tab-separated driver record block, written the way the driver emits it.
records() { printf '%b' "$*"; }

# ---------------------------------------------------------------------------
# Layer 1 — assertion functions, sourced without running anything.
# ---------------------------------------------------------------------------
# Deliberately NOT followed by the linter: run-gpu-smoke-local.sh ends in a top-level `exit`, and
# following it here makes shellcheck believe every line below is unreachable (it cannot evaluate
# the XE_GPU_SMOKE_LIB_ONLY guard that returns first). The script is linted as its own target.
# shellcheck source=/dev/null
XE_GPU_SMOKE_LIB_ONLY=1 source "${SMOKE}"

echo "== device audit =="

# The headline case: a GPU runtime that silently runs on the CPU must FAIL, and must surface the
# node's own reason/remediation rather than a bare "failed".
audit_fallback="$(records 'inferenceBackend\tcpu\ngpuExpected\ttrue\ncpuFallback\ttrue\ncpuFallbackReason\tno Vulkan ICD\ncpuFallbackRemediation\tbuild CUDA from source\n')"
out="$(assert_device_audit "${audit_fallback}" 2>&1)"; status=$?
check "cpuFallback=true fails" "1" "${status}"
check_contains "cpuFallback failure names CPU FALLBACK" "CPU FALLBACK" "${out}"
check_contains "cpuFallback failure surfaces the reason" "no Vulkan ICD" "${out}"
check_contains "cpuFallback failure surfaces the remediation" "build CUDA from source" "${out}"

audit_cpu="$(records 'inferenceBackend\tcpu\ngpuExpected\ttrue\ncpuFallback\tfalse\ncpuFallbackReason\t\ncpuFallbackRemediation\t\n')"
assert_device_audit "${audit_cpu}" >/dev/null 2>&1
check "inferenceBackend=cpu fails" "1" "$?"

# An indeterminate probe must never read as a pass — "unknown" is not "no GPU" and it is not "GPU".
audit_unknown="$(records 'inferenceBackend\tunknown\ngpuExpected\ttrue\ncpuFallback\tfalse\n')"
out="$(assert_device_audit "${audit_unknown}" 2>&1)"; status=$?
check "inferenceBackend=unknown fails" "1" "${status}"
check_contains "unknown probe is called out as not-a-pass" "not a pass" "${out}"

# An empty record block means the request failed; it must not fall through to a pass.
assert_device_audit "" >/dev/null 2>&1
check "empty audit records fail" "1" "$?"

audit_gpu_unexpected="$(records 'inferenceBackend\tcuda\ngpuExpected\tfalse\ncpuFallback\tfalse\n')"
assert_device_audit "${audit_gpu_unexpected}" >/dev/null 2>&1
check "gpuExpected=false fails" "1" "$?"

audit_ok="$(records 'inferenceBackend\tcuda\ngpuExpected\ttrue\ncpuFallback\tfalse\ngpuVendor\tnvidia\n')"
assert_device_audit "${audit_ok}" >/dev/null 2>&1
check "cuda + gpuExpected + no fallback passes" "0" "$?"

audit_vulkan_ok="$(records 'inferenceBackend\tvulkan\ngpuExpected\ttrue\ncpuFallback\tfalse\n')"
assert_device_audit "${audit_vulkan_ok}" >/dev/null 2>&1
check "a working vulkan backend passes" "0" "$?"

echo "== runtime identity =="

# `installed` is nullable and null does NOT mean "no runtime" — a fresh node running the
# pinned-floor binary reports null while working perfectly. Failing here would be a false red.
runtime_absent="$(records 'installed\tfalse\n')"
assert_runtime_identity "${runtime_absent}" "cuda" >/dev/null 2>&1
check "absent install record does not fail" "0" "$?"

# A CPU-only variant on a GPU box is fatal...
runtime_cpu="$(records 'installed\ttrue\ntag\tb9692\nvariant\tcpu\n')"
out="$(assert_runtime_identity "${runtime_cpu}" "cpu" 2>&1)"; status=$?
check "cpu variant with cpu backend fails" "1" "${status}"
check_contains "cpu variant failure is actionable" "XE_LLAMACPP_SERVER_PATH" "${out}"

# ...unless an override is genuinely supplying a GPU binary, which the audit can see and the
# installed record cannot. This is the bring-your-own-runtime case.
assert_runtime_identity "${runtime_cpu}" "cuda" >/dev/null 2>&1
check "cpu variant + cuda backend does not fail" "0" "$?"

runtime_no_tag="$(records 'installed\ttrue\nvariant\tvulkan\n')"
assert_runtime_identity "${runtime_no_tag}" "cuda" >/dev/null 2>&1
check "install record without a tag fails" "1" "$?"

runtime_vulkan="$(records 'installed\ttrue\ntag\tb9692\nvariant\tvulkan\nisSourceBuild\tfalse\n')"
assert_runtime_identity "${runtime_vulkan}" "cuda" >/dev/null 2>&1
check "vulkan variant passes" "0" "$?"

echo "== chat =="

assert_chat_reply "$(records 'contentLength\t0\nerror\t\nevents\tassistant-completed:1\n')" >/dev/null 2>&1
check "empty content fails" "1" "$?"

out="$(assert_chat_reply "$(records 'contentLength\t0\nerror\t\nevents\t\n')" 2>&1)"
check_contains "empty completed turn is called out" "not a pass" "${out}"

assert_chat_reply "$(records 'contentLength\t12\nerror\tmodel exploded\nevents\t\n')" >/dev/null 2>&1
check "an errored turn fails even with content" "1" "$?"

assert_chat_reply "$(records 'contentLength\t\nerror\t\nevents\t\n')" >/dev/null 2>&1
check "missing contentLength fails" "1" "$?"

assert_chat_reply "$(records 'contentLength\t42\nerror\t\ncontent\thi\nevents\tassistant-completed:1\n')" >/dev/null 2>&1
check "non-empty content passes" "0" "$?"

echo "== gpu actually used =="

# THE load-bearing assertion. Both halves must be able to fail independently.
# Read by assert_gpu_was_used, which lives in the sourced script the linter is told not to follow.
# shellcheck disable=SC2034
MIN_UTIL_PERCENT=15
# shellcheck disable=SC2034
MIN_VRAM_RISE_MIB=150

out="$(assert_gpu_was_used 3 3400 3300 3310 2>&1)"; status=$?
check "low utilisation fails" "1" "${status}"
check_contains "low utilisation names the CPU-fallback signature" "silent-CPU-fallback signature" "${out}"

out="$(assert_gpu_was_used 90 3400 3300 3310 2>&1)"; status=$?
check "high utilisation but no VRAM rise fails" "1" "${status}"
check_contains "no VRAM rise is explained" "No model weights appear to be resident" "${out}"

assert_gpu_was_used 0 0 3300 0 >/dev/null 2>&1
check "all-zero samples fail (no samples collected)" "1" "$?"

# nvidia-smi can emit non-numeric fields such as "[N/A]" on a degraded driver. Bash arithmetic
# would treat those as VARIABLE NAMES and abort the whole run under `set -u`, killing the ledger
# instead of producing a verdict. They must coerce to 0 and fail the floors.
check "as_int passes integers through" "4552" "$(as_int 4552)"
check "as_int coerces junk to 0" "0" "$(as_int '[N/A]')"
check "as_int coerces empty to 0" "0" "$(as_int '')"

assert_gpu_was_used "[N/A]" "[N/A]" 3300 "[N/A]" >/dev/null 2>&1
check "non-numeric samples fail rather than aborting" "1" "$?"

assert_gpu_was_used "" "" 3300 "" >/dev/null 2>&1
check "empty samples fail rather than aborting" "1" "$?"

assert_gpu_was_used 72 4552 3353 4552 >/dev/null 2>&1
check "real GPU numbers pass" "0" "$?"

echo "== tool calling =="

# Asserting configuration would have passed while the feature was broken; only the turn's own
# tool-call events count.
out="$(assert_tool_call "$(records 'toolsRequested\t\ntoolsCompleted\t\n')" 2>&1)"; status=$?
check "no tool offered fails" "1" "${status}"
check_contains "no-tool failure mentions the restart gate" "restarts" "${out}"

assert_tool_call "$(records 'toolsRequested\tCalculate\ntoolsCompleted\t\n')" >/dev/null 2>&1
check "requested but never completed fails" "1" "$?"

assert_tool_call "$(records 'toolsRequested\tCalculate\ntoolsCompleted\tCalculate\n')" >/dev/null 2>&1
check "requested and completed passes" "0" "$?"

echo "== image =="

assert_image_result "$(records 'status\tSucceeded\npng\tfalse\nbytes\t0\nerror\t\n')" >/dev/null 2>&1
check "succeeded but no PNG bytes fails" "1" "$?"

assert_image_result "$(records 'status\tFailed\npng\tfalse\nbytes\t0\nerror\tsd-server died\n')" >/dev/null 2>&1
check "a failed job fails" "1" "$?"

assert_image_result "$(records 'status\tSucceeded\npng\ttrue\nbytes\t78102\nerror\t\n')" >/dev/null 2>&1
check "a real PNG passes" "0" "$?"

echo "== eject =="

# Read by assert_vram_released in the sourced script.
# shellcheck disable=SC2034
EJECT_TOLERANCE_MIB=600
out="$(assert_vram_released 3300 9000 2>&1)"; status=$?
check "VRAM still held after eject fails" "1" "${status}"
check_contains "held VRAM names the orphan signature" "orphaned-llama-server signature" "${out}"

assert_vram_released 3353 3385 >/dev/null 2>&1
check "VRAM returned to baseline passes" "0" "$?"

# Same coercion trap as step 4: junk must not abort the run mid-summary.
assert_vram_released 3353 "[N/A]" >/dev/null 2>&1
check "non-numeric post-eject reading does not abort" "0" "$?"

echo "== model selection =="

# Picking a cloud model would produce a green chat step that never touched the GPU, so only
# kind=Chat rows are eligible and an empty list must fail rather than silently pick nothing.
pick_chat_model "$(records 'count\t0\n')" "" >/dev/null 2>&1
check "no models fails model selection" "1" "$?"

embedding_only="$(records 'model\tnomic-embed|Embedding|false\nmodel\tdraft-model|Draft|false\n')"
pick_chat_model "${embedding_only}" "" >/dev/null 2>&1
check "embedding/draft-only list fails model selection" "1" "$?"

mixed="$(records 'model\tnomic-embed|Embedding|false\nmodel\tqwen-chat|Chat|true\n')"
check "picks the chat model" "qwen-chat" "$(pick_chat_model "${mixed}" "")"
check "an explicit --model wins" "forced-model" "$(pick_chat_model "${mixed}" "forced-model")"

model_is_tool_capable "${mixed}" "qwen-chat"
check "tool-capable model detected" "0" "$?"
model_is_tool_capable "${mixed}" "nomic-embed"
check "non-tool-capable model detected" "1" "$?"

echo "== sample-file peak extraction =="

printf '5, 3300\n72, 4552\n11, 3400\n' >"${TEMP_ROOT}/samples"
check "peaks are the maxima" "72 4552" "$(gpu_sampler_peaks "${TEMP_ROOT}/samples")"
: >"${TEMP_ROOT}/empty-samples"
# An empty sample file must yield zeros, which then FAIL the step-4 floors. A sampler that
# collected nothing must never be mistaken for a GPU that did nothing wrong.
check "an empty sample file yields zeros" "0 0" "$(gpu_sampler_peaks "${TEMP_ROOT}/empty-samples")"
check "a missing sample file yields zeros" "0 0" "$(gpu_sampler_peaks "${TEMP_ROOT}/does-not-exist")"

echo "== ledger (the anti-vacuous-pass gate) =="

# All five arrays are the sourced ledger's own state.
# shellcheck disable=SC2034
ledger_reset() { LEDGER_EXPECTED=(); LEDGER_PASSED=(); LEDGER_SKIPPED=(); LEDGER_FAILED=(); FAILURES=(); }

# Nothing ran at all — the vacuous run. This is the single most important case here.
ledger_reset
out="$(ledger_finalize 2>&1)"; status=$?
check "an empty ledger fails" "1" "${status}"
check_contains "empty ledger says nothing ran" "nothing ran" "${out}"

# A step that was expected but recorded no verdict must fail, not be quietly dropped.
ledger_reset
ledger_expect "a"; ledger_expect "b"; ledger_pass "a" >/dev/null
out="$(ledger_finalize 2>&1)"; status=$?
check "a step with no verdict fails the run" "1" "${status}"
check_contains "no-verdict step is reported as such" "NO VERDICT b" "${out}"

# A step that ran and FAILED must be reported as FAILED, not as "no verdict" — they mean
# different things to whoever reads the summary.
ledger_reset
ledger_expect "a"; step_fail "a" "boom" >/dev/null 2>&1
out="$(ledger_finalize 2>&1)"; status=$?
check "a failed step fails the run" "1" "${status}"
check_contains "failed step is labelled FAILED" "FAILED     a" "${out}"
check_contains "failed step keeps its message" "a: boom" "${out}"

# Skipping an expected step is not a pass.
ledger_reset
ledger_expect "a"; ledger_skip "a"
out="$(ledger_finalize 2>&1)"; status=$?
check "skipping an expected step fails" "1" "${status}"
check_contains "skip is called out as not a pass" "NOT a pass" "${out}"

# The only green path: every expected step passed.
ledger_reset
ledger_expect "a"; ledger_expect "b"; ledger_pass "a" >/dev/null; ledger_pass "b" >/dev/null
ledger_finalize >/dev/null 2>&1
check "all expected steps passed => pass" "0" "$?"

# A step skipped by flags (never expected) must not fail the run.
ledger_reset
ledger_expect "a"; ledger_pass "a" >/dev/null; ledger_skip "6-image"
ledger_finalize >/dev/null 2>&1
check "an unexpected-but-skipped step does not fail" "0" "$?"

# ---------------------------------------------------------------------------
# Layer 2 — end-to-end preflight gates, with fakes on PATH.
# ---------------------------------------------------------------------------
echo "== preflight gates (end to end) =="

mkdir -p "${TEMP_ROOT}/bin"

# A box with no GPU: the central assertion is unmeasurable, so the run must refuse (exit 2)
# rather than "pass" by never testing anything.
cat >"${TEMP_ROOT}/bin/nvidia-smi" <<'FAKE_NO_GPU'
#!/usr/bin/env bash
exit 1
FAKE_NO_GPU
chmod 700 "${TEMP_ROOT}/bin/nvidia-smi"

out="$(PATH="${TEMP_ROOT}/bin:${PATH}" NO_BUILD_LOCK=1 "${SMOKE}" 2>&1)"; status=$?
check "no GPU => exit 2" "2" "${status}"
check_contains "no-GPU message explains why a pass would be meaningless" "nothing to prove" "${out}"

# From here on the GPU exists, so the instance-state gates are what is under test.
cat >"${TEMP_ROOT}/bin/nvidia-smi" <<'FAKE_GPU'
#!/usr/bin/env bash
for arg in "$@"; do
  case "${arg}" in
    --query-gpu=name) printf 'FakeGPU 9000\n'; exit 0 ;;
  esac
done
printf '7, 3300\n'
FAKE_GPU
chmod 700 "${TEMP_ROOT}/bin/nvidia-smi"

# `aspire ps` reporting THIS worktree's apphost as running => dev-status exits 0 => the smoke
# must refuse to reuse or stop it (exit 3), because the VRAM baseline would be meaningless.
APPHOST="${PROJECT_ROOT}/XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj"
cat >"${TEMP_ROOT}/bin/aspire" <<FAKE_ASPIRE_RUNNING
#!/usr/bin/env bash
if [[ "\$1" == "ps" ]]; then
  printf '[{"appHostPath":"%s","appHostPid":424242,"status":"running","dashboardUrl":"https://localhost:1/login?t=x"}]\n' "${APPHOST}"
  exit 0
fi
if [[ "\$1" == "describe" ]]; then
  printf '{"resources":[{"displayName":"app","resourceType":"Project","state":"Running","healthStatus":"Healthy","urls":[]}]}\n'
  exit 0
fi
exit 0
FAKE_ASPIRE_RUNNING
chmod 700 "${TEMP_ROOT}/bin/aspire"

out="$(PATH="${TEMP_ROOT}/bin:${PATH}" NO_BUILD_LOCK=1 XE_ASPIRE_APPHOST="${APPHOST}" "${SMOKE}" 2>&1)"; status=$?
check "an already-running instance => exit 3" "3" "${status}"
check_contains "refusal explains the baseline requirement" "clean box" "${out}"

# `aspire ps` emitting garbage => dev-status cannot tell (exit 4) => the smoke must refuse to
# start rather than guess. Ambiguity must never resolve to green.
cat >"${TEMP_ROOT}/bin/aspire" <<'FAKE_ASPIRE_BROKEN'
#!/usr/bin/env bash
if [[ "$1" == "ps" ]]; then
  printf 'not json at all\n'
  exit 0
fi
exit 0
FAKE_ASPIRE_BROKEN
chmod 700 "${TEMP_ROOT}/bin/aspire"

out="$(PATH="${TEMP_ROOT}/bin:${PATH}" NO_BUILD_LOCK=1 XE_ASPIRE_APPHOST="${APPHOST}" "${SMOKE}" 2>&1)"; status=$?
check "an unreadable instance state => exit 4" "4" "${status}"
check_contains "unreadable state refuses to start" "refusing to start" "${out}"

# A bad flag must not launch anything.
out="$(PATH="${TEMP_ROOT}/bin:${PATH}" NO_BUILD_LOCK=1 "${SMOKE}" --not-a-flag 2>&1)"; status=$?
check "an unknown option => exit 2" "2" "${status}"

# --model with no value is a usage error, not a silent default.
out="$(PATH="${TEMP_ROOT}/bin:${PATH}" NO_BUILD_LOCK=1 "${SMOKE}" --model 2>&1)"; status=$?
check "--model without a value => exit 2" "2" "${status}"

# ---------------------------------------------------------------------------
echo
if [[ "${FAILED}" -ne 0 ]]; then
  echo "gpu-smoke.test.sh: ${FAILED} of ${CHECKS} checks FAILED" >&2
  exit 1
fi
if [[ "${CHECKS}" -eq 0 ]]; then
  echo "gpu-smoke.test.sh: ZERO checks ran — this is not a pass." >&2
  exit 1
fi
echo "gpu-smoke.test.sh: ${CHECKS} checks passed"
echo "gpu-smoke.test.sh: PASS"
