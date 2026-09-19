#!/usr/bin/env bash
# Compare the committed frontend client with a freshly started desktop backend.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
PROJECT_ROOT="$(git -C "${SCRIPT_DIR}" rev-parse --show-toplevel)"
CLIENT_PROJECT="${PROJECT_ROOT}/XE-Local-AI-Engine.Client/XE-Local-AI-Engine.Client.csproj"
CLIENT_RELEASE_ROOT="${OPENAPI_LIVE_CLIENT_RELEASE_ROOT:-${PROJECT_ROOT}/XE-Local-AI-Engine.Client/bin/Release}"
FRONTEND_DIR="${PROJECT_ROOT}/XE-Local-AI-Engine.Client.React"
BUILD_LOCK="${PROJECT_ROOT}/scripts/with-build-lock.sh"
ASSEMBLY_GUARD="${PROJECT_ROOT}/scripts/assembly-guard.sh"
TIMEOUT_SECONDS="${OPENAPI_LIVE_TIMEOUT_SECONDS:-120}"

# The live server reads Release assemblies for the duration of the contract check. Hold the same
# repository-wide lock as builds/tests; the wrapper is re-entrant through XE_BUILD_LOCK_HELD.
if [[ -z "${XE_BUILD_LOCK_HELD:-}" ]]; then
  exec "${BUILD_LOCK}" -- "${BASH_SOURCE[0]}" "$@"
fi

for tool in dotnet pnpm python3 setsid; do
  command -v "${tool}" >/dev/null 2>&1 || { echo "[openapi-live] Missing required tool: ${tool}" >&2; exit 2; }
done
[[ -f "${CLIENT_PROJECT}" ]] || { echo "[openapi-live] Client project not found: ${CLIENT_PROJECT}" >&2; exit 2; }

temp_root="$(mktemp -d)"
chmod 700 "${temp_root}"
mkdir -p "${temp_root}/bin" "${temp_root}/home"
cat >"${temp_root}/bin/xdg-open" <<'EOF'
#!/usr/bin/env sh
exit 0
EOF
chmod 700 "${temp_root}/bin/xdg-open"
backend_log="${temp_root}/backend.log"
backend_pid=""
guard_state="${temp_root}/assembly-guard.state"
guard_started="false"

print_sanitized_tail() {
  echo "[openapi-live] Sanitized backend log tail:" >&2
  tail -n 40 "${backend_log}" \
    | sed -E \
        -e '/secret|token|password|connectionstring|api[-_]?key/Ic\[redacted sensitive log line]' \
        -e 's#(https?://[^?[:space:]]+)\?[^[:space:]]+#\1?[redacted]#g' >&2
}

