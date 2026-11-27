#!/usr/bin/env python3
"""Fail when a branch widens a shrink-only allowlist or baseline against its merge base with develop.

Usage: scripts/ratchet-baseline-check.py <branch> [--repo <path>] [--base <ref>]

Each guard reads its allowlist from the tree it checks, so a change that adds an exception together with its row,
or raises a ceiling, passes that guard. This compares every shrink-only data file below at the tip of <branch>
with the merge base of <branch> and --base (default develop) and prints one line per widening: ADDED (a key the base
lacks; a same-count replacement is an addition plus a deletion), RAISED (a higher limit; a key without a limit
field counts the times it is listed; a file-length include's maxLines) or NARROWED (a file-length rule include the
merge base enforces at error level is gone, or a TypeScript source under the React src/ that the merge base's top-level
files/linter includes select and the tip's do not). Deleted keys and lowered limits pass.
biome.json is checked closed-world, not modelled: every setting at the tip outside the allowed shapes is REJECTED
(biome_shape_violations; BiomeFileLengthOffList.test.mjs enforces the same shapes), any biome.json, biome.jsonc or
.ignore below the React project root other than its own biome.json is ADDED (a nested configuration or ignore file can
switch linting off), and so is every tracked TypeScript source under its src/ that the tip's .gitignore files ignore
(Biome's vcs.useIgnoreFile skips it; git itself judges, on those files alone), and every one that is linted and covered
by the file-length rule and whose size exceeds Biome's files.maxSize (Biome skips it with a warning) or that holds a
suppression comment naming the rule, its style group or the lint group.
A file the merge base lacks is a new baseline: printed as NEW and allowed once. An *Allowlist.txt under
XE-Local-AI-Engine.Tests/Architecture/ that this script does not know stops the comparison when the merge base holds
it, so a landed guard's list cannot pass unread. One only the branch adds is a new baseline too, printed as NEW
(unparsed by this checker): squash-guard.sh runs the develop copy of this script, which cannot know the parser entry the
same branch adds. Reads git objects only.

XE_RATCHET_WIDEN_REASON=<text>, set by the operator running scripts/squash-guard.sh, accepts the printed widening:
every finding and the reason are still printed, then exit 0.

Exit codes: 0 nothing widened (or the widening was accepted), 1 a baseline widened, 2 the comparison could not run
(no merge base, a missing branch, an unknown allowlist at the merge base, a malformed row, a python3 older than 3.13).
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import re
import subprocess
import sys
import tempfile
from collections import Counter
from pathlib import Path, PurePosixPath

ARCHITECTURE_DIR = "XE-Local-AI-Engine.Tests/Architecture/"
REACT_DIR = "XE-Local-AI-Engine.Client.React/"
# path -> (key fields, limit field): a row's key is its first `key fields` |-separated fields; its limit is the
# integer in the limit field, or the number of times the key is listed when the file has none. Text after `#` is a
# comment (TryOutParameterNullOnSuccessAllowlist.txt writes its reasons that way).
PIPE_BASELINES: dict[str, tuple[int, int | None]] = {
    ARCHITECTURE_DIR + "CommentBudgetAllowlist.txt": (2, 2),
    ARCHITECTURE_DIR + "DbContextUserAllowlist.txt": (1, None),
    ARCHITECTURE_DIR + "FilePlacementAllowlist.txt": (1, 1),
    ARCHITECTURE_DIR + "FileSizeAllowlist.txt": (1, 1),
    ARCHITECTURE_DIR + "PositionalRecordAllowlist.txt": (2, None),
    ARCHITECTURE_DIR + "RealTimerAllowlist.txt": (1, None),
    ARCHITECTURE_DIR + "SharedTestDoubleAllowlist.txt": (2, None),
    ARCHITECTURE_DIR + "SingleImplementationInterfaceAllowlist.txt": (1, None),
    ARCHITECTURE_DIR + "TestClassHostAllowlist.txt": (2, None),
    ARCHITECTURE_DIR + "TimeProviderDefaultAllowlist.txt": (1, None),
    ARCHITECTURE_DIR + "TryOutParameterNullOnSuccessAllowlist.txt": (1, None),
    ARCHITECTURE_DIR + "TupleAllowlist.txt": (1, 1),
    ARCHITECTURE_DIR + "UnhandledExceptionAllowlist.txt": (1, None),
    "scripts/docs-inventory-ratchet.txt": (2, 2),
}
# JSON arrays of fingerprints (Knip and dependency-cruiser no-growth baselines).
JSON_BASELINES = (
    REACT_DIR + "config/knip-baseline.json",
    REACT_DIR + "config/knip-production-baseline.json",
    REACT_DIR + "config/dependency-baseline.json",
)
# The noExcessiveLinesPerFile off-list and its maxLines (BiomeFileLengthOffList.test.mjs).
BIOME_CONFIG = REACT_DIR + "biome.json"
BIOME_RULE = "noExcessiveLinesPerFile"
BIOME_DEFAULT_MAX_SIZE = 1024 * 1024
# A suppression comment (line, range or file-wide) naming the rule, its style group or the whole lint group, for
# git grep -i -E; BiomeFileLengthOffList.test.mjs (SUPPRESSION) rejects the same comments.
SUPPRESSION = (
    r"biome-ignore(-all|-start)?([[:blank:]]+[^[:blank:]:]+)*[[:blank:]]+lint(/style(/"
    + BIOME_RULE
    + r")?)?(\([^)]*\))?([[:blank:]:*]|$)"
)
# Keys of the files an error-level file-length rule covers: shrink-only the other way round, so losing one widens.
COVERAGE = "covers|"
# A covered include's maxLines: raising it widens, a new include is new coverage.
MAX_LINES = "maxLines|"
OVERRIDE_VARIABLE = "XE_RATCHET_WIDEN_REASON"
# selected() matches Biome's includes with PurePosixPath.full_match (Python 3.13), the floor every scripts/** file
# targets on the host python3 (root pyproject.toml, per-file-target-version).
MIN_PYTHON = (3, 13)
# Variables that make git select another repository than --repo (scripts/lib/git_env.py).
_GIT_ENV_SPEC = importlib.util.spec_from_file_location(
    "git_env", Path(__file__).resolve().parent / "lib" / "git_env.py"
)
if _GIT_ENV_SPEC is None or _GIT_ENV_SPEC.loader is None:
    raise ImportError("scripts/lib/git_env.py")
_GIT_ENV = importlib.util.module_from_spec(_GIT_ENV_SPEC)
_GIT_ENV_SPEC.loader.exec_module(_GIT_ENV)
GIT_REPO_ENV_VARS = _GIT_ENV.REPO_ENV_VARS


class CheckError(Exception):
    """The comparison cannot run; never read as a pass."""


def git(repo: Path, *args: str, stdin: str | None = None, allowed: tuple[int, ...] = (0,)) -> str:
    env = {key: value for key, value in os.environ.items() if key not in GIT_REPO_ENV_VARS}
    try:
        result = subprocess.run(
            ["git", "-C", str(repo), *args],
            input=stdin,
            capture_output=True,
            encoding="utf-8",
            errors="surrogateescape",
            env=env,
            check=False,
        )
    except OSError as error:
        raise CheckError(f"git {' '.join(args)} failed: {error}") from error
    if result.returncode not in allowed:
        raise CheckError(f"git {' '.join(args)} failed with exit {result.returncode}: {result.stderr.strip()}")
    return result.stdout


def paths(repo: Path, *args: str, stdin: str | None = None, allowed: tuple[int, ...] = (0,)) -> list[str]:
    """The NUL-separated records of a git call given ``-z``: without it git quotes a name holding a non-ASCII byte,
    a tab, a line break, a quote or a backslash (``"src/Gr\\303\\266\\303\\237e.tsx"``), and no suffix or name test
    matches it."""
    return [record for record in git(repo, *args, stdin=stdin, allowed=allowed).split("\0") if record]


def pipe_limits(path: str, text: str) -> Counter[str]:
    key_fields, limit_field = PIPE_BASELINES[path]
    limits: Counter[str] = Counter()
    for number, raw in enumerate(text.splitlines(), start=1):
        line = raw.split("#", 1)[0].strip()
        if not line:
            continue
        fields = [field.strip() for field in line.split("|")]
        width = key_fields if limit_field is None else limit_field + 1
        if len(fields) < width or (limit_field is not None and not fields[limit_field].isdigit()):
            raise CheckError(f"{path} line {number}: cannot read a key and limit from {raw!r}")
        limits["|".join(fields[:key_fields])] += 1 if limit_field is None else int(fields[limit_field])
    return limits


def biome_limits(text: str) -> Counter[str]:
    """The off-list, and every include, exclusion and per-include maxLines of an override setting the file-length rule
    at error: the allowed shapes (biome_shape_violations) leave no other way to change where the rule applies."""
    limits: Counter[str] = Counter()
    for override in json.loads(text).get("overrides", []):
        setting = rule_setting(override)
        includes = override.get("includes", [])
        if setting == "off":
            limits.update(f"off-list|{include}" for include in includes)
        elif isinstance(setting, dict) and setting.get("level") == "error":
            # A new exclusion is an ADDED widening; a lost include is NARROWED (widenings).
            limits.update(f"error-excludes|{i}" if i.startswith("!") else COVERAGE + i for i in includes)
            if "maxLines" in setting.get("options", {}):
                for include in (i for i in includes if not i.startswith("!")):
                    key = MAX_LINES + include
                    limits[key] = max(limits[key], int(setting["options"]["maxLines"]))
    return limits


def rule_setting(section: dict) -> object:
    """The file-length rule's own setting in a configuration section (a level string or { level, options }), or None."""
    style = section.get("linter", {}).get("rules", {}).get("style")
    return style.get(BIOME_RULE) if isinstance(style, dict) else None


