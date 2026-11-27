#!/usr/bin/env bash
#
# Behavioural tests for scripts/lab-up.sh and scripts/lab-api.sh.
#
# Offline, no host, no network. The scripts under test are copied into a temp checkout; every Python driver in
# that copy is a dispatcher: the subcommands that talk to a node (auth, settings-ensure, runtime-ensure, model-ensure,
# download, first-turn) print scripted records, everything else (snapshot-key, manifest, base-url, model-link) execs the REAL
# driver.
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
FAKE = ("auth", "settings-ensure", "runtime-ensure", "model-ensure", "download", "first-turn")


def main():
    name = os.path.basename(sys.argv[0])
    sub = next((a for a in sys.argv[1:] if a in FAKE + ("snapshot-key", "manifest", "base-url", "model-link")), "")
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
    -w|--data-raw) shift 2 ;;
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
# Linked after the chmod, which follows symlinks; the real interpreter, as lab-up.sh empties the shim's MISE_DATA_DIR.
ln -s "$(python3 -c 'import sys; print(sys.executable)')" "${BIN}/python3"

TEST_ENV=(env -u XE_LAB_MODEL_SOURCE_DIR -u GIT_DIR -u GIT_WORK_TREE -u XE_LAB_PLAN -u XE_ASPIRE_APPHOST -u XE_NODE_OPERATOR_SECRET_FILE -u XE_LLAMACPP_VARIANT -u XE_LLAMACPP_SERVER_PATH -u LAB_TEST_PROFILE_ONLY
  -u XDG_DATA_HOME -u MISE_TRUSTED_CONFIG_PATHS -u MISE_DATA_DIR HOME="${FAKE_HOME}" PATH="${BIN}:${PATH}" LAB_TEST_CTL="${CTL}" LAB_TEST_REAL="${REAL}"
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
  # --cpu: TEST_ENV unsets the bring-your-own runtime, which section 17 covers on its own.
  "${TEST_ENV[@]}" ${extra[@]+"${extra[@]}"} "${REPO}/scripts/lab-up.sh" --profile test --cpu "$@" >/dev/null 2>"${CTL}/stderr"
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
runtime_present() { ctl runtime-ensure.out "present\ttrue\ntag\tb100\nvariant\tcuda\n"; }
runtime_present

echo "== 1. first run, --snapshot --until settings =="
settings_ok
run_lab -- --snapshot --until settings
check "first run exits 0" "0" "${STATUS}"
for p in data host auth settings; do check "first run: ${p}" "ran" "$(mf "phases.${p}.status")"; done
for p in runtime model first-turn; do check "first run: ${p} after --until" "stopped" "$(mf "phases.${p}.status")"; done
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
check "rerun: runtime present => skipped" "skipped" "$(mf phases.runtime.status)"
check "rerun: runtime reason names tag and variant" "runtime present b100 cuda" "$(mf phases.runtime.reason)"
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

echo "== 3b. managed llama.cpp runtime =="
settings_same
model_same
ctl runtime-ensure.out "present\tfalse\ntag\tb200\nvariant\tvulkan\n"
run_lab -- --until model
check "runtime missing: exits 0" "0" "${STATUS}"
check "runtime missing: runtime-ensure called once" "1" "$(calls runtime-ensure)"
check "runtime missing: runtime ran" "ran" "$(mf phases.runtime.status)"
check "runtime missing: reason names the install" "runtime installed b200 vulkan" "$(mf phases.runtime.reason)"
check "runtime missing: model phase judged on its own" "skipped" "$(mf phases.model.status)"
check "runtime-ensure gets --email" "admin@localhost.test" "$(argval "${CTL}/runtime-ensure.argv" --email)"
check_contains "lab-up.log masks the runtime-ensure password" "--password ***" "$(grep ' runtime-ensure ' "${LAB}/evidence/lab-up.log")"
ctl runtime-ensure.out "present\tfalse\ntag\tb200\nvariant\tvulkan\ntoken\ttok-runtime-opaque\n"
run_lab -- --until model
check "runtime renewal: renewed token kept" "tok-runtime-opaque" "$(cat "${LAB}/token" 2>/dev/null)"
check "runtime renewal: model-ensure uses the renewed token" "tok-runtime-opaque" "$(argval "${CTL}/model-ensure.argv" --token)"
runtime_present
run_lab -- --until settings
check "--until settings: runtime stopped" "stopped" "$(mf phases.runtime.status)"
check "--until settings: runtime-ensure not called" "0" "$(calls runtime-ensure)"
echo 1 >"${CTL}/runtime-ensure.exit"
run_lab -- --until model
check "runtime-ensure failure exits 1" "1" "${STATUS}"
check_contains "runtime-ensure failure is named" "runtime-ensure failed" "${ERR}"
check "runtime-ensure failure: model phase not reached" "0" "$(calls model-ensure)"
rm -f "${CTL}/runtime-ensure.exit"
# A bring-your-own server: the host is (re)started with the override, and nothing is installed.
echo 3 >"${CTL}/dev-status.exit"
run_lab XE_LLAMACPP_SERVER_PATH=/opt/byo/llama-server -- --until model
check "BYO: exits 0" "0" "${STATUS}"
check "BYO: runtime-ensure never called" "0" "$(calls runtime-ensure)"
check "BYO: runtime skipped" "skipped" "$(mf phases.runtime.status)"
check_contains "BYO: reason names the override" "bring-your-own llama-server" "$(mf phases.runtime.reason)"
echo 0 >"${CTL}/dev-status.exit"

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
check_contains "missing model names the failed link attempt" "could not be linked" "${ERR}"
check "the link was attempted against the (empty) user store" "1" "$(calls model-link)"
check "no download attempted" "0" "$(calls download)"
ctl model-ensure.out "installed\tfalse\nnoGguf\ttrue\n"
run_lab --
check_contains "a node with no GGUF at all gets the symlink hint" "a models dir of symlinks is rejected" "${ERR}"
ctl model-ensure.out "installed\tfalse\n"
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

echo "== 7b. missing model linked from the user-level store =="
STORE="${FAKE_HOME}/.local/share/XE-Local-AI-Engine/models"
LAB_MODELS="${REPO}/.tmp/lab/models"
WEIGHT="org-model-gguf-Q4_K_M-0123.gguf"
PROJECTOR="org-model-gguf-projector-0123.gguf"
mkdir -p "${STORE}"
printf 'weights' >"${STORE}/${WEIGHT}"
printf 'projector' >"${STORE}/${PROJECTOR}"
echo '{"SchemaVersion":2,"LocalFileName":"'"${WEIGHT}"'"}' >"${STORE}/${WEIGHT}.xe-model.json"
cat >"${STORE}/index.json" <<JSON
{ "Models": [ { "ModelName": "${MODEL}", "FileName": "${WEIGHT}", "LocalPath": "${STORE}/${WEIGHT}",
  "SizeBytes": 7, "Sha256": "$(printf 'weights' | sha256sum | cut -d' ' -f1)",
  "ProjectorLocalPath": "${STORE}/${PROJECTOR}", "ProjectorSizeBytes": 9,
  "ProjectorSha256": "$(printf 'projector' | sha256sum | cut -d' ' -f1)" } ] }
JSON
STORE_INDEX="$(cat "${STORE}/index.json")"
lab_index() {
  python3 -c 'import json, sys; m = json.load(open(sys.argv[1]))["Models"]; print(len(m), m[0]["LocalPath"], m[0]["ProjectorLocalPath"])' \
    "${LAB_MODELS}/index.json" 2>/dev/null || echo "<no lab index>"
}
ctl model-ensure.out "installed\tfalse\n"
ctl model-ensure.2.out "installed\ttrue\npreviouslySelected\t${MODEL}\nselected\t${MODEL}\nresident\tfalse\n"
run_lab --
check "linked from the store exits 0" "0" "${STATUS}"
check "link: model ran" "ran" "$(mf phases.model.status)"
check_contains "link: reason names the store" "linked from ${STORE}" "$(mf phases.model.reason)"
check "link: one model-link call" "1" "$(calls model-link)"
check "link: model-ensure re-run after the link" "2" "$(calls model-ensure)"
check "link: no download" "0" "$(calls download)"
check "link: the weights are a hard link (same inode)" "$(stat -c %i "${STORE}/${WEIGHT}")" "$(stat -c %i "${LAB_MODELS}/${WEIGHT}" 2>/dev/null)"
check "link: the projector is a hard link (same inode)" "$(stat -c %i "${STORE}/${PROJECTOR}")" \
  "$(stat -c %i "${LAB_MODELS}/${PROJECTOR}" 2>/dev/null)"
CHECKS=$((CHECKS + 1))
if [[ ! -f "${LAB_MODELS}/${WEIGHT}.xe-model.json" || "$(stat -c %i "${LAB_MODELS}/${WEIGHT}.xe-model.json")" == "$(stat -c %i "${STORE}/${WEIGHT}.xe-model.json")" ]]; then
  echo "  FAIL: link: the sidecar must be a copy, not a link" >&2; FAILED=$((FAILED + 1))
fi
check "link: the lab index names the lab paths" "1 ${LAB_MODELS}/${WEIGHT} ${LAB_MODELS}/${PROJECTOR}" "$(lab_index)"
check_contains "link: the driver reports two members linked" "$(printf 'members\t2')" "$(cat "${LAB}/evidence/lab-up.log")"
check "link: the store index is untouched" "${STORE_INDEX}" "$(cat "${STORE}/index.json")"
INODE="$(stat -c %i "${LAB_MODELS}/${WEIGHT}")"
run_lab --
check "link again: idempotent rerun exits 0" "0" "${STATUS}"
check "link again: same inode" "${INODE}" "$(stat -c %i "${LAB_MODELS}/${WEIGHT}" 2>/dev/null)"
check "link again: still one index entry" "1 ${LAB_MODELS}/${WEIGHT} ${LAB_MODELS}/${PROJECTOR}" "$(lab_index)"
run_lab -- --no-link
check "--no-link exits 2" "2" "${STATUS}"
check "--no-link: no link attempted" "0" "$(calls model-link)"
check_contains "--no-link names --download" "--download" "${ERR}"
run_lab -- --model "org/Absent-GGUF:Q4_0"
check "a model the store lacks exits 2" "2" "${STATUS}"
check_contains "a model the store lacks names --download" "--download" "${ERR}"
check "a model the store lacks: link attempted once" "1" "$(calls model-link)"
run_lab XE_LAB_MODEL_SOURCE_DIR="${TEMP_ROOT}/no-store" --
check "XE_LAB_MODEL_SOURCE_DIR without an index exits 2" "2" "${STATUS}"
check_contains "XE_LAB_MODEL_SOURCE_DIR is the dir that was tried" "${TEMP_ROOT}/no-store" "${ERR}"
rm -f "${CTL}/model-ensure.2.out"

echo "== 7c. model-link reads relative entries from the source dir and writes absolute targets =="
REL_STORE="${TEMP_ROOT}/rel-store"
mkdir -p "${REL_STORE}" "${TEMP_ROOT}/rel-cwd"
printf 'weights' >"${REL_STORE}/${WEIGHT}"
printf 'projector' >"${REL_STORE}/${PROJECTOR}"
cat >"${REL_STORE}/index.json" <<JSON
{ "Models": [ { "ModelName": "${MODEL}", "LocalPath": ".tmp/lab/models/${WEIGHT}",
  "ProjectorLocalPath": ".tmp/lab/models/${PROJECTOR}" } ] }
JSON
(cd "${TEMP_ROOT}/rel-cwd" && python3 "${REAL}/lab-driver.py" model-link --model "${MODEL}" --from "${REL_STORE}" \
  --into rel-into >/dev/null 2>&1)
check "relative entries: model-link exits 0 from an unrelated cwd" "0" "$?"
check "relative entries: the weights are a hard link (same inode)" "$(stat -c %i "${REL_STORE}/${WEIGHT}")" \
  "$(stat -c %i "${TEMP_ROOT}/rel-cwd/rel-into/${WEIGHT}" 2>/dev/null)"
check "relative entries: the target index holds absolute paths" \
  "1 ${TEMP_ROOT}/rel-cwd/rel-into/${WEIGHT} ${TEMP_ROOT}/rel-cwd/rel-into/${PROJECTOR}" \
  "$(LAB_MODELS="${TEMP_ROOT}/rel-cwd/rel-into" lab_index)"

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
p["env"]["GraphWorkflows__Enabled"] = "true"
json.dump(p, open(sys.argv[1], "w"))
' "${PROFILE_FILE}"
echo 3 >"${CTL}/dev-status.exit"
rm -f "${LAB}/host-env.keys"
run_lab -- --until host
check "host start with a feature-flag profile exits 0" "0" "${STATUS}"
KEYS="$(cat "${LAB}/host-env.keys" 2>/dev/null)"
for key in XDG_DATA_HOME HuggingFace__ModelsDirectory XE_LLAMACPP_SERVER_PATH XE_LLAMACPP_VARIANT LAB_TEST_PROFILE_ONLY \
    GraphWorkflows__Enabled; do
  check "host-env.keys holds ${key}" "yes" "$(grep -qx "${key}" <<<"${KEYS}" && echo yes || echo no)"
done
start_host_stub "${LAB_HOST_ENV[@]}" "GraphWorkflows__Enabled=true"
FLAG_HOST="${STUB_PID}"
echo "${FLAG_HOST}" >"${CTL}/host-pid"
run_lab -- --until host
check "same profile, same flagged host => reused (exit 0)" "0" "${STATUS}"
cp "${TEMP_ROOT}/test.json.orig" "${PROFILE_FILE}"
run_lab -- --until host
check "profile dropped the flag the host still carries => exit 3" "3" "${STATUS}"
check_contains "the dropped flag is named" "GraphWorkflows__Enabled=true, this run wants GraphWorkflows__Enabled=<unset>" "${ERR}"
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
  "${REPO}/scripts/lab-up.sh" --profile test --cpu --until host >/dev/null 2>"${CTL}/stderr")
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
  "${REPO}/scripts/lab-up.sh" --profile test --cpu --until host >/dev/null 2>"${CTL}/stderr")
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
  "${TEST_ENV[@]}" ${extra[@]+"${extra[@]}"} "${REPO}/scripts/lab-up.sh" --profile owner --cpu --until data "$@" \
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

