#!/usr/bin/env bash
set -euo pipefail

# XE-Local-AI-Engine — Linux host-agent uninstaller.
#
# Removes only what the product created: the user systemd unit, manifest-owned
# Docker containers/network/volumes, our XDG data/runtime/state dirs, desktop
# launchers, and the tray icon. Ownership is derived from the runtime manifest
# (containers[].name / .network / .volumes[].source) exactly like the runtime's
# own ContainerOwnership check — never from a broad `docker ps` wildcard.
#
# In `external` mode the manifest-scoped kill-list is the entire safety story:
# a user-owned container/volume/network not in our manifest survives untouched,
# and the user's Docker daemon / Ollama is never stopped or removed.

# ---------------------------------------------------------------------------
# Defaults / env overrides (mirror install-user-unit.sh exactly).
# ---------------------------------------------------------------------------
APPLICATIONS_DIR="${XE_APPLICATIONS_DIR:-/usr/share/applications}"
ICON_DIR="${XE_ICON_DIR:-/usr/share/icons/hicolor/256x256/apps}"
# Tray executable kept for env-override parity with install-user-unit.sh; the
# uninstaller removes launchers/icons by their fixed paths, not the executable
# (a package-managed binary is owned by dpkg/rpm, not by this script).
# shellcheck disable=SC2034  # parity with install-user-unit.sh; read for documentation only
TRAY_EXECUTABLE="${XE_TRAY_EXECUTABLE:-/usr/bin/xe-local-ai-engine-tray}"

XDG_CONFIG_HOME_RESOLVED="${XDG_CONFIG_HOME:-${HOME}/.config}"
XDG_RUNTIME_DIR_RESOLVED="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
XDG_STATE_HOME_RESOLVED="${XDG_STATE_HOME:-${HOME}/.local/state}"

CONFIG_DIR="${XDG_CONFIG_HOME_RESOLVED}/xe-host-agent"
RUNTIME_DIR="${XDG_RUNTIME_DIR_RESOLVED}/xe-host-agent"
STATE_DIR="${XDG_STATE_HOME_RESOLVED}/xe-host-agent"

MANIFEST_PATH="${CONFIG_DIR}/manifest.yaml"
RUNTIME_JSON_PATH="${RUNTIME_DIR}/runtime.json"

USER_SYSTEMD_DIR="${XDG_CONFIG_HOME_RESOLVED}/systemd/user"
UNIT_TARGET="${USER_SYSTEMD_DIR}/xe-host-agent.service"
UNIT_NAME="xe-host-agent.service"

DESKTOP_FILE="${APPLICATIONS_DIR}/xe-local-ai-engine.desktop"
DESKTOP_LOG_FILE="${APPLICATIONS_DIR}/xe-local-ai-engine-log.desktop"
ICON_TARGET="${ICON_DIR}/xe-local-ai-engine.ico"

DEFAULT_NETWORK="xe-engine-net"
# Static runtime binaries we may have extracted to a shared location (native).
RUNTIME_BINARIES=(
  "/usr/local/bin/dockerd-rootless.sh"
  "/usr/local/bin/dockerd-rootless-setuptool.sh"
  "/usr/local/bin/rootlesskit"
  "/usr/local/bin/rootlesskit-docker-proxy"
)

# ---------------------------------------------------------------------------
# Flags.
# ---------------------------------------------------------------------------
MODE="auto"
FORCE="false"
KEEP_MODELS="false"
KEEP_DATA="false"
DRY_RUN="false"
REMOVE_RUNTIME_BINARIES="false"

