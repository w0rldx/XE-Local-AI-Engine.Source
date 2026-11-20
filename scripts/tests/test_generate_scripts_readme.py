#!/usr/bin/env python3
"""Unit tests for scripts/generate-scripts-readme.py.

`unittest` rather than bare pytest functions for the same reason as test_docs_inventory_check.py: the release
contract runner executes every `scripts/tests/test_*.py` as `python3 <file>` and needs a non-vacuous `Ran N tests`.
The subject's filename is not a valid module name, so it is loaded through importlib.
"""

from __future__ import annotations

import contextlib
import importlib.util
import io
import tempfile
import unittest
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = REPO_ROOT / "scripts" / "generate-scripts-readme.py"
SPEC = importlib.util.spec_from_file_location("generate_scripts_readme", MODULE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"could not load {MODULE_PATH}")
MODULE: Any = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

FILES = {
    # shellcheck directive skipped, "<name> — " dropped, a sentence spanning two lines joined and cut at its end.
    "alpha.sh": "#!/usr/bin/env bash\n# shellcheck shell=bash\n# alpha.sh — does the first\n# thing. More detail.\n",
    # A blank comment line before the header is skipped.
    "beta.sh": "#!/usr/bin/env bash\n#\n# Beta header.\n\nset -e\n",
    "gamma.py": '#!/usr/bin/env python3\n"""Gamma docstring line.\n\nMore."""\n',
    "delta.py": "#!/usr/bin/env python3\n# Delta comment.\nimport sys\n",
    "notes.txt": "not a script\n",
}


class GenerateScriptsReadmeTests(unittest.TestCase):
    def make_scripts(self, files: dict[str, str]) -> Path:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        for name, text in files.items():
            (root / name).write_text(text, encoding="utf-8")
        (root / "lib").mkdir()
        (root / "lib" / "helper.sh").write_text("# helper\n", encoding="utf-8")
        (root / "docs").mkdir()
        (root / "docs" / "README.md").write_text("# Docs\n\nWhat docs holds.\n", encoding="utf-8")
        return root

    def test_rows_take_the_first_header_sentence_of_each_script(self) -> None:
        text, headerless = MODULE.render(self.make_scripts(FILES))

        self.assertEqual([], headerless)
        self.assertIn("- [`alpha.sh`](alpha.sh) — does the first thing.\n", text)
        self.assertIn("- [`beta.sh`](beta.sh) — Beta header.\n", text)
        self.assertIn("- [`delta.py`](delta.py) — Delta comment.\n", text)
        self.assertIn("- [`gamma.py`](gamma.py) — Gamma docstring line.\n", text)
        self.assertNotIn("notes.txt", text)

    def test_folders_use_their_readme_else_list_their_files(self) -> None:
        text, _ = MODULE.render(self.make_scripts(FILES))

        self.assertIn("- [`docs/`](docs/) — What docs holds.\n", text)
        self.assertIn("- [`lib/`](lib/) — Holds `helper.sh`.\n", text)

    def test_a_script_without_a_header_is_reported(self) -> None:
        _, headerless = MODULE.render(self.make_scripts({"bare.sh": "#!/usr/bin/env bash\nset -e\n"}))

        self.assertEqual(["bare.sh"], headerless)

    def run_main(self, *argv: str, scripts_dir: Path) -> int:
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            return MODULE.main(list(argv), scripts_dir=scripts_dir)

    def test_check_passes_on_a_current_readme_and_leaves_its_bytes(self) -> None:
        root = self.make_scripts(FILES)
        self.assertEqual(0, self.run_main(scripts_dir=root))
        before = (root / "README.md").read_bytes()

        self.assertEqual(0, self.run_main("--check", scripts_dir=root))
        self.assertEqual(before, (root / "README.md").read_bytes())

    def test_check_fails_on_a_stale_readme_and_never_writes(self) -> None:
        root = self.make_scripts(FILES)
        stale = b"# scripts/\n\n- [`gone.sh`](gone.sh) - Removed.\n"
        (root / "README.md").write_bytes(stale)

        self.assertEqual(1, self.run_main("--check", scripts_dir=root))
        self.assertEqual(stale, (root / "README.md").read_bytes())

    def test_check_fails_when_the_readme_is_missing_and_creates_none(self) -> None:
        root = self.make_scripts(FILES)

        self.assertEqual(1, self.run_main("--check", scripts_dir=root))
        self.assertFalse((root / "README.md").exists())

    def test_without_check_the_readme_is_written(self) -> None:
        root = self.make_scripts(FILES)
        (root / "README.md").write_bytes(b"stale\n")

        self.assertEqual(0, self.run_main(scripts_dir=root))
        self.assertEqual(MODULE.render(root)[0].encode("utf-8"), (root / "README.md").read_bytes())

    def test_an_unknown_argument_is_a_usage_error_that_writes_nothing(self) -> None:
        root = self.make_scripts(FILES)

        with self.assertRaises(SystemExit) as raised:
            self.run_main("--chek", scripts_dir=root)
        self.assertEqual(2, raised.exception.code)
        self.assertFalse((root / "README.md").exists())


if __name__ == "__main__":
    unittest.main()
