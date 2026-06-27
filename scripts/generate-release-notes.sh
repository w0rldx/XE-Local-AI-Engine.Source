#!/usr/bin/env bash
# Generate GitHub release notes (markdown) from conventional commits since the
# previous release tag, for feeding into `vpk pack --releaseNotes`.
#
# WHY: Velopack embeds the notes file at pack time and publishes it as the
# GitHub release body (vpk has no post-upload notes flag). This produces that
# file deterministically from git history so every release — including
# pre-releases — ships an auto-grouped changelog.
#
# Usage:
#   scripts/generate-release-notes.sh <version> [output-file]
#
#   <version>      Pack version WITHOUT a leading 'v' (e.g. 0.1.0-rc.1.2).
#                  The script adds the canonical 'v' prefix for the changelog
#                  heading. Defaults to Directory.Build.props if omitted.
#   [output-file]  Defaults to RELEASE_NOTES.md in the repo root.
#
# Behaviour:
#   - If HEAD is already tagged (CI tag-push trigger) -> notes for that tag.
#   - Otherwise (manual pack, no tag yet) -> notes for commits since the last
#     tag, labelled as the pending <version>.
#
# Requires: git-cliff on PATH (https://git-cliff.org). cliff.toml at repo root.
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

VERSION="${1:-}"
OUTPUT="${2:-RELEASE_NOTES.md}"

# Fall back to the version composed from Directory.Build.props.
if [[ -z "$VERSION" ]]; then
  PREFIX=$(grep -oP '(?<=<VersionPrefix>)[^<]+' Directory.Build.props)
  SUFFIX=$(grep -oP '(?<=<VersionSuffix>)[^<]+' Directory.Build.props || true)
  if [[ -n "${SUFFIX:-}" ]]; then
    VERSION="${PREFIX}-${SUFFIX}"
  else
    VERSION="${PREFIX}"
  fi
fi

# Normalise: strip any leading 'v' the caller may have passed, then re-add it
# for the canonical tag label.
VERSION="${VERSION#v}"
TAG="v${VERSION}"

if ! command -v git-cliff >/dev/null 2>&1; then
  echo "ERROR: git-cliff not found on PATH. Install from https://git-cliff.org" >&2
  exit 1
fi

echo "Generating release notes for ${TAG} -> ${OUTPUT}"

if git describe --exact-match --tags HEAD >/dev/null 2>&1; then
  # HEAD is tagged (CI): emit notes for the most recent tag.
  git-cliff --latest --strip header -o "$OUTPUT"
else
  # Pending build (manual): commits since last tag, labelled as this version.
  git-cliff --unreleased --tag "$TAG" --strip header -o "$OUTPUT"
fi

# git-cliff exits 0 with an empty body when no qualifying commits exist; make
# the release body non-empty so the GitHub release never looks broken.
if [[ ! -s "$OUTPUT" ]] || ! grep -q '[^[:space:]]' "$OUTPUT"; then
  printf '## %s\n\nMaintenance release — no user-facing changelog entries.\n' "$VERSION" > "$OUTPUT"
fi

echo "----- $OUTPUT -----"
cat "$OUTPUT"
