# Tray Launcher

The Tray is a thin Avalonia desktop application. It is the user's local entry point and status surface, not a management UI. The Node Web Server serves the React Web UI for canonical management workflows.

## Launch modes

| Mode | Shortcut | Behavior |
| --- | --- | --- |
| Normal | `XE-Local-AI-Engine` | Starts/reattaches to HostAgent and shows only the tray icon. |
| Log mode | `XE-Local-AI-Engine — Log Mode` | Starts/reattaches to HostAgent and tails the rotating HostAgent log in a console/terminal. |

HostAgent always writes rotating log files. Log mode tails those files; it does not pipe HostAgent stdout/stderr and closing the log console does not stop HostAgent.

## Single-instance behavior

If the user launches the Tray a second time, the second instance must surface or focus the existing tray session rather than starting another HostAgent. Re-attachment checks the runtime metadata file, PID liveness, executable path, and executable SHA-256 before trusting an existing HostAgent.

## Menu items

| Menu item | Visible when | Action |
| --- | --- | --- |
| Open Web UI | Always | Opens the `webUiUrl` from `GET /status` in the default browser. |
| Stop Services | `desired_state=running` | Confirms, then calls `POST /shutdown` with the admin token. |
| Start Services | `desired_state=stopped` | Calls `POST /startup` with the admin token. |
| Restart Runtime | `desired_state=running` | Confirms, then calls `POST /restart` with the admin token. |
| Show Diagnostics | Always | Displays `GET /logs?tail=200`. |
| Quit Tray | Always | Exits only the Tray process. HostAgent keeps running. |

## Icon states

| State | Meaning |
| --- | --- |
| Green | Desired state is running and all key services are healthy. |
| Yellow | Desired state is running but one or more services are degraded or preparing. |
| Gray | User requested stopped state. |
| Red | Desired state is running but HostAgent/admin API is unreachable or failed. |

The tooltip should state the literal state, for example `Stopped by user — click Start Services to resume`.

## Admin API rules

- Read the bearer token from the per-OS secure store.
- Re-read the token after any `Unauthorized` response because HostAgent rotates it on restart.
- Never log the token.
- Send mutation requests only after user confirmation where required.
- `Quit Tray` must not call shutdown.

## Platform behavior

The Tray never talks to the central platform. Any platform online/offline state change is a result of the Node Web Server connecting or disconnecting from the existing `WorkerHub` channel.
