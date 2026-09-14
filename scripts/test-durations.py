#!/usr/bin/env python3
"""Summarise TRX test durations per class, per test and per namespace.

Input is any mix of directories (searched recursively for `*.trx`) and `.trx` files, e.g. a
`COVERAGE_DIR` tree written by `scripts/run-tests-memory-safe.sh` or the TRX artifacts of a green CI
run. Three outputs, because three questions get asked of the same data:

* default — the per-class table (summed seconds, tests, s/test) plus the heaviest single tests, for
  deciding which classes a performance change should target;
* `--heavy` — one line per namespace in the exact shape of the `HEAVY` list in
  `scripts/run-tests-memory-safe.sh`, so its packer weights can be re-measured rather than guessed
  (docs/agent-knowledge.md section 1: the weights are load-bearing and the packer cannot split a
  namespace); pass `--runs N` when the paths hold N runs of the same suite and the seconds are
  divided by N, so the weights are a per-run mean instead of one run's noise;
* `--counts` — the classes/tests/namespaces/files the run actually contained, so a document can cite
  this script instead of hard-coding a number that goes stale.

The TRX shape read is this repo's TUnit/MTP output: top-level `TestDefinitions` and `Results` only, so a
report carrying `InnerResults` data rows could not double-count them.

Only `Passed` and `Failed` results are counted: anything else (skipped, not-executed) carries no
duration worth summing. Stdlib only, no network, no subprocesses.
"""

from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path

TRX_NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
DEFAULT_STRIP_PREFIX = "XE_Local_AI_Engine.Tests."
# The runner's own cut-off: everything unlisted weighs 1 there, and listing down to 10s is what made
# the pack balance (see the comment above `HEAVY=(` in scripts/run-tests-memory-safe.sh).
HEAVY_MINIMUM_SECONDS = 10
# `  <namespace padded to 52> # NNNs` — the column the runner's weight-parsing sed expects.
HEAVY_NAME_WIDTH = 52


def parse_duration(text: str) -> float:
    """Seconds from a TRX `hh:mm:ss.fffffff` duration."""
    hours, minutes, seconds = text.split(":")
    return int(hours) * 3600 + int(minutes) * 60 + float(seconds)


def collect_trx_files(paths: list[Path]) -> list[Path]:
    files: list[Path] = []
    for path in paths:
        if path.is_dir():
            files.extend(sorted(path.rglob("*.trx")))
        else:
            files.append(path)
    return files


def read_results(files: list[Path]) -> list[tuple[float, str, str]]:
    """(duration, class name, test name) for every executed result across the given TRX files."""
    results: list[tuple[float, str, str]] = []
    for file in files:
        root = ET.parse(file).getroot()
        class_of: dict[str, str] = {}
        for unit in root.iterfind("./t:TestDefinitions/t:UnitTest", TRX_NS):
            method = unit.find("t:TestMethod", TRX_NS)
            unit_id = unit.get("id")
            if method is not None and unit_id is not None:
                class_of[unit_id] = method.get("className", "?")
        for result in root.iterfind("./t:Results/t:UnitTestResult", TRX_NS):
            if result.get("outcome") not in ("Passed", "Failed"):
                continue
            duration = parse_duration(result.get("duration", "00:00:00"))
            class_name = class_of.get(result.get("testId", ""), "?")
            results.append((duration, class_name, result.get("testName", "?")))
    return results


def sum_by(results: list[tuple[float, str, str]], key: str) -> dict[str, tuple[float, int]]:
    """Summed seconds and test count per class, or per namespace when `key` is 'namespace'."""
    totals: dict[str, list[float]] = defaultdict(lambda: [0.0, 0])
    for duration, class_name, _ in results:
        name = class_name.rpartition(".")[0] if key == "namespace" else class_name
        totals[name][0] += duration
        totals[name][1] += 1
    return {name: (total, int(count)) for name, (total, count) in totals.items()}


