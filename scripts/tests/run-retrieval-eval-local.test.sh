#!/usr/bin/env bash
#
# Behavioural tests for scripts/run-retrieval-eval-local.sh.
#
# The runner's job is to refuse a green nobody earned: the live test SKIPS with exit 0 when it is not
# gated, and an INVALID config must never be quoted as a quality number. This suite drives those
# paths with a fake `dotnet` on PATH and a fake data root, so it needs no GPU, no models and no build.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
RUNNER="${SCRIPT_DIR}/run-retrieval-eval-local.sh"
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

# A fake node data root: a source-build llama-server and the two default models.
DATA_ROOT="${TEMP_ROOT}/data"
mkdir -p "${DATA_ROOT}/llama.cpp/source-build/active/build/bin" "${DATA_ROOT}/models"
printf '#!/usr/bin/env bash\nexit 0\n' >"${DATA_ROOT}/llama.cpp/source-build/active/build/bin/llama-server"
chmod 700 "${DATA_ROOT}/llama.cpp/source-build/active/build/bin/llama-server"
: >"${DATA_ROOT}/llama.cpp/source-build/active/build/bin/libggml.so.0.1"
ln -s libggml.so.0.1 "${DATA_ROOT}/llama.cpp/source-build/active/build/bin/libggml.so.0"
: >"${DATA_ROOT}/models/nomic-ai-nomic-embed-text-v1.5-gguf-F16-abc.gguf"
: >"${DATA_ROOT}/models/gpustack-bge-reranker-v2-m3-gguf-Q4_K_M-abc.gguf"
MODELS_DIR="${TEMP_ROOT}/scratch-models"
mkdir -p "${MODELS_DIR}"
: >"${MODELS_DIR}/qwen3-reranker-0.6b-q8_0.gguf"
STAMP="${TEMP_ROOT}/stamp"
: >"${STAMP}"

