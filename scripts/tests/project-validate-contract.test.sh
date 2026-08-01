#!/usr/bin/env bash
# shellcheck disable=SC2016

set -euo pipefail

# The assertions deliberately match literal shell source containing ${...}.

PROJECT_ROOT="$(git rev-parse --show-toplevel)"
VALIDATOR="${PROJECT_ROOT}/.opencode/scripts/project-validate.sh"

smoke_body="$(sed -n '/^_backend_smoke_body()/,/^}/p' "${VALIDATOR}")"
[[ "${smoke_body}" == *'dotnet restore "${project}"'* ]]
[[ "${smoke_body}" == *'dotnet build "${project}" --configuration Debug --no-restore'* ]]
[[ "${smoke_body}" == *'"${ASSEMBLY_GUARD}" guard --test-bins'* ]]
[[ "${smoke_body}" == *'--no-build'* ]]
[[ "${smoke_body}" == *'ApplicationStartupTests|FrameworkDevUiHostingSmokeTests'* ]]
[[ "${smoke_body}" == *'_assert_tests_ran "${out}" "backend-smoke"'* ]]

build_line="$(grep -nF 'dotnet build "${project}" --configuration Debug --no-restore' "${VALIDATOR}" | cut -d: -f1)"
guard_line="$(grep -nF '"${ASSEMBLY_GUARD}" guard --test-bins' "${VALIDATOR}" | awk -F: '$1 > 230 {print $1; exit}')"
[[ "${build_line}" =~ ^[0-9]+$ && "${guard_line}" =~ ^[0-9]+$ && "${build_line}" -lt "${guard_line}" ]]

backend_body="$(sed -n '/^_backend_body()/,/^}/p' "${VALIDATOR}")"
[[ "${backend_body}" == *'--configuration Release --no-restore'* ]]
[[ "${backend_body}" == *'dotnet test "${SOLUTION_FILE}" --configuration Release --no-build'* ]]

coverage_body="$(sed -n '/^_backend_coverage_body()/,/^}/p' "${VALIDATOR}")"
[[ "${coverage_body}" == *'mktemp -d "${LOG_DIR}/coverage/backend-${TS}-XXXXXX"'* ]]
[[ "${coverage_body}" == *'for test_project in "${test_projects[@]}"'* ]]
[[ "${coverage_body}" == *'module_dir="${run_dir}/${module_name}"'* ]]
[[ "${coverage_body}" == *'_assert_tests_ran "${out}" "backend-coverage:${module_name}"'* ]]
[[ "${coverage_body}" == *'--coverage-output coverage.cobertura.xml'* ]]
[[ "${coverage_body}" == *'--coverage-output-format cobertura'* ]]
[[ "${coverage_body}" != *'--collect:'* ]]
[[ "${coverage_body}" == *'"${ASSEMBLY_GUARD}" snapshot "${guard_state}" --test-bins'* ]]
[[ "${coverage_body}" == *'"${ASSEMBLY_GUARD}" verify "${guard_state}"'* ]]
[[ "${coverage_body}" == *'find "${run_dir}" -type f -name '\''coverage.cobertura.xml'\'''* ]]
[[ "${coverage_body}" == *'[[ ${#reports[@]} -eq ${#test_projects[@]} ]]'* ]]
[[ "${coverage_body}" == *'scripts/merge-cobertura.py'* ]]
[[ "${coverage_body}" == *'scripts/backend-coverage-baseline.txt'* ]]

live_check="$(cat "${PROJECT_ROOT}/scripts/openapi-live-check.sh")"
[[ "${live_check}" == *'exec "${BUILD_LOCK}" -- "${BASH_SOURCE[0]}" "$@"'* ]]
snapshot_line="$(grep -nF '"${ASSEMBLY_GUARD}" snapshot "${guard_state}"' <<<"${live_check}" | cut -d: -f1)"
launch_line="$(grep -nF 'setsid dotnet run' <<<"${live_check}" | cut -d: -f1)"
verify_line="$(grep -nF '"${ASSEMBLY_GUARD}" verify "${guard_state}"' <<<"${live_check}" | cut -d: -f1)"
[[ "${snapshot_line}" =~ ^[0-9]+$ && "${launch_line}" =~ ^[0-9]+$ && "${verify_line}" =~ ^[0-9]+$ ]]
[[ "${snapshot_line}" -lt "${launch_line}" ]]
[[ "${verify_line}" -lt "${snapshot_line}" ]]
[[ "${live_check}" == *'status="${guard_status}"'* ]]

# The live contract check is only REACHABLE through scope_full. The assertions above pin the inside of
# openapi-live-check.sh but say nothing about anything calling it, so a refactor of scope_full could delete
# the call and retire the only backend-vs-committed-spec gate while every other lane stayed green.
# `pnpm openapi:check` cannot cover this: it compares the committed spec with the generated client and is
# blind to a backend that has moved past the spec.
full_body="$(sed -n '/^scope_full()/,/^}/p' "${VALIDATOR}")"
[[ "${full_body}" == *'_run_group "openapi-live" _tree_openapi_live'* ]]

live_tree_body="$(sed -n '/^_tree_openapi_live()/,/^}/p' "${VALIDATOR}")"
[[ "${live_tree_body}" == *'./scripts/openapi-live-check.sh'* ]]
# Deliberately NOT _locked: openapi-live-check.sh takes the repository-wide build lock itself and is
# re-entrant through XE_BUILD_LOCK_HELD, so wrapping it again here would be redundant.
[[ "${full_body}" != *'_locked _tree_openapi_live'* ]]

# It must run AFTER the backend/frontend pre-gate: the live check starts the host with `dotnet run --no-build`
# (needs the backend tree's Release build) and then shells into `pnpm openapi:check:live` (needs the frontend
# tree's node_modules). Reordering it earlier would fail for environmental reasons and read as drift.
pre_gate_line="$(grep -nF '_run_group "backend" _tree_backend "frontend" _tree_frontend "scripts" _tree_scripts' "${VALIDATOR}" | cut -d: -f1)"
live_group_line="$(grep -nF '_run_group "openapi-live" _tree_openapi_live' "${VALIDATOR}" | cut -d: -f1)"
[[ "${pre_gate_line}" =~ ^[0-9]+$ && "${live_group_line}" =~ ^[0-9]+$ ]]
[[ "${pre_gate_line}" -lt "${live_group_line}" ]]

# XE_LAUNCH_MODE=desktop is load-bearing, not incidental: without it the host omits every desktop-only
# endpoint, so the live spec would match a client generated from a smaller surface and the check would pass
# vacuously (docs/agent-knowledge.md — "OpenAPI regen silently drops desktop-only endpoints").
[[ "${live_check}" == *'XE_LAUNCH_MODE=desktop'* ]]

echo "project-validate-contract.test.sh: PASS"
