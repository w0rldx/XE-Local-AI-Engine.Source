#!/usr/bin/env python3
"""Driver for scripts/run-model-matrix-local.sh: pinned models, scenario checks against a running dev host, the verdict.

Subcommands:
  resolve      print the manifest ids of a tier, or of a validated --models list
  local-file   check a model's installed file against the manifest SHA-256: ok, missing or mismatch
  base-url     read `scripts/dev-status.sh --json` on stdin and print the app origin
  download     install models through the product's own Hugging Face path
  run          run the checks per model; writes <evidence>/results.json and summary.md
  judge        exit 0 when results.json has at least one hard check and no failed one, else 1
  owned-servers  print pid and start time of every llama-server this checkout's node spawned

Hard checks are software contracts and fail the run. Measured checks record numbers or k/n and never fail it on their
own. Every REST call, hub event and llama-server /slots task is appended to the evidence directory when it happens.

The wire is read from llama-server's /slots of the model's own process, and only of a process the node under test
spawned (its parent chain reaches this checkout's host); another checkout serving the same file is ignored. `n_predict`
is the output limit the product sent and a `generation_prompt` that ends in `</think>` means thinking was switched off.
Chat runs at a non-zero temperature, the product's background JSON jobs at temperature 0, which is how a
memory-extraction task is told from a chat task.

`run` and `download` exit 2 when the node is not prepared (first-run setup not done, external access not `offline`),
`run` also when the node's device audit reports another backend than --variant (recorded in results.json when it
matches; it names the backend, it does not prove the GPU did the work), and 5 when the node cannot be reached or
logged in to. Stdlib only; the REST and SignalR client and the device audit are the GPU smoke driver's
(scripts/gpu-smoke-driver.py), loaded by import.
"""

from __future__ import annotations

import argparse
import datetime
import hashlib
import importlib.util
import json
import os
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request
import uuid
from pathlib import Path
from typing import Any

_SPEC = importlib.util.spec_from_file_location("gpu_smoke_driver", Path(__file__).with_name("gpu-smoke-driver.py"))
if _SPEC is None or _SPEC.loader is None:
    raise ImportError("scripts/gpu-smoke-driver.py")
gsd = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(gsd)

API = gsd.API
# Where this checkout's node host runs from: the runner builds and starts the host of the checkout this script is in.
HOST_DIR = str(Path(__file__).resolve().parent.parent / "XE-Local-AI-Engine.Client") + os.sep
TIERS = ("fast", "extended", "rc")
# How effort none switches a model's thinking off (manifest `thinkingOff`); None = the model does not think.
THINKING_OFF = ("template", "budget", None)
HARD_CHECKS = (
    "load-chat",
    "default-limit",
    "thinking-off",
    "length-limit",
    "tool-call",
    "memory-job",
    "moe-placement",
    "window-4096-tools",
    "no-think-tags",
    "eject",
)
MEASURED_CHECKS = ("multi-tool", "ttft", "gen-rate", "vram", "rss")
SHIPPED_BUDGETS = {"minimal": 1024, "low": 2048, "medium": 8192, "high": 24576}
SHIPPED_CAP_CEILING = 16384
LENGTH_LIMIT_TOKENS = 64
LENGTH_LIMIT_SAMPLES = 3
TERMINAL = ("assistant-completed", "assistant-failed", "assistant-cancelled", "assistant-interrupted")
PARKED = ("approval-requested", "question-requested")
THINK_TAGS = ("<think>", "</think>")
# The leading sentences of the product's two context-window refusals for a tool turn: InvocationRunner's
# ToolOfferExceedsWindowMessage (first-round fit) and ProviderContextWindowExceededException.RoundExceedsWindowMessage.
WINDOW_REFUSALS = {
    "refused-at-fit": "The tools offered with this message do not fit this model's context window",
    "refused-before-round-two": (
        "This request is too large for the model's context window even after trimming the conversation"
    ),
}
# What the supervisor answers when a pin starts while a launch of the same model is still being admitted.
ADMISSION_CONFLICT = "conflicts with another in-flight admission"

LONG_PROMPT = (
    "Write a complete, detailed guide to planning a two-week trip through Japan: itinerary for every day, "
    "transport, budget, food, etiquette, packing list and emergency information. Write at least 2,000 words."
)
MULTI_TOOL_PROMPT = (
    "Do these three steps with your tools, one after another: (1) compute 1234 * 5678 with the calculator; "
    "(2) add 98765 to that result with the calculator; (3) find out today's date with a tool. "
    "Then report both numbers and the date."
)


class PrerequisiteError(RuntimeError):
    """The node is not prepared for the lane; nothing was judged."""


def now() -> str:
    return datetime.datetime.now().isoformat(timespec="milliseconds")


# Pure logic (unit-tested in scripts/tests/test_model_matrix_driver.py)


def resolve_models(manifest: dict, tier: str, ids: list[str]) -> list[dict]:
    """The manifest entries a run covers: the explicit ids in the given order, else every entry of the tier."""
    models = manifest["models"]
    invalid = [m["id"] for m in models if m.get("thinkingOff", "missing") not in THINKING_OFF]
    if invalid:
        raise ValueError(f"thinkingOff must be template, budget or null: {', '.join(invalid)}")
    if ids:
        by_id = {m["id"]: m for m in models}
        unknown = [i for i in ids if i not in by_id]
        if unknown:
            raise ValueError(f"not in the manifest: {', '.join(unknown)} (known: {', '.join(by_id)})")
        return [by_id[i] for i in ids]
    if tier not in TIERS:
        raise ValueError(f"unknown tier {tier!r}; use one of {', '.join(TIERS)}")
    return [m for m in models if tier in m["tiers"]]


def resolve_checks(text: str) -> list[str]:
    names = [n.strip() for n in text.split(",") if n.strip()] if text else list(HARD_CHECKS + MEASURED_CHECKS)
    unknown = [n for n in names if n not in HARD_CHECKS + MEASURED_CHECKS]
    if unknown or not names:
        raise ValueError(f"unknown check(s): {', '.join(unknown) or '(none given)'}")
    return names


