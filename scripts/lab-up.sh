#!/usr/bin/env bash
# lab-up.sh — bring this checkout's lab node to a declared state and hand it over before any LLM turn.
#
# Why this exists
#   Every live round re-paid the same deterministic setup by hand: a scratch data dir, a fresh node, a throwaway
#   admin, the settings a round needs, the model installed and resident. This script does that part, skips every
#   phase that is already satisfied, and stops at a handoff point. Everything that involves model output is judged by
#   whoever reads the manifest, never by this script.
#
# Phases, in order (each "ensures" a state and is recorded as ran|skipped|stopped with a reason):
#   data        .tmp/lab/<profile>/xdg mirrors ~/.local/share (every child symlinked except XE-Local-AI-Engine) and
#               the lab env is exported (a profile `env` key never overrides a variable the caller already set). --fresh deletes the node state; otherwise a snapshot whose key matches this
#               checkout's migration set is restored when no node state exists.
#   host        scripts/dev-start.sh, unless dev-status.sh already shows this checkout's host; then wait until
#               GET auth/status answers.
#   auth        always: first-run setup if required, then login; the token goes to .tmp/lab/<profile>/token.
#   settings    PUT only the node settings that differ from the profile's `settings`.
#   model       the model is installed, selected as default, the others ejected; resident is reported honestly.
#   first-turn  opt-in (--until first-turn): one canned prompt, transcript in the evidence dir.
#   The host is left running: this is a lab. Stop it with scripts/dev-stop.sh.
#
# Usage:
#   scripts/lab-up.sh [options]
#
# Options:
#   --profile NAME   scripts/lab/profiles/NAME.json (default `default`).
#   --model NAME     Override the profile's `model`; substituted for `<model>` in the profile's settings.
#                    A model switch on a live host is `--until model --model <other>`.
#   --until PHASE    data|host|auth|settings|model|first-turn (default: the profile's `stop`).
#   --fresh          Start from a clean node: delete this checkout's node state (refused while its host runs).
#   --snapshot       After settings: stop the host, save the node state to .tmp/lab/<profile>/snapshot, restart.
#   --download       Install a missing model through the node from the profile's `modelSource`.
#   --no-build       Pass --no-build to dev-start.sh.
#   --plan NAME      The plan that owns this lab (default: env XE_LAB_PLAN, else `unassigned`).
#   --note TEXT      Free-text session note stored in the lab's OWNER file.
#   --take-over      Re-assign a lab owned by another plan to --plan (keeps `created`, adds `taken_over`).
#   --json           Print only the manifest JSON on stdout.
#   --help           Show this message.
#
# Ownership: .tmp/lab/<profile>/OWNER is a key=value file (plan, created, delete_after = created + 14 days,
# worktree, session_note, and taken_over or adopted when that happened). A lab owned by another plan is refused
# (exit 2) unless --take-over; a lab without OWNER is adopted by the requested plan. delete_after is informational:
# nothing here deletes a lab.
#
# Environment: LAB_EMAIL / LAB_PASSWORD (default admin@localhost.test / !Demo1234567), LAB_READY_TIMEOUT_SECONDS
# (default 240). Every driver call is appended to .tmp/lab/<profile>/evidence/lab-up.log, password and token redacted.
#
# Prerequisites: python3 and curl on PATH; aspire for the host phase (dev-start.sh checks it).
#
# Exit codes:
#   0   — the requested phases are in place; the manifest was written
#   1   — an interaction failed (transport, auth, contract, settings not verified, download)
#   2   — prerequisite missing / usage error (profile missing, model absent without --download, lab owned by
#         another plan without --take-over, XE_ASPIRE_APPHOST
#         naming another checkout's AppHost, XE_NODE_OPERATOR_SECRET_FILE outside this checkout's .data)
#   3   — this checkout's host is running but not for this lab (its environment differs), or --fresh while it runs
#   4   — could not establish whether this checkout's host is running
#   5   — the node did not start or did not answer within the timeout

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
if ! ROOT="$(git -C "${SCRIPT_DIR}" rev-parse --show-toplevel 2>/dev/null)"; then
  ROOT="$(cd "${SCRIPT_DIR}/.." && pwd -P)"
