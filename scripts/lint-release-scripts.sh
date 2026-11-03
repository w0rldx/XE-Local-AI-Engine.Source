#!/usr/bin/env bash
# lint-release-scripts.sh — static analysis for the release-critical shell + PowerShell scripts.
#
# Why this exists
#   The tag-triggered GitHub workflow is the official release path. The deprecated manual
#   packagers remain useful reference and recovery surfaces, so they still receive static analysis:
#   a defect in a gate itself (for example `@($null).Count` being 1) must not pass unnoticed.
#
# What it does
#   - shellcheck over the release-path shell scripts
#   - PSScriptAnalyzer over the release-path PowerShell scripts
#
# Missing tools are a LOUD FAILURE (exit 2), never a silent pass. A linter that no-ops when
# absent is worse than no linter: it manufactures a green result nobody earned.
#
# Usage:
#   scripts/lint-release-scripts.sh [options]
#
# Options:
#   --shell-only   Run shellcheck only.
#   --ps-only      Run PSScriptAnalyzer only.
#   --pester      Explicitly request the Pester suite (it is already part of the default run).
#   --pester-only  Run only the Pester suite.
#   --no-behavior  Skip the auto-enrolled release contract tests (for callers that just ran them,
#                  e.g. the release-contracts CI job).
#   --bootstrap    Install PSScriptAnalyzer (CurrentUser scope) if absent, then lint.
#                  Network access required. Without this flag, an absent module fails the run.
#   --help         Show this message.
#
# PSScriptAnalyzer rule selection lives in scripts/PSScriptAnalyzerSettings.psd1, so the same
# exclusions apply however the analyzer is invoked (script, editor, or by hand).
#
# Exit codes:
#   0  — all linters ran and reported no findings at or above the configured severity
#   1  — findings were reported
#   2  — a required linter is missing (nothing was checked) / usage error

set -uo pipefail

# It lints, builds and runs the contract tests of the checkout it lives in — from its own (real) location,
# never the caller's working directory or a caller's GIT_DIR/GIT_WORK_TREE (scripts/lib/git-env.sh, whose
# loading is mandatory).
PROJECT_ROOT="$(cd "$(dirname "$(realpath "${BASH_SOURCE[0]}")")/.." && pwd)"
# shellcheck source=scripts/lib/git-env.sh
source "${PROJECT_ROOT}/scripts/lib/git-env.sh" \
  || { echo "[lint] ERROR: cannot load ${PROJECT_ROOT}/scripts/lib/git-env.sh — copy scripts/lib/ along with this script." >&2; exit 2; }
xe_drop_git_repo_env

# Release-path shell scripts. Deliberately scoped: .tmp/worktrees is scratch, not release path.
SHELL_TARGETS=(
  "install.sh"
  "publish/package-rc.sh"
  "publish/linux/run-xe-local-ai-engine.sh"
  "publish/linux/uninstall-xe-local-ai-engine.sh"
  "scripts/generate-release-notes.sh"
  "scripts/dev-aspire-common.sh"
  "scripts/dev-start.sh"
  "scripts/dev-status.sh"
  "scripts/dev-stop.sh"
  "scripts/aspire-readiness-smoke.sh"
  "scripts/compliance/tests/sbom-tool-wrapper.test.sh"
  "scripts/openapi-live-check.sh"
  "scripts/run-release-contract-tests.sh"
  "scripts/tests/coverage-merge.test.sh"
  "scripts/tests/dev-aspire-helpers.test.sh"
  "scripts/tests/dev-stop-select.test.sh"
  "scripts/tests/gpu-smoke.test.sh"
  "scripts/tests/run-retrieval-eval-local.test.sh"
  "scripts/tests/openapi-live-check.test.sh"
  "scripts/tests/release-authority.test.sh"
  "scripts/run-backend-tests.sh"
  "scripts/run-tests-memory-safe.sh"
  "scripts/run-e2e-local.sh"
  "scripts/run-gpu-smoke-local.sh"
  "scripts/run-model-matrix-local.sh"
  "scripts/tests/model-matrix.test.sh"
  "scripts/run-tool-grammar-smoke-local.sh"
  "scripts/run-retrieval-eval-local.sh"
  "scripts/run-docker-smoke-local.sh"
  "scripts/run-agent-framework-validation.sh"
  "scripts/capture-agent-framework-dependencies.sh"
  "scripts/lint-release-scripts.sh"
  # Not release-path, but they gate the trustworthiness of every test result the release leans on —
  # a bug in the contamination guard reads as a phantom regression or, worse, hides a real one.
  "scripts/with-build-lock.sh"
  "scripts/lib/build-lock-common.sh"
  "scripts/lib/git-env.sh"
  "scripts/build-lock-status.sh"
  "scripts/tests/build-lock.test.sh"
  "scripts/tests/entry-point-location.test.sh"
  "scripts/lib/test-sizing.sh"
  "scripts/tests/test-sizing.test.sh"
  "scripts/assembly-guard.sh"
)

