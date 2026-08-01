# Worktree-safe Aspire lifecycle helpers

Use the repository wrappers rather than an unqualified `aspire start`, `aspire ps`, or
`aspire stop --all`:

```bash
scripts/dev-start.sh
scripts/dev-status.sh
scripts/dev-stop.sh
scripts/aspire-readiness-smoke.sh
```

All commands resolve the canonical AppHost path for the checkout that contains the script.
`dev-start.sh` always supplies `--apphost`, `--isolated`, and `--non-interactive`, so ports and
Aspire user secrets do not collide with another worktree. It launches the CLI in a captured session
and, on any CLI/registration/status failure or signal, identity-checks and removes survivors from
that session independently of Aspire's registry before attempting the normal scoped stop path.
Because `--non-interactive` rules out a prompt and `--isolated` rules out the shared user-secrets
store, `dev-start.sh` also seeds the AppHost's required `node-sqlite-key` parameter itself: it mints a
per-checkout, owner-only `XE-Local-AI-Engine.AppHost/.data/node.key` (base64 32 bytes, `.gitignore`d)
on first use, reuses it afterwards so encrypted dev data stays readable, and passes it as the
environment variable `Parameters__node-sqlite-key` — never on a command line.
`dev-status.sh` selects the same exact
AppHost from `aspire ps --format Json`, then emits only resource name/type/state/health and endpoint
URLs with query strings removed. It never prints resource environment, properties, connection
strings, or the dashboard login token.

`dev-stop.sh` sends an AppHost-qualified stop request and never uses `--all`. Before stopping, it
snapshots the selected AppHost, its exact-path Aspire ancestor, the Aspire 13.4 DCP sibling whose
command line contains the exact `--monitor <AppHost PID>` token pair, and their complete descendant
closure regardless of executable name. This preserves ownership across DCP children that use separate sessions/process groups
without sweeping another worktree merely because it shares a login/session SID. Every selected PID
is paired with its `/proc` start time and revalidated immediately before TERM/KILL, so PID reuse
cannot redirect cleanup. Aspire 13.4 survivors are terminated only from that snapshot. A descendant `llama-server` is therefore
provably owned and cleaned. Processes outside the selected graph—including another worktree's DCP
or a pre-existing managed `llama-server`—are untouched and do not make scoped teardown fail; success
proves only that the selected graph and exact registration are gone. Query failures or malformed Aspire JSON are errors, never
interpreted as "not running"; start, status, readiness, and stop all fail closed. `--dry-run` sends
no stop request.

`aspire-readiness-smoke.sh` refuses to reuse an already-running instance, starts a new isolated
instance, delegates readiness to `aspire wait app`, and traps every exit path through the scoped
stop helper. Override its default 180-second readiness budget with
`XE_ASPIRE_SMOKE_TIMEOUT_SECONDS`.

Set `XE_ASPIRE_APPHOST` only when intentionally targeting another explicit AppHost. The lifecycle
scripts never terminate a managed `llama-server` merely because it shares a per-user installation
directory; ownership must be present in the pre-stop ancestry snapshot.