def rule_switches(rules: dict) -> list[str]:
    """Group-wide switches that can turn the file-length rule off without naming it, and the rule itself."""
    style = rules.get("style", {})
    found = [f"sets linter.rules.{key}" for key in ("recommended", "preset") if key in rules]
    if not isinstance(style, dict):
        return [*found, "sets the style group"]
    return found + [f"sets linter.rules.style.{key}" for key in ("recommended", BIOME_RULE) if key in style]


def unexpected(section: object, allowed: tuple[str, ...], prefix: str) -> list[str]:
    return [f"sets {prefix}{key}" for key in (section if isinstance(section, dict) else {}) if key not in allowed]


def production_rule(setting: object) -> bool:
    """The one allowed non-off setting: error level with an integer maxLines (skipBlankLines optional)."""
    if not isinstance(setting, dict) or setting.get("level") != "error" or set(setting) - {"level", "options"}:
        return False
    options = setting.get("options", {})
    return type(options.get("maxLines")) is int and not set(options) - {"maxLines", "skipBlankLines"}


def biome_shape_violations(config: dict) -> list[str]:
    """Every biome.json setting outside the only shapes allowed to affect linting a production source: top-level
    linter enabled with its includes and rules (preset none), top-level includes ``modelled_include`` accepts, files
    and javascript without linting switches, exactly one override setting the file-length rule at error, at most one
    off-list override naming only the rule, and other overrides holding includes, javascript globals and rules that
    switch no group and name no file-length rule."""
    linter = config.get("linter", {})
    rules = {k: v for k, v in linter.get("rules", {}).items() if (k, v) != ("preset", "none")}
    found = ["extends another configuration"] if "extends" in config else []
    found += [] if linter.get("enabled", True) is True else ["linter.enabled is not true"]
    found += unexpected(linter, ("enabled", "includes", "rules"), "linter.")
    found += unexpected(config.get("files"), ("ignoreUnknown", "includes"), "files.")
    found += unexpected(config.get("javascript"), ("formatter", "globals"), "javascript.")
    found += [f"top level: {switch}" for switch in rule_switches(rules)]
    found += [
        f"{section}.includes: {json.dumps(pattern)} is not an include shape the guards model"
        for section in ("files", "linter")
        for pattern in config.get(section, {}).get("includes", [])
        if not modelled_include(pattern)
    ]
    production = off_lists = 0
    for index, override in enumerate(config.get("overrides", [])):
        setting = rule_setting(override)
        if override == {"includes": override.get("includes"), "linter": {"rules": {"style": {BIOME_RULE: "off"}}}}:
            off_lists += 1
            continue
        rules = override.get("linter", {}).get("rules", {})
        style = rules.get("style")
        problems = unexpected(override, ("includes", "linter", "javascript"), "")
        problems += unexpected(override.get("linter"), ("rules",), "linter.")
        problems += unexpected(override.get("javascript"), ("globals",), "javascript.")
        if setting is not None and production_rule(setting):
            production += 1
            style = {k: v for k, v in style.items() if k != BIOME_RULE}
        problems += rule_switches({**rules, "style": style} if style is not None else rules)
        found += [f"overrides[{index}] ({', '.join(override.get('includes', []))}): {p}" for p in problems]
    if production != 1:
        found.append(f"{production} overrides set {BIOME_RULE} at error with maxLines; exactly one may")
    if off_lists > 1:
        found.append(f"{off_lists} off-list overrides; at most one may")
    return found


