#!/usr/bin/env bash
# Contract test for how the gate, check and lock scripts find "this checkout" and scripts/lib/git-env.sh:
#   (a) a copy without lib/git-env.sh fails loudly and does nothing else — never runs unsanitized;
#   (b) invoked through a symlink, a script uses the checkout its REAL file lives in;
#   (c) the contract runner exercises its own checkout's scripts, whatever directory it is started from.
# Throwaway directories only; no real repository, index or lock is touched.
set -euo pipefail

ROOT="$(cd "$(dirname "$(realpath "${BASH_SOURCE[0]}")")/../.." && pwd)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
TMP="$(cd "$TMP" && pwd -P)"
# None of the throwaway checkouts is a git repository; the ceiling keeps discovery from walking out of
# them into whatever repository might enclose the temp directory. The lock and git variables a caller may
# have exported are removed so they cannot decide the results.
export GIT_CEILING_DIRECTORIES="$TMP"
unset BUILD_LOCK_FILE XE_BUILD_LOCK_HELD XE_BACKEND_GATE_LOCKED XE_OPENAPI_LIVE_LOCKED GIT_DIR GIT_WORK_TREE \
  GIT_INDEX_FILE

fail() { echo "FAIL: $*" >&2; exit 1; }

# --- (a) lib/git-env.sh missing: exit non-zero, name the file, do nothing else ---
NOLIB="$TMP/nolib"
mkdir -p "$NOLIB/scripts/lib"
# The lock scripts' own library is present, so it is git-env.sh alone that is missing.
cp "$ROOT/scripts/lib/build-lock-common.sh" "$NOLIB/scripts/lib/"
for script in with-build-lock.sh build-lock-status.sh run-backend-tests.sh run-release-contract-tests.sh \
    assembly-guard.sh lint-release-scripts.sh openapi-live-check.sh; do
  cp "$ROOT/scripts/$script" "$NOLIB/scripts/"
done
# "<script> <args>": each would DO something observable if it got past the load — run the command, take a
# lock, start a gate.
expect_refused() {
  local script="$1"; shift
  set +e
  out="$(cd "$TMP" && "$NOLIB/scripts/$script" "$@" 2>&1)"
  status=$?
  set -e
  [[ "$status" -ne 0 ]] || fail "$script ran without lib/git-env.sh: $out"
  [[ "$out" == *"lib/git-env.sh"* ]] || fail "$script did not name the missing file: $out"
}
expect_refused with-build-lock.sh -- touch "$TMP/wrapper-ran"
[[ ! -e "$TMP/wrapper-ran" ]] || fail "the wrapper ran its command without lib/git-env.sh"
expect_refused build-lock-status.sh --json
expect_refused run-backend-tests.sh --siblings-only
expect_refused run-release-contract-tests.sh
expect_refused assembly-guard.sh guard -- touch "$TMP/guard-ran"
[[ ! -e "$TMP/guard-ran" ]] || fail "assembly-guard ran its command without lib/git-env.sh"
expect_refused lint-release-scripts.sh --shell-only --no-behavior
expect_refused openapi-live-check.sh
[[ ! -e "$NOLIB/.tmp" ]] || fail "a script without lib/git-env.sh created $NOLIB/.tmp (a lock, results)"
echo "PASS (a) a missing lib/git-env.sh is fatal and nothing ran"

# --- (b) through a symlink: the checkout of the real file ---
# Two complete checkouts, A and B, each with the runner, the lock scripts, lib/ and one marker test that
# records the working directory it was run in: a test that finds its subject from the working directory
# (as several real ones do) exercises THAT checkout's scripts.
make_checkout() {
  local dir="$1"
  mkdir -p "$dir/scripts/lib" "$dir/scripts/tests" "$dir/scripts/compliance/tests" "$dir/scripts/performance/tests"
  cp "$ROOT/scripts/run-release-contract-tests.sh" "$ROOT/scripts/with-build-lock.sh" \
    "$ROOT/scripts/build-lock-status.sh" "$dir/scripts/"
  cp "$ROOT"/scripts/lib/*.sh "$dir/scripts/lib/"
  cat >"$dir/scripts/tests/marker.test.sh" <<'EOF'
#!/usr/bin/env bash
echo "exercised $(pwd -P)" >>"$MARKER_LOG"
echo "marker.test.sh: PASS"
EOF
  chmod +x "$dir/scripts/tests/marker.test.sh"
}
A="$TMP/checkout-a"
B="$TMP/checkout-b"
make_checkout "$A"
make_checkout "$B"
mkdir -p "$TMP/links"
ln -s "$A/scripts/with-build-lock.sh" "$TMP/links/with-build-lock.sh"
ln -s "$A/scripts/build-lock-status.sh" "$TMP/links/build-lock-status.sh"
ln -s "$A/scripts/run-release-contract-tests.sh" "$TMP/links/run-release-contract-tests.sh"
held="$(cd "$B" && "$TMP/links/with-build-lock.sh" --timeout 10 -- printenv XE_BUILD_LOCK_HELD)" \
  || fail "the symlinked wrapper failed"
[[ "$held" == "$A/.tmp/build.lock" ]] || fail "the symlinked wrapper locked $held, want $A/.tmp/build.lock"
lock="$(cd "$B" && "$TMP/links/build-lock-status.sh" --json | python3 -c 'import json,sys; print(json.load(sys.stdin)["lock"])')"
[[ "$lock" == "$A/.tmp/build.lock" ]] || fail "the symlinked status reported $lock, want $A/.tmp/build.lock"
[[ ! -e "$TMP/links/.tmp" && ! -e "$TMP/.tmp" ]] || fail "a symlinked script used the link's directory"
export MARKER_LOG="$TMP/marker.log"
: >"$MARKER_LOG"
(cd "$B" && "$TMP/links/run-release-contract-tests.sh" >/dev/null) || fail "the symlinked runner failed"
[[ "$(cat "$MARKER_LOG")" == "exercised $A" ]] || fail "the symlinked runner exercised: $(cat "$MARKER_LOG")"
echo "PASS (b) a symlinked wrapper, status and runner use the checkout of the real file"

# --- (c) the runner of checkout A, started from inside checkout B or from /, exercises A ---
for start in "$B" /; do
  : >"$MARKER_LOG"
  (cd "$start" && "$A/scripts/run-release-contract-tests.sh" >/dev/null) || fail "A's runner failed from $start"
  [[ "$(cat "$MARKER_LOG")" == "exercised $A" ]] \
    || fail "A's runner started from $start exercised: $(cat "$MARKER_LOG")"
done
echo "PASS (c) the runner exercises its own checkout from any working directory"

echo "entry-point-location.test.sh: PASS"
