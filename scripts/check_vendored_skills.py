#!/usr/bin/env python3
"""HLD-010 NFR-05: installer-owned skills carry no reference into this repository's tree."""

import re
import sys
from pathlib import Path

SKILLS = Path(".agents/skills")
LINK = re.compile(r"\]\(([^)#\s]+)(?:#[^)]*)?\)")
RULE_PATH = re.compile(r"`(?:\.github/instructions|\.agents/rules)/[^`]*\.instructions\.md`")


def is_owned(name):
    """The installer's owned set. `scripts/install-skills.sh` `is_owned_skill` is the source of truth;
    `test_check_vendored_skills.py` holds the two to each other."""
    return name.startswith("mimisbrunnr-") or name in ("ai-understanding", "ai-brain-dump")


def is_vendored(name):
    """Owned skills, plus the shared script folder the installer refreshes beside them."""
    return name == "_mimisbrunnr-shared" or is_owned(name)


def violations(skills=SKILLS):
    skills = skills.resolve()
    found = []
    for folder in sorted(p for p in skills.iterdir() if p.is_dir() and is_vendored(p.name)):
        for doc in sorted([*folder.rglob("*.md"), *folder.rglob("*.yaml")]):
            for number, line in enumerate(doc.read_text(encoding="utf-8").splitlines(), 1):
                if line.lstrip().startswith("|"):
                    continue
                where = f"{doc.relative_to(skills)}:{number}"
                found += [f"{where}: link leaves .agents/skills: {target}"
                          for target in LINK.findall(line) if leaves_skills(doc, target, skills)]
                found += [f"{where}: repo-relative rule path: {rule}" for rule in RULE_PATH.findall(line)]
    return found


def leaves_skills(doc, target, skills):
    if re.match(r"[a-z]+:", target) or target.startswith("/"):
        return False
    return not (doc.parent / target).resolve().is_relative_to(skills)


def main():
    found = violations()
    if found:
        print("::error::HLD-010 NFR-05 — an installer-owned skill references this repository's tree")
        print("\n".join(found))
        return 1
    print("vendored skills are self-contained (HLD-010 NFR-05)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