echo "== 17. runtime: bring-your-own CUDA server or an explicit --cpu =="
: >"${CTL}/apphost.log"
before_owner="$(cat "${OWNER}")"
"${TEST_ENV[@]}" "${REPO}/scripts/lab-up.sh" --profile owner --until data --plan plan-c >/dev/null 2>"${CTL}/stderr"
STATUS=$?
ERR="$(cat "${CTL}/stderr")"
check "no XE_LLAMACPP_SERVER_PATH and no --cpu => exit 2" "2" "${STATUS}"
check "the refusal is one line" "1" "$(grep -c . <<<"${ERR}")"
check_contains "the refusal names the variable and the flag" "REFUSED: XE_LLAMACPP_SERVER_PATH is not set" "${ERR}"
check_contains "the refusal offers --cpu" "pass --cpu" "${ERR}"
check "a refused run calls no dev script" "0" "$(count_lines "${CTL}/apphost.log")"
check "a refused run leaves OWNER alone" "${before_owner}" "$(cat "${OWNER}")"
"${TEST_ENV[@]}" XE_LLAMACPP_SERVER_PATH=/opt/byo/llama-server "${REPO}/scripts/lab-up.sh" --profile owner --until data \
  --plan plan-c >/dev/null 2>"${CTL}/stderr"
