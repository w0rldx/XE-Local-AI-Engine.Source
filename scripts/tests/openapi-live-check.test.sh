#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
TEMP_ROOT="$(mktemp -d)"
trap 'rm -rf -- "${TEMP_ROOT}"' EXIT
mkdir -p "${TEMP_ROOT}/bin"
mkdir -p "${TEMP_ROOT}/release"
printf 'stable\n' >"${TEMP_ROOT}/release/fake.dll"

cat >"${TEMP_ROOT}/bin/dotnet" <<'FAKE_DOTNET'
#!/usr/bin/env bash
set -euo pipefail
[[ " $* " == *" run "* && " $* " == *" --no-build "* && " $* " == *" --desktop "* ]]
if [[ -n "${FAKE_MUTATE_ASSEMBLY:-}" ]]; then
  printf 'changed\n' >>"${FAKE_MUTATE_ASSEMBLY}"
fi
port="$((32000 + RANDOM % 10000))"
printf '[test INF] Opened the default browser at http://127.0.0.1:%s/.\n' "${port}"
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
export OPENAPI_LIVE_TIMEOUT_SECONDS=10
export OPENAPI_LIVE_CLIENT_RELEASE_ROOT="${TEMP_ROOT}/release"

output="$("${SCRIPT_DIR}/openapi-live-check.sh" 2>&1)"
[[ "${output}" == *"PASS: live backend contract matches committed frontend artifacts."* ]]
[[ "${output}" == *"verify: build output unchanged during the run"* ]]
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
