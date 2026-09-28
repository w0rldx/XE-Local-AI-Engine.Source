#!/usr/bin/env bash
# run-retrieval-eval-local.sh — OPT-IN real-model retrieval eval (RetrievalEvalLiveTests).
#
# Why this exists
#   The deterministic RetrievalEval suite proves the ingestion/fusion/rerank plumbing with fake
#   vectors; it cannot say whether a REAL embedder and a REAL reranker improve retrieval, or how
#   long a rerank takes on this hardware. RetrievalEvalLiveTests launches real llama-server
#   processes (embedder + each reranker), scores the live corpus through the production search
#   path and writes retrieval-eval.json + retrieval-eval.md. It is OPT-IN; nothing invokes it.
#
# A skipped test is not a pass
#   The test is env-gated and TUnit reports a skip as success, so exit 0 from `dotnet test` proves
#   nothing. This script starts from an absent report and requires retrieval-eval.json to exist
#   afterwards. A run without the JSON is a failure, never a pass.
#
# INVALID is not a number
#   A config whose forced rerank degraded, or whose reranker failed its score sanity gate (e.g. a
#   Qwen3 GGUF scoring bug), is written as INVALID. Any INVALID config fails the run (exit 1) so it
#   can never be quoted as a quality result. NEG-* rows are the negative controls: NEG-dead is
#   INVALID by design, and the controls' own verdicts are checked instead.
#
# Usage:
#   scripts/run-retrieval-eval-local.sh [options]
#
# Options:
#   --server <path>        llama-server executable. Default: $XE_LLAMACPP_SERVER_PATH, else the
#                          installed runtime under the data root (source-build first). Read-only: a server
#                          under the data root runs from a private copy in .tmp/retrieval-eval/, removed on exit.
#   --data-root <dir>      Node data root to discover under (read-only, never written).
#                          Default: $XDG_DATA_HOME/XE-Local-AI-Engine (~/.local/share/XE-Local-AI-Engine).
#   --embed <path>         Embedding GGUF. Default: nomic-ai-nomic-embed-text-v1.5-gguf-F16-*.gguf
#                          under <data-root>/models.
#   --reranker <id=path[@langs]>  A reranker to measure (repeatable; the first one also runs PROD and
#                          the negative controls). Default: bge = gpustack-bge-reranker-v2-m3-gguf-Q4_K_M-*.gguf.
#                          The optional @en or @en,de suffix names the languages it supports (default:
#                          all); its sanity gate skips pairs in other languages.
#   --reranker-preset all  Also measure qwen3 (Qwen3-Reranker-0.6B Q8_0) and jina
#                          (jina-reranker-v1-turbo-en Q8_0, declared @en) when found in the scratch
#                          models dir or <data-root>/models.
#   --download             Fetch the missing qwen3/jina GGUFs from Hugging Face into the scratch
#                          models dir (never the data root), sha256-verified against the HF LFS oid.
#   --models-dir <dir>     Scratch models dir. Default: .tmp/retrieval-eval/models.
#   --chat <path>          Chat GGUF: enables the contention round.
#   --ngl <n>              GPU layers for every server (default 99; 0 = CPU-only pass).
#   --k <n>                Cut-off K (default 5).
#   --corpus <dir>         A private corpus dir instead of the committed LiveCorpus.
#   --report <dir>         Report dir. Default: .tmp/retrieval-eval/report-<UTC timestamp>.
#   --no-build             Skip the Release build (the previous Release output is used).
#   --help                 Show this message.
#
# The build lock:
#   The whole run (download, build and the live test) holds the cross-process build lock, so every
#   other gate on this box waits for it: up to the 90 min timeout, longer if it first queues behind
#   another holder. Check scripts/build-lock-status.sh before starting it.
#
# Env knobs:
#   XE_LLAMACPP_SERVER_PATH          honoured as the default --server
#   XE_RETRIEVAL_EVAL_TIMEOUT        wall-clock budget for the test run (default 90m, `timeout` syntax)
#   NO_BUILD_LOCK                    do NOT take the cross-process build lock (escape hatch)
#   NO_GUARD                         skip the assembly contamination guard
#
# Exit codes:
#   0   — the test ran, the JSON exists, no config is INVALID and every negative control passed
#   1   — the test failed, skipped, produced no JSON, or a config is INVALID (listed)
#   2   — a prerequisite is missing / usage error (nothing was run)
#   5   — infrastructure: a llama-server did not start/become healthy, or a download failed
#         (AbortReason "infra: ..."), or --chat was given and the contention round was skipped
#   75  — CONTAMINATED: the test assemblies changed mid-run; the result is void, re-run it
#   130 — interrupted (Ctrl-C)

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
if ! PROJECT_ROOT="$(git -C "${SCRIPT_DIR}" rev-parse --show-toplevel 2>/dev/null)"; then
  PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd -P)"
