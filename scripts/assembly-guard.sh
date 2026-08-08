#!/usr/bin/env bash
# assembly-guard.sh — detect a test run whose assemblies were overwritten underneath it.
#
# Why this exists
#   `dotnet test --no-build` loads assemblies from bin/. A concurrent `dotnet build` in another
#   process rewrites those files mid-run, and the test host then reports failures that have nothing
#   to do with the code: observed on this repo as `failed: 97` (of 4225) and `failed: 1`, both clean
#   on re-run, and as `FileNotFoundException: Microsoft.AspNetCore.SignalR.Client.Core, Version=
#   10.0.9.0` in the E2E suite. In every case the DLL mtimes fell INSIDE the run window.
#
#   The damage is not the lost run, it is the lost trust: a contaminated run is indistinguishable
#   from a real regression, so either someone chases a phantom for a day, or — far worse — someone
#   waves a REAL failure away as "probably contamination". With GitHub Actions disabled this suite
#   is the only gate this project has.
#
#   scripts/with-build-lock.sh prevents the collision between processes that opt in. This script is
#   the safety net for everything that does not: a bare `dotnet build` in another terminal cannot be
#   forced through a wrapper, but it CAN be caught after the fact. A run whose inputs changed is
#   reported as CONTAMINATED with its own exit code — never as test failures, and never as a pass.
#
# What it compares
#   For each root, every file that a build can rewrite and a test host can load: *.dll, *.exe, *.so,
#   *.dylib, *.deps.json, *.runtimeconfig.json, and the extensionless MTP apphost. Identity is
#   (size, mtime-with-sub-second-precision). Test runs write logs, .trx and temp files into these
#   trees all the time; none of those are tracked, so a normal run produces no diff.
#
# Where the boundaries go
#   Snapshot AFTER the build that the run itself performs, immediately before the first test process
#   starts; verify immediately after the last one exits. A legitimate build-then-test sequence is
#   therefore entirely outside the window and cannot produce a false positive. The `guard`
#   subcommand does exactly this and is the form to prefer.
#
# Usage:
#   scripts/assembly-guard.sh snapshot <state-file> [--test-bins] [<root>...]
#   scripts/assembly-guard.sh verify   <state-file>
#   scripts/assembly-guard.sh guard    [--state <file>] [--test-bins] [--root <dir>]... -- <cmd> [args...]
#
# Options:
#   --test-bins       Add every test project's build output (<repo>/*.Tests*/bin/*/net*) as a root.
#   --root <dir>      Add one root (repeatable). Non-existent roots are recorded and a root that
#                     DISAPPEARS mid-run is itself reported — a sibling `clean` is contamination too.
#   --state <file>    Where `guard` keeps its snapshot (default: a temp file it removes afterwards).
#   --help            Show this message.
#
# Exit codes:
#   0    — clean (for `guard`: clean AND the wrapped command succeeded)
#   75   — CONTAMINATED: the build output changed during the run (EX_TEMPFAIL — "re-run required").
#          `guard` returns 75 even when the wrapped command also failed: once the inputs moved, the
#          failure cannot be attributed either, so the only honest verdict is "re-run".
#   1-N  — for `guard`, the wrapped command's own exit status when the run was clean
#   2    — usage error
set -uo pipefail

PROJECT_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || (cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd))"

# EX_TEMPFAIL. Deliberately distinct from every status the test runners already produce (0 pass,
# 1 failures, 2 missing prerequisite, 8 zero-tests-matched) so "re-run me" can never be misread.
CONTAMINATED_EXIT=75

log()  { echo "[guard] $*" >&2; }
die()  { echo "[guard] $*" >&2; exit 2; }

usage() {
  sed -n '2,/^set -uo/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//; $d'
}