fi
GPU_DRIVER="${SCRIPT_DIR}/gpu-smoke-driver.py"
LAB_DRIVER="${SCRIPT_DIR}/lab-driver.py"
MATRIX_DRIVER="${SCRIPT_DIR}/model-matrix-driver.py"
APPHOST="${ROOT}/XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj"
APPHOST_DATA="${ROOT}/XE-Local-AI-Engine.AppHost/.data"
# The dev lifecycle helpers honour XE_ASPIRE_APPHOST. This lab works on THIS checkout only: an inherited override
# naming another AppHost would make dev-status answer for that host while --fresh deletes this checkout's state.
if [[ -n "${XE_ASPIRE_APPHOST:-}" && "$(cd "$(dirname "${XE_ASPIRE_APPHOST}")" 2>/dev/null && pwd -P)/$(basename "${XE_ASPIRE_APPHOST}")" != "${APPHOST}" ]]; then
  echo "[lab-up] XE_ASPIRE_APPHOST=${XE_ASPIRE_APPHOST} names another AppHost; this lab works on ${APPHOST} only. Unset it." >&2
  exit 2
fi
export XE_ASPIRE_APPHOST="${APPHOST}"
# Likewise the operator secret: dev-start.sh honours XE_NODE_OPERATOR_SECRET_FILE, and a key outside .data would be
# missing from a snapshot (which copies .data and dp-keys), so a restore could never decrypt. The lab owns its key.
if [[ -n "${XE_NODE_OPERATOR_SECRET_FILE:-}" && "$(cd "$(dirname "${XE_NODE_OPERATOR_SECRET_FILE}")" 2>/dev/null && pwd -P)/$(basename "${XE_NODE_OPERATOR_SECRET_FILE}")" != "${APPHOST_DATA}/node.key" ]]; then
  echo "[lab-up] XE_NODE_OPERATOR_SECRET_FILE=${XE_NODE_OPERATOR_SECRET_FILE} is outside this checkout's node state; the lab snapshot could not carry it. Unset it." >&2
  exit 2
fi
export XE_NODE_OPERATOR_SECRET_FILE="${APPHOST_DATA}/node.key"
CLIENT_DIR="${ROOT}/XE-Local-AI-Engine.Client"
EMAIL="${LAB_EMAIL:-admin@localhost.test}"
PASSWORD="${LAB_PASSWORD:-!Demo1234567}"
READY_TIMEOUT="${LAB_READY_TIMEOUT_SECONDS:-240}"
PHASES=(data host auth settings model first-turn)

log() { echo "[lab-up] $*" >&2; }
die() { local code="$1"; shift; echo "[lab-up] $*" >&2; exit "${code}"; }
usage() { sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//; $d'; }

record_value() { awk -F'\t' -v k="$1" '$1 == k { sub(/^[^\t]*\t/, ""); print; exit }' <<<"$2"; }
record_values() { awk -F'\t' -v k="$1" '$1 == k { sub(/^[^\t]*\t/, ""); print }' <<<"$2"; }

PROFILE="default"
MODEL_OVERRIDE=""
UNTIL=""
FRESH="false"
SNAPSHOT_REQ="false"
DOWNLOAD="false"
NO_BUILD="false"
JSON="false"
PLAN="${XE_LAB_PLAN:-unassigned}"
NOTE=""
TAKE_OVER="false"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --plan)      PLAN="${2:-}"; [[ -n "${PLAN}" ]] || die 2 "--plan needs a value"; shift 2 ;;
    --note)      NOTE="${2:-}"; [[ -n "${NOTE}" ]] || die 2 "--note needs a value"; shift 2 ;;
    --take-over) TAKE_OVER="true"; shift ;;
    --profile)  PROFILE="${2:-}"; [[ -n "${PROFILE}" ]] || die 2 "--profile needs a value"; shift 2 ;;
    --model)    MODEL_OVERRIDE="${2:-}"; [[ -n "${MODEL_OVERRIDE}" ]] || die 2 "--model needs a value"; shift 2 ;;
    --until)    UNTIL="${2:-}"; [[ -n "${UNTIL}" ]] || die 2 "--until needs a value"; shift 2 ;;
    --fresh)    FRESH="true"; shift ;;
    --snapshot) SNAPSHOT_REQ="true"; shift ;;
    --download) DOWNLOAD="true"; shift ;;
    --no-build) NO_BUILD="true"; shift ;;
    --json)     JSON="true"; shift ;;
    --help|-h)  usage; exit 0 ;;
    *)          echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

for tool in python3 curl; do
  command -v "${tool}" >/dev/null 2>&1 || die 2 "PREREQUISITE MISSING: ${tool} is not on PATH."
done
[[ "${PROFILE}" =~ ^[A-Za-z0-9._-]+$ ]] || die 2 "invalid profile name '${PROFILE}'"
PROFILE_FILE="${SCRIPT_DIR}/lab/profiles/${PROFILE}.json"
[[ -f "${PROFILE_FILE}" ]] || die 2 "PREREQUISITE MISSING: profile not found at ${PROFILE_FILE}"
[[ -f "${LAB_DRIVER}" ]] || die 2 "PREREQUISITE MISSING: driver not found at ${LAB_DRIVER}"

