#!/usr/bin/env bash
# Contract test for scripts/with-build-lock.sh. It runs the real script against a temporary lock
# file and trivial helper commands; nothing here builds or tests the product.

set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
WRAPPER="$ROOT/scripts/with-build-lock.sh"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
LOCK="$TMP/build.lock"

# The commands under the wrapper live in files rather than `bash -c` strings: a quoted -c body full
# of positional parameters is unreadable and hides its own quoting bugs.
write_helper() { local name="$1"; cat >"$TMP/$name"; chmod +x "$TMP/$name"; }

write_helper exit7.sh <<EOF
#!/usr/bin/env bash
exit 7
EOF

# Contending for the SAME lock from inside it. The marker has to be cleared, or the nested wrapper
# would recognise its own lock and run straight through instead of contending.
write_helper contend.sh <<EOF
#!/usr/bin/env bash
exec env -u XE_BUILD_LOCK_HELD "$WRAPPER" --lock-file "$LOCK" --timeout 1 -- true
EOF

write_helper reenter.sh <<EOF
#!/usr/bin/env bash
exec "$WRAPPER" --lock-file "$LOCK" --timeout 1 -- true
EOF

write_helper read-stdin.sh <<EOF
#!/usr/bin/env bash
read -r line && printf '%s\n' "\$line"
EOF

write_helper list-fds.sh <<EOF
#!/usr/bin/env bash
ls /proc/self/fd
EOF

run_wrapped() {
  set +e
  BUILD_LOCK_FILE="$LOCK" "$WRAPPER" -- "$TMP/$1" >"$TMP/out" 2>&1
  status=$?
  set -e
}

expect_status() {
  local want="$1" what="$2"
  [[ "$status" -eq "$want" ]] && return 0
  echo "$what: status was $status, wanted $want" >&2
  cat "$TMP/out" >&2
  exit 1
}

# --- the wrapped command's exit status is the wrapper's ---
run_wrapped exit7.sh
expect_status 7 "status passthrough"

# --- the lock is really held while the command runs ---
run_wrapped contend.sh
expect_status 69 "contended lock"

# --- the same lock, re-entered with the marker in place, runs straight through ---
run_wrapped reenter.sh
expect_status 0 "re-entrant lock"

# --- the caller's stdin reaches the command ---
# bash sends an async command's stdin to /dev/null unless it is redirected explicitly, and the
# wrapper runs the command asynchronously so that it can forward signals to it.
got="$(echo 'piped' | BUILD_LOCK_FILE="$LOCK" "$WRAPPER" -- "$TMP/read-stdin.sh")"
[[ "$got" == "piped" ]] || { echo "stdin did not reach the command: '$got'" >&2; exit 1; }

# --- the lock fd is NOT inherited: an MSBuild daemon would hold the lock for its whole idle life ---
run_wrapped list-fds.sh
expect_status 0 "fd listing"
if grep -Fxq '9' "$TMP/out"; then
  echo "fd 9 leaked into the wrapped command" >&2
  cat "$TMP/out" >&2
  exit 1
fi

# NOT tested here: cancellation. This wrapper runs its command in the FOREGROUND and installs no
# traps, so a signal aimed at its PID alone kills the wrapper and orphans the command. Cancelling a
# run through it means signalling the process GROUP — which a terminal's Ctrl-C does natively — and
# that contract belongs to the script being wrapped; scripts/tests/run-backend-tests.test.sh proves
# it for the backend gate.

echo "with-build-lock.test.sh: PASS"
