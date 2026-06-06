# Linux Uninstall

`XE-Local-AI-Engine.HostAgent.Linux/Packaging/uninstall-host-agent.sh` is the complete, install-type-aware teardown for a native or external (BYO) Linux install. It removes **only** what the product created and nothing the user owns.

The script is what a packaged DEB/RPM uninstall hook calls (with `-y`). It is also safe to run by hand.

## What it removes

| Target | Source / scope |
| --- | --- |
| User systemd unit | `${XDG_CONFIG_HOME:-~/.config}/systemd/user/xe-host-agent.service` (stop + disable + daemon-reload) |
| Owned containers | every `containers[].name` in the runtime manifest (`docker rm -f`) |
| Owned network | `containers[].network` (default `xe-engine-net`), only when we own containers |
| Owned models volume | named `volumes[].source` (e.g. `ollama-models`); skipped with `--keep-models` |
| Config dir | `${XDG_CONFIG_HOME:-~/.config}/xe-host-agent` (manifest); skipped with `--keep-data` |
| Runtime dir | `${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/xe-host-agent` (admin-token, hmac-secret, runtime.json); skipped with `--keep-data` |
| State dir | `${XDG_STATE_HOME:-~/.local/state}/xe-host-agent` (logs, node DB in external mode); skipped with `--keep-data` |
| Desktop launchers | `${XE_APPLICATIONS_DIR:-/usr/share/applications}/xe-local-ai-engine.desktop` and `-log.desktop` |
| Tray icon | `${XE_ICON_DIR:-/usr/share/icons/hicolor/256x256/apps}/xe-local-ai-engine.ico` |
| Static runtime binaries | `/usr/local/bin/dockerd-rootless*.sh`, `rootlesskit*`; **native only** and only with `--remove-runtime-binaries` (default OFF) |

## Ownership is manifest-derived

The Docker kill-list comes from the runtime manifest at `${XDG_CONFIG_HOME:-~/.config}/xe-host-agent/manifest.yaml` (or `runtime.json` for mode detection), exactly like the runtime's own `ContainerOwnership` check. It is **never** derived from a broad `docker ps` / `docker prune` / wildcard.

- Owned containers = every `containers[].name`.
- Owned network = `containers[].network` (default `xe-engine-net`).
- Owned volumes = `volumes[].source` values that are **not** absolute paths. An absolute source is a host bind mount the product never created, so it is never removed.

In **external** (BYO) mode this manifest scoping is the entire safety story: a user-owned container, volume, or network that is not in the manifest survives untouched, and the user's Docker daemon and Ollama are never stopped or removed. Static `/usr/local/bin` runtime binaries are never removed in external mode.

## Flags

| Flag | Effect |
| --- | --- |
| `--mode <auto\|native\|external>` | Install type. Default `auto`: manifest `runtimeMode` → `runtime.json` → `native` (with a printed assumption warning). |
| `-y`, `--yes` | Skip the typed confirmation (for automation / packaging). |
| `--keep-models` | Keep the owned models volume. |
| `--keep-data` | Keep the config / runtime / state directories (admin-token, hmac-secret, logs, manifest, node DB). |
| `--dry-run` | Print the removal inventory and exit without deleting anything. |
| `--remove-runtime-binaries` | Native only: also remove the static docker/rootless binaries from `/usr/local/bin`. Default OFF — the location is shared, and package-installed binaries are owned by dpkg/rpm, not this script. |
| `--help`, `-h` | Show help. |

### Environment overrides (mirror `install-user-unit.sh`)

| Variable | Default |
| --- | --- |
| `XE_APPLICATIONS_DIR` | `/usr/share/applications` |
| `XE_ICON_DIR` | `/usr/share/icons/hicolor/256x256/apps` |
| `XE_TRAY_EXECUTABLE` | `/usr/bin/xe-local-ai-engine-tray` |

## Confirmation gate

By default the script prints a full inventory — one line per target as `[remove]`, `[keep:flag]`, or `[absent]` — and then requires a typed `yes` before deleting anything. `--dry-run` prints the inventory and exits. `-y` / `--yes` skips the prompt for automation and the packaged uninstall hook.

## Usage

Inspect what would be removed (no changes):

```bash
bash XE-Local-AI-Engine.HostAgent.Linux/Packaging/uninstall-host-agent.sh --dry-run
```

Interactive full purge (prompts for `yes`):

```bash
bash XE-Local-AI-Engine.HostAgent.Linux/Packaging/uninstall-host-agent.sh
```

External (BYO) teardown, manifest-scoped, keep pulled models:

```bash
bash XE-Local-AI-Engine.HostAgent.Linux/Packaging/uninstall-host-agent.sh --mode external --keep-models
```

Unattended (packaging / automation):

```bash
bash XE-Local-AI-Engine.HostAgent.Linux/Packaging/uninstall-host-agent.sh -y
```

## Behavior notes

- **Idempotent and best-effort.** A missing target is reported as `[absent]` / `skipped`, not an error. After the confirmation gate, one failed step logs an `ERROR` and the run continues; the script exits non-zero only when a hard error occurred.
- **Docker order.** Containers are removed first, then the network, then volumes — `docker volume rm` fails while a container still references the volume.
- **Rootless Docker.** `DOCKER_HOST` defaults to `unix://${XDG_RUNTIME_DIR}/docker.sock` if it is not already set.
- **No secret values are read or logged.** The inventory prints paths only; the script deletes the secret files (admin token, HMAC secret, keys) but never opens them.
- **Package manager.** dpkg/rpm own package-installed files; the script does not fight the package manager. Removing those is the job of the DEB/RPM uninstall, which calls this script with `-y` for the user-scoped artifacts.

## Reinstall

After a full purge, reinstall from the package and relaunch the Tray (see [install-linux.md](install-linux.md)). The first launch recreates the user unit, rootless Docker network, containers, and pulls the bootstrap model.
