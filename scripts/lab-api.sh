#!/usr/bin/env bash
# lab-api.sh — one logged call against the lab node that scripts/lab-up.sh prepared.
#
# Usage:
#   scripts/lab-api.sh [--profile NAME] METHOD PATH [JSON]
#   scripts/lab-api.sh [--profile NAME] settle CONVERSATION_ID [TIMEOUT_SEC]
#
#   PATH is relative to /api/local/v1 (e.g. `chat/conversations`); one holding curl glob syntax ({ } [ ]) is refused,
#   so the route the log classifies is the one sent (percent-encode those characters). The base URL comes from
#   .tmp/lab/<profile>/manifest.json, the token from .tmp/lab/<profile>/token. On HTTP 401 it logs in once more
#   (LAB_EMAIL / LAB_PASSWORD, default admin@localhost.test / !Demo1234567), rewrites the token and retries once.
#   Prints the response body, then `[HTTP <code>]`. Every call is appended to .tmp/lab/<profile>/evidence/http.log:
#   a credential route's bodies (auth, MCP, cloud settings, …: scripts/gpu-smoke-driver.py) are replaced by a marker,
#   other bodies have passwords, tokens and keys redacted, every query value (path and bodies), every URL userinfo and
#   every credential path segment (a skill import preview token) is masked, and a JSON-shaped body or nested string
#   that does not parse, or a string holding a link whose spelling hides a credential segment, is replaced by a marker.
#   So is a body that is not text (an image) and a non-JSON body holding a credential value (an MCP, integration or
#   proxy key, a PEM block) anywhere; a JSON string holding one is redacted.
#
#   settle polls GET chat/conversations/<id> every 3 s until the last message's status is terminal
#   (Completed, Failed, Cancelled, Interrupted) and prints `status | role | error | content`; default timeout 600 s.
#   Only the last poll is logged.
#
#   TLS verification is disabled, so a base URL that is not loopback is refused. Proxy variables (http_proxy,
#   https_proxy, all_proxy) are ignored: every call, its token and the re-login password go straight to the node.
#
# Exit codes: 0 — a response was received (any HTTP status) or the conversation settled; 1 — transport failure,
# re-login failure or settle timeout; 2 — usage error (a METHOD other than letters, a PATH with glob syntax), or no
# lab manifest/token.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
if ! ROOT="$(git -C "${SCRIPT_DIR}" rev-parse --show-toplevel 2>/dev/null)"; then
  ROOT="$(cd "${SCRIPT_DIR}/.." && pwd -P)"
