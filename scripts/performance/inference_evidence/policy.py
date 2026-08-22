"""Comparison policy validation and fail-closed verdict generation."""

from __future__ import annotations

import json
from decimal import Decimal, InvalidOperation
from fractions import Fraction
from pathlib import Path
from typing import Any

from .contracts import (
    ALLOWED_IDENTITY_CHANGES,
    POLICY_FIELDS,
    POLICY_REQUIRED_FIELDS,
    POLICY_RULE_FIELDS,
    POLICY_RULE_KINDS,
    POLICY_SCHEMA_VERSION,
    POLICY_STATISTICS,
    SCHEMA_VERSION,
    CaptureError,
    is_finite_number,
    is_safe_policy_token,
    load_json,
    sha256_file,
    write_json_atomic,
)
from .identity import (
    immutable_gate_identity_projection,
    verified_framework_identity_projection,
    verified_runtime_identity_projection,
)


def artifact_commands(artifact: dict[str, Any], side: str) -> tuple[list[dict[str, Any]] | None, dict[str, Any] | None]:
    commands = artifact.get("commands")
    if not isinstance(commands, list) or not commands:
        return None, {"reason": "artifact.malformed", "side": side}
    names: set[str] = set()
    checked: list[dict[str, Any]] = []
    for command in commands:
        if not isinstance(command, dict):
            return None, {"reason": "artifact.malformed", "side": side}
        name = command.get("name")
        if not isinstance(name, str) or not name.strip():
            return None, {"reason": "artifact.malformed", "side": side}
        argv_sha256 = command.get("argv_sha256")
        if (
            not isinstance(argv_sha256, str)
            or len(argv_sha256) != 64
            or any(character not in "0123456789abcdef" for character in argv_sha256)
        ):
            return None, {"reason": "artifact.malformed", "side": side}
        if name in names:
            return None, {"reason": "artifact.duplicate_command_name", "side": side}
        names.add(name)
        checked.append(command)
    return checked, None


def validate_policy(policy: dict[str, Any]) -> tuple[str | None, list[str] | None, list[dict[str, Any]] | None]:
    policy_id = policy.get("policy_id") if is_safe_policy_token(policy.get("policy_id")) else None
    if not POLICY_REQUIRED_FIELDS.issubset(policy) or not set(policy).issubset(POLICY_FIELDS):
        return policy_id, None, None
    if policy.get("schema_version") != POLICY_SCHEMA_VERSION:
        return policy_id, None, None
    if policy_id is None:
        return None, None, None
    allowed = policy.get("allowed_identity_changes", [])
    if not isinstance(allowed, list) or any(not isinstance(item, str) for item in allowed):
        return policy_id, None, None
    if len(set(allowed)) != len(allowed) or any(item not in ALLOWED_IDENTITY_CHANGES for item in allowed):
        return policy_id, None, None
    rules = policy.get("rules")
    if not isinstance(rules, list) or not rules:
        return policy_id, None, None
    rule_ids: set[str] = set()
    checked_rules: list[dict[str, Any]] = []
    for rule in rules:
        if not isinstance(rule, dict) or set(rule) != POLICY_RULE_FIELDS:
            return policy_id, None, None
        rule_id = rule.get("id")
        threshold = rule.get("threshold_percent")
        if (
            not is_safe_policy_token(rule_id)
            or rule_id in rule_ids
            or not is_safe_policy_token(rule.get("command"))
            or not is_safe_policy_token(rule.get("metric"))
            or rule.get("statistic") not in POLICY_STATISTICS
            or rule.get("kind") not in POLICY_RULE_KINDS
            or not is_finite_number(threshold)
            or threshold < 0
        ):
            return policy_id, None, None
        rule_ids.add(rule_id)
        checked_rules.append(rule)
    return policy_id, allowed, checked_rules


def gate_identity(
    baseline: dict[str, Any],
    candidate: dict[str, Any],
    allowed_changes: list[str],
) -> dict[str, Any]:
    try:
        baseline_framework = verified_framework_identity_projection(baseline)
        candidate_framework = verified_framework_identity_projection(candidate)
        baseline_runtime = verified_runtime_identity_projection(baseline)
        candidate_runtime = verified_runtime_identity_projection(candidate)
        baseline_identity = immutable_gate_identity_projection(baseline)
        candidate_identity = immutable_gate_identity_projection(candidate)
    except (CaptureError, AttributeError, KeyError, TypeError):
        return {
            "status": "unevaluable",
            "reason": "identity.unverified",
            "allowed_changes": allowed_changes,
            "changed_dimensions": [],
        }

    projections = {
        "models": (baseline_identity["models"], candidate_identity["models"]),
        "corpus": (baseline_identity["corpus"], candidate_identity["corpus"]),
        "runtime": (baseline_runtime, candidate_runtime),
        "framework": (baseline_framework, candidate_framework),
        "machine": (baseline_identity["machine"], candidate_identity["machine"]),
        "devices": (baseline_identity["devices"], candidate_identity["devices"]),
    }
    changed = [name for name, (before, after) in projections.items() if before != after]
    undeclared = [name for name in changed if name not in allowed_changes]
    if undeclared:
        return {
            "status": "unevaluable",
            "reason": "identity.undeclared_mismatch",
            "allowed_changes": allowed_changes,
            "changed_dimensions": changed,
        }
    return {
        "status": "passed",
        "reason": "identity.declared_comparable" if changed else "identity.matched",
        "allowed_changes": allowed_changes,
        "changed_dimensions": changed,
    }