check "a bring-your-own server is accepted without --cpu" "0" "$?"
run_owner -- --plan plan-c
check "--cpu is accepted without a bring-your-own server" "0" "${STATUS}"

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
ctl curl-queue "401\t{\"error\":\"expired\"}\n200\t{\"ok\":true,\"accessToken\":\"leak-me\",\"key\":\"xemcp_key-leak\",\"privateKey\":\"pk-leak\",\"pem\":\"-----BEGIN PRIVATE KEY-----pem-leak\",\"mcp\":{\"apiKey\":{\"prefix\":\"p\"},\"key\":\"mcp-shape-leak\"},\"setting\":{\"key\":\"keep-generic-key\"},\"integration\":{\"secret_value\":\"xeint_int-leak\"},\"proxy\":[\"xeprx_prx-leak\"]}\n"
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
# An inbound MCP key create answers the plaintext key once, under "key".
check_absent "http.log never holds a created key" "xemcp_key-leak" "${HTTP_LOG}"
check_absent "http.log never holds a private key" "pk-leak" "${HTTP_LOG}"
check_absent "http.log never holds a PEM block" "pem-leak" "${HTTP_LOG}"
check_absent "http.log never holds the key of an MCP key response" "mcp-shape-leak" "${HTTP_LOG}"
check_contains "http.log keeps a generic key field" "keep-generic-key" "${HTTP_LOG}"
check_absent "http.log never holds an integration key" "int-leak" "${HTTP_LOG}"
check_absent "http.log never holds a proxy key" "prx-leak" "${HTTP_LOG}"
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
# The field scrub guards every route outside the credential families (defence in depth).
"${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test POST agents \
  '{"password":"prefix\"SECRET_REMAINDER","email":"a"}' >/dev/null 2>&1
HTTP_LOG="$(cat "${LAB}/evidence/http.log")"
check_absent "escaped quote in a password leaks nothing" "SECRET_REMAINDER" "${HTTP_LOG}"
check_contains "the request's other fields are kept" '"email"' "${HTTP_LOG}"
check_absent "response accessToken scrubbed" "resp-secret-1" "${HTTP_LOG}"
check_absent "nested response refreshToken scrubbed" "resp-secret-2" "${HTTP_LOG}"
check_contains "the response's other fields are kept" '"title": "kept"' "${HTTP_LOG}"
ctl curl-queue "200\t{}\n200\t{}\n"
"${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test PUT cloud-settings \
  '{"headers":[{"name":"X-Gateway-Key","value":"HEADER_SECRET","isSecret":true}]}' >/dev/null 2>&1
check_contains "a cloud settings save logs a marker, not its body" \
  '>> [body omitted: credential-bearing route,' "$(cat "${LAB}/evidence/http.log")"
"${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test PUT agents/a1 \
  '{"headers":[{"name":"X-Gateway-Key","value":"HEADER_SECRET","isSecret":true}]}' >/dev/null 2>&1
HTTP_LOG="$(cat "${LAB}/evidence/http.log")"
check_absent "a secret cloud header value leaks nothing" "HEADER_SECRET" "${HTTP_LOG}"
check_contains "a secret cloud header keeps its flag" '"isSecret": true' "${HTTP_LOG}"
# The node binds JSON names case-insensitively.
ctl curl-queue "200\t{}\n"
"${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test PUT agents/a1 \
  '{"Headers":[{"Name":"X-Gateway-Key","Value":"PASCAL_SECRET","IsSecret":true},{"name":"X-Other","vALUE":"MIXED_SECRET","ISSECRET":true}]}' >/dev/null 2>&1
