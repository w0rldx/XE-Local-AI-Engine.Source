#!/usr/bin/env bash
# run-tool-grammar-smoke-local.sh — OPT-IN live tool-grammar smoke against a real llama-server.
#
# Why this exists
#   A P1 shipped where llama-server rejected the whole turn with HTTP 400 "Failed to initialize
#   samplers: failed to parse grammar", because our tool parameter schemas carried repetition
#   bounds llama.cpp's json-schema-to-grammar cannot compile. Nothing in the automated suites can
#   catch that coming back:
#
#     * ChatLocalToolsE2ETests looks like it covers the local-tools path, but its chat backend is
#       FakeOllama. No chat template, no GBNF, no sampler ever runs there.
#     * LlamaGrammarToolSchemaCompatibilityTests pins our schemas under a bound we measured BY HAND.
#       It cannot notice llama.cpp changing that limit.
#
#   This script closes that gap by starting a REAL llama-server and posting the REAL production
#   tool offer at it. It is OPT-IN by design; nothing invokes it automatically. Run it by hand
#   after touching the tool catalog, the compatibility pass, or the pinned llama.cpp version.
#
# THE NEGATIVE CONTROL IS THE POINT — a 200 proves nothing on its own
#   The test posts the offer twice: once after the compatibility pass (must be 200) and once with
#   the original bounds (must be 400 carrying "failed to parse grammar"). The second post is what
#   makes the first one mean anything, because there are two ways a green run can be VACUOUS:
#
#     * A REASONING MODEL IS USELESS HERE. It emits reasoning_content first, so llama.cpp never
#       enters the constrained branch and never compiles a grammar at all. Qwen3.6-27B returns 200
#       on the exact payload that 400s on Qwen2.5-0.5B — a smoke that only checks "did it 200"
#       would pass against a model that never exercised the code path. Pick a NON-REASONING,
#       tool-capable model (Qwen2.5-0.5B-Instruct is ideal: 400 MB and fast).
#     * llama.cpp may have RAISED its limits, in which case the unsanitized offer compiles too and
#       LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound needs re-measuring.
#
#   The test fails on an accepted unsanitized offer and names both causes explicitly. It is never
#   allowed to pass.
#
#   A third way to be inert: llama-server IGNORES the offered tools without --jinja, so no grammar
#   is compiled. The test always launches with it, exactly as production does.
#
# What it asserts, in order. Each step must produce a verdict; a step that does not run is a
# FAILURE, never a silent skip.
#
#   1. Prerequisites  — a llama-server binary and a chat GGUF were discovered (or supplied).
#   2. Build          — the Tests project builds in Release (Debug skips the analyzers).
#   3. Test ran       — the gated test EXECUTED. It is env-gated and TUnit reports a skip as
#                       SUCCESS, so exit 0 alone can never be trusted: the run must show a
#                       non-zero test count, must not report a skip, and must have produced the
#                       evidence file the test writes only after both assertions pass.
#   4. Sanitized 200  — the shipped tool offer compiled into a grammar on the live server.
#   5. Unsanitized 400 — the negative control above. Without it, step 4 is unfalsifiable.
#
# Usage:
#   scripts/run-tool-grammar-smoke-local.sh [options]
#
# Options:
#   --server <path>   llama-server executable. Default: $XE_LLAMACPP_SERVER_PATH, else the
#                     installed runtime under the node data root (source-build first).
#   --model <path>    Chat GGUF. Default: the smallest installed one, EXCLUDING embedding,
#                     reranker and multimodal projector (mmproj) GGUFs — none of those is a chat
#                     model, so they would make the run inert in the same way a reasoning model does.
#   --data-root <dir> Node data root to discover under. Default: $XDG_DATA_HOME/XE-Local-AI-Engine
#                     (i.e. ~/.local/share/XE-Local-AI-Engine).
#   --evidence <file> Where the test writes its verdict JSON. Default: a temp file, left in place
#                     afterwards so the recorded verdicts can be inspected.
#   --help            Show this message.
#
# Prerequisites (all checked up front, each with an actionable failure message):
#   - dotnet, python3
#   - a llama-server executable
#   - a chat GGUF
#
# Env knobs:
#   XE_LLAMACPP_SERVER_PATH        honoured as the default --server
#   XE_TOOL_GRAMMAR_SMOKE_READY_SECONDS   llama-server readiness budget (default 300)
#   NO_BUILD_LOCK                  do NOT take the cross-process build lock (escape hatch)
#   NO_GUARD                       skip the contamination snapshot/verify
#
# Exit codes:
#   0   — every step ran AND passed
#   1   — the run was JUDGED and did not earn a pass: an assertion failed, or a step produced no
#         verdict (including "the test was skipped" and "zero tests ran"). Always accompanied by a
#         `=== Summary ===` block naming each step's verdict.
#   2   — a prerequisite is missing / usage error (nothing was run)
#   75  — CONTAMINATED: the test assemblies changed mid-run; the result is void, re-run it
#   130 — interrupted (Ctrl-C)

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
if ! PROJECT_ROOT="$(git -C "${SCRIPT_DIR}" rev-parse --show-toplevel 2>/dev/null)"; then
  PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd -P)"