LAB="${ROOT}/.tmp/lab/${PROFILE}"
EVIDENCE="${LAB}/evidence"
DRIVER_LOG="${EVIDENCE}/lab-up.log"
TOKEN_FILE="${LAB}/token"
DESIRED="${LAB}/desired-settings.json"
OWNER_FILE="${LAB}/OWNER"
[[ "${PLAN}" =~ ^[A-Za-z0-9._-]+$ ]] || die 2 "invalid plan name '${PLAN}' (letters, digits, . _ - only)"

owner_value() { sed -n "s/^$1=//p" "${OWNER_FILE}" | head -n 1; }

# Writes OWNER before anything else touches the lab, so a lab is never left without one. A lab owned by another plan
# is refused unless --take-over; an existing lab without OWNER predates ownership and is adopted.
ensure_lab_owner() {
  local now created extra owner_plan
  now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  created="${now}"
  extra=""
  if [[ -f "${OWNER_FILE}" ]]; then
    owner_plan="$(owner_value plan)"
    owner_plan="${owner_plan:-unassigned}"
    [[ "${owner_plan}" != "${PLAN}" ]] || return 0
    [[ "${TAKE_OVER}" == "true" ]] \
      || die 2 "lab ${LAB} is owned by plan '${owner_plan}', this run asks for plan '${PLAN}'. Use --plan ${owner_plan}, or --take-over to re-assign it."
    created="$(owner_value created)"
    created="${created:-${now}}"
    extra="taken_over=${now}"
    log "lab ${LAB} taken over from plan '${owner_plan}' by plan '${PLAN}'"
  elif [[ -d "${LAB}" ]]; then
    extra="adopted=${now}"
    log "lab ${LAB} had no OWNER; adopted by plan '${PLAN}'"
  fi
  mkdir -p "${LAB}"
  {
    echo "plan=${PLAN}"
    echo "created=${created}"
    echo "delete_after=$(date -u -d "${created} + 14 days" +%Y-%m-%d)"
    echo "worktree=$(realpath "${ROOT}")"
    echo "session_note=${NOTE//$'\n'/ }"
    [[ -z "${extra}" ]] || echo "${extra}"
  } >"${OWNER_FILE}.tmp"
  mv -f -- "${OWNER_FILE}.tmp" "${OWNER_FILE}"
}
ensure_lab_owner
mkdir -p "${EVIDENCE}" "${LAB}/xdg"

# The profile is read once: scalar records, `env<TAB>KEY<TAB>VALUE` records, and the desired settings with <model>
# substituted, written to ${DESIRED}.
PROFILE_RECORDS="$(python3 - "${PROFILE_FILE}" "${MODEL_OVERRIDE}" "${DESIRED}" <<'PY'
import json, re, sys

path, override, desired_path = sys.argv[1:4]
try:
    with open(path, encoding="utf-8") as handle:
        profile = json.load(handle)
except (OSError, json.JSONDecodeError) as error:
    sys.exit(f"[lab-up] cannot read profile {path}: {error}")
model = override or profile.get("model") or ""
if not model:
    sys.exit(f"[lab-up] profile {path} has no model and no --model was given")

def substitute(value):
    if isinstance(value, str):
        return value.replace("<model>", model)
    if isinstance(value, dict):
        return {k: substitute(v) for k, v in value.items()}
    if isinstance(value, list):
        return [substitute(v) for v in value]
    return value

with open(desired_path, "w", encoding="utf-8") as handle:
    json.dump(substitute(profile.get("settings") or {}), handle, indent=2)
source = profile.get("modelSource") or {}
for key, value in (
    ("model", model),
    ("profileModel", profile.get("model") or ""),
    ("stop", profile.get("stop") or "model"),
    ("modelsDir", profile.get("modelsDir") or ".tmp/lab/models"),
    ("firstTurnPrompt", profile.get("firstTurnPrompt") or ""),
    ("sourceRepo", source.get("repo") or ""),
    ("sourceFile", source.get("file") or ""),
):
    print(f"{key}\t{str(value).replace(chr(10), ' ')}")
for key, value in (profile.get("env") or {}).items():
    if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", key):
        sys.exit(f"[lab-up] profile env key {key!r} is not a valid variable name")
    print(f"env\t{key}\t{str(value).replace(chr(10), ' ')}")
PY
)" || die 2 "the profile could not be read (see above)."

