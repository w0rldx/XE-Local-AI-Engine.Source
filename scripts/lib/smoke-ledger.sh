# shellcheck shell=bash
# smoke-ledger.sh — the step ledger the opt-in smoke runners share: the anti-vacuous-pass mechanism.
#
# Every step declares itself expected BEFORE it runs and records a verdict when it finishes. The final gate asserts
# that every expected step recorded PASS and that at least one step ran. A step that dies, is skipped by accident, or
# silently returns early therefore FAILS the run instead of leaving a green summary behind — the same rule
# run-e2e-local.sh applies to a zero-test run.
#
# Usage: source "<repo>/scripts/lib/smoke-ledger.sh" "<log prefix>"

SMOKE_LEDGER_PREFIX="$1"

LEDGER_EXPECTED=()
LEDGER_PASSED=()
LEDGER_FAILED=()
FAILURES=()

ledger_expect() { LEDGER_EXPECTED+=("$1"); }
ledger_pass()   { LEDGER_PASSED+=("$1"); echo "[${SMOKE_LEDGER_PREFIX}] PASS  $1"; }

# Record a failure and keep going where it is safe to, so one run reports every broken step
# rather than only the first. The step name is tracked separately from the message so the summary
# can distinguish "this step ran and FAILED" from "this step produced no verdict at all" — the
# latter means something died unexpectedly and is a materially different thing to investigate.
step_fail() {
  local step="$1"; shift
  LEDGER_FAILED+=("${step}")
  FAILURES+=("${step}: $*")
  echo "[${SMOKE_LEDGER_PREFIX}] FAIL  ${step}: $*" >&2
  return 1
}

ledger_contains() {
  local needle="$1"; shift
  local item
  for item in "$@"; do
    [[ "${item}" == "${needle}" ]] && return 0
  done
  return 1
}

# The final gate. Returns non-zero when anything failed, when an expected step produced no
# verdict, or when nothing ran at all.
ledger_finalize() {
  local status=0 step
  echo
  echo "[${SMOKE_LEDGER_PREFIX}] === Summary ==="
  if [[ "${#LEDGER_EXPECTED[@]}" -eq 0 ]]; then
    echo "[${SMOKE_LEDGER_PREFIX}] FAIL: no steps were expected — nothing ran. This is not a pass." >&2
    return 1
  fi
  for step in "${LEDGER_EXPECTED[@]}"; do
    if ledger_contains "${step}" ${LEDGER_PASSED[@]+"${LEDGER_PASSED[@]}"}; then
      echo "  PASS       ${step}"
    elif ledger_contains "${step}" ${LEDGER_FAILED[@]+"${LEDGER_FAILED[@]}"}; then
      echo "  FAILED     ${step}" >&2
      status=1
    else
      # Nothing recorded a verdict: the step died or returned early without saying so. That is a
      # different failure from an assertion failing, and is exactly the vacuous-pass hole this
      # ledger exists to close.
      echo "  NO VERDICT ${step}  <- the step produced no result at all; treating as failure" >&2
      status=1
    fi
  done
  if [[ "${#FAILURES[@]}" -gt 0 ]]; then
    echo >&2
    echo "[${SMOKE_LEDGER_PREFIX}] ${#FAILURES[@]} failure(s):" >&2
    for step in "${FAILURES[@]}"; do echo "  - ${step}" >&2; done
    status=1
  fi
  return "${status}"
}
