#!/usr/bin/env python3
"""Fail when the docs have fallen behind an inventory the code owns, or agent-knowledge outgrows its caps.

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

The agent-knowledge checks are a growth guard rather than an inventory mention check: the always-read
index `docs/agent-knowledge.md` must link every topic file under `docs/agent-knowledge/`, stay under its
byte cap and keep its `## 0.`..`## 7.` anchors (code comments cite them), and every rule entry in a topic
file must stay short. A previous compaction regrew within a month; the caps make that a CI failure.

The markdown-links check walks every tracked Markdown file (`git ls-files`, minus vendored agent templates,
`LiveCorpus/` fixtures and `third-party/`) and resolves each relative inline link and reference definition
against the file's own directory: the target must exist, and a `#fragment` on a Markdown target (or a bare
same-file `#fragment`) must match a GitHub heading slug in that file or an explicit `id=`/`name=` HTML anchor.
Slugs follow GitHub: lowercase, inline markers dropped, every character that is not a letter, digit, space,
hyphen or underscore removed, spaces become hyphens without collapsing, and duplicates get `-1`, `-2`, ...
Moved or renamed pages are the failure it catches; external URLs are not fetched.

Four hygiene checks scan tracked files for rules AGENTS.md states: `file-line-citations` (no `file.cs:123` citation
outside fences in the instruction and wiki docs), `tracked-secrets` (no node.key, SQLite file, .env, dp-keys/ or
*.enc tracked), `tool-names` (no per-user agent or editor tool config named in product, gate, test or instruction
files) and `host-phrasing` (no home path, uid, subuid range or "this box" in docs; no RAM/CPU inventory unless the
line is dated). Their git calls drop the repository-selection variables (GIT_DIR, GIT_INDEX_FILE, ...), so an
exported private index cannot answer for the work tree. Their scope is narrowed by path, never by word:
- tool-names denylists config and marker forms (`.claude/`, `CLAUDE.md`, `.codex/`, `ponytail`, ...), not the product
  name of a third-party MCP client, which the inbound MCP surface legitimately names. The installers and their tests
  are exempt: they place the shipped skill into the user's agent skill directories.
- host-phrasing allows the bare word `subuid` (rootless-Docker product vocabulary) and bans a concrete
  `user:start:count` range entry. `docs/user-guide/` (end-user system requirements) and `docs/audits/` (dated
  reports) are exempt, and a quoted "this box" is the rule being stated, not used.

Exit codes: 0 clean, 1 something is missing from a page or over a cap, 2 a check could not run at all.
"""

from __future__ import annotations

import argparse
import codecs
import os
import re
import subprocess
import sys
from collections.abc import Callable, Iterable
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import unquote

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

AGENT_KNOWLEDGE_INDEX = Path("docs/agent-knowledge.md")
AGENT_KNOWLEDGE_DIR = Path("docs/agent-knowledge")
AGENT_KNOWLEDGE_EVIDENCE = "docs/agent-knowledge-evidence.md"
# Pending proposals collect here until promoted; the entry caps and the PROPOSED ban do not apply to it.
AGENT_KNOWLEDGE_PROPOSED = "proposed.md"
# The index is read in full before every non-trivial change, so its size is paid on every task.
AGENT_KNOWLEDGE_INDEX_MAX_BYTES = 12288
# A topic file is read whole when its area is touched; above this it stops being a quick read.
AGENT_KNOWLEDGE_TOPIC_MAX_BYTES = 32768
# An entry is rule, failure prevented and authority; anything longer is narrative for the evidence ledger.
AGENT_KNOWLEDGE_ENTRY_MAX_CHARS = 900
# Inbound code comments cite `docs/agent-knowledge.md §N`, so these section anchors must survive.
AGENT_KNOWLEDGE_SECTIONS = range(8)
MARKDOWN_HEADING_RE = re.compile(r"^#{1,6}\s", re.MULTILINE)
ENTRY_HEADING_RE = re.compile(r"^###\s+(?P<title>.*)$", re.MULTILINE)
BUILD_OUTPUT_DIRS = frozenset({"bin", "obj"})

