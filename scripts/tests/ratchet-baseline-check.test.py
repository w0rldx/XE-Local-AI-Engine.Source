#!/usr/bin/env python3
"""Contract test for scripts/ratchet-baseline-check.py against throwaway repositories (develop plus one branch)."""

from __future__ import annotations

import importlib.util
import json
import os
import runpy
import subprocess
import sys
import tempfile
import unittest
import unittest.mock
from pathlib import Path
from typing import Any

SCRIPT = Path(__file__).resolve().parents[1] / "ratchet-baseline-check.py"
ARCH = "XE-Local-AI-Engine.Tests/Architecture/"
TUPLES = ARCH + "TupleAllowlist.txt"
RECORDS = ARCH + "PositionalRecordAllowlist.txt"
KNIP = "XE-Local-AI-Engine.Client.React/config/knip-baseline.json"
REACT = "XE-Local-AI-Engine.Client.React/"
BIOME = REACT + "biome.json"


def biome(maxlines: int, *off: str) -> str:
    rule: dict[str, Any] = {"style": {"noExcessiveLinesPerFile": {"level": "error", "options": {"maxLines": maxlines}}}}
    overrides: list[dict[str, Any]] = [{"includes": ["src/**"], "linter": {"rules": rule}}]
    overrides.append({"includes": list(off), "linter": {"rules": {"style": {"noExcessiveLinesPerFile": "off"}}}})
    return json.dumps({"overrides": overrides})


BASE = {
    TUPLES: "# file|count\na.cs|3\nb.cs|2\n",
    RECORDS: "x.cs|T\n",
    ARCH + "TryOutParameterNullOnSuccessAllowlist.txt": "N.T.TryGet(value)  # absent: true with no value\n",
    KNIP: json.dumps(["k1"]),
    BIOME: biome(500, "src/Long.ts"),
}
GIT_ENV_SPEC = importlib.util.spec_from_file_location("git_env", SCRIPT.parent / "lib" / "git_env.py")
if GIT_ENV_SPEC is None or GIT_ENV_SPEC.loader is None:
    raise RuntimeError("could not load scripts/lib/git_env.py")
GIT_ENV: Any = importlib.util.module_from_spec(GIT_ENV_SPEC)
GIT_ENV_SPEC.loader.exec_module(GIT_ENV)
# Without a repository-selection variable or the caller's config, a fixture cannot reach the enclosing repository;
# the command-level settings also hide the XDG default ignore and attributes files and init templates, so no operator
# file refuses a fixture file.
ENV = GIT_ENV.fixture_env({key: value for key, value in os.environ.items() if not key.startswith("GIT_")})
ENV.update(GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_NOSYSTEM="1")


# Loaded through PYTHONPATH: the interpreter reports 3.12 and, as 3.12 does, has no PurePath.full_match.
OLD_PYTHON_SITE = """import pathlib, sys
sys.version_info = (3, 12, 9, "final", 0)
for owner in pathlib.PurePosixPath.__mro__:
    if "full_match" in vars(owner):
        delattr(owner, "full_match")
"""


