"""The store-layout contract is implemented twice; this holds the two to each other.

`ai-understanding` and `ai-memory-context` ship **different scripts with different unit postfixes**,
so neither can import the other without becoming shared-dependent — and shared-dependent means broken
alone in a zip, which is the entire reason `ai-memory-context` exists. So the contract is duplicated,
and duplication without a gate is drift waiting for a reason.

They must agree, and agreement is a **stated design property** rather than an accident:
`ai-memory-context/AGENTS.md` LADR-002 records that the legacy `<subject>-<stamp>/<slug>` shape is
read "so a store copied from either skill works unchanged". Cross-compatibility *is* the contract, so
a store written by one indexer has to read identically under the other — the newest copy of a slug is
the same copy, and the same folder is a problem for the same reasons.

The gap was invisible: the two `version_key` bodies differ in their docstring and their line wrapping
while being logically identical. That is exactly what no gate looks like.

Only the contract is compared — `stamp_of`, `session_of`, `safe_session_id`, `run_folder_name`,
`group_versions`, `version_key`, and the constants they share. The unit postfix, the default store and
the INDEX renderer are deliberately *different*; that is the point of the second skill.
"""

from __future__ import annotations

import importlib.util
import inspect
import unittest
from pathlib import Path

SKILLS = Path(__file__).resolve().parents[1] / ".agents" / "skills"


def load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


u = load("understanding_index", SKILLS / "ai-understanding/scripts/understanding_index.py")
m = load("memory_index", SKILLS / "ai-memory-context/scripts/memory_index.py")

# Folder names covering every branch of the stamp logic: both shapes, both failure modes, and the
# one exempt folder.
RUNS = [
    "20261016-1432-ses_abc",           # current shape
    "20261016-1432-ses_20261016-1432-x",  # current shape, timestamp-leading id
    "subject-20261016-1432",            # legacy shape
    "20261016-ses_abc",                 # date-only: rejected, sorts oldest
    "20261016-1432",                    # stamp, no session
    "installer",                        # no stamp at all
    u.UNFILED,                          # the permanent catch-all
    "-20261016-1432",                   # stamp with nothing in front of it
]


def record(run: str, updated: str, path: str) -> dict:
    return {"run": run, "unit": {"updated": updated}, "path": path,
            "slug": Path(path).name, "subject": run, "category": ""}