# Every test project's output tree. Both configurations: a stale Debug tree is inert, and pinning
# only Release would miss a concurrent `dotnet build` that happens to use the other configuration.
collect_test_bins() {
  local dir
  for dir in "${PROJECT_ROOT}"/*.Tests*/bin/*/net*; do
    [[ -d "${dir}" ]] && printf '%s\n' "${dir}"
  done
}

# Tab-separated `size <TAB> mtime <TAB> path`, sorted by path. Paths cannot contain tabs, so the
# three fields stay unambiguous.
write_manifest() {
  local out="$1"; shift
  local root
  : >"${out}"
  for root in "$@"; do
    [[ -d "${root}" ]] || continue
    find "${root}" -type f \
      \( -name '*.dll' -o -name '*.exe' -o -name '*.so' -o -name '*.dylib' \
         -o -name '*.deps.json' -o -name '*.runtimeconfig.json' \
         -o \( -perm -u+x -not -name '*.*' \) \) \
      -printf '%s\t%T@\t%p\n' 2>/dev/null
  done | LC_ALL=C sort -t'	' -k3 >>"${out}"
}

do_snapshot() {
  local state="$1"; shift
  local roots=("$@")
  [[ ${#roots[@]} -gt 0 ]] || die "snapshot needs at least one root (or --test-bins)"

  local body; body="$(mktemp)"
  write_manifest "${body}" "${roots[@]}"

  {
    echo "# xe-assembly-guard v1"
    printf '# taken %s\n' "$(date -Iseconds)"
    local root
    for root in "${roots[@]}"; do printf '# root %s\n' "${root}"; done
    cat "${body}"
  } >"${state}"
  rm -f "${body}"

  local count; count="$(grep -cv '^#' "${state}" || true)"
  log "snapshot: ${count} assemblies across ${#roots[@]} root(s) -> ${state}"
}

do_verify() {
  local state="$1"
  [[ -f "${state}" ]] || die "snapshot file not found: ${state}"

  local roots=()
  local line
  while IFS= read -r line; do roots+=("${line#\# root }"); done < <(grep '^# root ' "${state}" || true)
  [[ ${#roots[@]} -gt 0 ]] || die "snapshot file ${state} records no roots — it is not a valid snapshot"

  local before after
  before="$(mktemp)"; after="$(mktemp)"
  grep -v '^#' "${state}" >"${before}" || true
  write_manifest "${after}" "${roots[@]}"

  local report; report="$(mktemp)"
  awk -F'\t' '
    NR == FNR { size[$3] = $1; mtime[$3] = $2; next }
    {
      seen[$3] = 1
      if (!($3 in size)) { printf "ADDED    %s\n", $3; next }
      if (size[$3] != $1 || mtime[$3] != $2)
        printf "CHANGED  %s (size %s -> %s, mtime %s -> %s)\n", $3, size[$3], $1, mtime[$3], $2
    }
    END { for (p in size) if (!(p in seen)) printf "REMOVED  %s\n", p }
  ' "${before}" "${after}" | LC_ALL=C sort >"${report}"

  local changed; changed="$(wc -l <"${report}")"
  rm -f "${before}" "${after}"

  if [[ "${changed}" -eq 0 ]]; then
    log "verify: build output unchanged during the run — result is trustworthy."
    rm -f "${report}"
    return 0
  fi

  {
    echo
    echo "[guard] ================================================================"
    echo "[guard] CONTAMINATED RUN — RE-RUN REQUIRED. This is NOT a test result."
    echo "[guard] ================================================================"
    echo "[guard] ${changed} tracked file(s) were rewritten while the tests were running, so the"
    echo "[guard] test host was reading assemblies that changed underneath it. Whatever it reported"
    echo "[guard] — passes or failures — describes nothing. Do not treat it as either."
    echo "[guard]"
    sed 's/^/[guard]   /' "${report}" | head -40
    if [[ "${changed}" -gt 40 ]]; then
      echo "[guard]   ... and $((changed - 40)) more"
    fi
    echo "[guard]"
    echo "[guard] Almost certainly another process ran 'dotnet build' during the run. Find it, wait"
    echo "[guard] for it to finish, then re-run. To make the collision impossible between cooperating"
    echo "[guard] shells, run both through: scripts/with-build-lock.sh -- <command>"
  } >&2
  rm -f "${report}"
  return "${CONTAMINATED_EXIT}"
}

do_guard() {
  local state="" own_state="false"
  local roots=()
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --state)     state="${2:-}"; [[ -n "${state}" ]] || die "--state needs a value"; shift 2 ;;
      --root)      [[ -n "${2:-}" ]] || die "--root needs a value"; roots+=("$2"); shift 2 ;;
      --test-bins) while IFS= read -r line; do roots+=("${line}"); done < <(collect_test_bins); shift ;;
      --)          shift; break ;;
      *)           die "guard: unexpected argument '$1' (the command must come after --)" ;;
    esac
  done
  [[ $# -gt 0 ]] || die "guard: no command given (put it after --)"
  [[ ${#roots[@]} -gt 0 ]] || die "guard: no roots given (use --root and/or --test-bins)"

  if [[ -z "${state}" ]]; then
    mkdir -p "${PROJECT_ROOT}/.tmp"
    state="$(mktemp "${PROJECT_ROOT}/.tmp/assembly-guard-XXXXXX.state")"
    own_state="true"
  fi

  do_snapshot "${state}" "${roots[@]}"

  "$@"
  local status=$?

  local verify_status=0
  do_verify "${state}" || verify_status=$?
  [[ "${own_state}" == "true" ]] && rm -f "${state}"

  if [[ "${verify_status}" -ne 0 ]]; then
    if [[ "${status}" -ne 0 ]]; then
      log "(the wrapped command also exited ${status}, but that verdict is unusable — see above)"
    fi
    return "${verify_status}"
  fi
  return "${status}"
}

[[ $# -gt 0 ]] || { usage >&2; exit 2; }

case "$1" in
  --help|-h)
    usage; exit 0 ;;
  snapshot)
    shift
    [[ $# -gt 0 ]] || die "snapshot needs a state file path"
    STATE="$1"; shift
    ROOTS=()
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --test-bins) while IFS= read -r bin; do ROOTS+=("${bin}"); done < <(collect_test_bins); shift ;;
        --root)      [[ -n "${2:-}" ]] || die "--root needs a value"; ROOTS+=("$2"); shift 2 ;;
        -*)          die "snapshot: unknown option '$1'" ;;
        *)           ROOTS+=("$1"); shift ;;
      esac
    done
    do_snapshot "${STATE}" "${ROOTS[@]}" ;;
  verify)
    shift
    [[ $# -eq 1 ]] || die "verify takes exactly one argument: the state file written by snapshot"
    do_verify "$1" ;;
  guard)
    shift
    do_guard "$@" ;;
  *)
    die "unknown subcommand '$1' (expected: snapshot | verify | guard)" ;;
esac
