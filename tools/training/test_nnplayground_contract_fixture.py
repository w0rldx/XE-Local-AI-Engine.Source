#!/usr/bin/env python3
"""The Model Lab training stepper's C#<->Python contract fixture: tools/training/nnplayground.py run fresh on the
fixture's job, checked against the committed trace and summary the C# checks read (``ModelLabPlaygroundContractTests``)
and against the React tree's copy of that trace and summary, which must exist.

It sits in tools/training because it needs CPU torch, which Python validation provides and the torch-free release
contract lane over scripts/tests does not; it reads repository files and is not meant to run in a packaged runtime.
Structure must match exactly; numbers within rtol 1e-9, atol 1e-12, since another torch build may round a last bit
differently; ``torchVersion`` is not compared. Needs CPU torch: off Linux x86_64 (where the root dev venv carries
none) it skips, on Linux x86_64 a missing torch fails.

Only after a failure whose cause is understood and intended (a writer change, a torch upgrade), regenerate with
``python3 tools/training/test_nnplayground_contract_fixture.py --write`` and commit the result beside the C# and React
changes. The tests themselves never write.
"""

from __future__ import annotations

import importlib.util
import json
import math
import os
import platform
import subprocess  # noqa: S404 - runs this interpreter on the script under test
import sys
import tempfile
import unittest
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]
FIXTURE = REPO_ROOT / "XE-Local-AI-Engine.Tests" / "Fixtures" / "model-lab-playground-contract.json"
REACT_COPY = (
    REPO_ROOT
    / "XE-Local-AI-Engine.Client.React"
    / "src"
    / "features"
    / "modelLab"
    / "test"
    / "playgroundTrace.fixture.json"
)
SCRIPT = REPO_ROOT / "tools" / "training" / "nnplayground.py"
RTOL, ATOL = 1e-9, 1e-12
UNCOMPARED = frozenset(("torchVersion",))
RUN_ID = "contract-fixture-run"
WRITE_HINT = (
    "Explain the drift first; if it is intended, run `python3 tools/training/test_nnplayground_contract_fixture.py "
    "--write` and keep the C# checks and the React copy in step."
)


def _require_torch() -> None:
    if importlib.util.find_spec("torch") is None:
        if sys.platform == "linux" and platform.machine() == "x86_64":
            raise AssertionError("torch is missing: the root dev venv pins CPU torch on Linux x86_64.")
        raise unittest.SkipTest("torch is not installed: the root dev venv carries CPU torch on Linux x86_64 only.")


def run_fixture_job() -> tuple[dict[str, Any], dict[str, Any]]:
    """(trace, summary) as the script writes them for the fixture's job, its paths moved into a temp directory."""
    job = json.loads(FIXTURE.read_text(encoding="utf-8"))["job"]
    with tempfile.TemporaryDirectory() as directory:
        job = {
            **job,
            "tracePath": os.path.join(directory, "trace.json"),
            "resultPath": os.path.join(directory, "result.json"),
        }
        config = os.path.join(directory, "job.json")
        with open(config, "w", encoding="utf-8") as handle:
            json.dump(job, handle)
        completed = subprocess.run(  # noqa: S603 - this interpreter running the script under test
            [sys.executable, str(SCRIPT), "--config", config],
            capture_output=True,
            timeout=120,
            check=False,
            env={**os.environ, "XE_TRAINING_RUN_TOKEN": RUN_ID},
        )
        if completed.returncode != 0:
            output = f"{completed.stdout.decode()}\n{completed.stderr.decode()}"
            raise AssertionError(f"nnplayground.py exited {completed.returncode}:\n{output}")
        with open(job["tracePath"], encoding="utf-8") as trace, open(job["resultPath"], encoding="utf-8") as summary:
            return json.load(trace), json.load(summary)


