#!/usr/bin/env python3
"""L0 harness for the review queue, the conflict choice menus and staged dedup.

No decision model, no network beyond a loopback stub for the dedup model. The queue's own redaction and
the injected stage-2/3 seams make every acceptance criterion here deterministic — the properties that
matter (a held candidate persists instead of vanishing, a secret is scrubbed before the queue write, a
closed menu never accepts free text, a down model never reads as "no duplicate") are exactly the ones a
live model cannot pin.
"""

import http.server
import json
import os
import socket
import stat
import subprocess
import sys
import tempfile
import threading
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1] / "scripts"

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

    def test_an_exact_match_is_left_to_stage_one(self):
        """A candidate the exact stage already matched is reported as `exact`, not re-evaluated."""
        candidate = {"subject": "Storage engine", "statement": "Postgres chosen for AGE", "_exactMatch": True}
        proposals = dedup.stage([candidate], settings=self._settings())
        self.assertEqual(proposals[0]["outcome"], "exact")


if __name__ == "__main__":
    unittest.main(verbosity=2)
