#!/usr/bin/env bash
# Reliably stop the XE-Local-AI-Engine Aspire dev stack on WSL/Linux.
#
# WHY: 'aspire stop' is a confirmed no-op on this topology.  DCP and all
# child processes are reparented under the user session and detached from
# the AppHost PPID subtree, so 'aspire stop' (which only signals the
# AppHost) cannot reach them.  See microsoft/aspire#15806, #8919, #10377.
#
# TOPOLOGY (verified live):
#   aspire CLI  ──┐  SESS=S
#   AppHost     ──┘  SESS=S   (child of aspire CLI)
#   dcp         ──┐  SESS=S   (sibling — NOT under AppHost)
#     dotnet run      SESS=S
#       Client        SESS=S
#   vite/node         SESS=S   (under dcp)
#   llama-server      own PGID, exe under ~/.local/share/…/llama.cpp/
#
# Strategy: collect PIDs by (a) AppHost + its PPID-descendants, (b) same
# session-id + name allowlist (catches dcp subtree), (c) our llama-server
# by exe path.  SIGTERM all; wait 3 s; SIGKILL survivors.
#
# Usage:
#   scripts/dev-stop.sh [--all] [--dry-run] [--help]

set -euo pipefail

readonly APP_NAME="XE-Local-AI-Engine"
readonly LLAMA_DIR="${HOME}/.local/share/XE-Local-AI-Engine/llama.cpp"

DRY_RUN=false
ALL=false

for _arg in "$@"; do
  case "$_arg" in
    --dry-run) DRY_RUN=true ;;
    --all) ALL=true ;;
    --help|-h)
      cat <<'USAGE'
Usage: dev-stop.sh [--all] [--dry-run] [--help]

Reliably stops the XE-Local-AI-Engine Aspire dev stack on WSL/Linux.
'aspire stop' returns "stopped successfully" in ~0.1 s but kills nothing.

Options:
  --all       Stop EVERY running XE AppHost stack (any session/worktree),
              not just the first one found. Use when the main checkout and
              a worktree each have an AppHost up.
  --dry-run   Print the kill list without sending any signals.
  --help      Show this help and exit.
USAGE
      exit 0 ;;
    *) printf 'dev-stop: unknown argument: %s\n' "$_arg" >&2; exit 1 ;;
  esac
done

log()  { printf '[dev-stop] %s\n' "$*"; }
warn() { printf '[dev-stop] WARN: %s\n' "$*" >&2; }

# ── Process introspection (each returns empty string on failure; never exits) ─

pid_exists() { [[ -d "/proc/$1" ]]; }

comm_of() {
  ps -o comm= -p "$1" 2>/dev/null | head -1 || true
}

sid_of() {
  ps -o sid= -p "$1" 2>/dev/null | tr -d ' ' || true
}

pgid_of() {
  ps -o pgid= -p "$1" 2>/dev/null | tr -d ' ' || true
}

ppid_of() {
  ps -o ppid= -p "$1" 2>/dev/null | tr -d ' ' || true
}

cmdline_of() {
  tr '\0' ' ' < "/proc/$1/cmdline" 2>/dev/null | head -c 300 || true
}

exe_of() {
  readlink "/proc/$1/exe" 2>/dev/null || true
}

# Recursively print all descendant PIDs of $1 via PPID walk.
descendants_of() {
  local parent="$1"
  local children
  children=$(ps -e -o ppid=,pid= 2>/dev/null \
             | awk -v p="$parent" '$1==p {print $2}' || true)
  local child
  for child in $children; do
    [[ "$child" =~ ^[0-9]+$ ]] || continue
    echo "$child"
    descendants_of "$child"
  done
}

# ── Allowlist: process names that belong to this stack ────────────────────────
# Comm names (kernel truncates to 15 chars on Linux — keep entries ≤ 15).
ALLOWED_COMMS=( dcp dotnet node npm vite aspire )
# Cmdline fragments (untruncated; catches project names in argv).
ALLOWED_CMDLINE_FRAGS=( "XE-Local-AI-Engine" "client-react" "vite" )

is_our_process() {
  local pid="$1" pcomm cmd c f
  pcomm=$(comm_of "$pid")
  for c in "${ALLOWED_COMMS[@]}"; do
    [[ "$pcomm" == "$c" ]] && return 0
  done
  cmd=$(cmdline_of "$pid")
  for f in "${ALLOWED_CMDLINE_FRAGS[@]}"; do
    [[ "$cmd" == *"$f"* ]] && return 0
  done
  return 1
}

# ── Protected PIDs: current shell's ancestor chain — never kill these ─────────
PROTECTED_PIDS=()
_walk="$$"
for _step in $(seq 1 30); do
  [[ -n "$_walk" && "$_walk" != "0" && "$_walk" != "1" ]] || break
  PROTECTED_PIDS+=("$_walk")
  _walk=$(ppid_of "$_walk")
done
unset _walk _step

