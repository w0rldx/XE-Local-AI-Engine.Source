#!/usr/bin/env bash
#
# Contract tests for scripts/run-model-matrix-local.sh. No node, no model, no GPU.
#
# Two layers, as in gpu-smoke.test.sh:
#   1. The runner is sourced with XE_MODEL_MATRIX_LIB_ONLY=1 and its verdict function is fed results.json fixtures:
#      a failed hard check fails, a low measured value does not, and a run with nothing in it is never a pass.
#   2. End-to-end runs with fakes on PATH (dotnet, aspire, nvidia-smi), a two-model test manifest and a scratch models
#      directory prove the gates before and around the host: tier resolution, missing model, hash mismatch, a host that
#      is already up, a failed start, and teardown on an interrupt.
# The driver's pure logic (tiers, wire rules, verdict) has its own unit tests in test_model_matrix_driver.py.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd -P)"
RUNNER="${SCRIPT_DIR}/run-model-matrix-local.sh"
TEMP_ROOT="$(mktemp -d)"
RUNNER_PID=""
NODE_PID=""
FOREIGN_PID=""
# Nothing backgrounded may outlive the test, whatever happens below.
cleanup_test() {
  local pid
  for pid in ${RUNNER_PID} ${NODE_PID} ${FOREIGN_PID}; do kill "${pid}" 2>/dev/null; done
  rm -rf -- "${TEMP_ROOT}"
}
trap cleanup_test EXIT

FAILED=0
CHECKS=0

check() {
  local label="$1" expected="$2" actual="$3"
  CHECKS=$((CHECKS + 1))
  [[ "${expected}" == "${actual}" ]] && return 0
  echo "  FAIL: ${label}: expected '${expected}', got '${actual}'" >&2
  FAILED=$((FAILED + 1))
}

check_contains() {
  local label="$1" needle="$2" haystack="$3"
  CHECKS=$((CHECKS + 1))
  [[ "${haystack}" == *"${needle}"* ]] && return 0
  echo "  FAIL: ${label}: output did not contain '${needle}'" >&2
  echo "        got: ${haystack}" >&2
  FAILED=$((FAILED + 1))
}

check_absent() {
  local label="$1" needle="$2" haystack="$3"
  CHECKS=$((CHECKS + 1))
  [[ "${haystack}" != *"${needle}"* ]] && return 0
  echo "  FAIL: ${label}: output unexpectedly contained '${needle}'" >&2
  FAILED=$((FAILED + 1))
}

echo "== verdict (layer 1) =="

# The runner ends in a top-level exit the linter cannot see past the lib-only guard; it is linted as its own target.
# shellcheck source=/dev/null
XE_MODEL_MATRIX_LIB_ONLY=1 source "${RUNNER}"

verdict_of() {
  printf '%s' "$1" >"${TEMP_ROOT}/results.json"
  judge_results "${TEMP_ROOT}/results.json" >/dev/null 2>&1
  echo "$?"
}

check "missing results.json fails" "1" "$(judge_results "${TEMP_ROOT}/absent.json" >/dev/null 2>&1; echo "$?")"
check "a run with no models fails" "1" "$(verdict_of '{"models": []}')"
check "only n/a hard checks fail" "1" \
  "$(verdict_of '{"models": [{"id": "m", "checks": {"moe-placement": {"kind": "hard", "verdict": "n/a"}}}]}')"
check "a failed hard check fails" "1" \
  "$(verdict_of '{"models": [{"id": "m", "checks": {"load-chat": {"kind": "hard", "verdict": "pass"}, "eject": {"kind": "hard", "verdict": "fail", "detail": "still running"}}}]}')"
check "a low measured value does not fail" "0" \
  "$(verdict_of '{"models": [{"id": "m", "checks": {"load-chat": {"kind": "hard", "verdict": "pass"}, "multi-tool": {"kind": "measured", "verdict": "recorded", "value": "0/3"}}}]}')"
out="$(judge_results "${TEMP_ROOT}/results.json" 2>&1)"
check_contains "the verdict counts hard checks" "1/1 hard checks passed" "${out}"

echo "== gates (layer 2, end to end with fakes) =="

mkdir -p "${TEMP_ROOT}/bin" "${TEMP_ROOT}/models"
FAKE_LOG="${TEMP_ROOT}/fake-calls.log"
: >"${FAKE_LOG}"

cat >"${TEMP_ROOT}/bin/dotnet" <<'FAKE_DOTNET'
#!/usr/bin/env bash
[[ "${1:-}" == "--version" ]] && printf '10.0.100-test\n'
exit 0
FAKE_DOTNET

