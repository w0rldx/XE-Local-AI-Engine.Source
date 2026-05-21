# Windows Installation

This document describes the Windows managed runtime path for `XE-Local-AI-Engine`.

## Prerequisites

- Windows 11 with WSL2 support available by policy.
- Administrator approval for the MSI only. Runtime startup is non-elevated.
- Network access to pull the small bootstrap model and digest-pinned runtime images unless they are pre-seeded.

## Installer responsibilities

The MSI runs elevated once and performs the Windows-admin work:

1. Enable the WSL feature with `wsl --install --no-distribution` when needed.
2. Create `%PROGRAMDATA%\XE-Local-AI-Engine\host-agent\` with the documented ACL.
3. Drop HostAgent.Windows, Tray, rootfs tarball, scripts, manifests, and icons.
4. Create Start Menu and Desktop shortcuts:
   - `XE-Local-AI-Engine`
   - `XE-Local-AI-Engine — Log Mode`
5. Exit without starting any process.

The installer must not register a Windows service, scheduled task, Run-key, or background process.

## First launch flow

1. The user clicks `XE-Local-AI-Engine`.
2. The Tray starts as the current user and checks for an existing HostAgent runtime metadata file.
3. If no valid HostAgent is running, the Tray starts HostAgent.Windows detached.
4. HostAgent.Windows checks `wsl --status`. If WSL is blocked or unavailable, it reports an actionable diagnostic and exits.
5. HostAgent.Windows verifies the bundled rootfs SHA-256 and imports `xe-engine-runtime` using WSL2.
6. HostAgent.Windows runs the privileged in-distro bootstrap as root, terminates the distro at the phase boundary, verifies systemd readiness, then runs the unprivileged runtime install as `xe-engine`.
7. HostAgent.Linux starts rootless Docker, the Ollama container, and the Node Web Server container.
8. HostAgent.Linux pulls the bootstrap model.
9. The Node Web Server connects to `WorkerHub` only after `bootstrapModelReady=true`.
10. The Tray icon turns green and `Open Web UI` opens the Blazor Manager UI.

## Runtime files

| Path | Purpose |
| --- | --- |
| `%PROGRAMDATA%\XE-Local-AI-Engine\host-agent\runtime.json` | PID, admin port, executable path/hash, token generation, session id. |
| `%PROGRAMDATA%\XE-Local-AI-Engine\host-agent\logs\` | Rotating HostAgent logs. |
| `%PROGRAMDATA%\XE-Local-AI-Engine\host-agent\rootfs\` | Verified Ubuntu rootfs tarball. |

HostAgent removes `runtime.json` on graceful shutdown. A stale file after a crash is ignored unless PID, path, and executable SHA-256 all match.

## Reproducible headless transcript

H1's acceptance criteria require an embedded transcript captured from a reproducible Windows 11 headless runner. That capture requires the VM/packer/Pester runner and installer artifacts, which are not present in this checkout.

Expected runner shape:

```powershell
# run from a clean Windows 11 VM image
pwsh .\ci\host-agent\windows-clean-install.ps1 `
  -MsiPath .\artifacts\XE-Local-AI-Engine.msi `
  -TranscriptPath .\artifacts\windows-clean-install.transcript.txt
```

The transcript must end with equivalent evidence:

```text
MSI exit code: 0
Autostart guard: no service, no scheduled task, no Run-key
User launch: desktop shortcut invoked
HostAgent admin status: state=running desired_state=running ollama=healthy web-server=healthy
WorkerHub: connected
Tray: green
Open Web UI: browser launched Blazor URL
```

Replace this section with the captured transcript before claiming H1 complete.

## Troubleshooting pointers

- `WSL_BLOCKED_BY_POLICY`: collect `wsl --status` output and corporate policy diagnostics.
- `SYSTEMD_NOT_READY`: inspect the post-terminate systemd readiness checks.
- Tray red while desired state is running: read HostAgent logs and validate the bearer token store.
