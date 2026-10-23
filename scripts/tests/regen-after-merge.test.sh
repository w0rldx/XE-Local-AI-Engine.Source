#!/usr/bin/env bash
# Contract test for scripts/regen-after-merge.sh. Each case builds a throwaway repository holding the frontend's
# generated paths, puts a fake `pnpm` first on PATH that records each step and rewrites files (generated ones and
# one bystander), and asserts the exit code and the exact staged set. Nothing here touches this checkout.

set -uo pipefail

ROOT="$(cd "$(dirname "$(realpath "${BASH_SOURCE[0]}")")/../.." && pwd)"
SCRIPT="$ROOT/scripts/regen-after-merge.sh"
TMP="$(realpath "$(mktemp -d)")"
trap 'rm -rf "$TMP"' EXIT

# Fixture git calls and the script's own run with the caller's repository selectors and config removed, and
# discovery bounded by $TMP, so nothing can reach the enclosing repository.
SCRUB=()
for var in $(compgen -v GIT_); do SCRUB+=(-u "$var"); done
SCRUB+=(GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1 GIT_CEILING_DIRECTORIES="$TMP")
fg() { env "${SCRUB[@]}" git "$@"; }

mkdir -p "$TMP/bin"
cat >"$TMP/bin/pnpm" <<'FAKE_PNPM'
#!/usr/bin/env bash
# Fake pnpm: `pnpm run <step>` in the frontend directory. Logs the step, fails on FAKE_PNPM_FAIL, else rewrites.
[[ "$1" == run ]] || exit 64
echo "$2" >>"$FAKE_PNPM_LOG"
[[ "$2" == "${FAKE_PNPM_FAIL:-}" ]] && exit 1
case "$2" in
  openapi:generate)
    echo changed >>src/core/api/generated/client.ts
    rm src/core/api/generated/stale.ts
    echo new >src/core/api/generated/added.ts
    echo bystander >>src/other.ts ;;
  licenses:generate)
    echo '{"changed":true}' >src/features/about/data/third-party-licenses.generated.json ;;
  locales:sort)
    echo '{"a":1}' >src/locales/en.json
    echo stray >src/locales/notes.txt ;;
  *) exit 65 ;;
esac
FAKE_PNPM
chmod +x "$TMP/bin/pnpm"

failures=0
fail() { echo "FAIL $1"; sed 's/^/    /' "$TMP/out"; failures=$((failures + 1)); }

# Sets REPO and SPA to a fresh committed fixture.
fixture() {
  REPO="$TMP/$1" SPA="$TMP/$1/XE-Local-AI-Engine.Client.React"
  mkdir -p "$SPA/openapi" "$SPA/src/core/api/generated" "$SPA/src/features/about/data" "$SPA/src/locales"
  echo '{}' >"$SPA/package.json"
  echo '{}' >"$SPA/openapi/v1.json"
  echo client >"$SPA/src/core/api/generated/client.ts"
  echo stale >"$SPA/src/core/api/generated/stale.ts"
  echo '{}' >"$SPA/src/features/about/data/third-party-licenses.generated.json"
  echo '{}' >"$SPA/src/locales/en.json"
  echo '{}' >"$SPA/src/locales/de.json"
  echo other >"$SPA/src/other.ts"
  fg -C "$REPO" init -q
  fg -C "$REPO" config user.email fixture@example.invalid
  fg -C "$REPO" config user.name "Contract Fixture"
  fg -C "$REPO" add -A
  fg -C "$REPO" commit -q -m base
}

regen() {
  : >"$TMP/pnpm.log"
  env "${SCRUB[@]}" PATH="$TMP/bin:$PATH" FAKE_PNPM_LOG="$TMP/pnpm.log" "$@" "$SCRIPT" --repo "$REPO" >"$TMP/out" 2>&1
  status=$?
}

staged() { fg -C "$REPO" diff --cached --name-status | LC_ALL=C sort; }

# (a) every step runs in order and exactly the generated paths are staged, the deletion included.
fixture a
regen env
[[ "$status" -eq 0 ]] || fail "a: exit $status, wanted 0"
[[ "$(cat "$TMP/pnpm.log")" == $'openapi:generate\nlicenses:generate\nlocales:sort' ]] \
  || fail "a: steps ran as $(tr '\n' ' ' <"$TMP/pnpm.log")"
want="A	XE-Local-AI-Engine.Client.React/src/core/api/generated/added.ts
D	XE-Local-AI-Engine.Client.React/src/core/api/generated/stale.ts
M	XE-Local-AI-Engine.Client.React/src/core/api/generated/client.ts
M	XE-Local-AI-Engine.Client.React/src/features/about/data/third-party-licenses.generated.json
M	XE-Local-AI-Engine.Client.React/src/locales/en.json"
[[ "$(staged)" == "$want" ]] || { fail "a: staged set differs"; staged | sed 's/^/    got /'; }
grep -Fxq '  src/locales/en.json' "$TMP/out" || fail "a: staged list not printed"
if [[ "$(fg -C "$REPO" status --porcelain -- XE-Local-AI-Engine.Client.React/src/other.ts)" != " M "* ]]; then
  fail "a: the bystander was staged or not changed"
fi
[[ "$failures" -eq 0 ]] && echo "PASS a happy path"

# (b) a failing generator stops the run with exit 1 and stages nothing, though earlier steps changed files.
before=$failures
fixture b
regen env FAKE_PNPM_FAIL=licenses:generate
[[ "$status" -eq 1 ]] || fail "b: exit $status, wanted 1"
[[ "$(cat "$TMP/pnpm.log")" == $'openapi:generate\nlicenses:generate' ]] || fail "b: ran past the failing step"
[[ -z "$(staged)" ]] || fail "b: something was staged"
grep -Fq 'FAIL pnpm run licenses:generate' "$TMP/out" || fail "b: failing step not named"
[[ "$failures" -eq "$before" ]] && echo "PASS b failing generator"

# (c) usage errors exit 2 before any step.
before=$failures
env "${SCRUB[@]}" PATH="$TMP/bin:$PATH" "$SCRIPT" --bogus >"$TMP/out" 2>&1
[[ $? -eq 2 ]] || fail "c: unknown flag did not exit 2"
env "${SCRUB[@]}" PATH="$TMP/bin:$PATH" "$SCRIPT" --repo "$TMP/nowhere" >"$TMP/out" 2>&1
[[ $? -eq 2 ]] || fail "c: missing frontend did not exit 2"
[[ "$failures" -eq "$before" ]] && echo "PASS c usage errors"

if [[ "$failures" -ne 0 ]]; then
  echo "regen-after-merge.test.sh: FAIL ($failures)"
  exit 1
fi
echo "regen-after-merge.test.sh: PASS"
