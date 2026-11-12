#!/usr/bin/env python3
"""Fail when the committed OpenAPI contract loses something a client could depend on, unless acknowledged.

Compares the release baseline (`XE-Local-AI-Engine.Client.React/openapi/v1.baseline.json`) with the current
`openapi/v1.json`. Breaking, each printed as a finding id:
  operation-removed:<METHOD> <path>                 a path or one of its operations is gone
  schema-removed:<Schema>                           a component schema is gone
  property-removed:<Schema>.<property>              a property of a schema that still exists is gone
  request-property-required:<Schema>.<property>     a request-body schema (or one it references) requires a new
                                                    property
  request-property-required:<METHOD> <path> <media type> body.<property>
                                                    the body of one media type requires a property the baseline
                                                    body did not (inline, or a `$ref` that now names another schema;
                                                    `$ref`s are resolved one level, media types paired by key)
  request-body-required:<METHOD> <path>             the request body was optional or absent and is now required
  parameter-required:<METHOD> <path> <in>:<name>    a parameter is new and required, or became required
Additive changes pass. Not detected: type, format or enum changes; response-only changes beyond removal;
`allOf`/`oneOf` members; path-level parameters; `requestBody` `$ref`s to components.requestBodies; a media type
removed from a body.

An acknowledged finding passes: `openapi/breaking-acknowledged.json` is a JSON list of {"id", "reason"} objects.
An acknowledgement that matches no finding fails, so stale entries get removed.
`--rebaseline` (run at each release) copies the current spec over the baseline and empties the acknowledgements.

Usage: scripts/openapi-breaking-check.py [--baseline F] [--current F] [--acknowledged F] [--rebaseline]
Exit codes: 0 no unacknowledged break (or rebaselined), 1 unacknowledged break or stale acknowledgement,
2 unreadable or malformed input.
"""

from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path
from typing import Any

OPENAPI_DIR = Path(__file__).resolve().parents[1] / "XE-Local-AI-Engine.Client.React" / "openapi"
METHODS = ("get", "put", "post", "delete", "options", "head", "patch", "trace")
SCHEMA_REF = "#/components/schemas/"


class InputError(Exception):
    pass


def load_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        raise InputError(f"cannot read {path}: {error}") from error


def load_spec(path: Path) -> dict[str, Any]:
    spec = load_json(path)
    if not isinstance(spec, dict) or not isinstance(spec.get("paths"), dict):
        raise InputError(f"{path} is not an OpenAPI document (no 'paths' object)")
    if not all(isinstance(item, dict) for item in spec["paths"].values()):
        raise InputError(f"{path}: every entry of 'paths' must be an object")
    components = spec.get("components", {})
    if not isinstance(components, dict) or not isinstance(components.get("schemas", {}), dict):
        raise InputError(f"{path}: components.schemas is not an object")
    for name, schema in components.get("schemas", {}).items():
        check_schema(path, f"components.schemas.{name}", schema)
    for key, operation in operations(spec).items():
        parameters = operation.get("parameters", [])
        if not isinstance(parameters, list) or not all(isinstance(item, dict) for item in parameters):
            raise InputError(f"{path}: {key} parameters must be a list of objects")
        body = operation.get("requestBody", {})
        if not isinstance(body, dict) or not isinstance(body.get("content", {}), dict):
            raise InputError(f"{path}: {key} requestBody and its content must be objects")
        for media_type, media in body.get("content", {}).items():
            if not isinstance(media, dict):
                raise InputError(f"{path}: {key} requestBody content {media_type} must be an object")
            check_schema(path, f"{key} requestBody {media_type} schema", media.get("schema", {}))
    return spec


def check_schema(path: Path, where: str, schema: Any) -> None:
    if not isinstance(schema, dict):
        raise InputError(f"{path}: {where} must be an object")
    if not isinstance(schema.get("properties", {}), dict):
        raise InputError(f"{path}: {where} properties must be an object")
    names = schema.get("required", [])
    if not isinstance(names, list) or not all(isinstance(name, str) for name in names):
        raise InputError(f"{path}: {where} required must be a list of strings")


def load_acknowledged(path: Path) -> dict[str, str]:
    entries = load_json(path)
    if not isinstance(entries, list):
        raise InputError(f"{path} must be a JSON list of {{'id', 'reason'}} objects")
    acknowledged: dict[str, str] = {}
    for entry in entries:
        if not (
            isinstance(entry, dict)
            and isinstance(entry.get("id"), str)
            and entry["id"].strip()
            and isinstance(entry.get("reason"), str)
            and entry["reason"].strip()
        ):
            raise InputError(f"{path}: every entry needs a non-empty 'id' and 'reason': {entry!r}")
        if entry["id"] in acknowledged:
            raise InputError(f"{path}: duplicate id {entry['id']}")
        acknowledged[entry["id"]] = entry["reason"]
    return acknowledged


def operations(spec: dict[str, Any]) -> dict[str, dict[str, Any]]:
    found = {}
    for path, item in spec["paths"].items():
        for method, operation in item.items():
            if method in METHODS and isinstance(operation, dict):
                found[f"{method.upper()} {path}"] = operation
    return found


def schemas(spec: dict[str, Any]) -> dict[str, Any]:
    return spec.get("components", {}).get("schemas", {})


def refs(node: Any) -> set[str]:
    """Every component schema name a `$ref` anywhere under node points at."""
    if isinstance(node, dict):
        found = {node["$ref"].removeprefix(SCHEMA_REF)} if isinstance(node.get("$ref"), str) else set()
        for value in node.values():
            found |= refs(value)
        return found
    if isinstance(node, list):
        return set().union(*(refs(value) for value in node))
    return set()


