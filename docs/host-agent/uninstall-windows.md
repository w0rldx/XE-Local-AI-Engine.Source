# Windows Uninstall

`XE-Local-AI-Engine.HostAgent.Windows/Packaging/Windows/uninstall-host-agent.ps1` is the complete, install-type-aware teardown for a Windows install. It removes **only** what the product created and nothing the user owns. It never touches the WSL feature/platform, the WSL kernel, or any other distro.

The script is what a packaged MSI uninstall hook calls (with `-Force`). It is also safe to run by hand.

## What it removes

| Target | Source / scope |
| --- | --- |
| Processes | `XE-Local-AI-Engine.Tray` and `XE-Local-AI-Engine.HostAgent.Windows` (stopped first; not-running is fine) |
| Managed WSL distro | `xe-engine-runtime` (`wsl --terminate` then `wsl --unregister`); skipped with `-KeepData`. Unregister deletes the distro VHDX = all in-distro Docker, containers, the `ollama-models` volume, pulled models, the node DB, and keys. **Managed mode only.** |
| Owned containers | every `containers[].name` in the runtime manifest (`docker rm -f`). **External mode only.** |
| Owned network | `containers[].network` (default `xe-engine-net`). **External mode only.** |
| Owned models volume | named `volumes[].source` (e.g. `ollama-models`); skipped with `-KeepModels`. **External mode only.** |
| Host-agent data root | `%ProgramData%\XE-Local-AI-Engine\host-agent\` — logs, `runtime.json`, `desired-state.json`, `secrets\admin-token.dpapi`, `wsl\`, `rootfs\`; skipped with `-KeepData` |
| Binaries | `%ProgramFiles%\XE-Local-AI-Engine\` (HostAgent.Windows + Tray) |
| Shortcuts | `XE-Local-AI-Engine.lnk` and `XE-Local-AI-Engine — Log Mode.lnk` in both the Start Menu folder and the common Desktop |

## Install-type behavior

Mode selects the runtime-teardown shape (the only branching axis):

- **managed** (Windows-WSL, the default): the runtime, all containers, the models volume, the node DB, and keys all live inside the `xe-engine-runtime` distro, so `wsl --unregister` removes them transitively. There is no host-side Docker teardown. `-KeepModels` alone has no separate effect in managed mode (a note is printed); `-KeepData` keeps the distro and therefore implies keeping models.
- **external** ("own runtime"): there is no managed distro. The script removes only manifest-owned Docker artifacts (containers → network → volumes) against the configured Docker endpoint, then the on-host data dirs, binaries, and shortcuts. The user's Docker daemon, their Ollama, and any non-owned container/network/volume are never touched.

## Ownership is manifest-derived

In **external** mode the Docker kill-list comes from the runtime manifest at `%ProgramData%\XE-Local-AI-Engine\host-agent\manifest.{yaml,yml,json}` (override with `-ManifestPath`), exactly like the runtime's own `ContainerOwnership` check. It is **never** derived from a broad `docker ps` / `docker prune` / wildcard.

- Owned containers = every `containers[].name`.
- Owned network = `containers[].network` (default `xe-engine-net`).
- Owned volumes = `volumes[].source` values that are **not** paths or drive-qualified. A path/bind-mount source is a host mount the product never created, so it is never removed.

This manifest scoping is the entire safety story for external mode: a user-owned container, volume, or network that is not in the manifest survives untouched. **Fail-closed:** if external mode is requested but no manifest can be found, the Docker teardown is skipped (the script cannot prove ownership) and the owned containers must be removed manually — it never falls back to a wildcard.

For **managed** mode no manifest is needed: the distro name, network, and models volume are fixed and verified.

## Flags

| Flag | Effect |
| --- | --- |
| `-Mode <auto\|managed\|external>` | Install type. Default `auto`: manifest `runtimeMode` → `managed` (with a printed assumption note). A manifest `native` mode maps to the `managed` teardown on Windows. |
| `-Force` | Skip the typed confirmation (for automation / the MSI uninstall hook). |
| `-KeepModels` | External mode: keep the owned `ollama-models` volume. Managed mode: no separate effect (models live inside the distro). |
| `-KeepData` | Keep the host-agent data root (admin token, logs, runtime files) and, in managed mode, the WSL distro (implies keeping models). |
| `-WhatIf` | Print the removal inventory and the per-target plan, and delete nothing (dry-run). |
| `-InstallDirectory <path>` | Override the binaries directory. Default `%ProgramFiles%\XE-Local-AI-Engine`. |
| `-ShortcutDirectory <path>` | Override the Start Menu shortcut folder. |
| `-DesktopDirectory <path>` | Override the Desktop shortcut folder. Default = common Desktop. |
| `-ManifestPath <path>` | External mode: explicit manifest to scope the Docker teardown. |

## Confirmation gate

By default the script prints a full inventory — one line per target as `[remove]`, `[keep:flag]`, or `[absent]` — and then requires a typed `yes` before deleting anything. Anything other than `yes` aborts with exit code `2` and removes nothing. `-WhatIf` prints the inventory plus the per-target plan and exits. `-Force` skips the prompt for automation and the packaged uninstall hook.

## Usage

Inspect what would be removed (no changes):

```powershell
pwsh -File XE-Local-AI-Engine.HostAgent.Windows\Packaging\Windows\uninstall-host-agent.ps1 -WhatIf
```

Interactive full purge (prompts for `yes`):

```powershell
pwsh -File XE-Local-AI-Engine.HostAgent.Windows\Packaging\Windows\uninstall-host-agent.ps1
```

External teardown, manifest-scoped, keep pulled models:

```powershell
pwsh -File XE-Local-AI-Engine.HostAgent.Windows\Packaging\Windows\uninstall-host-agent.ps1 -Mode external -KeepModels
```

Unattended (MSI uninstall hook / automation):

```powershell
pwsh -File XE-Local-AI-Engine.HostAgent.Windows\Packaging\Windows\uninstall-host-agent.ps1 -Force
```

## Behavior notes

- **Idempotent and best-effort.** A missing target is reported as `[absent]`, not an error. After the confirmation gate, one failed step logs an `ERROR` and the run continues; the script exits non-zero only when a hard error occurred (exit `1`), exits `2` on a declined confirmation, otherwise exits `0`.
- **WSL is never broadened.** Only the literal `xe-engine-runtime` is passed to `--unregister`, and only after a `wsl --list --quiet` existence check. The script never calls `wsl --uninstall`, never disables the WSL feature, and never touches another distro.
- **Docker order (external).** Containers are removed first, then the network, then volumes — `docker volume rm` fails while a container still references the volume.
- **No secret values are read or logged.** The inventory prints paths only; the script deletes the secret files (`admin-token.dpapi`, in-distro keys via unregister) but never opens them.
- **Empty-path guard.** No recursive delete runs against an unresolved/empty path.
- **`wsl.exe` blocked by policy (fallback).** If `wsl --unregister` fails (e.g. corporate policy blocks `wsl.exe`), the script logs the failure plus the exact manual command — `wsl --unregister xe-engine-runtime` — and continues with the host-side cleanup (data root, binaries, shortcuts).

## Reinstall

After a full purge, reinstall from the MSI and relaunch the Tray (see [install-windows.md](install-windows.md)). The first launch re-imports the `xe-engine-runtime` distro, runs the in-distro bootstrap, starts the containers, and pulls the bootstrap model.
