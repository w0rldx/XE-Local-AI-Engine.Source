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
import importlib
import importlib.util
import os  # Re-exported for compatibility with direct Python consumers.
import sys
from pathlib import Path
from types import ModuleType
from typing import TYPE_CHECKING, Any, cast


def _load_sibling_implementation() -> tuple[ModuleType, ModuleType]:
    package_directory = Path(__file__).resolve().with_name("inference_evidence")
    package_init = package_directory / "__init__.py"
    path_digest = hashlib.sha256(os.fsencode(package_directory)).hexdigest()
    package_name = f"_xe_capture_inference_evidence_{path_digest}"
    implementation = sys.modules.get(package_name)
    if implementation is None:
        spec = importlib.util.spec_from_file_location(
            package_name,
            package_init,
            submodule_search_locations=[os.fspath(package_directory)],
        )
        if spec is None or spec.loader is None:
            raise ImportError(f"Could not load inference evidence implementation from {package_init}")
        implementation = importlib.util.module_from_spec(spec)
        sys.modules[package_name] = implementation
        try:
            spec.loader.exec_module(implementation)
        except BaseException:
            sys.modules.pop(package_name, None)
            raise
    else:
        loaded_path = Path(cast(str, implementation.__file__)).resolve()
        if loaded_path != package_init:
            raise ImportError(f"Private inference evidence package collision for {package_directory}")
    process = importlib.import_module(f"{package_name}.process")
    return implementation, process


def _is_qualified_package_import() -> bool:
    if __package__ is None:
        return False
    parent = sys.modules.get(__package__)
    search_locations = getattr(parent, "__path__", ())
    facade_directory = Path(__file__).resolve().parent
    return any(Path(location).resolve() == facade_directory for location in search_locations)


if TYPE_CHECKING or _is_qualified_package_import():
    from . import inference_evidence as _implementation
    from .inference_evidence import process as _process
else:
    _implementation, _process = _load_sibling_implementation()

SCHEMA_VERSION = _implementation.SCHEMA_VERSION
MAX_CAPTURE_STREAM_BYTES = _implementation.MAX_CAPTURE_STREAM_BYTES
PROCESS_CLEANUP_TIMEOUT_SECONDS = _implementation.PROCESS_CLEANUP_TIMEOUT_SECONDS
UUID_CORE_PATTERN = _implementation.UUID_CORE_PATTERN
GPU_UUID_PATTERN = _implementation.GPU_UUID_PATTERN
FIT_FLAGS_WITH_VALUE = _implementation.FIT_FLAGS_WITH_VALUE
FIT_FLAG_CANONICAL = _implementation.FIT_FLAG_CANONICAL
FIT_HELPER_ARGS_WITH_VALUE = _implementation.FIT_HELPER_ARGS_WITH_VALUE
FIT_HELPER_VALUELESS_ARGS = _implementation.FIT_HELPER_VALUELESS_ARGS
FRAMEWORK_COMMAND_PARTITIONS = _implementation.FRAMEWORK_COMMAND_PARTITIONS
FRAMEWORK_PACKAGE_NAMES = _implementation.FRAMEWORK_PACKAGE_NAMES
POLICY_SCHEMA_VERSION = _implementation.POLICY_SCHEMA_VERSION
POLICY_FIELDS = _implementation.POLICY_FIELDS
POLICY_REQUIRED_FIELDS = _implementation.POLICY_REQUIRED_FIELDS
POLICY_RULE_FIELDS = _implementation.POLICY_RULE_FIELDS
POLICY_SAFE_TOKEN_PATTERN = _implementation.POLICY_SAFE_TOKEN_PATTERN
POLICY_SAFE_TOKEN_MAX_LENGTH = _implementation.POLICY_SAFE_TOKEN_MAX_LENGTH
ALLOWED_IDENTITY_CHANGES = _implementation.ALLOWED_IDENTITY_CHANGES
POLICY_STATISTICS = _implementation.POLICY_STATISTICS
POLICY_RULE_KINDS = _implementation.POLICY_RULE_KINDS
CaptureError = _implementation.CaptureError
utc_now = _implementation.utc_now
load_json = _implementation.load_json
write_json = _implementation.write_json
write_json_atomic = _implementation.write_json_atomic
sha256_file = _implementation.sha256_file
sha256_tree = _implementation.sha256_tree
require_string = _implementation.require_string
is_finite_number = _implementation.is_finite_number
is_sha256 = _implementation.is_sha256
is_safe_policy_token = _implementation.is_safe_policy_token
validate_spec = _implementation.validate_spec
capture_text = _implementation.capture_text
global_gpu_used_mib = _implementation.global_gpu_used_mib
global_gpu_free_mib = _implementation.global_gpu_free_mib
process_budget_free_mib = _implementation.process_budget_free_mib
command_argv = _implementation.command_argv
numeric_metrics = _implementation.numeric_metrics
percentile95 = _implementation.percentile95
BoundedStreamCapture = _implementation.BoundedStreamCapture
drain_capture_stream = _implementation.drain_capture_stream
cleanup_process_group = _implementation.cleanup_process_group
run_once = _implementation.run_once
signal_process_group = _implementation.signal_process_group
run_command = _implementation.run_command
verify_identity = _implementation.verify_identity
runtime_local_dependencies = _implementation.runtime_local_dependencies
git_text = _implementation.git_text
central_package_versions = _implementation.central_package_versions
is_relative_to = _implementation.is_relative_to
verify_framework_identity = _implementation.verify_framework_identity
identity_projection = _implementation.identity_projection
framework_identity_projection = _implementation.framework_identity_projection
verified_runtime_identity_projection = _implementation.verified_runtime_identity_projection
verified_framework_identity_projection = _implementation.verified_framework_identity_projection
immutable_gate_identity_projection = _implementation.immutable_gate_identity_projection
capture_baseline = _implementation.capture_baseline
strip_one_verbose = _implementation.strip_one_verbose
extract_fit_flags = _implementation.extract_fit_flags
project_fit_helper_arguments = _implementation.project_fit_helper_arguments
option_values = _implementation.option_values
validate_kv_flash_equivalence = _implementation.validate_kv_flash_equivalence
validate_concrete_fit_flags = _implementation.validate_concrete_fit_flags
normalize_fit_flags = _implementation.normalize_fit_flags
without_fit_semantics = _implementation.without_fit_semantics
capture_fit = _implementation.capture_fit
compare_artifacts = _implementation.compare_artifacts
artifact_commands = _implementation.artifact_commands
validate_policy = _implementation.validate_policy
gate_identity = _implementation.gate_identity
unevaluable_rule = _implementation.unevaluable_rule
evaluate_policy_rule = _implementation.evaluate_policy_rule
gate_artifacts = _implementation.gate_artifacts
sanitize_artifact = _implementation.sanitize_artifact


