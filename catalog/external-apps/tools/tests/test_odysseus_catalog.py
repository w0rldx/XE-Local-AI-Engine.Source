"""The committed Odysseus catalog itself: the generated document, its seed copy and the classification.

These are data tests, not converter tests. They fail when someone hand-edits dist/applications.json or
the embedded seed, when the two drift apart, or when the variables.json buckets stop covering the
upstream compose.
"""

from __future__ import annotations

import json
from typing import Any

import yaml

import build_catalog

APPLICATION_DIR = build_catalog.APPLICATIONS_DIR / "odysseus"


def load_variables() -> dict[str, Any]:
    return json.loads((APPLICATION_DIR / "variables.json").read_text(encoding="utf-8"))


def load_compose() -> dict[str, Any]:
    return yaml.safe_load((APPLICATION_DIR / "compose.yml").read_text(encoding="utf-8"))


def test_the_committed_document_matches_a_fresh_build() -> None:
    committed = json.loads(build_catalog.DIST_PATH.read_text(encoding="utf-8"))
    rebuilt = build_catalog.build_document()
    rebuilt["generatedAtUtc"] = committed["generatedAtUtc"]

    assert build_catalog.serialize(rebuilt) == build_catalog.serialize(committed)


def test_the_embedded_seed_is_byte_identical_to_the_generated_document() -> None:
    assert build_catalog.SEED_PATH.exists(), (
        f"{build_catalog.SEED_PATH} is missing; the converter writes it on every build and commit 7 commits it."
    )
    assert build_catalog.SEED_PATH.read_bytes() == build_catalog.DIST_PATH.read_bytes()


def test_every_classified_name_appears_in_exactly_one_bucket() -> None:
    variables = load_variables()
    names = (
        [entry["name"] for entry in variables["exposed"]]
        + list(variables["fixed"])
        + [entry["name"] for entry in variables["dropped"]]
    )

    assert len(names) == len(set(names))
    assert len(names) == 54


def test_the_buckets_cover_every_compose_environment_key() -> None:
    variables = load_variables()
    classified = (
        {entry["name"] for entry in variables["exposed"]}
        | set(variables["fixed"])
        | {entry["name"] for entry in variables["dropped"]}
    )

    keys = {
        key
        for name, service in load_compose()["services"].items()
        for key in build_catalog.service_environment_keys("odysseus", name, service)
    }

    assert keys, "the compose declares no environment keys, which cannot be right"
    assert keys <= classified


def test_every_dropped_name_carries_a_reason() -> None:
    assert all(entry.get("reason") for entry in load_variables()["dropped"])