cleanup() {
  local status=$?
  if [[ "${backend_pid}" =~ ^[0-9]+$ ]]; then
    kill -TERM -- "-${backend_pid}" 2>/dev/null || true
    for _ in $(seq 1 20); do
      kill -0 "${backend_pid}" 2>/dev/null || break
      sleep 0.25
    done
    kill -KILL -- "-${backend_pid}" 2>/dev/null || true
    wait "${backend_pid}" 2>/dev/null || true
  fi
  if [[ "${guard_started}" == "true" ]]; then
    local guard_status=0
    "${ASSEMBLY_GUARD}" verify "${guard_state}" || guard_status=$?
    if [[ "${guard_status}" -ne 0 ]]; then
      status="${guard_status}"
    fi
  fi
  rm -rf -- "${temp_root}"
  trap - EXIT
  exit "${status}"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

# The isolated HOME/XDG_DATA_HOME below hides a mise trust store and every mise tool install, so on a
# mise-managed box each shim on PATH aborts and the host dies before readiness — which reads as a broken spec
# endpoint, not as a toolchain problem. Forward the caller's values, else the real user paths when they exist.
# A box without mise has neither, and nothing is exported. See docs/agent-knowledge.md section 1.
mise_trusted_config_paths="${MISE_TRUSTED_CONFIG_PATHS:-}"
mise_data_dir="${MISE_DATA_DIR:-}"
# ${HOME:-} throughout: this script runs under set -u, and an unset HOME must skip the fallback rather than
# abort the whole check.
if [[ -z "${mise_trusted_config_paths}" && -f "${HOME:-}/.config/mise/config.toml" ]]; then
  mise_trusted_config_paths="${HOME:-}/.config/mise/config.toml"
fi
if [[ -z "${mise_data_dir}" && -d "${HOME:-}/.local/share/mise" ]]; then
  mise_data_dir="${HOME:-}/.local/share/mise"
fi

# A Release host defaults to Production, and Program maps the OpenAPI document only when !IsProduction(), so
# without this the spec fetch below answers 404 on every path and reads as total contract loss.
host_env=(
  "PATH=${temp_root}/bin:${PATH}"
  "XDG_DATA_HOME=${temp_root}/data"
  "HOME=${temp_root}/home"
  "ASPNETCORE_ENVIRONMENT=Development"
  "XE_LAUNCH_MODE=desktop"
  "FirstRunModel__Enabled=false"
)
if [[ -n "${mise_trusted_config_paths}" ]]; then
  host_env+=("MISE_TRUSTED_CONFIG_PATHS=${mise_trusted_config_paths}")
fi
if [[ -n "${mise_data_dir}" ]]; then
  host_env+=("MISE_DATA_DIR=${mise_data_dir}")
fi

echo "[openapi-live] Starting an isolated desktop backend (Release, --no-build)."
"${ASSEMBLY_GUARD}" snapshot "${guard_state}" --root "${CLIENT_RELEASE_ROOT}"
guard_started="true"
# -u first: env inherits the caller's environment, so a MISE_* variable the caller exported EMPTY would reach
# the host as an empty data directory / trust list. Dropped, then re-added above only when it has a value.
env -u MISE_TRUSTED_CONFIG_PATHS -u MISE_DATA_DIR "${host_env[@]}" \
  setsid dotnet run --project "${CLIENT_PROJECT}" --configuration Release --no-build -- --desktop \
  >"${backend_log}" 2>&1 &
backend_pid=$!

# DesktopBootstrap uses Environment.SpecialFolder.LocalApplicationData, which on Linux resolves from
# XDG_DATA_HOME and only falls back to ${HOME}/.local/share when it is unset. The isolated XDG_DATA_HOME
# above is therefore where DesktopPortStore's explicit file contract lands; the HOME isolation is a separate
# concern (see the comment above it). Pointing this at the HOME path made it dead code and left the log-grep
# fallback below as the only way the port was ever found.
port_file="${temp_root}/data/XE-Local-AI-Engine/desktop-port.txt"
base_url=""
for _ in $(seq 1 "$((TIMEOUT_SECONDS * 4))"); do
  if ! kill -0 "${backend_pid}" 2>/dev/null; then
    echo "[openapi-live] Backend exited before readiness." >&2
    print_sanitized_tail
    exit 1
  fi
  if [[ -s "${port_file}" ]]; then
    port="$(tr -cd '0-9' <"${port_file}")"
    if [[ "${port}" =~ ^[0-9]+$ ]] && (( port > 1024 && port < 65536 )); then
      base_url="http://127.0.0.1:${port}"
    fi
  fi
  if [[ -z "${base_url}" ]]; then
    logged_url="$(grep -Eo 'Opened the default browser at https?://127\.0\.0\.1:[0-9]+/' "${backend_log}" \
      | tail -n 1 | sed -E 's/^Opened the default browser at //; s#/$##' || true)"
    if [[ "${logged_url}" =~ ^https?://127\.0\.0\.1:[0-9]+$ ]]; then
      base_url="${logged_url}"
    fi
  fi
  if [[ -n "${base_url}" ]]; then
    if BASE_URL="${base_url}" python3 -c '
import os, urllib.request
with urllib.request.urlopen(os.environ["BASE_URL"] + "/health/live", timeout=1) as response:
    raise SystemExit(0 if response.status == 200 else 1)
' >/dev/null 2>&1; then
      break
    fi
  fi
  sleep 0.25
done

if [[ -z "${base_url}" ]]; then
  echo "[openapi-live] Timed out waiting for the desktop backend port." >&2
  print_sanitized_tail
  exit 1
fi
if ! BASE_URL="${base_url}" python3 -c '
import os, urllib.request
with urllib.request.urlopen(os.environ["BASE_URL"] + "/health/live", timeout=2) as response:
    raise SystemExit(0 if response.status == 200 else 1)
' >/dev/null 2>&1; then
  echo "[openapi-live] Backend did not become ready within ${TIMEOUT_SECONDS}s." >&2
  print_sanitized_tail
  exit 1
fi

echo "[openapi-live] Backend ready; comparing live OpenAPI contract."
cd "${FRONTEND_DIR}"
OPENAPI_SPEC_URL="${base_url}/openapi/local/v1/v1.json" pnpm openapi:check:live
echo "[openapi-live] PASS: live backend contract matches committed frontend artifacts."