# The fake dotnet: `build` succeeds; `test` records the env it received and behaves per FAKE_MODE.
mkdir -p "${TEMP_ROOT}/bin"
cat >"${TEMP_ROOT}/bin/dotnet" <<'FAKE_DOTNET'
#!/usr/bin/env bash
[[ "${1:-}" == "build" ]] && exit 0
env | grep '^XE_RETRIEVAL_EVAL_' >"${FAKE_ENV_FILE}"
# What the server path held while the test ran: the executable and the libraries beside it.
server_dir="$(dirname "${XE_RETRIEVAL_EVAL_SERVER}")"
[[ -x "${XE_RETRIEVAL_EVAL_SERVER}" ]] && echo "SERVER_RUNNABLE=yes" >>"${FAKE_ENV_FILE}"
[[ -L "${server_dir}/libggml.so.0" && -f "${server_dir}/libggml.so.0.1" ]] && echo "SERVER_LIBS=yes" >>"${FAKE_ENV_FILE}"
report="${XE_RETRIEVAL_EVAL_REPORT}/retrieval-eval.json"
write_report() {
  mkdir -p "${XE_RETRIEVAL_EVAL_REPORT}"
  printf '%s\n' "$1" >"${report}"
  printf '# report\n' >"${XE_RETRIEVAL_EVAL_REPORT}/retrieval-eval.md"
}
neg='{"Id":"NEG-dead","Valid":false,"InvalidReason":"forced rerank degraded"}'
ok_controls='[{"Name":"dead-endpoint detected INVALID","Passed":true,"Detail":"x"},{"Name":"shuffled scores below F0","Passed":true,"Detail":"y"}]'
case "${FAKE_MODE}" in
  skip)    echo "Test run summary: Passed! total: 1 failed: 0 succeeded: 0 skipped: 1"; exit 0 ;;
  zero)    echo "Test run summary: Zero tests ran total: 0"; exit 8 ;;
  nojson)  echo "Test run summary: Failed! total: 1 failed: 1 succeeded: 0 skipped: 0"; exit 2 ;;
  infra)   echo "llama-server exited with code 1 before becoming healthy."; echo "total: 1 failed: 1"; exit 2 ;;
  contaminated) exit 75 ;;
  invalid)
    write_report "{\"Completed\":true,\"Configs\":[{\"Id\":\"F0\",\"Valid\":true},{\"Id\":\"qwen320\",\"Valid\":false,\"InvalidReason\":\"sanity gate failed\"},${neg}],\"NegativeControls\":${ok_controls}}"
    echo "total: 1 failed: 0 succeeded: 1 skipped: 0"; exit 0 ;;
  negid)
    write_report "{\"Completed\":true,\"Configs\":[{\"Id\":\"F0\",\"Valid\":true},{\"Id\":\"NEG-candidate-full\",\"Valid\":false,\"InvalidReason\":\"sanity gate failed\"},${neg}],\"NegativeControls\":${ok_controls}}"
    echo "total: 1 failed: 0 succeeded: 1 skipped: 0"; exit 0 ;;
  control)
    write_report "{\"Completed\":true,\"Configs\":[{\"Id\":\"F0\",\"Valid\":true},${neg}],\"NegativeControls\":[{\"Name\":\"shuffled scores below F0\",\"Passed\":false,\"Detail\":\"MRR 0.9 vs F0 0.5\"}]}"
    echo "total: 1 failed: 1 succeeded: 0 skipped: 0"; exit 2 ;;
  aborted-infra)
    write_report '{"Completed":false,"AbortReason":"infra: llama-server did not report healthy within 300s","Configs":[],"NegativeControls":[]}'
    echo "total: 1 failed: 1 succeeded: 0 skipped: 0"; exit 2 ;;
  aborted-insufficient)
    write_report '{"Completed":false,"AbortReason":"InvalidOperationException: insufficient scores, did not report healthy","Configs":[],"NegativeControls":[]}'
    echo "total: 1 failed: 1 succeeded: 0 skipped: 0"; exit 2 ;;
  aborted-timeout)
    write_report '{"Completed":false,"AbortReason":"TaskCanceledException: A task was canceled.","Configs":[],"NegativeControls":[]}'
    exit 124 ;;
  aborted)
    write_report '{"Completed":false,"AbortReason":"the live corpus loaded zero queries","Configs":[],"NegativeControls":[]}'
    echo "total: 1 failed: 1 succeeded: 0 skipped: 0"; exit 2 ;;
  no-completed)
    write_report "{\"Configs\":[{\"Id\":\"F0\",\"Valid\":true}],\"NegativeControls\":${ok_controls}}"
    echo "total: 1 failed: 0 succeeded: 1 skipped: 0"; exit 0 ;;
  orphan)
    # A test host that outlives `dotnet test` and still holds its stdout.
    sleep 300 &
    write_report "{\"Completed\":true,\"Configs\":[{\"Id\":\"F0\",\"Valid\":true}],\"NegativeControls\":${ok_controls}}"
    echo "total: 1 failed: 0 succeeded: 1 skipped: 0"; exit 0 ;;
  contention-skip)
    write_report "{\"Completed\":true,\"Configs\":[{\"Id\":\"F0\",\"Valid\":true}],\"NegativeControls\":${ok_controls},\"Contention\":{\"SkippedReason\":\"insufficient headroom: 2 GB free\"}}"
    echo "total: 1 failed: 0 succeeded: 1 skipped: 0"; exit 0 ;;
  hang)
    sleep 300 ;;
  happy)
    write_report "{\"Completed\":true,\"Configs\":[{\"Id\":\"F0\",\"Valid\":true},{\"Id\":\"bge20\",\"Valid\":true},${neg}],\"NegativeControls\":${ok_controls}}"
    echo "total: 1 failed: 0 succeeded: 1 skipped: 0"; exit 0 ;;
esac
exit 99
FAKE_DOTNET
chmod 700 "${TEMP_ROOT}/bin/dotnet"

