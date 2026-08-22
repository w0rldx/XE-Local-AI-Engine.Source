#!/usr/bin/env bash
# Contract tests for the root Bash installer. All network traffic stays on loopback.
set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
installer="$repo_root/install.sh"
temp_dir="$(mktemp -d)"
server_pid=""
cleanup() {
  [[ -z "$server_pid" ]] || kill "$server_pid" 2>/dev/null || true
  rm -rf "$temp_dir"
}
trap cleanup EXIT

fail() { echo "install.test.sh: FAIL: $*" >&2; exit 1; }
assert_contains() { grep -Fq -- "$2" <<<"$1" || fail "expected output to contain '$2'"; }
assert_status() {
  local expected="$1"; shift
  set +e
  command_output="$("$@" 2>&1)"
  command_status=$?
  set -e
  [[ "$command_status" -eq "$expected" ]] || fail "expected exit $expected, got $command_status: $command_output"
}

fixture="$temp_dir/fixture"
mkdir -p "$fixture/assets" "$fixture/repos/w0rldx/XE-Local-AI-Engine.Source/releases/tags"

cat >"$fixture/assets/XE-stable.AppImage" <<'APPIMAGE'
#!/usr/bin/env bash
printf '%s\t%s\n' "$*" "${XE_DATA_DIR:-}" >>"${XE_EXECUTION_MARKER:?}"
case "${1:-}" in
  --setup)
    [[ -n "${XE_ADMIN_EMAIL:-}" && -n "${XE_ADMIN_PASSWORD:-}" ]] || exit 3
    if [[ "${XE_TEST_SETUP_MODE:-created}" == fail ]]; then
      echo "setup validation rejected value; supplied password was $XE_ADMIN_PASSWORD" >&2
      exit 3
    fi
    if [[ "${XE_TEST_SETUP_MODE:-created}" == already ]]; then
      echo 'XE_SETUP=already-configured'
    else
      printf '%s\n' 'XE_SETUP=created' "XE_ADMIN_EMAIL=$XE_ADMIN_EMAIL"
    fi
    ;;
  --mcp-key)
    case "${XE_TEST_KEY_MODE:-ok}" in
      wrong) echo 'XE_MCP_KEY=wrong' ;;
      multiple) echo 'XE_MCP_KEY=xemcp_one'; echo 'XE_MCP_KEY=xemcp_two' ;;
      fail) echo 'key rotation backend unavailable for xemcp_hidden' >&2; exit 4 ;;
      *) echo 'warning: rotation' >&2; echo 'XE_MCP_KEY=xemcp_fixture' ;;
    esac
    ;;
  --mcp-only)
    [[ "${XE_TEST_START_MODE:-ready}" != no-ready ]] || { sleep 30; exit 0; }
    exec python3 -c '
import http.server,json,os,pathlib,socketserver
data=pathlib.Path(os.environ["XE_DATA_DIR"]); data.mkdir(parents=True,exist_ok=True)
class H(http.server.BaseHTTPRequestHandler):
 def do_GET(self):
  self.send_response(200 if self.path=="/health/ready" else 404); self.end_headers()
 def log_message(self,*args): pass
with socketserver.TCPServer(("127.0.0.1",0),H) as server:
 url=f"http://127.0.0.1:{server.server_address[1]}"
 (data/"desktop-port.txt").write_text(str(server.server_address[1]))
 (data/"ready.json").write_text(json.dumps({"version":"v1.0.0","url":url,"mcpUrl":url+"/api/local/v1/mcp/server","dataDir":str(data),"pid":os.getpid(),"startedAtUtc":"2026-08-22T00:00:00Z"}))
 print(f"XE_READY=1 XE_VERSION=v1.0.0 XE_URL={url} XE_MCP_URL={url}/api/local/v1/mcp/server XE_DATA_DIR={data}",flush=True)
 server.serve_forever()
'
    ;;
esac
APPIMAGE
cat >"$fixture/assets/XE-pre.AppImage" <<'APPIMAGE'
#!/usr/bin/env bash
echo prerelease-executed >>"${XE_EXECUTION_MARKER:?}"
APPIMAGE
cat >"$fixture/assets/XE-fuse.AppImage" <<'APPIMAGE'
#!/usr/bin/env bash
if [[ "${APPIMAGE_EXTRACT_AND_RUN:-0}" != 1 ]]; then
  echo 'fuse: failed to open /dev/fuse' >&2
  exit 1
fi
printf 'fallback:%s\n' "$*"
APPIMAGE
cat >"$fixture/assets/XE-fuse-delayed.AppImage" <<'APPIMAGE'
#!/usr/bin/env bash
printf '%s\n' "${APPIMAGE_EXTRACT_AND_RUN:-0}" >>"${XE_FUSE_ATTEMPT_LOG:?}"
if [[ "${APPIMAGE_EXTRACT_AND_RUN:-0}" != 1 ]]; then
  sleep 0.7
  echo 'fuse: delayed failure opening /dev/fuse' >&2
  exit 1
fi
exec python3 -c '
import http.server,json,os,pathlib,socketserver
data=pathlib.Path(os.environ["XE_DATA_DIR"]); data.mkdir(parents=True,exist_ok=True)
class H(http.server.BaseHTTPRequestHandler):
 def do_GET(self): self.send_response(200 if self.path=="/health/ready" else 404); self.end_headers()
 def log_message(self,*args): pass
with socketserver.TCPServer(("127.0.0.1",0),H) as server:
 url=f"http://127.0.0.1:{server.server_address[1]}"
 (data/"ready.json").write_text(json.dumps({"version":"v1.0.0","url":url,"mcpUrl":url+"/api/local/v1/mcp/server","dataDir":str(data),"pid":os.getpid(),"startedAtUtc":"2026-08-22T00:00:00Z"}))
 print(f"XE_READY=1 XE_VERSION=v1.0.0 XE_URL={url} XE_MCP_URL={url}/api/local/v1/mcp/server XE_DATA_DIR={data}",flush=True)
 server.serve_forever()
'
APPIMAGE
chmod +x "$fixture/assets/"*.AppImage

stable_hash="$(sha256sum "$fixture/assets/XE-stable.AppImage" | awk '{print $1}')"
pre_hash="$(sha256sum "$fixture/assets/XE-pre.AppImage" | awk '{print $1}')"
cat >"$fixture/assets/CHECKSUMS.sha256" <<EOF
$stable_hash  ./XE-stable.AppImage
$pre_hash  ./XE-pre.AppImage
EOF
cat >"$fixture/assets/CHECKSUMS-bad.sha256" <<EOF
0000000000000000000000000000000000000000000000000000000000000000  ./XE-stable.AppImage
EOF
cat >"$fixture/assets/MANIFEST-stable.json" <<EOF
{"tag":"v1.0.0","sourceSha":"1234567890abcdef","assets":[{"name":"XE-stable.AppImage","sha256":"$stable_hash"}]}
EOF
cat >"$fixture/assets/MANIFEST-pre.json" <<EOF
{"tag":"v1.1.0-rc.1","sourceSha":"abcdef1234567890","assets":[{"name":"XE-pre.AppImage","sha256":"$pre_hash"}]}
EOF
manifest_bad_hash="$(printf '%064d' 0 | tr 0 f)"
cat >"$fixture/assets/MANIFEST-bad.json" <<EOF
{"tag":"v0.8.0","sourceSha":"badbadbadbadbadb","assets":[{"name":"XE-stable.AppImage","sha256":"$manifest_bad_hash"}]}
EOF

