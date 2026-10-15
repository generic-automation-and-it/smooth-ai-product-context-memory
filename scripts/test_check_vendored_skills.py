import subprocess
import tempfile
import unittest
from pathlib import Path

from check_vendored_skills import is_owned, is_vendored, violations

INSTALLER = Path(__file__).resolve().parent / "install-skills.sh"
SKILLS = Path(__file__).resolve().parents[1] / ".agents" / "skills"


def installer_owns(name):
    """Run the installer's own `is_owned_skill`, extracted from the script, on `name`."""
    text = INSTALLER.read_text(encoding="utf-8")
    function = text[text.index("is_owned_skill() {"):]
    function = function[:function.index("\n}\n") + 3]
    return subprocess.run(["bash", "-c", f'{function}\nis_owned_skill "$1"', "_", name]).returncode == 0


class VendoredSkillsTests(unittest.TestCase):
    def tree(self, files):
        root = Path(tempfile.mkdtemp())
        self.addCleanup(lambda: subprocess.run(["rm", "-rf", str(root)]))
        for relative, text in files.items():
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8")
        return root

    def test_the_tree_is_self_contained(self):
        self.assertEqual([], violations(SKILLS))

    def test_an_out_of_tree_link_and_a_repo_rule_path_are_reported(self):
        root = self.tree({
            "mimisbrunnr-x/README.md": "See [d](../../docs/h.md) and `.github/instructions/a/b.instructions.md`.\n",
        })
        found = violations(root)
        self.assertEqual(2, len(found), found)
        self.assertTrue(all(f.startswith("mimisbrunnr-x/README.md:1") for f in found))

    def test_in_tree_absolute_anchor_and_table_references_are_allowed(self):
        root = self.tree({
            "mimisbrunnr-x/README.md": (
                "[peer](../ai-understanding/SKILL.md#a) [up](https://example.com/docs) [mail](mailto:a@b)\n"
                "| 2026 | [d](../../docs/h.md) `.github/instructions/a/b.instructions.md` | |\n"),
            "ai-understanding/SKILL.md": "x\n",
        })
        self.assertEqual([], violations(root))

    def test_a_skill_the_installer_does_not_own_is_not_checked(self):
        root = self.tree({"git-commit/SKILL.md": "[d](../../docs/h.md)\n"})
        self.assertEqual([], violations(root))

    def test_ownership_matches_the_installer(self):
        names = [p.name for p in SKILLS.iterdir() if p.is_dir()] + [
            "mimisbrunnr-new", "ai-understanding", "ai-brain-dump", "ai-review", "mimisbrunnr"]
        for name in names:
            with self.subTest(name=name):
                self.assertEqual(installer_owns(name), is_owned(name))

    def test_the_shared_folder_is_checked_though_the_installer_does_not_own_it(self):
        self.assertFalse(installer_owns("_mimisbrunnr-shared"))
        self.assertTrue(is_vendored("_mimisbrunnr-shared"))


if __name__ == "__main__":
    unittest.main()
