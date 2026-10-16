#!/usr/bin/env python3
"""Regenerate the local memory INDEX.md from the run/category/slug folders.

The store is `.context/context-memories/<yyyyMMdd-HHmm>-<session-id>/<category>/<slug>.memory.md`. A
**run folder** is one export run; categories live inside it, dateless, and recur across runs. A slug
names the knowledge, not one copy of it, so the **same slug in several run folders is a version
chain** rather than an error. The newest stamp is the current version; the rest are history and are
never edited or deleted. `_unfiled/` is the one exempt, unstamped folder and sorts oldest.

**The stamp keeps its `-HHmm`, and that is load-bearing.** The stamp is the primary sort key for
"which copy is current", and the `updated` fallback is required but **date-only** — so a date-only
stamp would collapse every export on one day into a single recency bucket and silently mark the wrong
version current. One export run per minute is the assumption that holds it correct.

Validation applies to current versions only: superseded copies are immutable history, and validating
them would force edits to the past. Folder-level problems are reported regardless, since they belong
to no single version.

A unit needing evidence artifacts puts them in a sibling `<slug>.assets/`.

Unlike `ai-understanding`, this store is **local-first and has no publish verb**: there is no zip and
no durability boundary in the transport sense, because the store is gitignored workspace state that a
user commits, copies or archives themselves. That is a deliberate v1 boundary, recorded in the skill's
LADR-001, not an omission — a half-ported `--publish` that archived an unverified shape would be
worse than none.

Usage:
    python3 .agents/skills/ai-memory-context/scripts/memory_index.py [store-dir] [--review]
    python3 .agents/skills/ai-memory-context/scripts/memory_index.py --run-folder [SESSION_ID]
    python3 .agents/skills/ai-memory-context/scripts/memory_index.py --stamp

Defaults to `.context/context-memories`. Standard library only; `git check-ignore` for the advisory
durability note, which is skipped with a stated reason outside a repository. Read/write only to the
store, no network.
Exits 1 when a current unit fails validation — the index is still written so the drift stays visible.
"""

from __future__ import annotations

import os
import re
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

DEFAULT_STORE = Path(".context/context-memories")
UNIT_SUFFIX = ".memory.md"
UNIT_GLOB = f"*{UNIT_SUFFIX}"
ASSETS_SUFFIX = ".assets"
REQUIRED_FIELDS = ("slug", "description", "scope", "confidence")
OPTIONAL_TEXT_FIELDS = ("question", "boundaries")
REQUIRED_PROVENANCE_FIELDS = ("learned", "session", "source")
VALID_SCOPES = ("portable", "repo-specific")
VALID_CONFIDENCE = ("observed", "verified", "contested")
UNFILED = "_unfiled"

# A legacy-looking run folder, and — decisively — a top-level folder with no unit files directly in
# it. Dispatching the walk on the folder *name* alone would leave it unable to see the nested layout,
# because every folder in a legacy-shaped store is a legacy run folder. A folder satisfying both
# readings (units *and* categories) is read as nested, which is the shape the writer produces.
SUBJECT_STAMP_RE = re.compile(r"^.+-\d{8}-\d{4}$")
RUN_STAMP_RE = re.compile(r"^(\d{8})-(\d{4})-(\S+)$")
RUN_ONLY_RE = re.compile(r"^\d{8}-\d{4}-?$")
# A suffix that is trying to be a stamp but has the wrong field widths, and a stamp with nothing in
# front of it. Each gets its own remedy, so the message names the actual fix rather than suggesting
# a second stamp.
MALFORMED_STAMP_RE = re.compile(r"^(.+)-\d+-\d+$")
STAMP_ONLY_RE = re.compile(r"^-\d+-\d+$")
BLOCK_SCALAR_RE = re.compile(r"^[|>][0-9+-]{0,2}$")
PLACEHOLDERS = {"", "todo", "tbd", "xxx", "n/a", "na", "none", "changeme", "your-value-here",
                "<value>", "placeholder", "example", "foo", "bar"}

# Session ids come from the environment rather than being guessed, on the same ladder the sibling
# marker scripts use — two skills inventing different readings of one id is how a file ends up filed
# under the wrong session.
SESSION_ID_VARS = ("CLAUDE_CODE_SESSION_ID", "CODEX_THREAD_ID", "OPENCODE_SESSION_ID",
                   "COPILOT_AGENT_SESSION_ID", "PI_SESSION_ID")
