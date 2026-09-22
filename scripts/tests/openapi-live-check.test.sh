#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
TEMP_ROOT="$(mktemp -d)"
# Name the command that ended the run: under -e a failing check exits 1 with no output, which left one CI log with
# nothing but the runner's verdict. Only the exit trap reports, so the negative controls that expect a failure stay quiet.
failed_at=''
rc=0
trap 'failed_at="line ${LINENO}: ${BASH_COMMAND}"' ERR
trap 'rc=$?; rm -rf -- "${TEMP_ROOT}"; if [[ "${rc}" -ne 0 ]]; then echo "openapi-live-check.test.sh: FAILED (exit ${rc}) at ${failed_at}" >&2; fi' EXIT
mkdir -p "${TEMP_ROOT}/bin"
mkdir -p "${TEMP_ROOT}/release"
printf 'stable\n' >"${TEMP_ROOT}/release/fake.dll"

cat >"${TEMP_ROOT}/bin/dotnet" <<'FAKE_DOTNET'
#!/usr/bin/env bash
set -euo pipefail
[[ " $* " == *" run "* && " $* " == *" --no-build "* && " $* " == *" --desktop "* ]]
# A Release host defaults to Production, which maps no OpenAPI document; the checker must pin the environment
# or the spec fetch 404s on every path.
[[ "${ASPNETCORE_ENVIRONMENT:-}" == "Development" ]]
# The mise variables the isolated HOME would otherwise hide. Recorded rather than asserted here so one case can
# require a forwarded value and another can require that nothing was exported at all.
printf '%s\n' "${MISE_DATA_DIR-<unset>}" >"${FAKE_MISE_RECORD}"
if [[ -n "${FAKE_MUTATE_ASSEMBLY:-}" ]]; then
  printf 'changed\n' >>"${FAKE_MUTATE_ASSEMBLY}"
fi
port="$((32000 + RANDOM % 10000))"
# Where the real host writes it: DesktopBootstrap resolves LocalApplicationData from XDG_DATA_HOME on Linux, and
# DesktopPortStore.Persist appends XE-Local-AI-Engine/desktop-port.txt.
mkdir -p "${XDG_DATA_HOME}/XE-Local-AI-Engine"
printf '%s\n' "${port}" >"${XDG_DATA_HOME}/XE-Local-AI-Engine/desktop-port.txt"
if [[ -z "${FAKE_SUPPRESS_BROWSER_LOG:-}" ]]; then
  printf '[test INF] Opened the default browser at http://127.0.0.1:%s/.\n' "${port}"
fi
exec python3 - "${port}" <<'PY'
import http.server, json, socketserver, sys
class Handler(http.server.BaseHTTPRequestHandler):
    health_requests = 0
    def do_GET(self):
        if self.path == "/health/live":
            Handler.health_requests += 1
            if Handler.health_requests < 3:
                self.send_error(503); return
            body = b'{"status":"Healthy"}'
        elif self.path == "/openapi/local/v1/v1.json":
            body = b'{"openapi":"3.1.0"}'
        else:
            self.send_error(404); return
        self.send_response(200); self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
    def log_message(self, *_): pass
with socketserver.TCPServer(("127.0.0.1", int(sys.argv[1])), Handler) as server:
    server.serve_forever()
PY
FAKE_DOTNET

cat >"${TEMP_ROOT}/bin/pnpm" <<'FAKE_PNPM'
#!/usr/bin/env bash
set -euo pipefail
[[ "$*" == "openapi:check:live" ]]
[[ "${OPENAPI_SPEC_URL}" =~ ^http://127\.0\.0\.1:[0-9]+/openapi/local/v1/v1\.json$ ]]
python3 -c 'import os,urllib.request; assert urllib.request.urlopen(os.environ["OPENAPI_SPEC_URL"], timeout=2).status == 200'
printf '%s\n' "${OPENAPI_SPEC_URL}" >"${FAKE_PNPM_RECORD}"
FAKE_PNPM
chmod 700 "${TEMP_ROOT}/bin/dotnet" "${TEMP_ROOT}/bin/pnpm"

export PATH="${TEMP_ROOT}/bin:${PATH}"
export FAKE_PNPM_RECORD="${TEMP_ROOT}/pnpm-record"
export FAKE_MISE_RECORD="${TEMP_ROOT}/mise-record"
export OPENAPI_LIVE_TIMEOUT_SECONDS=10
export OPENAPI_LIVE_CLIENT_RELEASE_ROOT="${TEMP_ROOT}/release"

# A caller that sets MISE_DATA_DIR: the value has to survive the HOME isolation and reach the host, or every
# mise shim on PATH aborts and the host dies before readiness.
output="$(MISE_DATA_DIR="${TEMP_ROOT}/mise-data" "${SCRIPT_DIR}/openapi-live-check.sh" 2>&1)"
[[ "${output}" == *"PASS: pnpm openapi:check:live succeeded against the live backend."* ]]
[[ "${output}" == *"verify: build output unchanged during the run"* ]]
[[ -s "${FAKE_PNPM_RECORD}" ]]
[[ "$(cat "${FAKE_MISE_RECORD}")" == "${TEMP_ROOT}/mise-data" ]]

# No caller value and no real user paths: the variable must be ABSENT from the host environment rather than
# exported empty, which mise reads as a data directory of "". Asserted on the record the fake writes before it
# does anything else, and the run's own outcome is ignored: a HOME with no trust store is precisely the
# condition that kills the host on a mise-managed box, which is the trap this forwarding exists to avoid.
rm -f "${FAKE_MISE_RECORD}"
HOME="${TEMP_ROOT}/no-such-home" MISE_DATA_DIR='' "${SCRIPT_DIR}/openapi-live-check.sh" >/dev/null 2>&1 || true
[[ "$(cat "${FAKE_MISE_RECORD}")" == "<unset>" ]]

# The port file is the checker's PRIMARY discovery path, and the log-grep below it is only a fallback. With the
# browser log suppressed the fallback has nothing to match, so this case passes only if port_file points where the
# host actually writes — under the isolated XDG_DATA_HOME, not under the isolated HOME. It failed (timed out) while
# the path was wrong, which is how the primary path stayed dead and unnoticed.
rm -f "${FAKE_PNPM_RECORD}"
port_file_output="$(FAKE_SUPPRESS_BROWSER_LOG=1 "${SCRIPT_DIR}/openapi-live-check.sh" 2>&1)"
[[ "${port_file_output}" == *"PASS: pnpm openapi:check:live succeeded against the live backend."* ]]
[[ -s "${FAKE_PNPM_RECORD}" ]]

printf 'stable\n' >"${TEMP_ROOT}/release/fake.dll"
export FAKE_MUTATE_ASSEMBLY="${TEMP_ROOT}/release/fake.dll"
set +e
contaminated_output="$("${SCRIPT_DIR}/openapi-live-check.sh" 2>&1)"
contaminated_status=$?
set -e
[[ "${contaminated_status}" -eq 75 ]]
[[ "${contaminated_output}" == *"CONTAMINATED RUN — RE-RUN REQUIRED"* ]]

echo "openapi-live-check.test.sh: PASS"
