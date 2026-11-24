#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
TEMP_ROOT="$(mktemp -d)"
# Name the command that ended the run: under -e a failing check exits 1 with no output, which left one CI log with
# nothing but the runner's verdict. Only the exit trap reports, so the negative controls that expect a failure stay quiet.
failed_at=''
rc=0
trap 'failed_at="line ${LINENO}: ${BASH_COMMAND}"' ERR
trap 'rc=$?; rm -rf -- "${TEMP_ROOT}"; if [[ "${rc}" -ne 0 ]]; then echo "openapi-live-check.test.sh: FAILED (exit ${rc}) at ${failed_at}" >&2; fi' EXIT

# The caller's environment must not decide these results: CI and OPENAPI_LIVE_SKIP_BUILD skip the build, the lock
# markers skip or redirect the lock, GIT_* select another repository, and the MSBuild hygiene variables are what a
# case below asserts the script sets itself. Each case adds back only what it is about.
for var in $(compgen -v OPENAPI_LIVE_) $(compgen -v GIT_) $(compgen -v FAKE_); do unset "${var}"; done
unset CI GITHUB_ACTIONS XE_OPENAPI_LIVE_LOCKED XE_BUILD_LOCK_HELD BUILD_LOCK_TIMEOUT MSBUILDDISABLENODEREUSE MISE_DATA_DIR \
  MISE_TRUSTED_CONFIG_PATHS
export NUGET_PACKAGES="${TEMP_ROOT}/nuget-packages"

mkdir -p "${TEMP_ROOT}/bin" "${TEMP_ROOT}/release"
printf 'stable\n' >"${TEMP_ROOT}/release/fake.dll"

cat >"${TEMP_ROOT}/bin/dotnet" <<'FAKE_DOTNET'
#!/usr/bin/env bash
set -euo pipefail
case "$1" in
  build-server) exit 0 ;;
  build)
    # Arguments, the hygiene variables and whether the build runs inside the lock the wrapper acquired.
    printf 'build %s nodereuse=%s nuget=%s held=%s\n' "${*:2}" "${MSBUILDDISABLENODEREUSE:-}" "${NUGET_PACKAGES:-}" \
      "${XE_BUILD_LOCK_HELD:-}" >>"${FAKE_DOTNET_LOG}"
    if [[ -n "${FAKE_BUILD_EXIT:-}" ]]; then
      echo "error MA0009: fake analyzer failure" >&2
      exit "${FAKE_BUILD_EXIT}"
    fi
    exit 0 ;;
esac
[[ " $* " == *" run "* && " $* " == *" --no-build "* && " $* " == *" --desktop "* ]]
echo "run" >>"${FAKE_DOTNET_LOG}"
# A Release host defaults to Production, which maps no OpenAPI document; the checker must pin the environment
# or the spec fetch 404s on every path.
[[ "${ASPNETCORE_ENVIRONMENT:-}" == "Development" ]]
# The mise variables the isolated HOME would otherwise hide. Recorded rather than asserted here so one case can
# require a forwarded value and another can require that nothing was exported at all.
printf '%s\n' "${MISE_DATA_DIR-<unset>}" >"${FAKE_MISE_RECORD}"
printf '%s\n' "${MISE_TRUSTED_CONFIG_PATHS-<unset>}" >"${FAKE_TRUST_RECORD}"
# As mise does for a config it reads but nobody trusted: the host dies before readiness.
IFS=: read -r -a required <<<"${FAKE_REQUIRED_TRUST:-}"
for config in "${required[@]}"; do
  if [[ ":${MISE_TRUSTED_CONFIG_PATHS:-}:" != *":${config}:"* ]]; then
    echo "mise ERROR Config file ${config} is not trusted." >&2
    exit 1
  fi
done
if [[ -n "${FAKE_MUTATE_ASSEMBLY:-}" ]]; then
  printf 'changed\n' >>"${FAKE_MUTATE_ASSEMBLY}"
fi
port="$((32000 + RANDOM % 10000))"
# Where the real host writes it: DesktopBootstrap resolves LocalApplicationData from XDG_DATA_HOME on Linux, and
# DesktopPortStore.Persist appends XE-Local-AI-Engine/desktop-port.txt.
mkdir -p "${XDG_DATA_HOME}/XE-Local-AI-Engine"
printf '%s\n' "${port}" >"${XDG_DATA_HOME}/XE-Local-AI-Engine/desktop-port.txt"
if [[ -z "${FAKE_SUPPRESS_BROWSER_LOG:-}" ]]; then
  printf '[test INF] Opened the default browser at http://127.0.0.1:%s/.\n' "${port}"
