#!/usr/bin/env python3
"""Contract tests for scripts/openapi-breaking-check.py: one row per breaking kind, additive kind and input shape."""

from __future__ import annotations

import copy
import importlib.util
import io
import json
import tempfile
import unittest
from collections.abc import Callable
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from typing import Any

MODULE_PATH = Path(__file__).resolve().parents[1] / "openapi-breaking-check.py"
SPEC = importlib.util.spec_from_file_location("openapi_breaking_check", MODULE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"could not load {MODULE_PATH}")
MODULE: Any = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

BASE: dict[str, Any] = {
    "openapi": "3.0.0",
    "paths": {
        "/api/items": {
            "get": {"parameters": [{"name": "take", "in": "query", "required": False}]},
            "post": {
                "requestBody": {"content": {"application/json": {"schema": {"$ref": "#/components/schemas/Create"}}}}
            },
        },
        "/api/inline": {
            "put": {"requestBody": {"content": {"application/json": {"schema": {"type": "object", "properties": {}}}}}},
        },
        "/api/upload": {
            "post": {
                "requestBody": {
                    "content": {
                        "application/json": {"schema": {"properties": {"a": {}}, "required": ["a"]}},
                        "multipart/form-data": {"schema": {"properties": {"b": {}}, "required": ["b"]}},
                    }
                }
            }
        },
    },
    "components": {
        "schemas": {
            "Create": {"properties": {"name": {}, "tag": {"$ref": "#/components/schemas/Tag"}}, "required": ["name"]},
            "Tag": {"properties": {"label": {}, "color": {}}},
            "Item": {"properties": {"id": {}, "name": {}}, "required": ["id"]},
        }
    },
}


def schema(spec: dict[str, Any], name: str) -> dict[str, Any]:
    return spec["components"]["schemas"][name]


def operation(spec: dict[str, Any], key: str) -> dict[str, Any]:
    method, path = key.split(" ")
    return spec["paths"][path][method]


def inline_body(spec: dict[str, Any]) -> dict[str, Any]:
    return operation(spec, "put /api/inline")["requestBody"]["content"]["application/json"]


def swap_create_for_v2(spec: dict[str, Any]) -> None:
    spec["components"]["schemas"]["CreateV2"] = {"properties": {"name": {}, "z": {}}, "required": ["name", "z"]}
    operation(spec, "post /api/items")["requestBody"]["content"]["application/json"]["schema"] = {
        "$ref": "#/components/schemas/CreateV2"
    }


def reorder_upload_content(spec: dict[str, Any]) -> None:
    body = operation(spec, "post /api/upload")["requestBody"]
    body["content"] = dict(reversed(list(body["content"].items())))


# (name, edit to the current spec, acknowledged ids, expected exit, expected text in the output or None)
ROWS: list[tuple[str, Callable[[dict[str, Any]], object], list[str], int, str | None]] = [
    ("operation removed", lambda s: s["paths"]["/api/items"].pop("get"), [], 1, "operation-removed:GET /api/items"),
    ("path removed", lambda s: s["paths"].pop("/api/inline"), [], 1, "operation-removed:PUT /api/inline"),
    ("schema removed", lambda s: s["components"]["schemas"].pop("Item"), [], 1, "schema-removed:Item"),
    ("property removed", lambda s: schema(s, "Item")["properties"].pop("name"), [], 1, "property-removed:Item.name"),
    (
        "request property newly required",
        lambda s: schema(s, "Create")["required"].append("tag"),
        [],
        1,
        "request-property-required:Create.tag",
    ),
    (
        "property required in a schema the request body references",
        lambda s: schema(s, "Tag").update(required=["label"]),
        [],
        1,
        "request-property-required:Tag.label",
    ),
    (
        "inline request body property newly required",
        lambda s: inline_body(s)["schema"].update(properties={"x": {}}, required=["x"]),
        [],
        1,
        "request-property-required:PUT /api/inline application/json body.x",
    ),
    (
        "body $ref swapped to a new schema with a new required property while the old one survives",
        swap_create_for_v2,
        [],
        1,
        "request-property-required:POST /api/items application/json body.z",
    ),
    (
        "inline request body replaced by a $ref with required properties",
        lambda s: inline_body(s).update(schema={"$ref": "#/components/schemas/Create"}),
        [],
        1,
        "request-property-required:PUT /api/inline application/json body.name",
    ),
    (
        "request body became required",
        lambda s: operation(s, "post /api/items")["requestBody"].update(required=True),
        [],
        1,
        "request-body-required:POST /api/items",
    ),
    ("additive: request body content types reordered", reorder_upload_content, [], 0, None),
    (
        "new required parameter",
        lambda s: operation(s, "get /api/items")["parameters"].append({"name": "q", "in": "query", "required": True}),
        [],
        1,
        "parameter-required:GET /api/items query:q",
    ),
    (
        "parameter became required",
        lambda s: operation(s, "get /api/items")["parameters"][0].update(required=True),
        [],
        1,
        "parameter-required:GET /api/items query:take",
    ),
    ("additive: new path", lambda s: s["paths"].update({"/api/new": {"get": {}}}), [], 0, None),
    ("additive: new operation", lambda s: s["paths"]["/api/inline"].update(get={}), [], 0, None),
    ("additive: new schema", lambda s: s["components"]["schemas"].update(New={"properties": {}}), [], 0, None),
    ("additive: new property", lambda s: schema(s, "Item")["properties"].update(extra={}), [], 0, None),
    (
        "additive: new optional parameter",
        lambda s: operation(s, "get /api/items")["parameters"].append({"name": "q", "in": "query"}),
        [],
        0,
        None,
    ),
    (
        "additive: response-only schema gains a required property",
        lambda s: schema(s, "Item")["required"].append("name"),
        [],
        0,
        None,
    ),
    ("additive: request property no longer required", lambda s: schema(s, "Create").update(required=[]), [], 0, None),
    (
        "acknowledged break passes",
        lambda s: s["components"]["schemas"].pop("Item"),
        ["schema-removed:Item"],
        0,
        "ACKNOWLEDGED schema-removed:Item",
    ),
    ("stale acknowledgement fails", lambda s: None, ["schema-removed:Gone"], 1, "STALE schema-removed:Gone"),
    (
        "acknowledging one break leaves another failing",
        lambda s: (s["components"]["schemas"].pop("Item"), s["paths"].pop("/api/inline")),
        ["schema-removed:Item"],
        1,
        "BREAKING operation-removed:PUT /api/inline",
    ),
]

