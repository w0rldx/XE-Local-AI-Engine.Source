#!/usr/bin/env bash

set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
TMP="$(mktemp -d)"
# Kills only PIDs this file started or its fake hosts recorded, should a case fail before they are gone.
cleanup() {
  if [[ -f "$TMP/spawned" ]]; then xargs -r kill -9 <"$TMP/spawned" 2>/dev/null || true; fi
  if [[ -n "${UNRELATED:-}" ]]; then kill -9 "$UNRELATED" 2>/dev/null || true; fi
  rm -rf "$TMP"
}
trap cleanup EXIT
FAKE="$TMP/repo"
PROJ="$FAKE/XE-Local-AI-Engine.Tests"
BIN="$PROJ/bin/Release/net10.0"
mkdir -p "$FAKE/scripts/lib" "$PROJ/Cases" "$BIN"
cp "$ROOT/scripts/run-tests-memory-safe.sh" "$FAKE/scripts/"
cp "$ROOT/scripts/lib/test-sizing.sh" "$ROOT/scripts/lib/proc-tree.sh" "$FAKE/scripts/lib/"
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
max="" filter="" results="" coverage="" trxname=""
while (($#)); do
  case "$1" in
    --maximum-parallel-tests) max="$2"; shift 2 ;;
    --treenode-filter) filter="$2"; shift 2 ;;
    --results-directory) results="$2"; shift 2 ;;
    --coverage-output) coverage="$2"; shift 2 ;;
    --report-trx-filename) trxname="$2"; shift 2 ;;
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
# A named TRX is written whatever the verdict, as MTP does; the log line proves no coverage rode along.
if [[ -n "$trxname" ]]; then
  mkdir -p "$results" && : >"$results/$trxname"
  printf 'trx=%s/%s coverage=%s\n' "$results" "$trxname" "$coverage" >>"$FAKE_LOG"
fi
# FAKE_BLOCK_NS: the batch whose filter names it holds still until, in this order, FAKE_AWAIT_HOLD (the runner's
# output) shows a held launch, FAKE_FREE_MEMINFO rewrites MemAvailable to 64 GB, and FAKE_LOG shows a batch started
# whose filter begins with FAKE_AWAIT_START. Each step is optional; one that never comes fails the batch.
await_in() {
  local i
  # real-timer: the subject is the real runner's scheduling, observed through its own output files.
  for ((i = 0; i < 600; i++)); do grep -Fq -- "$1" "$2" 2>/dev/null && return 0; sleep 0.1; done
  echo "fake host: '$1' never appeared in $2" >&2; exit 1
}
if [[ -n "${FAKE_BLOCK_NS:-}" && "$filter" == *"$FAKE_BLOCK_NS"* ]]; then
  [[ -z "${FAKE_AWAIT_HOLD:-}" ]] || await_in 'holding the next batch' "$FAKE_AWAIT_HOLD"
  [[ -z "${FAKE_FREE_MEMINFO:-}" ]] || printf 'MemAvailable:   67108864 kB\n' >"$XE_SIZING_MEMINFO"
  [[ -z "${FAKE_AWAIT_START:-}" ]] || await_in "filter=$FAKE_AWAIT_START" "$FAKE_LOG"
  echo "released $filter" >>"$FAKE_LOG"
fi
# FAKE_SPAWN: MTP's controller -> host -> engine chain. A child that starts a grandchild, both PIDs recorded in
# FAKE_SPAWN (child first); with XE_SIZING_PS set, that table describes the tree (a 2000 MB host). Then MemAvailable
# drops under any floor and this controller waits to be killed.
if [[ -n "${FAKE_SPAWN:-}" ]]; then
  bash -c 'echo "$$" >>"$1"; sleep 300 & echo "$!" >>"$1"; wait' _ "$FAKE_SPAWN" &
  for ((i = 0; i < 600; i++)); do [[ "$(wc -l <"$FAKE_SPAWN")" -ge 2 ]] && break; sleep 0.1; done
  mapfile -t kids <"$FAKE_SPAWN"
  [[ -z "${XE_SIZING_PS:-}" ]] || printf '%s 1 1024\n%s %s 2048000\n%s %s 1024\n' \
    "$$" "${kids[0]}" "$$" "${kids[1]}" "${kids[0]}" >"$XE_SIZING_PS"
  printf 'MemAvailable:     102400 kB\n' >"$XE_SIZING_MEMINFO"
  wait