__all__ = [
    "SCHEMA_VERSION",
    "MAX_CAPTURE_STREAM_BYTES",
    "PROCESS_CLEANUP_TIMEOUT_SECONDS",
    "UUID_CORE_PATTERN",
    "GPU_UUID_PATTERN",
    "FIT_FLAGS_WITH_VALUE",
    "FIT_FLAG_CANONICAL",
    "FIT_HELPER_ARGS_WITH_VALUE",
    "FIT_HELPER_VALUELESS_ARGS",
    "FRAMEWORK_COMMAND_PARTITIONS",
    "FRAMEWORK_PACKAGE_NAMES",
    "POLICY_SCHEMA_VERSION",
    "POLICY_FIELDS",
    "POLICY_REQUIRED_FIELDS",
    "POLICY_RULE_FIELDS",
    "POLICY_SAFE_TOKEN_PATTERN",
    "POLICY_SAFE_TOKEN_MAX_LENGTH",
    "ALLOWED_IDENTITY_CHANGES",
    "POLICY_STATISTICS",
    "POLICY_RULE_KINDS",
    "CaptureError",
    "utc_now",
    "load_json",
    "write_json",
    "write_json_atomic",
    "sha256_file",
    "sha256_tree",
    "require_string",
    "is_finite_number",
    "is_sha256",
    "is_safe_policy_token",
    "verify_identity",
    "runtime_local_dependencies",
    "capture_text",
    "git_text",
    "central_package_versions",
    "is_relative_to",
    "verify_framework_identity",
    "capture_ambient",
    "global_gpu_used_mib",
    "global_gpu_free_mib",
    "process_budget_free_mib",
    "capture_host",
    "validate_spec",
    "command_argv",
    "numeric_metrics",
    "percentile95",
    "BoundedStreamCapture",
    "drain_capture_stream",
    "cleanup_process_group",
    "run_once",
    "signal_process_group",
    "run_command",
    "capture_baseline",
    "strip_one_verbose",
    "extract_fit_flags",
    "project_fit_helper_arguments",
    "option_values",
    "validate_kv_flash_equivalence",
    "validate_concrete_fit_flags",
    "normalize_fit_flags",
    "without_fit_semantics",
    "capture_fit",
    "identity_projection",
    "framework_identity_projection",
    "verified_runtime_identity_projection",
    "verified_framework_identity_projection",
    "immutable_gate_identity_projection",
    "artifact_commands",
    "validate_policy",
    "gate_identity",
    "unevaluable_rule",
    "evaluate_policy_rule",
    "gate_artifacts",
    "compare_artifacts",
    "sanitize_artifact",
    "build_parser",
    "main",
]


def capture_ambient() -> dict[str, Any]:
    """Preserve the facade's historical ``capture_text`` monkeypatch seam."""
    return _process.capture_ambient(capture_text)


def capture_host(runtime_binary: Path) -> dict[str, Any]:
    """Preserve the facade's historical ``capture_text`` monkeypatch seam."""
    return _process.capture_host(runtime_binary, capture_text)


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
    gate = subparsers.add_parser("gate")
    gate.add_argument("--baseline", type=Path, required=True)
    gate.add_argument("--candidate", type=Path, required=True)
    gate.add_argument("--policy", type=Path, required=True)
    gate.add_argument("--output", type=Path, required=True)
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
        elif args.command == "gate":
            return gate_artifacts(
                args.baseline.resolve(),
                args.candidate.resolve(),
                args.policy.resolve(),
                args.output.resolve(),
            )
        else:
            sanitize_artifact(args.input.resolve(), args.output.resolve(), args.replace)
    except CaptureError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2
    print(f"Wrote {args.output.resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
