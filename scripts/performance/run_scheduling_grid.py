#!/usr/bin/env python3
"""Run the bounded Lane 4 llama-server scheduling experiment."""

from __future__ import annotations

import argparse
import concurrent.futures
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
from pathlib import Path


CONFIGS = [
    ("baseline", 1, 512, 512, 2048, False),
    ("p2", 2, 512, 512, 2048, False),
    ("p4", 4, 512, 512, 2048, False),
    ("p4-kvu", 4, 512, 512, 2048, True),
    ("p4-c4096-b1024", 4, 1024, 512, 4096, False),
    ("p4-c4096-ub1024", 4, 1024, 1024, 4096, False),
]


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


def gpu_sample() -> dict | None:
    process = subprocess.run(
        ["nvidia-smi", "--query-gpu=memory.total,memory.free,memory.used,utilization.gpu",
         "--format=csv,noheader,nounits"],
        text=True, capture_output=True, check=False,
    )
    if process.returncode:
        return None
    values = [int(item.strip()) for item in process.stdout.strip().split(",")]
    return dict(zip(("total_mib", "free_mib", "used_mib", "utilization_percent"), values, strict=True))


class Sampler:
    def __init__(self, pid: int):
        self.pid = pid
        self.peak_rss_bytes = 0
        self.max_gpu_used_mib = 0
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True)

    def __enter__(self):
        self._thread.start()
        return self

    def __exit__(self, *_):
        self._stop.set()
        self._thread.join()

    def _run(self):
        while not self._stop.wait(0.05):
            try:
                fields = Path(f"/proc/{self.pid}/status").read_text().splitlines()
                rss = next(int(line.split()[1]) * 1024 for line in fields if line.startswith("VmRSS:"))
                self.peak_rss_bytes = max(self.peak_rss_bytes, rss)
            except (OSError, StopIteration):
                pass
            sample = gpu_sample()
            if sample:
                self.max_gpu_used_mib = max(self.max_gpu_used_mib, sample["used_mib"])


def percentile(values: list[float], fraction: float) -> float:
    ordered = sorted(values)
    return ordered[max(0, min(len(ordered) - 1, int((len(ordered) * fraction + 0.999999)) - 1))]


def tokenize(port: int, content: str) -> int:
    result = post(port, "/tokenize", {"content": content, "add_special": True, "with_pieces": False})
    return len(result["tokens"])


def build_corpus(port: int) -> dict:
    prefix = "search_document: "
    short = prefix + "A concise local inference note."
    median = prefix + ("Scheduling affects independent request occupancy and physical batch capacity. " * 18)
    low, high, chosen = 1, 700, ""
    while low <= high:
        middle = (low + high) // 2
        candidate = prefix + ("evidence " * middle)
        count = tokenize(port, candidate)
        if count <= 512:
            chosen, low = candidate, middle + 1
        else:
            high = middle - 1
    return {
        "short": [short] * 16,
        "median": [median] * 8,
        "max": [chosen],
        "large": [median] * 16,
        "concurrent": [[short, short] for _ in range(8)],
        "token_readback": {
            "short": tokenize(port, short),
            "median": tokenize(port, median),
            "max_with_nomic_prefix_and_special_tokens": tokenize(port, chosen),
        },
    }


def embedding(port: int, inputs: list[str]) -> dict:
    return post(port, "/v1/embeddings", {"model": "nomic", "input": inputs})


def rerank(port: int, documents: list[str]) -> dict:
    return post(port, "/v1/rerank", {"query": "Which passage discusses inference scheduling?", "documents": documents})


def run_request(fn, port: int, items: list[str]) -> tuple[float, dict]:
    started = time.perf_counter()
    result = fn(port, items)
    return time.perf_counter() - started, result


def outputs_equivalent(left, right, tolerance: float = 1e-5) -> bool:
    if isinstance(left, float) and isinstance(right, (float, int)):
        return abs(left - right) <= tolerance
    if isinstance(left, list) and isinstance(right, list):
        return len(left) == len(right) and all(outputs_equivalent(a, b, tolerance) for a, b in zip(left, right, strict=True))
    if isinstance(left, dict) and isinstance(right, dict):
        ignored = {"prompt_tokens", "total_tokens"}
        keys = (set(left) | set(right)) - ignored
        return all(key in left and key in right and outputs_equivalent(left[key], right[key], tolerance) for key in keys)
    return left == right