HTTP_LOG="$(cat "${LAB}/evidence/http.log")"
check_absent "a PascalCase secret cloud header value leaks nothing" "PASCAL_SECRET" "${HTTP_LOG}"
check_absent "a mixed-case secret cloud header value leaks nothing" "MIXED_SECRET" "${HTTP_LOG}"
check_contains "a PascalCase secret cloud header keeps its spelling" '"Value": "(redacted)"' "${HTTP_LOG}"
# The vault answers its one-time recovery code on confirm, and the recovery unlock takes one and answers the next.
ctl curl-queue "200\t{\"recoveryCode\":\"VAULT_CONFIRM_CODE\"}\n200\t{\"recoveryCode\":\"VAULT_NEXT_CODE\"}\n200\t{\"userCode\":\"DEVICE_USER_CODE\",\"previewToken\":\"PREVIEW_TOKEN\"}\n"
"${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test POST auth/vault/confirm '{"password":"VAULT_PASSWORD"}' >/dev/null 2>&1
"${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test POST auth/vault/unlock-recovery \
  '{"recoveryCode":"VAULT_REQUEST_CODE","newPassword":"VAULT_NEW_PASSWORD"}' >/dev/null 2>&1
"${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test POST cloud-settings/entra/device-code/start '{}' >/dev/null 2>&1
HTTP_LOG="$(cat "${LAB}/evidence/http.log")"
for sentinel in VAULT_CONFIRM_CODE VAULT_NEXT_CODE VAULT_REQUEST_CODE VAULT_PASSWORD VAULT_NEW_PASSWORD DEVICE_USER_CODE PREVIEW_TOKEN; do
  check_absent "a one-time credential leaks nothing (${sentinel})" "${sentinel}" "${HTTP_LOG}"
done
check_contains "a vault confirm logs a marker for its response" \
  "$(printf '[body omitted: credential-bearing route, 37 bytes]\n[HTTP 200]')" "${HTTP_LOG}"
# An MCP registration carries credentials under arbitrary header and environment names and in its URL.
: >"${LAB}/evidence/http.log"
ctl curl-queue "201\t{\"mcpServerId\":\"m1\",\"headers\":{\"Authorization\":\"Bearer MCP_RESPONSE_HEADER\"}}\n200\t{}\n"
for verb in "POST mcp/servers" "PUT mcp/servers/m1"; do
  # shellcheck disable=SC2086  # the verb and the path are two words
  "${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test ${verb} \
    '{"name":"docs","transportKind":"Http","url":"https://user:MCP_URL_PASSWORD@mcp.example/sse?token=MCP_URL_TOKEN","headers":{"Authorization":"Bearer MCP_HEADER_SECRET"},"env":{"MY_SERVICE":"MCP_ENV_SECRET"}}' >/dev/null 2>&1
done
HTTP_LOG="$(cat "${LAB}/evidence/http.log")"
for sentinel in MCP_URL_PASSWORD MCP_URL_TOKEN MCP_HEADER_SECRET MCP_ENV_SECRET MCP_RESPONSE_HEADER; do
  check_absent "an MCP registration leaks nothing (${sentinel})" "${sentinel}" "${HTTP_LOG}"
done
check_contains "an MCP create keeps method and path" " POST mcp/servers" "${HTTP_LOG}"
check_contains "an MCP update keeps method and path" " PUT mcp/servers/m1" "${HTTP_LOG}"
check_contains "an MCP create keeps its status" "[HTTP 201]" "${HTTP_LOG}"
check "an MCP create and update log two request markers" "2" "$(grep -c '^>> \[body omitted: credential-bearing route, [0-9]* bytes\]$' "${LAB}/evidence/http.log")"
# The node routes a path decoded, dot segments resolved, empty segments dropped and case ignored; an ambiguous spelling
# (an encoded slash, `..` above the API root) counts as a credential route.
for spelling in ./mcp/servers MCP/Servers mcp//servers %6dcp/servers 'mcp%2Fservers' ../v1/mcp/servers; do
  : >"${LAB}/evidence/http.log"
  ctl curl-queue "201\t{\"headers\":{\"Authorization\":\"Bearer SPELLING_RESPONSE_HEADER\"}}\n"
  "${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test POST "${spelling}" \
    '{"headers":{"Authorization":"Bearer SPELLING_HEADER_SECRET"}}' >/dev/null 2>&1
  HTTP_LOG="$(cat "${LAB}/evidence/http.log")"
  check_absent "an MCP registration as ${spelling} logs no request header value" "SPELLING_HEADER_SECRET" "${HTTP_LOG}"
  check_absent "an MCP registration as ${spelling} logs no response header value" "SPELLING_RESPONSE_HEADER" "${HTTP_LOG}"
  check_contains "an MCP registration as ${spelling} keeps method and path" " POST ${spelling}" "${HTTP_LOG}"
done
# A spelling that is not a plain path (scheme-relative, another scheme, a backslash, a full URL) has no route the
# recorder can classify: a node that rejects it still never gets the submitted credential into the log.
for spelling in //mcp/servers '\\mcp\servers' http:mcp/servers mcp:servers http://127.0.0.1:1/api/local/v1/mcp/servers; do
  : >"${LAB}/evidence/http.log"
  ctl curl-queue "404\t{\"title\":\"Not Found\"}\n"
  "${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test POST "${spelling}" \
    '{"headers":{"Authorization":"Bearer AMBIGUOUS_HEADER_SECRET"}}' >/dev/null 2>&1
  HTTP_LOG="$(cat "${LAB}/evidence/http.log")"
  check_absent "a rejected MCP registration as ${spelling} logs no header value" "AMBIGUOUS_HEADER_SECRET" "${HTTP_LOG}"
  check_contains "a rejected MCP registration as ${spelling} logs a request marker" \
    ">> [body omitted: credential-bearing route," "${HTTP_LOG}"
  check_contains "a rejected MCP registration as ${spelling} keeps its status" "[HTTP 404]" "${HTTP_LOG}"
done
# A route outside the credential families stays readable; its URLs lose their userinfo and query values.
ctl curl-queue "200\t{\"items\":[{\"name\":\"docs\",\"url\":\"https://user:CATALOG_PASSWORD@mcp.example/sse?token=CATALOG_TOKEN&mode=sse\"},{\"name\":\"mail\",\"url\":\"https://ops.lab@mail.example:EMAIL_PASSWORD@mcp.example/sse\"}]}\n"
"${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test GET 'tool-catalog?source=https://u:PATH_PASSWORD@mcp.example/x' >/dev/null 2>&1
HTTP_LOG="$(cat "${LAB}/evidence/http.log")"
for sentinel in CATALOG_PASSWORD CATALOG_TOKEN PATH_PASSWORD EMAIL_PASSWORD; do
  check_absent "a URL's credentials leak nothing (${sentinel})" "${sentinel}" "${HTTP_LOG}"
done
check_contains "a non-sensitive route stays readable" '"name": "docs"' "${HTTP_LOG}"
check_contains "a URL keeps its host, path and query names" \
  '"url": "https://(redacted)@mcp.example/sse?token=(redacted)&mode=(redacted)"' "${HTTP_LOG}"
