#!/usr/bin/env bash
#
# Behavioural tests for scripts/lab-up.sh and scripts/lab-api.sh.
#
# Offline, no host, no network. The scripts under test are copied into a temp checkout; every Python driver in
# that copy is a dispatcher: the subcommands that talk to a node (auth, settings-ensure, model-ensure, download,
# first-turn) print scripted records, everything else (snapshot-key, manifest, base-url) execs the REAL driver.
# dev-start/dev-status/dev-stop, aspire and curl are fakes driven by files in ${CTL}. HOME and XDG point into the
# temp tree, so the xdg mirror never touches the real home.

set -uo pipefail

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
TEMP_ROOT="$(cd "$(mktemp -d)" && pwd -P)"
# Helper processes (a stand-in host whose /proc environ lab-up verifies, a loopback fake node) die with the test.
HELPER_PIDS=()
cleanup() {
  [[ "${#HELPER_PIDS[@]}" -eq 0 ]] || kill "${HELPER_PIDS[@]}" 2>/dev/null
  rm -rf -- "${TEMP_ROOT}"
}
trap cleanup EXIT

FAILED=0
CHECKS=0
check() {
  local label="$1" expected="$2" actual="$3"
  CHECKS=$((CHECKS + 1))
  [[ "${expected}" == "${actual}" ]] && return 0
  echo "  FAIL: ${label}: expected '${expected}', got '${actual}'" >&2
  FAILED=$((FAILED + 1))
}
check_contains() {
  local label="$1" needle="$2" haystack="$3"
  CHECKS=$((CHECKS + 1))
  [[ "${haystack}" == *"${needle}"* ]] && return 0
  echo "  FAIL: ${label}: did not contain '${needle}'" >&2
  echo "        got: ${haystack}" >&2
  FAILED=$((FAILED + 1))
}
check_absent() {
  local label="$1" needle="$2" haystack="$3"
  CHECKS=$((CHECKS + 1))
  [[ "${haystack}" != *"${needle}"* ]] && return 0
  echo "  FAIL: ${label}: unexpectedly contained '${needle}'" >&2
  FAILED=$((FAILED + 1))
}

# --- temp checkout -------------------------------------------------------------------------------------------
REPO="${TEMP_ROOT}/repo"
REAL="${TEMP_ROOT}/real"
CTL="${TEMP_ROOT}/ctl"
BIN="${TEMP_ROOT}/bin"
FAKE_HOME="${TEMP_ROOT}/home"
mkdir -p "${REPO}/scripts/lab/profiles" "${REAL}" "${REPO}/XE-Local-AI-Engine.AppHost" "${CTL}" "${BIN}" \
  "${REPO}/XE-Local-AI-Engine.Client.Persistence/Migrations" "${REPO}/XE-Local-AI-Engine.Client" \
  "${FAKE_HOME}/.local/share/mise" "${FAKE_HOME}/.local/share/XE-Local-AI-Engine"
cp "${SRC}/lab-up.sh" "${SRC}/lab-api.sh" "${REPO}/scripts/"
cp "${SRC}/lab-driver.py" "${SRC}/gpu-smoke-driver.py" "${SRC}/model-matrix-driver.py" "${REAL}/"
LAB="${REPO}/.tmp/lab/test"
# The copies must resolve ROOT to ${REPO}, never to a real checkout (whose .data, node-settings and lab dir may be live).
if git -C "${REPO}/scripts" rev-parse --show-toplevel >/dev/null 2>&1; then
  echo "lab-up.test.sh: ${TEMP_ROOT} is inside a git checkout; refusing to run the copies there." >&2
  exit 1
fi
MANIFEST="${LAB}/manifest.json"
DATA="${REPO}/XE-Local-AI-Engine.AppHost/.data"
CLIENT="${REPO}/XE-Local-AI-Engine.Client"
MODEL="org/Model-GGUF:Q4_K_M"
PASSWORD="Pw-s3cret-LAB"

MIG="${REPO}/XE-Local-AI-Engine.Client.Persistence/Migrations"
for f in 20260101000000_Init.cs 20260101000000_Init.Designer.cs 20260202000000_Second.cs AppDbContextModelSnapshot.cs; do
  : >"${MIG}/${f}"
done
EXPECTED_KEY="$(printf '%s\n%s' 20260101000000_Init.cs 20260202000000_Second.cs | sha256sum | cut -d' ' -f1)"

cat >"${REPO}/scripts/lab/profiles/test.json" <<JSON
{
  "model": "${MODEL}",
  "modelSource": { "repo": "org/Model-GGUF", "file": "model-Q4_K_M.gguf" },
  "settings": { "externalAccessProfile": "offline", "defaultModelName": "<model>" },
  "env": { "XE_LLAMACPP_VARIANT": "cuda", "LAB_TEST_PROFILE_ONLY": "from-profile" },
  "modelsDir": ".tmp/lab/models",
  "stop": "model",
  "firstTurnPrompt": "Reply OK."
}
JSON

# One dispatcher, installed under each driver's name: node-talking subcommands are scripted from ${CTL}/<sub>.out
# (or <sub>.2.out on the second call of a run), the rest exec the real driver of the same name. Imported as a module
# (lab-api.sh loads the driver's redact), it exposes the real driver.
cat >"${TEMP_ROOT}/dispatch.py" <<'PY'
import importlib.util, os, sys
ctl, real = os.environ["LAB_TEST_CTL"], os.environ["LAB_TEST_REAL"]
FAKE = ("auth", "settings-ensure", "model-ensure", "download", "first-turn")


def main():
    name = os.path.basename(sys.argv[0])
    sub = next((a for a in sys.argv[1:] if a in FAKE + ("snapshot-key", "manifest", "base-url")), "")
    with open(os.path.join(ctl, "calls.log"), "a", encoding="utf-8") as log:
        log.write(sub + "\n")
    with open(os.path.join(ctl, f"{sub}.argv"), "w", encoding="utf-8") as log:
        log.write("\n".join(sys.argv[1:]) + "\n")
    if sub not in FAKE:
        os.execv(sys.executable, [sys.executable, os.path.join(real, name), *sys.argv[1:]])
    with open(os.path.join(ctl, "calls.log"), encoding="utf-8") as log:
        nth = sum(1 for line in log if line.strip() == sub)
    code_file = os.path.join(ctl, f"{sub}.exit")
    if sub == "auth":
        stall = os.path.join(ctl, "auth.sleep")
        if os.path.exists(stall):
            import time
            time.sleep(float(open(stall).read()))  # a node that never answers the login
        print(f"token\ttok-{nth}-opaque")
    else:
        out = os.path.join(ctl, f"{sub}.{nth}.out")
        if not os.path.exists(out):
            out = os.path.join(ctl, f"{sub}.out")
        sys.stdout.write(open(out, encoding="utf-8").read())
    sys.exit(int(open(code_file).read()) if os.path.exists(code_file) else 0)


if __name__ == "__main__":
    main()
else:
    spec = importlib.util.spec_from_file_location("real_driver", os.path.join(real, os.path.basename(__file__)))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    globals().update({k: v for k, v in vars(module).items() if not k.startswith("__")})
PY
for d in lab-driver.py gpu-smoke-driver.py model-matrix-driver.py; do
  cp "${TEMP_ROOT}/dispatch.py" "${REPO}/scripts/${d}"
done

