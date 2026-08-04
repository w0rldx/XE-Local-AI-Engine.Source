#!/usr/bin/env bash
# package-rc.sh — build the distributable RC bundle(s) an external tester downloads.
#
# Produces, per target RID, a self-contained zip whose flat root holds:
#   - the single-file self-contained binary (dotnet publish output) + wwwroot SPA assets
#   - a prominently named launcher (Start-XE-Local-AI-Engine.cmd / start-xe-local-ai-engine.sh)
#     that sets XE_LAUNCH_MODE=desktop (the bare binary does NOT enter desktop mode)
#   - an uninstaller (Uninstall-XE-Local-AI-Engine.ps1 / uninstall-xe-local-ai-engine.sh)
#     that stops the app + its runtime children and removes the per-user data dir
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
# Output (in publish/dist/, each with a .sha256 sidecar):
#   linux-x64  XE-Local-AI-Engine-<version>-linux-Portable.zip  — Velopack-style, matching the Windows asset
#   win-x64    xe-local-ai-engine-<version>-win-x64.zip         — NOT the shipped Velopack naming scheme
# Both unzip to a versioned folder: xe-local-ai-engine-<version>-<rid>/

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
CLIENT_PROJECT="${REPO_ROOT}/XE-Local-AI-Engine.Client"
REACT_DIR="${REPO_ROOT}/XE-Local-AI-Engine.Client.React"
DIST_DIR="${SCRIPT_DIR}/dist"

# The app-update build flavor baked into every bundle this script produces. XE-Local-AI-Engine.Client.csproj
# copies appsettings.AppUpdate.$(UpdateChannel).json to the output as appsettings.AppUpdate.json; passing the
# value explicitly (rather than inheriting the csproj default) means a future change to that default cannot
# silently retarget these zips.
#
# `main` is a deliberate choice, not a leftover. This script produces a plain portable zip, NOT a Velopack
# installation, and the tester feed (w0rldx/XE-Local-AI-Engine.Tester-App) only carries Velopack releases
# uploaded by publish/package-tester-win.ps1 — a portable zip must never try to self-update into a layout it
# was never installed as. The tester flavor additionally ships an EMPTY GitHubAppClientId that only that
# PowerShell script injects, so a `tester` bundle from here would be an inert artifact wearing a live
# channel's name. The main channel is intentionally unconfigured for now, which leaves
# AppUpdateChannelOptions.IsConfigured false and the in-app updater honestly disabled — assert_app_config_sane
# proves that rather than assuming it.
UPDATE_CHANNEL="main"

RIDS=("win-x64" "linux-x64")
SKIP_WEB=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid)
      [[ $# -ge 2 ]] || { echo "Error: --rid needs a value (win-x64 | linux-x64)." >&2; exit 2; }
      RIDS=("$2"); shift 2 ;;
    --skip-web) SKIP_WEB=1; shift ;;
    -h|--help) sed -n '2,25p' "${BASH_SOURCE[0]}"; exit 0 ;;
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
3. Windows SmartScreen may warn "Windows protected your PC" because this build
   is unsigned (unknown publisher). This is expected for a tester build: click
   "More info", then "Run anyway".
4. A console window opens with live logs and your default browser opens the app.

First run downloads a llama.cpp runtime and a ~400 MB starter model from the
internet — this can take a few minutes and looks quiet; watch the console.
Needs ~2 GB free disk. A GPU is optional (CPU works).

To create your login: the first time the app opens, set an admin password.
To stop the app: close the console window (this also stops the model engine).
Your data lives under: %LOCALAPPDATA%\XE-Local-AI-Engine

Run only ONE instance at a time.

To fully remove the app: close it, then run  Uninstall-XE-Local-AI-Engine.ps1
(right-click > Run with PowerShell). It stops the app + model engine and, after
you confirm, deletes your data dir (%LOCALAPPDATA%\XE-Local-AI-Engine). Then
delete this unzipped folder. To delete the data by hand instead, remove that
folder yourself.

This software is proprietary — all rights reserved. See the LICENSE file in this
folder before copying or passing it on; NOTICE lists third-party components.

Found a problem? In the app, use "Report a problem" (Diagnostics) to export a
snapshot, and send it back along with what you did and any console log lines.
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

To fully remove the app: stop it, then from a terminal in this folder run
  ./uninstall-xe-local-ai-engine.sh
It stops the app + model engine and, after you confirm, deletes your data dir
($HOME/.local/share/XE-Local-AI-Engine). Then delete this unzipped folder. To
delete the data by hand instead, remove that folder yourself.

This software is proprietary — all rights reserved. See the LICENSE file in this
folder before copying or passing it on; NOTICE lists third-party components.

Found a problem? In the app, use "Report a problem" (Diagnostics) to export a
snapshot, and send it back along with what you did and any terminal log lines.
TXT
  fi
}