# A top-level include segment the guards model: literal path characters with `*` inside it, or `**` alone.
INCLUDE_SEGMENT = re.compile(r"\*\*|(?:[A-Za-z0-9_.-]|\*(?!\*))+")


def modelled_include(pattern: object) -> bool:
    """Whether a top-level files/linter include has a shape ``walked`` matches as Biome does: an optional ``!`` or
    ``!!``, then segments of INCLUDE_SEGMENT, never ``.``/``..``, empty or leading ``/``; a trailing ``/`` only negated.
    Braces, ``?``, classes and escapes are left out (Biome rejects the last three)."""
    if not isinstance(pattern, str):
        return False
    body = pattern.removeprefix("!!") if pattern.startswith("!!") else pattern.removeprefix("!")
    if body != pattern:
        body = body.removesuffix("/")
    return all(INCLUDE_SEGMENT.fullmatch(s) and s not in (".", "..") for s in body.split("/"))


def include_regex(body: str) -> re.Pattern[str]:
    """A modelled include as Biome matches it: ``*`` within one segment, ``**`` any number of segments (``a/**``
    never matches ``a``), dot names included."""
    segments = body.split("/")
    parts = [
        (".*" if i == len(segments) - 1 else "(?:[^/]+/)*")
        if segment == "**"
        else "[^/]*".join(map(re.escape, segment.split("*"))) + ("" if i == len(segments) - 1 else "/")
        for i, segment in enumerate(segments)
    ]
    return re.compile("".join(parts), re.DOTALL)  # a name may hold a line break


