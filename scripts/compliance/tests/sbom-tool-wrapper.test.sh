#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
wrapper="$repo_root/scripts/compliance/sbom-tool.sh"

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

[[ -x "$wrapper" ]] || fail "SBOM wrapper is missing or not executable"

manifest_version="$(
  python3 - "$repo_root/dotnet-tools.json" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    manifest = json.load(stream)

print(manifest["tools"]["microsoft.sbom.dotnettool"]["version"])
PY
)"

[[ "$manifest_version" == "4.1.5" ]] || fail "Microsoft.Sbom.DotNetTool must be pinned to 4.1.5"

temporary_directory="$(mktemp -d)"
trap 'rm -rf "$temporary_directory"' EXIT
cat > "$temporary_directory/dotnet" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-}" == "--list-runtimes" ]]; then
  printf 'Microsoft.NETCore.App 8.0.29 [/test/shared/Microsoft.NETCore.App]\n'
  exit 0
fi
if [[ "${1:-}" == "sbom-tool" && "${2:-}" == "Version" ]]; then
  printf 'Microsoft.Sbom.DotNetTool 4.1.5\n'
  exit 0
fi
exit 64
SH
chmod +x "$temporary_directory/dotnet"

version_output="$(PATH="$temporary_directory:$PATH" "$wrapper" Version)"
[[ "$version_output" == *"4.1.5"* ]] || fail "wrapper did not execute the pinned SBOM tool"

cat > "$temporary_directory/dotnet" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-}" == "--list-runtimes" ]]; then
  printf 'Microsoft.NETCore.App 10.0.10 [/test/shared/Microsoft.NETCore.App]\n'
  exit 0
fi
exit 64
SH
chmod +x "$temporary_directory/dotnet"

if PATH="$temporary_directory:$PATH" "$wrapper" Version > "$temporary_directory/no-net8.out" 2>&1; then
  fail "wrapper accepted a roll-forward-only .NET 10 host"
fi
grep -F 'requires a .NET 8 runtime' "$temporary_directory/no-net8.out" >/dev/null \
  || fail "wrapper did not explain the required .NET 8 runtime"

printf 'SBOM wrapper contract passed\n'
printf 'sbom-tool-wrapper.test.sh: PASS\n'