# Fail the build if any per-node runtime/state artifact leaked into the staged bundle. CL-1 relocated all of these out
# of ContentRoot into the per-user data dir, so a clean publish never produces them; this guard codifies that and stops
# a regression (e.g. a re-committed dev node-settings.json, or a *.enc credential file) from ever shipping.
#
# Intended publish allowlist (everything else under the stage is one of these, by design):
#   - appsettings*.json            (app configuration; carries no secrets — validated in the CL-2 audit.
#                                   "No secrets" was never the whole story: nothing checked that a
#                                   REPLACE_/CHANGE_ME/TODO stub was not shipping as if it were real
#                                   configuration. assert_app_config_sane below closes that gap.)
#   - *.runtimeconfig.json         (the .NET runtime host config)
#   - *.deps.json                  (the .NET dependency manifest)
#   - manifest.json                (AgentHome layout manifests are runtime-only; the app's own manifest.json is an asset)
#   - LICENSE, NOTICE              (proprietary license + third-party notices; published via Content items in
#                                   XE-Local-AI-Engine.Client.csproj, so both the Velopack path and this script
#                                   pick them up from the publish dir. Neither guard below matches them.)
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

# Fail the build if the publish output carries runtime state the app wrote beside its own executable. Ported from
# publish/package-tester-win.ps1 (@43b5bb08), which grew both this tripwire and the publish-dir wipe in package_rid
# after 0.1.0-rc.5.0 shipped a maintainer log full of source paths plus a dead-letter-queue directory. That release
# was packed by the PowerShell path, but nothing about the mechanism is Windows-specific:
#   - logs/               ResolveLogFileDirectory (LoggerExtensions.cs) reads NodeData:Directory, which ONLY desktop
#                         mode layers in; any other run falls back to ContentRootPath — the executable's own folder.
#   - dead-letter-queue/  FileDeadLetterStore roots on AppContext.BaseDirectory UNCONDITIONALLY and calls
#                         Directory.CreateDirectory in its constructor, so EVERY launch recreates it, desktop or not.
# The same ContentRootPath fallback governs dp-keys/ and, behind the desktop flag, node.sqlite / node.key — so a stray
# in-place run can leak real secrets, not just host paths.
#
# The wipe removes the accumulation mechanism; this catches whatever is created DURING a run (a smoke test between
# publish and zip, or a new code path that writes beside the executable). It fails loudly rather than scrubbing
# quietly: a silent scrub is how the original leak stayed invisible, and a maintainer who sees this throw learns the
# publish directory was executed in.
#
# Complements assert_no_runtime_state, which scans the STAGE for node-settings.json / *.enc / *.pin. This one scans the
# publish output for the state and secret classes, and names why each is disqualifying.
assert_publish_output_clean() {
  local pub="$1" entry pattern reason match
  local leaks=""
  # Same patterns and reasons as $forbiddenPublishArtifacts in publish/package-tester-win.ps1 — keep the two lists in
  # step, so a class caught on one platform is not silently shippable on the other.
  local forbidden=(
    "logs|log output from running the app in the publish directory (leaks host paths)"
    "dead-letter-queue|dead-letter queue created beside the executable on every launch"
    "dp-keys|Data Protection key ring"
    "*.sqlite|node database"
    "*.sqlite-wal|node database write-ahead log"
    "*.sqlite-shm|node database shared memory"
    "node.key|node encryption key"
    "*.enc|encrypted secret store (HF token, GitHub token, provider credentials)"
    "desktop-port.txt|persisted desktop loopback port"
    "*.log|log file"
  )

  for entry in "${forbidden[@]}"; do
    pattern="${entry%%|*}"
    reason="${entry#*|}"
    # -name matches directories as well as files, which is the point: logs/, dead-letter-queue/ and dp-keys/ are
    # directories, and an EMPTY one still proves the executable ran here.
    while IFS= read -r match; do
      [[ -n "${match}" ]] || continue
      leaks+="  ${match#./} — ${reason}"$'\n'
    done < <(cd "${pub}" && find . -name "${pattern}" -print)
  done

  if [[ -n "${leaks}" ]]; then
    echo "Error: runtime state found in the publish output. Zipping would ship it to testers:" >&2
    printf '%s' "${leaks}" >&2
    echo "This means the application was executed from '${pub}'. Never run the published binary in place — it writes" >&2
    echo "logs, queues and (in desktop mode) its database and keys next to itself. Re-run this script; it wipes the" >&2
    echo "publish directory first. Smoke-test the zip instead, which is what a tester actually receives." >&2
    exit 1
  fi
  echo ">> Publish output carries no runtime state (no logs, queues, keys or databases)."
}