def print_tables(results: list[tuple[float, str, str]], top: int, top_tests: int, strip: str) -> None:
    per_class = sum_by(results, "class")
    total = sum(seconds for seconds, _ in per_class.values())
    count = sum(tests for _, tests in per_class.values())
    ranked = sorted(per_class.items(), key=lambda item: (-item[1][0], item[0]))
    print(f"tests={count} sum_test_seconds={total:.0f} classes={len(per_class)}")
    print(f"\n## Top {top} classes by summed duration (s | tests | s/test | class)")
    for name, (seconds, tests) in ranked[:top]:
        print(f"{seconds:7.1f} {tests:5d} {seconds / tests:6.2f}  {name.removeprefix(strip)}")
    print(f"\n## Top {top_tests} single tests")
    for seconds, class_name, test_name in sorted(results, reverse=True)[:top_tests]:
        print(f"{seconds:7.1f}  {class_name.removeprefix(strip)}.{test_name[:70]}")
    running = 0.0
    for index, (_, (seconds, _tests)) in enumerate(ranked, start=1):
        running += seconds
        if running >= total * 0.5:
            print(f"\n50% of test-seconds sit in the top {index} classes")
            break


def print_heavy(results: list[tuple[float, str, str]], runs: int = 1) -> None:
    # Attribution is by the test class's own namespace, never by the TRX directory: under CI's
    # TEST_GROUPS shape that directory is a group, not a namespace. The known asymmetry is that a few
    # tests live in a parent namespace and are also matched by a child batch, so the parent's weight
    # absorbs a cost the child's batch pays too — which is what the packer should see, since both
    # batches really do run them.
    # `runs` > 1 means the paths hold that many runs of the same suite: divide before the cut-off and
    # the rounding, so the emitted weight is one run's mean rather than the sum of N.
    per_namespace = sum_by(results, "namespace")
    for name, (seconds, _tests) in sorted(per_namespace.items(), key=lambda item: (-item[1][0], item[0])):
        weight = round(seconds / runs)
        if weight < HEAVY_MINIMUM_SECONDS:
            break
        if not name or "?" in name:
            # A result whose class name did not resolve has no namespace, and the runner's weight
            # regex cannot match such a line — it would drop it without a word. Refuse to emit it.
            print(
                f"[test-durations] WARNING: dropped {weight}s of results with no resolvable namespace",
                file=sys.stderr,
            )
            continue
        print(f"  {name:<{HEAVY_NAME_WIDTH}} # {weight}s")


def print_counts(results: list[tuple[float, str, str]], files: list[Path]) -> None:
    per_class = sum_by(results, "class")
    per_namespace = sum_by(results, "namespace")
    total = sum(seconds for seconds, _ in per_class.values())
    print(
        f"files={len(files)} tests={len(results)} classes={len(per_class)} "
        f"namespaces={len(per_namespace)} sum_test_seconds={total:.0f}"
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Summarise TRX test durations per class, per test and per namespace.",
        epilog="Directories are searched recursively for *.trx.",
    )
    parser.add_argument("paths", nargs="+", type=Path, help="TRX files and/or directories holding them")
    parser.add_argument("--top", type=int, default=40, help="classes in the per-class table (default: 40)")
    parser.add_argument("--top-tests", type=int, default=25, help="single tests to list (default: 25)")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument(
        "--heavy",
        action="store_true",
        help="print the per-namespace HEAVY weight lines for scripts/run-tests-memory-safe.sh instead",
    )
    mode.add_argument("--counts", action="store_true", help="print only the classes/tests/namespaces/files seen")
    parser.add_argument(
        "--runs",
        type=int,
        default=1,
        help="the paths hold this many runs of the same suite; --heavy divides by it (default: 1)",
    )
    parser.add_argument(
        "--strip-prefix",
        default=DEFAULT_STRIP_PREFIX,
        help=f"namespace prefix trimmed from the tables (default: {DEFAULT_STRIP_PREFIX})",
    )
    args = parser.parse_args(argv)
    if args.runs < 1:
        parser.error(f"--runs must be at least 1, got {args.runs}")
    if args.runs != 1 and not args.heavy:
        # Accepting it elsewhere would hand back the N-run sum to a caller who asked for a mean.
        parser.error("--runs applies to --heavy only")

    missing = [path for path in args.paths if not path.exists()]
    if missing:
        parser.error(f"no such path: {', '.join(str(path) for path in missing)}")
    files = collect_trx_files(args.paths)
    if not files:
        parser.error("no .trx files found under the given paths")
    results = read_results(files)
    if not results:
        # A TRX set with no executed result is never a measurement; saying so beats printing zeroes.
        raise SystemExit(f"[test-durations] FAIL: {len(files)} TRX file(s) contained no executed test result")

    if args.counts:
        print_counts(results, files)
    elif args.heavy:
        print_heavy(results, args.runs)
    else:
        print_tables(results, args.top, args.top_tests, args.strip_prefix)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
