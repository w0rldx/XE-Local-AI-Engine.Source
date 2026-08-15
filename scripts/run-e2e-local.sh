#!/usr/bin/env bash
# run-e2e-local.sh — OPT-IN local runner for the Playwright E2E suite.
#
# Why this exists
#   The E2E suite is excluded from the solution-wide `dotnet test` on purpose: the csproj
#   demotes itself to a plain library unless -p:RunE2ETests=true is set (see
#   XE-Local-AI-Engine.Tests.E2ETests.csproj, the PropertyGroup guarded on RunE2ETests).
#   Its only other entry point is .github/workflows/e2e.yml, which is deliberately NOT a merge
#   gate: it runs on manual dispatch or on a PR labelled `run-e2e` only. This script is the local
#   equivalent of that workflow: same property, same browser install, same TZ, plus preflight
#   checks that fail loudly instead of producing a vacuous green run.
#
#   It is OPT-IN by design. Nothing invokes it automatically. Run it by hand before cutting a
#   tester RC (publish/package-tester-win.ps1) or before merging a risky UI change.
#
# Usage:
#   scripts/run-e2e-local.sh [options]
#
# Options:
#   --filter <expr>          TUnit/MTP --treenode-filter expression, e.g.
#                            '/*/*/HostBootSmokeE2ETests/*'. Default: run everything.
#   --configuration <cfg>    Build configuration (default: Release; CI uses Release).
#   --skip-browser-install   Skip `playwright.ps1 install`. Only safe when the browsers for the
#                            pinned Microsoft.Playwright version are already in ~/.cache/ms-playwright.
#   --no-deps                Run `playwright.ps1 install` WITHOUT --with-deps (no sudo/apt).
#                            Use on machines where the OS deps are already present.
#   --list                   List discovered tests and exit without running them.
#   --help                   Show this message.
#
# Prerequisites (all checked up front, each with an actionable failure message):
#   - .NET SDK matching global.json
#   - pnpm + node          — this runner executes the frontend lint/typecheck prerequisite; the
#                            fixture runs `pnpm install --frozen-lockfile` and `pnpm run build:e2e`
#                            in XE-Local-AI-Engine.Client.React and serves the built dist from the
#                            .NET host (XEReactClientFixture). No vite dev server is involved.
#   - pwsh                  — Playwright's browser installer ships as playwright.ps1 only.
#   - Playwright browsers   — chromium + headless shell under ~/.cache/ms-playwright.
#
# No model runtime is required: the suite uses XE-Local-AI-Engine.Testing.FakeOllama and an
# in-process WebApplicationFactory host, not llama-server. It therefore does NOT need
# scripts/dev-stop.sh afterwards — no port or VRAM is held.
#
# Build contamination
#   This is the most expensive run in the repo to lose, and it has already been lost this way once:
#   a concurrent `dotnet build` rewrote the assemblies mid-run and the suite died with
#   `FileNotFoundException: Microsoft.AspNetCore.SignalR.Client.Core, Version=10.0.9.0` — nothing to
#   do with the tests. So the script re-execs itself under the cross-process build lock
#   (scripts/with-build-lock.sh) and snapshots the E2E output tree around the run
#   (scripts/assembly-guard.sh). See docs/agent-knowledge.md §1.
#
# Exit codes:
#   0  — all E2E tests passed (and a non-zero number of them actually ran)
#   1  — one or more tests failed, or the run was vacuous (zero tests discovered)
#   2  — a prerequisite is missing / usage error (nothing was run)
#   75 — CONTAMINATED: the build output changed mid-run; the result is void, re-run it
#
# Env knobs:
#   NO_BUILD_LOCK  when set, do NOT take the cross-process build lock (escape hatch)
#   NO_GUARD       when set, skip the contamination snapshot/verify

set -uo pipefail

PROJECT_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || (cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd))"

