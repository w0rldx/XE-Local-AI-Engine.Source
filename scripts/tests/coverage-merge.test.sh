#!/usr/bin/env bash

set -euo pipefail

PROJECT_ROOT="$(git rev-parse --show-toplevel)"
TEMP_ROOT="$(mktemp -d)"
trap 'rm -rf -- "${TEMP_ROOT}"' EXIT

cat >"${TEMP_ROOT}/one.xml" <<'XML'
<coverage><packages><package><classes>
  <class name="A" filename="src/A.cs"><lines>
    <line number="1" hits="1"/><line number="2" hits="0"/>
  </lines></class>
  <class name="B" filename="src/B.cs"><lines><line number="1" hits="1"/></lines></class>
</classes></package></packages></coverage>
XML
cat >"${TEMP_ROOT}/two.xml" <<'XML'
<coverage><packages><package><classes>
  <class name="A" filename="src/A.cs"><lines>
    <line number="1" hits="0"/><line number="2" hits="1"/>
  </lines></class>
  <class name="NestedA" filename="src/A.cs"><lines><line number="1" hits="0"/></lines></class>
  <class name="C" filename="src/C.cs"><lines><line number="1" hits="0"/></lines></class>
</classes></package></packages></coverage>
XML
printf '75.00\n' >"${TEMP_ROOT}/baseline.txt"

output="$(python3 "${PROJECT_ROOT}/scripts/merge-cobertura.py" \
  --minimum-file "${TEMP_ROOT}/baseline.txt" "${TEMP_ROOT}/one.xml" "${TEMP_ROOT}/two.xml")"
[[ "${output}" == *"75.00% (3/4 unique source lines across 2 current report(s))"* ]]

printf '75.01\n' >"${TEMP_ROOT}/baseline.txt"
if python3 "${PROJECT_ROOT}/scripts/merge-cobertura.py" \
  --minimum-file "${TEMP_ROOT}/baseline.txt" "${TEMP_ROOT}/one.xml" "${TEMP_ROOT}/two.xml" >/dev/null; then
  echo "FAIL: merger accepted coverage below the committed minimum" >&2
  exit 1
fi

echo "coverage-merge.test.sh: PASS"
