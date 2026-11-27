"""git_env.py: the Python side of scripts/lib/git-env.sh, the one place Python scripts and tests take git's
environment from (scripts/tests/test_git_env.py holds it, git-env.sh and GitEnvironment.mjs to the same lists).

``REPO_ENV_VARS`` select another repository, work tree, index or object store than the working directory's (or -C's);
a query of the caller's real repository drops only these, so the caller's own configuration still applies.
``CONFIG_OVERRIDES`` inject configuration: an inherited GIT_CONFIG is the file ``git config`` writes (a fixture's
``git config user.email`` would rewrite the operator's identity), GIT_CONFIG_PARAMETERS and GIT_CONFIG_COUNT add
settings, GIT_TEMPLATE_DIR outranks init.templateDir. ``fixture_env`` drops both and adds ``SETTINGS`` at command
level, which outranks every config file, for git that creates or writes a scratch repository.
"""

from __future__ import annotations

import os
from collections.abc import Mapping

REPO_ENV_VARS = (
    "GIT_DIR",
    "GIT_WORK_TREE",
    "GIT_INDEX_FILE",
    "GIT_COMMON_DIR",
    "GIT_OBJECT_DIRECTORY",
    "GIT_ALTERNATE_OBJECT_DIRECTORIES",
    "GIT_NAMESPACE",
)
CONFIG_OVERRIDES = ("GIT_CONFIG", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT", "GIT_TEMPLATE_DIR")
# No hook, signing agent or file monitor a config names runs, and no global ignore or attributes file (configured or
# the XDG default) or init template can refuse or rewrite a fixture file.
SETTINGS = (
    ("core.hooksPath", os.devnull),
    ("commit.gpgSign", "false"),
    ("tag.gpgSign", "false"),
    ("core.excludesFile", os.devnull),
    ("core.attributesFile", os.devnull),
    ("core.fsmonitor", "false"),
    ("init.templateDir", ""),
)


def fixture_env(environ: Mapping[str, str] | None = None) -> dict[str, str]:
    """``environ`` (default: this process's) without ``REPO_ENV_VARS`` and ``CONFIG_OVERRIDES``, plus ``SETTINGS``."""
    source = os.environ if environ is None else environ
    dropped = (*REPO_ENV_VARS, *CONFIG_OVERRIDES)
    env = {key: value for key, value in source.items() if key.upper() not in dropped}
    env["GIT_CONFIG_COUNT"] = str(len(SETTINGS))
    for index, (key, value) in enumerate(SETTINGS):
        env.update({f"GIT_CONFIG_KEY_{index}": key, f"GIT_CONFIG_VALUE_{index}": value})
    return env
