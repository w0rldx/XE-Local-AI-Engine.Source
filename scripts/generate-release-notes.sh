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
#   scripts/generate-release-notes.sh [--since <ref>] <version> [output-file]
#
#   --since <ref>  Explicit range: commits after <ref> up to HEAD, labelled as
#                  <version>. Used by the Development build, whose base is the
#                  previous dev/ tag's commit (or the newest v* tag on the
#                  first build).
#   <version>      Pack version WITHOUT a leading 'v' (e.g. 0.1.0-rc.1.2).
#                  The script adds the canonical 'v' prefix for the changelog
#                  heading. Defaults to eng/ReleaseVersion.props if omitted.
#   [output-file]  Defaults to RELEASE_NOTES.md in the repo root.
#
# Behaviour:
#   - With --since -> notes for <ref>..HEAD, labelled as <version>.
#   - If HEAD is already tagged with a v* tag (CI tag-push trigger) -> notes for
#     that tag. The --match 'v*' filter is load-bearing: a dev/ tag on HEAD must
#     never steer an official release's notes.
#   - Otherwise (manual pack, no tag yet) -> notes for commits since the last
#     tag, labelled as the pending <version>.
#
# Requires: git-cliff on PATH (https://git-cliff.org). cliff.toml at repo root.
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

SINCE=""
POSITIONAL=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --since)
      SINCE="${2:-}"
      if [[ -z "$SINCE" ]]; then
        echo "ERROR: --since needs a git ref." >&2
        exit 1
      fi
      shift 2
      ;;
    *)
      POSITIONAL+=("$1")
      shift
      ;;
  esac
done

VERSION="${POSITIONAL[0]:-}"
OUTPUT="${POSITIONAL[1]:-RELEASE_NOTES.md}"

# Fall back to the version composed from the canonical release manifest.
if [[ -z "$VERSION" ]]; then
  VERSION="$(scripts/read-release-version.py)"
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

if [[ -n "$SINCE" ]]; then
  # Explicit range (Development builds): commits after <ref> up to HEAD, labelled as this version.
  git-cliff "${SINCE}..HEAD" --tag "$TAG" --strip header -o "$OUTPUT"
elif git describe --exact-match --match 'v*' --tags HEAD >/dev/null 2>&1; then
  # HEAD is tagged with a release tag (CI): emit notes for the most recent tag.
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