MODEL="$(record_value model "${PROFILE_RECORDS}")"
PROFILE_MODEL="$(record_value profileModel "${PROFILE_RECORDS}")"
UNTIL="${UNTIL:-$(record_value stop "${PROFILE_RECORDS}")}"
MODELS_DIR="$(record_value modelsDir "${PROFILE_RECORDS}")"
[[ "${MODELS_DIR}" == /* ]] || MODELS_DIR="${ROOT}/${MODELS_DIR}"
FIRST_TURN_PROMPT="$(record_value firstTurnPrompt "${PROFILE_RECORDS}")"
SOURCE_REPO="$(record_value sourceRepo "${PROFILE_RECORDS}")"
SOURCE_FILE="$(record_value sourceFile "${PROFILE_RECORDS}")"

until_index=-1
for i in "${!PHASES[@]}"; do [[ "${PHASES[$i]}" == "${UNTIL}" ]] && until_index="${i}"; done
[[ "${until_index}" -ge 0 ]] || die 2 "--until must be one of ${PHASES[*]}, got '${UNTIL}'"
[[ "${SNAPSHOT_REQ}" != "true" || "${until_index}" -ge 3 ]] || die 2 "--snapshot needs --until settings or later"
[[ "${UNTIL}" != "first-turn" || -n "${FIRST_TURN_PROMPT}" ]] || die 2 "profile ${PROFILE} has no firstTurnPrompt"

# Logs one driver call (argv with --password/--token values masked, stdout with the token masked) and passes the
# stdout through. The exit status is the driver's.
run_driver() {
  local out status=0 arg prev="" shown=()
  out="$("$@")" || status=$?
  for arg in "$@"; do
    if [[ "${prev}" == "--password" || "${prev}" == "--token" ]]; then shown+=("***"); else shown+=("${arg}"); fi
    prev="${arg}"
  done
  {
    echo "=== $(date -u +%Y-%m-%dT%H:%M:%SZ) ${shown[*]} -> ${status}"
    sed -E 's/^(token|accessToken)\t.*/\1\t***/' <<<"${out}"
  } >>"${DRIVER_LOG}"
  printf '%s\n' "${out}"
  return "${status}"
}
# A node subcommand of lab-driver.py: SUB, then the shared connection arguments, then the rest.
node_drive() {
  local sub="$1"; shift
  run_driver python3 "${LAB_DRIVER}" "${sub}" --base-url "${BASE_URL}" --token "${TOKEN}" --evidence "${EVIDENCE}" "$@"
}

# 0 = this checkout's host runs, 1 = it does not; anything else exits 4.
host_running() {
  local status=0
  "${SCRIPT_DIR}/dev-status.sh" >/dev/null 2>&1 || status=$?
  case "${status}" in
    0) return 0 ;;
    3) return 1 ;;
    *) die 4 "could not establish whether this checkout's host is running (dev-status.sh exit ${status})." ;;
  esac
}

declare -A PHASE_STATE=()
# Every variable the host is started with and cannot change afterwards; a running host is reused only when its
# process agrees on all of them (the profile's env keys are appended in phase_data).
HOST_ENV_KEYS=(XDG_DATA_HOME HuggingFace__ModelsDirectory XE_LLAMACPP_SERVER_PATH XE_LLAMACPP_VARIANT)
SNAPSHOT="none"
BASE_URL=""
VITE_URL=""
HOST_PID=""
TOKEN=""
MODEL_RESIDENT="false"
SETTINGS_APPLIED=()
EXTRA_SETS=()

