#!/usr/bin/env bash

set -euo pipefail

PROJECT_ROOT="$(git rev-parse --show-toplevel)"
VALIDATOR="${PROJECT_ROOT}/scripts/release/verify-release-authority.py"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "${TMP_DIR}"' EXIT

mkdir -p "${TMP_DIR}/docs/compliance"
printf 'public-safe evidence\n' > "${TMP_DIR}/docs/compliance/evidence.md"

categories=(
  project-rights-holder-apache-authority
  author-identities-aliases
  employer-contractor-predecessor-c0re-permissions
  copied-adapted-materials
  logos-media-branding
  vendored-agency-agents
  third-party-redistribution-terms
  canonical-tag-binary-authority
  signing-risk-decision
)

write_valid_fixture() {
  local output="$1"
  {
    printf '{\n'
    printf '  "schema_version": 1,\n'
    printf '  "legal_notice": "Structural check only; not legal advice or certification.",\n'
    printf '  "owner_confirmation_input": {"status": "superseded_by_register"},\n'
    printf '  "decisions": [\n'
    local separator=""
    local category
    for category in "${categories[@]}"; do
      subjects=""
      if [[ "${category}" == "author-identities-aliases" ]]; then
        subjects=',"subjects":["Public Alias One"]'
      fi
      printf '%s    {"id":"%s"%s,"status":"approved","approver":{"name":"Public Approver","authority_basis":"Rights holder"},"decision_date":"2026-01-01","expires_on":"2027-01-01","evidence":[{"reference":"approval memo","repository_path":"docs/compliance/evidence.md"}]}' "${separator}" "${category}" "${subjects}"
      separator=$',\n'
    done
    printf '\n  ]\n}\n'
  } > "${output}"
}

assert_fails_with() {
  local fixture="$1"
  local expected="$2"
  local output
  if output="$(python3 "${VALIDATOR}" --repository-root "${TMP_DIR}" --today 2026-08-07 "${fixture}" 2>&1)"; then
    echo "expected validator failure containing: ${expected}" >&2
    exit 1
  fi
  [[ "${output}" == *"${expected}"* ]] || {
    printf 'expected %q in output:\n%s\n' "${expected}" "${output}" >&2
    exit 1
  }
}

valid="${TMP_DIR}/valid.json"
write_valid_fixture "${valid}"
python3 "${VALIDATOR}" --repository-root "${TMP_DIR}" --today 2026-08-07 "${valid}" \
  | grep -Fq 'release-authority: PASS'

blank="${TMP_DIR}/blank.json"
jq '(.decisions[0].approver.name) = ""' "${valid}" > "${blank}"
assert_fails_with "${blank}" 'approver.name must be non-blank'

missing="${TMP_DIR}/missing.json"
jq 'del(.decisions[0])' "${valid}" > "${missing}"
assert_fails_with "${missing}" 'missing required categories'

unresolved="${TMP_DIR}/unresolved.json"
jq '(.decisions[0].status) = "unresolved"' "${valid}" > "${unresolved}"
assert_fails_with "${unresolved}" 'status must be approved'

stale="${TMP_DIR}/stale.json"
jq '(.decisions[0].expires_on) = "2026-08-06"' "${valid}" > "${stale}"
assert_fails_with "${stale}" 'approval is stale'

missing_path="${TMP_DIR}/missing-path.json"
jq '(.decisions[0].evidence[0].repository_path) = "docs/compliance/removed.md"' "${valid}" > "${missing_path}"
assert_fails_with "${missing_path}" 'evidence repository_path is missing or escapes the repository'

# A single public identity is the expected shape for a sole author.
one_author="${TMP_DIR}/one-author.json"
jq '(.decisions[] | select(.id == "author-identities-aliases") | .subjects) = ["Only one alias"]' "${valid}" > "${one_author}"
python3 "${VALIDATOR}" --repository-root "${TMP_DIR}" --today 2026-08-07 "${one_author}" \
  | grep -Fq 'release-authority: PASS'

no_author="${TMP_DIR}/no-author.json"
jq '(.decisions[] | select(.id == "author-identities-aliases") | .subjects) = []' "${valid}" > "${no_author}"
assert_fails_with "${no_author}" 'subjects must contain at least one non-blank public identity or alias'

blank_author="${TMP_DIR}/blank-author.json"
jq '(.decisions[] | select(.id == "author-identities-aliases") | .subjects) = [""]' "${valid}" > "${blank_author}"
assert_fails_with "${blank_author}" 'subjects must contain at least one non-blank public identity or alias'

echo "release-authority.test.sh: PASS"
