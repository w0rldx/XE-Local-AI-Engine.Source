# dev-stop.sh — Reliable Aspire dev-stack teardown

## Why `aspire stop` does nothing

`aspire stop` returns `✅ stopped successfully` in ~0.1 s but kills **zero
processes** on this topology.  The root cause is that `dcp` (the Aspire
orchestrator) and all child processes are reparented under the user session
manager and detached from the AppHost's PPID subtree.  `aspire stop` signals
only the AppHost PID, which cannot propagate the signal to sibling DCP
processes or their dotnet/node children.

Upstream tracking issues: [microsoft/aspire#15806](https://github.com/microsoft/aspire/issues/15806),
[#8919](https://github.com/microsoft/aspire/issues/8919),
[#10377](https://github.com/microsoft/aspire/issues/10377).

### Alternative: `aspire run` auto-stop

Re-running `aspire run` (or `dotnet run` in the AppHost) will detect an
existing session and stop the previous instance before starting a new one.
This is a verify-worthy alternative if you want to restart immediately — but
it does not work when you only want to stop without restarting.

## Usage

```bash
# Stop the running dev stack (SIGTERM → 3 s grace → SIGKILL)
scripts/dev-stop.sh

# Preview what would be killed without sending any signals
scripts/dev-stop.sh --dry-run
```

## How it works

1. **Locate the AppHost** via `aspire ps --format Json` (falls back to plain
   `aspire ps` text, then `/proc` cmdline scan if aspire is unavailable).
2. **Build the kill list** from three sources:
   - AppHost PID and all PPID-descendants that match the process-name allowlist.
   - All processes in the same Linux session (SID) as the AppHost that match
     the allowlist — this catches `dcp` and its subtree, which are session
     siblings, not PPID-children.
   - Any `llama-server` whose `/proc/<pid>/exe` resolves to a path under
     `~/.local/share/XE-Local-AI-Engine/llama.cpp/` — Ollama's
     `/usr/lib/ollama/llama-server` is never touched.
3. **Print the kill list** (always, even without `--dry-run`).
4. **SIGTERM** all listed PIDs, wait 3 s, then **SIGKILL** any survivors.
   `llama-server` is killed via its process group (`kill -KILL -- -<pgid>`)
   since it is typically a group leader.
5. **Safety guards**: the current shell's entire ancestor chain is protected
   and never added to the kill list; any PID not on the process-name allowlist
   is skipped with a warning even if it shares the same session.