phase_data() {
  local child name linked=0
  shopt -s nullglob dotglob
  for child in "${HOME}/.local/share"/*; do
    name="${child##*/}"
    [[ "${name}" == "XE-Local-AI-Engine" || -e "${LAB}/xdg/${name}" || -L "${LAB}/xdg/${name}" ]] && continue
    ln -s "${child}" "${LAB}/xdg/${name}"
    linked=$((linked + 1))
  done
  shopt -u nullglob dotglob
  mkdir -p "${LAB}/xdg/XE-Local-AI-Engine" "${MODELS_DIR}"
  export XDG_DATA_HOME="${LAB}/xdg"
  export MISE_DATA_DIR="${HOME}/.local/share/mise"
  export HuggingFace__ModelsDirectory="${MODELS_DIR}"
  export MSBUILDDISABLENODEREUSE=1
  export NUGET_PACKAGES="${HOME}/.nuget/packages"
  local _tag key value
  while IFS=$'\t' read -r _tag key value; do
    [[ "${_tag}" == "env" ]] || continue
    # The caller's environment wins, so a box-specific value never has to live in a tracked profile.
    [[ -z "${!key+set}" ]] && export "${key}=${value}"
    [[ " ${HOST_ENV_KEYS[*]} " == *" ${key} "* ]] || HOST_ENV_KEYS+=("${key}")
  done <<<"${PROFILE_RECORDS}"

  local key_records snapshot_key
  key_records="$(run_driver python3 "${LAB_DRIVER}" snapshot-key --migrations-dir "${ROOT}/XE-Local-AI-Engine.Client.Persistence/Migrations")" \
    || die 1 "snapshot-key failed (see ${DRIVER_LOG})."
  snapshot_key="$(record_value snapshotKey "${key_records}")"
  [[ -n "${snapshot_key}" ]] || die 1 "snapshot-key printed no snapshotKey record."
  SNAPSHOT_KEY="${snapshot_key}"

  if [[ "${FRESH}" == "true" ]]; then
    host_running && die 3 "--fresh refused: this checkout's host is running. Stop it with scripts/dev-stop.sh."
    # dp-keys is encrypted under node.key, so it goes with .data or the node fails closed on the old ring.
    rm -rf -- "${APPHOST_DATA}" "${CLIENT_DIR}/node-settings.json" "${CLIENT_DIR}/dp-keys" \
      "${LAB}/xdg/XE-Local-AI-Engine" "${TOKEN_FILE}"
    mkdir -p "${LAB}/xdg/XE-Local-AI-Engine"
    SNAPSHOT="fresh"
    PHASE_STATE[data]="ran:fresh node state"
    return 0
  fi
  if [[ -f "${LAB}/snapshot/key" ]]; then
    if [[ "$(<"${LAB}/snapshot/key")" != "${snapshot_key}" ]]; then
      SNAPSHOT="stale"
    elif [[ -e "${APPHOST_DATA}" ]]; then
      SNAPSHOT="skipped-existing-data"
    else
      mkdir -p "${APPHOST_DATA}"
      cp -a "${LAB}/snapshot/AppHost.data/." "${APPHOST_DATA}/"
      [[ ! -f "${LAB}/snapshot/node-settings.json" ]] || cp -a "${LAB}/snapshot/node-settings.json" "${CLIENT_DIR}/"
      if [[ -d "${LAB}/snapshot/dp-keys" ]]; then
        rm -rf -- "${CLIENT_DIR}/dp-keys"
        cp -a "${LAB}/snapshot/dp-keys" "${CLIENT_DIR}/dp-keys"
      fi
      SNAPSHOT="restored"
      PHASE_STATE[data]="ran:snapshot restored"
      return 0
    fi
  fi
  if [[ ! -e "${APPHOST_DATA}" ]]; then
    # No node state means dev-start.sh seeds a NEW node.key; a key ring or settings file left from an older node would
    # then fail closed (dp-keys is encrypted under the old key). A clean start removes them together.
    local leftover removed=()
    for leftover in "${CLIENT_DIR}/dp-keys" "${CLIENT_DIR}/node-settings.json"; do
      [[ -e "${leftover}" ]] || continue
      host_running && die 3 "no node state at ${APPHOST_DATA} but ${leftover} exists while this checkout's host runs; stop it with scripts/dev-stop.sh, then re-run (or use --fresh)."
      rm -rf -- "${leftover}"
      removed+=("${leftover##*/}")
    done
    PHASE_STATE[data]="ran:clean start (snapshot ${SNAPSHOT}${removed[0]+; removed leftover ${removed[*]}})"
  elif [[ "${linked}" -gt 0 ]]; then
    PHASE_STATE[data]="ran:xdg mirror created (snapshot ${SNAPSHOT})"
  else
    PHASE_STATE[data]="skipped:xdg mirror present (snapshot ${SNAPSHOT})"
  fi
}

# Starts this checkout's host and waits for it to answer, exit 5 otherwise. The startup key set is persisted beside the
# lab so a later run still compares a key this profile has since dropped (the host keeps it until restarted).
start_host() {
  local args=()
  [[ "${NO_BUILD}" == "true" ]] && args+=(--no-build)
  printf '%s\n' "${HOST_ENV_KEYS[@]}" >"${LAB}/host-env.keys"
  log "starting this checkout's host (log: ${EVIDENCE}/dev-start.log)"
  "${SCRIPT_DIR}/dev-start.sh" ${args[@]+"${args[@]}"} >>"${EVIDENCE}/dev-start.log" 2>&1 \
    || die 5 "dev-start.sh failed (see ${EVIDENCE}/dev-start.log); only one host may run per data directory."
  timeout "$((READY_TIMEOUT + 15))s" aspire wait app --apphost "${APPHOST}" --status healthy \
    --timeout "${READY_TIMEOUT}" --non-interactive --nologo >/dev/null \
    || die 5 "the app resource did not become healthy within ${READY_TIMEOUT}s."
}

