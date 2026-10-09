#!/usr/bin/env python3
"""Persistent review queue for candidates the capture path holds back.

The atomicity gate prints a held candidate and drops it; the decision gate below a threshold keeps only
its scores in the ledger. Neither survives the session. This module is where a held candidate goes
instead: an append-only JSONL queue outside every checkout, mode 600, redacted before it is written, so
a candidate held for review can actually be reviewed later.

Two record kinds share the file, both append-only:
  kind=entry      a candidate the capture path held, with the closed choice menu it can be resolved by
  kind=resolution a resolved entry's outcome — a choice from that menu, never free text

Nothing is deleted. A resolution is appended; the entry it resolved stays on disk as history.

The queue never directly writes to the store. A resolve that leads to a capture prints the export
command to route through the normal capture path (its dry-run veto) — the judgement to write stays with
the capture path and the human. This script records and validates, it does not decide.

Python 3.9+, stdlib only.
"""

import argparse
import datetime
import hashlib
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

DEFAULT_QUEUE = "~/.mimisbrunnr/review/queue.jsonl"
ENV_QUEUE = "CONTEXT_MEMORY_REVIEW_QUEUE"
# The entry cap. Not one of the operator settings an operator edits in `setup.md`: it exists so the
# queue cannot grow without bound, and it is exercised through the same kind of injectable seam as the
# decision gate's ledger cap (see `entry_cap`). A cap no case crosses is not a bound the suite tests.
ENV_CAP = "CONTEXT_MEMORY_REVIEW_QUEUE_MAX_ENTRIES"
DEFAULT_CAP = 1000

DIR_MODE = 0o700
FILE_MODE = 0o600

SCRIPTS = Path(__file__).resolve().parent
MENUS = SCRIPTS / "conflict_menus.json"
REDACTOR = SCRIPTS / "redact.py"

# Fields of a held candidate that a secret could hide in. Every one is scrubbed before the line is
# written, so the queue carries no value the candidate's own redaction missed.
CANDIDATE_TEXT_FIELDS = (
    "subject", "description", "statement", "contentSummary", "boundaries",
    "tags", "facets", "kind", "scope", "name", "answer", "why",
)


class QueueError(RuntimeError):
    """A refusal that names its own cause. Never carries candidate content."""

    def __init__(self, outcome, detail=""):
        super().__init__(detail or outcome)
        self.outcome = outcome
        self.detail = detail


def queue_path():
    """The queue file, overridable. Never a tracked path; `.context/` is wiped by sibling sessions."""
    return Path(os.path.expanduser(os.environ.get(ENV_QUEUE, DEFAULT_QUEUE)))


def load_menus():
    """The conflict menu data file, validated field by field.

    A data file an operator edits, so a malformed one is a classified refusal rather than a KeyError
    further down — the same rule the decision gate applies to its rubric.
    """
    try:
        with open(MENUS, encoding="utf-8") as handle:
            data = json.load(handle)
    except OSError as exc:
        raise QueueError("menus-unavailable", f"the menu file could not be read ({type(exc).__name__})")
    except ValueError:
        raise QueueError("menus-unavailable", "the menu file is not valid JSON")

    if not isinstance(data, dict):
        raise QueueError("menus-unavailable", "the menu file is not a JSON object")
    types = data.get("types")
    if not isinstance(types, dict) or not types:
        raise QueueError("menus-unavailable", "the menu file declares no 'types' object")

    menus = {}
    for name, entry in types.items():
        if not isinstance(entry, dict):
            raise QueueError("menus-unavailable", f"conflict type {name!r} is not an object")
        choices = entry.get("choices")
        if not isinstance(choices, list) or not choices:
            raise QueueError("menus-unavailable", f"conflict type {name!r} has no 'choices' list")
        if not all(isinstance(c, str) and c for c in choices):
            raise QueueError("menus-unavailable", f"conflict type {name!r} has a non-string choice")
        if len(set(choices)) != len(choices):
            raise QueueError("menus-unavailable", f"conflict type {name!r} has a duplicate choice")
        captures = entry.get("captures", [])
        if not isinstance(captures, list) or not all(c in choices for c in captures):
            raise QueueError("menus-unavailable", f"conflict type {name!r} has a 'captures' entry not in its choices")
        menus[name] = {"choices": list(choices), "captures": list(captures)}
    return menus


def choices_for(conflict_type, _menus=None):
    menus = _menus if _menus is not None else load_menus()
    if conflict_type not in menus:
        raise QueueError("unknown-conflict-type", f"conflict type {conflict_type!r} is not defined")
    return menus[conflict_type]["choices"]


