#!/usr/bin/env bash
# Auto-enroll every release/compliance contract test and reject vacuous green runs.
set -uo pipefail

repo_root="$(git rev-parse --show-toplevel 2>/dev/null || (cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd))"
test_roots=("$repo_root/scripts/tests" "$repo_root/scripts/compliance/tests")

command -v python3 >/dev/null 2>&1 || {
  echo "ERROR: python3 is required for release contract tests." >&2
  exit 2
}

tests=()
while IFS= read -r test_file; do
  tests+=("$test_file")
done < <(find "${test_roots[@]}" -maxdepth 1 -type f \( -name '*.test.sh' -o -name '*.test.py' \) -print | LC_ALL=C sort)

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
