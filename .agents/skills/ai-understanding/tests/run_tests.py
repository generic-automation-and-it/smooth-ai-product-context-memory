#!/usr/bin/env python3
"""Committed L0 harness for the ai-understanding index generator. stdlib unittest; no external runner.

Run: python3 -B .agents/skills/ai-understanding/tests/run_tests.py

Covers the durability guard: a gitignored store that holds units newer than the newest published
archive is warned about, and the store's gitignore status — not a text search — decides whether any
warning is owed. The guard is advisory: no reported unit changes the exit code.
"""

from __future__ import annotations

import io
import os
import subprocess
import sys
import tempfile
import unittest
import zipfile
from contextlib import redirect_stdout, redirect_stderr
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))

import understanding_index as ui  # noqa: E402

UNIT_FRONTMATTER = """---
slug: {slug}
description: test unit
scope: repo-specific
confidence: verified
updated: {updated}
provenance:
  learned: {updated}
  session: test-session
  source: test harness
---
# Answer
x
"""


def case_sensitive_filesystem(directory: Path) -> bool:
    """Whether two names differing only by case can coexist under `directory`.

    Asked rather than assumed, because the answer decides which collision fixtures are runnable: on a
    case-insensitive filesystem the colliding files cannot be created, so the on-disk wiring test skips
    and only the pure-function coverage applies. Guessing wrong would either silently skip everywhere
    or try to create a state the filesystem refuses.
    """
    probe = directory / ".case-probe"
    try:
        (probe / "a").mkdir(parents=True, exist_ok=True)
        (probe / "A").mkdir()
    except OSError:
        return False
    finally:
        for child in ("a", "A"):
            try:
                (probe / child).rmdir()
            except OSError:
                pass
        try:
            probe.rmdir()
        except OSError:
            pass
    return True


def make_repo(tmp: str, *, ignore_store: bool) -> Path:
    """A throwaway git repo with `.context/understandings` ignored or tracked."""
    subprocess.run(["git", "init", "-q", tmp], check=True, capture_output=True)
    # A developer's or runner's global excludes could ignore `.context/` and flip the tracked case.
    subprocess.run(["git", "-C", tmp, "config", "core.excludesFile", os.devnull], check=True)
    ignore = ".context/\n" if ignore_store else ""
    (Path(tmp) / ".gitignore").write_text(ignore, encoding="utf-8")
    return Path(tmp)


def write_unit(store: Path, subject: str, slug: str, updated: str = "2026-09-30") -> None:
    folder = store / subject
    folder.mkdir(parents=True, exist_ok=True)
    (folder / f"{slug}{ui.UNIT_SUFFIX}").write_text(
        UNIT_FRONTMATTER.format(slug=slug, updated=updated), encoding="utf-8"
    )


def publish(store: Path, name: str, *unit_paths: str) -> None:
    """Write a real archive holding the given `<subject>/<slug>.understanding.md` store paths."""
    pub = store.parent / ui.PUBLISH_DIR_NAME
    pub.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(pub / name, "w") as zf:
        for rel in unit_paths:
            zf.write(store / rel, rel)
        zf.writestr("INDEX.md", "# index\n")


def flagged(store: Path) -> set[str]:
    return {u["folder"] for u in ui.unpublished_units(ui.load_units(store)[0], store)}


def run(argv) -> tuple[int, str, str]:
    out, err = io.StringIO(), io.StringIO()
    with redirect_stdout(out), redirect_stderr(err):
        rc = ui.main(argv)
    return rc, out.getvalue(), err.getvalue()


def argv(store: Path, review: bool = False) -> list[str]:
    args = ["understanding_index.py", str(store)]
    if review:
        args.append("--review")
    return args


