#!/usr/bin/env bash
# run-model-matrix-local.sh — OPT-IN live model matrix: pinned GGUF models, scenario checks through a real local node.
#
# Why this exists
#   The real-model lanes (GPU smoke, hardware compat, tool grammar) passed on every model of the 2026-10 model-matrix
#   round BEFORE any of its fixes: they ask "does it answer", and every finding of that round was about how it answers
#   (thinking control, output limits, tool calls, background jobs, window pressure, MoE placement). This lane re-runs the
#   checks that pin those fixes, per pinned model, through the product's own API. It is not a quality benchmark and not
#   part of any gate: nothing invokes it automatically. It is slow: run it by hand once, as the last validation step
#   of a change that touches inference, thinking, tool offering, window budgeting or model fit, and before a tester
#   release candidate. Do not run it while iterating or for changes outside those areas.
#
# What it does
#   1. Resolves the models of a tier from scripts/model-matrix/models.json and checks each installed file against the
#      pinned SHA-256 (a mismatch is never a pass). With --download, missing models are installed through the node's
#      own Hugging Face path first and then verified the same way.
#   2. Owns the dev-host lifecycle like run-gpu-smoke-local.sh: refuses when this checkout's host is already up, builds,
#      starts, stops only what it started, and tears down through a trap on exit and on signals. A llama-server its
#      node spawned that outlives dev-stop.sh is stopped by PID and logged; no other process is touched.
#   3. Runs scripts/model-matrix-driver.py per model. Hard checks are software contracts and fail the run; measured
#      checks (three-step tool task k/3, time to first token, tokens/s, VRAM, RSS) are recorded and never fail it.
#      Every API call, hub event and llama-server /slots task is written to the evidence directory as it happens; the
#      run ends with results.json and summary.md there. No results, or no hard check that ran, is not a pass.
#      Only llama-server processes this checkout's host spawned count (slots, placement, RSS, eject): one whose parent
#      chain does not reach XE-Local-AI-Engine.Client of this checkout, such as another checkout's, is ignored.
#
# Usage:
#   scripts/run-model-matrix-local.sh [options]
#
# Options:
#   --tier fast|extended|rc  Models to run (default fast). fast = Qwen3.5-0.8B + 4B; extended adds Qwen3.5-9B,
#                            Granite 4.1-3B and LFM2.5-8B-A1B; rc adds Qwen3.6-35B-A3B and Qwen3.8-27B.
#   --models <id,id>         Run these manifest ids instead of a tier.
#   --checks <name,name>     Run only these checks (names: see scripts/model-matrix-driver.py HARD_CHECKS and
#                            MEASURED_CHECKS). Default: all.
#   --download               Install missing models through the node, then verify their hash.
#   --variant cuda|cpu       Runtime variant (default cuda). cpu skips the GPU-only checks. When XE_LLAMACPP_SERVER_PATH
#                            names a bring-your-own llama-server, XE_LLAMACPP_VARIANT is set to this value. Before any
#                            check, the node's device audit must report this backend (cuda without a CPU fallback, or
#                            cpu); a mismatch exits 2. Without a bring-your-own server the node runs its managed runtime,
#                            so --variant cpu on a GPU box needs a CPU llama-server through XE_LLAMACPP_SERVER_PATH. The
#                            audit establishes the backend, not that the GPU did the work: that is the GPU smoke's job.
#   --evidence <dir>         Evidence directory (default .tmp/model-matrix/<UTC timestamp> in this checkout).
#   --help                   Show this message.
#
# Prerequisites (checked; each failure exits 2):
#   - dotnet, python3, aspire on PATH; nvidia-smi and a GPU for --variant cuda.
#   - This checkout's node has been set up ONCE by hand: first-run setup done (operator account) and the external-access
#     profile set to `offline` in node settings, so nothing is provisioned or fetched during a run. The lane logs in, it
#     never creates an account. Credentials: XE_MODEL_MATRIX_EMAIL / XE_MODEL_MATRIX_PASSWORD (default
#     admin@localhost.test / !Demo1234567).
#   - The models are installed in the node's models directory: HuggingFace__ModelsDirectory, else
#     ${XDG_DATA_HOME:-~/.local/share}/XE-Local-AI-Engine/models (dev-start.sh's default), or --download.
#
# What the checks change on the node, and restore: the memory-job check sets the memory-extraction model and creates
# a probe agent (setting restored, agent deleted); the 4,096-window check pins a window profile (invalidated after).
# Conversations created by the run stay, as evidence.
#
# Exit codes:
#   0   — every hard check that ran passed, and at least one ran
#   1   — the product failed: a hard check failed, no hard check ran, no results were written, or a model installed by
#         --download does not match its pinned hash
#   2   — prerequisite missing / usage error (including a model absent without --download, a file on disk whose hash
#         is not the pinned one, or a node whose device audit reports another backend than --variant)
#   3   — an AppHost for this checkout is already running; refusing to reuse or stop it
#   4   — could not establish whether an AppHost is running; refusing to start one
#   5   — infrastructure: the AppHost did not build, start or become healthy, the base URL or login failed, or a
#         download did not complete
#   75  — CONTAMINATED: the build output changed mid-run; the result is void, re-run it
#   130 — interrupted; the AppHost is still torn down

