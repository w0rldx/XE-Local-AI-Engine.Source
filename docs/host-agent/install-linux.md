# Linux Installation

Linux native installs ship HostAgent.Linux and the Tray only. There is no HostAgent.Windows and no WSL boundary.

## Supported targets

- Ubuntu 22.04 or newer.
- Debian 12 or newer.
- Fedora and SELinux-specific support are post-MVP.

## Identity model

The runtime identity is the desktop user who launches the Tray. This is intentional: `systemctl --user` controls the calling user's own service manager. A dedicated runtime user would require a broker or privileged service, which is out of scope.

| Concern | Native Linux value |
| --- | --- |
| HostAgent owner | Desktop user |
| Rootless Docker owner | Desktop user |
| HostAgent unit | `~/.config/systemd/user/xe-host-agent.service` or distro user-unit path installed by the package |
| Linger | Not enabled |
| Config | `$XDG_CONFIG_HOME/xe-host-agent/` |
| Runtime | `$XDG_RUNTIME_DIR/xe-host-agent/` |
| Logs/state | `$XDG_STATE_HOME/xe-host-agent/` |

User logoff stops the runtime. The user starts it again from the desktop/application-menu launcher.

## Package responsibilities

`preinst` runs as root and may:

1. Install the systemd user-unit template.
2. Extract verified static Docker and rootless-extra tarballs to `/usr/local/bin/`.
3. Install `xe-local-ai-engine.desktop`, `xe-local-ai-engine-log.desktop`, and icons.

`postinst` does not start user services, does not enable linger, and does not create an XDG autostart entry.

The package must not create a dedicated `xe-engine` user on native Linux. That user exists only inside the Windows-managed WSL distro.

## First launch flow

1. The user launches `XE-Local-AI-Engine`.
2. The Tray runs `systemctl --user --runtime daemon-reload` and `systemctl --user start xe-host-agent.service`.
3. HostAgent.Linux refuses to run as root and reports `LINUX_REFUSES_ROOT_RUNTIME` if misconfigured.
4. On first start, HostAgent.Linux runs `dockerd-rootless-setuptool.sh install` as the desktop user if needed.
5. HostAgent.Linux starts rootless Docker, creates `xe-engine-net`, starts Ollama and the Node Web Server, opens the Unix socket, and writes the admin token.
6. HostAgent.Linux pulls the bootstrap model before the Web Server connects to `WorkerHub`.
7. The Tray turns green.

## Runtime locations

| File | Location |
| --- | --- |
| Admin token | `$XDG_RUNTIME_DIR/xe-host-agent/admin-token` mode `0600` or libsecret when available |
| HMAC secret | `$XDG_RUNTIME_DIR/xe-host-agent/hmac-secret`, readable only by the desktop user and the Web Server container runtime identity through the documented shared-group/ACL pattern; bind-mounted read-only into the Web Server container |
| Runtime metadata | `$XDG_RUNTIME_DIR/xe-host-agent/runtime.json` |
| Logs | `$XDG_STATE_HOME/xe-host-agent/logs/` |
| Manifest | `$XDG_CONFIG_HOME/xe-host-agent/manifest.yaml` |

## Reproducible headless transcript

H1's acceptance criteria require an embedded transcript captured from a reproducible Ubuntu/Debian clean-install runner. That capture requires package artifacts and a runner script, which are not present in this checkout.

Expected runner shape:

```bash
# run from a clean Ubuntu 22.04/24.04 VM or container-capable CI VM
bash ci/host-agent/linux-clean-install.sh \
  --package ./artifacts/xe-local-ai-engine.deb \
  --transcript ./artifacts/linux-clean-install.transcript.txt
```

The transcript must end with equivalent evidence:

```text
Package install exit code: 0
Autostart guard: no XDG autostart, unit not enabled, linger disabled
User launch: desktop launcher invoked
systemctl --user is-active xe-host-agent.service: active
HostAgent admin status: state=running desired_state=running ollama=healthy web-server=healthy
WorkerHub: connected
Tray: green
```

Replace this section with the captured transcript before claiming H1 complete.

## Troubleshooting pointers

- If rootless Docker cannot start, verify `XDG_RUNTIME_DIR`, `newuidmap/newgidmap`, and the rootless Docker install output.
- If the Tray cannot start the unit, verify it is running as the desktop user and not through `sudo`.
- If logoff stopped the runtime, relaunch from the desktop/application menu; this is expected MVP behavior.
