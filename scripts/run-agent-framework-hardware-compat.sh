#!/usr/bin/env bash
# Opt-in hardware lane: fixed GGUF + real llama-server through the production invocation path.

set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
GIT_COMMON_DIR="$(realpath "$(git rev-parse --git-common-dir)")"
SHARED_REPO_ROOT="$(dirname "${GIT_COMMON_DIR}")"
SHARED_BUILD_LOCK="${SHARED_REPO_ROOT}/.tmp/build.lock"
MODEL_PATH=""
SERVER_PATH=""
VARIANT="cpu"
OUTPUT="${REPO_ROOT}/docs/agent-framework/evidence/hardware-compatibility.json"

usage() {
  cat <<'EOF'
Usage: scripts/run-agent-framework-hardware-compat.sh --model <gguf> --server <llama-server> [options]

Options:
  --model <path>       Fixed local chat GGUF (required)
  --server <path>      Fixed llama-server executable (required)
  --variant <value>    cpu | cuda | vulkan (default: cpu)
  --output <file>      Sanitized evidence JSON destination
  --help               Show this help

This is opt-in and hardware-gated. It is not part of the deterministic release gate.
The test exercises:
  LocalModelProviderResolver -> LlamaServerLocalModelProvider ->
  Microsoft.Extensions.AI OpenAI adapter -> Microsoft Agent Framework ->
  production InvocationRunner.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --model) MODEL_PATH="$2"; shift 2 ;;
    --server) SERVER_PATH="$2"; shift 2 ;;
    --variant) VARIANT="$2"; shift 2 ;;
    --output) OUTPUT="$2"; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

[[ -n "${MODEL_PATH}" && -f "${MODEL_PATH}" ]] || {
  echo "--model must name an existing GGUF file." >&2
  exit 2
}
[[ -n "${SERVER_PATH}" && -x "${SERVER_PATH}" ]] || {
  echo "--server must name an executable llama-server file." >&2
  exit 2
}
case "${VARIANT}" in
  cpu|cuda|vulkan) ;;
  *) echo "--variant must be cpu, cuda, or vulkan." >&2; exit 2 ;;
esac

MODEL_PATH="$(realpath "${MODEL_PATH}")"
SERVER_PATH="$(realpath "${SERVER_PATH}")"
mkdir -p "$(dirname "${OUTPUT}")"
EVIDENCE_PATH="$(realpath -m "${OUTPUT}")"

export RUN_AGENT_FRAMEWORK_HARDWARE_COMPAT="true"
export RUN_LOCAL_INTEGRATION="true"
export XE_FRAMEWORK_COMPAT_GGUF_PATH="${MODEL_PATH}"
export XE_LLAMACPP_SERVER_PATH="${SERVER_PATH}"
export XE_LLAMACPP_VARIANT="${VARIANT}"
export XE_FRAMEWORK_COMPAT_EVIDENCE_PATH="${EVIDENCE_PATH}"
export XE_OLLAMA_RUNTIME_ENABLED="false"

cd "${REPO_ROOT}"
scripts/with-build-lock.sh --lock-file "${SHARED_BUILD_LOCK}" -- \
  dotnet build XE-Local-AI-Engine.Tests/XE-Local-AI-Engine.Tests.csproj \
    --configuration Release

scripts/with-build-lock.sh --lock-file "${SHARED_BUILD_LOCK}" -- \
  scripts/assembly-guard.sh guard --test-bins -- \
  timeout --signal=TERM --kill-after=30s 20m \
  dotnet test XE-Local-AI-Engine.Tests/XE-Local-AI-Engine.Tests.csproj \
    --configuration Release --no-build --max-parallel-test-modules 1 \
    --treenode-filter '/*/*/AgentFrameworkHardwareCompatibilityTests/*'

[[ -s "${OUTPUT}" ]] || {
  echo "Hardware compatibility test passed without producing evidence at ${OUTPUT}." >&2
  exit 1
}

python3 -m json.tool "${OUTPUT}" >/dev/null
echo "Hardware compatibility evidence written to ${OUTPUT}"