RELEASE_CONTRACT_RUNNER="scripts/run-release-contract-tests.sh"

# Release-path PowerShell scripts, plus the Windows ports of the two test-integrity guards. The
# guards are not on the release path, but they are the only thing standing between a Windows agent
# and a contaminated test run — the same reason the .sh originals are shellcheck'd here.
PS_TARGETS=(
  "install.ps1"
  "publish/package-tester-win.ps1"
  "publish/windows/uninstall-xe-local-ai-engine.ps1"
  "scripts/tests/windows-framework-launcher-smoke.ps1"
  "scripts/assembly-guard.ps1"
  "scripts/with-build-lock.ps1"
)

RUN_SHELL="true"
RUN_PS="true"
RUN_PESTER="true"
RUN_BEHAVIOR="true"
PESTER_DIRS=("publish/tests" "scripts/performance/tests")
BOOTSTRAP="false"
PSSA_SETTINGS="${PROJECT_ROOT}/scripts/PSScriptAnalyzerSettings.psd1"

log()         { echo "[lint] $*"; }
prereq_fail() { echo "[lint] PREREQUISITE MISSING: $*" >&2; exit 2; }

usage() {
  sed -n '2,/^set -uo/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//; $d'
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --shell-only) RUN_PS="false"; RUN_PESTER="false"; RUN_BEHAVIOR="false"; shift ;;
    --ps-only)    RUN_SHELL="false"; RUN_PESTER="false"; RUN_BEHAVIOR="false"; shift ;;
    --pester)     RUN_PESTER="true"; shift ;;
    --no-behavior) RUN_BEHAVIOR="false"; shift ;;
    --pester-only) RUN_SHELL="false"; RUN_PS="false"; RUN_PESTER="true"; RUN_BEHAVIOR="false"; shift ;;
    --bootstrap)  BOOTSTRAP="true"; shift ;;
    --help|-h)    usage; exit 0 ;;
    *)            echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

FINDINGS=0