class LayoutContractTests(unittest.TestCase):
    def test_stamp_parsing_agrees_on_every_folder_shape(self):
        for run in RUNS:
            with self.subTest(folder=run):
                self.assertEqual(u.stamp_of(run), m.stamp_of(run))

    def test_session_parsing_agrees(self):
        for run in RUNS:
            with self.subTest(folder=run):
                self.assertEqual(u.session_of(run), m.session_of(run))

    def test_an_unsanitised_session_id_cannot_nest_the_units_in_either(self):
        """A `/` in a session id lands the units one level deeper, the run folder never gets a valid
        stamp shape, and every unit drops out of the index silently."""
        for module in (u, m):
            with self.subTest(module=module.__name__):
                safe = module.safe_session_id("weird/id with spaces")
                self.assertNotIn("/", safe)
                self.assertNotIn(" ", safe)
                self.assertTrue(module.RUN_STAMP_RE.match(f"20261016-1432-{safe}"), safe)

    def test_two_ids_that_sanitise_alike_get_separate_folders_in_either(self):
        for module in (u, m):
            with self.subTest(module=module.__name__):
                self.assertNotEqual(module.safe_session_id("a/b"), module.safe_session_id("a_b"))

    def test_a_run_folder_name_is_current_shape_in_either(self):
        for module in (u, m):
            with self.subTest(module=module.__name__):
                name = module.run_folder_name("ses_abc")
                self.assertTrue(module.RUN_STAMP_RE.match(name), name)
                self.assertEqual("ses_abc", module.session_of(name))
                self.assertEqual(13, len(module.stamp_of(name)))

    def test_version_ordering_agrees_on_a_chain(self):
        """Newest stamp wins, an unstamped copy loses to any stamped one, and the tie-breaks after it
        are the same. This is the property LADR-002 depends on."""
        chain = [
            record("20261015-0900-ses_b", "2026-10-15", "20261015-0900-ses_b/c/x.memory.md"),
            record("20261016-1432-ses_a", "2026-10-16", "20261016-1432-ses_a/c/x.memory.md"),
            record(u.UNFILED, "2026-10-16", "_unfiled/x.memory.md"),
            record("20261016-1432-ses_z", "2026-10-16", "20261016-1432-ses_z/c/x.memory.md"),
        ]
        keyed_u = sorted(chain, key=u.version_key)
        keyed_m = sorted(chain, key=m.version_key)
        self.assertEqual([r["path"] for r in keyed_u], [r["path"] for r in keyed_m])
        self.assertEqual("_unfiled/x.memory.md", keyed_m[0]["path"],
                         "an unstamped folder sorts oldest in both")

    def test_group_versions_splits_the_same_way(self):
        chain = [
            record("20261015-0900-ses_b", "2026-10-15", "20261015-0900-ses_b/c/x.memory.md"),
            record("20261016-1432-ses_a", "2026-10-16", "20261016-1432-ses_a/c/x.memory.md"),
            record(u.UNFILED, "2026-10-16", "_unfiled/x.memory.md"),
        ]
        current_u, superseded_u = u.group_versions(chain)
        current_m, superseded_m = m.group_versions(chain)
        self.assertEqual([r["path"] for r in current_u], [r["path"] for r in current_m])
        self.assertEqual([r["path"] for r in superseded_u], [r["path"] for r in superseded_m])
        self.assertEqual(1, len(current_m))

    def test_the_shared_constants_agree(self):
        """Every genuinely shared constant must be present on **both** sides and equal.

        The previous version skipped a constant missing from either module, which read as deliberate
        — `ai-understanding` legitimately owns `PUBLISH_DIR_NAME` and this skill has no publish — but
        the `or` made it swallow a one-sided *deletion* too: remove `UNFILED` from one indexer and the
        parity check passes. A gate that cannot tell "intentionally absent" from "accidentally
        deleted" is not a gate.

        The one-sided case is therefore asserted rather than skipped: present on its owner, absent on
        the other. That is a stronger statement than the skip was — it fails both if the constant is
        deleted and if it is quietly copied across.
        """
        for name in ("UNFILED", "VALID_SCOPES", "VALID_CONFIDENCE", "REQUIRED_FIELDS",
                     "REQUIRED_PROVENANCE_FIELDS", "STALE_AFTER_DAYS", "STALE_AFTER_DAYS_OUTCOME",
                     "ASSETS_SUFFIX"):
            with self.subTest(constant=name):
                self.assertTrue(hasattr(u, name), f"{name} missing from ai-understanding")
                self.assertTrue(hasattr(m, name), f"{name} missing from ai-memory-context")
                self.assertEqual(getattr(u, name), getattr(m, name))

    def test_the_one_sided_constants_are_present_on_their_owner_and_absent_elsewhere(self):
        """Deliberately one-sided, asserted in both directions rather than skipped over."""
        for name, owner, absent in (
            ("PUBLISH_DIR_NAME", u, m),  # ai-understanding owns --publish; this skill has none
        ):
            with self.subTest(constant=name):
                self.assertTrue(hasattr(owner, name), f"{name} missing from its owner")
                self.assertFalse(hasattr(absent, name),
                                 f"{name} is documented as one-sided but has leaked into the "
                                 f"other indexer — re-check LADR-001 before allowing it")

    def test_the_session_id_ladder_agrees(self):
        """Both read the same environment names, so a file cannot be filed under one session by one
        skill and a different one by the other."""
        self.assertEqual(u.SESSION_ID_VARS, m.SESSION_ID_VARS)

    def test_the_two_indexers_still_differ_where_they_are_meant_to(self):
        """Guards against over-sharing: the unit postfix and the default store are what make this a
        second skill rather than a copy, and a future 'fix' that unifies them would silently merge two
        stores."""
        self.assertNotEqual(u.UNIT_SUFFIX, m.UNIT_SUFFIX)
        self.assertNotEqual(u.DEFAULT_STORE, m.DEFAULT_STORE)
        self.assertNotEqual(inspect.getsource(u.render), inspect.getsource(m.render))


if __name__ == "__main__":
    unittest.main()