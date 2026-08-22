"""Bounded process execution and host-observation helpers."""

from __future__ import annotations

import contextlib
import hashlib
import json
import math
import os
import platform
import re
import shutil
import signal
import statistics
import subprocess
import threading
import time
from collections.abc import Callable
from pathlib import Path
from typing import Any

from .contracts import (
    MAX_CAPTURE_STREAM_BYTES,
    PROCESS_CLEANUP_TIMEOUT_SECONDS,
    CaptureError,
    require_string,
    utc_now,
)


def capture_text(argv: list[str], timeout_seconds: float = 10) -> dict[str, Any]:
    executable = shutil.which(argv[0]) if not os.path.isabs(argv[0]) else argv[0]
    if not executable or not Path(executable).exists():
        return {"argv": argv, "available": False, "exit_code": None, "stdout": "", "stderr": ""}
    try:
        completed = subprocess.run(argv, text=True, capture_output=True, timeout=timeout_seconds, check=False)
        return {
            "argv": argv,
            "available": True,
            "exit_code": completed.returncode,
            "stdout": completed.stdout.strip(),
            "stderr": completed.stderr.strip(),
        }
    except (OSError, subprocess.TimeoutExpired) as exc:
        return {"argv": argv, "available": True, "exit_code": None, "stdout": "", "stderr": str(exc)}


def capture_ambient(
    _capture_text: Callable[[list[str]], dict[str, Any]] = capture_text,
) -> dict[str, Any]:
    load_average = list(os.getloadavg()) if hasattr(os, "getloadavg") else None
    memory: dict[str, int] = {}
    meminfo = Path("/proc/meminfo")
    if meminfo.exists():
        for line in meminfo.read_text(encoding="utf-8").splitlines():
            key, separator, value = line.partition(":")
            if separator and key in {"MemTotal", "MemAvailable", "SwapTotal", "SwapFree"}:
                memory[key + "KiB"] = int(value.strip().split()[0])
    gpu = _capture_text(
        [
            "nvidia-smi",
            "--query-gpu=index,name,driver_version,memory.total,memory.free,memory.used,utilization.gpu",
            "--format=csv,noheader,nounits",
        ]
    )
    return {"captured_at_utc": utc_now(), "load_average_1m_5m_15m": load_average, "memory": memory, "nvidia_smi": gpu}


def global_gpu_used_mib(ambient: dict[str, Any] | None) -> int | None:
    if not ambient:
        return None
    probe = ambient.get("nvidia_smi")
    if not isinstance(probe, dict) or probe.get("exit_code") != 0:
        return None
    stdout = probe.get("stdout")
    if not isinstance(stdout, str) or not stdout:
        return None
    total = 0
    for line in stdout.splitlines():
        parts = [part.strip() for part in line.split(",")]
        if len(parts) != 7:
            return None
        try:
            total += int(parts[5])
        except ValueError:
            return None
    return total


def global_gpu_free_mib(ambient: dict[str, Any] | None) -> int | None:
    if not ambient:
        return None
    probe = ambient.get("nvidia_smi")
    if not isinstance(probe, dict) or probe.get("exit_code") != 0:
        return None
    stdout = probe.get("stdout")
    if not isinstance(stdout, str) or not stdout:
        return None
    total = 0
    for line in stdout.splitlines():
        parts = [part.strip() for part in line.split(",")]
        if len(parts) != 7:
            return None
        try:
            total += int(parts[4])
        except ValueError:
            return None
    return total


def process_budget_free_mib(device_probe: dict[str, Any]) -> int | None:
    output = f"{device_probe.get('stdout', '')}\n{device_probe.get('stderr', '')}"
    values = [int(value) for value in re.findall(r"\((?:\d+ MiB,\s*)?(\d+) MiB free\)", output)]
    return sum(values) if values else None


