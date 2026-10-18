#!/usr/bin/env bash
# Contract test for scripts/assembly-guard.sh against a throwaway output tree: a clean run passes the
# wrapped command's status through; a tracked file rewritten mid-run is exit 75 CONTAMINATED even when the
# command also failed; untracked files (logs, .trx) never trip it; --test-bins finds *.Tests*/bin/*/net*;
# the dotted, extensionless MTP apphost is tracked.
set -euo pipefail

GUARD="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)/assembly-guard.sh"
TMP="$(mktemp -d)"
trap 'rm -rf -- "${TMP}"' EXIT
BIN="${TMP}/XE-Local-AI-Engine.Tests/bin/Release/net10.0"
mkdir -p "${BIN}"
printf 'v1' >"${BIN}/XE-Local-AI-Engine.Tests.dll"
printf '{}' >"${BIN}/XE-Local-AI-Engine.Tests.runtimeconfig.json"

fail() { echo "FAIL: $*" >&2; exit 1; }
guard() { set +e; "${GUARD}" guard --state "${TMP}/state" --root "${BIN}" -- "$@" >"${TMP}/out" 2>&1; rc=$?; set -e; }

guard true
[[ ${rc} -eq 0 ]] || { cat "${TMP}/out" >&2; fail "clean run exited ${rc}"; }
guard bash -c 'exit 7'
[[ ${rc} -eq 7 ]] || fail "clean failing run exited ${rc}, wanted the command's 7"
guard bash -c "printf log >'${BIN}/run.log'; printf x >'${BIN}/result.trx'"
[[ ${rc} -eq 0 ]] || { cat "${TMP}/out" >&2; fail "untracked log/trx writes tripped the guard (${rc})"; }

guard bash -c "printf v2-longer >'${BIN}/XE-Local-AI-Engine.Tests.dll'"
[[ ${rc} -eq 75 ]] || fail "rewritten dll exited ${rc}, wanted 75"
grep -q 'CONTAMINATED RUN' "${TMP}/out" || fail "no CONTAMINATED banner"
grep -q 'CHANGED .*XE-Local-AI-Engine.Tests.dll' "${TMP}/out" || fail "changed file not named"
guard bash -c "rm '${BIN}/XE-Local-AI-Engine.Tests.runtimeconfig.json'; exit 1"
[[ ${rc} -eq 75 ]] || fail "removed runtimeconfig plus a failing command exited ${rc}, wanted 75"

# --test-bins resolves from the caller's checkout: a throwaway repository holding the output tree.
git -C "${TMP}" init -q
set +e; (cd "${TMP}" && "${GUARD}" guard --test-bins -- bash -c "printf v3 >'${BIN}/XE-Local-AI-Engine.Tests.dll'") >"${TMP}/out" 2>&1; rc=$?; set -e
[[ ${rc} -eq 75 ]] || { cat "${TMP}/out" >&2; fail "--test-bins did not watch *.Tests*/bin (${rc})"; }

# The MTP apphost is an extensionless executable NAMED AFTER THE PROJECT, dots included. A guard that keys
# "apphost" on "no dot in the name" never sees it, and a rewritten apphost between batches goes unreported.
printf 'host-v1' >"${BIN}/XE-Local-AI-Engine.Tests"; chmod u+x "${BIN}/XE-Local-AI-Engine.Tests"
guard true
[[ ${rc} -eq 0 ]] || fail "fresh snapshot with an apphost should pass (rc=${rc})"
guard bash -c "printf host-v2-longer >'${BIN}/XE-Local-AI-Engine.Tests'"
[[ ${rc} -eq 75 ]] || fail "rewritten dotted apphost must be exit 75 (rc=${rc}): $(cat "${TMP}/out")"
grep -q 'CHANGED .*/XE-Local-AI-Engine.Tests (size' "${TMP}/out" || fail "changed apphost not named: $(cat "${TMP}/out")"

echo "assembly-guard.test.sh: PASS"
