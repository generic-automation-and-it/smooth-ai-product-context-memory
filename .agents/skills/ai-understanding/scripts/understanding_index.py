#!/usr/bin/env python3
"""Regenerate the Understandings INDEX.md from the subject/slug folders.

The store is `<subject>-<yyyyMMdd-HHmm>/<slug>.understanding.md`. A stamped folder is one export run;
a slug identifies the knowledge, not one copy of it, so the **same slug in several stamped folders is
a version chain** rather than an error (LADR-010). The newest stamp is the *current* version and the
rest are superseded history. `_unfiled/` is the one exempt, unstamped folder, and sorts oldest.
The index groups by subject for browsing but lists every current unit, because knowledge is
retrieved by the question it answers rather than by the subject that happened to produce it.

**Validation applies to current versions only.** Superseded copies are immutable history: validating
them would force edits to the past, and a `[[slug]]` pruned later would leave an old file permanently
invalid. Folder-level problems (an unstamped or empty subject folder, a stray unit at the store root,
the old per-unit folder shape, a unit file missing the `.understanding.md` postfix) are reported
regardless, since they belong to no single version.

A unit needing evidence artifacts (a repro, a log excerpt, a diagram) puts them in a
sibling `<slug>.assets/` directory, which is named after the slug and so cannot collide.

The index is a reference table, never a copy of the knowledge: an agent reads it to decide
which Understandings to load, then reads only those folders.

Usage:
    python3 .agents/skills/ai-understanding/scripts/understanding_index.py [store-dir]
    python3 .agents/skills/ai-understanding/scripts/understanding_index.py --consume-check <zip> [store-dir]
    python3 .agents/skills/ai-understanding/scripts/understanding_index.py --stamp

Defaults to `.context/understandings`. Pure standard library plus `git check-ignore` for the
durability warning, read/write only to the store, no network.
Exits 1 when a current unit fails validation — the index is still written so the drift is visible.
"""

from __future__ import annotations

import os
import re
import subprocess
import sys
import tempfile
import zipfile
from datetime import date, datetime, timezone
from pathlib import Path, PurePosixPath

DEFAULT_STORE = Path(".context/understandings")
ASSETS_SUFFIX = ".assets"
# A unit file is `<slug>.understanding.md`: the slug, then a type postfix mirroring the
# `.instructions.md` one rule files carry. The postfix is what lets a path glob select
# Understandings and nothing else -- `INDEX.md` and any stray note deliberately fall outside it.
UNIT_SUFFIX = ".understanding.md"
UNIT_GLOB = f"*{UNIT_SUFFIX}"
LEGACY_UNIT_FILENAME = "UNDERSTANDING.md"
REQUIRED_FIELDS = ("slug", "description", "scope", "confidence")
# `question` is optional: knowledge units carry one, outcome units match on `description` alone.
# `recheck` is optional too — one command that re-checks the unit, printed by `--review` beside a
# staleness flag. Both are validated only for a left-in placeholder; absence is legitimate.
OPTIONAL_TEXT_FIELDS = ("question", "recheck")
VALID_SCOPES = ("portable", "repo-specific")
VALID_CONFIDENCE = ("observed", "verified", "contested")
UNFILED = "_unfiled"
# A subject folder is stamped with when it was created: <subject>-yyyyMMdd-HHmm, 24-hour, UTC —
# stamps from different machines must compare, so the zone is fixed rather than local. `_unfiled` is
# the one permanent catch-all and is exempt. This is a shape check, not a calendar — it accepts an
# out-of-range date/time, but not a short or long field.
SUBJECT_STAMP_RE = re.compile(r"^.+-\d{8}-\d{4}$")
# A suffix that is trying to be a stamp but has the wrong field widths, or a stamp with no subject
# in front of it. Each gets a remedy that names the actual fix instead of suggesting a second stamp.
MALFORMED_STAMP_RE = re.compile(r"^(.+)-\d+-\d+$")
STAMP_ONLY_RE = re.compile(r"^-\d+-\d+$")
# A YAML block-scalar header (`>-`, `|`, `|2-`): this parser keeps the header as the value and drops
# the indented text under it. Every field that could tempt one is specified as a single line, so the
# shape is named as a problem rather than parsed.
BLOCK_SCALAR_RE = re.compile(r"^[|>][0-9+-]{0,2}$")
# A unit past this many days without an update is worth re-reading. Outcome units — the ones
# carrying no `question`, which record what a piece of work produced — decay faster than knowledge.
STALE_AFTER_DAYS = 90
STALE_AFTER_DAYS_OUTCOME = 30
# The publish target sits beside the store (`.context/understandings-publish`), the one durable form
# of the store (LADR-008). An archive mirrors the store's `<subject>-<stamp>/<slug>.understanding.md`
# paths, so a unit is published exactly when its path is a member of one.
PUBLISH_DIR_NAME = "understandings-publish"
# The store's parent directory in the default layout. Its own name is what tells `context_root` that
# the grandparent is the repo root, so an `agents_context` path resolves the same from any CWD.
CONTEXT_DIR_NAME = ".context"


def parse_frontmatter(text: str) -> dict:
    """Parse this format: flat scalars, lists, a nested map, and a list inside that map.

    Deep enough for `provenance.inherited`; anything deeper needs extending here, not just
    the template.
    """
    lines = text.splitlines()
    if not lines or lines[0].strip() != "---":
        return {}

    data: dict = {}
    parent: str | None = None   # top-level key holding a map or list
    child: str | None = None    # key inside that map holding a list

    for line in lines[1:]:
        if line.strip() == "---":
            break
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue

        if not line[:1].isspace():
            key, _, value = stripped.partition(":")
            if not _:
                continue
            key, value = key.strip(), unquote(value)
            child = None
            if value:
                data[key] = value
                parent = None
            else:
                data[key] = {}
                parent = key
            continue

        if parent is None:
            continue

        if stripped.startswith("- "):
            item = unquote(stripped[2:])
            if child is not None:
                data[parent].setdefault(child, []).append(item)
            else:
                if not isinstance(data.get(parent), list):
                    data[parent] = []
                data[parent].append(item)
            continue

        key, _, value = stripped.partition(":")
        if not _:
            continue
        key, value = key.strip(), unquote(value)
        if not isinstance(data.get(parent), dict):
            data[parent] = {}
        if value:
            data[parent][key] = value
            child = None
        else:
            data[parent][key] = []
            child = key

    return data


