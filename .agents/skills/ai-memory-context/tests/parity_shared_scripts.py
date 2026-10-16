#!/usr/bin/env python3
"""The three vendored scripts are byte-identical copies of the shared originals.

Not a refactor guard and not a style check: these files are duplicated so the skill runs where no
`mimisbrunnr-*` is installed and ships as one zip. The duplication is only safe while the two sides
are equal, and the copy direction is shared folder -> skill, so drift fails here naming the side that
changed. Editing one and not the other is the single failure mode this file exists for.
"""

import sys
import unittest
import re
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1] / "scripts"
SHARED = Path(__file__).resolve().parents[2] / "_mimisbrunnr-shared" / "scripts"
VENDORED = ("redact.py", "atomicity.py", "session_marker.py")


ESCAPE = re.compile(
    # Climbing out of the script's own directory, anchored on `__file__`. A bare `.parent`
    # is also how any Path is walked (`mpath.parent.mkdir(...)` in the marker is ordinary
    # code), so matching it unqualified flagged the real vendored scripts and would have
    # trained the reader to ignore this check.
    #
    # `__file__` and `.parent` are required on the *same line*, with anything between them,
    # rather than adjacent. The natural spelling puts a call in the way:
    # `Path(__file__).resolve().parent.parent` is the same escape as
    # `Path(__file__).parent.parent` with one more hop that no filesystem question changes,
    # and an earlier version demanding adjacency caught the hand-written form while missing
    # the one `resolve()` produces -- the exact form the review named. A gate that only
    # catches the spelling its author typed is not a gate.
    #
    # The wider match costs one false positive shape: a line that mentions `__file__` and
    # some unrelated `.parent` together. That reads as noise on a harness helper rather than
    # a hole in a shipped script, which is the right way round for a check whose failure is
    # "someone looks at this".
    r"__file__[^\n]*?\.parent\b"
    r"|parents\s*\[\s*[2-9]\s*\]"
    # Any mutation of the import path, whatever it is called. `sys.path.append` is the same
    # escape as `sys.path.insert` with a different method name.
    r"|sys\.path\s*\.\s*\w+"
        )

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

        for name in VENDORED:
            with self.subTest(script=name):
                body = (SCRIPTS / name).read_text(encoding="utf-8")
                self.assertIsNone(ESCAPE.search(body),
                                  f"{name} resolves outside the skill — it must be self-contained")

    def test_the_escape_pattern_catches_every_spelling_of_the_same_climb(self):
        """Finding 2. The escape check ran only against real files, so it proved that today's scripts
        are clean and never that the pattern recognises an escape at all.

        That is how `Path(__file__).resolve().parent.parent` got through: the shipped scripts write
        `.parent` directly, so the hand-written spelling was the only one the suite ever produced --
        and the pattern had been written to match exactly what the suite contained. A gate checked
        only by its own fixtures cannot notice the fixture is narrower than the thing it guards.
        """
        for code in ("sys.path.insert(0, p)", "sys.path.append(p)", "sys.path.extend([p])",
                     "Path(__file__).parent", "Path(__file__).parent.parent",
                     "Path(__file__).resolve().parent", "Path(__file__).resolve().parent.parent",
                     "Path(__file__).parents[2]", "Path(__file__).absolute().parents[1].parent"):
            with self.subTest(escape=code):
                self.assertIsNotNone(ESCAPE.search(code), f"escape not caught: {code}")

        # The other half, and the reason the pattern is anchored on `__file__` rather than on
        # `.parent`: ordinary code walks a Path constantly, and a pattern that flags that is a
        # pattern whose findings get ignored.
        for code in ("mpath.parent.mkdir(parents=True, exist_ok=True)",
                     "os.chmod(mpath.parent, 0o700)",
                     "x = 1"):
            with self.subTest(benign=code):
                self.assertIsNone(ESCAPE.search(code), f"false positive: {code}")


if __name__ == "__main__":
    unittest.main()
