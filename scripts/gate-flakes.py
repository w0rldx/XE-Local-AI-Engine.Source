#!/usr/bin/env python3
"""List backend gate flakes from the gate history: tests that failed on a clean tip that also passed unchanged.

Usage: scripts/gate-flakes.py [--log <gate-history.log>] [--flakes <known-flakes.txt>]

Reads backend rows only (the last field exit=<code>, no lane= field). A red row (exit other than 0) with reason=tests
(a contaminated, killed or lock-timeout run certifies nothing) and a scope other than no-build (its binaries may come
from another commit; the row does not record which), whose tip carries no +dirty/+moved suffix and whose failed= names
tests, with a green row (exit=0, scope=full: any other scope may have skipped the
failed test's project) on the same tip, prints one line per named test and tip: <test> <red time>
<green time> <tip> <worktree of the red run> <listed|UNLISTED|CANDIDATE>, tab-separated. The green row is the first
one after the red run, else the last one before it, preferring one that skipped no test: either order proves the test
passed on the code it failed on. A row records no per-test outcome, so a green that skipped any test (a p/f/s lane count
with s above 0) cannot prove the test ran: an unlisted test is then CANDIDATE, confirmed only by a rerun of its class.
UNLISTED means scripts/known-flakes.txt lacks it, whose header says to add it. failed= holds at most three names a run.
--log defaults to the main checkout's .tmp/gate-history.log, --flakes to the known-flakes.txt beside this script.

Exit codes: 0 always after reading the log (a report, never a verdict), 2 when the log cannot be read.
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent
LANE_COUNT = re.compile(r"\d+/\d+/(\d+)")


def default_log() -> Path:
    # The repository-selection variables must not point this at another repository (scripts/lib/git-env.sh).
    env = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")}
    result = subprocess.run(
        ["git", "-C", str(SCRIPTS_DIR), "rev-parse", "--path-format=absolute", "--git-common-dir"],
        capture_output=True,
        text=True,
        env=env,
        check=False,
    )
    main = Path(result.stdout.strip()).resolve().parent if result.returncode == 0 else SCRIPTS_DIR.parent
    return main / ".tmp" / "gate-history.log"


def listed_flakes(path: Path) -> set[str]:
    if not path.is_file():
        return set()
    lines = path.read_text(encoding="utf-8").splitlines()
    return {line.split("|", 1)[0] for line in lines if line.strip() and not line.startswith("#")}


def backend_rows(lines: list[str]) -> list[tuple[list[str], dict[str, str]]]:
    rows = []
    for line in lines:
        fields = line.rstrip("\n").split("\t")
        if len(fields) < 4 or not fields[-1].startswith("exit=") or fields[3].startswith("lane="):
            continue
        named = dict(field.split("=", 1) for field in fields[3:] if "=" in field)
        rows.append((fields, named))
    return rows


def skipped_none(named: dict[str, str]) -> bool:
    return all(int(match[1]) == 0 for value in named.values() if (match := LANE_COUNT.fullmatch(value)))


def flakes(lines: list[str], listed: set[str]) -> list[str]:
    rows = backend_rows(lines)
    found = []
    seen: set[tuple[str, str]] = set()
    for index, (red, red_named) in enumerate(rows):
        names = [name for name in red_named.get("failed", "-").split(",") if name and name != "-"]
        # reason=tests is written only for a red run.
        if red_named.get("reason") != "tests" or red_named.get("scope") == "no-build":
            continue
        if "+" in red[2] or red[2] == "-" or not names:
            continue
        greens = [
            (position, fields, skipped_none(named))
            for position, (fields, named) in enumerate(rows)
            if fields[2] == red[2] and named["exit"] == "0" and named.get("scope") == "full"
        ]
        later = [green for green in greens if green[0] > index]
        earlier = [green for green in greens if green[0] < index][::-1]
        ordered = later + earlier
        if not ordered:
            continue
        _, green, confirmed = next((green for green in ordered if green[2]), ordered[0])
        for name in names:
            if (name, red[2]) in seen:
                continue
            seen.add((name, red[2]))
            mark = "listed" if name in listed else "UNLISTED" if confirmed else "CANDIDATE"
            found.append("\t".join((name, red[0], green[0], red[2], red[1], mark)))
    return found


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--log", type=Path, default=None)
    parser.add_argument("--flakes", type=Path, default=SCRIPTS_DIR / "known-flakes.txt")
    args = parser.parse_args(argv)
    log = args.log or default_log()
    try:
        lines = log.read_text(encoding="utf-8").splitlines()
    except OSError as error:
        print(f"gate-flakes: cannot read {log}: {error}", file=sys.stderr)
        return 2
    for line in flakes(lines, listed_flakes(args.flakes)):
        print(line)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