# Reads the base URL, Vite URL and host pid from dev-status.sh --json, then waits for auth/status to answer.
discover_host() {
  local status_json urls
  status_json="$(XE_DEV_STATUS_SKIP_LAB_SCAN=1 "${SCRIPT_DIR}/dev-status.sh" --json 2>/dev/null)" \
    || die 5 "dev-status.sh --json failed."
  BASE_URL="$(python3 "${MATRIX_DRIVER}" base-url <<<"${status_json}")" \
    || die 5 "could not discover the app base URL from dev-status.sh --json."
  urls="$(python3 -c '
import json, sys
status = json.load(sys.stdin)
vite = ""
for resource in status.get("resources", []):
    if resource.get("name") == "client-react":
        vite = next((u["url"].rstrip("/") for u in resource.get("urls", []) if u.get("url")), "")
pid = status.get("pid")
# "|" is not IFS whitespace, so an empty Vite URL stays an empty first field instead of shifting the pid into it.
print(f"{vite}|{pid if isinstance(pid, int) else str()}")
' <<<"${status_json}")"
  IFS='|' read -r VITE_URL HOST_PID <<<"${urls}"
  local deadline=$((SECONDS + READY_TIMEOUT))
  until curl -skf --max-time 10 -o /dev/null "${BASE_URL}/api/local/v1/auth/status"; do
    [[ "${SECONDS}" -lt "${deadline}" ]] || die 5 "${BASE_URL} did not answer GET auth/status within ${READY_TIMEOUT}s."
    sleep 2
  done
  log "base URL ${BASE_URL}"
}

# A running host keeps the environment it was started with; the exports of phase_data cannot reach it. Reuse is
# allowed only when its process (read from /proc, Linux lab box) agrees with this run on every startup variable in
# HOST_ENV_KEYS: data and models directories, runtime override and variant, and the profile's env keys. A variable
# set on one side only is a mismatch too, so the manifest never describes a runtime the host does not have.
verify_running_host_env() {
  local environ="/proc/${HOST_PID}/environ" key wanted actual host_env
  [[ -n "${HOST_PID}" && -r "${environ}" ]] \
    || die 4 "could not read the running host's environment (${environ}); stop it with scripts/dev-stop.sh or re-run."
  host_env="$(tr '\0' '\n' <"${environ}")"
  # Keys the host was STARTED with (persisted by start_host) join the current set, so a key the profile has since
  # removed is still compared: the host still carries it, this run wants it unset, and that is a mismatch.
  if [[ -f "${LAB}/host-env.keys" ]]; then
    while IFS= read -r key; do
      [[ -z "${key}" || " ${HOST_ENV_KEYS[*]} " == *" ${key} "* ]] || HOST_ENV_KEYS+=("${key}")
    done <"${LAB}/host-env.keys"
  fi
  for key in "${HOST_ENV_KEYS[@]}"; do
    wanted="${!key-}"
    actual="$(grep -m1 "^${key}=" <<<"${host_env}" | cut -d= -f2- || true)"
    [[ "${actual}" == "${wanted}" ]] \
      || die 3 "this checkout's host (pid ${HOST_PID}) runs with ${key}=${actual:-<unset>}, this run wants ${key}=${wanted:-<unset>}: it is not this lab's host as configured now. Stop it with scripts/dev-stop.sh and re-run."
  done
}

phase_host() {
  if host_running; then
    discover_host
    verify_running_host_env
    PHASE_STATE[host]="skipped:this checkout's host is running with this lab's environment"
  else
    start_host
    discover_host
    PHASE_STATE[host]="ran:host started"
  fi
}

authenticate() {
  local records
  records="$(run_driver python3 "${GPU_DRIVER}" --base-url "${BASE_URL}" auth --email "${EMAIL}" --password "${PASSWORD}")" \
    || die 1 "authentication failed (see ${DRIVER_LOG})."
  TOKEN="$(record_value token "${records}")"
  [[ -n "${TOKEN}" ]] || die 1 "auth printed no token record."
  (umask 077 && printf '%s\n' "${TOKEN}" >"${TOKEN_FILE}")
  chmod 600 "${TOKEN_FILE}"
}

phase_auth() {
  authenticate
  PHASE_STATE[auth]="ran:token refreshed"
}