usage() {
  cat <<'EOF'
Usage:
  uninstall-host-agent.sh [options]

Removes the XE-Local-AI-Engine Linux host-agent: user systemd unit,
manifest-owned Docker containers/network/volumes, XDG config/runtime/state
data, desktop launchers, and the tray icon.

Options:
  --mode <auto|native|external>  Install type. Default: auto (manifest > runtime.json > native).
  -y, --yes                      Skip the typed confirmation (== Force). For automation/packaging.
  --keep-models                  Keep the owned models volume (e.g. ollama-models).
  --keep-data                    Keep config/runtime/state dirs (admin-token, hmac-secret, logs, manifest, node DB).
  --dry-run                      Print the removal inventory and exit without deleting anything.
  --remove-runtime-binaries      native only: also remove static docker/rootless binaries from /usr/local/bin (default OFF).
  --help, -h                     Show this help.

Environment overrides (mirror install-user-unit.sh):
  XE_APPLICATIONS_DIR   Default: /usr/share/applications
  XE_ICON_DIR           Default: /usr/share/icons/hicolor/256x256/apps
  XE_TRAY_EXECUTABLE    Default: /usr/bin/xe-local-ai-engine-tray

Safety:
  external mode removes ONLY manifest-owned Docker artifacts; it never enumerates,
  prunes, or wildcards, and never touches the user's Docker daemon or Ollama.
  Without -y the script prints a full inventory and requires a typed 'yes'.
EOF
}

log() {
  printf '[uninstall-host-agent] %s\n' "$*"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --mode)
      MODE="${2:-}"
      shift 2
      ;;
    -y|--yes)
      FORCE="true"
      shift
      ;;
    --keep-models)
      KEEP_MODELS="true"
      shift
      ;;
    --keep-data)
      KEEP_DATA="true"
      shift
      ;;
    --dry-run)
      DRY_RUN="true"
      shift
      ;;
    --remove-runtime-binaries)
      REMOVE_RUNTIME_BINARIES="true"
      shift
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

case "${MODE}" in
  auto|native|external) ;;
  *)
    echo "Invalid --mode: ${MODE} (expected auto|native|external)" >&2
    exit 1
    ;;
esac

# ---------------------------------------------------------------------------
# Manifest parsing (robust to the sample-manifest indentation).
#
# We deliberately use small awk passes rather than a YAML library so the
# script stays dependency-free. Ownership rules (match ContainerOwnership):
#   - owned containers  = containers[].name     (inside containers: block only)
#   - owned network     = containers[].network  (inside containers: block only)
#   - owned volumes     = containers[].volumes[].source values that are bare
#                         Docker named-volume tokens: [A-Za-z0-9][A-Za-z0-9._-]*
#                         (no slash, no backslash, no colon) — everything else
#                         is a host bind-mount we do not own.
#
# The containers:-block awk state machine exits the in-containers region as
# soon as it sees a top-level key (indentation <= base column of "containers:"),
# preventing stray name:/network:/source: keys under models:/metadata:/etc.
# from leaking into the Docker kill-list.
# ---------------------------------------------------------------------------
OWNED_CONTAINERS=()
OWNED_VOLUMES=()
OWNED_NETWORK="${DEFAULT_NETWORK}"
MANIFEST_RUNTIME_MODE=""