if [[ "${RUN_SHELL}" == "true" ]]; then
  command -v shellcheck >/dev/null 2>&1 || prereq_fail \
    "shellcheck not on PATH. Install it before releasing:
             mise use -g shellcheck   (this repo already resolves shellcheck via mise)
             — or —  apt-get install shellcheck  /  brew install shellcheck
             Re-run with --ps-only ONLY if you have separately verified the shell scripts."
  shellcheck_version="$(shellcheck --version | awk '/^version:/ { print $2 }')"
  # 0.9.x reports SC2317 (unreachable) and SC2015 (`a && b || true`) false positives that 0.10.0
  # removed; at --severity=style those fail the pass on scripts that are clean under a current
  # release. Refuse the stale linter outright rather than let it grade the scripts wrong.
  if [[ "$(printf '%s\n' "0.10.0" "${shellcheck_version}" | sort -V | head -n1)" != "0.10.0" ]]; then
    prereq_fail "shellcheck ${shellcheck_version} is too old (need >= 0.10.0; 0.9.x has SC2317/SC2015 false positives). Upgrade: mise use -g shellcheck"
  fi
  log "shellcheck ${shellcheck_version}"

  existing_shell=()
  for target in "${SHELL_TARGETS[@]}"; do
    if [[ -f "${PROJECT_ROOT}/${target}" ]]; then
      existing_shell+=("${PROJECT_ROOT}/${target}")
    else
      # A renamed or deleted release script must not silently drop out of the lint set.
      echo "[lint] FAIL: expected shell target '${target}' does not exist. Update SHELL_TARGETS." >&2
      FINDINGS=1
    fi
  done

  if [[ ${#existing_shell[@]} -gt 0 ]]; then
    log "=== shellcheck (${#existing_shell[@]} file(s)) ==="
    # -x follows `source`d files; severity style catches the low-severity-but-real issues
    # (unquoted expansions, useless cat) that bite release scripts.
    if shellcheck --severity=style --external-sources --format=tty "${existing_shell[@]}"; then
      log "shellcheck: clean"
    else
      FINDINGS=1
    fi
  fi
fi

if [[ "${RUN_PS}" == "true" ]]; then
  command -v pwsh >/dev/null 2>&1 || prereq_fail \
    "pwsh not on PATH — publish/package-tester-win.ps1 is the entire release gate and cannot be linted.
             Install a contained copy with:
               dotnet tool install --global PowerShell
             Re-run with --shell-only ONLY if you have separately verified the PowerShell scripts."

  if ! pwsh -NoProfile -Command 'if (-not (Get-Module -ListAvailable PSScriptAnalyzer)) { exit 1 }'; then
    if [[ "${BOOTSTRAP}" == "true" ]]; then
      log "PSScriptAnalyzer absent — installing (CurrentUser scope) because --bootstrap was given."
      pwsh -NoProfile -Command 'Install-Module PSScriptAnalyzer -Scope CurrentUser -Force -AcceptLicense -ErrorAction Stop' \
        || prereq_fail "PSScriptAnalyzer install failed (offline? PSGallery unreachable?)."
    else
      prereq_fail \
        "PSScriptAnalyzer module is not installed, so the release gate script was NOT checked.
             Install it with either:
               scripts/lint-release-scripts.sh --bootstrap
               pwsh -NoProfile -Command 'Install-Module PSScriptAnalyzer -Scope CurrentUser -Force -AcceptLicense'
             This is intentionally fatal: a skipped PowerShell lint must never read as a pass."
    fi
  fi

  [[ -f "${PSSA_SETTINGS}" ]] || prereq_fail \
    "PSScriptAnalyzer settings file missing at ${PSSA_SETTINGS}. Without it the rule exclusions are
             undefined and the run would not be reproducible."

  ps_version="$(pwsh -NoProfile -Command '(Get-Module -ListAvailable PSScriptAnalyzer | Select-Object -First 1).Version.ToString()')"
  log "PSScriptAnalyzer ${ps_version} (settings: scripts/PSScriptAnalyzerSettings.psd1)"

  for target in "${PS_TARGETS[@]}"; do
    if [[ ! -f "${PROJECT_ROOT}/${target}" ]]; then
      echo "[lint] FAIL: expected PowerShell target '${target}' does not exist. Update PS_TARGETS." >&2
      FINDINGS=1
      continue
    fi

    log "=== PSScriptAnalyzer: ${target} ==="
    # Invoke-ScriptAnalyzer returns findings as objects, not a non-zero exit code, so the
    # script itself decides the exit status from the finding count.
    if ! pwsh -NoProfile -Command "
        \$ErrorActionPreference = 'Stop'
        \$results = Invoke-ScriptAnalyzer -Path '${PROJECT_ROOT}/${target}' -Settings '${PSSA_SETTINGS}' -Recurse:\$false
        if (\$null -eq \$results) { \$results = @() }
        # NOTE: @(\$null).Count is 1 in PowerShell — the null check above is why this counts correctly.
        if (\$results.Count -eq 0) { Write-Host 'clean'; exit 0 }
        \$results |
          Sort-Object Severity, Line |
          Format-Table -AutoSize Severity, Line, RuleName, Message |
          Out-String -Width 200 |
          Write-Host
        Write-Host (\"{0} finding(s)\" -f \$results.Count)
        exit 1
      "; then
      FINDINGS=1
    fi
  done
fi

# Behavioral shell tests — part of the default validation, never implicit.
if [[ "${RUN_BEHAVIOR}" == "true" ]]; then
  log "=== auto-enrolled release contract tests ==="
  behavior_output="$("${PROJECT_ROOT}/${RELEASE_CONTRACT_RUNNER}" 2>&1)"
  behavior_status=$?
  printf '%s\n' "${behavior_output}"
  if [[ "${behavior_status}" -ne 0 ]] \
      || ! grep -Fxq 'run-release-contract-tests.sh: PASS' <<<"${behavior_output}"; then
    echo "[lint] FAIL: ${RELEASE_CONTRACT_RUNNER} failed or omitted its non-vacuous pass marker." >&2
    FINDINGS=1
  else
    log "release contract tests: passed"
  fi
fi

# Pester — unit tests over package-tester-win.ps1's pure logic and the Windows VRAM capture
# script's privacy contract. Both extract their subjects from the real .ps1 via the AST, so they
# run anywhere pwsh does; neither needs Windows or a GPU.
if [[ "${RUN_PESTER}" == "true" ]]; then
  command -v pwsh >/dev/null 2>&1 || prereq_fail \
    "pwsh not on PATH — the Pester suite could not run. dotnet tool install --global PowerShell"

  for pester_dir in "${PESTER_DIRS[@]}"; do
    [[ -d "${PROJECT_ROOT}/${pester_dir}" ]] || prereq_fail \
      "Pester suite not found at ${pester_dir}."
  done

  if ! pwsh -NoProfile -Command 'if (-not (Get-Module -ListAvailable Pester)) { exit 1 }'; then
    prereq_fail \
      "Pester module is not installed, so package-tester-win.ps1's logic was NOT tested.
             Install it with:
               pwsh -NoProfile -Command 'Install-Module Pester -Scope CurrentUser -Force -AcceptLicense -SkipPublisherCheck'
             Intentionally fatal: a skipped test suite must never read as a pass."
  fi

  cd "${PROJECT_ROOT}" || prereq_fail "could not cd to ${PROJECT_ROOT}"
  log "=== Pester: ${PESTER_DIRS[*]} ==="
  # Render the suite list as a PowerShell array literal: @('a','b').
  pester_paths_ps="$(printf "'%s'," "${PESTER_DIRS[@]}")"
  pester_paths_ps="@(${pester_paths_ps%,})"
  # An explicit configuration rather than -CI: that switch also writes a testResults.xml into the
  # repo root, which is not ours to litter. PassThru lets us apply the same vacuous-run guard used
  # everywhere else here — zero discovered tests is a FAILURE, not a pass.
  if ! pwsh -NoProfile -Command "
      \$config = New-PesterConfiguration
      \$config.Run.Path = ${pester_paths_ps}
      \$config.Run.PassThru = \$true
      \$config.TestResult.Enabled = \$false
      \$config.Output.Verbosity = 'Detailed'
      \$result = Invoke-Pester -Configuration \$config
      if (\$result.TotalCount -eq 0) {
        Write-Error 'Pester discovered ZERO tests — this is not a pass.'
        exit 2
      }
      if (\$result.FailedCount -gt 0) { exit 1 }
      exit 0
    "; then
    echo "[lint] FAIL: Pester suite failed (see above)." >&2
    FINDINGS=1
  fi
fi

echo
if [[ "${FINDINGS}" -ne 0 ]]; then
  log "RESULT: findings reported (see above)."
  exit 1
fi
log "RESULT: clean."
