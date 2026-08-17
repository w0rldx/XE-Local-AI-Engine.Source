#!/usr/bin/env python3
"""Fail when the code-grounded wiki has fallen behind an inventory the code owns.

`docs/wiki/` claims to enumerate things the code defines — one row per SignalR hub, one row per
nested route family in `LocalApiRoutes`, one section per React feature area, one entry per solution
project. Those claims rot silently: adding a hub or a feature directory is a one-line change that
nobody thinks of as a documentation change, and the wiki page still reads as complete afterwards.

This guard re-derives each inventory from the code and asserts every member is named in the page
that claims to list it. It is deliberately a *mention* check, not a structural one: the wiki pages
spell these names verbatim, so a substring match is enough to catch the failure that actually
happens (a brand-new name nobody wrote down) without dictating how a page is laid out.

Every inventory must be non-empty. An inventory that silently resolves to zero items — a moved
directory, a renamed source file, a regex that stopped matching — would make its check vacuously
green, which is the one outcome a guard must never produce.

Exit codes: 0 clean, 1 something is missing from a page, 2 a check could not run at all.
"""

from __future__ import annotations

import argparse
import re
import sys
from collections.abc import Callable, Iterable
from dataclasses import dataclass
from pathlib import Path

WIKI_DIR = Path("docs/wiki")
API_AND_HUBS_PAGE = WIKI_DIR / "09-api-and-hubs.md"
HOME_PAGE = WIKI_DIR / "Home.md"
REACT_CLIENT_PAGE = WIKI_DIR / "10-react-client.md"
PROJECT_LAYOUT_PAGE = WIKI_DIR / "02-project-layout.md"

CLIENT_PROJECT_DIR = Path("XE-Local-AI-Engine.Client")
LOCAL_API_ROUTES = CLIENT_PROJECT_DIR / "Endpoints" / "Common" / "LocalApiRoutes.cs"
REACT_FEATURES_DIR = Path("XE-Local-AI-Engine.Client.React/src/features")
SOLUTION_FILE = Path("XE-Local-AI-Engine.slnx")

# `app.MapHub<SchedulerHub>(...)` — the registration call site is the hub inventory (see the note in
# 09-api-and-hubs.md §2, which says exactly that).
MAP_HUB_RE = re.compile(r"MapHub<\s*(?P<hub>[A-Za-z0-9_]+)\s*>")
# Route families are the classes nested one level inside `LocalApiRoutes`, i.e. at exactly one
# indent step. Anchoring on the indent keeps the outer class out of the inventory without having to
# parse C#.
NESTED_ROUTE_CLASS_RE = re.compile(r"^ {4}public static class (?P<name>[A-Za-z0-9_]+)\b", re.MULTILINE)
SOLUTION_PROJECT_RE = re.compile(r"<Project\s+Path=\"(?P<path>[^\"]+)\"")
NUMBERED_WIKI_PAGE_GLOB = "[0-9][0-9]-*.md"
BUILD_OUTPUT_DIRS = frozenset({"bin", "obj"})


class InventoryError(Exception):
    """A check could not be evaluated — a missing file, or an inventory that came back empty."""


@dataclass(frozen=True)
class Missing:
    """One inventory member that no longer appears in the page claiming to list it."""

    check: str
    name: str
    doc: Path

    def render(self) -> str:
        return f"MISSING {self.check}: {self.name} — expected in {self.doc.as_posix()}"


@dataclass(frozen=True)
class CheckResult:
    check: str
    doc: Path
    inventory: tuple[str, ...]
    missing: tuple[Missing, ...]


def read_text(root: Path, relative: Path) -> str:
    path = root / relative
    if not path.is_file():
        raise InventoryError(f"{relative.as_posix()} does not exist under {root}")
    return path.read_text(encoding="utf-8")


def require_non_empty(check: str, source: str, names: Iterable[str]) -> tuple[str, ...]:
    inventory = tuple(sorted(set(names)))
    if not inventory:
        raise InventoryError(f"{check}: found no entries in {source} — the inventory cannot be empty")
    return inventory


def mentions(doc_text: str, name: str) -> bool:
    return name in doc_text


def build_result(check: str, doc: Path, doc_text: str, inventory: tuple[str, ...]) -> CheckResult:
    missing = tuple(Missing(check, name, doc) for name in inventory if not mentions(doc_text, name))
    return CheckResult(check=check, doc=doc, inventory=inventory, missing=missing)


def check_signalr_hubs(root: Path) -> CheckResult:
    """Every hub registered with `MapHub<>` under the Client project is named in 09-api-and-hubs.md.

    Home.md is deliberately not checked: it is an index page whose row for 09 delegates the hub
    enumeration ("the local SignalR hubs registered by the `MapHub<>` block") rather than repeating
    it, and check `wiki-pages` already proves that link exists.
    """
    check = "signalr-hubs"
    source_dir = root / CLIENT_PROJECT_DIR
    if not source_dir.is_dir():
        raise InventoryError(f"{CLIENT_PROJECT_DIR.as_posix()} does not exist under {root}")

    hubs: list[str] = []
    for path in sorted(source_dir.rglob("*.cs")):
        if BUILD_OUTPUT_DIRS.intersection(path.parts):
            continue
        hubs.extend(match.group("hub") for match in MAP_HUB_RE.finditer(path.read_text(encoding="utf-8")))

    inventory = require_non_empty(check, f"MapHub<> calls under {CLIENT_PROJECT_DIR.as_posix()}", hubs)
    return build_result(check, API_AND_HUBS_PAGE, read_text(root, API_AND_HUBS_PAGE), inventory)