phase_settings() {
  local records
  records="$(node_drive settings-ensure --desired "@${DESIRED}")" \
    || die 1 "settings-ensure failed (see ${DRIVER_LOG})."
  mapfile -t SETTINGS_APPLIED < <(record_values changed "${records}" | sed '/^$/d')
  [[ "$(record_value verified "${records}")" == "true" ]] \
    || die 1 "the node settings did not verify against ${DESIRED} (see ${DRIVER_LOG})."
  if [[ "$(record_value skipped "${records}")" == "true" ]]; then
    PHASE_STATE[settings]="skipped:no setting differs"
  else
    PHASE_STATE[settings]="ran:changed ${SETTINGS_APPLIED[*]:-nothing}"
  fi
}

take_snapshot() {
  log "snapshot: stopping the host to copy its state"
  "${SCRIPT_DIR}/dev-stop.sh" >>"${EVIDENCE}/dev-start.log" 2>&1 || die 1 "dev-stop.sh failed before the snapshot."
  rm -rf -- "${LAB}/snapshot"
  mkdir -p "${LAB}/snapshot/AppHost.data"
  cp -a "${APPHOST_DATA}/." "${LAB}/snapshot/AppHost.data/"
  [[ ! -f "${CLIENT_DIR}/node-settings.json" ]] || cp -a "${CLIENT_DIR}/node-settings.json" "${LAB}/snapshot/"
  [[ ! -d "${CLIENT_DIR}/dp-keys" ]] || cp -a "${CLIENT_DIR}/dp-keys" "${LAB}/snapshot/dp-keys"
  # The key is written last: a snapshot interrupted mid-copy has no key and is never restored.
  printf '%s\n' "${SNAPSHOT_KEY}" >"${LAB}/snapshot/key"
  SNAPSHOT="saved"
  start_host
  discover_host
  authenticate
}