fi

log()  { echo "[retrieval-eval] $*"; }
fail() { echo "[retrieval-eval] FAIL: $*" >&2; }
prereq_fail() { echo "[retrieval-eval] PREREQUISITE MISSING: $*" >&2; exit 2; }
usage() { sed -n '2,/^set -uo/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//; $d'; }

# Help must not queue behind another checkout's gate for the lock.
for arg in "$@"; do [[ "${arg}" == "-h" || "${arg}" == "--help" ]] && { usage; exit 0; }; done

# Taken by re-executing under the lock BEFORE the option loop consumes "$@" (see with-build-lock.sh).
if [[ -z "${XE_BUILD_LOCK_HELD:-}" && -z "${NO_BUILD_LOCK:-}" ]]; then
  exec "${PROJECT_ROOT}/scripts/with-build-lock.sh" -- "${BASH_SOURCE[0]}" "$@"
fi

# The test command (guard, timeout, dotnet test, the MTP test host and every llama-server it spawns)
# inherits this marker; this shell does not export it. Leftovers are reaped by marker, never by name
# or binary path, which other checkouts' hosts share. `timeout --kill-after` reaches only
# `dotnet test`, so an orphaned test host or server is found here.
RUN_ID="retrieval-eval-$$-${RANDOM}${RANDOM}"
marked_pids() {
  local environ pid
  for environ in /proc/[0-9]*/environ; do
    pid="${environ#/proc/}"
    pid="${pid%/environ}"
    if grep -qzxF "XE_RETRIEVAL_EVAL_RUN_ID=${RUN_ID}" "${environ}" 2>/dev/null; then
      printf '%s\n' "${pid}"
    fi
  done
}
reap_ours() {
  local pids
  pids="$(marked_pids)"
  [[ -n "${pids}" ]] || return 0
  log "reaping leftover processes started by this run: $(tr '\n' ' ' <<<"${pids}")"
  # shellcheck disable=SC2086 # one PID per word
  kill -TERM ${pids} 2>/dev/null
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    pids="$(marked_pids)"
    [[ -n "${pids}" ]] || return 0
    sleep 1
  done
  log "still alive after 10s, sending KILL: $(tr '\n' ' ' <<<"${pids}")"
  # shellcheck disable=SC2086 # one PID per word
  kill -KILL ${pids} 2>/dev/null
}
OUT_FILE=""
SERVER_COPY_DIR=""
# The servers are reaped first: the private server copy is removed only once nothing runs from it.
trap 'reap_ours; [[ -n "${OUT_FILE}" ]] && rm -f "${OUT_FILE}"; [[ -n "${SERVER_COPY_DIR}" ]] && rm -rf -- "${SERVER_COPY_DIR}"' EXIT
trap 'echo; log "Interrupted."; exit 130' INT TERM