class CaseCollisionTests(unittest.TestCase):
    """Two names that differ only by case are one file on a case-insensitive filesystem.

    The check is a pure function over records, which is the only way to test it honestly: on the very
    filesystems where the overwrite happens, the two colliding files cannot coexist on disk, so no
    fixture can create the state. Building the records directly is not a shortcut around the test — it
    is the only shape the test can have.
    """

    @staticmethod
    def records(*pairs: tuple[str, str]) -> list[dict]:
        return [{"slug": slug, "subject": subject, "path": f"{subject}/{slug}{ui.UNIT_SUFFIX}"}
                for slug, subject in pairs]

    def test_slugs_differing_only_by_case_are_reported(self):
        found = ui.case_collisions(self.records(("Foo", "s-20260101-0000"), ("foo", "s-20260101-0000")))
        self.assertEqual(len(found), 1, found)
        self.assertIn("'Foo'", found[0])
        self.assertIn("'foo'", found[0])
        self.assertIn("case-insensitive", found[0])

    def test_folders_differing_only_by_case_are_reported(self):
        found = ui.case_collisions(self.records(("one", "S-20260101-0000"), ("two", "s-20260101-0000")))
        self.assertEqual(len(found), 1, found)
        self.assertIn("subject folder", found[0])

    def test_a_repeated_name_is_a_version_chain_not_a_collision(self):
        """The same slug twice is the versioning mechanism, and it must stay silent.

        This is the negative control that matters: `case_collisions` runs over *every* record, so a
        check that fired on any repeated name would flag every improved unit in the store — and
        `group_versions` treats a repeated slug as legitimate history.
        """
        self.assertEqual(ui.case_collisions(self.records(("x", "a-20260101-0000"),
                                                        ("x", "b-20260101-0200"))), [])

    def test_distinct_names_are_not_reported(self):
        self.assertEqual(ui.case_collisions(self.records(("alpha", "a-20260101-0000"),
                                                        ("beta", "a-20260101-0000"))), [])

    def test_casefold_catches_what_lower_does_not(self):
        """`lower` keeps these apart; a filesystem does not, so the check must use `casefold`."""
        self.assertNotEqual("straße".lower(), "strasse".lower())
        self.assertEqual("straße".casefold(), "strasse".casefold())
        found = ui.case_collisions(self.records(("straße", "s-20260101-0000"),
                                                ("strasse", "s-20260101-0000")))
        self.assertEqual(len(found), 1, found)

    def test_the_check_is_wired_into_validation(self):
        """The pure tests pass whether or not the check runs. This one does not.

        `case_collisions` is only worth anything once its result reaches `load_units`'s problem list, and
        that wiring is invisible to a test that calls the function directly — reverting it left all five
        pure tests green. Asserted through the public entry point on a store that is *valid* in every
        other respect, so a non-empty problem list can only come from the collision check.
        """
        problems = ui.case_collisions(self.records(("Foo", "s-20260101-0000"),
                                                  ("foo", "s-20260101-0000")))
        self.assertTrue(problems, "fixture must produce a collision for this test to mean anything")

        with tempfile.TemporaryDirectory() as tmp:
            store = Path(tmp) / "understandings"
            # One unit, named so its slug and its subject each collide with a name the check is fed
            # directly — the store on disk is clean, so the only way a problem appears is the wiring.
            original = ui.case_collisions
            ui.case_collisions = lambda records: list(problems)
            try:
                write_unit(store, "s-20260101-0000", "alpha")
                _units, _superseded, reported = ui.load_units(store)
            finally:
                ui.case_collisions = original
            self.assertTrue(
                any("case-insensitive" in p for p in reported),
                f"collision problems never reached load_units' problem list: {reported}",
            )

    def test_a_clean_store_reports_no_collision_problem(self):
        """The negative control on the wiring: silence when there is nothing to report."""
        with tempfile.TemporaryDirectory() as tmp:
            store = Path(tmp) / "understandings"
            write_unit(store, "s-20260101-0000", "alpha")
            write_unit(store, "s-20260101-0000", "beta")
            _units, _superseded, reported = ui.load_units(store)
            self.assertEqual([p for p in reported if "case-insensitive" in p], [])

    @unittest.skipUnless(case_sensitive_filesystem(Path(tempfile.gettempdir())),
                         "needs a case-sensitive filesystem: the colliding files cannot coexist "
                         "where the overwrite actually happens")
    def test_collision_on_disk_reaches_the_report_and_exit_code(self):
        """The pure check above is worthless if its result never reaches the user.

        Only runnable where two case-differing files can coexist, so on a case-insensitive machine the
        wiring is untested by this fixture and the pure tests above are the coverage.
        """
        with tempfile.TemporaryDirectory() as tmp:
            store = Path(tmp) / "understandings"
            write_unit(store, "s-20260101-0000", "alpha")
            write_unit(store, "s-20260101-0000", "Alpha")
            rc, _out, err = run(argv(store))
            self.assertEqual(rc, 1, "a case collision must be a validation failure")
            self.assertIn("case-insensitive", err)


