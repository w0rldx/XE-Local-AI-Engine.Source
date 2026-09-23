#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = REPO_ROOT / "scripts" / "release" / "dev-build-identity.py"
SPEC = importlib.util.spec_from_file_location("dev_build_identity", MODULE_PATH)
assert SPEC is not None
assert SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

ANCHOR_TAG = "v1.0.0-rc.2"
ANCHOR_VERSION = "1.0.0-rc.2"
DATE = "20260922"


def tag(counter: int, date: str = DATE, anchor: str = ANCHOR_VERSION) -> str:
    return f"dev/{MODULE.compose_dev_version(anchor, date, counter)}"


class VersionRuleTests(unittest.TestCase):
    def test_prerelease_anchor_extends_the_rc_label_with_a_dot(self) -> None:
        self.assertEqual("1.0.0-rc.2.dev.20260922.1", MODULE.compose_dev_version("1.0.0-rc.2", DATE, 1))

    def test_stable_anchor_bumps_the_patch(self) -> None:
        self.assertEqual("1.0.1-dev.20260922.1", MODULE.compose_dev_version("1.0.0", DATE, 1))
        self.assertEqual("1.2.10-dev.20260922.4", MODULE.compose_dev_version("1.2.9", DATE, 4))

    def test_no_composed_version_ever_contains_two_hyphens(self) -> None:
        for anchor in ("1.0.0-rc.2", "1.0.0", "2.4.7-rc.11"):
            for counter in (1, 9, 10, 123):
                with self.subTest(anchor=anchor, counter=counter):
                    self.assertEqual(1, MODULE.compose_dev_version(anchor, DATE, counter).count("-"))

    def test_a_double_hyphen_anchor_is_refused(self) -> None:
        with self.assertRaisesRegex(ValueError, "hyphens"):
            MODULE.compose_dev_version("1.0.0-rc.2-hotfix", DATE, 1)

    def test_a_malformed_anchor_is_refused(self) -> None:
        with self.assertRaises(ValueError):
            MODULE.compose_dev_version("not-a-version", DATE, 1)

    def test_split_version_rejects_a_non_dev_tag(self) -> None:
        with self.assertRaises(ValueError):
            MODULE.split_version("v1.0.0-rc.2")


class CounterTests(unittest.TestCase):
    def prefix(self, date: str = DATE, anchor: str = ANCHOR_VERSION) -> str:
        return MODULE.dev_version_prefix(anchor, date)

    def test_counter_increments_within_the_same_day_and_anchor(self) -> None:
        existing = [MODULE.compose_dev_version(ANCHOR_VERSION, DATE, n) for n in (1, 2, 3)]
        self.assertEqual(4, MODULE.next_counter(existing, self.prefix()))

    def test_counter_resets_on_a_new_date(self) -> None:
        existing = [MODULE.compose_dev_version(ANCHOR_VERSION, DATE, 3)]
        self.assertEqual(1, MODULE.next_counter(existing, self.prefix(date="20260923")))

    def test_counter_ignores_a_different_anchor(self) -> None:
        existing = [MODULE.compose_dev_version("1.0.0-rc.1", DATE, 5)]
        self.assertEqual(1, MODULE.next_counter(existing, self.prefix()))

    def test_counter_is_numeric_not_lexical(self) -> None:
        existing = [MODULE.compose_dev_version(ANCHOR_VERSION, DATE, n) for n in (1, 9)]
        self.assertEqual(10, MODULE.next_counter(existing, self.prefix()))