port_file="$temp_dir/port"
request_log="$temp_dir/requests.log"
python3 - "$fixture" "$port_file" "$request_log" <<'PY' &
import http.server
import os
import socketserver
import sys

os.chdir(sys.argv[1])
request_log = sys.argv[3]
class Handler(http.server.SimpleHTTPRequestHandler):
    def do_GET(self):
        with open(request_log, 'a', encoding='utf-8') as handle:
            handle.write(f"{self.path} authorization={self.headers.get('Authorization', '')}\n")
        super().do_GET()
class Server(socketserver.TCPServer):
    allow_reuse_address = True
with Server(('127.0.0.1', 0), Handler) as server:
    with open(sys.argv[2], 'w', encoding='utf-8') as handle:
        handle.write(str(server.server_address[1]))
    server.serve_forever()
PY
server_pid=$!
for _ in $(seq 1 50); do [[ -s "$port_file" ]] && break; sleep 0.05; done
[[ -s "$port_file" ]] || fail 'fixture server did not start'
port="$(cat "$port_file")"
base="http://127.0.0.1:$port"

release_json() {
  local tag="$1" prerelease="$2" published="$3" asset="$4" checksum_path="${5:-CHECKSUMS.sha256}" manifest_path="${6:-}"
  local manifest_asset=""
  [[ -z "$manifest_path" ]] || manifest_asset=",{\"name\":\"RELEASE-MANIFEST.json\",\"browser_download_url\":\"https://github.com/assets/$manifest_path\"}"
  cat <<EOF
{"tag_name":"$tag","draft":false,"prerelease":$prerelease,"published_at":"$published","assets":[{"name":"$asset","browser_download_url":"https://github.com/assets/$asset"},{"name":"CHECKSUMS.sha256","browser_download_url":"https://github.com/assets/$checksum_path"}$manifest_asset]}
EOF
}
stable="$(release_json v1.0.0 false 2026-01-01T00:00:00Z XE-stable.AppImage CHECKSUMS.sha256 MANIFEST-stable.json)"
pre="$(release_json v1.1.0-rc.1 true 2026-02-01T00:00:00Z XE-pre.AppImage CHECKSUMS.sha256 MANIFEST-pre.json)"
bad="$(release_json v0.9.0 false 2025-12-01T00:00:00Z XE-stable.AppImage CHECKSUMS-bad.sha256)"
manifest_bad="$(release_json v0.8.0 false 2025-11-01T00:00:00Z XE-stable.AppImage CHECKSUMS.sha256 MANIFEST-bad.json)"
printf '[%s,%s]\n' "$stable" "$pre" >"$fixture/repos/w0rldx/XE-Local-AI-Engine.Source/releases/index.html"
printf '%s\n' "$stable" >"$fixture/repos/w0rldx/XE-Local-AI-Engine.Source/releases/tags/v1.0.0"
printf '%s\n' "$pre" >"$fixture/repos/w0rldx/XE-Local-AI-Engine.Source/releases/tags/v1.1.0-rc.1"
printf '%s\n' "$bad" >"$fixture/repos/w0rldx/XE-Local-AI-Engine.Source/releases/tags/v0.9.0"
printf '%s\n' "$manifest_bad" >"$fixture/repos/w0rldx/XE-Local-AI-Engine.Source/releases/tags/v0.8.0"

common=(--github-api-base "$base" --download-base "$base" --yes)
export XE_EXECUTION_MARKER="$temp_dir/executed"

stable_dir="$temp_dir/stable"
stable_output="$($installer "${common[@]}" --github-token secret-that-must-not-leak --install-dir "$stable_dir" 2>&1)"
assert_contains "$stable_output" 'XE_VERSION=v1.0.0'
assert_contains "$stable_output" 'Verified: v1.0.0 (commit 1234567890ab)'
[[ -x "$stable_dir/XE-stable.AppImage" ]] || fail 'stable AppImage was not installed executable'
[[ "$(cat "$stable_dir/.xe-local-ai-engine-install")" == 'XE_LOCAL_AI_ENGINE_INSTALL=1' ]] || fail 'ownership marker missing'
[[ ! -e "$XE_EXECUTION_MARKER" ]] || fail 'install-only unexpectedly executed the AppImage'
grep -Fq 'authorization=' "$request_log" || fail 'fixture server did not log requests'
if grep -Fq 'authorization=Bearer' "$request_log"; then fail 'GitHub token leaked to loopback or asset requests'; fi

pre_dir="$temp_dir/pre"
pre_output="$($installer "${common[@]}" --pre --install-dir "$pre_dir")"
assert_contains "$pre_output" 'XE_VERSION=v1.1.0-rc.1'
[[ -f "$pre_dir/XE-pre.AppImage" ]] || fail 'prerelease asset was not selected'

pin_dir="$temp_dir/pin"
pin_output="$($installer "${common[@]}" --version 1.0.0 --install-dir "$pin_dir")"
assert_contains "$pin_output" 'XE_VERSION=v1.0.0'

assert_status 4 "$installer" "${common[@]}" --version 9.9.9 --install-dir "$temp_dir/missing"
assert_contains "$command_output" 'No release found for tag v9.9.9'

assert_status 3 "$installer" "${common[@]}" --version 0.9.0 --install-dir "$temp_dir/bad"
assert_contains "$command_output" 'Checksum mismatch'
[[ ! -e "$temp_dir/bad" ]] || fail 'checksum failure mutated the install directory'

assert_status 3 "$installer" "${common[@]}" --version 0.8.0 --install-dir "$temp_dir/manifest-bad"
assert_contains "$command_output" 'RELEASE-MANIFEST.json does not agree'
[[ ! -e "$temp_dir/manifest-bad" ]] || fail 'manifest mismatch mutated the install directory'

mtime_before="$(stat -c %Y "$stable_dir/XE-stable.AppImage")"
sleep 1
noop_output="$($installer "${common[@]}" --install-dir "$stable_dir")"
assert_contains "$noop_output" 'XE_VERSION=v1.0.0'
[[ "$(stat -c %Y "$stable_dir/XE-stable.AppImage")" == "$mtime_before" ]] || fail 'idempotent install rewrote the artifact'

downgrade_dir="$temp_dir/downgrade"
$installer "${common[@]}" --pre --install-dir "$downgrade_dir" >/dev/null
[[ -f "$downgrade_dir/XE-pre.AppImage" ]] || fail 'upgrade setup failed'
$installer "${common[@]}" --version v1.0.0 --install-dir "$downgrade_dir" >/dev/null
[[ -f "$downgrade_dir/XE-stable.AppImage" && ! -e "$downgrade_dir/XE-pre.AppImage" ]] || fail 'version replacement retained stale files'

unowned_dir="$temp_dir/unowned"
mkdir -p "$unowned_dir"
printf 'preserve me\n' >"$unowned_dir/important.txt"
assert_status 1 "$installer" "${common[@]}" --install-dir "$unowned_dir"
assert_contains "$command_output" 'Refusing to replace non-empty directory'
[[ "$(cat "$unowned_dir/important.txt")" == 'preserve me' ]] || fail 'unowned directory was mutated'