# Refuse to ship configuration that reads as live but is really placeholder text — the counterpart of the
# published-config guard in publish/package-tester-win.ps1 (which proves the TESTER config IS configured).
# Here the required outcome is the inverse: the bundle is a portable zip, so its in-app updater must be
# recognisably INERT, and every other appsettings*.json must be free of placeholder stubs.
#
# The inertness test mirrors AppUpdateChannelOptions.IsConfigured
# (XE-Local-AI-Engine.Client.Application/Services/AppUpdate/AppUpdateChannelOptions.cs): configured means an
# https://github.com/<owner>/<repo> URL whose segments start with no REPLACE_/CHANGE_ME/TODO marker, AND a
# GitHub App client ID of at least 16 chars starting with "Iv". Anything short of both leaves the updater off.
assert_app_config_sane() {
  local stage="$1"
  # Separate `local`: bash expands every word of a `local` command before any of its assignments take
  # effect, so a cfg="${stage}/…" on the line above would interpolate the OUTER scope's stage (SC2318).
  local cfg="${stage}/appsettings.AppUpdate.json"
  local channel repo_url client_id owner repo reason="" stray

  [[ -f "${cfg}" ]] || { echo "Error: published app-update config missing at ${cfg}." >&2; exit 1; }

  channel="$(grep -oP '"Channel"\s*:\s*"\K[^"]*' "${cfg}" | head -1)"
  repo_url="$(grep -oP '"GitHubRepositoryUrl"\s*:\s*"\K[^"]*' "${cfg}" | head -1)"
  client_id="$(grep -oP '"GitHubAppClientId"\s*:\s*"\K[^"]*' "${cfg}" | head -1)"

  if [[ "${channel}" != "${UPDATE_CHANNEL}" ]]; then
    echo "Error: bundle ships app-update channel '${channel}' but -p:UpdateChannel=${UPDATE_CHANNEL} was requested." >&2
    echo "Check the Content Include/Link rules in XE-Local-AI-Engine.Client.csproj." >&2
    exit 1
  fi

  # Why the updater is off. An empty reason means IsConfigured would be TRUE — a live updater in a bundle
  # that cannot self-update, which is exactly what must not ship.
  if [[ "${repo_url}" =~ ^https://github\.com/([^/[:space:]]+)/([^/[:space:]]+)/?$ ]]; then
    owner="${BASH_REMATCH[1]}"
    repo="${BASH_REMATCH[2]}"
    case "${owner^^}/${repo^^}" in
      REPLACE_*|CHANGE_ME*|TODO*|*/REPLACE_*|*/CHANGE_ME*|*/TODO*) reason="repository URL is a placeholder (${repo_url})" ;;
    esac
  else
    reason="repository URL is not a github.com/<owner>/<repo> URL (${repo_url:-<empty>})"
  fi
  if [[ ! "${client_id}" =~ ^Iv[A-Za-z0-9.]{14,}$ ]]; then
    reason="${reason:+${reason}; }GitHub App client ID is not a real 'Iv…' id (${client_id:-<empty>})"
  fi

  if [[ -z "${reason}" ]]; then
    echo "Error: ${cfg} reads as LIVE update configuration (channel '${channel}', repo '${repo_url}')." >&2
    echo "A portable zip is not a Velopack installation and must never self-update. Ship an inert channel," >&2
    echo "or move this artifact to publish/package-tester-win.ps1, which owns the real update feed." >&2
    exit 1
  fi
  echo ">> App-update config is inert by design: channel '${channel}' — ${reason}."
  echo "   AppUpdateChannelOptions.IsConfigured is false, so the in-app updater ships disabled."

  # Every OTHER appsettings*.json must be placeholder-free: appsettings.AppUpdate.json is the only file whose
  # REPLACE_ markers are legitimate, and only because the check above proved they leave the feature off.
  stray="$(cd "${stage}" && grep -rlE 'REPLACE_|CHANGE_ME|TODO' --include='appsettings*.json' . 2>/dev/null \
    | grep -v '^\./appsettings\.AppUpdate\.json$' || true)"
  if [[ -n "${stray}" ]]; then
    echo "Error: placeholder configuration would ship in:" >&2
    echo "${stray}" >&2
    echo "Replace the REPLACE_/CHANGE_ME/TODO values or remove the file from the publish output." >&2
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
  local rid="$1" version="$2" exe os base artifact stage pub zip_path
  case "${rid}" in
    win-x64)   os="windows"; exe="XE-Local-AI-Engine.Client.exe" ;;
    linux-x64) os="linux";   exe="XE-Local-AI-Engine.Client" ;;
    *) echo "Error: unsupported RID '${rid}' (expected win-x64 | linux-x64)." >&2; exit 2 ;;
  esac

  # Wipe the publish leaf BEFORE publishing. `dotnet publish` never removes stale files from its output directory, and
  # the app writes runtime state beside its own executable, so anything a previous in-place run left behind is copied
  # straight into the stage by the `cp -r` below — that is the 0.1.0-rc.5.0 leak mechanism (assert_publish_output_clean
  # documents it in full). Only the publish leaf goes: incremental state lives in obj/ and bin/<config>/<tfm>/<rid>/,
  # so this forces a fresh output copy, not a full rebuild.
  pub="${CLIENT_PROJECT}/bin/Release/net10.0/${rid}/publish"
  rm -rf "${pub}"

  echo ">> Publishing ${rid} (single-file self-contained)…"
  dotnet publish "${CLIENT_PROJECT}" -c Release -r "${rid}" -p:PublishProfile="${rid}" \
    -p:UpdateChannel="${UPDATE_CHANNEL}" --nologo

  [[ -f "${pub}/${exe}" ]] || { echo "Error: expected published binary not found at ${pub}/${exe}." >&2; exit 1; }
  # Tripwire for anything the app wrote here during THIS run (the wipe above only removes what earlier runs left).
  assert_publish_output_clean "${pub}"

  # Two names, deliberately different.
  #
  # `base` names the folder INSIDE the zip and stays versioned. These bundles never self-update, so the
  # documented upgrade path is "unzip the new one, delete the old folder" — which a tester can only carry out
  # correctly if the extracted folder states which version it is. Velopack has no equivalent need: it manages
  # its own install layout.
  #
  # `artifact` is the download name on the release page. linux-x64 follows the shape of the Velopack portable
  # asset that publish/package-tester-win.ps1 uploads (XE-Local-AI-Engine-win-Portable.zip) so a tester reads
  # one naming scheme across both platforms, but keeps the version: a downloaded file that names its own
  # version is worth more than an exact character match with the Windows asset, and a tester who has both
  # sitting in ~/Downloads can still tell them apart. The token is `linux` (the Velopack CHANNEL) rather than
  # the `linux-x64` RID for the same reason `win` is not `win-x64` over there — and it is what a future
  # `vpk pack --channel linux` would emit, so the name survives the Linux side moving onto the update feed.
  #
  # win-x64 keeps its own lowercase shape. What this script cross-builds on Linux is a non-self-updating
  # bundle that no one has smoke-tested on Windows; dressing it in the naming scheme of the artifact the
  # tester docs teach people to verify by (Tester-App docs/download-from-github.md, docs/install-windows.md)
  # invites confusing the two on a release page. That scheme belongs to publish/package-tester-win.ps1 alone.
  base="xe-local-ai-engine-${version}-${rid}"
  case "${rid}" in
    linux-x64) artifact="XE-Local-AI-Engine-${version}-linux-Portable" ;;
    *)         artifact="${base}" ;;
  esac
  stage="${DIST_DIR}/stage/${base}"
  rm -rf "${stage}"; mkdir -p "${stage}"
  cp -r "${pub}/." "${stage}/"

  if [[ "${os}" == "windows" ]]; then
    cp "${SCRIPT_DIR}/windows/run-xe-local-ai-engine.cmd" "${stage}/Start-XE-Local-AI-Engine.cmd"
    cp "${SCRIPT_DIR}/windows/uninstall-xe-local-ai-engine.ps1" "${stage}/Uninstall-XE-Local-AI-Engine.ps1"
  else
    cp "${SCRIPT_DIR}/linux/run-xe-local-ai-engine.sh" "${stage}/start-xe-local-ai-engine.sh"
    cp "${SCRIPT_DIR}/linux/uninstall-xe-local-ai-engine.sh" "${stage}/uninstall-xe-local-ai-engine.sh"
    chmod +x "${stage}/start-xe-local-ai-engine.sh" "${stage}/uninstall-xe-local-ai-engine.sh" "${stage}/${exe}"
  fi
  write_readme "${os}" "${stage}/READ-ME-FIRST.txt"

  # Refuse to ship a bundle carrying any per-node runtime/state artifact (node-settings.json / *.enc / *.pin).
  assert_no_runtime_state "${stage}"
  # Refuse to ship placeholder configuration, or an updater this bundle could never honour.
  assert_app_config_sane "${stage}"

  # make_zip already separates the archived folder (${base}) from the archive path (${zip_path}), so the
  # versioned inner folder and the Velopack-shaped download name coexist without special-casing anything.
  zip_path="${DIST_DIR}/${artifact}.zip"
  make_zip "${DIST_DIR}/stage" "${base}" "${zip_path}"
  ( cd "${DIST_DIR}" && sha256sum "${artifact}.zip" >"${artifact}.zip.sha256" )
  rm -rf "${stage}"
  echo ">> Built ${zip_path} (unzips to ${base}/)"
  ( cd "${DIST_DIR}" && du -h "${artifact}.zip" | cut -f1 | xargs -I{} echo "   size {}  sha256 $(cut -d' ' -f1 "${artifact}.zip.sha256")" )
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
