import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from check_skill_selfcontainment import (
    SHARED_DEPENDENT,
    declaration_problems,
    escapes_for,
    is_owned,
    is_shared_dependent,
    owned_skills,
    rows,
    violations,
)

INSTALLER_SH = Path(__file__).resolve().parent / "install-skills.sh"
INSTALLER_PS1 = Path(__file__).resolve().parent / "install-skills.ps1"
SKILLS = Path(__file__).resolve().parents[1] / ".agents" / "skills"


def installer_owns(name, script=INSTALLER_SH):
    """Run the installer's own ownership test, extracted from the script, on `name`.

    Extracted rather than restated so the gate and the thing it is scoped to cannot drift apart
    unnoticed: this function *is* the installer, read out of the file that ships.
    """
    text = script.read_text(encoding="utf-8")
    if script.suffix == ".ps1":
        function = text[text.index("function Test-OwnedSkill {"):]
        function = function[: function.index("\n}\n") + 3]
        # Two PowerShell facts, both found by running this rather than reasoning about it.
        #
        # `pwsh -Command <script> <arg>` does not populate `$args` — the trailing word is appended to
        # the command text and then fails as an unrecognised cmdlet, so `$args[0]` silently
        # interpolates as `[0]`. And a script whose last statement is `return $false` still exits 0,
        # so the boolean has to become an exit code explicitly. Either one alone leaves the parity
        # case reporting every name as owned, i.e. passing on a gate that checked nothing.
        literal = "'" + name.replace("'", "''") + "'"
        return subprocess.run([
            "pwsh", "-NoProfile", "-Command",
            f"{function}\nif (Test-OwnedSkill -Name {literal}) {{ exit 0 }} else {{ exit 1 }}",
        ]).returncode == 0
    function = text[text.index("is_owned_skill() {"):]
    function = function[: function.index("\n}\n") + 3]
    return subprocess.run(["bash", "-c", f'{function}\nis_owned_skill "$1"', "_", name]).returncode == 0


