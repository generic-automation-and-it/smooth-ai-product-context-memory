#!/usr/bin/env python3
"""Committed L0 harness for the ai-understanding index generator. stdlib unittest; no external runner.

Run: python3 -B .agents/skills/ai-understanding/tests/run_tests.py

Covers the durability guard: a gitignored store that holds units newer than the newest published
archive is warned about, and the store's gitignore status — not a text search — decides whether any
warning is owed. The guard is advisory: no reported unit changes the exit code.
"""

from __future__ import annotations

import io
import subprocess
import sys
import tempfile
import unittest
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


def make_repo(tmp: str, *, ignore_store: bool) -> Path:
    """A throwaway git repo with `.context/understandings` ignored or tracked."""
    subprocess.run(["git", "init", "-q", tmp], check=True, capture_output=True)
    ignore = ".context/\n" if ignore_store else ""
    (Path(tmp) / ".gitignore").write_text(ignore, encoding="utf-8")
    return Path(tmp)


def write_unit(store: Path, subject: str, slug: str, updated: str = "2026-09-30") -> None:
    folder = store / subject
    folder.mkdir(parents=True, exist_ok=True)
    (folder / f"{slug}{ui.UNIT_SUFFIX}").write_text(
        UNIT_FRONTMATTER.format(slug=slug, updated=updated), encoding="utf-8"
    )


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
            pub = store.parent / ui.PUBLISH_DIR_NAME
            pub.mkdir(parents=True, exist_ok=True)
            (pub / "understandings-20260930-180000.zip").write_bytes(b"x")
            write_unit(store, "proj-20260930-1900", "new")
            rc, out, _ = run(argv(store, review=True))
            self.assertEqual(rc, 0)
            self.assertIn("proj-20260930-1900", out)
            self.assertNotIn("proj-20260930-1700", out)

    def test_unit_exported_before_publish_in_same_ten_minutes_is_not_flagged(self):
        """A folder stamped 18:03 is captured by an archive stamped 18:05:30. Comparing on a
        truncated ten-minute prefix flagged it; the archive must compare at minute precision."""
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=True)
            store = repo / ".context" / "understandings"
            write_unit(store, "proj-20260930-1803", "captured")
            write_unit(store, "proj-20260930-1805", "same-minute")
            write_unit(store, "proj-20260930-1806", "later")
            pub = store.parent / ui.PUBLISH_DIR_NAME
            pub.mkdir(parents=True, exist_ok=True)
            (pub / "understandings-20260930-180530.zip").write_bytes(b"x")
            flagged = {u["folder"] for u in ui.unpublished_units(ui.load_units(store)[0], store)}
            self.assertEqual(flagged, {"later"})

    def test_unfiled_unit_is_flagged_even_after_a_publish(self):
        """An unstamped `_unfiled` unit has no time to compare against the archive, so it cannot be
        proven captured; it is reported rather than silently treated as safe."""
        with tempfile.TemporaryDirectory() as tmp:
            repo = make_repo(tmp, ignore_store=True)
            store = repo / ".context" / "understandings"
            write_unit(store, "proj-20260930-1700", "old")
            write_unit(store, ui.UNFILED, "loose")
            pub = store.parent / ui.PUBLISH_DIR_NAME
            pub.mkdir(parents=True, exist_ok=True)
            (pub / "understandings-20260930-180000.zip").write_bytes(b"x")
            flagged = {u["folder"] for u in ui.unpublished_units(ui.load_units(store)[0], store)}
            self.assertEqual(flagged, {"loose"})

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