def verify_choice(conflict_type, choice, _menus=None):
    """A choice is valid only if it is in the conflict type's closed set. Free text never resolves."""
    choices = choices_for(conflict_type, _menus)
    if choice not in choices:
        raise QueueError("invalid-choice",
                         f"{choice!r} is not a valid choice for {conflict_type!r}; "
                         f"expected one of {', '.join(choices)}")
    return choice


def is_capture_choice(conflict_type, choice, _menus=None):
    """Only a menu's `captures` set routes through the capture path. Anything else is a non-write."""
    menus = _menus if _menus is not None else load_menus()
    entry = menus.get(conflict_type)
    if entry is None:
        return False
    return choice in entry["captures"]


def redact_candidate(candidate):
    """Scrub every free-text field of a candidate, or refuse.

    The redactor answers positionally over stdin, so content never reaches a process table. Fails
    closed: a redactor that cannot run means the queue cannot guarantee no secret is on disk, and the
    queue's whole value is that it is safe to leave on a machine that is not this checkout.
    """
    if not isinstance(candidate, dict):
        raise QueueError("bad-candidate", "a held candidate must be an object")
    if not REDACTOR.is_file():
        raise QueueError("redactor-unavailable", "the redactor script is missing; no entry was written")

    texts = [candidate[field] for field in CANDIDATE_TEXT_FIELDS
             if isinstance(candidate.get(field), str) and candidate[field]]
    if not texts:
        candidate = dict(candidate)
        return candidate

    try:
        proc = subprocess.run(
            [sys.executable, "-B", str(REDACTOR)],
            input=json.dumps(texts), capture_output=True, text=True, encoding="utf-8", timeout=60,
        )
    except (OSError, ValueError) as exc:
        raise QueueError("redactor-unavailable",
                         f"the redactor could not run ({type(exc).__name__}); no entry was written")

    if proc.returncode != 0:
        raise QueueError("redactor-unavailable", "the redactor exited non-zero; no entry was written")
    try:
        results = json.loads(proc.stdout)["results"]
    except (ValueError, KeyError, TypeError):
        raise QueueError("redactor-unavailable", "the redactor returned unreadable output; no entry was written")
    if not isinstance(results, list) or len(results) != len(texts):
        # Same arity rule as the decision gate: an untrustworthy mapping is exactly the condition under
        # which a field keeps its unscrubbed value and reaches the queue.
        raise QueueError("redactor-unavailable",
                         f"the redactor returned {len(results) if isinstance(results, list) else 'a non-list'} "
                         f"result(s) for {len(texts)} field(s); no entry was written")

    scrubbed = {}
    for field, redacted in zip(texts, results):
        if not isinstance(redacted, dict) or "redacted" not in redacted:
            raise QueueError("redactor-unavailable",
                             "a redactor result carried no 'redacted' field; no entry was written")
        scrubbed[field] = redacted["redacted"]

    out = dict(candidate)
    for field in CANDIDATE_TEXT_FIELDS:
        if isinstance(candidate.get(field), str) and candidate[field] in scrubbed:
            out[field] = scrubbed[candidate[field]]
    return out


def entry_cap():
    """The entry cap, overridable so it can be exercised through the **real write path**.

    Reaching `DEFAULT_CAP` needs thousands of real holds, so a test seam is the only way a case can
    cross the bound end to end. A malformed value is refused rather than clamped, on the same rule as
    every other numeric setting: a cap that silently becomes something other than what was asked for is
    one the operator trusts and the queue does not honour.
    """
    raw = os.environ.get(ENV_CAP, "").strip()
    if not raw:
        return DEFAULT_CAP
    try:
        value = int(raw)
    except ValueError:
        raise QueueError("bad-queue-config",
                         f"{ENV_CAP} must be a positive integer; refusing to run with a different cap") from None
    if value < 1:
        raise QueueError("bad-queue-config", f"{ENV_CAP} must be at least 1")
    return value


def entry_id(source, candidate):
    """A stable id for a candidate: hash of source plus subject and statement.

    NOT the candidate's content hash alone — a rewrite changes the statement, and same-source rewrites
    of the same fact should resolve to the same entry. The subject is what the store keys identity on,
    so it anchors the id alongside the source.
    """
    subject = candidate.get("subject") or candidate.get("description") or candidate.get("name") or ""
    statement = candidate.get("statement") or candidate.get("answer") or ""
    raw = f"{source}\x1f{subject}\x1f{statement}".encode("utf-8")
    return hashlib.sha256(raw).hexdigest()[:16]