_UNSAFE_SESSION_RE = re.compile(r"[^A-Za-z0-9._-]+")
STALE_AFTER_DAYS = 90
STALE_AFTER_DAYS_OUTCOME = 30


# --------------------------------------------------------------------------------------------------
# frontmatter
# --------------------------------------------------------------------------------------------------

def parse_frontmatter(text: str) -> dict:
    """Flat scalars, lists, and one nested map (`provenance`). Deeper nesting needs extending here."""
    lines = text.splitlines()
    if not lines or lines[0].strip() != "---":
        return {}
    fields: dict = {}
    key: str | None = None
    for line in lines[1:]:
        if line.strip() == "---":
            break
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        if not line.startswith((" ", "\t")):
            match = re.match(r"^([A-Za-z_][A-Za-z0-9_]*):\s*(.*)$", line)
            if not match:
                key = None
                continue
            key = match.group(1)
            fields[key] = match.group(2).strip()
        elif key is not None:
            # `provenance:` children, or a wrapped scalar.
            child = re.match(r"^\s+([A-Za-z_][A-Za-z0-9_]*):\s*(.*)$", line)
            if child:
                if not isinstance(fields.get(key), dict):
                    # First child promotes the empty scalar to a map.
                    fields[key] = {child.group(1): child.group(2).strip()}
                else:
                    # Later children must carry their VALUE. An earlier version used `setdefault`
                    # with an empty string here, which made every field after the first one parse as
                    # a placeholder — so a perfectly good `provenance` was reported as missing while
                    # the store looked fine in the file.
                    fields[key][child.group(1)] = child.group(2).strip()
            elif isinstance(fields.get(key), str):
                fields[key] = f"{fields[key]} {line.strip()}"
    return fields


def unquote(value: str) -> str:
    if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
        return value[1:-1]
    return value


def placeholder(value: object) -> bool:
    return not isinstance(value, str) or unquote(value.strip()).strip("<>").lower() in PLACEHOLDERS


def slug_of(unit_file: Path) -> str:
    name = unit_file.name
    if name.endswith(UNIT_SUFFIX):
        name = name[: -len(UNIT_SUFFIX)]
    elif name.endswith(".md"):
        name = name[:-3]
    return name


def iso_date(value: str) -> bool:
    return bool(re.match(r"^\d{4}-\d{2}-\d{2}$", unquote(value.strip())))


def _iter_values(value) -> list:
    if isinstance(value, dict):
        out = []
        for item in value.values():
            out += _iter_values(item)
        return out
    if isinstance(value, list):
        out = []
        for item in value:
            out += _iter_values(item)
        return out
    return [value]


# --------------------------------------------------------------------------------------------------
# stamps
# --------------------------------------------------------------------------------------------------

def stamp_of(run: str) -> str:
    """The `yyyyMMdd-HHmm` stamp of a run folder, or '' when it has none.

    Fixed-width and zero-padded so lexical order is chronological order. `_unfiled` and anything
    failing either check return '' and therefore sort oldest.

    Two shapes, because a legacy-shaped store is still read: `<yyyyMMdd-HHmm>-<session-id>` (what the
    writer produces) and `<subject>-<yyyyMMdd-HHmm>` (read, never written). The legacy stamp is a
    fixed-width tail slice, which is why the run shape could not keep its stamp there.
    """
    run_stamp = RUN_STAMP_RE.match(run)
    if run_stamp:
        return f"{run_stamp.group(1)}-{run_stamp.group(2)}"
    return run[-13:] if SUBJECT_STAMP_RE.match(run) else ""


def session_of(run: str) -> str:
    """The session id in a run folder's name, or '' for a legacy or unstamped folder.

    Redundant with the path today — the run folder is the path's first segment — and kept because the
    ordering is then visible in `version_key` rather than resting on a reader knowing that. An id is
    opaque, so this is a *deterministic* tiebreak, not a recency one.
    """
    run_stamp = RUN_STAMP_RE.match(run)
    return run_stamp.group(3) if run_stamp else ""