def unquote(value: str) -> str:
    """A YAML scalar written in quotes means its content; this parser would otherwise keep the quotes.

    Matters most for `"[[slug]]"` entries: quoted, they never started with `[[`, so the reference walk
    skipped them and the lineage reader did not count them — a dangling link exited 0 and an inherited
    unit still reported as never inherited.
    """
    value = value.strip()
    if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
        return value[1:-1].strip()
    return value


def placeholder(value: object) -> bool:
    """Total over the parsed frontmatter, not just over strings.

    Every value here comes from a file a human hand-edited, so a field the schema wants as a
    scalar arrives as a block list often enough to matter. A validator that raises on malformed
    input reports nothing about the file it was handed, which is the one job it has.
    """
    return isinstance(value, str) and value.startswith("<") and value.endswith(">")


def slug_of(unit_file: Path) -> str:
    """The slug a unit file addresses: its name without the `.understanding.md` postfix.

    Not `Path.stem`, which would keep the `.understanding` half and break every `[[slug]]` link.
    """
    return unit_file.name[: -len(UNIT_SUFFIX)]


def context_root(store: Path) -> Path:
    """The directory an `agents_context` path is written relative to.

    `agents_context` holds a repo-relative path, so it can only be resolved against the repository the
    store belongs to — not against whatever directory the generator happened to be invoked from.
    Checking it against the process CWD reported every unit's context missing when the script ran with
    an absolute store path from elsewhere, which is the documented `[store-dir]` form: 55 false problems
    and exit 1 on a clean store.

    The store sits at `<repo>/.context/understandings`, so the repo root is two levels up. Anything
    deeper is not this layout and falls back to the store's own parent, which is what a bespoke store
    directory outside a repo gets.
    """
    context_dir = store.parent
    if context_dir.name == CONTEXT_DIR_NAME and context_dir.parent != context_dir:
        return context_dir.parent
    return context_dir


def read_unit(unit_file: Path, subject: str, root: Path | None = None) -> tuple[dict | None, list[str]]:
    """Load and validate one `<subject>-<yyyyMMdd-HHmm>/<slug>.understanding.md`.

    `root` is the directory `agents_context` resolves against; it defaults to the process CWD, which is
    correct only when the caller had none better to offer.
    """
    slug = slug_of(unit_file)
    where = f"{subject}/{unit_file.name}"

    fields = parse_frontmatter(unit_file.read_text(encoding="utf-8"))
    if not fields:
        return None, [f"{where} has no frontmatter"]

    problems = []
    for field in REQUIRED_FIELDS:
        value = fields.get(field)
        if not isinstance(value, str) or not value or placeholder(value):
            problems.append(f"{where}: '{field}' is missing or still a placeholder")

    for field in OPTIONAL_TEXT_FIELDS:
        value = fields.get(field)
        if value is not None and (not isinstance(value, str) or not value or placeholder(value)):
            problems.append(f"{where}: '{field}' is present but empty or still a placeholder")

    updated = fields.get("updated")
    if not isinstance(updated, str) or not updated or placeholder(updated):
        problems.append(f"{where}: 'updated' is missing or still a placeholder")

    provenance = fields.get("provenance")
    if not isinstance(provenance, dict) or not provenance:
        problems.append(f"{where}: 'provenance' is missing")
    else:
        for key in ("learned", "session", "source"):
            value = provenance.get(key)
            if not isinstance(value, str) or not value or placeholder(value):
                problems.append(f"{where}: 'provenance.{key}' is missing or still a placeholder")

        inherited = provenance.get("inherited")
        if isinstance(inherited, list):
            for entry in inherited:
                if placeholder(str(entry).strip()):
                    problems.append(
                        f"{where}: 'provenance.inherited' still holds a template placeholder — "
                        "name what this session inherited, or omit the list"
                    )
        elif inherited is not None and not looks_like_flow_sequence(inherited):
            # A scalar never reaches the lineage reader, so a placeholder or bare name written on one
            # line would pass silently. A flow sequence is excluded only because `inline_sequences`
            # already reports it with the block-list remedy.
            problems.append(
                f"{where}: 'provenance.inherited' must be a block list of [[slug]] entries — "
                "rewrite it as one, or omit it"
            )

    context = fields.get("agents_context")
    if isinstance(context, str) and context and not placeholder(context):
        # Resolved against the repo root, not the CWD: the path is repo-relative by construction, so a
        # generator run from any other directory must reach the same verdict as one run from the root.
        base = root if root is not None else Path.cwd()
        if not (base / context).exists():
            problems.append(
                f"{where}: agents_context '{context}' does not exist under {base}"
            )

    if fields.get("slug") not in (slug, None):
        problems.append(f"{where}: slug '{fields['slug']}' does not match the file name")
    if isinstance(fields.get("scope"), str) and fields["scope"] not in VALID_SCOPES:
        problems.append(f"{where}: scope '{fields['scope']}' is not one of {VALID_SCOPES}")
    if isinstance(fields.get("confidence"), str) and fields["confidence"] not in VALID_CONFIDENCE:
        problems.append(f"{where}: confidence '{fields['confidence']}' is not one of {VALID_CONFIDENCE}")

    problems.extend(inline_sequences(fields, where))
    problems.extend(block_scalars(fields, where))

    fields["folder"] = slug
    fields["subject"] = subject
    fields["path"] = f"{subject}/{unit_file.name}"
    return fields, problems