owned_incomplete="$temp_dir/owned-incomplete"
mkdir -p "$owned_incomplete"
printf 'XE_LOCAL_AI_ENGINE_INSTALL=1\n' >"$owned_incomplete/.xe-local-ai-engine-install"
printf 'stale\n' >"$owned_incomplete/stale.txt"
$installer "${common[@]}" --install-dir "$owned_incomplete" >/dev/null
[[ -f "$owned_incomplete/XE-stable.AppImage" && ! -e "$owned_incomplete/stale.txt" ]] || fail 'owned incomplete install was not repaired atomically'

rollback_dir="$temp_dir/rollback"
mkdir -p "$rollback_dir"
printf 'XE_LOCAL_AI_ENGINE_INSTALL=1\n' >"$rollback_dir/.xe-local-ai-engine-install"
printf 'preserve\n' >"$rollback_dir/sentinel.txt"
printf 'v1.0.0\n' >"$rollback_dir/xe-install-version.txt"
# $1/$2 intentionally belong to the child bash -c positional arguments.
# shellcheck disable=SC2016
assert_status 1 env XE_INSTALLER_LIBRARY_ONLY=1 XE_TEST_FAIL_INSTALL_SWAP=1 \
  INSTALL_DIR="$rollback_dir" RESOLVED_VERSION=v2.0.0 \
  bash -c 'source "$1"; replace_install_from_artifact "$2" replacement.AppImage' \
  _ "$installer" "$fixture/assets/XE-stable.AppImage"
assert_contains "$command_output" 'prior installation was restored'
[[ "$(cat "$rollback_dir/sentinel.txt")" == preserve ]] || fail 'staging failure damaged the prior install'
[[ "$(cat "$rollback_dir/xe-install-version.txt")" == v1.0.0 ]] || fail 'rollback drifted the prior version marker'
[[ "$(cat "$rollback_dir/.xe-local-ai-engine-install")" == XE_LOCAL_AI_ENGINE_INSTALL=1 ]] || fail 'rollback drifted ownership marker'
[[ ! -e "$rollback_dir/replacement.AppImage" ]] || fail 'rollback left partial replacement payload'
if compgen -G "$temp_dir/.xe-install-*" >/dev/null; then fail 'rollback left staging or backup residue'; fi

assert_status 1 "$installer" "${common[@]}" --install-dir "$HOME"
assert_contains "$command_output" 'user home directory'
assert_status 1 env XDG_DATA_HOME="$temp_dir/xdg" "$installer" "${common[@]}" --install-dir "$temp_dir/xdg/XE-Local-AI-Engine"
assert_contains "$command_output" 'app-owned data directory'
assert_status 1 env XDG_DATA_HOME="$temp_dir/nonexistent-xdg" "$installer" "${common[@]}" --install-dir "$temp_dir/nonexistent-xdg"
assert_contains "$command_output" 'app-owned data directory'
[[ ! -e "$temp_dir/nonexistent-xdg" ]] || fail 'data-directory ancestor validation created the nonexistent path'
assert_status 1 "$installer" "${common[@]}" --install-dir /
assert_contains "$command_output" 'filesystem root'

dry_dir="$temp_dir/dry-run"
dry_output="$($installer "${common[@]}" --pre --dry-run --install-dir "$dry_dir")"
assert_contains "$dry_output" 'XE_INSTALL_PLAN=1'
[[ ! -e "$dry_dir" ]] || fail 'dry-run created the install directory'

assert_status 11 env XE_ADMIN_EMAIL=admin@localhost.test XE_ADMIN_PASSWORD=secret XE_TEST_KEY_MODE=wrong \
  "$installer" "${common[@]}" --setup --install-dir "$stable_dir"
assert_contains "$command_output" 'did not return exactly one XE_MCP_KEY line'
assert_status 11 env XE_ADMIN_EMAIL=admin@localhost.test XE_ADMIN_PASSWORD=secret XE_TEST_KEY_MODE=multiple \
  "$installer" "${common[@]}" --setup --install-dir "$stable_dir"
assert_contains "$command_output" 'did not return exactly one XE_MCP_KEY line'
assert_status 11 env XE_ADMIN_EMAIL=admin@localhost.test XE_ADMIN_PASSWORD='diagnostic-secret' XE_TEST_SETUP_MODE=fail \
  "$installer" "${common[@]}" --setup --install-dir "$stable_dir"
assert_contains "$command_output" 'engine code 3: setup validation rejected value'
assert_contains "$command_output" '[REDACTED]'
if grep -Fq 'diagnostic-secret' <<<"$command_output"; then fail 'setup diagnostic leaked the password'; fi
assert_status 11 env XE_ADMIN_EMAIL=admin@localhost.test XE_ADMIN_PASSWORD=secret XE_TEST_KEY_MODE=fail \
  "$installer" "${common[@]}" --setup --install-dir "$stable_dir"
assert_contains "$command_output" 'engine code 4: key rotation backend unavailable'
if grep -Fq 'xemcp_hidden' <<<"$command_output"; then fail 'key diagnostic leaked key material'; fi
assert_status 11 env -u XE_ADMIN_EMAIL -u XE_ADMIN_PASSWORD XE_NONINTERACTIVE=1 \
  "$installer" "${common[@]}" --setup --install-dir "$stable_dir"
assert_contains "$command_output" 'requires XE_ADMIN_EMAIL and XE_ADMIN_PASSWORD'

key_temp="$temp_dir/key-temp"
mkdir -p "$key_temp"
setup_output="$(TMPDIR="$key_temp" XE_ADMIN_EMAIL=admin@localhost.test XE_ADMIN_PASSWORD='never-print-this' \
  "$installer" "${common[@]}" --setup --install-dir "$stable_dir" 2>&1)"
assert_contains "$setup_output" 'XE_SETUP=created'
assert_contains "$setup_output" 'XE_ADMIN_EMAIL=admin@localhost.test'
[[ "$(grep -c '^XE_MCP_KEY=' <<<"$setup_output")" -eq 1 ]] || fail 'setup did not relay exactly one MCP key'
if grep -Fq 'never-print-this' <<<"$setup_output" || grep -Fq 'never-print-this' "$XE_EXECUTION_MARKER"; then
  fail 'administrator password leaked to output or engine argv'
fi
if grep -R -Fq 'xemcp_fixture' "$key_temp"; then fail 'MCP key was written to an installer-created temp file'; fi
if grep -Fq 'key_output="$(mktemp)' "$installer" \
    || grep -Fq -- '--mcp-key agentic >"$key_output"' "$installer"; then
  fail 'MCP key capture is not memory-only'
fi
if grep -Fq -- '--setup --mcp-only' "$XE_EXECUTION_MARKER"; then fail 'setup and serve were combined'; fi
already_output="$(XE_ADMIN_EMAIL=admin@localhost.test XE_ADMIN_PASSWORD=secret XE_TEST_SETUP_MODE=already \
  "$installer" "${common[@]}" --setup --install-dir "$stable_dir")"
assert_contains "$already_output" 'XE_SETUP=already-configured'
if grep -q '^XE_ADMIN_EMAIL=' <<<"$already_output"; then fail 'already-configured setup fabricated an email'; fi

start_data="$temp_dir/start-data"
start_output="$(XE_DATA_DIR="$start_data" XE_START_TIMEOUT_SECONDS=5 \
  "$installer" "${common[@]}" --start --install-dir "$stable_dir")"