def scenario(fn, port: int, items, repeats: int, concurrent: bool = False) -> dict:
    def once():
        if concurrent:
            started = time.perf_counter()
            with concurrent_futures() as executor:
                results = list(executor.map(lambda batch: fn(port, batch), items))
            return time.perf_counter() - started, results
        return run_request(fn, port, items)

    try:
        once()
    except urllib.error.HTTPError as error:
        return {
            "rejected": True,
            "http_status": error.code,
            "error_body": error.read().decode(errors="replace"),
            "deterministic_response": False,
        }
    elapsed, outputs = [], []
    for _ in range(repeats):
        try:
            duration, result = once()
        except urllib.error.HTTPError as error:
            return {
                "rejected": True,
                "http_status": error.code,
                "error_body": error.read().decode(errors="replace"),
                "deterministic_response": False,
            }
        elapsed.append(duration)
        outputs.append(result)
    item_count = sum(len(batch) for batch in items) if concurrent else len(items)
    return {
        "elapsed_seconds": elapsed,
        "median_seconds": statistics.median(elapsed),
        "p95_seconds": percentile(elapsed, 0.95),
        "median_items_per_second": item_count / statistics.median(elapsed),
        "deterministic_response": all(outputs_equivalent(outputs[0], item) for item in outputs[1:]),
    }


def concurrent_futures():
    return concurrent.futures.ThreadPoolExecutor(max_workers=8)


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


