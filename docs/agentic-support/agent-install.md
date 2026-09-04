# Install and operate XE Local AI Engine with an external agent

This is the end-to-end hub for a same-machine external agent. XE Local AI Engine exposes
**Streamable HTTP only** at a loopback address; there is no stdio transport and no supported LAN
listener. The engine's [external-agent skill](../../skills/xe-local-ai-engine/SKILL.md) is distinct
from the in-app skills assigned to saved agents.

## 1. Prerequisites

- Windows x64 or Linux x64. macOS and ARM release assets are not shipped.
- Linux: `curl`, `jq`, `sha256sum`, and Python 3 when `--install-skill` is used. FUSE is preferred;
  a real AppImage launch failure is retried with `APPIMAGE_EXTRACT_AND_RUN=1`.
- Windows: x64 ASP.NET Core Runtime 10.0.11 or a newer .NET 10 servicing patch. The installer checks
  it and prints the official download URL plus a non-authoritative `winget` hint; it never elevates.
- A secret store for the administrator password and one-time `xemcp_...` key.

The scripts resolve the latest stable GitHub release by default. `--pre`/`-Pre` includes
prereleases; `--version VERSION`/`-Version VERSION` selects an exact tag (the `v` prefix is optional).
Every platform artifact is verified against the release's mandatory `CHECKSUMS.sha256`; a present
`RELEASE-MANIFEST.json` is also checked.

> **Published-release caveat:** the raw scripts on `main` install published release assets, not the
> source tree. Until a release containing Agentic Support is published, `--setup`, `--start`, or
> `--install-skill` can fail against an older asset. Pin a release known to contain these contracts.

## 2. Install, set up, start, and install the external-agent skill

Load the real values from a secret manager; do not place them in a committed script or process
argument.

### Linux

```bash
export XE_ADMIN_EMAIL='admin@example.test'
export XE_ADMIN_PASSWORD='<secret-from-your-store>'
curl -fsSL https://raw.githubusercontent.com/w0rldx/XE-Local-AI-Engine.Source/main/install.sh | \
  bash -s -- --setup --start --install-skill
```

### Windows PowerShell

```powershell
$env:XE_ADMIN_EMAIL = 'admin@example.test'
$env:XE_ADMIN_PASSWORD = '<secret-from-your-store>'
$env:XE_SETUP = '1'
$env:XE_START = '1'
$env:XE_INSTALL_SKILL = '1'
irm https://raw.githubusercontent.com/w0rldx/XE-Local-AI-Engine.Source/main/install.ps1 | iex
```

PowerShell parameters cannot bind through `irm ... | iex`. For that piped form, use `XE_PRE=1`,
`XE_VERSION=<tag>`, or `XE_AUTOSTART=1` for prerelease, pinned, or autostart selection. To use normal
parameters or interactive prompts, download and invoke the script directly:

```powershell
$script = Join-Path $env:TEMP 'install-xe-local-ai-engine.ps1'
irm https://raw.githubusercontent.com/w0rldx/XE-Local-AI-Engine.Source/main/install.ps1 -OutFile $script
& $script -Version 'v1.0.0-rc.1' -Setup -Start -Autostart -InstallSkill
```

Piped Bash and PowerShell installs have no usable prompt input. Set `XE_ADMIN_EMAIL` and
`XE_ADMIN_PASSWORD` before requesting setup, or use the downloaded direct-execution form in a TTY.

The password reaches the engine only through `XE_ADMIN_PASSWORD`, never argv. Existing setup is an
idempotent success and prints `XE_SETUP=already-configured`; it does not compare or change the
existing credentials. A new setup prints:

```text
XE_SETUP=created
XE_ADMIN_EMAIL=admin@example.test
```

After engine setup, the installer's `--setup` workflow mints exactly one `agentic` key and prints it
exactly once:

```text
XE_MCP_KEY=xemcp_...
```

Capture that line without logging it. The node persists only a digest; a lost key must be rotated.
The installer starts the engine detached with `--mcp-only`, validates the PID in canonical
`ready.json`, polls `/health/ready`, then emits the ready line and `XE_PID=<pid>`.