# dev-status: exit code from ${CTL}/dev-status.exit (default 3 = stopped); --json describes a running host whose pid
# is ${CTL}/host-pid.
cat >"${REPO}/scripts/dev-status.sh" <<'SH'
#!/usr/bin/env bash
echo "dev-status ${XE_ASPIRE_APPHOST:-<unset>} ${XE_NODE_OPERATOR_SECRET_FILE:-<unset>}" >>"${LAB_TEST_CTL}/apphost.log"
if [[ "${1:-}" == "--json" ]]; then
  printf '{"pid":%s,"resources":%s}\n' "$(cat "${LAB_TEST_CTL}/host-pid")" \
    '[{"name":"app","urls":[{"url":"https://127.0.0.1:7443"}]},{"name":"client-react","urls":'"$(cat "${LAB_TEST_CTL}/vite-urls" 2>/dev/null || echo '[{"url":"http://127.0.0.1:5173/"}]')"'}]'
  exit 0
fi
exit "$(cat "${LAB_TEST_CTL}/dev-status.exit" 2>/dev/null || echo 3)"
SH
# dev-start: dumps its env, creates node state the way a first host start does, and is then "running".
cat >"${REPO}/scripts/dev-start.sh" <<'SH'
#!/usr/bin/env bash
root="$(cd "$(dirname "$0")/.." && pwd -P)"
echo start >>"${LAB_TEST_CTL}/dev-start.calls"
echo "dev-start ${XE_ASPIRE_APPHOST:-<unset>} ${XE_NODE_OPERATOR_SECRET_FILE:-<unset>}" >>"${LAB_TEST_CTL}/apphost.log"
env >"${LAB_TEST_CTL}/dev-start.env"
mkdir -p "${root}/XE-Local-AI-Engine.AppHost/.data" "${root}/XE-Local-AI-Engine.Client/dp-keys"
[[ -f "${root}/XE-Local-AI-Engine.AppHost/.data/node.db" ]] || echo "db-v1" >"${root}/XE-Local-AI-Engine.AppHost/.data/node.db"
[[ -f "${root}/XE-Local-AI-Engine.Client/dp-keys/key.xml" ]] || echo "ring-v1" >"${root}/XE-Local-AI-Engine.Client/dp-keys/key.xml"
[[ -f "${root}/XE-Local-AI-Engine.Client/node-settings.json" ]] || echo '{"s":1}' >"${root}/XE-Local-AI-Engine.Client/node-settings.json"
echo 0 >"${LAB_TEST_CTL}/dev-status.exit"
SH
cat >"${REPO}/scripts/dev-stop.sh" <<'SH'
#!/usr/bin/env bash
echo stop >>"${LAB_TEST_CTL}/dev-stop.calls"
echo "dev-stop ${XE_ASPIRE_APPHOST:-<unset>} ${XE_NODE_OPERATOR_SECRET_FILE:-<unset>}" >>"${LAB_TEST_CTL}/apphost.log"
echo 3 >"${LAB_TEST_CTL}/dev-status.exit"
SH
printf '#!/usr/bin/env bash\nexit 0\n' >"${BIN}/aspire"
# curl: GET auth/status exits ${CTL}/curl-status.exit (default 0). Any other URL answers the next
# "<code><TAB><body>" line of ${CTL}/curl-queue (then ${CTL}/curl-default), logs method, URL, bearer token and
# time bounds, and exits ${CTL}/curl-exit (default 0) — a non-zero exit after printing the code is a broken body.
cat >"${BIN}/curl" <<'SH'
#!/usr/bin/env bash
out=/dev/null url="" hdr="" method=GET ct=none mt=none
while (($#)); do
  case "$1" in
    -o) out="$2"; shift 2 ;;
    -X) method="$2"; shift 2 ;;
    --connect-timeout) ct="$2"; shift 2 ;;
    --max-time) mt="$2"; shift 2 ;;
    -w|--data-binary) shift 2 ;;
    -H) [[ "$2" == @* ]] && hdr="${2#@}"; shift 2 ;;
    -*) shift ;;
    *) url="$1"; shift ;;
  esac
done
[[ ! -f "${LAB_TEST_CTL}/curl-sleep" ]] || sleep "$(cat "${LAB_TEST_CTL}/curl-sleep")"  # a slow node
if [[ "${url}" == */auth/status ]]; then
  exit "$(cat "${LAB_TEST_CTL}/curl-status.exit" 2>/dev/null || echo 0)"
fi
q="${LAB_TEST_CTL}/curl-queue"
line="$(head -n 1 "${q}" 2>/dev/null)"
if [[ -n "${line}" ]]; then tail -n +2 "${q}" >"${q}.tmp"; mv "${q}.tmp" "${q}"; else line="$(cat "${LAB_TEST_CTL}/curl-default")"; fi
printf '%s %s %s ct=%s mt=%s\n' "${method}" "${url}" "$(cat "${hdr}" 2>/dev/null)" "${ct}" "${mt}" >>"${LAB_TEST_CTL}/curl.log"
printf '%s' "${line#*$'\t'}" >"${out}"
printf '%s' "${line%%$'\t'*}"
exit "$(cat "${LAB_TEST_CTL}/curl-exit" 2>/dev/null || echo 0)"
SH
chmod 755 "${REPO}/scripts/"*.sh "${REPO}/scripts/"*.py "${BIN}/"*

TEST_ENV=(env -u GIT_DIR -u GIT_WORK_TREE -u XE_LAB_PLAN -u XE_ASPIRE_APPHOST -u XE_NODE_OPERATOR_SECRET_FILE -u XE_LLAMACPP_VARIANT -u XE_LLAMACPP_SERVER_PATH -u LAB_TEST_PROFILE_ONLY
  -u XDG_DATA_HOME HOME="${FAKE_HOME}" PATH="${BIN}:${PATH}" LAB_TEST_CTL="${CTL}" LAB_TEST_REAL="${REAL}"
  LAB_READY_TIMEOUT_SECONDS=5 LAB_PASSWORD="${PASSWORD}")

ctl() { printf '%b' "$2" >"${CTL}/$1"; }
# A stand-in host: a sleeping process whose environment is exactly the given VAR=value pairs. Sets STUB_PID.
SLEEP_BIN="$(command -v sleep)"
start_host_stub() {
  env -i "$@" "${SLEEP_BIN}" 600 </dev/null >/dev/null 2>&1 &
  STUB_PID="$!"
  HELPER_PIDS+=("${STUB_PID}")
}
# The lab run exports exactly these startup variables (TEST_ENV unsets XE_LLAMACPP_SERVER_PATH; the profile sets the
# variant and LAB_TEST_PROFILE_ONLY), so a host started by it carries exactly these.
LAB_HOST_ENV=("XDG_DATA_HOME=${LAB}/xdg" "HuggingFace__ModelsDirectory=${REPO}/.tmp/lab/models"
  "XE_LLAMACPP_VARIANT=cuda" "LAB_TEST_PROFILE_ONLY=from-profile")