def looks_like_flow_sequence(value) -> bool:
    """True for a YAML flow sequence this parser reads as a scalar: fully `[...]`-bracketed.

    Requiring the closing bracket is what keeps prose out. A plain scalar cannot legally start
    with `[` in YAML, but this parser is lenient, and a description reading
    `[draft] how the thing behaves` is prose the writer meant — not a sequence.
    """
    return isinstance(value, str) and value.startswith("[") and value.endswith("]")


def iter_scalar_fields(fields: dict):
    """Yield every (field-path, string value) the parser produced, at both levels it supports.

    Shared by the two shape checks below. Both walk identically and both exist because this parser
    stores a value it could not really read as a plain string — so a new misread shape is a new
    predicate here, not a new traversal.
    """
    for key, value in fields.items():
        if key in ("folder", "subject", "path"):
            continue
        if isinstance(value, str):
            yield key, value
        elif isinstance(value, dict):
            for sub_key, sub_value in value.items():
                if isinstance(sub_value, str):
                    yield f"{key}.{sub_key}", sub_value


def block_scalars(fields: dict, where: str) -> list[str]:
    """Catch a value written as a YAML block scalar, which this parser reads as its header.

    `description: >-` followed by an indented paragraph stores the two characters `>-` and silently
    discards the text — the index then shows `>-` and exits 0. It is the natural way to write a long
    description, so it is named rather than left to be discovered in the rendered index.
    """
    return [
        f"{where}: '{field}' is written as a YAML block scalar — this parser keeps the '{value}' header "
        f"and drops the indented text below it; put the value on one line after the colon"
        for field, value in iter_scalar_fields(fields)
        if BLOCK_SCALAR_RE.match(value)
    ]


def inline_sequences(fields: dict, where: str) -> list[str]:
    """Catch a list written in YAML flow style, which this parser reads as a plain scalar.

    `links: [[a]]` is valid YAML and looks right, but it never becomes a list, so
    `dangling_references` never walks it and an unresolvable entry exits 0. Making the parser
    read flow sequences would have to guess where `[[a]]` is one reference and where it is a
    nested sequence; naming the shape is unambiguous and the block form is what the template uses.

    Checks both levels the parser supports. A flow sequence nested in a map — `provenance.inherited`
    being the one that exists — is the same defect and was missed when only the top level was walked.
    """
    return [
        _flow_problem(where, field)
        for field, value in iter_scalar_fields(fields)
        if looks_like_flow_sequence(value)
    ]


def _flow_problem(where: str, field: str) -> str:
    return (
        f"{where}: '{field}' is written as an inline list — this parser reads block lists only, so "
        f"its [[slug]] entries are never checked; rewrite it as a block list"
    )


def load_units(store: Path) -> tuple[list[dict], list[dict], list[str]]:
    """Read every copy in the store and return (current units, superseded units, problems).

    Problems come from current versions and from the folder layout. A superseded copy's own
    validation is skipped — see the module docstring for why history is exempt.
    """
    # `agents_context` is repo-relative, so it resolves against the repo the store belongs to rather
    # than the directory this script was invoked from — see `context_root`.
    root = context_root(store)
    records: list[dict] = []
    problems: list[str] = []

    for stray in sorted(store.glob("*.md")):
        if stray.name == "INDEX.md":
            continue
        # One message, not two: a stray that also lacks the postfix needs both fixes in one move.
        target = stray.name if stray.name.endswith(UNIT_SUFFIX) else f"{stray.stem}{UNIT_SUFFIX}"
        problems.append(
            f"{stray.name} sits at the store root — move it to <subject>-<yyyyMMdd-HHmm>/{target} "
            f"(use '{UNFILED}' when it belongs to no subject)"
        )

    for subject_dir in sorted(p for p in store.iterdir() if p.is_dir()):
        subject = subject_dir.name

        if subject != UNFILED and not SUBJECT_STAMP_RE.match(subject):
            problems.append(stamp_problem(subject))

        for legacy in sorted(subject_dir.glob(f"*/{LEGACY_UNIT_FILENAME}")):
            problems.append(
                f"{subject}/{legacy.parent.name}/ uses the old folder shape — move it to "
                f"{subject}/{legacy.parent.name}{UNIT_SUFFIX} (artifacts go in "
                f"{legacy.parent.name}{ASSETS_SUFFIX}/)"
            )

        unit_files = sorted(subject_dir.glob(UNIT_GLOB))
        # Units written before the postfix convention are invisible to the glob above. Naming them
        # is what keeps a whole pre-existing store from silently reading as empty.
        unpostfixed = sorted(p for p in subject_dir.glob("*.md") if not p.name.endswith(UNIT_SUFFIX))
        for stale in unpostfixed:
            problems.append(
                f"{subject}/{stale.name} is missing the '{UNIT_SUFFIX}' postfix — rename it to "
                f"{subject}/{stale.stem}{UNIT_SUFFIX} (its '{ASSETS_SUFFIX}' folder, if any, keeps "
                f"its name)"
            )
        if not unit_files:
            # A folder holding only un-postfixed files already has the more precise problem above.
            if not unpostfixed:
                problems.append(f"{subject}/ contains no Understandings")
            continue

        for unit_file in unit_files:
            unit, unit_problems = read_unit(unit_file, subject, root)
            records.append({
                # The slug comes from the file name, so a copy whose frontmatter failed to parse
                # still takes part in version grouping instead of vanishing from it.
                "slug": slug_of(unit_file),
                "subject": subject,
                "path": f"{subject}/{unit_file.name}",
                "unit": unit,
                "problems": unit_problems,
            })

    problems.extend(case_collisions(records))

    current, superseded = group_versions(records)
    for record in current:
        problems.extend(record["problems"])

    current_units = [r["unit"] for r in current if r["unit"]]
    superseded_units = [r["unit"] for r in superseded if r["unit"]]
    # A `[[slug]]` names the knowledge, not a revision, so it resolves against every version.
    known = {record["slug"] for record in records}
    problems.extend(dangling_references(current_units, known))
    return current_units, superseded_units, problems