def stamp_problem(name: str) -> str:
    """Name the actual fix for a top-level folder that fails both stamp checks."""
    if STAMP_ONLY_RE.match(name):
        return (f"{name}/ has a stamp but no subject name in front of it — the current shape is "
                f"<yyyyMMdd-HHmm>-<session-id> (rename to 20261016-1432-<session-id>/), or for the "
                f"legacy shape <subject>{name}/")
    malformed = MALFORMED_STAMP_RE.match(name)
    if malformed:
        return (f"{name}/ has a malformed creation stamp — the stamp must be exactly yyyyMMdd-HHmm "
                f"(8 digits, dash, 4 digits; 24-hour UTC); rename to "
                f"{malformed.group(1)}-<yyyyMMdd-HHmm>/ (legacy) or <yyyyMMdd-HHmm>-<session-id>/")
    if RUN_ONLY_RE.match(name):
        return (f"{name}/ has a stamp but no session id — a run folder is "
                f"<yyyyMMdd-HHmm>-<session-id>/, one export run per folder")
    return (f"{name}/ is missing the creation stamp — rename to <yyyyMMdd-HHmm>-<session-id>/ "
            f"(24-hour UTC; '{UNFILED}' is the only exempt folder)")


# --------------------------------------------------------------------------------------------------
# run folder naming
# --------------------------------------------------------------------------------------------------

def safe_session_id(session: str) -> str:
    """A session id that can sit inside a folder name without changing its shape.

    A `/` is the dangerous case: an unsanitised id lands the units one level deeper, the run folder
    never gets a valid stamp shape, and every unit in the export silently falls out of the index. A
    digest of the original is appended whenever the id had to be rewritten, so two distinct ids that
    sanitise alike still get separate folders.
    """
    import hashlib

    safe = _UNSAFE_SESSION_RE.sub("_", session).strip("._") or "session"
    if safe != session:
        safe = f"{safe}-{hashlib.sha256(session.encode('utf-8')).hexdigest()[:8]}"
    return safe


def resolve_session(explicit: str | None = None) -> str | None:
    if explicit:
        return explicit
    for name in SESSION_ID_VARS:
        value = os.environ.get(name)
        if value and value.strip():
            return value.strip()
    return None


def run_folder_name(session: str) -> str:
    """The complete run folder for `session`, stamped once.

    Resolving the clock here and returning the whole name is the point. An export writes its units
    across many file operations performed by the *agent*, not one process, so a stamp re-read between
    two units can land either side of a minute boundary and split a single run across two folders —
    each then holding half, and the version chain broken in a way no later check repairs.
    """
    return f"{datetime.now(timezone.utc).strftime('%Y%m%d-%H%M')}-{safe_session_id(session)}"


# --------------------------------------------------------------------------------------------------
# validation
# --------------------------------------------------------------------------------------------------

def read_unit(unit_file: Path, run: str, category: str) -> tuple[dict | None, list[str]]:
    """Load and validate one `<stamp>-<session>/<category>/<slug>.memory.md`."""
    slug = slug_of(unit_file)
    rel = f"{run}/{category}/{unit_file.name}" if category else f"{run}/{unit_file.name}"
    problems: list[str] = []

    try:
        text = unit_file.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError) as error:
        return None, [f"{rel}: unreadable ({error.__class__.__name__})"]

    fields = parse_frontmatter(text)
    if not fields:
        return None, [f"{rel}: no YAML frontmatter — a unit needs at least slug, description, "
                      f"scope and confidence"]

    where = rel
    for field in REQUIRED_FIELDS:
        if placeholder(fields.get(field)):
            problems.append(f"{where}: '{field}' is missing or still a placeholder")
    for field in OPTIONAL_TEXT_FIELDS:
        value = fields.get(field)
        if value is not None and placeholder(value):
            problems.append(f"{where}: '{field}' is present but empty")
    provenance = fields.get("provenance")
    if not isinstance(provenance, dict):
        problems.append(f"{where}: 'provenance' is missing or not a map of field: value")
    else:
        for field in REQUIRED_PROVENANCE_FIELDS:
            if placeholder(provenance.get(field)):
                problems.append(f"{where}: 'provenance.{field}' is missing or still a placeholder")
    if not placeholder(fields.get("updated")):
        if not iso_date(str(fields.get("updated"))):
            problems.append(f"{where}: 'updated' is not an ISO date (YYYY-MM-DD)")
    if not placeholder(fields.get("slug")) and unquote(str(fields["slug"])).strip() != slug:
        problems.append(
            f"{where}: 'slug' is '{unquote(str(fields['slug']))}' but the file name says '{slug}' — "
            f"they must match, since the slug addresses the knowledge rather than one copy of it"
        )
    scope = fields.get("scope")
    if not placeholder(scope) and unquote(str(scope)) not in VALID_SCOPES:
        problems.append(f"{where}: 'scope' must be one of {', '.join(VALID_SCOPES)}")
    confidence = fields.get("confidence")
    if not placeholder(confidence) and unquote(str(confidence)) not in VALID_CONFIDENCE:
        problems.append(f"{where}: 'confidence' must be one of {', '.join(VALID_CONFIDENCE)}")

    for key, value in fields.items():
        if isinstance(value, str) and BLOCK_SCALAR_RE.match(value.strip()):
            problems.append(f"{where}: '{key}' is a block scalar — every field is one line")

    if len(unquote(str(fields.get("description", "")))) < 12:
        problems.append(f"{where}: 'description' is under 12 characters — too short to carry a fact")

    fields["folder"] = slug
    fields["subject"] = category or run
    fields["path"] = rel
    return fields, problems