# A box without a GPU.
cat >"${TEMP_ROOT}/bin/nvidia-smi" <<'FAKE_NO_GPU'
#!/usr/bin/env bash
exit 1
FAKE_NO_GPU

APPHOST="${PROJECT_ROOT}/XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj"
# Behaviour by FAKE_ASPIRE_MODE: idle (nothing runs), running (this checkout's host is up), start-fails, start-slow.
cat >"${TEMP_ROOT}/bin/aspire" <<FAKE_ASPIRE
#!/usr/bin/env bash
echo "\$1" >>"${FAKE_LOG}"
case "\${FAKE_ASPIRE_MODE:-idle}:\$1" in
  running:ps) printf '[{"appHostPath":"%s","appHostPid":424242,"status":"running","dashboardUrl":"https://localhost:1/login?t=x"}]\n' "${APPHOST}" ;;
  running:describe) printf '{"resources":[{"displayName":"app","resourceType":"Project","state":"Running","healthStatus":"Healthy","urls":[]}]}\n' ;;
  *:ps) printf '[]\n' ;;
  start-fails:start) echo "simulated aspire start failure" >&2; exit 1 ;;
  start-slow:start) : >"${TEMP_ROOT}/start-reached"; sleep 3; exit 1 ;;
esac
exit 0
FAKE_ASPIRE
chmod 700 "${TEMP_ROOT}/bin/dotnet" "${TEMP_ROOT}/bin/nvidia-smi" "${TEMP_ROOT}/bin/aspire"

# A two-tier test manifest over two tiny "weight files", installed the way the product does it: file + sidecar.
sha_of() { printf '%s' "$1" | sha256sum | awk '{ print $1 }'; }
install_model() {
  local name="$1" file="$2" content="$3"
  printf '%s' "${content}" >"${TEMP_ROOT}/models/${file}"
  printf '{"ModelName": "%s", "LocalFileName": "%s"}' "${name}" "${file}" >"${TEMP_ROOT}/models/${file}.xe-model.json"
}
cat >"${TEMP_ROOT}/manifest.json" <<MANIFEST
{"models": [
  {"id": "small", "repo": "org/Small-GGUF", "file": "small.gguf", "sha256": "$(sha_of small-weights)", "sizeBytes": 13,
   "modelName": "org/Small-GGUF:Q4_K_M", "moe": false, "thinkingOff": "template",
   "toolCapable": true, "tiers": ["fast", "extended", "rc"]},
  {"id": "bigger", "repo": "org/Bigger-GGUF", "file": "bigger.gguf", "sha256": "$(sha_of bigger-weights)",
   "sizeBytes": 14, "modelName": "org/Bigger-GGUF:Q4_K_M", "moe": true, "thinkingOff": "budget",
   "toolCapable": true, "tiers": ["extended", "rc"]}
]}
MANIFEST
install_model "org/Small-GGUF:Q4_K_M" "org-small-q4.gguf" "small-weights"

run_matrix() {
  PATH="${TEMP_ROOT}/bin:${PATH}" NO_BUILD_LOCK=1 NO_GUARD=1 XE_ASPIRE_APPHOST="${APPHOST}" \
    XE_MODEL_MATRIX_MANIFEST="${TEMP_ROOT}/manifest.json" HuggingFace__ModelsDirectory="${TEMP_ROOT}/models" \
    "${RUNNER}" --evidence "${TEMP_ROOT}/evidence" "$@" 2>&1
}

out="$(run_matrix --tier nightly)"; status=$?
check "an unknown tier => exit 2" "2" "${status}"

# The manifest must say how a thinking model is switched off; a value the checks cannot judge is refused up front.
sed 's/"budget"/"maybe"/' "${TEMP_ROOT}/manifest.json" >"${TEMP_ROOT}/bad-manifest.json"
out="$(PATH="${TEMP_ROOT}/bin:${PATH}" NO_BUILD_LOCK=1 XE_MODEL_MATRIX_MANIFEST="${TEMP_ROOT}/bad-manifest.json" \
  HuggingFace__ModelsDirectory="${TEMP_ROOT}/models" "${RUNNER}" --variant cpu 2>&1)"; status=$?
check "an unknown thinkingOff value => exit 2" "2" "${status}"
check_contains "the bad entry is named" "thinkingOff must be template, budget or null: bigger" "${out}"

out="$(run_matrix --variant cpu --checks eject,bogus)"; status=$?
check "an unknown check => exit 2" "2" "${status}"

out="$(run_matrix)"; status=$?
check "--variant cuda on a box without a GPU => exit 2" "2" "${status}"
check_contains "the missing GPU is named" "no GPU" "${out}"