# Vendored, generated or corpus Markdown whose links point into trees that are not part of this repository.
MARKDOWN_LINK_EXCLUDED_PREFIXES = ("XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/",)
MARKDOWN_LINK_EXCLUDED_SEGMENTS = ("/LiveCorpus/", "third-party/")
# CommonMark: a backtick fence's info string holds no backtick (so ```x``` is inline code); a closing fence is a
# bare marker at least as long as the opener.
FENCE_RE = re.compile(r"^ {0,3}(?P<fence>`{3,}(?=[^`]*$)|~{3,})(?P<info>.*)$")
ATX_HEADING_RE = re.compile(r"^ {0,3}#{1,6}[ \t]+(?P<text>.*?)(?:[ \t]+#+)?[ \t]*$")
HTML_ANCHOR_RE = re.compile(r"\b(?:id|name)\s*=\s*[\"'](?P<anchor>[^\"']+)[\"']")
CODE_SPAN_RE = re.compile(r"(`+)(?:.+?)\1")
INLINE_LINK_RE = re.compile(r"\]\(\s*(?P<target><[^>]*>|[^)\s]+)(?:\s+(?:\"[^\"]*\"|'[^']*'|\([^)]*\)))?\s*\)")
REFERENCE_DEFINITION_RE = re.compile(r"^ {0,3}\[[^\]]+\]:\s*(?P<target><[^>]*>|\S+)")
SKIPPED_LINK_SCHEMES = ("http://", "https://", "mailto:")
# Inside a heading: a code span keeps its text, a link keeps its label, HTML tags and `*` markers vanish, and
# `_` vanishes only as an emphasis marker (not between two letters/digits, as in `gen_aitool`).
HEADING_MARKUP_RE = re.compile(
    r"`+(?P<code>[^`]*)`+|!?\[(?P<label>[^\]]*)\]\([^)]*\)|<[^>]+>|\*+|(?<![^\W_])_+|_+(?![^\W_])"
)

# `file-line-citations`: the instruction and architecture docs cite code as file + symbol (AGENTS.md); git's `*`
# crosses `/`, so these pathspecs cover docs/agent-knowledge/ and every wiki subdirectory.
CITATION_DOC_PATHSPECS = (
    "docs/agent-knowledge*.md",
    "docs/wiki/*.md",
    "AGENTS.md",
    "XE-Local-AI-Engine.Client.React/AGENTS.md",
)
FILE_LINE_CITATION_RE = re.compile(
    r"\b[\w./-]+\.(?:cs|csproj|slnx|props|targets|ts|tsx|js|jsx|mjs|cjs|py|sh|ps1|psm1|json|ya?ml|toml|md)#?:\d+\b"
)

# `tracked-secrets`: the runtime secret and state files AGENTS.md forbids committing. `.env` is the exact basename,
# so the tracked `.env.template` / `.env.tests` stay allowed.
TRACKED_SECRET_RE = re.compile(r"(?:^|/)(?:node\.key|\.env|[^/]*\.sqlite(?:-wal|-shm)?|[^/]*\.enc)$|(?:^|/)dp-keys/")

# `tool-names`: the repo never depends on optional per-user agent or editor tooling. The denylist is the config and
# marker forms from .gitignore's per-user tool entries plus well-known agent configs; the product name of a
# third-party MCP client is not on it (the inbound MCP surface names its clients). CHANGELOG.md, docs/audits/,
# docs/adr/ and the MCP-client runbooks are outside the pathspecs.
TOOL_NAME_PATHSPECS = (
    "*.cs",
    "*.ts",
    "*.tsx",
    "*.js",
    "*.jsx",
    "*.mjs",
    "*.cjs",
    "*.sh",
    "*.py",
    "*.ps1",
    "*.psm1",
    "*.psd1",
    "*.yml",
    "*.yaml",
    "*.toml",
    "*.props",
    "*.targets",
    "*.csproj",
    "package.json",
    "*/package.json",
    "AGENTS.md",
    "*/AGENTS.md",
    "docs/wiki/*",
    "docs/agent-knowledge*",
)
TOOL_NAME_EXEMPT_PATHS = frozenset(
    {
        # Shipped product surface: the installers place the engine's skill into the user's agent skill directories.
        "install.sh",
        "install.ps1",
        "publish/tests/install.Tests.ps1",
        "scripts/tests/install.test.sh",
        # Shipped product surface: the workspace copy skips an end user's IDE metadata directories (`.vs`, `.idea`).
        "XE-Local-AI-Engine.Client.Application/Services/Workspace/Implementation/SensitiveFileExclusionService.cs",
        "XE-Local-AI-Engine.Tests/Workspace/SensitiveFileExclusionServiceTests.cs",
        # This guard and its tests spell the denylist.
        "scripts/docs-inventory-check.py",
        "scripts/tests/test_docs_inventory_check.py",
    }
)
# A directory token (trailing `/`) ends at either separator, a quote, a backtick, whitespace or the line end, so
# `Path.Combine(home, ".codex", ...)` and a bare `".claude"` literal match as well as `.claude/settings.json`. It
# never follows a word character: `this.cursor = 0` and `"navigation.agents"` are member access and i18n keys.
TOOL_NAME_DIRECTORY_END = r"""(?:[\\/"'`\s]|$)"""
TOOL_NAME_DENYLIST = tuple(
    re.compile(r"(?<!\w)" + pattern[:-1] + TOOL_NAME_DIRECTORY_END if pattern.endswith("/") else pattern)
    for pattern in (
        r"\.claude/",
        r"\bCLAUDE(?:\.local)?\.md\b",
        r"\.codex/",
        r"\.cursor/",
        r"\.cursorrules\b",
        r"\.omc/",
        r"\.omx/",
        r"\boh-my-claudecode\b",
        r"\.junie/",
        r"\.agents/",
        r"\.testagent/",
        r"\.codegraph/",
        r"\.mcp\.json\b",
        r"\brepomix\b",
        r"\.windsurf",
        r"\.aider",
        r"\.serena/",
        r"copilot-instructions\.md",
        r"\bponytail\b",
        r"\.idea/",
        r"""(?<!\w)\.vscode(?:[\\/](?!extensions\.json)|["'`\s]|$)""",
    )
)