def capture_host(
    runtime_binary: Path,
    _capture_text: Callable[[list[str]], dict[str, Any]] = capture_text,
) -> dict[str, Any]:
    cpu = ""
    cpuinfo = Path("/proc/cpuinfo")
    if cpuinfo.exists():
        for line in cpuinfo.read_text(encoding="utf-8", errors="replace").splitlines():
            if line.lower().startswith("model name"):
                cpu = line.partition(":")[2].strip()
                break
    return {
        "os": platform.platform(),
        "kernel": platform.release(),
        "architecture": platform.machine(),
        "cpu": cpu or platform.processor() or "unknown",
        "logical_cpu_count": os.cpu_count(),
        "python": platform.python_version(),
        "runtime_version": _capture_text([str(runtime_binary), "--version"]),
        "runtime_devices": _capture_text([str(runtime_binary), "--list-devices"]),
        "nvidia_smi_driver": _capture_text(
            ["nvidia-smi", "--query-gpu=index,name,driver_version", "--format=csv,noheader"]
        ),
        "repository_head": _capture_text(["git", "rev-parse", "HEAD"]),
        "repository_status": _capture_text(["git", "status", "--short"]),
    }


def command_argv(command: dict[str, Any], context: str) -> list[str]:
    argv = command.get("argv")
    if not isinstance(argv, list) or not argv or not all(isinstance(item, str) and item for item in argv):
        raise CaptureError(f"{context}.argv must be a non-empty string array")
    return argv


def numeric_metrics(stdout: str) -> dict[str, float]:
    try:
        parsed = json.loads(stdout)
    except json.JSONDecodeError:
        return {}
    if isinstance(parsed, list):
        metrics: dict[str, float] = {}
        for item in parsed:
            if not isinstance(item, dict) or not isinstance(item.get("avg_ts"), (int, float)):
                continue
            if item.get("embeddings") is True:
                name = "embedding_tokens_per_second"
            elif isinstance(item.get("n_gen"), int) and item["n_gen"] > 0:
                name = "generation_tokens_per_second"
            elif isinstance(item.get("n_prompt"), int) and item["n_prompt"] > 0:
                name = "prompt_tokens_per_second"
            else:
                continue
            metrics[name] = float(item["avg_ts"])
        return metrics
    if not isinstance(parsed, dict):
        return {}
    return {
        key: float(value)
        for key, value in parsed.items()
        if isinstance(key, str) and isinstance(value, (int, float)) and math.isfinite(float(value))
    }


def percentile95(values: list[float]) -> float:
    ordered = sorted(values)
    index = max(0, math.ceil(0.95 * len(ordered)) - 1)
    return ordered[index]


class BoundedStreamCapture:
    def __init__(self) -> None:
        marker_size = len(f"\n... <truncated; full sha256={'0' * 64}> ...\n".encode())
        retained_bytes = MAX_CAPTURE_STREAM_BYTES - marker_size
        self._prefix_capacity = retained_bytes // 2
        self._suffix_capacity = retained_bytes - self._prefix_capacity
        self._digest = hashlib.sha256()
        self._byte_count = 0
        self._complete = bytearray()
        self._prefix = b""
        self._suffix = bytearray()
        self._truncated = False

    def add(self, value: str) -> None:
        encoded = value.encode("utf-8")
        self._digest.update(encoded)
        self._byte_count += len(encoded)
        if not self._truncated:
            combined = self._complete + encoded
            if len(combined) <= MAX_CAPTURE_STREAM_BYTES:
                self._complete = combined
                return
            self._truncated = True
            self._prefix = bytes(combined[: self._prefix_capacity])
            self._suffix = bytearray(combined[-self._suffix_capacity :])
            self._complete.clear()
            return

        self._suffix.extend(encoded)
        if len(self._suffix) > self._suffix_capacity:
            del self._suffix[: -self._suffix_capacity]

    def finish(self) -> tuple[str, dict[str, Any]]:
        digest = self._digest.hexdigest()
        metadata = {"bytes": self._byte_count, "sha256": digest, "truncated": self._truncated}
        if not self._truncated:
            return self._complete.decode("utf-8", errors="ignore"), metadata
        marker = f"\n... <truncated; full sha256={digest}> ...\n"
        bounded = self._prefix.decode("utf-8", errors="ignore") + marker + self._suffix.decode("utf-8", errors="ignore")
        return bounded, metadata


