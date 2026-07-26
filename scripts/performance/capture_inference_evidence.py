#!/usr/bin/env python3
"""Capture reproducible local-inference benchmark and fit/replay evidence.

The tool intentionally uses only the Python standard library. It treats the input
specification as an immutable experiment contract, verifies every declared file
hash before executing anything, records exact argv vectors, and writes one stable
JSON artifact that can later be compared without reconstructing operator state.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import platform
import re
import shlex
import shutil
import statistics
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


SCHEMA_VERSION = "1.0"
FIT_FLAGS_WITH_VALUE = (
    "-c", "--ctx-size",
    "-ngl", "--gpu-layers", "--n-gpu-layers",
    "-ts", "--tensor-split",
    "-ot", "--override-tensor",
    "-ctk", "--cache-type-k",
    "-ctv", "--cache-type-v",
    "-fa", "--flash-attn",
)
FIT_FLAG_CANONICAL = {
    "--ctx-size": "-c",
    "--gpu-layers": "-ngl",
    "--n-gpu-layers": "-ngl",
    "--tensor-split": "-ts",
    "--override-tensor": "-ot",
    "--cache-type-k": "-ctk",
    "--cache-type-v": "-ctv",
    "--flash-attn": "-fa",
}
FIT_HELPER_ARGS_WITH_VALUE = {
    "-m", "--model",
    "-c", "--ctx-size",
    "-b", "--batch-size",
    "-ub", "--ubatch-size",
    "-np", "--parallel",
    "-fa", "--flash-attn",
    "-ctk", "--cache-type-k",
    "-ctv", "--cache-type-v",
    "-dev", "--device",
    "-sm", "--split-mode",
    "-mg", "--main-gpu",
    "-fit", "--fit",
    "-fitt", "--fit-target",
    "-fitc", "--fit-ctx",
    "--pooling",
}
FIT_HELPER_VALUELESS_ARGS = {"--mlock", "--mmap", "--no-mmap", "--no-host", "--no-op-offload"}


class CaptureError(RuntimeError):
    pass


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def load_json(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise CaptureError(f"Could not read JSON from {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise CaptureError(f"{path} must contain a JSON object")
    return value


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    temp.replace(path)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def sha256_tree(path: Path) -> str:
    if path.is_file():
        return sha256_file(path)
    if not path.is_dir():
        raise CaptureError(f"Identity path does not exist: {path}")
    digest = hashlib.sha256()
    files = sorted(item for item in path.rglob("*") if item.is_file())
    if not files:
        raise CaptureError(f"Identity directory is empty: {path}")
    for item in files:
        relative = item.relative_to(path).as_posix().encode("utf-8")
        digest.update(len(relative).to_bytes(8, "big"))
        digest.update(relative)
        digest.update(bytes.fromhex(sha256_file(item)))
    return digest.hexdigest()


def require_string(obj: dict[str, Any], key: str, context: str) -> str:
    value = obj.get(key)
    if not isinstance(value, str) or not value.strip():
        raise CaptureError(f"{context}.{key} must be a non-empty string")
    return value


def verify_identity(label: str, entry: dict[str, Any]) -> dict[str, Any]:
    raw_path = Path(require_string(entry, "path", label)).expanduser().resolve()
    expected = require_string(entry, "sha256", label).lower()
    if len(expected) != 64 or any(char not in "0123456789abcdef" for char in expected):
        raise CaptureError(f"{label}.sha256 must be a lowercase SHA-256 digest")
    actual = sha256_tree(raw_path)
    if actual != expected:
        raise CaptureError(f"{label} hash mismatch: expected {expected}, got {actual} ({raw_path})")
    return {
        **entry,
        "path": str(raw_path),
        "sha256_verified": True,
        "runtime_local_dependencies": runtime_local_dependencies(raw_path) if raw_path.is_file() and os.access(raw_path, os.X_OK) else None,
    }


def runtime_local_dependencies(binary: Path) -> dict[str, Any]:
    probe = capture_text(["ldd", str(binary)])
    dependencies: list[dict[str, str]] = []
    if probe["exit_code"] == 0:
        binary_directory = binary.parent.resolve()
        for line in probe["stdout"].splitlines():
            candidate = line.partition("=>")[2].strip().split(" ", 1)[0] if "=>" in line else line.strip().split(" ", 1)[0]
            if not candidate.startswith("/"):
                continue
            path = Path(candidate).resolve()
            if path.is_file() and path.parent == binary_directory:
                dependencies.append({"name": path.name, "sha256": sha256_file(path)})
    dependencies.sort(key=lambda item: item["name"])
    manifest_digest = hashlib.sha256(json.dumps(dependencies, separators=(",", ":"), sort_keys=True).encode()).hexdigest()
    return {"ldd": probe, "files": dependencies, "manifest_sha256": manifest_digest}


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


def capture_ambient() -> dict[str, Any]:
    load_average = list(os.getloadavg()) if hasattr(os, "getloadavg") else None
    memory: dict[str, int] = {}
    meminfo = Path("/proc/meminfo")
    if meminfo.exists():
        for line in meminfo.read_text(encoding="utf-8").splitlines():
            key, separator, value = line.partition(":")
            if separator and key in {"MemTotal", "MemAvailable", "SwapTotal", "SwapFree"}:
                memory[key + "KiB"] = int(value.strip().split()[0])
    gpu = capture_text([
        "nvidia-smi",
        "--query-gpu=index,name,uuid,driver_version,memory.total,memory.free,memory.used,utilization.gpu",
        "--format=csv,noheader,nounits",
    ])
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
        if len(parts) != 8:
            return None
        try:
            total += int(parts[6])
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
        if len(parts) != 8:
            return None
        try:
            total += int(parts[5])
        except ValueError:
            return None
    return total


def process_budget_free_mib(device_probe: dict[str, Any]) -> int | None:
    output = f"{device_probe.get('stdout', '')}\n{device_probe.get('stderr', '')}"
    values = [int(value) for value in re.findall(r"\((?:\d+ MiB,\s*)?(\d+) MiB free\)", output)]
    return sum(values) if values else None


def capture_host(runtime_binary: Path) -> dict[str, Any]:
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
        "runtime_version": capture_text([str(runtime_binary), "--version"]),
        "runtime_devices": capture_text([str(runtime_binary), "--list-devices"]),
        "nvidia_smi_driver": capture_text(["nvidia-smi", "--query-gpu=index,name,uuid,driver_version", "--format=csv,noheader"]),
        "repository_head": capture_text(["git", "rev-parse", "HEAD"]),
        "repository_status": capture_text(["git", "status", "--short"]),
    }


def validate_spec(spec: dict[str, Any], expected_kind: str) -> None:
    if spec.get("schema_version") != SCHEMA_VERSION:
        raise CaptureError(f"schema_version must be {SCHEMA_VERSION!r}")
    if spec.get("kind") != expected_kind:
        raise CaptureError(f"kind must be {expected_kind!r}")
    require_string(spec, "capture_id", "spec")
    phase = require_string(spec, "phase", "spec")
    if phase not in {"baseline", "rebaseline", "fit-proof", "experiment"}:
        raise CaptureError("spec.phase must be baseline, rebaseline, fit-proof, or experiment")


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


def run_once(argv: list[str], timeout_seconds: float, expected_timeout: bool, cwd: str | None, env: dict[str, str]) -> dict[str, Any]:
    start = time.monotonic()
    try:
        process = subprocess.Popen(argv, cwd=cwd, env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True)
    except OSError as exc:
        raise CaptureError(f"Could not start {argv[0]!r}: {exc}") from exc
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
        process.terminate()
        try:
            stdout, stderr = process.communicate(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            stdout, stderr = process.communicate()
    else:
        stdout, stderr = process.communicate()
    elapsed_ms = (time.monotonic() - start) * 1000
    success = (timed_out and expected_timeout) or (not timed_out and process.returncode == 0)
    return {
        "exit_code": process.returncode,
        "timed_out": timed_out,
        "expected_timeout": expected_timeout,
        "success": success,
        "elapsed_ms": round(elapsed_ms, 3),
        "peak_rss_bytes": peak_rss_bytes or None,
        "ambient_during": ambient_during,
        "stdout": stdout,
        "stderr": stderr,
        "metrics": {"wall_elapsed_ms": round(elapsed_ms, 3), **numeric_metrics(stdout)},
    }


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
            aggregates[name] = {"median": statistics.median(values), "p95": percentile95(values), "minimum": min(values), "maximum": max(values)}
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


def capture_baseline(spec_path: Path, output_path: Path) -> None:
    spec = load_json(spec_path)
    validate_spec(spec, "inference-benchmark-capture")
    models = spec.get("models")
    if not isinstance(models, list) or not models:
        raise CaptureError("spec.models must be a non-empty array")
    verified_models = []
    for index, model in enumerate(models):
        if not isinstance(model, dict):
            raise CaptureError(f"spec.models[{index}] must be an object")
        for key in ("name", "role", "quant"):
            require_string(model, key, f"spec.models[{index}]")
        verified_models.append(verify_identity(f"spec.models[{index}]", model))
    corpus = spec.get("corpus")
    runtime = spec.get("runtime")
    if not isinstance(corpus, dict) or not isinstance(runtime, dict):
        raise CaptureError("spec.corpus and spec.runtime must be objects")
    require_string(corpus, "name", "spec.corpus")
    require_string(runtime, "tag", "spec.runtime")
    require_string(runtime, "provenance", "spec.runtime")
    require_string(runtime, "backend", "spec.runtime")
    verified_corpus = verify_identity("spec.corpus", corpus)
    verified_runtime = verify_identity("spec.runtime", runtime)
    auxiliaries = runtime.get("auxiliary_binaries", [])
    if not isinstance(auxiliaries, list) or not all(isinstance(item, dict) for item in auxiliaries):
        raise CaptureError("spec.runtime.auxiliary_binaries must be an array of identity objects")
    verified_auxiliaries = [
        verify_identity(f"spec.runtime.auxiliary_binaries[{index}]", item)
        for index, item in enumerate(auxiliaries)
    ]
    verified_runtime["auxiliary_binaries"] = verified_auxiliaries
    commands = spec.get("commands")
    if not isinstance(commands, list) or not commands:
        raise CaptureError("spec.commands must be a non-empty array")
    benchmark = spec.get("benchmark")
    framework = spec.get("framework")
    coverage = spec.get("coverage")
    if not isinstance(benchmark, dict) or not isinstance(framework, dict) or not isinstance(coverage, dict):
        raise CaptureError("spec.framework, spec.benchmark, and spec.coverage must be objects")
    for key in ("source_commit", "maf_version", "meai_version", "openai_version"):
        require_string(framework, key, "spec.framework")
    for key in ("cache_state", "acceptance_rule"):
        require_string(benchmark, key, "spec.benchmark")
    gaps = coverage.get("unvalidated")
    if not isinstance(gaps, list) or not gaps or not all(isinstance(item, dict) and item.get("target") and item.get("reason") for item in gaps):
        raise CaptureError("spec.coverage.unvalidated must explicitly list target/reason objects")

    started_at = utc_now()
    results = [run_command(command, f"spec.commands[{index}]") for index, command in enumerate(commands) if isinstance(command, dict)]
    if len(results) != len(commands):
        raise CaptureError("Every spec.commands entry must be an object")
    artifact = {
        "schema_version": SCHEMA_VERSION,
        "kind": "inference-benchmark-evidence",
        "capture_id": spec["capture_id"],
        "phase": spec["phase"],
        "started_at_utc": started_at,
        "completed_at_utc": utc_now(),
        "spec_sha256": sha256_file(spec_path),
        "source_spec": spec,
        "verified_identity": {"models": verified_models, "corpus": verified_corpus, "runtime": verified_runtime},
        "host": capture_host(Path(verified_runtime["path"])),
        "framework": framework,
        "benchmark": benchmark,
        "coverage": coverage,
        "commands": results,
    }
    write_json(output_path, artifact)


def strip_one_verbose(argv: list[str]) -> list[str]:
    result = list(argv)
    for flag in ("-v", "--verbose"):
        if flag in result:
            result.remove(flag)
            return result
    return result


def extract_fit_flags(argv: list[str]) -> dict[str, list[str]]:
    parsed: dict[str, list[str]] = {}
    index = 0
    while index < len(argv):
        flag = argv[index]
        if flag in FIT_FLAGS_WITH_VALUE:
            if index + 1 >= len(argv):
                raise CaptureError(f"Launch vector ends after {flag}")
            canonical = FIT_FLAG_CANONICAL.get(flag, flag)
            parsed.setdefault(canonical, []).append(argv[index + 1])
            index += 2
        else:
            index += 1
    return parsed


def project_fit_helper_arguments(server_argv: list[str]) -> list[str]:
    """Mirror LlamaFitParamsProcessRunner.BuildArguments over one production server vector."""
    projected: list[str] = []
    index = 0
    while index < len(server_argv):
        argument = server_argv[index]
        if argument in FIT_HELPER_ARGS_WITH_VALUE:
            if index + 1 >= len(server_argv):
                raise CaptureError(f"Production Explore vector ends after helper-relevant argument {argument}")
            projected.extend((argument, server_argv[index + 1]))
            index += 2
        elif argument in FIT_HELPER_VALUELESS_ARGS:
            projected.append(argument)
            index += 1
        else:
            index += 1
    return projected


def option_values(argv: list[str], option: str) -> list[str]:
    values: list[str] = []
    for index, argument in enumerate(argv):
        if argument != option:
            continue
        if index + 1 >= len(argv):
            raise CaptureError(f"Launch vector ends after {option}")
        values.append(argv[index + 1])
    return values


def validate_kv_flash_equivalence(explore_flags: dict[str, list[str]], replay_flags: dict[str, list[str]]) -> None:
    for flag in ("-ctk", "-ctv", "-fa"):
        explore_values = explore_flags.get(flag, [])
        replay_values = replay_flags.get(flag, [])
        if len(explore_values) > 1 or len(replay_values) > 1:
            raise CaptureError(f"Explore and replay must contain at most one {flag} value")
        if explore_values != replay_values:
            raise CaptureError(
                f"Explore and replay KV/flash-attention settings differ for {flag}: "
                f"explore={explore_values}, replay={replay_values}"
            )

    kv_k = explore_flags.get("-ctk", [])
    kv_v = explore_flags.get("-ctv", [])
    flash = explore_flags.get("-fa", [])
    if kv_k or kv_v or flash:
        if len(kv_k) != 1 or kv_k != kv_v or flash != ["on"]:
            raise CaptureError(
                "Optimized Explore/replay must use matching -ctk/-ctv values with flash attention set to on"
            )


def validate_concrete_fit_flags(fitted_flags: dict[str, list[str]]) -> None:
    contexts = fitted_flags.get("-c", [])
    if len(contexts) != 1:
        raise CaptureError("llama-fit-params output must contain exactly one -c value")

    gpu_layers = fitted_flags.get("-ngl", [])
    if len(gpu_layers) != 1:
        raise CaptureError("llama-fit-params output must contain exactly one -ngl value")

    try:
        context = int(contexts[0], 10)
    except ValueError as error:
        raise CaptureError("llama-fit-params -c must be a positive concrete integer") from error
    if context <= 0:
        raise CaptureError("llama-fit-params -c must be a positive concrete integer")

    try:
        placement = int(gpu_layers[0], 10)
    except ValueError as error:
        raise CaptureError("llama-fit-params -ngl must be an integer") from error
    if placement == -1:
        raise CaptureError("llama-fit-params -ngl -1 is automatic placement, not a frozen placement")
    if placement < -2:
        raise CaptureError("llama-fit-params -ngl must be -2 (all layers) or a non-negative layer count")


def normalize_fit_flags(fitted_flags: dict[str, list[str]], verbose_startup: str) -> dict[str, list[str]]:
    normalized = {key: list(values) for key, values in fitted_flags.items()}
    gpu_layers = normalized.get("-ngl", [])
    if gpu_layers == ["-1"]:
        offload_counts = [
            (int(match.group(1)), int(match.group(2)))
            for match in re.finditer(r"offloaded\s+(\d+)/(\d+)\s+layers to GPU", verbose_startup)
        ]
        if not offload_counts or any(offloaded <= 0 or offloaded != total for offloaded, total in offload_counts):
            raise CaptureError(
                "llama-fit-params -ngl -1 requires authoritative full-offload evidence before it can be normalized"
            )
        normalized["-ngl"] = ["-2"]

    validate_concrete_fit_flags(normalized)
    return normalized


def without_fit_semantics(argv: list[str]) -> list[str]:
    result: list[str] = []
    index = 0
    while index < len(argv):
        item = argv[index]
        if item == "--fit":
            index += 2 if index + 1 < len(argv) and argv[index + 1] in {"on", "off"} else 1
        elif item in FIT_FLAGS_WITH_VALUE:
            index += 2
        elif item in {"-v", "--verbose", "--metrics"}:
            # Profiling-only diagnostics do not change fit placement. Explore carries verbosity so startup can prove
            # full offload; both vectors carry --metrics, but replay appends it after role flags.
            index += 1
        else:
            result.append(item)
            index += 1
    return result


def capture_fit(spec_path: Path, output_path: Path) -> None:
    spec = load_json(spec_path)
    validate_spec(spec, "fit-replay-capture")
    binaries = spec.get("binaries")
    if not isinstance(binaries, dict):
        raise CaptureError("spec.binaries must be an object")
    server = verify_identity("spec.binaries.server", binaries.get("server", {}))
    helper = verify_identity("spec.binaries.fit_helper", binaries.get("fit_helper", {}))
    commands = spec.get("commands")
    vectors = spec.get("launch_vectors")
    if not isinstance(commands, dict) or not isinstance(vectors, dict):
        raise CaptureError("spec.commands and spec.launch_vectors must be objects")
    required = ("default_verbosity", "verbose", "fit_params", "explore", "replay")
    if any(not isinstance(commands.get(name), dict) for name in required):
        raise CaptureError("spec.commands must contain default_verbosity, verbose, fit_params, explore, and replay objects")
    default_argv = command_argv(commands["default_verbosity"], "spec.commands.default_verbosity")
    verbose_argv = command_argv(commands["verbose"], "spec.commands.verbose")
    fit_argv = command_argv(commands["fit_params"], "spec.commands.fit_params")
    if Path(default_argv[0]).resolve() != Path(server["path"]) or Path(verbose_argv[0]).resolve() != Path(server["path"]):
        raise CaptureError("default_verbosity and verbose must invoke the verified server binary")
    if Path(fit_argv[0]).resolve() != Path(helper["path"]):
        raise CaptureError("fit_params must invoke the verified fit-helper binary")
    if "-v" in default_argv or "--verbose" in default_argv:
        raise CaptureError("default_verbosity must not contain -v/--verbose")
    if strip_one_verbose(verbose_argv) != default_argv:
        raise CaptureError("verbose argv must equal default_verbosity argv plus exactly one -v/--verbose flag")
    explore = vectors.get("explore")
    replay = vectors.get("replay")
    if not isinstance(explore, list) or not isinstance(replay, list) or not all(isinstance(item, str) for item in explore + replay):
        raise CaptureError("launch_vectors.explore and replay must be string arrays")
    if option_values(explore, "--fit") != ["on"] or "--fit" in replay:
        raise CaptureError("explore must contain exactly one '--fit on' pair and replay must not contain --fit")
    if explore.count("--metrics") != 1 or replay.count("--metrics") != 1:
        raise CaptureError("production Explore and replay profiling vectors must each contain exactly one --metrics flag")
    verbose_count = sum(explore.count(flag) for flag in ("-v", "--verbose"))
    if verbose_count != 1:
        raise CaptureError("production Explore must contain exactly one -v/--verbose flag")
    if "-v" in replay or "--verbose" in replay:
        raise CaptureError("replay must not contain the profiling-only -v/--verbose flag")
    explore_argv = command_argv(commands["explore"], "spec.commands.explore")
    replay_argv = command_argv(commands["replay"], "spec.commands.replay")
    if Path(explore_argv[0]).resolve() != Path(server["path"]) or Path(replay_argv[0]).resolve() != Path(server["path"]):
        raise CaptureError("explore and replay must invoke the verified server binary")
    if explore_argv[1:] != explore or replay_argv[1:] != replay:
        raise CaptureError("launch_vectors must exactly equal the explore/replay command argv after the binary path")
    if verbose_argv[1:] != explore:
        raise CaptureError("verbose must use the exact production Explore launch vector")
    projected_helper_argv = project_fit_helper_arguments(explore)
    if fit_argv[1:] != projected_helper_argv:
        raise CaptureError(
            "fit_params argv must exactly equal the production helper projection of Explore: "
            f"expected={projected_helper_argv}, actual={fit_argv[1:]}"
        )

    default_result = run_command(commands["default_verbosity"], "spec.commands.default_verbosity")
    verbose_result = run_command(commands["verbose"], "spec.commands.verbose")
    fit_result = run_command(commands["fit_params"], "spec.commands.fit_params")
    explore_result = run_command(commands["explore"], "spec.commands.explore")
    replay_result = run_command(commands["replay"], "spec.commands.replay")
    fit_stdout = fit_result["runs"][-1]["stdout"]
    fit_line = next((line.strip() for line in reversed(fit_stdout.splitlines()) if line.strip().startswith("-c ")), "")
    if not fit_line:
        raise CaptureError("llama-fit-params output did not contain a deterministic '-c ...' argument line")
    default_text = default_result["runs"][-1]["stdout"] + "\n" + default_result["runs"][-1]["stderr"]
    verbose_text = verbose_result["runs"][-1]["stdout"] + "\n" + verbose_result["runs"][-1]["stderr"]
    explore_text = explore_result["runs"][-1]["stdout"] + "\n" + explore_result["runs"][-1]["stderr"]
    fitted = shlex.split(fit_line, posix=True)
    fitted_flags = extract_fit_flags(fitted)
    unexpected_fitted_flags = set(fitted_flags) - {"-c", "-ngl", "-ts", "-ot"}
    if unexpected_fitted_flags:
        raise CaptureError(
            "llama-fit-params stdout contains unsupported flags outside its machine-readable grammar: "
            f"{sorted(unexpected_fitted_flags)}"
        )
    normalized_fitted_flags = normalize_fit_flags(fitted_flags, explore_text)
    explore_flags = extract_fit_flags(explore)
    replay_flags = extract_fit_flags(replay)
    validate_concrete_fit_flags(replay_flags)
    if normalized_fitted_flags != {key: replay_flags.get(key, []) for key in normalized_fitted_flags}:
        raise CaptureError(
            "Replay placement differs from normalized llama-fit-params output: "
            f"fitted={fitted_flags}, normalized={normalized_fitted_flags}, replay={replay_flags}"
        )
    validate_kv_flash_equivalence(explore_flags, replay_flags)
    if without_fit_semantics(explore) != without_fit_semantics(replay):
        raise CaptureError("Explore and replay non-fit launch arguments are not byte-equivalent")
    acceptance = spec.get("resource_acceptance")
    if not isinstance(acceptance, dict):
        raise CaptureError("spec.resource_acceptance must be an object")
    tolerance = acceptance.get("max_delta_percent")
    if not isinstance(tolerance, (int, float)) or tolerance < 0:
        raise CaptureError("spec.resource_acceptance.max_delta_percent must be a non-negative number")
    explore_rss = explore_result["runs"][-1]["peak_rss_bytes"]
    replay_rss = replay_result["runs"][-1]["peak_rss_bytes"]
    rss_delta_percent = None
    rss_within_tolerance = None
    if explore_rss and replay_rss:
        rss_delta_percent = abs(replay_rss - explore_rss) / explore_rss * 100
        rss_within_tolerance = rss_delta_percent <= tolerance
        if not rss_within_tolerance:
            raise CaptureError(f"Explore/replay peak RSS delta {rss_delta_percent:.2f}% exceeds {tolerance:.2f}%")
    explore_gpu_used = global_gpu_used_mib(explore_result["runs"][-1]["ambient_during"])
    replay_gpu_used = global_gpu_used_mib(replay_result["runs"][-1]["ambient_during"])
    gpu_delta_percent = None
    gpu_within_tolerance = None
    if explore_gpu_used and replay_gpu_used:
        gpu_delta_percent = abs(replay_gpu_used - explore_gpu_used) / explore_gpu_used * 100
        gpu_within_tolerance = gpu_delta_percent <= tolerance
        if not gpu_within_tolerance:
            raise CaptureError(f"Explore/replay global GPU-used delta {gpu_delta_percent:.2f}% exceeds {tolerance:.2f}%")
    host = capture_host(Path(server["path"]))
    process_free = process_budget_free_mib(host["runtime_devices"])
    explore_global_free = global_gpu_free_mib(explore_result["runs"][-1]["ambient_during"])
    replay_global_free = global_gpu_free_mib(replay_result["runs"][-1]["ambient_during"])
    artifact = {
        "schema_version": SCHEMA_VERSION,
        "kind": "fit-replay-evidence",
        "capture_id": spec["capture_id"],
        "phase": spec["phase"],
        "completed_at_utc": utc_now(),
        "spec_sha256": sha256_file(spec_path),
        "verified_identity": {"server": server, "fit_helper": helper},
        "host": host,
        "captures": {
            "default_verbosity": default_result,
            "verbose": verbose_result,
            "fit_params": fit_result,
            "explore": explore_result,
            "replay": replay_result,
        },
        "launch_vectors": {"explore": explore, "replay": replay, "fitted_stdout_argv": fitted},
        "equivalence": {
            "fitted_flags": fitted_flags,
            "normalized_fitted_flags": normalized_fitted_flags,
            "explore_policy_flags": {
                key: explore_flags.get(key, [])
                for key in ("-ctk", "-ctv", "-fa")
            },
            "replay_flags": replay_flags,
            "non_fit_vector_equal": True,
            "placement_equal": True,
            "kv_flash_equal": True,
            "metrics_enabled_for_both": True,
            "peak_rss_delta_percent": rss_delta_percent,
            "peak_rss_within_tolerance": rss_within_tolerance,
            "global_gpu_used_delta_percent": gpu_delta_percent,
            "global_gpu_used_within_tolerance": gpu_within_tolerance,
            "resource_tolerance_percent": tolerance,
            "global_vram_samples": {
                "explore": explore_result["runs"][-1]["ambient_during"],
                "replay": replay_result["runs"][-1]["ambient_during"],
            },
            "vram_semantics": {
                "global_free_mib": {"explore": explore_global_free, "replay": replay_global_free},
                "process_budget_free_mib": process_free,
                "process_minus_global_free_mib": {
                    "explore": None if process_free is None or explore_global_free is None else process_free - explore_global_free,
                    "replay": None if process_free is None or replay_global_free is None else process_free - replay_global_free,
                },
                "interpretation": "Global free VRAM governs contention/invalidation; process-budget VRAM describes this process's WDDM/CUDA fit budget. Divergence is reported, never averaged.",
            },
            "verbosity_evidence": {
                "default_fit_detail_lines": default_text.count("common_params_fit_impl"),
                "verbose_fit_detail_lines": verbose_text.count("common_params_fit_impl"),
                "helper_stdout_argv": fitted,
            },
        },
        "coverage": spec.get("coverage", {}),
    }
    write_json(output_path, artifact)


def identity_projection(artifact: dict[str, Any]) -> dict[str, Any]:
    identity = artifact.get("verified_identity", {})
    host = artifact.get("host", {})
    return {
        "models": [{key: item.get(key) for key in ("name", "role", "quant", "sha256")} for item in identity.get("models", [])],
        "corpus": {key: identity.get("corpus", {}).get(key) for key in ("name", "sha256")},
        "runtime": {
            **{key: identity.get("runtime", {}).get(key) for key in ("tag", "provenance", "backend", "sha256")},
            "dependency_manifest_sha256": (identity.get("runtime", {}).get("runtime_local_dependencies") or {}).get("manifest_sha256"),
            "auxiliary_binaries": [
                {
                    **{key: item.get(key) for key in ("name", "sha256")},
                    "dependency_manifest_sha256": (item.get("runtime_local_dependencies") or {}).get("manifest_sha256"),
                }
                for item in identity.get("runtime", {}).get("auxiliary_binaries", [])
            ],
        },
        "machine": {key: host.get(key) for key in ("os", "kernel", "architecture", "cpu", "logical_cpu_count")},
        "devices": host.get("runtime_devices"),
    }


def compare_artifacts(baseline_path: Path, candidate_path: Path, output_path: Path) -> None:
    baseline = load_json(baseline_path)
    candidate = load_json(candidate_path)
    if baseline.get("kind") != "inference-benchmark-evidence" or candidate.get("kind") != "inference-benchmark-evidence":
        raise CaptureError("compare requires two inference-benchmark-evidence artifacts")
    baseline_identity = identity_projection(baseline)
    candidate_identity = identity_projection(candidate)
    mismatches = [key for key in baseline_identity if baseline_identity[key] != candidate_identity[key]]
    if mismatches:
        raise CaptureError("Artifacts are not comparable; identity differs in: " + ", ".join(mismatches))
    base_commands = {item["name"]: item for item in baseline.get("commands", [])}
    candidate_commands = {item["name"]: item for item in candidate.get("commands", [])}
    if set(base_commands) != set(candidate_commands):
        raise CaptureError("Artifacts do not contain the same command names")
    comparison: dict[str, Any] = {}
    for name in sorted(base_commands):
        if base_commands[name].get("argv_sha256") != candidate_commands[name].get("argv_sha256"):
            raise CaptureError(f"Command {name!r} used a different argv vector")
        metrics: dict[str, Any] = {}
        base_metrics = base_commands[name].get("aggregates", {})
        candidate_metrics = candidate_commands[name].get("aggregates", {})
        for metric in sorted(set(base_metrics) & set(candidate_metrics)):
            old = base_metrics[metric]["median"]
            new = candidate_metrics[metric]["median"]
            metrics[metric] = {"baseline_median": old, "candidate_median": new, "delta_percent": None if old == 0 else ((new - old) / old) * 100}
        comparison[name] = metrics
    write_json(output_path, {
        "schema_version": SCHEMA_VERSION,
        "kind": "inference-benchmark-comparison",
        "baseline": str(baseline_path.resolve()),
        "candidate": str(candidate_path.resolve()),
        "identity_equal": True,
        "commands": comparison,
        "generated_at_utc": utc_now(),
    })


def sanitize_artifact(input_path: Path, output_path: Path, replacements: list[str]) -> None:
    artifact = load_json(input_path)
    parsed: list[tuple[str, str]] = []
    for replacement in replacements:
        source, separator, label = replacement.partition("=")
        if not separator or not source or not label:
            raise CaptureError("--replace entries must use ABSOLUTE_PATH=$LABEL syntax")
        if not Path(source).is_absolute() or not label.startswith("$"):
            raise CaptureError("--replace source must be absolute and label must start with '$'")
        parsed.append((str(Path(source).resolve()), label))
    parsed.sort(key=lambda item: len(item[0]), reverse=True)

    def scrub(value: Any) -> Any:
        if isinstance(value, dict):
            return {key: scrub(item) for key, item in value.items()}
        if isinstance(value, list):
            return [scrub(item) for item in value]
        if isinstance(value, str):
            result = value
            for source, label in parsed:
                result = result.replace(source, label)
            return result
        return value

    sanitized = scrub(artifact)
    serialized = json.dumps(sanitized, sort_keys=True)
    leaked = re.search(r"(?:/home/[^/\\s]+|[A-Za-z]:\\\\Users\\\\[^\\\\\\s]+)", serialized)
    if leaked:
        raise CaptureError(f"Sanitized artifact still contains a user-home path near {leaked.group(0)!r}; add --replace")
    sanitized["sanitized"] = True
    sanitized["sanitization"] = {"replacements": [{"label": label} for _, label in parsed], "source_sha256": sha256_file(input_path)}
    write_json(output_path, sanitized)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    for name in ("baseline", "fit"):
        command = subparsers.add_parser(name)
        command.add_argument("--spec", type=Path, required=True)
        command.add_argument("--output", type=Path, required=True)
    compare = subparsers.add_parser("compare")
    compare.add_argument("--baseline", type=Path, required=True)
    compare.add_argument("--candidate", type=Path, required=True)
    compare.add_argument("--output", type=Path, required=True)
    sanitize = subparsers.add_parser("sanitize")
    sanitize.add_argument("--input", type=Path, required=True)
    sanitize.add_argument("--output", type=Path, required=True)
    sanitize.add_argument("--replace", action="append", default=[])
    return parser


def main() -> int:
    args = build_parser().parse_args()
    try:
        if args.command == "baseline":
            capture_baseline(args.spec.resolve(), args.output.resolve())
        elif args.command == "fit":
            capture_fit(args.spec.resolve(), args.output.resolve())
        elif args.command == "compare":
            compare_artifacts(args.baseline.resolve(), args.candidate.resolve(), args.output.resolve())
        else:
            sanitize_artifact(args.input.resolve(), args.output.resolve(), args.replace)
    except CaptureError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2
    print(f"Wrote {args.output.resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