# The profile's modelSource names the PROFILE's model only. For a --model override the source comes from the pinned
# model-matrix manifest (matched by product model name); without a match nothing is downloaded (exit 2), so a large
# unrelated GGUF is never fetched under another model's name.
resolve_download_source() {
  if [[ "${MODEL}" != "${PROFILE_MODEL}" ]]; then
    local found
    found="$(python3 -c "
import json, sys
name = sys.argv[2]
for entry in json.load(open(sys.argv[1], encoding='utf-8')).get('models', []):
    if entry.get('modelName') == name:
        print(f\"{entry.get('repo') or str()}\\t{entry.get('file') or str()}\")
        break
" "${SCRIPT_DIR}/model-matrix/models.json" "${MODEL}" 2>/dev/null || true)"
    SOURCE_REPO="${found%%$'\t'*}"
    SOURCE_FILE="${found#*$'\t'}"
    [[ -n "${found}" && -n "${SOURCE_REPO}" && -n "${SOURCE_FILE}" ]] \
      || die 2 "model ${MODEL} is not installed and no download source is known for it: the profile's modelSource names ${PROFILE_MODEL:-no model} and scripts/model-matrix/models.json has no entry with that modelName."
  fi
  [[ -n "${SOURCE_REPO}" && -n "${SOURCE_FILE}" ]] \
    || die 2 "model ${MODEL} is not installed and profile ${PROFILE} has no modelSource {repo, file} to download it from."
}

ensure_model() {
  node_drive model-ensure --model "${MODEL}" --eject-others
}

phase_model() {
  local records downloaded="false"
  records="$(ensure_model)" || die 1 "model-ensure failed (see ${DRIVER_LOG})."
  if [[ "$(record_value installed "${records}")" != "true" ]]; then
    local provider_error
    provider_error="$(record_value providerError "${records}")"
    [[ -z "${provider_error}" ]] \
      || log "the node lists no GGUF at all (${provider_error}); a models dir of symlinks is rejected, use hard links or copies."
    [[ "${DOWNLOAD}" == "true" ]] || die 2 "model ${MODEL} is not installed; re-run with --download to install it."
    resolve_download_source
    log "downloading ${SOURCE_REPO}/${SOURCE_FILE} through the node"
    records="$(node_drive download --model "${MODEL}" --repo "${SOURCE_REPO}" --file "${SOURCE_FILE}" \
      --email "${EMAIL}" --password "${PASSWORD}")" \
      || die 1 "the download failed (see ${DRIVER_LOG})."
    # A download longer than the access token's lifetime renews it inside the driver; keep the new token.
    local renewed
    renewed="$(record_value token "${records}")"
    if [[ -n "${renewed}" ]]; then
      TOKEN="${renewed}"
      (umask 077 && printf '%s\n' "${TOKEN}" >"${TOKEN_FILE}")
    fi
    [[ "$(record_value downloaded "${records}")" == "true" ]] || die 1 "the download of ${MODEL} did not complete."
    records="$(ensure_model)" || die 1 "model-ensure failed after the download (see ${DRIVER_LOG})."
    [[ "$(record_value installed "${records}")" == "true" ]] || die 1 "model ${MODEL} is still not installed after the download."
    downloaded="true"
  fi
  MODEL_RESIDENT="$(record_value resident "${records}")"
  MODEL_RESIDENT="${MODEL_RESIDENT:-false}"
  local ejected eject_failed
  eject_failed="$(record_values ejectFailed "${records}" | paste -sd, -)"
  [[ -z "${eject_failed}" ]] \
    || die 1 "could not eject ${eject_failed}: the previous model is still serving a turn; let it finish or stop it, then re-run."
  ejected="$(record_values ejected "${records}" | paste -sd, -)"
  [[ "$(record_value selected "${records}")" == "${MODEL}" ]] \
    || die 1 "models/select answered '$(record_value selected "${records}")', not ${MODEL}."
  local changes=()
  [[ "$(record_value previouslySelected "${records}")" == "${MODEL}" ]] || changes+=(selected)
  [[ "${downloaded}" == "false" ]] || changes+=(downloaded)
  [[ -z "${ejected}" ]] || changes+=("ejected=${ejected}")
  if [[ "${#changes[@]}" -eq 0 ]]; then
    PHASE_STATE[model]="skipped:already installed and selected, nothing to eject, resident=${MODEL_RESIDENT}"
  else
    PHASE_STATE[model]="ran:${changes[*]} resident=${MODEL_RESIDENT}"
  fi
}

phase_first_turn() {
  local records
  records="$(node_drive first-turn --model "${MODEL}" --prompt "${FIRST_TURN_PROMPT}")" \
    || die 1 "first-turn failed (see ${DRIVER_LOG})."
  MODEL_RESIDENT="$(record_value resident "${records}")"
  MODEL_RESIDENT="${MODEL_RESIDENT:-false}"
  EXTRA_SETS+=(--set "conversationId=$(record_value conversationId "${records}")"
    --set "answerChars=$(record_value answerChars "${records}")")
  PHASE_STATE[first-turn]="ran:conversation $(record_value conversationId "${records}")"
}

stopped="false"
for phase in "${PHASES[@]}"; do
  if [[ "${stopped}" == "true" ]]; then
    PHASE_STATE[${phase}]="stopped:--until ${UNTIL}"
    continue
  fi
  "phase_${phase//-/_}"
  if [[ "${phase}" == "settings" && "${SNAPSHOT_REQ}" == "true" ]]; then take_snapshot; fi
  if [[ "${phase}" == "${UNTIL}" ]]; then stopped="true"; fi
done

phase_args=()
for phase in "${PHASES[@]}"; do phase_args+=(--phase "${phase}=${PHASE_STATE[${phase}]}"); done
runtime_json="$(python3 -c 'import json, os; print(json.dumps({"serverPath": os.environ.get("XE_LLAMACPP_SERVER_PATH"), "variant": os.environ.get("XE_LLAMACPP_VARIANT")}))')"
settings_json="$(python3 -c 'import json, sys; print(json.dumps(sys.argv[1:]))' ${SETTINGS_APPLIED[@]+"${SETTINGS_APPLIED[@]}"})"
host_pids=()
[[ -z "${HOST_PID}" ]] || host_pids=(--set "hostPids=[${HOST_PID}]")
manifest="$(run_driver python3 "${LAB_DRIVER}" manifest --out "${LAB}/manifest.json" \
  --set "baseUrl=${BASE_URL}" --set "viteUrl=${VITE_URL}" --set "tokenPath=${TOKEN_FILE}" \
  --set "apiHelper=${SCRIPT_DIR}/lab-api.sh" --set "evidenceDir=${EVIDENCE}" --set "profile=${PROFILE}" \
  --set "model=${MODEL}" --set "modelResident=${MODEL_RESIDENT}" --set "settingsApplied=${settings_json}" \
  --set "snapshot=${SNAPSHOT}" --set "runtime=${runtime_json}" --set "stoppedAt=${UNTIL}" ${host_pids[@]+"${host_pids[@]}"} \
  ${EXTRA_SETS[@]+"${EXTRA_SETS[@]}"} "${phase_args[@]}")" || die 1 "writing the manifest failed (see ${DRIVER_LOG})."

summary=""
for phase in "${PHASES[@]}"; do summary+=" ${phase}=${PHASE_STATE[${phase}]%%:*}"; done
log "phases:${summary}"
[[ "${JSON}" == "true" ]] || echo "=== Handoff manifest (${LAB}/manifest.json) ==="
printf '%s\n' "${manifest}"
