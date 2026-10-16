#!/usr/bin/env python3
"""Which installer-owned skills can ship as a zip on their own, and which cannot.

`check_vendored_skills.py` answers a different question — HLD-010 NFR-05, "does this skill reach
into *this repository's* tree" — and it cannot answer this one for two structural reasons: it reads
only `*.md` and `*.yaml`, so it never sees a Python path resolution, and it skips table rows
(`if line.lstrip().startswith("|"): continue`), which is precisely where odin documents its shared
script calls. Both omissions are load-bearing here.

So this gate covers **both** ways a skill acquires a dependency it cannot carry alone:

1. **Runtime resolution.** A `.py` under the skill (excluding `tests/`, which are in-repo checks and
   legitimately compare against the original) resolving `Path(__file__).resolve().parents[N]` outside
   the skill folder.
2. **Documented invocation.** An interpreter command in a skill's prose that names a path escaping the
   skill folder. This is the mechanism that actually bites: odin has *no* shared import in its own
   scripts, and is still broken alone, because its SKILL.md tells the agent to shell out to a sibling.

A dependency is not a defect — the `mimisbrunnr-*` skills are built on the shared folder on purpose.
The defect is an *undeclared* one, so the shared-dependent skills are named in
`SHARED_DEPENDENT` and adding to that set is the deliberate act. A skill outside it that acquires an
escape fails, which is what makes "zip this alone" a mechanical question rather than a judgement call.

Usage:
    python3 scripts/check_skill_selfcontainment.py            # gate: fail on an undeclared escape
    python3 scripts/check_skill_selfcontainment.py --report   # the full table, always exit 0
    python3 scripts/check_skill_selfcontainment.py --zip ai-understanding

Read-only, standard library only, no network. A gate that repairs what it inspects is not a gate, so
this writes nothing at all.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

SKILLS = Path(".agents/skills")
SHARED = "_mimisbrunnr-shared"

# The installer's owned set, mirrored from `is_owned_skill` in scripts/install-skills.sh and
# `Test-OwnedSkill` in install-skills.ps1. `test_check_skill_selfcontainment.py` runs all three
# against each other, so a fourth copy cannot drift unnoticed.
OWNED = ("mimisbrunnr-", "ai-understanding", "ai-brain-dump")

# The skills that depend on the shared folder by design. Verified against the tree, not assumed:
#   odin        SKILL.md:344-352 documents seven `python3 .../_mimisbrunnr-shared/scripts/...` calls;
#               its own scripts (authority.py, divergence.py) have no shared reference at all.
#   kvasir      scripts/understanding_client.py:55 resolves `parents[2] / _mimisbrunnr-shared / scripts`.
#   saga        scripts/dossier_composer.py:53 does the same.
#   ymir        SKILL.md:23 shells to `../_mimisbrunnr-shared/scripts/context_memory_read_client.py`;
#               it ships no scripts/ of its own.
#   heimdallr   SKILL.md:19 shells to the shared find_session_metadata.py; it ships no scripts/ of
#               its own either, so it is wholly dependent rather than merely degraded.
# `mimisbrunnr-muninn-recall-feedback` is deliberately absent: its only script is recall_feedback.sh,
# which references nothing outside itself.
SHARED_DEPENDENT = (
    "mimisbrunnr-heimdallr-find-session-metadata",
    "mimisbrunnr-kvasir-understanding",
    "mimisbrunnr-odin-context-memory",
    "mimisbrunnr-saga-dossier",
    "mimisbrunnr-ymir-bootstrap",
)

# `Path(__file__).resolve().parents[N]` — the resolution form these scripts use. Matched on the
# expression rather than on a literal, so a script that builds the path differently still trips it.
PY_ESCAPE = re.compile(r"parents\s*\[\s*(\d+)\s*\]")
# Characters of context on each side of a `parents[N]` when deciding what it reaches. Sized for a
# wrapped path expression, not for a window: it only has to span the subscript and the segment that
# names the target, and every target here is a sibling folder named within one expression.
WINDOW = 200
# A path segment that leaves the skill: an explicit `../`, or a named sibling folder. `.../` (the
# elision these SKILL.md files use for "somewhere above me") deliberately does not match — it names
# no resolvable location, so it cannot be the thing that breaks.
ESCAPES = re.compile(r"(?:^|[\s\"'`(=/])(\.\./|_mimisbrunnr-shared)")
# An interpreter invocation. The pair (interpreter, escaping path) on one line is the signal; either
# half alone is not — prose that merely names the shared folder as the provenance of a copied file is
# documentation, not a dependency.
INTERPRETER = re.compile(r"\b(?:python3?|node|bash|sh)\b")


def is_owned(name: str) -> bool:
    return name.startswith(OWNED[0]) or name in OWNED[1:]


def is_shared_dependent(name: str) -> bool:
    return name in SHARED_DEPENDENT


def runtime_python(folder: Path) -> list[Path]:
    """A skill's own `.py` files, minus `tests/`.

    Excluded because a harness that pins a WET copy against the shared original has to name the
    original; that is a repository check, not something that ships. A *runtime* escape is the defect.
    """
    return [p for p in sorted(folder.rglob("*.py")) if "tests" not in p.relative_to(folder).parts]


def python_escapes(folder: Path) -> list[tuple[str, int, str]]:
    """Runtime escapes, found by scanning the file text rather than line by line.

    Scanning per line was the first version and it was wrong in the cheapest possible way: it
    reported `mimisbrunnr-saga-dossier` as self-contained, because its resolution wraps across two
    lines —

        _SHARED_SCRIPTS = (Path(__file__).resolve().parents[2]
                           / "_mimisbrunnr-shared" / "scripts")

    — so the `parents[2]` is on one line and the folder it reaches is named on the next, and neither
    line contains both. A line-oriented gate is a gate with a silent hole exactly where a formatter
    has wrapped something, which is the normal state of the file rather than the exception.
    """
    found = []
    for script in runtime_python(folder):
        text = script.read_text(encoding="utf-8")
        rel = script.relative_to(folder.parent)
        for match in PY_ESCAPE.finditer(text):
            # What matters is what the resolution reaches, and that can sit either side of the
            # subscript once the expression is wrapped.
            window = text[max(0, match.start() - WINDOW) : match.end() + WINDOW]
            if not ESCAPES.search(window):
                continue
            number = text.count("\n", 0, match.start()) + 1
            found.append((f"{rel}:{number}", number, "runtime path resolution leaves the skill"))
    return found


def prose_escapes(folder: Path) -> list[tuple[str, int, str]]:
    found = []
    for doc in sorted([*folder.rglob("*.md"), *folder.rglob("*.yaml")]):
        for number, line in enumerate(doc.read_text(encoding="utf-8").splitlines(), 1):
            if not INTERPRETER.search(line):
                continue
            if ESCAPES.search(line):
                rel = doc.relative_to(folder.parent)
                found.append((f"{rel}:{number}", number, "documented command leaves the skill"))
    return found


def escapes_for(folder: Path) -> list[tuple[str, int, str]]:
    return python_escapes(folder) + prose_escapes(folder)


def owned_skills(skills: Path = SKILLS) -> list[Path]:
    return [p for p in sorted(skills.iterdir()) if p.is_dir() and is_owned(p.name)]


def violations(skills: Path = SKILLS) -> list[str]:
    """Escapes in skills that are not declared shared-dependent."""
    found = []
    for folder in owned_skills(skills):
        escapes = escapes_for(folder)
        if not escapes:
            continue
        if is_shared_dependent(folder.name):
            continue
        found += [
            f"{where}: {why} — '{folder.name}' is not in SHARED_DEPENDENT"
            for where, _, why in escapes
        ]
    return found


def declaration_problems(skills: Path = SKILLS) -> list[str]:
    """Ways `SHARED_DEPENDENT` can stop describing the tree.

    Kept apart from `violations` because it is a claim about the declaration *as a whole*, which is
    only meaningful against the real skills root. Run against a synthetic tree it would report every
    named skill as missing, which is noise rather than a finding.
    """
    found = []
    for name in SHARED_DEPENDENT:
        folder = skills / name
        if not folder.is_dir():
            found.append(f"SHARED_DEPENDENT names '{name}', which is not a skill folder")
        elif not escapes_for(folder):
            # The declaration must describe the tree, not the other way round: a declared consumer
            # that no longer escapes has quietly become zippable, and leaving it declared keeps the
            # next reviewer reading a false row out of the table.
            found.append(
                f"SHARED_DEPENDENT names '{name}', which no longer escapes — declare it self-contained"
            )
    return found


def rows(skills: Path = SKILLS) -> list[tuple[str, str, int]]:
    out = []
    for folder in owned_skills(skills):
        escapes = escapes_for(folder)
        out.append(
            (
                folder.name,
                "shared-dependent" if is_shared_dependent(folder.name) else "self-contained",
                len(escapes),
            )
        )
    return out


def main() -> int:
    args = set(sys.argv[1:])
    if "--zip" in args:
        name = sys.argv[sys.argv.index("--zip") + 1]
        folder = SKILLS / name
        if not folder.is_dir():
            print(f"::error::no such skill folder: {name}")
            return 1
        escapes = escapes_for(folder)
        if escapes or is_shared_dependent(name):
            print(f"{name}: NOT zippable alone — needs {SHARED}/ alongside it")
            for where, _, why in escapes:
                print(f"  {where}: {why}")
            return 1
        print(f"{name}: zippable alone")
        return 0

    if "--report" in args:
        print(f"{'skill':52} {'verdict':17} escapes")
        for name, verdict, count in rows():
            print(f"{name:52} {verdict:17} {count}")
        return 0

    found = violations() + declaration_problems()
    if found:
        print("::error::an installer-owned skill has an undeclared dependency outside itself")
        print(
            "Either the skill is genuinely shared-dependent (declare it in SHARED_DEPENDENT) or the "
            "escape should not be there. Nothing outside .agents/skills/ travels with a zip."
        )
        print("\n".join(found))
        return 1
    print("skill self-containment is declared and honest")
    return 0


if __name__ == "__main__":
    sys.exit(main())