check_contains "a path's query value is masked whole" "GET tool-catalog?source=(redacted)" "${HTTP_LOG}"
check_contains "an email-style username is masked through the last @" '"url": "https://(redacted)@mcp.example/sse"' "${HTTP_LOG}"
# A long poll on the hub carries the connection token in its path; a 404 body may echo the polled URL.
ctl curl-queue "200\t{\"type\":6}\n404\tMissing chat/hub?id=HUB_TOKEN_SECRET\n"
"${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test GET 'chat/hub?id=HUB_TOKEN_SECRET' >/dev/null 2>&1
"${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test GET 'chat/hub?x=1&id=HUB_TOKEN_SECRET' >/dev/null 2>&1
HTTP_LOG="$(cat "${LAB}/evidence/http.log")"
check_absent "a hub call's connection token leaks nothing" "HUB_TOKEN_SECRET" "${HTTP_LOG}"
check_contains "a hub call logs its redacted path" "GET chat/hub?id=(redacted)" "${HTTP_LOG}"
check_contains "a hub call logs its response" '"type": 6' "${HTTP_LOG}"
# Every query value is masked whatever its name, case or encoding; a 404 body echoing the path is masked too.
for spelling in 'chat/hub?ID=SPELLED_HUB_TOKEN' 'chat/hub?%69d=SPELLED_HUB_TOKEN' 'chat/hub?Id=SPELLED_HUB_TOKEN&foo=SPELLED_FOO'; do
  : >"${LAB}/evidence/http.log"
  ctl curl-queue "404\tMissing ${spelling}\n"
  "${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test GET "${spelling}" >/dev/null 2>&1
  HTTP_LOG="$(cat "${LAB}/evidence/http.log")"
  check_absent "a hub call as ${spelling} logs no query value" "SPELLED_" "${HTTP_LOG}"
  check_contains "a hub call as ${spelling} keeps method and route" " GET chat/hub?" "${HTTP_LOG}"
done
# A skill preview resource path carries its import token as a segment; a 404 body may echo the path.
: >"${LAB}/evidence/http.log"
ctl curl-queue "200\tnotes\n404\tMissing skills/import/preview/PREVIEW_PATH_TOKEN/skills/s1/resources/x\n404\tMissing\n"
for spelling in skills/import/preview/PREVIEW_PATH_TOKEN/skills/s1/resources/notes.md \
  Skills/Import/Preview/PREVIEW_PATH_TOKEN/skills/s1/resources/x '%73kills/import/preview/PREVIEW_PATH_TOKEN/x'; do
  "${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test GET "${spelling}" >/dev/null 2>&1
done
HTTP_LOG="$(cat "${LAB}/evidence/http.log")"
check_absent "a skill preview read logs no import token" "PREVIEW_PATH_TOKEN" "${HTTP_LOG}"
check_contains "a skill preview read keeps the rest of its path" \
  " GET skills/import/preview/(redacted)/skills/s1/resources/notes.md" "${HTTP_LOG}"
check_contains "a skill preview read that a spelling hides is masked whole" " GET (redacted)" "${HTTP_LOG}"
check "a skill preview read's bodies are markers" "3" "$(grep -c '^\[body omitted: credential-bearing route, [0-9]* bytes\]$' "${LAB}/evidence/http.log")"
# A conversation's tool result can link a preview resource in any spelling; a 404 body can echo one.
: >"${LAB}/evidence/http.log"
ctl curl-queue "200\t{\"messages\":[{\"role\":\"tool\",\"content\":\"see /api/local/v1/%73kills/import/preview/LINK_PATH_TOKEN/x\"},{\"role\":\"tool\",\"content\":\"skills/import/preview/LINK_PATH_TOKEN/y\"}]}\n404\tMissing skills%2Fimport%2Fpreview%2FLINK_PATH_TOKEN\n"
for path in chat/conversations/c1 chat/conversations/c2; do
  "${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test GET "${path}" >/dev/null 2>&1
done
HTTP_LOG="$(cat "${LAB}/evidence/http.log")"
check_absent "a preview link inside a conversation logs no import token" "LINK_PATH_TOKEN" "${HTTP_LOG}"
check_contains "a canonical preview link inside a conversation is masked in place" \
  '"content": "skills/import/preview/(redacted)/y"' "${HTTP_LOG}"
check "a preview link a spelling hides is a marker" "2" "$(grep -o 'body omitted: unmaskable secret path, [0-9]* bytes' "${LAB}/evidence/http.log" | wc -l)"
# A valid frame whose serialized tool arguments were cut after a complete password records a marker in their place.
: >"${LAB}/evidence/http.log"
ctl curl-queue "200\t\n"
"${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test POST 'chat/hub?id=FRAME_HUB_TOKEN' \
  "$(printf '{"type":2,"invocationId":"1","item":{"toolCall":{"arguments":"{\\"password\\":\\"CUT_ARGS_PASSWORD\\",\\"q"}}}\036')" \
  >/dev/null 2>&1
HTTP_LOG="$(cat "${LAB}/evidence/http.log")"
check_absent "cut tool arguments in a valid frame leak no password" "CUT_ARGS_PASSWORD" "${HTTP_LOG}"
check_absent "a frame POST leaks no connection token" "FRAME_HUB_TOKEN" "${HTTP_LOG}"
check_contains "cut tool arguments are replaced by a marker" '"arguments": "[body omitted: unparseable JSON,' "${HTTP_LOG}"
# A byte-order mark or zero-width character before JSON makes it unparseable as it stands; it is still judged as JSON:
# complete and cut bodies, frames and serialized tool arguments, sent and answered.
for prefix in '\xef\xbb\xbf' '\xe2\x80\x8b'; do
  : >"${LAB}/evidence/http.log"
  ctl curl-queue "200\t${prefix}{\"accessToken\":\"BOM_ANSWER_TOKEN\"}\n200\t${prefix}{\"accessToken\":\"BOM_CUT_ANSWER\",\"re\n200\t\n"
  "${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test POST agents \
    "$(printf "${prefix}"'{"password":"BOM_BODY_PASSWORD"}')" >/dev/null 2>&1
  "${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test POST agents \
    "$(printf "${prefix}"'{"password":"BOM_CUT_PASSWORD","q')" >/dev/null 2>&1
  "${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test POST 'chat/hub?id=x' \
    "$(printf "${prefix}"'{"type":2,"item":{"toolCall":{"arguments":"'"${prefix}"'{\\"password\\":\\"BOM_ARGS_PASSWORD\\"}"}}}\036{"type":2,"item":{"toolCall":{"arguments":"'"${prefix}"'{\\"password\\":\\"BOM_CUT_ARGS\\",\\"q"}}}\036')" \
    >/dev/null 2>&1
  HTTP_LOG="$(cat "${LAB}/evidence/http.log")"
  for sentinel in BOM_ANSWER_TOKEN BOM_CUT_ANSWER BOM_BODY_PASSWORD BOM_CUT_PASSWORD BOM_ARGS_PASSWORD BOM_CUT_ARGS; do
    check_absent "JSON behind ${prefix} leaks nothing (${sentinel})" "${sentinel}" "${HTTP_LOG}"
  done
  check_contains "JSON behind ${prefix}: a body keeps its fields" '>> {"password": "(redacted)"}' "${HTTP_LOG}"
  check_contains "JSON behind ${prefix}: an answer keeps its fields" '{"accessToken": "(redacted)"}' "${HTTP_LOG}"
  check_contains "JSON behind ${prefix}: tool arguments keep their fields" '"arguments": "{\"password\": \"(redacted)\"}"' "${HTTP_LOG}"
  check "JSON behind ${prefix}: cut request, answer and arguments are markers" "3" "$(grep -o 'body omitted: unparseable JSON' "${LAB}/evidence/http.log" | wc -l | tr -d ' ')"