SERVER_PATH="${XE_LLAMACPP_SERVER_PATH:-}"
DATA_ROOT="${XDG_DATA_HOME:-${HOME}/.local/share}/XE-Local-AI-Engine"
EMBED_PATH=""
RERANKERS=()
PRESET=""
DOWNLOAD=""
MODELS_DIR="${PROJECT_ROOT}/.tmp/retrieval-eval/models"
CHAT_PATH=""
NGL=""
K=""
CORPUS_DIR=""
REPORT_DIR=""
NO_BUILD=""

need_value() {
  if [[ -z "${2:-}" || "${2}" == --* ]]; then
    echo "[retrieval-eval] $1 needs a value." >&2
    usage >&2
    exit 2
  fi
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --server)          need_value "$1" "${2:-}"; SERVER_PATH="$2"; shift 2 ;;
    --data-root)       need_value "$1" "${2:-}"; DATA_ROOT="$2"; shift 2 ;;
    --embed)           need_value "$1" "${2:-}"; EMBED_PATH="$2"; shift 2 ;;
    --reranker)        need_value "$1" "${2:-}"; RERANKERS+=("$2"); shift 2 ;;
    --reranker-preset) need_value "$1" "${2:-}"; PRESET="$2"; shift 2 ;;
    --download)        DOWNLOAD=1; shift ;;
    --models-dir)      need_value "$1" "${2:-}"; MODELS_DIR="$2"; shift 2 ;;
    --chat)            need_value "$1" "${2:-}"; CHAT_PATH="$2"; shift 2 ;;
    --ngl)             need_value "$1" "${2:-}"; NGL="$2"; shift 2 ;;
    --k)               need_value "$1" "${2:-}"; K="$2"; shift 2 ;;
    --corpus)          need_value "$1" "${2:-}"; CORPUS_DIR="$2"; shift 2 ;;
    --report)          need_value "$1" "${2:-}"; REPORT_DIR="$2"; shift 2 ;;
    --no-build)        NO_BUILD=1; shift ;;
    --help|-h)         usage; exit 0 ;;
    *) echo "[retrieval-eval] Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

[[ -z "${PRESET}" || "${PRESET}" == "all" ]] || prereq_fail "--reranker-preset only knows 'all' (got '${PRESET}')."
[[ -z "${NGL}" || "${NGL}" =~ ^[0-9]+$ ]] || prereq_fail "--ngl must be an integer >= 0 (got '${NGL}')."
[[ -z "${K}" || "${K}" =~ ^[1-9][0-9]*$ ]] || prereq_fail "--k must be an integer >= 1 (got '${K}')."
command -v dotnet >/dev/null 2>&1 || prereq_fail "dotnet is required to build and run the test."
command -v jq     >/dev/null 2>&1 || prereq_fail "jq is required to read the report JSON."

# First match of a filename glob under the given dirs (read-only; -maxdepth 1).
find_model() {
  local pattern="$1" dir found
  shift
  for dir in "$@"; do
    [[ -d "${dir}" ]] || continue
    found="$(find "${dir}" -maxdepth 1 -type f -iname "${pattern}" 2>/dev/null | LC_ALL=C sort | head -1)"
    if [[ -n "${found}" ]]; then
      printf '%s\n' "${found}"
      return 0
    fi
  done
  return 1
}

discover_server() {
  local candidate="${DATA_ROOT}/llama.cpp/source-build/active/build/bin/llama-server"
  if [[ -x "${candidate}" ]]; then
    printf '%s\n' "${candidate}"
    return 0
  fi
  if [[ -d "${DATA_ROOT}/llama.cpp" ]]; then
    candidate="$(find "${DATA_ROOT}/llama.cpp" -type f -name llama-server -perm -u+x -printf '%T@\t%p\n' 2>/dev/null \
                 | sort -rn | head -1 | cut -f2-)"
    [[ -n "${candidate}" ]] && { printf '%s\n' "${candidate}"; return 0; }
  fi
  return 1
}