Default application directories:

| platform | application | data |
|---|---|---|
| Linux | `~/.local/share/XE-Local-AI-Engine-App` | `${XDG_DATA_HOME:-~/.local/share}/XE-Local-AI-Engine` |
| Windows | `%LOCALAPPDATA%\XE-Local-AI-Engine-App` | `%LOCALAPPDATA%\XE-Local-AI-Engine` |

Override the application directory with `--install-dir`/`-InstallDir` or `XE_INSTALL_DIR`; override
the data directory with an absolute `XE_DATA_DIR`. The installer refuses unsafe targets and replaces
only its owned application tree.

## 3. Run engine commands directly

Use the installed executable while the app is stopped:

```text
--setup [--admin-email <email>] [--admin-password-stdin]
--mcp-key <delegate|agentic>
--status [--json]
--mcp-only [--port <1-65535>]
--desktop [--no-browser] [--port <1-65535>]
```

`--admin-password <value>` exists for interactive compatibility but exposes the value in process
listings; automation must use `XE_ADMIN_PASSWORD` or `--admin-password-stdin`. One-shot commands exit
instead of starting the web host unless `--mcp-only` or `--desktop` is explicitly present.

There is one inbound key row. `--mcp-key delegate` or `--mcp-key agentic` atomically replaces the
previous key, prints one `XE_MCP_KEY=` line, and invalidates every client still using the old value.

## 4. Readiness and status

Desktop and MCP-only mode print exactly one unformatted line, in this key order:

```text
XE_READY=1 XE_VERSION=<semver> XE_URL=http://127.0.0.1:<port> XE_MCP_URL=<XE_URL>/api/local/v1/mcp/server XE_DATA_DIR=<absolute-path>
```

`<data-dir>/ready.json` is the canonical machine-readable source:

```json
{
  "version": "<semver>",
  "url": "http://127.0.0.1:<port>",
  "mcpUrl": "http://127.0.0.1:<port>/api/local/v1/mcp/server",
  "dataDir": "<absolute-path>",
  "pid": 12345,
  "startedAtUtc": "<ISO-8601>"
}
```

It is removed on graceful shutdown. Treat it as stale whenever `pid` is not live, and require
`GET <url>/health/ready` to return 200 before trusting it.

`--status --json` never starts the engine or creates the data directory. Its exact fields are:

```json
{"running":true,"version":"<semver>","url":"http://127.0.0.1:<port>","mcpUrl":"http://127.0.0.1:<port>/api/local/v1/mcp/server","dataDir":"<absolute-path>","setupRequired":false,"installKind":"velopack-managed"}
```

`version`, `url`, `mcpUrl`, and `setupRequired` can be `null` when no healthy process is available;
`installKind` is `velopack-managed` or `unmanaged`. Status exits 0 only when the process, health
endpoint, and anonymous auth-status probe agree that the node is running; otherwise it exits 1.

## 5. Connect one of six MCP clients

Export `XE_MCP_URL` from the ready line and load the key as `XE_MCP_TOKEN`. Use the exact six client
examples in [setup and connect](../../skills/xe-local-ai-engine/references/setup-and-connect.md) or the
expanded [MCP client runbook](../runbooks/connect-an-mcp-client-runbook.md). All use:

```text
Authorization: Bearer <xemcp_...>
```

Never commit the value. Current configuration sources are linked from the runbook; client formats
change independently of the engine, so re-check those official pages after upgrading a client.

For example, Claude Code can expand the URL and token from the environment in `.mcp.json`:

```json
{
  "mcpServers": {
    "xe-local-ai-engine": {
      "type": "http",
      "url": "${XE_MCP_URL}",
      "headers": { "Authorization": "Bearer ${XE_MCP_TOKEN}" },
      "timeout": 1800000
    }
  }
}
```

The other five client examples are Codex CLI, Cursor, VS Code/GitHub Copilot, OpenCode, and Gemini
CLI; use the linked runbook rather than translating syntax between clients.

## 6. First delegation and administration workflow

1. Call `get_status` (agentic only) and `list_models`.
2. If no runtime is installed, call `start_runtime_acquisition`, then poll
   `get_runtime_acquisition`.