set -uo pipefail

MATRIX_SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
if ! MATRIX_PROJECT_ROOT="$(git -C "${MATRIX_SCRIPT_DIR}" rev-parse --show-toplevel 2>/dev/null)"; then
  MATRIX_PROJECT_ROOT="$(cd "${MATRIX_SCRIPT_DIR}/.." && pwd -P)"
fi
DRIVER="${MATRIX_SCRIPT_DIR}/model-matrix-driver.py"
MANIFEST="${XE_MODEL_MATRIX_MANIFEST:-${MATRIX_SCRIPT_DIR}/model-matrix/models.json}"
export XE_MODEL_MATRIX_EMAIL="${XE_MODEL_MATRIX_EMAIL:-admin@localhost.test}"
export XE_MODEL_MATRIX_PASSWORD="${XE_MODEL_MATRIX_PASSWORD:-!Demo1234567}"

log() { echo "[model-matrix] $*"; }
prereq_fail() { echo "[model-matrix] PREREQUISITE MISSING: $*" >&2; exit 2; }

# The verdict. results.json is the only input: a run without it, or without a single hard check that ran, is not a
# pass, and a measured value never decides it. Kept as a function so scripts/tests/model-matrix.test.sh can drive it.
judge_results() {
  python3 "${DRIVER}" judge --results "$1"
}

# Sourcing guard for scripts/tests/model-matrix.test.sh: define the functions above, run nothing.
# shellcheck disable=SC2317
if [[ -n "${XE_MODEL_MATRIX_LIB_ONLY:-}" ]]; then
  return 0 2>/dev/null || exit 0
fi

# Serialize against any other build/test before anything is built (see with-build-lock.sh).
if [[ -z "${XE_BUILD_LOCK_HELD:-}" && -z "${NO_BUILD_LOCK:-}" ]]; then
  exec "${MATRIX_PROJECT_ROOT}/scripts/with-build-lock.sh" -- "${BASH_SOURCE[0]}" "$@"
fi

TIER="fast"
MODEL_IDS=""
CHECKS=""
DOWNLOAD="false"
VARIANT="cuda"
EVIDENCE=""

usage() { sed -n '2,/^set -uo/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//; $d'; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --tier)     TIER="${2:-}"; [[ -n "${TIER}" ]] || prereq_fail "--tier needs a value"; shift 2 ;;
    --models)   MODEL_IDS="${2:-}"; [[ -n "${MODEL_IDS}" ]] || prereq_fail "--models needs a value"; shift 2 ;;
    --checks)   CHECKS="${2:-}"; [[ -n "${CHECKS}" ]] || prereq_fail "--checks needs a value"; shift 2 ;;
    --download) DOWNLOAD="true"; shift ;;
    --variant)  VARIANT="${2:-}"; [[ -n "${VARIANT}" ]] || prereq_fail "--variant needs a value"; shift 2 ;;
    --evidence) EVIDENCE="${2:-}"; [[ -n "${EVIDENCE}" ]] || prereq_fail "--evidence needs a value"; shift 2 ;;
    --help|-h)  usage; exit 0 ;;
    *)          echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

log "=== Preflight ==="
for tool in dotnet python3 aspire; do
  command -v "${tool}" >/dev/null 2>&1 || prereq_fail "${tool} is not on PATH."
done
[[ -f "${MANIFEST}" ]] || prereq_fail "manifest not found at ${MANIFEST}"
case "${VARIANT}" in
  cuda)
    command -v nvidia-smi >/dev/null 2>&1 || prereq_fail "nvidia-smi is not on PATH (needed for --variant cuda)."
    [[ -n "$(nvidia-smi --id=0 --query-gpu=name --format=csv,noheader 2>/dev/null | head -n 1)" ]] \
      || prereq_fail "nvidia-smi reports no GPU; use --variant cpu on a box without one." ;;
  cpu) ;;
  *) prereq_fail "--variant must be cuda or cpu, got '${VARIANT}'." ;;
esac
[[ -z "${XE_LLAMACPP_SERVER_PATH:-}" ]] || export XE_LLAMACPP_VARIANT="${VARIANT}"

