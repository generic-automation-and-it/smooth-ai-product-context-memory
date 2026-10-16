"""Harness for `scripts/pack-skill-zip.py`.

This script produces the artifact a desktop runtime actually loads, and it had no harness at all —
which is how a zip shipped a bogus empty directory entry while reporting itself "fit to upload". The
cases below are the ones that found it.

Runs offline and needs no archive fixture: every case builds a throwaway skills root in a temp
directory and reads the module directly.
"""

from __future__ import annotations

import importlib.util
import os
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
SCRIPT = REPO / "scripts" / "pack-skill-zip.py"

_spec = importlib.util.spec_from_file_location("pack_skill_zip", SCRIPT)
pack = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(pack)

UNIT = "---\nname: {name}\ndescription: d\n---\nbody\n"


class PackerTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(lambda: subprocess.run(["rm", "-rf", str(self.tmp)]))
        self.root = self.tmp / "clone"
        self.out = self.tmp / "out"
        self.skill = self.root / ".agents" / "skills" / "ai-understanding"
        (self.skill / "scripts").mkdir(parents=True)
        (self.skill / "tests").mkdir()
        (self.skill / "references").mkdir()
        (self.skill / "SKILL.md").write_text(UNIT.format(name="ai-understanding"), encoding="utf-8")
        (self.skill / "scripts" / "run.py").write_text("x = 1\n", encoding="utf-8")
        (self.skill / "tests" / "run_tests.py").write_text("# harness\n", encoding="utf-8")
        (self.skill / "references" / "guide.md").write_text("guide\n", encoding="utf-8")
        # The module resolves "inside the repository" against its own REPO constant.
        self._real_repo = pack.REPO
        pack.REPO = self.root.resolve()

    def tearDown(self):
        pack.REPO = self._real_repo

    def build(self):
        archive, skipped = pack.build(self.skill, self.out)
        return archive, skipped

    # --- shape ---------------------------------------------------------------------------------

    def test_the_zip_root_is_the_skill_folder(self):
        """The packaging guidance is explicit that the archive holds the skill directory *as its
        root*; `zip -r out.zip .agents/skills/<name>` produces exactly the structure it calls
        incorrect."""
        archive, _ = self.build()
        with zipfile.ZipFile(archive) as zf:
            roots = {Path(n).parts[0] for n in zf.namelist()}
        self.assertEqual({"ai-understanding"}, roots)

    def test_runtime_files_ship_and_tests_do_not(self):
        archive, _ = self.build()
        with zipfile.ZipFile(archive) as zf:
            names = set(zf.namelist())
        self.assertIn("ai-understanding/SKILL.md", names)
        self.assertIn("ai-understanding/scripts/run.py", names)
        self.assertIn("ai-understanding/references/guide.md", names)
        self.assertNotIn("ai-understanding/tests/run_tests.py", names)

    def test_the_exclusion_list_comes_from_the_installer(self):
        """Read out of `install-skills.sh` rather than kept as a second copy, so the packer and the
        installer cannot drift on what an installed copy omits."""
        self.assertIn("tests", pack.installer_excludes())
        self.assertIn("__pycache__", pack.installer_excludes())

    # --- symlinks ------------------------------------------------------------------------------

    def test_an_in_repo_link_is_materialised(self):
        """The reason the mechanism exists: an imported archive has no repository to resolve a link
        against, so a copied link would be a pointer to nothing."""
        os.symlink("scripts/run.py", self.skill / "ALIAS.py")
        archive, skipped = self.build()
        with zipfile.ZipFile(archive) as zf:
            names = set(zf.namelist())
        self.assertIn("ai-understanding/ALIAS.py", names)
        self.assertEqual([], skipped)
        with zipfile.ZipFile(archive) as zf:
            self.assertEqual("x = 1\n", zf.read("ai-understanding/ALIAS.py").decode())

    def test_a_link_out_of_the_repo_is_skipped_and_named(self):
        """Following `/etc/hosts` would pack a system file, so it is left out — and reported, rather
        than dropped silently."""
        os.symlink("/etc/hosts", self.skill / "outside.md")
        archive, skipped = self.build()
        with zipfile.ZipFile(archive) as zf:
            self.assertNotIn("ai-understanding/outside.md", zf.namelist())
        self.assertEqual(1, len(skipped))
        self.assertIn("outside.md", skipped[0])

    def test_a_dangling_link_is_skipped_and_named(self):
        os.symlink("/nowhere-at-all", self.skill / "dead.md")
        archive, skipped = self.build()
        with zipfile.ZipFile(archive) as zf:
            self.assertNotIn("ai-understanding/dead.md", zf.namelist())
        self.assertEqual(1, len(skipped))

    def test_a_link_resolving_to_a_directory_is_skipped_and_named(self):
        """A `self -> .` cycle. `zipfile.write` on a directory writes an empty entry rather than
        raising, so the first version of this shipped a hole in the skill and `check()` passed it."""
        os.symlink(".", self.skill / "self")
        archive, skipped = self.build()
        with zipfile.ZipFile(archive) as zf:
            self.assertEqual(["ai-understanding/SKILL.md", "ai-understanding/references/guide.md",
                              "ai-understanding/scripts/run.py"], sorted(zf.namelist()))
        self.assertEqual(1, len(skipped))
        self.assertIn("directory", skipped[0])

    def test_no_member_is_a_directory_entry(self):
        """The check that would have caught the hole: an archive member must be a file."""
        os.symlink(".", self.skill / "self")
        archive, _ = self.build()
        self.assertEqual([], pack.check(self.skill, archive))
        with zipfile.ZipFile(archive) as zf:
            self.assertFalse([i.filename for i in zf.infolist() if i.is_dir()])

    # --- resolve() contract --------------------------------------------------------------------

    def test_resolve_returns_none_for_a_link_with_no_target(self):
        """`os.path.realpath` is non-strict and hands back the path a dangling link wished for, so
        existence has to be checked here — the contract always claimed this."""
        os.symlink("/nowhere-at-all", self.tmp / "dead")
        self.assertIsNone(pack.resolve(self.tmp / "dead"))

    def test_resolve_follows_a_real_link(self):
        # The link lives in `self.tmp`, so a relative target resolves there, not in the skill.
        (self.tmp / "real.md").write_text("x\n", encoding="utf-8")
        os.symlink("real.md", self.tmp / "live")
        self.assertEqual((self.tmp / "real.md").resolve(), pack.resolve(self.tmp / "live"))

    def test_is_inside_is_component_wise_not_a_text_prefix(self):
        """`/repo-other` starts with `/repo` as text and is a different place."""
        root = Path("/repo")
        self.assertTrue(pack.is_inside(Path("/repo/skills/x"), root))
        self.assertFalse(pack.is_inside(Path("/repo-other/skills/x"), root))

    # --- skipped links do not accumulate --------------------------------------------------------

    def test_skipped_links_are_per_call_not_accumulated(self):
        """Module-level state would make a second pack in the same process report the first skill's
        skips as its own."""
        os.symlink("/etc/hosts", self.skill / "outside.md")
        _first, first = self.build()
        self.assertEqual(1, len(first))
        _second, second = self.build()
        self.assertEqual(first, second)

    # --- refusal --------------------------------------------------------------------------------

    def test_a_shared_dependent_skill_is_refused_before_anything_is_written(self):
        """A refusal that leaves a broken archive on disk is a trap: the next person uploads it."""
        self.assertFalse(hasattr(pack, "MEMBERS_SKIPPED"),
                         "module-level skip state is gone; the API is build() -> (archive, skipped)")
        skill = self.root / ".agents" / "skills" / "mimisbrunnr-odin-context-memory"
        skill.mkdir(parents=True)
        (skill / "SKILL.md").write_text(
            UNIT.format(name="mimisbrunnr-odin-context-memory")
            + "\nRun `python3 .../_mimisbrunnr-shared/scripts/redact.py`.\n", encoding="utf-8")
        result = subprocess.run(
            [sys.executable, "-B", str(SCRIPT), "mimisbrunnr-odin-context-memory",
             "--out", str(self.out)],
            cwd=REPO, capture_output=True, text=True)
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertIn("not zippable alone", result.stderr)
        self.assertFalse((self.out / "mimisbrunnr-odin-context-memory.zip").exists())

    def test_a_missing_skill_exits_two(self):
        result = subprocess.run([sys.executable, "-B", str(SCRIPT), "no-such-skill"],
                                cwd=REPO, capture_output=True, text=True)
        self.assertEqual(2, result.returncode)

    def test_check_on_an_absent_archive_exits_two(self):
        result = subprocess.run([sys.executable, "-B", str(SCRIPT), "ai-understanding",
                                 "--check", "--out", str(self.out)],
                                cwd=REPO, capture_output=True, text=True)
        self.assertEqual(2, result.returncode)

    # --- the real skills still pack -------------------------------------------------------------

    def test_every_self_contained_skill_packs_and_passes_its_own_checks(self):
        """The gate the desktop route actually depends on, run against the real tree."""
        for name in ("ai-understanding", "ai-brain-dump", "ai-memory-context"):
            with self.subTest(skill=name):
                skill = REPO / ".agents" / "skills" / name
                archive, skipped = pack.build(skill, self.out)
                self.assertEqual([], pack.check(skill, archive), f"{name}: {skipped}")


if __name__ == "__main__":
    unittest.main()