def case_collisions(records: list[dict]) -> list[str]:
    """Unit files or subject folders that are distinct names but the same entry on some filesystem.

    On a case-insensitive filesystem (the default on macOS and Windows) `Foo.understanding.md` and
    `foo.understanding.md` are one file, and the second write silently replaces the first. Ordinal
    comparison cannot see it, so neither can the version grouping: a repeated slug is treated as a
    version chain, which is correct on a case-sensitive filesystem and wrong here.

    Compared per filesystem entry, never per bare name: a unit file by its full `<subject>/<file>` path
    and a subject folder by its full stamped name. A store-wide slug bucket reported `Foo` and `foo` in
    two differently stamped folders, which are two files on every filesystem (issue 179).

    Running here catches a store that **already holds** a collision, and makes it reportable; it cannot
    undo an overwrite. The check that prevents the overwrite is the pre-extraction refusal in
    `publish-consume.md`, which runs before anything is written. Both are needed and neither substitutes
    for the other: this one is mechanical and always runs, that one is the only gate that is early
    enough to prevent.

    `casefold` rather than `lower` because it is the full Unicode case-folding operation, and it is what
    a filesystem compares: 'ß' folds to 'ss' and 'ﬁ' to 'fi', so two names `lower` keeps apart are one
    file on disk.
    """
    problems = []
    for field, kind in (("path", "unit file"), ("subject", "subject folder")):
        buckets: dict[str, set[str]] = {}
        for record in records:
            buckets.setdefault(str(record[field]).casefold(), set()).add(str(record[field]))
        for names in sorted(buckets.values(), key=sorted):
            # One distinct name cannot collide with itself; only a genuine disagreement is a problem.
            # Folder names are bucketed as a set, so many units in one folder stay silent, and a
            # repeated slug lives at a different path in each folder, so a version chain stays silent.
            if len(names) > 1:
                rendered = ", ".join(f"'{n}'" for n in sorted(names))
                problems.append(
                    f"{rendered} differ only by case and are one file on a case-insensitive "
                    f"filesystem — rename so each {kind} is distinct, or the later write silently "
                    f"replaces the earlier one"
                )
    return problems


def stamp_problem(subject: str) -> str:
    """Name the actual fix for a subject folder that fails the stamp check."""
    if STAMP_ONLY_RE.match(subject):
        return (
            f"{subject}/ has a stamp but no subject name in front of it — rename to "
            f"<subject>{subject}/ (kebab-case subject, then the -yyyyMMdd-HHmm stamp)"
        )
    malformed = MALFORMED_STAMP_RE.match(subject)
    if malformed:
        return (
            f"{subject}/ has a malformed creation stamp — the suffix must be exactly -yyyyMMdd-HHmm "
            f"(8 digits, dash, 4 digits; 24-hour UTC); rename to {malformed.group(1)}-<yyyyMMdd-HHmm>/"
        )
    return (
        f"{subject}/ is missing the <subject>-yyyyMMdd-HHmm creation stamp — rename to "
        f"{subject}-<yyyyMMdd-HHmm>/ (24-hour UTC; '{UNFILED}' is the only exempt folder)"
    )


def stamp_of(subject: str) -> str:
    """The `yyyyMMdd-HHmm` suffix of a subject folder, or '' when it has none.

    Fixed-width and zero-padded, so lexical order is chronological order. `_unfiled` and any
    folder failing the stamp check return '' and therefore sort oldest — `_unfiled` is a permanent
    catch-all rather than an export run, so a stamped improvement always supersedes a copy parked
    there.
    """
    return subject[-13:] if SUBJECT_STAMP_RE.match(subject) else ""


def is_gitignored(path: Path) -> bool:
    """True when `path` is ignored by the git repo it sits in.

    Runs `git check-ignore`, not a text search: the store may be ignored in this repo and tracked
    in another, and only the repo actually holding it decides whether a warning is owed. Without a
    repo, or without git on the path, the path is not ignored — nothing to warn about.
    """
    try:
        proc = subprocess.run(
            ["git", "-C", str(path.parent), "check-ignore", "-q", "--", path.name],
            capture_output=True,
        )
    except (FileNotFoundError, OSError):
        return False
    return proc.returncode == 0


def member_reads_cleanly(zf: zipfile.ZipFile, info: zipfile.ZipInfo) -> bool:
    """True when one member's bytes decompress and pass their CRC.

    The central directory lists a member whose data is damaged, so a name check alone clears it. The
    except is broad because a corrupt deflate stream raises `zlib.error` — neither an `OSError` nor a
    `BadZipFile` — and an encrypted or unsupported member raises other types again (issue 182).
    """
    try:
        with zf.open(info) as member:
            while member.read(1 << 20):
                pass
    except Exception:  # noqa: BLE001 — any unreadable member proves nothing
        return False
    return True