# Candidate rerankers: id, HF repo, pinned HF commit, file, reranker glob, supported languages (empty =
# all). The revision is pinned so a re-upload under the same name cannot silently change what a round measured.
CANDIDATES=(
  "qwen3|ggml-org/Qwen3-Reranker-0.6B-Q8_0-GGUF|a02f48bb4f057028298c21fa033da2b30d7742d5|qwen3-reranker-0.6b-q8_0.gguf|*qwen3-reranker-0.6b*q8_0*.gguf|"
  "jina|gpustack/jina-reranker-v1-turbo-en-GGUF|93961807ed915fd207f20e76112427dae4abef22|jina-reranker-v1-turbo-en-Q8_0.gguf|*jina-reranker-v1-turbo-en*Q8_0*.gguf|en"
)

download_candidate() {
  local repo="$1" revision="$2" file="$3" target="${MODELS_DIR}/$3" expected="" size="" actual
  read -r expected size < <(curl -fsSL "https://huggingface.co/api/models/${repo}/tree/${revision}" \
    | jq -r --arg f "${file}" '.[] | select(.path == $f) | "\(.lfs.oid // "") \(.size // "")"')
  if [[ -z "${expected}" ]]; then
    fail "could not resolve ${repo}@${revision}/${file} on the Hugging Face API."
    return 1
  fi
  log "downloading ${repo}@${revision}/${file} (${size} bytes) -> ${target}"
  mkdir -p "${MODELS_DIR}"
  if ! curl -fL --retry 3 -o "${target}.part" "https://huggingface.co/${repo}/resolve/${revision}/${file}"; then
    rm -f "${target}.part"
    fail "download of ${repo}/${file} failed."
    return 1
  fi
  actual="$(sha256sum "${target}.part" | cut -d' ' -f1)"
  if [[ "${actual}" != "${expected}" ]]; then
    rm -f "${target}.part"
    fail "${file}: sha256 ${actual} does not match the HF LFS oid ${expected}."
    return 1
  fi
  mv "${target}.part" "${target}"
  log "downloaded ${file}: revision ${revision}, $(stat -c %s "${target}") bytes, sha256 ${actual}"
}

if [[ -z "${SERVER_PATH}" ]]; then
  SERVER_PATH="$(discover_server || true)"
fi
[[ -n "${SERVER_PATH}" && -x "${SERVER_PATH}" ]] || prereq_fail "no llama-server executable found.
  Pass --server <path>, export XE_LLAMACPP_SERVER_PATH, or point --data-root at a node data root
  (looked under ${DATA_ROOT})."

if [[ -z "${EMBED_PATH}" ]]; then
  EMBED_PATH="$(find_model 'nomic-ai-nomic-embed-text-v1.5-gguf-F16-*.gguf' "${DATA_ROOT}/models" || true)"
fi
[[ -n "${EMBED_PATH}" && -f "${EMBED_PATH}" ]] || prereq_fail "no embedding GGUF found.
  Pass --embed <path.gguf> (looked for nomic-ai-nomic-embed-text-v1.5-gguf-F16-*.gguf under ${DATA_ROOT}/models)."

if [[ "${#RERANKERS[@]}" -eq 0 ]]; then
  BGE="$(find_model 'gpustack-bge-reranker-v2-m3-gguf-Q4_K_M-*.gguf' "${DATA_ROOT}/models" || true)"
  [[ -n "${BGE}" ]] || prereq_fail "no bge reranker GGUF found.
  Pass --reranker id=<path.gguf> (looked for gpustack-bge-reranker-v2-m3-gguf-Q4_K_M-*.gguf under ${DATA_ROOT}/models)."
  RERANKERS+=("bge=${BGE}")
fi

if [[ -n "${DOWNLOAD}" ]]; then
  command -v curl      >/dev/null 2>&1 || prereq_fail "curl is required for --download."
  command -v sha256sum >/dev/null 2>&1 || prereq_fail "sha256sum is required for --download."
  for entry in "${CANDIDATES[@]}"; do
    IFS='|' read -r _ repo revision file glob _ <<<"${entry}"
    if find_model "${glob}" "${MODELS_DIR}" "${DATA_ROOT}/models" >/dev/null; then
      log "present: ${file}"
      continue
    fi
    download_candidate "${repo}" "${revision}" "${file}" || exit 5
  done
