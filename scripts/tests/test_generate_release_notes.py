#!/usr/bin/env python3
"""Drive scripts/generate-release-notes.sh against PATH shims for `git` and `git-cliff`.

git-cliff is not installed on a developer box and `release-contracts` does not install it either, so the
only way to assert what the script *asks* git-cliff for is to record the argv. The shims also keep the run
inside a scratch directory: the `git rev-parse --show-toplevel` the script `cd`s into is the scratch root,
so nothing is written to the repository.
"""

from __future__ import annotations

import os
import re
import subprocess
import tempfile
import tomllib
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SCRIPT = REPO_ROOT / "scripts" / "generate-release-notes.sh"

GIT_SHIM = """#!/usr/bin/env bash
printf '%s\\n' "$*" >> "$XE_GIT_LOG"
case "$1" in
  rev-parse) echo "$XE_SCRATCH_ROOT" ;;
  describe) exit "${XE_DESCRIBE_EXIT:-0}" ;;
  *) exit 0 ;;
esac
"""

CLIFF_SHIM = """#!/usr/bin/env bash
printf '%s\\n' "$*" >> "$XE_CLIFF_LOG"
output=""
previous=""
for argument in "$@"; do
  if [[ "$previous" == "-o" ]]; then output="$argument"; fi
  previous="$argument"
done
if [[ -n "$output" ]]; then
  if [[ -n "${XE_CLIFF_EMPTY:-}" ]]; then
    : > "$output"
  else
    printf '### Features\\n\\n- a change\\n' > "$output"
  fi
fi
"""


class GenerateReleaseNotesTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        for name, body in (("git", GIT_SHIM), ("git-cliff", CLIFF_SHIM)):
            shim = self.bin / name
            shim.write_text(body, encoding="utf-8")
            shim.chmod(0o755)
        self.git_log = self.root / "git.log"
        self.cliff_log = self.root / "cliff.log"

    def run_script(self, *args: str, describe_exit: int = 1, cliff_empty: bool = False) -> None:
        environment = dict(os.environ)
        environment.update(
            {
                "PATH": f"{self.bin}{os.pathsep}{environment['PATH']}",
                "XE_GIT_LOG": str(self.git_log),
                "XE_CLIFF_LOG": str(self.cliff_log),
                "XE_SCRATCH_ROOT": str(self.root),
                "XE_DESCRIBE_EXIT": str(describe_exit),
            }
        )
        if cliff_empty:
            environment["XE_CLIFF_EMPTY"] = "1"
        completed = subprocess.run(
            ["bash", str(SCRIPT), *args],
            capture_output=True,
            text=True,
            check=False,
            cwd=self.root,
            env=environment,
        )
        self.assertEqual(0, completed.returncode, completed.stderr)

    def cliff_argv(self) -> str:
        return self.cliff_log.read_text(encoding="utf-8")

    def git_argv(self) -> str:
        return self.git_log.read_text(encoding="utf-8") if self.git_log.exists() else ""

    def describe_calls(self) -> list[str]:
        return [line for line in self.git_argv().splitlines() if line.startswith("describe")]

    def test_since_mode_passes_an_explicit_range_and_tag(self) -> None:
        self.run_script("--since", "abc123", "1.0.0-rc.2.dev.20260922.1", "OUT.md")
        argv = self.cliff_argv()
        self.assertIn("abc123..HEAD", argv)
        self.assertIn("--tag v1.0.0-rc.2.dev.20260922.1", argv)
        self.assertIn("--strip header", argv)
        self.assertNotIn("--latest", argv)
        self.assertNotIn("--unreleased", argv)

    def test_tagged_head_still_uses_latest(self) -> None:
        self.run_script("1.2.3", "OUT.md", describe_exit=0)
        argv = self.cliff_argv()
        self.assertIn("--latest", argv)
        self.assertNotIn("--tag", argv)

    def test_untagged_head_uses_unreleased_with_the_tag(self) -> None:
        self.run_script("1.2.3", "OUT.md", describe_exit=1)
        argv = self.cliff_argv()
        self.assertIn("--unreleased", argv)
        self.assertIn("--tag v1.2.3", argv)

    def test_head_tag_probe_is_restricted_to_v_tags(self) -> None:
        self.run_script("1.2.3", "OUT.md", describe_exit=1)
        calls = self.describe_calls()
        self.assertEqual(1, len(calls))
        self.assertIn("--match", calls[0])
        self.assertIn("v*", calls[0])

    def test_since_mode_does_not_probe_head_tags_at_all(self) -> None:
        self.run_script("--since", "abc123", "1.2.3", "OUT.md")
        self.assertEqual([], self.describe_calls())

    def test_empty_cliff_output_falls_back_to_a_maintenance_body(self) -> None:
        self.run_script("1.2.3", "OUT.md", cliff_empty=True)
        body = (self.root / "OUT.md").read_text(encoding="utf-8")
        self.assertIn("## 1.2.3", body)
        self.assertIn("Maintenance release — no user-facing changelog entries.", body)

    def test_usage_still_accepts_the_positional_release_form(self) -> None:
        self.run_script("1.2.3", "OUT.md")
        self.assertTrue((self.root / "OUT.md").is_file())
        self.assertIn("- a change", (self.root / "OUT.md").read_text(encoding="utf-8"))

    def test_since_without_a_ref_is_refused(self) -> None:
        completed = subprocess.run(
            ["bash", str(SCRIPT), "--since"],
            capture_output=True,
            text=True,
            check=False,
            cwd=self.root,
            env={
                **os.environ,
                "PATH": f"{self.bin}{os.pathsep}{os.environ['PATH']}",
                "XE_GIT_LOG": str(self.git_log),
                "XE_SCRATCH_ROOT": str(self.root),
            },
        )
        self.assertEqual(1, completed.returncode)
        self.assertIn("--since needs a git ref", completed.stderr)