def published_paths(store: Path) -> set[str]:
    """Every unit path held by an archive in the publish dir beside the store.

    Membership, not archive time: a `--portable-only` archive is newer than the `repo-specific` units it
    deliberately left out, so judging by time reports them published and lets them vanish unwarned. An
    unreadable zip contributes nothing, so its units stay reported — the safe direction.

    A member counts only once its bytes read back cleanly. The central directory alone lists a member
    whose data is damaged, so `namelist()` reported it published while no extract could recover it
    (issue 182). Each member is read on its own (`member_reads_cleanly`), but if any member fails
    `consume_problems` the whole archive is refused and no member is published.
    """
    paths: set[str] = set()
    pub_dir = store.parent / PUBLISH_DIR_NAME
    if not pub_dir.is_dir():
        return paths
    for archive in sorted(pub_dir.glob("*.zip")):
        # An archive `--consume` would refuse — an escaping or symlink entry, a damaged member, two
        # entries landing on one path — can restore nothing, so it proves nothing was captured; it used
        # to be credited member by member and silenced the warning (issue 190). Judged against an empty
        # store, so only the archive's own defects count, not a collision with the units it holds.
        try:
            with tempfile.TemporaryDirectory() as empty:
                if consume_problems(archive, Path(empty)):
                    continue
        except (zipfile.BadZipFile, OSError):
            continue
        try:
            with zipfile.ZipFile(archive) as zf:
                # Members that would restore: not a symlink, and bytes that read back cleanly.
                restorable = {info.filename for info in zf.infolist()
                              if entry_type_ok(info) and member_reads_cleanly(zf, info)}
                for info in zf.infolist():
                    # Exactly `<subject-folder>/<slug>.understanding.md`, the only shape publish writes.
                    # Taking the last two parts of any name let `../x/slug…`, `a/b/slug…` or an
                    # absolute path mark the unit at `x/slug…` published while no consume would put it
                    # there (issue 186).
                    name = info.filename
                    parts = name.split("/")
                    if (len(parts) != 2 or any(part in ("", ".", "..") for part in parts)
                            or "\\" in name or re.match(r"^[A-Za-z]:", name)
                            or not parts[1].endswith(UNIT_SUFFIX)):
                        continue
                    # A symlink entry is a pointer, not the unit: consume refuses it, so it can never
                    # land the unit anywhere and proves nothing was captured (issue 188).
                    if name not in restorable:
                        continue
                    # The archived bytes must be this working copy's, not an earlier one: a unit
                    # redacted in place or a refreshed asset after the last publish left the only current
                    # copy in a disposable workspace while the path still matched (consumer review
                    # 5440964552 #2). Publish's own edits to the copy are discounted, nothing else.
                    local_unit = store / name
                    if not (local_unit.is_file()
                            and _same_unit(zf.read(name), local_unit.read_bytes())):
                        continue
                    # A unit travels with its `<slug>.assets/` evidence (publish-consume step 3). An
                    # archive holding the unit file but not every local asset file would restore it
                    # without its repro or diagram, yet credited it published (review 5432012955 #3).
                    assets = store / parts[0] / (parts[1][: -len(UNIT_SUFFIX)] + ASSETS_SUFFIX)
                    if assets.is_dir():
                        local_assets = {
                            f"{parts[0]}/{assets.name}/{f.relative_to(assets).as_posix()}": f
                            for f in assets.rglob("*") if f.is_file()}
                        if not set(local_assets) <= restorable or any(
                                zf.read(member) != path.read_bytes()
                                for member, path in local_assets.items()):
                            continue
                    paths.add(name)
        except (zipfile.BadZipFile, OSError):
            continue
    return paths


_PUBLISHED_FROM = re.compile(r"(?m)^[ \t]*published_from:[^\n]*\n?")
_LINK = re.compile(r"\[\[([^\[\]\n]+)\]\]")


def _same_unit(archived: bytes, local: bytes) -> bool:
    """Whether an archived unit holds the working copy, discounting only what publish itself changes:
    the `provenance.published_from` line it adds, and the `[[…]]` brackets `--portable-only` drops
    around an excluded slug (publish-consume steps 4 and 5).

    The brackets are discounted in one direction only: a working-copy `[[slug]]` may appear bare in the
    archive. Stripping them from both sides also accepted a working copy whose `[[slug]]` had since
    become bare, so changed lineage read as published (consumer review 5441621898 #4).
    """
    archived_text = _PUBLISHED_FROM.sub("", archived.decode("utf-8", errors="replace"))
    local_text = local.decode("utf-8", errors="replace")
    pattern, last = [], 0
    for link in _LINK.finditer(local_text):
        slug = re.escape(link.group(1))
        pattern += [re.escape(local_text[last:link.start()]), rf"(?:\[\[{slug}\]\]|{slug})"]
        last = link.end()
    pattern.append(re.escape(local_text[last:]))
    return re.fullmatch("".join(pattern), archived_text, re.DOTALL) is not None


def unpublished_units(units: list[dict], store: Path) -> list[dict]:
    """Current units whose path is in no published archive. No archive means every unit.

    Superseded copies are never passed here: publish archives current versions only, so history is
    workspace-local by design and flagging it would raise a warning `--publish` can never clear.
    """
    published = published_paths(store)
    return [unit for unit in units if unit["path"] not in published]


S_IFMT = 0o170000
S_IFLNK = 0o120000
S_IFREG = 0o100000
S_IFDIR = 0o040000


def entry_type_ok(info: zipfile.ZipInfo) -> bool:
    """A member's Unix type matches what its name extracts as: a `/`-ending name a folder, every other
    name a regular file. A type of 0 (no Unix mode, e.g. an archive written on Windows) is accepted.
    Only symlinks were refused, so a directory, FIFO or device entry named like a unit was credited as
    published with no regular unit file behind it (consumer review 5438563690 #3)."""
    kind = (info.external_attr >> 16) & S_IFMT
    return kind in (0, S_IFDIR if info.filename.endswith("/") else S_IFREG)


