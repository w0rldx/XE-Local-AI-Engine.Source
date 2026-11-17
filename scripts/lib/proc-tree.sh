# shellcheck shell=bash
# proc-tree.sh — sourced by scripts/run-tests-memory-safe.sh, never executed. Reads a process tree from
# ONE `ps` snapshot, so the RSS sum and the PID list describe the same instant.
#
# WHY: every batch runs in Microsoft.Testing.Platform's test-host-controller mode. The PID the runner
# starts is a ~105 MB controller; the test host that holds the memory is its child
# (`--internal-testhostcontroller-pid <pid>`), and the host spawns engine processes in turn. Sampling or
# killing the controller's PID alone misses all of them.
#
# Test seam: XE_SIZING_PS names a file holding a `ps -eo pid=,ppid=,rss=` table that replaces the live
# one. The XE_SIZING_ prefix keeps it in the gate's contract-lane scrub, like XE_SIZING_MEMINFO.

# xe_proc_tree <pid> — prints "<rss_kb> [<descendant pid>...]" on one line: the summed RSS of <pid> and
# every descendant (grandchildren included), then the descendant PIDs, parents before children. A PID
# missing from the table prints "0".
xe_proc_tree() {
  local table
  if [[ -n "${XE_SIZING_PS:-}" ]]; then table="$(cat "$XE_SIZING_PS" 2>/dev/null)"
  else table="$(ps -eo pid=,ppid=,rss= 2>/dev/null)"; fi
  awk -v root="$1" '
    { rss[$1] = $3; kids[$2] = kids[$2] " " $1 }
    END {
      sum = rss[root] + 0; out = ""; n = split(kids[root], queue, " ")
      for (i = 1; i <= n; i++) {
        p = queue[i]; out = out " " p; sum += rss[p]
        m = split(kids[p], children, " "); for (j = 1; j <= m; j++) queue[++n] = children[j]
      }
      print sum out
    }' <<<"$table"
}