# `host-phrasing`: this is a public repository; docs describe the environment generically. Hardware is allowed as
# metadata of a dated measurement, so a line carrying an ISO date is exempt from the RAM/CPU patterns only. The user
# guide states end-user system requirements and docs/audits/ holds dated reports. `subuid` alone is product
# vocabulary (rootless Docker); a concrete `user:start:count` range entry is the leak.
HOST_PHRASING_PATHSPECS = ("docs/*.md", "AGENTS.md", "*/AGENTS.md")
HOST_PHRASING_EXEMPT_PREFIXES = ("docs/user-guide/", "docs/audits/")
HOST_PHRASING_HARDWARE_PATTERNS = frozenset(re.compile(pattern) for pattern in (r"\b\d+ ?GB RAM\b", r"\b\d+-core\b"))
HOST_PHRASING_PATTERNS = (
    *(
        re.compile(pattern)
        for pattern in (
            r"(?<![\w.-])/home/[a-z_][\w-]*",
            r"/run/user/\d+",
            r"\buid=",
            r"\b[a-z_][\w-]*:\d{5,}:\d+\b",
            r"(?<![\"'])\b[Tt]his box\b",
        )
    ),
    *sorted(HOST_PHRASING_HARDWARE_PATTERNS, key=lambda pattern: pattern.pattern),
)
# A date excuses only the hardware inventory above; a home path, uid, subuid range or "this box" is never metadata.
ISO_DATE_RE = re.compile(r"\b\d{4}-\d{2}-\d{2}\b")

# Variables that make git select another repository, work tree, index or object store than `cwd` (the same list as
# scripts/lib/git-env.sh). An agent's exported private GIT_INDEX_FILE would otherwise answer `git ls-files` from a
# stale index and hide a newly tracked secret. Discovery bounds (GIT_CEILING_DIRECTORIES, ...) and GIT_CONFIG* stay.
GIT_REPO_ENV_VARS = (
    "GIT_DIR",
    "GIT_WORK_TREE",
    "GIT_INDEX_FILE",
    "GIT_COMMON_DIR",
    "GIT_OBJECT_DIRECTORY",
    "GIT_ALTERNATE_OBJECT_DIRECTORIES",
    "GIT_NAMESPACE",
)


class InventoryError(Exception):
    """A check could not be evaluated — a missing file, or an inventory that came back empty."""


@dataclass(frozen=True)
class Missing:
    """One inventory member that no longer appears in the page claiming to list it."""

    check: str
    name: str
    doc: Path
    # Set for a violation that is not an absence (a size cap, a misplaced heading); replaces the MISSING wording.
    problem: str = ""
    # The row's prefix when `problem` is set.
    label: str = "OVER-CAP"

    def render(self) -> str:
        if self.problem:
            return f"{self.label} {self.check}: {self.doc.as_posix()}: {self.problem}"
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

    # Hidden directories are per-user tool or cache state (`.cache/`, editor and agent scratch), never a
    # feature; skip them so a local, git-ignored artefact does not fail the guard on a developer machine.
    names = (entry.name for entry in features_dir.iterdir() if entry.is_dir() and not entry.name.startswith("."))
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