class CliffTagBoundaryTests(unittest.TestCase):
    """`cliff.toml`'s tag regexes decide where an official release's commit range starts.

    Asserted with `re` against the real config rather than by running git-cliff, which is not installed on a
    developer box. git-cliff matches both patterns UNANCHORED with the Rust `regex` crate, and Python's
    `re.search` has the same semantics for these patterns.
    """

    RELEASE_TAGS = ("v1.0.0", "v1.0.0-rc.2", "v0.1.0-rc.5.1")
    NON_RELEASE_TAGS = ("dev/1.0.0-rc.2.dev.20260922.1", "codex/rollback-prior-ai-pins")

    @classmethod
    def setUpClass(cls) -> None:
        with (REPO_ROOT / "cliff.toml").open("rb") as handle:
            cls.git = tomllib.load(handle)["git"]

    def test_tag_pattern_matches_every_release_tag_shape(self) -> None:
        pattern = self.git["tag_pattern"]
        for tag in self.RELEASE_TAGS:
            self.assertIsNotNone(re.search(pattern, tag), f"tag_pattern must match '{tag}'")

    def test_tag_pattern_matches_no_tag_outside_the_release_line(self) -> None:
        # The old `v?[0-9]*` matched all of these, and the empty string too, so every `dev/` snapshot and
        # every `codex/` branch tag became a release boundary for --unreleased/--latest.
        pattern = self.git["tag_pattern"]
        for tag in (*self.NON_RELEASE_TAGS, ""):
            self.assertIsNone(re.search(pattern, tag), f"tag_pattern must not match '{tag}'")

    def test_ignore_tags_covers_development_tags_only(self) -> None:
        pattern = self.git["ignore_tags"]
        self.assertIsNotNone(re.search(pattern, "dev/1.0.0-rc.2.dev.20260922.1"))
        for tag in self.RELEASE_TAGS:
            self.assertIsNone(re.search(pattern, tag), f"ignore_tags must not match '{tag}'")


if __name__ == "__main__":
    unittest.main()
