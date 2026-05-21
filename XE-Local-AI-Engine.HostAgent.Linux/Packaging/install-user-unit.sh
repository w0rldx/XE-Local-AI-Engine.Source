#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
UNIT_SOURCE="${SCRIPT_DIR}/systemd/xe-host-agent.service"
USER_SYSTEMD_DIR="${XDG_CONFIG_HOME:-${HOME}/.config}/systemd/user"
UNIT_TARGET="${USER_SYSTEMD_DIR}/xe-host-agent.service"
APPLICATIONS_DIR="${XE_APPLICATIONS_DIR:-/usr/share/applications}"
ICON_DIR="${XE_ICON_DIR:-/usr/share/icons/hicolor/256x256/apps}"
TRAY_EXECUTABLE="${XE_TRAY_EXECUTABLE:-/usr/bin/xe-local-ai-engine-tray}"
ICON_SOURCE="${XE_TRAY_ICON_SOURCE:-${SCRIPT_DIR}/../../XE-Local-AI-Engine.Tray/Assets/app-icon.ico}"
ICON_TARGET="${ICON_DIR}/xe-local-ai-engine.ico"

mkdir -p "${USER_SYSTEMD_DIR}"
install -m 0644 "${UNIT_SOURCE}" "${UNIT_TARGET}"
systemctl --user daemon-reload

mkdir -p "${APPLICATIONS_DIR}" "${ICON_DIR}"
install -m 0644 "${ICON_SOURCE}" "${ICON_TARGET}"

cat > "${APPLICATIONS_DIR}/xe-local-ai-engine.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=XE-Local-AI-Engine
Comment=Start and manage XE Local AI Engine from the tray
Exec=${TRAY_EXECUTABLE}
Icon=${ICON_TARGET}
Terminal=false
Categories=Utility;Development;
DESKTOP

cat > "${APPLICATIONS_DIR}/xe-local-ai-engine-log.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=XE-Local-AI-Engine — Log Mode
Comment=Start XE Local AI Engine tray with diagnostics log mode
Exec=${TRAY_EXECUTABLE} --log
Icon=${ICON_TARGET}
Terminal=false
Categories=Utility;Development;
DESKTOP

chmod 0644 "${APPLICATIONS_DIR}/xe-local-ai-engine.desktop" "${APPLICATIONS_DIR}/xe-local-ai-engine-log.desktop"

cat <<'MESSAGE'
Installed xe-host-agent.service as a user unit.
The unit was not enabled and was not started.
Installed XE Local AI Engine application launchers.
The Tray is responsible for invoking: systemctl --user start xe-host-agent.service
MESSAGE