done
# A transfer cut after a complete credential leaves JSON no field rule can parse; curl fails, so the call is HTTP 000.
: >"${LAB}/evidence/http.log"
echo 56 >"${CTL}/curl-exit"
ctl curl-queue "200\t{\"accessToken\":\"CUT_LOGIN_TOKEN\",\"refreshTok\n200\t{\"items\":[{\"apiKey\":\"CUT_LIST_KEY\"},{\"na\n"
out="$("${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test POST auth/login '{"email":"a","password":"CUT_PASSWORD"}' 2>&1)"; status=$?
check "a truncated login exits 1" "1" "${status}"
check_contains "a truncated login reports HTTP 000" "[HTTP 000]" "${out}"
"${API_ENV[@]}" "${REPO}/scripts/lab-api.sh" --profile test GET agents >/dev/null 2>&1
rm -f "${CTL}/curl-exit"
HTTP_LOG="$(cat "${LAB}/evidence/http.log")"
for sentinel in CUT_LOGIN_TOKEN CUT_LIST_KEY CUT_PASSWORD; do
  check_absent "a truncated body leaks nothing (${sentinel})" "${sentinel}" "${HTTP_LOG}"
done
check "both truncated calls are logged as HTTP 000" "2" "$(grep -c '^\[HTTP 000\]$' "${LAB}/evidence/http.log")"
check_contains "a truncated body outside the credential routes logs a marker" \
  "$(printf '[body omitted: unparseable JSON, 40 bytes]\n[HTTP 000]')" "${HTTP_LOG}"
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
        raw = sys.argv[1] + ".raw"
        if os.path.exists(raw):
            # An artifact download: the bytes of <port file>.raw, served as the content type in <port file>.raw-type.
            data = open(raw, "rb").read()
            self.send_response(200)
            self.send_header("Content-Type", open(raw + "-type").read())
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
            return
        if self.path.endswith("/model-fit/llamacpp/runtime"):
            # The llama.cpp runtime status: the JSON in <port file>.runtime.
            self.answer(json.load(open(sys.argv[1] + ".runtime")))
            return
        # The memory model the node reports: the content of <port file>.memory when present, else unset (null).
        memory = sys.argv[1] + ".memory"
        self.answer(dict(BODY, memoryExtractionModelName=open(memory).read() if os.path.exists(memory) else None))
    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length") or 0)).decode()
        with open(sys.argv[1] + ".posts", "a") as log:
            log.write(f"{self.path}\t{body}\t{self.headers.get('Authorization')}\n")
        expired = sys.argv[1] + ".expire-once"
        if self.path.endswith("/variant/automatic") and os.path.exists(expired):
            os.remove(expired)  # the access token ran out during the install
            self.send_response(401)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        if self.path.endswith("/auth/login"):
            self.answer({"accessToken": "tok-renewed-runtime"})
        elif self.path.endswith("/model-fit/llamacpp/version"):
            self.answer({"version": "b300", "variant": json.loads(body)["variant"], "isPinnedFallback": True, "pinnedTag": "b300"})
        else:
            self.answer({})
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
# A desired "" is the retired clear signal: the PUT refuses it, so the driver sends null instead.
printf 'qwen3:8b' >"${CTL}/node-port.memory"
EV_CLEAR="${TEMP_ROOT}/driver-evidence-clear"
python3 "${REAL}/lab-driver.py" settings-ensure --base-url "http://127.0.0.1:$(cat "${CTL}/node-port" 2>/dev/null)" \
  --token t --evidence "${EV_CLEAR}" --desired '{"memoryExtractionModelName":""}' >/dev/null 2>&1
check "settings-ensure with a desired empty string exits 0" "0" "$?"
check "a desired empty string is PUT as null" '{"memoryExtractionModelName": null}' \
  "$(python3 -c 'import json,sys; print(next(json.loads(l)["request"] for l in open(sys.argv[1]) if json.loads(l)["method"] == "PUT"))' "${EV_CLEAR}/http.jsonl" 2>/dev/null)"
rm -f "${CTL}/node-port.memory"
out="$(python3 "${REAL}/lab-driver.py" settings-ensure --base-url "http://127.0.0.1:$(cat "${CTL}/node-port" 2>/dev/null)" \
  --token t --evidence "${TEMP_ROOT}/driver-evidence-unset" --desired '{"memoryExtractionModelName":""}' 2>/dev/null)"
check "a desired empty string against an unset member is skipped" $'skipped\ttrue' "$(grep '^skipped' <<<"${out}")"
check "a desired empty string against an unset member verifies" $'verified\ttrue' "$(grep '^verified' <<<"${out}")"
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
    "text": "{not json sink-cut-8"}})
