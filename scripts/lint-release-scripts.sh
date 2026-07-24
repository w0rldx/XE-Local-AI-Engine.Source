#!/usr/bin/env bash
# lint-release-scripts.sh — static analysis for the release-critical shell + PowerShell scripts.
#
# Why this exists
#   GitHub Actions is disabled for this repo, so publish/package-tester-win.ps1 is the ONLY
#   release path and the ONLY quality gate. Neither it nor publish/package-rc.sh had any static
#   analysis: a defect in the gate itself (e.g. `@($null).Count` being 1, which made the
#   vulnerability audit throw on a clean solution) shipped unnoticed because nothing looked at it.
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
#   --shell-only   Run shellcheck only (skip PowerShell).
#   --ps-only      Run PSScriptAnalyzer only (skip shell).
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

PROJECT_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || (cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd))"

# Release-path shell scripts. Deliberately scoped: .opencode/ has its own validators
# (validate-opencode.sh / validate-no-legacy.sh) and .tmp/worktrees is scratch.
SHELL_TARGETS=(
  "publish/package-rc.sh"
  "publish/linux/run-xe-local-ai-engine.sh"
  "publish/linux/uninstall-xe-local-ai-engine.sh"
  "scripts/generate-release-notes.sh"
  "scripts/dev-stop.sh"
  "scripts/run-tests-memory-safe.sh"
  "scripts/run-e2e-local.sh"
  "scripts/lint-release-scripts.sh"
)

# Release-path PowerShell scripts.
PS_TARGETS=(
  "publish/package-tester-win.ps1"
  "publish/windows/uninstall-xe-local-ai-engine.ps1"
)

RUN_SHELL="true"
RUN_PS="true"
BOOTSTRAP="false"
PSSA_SETTINGS="${PROJECT_ROOT}/scripts/PSScriptAnalyzerSettings.psd1"

log()         { echo "[lint] $*"; }
prereq_fail() { echo "[lint] PREREQUISITE MISSING: $*" >&2; exit 2; }

usage() {
  sed -n '2,/^set -uo/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//; $d'
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --shell-only) RUN_PS="false"; shift ;;
    --ps-only)    RUN_SHELL="false"; shift ;;
    --bootstrap)  BOOTSTRAP="true"; shift ;;
    --help|-h)    usage; exit 0 ;;
    *)            echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

FINDINGS=0

# ---------------------------------------------------------------------------
# ShellCheck pass
# ---------------------------------------------------------------------------
if [[ "${RUN_SHELL}" == "true" ]]; then
  command -v shellcheck >/dev/null 2>&1 || prereq_fail \
    "shellcheck not on PATH. Install it before releasing:
             mise use -g shellcheck   (this repo already resolves shellcheck via mise)
             — or —  apt-get install shellcheck  /  brew install shellcheck
             Re-run with --ps-only ONLY if you have separately verified the shell scripts."
  log "shellcheck $(shellcheck --version | awk '/^version:/ { print $2 }')"

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

# ---------------------------------------------------------------------------
# PSScriptAnalyzer
# ---------------------------------------------------------------------------
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

echo
if [[ "${FINDINGS}" -ne 0 ]]; then
  log "RESULT: findings reported (see above)."
  exit 1
fi
log "RESULT: clean."
