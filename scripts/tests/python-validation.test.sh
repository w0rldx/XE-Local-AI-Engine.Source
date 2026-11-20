#!/usr/bin/env bash
# Contract test for scripts/python-validation.sh. Each case copies the script into a throwaway repository (it
# resolves its project root from its own location), puts a fake `uv` first on PATH that records every call and
# fails on request, and asserts the exit code, the uv calls made and the scope chosen. Nothing here touches
# this checkout.

set -uo pipefail

ROOT="$(cd "$(dirname "$(realpath "${BASH_SOURCE[0]}")")/../.." && pwd)"
SOURCE_SCRIPT="$ROOT/scripts/python-validation.sh"
TMP="$(realpath "$(mktemp -d)")"
trap 'rm -rf "$TMP"' EXIT

# Fixture git calls and the script's own run with the caller's repository selectors and config removed, and
# discovery bounded by $TMP, so nothing can reach the enclosing repository.
SCRUB=()
for var in $(compgen -v GIT_); do SCRUB+=(-u "$var"); done
SCRUB+=(GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1 GIT_CEILING_DIRECTORIES="$TMP")
fg() { env "${SCRUB[@]}" git "$@"; }

mkdir -p "$TMP/bin"
cat >"$TMP/bin/uv" <<'FAKE_UV'
#!/usr/bin/env bash
# Fake uv: logs its arguments, fails when they contain FAKE_UV_FAIL. With FAKE_UV_FIFO set, pytest and bandit
# meet on that FIFO, which only completes when both run at once.
echo "$*" >>"$FAKE_UV_LOG"
if [[ -n "${FAKE_UV_FAIL:-}" && "$*" == *"$FAKE_UV_FAIL"* ]]; then
  echo "fake uv failing: $*"
  exit 3
fi
if [[ -n "${FAKE_UV_FIFO:-}" ]]; then
  case "$*" in
    "run pytest") timeout 30 bash -c 'read -r _ <"$1"' _ "$FAKE_UV_FIFO" || exit 4 ;;
    "run bandit "*) timeout 30 bash -c 'echo go >"$1"' _ "$FAKE_UV_FIFO" || exit 4 ;;
  esac
fi
exit 0
FAKE_UV
chmod +x "$TMP/bin/uv"

ROOTS="tools/training scripts catalog/external-apps/tools"
DEPS="sync --locked --all-groups"
STYLE_FORMAT="run ruff format --check $ROOTS"
STYLE_CHECK="run ruff check $ROOTS"
TYPES="run pyrefly check"
TESTS="run pytest"
SECURITY="run bandit -c pyproject.toml -r $ROOTS"
FOUR_TREES="$(printf '%s\n' "$STYLE_FORMAT" "$STYLE_CHECK" "$TYPES" "$TESTS" "$SECURITY" | LC_ALL=C sort)"

failures=0
fail() { echo "FAIL $1"; sed 's/^/    /' "$TMP/out"; failures=$((failures + 1)); }
section() { [[ "$failures" -eq "$1" ]] && echo "PASS $2"; }

# Sets REPO to a fresh repository: develop holds the script and the tooling config, HEAD is a feature branch.
fixture() {
  REPO="$TMP/$1"
  mkdir -p "$REPO/scripts" "$REPO/docs"
  cp "$SOURCE_SCRIPT" "$REPO/scripts/python-validation.sh"
  echo '[project]' >"$REPO/pyproject.toml"
  echo '.tmp/' >"$REPO/.gitignore"
  echo '# docs' >"$REPO/docs/README.md"
  fg -C "$REPO" init -q -b develop
  fg -C "$REPO" config user.email fixture@example.invalid
  fg -C "$REPO" config user.name "Contract Fixture"
  fg -C "$REPO" add -A
  fg -C "$REPO" commit -q -m base
  fg -C "$REPO" checkout -q -b feature
}

validate() {
  : >"$TMP/uv.log"
  env "${SCRUB[@]}" PATH="$TMP/bin:$PATH" FAKE_UV_LOG="$TMP/uv.log" "$@" \
    "$REPO/scripts/python-validation.sh" "${ARGS[@]}" >"$TMP/out" 2>&1
  status=$?
}

calls() { cat "$TMP/uv.log"; }
sorted_calls() { LC_ALL=C sort "$TMP/uv.log"; }