# Serialize against any other build/test that goes through the wrapper, before anything is built.
# The wrapper closes the lock fd in this child, so the MSBuild daemons left behind by the build
# below cannot inherit it (see with-build-lock.sh).
if [[ -z "${XE_BUILD_LOCK_HELD:-}" && -z "${NO_BUILD_LOCK:-}" ]]; then
  exec "${PROJECT_ROOT}/scripts/with-build-lock.sh" -- "${BASH_SOURCE[0]}" "$@"
fi
E2E_PROJECT="${PROJECT_ROOT}/XE-Local-AI-Engine.Tests.E2ETests/XE-Local-AI-Engine.Tests.E2ETests.csproj"
FRONTEND_DIR="${PROJECT_ROOT}/XE-Local-AI-Engine.Client.React"

CONFIGURATION="Release"
FILTER=""
SKIP_BROWSER_INSTALL="false"
WITH_DEPS="true"
LIST_ONLY="false"

log()  { echo "[e2e] $*"; }
fail() { echo "[e2e] FAIL: $*" >&2; exit 1; }
# Prerequisite failures exit 2 so a caller can tell "environment not ready" from "tests are red".
prereq_fail() { echo "[e2e] PREREQUISITE MISSING: $*" >&2; exit 2; }

usage() {
  sed -n '2,/^set -uo/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//; $d'
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --filter)                FILTER="${2:-}"; [[ -n "${FILTER}" ]] || prereq_fail "--filter needs an expression"; shift 2 ;;
    --configuration)         CONFIGURATION="${2:-}"; [[ -n "${CONFIGURATION}" ]] || prereq_fail "--configuration needs a value"; shift 2 ;;
    --skip-browser-install)  SKIP_BROWSER_INSTALL="true"; shift ;;
    --no-deps)               WITH_DEPS="false"; shift ;;
    --list)                  LIST_ONLY="true"; shift ;;
    --help|-h)               usage; exit 0 ;;
    *)                       echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

# ---------------------------------------------------------------------------
# Preflight — every failure names the exact fix.
# ---------------------------------------------------------------------------
log "=== Preflight ==="

[[ -f "${E2E_PROJECT}" ]] \
  || prereq_fail "E2E project not found at ${E2E_PROJECT}. Run this from inside the repository."

command -v dotnet >/dev/null 2>&1 \
  || prereq_fail "dotnet not on PATH. Install the SDK pinned in ${PROJECT_ROOT}/global.json."
log "dotnet     $(dotnet --version)"

command -v node >/dev/null 2>&1 \
  || prereq_fail "node not on PATH. The E2E fixture builds the React SPA before the host boots."
log "node       $(node --version)"

if command -v pnpm >/dev/null 2>&1; then
  log "pnpm       $(pnpm --version)"
else
  prereq_fail "pnpm not on PATH. XEReactClientFixture shells out to 'pnpm install --frozen-lockfile' and
             'pnpm run build' in ${FRONTEND_DIR} — a missing pnpm surfaces as an opaque
             InvalidOperationException from inside the fixture. Install pnpm (or enable corepack)."
fi

[[ -f "${FRONTEND_DIR}/package.json" ]] \
  || prereq_fail "React client not found at ${FRONTEND_DIR}. The fixture builds the SPA in-place and serves
             its dist/ from the .NET host; without it every test fails at fixture init."

if [[ "${LIST_ONLY}" != "true" ]]; then
  log "=== Frontend prerequisite (install + lint/typecheck) ==="
  (
    cd "${FRONTEND_DIR}" || exit 1
    pnpm install --frozen-lockfile && pnpm run lint
  ) || fail "React lint/typecheck failed; E2E would otherwise build with bare Vite and hide this defect."
fi

if ! command -v pwsh >/dev/null 2>&1; then
  prereq_fail "pwsh not on PATH. Playwright ships its browser installer as playwright.ps1 only.
             Install a contained copy with:
               dotnet tool install --global PowerShell
             then re-run. Use --skip-browser-install only if ~/.cache/ms-playwright is already populated."
fi
# shellcheck disable=SC2016  # $PSVersionTable is a PowerShell variable — it must NOT expand in bash.
log "pwsh       $(pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()')"