class RatchetBaselineCheckTests(unittest.TestCase):
    def git(self, *args: str) -> None:
        subprocess.run(["git", "-C", str(self.repo), *args], env=ENV, check=True, capture_output=True)

    def write(self, files: dict[str, str]) -> None:
        for relative, text in files.items():
            path = self.repo / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8")
        self.git("add", "-A")
        self.git("commit", "-q", "--allow-empty", "-m", "change")

    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.repo = Path(temporary.name)
        self.git("init", "-q", "-b", "develop")
        self.git("config", "user.email", "fixture@example.invalid")
        self.git("config", "user.name", "Contract Fixture")
        self.write(dict(BASE))
        self.git("switch", "-q", "-c", "feature")

    def run_check(self, *extra: str, reason: str = "") -> subprocess.CompletedProcess[str]:
        env = {**ENV, "XE_RATCHET_WIDEN_REASON": reason} if reason else ENV
        return subprocess.run(
            [sys.executable, str(SCRIPT), "feature", "--repo", str(self.repo), *extra],
            env=env,
            capture_output=True,
            text=True,
            check=False,
        )

    def assert_exit(self, want: int, result: subprocess.CompletedProcess[str]) -> None:
        self.assertEqual(want, result.returncode, result.stdout + result.stderr)

    def test_an_unchanged_baseline_passes(self) -> None:
        self.write({"README.md": "docs\n"})
        result = self.run_check()
        self.assert_exit(0, result)
        self.assertIn("0 widened", result.stdout)

    def test_a_row_added_in_the_same_change_fails(self) -> None:
        for name, files, finding in (
            ("tuple", {TUPLES: BASE[TUPLES] + "c.cs|1\n"}, f"ADDED {TUPLES}: c.cs"),
            ("knip", {KNIP: json.dumps(["k1", "k2"])}, f"ADDED {KNIP}: k2"),
            ("biome", {BIOME: biome(500, "src/Long.ts", "src/Other.ts")}, f"ADDED {BIOME}: off-list|src/Other.ts"),
        ):
            with self.subTest(name):
                self.setUp()
                self.write(files)
                result = self.run_check()
                self.assert_exit(1, result)
                self.assertEqual([finding], [line for line in result.stdout.splitlines() if " widened" not in line])

    def test_a_biome_setting_outside_the_allowed_shapes_is_rejected(self) -> None:
        """biome.json is checked closed-world: anything that could switch the file-length rule for a production source
        without being one of the allowed shapes is REJECTED, whatever Biome would make of it."""
        lab = ["src/features/**"]

        def rule(value: Any) -> dict[str, Any]:
            return {"linter": {"rules": {"style": {"noExcessiveLinesPerFile": value}}}}

        def limit(max_lines: int) -> dict[str, Any]:
            return rule({"level": "error", "options": {"maxLines": max_lines}})

        def edited(edit: Any) -> str:
            config = json.loads(BASE[BIOME])
            edit(config)
            return json.dumps(config)

        def appended(*overrides: dict[str, Any]) -> Any:
            return lambda config: config["overrides"].extend(overrides)

        rejected = f"REJECTED {BIOME}: "
        two_rules = rejected + "2 overrides set noExcessiveLinesPerFile at error with maxLines; exactly one may"
        for name, edit, findings in (
            (
                "javascript linter off for a feature",
                appended({"includes": lab, "javascript": {"linter": {"enabled": False}}}),
                [rejected + "overrides[2] (src/features/**): sets javascript.linter"],
            ),
            (
                "linter off for a feature",
                appended({"includes": lab, "linter": {"enabled": False}}),
                [rejected + "overrides[2] (src/features/**): sets linter.enabled"],
            ),
            (
                "linter off for tests only",
                appended({"includes": ["src/**/*.test.ts"], "linter": {"enabled": False}}),
                [rejected + "overrides[2] (src/**/*.test.ts): sets linter.enabled"],
            ),
            (
                "file skipped by size",
                appended({"includes": lab, "files": {"maxSize": 10}}),
                [rejected + "overrides[2] (src/features/**): sets files"],
            ),
            (
                "style group off",
                appended({"includes": lab, "linter": {"rules": {"style": "off"}}}),
                [rejected + "overrides[2] (src/features/**): sets the style group"],
            ),
            (
                "recommended switched",
                appended(
                    {"includes": lab, "linter": {"rules": {"recommended": False, "style": {"recommended": False}}}}
                ),
                [
                    rejected + "overrides[2] (src/features/**): sets linter.rules.recommended",
                    rejected + "overrides[2] (src/features/**): sets linter.rules.style.recommended",
                ],
            ),
            (
                "object-form directory exemption",
                appended({"includes": lab, **rule({"level": "off", "options": {"maxLines": 0}})}),
                [rejected + "overrides[2] (src/features/**): sets linter.rules.style.noExcessiveLinesPerFile"],
            ),
            (
                "warning in a later override",
                appended({"includes": lab, **rule("warn")}),
                [rejected + "overrides[2] (src/features/**): sets linter.rules.style.noExcessiveLinesPerFile"],
            ),
            # A new include's limit is new coverage, so only the shape check catches the later 900.
            ("a later, higher limit", appended({"includes": lab, **limit(900)}), [two_rules]),
            ("a subtree tightened to 300", appended({"includes": lab, **limit(300)}), [two_rules]),
            (
                "production rule downgraded",
                lambda config: config["overrides"][0]["linter"]["rules"]["style"]["noExcessiveLinesPerFile"].update(
                    level="warn"
                ),
                [
                    f"NARROWED {BIOME}: covers|src/**",
                    rejected + "overrides[0] (src/**): sets linter.rules.style.noExcessiveLinesPerFile",
                    rejected + "0 overrides set noExcessiveLinesPerFile at error with maxLines; exactly one may",
                ],
            ),
            (
                "top-level linter and language switches",
                lambda config: config.update(
                    linter={"enabled": False, "rules": {"style": {"noExcessiveLinesPerFile": "info"}}},
                    files={"maxSize": 10},
                    javascript={"linter": {"enabled": False}},
                    extends=["./other.json"],
                ),
                [
                    rejected + "extends another configuration",
                    rejected + "linter.enabled is not true",
                    rejected + "sets files.maxSize",
                    rejected + "sets javascript.linter",
                    rejected + "top level: sets linter.rules.style.noExcessiveLinesPerFile",
                ],
            ),
        ):
            with self.subTest(name):
                self.setUp()
                self.write({BIOME: edited(edit)})
                result = self.run_check()
                self.assert_exit(1, result)
                self.assertEqual(findings, [line for line in result.stdout.splitlines() if " widened" not in line])

    def test_raising_a_subtree_limit_fails_even_under_the_global_maximum(self) -> None:
        """A limit is compared per include, so 300 -> 450 under a 500 production limit is RAISED; the second rule
        override stays REJECTED however the merge base came to hold it."""

        def configured(subtree: int) -> str:
            config = json.loads(BASE[BIOME])
            rule = {"style": {"noExcessiveLinesPerFile": {"level": "error", "options": {"maxLines": subtree}}}}
            config["overrides"].append({"includes": ["src/features/**"], "linter": {"rules": rule}})
            return json.dumps(config)

        self.git("switch", "-q", "develop")
        self.write({BIOME: configured(300)})
        self.git("switch", "-q", "-C", "feature")
        self.write({BIOME: configured(450)})
        result = self.run_check()
        self.assert_exit(1, result)
        self.assertEqual(
            [
                f"RAISED {BIOME}: maxLines|src/features/** 300 -> 450",
                f"REJECTED {BIOME}: 2 overrides set noExcessiveLinesPerFile at error with maxLines; exactly one may",
            ],
            [line for line in result.stdout.splitlines() if " widened" not in line],
        )

    def test_an_operator_ignore_file_never_refuses_a_fixture_file(self) -> None:
        """The XDG default ignore file, which hiding the global config leaves in force, matching fixture files leaves
        them staged and checked."""
        with tempfile.TemporaryDirectory() as operator:
            (Path(operator) / "git").mkdir()
            (Path(operator) / "git" / "ignore").write_text("*.json\n*.txt\n", encoding="utf-8")
            with unittest.mock.patch.dict(ENV, XDG_CONFIG_HOME=operator):
                self.setUp()
                self.write({TUPLES: "# file|count\na.cs|3\nb.cs|2\nc.cs|1\n"})
                listed = subprocess.run(
                    ["git", "-C", str(self.repo), "ls-files"], env=ENV, capture_output=True, text=True, check=True
                ).stdout.splitlines()
                result = self.run_check()
        self.assertIn(KNIP, listed)
        self.assert_exit(1, result)
        self.assertIn(f"ADDED {TUPLES}: c.cs", result.stdout)

    def test_the_dropped_git_variables_are_the_ones_the_scripts_drop(self) -> None:
        script = (SCRIPT.parent / "lib" / "git-env.sh").read_text(encoding="utf-8")
        listed = script.split("XE_GIT_REPO_ENV_VARS=(", 1)[1].split(")", 1)[0].split()
        self.assertEqual(sorted(listed), sorted(runpy.run_path(str(SCRIPT))["GIT_REPO_ENV_VARS"]))

    def test_an_interpreter_older_than_the_floor_fails_closed(self) -> None:
        """A python3 that reports 3.12 and lacks PurePosixPath.full_match, as 3.12 does, is refused by name before
        any comparison, never with a traceback."""
        with tempfile.TemporaryDirectory() as site:
            (Path(site) / "sitecustomize.py").write_text(OLD_PYTHON_SITE, encoding="utf-8")
            # The committed biome.json, so the old code reaches full_match (test_the_real_biome_configuration_passes).
            self.git("switch", "-q", "develop")
            self.write({BIOME: (SCRIPT.parents[1] / BIOME).read_text(encoding="utf-8")})
            self.git("switch", "-q", "-C", "feature")
            self.write({f"{REACT}src/features/chat/ChatPage.tsx": "export const chat = 1;\n"})
            result = subprocess.run(
                [sys.executable, str(SCRIPT), "feature", "--repo", str(self.repo)],
                env={**ENV, "PYTHONPATH": site},
                capture_output=True,
                text=True,
                check=False,
            )
        self.assert_exit(2, result)
        self.assertIn("ratchet-baseline-check: ERROR needs Python 3.13 or newer", result.stderr)
        self.assertIn("is 3.12.9", result.stderr)
        self.assertNotIn("Traceback", result.stderr)

    def test_the_real_biome_configuration_passes(self) -> None:
        real = (SCRIPT.parents[1] / BIOME).read_text(encoding="utf-8")
        self.git("switch", "-q", "develop")
        self.write({BIOME: real, f"{REACT}src/features/modelLab/ModelLabPage.tsx": "export const page = 1;\n"})
        self.git("switch", "-q", "-C", "feature")
        self.write({f"{REACT}src/features/chat/ChatPage.tsx": "export const chat = 1;\n"})
        result = self.run_check()
        self.assert_exit(0, result)
        self.assertIn("0 widened", result.stdout)

    def test_narrowing_the_error_level_rule_fails_with_its_limit_unchanged(self) -> None:
        def configured(*includes: str) -> str:
            rule = {"style": {"noExcessiveLinesPerFile": {"level": "error", "options": {"maxLines": 500}}}}
            return json.dumps({"overrides": [{"includes": list(includes), "linter": {"rules": rule}}]})

        sources = ("src/**/*.ts", "src/**/*.tsx", "!src/**/*.test.ts")
        for name, includes, findings in (
            ("TSX include dropped", ("src/**/*.ts", "!src/**/*.test.ts"), [f"NARROWED {BIOME}: covers|src/**/*.tsx"]),
            ("exclusion added", (*sources, "!src/features/**"), [f"ADDED {BIOME}: error-excludes|!src/features/**"]),
            (
                "rule removed",
                (),
                [
                    f"NARROWED {BIOME}: covers|src/**/*.ts",
                    f"NARROWED {BIOME}: covers|src/**/*.tsx",
                    f"REJECTED {BIOME}: 0 overrides set noExcessiveLinesPerFile at error with maxLines; "
                    "exactly one may",
                ],
            ),
        ):
            with self.subTest(name):
                self.setUp()
                self.git("switch", "-q", "develop")
                self.write({BIOME: configured(*sources)})
                self.git("switch", "-q", "-C", "feature")
                self.write({BIOME: configured(*includes) if includes else json.dumps({"overrides": []})})
                result = self.run_check()
                self.assert_exit(1, result)
                self.assertEqual(findings, [line for line in result.stdout.splitlines() if " widened" not in line])

        # Covering more files or dropping an exclusion narrows nothing.
        self.setUp()
        self.git("switch", "-q", "develop")
        self.write({BIOME: configured(*sources)})
        self.git("switch", "-q", "-C", "feature")
        self.write({BIOME: configured("src/**/*.ts", "src/**/*.tsx", "src/**/*.mts")})
        self.assert_exit(0, self.run_check())

    def test_a_top_level_directory_exclusion_stops_linting_its_whole_subtree(self) -> None:
        """Biome matches the top-level includes against every directory it walks and skips an excluded directory's
        subtree, so a pattern naming only the directory leaves every source below it unlinted."""
        big = "".join(f"export const a{i} = {i};\n" for i in range(600))
        sources = {
            f"{REACT}src/features/modelLab/ModelLabPage.tsx": big,
            f"{REACT}src/features/modelLab/deep/Deep.tsx": "",
            f"{REACT}src/features/chat/ChatPage.tsx": "",
        }
        below = [
            f"NARROWED {BIOME}: unenforced|src/features/modelLab/ModelLabPage.tsx",
            f"NARROWED {BIOME}: unenforced|src/features/modelLab/deep/Deep.tsx",
        ]

        def top_level(section: str, *includes: str) -> str:
            config = json.loads(BASE[BIOME])
            config[section] = {"includes": list(includes)}
            return json.dumps(config)

        for section, includes, findings in (
            ("files", ("**", "!!src/features/modelLab"), below),
            ("files", ("**", "!src/features/modelLab"), below),
            ("linter", ("**", "!src/features/modelLab"), below),
            ("linter", ("**", "!**/modelLab"), below),
            ("linter", ("**", "!src/features/model*"), below),
            ("linter", ("**", "!src/features/modelLab", "src/features/modelLab/ModelLabPage.tsx"), below),
            ("linter", ("**", "!src/features/modelLab/deep"), below[1:]),
            (
                "linter",
                ("**", "!src/features/*"),
                [*below, f"NARROWED {BIOME}: unenforced|src/features/chat/ChatPage.tsx"],
            ),
        ):
            with self.subTest(section=section, includes=includes):
                self.setUp()
                self.git("switch", "-q", "develop")
                self.write(sources)
                self.git("switch", "-q", "-C", "feature")
                self.write({BIOME: top_level(section, *includes)})
                result = self.run_check()
                self.assert_exit(1, result)
                self.assertEqual(
                    sorted(findings), sorted(line for line in result.stdout.splitlines() if " widened" not in line)
                )

        # A shape the guards do not model is rejected, whatever it would match.
        for section, pattern in (
            ("linter", "!src/features/{modelLab,chat}"),
            ("files", "./src/**"),
            ("linter", "src/"),
            ("linter", "!src//features"),
            ("linter", "!src/../src/features"),
            ("files", "!!!src/features"),
            ("linter", "/src/**"),
        ):
            with self.subTest(section=section, pattern=pattern):
                self.setUp()
                self.write({BIOME: top_level(section, "**", pattern)})
                result = self.run_check()
                self.assert_exit(1, result)
                self.assertIn(
                    f"REJECTED {BIOME}: {section}.includes: {json.dumps(pattern)} is not an include shape the guards "
                    "model",
                    result.stdout,
                )

    def test_a_top_level_include_or_exclusion_that_stops_linting_a_source_fails(self) -> None:
        sources = {f"{REACT}src/Main.ts": "", f"{REACT}src/features/Page.tsx": "", f"{REACT}src/Main.test.ts": ""}

        def top_level(section: str, *includes: str) -> str:
            config = json.loads(BASE[BIOME])
            config[section] = {"includes": list(includes)}
            return json.dumps(config)

        for name, config, findings in (
            (
                "files.includes without TSX",
                top_level("files", "src/**/*.ts"),
                [f"NARROWED {BIOME}: unenforced|src/features/Page.tsx"],
            ),
            (
                "linter.includes exclusion",
                top_level("linter", "**", "!src/features/**"),
                [f"NARROWED {BIOME}: unenforced|src/features/Page.tsx"],
            ),
        ):
            with self.subTest(name):
                self.setUp()
                self.git("switch", "-q", "develop")
                self.write(sources)
                self.git("switch", "-q", "-C", "feature")
                self.write({BIOME: config})
                result = self.run_check()
                self.assert_exit(1, result)
                self.assertEqual(findings, [line for line in result.stdout.splitlines() if " widened" not in line])

        # Excluding files that are no TypeScript source, or adding a source under an exclusion the base had, narrows
        # nothing.
        self.setUp()
        self.git("switch", "-q", "develop")
        self.write({**sources, BIOME: top_level("linter", "**", "!**/*.d.ts")})
        self.git("switch", "-q", "-C", "feature")
        config = json.loads(top_level("linter", "**", "!**/*.d.ts"))
        config["files"] = {"includes": ["**", "!**/pnpm-lock.yaml"]}
        self.write({f"{REACT}src/types.d.ts": "", BIOME: json.dumps(config)})
        self.assert_exit(0, self.run_check())

    def test_a_nested_biome_configuration_fails(self) -> None:
        """A nested biome.json or biome.jsonc with ``root: false`` can switch linting off for its subtree; an oversized
        component beside it would pass both file-length guards."""
        lab = f"{REACT}src/features/modelLab/"
        nested = json.dumps({"root": False, "linter": {"enabled": False}})
        for name in ("biome.json", "biome.jsonc"):
            with self.subTest(name):
                self.setUp()
                self.git("switch", "-q", "develop")
                self.write({BIOME: biome(500, "src/Long.ts")})
                self.git("switch", "-q", "-C", "feature")
                self.write({lab + name: nested, lab + "ModelLabPage.tsx": "export const line = 1;\n" * 600})
                result = self.run_check()
                self.assert_exit(1, result)
                self.assertIn(f"ADDED {lab}{name}: nested Biome configuration", result.stdout)

    def test_a_tracked_source_ignored_by_git_or_a_biome_ignore_file_fails(self) -> None:
        """Biome's vcs.useIgnoreFile skips a tracked file a .gitignore lists, and Biome reads .ignore files git does
        not; an oversized component hidden either way would pass both file-length guards."""
        page = "src/features/lab/LabPage.tsx"
        for name, ignore_file, line, finding in (
            ("React .gitignore", f"{REACT}.gitignore", page, f"ADDED {REACT}{page}: ignored by git, so Biome skips it"),
            (
                "nested .gitignore",
                f"{REACT}src/features/.gitignore",
                "lab/",
                f"ADDED {REACT}{page}: ignored by git, so Biome skips it",
            ),
            (
                "repository .gitignore",
                ".gitignore",
                REACT + page,
                f"ADDED {REACT}{page}: ignored by git, so Biome skips it",
            ),
            (
                "nested .ignore",
                f"{REACT}src/features/.ignore",
                "lab/",
                f"ADDED {REACT}src/features/.ignore: Biome ignore file",
            ),
        ):
            with self.subTest(name):
                self.setUp()
                self.write({REACT + page: "export const line = 1;\n" * 600})
                self.write({ignore_file: line + "\n"})
                result = self.run_check()
                self.assert_exit(1, result)
                self.assertEqual([finding], [line for line in result.stdout.splitlines() if " widened" not in line])

        # Ignoring a file that is no TypeScript source, or an untracked one, widens nothing.
        self.setUp()
        self.write(
            {f"{REACT}.gitignore": "dist/\nsrc/features/lab/Scratch.tsx\n", REACT + page: "export const a = 1;\n"}
        )
        self.assert_exit(0, self.run_check())

    def test_a_covered_source_above_biomes_size_limit_fails(self) -> None:
        """Biome skips a file above its 1 MiB files.maxSize with a warning, so a source over both thresholds would pass
        the line limit unread; a generated file the rule excludes may be that large."""
        real = (SCRIPT.parents[1] / BIOME).read_text(encoding="utf-8")
        page, generated = "src/features/lab/LabPage.tsx", "src/core/api/generated/types.gen.ts"
        oversized = f"export const line = 1; // {'x' * 1900}\n" * 600
        self.git("switch", "-q", "develop")
        self.write({BIOME: real})
        self.git("switch", "-q", "-C", "feature")
        # A declaration file the top-level linter includes leave out is never linted, whatever its size.
        declarations = "src/types/vendor.d.ts"
        self.write(
            {REACT + generated: oversized, REACT + declarations: oversized, REACT + page: "export const a = 1;\n"}
        )
        self.assert_exit(0, self.run_check())
        self.write({REACT + page: oversized})
        result = self.run_check()
        self.assert_exit(1, result)
        self.assertEqual(
            [f"ADDED {REACT}{page}: {len(oversized)} bytes, over Biome's files.maxSize 1048576, so Biome skips it"],
            [line for line in result.stdout.splitlines() if " widened" not in line],
        )

    def test_an_off_list_exemption_moved_into_a_source_suppression_fails(self) -> None:
        """A suppression comment naming the file-length rule, its style group or the lint group exempts a covered
        source from the rule; deleting the off-list row while adding one would read as retired debt."""
        real = json.loads((SCRIPT.parents[1] / BIOME).read_text(encoding="utf-8"))
        page, test = "src/features/lab/LabPage.tsx", "src/features/lab/LabPage.test.tsx"
        body = "export const line = 1;\n" * 600

        def with_off_list(*entries: str) -> str:
            config = json.loads(json.dumps(real))
            off = next(o for o in config["overrides"] if rule_of(o) == "off")
            off["includes"] = list(entries)
            return json.dumps(config)

        def rule_of(override: dict[str, Any]) -> object:
            return override.get("linter", {}).get("rules", {}).get("style", {}).get("noExcessiveLinesPerFile")

        finding = f"ADDED {REACT}{page}: a source comment suppresses noExcessiveLinesPerFile"
        for comment in (
            "// biome-ignore-all lint/style/noExcessiveLinesPerFile: moved from the off-list",
            "// biome-ignore-all lint: reason",
            "/* biome-ignore-start lint/correctness/noUnusedVariables lint/style: reason */",
            "{/* biome-ignore lint/style/noExcessiveLinesPerFile(x): reason */}",
            "// BIOME-IGNORE-ALL LINT/STYLE/NOEXCESSIVELINESPERFILE: reason",
        ):
            with self.subTest(comment):
                self.setUp()
                self.git("switch", "-q", "develop")
                self.write({BIOME: with_off_list(page), REACT + page: body})
                self.git("switch", "-q", "-C", "feature")
                self.write({BIOME: with_off_list(), REACT + page: f"{comment}\n{body}"})
                result = self.run_check()
                self.assert_exit(1, result)
                self.assertEqual([finding], [line for line in result.stdout.splitlines() if " widened" not in line])

        # Another rule's suppression, or one in a file the rule excludes, widens nothing.
        self.setUp()
        self.git("switch", "-q", "develop")
        self.write({BIOME: json.dumps(real)})
        self.git("switch", "-q", "-C", "feature")
        self.write(
            {
                REACT + page: "// biome-ignore-all lint/style/useNamingConvention: reason\nexport const a = 1;\n",
                REACT + test: "// biome-ignore-all lint: reason\nexport const a = 1;\n",
            }
        )
        self.assert_exit(0, self.run_check())

    def test_a_name_git_would_quote_is_judged_like_any_other(self) -> None:
        """git quotes a path holding a non-ASCII byte, a tab, a line break, a quote or a backslash unless asked for NUL
        records; a source, configuration or ignore file under such a directory is read whole."""
        real = (SCRIPT.parents[1] / BIOME).read_text(encoding="utf-8")
        oversized = f"export const line = 1; // {'x' * 1900}\n" * 600
        too_large = f"{len(oversized)} bytes, over Biome's files.maxSize 1048576, so Biome skips it"
        suppressed = "// biome-ignore-all lint/style/noExcessiveLinesPerFile: reason\nexport const a = 1;\n"
        for directory in ("src/features/Größe", 'src/features/tab\tand"quote\\', "src/features/line\nbreak"):
            page, other = f"{directory}/LabPage.tsx", f"{directory}/Other.tsx"
            cases: tuple[tuple[str, dict[str, str], str], ...] = (
                (
                    "oversized",
                    {REACT + page: oversized},
                    f"ADDED {REACT}{page}: {too_large}",
                ),
                (
                    "suppression",
                    {REACT + page: suppressed},
                    f"ADDED {REACT}{page}: a source comment suppresses noExcessiveLinesPerFile",
                ),
                (
                    "ignore file",
                    {f"{REACT}{directory}/.ignore": "*\n"},
                    f"ADDED {REACT}{directory}/.ignore: Biome ignore file",
                ),
                (
                    "nested configuration",
                    {f"{REACT}{directory}/biome.json": "{}"},
                    f"ADDED {REACT}{directory}/biome.json: nested Biome configuration",
                ),
                (
                    "nested .gitignore",
                    {f"{REACT}{directory}/.gitignore": "Other.tsx\n"},
                    f"ADDED {REACT}{other}: ignored by git, so Biome skips it",
                ),
            )
            for name, files, finding in cases:
                with self.subTest(directory=directory, case=name):
                    self.setUp()
                    self.git("switch", "-q", "develop")
                    self.write({BIOME: real})
                    self.git("switch", "-q", "-C", "feature")
                    # Tracked first: the ignore file would keep git add from staging it.
                    self.write({REACT + other: "export const a = 1;\n"})
                    self.write(files)
                    result = self.run_check()
                    self.assert_exit(1, result)
                    self.assertIn(finding + "\n", result.stdout)
                    self.assertIn("1 widened", result.stdout)
        # An Architecture allowlist this script does not know, under a quoted name, still stops the comparison.
        self.setUp()
        self.write({ARCH + "GrößeAllowlist.txt": "x\n"})
        self.git("branch", "-f", "develop", "feature")
        result = self.run_check()
        self.assert_exit(2, result)
        self.assertIn(f"unknown allowlist {ARCH}GrößeAllowlist.txt", result.stderr)

    def test_a_same_count_replacement_fails_on_the_new_key(self) -> None:
        self.write({RECORDS: "y.cs|T\n"})
        result = self.run_check()
        self.assert_exit(1, result)
        self.assertIn(f"ADDED {RECORDS}: y.cs|T", result.stdout)
        self.assertNotIn("x.cs", result.stdout)

    def test_a_raised_limit_fails(self) -> None:
        for name, files, finding in (
            ("ceiling", {TUPLES: "a.cs|4\nb.cs|2\n"}, f"RAISED {TUPLES}: a.cs 3 -> 4"),
            ("listed twice", {RECORDS: "x.cs|T\nx.cs|T\n"}, f"RAISED {RECORDS}: x.cs|T 1 -> 2"),
            ("maxLines", {BIOME: biome(600, "src/Long.ts")}, f"RAISED {BIOME}: maxLines|src/** 500 -> 600"),
        ):
            with self.subTest(name):
                self.setUp()
                self.write(files)
                result = self.run_check()
                self.assert_exit(1, result)
                self.assertIn(finding, result.stdout)

    def test_a_deletion_a_lowered_limit_and_a_rewritten_reason_pass(self) -> None:
        self.write(
            {
                TUPLES: "a.cs|2\n",
                KNIP: "[]",
                BIOME: biome(400),
                ARCH + "TryOutParameterNullOnSuccessAllowlist.txt": "N.T.TryGet(value)  # reworded reason\n",
            }
        )
        self.assert_exit(0, self.run_check())

    def test_a_new_baseline_file_is_allowed_once_and_named(self) -> None:
        self.write({ARCH + "FileSizeAllowlist.txt": "big.cs|1100\n"})
        result = self.run_check()
        self.assert_exit(0, result)
        self.assertIn(f"NEW {ARCH}FileSizeAllowlist.txt: a new baseline of 1 keys, allowed once", result.stdout)

        self.write({ARCH + "FileSizeAllowlist.txt": "big.cs|1100\nhuge.cs|1200\n"})
        self.git("switch", "-q", "develop")
        self.git("merge", "-q", "--ff-only", "feature~1")
        self.git("switch", "-q", "feature")
        self.assert_exit(1, self.run_check())

    def test_an_unavailable_merge_base_fails_closed(self) -> None:
        self.git("switch", "-q", "--orphan", "feature-orphan")
        self.write({TUPLES: "a.cs|1\n"})
        self.git("branch", "-q", "-f", "feature", "feature-orphan")
        result = self.run_check()
        self.assert_exit(2, result)
        self.assertIn("merge-base", result.stderr)

    def test_an_unknown_allowlist_at_the_merge_base_or_a_malformed_row_stops_the_check(self) -> None:
        for name, files in (
            ("unknown at the merge base", {ARCH + "NewGuardAllowlist.txt": "x\n"}),
            ("malformed", {TUPLES: "a.cs|many\n"}),
        ):
            with self.subTest(name):
                self.setUp()
                self.write(files)
                if name.startswith("unknown"):
                    self.git("branch", "-f", "develop", "feature")
                self.assert_exit(2, self.run_check())

    def test_a_new_guard_list_with_its_parser_entry_only_on_the_branch_is_allowed_once(self) -> None:
        # The guard runs develop's copy of this script, so the parser entry the branch adds is not the running one.
        new_list = ARCH + "NewGuardAllowlist.txt"
        schema = SCRIPT.read_text(encoding="utf-8").replace(
            '    "scripts/docs-inventory-ratchet.txt": (2, 2),\n',
            f'    "scripts/docs-inventory-ratchet.txt": (2, 2),\n    "{new_list}": (1, None),\n',
        )
        self.assertIn(new_list, schema)
        self.write({new_list: "Some.Type\n", "scripts/ratchet-baseline-check.py": schema})
        result = self.run_check()
        self.assert_exit(0, result)
        self.assertIn(f"NEW (unparsed by this checker) {new_list}: a new baseline, allowed once", result.stdout)
        self.assertIn("0 widened", result.stdout)

        self.write({TUPLES: BASE[TUPLES] + "c.cs|1\n"})
        result = self.run_check()
        self.assert_exit(1, result)
        self.assertIn(f"ADDED {TUPLES}: c.cs", result.stdout)

    def test_the_operator_reason_accepts_a_widening_and_is_printed(self) -> None:
        self.write({TUPLES: BASE[TUPLES] + "c.cs|1\n"})
        result = self.run_check(reason="file moved under the same count")
        self.assert_exit(0, result)
        self.assertIn(f"ADDED {TUPLES}: c.cs", result.stdout)
        self.assertIn("accepted by XE_RATCHET_WIDEN_REASON: file moved under the same count", result.stdout)


if __name__ == "__main__":
    unittest.main()
