#!/usr/bin/env python3
"""Contract test for scripts/lib/git_env.py: the same lists as scripts/lib/git-env.sh, and the fixture environment."""

from __future__ import annotations

import importlib.util
import os
import unittest
from pathlib import Path
from typing import Any

LIB = Path(__file__).resolve().parents[1] / "lib"
SPEC = importlib.util.spec_from_file_location("git_env", LIB / "git_env.py")
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("could not load scripts/lib/git_env.py")
GIT_ENV: Any = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GIT_ENV)


def shell_list(name: str) -> list[str]:
    script = (LIB / "git-env.sh").read_text(encoding="utf-8")
    return sorted(script.split(f"{name}=(", 1)[1].split(")", 1)[0].split())


class GitEnvTests(unittest.TestCase):
    def test_the_lists_are_the_ones_git_env_sh_drops(self) -> None:
        self.assertEqual(shell_list("XE_GIT_REPO_ENV_VARS"), sorted(GIT_ENV.REPO_ENV_VARS))
        self.assertEqual(shell_list("XE_GIT_CONFIG_ENV_VARS"), sorted(GIT_ENV.CONFIG_OVERRIDES))

    def test_the_fixture_environment_drops_selectors_and_injected_config_and_pins_the_settings(self) -> None:
        caller = {name: "/caller" for name in (*GIT_ENV.REPO_ENV_VARS, *GIT_ENV.CONFIG_OVERRIDES)}
        caller.update(
            GIT_CONFIG_KEY_0="user.email", GIT_CONFIG_VALUE_0="x", GIT_CEILING_DIRECTORIES="/ceiling", HOME="/h"
        )

        env = GIT_ENV.fixture_env(caller)

        self.assertEqual(
            {"GIT_CEILING_DIRECTORIES": "/ceiling", "HOME": "/h", "GIT_CONFIG_COUNT": str(len(GIT_ENV.SETTINGS))},
            {key: value for key, value in env.items() if not key.startswith(("GIT_CONFIG_KEY_", "GIT_CONFIG_VALUE_"))},
        )
        pairs = [(env[f"GIT_CONFIG_KEY_{i}"], env[f"GIT_CONFIG_VALUE_{i}"]) for i in range(len(GIT_ENV.SETTINGS))]
        self.assertEqual(list(GIT_ENV.SETTINGS), pairs)
        self.assertIn(("core.hooksPath", os.devnull), pairs)


if __name__ == "__main__":
    unittest.main()
