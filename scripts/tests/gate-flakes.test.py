#!/usr/bin/env python3
"""Contract test for scripts/gate-flakes.py against a fixture gate history."""

from __future__ import annotations

import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "gate-flakes.py"


def backend(
    time: str,
    tip: str,
    exit_code: int,
    failed: str = "-",
    scope: str = "full",
    reason: str = "",
    worktree: str = "/wt",
    skipped: int = 0,
) -> str:
    return "\t".join(
        (
            time,
            worktree,
            tip,
            f"Tests=10/0/{skipped}",
            "Persistence=1/0/0",
            "Agent=1/0/0",
            "Cli=1/0/0",
            "Contracts=skipped",
            f"scope={scope}",
            "wall=400",
            f"reason={reason or ('ok' if exit_code == 0 else 'tests')}",
            f"skips={'live-gated:1' if skipped else ''}",
            "unreported=0",
            f"failed={failed}",
            "flakes=-",
            "ratchets=Tuple:1",
            "lanes=Tests:300",
            f"exit={exit_code}",
        )
    )


def lane(time: str, tip: str, exit_code: int) -> str:
    return "\t".join((time, "/wt", tip, "lane=frontend", "wall=60", f"lane-exit={exit_code}"))


class GateFlakesTests(unittest.TestCase):
    def run_script(self, rows: list[str], flakes: str = "") -> subprocess.CompletedProcess[str]:
        with tempfile.TemporaryDirectory() as root:
            log = Path(root) / "gate-history.log"
            log.write_text("\n".join(rows) + "\n", encoding="utf-8")
            known = Path(root) / "known-flakes.txt"
            known.write_text("# header|x|y\n" + flakes, encoding="utf-8")
            return subprocess.run(
                [sys.executable, str(SCRIPT), "--log", str(log), "--flakes", str(known)],
                capture_output=True,
                text=True,
                timeout=30,
                check=False,
            )

    def test_red_then_green_on_one_clean_tip_prints_each_failed_test_once(self) -> None:
        result = self.run_script(
            [
                backend("T1", "aaa", 1, failed="Flaky_A,Flaky_B"),
                backend("T2", "aaa", 1, failed="Flaky_A"),
                backend("T3", "aaa", 0),
            ],
            flakes="Flaky_B|2026-11-26|note\n",
        )
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(
            ["Flaky_A\tT1\tT3\taaa\t/wt\tUNLISTED", "Flaky_B\tT1\tT3\taaa\t/wt\tlisted"],
            result.stdout.splitlines(),
        )

    def test_green_before_red_on_one_clean_tip_counts_too(self) -> None:
        result = self.run_script([backend("T1", "aaa", 0), backend("T2", "aaa", 1, failed="Flaky_A")])
        self.assertEqual(["Flaky_A\tT2\tT1\taaa\t/wt\tUNLISTED"], result.stdout.splitlines())

    def test_nothing_proves_a_flake_without_a_full_green_on_the_same_clean_tip(self) -> None:
        result = self.run_script(
            [
                # Red on a dirty or moved tip: the code that failed is not a commit.
                backend("T1", "bbb+dirty", 1, failed="Dirty_A"),
                backend("T2", "bbb", 0),
                backend("T3", "ccc+moved", 1, failed="Moved_A"),
                backend("T4", "ccc", 0),
                # Green only on another tip: a fix may have landed in between.
                backend("T5", "ddd", 1, failed="Fixed_A"),
                backend("T6", "eee", 0),
                # A partial green may not have run the test at all.
                backend("T7", "fff", 1, failed="Partial_A"),
                backend("T8", "fff", 0, scope="shard"),
                backend("T9", "fff", 0, scope="siblings-only"),
                # A red with no failed test names (lock timeout, build red) names nothing.
                backend("T10", "ggg", 69),
                backend("T11", "ggg", 0),
                # A contaminated, killed or lock-timeout run certifies nothing, whatever failed= names.
                backend("T14", "iii", 75, failed="Contaminated_A", reason="contaminated"),
                backend("T15", "iii", 143, failed="Killed_A", reason="killed"),
                backend("T16", "iii", 69, failed="Locked_A", reason="lock-timeout"),
                backend("T17", "iii", 0),
                # Lane rows are not backend runs.
                lane("T12", "hhh", 1),
                lane("T13", "hhh", 0),
            ]
        )
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", result.stdout)

    def test_a_no_build_green_proves_no_flake(self) -> None:
        # NO_BUILD=1 records scope=no-build before --siblings-only or TEST_SHARD, so the run may have skipped the test.
        result = self.run_script(
            [backend("T1", "aaa", 1, failed="Partial_A"), backend("T2", "aaa", 0, scope="no-build")]
        )
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", result.stdout)

    def test_a_no_build_red_proves_no_flake(self) -> None:
        # A no-build run may test binaries built from the previous commit while recording the current tip.
        result = self.run_script([backend("T1", "aaa", 1, failed="Stale_A", scope="no-build"), backend("T2", "aaa", 0)])
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", result.stdout)

    def test_an_enabled_red_then_a_green_that_skipped_tests_is_only_a_candidate(self) -> None:
        # The red run enabled a live-gated test; the default full run skipped it, so it proves nothing about that test.
        result = self.run_script(
            [
                backend("T1", "aaa", 1, failed="Live_A,Listed_A"),
                backend("T2", "aaa", 0, skipped=1),
            ],
            flakes="Listed_A|2026-11-26|note\n",
        )
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(
            ["Live_A\tT1\tT2\taaa\t/wt\tCANDIDATE", "Listed_A\tT1\tT2\taaa\t/wt\tlisted"],
            result.stdout.splitlines(),
        )

    def test_a_green_that_skipped_nothing_wins_over_a_nearer_one_that_skipped_tests(self) -> None:
        result = self.run_script(
            [backend("T1", "aaa", 0), backend("T2", "aaa", 1, failed="Flaky_A"), backend("T3", "aaa", 0, skipped=2)]
        )
        self.assertEqual(["Flaky_A\tT2\tT1\taaa\t/wt\tUNLISTED"], result.stdout.splitlines())

    def test_an_unreadable_log_exits_two(self) -> None:
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--log", "/nonexistent/gate-history.log"],
            capture_output=True,
            text=True,
            timeout=30,
            check=False,
        )
        self.assertEqual(2, result.returncode)
        self.assertIn("cannot read", result.stderr)


if __name__ == "__main__":
    unittest.main()
