#!/usr/bin/env bash

set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
FAKE="$TMP/repo"
PROJ="$FAKE/XE-Local-AI-Engine.Tests"
BIN="$PROJ/bin/Release/net10.0"
mkdir -p "$FAKE/scripts/lib" "$PROJ/Cases" "$BIN"
cp "$ROOT/scripts/run-tests-memory-safe.sh" "$FAKE/scripts/"
cp "$ROOT/scripts/lib/test-sizing.sh" "$FAKE/scripts/lib/"
printf 'MemAvailable:    6291456 kB\n' >"$TMP/meminfo-6g"

cat >"$FAKE/scripts/with-build-lock.sh" <<'EOF'
#!/usr/bin/env bash
shift
export XE_BUILD_LOCK_HELD=1
exec "$@"
EOF

# FAKE_GUARD_VERIFY_EXIT: what `verify` reports, so the contamination verdict (75) can be driven.
cat >"$FAKE/scripts/assembly-guard.sh" <<'EOF'
#!/usr/bin/env bash
[[ "$1" == snapshot ]] && : >"$2"
[[ "$1" == verify ]] && exit "${FAKE_GUARD_VERIFY_EXIT:-0}"
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
# The template pre-warm names one test; the real one publishes the migrated template next to the binary.
if [[ "$filter" == */TestServerWebAppFactoryTemplateSweepTests/* && -z "${FAKE_NO_TEMPLATE:-}" ]]; then
  mkdir -p "$(dirname "$0")/sqlite-templates" && : >"$(dirname "$0")/sqlite-templates/fake.sqlite"
fi
# FAKE_COVERAGE picks the report: healthy (default, every sentinel package hit), lossy (sentinels at
# line-rate="0", the CI loss), lossy-once (lossy on a unit's first call, healthy on its second).
if [[ -n "$coverage" ]]; then
  mkdir -p "$results"
  rate=0.5 seen="$FAKE_LOG.seen-$(basename "$results")"
  case "${FAKE_COVERAGE:-healthy}" in
    lossy) rate=0 ;;
    lossy-once) [[ -e "$seen" ]] || rate=0; : >"$seen" ;;
  esac
  { printf '<coverage><packages>'
    for pkg in XE-Local-AI-Engine.Client.Application XE-Local-AI-Engine.Client.Persistence XE-Local-AI-Engine.Client; do
      printf '<package line-rate="%s" branch-rate="0" complexity="1" name="%s"><classes><class /></classes></package>' \
        "$rate" "$pkg"
    done
    printf '</packages></coverage>\n'; } >"$results/$coverage"
  : >"$results/fake.trx"
fi
# FAKE_SUMMARY picks the MTP summary: passed (default), failed, skipped (all skipped), none.
case "${FAKE_SUMMARY:-passed}" in
  passed)  printf 'Test run summary: Passed!\n  total: 1\n  failed: 0\n  succeeded: 1\n  skipped: 0\n' ;;
  failed)  printf 'Test run summary: Failed!\n  total: 3\n  failed: 2\n  succeeded: 1\n  skipped: 0\n'
           exit 2 ;;
  skipped) printf 'Test run summary: Passed!\n  total: 4\n  failed: 0\n  succeeded: 0\n  skipped: 4\n' ;;
  none)    exit 0 ;;
esac
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

# Every namespace runs at width 1 unless PAR says otherwise — DevWorkflows included, since the
# width-2 exception it used to carry was removed (measured: no wall-clock gain, 50% more peak RSS).
write_namespaces XE_Local_AI_Engine.Tests.DevWorkflows
run_case dev-default env
grep -Fqx 'max=1 html=1 filter=/*/XE_Local_AI_Engine.Tests.DevWorkflows/*/*' "$TMP/dev-default.log"

run_case dev-explicit env PAR=3
grep -Fqx 'max=3 html=1 filter=/*/XE_Local_AI_Engine.Tests.DevWorkflows/*/*' "$TMP/dev-explicit.log"

write_namespaces XE_Local_AI_Engine.Tests.Ordinary
run_case ordinary env
grep -Fqx 'max=1 html=1 filter=/*/XE_Local_AI_Engine.Tests.Ordinary/*/*' "$TMP/ordinary.log"

output="$(FAKE_LOG="$TMP/profile.log" NO_BUILD=1 XE_TEST_PROFILE=low-memory \
  "$FAKE/scripts/run-tests-memory-safe.sh")"
grep -Fq 'Running namespace batches (JOBS=1' <<<"$output"
grep -Fqx 'max=1 html=1 filter=/*/XE_Local_AI_Engine.Tests.Ordinary/*/*' "$TMP/profile.log"

output="$(FAKE_LOG="$TMP/profile-override.log" NO_BUILD=1 XE_TEST_PROFILE=low-memory JOBS=2 PAR=3 \
  "$FAKE/scripts/run-tests-memory-safe.sh")"
grep -Fq 'Running namespace batches (JOBS=2' <<<"$output"
grep -Fqx 'max=3 html=1 filter=/*/XE_Local_AI_Engine.Tests.Ordinary/*/*' "$TMP/profile-override.log"

# No profile, no JOBS: sized from free RAM and the evidence line is printed; explicit JOBS skips it.
output="$(FAKE_LOG="$TMP/sized.log" NO_BUILD=1 XE_SIZING_MEMINFO="$TMP/meminfo-6g" \
  "$FAKE/scripts/run-tests-memory-safe.sh")"
grep -Fq '>> Sizing: MemAvailable=6.0 GB → JOBS=1 (default' <<<"$output"
grep -Fq 'Running namespace batches (JOBS=1' <<<"$output"
output="$(FAKE_LOG="$TMP/sized-explicit.log" NO_BUILD=1 JOBS=2 XE_SIZING_MEMINFO="$TMP/meminfo-6g" \
  "$FAKE/scripts/run-tests-memory-safe.sh")"
if grep -Fq '>> Sizing:' <<<"$output"; then echo "explicit JOBS was re-sized" >&2; exit 1; fi
grep -Fq 'Running namespace batches (JOBS=2' <<<"$output"

set +e
invalid_output="$(FAKE_LOG="$TMP/profile-invalid.log" NO_BUILD=1 XE_TEST_PROFILE=small \
  "$FAKE/scripts/run-tests-memory-safe.sh" 2>&1)"
invalid_status=$?
set -e
[[ "$invalid_status" -eq 2 ]]
grep -Fq "XE_TEST_PROFILE must be 'low-memory' or unset" <<<"$invalid_output"

write_namespaces XE_Local_AI_Engine.Tests.DevWorkflows XE_Local_AI_Engine.Tests.Ordinary
run_case grouped env TEST_GROUPS=1
grep -Fqx 'max=1 html=1 filter=/*/(XE_Local_AI_Engine.Tests.DevWorkflows|XE_Local_AI_Engine.Tests.Ordinary)/*/*' \
  "$TMP/grouped.log"

# Coverage mode pre-warms the migrated template once, by name, before the slot clones; a run that leaves no
# template behind fails loudly instead of letting every slot process build its own.
write_namespaces XE_Local_AI_Engine.Tests.DevWorkflows
run_case coverage env COVERAGE_DIR="$TMP/coverage"
grep -Fqx 'max=1 html=1 filter=/*/XE_Local_AI_Engine.Tests.DevWorkflows/*/*' "$TMP/coverage.log"
grep -q '<class' "$TMP/coverage/XE_Local_AI_Engine.Tests.DevWorkflows/coverage.cobertura.xml"
[[ "$(grep -c 'TestServerWebAppFactoryTemplateSweepTests' "$TMP/coverage.log")" -eq 1 ]]
[[ "$(head -n 1 "$TMP/coverage.log")" == *TestServerWebAppFactoryTemplateSweepTests* ]]
rm -rf "$BIN/sqlite-templates"
set +e
prewarm_output="$(FAKE_LOG="$TMP/prewarm-missing.log" FAKE_NO_TEMPLATE=1 NO_BUILD=1 JOBS=1 COVERAGE_DIR="$TMP/coverage2" \
  "$FAKE/scripts/run-tests-memory-safe.sh" 2>&1)"
prewarm_status=$?
set -e
[[ "$prewarm_status" -eq 1 ]]
grep -Fq 'ERROR: template pre-warm failed' <<<"$prewarm_output"
[[ "$(wc -l <"$TMP/prewarm-missing.log")" -eq 1 ]]

# A grouped coverage report whose sentinel packages lost their hits is retried once in a re-cloned slot; the
# retry's report and TRX are the only ones left, and the summary names the unit.
run_case coverage-lossy-once env COVERAGE_DIR="$TMP/cov-retry" TEST_GROUPS=1 FAKE_COVERAGE=lossy-once
unit_dir="$TMP/cov-retry/group0[DevWorkflows+0]"
grep -Fq "!! coverage lost in group0[DevWorkflows+0]: XE-Local-AI-Engine.Client.Application XE-Local-AI-Engine.Client.Persistence XE-Local-AI-Engine.Client; retrying once in a re-cloned slot" <<<"$output"
grep -Fq 'Coverage retried (lost hits on the first attempt): group0[DevWorkflows+0]' <<<"$output"
[[ "$(find "$unit_dir" -name coverage.cobertura.xml | wc -l)" -eq 1 ]]
[[ "$(find "$unit_dir" -name '*.trx' | wc -l)" -eq 1 ]]
grep -Fq 'line-rate="0.5"' "$unit_dir/coverage.cobertura.xml"
[[ -d "$unit_dir/coverage-logs-attempt1" ]]
[[ "$(grep -c 'DevWorkflows)/' "$TMP/coverage-lossy-once.log")" -eq 2 ]]

write_namespaces XE_Local_AI_Engine.Tests.Ordinary
set +e
partial_output="$(FAKE_LOG="$TMP/partial-error.log" FAKE_EXIT_AFTER_SUMMARY=2 NO_BUILD=1 JOBS=1 \
  "$FAKE/scripts/run-tests-memory-safe.sh" 2>&1)"
partial_status=$?
set -e
[[ "$partial_status" -ne 0 ]]
grep -Fq 'FAILED namespaces: XE_Local_AI_Engine.Tests.Ordinary(exit=2)' <<<"$partial_output"

# Runs the script expecting a red verdict; captures output + status for the asserts that follow.
run_red() {
  local name="$1"; shift
  : >"$TMP/$name.log"
  set +e
  output="$(FAKE_LOG="$TMP/$name.log" NO_BUILD=1 JOBS=1 "$@" "$FAKE/scripts/run-tests-memory-safe.sh" 2>&1)"
  status=$?
  set -e
  if grep -Fq 'ALL NAMESPACE BATCHES GREEN' <<<"$output"; then
    echo "$name: red case printed GREEN" >&2; printf '%s\n' "$output" >&2; exit 1
  fi
}

# --- red paths: failed tests, no summary, all skipped (HOLLOW), contamination ---
run_red failed env FAKE_SUMMARY=failed
[[ "$status" -eq 1 ]] || { echo "failed case exited $status" >&2; exit 1; }
grep -Fq 'FAILED namespaces: XE_Local_AI_Engine.Tests.Ordinary' <<<"$output"
grep -Fq 'TOTAL: pass=1 fail=2 skip=0' <<<"$output"

run_red no-summary env FAKE_SUMMARY=none
[[ "$status" -eq 1 ]] || { echo "no-summary case exited $status" >&2; exit 1; }
grep -Fq 'FAILED namespaces: XE_Local_AI_Engine.Tests.Ordinary(no-summary,exit=0)' <<<"$output"

run_red hollow env FAKE_SUMMARY=skipped
[[ "$status" -eq 1 ]] || { echo "hollow case exited $status" >&2; printf '%s\n' "$output" >&2; exit 1; }
grep -Fq 'HOLLOW: all 4 tests skipped in XE_Local_AI_Engine.Tests.Ordinary' <<<"$output"
grep -Fq 'FAILED namespaces: XE_Local_AI_Engine.Tests.Ordinary(hollow)' <<<"$output"
grep -Fq 'TOTAL: pass=0 fail=0 skip=4' <<<"$output"

# A loss that survives the one retry is red, and the host ran exactly twice for the unit.
write_namespaces XE_Local_AI_Engine.Tests.Ordinary
run_red coverage-lossy env COVERAGE_DIR="$TMP/cov-lossy" TEST_GROUPS=1 FAKE_COVERAGE=lossy
[[ "$status" -eq 1 ]] || { echo "coverage-lossy case exited $status" >&2; printf '%s\n' "$output" >&2; exit 1; }
grep -Fq 'FAILED namespaces: group0[Ordinary+0](coverage-lost)' <<<"$output"
[[ "$(grep -c 'Ordinary)/' "$TMP/coverage-lossy.log")" -eq 2 ]]

# A pack finer than 16 groups can isolate a namespace that touches no sentinel, so the check is off there:
# the same lossy report passes without a retry and the host ran once.
run_case coverage-lossy-fine-pack env COVERAGE_DIR="$TMP/cov-fine" TEST_GROUPS=17 FAKE_COVERAGE=lossy
if grep -Fq 'coverage lost' <<<"$output"; then echo "fine pack ran the sentinel check" >&2; exit 1; fi
[[ "$(grep -c 'Ordinary)/' "$TMP/coverage-lossy-fine-pack.log")" -eq 1 ]]

# Contamination outranks the red verdict of the same run.
run_red contaminated env FAKE_GUARD_VERIFY_EXIT=1 FAKE_SUMMARY=failed
[[ "$status" -eq 75 ]] || { echo "contaminated case exited $status" >&2; exit 1; }
grep -Fq 'RESULT VOID' <<<"$output"

# --- TEST_SHARD strides the groups by modulo: four shards of four groups partition the module ---
write_namespaces XE_Local_AI_Engine.Tests.ShardA XE_Local_AI_Engine.Tests.ShardB \
  XE_Local_AI_Engine.Tests.ShardC XE_Local_AI_Engine.Tests.ShardD
: >"$TMP/shard-all.log"
for i in 0 1 2 3; do
  run_case "shard-$i" env TEST_GROUPS=4 TEST_SHARD="$i/4"
  grep -Fq ">> Shard $i/4: running groups $i of 4." <<<"$output"
  [[ "$(wc -l <"$TMP/shard-$i.log")" -eq 1 ]] || { echo "shard $i ran $(wc -l <"$TMP/shard-$i.log") units" >&2; exit 1; }
  cat "$TMP/shard-$i.log" >>"$TMP/shard-all.log"
done
for ns in ShardA ShardB ShardC ShardD; do
  [[ "$(grep -c "XE_Local_AI_Engine.Tests.$ns)" "$TMP/shard-all.log")" -eq 1 ]] \
    || { echo "$ns was not run exactly once across the shards" >&2; cat "$TMP/shard-all.log" >&2; exit 1; }
done

echo "run-tests-memory-safe.test.sh: PASS"
