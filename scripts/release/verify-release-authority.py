#!/usr/bin/env python3
"""Fail-closed structural gate for the release authority register.

This checks repository evidence and approval freshness. It does not provide legal
advice and does not certify that an approval or its evidence is legally sufficient.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import sys
from pathlib import Path
from typing import Any, TypeGuard

REQUIRED_CATEGORIES = {
    "project-rights-holder-apache-authority",
    "author-identities-aliases",
    "employer-contractor-predecessor-c0re-permissions",
    "copied-adapted-materials",
    "logos-media-branding",
    "vendored-agency-agents",
    "third-party-redistribution-terms",
    "canonical-tag-binary-authority",
    "signing-risk-decision",
}


def nonblank(value: Any) -> TypeGuard[str]:
    return isinstance(value, str) and bool(value.strip())


def parse_date(value: Any, field: str, errors: list[str]) -> dt.date | None:
    if not nonblank(value):
        errors.append(f"{field} must be a non-blank ISO date")
        return None
    try:
        return dt.date.fromisoformat(value)
    except ValueError:
        errors.append(f"{field} must use YYYY-MM-DD")
        return None


def validate_evidence_path(repository_root: Path, category_id: str, value: Any, errors: list[str]) -> None:
    if value is None:
        return
    if not nonblank(value):
        errors.append(f"{category_id}: evidence repository_path must be non-blank when present")
        return

    candidate = repository_root / value
    try:
        resolved = candidate.resolve(strict=True)
        resolved.relative_to(repository_root.resolve(strict=True))
    except (FileNotFoundError, ValueError):
        errors.append(f"{category_id}: evidence repository_path is missing or escapes the repository: {value}")
        return

    if not resolved.is_file():
        errors.append(f"{category_id}: evidence repository_path is not a file: {value}")


def validate(register: Any, repository_root: Path, today: dt.date) -> list[str]:
    errors: list[str] = []
    if not isinstance(register, dict):
        return ["register root must be a JSON object"]

    if register.get("schema_version") != 1:
        errors.append("schema_version must be 1")
    if not nonblank(register.get("legal_notice")):
        errors.append("legal_notice must be non-blank")

    confirmation = register.get("owner_confirmation_input")
    if not isinstance(confirmation, dict):
        errors.append("owner_confirmation_input must be an object")
    elif confirmation.get("status") not in {"pending_durable_evidence", "superseded_by_register"}:
        errors.append("owner_confirmation_input.status must be pending_durable_evidence or superseded_by_register")

    decisions = register.get("decisions")
    if not isinstance(decisions, list):
        return errors + ["decisions must be an array"]

    by_id: dict[str, dict[str, Any]] = {}
    for index, raw_decision in enumerate(decisions):
        if not isinstance(raw_decision, dict):
            errors.append(f"decisions[{index}] must be an object")
            continue
        category_id = raw_decision.get("id")
        if not nonblank(category_id):
            errors.append(f"decisions[{index}].id must be non-blank")
            continue
        if category_id in by_id:
            errors.append(f"duplicate category: {category_id}")
            continue
        by_id[category_id] = raw_decision

    missing = sorted(REQUIRED_CATEGORIES - by_id.keys())
    unexpected = sorted(by_id.keys() - REQUIRED_CATEGORIES)
    if missing:
        errors.append(f"missing required categories: {', '.join(missing)}")
    if unexpected:
        errors.append(f"unexpected categories: {', '.join(unexpected)}")

    for category_id in sorted(REQUIRED_CATEGORIES & by_id.keys()):
        decision = by_id[category_id]
        if category_id == "author-identities-aliases":
            subjects = decision.get("subjects")
            if not isinstance(subjects, list) or not subjects or any(not nonblank(subject) for subject in subjects):
                errors.append(
                    "author-identities-aliases: subjects must contain at least one non-blank public identity or alias"
                )
        status = decision.get("status")
        if status != "approved":
            errors.append(f"{category_id}: status must be approved (found {status!r})")

        approver = decision.get("approver")
        if not isinstance(approver, dict):
            errors.append(f"{category_id}: approver must be an object")
        else:
            if not nonblank(approver.get("name")):
                errors.append(f"{category_id}: approver.name must be non-blank")
            if not nonblank(approver.get("authority_basis")):
                errors.append(f"{category_id}: approver.authority_basis must be non-blank")

        decision_date = parse_date(decision.get("decision_date"), f"{category_id}.decision_date", errors)
        expires_on = parse_date(decision.get("expires_on"), f"{category_id}.expires_on", errors)
        if decision_date is not None and decision_date > today:
            errors.append(f"{category_id}: decision_date cannot be in the future")
        if expires_on is not None and expires_on < today:
            errors.append(f"{category_id}: approval is stale (expired {expires_on.isoformat()})")
        if decision_date is not None and expires_on is not None and expires_on < decision_date:
            errors.append(f"{category_id}: expires_on precedes decision_date")

        evidence = decision.get("evidence")
        if not isinstance(evidence, list) or not evidence:
            errors.append(f"{category_id}: evidence must be a non-empty array")
            continue
        for evidence_index, item in enumerate(evidence):
            if not isinstance(item, dict):
                errors.append(f"{category_id}: evidence[{evidence_index}] must be an object")
                continue
            if not nonblank(item.get("reference")):
                errors.append(f"{category_id}: evidence[{evidence_index}].reference must be non-blank")
            validate_evidence_path(repository_root, category_id, item.get("repository_path"), errors)

    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "register",
        nargs="?",
        default="docs/compliance/release-authority-register.json",
        help="register path (default: docs/compliance/release-authority-register.json)",
    )
    parser.add_argument(
        "--repository-root",
        type=Path,
        help="repository root used to resolve evidence paths (default: git root)",
    )
    parser.add_argument("--today", help=argparse.SUPPRESS)
    args = parser.parse_args()

    if args.repository_root is None:
        repository_root = Path(__file__).resolve().parents[2]
    else:
        repository_root = args.repository_root.resolve()

    register_path = Path(args.register)
    if not register_path.is_absolute():
        register_path = repository_root / register_path

    try:
        today = dt.date.fromisoformat(args.today) if args.today else dt.date.today()
        register = json.loads(register_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError, ValueError) as exc:
        print(f"release-authority: FAIL: {exc}", file=sys.stderr)
        return 1

    errors = validate(register, repository_root, today)
    if errors:
        print("release-authority: FAIL", file=sys.stderr)
        for error in errors:
            print(f"  - {error}", file=sys.stderr)
        print(
            "This automation checks structure, completeness, evidence paths, and freshness only; "
            "it is not legal advice or certification.",
            file=sys.stderr,
        )
        return 1

    print(
        "release-authority: PASS — all required decisions are approved, evidenced, and current. "
        "This is a structural gate, not legal advice or certification."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