export FAKE_ENV_FILE="${TEMP_ROOT}/env" TEMP_ROOT DATA_ROOT MODELS_DIR RUNNER
run() {
  env -u XE_LLAMACPP_SERVER_PATH PATH="${TEMP_ROOT}/bin:${PATH}" NO_BUILD_LOCK=1 NO_GUARD=1 FAKE_MODE="${FAKE_MODE:-happy}" \
    "${RUNNER}" --data-root "${DATA_ROOT}" --models-dir "${MODELS_DIR}" --report "${TEMP_ROOT}/report" "$@" 2>&1
}

echo "== usage and prerequisites =="

out="$(run --not-a-flag)"; check "unknown option => 2" "2" "$?"
out="$(run --server)"; check "--server without a value => 2" "2" "$?"
out="$(run --k 0)"; check "--k 0 => 2" "2" "$?"
out="$(run --ngl x)"; check "--ngl non-integer => 2" "2" "$?"
out="$(run --reranker-preset some)"; check "unknown preset => 2" "2" "$?"
out="$(run --reranker broken)"; check "--reranker without id=path => 2" "2" "$?"
out="$(run --reranker q=/does/not/exist.gguf)"; check "a missing reranker file => 2" "2" "$?"
out="$(run --data-root "${TEMP_ROOT}/empty")"; status=$?
check "no llama-server => 2" "2" "${status}"
check_contains "no llama-server is actionable" "no llama-server executable found" "${out}"
out="$(run --embed "${TEMP_ROOT}/missing.gguf")"; check "a missing embedding model => 2" "2" "$?"
out="$(run --chat "${TEMP_ROOT}/missing.gguf")"; check "a missing chat model => 2" "2" "$?"

echo "== a run without a report is never a pass =="

out="$(FAKE_MODE=skip run)"; status=$?
check "a skipped test => 1" "1" "${status}"
check_contains "skip is named" "SKIPPED" "${out}"
out="$(FAKE_MODE=zero run)"; status=$?
check "zero tests => 1" "1" "${status}"
out="$(FAKE_MODE=nojson run)"; status=$?
check "a failed test without JSON => 1" "1" "${status}"
out="$(FAKE_MODE=infra run)"; status=$?
check "a llama-server that never became healthy => 5" "5" "${status}"
out="$(FAKE_MODE=contaminated run)"; status=$?
check "contamination => 75" "75" "${status}"

echo "== aborted runs (report written in a finally block) =="

out="$(FAKE_MODE=aborted-infra run)"; status=$?
check "an infra abort => 5" "5" "${status}"
check_contains "the infra abort reason is shown" "did not report healthy within 300s" "${out}"
check_contains "an aborted run still prints the report path" "report: ${TEMP_ROOT}/report/retrieval-eval.json" "${out}"
out="$(FAKE_MODE=aborted run)"; status=$?
check "a non-infra abort => 1" "1" "${status}"
check_contains "the abort reason is shown" "the live corpus loaded zero queries" "${out}"
out="$(FAKE_MODE=aborted-insufficient run)"; status=$?
check "a product abort whose text mentions insufficient or healthy => 1" "1" "${status}"
out="$(FAKE_MODE=aborted-timeout run)"; status=$?
check "a timed-out run with a report => 1" "1" "${status}"
check_contains "the timeout is named when a report exists" "timeout before completing" "${out}"
out="$(FAKE_MODE=no-completed run)"; status=$?
check "a report without Completed => 1" "1" "${status}"

: >"${TEMP_ROOT}/chat.gguf"
out="$(FAKE_MODE=contention-skip run --chat "${TEMP_ROOT}/chat.gguf")"; status=$?
check "a requested contention round skipped for headroom => 5" "5" "${status}"
check_contains "the headroom skip reason is shown" "insufficient headroom: 2 GB free" "${out}"
out="$(FAKE_MODE=contention-skip run)"; status=$?
check "a contention skip without --chat does not fail the run" "0" "${status}"

