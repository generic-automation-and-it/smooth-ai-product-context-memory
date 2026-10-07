#!/usr/bin/env python3
"""Committed L0 harness for the ai-understanding index generator. stdlib unittest; no external runner.

Run: python3 -B .agents/skills/ai-understanding/tests/run_tests.py

Covers the durability guard: a gitignored store that holds current units no publish archive contains
(judged by archive membership, never archive time) is warned about, and the store's gitignore status — not a text search — decides whether any
warning is owed. The guard is advisory: no reported unit changes the exit code.
"""

from __future__ import annotations

import io
import os
import re
import shutil
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
    # A probe directory of its own, never a fixed name: two runs sharing `directory` (two harnesses on
    # one machine, or CI shards) used to create and remove the same `.case-probe`, so one run's cleanup
    # deleted the other's probe mid-check (issue 186). Only what this call created is removed.
    probe = Path(tempfile.mkdtemp(prefix="case-probe-", dir=directory))
    try:
        (probe / "a").mkdir()
        (probe / "A").mkdir()
    except OSError:
        return False
    finally:
        shutil.rmtree(probe, ignore_errors=True)
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
    publish_with(store, name, zipfile.ZIP_STORED, *unit_paths)


def publish_with(store: Path, name: str, compression: int, *unit_paths: str) -> None:
    pub = store.parent / ui.PUBLISH_DIR_NAME
    pub.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(pub / name, "w", compression) as zf:
        for rel in unit_paths:
            zf.write(store / rel, rel)
        zf.writestr("INDEX.md", "# index\n")


def corrupt_member(archive: Path, member: str) -> None:
    """Flip bytes inside one member's data, leaving the central directory (and `namelist()`) intact."""
    with zipfile.ZipFile(archive) as zf:
        info = zf.getinfo(member)
    data = bytearray(archive.read_bytes())
    # Local header: 30 fixed bytes, then the name and extra field whose lengths sit at offsets 26/28.
    name_len = int.from_bytes(data[info.header_offset + 26:info.header_offset + 28], "little")
    extra_len = int.from_bytes(data[info.header_offset + 28:info.header_offset + 30], "little")
    start = info.header_offset + 30 + name_len + extra_len
    for i in range(start + 2, start + 2 + min(8, info.compress_size - 2)):
        data[i] ^= 0xFF
    archive.write_bytes(bytes(data))


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
        self.assertIn(f"'s-20260101-0000/Foo{ui.UNIT_SUFFIX}'", found[0])
        self.assertIn(f"'s-20260101-0000/foo{ui.UNIT_SUFFIX}'", found[0])
        self.assertIn("unit file", found[0])
        self.assertIn("case-insensitive", found[0])

    def test_case_differing_slugs_in_differently_stamped_folders_are_distinct_files(self):
        """Issue 179: a store-wide slug bucket reported these, but they are two paths on every filesystem."""
        self.assertEqual(ui.case_collisions(self.records(("Foo", "auth-20261001-1200"),
                                                        ("foo", "auth-20261002-1200"))), [])

    def test_folders_differing_only_by_case_in_name_but_not_stamp_are_distinct(self):
        """`Auth-<stamp1>` and `auth-<stamp2>` are two directories; the full stamped name is compared."""
        self.assertEqual(ui.case_collisions(self.records(("x", "Auth-20261001-1200"),
                                                        ("x", "auth-20261002-1200"))), [])

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
            #
            # The substitute records what it was handed. A lambda that ignored `records` and returned
            # the canned problems would pass just as green if `load_units` passed the wrong records —
            # a supersede-only list, say, or an empty one — so the call site's actual argument is
            # asserted, not just that the function was reached.
            seen: list[list[dict]] = []
            original = ui.case_collisions

            def record_and_report(records):
                seen.append(records)
                return list(problems)

            ui.case_collisions = record_and_report
            try:
                write_unit(store, "s-20260101-0000", "alpha")
                _units, _superseded, reported = ui.load_units(store)
            finally:
                ui.case_collisions = original
            self.assertEqual(
                len(seen), 1, f"case_collisions must be called exactly once per load, saw {len(seen)}"
            )
            self.assertEqual(
                sorted(r["slug"] for r in seen[0]), ["alpha"],
                f"case_collisions must receive the store's own records, got {seen[0]}",
            )
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