def drain_capture_stream(stream: Any, capture: BoundedStreamCapture, errors: list[BaseException]) -> None:
    try:
        while chunk := stream.read(8192):
            capture.add(chunk)
    except BaseException as exc:
        errors.append(exc)
    finally:
        stream.close()


def cleanup_process_group(
    process: subprocess.Popen[str], readers: tuple[threading.Thread, threading.Thread], executable: str
) -> None:
    deadline = time.monotonic() + PROCESS_CLEANUP_TIMEOUT_SECONDS
    signal_process_group(process, signal.SIGKILL)
    try:
        process.wait(timeout=max(0.001, deadline - time.monotonic()))
    except subprocess.TimeoutExpired as exc:
        raise CaptureError(f"Cleanup failed: process group for {executable!r} did not exit after SIGKILL") from exc
    for reader in readers:
        reader.join(timeout=max(0.0, deadline - time.monotonic()))
    if any(reader.is_alive() for reader in readers):
        raise CaptureError(f"Cleanup failed: output readers for {executable!r} did not finish")


def run_once(
    argv: list[str], timeout_seconds: float, expected_timeout: bool, cwd: str | None, env: dict[str, str]
) -> dict[str, Any]:
    start = time.monotonic()
    try:
        process = subprocess.Popen(
            argv, cwd=cwd, env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True
        )
    except OSError as exc:
        raise CaptureError(f"Could not start {argv[0]!r}: {exc}") from exc
    stdout_capture = BoundedStreamCapture()
    stderr_capture = BoundedStreamCapture()
    reader_errors: list[BaseException] = []
    stdout_reader = threading.Thread(
        target=drain_capture_stream, args=(process.stdout, stdout_capture, reader_errors), daemon=True
    )
    stderr_reader = threading.Thread(
        target=drain_capture_stream, args=(process.stderr, stderr_capture, reader_errors), daemon=True
    )
    stdout_reader.start()
    stderr_reader.start()
    cleanup_finished = False
    try:
        deadline = start + timeout_seconds
        peak_rss_bytes = 0
        ambient_during: dict[str, Any] | None = None
        while process.poll() is None and time.monotonic() < deadline:
            status_path = Path(f"/proc/{process.pid}/status")
            if status_path.exists():
                for line in status_path.read_text(encoding="utf-8", errors="replace").splitlines():
                    if line.startswith("VmRSS:"):
                        peak_rss_bytes = max(peak_rss_bytes, int(line.split()[1]) * 1024)
                        break
            if ambient_during is None and time.monotonic() - start >= min(0.5, timeout_seconds / 2):
                ambient_during = capture_ambient()
            time.sleep(min(0.1, max(0.01, deadline - time.monotonic())))
        timed_out = process.poll() is None
        if timed_out:
            signal_process_group(process, signal.SIGTERM)
            with contextlib.suppress(subprocess.TimeoutExpired):
                process.wait(timeout=PROCESS_CLEANUP_TIMEOUT_SECONDS)
        cleanup_process_group(process, (stdout_reader, stderr_reader), argv[0])
        cleanup_finished = True
        if reader_errors:
            raise CaptureError(f"Could not read captured output from {argv[0]!r}: {reader_errors[0]}")
        stdout, stdout_metadata = stdout_capture.finish()
        stderr, stderr_metadata = stderr_capture.finish()
        elapsed_ms = (time.monotonic() - start) * 1000
        success = (timed_out and expected_timeout) or (not timed_out and process.returncode == 0)
        parsed_metrics = {} if stdout_metadata["truncated"] else numeric_metrics(stdout)
        metrics = {"wall_elapsed_ms": round(elapsed_ms, 3), **parsed_metrics}
        return {
            "exit_code": process.returncode,
            "timed_out": timed_out,
            "expected_timeout": expected_timeout,
            "success": success,
            "elapsed_ms": round(elapsed_ms, 3),
            "peak_rss_bytes": peak_rss_bytes or None,
            "ambient_during": ambient_during,
            "stdout": stdout,
            "stdout_bytes": stdout_metadata["bytes"],
            "stdout_sha256": stdout_metadata["sha256"],
            "stdout_truncated": stdout_metadata["truncated"],
            "stderr": stderr,
            "stderr_bytes": stderr_metadata["bytes"],
            "stderr_sha256": stderr_metadata["sha256"],
            "stderr_truncated": stderr_metadata["truncated"],
            "metrics": metrics,
        }
    finally:
        if not cleanup_finished:
            cleanup_process_group(process, (stdout_reader, stderr_reader), argv[0])