def check_local_api_route_families(root: Path) -> CheckResult:
    """Every nested route class in LocalApiRoutes.cs has its row in 09-api-and-hubs.md."""
    check = "local-api-routes"
    source = read_text(root, LOCAL_API_ROUTES)
    names = (match.group("name") for match in NESTED_ROUTE_CLASS_RE.finditer(source))
    inventory = require_non_empty(check, LOCAL_API_ROUTES.as_posix(), names)
    return build_result(check, API_AND_HUBS_PAGE, read_text(root, API_AND_HUBS_PAGE), inventory)


def check_react_features(root: Path) -> CheckResult:
    """Every directory under the React client's features/ root is named in 10-react-client.md."""
    check = "react-features"
    features_dir = root / REACT_FEATURES_DIR
    if not features_dir.is_dir():
        raise InventoryError(f"{REACT_FEATURES_DIR.as_posix()} does not exist under {root}")

    names = (entry.name for entry in features_dir.iterdir() if entry.is_dir())
    inventory = require_non_empty(check, REACT_FEATURES_DIR.as_posix(), names)
    return build_result(check, REACT_CLIENT_PAGE, read_text(root, REACT_CLIENT_PAGE), inventory)


def check_wiki_pages_linked(root: Path) -> CheckResult:
    """Every numbered wiki page is reachable from Home.md as a markdown link."""
    check = "wiki-pages"
    wiki_dir = root / WIKI_DIR
    if not wiki_dir.is_dir():
        raise InventoryError(f"{WIKI_DIR.as_posix()} does not exist under {root}")

    names = (page.name for page in wiki_dir.glob(NUMBERED_WIKI_PAGE_GLOB))
    inventory = require_non_empty(check, WIKI_DIR.as_posix(), names)

    home_text = read_text(root, HOME_PAGE)
    missing = tuple(
        Missing(check, name, HOME_PAGE)
        for name in inventory
        if not re.search(rf"\]\(\s*{re.escape(name)}(?:#[^)]*)?\s*\)", home_text)
    )
    return CheckResult(check=check, doc=HOME_PAGE, inventory=inventory, missing=missing)


def check_solution_projects(root: Path) -> CheckResult:
    """Every project (by .csproj name) enrolled in the solution is named in 02-project-layout.md."""
    check = "solution-projects"
    solution = read_text(root, SOLUTION_FILE)
    # Paths in the .slnx mix both separators (`A/A.csproj` and `A\A.csproj`). The project's identity is the
    # project file's own name (its stem), never the leading directory: a project enrolled *beneath* an already
    # documented directory (`Client/Plugins/NewPlugin.csproj`) must still be caught. Solution items that are
    # not project files (`Directory.Build.props`, `README.md`, ...) are skipped.
    names = (
        re.split(r"[\\/]", match.group("path"))[-1].removesuffix(".csproj")
        for match in SOLUTION_PROJECT_RE.finditer(solution)
        if match.group("path").endswith(".csproj")
    )
    inventory = require_non_empty(check, SOLUTION_FILE.as_posix(), names)
    return build_result(check, PROJECT_LAYOUT_PAGE, read_text(root, PROJECT_LAYOUT_PAGE), inventory)


CHECKS: tuple[Callable[[Path], CheckResult], ...] = (
    check_signalr_hubs,
    check_local_api_route_families,
    check_react_features,
    check_wiki_pages_linked,
    check_solution_projects,
)


def default_repo_root() -> Path:
    return Path(__file__).resolve().parents[1]


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog="docs-inventory-check",
        description="Fail when docs/wiki/ has fallen behind an inventory the code owns.",
    )
    parser.add_argument(
        "--repo-root",
        type=Path,
        default=default_repo_root(),
        help="Repository root to check (default: the directory containing scripts/).",
    )
    parser.add_argument(
        "--verbose",
        action="store_true",
        help="Print the size of every inventory that was checked, not only the failures.",
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    root: Path = args.repo_root.resolve()

    results: list[CheckResult] = []
    for check in CHECKS:
        try:
            results.append(check(root))
        except InventoryError as error:
            print(f"ERROR {error}", file=sys.stderr)
            return 2

    missing = [item for result in results for item in result.missing]

    if args.verbose:
        for result in results:
            print(
                f"checked {len(result.inventory)} {result.check} entries "
                f"against {result.doc.as_posix()} ({len(result.missing)} missing)"
            )

    for item in missing:
        print(item.render())

    items = sum(len(result.inventory) for result in results)
    print(f"docs-inventory-check: {len(results)} checks, {items} inventory entries, {len(missing)} missing")
    return 1 if missing else 0


if __name__ == "__main__":
    sys.exit(main())
