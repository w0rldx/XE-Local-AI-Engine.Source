#!/usr/bin/env python3
"""Derive a Development build's version identity, its no-change skip and its prune set.

`.github/workflows/dev-build.yml` is the only caller. `identity` runs before packaging; `prune` runs
after the new release is published, because a pre-publish list of the newest 30 would always prune
nothing. Both subcommands print one JSON object to stdout.

Every rule is a pure function taking its inputs as arguments; git is read only from `main()`, so the
tests need no repository fixture (and no commit identity, which the repository forbids setting).

Version rule (pre-1.0 and after, ADR 0014 D5) — the anchor is the newest `v*` tag reachable from the
built commit:

    anchor 1.0.0-rc.2  ->  1.0.0-rc.2.dev.<yyyymmdd>.<n>
    anchor 1.0.0       ->  1.0.1-dev.<yyyymmdd>.<n>

Dots only inside the suffix. A second hyphen would make the label non-numeric, and Velopack ranks a
non-numeric prerelease label above every numeric one, so `1.0.0-rc.2-dev.1` would outrank
`1.0.0-rc.9` and strand every Development tester above the RC line. `compose_dev_version` asserts its
own output to make that unreachable.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from collections.abc import Iterable, Sequence
from datetime import UTC, datetime
from pathlib import Path

DEV_TAG_PREFIX = "dev/"
DEFAULT_KEEP = 30
STABLE_VERSION = re.compile(r"(\d+)\.(\d+)\.(\d+)")
ANCHOR_RC = re.compile(r"-rc\.(\d+)")
DEV_TAIL = re.compile(r"[.-]dev\.(\d{8})\.(\d+)$")
BUILD_DATE = re.compile(r"\d{8}")


def split_version(dev_tag: str) -> str:
    """`dev/1.0.0-rc.2.dev.20260922.3` -> `1.0.0-rc.2.dev.20260922.3`."""
    if not dev_tag.startswith(DEV_TAG_PREFIX):
        raise ValueError(f"not a Development tag: '{dev_tag}'")
    version = dev_tag[len(DEV_TAG_PREFIX) :]
    if not version:
        raise ValueError(f"Development tag carries no version: '{dev_tag}'")
    return version


def dev_version_prefix(anchor_version: str, date: str) -> str:
    """The Development version for this anchor and date, minus the counter."""
    if not BUILD_DATE.fullmatch(date):
        raise ValueError(f"build date must be yyyymmdd, got '{date}'")
    if "-" in anchor_version:
        return f"{anchor_version}.dev.{date}."
    stable = STABLE_VERSION.fullmatch(anchor_version)
    if stable is None:
        raise ValueError(f"anchor version is neither X.Y.Z nor a prerelease: '{anchor_version}'")
    major, minor, patch = (int(part) for part in stable.groups())
    return f"{major}.{minor}.{patch + 1}-dev.{date}."


def compose_dev_version(anchor_version: str, date: str, counter: int) -> str:
    if counter < 1:
        raise ValueError(f"Development counter starts at 1, got {counter}")
    version = f"{dev_version_prefix(anchor_version, date)}{counter}"
    if version.count("-") != 1:
        raise ValueError(
            f"composed Development version '{version}' carries {version.count('-')} hyphens; "
            "exactly one is allowed, or it outranks every release candidate"
        )
    return version


def next_counter(dev_versions: Iterable[str], prefix: str) -> int:
    counters = [
        int(version[len(prefix) :])
        for version in dev_versions
        if version.startswith(prefix) and version[len(prefix) :].isdigit()
    ]
    return max(counters) + 1 if counters else 1


def anchor_key(anchor_version: str) -> tuple[int, int, int, int, int, str]:
    """Order the anchor a Development version carries; a stable anchor outranks every rc of the same X.Y.Z.

    Kept total rather than strict: an anchor shape D5 does not define falls back to its label text instead
    of raising, because this key only breaks a tie between two Development builds made on one date.
    """
    stable = STABLE_VERSION.match(anchor_version)
    if stable is None:
        raise ValueError(f"not a Development anchor: '{anchor_version}'")
    major, minor, patch = (int(part) for part in stable.groups())
    label = anchor_version[stable.end() :]
    if not label:
        return major, minor, patch, 2, 0, ""
    candidate = ANCHOR_RC.fullmatch(label)
    if candidate is not None:
        return major, minor, patch, 1, int(candidate.group(1)), ""
    return major, minor, patch, 0, 0, label


def sort_key(dev_version: str) -> tuple[int, tuple[int, int, int, int, int, str], int]:
    """Order Development versions by build date, then anchor, then counter. Not a general SemVer comparator.

    The anchor has to sit in the key: two anchors building on one UTC date tie on date and counter, and a
    string tail-break sorts `1.0.0-rc.10.dev.<d>.1` BELOW `1.0.0-rc.9.dev.<d>.1`, which names the wrong tag
    as `previous_dev_tag` and generates release notes over the wrong range.
    """
    tail = DEV_TAIL.search(dev_version)
    if tail is None:
        raise ValueError(f"not a Development version: '{dev_version}'")
    return int(tail.group(1)), anchor_key(dev_version[: tail.start()]), int(tail.group(2))


def newest_dev_tag(dev_tags: Sequence[str]) -> str | None:
    """The highest Development tag by version, never by tag creation order."""
    if not dev_tags:
        return None
    return max(dev_tags, key=lambda tag: sort_key(split_version(tag)))


def select_prune(dev_release_tags: Sequence[str], keep: int) -> list[str]:
    if keep < 1:
        raise ValueError(f"retention must keep at least one release, got {keep}")
    for tag in dev_release_tags:
        split_version(tag)
    ordered = sorted(dev_release_tags, key=lambda tag: sort_key(split_version(tag)), reverse=True)
    return ordered[keep:]


def compute_identity(
    anchor_tag: str,
    anchor_version: str,
    dev_tags: Sequence[str],
    tag_shas: dict[str, str],
    current_sha: str,
    date: str,
) -> dict[str, object]:
    previous_dev_tag = newest_dev_tag(dev_tags)
    previous_dev_sha = tag_shas.get(previous_dev_tag) if previous_dev_tag is not None else None
    prefix = dev_version_prefix(anchor_version, date)
    counter = next_counter((split_version(tag) for tag in dev_tags), prefix)
    dev_version = compose_dev_version(anchor_version, date, counter)
    return {
        # True exactly when the previous Development build already published this commit.
        "skip": previous_dev_sha is not None and previous_dev_sha == current_sha,
        "anchor_tag": anchor_tag,
        "anchor_version": anchor_version,
        "previous_dev_tag": previous_dev_tag,
        "previous_dev_sha": previous_dev_sha,
        "dev_version": dev_version,
        "dev_tag": f"{DEV_TAG_PREFIX}{dev_version}",
        # First build on this repository: base the notes on the anchor tag instead.
        "notes_base_ref": previous_dev_sha if previous_dev_sha is not None else anchor_tag,
        "source_sha": current_sha,
    }


def git(repo_root: Path, *args: str) -> str | None:
    completed = subprocess.run(["git", *args], cwd=repo_root, capture_output=True, text=True, check=False)
    if completed.returncode != 0:
        return None
    return completed.stdout.strip()


def run_identity(args: argparse.Namespace) -> int:
    repo_root = Path(args.repo_root).resolve()
    anchor_tag = git(repo_root, "describe", "--tags", "--abbrev=0", "--match", "v*", args.sha)
    if not anchor_tag:
        print("ERROR: no reachable v* tag to anchor the Development version on.", file=sys.stderr)
        return 2
    source_sha = git(repo_root, "rev-parse", f"{args.sha}^{{commit}}")
    if not source_sha:
        print(f"ERROR: '{args.sha}' does not resolve to a commit.", file=sys.stderr)
        return 2
    listed = git(repo_root, "tag", "--list", f"{DEV_TAG_PREFIX}*")
    if listed is None:
        print("ERROR: could not list Development tags.", file=sys.stderr)
        return 2
    dev_tags = [line.strip() for line in listed.splitlines() if line.strip()]
    tag_shas: dict[str, str] = {}
    previous = newest_dev_tag(dev_tags)
    if previous is not None:
        previous_sha = git(repo_root, "rev-parse", f"{previous}^{{commit}}")
        if not previous_sha:
            print(f"ERROR: '{previous}' does not resolve to a commit.", file=sys.stderr)
            return 2
        tag_shas[previous] = previous_sha
    identity = compute_identity(
        anchor_tag=anchor_tag,
        anchor_version=anchor_tag.removeprefix("v"),
        dev_tags=dev_tags,
        tag_shas=tag_shas,
        current_sha=source_sha,
        date=args.date,
    )
    print(json.dumps(identity, indent=2))
    return 0


def load_release_tags(source: str) -> list[str]:
    raw = sys.stdin.read() if source == "-" else Path(source).read_text(encoding="utf-8")
    payload = json.loads(raw)
    if not isinstance(payload, list):
        raise ValueError("--releases must contain a JSON array")
    tags: list[str] = []
    for entry in payload:
        if isinstance(entry, str):
            tags.append(entry)
        elif isinstance(entry, dict) and isinstance(entry.get("tagName"), str):
            tags.append(entry["tagName"])
        else:
            raise ValueError(f"unsupported release entry: {entry!r}")
    return tags


def run_prune(args: argparse.Namespace) -> int:
    tags = load_release_tags(args.releases)
    delete = select_prune(tags, args.keep)
    print(json.dumps({"keep": args.keep, "total": len(tags), "delete": delete}, indent=2))
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    subcommands = parser.add_subparsers(dest="command", required=True)

    identity = subcommands.add_parser("identity", help="Derive the next Development version and the skip decision.")
    identity.add_argument("--sha", default="HEAD", help="The commit being built.")
    identity.add_argument("--date", default=datetime.now(UTC).strftime("%Y%m%d"), help="UTC build date as yyyymmdd.")
    identity.add_argument("--repo-root", default=".", help="Repository to read tags from.")
    identity.set_defaults(handler=run_identity)

    prune = subcommands.add_parser("prune", help="Select superseded Development releases to delete.")
    prune.add_argument(
        "--keep",
        type=int,
        default=DEFAULT_KEEP,
        help="Development releases to retain. This default is the retention policy; no caller passes it.",
    )
    prune.add_argument("--releases", required=True, help="JSON array of tag names, or '-' for stdin.")
    prune.set_defaults(handler=run_prune)
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        return args.handler(args)
    except (ValueError, json.JSONDecodeError, OSError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