' "${REAL}/lab-driver.py" "${EV}/sink.jsonl" >/dev/null 2>&1
SINK="$(cat "${EV}/sink.jsonl" 2>/dev/null)"
check_contains "append_jsonl wrote the record" '"accessToken": "(redacted)"' "${SINK}"
check_absent "append_jsonl never writes sink-secret-5" "sink-secret-5" "${SINK}"
check_absent "a password inside a JSON-string arguments field is scrubbed" "sink-secret-6" "${SINK}"
check_absent "an apiKey inside a JSON-string result array is scrubbed" "sink-secret-7" "${SINK}"
check_contains "the JSON-string arguments keep their other fields" "kept-path" "${SINK}"
check_absent "a string that only looks like JSON is omitted whole" "sink-cut-8" "${SINK}"
check_contains "a string that only looks like JSON leaves a marker" "[body omitted: unparseable JSON, 20 bytes]" "${SINK}"
# runtime-ensure against the fake node: a recorded runtime is left alone; none installs the node-selected variant.
NODE_URL="http://127.0.0.1:$(cat "${CTL}/node-port" 2>/dev/null)"
POSTS="${CTL}/node-port.posts"
runtime_ensure() {
  python3 "${REAL}/lab-driver.py" runtime-ensure --base-url "${NODE_URL}" --token t --evidence "${TEMP_ROOT}/driver-evidence-runtime" "$@" 2>&1
}
echo '{"installed":{"tag":"b250","variant":"cuda"},"selectedVariant":"cuda"}' >"${CTL}/node-port.runtime"
out="$(runtime_ensure)"
check "runtime present: reported with its tag and variant" $'present\ttrue\ntag\tb250\nvariant\tcuda' "${out}"
check "runtime present: nothing POSTed" "0" "$(count_lines "${POSTS}")"
echo '{"installed":null,"selectedVariant":"vulkan"}' >"${CTL}/node-port.runtime"
out="$(runtime_ensure)"
check "runtime missing: the node-selected variant is installed" $'present\tfalse\ntag\tb300\nvariant\tvulkan' "${out}"
check "runtime missing: install, then automatic selection restored" \
  '/api/local/v1/model-fit/llamacpp/version {"variant": "vulkan"}|/api/local/v1/model-fit/llamacpp/variant/automatic {}' \
  "$(awk -F'\t' '{ print $1 " " $2 }' "${POSTS}" | paste -sd'|' -)"
rm -f "${POSTS}"
: >"${CTL}/node-port.expire-once"
out="$(runtime_ensure --email a@b.test --password pw)"
check "runtime 401 after the install: re-login, then automatic retried with the new token" \
  "version Bearer t|automatic Bearer t|login None|automatic Bearer tok-renewed-runtime" \
  "$(awk -F'\t' '{ n = split($1, part, "/"); print part[n] " " $3 }' "${POSTS}" | paste -sd'|' -)"
check_contains "runtime 401: the renewed token is emitted" $'token\ttok-renewed-runtime' "${out}"
rm -f "${POSTS}"
: >"${CTL}/node-port.expire-once"
runtime_ensure >/dev/null
check "runtime 401 without credentials => exit 1" "1" "$?"
rm -f "${CTL}/node-port.expire-once"
echo '{"installed":null}' >"${CTL}/node-port.runtime"
runtime_ensure >/dev/null
check "runtime status without selectedVariant => exit 1" "1" "$?"
# lab-api.sh through the REAL curl against the fake node: what the node receives is what http.log classified.
CURL_BIN="${TEMP_ROOT}/curl-bin"
mkdir -p "${CURL_BIN}" "${REPO}/.tmp/lab/real/evidence"
ln -s "$(python3 -c 'import sys; print(sys.executable)')" "${CURL_BIN}/python3"
ln -s "$(command -v curl)" "${CURL_BIN}/curl"
printf '{"baseUrl":"%s"}\n' "${NODE_URL}" >"${REPO}/.tmp/lab/real/manifest.json"
echo "tok-real-opaque" >"${REPO}/.tmp/lab/real/token"
REAL_LOG="${REPO}/.tmp/lab/real/evidence/http.log"
# A .curlrc that adds a URL: the request must never reach it.
printf 'url = "%s/api/local/v1/from-curlrc"\n' "${NODE_URL}" >"${FAKE_HOME}/.curlrc"
real_api() { "${TEST_ENV[@]}" PATH="${CURL_BIN}:${PATH}" CURL_HOME="${FAKE_HOME}" "${REPO}/scripts/lab-api.sh" --profile real "$@"; }
rm -f "${POSTS}"
out="$(real_api POST mcp/servers '{"name":"docs","headers":{"Authorization":"Bearer REAL_MCP_SECRET"}}' 2>&1)"
check "a real-curl MCP registration exits 0" "0" "$?"
check "a real-curl MCP registration reaches the node once, at the route it names" \
  "/api/local/v1/mcp/servers" "$(cut -f1 "${POSTS}" 2>/dev/null | paste -sd'|' -)"
check_contains "a real-curl MCP registration sends its body" "REAL_MCP_SECRET" "$(cat "${POSTS}" 2>/dev/null)"
check_absent "a real-curl MCP registration logs no header value" "REAL_MCP_SECRET" "$(cat "${REAL_LOG}")"
check_contains "a real-curl MCP registration logs a request marker" \
  ">> [body omitted: credential-bearing route," "$(cat "${REAL_LOG}")"
# curl would expand a brace or bracket list into several URLs after log_call classified the literal path.
for spelling in '{chat/conversations,mcp/servers}' 'mcp/{servers}' 'mcp/servers[1-2]' 'chat/[a]' 'mcp/servers}' '[x'; do
  rm -f "${POSTS}"
  : >"${REAL_LOG}"
  out="$(real_api POST "${spelling}" '{"headers":{"Authorization":"Bearer GLOB_HEADER_SECRET"}}' 2>&1)"
  check "a path with glob syntax ${spelling} => exit 2" "2" "$?"
  check_contains "a path with glob syntax ${spelling} is refused by name" "curl glob syntax" "${out}"
  check "a path with glob syntax ${spelling} sends nothing" "0" "$(count_lines "${POSTS}")"
  check "a path with glob syntax ${spelling} logs nothing" "0" "$(count_lines "${REAL_LOG}")"
  check_absent "a path with glob syntax ${spelling} never logs the header value" "GLOB_HEADER_SECRET" "$(cat "${REAL_LOG}")"
