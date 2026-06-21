#!/usr/bin/env bash
# package-rc.sh — build the distributable RC bundle(s) an external tester downloads.
#
# Produces, per target RID, a self-contained zip whose flat root holds:
#   - the single-file self-contained binary (dotnet publish output) + wwwroot SPA assets
#   - a prominently named launcher (Start-XE-Local-AI-Engine.cmd / start-xe-local-ai-engine.sh)
#     that sets XE_LAUNCH_MODE=desktop (the bare binary does NOT enter desktop mode)
#   - READ-ME-FIRST.txt — the one-screen tester quickstart
# plus a .sha256 sidecar for each zip.
#
# Cross-compiles both RIDs from Linux. The Windows bundle is built here but must be
# smoke-tested on a real Windows machine before tagging (see TESTER-QUICKSTART.md / the
# thin-RC plan's operator checklist).
#
# Usage:
#   publish/package-rc.sh                 # both win-x64 and linux-x64
#   publish/package-rc.sh --rid win-x64   # one RID
#   publish/package-rc.sh --skip-web      # reuse the existing React dist (skip pnpm build)
#
# Output: publish/dist/xe-local-ai-engine-<version>-<rid>.zip (+ .sha256)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
CLIENT_PROJECT="${REPO_ROOT}/XE-Local-AI-Engine.Client"
REACT_DIR="${REPO_ROOT}/XE-Local-AI-Engine.Client.React"
DIST_DIR="${SCRIPT_DIR}/dist"

RIDS=("win-x64" "linux-x64")
SKIP_WEB=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid)
      [[ $# -ge 2 ]] || { echo "Error: --rid needs a value (win-x64 | linux-x64)." >&2; exit 2; }
      RIDS=("$2"); shift 2 ;;
    --skip-web) SKIP_WEB=1; shift ;;
    -h|--help) sed -n '2,22p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "Error: unknown argument '$1'." >&2; exit 2 ;;
  esac
done

# Version is the single source of truth in Directory.Build.props (VersionPrefix[-VersionSuffix]).
read_version() {
  local props="${REPO_ROOT}/Directory.Build.props" prefix suffix
  prefix="$(grep -oP '(?<=<VersionPrefix>)[^<]+' "${props}" | head -1)"
  suffix="$(grep -oP '(?<=<VersionSuffix>)[^<]+' "${props}" | head -1)"
  [[ -n "${prefix}" ]] || { echo "Error: no <VersionPrefix> in Directory.Build.props." >&2; exit 1; }
  if [[ -n "${suffix}" ]]; then echo "${prefix}-${suffix}"; else echo "${prefix}"; fi
}

build_web() {
  if [[ "${SKIP_WEB}" -eq 1 ]]; then
    [[ -f "${REACT_DIR}/dist/index.html" ]] || { echo "Error: --skip-web but no React dist found; run a build first." >&2; exit 1; }
    echo ">> Reusing existing React dist (--skip-web)."
    return
  fi
  echo ">> Building React SPA (pnpm build)…"
  ( cd "${REACT_DIR}" && pnpm install --frozen-lockfile && pnpm run build )
  [[ -f "${REACT_DIR}/dist/index.html" ]] || { echo "Error: React build produced no dist/index.html." >&2; exit 1; }
}

# Emits the per-OS quickstart shipped inside the zip.
write_readme() {
  local os="$1" out="$2"
  if [[ "${os}" == "windows" ]]; then
    cat >"${out}" <<'TXT'
XE Local AI Engine — tester quickstart (Windows)
================================================

1. Unzip this folder anywhere (e.g. your Desktop).
2. Double-click  Start-XE-Local-AI-Engine.cmd
   (Do NOT double-click XE-Local-AI-Engine.Client.exe directly — it will not
    open the app correctly. Always use the Start launcher.)
3. A console window opens with live logs and your default browser opens the app.

First run downloads a llama.cpp runtime and a ~400 MB starter model from the
internet — this can take a few minutes and looks quiet; watch the console.
Needs ~2 GB free disk. A GPU is optional (CPU works).

To create your login: the first time the app opens, set an admin password.
To stop the app: close the console window (this also stops the model engine).
Your data lives under: %LOCALAPPDATA%\XE-Local-AI-Engine

Run only ONE instance at a time.

Found a problem? Note what you did, the console log lines, and any browser
error, and send them back.
TXT
  else
    cat >"${out}" <<'TXT'
XE Local AI Engine — tester quickstart (Linux)
==============================================

1. Unzip this folder anywhere.
2. From a terminal in this folder, run:  ./start-xe-local-ai-engine.sh
   (Do NOT run ./XE-Local-AI-Engine.Client directly — it will not enter desktop
    mode. Always use the start launcher.)
3. The terminal shows live logs and your default browser opens the app.

First run downloads a llama.cpp runtime and a ~400 MB starter model from the
internet — this can take a few minutes and looks quiet; watch the terminal.
Needs ~2 GB free disk. A GPU is optional (CPU works).

To create your login: the first time the app opens, set an admin password.
To stop the app: close the terminal (this also stops the model engine).
Your data lives under: $HOME/.local/share/XE-Local-AI-Engine

Run only ONE instance at a time.

Found a problem? Note what you did, the terminal log lines, and any browser
error, and send them back.
TXT
  fi
}