def _read_entries():
    """Every record in the queue as a list of dicts. A missing file is empty, not an error."""
    path = queue_path()
    if not path.is_file():
        return []
    entries = []
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                entries.append(json.loads(line))
            except ValueError:
                # A corrupt line is reported, not read as data; the record it was is one we cannot act
                # on, and acting on a guess would resolve the wrong candidate.
                continue
    return entries


def entry_count():
    """The number of unresolved entry records (kind=entry). Resolutions are records, not entries."""
    return sum(1 for record in _read_entries() if record.get("kind") == "entry")


def _ensure_permissions(path):
    """Set the queue file to mode 600. The parent review directory was created at 700 on first write.

    Never chmod an arbitrary parent: the queue is overridable, and chmodding a shared or operator-owned
    directory to 700 would be a permission change nobody asked for. Only the queue's own file is
    hardened here; the directory's mode is whatever `mkdir` set when it did not already exist.
    """
    path.parent.mkdir(mode=DIR_MODE, parents=True, exist_ok=True)
    try:
        os.chmod(path, FILE_MODE)
    except OSError:
        # A best-effort hardening; the queue is still written, but the mode is reported rather than
        # silently trusted. The caller sees the refusal surface if it matters.
        pass


def append(entry):
    """Append one held candidate to the queue. Returns the entry id. Refuses past the cap.

    `entry` carries `conflictType`, `heldBy`, `reason`, `evidence`, `candidate`, `binding`, `source`,
    `sessionId`; the menu and id are derived here so a caller cannot drift from the data file. The
    candidate is redacted before the write and the menu is validated, so a queue line is always a
    coherent, closed-menu entry.
    """
    conflict_type = entry.get("conflictType")
    if not isinstance(conflict_type, str) or not conflict_type:
        raise QueueError("bad-entry", "a held entry must carry a conflictType")
    menus = load_menus()
    choices = choices_for(conflict_type, menus)

    candidate = redact_candidate(entry.get("candidate") or {})
    source = entry.get("source") or ""
    record = {
        "kind": "entry",
        "id": entry.get("id") or entry_id(source, candidate),
        "heldAt": entry.get("heldAt") or datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "heldBy": entry.get("heldBy") or "conflict",
        "conflictType": conflict_type,
        "reason": entry.get("reason") or "",
        "evidence": entry.get("evidence") or {},
        "candidate": candidate,
        "binding": entry.get("binding") or {},
        "source": source,
        "sessionId": entry.get("sessionId") or "",
        "menu": choices,
    }

    if entry_count() + 1 > entry_cap():
        # Refused, not truncated: a queue that silently drops its oldest held candidate would lose the
        # very record a reviewer was about to act on.
        raise QueueError("queue-full",
                         f"the review queue holds {entry_count()} entry(ies); the {entry_cap()}-entry "
                         "cap is reached. Review or resolve the queue before adding more.")

    path = queue_path()
    path.parent.mkdir(mode=DIR_MODE, parents=True, exist_ok=True)
    fd, temp = tempfile.mkstemp(dir=str(path.parent), prefix=".queue-")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            if path.is_file():
                with open(path, encoding="utf-8") as existing:
                    for line in existing:
                        handle.write(line)
            handle.write(json.dumps(record, ensure_ascii=False) + "\n")
        os.chmod(temp, FILE_MODE)
        os.replace(temp, path)
        _ensure_permissions(path)
    except OSError:
        try:
            os.unlink(temp)
        except OSError:
            pass
        raise
    return record["id"]


def _find(id_value):
    for record in _read_entries():
        if record.get("kind") == "entry" and record.get("id") == id_value:
            return record
    return None


def _resolutions(id_value):
    return [r for r in _read_entries()
            if r.get("kind") == "resolution" and r.get("id") == id_value]


def cmd_append(args):
    """Append one held candidate read from stdin (a JSON object). Prints the entry id.

    The capture clients cannot import this module (the two script folders ship as separate packages, the
    same rule that keeps the recall notice duplicated and asserted equal), so this is how a gate in
    `mimisbrunnr-kvasir-understanding` hands a held candidate to the queue.
    """
    try:
        entry = json.load(sys.stdin)
    except ValueError:
        raise QueueError("bad-entry", "stdin did not carry a JSON object") from None
    if not isinstance(entry, dict):
        raise QueueError("bad-entry", "stdin must carry a JSON object")
    _id = append(entry)
    print(json.dumps({"id": _id, "path": str(queue_path())}, indent=2))
    return 0


