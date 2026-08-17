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
import sys
import tempfile
import unittest
from pathlib import Path
from typing import Any

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

Feature areas: `alpha`, `beta`.
"""

PROJECT_LAYOUT_MD = """# Project Layout

- `Contoso.One/` — one
- `Contoso.Two/` — two
- `Contoso.One/Plugins/Contoso.Nested/` — nested project (`Contoso.Nested`)
"""


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

        return root

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

    def test_main_is_clean_on_the_real_repository(self) -> None:
        stdout = io.StringIO()
        with contextlib.redirect_stdout(stdout):
            exit_code = MODULE.main(["--repo-root", str(REPO_ROOT), "--verbose"])

        out = stdout.getvalue()
        self.assertEqual(0, exit_code, out)
        self.assertIn("0 missing", out)


if __name__ == "__main__":
    unittest.main()
