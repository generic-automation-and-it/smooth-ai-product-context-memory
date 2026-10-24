#!/usr/bin/env python3
"""L0 harness for the review queue, the conflict choice menus and staged dedup.

No decision model, no network beyond a loopback stub for the dedup model. The queue's own redaction and
the injected stage-2/3 seams make every acceptance criterion here deterministic — the properties that
matter (a held candidate persists instead of vanishing, a secret is scrubbed before the queue write, a
closed menu never accepts free text, a down model never reads as "no duplicate") are exactly the ones a
live model cannot pin.
"""

import argparse
import contextlib
import http.server
import io
import json
import os
import socket
import stat
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[2] / "_mimisbrunnr-shared" / "scripts"

# Hermetic before any import: the dedup module imports `decisions_gate`, which seeds settings from the
# machine credential file at import. Point it at a path that cannot exist so the harness never reads the
# operator's real credentials, and redirect the review queue to a temp path.
_TMP = tempfile.mkdtemp()
os.environ["CONTEXT_MEMORY_CREDENTIAL_FILE"] = str(Path(_TMP) / "no-credentials")
os.environ["CONTEXT_MEMORY_REVIEW_QUEUE"] = str(Path(_TMP) / "queue.jsonl")
os.environ["CONTEXT_MEMORY_DECISIONS_DEDUP_ENABLED"] = "false"
os.environ["CONTEXT_MEMORY_DECISIONS_ENABLED"] = "false"

sys.path.insert(0, str(SCRIPTS))
import importlib.util as _ilu

def _load(name):
    spec = _ilu.spec_from_file_location(name, SCRIPTS / f"{name}.py")
    module = _ilu.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

rq = _load("review_queue")
dedup = _load("dedup")

MENUS = SCRIPTS / "conflict_menus.json"

EXPECTED_MENUS = {
    "duplicate": ["version", "link", "skip"],
    "contradiction": ["keep-both", "supersede", "skip"],
    "cross-kind-subject": ["reclassify", "rename", "drop"],
    "held-atomic": ["split", "drop", "park"],
    "below-value": ["rewrite", "capture-as-proposed", "drop", "park"],
}


class StubHandler(http.server.BaseHTTPRequestHandler):
    """Answers /v1/systemone with the next probability from `probabilities`, per request."""

    def do_POST(self):  # noqa: N802 — fixed by BaseHTTPRequestHandler
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length) if length else b""
        try:
            body = json.loads(raw.decode("utf-8"))
        except ValueError:
            body = {}
        self.server.requests.append(body)
        probability = self.server.probabilities.pop(0) if self.server.probabilities else 0.0
        answers = {}
        for key in body.get("questions", {}):
            answers[key] = {"noul": probability}
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(json.dumps({"answers": answers}).encode("utf-8"))

    def log_message(self, *args):
        pass


class Stub:
    def __init__(self, probabilities):
        self.probabilities = probabilities
        self.requests = []
        self.server = http.server.HTTPServer(("127.0.0.1", 0), StubHandler)
        self.server.probabilities = probabilities
        self.server.requests = self.requests
        self.port = self.server.server_address[1]
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def url(self):
        return f"http://127.0.0.1:{self.port}"

    def close(self):
        self.server.shutdown()
        self.server.server_close()


class RawHandler(http.server.BaseHTTPRequestHandler):
    """Answers every POST with the server's fixed `(status, headers, body)`."""

    def do_POST(self):  # noqa: N802 — fixed by BaseHTTPRequestHandler
        length = int(self.headers.get("Content-Length") or 0)
        if length:
            self.rfile.read(length)
        self.server.requests.append(self.path)
        status, headers, body = self.server.response
        self.send_response(status)
        for name, value in headers.items():
            self.send_header(name, value)
        self.end_headers()
        self.wfile.write(body.encode("utf-8"))

    def log_message(self, *args):
        pass


class RawStub(Stub):
    def __init__(self, status, body, headers=None):
        self.requests = []
        self.server = http.server.HTTPServer(("127.0.0.1", 0), RawHandler)
        self.server.response = (status, headers or {"Content-Type": "application/json"}, body)
        self.server.requests = self.requests
        self.port = self.server.server_address[1]
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()


class ReviewQueueTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)
        self.queue = Path(self.dir.name) / "queue.jsonl"
        os.environ["CONTEXT_MEMORY_REVIEW_QUEUE"] = str(self.queue)
        os.environ.pop("CONTEXT_MEMORY_REVIEW_QUEUE_MAX_ENTRIES", None)

    def _entry(self, **overrides):
        entry = {
            "conflictType": "held-atomic",
            "heldBy": "atomicity",
            "reason": "bundled (tally)",
            "candidate": {"subject": "Storage engine", "statement": "Postgres and SQLite and Redis."},
            "source": "session.md",
            "sessionId": "s1",
        }
        entry.update(overrides)
        return entry

    def test_append_persists_an_entry(self):
        """An append writes exactly one record; the candidate survives on disk, not the terminal."""
        eid = rq.append(self._entry())
        lines = self.queue.read_text(encoding="utf-8").strip().splitlines()
        self.assertEqual(len(lines), 1)
        record = json.loads(lines[0])
        self.assertEqual(record["id"], eid)
        self.assertEqual(record["heldBy"], "atomicity")
        self.assertEqual(record["conflictType"], "held-atomic")
        self.assertEqual(record["menu"], ["split", "drop", "park"])
        self.assertEqual(record["candidate"]["subject"], "Storage engine")

    def test_append_redacts_a_secret(self):
        """A secret in a held candidate is scrubbed before the queue write, so the queue never leaves one."""
        candidate = {"subject": "creds", "statement": "the login is https://user:hunter2@example.com/x"}
        eid = rq.append(self._entry(candidate=candidate))
        text = self.queue.read_text(encoding="utf-8")
        self.assertNotIn("hunter2", text)
        self.assertIn("<redacted>", text.lower())

    def test_append_redacts_nested_lists_reason_and_evidence(self):
        """Every persisted string is scrubbed — list entries, nested objects, `reason` and `evidence` — not
        only a named set of top-level candidate fields."""
        secret = "https://user:hunter2@example.com/x"
        candidate = {"subject": "creds", "statement": "fine", "tags": [secret],
                     "sources": [{"reference": secret}]}
        rq.append(self._entry(candidate=candidate, reason=f"held: {secret}",
                              evidence={"matched": {"statement": secret}}))
        text = self.queue.read_text(encoding="utf-8")
        self.assertNotIn("hunter2", text)
        record = json.loads(text.strip().splitlines()[0])
        self.assertEqual(record["candidate"]["statement"], "fine", "a clean field is left as it was")
        self.assertIsInstance(record["candidate"]["tags"], list, "redaction keeps the record's shape")

    def test_append_refuses_when_the_redactor_cannot_run(self):
        """Fail closed: no redactor, no entry."""
        with patch.object(rq, "REDACTOR", Path(self.dir.name) / "missing.py"):
            with self.assertRaises(rq.QueueError) as ctx:
                rq.append(self._entry(reason="anything"))
        self.assertEqual(ctx.exception.outcome, "redactor-unavailable")
        self.assertFalse(self.queue.exists())

    def test_an_edited_resolution_is_redacted(self):
        """`resolve --edit` persists the rewrite beside the entry, so it is scrubbed like the entry."""
        eid = rq.append(self._entry(conflictType="below-value", heldBy="value"))
        edit = Path(self.dir.name) / "edit.txt"
        edit.write_text("rewritten: https://user:hunter2@example.com/x", encoding="utf-8")
        rc = self._run(["resolve", eid, "--choice", "rewrite", "--edit", str(edit)])
        self.assertEqual(rc, 0)
        text = self.queue.read_text(encoding="utf-8")
        self.assertNotIn("hunter2", text)
        self.assertIn("rewritten:", text)

    def test_a_resolution_closes_only_the_hold_it_followed(self):
        """A candidate held again after it was resolved is open again: it lists as open, and its new
        hold counts toward the cap. Resolving by id alone left it reading resolved and out of the count,
        so a reviewer was told the queue had room while a live hold sat in it."""
        eid = rq.append(self._entry())
        self.assertEqual(self._run(["resolve", eid, "--choice", "drop"]), 0)
        rq.append(self._entry())
        self.assertEqual(rq.entry_count(), 1, "the re-held candidate is an unresolved entry again")
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            self.assertEqual(rq.cmd_list(argparse.Namespace(held_by=None, table=True)), 0)
        states = [line.split("\t")[3] for line in buffer.getvalue().strip().splitlines()]
        self.assertEqual(states, ["resolved", "open"])

    def test_a_resolved_entry_frees_its_cap_slot(self):
        """The cap counts unresolved entries: resolving one is what the refusal tells a reviewer to do."""
        os.environ["CONTEXT_MEMORY_REVIEW_QUEUE_MAX_ENTRIES"] = "1"
        eid = rq.append(self._entry())
        self.assertEqual(self._run(["resolve", eid, "--choice", "drop"]), 0)
        self.assertEqual(rq.entry_count(), 0)
        rq.append(self._entry(candidate={"subject": "Another", "statement": "a second held fact"}))
        self.assertEqual(rq.entry_count(), 1)

    def test_concurrent_appends_lose_no_held_candidate(self):
        """Each append copies the file and replaces it; unserialised, the later replace erased the earlier
        record while both callers reported success."""
        count = 8
        barrier = threading.Barrier(count)
        errors = []

        def hold(index):
            try:
                barrier.wait()
                rq.append(self._entry(candidate={"subject": f"Subject {index}", "statement": f"Claim {index}."}))
            except Exception as exc:  # noqa: BLE001 — surfaced through the assertion below
                errors.append(exc)

        threads = [threading.Thread(target=hold, args=(i,)) for i in range(count)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()
        self.assertEqual(errors, [])
        subjects = {json.loads(line)["candidate"]["subject"]
                    for line in self.queue.read_text(encoding="utf-8").splitlines()}
        self.assertEqual(subjects, {f"Subject {i}" for i in range(count)})
        self.assertFalse(self.queue.with_name(self.queue.name + ".lock").exists(), "the lock is released")

    def test_a_held_lock_refuses_the_write_and_a_stale_one_is_cleared(self):
        lock = self.queue.with_name(self.queue.name + ".lock")
        lock.parent.mkdir(parents=True, exist_ok=True)
        lock.write_text("", encoding="utf-8")
        with patch.object(rq, "LOCK_WAIT_SECONDS", 0.2):
            with self.assertRaises(rq.QueueError) as ctx:
                rq.append(self._entry())
        self.assertEqual(ctx.exception.outcome, "queue-busy")
        self.assertFalse(self.queue.exists(), "a refused write leaves the queue as found")
        old = time.time() - rq.LOCK_STALE_SECONDS - 5
        os.utime(lock, (old, old))
        rq.append(self._entry())
        self.assertEqual(len(self.queue.read_text(encoding="utf-8").splitlines()), 1)

    def test_a_corrupt_line_refuses_every_read_and_write(self):
        """A skipped line hid a lost candidate and let the cap admit another hold."""
        rq.append(self._entry())
        with open(self.queue, "a", encoding="utf-8") as handle:
            handle.write("{not json\n[1, 2]\n")
        for action in (rq.entry_count, lambda: rq.append(self._entry(reason="another"))):
            with self.subTest(action=getattr(action, "__name__", "append")):
                with self.assertRaises(rq.QueueError) as ctx:
                    action()
                self.assertEqual(ctx.exception.outcome, "corrupt-queue")
        self.assertNotEqual(self._run(["list"]), 0)
        self.assertEqual(len(self.queue.read_text(encoding="utf-8").splitlines()), 3, "nothing was rewritten")

    def test_queue_file_is_600_and_outside_the_checkout(self):
        """Mode 600 and under the redirected path, never inside the repo tree."""
        rq.append(self._entry())
        self.assertEqual(stat.S_IMODE(self.queue.stat().st_mode), 0o600)
        self.assertNotIn("conductor/workspaces", str(self.queue))

    def test_cap_refuses_rather_than_truncating(self):
        """Crossing the cap refuses; the first entry is not evicted to make room."""
        os.environ["CONTEXT_MEMORY_REVIEW_QUEUE_MAX_ENTRIES"] = "1"
        rq.append(self._entry())
        with self.assertRaises(rq.QueueError) as ctx:
            rq.append(self._entry())
        self.assertEqual(ctx.exception.outcome, "queue-full")
        lines = self.queue.read_text(encoding="utf-8").strip().splitlines()
        self.assertEqual(len(lines), 1)

    def test_cap_seam_is_inert_when_unset(self):
        """Without the cap variable, the default holds; the seam is not always-on."""
        self.assertEqual(rq.entry_cap(), rq.DEFAULT_CAP)

    def test_invalid_choice_is_refused_as_free_text(self):
        """A resolve with a choice outside the menu is refused, never accepted."""
        eid = rq.append(self._entry())
        rc = self._run(["resolve", eid, "--choice", "whatever"])
        self.assertNotEqual(rc, 0)
        lines = self.queue.read_text(encoding="utf-8").strip().splitlines()
        self.assertEqual(len(lines), 1, "a refused resolve must not append a resolution")

    def test_unknown_conflict_type_fails(self):
        """A conflict type the data file does not define is refused, not silently given a menu."""
        with self.assertRaises(rq.QueueError):
            rq.append(self._entry(conflictType="nope"))

    def test_resolve_appends_a_resolution_without_deleting(self):
        """A resolution is appended; the entry it resolved stays on disk as history."""
        eid = rq.append(self._entry())
        rc = self._run(["resolve", eid, "--choice", "drop"])
        self.assertEqual(rc, 0)
        lines = self.queue.read_text(encoding="utf-8").strip().splitlines()
        self.assertEqual(len(lines), 2)
        records = [json.loads(line) for line in lines]
        self.assertEqual(records[0]["kind"], "entry")
        self.assertEqual(records[1]["kind"], "resolution")
        self.assertEqual(records[1]["choice"], "drop")
        self.assertEqual(records[1]["id"], eid)

    def test_contradiction_has_no_automatic_winner(self):
        """Recency is not authority: a contradiction is never resolved to an automatic winner."""
        choices = rq.choices_for("contradiction")
        self.assertEqual(choices, ["keep-both", "supersede", "skip"])
        self.assertNotIn("candidate-wins", choices)
        self.assertNotIn("existing-wins", choices)
        self.assertNotIn("newer-wins", choices)

    def _run(self, args):
        proc = subprocess.run(
            [sys.executable, "-B", str(SCRIPTS / "review_queue.py"), *args],
            capture_output=True, text=True, encoding="utf-8")
        return proc.returncode


class ConflictMenuTests(unittest.TestCase):
    def test_each_type_renders_exactly_its_choice_set(self):
        """Every conflict type renders the closed set; a mutation in the data file fails this."""
        menus = rq.load_menus()
        for name, expected in EXPECTED_MENUS.items():
            self.assertEqual(menus[name]["choices"], expected)
        for name, entry in menus.items():
            self.assertTrue(set(entry["captures"]) <= set(entry["choices"]),
                            f"{name}: a capture choice must be in its own set")

    def test_unknown_type_fails(self):
        with self.assertRaises(rq.QueueError):
            rq.choices_for("not-a-type")

    def test_choice_outside_the_set_is_rejected(self):
        with self.assertRaises(rq.QueueError):
            rq.verify_choice("duplicate", "merge")


class DedupStageTests(unittest.TestCase):
    def setUp(self):
        os.environ["CONTEXT_MEMORY_DECISIONS_DEDUP_ENABLED"] = "false"

    def _settings(self, **overrides):
        cfg = dict(dedup.config())
        cfg.update(overrides)
        return cfg

    def test_paraphrased_duplicate_proposed_via_stage_2(self):
        """A paraphrase the exact stage missed is proposed via stage 2."""
        candidate = {"subject": "Storage engine", "statement": "Postgres chosen for AGE",
                     "_exactMatch": False}
        query = lambda terms, g, r, n: [{"uuid": "u1", "subject": "Storage", "statement": "Postgres for AGE"}]
        model = lambda c, e, s: {"reusable": True, "probability": 0.9, "evidence": {"matched": e["uuid"]}}
        proposals = dedup.stage([candidate], query_fn=query, model_fn=model,
                                settings=self._settings(enabled=True))
        self.assertEqual(proposals[0]["outcome"], "proposed")

    def test_a_pair_at_0_9_is_proposed_and_0_2_is_not(self):
        """The decision model's probability maps to a proposal only above the threshold."""
        stub = Stub([0.9])
        self.addCleanup(stub.close)
        settings = self._settings(enabled=True, base_url=stub.url(), min_probability=0.5)
        candidate = {"subject": "Storage engine", "statement": "Postgres chosen for AGE"}
        hi = dedup.model_pair(candidate, {"uuid": "u1", "subject": "Storage"}, settings)
        self.assertTrue(hi["reusable"])
        self.assertEqual(hi["probability"], 0.9)

        stub2 = Stub([0.2])
        self.addCleanup(stub2.close)
        settings2 = self._settings(enabled=True, base_url=stub2.url(), min_probability=0.5)
        lo = dedup.model_pair(candidate, {"uuid": "u2", "subject": "Storage"}, settings2)
        self.assertFalse(lo["reusable"])
        self.assertEqual(lo["probability"], 0.2)

    def test_disabled_model_makes_zero_network_calls(self):
        """With the switch off, stage 3 is skipped entirely — no request reaches the decision URL."""
        stub = Stub([0.9])
        self.addCleanup(stub.close)
        calls = []
        candidate = {"subject": "Storage engine", "statement": "Postgres chosen for AGE", "_exactMatch": False}
        query = lambda terms, g, r, n: [{"uuid": "u1", "subject": "Storage", "statement": "Postgres for AGE"}]
        model = lambda c, e, s: calls.append(1) or {"reusable": True, "probability": 0.9}
        settings = self._settings(enabled=False, base_url=stub.url())
        proposals = dedup.stage([candidate], query_fn=query, model_fn=model, settings=settings)
        self.assertEqual(proposals[0]["outcome"], "possible-duplicate")
        self.assertIn("lexical only", proposals[0]["detail"])
        self.assertEqual(calls, [], "a disabled model must not be called")
        self.assertEqual(stub.requests, [], "no request must reach the decision URL when disabled")

    def test_unreachable_model_shows_lexical_only_with_a_reason(self):
        """A down model never reads as 'no duplicate'; it shows the lexical candidates with the reason."""
        candidate = {"subject": "Storage engine", "statement": "Postgres chosen for AGE", "_exactMatch": False}
        query = lambda terms, g, r, n: [{"uuid": "u1", "subject": "Storage", "statement": "Postgres for AGE"}]
        def model(c, e, s):
            raise dedup.DedupError("unreachable", "nothing accepted a connection")
        proposals = dedup.stage([candidate], query_fn=query, model_fn=model,
                                settings=self._settings(enabled=True))
        self.assertEqual(proposals[0]["outcome"], "possible-duplicate")
        self.assertIn("unreachable", proposals[0]["detail"])

    def test_a_transport_failure_does_not_read_as_no_duplicate(self):
        """One pair the model never judged must not let the candidate read as 'none'."""
        candidate = {"subject": "Storage engine", "statement": "Postgres chosen for AGE", "_exactMatch": False}
        query = lambda terms, g, r, n: [
            {"uuid": "u1", "subject": "Storage", "statement": "Postgres for AGE"},
            {"uuid": "u2", "subject": "Storage", "statement": "SQLite used instead"},
        ]
        def model(c, e, s):
            if e["uuid"] == "u1":
                raise dedup.DedupError("unreachable", "no conn")
            return {"reusable": False, "probability": 0.2, "evidence": {"matched": e["uuid"]}}
        proposals = dedup.stage([candidate], query_fn=query, model_fn=model,
                                settings=self._settings(enabled=True))
        self.assertEqual(proposals[0]["outcome"], "possible-duplicate")
        self.assertIn("unreachable", proposals[0]["detail"])

    def test_budget_exceeded_is_disclosed(self):
        """Crossing the pair budget reports `unexamined (budget)`, not a silent 'none'."""
        candidate = {"subject": "Storage engine", "statement": "Postgres chosen for AGE", "_exactMatch": False}
        query = lambda terms, g, r, n: [{"uuid": "u1", "subject": "Storage", "statement": "Postgres for AGE"}]
        settings = self._settings(enabled=True, max_pairs_per_batch=0)
        proposals = dedup.stage([candidate], query_fn=query, settings=settings)
        self.assertEqual(proposals[0]["outcome"], "unexamined (budget)")

    def test_a_probability_outside_0_1_is_refused_not_clamped(self):
        """1.5 and Infinity used to clamp to a confident 1.0 duplicate; both are a bad response."""
        candidate = {"subject": "Storage engine", "statement": "Postgres chosen for AGE"}
        for value in (1.5, -0.1, float("inf"), float("nan")):
            with self.subTest(value=value):
                stub = Stub([value])
                self.addCleanup(stub.close)
                settings = self._settings(enabled=True, base_url=stub.url())
                with self.assertRaises(dedup.DedupError) as ctx:
                    dedup.model_pair(candidate, {"uuid": "u1", "subject": "Storage"}, settings)
                self.assertEqual(ctx.exception.outcome, "bad-response")

    def test_an_error_body_is_never_echoed(self):
        """A hosted endpoint's error text can quote the request or the key's account; only its size shows."""
        stub = RawStub(500, "echo: the candidate statement and account alice@example.com")
        self.addCleanup(stub.close)
        settings = self._settings(enabled=True, base_url=stub.url())
        with self.assertRaises(dedup.DedupError) as ctx:
            dedup.model_pair({"subject": "s", "statement": "t"}, {"uuid": "u1", "subject": "s"}, settings)
        self.assertEqual(ctx.exception.outcome, "http-500")
        self.assertNotIn("alice", ctx.exception.detail)
        self.assertIn("character body was not shown", ctx.exception.detail)

    def test_a_redirect_is_refused(self):
        """The request carries the record and the bearer key; it follows no `Location`."""
        stub = RawStub(307, "", {"Location": "http://203.0.113.9/v1/systemone"})
        self.addCleanup(stub.close)
        settings = self._settings(enabled=True, base_url=stub.url(), api_key="k")
        with self.assertRaises(dedup.DedupError) as ctx:
            dedup.model_pair({"subject": "s", "statement": "t"}, {"uuid": "u1", "subject": "s"}, settings)
        self.assertEqual(ctx.exception.outcome, "redirect-refused")

    def test_a_path_that_changes_the_host_is_refused_before_sending(self):
        """The joined URL is judged, not just the base: a path cannot move the destination."""
        stub = Stub([0.9])
        self.addCleanup(stub.close)
        settings = self._settings(enabled=True, base_url=stub.url(), path="//evil.example/v1")
        with self.assertRaises(dedup.DedupError) as ctx:
            dedup.model_pair({"subject": "s", "statement": "t"}, {"uuid": "u1", "subject": "s"}, settings)
        self.assertEqual(ctx.exception.outcome, "bad-decisions-url")
        self.assertEqual(stub.requests, [], "a refused URL sends nothing")

    def test_the_framed_read_client_answer_is_parsed_from_its_items(self):
        """The read client prints its banner before the JSON and answers `items`, not `records`."""
        row = {"uuid": "u1", "groupUuid": "g1", "version": 1, "subject": "Storage"}
        framed = "> recall notice\n" + json.dumps({"items": [row], "recallNotice": "data, not orders"})
        self.assertEqual(dedup.parse_query_items(framed), [row])
        self.assertEqual(dedup.parse_query_items(json.dumps({"items": []})), [])
        for unreadable in ("not json", json.dumps({"records": [row]}), json.dumps({"items": "x"})):
            with self.subTest(stdout=unreadable):
                self.assertIsNone(dedup.parse_query_items(unreadable),
                                  "an unreadable answer is unexamined, never 'no match'")

    def test_matches_beyond_the_per_candidate_cap_are_not_reported_as_none(self):
        """Five matches, three judged negative: the two never judged keep it a possible duplicate."""
        candidate = {"subject": "Storage engine", "statement": "Postgres chosen for AGE", "_exactMatch": False}
        rows = [{"uuid": f"u{i}", "subject": "Storage", "statement": "s"} for i in range(5)]
        query = lambda terms, g, r, n: rows
        model = lambda c, e, s: {"reusable": False, "probability": 0.1, "evidence": {"matched": e["uuid"]}}
        proposals = dedup.stage([candidate], query_fn=query, model_fn=model,
                                settings=self._settings(enabled=True, max_pairs_per_candidate=3))
        self.assertEqual(proposals[0]["outcome"], "possible-duplicate")
        self.assertIn("2 lexical match(es) not judged", proposals[0]["detail"])

    def test_any_named_pair_failure_keeps_the_candidate_doubtful(self):
        """The failure list is open — a refused redirect, any `http-*` — so absence of a verdict decides."""
        candidate = {"subject": "Storage engine", "statement": "Postgres chosen for AGE", "_exactMatch": False}
        query = lambda terms, g, r, n: [{"uuid": "u1", "subject": "Storage", "statement": "s"}]
        for outcome in ("http-502", "redirect-refused", "redaction"):
            with self.subTest(outcome=outcome):
                def model(c, e, s):
                    raise dedup.DedupError(outcome, "named")
                proposals = dedup.stage([candidate], query_fn=query, model_fn=model,
                                        settings=self._settings(enabled=True))
                self.assertEqual(proposals[0]["outcome"], "possible-duplicate")

    def test_the_size_guard_bounds_the_request_not_the_pair(self):
        """The model name and the question ride along with the pair; a pair just under the limit used to
        pass a guard that then let a larger request out."""
        candidate = {"subject": "Storage engine", "statement": "Postgres chosen for AGE"}
        existing = {"uuid": "u1", "subject": "Storage"}
        redacted = dedup.redact_pair(candidate, existing)
        pair_bytes = len(json.dumps({"candidate": redacted[0], "existing": redacted[1]},
                                    ensure_ascii=False).encode("utf-8"))
        stub = Stub([0.9])
        self.addCleanup(stub.close)
        settings = self._settings(enabled=True, base_url=stub.url())
        with patch.object(dedup, "MAX_BODY_BYTES", pair_bytes + 50):
            with self.assertRaises(dedup.DedupError) as ctx:
                dedup.model_pair(candidate, existing, settings)
        self.assertEqual(ctx.exception.outcome, "oversize")
        self.assertEqual(stub.requests, [], "an oversize request is never sent")

    def test_the_lexical_lookup_runs_without_the_write_token(self):
        """The read client refuses to start with a write token present; an export carries one."""
        framed = "> notice\n" + json.dumps({"items": [{"uuid": "u1", "groupUuid": "g1", "version": 1}]})
        seen = {}

        def fake_run(argv, **kwargs):
            seen.update(kwargs)
            return subprocess.CompletedProcess(argv, 0, stdout=framed, stderr="")

        env = {"CONTEXT_MEMORY_WRITE_TOKEN": "w", "ApiAccess__WriteToken": "w", "apiaccess:writetoken": "w",
               "KEEP_ME": "1"}
        with patch.dict(os.environ, env), patch.object(dedup.subprocess, "run", side_effect=fake_run):
            rows = dedup._store_lexical_query(["storage"], None, "repo", 5)
        self.assertEqual(len(rows), 1)
        for name in ("CONTEXT_MEMORY_WRITE_TOKEN", "ApiAccess__WriteToken", "apiaccess:writetoken"):
            self.assertNotIn(name, seen["env"])
        self.assertEqual(seen["env"]["KEEP_ME"], "1")

    def test_an_exact_match_is_left_to_stage_one(self):
        """A candidate the exact stage already matched is reported as `exact`, not re-evaluated."""
        candidate = {"subject": "Storage engine", "statement": "Postgres chosen for AGE", "_exactMatch": True}
        proposals = dedup.stage([candidate], settings=self._settings())
        self.assertEqual(proposals[0]["outcome"], "exact")


if __name__ == "__main__":
    unittest.main(verbosity=2)