fi

if [[ "${PRESET}" == "all" ]]; then
  for entry in "${CANDIDATES[@]}"; do
    IFS='|' read -r id _ _ file glob languages <<<"${entry}"
    if found="$(find_model "${glob}" "${MODELS_DIR}" "${DATA_ROOT}/models")"; then
      RERANKERS+=("${id}=${found}${languages:+@${languages}}")
    else
      log "preset all: ${file} not found (pass --download to fetch it); measuring without ${id}."
    fi
  done
fi

RERANKER_ENV=""
for entry in "${RERANKERS[@]}"; do
  [[ "${entry}" == *=* && -n "${entry%%=*}" && -n "${entry#*=}" ]] || prereq_fail "--reranker '${entry}' is not id=path."
  path="${entry#*=}"
  suffix=""
  # The same rule as RetrievalEvalLiveSettings.ParseRerankers: only 2-3 letter codes after the last @ are a suffix.
  if [[ "${path}" =~ ^(.+)@([A-Za-z]{2,3}(,[A-Za-z]{2,3})*)$ ]]; then
    path="${BASH_REMATCH[1]}"
    suffix="@${BASH_REMATCH[2]}"
  fi
  [[ -f "${path}" ]] || prereq_fail "reranker '${entry%%=*}': ${path} does not exist."
  RERANKER_ENV+="${RERANKER_ENV:+;}${entry%%=*}=$(realpath "${path}")${suffix}"
done
[[ -z "${CHAT_PATH}" || -f "${CHAT_PATH}" ]] || prereq_fail "--chat ${CHAT_PATH} does not exist."
[[ -z "${CORPUS_DIR}" || -d "${CORPUS_DIR}" ]] || prereq_fail "--corpus ${CORPUS_DIR} is not a directory."

SERVER_PATH="$(realpath "${SERVER_PATH}")"

# A host's StaleProcessReaper kills every llama-server whose EXECUTABLE lies under its managed data root, so a host
# starting mid-eval would kill servers run from there (docs/agent-knowledge/dev-runtime.md). Such a server runs from a
# private copy, with the llama/ggml libraries beside it: the build's RUNPATH is $ORIGIN, so the copy loads its own.
DATA_ROOT_REAL="$(realpath -m "${DATA_ROOT}")"
if [[ "${SERVER_PATH}" == "${DATA_ROOT_REAL}/"* ]]; then
  SERVER_COPY_DIR="${PROJECT_ROOT}/.tmp/retrieval-eval/llama-bin-${RUN_ID}"
  mkdir -p "${SERVER_COPY_DIR}"
  server_dir="$(dirname "${SERVER_PATH}")"
  cp -P -- "${SERVER_PATH}" "${SERVER_COPY_DIR}/" \
    || prereq_fail "could not copy ${SERVER_PATH} to ${SERVER_COPY_DIR}."
  shopt -s nullglob
  libs=("${server_dir}"/lib*.so*)
  shopt -u nullglob
  if [[ "${#libs[@]}" -gt 0 ]]; then
    cp -P -- "${libs[@]}" "${SERVER_COPY_DIR}/" || prereq_fail "could not copy the libraries beside ${SERVER_PATH}."
  fi
  SERVER_PATH="${SERVER_COPY_DIR}/$(basename "${SERVER_PATH}")"
  # Only the executable path matters to the reaper; a library resolving back to the build dir would still be safe.
  if command -v ldd >/dev/null 2>&1 && LDD_OUT="$(ldd "${SERVER_PATH}" 2>/dev/null)" && grep -q 'not found' <<<"${LDD_OUT}"; then
    prereq_fail "the private llama-server copy cannot load its libraries:
