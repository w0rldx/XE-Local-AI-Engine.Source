"""Unit tests for scripts/test-durations.py.

`unittest.TestCase` rather than bare pytest functions for the reason spelled out in
test_docs_inventory_check.py: two runners execute this file, `python-quality` under pytest and
scripts/run-release-contract-tests.sh as a bare `python3 <file>` that demands a non-vacuous
`Ran N tests` / `OK`. The subject's filename is not a valid module name, so it is loaded through
importlib the same way.

The fixture is a synthetic two-file TRX set written to a temporary directory: no network, no sleeps,
no dependency on a real test run. The load-bearing case is `--heavy`, whose lines are fed back
through the exact `sed` regex `scripts/run-tests-memory-safe.sh` uses to parse its packer weights
(docs/agent-knowledge.md section 1) — a format drift there silently degrades the pack to
round-robin, which is why it is asserted mechanically rather than by eye.
"""

from __future__ import annotations

import contextlib
import importlib.util
import io
import re
import sys
import tempfile
import unittest
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = REPO_ROOT / "scripts" / "test-durations.py"
SPEC = importlib.util.spec_from_file_location("test_durations", MODULE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"could not load {MODULE_PATH}")
MODULE: Any = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)

# The weight parse in scripts/run-tests-memory-safe.sh, transcribed verbatim from its sed program.
# The transcription is only valid while the runner still carries that program, which
# test_the_runner_still_carries_the_transcribed_weight_parse asserts — otherwise a sed edit on the
# runner's side would leave this file green while the pack degraded to round-robin.
SED_PROGRAM = r"""sed -nE 's/^  ([A-Za-z0-9_.]+) +# *([0-9]+)s.*/\1 \2/p'"""
RUNNER_WEIGHT_PATTERN = re.compile(r"^  ([A-Za-z0-9_.]+) +# *([0-9]+)s.*")
RUNNER_PATH = REPO_ROOT / "scripts" / "run-tests-memory-safe.sh"

TRX_TEMPLATE = """<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <TestDefinitions>
{definitions}
  </TestDefinitions>
  <Results>
{results}
  </Results>
</TestRun>
"""


def write_trx(path: Path, rows: list[tuple[str, str, str, str]], *, omit_definitions: bool = False) -> None:
    """Write a TRX holding `rows` of (class name, test name, duration, outcome).

    `omit_definitions` writes the results without their `UnitTest` entries, i.e. results whose class
    name cannot be resolved.
    """
    definitions = []
    results = []
    for index, (class_name, test_name, duration, outcome) in enumerate(rows):
        test_id = f"{index:08d}-0000-0000-0000-000000000000"
        definitions.append(
            f'    <UnitTest id="{test_id}" name="{test_name}">'
            f'<TestMethod className="{class_name}" name="{test_name}" /></UnitTest>'
        )
        results.append(
            f'    <UnitTestResult testId="{test_id}" testName="{test_name}" '
            f'duration="{duration}" outcome="{outcome}" />'
        )
    path.write_text(
        TRX_TEMPLATE.format(definitions="" if omit_definitions else "\n".join(definitions), results="\n".join(results)),
        encoding="utf-8",
    )


def invoke(argv: list[str]) -> tuple[str, str]:
    stdout, stderr = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
        exit_code = MODULE.main(argv)
    if exit_code != 0:
        raise AssertionError(f"exit {exit_code}: {stdout.getvalue()}")
    return stdout.getvalue(), stderr.getvalue()


def run(argv: list[str]) -> str:
    return invoke(argv)[0]


class TestDurationsTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.root = Path(self._tmp.name)
        (self.root / "Alpha").mkdir()
        (self.root / "Beta").mkdir()
        write_trx(
            self.root / "Alpha" / "run.trx",
            [
                ("Suite.Tests.Alpha.HeavyTests", "SlowOne", "00:00:20.0000000", "Passed"),
                ("Suite.Tests.Alpha.HeavyTests", "SlowTwo", "00:00:10.0000000", "Failed"),
                ("Suite.Tests.Alpha.HeavyTests", "NeverRan", "00:00:00.0000000", "NotExecuted"),
            ],
        )
        write_trx(
            self.root / "Beta" / "run.trx",
            [
                ("Suite.Tests.Beta.LightTests", "Quick", "00:00:01.5000000", "Passed"),
                ("Suite.Tests.Beta.LightTests", "Quicker", "00:00:00.5000000", "Passed"),
            ],
        )

    def test_per_class_table_sums_ranks_and_ignores_unexecuted_results(self) -> None:
        out = run([str(self.root), "--strip-prefix", "Suite.Tests."])

        self.assertIn("tests=4 sum_test_seconds=32 classes=2", out)
        lines = out.split("(s | tests | s/test | class)")[1].splitlines()[1:3]
        self.assertIn("15.00", lines[0])  # 30 s over the two executed tests, not three
        self.assertTrue(lines[0].endswith("Alpha.HeavyTests"), lines[0])
        self.assertTrue(lines[1].endswith("Beta.LightTests"), lines[1])

    def test_top_tests_listing_is_ordered_and_capped(self) -> None:
        out = run([str(self.root), "--top-tests", "2", "--strip-prefix", "Suite.Tests."])

        section = out.split("## Top 2 single tests")[1].splitlines()[1:3]
        self.assertTrue(section[0].strip().startswith("20.0"), section)
        self.assertIn("Alpha.HeavyTests.SlowOne", section[0])
        self.assertIn("Alpha.HeavyTests.SlowTwo", section[1])

    def test_heavy_lines_parse_with_the_runners_own_weight_regex(self) -> None:
        out = run([str(self.root), "--heavy"])

        parsed = [RUNNER_WEIGHT_PATTERN.match(line) for line in out.splitlines()]
        self.assertTrue(all(parsed), out)
        self.assertEqual([("Suite.Tests.Alpha", "30")], [match.groups() for match in parsed if match])

    def test_heavy_drops_namespaces_below_the_runners_cut_off(self) -> None:
        # Beta sums to 2 s and must not appear: everything unlisted weighs 1 in the packer anyway.
        out = run([str(self.root), "--heavy"])

        self.assertNotIn("Suite.Tests.Beta", out)

    def test_counts_reports_the_files_and_namespaces_seen(self) -> None:
        out = run([str(self.root), "--counts"])

        self.assertEqual("files=2 tests=4 classes=2 namespaces=2 sum_test_seconds=32", out.strip())

    def test_a_trx_set_with_no_executed_result_fails_instead_of_printing_zeroes(self) -> None:
        empty = self.root / "Empty"
        empty.mkdir()
        write_trx(empty / "run.trx", [("Suite.Tests.Gamma.Tests", "Skipped", "00:00:00.0000000", "NotExecuted")])

        with self.assertRaises(SystemExit) as caught:
            run([str(empty)])

        self.assertIn("contained no executed test result", str(caught.exception))

    def test_the_runner_still_carries_the_transcribed_weight_parse(self) -> None:
        # Without this, a sed edit on the runner's side leaves the format assertions above green
        # while the packer silently falls back to round-robin (docs/agent-knowledge.md section 1).
        self.assertIn(SED_PROGRAM, RUNNER_PATH.read_text(encoding="utf-8"))

    def test_heavy_refuses_a_result_whose_class_never_resolved(self) -> None:
        orphan = self.root / "Orphan"
        orphan.mkdir()
        write_trx(
            orphan / "run.trx",
            [("Suite.Tests.Gamma.Tests", "Loose", "00:00:30.0000000", "Passed")],
            omit_definitions=True,
        )

        out, err = invoke([str(orphan), "--heavy"])

        self.assertEqual("", out)
        self.assertIn("no resolvable namespace", err)

    def test_heavy_and_counts_cannot_be_combined(self) -> None:
        with self.assertRaises(SystemExit) as caught:
            run([str(self.root), "--heavy", "--counts"])

        self.assertEqual(2, caught.exception.code)

    def test_a_missing_path_is_rejected(self) -> None:
        with self.assertRaises(SystemExit) as caught:
            run([str(self.root / "does-not-exist")])

        self.assertEqual(2, caught.exception.code)


if __name__ == "__main__":
    unittest.main()