# ---------------------------------------------------------------------------
# Build — -p:RunE2ETests=true is mandatory on BOTH build and test.
# ---------------------------------------------------------------------------
log "=== Building E2E test app (${CONFIGURATION}, RunE2ETests=true) ==="
dotnet build "${E2E_PROJECT}" -p:RunE2ETests=true --configuration "${CONFIGURATION}" \
  || fail "E2E project build failed. Fix the compile errors above before running the suite."

TFM_DIR="$(find "${PROJECT_ROOT}/XE-Local-AI-Engine.Tests.E2ETests/bin/${CONFIGURATION}" -maxdepth 1 -type d -name 'net*' | head -n 1)"
[[ -n "${TFM_DIR}" ]] \
  || fail "Could not locate the build output under bin/${CONFIGURATION}. Did the build actually produce a TFM directory?"

PLAYWRIGHT_PS1="${TFM_DIR}/playwright.ps1"
[[ -f "${PLAYWRIGHT_PS1}" ]] \
  || fail "playwright.ps1 missing at ${PLAYWRIGHT_PS1}. It is emitted by the Microsoft.Playwright package —
       a missing file means the build did not restore Microsoft.Playwright, or RunE2ETests=true was not applied."

# ---------------------------------------------------------------------------
# Browsers
# ---------------------------------------------------------------------------
if [[ "${SKIP_BROWSER_INSTALL}" == "true" ]]; then
  log "=== Skipping Playwright browser install (--skip-browser-install) ==="
  if ! compgen -G "${HOME}/.cache/ms-playwright/chromium-*" >/dev/null; then
    prereq_fail "--skip-browser-install was given but no chromium build exists under ~/.cache/ms-playwright.
             Every test would fail with \"Executable doesn't exist\". Re-run without the flag."
  fi
else
  log "=== Installing Playwright browsers ==="
  if [[ "${WITH_DEPS}" == "true" ]]; then
    # --with-deps runs apt-get and needs root. On a sudo-less box, retry without it and let the
    # browser-launch failure (if any) be the honest signal rather than pretending deps are fine.
    if ! pwsh -NoProfile -File "${PLAYWRIGHT_PS1}" install --with-deps chromium; then
      log "WARN: 'install --with-deps' failed (usually: no sudo / not a Debian-family image)."
      log "WARN: retrying without --with-deps; if chromium then fails to launch, the OS libs are genuinely missing."
      pwsh -NoProfile -File "${PLAYWRIGHT_PS1}" install chromium \
        || prereq_fail "Playwright browser install failed. Install chromium's OS dependencies manually
             (see 'pwsh ${PLAYWRIGHT_PS1} install-deps') and re-run."
    fi
  else
    pwsh -NoProfile -File "${PLAYWRIGHT_PS1}" install chromium \
      || prereq_fail "Playwright browser install failed. Re-run without --no-deps, or install chromium's
             OS dependencies manually."
  fi
fi

# ---------------------------------------------------------------------------
# Run
# ---------------------------------------------------------------------------
TEST_ARGS=("${E2E_PROJECT}" --configuration "${CONFIGURATION}" --no-build -p:RunE2ETests=true)
if [[ "${LIST_ONLY}" == "true" ]]; then
  # Discovery must go through the NATIVE MTP test-host exe. `dotnet test ... -- --list-tests`
  # reports "Zero tests ran / total: 0" even though the same assembly discovers 64 tests when the
  # host is invoked directly — the dotnet-test wrapper swallows the discovery-only mode.
  TEST_HOST="${TFM_DIR}/XE-Local-AI-Engine.Tests.E2ETests"
  [[ -x "${TEST_HOST}" ]] \
    || fail "native MTP test host not found at ${TEST_HOST}. On Windows the csproj sets UseAppHost=false;
       use 'dotnet ${TFM_DIR}/XE-Local-AI-Engine.Tests.E2ETests.dll --list-tests' there instead."
  log "=== Listing discovered tests (native MTP host) ==="
  exec "${TEST_HOST}" --list-tests
fi
if [[ -n "${FILTER}" ]]; then
  # MTP takes --treenode-filter (NOT VSTest's --filter); everything after `--` goes to the test host.
  TEST_ARGS+=(-- --treenode-filter "${FILTER}")
  log "Filter: ${FILTER}"
