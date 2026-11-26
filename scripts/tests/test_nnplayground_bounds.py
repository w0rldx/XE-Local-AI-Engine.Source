#!/usr/bin/env python3
"""The Model Lab training stepper's knob bounds and dataset vocabulary: the host's start request, as the committed
OpenAPI document publishes it, against ``KNOB_BOUNDS`` and ``DATASETS`` in tools/training/nnplayground.py.

Outside tools/training on purpose: it reads the React tree's OpenAPI copy. A missing bound or enum fails; it never
skips. Imports nnplayground without torch (the constants load without it).
"""

from __future__ import annotations

import importlib
import json
import sys
import unittest
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]
OPENAPI = REPO_ROOT / "XE-Local-AI-Engine.Client.React" / "openapi" / "v1.json"
START_PATH = "/api/local/v1/model-lab/playground/runs"
# nnplayground imports its siblings by name, so tools/training goes on the path as it does under pytest's config.
sys.path.insert(0, str(REPO_ROOT / "tools" / "training"))
NN: Any = importlib.import_module("nnplayground")


def _resolve(document: dict[str, Any], schema: dict[str, Any]) -> dict[str, Any]:
    while "$ref" in schema:
        node: Any = document
        for part in schema["$ref"].removeprefix("#/").split("/"):
            node = node[part]
        schema = node
    return schema


def _start_request(document: dict[str, Any]) -> dict[str, Any]:
    body = document["paths"][START_PATH]["post"]["requestBody"]["content"]["application/json"]["schema"]
    return _resolve(document, body)


class PlaygroundBoundsTests(unittest.TestCase):
    def setUp(self) -> None:
        self.document = json.loads(OPENAPI.read_text(encoding="utf-8"))
        self.request = _start_request(self.document)

    def test_every_knob_bound_in_the_start_request_equals_the_scripts(self) -> None:
        properties = self.request["properties"]
        for name, (low, high) in NN.KNOB_BOUNDS.items():
            with self.subTest(knob=name):
                self.assertIn(name, properties, f"the start request has no '{name}'")
                schema = _resolve(self.document, properties[name])
                self.assertIn("minimum", schema, f"'{name}' publishes no minimum")
                self.assertIn("maximum", schema, f"'{name}' publishes no maximum")
                self.assertEqual((schema["minimum"], schema["maximum"]), (low, high))
                self.assertIn(name, self.request.get("required", []), f"'{name}' is not required")

    def test_every_bounded_property_in_the_start_request_is_a_script_knob(self) -> None:
        bounded = {
            name
            for name, schema in self.request["properties"].items()
            if {"minimum", "maximum"} & _resolve(self.document, schema).keys()
        }
        self.assertEqual(bounded, set(NN.KNOB_BOUNDS))

    def test_the_dataset_enum_equals_the_scripts(self) -> None:
        self.assertIn("dataset", self.request["properties"], "the start request has no 'dataset'")
        schema = _resolve(self.document, self.request["properties"]["dataset"])
        self.assertIn("enum", schema, "'dataset' publishes no enum")
        self.assertEqual(tuple(schema["enum"]), NN.DATASETS)


if __name__ == "__main__":
    unittest.main()