done
out="$(real_api settle '{a,b}' 1 2>&1)"
check "settle with glob syntax in the conversation id => exit 2" "2" "$?"
check_contains "settle with glob syntax is refused by name" "curl glob syntax" "${out}"
out="$(real_api $'POST\nX-Forged: 1' chat/conversations '{}' 2>&1)"
check "a method holding a line break => exit 2" "2" "$?"
# A body starting with @ is sent as written, never as the content of the file it names.
printf 'FILE_BODY_SECRET' >"${CTL}/body-file"
rm -f "${POSTS}"
real_api POST chat/conversations "@${CTL}/body-file" >/dev/null 2>&1
check "a body starting with @ is sent literally" "@${CTL}/body-file" "$(cut -f2 "${POSTS}" 2>/dev/null)"
# An artifact download is no JSON: a credential value anywhere in it, or a body that is not text, leaves a marker.
artifact() {
  printf '%b' "$2" >"${CTL}/node-port.raw"
  printf '%s' "$1" >"${CTL}/node-port.raw-type"
  : >"${REAL_LOG}"
  real_api GET benchmarks/runs/r1/artifacts/a >"${CTL}/artifact.out" 2>&1; status=$?
  out="$(tr -d '\0' <"${CTL}/artifact.out")"  # a binary body prints its NUL bytes
  rm -f "${CTL}/node-port.raw" "${CTL}/node-port.raw-type"
}
for body in 'run finished\nkey: xeint_ART_SECRET_1 (rotate)\n' \
  'notes\n-----BEGIN OPENSSH PRIVATE KEY-----\nART_SECRET_2\n-----END OPENSSH PRIVATE KEY-----\ntrailer\n' \
  'proxy XEPRX_ART_SECRET_3'; do
  artifact "text/plain; charset=utf-8" "${body}"
  check "a text artifact holding a credential value is printed (${body:0:12})" "0" "${status}"
  check_contains "a text artifact holding a credential value is fetched (${body:0:12})" "ART_SECRET_" "${out}"
  check_absent "a text artifact never logs its credential value (${body:0:12})" "ART_SECRET_" "$(cat "${REAL_LOG}")"
  check_contains "a text artifact holding a credential value logs a marker (${body:0:12})" \
    "[body omitted: credential value," "$(cat "${REAL_LOG}")"
done
artifact "application/json" '{"name":"run","note":"key xemcp_ART_SECRET_4 here","n":1}'
check_absent "a JSON string holding a credential value is never logged" "ART_SECRET_4" "$(cat "${REAL_LOG}")"
check_contains "a JSON string holding a credential value is redacted, the rest kept" \
  '{"name": "run", "note": "(redacted)", "n": 1}' "$(cat "${REAL_LOG}")"
artifact "image/png" '\x89PNG\r\n\x1a\n\x00\x00ART_SECRET_5\xff\xfe'
check "a binary artifact download exits 0" "0" "${status}"
check_absent "a binary artifact never logs its bytes" "ART_SECRET_5" "$(cat "${REAL_LOG}")"
check_contains "a binary artifact logs a marker" "[body omitted: not text," "$(cat "${REAL_LOG}")"
artifact "text/plain" 'run finished in 4 s\n'
check_contains "a text artifact without a credential value is logged as is" "run finished in 4 s" "$(cat "${REAL_LOG}")"
# Proxy variables pointing at a listener: lab traffic and its credentials go to the loopback node, never to it.
cat >"${TEMP_ROOT}/fake-proxy.py" <<'PROXY'
import os, socket, sys
server = socket.create_server(("127.0.0.1", 0))
with open(sys.argv[1] + ".tmp", "w") as handle:
    handle.write(str(server.getsockname()[1]))
os.replace(sys.argv[1] + ".tmp", sys.argv[1])
while True:
    connection, _ = server.accept()
    connection.settimeout(2)
    try:
        data = connection.recv(4096)
    except OSError:
        data = b""
    with open(sys.argv[1] + ".log", "ab") as log:
        log.write(b"CONNECTION " + data + b"\n")
    connection.close()
PROXY
python3 "${TEMP_ROOT}/fake-proxy.py" "${CTL}/proxy-port" </dev/null >/dev/null 2>&1 &
HELPER_PIDS+=("$!")
for _ in $(seq 100); do [[ -s "${CTL}/proxy-port" ]] && break; "${SLEEP_BIN}" 0.05; done  # real-timer: server startup
PROXY_URL="http://127.0.0.1:$(cat "${CTL}/proxy-port" 2>/dev/null)"
PROXY_ENV=(-u no_proxy -u NO_PROXY http_proxy="${PROXY_URL}" HTTP_PROXY="${PROXY_URL}" https_proxy="${PROXY_URL}"
  HTTPS_PROXY="${PROXY_URL}" all_proxy="${PROXY_URL}" ALL_PROXY="${PROXY_URL}")
check "the fake proxy listens" "yes" "$([[ -s "${CTL}/proxy-port" ]] && echo yes || echo no)"
rm -f "${POSTS}"
out="$("${TEST_ENV[@]}" PATH="${CURL_BIN}:${PATH}" CURL_HOME="${FAKE_HOME}" env "${PROXY_ENV[@]}" \
  "${REPO}/scripts/lab-api.sh" --profile real POST chat/conversations '{"title":"PROXY_BODY_SECRET"}' 2>&1)"
check "a call under proxy variables exits 0" "0" "$?"
check_contains "a call under proxy variables reaches the node" "PROXY_BODY_SECRET" "$(cat "${POSTS}" 2>/dev/null)"
check_contains "a call under proxy variables sends the bearer token to the node" "Bearer tok-real-opaque" \
  "$(cat "${POSTS}" 2>/dev/null)"
rm -f "${POSTS}"
records="$(env "${PROXY_ENV[@]}" python3 "${REAL}/gpu-smoke-driver.py" --base-url "${NODE_URL}" auth \
  --email a@b.test --password PROXY_PASSWORD_SECRET 2>&1)"
check "a driver login under proxy variables exits 0" "0" "$?"
check_contains "a driver login under proxy variables reaches the node" "PROXY_PASSWORD_SECRET" \
  "$(cat "${POSTS}" 2>/dev/null)"
check_contains "a driver login under proxy variables gets the node's token" $'token\ttok-renewed-runtime' "${records}"
check "the proxy receives no connection" "0" "$(count_lines "${CTL}/proxy-port.log")"
rm -f "${FAKE_HOME}/.curlrc"
# model-ensure: cloud rows do not count as a GGUF, another llama.cpp row does.
model_ensure_listing() {
  python3 -c '
import importlib.util, sys, types
spec = importlib.util.spec_from_file_location("lab_driver", sys.argv[1])
driver = importlib.util.module_from_spec(spec)
spec.loader.exec_module(driver)
listing = {"items": [{"modelName": "other", "provider": sys.argv[2]}]}
driver.client_for = lambda args: types.SimpleNamespace(get=lambda path: listing)
driver.command_model_ensure(types.SimpleNamespace(model="wanted", eject_others=False))
' "${REAL}/lab-driver.py" "$1" 2>&1
}
check "model-ensure with only cloud rows reports no GGUF" $'installed\tfalse\nnoGguf\ttrue' "$(model_ensure_listing CodexOAuth)"
check "model-ensure with another GGUF reports only the missing model" $'installed\tfalse' "$(model_ensure_listing llamacpp)"

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
