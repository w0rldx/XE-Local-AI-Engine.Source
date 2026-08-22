#!/usr/bin/env python3
"""Validate a repository-hosted Agent Skill without external dependencies.

Exit codes: 0 clean, 1 one or more validation violations, 2 the target cannot be checked.
"""

from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import unquote, urlsplit

NAME_RE = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
LINK_RE = re.compile(r"!?\[[^\]]*\]\((?P<target>[^)]+)\)")
TOP_LEVEL_FIELD_RE = re.compile(r"^(?P<key>[A-Za-z0-9_-]+):(?:\s*(?P<value>.*))?$")
REQUIRED_FIELDS = ("name", "description", "license")
MAX_DESCRIPTION_LENGTH = 1024
MAX_BODY_LINES_EXCLUSIVE = 500


class SkillLintError(Exception):
    """The requested skill cannot be inspected at all."""


@dataclass(frozen=True)
class SkillDocument:
    fields: dict[str, str]
    body: str


def parse_frontmatter(skill_file: Path) -> SkillDocument:
    text = skill_file.read_text(encoding="utf-8")
    lines = text.splitlines()
    if not lines or lines[0] != "---":
        return SkillDocument({}, text)

    try:
        closing_index = lines.index("---", 1)
    except ValueError:
        return SkillDocument({}, text)

    frontmatter = lines[1:closing_index]
    fields: dict[str, str] = {}
    index = 0
    while index < len(frontmatter):
        match = TOP_LEVEL_FIELD_RE.match(frontmatter[index])
        if match is None:
            index += 1
            continue

        key = match.group("key")
        value = (match.group("value") or "").strip()
        if value in {">", ">-", "|", "|-"}:
            continuation: list[str] = []
            index += 1
            while index < len(frontmatter) and (not frontmatter[index] or frontmatter[index][0].isspace()):
                continuation.append(frontmatter[index].strip())
                index += 1
            fields[key] = " ".join(part for part in continuation if part)
            continue

        fields[key] = value.strip("\"'")
        index += 1

    return SkillDocument(fields, "\n".join(lines[closing_index + 1 :]))


def resolve_repo_root(skill_dir: Path, requested_root: Path | None) -> Path:
    if requested_root is not None:
        root = requested_root.resolve()
    elif skill_dir.parent.name == "skills":
        root = skill_dir.parent.parent.resolve()
    else:
        root = Path.cwd().resolve()
    if not root.is_dir():
        raise SkillLintError(f"repository root does not exist: {root}")
    return root


def markdown_files(skill_dir: Path) -> tuple[Path, ...]:
    skill_file = skill_dir / "SKILL.md"
    references = skill_dir / "references"
    files = [skill_file]
    if references.is_dir():
        files.extend(sorted(references.glob("*.md")))
    return tuple(files)


def link_target(raw_target: str) -> str:
    target = raw_target.strip()
    if target.startswith("<") and ">" in target:
        return target[1 : target.index(">")]
    # Markdown permits an optional title after a whitespace-delimited destination. The skill uses
    # angle brackets for destinations containing whitespace, so the first token is unambiguous here.
    return target.split(maxsplit=1)[0] if target else ""


def validate_links(skill_dir: Path, repo_root: Path) -> list[str]:
    violations: list[str] = []
    for markdown in markdown_files(skill_dir):
        for match in LINK_RE.finditer(markdown.read_text(encoding="utf-8")):
            raw = link_target(match.group("target"))
            parsed = urlsplit(raw)
            if not raw or raw.startswith("#") or parsed.scheme or parsed.netloc:
                continue
            destination = (markdown.parent / unquote(parsed.path)).resolve()
            if not destination.is_relative_to(repo_root):
                violations.append(f"{markdown.relative_to(repo_root)}: link escapes the repository: {raw}")
            elif not destination.is_file():
                violations.append(f"{markdown.relative_to(repo_root)}: link target does not exist: {raw}")
    return violations


def validate(skill_dir: Path, repo_root: Path | None = None) -> list[str]:
    skill_dir = skill_dir.resolve()
    if not skill_dir.is_dir():
        raise SkillLintError(f"skill directory does not exist: {skill_dir}")
    skill_file = skill_dir / "SKILL.md"
    if not skill_file.is_file():
        raise SkillLintError(f"SKILL.md does not exist under {skill_dir}")

    root = resolve_repo_root(skill_dir, repo_root)
    if not skill_dir.is_relative_to(root):
        raise SkillLintError(f"skill directory is outside repository root: {skill_dir}")

    document = parse_frontmatter(skill_file)
    violations: list[str] = []
    for field in REQUIRED_FIELDS:
        if not document.fields.get(field, "").strip():
            violations.append(f"SKILL.md frontmatter requires a non-empty {field} field")

    name = document.fields.get("name", "")
    if name and name != skill_dir.name:
        violations.append(f"frontmatter name '{name}' must equal parent directory name '{skill_dir.name}'")
    if name and NAME_RE.fullmatch(name) is None:
        violations.append("frontmatter name must contain lowercase letters, digits, and single hyphens only")

    description = document.fields.get("description", "")
    if description and not 1 <= len(description) <= MAX_DESCRIPTION_LENGTH:
        violations.append(
            f"frontmatter description must be 1..{MAX_DESCRIPTION_LENGTH} characters (found {len(description)})"
        )

    body_lines = len(document.body.splitlines())
    if body_lines >= MAX_BODY_LINES_EXCLUSIVE:
        violations.append(f"SKILL.md body must be under {MAX_BODY_LINES_EXCLUSIVE} lines (found {body_lines})")

    violations.extend(validate_links(skill_dir, root))
    return violations


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(prog="skill-lint", description="Validate a repository-hosted Agent Skill.")
    parser.add_argument("skill_dir", type=Path, help="Path to the skill directory containing SKILL.md.")
    parser.add_argument("--repo-root", type=Path, help="Repository root used to constrain relative links.")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        violations = validate(args.skill_dir, args.repo_root)
    except (OSError, SkillLintError) as error:
        print(f"skill-lint: {error}", file=sys.stderr)
        return 2

    for violation in violations:
        print(f"skill-lint: {violation}", file=sys.stderr)
    return 1 if violations else 0


if __name__ == "__main__":
    raise SystemExit(main())
