#!/usr/bin/env bash
set -euo pipefail

PACKAGE_PATH=""
TRANSCRIPT_PATH=""
TIMEOUT_SECONDS="900"
EXPECTED_SHA256=""
REQUIRE_PACKAGE_SIGNATURE="false"
ALLOW_APT_DEB_INSTALL="false"

AUTOSTART_PATTERN='XE[-_[:space:]]*Local[-_[:space:]]*AI[-_[:space:]]*Engine|xe[-_[:space:]]*host[-_[:space:]]*agent'

usage() {
  cat <<'EOF'
Usage:
  linux-clean-install.sh --package <path> --transcript <path> [options]

Required:
  --package <path>              Path to .deb or .rpm package
  --transcript <path>           Path to transcript output

Options:
  --timeout-seconds <seconds>   Timeout budget for package install. Default: 900
  --expected-sha256 <hash>      Optional expected SHA-256 hash for the package
  --require-package-signature   Require package signature validation where supported
  --allow-apt-deb-install       For .deb packages, use apt-get install with the local package path instead of dpkg -i
  --help, -h                    Show help

Run from a clean Ubuntu/Debian/Fedora/RHEL-like runner at the repository root.
The script records package installation and autostart-guard evidence.

Post-launch HostAgent/Tray health evidence must be appended by the clean-runner
harness after user-session launch.
EOF
}

log() {
  printf '[linux-clean-install] %s\n' "$*"
}

fail() {
  log "ERROR: $*"
  exit 1
}

require_command() {
  local command_name="$1"

  if ! command -v "${command_name}" >/dev/null 2>&1; then
    fail "Required command not found: ${command_name}"
  fi
}

is_positive_integer() {
  [[ "$1" =~ ^[1-9][0-9]*$ ]]
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --package)
      PACKAGE_PATH="${2:-}"
      shift 2
      ;;
    --transcript)
      TRANSCRIPT_PATH="${2:-}"
      shift 2
      ;;
    --timeout-seconds)
      TIMEOUT_SECONDS="${2:-900}"
      shift 2
      ;;
    --expected-sha256)
      EXPECTED_SHA256="${2:-}"
      shift 2
      ;;
    --require-package-signature)
      REQUIRE_PACKAGE_SIGNATURE="true"
      shift
      ;;
    --allow-apt-deb-install)
      ALLOW_APT_DEB_INSTALL="true"
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

[[ -n "${PACKAGE_PATH}" ]] || { echo "--package is required" >&2; exit 1; }
[[ -n "${TRANSCRIPT_PATH}" ]] || { echo "--transcript is required" >&2; exit 1; }
[[ -f "${PACKAGE_PATH}" ]] || { echo "Package not found: ${PACKAGE_PATH}" >&2; exit 1; }
is_positive_integer "${TIMEOUT_SECONDS}" || { echo "--timeout-seconds must be a positive integer" >&2; exit 1; }

mkdir -p "$(dirname "${TRANSCRIPT_PATH}")"

exec > >(tee "${TRANSCRIPT_PATH}") 2>&1

require_command realpath
require_command sha256sum
require_command timeout
require_command grep
require_command awk
require_command id
require_command uname
require_command date

PACKAGE_PATH="$(realpath "${PACKAGE_PATH}")"

assert_expected_hash() {
  if [[ -z "${EXPECTED_SHA256}" ]]; then
    log "Package SHA-256 validation: skipped"
    return 0
  fi

  local actual_hash
  actual_hash="$(sha256sum "${PACKAGE_PATH}" | awk '{print $1}')"

  log "Package SHA-256: ${actual_hash}"

  if [[ "${actual_hash,,}" != "${EXPECTED_SHA256,,}" ]]; then
    fail "Package SHA-256 mismatch. Expected '${EXPECTED_SHA256}', got '${actual_hash}'."
  fi

  log "Package SHA-256 validation: passed"
}

assert_package_signature() {
  if [[ "${REQUIRE_PACKAGE_SIGNATURE}" != "true" ]]; then
    log "Package signature validation: skipped"
    return 0
  fi

  case "${PACKAGE_PATH}" in
    *.rpm)
      require_command rpm

      log "RPM signature validation: running rpm --checksig"
      rpm --checksig "${PACKAGE_PATH}"

      log "RPM signature validation: passed"
      ;;
    *.deb)
      if command -v debsig-verify >/dev/null 2>&1; then
        log "DEB signature validation: running debsig-verify"
        debsig-verify "${PACKAGE_PATH}"
        log "DEB signature validation: passed"
      elif command -v dpkg-sig >/dev/null 2>&1; then
        log "DEB signature validation: running dpkg-sig --verify"
        dpkg-sig --verify "${PACKAGE_PATH}"
        log "DEB signature validation: passed"
      else
        fail "DEB signature validation requested, but neither debsig-verify nor dpkg-sig is installed."
      fi
      ;;
    *)
      fail "Unsupported package type for signature validation: ${PACKAGE_PATH}"
      ;;
  esac
}

