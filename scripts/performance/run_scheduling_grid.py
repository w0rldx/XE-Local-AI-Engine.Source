#!/usr/bin/env python3
"""Run the bounded Lane 4 llama-server scheduling experiment."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import signal
import statistics
import subprocess
import threading
import time
import urllib.error
import urllib.request
from collections.abc import Callable
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

CONFIGS = [
    ("baseline", 1, 512, 512, 2048, False),
    ("p2", 2, 512, 512, 2048, False),
    ("p4", 4, 512, 512, 2048, False),
    ("p4-kvu", 4, 512, 512, 2048, True),
    ("p4-c4096-b1024", 4, 1024, 512, 4096, False),
    ("p4-c4096-ub1024", 4, 1024, 1024, 4096, False),
]
SCENARIOS = ("short", "median", "max", "large", "concurrent")
IGNORED_OUTPUT_KEYS = frozenset(("prompt_tokens", "total_tokens"))


def digest(path: Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(chunk)
    return value.hexdigest()


def post(port: int, route: str, payload: dict, timeout: float = 120) -> dict:
    request = urllib.request.Request(
        f"http://127.0.0.1:{port}{route}",
        data=json.dumps(payload, separators=(",", ":")).encode(),
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.load(response)


def get(port: int, route: str, timeout: float = 2) -> str:
    with urllib.request.urlopen(f"http://127.0.0.1:{port}{route}", timeout=timeout) as response:
        return response.read().decode()


def parse_process_gpu_rows(stdout: str, pid: int) -> int:
    """Return GPU memory used by exactly pid, ignoring unrelated processes."""
    total = 0
    for line in stdout.splitlines():
        fields = [item.strip() for item in line.split(",")]
        if len(fields) != 2:
            continue
        try:
            row_pid = int(fields[0])
            used_mib = int(fields[1])
        except ValueError:
            continue
        if row_pid == pid:
            total += used_mib
    return total


def gpu_process_sample(
    pid: int,
    runner: Callable[..., subprocess.CompletedProcess] = subprocess.run,
) -> dict:
    try:
        process = runner(
            [
                "nvidia-smi",
                "--query-compute-apps=pid,used_gpu_memory",
                "--format=csv,noheader,nounits",
            ],
            text=True,
            capture_output=True,
            check=False,
        )
    except OSError as error:
        return {"status": "unavailable", "reason": f"nvidia-smi launch failed: {error}"}
    if process.returncode:
        reason = process.stderr.strip() or f"nvidia-smi exited {process.returncode}"
        return {"status": "unavailable", "reason": reason}
    used_mib = parse_process_gpu_rows(process.stdout, pid)
    if used_mib == 0:
        return {"status": "zero", "used_mib": 0}
    return {"status": "measured", "used_mib": used_mib}


class Sampler:
    def __init__(
        self,
        pid: int,
        backend: str,
        gpu_probe: Callable[[int], dict] = gpu_process_sample,
    ):
        self.pid = pid
        self.backend = backend
        self.gpu_probe = gpu_probe
        self.peak_rss_bytes = 0
        self.peak_process_gpu_residency_mib = 0
        self._gpu_unavailable_reason: str | None = None
        self._gpu_sample_count = 0
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True)

    def __enter__(self):
        self._thread.start()
        return self

    def __exit__(self, *_):
        self._stop.set()
        self._thread.join()

    @property
    def gpu_residency(self) -> dict:
        if self.backend == "cpu":
            return {"status": "not_applicable", "reason": "cpu_backend"}
        if self._gpu_unavailable_reason is not None:
            return {"status": "unavailable", "reason": self._gpu_unavailable_reason}
        if self.peak_process_gpu_residency_mib == 0:
            return {
                "status": "zero",
                "peak_used_mib": 0,
                "sample_count": self._gpu_sample_count,
            }
        return {
            "status": "measured",
            "peak_used_mib": self.peak_process_gpu_residency_mib,
            "sample_count": self._gpu_sample_count,
        }

    def _run(self):
        while not self._stop.wait(0.05):
            try:
                fields = Path(f"/proc/{self.pid}/status").read_text().splitlines()
                rss = next(int(line.split()[1]) * 1024 for line in fields if line.startswith("VmRSS:"))
                self.peak_rss_bytes = max(self.peak_rss_bytes, rss)
            except (OSError, StopIteration):
                pass
            if self.backend == "cpu":
                continue
            sample = self.gpu_probe(self.pid)
            self._gpu_sample_count += 1
            if sample["status"] == "unavailable":
                self._gpu_unavailable_reason = sample["reason"]
            elif sample["status"] == "measured":
                self.peak_process_gpu_residency_mib = max(
                    self.peak_process_gpu_residency_mib,
                    sample["used_mib"],
                )


def percentile(values: list[float], fraction: float) -> float:
    ordered = sorted(values)
    return ordered[max(0, min(len(ordered) - 1, int(len(ordered) * fraction + 0.999999) - 1))]


def tokenize(port: int, content: str) -> int:
    result = post(
        port,
        "/tokenize",
        {"content": content, "add_special": True, "with_pieces": False},
    )
    return len(result["tokens"])


def distinct_inputs(prefix: str, text: str, count: int, label: str) -> list[str]:
    return [f"{prefix}{text} Deterministic {label} input {index:02d}." for index in range(count)]


def embedding(port: int, inputs: list[str]) -> dict:
    return post(port, "/v1/embeddings", {"model": "nomic", "input": inputs})


def rerank(port: int, documents: list[str]) -> dict:
    return post(
        port,
        "/v1/rerank",
        {
            "query": "Which passage discusses inference scheduling?",
            "documents": documents,
        },
    )


def role_request(role: str) -> Callable[[int, list[str]], dict]:
    if role == "embedding":
        return embedding
    if role == "reranker":
        return rerank
    raise ValueError(f"unsupported role: {role}")


def actual_request_preflight(
    fn: Callable[[int, list[str]], dict],
    port: int,
    items: list[str],
    role: str,
) -> dict:
    route = "/v1/embeddings" if role == "embedding" else "/v1/rerank"
    try:
        response = fn(port, items)
    except urllib.error.HTTPError as error:
        return {
            "status": "rejected",
            "route": route,
            "http_status": error.code,
            "error_body": error.read().decode(errors="replace"),
        }
    canonical = canonicalize_output(response)
    return {
        "status": "accepted",
        "route": route,
        "canonical_output_sha256": canonical_output_digest(canonical),
    }


def _actual_request_accepted(
    fn: Callable[[int, list[str]], dict],
    port: int,
    content: str,
) -> bool:
    try:
        fn(port, [content])
    except urllib.error.HTTPError:
        return False
    return True


def build_corpus(port: int, role: str) -> dict:
    fn = role_request(role)
    prefix = "search_document: " if role == "embedding" else ""
    short_text = "A concise local inference note."
    median_text = ("Scheduling affects independent request occupancy and physical batch capacity. " * 18).strip()
    short = distinct_inputs(prefix, short_text, 16, "short")
    median = distinct_inputs(prefix, median_text, 8, "median")
    large = distinct_inputs(prefix, median_text, 16, "large")
    concurrent = [distinct_inputs(prefix, short_text, 2, f"concurrent-{batch:02d}") for batch in range(8)]

    low, high, chosen = 1, 700, ""
    while low <= high:
        middle = (low + high) // 2
        candidate = prefix + ("evidence " * middle) + "deterministic maximum input."
        raw_tokens = tokenize(port, candidate)
        if raw_tokens <= 512 and _actual_request_accepted(fn, port, candidate):
            chosen, low = candidate, middle + 1
        else:
            high = middle - 1
    if not chosen:
        raise RuntimeError(f"{role} maximum corpus construction found no accepted input")

    return {
        "short": short,
        "median": median,
        "max": [chosen],
        "large": large,
        "concurrent": concurrent,
        "token_readback": {
            "short_max": max(tokenize(port, item) for item in short),
            "median_max": max(tokenize(port, item) for item in median),
            "max_document": tokenize(port, chosen),
        },
        "maximum_construction": {
            "role": role,
            "actual_request_route": ("/v1/embeddings" if role == "embedding" else "/v1/rerank"),
            "includes_role_template_overhead": True,
        },
    }


def canonicalize_output(value):
    if isinstance(value, dict):
        return {key: canonicalize_output(item) for key, item in value.items() if key not in IGNORED_OUTPUT_KEYS}
    if isinstance(value, list):
        return [canonicalize_output(item) for item in value]
    return value


def canonical_output_digest(value) -> str:
    encoded = json.dumps(
        value,
        sort_keys=True,
        separators=(",", ":"),
        ensure_ascii=False,
    ).encode()
    return hashlib.sha256(encoded).hexdigest()


def outputs_equivalent(left, right, tolerance: float = 1e-5) -> bool:
    if (
        isinstance(left, (float, int))
        and not isinstance(left, bool)
        and isinstance(right, (float, int))
        and not isinstance(right, bool)
    ):
        return abs(float(left) - float(right)) <= tolerance
    if isinstance(left, list) and isinstance(right, list):
        return len(left) == len(right) and all(
            outputs_equivalent(a, b, tolerance) for a, b in zip(left, right, strict=True)
        )
    if isinstance(left, dict) and isinstance(right, dict):
        keys = (set(left) | set(right)) - IGNORED_OUTPUT_KEYS
        return all(
            key in left and key in right and outputs_equivalent(left[key], right[key], tolerance) for key in keys
        )
    return left == right


def run_request(fn, port: int, items: list[str]) -> tuple[float, dict]:
    started = time.perf_counter()
    result = fn(port, items)
    return time.perf_counter() - started, result


def scenario(fn, port: int, items, repeats: int, concurrent: bool = False) -> dict:
    def once():
        if concurrent:
            started = time.perf_counter()
            with ThreadPoolExecutor(max_workers=8) as executor:
                results = list(executor.map(lambda batch: fn(port, batch), items))
            return time.perf_counter() - started, results
        return run_request(fn, port, items)

    try:
        once()
    except urllib.error.HTTPError as error:
        return rejected_scenario(error)
    elapsed, outputs = [], []
    for _ in range(repeats):
        try:
            duration, result = once()
        except urllib.error.HTTPError as error:
            return rejected_scenario(error)
        elapsed.append(duration)
        outputs.append(canonicalize_output(result))
    canonical = outputs[0]
    item_count = sum(len(batch) for batch in items) if concurrent else len(items)
    return {
        "elapsed_seconds": elapsed,
        "median_seconds": statistics.median(elapsed),
        "p95_seconds": percentile(elapsed, 0.95),
        "median_items_per_second": item_count / statistics.median(elapsed),
        "repeat_deterministic": all(outputs_equivalent(canonical, item) for item in outputs[1:]),
        "canonical_output_sha256": canonical_output_digest(canonical),
        "_canonical_output": canonical,
    }


def rejected_scenario(error: urllib.error.HTTPError) -> dict:
    return {
        "rejected": True,
        "http_status": error.code,
        "error_body": error.read().decode(errors="replace"),
        "repeat_deterministic": False,
    }


def parse_context_readback(log_text: str) -> dict:
    slots = [int(value) for value in re.findall(r"new slot, n_ctx = (\d+)", log_text)]
    if not slots:
        return {
            "status": "missing",
            "readback_source": None,
            "reason": "llama-server emitted no explicit per-slot n_ctx readback",
        }
    return {
        "status": "measured",
        "readback_source": "llama-server slot initialization log",
        "per_sequence_context": min(slots),
        "slot_contexts": slots,
    }


def wait_ready(process: subprocess.Popen, port: int, log_path: Path):
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"server exited {process.returncode}: {log_path.read_text(errors='replace')[-4000:]}")
        try:
            if "ok" in get(port, "/health").lower():
                return
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(0.1)
    raise RuntimeError("server readiness timeout")


def run_config(
    args,
    role: str,
    backend: str,
    config,
    port: int,
    corpus: dict | None,
) -> tuple[dict, dict]:
    name, parallel, batch, ubatch, context, unified = config
    model = Path(args.embedding_model if role == "embedding" else args.reranker_model)
    argv = [
        args.server,
        "-m",
        str(model),
        "--host",
        "127.0.0.1",
        "--port",
        str(port),
        "--parallel",
        str(parallel),
        "--no-warmup",
        "--metrics",
        "-c",
        str(context),
        "-b",
        str(batch),
        "-ub",
        str(ubatch),
        "-ngl",
        "-1" if backend == "cuda" else "0",
        "--threads",
        str(args.cpu_threads),
    ]
    argv += ["--kv-unified" if unified else "--no-kv-unified"]
    argv += ["--embeddings", "--pooling", "mean"] if role == "embedding" else ["--rerank", "--pooling", "rank"]
    log_path = Path(args.raw_dir) / f"{backend}-{role}-{name}.log"
    with log_path.open("w+") as log:
        process = subprocess.Popen(
            argv,
            stdout=log,
            stderr=subprocess.STDOUT,
            start_new_session=True,
        )
        try:
            wait_ready(process, port, log_path)
            if corpus is None:
                corpus = build_corpus(port, role)
            fn = role_request(role)
            preflight = actual_request_preflight(fn, port, corpus["max"], role)
            log.flush()
            context_readback = parse_context_readback(log_path.read_text(errors="replace"))
            gate = preflight["status"] == "accepted" and context_readback["status"] == "measured"
            result = {
                "name": name,
                "role": role,
                "backend": backend,
                "argv": argv,
                "argv_sha256": hashlib.sha256(b"\0".join(item.encode() for item in argv)).hexdigest(),
                "parallel": parallel,
                "n_batch": batch,
                "n_ubatch": ubatch,
                "requested_total_context": context,
                "context_readback": context_readback,
                "kv_unified": unified,
                "token_gate": {
                    "document_tokens": corpus["token_readback"]["max_document"],
                    "actual_role_request": preflight,
                    "passed": gate,
                },
                "startup_log_sha256": digest(log_path),
            }
            if gate:
                with Sampler(process.pid, backend) as sampler:
                    result["scenarios"] = {
                        key: scenario(
                            fn,
                            port,
                            corpus[key],
                            args.repeats,
                            key == "concurrent",
                        )
                        for key in SCENARIOS
                    }
                result["peak_rss_bytes"] = sampler.peak_rss_bytes
                result["process_gpu_residency"] = sampler.gpu_residency
                result["repeat_determinism_passed"] = all(
                    item["repeat_deterministic"] for item in result["scenarios"].values()
                )
                if any(item.get("rejected") for item in result["scenarios"].values()):
                    result["token_gate"]["passed"] = False
            return result, corpus
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(10)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait()


def baseline_eligible(baseline: dict) -> tuple[bool, str | None]:
    if baseline.get("context_readback", {}).get("status") != "measured":
        return False, "baseline_missing_context_readback"
    if baseline.get("token_gate", {}).get("passed") is not True:
        return False, "baseline_role_preflight_or_runtime_rejected"
    if baseline.get("repeat_determinism_passed") is not True:
        return False, "baseline_repeat_nondeterministic"
    for scenario_name in SCENARIOS:
        value = baseline.get("scenarios", {}).get(scenario_name, {})
        if value.get("rejected") or "_canonical_output" not in value:
            return False, f"baseline_{scenario_name}_unusable"
    return True, None


def gpu_memory_delta(baseline: dict, candidate: dict, backend: str) -> dict:
    if backend == "cpu":
        return {
            "status": "not_applicable",
            "delta_percent": None,
            "passed": True,
            "reason": "cpu_backend_ignores_gpu_activity",
        }
    old = baseline.get("process_gpu_residency", {})
    new = candidate.get("process_gpu_residency", {})
    if old.get("status") != "measured" or new.get("status") != "measured":
        return {
            "status": "not_comparable",
            "delta_percent": None,
            "passed": False,
            "reason": (
                f"cuda residency requires measured non-zero samples; "
                f"baseline={old.get('status', 'missing')}, "
                f"candidate={new.get('status', 'missing')}"
            ),
        }
    old_value = old.get("peak_used_mib", 0)
    new_value = new.get("peak_used_mib", 0)
    if old_value <= 0 or new_value <= 0:
        return {
            "status": "not_comparable",
            "delta_percent": None,
            "passed": False,
            "reason": "cuda residency was zero",
        }
    delta = (new_value / old_value - 1) * 100
    return {
        "status": "comparable",
        "delta_percent": delta,
        "passed": delta <= 5,
    }


def evaluate(results: list[dict]) -> dict:
    comparisons = []
    for backend in ("cuda", "cpu"):
        for role in ("embedding", "reranker"):
            group = [item for item in results if item["backend"] == backend and item["role"] == role]
            baseline = next(item for item in group if item["name"] == "baseline")
            eligible, baseline_reason = baseline_eligible(baseline)
            for scenario_name in SCENARIOS:
                scenario_value = baseline.get("scenarios", {}).get(scenario_name)
                if scenario_value is not None:
                    scenario_value["equivalent_to_baseline"] = eligible
            for candidate in group:
                if candidate is baseline:
                    continue
                deltas: dict[str, float | None] = {}
                semantic_equivalence: dict[str, bool] = {}
                comparable = eligible
                for scenario_name in SCENARIOS:
                    old = baseline.get("scenarios", {}).get(scenario_name, {})
                    new = candidate.get("scenarios", {}).get(scenario_name, {})
                    equivalent = (
                        eligible
                        and "_canonical_output" in old
                        and "_canonical_output" in new
                        and outputs_equivalent(
                            old["_canonical_output"],
                            new["_canonical_output"],
                        )
                    )
                    semantic_equivalence[scenario_name] = equivalent
                    if new:
                        new["equivalent_to_baseline"] = equivalent
                    scenario_comparable = (
                        eligible
                        and not old.get("rejected")
                        and not new.get("rejected")
                        and old.get("repeat_deterministic") is True
                        and new.get("repeat_deterministic") is True
                        and equivalent
                        and "median_items_per_second" in old
                        and "median_items_per_second" in new
                    )
                    if not scenario_comparable:
                        comparable = False
                        deltas[scenario_name] = None
                    else:
                        deltas[scenario_name] = (
                            new["median_items_per_second"] / old["median_items_per_second"] - 1
                        ) * 100
                old_rss = baseline.get("peak_rss_bytes", 0)
                new_rss = candidate.get("peak_rss_bytes", 0)
                rss_delta = (new_rss / old_rss - 1) * 100 if old_rss > 0 and new_rss > 0 else None
                gpu_delta = gpu_memory_delta(baseline, candidate, backend)
                qualifies = (
                    comparable
                    and candidate.get("context_readback", {}).get("status") == "measured"
                    and candidate.get("token_gate", {}).get("passed") is True
                    and candidate.get("repeat_determinism_passed") is True
                    and all(semantic_equivalence.values())
                    and all(value is not None and value >= 20 for value in deltas.values())
                    and rss_delta is not None
                    and rss_delta <= 5
                    and gpu_delta["passed"] is True
                )
                comparisons.append(
                    {
                        "backend": backend,
                        "role": role,
                        "candidate": candidate["name"],
                        "throughput_delta_percent": deltas,
                        "rss_delta_percent": rss_delta,
                        "process_gpu_residency": gpu_delta,
                        "semantic_equivalence_to_baseline": semantic_equivalence,
                        "baseline_eligible": eligible,
                        "baseline_ineligible_reason": baseline_reason,
                        "comparable_to_baseline": comparable,
                        "qualifies": qualifies,
                    }
                )
    winners = [item for item in comparisons if item["qualifies"]]
    return {
        "acceptance_rule": (
            "All five corpus scenarios >=20% median throughput improvement; "
            "actual role-request preflight, explicit context readback, repeat "
            "determinism, and baseline semantic equivalence pass; peak RSS and "
            "CUDA process-residency regression each <=5%. CPU cells ignore GPU."
        ),
        "winners": winners,
        "ship_production_tuning": bool(winners),
        "outcome": "ship" if winners else "no-change",
        "comparisons": comparisons,
    }


def extract_baseline_outputs(results: list[dict]) -> dict:
    outputs: dict[str, dict[str, dict]] = {}
    for item in results:
        backend = item["backend"]
        role = item["role"]
        if item["name"] == "baseline":
            outputs.setdefault(backend, {})[role] = {
                scenario_name: item["scenarios"][scenario_name].get("_canonical_output")
                for scenario_name in SCENARIOS
                if scenario_name in item.get("scenarios", {})
            }
        for scenario_value in item.get("scenarios", {}).values():
            scenario_value.pop("_canonical_output", None)
    return outputs


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--server", required=True)
    parser.add_argument("--embedding-model", required=True)
    parser.add_argument("--reranker-model", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--raw-dir", required=True)
    parser.add_argument("--source-commit", default="88bd2353")
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--cpu-threads", type=int, default=16)
    args = parser.parse_args()
    Path(args.raw_dir).mkdir(parents=True, exist_ok=True)
    identities = {
        key: {"path": str(Path(value).resolve()), "sha256": digest(Path(value))}
        for key, value in {
            "server": args.server,
            "embedding_model": args.embedding_model,
            "reranker_model": args.reranker_model,
        }.items()
    }
    results: list[dict] = []
    corpora: dict[str, dict[str, dict]] = {}
    port = 19300
    for backend in ("cuda", "cpu"):
        corpora[backend] = {}
        for role in ("embedding", "reranker"):
            corpus = None
            for config in CONFIGS:
                item, corpus = run_config(
                    args,
                    role,
                    backend,
                    config,
                    port,
                    corpus,
                )
                results.append(item)
                port += 1
            corpora[backend][role] = corpus
    decision = evaluate(results)
    baseline_outputs = extract_baseline_outputs(results)
    artifact = {
        "schema_version": "2.0",
        "kind": "lane4-scheduling-grid",
        "source_commit": args.source_commit,
        "runtime_identity": identities,
        "captured_at_utc": time.strftime(
            "%Y-%m-%dT%H:%M:%SZ",
            time.gmtime(),
        ),
        "grid": {
            "configs": [list(item) for item in CONFIGS],
            "backends": ["cuda", "cpu"],
            "roles": ["embedding", "reranker"],
        },
        "corpora": corpora,
        "canonical_baseline_outputs": baseline_outputs,
        "results": results,
        "chat_launch_contract": {
            "requirement": (
                "No chat setting is varied; source remains pinned at 88bd2353 "
                "and no production launch-policy edit is made."
            ),
            "supervisor_sha256": digest(
                Path("XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaServerProcessSupervisor.cs")
            ),
        },
        "gaps": [
            "Vulkan: no NVIDIA Vulkan ICD",
            "Native Windows manual",
            "8 GB hardware unavailable",
            "native Linux OOM unavailable",
        ],
        "decision": decision,
    }
    Path(args.output).parent.mkdir(parents=True, exist_ok=True)
    Path(args.output).write_text(json.dumps(artifact, indent=2, sort_keys=True) + "\n")
    print(f"Wrote {args.output}")


if __name__ == "__main__":
    main()