echo "== leftover processes =="

# Every process of this run carries the run-id marker the fake recorded; none may survive the runner.
marker_survivors() {
  local marker environ count=0
  marker="$(grep '^XE_RETRIEVAL_EVAL_RUN_ID=' "${FAKE_ENV_FILE}")"
  [[ -n "${marker}" ]] || { echo "no-marker"; return; }
  for environ in /proc/[0-9]*/environ; do
    grep -qzxF "${marker}" "${environ}" 2>/dev/null && count=$((count + 1))
  done
  echo "${count}"
}

out="$(FAKE_MODE=orphan timeout 120 bash -c "$(declare -f run); run")"; status=$?
check "an orphaned test host holding stdout does not hang the runner" "0" "${status}"
check "the orphaned test host is reaped" "0" "$(marker_survivors)"

out="$(FAKE_MODE=hang XE_RETRIEVAL_EVAL_TIMEOUT=2s timeout 120 bash -c "$(declare -f run); run")"; status=$?
check "a hung test run times out => 1" "1" "${status}"
check_contains "the timeout is named" "timeout" "${out}"
check "the hung test process is reaped" "0" "$(marker_survivors)"

echo "== report verdicts =="

out="$(FAKE_MODE=invalid run)"; status=$?
check "an INVALID config => 1" "1" "${status}"
check_contains "the INVALID config is listed with its reason" "qwen320: sanity gate failed" "${out}"
CHECKS=$((CHECKS + 1))
if [[ "${out}" == *"  - NEG-dead"* ]]; then
  echo "  FAIL: NEG-dead is INVALID by design and must not be listed as an INVALID config" >&2
  FAILED=$((FAILED + 1))
fi

out="$(FAKE_MODE=negid run)"; status=$?
check "an INVALID measured row named NEG-* but not a control => 1" "1" "${status}"
check_contains "that row is listed" "NEG-candidate-full: sanity gate failed" "${out}"

out="$(FAKE_MODE=control run)"; status=$?
check "a failed negative control => 1" "1" "${status}"
check_contains "the failed control is listed" "shuffled scores below F0: MRR 0.9 vs F0 0.5" "${out}"

out="$(FAKE_MODE=happy run --ngl 0 --k 7)"; status=$?
check "a clean report => 0" "0" "${status}"
check_contains "the JSON path is printed" "report: ${TEMP_ROOT}/report/retrieval-eval.json" "${out}"
check_contains "the Markdown path is printed" "report: ${TEMP_ROOT}/report/retrieval-eval.md" "${out}"
env_seen="$(cat "${FAKE_ENV_FILE}")"
# A data-root server runs from a private copy: a host's stale-process reaper kills anything under its data root.
server_seen="$(sed -n 's/^XE_RETRIEVAL_EVAL_SERVER=//p' <<<"${env_seen}")"
check_contains "the data-root server reaches the test as a private copy" "/.tmp/retrieval-eval/llama-bin-retrieval-eval-" "${server_seen}"
check "the copy lies outside the data root" "" "$([[ "${server_seen}" == "${DATA_ROOT}/"* ]] && echo inside)"
check_contains "the copy was runnable during the test" "SERVER_RUNNABLE=yes" "${env_seen}"
check_contains "the libraries beside the server were copied, symlinks kept" "SERVER_LIBS=yes" "${env_seen}"
check "the private copy is removed on exit" "gone" "$([[ -e "${server_seen}" ]] || echo gone)"
check_contains "the copy is logged" "llama-server copied out of the data root" "${out}"