RESOLVED="$(python3 "${DRIVER}" resolve --manifest "${MANIFEST}" --tier "${TIER}" --models "${MODEL_IDS}" \
  --checks "${CHECKS}")" || prereq_fail "could not resolve the models or checks (see above)."
mapfile -t IDS <<<"${RESOLVED}"
[[ -n "${IDS[0]:-}" ]] || prereq_fail "tier '${TIER}' has no models in ${MANIFEST}."
IDS_CSV="$(IFS=,; echo "${IDS[*]}")"
log "models     ${IDS_CSV} (tier ${TIER}, variant ${VARIANT})"

# The host and this script must agree on the models directory, so it is exported for dev-start.sh too.
export HuggingFace__ModelsDirectory="${HuggingFace__ModelsDirectory:-${XDG_DATA_HOME:-${HOME}/.local/share}/XE-Local-AI-Engine/models}"
MODELS_DIR="${HuggingFace__ModelsDirectory}"
log "models dir ${MODELS_DIR}"

# Prints the ids whose installed file is missing; exits the script on a hash mismatch.
verify_models() {
  local mismatch_status="$1" id state actual
  local missing=()
  for id in "${IDS[@]}"; do
    IFS=$'\t' read -r state actual < <(python3 "${DRIVER}" local-file --manifest "${MANIFEST}" \
      --models-dir "${MODELS_DIR}" --id "${id}")
    case "${state}" in
      ok) log "verified   ${id} sha256 matches the manifest" >&2 ;;
      missing) missing+=("${id}") ;;
      *)
        echo "[model-matrix] ${id}: the installed file's sha256 ${actual:-?} is not the pinned one; it is not the artifact this lane pins." >&2
        exit "${mismatch_status}" ;;
    esac
  done
  printf '%s\n' "${missing[@]+"${missing[@]}"}"
}

# verify_models runs in a command substitution, so its exit on a mismatch is propagated explicitly.
MISSING="$(verify_models 2)" || exit $?
if [[ -n "${MISSING}" && "${DOWNLOAD}" != "true" ]]; then
  prereq_fail "not installed: $(paste -sd, - <<<"${MISSING}"). Install them, or re-run with --download."
fi

if "${MATRIX_SCRIPT_DIR}/dev-status.sh" >/dev/null 2>&1; then
  echo "[model-matrix] An AppHost for this checkout is already running. Refusing to reuse or stop it." >&2
  echo "[model-matrix] Stop it first with scripts/dev-stop.sh." >&2
  exit 3
else
  status_result=$?
  if [[ "${status_result}" -ne 3 ]]; then
    echo "[model-matrix] Could not establish whether this AppHost is stopped; refusing to start one." >&2
    exit 4
  fi
fi

EVIDENCE="${EVIDENCE:-${MATRIX_PROJECT_ROOT}/.tmp/model-matrix/$(date -u +%Y%m%dT%H%M%SZ)}"
mkdir -p "${EVIDENCE}" || prereq_fail "cannot create the evidence directory ${EVIDENCE}"
EVIDENCE="$(cd "${EVIDENCE}" && pwd -P)"
log "evidence   ${EVIDENCE}"

GUARD_STATE=""
CLEANUP_ARMED="false"
CLIENT_DEBUG_ROOT="${MATRIX_PROJECT_ROOT}/XE-Local-AI-Engine.Client/bin/Debug"
ASPIRE_APPHOST="${XE_ASPIRE_APPHOST:-${MATRIX_PROJECT_ROOT}/XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj}"

# Field 22 of /proc/<pid>/stat, counted after the command name (which may contain spaces); empty once it is gone.
# Used by cleanup, which only the EXIT trap invokes.
# shellcheck disable=SC2329
proc_start_time() {
  local stat
  stat="$(cat "/proc/$1/stat" 2>/dev/null)" || return 0
  awk '{ print $20 }' <<<"${stat##*) }"
}