def unevaluable_rule(rule: dict[str, Any], reason: str) -> dict[str, Any]:
    return {
        "id": rule["id"],
        "command": rule["command"],
        "metric": rule["metric"],
        "statistic": rule["statistic"],
        "kind": rule["kind"],
        "threshold_percent": rule["threshold_percent"],
        "reason": reason,
        "passed": False,
    }


def evaluate_policy_rule(
    rule: dict[str, Any],
    baseline_commands: dict[str, dict[str, Any]],
    candidate_commands: dict[str, dict[str, Any]],
) -> dict[str, Any]:
    name = rule["command"]
    if name not in baseline_commands or name not in candidate_commands:
        return unevaluable_rule(rule, "rule.command_missing")
    baseline_command = baseline_commands[name]
    candidate_command = candidate_commands[name]
    if baseline_command.get("argv_sha256") != candidate_command.get("argv_sha256"):
        return unevaluable_rule(rule, "identity.undeclared_mismatch")
    baseline_aggregates = baseline_command.get("aggregates")
    candidate_aggregates = candidate_command.get("aggregates")
    if not isinstance(baseline_aggregates, dict) or not isinstance(candidate_aggregates, dict):
        return unevaluable_rule(rule, "rule.metric_missing")
    metric = rule["metric"]
    if metric not in baseline_aggregates or metric not in candidate_aggregates:
        return unevaluable_rule(rule, "rule.metric_missing")
    baseline_metric = baseline_aggregates[metric]
    candidate_metric = candidate_aggregates[metric]
    if not isinstance(baseline_metric, dict) or not isinstance(candidate_metric, dict):
        return unevaluable_rule(rule, "rule.statistic_missing")
    statistic = rule["statistic"]
    if statistic not in baseline_metric or statistic not in candidate_metric:
        return unevaluable_rule(rule, "rule.statistic_missing")
    baseline_value = baseline_metric[statistic]
    candidate_value = candidate_metric[statistic]
    if (
        isinstance(baseline_value, bool)
        or isinstance(candidate_value, bool)
        or not isinstance(baseline_value, (int, float))
        or not isinstance(candidate_value, (int, float))
    ):
        return unevaluable_rule(rule, "rule.value_non_finite")
    if not is_finite_number(baseline_value) or not is_finite_number(candidate_value):
        return unevaluable_rule(rule, "rule.value_non_finite")
    if baseline_value <= 0 or candidate_value <= 0:
        return unevaluable_rule(rule, "rule.value_zero_or_negative")
    try:
        baseline_fraction = Fraction(Decimal(str(baseline_value)))
        candidate_fraction = Fraction(Decimal(str(candidate_value)))
        threshold_fraction = Fraction(Decimal(str(rule["threshold_percent"])))
        boundary_left = candidate_fraction * 100
        boundary_right = baseline_fraction * (100 + threshold_fraction)
        delta_fraction = ((candidate_fraction / baseline_fraction) - 1) * 100
    except (InvalidOperation, OverflowError, ZeroDivisionError):
        return unevaluable_rule(rule, "rule.value_non_finite")
    threshold = rule["threshold_percent"]
    passed = (
        boundary_left >= boundary_right
        if rule["kind"] == "minimum_improvement_percent"
        else boundary_left <= boundary_right
    )
    try:
        delta: int | float = delta_fraction.numerator if delta_fraction.denominator == 1 else float(delta_fraction)
        json.dumps(delta, allow_nan=False)
    except (OverflowError, TypeError, ValueError):
        return unevaluable_rule(rule, "rule.value_non_finite")
    if not is_finite_number(delta):
        return unevaluable_rule(rule, "rule.value_non_finite")
    return {
        "id": rule["id"],
        "command": name,
        "metric": metric,
        "statistic": statistic,
        "kind": rule["kind"],
        "threshold_percent": threshold,
        "baseline_value": baseline_value,
        "candidate_value": candidate_value,
        "delta_percent": delta,
        "reason": "rule.passed" if passed else "rule.threshold_rejected",
        "passed": passed,
    }


