#!/usr/bin/env python3
"""Unit tests for scripts/docs-inventory-check.py.

Two runners execute this file and both have to be satisfied, which is why it is `unittest` rather
than bare pytest functions: the `python-quality` job runs it under pytest, while
`scripts/run-release-contract-tests.sh` auto-enrols every `scripts/tests/test_*.py`, runs it as
`python3 <file>`, and rejects the run unless it prints a non-vacuous `Ran N tests` / `OK`. That is
also why the existing files here are `unittest.TestCase`.

The subject's filename is not a valid module name, so it is loaded through importlib the same way
release-envelope.test.py loads its subject. Each check gets a passing case and a one-item-missing
case against a synthetic repository in a temporary directory, plus a hollow-gate case proving an
empty inventory raises rather than passing vacuously. One end-to-end test runs the whole guard
against the real repository root, which must be clean.
"""

from __future__ import annotations

import contextlib
import importlib.util
import io
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from typing import Any
from unittest import mock

REPO_ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = REPO_ROOT / "scripts" / "docs-inventory-check.py"
SPEC = importlib.util.spec_from_file_location("docs_inventory_check", MODULE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"could not load {MODULE_PATH}")
MODULE: Any = importlib.util.module_from_spec(SPEC)
# Register before executing: @dataclass resolves its own module out of sys.modules while the class
# body is being processed, and raises AttributeError if the module is not there yet.
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)

PROGRAM_CS = """
internal static class Startup
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapHub<AlphaHub>(LocalApiRoutes.Widgets.Hub).RequireAuthorization();
        app.MapHub<BetaHub>(LocalApiRoutes.Gadgets.Hub).RequireAuthorization();
    }
}
"""

LOCAL_API_ROUTES_CS = """
public static class LocalApiRoutes
{
    public static class Widgets
    {
        public const string Hub = "widgets/hub";
    }

    public static class Gadgets
    {
        public const string Hub = "gadgets/hub";

        public static class NotARouteFamily
        {
            public const string Nested = "nested";
        }
    }
}
"""

SOLUTION_XML = """<Solution>
    <Folder Name="/Solution Items/">
        <Project Path="Directory.Build.props"/>
    </Folder>
    <Folder Name="/Src/">
        <Project Path="Contoso.One/Contoso.One.csproj"/>
        <Project Path="Contoso.Two\\Contoso.Two.csproj"/>
        <Project Path="Contoso.One/Plugins/Contoso.Nested.csproj"/>
    </Folder>
</Solution>
"""

HOME_MD = """# Home

| 02 | [Project Layout](02-project-layout.md) | projects |
| 09 | [API & Hubs](09-api-and-hubs.md) | hubs |
| 10 | [React Client](10-react-client.md#features) | features |
"""

API_AND_HUBS_MD = """# API & Hubs

| **Widgets** (`widgets/*`) | routes | owner |
| **Gadgets** (`gadgets/*`) | routes | owner |

Hubs: `AlphaHub`, `BetaHub`.
"""

REACT_CLIENT_MD = """# React Client

## Features

Feature areas: `alpha`, `beta`.
"""

PROJECT_LAYOUT_MD = """# Project Layout

- `Contoso.One/` — one
- `Contoso.Two/` — two
- `Contoso.One/Plugins/Contoso.Nested/` — nested project (`Contoso.Nested`)
"""

AGENT_KNOWLEDGE_INDEX_MD = (
    "# Agent knowledge\n\n"
    + "".join(f"## {n}. Section {n}\n\nSee [topic](agent-knowledge/build.md#short-rule).\n\n" for n in range(8))
    + "Pending: [proposed](agent-knowledge/proposed.md).\n"
)

BUILD_TOPIC_MD = """# Build

### Short rule

Do the thing; it prevents the failure. Authority: a test.

## Subsection

### Another rule

Also short.
"""

PROPOSED_TOPIC_MD = """# Proposed

### PROPOSED: a long pending rule

""" + ("narrative " * 200)


def git(root: Path, *args: str) -> None:
    """Run git on the fixture repository, never on an index or repository the caller exported."""
    env = {key: value for key, value in os.environ.items() if key not in MODULE.GIT_REPO_ENV_VARS}
    subprocess.run(["git", *args], cwd=root, env=env, check=True)