def signal_process_group(process: subprocess.Popen[str], requested_signal: signal.Signals) -> None:
    try:
        if os.name == "posix":
            os.killpg(process.pid, requested_signal)
        elif process.poll() is None:
            if requested_signal == signal.SIGTERM:
                process.terminate()
            else:
                process.kill()
    except (OSError, ProcessLookupError):
        # The group may already be gone; cleanup is deliberately idempotent.
        pass


def run_command(command: dict[str, Any], context: str) -> dict[str, Any]:
    argv = command_argv(command, context)
    warmups = command.get("warmups", 0)
    repeats = command.get("repeats", 1)
    timeout_seconds = command.get("timeout_seconds", 120)
    expected_timeout = command.get("expected_timeout", False)
    if not isinstance(warmups, int) or warmups < 0:
        raise CaptureError(f"{context}.warmups must be a non-negative integer")
    if not isinstance(repeats, int) or repeats < 1:
        raise CaptureError(f"{context}.repeats must be a positive integer")
    if not isinstance(timeout_seconds, (int, float)) or timeout_seconds <= 0:
        raise CaptureError(f"{context}.timeout_seconds must be positive")
    if not isinstance(expected_timeout, bool):
        raise CaptureError(f"{context}.expected_timeout must be boolean")
    raw_env = command.get("env", {})
    if not isinstance(raw_env, dict) or not all(isinstance(k, str) and isinstance(v, str) for k, v in raw_env.items()):
        raise CaptureError(f"{context}.env must be a string map")
    env = {**os.environ, **raw_env}
    cwd = command.get("cwd")
    if cwd is not None and (not isinstance(cwd, str) or not Path(cwd).is_dir()):
        raise CaptureError(f"{context}.cwd must be an existing directory")

    warmup_results = [run_once(argv, float(timeout_seconds), expected_timeout, cwd, env) for _ in range(warmups)]
    before = capture_ambient()
    runs = [run_once(argv, float(timeout_seconds), expected_timeout, cwd, env) for _ in range(repeats)]
    after = capture_ambient()
    if any(not run["success"] for run in warmup_results + runs):
        raise CaptureError(f"{context} failed; partial evidence is retained only in the terminal output")
    metric_names = sorted({name for run in runs for name in run["metrics"]})
    aggregates: dict[str, dict[str, float]] = {}
    for name in metric_names:
        values = [run["metrics"][name] for run in runs if name in run["metrics"]]
        if len(values) == repeats:
            aggregates[name] = {
                "median": statistics.median(values),
                "p95": percentile95(values),
                "minimum": min(values),
                "maximum": max(values),
            }
    return {
        "name": require_string(command, "name", context),
        "partition": command.get("partition", "native-performance"),
        "comparability": command.get("comparability", "exact-argv-and-identity"),
        "argv": argv,
        "argv_sha256": hashlib.sha256(json.dumps(argv, separators=(",", ":")).encode()).hexdigest(),
        "warmups": warmups,
        "repeats": repeats,
        "timeout_seconds": timeout_seconds,
        "warmup_results": warmup_results,
        "runs": runs,
        "aggregates": aggregates,
        "ambient_before": before,
        "ambient_after": after,
    }
