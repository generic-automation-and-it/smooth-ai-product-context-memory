#!/usr/bin/env python3
"""Session marker — the import sets the working point, export cuts at it (HLD-007 LADR-11).

A marked import records references (ids + versions/subjects, or selector + as-of),
never memory content, in a per-skill client-side file keyed by the harness session id:

    .context/session-markers/<type>-<session-id>.json   type in kvasir|odin|ai-understanding

The last marker of any verb (import or export) is the boundary; every marked
entry's ids stay excluded. A default export drops those ids before preflight;
``export --all`` ignores the marker for selection (a successful write still records);
``--nomarker`` entries are session history and flow into export. A candidate that
changes an excluded item is a supersession, never dropped.

Session id resolution (first non-empty wins, else explicit ``--session``, else no marker):

    CLAUDE_CODE_SESSION_ID, CODEX_THREAD_ID, OPENCODE_SESSION_ID,
    COPILOT_AGENT_SESSION_ID, PI_SESSION_ID

This module is a WET copy across the three skill script dirs (kvasir, odin,
ai-understanding) — the same reason the recall notice and the decision rubric are
copied rather than imported: the script folders ship as separate packages.
Keep the three copies byte-identical; the harness asserts it.

No secrets here: session ids are not secrets but are not logged beyond the
``Marker:`` line, and the marker file holds references only.
"""

from __future__ import annotations

import datetime as dt
import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

MARKER_TYPES = ("kvasir", "odin", "ai-understanding")

# (env var, harness name) in resolution order — LADR-11.
SESSION_ENV_CHAIN = (
    ("CLAUDE_CODE_SESSION_ID", "claude-code"),
    ("CODEX_THREAD_ID", "codex"),
    ("OPENCODE_SESSION_ID", "opencode"),
    ("COPILOT_AGENT_SESSION_ID", "copilot"),
    ("PI_SESSION_ID", "pi"),
)

_MARKER_DIRNAME = "session-markers"
_SAFE_SESSION_RE = re.compile(r"[^A-Za-z0-9._-]+")


def resolve_session(explicit: str | None = None,
                    env: dict | None = None) -> tuple[str, str]:
    """Return (session_id, harness). Empty id means no marker.

    ``explicit`` (--session) beats the environment; the env chain beats nothing.
    """
    if explicit is not None and str(explicit).strip():
        return str(explicit).strip(), "explicit"
    source = env if env is not None else os.environ
    for name, harness in SESSION_ENV_CHAIN:
        try:
            value = source.get(name, "")
        except AttributeError:
            value = ""
        if isinstance(value, str) and value.strip():
            return value.strip(), harness
    return "", ""


def marker_root() -> Path:
    """Directory holding marker files: ``.context/session-markers`` under the git
    top level (else the working directory). ``SESSION_MARKER_ROOT`` overrides for
    tests; production never sets it."""
    override = os.environ.get("SESSION_MARKER_ROOT", "").strip()
    if override:
        return Path(override)
    try:
        proc = subprocess.run(["git", "rev-parse", "--show-toplevel"],
                              capture_output=True, text=True, timeout=10)
    except (OSError, subprocess.SubprocessError):
        proc = None
    top = (Path(proc.stdout.strip()) if proc is not None and proc.returncode == 0
           and proc.stdout.strip() else Path.cwd())
    return top / ".context" / _MARKER_DIRNAME


def marker_path(marker_type: str, session: str, root: Path | None = None) -> Path:
    if marker_type not in MARKER_TYPES:
        raise ValueError(f"unknown marker type {marker_type!r}")
    safe = _SAFE_SESSION_RE.sub("_", session).strip("._") or "session"
    base = root if root is not None else marker_root()
    return base / f"{marker_type}-{safe}.json"


def _now_iso() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")


def load_marker(marker_type: str, session: str,
                root: Path | None = None) -> dict | None:
    path = marker_path(marker_type, session, root)
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    if not isinstance(data, dict) or data.get("type") != marker_type:
        return None
    return data