# Invoked by the EXIT trap below.
# shellcheck disable=SC2329
cleanup() {
  local status=$?
  trap - EXIT
  if [[ "${CLEANUP_ARMED}" == "true" ]]; then
    # Ownership is the parent chain to this checkout's host, so it is read while the host is still up.
    local owned pid start
    owned="$(python3 "${DRIVER}" owned-servers 2>/dev/null)"
    log "cleanup: stopping the AppHost this run started"
    # NEVER `aspire stop --all`: dev-stop.sh is scoped to this checkout and reaps a leftover llama-server.
    "${MATRIX_SCRIPT_DIR}/dev-stop.sh" >/dev/null 2>&1 || {
      echo "[model-matrix] Cleanup failed; an AppHost or llama-server may still be running." >&2
      [[ "${status}" -ne 0 ]] || status=1
    }
    # A llama-server of this node that outlived the host (seen: an embedding server started as a turn failed) is
    # stopped by PID. The start time guards against a reused PID; nothing this node did not spawn is touched.
    while IFS=$'\t' read -r pid start; do
      [[ -n "${pid}" && "$(proc_start_time "${pid}")" == "${start}" ]] || continue
      log "cleanup: stopping llama-server ${pid}, left behind by this run's node"
      kill -TERM "${pid}" 2>/dev/null
      for _ in $(seq 1 50); do
        [[ "$(proc_start_time "${pid}")" == "${start}" ]] || break
        sleep 0.1
      done
      [[ "$(proc_start_time "${pid}")" != "${start}" ]] || kill -KILL "${pid}" 2>/dev/null
    done <<<"${owned}"
  fi
  if [[ -n "${GUARD_STATE}" ]]; then
    if ! "${MATRIX_PROJECT_ROOT}/scripts/assembly-guard.sh" verify "${GUARD_STATE}"; then
      case "${status}" in
        0|1) status=75 ;;
        *)   echo "[model-matrix] NOTE: the build output also changed during the run, but exit ${status} is the more actionable failure." >&2 ;;
      esac
    fi
    rm -f -- "${GUARD_STATE}"
  fi
  exit "${status}"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

log "=== Building this checkout's AppHost ==="
dotnet build "${ASPIRE_APPHOST}" --configuration Debug --nologo >"${EVIDENCE}/build.log" 2>&1 || {
  echo "[model-matrix] the AppHost Debug build failed (${EVIDENCE}/build.log); nothing was judged." >&2
  exit 5
}

if [[ -z "${NO_GUARD:-}" && -d "${CLIENT_DEBUG_ROOT}" ]]; then
  GUARD_STATE="$(mktemp)"
  "${MATRIX_PROJECT_ROOT}/scripts/assembly-guard.sh" snapshot "${GUARD_STATE}" --root "${CLIENT_DEBUG_ROOT}"
fi

log "=== Starting this checkout's AppHost ==="
CLEANUP_ARMED="true"
"${MATRIX_SCRIPT_DIR}/dev-start.sh" --no-build >/dev/null || {
  echo "[model-matrix] dev-start.sh failed; only one host may run per data directory. Nothing was judged." >&2
  exit 5
}
READY_TIMEOUT_SECONDS="${XE_MODEL_MATRIX_TIMEOUT_SECONDS:-240}"
timeout "$((READY_TIMEOUT_SECONDS + 15))s" aspire wait app --apphost "${ASPIRE_APPHOST}" \
  --status healthy --timeout "${READY_TIMEOUT_SECONDS}" --non-interactive --nologo >/dev/null || {
  echo "[model-matrix] the app resource did not become healthy within ${READY_TIMEOUT_SECONDS}s." >&2
  exit 5
}
BASE_URL="$("${MATRIX_SCRIPT_DIR}/dev-status.sh" --json | python3 "${DRIVER}" base-url)" || BASE_URL=""
[[ -n "${BASE_URL}" ]] || { echo "[model-matrix] could not discover the app base URL from dev-status.sh --json." >&2; exit 5; }
log "base URL   ${BASE_URL}"

if [[ -n "${MISSING}" ]]; then
  log "=== Downloading through the node: $(paste -sd, - <<<"${MISSING}") ==="
  python3 "${DRIVER}" download --base-url "${BASE_URL}" --manifest "${MANIFEST}" \
    --ids "$(paste -sd, - <<<"${MISSING}")" --evidence "${EVIDENCE}"
  download_status=$?
  [[ "${download_status}" -eq 0 ]] || exit "${download_status}"
  # A product download that does not match its pin is a product failure (1), not a local prerequisite.
  STILL_MISSING="$(verify_models 1)" || exit $?
  [[ -z "${STILL_MISSING}" ]] || { echo "[model-matrix] downloaded but not installed: ${STILL_MISSING}" >&2; exit 1; }
fi

log "=== Running the checks ==="
python3 "${DRIVER}" run --base-url "${BASE_URL}" --manifest "${MANIFEST}" --ids "${IDS_CSV}" \
  --models-dir "${MODELS_DIR}" --variant "${VARIANT}" --tier "${TIER}" --checks "${CHECKS}" --evidence "${EVIDENCE}"
run_status=$?
[[ "${run_status}" -eq 0 ]] || exit "${run_status}"

echo
log "=== Verdict (${EVIDENCE}/summary.md) ==="
if judge_results "${EVIDENCE}/results.json"; then
  log "RESULT: PASS"
  exit 0
fi
log "RESULT: FAIL — see the failures above. This is NOT a pass."
exit 1
