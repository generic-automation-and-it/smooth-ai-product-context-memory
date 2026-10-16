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
        """The reason the copies exist. Any path expression that climbs out of the skill folder, or a
        `sys.path` mutation of any kind, would reintroduce the exact dependency that makes a zipped
        copy of this skill broken.

        Broader than it looks, deliberately. `sys.path.insert` alone missed `sys.path.append` — which
        is the same escape with a different method name — and missed a `.parent` / `.parent.parent`
        chain built without the `parents[N]` subscript. All three reintroduce the dependency while the
        suite stays green, which is the one failure mode a parity gate exists to prevent.
        """
        import re

        escape = re.compile(
            # Climbing out of the script's own directory. Anchored on `__file__` on purpose: a bare
            # `.parent` is also how any Path is walked (`mpath.parent.mkdir(...)` in the marker is
            # ordinary code), so matching it unqualified flagged the real vendored scripts and would
            # have trained the reader to ignore this check.
            r"__file__\)?\s*\.\s*parent"
            r"|parents\s*\[\s*[2-9]\s*\]"
            # Any mutation of the import path, whatever it is called. `sys.path.append` is the same
            # escape as `sys.path.insert` with a different method name.
            r"|sys\.path\s*\.\s*\w+"
        )
        for name in VENDORED:
            with self.subTest(script=name):
                body = (SCRIPTS / name).read_text(encoding="utf-8")
                self.assertIsNone(escape.search(body),
                                  f"{name} resolves outside the skill — it must be self-contained")


if __name__ == "__main__":
    unittest.main()
