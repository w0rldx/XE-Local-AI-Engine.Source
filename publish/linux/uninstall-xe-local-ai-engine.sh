#!/bin/sh
# uninstall-xe-local-ai-engine.sh — remove XE Local AI Engine (Linux).
#
# What this does, in order:
#   1. Stops any running XE Local AI Engine process and the llama-server / sd-server
#      child runtimes THIS app spawned (matched strictly by executable path under the
#      app's own per-user data directory — an unrelated llama-server/Ollama is never
#      touched, mirroring the app's own StaleLlamaServerReaper).
#   2. If a Velopack-managed install is detected, notes that the OS/Velopack uninstall
#      owns the app binaries and points at it (this script does not delete a managed
#      install tree — Velopack does).
#   3. After an explicit confirmation, deletes ONLY the per-user data directory
#      ($XDG_DATA_HOME/XE-Local-AI-Engine, default ~/.local/share/XE-Local-AI-Engine):
#      node.sqlite, node.key, node-settings.json, hf-token.enc, the downloaded
#      llama.cpp / stable-diffusion.cpp binaries, the GGUF/image models, and the
#      AgentHome workspace.
#
# It NEVER deletes anything outside that exact directory. Portable-zip users: after
# running this, also delete the folder you unzipped the app into.
#
# Usage:
#   ./uninstall-xe-local-ai-engine.sh            # interactive (prompts before deleting)
#   ./uninstall-xe-local-ai-engine.sh --yes      # non-interactive (no prompt)
#   ./uninstall-xe-local-ai-engine.sh --dry-run  # show what would happen, delete nothing
#   ./uninstall-xe-local-ai-engine.sh --keep-data # stop processes only, keep the data dir
#   ./uninstall-xe-local-ai-engine.sh --help

set -eu

APP_NAME="XE Local AI Engine"
BINARY_NAME="XE-Local-AI-Engine.Client"   # running-process / binary name (NOT the data dir)
DATA_DIR_NAME="XE-Local-AI-Engine"        # per-user data directory name (no ".Client")

ASSUME_YES=0
DRY_RUN=0
KEEP_DATA=0
ALLOW_ROOT=0

usage() {
  # Print the leading comment header (lines after the shebang up to the first
  # non-comment line), stripping the "# " prefix.
  awk 'NR > 1 { if ($0 ~ /^#/) { sub(/^# ?/, ""); print } else { exit } }' "$0"
  exit "${1:-0}"
}

while [ $# -gt 0 ]; do
  case "$1" in
    -y|--yes) ASSUME_YES=1 ;;
    --dry-run) DRY_RUN=1 ;;
    --keep-data) KEEP_DATA=1 ;;
    --allow-root) ALLOW_ROOT=1 ;;
    -h|--help) usage 0 ;;
    *) printf 'Error: unknown argument "%s"\n\n' "$1" >&2; usage 2 ;;
  esac
  shift
done

# Per-user uninstall: running as root would target root's home and risk deleting the
# wrong data dir. Refuse unless the operator explicitly opts in.
if [ "$(id -u)" = "0" ] && [ "${ALLOW_ROOT}" -eq 0 ]; then
  echo "Error: do not run this uninstaller as root. XE Local AI Engine stores its data" >&2
  echo "under a normal user's home directory. Re-run it as the user who ran the app" >&2
  echo "(or pass --allow-root if you really installed it under this root account)." >&2
  exit 1
fi

DATA_HOME="${XDG_DATA_HOME:-${HOME}/.local/share}"
DATA_DIR="${DATA_HOME}/${DATA_DIR_NAME}"

echo ">> ${APP_NAME} uninstaller"
echo "   Data directory: ${DATA_DIR}"
[ "${DRY_RUN}" -eq 1 ] && echo "   (dry-run — nothing will be stopped or deleted)"
echo

# --- 1. Stop running processes ------------------------------------------------------

# Resolve a pid's executable path, or empty on failure.
exe_path() {
  # /proc/<pid>/exe is a symlink to the running binary.
  readlink "/proc/$1/exe" 2>/dev/null || true
}

# True when path "$1" is the dir "$2" or lives strictly under it (trailing-separator
# guard prevents a sibling-prefix false match, matching StaleLlamaServerReaper).
is_under_dir() {
  case "$1" in
    "$2"/*) return 0 ;;
    *) return 1 ;;
  esac
}

# Collect target PIDs: the app process (exe basename == BINARY_NAME) plus any
# llama-server / sd-server whose exe lives under our data dir.
collect_pids() {
  [ -d /proc ] || return 0
  for pid_dir in /proc/[0-9]*; do
    pid="${pid_dir#/proc/}"
    exe="$(exe_path "${pid}")"
    [ -n "${exe}" ] || continue
    base="${exe##*/}"
    case "${base}" in
      "${BINARY_NAME}")
        echo "${pid} ${exe}" ;;
      llama-server|sd-server|llama-server.*|sd-server.*)
        if is_under_dir "${exe}" "${DATA_DIR}"; then
          echo "${pid} ${exe}"
        fi ;;
    esac
  done
}