def walked(path: str, includes: list[str] | None) -> bool:
    """Whether a top-level includes list (absent: every file) lets Biome lint ``path``: a positive include matches it
    and no negated one (``!`` or ``!!``, a trailing ``/`` dropped) matches it or a directory above it, since Biome skips
    an excluded directory's whole subtree. Accepted ceiling: a path re-included after an exclusion counts as excluded,
    stricter than Biome's last-match order."""
    if includes is None:
        return True
    parts = path.split("/")
    walk = ["/".join(parts[:depth]) for depth in range(1, len(parts) + 1)]
    positive = [include_regex(p) for p in includes if not p.startswith("!")]
    negated = [include_regex(p.lstrip("!").removesuffix("/")) for p in includes if p.startswith("!")]
    return any(r.fullmatch(path) for r in positive) and not any(r.fullmatch(d) for r in negated for d in walk)


def linted(config: dict, source: str) -> bool:
    """Whether biome.json's top-level files and linter includes let Biome lint ``source``."""
    files, linter = config.get("files", {}), config.get("linter", {})
    return walked(source, files.get("includes")) and walked(source, linter.get("includes"))


def selected(path: str, includes: list[str] | None) -> bool:
    """Whether an override's includes (absent: every file) select ``path``: a positive pattern matches, none negated.
    Biome matches override includes against the file path alone, never a directory above it."""
    if includes is None:
        return True
    match = PurePosixPath(path).full_match
    return any(match(p) for p in includes if not p.startswith("!")) and not any(
        match(p.lstrip("!")) for p in includes if p.startswith("!")
    )