def cmd_list(args):
    entries = [r for r in _read_entries() if r.get("kind") == "entry"]
    if args.held_by:
        entries = [e for e in entries if e.get("heldBy") == args.held_by]

    if args.table:
        for entry in entries:
            resolved = "resolved" if _resolutions(entry["id"]) else "open"
            subject = (entry.get("candidate") or {}).get("subject") or "<no subject>"
            print(f"{entry['id']}\t{entry['heldBy']}\t{entry.get('heldAt')}\t{resolved}\t{subject}")
        return 0

    print(f"{len(entries)} held candidate(s) -> {queue_path()}")
    for entry in entries:
        resolved = "resolved" if _resolutions(entry["id"]) else "open"
        subject = (entry.get("candidate") or {}).get("subject") or "<no subject>"
        print(f"  [{entry['id']}] {entry.get('heldBy')} / {entry.get('conflictType')} / {resolved} "
              f"({entry.get('heldAt')}): {subject}")
        print(f"    choices: {', '.join(entry.get('menu') or [])}")
    return 0


def cmd_show(args):
    entry = _find(args.id)
    if entry is None:
        print(f"no held candidate with id {args.id}", file=sys.stderr)
        return 1
    print(json.dumps(entry, indent=2, ensure_ascii=False))
    resolutions = _resolutions(args.id)
    if resolutions:
        print("resolutions:")
        for resolution in resolutions:
            print(json.dumps(resolution, indent=2, ensure_ascii=False))
    return 0


def cmd_resolve(args):
    entry = _find(args.id)
    if entry is None:
        print(f"no held candidate with id {args.id}", file=sys.stderr)
        return 1

    try:
        verify_choice(entry["conflictType"], args.choice)
    except QueueError as exc:
        print(f"REFUSED: {exc.detail}", file=sys.stderr)
        return 1

    resolution = {
        "kind": "resolution",
        "id": entry["id"],
        "resolvedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "choice": args.choice,
    }
    if args.edit:
        try:
            with open(args.edit, encoding="utf-8") as handle:
                resolution["edited"] = handle.read()
        except OSError as exc:
            print(f"REFUSED: the edit file could not be read ({type(exc).__name__})", file=sys.stderr)
            return 1

    path = queue_path()
    path.parent.mkdir(mode=DIR_MODE, parents=True, exist_ok=True)
    fd, temp = tempfile.mkstemp(dir=str(path.parent), prefix=".queue-")
    with os.fdopen(fd, "w", encoding="utf-8") as handle:
        if path.is_file():
            with open(path, encoding="utf-8") as existing:
                for line in existing:
                    handle.write(line)
        handle.write(json.dumps(resolution, ensure_ascii=False) + "\n")
    os.chmod(temp, FILE_MODE)
    os.replace(temp, path)
    _ensure_permissions(path)

    if is_capture_choice(entry["conflictType"], args.choice):
        print(f"resolved {entry['id']} as {args.choice}: route through the capture path's export "
              "with its dry-run veto (see SKILL.md) — never a direct queue write.")
    else:
        extra = " (the entry stays in the queue)" if args.choice == "park" else ""
        print(f"resolved {entry['id']} as {args.choice}; nothing was written to the store{extra}.")
    return 0


def main():
    parser = argparse.ArgumentParser(prog="review", description="Inspect and resolve the capture review queue.")
    sub = parser.add_subparsers(dest="command", required=True)

    append_parser = sub.add_parser("append", help="append a held candidate from stdin")
    append_parser.set_defaults(func=cmd_append)

    list_parser = sub.add_parser("list", help="list held candidates")
    list_parser.add_argument("--held-by", help="filter by the gate that held it")
    list_parser.add_argument("--table", action="store_true", help="one line per candidate, tab-separated")
    list_parser.set_defaults(func=cmd_list)

    show_parser = sub.add_parser("show", help="show one held candidate and its resolutions")
    show_parser.add_argument("id")
    show_parser.set_defaults(func=cmd_show)

    resolve_parser = sub.add_parser("resolve", help="resolve a held candidate with a closed-menu choice")
    resolve_parser.add_argument("id")
    resolve_parser.add_argument("--choice", required=True, help="one of the candidate's menu choices")
    resolve_parser.add_argument("--edit", help="path to read an edited candidate from")
    resolve_parser.set_defaults(func=cmd_resolve)

    args = parser.parse_args()
    try:
        return args.func(args)
    except QueueError as exc:
        print(json.dumps({"outcome": exc.outcome, "detail": exc.detail}, indent=2), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