fi

log()  { echo "[grammar-smoke] $*"; }
warn() { echo "[grammar-smoke] WARN: $*" >&2; }
prereq_fail() { echo "[grammar-smoke] PREREQUISITE MISSING: $*" >&2; exit 2; }
usage() { sed -n '2,/^set -uo/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//; $d'; }

trap 'echo; log "Interrupted."; exit 130' INT

# Step ledger — the anti-vacuous-pass mechanism, same shape as run-gpu-smoke-local.sh.
#
# Every step declares itself expected BEFORE it runs and records a verdict when it finishes. The
# final gate asserts that every expected step recorded PASS and that at least one step ran. A step
# that dies, is skipped, or silently returns early therefore FAILS the run instead of leaving a
# green summary behind — the same rule run-e2e-local.sh applies to a zero-test run.
LEDGER_EXPECTED=()
LEDGER_PASSED=()
LEDGER_FAILED=()
FAILURES=()

ledger_expect() { LEDGER_EXPECTED+=("$1"); }
ledger_pass()   { LEDGER_PASSED+=("$1"); log "PASS  $1"; }

step_fail() {
  local step="$1"; shift
  LEDGER_FAILED+=("${step}")
  FAILURES+=("${step}: $*")
  echo "[grammar-smoke] FAIL  ${step}: $*" >&2
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

ledger_finalize() {
  local status=0 step
  echo
  log "=== Summary ==="
  if [[ "${#LEDGER_EXPECTED[@]}" -eq 0 ]]; then
    echo "[grammar-smoke] FAIL: no steps were expected — nothing ran. This is not a pass." >&2
    return 1
  fi
  for step in "${LEDGER_EXPECTED[@]}"; do
    if ledger_contains "${step}" ${LEDGER_PASSED[@]+"${LEDGER_PASSED[@]}"}; then
      echo "  PASS       ${step}"
    elif ledger_contains "${step}" ${LEDGER_FAILED[@]+"${LEDGER_FAILED[@]}"}; then
      echo "  FAILED     ${step}" >&2
      status=1
    else
      # Nothing recorded a verdict: the step died or returned early without saying so. That is a
      # different failure from an assertion failing, and is exactly the vacuous-pass hole this
      # ledger exists to close.
      echo "  NO VERDICT ${step}  <- the step produced no result at all; treating as failure" >&2
      status=1
    fi
  done
  if [[ "${#FAILURES[@]}" -gt 0 ]]; then
    echo >&2
    echo "[grammar-smoke] ${#FAILURES[@]} failure(s):" >&2
    for step in "${FAILURES[@]}"; do echo "  - ${step}" >&2; done
    status=1
  fi
  return "${status}"
}

# The build lock cannot be inherited through an exported variable (see with-build-lock.sh), so it
# is taken by re-executing this whole script under it. This MUST happen before the option loop
# below consumes "$@" — re-exec'ing afterwards silently drops every flag the operator passed.
if [[ -z "${XE_BUILD_LOCK_HELD:-}" && -z "${NO_BUILD_LOCK:-}" ]]; then
  exec "${PROJECT_ROOT}/scripts/with-build-lock.sh" -- "${BASH_SOURCE[0]}" "$@"
fi

SERVER_PATH="${XE_LLAMACPP_SERVER_PATH:-}"
MODEL_PATH=""
DATA_ROOT="${XDG_DATA_HOME:-${HOME}/.local/share}/XE-Local-AI-Engine"
EVIDENCE_PATH=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --server)    SERVER_PATH="${2:-}"; shift 2 ;;
    --model)     MODEL_PATH="${2:-}"; shift 2 ;;
    --data-root) DATA_ROOT="${2:-}"; shift 2 ;;
    --evidence)  EVIDENCE_PATH="${2:-}"; shift 2 ;;
    --help|-h)   usage; exit 0 ;;
    *) echo "[grammar-smoke] Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

command -v dotnet  >/dev/null 2>&1 || prereq_fail "dotnet is required to build and run the test."
command -v python3 >/dev/null 2>&1 || prereq_fail "python3 is required to read the evidence JSON."

# Step 1 — prerequisites: a llama-server binary and a chat GGUF.
ledger_expect "1-prerequisites"