# Fail the build if any per-node runtime/state artifact leaked into the staged bundle. CL-1 relocated all of these out
# of ContentRoot into the per-user data dir, so a clean publish never produces them; this guard codifies that and stops
# a regression (e.g. a re-committed dev node-settings.json, or a *.enc credential file) from ever shipping.
#
# Intended publish allowlist (everything else under the stage is one of these, by design):
#   - appsettings*.json            (app configuration; carries no secrets — validated in the CL-2 audit)
#   - *.runtimeconfig.json         (the .NET runtime host config)
#   - *.deps.json                  (the .NET dependency manifest)
#   - manifest.json                (AgentHome layout manifests are runtime-only; the app's own manifest.json is an asset)
#   - wwwroot/**                   (the React SPA build, incl. hashed node-settings-*.js chunks — NOT node-settings.json)
#   - the single-file binary + launcher + READ-ME-FIRST.txt
assert_no_runtime_state() {
  local stage="$1" leak
  # node-settings.json (dev/runtime state), any encrypted credential (*.enc), any cert pin (*.pin). The React build
  # emits node-settings-<hash>.js chunks — those are assets and must NOT match, so the name pattern is exact.
  leak="$(cd "${stage}" && find . \( -name 'node-settings.json' -o -name '*.enc' -o -name '*.pin' \) -print)"
  if [[ -n "${leak}" ]]; then
    echo "Error: runtime/state files leaked into the published bundle (must never ship):" >&2
    echo "${leak}" >&2
    echo "These belong under the per-user data dir at runtime (CL-1). Check the csproj Content items / a stray committed file." >&2
    exit 1
  fi
}

make_zip() {
  local stage_parent="$1" base="$2" zip_path="$3"
  rm -f "${zip_path}"
  if command -v zip >/dev/null 2>&1; then
    ( cd "${stage_parent}" && zip -rq "${zip_path}" "${base}" )
  else
    # Fallback when the `zip` CLI is absent.
    ( cd "${stage_parent}" && python3 -c 'import shutil,sys; shutil.make_archive(sys.argv[1][:-4], "zip", ".", sys.argv[2])' "${zip_path}" "${base}" )
  fi
}

package_rid() {
  local rid="$1" version="$2" exe os base stage pub zip_path
  case "${rid}" in
    win-x64)   os="windows"; exe="XE-Local-AI-Engine.Client.exe" ;;
    linux-x64) os="linux";   exe="XE-Local-AI-Engine.Client" ;;
    *) echo "Error: unsupported RID '${rid}' (expected win-x64 | linux-x64)." >&2; exit 2 ;;
  esac

  echo ">> Publishing ${rid} (single-file self-contained)…"
  dotnet publish "${CLIENT_PROJECT}" -c Release -r "${rid}" -p:PublishProfile="${rid}" --nologo

  pub="${CLIENT_PROJECT}/bin/Release/net10.0/${rid}/publish"
  [[ -f "${pub}/${exe}" ]] || { echo "Error: expected published binary not found at ${pub}/${exe}." >&2; exit 1; }

  base="xe-local-ai-engine-${version}-${rid}"
  stage="${DIST_DIR}/stage/${base}"
  rm -rf "${stage}"; mkdir -p "${stage}"
  cp -r "${pub}/." "${stage}/"

  if [[ "${os}" == "windows" ]]; then
    cp "${SCRIPT_DIR}/windows/run-xe-local-ai-engine.cmd" "${stage}/Start-XE-Local-AI-Engine.cmd"
  else
    cp "${SCRIPT_DIR}/linux/run-xe-local-ai-engine.sh" "${stage}/start-xe-local-ai-engine.sh"
    chmod +x "${stage}/start-xe-local-ai-engine.sh" "${stage}/${exe}"
  fi
  write_readme "${os}" "${stage}/READ-ME-FIRST.txt"

  # Refuse to ship a bundle carrying any per-node runtime/state artifact (node-settings.json / *.enc / *.pin).
  assert_no_runtime_state "${stage}"

  zip_path="${DIST_DIR}/${base}.zip"
  make_zip "${DIST_DIR}/stage" "${base}" "${zip_path}"
  ( cd "${DIST_DIR}" && sha256sum "${base}.zip" >"${base}.zip.sha256" )
  rm -rf "${stage}"
  echo ">> Built ${zip_path}"
  ( cd "${DIST_DIR}" && du -h "${base}.zip" | cut -f1 | xargs -I{} echo "   size {}  sha256 $(cut -d' ' -f1 "${base}.zip.sha256")" )
}

main() {
  command -v dotnet >/dev/null 2>&1 || { echo "Error: dotnet SDK not found on PATH." >&2; exit 1; }
  local version; version="$(read_version)"
  echo ">> Packaging XE Local AI Engine ${version} for: ${RIDS[*]}"
  mkdir -p "${DIST_DIR}"
  build_web
  for rid in "${RIDS[@]}"; do package_rid "${rid}" "${version}"; done
  rmdir "${DIST_DIR}/stage" 2>/dev/null || true
  echo ">> Done. Artifacts in ${DIST_DIR}"
}

main "$@"