class SelfContainmentTests(unittest.TestCase):
    def tree(self, files):
        root = Path(tempfile.mkdtemp())
        self.addCleanup(lambda: subprocess.run(["rm", "-rf", str(root)]))
        for relative, text in files.items():
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8")
        return root

    # --- the tree as it stands -------------------------------------------------------------------

    def test_the_declared_tree_passes(self):
        self.assertEqual([], violations(SKILLS))
        self.assertEqual([], declaration_problems(SKILLS))

    def test_the_report_classifies_every_owned_skill_exactly_once(self):
        names = [name for name, _, _ in rows(SKILLS)]
        self.assertEqual(sorted(p.name for p in owned_skills(SKILLS)), sorted(names))
        self.assertEqual(len(names), len(set(names)))

    # --- mechanism 1: runtime resolution ----------------------------------------------------------

    def test_a_python_resolution_out_of_the_skill_is_reported(self):
        root = self.tree({
            "ai-understanding/scripts/a.py": (
                "from pathlib import Path\n"
                'S = Path(__file__).resolve().parents[2] / "_mimisbrunnr-shared" / "scripts"\n'),
        })
        found = violations(root)
        self.assertEqual(1, len(found), found)
        self.assertIn("ai-understanding/scripts/a.py:2", found[0])

    def test_a_resolution_wrapped_across_lines_is_still_reported(self):
        """The regression that shaped this gate.

        `mimisbrunnr-saga-dossier/scripts/dossier_composer.py` wraps its resolution over two lines,
        so the subscript and the folder it reaches land on different ones. A line-oriented scan reads
        that file as self-contained, which is exactly the verdict a zip needs and must not get.
        """
        root = self.tree({
            "ai-understanding/scripts/a.py": (
                "from pathlib import Path\n"
                'S = (Path(__file__).resolve().parents[2]\n'
                '     / "_mimisbrunnr-shared" / "scripts")\n'),
        })
        found = violations(root)
        self.assertEqual(1, len(found), found)
        self.assertIn("ai-understanding/scripts/a.py:2", found[0])

    def test_a_test_harness_may_reach_the_original_without_the_skill_depending_on_it(self):
        root = self.tree({
            "ai-understanding/scripts/a.py": "x = 1\n",
            "ai-understanding/tests/parity.py": (
                "from pathlib import Path\n"
                'ORIG = Path(__file__).resolve().parents[2] / "_mimisbrunnr-shared" / "scripts"\n'),
        })
        self.assertEqual([], violations(root))

    def test_a_resolution_into_the_skills_own_scripts_is_not_an_escape(self):
        root = self.tree({
            "ai-understanding/scripts/a.py": (
                "from pathlib import Path\n"
                'S = Path(__file__).resolve().parents[1] / "scripts"\n'),
        })
        self.assertEqual([], violations(root))

    # --- mechanism 2: documented invocation -------------------------------------------------------

    def test_a_documented_command_out_of_the_skill_is_reported_including_table_rows(self):
        """Table rows are where odin's seven shared-script calls live, and the sibling gate skips
        them by name — so this is the mechanism that actually decides the odin verdict."""
        root = self.tree({
            "ai-understanding/SKILL.md": (
                "| `redact.py` | `python3 .../_mimisbrunnr-shared/scripts/redact.py` | stage 2 |\n"),
        })
        found = violations(root)
        self.assertEqual(1, len(found), found)
        self.assertIn("ai-understanding/SKILL.md:1", found[0])

    def test_prose_naming_the_shared_folder_as_provenance_is_not_a_dependency(self):
        """`ai-understanding/AGENTS.md` explains that its scripts are byte-identical copies of the
        shared originals. Naming them is documentation; invoking them would be a dependency."""
        root = self.tree({
            "ai-understanding/AGENTS.md": (
                "byte-identical copies of `_mimisbrunnr-shared/scripts/`** — never imported\n"),
        })
        self.assertEqual([], violations(root))

    def test_an_elision_above_the_skill_is_not_an_escape(self):
        root = self.tree({
            "ai-understanding/SKILL.md": "python3 .../understanding_client.py load <input>\n",
        })
        self.assertEqual([], violations(root))

    # --- the declaration itself -------------------------------------------------------------------

    def test_a_declared_shared_consumer_passes_and_keeps_its_escape(self):
        folder = SKILLS / "mimisbrunnr-odin-context-memory"
        self.assertTrue(is_shared_dependent(folder.name))
        self.assertTrue(escapes_for(folder), "odin should still have documented shared-script calls")

    def test_declaring_a_skill_that_no_longer_escapes_is_reported(self):
        """The declaration must describe the tree, not the other way round. A stale entry keeps the
        next reviewer reading a false row out of the table.

        The fixture has to use a name the declaration actually names — `declaration_problems` walks
        `SHARED_DEPENDENT`, so a clean `ai-understanding` folder would prove nothing.
        """
        name = SHARED_DEPENDENT[0]
        root = self.tree({f"{name}/SKILL.md": "nothing here\n"})
        found = declaration_problems(root)
        stale = [f for f in found if "no longer escapes" in f]
        self.assertEqual(1, len(stale), found)
        self.assertIn(name, stale[0])

    def test_declaring_a_skill_that_does_not_exist_is_reported(self):
        root = self.tree({"ai-understanding/SKILL.md": "nothing here\n"})
        found = declaration_problems(root)
        self.assertEqual(len(SHARED_DEPENDENT), len(found), found)
        self.assertTrue(all("not a skill folder" in f for f in found), found)

    def test_a_skill_the_installer_does_not_own_is_not_checked(self):
        root = self.tree({"git-commit/SKILL.md": "python3 ../_mimisbrunnr-shared/scripts/x.py\n"})
        self.assertEqual([], violations(root))

    # --- the gate stays scoped to the installer ---------------------------------------------------

    def test_ownership_matches_the_bash_installer(self):
        names = [p.name for p in SKILLS.iterdir() if p.is_dir()] + [
            "mimisbrunnr-new", "ai-understanding", "ai-brain-dump", "ai-memory-context",
            "ai-review", "mimisbrunnr", "_mimisbrunnr-shared",
        ]
        for name in names:
            with self.subTest(name=name):
                self.assertEqual(installer_owns(name), is_owned(name))

    def test_ownership_matches_the_powershell_installer(self):
        import shutil

        if not shutil.which("pwsh"):
            self.skipTest("pwsh absent — the PowerShell port is covered by the installer harness")
        names = [p.name for p in SKILLS.iterdir() if p.is_dir()] + ["mimisbrunnr-new", "ai-review"]
        for name in names:
            with self.subTest(name=name):
                self.assertEqual(installer_owns(name, INSTALLER_PS1), is_owned(name))


