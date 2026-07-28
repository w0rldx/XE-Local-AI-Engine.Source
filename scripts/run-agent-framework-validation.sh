#!/usr/bin/env bash
# Fresh, serialized validation for the Agent Framework 1.15 / MEAI closure.

set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
GIT_COMMON_DIR="$(realpath "$(git rev-parse --git-common-dir)")"
SHARED_REPO_ROOT="$(dirname "${GIT_COMMON_DIR}")"
SHARED_BUILD_LOCK="${SHARED_REPO_ROOT}/.tmp/build.lock"
OUTPUT_DIR="${REPO_ROOT}/docs/agent-framework/evidence/validation"
WITH_LINUX_PACKAGE="false"
VERIFY_SOURCE_IDENTITY_ONLY="false"

usage() {
  cat <<'EOF'
Usage: scripts/run-agent-framework-validation.sh [options]

Options:
  --output <dir>          Evidence directory
  --with-linux-package   Build the portable linux-x64 package after validation
  --verify-source-identity-only
                         Verify the tracked tree and untracked-file gate without running validation
  --help                 Show this help

This lane is intentionally Linux-safe. It runs:
  * Release restore + build
  * the permanent deterministic Agent Framework compatibility suite
  * the Release llama-server adapter-policy and architecture dependency tests
  * Debug restore + build
  * the Debug-only DevUI registration and route-registration contract tests
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
    --verify-source-identity-only) VERIFY_SOURCE_IDENTITY_ONLY="true"; shift ;;
    --help|-h) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

FRAMEWORK_DOCS_ROOT="$(realpath -m "${REPO_ROOT}/docs/agent-framework")"
OUTPUT_DIR="$(realpath -m "${OUTPUT_DIR}")"
case "${OUTPUT_DIR}" in
  "${FRAMEWORK_DOCS_ROOT}/"*) ;;
  *)
    echo "Evidence output must stay below ${FRAMEWORK_DOCS_ROOT}" >&2
    exit 2
    ;;
esac

OUTPUT_RELATIVE="${OUTPUT_DIR#"${REPO_ROOT}/"}"
SOURCE_TREE_SCOPE="tracked worktree excluding ${OUTPUT_RELATIVE}; nonignored untracked files rejected except .tmp"

assert_no_untracked_source() {
  python3 - "${REPO_ROOT}" "${OUTPUT_RELATIVE}" <<'PY'
import os
import subprocess
import sys

repo_root, output_relative = sys.argv[1:]
raw = subprocess.check_output(
    ["git", "ls-files", "--others", "--exclude-standard", "-z", "--", "."],
    cwd=repo_root,
)
paths = [os.fsdecode(path) for path in raw.split(b"\0") if path]
allowed_prefixes = (output_relative.rstrip("/") + "/", ".tmp/")
unexpected = [
    path
    for path in paths
    if path != output_relative
    and path != ".tmp"
    and not path.startswith(allowed_prefixes)
]
if unexpected:
    print(
        "Untracked files outside the explicit evidence/temp/generated exclusions make "
        "Agent Framework source identity ambiguous:",
        file=sys.stderr,
    )
    for path in unexpected:
        print(f"  - {path}", file=sys.stderr)
    raise SystemExit(75)
PY
}

compute_source_tree() {
  local source_tree_index source_tree
  source_tree_index="$(mktemp "${REPO_ROOT}/.tmp/framework-validation-index.XXXXXX")"
  rm -f "${source_tree_index}"
  GIT_INDEX_FILE="${source_tree_index}" git read-tree HEAD
  GIT_INDEX_FILE="${source_tree_index}" git add -A -- .
  GIT_INDEX_FILE="${source_tree_index}" git rm -r --cached --ignore-unmatch --quiet -- \
    "${OUTPUT_RELATIVE}" \
    .tmp
  source_tree="$(GIT_INDEX_FILE="${source_tree_index}" git write-tree)"
  rm -f "${source_tree_index}"
  printf '%s\n' "${source_tree}"
}

cd "${REPO_ROOT}"
mkdir -p "${REPO_ROOT}/.tmp"
assert_no_untracked_source

# Bind the evidence to the exact tracked source under test even when the correction worktree is intentionally
# uncommitted. The evidence output is excluded to avoid a self-referential tree identity.
SOURCE_TREE="$(compute_source_tree)"