# (name, file to corrupt, its text, expected stderr fragment)
MALFORMED = [
    ("current is not JSON", "current", "{not json", "cannot read"),
    ("baseline has no paths", "baseline", '{"openapi": "3.0.0"}', "no 'paths' object"),
    ("path item is not an object", "current", '{"paths": {"/a": []}}', "must be an object"),
    ("schemas is not an object", "current", '{"paths": {}, "components": {"schemas": []}}', "components.schemas"),
    ("requestBody is not an object", "current", '{"paths": {"/a": {"post": {"requestBody": "x"}}}}', "requestBody"),
    (
        "requestBody content is not an object",
        "current",
        '{"paths": {"/a": {"post": {"requestBody": {"content": ["x"]}}}}}',
        "content",
    ),
    (
        "body schema is not an object",
        "current",
        '{"paths": {"/a": {"post": {"requestBody": {"content": {"application/json": {"schema": 1}}}}}}}',
        "schema",
    ),
    (
        "parameters is not a list of objects",
        "current",
        '{"paths": {"/a": {"get": {"parameters": ["q"]}}}}',
        "parameters",
    ),
    (
        "properties is a list",
        "current",
        '{"paths": {}, "components": {"schemas": {"Item": {"properties": [{}]}}}}',
        "properties",
    ),
    (
        "required is not a list of strings",
        "current",
        '{"paths": {}, "components": {"schemas": {"Item": {"required": [{}]}}}}',
        "required",
    ),
    ("acknowledgements are not a list", "acknowledged", '{"id": "x"}', "JSON list"),
    (
        "acknowledgement without a reason",
        "acknowledged",
        '[{"id": "schema-removed:Item"}]',
        "non-empty 'id' and 'reason'",
    ),
    (
        "duplicate acknowledgement",
        "acknowledged",
        '[{"id": "a", "reason": "r"}, {"id": "a", "reason": "r"}]',
        "duplicate",
    ),
]


class OpenApiBreakingCheckTests(unittest.TestCase):
    def files(self, current: dict[str, Any], acknowledged: list[str]) -> dict[str, Path]:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        root = Path(directory.name)
        paths = {name: root / f"{name}.json" for name in ("baseline", "current", "acknowledged")}
        paths["baseline"].write_text(json.dumps(BASE), encoding="utf-8")
        paths["current"].write_text(json.dumps(current), encoding="utf-8")
        entries = [{"id": entry, "reason": "intended"} for entry in acknowledged]
        paths["acknowledged"].write_text(json.dumps(entries), encoding="utf-8")
        return paths

    def run_check(self, paths: dict[str, Path], *extra: str) -> tuple[int, str, str]:
        out, err = io.StringIO(), io.StringIO()
        argv = [f"--{name}={path}" for name, path in paths.items()] + list(extra)
        with redirect_stdout(out), redirect_stderr(err):
            code = MODULE.main(argv)
        return code, out.getvalue(), err.getvalue()

    def test_rows(self) -> None:
        for name, edit, acknowledged, expected_code, expected_text in ROWS:
            with self.subTest(name):
                current = copy.deepcopy(BASE)
                edit(current)
                code, out, _ = self.run_check(self.files(current, acknowledged))
                self.assertEqual(expected_code, code, out)
                if expected_text is not None:
                    self.assertIn(expected_text, out)
                if expected_code == 0:
                    self.assertNotIn("BREAKING", out)

    def test_malformed_inputs_exit_2_with_a_clear_error(self) -> None:
        for name, target, text, fragment in MALFORMED:
            with self.subTest(name):
                paths = self.files(copy.deepcopy(BASE), [])
                paths[target].write_text(text, encoding="utf-8")
                code, _, err = self.run_check(paths)
                self.assertEqual(2, code)
                self.assertIn(fragment, err)

    def test_missing_file_exits_2(self) -> None:
        paths = self.files(copy.deepcopy(BASE), [])
        paths["acknowledged"].unlink()
        code, _, err = self.run_check(paths)
        self.assertEqual(2, code)
        self.assertIn("cannot read", err)

    def test_rebaseline_copies_current_and_empties_acknowledgements(self) -> None:
        current = copy.deepcopy(BASE)
        del current["components"]["schemas"]["Item"]
        paths = self.files(current, ["schema-removed:Item"])

        code, _, _ = self.run_check(paths, "--rebaseline")

        self.assertEqual(0, code)
        self.assertEqual(paths["current"].read_bytes(), paths["baseline"].read_bytes())
        self.assertEqual([], json.loads(paths["acknowledged"].read_text(encoding="utf-8")))
        self.assertEqual(0, self.run_check(paths)[0])


if __name__ == "__main__":
    unittest.main()
