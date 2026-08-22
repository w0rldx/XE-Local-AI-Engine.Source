#!/usr/bin/env bash
# Install XE Local AI Engine from a verified GitHub release.

set -uo pipefail

readonly XE_REPOSITORY="w0rldx/XE-Local-AI-Engine.Source"
readonly DEFAULT_API_BASE="https://api.github.com"
readonly DEFAULT_DOWNLOAD_BASE="https://github.com"
readonly OWNERSHIP_MARKER=".xe-local-ai-engine-install"
readonly OWNERSHIP_VALUE="XE_LOCAL_AI_ENGINE_INSTALL=1"
readonly AUTOSTART_OWNERSHIP_MARKER=".xe-local-ai-engine-autostart"
readonly AUTOSTART_OWNERSHIP_VALUE="XE_LOCAL_AI_ENGINE_AUTOSTART=1"
readonly FUSE_FAILURE_PATTERN='(^|[^[:alnum:]_])(fuse|libfuse([.]so([.][0-9]+)*)?|fusermount[0-9]*)([^[:alnum:]_]|$)|appimage[^[:cntrl:]]*mount|squashfs[^[:cntrl:]]*mount'

log() { printf '%s\n' "$*" >&2; }
die() { local code="$1"; shift; log "ERROR: $*"; exit "$code"; }

usage() {
  cat <<'EOF'
Usage: install.sh [options]
  --version VERSION       Install an exact release (v prefix optional)
  --pre                   Include prereleases when selecting the latest release
  --install-dir DIR       Install directory
  --yes                   Non-interactive mode
  --github-token TOKEN    GitHub API token
  --dry-run               Resolve and print the plan without downloading or writing files
  --github-api-base URL   API override (HTTPS, or loopback HTTP for tests)
  --download-base URL     Download override (HTTPS, or loopback HTTP for tests)
  --setup                 Configure the administrator and generate an agentic MCP key
  --start                 Start the installed engine detached in MCP-only mode
  --autostart             Register user-scoped MCP-only autostart (opt-in)
  --install-skill         Install the bundled skill for Claude and agent clients
  --no-autostart          Keep autostart disabled (the default)
  -h, --help              Show this help

Exit codes: 0 success; 1 generic/usage; 2 unsupported platform or asset;
3 checksum mismatch; 4 network or release-not-found; 10-14 reserved feature failures.
EOF
}

normalize_tag() {
  case "$1" in
    v*) printf '%s\n' "$1" ;;
    *) printf 'v%s\n' "$1" ;;
  esac
}