def biome_findings(repo: Path, merge_base: str, tip: str) -> list[str]:
    """Every TypeScript source at the tip that the merge base's top-level includes select and the tip's do not, and
    every setting of the tip's biome.json outside the allowed shapes."""
    base, current = (
        json.loads(git(repo, "show", f"{commit}:{BIOME_CONFIG}"))
        if paths(repo, "ls-tree", "-z", "--name-only", commit, "--", BIOME_CONFIG)
        else None
        for commit in (merge_base, tip)
    )
    if current is None:
        return []  # a deleted biome.json loses its covers| keys instead
    sizes = {}  # "<mode> <type> <object> <size>\t<path>"
    for record in paths(repo, "ls-tree", "-z", "-r", "-l", tip, "--", REACT_DIR + "src"):
        meta, path = record.split("\t", 1)
        if path.endswith((".ts", ".tsx")):
            sizes[path.removeprefix(REACT_DIR)] = int(meta.split()[3])
    # Biome skips a file above files.maxSize (1 MiB unless set; setting it is REJECTED) with only a warning.
    max_size = current.get("files", {}).get("maxSize", BIOME_DEFAULT_MAX_SIZE)
    covered = [o.get("includes", []) for o in current.get("overrides", []) if production_rule(rule_setting(o))]
    judged = [s for s in sizes if linted(current, s) and any(selected(s, includes) for includes in covered)]
    # "<tip>:<path>\0" per source holding a suppression comment of the rule.
    matches = paths(
        repo, "grep", "-I", "-i", "-E", "-l", "-z", SUPPRESSION, tip, "--", REACT_DIR + "src", allowed=(0, 1)
    )
    suppressed = {m.removeprefix(f"{tip}:").removeprefix(REACT_DIR) for m in matches}
    return (
        [
            f"NARROWED {BIOME_CONFIG}: unenforced|{source}"
            for source in sizes
            if base is not None and linted(base, source) and not linted(current, source)
        ]
        + [f"REJECTED {BIOME_CONFIG}: {violation}" for violation in biome_shape_violations(current)]
        + [
            f"ADDED {REACT_DIR}{source}: {size} bytes, over Biome's files.maxSize {max_size}, so Biome skips it"
            for source, size in sizes.items()
            if size > max_size and source in judged
        ]
        + [
            f"ADDED {REACT_DIR}{source}: a source comment suppresses {BIOME_RULE}"
            for source in judged
            if source in suppressed
        ]
    )


def nested_biome_configs(repo: Path, tip: str) -> list[str]:
    """Every biome.json, biome.jsonc or .ignore (an ignore file Biome reads and git does not) below the React project
    root at the tip except the root configuration."""
    return [
        f"ADDED {path}: {'Biome ignore file' if path.endswith('.ignore') else 'nested Biome configuration'}"
        for path in paths(repo, "ls-tree", "-z", "-r", "--name-only", tip, "--", REACT_DIR)
        if path.rsplit("/", 1)[-1] in ("biome.json", "biome.jsonc", ".ignore") and path != BIOME_CONFIG
    ]


def git_ignored_sources(repo: Path, tip: str) -> list[str]:
    """Every tracked TypeScript source under the React src/ that the tip's .gitignore files ignore: git judges, in a
    scratch repository holding only those files, without the caller's global excludes."""
    tracked = paths(repo, "ls-tree", "-z", "-r", "--name-only", tip)
    sources = [path for path in tracked if path.startswith(REACT_DIR + "src/") and path.endswith((".ts", ".tsx"))]
    with tempfile.TemporaryDirectory() as scratch:
        root = Path(scratch)
        for path in (p for p in tracked if p.rsplit("/", 1)[-1] == ".gitignore"):
            (root / path).parent.mkdir(parents=True, exist_ok=True)
            text = git(repo, "show", f"{tip}:{path}")
            (root / path).write_text(text, encoding="utf-8", errors="surrogateescape")
        git(root, "init", "-q", "--template=")  # no operator template's info/exclude
        ignored = paths(
            root,
            "-c",
            f"core.excludesFile={os.devnull}",
            "check-ignore",
            "--no-index",
            "--stdin",
            "-z",
            stdin="\0".join(sources),
            allowed=(0, 1),
        )
    return [f"ADDED {path}: ignored by git, so Biome skips it" for path in ignored]