class IdentityTests(unittest.TestCase):
    def identity(self, dev_tags: list[str], tag_shas: dict[str, str], current_sha: str) -> dict[str, object]:
        return MODULE.compute_identity(
            anchor_tag=ANCHOR_TAG,
            anchor_version=ANCHOR_VERSION,
            dev_tags=dev_tags,
            tag_shas=tag_shas,
            current_sha=current_sha,
            date=DATE,
        )

    def test_same_sha_as_the_previous_dev_tag_skips(self) -> None:
        result = self.identity([tag(1)], {tag(1): "aaa"}, "aaa")
        self.assertIs(True, result["skip"])

    def test_a_new_sha_does_not_skip(self) -> None:
        result = self.identity([tag(1)], {tag(1): "aaa"}, "bbb")
        self.assertIs(False, result["skip"])
        self.assertEqual("aaa", result["previous_dev_sha"])
        self.assertEqual("aaa", result["notes_base_ref"])
        self.assertEqual("1.0.0-rc.2.dev.20260922.2", result["dev_version"])

    def test_first_build_bases_notes_on_the_newest_v_tag(self) -> None:
        result = self.identity([], {}, "bbb")
        self.assertIsNone(result["previous_dev_tag"])
        self.assertIsNone(result["previous_dev_sha"])
        self.assertEqual(ANCHOR_TAG, result["notes_base_ref"])
        self.assertEqual("1.0.0-rc.2.dev.20260922.1", result["dev_version"])
        self.assertEqual("dev/1.0.0-rc.2.dev.20260922.1", result["dev_tag"])
        self.assertIs(False, result["skip"])

    def test_previous_dev_tag_is_the_highest_version_not_the_last_listed(self) -> None:
        listed = [tag(10), tag(9), tag(1)]
        result = self.identity(listed, {tag(10): "aaa"}, "bbb")
        self.assertEqual(tag(10), result["previous_dev_tag"])
        self.assertEqual("aaa", result["previous_dev_sha"])

    def test_a_newer_date_outranks_a_higher_counter(self) -> None:
        listed = [tag(9), tag(1, date="20260923")]
        self.assertEqual(tag(1, date="20260923"), MODULE.newest_dev_tag(listed))

    def test_a_higher_rc_anchor_wins_within_one_date(self) -> None:
        # A string tail-break sorts rc.10 below rc.9 and names the wrong previous Development tag.
        listed = [tag(1, anchor="1.0.0-rc.9"), tag(1, anchor="1.0.0-rc.10")]
        self.assertEqual(tag(1, anchor="1.0.0-rc.10"), MODULE.newest_dev_tag(listed))

    def test_a_stable_anchor_outranks_every_rc_of_the_same_version(self) -> None:
        listed = [tag(5, anchor="1.0.1-rc.7"), tag(1, anchor="1.0.0")]
        self.assertEqual(tag(1, anchor="1.0.0"), MODULE.newest_dev_tag(listed))

    def test_the_build_date_still_dominates_the_anchor(self) -> None:
        listed = [tag(9, anchor="1.0.0-rc.10"), tag(1, date="20260923", anchor="1.0.0-rc.1")]
        self.assertEqual(tag(1, date="20260923", anchor="1.0.0-rc.1"), MODULE.newest_dev_tag(listed))


class PruneTests(unittest.TestCase):
    def test_prune_keeps_the_newest_thirty(self) -> None:
        tags = [tag(n) for n in range(1, 36)]
        delete = MODULE.select_prune(tags, MODULE.DEFAULT_KEEP)
        self.assertEqual(5, len(delete))
        kept = [t for t in tags if t not in delete]
        newest_deleted = max(MODULE.sort_key(MODULE.split_version(t)) for t in delete)
        oldest_kept = min(MODULE.sort_key(MODULE.split_version(t)) for t in kept)
        self.assertLess(newest_deleted, oldest_kept)

    def test_prune_keeps_everything_below_the_threshold(self) -> None:
        tags = [tag(n) for n in range(1, 31)]
        self.assertEqual([], MODULE.select_prune(tags, MODULE.DEFAULT_KEEP))

    def test_prune_refuses_a_non_dev_tag(self) -> None:
        with self.assertRaises(ValueError):
            MODULE.select_prune([tag(1), "v1.0.0-rc.2"], MODULE.DEFAULT_KEEP)

    def test_prune_default_keep_is_thirty(self) -> None:
        self.assertEqual(30, MODULE.DEFAULT_KEEP)
        parsed = MODULE.build_parser().parse_args(["prune", "--releases", "-"])
        self.assertEqual(30, parsed.keep)

    def test_prune_accepts_the_gh_release_list_object_shape(self) -> None:
        payload = json.dumps([{"tagName": tag(1)}, {"tagName": tag(2)}])
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = Path(directory.name) / "releases.json"
        path.write_text(payload, encoding="utf-8")
        self.assertEqual([tag(1)], MODULE.select_prune(MODULE.load_release_tags(str(path)), 1))


class CommandLineTests(unittest.TestCase):
    def test_identity_cli_emits_every_documented_field(self) -> None:
        completed = subprocess.run(
            [sys.executable, str(MODULE_PATH), "identity", "--sha", "HEAD", "--repo-root", str(REPO_ROOT)],
            capture_output=True,
            text=True,
            check=True,
        )
        payload = json.loads(completed.stdout)
        self.assertEqual(
            {
                "skip",
                "anchor_tag",
                "anchor_version",
                "previous_dev_tag",
                "previous_dev_sha",
                "dev_version",
                "dev_tag",
                "notes_base_ref",
                "source_sha",
            },
            set(payload),
        )
        self.assertTrue(payload["anchor_tag"].startswith("v"))
        self.assertTrue(payload["dev_tag"].startswith("dev/"))
        self.assertEqual(1, payload["dev_version"].count("-"))
        self.assertEqual(40, len(payload["source_sha"]))

    def test_identity_cli_refuses_a_commit_with_no_anchor_tag(self) -> None:
        completed = subprocess.run(
            [sys.executable, str(MODULE_PATH), "identity", "--sha", "nope-no-such-ref", "--repo-root", str(REPO_ROOT)],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(2, completed.returncode)
        self.assertIn("no reachable v* tag", completed.stderr)


if __name__ == "__main__":
    unittest.main()
