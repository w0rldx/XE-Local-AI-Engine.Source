#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

if ! dotnet --list-runtimes | grep -Eq '^Microsoft\.NETCore\.App 8\.'; then
  printf '%s\n' \
    'ERROR: Microsoft.Sbom.DotNetTool 4.1.5 requires a .NET 8 runtime.' \
    'Install a supported .NET 8 runtime; do not roll the tool forward to .NET 10 because component detection becomes incomplete.' >&2
  exit 2
fi

unset DOTNET_ROLL_FORWARD
cd "$repo_root"
exec dotnet sbom-tool "$@"
