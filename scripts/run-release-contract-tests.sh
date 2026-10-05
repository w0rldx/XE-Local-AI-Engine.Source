#!/usr/bin/env bash
# Auto-enroll every release/compliance contract test and reject vacuous green runs.
set -uo pipefail

# The checkout this runner lives in, from its own location: the tests beside it are the ones to run, never
# whatever a caller's GIT_DIR/GIT_WORK_TREE or working directory names. The repository-selection variables
# are dropped for every test it starts (scripts/lib/git-env.sh).
repo_root="$(cd "$(dirname "$(realpath "${BASH_SOURCE[0]}")")/.." && pwd)"
# shellcheck source=scripts/lib/git-env.sh
source "$repo_root/scripts/lib/git-env.sh" \
  || { echo "ERROR: cannot load $repo_root/scripts/lib/git-env.sh — copy scripts/lib/ along with this runner." >&2; exit 2; }
xe_drop_git_repo_env
# And from inside it: several tests find their subject from the working directory (`git rev-parse`,
# relative paths), so a runner started from another checkout would otherwise run these test files
# against THAT checkout's scripts. Nothing below reads the caller's directory.
cd "$repo_root" || { echo "ERROR: cannot enter $repo_root." >&2; exit 2; }
# scripts/performance/tests uses the unittest-discovery naming (test_*.py) rather than this
# runner's *.test.py convention, so both patterns are discovered below. Its third file,
# capture_windows_vram.Tests.ps1, is Pester and runs from scripts/lint-release-scripts.sh instead.
test_roots=(
  "$repo_root/scripts/tests"
  "$repo_root/scripts/compliance/tests"
  "$repo_root/scripts/performance/tests"
)

command -v python3 >/dev/null 2>&1 || {
  echo "ERROR: python3 is required for release contract tests." >&2
  exit 2
}

tests=()
while IFS= read -r test_file; do
  tests+=("$test_file")
done < <(find "${test_roots[@]}" -maxdepth 1 -type f \
  \( -name '*.test.sh' -o -name '*.test.py' -o -name 'test_*.py' \) -print | LC_ALL=C sort)

if [[ "${#tests[@]}" -eq 0 ]]; then
  echo "ERROR: release contract test discovery found zero tests." >&2
  exit 1
fi

passed=0
for test_file in "${tests[@]}"; do
  relative="${test_file#"$repo_root/"}"
  echo "[release-contract] $relative"
  if [[ "$test_file" == *.test.sh ]]; then
    output="$("$test_file" 2>&1)"
    test_status=$?
    printf '%s\n' "$output"
    expected="$(basename "$test_file"): PASS"
    if [[ "$test_status" -ne 0 ]] || ! grep -Fxq "$expected" <<<"$output"; then
      echo "ERROR: $relative exited $test_status or omitted '$expected'." >&2
      exit 1
    fi
  else
    output="$(python3 "$test_file" 2>&1)"
    test_status=$?
    printf '%s\n' "$output"
    if [[ "$test_status" -ne 0 ]] \
        || ! grep -Eq '^Ran [1-9][0-9]* tests? ' <<<"$output" \
        || ! grep -Fxq 'OK' <<<"$output"; then
      echo "ERROR: $relative failed or reported a vacuous Python test run." >&2
      exit 1
    fi
  fi
  ((passed += 1))
done

echo "[release-contract] $passed/${#tests[@]} test files passed"
echo "run-release-contract-tests.sh: PASS"
