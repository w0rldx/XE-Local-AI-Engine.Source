#!/usr/bin/env bash

set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
FAKE="$TMP/repo"
PROJ="$FAKE/XE-Local-AI-Engine.Tests"
BIN="$PROJ/bin/Release/net10.0"
mkdir -p "$FAKE/scripts" "$PROJ/Cases" "$BIN"
cp "$ROOT/scripts/run-tests-memory-safe.sh" "$FAKE/scripts/"

cat >"$FAKE/scripts/with-build-lock.sh" <<'EOF'
#!/usr/bin/env bash
shift
export XE_BUILD_LOCK_HELD=1
exec "$@"
EOF

cat >"$FAKE/scripts/assembly-guard.sh" <<'EOF'
#!/usr/bin/env bash
[[ "$1" == snapshot ]] && : >"$2"
exit 0
EOF

cat >"$BIN/XE-Local-AI-Engine.Tests" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
max="" filter="" results="" coverage=""
while (($#)); do
  case "$1" in
    --maximum-parallel-tests) max="$2"; shift 2 ;;
    --treenode-filter) filter="$2"; shift 2 ;;
    --results-directory) results="$2"; shift 2 ;;
    --coverage-output) coverage="$2"; shift 2 ;;
    *) shift ;;
  esac
done
printf 'max=%s html=%s filter=%s\n' "$max" "${TUNIT_DISABLE_HTML_REPORTER:-}" "$filter" >>"$FAKE_LOG"
if [[ -n "$coverage" ]]; then
  mkdir -p "$results"
  printf '<coverage><packages><package><classes><class /></classes></package></packages></coverage>\n' \
    >"$results/$coverage"
fi
cat <<'SUMMARY'
Test run summary: Passed!
  total: 1
  failed: 0
  succeeded: 1
SUMMARY
exit "${FAKE_EXIT_AFTER_SUMMARY:-0}"
EOF
chmod +x "$FAKE/scripts/with-build-lock.sh" "$FAKE/scripts/assembly-guard.sh" \
  "$BIN/XE-Local-AI-Engine.Tests"

write_namespaces() {
  rm -f "$PROJ/Cases"/*.cs
  local ns
  for ns in "$@"; do
    printf 'namespace %s;\n[Test]\n' "$ns" >"$PROJ/Cases/${ns##*.}.cs"
  done
}

run_case() {
  local name="$1"; shift
  : >"$TMP/$name.log"
  output="$(FAKE_LOG="$TMP/$name.log" NO_BUILD=1 JOBS=1 "$@" "$FAKE/scripts/run-tests-memory-safe.sh")"
  grep -Fq 'ALL NAMESPACE BATCHES GREEN' <<<"$output"
  grep -Fq 'TOTAL: pass=' <<<"$output"
  grep -Fq 'fail=0' <<<"$output"
}

write_namespaces XE_Local_AI_Engine.Tests.DevWorkflows
run_case dev-default env
grep -Fqx 'max=2 html=1 filter=/*/XE_Local_AI_Engine.Tests.DevWorkflows/*/*' "$TMP/dev-default.log"

run_case dev-explicit env PAR=1
grep -Fqx 'max=1 html=1 filter=/*/XE_Local_AI_Engine.Tests.DevWorkflows/*/*' "$TMP/dev-explicit.log"

write_namespaces XE_Local_AI_Engine.Tests.Ordinary
run_case ordinary env
grep -Fqx 'max=1 html=1 filter=/*/XE_Local_AI_Engine.Tests.Ordinary/*/*' "$TMP/ordinary.log"

write_namespaces XE_Local_AI_Engine.Tests.DevWorkflows XE_Local_AI_Engine.Tests.Ordinary
run_case grouped env TEST_GROUPS=1
grep -Fqx 'max=1 html=1 filter=/*/(XE_Local_AI_Engine.Tests.DevWorkflows|XE_Local_AI_Engine.Tests.Ordinary)/*/*' \
  "$TMP/grouped.log"

write_namespaces XE_Local_AI_Engine.Tests.DevWorkflows
run_case coverage env COVERAGE_DIR="$TMP/coverage"
grep -Fqx 'max=1 html=1 filter=/*/XE_Local_AI_Engine.Tests.DevWorkflows/*/*' "$TMP/coverage.log"
grep -q '<class' "$TMP/coverage/XE_Local_AI_Engine.Tests.DevWorkflows/coverage.cobertura.xml"

write_namespaces XE_Local_AI_Engine.Tests.Ordinary
set +e
partial_output="$(FAKE_LOG="$TMP/partial-error.log" FAKE_EXIT_AFTER_SUMMARY=2 NO_BUILD=1 JOBS=1 \
  "$FAKE/scripts/run-tests-memory-safe.sh" 2>&1)"
partial_status=$?
set -e
[[ "$partial_status" -ne 0 ]]
grep -Fq 'FAILED namespaces: XE_Local_AI_Engine.Tests.Ordinary(exit=2)' <<<"$partial_output"

echo "run-tests-memory-safe.test.sh: PASS"