def exclusion_set(marker: dict | None) -> tuple[set[str], dict[str, str], str, int]:
    """Return (ids, subject->id, boundary, count) for a loaded marker.

    Only marked (nomarker == false) imports contribute. Marked wins
    order-independent: an id imported both marked and --nomarker stays excluded.
    """
    if not isinstance(marker, dict):
        return set(), {}, "", 0
    ids: set[str] = set()
    subjects: dict[str, str] = {}
    for entry in marker.get("imports") or []:
        if not isinstance(entry, dict) or entry.get("nomarker"):
            continue
        for item in entry.get("items") or []:
            if not isinstance(item, dict):
                continue
            ident = item.get("id")
            if isinstance(ident, str) and ident:
                ids.add(ident)
                subject = item.get("subject")
                if isinstance(subject, str) and subject and subject not in subjects:
                    subjects[subject] = ident
    boundary = marker.get("boundary") if isinstance(marker.get("boundary"), str) else ""
    return ids, subjects, boundary, len(ids)


def exclusion_slugs(marker: dict | None) -> set[str]:
    """File-identity exclusions: slugs recorded by dump / ai-understanding exports.

    Store exports record {id, version}; file scopes record {slug} (dump: {folder, slugs},
    ai-understanding: {path, slug} — LADR-11 "Identity per target"). Both move the boundary;
    both exclude. Marked-wins applies within each namespace.
    """
    slugs: set[str] = set()
    if not isinstance(marker, dict):
        return slugs
    for entry in marker.get("imports") or []:
        if not isinstance(entry, dict) or entry.get("nomarker"):
            continue
        for item in entry.get("items") or []:
            if isinstance(item, dict) and not item.get("id"):
                slug = item.get("slug")
                if isinstance(slug, str) and slug:
                    slugs.add(slug)
        for slug in entry.get("slugs") or []:
            if isinstance(slug, str) and slug:
                slugs.add(slug)
    return slugs


def boundary_line(marker: dict | None, ignored: bool = False) -> str:
    """One-line digest for export: marker time + count, none, or ignored (--all)."""
    if ignored:
        return "Boundary: ignored (--all)"
    if not isinstance(marker, dict):
        return "Boundary: none"
    _, _, boundary, count = exclusion_set(marker)
    total = count + len(exclusion_slugs(marker))
    if not boundary or not total:
        # No marker, or only --nomarker / id-less imports: default export == --all.
        return "Boundary: none"
    return f"Boundary: marker {boundary}, {total} excluded"