3. If no suitable model is installed, call `start_model_pull`, poll `get_model_pull`, then call
   `set_default_model`.
4. Call `list_agents`; use `run_agent` for bounded synchronous work or `start_agent_run` with a fresh
   UUID followed by `get_agent_run` for durable work.
5. Use `list_workspaces` only when the seeded read-only Coder needs an operator-authorized workspace.

A `delegate` key sees exactly 8 shared tools. An `agentic` key sees all 23: those 8 plus 15
administration tools. The exact names, inputs, lifecycle values, and 17-field settings whitelist are
in the [MCP tools reference](../../skills/xe-local-ai-engine/references/mcp-tools.md).

## 7. Security model

- The listener and local API remain loopback-only. Agentic does not grant an Operator role, JWT,
  arbitrary REST access, a routable listener, or a general policy bypass.
- Agentic is trusted operator-equivalent only for the enumerated 23-tool inbound MCP surface.
- An agentic root run may receive its saved agent's complete allowed-tool set. Approval-required
  calls are auto-approved only after a strict metadata-only audit write. Audit failure blocks the
  call. Arguments, prompts, messages, tokens, passwords, full keys, and host paths are not audited or
  logged. Spawned children retain normal curated tools and do not inherit agentic elevation.
- A durable run accepted before key rotation retains the captured authority across disconnect,
  restart, and rotation. Cancel it explicitly if it must not execute.
- `CustomToolsEnabled` is excluded from the agentic settings whitelist.

For a client on another machine, keep the engine bound to loopback and use an operator-owned tunnel
whose engine-side connection terminates on loopback, for example SSH local forwarding. A same-host
reverse proxy makes every forwarded socket peer appear loopback and is unsupported; do not use it to
publish the local API. Tailscale may protect the outer path, but it must not change the engine into a
routable listener.

## 8. Rotation

Stop or leave the current process running and choose one documented path:

- stopped engine: `--mcp-key delegate` or `--mcp-key agentic`;
- running engine: Operator login, then `POST /api/local/v1/mcp/server-key` with
  `{"scope":"delegate"}` or `{"scope":"agentic"}`.

Capture the new plaintext once, update every client secret, then reconnect. There is no dual-valid
window. Previously admitted durable agentic runs keep their captured authority.

## 9. Autostart (opt-in only)

Pass `--autostart`/`-Autostart` explicitly. Without it, installation never registers background
startup. Linux installs the current-user `~/.config/systemd/user/xe-local-ai-engine.service` and
runs an installer-owned launcher with `--mcp-only`; Windows registers the current-user,
limited-run-level Scheduled Task `XE Local AI Engine` at logon through its own launcher. Both
launchers persist the resolved effective `XE_DATA_DIR` (including custom paths with spaces) but no
administrator password or MCP key. Paths containing control characters are rejected with exit 14.
On Linux, the launcher observes the actual AppImage startup: after a reported FUSE mount failure it
retries exactly once with `APPIMAGE_EXTRACT_AND_RUN=1` and remembers that mode for later starts;
other failures remain nonzero for systemd's `Restart=on-failure`. Neither platform elevates.
Installer updates are ownership-guarded and transactional: a registration failure restores the
previous launcher, unit or task definition, and enabled state.

Remove the registration before uninstalling:

```bash
set -euo pipefail
autostart_dir="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/xe-local-ai-engine"
unit="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/xe-local-ai-engine.service"
marker="$autostart_dir/.xe-local-ai-engine-autostart"
IFS= read -r unit_marker <"$unit"
[[ -f "$unit" && ! -L "$unit" && -d "$autostart_dir" && ! -L "$autostart_dir" \
  && -f "$autostart_dir/launch" && ! -L "$autostart_dir/launch" \
  && -f "$marker" && ! -L "$marker" \
  && "$unit_marker" == '# XE_LOCAL_AI_ENGINE_AUTOSTART=1' \
  && "$(cat -- "$marker")" == 'XE_LOCAL_AI_ENGINE_AUTOSTART=1' ]] \
  || { echo 'Refusing to remove unowned or linked autostart state.' >&2; exit 1; }
while IFS= read -r entry; do
  [[ ! -L "$entry" ]] || { echo "Refusing linked entry: $entry" >&2; exit 1; }
  case "${entry##*/}" in launch|.xe-local-ai-engine-autostart|appimage-extract-and-run) ;; \
    *) echo "Refusing unowned entry: $entry" >&2; exit 1 ;; esac
done < <(find "$autostart_dir" -mindepth 1 -maxdepth 1 -print)
systemctl --user disable --now xe-local-ai-engine.service
rm -- "$unit"
rm -- "$autostart_dir/launch"
rm -- "$marker"
[[ ! -e "$autostart_dir/appimage-extract-and-run" ]] || rm -- "$autostart_dir/appimage-extract-and-run"
rmdir -- "$autostart_dir"
systemctl --user daemon-reload
```

```powershell
$ErrorActionPreference = 'Stop'
$directory = Join-Path $env:LOCALAPPDATA 'XE-Local-AI-Engine-Autostart'
$marker = Join-Path $directory '.xe-local-ai-engine-autostart'
$directoryItem = Get-Item -LiteralPath $directory -Force
$markerItem = Get-Item -LiteralPath $marker -Force
if (($directoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    ($markerItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    (Get-Content -LiteralPath $marker -Raw).Trim() -cne 'XE_LOCAL_AI_ENGINE_AUTOSTART=1') {
    throw 'Refusing to remove unowned or reparse-point autostart state.'
}
foreach ($item in @(Get-ChildItem -LiteralPath $directory -Force)) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        ($item.Name -cne '.xe-local-ai-engine-autostart' -and $item.Name -cnotmatch '^launch-[a-f0-9]{64}\.ps1$')) {
        throw "Refusing unowned or reparse-point autostart entry: $($item.FullName)"
    }
}
$task = Get-ScheduledTask -TaskName 'XE Local AI Engine' -ErrorAction Stop
$actions = @($task.Actions)
$expectedHost = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if ($actions.Count -ne 1 -or $actions[0].Execute -cne $expectedHost -or
    $actions[0].Arguments -notmatch '(?:^|\s)-File\s+"([^"]+)"(?:\s|$)') {
    throw 'Refusing to remove a task without one explicit installer launcher action.'
}
$taskLauncher = [IO.Path]::GetFullPath($Matches[1])
if ([IO.Path]::GetDirectoryName($taskLauncher) -cne [IO.Path]::GetFullPath($directory) -or
    [IO.Path]::GetFileName($taskLauncher) -cnotmatch '^launch-[a-f0-9]{64}\.ps1$' -or
    -not (Test-Path -LiteralPath $taskLauncher -PathType Leaf)) {
    throw 'Refusing to remove a task that is not bound to an installer-owned launcher.'
}
Unregister-ScheduledTask -TaskName 'XE Local AI Engine' -Confirm:$false
Remove-Item -LiteralPath $directory -Recurse -Force
```

## 10. Upgrade and uninstall

Stop the running node before replacing its application tree. For autostart installs, stop the owning
user service or task:

```bash
systemctl --user stop xe-local-ai-engine.service
```

```powershell
Stop-ScheduledTask -TaskName 'XE Local AI Engine'
```

For a manually managed node, stop it through the terminal or process manager that owns it and wait
for that manager to report exit. The product does not expose a stop command. If the installer
started a detached process, first require `--status --json` to report a healthy node. Treat the PID
in `ready.json` as discovery evidence only: independently inspect that PID with the operating
system's process manager and confirm its executable identity and resolved path match the expected XE
Local AI Engine executable beneath the marker-owned application directory. Only then terminate it
through that process manager using a bounded wait. Never terminate a PID based on mutable
`ready.json` alone; if identity cannot be proved, recover the owning terminal/process manager rather
than guessing. Before replacement, require `--status --json` to report `running:false` and exit 1.