fi
# FAKE_SUMMARY picks the MTP summary: passed (default), failed, skipped (all skipped), none.
case "${FAKE_SUMMARY:-passed}" in
  passed)  printf 'Test run summary: Passed!\n  total: 1\n  failed: 0\n  succeeded: 1\n  skipped: 0\n' ;;
  failed)  printf 'failed SomeTest (12ms)\n  Assertion failed: Expected: 5, actual: 4\n    at Some.Frame()\n\n'
           printf 'Test run summary: Failed!\n  total: 3\n  failed: 2\n  succeeded: 1\n  skipped: 0\n'
           exit 2 ;;
  skipped) printf 'Test run summary: Passed!\n  total: 4\n  failed: 0\n  succeeded: 0\n  skipped: 4\n' ;;
  none)    exit 0 ;;
esac
exit "${FAKE_EXIT_AFTER_SUMMARY:-0}"
EOF
chmod +x "$FAKE/scripts/with-build-lock.sh" "$FAKE/scripts/assembly-guard.sh" \
  "$BIN/XE-Local-AI-Engine.Tests"

# --- scripts/lib/proc-tree.sh: one ps table in, the tree's summed RSS and its descendant PIDs out ---
# shellcheck source=scripts/lib/proc-tree.sh
source "$ROOT/scripts/lib/proc-tree.sh"
# A controller (10) whose test host (11) started an engine (12) and a helper (13); 20 is unrelated.
printf '%s\n' '  1     0    100' ' 10     1 105000' ' 11    10 700000' ' 12    11  50000' ' 13    11   2000' \
  ' 20     1 999999' >"$TMP/ps-table"
check_tree() {
  local got; got="$(XE_SIZING_PS="$TMP/ps-table" xe_proc_tree "$2")"
  [[ "$got" == "$1" ]] || { echo "xe_proc_tree $2: wanted '$1', got '$got'" >&2; exit 1; }
}
check_tree '857000 11 12 13' 10   # grandchildren in, the unrelated 20 out
check_tree '2000' 13              # a leaf: its own RSS, no descendants
check_tree '0' 99                 # a PID missing from the table

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

# Every namespace runs at width 1 unless PAR says otherwise — a heavy one included, because a
# width-2 exception for one was measured: no wall-clock gain, 50% more peak RSS.
write_namespaces XE_Local_AI_Engine.Tests.GraphWorkflows
run_case dev-default env
grep -Fqx 'max=1 html=1 filter=/*/XE_Local_AI_Engine.Tests.GraphWorkflows/*/*' "$TMP/dev-default.log"

run_case dev-explicit env PAR=3
grep -Fqx 'max=3 html=1 filter=/*/XE_Local_AI_Engine.Tests.GraphWorkflows/*/*' "$TMP/dev-explicit.log"

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

write_namespaces XE_Local_AI_Engine.Tests.GraphWorkflows XE_Local_AI_Engine.Tests.Ordinary
run_case grouped env TEST_GROUPS=1
grep -Fqx 'max=1 html=1 filter=/*/(XE_Local_AI_Engine.Tests.GraphWorkflows|XE_Local_AI_Engine.Tests.Ordinary)/*/*' \
  "$TMP/grouped.log"

# Coverage mode pre-warms the migrated template once, by name, before the slot clones; a run that leaves no
# template behind fails loudly instead of letting every slot process build its own.
write_namespaces XE_Local_AI_Engine.Tests.GraphWorkflows
run_case coverage env COVERAGE_DIR="$TMP/coverage"
grep -Fqx 'max=1 html=1 filter=/*/XE_Local_AI_Engine.Tests.GraphWorkflows/*/*' "$TMP/coverage.log"
grep -q '<class' "$TMP/coverage/XE_Local_AI_Engine.Tests.GraphWorkflows/coverage.cobertura.xml"
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
unit_dir="$TMP/cov-retry/group0[GraphWorkflows+0]"
grep -Fq "!! coverage lost in group0[GraphWorkflows+0]: XE-Local-AI-Engine.Client.Application XE-Local-AI-Engine.Client.Persistence XE-Local-AI-Engine.Client; retrying once in a re-cloned slot" <<<"$output"
grep -Fq 'Coverage retried (lost hits on the first attempt): group0[GraphWorkflows+0]' <<<"$output"
[[ "$(find "$unit_dir" -name coverage.cobertura.xml | wc -l)" -eq 1 ]]
[[ "$(find "$unit_dir" -name '*.trx' | wc -l)" -eq 1 ]]
grep -Fq 'line-rate="0.5"' "$unit_dir/coverage.cobertura.xml"
[[ -d "$unit_dir/coverage-logs-attempt1" ]]
[[ "$(grep -c 'GraphWorkflows)/' "$TMP/coverage-lossy-once.log")" -eq 2 ]]

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

