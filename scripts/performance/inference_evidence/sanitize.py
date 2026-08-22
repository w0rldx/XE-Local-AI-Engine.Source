"""Evidence artifact sanitization."""

from __future__ import annotations

import json
import re
from pathlib import Path
from typing import Any

from .contracts import GPU_UUID_PATTERN, CaptureError, load_json, sha256_file, write_json


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