fi

log "=== Running E2E suite ==="
log "First run is slow: the fixture does 'pnpm install --frozen-lockfile' + 'pnpm run build:e2e'."
OUT_FILE="$(mktemp -t xe-e2e-XXXXXX.log)"
GUARD_STATE=""
# The log has to survive every exit path below (the guards and the failure diagnosis all grep it),
# so cleanup goes on a trap rather than being sprinkled before each exit.
trap 'rm -f "${OUT_FILE}" "${GUARD_STATE}"' EXIT

# Snapshot AFTER our own build and browser install, immediately before the first test process — so
# this script's own writes are outside the window and cannot read as interference.
if [[ -z "${NO_GUARD:-}" ]]; then
  mkdir -p "${PROJECT_ROOT}/.tmp"
  GUARD_STATE="$(mktemp "${PROJECT_ROOT}/.tmp/assembly-guard-e2e-XXXXXX.state")"
  "${PROJECT_ROOT}/scripts/assembly-guard.sh" snapshot "${GUARD_STATE}" --root "${TFM_DIR}"
fi

# TZ matches .github/workflows/e2e.yml so date-formatting assertions behave identically.
TZ="Europe/Berlin" dotnet test "${TEST_ARGS[@]}" 2>&1 | tee "${OUT_FILE}"
STATUS="${PIPESTATUS[0]}"

# ---------------------------------------------------------------------------
# Contamination check runs FIRST. A run whose assemblies were rewritten underneath it can fail in
# any shape at all — including a vacuous zero-test summary — so it must be diagnosed as
# contamination rather than routed into the failure explanations below, which would all be wrong.
# ---------------------------------------------------------------------------
if [[ -n "${GUARD_STATE}" ]]; then
  if ! "${PROJECT_ROOT}/scripts/assembly-guard.sh" verify "${GUARD_STATE}"; then
    log "The suite's own exit status was ${STATUS}; ignore it and re-run."
    exit 75
  fi
fi

# ---------------------------------------------------------------------------
# Vacuous-run guard — mirrors _assert_tests_ran in .opencode/scripts/project-validate.sh.
# Without RunE2ETests=true the project is a library, `dotnet test` finds nothing, and exit 0
# would otherwise read as a green E2E run.
# ---------------------------------------------------------------------------
if grep -qiE 'total:[[:space:]]*0([^0-9]|$)|zero tests ran' "${OUT_FILE}"; then
  fail "runner discovered ZERO tests — -p:RunE2ETests=true did not take effect. This is not a pass."
fi
if ! grep -qiE 'total:[[:space:]]*[1-9]' "${OUT_FILE}"; then
  fail "no MTP test summary found — the E2E project did not run as a test app. This is not a pass."
fi

if [[ "${STATUS}" -ne 0 ]]; then
  echo
# The runner already completed frontend lint/typecheck before the fixture's intentionally bare
# build:e2e. A fixture build failure here is therefore a Vite/bundling failure, not hidden lint.
  if grep -q "Process 'pnpm run build:e2e' exited with code" "${OUT_FILE}" 2>/dev/null; then
    log "ROOT CAUSE: the React client failed to build, so the E2E fixture could not serve the SPA."
    log "Every SPA-dependent test failed at fixture init — this is NOT 62 independent test failures."
    log "Reproduce and fix it directly:"
    log "  cd ${FRONTEND_DIR} && pnpm run build"
    grep -oE "error TS[0-9]+: [^\"]*" "${OUT_FILE}" 2>/dev/null | sort -u | sed 's/^/[e2e]   /'
    exit 1
  fi
  log "Suite FAILED. Playwright traces for failing tests (if any) were written to:"
  log "  ${PROJECT_ROOT}/XE-Local-AI-Engine.Tests.E2ETests/**/test-results/traces/"
  log "Open one with: pwsh ${PLAYWRIGHT_PS1} show-trace <trace.zip>"
  exit 1
fi

echo
log "Suite PASSED."