def gate_artifacts(
    baseline_path: Path,
    candidate_path: Path,
    policy_path: Path,
    output_path: Path,
) -> int:
    hashes: dict[str, str] = {}
    for name, path in (
        ("baseline_sha256", baseline_path),
        ("candidate_sha256", candidate_path),
        ("policy_sha256", policy_path),
    ):
        try:
            hashes[name] = sha256_file(path)
        except OSError as exc:
            raise CaptureError("Could not read and hash all gate inputs") from exc

    policy_id: str | None = None
    identity = {
        "status": "unevaluable",
        "reason": "policy.malformed",
        "allowed_changes": [],
        "changed_dimensions": [],
    }
    rule_results: list[dict[str, Any]] = []
    exit_code = 2

    def load_gate_input(path: Path) -> dict[str, Any] | None:
        try:
            return load_json(path)
        except CaptureError:
            return None

    baseline = load_gate_input(baseline_path)
    candidate = load_gate_input(candidate_path)
    policy = load_gate_input(policy_path)

    if isinstance(policy, dict):
        candidate_policy_id = policy.get("policy_id")
        policy_id = candidate_policy_id if isinstance(candidate_policy_id, str) else None
        policy_id, allowed_changes, rules = validate_policy(policy)
    else:
        allowed_changes = rules = None

    if allowed_changes is not None and rules is not None:
        if not isinstance(baseline, dict) or not isinstance(candidate, dict):
            invalid_side = "baseline" if not isinstance(baseline, dict) else "candidate"
            identity = {
                "status": "unevaluable",
                "reason": "artifact.malformed",
                "side": invalid_side,
                "allowed_changes": allowed_changes,
                "changed_dimensions": [],
            }
            rule_results = [unevaluable_rule(rule, "artifact.malformed") for rule in rules]
        elif (
            baseline.get("schema_version") != SCHEMA_VERSION
            or candidate.get("schema_version") != SCHEMA_VERSION
            or baseline.get("kind") != "inference-benchmark-evidence"
            or candidate.get("kind") != "inference-benchmark-evidence"
        ):
            identity = {
                "status": "unevaluable",
                "reason": "artifact.malformed",
                "allowed_changes": allowed_changes,
                "changed_dimensions": [],
            }
            rule_results = [unevaluable_rule(rule, "artifact.malformed") for rule in rules]
        else:
            baseline_command_list, baseline_error = artifact_commands(baseline, "baseline")
            candidate_command_list, candidate_error = artifact_commands(candidate, "candidate")
            artifact_error = baseline_error or candidate_error
            if artifact_error is not None:
                identity = {
                    "status": "unevaluable",
                    **artifact_error,
                    "allowed_changes": allowed_changes,
                    "changed_dimensions": [],
                }
                rule_results = [unevaluable_rule(rule, artifact_error["reason"]) for rule in rules]
            else:
                if baseline_command_list is None or candidate_command_list is None:
                    raise CaptureError("Gate command lists went missing without an artifact error")
                baseline_commands = {item["name"]: item for item in baseline_command_list}
                candidate_commands = {item["name"]: item for item in candidate_command_list}
                baseline_names = set(baseline_commands)
                candidate_names = set(candidate_commands)
                command_names_differ = baseline_names != candidate_names
                command_argv_differ = not command_names_differ and any(
                    baseline_commands[name]["argv_sha256"] != candidate_commands[name]["argv_sha256"]
                    for name in baseline_names
                )
                if command_names_differ or command_argv_differ:
                    identity = {
                        "status": "unevaluable",
                        "reason": "identity.undeclared_mismatch",
                        "allowed_changes": allowed_changes,
                        "changed_dimensions": ["command_names" if command_names_differ else "command_argv"],
                    }
                    rule_results = []
                    for rule in rules:
                        referenced_command_missing = (
                            rule["command"] not in baseline_commands or rule["command"] not in candidate_commands
                        )
                        rule_results.append(
                            unevaluable_rule(
                                rule,
                                "rule.command_missing"
                                if referenced_command_missing
                                else "identity.undeclared_mismatch",
                            )
                        )
                else:
                    identity = gate_identity(baseline, candidate, allowed_changes)
                    if identity["status"] != "passed":
                        rule_results = [unevaluable_rule(rule, identity["reason"]) for rule in rules]
                    else:
                        rule_results = [
                            evaluate_policy_rule(rule, baseline_commands, candidate_commands) for rule in rules
                        ]
                        if any(
                            result["reason"] not in {"rule.passed", "rule.threshold_rejected"}
                            for result in rule_results
                        ):
                            exit_code = 2
                        elif any(not result["passed"] for result in rule_results):
                            exit_code = 3
                        else:
                            exit_code = 0

    status = "passed" if exit_code == 0 else "rejected" if exit_code == 3 else "unevaluable"
    verdict = {
        "schema_version": POLICY_SCHEMA_VERSION,
        "kind": "inference-comparison-verdict",
        "policy_id": policy_id,
        "status": status,
        "passed": exit_code == 0,
        "identity": identity,
        "rules": rule_results,
        "hashes": hashes,
    }
    write_json_atomic(output_path, verdict)
    return exit_code
