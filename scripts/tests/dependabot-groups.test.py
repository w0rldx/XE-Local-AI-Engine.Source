#!/usr/bin/env python3
from __future__ import annotations

import fnmatch
import json
import unittest
from ast import literal_eval
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]
DEPENDABOT = REPO_ROOT / ".github" / "dependabot.yml"
PACKAGE_MANIFEST = REPO_ROOT / "XE-Local-AI-Engine.Client.React" / "package.json"
PNPM_WORKSPACE = REPO_ROOT / "XE-Local-AI-Engine.Client.React" / "pnpm-workspace.yaml"
TRAINING_MANIFEST = REPO_ROOT / "tools" / "training" / "pyproject.toml"

EXPECTED_GROUPS = {
    "frontend-runtime",
    "frontend-openapi-codegen",
    "frontend-type-definitions",
    "frontend-development-tooling",
}
CODEGEN_DEPENDENCIES = {"@hey-api/openapi-ts", "typescript"}


def parse_scalar(value: str) -> str | int | bool | None:
    if value.startswith(("'", '"')):
        parsed = literal_eval(value)
        if not isinstance(parsed, str):
            raise ValueError(f"unsupported quoted YAML scalar: {value}")
        return parsed
    if value == "true":
        return True
    if value == "false":
        return False
    if value in {"null", "~"}:
        return None
    try:
        return int(value)
    except ValueError:
        return value


def split_mapping_entry(source: str) -> tuple[str, str]:
    key, separator, value = source.partition(":")
    if not separator or not key or key != key.strip():
        raise ValueError(f"unsupported YAML mapping entry: {source}")
    return key, value.strip()


def load_yaml(path: Path) -> dict[str, Any]:
    lines = [
        (len(line) - len(line.lstrip(" ")), line.lstrip(" "))
        for line in path.read_text(encoding="utf-8").splitlines()
        if line.strip() and not line.lstrip().startswith("#")
    ]

    def parse_node(index: int, indent: int) -> tuple[Any, int]:
        if index >= len(lines) or lines[index][0] != indent:
            raise ValueError(f"expected YAML content at indentation {indent}")
        if lines[index][1].startswith("- "):
            return parse_sequence(index, indent)
        return parse_mapping(index, indent)

    def parse_mapping(index: int, indent: int) -> tuple[dict[str, Any], int]:
        result: dict[str, Any] = {}
        while index < len(lines) and lines[index][0] == indent and not lines[index][1].startswith("- "):
            key, value = split_mapping_entry(lines[index][1])
            if key in result:
                raise ValueError(f"duplicate YAML key: {key}")
            index += 1
            if value:
                result[key] = parse_scalar(value)
            else:
                if index >= len(lines) or lines[index][0] <= indent:
                    raise ValueError(f"YAML key has no value: {key}")
                result[key], index = parse_node(index, lines[index][0])
        return result, index

    def parse_sequence(index: int, indent: int) -> tuple[list[Any], int]:
        result: list[Any] = []
        while index < len(lines) and lines[index][0] == indent and lines[index][1].startswith("- "):
            item = lines[index][1][2:].strip()
            index += 1
            if item.startswith(("'", '"')) or ":" not in item:
                result.append(parse_scalar(item))
                continue

            key, value = split_mapping_entry(item)
            mapping: dict[str, Any] = {key: parse_scalar(value)}
            if index < len(lines) and lines[index][0] > indent:
                continuation, index = parse_mapping(index, lines[index][0])
                duplicate_keys = mapping.keys() & continuation.keys()
                if duplicate_keys:
                    raise ValueError(f"duplicate YAML keys: {sorted(duplicate_keys)}")
                mapping.update(continuation)
            result.append(mapping)
        return result, index

    parsed, final_index = parse_node(0, lines[0][0])
    if final_index != len(lines):
        raise ValueError(f"unsupported YAML structure at: {lines[final_index][1]}")
    if not isinstance(parsed, dict):
        raise TypeError("dependabot.yml must contain a top-level mapping")
    return parsed


def matches(group: dict[str, Any], dependency: str, dependency_type: str) -> bool:
    if group.get("dependency-type") != dependency_type:
        return False
    patterns = group.get("patterns", ["*"])
    excluded = group.get("exclude-patterns", [])
    return any(fnmatch.fnmatchcase(dependency, pattern) for pattern in patterns) and not any(
        fnmatch.fnmatchcase(dependency, pattern) for pattern in excluded
    )


class DependabotGroupContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        configuration = load_yaml(DEPENDABOT)
        updates = configuration.get("updates", [])
        npm_updates = [
            update
            for update in updates
            if update.get("package-ecosystem") == "npm"
            and update.get("directory") == "/XE-Local-AI-Engine.Client.React"
        ]
        if len(npm_updates) != 1:
            raise AssertionError("expected exactly one frontend npm Dependabot configuration")
        cls.npm_update = npm_updates[0]
        cls.groups = cls.npm_update.get("groups", {})
        uv_updates = [
            update
            for update in updates
            if update.get("package-ecosystem") == "uv" and update.get("directory") == "/tools/training"
        ]
        if len(uv_updates) != 1:
            raise AssertionError("expected exactly one training-runtime uv Dependabot configuration")
        cls.uv_update = uv_updates[0]
        cls.package_manifest = json.loads(PACKAGE_MANIFEST.read_text(encoding="utf-8"))
        cls.pnpm_workspace = load_yaml(PNPM_WORKSPACE)

    def test_frontend_schedule_and_scope_are_preserved(self) -> None:
        self.assertEqual("develop", self.npm_update["target-branch"])
        self.assertEqual(3, self.npm_update["open-pull-requests-limit"])
        self.assertEqual(
            {"interval": "weekly", "day": "monday", "time": "06:30", "timezone": "Europe/Berlin"},
            self.npm_update["schedule"],
        )

    def test_training_runtime_schedule_and_scope_are_preserved(self) -> None:
        self.assertTrue(TRAINING_MANIFEST.is_file(), f"uv directory does not hold a manifest: {TRAINING_MANIFEST}")
        self.assertEqual("develop", self.uv_update["target-branch"])
        self.assertEqual(
            {"interval": "weekly", "day": "monday", "time": "06:00", "timezone": "Europe/Berlin"},
            self.uv_update["schedule"],
        )

    def test_training_runtime_updates_stay_one_at_a_time_and_ungrouped(self) -> None:
        # Every unsloth-capped bump needs its own `uv lock` verification, so the runtime is
        # deliberately not batched: one open PR, no groups.
        self.assertEqual(1, self.uv_update["open-pull-requests-limit"])
        self.assertNotIn("groups", self.uv_update)

    def test_release_age_policy_is_compatible_with_dependabot(self) -> None:
        self.assertEqual(10080, self.pnpm_workspace["minimumReleaseAge"])
        self.assertFalse(self.pnpm_workspace["minimumReleaseAgeStrict"])
        self.assertEqual({"default-days": 7}, self.npm_update["cooldown"])

    def test_unsupported_typescript_and_node_type_majors_are_temporarily_ignored(self) -> None:
        ignores = {entry["dependency-name"]: entry for entry in self.npm_update["ignore"]}

        self.assertEqual(
            {"dependency-name": "typescript", "versions": [">=7"]},
            ignores["typescript"],
        )
        self.assertEqual(
            {
                "dependency-name": "@types/node",
                "update-types": ["version-update:semver-major"],
            },
            ignores["@types/node"],
        )

    def test_groups_are_deliberate_and_non_overlapping_for_current_dependencies(self) -> None:
        self.assertEqual(EXPECTED_GROUPS, set(self.groups))

        dependency_sets = {
            "production": self.package_manifest["dependencies"],
            "development": self.package_manifest["devDependencies"],
        }
        for dependency_type, dependencies in dependency_sets.items():
            for dependency in dependencies:
                matching_groups = [
                    name for name, group in self.groups.items() if matches(group, dependency, dependency_type)
                ]
                self.assertEqual(
                    1,
                    len(matching_groups),
                    f"{dependency} ({dependency_type}) matched {matching_groups}",
                )

    def test_openapi_codegen_pair_is_isolated_from_development_catch_all(self) -> None:
        codegen = self.groups["frontend-openapi-codegen"]
        self.assertEqual("development", codegen["dependency-type"])
        self.assertEqual(CODEGEN_DEPENDENCIES, set(codegen["patterns"]))

        catch_all = self.groups["frontend-development-tooling"]
        self.assertTrue(CODEGEN_DEPENDENCIES.issubset(set(catch_all["exclude-patterns"])))
        for dependency in CODEGEN_DEPENDENCIES:
            self.assertTrue(matches(codegen, dependency, "development"))
            self.assertFalse(matches(catch_all, dependency, "development"))

    def test_production_updates_are_separated_from_development_updates(self) -> None:
        runtime = self.groups["frontend-runtime"]
        self.assertEqual("production", runtime["dependency-type"])
        self.assertEqual(["*"], runtime["patterns"])
        for name, group in self.groups.items():
            if name != "frontend-runtime":
                self.assertEqual("development", group["dependency-type"])


if __name__ == "__main__":
    unittest.main()