collect_systemd_hits() {
  local scope="$1"
  local systemctl_args=()

  if [[ "${scope}" == "user" ]]; then
    systemctl_args=(--user)
  fi

  if ! command -v systemctl >/dev/null 2>&1; then
    return 0
  fi

  log "Autostart scan: systemd ${scope} unit files matching package pattern"

  systemctl "${systemctl_args[@]}" list-unit-files --no-pager --no-legend 2>/dev/null |
    grep -Eai "${AUTOSTART_PATTERN}" || true

  log "Autostart scan: systemd ${scope} enabled units matching package pattern"

  systemctl "${systemctl_args[@]}" list-unit-files --state=enabled --no-pager --no-legend 2>/dev/null |
    grep -Eai "${AUTOSTART_PATTERN}" || true
}

has_systemd_enabled_hit() {
  local scope="$1"
  local systemctl_args=()

  if [[ "${scope}" == "user" ]]; then
    systemctl_args=(--user)
  fi

  if ! command -v systemctl >/dev/null 2>&1; then
    return 1
  fi

  systemctl "${systemctl_args[@]}" list-unit-files --state=enabled --no-pager --no-legend 2>/dev/null |
    grep -Eaiq "${AUTOSTART_PATTERN}"
}

has_specific_user_unit_enabled() {
  if ! command -v systemctl >/dev/null 2>&1; then
    return 1
  fi

  systemctl --user is-enabled xe-host-agent.service >/dev/null 2>&1
}

has_linger_enabled() {
  if ! command -v loginctl >/dev/null 2>&1; then
    return 1
  fi

  loginctl show-user "$(id -un)" -p Linger 2>/dev/null |
    grep -q 'Linger=yes'
}

has_xdg_autostart_hit() {
  local autostart_dirs=(
    "${HOME}/.config/autostart"
    "/etc/xdg/autostart"
  )

  local autostart_dir

  for autostart_dir in "${autostart_dirs[@]}"; do
    if [[ -d "${autostart_dir}" ]] &&
      grep -RIEiq "${AUTOSTART_PATTERN}" "${autostart_dir}" 2>/dev/null; then
      return 0
    fi
  done

  return 1
}

print_xdg_autostart_hits() {
  local autostart_dirs=(
    "${HOME}/.config/autostart"
    "/etc/xdg/autostart"
  )

  local autostart_dir

  for autostart_dir in "${autostart_dirs[@]}"; do
    if [[ -d "${autostart_dir}" ]]; then
      grep -RIEin "${AUTOSTART_PATTERN}" "${autostart_dir}" 2>/dev/null || true
    fi
  done
}

has_systemd_unit_file_hit() {
  local unit_dirs=(
    "${HOME}/.config/systemd/user"
    "/etc/systemd/user"
    "/usr/lib/systemd/user"
    "/lib/systemd/system"
    "/etc/systemd/system"
    "/usr/lib/systemd/system"
  )

  local unit_dir

  for unit_dir in "${unit_dirs[@]}"; do
    if [[ -d "${unit_dir}" ]] &&
      find "${unit_dir}" -maxdepth 3 -type f,l -print 2>/dev/null |
        grep -Eaiq "${AUTOSTART_PATTERN}"; then
      return 0
    fi

    if [[ -d "${unit_dir}" ]] &&
      grep -RIEiq "${AUTOSTART_PATTERN}" "${unit_dir}" 2>/dev/null; then
      return 0
    fi
  done

  return 1
}

print_systemd_unit_file_hits() {
  local unit_dirs=(
    "${HOME}/.config/systemd/user"
    "/etc/systemd/user"
    "/usr/lib/systemd/user"
    "/lib/systemd/system"
    "/etc/systemd/system"
    "/usr/lib/systemd/system"
  )

  local unit_dir

  for unit_dir in "${unit_dirs[@]}"; do
    if [[ -d "${unit_dir}" ]]; then
      find "${unit_dir}" -maxdepth 3 -type f,l -print 2>/dev/null |
        grep -Eai "${AUTOSTART_PATTERN}" || true

      grep -RIEin "${AUTOSTART_PATTERN}" "${unit_dir}" 2>/dev/null || true
    fi
  done
}