Re-run the installer to update atomically to the latest stable release, or pin
`--version`/`-Version`. Use `--pre`/`-Pre` only when prerelease acceptance is intended. Pass
`--start`/set `XE_START=1` only to restart a manually managed detached process after replacement.
For an autostart installation, pass `--autostart`/set `XE_AUTOSTART=1` to register and enable the
updated user service/task; that option does **not** start it. Start it explicitly after installation:

```bash
systemctl --user start xe-local-ai-engine.service
```

```powershell
Start-ScheduledTask -TaskName 'XE Local AI Engine'
```

Re-run `--install-skill` so client instructions stay version-matched.

The installer intentionally has no uninstall verb. The existing guarded uninstall helpers stop the
node and optionally remove its **data directory only**; they neither identify nor remove the
portable application directory. Run the appropriate helper as the same user who ran the app:

```bash
curl -fsSL https://raw.githubusercontent.com/w0rldx/XE-Local-AI-Engine.Source/main/publish/linux/uninstall-xe-local-ai-engine.sh | \
  bash -s -- --dry-run
```

```powershell
$script = Join-Path $env:TEMP 'uninstall-xe-local-ai-engine.ps1'
irm https://raw.githubusercontent.com/w0rldx/XE-Local-AI-Engine.Source/main/publish/windows/uninstall-xe-local-ai-engine.ps1 -OutFile $script
& $script -DryRun
```

Review the dry run, then repeat without `--dry-run`/`-DryRun` (use `--yes`/`-Yes` only when the data
deletion is intended, or `--keep-data`/`-KeepData` to retain it). Remove the separate installer-owned
application tree only after validating its exact ownership marker:

```bash
app_dir="${XE_INSTALL_DIR:-$HOME/.local/share/XE-Local-AI-Engine-App}"
marker="$app_dir/.xe-local-ai-engine-install"
test -d "$app_dir" && test ! -L "$app_dir" && \
  test "$(cat "$marker" 2>/dev/null)" = 'XE_LOCAL_AI_ENGINE_INSTALL=1' || {
    echo "Refusing to remove an unowned application directory: $app_dir" >&2
    exit 1
  }
rm -rf -- "$app_dir"
```

```powershell
$appDir = if ($env:XE_INSTALL_DIR) { $env:XE_INSTALL_DIR } else {
    Join-Path $env:LOCALAPPDATA 'XE-Local-AI-Engine-App'
}
$marker = Join-Path $appDir '.xe-local-ai-engine-install'
$item = Get-Item -LiteralPath $appDir -Force -ErrorAction Stop
if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
    (Get-Content -LiteralPath $marker -Raw).Trim() -cne 'XE_LOCAL_AI_ENGINE_INSTALL=1') {
    throw "Refusing to remove an unowned application directory: $appDir"
}
Remove-Item -LiteralPath $appDir -Recurse -Force
```

Remove `~/.claude/skills/xe-local-ai-engine` and
`~/.agents/skills/xe-local-ai-engine` separately if the external-agent skill is no longer wanted.

## 11. Exit codes and troubleshooting

### Engine one-shot commands

| code | meaning |
|---:|---|
| 0 | Success (including setup already configured). |
| 1 | Stopped/not running or unexpected failure. |
| 2 | Usage/argument error. |
| 3 | Validation failure (email, password, or scope). |
| 4 | Single-instance lease already held. |
| 5 | Setup/auth/credential command failure. |
| 6 | Requested port unavailable. |

### Installer

| code | meaning |
|---:|---|
| 0 | Success. |
| 1 | Generic/usage/post-start failure. |
| 2 | Unsupported platform/architecture or missing platform asset. |
| 3 | Missing or mismatched mandatory checksum (or conflicting manifest). |
| 4 | Network/download/release-resolution failure. |
| 10 | Windows ASP.NET Core runtime prerequisite missing. |
| 11 | Setup or agentic key generation failed. |
| 12 | Start/readiness timed out. |
| 13 | External-agent skill installation failed. |
| 14 | User-scoped autostart registration failed. |

Installer codes never propagate the engine's numeric code raw; diagnostics name the engine code and
return the installer category. For symptoms and recoveries, see the
[troubleshooting reference](../../skills/xe-local-ai-engine/references/troubleshooting.md).
