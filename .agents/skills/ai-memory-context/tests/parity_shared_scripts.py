#!/usr/bin/env python3
"""The three vendored scripts are byte-identical copies of the shared originals.

Not a refactor guard and not a style check: these files are duplicated so the skill runs where no
`mimisbrunnr-*` is installed and ships as one zip. The duplication is only safe while the two sides
are equal, and the copy direction is shared folder -> skill, so drift fails here naming the side that
changed. Editing one and not the other is the single failure mode this file exists for.
"""

import sys
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1] / "scripts"
SHARED = Path(__file__).resolve().parents[2] / "_mimisbrunnr-shared" / "scripts"
VENDORED = ("redact.py", "atomicity.py", "session_marker.py")


class ParityTests(unittest.TestCase):
    def test_each_vendored_script_matches_the_shared_original(self):
        for name in VENDORED:
            with self.subTest(script=name):
                self.assertEqual(
                    (SHARED / name).read_text(encoding="utf-8"),
                    (SCRIPTS / name).read_text(encoding="utf-8"),
                    f"{name} has drifted from _mimisbrunnr-shared/scripts/{name} — this is a WET copy, "
                    f"so both sides change together",
                )

    def test_no_vendored_script_reaches_outside_the_skill(self):
        """The reason the copies exist. A `parents[2]` or a `sys.path` insert would reintroduce the
        exact dependency that makes a zipped copy of this skill broken."""
        import re

        escape = re.compile(r"parents\s*\[\s*[2-9]\s*\]|sys\.path\.insert")
        for name in VENDORED:
            with self.subTest(script=name):
                body = (SCRIPTS / name).read_text(encoding="utf-8")
                self.assertIsNone(escape.search(body),
                                  f"{name} resolves outside the skill — it must be self-contained")


if __name__ == "__main__":
    unittest.main()
