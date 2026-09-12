"""The committed catalog itself: the generated document and its seed copy.

These are data tests, not converter tests. They fail when someone hand-edits dist/applications.json or
the embedded seed, or when the two drift apart. The shipped catalog carries no applications today, so
the empty document is part of what they pin.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

import build_catalog


def test_the_committed_document_matches_a_fresh_build() -> None:
    committed = json.loads(build_catalog.DIST_PATH.read_text(encoding="utf-8"))
    rebuilt = build_catalog.build_document()
    rebuilt["generatedAtUtc"] = committed["generatedAtUtc"]

    assert build_catalog.serialize(rebuilt) == build_catalog.serialize(committed)


def test_the_embedded_seed_is_byte_identical_to_the_generated_document() -> None:
    assert build_catalog.SEED_PATH.exists(), (
        f"{build_catalog.SEED_PATH} is missing; the converter writes it on every build and the seed is committed."
    )
    assert build_catalog.SEED_PATH.read_bytes() == build_catalog.DIST_PATH.read_bytes()


@pytest.mark.parametrize("create_directory", [False, True], ids=["absent", "empty"])
def test_a_catalog_with_no_applications_builds_the_empty_document(tmp_path: Path, create_directory: bool) -> None:
    """The shipped catalog ships no application, and git records no empty directory -- so a checkout carries no
    applications/ directory at all. Both spellings must build the empty document rather than raise."""
    applications = tmp_path / "applications"
    if create_directory:
        applications.mkdir()

    with pytest.MonkeyPatch.context() as patch:
        patch.setattr(build_catalog, "APPLICATIONS_DIR", applications)
        document = build_catalog.build_document()

    assert document["schemaVersion"] == build_catalog.SCHEMA_VERSION
    assert document["applications"] == []