assert_contains "$start_output" 'XE_READY=1 XE_VERSION=v1.0.0'
assert_contains "$start_output" "XE_DATA_DIR=$start_data"
start_pid="$(sed -n 's/^XE_PID=//p' <<<"$start_output")"
[[ -n "$start_pid" ]] || fail 'start did not report a PID'
[[ "$(jq -r '.pid' "$start_data/ready.json")" == "$start_pid" ]] || fail 'ready.json PID did not match launched PID'
kill "$start_pid" 2>/dev/null || true

delayed_fuse_data="$temp_dir/delayed-fuse-data"
fuse_attempt_log="$temp_dir/fuse-attempts.log"
delayed_fuse_output="$(XE_INSTALLER_LIBRARY_ONLY=1 XE_DATA_DIR="$delayed_fuse_data" \
  XE_START_TIMEOUT_SECONDS=5 XE_START_POLL_SECONDS=0.1 XE_FUSE_ATTEMPT_LOG="$fuse_attempt_log" \
  INSTALL_DIR="$temp_dir" bash -c 'source "$1"; run_start "$2"' \
  _ "$installer" "$fixture/assets/XE-fuse-delayed.AppImage" 2>&1)"
assert_contains "$delayed_fuse_output" 'FUSE launch failed; retrying with APPIMAGE_EXTRACT_AND_RUN=1'
[[ "$(grep -c '^0$' "$fuse_attempt_log")" -eq 1 && "$(grep -c '^1$' "$fuse_attempt_log")" -eq 1 ]] \
  || fail 'delayed FUSE failure did not retry exactly once'
delayed_fuse_pid="$(sed -n 's/^XE_PID=//p' <<<"$delayed_fuse_output")"
[[ -n "$delayed_fuse_pid" ]] || fail 'delayed FUSE retry did not become ready'
kill "$delayed_fuse_pid" 2>/dev/null || true

assert_status 12 env XE_DATA_DIR="$temp_dir/timeout-data" XE_START_TIMEOUT_SECONDS=1 XE_TEST_START_MODE=no-ready \
  "$installer" "${common[@]}" --start --install-dir "$stable_dir"
assert_contains "$command_output" 'did not produce live canonical ready.json evidence'

autostart_bin="$temp_dir/autostart-bin"
autostart_log="$temp_dir/systemctl.log"
mkdir -p "$autostart_bin"
cat >"$autostart_bin/systemctl" <<'SYSTEMCTL'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${XE_SYSTEMCTL_LOG:?}"
command_name="${2:-}"
case "$command_name" in
  is-enabled)
    if [[ "${XE_SYSTEMCTL_FAIL_MODE:-}" == is-enabled ]]; then
      echo 'Failed to query unit state' >&2
      exit 2
    fi
    if [[ ! -f "${XE_SYSTEMCTL_STATE:?}" ]]; then
      unit_path="${XE_SYSTEMCTL_UNIT_PATH:-${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/xe-local-ai-engine.service}"
      if [[ -e "$unit_path" ]]; then
        printf '%s\n' disabled
        exit 1
      fi
      printf '%s\n' not-found; exit 4
    fi
    state="$(cat "$XE_SYSTEMCTL_STATE")"
    printf '%s\n' "$state"
    [[ "$state" == enabled ]]
    ;;
  daemon-reload)
    if [[ "${XE_SYSTEMCTL_FAIL_MODE:-}" == daemon-reload && ! -e "${XE_SYSTEMCTL_STATE:?}.daemon-failed" ]]; then
      : >"${XE_SYSTEMCTL_STATE}.daemon-failed"
      exit 1
    fi
    ;;
  enable)
    printf '%s\n' enabled >"${XE_SYSTEMCTL_STATE:?}"
    if [[ "${XE_SYSTEMCTL_FAIL_MODE:-}" == enable-and-disable ]]; then
      exit 1
    fi
    if [[ "${XE_SYSTEMCTL_FAIL_MODE:-}" == enable && ! -e "${XE_SYSTEMCTL_STATE}.enable-failed" ]]; then
      : >"${XE_SYSTEMCTL_STATE}.enable-failed"
      exit 1
    fi
    ;;
  disable)
    [[ "${XE_SYSTEMCTL_FAIL_MODE:-}" != enable-and-disable ]] || exit 1
    rm -f -- "${XE_SYSTEMCTL_STATE:?}"
    ;;
esac
SYSTEMCTL
chmod +x "$autostart_bin/systemctl"
config_home="$temp_dir/config %i"
autostart_state="$temp_dir/systemctl.state"
[[ ! -e "$config_home/systemd/user/xe-local-ai-engine.service" ]] || fail 'autostart existed before opt-in'
autostart_data="$temp_dir/custom data/engine state"
PATH="$autostart_bin:$PATH" XE_SYSTEMCTL_LOG="$autostart_log" XE_SYSTEMCTL_STATE="$autostart_state" XDG_CONFIG_HOME="$config_home" \
  XE_DATA_DIR="$autostart_data" XE_ADMIN_EMAIL=admin@localhost.test XE_ADMIN_PASSWORD=secret \
  "$installer" "${common[@]}" --setup --autostart --install-dir "$stable_dir" >/dev/null
unit="$config_home/systemd/user/xe-local-ai-engine.service"
launcher="$config_home/systemd/user/xe-local-ai-engine/launch"
[[ -f "$unit" ]] || fail 'autostart did not create a user unit'
[[ -x "$launcher" ]] || fail 'autostart did not create an executable installer-owned launcher'
escaped_launcher="${launcher//%/%%}"
assert_contains "$(cat "$unit")" "ExecStart=\"$escaped_launcher\""
if grep -Eqi 'password|XE_ADMIN|credential' "$unit" "$launcher"; then fail 'autostart files contain credentials'; fi
grep -Fq -- $'--setup\t'"$autostart_data" "$XE_EXECUTION_MARKER" \
  || fail 'setup did not inherit the same resolved custom data directory as autostart'
assert_contains "$(cat "$autostart_log")" '--user daemon-reload'
assert_contains "$(cat "$autostart_log")" '--user enable xe-local-ai-engine.service'
unit_hash="$(sha256sum "$unit")"
launcher_hash="$(sha256sum "$launcher")"
PATH="$autostart_bin:$PATH" XE_SYSTEMCTL_LOG="$autostart_log" XE_SYSTEMCTL_STATE="$autostart_state" XDG_CONFIG_HOME="$config_home" \
  XE_DATA_DIR="$autostart_data" "$installer" "${common[@]}" --autostart --install-dir "$stable_dir" >/dev/null
[[ "$(sha256sum "$unit")" == "$unit_hash" ]] || fail 'idempotent autostart changed its unit'
[[ "$(sha256sum "$launcher")" == "$launcher_hash" ]] || fail 'idempotent autostart changed its launcher content'

launcher_primary="$temp_dir/primary-launcher/launch"
primary_engine="$temp_dir/primary engine.AppImage"
primary_log="$temp_dir/primary-launch.log"
cat >"$primary_engine" <<'PRIMARY'
#!/usr/bin/env bash
printf '%s|%s|%s\n' "${APPIMAGE_EXTRACT_AND_RUN:-0}" "$XE_DATA_DIR" "$*" >>"${XE_AUTOSTART_ATTEMPT_LOG:?}"
PRIMARY
chmod +x "$primary_engine"
XE_INSTALLER_LIBRARY_ONLY=1 bash -c 'source "$1"; write_autostart_launcher "$2" "$3" "$4"' \
  _ "$installer" "$primary_engine" "$autostart_data" "$launcher_primary"