def agent_knowledge_topic_files(root: Path, check: str) -> list[Path]:
    topic_dir = root / AGENT_KNOWLEDGE_DIR
    if not topic_dir.is_dir():
        raise InventoryError(f"{AGENT_KNOWLEDGE_DIR.as_posix()} does not exist under {root}")
    files = sorted(topic_dir.glob("*.md"))
    require_non_empty(check, AGENT_KNOWLEDGE_DIR.as_posix(), (path.name for path in files))
    return files


def check_agent_knowledge_index(root: Path) -> CheckResult:
    """The index links every topic file, stays under its byte cap, and keeps its `## 0.`..`## 7.` anchors."""
    check = "agent-knowledge-index"
    names = tuple(path.name for path in agent_knowledge_topic_files(root, check))
    index_text = read_text(root, AGENT_KNOWLEDGE_INDEX)
    index = AGENT_KNOWLEDGE_INDEX

    missing = [
        Missing(check, name, index)
        for name in names
        if not re.search(rf"\]\(\s*[^)\s]*agent-knowledge/{re.escape(name)}(?:#[^)]*)?\s*\)", index_text)
    ]
    missing.extend(
        Missing(check, f"## {section}.", index)
        for section in AGENT_KNOWLEDGE_SECTIONS
        if not re.search(rf"^## {section}\.", index_text, re.MULTILINE)
    )
    size = len(index_text.encode("utf-8"))
    if size > AGENT_KNOWLEDGE_INDEX_MAX_BYTES:
        missing.append(
            Missing(
                check,
                "size",
                index,
                f"index is {size} bytes, cap {AGENT_KNOWLEDGE_INDEX_MAX_BYTES}; move detail into a topic file",
            )
        )
    return CheckResult(check=check, doc=index, inventory=names, missing=tuple(missing))


def check_agent_knowledge_entries(root: Path) -> CheckResult:
    """Every topic file but proposed.md stays under its byte cap, with short entries and no PROPOSED heading."""
    check = "agent-knowledge-entries"
    files = [path for path in agent_knowledge_topic_files(root, check) if path.name != AGENT_KNOWLEDGE_PROPOSED]
    inventory = require_non_empty(check, AGENT_KNOWLEDGE_DIR.as_posix(), (path.name for path in files))

    missing: list[Missing] = []
    for path in files:
        doc = AGENT_KNOWLEDGE_DIR / path.name
        text = path.read_text(encoding="utf-8")
        size = len(text.encode("utf-8"))
        if size > AGENT_KNOWLEDGE_TOPIC_MAX_BYTES:
            missing.append(
                Missing(
                    check,
                    path.name,
                    doc,
                    f"file is {size} bytes, cap {AGENT_KNOWLEDGE_TOPIC_MAX_BYTES}; "
                    f"condense entries or move narrative to {AGENT_KNOWLEDGE_EVIDENCE}",
                )
            )
        # Known limit: headings inside fenced code blocks are not skipped; a `###` line in a fence splits an entry.
        for match in ENTRY_HEADING_RE.finditer(text):
            title = match.group("title").strip()
            if title.startswith("PROPOSED"):
                missing.append(
                    Missing(
                        check, title, doc, f'"### {title}" is a pending proposal; move it to {AGENT_KNOWLEDGE_PROPOSED}'
                    )
                )
            next_heading = MARKDOWN_HEADING_RE.search(text, match.end())
            body = text[match.end() : next_heading.start() if next_heading else len(text)].strip()
            if len(body) > AGENT_KNOWLEDGE_ENTRY_MAX_CHARS:
                missing.append(
                    Missing(
                        check,
                        title,
                        doc,
                        f'entry "### {title}" is {len(body)} chars, cap {AGENT_KNOWLEDGE_ENTRY_MAX_CHARS}; '
                        f"move narrative to {AGENT_KNOWLEDGE_EVIDENCE}",
                    )
                )
    return CheckResult(check=check, doc=AGENT_KNOWLEDGE_DIR, inventory=inventory, missing=tuple(missing))


def github_slug(heading: str) -> str:
    """The anchor GitHub derives from a heading's text, before duplicate suffixes."""
    text = HEADING_MARKUP_RE.sub(lambda m: m.group("code") or m.group("label") or "", heading).lower()
    return "".join(c for c in text if c.isalnum() or c in " -_").replace(" ", "-")


