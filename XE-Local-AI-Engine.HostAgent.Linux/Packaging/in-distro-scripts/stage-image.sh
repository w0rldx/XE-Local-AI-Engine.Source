#!/usr/bin/env bash
# stage-image.sh — in-distro static script (Phase: image-load / step 1 of 2).
#
# Copies the XE node-web-server image tar from the Windows host (visible at a
# per-machine /mnt/<drive> path) to the fixed in-distro staging path
# /tmp/xe-image.tar.gz so the load step (load-image.sh) can reference a
# constant path whose hash is machine-independent.
#
# Transport contract (§7.5a / §6.3):
#   - This script is fed via `wsl … bash -s` (BootstrapAsync seam).
#   - The ONLY variable input is the Windows-host source path, supplied on
#     stdin as a single line AFTER the script body (the `bash -s` seam pipes
#     the script on stdin; the script then reads one more line from stdin for
#     the path).  The path MUST NOT appear in this script body — that would
#     change the SHA-256 per machine and break VerifyScriptHash.
#   - Caller: XE-Local-AI-Engine.Installer WindowsInstallerDriver.StageImageAsync,
#     which calls Wsl2Driver.BootstrapAsync(scriptText, expectedSha256) and
#     appends the path token as a second stdin line.
#
# IMPORTANT: keep this file byte-for-byte identical across machines.
# The SHA-256 of the UTF-8 content of this file is recorded in
# bundle-metadata.json (STAGE_IMAGE_SCRIPT_SHA256) and verified by
# Wsl2Driver.BootstrapAsync before execution.
#
# Exit codes: 0 = success; non-zero = failure (caller aborts install).

set -euo pipefail

STAGING_PATH="/tmp/xe-image.tar.gz"

# Read the Windows-host source path from stdin (second line after script body).
# The caller writes: <script-body-newline><windows-path-as-/mnt/…-newline>
read -r SRC_PATH

if [[ -z "${SRC_PATH}" ]]; then
    echo "stage-image: ERROR: empty source path received on stdin" >&2
    exit 1
fi

# Validate the path looks like a /mnt/ WSL mount path (defensive; not a
# security boundary — the VerifyScriptHash on the script body is the boundary).
if [[ "${SRC_PATH}" != /mnt/* ]]; then
    echo "stage-image: ERROR: source path does not start with /mnt/ — got: ${SRC_PATH}" >&2
    exit 1
fi

# Reject paths containing '..' segments to prevent traversal out of the /mnt/ subtree.
# Match any path component that is exactly '..' (surrounded by slashes, at start, or at end).
if echo "${SRC_PATH}" | grep -qE '(^|/)\.\.(/|$)'; then
    echo "stage-image: ERROR: source path contains '..' segment — got: ${SRC_PATH}" >&2
    exit 1
fi

if [[ ! -f "${SRC_PATH}" ]]; then
    echo "stage-image: ERROR: source file not found: ${SRC_PATH}" >&2
    exit 1
fi

echo "stage-image: copying image tar to ${STAGING_PATH} ..."
cp -- "${SRC_PATH}" "${STAGING_PATH}"

echo "stage-image: done ($(du -sh "${STAGING_PATH}" | cut -f1))"
