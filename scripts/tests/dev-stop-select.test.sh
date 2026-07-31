#!/usr/bin/env bash

set -euo pipefail

PROJECT_ROOT="$(git rev-parse --show-toplevel)"
SELECTOR="${PROJECT_ROOT}/scripts/dev-stop-select.py"
TARGET_ROOT="/repo/worktree-a"
TARGET_APPHOST="${TARGET_ROOT}/XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj"

actual="$(python3 "${SELECTOR}" \
  --apphost-pid 100 \
  --apphost-path "${TARGET_APPHOST}" \
  --protected 999 <<EOF
90	1	42	900	aspire	aspire start --apphost ${TARGET_APPHOST} --isolated
100	90	42	1000	dotnet	${TARGET_APPHOST}
101	1	42	1001	dcp	/usr/bin/dcp --monitor 100
102	101	42	1002	dotnet	${TARGET_ROOT}/XE-Local-AI-Engine.Client.dll
103	101	42	1003	node	vite --root ${TARGET_ROOT}/XE-Local-AI-Engine.Client.React
110	101	42	1010	docker	docker run sqlite-web
111	101	42	1011	XE-Local-AI-En	${TARGET_ROOT}/XE-Local-AI-Engine.Client
112	110	42	1012	MainThread	python sqlite-web
113	111	42	1013	node-MainThread	node ${TARGET_ROOT}/server.js
114	113	42	1014	sh	sh -c pnpm dev
104	1	42	1004	dotnet	/repo/unrelated/worker.dll
105	102	77	1005	llama-server	/home/user/.local/share/XE-Local-AI-Engine/llama.cpp/llama-server
106	1	88	1006	llama-server	/home/user/.local/share/XE-Local-AI-Engine/llama.cpp/llama-server
107	1	42	1007	dotnet	/repo/worktree-b/XE-Local-AI-Engine.Client.dll
108	1	42	1008	dotnet	dotnet test ${TARGET_ROOT}/XE-Local-AI-Engine.Tests.csproj
109	1	42	1009	node	node ${TARGET_ROOT}/tools/unrelated-tooling.js
190	1	42	1190	aspire	aspire start --apphost /repo/worktree-b/XE-Local-AI-Engine.AppHost/AppHost.csproj --isolated
200	190	42	1200	dotnet	/repo/worktree-b/XE-Local-AI-Engine.AppHost/AppHost.dll
191	1	42	1191	dcp	/usr/bin/dcp --monitor 200
192	191	42	1192	node	vite --root /repo/worktree-b/XE-Local-AI-Engine.Client.React
999	101	42	1999	node	${TARGET_ROOT}/protected-agent.js
EOF
)"

expected=$'90\t900\n100\t1000\n101\t1001\n102\t1002\n103\t1003\n105\t1005\n110\t1010\n111\t1011\n112\t1012\n113\t1013\n114\t1014'
[[ "${actual}" == "${expected}" ]] || {
  printf 'FAIL: expected scoped PIDs:\n%s\nactual:\n%s\n' "${expected}" "${actual}" >&2
  exit 1
}

without_apphost="$(python3 "${SELECTOR}" \
  --apphost-pid 100 \
  --apphost-path "${TARGET_APPHOST}" <<'EOF'
101	1	42	1001	dcp	/usr/bin/dcp --monitor 100
102	101	42	1002	XE-Local-AI-En	/repo/worktree-a/XE-Local-AI-Engine.Client
110	101	42	1010	docker	docker run sqlite-web
112	110	42	1012	MainThread	python sqlite-web
191	1	42	1191	dcp	/usr/bin/dcp --monitor 200
192	191	42	1192	node-MainThread	node /repo/worktree-b/server.js
EOF
)"
expected_without_apphost=$'101\t1001\n102\t1002\n110\t1010\n112\t1012'
[[ "${without_apphost}" == "${expected_without_apphost}" ]] || {
  printf 'FAIL: DCP descendants must survive AppHost-record disappearance:\n%s\nactual:\n%s\n' \
    "${expected_without_apphost}" "${without_apphost}" >&2
  exit 1
}

echo "dev-stop-select.test.sh: PASS"
