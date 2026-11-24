# shellcheck shell=bash
# Sourced by scripts/run-backend-tests.sh and scripts/run-tests-memory-safe.sh. Why a mise shim must not be what the
# gate and its lanes run: run-backend-tests.sh, "Version-manager shims".

# xe_mise_tool_dirs_first <repo>: when `command -v` of dotnet, python3 or node resolves (readlink -f) to a file named
# mise, asks that mise from <repo> for `mise bin-paths` and inserts each listed directory that exists, in mise's order,
# right before the shims entry on PATH (entries compared without trailing slashes; at the front when none matches),
# then prints one ">> mise shims on PATH:" line. Every other entry, earlier ordinary ones included, is kept as written.
# PATH stays as it was, and nothing is printed, when there is no such shim, `mise bin-paths` fails or lists no
# existing directory, or every listed directory already precedes the shims entry. That last rule makes a second pass
# (the gate hands its runner a PATH it already fixed) a no-op: a tool with a shim but no bin-paths directory still
# resolves to mise afterwards, so the shim alone cannot tell a fixed PATH from a raw one.
xe_mise_tool_dirs_first() {
  local repo="$1" tool cmd mise_bin="" shims="" listed dir entry rest cmp want prefix="" new="" hit=0
  local -a dirs=()
  local -A before=()
  for tool in dotnet python3 node; do
    cmd="$(command -v "$tool")" || continue
    if [[ "$(basename "$(readlink -f "$cmd")")" == mise ]]; then
      mise_bin="$(readlink -f "$cmd")" shims="$(dirname "$cmd")"
      break
    fi
  done
  [[ -n "$mise_bin" ]] && listed="$(cd "$repo" && "$mise_bin" bin-paths 2>/dev/null)" || return 0
  while IFS= read -r dir; do
    [[ -n "$dir" && -d "$dir" ]] || continue
    dirs+=("$dir")
    prefix+="$dir:"
  done <<<"$listed"
  (( ${#dirs[@]} > 0 )) || return 0

  # `/x/shims/` is the directory `dirname` reports as `/x/shims`.
  want="$shims"
  while [[ "$want" == ?*/ ]]; do want="${want%/}"; done
  rest="$PATH:"
  while [[ -n "$rest" ]]; do
    entry="${rest%%:*}" rest="${rest#*:}"
    cmp="$entry"
    while [[ "$cmp" == ?*/ ]]; do cmp="${cmp%/}"; done
    if (( hit == 0 )) && [[ "$cmp" == "$want" ]]; then
      new+="$prefix" hit=1
    fi
    (( hit == 1 )) || [[ -z "$cmp" ]] || before["$cmp"]=1
    new+="$entry:"
  done

  for dir in "${dirs[@]}"; do
    cmp="$dir"
    while [[ "$cmp" == ?*/ ]]; do cmp="${cmp%/}"; done
    if [[ -z "${before[$cmp]-}" ]]; then
      (( hit == 1 )) || new="$prefix$new"
      export PATH="${new%:}"
      echo ">> mise shims on PATH: using the real tool directories from 'mise bin-paths' first (${#dirs[@]} dirs)"
      return 0
    fi
  done
  return 0
}