def consume_problems(archive: Path, store: Path) -> list[str]:
    """Every reason to refuse unpacking `archive` into `store`; empty means it is safe to extract.

    The pre-extraction gate for `--consume` (issue 182 moved it here from a documented heredoc, which
    the skill's `allowed-tools` could not run). An archive is untrusted input and is refused whole, never
    in part: an entry whose path escapes the store (absolute, a drive, `..`, a backslash, or a local
    symlinked folder pointing outside), a symlink entry (its Unix mode in `external_attr`, which `unzip
    -l` cannot show), a member whose data does not read back cleanly (CRC or decompression failure,
    issue 184), or a case-folded collision with another entry or a local path. A collision is the
    same class as an escape: on a case-insensitive filesystem the second extract silently replaces the
    first, and no check afterwards can see it because by then the destination is the source.
    """
    problems: list[str] = []
    store_real = Path(os.path.realpath(store))
    local_files: dict[str, Path] = {}
    local_dirs: dict[str, Path] = {}
    if store.is_dir():
        for existing in store.rglob("*"):
            rel = existing.relative_to(store).as_posix()
            (local_dirs if existing.is_dir() else local_files).setdefault(rel.casefold(), existing)
    claimed_files: dict[str, str] = {}
    claimed_dirs: dict[str, str] = {}

    def collide(entry: str, target: str) -> None:
        problems.append(f"'{entry}' collides on a case-insensitive filesystem with {target}")

    def check_dir(folder: str, entry: str) -> None:
        key = folder.casefold()
        if key == "index.md":
            problems.append(f"'{entry}' makes 'INDEX.md' a folder; the store's root index must be a file")
            return
        if key in local_files:
            collide(entry, f"local '{local_files[key]}'")
        elif key in local_dirs and local_dirs[key].relative_to(store).as_posix() != folder:
            collide(entry, f"local '{local_dirs[key]}'")
        if key in claimed_files:
            collide(entry, f"'{claimed_files[key]}' elsewhere in this archive")
        elif claimed_dirs.setdefault(key, folder) != folder:
            collide(entry, f"folder '{claimed_dirs[key]}' elsewhere in this archive")

    with zipfile.ZipFile(archive) as zf:
        infos = zf.infolist()
        # Every member's bytes are read before anything else is judged. A name and a mode say nothing
        # about the data, and a damaged member extracts as garbage or not at all — a partial extract,
        # which refusing the whole archive exists to prevent (issue 184).
        damaged = {info.filename for info in infos if not member_reads_cleanly(zf, info)}
    for info in infos:
        name = info.filename
        path = PurePosixPath(name)
        if name in damaged:
            problems.append(f"'{name}' is damaged: its data does not read back cleanly")
        if (name.startswith("/") or "\\" in name or re.match(r"^[A-Za-z]:", name)
                or ".." in path.parts):
            problems.append(f"'{name}' escapes the target store")
            continue
        if (info.external_attr >> 16) & S_IFMT == S_IFLNK:
            problems.append(f"'{name}' is a symlink entry")
            continue
        if not entry_type_ok(info):
            problems.append(f"'{name}' is not a regular file or folder entry")
            continue
        resolved = Path(os.path.realpath(store_real / name))
        if resolved != store_real and store_real not in resolved.parents:
            problems.append(f"'{name}' escapes the target store through a local symlink")
            continue
        # Every parent folder, outermost first; the last of `parents` is '.', the store itself.
        for parent in reversed(list(path.parents)[:-1]):
            check_dir(parent.as_posix(), name)
        # The generated root index is exempt: every archive carries one and every store holds one, and
        # it is regenerated after every write, so a case fold on it cannot lose knowledge. Only the
        # root one — a nested `INDEX.md` is not regenerated by anything and lands like any file
        # (issue 184).
        # Exempt only as the root **file**: a directory named `INDEX.md` (an explicit `INDEX.md/` entry,
        # or a parent of another entry) took the exemption too and landed where the generated index
        # must be written, so the next regeneration failed or the folder shadowed it (issue 186).
        if name == "INDEX.md":
            if "index.md" in local_dirs:
                problems.append("'INDEX.md' must be a file: the store holds a folder by that name")
            continue
        if name.endswith("/"):
            check_dir(path.as_posix(), name)
            continue
        key = path.as_posix().casefold()
        # Where the entry would really land. A local folder symlinked inside the store makes
        # `alias/x.md` land on `real/x.md`, which the name-based check could not see, so an existing
        # file was overwritten through the alias (issue 188).
        resolved_key = resolved.relative_to(store_real).as_posix().casefold()
        if resolved_key != key and (resolved_key in local_files or resolved_key in local_dirs):
            collide(name, f"local '{local_files.get(resolved_key) or local_dirs.get(resolved_key)}'")
        if key in local_files or key in local_dirs:
            collide(name, f"local '{local_files.get(key) or local_dirs[key]}'")
        elif key in claimed_files or key in claimed_dirs:
            collide(name, f"'{claimed_files.get(key) or claimed_dirs[key]}' elsewhere in this archive")
        elif resolved_key in claimed_files or resolved_key in claimed_dirs:
            # Two entries with different names that land on one file — `alias/x` through a local
            # symlinked folder and `real/x` — were compared by name only, so one extracted over the
            # other (issue 190).
            collide(name, f"'{claimed_files.get(resolved_key) or claimed_dirs[resolved_key]}' "
                          "elsewhere in this archive (same destination)")
        claimed_files.setdefault(key, name)
        claimed_files.setdefault(resolved_key, name)
    return problems


def consume_check(archive: Path, store: Path) -> int:
    """CLI for `consume_problems`: 0 safe to extract, 1 refused, 2 archive unreadable."""
    try:
        problems = consume_problems(archive, store)
    except (zipfile.BadZipFile, OSError):
        print(f"refusing: {archive} is not a readable zip archive; nothing was extracted",
              file=sys.stderr)
        return 2
    if problems:
        for problem in problems:
            print(f"refusing: {problem}")
        print("refused: the whole archive is rejected; extract none of it")
        return 1
    print(f"consume-check: {archive} is safe to extract into {store} "
          "(no escaping path, symlink entry, damaged member or case-folded collision)")
    return 0


def version_key(record: dict) -> tuple[str, str, str]:
    """Recency of one copy of a slug. Folder stamp decides; `updated` and path break ties.

    The fallback matters for `_unfiled` and for two runs that landed in the same minute — the
    worktask's stated default is that the folder stamp decides, falling back to `updated`. Path
    is last so the result is deterministic rather than filesystem-order dependent.
    """
    fields = record["unit"] or {}
    return (stamp_of(record["subject"]), str(fields.get("updated") or ""), record["path"])