discover_server() {
  local candidate
  # The source build is preferred: on a CUDA box it is the binary that actually runs the GPU, and
  # its layout is fixed (LlamaCppSourceBuildService: {root}/llama.cpp/source-build/active/build/bin).
  candidate="${DATA_ROOT}/llama.cpp/source-build/active/build/bin/llama-server"
  if [[ -x "${candidate}" ]]; then
    printf '%s\n' "${candidate}"
    return 0
  fi
  # Otherwise any acquired variant under {root}/llama.cpp (cuda / vulkan / cpu), newest first.
  if [[ -d "${DATA_ROOT}/llama.cpp" ]]; then
    candidate="$(find "${DATA_ROOT}/llama.cpp" -type f -name llama-server -perm -u+x -printf '%T@\t%p\n' 2>/dev/null \
                 | sort -rn | head -1 | cut -f2-)"
    if [[ -n "${candidate}" ]]; then
      printf '%s\n' "${candidate}"
      return 0
    fi
  fi
  return 1
}

# Auto-pick the smallest CHAT GGUF. Embedding and reranker GGUFs are excluded on purpose: they ship
# no chat template, so llama-server would never compile a tool grammar and the whole run would be
# inert in exactly the way the negative control exists to catch — except it would fail confusingly
# instead of reporting "wrong kind of model". A real model store can have an embedding GGUF sitting
# next to the chat ones, so this is not a hypothetical. Multimodal projectors (mmproj-*.gguf) are
# excluded for the same reason and are usually the SMALLEST file in the store, so without the
# exclusion they win the size sort every time (2026-09-04: a gemma projector was auto-picked).
discover_model() {
  local dir
  for dir in "${DATA_ROOT}/models" "${DATA_ROOT}"; do
    [[ -d "${dir}" ]] || continue
    local found
    found="$(find "${dir}" -maxdepth 2 -type f -name '*.gguf' -printf '%s\t%p\n' 2>/dev/null \
             | grep -viE '(embed|rerank|bge-|nomic|mmproj|projector)' \
             | sort -n | head -1 | cut -f2-)"
    if [[ -n "${found}" ]]; then
      printf '%s\n' "${found}"
      return 0
    fi
  done
  return 1
}

if [[ -z "${SERVER_PATH}" ]]; then
  SERVER_PATH="$(discover_server || true)"
fi
if [[ -z "${SERVER_PATH}" || ! -x "${SERVER_PATH}" ]]; then
  prereq_fail "no llama-server executable found.
  Pass --server <path>, export XE_LLAMACPP_SERVER_PATH, or point --data-root at a node data root
  containing llama.cpp/source-build/active/build/bin/llama-server (looked under ${DATA_ROOT})."
fi

if [[ -z "${MODEL_PATH}" ]]; then
  MODEL_PATH="$(discover_model || true)"
fi
if [[ -z "${MODEL_PATH}" || ! -f "${MODEL_PATH}" ]]; then
  prereq_fail "no chat GGUF found.
  Pass --model <path.gguf>, or point --data-root at a node data root with a models/ directory
  (looked under ${DATA_ROOT}). It must be a NON-REASONING tool-capable chat model — see the header:
  a reasoning model never compiles the grammar, so the smoke would be inert."
fi

SERVER_PATH="$(realpath "${SERVER_PATH}")"
MODEL_PATH="$(realpath "${MODEL_PATH}")"
log "llama-server: ${SERVER_PATH}"
log "model:        ${MODEL_PATH}"
warn "the model must be NON-REASONING and tool-capable. A reasoning model emits reasoning_content"
warn "first, never compiles a grammar, and would make this run inert — the negative control (step 5)"
warn "is what catches that, so read its verdict rather than the exit code alone."
ledger_pass "1-prerequisites"

if [[ -z "${EVIDENCE_PATH}" ]]; then
  EVIDENCE_PATH="$(mktemp -t xe-tool-grammar-smoke-XXXXXX.json)"
fi
EVIDENCE_PATH="$(realpath -m "${EVIDENCE_PATH}")"
# The test writes this file only after BOTH assertions pass. Starting from a guaranteed-absent file
# is what makes "the file exists" a sound proof that the test really executed.
rm -f "${EVIDENCE_PATH}"

OUT_FILE="$(mktemp -t xe-tool-grammar-smoke-out-XXXXXX.log)"
trap 'rm -f "${OUT_FILE}"' EXIT

# Step 2 — Release build. Debug skips analyzer execution, so a green Debug build is not
# verification (docs/agent-knowledge.md §1).
ledger_expect "2-build"
log "=== Building XE-Local-AI-Engine.Tests (Release) ==="
if dotnet build "${PROJECT_ROOT}/XE-Local-AI-Engine.Tests/XE-Local-AI-Engine.Tests.csproj" --configuration Release; then
  ledger_pass "2-build"
else
  step_fail "2-build" "the Tests project did not build in Release."
  ledger_finalize
  exit 1
fi

# Step 3 — run the gated test and prove it EXECUTED.
ledger_expect "3-test-ran"
ledger_expect "4-sanitized-offer-accepted"
ledger_expect "5-unsanitized-offer-rejected"