validate_network_base() {
  local name="$1" url="$2"
  if [[ ! "$url" =~ ^(https?)://([^/]+)(/.*)?$ ]]; then
    die 1 "$name must be an absolute HTTP(S) URL."
  fi
  local scheme="${BASH_REMATCH[1]}" authority="${BASH_REMATCH[2]}"
  [[ "$authority" != *@* ]] || die 1 "$name must not contain URL user information."
  [[ "$authority" != \[* ]] || die 1 "$name does not support IPv6 authorities."
  local host="${authority%%:*}"
  [[ -n "$host" ]] || die 1 "$name has an empty hostname."
  if [[ "$scheme" == http && "$host" != localhost && "$host" != 127.0.0.1 ]]; then
    die 1 "$name must use HTTPS; loopback HTTP is allowed only for explicit tests."
  fi
  VALIDATED_URL_SCHEME="$scheme"
  VALIDATED_URL_HOST="$host"
}

rewrite_download_url() {
  local url="$1" base="$2"
  if [[ "$base" == "$DEFAULT_DOWNLOAD_BASE" ]]; then
    printf '%s\n' "$url"
    return
  fi
  local path="${url#*://}"
  path="/${path#*/}"
  printf '%s%s\n' "${base%/}" "$path"
}

curl_args_for_url() {
  local url="$1"
  CURL_SECURITY_ARGS=(--tlsv1.2)
  [[ "$url" == https://* ]] && CURL_SECURITY_ARGS=(--proto '=https' --tlsv1.2)
}

http_get() {
  local url="$1" output="$2" headers="$3" authenticate="${4:-true}"
  validate_network_base "request URL" "$url"
  curl_args_for_url "$url"
  local -a args=(-fsSL "${CURL_SECURITY_ARGS[@]}" -D "$headers" -o "$output")
  if [[ "$authenticate" == true && -n "$GITHUB_TOKEN" \
      && "$VALIDATED_URL_SCHEME" == https && "$VALIDATED_URL_HOST" == api.github.com ]]; then
    args+=(-H "Authorization: Bearer $GITHUB_TOKEN")
  fi
  curl "${args[@]}" "$url"
}

canonicalize_install_dir() {
  local requested="$1"
  [[ -n "$requested" ]] || die 1 "Install directory must not be empty."
  command -v readlink >/dev/null 2>&1 || die 1 "readlink is required to validate the install directory."
  local canonical home_canonical data_canonical
  canonical="$(readlink -m -- "$requested")" || die 1 "Could not canonicalize install directory $requested."
  home_canonical="$(readlink -m -- "${HOME:?HOME is required}")" || die 1 "Could not canonicalize HOME."
  data_canonical="$(readlink -m -- "${XDG_DATA_HOME:-$HOME/.local/share}/XE-Local-AI-Engine")" \
    || die 1 "Could not canonicalize the application data directory."
  [[ "$canonical" != / ]] || die 1 "Install directory must not be a filesystem root."
  [[ "$canonical" != "$home_canonical" ]] || die 1 "Install directory must not be the user home directory."
  [[ "$canonical" != "$data_canonical" \
      && "$canonical" != "$data_canonical/"* \
      && "$data_canonical" != "$canonical/"* ]] \
    || die 1 "Install directory must not be the app-owned data directory."
  INSTALL_DIR="$canonical"
}

install_dir_is_owned() {
  [[ -f "$1/$OWNERSHIP_MARKER" ]] \
    && [[ "$(cat "$1/$OWNERSHIP_MARKER" 2>/dev/null)" == "$OWNERSHIP_VALUE" ]]
}

install_dir_is_nonempty() {
  [[ -d "$1" ]] && [[ -n "$(find "$1" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)" ]]
}

assert_install_dir_safe_to_use() {
  [[ ! -e "$INSTALL_DIR" || -d "$INSTALL_DIR" ]] || die 1 "Install path exists and is not a directory: $INSTALL_DIR"
  if install_dir_is_nonempty "$INSTALL_DIR" && ! install_dir_is_owned "$INSTALL_DIR"; then
    die 1 "Refusing to replace non-empty directory without a valid $OWNERSHIP_MARKER marker: $INSTALL_DIR"
  fi
}

install_dir_is_complete() {
  local asset="$1"
  install_dir_is_owned "$INSTALL_DIR" \
    && [[ -f "$INSTALL_DIR/xe-install-version.txt" ]] \
    && [[ "$(cat "$INSTALL_DIR/xe-install-version.txt" 2>/dev/null)" == "$RESOLVED_VERSION" ]] \
    && [[ -f "$INSTALL_DIR/$asset" ]]
}

replace_install_from_artifact() {
  local artifact="$1" asset="$2"
  local parent stage backup=""
  parent="$(dirname "$INSTALL_DIR")"
  mkdir -p -- "$parent" || die 1 "Could not create install parent directory $parent."
  stage="$(mktemp -d "$parent/.xe-install-staging.XXXXXX")" || die 1 "Could not create installer staging directory."
  if ! cp -- "$artifact" "$stage/$asset" \
      || ! chmod +x "$stage/$asset" \
      || ! printf '%s\n' "$RESOLVED_VERSION" >"$stage/xe-install-version.txt" \
      || ! printf '%s\n' "$OWNERSHIP_VALUE" >"$stage/$OWNERSHIP_MARKER"; then
    rm -rf -- "$stage"
    die 1 "Could not prepare the staged installation."
  fi

  if [[ -d "$INSTALL_DIR" ]]; then
    if install_dir_is_nonempty "$INSTALL_DIR"; then
      install_dir_is_owned "$INSTALL_DIR" || die 1 "Refusing to replace an unowned install directory."
      backup="$parent/.xe-install-backup.$$.${RANDOM}"
      mv -T -- "$INSTALL_DIR" "$backup" || die 1 "Could not preserve the existing installation for rollback."
    else
      rmdir -- "$INSTALL_DIR" || die 1 "Could not remove the empty install directory."
    fi
  fi

  if [[ "${XE_TEST_FAIL_INSTALL_SWAP:-0}" == 1 ]] || ! mv -T -- "$stage" "$INSTALL_DIR"; then
    rm -rf -- "$stage" || die 1 "Install replacement failed and the staged payload could not be removed: $stage"
    [[ -z "$backup" ]] || mv -T -- "$backup" "$INSTALL_DIR" \
      || die 1 "Install replacement failed and rollback also failed; preserved backup: $backup"
    die 1 "Install replacement failed; the prior installation was restored."
  fi
  if [[ -n "$backup" ]] && ! rm -rf -- "$backup"; then
    die 1 "Installation succeeded but the owned rollback backup could not be removed: $backup"
  fi
}

resolve_release() {
  local scratch="$1"
  local response="$scratch/release.json" headers="$scratch/release.headers"
  local url
  if [[ -n "$VERSION" ]]; then
    VERSION="$(normalize_tag "$VERSION")"
    url="${API_BASE%/}/repos/$XE_REPOSITORY/releases/tags/$VERSION"
    if ! http_get "$url" "$response" "$headers"; then
      die 4 "No release found for tag $VERSION, or the GitHub API request failed."
    fi
  else
    url="${API_BASE%/}/repos/$XE_REPOSITORY/releases?per_page=100"
    local pages="$scratch/releases.jsonl"
    : >"$pages"
    while [[ -n "$url" ]]; do
      if ! http_get "$url" "$response" "$headers"; then
        if grep -qi '^x-ratelimit-remaining:[[:space:]]*0' "$headers" 2>/dev/null; then
          local reset
          reset="$(awk -F': ' 'tolower($1)=="x-ratelimit-reset" {gsub("\\r", "", $2); print $2}' "$headers")"
          die 4 "GitHub API rate limit exhausted (reset epoch: ${reset:-unknown}); use --github-token or XE_GITHUB_TOKEN."
        fi
        die 4 "GitHub API request failed: $url"
      fi
      jq -c '.[]' "$response" >>"$pages" || die 4 "GitHub API returned invalid release JSON."
      url="$(grep -i '^link:' "$headers" | grep -o '<[^>]*>; rel="next"' | head -n1 | sed 's/^<//; s/>; rel="next"$//' || true)"
    done
    if [[ "$INCLUDE_PRE" == true ]]; then
      jq -cs 'map(select(.draft == false)) | sort_by(.published_at) | reverse | .[0] // empty' "$pages" >"$response"
    else
      jq -cs 'map(select(.draft == false and .prerelease == false)) | sort_by(.published_at) | reverse | .[0] // empty' "$pages" >"$response"
    fi
    [[ -s "$response" ]] || die 4 "No matching release was found."
  fi
  jq -e 'type == "object" and (.tag_name | type == "string") and (.assets | type == "array")' "$response" >/dev/null \
    || die 4 "GitHub API returned an invalid release object."
  RELEASE_JSON="$response"
  RESOLVED_VERSION="$(jq -r '.tag_name' "$response")"
}

select_asset() {
  local suffix="$1" insensitive="${2:-false}"
  local filter
  if [[ "$insensitive" == true ]]; then
    filter="[.assets[] | select(.name | ascii_downcase | endswith(\"${suffix,,}\"))]"
  else
    filter="[.assets[] | select(.name | endswith(\"$suffix\"))]"
  fi
  local count
  count="$(jq "$filter | length" "$RELEASE_JSON")"
  [[ "$count" == 1 ]] || die 2 "Release $RESOLVED_VERSION must contain exactly one *$suffix asset (found $count)."
  ASSET_NAME="$(jq -r "$filter | .[0].name" "$RELEASE_JSON")"
  ASSET_URL="$(jq -r "$filter | .[0].browser_download_url" "$RELEASE_JSON")"
}

asset_url_by_name() {
  local name="$1"
  jq -r --arg name "$name" '.assets[] | select(.name == $name) | .browser_download_url' "$RELEASE_JSON" | head -n1
}

download_asset() {
  local url="$1" output="$2" headers="$3"
  url="$(rewrite_download_url "$url" "$DOWNLOAD_BASE")"
  validate_network_base "asset URL" "$url"
  http_get "$url" "$output" "$headers" false || die 4 "Download failed: $url"
}

verify_checksum() {
  local artifact="$1" checksum_file="$2" name="$3"
  local expected actual
  expected="$(awk -v target="./$name" '$2 == target {print tolower($1)}' "$checksum_file")"
  [[ -n "$expected" ]] || die 3 "CHECKSUMS.sha256 has no entry for ./$name."
  actual="$(sha256sum "$artifact" | awk '{print tolower($1)}')"
  [[ "$actual" == "$expected" ]] || die 3 "Checksum mismatch for $name (expected $expected, got $actual)."
  VERIFIED_HASH="$actual"
}

verify_optional_manifest() {
  local scratch="$1" manifest_url
  local manifest_file="$scratch/RELEASE-MANIFEST.json"
  manifest_url="$(asset_url_by_name 'RELEASE-MANIFEST.json')"
  if [[ -z "$manifest_url" || "$manifest_url" == null ]]; then
    log "WARNING: RELEASE-MANIFEST.json is absent; the mandatory checksum was still verified."
    return 0
  fi
  manifest_url="$(rewrite_download_url "$manifest_url" "$DOWNLOAD_BASE")"
  validate_network_base "manifest URL" "$manifest_url"
  if ! http_get "$manifest_url" "$manifest_file" "$scratch/manifest.headers" false; then
    log "WARNING: RELEASE-MANIFEST.json could not be downloaded; the mandatory checksum was still verified."
    return 0
  fi
  if ! jq -e --arg tag "$RESOLVED_VERSION" --arg name "$ASSET_NAME" --arg hash "$VERIFIED_HASH" \
      '.tag == $tag and any(.assets[]?; .name == $name and (.sha256 | ascii_downcase) == $hash)' \
      "$manifest_file" >/dev/null 2>&1; then
    die 3 "RELEASE-MANIFEST.json does not agree with the verified tag and asset checksum."
  fi
  local source_sha
  source_sha="$(jq -r '.sourceSha // empty' "$manifest_file" | cut -c1-12)"
  [[ -z "$source_sha" ]] || log "Verified: $RESOLVED_VERSION (commit $source_sha)"
}

warn_if_fuse_unavailable() {
  [[ -e /dev/fuse ]] && return 0
  log "FUSE was not detected; the AppImage was installed without executing it."
  log "Install libfuse2 (Ubuntu 24.04: libfuse2t64), or 'fuse' on Fedora/RHEL."
  log "A later launch can retry with APPIMAGE_EXTRACT_AND_RUN=1 when a FUSE failure is reported."
}

is_fuse_failure() {
  grep -Eqi "$FUSE_FAILURE_PATTERN" "$1"
}

run_appimage_with_fallback() {
  local appimage="$1"; shift
  local error_file
  error_file="$(mktemp)" || return 1
  if "$appimage" "$@" 2>"$error_file"; then
    rm -f "$error_file"
    return 0
  fi
  if is_fuse_failure "$error_file"; then
    log "FUSE launch failed; retrying with APPIMAGE_EXTRACT_AND_RUN=1."
    APPIMAGE_EXTRACT_AND_RUN=1 "$appimage" "$@"
    local status=$?
    rm -f "$error_file"
    return "$status"
  fi
  cat "$error_file" >&2
  rm -f "$error_file"
  return 1
}

resolve_data_dir() {
  local requested="${XE_DATA_DIR:-${XDG_DATA_HOME:-$HOME/.local/share}/XE-Local-AI-Engine}"
  DATA_DIR="$(readlink -m -- "$requested")" || die 1 "Could not resolve the engine data directory."
}

resolve_setup_credentials() {
  ADMIN_EMAIL="${XE_ADMIN_EMAIL:-}"
  ADMIN_PASSWORD="${XE_ADMIN_PASSWORD:-}"
  if [[ -z "$ADMIN_EMAIL" && "$NONINTERACTIVE" == false ]]; then
    read -r -p 'Administrator email: ' ADMIN_EMAIL
  fi
  if [[ -z "$ADMIN_PASSWORD" && "$NONINTERACTIVE" == false ]]; then
    read -r -s -p 'Administrator password: ' ADMIN_PASSWORD
    printf '\n' >&2
  fi
  [[ -n "$ADMIN_EMAIL" && -n "$ADMIN_PASSWORD" ]] \
    || die 11 "--setup requires XE_ADMIN_EMAIL and XE_ADMIN_PASSWORD in non-interactive mode."
}

sanitize_engine_diagnostic() {
  local text="$1" secret="${2:-}" line sanitized="" count=0 key
  while IFS= read -r line; do
    [[ "$line" != XE_SETUP=* && "$line" != XE_ADMIN_EMAIL=* && "$line" != XE_MCP_KEY=* ]] || continue
    [[ -z "$secret" ]] || line="${line//"$secret"/[REDACTED]}"
    while [[ "$line" =~ (xemcp_[^[:space:]]+) ]]; do
      key="${BASH_REMATCH[1]}"
      line="${line//"$key"/[REDACTED]}"
    done
    line="${line:0:500}"
    sanitized+="${sanitized:+ | }$line"
    count=$((count + 1))
    [[ "$count" -lt 5 ]] || break
  done <<<"$text"
  printf '%s' "${sanitized:-no sanitized engine diagnostic output}"
}

run_setup() {
  local exe="$1" setup_output key_output setup_status key_status diagnostic
  resolve_setup_credentials

  if setup_output="$(XE_ADMIN_EMAIL="$ADMIN_EMAIL" XE_ADMIN_PASSWORD="$ADMIN_PASSWORD" "$exe" --setup 2>&1)"; then
    setup_status=0
  else
    setup_status=$?
  fi
  if [[ "$setup_status" -ne 0 ]]; then
    diagnostic="$(sanitize_engine_diagnostic "$setup_output" "$ADMIN_PASSWORD")"
    die 11 "Engine --setup exited with engine code $setup_status: $diagnostic"
  fi
  local setup_count
  setup_count="$(grep -Ec '^XE_SETUP=(created|already-configured)$' <<<"$setup_output" || true)"
  [[ "$setup_count" -eq 1 ]] || die 11 "Engine --setup did not return exactly one valid XE_SETUP line."
  local setup_value email_count
  setup_value="$(grep -E '^XE_SETUP=(created|already-configured)$' <<<"$setup_output")"
  email_count="$(grep -Ec '^XE_ADMIN_EMAIL=[^[:cntrl:]]+$' <<<"$setup_output" || true)"
  if [[ "$setup_value" == XE_SETUP=created && "$email_count" -ne 1 ]] \
      || [[ "$setup_value" == XE_SETUP=already-configured && "$email_count" -ne 0 ]]; then
    die 11 "Engine --setup returned an invalid XE_ADMIN_EMAIL contract."
  fi
  printf '%s\n' "$setup_value"
  [[ "$email_count" -eq 0 ]] || grep -E '^XE_ADMIN_EMAIL=[^[:cntrl:]]+$' <<<"$setup_output"

  if key_output="$("$exe" --mcp-key agentic 2>&1)"; then
    key_status=0
  else
    key_status=$?
  fi
  if [[ "$key_status" -ne 0 ]]; then
    diagnostic="$(sanitize_engine_diagnostic "$key_output" "$ADMIN_PASSWORD")"
    die 11 "Engine --mcp-key agentic exited with engine code $key_status: $diagnostic"
  fi
  local key_count
  key_count="$(grep -Ec '^XE_MCP_KEY=xemcp_[^[:space:][:cntrl:]]+$' <<<"$key_output" || true)"
  [[ "$key_count" -eq 1 ]] || die 11 "Engine --mcp-key agentic did not return exactly one XE_MCP_KEY line."
  grep -E '^XE_MCP_KEY=xemcp_[^[:space:][:cntrl:]]+$' <<<"$key_output"
  ADMIN_PASSWORD=""
}

launch_detached() {
  local exe="$1" log_file="$2" extract_and_run="${3:-false}"
  if command -v setsid >/dev/null 2>&1; then
    if [[ "$extract_and_run" == true ]]; then
      APPIMAGE_EXTRACT_AND_RUN=1 setsid nohup "$exe" --mcp-only >"$log_file" 2>&1 </dev/null &
    else
      setsid nohup "$exe" --mcp-only >"$log_file" 2>&1 </dev/null &
    fi
  else
    if [[ "$extract_and_run" == true ]]; then
      APPIMAGE_EXTRACT_AND_RUN=1 nohup "$exe" --mcp-only >"$log_file" 2>&1 </dev/null &
    else
      nohup "$exe" --mcp-only >"$log_file" 2>&1 </dev/null &
    fi
  fi
  ENGINE_PID=$!
}

ready_json_is_valid() {
  local ready_file="$1" pid="$2"
  kill -0 "$pid" 2>/dev/null || return 1
  jq -e --argjson pid "$pid" --arg data "$DATA_DIR" '
    .pid == $pid and .dataDir == $data and
    (.version | type == "string" and length > 0) and
    (.startedAtUtc | type == "string" and length > 0) and
    (.url | type == "string" and test("^http://127\\.0\\.0\\.1:[0-9]+$")) and
    .mcpUrl == (.url + "/api/local/v1/mcp/server")
  ' "$ready_file" >/dev/null 2>&1
}

run_start() {
  local exe="$1" log_file ready_file deadline ready_url mcp_url version ready_line captured_count timeout
  local fuse_retried=false
  resolve_data_dir
  timeout="${XE_START_TIMEOUT_SECONDS:-60}"
  [[ "$timeout" =~ ^[0-9]+$ ]] || die 1 "XE_START_TIMEOUT_SECONDS must be a non-negative integer."
  log_file="${XE_START_LOG_FILE:-$INSTALL_DIR/xe-mcp-only.log}"
  ready_file="$DATA_DIR/ready.json"
  launch_detached "$exe" "$log_file" false
  deadline=$((SECONDS + timeout))
  while (( SECONDS <= deadline )); do
    if ! kill -0 "$ENGINE_PID" 2>/dev/null; then
      wait "$ENGINE_PID" 2>/dev/null || true
      if [[ "$fuse_retried" == false ]] && is_fuse_failure "$log_file"; then
        log "FUSE launch failed; retrying with APPIMAGE_EXTRACT_AND_RUN=1."
        fuse_retried=true
        launch_detached "$exe" "$log_file" true
        continue
      fi
      [[ "$fuse_retried" == false ]] \
        || die 1 "The AppImage still failed after the FUSE extract-and-run fallback. See $log_file."
      die 1 "The engine exited before readiness. See $log_file."
    fi
    if ready_json_is_valid "$ready_file" "$ENGINE_PID"; then
      ready_url="$(jq -r '.url' "$ready_file")"
      if curl -fsS --max-time 2 "$ready_url/health/ready" >/dev/null 2>&1; then
        version="$(jq -r '.version' "$ready_file")"
        mcp_url="$(jq -r '.mcpUrl' "$ready_file")"
        ready_line="XE_READY=1 XE_VERSION=$version XE_URL=$ready_url XE_MCP_URL=$mcp_url XE_DATA_DIR=$DATA_DIR"
        captured_count="$(grep -Fxc -- "$ready_line" "$log_file" 2>/dev/null || true)"
        if [[ "$captured_count" -eq 1 ]]; then
          grep -Fx -- "$ready_line" "$log_file"
        else
          printf '%s\n' "$ready_line"
        fi
        printf 'XE_PID=%s\n' "$ENGINE_PID"
        return 0
      fi
    fi
    sleep "${XE_START_POLL_SECONDS:-0.2}"
  done
  kill "$ENGINE_PID" 2>/dev/null || true
  die 12 "Engine did not produce live canonical ready.json evidence and a healthy /health/ready response within ${timeout}s."
}

systemd_quote() {
  local value="$1"
  value="${value//%/%%}"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  printf '"%s"' "$value"
}

validate_autostart_path() {
  local name="$1" value="$2"
  [[ -n "$value" && "$value" == /* ]] || die 14 "$name must be an absolute path."
  [[ ! "$value" =~ [[:cntrl:]] ]] || die 14 "$name contains unsupported control characters."
}

write_autostart_launcher() {
  local exe="$1" data_dir="$2" launcher="$3" launcher_dir temp_file quoted_exe quoted_data quoted_pattern
  launcher_dir="$(dirname -- "$launcher")"
  mkdir -p -- "$launcher_dir" || die 14 "Could not create the user autostart launcher directory."
  chmod 0700 "$launcher_dir" || die 14 "Could not secure the user autostart launcher directory."
  [[ ! -L "$launcher_dir" ]] || die 14 "The user autostart launcher directory must not be a symbolic link."
  temp_file="$(mktemp "$launcher_dir/.launch.XXXXXX")" || die 14 "Could not stage the user autostart launcher."
  printf -v quoted_exe '%q' "$exe"
  printf -v quoted_data '%q' "$data_dir"
  printf -v quoted_pattern '%q' "$FUSE_FAILURE_PATTERN"
  cat >"$temp_file" <<EOF
#!/usr/bin/env bash
set -uo pipefail
exe=$quoted_exe
export XE_DATA_DIR=$quoted_data
fuse_pattern=$quoted_pattern
mode_file=\$(dirname -- "\$0")/appimage-extract-and-run
engine_pid=""
classifier_pid=""
capture_dir=""

cleanup() {
  trap - EXIT HUP INT TERM
  [[ -z "\$engine_pid" ]] || kill -TERM "\$engine_pid" 2>/dev/null || true
  [[ -z "\$classifier_pid" ]] || kill -TERM "\$classifier_pid" 2>/dev/null || true
  [[ -z "\$classifier_pid" ]] || wait "\$classifier_pid" 2>/dev/null || true
  [[ -z "\$capture_dir" ]] || rm -rf -- "\$capture_dir"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

run_engine() {
  local extract="\$1" stderr_target="\${2:-}"
  if [[ "\$extract" == true ]]; then
    APPIMAGE_EXTRACT_AND_RUN=1 "\$exe" --mcp-only &
  elif [[ -n "\$stderr_target" ]]; then
    "\$exe" --mcp-only 2>"\$stderr_target" &
  else
    "\$exe" --mcp-only &
  fi
  engine_pid=\$!
  wait "\$engine_pid"
  local result=\$?
  engine_pid=""
  return "\$result"
}

if [[ -f "\$mode_file" ]]; then
  run_engine true
  exit \$?
fi

capture_dir=\$(mktemp -d "\${XDG_RUNTIME_DIR:-\${TMPDIR:-/tmp}}/xe-local-ai-engine-autostart.XXXXXX") || {
  printf '%s\n' 'XE Local AI Engine autostart could not create its diagnostic capture directory.' >&2
  exit 1
}
stderr_pipe="\$capture_dir/stderr.pipe"
fuse_seen="\$capture_dir/fuse-seen"
mkfifo -- "\$stderr_pipe" || exit 1
(
  window=""
  while true; do
    chunk=""
    IFS= read -r -N 4096 chunk
    read_status=\$?
    if [[ -n "\$chunk" ]]; then
      printf '%s' "\$chunk" >&2
      window="\${window}\${chunk,,}"
      [[ "\$window" =~ \$fuse_pattern ]] && : >"\$fuse_seen"
      window="\${window: -256}"
    fi
    [[ "\$read_status" -eq 0 ]] || break
  done <"\$stderr_pipe"
) &
classifier_pid=\$!
run_engine false "\$stderr_pipe"
status=\$?
wait "\$classifier_pid" 2>/dev/null || true
classifier_pid=""

if [[ "\$status" -ne 0 && -f "\$fuse_seen" ]]; then
  printf '%s\n' 'FUSE launch failed; retrying with APPIMAGE_EXTRACT_AND_RUN=1.' >&2
  temp_mode="\$mode_file.tmp.\$\$"
  : >"\$temp_mode" && chmod 0600 "\$temp_mode" && mv -f -- "\$temp_mode" "\$mode_file" || exit 1
  run_engine true
  exit \$?
fi

exit "\$status"
EOF
  chmod 0700 "$temp_file" || die 14 "Could not secure the user autostart launcher."
  mv -f -- "$temp_file" "$launcher" || die 14 "Could not install the user autostart launcher."
}

validate_existing_autostart() {
  local unit_file="$1" launcher_dir="$2" entry base owner mode first_line current_uid
  local marker="$launcher_dir/$AUTOSTART_OWNERSHIP_MARKER"
  current_uid="$(id -u)" || die 14 "Could not determine the current user for autostart validation."
  if [[ ! -e "$unit_file" && ! -e "$launcher_dir" && ! -L "$unit_file" && ! -L "$launcher_dir" ]]; then
    return 1
  fi
  [[ -f "$unit_file" && ! -L "$unit_file" ]] \
    || die 14 "Existing autostart unit is not a regular installer-owned file."
  [[ -d "$launcher_dir" && ! -L "$launcher_dir" ]] \
    || die 14 "Existing autostart launcher path is not an installer-owned directory."
  [[ -f "$marker" && ! -L "$marker" && "$(cat -- "$marker")" == "$AUTOSTART_OWNERSHIP_VALUE" ]] \
    || die 14 "Existing autostart launcher has no valid installer ownership marker."
  [[ -f "$launcher_dir/launch" && ! -L "$launcher_dir/launch" ]] \
    || die 14 "Existing autostart launcher is missing or unsafe."
  IFS= read -r first_line <"$unit_file" || true
  [[ "$first_line" == "# $AUTOSTART_OWNERSHIP_VALUE" ]] \
    || die 14 "Existing autostart unit has no valid installer ownership marker."
  for entry in "$unit_file" "$launcher_dir" "$marker" "$launcher_dir/launch"; do
    owner="$(stat -c '%u' -- "$entry")" || die 14 "Could not validate autostart ownership: $entry"
    [[ "$owner" == "$current_uid" ]] || die 14 "Existing autostart content is not owned by the current user: $entry"
    mode="$(stat -c '%a' -- "$entry")" || die 14 "Could not validate autostart permissions: $entry"
    (( (8#$mode & 0022) == 0 )) || die 14 "Existing autostart content is group- or other-writable: $entry"
  done
  while IFS= read -r entry; do
    base="$(basename -- "$entry")"
    case "$base" in
      launch|"$AUTOSTART_OWNERSHIP_MARKER") ;;
      appimage-extract-and-run) [[ -f "$entry" && ! -L "$entry" ]] \
        || die 14 "Existing AppImage fallback marker is unsafe." ;;
      *) die 14 "Existing autostart launcher directory contains unowned content: $base" ;;
    esac
    owner="$(stat -c '%u' -- "$entry")" || die 14 "Could not validate autostart ownership: $entry"
    [[ "$owner" == "$current_uid" ]] || die 14 "Existing autostart content is not owned by the current user: $entry"
    mode="$(stat -c '%a' -- "$entry")" || die 14 "Could not validate autostart permissions: $entry"
    (( (8#$mode & 0022) == 0 )) || die 14 "Existing autostart content is group- or other-writable: $entry"
  done < <(find "$launcher_dir" -mindepth 1 -maxdepth 1 -print)
  return 0
}

register_autostart() {
  local exe="$1" unit_dir unit_file launcher_dir launcher transaction stage enabled_output enabled_status state_restore_command unit_dir_mode
  local prior_enabled="" had_prior=false unit_dir_existed=false registration_error="" rollback_error=""
  local old_unit_moved=false unit_committed=false old_launcher_moved=false launcher_committed=false
  local manager_touched=false state_touched=false retain_transaction=false state_restored=false cleanup_attempted=false
  command -v systemctl >/dev/null 2>&1 || die 14 "systemctl is required for --autostart on Linux."
  [[ -n "${DATA_DIR:-}" ]] || resolve_data_dir
  validate_autostart_path "Engine executable" "$exe"
  validate_autostart_path "Engine data directory" "$DATA_DIR"
  unit_dir="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
  unit_file="$unit_dir/xe-local-ai-engine.service"
  launcher_dir="$unit_dir/xe-local-ai-engine"
  launcher="$launcher_dir/launch"
  validate_autostart_path "Autostart launcher" "$launcher"
  if [[ -e "$unit_dir" || -L "$unit_dir" ]]; then
    unit_dir_existed=true
    [[ -d "$unit_dir" && ! -L "$unit_dir" ]] \
      || die 14 "The user systemd directory must not be a symbolic link."
    [[ "$(stat -c '%u' -- "$unit_dir")" == "$(id -u)" ]] \
      || die 14 "The user systemd directory must be owned by the current user."
    unit_dir_mode="$(stat -c '%a' -- "$unit_dir")" \
      || die 14 "Could not validate the user systemd directory permissions."
    (( (8#$unit_dir_mode & 0022) == 0 )) \
      || die 14 "The user systemd directory must not be group- or other-writable."
  fi
  if validate_existing_autostart "$unit_file" "$launcher_dir"; then had_prior=true; fi
  enabled_output="$(systemctl --user is-enabled xe-local-ai-engine.service 2>&1)"
  enabled_status=$?
  enabled_output="${enabled_output%%$'\n'*}"
  case "$enabled_output:$enabled_status" in
    enabled:0) prior_enabled=enabled; state_restore_command=enable ;;
    disabled:1) prior_enabled=disabled; state_restore_command=disable ;;
    not-found:4) [[ "$had_prior" == false ]] \
      || die 14 "Owned autostart files exist, but systemd reported the unit as not found."
      prior_enabled=absent; state_restore_command=disable ;;
    *) die 14 "Could not determine the existing user systemd enabled state without ambiguity." ;;
  esac
  mkdir -p -- "$unit_dir" || die 14 "Could not create the user systemd directory."
  if [[ "$unit_dir_existed" == false ]]; then
    chmod 0700 "$unit_dir" || die 14 "Could not secure the user systemd directory."
  fi
  transaction="$(mktemp -d "$unit_dir/.xe-autostart-transaction.XXXXXX")" \
    || die 14 "Could not stage the autostart transaction."
  if [[ "${XE_TEST_FAIL_AUTOSTART_STEP:-}" == secure-transaction ]] || ! chmod 0700 "$transaction"; then
    rm -rf -- "$transaction"
    die 14 "Could not secure the autostart transaction. Prior autostart files and enabled state were restored."
  fi
  stage="$transaction/stage"
  if [[ "${XE_TEST_FAIL_AUTOSTART_STEP:-}" == stage-directory ]] \
      || ! mkdir -p -- "$stage/launcher" || ! chmod 0700 "$stage" "$stage/launcher"; then
    registration_error="Could not stage the autostart launcher directory."
  fi
  if [[ -z "$registration_error" ]] && { [[ "${XE_TEST_FAIL_AUTOSTART_STEP:-}" == stage-launcher ]] \
      || ! (write_autostart_launcher "$exe" "$DATA_DIR" "$stage/launcher/launch"); }; then
    registration_error="Could not stage the autostart launcher."
  fi
  if [[ -z "$registration_error" ]] && { [[ "${XE_TEST_FAIL_AUTOSTART_STEP:-}" == stage-marker ]] \
      || ! printf '%s\n' "$AUTOSTART_OWNERSHIP_VALUE" >"$stage/launcher/$AUTOSTART_OWNERSHIP_MARKER" \
      || ! chmod 0600 "$stage/launcher/$AUTOSTART_OWNERSHIP_MARKER"; }; then
    registration_error="Could not stage the autostart ownership marker."
  fi
  if [[ "$had_prior" == true && -f "$launcher_dir/appimage-extract-and-run" ]]; then
    if [[ -z "$registration_error" ]] && { [[ "${XE_TEST_FAIL_AUTOSTART_STEP:-}" == stage-fallback ]] \
        || ! cp -p -- "$launcher_dir/appimage-extract-and-run" "$stage/launcher/appimage-extract-and-run"; }; then
      registration_error="Could not preserve the AppImage fallback mode."
    fi
  fi
  if [[ -z "$registration_error" ]]; then
    if [[ "${XE_TEST_FAIL_AUTOSTART_STEP:-}" == stage-unit ]]; then
      registration_error="Could not stage the user systemd unit."
    elif ! cat >"$stage/unit" <<EOF
# $AUTOSTART_OWNERSHIP_VALUE
[Unit]
Description=XE Local AI Engine (MCP only)

[Service]
Type=simple
ExecStart=$(systemd_quote "$launcher")
Restart=on-failure

[Install]
WantedBy=default.target
EOF
    then
      registration_error="Could not stage the user systemd unit."
    elif ! chmod 0600 "$stage/unit"; then
      registration_error="Could not secure the staged user systemd unit."
    fi
  fi
  if [[ "$had_prior" == true ]]; then
    if [[ -z "$registration_error" ]] && { [[ "${XE_TEST_FAIL_AUTOSTART_STEP:-}" == backup-unit ]] \
        || ! cp -p -- "$unit_file" "$transaction/prior-unit"; }; then
      registration_error="Could not back up the existing autostart unit."
    fi
    if [[ -z "$registration_error" ]] && { [[ "${XE_TEST_FAIL_AUTOSTART_STEP:-}" == backup-launcher ]] \
        || ! cp -a -- "$launcher_dir" "$transaction/prior-launcher"; }; then
      registration_error="Could not back up the existing autostart launcher."
    fi
  fi
  if [[ -z "$registration_error" && "$had_prior" == true ]]; then
    if [[ "${XE_TEST_FAIL_AUTOSTART_STEP:-}" == swap-old-unit ]] \
        || ! mv -- "$unit_file" "$transaction/replaced-unit"; then
      registration_error="Could not preserve the current user systemd unit."
    else old_unit_moved=true; fi
  fi
  if [[ -z "$registration_error" ]]; then
    if [[ "${XE_TEST_FAIL_AUTOSTART_STEP:-}" == swap-new-unit ]] \
        || ! mv -- "$stage/unit" "$unit_file"; then
      registration_error="Could not install the user systemd unit."
    else unit_committed=true; fi
  fi
  if [[ -z "$registration_error" && "$had_prior" == true ]]; then
    if [[ "${XE_TEST_FAIL_AUTOSTART_STEP:-}" == swap-old-launcher ]] \
        || ! mv -- "$launcher_dir" "$transaction/replaced-launcher"; then
      registration_error="Could not preserve the current autostart launcher."
    else old_launcher_moved=true; fi
  fi
  if [[ -z "$registration_error" ]]; then
    if [[ "${XE_TEST_FAIL_AUTOSTART_STEP:-}" == swap-new-launcher ]] \
        || ! mv -- "$stage/launcher" "$launcher_dir"; then
      registration_error="Could not install the user autostart launcher."
    else launcher_committed=true; fi
  fi
  if [[ -z "$registration_error" ]]; then
    manager_touched=true
    if ! systemctl --user daemon-reload; then
      registration_error="Could not reload the user systemd manager."
    fi
  fi
  if [[ -z "$registration_error" ]]; then
    state_touched=true
    if ! systemctl --user enable xe-local-ai-engine.service; then
      registration_error="Could not enable the user systemd unit."
    fi
  fi
  if [[ -n "$registration_error" ]]; then
    if [[ "$prior_enabled" == absent && ( "$manager_touched" == true || "$state_touched" == true ) ]]; then
      if systemctl --user disable xe-local-ai-engine.service >/dev/null 2>&1; then
        state_restored=true
      else
        rollback_error="Could not remove the newly created enabled link."
        retain_transaction=true
      fi
    fi
    if [[ "$launcher_committed" == true ]]; then
      if [[ "${XE_TEST_FAIL_AUTOSTART_ROLLBACK_STEP:-}" == remove-launcher ]] \
          || ! rm -rf -- "$launcher_dir"; then
        rollback_error="${rollback_error:+$rollback_error }Could not remove the committed autostart launcher."
        retain_transaction=true
      fi
    fi
    if [[ "$old_launcher_moved" == true && ! -e "$launcher_dir" ]]; then
      if [[ "${XE_TEST_FAIL_AUTOSTART_ROLLBACK_STEP:-}" == restore-launcher ]] \
          || ! mv -- "$transaction/replaced-launcher" "$launcher_dir"; then
        rollback_error="${rollback_error:+$rollback_error }Could not restore the prior autostart launcher."
        retain_transaction=true
      fi
    fi
    if [[ "$unit_committed" == true ]]; then
      if [[ "${XE_TEST_FAIL_AUTOSTART_ROLLBACK_STEP:-}" == remove-unit ]] \
          || ! rm -f -- "$unit_file"; then
        rollback_error="${rollback_error:+$rollback_error }Could not remove the committed user systemd unit."
        retain_transaction=true
      fi
    fi
    if [[ "$old_unit_moved" == true && ! -e "$unit_file" ]]; then
      if [[ "${XE_TEST_FAIL_AUTOSTART_ROLLBACK_STEP:-}" == restore-unit ]] \
          || ! mv -- "$transaction/replaced-unit" "$unit_file"; then
        rollback_error="${rollback_error:+$rollback_error }Could not restore the prior user systemd unit."
        retain_transaction=true
      fi
    fi
    if [[ "$manager_touched" == true || "$state_touched" == true ]]; then
      systemctl --user daemon-reload >/dev/null 2>&1 \
        || rollback_error="${rollback_error:+$rollback_error }Could not reload restored unit state."
      if [[ "$state_restored" == false ]]; then
        systemctl --user "$state_restore_command" xe-local-ai-engine.service >/dev/null 2>&1 \
          || rollback_error="${rollback_error:+$rollback_error }Could not restore $prior_enabled state."
      fi
    fi
    [[ -z "$rollback_error" ]] || retain_transaction=true
    if [[ "$retain_transaction" == false ]]; then
      cleanup_attempted=true
      if [[ "${XE_TEST_FAIL_AUTOSTART_ROLLBACK_STEP:-}" == cleanup-transaction ]]; then
        rm -f -- "$transaction/prior-launcher/launch" \
          "$transaction/prior-launcher/$AUTOSTART_OWNERSHIP_MARKER"
        rollback_error="${rollback_error:+$rollback_error }Could not completely remove the autostart transaction."
        retain_transaction=true
      elif ! rm -rf -- "$transaction"; then
        rollback_error="${rollback_error:+$rollback_error }Could not completely remove the autostart transaction."
        retain_transaction=true
      fi
    fi
    if [[ -n "$rollback_error" ]]; then
      if [[ "$cleanup_attempted" == false && "$had_prior" == true \
          && -f "$transaction/prior-unit" && -f "$transaction/prior-launcher/launch" \
          && -f "$transaction/prior-launcher/$AUTOSTART_OWNERSHIP_MARKER" ]]; then
        die 14 "$registration_error Rollback failed: $rollback_error Recovery data retained at: $transaction"
      fi
      die 14 "$registration_error Rollback failed: $rollback_error Transaction cleanup incomplete; possible residue at: $transaction"
    fi
    die 14 "$registration_error Prior autostart files and enabled state were restored."
  fi
  rm -rf -- "$transaction" || die 14 "Autostart was registered, but transaction cleanup failed: $transaction"
  log "Autostart registered for the current user. To start without an active login, consider: loginctl enable-linger $USER"
}

install_skill_tree() {
  local scratch="$1" zip_file extract_dir
  zip_file="$scratch/source.zip"
  extract_dir="$scratch/skill-source"
  local zip_url="${API_BASE%/}/repos/$XE_REPOSITORY/zipball/$RESOLVED_VERSION"
  command -v python3 >/dev/null 2>&1 || die 13 "python3 is required for safe skill archive extraction."
  if ! http_get "$zip_url" "$zip_file" "$scratch/skill.headers"; then
    die 13 "Could not download the source archive for $RESOLVED_VERSION."
  fi
  mkdir -p -- "$extract_dir" || die 13 "Could not stage the skill."
  if ! python3 - "$zip_file" "$extract_dir" <<'PY'
import os, pathlib, shutil, stat, sys, zipfile
archive, target = sys.argv[1:]
with zipfile.ZipFile(archive) as source:
    entries = []
    roots = set()
    for item in source.infolist():
        path = pathlib.PurePosixPath(item.filename)
        if path.is_absolute() or '..' in path.parts or len(path.parts) < 4:
            if 'skills' in path.parts and 'xe-local-ai-engine' in path.parts:
                raise ValueError('unsafe skill archive path')
            continue
        try:
            index = path.parts.index('skills')
        except ValueError:
            continue
        if path.parts[index:index + 2] != ('skills', 'xe-local-ai-engine') or index != 1:
            continue
        mode = item.external_attr >> 16
        if stat.S_ISLNK(mode):
            raise ValueError('skill archive contains a symbolic link')
        roots.add(path.parts[0])
        entries.append((item, path.parts[index + 2:]))
    if len(roots) != 1 or not entries or not any(parts and parts[-1] == 'SKILL.md' for _, parts in entries):
        raise ValueError('skill subtree missing or ambiguous')
    root = pathlib.Path(target).resolve()
    for item, parts in entries:
        if not parts:
            continue
        destination = root.joinpath(*parts)
        if root not in destination.resolve().parents:
            raise ValueError('skill path escaped staging')
        if item.is_dir():
            destination.mkdir(parents=True, exist_ok=True)
        else:
            destination.parent.mkdir(parents=True, exist_ok=True)
            with source.open(item) as incoming, destination.open('wb') as outgoing:
                shutil.copyfileobj(incoming, outgoing)
PY
  then
    die 13 "The source archive did not contain one safe skills/xe-local-ai-engine tree."
  fi
  local -a destinations=(
    "$HOME/.claude/skills/xe-local-ai-engine"
    "$HOME/.agents/skills/xe-local-ai-engine"
  ) stages=("" "") backups=("" "") committed=(false false)
  local index destination parent transaction_error="" retained_backup=""

  # Prepare both copies before changing either destination.
  for index in 0 1; do
    destination="${destinations[$index]}"
    parent="$(dirname "$destination")"
    mkdir -p -- "$parent" || die 13 "Could not create skill destination $parent."
    [[ ! -L "$destination" ]] || die 13 "Refusing to replace symbolic-link skill destination $destination."
    [[ ! -e "$destination" || -d "$destination" ]] || die 13 "Refusing to replace non-directory skill destination $destination."
    stages[index]="$(mktemp -d "$parent/.xe-skill-staging.XXXXXX")" || die 13 "Could not stage skill destination."
    cp -a -- "$extract_dir/." "${stages[$index]}/" \
      || die 13 "Could not copy skill files into staging for $destination."
  done

  # Preserve both old trees before committing either new tree.
  for index in 0 1; do
    destination="${destinations[$index]}"
    if [[ -d "$destination" ]]; then
      parent="$(dirname "$destination")"
      backups[index]="$parent/.xe-skill-backup.$$.${RANDOM}.$index"
      if ! mv -T -- "$destination" "${backups[$index]}"; then
        transaction_error="Could not preserve existing skill destination $destination."
        break
      fi
    fi
  done

  if [[ -z "$transaction_error" ]]; then
    for index in 0 1; do
      destination="${destinations[$index]}"
      if [[ "$index" -eq 1 && "${XE_TEST_FAIL_SKILL_SECOND_SWAP:-0}" == 1 ]]; then
        transaction_error="Injected second skill destination swap failure."
        break
      fi
      if mv -T -- "${stages[$index]}" "$destination"; then
        committed[index]=true
        stages[index]=""
      else
        transaction_error="Could not replace skill destination $destination."
        break
      fi
    done
  fi

  if [[ -n "$transaction_error" ]]; then
    for index in 0 1; do
      destination="${destinations[$index]}"
      if [[ "${committed[$index]}" == true ]] && ! rm -rf -- "$destination"; then
        retained_backup="${backups[$index]:-$destination}"
        continue
      fi
      if [[ -n "${backups[$index]}" && -d "${backups[$index]}" ]]; then
        if [[ -z "$retained_backup" && "${XE_TEST_FAIL_SKILL_RESTORE:-0}" == 1 ]]; then
          retained_backup="${backups[$index]}"
          continue
        fi
        if ! mv -T -- "${backups[$index]}" "$destination"; then
          retained_backup="${backups[$index]}"
        fi
      fi
      [[ -z "${stages[$index]}" ]] || rm -rf -- "${stages[$index]}" \
        || retained_backup="${backups[$index]:-${stages[$index]}}"
    done
    [[ -z "$retained_backup" ]] \
      || die 13 "$transaction_error Rollback failed; retained backup: $retained_backup"
    die 13 "$transaction_error Prior skill destinations were restored."
  fi

  for index in 0 1; do
    [[ -z "${backups[$index]}" ]] || rm -rf -- "${backups[$index]}" \
      || die 13 "Skills were committed, but backup cleanup failed; retained backup: ${backups[$index]}"
  done
}

run_post_install_actions() {
  local exe="$1" scratch="$2"
  if [[ "$SETUP" == true || "$START" == true || "$AUTOSTART" == true ]]; then
    resolve_data_dir
    export XE_DATA_DIR="$DATA_DIR"
  fi
  [[ "$SETUP" == false ]] || run_setup "$exe"
  [[ "$INSTALL_SKILL" == false ]] || install_skill_tree "$scratch"
  [[ "$AUTOSTART" == false ]] || register_autostart "$exe"
  [[ "$START" == false ]] || run_start "$exe"
}

main() {
  VERSION="${XE_VERSION:-}"
  INCLUDE_PRE=false
  [[ "${XE_PRE:-0}" == 1 ]] && INCLUDE_PRE=true
  INSTALL_DIR="${XE_INSTALL_DIR:-${HOME:?HOME is required}/.local/share/XE-Local-AI-Engine-App}"
  GITHUB_TOKEN="${XE_GITHUB_TOKEN:-}"
  API_BASE="${XE_GITHUB_API_BASE:-$DEFAULT_API_BASE}"
  DOWNLOAD_BASE="${XE_DOWNLOAD_BASE:-$DEFAULT_DOWNLOAD_BASE}"
  DRY_RUN=false
  SETUP=false; [[ "${XE_SETUP:-0}" == 1 ]] && SETUP=true
  START=false; [[ "${XE_START:-0}" == 1 ]] && START=true
  AUTOSTART=false; [[ "${XE_AUTOSTART:-0}" == 1 ]] && AUTOSTART=true
  INSTALL_SKILL=false; [[ "${XE_INSTALL_SKILL:-0}" == 1 ]] && INSTALL_SKILL=true
  NONINTERACTIVE=false
  [[ ! -t 0 || -n "${CI:-}" || "${XE_NONINTERACTIVE:-0}" == 1 ]] && NONINTERACTIVE=true

  while [[ $# -gt 0 ]]; do
    case "$1" in
      --version) [[ $# -ge 2 ]] || die 1 "--version requires a value."; VERSION="$2"; shift 2 ;;
      --pre) INCLUDE_PRE=true; shift ;;
      --install-dir) [[ $# -ge 2 ]] || die 1 "--install-dir requires a value."; INSTALL_DIR="$2"; shift 2 ;;
      --yes) NONINTERACTIVE=true; shift ;;
      --github-token) [[ $# -ge 2 ]] || die 1 "--github-token requires a value."; GITHUB_TOKEN="$2"; shift 2 ;;
      --dry-run) DRY_RUN=true; shift ;;
      --github-api-base) [[ $# -ge 2 ]] || die 1 "--github-api-base requires a value."; API_BASE="$2"; shift 2 ;;
      --download-base) [[ $# -ge 2 ]] || die 1 "--download-base requires a value."; DOWNLOAD_BASE="$2"; shift 2 ;;
      --setup) SETUP=true; shift ;;
      --start) START=true; shift ;;
      --autostart) AUTOSTART=true; shift ;;
      --no-autostart) AUTOSTART=false; shift ;;
      --install-skill) INSTALL_SKILL=true; shift ;;
      --help|-h) usage; exit 0 ;;
      *) die 1 "Unknown option: $1" ;;
    esac
  done

  validate_network_base "GitHub API base" "$API_BASE"
  validate_network_base "download base" "$DOWNLOAD_BASE"
  canonicalize_install_dir "$INSTALL_DIR"
  assert_install_dir_safe_to_use
  [[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 ]] || die 2 "install.sh supports linux-x64 only."
  command -v curl >/dev/null 2>&1 || die 1 "curl is required."
  command -v jq >/dev/null 2>&1 || die 1 "jq is required."
  command -v sha256sum >/dev/null 2>&1 || die 1 "sha256sum is required."

  local scratch
  scratch="$(mktemp -d)" || die 1 "Could not create a temporary directory."
  SCRATCH_DIR="$scratch"
  trap 'rm -rf -- "$SCRATCH_DIR"' EXIT
  resolve_release "$scratch"
  select_asset '.AppImage'

  if [[ "$DRY_RUN" == true ]]; then
    printf 'XE_INSTALL_PLAN=1 XE_INSTALLED=%s XE_VERSION=%s XE_ASSET=%s\n' "$INSTALL_DIR" "$RESOLVED_VERSION" "$ASSET_NAME"
    return 0
  fi

  local exe="$INSTALL_DIR/$ASSET_NAME"
  if install_dir_is_complete "$ASSET_NAME"; then
    run_post_install_actions "$exe" "$scratch"
    printf 'XE_INSTALLED=%s XE_VERSION=%s XE_EXE=%s\n' "$INSTALL_DIR" "$RESOLVED_VERSION" "$exe"
    return 0
  fi

  local checksum_url artifact checksum_file
  checksum_url="$(asset_url_by_name 'CHECKSUMS.sha256')"
  [[ -n "$checksum_url" && "$checksum_url" != null ]] || die 3 "Release $RESOLVED_VERSION has no CHECKSUMS.sha256 asset."
  artifact="$scratch/$ASSET_NAME"
  checksum_file="$scratch/CHECKSUMS.sha256"
  download_asset "$ASSET_URL" "$artifact" "$scratch/asset.headers"
  download_asset "$checksum_url" "$checksum_file" "$scratch/checksum.headers"
  verify_checksum "$artifact" "$checksum_file" "$ASSET_NAME"
  verify_optional_manifest "$scratch"

  replace_install_from_artifact "$artifact" "$ASSET_NAME"
  warn_if_fuse_unavailable
  run_post_install_actions "$exe" "$scratch"
  printf 'XE_INSTALLED=%s XE_VERSION=%s XE_EXE=%s\n' "$INSTALL_DIR" "$RESOLVED_VERSION" "$exe"
}

[[ "${XE_INSTALLER_LIBRARY_ONLY:-0}" == 1 ]] || main "$@"
