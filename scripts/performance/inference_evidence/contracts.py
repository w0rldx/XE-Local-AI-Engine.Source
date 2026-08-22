"""Shared contracts and leaf utilities for inference evidence tooling."""

from __future__ import annotations

import contextlib
import hashlib
import json
import os
import re
import tempfile
from datetime import UTC, datetime
from decimal import Decimal, InvalidOperation
from pathlib import Path
from typing import Any, TypeGuard

SCHEMA_VERSION = "1.0"
MAX_CAPTURE_STREAM_BYTES = 256 * 1024
PROCESS_CLEANUP_TIMEOUT_SECONDS = 5.0
UUID_CORE_PATTERN = r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}"
GPU_UUID_PATTERN = re.compile(
    rf"\b(?:MIG-GPU-{UUID_CORE_PATTERN}/\d+/\d+|MIG-{UUID_CORE_PATTERN}|GPU-{UUID_CORE_PATTERN})\b",
    re.IGNORECASE,
)
FIT_FLAGS_WITH_VALUE = (
    "-c",
    "--ctx-size",
    "-ngl",
    "--gpu-layers",
    "--n-gpu-layers",
    "-ts",
    "--tensor-split",
    "-ot",
    "--override-tensor",
    "-ctk",
    "--cache-type-k",
    "-ctv",
    "--cache-type-v",
    "-fa",
    "--flash-attn",
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
    "-m",
    "--model",
    "-c",
    "--ctx-size",
    "-b",
    "--batch-size",
    "-ub",
    "--ubatch-size",
    "-np",
    "--parallel",
    "-fa",
    "--flash-attn",
    "-ctk",
    "--cache-type-k",
    "-ctv",
    "--cache-type-v",
    "-dev",
    "--device",
    "-sm",
    "--split-mode",
    "-mg",
    "--main-gpu",
    "-fit",
    "--fit",
    "-fitt",
    "--fit-target",
    "-fitc",
    "--fit-ctx",
}
FIT_HELPER_VALUELESS_ARGS = {"--mlock", "--mmap", "--no-mmap", "--no-host", "--no-op-offload"}
FRAMEWORK_COMMAND_PARTITIONS = {"framework-contract", "application-harness", "provider-contract"}
FRAMEWORK_PACKAGE_NAMES = {
    "maf_version": "Microsoft.Agents.AI",
    "meai_version": "Microsoft.Extensions.AI",
    "openai_version": "OpenAI",
    "mcp_version": "ModelContextProtocol",
}
POLICY_SCHEMA_VERSION = "1.0"
POLICY_FIELDS = {"schema_version", "policy_id", "allowed_identity_changes", "rules"}
POLICY_REQUIRED_FIELDS = {"schema_version", "policy_id", "rules"}
POLICY_RULE_FIELDS = {"id", "command", "metric", "statistic", "kind", "threshold_percent"}
POLICY_SAFE_TOKEN_PATTERN = re.compile(r"^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*$")
POLICY_SAFE_TOKEN_MAX_LENGTH = 128
ALLOWED_IDENTITY_CHANGES = {"framework", "runtime"}
POLICY_STATISTICS = {"median", "p95"}
POLICY_RULE_KINDS = {"minimum_improvement_percent", "maximum_regression_percent"}


class CaptureError(RuntimeError):
    pass


def utc_now() -> str:
    return datetime.now(UTC).isoformat(timespec="seconds").replace("+00:00", "Z")


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


def write_json_atomic(path: Path, value: Any) -> None:
    """Durably replace one JSON verdict without exposing a partially written file."""
    temporary_path: Path | None = None
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile(
            mode="w",
            encoding="utf-8",
            dir=path.parent,
            prefix=f".{path.name}.",
            suffix=".tmp",
            delete=False,
        ) as stream:
            temporary_path = Path(stream.name)
            json.dump(value, stream, indent=2, sort_keys=True, allow_nan=False)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary_path, path)
        temporary_path = None
    except (OSError, TypeError, ValueError) as exc:
        raise CaptureError("Could not write gate verdict to the requested destination") from exc
    finally:
        if temporary_path is not None:
            with contextlib.suppress(OSError):
                temporary_path.unlink(missing_ok=True)


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


def is_finite_number(value: Any) -> TypeGuard[int | float]:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return False
    try:
        return Decimal(str(value)).is_finite()
    except (InvalidOperation, OverflowError, ValueError):
        return False


def is_sha256(value: Any) -> bool:
    return isinstance(value, str) and len(value) == 64 and all(character in "0123456789abcdef" for character in value)


def is_safe_policy_token(value: Any) -> TypeGuard[str]:
    return (
        isinstance(value, str)
        and len(value) <= POLICY_SAFE_TOKEN_MAX_LENGTH
        and POLICY_SAFE_TOKEN_PATTERN.fullmatch(value) is not None
    )


def validate_spec(spec: dict[str, Any], expected_kind: str) -> None:
    if spec.get("schema_version") != SCHEMA_VERSION:
        raise CaptureError(f"schema_version must be {SCHEMA_VERSION!r}")
    if spec.get("kind") != expected_kind:
        raise CaptureError(f"kind must be {expected_kind!r}")
    require_string(spec, "capture_id", "spec")
    phase = require_string(spec, "phase", "spec")
    if phase not in {"baseline", "rebaseline", "fit-proof", "experiment"}:
        raise CaptureError("spec.phase must be baseline, rebaseline, fit-proof, or experiment")