def differences(expected: Any, actual: Any, path: str = "$", skip: frozenset[str] = frozenset()) -> list[str]:
    """Where ``actual`` departs from ``expected``: keys, lengths, JSON types and null-ness exactly, numbers within
    tolerance. ``skip`` names top-level keys left out."""
    if isinstance(expected, dict) and isinstance(actual, dict):
        if expected.keys() != actual.keys():
            return [f"{path}: keys {sorted(expected)} != {sorted(actual)}"]
        found: list[str] = []
        for key in expected:
            if key not in skip:
                found += differences(expected[key], actual[key], f"{path}.{key}")
        return found
    if isinstance(expected, list) and isinstance(actual, list):
        if len(expected) != len(actual):
            return [f"{path}: length {len(expected)} != {len(actual)}"]
        found = []
        for index, (left, right) in enumerate(zip(expected, actual, strict=True)):
            found += differences(left, right, f"{path}[{index}]")
        return found
    if type(expected) is not type(actual):
        return [f"{path}: {type(expected).__name__} {expected!r} != {type(actual).__name__} {actual!r}"]
    if isinstance(expected, float):
        return (
            []
            if math.isclose(expected, actual, rel_tol=RTOL, abs_tol=ATOL)
            else [f"{path}: {expected!r} != {actual!r}"]
        )
    return [] if expected == actual else [f"{path}: {expected!r} != {actual!r}"]


def react_copy_differences(committed: dict[str, Any], react_copy: Path) -> list[str]:
    """Where the React copy departs from the committed trace and summary; a missing copy is a difference."""
    if not react_copy.is_file():
        return [f"react: {react_copy} is missing; the React stepper tests read it, so move it back or fix REACT_COPY."]
    react = json.loads(react_copy.read_text(encoding="utf-8"))
    return differences({"trace": committed["trace"], "summary": committed["summary"]}, react, "react")


def _report(found: list[str]) -> str:
    shown = "\n".join(found[:20])
    more = f"\n... and {len(found) - 20} more" if len(found) > 20 else ""
    return f"{shown}{more}\n{WRITE_HINT}"


class PlaygroundContractFixtureTests(unittest.TestCase):
    def test_a_fresh_run_of_the_fixtures_job_matches_the_committed_trace_and_summary(self) -> None:
        _require_torch()
        committed = json.loads(FIXTURE.read_text(encoding="utf-8"))
        trace, summary = run_fixture_job()
        found = differences(committed["trace"], trace, "trace", UNCOMPARED)
        found += differences(committed["summary"], summary, "summary")
        found += react_copy_differences(committed, REACT_COPY)
        assert not found, _report(found)

    def test_the_react_copy_is_present_and_a_missing_one_fails(self) -> None:
        committed = json.loads(FIXTURE.read_text(encoding="utf-8"))
        found = react_copy_differences(committed, REACT_COPY)
        assert not found, _report(found)
        with tempfile.TemporaryDirectory() as directory:
            assert react_copy_differences(committed, Path(directory) / "moved.fixture.json")

    def test_the_comparison_catches_drift(self) -> None:
        base = {"a": [1.0, None, True], "b": {"c": "x", "n": 3}}
        assert differences(base, json.loads(json.dumps(base))) == []
        assert differences(base, {**base, "a": [1.0 + 1e-12, None, True]}) == []
        assert differences(base, {**base, "a": [1.0 + 1e-6, None, True]})
        assert differences(base, {**base, "a": [1.0, 0.0, True]})
        assert differences(base, {**base, "a": [1.0, None, 1]})
        assert differences(base, {**base, "a": [1.0, None]})
        assert differences(base, {**base, "b": {"c": "x", "n": 3.0}})
        assert differences(base, {**base, "b": {"c": "x"}})
        assert differences(base, {**base, "b": "anything"}, skip=frozenset(("b",))) == []


if __name__ == "__main__":
    if sys.argv[1:] == ["--write"]:
        fixture = json.loads(FIXTURE.read_text(encoding="utf-8"))
        fixture["trace"], fixture["summary"] = run_fixture_job()
        FIXTURE.write_text(json.dumps(fixture, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        react = {"trace": fixture["trace"], "summary": fixture["summary"]}
        REACT_COPY.write_text(json.dumps(react, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    else:
        unittest.main()