# (a) usage errors exit 1 and --help exits 0, all before any uv call.
before=$failures
fixture a
for args in "" "--scope" "--scope --serial" "--base" "--bogus" "--scope nonsense"; do
  read -ra ARGS <<<"$args"
  validate env
  [[ "$status" -eq 1 ]] || fail "a: '$args' exited $status, wanted 1"
  [[ -z "$(calls)" ]] || fail "a: '$args' called uv"
done
ARGS=(--help)
validate env
[[ "$status" -eq 0 ]] || fail "a: --help exited $status"
grep -q '^Usage: python-validation.sh --scope <scope>' "$TMP/out" || fail "a: --help printed no usage"
section "$before" "a usage"

# (b) no uv on PATH exits 2.
before=$failures
mkdir -p "$TMP/nouv"
for tool in dirname date mkdir; do ln -sf "$(command -v "$tool")" "$TMP/nouv/$tool"; done
env "${SCRUB[@]}" PATH="$TMP/nouv" "$BASH" "$REPO/scripts/python-validation.sh" --scope style >"$TMP/out" 2>&1
status=$?
[[ "$status" -eq 2 ]] || fail "b: exited $status without uv, wanted 2"
grep -q 'uv is required' "$TMP/out" || fail "b: missing-uv message not printed"
section "$before" "b missing uv"

# (c) each single scope runs exactly its own commands, in order, and logs to .tmp/validate-logs/<tree>-*.log.
before=$failures
fixture c
for case in "deps|$DEPS" "style|$STYLE_FORMAT"$'\n'"$STYLE_CHECK" "types|$TYPES" "tests|$TESTS" \
  "security|$SECURITY"; do
  scope="${case%%|*}"
  ARGS=(--scope "$scope")
  validate env
  [[ "$status" -eq 0 ]] || fail "c: --scope $scope exited $status"
  [[ "$(calls)" == "${case#*|}" ]] || fail "c: --scope $scope called: $(calls | tr '\n' ';')"
  grep -q "PASS ${scope} " "$TMP/out" || fail "c: --scope $scope reported no PASS"
  compgen -G "$REPO/.tmp/validate-logs/${scope}-*.log" >/dev/null || fail "c: no $scope log written"
done
section "$before" "c single scopes"

# (d) a failing tree exits 1, names the tree and prints the tail of its log.
before=$failures
fixture d
ARGS=(--scope style)
validate env FAKE_UV_FAIL="ruff check"
[[ "$status" -eq 1 ]] || fail "d: failing style exited $status, wanted 1"
grep -q 'FAIL style (status=3' "$TMP/out" || fail "d: failing tree not named"
grep -q 'fake uv failing: run ruff check' "$TMP/out" || fail "d: log tail not printed"
section "$before" "d failing tree"

# (e) full: deps first, then the four trees; a failing deps stops before them.
before=$failures
fixture e
ARGS=(--scope full)
validate env
[[ "$status" -eq 0 ]] || fail "e: full exited $status"
[[ "$(head -n 1 "$TMP/uv.log")" == "$DEPS" ]] || fail "e: deps did not run first"
[[ "$(tail -n +2 "$TMP/uv.log" | LC_ALL=C sort)" == "$FOUR_TREES" ]] || fail "e: trees after deps differ"
validate env FAKE_UV_FAIL="sync"
[[ "$status" -eq 1 ]] || fail "e: failing deps exited $status, wanted 1"
[[ "$(calls)" == "$DEPS" ]] || fail "e: trees ran after a failing deps: $(calls | tr '\n' ';')"
section "$before" "e full"

# (f) trees run concurrently by default; --serial runs them one after another in declaration order.
before=$failures
fixture f
mkfifo "$TMP/rendezvous"
ARGS=(--scope changed)
echo 'x = 1' >"$REPO/scripts/new.py"
validate env FAKE_UV_FIFO="$TMP/rendezvous"
[[ "$status" -eq 0 ]] || fail "f: pytest and bandit did not run at the same time (exit $status)"
ARGS=(--scope changed --serial)
validate env
[[ "$status" -eq 0 ]] || fail "f: --serial exited $status"
[[ "$(calls)" == "$(printf '%s\n' "$STYLE_FORMAT" "$STYLE_CHECK" "$TYPES" "$TESTS" "$SECURITY")" ]] \
  || fail "f: --serial order: $(calls | tr '\n' ';')"
section "$before" "f parallel and --serial"