def installed_file(models_dir: Path, model_name: str) -> Path | None:
    """The weight file the product installed for a model, named by its `<file>.xe-model.json` sidecar."""
    for sidecar in sorted(models_dir.glob("*.xe-model.json")):
        try:
            data = json.loads(sidecar.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        if data.get("ModelName") == model_name and data.get("LocalFileName"):
            return models_dir / data["LocalFileName"]
    return None


def file_state(models_dir: Path, entry: dict) -> tuple[str, str]:
    path = installed_file(models_dir, entry["modelName"])
    if path is None or not path.is_file():
        return "missing", ""
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    actual = digest.hexdigest()
    return ("ok" if actual == entry["sha256"] else "mismatch"), actual


def expected_default_limit(settings: dict, window: int, reasoning: bool) -> int:
    """The limit a turn without an explicit one carries: applied reasoning budget + answer cap, within the window.

    Mirrors the node rule: the budget is the default effort's (clamped to half the window, zero for a non-reasoning
    model), the answer cap is half the window up to the ceiling setting. Only the `cap` mode sends a limit; `notice` and
    `off` send none (-1 on the wire). Unset settings take the shipped defaults.
    """
    if (settings.get("chatOutputCapMode") or "cap") != "cap":
        return -1
    budget = 0
    if reasoning:
        effort = settings.get("defaultReasoningEffort") or "low"
        budget = settings.get(f"reasoningBudget{effort.capitalize()}Tokens") or SHIPPED_BUDGETS[effort]
        budget = min(budget, max(window // 2, 1))
    cap = min(window // 2, settings.get("chatOutputCapMaxTokens") or SHIPPED_CAP_CEILING)
    return min(budget + cap, window)


def thinking_off(generation_prompt: str | None) -> bool:
    """llama-server renders a switched-off thinking block as an empty, already closed one at the end of the prompt."""
    return (generation_prompt or "").rstrip().endswith("</think>")


def background_job_problems(jobs: list[dict], switch: str | None) -> list[str]:
    """What is wrong with the observed background JSON jobs: every one must be capped, and for a template switch its
    generation prompt must show thinking off. A budget switch is invisible in the prompt, so only the cap is read."""
    problems = []
    for job in jobs:
        if not (isinstance(job["nPredict"], int) and 0 < job["nPredict"] <= 4096):
            problems.append(f"task {job.get('idTask')} uncapped (n_predict {job['nPredict']})")
        if switch == "template" and not thinking_off(job["generationPromptTail"]):
            problems.append(f"task {job.get('idTask')} thinking on")
    return problems


def length_limit_verdict(samples: list[dict], limit: int) -> tuple[str, str]:
    """pass / fail / n/a for the length-limit samples so far, judged on what happened, not on what was hoped for.

    A sample that ends ON the limit must carry the OutputLimitReached notice; output above the limit, or the notice
    without the limit having been reached, fails. A model that stops by itself below the limit proves nothing: the
    caller takes another sample, and when none reached the limit the check is n/a, never a pass.
    """
    for index, turn in enumerate(samples, 1):
        produced, notice = turn.get("outputTokens"), "OutputLimitReached" in turn["notices"]
        if turn["outcome"] != "assistant-completed" or not isinstance(produced, int):
            return "fail", f"sample {index}: {turn['outcome']}, output tokens {produced}"
        if produced > limit:
            return "fail", f"sample {index}: {produced} output tokens exceed the limit"
        if produced == limit:
            if notice:
                return "pass", f"sample {index}: stopped on the limit with the notice"
            return "fail", f"sample {index}: stopped on the limit without the notice"
        if notice:
            return "fail", f"sample {index}: notice after {produced} tokens, below the limit"
    return "n/a", f"no sample reached the limit in {len(samples)} ({[t.get('outputTokens') for t in samples]} tokens)"


def budget_answer_problems(turn: dict, stored: str | None) -> list[str]:
    """What a budget-switched model's effort-none turn got wrong: reasoning text is allowed, an empty or tagged answer
    or an EmptyAnswer notice is not."""
    problems = []
    if turn["outcome"] != "assistant-completed":
        problems.append(f"outcome {turn['outcome']}")
    if "EmptyAnswer" in turn["notices"]:
        problems.append("EmptyAnswer notice")
    if stored is None:
        problems.append("no persisted answer")
    elif not stored.strip():
        problems.append("empty answer")
    elif any(tag in stored for tag in THINK_TAGS):
        problems.append("think tag in the persisted answer")
    return problems


def argv_value(argv: list[str], *flags: str) -> str | None:
    return next((argv[i + 1] for i, a in enumerate(argv[:-1]) if a in flags), None)


def expert_offload_flags(argv: list[str]) -> list[str]:
    """Spawn arguments that move MoE experts off the GPU."""
    found = [a for a in argv if a in ("--cpu-moe", "-cmoe", "--n-cpu-moe", "-ncmoe")]
    for i, arg in enumerate(argv[:-1]):
        if arg in ("-ot", "--override-tensor") and "exps" in argv[i + 1]:
            found.append(f"{arg} {argv[i + 1]}")
    return found


def window_tools_verdict(turn: dict, windows: list[int]) -> tuple[str, str]:
    """(pass|fail, outcome) for the tool turn at a pinned 4,096 window, judged on the documented contract.

    At 4,096 a small model sits on the edge of the budget: whether the turn runs with a narrowed offer or ends with one
    of the product's own context-window refusals depends on whether the node has calibrated the model's tool-template
    overhead yet. All three are the contract; any other failure text (a provider error, the compact message for a turn
    without history), no terminal event, or a completed turn without the ToolsFiltered notice is not.
    """
    if not set(windows) <= {4096}:
        return "fail", f"window {windows}, not the pinned 4096"
    if turn["outcome"] == "assistant-completed":
        ok = completed(turn) and "ToolsFiltered" in turn["notices"] and windows == [4096]
        return ("pass" if ok else "fail"), "completed"
    error = str(turn.get("error") or "")
    if turn["outcome"] == "assistant-failed":
        for outcome, sentence in WINDOW_REFUSALS.items():
            if error.startswith(sentence):
                return "pass", outcome
    return "fail", f"{turn['outcome']}: other failure"


def backend_problem(requested: str, audit: dict) -> str | None:
    """Why the backend the node's device audit reports is not the requested variant, or None when it is.

    cuda needs the audit's `cuda` without a CPU fallback; cpu needs `cpu`. Anything else, `unknown` included, is a
    mismatch: a run labelled with one backend while the node runs another would skip or judge the wrong checks.
    """
    backend, fallback = audit.get("inferenceBackend"), bool(audit.get("cpuFallback"))
    if (requested == "cuda" and backend == "cuda" and not fallback) or (requested == "cpu" and backend == "cpu"):
        return None
    observed = f"{backend!r}" + (" with a CPU fallback" if fallback else "")
    reason = audit.get("cpuFallbackReason") or audit.get("backendUndeterminedReason")
    fix = (
        "supply a CPU llama-server through XE_LLAMACPP_SERVER_PATH (the lane then sets XE_LLAMACPP_VARIANT=cpu), "
        "or run --variant cuda"
        if requested == "cpu"
        else "install a CUDA llama.cpp runtime on the node, or supply a CUDA llama-server through "
        "XE_LLAMACPP_SERVER_PATH; or run --variant cpu"
    )
    return (
        f"--variant {requested} was requested, but the node's device audit reports backend {observed}"
        f"{f' ({reason})' if reason else ''}. Fix: {fix}."
    )


def judge(results: dict) -> tuple[int, list[str]]:
    """0 only when at least one hard check ran and none failed. A measured value never decides the exit code."""
    lines, failed, ran = [], 0, 0
    for model in results.get("models", []):
        for name, check in model.get("checks", {}).items():
            if check.get("kind") != "hard" or check.get("verdict") not in ("pass", "fail"):
                continue
            ran += 1
            if check["verdict"] == "fail":
                failed += 1
                lines.append(f"FAIL {model.get('id')} {name}: {check.get('detail')}")
    if ran == 0:
        lines.append("FAIL no hard check ran: an empty run is not a pass")
        return 1, lines
    lines.append(f"{ran - failed}/{ran} hard checks passed")
    return (1 if failed else 0), lines


def summary_markdown(results: dict) -> str:
    models = results.get("models", [])
    head = "| check | " + " | ".join(m["id"] for m in models) + " |"
    rows = [head, "|" + "---|" * (len(models) + 1)]
    names = [n for n in HARD_CHECKS + MEASURED_CHECKS if any(n in m.get("checks", {}) for m in models)]
    for name in names:
        cells = []
        for model in models:
            check = model.get("checks", {}).get(name)
            if check is None:
                cells.append("")
            elif check["kind"] == "measured":
                cells.append(str(check.get("value")))
            else:
                cells.append("**FAIL**" if check["verdict"] == "fail" else check["verdict"])
        kind = "hard" if name in HARD_CHECKS else "measured"
        rows.append(f"| {name} ({kind}) | " + " | ".join(cells) + " |")
    code, lines = judge(results)
    detail = []
    for model in models:
        for name, check in model.get("checks", {}).items():
            detail.append(f"- `{model['id']}` {name}: {check.get('verdict', '')} {check.get('detail', '')}".rstrip())
    return "\n".join(
        [
            f"# Model matrix run {results.get('startedAt', '')}",
            "",
            f"Tier `{results.get('tier')}`, variant `{results.get('variant')}` (device audit: backend "
            f"`{(results.get('backend') or {}).get('inferenceBackend')}`), verdict "
            f"**{'PASS' if code == 0 else 'FAIL'}** ({lines[-1]}).",
            "",
            *rows,
            "",
            "## Details",
            "",
            *detail,
            "",
        ]
    )


# Evidence, client, wire


class Evidence:
    def __init__(self, root: Path) -> None:
        self.root = root
        self.dir = root
        root.mkdir(parents=True, exist_ok=True)

    def at(self, name: str) -> None:
        self.dir = self.root / name
        self.dir.mkdir(parents=True, exist_ok=True)

    def append(self, name: str, record: dict) -> None:
        with (self.dir / name).open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(record, ensure_ascii=False, default=str) + "\n")


class Client(gsd.NodeClient):
    """NodeClient that logs every call (hub polls excepted) at call time and logs in again once on a 401."""

    def __init__(self, base_url: str, evidence: Evidence) -> None:
        super().__init__(base_url, None, 180.0)
        self.evidence = evidence

    def login(self) -> None:
        email = os.environ.get("XE_MODEL_MATRIX_EMAIL", "")
        password = os.environ.get("XE_MODEL_MATRIX_PASSWORD", "")
        if not email or not password:
            raise PrerequisiteError("XE_MODEL_MATRIX_EMAIL / XE_MODEL_MATRIX_PASSWORD are not set")
        self.token = None
        status = self.request("GET", f"{API}/auth/status")[1]
        if isinstance(status, dict) and status.get("setupRequired"):
            raise PrerequisiteError("the node has no operator yet: complete first-run setup, then re-run")
        payload = self.request("POST", f"{API}/auth/login", json.dumps({"email": email, "password": password}))[1]
        if not isinstance(payload, dict) or not payload.get("accessToken"):
            raise gsd.DriverError("auth/login returned no accessToken")
        self.token = payload["accessToken"]

    def request(self, method, path, body=None, content_type="application/json", expect_binary=False, allowed_status=()):
        poll = "/hub?id=" in path and method in ("GET", "DELETE")
        record: dict = {"t": now(), "method": method, "path": path}
        if body is not None and not poll and not path.endswith("/auth/login"):
            text = body.decode("utf-8", "replace") if isinstance(body, bytes) else body
            record["request"] = text[:20000]
        started = time.monotonic()
        try:
            try:
                status, payload = super().request(method, path, body, content_type, expect_binary, allowed_status)
            except gsd.DriverError as error:
                if "HTTP 401" not in str(error) or not self.token:
                    raise
                self.login()
                status, payload = super().request(method, path, body, content_type, expect_binary, allowed_status)
        except gsd.DriverError as error:
            record.update(ms=int((time.monotonic() - started) * 1000), error=str(error)[:4000])
            self.evidence.append("http.jsonl", record)
            raise
        if not poll:
            shown = payload.decode("utf-8", "replace")[:2000] if isinstance(payload, bytes) else payload
            if path.endswith("/auth/login"):
                shown = "(token omitted)"
            record.update(ms=int((time.monotonic() - started) * 1000), status=status, response=shown)
            self.evidence.append("http.jsonl", record)
        return status, payload

    def get(self, path: str, allowed: tuple[int, ...] = ()) -> Any:
        return self.request("GET", API + path, allowed_status=allowed)[1]

    def post(self, path: str, body: object, allowed: tuple[int, ...] = ()) -> tuple[int, Any]:
        return self.request("POST", API + path, json.dumps(body), allowed_status=allowed)

    def put(self, path: str, body: object) -> tuple[int, Any]:
        return self.request("PUT", API + path, json.dumps(body))

    def delete(self, path: str) -> tuple[int, Any]:
        return self.request("DELETE", API + path, allowed_status=(404,))

    def eject_all(self, model: str | None = None, timeout: float = 60.0) -> bool:
        """Eject every running entry (of one model, or all) and wait until the running view no longer lists it."""

        def running() -> list[dict]:
            items = self.get("/model-fit/running").get("items", [])
            return [i for i in items if model is None or i.get("modelName") == model]

        for item in running():
            self.post(
                "/model-fit/running/eject",
                {"modelName": item.get("modelName"), "role": item.get("role") or "", "force": False},
                allowed=(409,),
            )
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if not running():
                return True
            time.sleep(1)
        return False


def connect(base_url: str, evidence: Evidence) -> Client:
    client = Client(base_url, evidence)
    try:
        client.login()
    except gsd.DriverError as error:
        raise ConnectionError(f"cannot log in to {base_url}: {error}") from error
    profile = (client.get("/node-settings") or {}).get("externalAccessProfile")
    if profile != "offline":
        raise PrerequisiteError(
            "the node's external-access profile must be `offline` (node settings) so nothing is provisioned "
            f"or fetched during the run; it is {profile!r}"
        )
    return client


def owned_servers(table: dict[int, dict], model_file: str, host_dir: str) -> list[int]:
    """The llama-server pids serving this model file whose parent chain reaches the node under test.

    The node spawns llama-server as its own child (`setsid` execs in place), so a server is this node's when an ancestor
    was launched from `host_dir` (its executable or an argument lies there). A server of the same file under another
    checkout's node, or an orphan reparented to init, is someone else's and is ignored.
    """
    owned = []
    for pid, proc in table.items():
        argv = proc["argv"]
        if not argv or not argv[0].endswith("llama-server") or not any(a.endswith(model_file) for a in argv):
            continue
        seen, parent = {pid}, proc["ppid"]
        while parent in table and parent not in seen:
            if any(p.startswith(host_dir) for p in (table[parent]["exe"], *table[parent]["argv"])):
                owned.append(pid)
                break
            seen.add(parent)
            parent = table[parent]["ppid"]
    return owned


def proc_table() -> dict[int, dict]:
    """Every readable process: parent pid, argv and executable (Linux /proc)."""
    table = {}
    for pid in filter(str.isdigit, os.listdir("/proc")):
        try:
            argv = [p.decode("utf-8", "replace") for p in Path(f"/proc/{pid}/cmdline").read_bytes().split(b"\0") if p]
            # The command name may contain spaces and parentheses; the fields after the last ')' are state, ppid, ...
            ppid = int(Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()[1])
        except (OSError, ValueError, IndexError):
            continue
        try:
            exe = os.readlink(f"/proc/{pid}/exe")
        except OSError:
            exe = ""
        table[int(pid)] = {"ppid": ppid, "argv": argv, "exe": exe}
    return table


def llama_processes(model_file: str) -> list[dict]:
    """The node under test's llama-server processes serving this model file: pid, argv, RSS MiB (Linux /proc)."""
    table = proc_table()
    found = []
    for pid in owned_servers(table, model_file, HOST_DIR):
        try:
            argv = table[pid]["argv"]
            rss = next(
                (
                    int(line.split()[1]) // 1024
                    for line in Path(f"/proc/{pid}/status").read_text().splitlines()
                    if line.startswith("VmRSS:")
                ),
                0,
            )
            found.append({"pid": pid, "argv": argv, "rssMiB": rss})
        except (OSError, ValueError):
            continue
    return found


def gpu_memory_mib() -> tuple[int, int] | None:
    """(used, total) MiB of GPU 0, or None when nvidia-smi gives no number."""
    try:
        out = subprocess.run(
            ["nvidia-smi", "--id=0", "--query-gpu=memory.used,memory.total", "--format=csv,noheader,nounits"],
            capture_output=True,
            text=True,
            timeout=20,
            check=False,
        ).stdout
        used, total = (int(v) for v in out.strip().splitlines()[0].split(","))
        return used, total
    except (OSError, ValueError, IndexError, subprocess.SubprocessError):
        return None


class SlotWatcher:
    """Records every new llama-server task of one model file from /slots, labelled with the turn running at the time."""

    def __init__(self, evidence: Evidence, model_file: str) -> None:
        self.evidence, self.model_file = evidence, model_file
        self.tasks: dict[tuple, dict] = {}
        self.known: set[tuple] = set()
        self.label: str | None = None
        self.lock = threading.Lock()
        self.stop_flag = threading.Event()
        self.poll(record=False)
        self.thread = threading.Thread(target=self._run, daemon=True)
        self.thread.start()

    def poll(self, record: bool = True) -> None:
        with self.lock:
            for proc in llama_processes(self.model_file):
                port = argv_value(proc["argv"], "--port")
                try:
                    url = f"http://127.0.0.1:{port}/slots"
                    # Loopback http only: the URL is built here from a port of a local llama-server process.
                    slots = json.loads(urllib.request.urlopen(url, timeout=2).read())  # noqa: S310  # nosec B310
                except (OSError, ValueError):
                    continue
                for slot in slots if isinstance(slots, list) else []:
                    # The pid belongs in the key: a respawned llama-server can reuse the port and restarts its task
                    # ids at 0, so (port, id_task) alone would take its tasks for ones already seen.
                    key = (proc["pid"], port, slot.get("id_task"))
                    if not isinstance(key[2], int) or key[2] < 0:
                        continue
                    token = slot.get("next_token")
                    token = token[0] if isinstance(token, list) and token else token
                    decoded = token.get("n_decoded") if isinstance(token, dict) else None
                    if key in self.tasks:
                        self.tasks[key]["nDecoded"] = decoded
                        continue
                    if key in self.known or not record:
                        self.known.add(key)
                        continue
                    params = slot.get("params") or {}
                    task = {
                        "t": now(),
                        "label": self.label,
                        "port": port,
                        "pid": key[0],
                        "idTask": key[2],
                        "nCtx": slot.get("n_ctx"),
                        "processing": slot.get("is_processing"),
                        "nPredict": params.get("n_predict"),
                        "temperature": params.get("temperature"),
                        "generationPromptTail": str(params.get("generation_prompt") or "")[-60:],
                        "nDecoded": decoded,
                    }
                    self.tasks[key] = task
                    self.evidence.append("slots.jsonl", task)

    def _run(self) -> None:
        while not self.stop_flag.wait(0.2):
            try:
                self.poll()
            except Exception as error:  # noqa: BLE001  # the watcher must never kill a check
                self.evidence.append("slots.jsonl", {"t": now(), "watcherError": str(error)[:300]})

    def stop(self) -> None:
        self.stop_flag.set()
        self.thread.join(timeout=5)
        self.evidence.append("slots.jsonl", {"t": now(), "final": list(self.tasks.values())})

    def of(self, label: str) -> list[dict]:
        return [t for t in self.tasks.values() if t["label"] == label]

    def background(self) -> list[dict]:
        return [t for t in self.tasks.values() if t["temperature"] == 0]


class Session:
    """Everything the checks of one model share."""

    def __init__(
        self, client: Client, evidence: Evidence, entry: dict, model_file: str, variant: str, checks: list[str]
    ) -> None:
        self.client, self.evidence, self.entry, self.variant = client, evidence, entry, variant
        self.model, self.model_file, self.checks = entry["modelName"], model_file, checks
        self.watch = SlotWatcher(evidence, model_file)
        self.turns: list[dict] = []

    def turn(
        self,
        label: str,
        content: str,
        *,
        effort: str | None = None,
        tools: bool = False,
        conversation: str | None = None,
        agent: str | None = None,
        sampling: dict | None = None,
    ) -> dict:
        if conversation is None:
            body = {"title": f"model-matrix {label}", **({"agentDefinitionId": agent} if agent else {})}
            conversation = self.client.post("/chat/conversations", body)[1]["conversationId"]
        message_id, request_id = str(uuid.uuid4()), str(uuid.uuid4())
        body = {
            "conversationId": conversation,
            "content": content,
            "model": self.model,
            "useLocalTools": tools,
            "messageId": message_id,
            "requestId": request_id,
        }
        if effort is not None:
            body["reasoningEffort"] = effort
        if sampling:
            body["samplingOptions"] = sampling
        stream_file = f"stream-{label}.jsonl"
        self.evidence.append(stream_file, {"t": 0, "send": body, "at": now()})
        result: dict = {
            "label": label,
            "conversationId": conversation,
            "messageId": message_id,
            "outcome": "no-terminal-event",
            "error": None,
            "notices": [],
            "toolsRequested": [],
            "toolsCompleted": [],
            "toolErrors": 0,
            "ttft": None,
        }
        text: list[str] = []
        reasoning: list[str] = []
        start = time.monotonic()
        self.watch.label = label
        try:
            with gsd.HubStream(self.client, f"{API}/chat/hub") as stream:
                for event in stream.invoke_stream("SendMessage", [body], 600.0):
                    t = round(time.monotonic() - start, 3)
                    self.evidence.append(stream_file, {"t": t, **event})
                    kind = event.get("type")
                    if event.get("delta"):
                        text.append(event["delta"])
                        result["ttft"] = result["ttft"] or t
                    if event.get("reasoningDelta"):
                        reasoning.append(event["reasoningDelta"])
                    if kind == "assistant-snapshot":
                        text, reasoning = [event.get("content") or ""], [event.get("reasoning") or ""]
                    elif kind == "assistant-notice":
                        result["notices"].append(event.get("noticeKind"))
                    elif kind == "tool-call-requested":
                        result["toolsRequested"].append(event.get("toolName"))
                    elif kind == "tool-call-completed":
                        result["toolsCompleted"].append(event.get("toolName"))
                        result["toolErrors"] += 1 if event.get("isError") else 0
                    elif kind in TERMINAL:
                        result.update(
                            outcome=kind,
                            error=event.get("error"),
                            outputTokens=event.get("outputTokens"),
                            inputTokens=event.get("inputTokens"),
                        )
                        if event.get("content") is not None:
                            text = [event["content"]]
                        if event.get("reasoning") is not None:
                            reasoning = [event["reasoning"]]
                    elif kind in PARKED:
                        # Nobody answers a parked turn here: cancel it so it releases the slot, and record it.
                        result["outcome"] = kind
                        self.client.post(
                            "/chat/cancel",
                            {"conversationId": conversation, "messageId": message_id, "requestId": request_id},
                            allowed=tuple(range(400, 500)),
                        )
                        break
        except gsd.DriverError as error:
            result.update(outcome="driver-error", error=str(error)[:2000])
        finally:
            self.watch.poll()
            self.watch.label = None
        final = "".join(text)
        result.update(
            seconds=round(time.monotonic() - start, 2),
            content=final[:2000],
            contentChars=len(final.strip()),
            reasoningChars=len("".join(reasoning)),
            wire=self.watch.of(label),
        )
        self.evidence.append("turns.jsonl", result)
        self.turns.append(result)
        return result

    def running(self) -> list[dict]:
        items = self.client.get("/model-fit/running").get("items", [])
        return [i for i in items if i.get("modelName") == self.model]

    def persisted(self, turn: dict) -> str | None:
        """The turn's assistant answer as stored (what a reload shows), or None when the message is not found."""
        conversation = self.client.get(f"/chat/conversations/{turn['conversationId']}", allowed=(404,)) or {}
        message = next(
            (m for m in conversation.get("messages", []) if turn["messageId"] in (m.get("id"), m.get("messageId"))),
            None,
        )
        return None if message is None else message.get("content") or ""

    def eject(self, timeout: float = 60.0) -> bool:
        """Eject the model; True once neither the running view nor the process table has it."""
        self.client.eject_all(self.model, timeout)
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if not self.running() and not llama_processes(self.model_file):
                return True
            time.sleep(1)
        return False


def hard(ok: bool, detail: str) -> dict:
    return {"kind": "hard", "verdict": "pass" if ok else "fail", "detail": detail}


def not_applicable(detail: str) -> dict:
    return {"kind": "hard", "verdict": "n/a", "detail": detail}


def measured(value: object, detail: str = "") -> dict:
    return {"kind": "measured", "verdict": "recorded", "value": value, "detail": detail}


def completed(turn: dict) -> bool:
    return turn["outcome"] == "assistant-completed" and turn["contentChars"] > 0


def brief(turn: dict) -> str:
    wire = [(w["nPredict"], "off" if thinking_off(w["generationPromptTail"]) else "on") for w in turn["wire"]]
    return (
        f"{turn['label']}: {turn['outcome']} {turn['seconds']}s content={turn['contentChars']}c "
        f"reasoning={turn['reasoningChars']}c out={turn.get('outputTokens')} notices={turn['notices']} "
        f"tools={turn['toolsCompleted']} wire(n_predict,thinking)={wire} err={str(turn['error'])[:200]}"
    )


# Checks. Each returns {check name: result}; a check that changes node state restores it in `finally`.


def check_load_and_limit(s: Session, item: dict, settings: dict) -> dict:
    turn = s.turn("plain", "Write three short sentences about the sea.")
    out = {"load-chat": hard(completed(turn), brief(turn))}
    running = s.running()
    window = next((r.get("effectiveContextTokens") for r in running if r.get("effectiveContextTokens")), None)
    window = window or next((w["nCtx"] for w in turn["wire"] if w.get("nCtx")), None)
    chat_tasks = [w for w in turn["wire"] if w["temperature"] != 0]
    if not window or not chat_tasks:
        out["default-limit"] = hard(False, f"no wire evidence (window={window}, tasks={len(chat_tasks)})")
        return out
    expected = expected_default_limit(settings, window, bool(item.get("isReasoningCapable")))
    seen = sorted({w["nPredict"] for w in chat_tasks})
    out["default-limit"] = hard(
        seen == [expected],
        f"n_predict {seen}, expected {expected} (window {window}, reasoning {item.get('isReasoningCapable')}, "
        f"mode {settings.get('chatOutputCapMode')}, effort {settings.get('defaultReasoningEffort')})",
    )
    return out


def check_thinking_off(s: Session) -> dict:
    switch = s.entry["thinkingOff"]
    if switch is None:
        return {"thinking-off": not_applicable("manifest: not a thinking model")}
    if switch == "template":
        turn = s.turn("thinking-off", "What is 17 + 25? Reply with just the number.", effort="none")
        ok = (
            completed(turn)
            and turn["reasoningChars"] == 0
            and not [w for w in turn["wire"] if w["temperature"] != 0 and not thinking_off(w["generationPromptTail"])]
        )
        return {"thinking-off": hard(ok, f"template switch, judged on the response and the wire; {brief(turn)}")}
    # A budget switch (zero reasoning budget) is invisible on the wire and cannot stop such a model from writing a
    # short deliberation, which the node files under reasoning. What the node does guarantee at effort none: a
    # non-empty answer, no EmptyAnswer notice and no think tags in what is stored. "Say OK." once came back empty.
    problems, details = [], []
    for label, prompt in (
        ("thinking-off-ok", "Say OK."),
        ("thinking-off-sum", "What is 17 + 25? Reply with just the number."),
    ):
        turn = s.turn(label, prompt, effort="none")
        stored = s.persisted(turn)
        problems += [f"{label}: {p}" for p in budget_answer_problems(turn, stored)]
        details.append(f"{label} persisted {stored!r} reasoning {turn['reasoningChars']}c; {brief(turn)}")
    detail = f"budget switch, judged on the answer; problems {problems}; " + " | ".join(details)
    return {"thinking-off": hard(not problems, detail)}


def check_length_limit(s: Session, baseline_vram: int | None) -> dict:
    samples: list[dict] = []
    verdict, reason = "n/a", ""
    for run in range(1, LENGTH_LIMIT_SAMPLES + 1):
        turn = s.turn(
            f"length-limit-{run}", LONG_PROMPT, effort="none", sampling={"maxOutputTokens": LENGTH_LIMIT_TOKENS}
        )
        samples.append(turn)
        verdict, reason = length_limit_verdict(samples, LENGTH_LIMIT_TOKENS)
        if verdict != "n/a":
            break
    detail = f"limit {LENGTH_LIMIT_TOKENS}, {reason}; " + " | ".join(brief(t) for t in samples)
    out = {"length-limit": {"kind": "hard", "verdict": verdict, "detail": detail}}
    turn = samples[0]  # the measured numbers come from the first sample: warm, thinking off, same prompt every run
    produced = turn.get("outputTokens")
    if "ttft" in s.checks:
        out["ttft"] = measured(turn["ttft"], "seconds to the first answer token, warm, thinking off")
    if "gen-rate" in s.checks and isinstance(produced, int) and turn["ttft"]:
        out["gen-rate"] = measured(round(produced / max(turn["seconds"] - turn["ttft"], 0.01), 1), "tokens/s")
    if "vram" in s.checks:
        memory = gpu_memory_mib() if s.variant == "cuda" else None
        rise = memory[0] - baseline_vram if memory and baseline_vram is not None else None
        out["vram"] = measured(rise, "MiB over the pre-load baseline (whole device)")
    if "rss" in s.checks:
        out["rss"] = measured(sum(p["rssMiB"] for p in llama_processes(s.model_file)), "MiB, llama-server RSS")
    return out


def check_tool_call(s: Session) -> dict:
    if not s.entry["toolCapable"]:
        return {"tool-call": not_applicable("manifest: not tool-capable")}
    turn = s.turn("tool-call", "Use the calculator tool to compute 48271 * 3917, then tell me the result.", tools=True)
    ok = completed(turn) and "Calculate" in turn["toolsCompleted"] and turn["toolErrors"] == 0
    return {"tool-call": hard(ok, brief(turn))}


def check_multi_tool(s: Session) -> dict:
    if not s.entry["toolCapable"]:
        return {"multi-tool": measured(None, "manifest: not tool-capable")}
    done = 0
    for run in range(1, 4):
        turn = s.turn(f"multi-tool-{run}", MULTI_TOOL_PROMPT, tools=True)
        calls = turn["toolsCompleted"]
        done += completed(turn) and calls.count("Calculate") >= 2 and "GetCurrentTime" in calls
    return {"multi-tool": measured(f"{done}/3", "three-step task completed with all three tool calls")}


def check_memory_job(s: Session) -> dict:
    switch = s.entry["thinkingOff"]
    if switch is None:
        return {"memory-job": not_applicable("manifest: not a thinking model")}
    previous = s.client.get("/node-settings").get("memoryExtractionModelName")
    agent_id = None
    try:
        s.client.put("/node-settings", {"memoryExtractionModelName": s.model})
        agent_id = s.client.post(
            "/agents",
            {
                "name": f"model-matrix memory probe {uuid.uuid4().hex[:6]}",
                "description": "model matrix lane",
                "kind": "Single",
                "instructions": "You are a helpful assistant. Be concise.",
                "allowedToolNames": [],
                "toolApprovals": {},
                "playbookEnabled": True,
                "memoryExtractionEnabled": True,
            },
        )[1]["id"]
        first = s.turn(
            "memory-1",
            "From now on, always give distances in kilometres and keep answers to two "
            "sentences. How far is it from Berlin to Munich?",
            agent=agent_id,
        )
        second = s.turn("memory-2", "And from Hamburg to Cologne?", conversation=first["conversationId"])
        deadline = time.monotonic() + 60
        while not s.watch.background() and time.monotonic() < deadline:
            time.sleep(1)
        jobs = s.watch.background()
        problems = background_job_problems(jobs, switch)
        ok = completed(first) and completed(second) and bool(jobs) and not problems
        verified = (
            "cap and thinking off on the wire"
            if switch == "template"
            else "cap on the wire; thinking off NOT verified (budget switch: neither /slots nor the API shows the "
            "job's budget or its reasoning)"
        )
        detail = (
            f"{verified}; background jobs {[(j['nPredict'], j['generationPromptTail'][-12:]) for j in jobs]}, "
            f"problems {problems}; next turn: {brief(second)}"
        )
        return {"memory-job": hard(ok, detail)}
    finally:
        s.client.put("/node-settings", {"memoryExtractionModelName": previous or ""})
        if agent_id:
            s.client.delete(f"/agents/{agent_id}")


def check_moe_placement(s: Session) -> dict:
    if not s.entry["moe"] or s.variant != "cuda":
        return {"moe-placement": not_applicable("not a MoE model on the GPU")}
    s.eject()
    memory = gpu_memory_mib()
    needed = s.entry["sizeBytes"] // (1 << 20) * 115 // 100 + 1024
    if memory is None or memory[1] - memory[0] < needed:
        return {
            "moe-placement": not_applicable(f"needs ~{needed} MiB free to fit resident; GPU (used, total) {memory}")
        }
    turn = s.turn("moe-load", "Say OK.", effort="none")
    argv = next((p["argv"] for p in llama_processes(s.model_file)), [])
    offload = expert_offload_flags(argv)
    ctx = argv_value(argv, "-c", "--ctx-size")
    surface = [(r.get("effectiveContextTokens"), r.get("expertsOffloaded")) for r in s.running()]
    # Placement is judged on its own: the load turn must complete, its answer text belongs to other checks.
    loaded = turn["outcome"] == "assistant-completed"
    ok = loaded and bool(argv) and not offload and surface == [(int(ctx or 0), False)]
    return {
        "moe-placement": hard(
            ok, f"spawn -c {ctx}, offload flags {offload}, running (window, expertsOffloaded) {surface}; {brief(turn)}"
        )
    }


def explore_after_eject(s: Session, attempts: int = 10, pause: float = 3.0) -> tuple[int, Any]:
    """Eject the model, then start the 4,096 explore; a refusal because a launch of the model is still being admitted
    (an earlier check's turn can leave a background job that re-admits it right after the eject) ejects and retries."""
    status, explore = 0, None
    for attempt in range(attempts):
        if attempt:
            time.sleep(pause)
        s.eject()
        status, explore = s.client.post(
            "/model-fit/profiles/explore",
            {"modelName": s.model, "role": "Chat", "contextTokens": 4096},
            allowed=tuple(range(400, 600)),
        )
        if not (status >= 400 and ADMISSION_CONFLICT in json.dumps(explore)):
            break
    return status, explore


def check_window_tools(s: Session) -> dict:
    if not s.entry["toolCapable"] or s.variant != "cuda":
        return {"window-4096-tools": not_applicable("needs a tool-capable model on the GPU")}
    profile_id = None
    try:
        status, explore = explore_after_eject(s)
        profile = (explore.get("profile") or {}) if isinstance(explore, dict) else {}
        profile_id = profile.get("profileId") or profile.get("id")
        if status >= 400 or not profile_id:
            return {"window-4096-tools": hard(False, f"explore 4096 refused: HTTP {status} {str(explore)[:300]}")}
        steps = []
        for step, body in (
            ("benchmark", {"profileId": profile_id, "allowPreSpawnVramPressure": True}),
            ("freeze", {"profileId": profile_id}),
        ):
            status, _ = s.client.post(f"/model-fit/profiles/{step}", body, allowed=tuple(range(400, 600)))
            steps.append(f"{step} {status}")
            if status >= 400:
                return {"window-4096-tools": hard(False, f"pinning 4096 failed: {steps}")}
        s.eject()
        agents = s.client.get("/agents").get("items", [])
        agent = next((a["id"] for a in agents if a.get("isDefaultAssistant")), None)
        turn = s.turn(
            "window-4096",
            "What is 17 multiplied by 23? Use the Calculate tool to work it out.",
            tools=True,
            agent=agent,
        )
        windows = sorted({w["nCtx"] for w in turn["wire"] if w["temperature"] != 0})
        # Whether the model calls the tool is model behaviour (pinned by tool-call at the full window): reported only.
        verdict, outcome = window_tools_verdict(turn, windows)
        called = "Calculate" in turn["toolsCompleted"]
        detail = f"outcome {outcome}, slot n_ctx {windows}, Calculate called {called}; {brief(turn)}"
        return {"window-4096-tools": {"kind": "hard", "verdict": verdict, "outcome": outcome, "detail": detail}}
    finally:
        if profile_id:
            s.client.post("/model-fit/profiles/invalidate", {"profileId": profile_id}, allowed=tuple(range(400, 600)))
        s.eject()


def check_no_think_tags(s: Session) -> dict:
    leaked, read = [], 0
    for turn in s.turns:
        if turn["outcome"] != "assistant-completed":
            continue
        content = s.persisted(turn)
        if content is None:
            continue
        read += 1
        if any(tag in content for tag in THINK_TAGS):
            leaked.append(turn["label"])
    return {"no-think-tags": hard(read > 0 and not leaked, f"{read} persisted answers read, tags in {leaked}")}


def run_model(
    client: Client, evidence: Evidence, entry: dict, models_dir: Path, variant: str, checks: list[str]
) -> dict:
    evidence.at(entry["id"])
    path = installed_file(models_dir, entry["modelName"])
    result: dict = {"id": entry["id"], "modelName": entry["modelName"], "checks": {}}
    item = next((i for i in client.get("/models").get("items", []) if i.get("modelName") == entry["modelName"]), None)
    if item is None or path is None:
        result["checks"]["load-chat"] = hard(False, "the node does not list the model, or no installed file")
        return result
    session = Session(client, evidence, entry, path.name, variant, checks)
    try:
        # Every model starts from an empty runtime, so the VRAM baseline and the slot evidence are its own.
        client.eject_all()
        memory = gpu_memory_mib() if variant == "cuda" else None
        settings = client.get("/node-settings")
        steps = [
            ({"load-chat", "default-limit"}, lambda: check_load_and_limit(session, item, settings)),
            ({"thinking-off"}, lambda: check_thinking_off(session)),
            (
                {"length-limit", "ttft", "gen-rate", "vram", "rss"},
                lambda: check_length_limit(session, memory[0] if memory else None),
            ),
            ({"tool-call"}, lambda: check_tool_call(session)),
            ({"multi-tool"}, lambda: check_multi_tool(session)),
            ({"memory-job"}, lambda: check_memory_job(session)),
            ({"moe-placement"}, lambda: check_moe_placement(session)),
            ({"window-4096-tools"}, lambda: check_window_tools(session)),
            ({"no-think-tags"}, lambda: check_no_think_tags(session)),
            ({"eject"}, lambda: {"eject": hard(session.eject(), "no running entry and no llama-server process left")}),
        ]
        for names, step in steps:
            if not names & set(checks):
                continue
            try:
                outcome = step()
            except gsd.DriverError as error:
                outcome = {n: hard(False, f"driver error: {error}") for n in names if n in HARD_CHECKS}
            result["checks"].update({k: v for k, v in outcome.items() if k in checks})
            evidence.append("checks.jsonl", {"t": now(), **outcome})
    finally:
        session.watch.stop()
    return result


# Subcommands


def command_resolve(args: argparse.Namespace) -> int:
    manifest = json.loads(Path(args.manifest).read_text(encoding="utf-8"))
    try:
        entries = resolve_models(manifest, args.tier, [i for i in args.models.split(",") if i])
        resolve_checks(args.checks)
    except ValueError as error:
        print(f"[model-matrix-driver] {error}", file=sys.stderr)
        return 2
    for entry in entries:
        print(entry["id"])
    return 0


def command_local_file(args: argparse.Namespace) -> int:
    manifest = json.loads(Path(args.manifest).read_text(encoding="utf-8"))
    entry = resolve_models(manifest, "", [args.id])[0]
    state, actual = file_state(Path(args.models_dir), entry)
    print(f"{state}\t{actual}")
    return 0


def command_base_url(_: argparse.Namespace) -> int:
    try:
        status = json.load(sys.stdin)
    except json.JSONDecodeError:
        return 1
    for resource in status.get("resources", []):
        if resource.get("name") != "app":
            continue
        origins = [u.get("url", "").rstrip("/") for u in resource.get("urls", []) if u.get("url", "").count("/") == 2]
        for scheme in ("https://", "http://"):
            origin = next((o for o in origins if o.startswith(scheme)), None)
            if origin:
                print(origin)
                return 0
    return 1


def command_download(args: argparse.Namespace) -> int:
    manifest = json.loads(Path(args.manifest).read_text(encoding="utf-8"))
    evidence = Evidence(Path(args.evidence) / "_download")
    client = connect(args.base_url, evidence)
    for entry in resolve_models(manifest, "", args.ids.split(",")):
        _, started = client.post(
            "/model-fit/download", {"repoId": entry["repo"], "fileName": entry["file"], "includeProjector": False}
        )
        operation = urllib.parse.quote(str(started["operationId"]))
        deadline, phase = time.monotonic() + args.timeout, ""
        while time.monotonic() < deadline:
            state = client.get(f"/model-fit/gguf/downloads/operations/{operation}")
            phase = str(state.get("phase") or "")
            if phase.lower() not in ("running", "queued", "pending", "starting", "verifying", "finalizing"):
                break
            time.sleep(5)
        if phase.lower() != "completed":
            raise gsd.DriverError(f"download of {entry['id']} ended in phase {phase!r}")
        print(f"{entry['id']}\tdownloaded")
    return 0


def command_run(args: argparse.Namespace) -> int:
    manifest = json.loads(Path(args.manifest).read_text(encoding="utf-8"))
    entries = resolve_models(manifest, "", args.ids.split(","))
    checks = resolve_checks(args.checks)
    evidence = Evidence(Path(args.evidence))
    client = connect(args.base_url, evidence)
    # Before anything is judged: the backend the node actually runs must be the one the run is labelled with.
    audit = gsd.device_audit(client)
    problem = backend_problem(args.variant, audit)
    if problem:
        raise PrerequisiteError(problem)
    backend = {k: audit.get(k) for k in ("inferenceBackend", "cpuFallback", "gpuExpected")}
    results: dict = {
        "startedAt": now(),
        "tier": args.tier,
        "variant": args.variant,
        "backend": backend,
        "checks": checks,
        "models": [],
    }
    print(f"[model-matrix-driver] device audit: backend {backend['inferenceBackend']}", flush=True)
    for entry in entries:
        print(f"[model-matrix-driver] {entry['id']}: running {len(checks)} check(s)", flush=True)
        model = run_model(client, evidence, entry, Path(args.models_dir), args.variant, checks)
        results["models"].append(model)
        for name, check in model["checks"].items():
            value = f"{check.get('value')} " if check["kind"] == "measured" else ""
            print(f"  {check['verdict']:8} {name}: {value}{str(check.get('detail'))[:300]}", flush=True)
    results["finishedAt"] = now()
    (evidence.root / "results.json").write_text(json.dumps(results, indent=2, ensure_ascii=False), encoding="utf-8")
    (evidence.root / "summary.md").write_text(summary_markdown(results), encoding="utf-8")
    return 0


def command_owned_servers(_: argparse.Namespace) -> int:
    """Print `pid<TAB>start time` of every llama-server this checkout's node spawned, whatever model it serves."""
    for pid in owned_servers(proc_table(), "", HOST_DIR):
        try:
            start = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()[19]
        except (OSError, IndexError):
            continue
        print(f"{pid}\t{start}")
    return 0


def command_judge(args: argparse.Namespace) -> int:
    try:
        results = json.loads(Path(args.results).read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        print(f"FAIL no readable results ({error}): a run without evidence is not a pass")
        return 1
    code, lines = judge(results)
    print("\n".join(lines))
    return code


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    resolve = sub.add_parser("resolve")
    resolve.add_argument("--manifest", required=True)
    resolve.add_argument("--tier", default="fast")
    resolve.add_argument("--models", default="")
    resolve.add_argument("--checks", default="")
    resolve.set_defaults(func=command_resolve)
    local = sub.add_parser("local-file")
    local.add_argument("--manifest", required=True)
    local.add_argument("--models-dir", required=True)
    local.add_argument("--id", required=True)
    local.set_defaults(func=command_local_file)
    sub.add_parser("base-url").set_defaults(func=command_base_url)
    sub.add_parser("owned-servers").set_defaults(func=command_owned_servers)
    download = sub.add_parser("download")
    run = sub.add_parser("run")
    for node in (download, run):
        node.add_argument("--base-url", required=True)
        node.add_argument("--manifest", required=True)
        node.add_argument("--ids", required=True)
        node.add_argument("--evidence", required=True)
    download.add_argument("--timeout", type=float, default=3600.0)
    download.set_defaults(func=command_download)
    run.add_argument("--models-dir", required=True)
    run.add_argument("--variant", choices=("cuda", "cpu"), default="cuda")
    run.add_argument("--tier", default="")
    run.add_argument("--checks", default="")
    run.set_defaults(func=command_run)
    judge_cmd = sub.add_parser("judge")
    judge_cmd.add_argument("--results", required=True)
    judge_cmd.set_defaults(func=command_judge)
    return parser


def main(argv: list[str]) -> int:
    args = build_parser().parse_args(argv)
    try:
        return args.func(args)
    except PrerequisiteError as error:
        print(f"[model-matrix-driver] PREREQUISITE: {error}", file=sys.stderr)
        return 2
    except (ConnectionError, gsd.DriverError) as error:
        print(f"[model-matrix-driver] {args.command}: {error}", file=sys.stderr)
        return 5


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