# The assertion line under a failure header reaches the printed output, not only the header.
grep -Fq 'Expected: 5, actual: 4' <<<"$output"

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

# --- TRX_DIR: one TRX per unit, named after it, in its own directory; no coverage; a rerun replaces it ---
write_namespaces XE_Local_AI_Engine.Tests.GraphWorkflows XE_Local_AI_Engine.Tests.Ordinary
mkdir -p "$TMP/trx/XE_Local_AI_Engine.Tests.Ordinary" && : >"$TMP/trx/XE_Local_AI_Engine.Tests.Ordinary/stale.trx"
run_case trx env TRX_DIR="$TMP/trx"
for ns in GraphWorkflows Ordinary; do
  unit="XE_Local_AI_Engine.Tests.$ns"
  grep -Fqx "trx=$TMP/trx/$unit/$unit.trx coverage=" "$TMP/trx.log"
  [[ "$(find "$TMP/trx/$unit" -type f)" == "$TMP/trx/$unit/$unit.trx" ]] \
    || { echo "trx: $unit holds $(find "$TMP/trx/$unit" -type f)" >&2; exit 1; }
done
# Grouped units are named after the group, which is what the summary's FAILED line names too.
write_namespaces XE_Local_AI_Engine.Tests.Ordinary
run_case trx-grouped env TRX_DIR="$TMP/trx-grouped" TEST_GROUPS=1
[[ -f "$TMP/trx-grouped/group0[Ordinary+0]/group0[Ordinary+0].trx" ]]
# A red batch keeps its TRX and its verdict: same exit, same FAILED line as without TRX_DIR.
run_red trx-failed env TRX_DIR="$TMP/trx-red" FAKE_SUMMARY=failed
[[ "$status" -eq 1 ]] || { echo "trx-failed case exited $status" >&2; exit 1; }
grep -Fq 'FAILED namespaces: XE_Local_AI_Engine.Tests.Ordinary' <<<"$output"
grep -Fq 'TOTAL: pass=1 fail=2 skip=0' <<<"$output"
[[ -f "$TMP/trx-red/XE_Local_AI_Engine.Tests.Ordinary/XE_Local_AI_Engine.Tests.Ordinary.trx" ]]
# COVERAGE_DIR already writes a TRX per unit and wins: nothing lands under TRX_DIR.
run_case trx-coverage env TRX_DIR="$TMP/trx-cov" COVERAGE_DIR="$TMP/cov-trx"
[[ ! -e "$TMP/trx-cov" ]] || { echo "TRX_DIR used beside COVERAGE_DIR" >&2; exit 1; }
[[ -f "$TMP/cov-trx/XE_Local_AI_Engine.Tests.Ordinary/fake.trx" ]]

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

# --- backpressure: with a batch running, the next waits for one batch + AVAIL_FLOOR of MemAvailable ---
# Runs the script with its output in <name>.out, where a fake host can watch for the held-launch line.
run_to_file() {
  local name="$1"; shift
  : >"$TMP/$name.log"
  set +e
  FAKE_LOG="$TMP/$name.log" NO_BUILD=1 JOBS=2 "$@" "$FAKE/scripts/run-tests-memory-safe.sh" >"$TMP/$name.out" 2>&1
  status=$?
  set -e
  output="$(cat "$TMP/$name.out")"
  if [[ "$status" -ne 0 ]] || ! grep -Fq 'ALL NAMESPACE BATCHES GREEN' <<<"$output"; then
    echo "$name: exited $status" >&2; printf '%s\n' "$output" >&2; exit 1
  fi
}
line_of() { grep -nF -- "$1" "$2" | head -1 | cut -d: -f1; }
refute() { if grep -Fq "$@"; then echo "unexpected: grep -F $*" >&2; exit 1; fi; }
write_namespaces XE_Local_AI_Engine.Tests.HoldA XE_Local_AI_Engine.Tests.HoldB
printf 'MemAvailable:    1048576 kB\n' >"$TMP/meminfo-1g"
printf 'MemAvailable:   67108864 kB\n' >"$TMP/meminfo-64g"
next_filter='/*/XE_Local_AI_Engine.Tests.HoldB/'