def markdown_lines_outside_fences(text: str) -> Iterable[str]:
    fence = ""
    for line in text.splitlines():
        match = FENCE_RE.match(line)
        if fence:
            if (
                match
                and match.group("fence")[0] == fence[0]
                and len(match.group("fence")) >= len(fence)
                and not match.group("info").strip()
            ):
                fence = ""
        elif match:
            fence = match.group("fence")
        else:
            yield line


def markdown_anchors(text: str) -> frozenset[str]:
    """Every fragment a link into this file may use: heading slugs (with -1, -2 suffixes) and HTML ids."""
    anchors: set[str] = set(HTML_ANCHOR_RE.findall(text))
    occurrences: dict[str, int] = {}
    for line in markdown_lines_outside_fences(text):
        match = ATX_HEADING_RE.match(line)
        if not match:
            continue
        base = slug = github_slug(match.group("text"))
        while slug in occurrences:
            occurrences[base] += 1
            slug = f"{base}-{occurrences[base]}"
        occurrences[slug] = 0
        anchors.add(slug)
    return frozenset(anchors)


def markdown_link_targets(text: str) -> Iterable[str]:
    for line in markdown_lines_outside_fences(text):
        definition = REFERENCE_DEFINITION_RE.match(line)
        if definition:
            yield definition.group("target").strip("<>")
            continue
        for match in INLINE_LINK_RE.finditer(CODE_SPAN_RE.sub("", line)):
            yield match.group("target").strip("<>")


def git_ls_files(root: Path, check: str, *pathspecs: str) -> list[str]:
    """Tracked paths (posix, repo-relative) matching `pathspecs`; git's `*` also matches `/`."""
    env = {key: value for key, value in os.environ.items() if key not in GIT_REPO_ENV_VARS}
    try:
        listed = subprocess.run(
            ["git", "ls-files", "-z", "--", *pathspecs], cwd=root, env=env, capture_output=True, check=True, text=True
        ).stdout
    except (OSError, subprocess.CalledProcessError) as error:
        raise InventoryError(f"{check}: git ls-files failed under {root}: {error}") from error
    return [name for name in listed.split("\0") if name]


def tracked_markdown_files(root: Path) -> list[Path]:
    return [
        root / name
        for name in git_ls_files(root, "markdown-links", "*.md")
        if (root / name).is_file()  # a tracked file deleted in the working tree is not a link source
        and not name.startswith(MARKDOWN_LINK_EXCLUDED_PREFIXES)
        and not any(segment in f"/{name}" for segment in MARKDOWN_LINK_EXCLUDED_SEGMENTS)
    ]


def check_markdown_links(root: Path, files: Iterable[Path] | None = None) -> CheckResult:
    """Every relative link in a tracked Markdown file resolves, and every `#fragment` into Markdown exists.

    `files` replaces the `git ls-files` inventory, so the parsing can be tested without a git repository.
    """
    check = "markdown-links"
    sources = sorted(tracked_markdown_files(root) if files is None else files)
    inventory = require_non_empty(check, "tracked Markdown files", (p.relative_to(root).as_posix() for p in sources))
    anchor_cache: dict[Path, frozenset[str]] = {}

    def anchors_of(path: Path) -> frozenset[str]:
        if path not in anchor_cache:
            anchor_cache[path] = markdown_anchors(path.read_text(encoding="utf-8"))
        return anchor_cache[path]

    missing: list[Missing] = []
    for source in sources:
        doc = source.relative_to(root)
        for target in markdown_link_targets(source.read_text(encoding="utf-8")):
            if target.startswith(SKIPPED_LINK_SCHEMES):
                continue
            path_part, _, fragment = unquote(target).partition("#")
            if path_part.startswith("/"):
                resolved = root / path_part.lstrip("/")
            else:
                resolved = (source.parent / path_part) if path_part else source
            if not resolved.exists():
                missing.append(Missing(check, target, doc, f"broken path: {target}", "BROKEN"))
            elif fragment and resolved.is_file() and resolved.suffix == ".md" and fragment not in anchors_of(resolved):
                missing.append(Missing(check, target, doc, f"missing anchor: {target}", "BROKEN"))
    return CheckResult(check=check, doc=Path("."), inventory=inventory, missing=tuple(missing))


def decode_text(data: bytes) -> str | None:
    """UTF-8 (BOM stripped), or UTF-16 when the file starts with a UTF-16 BOM; None when neither decodes."""
    encoding = "utf-16" if data.startswith((codecs.BOM_UTF16_LE, codecs.BOM_UTF16_BE)) else "utf-8-sig"
    try:
        return data.decode(encoding)
    except UnicodeDecodeError:
        return None