def body_schemas(operation: dict[str, Any]) -> dict[str, dict[str, Any]]:
    """The request body schema per media type."""
    content = (operation.get("requestBody") or {}).get("content") or {}
    return {media_type: media["schema"] for media_type, media in content.items() if media.get("schema")}


def body_required(operation: dict[str, Any]) -> bool:
    return bool((operation.get("requestBody") or {}).get("required"))


def resolve(spec: dict[str, Any], schema: dict[str, Any]) -> Any:
    """A body schema with one `$ref` level resolved to the component schema it names."""
    if isinstance(schema.get("$ref"), str):
        return schemas(spec).get(schema["$ref"].removeprefix(SCHEMA_REF))
    return schema


def request_schema_names(spec: dict[str, Any]) -> set[str]:
    """Component schemas a request body reaches, following `$ref` transitively."""
    pending = set().union(*(refs(body_schemas(op)) for op in operations(spec).values()))
    reached: set[str] = set()
    while pending:
        name = pending.pop()
        if name not in reached:
            reached.add(name)
            pending |= refs(schemas(spec).get(name)) - reached
    return reached


def required(schema: Any) -> set[str]:
    return set(schema.get("required") or []) if isinstance(schema, dict) else set()


def properties(schema: Any) -> set[str]:
    return set(schema.get("properties") or {}) if isinstance(schema, dict) else set()


def required_parameters(operation: dict[str, Any]) -> set[str]:
    return {
        f"{parameter.get('in')}:{parameter.get('name')}"
        for parameter in operation.get("parameters") or []
        if isinstance(parameter, dict) and parameter.get("required")
    }


def find_breaks(baseline: dict[str, Any], current: dict[str, Any]) -> list[str]:
    findings = []
    old_ops, new_ops = operations(baseline), operations(current)
    old_schemas, new_schemas = schemas(baseline), schemas(current)

    for key, old_op in old_ops.items():
        new_op = new_ops.get(key)
        if new_op is None:
            findings.append(f"operation-removed:{key}")
            continue
        for parameter in sorted(required_parameters(new_op) - required_parameters(old_op)):
            findings.append(f"parameter-required:{key} {parameter}")
        if body_required(new_op) and not body_required(old_op):
            findings.append(f"request-body-required:{key}")
        old_bodies, new_bodies = body_schemas(old_op), body_schemas(new_op)
        for media_type in sorted(old_bodies.keys() & new_bodies.keys()):
            old_body, new_body = old_bodies[media_type], new_bodies[media_type]
            if "$ref" in new_body and new_body["$ref"] == old_body.get("$ref"):
                continue  # the same component schema: the schema loop below compares it
            for name in sorted(required(resolve(current, new_body)) - required(resolve(baseline, old_body))):
                findings.append(f"request-property-required:{key} {media_type} body.{name}")

    request_names = request_schema_names(current)
    for name, old_schema in old_schemas.items():
        if name not in new_schemas:
            findings.append(f"schema-removed:{name}")
            continue
        new_schema = new_schemas[name]
        for prop in sorted(properties(old_schema) - properties(new_schema)):
            findings.append(f"property-removed:{name}.{prop}")
        if name in request_names:
            for prop in sorted(required(new_schema) - required(old_schema)):
                findings.append(f"request-property-required:{name}.{prop}")
    return sorted(findings)


def check(baseline_path: Path, current_path: Path, acknowledged_path: Path) -> int:
    findings = find_breaks(load_spec(baseline_path), load_spec(current_path))
    acknowledged = load_acknowledged(acknowledged_path)
    unacknowledged = [finding for finding in findings if finding not in acknowledged]
    stale = sorted(set(acknowledged) - set(findings))
    for finding in findings:
        print(f"{'ACKNOWLEDGED' if finding in acknowledged else 'BREAKING'} {finding}")
    for entry in stale:
        print(f"STALE {entry} (matches no finding; remove it from {acknowledged_path.name})")
    if unacknowledged or stale:
        print(
            f"openapi-breaking-check: FAIL ({len(unacknowledged)} unacknowledged, {len(stale)} stale). "
            f"Restore the contract, or add the id with a reason to {acknowledged_path.name}."
        )
        return 1
    print(f"openapi-breaking-check: OK ({len(findings)} acknowledged)")
    return 0


def rebaseline(baseline_path: Path, current_path: Path, acknowledged_path: Path) -> int:
    load_spec(current_path)
    shutil.copyfile(current_path, baseline_path)
    acknowledged_path.write_text("[]\n", encoding="utf-8")
    print(f"openapi-breaking-check: baseline rewritten from {current_path.name}; acknowledgements emptied")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--baseline", type=Path, default=OPENAPI_DIR / "v1.baseline.json")
    parser.add_argument("--current", type=Path, default=OPENAPI_DIR / "v1.json")
    parser.add_argument("--acknowledged", type=Path, default=OPENAPI_DIR / "breaking-acknowledged.json")
    parser.add_argument("--rebaseline", action="store_true", help="copy current over baseline, empty acknowledgements")
    args = parser.parse_args(argv)
    try:
        if args.rebaseline:
            return rebaseline(args.baseline, args.current, args.acknowledged)
        return check(args.baseline, args.current, args.acknowledged)
    except InputError as error:
        print(f"openapi-breaking-check: ERROR {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