def _record(unit_file: Path, run: str, category: str) -> dict:
    """One unit's record, for either layout.

    The slug comes from the file name, so a copy whose frontmatter failed to parse still takes part
    in version grouping instead of vanishing from it.
    """
    unit, unit_problems = read_unit(unit_file, run, category)
    rel = f"{run}/{category}/{unit_file.name}" if category else f"{run}/{unit_file.name}"
    return {"slug": slug_of(unit_file), "subject": category or run, "run": run,
            "category": category, "path": rel, "unit": unit, "problems": unit_problems}


def load_units(store: Path) -> tuple[list[dict], list[dict], list[str]]:
    """Read every copy in the store and return (current units, superseded units, problems)."""
    records: list[dict] = []
    problems: list[str] = []

    for stray in sorted(store.glob("*.md")):
        if stray.name == "INDEX.md":
            continue
        target = stray.name if stray.name.endswith(UNIT_SUFFIX) else f"{stray.stem}{UNIT_SUFFIX}"
        problems.append(
            f"{stray.name} sits at the store root — move it to "
            f"<yyyyMMdd-HHmm>-<session-id>/<category>/{target} "
            f"(use '{UNFILED}' when it belongs to no category)"
        )

    for run_dir in sorted(p for p in store.iterdir() if p.is_dir()):
        run = run_dir.name
        if run != UNFILED and not SUBJECT_STAMP_RE.match(run) and not RUN_STAMP_RE.match(run):
            problems.append(stamp_problem(run))

        direct = sorted(run_dir.glob(UNIT_GLOB))
        categories = sorted(p for p in run_dir.iterdir() if p.is_dir())
        nested = not direct and bool(categories)

        if nested:
            for category_dir in categories:
                category = category_dir.name
                units = sorted(category_dir.glob(UNIT_GLOB))
                stale = sorted(p for p in category_dir.glob("*.md")
                               if not p.name.endswith(UNIT_SUFFIX))
                for path in stale:
                    problems.append(
                        f"{run}/{category}/{path.name} is missing the '{UNIT_SUFFIX}' postfix — "
                        f"rename it to {run}/{category}/{path.stem}{UNIT_SUFFIX}"
                    )
                if not units:
                    if not stale:
                        problems.append(f"{run}/{category}/ contains no memories")
                    continue
                for unit_file in units:
                    records.append(_record(unit_file, run, category))
            continue

        unpostfixed = sorted(p for p in run_dir.glob("*.md")
                             if not p.name.endswith(UNIT_SUFFIX))
        for stale in unpostfixed:
            problems.append(
                f"{run}/{stale.name} is missing the '{UNIT_SUFFIX}' postfix — rename it to "
                f"{run}/{stale.stem}{UNIT_SUFFIX}"
            )
        if not direct:
            if not unpostfixed:
                problems.append(f"{run}/ contains no memories")
            continue
        for unit_file in direct:
            records.append(_record(unit_file, run, ""))

    problems.extend(case_collisions(records))
    current, superseded = group_versions(records)
    for record in current:
        problems.extend(record["problems"])
    return ([r["unit"] for r in current if r["unit"]],
            [r["unit"] for r in superseded if r["unit"]],
            problems)