fi
exec python3 - "${port}" <<'PY'
import http.server, json, socketserver, sys
class Handler(http.server.BaseHTTPRequestHandler):
    health_requests = 0
    def do_GET(self):
        if self.path == "/health/live":
            Handler.health_requests += 1
            if Handler.health_requests < 3:
                self.send_error(503); return
            body = b'{"status":"Healthy"}'
        elif self.path == "/openapi/local/v1/v1.json":
            body = b'{"openapi":"3.1.0"}'
        else:
            self.send_error(404); return
        self.send_response(200); self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
    def log_message(self, *_): pass
with socketserver.TCPServer(("127.0.0.1", int(sys.argv[1])), Handler) as server:
    server.serve_forever()
PY
FAKE_DOTNET

cat >"${TEMP_ROOT}/bin/pnpm" <<'FAKE_PNPM'
#!/usr/bin/env bash
set -euo pipefail
[[ "$*" == "openapi:check:live" ]]
[[ "${OPENAPI_SPEC_URL}" =~ ^http://127\.0\.0\.1:[0-9]+/openapi/local/v1/v1\.json$ ]]
python3 -c 'import os,urllib.request; assert urllib.request.urlopen(os.environ["OPENAPI_SPEC_URL"], timeout=2).status == 200'
printf '%s\n' "${OPENAPI_SPEC_URL}" >"${FAKE_PNPM_RECORD}"
FAKE_PNPM
chmod 700 "${TEMP_ROOT}/bin/dotnet" "${TEMP_ROOT}/bin/pnpm"
# The interpreter itself, not a version-manager shim: the cases below move HOME, and a shim reading the moved HOME
# refuses to run, which would fail the readiness probe for a reason that has nothing to do with the script.
ln -s "$(python3 -c 'import sys; print(sys.executable)')" "${TEMP_ROOT}/bin/python3"

export PATH="${TEMP_ROOT}/bin:${PATH}"
export FAKE_PNPM_RECORD="${TEMP_ROOT}/pnpm-record"
export FAKE_MISE_RECORD="${TEMP_ROOT}/mise-record"
export FAKE_TRUST_RECORD="${TEMP_ROOT}/trust-record"
export FAKE_DOTNET_LOG="${TEMP_ROOT}/dotnet.log"
export OPENAPI_LIVE_TIMEOUT_SECONDS=10
export OPENAPI_LIVE_CLIENT_RELEASE_ROOT="${TEMP_ROOT}/release"
# The script under test re-execs itself under the shared build lock. With a fake dotnet on PATH there is
# nothing to serialize against, and waiting behind a real gate stalled the whole contract run on 2026-09-23.
export BUILD_LOCK_FILE="${TEMP_ROOT}/build.lock"
CLIENT_CSPROJ="${SCRIPT_DIR%/scripts}/XE-Local-AI-Engine.Client/XE-Local-AI-Engine.Client.csproj"

# `! grep` never trips errexit, so a forbidden match has to fail explicitly.
refute_grep() {
  if grep "$@"; then echo "unexpected match: grep $*" >&2; return 1; fi
}

# Runs the script (CHECK_SCRIPT, else this checkout's) with extra env assignments; fresh records per run.
run_check() {
  rm -f "${FAKE_MISE_RECORD}" "${FAKE_TRUST_RECORD}" "${FAKE_DOTNET_LOG}"
  : >"${FAKE_DOTNET_LOG}"
  set +e
  check_output="$(env "$@" "${CHECK_SCRIPT:-${SCRIPT_DIR}/openapi-live-check.sh}" 2>&1)"
  check_status=$?
  set -e
}

# A caller that sets MISE_DATA_DIR: the value has to survive the HOME isolation and reach the host, or every
# mise shim on PATH aborts and the host dies before readiness. The host project is built first — Release,
# incremental, with the MSBuild hygiene, inside the lock — and only then started.
run_check MISE_DATA_DIR="${TEMP_ROOT}/mise-data"
[[ "${check_status}" -eq 0 ]] || { echo "${check_output}" >&2; exit 1; }
[[ "${check_output}" == *"PASS: pnpm openapi:check:live succeeded against the live backend."* ]]
[[ "${check_output}" == *"verify: build output unchanged during the run"* ]]
[[ -s "${FAKE_PNPM_RECORD}" ]]
[[ "$(cat "${FAKE_MISE_RECORD}")" == "${TEMP_ROOT}/mise-data" ]]
mapfile -t dotnet_calls <"${FAKE_DOTNET_LOG}"
[[ "${#dotnet_calls[@]}" -eq 2 && "${dotnet_calls[1]}" == "run" ]] || { printf '%s\n' "${dotnet_calls[@]}" >&2; exit 1; }
[[ "${dotnet_calls[0]}" == "build ${CLIENT_CSPROJ} --configuration Release nodereuse=1 nuget=${NUGET_PACKAGES} held=${BUILD_LOCK_FILE}" ]] \
  || { echo "unexpected build call: ${dotnet_calls[0]}" >&2; exit 1; }

# A caller that already exports XE_BUILD_LOCK_HELD for ANOTHER lock (or a stale "1") still gets this lock for the
# build: the wrapper, not the script, decides whether to pass through.
run_check XE_BUILD_LOCK_HELD=1
[[ "${check_status}" -eq 0 ]] || { echo "${check_output}" >&2; exit 1; }
grep -q " held=${BUILD_LOCK_FILE}\$" "${FAKE_DOTNET_LOG}" \
  || { echo "build ran outside the lock: $(head -1 "${FAKE_DOTNET_LOG}")" >&2; exit 1; }

# A failed build (an analyzer error in Release included) stops the script before the host starts: running the
# previous binaries would be the false green this build exists to prevent.
run_check FAKE_BUILD_EXIT=1
[[ "${check_status}" -eq 1 ]] || { echo "failed build exited ${check_status}: ${check_output}" >&2; exit 1; }
[[ "${check_output}" == *"error MA0009: fake analyzer failure"* ]]
[[ "${check_output}" == *"BUILD FAILED — the host was not started"* ]]
refute_grep -qx run "${FAKE_DOTNET_LOG}"
[[ ! -e "${FAKE_MISE_RECORD}" ]]

# No environment but the explicit opt-out skips the build: a CI variable (any value; a developer shell may
# export one) or a GitHub Actions runner still builds, which there is an incremental no-op after the job's
# solution build.
for ci_env in CI=true CI=false GITHUB_ACTIONS=true; do
  run_check "${ci_env}"
  [[ "${check_status}" -eq 0 ]] || { echo "${check_output}" >&2; exit 1; }
  grep -q '^build ' "${FAKE_DOTNET_LOG}" || { echo "${ci_env} skipped the build" >&2; exit 1; }
  [[ "$(tail -n 1 "${FAKE_DOTNET_LOG}")" == "run" ]]
done

# The opt-out skips it too, loudly.
run_check OPENAPI_LIVE_SKIP_BUILD=1
[[ "${check_status}" -eq 0 ]] || { echo "${check_output}" >&2; exit 1; }
[[ "${check_output}" == *"WARNING: OPENAPI_LIVE_SKIP_BUILD=1 — not building"* ]]
refute_grep -q '^build ' "${FAKE_DOTNET_LOG}"

# No caller value and no real user paths: the variable must be ABSENT from the host environment rather than
# exported empty, which mise reads as a data directory of "". Asserted on the record the fake writes before it
# does anything else, and the run's own outcome is ignored: a HOME with no trust store is precisely the
# condition that kills the host on a mise-managed box, which is the trap this forwarding exists to avoid.
run_check HOME="${TEMP_ROOT}/no-such-home" MISE_DATA_DIR=''
[[ "$(cat "${FAKE_MISE_RECORD}")" == "<unset>" ]]

# The port file is the checker's PRIMARY discovery path, and the log-grep below it is only a fallback. With the
# browser log suppressed the fallback has nothing to match, so this case passes only if port_file points where the
# host actually writes — under the isolated XDG_DATA_HOME, not under the isolated HOME. It failed (timed out) while
# the path was wrong, which is how the primary path stayed dead and unnoticed.
rm -f "${FAKE_PNPM_RECORD}"
run_check FAKE_SUPPRESS_BROWSER_LOG=1
[[ "${check_output}" == *"PASS: pnpm openapi:check:live succeeded against the live backend."* ]]
[[ -s "${FAKE_PNPM_RECORD}" ]]

printf 'stable\n' >"${TEMP_ROOT}/release/fake.dll"
run_check FAKE_MUTATE_ASSEMBLY="${TEMP_ROOT}/release/fake.dll"
[[ "${check_status}" -eq 75 ]]
[[ "${check_output}" == *"CONTAMINATED RUN — RE-RUN REQUIRED"* ]]

# The mise trust list, from a linked worktree nested in its main checkout as .tmp/worktrees/ is, below a directory
# with a mise.toml of its own. The fake host refuses to start unless both checkout entries are trusted.
mkdir -p "${TEMP_ROOT}/outer/main"
OUTER="$(cd "${TEMP_ROOT}/outer" && pwd -P)"
MAIN="${OUTER}/main"
WT="${MAIN}/.tmp/worktrees/wt"
# One empty commit to branch from: `worktree add --orphan` needs Git 2.42, and older Git ends the run here.
seed_commit() {
  git -C "$1" config user.email fixture@example.invalid
  git -C "$1" config user.name "Contract Fixture"
  GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1 git -C "$1" commit -q --allow-empty -m base
}
git init -q "${MAIN}"
seed_commit "${MAIN}"
git -C "${MAIN}" worktree add -q -b fixture-wt "${WT}"
# The script and what it sources or runs, plus an empty host project, so it runs from that checkout.
install_checkout() {
  mkdir -p "$1/scripts" "$1/XE-Local-AI-Engine.Client" "$1/XE-Local-AI-Engine.Client.React"
  cp "${SCRIPT_DIR}/openapi-live-check.sh" "${SCRIPT_DIR}/with-build-lock.sh" "${SCRIPT_DIR}/assembly-guard.sh" "$1/scripts/"
  cp -r "${SCRIPT_DIR}/lib" "$1/scripts/"
  : >"$1/XE-Local-AI-Engine.Client/XE-Local-AI-Engine.Client.csproj"
}
for checkout in "${MAIN}" "${WT}"; do install_checkout "${checkout}"; done
for dir in "${OUTER}" "${MAIN}" "${WT}"; do printf '[tools]\n' >"${dir}/mise.toml"; done
trusted_pair="${WT}/mise.toml:${MAIN}/mise.toml"
user_home="${TEMP_ROOT}/user-home"
mkdir -p "${user_home}/.config/mise"
printf '[tools]\n' >"${user_home}/.config/mise/config.toml"

# Negative control: the fake host itself refuses a list without the worktree's entry, so a script that dropped it
# would fail the cases below rather than pass them. Only the refusal itself counts: a timeout (124) or any other
# failure would say nothing about the trust check.
set +e
refusal="$(env MISE_TRUSTED_CONFIG_PATHS="${MAIN}/mise.toml" FAKE_REQUIRED_TRUST="${trusted_pair}" \
  ASPNETCORE_ENVIRONMENT=Development XDG_DATA_HOME="${TEMP_ROOT}/negative-data" \
  timeout 10 "${TEMP_ROOT}/bin/dotnet" run --no-build -- --desktop 2>&1)"
refusal_status=$?
set -e
failed_at=''
[[ "${refusal_status}" -eq 1 && "${refusal}" == "mise ERROR Config file ${WT}/mise.toml is not trusted." ]] \
  || { echo "the fake host did not refuse the untrusted worktree config (exit ${refusal_status}): ${refusal}" >&2; exit 1; }

# No caller list and no user config: both checkout entries anyway, and nothing from above the main checkout.
CHECK_SCRIPT="${WT}/scripts/openapi-live-check.sh" run_check HOME="${TEMP_ROOT}/no-such-home" FAKE_REQUIRED_TRUST="${trusted_pair}"
[[ "${check_status}" -eq 0 ]] || { echo "${check_output}" >&2; exit 1; }
[[ "$(cat "${FAKE_TRUST_RECORD}")" == "${trusted_pair}" ]] || { echo "trust list: $(cat "${FAKE_TRUST_RECORD}")" >&2; exit 1; }
refute_grep -q "${OUTER}/mise.toml" "${FAKE_TRUST_RECORD}"

# The user config when it exists, then the checkout entries.
CHECK_SCRIPT="${WT}/scripts/openapi-live-check.sh" run_check HOME="${user_home}" FAKE_REQUIRED_TRUST="${trusted_pair}"
[[ "${check_status}" -eq 0 ]] || { echo "${check_output}" >&2; exit 1; }
[[ "$(cat "${FAKE_TRUST_RECORD}")" == "${user_home}/.config/mise/config.toml:${trusted_pair}" ]] \
  || { echo "trust list: $(cat "${FAKE_TRUST_RECORD}")" >&2; exit 1; }

# A caller's list is kept as given (it replaces the user config) and an entry it already holds is not repeated.
CHECK_SCRIPT="${WT}/scripts/openapi-live-check.sh" run_check HOME="${user_home}" FAKE_REQUIRED_TRUST="${trusted_pair}" \
  MISE_TRUSTED_CONFIG_PATHS="/opt/caller/config.toml:${WT}/mise.toml"
[[ "${check_status}" -eq 0 ]] || { echo "${check_output}" >&2; exit 1; }
[[ "$(cat "${FAKE_TRUST_RECORD}")" == "/opt/caller/config.toml:${trusted_pair}" ]] \
  || { echo "trust list: $(cat "${FAKE_TRUST_RECORD}")" >&2; exit 1; }

# From the main checkout itself the two entries are one.
CHECK_SCRIPT="${MAIN}/scripts/openapi-live-check.sh" run_check HOME="${TEMP_ROOT}/no-such-home" \
  FAKE_REQUIRED_TRUST="${MAIN}/mise.toml"
[[ "${check_status}" -eq 0 ]] || { echo "${check_output}" >&2; exit 1; }
[[ "$(cat "${FAKE_TRUST_RECORD}")" == "${MAIN}/mise.toml" ]] || { echo "trust list: $(cat "${FAKE_TRUST_RECORD}")" >&2; exit 1; }

# No main checkout holds these, so only their own entry is trusted: a worktree of a bare repository (git marks the
# first entry bare), and a separate-git-dir checkout and its linked worktree (git lists the git dir as the main
# worktree). A mise.toml sits beside each git dir, where the parent of the common dir would have pointed.
mkdir -p "${OUTER}/bare" "${OUTER}/separate/meta"
git clone -q --bare "${MAIN}" "${OUTER}/bare/repo.git"
git -C "${OUTER}/bare/repo.git" worktree add -q -b fixture-bare "${OUTER}/bare/wt"
git init -q --separate-git-dir "${OUTER}/separate/meta/repo.git" "${OUTER}/separate/checkout"
seed_commit "${OUTER}/separate/checkout"
git -C "${OUTER}/separate/checkout" worktree add -q -b fixture-linked "${OUTER}/separate/checkout/linked"
for dir in "${OUTER}/bare" "${OUTER}/separate/meta"; do printf '[tools]\n' >"${dir}/mise.toml"; done
for checkout in "${OUTER}/bare/wt" "${OUTER}/separate/checkout" "${OUTER}/separate/checkout/linked"; do
  install_checkout "${checkout}"
  printf '[tools]\n' >"${checkout}/mise.toml"
  CHECK_SCRIPT="${checkout}/scripts/openapi-live-check.sh" run_check HOME="${TEMP_ROOT}/no-such-home" \
    FAKE_REQUIRED_TRUST="${checkout}/mise.toml"
  [[ "${check_status}" -eq 0 ]] || { echo "${check_output}" >&2; exit 1; }
  [[ "$(cat "${FAKE_TRUST_RECORD}")" == "${checkout}/mise.toml" ]] \
    || { echo "trust list from ${checkout}: $(cat "${FAKE_TRUST_RECORD}")" >&2; exit 1; }
done

# A checkout under a directory whose name holds ':' would split the colon-delimited trust list into wrong entries:
# refused with exit 2 before the host starts, so nothing is trusted.
colon_checkout="${OUTER}/co:lon/checkout"
install_checkout "${colon_checkout}"
printf '[tools]\n' >"${colon_checkout}/mise.toml"
CHECK_SCRIPT="${colon_checkout}/scripts/openapi-live-check.sh" run_check HOME="${TEMP_ROOT}/no-such-home"
[[ "${check_status}" -eq 2 ]] || { echo "colon checkout exited ${check_status}: ${check_output}" >&2; exit 1; }
[[ "${check_output}" == *"ERROR: ${colon_checkout}/mise.toml contains ':', which MISE_TRUSTED_CONFIG_PATHS cannot hold;"* ]]
[[ ! -e "${FAKE_TRUST_RECORD}" ]]
refute_grep -qx run "${FAKE_DOTNET_LOG}"

echo "openapi-live-check.test.sh: PASS"