assert_no_autostart() {
  local failed="false"

  collect_systemd_hits "user"
  collect_systemd_hits "system"

  if has_specific_user_unit_enabled; then
    log "Autostart guard failed: user unit xe-host-agent.service is enabled"
    failed="true"
  fi

  if has_systemd_enabled_hit "user"; then
    log "Autostart guard failed: matching enabled user systemd unit found"
    systemctl --user list-unit-files --state=enabled --no-pager --no-legend 2>/dev/null |
      grep -Eai "${AUTOSTART_PATTERN}" || true
    failed="true"
  fi

  if has_systemd_enabled_hit "system"; then
    log "Autostart guard failed: matching enabled system systemd unit found"
    systemctl list-unit-files --state=enabled --no-pager --no-legend 2>/dev/null |
      grep -Eai "${AUTOSTART_PATTERN}" || true
    failed="true"
  fi

  if has_linger_enabled; then
    log "Autostart guard failed: linger enabled for user $(id -un)"
    failed="true"
  fi

  if has_xdg_autostart_hit; then
    log "Autostart guard failed: XDG autostart entry found"
    print_xdg_autostart_hits
    failed="true"
  fi

  if has_systemd_unit_file_hit; then
    log "Autostart guard evidence: matching systemd unit file/template found"

    print_systemd_unit_file_hits

    # Package-installed unit templates are allowed for manual/user-launch startup.
    # The release-blocking autostart failure is an enabled unit, linger, or XDG
    # autostart entry; those checks remain strict above.
  fi

  if [[ "${failed}" == "true" ]]; then
    fail "Autostart guard failed: enabled systemd unit, linger, or XDG autostart entry found."
  fi

  log "Autostart guard: no enabled system/user unit, no linger, no XDG autostart"
}

run_with_timeout() {
  local description="$1"
  shift

  log "Running with timeout ${TIMEOUT_SECONDS}s: ${description}"

  set +e
  timeout --kill-after=30s "${TIMEOUT_SECONDS}s" "$@"
  local exit_code=$?
  set -e

  if [[ "${exit_code}" -eq 124 ]]; then
    fail "${description} timed out after ${TIMEOUT_SECONDS} seconds"
  fi

  if [[ "${exit_code}" -eq 137 ]]; then
    fail "${description} was killed after timeout grace period"
  fi

  if [[ "${exit_code}" -ne 0 ]]; then
    fail "${description} failed with exit code ${exit_code}"
  fi

  log "${description} exit code: 0"
}

install_deb_package() {
  if [[ "${ALLOW_APT_DEB_INSTALL}" == "true" ]]; then
    require_command sudo
    require_command apt-get

    # apt-get install with a local package path can resolve dependencies from configured repos.
    # This is less "raw package only" than dpkg -i, but often better for clean runners.
    run_with_timeout "DEB package install via apt-get" \
      sudo apt-get install -y "${PACKAGE_PATH}"
  else
    require_command sudo
    require_command dpkg

    # dpkg -i is stricter and does not auto-resolve missing dependencies.
    # Good when the clean-install contract expects the package to be self-contained
    # or the runner image to already contain all dependencies.
    run_with_timeout "DEB package install via dpkg" \
      sudo dpkg -i "${PACKAGE_PATH}"
  fi
}

install_rpm_package() {
  require_command sudo

  if command -v dnf >/dev/null 2>&1; then
    run_with_timeout "RPM package install via dnf" \
      sudo dnf install -y "${PACKAGE_PATH}"
  elif command -v yum >/dev/null 2>&1; then
    run_with_timeout "RPM package install via yum" \
      sudo yum install -y "${PACKAGE_PATH}"
  else
    require_command rpm

    run_with_timeout "RPM package install via rpm" \
      sudo rpm -Uvh "${PACKAGE_PATH}"
  fi
}

log "Started: $(date -Iseconds)"
log "Runner: $(uname -a)"
log "User: $(id -un)"
log "UID: $(id -u)"
log "Package: ${PACKAGE_PATH}"
log "Timeout budget seconds: ${TIMEOUT_SECONDS}"
log "Require package signature: ${REQUIRE_PACKAGE_SIGNATURE}"
log "Allow apt deb install: ${ALLOW_APT_DEB_INSTALL}"

assert_expected_hash
assert_package_signature

case "${PACKAGE_PATH}" in
  *.deb)
    install_deb_package
    ;;
  *.rpm)
    install_rpm_package
    ;;
  *)
    fail "Unsupported package type. Expected .deb or .rpm: ${PACKAGE_PATH}"
    ;;
esac

log "Package install exit code: 0"

assert_no_autostart

log "User launch: pending external desktop launcher invocation by clean-runner harness"
log "systemctl --user is-active xe-host-agent.service: pending post-launch status capture"
log "HostAgent admin status: pending post-launch status capture"
log "WorkerHub: pending post-launch status capture"
log "Tray: pending post-launch status capture"
log "Open Web UI: pending post-launch browser assertion"

log "Completed: $(date -Iseconds)"