XE_AUTOSTART_ATTEMPT_LOG="$primary_log" "$launcher_primary"
[[ "$(cat "$primary_log")" == "0|$autostart_data|--mcp-only" ]] \
  || fail 'primary autostart launch did not preserve the custom data directory without fallback'

launcher_delayed="$temp_dir/delayed-launcher/launch"
delayed_engine="$temp_dir/delayed fuse.AppImage"
delayed_log="$temp_dir/delayed-autostart.log"
cat >"$delayed_engine" <<'DELAYED'
#!/usr/bin/env bash
printf '%s|%s|%s\n' "${APPIMAGE_EXTRACT_AND_RUN:-0}" "$XE_DATA_DIR" "$*" >>"${XE_AUTOSTART_ATTEMPT_LOG:?}"
if [[ "${APPIMAGE_EXTRACT_AND_RUN:-0}" != 1 ]]; then
  sleep 0.3
  echo 'dlopen(): error loading libfuse.so.2' >&2
  exit 1
fi
DELAYED
chmod +x "$delayed_engine"
XE_INSTALLER_LIBRARY_ONLY=1 bash -c 'source "$1"; write_autostart_launcher "$2" "$3" "$4"' \
  _ "$installer" "$delayed_engine" "$autostart_data" "$launcher_delayed"
XE_AUTOSTART_ATTEMPT_LOG="$delayed_log" "$launcher_delayed" >/dev/null 2>&1
[[ "$(grep -c '^0|' "$delayed_log")" -eq 1 && "$(grep -c '^1|' "$delayed_log")" -eq 1 ]] \
  || fail 'autostart delayed FUSE failure did not retry exactly once'
XE_AUTOSTART_ATTEMPT_LOG="$delayed_log" "$launcher_delayed" >/dev/null 2>&1
[[ "$(grep -c '^0|' "$delayed_log")" -eq 1 && "$(grep -c '^1|' "$delayed_log")" -eq 2 ]] \
  || fail 'autostart did not persist extract-and-run mode after the verified FUSE failure'

launcher_volume="$temp_dir/volume-launcher/launch"
volume_engine="$temp_dir/high-volume.AppImage"
volume_log="$temp_dir/high-volume-attempts.log"
runtime_capture="$temp_dir/runtime-capture"
mkdir -p "$runtime_capture"
cat >"$volume_engine" <<'VOLUME'
#!/usr/bin/env bash
printf '%s\n' "${APPIMAGE_EXTRACT_AND_RUN:-0}" >>"${XE_AUTOSTART_ATTEMPT_LOG:?}"
if [[ "${APPIMAGE_EXTRACT_AND_RUN:-0}" != 1 ]]; then
  head -c 8388608 /dev/zero | tr '\0' x >&2
  printf '\n' >&2
  echo 'fusermount: mount failed' >&2
  exit 1
fi
VOLUME
chmod +x "$volume_engine"
XE_INSTALLER_LIBRARY_ONLY=1 bash -c 'source "$1"; write_autostart_launcher "$2" "$3" "$4"' \
  _ "$installer" "$volume_engine" "$autostart_data" "$launcher_volume"
XDG_RUNTIME_DIR="$runtime_capture" XE_AUTOSTART_ATTEMPT_LOG="$volume_log" "$launcher_volume" >/dev/null 2>&1
[[ "$(grep -c '^0$' "$volume_log")" -eq 1 && "$(grep -c '^1$' "$volume_log")" -eq 1 ]] \
  || fail 'bounded streaming classifier missed high-volume fusermount failure'
if find "$runtime_capture" -mindepth 1 -print -quit | grep -q .; then
  fail 'autostart bounded classifier left capture files after normal exit'
fi

launcher_signal="$temp_dir/signal-launcher/launch"
signal_engine="$temp_dir/signal.AppImage"
signal_runtime="$temp_dir/signal-runtime"
mkdir -p "$signal_runtime"
cat >"$signal_engine" <<'SIGNAL'
#!/usr/bin/env bash
exec sleep 30
SIGNAL
chmod +x "$signal_engine"
XE_INSTALLER_LIBRARY_ONLY=1 bash -c 'source "$1"; write_autostart_launcher "$2" "$3" "$4"' \
  _ "$installer" "$signal_engine" "$autostart_data" "$launcher_signal"
XDG_RUNTIME_DIR="$signal_runtime" "$launcher_signal" >/dev/null 2>&1 &
signal_launcher_pid=$!
for _ in {1..50}; do
  find "$signal_runtime" -mindepth 1 -print -quit | grep -q . && break
  sleep 0.02
done
kill -TERM "$signal_launcher_pid"
set +e
wait "$signal_launcher_pid"
signal_status=$?
set -e
[[ "$signal_status" -eq 143 ]] || fail "signal cleanup launcher exited $signal_status instead of 143"
if find "$signal_runtime" -mindepth 1 -print -quit | grep -q .; then
  fail 'autostart bounded classifier left capture files after signal cleanup'
fi

launcher_non_fuse="$temp_dir/non-fuse-launcher/launch"
non_fuse_engine="$temp_dir/non-fuse.AppImage"
non_fuse_log="$temp_dir/non-fuse-autostart.log"
cat >"$non_fuse_engine" <<'NONFUSE'
#!/usr/bin/env bash
printf '%s\n' "${APPIMAGE_EXTRACT_AND_RUN:-0}" >>"${XE_AUTOSTART_ATTEMPT_LOG:?}"
echo 'connection refused before startup' >&2
exit 23
NONFUSE
chmod +x "$non_fuse_engine"
XE_INSTALLER_LIBRARY_ONLY=1 bash -c 'source "$1"; write_autostart_launcher "$2" "$3" "$4"' \
  _ "$installer" "$non_fuse_engine" "$autostart_data" "$launcher_non_fuse"
assert_status 23 env XE_AUTOSTART_ATTEMPT_LOG="$non_fuse_log" "$launcher_non_fuse"
[[ "$(cat "$non_fuse_log")" == 0 ]] || fail 'non-FUSE autostart failure was retried instead of flowing to systemd'

old_unit_hash="$(sha256sum "$unit")"
old_launcher_hash="$(sha256sum "$launcher")"
fallback_mode="$config_home/systemd/user/xe-local-ai-engine/appimage-extract-and-run"
printf '%s\n' preserved >"$fallback_mode"
chmod 0600 "$fallback_mode"
old_fallback_hash="$(sha256sum "$fallback_mode")"
for failure_step in secure-transaction stage-directory stage-launcher stage-marker stage-fallback stage-unit backup-unit backup-launcher \
  swap-old-unit swap-new-unit swap-old-launcher swap-new-launcher; do
  assert_status 14 env PATH="$autostart_bin:$PATH" XE_SYSTEMCTL_LOG="$autostart_log" XE_SYSTEMCTL_STATE="$autostart_state" \
    XE_TEST_FAIL_AUTOSTART_STEP="$failure_step" XDG_CONFIG_HOME="$config_home" \
    XE_DATA_DIR="$temp_dir/replacement data $failure_step" \
    "$installer" "${common[@]}" --autostart --install-dir "$stable_dir"
  assert_contains "$command_output" 'Prior autostart files and enabled state were restored'
  [[ "$(sha256sum "$unit")" == "$old_unit_hash" && "$(sha256sum "$launcher")" == "$old_launcher_hash" \
      && "$(sha256sum "$fallback_mode")" == "$old_fallback_hash" ]] \
    || fail "autostart $failure_step rollback did not restore prior bytes"
  [[ "$(cat "$autostart_state")" == enabled ]] || fail "autostart $failure_step changed enabled state"
  if find "$config_home/systemd/user" -maxdepth 1 -name '.xe-autostart-transaction.*' -print -quit | grep -q .; then
    fail "autostart $failure_step left transaction residue"
  fi