class InheritedShapeTests(unittest.TestCase):
    """`provenance.inherited` must be a block list; any other present shape is a validation problem.

    A scalar never reaches the list-only placeholder check or the lineage reader, so a one-line
    placeholder exited 0 and silently recorded nothing (issue 179).
    """

    def write_inherited_unit(self, store: Path, inherited_block: str) -> None:
        folder = store / "proj-20260930-1700"
        folder.mkdir(parents=True, exist_ok=True)
        (folder / f"alpha{ui.UNIT_SUFFIX}").write_text(
            UNIT_FRONTMATTER.format(slug="alpha", updated="2026-09-30")
            .replace("  source: test harness\n", f"  source: test harness\n{inherited_block}"),
            encoding="utf-8")

    def problems_for(self, inherited_block: str) -> list[str]:
        with tempfile.TemporaryDirectory() as tmp:
            store = Path(tmp) / "understandings"
            self.write_inherited_unit(store, inherited_block)
            return ui.load_units(store)[2]

    def test_scalar_placeholder_is_reported(self):
        found = [p for p in self.problems_for("  inherited: <[[slug]] this session acted on>\n")
                 if "provenance.inherited" in p]
        self.assertEqual(len(found), 1, found)
        self.assertIn("block list", found[0])

    def test_scalar_placeholder_fails_the_exit_code(self):
        with tempfile.TemporaryDirectory() as tmp:
            store = Path(tmp) / "understandings"
            self.write_inherited_unit(store, "  inherited: <[[slug]] this session acted on>\n")
            rc, _out, err = run(argv(store))
            self.assertEqual(rc, 1)
            self.assertIn("provenance.inherited", err)

    def test_bare_scalar_name_is_reported(self):
        self.assertTrue(any("provenance.inherited" in p
                            for p in self.problems_for("  inherited: alpha\n")))

    def test_flow_sequence_is_reported_once_by_the_inline_list_check(self):
        found = [p for p in self.problems_for("  inherited: [[alpha]]\n") if "provenance.inherited" in p]
        self.assertEqual(len(found), 1, found)
        self.assertIn("inline list", found[0])

    def test_block_list_and_absence_are_accepted(self):
        """Negative controls: the shapes the template uses must stay silent."""
        self.assertEqual(self.problems_for("  inherited:\n    - \"[[alpha]]\"\n"), [])
        self.assertEqual(self.problems_for(""), [])


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

    def test_a_unit_whose_assets_were_left_out_is_not_published(self):
        """Review 5432012955 #3: the unit file alone counted as published, so a lost workspace could
        leave a "published" Understanding without the repro or diagram in its `.assets/`."""
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=True)
            store = repo / ".context" / "understandings"
            write_unit(store, "proj-20260930-1700", "alpha")
            assets = store / "proj-20260930-1700" / ("alpha" + ui.ASSETS_SUFFIX)
            (assets / "repro").mkdir(parents=True)
            (assets / "repro" / "steps.sh").write_text("echo repro\n", encoding="utf-8")
            (assets / "diagram.md").write_text("graph\n", encoding="utf-8")
            unit = "proj-20260930-1700/alpha.understanding.md"
            publish(store, "understandings-20260930-180000.zip", unit,
                    "proj-20260930-1700/alpha.assets/diagram.md")
            self.assertNotIn(unit, ui.published_paths(store), "one asset file is missing")
            publish(store, "understandings-20260930-190000.zip", unit,
                    "proj-20260930-1700/alpha.assets/diagram.md",
                    "proj-20260930-1700/alpha.assets/repro/steps.sh")
            self.assertIn(unit, ui.published_paths(store), "control: unit and every asset archived")

    def test_a_unit_changed_after_its_publish_is_not_published(self):
        """Consumer review 5440964552 #2: membership was judged by path alone, so a unit redacted in
        place, or an asset refreshed, after the last publish still read as captured."""
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=True)
            store = repo / ".context" / "understandings"
            write_unit(store, "proj-20260930-1700", "alpha")
            assets = store / "proj-20260930-1700" / ("alpha" + ui.ASSETS_SUFFIX)
            assets.mkdir(parents=True)
            (assets / "diagram.md").write_text("graph v1\n", encoding="utf-8")
            unit = "proj-20260930-1700/alpha.understanding.md"
            publish(store, "understandings-20260930-180000.zip", unit,
                    "proj-20260930-1700/alpha.assets/diagram.md")
            self.assertIn(unit, ui.published_paths(store), "control: archived as it stands")
            (assets / "diagram.md").write_text("graph v2\n", encoding="utf-8")
            self.assertNotIn(unit, ui.published_paths(store), "an asset changed after publish")
            (assets / "diagram.md").write_text("graph v1\n", encoding="utf-8")
            local = store / unit
            local.write_text(local.read_text(encoding="utf-8") + "\nA <REDACTED> value.\n",
                             encoding="utf-8")
            self.assertNotIn(unit, ui.published_paths(store), "the unit changed after publish")

    def test_publishs_own_edits_to_the_archived_copy_still_count_as_published(self):
        """The archived copy gains `provenance.published_from` and, under `--portable-only`, loses the
        brackets around an excluded slug; neither is a change to the knowledge."""
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=True)
            store = repo / ".context" / "understandings"
            write_unit(store, "proj-20260930-1700", "alpha")
            unit = "proj-20260930-1700/alpha.understanding.md"
            local = store / unit
            local.write_text(local.read_text(encoding="utf-8").replace(
                "provenance:\n", "provenance:\n  inherited:\n    - \"[[beta]]\"\n", 1), encoding="utf-8")
            archived = local.read_text(encoding="utf-8").replace("[[beta]]", "beta").replace(
                "provenance:\n", "provenance:\n  published_from: org/repo\n", 1)
            self.assertNotEqual(archived, local.read_text(encoding="utf-8"))
            pub = store.parent / ui.PUBLISH_DIR_NAME
            pub.mkdir(parents=True, exist_ok=True)
            with zipfile.ZipFile(pub / "understandings-20260930-180000.zip", "w") as zf:
                zf.writestr(unit, archived)
            self.assertIn(unit, ui.published_paths(store))

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

    def test_a_member_outside_the_published_shape_proves_nothing(self):
        """Issue 186: the last two parts of any member name were taken as a unit path, so `x/<subject>/
        <slug>`, `../<subject>/<slug>` or an absolute name marked the unit published while no consume
        would place it there. Only `<subject>/<slug>.understanding.md` counts."""
        rel = "proj-20260930-1700/alpha.understanding.md"
        for name in ("wrap/" + rel, "../" + rel, "/" + rel, "./" + rel, "proj-20260930-1700//alpha.understanding.md"):
            with self.subTest(member=name), tempfile.TemporaryDirectory() as tmp:
                repo = make_repo(tmp, ignore_store=True)
                store = repo / ".context" / "understandings"
                write_unit(store, "proj-20260930-1700", "alpha")
                pub = store.parent / ui.PUBLISH_DIR_NAME
                pub.mkdir(parents=True, exist_ok=True)
                with zipfile.ZipFile(pub / "understandings-20260930-180000.zip", "w") as zf:
                    zf.writestr(name, "body\n")
                self.assertEqual(flagged(store), {"alpha"})

    def test_a_symlink_entry_proves_nothing_was_published(self):
        """Issue 188: a symlink entry named like a unit was counted as the unit's capture, though consume
        refuses it and it holds no unit."""
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=True)
            store = repo / ".context" / "understandings"
            write_unit(store, "proj-20260930-1700", "alpha")
            pub = store.parent / ui.PUBLISH_DIR_NAME
            pub.mkdir(parents=True, exist_ok=True)
            info = zipfile.ZipInfo("proj-20260930-1700/alpha.understanding.md")
            info.external_attr = (0o120777 << 16)
            with zipfile.ZipFile(pub / "understandings-20260930-180000.zip", "w") as zf:
                zf.writestr(info, "../../elsewhere")
            self.assertEqual(flagged(store), {"alpha"})

    def test_a_directory_entry_named_like_a_unit_proves_nothing_was_published(self):
        """Consumer review 5438563690 #3: a directory-mode member named like a unit was credited as
        published, though the archive held no regular unit file."""
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=True)
            store = repo / ".context" / "understandings"
            write_unit(store, "proj-20260930-1700", "alpha")
            pub = store.parent / ui.PUBLISH_DIR_NAME
            pub.mkdir(parents=True, exist_ok=True)
            info = zipfile.ZipInfo("proj-20260930-1700/alpha.understanding.md")
            info.external_attr = (0o040755 << 16)
            with zipfile.ZipFile(pub / "understandings-20260930-180000.zip", "w") as zf:
                zf.writestr(info, "body\n")
            self.assertEqual(flagged(store), {"alpha"})

    def test_an_archive_consume_would_refuse_proves_nothing(self):
        """Issue 190: units in an archive `--consume` rejects — here an escaping entry beside a valid
        unit — were credited as published, so an unrestorable archive silenced the warning."""
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=True)
            store = repo / ".context" / "understandings"
            write_unit(store, "proj-20260930-1700", "alpha")
            pub = store.parent / ui.PUBLISH_DIR_NAME
            pub.mkdir(parents=True, exist_ok=True)
            with zipfile.ZipFile(pub / "understandings-20260930-180000.zip", "w") as zf:
                zf.write(store / "proj-20260930-1700" / "alpha.understanding.md",
                         "proj-20260930-1700/alpha.understanding.md")
                zf.writestr("../escape.md", "x\n")
            self.assertEqual(flagged(store), {"alpha"})

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

    def test_a_damaged_member_makes_its_archive_prove_nothing(self):
        """Issue 182: the central directory still lists a member whose bytes are damaged, so judging by
        `namelist()` reported it published. Stored data fails its CRC (`BadZipFile`); a deflate stream
        fails in zlib (`zlib.error`, not an `OSError`), which a narrow except let crash the generator.

        Issue 182 also spared the damaged member's siblings. Issue 184 made `--consume` refuse the
        whole archive for one damaged member, so those siblings cannot be restored from it either, and
        crediting them silenced the warning for units nothing could bring back (issue 190)."""
        for compression in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED):
            with self.subTest(compression=compression), tempfile.TemporaryDirectory() as tmp:
                repo = make_repo(tmp, ignore_store=True)
                store = repo / ".context" / "understandings"
                write_unit(store, "proj-20260930-1700", "damaged")
                write_unit(store, "proj-20260930-1700", "intact")
                archive = store.parent / ui.PUBLISH_DIR_NAME / "understandings-20260930-180000.zip"
                publish_with(store, archive.name, compression,
                             "proj-20260930-1700/damaged.understanding.md",
                             "proj-20260930-1700/intact.understanding.md")
                corrupt_member(archive, "proj-20260930-1700/damaged.understanding.md")
                with zipfile.ZipFile(archive) as zf:
                    self.assertIn("proj-20260930-1700/damaged.understanding.md", zf.namelist())
                self.assertEqual(flagged(store), {"damaged", "intact"})
                rc, out, err = run(argv(store, review=True))
                self.assertEqual(rc, 0, err)
                self.assertIn("proj-20260930-1700/damaged.understanding.md", out)
                self.assertIn("proj-20260930-1700/intact.understanding.md", out)
                self.assertNotIn("Traceback", err)

    def test_superseded_copies_are_workspace_local_and_never_flagged(self):
        """Issue 182: publish archives current versions only, so a superseded copy is never in any
        archive. Flagging it would raise a warning `--publish` can never clear."""
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=True)
            store = repo / ".context" / "understandings"
            write_unit(store, "proj-20260930-1700", "decay")
            write_unit(store, "proj-20261001-0900", "decay")
            publish(store, "understandings-20261001-100000.zip",
                    "proj-20261001-0900/decay.understanding.md")
            self.assertEqual(flagged(store), set())
            rc, out, _ = run(argv(store, review=True))
            self.assertEqual(rc, 0)
            self.assertNotIn("unpublished", out)
            self.assertNotIn("warning:", out)
            self.assertIn("superseded copy(ies) on disk", out)

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