def group_versions(records: list[dict]) -> tuple[list[dict], list[dict]]:
    """Split every copy of every slug into (current, superseded).

    One slug has exactly one current version: the copy with the highest `version_key`. This
    replaces the retired `duplicate_slugs` check — a repeated slug is the versioning mechanism
    now, and the cost of that is real: a typo'd slug colliding with unrelated knowledge is no
    longer distinguishable from a deliberate revision, and nothing mechanical catches it. The
    export procedure's reconcile step is what prevents it.
    """
    by_slug: dict[str, list[dict]] = {}
    for record in records:
        by_slug.setdefault(record["slug"], []).append(record)

    newest = {slug: max(copies, key=version_key) for slug, copies in by_slug.items()}
    current_paths = {record["path"] for record in newest.values()}
    current = [r for r in records if r["path"] in current_paths]
    superseded = [r for r in records if r["path"] not in current_paths]
    return current, superseded


def iter_reference_fields(unit: dict):
    """Yield every (field-path, list) in a unit's frontmatter that could hold `[[slug]]` refs.

    Walks the shape generically — top-level lists and lists one level inside a map — so a
    field added to the template is validated without touching this file. Parsing was already
    generic; validation used to be per-field, and a new field went silently unchecked.
    """
    for key, value in unit.items():
        if key in ("folder", "subject", "path"):
            continue
        if isinstance(value, list):
            yield key, value
        elif isinstance(value, dict):
            for sub_key, sub_value in value.items():
                if isinstance(sub_value, list):
                    yield f"{key}.{sub_key}", sub_value


# How to word an unresolvable reference, per field. Any field not listed gets the default.
REFERENCE_REMEDIES = {
    "provenance.inherited": (
        "is no longer in the store — restore it, point at what superseded it, "
        "or unbracket it to keep the lineage"
    ),
}
DEFAULT_REMEDY = "has no matching Understanding"


def dangling_references(units: list[dict], known: set[str]) -> list[str]:
    """An unresolvable `[[slug]]` in any frontmatter list, wherever it appears.

    Only bracketed entries are references. A bare string is a historical note — that is how a
    lineage survives the Understanding it names being pruned or promoted away.

    `known` covers every version of every slug, including superseded copies: a link means the
    knowledge, so it resolves as long as some copy of that slug exists.
    """
    dangling = []
    for unit in units:
        for field, values in iter_reference_fields(unit):
            for entry in values:
                entry = str(entry).strip()
                if not entry.startswith("[[") or placeholder(entry):
                    continue
                target = entry.strip("[]")
                if target and target not in known:
                    remedy = REFERENCE_REMEDIES.get(field, DEFAULT_REMEDY)
                    dangling.append(f"{unit['path']}: {field} [[{target}]] {remedy}")
    return dangling


def inherited_targets(units: list[dict]) -> set[str]:
    """Every slug any unit records having acted on. The store's only usage signal.

    The list guard is load-bearing. A flow-style or bare scalar (`inherited: [[beta]]`) parses as a
    string, and iterating it yields characters: the real target is lost *and* the garbage entries make
    `lineage_recorded` true, so every unit reports as never inherited. A current unit carrying that
    shape is caught by `inline_sequences`, but a superseded copy is exempt from validation while still
    feeding this function — so without the guard an old copy corrupts the report with nothing printed
    and exit 0.

    The placeholder guard is the same failure through a different door. An untouched template entry is
    not bracketed, so it reads as a deliberate unbracketed note and counts as usage: it satisfies the
    carrier's own lineage *and* sets `lineage_recorded`, flagging every other unit as never inherited.
    `read_unit` rejects it on a current unit, but a superseded copy is exempt from validation while
    still feeding this function, so the guard has to live here too.
    """
    targets = set()
    for unit in units:
        provenance = unit.get("provenance")
        if not isinstance(provenance, dict):
            continue
        inherited = provenance.get("inherited")
        if not isinstance(inherited, list):
            continue
        for entry in inherited:
            entry = str(entry).strip()
            if placeholder(entry):
                continue
            targets.add(entry.strip("[]"))
    return targets


def days_since(value) -> int | None:
    try:
        return (date.today() - date.fromisoformat(str(value).strip())).days
    except ValueError:
        return None


def review(units: list[dict], superseded: list[dict] | None = None,
           unpublished: list[dict] | None = None) -> list[str]:
    """Advisory decay report: which units are worth re-reading, pruning or promoting.

    Not validation — none of this is wrong, and the report never changes the exit code. It exists
    because a store that only grows stops being readable, and nothing else notices.

    Runs over current versions only, and the never-inherited signal counts inheritance of **any**
    version: usage accrues to the slug, so a unit improved three times is not reported as unused
    because the lineage names an earlier revision.

    A flagged unit prints its `recheck` command, which is what turns a staleness flag into an action;
    a stale unit carrying none is told so, since that is the moment to add one.
    """
    superseded = superseded or []
    used = inherited_targets(units + superseded)
    # In a store where nothing has recorded inheritance yet, "never inherited" carries no signal.
    lineage_recorded = bool(used)

    lines = []
    for unit in units:
        flags = []
        is_outcome = not unit.get("question")
        age = days_since(unit.get("updated"))
        limit = STALE_AFTER_DAYS_OUTCOME if is_outcome else STALE_AFTER_DAYS

        if unit.get("confidence") == "contested":
            flags.append("contested — the code disagreed with it; confirm or retire")
        if lineage_recorded and unit["folder"] not in used:
            flags.append("never inherited — the question may not match what anyone actually asks")
        if age is not None and age > limit:
            kind = "outcome" if is_outcome else "knowledge"
            flags.append(f"{age}d since update ({kind} unit, flagged past {limit}d) — re-check it still holds")
        if flags:
            lines.append(f"{unit['path']}:")
            lines.extend(f"    {f}" for f in flags)
            recheck = unit.get("recheck")
            if isinstance(recheck, str) and recheck and not placeholder(recheck):
                lines.append(f"    recheck: {recheck}")
            elif age is not None and age > limit:
                lines.append("    no 'recheck' command — add one so the next flag is actionable")

    revisions: dict[str, int] = {}
    for unit in superseded:
        revisions[unit["folder"]] = revisions.get(unit["folder"], 0) + 1
    for slug in sorted(revisions):
        lines.append(
            f"{slug}: {revisions[slug]} superseded copy(ies) on disk — prune guidance only; "
            f"history is never deleted automatically"
        )

    if unpublished:
        lines.append("unpublished — the store is gitignored and these units are in no published "
                     "archive, so they would vanish with this workspace:")
        lines.extend(f"    {unit['path']}" for unit in unpublished)
        lines.append("    run `ai-understanding --publish` to carry them out")

    return lines