OUTSIDE_SERVER="${TEMP_ROOT}/byo/llama-server"
mkdir -p "$(dirname "${OUTSIDE_SERVER}")"
printf '#!/usr/bin/env bash\nexit 0\n' >"${OUTSIDE_SERVER}"
chmod 700 "${OUTSIDE_SERVER}"
out="$(FAKE_MODE=happy run --server "${OUTSIDE_SERVER}")"; status=$?
check "an explicit server outside the data root => 0" "0" "${status}"
check_contains "a server outside the data root is used as is" "XE_RETRIEVAL_EVAL_SERVER=${OUTSIDE_SERVER}" "$(cat "${FAKE_ENV_FILE}")"
out="$(FAKE_MODE=happy run --server "${DATA_ROOT}/llama.cpp/source-build/active/build/bin/llama-server")"; status=$?
check "an explicit server under the data root => 0" "0" "${status}"
check_contains "an explicit server under the data root is copied too" "/.tmp/retrieval-eval/llama-bin-" "$(cat "${FAKE_ENV_FILE}")"
check_contains "the discovered embedder reaches the test" \
  "XE_RETRIEVAL_EVAL_EMBED_MODEL=${DATA_ROOT}/models/nomic-ai-nomic-embed-text-v1.5-gguf-F16-abc.gguf" "${env_seen}"
check_contains "bge is the default reranker" \
  "XE_RETRIEVAL_EVAL_RERANKERS=bge=${DATA_ROOT}/models/gpustack-bge-reranker-v2-m3-gguf-Q4_K_M-abc.gguf" "${env_seen}"
check_contains "--ngl reaches the test" "XE_RETRIEVAL_EVAL_NGL=0" "${env_seen}"
check_contains "--k reaches the test" "XE_RETRIEVAL_EVAL_K=7" "${env_seen}"

out="$(FAKE_MODE=happy run --reranker-preset all)"; status=$?
check "preset all without jina still runs => 0" "0" "${status}"
check_contains "preset all appends the scratch qwen3 after bge" \
  "XE_RETRIEVAL_EVAL_RERANKERS=bge=${DATA_ROOT}/models/gpustack-bge-reranker-v2-m3-gguf-Q4_K_M-abc.gguf;qwen3=${MODELS_DIR}/qwen3-reranker-0.6b-q8_0.gguf" \
  "$(cat "${FAKE_ENV_FILE}")"
check_contains "a missing preset model is named, not silently dropped" "measuring without jina" "${out}"

: >"${MODELS_DIR}/jina-reranker-v1-turbo-en-Q8_0.gguf"
out="$(FAKE_MODE=happy run --reranker-preset all)"; status=$?
check "preset all with jina => 0" "0" "${status}"
check_contains "preset all declares jina English-only" \
  ";jina=${MODELS_DIR}/jina-reranker-v1-turbo-en-Q8_0.gguf@en" "$(cat "${FAKE_ENV_FILE}")"
rm -f "${MODELS_DIR}/jina-reranker-v1-turbo-en-Q8_0.gguf"
out="$(FAKE_MODE=happy run --reranker "x=${MODELS_DIR}/qwen3-reranker-0.6b-q8_0.gguf@en,de")"; status=$?
check "an explicit language suffix => 0" "0" "${status}"
check_contains "an explicit language suffix reaches the test after the resolved path" \
  "XE_RETRIEVAL_EVAL_RERANKERS=x=${MODELS_DIR}/qwen3-reranker-0.6b-q8_0.gguf@en,de" "$(cat "${FAKE_ENV_FILE}")"

# The data root is read-only to the runner: nothing in it may be newer than the fixture.
check "the data root was never written" "" "$(find "${DATA_ROOT}" -newer "${STAMP}" -print)"

echo
if [[ "${FAILED}" -ne 0 ]]; then
  echo "run-retrieval-eval-local.test.sh: ${FAILED} of ${CHECKS} checks FAILED" >&2
  exit 1
fi
if [[ "${CHECKS}" -eq 0 ]]; then
  echo "run-retrieval-eval-local.test.sh: ZERO checks ran — this is not a pass." >&2
  exit 1
fi
echo "run-retrieval-eval-local.test.sh: ${CHECKS} checks passed"
echo "run-retrieval-eval-local.test.sh: PASS"