# (g) changed scope: nothing changed, or nothing Python-relevant, runs no tree.
before=$failures
fixture g
ARGS=(--scope changed)
validate env
[[ "$status" -eq 0 && -z "$(calls)" ]] || fail "g: clean tree exited $status or called uv"
grep -q 'No changed files detected.' "$TMP/out" || fail "g: clean tree not reported"
echo '# more' >>"$REPO/docs/README.md"
validate env
[[ "$status" -eq 0 && -z "$(calls)" ]] || fail "g: docs-only change exited $status or called uv"
grep -q 'No Python validation needed.' "$TMP/out" || fail "g: docs-only change not reported"
section "$before" "g changed, nothing to run"

# (h) changed scope: a committed nested script, a catalog data file or the catalog seed runs the four trees
# without deps.
before=$failures
for path in scripts/sub/tool.py tools/training/pkg/mod.py catalog/app/compose.yml \
  XE-Local-AI-Engine.Client.Application/Services/ExternalApps/Catalog/external-apps-catalog.seed.json; do
  fixture "h-${path//\//_}"
  mkdir -p "$REPO/$(dirname "$path")"
  echo 'changed' >"$REPO/$path"
  fg -C "$REPO" add -A
  fg -C "$REPO" commit -q -m change
  ARGS=(--scope changed)
  validate env
  [[ "$status" -eq 0 ]] || fail "h: $path exited $status"
  [[ "$(sorted_calls)" == "$FOUR_TREES" ]] || fail "h: $path called: $(calls | tr '\n' ';')"
done
section "$before" "h changed, Python trees"

# (i) changed scope: the tooling config, the lock, this script or .gitignore escalates to full (deps first).
before=$failures
for path in pyproject.toml uv.lock scripts/python-validation.sh .gitignore; do
  fixture "i-${path//\//_}"
  echo '# changed' >>"$REPO/$path"
  ARGS=(--scope changed)
  validate env
  [[ "$status" -eq 0 ]] || fail "i: $path exited $status"
  [[ "$(head -n 1 "$TMP/uv.log")" == "$DEPS" ]] || fail "i: $path did not escalate to full"
  [[ "$(tail -n +2 "$TMP/uv.log" | LC_ALL=C sort)" == "$FOUR_TREES" ]] || fail "i: $path trees differ"
done
section "$before" "i changed, full"

# (j) --base: an unresolvable base exits 1 before any tree; origin/<base> is used when no local branch exists.
before=$failures
fixture j
echo 'x = 1' >"$REPO/scripts/new.py"
ARGS=(--scope changed --base nowhere)
validate env
[[ "$status" -eq 1 && -z "$(calls)" ]] || fail "j: unresolvable base exited $status or called uv"
grep -q "cannot resolve --base 'nowhere'" "$TMP/out" || fail "j: unresolvable base not named"
fg -C "$REPO" update-ref refs/remotes/origin/release refs/heads/develop
ARGS=(--scope changed --base release)
validate env
[[ "$status" -eq 0 ]] || fail "j: origin/release base exited $status"
grep -q 'Changed-scope validation (base: release)' "$TMP/out" || fail "j: base not reported"
[[ "$(sorted_calls)" == "$FOUR_TREES" ]] || fail "j: origin/release base called: $(calls | tr '\n' ';')"
section "$before" "j --base"

# (k) VALIDATE_LOG_RETENTION keeps that many old logs per tree before the run adds its own.
before=$failures
fixture k
mkdir -p "$REPO/.tmp/validate-logs"
for day in 1 2 3; do
  touch -d "2026-01-0${day}" "$REPO/.tmp/validate-logs/style-old${day}.log"
done
ARGS=(--scope style)
validate env VALIDATE_LOG_RETENTION=2
[[ "$status" -eq 0 ]] || fail "k: exited $status"
[[ ! -e "$REPO/.tmp/validate-logs/style-old1.log" ]] || fail "k: the oldest log was kept"
[[ -e "$REPO/.tmp/validate-logs/style-old2.log" && -e "$REPO/.tmp/validate-logs/style-old3.log" ]] \
  || fail "k: a log within the retention was removed"
[[ "$(compgen -G "$REPO/.tmp/validate-logs/style-*.log" | wc -l)" -eq 3 ]] || fail "k: wrong log count"
section "$before" "k log retention"

if [[ "$failures" -ne 0 ]]; then
  echo "python-validation.test.sh: FAIL ($failures)"
  exit 1
fi
echo "python-validation.test.sh: PASS"
