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

echo "project-validate-contract.test.sh: PASS"