$(grep 'not found' <<<"${LDD_OUT}")"
  fi
  log "llama-server copied out of the data root (host reapers key on it): ${SERVER_COPY_DIR}"
fi
EMBED_PATH="$(realpath "${EMBED_PATH}")"
REPORT_DIR="$(realpath -m "${REPORT_DIR:-${PROJECT_ROOT}/.tmp/retrieval-eval/report-$(date -u +%Y%m%d-%H%M%S)}")"
REPORT_JSON="${REPORT_DIR}/retrieval-eval.json"
REPORT_MD="${REPORT_DIR}/retrieval-eval.md"
# Starting from an absent report is what makes "the JSON exists" proof that the test ran.
rm -f "${REPORT_JSON}" "${REPORT_MD}"

log "llama-server: ${SERVER_PATH}"
log "embed:        ${EMBED_PATH}"
log "rerankers:    ${RERANKER_ENV}"
[[ -n "${CHAT_PATH}" ]] && log "chat:         ${CHAT_PATH} (contention round)"
log "report:       ${REPORT_DIR}"

PROJECT="${PROJECT_ROOT}/XE-Local-AI-Engine.Client.Persistence.Tests/XE-Local-AI-Engine.Client.Persistence.Tests.csproj"
export MSBUILDDISABLENODEREUSE=1
export NUGET_PACKAGES="${NUGET_PACKAGES:-${HOME}/.nuget/packages}"

# Release only: Debug skips the analyzers (docs/agent-knowledge.md §1).
if [[ -z "${NO_BUILD}" ]]; then
  log "=== Building Client.Persistence.Tests (Release) ==="
  if ! dotnet build "${PROJECT}" --configuration Release; then
    fail "the test project did not build in Release."
    exit 1
  fi
fi

if [[ -z "${NO_GUARD:-}" ]]; then
  RUNNER=("${PROJECT_ROOT}/scripts/assembly-guard.sh" guard --test-bins --)
else
  RUNNER=()
fi

OUT_FILE="$(mktemp -t xe-retrieval-eval-out-XXXXXX.log)"
log "=== Running RetrievalEvalLiveTests ==="
XE_RETRIEVAL_EVAL_SERVER="${SERVER_PATH}" \
XE_RETRIEVAL_EVAL_EMBED_MODEL="${EMBED_PATH}" \
XE_RETRIEVAL_EVAL_RERANKERS="${RERANKER_ENV}" \
XE_RETRIEVAL_EVAL_REPORT="${REPORT_DIR}" \
XE_RETRIEVAL_EVAL_NGL="${NGL}" \
XE_RETRIEVAL_EVAL_K="${K}" \
XE_RETRIEVAL_EVAL_CHAT_MODEL="${CHAT_PATH:+$(realpath "${CHAT_PATH}")}" \
XE_RETRIEVAL_EVAL_CORPUS_DIR="${CORPUS_DIR:+$(realpath "${CORPUS_DIR}")}" \
XE_RETRIEVAL_EVAL_RUN_ID="${RUN_ID}" \
  ${RUNNER[@]+"${RUNNER[@]}"} timeout --signal=TERM --kill-after=30s "${XE_RETRIEVAL_EVAL_TIMEOUT:-90m}" \
  dotnet test "${PROJECT}" --configuration Release --no-build \
  --treenode-filter '/*/*/RetrievalEvalLiveTests/*' >"${OUT_FILE}" 2>&1 &
TEST_PID=$!
# Output goes to a file, not a pipe: an orphan that still holds stdout can then never hang the runner.
tail --pid="${TEST_PID}" -n +1 -f "${OUT_FILE}"
wait "${TEST_PID}"
TEST_STATUS=$?
reap_ours

if [[ "${TEST_STATUS}" -eq 75 ]]; then
  log "CONTAMINATED: the test assemblies changed mid-run. This result is VOID, not red — re-run it."
  exit 75