SKILL_DIR = Path(__file__).resolve().parents[1]


def make_archive(path: Path, *entries) -> Path:
    """Entries are names (written with a small body) or `(ZipInfo, body)` pairs for special modes."""
    with zipfile.ZipFile(path, "w") as zf:
        for entry in entries:
            if isinstance(entry, tuple):
                zf.writestr(*entry)
            else:
                zf.writestr(entry, "body\n")
    return path


def consume(archive: Path, store: Path) -> tuple[int, str, str]:
    return run(["understanding_index.py", "--consume-check", str(archive), str(store)])


class ConsumeCheckTests(unittest.TestCase):
    """Issue 182: the pre-extraction gate was a documented heredoc and pipeline the skill's
    `allowed-tools` could not run, so it moved into the generator as `--consume-check`."""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        self.store = self.root / "store"
        self.store.mkdir()

    def tearDown(self):
        self._tmp.cleanup()

    def assert_refused(self, archive: Path, fragment: str) -> None:
        before = sorted(p.as_posix() for p in self.store.rglob("*"))
        rc, out, _ = consume(archive, self.store)
        self.assertEqual(rc, 1, out)
        self.assertIn(fragment, out)
        self.assertIn("refused: the whole archive is rejected", out)
        self.assertEqual(sorted(p.as_posix() for p in self.store.rglob("*")), before,
                         "the check must never extract anything")

    def test_a_clean_archive_is_accepted(self):
        archive = make_archive(self.root / "a.zip", "s-20260101-0000/a.understanding.md", "INDEX.md")
        rc, out, _ = consume(archive, self.store)
        self.assertEqual(rc, 0, out)
        self.assertIn("safe to extract", out)

    def test_escaping_paths_are_refused(self):
        for name in ("../evil.understanding.md", "s-20260101-0000/../../evil.md", "/etc/evil.md",
                     "C:/evil.md", "s-20260101-0000\\..\\evil.md"):
            with self.subTest(name=name):
                self.assert_refused(make_archive(self.root / "e.zip", name), "escapes the target store")

    def test_a_symlink_entry_is_refused(self):
        info = zipfile.ZipInfo("s-20260101-0000/link.understanding.md")
        info.external_attr = (0o120777 << 16)
        self.assert_refused(make_archive(self.root / "l.zip", (info, "/etc/passwd")), "symlink entry")

    def test_a_non_regular_entry_is_refused(self):
        """Consumer review 5438563690 #3: only symlinks were refused, so a directory, FIFO or device
        entry named like a unit passed the gate. A folder entry named as a folder, and an entry with
        no Unix mode at all, still pass."""
        for mode in (0o040755, 0o010644, 0o020644):
            with self.subTest(mode=oct(mode)):
                info = zipfile.ZipInfo("s-20260101-0000/odd.understanding.md")
                info.external_attr = mode << 16
                self.assert_refused(make_archive(self.root / "n.zip", (info, "x")),
                                    "not a regular file or folder entry")
        folder = zipfile.ZipInfo("s-20260101-0000/")
        folder.external_attr = 0o040755 << 16
        plain = zipfile.ZipInfo("s-20260101-0000/a.understanding.md")
        plain.external_attr = 0
        rc, out, _ = consume(make_archive(self.root / "ok.zip", (folder, ""), (plain, "x")), self.store)
        self.assertEqual(rc, 0, out)

    def test_an_existing_file_reached_through_an_internal_symlink_is_refused(self):
        """Issue 188: a local folder symlinked to another folder inside the store passed the escape
        check, and the name-based collision check could not see the file the entry would overwrite."""
        real = self.store / "s-20260101-0000"
        real.mkdir()
        (real / "a.understanding.md").write_text("local\n", encoding="utf-8")
        (self.store / "alias-20260101-0000").symlink_to(real, target_is_directory=True)
        self.assert_refused(make_archive(self.root / "a.zip", "alias-20260101-0000/a.understanding.md"),
                            "local '")

    def test_two_entries_landing_on_one_file_are_refused(self):
        """Issue 190: `alias/x` (through a local folder symlinked inside the store) and `real/x` have
        different names and the same destination; compared by name only, one extracted over the other."""
        real = self.store / "s-20260101-0000"
        real.mkdir()
        (self.store / "alias-20260101-0000").symlink_to(real, target_is_directory=True)
        self.assert_refused(make_archive(self.root / "d.zip", "alias-20260101-0000/b.understanding.md",
                                         "s-20260101-0000/b.understanding.md"),
                            "elsewhere in this archive")

    def test_a_local_symlinked_folder_that_leads_outside_is_refused(self):
        outside = self.root / "outside"
        outside.mkdir()
        (self.store / "s-20260101-0000").symlink_to(outside, target_is_directory=True)
        self.assert_refused(make_archive(self.root / "s.zip", "s-20260101-0000/a.understanding.md"),
                            "through a local symlink")

    def test_a_case_collision_between_entries_is_refused(self):
        archive = make_archive(self.root / "c.zip", "Foo-20260101-0000/a.understanding.md",
                               "foo-20260101-0000/b.understanding.md")
        self.assert_refused(archive, "elsewhere in this archive")

    def test_a_case_collision_with_a_local_folder_is_refused(self):
        write_unit(self.store, "s-20260101-0000", "local")
        archive = make_archive(self.root / "c.zip", "S-20260101-0000/incoming.understanding.md")
        self.assert_refused(archive, "local '")

    def test_an_exactly_matching_local_folder_is_not_a_collision(self):
        write_unit(self.store, "s-20260101-0000", "local")
        archive = make_archive(self.root / "x.zip", "s-20260101-0000/incoming.understanding.md",
                               "INDEX.md")
        (self.store / "INDEX.md").write_text("# index\n", encoding="utf-8")
        rc, out, _ = consume(archive, self.store)
        self.assertEqual(rc, 0, out)

    def test_casefold_catches_what_lower_does_not(self):
        archive = make_archive(self.root / "u.zip", "straße-20260101-0000/a.understanding.md",
                               "strasse-20260101-0000/b.understanding.md")
        self.assert_refused(archive, "elsewhere in this archive")

    def test_a_damaged_member_is_refused(self):
        """Issue 184: the gate inspected names and modes only, so an archive whose member data was
        damaged was cleared for extraction. Stored data fails its CRC; a deflate stream fails in zlib."""
        member = "s-20260101-0000/a.understanding.md"
        for compression in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED):
            with self.subTest(compression=compression):
                archive = self.root / f"d{compression}.zip"
                with zipfile.ZipFile(archive, "w", compression) as zf:
                    zf.writestr(member, "synthetic body long enough to corrupt\n" * 4)
                    zf.writestr("s-20260101-0000/b.understanding.md", "intact\n")
                    zf.writestr("INDEX.md", "# index\n")
                corrupt_member(archive, member)
                self.assert_refused(archive, f"'{member}' is damaged")

    def test_a_nested_index_is_not_exempt_from_collision(self):
        """Issue 184: the exemption matched any file named `INDEX.md`, so a nested one landing on a local
        file differing only by case was cleared. Only the generated root index is exempt."""
        (self.store / "s-20260101-0000").mkdir()
        (self.store / "s-20260101-0000" / "index.md").write_text("local\n", encoding="utf-8")
        archive = make_archive(self.root / "n.zip", "s-20260101-0000/INDEX.md")
        self.assert_refused(archive, "local '")

    def test_a_nested_index_collides_with_another_entry(self):
        archive = make_archive(self.root / "n2.zip", "s-20260101-0000/index.md",
                               "s-20260101-0000/INDEX.md")
        self.assert_refused(archive, "elsewhere in this archive")

    def test_a_folder_named_index_is_refused(self):
        """Issue 186: the root-index exemption also matched a directory entry `INDEX.md/` and a parent
        folder `INDEX.md/…`, so a folder could land where the generated index is written."""
        for entries, fragment in (((("INDEX.md/"),), "the store's root index must be a file"),
                                  (("INDEX.md/x.md",), "the store's root index must be a file"),
                                  (("index.md/s-20260101-0000/a.understanding.md",),
                                   "the store's root index must be a file")):
            with self.subTest(entries=entries):
                self.assert_refused(make_archive(self.root / "i.zip", *entries), fragment)
        (self.store / "INDEX.md").mkdir()
        self.assert_refused(make_archive(self.root / "j.zip", "INDEX.md"),
                            "'INDEX.md' must be a file")

    def test_the_root_index_stays_exempt(self):
        """Control: the root index still lands on a local one differing by case, as every consume does,
        and an archive wrapped in a top-level folder carrying its own index is not refused for it."""
        (self.store / "index.md").write_text("local\n", encoding="utf-8")
        rc, out, _ = consume(make_archive(self.root / "r.zip", "INDEX.md"), self.store)
        self.assertEqual(rc, 0, out)
        wrapped = make_archive(self.root / "w.zip", "understandings/INDEX.md",
                               "understandings/s-20260101-0000/a.understanding.md")
        rc, out, _ = consume(wrapped, self.store)
        self.assertEqual(rc, 0, out)

    def test_an_unreadable_archive_exits_2(self):
        bad = self.root / "bad.zip"
        bad.write_bytes(b"not a zip")
        rc, _, err = consume(bad, self.store)
        self.assertEqual(rc, 2)
        self.assertIn("not a readable zip archive", err)

    def test_bad_arguments_exit_2(self):
        self.assertEqual(run(["understanding_index.py", "--consume-check"])[0], 2)
        self.assertEqual(run(["understanding_index.py", "--consume-check", "a", "b", "c"])[0], 2)

    def test_stamp_prints_a_utc_minute_stamp(self):
        rc, out, _ = run(["understanding_index.py", "--stamp"])
        self.assertEqual(rc, 0)
        self.assertRegex(out.strip(), r"^\d{8}-\d{4}$")