def limits_of(path: str, text: str) -> Counter[str]:
    try:
        if path in PIPE_BASELINES:
            return pipe_limits(path, text)
        if path == BIOME_CONFIG:
            return biome_limits(text)
        return Counter(str(entry) for entry in json.loads(text))
    except (ValueError, TypeError, AttributeError) as error:
        raise CheckError(f"{path}: cannot read: {error}") from error


def baselines_at(repo: Path, commit: str) -> tuple[dict[str, Counter[str]], list[str]]:
    """The limits of every known data file present at `commit`, and the Architecture allowlists this script lacks."""
    known = (*PIPE_BASELINES, *JSON_BASELINES, BIOME_CONFIG)
    present = paths(repo, "ls-tree", "-z", "-r", "--name-only", commit, "--", *known, ARCHITECTURE_DIR)
    unknown = [p for p in present if p.endswith("Allowlist.txt") and p not in PIPE_BASELINES]
    baselines = {path: limits_of(path, git(repo, "show", f"{commit}:{path}")) for path in present if path in known}
    # biome.json predates the rule: without it the file holds no baseline yet.
    if not baselines.get(BIOME_CONFIG, True):
        del baselines[BIOME_CONFIG]
    return baselines, unknown


def widenings(base: dict[str, Counter[str]], tip: dict[str, Counter[str]]) -> list[str]:
    findings = []
    for path, limits in sorted(tip.items()):
        if path not in base:
            findings.append(f"NEW {path}: a new baseline of {len(limits)} keys, allowed once")
            continue
        for key, limit in sorted(limits.items()):
            if key.startswith(COVERAGE) or (key.startswith(MAX_LINES) and key not in base[path]):
                continue  # covering more files never widens
            if key not in base[path]:
                findings.append(f"ADDED {path}: {key}")
            elif limit > base[path][key]:
                findings.append(f"RAISED {path}: {key} {base[path][key]} -> {limit}")
    for path, limits in sorted(base.items()):
        lost = [key for key in sorted(limits) if key.startswith(COVERAGE) and key not in tip.get(path, {})]
        findings.extend(f"NARROWED {path}: {key}" for key in lost)
    return findings


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="ratchet-baseline-check", description="Fail when a branch widens a shrink-only allowlist or baseline."
    )
    parser.add_argument("branch")
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--base", default="develop")
    args = parser.parse_args(argv)
    if tuple(sys.version_info[:2]) < MIN_PYTHON:
        found = ".".join(map(str, sys.version_info[:3]))
        print(f"ratchet-baseline-check: ERROR needs Python 3.13 or newer, {sys.executable} is {found}", file=sys.stderr)
        return 2

    try:
        tip = git(args.repo, "rev-parse", "--verify", "--quiet", f"{args.branch}^{{commit}}").strip()
        merge_base = git(args.repo, "merge-base", args.base, tip).strip()
        base, base_unknown = baselines_at(args.repo, merge_base)
        if base_unknown:
            unread = ", ".join(base_unknown)
            raise CheckError(f"unknown allowlist {unread}: name its key and limit fields in PIPE_BASELINES")
        branch, branch_unknown = baselines_at(args.repo, tip)
        # A path's unknown-ness does not depend on the commit, so every one left here is new on the branch.
        findings = (
            widenings(base, branch)
            + biome_findings(args.repo, merge_base, tip)
            + nested_biome_configs(args.repo, tip)
            + git_ignored_sources(args.repo, tip)
            + [f"NEW (unparsed by this checker) {path}: a new baseline, allowed once" for path in branch_unknown]
        )
    except CheckError as error:
        print(f"ratchet-baseline-check: ERROR {error}", file=sys.stderr)
        return 2

    for finding in findings:
        print(finding)
    widened = sum(not finding.startswith("NEW ") for finding in findings)
    print(f"ratchet-baseline-check: {args.branch} @ {tip[:12]} vs merge base {merge_base[:12]}: {widened} widened")
    if not widened:
        return 0
    reason = os.environ.get(OVERRIDE_VARIABLE, "").strip()
    if reason:
        print(f"ratchet-baseline-check: widening accepted by {OVERRIDE_VARIABLE}: {reason}")
        return 0
    return 1


if __name__ == "__main__":
    sys.exit(main())