fi
usage() { sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//; $d'; }
die() { local code="$1"; shift; echo "[lab-api] $*" >&2; exit "${code}"; }

PROFILE="default"
if [[ "${1:-}" == "--profile" ]]; then
  PROFILE="${2:-}"
  shift 2 || true
fi
case "${1:-}" in --help|-h) usage; exit 0 ;; esac
[[ $# -ge 2 && "${PROFILE}" =~ ^[A-Za-z0-9._-]+$ ]] || { usage >&2; exit 2; }

LAB="${ROOT}/.tmp/lab/${PROFILE}"
TOKEN_FILE="${LAB}/token"
HTTP_LOG="${LAB}/evidence/http.log"
[[ -f "${LAB}/manifest.json" && -f "${TOKEN_FILE}" ]] || die 2 "no lab at ${LAB}; run scripts/lab-up.sh first."
BASE_URL="$(python3 -c '
import json, sys
from urllib.parse import urlsplit
base = json.load(open(sys.argv[1], encoding="utf-8")).get("baseUrl") or ""
if urlsplit(base).hostname not in ("localhost", "127.0.0.1", "::1"):
    sys.exit(f"[lab-api] refusing non-loopback base URL {base!r}: TLS verification is disabled")
print(base.rstrip("/"))
' "${LAB}/manifest.json")" || exit 2
mkdir -p "${LAB}/evidence"
RESP="$(mktemp "${LAB}/.resp.XXXXXX")"
HDR="$(mktemp "${LAB}/.hdr.XXXXXX")"
trap 'rm -f -- "${RESP}" "${HDR}"' EXIT

# redact FUNCTION [ARG...]: stdin through the driver's FUNCTION(text, ARG...), so the shell and the Python recorder share
# one rule. `redact redact PATH` replaces the whole body of a credential route (auth, MCP, cloud settings, …) by a
# marker; any other JSON body has its credential-like fields blanked at any depth (a regex over quoted strings stops at
# an escaped quote and leaks the rest), and a non-JSON body keeps its text with every query value and userinfo masked,
# unless it is not text or holds a credential value (a key, a PEM block) anywhere: then it is a marker.
# redact_path masks everything after the logged path's first `?` (a hub path carries the SignalR connection token).
redact() {
  # The program goes in -c so stdin stays the text being redacted. Bytes that are not UTF-8 (an image) decode to
  # replacement characters, which the driver omits as a body that is not text, whatever the locale.
  python3 -c '
import importlib.util, sys
spec = importlib.util.spec_from_file_location("lab_driver", sys.argv[1])
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
text = sys.stdin.buffer.read().decode("utf-8", "replace")
sys.stdout.buffer.write(getattr(module, sys.argv[2])(text, *sys.argv[3:]).encode("utf-8"))
' "${SCRIPT_DIR}/lab-driver.py" "$@"
}

# call METHOD PATH [BODY] [MAX_TIME_SEC]: writes the body to ${RESP} and prints the HTTP code, 000 on a transport
# failure — including one curl reports AFTER the status line (a truncated or reset body), so a 200 with an incomplete
# body is never a success. Every request is bounded (connect 10 s, total MAX_TIME, default 120 s). The token goes
# through a 0600 header file, never argv; Content-Type only with a body (a GET carrying it gets a 400).
# curl sends exactly what log_call records: no .curlrc (-q), one literal URL (--globoff), the body as given, never a
# file named by a leading @ (--data-raw), straight to the loopback node whatever proxy variables are set (--noproxy).
call() {
  local code rc=0
  local args=(-q -sk --globoff -o "${RESP}" -w '%{http_code}' --connect-timeout 10 --max-time "${4:-120}" -X "$1"
    --noproxy '*' "${BASE_URL}/api/local/v1/${2#/}")
  [[ -z "${3:-}" ]] || args+=(-H 'Content-Type: application/json' --data-raw "$3")
  printf 'Authorization: Bearer %s\n' "$(<"${TOKEN_FILE}")" >"${HDR}"
  : >"${RESP}"  # never let a previous response survive a failed transfer
  code="$(curl "${args[@]}" -H "@${HDR}")" || rc=$?
  if [[ "${rc}" -ne 0 ]]; then printf '000'; else printf '%s' "${code}"; fi
}

# Seconds a request may take now: the remaining settle budget when DEADLINE is set (at most $1, default 120), else $1.
budget() {
  local cap="${1:-120}" left
  [[ -n "${DEADLINE:-}" ]] || { printf '%s' "${cap}"; return; }
  left=$((DEADLINE - SECONDS)); [[ "${left}" -ge 1 ]] || left=1
  [[ "${left}" -le "${cap}" ]] && printf '%s' "${left}" || printf '%s' "${cap}"
}

# relogin MAX_TIME_SEC: the whole login subprocess is bounded by wall clock (it makes up to three requests, and the
# driver's --timeout is only a per-request inactivity bound), so a slow or trickling node cannot hold a settle past
# its deadline through the authentication detour.
relogin() {
  local records token status=0
  records="$(timeout "${1:-120}s" python3 "${SCRIPT_DIR}/gpu-smoke-driver.py" --base-url "${BASE_URL}" \
    --timeout "${1:-120}" auth --email "${LAB_EMAIL:-admin@localhost.test}" --password "${LAB_PASSWORD:-!Demo1234567}")" \
    || status=$?
  [[ "${status}" -ne 124 ]] || die 1 "re-login after 401 timed out after ${1:-120}s."
  [[ "${status}" -eq 0 ]] || die 1 "re-login after 401 failed."
  token="$(awk -F'\t' '$1 == "token" { print $2; exit }' <<<"${records}")"
  [[ -n "${token}" ]] || die 1 "re-login printed no token."
  (umask 077 && printf '%s\n' "${token}" >"${TOKEN_FILE}")
}

# call_authed METHOD PATH [BODY] [CAP]: one call with a single 401 re-login. Every leg (first call, login, retry) is
# bounded by budget(CAP), recomputed before it starts, so under a DEADLINE the whole detour fits the remaining time.
call_authed() {
  local code cap="${4:-120}"
  code="$(call "$1" "$2" "${3:-}" "$(budget "${cap}")")"
  if [[ "${code}" == "401" ]]; then
    # Under a deadline that has already passed, the 401 is returned as is: no login, no retry.
    if [[ -n "${DEADLINE:-}" && "${SECONDS}" -ge "${DEADLINE}" ]]; then printf '%s' "${code}"; return; fi
    relogin "$(budget "${cap}")"
    code="$(call "$1" "$2" "${3:-}" "$(budget "${cap}")")"
  fi
  printf '%s' "${code}"
}

# Every logged part is redacted on its own: the path by redact_path, each body by redact (which masks the query values
# of a body that echoes a hub URL).
log_call() {
  {
    echo "=== $(date -u +%Y-%m-%dT%H:%M:%SZ) $1 $(printf '%s' "$2" | redact redact_path)"
    [[ -z "${3:-}" ]] || printf '>> %s\n' "$(redact redact "$2" <<<"$3")"  # redact first: the prefix makes it non-JSON
    redact redact "$2" <"${RESP}"
    echo
    echo "[HTTP $4]"
  } >>"${HTTP_LOG}"
}

# literal_path PATH: refuses (exit 2, before anything is sent or logged) a path holding curl glob syntax. --globoff
# already sends it literally; the refusal also keeps one invocation to one route whatever curl's globbing default.
literal_path() {
  [[ "$1" != *[][{}]* ]] || die 2 "PATH holds curl glob syntax ({ } [ ]); percent-encode it (%7B %7D %5B %5D)."
}

if [[ "$1" == "settle" ]]; then
  conversation="$2"
  literal_path "${conversation}"
  timeout_sec="${3:-600}"
  [[ "${timeout_sec}" =~ ^[0-9]+$ ]] || die 2 "TIMEOUT_SEC must be an integer"
  deadline=$((SECONDS + timeout_sec))
  DEADLINE="${deadline}"
  polls=0
  while :; do
    # Each poll, and any re-login and retry inside it, is bounded by what is left of the settle budget (at most 60 s
    # per leg), so a node that accepts the connection and then stalls cannot push the loop past its deadline.
    code="$(call_authed GET "chat/conversations/${conversation}" "" 60)"
    polls=$((polls + 1))
    line="$(python3 -c '
import json, sys
try:
    data = json.load(open(sys.argv[1], encoding="utf-8"))
except (OSError, ValueError):
    print("unreadable | | | ")
    raise SystemExit
items = (data.get("messages") or data.get("items") or []) if isinstance(data, dict) else []
last = items[-1] if items else {}
fields = (last.get("status"), last.get("role"), (last.get("error") or "")[:200],
          (last.get("content") or "")[:160].replace("\n", " "))
print(" | ".join(str(f) for f in fields))
' "${RESP}")"
    # A terminal status counts only on a successful exchange: a failed transfer (000) or an error status is a failed
    # poll, however readable the body is.
    if [[ "${code}" == 2* ]]; then
      case "${line,,}" in
        completed*|failed*|cancelled*|interrupted*)
          log_call GET "chat/conversations/${conversation} (settle, poll ${polls})" "" "${code}"
          echo "${line}"
          exit 0 ;;
      esac
    else
      line="http ${code} | | | "
    fi
    if [[ "${SECONDS}" -ge "${deadline}" ]]; then
      log_call GET "chat/conversations/${conversation} (settle TIMEOUT, poll ${polls})" "" "${code}"
      echo "TIMEOUT last=${line} [HTTP ${code}]"
      exit 1
    fi
    sleep 3
  done
fi

method="${1^^}"
path="$2"
body="${3:-}"
# The method goes on the log line as is: a token, never whitespace or a line break.
[[ "${method}" =~ ^[A-Z]+$ ]] || die 2 "METHOD must be letters only."
literal_path "${path}"
code="$(call_authed "${method}" "${path}" "${body}")"
log_call "${method}" "${path}" "${body}" "${code}"
cat "${RESP}"
echo
echo "[HTTP ${code}]"
[[ "${code}" != "000" ]] || exit 1