parse_manifest() {
  local manifest="$1"
  [[ -f "${manifest}" ]] || return 0

  MANIFEST_RUNTIME_MODE="$(
    awk '
      /^[[:space:]]*runtimeMode:[[:space:]]*/ {
        sub(/^[[:space:]]*runtimeMode:[[:space:]]*/, "")
        gsub(/["'\'']/, "")
        sub(/[[:space:]]*#.*$/, "")
        gsub(/[[:space:]]+$/, "")
        print
        exit
      }
    ' "${manifest}"
  )"

  # All three captures (name, network, source) use a shared containers:-block
  # indentation state machine so keys outside that block are ignored.
  local names
  names="$(
    awk '
      /^[[:space:]]*containers:[[:space:]]*$/ { inc=1; match($0,/[^ ]/); base=RSTART; next }
      inc && /^[[:space:]]*[A-Za-z]/ { match($0,/[^ ]/); if (RSTART <= base) { inc=0; next } }
      inc && /^[[:space:]]*-?[[:space:]]*name:[[:space:]]*/ {
        s=$0; sub(/^[[:space:]]*-?[[:space:]]*name:[[:space:]]*/,"",s)
        gsub(/["'\'']/, "", s); sub(/[[:space:]]*#.*$/, "", s); gsub(/[[:space:]]+$/, "", s)
        if (s != "") print s
      }
    ' "${manifest}"
  )"
  local name
  while IFS= read -r name; do
    [[ -n "${name}" ]] && OWNED_CONTAINERS+=("${name}")
  done <<<"${names}"

  local network
  network="$(
    awk '
      /^[[:space:]]*containers:[[:space:]]*$/ { inc=1; match($0,/[^ ]/); base=RSTART; next }
      inc && /^[[:space:]]*[A-Za-z]/ { match($0,/[^ ]/); if (RSTART <= base) { inc=0 } }
      inc && /^[[:space:]]*network:[[:space:]]*/ {
        s=$0; sub(/^[[:space:]]*network:[[:space:]]*/,"",s)
        gsub(/["'\'']/, "", s); sub(/[[:space:]]*#.*$/, "", s); gsub(/[[:space:]]+$/, "", s)
        if (s != "") { print s; exit }
      }
    ' "${manifest}"
  )"
  [[ -n "${network}" ]] && OWNED_NETWORK="${network}"

  # Named volumes only: a Docker named volume is a bare token matching
  # [A-Za-z0-9][A-Za-z0-9._-]* — no slash, no backslash, no colon.
  # Anything else (absolute path, relative path, Windows path) is a host
  # bind-mount we do not own and must not remove.
  local sources
  sources="$(
    awk '
      /^[[:space:]]*containers:[[:space:]]*$/ { inc=1; match($0,/[^ ]/); base=RSTART; next }
      inc && /^[[:space:]]*[A-Za-z]/ { match($0,/[^ ]/); if (RSTART <= base) { inc=0 } }
      inc && /^[[:space:]]*-?[[:space:]]*source:[[:space:]]*/ {
        s=$0; sub(/^[[:space:]]*-?[[:space:]]*source:[[:space:]]*/,"",s)
        gsub(/["'\'']/, "", s); sub(/[[:space:]]*#.*$/, "", s); gsub(/[[:space:]]+$/, "", s)
        if (s ~ /^[A-Za-z0-9][A-Za-z0-9._-]*$/) print s
      }
    ' "${manifest}"
  )"
  local source
  while IFS= read -r source; do
    [[ -n "${source}" ]] && OWNED_VOLUMES+=("${source}")
  done <<<"${sources}"

  return 0
}

read_runtime_mode_from_json() {
  local json="$1"
  [[ -f "${json}" ]] || return 0
  # Minimal extraction: "runtimeMode": "value". No secret fields are read.
  grep -oE '"runtimeMode"[[:space:]]*:[[:space:]]*"[^"]*"' "${json}" 2>/dev/null |
    head -n1 |
    sed -E 's/.*"runtimeMode"[[:space:]]*:[[:space:]]*"([^"]*)".*/\1/'
}

# ---------------------------------------------------------------------------
# Mode resolution: explicit --mode > manifest runtimeMode > runtime.json > native.
# ---------------------------------------------------------------------------
parse_manifest "${MANIFEST_PATH}"

RESOLVED_MODE=""
MODE_SOURCE=""
if [[ "${MODE}" != "auto" ]]; then
  RESOLVED_MODE="${MODE}"
  MODE_SOURCE="--mode flag"
elif [[ -n "${MANIFEST_RUNTIME_MODE}" ]]; then
  RESOLVED_MODE="${MANIFEST_RUNTIME_MODE}"
  MODE_SOURCE="manifest runtimeMode (${MANIFEST_PATH})"
else
  runtime_json_mode="$(read_runtime_mode_from_json "${RUNTIME_JSON_PATH}")"
  if [[ -n "${runtime_json_mode}" ]]; then
    RESOLVED_MODE="${runtime_json_mode}"
    MODE_SOURCE="runtime.json (${RUNTIME_JSON_PATH})"
  else
    RESOLVED_MODE="native"
    MODE_SOURCE="default (no manifest/runtime.json found)"
  fi
fi

case "${RESOLVED_MODE}" in
  managed|native|external) ;;
  *)
    log "WARNING: unrecognized runtimeMode '${RESOLVED_MODE}' from ${MODE_SOURCE}; treating as 'native'."
    RESOLVED_MODE="native"
    ;;
esac

if [[ "${MODE_SOURCE}" == default* ]]; then
  log "WARNING: assuming mode '${RESOLVED_MODE}' — ${MODE_SOURCE}. Pass --mode to override."
fi

# ---------------------------------------------------------------------------
# Docker endpoint (rootless): only set DOCKER_HOST if not already set.
# ---------------------------------------------------------------------------
if [[ -z "${DOCKER_HOST:-}" ]]; then
  export DOCKER_HOST="unix://${XDG_RUNTIME_DIR_RESOLVED}/docker.sock"
fi

docker_available() {
  command -v docker >/dev/null 2>&1
}

container_exists() {
  docker_available || return 1
  docker container inspect "$1" >/dev/null 2>&1
}

network_exists() {
  docker_available || return 1
  docker network inspect "$1" >/dev/null 2>&1
}

volume_exists() {
  docker_available || return 1
  docker volume inspect "$1" >/dev/null 2>&1
}

# ---------------------------------------------------------------------------
# Inventory.
# Status tokens: [remove] / [keep:flag] / [absent].
# ---------------------------------------------------------------------------
inventory_line() {
  printf '  %-14s %s\n' "$1" "$2"
}

print_inventory() {
  log "Resolved install mode: ${RESOLVED_MODE} (source: ${MODE_SOURCE})"
  echo
  echo "The following will be removed (status per target):"
  echo

  echo "Service:"
  if [[ -f "${UNIT_TARGET}" ]]; then
    inventory_line "[remove]" "user unit ${UNIT_TARGET} (stop + disable + daemon-reload)"
  else
    inventory_line "[absent]" "user unit ${UNIT_TARGET}"
  fi
  echo

  echo "Docker (manifest-scoped, mode=${RESOLVED_MODE}):"
  if ! docker_available; then
    inventory_line "[absent]" "docker CLI not found — Docker teardown will be skipped"
  elif [[ ${#OWNED_CONTAINERS[@]} -eq 0 && ${#OWNED_VOLUMES[@]} -eq 0 ]]; then
    inventory_line "[absent]" "no manifest found at ${MANIFEST_PATH} — no owned containers/volumes to remove"
  fi

  local container
  for container in "${OWNED_CONTAINERS[@]}"; do
    if container_exists "${container}"; then
      inventory_line "[remove]" "container ${container}"
    else
      inventory_line "[absent]" "container ${container}"
    fi
  done

  if [[ ${#OWNED_CONTAINERS[@]} -gt 0 ]]; then
    if network_exists "${OWNED_NETWORK}"; then
      inventory_line "[remove]" "network ${OWNED_NETWORK} (owned)"
    else
      inventory_line "[absent]" "network ${OWNED_NETWORK}"
    fi
  fi

  local volume
  for volume in "${OWNED_VOLUMES[@]}"; do
    if [[ "${KEEP_MODELS}" == "true" ]]; then
      inventory_line "[keep:--keep-models]" "volume ${volume}"
    elif volume_exists "${volume}"; then
      inventory_line "[remove]" "volume ${volume}"
    else
      inventory_line "[absent]" "volume ${volume}"
    fi
  done
  echo

  echo "Data directories:"
  local dir
  for dir in "${CONFIG_DIR}" "${RUNTIME_DIR}" "${STATE_DIR}"; do
    if [[ "${KEEP_DATA}" == "true" ]]; then
      inventory_line "[keep:--keep-data]" "${dir}"
    elif [[ -e "${dir}" ]]; then
      inventory_line "[remove]" "${dir}"
    else
      inventory_line "[absent]" "${dir}"
    fi
  done
  echo

  echo "Desktop integration:"
  local file
  for file in "${DESKTOP_FILE}" "${DESKTOP_LOG_FILE}" "${ICON_TARGET}"; do
    if [[ -e "${file}" ]]; then
      inventory_line "[remove]" "${file}"
    else
      inventory_line "[absent]" "${file}"
    fi
  done
  echo

  echo "Runtime binaries (/usr/local/bin):"
  if [[ "${RESOLVED_MODE}" == "native" && "${REMOVE_RUNTIME_BINARIES}" == "true" ]]; then
    local binary
    for binary in "${RUNTIME_BINARIES[@]}"; do
      if [[ -e "${binary}" ]]; then
        inventory_line "[remove]" "${binary}"
      else
        inventory_line "[absent]" "${binary}"
      fi
    done
  elif [[ "${RESOLVED_MODE}" == "external" ]]; then
    inventory_line "[keep:external]" "static runtime binaries (never removed in external mode)"
  else
    inventory_line "[keep:default]" "static runtime binaries (use --remove-runtime-binaries in native mode)"
  fi
  echo
}

# ---------------------------------------------------------------------------
# Teardown helpers (best-effort, idempotent; guarded for set -e).
# ---------------------------------------------------------------------------
HARD_ERROR="false"
REMOVED_COUNT=0
SKIPPED_COUNT=0

mark_removed() {
  REMOVED_COUNT=$((REMOVED_COUNT + 1))
  log "removed: $1"
}

mark_skipped() {
  SKIPPED_COUNT=$((SKIPPED_COUNT + 1))
  log "skipped (absent): $1"
}

mark_error() {
  HARD_ERROR="true"
  log "ERROR: $1"
}

teardown_service() {
  command -v systemctl >/dev/null 2>&1 || { log "systemctl not found; skipping unit teardown."; return 0; }

  # stop / disable are no-ops if the unit is not running / not enabled.
  # Capture non-zero results and log a warning so a stuck unit is visible.
  local sc_out
  if ! sc_out="$(systemctl --user stop "${UNIT_NAME}" 2>&1)"; then
    log "WARNING: systemctl --user stop ${UNIT_NAME}: ${sc_out}"
  fi
  if ! sc_out="$(systemctl --user disable "${UNIT_NAME}" 2>&1)"; then
    log "WARNING: systemctl --user disable ${UNIT_NAME}: ${sc_out}"
  fi

  if [[ -f "${UNIT_TARGET}" ]]; then
    if rm -f -- "${UNIT_TARGET}"; then
      mark_removed "user unit ${UNIT_TARGET}"
    else
      mark_error "failed to remove ${UNIT_TARGET}"
    fi
  else
    mark_skipped "user unit ${UNIT_TARGET}"
  fi

  systemctl --user daemon-reload >/dev/null 2>&1 || true
}

teardown_docker() {
  if ! docker_available; then
    log "docker CLI not found; skipping manifest-scoped Docker teardown."
    return 0
  fi
  if [[ ${#OWNED_CONTAINERS[@]} -eq 0 && ${#OWNED_VOLUMES[@]} -eq 0 ]]; then
    log "no manifest-owned Docker artifacts resolved; nothing to remove."
    return 0
  fi

  # Order: containers -> network -> volumes (volume rm fails while referenced).
  local container
  for container in "${OWNED_CONTAINERS[@]}"; do
    if container_exists "${container}"; then
      if docker rm -f -- "${container}" >/dev/null 2>&1; then
        mark_removed "container ${container}"
      else
        mark_error "failed to remove container ${container}"
      fi
    else
      mark_skipped "container ${container}"
    fi
  done

  if [[ ${#OWNED_CONTAINERS[@]} -gt 0 ]]; then
    if network_exists "${OWNED_NETWORK}"; then
      local net_err
      if net_err="$(docker network rm -- "${OWNED_NETWORK}" 2>&1)"; then
        mark_removed "network ${OWNED_NETWORK}"
      else
        # "has active endpoints" / "in use" = a foreign container still uses
        # our network; that is benign — skip, do not hard-error.
        case "${net_err}" in
          *"active endpoints"*|*"in use"*|*"has active"*)
            mark_skipped "network ${OWNED_NETWORK} (still in use by a foreign container — remove manually after stopping it)"
            ;;
          *)
            mark_error "failed to remove network ${OWNED_NETWORK}: ${net_err}"
            ;;
        esac
      fi
    else
      mark_skipped "network ${OWNED_NETWORK}"
    fi
  fi

  if [[ "${KEEP_MODELS}" == "true" ]]; then
    local kept
    for kept in "${OWNED_VOLUMES[@]}"; do
      log "keep (--keep-models): volume ${kept}"
    done
    return 0
  fi

  local volume
  for volume in "${OWNED_VOLUMES[@]}"; do
    if volume_exists "${volume}"; then
      if docker volume rm -- "${volume}" >/dev/null 2>&1; then
        mark_removed "volume ${volume}"
      else
        mark_error "failed to remove volume ${volume} (still referenced?)"
      fi
    else
      mark_skipped "volume ${volume}"
    fi
  done
}

remove_dir() {
  # Guard: an unset/empty path must never reach rm -rf.
  local dir="${1:?remove_dir requires a non-empty path}"
  if [[ -e "${dir}" ]]; then
    if rm -rf -- "${dir}"; then
      mark_removed "directory ${dir}"
    else
      mark_error "failed to remove directory ${dir}"
    fi
  else
    mark_skipped "directory ${dir}"
  fi
}

teardown_data() {
  if [[ "${KEEP_DATA}" == "true" ]]; then
    log "keep (--keep-data): ${CONFIG_DIR} ${RUNTIME_DIR} ${STATE_DIR}"
    return 0
  fi
  remove_dir "${CONFIG_DIR}"
  remove_dir "${RUNTIME_DIR}"
  remove_dir "${STATE_DIR}"
}

remove_file() {
  local file="${1:?remove_file requires a non-empty path}"
  if [[ -e "${file}" ]]; then
    if rm -f -- "${file}"; then
      mark_removed "file ${file}"
    else
      mark_error "failed to remove file ${file}"
    fi
  else
    mark_skipped "file ${file}"
  fi
}

teardown_desktop() {
  remove_file "${DESKTOP_FILE}"
  remove_file "${DESKTOP_LOG_FILE}"
  remove_file "${ICON_TARGET}"
}

teardown_runtime_binaries() {
  if [[ "${RESOLVED_MODE}" != "native" ]]; then
    return 0
  fi
  if [[ "${REMOVE_RUNTIME_BINARIES}" != "true" ]]; then
    log "keep (default): static runtime binaries in /usr/local/bin (use --remove-runtime-binaries)."
    return 0
  fi
  local binary
  for binary in "${RUNTIME_BINARIES[@]}"; do
    remove_file "${binary}"
  done
}

# ---------------------------------------------------------------------------
# Run.
# ---------------------------------------------------------------------------
print_inventory

if [[ "${DRY_RUN}" == "true" ]]; then
  log "Dry run: no changes made."
  exit 0
fi

if [[ "${FORCE}" != "true" ]]; then
  printf 'Type "yes" to remove everything listed above: '
  read -r confirmation || confirmation=""
  if [[ "${confirmation}" != "yes" ]]; then
    log "Aborted: confirmation not given. No changes made."
    exit 0
  fi
fi

log "Starting teardown (mode=${RESOLVED_MODE})..."
teardown_service
teardown_docker
teardown_data
teardown_desktop
teardown_runtime_binaries

echo
log "Summary: removed=${REMOVED_COUNT} skipped=${SKIPPED_COUNT}"
if [[ "${HARD_ERROR}" == "true" ]]; then
  log "Completed with errors — see ERROR lines above."
  exit 1
fi
log "Uninstall complete."
exit 0
