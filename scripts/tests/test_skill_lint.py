#!/usr/bin/env python3
"""Dual-runner tests for scripts/skill-lint.py."""

from __future__ import annotations

import contextlib
import importlib.util
import io
import sys
import tempfile
import unittest
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = REPO_ROOT / "scripts" / "skill-lint.py"
SPEC = importlib.util.spec_from_file_location("skill_lint", MODULE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"could not load {MODULE_PATH}")
MODULE: Any = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class SkillLintTests(unittest.TestCase):
    def make_skill(
        self,
        *,
        directory_name: str = "example-skill",
        name: str = "example-skill",
        description: str = "A valid description.",
        license_name: str = "Apache-2.0",
        body: str = "# Example\n\nSee [details](references/details.md).\n",
    ) -> tuple[Path, Path]:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name) / "repo"
        skill = root / "skills" / directory_name
        references = skill / "references"
        references.mkdir(parents=True)
        (references / "details.md").write_text("# Details\n", encoding="utf-8")
        (skill / "SKILL.md").write_text(
            f"---\nname: {name}\ndescription: >-\n  {description}\nlicense: {license_name}\n---\n{body}",
            encoding="utf-8",
        )
        return root, skill

    def test_valid_skill_passes(self) -> None:
        root, skill = self.make_skill()

        self.assertEqual([], MODULE.validate(skill, root))

    def test_required_frontmatter_fields_are_accepted(self) -> None:
        root, skill = self.make_skill()

        document = MODULE.parse_frontmatter(skill / "SKILL.md")

        self.assertEqual("example-skill", document.fields["name"])
        self.assertEqual("A valid description.", document.fields["description"])
        self.assertEqual("Apache-2.0", document.fields["license"])
        self.assertEqual([], MODULE.validate(skill, root))

    def test_missing_required_frontmatter_field_fails(self) -> None:
        root, skill = self.make_skill(license_name="")

        self.assertIn("SKILL.md frontmatter requires a non-empty license field", MODULE.validate(skill, root))

    def test_matching_directory_name_passes(self) -> None:
        root, skill = self.make_skill(directory_name="matching-name", name="matching-name")

        self.assertEqual([], MODULE.validate(skill, root))

    def test_mismatched_directory_name_fails(self) -> None:
        root, skill = self.make_skill(directory_name="directory-name", name="different-name")

        self.assertTrue(any("must equal parent directory name" in item for item in MODULE.validate(skill, root)))

    def test_valid_hyphenated_name_passes(self) -> None:
        root, skill = self.make_skill(directory_name="skill-2", name="skill-2")

        self.assertEqual([], MODULE.validate(skill, root))

    def test_invalid_name_shape_fails(self) -> None:
        root, skill = self.make_skill(directory_name="Bad_Name", name="Bad_Name")

        self.assertTrue(any("single hyphens only" in item for item in MODULE.validate(skill, root)))

    def test_description_at_limit_passes(self) -> None:
        root, skill = self.make_skill(description="x" * 1024)

        self.assertEqual([], MODULE.validate(skill, root))

    def test_description_over_limit_fails(self) -> None:
        root, skill = self.make_skill(description="x" * 1025)

        self.assertTrue(any("1..1024" in item for item in MODULE.validate(skill, root)))

    def test_resolving_relative_links_passes(self) -> None:
        root, skill = self.make_skill(body="# Example\n\n[Reference](references/details.md)\n[Root](../../README.md)\n")
        (root / "README.md").write_text("# Root\n", encoding="utf-8")

        self.assertEqual([], MODULE.validate(skill, root))

    def test_missing_relative_link_target_fails(self) -> None:
        root, skill = self.make_skill(body="# Example\n\n[Missing](references/missing.md)\n")

        self.assertTrue(any("link target does not exist" in item for item in MODULE.validate(skill, root)))

    def test_body_below_limit_passes(self) -> None:
        root, skill = self.make_skill(body="\n".join(["# Example", *(["body"] * 498)]))

        self.assertEqual([], MODULE.validate(skill, root))

    def test_body_at_limit_fails(self) -> None:
        root, skill = self.make_skill(body="\n".join(["# Example", *(["body"] * 499)]))

        self.assertTrue(any("must be under 500 lines" in item for item in MODULE.validate(skill, root)))

    def test_missing_skill_directory_exits_two(self) -> None:
        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr):
            exit_code = MODULE.main([str(REPO_ROOT / "does-not-exist")])

        self.assertEqual(2, exit_code)
        self.assertIn("skill directory does not exist", stderr.getvalue())

    def test_real_skill_is_clean(self) -> None:
        skill = REPO_ROOT / "skills" / "xe-local-ai-engine"

        self.assertEqual([], MODULE.validate(skill, REPO_ROOT))


if __name__ == "__main__":
    unittest.main()