class DocsInventoryCheckTests(unittest.TestCase):
    def make_repo(self) -> Path:
        """Build a miniature repository that every check passes on."""
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name) / "repo"

        program = root / "XE-Local-AI-Engine.Client" / "Program.cs"
        program.parent.mkdir(parents=True)
        program.write_text(PROGRAM_CS, encoding="utf-8")

        routes = root / "XE-Local-AI-Engine.Client" / "Endpoints" / "Common" / "LocalApiRoutes.cs"
        routes.parent.mkdir(parents=True)
        routes.write_text(LOCAL_API_ROUTES_CS, encoding="utf-8")

        features = root / "XE-Local-AI-Engine.Client.React" / "src" / "features"
        for name in ("alpha", "beta"):
            (features / name).mkdir(parents=True)

        (root / "XE-Local-AI-Engine.slnx").write_text(SOLUTION_XML, encoding="utf-8")

        wiki = root / "docs" / "wiki"
        wiki.mkdir(parents=True)
        (wiki / "Home.md").write_text(HOME_MD, encoding="utf-8")
        (wiki / "02-project-layout.md").write_text(PROJECT_LAYOUT_MD, encoding="utf-8")
        (wiki / "09-api-and-hubs.md").write_text(API_AND_HUBS_MD, encoding="utf-8")
        (wiki / "10-react-client.md").write_text(REACT_CLIENT_MD, encoding="utf-8")

        (root / "docs" / "agent-knowledge.md").write_text(AGENT_KNOWLEDGE_INDEX_MD, encoding="utf-8")
        topics = root / "docs" / "agent-knowledge"
        topics.mkdir()
        (topics / "build.md").write_text(BUILD_TOPIC_MD, encoding="utf-8")
        (topics / "proposed.md").write_text(PROPOSED_TOPIC_MD, encoding="utf-8")

        # main() runs check_markdown_links over `git ls-files`, so the miniature repository is a git repository.
        if shutil.which("git") is None:
            self.skipTest("git is not on PATH; main() needs it for the markdown-links inventory")
        git(root, "init", "-q")
        git(root, "add", "-A")

        return root

    def make_docs(self, files: dict[str, str]) -> tuple[Path, list[Path]]:
        """Write Markdown files into a plain (non-git) directory and return them as an injected inventory."""
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        paths = []
        for relative, text in files.items():
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8")
            paths.append(path)
        return root, paths

    def link_problems(self, files: dict[str, str]) -> list[str]:
        root, paths = self.make_docs(files)
        return [item.render() for item in MODULE.check_markdown_links(root, paths).missing]

    @staticmethod
    def drop(root: Path, relative: str, needle: str) -> None:
        """Remove one inventory name from a wiki page, leaving the rest intact."""
        page = root / relative
        page.write_text(page.read_text(encoding="utf-8").replace(needle, ""), encoding="utf-8")

    def test_signalr_hubs_pass_when_every_hub_is_named(self) -> None:
        result = MODULE.check_signalr_hubs(self.make_repo())

        self.assertEqual(("AlphaHub", "BetaHub"), result.inventory)
        self.assertEqual((), result.missing)

    def test_signalr_hubs_report_a_hub_missing_from_the_api_page(self) -> None:
        root = self.make_repo()
        self.drop(root, "docs/wiki/09-api-and-hubs.md", "BetaHub")

        result = MODULE.check_signalr_hubs(root)

        self.assertEqual(["BetaHub"], [item.name for item in result.missing])
        self.assertEqual(
            "MISSING signalr-hubs: BetaHub — expected in docs/wiki/09-api-and-hubs.md",
            result.missing[0].render(),
        )

    def test_route_families_take_only_the_classes_nested_directly_in_local_api_routes(self) -> None:
        result = MODULE.check_local_api_route_families(self.make_repo())

        # Neither the outer class nor the doubly nested one is a route family.
        self.assertEqual(("Gadgets", "Widgets"), result.inventory)
        self.assertEqual((), result.missing)

    def test_route_families_report_a_family_missing_from_the_api_page(self) -> None:
        root = self.make_repo()
        self.drop(root, "docs/wiki/09-api-and-hubs.md", "Widgets")

        result = MODULE.check_local_api_route_families(root)

        self.assertEqual(["Widgets"], [item.name for item in result.missing])

    def test_react_features_pass_when_every_directory_is_named(self) -> None:
        result = MODULE.check_react_features(self.make_repo())

        self.assertEqual(("alpha", "beta"), result.inventory)
        self.assertEqual((), result.missing)

    def test_react_features_report_an_undocumented_feature_directory(self) -> None:
        root = self.make_repo()
        (root / "XE-Local-AI-Engine.Client.React" / "src" / "features" / "gamma").mkdir()

        result = MODULE.check_react_features(root)

        self.assertEqual(["gamma"], [item.name for item in result.missing])
        self.assertEqual("docs/wiki/10-react-client.md", result.missing[0].doc.as_posix())

    def test_wiki_pages_pass_when_home_links_every_numbered_page(self) -> None:
        result = MODULE.check_wiki_pages_linked(self.make_repo())

        expected = ("02-project-layout.md", "09-api-and-hubs.md", "10-react-client.md")
        self.assertEqual(expected, result.inventory)
        self.assertEqual((), result.missing)

    def test_wiki_pages_report_a_page_home_only_names_without_linking(self) -> None:
        root = self.make_repo()
        home = root / "docs" / "wiki" / "Home.md"
        # A bare mention is not a link — the check must still flag it.
        unlinked = home.read_text(encoding="utf-8").replace("[API & Hubs](09-api-and-hubs.md)", "09-api-and-hubs.md")
        home.write_text(unlinked, encoding="utf-8")

        result = MODULE.check_wiki_pages_linked(root)

        self.assertEqual(["09-api-and-hubs.md"], [item.name for item in result.missing])

    def test_react_features_skip_hidden_tooling_directories(self) -> None:
        root = self.make_repo()
        (root / "XE-Local-AI-Engine.Client.React" / "src" / "features" / ".cache").mkdir()

        result = MODULE.check_react_features(root)

        self.assertNotIn(".cache", result.inventory)
        self.assertEqual((), result.missing)

    def test_solution_projects_use_the_project_file_stem_as_identity(self) -> None:
        result = MODULE.check_solution_projects(self.make_repo())

        # Both path separators, a project nested beneath an already documented directory (its own name must
        # still be checked), and non-project solution items (skipped).
        self.assertEqual(("Contoso.Nested", "Contoso.One", "Contoso.Two"), result.inventory)
        self.assertEqual((), result.missing)

    def test_solution_projects_report_a_nested_project_missing_from_the_layout_page(self) -> None:
        root = self.make_repo()
        self.drop(root, "docs/wiki/02-project-layout.md", "Contoso.Nested")

        result = MODULE.check_solution_projects(root)

        self.assertEqual(["Contoso.Nested"], [item.name for item in result.missing])

    def test_solution_projects_report_a_project_missing_from_the_layout_page(self) -> None:
        root = self.make_repo()
        self.drop(root, "docs/wiki/02-project-layout.md", "Contoso.Two")

        result = MODULE.check_solution_projects(root)

        self.assertEqual(["Contoso.Two"], [item.name for item in result.missing])

    def test_an_empty_inventory_raises_instead_of_passing_vacuously(self) -> None:
        root = self.make_repo()
        (root / "XE-Local-AI-Engine.Client" / "Program.cs").write_text("// no hubs here\n", encoding="utf-8")

        with self.assertRaises(MODULE.InventoryError):
            MODULE.check_signalr_hubs(root)

    def test_a_missing_source_file_exits_two_rather_than_reporting_a_clean_tree(self) -> None:
        root = self.make_repo()
        (root / "XE-Local-AI-Engine.slnx").unlink()

        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr):
            exit_code = MODULE.main(["--repo-root", str(root)])

        self.assertEqual(2, exit_code)
        self.assertIn("XE-Local-AI-Engine.slnx", stderr.getvalue())

    def test_main_reports_and_fails_on_a_stale_page(self) -> None:
        root = self.make_repo()
        self.drop(root, "docs/wiki/09-api-and-hubs.md", "BetaHub")

        stdout = io.StringIO()
        with contextlib.redirect_stdout(stdout):
            exit_code = MODULE.main(["--repo-root", str(root)])

        out = stdout.getvalue()
        self.assertEqual(1, exit_code)
        self.assertIn("MISSING signalr-hubs: BetaHub — expected in docs/wiki/09-api-and-hubs.md", out)
        self.assertIn("1 missing", out)

    def test_agent_knowledge_index_passes_when_every_topic_is_linked(self) -> None:
        result = MODULE.check_agent_knowledge_index(self.make_repo())

        self.assertEqual(("build.md", "proposed.md"), result.inventory)
        self.assertEqual((), result.missing)

    def test_agent_knowledge_index_reports_an_unlinked_topic_file(self) -> None:
        root = self.make_repo()
        (root / "docs" / "agent-knowledge" / "frontend.md").write_text("# Frontend\n", encoding="utf-8")

        result = MODULE.check_agent_knowledge_index(root)

        self.assertEqual(["frontend.md"], [item.name for item in result.missing])
        self.assertEqual(
            "MISSING agent-knowledge-index: frontend.md — expected in docs/agent-knowledge.md",
            result.missing[0].render(),
        )

    def test_agent_knowledge_index_reports_an_oversize_index(self) -> None:
        root = self.make_repo()
        index = root / "docs" / "agent-knowledge.md"
        index.write_text(AGENT_KNOWLEDGE_INDEX_MD + "x" * MODULE.AGENT_KNOWLEDGE_INDEX_MAX_BYTES, encoding="utf-8")

        result = MODULE.check_agent_knowledge_index(root)

        self.assertEqual(1, len(result.missing))
        rendered = result.missing[0].render()
        self.assertTrue(rendered.startswith("OVER-CAP agent-knowledge-index: docs/agent-knowledge.md: index is "))
        self.assertIn(f"cap {MODULE.AGENT_KNOWLEDGE_INDEX_MAX_BYTES}", rendered)

    def test_agent_knowledge_index_reports_a_missing_section_anchor(self) -> None:
        root = self.make_repo()
        self.drop(root, "docs/agent-knowledge.md", "## 3. Section 3")

        result = MODULE.check_agent_knowledge_index(root)

        self.assertEqual(["## 3."], [item.name for item in result.missing])

    def test_agent_knowledge_index_raises_on_an_empty_topic_directory(self) -> None:
        root = self.make_repo()
        for path in (root / "docs" / "agent-knowledge").iterdir():
            path.unlink()

        with self.assertRaises(MODULE.InventoryError):
            MODULE.check_agent_knowledge_index(root)
        with self.assertRaises(MODULE.InventoryError):
            MODULE.check_agent_knowledge_entries(root)

    def test_agent_knowledge_entries_pass_and_exempt_proposed(self) -> None:
        # proposed.md holds an oversize PROPOSED entry in the fixture; it must not be flagged.
        result = MODULE.check_agent_knowledge_entries(self.make_repo())

        self.assertEqual(("build.md",), result.inventory)
        self.assertEqual((), result.missing)

    def test_agent_knowledge_entries_report_an_oversize_entry(self) -> None:
        root = self.make_repo()
        topic = root / "docs" / "agent-knowledge" / "build.md"
        body = "y" * (MODULE.AGENT_KNOWLEDGE_ENTRY_MAX_CHARS + 1)
        topic.write_text(BUILD_TOPIC_MD.replace("Also short.", body), encoding="utf-8")

        result = MODULE.check_agent_knowledge_entries(root)

        self.assertEqual(["Another rule"], [item.name for item in result.missing])
        self.assertEqual(
            f'OVER-CAP agent-knowledge-entries: docs/agent-knowledge/build.md: entry "### Another rule" is '
            f"{MODULE.AGENT_KNOWLEDGE_ENTRY_MAX_CHARS + 1} chars, cap {MODULE.AGENT_KNOWLEDGE_ENTRY_MAX_CHARS}; "
            "move narrative to docs/agent-knowledge-evidence.md",
            result.missing[0].render(),
        )

    def test_agent_knowledge_entries_report_an_oversize_file(self) -> None:
        root = self.make_repo()
        topic = root / "docs" / "agent-knowledge" / "build.md"
        # Many short entries: every entry is under its cap, only the file total is over.
        entry = "### Rule\n\n" + "z" * 500 + "\n\n"
        count = MODULE.AGENT_KNOWLEDGE_TOPIC_MAX_BYTES // len(entry) + 1
        topic.write_text("# Build\n\n" + entry * count, encoding="utf-8")

        result = MODULE.check_agent_knowledge_entries(root)

        self.assertEqual(["build.md"], [item.name for item in result.missing])
        self.assertIn(f"cap {MODULE.AGENT_KNOWLEDGE_TOPIC_MAX_BYTES}", result.missing[0].render())

    def test_agent_knowledge_entries_report_a_stray_proposed_heading(self) -> None:
        root = self.make_repo()
        topic = root / "docs" / "agent-knowledge" / "build.md"
        topic.write_text(BUILD_TOPIC_MD + "\n### PROPOSED: new rule\n\nShort.\n", encoding="utf-8")

        result = MODULE.check_agent_knowledge_entries(root)

        self.assertEqual(["PROPOSED: new rule"], [item.name for item in result.missing])
        self.assertIn("move it to proposed.md", result.missing[0].render())

    def test_markdown_links_resolve_relative_paths_directories_and_anchors(self) -> None:
        problems = self.link_problems(
            {
                "docs/a.md": (
                    "# A\n\n## Local\n\n[b](b.md#second) [dir](sub/) [self](#local) [img](sub/x.png 'title')\n"
                    '[spaced](my%20page.md) [root](/docs/b.md) [titled](b.md "Title")\n\n[ref]: b.md#explicit\n'
                ),
                "docs/b.md": '# B\n\n## Second\n\n<a id="explicit"></a>\n',
                "docs/my page.md": "# Spaced\n",
                "docs/sub/x.png": "",
            }
        )

        self.assertEqual([], problems)

    def test_markdown_links_report_a_broken_relative_path(self) -> None:
        problems = self.link_problems({"docs/a.md": "See [gone](../wiki/gone.md) and [ref].\n\n[ref]: nope.md\n"})

        self.assertEqual(
            [
                "BROKEN markdown-links: docs/a.md: broken path: ../wiki/gone.md",
                "BROKEN markdown-links: docs/a.md: broken path: nope.md",
            ],
            problems,
        )

    def test_markdown_links_report_a_missing_anchor_in_another_file_and_the_same_file(self) -> None:
        problems = self.link_problems({"a.md": "# A\n\n[x](b.md#nowhere) [y](#also-nowhere)\n", "b.md": "# B\n"})

        self.assertEqual(
            [
                "BROKEN markdown-links: a.md: missing anchor: b.md#nowhere",
                "BROKEN markdown-links: a.md: missing anchor: #also-nowhere",
            ],
            problems,
        )

    def test_github_slugs_for_real_repository_headings(self) -> None:
        cases = {
            # docs/user-guide/docs/first-run.md
            "Step 5 — Get a model that is actually good": "step-5--get-a-model-that-is-actually-good",
            # docs/user-guide/docs/glossary.md
            "Quantization (Q4_K_M, Q5_K_M, Q8_0…)": "quantization-q4_k_m-q5_k_m-q8_0",
            # docs/wiki/12-security-and-privacy.md
            "7.1 The isolated launch mode (`SandboxIsolationMode.Filesystem`) — opt-in, consumed by `run_python`": (
                "71-the-isolated-launch-mode-sandboxisolationmodefilesystem--opt-in-consumed-by-run_python"
            ),
            # docs/wiki/03-local-runtime-and-providers.md
            "`Providers.WhisperCpp` — the supervised speech-to-text runtime": (
                "providerswhispercpp--the-supervised-speech-to-text-runtime"
            ),
            "**Bold** _emphasis_ and gen_aitool [link](x.md) `__init__`": "bold-emphasis-and-gen_aitool-link-__init__",
            "Ümlaut Überschrift": "ümlaut-überschrift",
        }
        for heading, slug in cases.items():
            with self.subTest(heading=heading):
                self.assertEqual(slug, MODULE.github_slug(heading))
                problems = self.link_problems(
                    {
                        "a.md": f"### {heading}\n\n[x](#{slug}) [y](b.md#{slug})\n",
                        "b.md": f"## {heading} ##\n",
                        "x.md": "",
                    }
                )
                self.assertEqual([], problems)

    def test_duplicate_headings_get_numbered_suffixes(self) -> None:
        anchors = MODULE.markdown_anchors("# Setup\n\n## Setup\n\n## Setup\n\n## Setup-1\n")

        self.assertEqual({"setup", "setup-1", "setup-2", "setup-1-1"}, set(anchors))

    def test_headings_and_links_inside_fenced_blocks_are_ignored(self) -> None:
        text = "# Real\n\n```bash\n# not-a-heading\n[x](gone.md)\n````\n~~~\n## also-not\n~~~\n"
        problems = self.link_problems({"a.md": text + "[y](#not-a-heading) [z](#also-not) `[w](gone.md)`\n"})

        self.assertEqual(
            [
                "BROKEN markdown-links: a.md: missing anchor: #not-a-heading",
                "BROKEN markdown-links: a.md: missing anchor: #also-not",
            ],
            problems,
        )

    def test_external_links_are_not_checked(self) -> None:
        problems = self.link_problems(
            {"a.md": "[w](https://example.invalid/x.md#y) [h](http://example.invalid) [m](mailto:a@example.invalid)\n"}
        )

        self.assertEqual([], problems)

    def test_markdown_inventory_skips_vendored_corpus_and_third_party_files(self) -> None:
        root = self.make_repo()
        broken = "[x](does-not-exist.md)\n"
        for relative in (
            "XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/sources/t.md",
            "XE-Local-AI-Engine.Tests/Fixtures/LiveCorpus/c.md",
            "third-party/lib/README.md",
        ):
            path = root / relative
            path.parent.mkdir(parents=True)
            path.write_text(broken, encoding="utf-8")
        (root / "docs" / "kept.md").write_text(broken, encoding="utf-8")
        git(root, "add", "-A")

        result = MODULE.check_markdown_links(root)

        # The fixture's own pages are link-clean, so only the one non-excluded broken file is reported.
        self.assertEqual(["docs/kept.md"], [item.doc.as_posix() for item in result.missing])
        self.assertNotIn("third-party/lib/README.md", result.inventory)

    def test_an_empty_markdown_inventory_raises(self) -> None:
        root, _ = self.make_docs({})

        with self.assertRaises(MODULE.InventoryError):
            MODULE.check_markdown_links(root, [])

    @staticmethod
    def track(root: Path, files: dict[str, str]) -> None:
        """Write files into the miniature repository and stage them, so `git ls-files` lists them."""
        for relative, text in files.items():
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8")
        git(root, "add", "-A")

    def test_hygiene_checks_pass_on_a_clean_repository(self) -> None:
        root = self.make_repo()
        for check in (
            MODULE.check_file_line_citations,
            MODULE.check_tracked_secrets,
            MODULE.check_tool_names,
            MODULE.check_host_phrasing,
        ):
            with self.subTest(check=check.__name__):
                result = check(root)
                self.assertNotEqual((), result.inventory)
                self.assertEqual((), result.missing)

    def test_file_line_citations_report_a_line_citation_outside_fences_in_scoped_docs(self) -> None:
        root = self.make_repo()
        self.track(
            root,
            {
                "docs/wiki/20-new.md": "See `Foo/Bar.cs:120` and scripts/x.sh#:4.\n\n```text\nBaz.ts:9\n```\n",
                "docs/agent-knowledge/build.md": BUILD_TOPIC_MD + "\nCited by `Startup.cs` and `Startup.Map`.\n",
                "docs/other/notes.md": "Unscoped: Foo.cs:1\n",
            },
        )

        result = MODULE.check_file_line_citations(root)

        self.assertEqual(["Foo/Bar.cs:120", "scripts/x.sh#:4"], [item.name for item in result.missing])
        self.assertEqual({"docs/wiki/20-new.md"}, {item.doc.as_posix() for item in result.missing})
        self.assertTrue(result.missing[0].render().startswith("CITATION file-line-citations: docs/wiki/20-new.md: "))

    def test_file_line_citations_cover_script_and_configuration_extensions(self) -> None:
        root = self.make_repo()
        cited = [
            "a.js:1",
            "b.jsx:2",
            "c.cjs:3",
            "package.json:4",
            "ci.yml:5",
            "compose.yaml:6",
            "pyproject.toml:7",
            "App.csproj:8",
            "Directory.Build.props:9",
            "Directory.Build.targets:10",
            "docs/wiki/01-x.md:11",
        ]
        self.track(root, {"docs/wiki/20-new.md": "".join(f"See {name} here.\n" for name in cited)})

        result = MODULE.check_file_line_citations(root)

        self.assertEqual(cited, [item.name for item in result.missing])

    def test_inline_code_with_three_backticks_does_not_open_a_fence(self) -> None:
        root = self.make_repo()
        self.track(root, {"docs/wiki/20-new.md": "```example``` is inline code.\nSee Program.cs:12.\n"})

        result = MODULE.check_file_line_citations(root)

        self.assertEqual(["Program.cs:12"], [item.name for item in result.missing])

    def test_fence_closes_only_on_a_bare_marker_and_tilde_info_may_hold_backticks(self) -> None:
        text = "```bash\n```not-a-close\nA.cs:1\n```\nB.cs:2\n~~~ info `x`\nC.cs:3\n~~~\nD.cs:4\n"

        self.assertEqual(["B.cs:2", "D.cs:4"], list(MODULE.markdown_lines_outside_fences(text)))

    def test_tracked_secrets_report_runtime_state_but_allow_env_templates(self) -> None:
        root = self.make_repo()
        planted = ["node.key", "data/app.sqlite", "data/app.sqlite-wal", ".env", "dp-keys/key.xml", "x/creds.enc"]
        self.track(root, {name: "" for name in [*planted, ".env.template", ".env.tests", "docs/sqlite.md"]})

        result = MODULE.check_tracked_secrets(root)

        self.assertEqual(sorted(planted), sorted(item.name for item in result.missing))

    def test_tool_names_report_tool_config_in_product_code_but_not_in_exempt_installers(self) -> None:
        root = self.make_repo()
        self.track(
            root,
            {
                "src/A.cs": "// settings live in ~/.claude/settings.json\nclass A {}\n",
                "src/B.ts": "// ponytail: shortcut\n",
                "src/C.cs": "// An external MCP client such as Claude Code connects here.\n",
                "install.sh": 'skills="$HOME/.claude/skills"\n',
                "docs/other/runbook.md": "Put it in .claude/settings.json\n",
            },
        )

        result = MODULE.check_tool_names(root)

        self.assertEqual(
            [("src/A.cs", ".claude/"), ("src/B.ts", "ponytail")],
            [(item.doc.as_posix(), item.name) for item in result.missing],
        )
        self.assertTrue(result.missing[0].render().startswith("TOOL-NAME tool-names: src/A.cs: line 1: "))

    def test_host_phrasing_reports_host_details_unless_dated_quoted_or_user_guide(self) -> None:
        root = self.make_repo()
        self.track(
            root,
            {
                "docs/wiki/20-new.md": (
                    "Logs land in /home/alice/logs.\n"
                    "Measured on this box.\n"
                    "The host has 64 GB RAM and a 32-core CPU.\n"
                    "Mapping alice:100000:65536 in /etc/subuid.\n"
                    "A rootless daemon maps the uid into the operator's subuid range.\n"
                    'Rule: no "this box" phrasing.\n'
                    "On 2026-01-02, measured with 64 GB RAM on a 16-core host.\n"
                    "chmod jail/home/tmp to 0700.\n"
                ),
                "docs/user-guide/docs/faq.md": "Windows with 8 GB RAM works.\n",
            },
        )

        result = MODULE.check_host_phrasing(root)

        self.assertEqual(
            ["/home/alice", "this box", "64 GB RAM", "32-core", "alice:100000:65536"],
            [item.name for item in result.missing],
        )
        self.assertEqual({"docs/wiki/20-new.md"}, {item.doc.as_posix() for item in result.missing})

    def test_host_phrasing_date_excuses_only_hardware_inventory(self) -> None:
        root = self.make_repo()
        self.track(
            root,
            {
                "docs/agent-knowledge-evidence.md": (
                    "2026-10-08: logs in /home/alice, uid=1000\n"
                    "2026-10-08: mapped alice:100000:65536 on this box\n"
                    "2026-10-08: measured with 64 GB RAM on a 16-core host\n"
                ),
            },
        )

        result = MODULE.check_host_phrasing(root)

        self.assertEqual(
            ["/home/alice", "uid=", "alice:100000:65536", "this box"],
            [item.name for item in result.missing],
        )

    def test_tool_names_scan_javascript_sources(self) -> None:
        root = self.make_repo()
        planted = {
            "XE-Local-AI-Engine.Client.React/scripts/gate.mjs": "// reads .claude/settings.json\n",
            "src/a.js": "// ponytail: shortcut\n",
            "src/b.cjs": "module.exports = '.codex/';\n",
            "src/C.jsx": "// see CLAUDE.md\n",
        }
        self.track(root, {**planted, "src/clean.mjs": "export const ok = 1;\n"})

        result = MODULE.check_tool_names(root)

        self.assertEqual(sorted(planted), sorted(item.doc.as_posix() for item in result.missing))

    def test_tool_names_match_directory_patterns_with_a_backslash_separator(self) -> None:
        root = self.make_repo()
        planted = {
            "publish/a.ps1": '$cfg = Join-Path $HOME ".codex\\config.toml"\n',
            "publish/b.ps1": '$dir = "$env:USERPROFILE\\.claude\\skills"\n',
        }
        self.track(root, {**planted, "publish/clean.ps1": '$p = "C:\\Program Files\\x"\n'})

        result = MODULE.check_tool_names(root)

        self.assertEqual(sorted(planted), sorted(item.doc.as_posix() for item in result.missing))

    def test_tool_names_match_directory_tokens_ending_at_a_quote_whitespace_or_line_end(self) -> None:
        root = self.make_repo()
        planted = {
            "src/A.cs": 'var cfg = Path.Combine(home, ".codex", "config.toml");\n',
            "src/B.ts": 'const dir = ".claude";\n',
            "src/c.sh": "ls ~/.omc\n",
            "src/d.py": "skip = {'.serena', 'x'}\n",
        }
        clean = {
            "src/E.cs": "// An external MCP client such as Claude Code connects here.\n",
            "src/f.js": 'this.cursor = 0;\nt("navigation.agents");\n',
            "src/G.ts": 'const p = ".vscode/extensions.json";\n',
        }
        self.track(root, {**planted, **clean})

        result = MODULE.check_tool_names(root)

        self.assertEqual(sorted(planted), sorted(item.doc.as_posix() for item in result.missing))

    def test_tool_names_scan_gate_configuration_files(self) -> None:
        root = self.make_repo()
        planted = {
            "package.json": '{"scripts": {"lint": "node .claude/hooks/lint.mjs"}}\n',
            "XE-Local-AI-Engine.Client.React/package.json": '{"scripts": {"x": "repomix ."}}\n',
            ".github/workflows/x.yaml": "steps:\n  - run: cat CLAUDE.md\n",
            "pnpm-workspace.yaml": "packages: ['.codex/*']\n",
            "pyproject.toml": 'exclude = [".omc/"]\n',
            "Directory.Build.props": '<Compile Remove=".cursor/**" />\n',
            "build/x.targets": '<Exec Command="ponytail" />\n',
            "publish/m.psm1": "$d = Join-Path $HOME '.claude'\n",
            "publish/m.psd1": "@{ Path = '.junie/' }\n",
        }
        self.track(root, {**planted, "XE-Local-AI-Engine.Client.React/tsconfig.json": '{"x": ".claude/"}\n'})

        result = MODULE.check_tool_names(root)

        self.assertEqual(sorted(planted), sorted(item.doc.as_posix() for item in result.missing))

    def test_tool_names_scan_project_files(self) -> None:
        root = self.make_repo()
        planted = {"src/App/App.csproj": '<Import Project="../.codex/gate.targets" Condition="Exists(\'x\')" />\n'}
        self.track(root, {**planted, "src/Clean/Clean.csproj": '<Project Sdk="Microsoft.NET.Sdk" />\n'})

        result = MODULE.check_tool_names(root)

        self.assertEqual(sorted(planted), sorted(item.doc.as_posix() for item in result.missing))

    def test_tool_names_decode_a_utf16_file_and_report_an_undecodable_one(self) -> None:
        root = self.make_repo()
        self.track(root, {"publish/clean.ps1": "$p = 1\n"})
        (root / "publish" / "wide.ps1").write_bytes('Join-Path $HOME ".codex"\n'.encode("utf-16"))
        (root / "publish" / "bom.ps1").write_bytes('Join-Path $HOME ".claude"\n'.encode("utf-8-sig"))
        (root / "publish" / "blob.ps1").write_bytes(b"\x80\x81\xfe not text\n")
        git(root, "add", "-A")

        result = MODULE.check_tool_names(root)

        found = {(item.doc.as_posix(), item.problem.split(":")[0]) for item in result.missing}
        self.assertEqual(
            {("publish/wide.ps1", "line 1"), ("publish/bom.ps1", "line 1"), ("publish/blob.ps1", "undecodable")},
            found,
        )

    def test_git_queries_ignore_an_exported_private_index(self) -> None:
        root = self.make_repo()
        stale_index = root.parent / "stale-index"
        shutil.copyfile(root / ".git" / "index", stale_index)
        self.track(root, {"node.key": ""})
        # Without the scrub, `git ls-files` answers from the stale index, which predates node.key.
        stale_env = {"GIT_INDEX_FILE": str(stale_index), "GIT_DIR": str(root / ".git")}
        stale_listing = subprocess.run(
            ["git", "ls-files"], cwd=root, env={**os.environ, **stale_env}, capture_output=True, text=True, check=True
        ).stdout
        self.assertNotIn("node.key", stale_listing.split())

        with mock.patch.dict(os.environ, stale_env):
            result = MODULE.check_tracked_secrets(root)

        self.assertEqual(["node.key"], [item.name for item in result.missing])

    def test_the_script_exits_zero_on_the_real_repository(self) -> None:
        completed = subprocess.run(
            [sys.executable, str(MODULE_PATH)], cwd=REPO_ROOT, capture_output=True, text=True, check=False
        )

        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        self.assertIn("12 checks", completed.stdout)

    def test_main_is_clean_on_the_real_repository(self) -> None:
        stdout = io.StringIO()
        with contextlib.redirect_stdout(stdout):
            exit_code = MODULE.main(["--repo-root", str(REPO_ROOT), "--verbose"])

        out = stdout.getvalue()
        self.assertEqual(0, exit_code, out)
        self.assertIn("0 missing", out)


if __name__ == "__main__":
    unittest.main()