TEST_CMD=(dotnet test "${PROJECT_ROOT}/XE-Local-AI-Engine.Tests/XE-Local-AI-Engine.Tests.csproj"
          --configuration Release --no-build --max-parallel-test-modules 1
          --treenode-filter '/*/*/LlamaGrammarLiveSmokeTests/*')

if [[ -z "${NO_GUARD:-}" ]]; then
  RUNNER=("${PROJECT_ROOT}/scripts/assembly-guard.sh" guard --test-bins --)
else
  RUNNER=()
fi

log "=== Running the live tool-grammar smoke ==="
XE_TOOL_GRAMMAR_SMOKE_SERVER="${SERVER_PATH}" \
XE_TOOL_GRAMMAR_SMOKE_MODEL="${MODEL_PATH}" \
XE_TOOL_GRAMMAR_SMOKE_EVIDENCE_PATH="${EVIDENCE_PATH}" \
  ${RUNNER[@]+"${RUNNER[@]}"} timeout --signal=TERM --kill-after=30s 20m "${TEST_CMD[@]}" 2>&1 | tee "${OUT_FILE}"
TEST_STATUS="${PIPESTATUS[0]}"

# Contamination is diagnosed FIRST: a run whose assemblies were rewritten underneath it can fail in
# any shape at all, including a vacuous zero-test summary, and every explanation below would be wrong.
if [[ "${TEST_STATUS}" -eq 75 ]]; then
  log "CONTAMINATED: the test assemblies changed mid-run. This result is VOID, not red — re-run it."
  exit 75
fi

# The gate is env-gated and TUnit reports a SKIP as success, so exit 0 is not evidence. Three
# independent checks have to agree before this step passes.
if grep -qiE 'total:[[:space:]]*0([^0-9]|$)|zero tests ran' "${OUT_FILE}"; then
  step_fail "3-test-ran" "the runner discovered ZERO tests — the treenode filter matched nothing. This is not a pass."
elif ! grep -qiE 'total:[[:space:]]*[1-9]' "${OUT_FILE}"; then
  step_fail "3-test-ran" "no MTP test summary was found — the module did not run as a test app. This is not a pass."
elif grep -qiE 'skipped:[[:space:]]*[1-9]' "${OUT_FILE}"; then
  step_fail "3-test-ran" "the test SKIPPED itself — the gate variables did not reach it. This is not a pass."
elif [[ ! -s "${EVIDENCE_PATH}" ]]; then
  step_fail "3-test-ran" "no evidence was written to ${EVIDENCE_PATH}; the test did not reach its verdict."
elif [[ "${TEST_STATUS}" -ne 0 ]]; then
  step_fail "3-test-ran" "the test run exited ${TEST_STATUS}. Read the assertion message above."
else
  ledger_pass "3-test-ran"
fi

# Steps 4 and 5 — read the verdicts the test recorded. The test writes this file only after both
# assertions pass, so these steps report what was judged rather than re-deciding it.
if [[ -s "${EVIDENCE_PATH}" ]]; then
  log "=== Evidence ==="
  python3 -m json.tool "${EVIDENCE_PATH}" || warn "the evidence file is not valid JSON."

  SANITIZED_STATUS="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["sanitizedOffer"]["actual"])' "${EVIDENCE_PATH}" 2>/dev/null || echo "")"
  UNSANITIZED_STATUS="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["unsanitizedOffer"]["actual"])' "${EVIDENCE_PATH}" 2>/dev/null || echo "")"
  UNSANITIZED_MESSAGE="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["unsanitizedOffer"]["message"])' "${EVIDENCE_PATH}" 2>/dev/null || echo "")"

  if [[ "${SANITIZED_STATUS}" == "200" ]]; then
    ledger_pass "4-sanitized-offer-accepted"
  else
    step_fail "4-sanitized-offer-accepted" "the sanitized production tool offer was not accepted (HTTP ${SANITIZED_STATUS:-<none>})."
  fi

  if [[ "${UNSANITIZED_STATUS}" == "400" ]] && grep -qi 'failed to parse grammar' <<<"${UNSANITIZED_MESSAGE}"; then
    ledger_pass "5-unsanitized-offer-rejected"
  else
    step_fail "5-unsanitized-offer-rejected" \
      "the NEGATIVE CONTROL did not hold (HTTP ${UNSANITIZED_STATUS:-<none>}): ${UNSANITIZED_MESSAGE:-<no message>}.
  Either this model is a REASONING model (it never compiles a grammar, so the run is inert), or
  llama.cpp raised its repetition limits and MaxGrammarRepetitionBound must be re-measured."
  fi
fi

if ledger_finalize; then
  echo
  log "Live tool-grammar smoke PASSED."
  exit 0
fi
exit 1