done

for systemctl_failure in daemon-reload enable; do
  rm -f "$autostart_state.$systemctl_failure-failed"
  assert_status 14 env PATH="$autostart_bin:$PATH" XE_SYSTEMCTL_LOG="$autostart_log" XE_SYSTEMCTL_STATE="$autostart_state" \
    XE_SYSTEMCTL_FAIL_MODE="$systemctl_failure" XDG_CONFIG_HOME="$config_home" \
    XE_DATA_DIR="$temp_dir/replacement data $systemctl_failure" \
    "$installer" "${common[@]}" --autostart --install-dir "$stable_dir"
  assert_contains "$command_output" 'Prior autostart files and enabled state were restored'
  [[ "$(sha256sum "$unit")" == "$old_unit_hash" && "$(sha256sum "$launcher")" == "$old_launcher_hash" \
      && "$(sha256sum "$fallback_mode")" == "$old_fallback_hash" ]] \
    || fail "autostart $systemctl_failure rollback did not restore prior bytes"
  [[ "$(cat "$autostart_state")" == enabled ]] || fail "autostart $systemctl_failure did not restore enabled state"
  if find "$config_home/systemd/user" -maxdepth 1 -name '.xe-autostart-transaction.*' -print -quit | grep -q .; then
    fail "autostart $systemctl_failure left transaction residue"
  fi
done

assert_status 14 env PATH="$autostart_bin:$PATH" XE_SYSTEMCTL_LOG="$autostart_log" XE_SYSTEMCTL_STATE="$autostart_state" \
  XE_SYSTEMCTL_FAIL_MODE=is-enabled XDG_CONFIG_HOME="$config_home" XE_DATA_DIR="$temp_dir/query failure data" \
  "$installer" "${common[@]}" --autostart --install-dir "$stable_dir"
assert_contains "$command_output" 'without ambiguity'
[[ "$(sha256sum "$unit")" == "$old_unit_hash" && "$(sha256sum "$launcher")" == "$old_launcher_hash" \
    && "$(sha256sum "$fallback_mode")" == "$old_fallback_hash" ]] \
  || fail 'is-enabled query error mutated autostart bytes'
[[ "$(cat "$autostart_state")" == enabled ]] || fail 'is-enabled query error changed enabled state'
if find "$config_home/systemd/user" -maxdepth 1 -name '.xe-autostart-transaction.*' -print -quit | grep -q .; then
  fail 'is-enabled query error created transaction residue'
fi

query_fail_config="$temp_dir/query-fail-config"
query_fail_state="$temp_dir/query-fail.state"
assert_status 14 env PATH="$autostart_bin:$PATH" XE_SYSTEMCTL_LOG="$autostart_log" XE_SYSTEMCTL_STATE="$query_fail_state" \
  XE_SYSTEMCTL_FAIL_MODE=is-enabled XDG_CONFIG_HOME="$query_fail_config" \
  "$installer" "${common[@]}" --autostart --install-dir "$stable_dir"
assert_contains "$command_output" 'without ambiguity'
[[ ! -e "$query_fail_config" ]] || fail 'fresh is-enabled query error mutated the autostart filesystem'

ambiguous_state="$temp_dir/ambiguous-systemctl.state"
printf '%s\n' static >"$ambiguous_state"
assert_status 14 env PATH="$autostart_bin:$PATH" XE_SYSTEMCTL_LOG="$autostart_log" XE_SYSTEMCTL_STATE="$ambiguous_state" \
  XDG_CONFIG_HOME="$config_home" XE_DATA_DIR="$temp_dir/ambiguous state data" \
  "$installer" "${common[@]}" --autostart --install-dir "$stable_dir"
assert_contains "$command_output" 'without ambiguity'
[[ "$(sha256sum "$unit")" == "$old_unit_hash" && "$(sha256sum "$launcher")" == "$old_launcher_hash" \
    && "$(sha256sum "$fallback_mode")" == "$old_fallback_hash" ]] \
  || fail 'unrecognized is-enabled state mutated autostart bytes'

fresh_fail_config="$temp_dir/config-fail"
fresh_fail_state="$temp_dir/fresh-fail.state"
assert_status 14 env PATH="$autostart_bin:$PATH" XE_SYSTEMCTL_LOG="$autostart_log" XE_SYSTEMCTL_STATE="$fresh_fail_state" \
  XE_SYSTEMCTL_FAIL_MODE=enable XDG_CONFIG_HOME="$fresh_fail_config" \
  "$installer" "${common[@]}" --autostart --install-dir "$stable_dir"
assert_contains "$command_output" 'Prior autostart files and enabled state were restored'
[[ ! -e "$fresh_fail_config/systemd/user/xe-local-ai-engine.service" \
    && ! -e "$fresh_fail_config/systemd/user/xe-local-ai-engine" ]] \
  || fail 'fresh autostart registration failure left files behind'
[[ ! -e "$fresh_fail_state" ]] || fail 'fresh autostart rollback did not restore absent state'

open_umask_config="$temp_dir/config-open-umask"
open_umask_state="$temp_dir/open-umask.state"
( umask 000; PATH="$autostart_bin:$PATH" XE_SYSTEMCTL_LOG="$autostart_log" XE_SYSTEMCTL_STATE="$open_umask_state" \
    XDG_CONFIG_HOME="$open_umask_config" "$installer" "${common[@]}" --autostart --install-dir "$stable_dir" >/dev/null )
open_umask_unit="$open_umask_config/systemd/user/xe-local-ai-engine.service"
open_umask_launcher_dir="$open_umask_config/systemd/user/xe-local-ai-engine"
[[ "$(stat -c '%a' "$open_umask_launcher_dir")" == 700 ]] || fail 'autostart launcher directory ignored explicit 0700 mode'
[[ "$(stat -c '%a' "$open_umask_launcher_dir/launch")" == 700 ]] || fail 'autostart launcher ignored explicit 0700 mode'
[[ "$(stat -c '%a' "$open_umask_unit")" == 600 ]] || fail 'autostart unit ignored explicit safe mode'
[[ "$(stat -c '%a' "$open_umask_launcher_dir/.xe-local-ai-engine-autostart")" == 600 ]] \
  || fail 'autostart ownership marker ignored explicit safe mode'
[[ "$(stat -c '%a' "$open_umask_config/systemd/user")" == 700 ]] \
  || fail 'new user systemd directory ignored explicit 0700 mode'

hostile_parent_config="$temp_dir/hostile-parent-config"
mkdir -p "$hostile_parent_config/systemd/user"
chmod 0777 "$hostile_parent_config/systemd/user"
assert_status 14 env PATH="$autostart_bin:$PATH" XE_SYSTEMCTL_LOG="$autostart_log" \
  XE_SYSTEMCTL_STATE="$temp_dir/hostile-parent.state" XDG_CONFIG_HOME="$hostile_parent_config" \
  "$installer" "${common[@]}" --autostart --install-dir "$stable_dir"
assert_contains "$command_output" 'must not be group- or other-writable'