class AgentsContextResolutionTests(unittest.TestCase):
    """`agents_context` is a repo-relative path, so the verdict must not depend on the CWD.

    The check used `Path(context).exists()` against the process CWD, so the documented
    `understanding_index.py <absolute-store-dir>` form reported every unit's context missing when run
    from anywhere but the repo root — 55 false problems and exit 1 on this repo's own clean store.
    """

    def write_context_unit(self, repo: Path, context: str) -> Path:
        store = repo / ".context" / "understandings"
        folder = store / "proj-20260930-1700"
        folder.mkdir(parents=True, exist_ok=True)
        (repo / "src").mkdir(exist_ok=True)
        (repo / "src" / "Handler.cs").write_text("// code\n", encoding="utf-8")
        (folder / f"alpha{ui.UNIT_SUFFIX}").write_text(
            UNIT_FRONTMATTER.format(slug="alpha", updated="2026-09-30")
            .replace("---\n# Answer", f"agents_context: {context}\n---\n# Answer"), encoding="utf-8")
        return store

    def test_context_is_found_when_run_from_outside_the_repo(self):
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=True)
            store = self.write_context_unit(repo, "src/Handler.cs")
            previous = os.getcwd()
            os.chdir(tempfile.gettempdir())
            try:
                rc, _, err = run(argv(store))
            finally:
                os.chdir(previous)
            self.assertEqual(rc, 0, err)
            self.assertNotIn("does not exist", err)

    def test_same_verdict_from_inside_and_outside_the_repo(self):
        """The point of the fix: one store, one verdict, regardless of where the generator runs."""
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=True)
            store = self.write_context_unit(repo, "src/Handler.cs")
            inside_rc, _, inside_err = run(argv(store))
            previous = os.getcwd()
            os.chdir(tempfile.gettempdir())
            try:
                outside_rc, _, outside_err = run(argv(store))
            finally:
                os.chdir(previous)
            self.assertEqual(inside_rc, outside_rc)
            self.assertEqual(inside_err, outside_err)

    def test_a_genuinely_missing_context_is_still_reported(self):
        """Negative control: fixing the base directory must not disable the check."""
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=True)
            store = self.write_context_unit(repo, "src/NotThere.cs")
            rc, _, err = run(argv(store))
            self.assertEqual(rc, 1)
            self.assertIn("does not exist", err)

    def test_root_is_the_repo_for_the_default_layout(self):
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=True)
            store = repo / ".context" / "understandings"
            self.assertEqual(ui.context_root(store), repo)

    def test_root_is_the_store_parent_outside_the_default_layout(self):
        """A bespoke store directory has no `.context` parent to climb, so it must not invent one."""
        with tempfile.TemporaryDirectory() as tmp:
            store = Path(tmp) / "somewhere" / "store"
            self.assertEqual(ui.context_root(store), store.parent)