def cell(value) -> str:
    if not isinstance(value, str) or not value:
        return "—"
    return value.replace("|", "\\|")


def render(units: list[dict]) -> str:
    lines = [
        "# Understandings — INDEX",
        "",
        "Generated by `.agents/skills/ai-understanding/scripts/understanding_index.py`. Do not hand-edit.",
        "",
        "Grouped by subject for browsing; every Understanding is listed individually because knowledge is",
        "retrieved by the **question** it answers, not by the subject that produced it. Match a question",
        "against what you are about to ask, then read only the units that matched.",
        "",
        "One row per slug, resolving to its **newest** version. A slug repeated across stamped folders is a",
        "version chain; superseded copies stay on disk, unlisted. When two Understandings conflict, the newer",
        "wins — but the system outranks both.",
        "",
    ]

    if not units:
        lines += ["_No Understandings encoded yet._", ""]
        return "\n".join(lines)

    subjects: dict[str, list[dict]] = {}
    for unit in units:
        subjects.setdefault(unit["subject"], []).append(unit)

    for subject in sorted(subjects):
        lines += [
            f"## {subject}",
            "",
            "| Understanding | Description | Question | Scope | Confidence | Updated |",
            "|---------------|-------------|----------|-------|------------|---------|",
        ]
        for unit in subjects[subject]:
            path = unit["path"]
            lines.append(
                f"| [`{unit['folder']}`](./{path}) | {cell(unit.get('description'))} "
                f"| {cell(unit.get('question'))} | {cell(unit.get('scope'))} "
                f"| {cell(unit.get('confidence'))} | {cell(unit.get('updated'))} |"
            )
        lines.append("")
    return "\n".join(lines)


USAGE = f"""usage: understanding_index.py [store-dir] [--review]
       understanding_index.py --consume-check <zip> [store-dir]
       understanding_index.py --stamp

Regenerate INDEX.md from the <subject>-<yyyyMMdd-HHmm>/<slug>.understanding.md units.
A slug repeated across stamped folders is a version chain: the newest stamp is indexed,
older copies stay on disk unlisted and exempt from validation.
Defaults to {DEFAULT_STORE}. --review adds an advisory decay report, including an `unpublished`
section when the store is gitignored. A gitignored store also prints a one-line durability warning.

--consume-check lists <zip> without extracting it and refuses the whole archive on an escaping path,
a symlink entry or a case-folded collision with another entry or with the target store.
--stamp prints the current UTC subject-folder stamp (yyyyMMdd-HHmm).

Exit codes: 0 clean · 1 validation problems (index still written) or archive refused ·
2 store not found, bad arguments, or an unreadable archive."""


def main(argv: list[str]) -> int:
    args = argv[1:]
    if any(a in ("-h", "--help") for a in args):
        print(USAGE)
        return 0
    if args == ["--stamp"]:
        print(datetime.now(timezone.utc).strftime("%Y%m%d-%H%M"))
        return 0
    if args and args[0] == "--consume-check":
        rest = args[1:]
        if not 1 <= len(rest) <= 2 or any(a.startswith("-") for a in rest):
            print(f"--consume-check takes <zip> [store-dir]\n\n{USAGE}", file=sys.stderr)
            return 2
        return consume_check(Path(rest[0]), Path(rest[1]) if len(rest) == 2 else DEFAULT_STORE)
    wants_review = "--review" in args
    args = [a for a in args if a != "--review"]
    unknown = [a for a in args if a.startswith("-")]
    if unknown:
        print(f"unknown option: {unknown[0]}\n\n{USAGE}", file=sys.stderr)
        return 2
    if len(args) > 1:
        print(f"expected at most one store-dir, got {len(args)}\n\n{USAGE}", file=sys.stderr)
        return 2

    store = Path(args[0]) if args else DEFAULT_STORE
    if not store.is_dir():
        print(f"store not found: {store}", file=sys.stderr)
        return 2

    units, superseded, problems = load_units(store)
    (store / "INDEX.md").write_text(render(units), encoding="utf-8")
    subject_count = len({unit["subject"] for unit in units})
    history = f", {len(superseded)} superseded copy(ies) unlisted" if superseded else ""
    print(
        f"indexed {len(units)} understanding(s) across {subject_count} subject(s){history} "
        f"-> {store / 'INDEX.md'}"
    )

    unpublished = unpublished_units(units, store) if is_gitignored(store) else []
    if unpublished:
        print(
            f"warning: {store} is gitignored and {len(unpublished)} current unit(s) are unpublished — "
            f"run `ai-understanding --publish` to carry them out of this workspace"
        )

    if wants_review:
        report = review(units, superseded, unpublished)
        print("\nreview — advisory, does not affect the exit code")
        print("\n".join(report) if report else "    nothing flagged")

    for problem in problems:
        print(f"  problem: {problem}", file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