def scan_lines(
    root: Path,
    check: str,
    names: Iterable[str],
    patterns: Iterable[re.Pattern[str]],
    label: str,
    lines_of: Callable[[str], Iterable[str]] = str.splitlines,
    line_exempt: re.Pattern[str] | None = None,
    exempt_patterns: frozenset[re.Pattern[str]] = frozenset(),
) -> CheckResult:
    """Report every match of `patterns` in the given tracked files, one row per file line and pattern.

    A line matching `line_exempt` skips only the patterns in `exempt_patterns`; every other pattern still applies.
    """
    inventory = require_non_empty(check, "tracked files in scope", names)
    missing: list[Missing] = []
    for name in inventory:
        path = root / name
        if not path.is_file():  # a tracked symlink to a directory, or a deletion not yet staged
            continue
        text = decode_text(path.read_bytes())
        if text is None:  # never skipped: a file this scan cannot read is a file it cannot clear
            missing.append(Missing(check, name, Path(name), f"undecodable: {name}", label))
            continue
        for number, line in enumerate(lines_of(text), start=1):
            exempt = line_exempt is not None and line_exempt.search(line) is not None
            for pattern in patterns:
                if exempt and pattern in exempt_patterns:
                    continue
                for hit in pattern.finditer(line):
                    excerpt = line[max(0, hit.start() - 80) : hit.end() + 80].strip()
                    problem = f"line {number}: {hit.group(0)!r} in: {excerpt}"
                    missing.append(Missing(check, hit.group(0), Path(name), problem, label))
    return CheckResult(check=check, doc=Path("."), inventory=inventory, missing=tuple(missing))


def check_file_line_citations(root: Path) -> CheckResult:
    """Instruction and architecture docs cite code as file + symbol, never `file:line` (lines drift)."""
    check = "file-line-citations"
    names = git_ls_files(root, check, *CITATION_DOC_PATHSPECS)
    return scan_lines(root, check, names, (FILE_LINE_CITATION_RE,), "CITATION", markdown_lines_outside_fences)


def check_tracked_secrets(root: Path) -> CheckResult:
    """No runtime secret or state file is tracked: node.key, SQLite databases, .env, dp-keys/, *.enc."""
    check = "tracked-secrets"
    inventory = require_non_empty(check, "git ls-files", git_ls_files(root, check))
    missing = tuple(
        Missing(check, name, Path(name), "runtime secret or state file is tracked; git rm --cached it", "TRACKED")
        for name in inventory
        if TRACKED_SECRET_RE.search(name)
    )
    return CheckResult(check=check, doc=Path("."), inventory=inventory, missing=missing)


def check_tool_names(root: Path) -> CheckResult:
    """Product, gate, test and instruction files name no optional per-user agent or editor tooling."""
    check = "tool-names"
    names = [name for name in git_ls_files(root, check, *TOOL_NAME_PATHSPECS) if name not in TOOL_NAME_EXEMPT_PATHS]
    return scan_lines(root, check, names, TOOL_NAME_DENYLIST, "TOOL-NAME")


def check_host_phrasing(root: Path) -> CheckResult:
    """Docs describe the environment generically: no home paths, uids, subuid ranges, RAM/CPU inventory."""
    check = "host-phrasing"
    names = [
        name
        for name in git_ls_files(root, check, *HOST_PHRASING_PATHSPECS)
        if not name.startswith(HOST_PHRASING_EXEMPT_PREFIXES)
    ]
    return scan_lines(
        root,
        check,
        names,
        HOST_PHRASING_PATTERNS,
        "HOST",
        line_exempt=ISO_DATE_RE,
        exempt_patterns=HOST_PHRASING_HARDWARE_PATTERNS,
    )


CHECKS: tuple[Callable[[Path], CheckResult], ...] = (
    check_signalr_hubs,
    check_local_api_route_families,
    check_react_features,
    check_wiki_pages_linked,
    check_solution_projects,
    check_agent_knowledge_index,
    check_agent_knowledge_entries,
    check_markdown_links,
    check_file_line_citations,
    check_tracked_secrets,
    check_tool_names,
    check_host_phrasing,
)


def default_repo_root() -> Path:
    return Path(__file__).resolve().parents[1]


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog="docs-inventory-check",
        description=(
            "Fail when docs/wiki/ has fallen behind an inventory the code owns, "
            "docs/agent-knowledge outgrows its size caps, or a relative Markdown link or anchor is broken."
        ),
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
