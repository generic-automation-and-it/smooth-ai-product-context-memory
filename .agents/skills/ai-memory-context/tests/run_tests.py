#!/usr/bin/env python3
"""Harness for `memory_index.py`.

Written after the parser bug it now pins: the first version of `parse_frontmatter` promoted the
first `provenance` child to a map and then dropped the value of every later one, so a perfectly good
`provenance:` block reported `session` and `source` as missing while the file looked fine to the eye.
A parser that loses values is invisible until something depends on it, which is why every field is
parsed and asserted here rather than spot-checked.
"""

from __future__ import annotations

import importlib.util
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location(
    "memory_index", Path(__file__).resolve().parents[1] / "scripts" / "memory_index.py"
)
mi = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(mi)

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "memory_index.py"

UNIT = """---
slug: {slug}
description: {description}
question: {question}
scope: {scope}
confidence: {confidence}
updated: {updated}
agents_context: src/Example.cs
provenance:
  learned: 2026-10-01
  session: ses_test
  source: harness
---
# {question}

The answer body.
"""


def unit_text(slug="a-memory", description="A description that is long enough",
              question="What is the answer?", scope="repo-specific",
              confidence="observed", updated="2026-10-01") -> str:
    return UNIT.format(slug=slug, description=description, question=f"question: {question}",
                       scope=scope, confidence=confidence, updated=updated)


def write(store: Path, run: str, category: str, slug: str, **kwargs) -> Path:
    folder = store / run / category if category else store / run
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / f"{slug}{mi.UNIT_SUFFIX}"
    path.write_text(unit_text(slug=slug, **kwargs), encoding="utf-8")
    return path


def run(*args, cwd=None):
    return subprocess.run([sys.executable, "-B", str(SCRIPT), *args],
                          capture_output=True, text=True, cwd=cwd)


class FrontmatterTests(unittest.TestCase):
    def test_every_provenance_child_keeps_its_value(self):
        """The regression. The second and later children used to parse as empty strings."""
        fields = mi.parse_frontmatter(unit_text())
        self.assertEqual({"learned": "2026-10-01", "session": "ses_test", "source": "harness"},
                         fields["provenance"])

    def test_scalar_fields_are_read_whole(self):
        fields = mi.parse_frontmatter(unit_text(description="A description that is long enough"))
        self.assertEqual("A description that is long enough", mi.unquote(fields["description"]))
        self.assertEqual("repo-specific", mi.unquote(fields["scope"]))

    def test_text_without_frontmatter_parses_to_nothing(self):
        self.assertEqual({}, mi.parse_frontmatter("# just a body\n"))

    def test_a_missing_provenance_is_reported_rather_than_defaulted(self):
        body = "slug: x\ndescription: A description long enough\nscope: portable\nconfidence: observed\nupdated: 2026-10-01\n"
        _unit, problems = mi.read_unit(_named(body, "x"), "20261016-1432-ses_a", "cat")
        self.assertTrue(any("provenance" in p for p in problems), problems)


def _named(body: str, slug: str) -> Path:
    path = Path(tempfile.mkdtemp()) / f"{slug}{mi.UNIT_SUFFIX}"
    path.write_text(f"---\n{body}---\n# q\n\nbody\n", encoding="utf-8")
    return path


class StoreLayoutTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(lambda: shutil.rmtree(self.tmp, ignore_errors=True))
        self.store = Path(self.tmp) / "context-memories"
        self.store.mkdir()

    def problems(self):
        return mi.load_units(self.store)[2]

    def test_a_run_folder_unit_is_found_and_indexed_under_its_category(self):
        write(self.store, "20261016-1432-ses_a", "pipeline", "prune-order")
        units, _sup, problems = mi.load_units(self.store)
        self.assertEqual([], problems)
        self.assertEqual(["prune-order"], [u["slug"] for u in units])
        self.assertEqual("pipeline", units[0]["subject"])

    def test_a_run_folder_holding_both_shapes_indexes_everything_and_names_the_strays(self):
        """Finding 1. A run folder holding units *and* category subfolders.

        The dispatch read `nested = not direct and bool(categories)`, so a mixed folder was taken as
        legacy and every nested category subtree vanished from the index -- with no problem and exit
        0, producing a complete-looking INDEX.md missing every categorised memory in that run. One
        stray unit at the run root was itself enough to suppress the nested branch.

        Both halves now survive, and the losing side is named rather than dropped: fixing the dispatch
        alone would have stopped the category half disappearing and left the run-root half silently
        discarded in its place, which is the same defect with the other name on it.
        """
        write(self.store, "20261016-1432-ses_a", "pipeline", "nested-one")
        folder = self.store / "20261016-1432-ses_a"
        (folder / f"direct-one{mi.UNIT_SUFFIX}").write_text(unit_text(slug="direct-one"),
                                                           encoding="utf-8")
        units, _sup, problems = mi.load_units(self.store)
        self.assertEqual(["nested-one"], [u["slug"] for u in units],
                         "the categorised memory was silently dropped")
        self.assertTrue(any("direct-one" in p and "run root" in p for p in problems), problems)

    def test_an_assets_sibling_does_not_turn_a_legacy_folder_into_a_nested_one(self):
        """The reason `categories` excludes `ASSETS_SUFFIX`. Applied here without inheriting the
        regression that exclusion was written to fix in the sibling indexer: a `<slug>.assets/` folder
        is a unit's evidence, not a category, so counting one emptied a legacy folder entirely."""
        folder = self.store / "legacysubject-20261014-0800"
        folder.mkdir(parents=True)
        (folder / f"keep{mi.UNIT_SUFFIX}").write_text(unit_text(slug="keep"), encoding="utf-8")
        assets = folder / f"keep{mi.ASSETS_SUFFIX}"
        assets.mkdir()
        (assets / "evidence.md").write_text("x", encoding="utf-8")
        units, _sup, problems = mi.load_units(self.store)
        self.assertEqual(["keep"], [u["slug"] for u in units])
        self.assertEqual([], problems)

    def test_a_legacy_shaped_store_still_reads(self):
        folder = self.store / "legacysubject-20261014-0800"
        folder.mkdir(parents=True)
        (folder / f"old-one{mi.UNIT_SUFFIX}").write_text(unit_text(slug="old-one"), encoding="utf-8")
        units, _sup, problems = mi.load_units(self.store)
        self.assertEqual([], problems)
        self.assertEqual(["old-one"], [u["slug"] for u in units])

    def test_a_mixed_store_indexes_both_shapes_without_complaining(self):
        write(self.store, "20261016-1432-ses_a", "pipeline", "new-one")
        folder = self.store / "legacysubject-20261014-0800"
        folder.mkdir(parents=True)
        (folder / f"old-one{mi.UNIT_SUFFIX}").write_text(unit_text(slug="old-one"), encoding="utf-8")
        units, _sup, problems = mi.load_units(self.store)
        self.assertEqual([], problems)
        self.assertEqual(["new-one", "old-one"], sorted(u["slug"] for u in units))

    def test_unfiled_reads_and_a_stamped_copy_supersedes_it(self):
        folder = self.store / mi.UNFILED
        folder.mkdir(parents=True)
        (folder / f"loose{mi.UNIT_SUFFIX}").write_text(unit_text(slug="loose"), encoding="utf-8")
        self.assertEqual(["loose"], [u["slug"] for u in mi.load_units(self.store)[0]])
        write(self.store, "20260101-0000-ses_a", "cat", "loose")
        units, superseded, _ = mi.load_units(self.store)
        self.assertTrue(units[0]["path"].startswith("20260101-0000-ses_a/"), units[0]["path"])
        self.assertEqual(["loose"], [u["slug"] for u in superseded])

    def test_an_empty_run_folder_is_reported(self):
        (self.store / "20261016-1432-ses_a").mkdir(parents=True)
        self.assertTrue(any("contains no memories" in p for p in self.problems()), self.problems())

    def test_a_run_folder_without_a_session_id_is_reported(self):
        (self.store / "20261016-1432").mkdir(parents=True)
        self.assertTrue(any("no session id" in p for p in self.problems()), self.problems())

    def test_a_stray_at_the_store_root_names_the_current_shape(self):
        (self.store / f"stray{mi.UNIT_SUFFIX}").write_text("x", encoding="utf-8")
        self.assertTrue(any("<category>" in p for p in self.problems()), self.problems())

    def test_a_unit_missing_the_postfix_is_named(self):
        folder = self.store / "20261016-1432-ses_a" / "cat"
        folder.mkdir(parents=True)
        (folder / "no-postfix.md").write_text("x", encoding="utf-8")
        self.assertTrue(any("missing the" in p and "cat/no-postfix.md" in p for p in self.problems()),
                        self.problems())


class VersionChainTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(lambda: shutil.rmtree(self.tmp, ignore_errors=True))
        self.store = Path(self.tmp) / "store"
        self.store.mkdir()

    def test_the_newest_run_wins(self):
        write(self.store, "20261015-0900-ses_a", "cat", "shared", updated="2026-10-15")
        write(self.store, "20261016-1432-ses_b", "cat", "shared", updated="2026-10-16")
        units, superseded, _ = mi.load_units(self.store)
        self.assertEqual(1, len(units))
        self.assertEqual("2026-10-16", units[0]["updated"])
        self.assertEqual(1, len(superseded))

    def test_the_same_minute_resolution_is_deterministic(self):
        """`updated` is required but date-only, so same-minute runs tie on stamp and updated. What
        holds at that boundary is stability, not any particular field winning."""
        write(self.store, "20261016-1432-ses_aaa", "zcat", "tie", updated="2026-10-16")
        write(self.store, "20261016-1432-ses_zzz", "acat", "tie", updated="2026-10-16")
        winners = {mi.load_units(self.store)[0][0]["path"] for _ in range(5)}
        self.assertEqual(1, len(winners), winners)

    def test_a_date_only_run_folder_is_not_a_stamp(self):
        self.assertEqual("", mi.stamp_of("20261016-ses_abc"))
        self.assertFalse(mi.RUN_STAMP_RE.match("20261016-ses_abc"))

    def test_stamp_and_session_parse_from_both_shapes(self):
        self.assertEqual("20261016-1432", mi.stamp_of("20261016-1432-ses_abc"))
        self.assertEqual("ses_abc", mi.session_of("20261016-1432-ses_abc"))
        self.assertEqual("20261016-1432", mi.stamp_of("subject-20261016-1432"))
        self.assertEqual("", mi.stamp_of(mi.UNFILED))
        self.assertEqual("", mi.stamp_of("unstamped"))


class RunFolderVerbTests(unittest.TestCase):
    def call(self, *args):
        environment = {k: v for k, v in os.environ.items() if not k.endswith("_SESSION_ID")}
        return subprocess.run([sys.executable, "-B", str(SCRIPT), *args],
                              capture_output=True, text=True, env=environment)

    def test_it_prints_a_run_folder_that_satisfies_the_stamp_shape(self):
        result = self.call("--run-folder", "ses_abc123")
        self.assertEqual(0, result.returncode, result.stderr)
        name = result.stdout.strip()
        self.assertTrue(mi.RUN_STAMP_RE.match(name), name)
        self.assertEqual("ses_abc123", mi.session_of(name))

    def test_it_reads_the_session_id_from_the_environment(self):
        result = subprocess.run(
            [sys.executable, "-B", str(SCRIPT), "--run-folder"],
            capture_output=True, text=True, env={**os.environ, "OPENCODE_SESSION_ID": "ses_env"})
        self.assertEqual("ses_env", mi.session_of(result.stdout.strip()))

    def test_an_explicit_id_outranks_the_environment(self):
        result = subprocess.run(
            [sys.executable, "-B", str(SCRIPT), "--run-folder", "ses_explicit"],
            capture_output=True, text=True, env={**os.environ, "OPENCODE_SESSION_ID": "ses_env"})
        self.assertEqual("ses_explicit", mi.session_of(result.stdout.strip()))

    def test_no_session_id_refuses_rather_than_inventing_one(self):
        result = self.call("--run-folder")
        self.assertEqual(2, result.returncode)
        self.assertIn("needs a session id", result.stderr)

    def test_an_id_with_a_separator_cannot_nest_the_units(self):
        """A `/` lands the units a level deeper, the run folder never gets a valid stamp shape, and
        every unit in the export drops out of the index with no error anywhere."""
        result = self.call("--run-folder", "weird/id with spaces")
        name = result.stdout.strip()
        self.assertNotIn("/", name)
        self.assertNotIn(" ", name)
        self.assertTrue(mi.RUN_STAMP_RE.match(name), name)

    def test_two_ids_that_sanitise_alike_still_get_separate_folders(self):
        self.assertNotEqual(mi.safe_session_id("a/b"), mi.safe_session_id("a_b"))

    def test_two_calls_in_the_same_minute_agree(self):
        self.assertEqual(self.call("--run-folder", "ses_a").stdout.strip(),
                         self.call("--run-folder", "ses_a").stdout.strip())

    def test_stamp_alone_prints_only_the_stamp(self):
        self.assertRegex(self.call("--stamp").stdout.strip(), r"^\d{8}-\d{4}$")


class CliTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(lambda: shutil.rmtree(self.tmp, ignore_errors=True))
        self.store = Path(self.tmp) / "store"
        self.store.mkdir()

    def test_it_writes_the_index_and_exits_zero_on_a_clean_store(self):
        write(self.store, "20261016-1432-ses_a", "cat", "one")
        result = run(str(self.store))
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        index = (self.store / "INDEX.md").read_text(encoding="utf-8")
        self.assertIn("## cat", index)
        self.assertIn("./20261016-1432-ses_a/cat/one.memory.md", index)

    def test_a_problem_exits_one_and_the_index_is_still_written(self):
        write(self.store, "20261016-1432-ses_a", "cat", "one")
        body = unit_text(slug="one").replace("confidence: observed", "confidence: maybe")
        (self.store / "20261016-1432-ses_a" / "cat" / f"one{mi.UNIT_SUFFIX}").write_text(
            body, encoding="utf-8")
        result = run(str(self.store))
        self.assertEqual(1, result.returncode)
        self.assertTrue((self.store / "INDEX.md").is_file(),
                        "the index must still be written so the drift stays visible")

    def test_an_empty_store_still_writes_an_index(self):
        result = run(str(self.store))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("_No memories encoded yet._", (self.store / "INDEX.md").read_text(encoding="utf-8"))

    def test_a_missing_store_exits_two(self):
        self.assertEqual(2, run(str(self.store / "nope")).returncode)

    def test_review_is_advisory_and_never_fails_the_run(self):
        write(self.store, "20261016-1432-ses_a", "cat", "one", updated="2020-01-01")
        result = run(str(self.store), "--review")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("stale", result.stdout)

    def test_there_is_no_publish_verb(self):
        """LADR-001: durability is the user's filesystem. A half-ported publish would be worse than
        none, so the verb is absent rather than present and unverified."""
        self.assertEqual(2, run(str(self.store), "--publish").returncode)
        self.assertNotIn("--publish <", run("--help").stdout)


class TemplateTests(unittest.TestCase):
    def test_the_shipped_template_validates_as_a_placeholder_rich_example(self):
        """The template must parse under the same parser the store uses, or it teaches a shape the
        generator rejects."""
        body = (Path(__file__).resolve().parents[1] / "assets" / "MEMORY.template.md").read_text(
            encoding="utf-8")
        fields = mi.parse_frontmatter(body)
        for field in mi.REQUIRED_FIELDS:
            self.assertIn(field, fields, f"template omits required field {field}")
        for field in mi.REQUIRED_PROVENANCE_FIELDS:
            self.assertIn(field, fields["provenance"], f"template omits provenance.{field}")


if __name__ == "__main__":
    unittest.main()
