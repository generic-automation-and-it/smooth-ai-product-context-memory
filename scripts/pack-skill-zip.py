#!/usr/bin/env python3
"""Pack an installer-owned skill into a zip that a desktop skill runtime will load.

Three things this gets right that a plain `zip -r` does not, each from a documented requirement
rather than a preference:

1. **The zip's root is the skill folder.** The claude.ai packaging guidance is explicit that the
   archive holds the skill directory *as its root*, not the directory's contents at the top level.
   `zip -r out.zip .agents/skills/<name>` produces the "incorrect structure" the same page warns
   about — files sitting directly in the zip root, with no folder to carry the `name`.

2. **The same exclusions the installer applies.** `tests/` and `__pycache__/` are pruned by
   `install-skills.sh`; a zip built straight from the checkout would carry them, which is both
   bulk and — for `ai-understanding`, whose tests reference `_mimisbrunnr-shared` by a `parents[2]`
   escape — a set of files that cannot work where they land. The exclusion list is read from the
   installer so the two cannot drift.

3. **The same 0600/0700 caveat is avoided by construction.** Zip stores no reliable Unix modes, so
   nothing here promises modes the archive will not keep.

Usage:
    python3 scripts/pack-skill-zip.py <skill> [--out DIR] [--check]

`--check` verifies the archive instead of writing it: root shape, exclusions held, and a self-
containment verdict from `check_skill_selfcontainment`. Read-only; creates nothing.
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
SKILLS = REPO / ".agents" / "skills"
INSTALLER = REPO / "scripts" / "install-skills.sh"
SELF_CONTAINMENT = REPO / "scripts" / "check_skill_selfcontainment.py"

# Kept in step with `prune_non_shippable` in the installer, and asserted against it at run time —
# two hand-maintained lists would drift silently, and the drift would only show up as a desktop
# install carrying a test suite.
FALLBACK_EXCLUDES = ("tests", "__pycache__")

# Links the packer declined to follow, reported on the way out. Module-level because `members`
# recurses through `build` and the caller needs the list afterwards.
MEMBERS_SKIPPED: list[str] = []


def installer_excludes() -> tuple[str, ...]:
    """The excluded directory names, read out of the installer itself."""
    try:
        text = INSTALLER.read_text(encoding="utf-8")
    except OSError:
        return FALLBACK_EXCLUDES
    body = text[text.index("prune_non_shippable() {"):]
    body = body[: body.index("\n}\n")]
    names = set(FALLBACK_EXCLUDES)
    for match in re.finditer(r'rm -rf "\$dest/([^"]+)"', body):
        names.add(match.group(1))
    for match in re.finditer(r'-name (\S+?)["\s]', body):
        names.add(match.group(1))
    return tuple(sorted(names))


def members(skill: Path, root: Path, excludes: tuple[str, ...]) -> list[tuple[Path, str]]:
    """Every file to pack, as (source path, arcname relative to the skill folder).

    A symlink whose target resolves **inside the repository** is materialised — the archive carries
    what it points at, so the zip is self-contained and imports into a desktop runtime, which has no
    repository to resolve a link against. This is the same rule the installer applies when copying,
    and for the same reason: a link is an authoring convenience, and shipping it would ship a pointer
    into a path the consumer does not have.

    A link pointing outside the repository, or one with no target, is **skipped and reported** rather
    than followed. Storing it would make the zip's extraction differ by platform, and following an
    outside link would pack a file the skill never referenced — for `/etc/hosts`, a system file.
    """
    out: list[tuple[Path, str]] = []
    skipped: list[str] = []
    for path in sorted(skill.rglob("*")):
        rel = path.relative_to(skill)
        if any(part in excludes for part in rel.parts):
            continue
        if path.is_symlink():
            target = resolve(path)
            if target is not None and is_inside(target, root):
                out.append((target, str(rel)))
            else:
                skipped.append(f"{rel} -> {os.readlink(path)}")
            continue
        if path.is_file():
            out.append((path, str(rel)))
    MEMBERS_SKIPPED.extend(skipped)
    return out


def resolve(link: Path) -> Path | None:
    """The canonical target of a link, or None when it does not resolve (dangling or a loop)."""
    try:
        return Path(os.path.realpath(link))
    except OSError:
        return None


def is_inside(path: Path, root: Path) -> bool:
    """True when `path` is under `root`, component-wise rather than as a text prefix."""
    try:
        return path.is_relative_to(root)
    except ValueError:
        return False


def build(skill: Path, out_dir: Path) -> Path:
    excludes = installer_excludes()
    archive = out_dir / f"{skill.name}.zip"
    out_dir.mkdir(parents=True, exist_ok=True)
    root = REPO.resolve()
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as zf:
        for path, rel in members(skill, root, excludes):
            # `arcname` keeps the skill folder as the single top-level entry.
            zf.write(path, arcname=str(Path(skill.name) / rel))
    return archive


def check(skill: Path, archive: Path) -> list[str]:
    """Problems with a packed archive. Empty list means it is fit to upload."""
    problems: list[str] = []
    excludes = installer_excludes()

    with zipfile.ZipFile(archive) as zf:
        names = zf.namelist()
        roots = {Path(n).parts[0] for n in names if Path(n).parts}
        if roots != {skill.name}:
            problems.append(
                f"the zip root is {sorted(roots)}, expected exactly ['{skill.name}'] — the archive "
                f"must carry the skill folder itself, not its contents"
            )
        for name in names:
            parts = Path(name).parts
            if any(part in excludes for part in parts):
                problems.append(f"{name}: carries an excluded directory ({', '.join(excludes)})")
            if Path(name).is_absolute() or ".." in parts:
                problems.append(f"{name}: escaping or absolute member")
        if f"{skill.name}/SKILL.md" not in names:
            problems.append(f"{skill.name}/SKILL.md is absent — nothing would ever trigger")
    return problems


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("skill", help="skill folder name under .agents/skills/")
    parser.add_argument("--out", default=".context/desktop-zips",
                        help="output directory (default: .context/desktop-zips)")
    parser.add_argument("--check", action="store_true",
                        help="verify the archive without writing one")
    args = parser.parse_args()

    skill = SKILLS / args.skill
    if not (skill / "SKILL.md").is_file():
        print(f"::error::no SKILL.md in {skill}", file=sys.stderr)
        return 2

    # Self-containment is settled BEFORE the archive is written. A packer that builds a broken zip
    # and then complains about it leaves the broken artifact on disk, and the next thing anyone does
    # with that directory is upload it.
    verdict = subprocess.run(
        [sys.executable, "-B", str(SELF_CONTAINMENT), "--zip", args.skill],
        cwd=REPO, capture_output=True, text=True,
    )
    if verdict.returncode != 0:
        print(f"::error::{args.skill} is not zippable alone — nothing was written")
        print("\n".join(f"  {line}" for line in verdict.stdout.splitlines()[1:]))
        print("  Either ship _mimisbrunnr-shared/ alongside it, or do not ship it to desktop.")
        return 1

    if args.check:
        archive = Path(args.out) / f"{args.skill}.zip"
        if not archive.is_file():
            print(f"::error::{archive} does not exist; build it first", file=sys.stderr)
            return 2
        problems = check(skill, archive)
        if problems:
            print(f"::error::{args.skill}.zip is not fit to upload")
            print("\n".join(f"  {p}" for p in problems))
            return 1
        print(f"{args.skill}.zip: fit to upload "
              f"(root={args.skill}, excludes={','.join(installer_excludes())})")
        return 0

    archive = build(skill, Path(args.out))
    problems = check(skill, archive)
    print(f"packed {archive.relative_to(REPO) if archive.is_relative_to(REPO) else archive}")
    for entry in MEMBERS_SKIPPED:
        print(f"  skipped link outside the repo (kept out of the archive): {entry}")
    if problems:
        print("::error::the archive does not pass its own checks")
        print("\n".join(f"  {p}" for p in problems))
        return 1
    print(f"  root shape ok · excludes {','.join(installer_excludes())} · self-contained")
    return 0


if __name__ == "__main__":
    sys.exit(main())