# 1 GB free: HoldB is held while HoldA runs, and starts once HoldA has finished.
run_to_file hold-finish env XE_SIZING_MEMINFO="$TMP/meminfo-1g" FAKE_BLOCK_NS=Tests.HoldA \
  FAKE_AWAIT_HOLD="$TMP/hold-finish.out"
[[ "$(grep -c 'holding the next batch' <<<"$output")" -eq 1 ]]
grep -Fq '.. holding the next batch: MemAvailable 1024MB < 1824MB (one batch 1.0 GB + AVAIL_FLOOR 800MB)' <<<"$output"
(( $(line_of 'released /*/XE_Local_AI_Engine.Tests.HoldA/' "$TMP/hold-finish.log") \
   < $(line_of "filter=$next_filter" "$TMP/hold-finish.log") )) \
  || { echo "hold-finish: HoldB started before HoldA finished" >&2; cat "$TMP/hold-finish.log" >&2; exit 1; }

# Memory freed while HoldA still runs: HoldB starts at once (HoldA itself waits for that start).
cp "$TMP/meminfo-1g" "$TMP/meminfo-freed"
run_to_file hold-memory env XE_SIZING_MEMINFO="$TMP/meminfo-freed" FAKE_BLOCK_NS=Tests.HoldA \
  FAKE_AWAIT_HOLD="$TMP/hold-memory.out" FAKE_FREE_MEMINFO=1 FAKE_AWAIT_START="$next_filter"
grep -Fq 'holding the next batch' <<<"$output"

# Room for a batch, or unknown memory: never held. HoldA stays running until HoldB has started.
for room in "$TMP/meminfo-64g" "$TMP/absent-meminfo"; do
  run_to_file hold-none env XE_SIZING_MEMINFO="$room" FAKE_BLOCK_NS=Tests.HoldA FAKE_AWAIT_START="$next_filter"
  refute 'holding the next batch' <<<"$output"
done

# --- SAFETY-KILL: the controller and every descendant die, an unrelated process does not ---
# A zombie still answers kill -0, and its reaper is whatever this file runs under, so state Z counts as gone.
alive() { local st; st="$(ps -o stat= -p "$1" 2>/dev/null)"; [[ -n "$st" && "$st" != Z* ]]; }
write_namespaces XE_Local_AI_Engine.Tests.Ordinary
sleep 300 >/dev/null 2>&1 & UNRELATED=$!
for table in live seamed; do
  : >"$TMP/spawned"
  printf 'MemAvailable:   67108864 kB\n' >"$TMP/meminfo-kill"
  seam=()
  [[ "$table" == live ]] || seam=(XE_SIZING_PS="$TMP/ps-kill")
  run_red "kill-$table" env XE_SIZING_MEMINFO="$TMP/meminfo-kill" FAKE_SPAWN="$TMP/spawned" ${seam[@]+"${seam[@]}"}
  [[ "$status" -eq 1 ]] || { echo "kill-$table exited $status" >&2; printf '%s\n' "$output" >&2; exit 1; }
  grep -Fq '!! SAFETY-KILL XE_Local_AI_Engine.Tests.Ordinary: avail 100MB < 800MB' <<<"$output"
  grep -Fq 'FAILED namespaces: XE_Local_AI_Engine.Tests.Ordinary(oom)' <<<"$output"
  # The seamed table gives the host 2000 MB: peakRSS is the tree's sum, not the controller's 1 MB.
  [[ "$table" == live ]] || grep -Fq 'peakRSS(any batch)=2002MB' <<<"$output"
  mapfile -t descendants <"$TMP/spawned"
  [[ "${#descendants[@]}" -eq 2 ]] || { echo "kill-$table: fake host recorded ${descendants[*]}" >&2; exit 1; }
  deadline=$((SECONDS + 60))
  while :; do
    survivors=()
    for pid in "${descendants[@]}"; do alive "$pid" && survivors+=("$pid"); done
    [[ "${#survivors[@]}" -eq 0 ]] && break
    (( SECONDS <= deadline )) || { echo "kill-$table: descendants survived SAFETY-KILL: ${survivors[*]}" >&2; exit 1; }
    # real-timer: a KILL is delivered asynchronously; the assertion waits for real processes to die.
    sleep 0.2
  done
  alive "$UNRELATED" || { echo "kill-$table: SAFETY-KILL took an unrelated process" >&2; exit 1; }
done

echo "run-tests-memory-safe.test.sh: PASS"