if [[ "${VERIFY_SOURCE_IDENTITY_ONLY}" == "true" ]]; then
  echo "Agent Framework source identity verified: ${SOURCE_TREE}"
  exit 0
fi

rm -rf "${OUTPUT_DIR}"
mkdir -p "${OUTPUT_DIR}" "${REPO_ROOT}/.tmp"
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

run_step release-restore scripts/with-build-lock.sh --lock-file "${SHARED_BUILD_LOCK}" -- \
  dotnet restore XE-Local-AI-Engine.slnx -p:Configuration=Release
run_step release-build scripts/with-build-lock.sh --lock-file "${SHARED_BUILD_LOCK}" -- \
  dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-restore
run_step release-agent-deterministic-tests scripts/with-build-lock.sh --lock-file "${SHARED_BUILD_LOCK}" -- \
  scripts/assembly-guard.sh guard --test-bins -- \
  dotnet test \
    --project XE-Local-AI-Engine.AI.Agent.Tests/XE-Local-AI-Engine.AI.Agent.Tests.csproj \
    --configuration Release --no-build --max-parallel-test-modules 1
run_step release-adapter-architecture-tests scripts/with-build-lock.sh --lock-file "${SHARED_BUILD_LOCK}" -- \
  scripts/assembly-guard.sh guard --test-bins -- \
  dotnet test \
    --project XE-Local-AI-Engine.Tests/XE-Local-AI-Engine.Tests.csproj \
    --configuration Release --no-build --max-parallel-test-modules 1 \
    --treenode-filter '/*/*/(LlamaServerAdapterIntegrationTests|LayerDependencyTests)/*'
run_step debug-restore scripts/with-build-lock.sh --lock-file "${SHARED_BUILD_LOCK}" -- \
  dotnet restore XE-Local-AI-Engine.slnx -p:Configuration=Debug
run_step debug-build scripts/with-build-lock.sh --lock-file "${SHARED_BUILD_LOCK}" -- \
  dotnet build XE-Local-AI-Engine.slnx --configuration Debug --no-restore
run_step debug-devui-registration-contract scripts/with-build-lock.sh --lock-file "${SHARED_BUILD_LOCK}" -- \
  scripts/assembly-guard.sh guard --test-bins -- \
  dotnet test XE-Local-AI-Engine.AI.Agent.Tests/XE-Local-AI-Engine.AI.Agent.Tests.csproj \
    --configuration Debug --no-build --max-parallel-test-modules 1 \
    --treenode-filter '/*/*/AgentDevUiExtensionsTests/*'
run_step debug-devui-route-contract scripts/with-build-lock.sh --lock-file "${SHARED_BUILD_LOCK}" -- \
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

assert_no_untracked_source
FINAL_SOURCE_TREE="$(compute_source_tree)"
if [[ "${FINAL_SOURCE_TREE}" != "${SOURCE_TREE}" ]]; then
  echo "Tracked source changed during Agent Framework validation; result is void and must be rerun." >&2
  exit 75
fi

python3 - "${OUTPUT_DIR}" "${WITH_LINUX_PACKAGE}" "$(git rev-parse HEAD)" "${SOURCE_TREE}" "${SOURCE_TREE_SCOPE}" <<'PY'
import datetime
import hashlib
import json
import pathlib
import sys

output = pathlib.Path(sys.argv[1])
with_package = sys.argv[2] == "true"
commit = sys.argv[3]
source_tree = sys.argv[4]
source_tree_scope = sys.argv[5]
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
    "sourceTree": source_tree,
    "sourceTreeScope": source_tree_scope,
    "result": "passed",
    "linuxPortablePackageIncluded": with_package,
    "releaseTestScopes": {
        "agentFramework": "All XE-Local-AI-Engine.AI.Agent.Tests in Release.",
        "adapterAndArchitecture": (
            "LlamaServerAdapterIntegrationTests and LayerDependencyTests in Release; "
            "live llama-server adapter round trips remain opt-in when their environment variables are absent."
        ),
    },
    "debugContractChecks": {
        "registration": "AgentDevUiExtensionsTests proves Debug-only DI registration.",
        "route": "FrameworkDevUiHostingSmokeTests proves Debug-only endpoint mapping; it is not a live browser or HTTP runtime proof.",
    },
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
