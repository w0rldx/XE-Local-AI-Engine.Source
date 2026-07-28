#!/usr/bin/env bash
# Capture exact Release/Debug direct + transitive NuGet graphs and package status reports
# at two git commits. Each commit is restored in an isolated detached worktree so conditional
# Debug-only Agent Framework references cannot bleed across configurations.

set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
GIT_COMMON_DIR="$(realpath "$(git rev-parse --git-common-dir)")"
SHARED_REPO_ROOT="$(dirname "${GIT_COMMON_DIR}")"
SHARED_BUILD_LOCK="${SHARED_REPO_ROOT}/.tmp/build.lock"
SOLUTION="XE-Local-AI-Engine.slnx"
BASELINE_REF="e67d6697"
CURRENT_REF="HEAD"
OUTPUT_DIR="${REPO_ROOT}/docs/agent-framework/evidence/dependencies"

usage() {
  cat <<'EOF'
Usage: scripts/capture-agent-framework-dependencies.sh [options]

Options:
  --baseline-ref <ref>  Pre-upgrade commit (default: e67d6697)
  --current-ref <ref>   Post-upgrade commit (default: HEAD)
  --output <dir>        Evidence directory
  --help                Show this help

The command performs real Release and Debug restores. It must not run concurrently
with any build/test process; the repository build lock serializes every restore.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --baseline-ref) BASELINE_REF="$2"; shift 2 ;;
    --current-ref) CURRENT_REF="$2"; shift 2 ;;
    --output) OUTPUT_DIR="$2"; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

for command in git dotnet python3 sha256sum; do
  command -v "${command}" >/dev/null 2>&1 || {
    echo "Required command is missing: ${command}" >&2
    exit 2
  }
done

BASELINE_COMMIT="$(git rev-parse "${BASELINE_REF}^{commit}")"
CURRENT_COMMIT="$(git rev-parse "${CURRENT_REF}^{commit}")"
FRAMEWORK_DOCS_ROOT="$(realpath -m "${REPO_ROOT}/docs/agent-framework")"
OUTPUT_DIR="$(realpath -m "${OUTPUT_DIR}")"
case "${OUTPUT_DIR}" in
  "${FRAMEWORK_DOCS_ROOT}/"*) ;;
  *)
    echo "Evidence output must stay below ${FRAMEWORK_DOCS_ROOT}" >&2
    exit 2
    ;;
esac
mkdir -p "${REPO_ROOT}/.tmp"
RUN_ROOT="$(mktemp -d "${REPO_ROOT}/.tmp/framework-dependencies.XXXXXX")"
WORKTREES=()

cleanup() {
  local worktree
  for worktree in "${WORKTREES[@]}"; do
    git -C "${REPO_ROOT}" worktree remove --force "${worktree}" >/dev/null 2>&1 || true
  done
  rm -rf "${RUN_ROOT}"
}
trap cleanup EXIT

sanitize_json() {
  local source="$1" destination="$2" checkout_root="$3"
  python3 - "${source}" "${destination}" "${checkout_root}" "${REPO_ROOT}" <<'PY'
import json
import pathlib
import sys

source, destination, checkout_root, repo_root = sys.argv[1:]
payload = json.loads(pathlib.Path(source).read_text(encoding="utf-8"))
serialized = json.dumps(payload, indent=2, sort_keys=True)
for value in (checkout_root, repo_root):
    serialized = serialized.replace(value.replace("\\", "/"), "$REPO")
    serialized = serialized.replace(value.replace("/", "\\"), "$REPO")
pathlib.Path(destination).write_text(serialized + "\n", encoding="utf-8")
PY
}

capture_central_pins() {
  local checkout="$1" destination="$2"
  python3 - "${checkout}/Directory.Packages.props" "${destination}" <<'PY'
import pathlib
import sys
import xml.etree.ElementTree as ET

source, destination = map(pathlib.Path, sys.argv[1:])
root = ET.parse(source).getroot()
rows = []
for item_group in root.findall("ItemGroup"):
    for package in item_group.findall("PackageVersion"):
        package_id = package.attrib.get("Include")
        version = package.attrib.get("Version")
        if package_id and version:
            rows.append((package_id, version))
rows.sort(key=lambda row: row[0].casefold())
destination.write_text(
    "packageId\tcentralVersion\n" + "".join(f"{package_id}\t{version}\n" for package_id, version in rows),
    encoding="utf-8",
)
PY
}

run_package_report() {
  local checkout="$1" configuration="$2" destination="$3"
  shift 3
  local raw="${destination}.raw"
  (
    cd "${checkout}"
    Configuration="${configuration}" dotnet package list \
      --project "${SOLUTION}" \
      --no-restore \
      --format json \
      --output-version 1 \
      "$@" >"${raw}"
  )
  sanitize_json "${raw}" "${destination}" "${checkout}"
  rm -f "${raw}"
}

capture_ref() {
  local label="$1" commit="$2"
  local checkout="${RUN_ROOT}/${label}"
  local label_output="${OUTPUT_DIR}/${label}"

  git -C "${REPO_ROOT}" worktree add --detach "${checkout}" "${commit}" >/dev/null
  WORKTREES+=("${checkout}")
  mkdir -p "${label_output}"

  printf '%s\n' "${commit}" >"${label_output}/commit.txt"
  capture_central_pins "${checkout}" "${label_output}/central-pins.tsv"

  local configuration configuration_output
  for configuration in Release Debug; do
    configuration_output="${label_output}/${configuration,,}"
    mkdir -p "${configuration_output}"

    "${checkout}/scripts/with-build-lock.sh" --lock-file "${SHARED_BUILD_LOCK}" -- \
      dotnet restore "${checkout}/${SOLUTION}" -p:Configuration="${configuration}"

    run_package_report "${checkout}" "${configuration}" \
      "${configuration_output}/dependency-graph.json" \
      --include-transitive
    run_package_report "${checkout}" "${configuration}" \
      "${configuration_output}/vulnerable.json" \
      --include-transitive --vulnerable
    run_package_report "${checkout}" "${configuration}" \
      "${configuration_output}/deprecated.json" \
      --include-transitive --deprecated
    run_package_report "${checkout}" "${configuration}" \
      "${configuration_output}/outdated.json" \
      --include-transitive --outdated --include-prerelease
  done
}

rm -rf "${OUTPUT_DIR}"
mkdir -p "${OUTPUT_DIR}"

capture_ref baseline "${BASELINE_COMMIT}"
capture_ref current "${CURRENT_COMMIT}"

python3 - "${OUTPUT_DIR}" "${BASELINE_COMMIT}" "${CURRENT_COMMIT}" <<'PY'
import datetime
import hashlib
import json
import pathlib
import sys

output = pathlib.Path(sys.argv[1])
baseline, current = sys.argv[2:]
files = []
for path in sorted(output.rglob("*")):
    if not path.is_file() or path.name == "manifest.json":
        continue
    files.append({
        "path": path.relative_to(output).as_posix(),
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
    })
manifest = {
    "schemaVersion": 1,
    "capturedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    "baselineCommit": baseline,
    "currentCommit": current,
    "configurations": ["Release", "Debug"],
    "reports": {
        "dependency-graph.json": "exact direct and transitive resolved packages",
        "vulnerable.json": "NuGet vulnerability status",
        "deprecated.json": "NuGet deprecation status",
        "outdated.json": "NuGet current/latest status, including prereleases",
        "central-pins.tsv": "exact centrally declared versions",
    },
    "files": files,
}
(output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
PY

echo "Dependency evidence written to ${OUTPUT_DIR}"