class DurabilityGuardTests(unittest.TestCase):
    def test_no_archive_flags_every_unit_unpublished(self):
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=True)
            store = repo / ".context" / "understandings"
            write_unit(store, "proj-20260930-1700", "alpha")
            write_unit(store, "proj-20260930-1710", "beta")
            rc, out, _ = run(argv(store, review=True))
            self.assertEqual(rc, 0)
            self.assertIn("unpublished", out)
            self.assertIn("alpha", out)
            self.assertIn("beta", out)
            self.assertIn("gitignored", out)

    def test_published_then_one_new_unit_reports_only_that_unit(self):
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=True)
            store = repo / ".context" / "understandings"
            write_unit(store, "proj-20260930-1700", "old")
            publish(store, "understandings-20260930-180000.zip", "proj-20260930-1700/old.understanding.md")
            write_unit(store, "proj-20260930-1900", "new")
            rc, out, _ = run(argv(store, review=True))
            self.assertEqual(rc, 0)
            self.assertIn("proj-20260930-1900", out)
            self.assertNotIn("proj-20260930-1700", out)

    def test_unit_left_out_of_a_newer_portable_only_archive_stays_unpublished(self):
        """A `--portable-only` archive is newer than the repo-specific unit it excluded. Judging by
        archive time reported that unit published, so it could vanish with no warning."""
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=True)
            store = repo / ".context" / "understandings"
            write_unit(store, "proj-20260930-1700", "portable-one")
            write_unit(store, "proj-20260930-1700", "repo-only")
            publish(store, "understandings-20260930-180000.zip",
                    "proj-20260930-1700/portable-one.understanding.md")
            self.assertEqual(flagged(store), {"repo-only"})

    def test_capture_is_decided_by_membership_not_by_stamp(self):
        """A unit stamped after the archive but present in it is captured; one stamped before it but
        absent is not. The stamp says nothing about what the archive holds."""
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=True)
            store = repo / ".context" / "understandings"
            write_unit(store, "proj-20260930-1803", "earlier-missing")
            write_unit(store, "proj-20260930-1806", "later-included")
            publish(store, "understandings-20260930-180530.zip",
                    "proj-20260930-1806/later-included.understanding.md")
            self.assertEqual(flagged(store), {"earlier-missing"})

    def test_unfiled_unit_follows_membership_like_any_other(self):
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=True)
            store = repo / ".context" / "understandings"
            write_unit(store, ui.UNFILED, "kept")
            write_unit(store, ui.UNFILED, "loose")
            publish(store, "understandings-20260930-180000.zip", f"{ui.UNFILED}/kept.understanding.md")
            self.assertEqual(flagged(store), {"loose"})

    def test_unreadable_archive_counts_for_nothing(self):
        """A corrupt zip cannot prove anything was captured; its units stay reported, without a crash."""
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=True)
            store = repo / ".context" / "understandings"
            write_unit(store, "proj-20260930-1700", "alpha")
            pub = store.parent / ui.PUBLISH_DIR_NAME
            pub.mkdir(parents=True, exist_ok=True)
            (pub / "understandings-20260930-180000.zip").write_bytes(b"not a zip")
            self.assertEqual(flagged(store), {"alpha"})

    def test_tracked_store_produces_no_warning(self):
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=False)
            store = repo / ".context" / "understandings"
            write_unit(store, "proj-20260930-1700", "alpha")
            rc, out, _ = run(argv(store, review=True))
            self.assertEqual(rc, 0)
            self.assertNotIn("unpublished", out)
            self.assertNotIn("gitignored", out)

    def test_review_exit_code_unaffected_by_unpublished(self):
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=True)
            store = repo / ".context" / "understandings"
            write_unit(store, "proj-20260930-1700", "alpha")
            rc0, out0, _ = run(argv(store))
            rc1, out1, _ = run(argv(store, review=True))
            self.assertEqual(rc0, 0)
            self.assertEqual(rc1, 0)
            self.assertIn("unpublished", out1)


if __name__ == "__main__":
    unittest.main()