for rollback_step in remove-launcher remove-unit restore-launcher restore-unit; do
  recovery_config="$temp_dir/recovery-$rollback_step"
  cp -a -- "$config_home" "$recovery_config"
  recovery_state="$temp_dir/recovery-$rollback_step.state"
  printf '%s\n' enabled >"$recovery_state"
  rm -f -- "$recovery_state.daemon-failed"
  assert_status 14 env PATH="$autostart_bin:$PATH" XE_SYSTEMCTL_LOG="$autostart_log" \
    XE_SYSTEMCTL_STATE="$recovery_state" XE_SYSTEMCTL_FAIL_MODE=daemon-reload \
    XE_TEST_FAIL_AUTOSTART_ROLLBACK_STEP="$rollback_step" XDG_CONFIG_HOME="$recovery_config" \
    XE_DATA_DIR="$temp_dir/recovery data $rollback_step" \
    "$installer" "${common[@]}" --autostart --install-dir "$stable_dir"
  assert_contains "$command_output" 'Recovery data retained at:'
  recovery_path="$(sed -n 's/.*Recovery data retained at: //p' <<<"$command_output" | tail -n1)"
  [[ -d "$recovery_path" && -f "$recovery_path/prior-unit" && -d "$recovery_path/prior-launcher" ]] \
    || fail "rollback $rollback_step did not retain and name its transaction recovery copies"
done

cleanup_config="$temp_dir/recovery-cleanup"
cp -a -- "$config_home" "$cleanup_config"
cleanup_state="$temp_dir/recovery-cleanup.state"
printf '%s\n' enabled >"$cleanup_state"
assert_status 14 env PATH="$autostart_bin:$PATH" XE_SYSTEMCTL_LOG="$autostart_log" \
  XE_SYSTEMCTL_STATE="$cleanup_state" XE_SYSTEMCTL_FAIL_MODE=daemon-reload \
  XE_TEST_FAIL_AUTOSTART_ROLLBACK_STEP=cleanup-transaction XDG_CONFIG_HOME="$cleanup_config" \
  XE_DATA_DIR="$temp_dir/recovery cleanup data" \
  "$installer" "${common[@]}" --autostart --install-dir "$stable_dir"
assert_contains "$command_output" 'Transaction cleanup incomplete; possible residue at:'
if grep -Fq 'Recovery data retained at:' <<<"$command_output"; then
  fail 'destructive transaction cleanup failure promised verified recovery data'
fi
cleanup_path="$(sed -n 's/.*possible residue at: //p' <<<"$command_output" | tail -n1)"
[[ -d "$cleanup_path" && -f "$cleanup_path/prior-unit" && -d "$cleanup_path/prior-launcher" \
    && ! -e "$cleanup_path/prior-launcher/launch" \
    && ! -e "$cleanup_path/prior-launcher/.xe-local-ai-engine-autostart" ]] \
  || fail 'cleanup-failure seam did not model partially destructive transaction removal'

combined_config="$temp_dir/recovery-combined"
combined_state="$temp_dir/recovery-combined.state"
assert_status 14 env PATH="$autostart_bin:$PATH" XE_SYSTEMCTL_LOG="$autostart_log" \
  XE_SYSTEMCTL_STATE="$combined_state" XE_SYSTEMCTL_FAIL_MODE=enable-and-disable \
  XE_TEST_FAIL_AUTOSTART_ROLLBACK_STEP=remove-launcher XDG_CONFIG_HOME="$combined_config" \
  "$installer" "${common[@]}" --autostart --install-dir "$stable_dir"
assert_contains "$command_output" 'Could not remove the newly created enabled link.'
assert_contains "$command_output" 'Could not remove the committed autostart launcher.'
assert_contains "$command_output" 'Transaction cleanup incomplete; possible residue at:'

unowned_config="$temp_dir/unowned-autostart"
mkdir -p "$unowned_config/systemd/user/xe-local-ai-engine"
printf '%s\n' '# foreign unit' >"$unowned_config/systemd/user/xe-local-ai-engine.service"
printf '%s\n' foreign >"$unowned_config/systemd/user/xe-local-ai-engine/launch"
assert_status 14 env PATH="$autostart_bin:$PATH" XE_SYSTEMCTL_LOG="$autostart_log" XE_SYSTEMCTL_STATE="$autostart_state" \
  XDG_CONFIG_HOME="$unowned_config" "$installer" "${common[@]}" --autostart --install-dir "$stable_dir"
assert_contains "$command_output" 'ownership marker'

linked_config="$temp_dir/linked-autostart"
mkdir -p "$linked_config/systemd/user" "$temp_dir/linked-launcher-target"
printf '%s\n' '# XE_LOCAL_AI_ENGINE_AUTOSTART=1' >"$linked_config/systemd/user/xe-local-ai-engine.service"
ln -s "$temp_dir/linked-launcher-target" "$linked_config/systemd/user/xe-local-ai-engine"
assert_status 14 env PATH="$autostart_bin:$PATH" XE_SYSTEMCTL_LOG="$autostart_log" XE_SYSTEMCTL_STATE="$autostart_state" \
  XDG_CONFIG_HOME="$linked_config" "$installer" "${common[@]}" --autostart --install-dir "$stable_dir"
assert_contains "$command_output" 'installer-owned directory'

chmod 0777 "$launcher"
assert_status 14 env PATH="$autostart_bin:$PATH" XE_SYSTEMCTL_LOG="$autostart_log" XE_SYSTEMCTL_STATE="$autostart_state" \
  XDG_CONFIG_HOME="$config_home" "$installer" "${common[@]}" --autostart --install-dir "$stable_dir"
assert_contains "$command_output" 'group- or other-writable'
chmod 0700 "$launcher"

assert_status 14 env XE_INSTALLER_LIBRARY_ONLY=1 DATA_DIR=$'/tmp/xe-data\ninvalid' XDG_CONFIG_HOME="$temp_dir/config-control" \
  bash -c 'source "$1"; register_autostart "$2"' _ "$installer" "$stable_dir/XE-stable.AppImage"
assert_contains "$command_output" 'unsupported control characters'

skill_root="$temp_dir/skill-archive/root-prefix/skills/xe-local-ai-engine"
mkdir -p "$skill_root/references" "$fixture/repos/w0rldx/XE-Local-AI-Engine.Source/zipball"
printf '%s\n' '# fixture skill' >"$skill_root/SKILL.md"
printf '%s\n' 'fixture reference' >"$skill_root/references/client.md"
python3 - "$temp_dir/skill-archive" "$fixture/repos/w0rldx/XE-Local-AI-Engine.Source/zipball/v1.0.0" <<'PY'
import os,sys,zipfile
root,out=sys.argv[1:]
with zipfile.ZipFile(out,'w') as archive:
 for base,dirs,files in os.walk(root):
  for name in files:
   path=os.path.join(base,name); archive.write(path,os.path.relpath(path,root))
PY
skill_home="$temp_dir/skill-home"
mkdir -p "$skill_home"
HOME="$skill_home" "$installer" "${common[@]}" --install-skill --install-dir "$stable_dir" >/dev/null
[[ -f "$skill_home/.claude/skills/xe-local-ai-engine/SKILL.md" ]] || fail 'Claude skill destination missing'
[[ -f "$skill_home/.agents/skills/xe-local-ai-engine/references/client.md" ]] || fail 'agent skill destination missing'
printf 'stale\n' >"$skill_home/.claude/skills/xe-local-ai-engine/stale.txt"
HOME="$skill_home" "$installer" "${common[@]}" --install-skill --install-dir "$stable_dir" >/dev/null
[[ ! -e "$skill_home/.claude/skills/xe-local-ai-engine/stale.txt" ]] || fail 'skill replacement retained stale files'
assert_contains "$(cat "$request_log")" '/zipball/v1.0.0'