start_host_stub "${LAB_HOST_ENV[@]}"
GOOD_HOST="${STUB_PID}"
start_host_stub "${LAB_HOST_ENV[@]}" "HuggingFace__ModelsDirectory=/elsewhere/models"
WRONG_HOST="${STUB_PID}"
start_host_stub "${LAB_HOST_ENV[@]}" "XE_LLAMACPP_VARIANT=vulkan"
WRONG_VARIANT_HOST="${STUB_PID}"
start_host_stub "${LAB_HOST_ENV[@]:0:3}"
MISSING_KEY_HOST="${STUB_PID}"
start_host_stub "${LAB_HOST_ENV[@]}" "XE_LLAMACPP_SERVER_PATH=/opt/byo/llama-server"
EXTRA_RUNTIME_HOST="${STUB_PID}"
(exit 0) &
DEAD_HOST="$!"
wait "${DEAD_HOST}"
echo "${GOOD_HOST}" >"${CTL}/host-pid"
# run_lab [VAR=value ...] -- lab-up args: sets STATUS and ERR (stderr); stdout is ignored, the manifest file is read.
run_lab() {
  local extra=()
  while [[ $# -gt 0 && "$1" != "--" ]]; do extra+=("$1"); shift; done
  shift
  rm -f -- "${MANIFEST}" "${CTL}/calls.log"
  "${TEST_ENV[@]}" ${extra[@]+"${extra[@]}"} "${REPO}/scripts/lab-up.sh" --profile test "$@" >/dev/null 2>"${CTL}/stderr"
  STATUS=$?
  ERR="$(cat "${CTL}/stderr")"
}
mf() {
  python3 -c '
import json, sys
d = json.load(open(sys.argv[1]))
for k in sys.argv[2].split("."):
    d = d[k]
print(d if isinstance(d, str) else json.dumps(d))
' "${MANIFEST}" "$1" 2>/dev/null || echo "<missing $1>"
}
calls() { local n; n="$(grep -cx "$1" "${CTL}/calls.log" 2>/dev/null)"; echo "${n:-0}"; }
# argval FILE FLAG: the value after FLAG in a one-arg-per-line argv log.
argval() { grep -A1 -x -- "$2" "$1" 2>/dev/null | sed -n 2p; }
count_lines() { [[ -f "$1" ]] && wc -l <"$1" | tr -d ' ' || echo 0; }
settings_ok() { ctl settings-ensure.out "changed\texternalAccessProfile\nchanged\tdefaultModelName\nskipped\tfalse\nverified\ttrue\n"; }
settings_same() { ctl settings-ensure.out "skipped\ttrue\nverified\ttrue\n"; }
model_same() { ctl model-ensure.out "installed\ttrue\npreviouslySelected\t${MODEL}\nselected\t${MODEL}\nresident\tfalse\n"; }

echo "== 1. first run, --snapshot --until settings =="
settings_ok
run_lab -- --snapshot --until settings
check "first run exits 0" "0" "${STATUS}"
for p in data host auth settings; do check "first run: ${p}" "ran" "$(mf "phases.${p}.status")"; done
for p in model first-turn; do check "first run: ${p} after --until" "stopped" "$(mf "phases.${p}.status")"; done
check "snapshot saved" "saved" "$(mf snapshot)"
check "snapshot key = sha256 of sorted migration names" "${EXPECTED_KEY}" "$(cat "${LAB}/snapshot/key" 2>/dev/null)"
check "snapshot holds AppHost .data" "db-v1" "$(cat "${LAB}/snapshot/AppHost.data/node.db" 2>/dev/null)"
check "snapshot holds dp-keys" "ring-v1" "$(cat "${LAB}/snapshot/dp-keys/key.xml" 2>/dev/null)"
check "host started twice (before and after the snapshot)" "2" "$(count_lines "${CTL}/dev-start.calls")"
check "host stopped once for the snapshot" "1" "$(count_lines "${CTL}/dev-stop.calls")"
check "re-authenticated after the restart" "2" "$(calls auth)"
check "settingsApplied" '["externalAccessProfile", "defaultModelName"]' "$(mf settingsApplied)"
check "baseUrl from dev-status --json" "https://127.0.0.1:7443" "$(mf baseUrl)"
check "viteUrl" "http://127.0.0.1:5173" "$(mf viteUrl)"
check "hostPids" "[${GOOD_HOST}]" "$(mf hostPids)"
check "stoppedAt" "settings" "$(mf stoppedAt)"
check "token file holds the latest token" "tok-2-opaque" "$(cat "${LAB}/token" 2>/dev/null)"
check "token file is 600" "600" "$(stat -c %a "${LAB}/token" 2>/dev/null)"
check "xdg mirrors ~/.local/share children" "${FAKE_HOME}/.local/share/mise" "$(readlink "${LAB}/xdg/mise")"
CHECKS=$((CHECKS + 1))
if [[ -L "${LAB}/xdg/XE-Local-AI-Engine" || ! -d "${LAB}/xdg/XE-Local-AI-Engine" ]]; then
  echo "  FAIL: xdg/XE-Local-AI-Engine must be a real dir, never a link to the real one" >&2; FAILED=$((FAILED + 1))
fi
grep -q '^XDG_DATA_HOME=.*/.tmp/lab/test/xdg$' "${CTL}/dev-start.env"
check "dev-start runs with the scratch XDG_DATA_HOME" "0" "$?"
check "profile env applies when the caller has none" "1" "$(grep -cx 'XE_LLAMACPP_VARIANT=cuda' "${CTL}/dev-start.env")"
check "manifest runtime.variant" "cuda" "$(mf runtime.variant)"

echo "== 2. rerun on the same lab =="
settings_same
model_same
rm -f "${CTL}/dev-start.calls"
run_lab --
check "rerun exits 0" "0" "${STATUS}"
check "rerun: data" "skipped" "$(mf phases.data.status)"
check "rerun: snapshot not restored over existing data" "skipped-existing-data" "$(mf snapshot)"
check "rerun: host" "skipped" "$(mf phases.host.status)"
check "rerun: dev-start not called" "0" "$(count_lines "${CTL}/dev-start.calls")"
check_contains "rerun: host reason says the environment was verified" "this lab's environment" "$(mf phases.host.reason)"
check "rerun: auth always runs" "ran" "$(mf phases.auth.status)"
check "rerun: settings" "skipped" "$(mf phases.settings.status)"
check "rerun: model" "skipped" "$(mf phases.model.status)"
check "rerun: first-turn stopped by profile stop" "stopped" "$(mf phases.first-turn.status)"
check "rerun: modelResident" "false" "$(mf modelResident)"
check "rerun: settingsApplied empty" "[]" "$(mf settingsApplied)"
check "rerun: no download" "0" "$(calls download)"

echo "== 3. model switch =="
OTHER="org/Other-GGUF:Q8_0"
ctl settings-ensure.out "changed\tdefaultModelName\nskipped\tfalse\nverified\ttrue\n"
ctl model-ensure.out "installed\ttrue\npreviouslySelected\t${MODEL}\nselected\t${OTHER}\nejected\t${MODEL}\nresident\ttrue\n"
run_lab -- --until model --model "${OTHER}"
check "switch exits 0" "0" "${STATUS}"
check "switch: model ran" "ran" "$(mf phases.model.status)"
check_contains "switch: reason names the eject" "ejected=${MODEL}" "$(mf phases.model.reason)"
check_contains "switch: reason names the selection" "selected" "$(mf phases.model.reason)"
check "switch: manifest model" "${OTHER}" "$(mf model)"
check "switch: modelResident" "true" "$(mf modelResident)"
check "switch: settingsApplied" '["defaultModelName"]' "$(mf settingsApplied)"
check "switch: desired settings carry the new model" "${OTHER}" \
  "$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["defaultModelName"])' "${LAB}/desired-settings.json")"
ctl model-ensure.out "installed\ttrue\npreviouslySelected\t${OTHER}\nselected\t${OTHER}\nresident\tfalse\n"
run_lab -- --until model --model "${OTHER}"
check "already selected, nothing ejected => model skipped" "skipped" "$(mf phases.model.status)"
ctl model-ensure.out "installed\ttrue\npreviouslySelected\t${OTHER}\nselected\t${OTHER}\nejected\tstray\nresident\tfalse\n"
run_lab -- --until model --model "${OTHER}"
check "selected but something ejected => model ran" "ran" "$(mf phases.model.status)"
ctl model-ensure.out "installed\ttrue\npreviouslySelected\t${OTHER}\nselected\tsomething-else\nresident\tfalse\n"
run_lab -- --until model --model "${OTHER}"
check "select answering another model => exit 1" "1" "${STATUS}"

ctl model-ensure.out "installed\ttrue\npreviouslySelected\t${MODEL}\nselected\t${OTHER}\nejectFailed\t${MODEL}|timed_out_still_busy\nresident\tfalse\n"
run_lab -- --until model --model "${OTHER}"
check "eject that failed => exit 1" "1" "${STATUS}"
check_contains "eject failure names the model" "could not eject ${MODEL}" "${ERR}"

ctl model-ensure.out "installed\ttrue\npreviouslySelected\t${OTHER}\nselected\t${OTHER}\ngone\t${MODEL}\nresident\tfalse\n"
run_lab -- --until model --model "${OTHER}"
check "an eject that found the model already gone exits 0" "0" "${STATUS}"
check "an already-gone model leaves the model phase skipped" "skipped" "$(mf phases.model.status)"
check_absent "an already-gone model is no eject failure" "could not eject" "${ERR}"

echo "== 4. stale snapshot =="
echo "not-the-key" >"${LAB}/snapshot/key"
rm -rf -- "${DATA}"
echo 3 >"${CTL}/dev-status.exit"
run_lab -- --until data
check "stale run exits 0" "0" "${STATUS}"
check "stale snapshot reported" "stale" "$(mf snapshot)"
check "stale snapshot is not restored" "absent" "$([[ -e "${DATA}" ]] && echo present || echo absent)"
check "clean start removes the leftover dp-keys" "absent" "$([[ -e "${CLIENT}/dp-keys" ]] && echo present || echo absent)"
check "clean start removes the leftover node-settings" "absent" "$([[ -e "${CLIENT}/node-settings.json" ]] && echo present || echo absent)"
check_contains "data reason says clean start" "clean start" "$(mf phases.data.reason)"
check_contains "data reason names what it removed" "removed leftover dp-keys node-settings.json" "$(mf phases.data.reason)"
mkdir -p "${CLIENT}/dp-keys"
echo "ring-old" >"${CLIENT}/dp-keys/key.xml"
echo '{"s":0}' >"${CLIENT}/node-settings.json"
echo 0 >"${CTL}/dev-status.exit"
run_lab -- --until data
check "leftovers while the host runs => exit 3" "3" "${STATUS}"
check "host running: dp-keys kept" "ring-old" "$(cat "${CLIENT}/dp-keys/key.xml" 2>/dev/null)"
check "host running: node-settings kept" '{"s":0}' "$(cat "${CLIENT}/node-settings.json" 2>/dev/null)"
echo 3 >"${CTL}/dev-status.exit"

echo "== 5. snapshot restore =="
printf '%s\n' "${EXPECTED_KEY}" >"${LAB}/snapshot/key"
rm -rf -- "${CLIENT}/dp-keys" "${CLIENT}/node-settings.json"
run_lab -- --until data
check "restore run exits 0" "0" "${STATUS}"
check "snapshot restored" "restored" "$(mf snapshot)"
check "restore: data ran" "ran" "$(mf phases.data.status)"
check "restore: .data copied back" "db-v1" "$(cat "${DATA}/node.db" 2>/dev/null)"
check "restore: dp-keys copied back" "ring-v1" "$(cat "${CLIENT}/dp-keys/key.xml" 2>/dev/null)"
check "restore: node-settings copied back" '{"s":1}' "$(cat "${CLIENT}/node-settings.json" 2>/dev/null)"

echo "== 6. --fresh while the host runs =="
echo 0 >"${CTL}/dev-status.exit"
echo marker >"${LAB}/xdg/XE-Local-AI-Engine/marker"
run_lab -- --fresh
check "--fresh on a running host exits 3" "3" "${STATUS}"
check "--fresh refused: .data kept" "db-v1" "$(cat "${DATA}/node.db" 2>/dev/null)"
check "--fresh refused: dp-keys kept" "ring-v1" "$(cat "${CLIENT}/dp-keys/key.xml" 2>/dev/null)"
check "--fresh refused: xdg node dir kept" "marker" "$(cat "${LAB}/xdg/XE-Local-AI-Engine/marker" 2>/dev/null)"
check "--fresh refused: token kept" "present" "$([[ -f "${LAB}/token" ]] && echo present || echo absent)"

echo "== 7. missing model =="
settings_same
ctl model-ensure.out "installed\tfalse\n"
run_lab --
check "missing model without --download exits 2" "2" "${STATUS}"
check_contains "missing model names --download" "--download" "${ERR}"
check "no download attempted" "0" "$(calls download)"
ctl download.out "model\t${MODEL}\ntoken\ttok-renewed-opaque\ntokenRenewals\t1\ndownloaded\ttrue\nphase\tCompleted\n"
ctl model-ensure.2.out "installed\ttrue\npreviouslySelected\t${MODEL}\nselected\t${MODEL}\nresident\tfalse\n"
run_lab -- --download
check "--download exits 0" "0" "${STATUS}"
check "--download: model ran" "ran" "$(mf phases.model.status)"
check_contains "--download: reason says downloaded" "downloaded" "$(mf phases.model.reason)"
check "--download: one download" "1" "$(calls download)"
check "--download of the profile model uses modelSource.repo" "org/Model-GGUF" "$(argval "${CTL}/download.argv" --repo)"
check "--download of the profile model uses modelSource.file" "model-Q4_K_M.gguf" "$(argval "${CTL}/download.argv" --file)"
check "--download: model-ensure re-run after download" "2" "$(calls model-ensure)"
check "--download: manifest written after the renewal" "${MODEL}" "$(mf model)"
check "--download: renewed token kept" "tok-renewed-opaque" "$(cat "${LAB}/token" 2>/dev/null)"
check "--download: token file stays 600" "600" "$(stat -c %a "${LAB}/token" 2>/dev/null)"
check "--download: model-ensure after the download uses the renewed token" "1" \
  "$(grep -cx 'tok-renewed-opaque' "${CTL}/model-ensure.argv")"
check "--download: driver gets --email" "1" "$(grep -cx -- '--email' "${CTL}/download.argv")"
check "--download: driver gets --password and its value" "1" \
  "$(grep -A1 -x -- '--password' "${CTL}/download.argv" | grep -cx "${PASSWORD}")"
check_contains "--download: lab-up.log masks the download password" "--email admin@localhost.test --password ***" \
  "$(grep ' download ' "${LAB}/evidence/lab-up.log")"
ctl download.out "model\t${MODEL}\ndownloaded\tfalse\nphase\tFailed\n"
run_lab -- --download
check "incomplete download exits 1" "1" "${STATUS}"
LISTED="org/Listed-GGUF:Q8_0"
mkdir -p "${REPO}/scripts/model-matrix"
cat >"${REPO}/scripts/model-matrix/models.json" <<JSON
{ "models": [ { "modelName": "${LISTED}", "repo": "org/Listed-GGUF", "file": "listed-Q8_0.gguf" } ] }
JSON
ctl download.out "model\t${LISTED}\ndownloaded\ttrue\nphase\tCompleted\n"
ctl model-ensure.2.out "installed\ttrue\npreviouslySelected\t${MODEL}\nselected\t${LISTED}\nresident\tfalse\n"
run_lab -- --download --model "${LISTED}"
check "--model OTHER --download from the matrix exits 0" "0" "${STATUS}"
check "OTHER downloads from its matrix entry's repo" "org/Listed-GGUF" "$(argval "${CTL}/download.argv" --repo)"
check "OTHER downloads its matrix entry's file" "listed-Q8_0.gguf" "$(argval "${CTL}/download.argv" --file)"
run_lab -- --download --model "org/Unknown-GGUF:Q4_0"
check "OTHER with no download source => exit 2" "2" "${STATUS}"
check "no download attempted for an unknown source" "0" "$(calls download)"
check_contains "the unknown model is named" "org/Unknown-GGUF:Q4_0" "${ERR}"
rm -f "${CTL}/model-ensure.2.out"

echo "== 8. settings not verified =="
ctl settings-ensure.out "changed\texternalAccessProfile\nskipped\tfalse\nverified\tfalse\n"
run_lab --
check "verified=false exits 1" "1" "${STATUS}"
check "no manifest written on failure" "absent" "$([[ -e "${MANIFEST}" ]] && echo present || echo absent)"
settings_same
echo 1 >"${CTL}/auth.exit"
run_lab --
check "auth driver failure exits 1" "1" "${STATUS}"
rm -f "${CTL}/auth.exit"

echo "== 9. host status unknown / node never answers =="
echo 1 >"${CTL}/dev-status.exit"
run_lab -- --until host
check "dev-status exit 1 => exit 4" "4" "${STATUS}"
echo 0 >"${CTL}/dev-status.exit"
echo 7 >"${CTL}/curl-status.exit"
started="${SECONDS}"
run_lab -- --until host
check "auth/status never answers => exit 5" "5" "${STATUS}"
check "the wait honoured LAB_READY_TIMEOUT_SECONDS" "true" "$([[ $((SECONDS - started)) -le 15 ]] && echo true || echo false)"
rm -f "${CTL}/curl-status.exit"

echo 0 >"${CTL}/dev-status.exit"
echo "${WRONG_HOST}" >"${CTL}/host-pid"
run_lab -- --until host
check "running host with another models dir => exit 3" "3" "${STATUS}"
check_contains "the mismatch is named" "HuggingFace__ModelsDirectory=/elsewhere/models" "${ERR}"
echo "${DEAD_HOST}" >"${CTL}/host-pid"
run_lab -- --until host
check "running host whose environ is unreadable => exit 4" "4" "${STATUS}"
check_contains "the unreadable environ is named" "/proc/${DEAD_HOST}/environ" "${ERR}"
echo "${WRONG_VARIANT_HOST}" >"${CTL}/host-pid"
run_lab -- --until host
check "running host with another runtime variant => exit 3" "3" "${STATUS}"
check_contains "the variant mismatch names the key" "XE_LLAMACPP_VARIANT=vulkan, this run wants XE_LLAMACPP_VARIANT=cuda" "${ERR}"
echo "${MISSING_KEY_HOST}" >"${CTL}/host-pid"
run_lab -- --until host
check "running host without a profile env key => exit 3" "3" "${STATUS}"
check_contains "the missing key is named" "LAB_TEST_PROFILE_ONLY=<unset>" "${ERR}"
echo "${EXTRA_RUNTIME_HOST}" >"${CTL}/host-pid"
run_lab -- --until host
check "running host with a runtime override this run lacks => exit 3" "3" "${STATUS}"
check_contains "the one-sided override is named" "XE_LLAMACPP_SERVER_PATH=/opt/byo/llama-server, this run wants XE_LLAMACPP_SERVER_PATH=<unset>" "${ERR}"
echo "${GOOD_HOST}" >"${CTL}/host-pid"
echo '[]' >"${CTL}/vite-urls"
run_lab -- --until host
check "no Vite URL: run exits 0" "0" "${STATUS}"
check "no Vite URL: viteUrl is empty" "" "$(mf viteUrl)"
check "no Vite URL: hostPids keeps the pid" "[${GOOD_HOST}]" "$(mf hostPids)"
rm -f "${CTL}/vite-urls"
echo "${GOOD_HOST}" >"${CTL}/host-pid"

echo "== 12. caller env beats profile env =="
echo 3 >"${CTL}/dev-status.exit"
rm -rf -- "${DATA}"
run_lab XE_LLAMACPP_VARIANT=vulkan -- --until host
check "host run exits 0" "0" "${STATUS}"
check "caller's XE_LLAMACPP_VARIANT kept" "1" "$(grep -cx 'XE_LLAMACPP_VARIANT=vulkan' "${CTL}/dev-start.env")"
check "profile-only key still applied" "1" "$(grep -cx 'LAB_TEST_PROFILE_ONLY=from-profile' "${CTL}/dev-start.env")"

echo "== 14. startup keys persisted by the host start =="
PROFILE_FILE="${REPO}/scripts/lab/profiles/test.json"
cp "${PROFILE_FILE}" "${TEMP_ROOT}/test.json.orig"
python3 -c '
import json, sys
p = json.load(open(sys.argv[1]))
p["env"]["DevWorkflows__Enabled"] = "true"
json.dump(p, open(sys.argv[1], "w"))
' "${PROFILE_FILE}"
echo 3 >"${CTL}/dev-status.exit"
rm -f "${LAB}/host-env.keys"
run_lab -- --until host
check "host start with a feature-flag profile exits 0" "0" "${STATUS}"
KEYS="$(cat "${LAB}/host-env.keys" 2>/dev/null)"
for key in XDG_DATA_HOME HuggingFace__ModelsDirectory XE_LLAMACPP_SERVER_PATH XE_LLAMACPP_VARIANT LAB_TEST_PROFILE_ONLY \
    DevWorkflows__Enabled; do
  check "host-env.keys holds ${key}" "yes" "$(grep -qx "${key}" <<<"${KEYS}" && echo yes || echo no)"
done
start_host_stub "${LAB_HOST_ENV[@]}" "DevWorkflows__Enabled=true"
FLAG_HOST="${STUB_PID}"
echo "${FLAG_HOST}" >"${CTL}/host-pid"
run_lab -- --until host
check "same profile, same flagged host => reused (exit 0)" "0" "${STATUS}"
cp "${TEMP_ROOT}/test.json.orig" "${PROFILE_FILE}"
run_lab -- --until host
check "profile dropped the flag the host still carries => exit 3" "3" "${STATUS}"
check_contains "the dropped flag is named" "DevWorkflows__Enabled=true, this run wants DevWorkflows__Enabled=<unset>" "${ERR}"
echo "${GOOD_HOST}" >"${CTL}/host-pid"
run_lab -- --until host
check "back on the matching host => exit 0 (manifest rewritten for lab-api below)" "0" "${STATUS}"

echo "== 15. AppHost override pinned to this checkout =="
OWN_APPHOST="${REPO}/XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj"
OWN_KEY="${DATA}/node.key"
CHECKS=$((CHECKS + 1))
if [[ ! -s "${CTL}/apphost.log" ]] || grep -v " ${OWN_APPHOST} ${OWN_KEY}$" "${CTL}/apphost.log" >&2; then
  echo "  FAIL: every dev-start/dev-status/dev-stop call must see XE_ASPIRE_APPHOST=${OWN_APPHOST} and" \
    "XE_NODE_OPERATOR_SECRET_FILE=${OWN_KEY} (lines above)" >&2
  FAILED=$((FAILED + 1))
fi
check "the fakes saw dev-start, dev-status and dev-stop" "3" "$(cut -d' ' -f1 "${CTL}/apphost.log" | sort -u | wc -l | tr -d ' ')"
: >"${CTL}/apphost.log"
echo 0 >"${CTL}/dev-status.exit"
run_lab XE_ASPIRE_APPHOST=/other/XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj -- --fresh
check "another checkout's AppHost => exit 2" "2" "${STATUS}"
check_contains "the foreign AppHost is named" "names another AppHost" "${ERR}"
check "no dev script ran for a foreign AppHost" "0" "$(count_lines "${CTL}/apphost.log")"
check "--fresh with a foreign AppHost deleted nothing" "db-v1" "$(cat "${DATA}/node.db" 2>/dev/null)"
check "no manifest for a refused AppHost" "absent" "$([[ -e "${MANIFEST}" ]] && echo present || echo absent)"
rm -f -- "${MANIFEST}"
(cd "${REPO}" && "${TEST_ENV[@]}" XE_ASPIRE_APPHOST=XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj \
  "${REPO}/scripts/lab-up.sh" --profile test --until host >/dev/null 2>"${CTL}/stderr")
check "a relative override naming this checkout's AppHost is accepted" "0" "$?"
check "the accepted override reaches dev-status as the absolute path" "dev-status ${OWN_APPHOST} ${OWN_KEY}" \
  "$(sort -u "${CTL}/apphost.log")"

: >"${CTL}/apphost.log"
run_lab XE_NODE_OPERATOR_SECRET_FILE="${TEMP_ROOT}/elsewhere/node.key" -- --fresh
check "an operator secret outside this checkout's .data => exit 2" "2" "${STATUS}"
check_contains "the foreign secret file is named" "outside this checkout's node state" "${ERR}"
check "no dev script ran for a foreign secret file" "0" "$(count_lines "${CTL}/apphost.log")"
check "--fresh with a foreign secret file deleted nothing" "db-v1" "$(cat "${DATA}/node.db" 2>/dev/null)"
check "--fresh with a foreign secret file kept dp-keys" "present" "$([[ -d "${CLIENT}/dp-keys" ]] && echo present || echo absent)"
(cd "${REPO}" && "${TEST_ENV[@]}" XE_NODE_OPERATOR_SECRET_FILE=XE-Local-AI-Engine.AppHost/.data/node.key \
  "${REPO}/scripts/lab-up.sh" --profile test --until host >/dev/null 2>"${CTL}/stderr")
check "a relative secret path naming this checkout's node.key is accepted" "0" "$?"
check "the accepted secret path reaches dev-status as the absolute path" "dev-status ${OWN_APPHOST} ${OWN_KEY}" \
  "$(sort -u "${CTL}/apphost.log")"

echo "== 16. lab ownership (OWNER file) =="
# A second profile gets its own lab dir, so the `test` lab's manifest (read by section 10) stays as it is.
cp "${REPO}/scripts/lab/profiles/test.json" "${REPO}/scripts/lab/profiles/owner.json"
OWNED_LAB="${REPO}/.tmp/lab/owner"
OWNER="${OWNED_LAB}/OWNER"
# run_owner [VAR=value ...] -- lab-up args, like run_lab, on the `owner` profile up to the data phase.
run_owner() {
  local extra=()
  while [[ $# -gt 0 && "$1" != "--" ]]; do extra+=("$1"); shift; done
  shift
  "${TEST_ENV[@]}" ${extra[@]+"${extra[@]}"} "${REPO}/scripts/lab-up.sh" --profile owner --until data "$@" \
    >/dev/null 2>"${CTL}/stderr"
  STATUS=$?
  ERR="$(cat "${CTL}/stderr")"
}
owner_key() { sed -n "s/^$1=//p" "${OWNER}" 2>/dev/null; }
run_owner -- --plan plan-a --note "first round"
check "first bring-up exits 0" "0" "${STATUS}"
check "OWNER plan" "plan-a" "$(owner_key plan)"
check "OWNER session_note" "first round" "$(owner_key session_note)"
check "OWNER worktree is the checkout that ran lab-up" "${REPO}" "$(owner_key worktree)"
CREATED="$(owner_key created)"
check "OWNER created is ISO-8601 UTC" "ok" "$([[ "${CREATED}" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$ ]] && echo ok)"
check "OWNER delete_after is created + 14 days" "$(date -u -d "${CREATED} + 14 days" +%Y-%m-%d)" "$(owner_key delete_after)"
check "a new lab is neither adopted nor taken over" "0" "$(grep -c '^adopted=\|^taken_over=' "${OWNER}")"
check "OWNER keys in order" "plan created delete_after worktree session_note" "$(cut -d= -f1 "${OWNER}" | paste -sd' ' -)"

run_owner -- --plan plan-b
check "another plan on an owned lab => exit 2" "2" "${STATUS}"
check_contains "the refusal names the owning plan" "owned by plan 'plan-a'" "${ERR}"
check_contains "the refusal names the requested plan" "asks for plan 'plan-b'" "${ERR}"
check "the refusal leaves OWNER alone" "plan-a" "$(owner_key plan)"
run_owner -- --note "default plan"
check "no --plan means plan 'unassigned', refused on a plan-a lab" "2" "${STATUS}"
run_owner XE_LAB_PLAN=plan-a --
check "XE_LAB_PLAN names the plan" "0" "${STATUS}"
check "the owning plan's rerun keeps OWNER" "first round" "$(owner_key session_note)"

printf 'plan=plan-a\ncreated=2026-01-01T00:00:00Z\ndelete_after=2026-01-15\nworktree=/old\nsession_note=\n' >"${OWNER}"
run_owner -- --plan plan-b --take-over --note "second round"
check "--take-over exits 0" "0" "${STATUS}"
check "--take-over: new plan" "plan-b" "$(owner_key plan)"
check "--take-over keeps created" "2026-01-01T00:00:00Z" "$(owner_key created)"
check "--take-over keeps delete_after = created + 14 days" "2026-01-15" "$(owner_key delete_after)"
check "--take-over: worktree rewritten" "${REPO}" "$(owner_key worktree)"
check "--take-over: note rewritten" "second round" "$(owner_key session_note)"
check "--take-over records taken_over" "ok" "$([[ "$(owner_key taken_over)" =~ ^[0-9]{4}-.*Z$ ]] && echo ok)"

rm -f -- "${OWNER}"
run_owner -- --plan plan-c
check "a lab without OWNER is adopted, not refused" "0" "${STATUS}"
check "adoption: plan" "plan-c" "$(owner_key plan)"
check "adoption records adopted" "ok" "$([[ "$(owner_key adopted)" =~ ^[0-9]{4}-.*Z$ ]] && echo ok)"
check_contains "adoption is logged" "adopted by plan 'plan-c'" "${ERR}"

run_owner -- --plan 'bad plan'
check "an invalid plan name => exit 2" "2" "${STATUS}"
check "an invalid plan name leaves OWNER alone" "plan-c" "$(owner_key plan)"

echo "== 11. redaction =="
LOG="$(cat "${LAB}/evidence/lab-up.log")"
check_contains "lab-up.log records the auth call" "--password ***" "${LOG}"
check_absent "lab-up.log never holds the password" "${PASSWORD}" "${LOG}"
check "lab-up.log never holds a token" "0" "$(grep -c 'tok-[a-z0-9]*-opaque' "${LAB}/evidence/lab-up.log")"
check_absent "manifest never holds a token" "-opaque" "$(cat "${MANIFEST}")"

echo "== 10. lab-api.sh =="
API_ENV=("${TEST_ENV[@]}")
echo "tok-old-opaque" >"${LAB}/token"
rm -f "${CTL}/calls.log" "${CTL}/curl.log"
ctl curl-queue "401\t{\"error\":\"expired\"}\n200\t{\"ok\":true,\"accessToken\":\"leak-me\"}\n"
out="$("${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test POST chat/conversations \
  "{\"password\":\"${PASSWORD}\",\"title\":\"t\"}" 2>&1)"; status=$?
check "401 then 200 exits 0" "0" "${status}"
check_contains "prints the final code" "[HTTP 200]" "${out}"
check "exactly one re-login" "1" "$(calls auth)"
check "a plain call's re-login gets --timeout 120" "120" "$(argval "${CTL}/auth.argv" --timeout)"
check "token file rewritten" "tok-1-opaque" "$(cat "${LAB}/token")"
check "first call used the old token" "1" "$(grep -c 'POST .*chat/conversations Authorization: Bearer tok-old-opaque' "${CTL}/curl.log")"
check "retry used the new token" "1" "$(grep -c 'POST .*chat/conversations Authorization: Bearer tok-1-opaque' "${CTL}/curl.log")"
HTTP_LOG="$(cat "${LAB}/evidence/http.log" 2>/dev/null)"
check_contains "http.log has the request" "POST chat/conversations" "${HTTP_LOG}"
# An empty-body regression (the redactor reading nothing) would pass every "never holds" check above.
check_contains "http.log logs a non-empty request body" '>> {"password": "(redacted)", "title": "t"}' "${HTTP_LOG}"
check_contains "http.log logs a non-empty response body" '"ok": true' "${HTTP_LOG}"
check_contains "http.log has the redacted password field" '"password": "(redacted)"' "${HTTP_LOG}"
check_absent "http.log never holds the password" "${PASSWORD}" "${HTTP_LOG}"
check_absent "http.log never holds a response token" "leak-me" "${HTTP_LOG}"
check_absent "http.log never holds the bearer token" "tok-1-opaque" "${HTTP_LOG}"
check "every curl call is bounded (connect 10, max 120)" "2" "$(grep -c ' ct=10 mt=120$' "${CTL}/curl.log")"
check "the 401 retry keeps the first call's max-time" "1" "$(grep -c 'tok-1-opaque ct=10 mt=120$' "${CTL}/curl.log")"

echo 7 >"${CTL}/curl-exit"
ctl curl-queue "200\t{\"ok\":tr\n"
out="$("${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test GET models 2>&1)"; status=$?
check "200 then a transport error => exit 1" "1" "${status}"
check_contains "a broken body reports HTTP 000" "[HTTP 000]" "${out}"
rm -f "${CTL}/curl-exit"
ctl curl-queue "200\t{\"ok\":true}\n"
out="$("${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test GET models 2>&1)"; status=$?
check "200 with a clean exit => exit 0" "0" "${status}"
check_contains "a clean 200 reports HTTP 200" "[HTTP 200]" "${out}"

rm -f "${CTL}/calls.log"
ctl curl-queue "401\t{}\n401\t{}\n"
"${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test GET models >/dev/null 2>&1
check "a second 401 is returned, not retried again" "1" "$(calls auth)"

rm -f "${CTL}/curl.log"
ctl curl-queue "200\t{\"messages\":[{\"status\":\"Completed\",\"role\":\"assistant\",\"content\":\"hi\"}]}\n"
out="$("${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test settle conv-1 5 2>&1)"; status=$?
check "settle on a terminal status exits 0" "0" "${status}"
check_contains "settle prints the last message" "Completed | assistant |  | hi" "${out}"
check "a settle budget of 5 s bounds the poll at 5 s" "1" "$(grep -c 'conv-1 .* ct=10 mt=5$' "${CTL}/curl.log")"
rm -f "${CTL}/curl.log"
: >"${CTL}/curl-queue"
ctl curl-default "200\t{\"messages\":[{\"status\":\"Running\",\"role\":\"assistant\"}]}"
out="$("${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test settle conv-1 1 2>&1)"; status=$?
check "settle timeout exits 1" "1" "${status}"
check "settle polls are bounded by the remaining budget" "$(grep -c 'conv-1 ' "${CTL}/curl.log")" \
  "$(grep -c 'conv-1 .* ct=10 mt=1$' "${CTL}/curl.log")"
check_contains "settle timeout is reported" "TIMEOUT" "${out}"

ctl curl-default "200\t{\"messages\":[{\"status\":\"Completed\",\"role\":\"assistant\"}]}"
echo 7 >"${CTL}/curl-exit"
out="$("${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test settle conv-2 1 2>&1)"; status=$?
check "settle: terminal body on a failed transfer does not settle" "1" "${status}"
check_contains "settle: failed transfer reports TIMEOUT with 000" "[HTTP 000]" "${out}"
rm -f "${CTL}/curl-exit"
ctl curl-default "500\t{\"messages\":[{\"status\":\"Completed\",\"role\":\"assistant\"}]}"
out="$("${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test settle conv-2 1 2>&1)"; status=$?
check "settle: terminal body on HTTP 500 does not settle" "1" "${status}"
check_contains "settle: HTTP 500 reports TIMEOUT" "TIMEOUT last=http 500" "${out}"

: >"${LAB}/evidence/http.log"
ctl curl-queue "200\t{\"accessToken\":\"resp-secret-1\",\"nested\":{\"refreshToken\":\"resp-secret-2\"},\"title\":\"kept\"}\n"
"${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test POST auth/login \
  '{"password":"prefix\"SECRET_REMAINDER","email":"a"}' >/dev/null 2>&1
HTTP_LOG="$(cat "${LAB}/evidence/http.log")"
check_absent "escaped quote in a password leaks nothing" "SECRET_REMAINDER" "${HTTP_LOG}"
check_contains "the request's other fields are kept" '"email"' "${HTTP_LOG}"
check_absent "response accessToken scrubbed" "resp-secret-1" "${HTTP_LOG}"
check_absent "nested response refreshToken scrubbed" "resp-secret-2" "${HTTP_LOG}"
check_contains "the response's other fields are kept" '"title": "kept"' "${HTTP_LOG}"
ctl curl-queue "200\tplain text body\n"
"${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test GET health >/dev/null 2>&1
check_contains "a non-JSON body passes through unchanged" "plain text body" "$(cat "${LAB}/evidence/http.log")"

mkdir -p "${REPO}/.tmp/lab/remote"
echo '{"baseUrl":"https://example.com"}' >"${REPO}/.tmp/lab/remote/manifest.json"
echo tok >"${REPO}/.tmp/lab/remote/token"
"${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile remote GET models >/dev/null 2>&1
check "non-loopback baseUrl => exit 2" "2" "$?"
"${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile nolab GET models >/dev/null 2>&1
check "no lab => exit 2" "2" "$?"

rm -f "${CTL}/curl.log" "${CTL}/calls.log"
ctl curl-queue "401\t{}\n200\t{\"messages\":[{\"status\":\"Completed\",\"role\":\"assistant\"}]}\n"
out="$("${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test settle conv-3 2 2>&1)"; status=$?
check "settle across a 401 settles" "0" "${status}"
check "settle re-logs-in once" "1" "$(calls auth)"
login_timeout="$(argval "${CTL}/auth.argv" --timeout)"
check "settle's re-login is bounded by the 2 s budget (got --timeout ${login_timeout})" "yes" \
  "$([[ "${login_timeout}" =~ ^[0-9]+$ && "${login_timeout}" -ge 1 && "${login_timeout}" -le 2 ]] && echo yes || echo no)"
retry_mt="$(sed -n 2p "${CTL}/curl.log" | sed -E 's/.* mt=//')"
check "settle's retry is bounded by the remaining budget (got --max-time ${retry_mt})" "yes" \
  "$([[ "${retry_mt}" =~ ^[0-9]+$ && "${retry_mt}" -ge 1 && "${retry_mt}" -le 2 ]] && echo yes || echo no)"

rm -f "${CTL}/calls.log"
echo 10 >"${CTL}/auth.sleep"
ctl curl-queue "401\t{}\n"
ctl curl-default "200\t{\"messages\":[{\"status\":\"Running\"}]}"
started="${SECONDS}"
out="$("${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test settle conv-4 2 2>&1)"; status=$?
elapsed=$((SECONDS - started))
rm -f "${CTL}/auth.sleep"
check "settle with a stalled re-login exits 1" "1" "${status}"
check "settle with a stalled re-login ends within 5 s (took ${elapsed} s)" "yes" "$([[ "${elapsed}" -le 5 ]] && echo yes || echo no)"
check_contains "the stalled re-login is named" "re-login after 401 timed out" "${out}"

rm -f "${CTL}/calls.log"
echo 2 >"${CTL}/curl-sleep"
ctl curl-queue "401\t{}\n"
out="$("${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test settle conv-5 1 2>&1)"; status=$?
rm -f "${CTL}/curl-sleep"
check "a 401 after the deadline => exit 1" "1" "${status}"
check_contains "a 401 after the deadline reports TIMEOUT" "TIMEOUT last=http 401" "${out}"
check "a 401 after the deadline never re-logs-in" "0" "$(calls auth)"

echo "== 13. lab-driver evidence redaction against a loopback fake node =="
cat >"${TEMP_ROOT}/fake-node.py" <<'NODE'
import json, os, sys
from http.server import BaseHTTPRequestHandler, HTTPServer

BODY = {"accessToken": "tok-secret-1", "nested": {"refreshToken": "tok-secret-2", "apiKey": "key-secret-3"},
        "promptTokens": 5, "externalAccessProfile": "online"}

class Handler(BaseHTTPRequestHandler):
    def answer(self, payload):
        data = json.dumps(payload).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)
    def do_GET(self):
        self.answer(BODY)
    def do_PUT(self):
        self.rfile.read(int(self.headers.get("Content-Length") or 0))
        self.answer({})
    def log_message(self, *args):
        pass

server = HTTPServer(("127.0.0.1", 0), Handler)
with open(sys.argv[1] + ".tmp", "w") as handle:
    handle.write(str(server.server_address[1]))
os.replace(sys.argv[1] + ".tmp", sys.argv[1])
server.serve_forever()
NODE
python3 "${TEMP_ROOT}/fake-node.py" "${CTL}/node-port" </dev/null >/dev/null 2>&1 &
HELPER_PIDS+=("$!")
for _ in $(seq 100); do [[ -s "${CTL}/node-port" ]] && break; "${SLEEP_BIN}" 0.05; done  # real-timer: server startup
EV="${TEMP_ROOT}/driver-evidence"
python3 "${REAL}/lab-driver.py" settings-ensure --base-url "http://127.0.0.1:$(cat "${CTL}/node-port" 2>/dev/null)" \
  --token t --evidence "${EV}" --desired '{"externalAccessProfile":"offline","adminPassword":"pw-secret-4"}' >/dev/null 2>&1
check "real settings-ensure against the fake node exits 0" "0" "$?"
JSONL="$(cat "${EV}/http.jsonl" 2>/dev/null)"
check "http.jsonl has GET, PUT, GET" "GET PUT GET" \
  "$(python3 -c 'import json,sys; print(" ".join(json.loads(l)["method"] for l in open(sys.argv[1])))' "${EV}/http.jsonl" 2>/dev/null)"
for secret in tok-secret-1 tok-secret-2 key-secret-3 pw-secret-4; do
  check_absent "http.jsonl never holds ${secret}" "${secret}" "${JSONL}"
done
check_contains "http.jsonl keeps non-secret token counters" "promptTokens" "${JSONL}"
check_contains "http.jsonl keeps the PUT's other fields" "externalAccessProfile" "${JSONL}"
# The sink scrubs too, so a record that bypasses RecordingClient (hub events) cannot write a credential.
python3 -c '
import importlib.util, json, sys
from pathlib import Path
spec = importlib.util.spec_from_file_location("lab_driver", sys.argv[1])
driver = importlib.util.module_from_spec(spec)
spec.loader.exec_module(driver)
driver.append_jsonl(Path(sys.argv[2]), {"event": "x", "payload": {"accessToken": "sink-secret-5"}})
# A chat stream tool event: Arguments and Result are STRINGS holding JSON.
driver.append_jsonl(Path(sys.argv[2]), {"event": "ToolCall", "payload": {
    "arguments": json.dumps({"path": "kept-path", "password": "sink-secret-6"}),
    "result": json.dumps([{"apiKey": "sink-secret-7"}]),
    "text": "{not json sink-kept-8"}})
' "${REAL}/lab-driver.py" "${EV}/sink.jsonl" >/dev/null 2>&1
SINK="$(cat "${EV}/sink.jsonl" 2>/dev/null)"
check_contains "append_jsonl wrote the record" '"accessToken": "(redacted)"' "${SINK}"
check_absent "append_jsonl never writes sink-secret-5" "sink-secret-5" "${SINK}"
check_absent "a password inside a JSON-string arguments field is scrubbed" "sink-secret-6" "${SINK}"
check_absent "an apiKey inside a JSON-string result array is scrubbed" "sink-secret-7" "${SINK}"
check_contains "the JSON-string arguments keep their other fields" "kept-path" "${SINK}"
check_contains "a string that only looks like JSON passes through unchanged" "sink-kept-8" "${SINK}"

echo
if [[ "${FAILED}" -ne 0 ]]; then
  echo "lab-up.test.sh: ${FAILED} of ${CHECKS} checks FAILED" >&2
  exit 1
fi
if [[ "${CHECKS}" -eq 0 ]]; then
  echo "lab-up.test.sh: ZERO checks ran — this is not a pass." >&2
  exit 1
fi
echo "lab-up.test.sh: ${CHECKS} checks passed"
echo "lab-up.test.sh: PASS"
