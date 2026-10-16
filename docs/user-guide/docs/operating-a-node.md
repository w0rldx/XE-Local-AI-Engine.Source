# Operating a node

The facts an operator needs in one place. Each section is a summary with a pointer; the linked page is the
authority.

## Where things live

Everything the node owns sits in one data folder, separate from the app folder. On Windows it is
`%LOCALAPPDATA%\XE-Local-AI-Engine`. On Linux it is `~/.local/share/XE-Local-AI-Engine`, or
`$XDG_DATA_HOME/XE-Local-AI-Engine` when that variable is set. Run one instance per data folder, and stop the app
before touching anything in it.

- `node.sqlite` is the database, `node.key` its encryption key, `node-settings.json` the stored settings.
- `models/` and the `llama.cpp/`, `stable-diffusion.cpp/`, `whisper.cpp/` folders hold the downloads.
- `logs/` holds the rolling node log and the startup logs. The Diagnostics page can bundle them for a report.

→ [Where your data lives](privacy-and-data.md#where-your-data-lives),
[Linux data directory](install-linux.md), [Log files](feedback.md#log-files)

## Exit codes

A node that cannot start safely stops with its own code, and codes 5, 8, 9 and 10 also print a hint on standard
error. Code 8 means the node key does not match the database: delete neither, restore the matching pair. Code 9 means
the database migration failed, or a staged snapshot restore could not be applied: follow the printed restore hint.

→ [Exit code table with the next step for each code](updating.md#when-the-node-refuses-to-start)

## Backups and restore

The node keeps database snapshots in `backups/` inside the data folder. One is taken automatically before an update
changes the database, and you can take one on the **Diagnostics** page. A restore is staged: the node stops, and the
next start puts the snapshot in place and keeps the replaced database as `node.sqlite.prerestore-<time>`.

- A snapshot holds the database only. It opens only with the same `node.key`, so back up the key and the settings
  file yourself.
- Scripted use: `GET` and `POST node/backups` list and take snapshots, and `POST node/backups/<name>/restore` restores
  one, all under `/api/local/v1`.

→ [Restoring a database snapshot](updating.md#restoring-a-database-snapshot)

## Going back to an older version

Migrations are forward-only. Back up the whole data folder first, because an older binary may not understand a
database a newer one already migrated. Before a downgrade, run the knowledge preflight with the app stopped
(`--knowledge-downgrade-preflight`), and export a consistent copy with `--knowledge-downgrade-export`. Never delete
`node.sqlite` or `node.key` as a downgrade technique.

→ [Going back to an older version](updating.md#going-back-to-an-older-version)

## What phones home

Nothing sends telemetry, and the node opens no connection to a control plane. What leaves the machine does so because
an operator enabled it: model downloads from Hugging Face, engine and update checks against GitHub, cloud and
OpenAI-compatible providers, outbound MCP servers, and web search and page fetches. With **Offline / Manual** chosen at
first run, the app makes none of the automatic ones. Which release channel a node follows is a node setting, not a
build-time fact.

→ [What connects to the internet](privacy-and-data.md#what-connects-to-the-internet),
[egress table](../../wiki/12-security-and-privacy.md#1-egress-invariant-the-node-has-no-control-plane-channel),
[ADR 0014, update channels](../../adr/0014-update-channels-and-development-builds.md)

## Settings and the API

Nearly every stored setting has a field on the Node settings page. The few that do not are listed in
[Stored settings with no Node settings form field](../../wiki/11-hosting-and-deployment.md#stored-settings-with-no-node-settings-form-field).
What outside clients can rely on from the local API is in [API stability](for-experienced-users.md#api-stability).

**[← Back to the main page](../README.md)**