def record_import(marker_type: str, session: str, harness: str, verb: str,
                  nomarker: bool, items: list[dict] | None = None,
                  selector: dict | None = None, as_of: str | None = None,
                  root: Path | None = None,
                  at: str | None = None,
                  folder: str | None = None, slugs: list[str] | None = None,
                  path: str | None = None) -> tuple[str, str]:
    """Append one import entry to the marker file (atomic temp+rename).

    ``items`` are [{id, version, subject}] references — never content.
    ``selector`` + ``as_of`` record an --all-kinds or selector-wide import
    (replayable because the store is append-only); they move the boundary
    without adding id exclusions. Id-less imports are history: logged with
    empty items and no boundary move.

    Returns (path_str, status) where status is one of:
    ``set`` (new marker with a boundary), ``extended`` (appended),
    ``history`` (logged, no boundary change), ``skipped`` (--nomarker).
    """
    mpath = marker_path(marker_type, session, root)
    now = at or _now_iso()
    clean_items = []
    for item in items or []:
        if not isinstance(item, dict):
            continue
        ident = item.get("id")
        if isinstance(ident, str) and ident:
            clean_items.append({
                "id": ident,
                "version": item.get("version"),
                "subject": item.get("subject") or "",
            })
            continue
        slug = item.get("slug")
        if isinstance(slug, str) and slug:
            clean_items.append({"slug": slug, "subject": item.get("subject") or ""})
    clean_slugs = [s for s in (slugs or []) if isinstance(s, str) and s]
    entry: dict = {
        "at": now,
        "verb": verb,
        "nomarker": bool(nomarker),
        "items": clean_items,
    }
    if selector:
        entry["selector"] = selector
    if as_of:
        entry["as_of"] = as_of
    if folder:
        entry["folder"] = folder
    if clean_slugs:
        entry["slugs"] = clean_slugs
    if path:
        entry["path"] = path

    existed = mpath.is_file()
    marker = load_marker(marker_type, session, root) if existed else None
    if existed and marker is None:
        # Fail closed: an unreadable marker still names real exclusions, so it is
        # moved aside (never silently replaced) and the loss is said aloud. The new
        # entry starts fresh; the old file stays next to it for inspection.
        aside = mpath.with_name(
            mpath.name + ".corrupt-"
            + dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ"))
        try:
            os.replace(mpath, aside)
        except OSError:
            pass
        print(f"WARNING: session marker {mpath} unreadable; moved aside to {aside} "
              f"and starting fresh (earlier exclusions no longer apply).",
              file=sys.stderr)
    if marker is None:
        marker = {
            "type": marker_type,
            "session": session,
            "harness": harness,
            "boundary": "",
            "imports": [],
        }
    marker["imports"].append(entry)

    moves_boundary = (not nomarker) and bool(clean_items or selector or folder
                                        or clean_slugs or path)
    if moves_boundary:
        marker["boundary"] = now
        status = "extended" if existed and any(
            isinstance(e, dict) and not e.get("nomarker") and
            (e.get("items") or e.get("selector") or e.get("folder")
             or e.get("slugs") or e.get("path"))
            for e in marker.get("imports", [])[:-1]
        ) else "set"
    elif nomarker:
        status = "skipped"
    else:
        status = "history"

    # Owner-only like the scratch folder: session ids are not secrets, but they sit
    # in these filenames, so other accounts get no read. Repair unconditionally
    # (mkdir -m sets the mode only on creation); a chmod failure raises before
    # anything is written, so a marker is never left exposed.
    mpath.parent.mkdir(parents=True, exist_ok=True)
    os.chmod(mpath.parent, 0o700)
    fd, tmp = tempfile.mkstemp(dir=str(mpath.parent), prefix=mpath.name + ".",
                               suffix=".tmp")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            json.dump(marker, handle, indent=2, ensure_ascii=False)
            handle.write("\n")
        os.replace(tmp, mpath)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise
    return str(mpath), status


def append_export(marker_type: str, session: str, harness: str,
                  items: list[dict] | None = None,
                  folder: str | None = None, slugs: list[str] | None = None,
                  path: str | None = None, root: Path | None = None,
                  at: str | None = None) -> tuple[str, str]:
    """Append one export receipt entry (E1, E6): only what was actually written.

    Thin wrapper over record_import with verb="export". Call after a confirmed write
    (never the plan): dry runs, refusals, empty sets and review-queue deferrals pass
    nothing and therefore record nothing. Returns (path, status) like record_import.
    """
    return record_import(marker_type, session, harness, "export", False,
                         items, None, None, root, at,
                         folder=folder, slugs=slugs, path=path)


def marker_line(status: str, session: str, count: int = 0, verb: str = "import") -> str:
    """One-line receipt. Status from record_import (or 'none').

    Export receipts name the verb (`Marker: set (export, N items)`); import receipts
    keep the exclusion wording. `skipped` covers `--nomarker` (both verbs);
    `history`/`none` never move the boundary.
    """
    if verb == "export":
        if status in ("set", "extended"):
            return f"Marker: {status} (export, {count} item(s))"
        if status == "skipped":
            return "Marker: skipped (--nomarker; boundary and exclusions unchanged)"
        if status in ("empty", "history"):
            return "Marker: skipped (nothing written)"
        return "Marker: none (no session id)"
    if status == "set":
        return f"Marker: set ({count} id(s) excluded on default export)"
    if status == "extended":
        return (f"Marker: extended (boundary moved; {count} id(s) excluded "
                f"on default export)")
    if status == "skipped":
        return "Marker: skipped (--nomarker; flows into default export)"
    if status == "history":
        return "Marker: none (id-less history; no boundary change)"
    return "Marker: none (no session id)"


# ----------------------------------------------------------------------------- cli


def _cli_record(argv: list[str]) -> int:
    import argparse as _ap
    parser = _ap.ArgumentParser(prog="session_marker record")
    parser.add_argument("--type", required=True, choices=list(MARKER_TYPES))
    parser.add_argument("--session", default=None)
    parser.add_argument("--verb", default="import")
    parser.add_argument("--nomarker", action="store_true")
    parser.add_argument("--item", action="append", default=[],
                        help="id[:version[:subject]], repeatable (store identity)")
    parser.add_argument("--slug", action="append", default=[],
                        help="slug[::subject], repeatable (file identity: dump / ai-understanding)")
    parser.add_argument("--folder", default=None, help="dump folder name (moves the boundary)")
    parser.add_argument("--path", default=None, help="ai-understanding unit path")
    parser.add_argument("--selector", default=None, help="JSON object")
    parser.add_argument("--as-of", default=None)
    args = parser.parse_args(argv)
    sid, harness = resolve_session(args.session)
    if not sid:
        print(marker_line("none", ""))
        return 0
    items = []
    for raw in args.item:
        ident, _, rest = raw.partition(":")
        version_str, _, subject = rest.partition(":")
        try:
            version = int(version_str) if version_str else None
        except ValueError:
            version = None
        if ident:
            items.append({"id": ident, "version": version, "subject": subject})
    slugs = []
    for raw in args.slug:
        slug, _, subject = raw.partition("::")
        if slug:
            slugs.append(slug)
            if subject:
                items.append({"slug": slug, "subject": subject})
    selector = None
    if args.selector:
        try:
            selector = json.loads(args.selector)
        except ValueError:
            import sys as _sys_err
            print(f"REFUSED: --selector is not JSON: {args.selector[:100]}",
                  file=_sys_err.stderr)
            return 2
    import sys as _sys
    try:
        _, status = record_import(args.type, sid, harness, args.verb,
                                  args.nomarker, items, selector, args.as_of,
                                  folder=args.folder, slugs=slugs or None,
                                  path=args.path)
    except OSError as error:
        print(f"NOTE: session marker not written ({error}).", file=_sys.stderr)
        return 1
    marker = load_marker(args.type, sid)
    _, _, _, count = exclusion_set(marker)
    total = count + len(exclusion_slugs(marker))
    print(marker_line(status, sid, total, verb=args.verb))
    return 0


def _cli_boundary(argv: list[str]) -> int:
    import argparse as _ap
    parser = _ap.ArgumentParser(prog="session_marker boundary")
    parser.add_argument("--type", required=True, choices=list(MARKER_TYPES))
    parser.add_argument("--session", default=None)
    parser.add_argument("--all", action="store_true", dest="ignore")
    args = parser.parse_args(argv)
    if args.ignore:
        print(boundary_line(None, ignored=True))
        return 0
    sid, _ = resolve_session(args.session)
    if not sid:
        print(boundary_line(None))
        return 0
    print(boundary_line(load_marker(args.type, sid)))
    return 0


def _cli_show(argv: list[str]) -> int:
    import argparse as _ap
    parser = _ap.ArgumentParser(prog="session_marker show")
    parser.add_argument("--type", required=True, choices=list(MARKER_TYPES))
    parser.add_argument("--session", default=None)
    args = parser.parse_args(argv)
    sid, _ = resolve_session(args.session)
    if not sid:
        print("{}")
        return 0
    marker = load_marker(args.type, sid)
    print(json.dumps(marker or {}, indent=2, ensure_ascii=False))
    return 0


def main(argv: list[str] | None = None) -> int:
    import sys as _sys
    args = list(_sys.argv[1:] if argv is None else argv)
    if not args or args[0] not in ("record", "boundary", "show"):
        print("usage: session_marker.py {record|boundary|show} ...", file=_sys.stderr)
        return 2
    verb, rest = args[0], args[1:]
    if verb == "record":
        return _cli_record(rest)
    if verb == "boundary":
        return _cli_boundary(rest)
    return _cli_show(rest)


if __name__ == "__main__":
    import sys
    raise SystemExit(main())
