# Runbook — use the `xe` terminal client

> Audience: an operator who wants to chat with, or script against, a node running on the same machine, from a local
> terminal or an SSH session on that machine.

`xe` attaches to a node that is already running. It never starts the engine and cannot reach a node on another
machine: the node's API answers on loopback only. Reference (discovery, sign-in, token file, JSON schema, exit codes):
[Hosting & Deployment, "Terminal client (`xe`)"](../wiki/11-hosting-and-deployment.md#terminal-client-xe).

## 1. Get the binary

- **Windows.** `xe.exe` sits in the `current\` folder of the unpacked Portable ZIP, beside
  `XE-Local-AI-Engine.WindowsLauncher.exe`. It uses the same ASP.NET Core runtime the engine needs. Add that folder to
  `PATH`, or call it by full path.
- **Linux.** `xe` is a self-contained file inside the AppImage. Extract it once and put it on `PATH`; repeat after an
  update so the client matches the node:

  ```bash
  ./XE-Local-AI-Engine.AppImage --appimage-extract 'usr/bin/xe'
  install -m 0755 squashfs-root/usr/bin/xe ~/.local/bin/xe
  rm -rf squashfs-root
  ```

- **From source.** `dotnet publish XE-Local-AI-Engine.Cli -c Release -p:PublishProfile=linux-x64` (or `win-x64`).

`xe version` prints the client version; `xe status` prints the node's.

## 2. First run

1. Start the engine as usual (desktop, `--headless` or `--mcp-only`). It must have finished first-run setup; otherwise
   every command exits 4 and names the `--setup` step.
2. Run `xe status`. It finds the node through `ready.json` in the per-user data folder; pass `--url
   http://127.0.0.1:<port>` or set `XE_URL` when the node uses another data directory.
3. When the vault is locked (every engine start until someone unlocks it) or no session is stored yet, `xe` asks for
   the admin password on the terminal. It unlocks the vault, signs in, and keeps the session tokens in
   `~/.config/xe/tokens.json` (Windows: `%APPDATA%\xe\tokens.json`), readable by you only.
4. `xe models` and `xe agents` list what the node has; `xe chat --agent <name>` starts a conversation.

Inside `xe chat`: end a line with `\` to continue it, `/help` lists the slash commands, Ctrl-C during a reply cancels it
on the node, Ctrl-C at the prompt leaves. A tool that needs approval shows a prompt (approve once, deny, and approve for
the session when the tool allows it); an `ask_user` question shows its options plus a free-text answer.

## 3. Over SSH

`xe` works in an SSH session on the node's machine with no port forwarding: it talks to `127.0.0.1` on that machine.

- The interactive password prompt needs a terminal: use `ssh -t host xe chat`.
- A closed SSH connection does not stop the turn on the node. Reconnect and run
  `xe chat --conversation <id>` (ids: `/conversations` inside `xe chat`) to re-attach, including to a prompt that is
  still waiting. The node times a waiting prompt out after 10 minutes.
- Run `xe` on the node's machine. Running it elsewhere against a forwarded port is neither supported nor tested in this
  release.

## 4. Scripts and `ask --json`

- Give the password without a terminal through `XE_ADMIN_PASSWORD` or one line on standard input with
  `--password-stdin`. Never put it on the command line.
- In a throwaway context, `--no-token-file` keeps the session in memory only.
- `xe ask "<prompt>"` prints only the answer on standard output; thinking, tool activity and notices go to standard
  error. `--json` prints one JSON object per line and a final `summary` line.
- Without a terminal nothing is approved: an approval is denied and a question cancels the turn, and the exit code is
  6. Run such a turn in `xe chat` instead, or use an agent whose tools need no approval.

```bash
printf '%s\n' "$ADMIN_PASSWORD" | xe ask --password-stdin --json --agent Coder "List the largest source files" \
  | jq -r 'select(.type == "summary") | .text'
```

```bash
xe ask --json "Summarize the release notes" > turn.jsonl
case $? in
  0) jq -r 'select(.type == "summary") | .text' turn.jsonl ;;
  3) echo "the engine is not running" ;;
  4) echo "sign-in or vault problem: see the error line" ;;
  6) echo "the turn needed an approval or an answer" ;;
  *) jq -c 'select(.type == "error" or .type == "summary")' turn.jsonl ;;
esac
```

## 5. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Exit 3, "No running engine found" | The engine is not running for this data directory. Start it, or pass `--url`. |
| Exit 3, "is no longer running" | `ready.json` names a dead process. Start the engine again. |
| Exit 3, "certificate was refused" | The development host serves https with a self-signed certificate. Pass `--insecure-dev-cert`. |
| Exit 4, "no admin account yet" | Finish setup: run the engine with `--setup`, or use the web UI. |
| Exit 4, "vault is locked and no admin password was given" | Set `XE_ADMIN_PASSWORD`, pass `--password-stdin`, or run in a terminal. |
| Exit 2, "No agent '…'" | Check `xe agents`; pass the id when two agents share a name. |
| Exit 6 from `xe chat` | `xe chat` needs a terminal on both standard input and output; use `xe ask` in scripts. |
