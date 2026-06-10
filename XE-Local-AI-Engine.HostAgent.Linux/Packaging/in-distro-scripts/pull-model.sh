#!/usr/bin/env bash
# pull-model.sh — in-distro static script (Phase: model-pull / verify-wait).
#
# MECHANISM NOTE (important — do not revert to bare `ollama pull`):
#   In the managed setup, Ollama runs as the ollama/ollama:0.30.5 CONTAINER
#   inside the distro (Plans/artifacts/sample-manifests/managed.yaml).  The
#   `ollama` binary does NOT exist in the distro rootfs.
#
#   The HostAgent.Linux BootstrapModelReadinessHostedService
#   (Models/BootstrapModelReadinessHostedService.cs + BootstrapModelReadinessService.cs)
#   automatically pulls the bootstrap model via OllamaSharp HTTP to
#   http://127.0.0.1:11434 (Program.cs:134) on every HostAgent start, retrying
#   every 5 s until the model is present.  The installer's model-pull phase
#   therefore delegates the actual pull to the HostAgent bootstrap and simply
#   waits for the model to appear in GET /api/tags.
#
# This script:
#   1. Confirms the Ollama API is reachable at http://127.0.0.1:11434.
#   2. Polls GET /api/tags with a bounded timeout until the bootstrap model
#      name appears — i.e. waits for the HostAgent bootstrap pull to complete.
#   3. Fails with a clear message if the timeout expires.
#
# Transport contract (§7.5a):
#   - This script is fed via `wsl … bash -s` (RuntimeInstallAsync seam,
#     xe-engine user).
#   - The bootstrap model name is baked at bundle build time (@@XE_BOOTSTRAP_MODEL@@).
#     No per-machine input is needed; script body is fully static and hash-stable.
#   - Caller: XE-Local-AI-Engine.Installer WindowsInstallerDriver.PullModelAsync,
#     which calls Wsl2Driver.RuntimeInstallAsync(scriptText, expectedSha256).
#
# The SHA-256 of the UTF-8 content of this file (after token substitution by
# build-rc-zip.ps1) is recorded in bundle-metadata.json
# (PULL_MODEL_SCRIPT_SHA256) and verified by Wsl2Driver.RuntimeInstallAsync
# before execution.
#
# Token substitution: build-rc-zip.ps1 replaces the @@-delimited token:
#   @@XE_BOOTSTRAP_MODEL@@  →  model name (e.g. qwen3:0.6b)
#
# Idempotency: polling /api/tags is read-only; if the model is already present
# the script exits immediately on the first poll.
#
# Prerequisites: the Ollama container must be running and the HostAgent must
# have been started (ColdStartAsync) before this script is invoked.  The
# installer's model-pull phase is sequenced after host-agent-install.
#
# Exit codes: 0 = model present and verified; non-zero = failure (caller aborts).

set -euo pipefail

BOOTSTRAP_MODEL="@@XE_BOOTSTRAP_MODEL@@"
OLLAMA_URL="http://127.0.0.1:11434"
# Maximum seconds to wait for the model to become available.
# qwen3:0.6b is ~400 MB; on typical broadband 10 minutes is generous.
WAIT_TIMEOUT_SECONDS=600
POLL_INTERVAL_SECONDS=10

# The Ollama API name field may include or omit the tag depending on version.
# Match on the full "repo:tag" string first, then fall back to the base name.
MODEL_BASE="${BOOTSTRAP_MODEL%%:*}"

echo "pull-model: waiting for bootstrap model '${BOOTSTRAP_MODEL}' via Ollama API at ${OLLAMA_URL} ..."
echo "pull-model: BootstrapModelReadinessHostedService pulls it automatically on HostAgent start (5 s retry loop)."
echo "pull-model: this script waits up to ${WAIT_TIMEOUT_SECONDS}s for that pull to complete."

if ! command -v curl >/dev/null 2>&1; then
    echo "pull-model: ERROR: curl not found in distro PATH — cannot poll Ollama API" >&2
    exit 1
fi

elapsed=0
while true; do
    # GET /api/tags returns {"models":[{"name":"qwen3:0.6b",...},...]}
    # curl --fail exits non-zero on HTTP errors; || true lets us handle the empty case below.
    tags_json="$(curl --silent --fail --max-time 5 "${OLLAMA_URL}/api/tags" 2>/dev/null || true)"

    if [[ -n "${tags_json}" ]]; then
        if echo "${tags_json}" | grep -qF "\"${BOOTSTRAP_MODEL}\"" || \
           echo "${tags_json}" | grep -qF "\"${MODEL_BASE}\""; then
            echo "pull-model: model '${BOOTSTRAP_MODEL}' is present (elapsed: ${elapsed}s)"
            echo "pull-model: done"
            exit 0
        fi
    fi

    if (( elapsed >= WAIT_TIMEOUT_SECONDS )); then
        echo "pull-model: ERROR: timed out after ${WAIT_TIMEOUT_SECONDS}s waiting for '${BOOTSTRAP_MODEL}'" >&2
        echo "pull-model: last /api/tags response: ${tags_json:-(empty or unreachable)}" >&2
        echo "pull-model: check that the Ollama container is running and the HostAgent has started" >&2
        exit 1
    fi

    echo "pull-model: model not yet available (elapsed: ${elapsed}s) — polling again in ${POLL_INTERVAL_SECONDS}s ..."
    sleep "${POLL_INTERVAL_SECONDS}"
    (( elapsed += POLL_INTERVAL_SECONDS ))
done