# Tier resolution through the runner: extended adds `bigger`, which is not installed, so only it is reported.
out="$(run_matrix --variant cpu --tier extended)"; status=$?
check "a model of the tier missing, no --download => exit 2" "2" "${status}"
check_contains "the missing model is named with the way out" "not installed: bigger. Install them, or re-run with --download" "${out}"
check_contains "the tier resolves to both models" "models     small,bigger (tier extended" "${out}"
check_absent "nothing was started" "Starting this checkout's AppHost" "${out}"

# The fast tier is `small` alone and installed; tamper with its bytes.
printf '%s' "tampered" >"${TEMP_ROOT}/models/org-small-q4.gguf"
out="$(run_matrix --variant cpu --tier fast)"; status=$?
check "a hash mismatch => exit 2" "2" "${status}"
check_contains "the mismatch says it is not the pinned artifact" "is not the pinned one" "${out}"
install_model "org/Small-GGUF:Q4_K_M" "org-small-q4.gguf" "small-weights"

out="$(FAKE_ASPIRE_MODE=running run_matrix --variant cpu)"; status=$?
check "a host already running => exit 3" "3" "${status}"
check_contains "the verified model was hashed first" "verified   small sha256 matches" "${out}"

: >"${FAKE_LOG}"
out="$(FAKE_ASPIRE_MODE=start-fails run_matrix --variant cpu)"; status=$?
check "a failed AppHost start => exit 5, not 1" "5" "${status}"
check_absent "an infrastructure abort prints no verdict" "RESULT:" "${out}"
check_contains "the started host is torn down after a failed start" "cleanup: stopping the AppHost" "${out}"

# Interrupt while the host is starting: the run must exit 130 and still run the scoped teardown.
: >"${FAKE_LOG}"
rm -f -- "${TEMP_ROOT}/start-reached"
# Started directly, not through run_matrix: the signal must reach the runner itself, not a wrapping subshell.
PATH="${TEMP_ROOT}/bin:${PATH}" NO_BUILD_LOCK=1 NO_GUARD=1 XE_ASPIRE_APPHOST="${APPHOST}" FAKE_ASPIRE_MODE=start-slow \
  XE_MODEL_MATRIX_MANIFEST="${TEMP_ROOT}/manifest.json" HuggingFace__ModelsDirectory="${TEMP_ROOT}/models" \
  "${RUNNER}" --evidence "${TEMP_ROOT}/evidence" --variant cpu >"${TEMP_ROOT}/interrupt.out" 2>&1 &
RUNNER_PID=$!
for _ in $(seq 1 300); do
  [[ -e "${TEMP_ROOT}/start-reached" ]] && break
  sleep 0.1
done
kill -TERM "${RUNNER_PID}" 2>/dev/null
wait "${RUNNER_PID}"
status=$?
RUNNER_PID=""
out="$(cat "${TEMP_ROOT}/interrupt.out")"
check "an interrupt => exit 130" "130" "${status}"
check_contains "an interrupt still tears the host down" "cleanup: stopping the AppHost" "${out}"
calls="$(paste -sd' ' "${FAKE_LOG}")"
check_contains "teardown asked aspire for this checkout's instance after the start" "start ps" "${calls}"

echo "== the driver against a fake node (layer 3) =="

# Just enough of the node API for `run`: login, offline profile, the model listed, nothing running, and a device audit
# read from audit.json on every request so each case can set the backend the node reports.
cat >"${TEMP_ROOT}/fake-node.py" <<'FAKE_NODE'
import http.server, json, os, sys
AUDIT = os.path.join(os.path.dirname(sys.argv[1]), "audit.json")
ROUTES = {
    "/api/local/v1/auth/status": {"setupRequired": False},
    "/api/local/v1/auth/login": {"accessToken": "fake-token"},
    "/api/local/v1/node-settings": {"externalAccessProfile": "offline"},
    "/api/local/v1/models": {"items": [{"modelName": sys.argv[2]}]},
    "/api/local/v1/model-fit/running": {"items": []},
}
class Handler(http.server.BaseHTTPRequestHandler):
    def reply(self):
        self.rfile.read(int(self.headers.get("Content-Length") or 0))
        path = self.path.split("?")[0]
        body = json.load(open(AUDIT)) if path.endswith("/model-fit/hardware-profile") else ROUTES.get(path)
        data = json.dumps(body).encode() if body is not None else b""
        self.send_response(200 if body is not None else 404)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)
    do_GET = do_POST = do_PUT = reply
    def log_message(self, *_):
        pass
server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
with open(sys.argv[1] + ".tmp", "w") as handle:
    handle.write(str(server.server_address[1]))