# Inline spans that name a command without instructing the agent to run it. Each is described in
# the docs as something the script does itself or as the thing not to do.
DESCRIBED_NOT_RUN = {
    "git check-ignore",  # run by the generator, which reports its answer
    "python3 -",         # cited as the refused heredoc form
    "unzip -l",          # cited as unable to show a symlink entry
}
SHELL_WORDS = ("ls", "date", "python3", "python", "unzip", "zip", "grep", "git", "cat", "find", "mkdir",
               "cp", "mv", "rm", "bash", "sh", "cd", "sed", "awk", "curl", "echo", "printf", "touch",
               "tar")


class AllowedToolsCoverDocumentedCommandsTests(unittest.TestCase):
    """Issue 182: the docs told the agent to run commands `allowed-tools` did not permit (a heredoc
    `python3 -`, `… | grep`, `ls`, `date`), so the consume gate was denied or prompted and an agent
    that skipped it extracted unchecked. Every command the skill's docs instruct must fit a
    `Bash(<prefix>:*)` entry."""

    @staticmethod
    def allowed_prefixes() -> list[str]:
        text = (SKILL_DIR / "SKILL.md").read_text(encoding="utf-8")
        frontmatter = text.split("---", 2)[1]
        return re.findall(r"^\s*-\s*Bash\((.+?):\*\)\s*$", frontmatter, re.M)

    @staticmethod
    def documented_commands() -> list[tuple[str, str]]:
        docs = [SKILL_DIR / "SKILL.md", *sorted((SKILL_DIR / "references").glob("*.md"))]
        found = []
        for doc in docs:
            text = doc.read_text(encoding="utf-8")
            for block in re.findall(r"```(?:bash|sh|shell|zsh)\n(.*?)```", text, re.S):
                for line in block.splitlines():
                    line = line.split(" #", 1)[0].strip()
                    if line and not line.startswith("#"):
                        found.extend((doc.name, part.strip())
                                     for part in re.split(r"\|\||&&|\||;", line) if part.strip())
            for span in re.findall(r"`([^`\n]+)`", text):
                if span.split(" ", 1)[0] in SHELL_WORDS and span not in DESCRIBED_NOT_RUN:
                    found.append((doc.name, span))
        return found

    def test_every_documented_command_fits_an_allowed_prefix(self):
        prefixes = self.allowed_prefixes()
        self.assertIn("python3 .agents/skills/ai-understanding/scripts/understanding_index.py",
                      prefixes)
        commands = self.documented_commands()
        self.assertTrue(any("--consume-check" in c for _, c in commands), commands)
        for doc, command in commands:
            with self.subTest(doc=doc, command=command):
                self.assertNotIn("<<", command, "a heredoc is not a permitted command")
                self.assertTrue(any(command == p or command.startswith(p + " ") for p in prefixes),
                                f"{doc}: `{command}` fits no allowed-tools Bash prefix {prefixes}")


class CaseProbeTests(unittest.TestCase):
    """Issue 186: the filesystem probe used a fixed `.case-probe` name and removed it, so a run sharing
    the directory deleted another run's probe mid-check."""

    def test_the_probe_touches_only_what_it_created(self):
        with tempfile.TemporaryDirectory() as tmp:
            directory = Path(tmp)
            other = directory / ".case-probe" / "a"
            other.mkdir(parents=True)
            case_sensitive_filesystem(directory)
            self.assertTrue(other.is_dir(), "another run's probe was removed")
            self.assertEqual(sorted(p.name for p in directory.iterdir()), [".case-probe"],
                             "the probe left its own directory behind")


class ReadmeTests(unittest.TestCase):
    def test_loading_names_description_matching_for_outcome_units(self):
        """Issue 188: the README said only a question is matched, while an outcome unit carries none
        and is matched on its description (SKILL.md, Import)."""
        readme = (Path(ui.__file__).resolve().parents[1] / "README.md").read_text(encoding="utf-8")
        row = next(line for line in readme.splitlines() if line.startswith("| Loading |"))
        self.assertIn("description matches", row)


if __name__ == "__main__":
    unittest.main()