printf 'old-claude\n' >"$skill_home/.claude/skills/xe-local-ai-engine/SKILL.md"
printf 'old-agent\n' >"$skill_home/.agents/skills/xe-local-ai-engine/SKILL.md"
assert_status 13 env HOME="$skill_home" XE_TEST_FAIL_SKILL_SECOND_SWAP=1 \
  "$installer" "${common[@]}" --install-skill --install-dir "$stable_dir"
assert_contains "$command_output" 'Prior skill destinations were restored'
[[ "$(cat "$skill_home/.claude/skills/xe-local-ai-engine/SKILL.md")" == old-claude ]] \
  || fail 'second skill swap failure did not restore Claude destination'
[[ "$(cat "$skill_home/.agents/skills/xe-local-ai-engine/SKILL.md")" == old-agent ]] \
  || fail 'second skill swap failure did not restore agent destination'
if find "$skill_home" -name '.xe-skill-staging.*' -o -name '.xe-skill-backup.*' | grep -q .; then
  fail 'successful skill rollback left staging or backup residue'
fi

assert_status 13 env HOME="$skill_home" XE_TEST_FAIL_SKILL_SECOND_SWAP=1 XE_TEST_FAIL_SKILL_RESTORE=1 \
  "$installer" "${common[@]}" --install-skill --install-dir "$stable_dir"
assert_contains "$command_output" 'Rollback failed; retained backup:'
retained_backup="$(sed -n 's/.*retained backup: //p' <<<"$command_output" | tail -n1)"
[[ -d "$retained_backup" && "$(cat "$retained_backup/SKILL.md")" == old-claude ]] \
  || fail 'restore failure did not retain and report the Claude backup'
[[ "$(cat "$skill_home/.agents/skills/xe-local-ai-engine/SKILL.md")" == old-agent ]] \
  || fail 'restore failure did not restore the unaffected agent destination'

python3 - "$fixture/repos/w0rldx/XE-Local-AI-Engine.Source/zipball/v1.0.0" traversal <<'PY'
import sys,zipfile
out,kind=sys.argv[1:]
with zipfile.ZipFile(out,'w') as archive:
 archive.writestr('prefix/skills/xe-local-ai-engine/SKILL.md','# skill')
 archive.writestr('prefix/skills/xe-local-ai-engine/../../escape.txt','unsafe')
PY
assert_status 13 env HOME="$skill_home" "$installer" "${common[@]}" --install-skill --install-dir "$stable_dir"
assert_contains "$command_output" 'one safe skills/xe-local-ai-engine tree'
[[ ! -e "$skill_home/escape.txt" ]] || fail 'skill traversal escaped its staging root'

python3 - "$fixture/repos/w0rldx/XE-Local-AI-Engine.Source/zipball/v1.0.0" <<'PY'
import stat,sys,zipfile
with zipfile.ZipFile(sys.argv[1],'w') as archive:
 archive.writestr('prefix/skills/xe-local-ai-engine/SKILL.md','# skill')
 link=zipfile.ZipInfo('prefix/skills/xe-local-ai-engine/references/link')
 link.create_system=3; link.external_attr=(stat.S_IFLNK | 0o777) << 16
 archive.writestr(link,'../../outside')
PY
assert_status 13 env HOME="$skill_home" "$installer" "${common[@]}" --install-skill --install-dir "$stable_dir"
assert_contains "$command_output" 'one safe skills/xe-local-ai-engine tree'

stale_data="$temp_dir/stale-data"
mkdir -p "$stale_data"
printf '%s\n' '{"version":"v1","url":"http://127.0.0.1:5199","mcpUrl":"http://127.0.0.1:5199/api/local/v1/mcp/server","dataDir":"'"$stale_data"'","pid":999999,"startedAtUtc":"2026-08-22T00:00:00Z"}' >"$stale_data/ready.json"
assert_status 1 env XE_INSTALLER_LIBRARY_ONLY=1 DATA_DIR="$stale_data" \
  bash -c 'source "$1"; ready_json_is_valid "$2" 999999' _ "$installer" "$stale_data/ready.json"

assert_status 1 env XE_INSTALLER_LIBRARY_ONLY=1 XE_DATA_DIR="$temp_dir/fuse-start-data" \
  XE_START_TIMEOUT_SECONDS=1 INSTALL_DIR="$temp_dir" \
  bash -c 'source "$1"; run_start "$2"' _ "$installer" "$fixture/assets/XE-fuse.AppImage"
assert_contains "$command_output" 'FUSE launch failed; retrying with APPIMAGE_EXTRACT_AND_RUN=1'

dry_actions="$temp_dir/dry-actions"
HOME="$dry_actions" XE_ADMIN_EMAIL=admin@localhost.test XE_ADMIN_PASSWORD=secret XE_AUTOSTART=1 \
  XE_INSTALL_SKILL=1 XE_START=1 "$installer" "${common[@]}" --setup --dry-run --install-dir "$temp_dir/dry-actions-install" >/dev/null
[[ ! -e "$dry_actions" && ! -e "$temp_dir/dry-actions-install" ]] || fail 'dry-run post-install flags mutated files'

assert_status 1 "$installer" --github-api-base http://example.com --dry-run
assert_contains "$command_output" 'must use HTTPS'
assert_status 1 "$installer" --github-api-base http://localhost.evil --dry-run
assert_contains "$command_output" 'must use HTTPS'
assert_status 1 "$installer" --github-api-base http://localhost@evil.test --dry-run
assert_contains "$command_output" 'must not contain URL user information'
assert_status 1 "$installer" --github-api-base https://user@example.com --dry-run
assert_contains "$command_output" 'must not contain URL user information'

fallback_output="$(XE_INSTALLER_LIBRARY_ONLY=1 bash -c 'source "$1"; run_appimage_with_fallback "$2" --serve' _ "$installer" "$fixture/assets/XE-fuse.AppImage")"
assert_contains "$fallback_output" 'fallback:--serve'

classifier_cases="$temp_dir/classifier-cases"
mkdir -p "$classifier_cases"
for text in 'fuse: failed to open /dev/fuse' 'dlopen(): error loading libfuse.so.2' \
  'fusermount: mount failed' 'fusermount3: mount failed'; do
  printf '%s\n' "$text" >"$classifier_cases/message"
  XE_INSTALLER_LIBRARY_ONLY=1 bash -c 'source "$1"; is_fuse_failure "$2"' _ "$installer" "$classifier_cases/message" \
    || fail "shared FUSE classifier rejected: $text"
done
for text in 'connection refused' 'state is confused' 'configuration rejected'; do
  printf '%s\n' "$text" >"$classifier_cases/message"
  if XE_INSTALLER_LIBRARY_ONLY=1 bash -c 'source "$1"; is_fuse_failure "$2"' _ "$installer" "$classifier_cases/message"; then
    fail "shared FUSE classifier accepted non-FUSE text: $text"
  fi
done

if grep -Fq -- '--appimage-version' "$installer"; then
  fail 'installer must not use --appimage-version as a FUSE proof'
fi

echo 'install.test.sh: PASS'