os.replace(sys.argv[1] + ".tmp", sys.argv[1])
server.serve_forever()
FAKE_NODE
python3 "${TEMP_ROOT}/fake-node.py" "${TEMP_ROOT}/node.port" "org/Small-GGUF:Q4_K_M" &
NODE_PID=$!
for _ in $(seq 1 100); do
  [[ -s "${TEMP_ROOT}/node.port" ]] && break
  sleep 0.1
done
NODE_URL="http://127.0.0.1:$(cat "${TEMP_ROOT}/node.port" 2>/dev/null)"

# A llama-server serving the same model file that this checkout's node did not spawn (another checkout's, say).
(exec -a "${TEMP_ROOT}/llama-server" python3 -c 'import time; time.sleep(600)' -m "${TEMP_ROOT}/models/org-small-q4.gguf") &
FOREIGN_PID=$!

run_driver() {
  PATH="${TEMP_ROOT}/bin:${PATH}" python3 "${SCRIPT_DIR}/model-matrix-driver.py" run --base-url "${NODE_URL}" \
    --manifest "${TEMP_ROOT}/manifest.json" --ids small --models-dir "${TEMP_ROOT}/models" \
    --evidence "${TEMP_ROOT}/driver-evidence" "$@" 2>&1
}

set_audit() {
  printf '{"inferenceBackend": "%s", "gpuExpected": true, "cpuFallback": %s, "cpuFallbackReason": %s,
    "cpuFallbackRemediation": null, "gpuVendor": "nvidia", "gpuAccelAvailable": true, "vramBytes": 1,
    "vramKnown": true}' "$1" "$2" "$3" >"${TEMP_ROOT}/audit.json"
}

# The requested variant must be the backend the node runs, before any check is judged.
set_audit cuda false null
out="$(run_driver --variant cpu --checks eject)"; status=$?
check "requested cpu, node runs cuda => exit 2" "2" "${status}"
check_contains "the mismatch says what was requested and observed" \
  "--variant cpu was requested, but the node's device audit reports backend 'cuda'" "${out}"
check_contains "the mismatch says how to fix it" "supply a CPU llama-server through XE_LLAMACPP_SERVER_PATH" "${out}"
check "a refused run judges nothing" "absent" "$([[ -e "${TEMP_ROOT}/driver-evidence/results.json" ]] && echo present || echo absent)"

set_audit cpu true '"the CUDA runtime sees no device"'
out="$(run_driver --variant cuda --checks eject)"; status=$?
check "requested cuda, node in CPU fallback => exit 2" "2" "${status}"
check_contains "the fallback and its reason are named" \
  "backend 'cpu' with a CPU fallback (the CUDA runtime sees no device)" "${out}"

set_audit cpu false null
rm -rf -- "${TEMP_ROOT}/driver-evidence"
out="$(run_driver --variant cpu --checks eject)"; status=$?
check "the matching backend runs the checks" "0" "${status}"
backend="$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1]))["backend"]["inferenceBackend"])' \
  "${TEMP_ROOT}/driver-evidence/results.json" 2>/dev/null)"
check "results.json records the observed backend" "cpu" "${backend}"
check_contains "summary.md records the observed backend" "(device audit: backend \`cpu\`)" \
  "$(cat "${TEMP_ROOT}/driver-evidence/summary.md" 2>/dev/null)"
eject="$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1]))["models"][0]["checks"]["eject"]["verdict"])' \
  "${TEMP_ROOT}/driver-evidence/results.json" 2>/dev/null)"
check "a foreign llama-server of the same file does not hold up eject" "pass" "${eject}"
check "the foreign process was left alone" "0" "$(kill -0 "${FOREIGN_PID}" 2>/dev/null; echo "$?")"

# The runner's teardown stops leftover llama-servers of its own node only: a foreign one survives it.
out="$(FAKE_ASPIRE_MODE=start-fails run_matrix --variant cpu)"; status=$?
check "the teardown run still exits 5" "5" "${status}"
check_absent "the teardown stops no foreign llama-server" "cleanup: stopping llama-server" "${out}"
check "a foreign llama-server survives the runner's teardown" "0" "$(kill -0 "${FOREIGN_PID}" 2>/dev/null; echo "$?")"

echo
if [[ "${FAILED}" -ne 0 ]]; then
  echo "model-matrix.test.sh: ${FAILED} of ${CHECKS} checks FAILED" >&2
  exit 1
fi
if [[ "${CHECKS}" -eq 0 ]]; then
  echo "model-matrix.test.sh: ZERO checks ran — this is not a pass." >&2
  exit 1
fi
echo "model-matrix.test.sh: ${CHECKS} checks passed"
echo "model-matrix.test.sh: PASS"
