#!/usr/bin/env bash
# Contract test for publish/linux/uninstall-xe-local-ai-engine.sh process selection. It runs the real
# script in --dry-run --keep-data mode against a fake process table, so nothing is stopped or deleted.

set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
SCRIPT="$ROOT/publish/linux/uninstall-xe-local-ai-engine.sh"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

DATA_HOME="$TMP/share"
DATA_DIR="$DATA_HOME/XE-Local-AI-Engine"
PROC="$TMP/proc"

# One fake pid per line: "<pid> <exe target>". readlink only needs the symlink, not the target.
fake_process() { mkdir -p "$PROC/$1/fd"; ln -s "$2" "$PROC/$1/exe"; }
# An open file descriptor: what the script reads to tell this data dir's shell and app from same-named ones.
fake_fd() { ln -s "$3" "$PROC/$1/fd/$2"; }
fake_process 300 "$DATA_DIR/whisper.cpp/whisper-server"
fake_process 301 "$DATA_DIR/python/cpython-3.12-linux/bin/python3.12"
fake_process 200 "/opt/xe/current/XE-Local-AI-Engine.Client"
fake_fd 200 3 "/dev/null"
fake_fd 200 7 "$DATA_DIR/instance.lock"
fake_process 100 "/opt/xe/current/XE-Local-AI-Engine.Desktop"
fake_fd 100 5 "$DATA_DIR/desktop-shell.lock"
# Same names, but hosts of ANOTHER data dir (a second checkout's dev host, another install): never stopped.
fake_process 500 "/repo/other-checkout/bin/XE-Local-AI-Engine.Client"
fake_fd 500 7 "$TMP/other-data/XE-Local-AI-Engine/instance.lock"
fake_process 501 "/opt/other/XE-Local-AI-Engine.Desktop"
fake_fd 501 5 "$TMP/other-data/XE-Local-AI-Engine/desktop-shell.lock"
fake_process 502 "/opt/xe/current/XE-Local-AI-Engine.Client"
fake_process 400 "/usr/bin/python3.12"
fake_process 401 "${DATA_DIR}-sibling/llama.cpp/llama-server"
fake_process 402 "/usr/local/bin/whisper-server"

output="$(HOME="$TMP/home" XDG_DATA_HOME="$DATA_HOME" XE_UNINSTALL_PROC_ROOT="$PROC" sh "$SCRIPT" --dry-run --keep-data)"
actual="$(printf '%s\n' "$output" | awk '$1 == "pid" { print $2 }' | paste -sd' ' -)"

# Shell first, then the app, then the data-dir-owned whisper-server and trainer interpreter; never a
# system Python, a sibling-prefixed directory, an unrelated whisper-server, or a same-named shell or app that
# does not hold this data dir's lease (500-502).
expected="100 200 300 301"
[[ "$actual" == "$expected" ]] || {
  printf 'FAIL: expected stop order "%s", got "%s"\nfull output:\n%s\n' "$expected" "$actual" "$output" >&2
  exit 1
}

echo "uninstall-linux.test.sh: PASS"