def run_config(args, role: str, backend: str, config, port: int, corpus: dict | None) -> tuple[dict, dict]:
    name, parallel, batch, ubatch, context, unified = config
    model = Path(args.embedding_model if role == "embedding" else args.reranker_model)
    argv = [
        args.server, "-m", str(model), "--host", "127.0.0.1", "--port", str(port),
        "--parallel", str(parallel), "--no-warmup", "--metrics", "-c", str(context),
        "-b", str(batch), "-ub", str(ubatch), "-ngl", "-1" if backend == "cuda" else "0",
        "--threads", str(args.cpu_threads),
    ]
    argv += ["--kv-unified" if unified else "--no-kv-unified"]
    argv += ["--embeddings", "--pooling", "mean"] if role == "embedding" else ["--rerank", "--pooling", "rank"]
    log_path = Path(args.raw_dir) / f"{backend}-{role}-{name}.log"
    before_gpu = gpu_sample()
    with log_path.open("w+") as log:
        process = subprocess.Popen(argv, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            wait_ready(process, port, log_path)
            if corpus is None:
                corpus = build_corpus(port)
            log.flush()
            text = log_path.read_text(errors="replace")
            slots = [int(value) for value in re.findall(r"new slot, n_ctx = (\\d+)", text)]
            per_sequence = min(slots) if slots else (context if unified else ((context // parallel + 255) // 256) * 256)
            required = corpus["token_readback"]["max_with_nomic_prefix_and_special_tokens"]
            gate = per_sequence >= required and ubatch >= required
            result = {
                "name": name, "role": role, "backend": backend, "argv": argv,
                "argv_sha256": hashlib.sha256(b"\\0".join(item.encode() for item in argv)).hexdigest(),
                "parallel": parallel, "n_batch": batch, "n_ubatch": ubatch,
                "requested_total_context": context, "readback_per_sequence_context": per_sequence,
                "kv_unified": unified, "token_gate_required": required, "token_gate_passed": gate,
                "startup_log_sha256": digest(log_path),
            }
            if gate:
                fn = embedding if role == "embedding" else rerank
                with Sampler(process.pid) as sampler:
                    result["scenarios"] = {
                        key: scenario(fn, port, corpus[key], args.repeats, key == "concurrent")
                        for key in ("short", "median", "max", "large", "concurrent")
                    }
                result["peak_rss_bytes"] = sampler.peak_rss_bytes
                result["peak_global_gpu_used_mib"] = sampler.max_gpu_used_mib
                result["correctness_passed"] = all(item["deterministic_response"] for item in result["scenarios"].values())
                if result["scenarios"]["max"].get("rejected"):
                    result["token_gate_passed"] = False
                    result["token_gate_runtime_rejection"] = result["scenarios"]["max"]["error_body"]
            return result, corpus
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(10)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait()
            result_gpu = gpu_sample()
            if "result" in locals():
                result["gpu_before"] = before_gpu
                result["gpu_after"] = result_gpu


def evaluate(results: list[dict]) -> dict:
    comparisons = []
    for backend in ("cuda", "cpu"):
        for role in ("embedding", "reranker"):
            group = [item for item in results if item["backend"] == backend and item["role"] == role]
            baseline = next(item for item in group if item["name"] == "baseline")
            for candidate in group:
                if candidate is baseline:
                    continue
                deltas = {}
                comparable = True
                for scenario_name in ("short", "median", "max", "large", "concurrent"):
                    old = baseline.get("scenarios", {}).get(scenario_name, {})
                    new = candidate.get("scenarios", {}).get(scenario_name, {})
                    if old.get("rejected") or new.get("rejected") or "median_items_per_second" not in old or "median_items_per_second" not in new:
                        comparable = False
                        deltas[scenario_name] = None
                    else:
                        deltas[scenario_name] = (new["median_items_per_second"] / old["median_items_per_second"] - 1) * 100
                rss_delta = (candidate["peak_rss_bytes"] / baseline["peak_rss_bytes"] - 1) * 100
                gpu_delta = (candidate["peak_global_gpu_used_mib"] / baseline["peak_global_gpu_used_mib"] - 1) * 100
                qualifies = (
                    comparable
                    and candidate.get("token_gate_passed") is True
                    and candidate.get("correctness_passed") is True
                    and all(value is not None and value >= 20 for value in deltas.values())
                    and rss_delta <= 5
                    and gpu_delta <= 5
                )
                comparisons.append({
                    "backend": backend, "role": role, "candidate": candidate["name"],
                    "throughput_delta_percent": deltas, "rss_delta_percent": rss_delta,
                    "global_gpu_used_delta_percent": gpu_delta, "comparable_to_baseline": comparable,
                    "qualifies": qualifies,
                })
    winners = [item for item in comparisons if item["qualifies"]]
    return {
        "acceptance_rule": "All five corpus scenarios >=20% median throughput improvement; token/readback and semantic correctness gates pass; peak RSS and global GPU-used regression each <=5%.",
        "winners": winners,
        "ship_production_tuning": bool(winners),
        "outcome": "ship" if winners else "no-change",
        "comparisons": comparisons,
    }


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
        for key, value in {"server": args.server, "embedding_model": args.embedding_model, "reranker_model": args.reranker_model}.items()
    }
    results, corpus, port = [], None, 19300
    for backend in ("cuda", "cpu"):
        for role in ("embedding", "reranker"):
            for config in CONFIGS:
                item, corpus = run_config(args, role, backend, config, port, corpus)
                results.append(item)
                port += 1
    artifact = {
        "schema_version": "1.0", "kind": "lane4-scheduling-grid",
        "source_commit": args.source_commit, "runtime_identity": identities,
        "captured_at_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "grid": {"configs": [list(item) for item in CONFIGS], "backends": ["cuda", "cpu"], "roles": ["embedding", "reranker"]},
        "corpus": corpus, "results": results,
        "chat_launch_contract": {
            "requirement": "No chat setting is varied; source remains pinned at 88bd2353 and no production launch-policy edit is made.",
            "supervisor_sha256": digest(Path("XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaServerProcessSupervisor.cs")),
        },
        "gaps": ["Vulkan: no NVIDIA Vulkan ICD", "Native Windows manual", "8 GB hardware unavailable", "native Linux OOM unavailable"],
        "decision": evaluate(results),
    }
    Path(args.output).parent.mkdir(parents=True, exist_ok=True)
    Path(args.output).write_text(json.dumps(artifact, indent=2, sort_keys=True) + "\n")
    print(f"Wrote {args.output}")


if __name__ == "__main__":
    main()