fi

if [[ ! -s "${REPORT_JSON}" ]]; then
  if grep -qE 'before becoming healthy|did not report healthy|Could not start llama-server' "${OUT_FILE}"; then
    fail "a llama-server did not start or become healthy (infrastructure, nothing was judged). See the output above."
    exit 5
  fi
  if [[ "${TEST_STATUS}" -eq 124 ]]; then
    fail "the run hit the ${XE_RETRIEVAL_EVAL_TIMEOUT:-90m} timeout before writing a report."
  elif grep -qiE 'skipped:[[:space:]]*[1-9]' "${OUT_FILE}"; then
    fail "the test SKIPPED itself — the gate variables did not reach it. This is not a pass."
  elif ! grep -qiE 'total:[[:space:]]*[1-9]' "${OUT_FILE}"; then
    fail "no test ran (zero tests or no MTP summary). This is not a pass."
  else
    fail "the test exited ${TEST_STATUS} without writing ${REPORT_JSON}."
  fi
  exit 1
fi

# The test writes the report in a finally block, so an aborted run still leaves a JSON behind:
# Completed=false (or missing) is never a pass.
if [[ "$(jq -r '.Completed // false' "${REPORT_JSON}" 2>/dev/null)" != "true" ]]; then
  ABORT_REASON="$(jq -r '.AbortReason // "no abort reason recorded"' "${REPORT_JSON}" 2>/dev/null)"
  log "report: ${REPORT_JSON}"
  log "report: ${REPORT_MD}"
  if [[ "${TEST_STATUS}" -eq 124 ]]; then
    fail "the run hit the ${XE_RETRIEVAL_EVAL_TIMEOUT:-90m} timeout before completing: ${ABORT_REASON}"
    exit 1
  fi
  # The test prefixes exactly its infrastructure aborts with "infra:"; free text here could be a product message.
  if grep -qE '^infra:' <<<"${ABORT_REASON}"; then
    fail "the run aborted on infrastructure (nothing was judged): ${ABORT_REASON}"
    exit 5
  fi
  fail "the run did not complete: ${ABORT_REASON}"
  exit 1
fi

STATUS=0
if ! INVALID="$(jq -r '.Configs[] | select((.Valid | not) and (.Id != "NEG-dead") and (.Id != "NEG-shuffle")) | "  - \(.Id): \(.InvalidReason // "no reason")"' "${REPORT_JSON}")" \
   || ! CONTROLS="$(jq -r '.NegativeControls[] | select(.Passed | not) | "  - \(.Name): \(.Detail)"' "${REPORT_JSON}")"; then
  fail "${REPORT_JSON} is not a readable report."
  exit 1
fi
if [[ -n "${INVALID}" ]]; then
  fail "INVALID configs (not quality results):"
  echo "${INVALID}" >&2
  STATUS=1
fi
if [[ -n "${CONTROLS}" ]]; then
  fail "negative controls failed:"
  echo "${CONTROLS}" >&2
  STATUS=1
fi
if [[ "${TEST_STATUS}" -ne 0 ]]; then
  fail "the test run exited ${TEST_STATUS}. Read the assertion message above."
  STATUS=1
fi

# A requested contention round the box had no headroom for is a visible infra skip (PLAN §4.4): the
# quality numbers stand, but the round the operator asked for did not run.
CONTENTION_SKIP="$(jq -r '.Contention.SkippedReason // empty' "${REPORT_JSON}")"
if [[ "${STATUS}" -eq 0 && -n "${CHAT_PATH}" && -n "${CONTENTION_SKIP}" ]]; then
  fail "the contention round was skipped (infrastructure): ${CONTENTION_SKIP}"
  STATUS=5
fi

echo
log "report: ${REPORT_JSON}"
log "report: ${REPORT_MD}"
if [[ "${STATUS}" -eq 0 ]]; then
  log "Live retrieval eval PASSED."
fi
exit "${STATUS}"
