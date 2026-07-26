#!/usr/bin/env bash
# Fresh, serialized validation for the Agent Framework 1.15 / MEAI closure.

set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
GIT_COMMON_DIR="$(realpath "$(git rev-parse --git-common-dir)")"
SHARED_REPO_ROOT="$(dirname "${GIT_COMMON_DIR}")"
SHARED_BUILD_LOCK="${SHARED_REPO_ROOT}/.tmp/build.lock"
OUTPUT_DIR="${REPO_ROOT}/docs/agent-framework/evidence/validation"
WITH_LINUX_PACKAGE="false"

usage() {
  cat <<'EOF'
Usage: scripts/run-agent-framework-validation.sh [options]

Options:
  --output <dir>          Evidence directory
  --with-linux-package   Build the portable linux-x64 package after validation
  --help                 Show this help

This lane is intentionally Linux-safe. It runs:
  * Release restore + build
  * the permanent deterministic Agent Framework compatibility suite
  * Debug restore + build
  * the Debug-only DevUI registration/hosting smoke tests
  * release-script static analysis (shellcheck + PSScriptAnalyzer + P0_SPIKE compile gate)
  * optionally the real linux-x64 portable packager

The canonical Windows Velopack tester packager is not executed here. Its remaining
native-Windows proof must be run on Windows with publish/package-tester-win.ps1 -SkipUpload.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --output) OUTPUT_DIR="$2"; shift 2 ;;
    --with-linux-package) WITH_LINUX_PACKAGE="true"; shift ;;
    --help|-h) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

mkdir -p "${OUTPUT_DIR}"
RAW_DIR="$(mktemp -d "${REPO_ROOT}/.tmp/framework-validation.XXXXXX")"
trap 'rm -rf "${RAW_DIR}"' EXIT

run_step() {
  local id="$1"
  shift
  local raw="${RAW_DIR}/${id}.log"
  echo "== ${id} =="
  if "$@" >"${raw}" 2>&1; then
    python3 - "${raw}" "${OUTPUT_DIR}/${id}.log" "${REPO_ROOT}" <<'PY'
import pathlib
import sys

source, destination, repo = sys.argv[1:]
text = pathlib.Path(source).read_text(encoding="utf-8", errors="replace")
text = text.replace(repo, "$REPO").replace(repo.replace("/", "\\"), "$REPO")
text = "\n".join(line.rstrip() for line in text.splitlines()) + "\n"
pathlib.Path(destination).write_text(text, encoding="utf-8")
PY
  else
    status=$?
    cat "${raw}" >&2
    echo "Step '${id}' failed with exit ${status}; no later validation step was attempted." >&2
    exit "${status}"
  fi
}

cd "${REPO_ROOT}"
run_step release-restore scripts/with-build-lock.sh --lock-file "${SHARED_BUILD_LOCK}" -- \
  dotnet restore XE-Local-AI-Engine.slnx -p:Configuration=Release
run_step release-build scripts/with-build-lock.sh --lock-file "${SHARED_BUILD_LOCK}" -- \
  dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-restore
run_step release-agent-deterministic-tests scripts/with-build-lock.sh --lock-file "${SHARED_BUILD_LOCK}" -- \
  scripts/assembly-guard.sh guard --test-bins -- \
  dotnet test \
    --project XE-Local-AI-Engine.AI.Agent.Tests/XE-Local-AI-Engine.AI.Agent.Tests.csproj \
    --configuration Release --no-build --max-parallel-test-modules 1
run_step debug-restore scripts/with-build-lock.sh --lock-file "${SHARED_BUILD_LOCK}" -- \
  dotnet restore XE-Local-AI-Engine.slnx -p:Configuration=Debug
run_step debug-build scripts/with-build-lock.sh --lock-file "${SHARED_BUILD_LOCK}" -- \
  dotnet build XE-Local-AI-Engine.slnx --configuration Debug --no-restore
run_step debug-devui-registration-smoke scripts/with-build-lock.sh --lock-file "${SHARED_BUILD_LOCK}" -- \
  scripts/assembly-guard.sh guard --test-bins -- \
  dotnet test XE-Local-AI-Engine.AI.Agent.Tests/XE-Local-AI-Engine.AI.Agent.Tests.csproj \
    --configuration Debug --no-build --max-parallel-test-modules 1 \
    --treenode-filter '/*/*/AgentDevUiExtensionsTests/*'
run_step debug-devui-hosting-smoke scripts/with-build-lock.sh --lock-file "${SHARED_BUILD_LOCK}" -- \
  scripts/assembly-guard.sh guard --test-bins -- \
  dotnet test XE-Local-AI-Engine.Tests/XE-Local-AI-Engine.Tests.csproj \
    --configuration Debug --no-build --max-parallel-test-modules 1 \
    --treenode-filter '/*/*/FrameworkDevUiHostingSmokeTests/*'
run_step release-static-analysis scripts/with-build-lock.sh --lock-file "${SHARED_BUILD_LOCK}" -- \
  scripts/lint-release-scripts.sh

if [[ "${WITH_LINUX_PACKAGE}" == "true" ]]; then
  run_step linux-portable-package scripts/with-build-lock.sh --lock-file "${SHARED_BUILD_LOCK}" -- \
    publish/package-rc.sh --rid linux-x64
fi

python3 - "${OUTPUT_DIR}" "${WITH_LINUX_PACKAGE}" "$(git rev-parse HEAD)" <<'PY'
import datetime
import hashlib
import json
import pathlib
import sys

output = pathlib.Path(sys.argv[1])
with_package = sys.argv[2] == "true"
commit = sys.argv[3]
logs = []
for path in sorted(output.glob("*.log")):
    logs.append({
        "step": path.stem,
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
    })
manifest = {
    "schemaVersion": 1,
    "capturedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    "commit": commit,
    "result": "passed",
    "linuxPortablePackageIncluded": with_package,
    "windowsTesterPackage": {
        "status": "gap",
        "reason": "publish/package-tester-win.ps1 requires a native Windows packaging machine",
        "replay": "pwsh ./publish/package-tester-win.ps1 -SkipUpload",
    },
    "logs": logs,
}
(output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
PY

echo "Framework validation evidence written to ${OUTPUT_DIR}"