class GateEntryPointTests(unittest.TestCase):
    """The checks above call the functions; these run the gate the way CI does.

    Without this group the wiring is untested, and the harness proved it: deleting the
    `declaration_problems()` call from `main()` left every unit test green while the gate stopped
    noticing a stale declaration. A gate whose tests never invoke it can drift from it silently.
    """

    GATE = Path(__file__).resolve().parent / "check_skill_selfcontainment.py"

    def tree(self, files):
        root = Path(tempfile.mkdtemp())
        self.addCleanup(lambda: subprocess.run(["rm", "-rf", str(root)]))
        for relative, text in files.items():
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8")
        return root

    def gate(self, root, *args):
        return subprocess.run(
            [sys.executable, "-B", str(self.GATE), *args],
            cwd=root, capture_output=True, text=True,
        )

    def test_a_clean_tree_exits_zero(self):
        run = self.gate(SKILLS.parents[1])
        self.assertEqual(0, run.returncode, run.stdout + run.stderr)

    def test_the_real_tree_exits_zero(self):
        run = self.gate(SKILLS.parents[1])
        self.assertEqual(0, run.returncode, run.stdout + run.stderr)

    def test_an_undeclared_escape_fails_the_gate(self):
        root = self.tree({
            ".agents/skills/ai-understanding/SKILL.md":
                "| `x.py` | `python3 .../_mimisbrunnr-shared/scripts/x.py` | |\n",
        })
        run = self.gate(root)
        self.assertEqual(1, run.returncode, run.stdout)
        self.assertIn("undeclared dependency", run.stdout)

    def test_a_stale_declaration_fails_the_gate(self):
        """The wiring the function-level tests could not see — see this class's docstring."""
        name = SHARED_DEPENDENT[0]
        root = self.tree({f".agents/skills/{name}/SKILL.md": "nothing here\n"})
        run = self.gate(root)
        self.assertEqual(1, run.returncode, run.stdout)
        self.assertIn("no longer escapes", run.stdout)

    def test_report_exits_zero_even_on_a_violation(self):
        root = self.tree({
            ".agents/skills/ai-understanding/SKILL.md":
                "| `x.py` | `python3 .../_mimisbrunnr-shared/scripts/x.py` | |\n",
        })
        run = self.gate(root, "--report")
        self.assertEqual(0, run.returncode, run.stdout)
        self.assertIn("self-contained", run.stdout)

    def test_zip_verdicts_differ_by_self_containment(self):
        root = SKILLS.parents[1]
        zippable = self.gate(root, "--zip", "ai-understanding")
        self.assertEqual(0, zippable.returncode, zippable.stdout)
        self.assertIn("zippable alone", zippable.stdout)

        dependent = self.gate(root, "--zip", "mimisbrunnr-odin-context-memory")
        self.assertEqual(1, dependent.returncode, dependent.stdout)
        self.assertIn("NOT zippable alone", dependent.stdout)

    def test_zip_naming_an_unknown_skill_fails(self):
        run = self.gate(SKILLS.parents[1], "--zip", "no-such-skill")
        self.assertEqual(1, run.returncode, run.stdout)


if __name__ == "__main__":
    unittest.main()