#!/usr/bin/env bash
# load-image.sh — in-distro static script (Phase: image-load / step 2 of 2).
#
# Loads the XE node-web-server image tar from the fixed staging path
# /tmp/xe-image.tar.gz, retags the loaded image so RepositoryWithTag resolves
# for InspectImageAsync, then verifies the loaded config Id matches the
# build-time-recorded expected value.  Cleans up the staging file on exit.
#
# Transport contract (§7.5a / §6.3):
#   - This script is fed via `wsl … bash -s` (BootstrapAsync seam).
#   - No per-machine input is needed — all values are baked at bundle build
#     time (XE_EXPECTED_IMAGE_ID, XE_REPO_TAG).  The script body is therefore
#     fully static and its SHA-256 is machine-independent.
#   - Caller: XE-Local-AI-Engine.Installer WindowsInstallerDriver.LoadImageAsync,
#     which calls Wsl2Driver.BootstrapAsync(scriptText, expectedSha256).
#
# The SHA-256 of the UTF-8 content of this file (after token substitution by
# build-rc-zip.ps1) is recorded in bundle-metadata.json
# (LOAD_IMAGE_SCRIPT_SHA256) and verified by Wsl2Driver.BootstrapAsync before
# execution.
#
# Token substitution: build-rc-zip.ps1 replaces the two @@-delimited tokens
# before computing the SHA and writing the final script into the bundle:
#   @@XE_EXPECTED_IMAGE_ID@@  →  sha256:<hex> config Id from `docker inspect`
#   @@XE_REPO_TAG@@           →  <repository>:<tag>  (e.g. ghcr.io/c0re/xe-local-ai-engine:0.1.0)
#
# Exit codes: 0 = success; non-zero = failure (caller aborts install).

set -euo pipefail

STAGING_PATH="/tmp/xe-image.tar.gz"
EXPECTED_ID="@@XE_EXPECTED_IMAGE_ID@@"
REPO_TAG="@@XE_REPO_TAG@@"

cleanup() {
    if [[ -f "${STAGING_PATH}" ]]; then
        echo "load-image: cleaning up staging file ${STAGING_PATH}"
        rm -f -- "${STAGING_PATH}"
    fi
}
trap cleanup EXIT

if [[ ! -f "${STAGING_PATH}" ]]; then
    echo "load-image: ERROR: staging file not found: ${STAGING_PATH}" >&2
    echo "load-image: run stage-image.sh first" >&2
    exit 1
fi

echo "load-image: loading image from ${STAGING_PATH} ..."
docker load -i "${STAGING_PATH}"

echo "load-image: retagging loaded image as ${REPO_TAG} ..."
# Tag by the baked config Id so we can reference it by canonical digest.
docker tag "${EXPECTED_ID}" "${REPO_TAG}"

echo "load-image: verifying loaded config Id ..."
ACTUAL_ID="$(docker inspect --format '{{.Id}}' "${REPO_TAG}")"

if [[ "${ACTUAL_ID}" != "${EXPECTED_ID}" ]]; then
    echo "load-image: ERROR: config Id mismatch" >&2
    echo "load-image:   expected: ${EXPECTED_ID}" >&2
    echo "load-image:   actual:   ${ACTUAL_ID}" >&2
    exit 1
fi

echo "load-image: verified — config Id matches bundle-recorded value"
echo "load-image: done"