PIDS_INFO="$(collect_pids || true)"

if [ -n "${PIDS_INFO}" ]; then
  echo ">> Running ${APP_NAME} processes to stop:"
  echo "${PIDS_INFO}" | while IFS= read -r line; do
    [ -n "${line}" ] && echo "     pid ${line}"
  done
  if [ "${DRY_RUN}" -eq 0 ]; then
    # Graceful first (SIGTERM → the host reaps its own child runtimes), then force.
    PIDS="$(echo "${PIDS_INFO}" | awk '{print $1}')"
    for pid in ${PIDS}; do kill -TERM "${pid}" 2>/dev/null || true; done
    # Wait up to ~5s for graceful exit.
    waited=0
    while [ "${waited}" -lt 5 ]; do
      alive=0
      for pid in ${PIDS}; do
        kill -0 "${pid}" 2>/dev/null && alive=1
      done
      [ "${alive}" -eq 0 ] && break
      sleep 1
      waited=$((waited + 1))
    done
    for pid in ${PIDS}; do
      if kill -0 "${pid}" 2>/dev/null; then
        echo "     pid ${pid} did not exit gracefully — sending SIGKILL"
        kill -KILL "${pid}" 2>/dev/null || true
      fi
    done
    echo ">> Processes stopped."
  fi
else
  echo ">> No running ${APP_NAME} processes found."
fi
echo

# --- 2. Velopack-managed install note ----------------------------------------------

# Best-effort detection of a Velopack-managed install (installer/portable flavor):
# Velopack lays out a "current/" dir next to an "Update" helper binary.
VELOPACK_ROOT=""
for candidate in \
  "${DATA_DIR}" \
  "${HOME}/.local/${DATA_DIR_NAME}" \
  "${DATA_HOME}/${DATA_DIR_NAME}-app" \
  "${HOME}/Applications/${DATA_DIR_NAME}"; do
  if [ -d "${candidate}/current" ] || [ -x "${candidate}/Update" ]; then
    VELOPACK_ROOT="${candidate}"
    break
  fi
done

if [ -n "${VELOPACK_ROOT}" ]; then
  # A Velopack-managed install owns its own tree (and on some layouts that tree also
  # contains the data dir). We must NOT brute-force delete a managed install out from
  # under Velopack — hand it back to the Velopack/OS uninstall and stop here. The
  # portable/manual zip these scripts ship for is NOT managed, so it never reaches here.
  echo ">> A Velopack-managed install was detected at: ${VELOPACK_ROOT}"
  echo "   Velopack owns app removal. Uninstall the app itself with your OS/app menu"
  echo "   (or run its Update helper's uninstall). This script will not delete a managed"
  echo "   install tree. Processes were already stopped above."
  exit 0
fi

# --- 3. Delete the per-user data directory (portable / manual install) -------------

if [ "${KEEP_DATA}" -eq 1 ]; then
  echo ">> --keep-data: leaving ${DATA_DIR} in place. Done."
  exit 0
fi

if [ ! -d "${DATA_DIR}" ]; then
  echo ">> No data directory at ${DATA_DIR} — nothing left to remove. Done."
  exit 0
fi

if [ "${DRY_RUN}" -eq 1 ]; then
  echo ">> Dry-run: would delete ${DATA_DIR} (and everything under it)."
  exit 0
fi

if [ "${ASSUME_YES}" -eq 0 ]; then
  echo "This will permanently delete your ${APP_NAME} data:"
  echo "  ${DATA_DIR}"
  echo "  (database, keys, settings, downloaded runtimes, and all models)."
  printf 'Type "y" to delete, anything else to cancel: '
  if ! IFS= read -r reply; then
    echo
    echo ">> No input (non-interactive without --yes) — aborting without deleting."
    exit 0
  fi
  case "${reply}" in
    y|Y) : ;;
    *) echo ">> Cancelled. Nothing was deleted."; exit 0 ;;
  esac
fi

# Guard: never operate on an empty/blank path.
: "${DATA_DIR:?data directory path is unexpectedly empty}"
rm -rf -- "${DATA_DIR}"
echo ">> Removed ${DATA_DIR}."
echo ">> ${APP_NAME} data removed. If you used the portable zip, also delete the folder"
echo "   you unzipped the app into."
