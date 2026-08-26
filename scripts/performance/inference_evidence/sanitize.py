"""Evidence artifact sanitization."""

from __future__ import annotations

import json
import re
from pathlib import Path
from typing import Any

from .contracts import GPU_UUID_PATTERN, CaptureError, load_json, sha256_file, write_json


def host_replacements(artifact: Any) -> list[tuple[str, str]]:
    """Redaction pairs for host identity the capture reads off the machine.

    ``capture_host`` records ``platform``/``/proc/cpuinfo`` values verbatim, which name the
    operator's exact CPU model and kernel build. Those identify the machine without adding
    anything a benchmark reader needs, so they are redacted alongside paths and GPU UUIDs.
    GPU name, VRAM, and driver are deliberately kept: they are benchmark metadata.
    """
    host = artifact.get("host") if isinstance(artifact, dict) else None
    if not isinstance(host, dict):
        return []
    pairs: list[tuple[str, str]] = []
    cpu = host.get("cpu")
    if isinstance(cpu, str) and cpu.strip() and cpu != "unknown":
        pairs.append((cpu, "<redacted-cpu>"))
    kernel = host.get("kernel")
    if isinstance(kernel, str) and kernel.strip():
        pairs.append((kernel, "<redacted-kernel>"))
        operating_system = host.get("os")
        architecture = host.get("architecture")
        if isinstance(operating_system, str) and kernel in operating_system:
            family = operating_system.partition("-")[0] or "unknown"
            machine = architecture if isinstance(architecture, str) and architecture else "unknown"
            pairs.append((operating_system, f"{family}-<redacted-kernel>-{machine}"))
    return pairs


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
    parsed.extend(host_replacements(artifact))
    # Longest first so the full OS string is redacted before its embedded kernel substring.
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
            return GPU_UUID_PATTERN.sub("<gpu-uuid>", result)
        return value

    sanitized = scrub(artifact)
    serialized = json.dumps(sanitized, sort_keys=True)
    leaked = re.search(r"(?:/home/[^/\s]+|[A-Za-z]:\\\\Users\\\\[^\\\\\\s]+)", serialized)
    if leaked:
        raise CaptureError(
            f"Sanitized artifact still contains a user-home path near {leaked.group(0)!r}; add --replace"
        )
    leaked_gpu_uuid = GPU_UUID_PATTERN.search(serialized)
    if leaked_gpu_uuid:
        raise CaptureError("Sanitized artifact still contains a GPU UUID")
    sanitized["sanitized"] = True
    sanitized["sanitization"] = {
        "replacements": [{"label": label} for _, label in parsed],
        "source_sha256": sha256_file(input_path),
    }
    write_json(output_path, sanitized)