is_protected() {
  local pid="$1" pp
  for pp in "${PROTECTED_PIDS[@]}"; do [[ "$pid" == "$pp" ]] && return 0; done
  return 1
}

# ── Step 1: Locate the AppHost(s) ─────────────────────────────────────────────

# Scan /proc cmdlines for every running AppHost (no aspire dependency).
all_apphost_pids() {
  local cf pid
  for cf in /proc/[0-9]*/cmdline; do
    [[ -f "$cf" ]] || continue
    if grep -qF "${APP_NAME}.AppHost" "$cf" 2>/dev/null; then
      pid="${cf%/cmdline}"; pid="${pid##*/proc/}"
      # Only the actual AppHost binary/dll run — not e.g. an editor with the
      # path in argv: require the comm to be on the allowlist.
      is_our_process "$pid" && echo "$pid"
    fi
  done
}

APPHOST_PIDS=()

if $ALL; then
  log "Scanning /proc for ALL running ${APP_NAME} AppHosts (--all)..."
  while IFS= read -r _apid; do
    [[ "$_apid" =~ ^[0-9]+$ ]] && APPHOST_PIDS+=("$_apid")
  done < <(all_apphost_pids | sort -un)
  unset _apid
  if [[ ${#APPHOST_PIDS[@]} -eq 0 ]]; then
    log "No running ${APP_NAME} AppHost found — nothing to stop."
    exit 0
  fi
  log "Found ${#APPHOST_PIDS[@]} AppHost(s): ${APPHOST_PIDS[*]}"
else

log "Querying 'aspire ps' for a running ${APP_NAME} AppHost..."

APPHOST_PID=""

# Attempt 1: JSON format (aspire ≥ 9.x)
if aspire_out=$(aspire ps --format Json 2>/dev/null) && [[ -n "$aspire_out" ]]; then
  if command -v python3 &>/dev/null; then
    APPHOST_PID=$(python3 -c "
import sys, json
try:
    data = json.loads(sys.stdin.read())
except Exception:
    sys.exit(0)
items = data if isinstance(data, list) else []
for k in ('apps', 'resources', 'instances'):
    if isinstance(data, dict) and k in data:
        items = data[k]; break
for item in items:
    name = str(item.get('Name') or item.get('name') or '')
    pid  = int(item.get('Pid')  or item.get('pid')  or 0)
    if '${APP_NAME}' in name and pid > 0:
        print(pid); break
" <<< "$aspire_out" 2>/dev/null || true)
  fi
fi

# Attempt 2: plain text aspire ps — PID is usually the first long number on
# the app line (field positions vary by aspire version).
if [[ -z "$APPHOST_PID" ]]; then
  aspire_text=$(aspire ps 2>/dev/null || true)
  APPHOST_PID=$(printf '%s\n' "$aspire_text" \
    | grep -E "$APP_NAME" \
    | grep -Eo '\b[0-9]{4,7}\b' \
    | head -1 || true)
fi

# Attempt 3: scan /proc cmdlines directly (no aspire dependency).
if [[ -z "$APPHOST_PID" ]]; then
  for _cf in /proc/[0-9]*/cmdline; do
    [[ -f "$_cf" ]] || continue
    if grep -qF "${APP_NAME}.AppHost" "$_cf" 2>/dev/null; then
      APPHOST_PID="${_cf%/cmdline}"; APPHOST_PID="${APPHOST_PID##*/proc/}"
      break
    fi
  done
  unset _cf
fi

if [[ -z "$APPHOST_PID" ]] || ! pid_exists "$APPHOST_PID"; then
  log "No running ${APP_NAME} AppHost found — nothing to stop."
  exit 0
fi

APPHOST_PIDS=("$APPHOST_PID")
fi  # $ALL

# ── Step 2: Build the kill list ───────────────────────────────────────────────
declare -A KILL_LIST   # [pid]=reason

add_pid() {
  local pid="$1" reason="$2"
  [[ "$pid" =~ ^[0-9]+$ && "$pid" != "0" && "$pid" != "1" ]] || return 0
  pid_exists "$pid" || return 0
  if is_protected "$pid"; then
    warn "PID ${pid} ($(comm_of "$pid")) is in current shell chain — skipping."
    return 0
  fi
  KILL_LIST["$pid"]="$reason"
}

# (a) + (b) run once per located AppHost; the KILL_LIST map dedupes overlap.
for _apppid in "${APPHOST_PIDS[@]}"; do
  _appsid=$(sid_of "$_apppid")
  log "AppHost: PID=${_apppid}  SID=${_appsid}  comm=$(comm_of "$_apppid")"
  [[ -n "$_appsid" ]] \
    || warn "Could not read AppHost ${_apppid} session ID; session-based scan skipped for it."

  # a) AppHost itself and every PPID-descendant that matches the allowlist.
  add_pid "$_apppid" "AppHost"
  for _dpid in $(descendants_of "$_apppid"); do
    if is_our_process "$_dpid"; then
      add_pid "$_dpid" "AppHost-descendant"
    else
      warn "PID ${_dpid} ($(comm_of "$_dpid")) is AppHost descendant but not on allowlist — skipping."
    fi
  done
  unset _dpid

  # b) Same-session processes that match the allowlist.
  #    Catches dcp + its subtree, which is a session sibling of AppHost,
  #    not a PPID-descendant — so it isn't covered by (a).
  if [[ -n "$_appsid" ]]; then
    while IFS=' ' read -r _spid _ssid; do
      [[ "$_spid" =~ ^[0-9]+$ && "$_ssid" == "$_appsid" ]] || continue
      is_our_process "$_spid" || continue
      add_pid "$_spid" "same-session(sid=${_appsid})"
    done < <(ps -e -o pid=,sid= 2>/dev/null || true)
  fi
done
unset _apppid _appsid

# c) Our llama-server instances — matched strictly by exe path under LLAMA_DIR
#    so Ollama's /usr/lib/ollama/llama-server is never touched.
for _elink in /proc/[0-9]*/exe; do
  [[ -L "$_elink" ]] || continue
  _lpid="${_elink%/exe}"; _lpid="${_lpid##*/proc/}"
  [[ "$_lpid" =~ ^[0-9]+$ ]] || continue
  _exe=$(exe_of "$_lpid")
  [[ "$_exe" == "${LLAMA_DIR}"* ]] || continue
  add_pid "$_lpid" "llama-server(${_exe#"${LLAMA_DIR}/"})"
done
unset _elink _lpid _exe

# ── Step 3: Report kill list ──────────────────────────────────────────────────
if [[ ${#KILL_LIST[@]} -eq 0 ]]; then
  log "Kill list is empty — nothing to stop."
  exit 0
fi

log "Processes to stop (${#KILL_LIST[@]} total):"
printf '  %-8s  %-26s  %s\n' 'PID' 'COMM' 'REASON'
printf '  %-8s  %-26s  %s\n' '---' '----' '------'
for _pid in $(printf '%s\n' "${!KILL_LIST[@]}" | sort -n); do
  printf '  %-8s  %-26s  %s\n' "$_pid" "$(comm_of "$_pid")" "${KILL_LIST[$_pid]}"
done
unset _pid

if $DRY_RUN; then
  log "--dry-run: not sending any signals."
  exit 0
fi

# ── Step 4: SIGTERM → 3 s grace → SIGKILL survivors ──────────────────────────
log "Sending SIGTERM to all ${#KILL_LIST[@]} processes..."
for _pid in "${!KILL_LIST[@]}"; do
  kill -TERM "$_pid" 2>/dev/null || true
done
unset _pid

log "Waiting 3 s for graceful shutdown..."
sleep 3

SURVIVORS=()
_term_stopped=0
for _pid in "${!KILL_LIST[@]}"; do
  if pid_exists "$_pid"; then
    SURVIVORS+=("$_pid")
  else
    _term_stopped=$(( _term_stopped + 1 ))
  fi
done
unset _pid
log "SIGTERM stopped ${_term_stopped}; ${#SURVIVORS[@]} survivor(s) need SIGKILL."

STILL_ALIVE=()
if [[ ${#SURVIVORS[@]} -gt 0 ]]; then
  for _pid in "${SURVIVORS[@]}"; do
    _reason="${KILL_LIST[$_pid]:-}"
    if [[ "$_reason" == "llama-server"* ]]; then
      # llama-server is typically a session/group leader; kill the whole group.
      _pgid=$(pgid_of "$_pid")
      if [[ -n "$_pgid" && "$_pgid" != "0" ]]; then
        log "  SIGKILL process group -${_pgid}  (llama-server PID ${_pid})"
        kill -KILL -- "-${_pgid}" 2>/dev/null || kill -KILL "$_pid" 2>/dev/null || true
      else
        kill -KILL "$_pid" 2>/dev/null || true
      fi
    else
      log "  SIGKILL PID ${_pid}  ($(comm_of "$_pid"))"
      kill -KILL "$_pid" 2>/dev/null || true
    fi
  done
  unset _pid _reason _pgid

  sleep 1

  for _pid in "${SURVIVORS[@]}"; do
    pid_exists "$_pid" && STILL_ALIVE+=("$_pid") || true
  done
  unset _pid

  if [[ ${#STILL_ALIVE[@]} -gt 0 ]]; then
    warn "These PIDs survived SIGKILL (zombie / kernel thread?):"
    for _pid in "${STILL_ALIVE[@]}"; do
      warn "  PID ${_pid}  comm=$(comm_of "$_pid")"
    done
    unset _pid
  fi
fi

_final_stopped=$(( ${#KILL_LIST[@]} - ${#STILL_ALIVE[@]} ))
log "Done: ${_final_stopped} / ${#KILL_LIST[@]} processes stopped."
