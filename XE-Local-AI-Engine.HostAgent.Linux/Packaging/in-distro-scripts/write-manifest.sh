#!/usr/bin/env bash
# write-manifest.sh — in-distro static script (Phase: manifest-delivery).
#
# Receives the runtime manifest YAML on stdin (after the hashed script body)
# and writes it to the xe-engine user's host-agent config directory.
#
# Transport contract (§7.5a / §6.3):
#   - This script is fed via `wsl … bash -s` (BootstrapAsync seam), running as
#     root (RootUser = "root" in WindowsInstallerDriver).
#   - The manifest YAML content is supplied on stdin AFTER the script body,
#     read here via a heredoc sentinel (read until the sentinel line).
#   - The manifest content MUST NOT appear in this script body — that would
#     change the SHA-256 per machine and break VerifyScriptHash.
#   - Caller: WindowsInstallerDriver.DeliverManifestToDistroAsync, which calls
#     Wsl2Driver.BootstrapAsync(scriptText, expectedSha256, manifestYaml, ct).
#
# IMPORTANT: keep this file byte-for-byte identical across machines.
# The SHA-256 of the UTF-8 content of this file is recorded in
# bundle-metadata.json (writeManifestScriptSha256) and verified by
# Wsl2Driver.BootstrapAsync before execution.
#
# Exit codes: 0 = success; non-zero = failure (caller aborts install).

set -euo pipefail

XE_USER="xe-engine"
XE_CONFIG_DIR="/home/${XE_USER}/.config/xe-host-agent"
MANIFEST_PATH="${XE_CONFIG_DIR}/manifest.yaml"

# Read the manifest YAML from stdin until the EOF sentinel.
# The BootstrapAsync seam pipes: <script-body-newline><manifest-yaml-newline>
# bash -s consumes the script body first; the remaining stdin is the manifest.
MANIFEST_YAML=$(cat)

if [[ -z "${MANIFEST_YAML}" ]]; then
    echo "write-manifest: ERROR: empty manifest YAML received on stdin" >&2
    exit 1
fi

# Verify the xe-engine user exists before writing to their home directory.
if ! id "${XE_USER}" >/dev/null 2>&1; then
    echo "write-manifest: ERROR: user '${XE_USER}' does not exist in the distro" >&2
    exit 1
fi

# Create the config directory if it does not exist.
mkdir -p "${XE_CONFIG_DIR}"

# Write the manifest YAML to the target path (overwrite if already present —
# reset and re-install both legitimately call this step).
printf '%s\n' "${MANIFEST_YAML}" > "${MANIFEST_PATH}"

# Transfer ownership to the xe-engine user so HostAgent (running as xe-engine)
# can read the manifest without requiring elevated access at runtime.
chown -R "${XE_USER}:${XE_USER}" "${XE_CONFIG_DIR}"

# Sanity check: confirm the file was written and is non-empty.
if [[ ! -s "${MANIFEST_PATH}" ]]; then
    echo "write-manifest: ERROR: manifest file is empty or missing after write: ${MANIFEST_PATH}" >&2
    exit 1
fi

echo "write-manifest: manifest written to ${MANIFEST_PATH} ($(wc -c < "${MANIFEST_PATH}") bytes)"