def case_collisions(records: list[dict]) -> list[str]:
    """Memory files that are distinct names but the same entry on some filesystem.

    On a case-insensitive filesystem (the default on macOS and Windows) `Deploy-Pipeline.memory.md`
    and `deploy-pipeline.memory.md` are one file, and the second write silently replaces the first.
    Ordinal comparison cannot see it, so neither can the version grouping: the two are treated as
    unrelated memories and both stay current.

    Running here catches a store that **already holds** a collision and makes it reportable. It cannot
    undo an overwrite — the second write is gone by the time this runs. What prevents the overwrite is
    the export procedure: a slug differing only by case from one already in the store is an
    *improved* copy of that slug, not a new one, so it must reuse the existing name.

    `casefold` rather than `lower` because it is the full Unicode case-folding operation, and it is
    what a filesystem compares: 'ss' folds from 'ss' and 'fi' from 'fi', so two names `lower` keeps
    apart are one file on disk.
    """
    problems = []
    for field, kind in (("path", "memory file"), ("category", "category folder")):
        buckets: dict[str, set[str]] = {}
        for record in records:
            value = str(record.get(field) or "")
            if value:
                buckets.setdefault(value.casefold(), set()).add(value)
        for names in sorted(buckets.values(), key=sorted):
            # One distinct name cannot collide with itself; only a genuine disagreement is a problem.
            if len(names) > 1:
                rendered = ", ".join(f"'{n}'" for n in sorted(names))
                problems.append(
                    f"{rendered} differ only by case and are one file on a case-insensitive "
                    f"filesystem — rename so each {kind} is distinct, or the later write silently "
                    f"replaces the earlier one"
                )
    return problems


def version_key(record: dict) -> tuple[str, str, str, str]:
    """Recency of one copy of a slug. Run stamp decides; `updated`, session and path break ties."""
    fields = record["unit"] or {}
    return (stamp_of(record["run"]), str(fields.get("updated") or ""),
            session_of(record["run"]), record["path"])


def group_versions(records: list[dict]) -> tuple[list[dict], list[dict]]:
    """Split every copy of every slug into (current, superseded).

    One slug has exactly one current version: the copy with the highest `version_key`. This is the
    versioning mechanism, and the cost is real — a typo'd slug colliding with unrelated knowledge is
    no longer distinguishable from a deliberate revision, and nothing mechanical catches it. The
    export procedure's reconcile step is what prevents it.
    """
    by_slug: dict[str, list[dict]] = {}
    for record in records:
        by_slug.setdefault(record["slug"], []).append(record)
    newest = {slug: max(copies, key=version_key) for slug, copies in by_slug.items()}
    current_paths = {r["path"] for r in newest.values()}
    return ([r for r in records if r["path"] in current_paths],
            [r for r in records if r["path"] not in current_paths])


# --------------------------------------------------------------------------------------------------
# review + render
# --------------------------------------------------------------------------------------------------

def days_since(value) -> int | None:
    if not isinstance(value, str) or not iso_date(value):
        return None
    try:
        then = datetime.strptime(unquote(value), "%Y-%m-%d").replace(tzinfo=timezone.utc)
    except ValueError:
        return None
    return (datetime.now(timezone.utc) - then).days


def is_gitignored(path: Path) -> bool | None:
    """True/False from `git check-ignore`; None when git cannot answer (no repository).

    `None` is distinct from False on purpose: outside a repository the question has no answer, and
    reporting it as "not ignored" would produce a durability warning that means nothing.
    """
    try:
        result = subprocess.run(["git", "check-ignore", "-q", str(path)],
                                capture_output=True, timeout=10)
    except (OSError, subprocess.SubprocessError):
        return None
    if result.returncode == 0:
        return True
    if result.returncode == 1:
        return False
    return None


def review(units: list[dict], superseded: list[dict]) -> list[str]:
    """An advisory decay report. Never fails the run."""
    lines: list[str] = []
    stale: list[dict] = []
    for unit in units:
        age = days_since(unit.get("updated"))
        if age is None:
            continue
        limit = STALE_AFTER_DAYS_OUTCOME if placeholder(unit.get("question")) else STALE_AFTER_DAYS
        if age > limit:
            stale.append(unit)
    if stale:
        lines.append(f"stale ({len(stale)} unit(s) past their recheck window):")
        for unit in sorted(stale, key=lambda u: -(days_since(u.get("updated")) or 0)):
            lines.append(f"  {unit['path']} — {days_since(unit.get('updated'))}d since updated")
    if superseded:
        lines.append(f"superseded: {len(superseded)} copy/copies on disk, unlisted and immutable")
    return lines


def cell(value) -> str:
    text = "" if value is None else str(value)
    return text.replace("|", "\\|").replace("\n", " ")


def render(units: list[dict]) -> str:
    lines = [
        "# Memories — INDEX",
        "",
        "Generated by `.agents/skills/ai-memory-context/scripts/memory_index.py`. Do not hand-edit.",
        "",
        "Grouped by category for browsing; every memory is listed individually because knowledge is",
        "retrieved by the **question** it answers, not by the category that happened to hold it.",
        "",
        "One row per slug, resolving to its **newest** version. A slug repeated across run folders is",
        "a version chain; superseded copies stay on disk, unlisted. When two memories conflict, the",
        "newer wins — but the system outranks both.",
        "",
    ]
    if not units:
        lines += ["_No memories encoded yet._", ""]
        return "\n".join(lines)

    categories: dict[str, list[dict]] = {}
    for unit in units:
        categories.setdefault(unit["subject"], []).append(unit)
    for category in sorted(categories):
        lines += [
            f"## {category}", "",
            "| Memory | Description | Question | Scope | Confidence | Updated |",
            "|--------|-------------|----------|-------|------------|---------|",
        ]
        for unit in categories[category]:
            lines.append(
                f"| [`{unit['folder']}`](./{unit['path']}) | {cell(unit.get('description'))} "
                f"| {cell(unit.get('question'))} | {cell(unit.get('scope'))} "
                f"| {cell(unit.get('confidence'))} | {cell(unit.get('updated'))} |"
            )
        lines.append("")
    return "\n".join(lines)


USAGE = f"""usage: memory_index.py [store-dir] [--review]
       memory_index.py --run-folder [SESSION_ID]
       memory_index.py --stamp

Regenerate INDEX.md from the <yyyyMMdd-HHmm>-<session-id>/<category>/<slug>{UNIT_SUFFIX} units.
A slug repeated across run folders is a version chain: the newest stamp is indexed, older copies stay
on disk unlisted and exempt from validation.
Defaults to {DEFAULT_STORE}. --review adds an advisory decay report.
--run-folder prints the COMPLETE run folder in one call, for a session id given as an argument or
taken from the environment. Call it ONCE per export and reuse the value for every unit: the stamp is
resolved there, so a run cannot straddle a minute boundary. An id outside [A-Za-z0-9._-] is rewritten
with a digest appended, since a separator would nest the units and drop them from the index.
--stamp prints the bare creation stamp (yyyyMMdd-HHmm).

There is no --publish: this store is local-first, and its durability boundary is the user's own
filesystem or VCS, not a transport. See the skill's LADR-001.

Exit codes: 0 clean · 1 validation problems (index still written) · 2 store not found or bad arguments."""


def _run_folder(session: str | None) -> int:
    resolved = resolve_session(session)
    if not resolved:
        print("--run-folder needs a session id: pass one, or set one of "
              f"{', '.join(SESSION_ID_VARS)}", file=sys.stderr)
        return 2
    print(run_folder_name(resolved))
    return 0


def main(argv: list[str]) -> int:
    args = argv[1:]
    if args == ["--stamp"]:
        print(datetime.now(timezone.utc).strftime("%Y%m%d-%H%M"))
        return 0
    if args and args[0] == "--run-folder":
        if len(args) > 2:
            print(f"--run-folder takes at most one session id\n\n{USAGE}", file=sys.stderr)
            return 2
        return _run_folder(args[1] if len(args) == 2 else None)
    if args and args[0] in ("-h", "--help"):
        print(USAGE)
        return 0

    wants_review = "--review" in args
    args = [a for a in args if a != "--review"]
    unknown = [a for a in args if a.startswith("-")]
    if unknown:
        print(f"unknown option(s): {' '.join(unknown)}\n\n{USAGE}", file=sys.stderr)
        return 2
    if len(args) > 1:
        print(f"expected at most one store directory\n\n{USAGE}", file=sys.stderr)
        return 2

    store = Path(args[0]) if args else DEFAULT_STORE
    if not store.is_dir():
        print(f"{store} is not a directory — nothing to index", file=sys.stderr)
        return 2

    current, superseded, problems = load_units(store)
    (store / "INDEX.md").write_text(render(current), encoding="utf-8")

    for problem in problems:
        print(f"  problem: {problem}")
    ignored = is_gitignored(store)
    if ignored is True:
        print(f"note: {store} is gitignored — durability is your filesystem or VCS, not this skill")
    elif ignored is None:
        print(f"note: git could not answer whether {store} is ignored (no repository here)")

    print(f"indexed {len(current)} memor(ies) across "
          f"{len({u['subject'] for u in current})} categor(ies), "
          f"{len(superseded)} superseded copy/copies unlisted -> {store / 'INDEX.md'}")
    if wants_review:
        for line in review(current, superseded):
            print(line)
    if problems:
        print(f"{len(problems)} problem(s)", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))